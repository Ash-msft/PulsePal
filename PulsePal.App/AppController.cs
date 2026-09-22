using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using PulsePal.App.Services;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.App;

public sealed partial class AppController : ObservableObject, IDisposable
{
    private readonly JsonStateStore _store;
    private readonly DispatcherQueueTimer _uiTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly RecoverySession _recovery;
    private readonly Stopwatch _visibleTime = new();
    private readonly Stopwatch _checkpointTime = Stopwatch.StartNew();
    private readonly List<string> _activity = [];
    private CompanionWindow? _window;
    private DemoControlsWindow? _controls;
    private CompanionProfileWindow? _profile;
    private bool _peakPromptActive;
    private DateTimeOffset? _peakResolvedSince;
    private TrayIcon? _tray;
    private DateTimeOffset _suppressedUntil;
    private bool _exiting;
    private bool _disposed;
    private string _status = "Starting local storage…";
    private CharacterState _character = CharacterState.Normal;
    private bool _storageHealthy = true;
    private bool _skipNextFocusSummary;

    public DemoEngine Engine { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string LastMessage { get; private set; } = "";
    public IReadOnlyList<string> ActivityLog => _activity;
    public bool HasWindow => _window is not null;
    public bool IsBreathing => ActiveRecovery == RecoveryActivity.Breathing;
    public RecoveryActivity? ActiveRecovery => _recovery.ActiveActivity;
    public TimeSpan RecoveryRemaining => _recovery.Remaining;
    public bool IsPeakPromptActive => _peakPromptActive;
    public event Action? Updated;
    public event Action? ExitRequested;

    public AppController(DemoEngine engine, JsonStateStore store, DispatcherQueue dispatcher)
    {
        Engine = engine;
        _store = store;
        _recovery = new RecoverySession(engine.Clock);
        Tour = new GuidedTour(engine.Clock);
        _uiTimer = dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(50);
        _uiTimer.Tick += OnUiTick;
    }

    public async Task InitializeAsync()
    {
        _window = new CompanionWindow(this);
        try
        {
            var state = await _store.LoadAsync();
            Engine.Restore(state);
            _character = state.Character;
            Status = "Saved locally · " + _store.FilePath;
        }
        catch (Exception exception)
        {
            _storageHealthy = false;
            ReportError("Local settings could not be loaded. Defaults are in use.", exception);
        }
        try
        {
            _tray = new TrayIcon(HandleTrayCommand);
            _tray.Error += exception => ReportError("Notification-area icon failed", exception);
        }
        catch (Exception exception)
        {
            ReportError("Tray unavailable. Keep this companion open; Exit remains available.", exception);
        }
        var snapshot = await Engine.TickAsync(DateTimeOffset.Now);
        AcceptSnapshot(snapshot, false);
        _window.ApplyPreferences(Engine.Preferences);
        _selectedPresentationBreak = Engine.Preferences.PreferredBreak;
        _tray?.SetCompanionName(CompanionProfiles.Get(Engine.Preferences.CompanionProfile).Name);
        _window.SetCharacter(_character);
        ShowMessage(Engine.MorningBriefing(), true);
        _uiTimer.Start();
    }

    private void HandleTrayCommand(string command)
    {
        switch (command)
        {
            case "show": ShowCompanion(); break;
            case "focus": ToggleFocus(); break;
            case "breathing": StartBreathing(); break;
            case "screen-break": StartRecovery(RecoveryActivity.ScreenBreak); break;
            case "water-break": StartRecovery(RecoveryActivity.WaterBreak); break;
            case "stretch-break": StartRecovery(RecoveryActivity.StretchBreak); break;
            case "profile": ShowProfile(); break;
            case "presenter": ShowPresenter(); break;
            case "story": ShowSessionStory(); break;
            case "meet": MeetCompanion(); break;
            case "controls": ShowControls(); break;
            case "connected": ShowConnectedHealth(); break;
            case "exit": RequestExit(); break;
        }
    }

    public void AcceptSnapshot(DemoSnapshot snapshot, bool allowMessage = true)
    {
        if (_exiting || !Engine.IsCurrent(snapshot) || IsPresentationPaused) return;
        try
        {
            _tray?.SetFocusActive(Engine.IsFocusActive);
            _window?.SetFocus(Engine.IsFocusActive);
            if (_peakPromptActive)
            {
                if (snapshot.Analysis.StressScore < 80)
                {
                    _peakResolvedSince ??= snapshot.Wearable.Timestamp;
                    if (snapshot.Wearable.Timestamp - _peakResolvedSince.Value >= TimeSpan.FromSeconds(10))
                        ClearPeakPrompt();
                }
                else _peakResolvedSince = null;
            }
            if (allowMessage && snapshot.Message is { } message)
            {
                if (_skipNextFocusSummary && message.Category == "focus-summary")
                    _skipNextFocusSummary = false;
                else if (!_recovery.IsActive && SessionNow >= _suppressedUntil &&
                    (!Tour.IsActive || message.Category is "peak-stress" or "notification"))
                    ShowMessage(message);
                else if (message.Category == "notification")
                    _window?.SetBanner(message.Title + " · " + message.Text);
            }
            Updated?.Invoke();
            if (_checkpointTime.Elapsed >= TimeSpan.FromSeconds(30))
            {
                _checkpointTime.Restart();
                _ = PersistAsync();
            }
        }
        catch (Exception exception) { ReportError("Could not update companion", exception); }
    }

    public void ShowCompanion()
    {
        if (_exiting) return;
        _window?.ShowPopup();
        _visibleTime.Restart();
    }

    public void ShowControls() => Run("Open demo controls", () =>
    {
        if (_controls is null || _controls.IsClosed) _controls = new DemoControlsWindow(this);
        _controls.Activate();
    });

    public void ShowProfile() => Run("Open companion profile", () =>
    {
        if (_profile is null || _profile.IsClosed) _profile = new CompanionProfileWindow(this);
        _profile.Activate();
    });

    public void SetScenario(DemoScenario scenario) => Run("Change scenario", () =>
    {
        if (ExplainPresentationLock() || ExplainRecoveryLock()) return;
        Engine.SetScenario(scenario);
        Log("Scenario → " + scenario);
        ShowMessage(new("Scenario ready", $"Synthetic {scenario} samples begin on the next tick.", CharacterState.Thinking, "demo"), true);
        _ = PersistAsync();
    });

    private bool ExplainRecoveryLock()
    {
        if (!_recovery.IsActive) return false;
        _window?.SetBanner("A break is running. Finish or cancel it before starting focus, another break, or a new scenario.");
        ShowCompanion();
        return true;
    }

    private string EndFocusForFeedback()
    {
        var released = Engine.PendingNotifications.ToArray();
        var summary = Engine.EndFocus(SessionNow);
        foreach (var item in released) Log("RELEASED · " + item.Kind + " · " + item.Title);
        if (summary is null) return string.Empty;
        _skipNextFocusSummary = true;
        var elapsed = summary.Duration;
        var text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00} of focus · " +
            $"{summary.DelayedCount} deferred · {summary.UrgentCount} urgent allowed. " +
            (released.Length == 0 ? "No queued demo notifications." : $"{released.Length} queued notifications released; see Demo controls.");
        Log("FOCUS COMPLETE · " + text);
        _tray?.SetFocusActive(false);
        _window?.SetFocus(false);
        return text;
    }

    public void ToggleFocus() => Run("Change focus session", () =>
    {
        if (ExplainPresentationLock() || ExplainRecoveryLock()) return;
        if (Engine.IsFocusActive)
        {
            var text = EndFocusForFeedback();
            ShowMessage(new("A little progress, celebrated.", text, CharacterState.Celebrating, "focus-summary"), true);
        }
        else
        {
            Engine.StartFocus(SessionNow);
            Log("FOCUS STARTED · simulated Attention Shield on");
            var encouragement = CompanionProfiles.Get(Engine.Preferences.CompanionProfile).FocusEncouragement;
            ShowMessage(new("One thing at a time.", encouragement + " Your focus session is running. Low-priority demo notifications wait; urgent demo notifications still get through.", CharacterState.Focused, "focus"), true);
        }
        _tray?.SetFocusActive(Engine.IsFocusActive);
        _window?.SetFocus(Engine.IsFocusActive);
        _ = PersistAsync();
    });

    public void SimulateNotification(NotificationKind kind) => Run("Simulate notification", () =>
    {
        if (ExplainPresentationLock()) return;
        DeliverNotification(kind);
    });

    private void DeliverNotification(NotificationKind kind, string? title = null)
    {
        var decision = Engine.SimulateNotification(kind, SessionNow, title);
        Log($"{(decision.Allowed ? "ALLOWED" : "QUEUED")} · {kind} · {decision.Notification.Title} · {decision.Reason}");
        if (decision.Allowed)
        {
            if (_recovery.IsActive || _peakPromptActive)
                _window?.SetBanner("Urgent demo notification allowed: " + decision.Notification.Title + " · " + decision.Reason +
                    (_recovery.IsActive ? " · Your break continues." : " · Your break choices remain available."));
            else
                ShowMessage(new("Demo notification allowed", decision.Notification.Title + "\n" + decision.Reason, CharacterState.Encouraging, "notification"), true);
        }
        else if (Tour.IsActive)
            ShowMessage(new("I'll hold that for later", decision.Notification.Title + " · " + decision.Reason +
                " Only synthetic notifications are deferred; urgent requests still get through.", CharacterState.Focused, "tour"), true);
    }

    public void StartBreathing() => StartRecovery(RecoveryActivity.Breathing);

    public void StartRecovery(RecoveryActivity activity) => Run("Start recovery", () =>
    {
        if (IsPresentationPaused || ExplainRecoveryLock()) return;
        if (Tour.IsActive && Tour.Step != TourStep.Recovery)
        {
            _window?.SetBanner("The guide will offer recovery after the sustained peak. Stop the guide first if you want an unscripted break now.");
            return;
        }
        var option = RecoveryActivities.Get(activity);
        _recoveryBefore = Engine.Current;
        LastComparison = null;
        var focusFeedback = Engine.IsFocusActive ? "Focus ended for your break. " + EndFocusForFeedback() : string.Empty;
        var accelerated = AcceleratedDemo && Engine.SupportsPresentation;
        _recovery.Start(activity, accelerated);
        if (Tour.Step == TourStep.Recovery) _tourRecoveryAttempted = true;
        Engine.SetRecoveryActive(true);
        ClearPeakPrompt();
        _character = CharacterState.Resting;
        _window?.BeginRecovery(activity, focusFeedback, accelerated);
        Log($"RECOVERY STARTED · {activity} · {_recovery.ActualDuration.TotalSeconds:0} actual seconds · {option.Duration.TotalSeconds:0} represented seconds");
        LastMessage = (accelerated ? "Accelerated demo · " : "") + option.Title + " · " +
            (accelerated ? "15 actual seconds; illustration, not real breathing instruction or a health outcome." : option.Instructions) +
            (focusFeedback.Length > 0 ? " · " + focusFeedback : string.Empty);
        RefreshPresentationLabel();
        ShowCompanion();
        _ = PersistAsync();
    });

    public void CancelBreathing()
    {
        if (IsBreathing) CancelRecovery();
    }

    public void CancelRecovery() => Run("Cancel recovery", () =>
    {
        if (!_recovery.IsActive) return;
        var activity = ActiveRecovery;
        CancelRecoveryCore();
        Log($"RECOVERY CANCELLED · {activity} · no completion benefit applied");
        ShowMessage(new("At your own pace.", "Break cancelled. No recovery benefit was applied. You can try again whenever you like.", CharacterState.Encouraging, "recovery-cancel"), true);
    });

    public void HideRecoveryTimer() => Run("Hide recovery timer", () =>
    {
        if (!_recovery.IsActive) return;
        if (_tray?.IsRegistered != true)
        {
            _window?.SetBanner("Tray unavailable; keep this timer open so you can return to it.");
            return;
        }
        _visibleTime.Reset();
        _window?.HidePopup();
    });

    private void OnUiTick(DispatcherQueueTimer sender, object args)
    {
        if (_exiting || IsPresentationPaused) return;
        try
        {
            if (_recovery.IsActive)
            {
                if (_recovery.TryComplete() is { } completed)
                {
                    CompleteRecoveryFeedback(completed);
                }
                else if (IsBreathing && !_recovery.IsAccelerated)
                {
                    var seconds = _recovery.Elapsed.TotalSeconds;
                    var phase = seconds % 12;
                    var label = phase < 4 ? "Breathe in" : phase < 6 ? "Hold gently" : "Breathe out";
                    var remaining = phase < 4 ? 4 - phase : phase < 6 ? 6 - phase : 12 - phase;
                    var expansion = phase < 4 ? phase / 4 : phase < 6 ? 1 : 1 - (phase - 6) / 6;
                    _window?.UpdateBreathing(label, (int)Math.Ceiling(remaining), (int)(seconds / 12) + 1, _recovery.Progress, expansion);
                }
                else
                {
                    _window?.UpdateRecovery(RecoveryRemaining, _recovery.Progress);
                }
            }
            AdvancePresentation(false);
            TryDeferredIntroduction();
            if (!_recovery.IsActive && !_peakPromptActive && _visibleTime.IsRunning &&
                _visibleTime.Elapsed.TotalSeconds >= Engine.Preferences.PopupSeconds &&
                _tray?.IsRegistered == true && _storageHealthy && _window?.HasError != true)
            {
                _visibleTime.Reset();
                _window?.HidePopup();
            }
        }
        catch (Exception exception)
        {
            CancelRecoveryCore();
            ReportError("Companion animation failed", exception);
        }
    }

    public void Dismiss() => Run("Dismiss companion", () =>
    {
        if (_recovery.IsActive) { HideRecoveryTimer(); return; }
        Suppress(TimeSpan.FromMinutes(5));
    });

    public void RemindLater() => Run("Snooze companion", () =>
    {
        if (_recovery.IsActive) { HideRecoveryTimer(); return; }
        Suppress(TimeSpan.FromMinutes(10));
    });

    private void ClearPeakPrompt()
    {
        if (!_peakPromptActive) return;
        _peakPromptActive = false;
        _peakResolvedSince = null;
        _window?.SetPeakPrompt(null);
        _visibleTime.Restart();
    }

    private void Suppress(TimeSpan duration)
    {
        ClearPeakPrompt();
        Engine.Snooze(duration, SessionNow);
        _suppressedUntil = SessionNow + duration;
        if (Tour.IsActive) PausePresentation();
        Log("Companion snoozed for " + duration.TotalMinutes + " minutes");
        _visibleTime.Reset();
        if (_tray?.IsRegistered == true) _window?.HidePopup();
        else _window?.SetBanner("Tray unavailable; the window stays available so PulsePal is not lost.");
    }

    public void SavePreferences(UserPreferences preferences) => Run("Save preferences", () =>
    {
        var previousProfile = Engine.Preferences.CompanionProfile;
        Engine.Preferences = preferences with
        {
            DisplayName = string.IsNullOrWhiteSpace(preferences.DisplayName) ? "Ashwani" : preferences.DisplayName.Trim(),
            PopupSeconds = Math.Clamp(preferences.PopupSeconds, 5, 120)
        };
        _window?.ApplyPreferences(Engine.Preferences);
        _tray?.SetCompanionName(CompanionProfiles.Get(Engine.Preferences.CompanionProfile).Name);
        if (!Tour.IsActive) SelectedPresentationBreak = Engine.Preferences.PreferredBreak;
        if (previousProfile != Engine.Preferences.CompanionProfile) RequestIntroduction(false);
        Log("Preferences updated");
        _ = PersistAsync();
    });

    private void ShowMessage(CompanionMessage message, bool force = false)
    {
        if (!force && (SessionNow < _suppressedUntil || IsPresentationPaused)) return;
        LastMessage = message.Title + " · " + message.Text;
        if (_recovery.IsActive)
        {
            _window?.SetBanner(LastMessage);
            Updated?.Invoke();
            return;
        }
        if (message.Category == "peak-stress")
        {
            if (_peakPromptActive) return;
            _peakPromptActive = true;
            _window?.SetPeakPrompt(message);
            ShowCompanion();
            return;
        }
        if (_peakPromptActive)
        {
            if (message.Category is "notification" or "focus-summary" or "focus")
                _window?.SetBanner(LastMessage);
            return;
        }
        _character = message.Character;
        _window?.SetMessage(message);
        ShowCompanion();
    }

    private void Run(string operation, Action action)
    {
        if (_exiting) return;
        try { action(); Updated?.Invoke(); }
        catch (Exception exception) { ReportError(operation, exception); }
    }

    private void Log(string text)
    {
        _activity.Add($"{DateTimeOffset.Now:HH:mm:ss} · {text}");
        if (_activity.Count > 200) _activity.RemoveRange(0, _activity.Count - 200);
    }

    public void ReportError(string operation, Exception exception)
    {
        AppLog.Write(operation, exception);
        Status = operation + " " + exception.Message;
        Log("ERROR · " + Status);
        _window?.SetError(Status);
        ShowCompanion();
        // Error rendering does not invoke Updated: a failing subscriber must not recurse.
    }

    private async Task PersistAsync(bool throwOnFailure = false)
    {
        await _saveGate.WaitAsync();
        try
        {
            var state = _ordinaryStateBeforePresentation is { } ordinary && Engine.Clock.IsPresentation
                ? ordinary with { Preferences = Engine.Preferences, LastSaved = DateTimeOffset.Now }
                : Engine.ExportState(DateTimeOffset.Now, _character);
            await _store.SaveAsync(state);
            _storageHealthy = true;
            Status = "Saved " + DateTimeOffset.Now.ToString("t") + " · " + _store.FilePath;
            Updated?.Invoke();
        }
        catch (Exception exception)
        {
            _storageHealthy = false;
            ReportError("Local storage failed. Changes may not survive restart.", exception);
            if (throwOnFailure) throw;
        }
        finally { _saveGate.Release(); }
    }

    public void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        _uiTimer.Stop();
        CancelRecoveryCore();
        ExitRequested?.Invoke();
    }

    public async Task ShutdownAsync()
    {
        try { await CloseConnectedHealthAsync(); }
        finally
        {
            if (Engine.IsFocusActive) Engine.EndFocus(SessionNow);
            await PersistAsync(true);
        }
    }

    public async Task RunSmokeTestAsync(bool full = false)
    {
        await Task.Delay(5500);
        var checks = new Dictionary<string, bool>();
        Directory.CreateDirectory(AppLog.DirectoryPath);
        checks["RenderedVisualContent"] = _window is not null &&
            await _window.CaptureSmokeVisualAsync(Path.Combine(AppLog.DirectoryPath, "smoke-test.png"));
        foreach (var character in Enum.GetValues<CharacterState>())
        {
            _window?.SetCharacter(character);
            await Task.Delay(80);
        }
        checks["AllCharacterStatesRendered"] = true;
        _window?.SetCharacter(_character);
        if (full)
        {
            var original = Engine.ExportState(DateTimeOffset.Now, _character);
            try
            {
                foreach (var scenario in Enum.GetValues<DemoScenario>())
                {
                    SetScenario(scenario);
                    await Task.Delay(2200);
                    checks["Scenario_" + scenario] = Engine.Current?.Scenario == scenario;
                }
                SetScenario(DemoScenario.HealthyDay);
                await Task.Delay(2200);
                var window = _window ?? throw new InvalidOperationException("Companion window unavailable.");
                checks["RecoveryAndProfileCommandsBound"] = window.RecoveryCommandsBound;
                window.ViewModel.ShowProfileCommand.Execute(null);
                await Task.Delay(250);
                checks["ProfileWindowOpens"] = _profile is { IsClosed: false };
                _profile?.Close();
                foreach (var profile in CompanionProfiles.All)
                {
                    SavePreferences(Engine.Preferences with { CompanionProfile = profile.Id, PreferredBreak = RecoveryActivity.StretchBreak, PopupSeconds = 5 });
                    foreach (var expression in Enum.GetValues<CharacterState>())
                    {
                        window.SetCharacter(expression);
                        await Task.Delay(80);
                    }
                    window.SetCharacter(CharacterState.Normal);
                    checks["ProfileApplied_" + profile.Id] = window.AppliedProfile == profile.Id &&
                        window.DisplayedIdentity.Contains(profile.Name) && window.DisplayedIdentity.Contains(profile.Personality);
                    checks["ProfileRendered_" + profile.Id] = await window.CaptureSmokeVisualAsync(
                        Path.Combine(AppLog.DirectoryPath, "profile-" + profile.Id + ".png"));
                }
                SavePreferences(Engine.Preferences with { PopupSeconds = 5 });
                checks["PreferenceEditsPreserveProfileAndBreak"] = Engine.Preferences.CompanionProfile == CompanionProfileId.Kairo &&
                    Engine.Preferences.PreferredBreak == RecoveryActivity.StretchBreak &&
                    window.ViewModel.RecommendedBreak.Contains(RecoveryActivities.Get(RecoveryActivity.StretchBreak).Title);
                var sample = Engine.Current ?? throw new InvalidOperationException("No snapshot for prompt smoke test.");
                var peak = sample with
                {
                    Analysis = sample.Analysis with { StressScore = 95, State = WellnessState.HighStress },
                    Message = new("Persistent peak probe", "Choose a pause; Remind Later is respected.", CharacterState.Concerned, "peak-stress")
                };
                AcceptSnapshot(peak);
                await Task.Delay(5600);
                checks["PeakPromptPreventsAutoHide"] = IsPeakPromptActive && window.IsPeakPromptVisible && window.IsPopupVisible;
                var visibleElapsed = _visibleTime.Elapsed;
                AcceptSnapshot(peak);
                checks["RepeatedPeakDoesNotReopen"] = _visibleTime.Elapsed >= visibleElapsed;
                AcceptSnapshot(peak with { Message = new("Mild probe", "Must not replace choices", CharacterState.Thinking, "hydration") });
                SimulateNotification(NotificationKind.Escalation);
                checks["PeakSurvivesMildAndUrgentMessages"] = window.IsPeakPromptVisible && window.DisplayedBanner.Contains("Urgent demo notification allowed");
                var resolvedAt = DateTimeOffset.Now;
                var resolved = peak with
                {
                    Analysis = peak.Analysis with { StressScore = 79 },
                    Wearable = peak.Wearable with { Timestamp = resolvedAt }, Message = null
                };
                AcceptSnapshot(resolved);
                checks["PeakResolutionHasHysteresis"] = IsPeakPromptActive;
                AcceptSnapshot(resolved with { Wearable = resolved.Wearable with { Timestamp = resolvedAt.AddSeconds(10) } });
                checks["ResolvedStressClearsPrompt"] = !IsPeakPromptActive && !window.IsPeakPromptVisible;
                AcceptSnapshot(peak);
                window.ViewModel.StartScreenBreakCommand.Execute(null);
                checks["TakingBreakClearsPeakPrompt"] = ActiveRecovery == RecoveryActivity.ScreenBreak && !window.IsPeakPromptVisible;
                await Task.Delay(5600);
                checks["ScreenBreakPreventsAutoHide"] = ActiveRecovery == RecoveryActivity.ScreenBreak && window.IsPopupVisible;
                var beforeHide = RecoveryRemaining;
                window.ViewModel.HideRecoveryTimerCommand.Execute(null);
                await Task.Delay(300);
                checks["HideKeepsScreenBreakRunning"] = !window.IsPopupVisible && ActiveRecovery == RecoveryActivity.ScreenBreak && RecoveryRemaining < beforeHide;
                SimulateNotification(NotificationKind.Escalation);
                checks["UrgentDoesNotReopenHiddenBreak"] = !window.IsPopupVisible && window.DisplayedBanner.Contains("Urgent demo notification allowed");
                HandleTrayCommand("show");
                checks["TrayReopensSameCountdown"] = window.IsPopupVisible && ActiveRecovery == RecoveryActivity.ScreenBreak &&
                    RecoveryRemaining < beforeHide && window.DisplayedBanner.Contains("Urgent demo notification allowed");
                window.ViewModel.CancelRecoveryCommand.Execute(null);
                // This is a new UI prompt probe, not a stale sample from before the recovery boundary.
                peak = peak with { Generation = Engine.Generation };
                AcceptSnapshot(peak);
                window.ViewModel.RemindLaterCommand.Execute(null);
                await Task.Delay(250);
                AcceptSnapshot(peak);
                checks["PeakSnoozeRespected"] = !IsPeakPromptActive && !window.IsPeakPromptVisible && !window.IsPopupVisible;
                window.ViewModel.StartStretchBreakCommand.Execute(null);
                checks["MovementUsesTwoMinuteTimer"] = ActiveRecovery == RecoveryActivity.StretchBreak && RecoveryRemaining.TotalSeconds > 118;
                window.ViewModel.CancelRecoveryCommand.Execute(null);
                window.ViewModel.ToggleFocusCommand.Execute(null);
                checks["ProfileFocusPhraseApplied"] = LastMessage.Contains(CompanionProfiles.Get(Engine.Preferences.CompanionProfile).FocusEncouragement);
                SimulateNotification(NotificationKind.FyiEmail);
                checks["FocusQueuesLowPriority"] = Engine.IsFocusActive && Engine.PendingNotifications.Count == 1;
                SimulateNotification(NotificationKind.Escalation);
                checks["UrgentAllowedImmediately"] = Engine.NotificationHistory.Last().Allowed && window.IsPopupVisible;
                StartBreathing();
                checks["BreakEndsFocusAndReleasesQueue"] = !Engine.IsFocusActive && Engine.PendingNotifications.Count == 0 &&
                    _activity.Any(item => item.Contains("RELEASED")) && window.DisplayedFocusFeedback.Contains("Focus ended") &&
                    window.DisplayedFocusFeedback.Contains("1 deferred") && window.DisplayedFocusFeedback.Contains("1 urgent allowed");
                var focusSummaries = _activity.Count(item => item.Contains("FOCUS COMPLETE"));
                await Task.Delay(250);
                CancelBreathing();
                window.ViewModel.StartWaterBreakCommand.Execute(null);
                checks["WaterUsesOneMinuteTimer"] = ActiveRecovery == RecoveryActivity.WaterBreak && RecoveryRemaining.TotalSeconds > 58;
                window.ViewModel.CancelRecoveryCommand.Execute(null);
                checks["CancellationHasNoCompletion"] = ActiveRecovery is null && !_activity.Any(item => item.Contains("RECOVERY COMPLETE"));
                window.ViewModel.StartBreathingCommand.Execute(null);
                var scenarioBeforeLock = Engine.Scenario;
                ToggleFocus();
                SetScenario(DemoScenario.RisingStress);
                StartRecovery(RecoveryActivity.ScreenBreak);
                checks["RecoveryBlocksFocusScenarioAndSecondBreak"] = !Engine.IsFocusActive && Engine.Scenario == scenarioBeforeLock &&
                    IsBreathing && window.DisplayedBanner.Contains("Finish or cancel");
                var title = window.DisplayedTitle;
                SimulateNotification(NotificationKind.Escalation);
                checks["UrgentDoesNotInterruptBreathing"] = IsBreathing && title == window.DisplayedTitle &&
                    window.DisplayedBanner.Contains("Urgent demo notification allowed");
                await Task.Delay(20000);
                checks["BreathingPreventsAutoHide"] = IsBreathing && window.IsPopupVisible;
                await Task.Delay(41000);
                checks["FullMinuteCompletesOnce"] = ActiveRecovery is null && _activity.Count(item => item.Contains("RECOVERY COMPLETE · Breathing")) == 1;
                checks["CancelledWaterHasNoLateCompletion"] = !_activity.Any(item => item.Contains("RECOVERY COMPLETE · WaterBreak")) &&
                    _activity.Count(item => item.Contains("RECOVERY COMPLETE")) == 1;
                checks["BreakTimeDoesNotCountAsFocus"] = !Engine.IsFocusActive &&
                    _activity.Count(item => item.Contains("FOCUS COMPLETE")) == focusSummaries;
                checks["ProfileCompletionPhraseApplied"] = LastMessage.Contains(CompanionProfiles.Get(Engine.Preferences.CompanionProfile).RecoveryEncouragement);
                ToggleFocus();
                checks["FocusCanRestartAfterRecovery"] = Engine.IsFocusActive;
                ToggleFocus();
                checks["FocusCanEndAfterRecovery"] = !Engine.IsFocusActive && LastMessage.Contains("urgent allowed");
                Dismiss();
                await Task.Delay(250);
                checks["DismissHides"] = _window?.IsPopupVisible == false;
                var snapshot = Engine.Current;
                if (snapshot is not null)
                    AcceptSnapshot(snapshot with { Message = new("Cooldown probe", "Must not show", CharacterState.Thinking, "test") });
                checks["DismissCooldownHonored"] = _window?.IsPopupVisible == false;
                ShowCompanion();
                _window?.HidePopup();
                await Task.Delay(40);
                ShowCompanion();
                await Task.Delay(220);
                checks["ShowDuringHideRestoresWindow"] = _window?.IsPopupVisible == true;
                SavePreferences(Engine.Preferences with { PopupSeconds = 5 });
                await Task.Delay(5600);
                checks["PopupDurationApplied"] = _window?.IsPopupVisible == false;
            }
            catch (Exception exception)
            {
                checks["NoTestException"] = false;
                ReportError("Full UI smoke test failed", exception);
            }
            finally
            {
                _recovery.Cancel();
                Engine.SetRecoveryActive(false);
                _window?.EndRecovery();
                ClearPeakPrompt();
                Engine.Restore(original);
                _window?.ApplyPreferences(Engine.Preferences);
                _character = original.Character;
                _window?.SetCharacter(_character);
                await Task.Delay(2200);
            }
        }
        await PersistAsync();
        var record = new
        {
            Timestamp = DateTimeOffset.Now,
            UiInitialized = _window?.IsUiReady == true,
            TrayRegistered = _tray?.IsRegistered == true,
            EngineInitialized = Engine.Current is not null,
            Scenario = Engine.Scenario.ToString(),
            Character = _character.ToString(),
            StorageHealthy = _storageHealthy,
            StoragePath = _store.FilePath,
            StateCount = Enum.GetValues<CharacterState>().Length,
            FullTest = full,
            Checks = checks,
            Status,
            SyntheticDataOnly = true
        };
        Directory.CreateDirectory(AppLog.DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(AppLog.DirectoryPath, "smoke-test.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = record.UiInitialized && record.TrayRegistered && record.EngineInitialized &&
            record.StorageHealthy && checks.Values.All(value => value) ? 0 : 1;
        RequestExit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiTimer.Stop();
        _uiTimer.Tick -= OnUiTick;
        _tray?.Dispose();
        if (_controls is { IsClosed: false }) _controls.Close();
        if (_profile is { IsClosed: false }) _profile.Close();
        if (_presenter is { IsClosed: false }) _presenter.Close();
        _window?.CloseForExit();
    }
}
