#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using HealthKit;
using PulsePal.Connected;

namespace PulsePal.Phone.iOS;

/// <summary>Bounded, read-only HealthKit access; the caller owns foreground orchestration.</summary>
public sealed class HealthKitReader : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<Operation> pending = new();
    private HKHealthStore? store;
    private bool disposed;
    private int authorizationCallbacks;

    public bool IsSupported => HKHealthStore.IsHealthDataAvailable;

    /// <summary>
    /// Requests only read access. Completion means the authorization request completed,
    /// not that read access was granted. An unsupported device requires no authorization.
    /// </summary>
    public async Task AuthorizeAsync(bool heartRate, bool bloodPressure, CancellationToken ct = default)
    {
        try
        {
            CheckState(ct);
            if ((!heartRate && !bloodPressure) || !IsSupported)
                return;

            var hr = heartRate ? QuantityType(HKQuantityTypeIdentifier.HeartRate) : null;
            var systolic = bloodPressure ? QuantityType(HKQuantityTypeIdentifier.BloodPressureSystolic) : null;
            var diastolic = bloodPressure ? QuantityType(HKQuantityTypeIdentifier.BloodPressureDiastolic) : null;
            var types = new List<NSObject>();
            if (hr is not null) types.Add(hr);
            if (systolic is not null) types.Add(systolic);
            if (diastolic is not null) types.Add(diastolic);
            // Correlations are queried, but authorization is requested for their constituent types.
            using var readTypes = new NSSet(types.ToArray());
            using var writeTypes = new NSSet();
            Operation operation;
            lock (gate)
            {
                CheckState(ct);
                // Cancelling the managed wait cannot dismiss Apple's existing sheet.
                if (authorizationCallbacks != 0)
                    throw new HealthKitReadException();
                store ??= new HKHealthStore();
                operation = Register(ct);
                if (pending.Contains(operation))
                {
                    authorizationCallbacks++;
                    try
                    {
                        store.RequestAuthorizationToShare(writeTypes, readTypes, (success, error) =>
                        {
                            lock (gate)
                            {
                                try
                                {
                                    if (pending.Contains(operation))
                                        Complete(operation, Array.Empty<HealthRecord>(), !success || error is not null);
                                }
                                finally
                                {
                                    authorizationCallbacks--;
                                    DisposeStoreIfReady();
                                }
                            }
                        });
                    }
                    catch (Exception error) when (ShouldSanitize(error))
                    {
                        authorizationCallbacks--;
                        Complete(operation, Array.Empty<HealthRecord>(), failed: true);
                    }
                }
            }
            await operation.Completion.Task.ConfigureAwait(false);
        }
        catch (Exception error) when (ShouldSanitize(error))
        {
            throw new HealthKitReadException();
        }
    }

    public async Task<SyncRecordsRequest> ReadAsync(
        bool heartRate, bool bloodPressure, CancellationToken ct = default)
    {
        var records = new List<HealthRecord>();
        try
        {
            CheckState(ct);
            var categories = new List<CategoryReadStatus>();
            if (!IsSupported)
            {
                if (heartRate) categories.Add(new(HealthMetric.HeartRate, ReadAvailability.Unsupported));
                if (bloodPressure) categories.Add(new(HealthMetric.BloodPressure, ReadAvailability.Unsupported));
                return new(records.ToArray(), categories.ToArray());
            }

            var now = DateTimeOffset.UtcNow;
            if (heartRate)
            {
                var type = QuantityType(HKQuantityTypeIdentifier.HeartRate);
                var unit = HKUnit.Count.UnitDividedBy(HKUnit.Minute);
                var result = await QueryAsync(type, now.AddHours(-24), now, samples =>
                {
                    var converted = new List<HealthRecord>();
                    foreach (var sample in samples)
                        if (sample is HKQuantitySample quantity && TryValue(quantity, unit, out var value))
                        {
                            var record = Record(sample, HealthMetric.HeartRate, value, null);
                            if (record is not null) converted.Add(record);
                        }
                    return converted.ToArray();
                }, ct).ConfigureAwait(false);
                AddCategory(HealthMetric.HeartRate, result, records, categories);
            }

            if (bloodPressure)
            {
                var type = HKCorrelationType.Create(HKCorrelationTypeIdentifier.BloodPressure)
                    ?? throw new HealthKitReadException();
                var systolic = QuantityType(HKQuantityTypeIdentifier.BloodPressureSystolic);
                var diastolic = QuantityType(HKQuantityTypeIdentifier.BloodPressureDiastolic);
                var unit = HKUnit.MillimeterOfMercury;
                var result = await QueryAsync(type, now.AddDays(-7), now, samples =>
                {
                    var converted = new List<HealthRecord>();
                    foreach (var sample in samples)
                    {
                        if (sample is not HKCorrelation correlation)
                            continue;
                        using var systolicObjects = correlation.GetObjects(systolic);
                        using var diastolicObjects = correlation.GetObjects(diastolic);
                        var sys = systolicObjects.ToArray<HKSample>();
                        var dia = diastolicObjects.ToArray<HKSample>();
                        // Never join independent samples by timestamp or choose an ambiguous component.
                        if (sys.Length != 1 || dia.Length != 1 ||
                            sys[0] is not HKQuantitySample sysSample || dia[0] is not HKQuantitySample diaSample ||
                            !TryValue(sysSample, unit, out var sysValue) ||
                            !TryValue(diaSample, unit, out var diaValue))
                            continue;
                        var record = Record(correlation, HealthMetric.BloodPressure, sysValue, diaValue);
                        if (record is not null) converted.Add(record);
                    }
                    return converted.ToArray();
                }, ct).ConfigureAwait(false);
                AddCategory(HealthMetric.BloodPressure, result, records, categories);
            }

            CheckState(ct);
            return new(records.ToArray(), categories.ToArray());
        }
        catch (Exception error) when (ShouldSanitize(error))
        {
            throw new HealthKitReadException();
        }
        finally
        {
            records.Clear();
        }
    }

    private Task<HealthRecord[]> QueryAsync(HKSampleType type, DateTimeOffset from, DateTimeOffset until,
        Func<HKSample[], HealthRecord[]> convert, CancellationToken ct)
    {
        lock (gate)
        {
            CheckState(ct);
            store ??= new HKHealthStore();
            var operation = Register(ct);
            if (!pending.Contains(operation))
                return operation.Completion.Task;
            try
            {
                using var start = NSDate.FromTimeIntervalSince1970((from - DateTimeOffset.UnixEpoch).TotalSeconds);
                using var end = NSDate.FromTimeIntervalSince1970((until - DateTimeOffset.UnixEpoch).TotalSeconds);
                using var predicate = HKQuery.GetPredicateForSamples(start, end,
                    HKQueryOptions.StrictStartDate | HKQueryOptions.StrictEndDate);
                using var descending = new NSSortDescriptor(HKSample.SortIdentifierStartDate, false);
                operation.Query = new HKSampleQuery(type, predicate, (nuint)128, new[] { descending },
                    (_, samples, error) =>
                    {
                        lock (gate)
                        {
                            // Cancellation/disposal removes the operation before releasing native objects.
                            // A late callback must not inspect samples, captured types, units or the store.
                            if (!pending.Contains(operation))
                                return;
                            try
                            {
                                if (error is not null)
                                    Complete(operation, Array.Empty<HealthRecord>(), failed: true);
                                else
                                    Complete(operation, convert(samples ?? Array.Empty<HKSample>()), failed: false);
                            }
                            catch (Exception failure) when (ShouldSanitize(failure))
                            {
                                Complete(operation, Array.Empty<HealthRecord>(), failed: true);
                            }
                        }
                    });
                operation.Started = true;
                store.ExecuteQuery(operation.Query);
            }
            catch (Exception error) when (ShouldSanitize(error))
            {
                Cancel(operation, failed: true);
            }
            return operation.Completion.Task;
        }
    }

    // All operation transitions, native submission and native stopping are serialized by gate.
    private Operation Register(CancellationToken ct)
    {
        var operation = new Operation(ct);
        pending.Add(operation);
        operation.Registration = ct.Register(() =>
        {
            lock (gate)
                Cancel(operation, failed: false);
        });
        // Register can invoke the callback synchronously for an already-cancelled token.
        if (!pending.Contains(operation))
            operation.Registration.Unregister();
        return operation;
    }

    private void Complete(Operation operation, HealthRecord[] records, bool failed)
    {
        if (!pending.Remove(operation))
        {
            Array.Clear(records);
            return;
        }
        try
        {
            Release(operation);
        }
        catch (Exception error) when (ShouldSanitize(error))
        {
            failed = true;
        }
        if (failed)
        {
            Array.Clear(records);
            operation.Completion.TrySetException(new HealthKitReadException());
        }
        else operation.Completion.TrySetResult(records);
    }

    private void Cancel(Operation operation, bool failed)
    {
        if (!pending.Remove(operation))
            return;
        try
        {
            if (operation.Started && operation.Query is not null)
                store!.StopQuery(operation.Query);
        }
        catch (Exception error) when (ShouldSanitize(error))
        {
            failed = true;
        }
        finally
        {
            try
            {
                Release(operation);
            }
            catch (Exception error) when (ShouldSanitize(error))
            {
                failed = true;
            }
        }
        if (failed) operation.Completion.TrySetException(new HealthKitReadException());
        else operation.Completion.TrySetCanceled(operation.Token.IsCancellationRequested
            ? operation.Token : new CancellationToken(canceled: true));
    }

    private static void Release(Operation operation)
    {
        // Dispose() on the registration could deadlock against a callback waiting for gate.
        operation.Registration.Unregister();
        operation.Query?.Dispose();
        operation.Query = null;
    }

    // Type/unit factories may return shared native objects; do not explicitly dispose their wrappers.
    private static HKQuantityType QuantityType(HKQuantityTypeIdentifier identifier) =>
        HKQuantityType.Create(identifier) ?? throw new HealthKitReadException();

    private static bool TryValue(HKQuantitySample sample, HKUnit unit, out double value)
    {
        value = 0;
        if (!sample.Quantity.IsCompatible(unit))
            return false;
        value = sample.Quantity.GetDoubleValue(unit);
        // This is the shared bridge's wire limit, not a clinical plausibility rule.
        return double.IsFinite(value) && value is > 0 and <= 1000;
    }

    private static HealthRecord? Record(HKSample sample, HealthMetric metric, double value, double? secondary)
    {
        var startSeconds = sample.StartDate.SecondsSince1970;
        var endSeconds = sample.EndDate.SecondsSince1970;
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || endSeconds < startSeconds)
            return null;
        DateTimeOffset start;
        DateTimeOffset end;
        try
        {
            start = DateTimeOffset.UnixEpoch.AddSeconds(startSeconds);
            end = DateTimeOffset.UnixEpoch.AddSeconds(endSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        var revision = sample.SourceRevision;
        var source = revision.Source;
        var sourceApp = $"{source.Name} (bundle: {source.BundleIdentifier}; version: {revision.Version})";
        var sourceDevice = sample.Device?.Name;
        if (string.IsNullOrWhiteSpace(sourceDevice))
            sourceDevice = null;
        if (!ValidMetadata(sourceApp, 512) ||
            (sourceDevice is not null && !ValidMetadata(sourceDevice, 256)))
            return null;
        // HealthKit samples are immutable: replacement samples have new UUIDs.
        // Version 1 is stable across rereads, unlike a query-time or synchronization timestamp.
        return new(sample.Uuid.AsString(), 1L, metric, start, end, value, secondary,
            sourceApp, sourceDevice);
    }

    private static bool ValidMetadata(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.EnumerateRunes().All(r => Rune.GetUnicodeCategory(r) is not
            (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
             or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));

    private static void AddCategory(HealthMetric metric, HealthRecord[] result,
        List<HealthRecord> records, List<CategoryReadStatus> categories)
    {
        records.AddRange(result);
        // HealthKit intentionally conceals read denial; empty results cannot establish permission.
        categories.Add(new(metric, result.Length == 0
            ? ReadAvailability.PermissionNotVerifiable : ReadAvailability.Available));
        Array.Clear(result);
    }

    private void CheckState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
            ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static bool ShouldSanitize(Exception error) =>
        error is not ObjectDisposedException &&
        error is ArgumentException or InvalidOperationException or NotSupportedException or
            OverflowException or NSErrorException or ObjCRuntime.ObjCException;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var operation in pending.ToArray())
                Cancel(operation, failed: false);
            DisposeStoreIfReady();
        }
    }

    private void DisposeStoreIfReady()
    {
        // Apple exposes no cancellation API for the authorization sheet. Cancel the managed
        // wait, ignore late authorization results, and keep its store alive until its callback.
        if (disposed && authorizationCallbacks == 0)
        {
            store?.Dispose();
            store = null;
        }
    }

    private sealed class Operation(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<HealthRecord[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public HKSampleQuery? Query;
        public bool Started;
    }
}

/// <summary>A UI-safe error without native descriptions, health data or native inner exceptions.</summary>
public sealed class HealthKitReadException : Exception
{
    public HealthKitReadException() : base("HealthKit could not complete the request. Please try again.") { }
}
