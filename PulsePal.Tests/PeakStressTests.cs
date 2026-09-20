using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class PeakStressTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SyntheticPeakEscalatesWithinTenSecondsDespiteEarlierMildCooldown()
    {
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(DemoScenario.RisingStress);
        var snapshots = await Ticks(new DemoEngine(provider), 0, 100);
        var mild = snapshots.First(s => s.Message?.Category == "sustained-stress");
        var peak = Assert.Single(snapshots, s => s.Message?.Category == "peak-stress");
        var threshold = snapshots.First(s => s.Analysis.StressScore >= 90);
        Assert.Equal(TimeSpan.FromSeconds(10), peak.Wearable.Timestamp - threshold.Wearable.Timestamp);
        Assert.InRange((peak.Wearable.Timestamp - mild.Wearable.Timestamp).TotalSeconds, 1, 59);
        Assert.Equal(CharacterState.Concerned, peak.Message!.Character);
        foreach (var text in new[] { "Synthetic", "90/100", "10 seconds", "5 minutes", "water", "movement", "breathing", "non-medical", "not a diagnosis", "Remind Later" })
            Assert.Contains(text, peak.Message.Text);
        Assert.NotEqual(mild.Message!.Text, peak.Message.Text);
    }

    [Fact]
    public async Task PeakRepeatsAreBoundedToTwoMinutesWithoutInterleavedMildPrompts()
    {
        var snapshots = await Ticks(HighEngine(), 0, 410);
        var peaks = snapshots.Where(s => s.Message?.Category == "peak-stress").ToArray();
        Assert.True(peaks.Length >= 3);
        for (int i = 1; i < peaks.Length; i++)
            Assert.Equal(TimeSpan.FromSeconds(120), peaks[i].Wearable.Timestamp - peaks[i - 1].Wearable.Timestamp);
        Assert.All(snapshots.Where(s => s.Wearable.Timestamp >= peaks[0].Wearable.Timestamp && s.Message is not null),
            s => Assert.Equal("peak-stress", s.Message!.Category));
    }

    [Fact]
    public async Task SnoozeBeforePeakSuppressesEscalationAndDismissalSnoozeSuppressesRepeats()
    {
        var engine = HighEngine();
        await Ticks(engine, 0, 30);
        engine.Snooze(TimeSpan.FromSeconds(100), Start.AddSeconds(30));
        Assert.All(await Ticks(engine, 32, 128), s => Assert.Null(s.Message));
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(130))).Message?.Category);
        engine.Snooze(TimeSpan.FromSeconds(300), Start.AddSeconds(130));
        Assert.Null(engine.Current!.Message);
        engine.Snooze(TimeSpan.FromSeconds(1), Start.AddSeconds(132));
        Assert.All(await Ticks(engine, 132, 428), s => Assert.Null(s.Message));
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(430))).Message?.Category);
        Assert.All(await Ticks(engine, 432, 548), s => Assert.Null(s.Message));
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(550))).Message?.Category);
    }

    [Fact]
    public async Task PeakStillInterruptsMaximumFocusWithoutChangingFocusFacts()
    {
        var engine = HighEngine();
        engine.SetScenario(DemoScenario.DeepFocusSession);
        engine.StartFocus(Start);
        var snapshots = await Ticks(engine, 0, 80);
        var peak = Assert.Single(snapshots, s => s.Message?.Category == "peak-stress");
        Assert.Equal(90, peak.Analysis.FocusScore);
        Assert.True(peak.IsFocusActive);
        Assert.Equal(Start, peak.FocusStartedAt);
        Assert.All(snapshots.Where(s => s.Analysis.State == WellnessState.Stressed), s => Assert.Null(s.Message));
    }

    [Fact]
    public async Task BurnoutRiskAlreadyPresentCanEscalateToPeak()
    {
        bool peak = false;
        var engine = new DemoEngine(new TestProvider(now => peak ? High(now) : Healthy(now) with { StressLevel = 75 }));
        engine.SetScenario(DemoScenario.MeetingOverload);
        var early = await Ticks(engine, 0, 80);
        Assert.Equal(WellnessState.BurnoutRisk, early[^1].Analysis.State);
        Assert.InRange(early[^1].Analysis.StressScore, 80, 89.99);
        Assert.DoesNotContain(early, s => s.Message?.Category == "peak-stress");
        peak = true;
        var escalation = Assert.Single(await Ticks(engine, 82, 110), s => s.Message?.Category == "peak-stress");
        Assert.Equal(WellnessState.BurnoutRisk, escalation.Analysis.State);
        Assert.InRange((escalation.Wearable.Timestamp - early.Last(s => s.Message is not null).Wearable.Timestamp).TotalSeconds, 1, 59);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BelowEightyMustPersistTenSecondsToResetTheEpisode(bool sustainedRecovery)
    {
        double target = 100;
        var engine = new DemoEngine(new TestProvider(now => Healthy(now) with
        {
            HeartRate = 100, StressLevel = (target - 45) / 0.85 + 30
        }));
        engine.SetScenario(DemoScenario.DeepFocusSession);
        await Ticks(engine, 0, 162);
        target = 75;
        int lowEnd = sustainedRecovery ? 180 : 168;
        var low = await Ticks(engine, 164, lowEnd);
        Assert.True(low[^1].Analysis.StressScore < 80);
        target = 100;
        var returned = await Ticks(engine, lowEnd + 2, lowEnd + 40);
        var firstHigh = returned.First(s => s.Analysis.StressScore >= 90);
        var peak = Assert.Single(returned, s => s.Message?.Category == "peak-stress");
        Assert.Equal(TimeSpan.FromSeconds(sustainedRecovery ? 10 : 0), peak.Wearable.Timestamp - firstHigh.Wearable.Timestamp);
    }

    [Fact]
    public async Task DuplicateOrStaleObservationsCannotAccumulatePeakDuration()
    {
        bool stale = false;
        var engine = new DemoEngine(new TestProvider(now => High(stale ? Start.AddSeconds(36) : now)));
        await Ticks(engine, 0, 36);
        Assert.True(engine.Current!.Analysis.StressScore >= 90);
        stale = true;
        for (int i = 0; i < 50; i++) Assert.NotEqual("peak-stress", (await engine.TickAsync(Start.AddSeconds(36))).Message?.Category);
        Assert.All(await Ticks(engine, 38, 100), s => Assert.NotEqual("peak-stress", s.Message?.Category));
        stale = false;
        var resumed = await Ticks(engine, 102, 142);
        Assert.DoesNotContain(resumed, s => s.Message?.Category == "peak-stress");
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(148))).Message?.Category);
    }

    [Fact]
    public async Task RunningBreakSuppressesEveryPromptAndDoesNotEraseOrEscalateRisk()
    {
        var engine = HighEngine();
        engine.SetScenario(DemoScenario.MeetingOverload);
        await Ticks(engine, 0, 80);
        var before = engine.Current!;
        Assert.Equal(WellnessState.BurnoutRisk, before.Analysis.State);
        engine.SetRecoveryActive(true);
        engine.SetRecoveryActive(true);
        Assert.Null(engine.Current!.Message);
        Assert.Equal(before.Analysis, engine.Current.Analysis);
        Assert.Equal(before.Wearable, engine.Current.Wearable);
        engine.StartFocus(Start.AddSeconds(80));
        engine.EndFocus(Start.AddSeconds(82));
        Assert.Null(engine.Current.Message);
        foreach (var snapshot in await Ticks(engine, 82, 400))
        {
            Assert.Null(snapshot.Message);
            Assert.Equal(before.Analysis.BurnoutRiskScore, snapshot.Analysis.BurnoutRiskScore);
            Assert.Equal(High(snapshot.Wearable.Timestamp), snapshot.Wearable);
        }
        engine.SetRecoveryActive(false);
        engine.SetRecoveryActive(false);
        Assert.All(await Ticks(engine, 402, 410), s => Assert.NotEqual("peak-stress", s.Message?.Category));
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(412))).Message?.Category);
    }

    [Theory]
    [InlineData(DemoScenario.PoorSleepDay)]
    [InlineData(DemoScenario.RecoveryAfterBreak)]
    [InlineData(DemoScenario.HealthyDay)]
    public async Task ActiveRecoveryAlsoSuppressesFatigueRecoveryAndHydration(DemoScenario scenario)
    {
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(scenario);
        var engine = new DemoEngine(provider);
        engine.SetRecoveryActive(true);
        Assert.All(await Ticks(engine, 0, 1810), s => Assert.Null(s.Message));
        engine.SetRecoveryActive(false);
        Assert.Equal(scenario switch
        {
            DemoScenario.PoorSleepDay => "fatigue",
            DemoScenario.RecoveryAfterBreak => "recovery",
            _ => "hydration"
        }, (await engine.TickAsync(Start.AddSeconds(1812))).Message?.Category);
    }

    [Fact]
    public void PeakCooldownBypassesOnlyOrdinaryCooldownAndNeverSnoozeOrItsRepeatLimit()
    {
        var policy = new CooldownPolicy();
        Assert.True(policy.TryAllow("sustained-stress", Start));
        Assert.False(policy.TryAllow("fatigue", Start.AddSeconds(10)));
        Assert.False(policy.TryAllowPeakStress(Start.AddTicks(-1)));
        Assert.True(policy.TryAllowPeakStress(Start.AddSeconds(10)));
        Assert.False(policy.TryAllow("fatigue", Start.AddSeconds(60)));
        Assert.True(policy.TryAllow("fatigue", Start.AddSeconds(70)));
        Assert.False(policy.TryAllowPeakStress(Start.AddSeconds(129.999)));
        policy.Snooze(TimeSpan.FromSeconds(60), Start.AddSeconds(120));
        Assert.False(policy.TryAllowPeakStress(Start.AddSeconds(130)));
        Assert.False(policy.TryAllowPeakStress(Start.AddSeconds(179.999)));
        Assert.True(policy.TryAllowPeakStress(Start.AddSeconds(180)));
        Assert.False(policy.TryAllowPeakStress(Start.AddSeconds(180)));
        policy.Reset();
        Assert.True(policy.TryAllowPeakStress(Start));
    }

    [Fact]
    public void PeakCooldownIsAtomicUnderConcurrentCalls()
    {
        var policy = new CooldownPolicy();
        int allowed = 0;
        Parallel.For(0, 100, _ =>
        {
            if (policy.TryAllowPeakStress(Start)) Interlocked.Increment(ref allowed);
        });
        Assert.Equal(1, allowed);
    }

    [Fact]
    public void RunningTimerPausesAnalyzerRiskTimingWithoutGrantingRecovery()
    {
        var analyzer = new WellnessAnalyzer();
        var context = new WorkContext { MeetingsToday = 9 };
        AnalysisResult before = null!;
        for (int i = 0; i <= 40; i += 2) before = analyzer.Analyze(High(Start.AddSeconds(i)), context);
        Assert.True(before.BurnoutRiskScore > 0);
        for (int i = 42; i <= 340; i += 2)
        {
            var active = analyzer.Analyze(High(Start.AddSeconds(i)), context, recoveryActive: true);
            Assert.Equal(before.BurnoutRiskScore, active.BurnoutRiskScore);
            Assert.NotEqual(WellnessState.Recovering, active.State);
            Assert.NotEqual(WellnessState.BurnoutRisk, active.State);
            Assert.Contains(active.Reasons, r => r.Contains("no completion benefit"));
        }
        var resumed = analyzer.Analyze(High(Start.AddSeconds(342)), context);
        Assert.InRange(resumed.BurnoutRiskScore, before.BurnoutRiskScore, 49);
        Assert.Equal(WellnessState.BurnoutRisk, analyzer.Analyze(High(Start.AddSeconds(360)), context).State);
    }

    [Fact]
    public async Task StaleHighObservationCannotRepeatAnAlreadyEstablishedPeakPrompt()
    {
        bool stale = false;
        var engine = new DemoEngine(new TestProvider(now => High(stale ? Start.AddSeconds(60) : now)));
        Assert.Contains(await Ticks(engine, 0, 60), s => s.Message?.Category == "peak-stress");
        stale = true;
        Assert.All(await Ticks(engine, 62, 400), s => Assert.Null(s.Message));
        stale = false;
        Assert.DoesNotContain(await Ticks(engine, 402, 442), s => s.Message?.Category == "peak-stress");
        Assert.Equal("peak-stress", (await engine.TickAsync(Start.AddSeconds(448))).Message?.Category);
    }

    [Fact]
    public async Task BriefPeakBelowTenSecondsDoesNotEscalate()
    {
        var engine = new DemoEngine(new TestProvider(now => now <= Start.AddSeconds(42) ? High(now) : Healthy(now)));
        var snapshots = await Ticks(engine, 0, 90);
        Assert.Contains(snapshots, s => s.Analysis.StressScore >= 90);
        Assert.DoesNotContain(snapshots, s => s.Message?.Category == "peak-stress");
    }

    [Theory]
    [InlineData(89.9, false)]
    [InlineData(90.1, true)]
    public async Task PeakThresholdIsSeparateFromEarlyHighStress(double target, bool peakExpected)
    {
        var engine = new DemoEngine(new TestProvider(now => Healthy(now) with
        {
            HeartRate = 100, StressLevel = (target - 45) / 0.85 + 30
        }));
        engine.SetScenario(DemoScenario.DeepFocusSession);
        var snapshots = await Ticks(engine, 0, 100);
        Assert.Equal(WellnessState.HighStress, snapshots[^1].Analysis.State);
        Assert.Equal(target, snapshots[^1].Analysis.StressScore, 8);
        Assert.Equal(peakExpected, snapshots.Any(s => s.Message?.Category == "peak-stress"));
    }

    [Fact]
    public async Task AnalyzerExplainsEarlyHighAndPeakThresholdsDistinctly()
    {
        var snapshots = await Ticks(HighEngine(), 0, 40);
        var early = snapshots.First(s => s.Analysis.State == WellnessState.Stressed);
        var high = snapshots.First(s => s.Analysis.State == WellnessState.HighStress);
        var peak = snapshots.First(s => s.Analysis.StressScore >= 90);
        Assert.Contains(early.Analysis.Reasons, r => r.Contains("65/100") && r.Contains("early"));
        Assert.Contains(high.Analysis.Reasons, r => r.Contains("80/100") && r.Contains("below"));
        Assert.Contains(peak.Analysis.Reasons, r => r.Contains("90/100") && r.Contains("10 seconds"));
    }

    private static DemoEngine HighEngine() => new(new TestProvider(High));
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
    private static async Task<List<DemoSnapshot>> Ticks(DemoEngine engine, int first, int last)
    {
        var snapshots = new List<DemoSnapshot>();
        for (int second = first; second <= last; second += 2)
            snapshots.Add(await engine.TickAsync(Start.AddSeconds(second)));
        return snapshots;
    }
    private sealed class TestProvider(Func<DateTimeOffset, WearableSample> sample) : IWearableProvider
    {
        public string Name => "Reported test data";
        public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now, CancellationToken cancellationToken = default)
            => Task.FromResult(sample(now));
    }
}
