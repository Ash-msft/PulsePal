#if DEBUG
using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.OS;
using global::Android.Runtime;
using PulsePal.Connected;

namespace PulsePal.Phone.Android;

[Instrumentation(Name = "org.pulsepal.phone.NativeReadInstrumentation",
    TargetPackage = "org.pulsepal.phone")]
public sealed class NativeReadInstrumentation : Instrumentation
{
    private string expected = "Denied";
    private bool requestPermissions;

    public NativeReadInstrumentation() { }

    public NativeReadInstrumentation(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership) { }

    public override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        expected = arguments?.GetString("expected") ?? "Denied";
        requestPermissions = arguments?.GetString("requestPermissions") == "true";
        Start();
    }

    public override void OnStart()
    {
        base.OnStart();
        var result = new Bundle();
        var outcome = Result.Canceled;
        Activity? activity = null;
        try
        {
            if (!Enum.TryParse<ReadAvailability>(expected, out var availability))
                throw new InvalidOperationException("Unknown expected availability.");
            var context = TargetContext ?? throw new InvalidOperationException("Missing test context.");
            var intent = new Intent(context, typeof(PermissionsRationaleActivity));
            intent.AddFlags(ActivityFlags.NewTask);
            activity = StartActivitySync(intent);
            WaitForIdleSync();
            if (requestPermissions)
            {
                var permissions = new[]
                {
                    NativeHealthReader.PermissionFor(HealthMetric.HeartRate),
                    NativeHealthReader.PermissionFor(HealthMetric.BloodPressure)
                };
                RunOnMainSync(() => activity!.RequestPermissions(permissions, 42));
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (permissions.Any(permission => context.CheckSelfPermission(permission) != Permission.Granted)
                       && DateTime.UtcNow < deadline)
                    Thread.Sleep(200);
                WaitForIdleSync();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var reader = new NativeHealthReader(context);
            var empty = reader.ReadAsync(false, false, timeout.Token).GetAwaiter().GetResult();
            if (empty.Records.Length != 0 || empty.Categories.Length != 0)
                throw new InvalidOperationException("Unselected categories were read.");
            var snapshot = reader.ReadAsync(true, true, timeout.Token).GetAwaiter().GetResult();
            VerifySnapshot(snapshot, availability, HealthMetric.HeartRate, HealthMetric.BloodPressure);
            var heartRate = reader.ReadAsync(true, false, timeout.Token).GetAwaiter().GetResult();
            VerifySnapshot(heartRate, availability, HealthMetric.HeartRate);
            var bloodPressure = reader.ReadAsync(false, true, timeout.Token).GetAwaiter().GetResult();
            VerifySnapshot(bloodPressure, availability, HealthMetric.BloodPressure);
            var repeated = new SyncRecordsRequest(
                heartRate.Records.Concat(bloodPressure.Records).ToArray(),
                heartRate.Categories.Concat(bloodPressure.Categories).ToArray());
            VerifySnapshot(repeated, availability, HealthMetric.HeartRate, HealthMetric.BloodPressure);
            var heartStability = VerifyRepeatedRead(snapshot, repeated, HealthMetric.HeartRate);
            var pressureStability = VerifyRepeatedRead(snapshot, repeated, HealthMetric.BloodPressure);
            result.PutString("stream",
                $"PASS: real-store selected/unselected and independent reads; both categories {availability}; " +
                $"each <=128; unchanged overlapping records checked: HR {heartStability.Stable}, BP {pressureStability.Stable}; " +
                $"changed revisions allowed: HR {heartStability.Revised}, BP {pressureStability.Revised}. " +
                "No seeds, source-equivalence, or wearable coverage; zero overlap cannot establish stability.\n");
            outcome = Result.Ok;
        }
        catch (Exception ex)
        {
            result.PutString("stream", $"FAIL: native read verification ({ex.GetType().Name}).\n");
        }
        finally
        {
            if (activity is not null) RunOnMainSync(activity.Finish);
        }
        Finish(outcome, result);
    }

    private static void VerifySnapshot(
        SyncRecordsRequest snapshot, ReadAvailability availability, params HealthMetric[] selected)
    {
        if (snapshot.Categories.Length != selected.Length
            || selected.Any(metric => snapshot.Categories.Count(category =>
                category.Metric == metric && category.Availability == availability) != 1)
            || snapshot.Records.Length > selected.Length * 128)
            throw new InvalidOperationException("Native availability or payload bound mismatch.");
        if (availability != ReadAvailability.Available && snapshot.Records.Length != 0)
            throw new InvalidOperationException("Unavailable category produced measurements.");
        foreach (var metric in selected)
        {
            int count = snapshot.Records.Count(record => record.Metric == metric);
            if (count > 128 || (availability == ReadAvailability.Available && count == 0))
                throw new InvalidOperationException("Available requires actual records in each selected category, bounded at 128.");
        }

        var ids = new HashSet<(HealthMetric, string, string)>();
        var now = DateTimeOffset.UtcNow;
        foreach (var record in snapshot.Records)
        {
            if (!selected.Contains(record.Metric)
                || !ids.Add((record.Metric, record.SourceApp, record.RecordId))
                || string.IsNullOrWhiteSpace(record.RecordId) || record.RecordId.Length > 128
                || record.RecordId.Any(character => character is < ' ' or > '~')
                || !record.RecordId.StartsWith(record.Metric == HealthMetric.HeartRate
                    ? "healthconnect:hr:" : "healthconnect:bp:", StringComparison.Ordinal)
                || !HealthMetadata.IsValid(record.SourceApp, 512)
                || (record.SourceDevice is not null && !HealthMetadata.IsValid(record.SourceDevice, 256))
                || record.Version < 0 || record.EndAt is not null
                || record.MeasuredAt.Offset != TimeSpan.Zero
                || record.MeasuredAt < now.AddDays(-30) || record.MeasuredAt > now.AddMinutes(2)
                || !double.IsFinite(record.Value) || record.Value is <= 0 or > 1000
                || (record.Metric == HealthMetric.HeartRate
                    ? record.SecondaryValue is not null
                    : record.SecondaryValue is not (> 0 and <= 1000)
                        || !double.IsFinite(record.SecondaryValue.Value)))
                throw new InvalidOperationException("Native record violates the bridge measurement contract.");
        }
    }

    private static (int Stable, int Revised) VerifyRepeatedRead(
        SyncRecordsRequest first, SyncRecordsRequest repeated, HealthMetric metric)
    {
        var previous = first.Records.Where(record => record.Metric == metric)
            .ToDictionary(record => (record.SourceApp, record.RecordId));
        int stable = 0;
        int revised = 0;
        foreach (var record in repeated.Records.Where(record => record.Metric == metric))
        {
            // The real store may add, remove, revise, or age records out between reads.
            if (!previous.TryGetValue((record.SourceApp, record.RecordId), out var prior)) continue;
            if (metric == HealthMetric.HeartRate && record.MeasuredAt != prior.MeasuredAt)
                throw new InvalidOperationException("A heart-rate sample ID changed its measurement time.");
            if (record.Version != prior.Version)
            {
                revised++;
                continue;
            }
            if (record != prior)
                throw new InvalidOperationException("An unchanged native revision changed its measurement or metadata.");
            stable++;
        }
        return (stable, revised);
    }
}
#endif
