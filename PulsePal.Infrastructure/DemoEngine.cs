using PulsePal.Core;

namespace PulsePal.Infrastructure;

/// <summary>Thread-safe orchestration for a two-second demo polling loop. All scores are non-medical heuristics.</summary>
public sealed class DemoEngine
{
    private const int HistoryLimit = 500;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly IWearableProvider _provider;
    private readonly MockWorkContextProvider _contexts = new();
    private readonly WellnessAnalyzer _analyzer = new();
    private readonly AttentionShield _shield = new();
    private readonly CooldownPolicy _cooldown = new();
    private readonly Queue<DemoHistoryEntry> _history = new();
    private DemoSnapshot? _current;
    private DemoScenario _scenario;
    private UserPreferences _preferences = new();
    private long _revision;
    private long _generation;
    private long _lifecycleVersion;
    private DateTimeOffset? _lastTickAt;
    private DateTimeOffset? _firstTickAt;
    private DateTimeOffset? _lastHydrationAt;
    private DateTimeOffset? _recoveryAt;
    private RecoveryActivity? _completedActivity;
    private double? _recoveryStress;
    private double? _recoveryHeartRate;
    private bool _recoveryActive;
    private DateTimeOffset? _peakSince;
    private DateTimeOffset? _belowPeakSince;
    private DateTimeOffset? _lastStressSampleAt;
    private bool _peakEpisode;
    private CompanionMessage? _pendingSessionMessage;

    private readonly record struct RecoveryBenefit(double Stress, double HeartRate, double Hrv,
        double Recovery, double Readiness, double Respiration);

    private static RecoveryBenefit Benefit(RecoveryActivity activity) => activity switch
    {
        RecoveryActivity.Breathing => new(22, 8, 10, 12, 8, 3),
        RecoveryActivity.ScreenBreak => new(30, 10, 12, 16, 10, 4),
        RecoveryActivity.WaterBreak => new(10, 3, 3, 5, 4, 1),
        RecoveryActivity.StretchBreak => new(16, 5, 6, 9, 6, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(activity))
    };

    public DemoEngine() : this(new SyntheticWearableProvider()) { }

    public DemoEngine(IWearableProvider provider, SessionClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        Clock = clock ?? new SessionClock();
        if (provider is SyntheticWearableProvider synthetic)
            _scenario = synthetic.Scenario;
    }

    public SessionClock Clock { get; } = new();
    public SessionLedger Ledger { get; } = new();
    public bool SupportsPresentation => _provider is SyntheticWearableProvider;
    public bool IsPresentationPaused { get { lock (_gate) return Clock.IsPresentation && Clock.IsPaused; } }
    public long Generation { get { lock (_gate) return _generation; } }
    public bool PeakStressEstablished
    {
        get { lock (_gate) return _peakEpisode && _current is { } current && IsPeak(current.Analysis); }
    }

    public bool IsCurrent(DemoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) return snapshot.Generation == _generation;
    }

    public DemoSnapshot? Current { get { lock (_gate) return _current; } }
    public DemoScenario Scenario { get { lock (_gate) return _scenario; } }
    public bool IsFocusActive { get { lock (_gate) return _shield.IsFocusActive; } }
    public IReadOnlyList<AttentionNotification> PendingNotifications
    {
        get { lock (_gate) return _shield.PendingNotifications; }
    }
    public IReadOnlyList<NotificationDecision> NotificationHistory
    {
        get { lock (_gate) return _shield.History; }
    }
    public IReadOnlyList<DemoHistoryEntry> History
    {
        get { lock (_gate) return Array.AsReadOnly(_history.ToArray()); }
    }
    public UserPreferences Preferences
    {
        get { lock (_gate) return _preferences; }
        set
        {
            var validated = JsonStateStore.Validate(new PersistedAppState { Preferences = value }).Preferences;
            lock (_gate) _preferences = validated;
        }
    }

    public async Task<DemoSnapshot> TickAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await TickCoreAsync(now, cancellationToken).ConfigureAwait(false)
        ?? throw new OperationCanceledException("The sampling lifecycle changed.", cancellationToken);

    public Task<DemoSnapshot?> SampleAsync(CancellationToken ct = default) => TickCoreAsync(null, ct);

    private async Task<DemoSnapshot?> TickCoreAsync(DateTimeOffset? requestedAt, CancellationToken cancellationToken)
    {
        long generation;
        long lifecycle;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPresentationPaused) return null;
            generation = _generation;
            lifecycle = _lifecycleVersion;
        }
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                WorkContext context;
                long revision;
                DateTimeOffset now;
                Task<WearableSample> request;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lifecycle != _lifecycleVersion || IsPresentationPaused ||
                        (requestedAt is null && generation != _generation))
                        return null;
                    now = requestedAt ?? Clock.GetUtcNow();
                    if (_lastTickAt is { } last && now < last)
                        throw new ArgumentOutOfRangeException(nameof(now), "Tick times must be nondecreasing.");
                    context = _contexts.GetContext(_scenario);
                    revision = _revision;
                    // Start the read under the state lock, but never hold that lock across asynchronous I/O.
                    request = _provider.GetSampleAsync(context, now, cancellationToken);
                }
                var sample = await request.ConfigureAwait(false);
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lifecycle != _lifecycleVersion || IsPresentationPaused ||
                        (requestedAt is null && generation != _generation))
                        return null;
                    // A UI scenario change or restore invalidates the in-flight work context.
                    if (revision != _revision)
                        continue;
                    ArgumentNullException.ThrowIfNull(sample);
                    _firstTickAt ??= now;
                    sample = ApplySyntheticRecovery(sample, now);
                    bool recovering = IsRecovering(now);
                    var analysis = _analyzer.Analyze(sample, context, _shield.IsFocusActive,
                        recovering && _provider is SyntheticWearableProvider, _recoveryActive,
                        _completedActivity is { } activity ? Benefit(activity).Stress : 20);
                    if (recovering) analysis = ReportRecovery(analysis);
                    bool peakReady = UpdatePeakStress(analysis, sample.Timestamp);
                    var message = _recoveryActive ? null : _pendingSessionMessage ?? SelectMessage(analysis, now, peakReady);
                    _pendingSessionMessage = null;
                    _current = new DemoSnapshot(sample, context, analysis, _scenario,
                        _shield.IsFocusActive, _shield.FocusStartedAt, _shield.PendingNotifications.Count,
                        _shield.UrgentCount, message) { Generation = _generation };
                    if (_lastTickAt != now)
                    {
                        _history.Enqueue(new DemoHistoryEntry(now, _scenario, analysis.State,
                            analysis.StressScore, analysis.FocusScore));
                        if (_history.Count > HistoryLimit) _history.Dequeue();
                    }
                    _lastTickAt = now;
                    return _current;
                }
            }
        }
        finally { _tickGate.Release(); }
    }

    public void StartPresentation()
    {
        lock (_gate)
        {
            EnsurePresentationSupported();
            ResetRuntime(DemoScenario.HealthyDay);
            Ledger.Reset();
            Clock.Resume();
            Clock.BeginPresentation(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
            _lifecycleVersion++;
        }
    }

    public void ResetPresentation()
    {
        lock (_gate)
        {
            EnsurePresentationSupported();
            ResetRuntime(DemoScenario.HealthyDay);
            Ledger.Reset();
            Clock.EndPresentation();
            Clock.Resume();
            _lifecycleVersion++;
        }
    }

    public void StopPresentation()
    {
        lock (_gate)
        {
            if (!Clock.IsPresentation) return;
            EndFocus(Clock.GetUtcNow());
            ResetRuntime(DemoScenario.HealthyDay);
            Clock.EndPresentation();
            Clock.Resume();
            _lifecycleVersion++;
        }
    }

    public void PausePresentation()
    {
        lock (_gate)
        {
            if (!Clock.IsPresentation || Clock.IsPaused) return;
            Clock.Pause();
            InvalidatePresentationMessages();
        }
    }

    public void ResumePresentation()
    {
        lock (_gate)
        {
            if (!Clock.IsPresentation || !Clock.IsPaused) return;
            Clock.Resume();
            InvalidatePresentationMessages();
        }
    }

    private void EnsurePresentationSupported()
    {
        if (!SupportsPresentation)
            throw new NotSupportedException("Presentation mode requires the synthetic wearable provider; replay and live providers cannot be reset.");
    }

    private void InvalidatePresentationMessages()
    {
        _generation++;
        _lifecycleVersion++;
        _pendingSessionMessage = null;
        if (_current is not null)
            _current = _current with { Generation = _generation, Message = null };
    }

    private void ResetRuntime(DemoScenario scenario)
    {
        if (_provider is SyntheticWearableProvider synthetic) synthetic.Reset(scenario);
        _scenario = scenario;
        _history.Clear();
        _shield.Reset();
        _analyzer.Reset();
        _cooldown.Reset();
        _current = null;
        _lastTickAt = null;
        _firstTickAt = null;
        _lastHydrationAt = null;
        _recoveryAt = null;
        _completedActivity = null;
        _recoveryStress = null;
        _recoveryHeartRate = null;
        _recoveryActive = false;
        ResetPeakStress();
        _pendingSessionMessage = null;
        _revision++;
        _generation++;
    }

    public void SetScenario(DemoScenario scenario)
    {
        if (!Enum.IsDefined(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        lock (_gate)
        {
            if (_scenario == scenario) return;
            if (_provider is SyntheticWearableProvider synthetic) synthetic.SetScenario(scenario);
            _scenario = scenario;
            _revision++;
            _generation++;
            // Keep the analyzer's daily sleep burden, cooldown and intervention history across scenarios.
            _current = null;
        }
    }

    public void StartFocus(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_shield.IsFocusActive) return;
            _shield.StartFocus(now);
            Ledger.StartFocus(now);
            _pendingSessionMessage = null;
            RefreshFocusSnapshot(null);
        }
    }

    public FocusSessionSummary? EndFocus(DateTimeOffset now)
    {
        lock (_gate)
        {
            var summary = _shield.EndFocus(now);
            if (summary is null) return null;
            Ledger.EndFocus(now);
            var message = new CompanionMessage("Focus session complete",
                $"{summary.Duration.TotalMinutes:F1} minutes protected. Released {summary.DelayedCount} delayed notifications; " +
                $"{summary.UrgentCount} priority notifications were allowed. See notification history for the released items.",
                CharacterState.Celebrating, "focus-summary");
            _pendingSessionMessage = message;
            RefreshFocusSnapshot(message);
            return summary;
        }
    }

    public void SetRecoveryActive(bool active)
    {
        lock (_gate)
        {
            if (_recoveryActive == active) return;
            _recoveryActive = active;
            _generation++;
            ResetPeakStress();
            if (active) _pendingSessionMessage = null;
            if (_current is not null)
                _current = _current with { Generation = _generation, Message = null };
        }
    }

    public void CompleteBreathing(DateTimeOffset now) => CompleteRecovery(RecoveryActivity.Breathing, now);

    public DemoSnapshot? CompleteRecoveryAndCapture(RecoveryActivity activity, DateTimeOffset now)
    {
        lock (_gate)
        {
            CompleteRecovery(activity, now);
            return _current;
        }
    }

    /// <summary>Called only after a recovery timer completes; benefits are bounded synthetic demo estimates.</summary>
    public void CompleteRecovery(RecoveryActivity activity, DateTimeOffset now)
    {
        var benefit = Benefit(activity);
        lock (_gate)
        {
            if (_lastTickAt is { } last && now < last) now = last;
            _generation++;
            _recoveryActive = false;
            _recoveryAt = now;
            _completedActivity = activity;
            _recoveryStress = _current?.Wearable.StressLevel is { } stress ? Math.Max(12, stress - benefit.Stress) : null;
            _recoveryHeartRate = _current?.Wearable.HeartRate is { } heartRate ? Math.Max(58, heartRate - benefit.HeartRate) : null;
            ResetPeakStress();
            if (_current is not null)
            {
                var sample = ApplySyntheticRecovery(_current.Wearable, now);
                var analysis = _provider is SyntheticWearableProvider
                    ? _analyzer.Analyze(sample, _current.Context, _shield.IsFocusActive, recovering: true,
                        recoveryStressReduction: benefit.Stress)
                    : _current.Analysis;
                _current = _current with
                {
                    Generation = _generation,
                    Wearable = sample,
                    Analysis = ReportRecovery(analysis),
                    Message = null
                };
            }
        }
    }

    public NotificationDecision SimulateNotification(NotificationKind kind, DateTimeOffset now, string? title = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_gate)
        {
            title ??= kind switch
            {
                NotificationKind.ManagerMessage => "Manager: please review the delivery plan",
                NotificationKind.Escalation => "Escalation: a customer needs your help",
                NotificationKind.CriticalAlert => "Critical: service incident needs attention",
                NotificationKind.MeetingReminder => "Meeting starts in five minutes",
                NotificationKind.FyiEmail => "FYI: weekly project update",
                NotificationKind.GroupMessage => "Team chat: a new group message",
                _ => "Low priority: background task completed"
            };
            var decision = _shield.Decide(new AttentionNotification(Guid.NewGuid(), now, kind, title));
            Ledger.RecordNotification(decision, _shield.IsFocusActive);
            RefreshFocusSnapshot(_current?.Message);
            // The UI displays allowed notification decisions directly, independently of wellness snooze.
            return decision;
        }
    }

    public void Snooze(TimeSpan duration, DateTimeOffset now)
    {
        lock (_gate)
        {
            _cooldown.Snooze(duration, now);
            if (duration > TimeSpan.Zero && _current is not null)
                _current = _current with { Message = null };
        }
    }

    public CompanionMessage MorningBriefing()
    {
        lock (_gate)
        {
            var context = _contexts.GetContext(_scenario);
            string sleep = _current?.Wearable.TotalSleepMinutes is { } minutes
                ? $"Reported sleep: {minutes / 60}h {minutes % 60}m. "
                : "Sleep data is not available yet. ";
            var profile = CompanionProfiles.Get(_preferences.CompanionProfile);
            return new CompanionMessage($"Good morning, {_preferences.DisplayName}",
                profile.Greeting + " " + sleep + $"{context.MeetingsToday} meetings and {context.PriorityTasks} priority tasks today. " +
                "Protect a focus block and leave room for breaks. Wellness estimates are non-medical demo heuristics.",
                CharacterState.Encouraging, "morning-briefing");
        }
    }

    public void Restore(PersistedAppState state)
    {
        var validated = JsonStateStore.Validate(state);
        lock (_gate)
        {
            ResetRuntime(validated.Scenario);
            Ledger.Reset();
            _preferences = validated.Preferences;
            foreach (var entry in validated.History) _history.Enqueue(entry);
        }
    }

    public PersistedAppState ExportState(DateTimeOffset now, CharacterState character)
    {
        lock (_gate)
        {
            return JsonStateStore.Validate(new PersistedAppState
            {
                Preferences = _preferences,
                Scenario = _scenario,
                Character = character,
                LastSaved = now,
                History = Array.AsReadOnly(_history.ToArray())
            });
        }
    }

    private bool IsRecovering(DateTimeOffset now) => _scenario == DemoScenario.RecoveryAfterBreak ||
        (_recoveryAt is { } started && now >= started && now - started <= TimeSpan.FromSeconds(90));

    private AnalysisResult ReportRecovery(AnalysisResult analysis)
    {
        var title = _completedActivity is { } activity ? RecoveryActivities.Get(activity).Title : "Recovery break";
        var effect = _provider is SyntheticWearableProvider
            ? "recovery is a non-medical synthetic demo effect, not a guaranteed physiological response."
            : "recovery is a reported activity only; measurements and numeric estimates are unchanged by completion.";
        return analysis with
        {
            State = WellnessState.Recovering,
            Reasons = Array.AsReadOnly(analysis.Reasons.Append($"{title} reported; {effect}").ToArray())
        };
    }

    private WearableSample ApplySyntheticRecovery(WearableSample sample, DateTimeOffset now)
    {
        // Do not invent improved measurements for replay or real adapters. Only synthetic telemetry is adjusted.
        if (_provider is not SyntheticWearableProvider || _recoveryAt is not { } started ||
            _completedActivity is not { } activity || now < started)
            return sample;
        double seconds = (now - started).TotalSeconds;
        if (seconds >= 120) return sample;
        var benefit = Benefit(activity);
        double weight = seconds <= 90 ? 1 : (120 - seconds) / 30;
        double? Lower(double? value, double? ceiling, double amount, double floor) => value is { } number
            ? number - Math.Max(0, number - (ceiling ?? Math.Max(floor, number - amount))) * weight : null;
        double? Raise(double? value, double amount, double maximum = 100) => value is { } number
            ? Math.Min(maximum, number + amount * weight) : null;
        return sample with
        {
            StressLevel = Lower(sample.StressLevel, _recoveryStress, benefit.Stress, 12),
            HeartRate = Lower(sample.HeartRate, _recoveryHeartRate, benefit.HeartRate, 58),
            HeartRateVariability = Raise(sample.HeartRateVariability, benefit.Hrv, 300),
            RecoveryScore = Raise(sample.RecoveryScore, benefit.Recovery),
            ReadinessScore = Raise(sample.ReadinessScore, benefit.Readiness),
            RespirationRate = Lower(sample.RespirationRate, null, benefit.Respiration, 12)
        };
    }

    private void ResetPeakStress()
    {
        _peakSince = null;
        _belowPeakSince = null;
        _lastStressSampleAt = null;
        _peakEpisode = false;
    }

    private static bool IsPeak(AnalysisResult analysis) => analysis.StressScore >= 90 &&
        analysis.State is WellnessState.HighStress or WellnessState.BurnoutRisk;

    private bool UpdatePeakStress(AnalysisResult analysis, DateTimeOffset timestamp)
    {
        if (_recoveryActive || analysis.State == WellnessState.Recovering)
        {
            ResetPeakStress();
            return false;
        }
        if (_lastStressSampleAt is { } last)
        {
            if (timestamp <= last) return false;
            if (timestamp - last > TimeSpan.FromSeconds(30)) ResetPeakStress();
        }
        _lastStressSampleAt = timestamp;
        if (analysis.StressScore < 80)
        {
            _belowPeakSince ??= timestamp;
            _peakSince = null;
            if (timestamp - _belowPeakSince.Value >= TimeSpan.FromSeconds(10)) _peakEpisode = false;
        }
        else
        {
            _belowPeakSince = null;
            if (IsPeak(analysis))
            {
                _peakSince ??= timestamp;
                if (timestamp - _peakSince.Value >= TimeSpan.FromSeconds(10)) _peakEpisode = true;
            }
            else _peakSince = null;
        }
        return _peakEpisode && IsPeak(analysis);
    }

    private CompanionMessage? SelectMessage(AnalysisResult analysis, DateTimeOffset now, bool peakReady)
    {
        if (_recoveryActive) return null;
        var profile = CompanionProfiles.Get(_preferences.CompanionProfile);
        if (peakReady)
        {
            if (!_cooldown.TryAllowPeakStress(now)) return null;
            var source = _provider is SyntheticWearableProvider ? "Synthetic stress cues" : "Demo stress estimates from reported cues";
            return new("Persistent high stress: choose a break", $"{source} established a persistent peak episode: at least 90/100 for 10 seconds. " +
                "Consider stepping away from the screen for 5 minutes, a water break, gentle movement, or breathing. " +
                "This is a non-medical demo cue, not a diagnosis. Remind Later will be respected. " +
                profile.RecoveryEncouragement, CharacterState.Concerned, "peak-stress");
        }
        CompanionMessage? message = analysis.State switch
        {
            WellnessState.HighStress or WellnessState.Stressed or WellnessState.BurnoutRisk
                when !_peakEpisode && (!_shield.IsFocusActive || analysis.State != WellnessState.Stressed) =>
                new("A moment to reset?", (analysis.State == WellnessState.Stressed
                    ? "An early sustained-stress cue is present. "
                    : "High stress cues are present; persistent peak-stress timing is checked separately. ") +
                    "Consider a screen break, water, gentle movement, or breathing. " +
                    "This is a non-medical demo estimate, not a diagnosis. " + profile.RecoveryEncouragement,
                    CharacterState.Concerned, "sustained-stress"),
            WellnessState.Fatigued when !_shield.IsFocusActive =>
                new("Make room for recovery", "Short sleep and energy cues suggest a lighter pace and breaks today. " +
                    "This is a non-medical demo estimate.", CharacterState.Encouraging, "fatigue"),
            WellnessState.Recovering when !_shield.IsFocusActive =>
                new("A welcome break", profile.RecoveryEncouragement + " " +
                    (_provider is SyntheticWearableProvider ? "Your break is reflected in the synthetic demo recovery trend. "
                        : "Your break is recorded; no measurement improvement is assumed. ") +
                    "It does not erase last night's sleep or guarantee a health effect.", CharacterState.Happy, "recovery"),
            _ => null
        };
        if (message is null && !_shield.IsFocusActive && _preferences.HydrationReminders &&
            _firstTickAt is { } first && now - (_lastHydrationAt ?? first) >= TimeSpan.FromMinutes(30))
            message = new("Time for a water break?", "Take a comfortable pause and have some water if you need it.",
                CharacterState.Encouraging, "hydration");
        if (message is null || !_cooldown.TryAllow(message.Category, now)) return null;
        if (message.Category == "hydration") _lastHydrationAt = now;
        return message;
    }

    private void RefreshFocusSnapshot(CompanionMessage? message)
    {
        if (_current is not null)
            _current = _current with
            {
                IsFocusActive = _shield.IsFocusActive,
                FocusStartedAt = _shield.FocusStartedAt,
                QueuedNotifications = _shield.PendingNotifications.Count,
                UrgentNotifications = _shield.UrgentCount,
                Message = _recoveryActive ? null : message
            };
    }
}
