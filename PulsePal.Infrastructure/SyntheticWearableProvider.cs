using PulsePal.Core;

namespace PulsePal.Infrastructure;

public sealed class SyntheticWearableProvider : IWearableProvider
{
    private readonly object _gate = new();
    private readonly double _phase;
    private DemoScenario _scenario;
    private DateTimeOffset? _lastSampleAt;
    private DateTimeOffset? _scenarioStartedAt;
    private DateTimeOffset? _breathingUntil;
    private DateOnly? _day;
    private bool _poorSleep;
    private bool _wasWalking;
    private long _walkingTicks;
    private double _calories;
    private double _stress = 30, _heartRate = 73, _hrv = 61.5, _recovery = 72.1, _respiration = 14.1;
    private double _breathingStress, _breathingHeartRate;
    private WorkContext _lastContext = new();

    public SyntheticWearableProvider() : this(Random.Shared.Next()) { }

    public SyntheticWearableProvider(int seed)
    {
        _phase = new Random(seed).NextDouble() * Math.Tau;
    }

    public string Name => "Synthetic wearable (demo)";

    public DemoScenario Scenario
    {
        get { lock (_gate) return _scenario; }
    }

    public void Reset(DemoScenario scenario = DemoScenario.HealthyDay)
    {
        if (!Enum.IsDefined(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        lock (_gate)
        {
            _scenario = scenario;
            _lastSampleAt = null;
            _scenarioStartedAt = null;
            _breathingUntil = null;
            _day = null;
            _poorSleep = false;
            _wasWalking = false;
            _walkingTicks = 0;
            _calories = 0;
            _stress = 30;
            _heartRate = 73;
            _hrv = 61.5;
            _recovery = 72.1;
            _respiration = 14.1;
            _breathingStress = 0;
            _breathingHeartRate = 0;
            _lastContext = new();
        }
    }

    public void SetScenario(DemoScenario scenario)
    {
        if (!Enum.IsDefined(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        lock (_gate)
        {
            if (_scenario == scenario) return;
            _scenario = scenario;
            _scenarioStartedAt = null;
        }
    }

    public void CompleteBreathing(DateTimeOffset now)
    {
        lock (_gate)
        {
            Advance(_lastContext, now);
            // A modest persistent reduction avoids doubling the engine's recovery overlay.
            _stress = Math.Max(12, _stress - 22);
            _heartRate = Math.Max(58, _heartRate - 8);
            _breathingStress = _stress;
            _breathingHeartRate = _heartRate;
            _breathingUntil = now.AddSeconds(90);
        }
    }

    public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Advance(context, now));
        }
    }

    private WearableSample Advance(WorkContext context, DateTimeOffset now)
    {
        if (_lastSampleAt is { } last && now < last)
            throw new ArgumentOutOfRangeException(nameof(now), "Sample and breathing times must be nondecreasing.");

        // Integrate the protected interval separately even if polling skipped its end.
        if (_lastSampleAt is { } before && _breathingUntil is { } boundary && before < boundary && now > boundary)
            Advance(context, boundary);

        double seconds = _lastSampleAt is { } previous ? (now - previous).TotalSeconds : 0;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        bool newDay = _day != day;
        if (newDay)
        {
            // Daily counters and sleep use UTC dates, independent of caller offset changes.
            _day = day;
            _walkingTicks = 0;
            _calories = 0;
            _poorSleep = _scenario == DemoScenario.PoorSleepDay;
        }
        else if (_scenario == DemoScenario.PoorSleepDay)
        {
            // Selecting poor sleep changes last night's history, not just this tick's mood.
            _poorSleep = true;
        }

        if (_lastSampleAt.HasValue)
        {
            long ticks = (now - _lastSampleAt.Value).Ticks;
            if (newDay) ticks = Math.Min(ticks, now.UtcDateTime.TimeOfDay.Ticks);
            double minutes = (double)ticks / TimeSpan.TicksPerMinute;
            _calories += minutes * (_wasWalking ? 4.65 : 1.15);
            if (_wasWalking)
            {
                _walkingTicks += ticks;
            }
        }

        _scenarioStartedAt ??= now;
        double elapsed = (now - _scenarioStartedAt.Value).TotalSeconds;
        double progress = Math.Clamp(elapsed / 60, 0, 1);
        bool walking = _scenario == DemoScenario.RecoveryAfterBreak;
        double targetStress = _scenario switch
        {
            DemoScenario.HealthyDay => 28,
            DemoScenario.DeepFocusSession => 20,
            DemoScenario.RisingStress => 30 + 62 * Math.Clamp(elapsed / 20, 0, 1),
            DemoScenario.MeetingOverload => 80 + Math.Min(10, Math.Max(0, (double)context.AppSwitchCount) * 0.3
                + Math.Max(0, (double)context.EscalationCount) * 2),
            DemoScenario.RecoveryAfterBreak => 28 - 10 * progress,
            DemoScenario.PoorSleepDay => 42,
            _ => throw new InvalidOperationException("Unknown demo scenario.")
        };
        // Bounded waves add variation without random-walk drift over long sessions.
        double wave = Math.Sin(now.UtcDateTime.TimeOfDay.TotalSeconds / 18 + _phase);
        targetStress = Math.Clamp(targetStress + (_poorSleep ? 9 : 0) + wave, 10, 96);
        bool breathing = _breathingUntil is { } until && now <= until;
        if (breathing) targetStress = Math.Min(targetStress, _breathingStress);
        double blend = 1 - Math.Exp(-seconds / 3);
        if (!_lastSampleAt.HasValue && _poorSleep)
        {
            _stress = 45;
            _heartRate = 82;
            _hrv = 39;
            _recovery = 42;
            _respiration = 16;
        }
        _stress = Approach(_stress, targetStress, blend, seconds * 3.5);
        double targetHeartRate = 62 + 0.36 * _stress + (_poorSleep ? 4 : 0) + (walking ? 20 : 0);
        if (breathing) targetHeartRate = Math.Min(targetHeartRate, _breathingHeartRate);
        _heartRate = Approach(_heartRate, targetHeartRate, blend, seconds * 2);
        _hrv = Approach(_hrv, 78 - 0.55 * _stress - (_poorSleep ? 14 : 0), blend, seconds * 2);
        _recovery = Approach(_recovery, Math.Clamp(88 - 0.53 * _stress - (_poorSleep ? 22 : 0)
            + (walking ? 10 * progress : 0), 10, 96), blend, seconds * 2);
        _respiration = Approach(_respiration, 12 + 0.07 * _stress + (walking ? 2 : 0), blend, seconds * 0.4);
        _lastSampleAt = now;
        _lastContext = context;
        _wasWalking = walking;

        int totalSleep = _poorSleep ? 290 : 465;
        int deepSleep = _poorSleep ? 35 : 95;
        int remSleep = _poorSleep ? 50 : 110;
        return new WearableSample
        {
            Timestamp = now,
            HeartRate = _heartRate,
            RestingHeartRate = _poorSleep ? 67 : 60,
            HeartRateVariability = _hrv,
            StressLevel = _stress,
            Steps = (int)(_walkingTicks * 96 / TimeSpan.TicksPerMinute),
            Calories = _calories,
            ActiveMinutes = (int)(_walkingTicks / TimeSpan.TicksPerMinute),
            SleepScore = _poorSleep ? 42 : 88,
            TotalSleepMinutes = totalSleep,
            DeepSleepMinutes = deepSleep,
            RemSleepMinutes = remSleep,
            LightSleepMinutes = totalSleep - deepSleep - remSleep,
            ReadinessScore = Math.Clamp(_recovery + (_poorSleep ? -6 : 4), 0, 100),
            RecoveryScore = _recovery,
            BloodOxygen = 97.5 + 0.3 * wave,
            RespirationRate = _respiration,
            BodyBattery = Math.Clamp(_recovery - (_poorSleep ? 8 : 3), 0, 100),
            FocusScore = null
        };
    }

    private static double Approach(double current, double target, double blend, double maximumChange) =>
        current + Math.Clamp((target - current) * blend, -maximumChange, maximumChange);
}
