using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class RecoveryEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RecoveryActivity.Breathing, 22, 8)]
    [InlineData(RecoveryActivity.ScreenBreak, 30, 10)]
    [InlineData(RecoveryActivity.WaterBreak, 10, 3)]
    [InlineData(RecoveryActivity.StretchBreak, 16, 5)]
    public async Task ActivitiesGiveDistinctCorrelatedBoundedSyntheticBenefitsThatPersistAndExpire(
        RecoveryActivity activity, double stressReduction, double heartRateReduction)
    {
        var engine = Synthetic();
        var control = Synthetic();
        await Feed(engine, 0, 80);
        await Feed(control, 0, 80);
        var before = engine.Current!;
        engine.CompleteRecovery(activity, Start.AddSeconds(80));
        var immediate = engine.Current!;
        Assert.Equal(before.Wearable.StressLevel!.Value - stressReduction, immediate.Wearable.StressLevel!.Value, 8);
        Assert.Equal(before.Wearable.HeartRate!.Value - heartRateReduction, immediate.Wearable.HeartRate!.Value, 8);
        Assert.Equal(WellnessState.Recovering, immediate.Analysis.State);
        Assert.True(immediate.Analysis.StressScore < before.Analysis.StressScore);
        Assert.Contains(immediate.Analysis.Reasons, r => r.Contains(RecoveryActivities.Get(activity).Title));
        Assert.Contains(immediate.Analysis.Reasons, r => r.Contains("not a guaranteed physiological response"));
        Assert.Null(immediate.Message);
        AssertUnaffectedMetrics(before.Wearable, immediate.Wearable);
        var sameTime = await engine.TickAsync(Start.AddSeconds(80));
        Assert.Equal(immediate.Analysis.StressScore, sameTime.Analysis.StressScore);
        Assert.Equal(immediate.Analysis.BurnoutRiskScore, sameTime.Analysis.BurnoutRiskScore);
        Assert.Equal(immediate.Wearable, sameTime.Wearable);
        for (int second = 82; second <= 200; second += 2)
        {
            var actual = await engine.TickAsync(Start.AddSeconds(second));
            var baseline = await control.TickAsync(Start.AddSeconds(second));
            AssertUnaffectedMetrics(baseline.Wearable, actual.Wearable);
            Assert.InRange(actual.Wearable.StressLevel!.Value, 0, 100);
            Assert.InRange(actual.Wearable.HeartRate!.Value, 58, 240);
            Assert.InRange(actual.Wearable.HeartRateVariability!.Value, 1, 300);
            Assert.InRange(actual.Wearable.RecoveryScore!.Value, 0, 100);
            Assert.InRange(actual.Wearable.ReadinessScore!.Value, 0, 100);
            Assert.InRange(actual.Analysis.StressScore, 0, 100);
            Assert.InRange(actual.Analysis.BurnoutRiskScore, 0, 100);
            if (second <= 170)
            {
                Assert.Equal(WellnessState.Recovering, actual.Analysis.State);
                Assert.True(actual.Wearable.StressLevel < baseline.Wearable.StressLevel);
                Assert.True(actual.Wearable.HeartRate < baseline.Wearable.HeartRate);
                Assert.True(actual.Wearable.HeartRateVariability > baseline.Wearable.HeartRateVariability);
                Assert.True(actual.Wearable.RecoveryScore > baseline.Wearable.RecoveryScore);
                Assert.True(actual.Wearable.ReadinessScore > baseline.Wearable.ReadinessScore);
                Assert.True(actual.Wearable.RespirationRate < baseline.Wearable.RespirationRate);
                Assert.True(actual.Analysis.StressScore < baseline.Analysis.StressScore);
                Assert.NotEqual("peak-stress", actual.Message?.Category);
            }
            else if (second < 200)
                Assert.True(actual.Wearable.StressLevel < baseline.Wearable.StressLevel);
            else
                Assert.Equal(baseline.Wearable, actual.Wearable);
        }
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing)]
    [InlineData(RecoveryActivity.ScreenBreak)]
    [InlineData(RecoveryActivity.WaterBreak)]
    [InlineData(RecoveryActivity.StretchBreak)]
    public async Task EveryActivityPreservesPoorSleepAndItsDailyBurden(RecoveryActivity activity)
    {
        var engine = Synthetic(DemoScenario.PoorSleepDay);
        await Feed(engine, 0, 20);
        engine.SetScenario(DemoScenario.RisingStress);
        await Feed(engine, 22, 100);
        var before = engine.Current!;
        engine.CompleteRecovery(activity, Start.AddSeconds(100));
        AssertUnaffectedMetrics(before.Wearable, engine.Current!.Wearable);
        for (int second = 102; second <= 180; second += 2)
        {
            var snapshot = await engine.TickAsync(Start.AddSeconds(second));
            Assert.Equal(before.Wearable.TotalSleepMinutes, snapshot.Wearable.TotalSleepMinutes);
            Assert.Equal(before.Wearable.SleepScore, snapshot.Wearable.SleepScore);
            Assert.True(snapshot.Analysis.FatigueScore >= 65);
            Assert.Contains(snapshot.Analysis.Reasons, r => r.Contains("throughout this UTC day"));
        }
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing)]
    [InlineData(RecoveryActivity.ScreenBreak)]
    [InlineData(RecoveryActivity.WaterBreak)]
    [InlineData(RecoveryActivity.StretchBreak)]
    public async Task ReplayAndLiveAdaptersNeverReceiveFabricatedBenefitsIncludingNullMetrics(RecoveryActivity activity)
    {
        foreach (var sample in new[]
        {
            new WearableSample(),
            new WearableSample { HeartRate = 110, RestingHeartRate = 60, StressLevel = 95, HeartRateVariability = 15,
                TotalSleepMinutes = 280, Steps = 1234, Calories = 500, ActiveMinutes = 12, RecoveryScore = 20,
                ReadinessScore = 25, BodyBattery = 20, SleepScore = 40, RespirationRate = 20 },
            new WearableSample { StressLevel = 95, Steps = 100, HeartRate = 110 }
        })
        foreach (bool replay in new[] { false, true })
        {
            IWearableProvider Provider() => replay ? new ReplayWearableProvider(new[] { sample }, loop: true) : new LiveProvider(sample);
            var engine = new DemoEngine(Provider());
            var control = new DemoEngine(Provider());
            engine.SetScenario(DemoScenario.MeetingOverload);
            control.SetScenario(DemoScenario.MeetingOverload);
            await Feed(engine, 0, 80);
            await Feed(control, 0, 80);
            var before = engine.Current!;
            engine.CompleteRecovery(activity, Start.AddSeconds(80));
            Assert.Equal(before.Wearable, engine.Current!.Wearable);
            AssertScoresEqual(before.Analysis, engine.Current.Analysis);
            Assert.Equal(WellnessState.Recovering, engine.Current.Analysis.State);
            for (int second = 82; second <= 150; second += 2)
            {
                var actual = await engine.TickAsync(Start.AddSeconds(second));
                var baseline = await control.TickAsync(Start.AddSeconds(second));
                Assert.Equal(sample with { Timestamp = actual.Wearable.Timestamp }, actual.Wearable);
                AssertScoresEqual(baseline.Analysis, actual.Analysis);
                Assert.Equal(WellnessState.Recovering, actual.Analysis.State);
                Assert.NotEqual("peak-stress", actual.Message?.Category);
                if (actual.Message?.Category == "recovery") Assert.Contains("no measurement improvement", actual.Message.Text);
            }
        }
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing)]
    [InlineData(RecoveryActivity.ScreenBreak)]
    [InlineData(RecoveryActivity.WaterBreak)]
    [InlineData(RecoveryActivity.StretchBreak)]
    public async Task RecoveryBeforeFirstSampleCannotInventLiveDataOrConnectUnsupportedProviders(RecoveryActivity activity)
    {
        foreach (IWearableProvider provider in new IWearableProvider[] { new GarminProviderStub(), new OuraProviderStub() })
        {
            var engine = new DemoEngine(provider);
            engine.SetRecoveryActive(true);
            engine.CompleteRecovery(activity, Start);
            Assert.Null(engine.Current);
            await Assert.ThrowsAsync<NotSupportedException>(() => engine.TickAsync(Start));
            Assert.Null(engine.Current);
            Assert.Empty(engine.History);
        }
        var missing = new DemoEngine(new LiveProvider(new WearableSample()));
        missing.CompleteRecovery(activity, Start);
        var actual = await missing.TickAsync(Start);
        Assert.Equal(new WearableSample { Timestamp = Start }, actual.Wearable);
        Assert.Equal(WellnessState.Recovering, actual.Analysis.State);
    }

    [Fact]
    public async Task StartingThenCancellingRecoveryDoesNotApplySyntheticBenefits()
    {
        var engine = Synthetic();
        var control = Synthetic();
        await Feed(engine, 0, 80);
        await Feed(control, 0, 80);
        var before = engine.Current!;
        engine.SetRecoveryActive(true);
        Assert.Equal(before.Analysis, engine.Current!.Analysis);
        Assert.Equal(before.Wearable, engine.Current.Wearable);
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.ScreenBreak);
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(session.TryComplete());
        session.Cancel();
        engine.SetRecoveryActive(false);
        Assert.Null(session.TryComplete());
        var actual = await engine.TickAsync(Start.AddSeconds(82));
        var baseline = await control.TickAsync(Start.AddSeconds(82));
        Assert.Equal(baseline.Wearable, actual.Wearable);
        AssertScoresEqual(baseline.Analysis, actual.Analysis);
        Assert.NotEqual(WellnessState.Recovering, actual.Analysis.State);
    }

    [Fact]
    public async Task FullScreenTimerCompletionEndsSuppressionAndRetainsSnooze()
    {
        var engine = Synthetic();
        await Feed(engine, 0, 80);
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.ScreenBreak);
        engine.SetRecoveryActive(true);
        engine.Snooze(TimeSpan.FromMinutes(10), Start.AddSeconds(80));
        for (int second = 82; second <= 380; second += 2)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Null((await engine.TickAsync(Start.AddSeconds(second))).Message);
            var completed = session.TryComplete();
            if (second < 380) Assert.Null(completed);
            else
            {
                Assert.Equal(RecoveryActivity.ScreenBreak, completed);
                engine.CompleteRecovery(completed!.Value, Start.AddSeconds(second));
            }
        }
        Assert.Null(session.TryComplete());
        Assert.Equal(WellnessState.Recovering, engine.Current!.Analysis.State);
        for (int second = 382; second < 680; second += 2)
            Assert.Null((await engine.TickAsync(Start.AddSeconds(second))).Message);
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(680))).Message?.Category);
    }

    [Fact]
    public async Task CompatibilityForwardingHasIdenticalResultsAndOlderCompletionTimeIsClamped()
    {
        var legacy = Synthetic();
        var modern = Synthetic();
        await Feed(legacy, 0, 80);
        await Feed(modern, 0, 80);
        legacy.CompleteBreathing(Start.AddSeconds(10));
        modern.CompleteRecovery(RecoveryActivity.Breathing, Start.AddSeconds(80));
        Assert.Equal(legacy.Current!.Wearable, modern.Current!.Wearable);
        AssertScoresEqual(legacy.Current.Analysis, modern.Current.Analysis);
        for (int second = 82; second <= 210; second += 2)
        {
            var a = await legacy.TickAsync(Start.AddSeconds(second));
            var b = await modern.TickAsync(Start.AddSeconds(second));
            Assert.Equal(a.Wearable, b.Wearable);
            AssertScoresEqual(a.Analysis, b.Analysis);
            Assert.Equal(a.Message, b.Message);
        }
    }

    [Fact]
    public async Task InvalidCompletionIsAtomicAndRestoreClearsActiveAndCompletedRecovery()
    {
        var engine = Synthetic();
        await Feed(engine, 0, 80);
        engine.SetRecoveryActive(true);
        var before = engine.Current;
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.CompleteRecovery((RecoveryActivity)999, Start.AddSeconds(80)));
        Assert.Same(before, engine.Current);
        Assert.Null((await engine.TickAsync(Start.AddSeconds(82))).Message);
        engine.CompleteRecovery(RecoveryActivity.WaterBreak, Start.AddSeconds(82));
        engine.SetRecoveryActive(true);
        engine.Restore(new PersistedAppState { Scenario = DemoScenario.PoorSleepDay });
        var restored = await engine.TickAsync(Start.AddSeconds(84));
        Assert.NotEqual(WellnessState.Recovering, restored.Analysis.State);
        Assert.Equal("fatigue", restored.Message?.Category);
    }

    [Fact]
    public async Task RecoveryMutationWhileProviderReadIsPendingIsNonblockingAndAppliedAtCommit()
    {
        var pending = new TaskCompletionSource<WearableSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new DemoEngine(new PendingProvider(pending.Task));
        var tick = engine.TickAsync(Start);
        engine.SetRecoveryActive(true);
        Assert.False(tick.IsCompleted);
        pending.SetResult(new WearableSample { Timestamp = Start, SleepScore = 20, TotalSleepMinutes = 240 });
        var snapshot = await tick.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(snapshot.Message);
        Assert.NotEqual(WellnessState.Recovering, snapshot.Analysis.State);
        engine.CompleteRecovery(RecoveryActivity.WaterBreak, Start);
        Assert.Equal(snapshot.Wearable, engine.Current!.Wearable);
        AssertScoresEqual(snapshot.Analysis, engine.Current.Analysis);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void AnalyzerRejectsInvalidRecoveryEstimate(double reduction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WellnessAnalyzer().Analyze(new WearableSample(),
            new WorkContext(), recovering: true, recoveryStressReduction: reduction));
    }

    private static void AssertScoresEqual(AnalysisResult expected, AnalysisResult actual)
    {
        Assert.Equal(expected.FocusScore, actual.FocusScore);
        Assert.Equal(expected.StressScore, actual.StressScore);
        Assert.Equal(expected.FatigueScore, actual.FatigueScore);
        Assert.Equal(expected.BurnoutRiskScore, actual.BurnoutRiskScore);
    }
    private static void AssertUnaffectedMetrics(WearableSample expected, WearableSample actual)
    {
        Assert.Equal(expected.Steps, actual.Steps);
        Assert.Equal(expected.ActiveMinutes, actual.ActiveMinutes);
        Assert.Equal(expected.Calories, actual.Calories);
        Assert.Equal(expected.TotalSleepMinutes, actual.TotalSleepMinutes);
        Assert.Equal(expected.SleepScore, actual.SleepScore);
        Assert.Equal(expected.DeepSleepMinutes, actual.DeepSleepMinutes);
        Assert.Equal(expected.RemSleepMinutes, actual.RemSleepMinutes);
        Assert.Equal(expected.LightSleepMinutes, actual.LightSleepMinutes);
        Assert.Equal(expected.RestingHeartRate, actual.RestingHeartRate);
        Assert.Equal(expected.BodyBattery, actual.BodyBattery);
        Assert.Equal(expected.BloodOxygen, actual.BloodOxygen);
        Assert.Equal(expected.FocusScore, actual.FocusScore);
    }
    private static DemoEngine Synthetic(DemoScenario scenario = DemoScenario.RisingStress)
    {
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(scenario);
        return new DemoEngine(provider);
    }
    private static async Task Feed(DemoEngine engine, int first, int last)
    {
        for (int second = first; second <= last; second += 2) await engine.TickAsync(Start.AddSeconds(second));
    }
    private sealed class LiveProvider(WearableSample sample) : IWearableProvider
    {
        public string Name => "Synthetic wearable (demo)";
        public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now, CancellationToken cancellationToken = default)
            => Task.FromResult(sample with { Timestamp = now });
    }
    private sealed class PendingProvider(Task<WearableSample> sample) : IWearableProvider
    {
        public string Name => "Pending live sample";
        public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now, CancellationToken cancellationToken = default) => sample;
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
