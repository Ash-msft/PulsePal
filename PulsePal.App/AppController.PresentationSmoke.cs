using System.Diagnostics;
using System.Text.Json;
using PulsePal.App.Services;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.App;

public sealed partial class AppController
{
    public async Task RunPresentationSmokeTestAsync()
    {
        var checks = new Dictionary<string, bool>();
        var errors = new List<string>();
        var cycles = new List<object>();
        var original = Engine.ExportState(DateTimeOffset.Now, _character);
        var observed = new List<DemoSnapshot>();
        var steps = new HashSet<TourStep>();
        var sawPeak = false;
        var sampleKeys = new HashSet<DemoSnapshot>();
        var controllerErrors = new HashSet<string>();

        void Observe()
        {
            foreach (var entry in _activity.Where(item => item.Contains("ERROR", StringComparison.Ordinal)))
                controllerErrors.Add(entry);
            steps.Add(Tour.Step);
            if (Engine.Current is not { } sample || !Engine.IsCurrent(sample)) return;
            // Completion can apply a real synthetic benefit to the current sample without changing its timestamp.
            if (sampleKeys.Add(sample)) observed.Add(sample);
            sawPeak |= Engine.PeakStressEstablished && sample.Analysis.StressScore >= 80;
        }

        void Check(string name, bool passed) => checks[name] = passed;

        async Task WaitFor(string name, Func<bool> ready, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                Observe();
                if (ready()) { Check(name, true); return; }
                await Task.Delay(100);
            }
            Check(name, false);
            throw new TimeoutException($"{name}: timed out after {timeout.TotalSeconds:0}s; step={Tour.Step}, " +
                $"stress={Engine.Current?.Analysis.StressScore}, recovery={ActiveRecovery}, status={Status}");
        }

        static string StoryState(SessionStory story) => JsonSerializer.Serialize(story);
        string[] NotificationEvents() => Engine.NotificationHistory
            .Select(item => $"{item.Notification.Id}|{item.Notification.Kind}|{item.Notification.Title}|{item.Allowed}").ToArray();

        void RejectStale(string name, DemoSnapshot stale)
        {
            var message = LastMessage;
            var story = StoryState(SessionStory);
            var activity = _activity.ToArray();
            var updates = 0;
            void CountUpdate() => updates++;
            Updated += CountUpdate;
            try
            {
                AcceptSnapshot(stale with
                {
                    Message = new("Stale presentation sample", "This must never be displayed.",
                        CharacterState.Concerned, "peak-stress")
                });
                Check(name, !Engine.IsCurrent(stale) && updates == 0 && LastMessage == message &&
                    StoryState(SessionStory) == story && activity.SequenceEqual(_activity));
            }
            finally { Updated -= CountUpdate; }
        }

        Updated += Observe;
        try
        {
            Check("UiInitialized", _window?.IsUiReady == true);
            Check("StartupStorageHealthy", _storageHealthy);
            Check("IsolatedState", !string.Equals(_store.FilePath,
                new JsonStateStore().FilePath, StringComparison.OrdinalIgnoreCase));
            ShowPresenter();
            Check("PresenterOpenedWithoutStartingTour", Tour.Step == TourStep.Idle);

            foreach (var profile in CompanionProfiles.All)
            {
                var changed = Engine.Preferences.CompanionProfile != profile.Id;
                SavePreferences(Engine.Preferences with
                {
                    CompanionProfile = profile.Id, PreferredBreak = RecoveryActivity.ScreenBreak
                });
                if (changed)
                    Check("ProfileGreeting_" + profile.Id, LastMessage.Contains(profile.Greeting, StringComparison.Ordinal));
                Check("ProfileIdentity_" + profile.Id, _window?.AppliedProfile == profile.Id &&
                    _window.DisplayedIdentity.Contains(profile.Name, StringComparison.Ordinal) &&
                    _window.DisplayedIdentity.Contains(profile.Personality, StringComparison.Ordinal));
                MeetCompanion();
                var greeting = LastMessage;
                Check("GreetingReplay_" + profile.Id, greeting.Contains(profile.Greeting, StringComparison.Ordinal));
                MeetCompanion();
                Check("GreetingReplayRepeat_" + profile.Id, LastMessage == greeting);
            }

            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var prefix = $"Cycle{cycle}_";
                ResetPresentation();
                SavePreferences(Engine.Preferences with
                {
                    PreferredBreak = cycle == 1 ? RecoveryActivity.WaterBreak : RecoveryActivity.ScreenBreak
                });
                AcceleratedDemo = true;
                StartPresentation();
                observed.Clear();
                sampleKeys.Clear();
                steps.Clear();
                sawPeak = false;
                Observe();
                var tourWatch = Stopwatch.StartNew();
                TimeSpan Budget() => TimeSpan.FromSeconds(Math.Max(0, 150 - tourWatch.Elapsed.TotalSeconds));
                Check(prefix + "StartsAtBriefing", Tour.Step == TourStep.Briefing);

                if (cycle == 1)
                {
                    NextPresentation();
                    Check(prefix + "ManualFocus", Tour.Step == TourStep.Focus && Engine.IsFocusActive);
                    StartRecovery(RecoveryActivity.ScreenBreak);
                    HandleTrayCommand("water-break");
                    Check(prefix + "EarlyRecoveryCannotBypassGuide", ActiveRecovery is null && Engine.IsFocusActive &&
                        Tour.Step == TourStep.Focus && !_window!.ViewModel.StartScreenBreakCommand.CanExecute(null));
                    await WaitFor(prefix + "FocusSample", () => Engine.Current is { } sample &&
                        sample.IsFocusActive && Engine.IsCurrent(sample), TimeSpan.FromSeconds(6));
                    var stale = Engine.Current!;
                    PausePresentation();
                    var pausedStory = StoryState(SessionStory);
                    var pausedHistory = Engine.History.ToArray();
                    var pausedEvents = NotificationEvents();
                    var pausedClock = Engine.Clock.GetUtcNow();
                    var pausedStepTime = Tour.TimeInStep;
                    RejectStale(prefix + "PauseRejectsQueuedSnapshot", stale);
                    var pausedMessage = LastMessage;
                    SavePreferences(Engine.Preferences);
                    Check(prefix + "UnchangedPreferencesDoNotIntroduce", LastMessage == pausedMessage);
                    await Task.Delay(2500);
                    Check(prefix + "PauseFreezesFocusAndSampling", IsPresentationPaused &&
                        Tour.Step == TourStep.Focus && Tour.TimeInStep == pausedStepTime &&
                        Engine.Clock.GetUtcNow() == pausedClock && StoryState(SessionStory) == pausedStory &&
                        pausedHistory.SequenceEqual(Engine.History) && pausedEvents.SequenceEqual(NotificationEvents()));
                    var resumedFocus = SessionStory.FocusTime;
                    ResumePresentation();
                    await Task.Delay(500);
                    Check(prefix + "ResumeExcludesPausedWallTime", !IsPresentationPaused &&
                        SessionStory.FocusTime > resumedFocus &&
                        SessionStory.FocusTime - resumedFocus < TimeSpan.FromSeconds(2));
                    NextPresentation();
                    Check(prefix + "ManualInterruptions", Tour.Step == TourStep.Interruptions);
                    NextPresentation();
                    Check(prefix + "ManualMeeting", Tour.Step == TourStep.MeetingStress);
                    var events = NotificationEvents();
                    NextPresentation();
                    NextPresentation();
                    Check(prefix + "NextWaitsForRealPeak", Tour.Step == TourStep.MeetingStress &&
                        ActiveRecovery is null && SessionStory.CompletedBreaks.Count == 0 && LastComparison is null);
                    Check(prefix + "NextDoesNotRedeliverNotifications", events.SequenceEqual(NotificationEvents()));
                }

                await WaitFor(prefix + "ActualPeakAndRecoveryChoice", () => Tour.Step == TourStep.Recovery, Budget());
                Observe();
                Check(prefix + "ObservedActualHighStressPeak", sawPeak);
                Check(prefix + "RecoveryHasChoosingWindow", ActiveRecovery is null);
                if (cycle == 1)
                {
                    SelectedPresentationBreak = RecoveryActivity.ScreenBreak;
                    StartSelectedPresentationRecovery();
                }
                await WaitFor(prefix + "RecoveryStarted", () => ActiveRecovery is not null, Budget());
                Check(prefix + (cycle == 1 ? "SelectedRecoveryUsed" : "PreferredRecoveryAutoStarted"),
                    ActiveRecovery == RecoveryActivity.ScreenBreak &&
                    Engine.Preferences.PreferredBreak == (cycle == 1 ? RecoveryActivity.WaterBreak : RecoveryActivity.ScreenBreak));
                Check(prefix + "Actual15Represented300", _recovery.IsAccelerated &&
                    _recovery.ActualDuration == TimeSpan.FromSeconds(15) &&
                    _recovery.RepresentedDuration == TimeSpan.FromSeconds(300));
                Check(prefix + "AcceleratedCountdownLabel",
                    (_window?.DisplayedTitle + " " + LastMessage).Contains("accelerated", StringComparison.OrdinalIgnoreCase));
                var recoveryWatch = Stopwatch.StartNew();
                var alreadyElapsed = _recovery.Elapsed;
                var pausedDuration = TimeSpan.Zero;
                var beforeCompletion = SessionStory.CompletedBreaks.Count;
                var eventsDuringRecovery = NotificationEvents();
                var sampleBeforeNext = Engine.Current;
                NextPresentation();
                NextPresentation();
                Check(prefix + "NextCannotCompleteRecoveryOrApplyBenefit", Tour.Step == TourStep.Recovery &&
                    ActiveRecovery is not null && LastComparison is null &&
                    SessionStory.CompletedBreaks.Count == beforeCompletion && Engine.Current == sampleBeforeNext);
                Check(prefix + "RecoveryNextDoesNotRedeliver", eventsDuringRecovery.SequenceEqual(NotificationEvents()));

                if (cycle == 1)
                {
                    PausePresentation();
                    var pauseWatch = Stopwatch.StartNew();
                    var remaining = RecoveryRemaining;
                    var pausedStory = StoryState(SessionStory);
                    var historyCount = Engine.History.Count;
                    await Task.Delay(2500);
                    Check(prefix + "PauseFreezesRecovery", IsPresentationPaused && ActiveRecovery == RecoveryActivity.ScreenBreak &&
                        RecoveryRemaining == remaining && StoryState(SessionStory) == pausedStory &&
                        Engine.History.Count == historyCount);
                    pausedDuration = pauseWatch.Elapsed;
                    ResumePresentation();
                    Check(prefix + "RecoveryResumeExcludesPause", RecoveryRemaining > remaining - TimeSpan.FromMilliseconds(500));
                }

                await WaitFor(prefix + "FullRecoveryCompleted", () => SessionStory.CompletedBreaks.Count > beforeCompletion, Budget());
                var completionElapsed = alreadyElapsed + recoveryWatch.Elapsed - pausedDuration;
                Check(prefix + "NoEarlyCompletion", completionElapsed >= TimeSpan.FromMilliseconds(14750));
                await WaitFor(prefix + "ActualComparisonAvailable", () => LastComparison is not null, Budget());
                var comparison = LastComparison!;
                Observe();
                var beforeIndex = observed.FindIndex(sample =>
                    comparison.StressBefore == sample.Analysis.StressScore &&
                    comparison.RecoveryBefore == sample.Wearable.RecoveryScore &&
                    comparison.HeartRateBefore == sample.Wearable.HeartRate);
                Check(prefix + "BeforeUsesActualSample", beforeIndex >= 0 && comparison.StressBefore >= 80);
                Check(prefix + "AfterUsesActualSample", beforeIndex >= 0 && observed.Skip(beforeIndex + 1).Any(sample =>
                    comparison.StressAfter == sample.Analysis.StressScore &&
                    comparison.RecoveryAfter == sample.Wearable.RecoveryScore &&
                    comparison.HeartRateAfter == sample.Wearable.HeartRate));
                Check(prefix + "ComparisonHasSyntheticAcceleratedLabels",
                    comparison.IsSynthetic && comparison.IsAccelerated &&
                    comparison.Description.Contains("synthetic", StringComparison.OrdinalIgnoreCase) &&
                    comparison.Description.Contains("accelerated", StringComparison.OrdinalIgnoreCase));
                if (cycle == 1)
                {
                    await WaitFor(prefix + "ComparisonStep", () => Tour.Step == TourStep.Comparison, Budget());
                    NextPresentation();
                }
                await WaitFor(prefix + "SummaryReached", () => Tour.Step == TourStep.Summary, Budget());
                var story = SessionStory;
                Check(prefix + "SummaryExactCounts", story.CompletedFocusSessions == 1 &&
                    story.DeferredNotifications == 2 && story.UrgentNotifications == 3 &&
                    story.CompletedBreaks.Count == 1 && story.CancelledBreaks == 0 && !story.IsFocusActive);
                var decisions = Engine.NotificationHistory.ToArray();
                var deliveries = decisions.GroupBy(item => item.Notification.Id).Select(group => group.First()).ToArray();
                var releases = decisions.Where(item => item.Reason == "Released after focus session ended.").ToArray();
                var expectedKinds = new[] { NotificationKind.FyiEmail, NotificationKind.GroupMessage,
                    NotificationKind.ManagerMessage, NotificationKind.MeetingReminder, NotificationKind.Escalation };
                Check(prefix + "NotificationKindsTitlesAndCounts", decisions.Length == 7 && deliveries.Length == 5 &&
                    expectedKinds.All(kind => deliveries.Count(item => item.Notification.Kind == kind) == 1) &&
                    deliveries.Select(item => item.Notification.Title).Distinct().Count() == 5 &&
                    deliveries.All(item => item.Allowed ==
                        (item.Notification.Kind is NotificationKind.ManagerMessage or NotificationKind.MeetingReminder or NotificationKind.Escalation)));
                Check(prefix + "DeferredNotificationsReleasedExactlyOnce", releases.Length == 2 &&
                    releases.All(item => item.Allowed && deliveries.Any(first => !first.Allowed &&
                        first.Notification.Id == item.Notification.Id)) && releases.Select(item => item.Notification.Id).Distinct().Count() == 2);
                Check(prefix + "CompletionMetadata", story.CompletedBreaks.Count == 1 &&
                    story.CompletedBreaks[0].ActualElapsed == TimeSpan.FromSeconds(15) &&
                    story.CompletedBreaks[0].RepresentedDuration == TimeSpan.FromSeconds(300) &&
                    story.CompletedBreaks[0].IsAccelerated &&
                    story.CompletedBreaks[0].Comparison == comparison &&
                    story.Description.Contains("00:00:15", StringComparison.Ordinal) &&
                    story.Description.Contains("00:05:00", StringComparison.Ordinal));
                var summaryBefore = StoryState(story);
                var notificationsBefore = NotificationEvents();
                ShowSessionStory();
                ShowSessionStory();
                await Task.Delay(2200);
                Check(prefix + "SummaryOpeningIsReadOnlyAndCompletionOnce", StoryState(SessionStory) == summaryBefore &&
                    notificationsBefore.SequenceEqual(NotificationEvents()) && SessionStory.CompletedBreaks.Count == 1);
                Check(prefix + "EveryTourPhaseVisited", Enum.GetValues<TourStep>()
                    .Where(step => step != TourStep.Idle).All(steps.Contains));
                cycles.Add(new
                {
                    Cycle = cycle, Automatic = cycle == 2, ElapsedSeconds = tourWatch.Elapsed.TotalSeconds,
                    PeakObserved = sawPeak, Story = story, Comparison = comparison,
                    Notifications = decisions, Steps = steps.Select(step => step.ToString()).ToArray(),
                    Samples = observed.Select(sample => new
                    {
                        sample.Wearable.Timestamp, sample.Generation, sample.Scenario,
                        sample.Analysis.StressScore, sample.Wearable.HeartRate, sample.Wearable.RecoveryScore
                    }).ToArray()
                });
            }

            ResetPresentation();
            AcceleratedDemo = true;
            StartRecovery(RecoveryActivity.ScreenBreak);
            Check("CancellationPathStarted", ActiveRecovery == RecoveryActivity.ScreenBreak);
            await WaitFor("CancellationHasRealSample", () => Engine.Current is { } sample &&
                Engine.IsCurrent(sample), TimeSpan.FromSeconds(6));
            CancelRecovery();
            Check("CancellationHasNoComparisonOrCompletion", ActiveRecovery is null && LastComparison is null &&
                SessionStory.CompletedBreaks.Count == 0 && SessionStory.CancelledBreaks == 1);
            var cancelledSample = Engine.Current;
            await WaitFor("SamplingContinuesAfterCancellation", () => Engine.Current is { } sample &&
                sample.Wearable.Timestamp != cancelledSample?.Wearable.Timestamp, TimeSpan.FromSeconds(6));
            Check("CancelledRecoveryStaysUncompleted", ActiveRecovery is null && LastComparison is null &&
                SessionStory.CompletedBreaks.Count == 0 && SessionStory.CancelledBreaks == 1);
            var preferences = Engine.Preferences;
            var staleBeforeReset = Engine.Current;
            for (var reset = 1; reset <= 2; reset++)
            {
                ResetPresentation();
                var story = SessionStory;
                Check("CleanReset" + reset, Tour.Step == TourStep.Idle && !IsPresentationPaused &&
                    !Engine.IsFocusActive && ActiveRecovery is null && LastComparison is null &&
                    Engine.History.Count == 0 && Engine.NotificationHistory.Count == 0 &&
                    Engine.PendingNotifications.Count == 0 && story.FocusTime == TimeSpan.Zero &&
                    story.CompletedFocusSessions == 0 && story.DeferredNotifications == 0 &&
                    story.UrgentNotifications == 0 && story.CompletedBreaks.Count == 0 && story.CancelledBreaks == 0 &&
                    Engine.Preferences == preferences);
            }
            if (staleBeforeReset is not null) RejectStale("ResetRejectsQueuedSnapshot", staleBeforeReset);
            else Check("ResetRejectsQueuedSnapshot", false);
            Observe();
            Check("NoControllerErrors", controllerErrors.Count == 0);
        }
        catch (Exception exception)
        {
            Check("NoTestException", false);
            errors.Add(exception.ToString());
            AppLog.Write("Presentation smoke failed", exception);
        }
        finally
        {
            try
            {
                StopPresentation();
                ResetPresentation();
                AcceleratedDemo = false;
                await WaitFor("OrdinarySamplingResumes", () => Engine.Current is { } sample &&
                    Engine.IsCurrent(sample) && !Engine.Clock.IsPresentation, TimeSpan.FromSeconds(6));
            }
            catch (Exception exception)
            {
                Check("LifecycleCleanupSucceeded", false);
                errors.Add(exception.ToString());
                AppLog.Write("Presentation smoke lifecycle cleanup failed", exception);
            }
            finally
            {
                Updated -= Observe;
                _uiTimer.Stop();
                AcceleratedDemo = false;
                _recovery.Cancel();
                Engine.SetRecoveryActive(false);
                _window?.EndRecovery();
                try
                {
                    Engine.Restore(original);
                    _character = original.Character;
                    _window?.ApplyPreferences(Engine.Preferences);
                    _window?.SetCharacter(_character);
                    await PersistAsync(true);
                    var restored = await _store.LoadAsync();
                    Check("OriginalOrdinaryStateRestored", restored.Preferences == original.Preferences &&
                        restored.Scenario == original.Scenario && restored.Character == original.Character &&
                        !AcceleratedDemo && !Engine.Clock.IsPresentation);
                    Check("StorageHealthy", _storageHealthy);
                }
                catch (Exception exception)
                {
                    Check("OriginalOrdinaryStateRestored", false);
                    errors.Add(exception.ToString());
                    AppLog.Write("Presentation smoke state restoration failed", exception);
                }
            }
        }

        Environment.ExitCode = checks.Values.All(value => value) ? 0 : 1;
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(AppLog.DirectoryPath, "smoke-test-presentation.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = DateTimeOffset.Now, PresentationTest = true, FullTest = false,
                    SyntheticDataOnly = true, AcceleratedRecoveryOnly = true, StressClockAccelerated = false,
                    StateIsolated = true, StoragePath = _store.FilePath, LogDirectory = AppLog.DirectoryPath,
                    Flags = Environment.GetCommandLineArgs().Where(argument => argument.StartsWith("--", StringComparison.Ordinal)).ToArray(),
                    Passed = Environment.ExitCode == 0, Checks = checks, Errors = errors,
                    ControllerErrors = controllerErrors, Cycles = cycles
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { RequestExit(); }
    }
}
