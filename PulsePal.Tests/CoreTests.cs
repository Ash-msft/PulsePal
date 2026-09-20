using PulsePal.Core;

namespace PulsePal.Tests;

public sealed class CoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private static WearableSample Healthy(double seconds = 0) => new()
    {
        Timestamp = Now.AddSeconds(seconds), HeartRate = 70, RestingHeartRate = 60,
        HeartRateVariability = 65, StressLevel = 20, SleepScore = 85, TotalSleepMinutes = 480,
        BodyBattery = 80, ReadinessScore = 85, RecoveryScore = 85
    };

    private static WearableSample High(double seconds = 0) => Healthy(seconds) with
    {
        HeartRate = 110, HeartRateVariability = 15, StressLevel = 95
    };

    private static AnalysisResult Feed(WellnessAnalyzer analyzer, WearableSample sample, WorkContext context,
        int duration, int interval = 1)
    {
        var result = analyzer.Analyze(sample, context);
        for (int second = interval; second <= duration; second += interval)
            result = analyzer.Analyze(sample with { Timestamp = sample.Timestamp.AddSeconds(second) }, context);
        return result;
    }

    private static void Bounded(AnalysisResult result)
    {
        foreach (var score in new[] { result.FocusScore, result.StressScore, result.FatigueScore, result.BurnoutRiskScore })
        {
            Assert.True(double.IsFinite(score));
            Assert.InRange(score, 0, 100);
        }
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void AnalyzerRejectsNullInputs()
    {
        var analyzer = new WellnessAnalyzer();
        Assert.Throws<ArgumentNullException>(() => analyzer.Analyze(null!, new WorkContext()));
        Assert.Throws<ArgumentNullException>(() => analyzer.Analyze(Healthy(), null!));
    }

    [Fact]
    public void HealthyDayIsBalancedAndExplainable()
    {
        var result = new WellnessAnalyzer().Analyze(Healthy(), new WorkContext());
        Bounded(result);
        Assert.Equal(WellnessState.Balanced, result.State);
        Assert.InRange(result.StressScore, 0, 40);
        Assert.InRange(result.FatigueScore, 0, 40);
        Assert.Equal(0, result.BurnoutRiskScore);
        Assert.Contains(result.Reasons, reason => reason.Contains("Non-medical"));
    }

    [Fact]
    public void FocusIgnoresAllWearableValuesAndUsesWorkContext()
    {
        var context = new WorkContext();
        var low = new WellnessAnalyzer().Analyze(Healthy() with { FocusScore = 0 }, context);
        var high = new WellnessAnalyzer().Analyze(High() with { FocusScore = 100, SleepScore = 20 }, context);
        var invalid = new WellnessAnalyzer().Analyze(Healthy() with { FocusScore = double.NaN }, context);
        Assert.Equal(low.FocusScore, high.FocusScore);
        Assert.Equal(low.FocusScore, invalid.FocusScore);
        var focused = new WellnessAnalyzer().Analyze(Healthy(), context, focusActive: true);
        Assert.True(focused.FocusScore > low.FocusScore);
        Assert.Equal(WellnessState.Focused, focused.State);
        var interrupted = new WellnessAnalyzer().Analyze(Healthy(), context with { AppSwitchCount = 12, UpcomingMeetings = 3 });
        Assert.True(interrupted.FocusScore < low.FocusScore);
        var idle = new WellnessAnalyzer().Analyze(Healthy(), context with { IdleTime = TimeSpan.FromMinutes(10) }, true);
        Assert.InRange(idle.FocusScore, 0, 20);
        Assert.NotEqual(WellnessState.Focused, idle.State);
    }

    [Fact]
    public void MissingMetricsRemainNeutralEvenUnderHeavyWorkload()
    {
        var context = new WorkContext { MeetingsToday = 100, AppSwitchCount = 100, EscalationCount = 100 };
        var result = Feed(new WellnessAnalyzer(), new WearableSample { Timestamp = Now }, context, 180);
        Bounded(result);
        Assert.InRange(result.StressScore, 0, 55);
        Assert.Equal(0, result.BurnoutRiskScore);
        Assert.DoesNotContain(result.State, new[] { WellnessState.Stressed, WellnessState.HighStress, WellnessState.BurnoutRisk });
        Assert.Contains(result.Reasons, reason => reason.Contains("Heart rate missing"));
        Assert.Contains(result.Reasons, reason => reason.Contains("Sleep score missing"));
        Assert.Contains(result.Reasons, reason => reason.Contains("neutral estimate"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1000)]
    [InlineData(double.MaxValue)]
    public void InvalidMetricsAndExtremeContextHaveBoundedScores(double value)
    {
        var sample = new WearableSample
        {
            Timestamp = Now, HeartRate = value, RestingHeartRate = value,
            HeartRateVariability = value, StressLevel = value, SleepScore = value,
            TotalSleepMinutes = int.MaxValue, BodyBattery = value, ReadinessScore = value,
            RecoveryScore = value, FocusScore = value, ActiveMinutes = int.MinValue
        };
        var context = new WorkContext
        {
            AppSwitchCount = int.MaxValue, MeetingsToday = int.MaxValue,
            UpcomingMeetings = int.MaxValue, EscalationCount = int.MaxValue,
            UnreadImportantItems = int.MaxValue, PriorityTasks = int.MaxValue, IdleTime = TimeSpan.MaxValue
        };
        var result = Feed(new WellnessAnalyzer(), sample, context, 90);
        Bounded(result);
        Assert.InRange(result.StressScore, 0, 55);
        Assert.Equal(0, result.BurnoutRiskScore);
        Assert.Contains(result.Reasons, reason => reason.Contains("invalid"));
    }

    [Fact]
    public void NegativeContextIsTreatedAsZero()
    {
        var normal = new WellnessAnalyzer().Analyze(Healthy(), new WorkContext());
        var negative = new WellnessAnalyzer().Analyze(Healthy(), new WorkContext
        {
            AppSwitchCount = -1, MeetingsToday = -1, UpcomingMeetings = -1,
            EscalationCount = -1, UnreadImportantItems = -1, IdleTime = TimeSpan.MinValue
        });
        Assert.Equal(normal.FocusScore, negative.FocusScore);
        Assert.Equal(normal.StressScore, negative.StressScore);
        Assert.Equal(normal.FatigueScore, negative.FatigueScore);
    }

    [Fact]
    public void StressRequiresElapsedHighSamplesAndRampsWithinFortySeconds()
    {
        var analyzer = new WellnessAnalyzer();
        var context = new WorkContext();
        for (int i = 0; i < 100; i++)
        {
            var duplicate = analyzer.Analyze(High(), context);
            Assert.Equal(30, duplicate.StressScore);
            Assert.Equal(WellnessState.Balanced, duplicate.State);
        }
        var eleven = analyzer.Analyze(High(11), context);
        Assert.NotEqual(WellnessState.Stressed, eleven.State);
        Assert.NotEqual(WellnessState.HighStress, eleven.State);
        var twelve = analyzer.Analyze(High(12), context);
        Assert.Contains(twelve.Reasons, reason => reason.Contains("sustained for at least 12 seconds"));
        var twentyFour = analyzer.Analyze(High(24), context);
        Assert.Equal(WellnessState.Stressed, twentyFour.State);
        var forty = analyzer.Analyze(High(40), context);
        Assert.Equal(WellnessState.HighStress, forty.State);
        Assert.True(forty.StressScore > twentyFour.StressScore);
    }

    [Fact]
    public void StressRampUsesTimeNotSampleCount()
    {
        var dense = Feed(new WellnessAnalyzer(), High(), new WorkContext(), 30, 1);
        var sparse = Feed(new WellnessAnalyzer(), High(), new WorkContext(), 30, 10);
        Assert.Equal(dense.StressScore, sparse.StressScore);
        Assert.Equal(dense.State, sparse.State);
    }

    [Fact]
    public void ElevatedHeartRateAloneOrLowHrvAloneCannotEstablishHighStress()
    {
        foreach (var sample in new[]
        {
            Healthy() with { HeartRate = 130, StressLevel = null },
            Healthy() with { HeartRateVariability = 10, StressLevel = null },
            Healthy() with { HeartRate = 130, RestingHeartRate = null, StressLevel = null }
        })
        {
            var result = Feed(new WellnessAnalyzer(), sample, new WorkContext(), 90);
            Assert.InRange(result.StressScore, 0, 55);
            Assert.Equal(0, result.BurnoutRiskScore);
        }
    }

    [Fact]
    public void CorrelatedCuesIncreaseStressAndActivityDiscountsHeartRate()
    {
        var baseline = Feed(new WellnessAnalyzer(), Healthy() with { StressLevel = 70 }, new WorkContext(), 40);
        var correlated = Feed(new WellnessAnalyzer(), High() with { StressLevel = 70 }, new WorkContext(), 40);
        var active = Feed(new WellnessAnalyzer(), High() with { StressLevel = 70, ActiveMinutes = 40 }, new WorkContext(), 40);
        Assert.True(correlated.StressScore > baseline.StressScore);
        Assert.True(active.StressScore < correlated.StressScore);
        Assert.Contains(active.Reasons, reason => reason.Contains("activity may explain"));
        var unreported = Feed(new WellnessAnalyzer(), High() with { StressLevel = null }, new WorkContext(), 40);
        Assert.Equal(WellnessState.Stressed, unreported.State);
        Assert.Contains(unreported.Reasons, reason => reason.Contains("correlate"));
    }

    [Fact]
    public void StressRampsWithinFortySecondsEvenAfterRecovery()
    {
        var analyzer = new WellnessAnalyzer();
        var recovered = analyzer.Analyze(Healthy() with { StressLevel = 0 }, new WorkContext(), recovering: true);
        Assert.Equal(0, recovered.StressScore);
        var stressed = Feed(analyzer, High(1), new WorkContext(), 40);
        Assert.Contains(stressed.State, new[] { WellnessState.Stressed, WellnessState.HighStress });
    }

    [Fact]
    public void LowSampleAndRecoveryInterruptSustainedStress()
    {
        var analyzer = new WellnessAnalyzer();
        Feed(analyzer, High(), new WorkContext(), 40);
        var low = analyzer.Analyze(Healthy(41), new WorkContext());
        Assert.Equal(WellnessState.Balanced, low.State);
        var spike = analyzer.Analyze(High(42), new WorkContext());
        Assert.NotEqual(WellnessState.HighStress, spike.State);
        var recovering = analyzer.Analyze(High(43), new WorkContext(), recovering: true);
        Assert.Equal(WellnessState.Recovering, recovering.State);
        Assert.Equal(0, recovering.BurnoutRiskScore);
        var next = analyzer.Analyze(High(44), new WorkContext());
        Assert.Contains(next.Reasons, reason => reason.Contains("not yet sustained"));
    }

    [Fact]
    public void PoorSleepPersistsAllDayEvenThroughRecoveryAndClearsNextUtcDay()
    {
        var analyzer = new WellnessAnalyzer();
        var poor = analyzer.Analyze(Healthy() with { SleepScore = 30, TotalSleepMinutes = 240 }, new WorkContext());
        Assert.Equal(WellnessState.Fatigued, poor.State);
        var afternoon = analyzer.Analyze(Healthy() with
        {
            Timestamp = Now.AddHours(8), SleepScore = null, TotalSleepMinutes = null
        }, new WorkContext(), recovering: true);
        Assert.Equal(poor.FatigueScore, afternoon.FatigueScore);
        Assert.Equal(WellnessState.Recovering, afternoon.State);
        Assert.Contains(afternoon.Reasons, reason => reason.Contains("throughout this UTC day"));
        var improvedReport = analyzer.Analyze(Healthy() with { Timestamp = Now.AddHours(9) }, new WorkContext());
        Assert.Equal(poor.FatigueScore, improvedReport.FatigueScore);
        var tomorrow = analyzer.Analyze(Healthy() with
        {
            Timestamp = Now.AddDays(1), SleepScore = null, TotalSleepMinutes = null
        }, new WorkContext());
        Assert.True(tomorrow.FatigueScore < poor.FatigueScore);
        Assert.Equal(WellnessState.Balanced, tomorrow.State);
    }

    [Fact]
    public void FatigueUsesSleepDurationAndEnergyNotFocusScore()
    {
        var sample = Healthy() with { SleepScore = null, TotalSleepMinutes = 240, BodyBattery = 10, ReadinessScore = 20, RecoveryScore = 20 };
        var result = new WellnessAnalyzer().Analyze(sample, new WorkContext());
        Assert.Equal(WellnessState.Fatigued, result.State);
        Assert.InRange(result.FatigueScore, 65, 100);
        Assert.Equal(0, result.BurnoutRiskScore);
        Bounded(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BurnoutNeedsSixtySecondsPlusSleepOrWorkRisk(bool sleepRisk)
    {
        var analyzer = new WellnessAnalyzer();
        var sample = sleepRisk ? High() with { SleepScore = 30 } : High();
        var context = sleepRisk ? new WorkContext() : new WorkContext { MeetingsToday = 9 };
        var before = Feed(analyzer, sample, context, 59);
        Assert.NotEqual(WellnessState.BurnoutRisk, before.State);
        Assert.InRange(before.BurnoutRiskScore, 0, 49);
        var after = analyzer.Analyze(sample with { Timestamp = Now.AddSeconds(60) }, context);
        Assert.Equal(WellnessState.BurnoutRisk, after.State);
        Assert.InRange(after.BurnoutRiskScore, 65, 100);
        Assert.Contains(after.Reasons, reason => reason.Contains("at least 60 seconds plus"));
    }

    [Fact]
    public void StressAloneAndPoorSleepAloneNeverCreateBurnout()
    {
        var stressed = Feed(new WellnessAnalyzer(), High(), new WorkContext(), 180);
        var tired = Feed(new WellnessAnalyzer(), Healthy() with { SleepScore = 10 }, new WorkContext(), 180);
        Assert.NotEqual(WellnessState.BurnoutRisk, stressed.State);
        Assert.NotEqual(WellnessState.BurnoutRisk, tired.State);
        Assert.Equal(0, stressed.BurnoutRiskScore);
        Assert.Equal(0, tired.BurnoutRiskScore);
    }

    [Fact]
    public void BurnoutTimingRestartsAfterLowSampleOrLostWorkRisk()
    {
        var analyzer = new WellnessAnalyzer();
        var busy = new WorkContext { MeetingsToday = 9 };
        Feed(analyzer, High(), busy, 50);
        analyzer.Analyze(High(51), new WorkContext());
        var restarted = analyzer.Analyze(High(52), busy);
        Assert.Equal(0, restarted.BurnoutRiskScore);
        analyzer.Analyze(Healthy(53), busy);
        var again = analyzer.Analyze(High(60), busy);
        Assert.Equal(0, again.BurnoutRiskScore);
        Assert.NotEqual(WellnessState.BurnoutRisk, again.State);
    }

    [Fact]
    public void GapsAndOutOfOrderSamplesCannotFabricateSustainedRisk()
    {
        var analyzer = new WellnessAnalyzer();
        var busy = new WorkContext { MeetingsToday = 9 };
        var result = Feed(analyzer, High(), busy, 30);
        var old = analyzer.Analyze(High(-1000), busy);
        Assert.Equal(result.StressScore, old.StressScore);
        Assert.Contains(old.Reasons, reason => reason.Contains("Out-of-order"));
        var gap = analyzer.Analyze(High(1000), busy);
        Assert.Equal(0, gap.BurnoutRiskScore);
        Assert.Equal(30, gap.StressScore);
        Assert.Contains(gap.Reasons, reason => reason.Contains("Observation gap"));
        Assert.NotEqual(WellnessState.BurnoutRisk, gap.State);
    }

    [Fact]
    public void EquivalentUtcTimestampsDoNotAdvanceTiming()
    {
        var analyzer = new WellnessAnalyzer();
        var first = analyzer.Analyze(High(), new WorkContext());
        var same = analyzer.Analyze(High() with { Timestamp = Now.ToOffset(TimeSpan.FromHours(5)) }, new WorkContext());
        Assert.Equal(first.StressScore, same.StressScore);
        Assert.Equal(first.BurnoutRiskScore, same.BurnoutRiskScore);
    }

    [Fact]
    public void AnalyzerResetRestoresDeterminismAndReasonsCannotBeMutated()
    {
        var analyzer = new WellnessAnalyzer();
        var first = analyzer.Analyze(Healthy(), new WorkContext());
        var reasons = first.Reasons.ToArray();
        Assert.Throws<NotSupportedException>(() => ((IList<string>)first.Reasons).Add("changed"));
        Feed(analyzer, High(1) with { SleepScore = 20 }, new WorkContext(), 80);
        Assert.Equal(reasons, first.Reasons);
        analyzer.Reset();
        analyzer.Reset();
        var reset = analyzer.Analyze(Healthy(), new WorkContext());
        Assert.Equal(first.FocusScore, reset.FocusScore);
        Assert.Equal(first.StressScore, reset.StressScore);
        Assert.Equal(first.FatigueScore, reset.FatigueScore);
        Assert.Equal(first.State, reset.State);
        Assert.Equal(first.Reasons, reset.Reasons);
    }

    private static AttentionNotification Notification(NotificationKind kind) => new(Guid.NewGuid(), Now, kind, "Demo notification");

    [Theory]
    [InlineData(NotificationKind.ManagerMessage, true)]
    [InlineData(NotificationKind.Escalation, true)]
    [InlineData(NotificationKind.CriticalAlert, true)]
    [InlineData(NotificationKind.MeetingReminder, true)]
    [InlineData(NotificationKind.FyiEmail, false)]
    [InlineData(NotificationKind.GroupMessage, false)]
    [InlineData(NotificationKind.LowPriorityAlert, false)]
    [InlineData((NotificationKind)999, false)]
    public void AttentionFocusUsesExactPriorityAllowlist(NotificationKind kind, bool allowed)
    {
        var shield = new AttentionShield();
        var notification = Notification(kind);
        Assert.True(shield.Decide(notification).Allowed);
        Assert.Equal(0, shield.UrgentCount);
        shield.StartFocus(Now);
        var decision = shield.Decide(notification);
        Assert.Equal(allowed, decision.Allowed);
        Assert.Same(notification, decision.Notification);
        Assert.Equal(allowed ? 1 : 0, shield.UrgentCount);
        Assert.Equal(allowed ? 0 : 1, shield.PendingNotifications.Count);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void FocusEndReleasesQueueAndPreservesOriginalDecisions()
    {
        var shield = new AttentionShield();
        shield.StartFocus(Now);
        var first = shield.Decide(Notification(NotificationKind.FyiEmail));
        var second = shield.Decide(Notification(NotificationKind.GroupMessage));
        shield.Decide(Notification(NotificationKind.ManagerMessage));
        shield.Decide(Notification(NotificationKind.MeetingReminder));
        var summary = shield.EndFocus(Now.AddMinutes(5));
        Assert.NotNull(summary);
        Assert.Equal(TimeSpan.FromMinutes(5), summary.Duration);
        Assert.Equal(2, summary.DelayedCount);
        Assert.Equal(2, summary.UrgentCount);
        Assert.Equal(2, shield.UrgentCount);
        Assert.False(shield.IsFocusActive);
        Assert.Null(shield.FocusStartedAt);
        Assert.Empty(shield.PendingNotifications);
        Assert.Equal(6, shield.History.Count);
        Assert.Same(first, shield.History[0]);
        Assert.False(first.Allowed);
        Assert.False(second.Allowed);
        Assert.True(shield.History[4].Allowed);
        Assert.True(shield.History[5].Allowed);
        Assert.Same(first.Notification, shield.History[4].Notification);
        Assert.Same(second.Notification, shield.History[5].Notification);
        Assert.Contains("Released", shield.History[4].Reason);
        Assert.Null(shield.EndFocus(Now.AddMinutes(6)));
        Assert.Equal(6, shield.History.Count);
    }

    [Fact]
    public void FocusRepeatedStartDoesNotLoseSessionAndNextSessionResetsCounts()
    {
        var shield = new AttentionShield();
        Assert.Null(shield.EndFocus(Now));
        shield.StartFocus(Now);
        shield.Decide(Notification(NotificationKind.FyiEmail));
        shield.Decide(Notification(NotificationKind.Escalation));
        shield.StartFocus(Now.AddMinutes(2));
        Assert.Equal(Now, shield.FocusStartedAt);
        Assert.Equal(1, shield.UrgentCount);
        Assert.Single(shield.PendingNotifications);
        var summary = shield.EndFocus(Now.AddMinutes(-1));
        Assert.Equal(TimeSpan.Zero, summary!.Duration);
        shield.StartFocus(Now.AddMinutes(3));
        Assert.Equal(0, shield.UrgentCount);
        Assert.Empty(shield.PendingNotifications);
        Assert.Equal(0, shield.EndFocus(Now.AddMinutes(4))!.DelayedCount);
    }

    [Fact]
    public void AttentionHistoryIsBoundedWithoutDroppingPendingNotifications()
    {
        var shield = new AttentionShield();
        shield.StartFocus(Now);
        var first = shield.Decide(Notification(NotificationKind.FyiEmail));
        for (int i = 1; i < 620; i++)
            shield.Decide(Notification(NotificationKind.FyiEmail));
        Assert.Equal(500, shield.History.Count);
        Assert.Equal(620, shield.PendingNotifications.Count);
        Assert.Same(first.Notification, shield.PendingNotifications[0]);
        var summary = shield.EndFocus(Now.AddHours(1));
        Assert.Equal(620, summary!.DelayedCount);
        Assert.Empty(shield.PendingNotifications);
        Assert.Equal(500, shield.History.Count);
        Assert.All(shield.History, decision =>
        {
            Assert.True(decision.Allowed);
            Assert.Contains("Released", decision.Reason);
        });
        Assert.False(first.Allowed);
    }

    [Fact]
    public void AttentionCollectionsAreImmutableSnapshotsAndResetClearsEverything()
    {
        var shield = new AttentionShield();
        Assert.Throws<ArgumentNullException>(() => shield.Decide(null!));
        shield.StartFocus(Now);
        shield.Decide(Notification(NotificationKind.FyiEmail));
        var pending = shield.PendingNotifications;
        var history = shield.History;
        Assert.Throws<NotSupportedException>(() => ((IList<AttentionNotification>)pending).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NotificationDecision>)history).Clear());
        shield.Decide(Notification(NotificationKind.CriticalAlert));
        shield.Decide(Notification(NotificationKind.GroupMessage));
        Assert.Single(pending);
        Assert.Single(history);
        shield.Reset();
        shield.Reset();
        Assert.False(shield.IsFocusActive);
        Assert.Null(shield.FocusStartedAt);
        Assert.Empty(shield.PendingNotifications);
        Assert.Empty(shield.History);
        Assert.Equal(0, shield.UrgentCount);
        Assert.Single(pending);
        Assert.Single(history);
    }

    [Fact]
    public void DefaultCooldownIsGlobalAndCategoryScopedWithExactBoundary()
    {
        var policy = new CooldownPolicy();
        Assert.True(policy.TryAllow("stress", Now));
        Assert.False(policy.TryAllow("stress", Now.AddSeconds(59.999)));
        Assert.False(policy.TryAllow("fatigue", Now.AddSeconds(59.999)));
        Assert.True(policy.TryAllow("STRESS", Now.AddSeconds(60)));
        Assert.False(policy.TryAllow("fatigue", Now.AddSeconds(61)));
        Assert.True(policy.TryAllow("fatigue", Now.AddSeconds(120)));
        Assert.False(policy.TryAllow("stress", Now.AddSeconds(121)));
    }

    [Fact]
    public void DeniedCooldownAttemptsDoNotConsumeAnyWindow()
    {
        var policy = new CooldownPolicy(TimeSpan.FromSeconds(90));
        Assert.True(policy.TryAllow("one", Now));
        for (int second = 1; second < 90; second++)
            Assert.False(policy.TryAllow("two", Now.AddSeconds(second)));
        Assert.True(policy.TryAllow("two", Now.AddSeconds(90)));
        Assert.True(policy.TryAllow("one", Now.AddSeconds(180)));
    }

    [Fact]
    public void SnoozeBlocksAllCategoriesExtendsAndDoesNotConsumeCooldown()
    {
        var policy = new CooldownPolicy();
        policy.Snooze(TimeSpan.FromMinutes(2), Now);
        Assert.False(policy.TryAllow("stress", Now));
        Assert.False(policy.TryAllow("fatigue", Now.AddSeconds(60)));
        policy.Snooze(TimeSpan.FromSeconds(1), Now.AddSeconds(60));
        policy.Snooze(TimeSpan.Zero, Now.AddSeconds(60));
        Assert.False(policy.TryAllow("hydration", Now.AddSeconds(119)));
        policy.Snooze(TimeSpan.FromMinutes(2), Now.AddSeconds(100));
        Assert.False(policy.TryAllow("stress", Now.AddSeconds(219)));
        Assert.True(policy.TryAllow("stress", Now.AddSeconds(220)));
        Assert.False(policy.TryAllow("fatigue", Now.AddSeconds(221)));
        Assert.True(policy.TryAllow("fatigue", Now.AddSeconds(280)));
    }

    [Fact]
    public void SnoozeCannotBypassExistingCooldown()
    {
        var policy = new CooldownPolicy();
        Assert.True(policy.TryAllow("stress", Now));
        policy.Snooze(TimeSpan.FromSeconds(10), Now.AddSeconds(1));
        Assert.False(policy.TryAllow("stress", Now.AddSeconds(11)));
        Assert.True(policy.TryAllow("stress", Now.AddSeconds(60)));
    }

    [Fact]
    public void CooldownValidatesDurationsCategoriesAndBackwardClock()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CooldownPolicy(TimeSpan.FromTicks(-1)));
        var policy = new CooldownPolicy(TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Snooze(TimeSpan.FromTicks(-1), Now));
        foreach (var category in new[] { null, "", "  " })
            Assert.Throws<ArgumentException>(() => policy.TryAllow(category!, Now));
        Assert.True(policy.TryAllow("stress", Now));
        Assert.True(policy.TryAllow("stress", Now));
        Assert.False(policy.TryAllow("fatigue", Now.AddTicks(-1)));
        Assert.True(policy.TryAllow("fatigue", Now));
    }

    [Fact]
    public void CooldownResetClearsSnoozeAndAllowHistory()
    {
        var policy = new CooldownPolicy();
        Assert.True(policy.TryAllow("stress", Now));
        policy.Snooze(TimeSpan.FromDays(1), Now);
        policy.Reset();
        policy.Reset();
        Assert.True(policy.TryAllow("stress", Now));
    }

    [Fact]
    public void SnoozeAndCooldownHandleTimestampExtremesWithoutOverflow()
    {
        var policy = new CooldownPolicy(TimeSpan.MaxValue);
        Assert.True(policy.TryAllow("stress", DateTimeOffset.MinValue));
        Assert.False(policy.TryAllow("stress", DateTimeOffset.MaxValue));
        policy.Reset();
        var nearEnd = DateTimeOffset.MaxValue.AddSeconds(-1);
        policy.Snooze(TimeSpan.MaxValue, nearEnd);
        Assert.False(policy.TryAllow("stress", nearEnd));
        Assert.True(policy.TryAllow("stress", DateTimeOffset.MaxValue));
    }

    [Fact]
    public void ConcurrentAttentionAndCooldownCallsPreserveState()
    {
        var shield = new AttentionShield();
        shield.StartFocus(Now);
        Parallel.For(0, 600, _ => shield.Decide(Notification(NotificationKind.FyiEmail)));
        Assert.Equal(600, shield.PendingNotifications.Count);
        Assert.Equal(500, shield.History.Count);
        Assert.Equal(600, shield.EndFocus(Now.AddMinutes(1))!.DelayedCount);
        var policy = new CooldownPolicy();
        int allowed = 0;
        Parallel.For(0, 100, i =>
        {
            if (policy.TryAllow(i.ToString(), Now))
                Interlocked.Increment(ref allowed);
        });
        Assert.Equal(1, allowed);
    }
}
