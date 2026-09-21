using System.Text.Json;
using System.Threading.Channels;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class PresentationEngineTests
{
    private static readonly DateTimeOffset PresentationStart = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly MockWorkContextProvider Contexts = new();
    private static readonly UserPreferences Preferences = new()
    {
        DisplayName = "आशा — café 🌿",
        HydrationReminders = false,
        PopupSeconds = 23,
        PreferredBreak = RecoveryActivity.StretchBreak
    };

    [Fact]
    public void ConstructorExposesInjectedClockAndStableLedgerAndOnlySyntheticSupportsPresentation()
    {
        var clock = new SessionClock(new ManualTimeProvider(PresentationStart));
        var synthetic = new DemoEngine(new SyntheticWearableProvider(7), clock);
        Assert.Same(clock, synthetic.Clock);
        Assert.Same(synthetic.Ledger, synthetic.Ledger);
        Assert.True(synthetic.SupportsPresentation);
        Assert.False(synthetic.IsPresentationPaused);
        Assert.False(clock.IsPresentation);
        Assert.False(new DemoEngine(new ReplayWearableProvider(new[] { Healthy(PresentationStart) })).SupportsPresentation);
        Assert.False(new DemoEngine(new ReportedProvider(Healthy)).SupportsPresentation);
    }

    [Fact]
    public async Task TwoCompleteStartsReproduceTelemetryAnalysisMessagesAndSessionTotals()
    {
        var source = new ManualTimeProvider(PresentationStart.AddMonths(8));
        var engine = Synthetic(source);
        engine.Preferences = Preferences;

        var first = await RunPresentation(engine, source);
        var firstHistory = engine.History.ToArray();
        var firstStory = JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow()));
        long firstGeneration = engine.Generation;
        source.Advance(TimeSpan.FromDays(3));
        var second = await RunPresentation(engine, source);

        Assert.True(engine.Generation > firstGeneration);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            AssertEquivalent(first[i], second[i]);
        Assert.Equal(firstHistory, engine.History.ToArray());
        Assert.Equal(firstStory, JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow())));
        Assert.Equal(Preferences, engine.Preferences);
        Assert.Contains(first, snapshot => snapshot.Message?.Category == "peak-stress");
        Assert.Contains(first, snapshot => snapshot.Analysis.State == WellnessState.Recovering);
        Assert.False(engine.IsCurrent(first[^1]));
        Assert.True(engine.IsCurrent(second[^1]));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("reset")]
    [InlineData("restore")]
    public async Task FullResetClearsRuntimeSleepAnalyzerCooldownSnoozeRecoveryHistoryAndPendingMessages(string transition)
    {
        var source = new ManualTimeProvider(PresentationStart);
        var provider = new SyntheticWearableProvider(7);
        var engine = new DemoEngine(provider, new SessionClock(source)) { Preferences = Preferences };
        if (transition != "restore") engine.StartPresentation();
        await DirtyRuntime(engine, source);
        var old = engine.Current!;
        long generation = engine.Generation;
        engine.PausePresentation();
        source.SetUtcNow(PresentationStart);

        ApplyReset(engine, transition);

        Assert.True(engine.Generation > generation);
        Assert.False(engine.IsCurrent(old));
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        Assert.Empty(engine.PendingNotifications);
        Assert.Empty(engine.NotificationHistory);
        Assert.False(engine.IsFocusActive);
        Assert.False(engine.PeakStressEstablished);
        Assert.False(engine.IsPresentationPaused);
        Assert.Equal(transition == "start", engine.Clock.IsPresentation);
        Assert.Equal(DemoScenario.HealthyDay, engine.Scenario);
        Assert.Equal(DemoScenario.HealthyDay, provider.Scenario);
        Assert.Equal(Preferences, engine.Preferences);
        AssertEmptyLedger(engine);

        var freshSource = new ManualTimeProvider(PresentationStart);
        var fresh = Synthetic(freshSource);
        fresh.Preferences = Preferences;
        if (transition == "start") fresh.StartPresentation();
        var reset = await Sample(engine);
        AssertEquivalent(await Sample(fresh), reset);
        Assert.Equal(465, reset.Wearable.TotalSleepMinutes);
        Assert.Equal(0, reset.Wearable.Steps);
        Assert.Equal(0, reset.Wearable.ActiveMinutes);
        Assert.Equal(0d, reset.Wearable.Calories);
        Assert.Null(reset.Message);
        engine.SetScenario(DemoScenario.RisingStress);
        fresh.SetScenario(DemoScenario.RisingStress);
        bool prompted = false;
        for (int second = 0; second <= 100; second += 2)
        {
            if (second > 0)
            {
                source.Advance(TimeSpan.FromSeconds(2));
                freshSource.Advance(TimeSpan.FromSeconds(2));
            }
            var actual = await Sample(engine);
            AssertEquivalent(await Sample(fresh), actual);
            prompted |= actual.Message?.Category == "peak-stress";
        }
        Assert.True(prompted);
    }

    [Theory]
    [InlineData(DemoScenario.HealthyDay)]
    [InlineData(DemoScenario.DeepFocusSession)]
    [InlineData(DemoScenario.RisingStress)]
    [InlineData(DemoScenario.MeetingOverload)]
    [InlineData(DemoScenario.RecoveryAfterBreak)]
    [InlineData(DemoScenario.PoorSleepDay)]
    public async Task ProviderResetMatchesFreshSeedIncludingCountersSleepContextAndBreathing(DemoScenario scenario)
    {
        var used = new SyntheticWearableProvider(123);
        used.SetScenario(DemoScenario.PoorSleepDay);
        await used.GetSampleAsync(Contexts.GetContext(used.Scenario), PresentationStart);
        used.SetScenario(DemoScenario.RecoveryAfterBreak);
        await used.GetSampleAsync(Contexts.GetContext(used.Scenario), PresentationStart.AddSeconds(2));
        var walking = await used.GetSampleAsync(Contexts.GetContext(used.Scenario), PresentationStart.AddMinutes(2));
        Assert.True(walking.Steps > 0);
        Assert.True(walking.Calories > 0);
        used.SetScenario(DemoScenario.MeetingOverload);
        await used.GetSampleAsync(Contexts.GetContext(used.Scenario), PresentationStart.AddMinutes(3));
        used.CompleteBreathing(PresentationStart.AddMinutes(3));

        used.Reset(scenario);
        var fresh = new SyntheticWearableProvider(123);
        fresh.SetScenario(scenario);
        Assert.Equal(scenario, used.Scenario);
        foreach (int second in new[] { 0, 2, 20, 60, 120, 180 })
        {
            var now = PresentationStart.AddSeconds(second);
            var context = Contexts.GetContext(scenario);
            Assert.Equal(await fresh.GetSampleAsync(context, now), await used.GetSampleAsync(context, now));
        }

        used.Reset();
        fresh = new SyntheticWearableProvider(123);
        Assert.Equal(DemoScenario.HealthyDay, used.Scenario);
        // Breathing before the first read also exercises the reset provider's remembered context.
        used.CompleteBreathing(PresentationStart);
        fresh.CompleteBreathing(PresentationStart);
        foreach (int second in new[] { 0, 2, 90, 92, 180 })
        {
            var now = PresentationStart.AddSeconds(second);
            Assert.Equal(await fresh.GetSampleAsync(new WorkContext(), now),
                await used.GetSampleAsync(new WorkContext(), now));
        }
    }

    [Theory]
    [InlineData(DemoScenario.HealthyDay)]
    [InlineData(DemoScenario.PoorSleepDay)]
    [InlineData(DemoScenario.RecoveryAfterBreak)]
    public async Task RestoreResetsSyntheticToSavedScenarioAndClearsSessionLedger(DemoScenario scenario)
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        await DirtyRuntime(engine, source);
        source.SetUtcNow(PresentationStart);
        var savedHistory = new[]
        {
            new DemoHistoryEntry(PresentationStart.AddMinutes(-1), scenario, WellnessState.Balanced, 30, 70)
        };
        var saved = new PersistedAppState { Scenario = scenario, Preferences = Preferences, History = savedHistory };
        long generation = engine.Generation;
        engine.Restore(saved);

        Assert.True(engine.Generation > generation);
        Assert.Equal(scenario, engine.Scenario);
        Assert.Equal(savedHistory, engine.History.ToArray());
        Assert.Equal(Preferences, engine.Preferences);
        AssertEmptyLedger(engine);
        var fresh = Synthetic(new ManualTimeProvider(PresentationStart));
        fresh.Restore(saved);
        AssertEquivalent(await Sample(fresh), await Sample(engine));
    }

    [Theory]
    [InlineData("start", false)]
    [InlineData("start", true)]
    [InlineData("reset", false)]
    [InlineData("reset", true)]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    public async Task ResetDiscardsFocusSummaryEvenBeforeAnySnapshotExists(string transition, bool hasSnapshot)
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        if (hasSnapshot) await Sample(engine);
        engine.StartFocus(PresentationStart);
        engine.EndFocus(PresentationStart.AddSeconds(10));
        if (hasSnapshot) Assert.Equal("focus-summary", engine.Current!.Message?.Category);

        ApplyReset(engine, transition);

        Assert.Null(engine.Current);
        Assert.Null((await Sample(engine)).Message);
        AssertEmptyLedger(engine);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnsupportedStartAndResetThrowBeforeChangingAnyObservableState(bool replay, bool reset)
    {
        var source = new ManualTimeProvider(PresentationStart);
        IWearableProvider provider = replay
            ? new ReplayWearableProvider(new[] { Healthy(PresentationStart) }, loop: true)
            : new ReportedProvider(Healthy);
        var preferences = Preferences with { HydrationReminders = true };
        var engine = new DemoEngine(provider, new SessionClock(source)) { Preferences = preferences };
        engine.SetScenario(DemoScenario.DeepFocusSession);
        engine.StartFocus(PresentationStart);
        engine.SimulateNotification(NotificationKind.FyiEmail, PresentationStart, "Keep this — संदेश");
        await Sample(engine);
        engine.Snooze(TimeSpan.FromHours(1), PresentationStart);
        engine.Ledger.CancelRecovery();
        var before = engine.Current!;
        var history = engine.History.ToArray();
        var notifications = engine.NotificationHistory.ToArray();
        var pending = engine.PendingNotifications.ToArray();
        string story = JsonSerializer.Serialize(engine.Ledger.Snapshot(PresentationStart));
        long generation = engine.Generation;
        long timestamp = engine.Clock.GetTimestamp();

        Assert.Throws<NotSupportedException>(() =>
        {
            if (reset) engine.ResetPresentation();
            else engine.StartPresentation();
        });

        Assert.Same(before, engine.Current);
        Assert.True(engine.IsCurrent(before));
        Assert.Equal(generation, engine.Generation);
        Assert.Equal(DemoScenario.DeepFocusSession, engine.Scenario);
        Assert.Equal(preferences, engine.Preferences);
        Assert.True(engine.IsFocusActive);
        Assert.False(engine.Clock.IsPresentation);
        Assert.False(engine.IsPresentationPaused);
        Assert.Equal(PresentationStart, engine.Clock.GetUtcNow());
        Assert.Equal(timestamp, engine.Clock.GetTimestamp());
        Assert.Equal(history, engine.History.ToArray());
        Assert.Equal(notifications, engine.NotificationHistory.ToArray());
        Assert.Equal(pending, engine.PendingNotifications.ToArray());
        Assert.Equal(story, JsonSerializer.Serialize(engine.Ledger.Snapshot(PresentationStart)));
        engine.EndFocus(PresentationStart);
        await Sample(engine); // Consume the focus summary before checking the retained snooze.
        engine.SetScenario(DemoScenario.PoorSleepDay);
        source.Advance(TimeSpan.FromMinutes(30));
        Assert.Null((await Sample(engine)).Message);
    }

    [Fact]
    public async Task GenerationsInvalidateSnapshotsAndPauseResumeOnlyChangeOnEffectiveTransitions()
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        var original = await Sample(engine);
        Assert.True(engine.IsCurrent(original));
        Assert.False(engine.IsCurrent(original with { Generation = original.Generation - 1 }));
        engine.StartPresentation();
        Assert.False(engine.IsCurrent(original));
        var running = await Sample(engine);
        Assert.Equal(engine.Generation, running.Generation);
        long generation = engine.Generation;
        engine.ResumePresentation();
        Assert.Equal(generation, engine.Generation);
        engine.PausePresentation();
        Assert.True(engine.Generation > generation);
        Assert.False(engine.IsCurrent(running));
        generation = engine.Generation;
        engine.PausePresentation();
        Assert.Equal(generation, engine.Generation);
        engine.ResumePresentation();
        Assert.True(engine.Generation > generation);
        generation = engine.Generation;
        engine.ResumePresentation();
        Assert.Equal(generation, engine.Generation);
        var resumed = await Sample(engine);
        Assert.Equal(generation, resumed.Generation);
        Assert.True(engine.IsCurrent(resumed));
        engine.ResetPresentation();
        Assert.True(engine.Generation > generation);
        Assert.False(engine.IsCurrent(resumed));
    }

    [Fact]
    public async Task ScenarioChangesInvalidateDispatchedPeaksButUnchangedOrInvalidScenariosDoNot()
    {
        var engine = new DemoEngine(new ReportedProvider(High));
        engine.SetScenario(DemoScenario.MeetingOverload);
        DemoSnapshot? peak = null;
        for (int second = 0; second <= 100 && peak is null; second += 2)
        {
            var snapshot = await engine.TickAsync(PresentationStart.AddSeconds(second));
            if (snapshot.Message?.Category == "peak-stress") peak = snapshot;
        }
        Assert.NotNull(peak);
        long generation = engine.Generation;
        var history = engine.History.ToArray();
        engine.SetScenario(DemoScenario.MeetingOverload);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetScenario((DemoScenario)999));
        Assert.Equal(generation, engine.Generation);
        Assert.Same(peak, engine.Current);
        Assert.True(engine.IsCurrent(peak));

        engine.SetScenario(DemoScenario.HealthyDay);

        Assert.Equal(generation + 1, engine.Generation);
        Assert.False(engine.IsCurrent(peak));
        Assert.Null(engine.Current);
        Assert.Equal(history, engine.History.ToArray());
        var fresh = await engine.TickAsync(PresentationStart.AddSeconds(102));
        Assert.Equal(DemoScenario.HealthyDay, fresh.Scenario);
        Assert.True(engine.IsCurrent(fresh));
    }

    [Fact]
    public async Task RecoveryStartAndCancellationInvalidateDispatchedSnapshotsOnlyOnEffectiveTransitions()
    {
        var engine = new DemoEngine(new ReportedProvider(High));
        engine.SetScenario(DemoScenario.MeetingOverload);
        var before = await engine.TickAsync(PresentationStart);
        for (int second = 2; second <= 100 && before.Message?.Category != "peak-stress"; second += 2)
            before = await engine.TickAsync(PresentationStart.AddSeconds(second));
        Assert.Equal("peak-stress", before.Message?.Category);
        Assert.NotNull(before.Message);
        long generation = engine.Generation;
        engine.SetRecoveryActive(false);
        Assert.Equal(generation, engine.Generation);
        Assert.Same(before, engine.Current);

        engine.SetRecoveryActive(true);

        var active = Assert.IsType<DemoSnapshot>(engine.Current);
        Assert.Equal(generation + 1, active.Generation);
        Assert.True(engine.IsCurrent(active));
        Assert.False(engine.IsCurrent(before));
        Assert.Null(active.Message);
        Assert.Equal(before.Wearable, active.Wearable);
        Assert.Equal(before.Analysis, active.Analysis);
        engine.SetRecoveryActive(true);
        Assert.Equal(active.Generation, engine.Generation);
        Assert.Same(active, engine.Current);

        engine.SetRecoveryActive(false);

        var cancelled = Assert.IsType<DemoSnapshot>(engine.Current);
        Assert.Equal(active.Generation + 1, cancelled.Generation);
        Assert.True(engine.IsCurrent(cancelled));
        Assert.False(engine.IsCurrent(active));
        Assert.Null(cancelled.Message);
        Assert.Equal(before.Wearable, cancelled.Wearable);
        Assert.Equal(before.Analysis, cancelled.Analysis);
        engine.SetRecoveryActive(false);
        Assert.Same(cancelled, engine.Current);
        Assert.Equal(cancelled.Generation, engine.Generation);
    }

    [Theory]
    [InlineData("scenario")]
    [InlineData("recovery-start")]
    [InlineData("recovery-cancel")]
    [InlineData("recovery-complete")]
    [InlineData("recovery-complete-without-start")]
    public async Task StateBoundariesDiscardInFlightAndQueuedSamplesWithoutProviderRetry(string transition)
    {
        using var provider = new ControlledProvider();
        var source = new ManualTimeProvider(PresentationStart);
        var engine = new DemoEngine(provider, new SessionClock(source));
        var initial = engine.SampleAsync();
        (await provider.NextRead()).Complete();
        await initial.WaitAsync(Timeout);
        if (transition is "recovery-cancel" or "recovery-complete") engine.SetRecoveryActive(true);
        var before = engine.Current!;
        source.Advance(TimeSpan.FromSeconds(2));
        var active = engine.SampleAsync();
        var stale = await provider.NextRead();
        var queued = engine.SampleAsync();
        Assert.False(queued.IsCompleted);

        switch (transition)
        {
            case "scenario": engine.SetScenario(DemoScenario.DeepFocusSession); break;
            case "recovery-start": engine.SetRecoveryActive(true); break;
            case "recovery-cancel": engine.SetRecoveryActive(false); break;
            default: engine.CompleteRecovery(RecoveryActivity.ScreenBreak, source.GetUtcNow()); break;
        }
        var after = engine.Current;
        Assert.Equal(before.Generation + 1, engine.Generation);
        Assert.False(engine.IsCurrent(before));
        if (after is not null)
        {
            Assert.True(engine.IsCurrent(after));
            Assert.Null(after.Message);
        }
        stale.Complete(High(stale.Now) with { SleepScore = 20, TotalSleepMinutes = 240 });
        Assert.Null(await active.WaitAsync(Timeout));
        Assert.Null(await queued.WaitAsync(Timeout));
        Assert.Equal(2, provider.Calls);
        Assert.Same(after, engine.Current);
        Assert.Single(engine.History);

        var next = engine.SampleAsync();
        var freshRead = await provider.NextRead();
        Assert.Equal(Contexts.GetContext(engine.Scenario), freshRead.Context);
        freshRead.Complete();
        var fresh = Assert.IsType<DemoSnapshot>(await next.WaitAsync(Timeout));
        Assert.True(engine.IsCurrent(fresh));
        Assert.InRange(fresh.Analysis.FatigueScore, 0, 40);
        if (transition == "recovery-start") Assert.Null(fresh.Message);
        if (transition.StartsWith("recovery-complete")) Assert.Equal(WellnessState.Recovering, fresh.Analysis.State);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(2, engine.History.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedScenarioAndRecoveryDoNotDiscardInFlightOrQueuedSamples(bool recovering)
    {
        using var provider = new ControlledProvider();
        var engine = new DemoEngine(provider, new SessionClock(new ManualTimeProvider(PresentationStart)));
        engine.SetRecoveryActive(recovering);
        long generation = engine.Generation;
        var active = engine.SampleAsync();
        var first = await provider.NextRead();
        var queued = engine.SampleAsync();
        engine.SetScenario(engine.Scenario);
        engine.SetRecoveryActive(recovering);
        Assert.Equal(generation, engine.Generation);
        first.Complete();
        Assert.NotNull(await active.WaitAsync(Timeout));
        (await provider.NextRead()).Complete();
        var last = Assert.IsType<DemoSnapshot>(await queued.WaitAsync(Timeout));
        Assert.Equal(generation, last.Generation);
        Assert.True(engine.IsCurrent(last));
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("cancel")]
    [InlineData("complete")]
    public async Task LegacyTickCommitsLatestRecoveryStateWithoutCancellationOrRetry(string transition)
    {
        using var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        if (transition != "start") engine.SetRecoveryActive(true);
        var tick = engine.TickAsync(PresentationStart);
        var read = await provider.NextRead();
        if (transition == "complete") engine.CompleteRecovery(RecoveryActivity.ScreenBreak, PresentationStart);
        else engine.SetRecoveryActive(transition == "start");
        read.Complete();
        var snapshot = await tick.WaitAsync(Timeout);
        Assert.True(engine.IsCurrent(snapshot));
        Assert.Equal(1, provider.Calls);
        if (transition == "start") Assert.Null(snapshot.Message);
        Assert.Equal(transition == "complete", snapshot.Analysis.State == WellnessState.Recovering);
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing)]
    [InlineData(RecoveryActivity.ScreenBreak)]
    [InlineData(RecoveryActivity.WaterBreak)]
    [InlineData(RecoveryActivity.StretchBreak)]
    public async Task CompleteRecoveryAndCaptureReturnsStableCompletionSnapshotWithNewGeneration(RecoveryActivity activity)
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        var legacy = Synthetic(new ManualTimeProvider(PresentationStart));
        await Sample(engine);
        await Sample(legacy);
        engine.SetRecoveryActive(true);
        legacy.SetRecoveryActive(true);
        var before = engine.Current!;
        var captured = Assert.IsType<DemoSnapshot>(engine.CompleteRecoveryAndCapture(activity, PresentationStart));
        legacy.CompleteRecovery(activity, PresentationStart);

        Assert.Same(captured, engine.Current);
        AssertEquivalent(legacy.Current!, captured);
        Assert.Equal(before.Generation + 1, captured.Generation);
        Assert.True(engine.IsCurrent(captured));
        Assert.False(engine.IsCurrent(before));
        Assert.Equal(WellnessState.Recovering, captured.Analysis.State);
        Assert.Null(captured.Message);
        Assert.Empty(engine.Ledger.Snapshot(PresentationStart).CompletedBreaks);
        source.Advance(TimeSpan.FromSeconds(2));
        var next = await Sample(engine);
        Assert.NotSame(captured, next);
        Assert.Equal(PresentationStart, captured.Wearable.Timestamp);
        Assert.Null(captured.Message);
        Assert.Equal(captured.Generation, next.Generation);
    }

    [Fact]
    public async Task CompleteRecoveryAndCaptureHandlesMissingSnapshotAndRejectsInvalidActivityAtomically()
    {
        var engine = Synthetic(new ManualTimeProvider(PresentationStart));
        long generation = engine.Generation;
        Assert.Null(engine.CompleteRecoveryAndCapture(RecoveryActivity.ScreenBreak, PresentationStart));
        Assert.Equal(generation + 1, engine.Generation);
        var recovered = await Sample(engine);
        Assert.Equal(WellnessState.Recovering, recovered.Analysis.State);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.CompleteRecoveryAndCapture((RecoveryActivity)999, PresentationStart));
        Assert.Equal(recovered.Generation, engine.Generation);
        Assert.Same(recovered, engine.Current);
        Assert.True(engine.IsCurrent(recovered));
    }

    [Fact]
    public async Task PausedSamplingDoesNotCallTheProviderAndLegacyTickIsCancelled()
    {
        using var provider = new ControlledProvider();
        var source = new ManualTimeProvider(PresentationStart);
        var clock = new SessionClock(source);
        clock.BeginPresentation(PresentationStart);
        var engine = new DemoEngine(provider, clock);
        engine.PausePresentation();
        source.Advance(TimeSpan.FromHours(1));
        Assert.Null(await engine.SampleAsync().WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TickAsync(clock.GetUtcNow()).WaitAsync(Timeout));
        Assert.Equal(0, provider.Calls);
        Assert.Empty(engine.History);
        engine.ResumePresentation();
        var next = engine.SampleAsync();
        var read = await provider.NextRead();
        Assert.Equal(PresentationStart, read.Now);
        read.Complete();
        Assert.NotNull(await next.WaitAsync(Timeout));
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseFreezesUtcAndMonotonicTimeSkipsSamplesAndDiscardsCurrentAndPendingMessages(bool pendingOnly)
    {
        var source = new ManualTimeProvider(PresentationStart.AddMonths(2));
        var engine = Synthetic(source);
        engine.StartPresentation();
        engine.SetScenario(DemoScenario.RecoveryAfterBreak);
        if (!pendingOnly) await Sample(engine);
        engine.StartFocus(engine.Clock.GetUtcNow());
        source.Advance(TimeSpan.FromSeconds(12));
        engine.SimulateNotification(NotificationKind.GroupMessage, engine.Clock.GetUtcNow());
        engine.EndFocus(engine.Clock.GetUtcNow());
        if (!pendingOnly) Assert.Equal("focus-summary", engine.Current!.Message?.Category);
        var history = engine.History.ToArray();
        string story = JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow()));
        var wearable = engine.Current?.Wearable;

        engine.PausePresentation();
        var utc = engine.Clock.GetUtcNow();
        long timestamp = engine.Clock.GetTimestamp();
        source.Advance(TimeSpan.FromHours(3));
        source.SetUtcNow(PresentationStart.AddYears(-3));
        for (int i = 0; i < 3; i++)
            Assert.Null(await engine.SampleAsync().WaitAsync(Timeout));
        Assert.True(engine.IsPresentationPaused);
        Assert.True(engine.Clock.IsPaused);
        Assert.Equal(utc, engine.Clock.GetUtcNow());
        Assert.Equal(timestamp, engine.Clock.GetTimestamp());
        Assert.Equal(history, engine.History.ToArray());
        if (engine.Current is { } current) Assert.Equal(wearable, current.Wearable);
        Assert.Null(engine.Current?.Message);
        Assert.Equal(story, JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow())));

        engine.ResumePresentation();
        Assert.False(engine.IsPresentationPaused);
        Assert.False(engine.Clock.IsPaused);
        Assert.Equal(utc, engine.Clock.GetUtcNow());
        source.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), engine.Clock.GetElapsedTime(timestamp, engine.Clock.GetTimestamp()));
        var resumed = await Sample(engine);
        Assert.Equal(utc.AddSeconds(2), resumed.Wearable.Timestamp);
        Assert.NotEqual("focus-summary", resumed.Message?.Category);
        var fresh = Synthetic(new ManualTimeProvider(PresentationStart));
        fresh.SetScenario(DemoScenario.RecoveryAfterBreak);
        if (!pendingOnly) await fresh.TickAsync(PresentationStart);
        var expected = await fresh.TickAsync(utc.AddSeconds(2));
        Assert.Equal(expected.Wearable, resumed.Wearable);
    }

    [Fact]
    public async Task RestoreDiscardsPendingAndQueuedSamplesWithoutRetryingTheOldRead()
    {
        using var provider = new ControlledProvider();
        var source = new ManualTimeProvider(PresentationStart);
        var engine = new DemoEngine(provider, new SessionClock(source));
        var active = engine.SampleAsync();
        var stale = await provider.NextRead();
        var queued = engine.SampleAsync();
        Assert.False(queued.IsCompleted);
        long generation = engine.Generation;
        engine.Restore(new PersistedAppState { Scenario = DemoScenario.DeepFocusSession });
        Assert.True(engine.Generation > generation);
        stale.Complete(High(stale.Now) with { SleepScore = 20, TotalSleepMinutes = 240 });

        Assert.Null(await active.WaitAsync(Timeout));
        Assert.Null(await queued.WaitAsync(Timeout));
        Assert.Equal(1, provider.Calls);
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        AssertEmptyLedger(engine);
        source.Advance(TimeSpan.FromSeconds(2));
        var next = engine.SampleAsync();
        var read = await provider.NextRead();
        Assert.Equal(Contexts.GetContext(DemoScenario.DeepFocusSession), read.Context);
        Assert.Equal(source.GetUtcNow(), read.Now);
        read.Complete();
        var snapshot = Assert.IsType<DemoSnapshot>(await next.WaitAsync(Timeout));
        Assert.Equal(engine.Generation, snapshot.Generation);
        Assert.True(engine.IsCurrent(snapshot));
        Assert.Equal(2, provider.Calls);
        Assert.Single(engine.History);
        Assert.InRange(snapshot.Analysis.FatigueScore, 0, 40);
        Assert.Null(snapshot.Message);
    }

    [Fact]
    public async Task SamplesSerializeAndReadFreshClockTimeOnlyAfterAcquiringTheGate()
    {
        using var provider = new ControlledProvider();
        var source = new ManualTimeProvider(PresentationStart);
        var engine = new DemoEngine(provider, new SessionClock(source));
        var first = engine.SampleAsync();
        var firstRead = await provider.NextRead();
        var second = engine.SampleAsync();
        var third = engine.SampleAsync();
        Assert.Equal(1, provider.Calls);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        source.Advance(TimeSpan.FromSeconds(7));
        firstRead.Complete();
        await first.WaitAsync(Timeout);

        var secondRead = await provider.NextRead();
        Assert.Equal(PresentationStart.AddSeconds(7), secondRead.Now);
        Assert.Equal(2, provider.Calls);
        Assert.Single(engine.History);
        Assert.False(third.IsCompleted);
        source.Advance(TimeSpan.FromSeconds(11));
        secondRead.Complete();
        var thirdRead = await provider.NextRead();
        Assert.Equal(PresentationStart.AddSeconds(18), thirdRead.Now);
        thirdRead.Complete();
        var snapshots = await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        Assert.Equal(1, provider.MaximumConcurrentReads);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(new[] { PresentationStart, PresentationStart.AddSeconds(7), PresentationStart.AddSeconds(18) },
            engine.History.Select(entry => entry.Timestamp));
        Assert.Same(Assert.Single(snapshots, snapshot => snapshot?.Wearable.Timestamp == thirdRead.Now), engine.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SampleCancellationCannotCommitOrConsumeQueuedProviderWork(bool cancelQueued)
    {
        using var provider = new ControlledProvider();
        using var cancellation = new CancellationTokenSource();
        var source = new ManualTimeProvider(PresentationStart);
        var engine = new DemoEngine(provider, new SessionClock(source));
        var active = engine.SampleAsync(cancelQueued ? CancellationToken.None : cancellation.Token);
        var read = await provider.NextRead();
        var cancelled = cancelQueued ? engine.SampleAsync(cancellation.Token) : active;
        cancellation.Cancel();
        if (!cancelQueued) read.Complete(High(read.Now));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));
        Assert.Equal(1, provider.Calls);
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        if (cancelQueued)
        {
            Assert.False(active.IsCompleted);
            read.Complete();
            Assert.NotNull(await active.WaitAsync(Timeout));
        }
        source.Advance(TimeSpan.FromSeconds(2));
        var next = engine.SampleAsync();
        (await provider.NextRead()).Complete();
        Assert.NotNull(await next.WaitAsync(Timeout));
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTickStillRetriesScenarioChangesAndOrdinaryRestore(bool restore)
    {
        using var provider = new ControlledProvider();
        var engine = new DemoEngine(provider, new SessionClock(new ManualTimeProvider(PresentationStart)));
        var tick = engine.TickAsync(PresentationStart);
        var stale = await provider.NextRead();
        if (restore) engine.Restore(new PersistedAppState { Scenario = DemoScenario.DeepFocusSession });
        else engine.SetScenario(DemoScenario.DeepFocusSession);
        stale.Complete(High(stale.Now) with { SleepScore = 20 });
        var retry = await provider.NextRead();
        Assert.Equal(Contexts.GetContext(DemoScenario.DeepFocusSession), retry.Context);
        Assert.Equal(PresentationStart, retry.Now);
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        retry.Complete();
        var snapshot = await tick.WaitAsync(Timeout);
        Assert.Equal(DemoScenario.DeepFocusSession, snapshot.Scenario);
        Assert.Equal(engine.Generation, snapshot.Generation);
        Assert.InRange(snapshot.Analysis.FatigueScore, 0, 40);
        Assert.Equal(2, provider.Calls);
        Assert.Single(engine.History);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedLifecycleDoesNotInvalidateActiveOrQueuedSamples(bool reset)
    {
        using var provider = new ControlledProvider();
        var source = new ManualTimeProvider(PresentationStart);
        var engine = new DemoEngine(provider, new SessionClock(source));
        var active = engine.SampleAsync();
        var firstRead = await provider.NextRead();
        var queued = engine.SampleAsync();
        long generation = engine.Generation;
        Assert.Throws<NotSupportedException>(() =>
        {
            if (reset) engine.ResetPresentation();
            else engine.StartPresentation();
        });
        Assert.Equal(generation, engine.Generation);
        source.Advance(TimeSpan.FromSeconds(2));
        firstRead.Complete();
        Assert.NotNull(await active.WaitAsync(Timeout));
        var nextRead = await provider.NextRead();
        nextRead.Complete();
        var snapshot = Assert.IsType<DemoSnapshot>(await queued.WaitAsync(Timeout));
        Assert.Equal(generation, snapshot.Generation);
        Assert.True(engine.IsCurrent(snapshot));
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, engine.History.Count);
    }

    [Theory]
    [InlineData("pause", false)]
    [InlineData("pause-resume", false)]
    [InlineData("stop", false)]
    [InlineData("pause", true)]
    [InlineData("pause-resume", true)]
    [InlineData("stop", true)]
    public async Task LifecycleTransitionsInvalidatePendingAndQueuedReadsRatherThanRetrying(string transition, bool legacy)
    {
        using var provider = new ControlledProvider();
        var source = new ManualTimeProvider(PresentationStart);
        var clock = new SessionClock(source);
        // Inject an already-running clock to control I/O without replacing the sealed synthetic provider.
        clock.BeginPresentation(PresentationStart);
        var engine = new DemoEngine(provider, clock);
        Task<DemoSnapshot?> Read() => legacy ? LegacyRead(engine) : engine.SampleAsync();
        var active = Read();
        var stale = await provider.NextRead();
        var queued = Read();
        Assert.False(queued.IsCompleted);
        if (transition == "stop") engine.StopPresentation();
        else
        {
            engine.PausePresentation();
            if (transition == "pause-resume") engine.ResumePresentation();
        }
        stale.Complete(High(stale.Now));
        if (legacy)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(Timeout));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        }
        else
        {
            Assert.Null(await active.WaitAsync(Timeout));
            Assert.Null(await queued.WaitAsync(Timeout));
        }
        Assert.Equal(1, provider.Calls);
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    public async Task StopEndsFocusAtPresentationTimePreservesLedgerAndSafelyReturnsToEitherWallclockDirection(
        int wallclockYears, bool paused)
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        engine.Preferences = Preferences;
        engine.StartPresentation();
        engine.SetScenario(DemoScenario.PoorSleepDay);
        await Sample(engine);
        engine.StartFocus(engine.Clock.GetUtcNow());
        source.Advance(TimeSpan.FromSeconds(4));
        engine.EndFocus(engine.Clock.GetUtcNow());
        engine.StartFocus(engine.Clock.GetUtcNow());
        engine.SimulateNotification(NotificationKind.FyiEmail, engine.Clock.GetUtcNow(), "Deferred — FYI");
        engine.SimulateNotification(NotificationKind.CriticalAlert, engine.Clock.GetUtcNow(), "Urgent — incident");
        source.Advance(TimeSpan.FromSeconds(20));
        await Sample(engine);
        var completion = Completion();
        engine.Ledger.CompleteRecovery(completion);
        engine.Ledger.CancelRecovery();
        engine.SetRecoveryActive(true);
        var old = engine.Current!;
        if (paused) engine.PausePresentation();
        long generation = engine.Generation;
        source.SetUtcNow(PresentationStart.AddYears(wallclockYears));

        engine.StopPresentation();

        Assert.True(engine.Generation > generation);
        Assert.False(engine.IsCurrent(old));
        Assert.False(engine.Clock.IsPresentation);
        Assert.False(engine.IsPresentationPaused);
        Assert.False(engine.IsFocusActive);
        Assert.False(engine.PeakStressEstablished);
        Assert.Equal(source.GetUtcNow(), engine.Clock.GetUtcNow());
        Assert.Equal(DemoScenario.HealthyDay, engine.Scenario);
        Assert.Equal(Preferences, engine.Preferences);
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        Assert.Empty(engine.PendingNotifications);
        Assert.Empty(engine.NotificationHistory);
        var story = engine.Ledger.Snapshot(engine.Clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(24), story.FocusTime);
        Assert.Equal(2, story.CompletedFocusSessions);
        Assert.Equal(1, story.DeferredNotifications);
        Assert.Equal(1, story.UrgentNotifications);
        Assert.Equal(1, story.CancelledBreaks);
        Assert.Same(completion, Assert.Single(story.CompletedBreaks));
        Assert.False(story.IsFocusActive);
        Assert.Null(engine.EndFocus(engine.Clock.GetUtcNow()));
        var fresh = Synthetic(new ManualTimeProvider(source.GetUtcNow()));
        fresh.Preferences = Preferences;
        var actual = await Sample(engine);
        AssertEquivalent(await Sample(fresh), actual);
        Assert.NotEqual(WellnessState.Recovering, actual.Analysis.State);
        Assert.Equal(JsonSerializer.Serialize(story),
            JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow())));
        generation = engine.Generation;
        engine.StopPresentation();
        Assert.Equal(generation, engine.Generation);
        Assert.Same(actual, engine.Current);
        Assert.Equal(JsonSerializer.Serialize(story),
            JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow())));
    }

    [Fact]
    public async Task FocusLedgerCountsScriptedTitlesOnlyDuringFocusWithoutCountingReleaseOrRepeatedStartEnd()
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        engine.StartPresentation();
        foreach (var kind in Enum.GetValues<NotificationKind>())
            Assert.True(engine.SimulateNotification(kind, PresentationStart, $"Outside — {kind}").Allowed);
        AssertEmptyLedger(engine);
        engine.StartFocus(PresentationStart);
        engine.StartFocus(PresentationStart.AddSeconds(5));
        var decisions = Enum.GetValues<NotificationKind>().Select(kind =>
            engine.SimulateNotification(kind, PresentationStart.AddSeconds(6), $"Scripted संदेश — {kind}")).ToArray();
        Assert.All(decisions, decision => Assert.Equal($"Scripted संदेश — {decision.Notification.Kind}", decision.Notification.Title));
        Assert.Equal(3, decisions.Count(decision => !decision.Allowed));
        Assert.Equal(4, decisions.Count(decision => decision.Allowed));
        Assert.Equal(3, engine.PendingNotifications.Count);
        source.Advance(TimeSpan.FromSeconds(12));
        var active = engine.Ledger.Snapshot(engine.Clock.GetUtcNow());
        Assert.True(active.IsFocusActive);
        Assert.Equal(TimeSpan.FromSeconds(12), active.FocusTime);
        Assert.Equal(0, active.CompletedFocusSessions);
        var summary = engine.EndFocus(engine.Clock.GetUtcNow());
        Assert.Equal(new FocusSessionSummary(TimeSpan.FromSeconds(12), 3, 4), summary);
        Assert.Null(engine.EndFocus(engine.Clock.GetUtcNow().AddHours(1)));
        Assert.Empty(engine.PendingNotifications);
        foreach (var deferred in decisions.Where(decision => !decision.Allowed))
            Assert.Contains(engine.NotificationHistory, decision => decision.Allowed && decision.Notification == deferred.Notification);
        engine.SimulateNotification(NotificationKind.CriticalAlert, engine.Clock.GetUtcNow(), "Outside again");
        await Sample(engine);
        var complete = engine.Ledger.Snapshot(engine.Clock.GetUtcNow().AddHours(2));
        Assert.False(complete.IsFocusActive);
        Assert.Equal(TimeSpan.FromSeconds(12), complete.FocusTime);
        Assert.Equal(1, complete.CompletedFocusSessions);
        Assert.Equal(3, complete.DeferredNotifications);
        Assert.Equal(4, complete.UrgentNotifications);
        Assert.NotEmpty(complete.Description);

        engine.StartFocus(engine.Clock.GetUtcNow());
        source.Advance(TimeSpan.FromSeconds(8));
        engine.EndFocus(engine.Clock.GetUtcNow());
        var second = engine.Ledger.Snapshot(engine.Clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(20), second.FocusTime);
        Assert.Equal(2, second.CompletedFocusSessions);
        Assert.Equal(3, second.DeferredNotifications);
        Assert.Equal(4, second.UrgentNotifications);
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing)]
    [InlineData(RecoveryActivity.ScreenBreak)]
    [InlineData(RecoveryActivity.WaterBreak)]
    [InlineData(RecoveryActivity.StretchBreak)]
    public async Task EngineRecoveryCompletionDoesNotRecordControllerOwnedLedgerEntries(RecoveryActivity activity)
    {
        var source = new ManualTimeProvider(PresentationStart);
        var engine = Synthetic(source);
        engine.StartPresentation();
        await Sample(engine);
        engine.Ledger.CancelRecovery();
        string before = JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow()));
        engine.SetRecoveryActive(true);
        engine.CompleteRecovery(activity, engine.Clock.GetUtcNow());
        engine.CompleteRecovery(activity, engine.Clock.GetUtcNow());
        Assert.Equal(WellnessState.Recovering, engine.Current!.Analysis.State);
        Assert.Equal(before, JsonSerializer.Serialize(engine.Ledger.Snapshot(engine.Clock.GetUtcNow())));
        var completion = Completion(activity);
        engine.Ledger.CompleteRecovery(completion);
        engine.CompleteRecovery(activity, engine.Clock.GetUtcNow());
        Assert.Same(completion, Assert.Single(engine.Ledger.Snapshot(engine.Clock.GetUtcNow()).CompletedBreaks));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeakPropertyRequiresSustainedEpisodeAndCurrentThresholdEvenWhenPromptsAreSnoozed(bool completeRecovery)
    {
        bool high = true;
        var source = new ManualTimeProvider(PresentationStart);
        var engine = new DemoEngine(new ReportedProvider(now => high ? High(now) : Healthy(now)), new SessionClock(source));
        engine.Snooze(TimeSpan.FromHours(1), PresentationStart);
        DateTimeOffset? thresholdAt = null;
        for (int second = 0; second <= 60; second += 2)
        {
            if (second > 0) source.Advance(TimeSpan.FromSeconds(2));
            var snapshot = await Sample(engine);
            if (snapshot.Analysis.StressScore >= 90) thresholdAt ??= snapshot.Wearable.Timestamp;
            bool sustained = thresholdAt.HasValue && snapshot.Wearable.Timestamp - thresholdAt.Value >= TimeSpan.FromSeconds(10);
            Assert.Equal(sustained, engine.PeakStressEstablished);
            Assert.Null(snapshot.Message);
        }
        Assert.NotNull(thresholdAt);
        Assert.True(engine.PeakStressEstablished);
        high = false;
        for (int i = 0; i < 2; i++)
        {
            source.Advance(TimeSpan.FromSeconds(2));
            await Sample(engine);
        }
        Assert.True(engine.Current!.Analysis.StressScore < 90);
        Assert.False(engine.PeakStressEstablished);
        high = true;
        for (int i = 0; i < 15; i++)
        {
            source.Advance(TimeSpan.FromSeconds(2));
            await Sample(engine);
        }
        Assert.True(engine.PeakStressEstablished);
        if (completeRecovery) engine.CompleteRecovery(RecoveryActivity.ScreenBreak, engine.Clock.GetUtcNow());
        else engine.SetRecoveryActive(true);
        Assert.False(engine.PeakStressEstablished);
        Assert.Null(engine.Current!.Message);
        Assert.Empty(engine.Ledger.Snapshot(engine.Clock.GetUtcNow()).CompletedBreaks);
    }

    private static DemoEngine Synthetic(ManualTimeProvider source) =>
        new(new SyntheticWearableProvider(7), new SessionClock(source));

    private static async Task<DemoSnapshot> Sample(DemoEngine engine) =>
        Assert.IsType<DemoSnapshot>(await engine.SampleAsync().WaitAsync(Timeout));

    private static async Task<DemoSnapshot?> LegacyRead(DemoEngine engine) =>
        await engine.TickAsync(engine.Clock.GetUtcNow());

    private static void ApplyReset(DemoEngine engine, string transition)
    {
        switch (transition)
        {
            case "start": engine.StartPresentation(); break;
            case "reset": engine.ResetPresentation(); break;
            case "restore": engine.Restore(new PersistedAppState { Preferences = Preferences }); break;
            default: throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private static async Task DirtyRuntime(DemoEngine engine, ManualTimeProvider source)
    {
        engine.SetScenario(DemoScenario.PoorSleepDay);
        await Sample(engine);
        engine.SetScenario(DemoScenario.RecoveryAfterBreak);
        await Sample(engine);
        source.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await Sample(engine)).Wearable.Steps > 0);
        engine.SetScenario(DemoScenario.RisingStress);
        for (int i = 0; i <= 50; i++)
        {
            source.Advance(TimeSpan.FromSeconds(2));
            await Sample(engine);
        }
        Assert.True(engine.PeakStressEstablished);
        engine.StartFocus(engine.Clock.GetUtcNow());
        engine.SimulateNotification(NotificationKind.GroupMessage, engine.Clock.GetUtcNow(), "Pending — टीम");
        engine.SimulateNotification(NotificationKind.ManagerMessage, engine.Clock.GetUtcNow(), "Urgent — समीक्षा");
        source.Advance(TimeSpan.FromSeconds(2));
        engine.EndFocus(engine.Clock.GetUtcNow());
        engine.CompleteRecovery(RecoveryActivity.ScreenBreak, engine.Clock.GetUtcNow());
        engine.SetRecoveryActive(true);
        engine.Ledger.CompleteRecovery(Completion());
        engine.Ledger.CancelRecovery();
        engine.Snooze(TimeSpan.FromHours(4), engine.Clock.GetUtcNow());
        engine.StartFocus(engine.Clock.GetUtcNow());
        engine.SimulateNotification(NotificationKind.GroupMessage, engine.Clock.GetUtcNow(), "Still pending — टीम");
        Assert.True(engine.IsFocusActive);
        Assert.NotEmpty(engine.PendingNotifications);
        Assert.NotEmpty(engine.History);
        Assert.NotEmpty(engine.NotificationHistory);
        Assert.Equal(1, engine.Ledger.Snapshot(engine.Clock.GetUtcNow()).CompletedFocusSessions);
    }

    private static async Task<List<DemoSnapshot>> RunPresentation(DemoEngine engine, ManualTimeProvider source)
    {
        engine.StartPresentation();
        Assert.Equal(PresentationStart, engine.Clock.GetUtcNow());
        Assert.Equal(DemoScenario.HealthyDay, engine.Scenario);
        AssertEmptyLedger(engine);
        var snapshots = new List<DemoSnapshot> { await Sample(engine) };
        engine.SetScenario(DemoScenario.DeepFocusSession);
        engine.StartFocus(engine.Clock.GetUtcNow());
        engine.SimulateNotification(NotificationKind.FyiEmail, engine.Clock.GetUtcNow(), "FYI — project");
        engine.SimulateNotification(NotificationKind.CriticalAlert, engine.Clock.GetUtcNow(), "Incident — priority");
        source.Advance(TimeSpan.FromSeconds(10));
        snapshots.Add(await Sample(engine));
        engine.EndFocus(engine.Clock.GetUtcNow());
        snapshots.Add(await Sample(engine));
        engine.SetScenario(DemoScenario.RisingStress);
        for (int i = 0; i < 60; i++)
        {
            source.Advance(TimeSpan.FromSeconds(2));
            snapshots.Add(await Sample(engine));
        }
        engine.SetRecoveryActive(true);
        source.Advance(TimeSpan.FromSeconds(10));
        engine.CompleteRecovery(RecoveryActivity.ScreenBreak, engine.Clock.GetUtcNow());
        engine.SetScenario(DemoScenario.RecoveryAfterBreak);
        for (int i = 0; i < 10; i++)
        {
            source.Advance(TimeSpan.FromSeconds(2));
            snapshots.Add(await Sample(engine));
        }
        engine.Snooze(TimeSpan.FromHours(1), engine.Clock.GetUtcNow());
        return snapshots;
    }

    private static void AssertEquivalent(DemoSnapshot expected, DemoSnapshot actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected with { Generation = 0 }),
            JsonSerializer.Serialize(actual with { Generation = 0 }));

    private static void AssertEmptyLedger(DemoEngine engine)
    {
        var story = engine.Ledger.Snapshot(engine.Clock.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, story.FocusTime);
        Assert.Equal(0, story.CompletedFocusSessions);
        Assert.Equal(0, story.DeferredNotifications);
        Assert.Equal(0, story.UrgentNotifications);
        Assert.Empty(story.CompletedBreaks);
        Assert.Equal(0, story.CancelledBreaks);
        Assert.False(story.IsFocusActive);
    }

    private static WearableSample Healthy(DateTimeOffset now) => new()
    {
        Timestamp = now, HeartRate = 70, RestingHeartRate = 60, HeartRateVariability = 65,
        StressLevel = 20, SleepScore = 85, TotalSleepMinutes = 480,
        BodyBattery = 80, ReadinessScore = 85, RecoveryScore = 85
    };

    private static WearableSample High(DateTimeOffset now) => Healthy(now) with
    {
        HeartRate = 110, HeartRateVariability = 15, StressLevel = 95
    };

    private static RecoveryCompletion Completion(RecoveryActivity activity = RecoveryActivity.ScreenBreak) =>
        new(activity, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), true,
            new RecoveryComparison(70, 40, 30, 60, 90, 72, true, true));

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp = 10_000;
        public override long TimestampFrequency => 1000;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public void SetUtcNow(DateTimeOffset value) { lock (_gate) _utcNow = value; }
        public void Advance(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp += elapsed.Ticks / TimeSpan.TicksPerMillisecond;
            }
        }
    }

    private sealed class ReportedProvider(Func<DateTimeOffset, WearableSample> sample) : IWearableProvider
    {
        public string Name => "Deterministic reported measurements";
        public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(sample(now));
        }
    }

    private sealed class PendingRead(WorkContext context, DateTimeOffset now)
    {
        private readonly TaskCompletionSource<WearableSample> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkContext Context { get; } = context;
        public DateTimeOffset Now { get; } = now;
        public Task<WearableSample> Result => _completion.Task;
        public void Complete(WearableSample? sample = null) => _completion.TrySetResult(sample ?? Healthy(Now));
    }

    private sealed class ControlledProvider : IWearableProvider, IDisposable
    {
        private readonly Channel<PendingRead> _reads = Channel.CreateUnbounded<PendingRead>();
        private readonly System.Collections.Concurrent.ConcurrentBag<PendingRead> _pending = new();
        private int _calls;
        private int _active;
        private int _maximum;
        public string Name => "Bounded manually completed provider";
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrentReads => Volatile.Read(ref _maximum);
        public Task<PendingRead> NextRead() => _reads.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

        public async Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            int maximum;
            do
            {
                maximum = Volatile.Read(ref _maximum);
                if (maximum >= active) break;
            } while (Interlocked.CompareExchange(ref _maximum, active, maximum) != maximum);
            var read = new PendingRead(context, now);
            _pending.Add(read);
            try
            {
                Assert.True(_reads.Writer.TryWrite(read));
                // Ignore cancellation deliberately: lifecycle and commit guards belong to the engine.
                return await read.Result.WaitAsync(Timeout).ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Dispose()
        {
            foreach (var read in _pending) read.Complete();
            _reads.Writer.TryComplete();
        }
    }
}
