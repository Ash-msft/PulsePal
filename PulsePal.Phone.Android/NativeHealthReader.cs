using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Health.Connect;
using global::Android.Health.Connect.DataTypes;
using global::Android.OS;
using global::Android.Runtime;
using Java.Time;
using PulsePal.Connected;

namespace PulsePal.Phone.Android;

public sealed class NativeHealthReader
{
    private readonly Context context;

    public NativeHealthReader(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
    }

    public static string PermissionFor(HealthMetric metric) => metric switch
    {
        HealthMetric.HeartRate => "android.permission.health.READ_HEART_RATE",
        HealthMetric.BloodPressure => "android.permission.health.READ_BLOOD_PRESSURE",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    public bool IsSupported =>
        OperatingSystem.IsAndroidVersionAtLeast(34) && Api34.HasService(context);

    public Task<SyncRecordsRequest> ReadAsync(
        bool heartRate, bool bloodPressure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selected = new List<HealthMetric>(2);
        if (heartRate) selected.Add(HealthMetric.HeartRate);
        if (bloodPressure) selected.Add(HealthMetric.BloodPressure);
        if (selected.Count == 0)
            return Task.FromResult(new SyncRecordsRequest([], []));
        if (!OperatingSystem.IsAndroidVersionAtLeast(34))
            return Task.FromResult(Unsupported(selected));

        return Api34.ReadAsync(context, selected, DateTimeOffset.UtcNow, ct);
    }

    private static SyncRecordsRequest Unsupported(IEnumerable<HealthMetric> selected) =>
        new([], selected.Select(metric =>
            new CategoryReadStatus(metric, ReadAvailability.Unsupported)).ToArray());

    // Keep API 34 types out of the facade's fields/signatures and prevent guarded calls
    // from being inlined into code that also runs on Android 28-33.
    [SupportedOSPlatform("android34.0")]
    private static class Api34
    {
        private const int PageSize = 64;
        private const int MaxPages = 4;
        private const int MaxPayload = 256;
        private const int CategoryLimit = 128;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool HasService(Context context) =>
            context.GetSystemService("healthconnect") is HealthConnectManager;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static async Task<SyncRecordsRequest> ReadAsync(
            Context context, IReadOnlyList<HealthMetric> selected,
            DateTimeOffset windowEnd, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.GetSystemService("healthconnect") is not HealthConnectManager manager)
                return Unsupported(selected);

            var records = new List<HealthRecord>(MaxPayload);
            var statuses = new List<CategoryReadStatus>(selected.Count);
            try
            {
                foreach (var metric in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    if (context.CheckSelfPermission(PermissionFor(metric)) != Permission.Granted)
                    {
                        statuses.Add(new(metric, ReadAvailability.Denied));
                        continue;
                    }

                    // Stage each category so revocation on a later page never leaks partial data.
                    var categoryRecords = new List<HealthRecord>(CategoryLimit);
                    ReadAvailability availability;
                    try
                    {
                        await ReadCategoryAsync(context, manager, metric, windowEnd,
                            CategoryLimit, categoryRecords, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (context.CheckSelfPermission(PermissionFor(metric)) != Permission.Granted)
                        {
                            availability = ReadAvailability.Denied;
                        }
                        else
                        {
                            availability = categoryRecords.Count == 0
                                ? ReadAvailability.NoData : ReadAvailability.Available;
                            records.AddRange(categoryRecords);
                        }
                    }
                    catch (Java.Lang.SecurityException)
                    {
                        availability = ReadAvailability.Denied;
                    }
                    catch (HealthConnectException ex)
                        when (ex.ErrorCode == HealthConnectExceptionErrorReason.Security)
                    {
                        availability = ReadAvailability.Denied;
                    }
                    catch (Java.Lang.UnsupportedOperationException)
                    {
                        availability = ReadAvailability.Unsupported;
                    }
                    catch (HealthConnectException ex)
                        when (ex.ErrorCode == HealthConnectExceptionErrorReason.UnsupportedOperation)
                    {
                        availability = ReadAvailability.Unsupported;
                    }
                    finally
                    {
                        categoryRecords.Clear();
                    }

                    statuses.Add(new(metric, availability));
                }

                // Reading the second category may outlive the first category's permission.
                for (int index = 0; index < statuses.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var metric = statuses[index].Metric;
                    if (context.CheckSelfPermission(PermissionFor(metric)) != Permission.Granted)
                    {
                        records.RemoveAll(record => record.Metric == metric);
                        statuses[index] = new(metric, ReadAvailability.Denied);
                    }
                }

                ct.ThrowIfCancellationRequested();
                return new(records.OrderByDescending(record => record.MeasuredAt).ToArray(),
                    statuses.ToArray());
            }
            finally
            {
                // Arrays returned above own their references; clear only managed staging.
                records.Clear();
                statuses.Clear();
            }
        }

        private static async Task ReadCategoryAsync(
            Context context, HealthConnectManager manager, HealthMetric metric,
            DateTimeOffset windowEnd, int limit, List<HealthRecord> records,
            CancellationToken ct)
        {
            var windowStart = metric == HealthMetric.HeartRate
                ? windowEnd.AddHours(-24) : windowEnd.AddDays(-7);
            using var start = ToInstant(windowStart);
            using var end = ToInstant(windowEnd);
            // Health Connect filters interval records by their start, not sample time.
            // Scan bounded parent history to include sessions crossing the 24-hour edge;
            // only individual samples inside the requested 24 hours leave this adapter.
            using var queryStart = ToInstant(metric == HealthMetric.HeartRate
                ? windowEnd.AddDays(-30) : windowStart);
            using var timeBuilder = new TimeInstantRangeFilter.Builder();
            timeBuilder.SetStartTime(queryStart);
            timeBuilder.SetEndTime(end);
            using var timeFilter = timeBuilder.Build();
            using var recordType = Java.Lang.Class.FromType(metric == HealthMetric.HeartRate
                ? typeof(HeartRateRecord) : typeof(BloodPressureRecord));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            long pageToken = -1;

            for (int page = 0; page < MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                using var builder = new ReadRecordsRequestUsingFilters.Builder(recordType);
                builder.SetTimeRangeFilter(timeFilter);
                builder.SetPageSize(PageSize);
                // A continuation token encodes its sort direction; Android forbids also
                // setting an explicit direction on continuation requests.
                if (page == 0) builder.SetAscending(false);
                else builder.SetPageToken(pageToken);
                using var request = builder.Build();
                using var response = await ReadPageAsync(context, manager, request, ct)
                    .ConfigureAwait(false);

                foreach (Java.Lang.Object item in response.Records)
                {
                    ct.ThrowIfCancellationRequested();
                    if (metric == HealthMetric.HeartRate)
                    {
                        using var record = item.JavaCast<HeartRateRecord>();
                        AddHeartRate(record, start, end, limit, records, ids, ct);
                    }
                    else
                    {
                        using var record = item.JavaCast<BloodPressureRecord>();
                        AddBloodPressure(record, start, end, records, ids);
                    }
                    if (metric == HealthMetric.BloodPressure && records.Count >= limit) break;
                }

                pageToken = response.NextPageToken;
                if (pageToken == -1
                    || (metric == HealthMetric.BloodPressure && records.Count >= limit)) break;
            }
        }

        private static void AddHeartRate(
            HeartRateRecord record, Instant start, Instant end, int limit,
            List<HealthRecord> records, HashSet<string> ids, CancellationToken ct)
        {
            var metadata = record.Metadata;
            string parentId = metadata.Id;
            if (!IsValidId(parentId)) return;
            foreach (var sample in record.Samples
                .OrderByDescending(sample => sample.Time.EpochSecond)
                .ThenByDescending(sample => sample.Time.Nano))
            {
                ct.ThrowIfCancellationRequested();
                using var time = sample.Time;
                double bpm = sample.BeatsPerMinute;
                if (!IsValidValue(bpm) || !InWindow(time, start, end)) continue;
                // Preserve all nine fractional digits in identity, even though the
                // shared DateTimeOffset measurement has only 100 ns precision.
                string id = string.Create(CultureInfo.InvariantCulture,
                    $"healthconnect:hr:{parentId}:{time.EpochSecond}:{time.Nano:D9}");
                if (ids.Contains(id)) continue;
                var measurement = CreateRecord(metadata, id, HealthMetric.HeartRate, time, bpm, null);
                if (measurement is null) continue;
                if (records.Count == limit)
                {
                    // Parent records are ordered by start, not by their sample times.
                    // Keep the newest samples across the entire bounded page scan.
                    var oldest = records.MinBy(record => record.MeasuredAt)!;
                    if (measurement.MeasuredAt <= oldest.MeasuredAt) break;
                    records.Remove(oldest);
                    ids.Remove(oldest.RecordId);
                }
                ids.Add(id);
                records.Add(measurement);
            }
        }

        private static void AddBloodPressure(
            BloodPressureRecord record, Instant start, Instant end,
            List<HealthRecord> records, HashSet<string> ids)
        {
            using var time = record.Time;
            double systolic = record.Systolic.InMillimetersOfMercury;
            double diastolic = record.Diastolic.InMillimetersOfMercury;
            if (!IsValidValue(systolic) || !IsValidValue(diastolic)
                || !InWindow(time, start, end)) return;
            var metadata = record.Metadata;
            string parentId = metadata.Id;
            if (!IsValidId(parentId)) return;
            string id = $"healthconnect:bp:{parentId}";
            var measurement = CreateRecord(metadata, id, HealthMetric.BloodPressure,
                time, systolic, diastolic);
            if (measurement is null) return;
            if (!ids.Add(id)) return;
            records.Add(measurement);
        }

        private static HealthRecord? CreateRecord(
            Metadata metadata, string id, HealthMetric metric, Instant time,
            double value, double? secondaryValue)
        {
            string sourceApp = metadata.DataOrigin.PackageName;
            if (!IsValidId(id) || !HealthMetadata.IsValid(sourceApp, 512)) return null;
            var device = metadata.Device;
            string? sourceDevice = device is null ? null : string.Join(" ",
                new[] { device.Manufacturer, device.Model }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (string.IsNullOrWhiteSpace(sourceDevice)) sourceDevice = null;
            if (sourceDevice is not null && !HealthMetadata.IsValid(sourceDevice, 256)) return null;
            using var modified = metadata.LastModifiedTime;
            if (!TryToDateTimeOffset(modified, out var lastModified)
                || !TryToDateTimeOffset(time, out var measuredAt)) return null;
            return new(id, lastModified.UtcTicks, metric, measuredAt, null,
                value, secondaryValue, sourceApp, sourceDevice);
        }

        private static bool IsValidId(string? id) =>
            !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
            id.All(character => character is >= ' ' and <= '~');

        private static bool IsValidValue(double value) =>
            double.IsFinite(value) && value is > 0 and <= 1000;

        private static bool InWindow(Instant time, Instant start, Instant end) =>
            time.CompareTo(start) >= 0 && time.CompareTo(end) < 0;

        private static Instant ToInstant(DateTimeOffset time) =>
            Instant.OfEpochSecond(time.ToUnixTimeSeconds(),
                time.UtcTicks % TimeSpan.TicksPerSecond * 100)
            ?? throw new InvalidOperationException("Could not construct a native time filter.");

        private static bool TryToDateTimeOffset(Instant time, out DateTimeOffset value)
        {
            value = default;
            long seconds = time.EpochSecond;
            if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds()
                || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return false;
            value = new(DateTimeOffset.UnixEpoch.Ticks
                + seconds * TimeSpan.TicksPerSecond + time.Nano / 100, TimeSpan.Zero);
            return true;
        }

        private static async Task<ReadRecordsResponse> ReadPageAsync(
            Context context, HealthConnectManager manager,
            ReadRecordsRequestUsingFilters request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<ReadRecordsResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var receiver = new RecordsReceiver(completion);
            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            try
            {
                ct.ThrowIfCancellationRequested();
                manager.ReadRecords(request, context.MainExecutor
                    ?? throw new InvalidOperationException("Android returned no main executor."),
                    receiver);
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                // Native ReadRecords has no cancellation signal. Do not dispose its
                // receiver on cancellation: a late callback must remain safe.
                GC.KeepAlive(receiver);
            }
        }

        private sealed class RecordsReceiver(
            TaskCompletionSource<ReadRecordsResponse> completion)
            : Java.Lang.Object, IOutcomeReceiver
        {
            public void OnResult(Java.Lang.Object? result)
            {
                if (result is null)
                {
                    completion.TrySetException(
                        new InvalidOperationException("Health Connect returned no response."));
                    return;
                }
                ReadRecordsResponse response;
                try
                {
                    response = result.JavaCast<ReadRecordsResponse>();
                }
                catch (Exception ex) when (ex is InvalidCastException or Java.Lang.ClassCastException)
                {
                    completion.TrySetException(ex);
                    return;
                }
                if (!completion.TrySetResult(response)) response.Dispose();
            }

            public void OnError(Java.Lang.Object? error)
            {
                if (error is null)
                {
                    completion.TrySetException(
                        new InvalidOperationException("Health Connect returned no error detail."));
                    return;
                }
                HealthConnectException exception;
                try
                {
                    exception = error.JavaCast<HealthConnectException>();
                }
                catch (Exception ex) when (ex is InvalidCastException or Java.Lang.ClassCastException)
                {
                    completion.TrySetException(ex);
                    return;
                }
                completion.TrySetException(exception);
            }
        }
    }
}
