using System.Collections;
using System.Text.Json;
using System.Threading.Channels;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class EngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static DemoEngine Synthetic(DemoScenario scenario = DemoScenario.HealthyDay)
    {
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(scenario);
        return new DemoEngine(provider);
    }

    private static WearableSample Healthy(DateTimeOffset now) => new()
    {
        Timestamp = now, HeartRate = 70, RestingHeartRate = 60, HeartRateVariability = 65,
        StressLevel = 20, SleepScore = 85, TotalSleepMinutes = 480, BodyBattery = 80,
        ReadinessScore = 85, RecoveryScore = 85
    };

    private static WearableSample High(DateTimeOffset now) => Healthy(now) with
    {
        HeartRate = 110, HeartRateVariability = 15, StressLevel = 95
    };

    private static async Task<List<DemoSnapshot>> Ticks(DemoEngine engine, int first, int last)
    {
        var snapshots = new List<DemoSnapshot>();
        for (int second = first; second <= last; second += 2)
            snapshots.Add(await engine.TickAsync(Start.AddSeconds(second)));
        return snapshots;
    }

    private static void AssertBounded(DemoSnapshot snapshot)
    {
        var analysis = snapshot.Analysis;
        foreach (double score in new[] { analysis.FocusScore, analysis.StressScore, analysis.FatigueScore, analysis.BurnoutRiskScore })
        {
            Assert.True(double.IsFinite(score));
            Assert.InRange(score, 0, 100);
        }
        Assert.NotEmpty(analysis.Reasons);
        Assert.Contains(analysis.Reasons, reason => reason.Contains("Non-medical", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> items)
    {
        Assert.NotEmpty(items);
        if (items is IList<T> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic[0] = items[0]);
            Assert.Throws<NotSupportedException>(() => generic.Add(items[0]));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }
        if (items is IList list)
        {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = items[0]);
        }
    }

    [Theory]
    [InlineData(DemoScenario.HealthyDay, WellnessState.Balanced)]
    [InlineData(DemoScenario.DeepFocusSession, WellnessState.Focused)]
    [InlineData(DemoScenario.RisingStress, WellnessState.BurnoutRisk)]
    [InlineData(DemoScenario.MeetingOverload, WellnessState.BurnoutRisk)]
    [InlineData(DemoScenario.RecoveryAfterBreak, WellnessState.Recovering)]
    [InlineData(DemoScenario.PoorSleepDay, WellnessState.Fatigued)]
    public async Task AllSixScenariosRunEndToEnd(DemoScenario scenario, WellnessState expected)
    {
        var engine = Synthetic(scenario);
        Assert.Equal(scenario, engine.Scenario);
        Assert.False(engine.IsFocusActive);
        if (scenario == DemoScenario.DeepFocusSession)
            engine.StartFocus(Start);

        var snapshots = await Ticks(engine, 0, 120);
        Assert.Equal(61, snapshots.Count);
        Assert.Equal(expected, snapshots[^1].Analysis.State);
        Assert.Same(snapshots[^1], engine.Current);
        Assert.Equal(61, engine.History.Count);
        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            AssertBounded(snapshot);
            Assert.Equal(Start.AddSeconds(i * 2), snapshot.Wearable.Timestamp);
            Assert.Equal(scenario, snapshot.Scenario);
            Assert.Equal(new MockWorkContextProvider().GetContext(scenario), snapshot.Context);
            Assert.Equal(scenario == DemoScenario.DeepFocusSession, snapshot.IsFocusActive);
            Assert.Equal(new DemoHistoryEntry(snapshot.Wearable.Timestamp, scenario, snapshot.Analysis.State,
                snapshot.Analysis.StressScore, snapshot.Analysis.FocusScore), engine.History[i]);
        }
        if (scenario == DemoScenario.DeepFocusSession)
        {
            Assert.Equal(Start, snapshots[^1].FocusStartedAt);
            Assert.All(snapshots, snapshot => Assert.Null(snapshot.Message));
        }
    }

    [Fact]
    public async Task RisingStressBecomesVisibleBetweenTwentyAndFortySecondsNotOnASingleTick()
    {
        var snapshots = await Ticks(Synthetic(DemoScenario.RisingStress), 0, 40);
        Assert.All(snapshots.Where(s => s.Wearable.Timestamp < Start.AddSeconds(20)), snapshot =>
        {
            Assert.DoesNotContain(snapshot.Analysis.State,
                new[] { WellnessState.Stressed, WellnessState.HighStress, WellnessState.BurnoutRisk });
            Assert.Null(snapshot.Message);
        });
        var first = Assert.Single(snapshots, s => s.Message?.Category == "sustained-stress");
        Assert.InRange((first.Wearable.Timestamp - Start).TotalSeconds, 20, 40);
        Assert.Contains(first.Analysis.State, new[] { WellnessState.Stressed, WellnessState.HighStress });
        Assert.Contains(first.Analysis.Reasons, reason => reason.Contains("sustained for at least 12 seconds"));
        Assert.True(snapshots[^1].Analysis.StressScore > snapshots[0].Analysis.StressScore + 30);
        Assert.Equal(WellnessState.HighStress, snapshots[^1].Analysis.State);
    }

    [Fact]
    public async Task BurnoutRequiresSixtySecondsOfObservedStressAndWorkRisk()
    {
        var engine = new DemoEngine(new DelegateProvider((_, now, _) => Task.FromResult(High(now))));
        engine.SetScenario(DemoScenario.MeetingOverload);
        var before = await Ticks(engine, 0, 58);
        Assert.All(before, snapshot =>
        {
            Assert.NotEqual(WellnessState.BurnoutRisk, snapshot.Analysis.State);
            Assert.InRange(snapshot.Analysis.BurnoutRiskScore, 0, 49);
        });
        var sustained = await engine.TickAsync(Start.AddSeconds(60));
        Assert.Equal(WellnessState.BurnoutRisk, sustained.Analysis.State);
        Assert.InRange(sustained.Analysis.BurnoutRiskScore, 65, 100);
        Assert.Contains(sustained.Analysis.Reasons, reason => reason.Contains("at least 60 seconds plus"));
    }

    [Fact]
    public async Task ObservationGapCannotFabricateSustainedStressOrBurnout()
    {
        var engine = new DemoEngine(new DelegateProvider((_, now, _) => Task.FromResult(High(now))));
        engine.SetScenario(DemoScenario.MeetingOverload);
        await Ticks(engine, 0, 40);
        var afterGap = await engine.TickAsync(Start.AddMinutes(2));
        Assert.Equal(30, afterGap.Analysis.StressScore);
        Assert.Equal(0, afterGap.Analysis.BurnoutRiskScore);
        Assert.DoesNotContain(afterGap.Analysis.State,
            new[] { WellnessState.Stressed, WellnessState.HighStress, WellnessState.BurnoutRisk });
        Assert.Contains(afterGap.Analysis.Reasons, reason => reason.Contains("Observation gap"));
    }

    [Fact]
    public async Task NullableInjectedMetricsRemainMissingAndNeverImplyHighStress()
    {
        var engine = new DemoEngine(new DelegateProvider((_, now, _) =>
            Task.FromResult(new WearableSample { Timestamp = now })));
        engine.SetScenario(DemoScenario.MeetingOverload);
        var snapshots = await Ticks(engine, 0, 120);
        foreach (var snapshot in snapshots)
        {
            Assert.Equal(new WearableSample { Timestamp = snapshot.Wearable.Timestamp }, snapshot.Wearable);
            AssertBounded(snapshot);
            Assert.InRange(snapshot.Analysis.StressScore, 0, 55);
            Assert.Equal(0, snapshot.Analysis.BurnoutRiskScore);
            Assert.Null(snapshot.Message);
            Assert.Contains(snapshot.Analysis.Reasons, reason => reason.Contains("Heart rate missing"));
            Assert.Contains(snapshot.Analysis.Reasons, reason => reason.Contains("Sleep score missing"));
        }
        engine.CompleteBreathing(Start.AddSeconds(120));
        Assert.Null(engine.Current!.Wearable.HeartRate);
        Assert.Null(engine.Current.Wearable.StressLevel);
        Assert.Null((await engine.TickAsync(Start.AddSeconds(122))).Wearable.RecoveryScore);
    }

    [Theory]
    [InlineData(DemoScenario.PoorSleepDay, WellnessState.Fatigued, "fatigue")]
    [InlineData(DemoScenario.RecoveryAfterBreak, WellnessState.Recovering, "recovery")]
    public async Task FocusSuppressesFatigueAndRecoveryWithoutSuppressingAnalysis(
        DemoScenario scenario, WellnessState state, string category)
    {
        var engine = Synthetic(scenario);
        engine.StartFocus(Start);
        var focused = await Ticks(engine, 0, 70);
        Assert.All(focused, snapshot =>
        {
            Assert.True(snapshot.IsFocusActive);
            Assert.Equal(state, snapshot.Analysis.State);
            Assert.Null(snapshot.Message);
        });
        engine.EndFocus(Start.AddSeconds(70));
        Assert.Equal("focus-summary", (await engine.TickAsync(Start.AddSeconds(72))).Message?.Category);
        Assert.Equal(category, (await engine.TickAsync(Start.AddSeconds(74))).Message?.Category);
    }

    [Fact]
    public async Task FocusSuppressesHydrationUntilSessionEnds()
    {
        var engine = Synthetic(DemoScenario.DeepFocusSession);
        engine.StartFocus(Start);
        Assert.All(await Ticks(engine, 0, 1802), snapshot => Assert.Null(snapshot.Message));
        engine.EndFocus(Start.AddSeconds(1802));
        Assert.Equal("focus-summary", (await engine.TickAsync(Start.AddSeconds(1804))).Message?.Category);
        Assert.Equal("hydration", (await engine.TickAsync(Start.AddSeconds(1806))).Message?.Category);
    }

    [Fact]
    public async Task SustainedStressCanInterruptFocus()
    {
        var engine = Synthetic(DemoScenario.RisingStress);
        engine.StartFocus(Start);
        var snapshots = await Ticks(engine, 0, 40);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsFocusActive));
        Assert.Contains(snapshots, snapshot => snapshot.Message?.Category == "sustained-stress");
        Assert.All(snapshots.Where(snapshot => snapshot.Analysis.State == WellnessState.Stressed),
            snapshot => Assert.Null(snapshot.Message));
        Assert.Equal(WellnessState.HighStress, snapshots[^1].Analysis.State);
    }

    [Fact]
    public async Task EveryNotificationKindRespectsFocusPriorityBypassesSnoozeAndIsReleasedWithSummary()
    {
        var engine = Synthetic();
        await engine.TickAsync(Start);
        engine.StartFocus(Start);
        engine.Snooze(TimeSpan.FromHours(1), Start);
        var kinds = Enum.GetValues<NotificationKind>();
        var urgent = new[] { NotificationKind.ManagerMessage, NotificationKind.Escalation,
            NotificationKind.CriticalAlert, NotificationKind.MeetingReminder };
        var decisions = kinds.Select(kind => engine.SimulateNotification(kind, Start.AddSeconds(2))).ToArray();
        foreach (var decision in decisions)
        {
            Assert.Equal(urgent.Contains(decision.Notification.Kind), decision.Allowed);
            Assert.NotEqual(Guid.Empty, decision.Notification.Id);
            Assert.Equal(Start.AddSeconds(2), decision.Notification.Timestamp);
            Assert.False(string.IsNullOrWhiteSpace(decision.Notification.Title));
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        }
        Assert.Equal(kinds.Length, decisions.Select(d => d.Notification.Id).Distinct().Count());
        var pending = engine.PendingNotifications;
        var history = engine.NotificationHistory;
        Assert.Equal(3, pending.Count);
        Assert.Equal(7, history.Count);
        AssertReadOnly(pending);
        AssertReadOnly(history);
        Assert.Equal(decisions.Where(d => !d.Allowed).Select(d => d.Notification), pending);
        Assert.Equal(3, engine.Current!.QueuedNotifications);
        Assert.Equal(4, engine.Current.UrgentNotifications);
        engine.StartFocus(Start.AddSeconds(10));
        Assert.Equal(Start, engine.Current.FocusStartedAt);
        Assert.Equal(3, engine.PendingNotifications.Count);
        Assert.All(await Ticks(engine, 2, 60), snapshot => Assert.Null(snapshot.Message));

        var summary = engine.EndFocus(Start.AddSeconds(60));
        Assert.Equal(new FocusSessionSummary(TimeSpan.FromMinutes(1), 3, 4), summary);
        Assert.False(engine.IsFocusActive);
        Assert.Empty(engine.PendingNotifications);
        Assert.False(engine.Current!.IsFocusActive);
        Assert.Null(engine.Current.FocusStartedAt);
        Assert.Equal(0, engine.Current.QueuedNotifications);
        Assert.Equal("focus-summary", engine.Current.Message?.Category);
        Assert.Contains("3 delayed", engine.Current.Message!.Text);
        Assert.Contains("4 priority", engine.Current.Message.Text);
        Assert.Equal(CharacterState.Celebrating, engine.Current.Message.Character);
        foreach (var item in pending)
        {
            Assert.Contains(engine.NotificationHistory, d => d.Notification == item && !d.Allowed);
            Assert.Contains(engine.NotificationHistory, d => d.Notification == item && d.Allowed && d.Reason.Contains("Released"));
        }
        Assert.Equal(10, engine.NotificationHistory.Count);
        Assert.Equal(3, pending.Count);
        Assert.Equal(7, history.Count);
        Assert.Equal("focus-summary", (await engine.TickAsync(Start.AddSeconds(62))).Message?.Category);
        Assert.Null((await engine.TickAsync(Start.AddSeconds(64))).Message);
        Assert.Null(engine.EndFocus(Start.AddSeconds(64)));
        Assert.All(kinds, kind => Assert.True(engine.SimulateNotification(kind, Start.AddSeconds(64)).Allowed));
        engine.StartFocus(Start.AddSeconds(66));
        Assert.Equal(0, engine.Current!.UrgentNotifications);
        Assert.Equal(0, engine.Current.QueuedNotifications);
    }

    [Fact]
    public async Task CooldownIsGlobalAndExpiresAtSixtySecondsWithNormalPolling()
    {
        var engine = Synthetic(DemoScenario.PoorSleepDay);
        Assert.Equal("fatigue", (await engine.TickAsync(Start)).Message?.Category);
        Assert.All(await Ticks(engine, 2, 20), snapshot => Assert.Null(snapshot.Message));
        engine.SetScenario(DemoScenario.RecoveryAfterBreak);
        Assert.All(await Ticks(engine, 22, 58), snapshot => Assert.Null(snapshot.Message));
        Assert.Equal("recovery", (await engine.TickAsync(Start.AddSeconds(60))).Message?.Category);
        Assert.All(await Ticks(engine, 62, 118), snapshot => Assert.Null(snapshot.Message));
        Assert.Equal("recovery", (await engine.TickAsync(Start.AddSeconds(120))).Message?.Category);
    }

    [Fact]
    public async Task SnoozeClearsCurrentMessageCannotBeShortenedAndExpiresWithoutConsumingCooldown()
    {
        var engine = Synthetic(DemoScenario.PoorSleepDay);
        Assert.Equal("fatigue", (await engine.TickAsync(Start)).Message?.Category);
        engine.Snooze(TimeSpan.Zero, Start);
        Assert.NotNull(engine.Current!.Message);
        engine.Snooze(TimeSpan.FromSeconds(120), Start);
        Assert.Null(engine.Current.Message);
        await Ticks(engine, 2, 30);
        engine.Snooze(TimeSpan.FromSeconds(2), Start.AddSeconds(30));
        engine.Snooze(TimeSpan.Zero, Start.AddSeconds(30));
        Assert.All(await Ticks(engine, 32, 118), snapshot => Assert.Null(snapshot.Message));
        Assert.Equal("fatigue", (await engine.TickAsync(Start.AddSeconds(120))).Message?.Category);
        Assert.All(await Ticks(engine, 122, 178), snapshot => Assert.Null(snapshot.Message));
        Assert.Equal("fatigue", (await engine.TickAsync(Start.AddSeconds(180))).Message?.Category);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HydrationRespectsPreferencesAndThirtyMinuteIntervals(bool enabled)
    {
        var engine = Synthetic();
        engine.Preferences = new UserPreferences { HydrationReminders = enabled };
        Assert.All(await Ticks(engine, 0, 1798), snapshot => Assert.Null(snapshot.Message));
        var first = await engine.TickAsync(Start.AddSeconds(1800));
        Assert.Equal(enabled ? "hydration" : null, first.Message?.Category);
        Assert.All(await Ticks(engine, 1802, 3598), snapshot => Assert.Null(snapshot.Message));
        Assert.Equal(enabled ? "hydration" : null,
            (await engine.TickAsync(Start.AddSeconds(3600))).Message?.Category);
    }

    [Fact]
    public async Task BreathingAfterHighStressLowersSyntheticHeartRateAndStressForAtLeastSixtySeconds()
    {
        var engine = Synthetic(DemoScenario.RisingStress);
        var before = (await Ticks(engine, 0, 40))[^1];
        Assert.Equal(WellnessState.HighStress, before.Analysis.State);
        engine.CompleteBreathing(Start.AddSeconds(40));
        var immediate = engine.Current!;
        Assert.Equal(WellnessState.Recovering, immediate.Analysis.State);
        Assert.Equal(0, immediate.Analysis.BurnoutRiskScore);
        Assert.Null(immediate.Message);
        Assert.True(immediate.Wearable.StressLevel <= before.Wearable.StressLevel - 20);
        Assert.True(immediate.Wearable.HeartRate <= before.Wearable.HeartRate - 7);
        Assert.True(immediate.Analysis.StressScore < before.Analysis.StressScore);
        Assert.Contains(immediate.Analysis.Reasons, reason => reason.Contains("not a guaranteed physiological response"));
        Assert.Equal(WellnessState.HighStress, before.Analysis.State);
        engine.SetScenario(DemoScenario.MeetingOverload);
        foreach (var snapshot in await Ticks(engine, 42, 100))
        {
            Assert.Equal(WellnessState.Recovering, snapshot.Analysis.State);
            Assert.Equal(0, snapshot.Analysis.BurnoutRiskScore);
            Assert.True(snapshot.Wearable.StressLevel <= immediate.Wearable.StressLevel + 0.001);
            Assert.True(snapshot.Wearable.HeartRate <= immediate.Wearable.HeartRate + 0.001);
            Assert.True(snapshot.Analysis.StressScore < before.Analysis.StressScore);
        }
    }

    [Fact]
    public async Task BreathingDoesNotInventImprovedInjectedMeasurements()
    {
        var engine = new DemoEngine(new DelegateProvider((_, now, _) => Task.FromResult(High(now))));
        await Ticks(engine, 0, 40);
        engine.CompleteBreathing(Start.AddSeconds(40));
        Assert.Equal(High(Start.AddSeconds(40)), engine.Current!.Wearable);
        foreach (var snapshot in await Ticks(engine, 42, 100))
        {
            Assert.Equal(High(snapshot.Wearable.Timestamp), snapshot.Wearable);
            Assert.Equal(WellnessState.Recovering, snapshot.Analysis.State);
        }
    }

    [Fact]
    public async Task WalkingIncreasesStepsAndRecoveryWithoutErasingPoorSleepBurden()
    {
        var engine = Synthetic(DemoScenario.PoorSleepDay);
        var poor = (await Ticks(engine, 0, 20))[^1];
        engine.SetScenario(DemoScenario.RecoveryAfterBreak);
        var walking = await Ticks(engine, 22, 142);
        Assert.All(walking, snapshot =>
        {
            Assert.Equal(WellnessState.Recovering, snapshot.Analysis.State);
            Assert.Equal(poor.Wearable.SleepScore, snapshot.Wearable.SleepScore);
            Assert.Equal(poor.Wearable.TotalSleepMinutes, snapshot.Wearable.TotalSleepMinutes);
            Assert.Contains(snapshot.Analysis.Reasons, reason => reason.Contains("throughout this UTC day"));
        });
        for (int i = 1; i < walking.Count; i++)
            Assert.True(walking[i].Wearable.Steps >= walking[i - 1].Wearable.Steps);
        Assert.True(walking[^1].Wearable.Steps > walking[0].Wearable.Steps);
        Assert.True(walking[^1].Wearable.ActiveMinutes > walking[0].Wearable.ActiveMinutes);
        Assert.True(walking[^1].Wearable.RecoveryScore > walking[0].Wearable.RecoveryScore + 10);
        engine.SetScenario(DemoScenario.HealthyDay);
        var stopped = await engine.TickAsync(Start.AddSeconds(144));
        Assert.Equal(stopped.Wearable.Steps, (await Ticks(engine, 146, 164))[^1].Wearable.Steps);
    }

    [Fact]
    public async Task HistoryIsBoundedAtFiveHundredAndAllSnapshotsAreDetachedReadOnlyCollections()
    {
        var engine = Synthetic();
        var first = await engine.TickAsync(Start);
        var originalHistory = engine.History;
        var originalReasons = first.Analysis.Reasons.ToArray();
        var exported = engine.ExportState(Start, CharacterState.Normal);
        AssertReadOnly(first.Analysis.Reasons);
        AssertReadOnly(originalHistory);
        AssertReadOnly(exported.History);
        await Ticks(engine, 2, 1002);
        Assert.Equal(500, engine.History.Count);
        Assert.Equal(Start.AddSeconds(4), engine.History[0].Timestamp);
        Assert.Equal(Start.AddSeconds(1002), engine.History[^1].Timestamp);
        Assert.Single(originalHistory);
        Assert.Single(exported.History);
        Assert.Equal(Start, originalHistory[0].Timestamp);
        Assert.Equal(originalReasons, first.Analysis.Reasons);
        Assert.False(first.IsFocusActive);
        engine.StartFocus(Start.AddSeconds(1002));
        Assert.False(first.IsFocusActive);
        Assert.True(engine.Current!.IsFocusActive);
        await engine.TickAsync(Start.AddSeconds(1002).ToOffset(TimeSpan.FromHours(5.5)));
        Assert.Equal(500, engine.History.Count);
        AssertReadOnly(engine.History);

        var earliest = engine.SimulateNotification(NotificationKind.CriticalAlert, Start);
        var originalNotifications = engine.NotificationHistory;
        for (int i = 1; i <= 501; i++)
            engine.SimulateNotification(NotificationKind.CriticalAlert, Start.AddSeconds(i * 2));
        Assert.Equal(500, engine.NotificationHistory.Count);
        Assert.DoesNotContain(engine.NotificationHistory, d => d.Notification.Id == earliest.Notification.Id);
        Assert.Single(originalNotifications);
        AssertReadOnly(engine.NotificationHistory);
    }

    [Fact]
    public async Task DefaultApiAndInjectedProviderHonorContextTimestampAndCancellationToken()
    {
        var defaults = new DemoEngine();
        Assert.Null(defaults.Current);
        Assert.Equal(DemoScenario.HealthyDay, defaults.Scenario);
        Assert.False(defaults.IsFocusActive);
        Assert.Empty(defaults.History);
        Assert.Empty(defaults.PendingNotifications);
        Assert.Empty(defaults.NotificationHistory);
        Assert.Equal(new UserPreferences(), defaults.Preferences);
        Assert.Null(defaults.EndFocus(Start));
        Assert.Equal("morning-briefing", defaults.MorningBriefing().Category);
        Assert.Contains("not available", defaults.MorningBriefing().Text);
        AssertBounded(await defaults.TickAsync(Start));
        Assert.Throws<ArgumentNullException>(() => new DemoEngine(null!));

        using var cancellation = new CancellationTokenSource();
        var sample = Healthy(Start);
        int calls = 0;
        var engine = new DemoEngine(new DelegateProvider((context, now, token) =>
        {
            calls++;
            Assert.Equal(new MockWorkContextProvider().GetContext(DemoScenario.DeepFocusSession), context);
            Assert.Equal(Start, now);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult(sample);
        }));
        engine.SetScenario(DemoScenario.DeepFocusSession);
        engine.StartFocus(Start);
        var snapshot = await engine.TickAsync(Start, cancellation.Token);
        Assert.Same(sample, snapshot.Wearable);
        Assert.Equal(1, calls);
        Assert.Equal(WellnessState.Focused, snapshot.Analysis.State);
        engine.Preferences = new UserPreferences { DisplayName = "Taylor", PopupSeconds = 25 };
        var briefing = engine.MorningBriefing();
        Assert.Contains("Taylor", briefing.Title);
        Assert.Contains("8h 0m", briefing.Text);
        Assert.Contains("0 meetings and 1 priority tasks", briefing.Text);
    }

    [Fact]
    public async Task RestoreAndExportRoundTripDetachedStateAndResetTransientSessions()
    {
        var source = Synthetic(DemoScenario.PoorSleepDay);
        source.Preferences = new UserPreferences { DisplayName = "Taylor", HydrationReminders = false, PopupSeconds = 30 };
        await Ticks(source, 0, 60);
        var saved = source.ExportState(Start.AddSeconds(60), CharacterState.Encouraging);
        Assert.Equal(Start.AddSeconds(60), saved.LastSaved);
        Assert.Equal(CharacterState.Encouraging, saved.Character);
        Assert.Equal(source.Preferences, saved.Preferences);
        Assert.NotSame(source.Preferences, saved.Preferences);

        var engine = Synthetic(DemoScenario.RisingStress);
        await Ticks(engine, 0, 40);
        engine.StartFocus(Start.AddSeconds(40));
        engine.SimulateNotification(NotificationKind.FyiEmail, Start.AddSeconds(40));
        engine.Snooze(TimeSpan.FromHours(1), Start.AddSeconds(40));
        engine.CompleteBreathing(Start.AddSeconds(40));
        var mutableHistory = saved.History.ToList();
        engine.Restore(saved with { History = mutableHistory });
        mutableHistory.Clear();
        Assert.Null(engine.Current);
        Assert.False(engine.IsFocusActive);
        Assert.Empty(engine.PendingNotifications);
        Assert.Empty(engine.NotificationHistory);
        Assert.Equal(saved.Preferences, engine.Preferences);
        Assert.Equal(saved.Scenario, engine.Scenario);
        Assert.Equal(saved.History.ToArray(), engine.History.ToArray());
        AssertReadOnly(engine.History);
        var exported = engine.ExportState(Start.AddSeconds(62), CharacterState.Happy);
        Assert.Equal(saved.History.ToArray(), exported.History.ToArray());
        Assert.Equal(CharacterState.Happy, exported.Character);
        Assert.Equal(Start.AddSeconds(62), exported.LastSaved);
        var next = await engine.TickAsync(Start.AddSeconds(62));
        Assert.Equal(WellnessState.Fatigued, next.Analysis.State);
        Assert.Equal("fatigue", next.Message?.Category);
        Assert.Equal(0, next.Analysis.BurnoutRiskScore);
        Assert.Equal(saved.History.Count + 1, engine.History.Count);
        Assert.Equal(saved.History.Count, exported.History.Count);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("preferences")]
    [InlineData("history")]
    [InlineData("name-null")]
    [InlineData("name-long")]
    [InlineData("popup-low")]
    [InlineData("popup-high")]
    [InlineData("scenario")]
    [InlineData("character")]
    [InlineData("entry-null")]
    [InlineData("entry-scenario")]
    [InlineData("entry-state")]
    [InlineData("stress-nan")]
    [InlineData("stress-high")]
    [InlineData("focus-infinity")]
    [InlineData("focus-negative")]
    [InlineData("history-long")]
    public async Task InvalidRestoreIsAtomicAndPreservesFocusHistoryPreferencesAndSnooze(string invalidCase)
    {
        var engine = Synthetic(DemoScenario.PoorSleepDay);
        await engine.TickAsync(Start);
        engine.StartFocus(Start);
        engine.SimulateNotification(NotificationKind.GroupMessage, Start);
        engine.Snooze(TimeSpan.FromMinutes(10), Start);
        var current = engine.Current;
        var preferences = engine.Preferences;
        var history = engine.History.ToArray();
        var notifications = engine.NotificationHistory.ToArray();
        var pending = engine.PendingNotifications.ToArray();
        var valid = new PersistedAppState();
        var entry = new DemoHistoryEntry(Start, DemoScenario.HealthyDay, WellnessState.Balanced, 20, 50);
        PersistedAppState invalid = invalidCase switch
        {
            "state" => null!,
            "preferences" => valid with { Preferences = null! },
            "history" => valid with { History = null! },
            "name-null" => valid with { Preferences = new UserPreferences { DisplayName = null! } },
            "name-long" => valid with { Preferences = new UserPreferences { DisplayName = new string('x', 101) } },
            "popup-low" => valid with { Preferences = new UserPreferences { PopupSeconds = 0 } },
            "popup-high" => valid with { Preferences = new UserPreferences { PopupSeconds = 301 } },
            "scenario" => valid with { Scenario = (DemoScenario)999 },
            "character" => valid with { Character = (CharacterState)999 },
            "entry-null" => valid with { History = new DemoHistoryEntry[] { null! } },
            "entry-scenario" => valid with { History = new[] { entry with { Scenario = (DemoScenario)999 } } },
            "entry-state" => valid with { History = new[] { entry with { State = (WellnessState)999 } } },
            "stress-nan" => valid with { History = new[] { entry with { StressScore = double.NaN } } },
            "stress-high" => valid with { History = new[] { entry with { StressScore = 101 } } },
            "focus-infinity" => valid with { History = new[] { entry with { FocusScore = double.PositiveInfinity } } },
            "focus-negative" => valid with { History = new[] { entry with { FocusScore = -1 } } },
            "history-long" => valid with { History = Enumerable.Repeat(entry, 501).ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };
        Assert.Throws<JsonException>(() => engine.Restore(invalid));
        Assert.Same(current, engine.Current);
        Assert.Same(preferences, engine.Preferences);
        Assert.Equal(DemoScenario.PoorSleepDay, engine.Scenario);
        Assert.True(engine.IsFocusActive);
        Assert.Equal(history, engine.History.ToArray());
        Assert.Equal(notifications, engine.NotificationHistory.ToArray());
        Assert.Equal(pending, engine.PendingNotifications.ToArray());
        engine.EndFocus(Start.AddSeconds(2));
        Assert.Equal("focus-summary", (await engine.TickAsync(Start.AddSeconds(2))).Message?.Category);
        Assert.All(await Ticks(engine, 4, 62), snapshot => Assert.Null(snapshot.Message));
    }

    [Fact]
    public async Task InvalidPreferencesAndApiArgumentsDoNotMutateCurrentState()
    {
        var engine = Synthetic();
        var current = await engine.TickAsync(Start);
        var original = engine.Preferences;
        foreach (var invalid in new UserPreferences[]
        {
            null!, new() { DisplayName = null! }, new() { DisplayName = new string('x', 101) },
            new() { PopupSeconds = 0 }, new() { PopupSeconds = 301 }
        })
        {
            Assert.Throws<JsonException>(() => engine.Preferences = invalid);
            Assert.Same(original, engine.Preferences);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetScenario((DemoScenario)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SimulateNotification((NotificationKind)999, Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Snooze(TimeSpan.FromSeconds(-1), Start));
        Assert.Throws<JsonException>(() => engine.ExportState(Start, (CharacterState)999));
        Assert.Same(current, engine.Current);
        Assert.Single(engine.History);
        Assert.Empty(engine.NotificationHistory);
        Assert.Equal(DemoScenario.HealthyDay, engine.Scenario);
        foreach (var preference in new[]
        {
            new UserPreferences { DisplayName = "", PopupSeconds = 1 },
            new UserPreferences { DisplayName = new string('x', 100), PopupSeconds = 300 }
        })
        {
            engine.Preferences = preference;
            Assert.Equal(preference, engine.Preferences);
            Assert.NotSame(preference, engine.Preferences);
        }
    }

    [Fact]
    public async Task DuplicateAndBackwardTicksDoNotAdvanceHistoryOrCallProviderForBackwardTime()
    {
        int calls = 0;
        var engine = new DemoEngine(new DelegateProvider((_, now, _) =>
        {
            calls++;
            return Task.FromResult(High(now));
        }));
        engine.SetScenario(DemoScenario.MeetingOverload);
        await engine.TickAsync(Start);
        for (int i = 0; i < 20; i++)
        {
            var duplicate = await engine.TickAsync(Start);
            Assert.Equal(30, duplicate.Analysis.StressScore);
            Assert.Equal(0, duplicate.Analysis.BurnoutRiskScore);
        }
        Assert.Single(engine.History);
        var current = engine.Current;
        Assert.Equal(21, calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.TickAsync(Start.AddSeconds(-2)));
        Assert.Equal(21, calls);
        Assert.Same(current, engine.Current);
        await engine.TickAsync(Start.AddSeconds(2));
        Assert.Equal(2, engine.History.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousAndAsynchronousProviderFailuresPropagateWithoutCommitAndReleaseTickGate(bool synchronous)
    {
        var failure = new IOException("Injected wearable read failed.");
        int calls = 0;
        var engine = new DemoEngine(new DelegateProvider((_, now, _) =>
        {
            if (++calls == 2)
            {
                if (synchronous) throw failure;
                return Task.FromException<WearableSample>(failure);
            }
            return Task.FromResult(Healthy(now));
        }));
        var before = await engine.TickAsync(Start);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => engine.TickAsync(Start.AddSeconds(2))));
        Assert.Same(before, engine.Current);
        Assert.Single(engine.History);
        await engine.TickAsync(Start.AddSeconds(2)).WaitAsync(Timeout);
        Assert.Equal(2, engine.History.Count);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NullSampleIsRejectedWithoutCommitAndNextTickCanSucceed()
    {
        int calls = 0;
        var engine = new DemoEngine(new DelegateProvider((_, now, _) =>
            Task.FromResult(++calls == 1 ? null! : Healthy(now))));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.TickAsync(Start));
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        AssertBounded(await engine.TickAsync(Start).WaitAsync(Timeout));
        Assert.Single(engine.History);
    }

    [Fact]
    public async Task PreCancelledTickNeverCallsProviderAndProviderCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        var failure = new OperationCanceledException(cancellation.Token);
        var engine = new DemoEngine(new DelegateProvider((_, now, _) =>
        {
            calls++;
            return calls == 1 ? Task.FromException<WearableSample>(failure) : Task.FromResult(Healthy(now));
        }));
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TickAsync(Start, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TickAsync(Start)));
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        await engine.TickAsync(Start).WaitAsync(Timeout);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UiReadsAndMutationsDoNotBlockWhileBackgroundProviderIsAwaited()
    {
        var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        var tick = Task.Run(() => engine.TickAsync(Start));
        var read = await provider.NextRead();
        try
        {
            await Task.Run(() =>
            {
                Assert.Null(engine.Current);
                Assert.Equal(DemoScenario.HealthyDay, engine.Scenario);
                Assert.Empty(engine.History);
                engine.Preferences = new UserPreferences { DisplayName = "Concurrent UI" };
                engine.StartFocus(Start);
                Assert.True(engine.IsFocusActive);
                Assert.False(engine.SimulateNotification(NotificationKind.GroupMessage, Start).Allowed);
                Assert.True(engine.SimulateNotification(NotificationKind.CriticalAlert, Start).Allowed);
                Assert.Single(engine.PendingNotifications);
                Assert.Equal(2, engine.NotificationHistory.Count);
                engine.Snooze(TimeSpan.FromMinutes(5), Start);
                engine.CompleteBreathing(Start);
                Assert.Contains("Concurrent UI", engine.MorningBriefing().Title);
                Assert.Equal("Concurrent UI", engine.ExportState(Start, CharacterState.Focused).Preferences.DisplayName);
                Assert.Equal(new FocusSessionSummary(TimeSpan.Zero, 1, 1), engine.EndFocus(Start));
                engine.StartFocus(Start);
            }).WaitAsync(Timeout);
            Assert.False(tick.IsCompleted);
        }
        finally
        {
            read.Complete(Healthy(Start));
        }
        var snapshot = await tick.WaitAsync(Timeout);
        Assert.True(snapshot.IsFocusActive);
        Assert.Equal(0, snapshot.QueuedNotifications);
        Assert.Equal(0, snapshot.UrgentNotifications);
        Assert.Equal(WellnessState.Recovering, snapshot.Analysis.State);
        Assert.Single(engine.History);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScenarioChangeOrRestoreDuringPendingReadRetriesWithNewContextBeforeCommit(bool restore)
    {
        var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        using var cancellation = new CancellationTokenSource();
        var tick = engine.TickAsync(Start, cancellation.Token);
        var stale = await provider.NextRead();
        Assert.Equal(new MockWorkContextProvider().GetContext(DemoScenario.HealthyDay), stale.Context);
        try
        {
            await Task.Run(() =>
            {
                if (restore)
                    engine.Restore(new PersistedAppState { Scenario = DemoScenario.DeepFocusSession });
                else
                    engine.SetScenario(DemoScenario.DeepFocusSession);
                engine.StartFocus(Start);
            }).WaitAsync(Timeout);
        }
        finally
        {
            stale.Complete(High(Start) with { SleepScore = 20, TotalSleepMinutes = 240 });
        }
        var retry = await provider.NextRead();
        try
        {
            Assert.Equal(new MockWorkContextProvider().GetContext(DemoScenario.DeepFocusSession), retry.Context);
            Assert.Equal(Start, retry.Now);
            Assert.Equal(cancellation.Token, retry.Token);
            Assert.Null(engine.Current);
            Assert.Empty(engine.History);
        }
        finally
        {
            retry.Complete(Healthy(Start));
        }
        var snapshot = await tick.WaitAsync(Timeout);
        Assert.Equal(DemoScenario.DeepFocusSession, snapshot.Scenario);
        Assert.Equal(retry.Context, snapshot.Context);
        Assert.Equal(WellnessState.Focused, snapshot.Analysis.State);
        Assert.InRange(snapshot.Analysis.FatigueScore, 0, 40);
        Assert.Null(snapshot.Message);
        Assert.Single(engine.History);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CancelledPendingReadCannotPartiallyCommitAnalysisHistoryOrCooldown()
    {
        var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        var initial = engine.TickAsync(Start);
        (await provider.NextRead()).Complete(Healthy(Start));
        var before = await initial.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var tick = engine.TickAsync(Start.AddSeconds(60), cancellation.Token);
        var read = await provider.NextRead();
        Assert.Equal(cancellation.Token, read.Token);
        cancellation.Cancel();
        // The provider deliberately ignores cancellation; the engine must guard its own commit.
        read.Complete(High(read.Now) with { SleepScore = 20, TotalSleepMinutes = 240 });
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick.WaitAsync(Timeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(before, engine.Current);
        Assert.Single(engine.History);
        var next = engine.TickAsync(Start.AddSeconds(2));
        (await provider.NextRead()).Complete(Healthy(Start.AddSeconds(2)));
        var actual = await next.WaitAsync(Timeout);
        var control = new DemoEngine(new DelegateProvider((_, now, _) => Task.FromResult(Healthy(now))));
        await control.TickAsync(Start);
        var expected = await control.TickAsync(Start.AddSeconds(2));
        Assert.Equal(expected.Analysis.State, actual.Analysis.State);
        Assert.Equal(expected.Analysis.StressScore, actual.Analysis.StressScore);
        Assert.Equal(expected.Analysis.FatigueScore, actual.Analysis.FatigueScore);
        Assert.Equal(expected.Analysis.BurnoutRiskScore, actual.Analysis.BurnoutRiskScore);
        Assert.Equal(expected.Analysis.Reasons.ToArray(), actual.Analysis.Reasons.ToArray());
        Assert.Equal(2, engine.History.Count);
        var fatigue = engine.TickAsync(Start.AddSeconds(4));
        (await provider.NextRead()).Complete(Healthy(Start.AddSeconds(4)) with { SleepScore = 20 });
        Assert.Equal("fatigue", (await fatigue.WaitAsync(Timeout)).Message?.Category);
    }

    [Fact]
    public async Task CancellationDoesNotConsumePendingFocusSummary()
    {
        var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        engine.StartFocus(Start);
        engine.SimulateNotification(NotificationKind.FyiEmail, Start);
        engine.EndFocus(Start.AddSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var tick = engine.TickAsync(Start.AddSeconds(2), cancellation.Token);
        var read = await provider.NextRead();
        cancellation.Cancel();
        read.Complete(Healthy(read.Now));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick.WaitAsync(Timeout));
        Assert.Null(engine.Current);
        Assert.Empty(engine.History);
        var retry = engine.TickAsync(Start.AddSeconds(2));
        (await provider.NextRead()).Complete(Healthy(Start.AddSeconds(2)));
        Assert.Equal("focus-summary", (await retry.WaitAsync(Timeout)).Message?.Category);
    }

    [Fact]
    public async Task SimultaneousTicksSerializeProviderReadsAndCommitInOrder()
    {
        var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        var first = engine.TickAsync(Start);
        var firstRead = await provider.NextRead();
        var second = engine.TickAsync(Start.AddSeconds(2));
        var third = engine.TickAsync(Start.AddSeconds(4));
        try
        {
            Assert.Equal(1, provider.Calls);
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
            Assert.Empty(engine.History);
        }
        finally
        {
            firstRead.Complete(Healthy(Start));
        }
        await first.WaitAsync(Timeout);
        var secondRead = await provider.NextRead();
        try
        {
            Assert.Equal(Start.AddSeconds(2), secondRead.Now);
            Assert.Equal(2, provider.Calls);
            Assert.Single(engine.History);
            Assert.False(third.IsCompleted);
        }
        finally
        {
            secondRead.Complete(Healthy(secondRead.Now));
        }
        await second.WaitAsync(Timeout);
        var thirdRead = await provider.NextRead();
        thirdRead.Complete(Healthy(thirdRead.Now));
        var snapshots = await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        Assert.Equal(Start.AddSeconds(4), thirdRead.Now);
        Assert.Equal(1, provider.MaximumConcurrentReads);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(new[] { Start, Start.AddSeconds(2), Start.AddSeconds(4) }, engine.History.Select(h => h.Timestamp));
        Assert.Same(snapshots[^1], engine.Current);
    }

    [Fact]
    public async Task CancellingQueuedTickDoesNotCallProviderOrCancelActiveTick()
    {
        var provider = new ControlledProvider();
        var engine = new DemoEngine(provider);
        var active = engine.TickAsync(Start);
        var read = await provider.NextRead();
        using var cancellation = new CancellationTokenSource();
        var queued = engine.TickAsync(Start.AddSeconds(2), cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
            Assert.Equal(1, provider.Calls);
            Assert.False(active.IsCompleted);
            Assert.Empty(engine.History);
        }
        finally
        {
            read.Complete(Healthy(Start));
        }
        await active.WaitAsync(Timeout);
        var next = engine.TickAsync(Start.AddSeconds(4));
        (await provider.NextRead()).Complete(Healthy(Start.AddSeconds(4)));
        await next.WaitAsync(Timeout);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(new[] { Start, Start.AddSeconds(4) }, engine.History.Select(h => h.Timestamp));
    }

    private sealed class DelegateProvider(
        Func<WorkContext, DateTimeOffset, CancellationToken, Task<WearableSample>> read) : IWearableProvider
    {
        public string Name => "Deterministic integration provider";
        public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
            CancellationToken cancellationToken = default) => read(context, now, cancellationToken);
    }

    private sealed class PendingRead(WorkContext context, DateTimeOffset now, CancellationToken token)
    {
        private readonly TaskCompletionSource<WearableSample> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkContext Context { get; } = context;
        public DateTimeOffset Now { get; } = now;
        public CancellationToken Token { get; } = token;
        public Task<WearableSample> Result => _completion.Task;
        public void Complete(WearableSample sample) => _completion.TrySetResult(sample);
    }

    private sealed class ControlledProvider : IWearableProvider
    {
        private readonly Channel<PendingRead> _reads = Channel.CreateUnbounded<PendingRead>();
        private int _calls;
        private int _active;
        private int _maximum;
        public string Name => "Manually completed asynchronous integration provider";
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
            var pending = new PendingRead(context, now, cancellationToken);
            try
            {
                Assert.True(_reads.Writer.TryWrite(pending));
                return await pending.Result.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
