namespace PulsePal.Core;

/// <summary>Explainable, non-medical demo estimates; not a physiological focus measurement.</summary>
public sealed class WellnessAnalyzer
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastTimestamp;
    private DateTimeOffset? _highSince;
    private DateTimeOffset? _riskSince;
    private DateOnly? _sleepDay;
    private double _dailySleepBurden;
    private double _stress = 30;
    private AnalysisResult? _lastResult;

    public AnalysisResult Analyze(WearableSample sample, WorkContext context, bool focusActive = false, bool recovering = false,
        bool recoveryActive = false, double recoveryStressReduction = 20)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(context);
        if (!double.IsFinite(recoveryStressReduction) || recoveryStressReduction is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(recoveryStressReduction));

        lock (_gate)
        {
            if (_lastTimestamp is { } previous && sample.Timestamp < previous)
            {
                var ignoredReasons = _lastResult!.Reasons.ToList();
                ignoredReasons.Add("Out-of-order sample ignored; it cannot advance sustained risk.");
                return _lastResult with { Reasons = ignoredReasons.AsReadOnly() };
            }

            var reasons = new List<string>
            {
                "Non-medical demo heuristic, not a diagnosis or a physiological focus measurement."
            };
            double? Metric(double? value, string name, double minimum, double maximum)
            {
                if (value is { } number && double.IsFinite(number) && number >= minimum && number <= maximum)
                    return number;
                reasons.Add($"{name} missing or invalid; neutral estimate, not evidence of high stress.");
                return null;
            }

            var heartRate = Metric(sample.HeartRate, "Heart rate", 25, 240);
            var restingRate = Metric(sample.RestingHeartRate, "Resting heart rate", 25, 140);
            var hrv = Metric(sample.HeartRateVariability, "Heart-rate variability", 1, 300);
            var reportedStress = Metric(sample.StressLevel, "Wearable stress", 0, 100);
            var sleepScore = Metric(sample.SleepScore, "Sleep score", 0, 100);
            var sleepMinutes = Metric(sample.TotalSleepMinutes, "Sleep duration", 0, 1440);
            var battery = Metric(sample.BodyBattery, "Body battery", 0, 100);
            var readiness = Metric(sample.ReadinessScore, "Readiness", 0, 100);
            var recovery = Metric(sample.RecoveryScore, "Recovery", 0, 100);

            double switches = Math.Clamp(context.AppSwitchCount, 0, 100);
            double meetings = Math.Clamp(context.MeetingsToday, 0, 100);
            double upcoming = Math.Clamp(context.UpcomingMeetings, 0, 100);
            double escalations = Math.Clamp(context.EscalationCount, 0, 100);
            double unread = Math.Clamp(context.UnreadImportantItems, 0, 100);
            var idle = context.IdleTime < TimeSpan.Zero ? TimeSpan.Zero : context.IdleTime;
            var workload = Math.Min(15, switches * 0.5 + Math.Max(0, meetings - 3) * 2 + escalations * 3);
            if (workload >= 5)
                reasons.Add("App switching, meeting load or escalations add work-context pressure.");

            var focus = Bound(76 + (focusActive ? 14 : 0) - switches * 4 - upcoming * 3 - escalations * 5 - unread * 0.5);
            if (idle >= TimeSpan.FromMinutes(2))
                focus = Bound(focus - 20);
            if (idle >= TimeSpan.FromMinutes(5))
                focus = Math.Min(focus, 20);
            reasons.Add("Focus estimate uses work interruptions, idle time and the focus session only; wearable FocusScore is ignored.");

            // Keep the worst observed sleep burden for the UTC day, even when later samples omit sleep.
            var day = DateOnly.FromDateTime(sample.Timestamp.UtcDateTime);
            if (_sleepDay != day)
            {
                _sleepDay = day;
                _dailySleepBurden = 0;
            }
            var sleepBurden = Math.Max(
                sleepScore.HasValue ? Math.Clamp(80 - sleepScore.Value, 0, 65) : 0,
                sleepMinutes.HasValue ? Math.Clamp((480 - sleepMinutes.Value) / 6, 0, 65) : 0);
            _dailySleepBurden = Math.Max(_dailySleepBurden, sleepBurden);
            if (_dailySleepBurden > 0)
                reasons.Add("Poor or short sleep contributes to fatigue throughout this UTC day, including after a break.");

            var fatigue = Bound(20 + _dailySleepBurden + Math.Max(0, 50 - (battery ?? 50)) * 0.5
                + Math.Max(0, 50 - (readiness ?? 50)) * 0.25 + Math.Max(0, 50 - (recovery ?? 50)) * 0.25 + workload);
            if (battery < 40 || readiness < 40 || recovery < 40)
                reasons.Add("Low reported energy, readiness or recovery contributes to fatigue, not a diagnosis.");

            var targetStress = reportedStress.HasValue ? 30 + (reportedStress.Value - 30) * 0.85 : 30;
            var heartElevation = heartRate.HasValue && restingRate.HasValue
                ? Math.Clamp((heartRate.Value - restingRate.Value - 15) * 0.6, 0, 18) : 0;
            if (sample.ActiveMinutes >= 30 && heartElevation > 0)
            {
                heartElevation *= 0.5;
                reasons.Add("Reported activity may explain some heart-rate elevation; its stress weight is reduced.");
            }
            var lowHrv = hrv <= 30;
            if (heartElevation > 0)
                reasons.Add("Heart rate above the reported resting baseline contributes a limited stress cue.");
            if (lowHrv)
                reasons.Add("Low reported HRV contributes a limited cue; HRV alone does not establish high stress.");
            if (reportedStress >= 65)
                reasons.Add("Elevated wearable stress is an observed cue, checked for persistence over time.");

            var correlated = heartElevation >= 12 && lowHrv;
            if (correlated)
                reasons.Add("Elevated heart rate and low HRV correlate; together they carry more weight than either cue alone.");
            targetStress = Bound(targetStress + heartElevation + (lowHrv ? 12 : 0) + workload + (correlated ? 8 : 0));
            var highEvidence = reportedStress >= 65 || correlated;
            if (!highEvidence)
                targetStress = Math.Min(targetStress, 55);

            var elapsed = _lastTimestamp is { } last ? (sample.Timestamp - last).TotalSeconds : 0;
            if (recoveryActive && _lastTimestamp is { } previousTick)
            {
                var paused = sample.Timestamp - previousTick;
                if (_highSince is { } highSince) _highSince = highSince + paused;
                if (_riskSince is { } riskSince) _riskSince = riskSince + paused;
                reasons.Add("Recovery timer is running; sustained-risk timing is paused, with no completion benefit yet.");
            }
            // A long gap is not evidence that stress persisted while observations were absent.
            if (elapsed > 30 && !recoveryActive)
            {
                _highSince = null;
                _riskSince = null;
                _stress = 30;
                elapsed = 0;
                reasons.Add("Observation gap exceeded 30 seconds; sustained-risk timing restarted.");
            }
            if (recovering)
            {
                targetStress = Math.Max(0, targetStress - recoveryStressReduction);
                if (_lastResult is { State: not WellnessState.Recovering })
                    _stress = Math.Max(0, _stress - recoveryStressReduction);
                reasons.Add("Recovery mode reflects a reported break; it does not erase the day's sleep burden.");
            }

            var high = highEvidence && targetStress >= 65 && !recovering;
            if (high)
                _highSince ??= sample.Timestamp;
            else
                _highSince = null;

            var contextualRisk = _dailySleepBurden >= 20 || workload >= 10;
            if (high && contextualRisk)
                _riskSince ??= sample.Timestamp;
            else
                _riskSince = null;

            if (!_lastTimestamp.HasValue || elapsed == 0 && _lastTimestamp != sample.Timestamp)
                _stress = Math.Min(30, targetStress);
            else
                _stress += Math.Clamp(targetStress - _stress, -5 * elapsed, 1.75 * elapsed);
            _stress = Bound(_stress);

            var highSeconds = _highSince.HasValue ? (sample.Timestamp - _highSince.Value).TotalSeconds : 0;
            var riskSeconds = _riskSince.HasValue ? (sample.Timestamp - _riskSince.Value).TotalSeconds : 0;
            var sustained = high && highSeconds >= 12;
            var burnout = high && contextualRisk && riskSeconds >= 60;
            var burnoutScore = burnout
                ? Bound(65 + Math.Max(0, _stress - 65) * 0.3 + _dailySleepBurden * 0.2 + workload * 0.4)
                : high && contextualRisk ? Math.Min(49, riskSeconds / 60 * 49) : 0;
            if (recoveryActive)
                burnoutScore = Math.Min(burnoutScore, _lastResult?.BurnoutRiskScore ?? 0);
            if (high)
                reasons.Add(sustained
                    ? "Elevated stress cues sustained for at least 12 seconds; stress score ramps using elapsed time."
                    : "Elevated stress cues are not yet sustained for 12 seconds; a single spike is insufficient.");
            if (sustained && _stress >= 90)
                reasons.Add("Stress estimate is at least 90/100; peak-stress prompting requires 10 seconds of persistent observations at this level.");
            else if (sustained && _stress >= 80)
                reasons.Add("Stress estimate is at least 80/100: high stress, below the persistent peak-stress prompt threshold.");
            else if (sustained && _stress >= 65)
                reasons.Add("Stress estimate is at least 65/100: an early sustained-stress cue, not a peak-stress alert.");
            if (burnout)
                reasons.Add("Burnout risk is a non-medical demo flag: elevated cues sustained for at least 60 seconds plus poor sleep or heavy work context.");
            else
                reasons.Add("No sustained burnout flag: at least 60 seconds of elevated cues plus sleep or work risk is required.");

            var state = recovering ? WellnessState.Recovering
                : burnout ? WellnessState.BurnoutRisk
                : sustained && _stress >= 80 ? WellnessState.HighStress
                : sustained && _stress >= 65 ? WellnessState.Stressed
                : fatigue >= 65 ? WellnessState.Fatigued
                : focus >= 80 ? WellnessState.Focused
                : WellnessState.Balanced;
            _lastTimestamp = sample.Timestamp;
            _lastResult = new AnalysisResult(focus, _stress, fatigue, burnoutScore, state, reasons.AsReadOnly());
            return _lastResult;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastTimestamp = null;
            _highSince = null;
            _riskSince = null;
            _sleepDay = null;
            _dailySleepBurden = 0;
            _stress = 30;
            _lastResult = null;
        }
    }

    private static double Bound(double value) => Math.Clamp(value, 0, 100);
}
