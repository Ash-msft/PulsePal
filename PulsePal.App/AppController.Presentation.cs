using System.Diagnostics;
using PulsePal.Core;

namespace PulsePal.App;

public sealed partial class AppController
{
    private PresenterWindow? _presenter;
    private readonly GuidedEvents _guidedEvents = new();
    private readonly Stopwatch _presentationRefresh = Stopwatch.StartNew();
    private PersistedAppState? _ordinaryStateBeforePresentation;
    private DemoSnapshot? _recoveryBefore;
    private bool _acceleratedDemo;
    private bool _tourRecoveryAttempted;
    private bool _tourRecoveryResolved;
    private bool _introPending;
    private bool _explicitIntroPending;
    private RecoveryActivity _selectedPresentationBreak = RecoveryActivity.ScreenBreak;

    public GuidedTour Tour { get; }
    public bool IsPresentationPaused => Engine.IsPresentationPaused;
    private DateTimeOffset SessionNow => Engine.Clock.GetUtcNow();
    public SessionStory SessionStory => Engine.Ledger.Snapshot(SessionNow);
    public RecoveryComparison? LastComparison { get; private set; }

    public bool AcceleratedDemo
    {
        get => _acceleratedDemo;
        set
        {
            if (_recovery.IsActive || IsPresentationPaused || value == _acceleratedDemo) return;
            if (value && !Engine.SupportsPresentation)
            {
                Status = "Accelerated demo requires synthetic telemetry. Replay and live data are never changed.";
                Updated?.Invoke();
                return;
            }
            SetProperty(ref _acceleratedDemo, value);
            RefreshPresentationLabel();
            Updated?.Invoke();
        }
    }

    public RecoveryActivity SelectedPresentationBreak
    {
        get => _selectedPresentationBreak;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_recovery.IsActive || IsPresentationPaused || value == _selectedPresentationBreak) return;
            SetProperty(ref _selectedPresentationBreak, value);
            Updated?.Invoke();
        }
    }

    public string PresentationStatus
    {
        get
        {
            if (IsPresentationPaused) return "Simulation, focus accounting and recovery timer are frozen. Resume when ready.";
            if (_recovery.IsActive)
                return $"Waiting for {RecoveryActivities.Get(ActiveRecovery!.Value).Title}: {RecoveryRemaining:mm\\:ss} remaining. Next cannot finish a break.";
            if (Tour.Step == TourStep.Recovery && !_tourRecoveryAttempted)
                return $"Choose any break now, or {RecoveryActivities.Get(SelectedPresentationBreak).Title} starts automatically in {Math.Max(0, Math.Ceiling(8 - Tour.TimeInStep.TotalSeconds)):0}s. This uses your saved preference unless you choose another.";
            if (Tour.Step == TourStep.Summary) return "End-of-demo summary · session totals only. Stop returns to ordinary wallclock mode; Start begins a fresh session.";
            if (Tour.Step == TourStep.Idle) return "Start asks before clearing the current simulated session. Acceleration is opt-in and never saved.";
            return Tour.WaitingReason;
        }
    }

    public void ShowPresenter() => Run("Open presenter", () =>
    {
        if (_presenter is null || _presenter.IsClosed) _presenter = new PresenterWindow(this);
        _presenter.Activate();
    });

    public void ShowSessionStory() => Run("Open Session story", () =>
    {
        ShowPresenter();
        _presenter?.ShowStory();
    });

    // UI commands confirm first; this synchronous lifecycle also supports isolated smoke tests.
    public void StartPresentation() => Run("Start guided session", () =>
    {
        if (!Engine.SupportsPresentation)
            throw new NotSupportedException("A day with PulsePal requires the synthetic wearable provider. Live and replay sessions cannot be reset.");
        var ordinary = _ordinaryStateBeforePresentation ?? Engine.ExportState(DateTimeOffset.Now, _character);
        ClearControllerSession();
        Engine.StartPresentation();
        _ordinaryStateBeforePresentation = ordinary;
        _selectedPresentationBreak = Engine.Preferences.PreferredBreak;
        Tour.Start();
        Log("A DAY WITH PULSEPAL · fresh synthetic session started; previous work cleared with confirmation");
        EnterPresentationStep();
    });

    public void ResetPresentation() => Run("Reset guided session", () =>
    {
        if (!Engine.SupportsPresentation)
            throw new NotSupportedException("Reset is supported only for the synthetic demo; live and replay data are unchanged.");
        ClearControllerSession();
        Engine.ResetPresentation();
        Tour.Stop();
        _ordinaryStateBeforePresentation = null;
        _acceleratedDemo = false;
        _selectedPresentationBreak = Engine.Preferences.PreferredBreak;
        RefreshPresentationLabel();
        ShowMessage(new("Fresh simulated session", "History, queue, snooze, timers, comparisons and session totals were cleared. Your companion and preferences are unchanged.", CharacterState.Thinking, "tour"), true);
        _ = PersistAsync();
    });

    public void PausePresentation() => Run("Pause guided session", () =>
    {
        if (!Engine.Clock.IsPresentation || IsPresentationPaused) return;
        Engine.PausePresentation();
        _visibleTime.Stop();
        RefreshPresentationLabel();
    });

    public void ResumePresentation() => Run("Resume guided session", () =>
    {
        if (!IsPresentationPaused) return;
        Engine.ResumePresentation();
        if (_window?.IsPopupVisible == true) _visibleTime.Start();
        RefreshPresentationLabel();
    });

    public void NextPresentation() => Run("Next guided step", () => AdvancePresentation(true));

    public void StopPresentation() => Run("Stop guided session", () =>
    {
        var wasPresentation = Engine.Clock.IsPresentation || Tour.Step != TourStep.Idle;
        CancelRecoveryCore();
        if (Engine.IsFocusActive && wasPresentation) EndFocusForFeedback();
        Engine.StopPresentation();
        Tour.Stop();
        _ordinaryStateBeforePresentation = null;
        _acceleratedDemo = false;
        _suppressedUntil = default;
        _skipNextFocusSummary = false;
        ClearPeakPrompt();
        _window?.ClearTransientPresentation();
        RefreshPresentationLabel();
        if (wasPresentation)
            ShowMessage(new("Back to your own pace", "Ordinary timing is restored. Session story remains available; an unfinished break has no completion benefit.", CharacterState.Normal, "tour"), true);
    });

    public void StartSelectedPresentationRecovery() => StartRecovery(SelectedPresentationBreak);

    private void ClearControllerSession()
    {
        _recovery.Cancel();
        _recoveryBefore = null;
        LastComparison = null;
        _tourRecoveryAttempted = _tourRecoveryResolved = false;
        _introPending = _explicitIntroPending = false;
        _suppressedUntil = default;
        _skipNextFocusSummary = false;
        _peakPromptActive = false;
        _peakResolvedSince = null;
        _visibleTime.Reset();
        _activity.Clear();
        _guidedEvents.Reset();
        _window?.ClearTransientPresentation();
        _window?.SetFocus(false);
        _tray?.SetFocusActive(false);
    }

    private bool ExplainPresentationLock()
    {
        if (!Tour.IsActive && !IsPresentationPaused) return false;
        _window?.SetBanner("The guided session owns focus and scenarios. Use Pause, Next or Stop in the presenter to keep its story coherent.");
        return true;
    }

    private void AdvancePresentation(bool manual)
    {
        if (IsPresentationPaused) return;
        if (Tour.IsActive)
        {
            DeliverGuidedEvents(false);
            if (Tour.Step == TourStep.Recovery && !_tourRecoveryAttempted &&
                Tour.TimeInStep >= TimeSpan.FromSeconds(8))
                StartSelectedPresentationRecovery();
            if (!_recovery.IsActive)
            {
                var previous = Tour.Step;
                var advanced = manual
                    ? Tour.Next(Engine.PeakStressEstablished, false, _tourRecoveryResolved)
                    : Tour.Update(Engine.PeakStressEstablished, false, _tourRecoveryResolved);
                if (advanced)
                {
                    DeliverGuidedEvents(true, previous);
                    EnterPresentationStep();
                }
            }
        }
        if (manual || _presentationRefresh.Elapsed >= TimeSpan.FromMilliseconds(250))
        {
            _presentationRefresh.Restart();
            RefreshPresentationLabel();
            // Avoid rebuilding developer history at animation frequency when the presenter is closed.
            if (manual || _presenter is { IsClosed: false }) Updated?.Invoke();
        }
    }

    private void DeliverGuidedEvents(bool finishStep, TourStep? step = null)
    {
        foreach (var item in _guidedEvents.TakeDue(step ?? Tour.Step, Tour.TimeInStep, finishStep))
            DeliverNotification(item.Kind, item.Title.Replace("{name}", Engine.Preferences.DisplayName, StringComparison.Ordinal));
    }

    private void EnterPresentationStep()
    {
        var profile = CompanionProfiles.Get(Engine.Preferences.CompanionProfile);
        switch (Tour.Step)
        {
            case TourStep.Briefing:
                ShowMessage(Engine.MorningBriefing() with { Text = profile.Greeting + " " + Tour.Narrative + " A fresh simulated session has begun." }, true);
                break;
            case TourStep.Focus:
                Engine.SetScenario(DemoScenario.DeepFocusSession);
                Engine.StartFocus(SessionNow);
                _tray?.SetFocusActive(true);
                _window?.SetFocus(true);
                Log("FOCUS STARTED · guided simulated Attention Shield on");
                ShowMessage(new(Tour.Title, profile.FocusEncouragement + " " + Tour.Narrative, CharacterState.Focused, "tour"), true);
                break;
            case TourStep.Interruptions:
                ShowMessage(new(Tour.Title, "I'll defer FYIs and group chat during focus. Manager approvals, meeting reminders and customer escalations still get through. These are synthetic notifications only.", CharacterState.Thinking, "tour"), true);
                break;
            case TourStep.MeetingStress:
                Engine.SetScenario(DemoScenario.MeetingOverload);
                ShowMessage(new(Tour.Title, "Meetings and app switching add pressure in this scenario. I'll watch correlated synthetic signals until stress stays at least 90 for 10 seconds; Next cannot invent a peak.", CharacterState.Concerned, "tour"), true);
                break;
            case TourStep.Recovery:
                _tourRecoveryAttempted = _tourRecoveryResolved = false;
                _window?.SetBanner(PresentationStatus);
                ShowCompanion();
                break;
            case TourStep.Comparison:
                ShowMessage(new("Before and after our pause", LastComparison?.Description ??
                    "The break was cancelled. No completion benefit or success comparison was recorded.", CharacterState.Encouraging, "tour"), true);
                break;
            case TourStep.Summary:
                if (Engine.IsFocusActive) EndFocusForFeedback();
                Engine.SetScenario(DemoScenario.HealthyDay);
                ShowMessage(new("Session story / End-of-demo summary", SessionStory.Description +
                    " Your comparison remains in Session story; these are not calendar-day totals or productivity gains.", CharacterState.Celebrating, "tour"), true);
                ShowSessionStory();
                break;
        }
        DeliverGuidedEvents(false);
        RefreshPresentationLabel();
        Updated?.Invoke();
    }

    private void CompleteRecoveryFeedback(RecoveryActivity completed)
    {
        var after = Engine.CompleteRecoveryAndCapture(completed, SessionNow);
        _window?.EndRecovery();
        LastComparison = RecoveryComparison.Capture(_recoveryBefore, after, Engine.SupportsPresentation, _recovery.IsAccelerated);
        Engine.Ledger.CompleteRecovery(new(completed, _recovery.ActualDuration, _recovery.RepresentedDuration,
            _recovery.IsAccelerated, LastComparison));
        _recoveryBefore = null;
        if (Tour.Step == TourStep.Recovery) _tourRecoveryResolved = true;
        Log("RECOVERY COMPLETE · " + completed);
        var option = RecoveryActivities.Get(completed);
        var encouragement = CompanionProfiles.Get(Engine.Preferences.CompanionProfile).RecoveryEncouragement;
        var text = (_recovery.IsAccelerated
            ? $"Accelerated demo preview completed: {_recovery.ActualDuration.TotalSeconds:0} actual seconds, {_recovery.RepresentedDuration.TotalSeconds:0} represented seconds. "
            : option.CompletionText + " ") + encouragement + " " + LastComparison.Description;
        ShowMessage(new("A softer landing.", text, CharacterState.Happy, "recovery-complete"), true);
        _ = PersistAsync();
        Updated?.Invoke();
    }

    private void CancelRecoveryCore()
    {
        if (!_recovery.IsActive) return;
        _recovery.Cancel();
        Engine.SetRecoveryActive(false);
        Engine.Ledger.CancelRecovery();
        _recoveryBefore = null;
        LastComparison = null;
        if (Tour.Step == TourStep.Recovery) _tourRecoveryResolved = true;
        _window?.EndRecovery();
        RefreshPresentationLabel();
    }

    private void RefreshPresentationLabel()
    {
        var text = AcceleratedDemo ? "Accelerated demo · recovery previews, not real-duration breaks" : "";
        if (Engine.Clock.IsPresentation)
            text = "A day with PulsePal · " + (IsPresentationPaused ? "Paused" : Tour.Step.ToString()) +
                (text.Length > 0 ? " · " + text : " · real-time recovery");
        _window?.SetPresentationLabel(text);
    }

    public void MeetCompanion() => Run("Meet companion", () => RequestIntroduction(true));

    private void RequestIntroduction(bool explicitlyRequested)
    {
        _introPending = true;
        _explicitIntroPending |= explicitlyRequested;
        if (explicitlyRequested && (_recovery.IsActive || _peakPromptActive || IsPresentationPaused))
        {
            var profile = CompanionProfiles.Get(Engine.Preferences.CompanionProfile);
            _window?.SetBanner($"Meet {profile.Name} · {profile.Personality}. {profile.Greeting} Your current prompt or recovery remains in place.");
            ShowCompanion();
            _introPending = _explicitIntroPending = false;
            return;
        }
        TryDeferredIntroduction();
    }

    private void TryDeferredIntroduction()
    {
        if (!_introPending || IsPresentationPaused || _recovery.IsActive || _peakPromptActive ||
            (!_explicitIntroPending && SessionNow < _suppressedUntil)) return;
        var profile = CompanionProfiles.Get(Engine.Preferences.CompanionProfile);
        var message = new CompanionMessage($"Meet {profile.Name} · {profile.Personality}",
            profile.Greeting + " " + profile.FocusEncouragement + " " + profile.RecoveryEncouragement,
            profile.Id == CompanionProfileId.Kairo ? CharacterState.Celebrating :
                profile.Id == CompanionProfileId.Lumi ? CharacterState.Happy : CharacterState.Encouraging, "introduction");
        _introPending = _explicitIntroPending = false;
        _character = message.Character;
        LastMessage = message.Title + " · " + message.Text;
        _window?.PlayIntroduction(message);
        ShowCompanion();
        Updated?.Invoke();
    }
}
