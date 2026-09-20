using System.Text.Json;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class ProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly MockWorkContextProvider Contexts = new();

    [Theory]
    [InlineData(DemoScenario.HealthyDay)]
    [InlineData(DemoScenario.DeepFocusSession)]
    [InlineData(DemoScenario.RisingStress)]
    [InlineData(DemoScenario.MeetingOverload)]
    [InlineData(DemoScenario.RecoveryAfterBreak)]
    [InlineData(DemoScenario.PoorSleepDay)]
    public async Task ScenariosStayRealisticSmoothAndCumulative(DemoScenario scenario)
    {
        var provider = new SyntheticWearableProvider(42);
        provider.SetScenario(scenario);
        Assert.Equal(scenario, provider.Scenario);
        Assert.False(string.IsNullOrWhiteSpace(provider.Name));
        WearableSample? previous = null;
        for (int seconds = 0; seconds <= 300; seconds += 2)
        {
            var sample = await provider.GetSampleAsync(Contexts.GetContext(scenario), Start.AddSeconds(seconds));
            Assert.Equal(Start.AddSeconds(seconds), sample.Timestamp);
            AssertRange(sample.HeartRate, 55, 115);
            AssertRange(sample.RestingHeartRate, 50, 75);
            AssertRange(sample.HeartRateVariability, 10, 90);
            AssertRange(sample.StressLevel, 0, 100);
            AssertRange(sample.RecoveryScore, 0, 100);
            AssertRange(sample.ReadinessScore, 0, 100);
            AssertRange(sample.BodyBattery, 0, 100);
            AssertRange(sample.RespirationRate, 10, 24);
            AssertRange(sample.BloodOxygen, 95, 100);
            AssertRange(sample.SleepScore, 0, 100);
            Assert.InRange(sample.TotalSleepMinutes!.Value, 240, 540);
            Assert.Equal(sample.TotalSleepMinutes, sample.DeepSleepMinutes + sample.RemSleepMinutes + sample.LightSleepMinutes);
            Assert.Null(sample.FocusScore);
            if (previous is not null)
            {
                Assert.True(sample.Steps >= previous.Steps);
                Assert.True(sample.Calories >= previous.Calories);
                Assert.True(sample.ActiveMinutes >= previous.ActiveMinutes);
                Assert.InRange(Math.Abs(sample.HeartRate!.Value - previous.HeartRate!.Value), 0, 4.001);
                Assert.InRange(Math.Abs(sample.StressLevel!.Value - previous.StressLevel!.Value), 0, 7.001);
                Assert.InRange(Math.Abs(sample.HeartRateVariability!.Value - previous.HeartRateVariability!.Value), 0, 4.001);
                Assert.InRange(Math.Abs(sample.RecoveryScore!.Value - previous.RecoveryScore!.Value), 0, 4.001);
                Assert.InRange(Math.Abs(sample.RespirationRate!.Value - previous.RespirationRate!.Value), 0, 0.801);
            }
            if (scenario != DemoScenario.RecoveryAfterBreak)
            {
                Assert.Equal(0, sample.Steps);
                Assert.Equal(0, sample.ActiveMinutes);
            }
            previous = sample;
        }
    }

    [Fact]
    public async Task RisingStressRampsByTwentyFourSecondsWithCorrelatedPhysiology()
    {
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(DemoScenario.RisingStress);
        var samples = await SampleTicks(provider, Start, 24);
        AssertRange(samples[0].StressLevel, 20, 40);
        AssertRange(samples[^1].StressLevel, 80, 100);
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].StressLevel > samples[i - 1].StressLevel);
            Assert.InRange(samples[i].StressLevel!.Value - samples[i - 1].StressLevel!.Value, 0, 7);
            Assert.True(samples[i].HeartRate > samples[i - 1].HeartRate);
            Assert.True(samples[i].HeartRateVariability < samples[i - 1].HeartRateVariability);
            Assert.True(samples[i].RecoveryScore < samples[i - 1].RecoveryScore);
            Assert.True(samples[i].RespirationRate > samples[i - 1].RespirationRate);
        }
    }

    [Fact]
    public async Task DeepFocusIsCalmAndMeetingContextContributesToStress()
    {
        var focus = new SyntheticWearableProvider(1);
        focus.SetScenario(DemoScenario.DeepFocusSession);
        var calm = (await SampleTicks(focus, Start, 60))[^1];
        AssertRange(calm.StressLevel, 15, 25);
        AssertRange(calm.HeartRate, 60, 78);
        var meeting = new SyntheticWearableProvider(1);
        var quietMeeting = new SyntheticWearableProvider(1);
        meeting.SetScenario(DemoScenario.MeetingOverload);
        quietMeeting.SetScenario(DemoScenario.MeetingOverload);
        WearableSample? busy = null, quiet = null;
        for (int seconds = 0; seconds <= 60; seconds += 2)
        {
            busy = await meeting.GetSampleAsync(Contexts.GetContext(meeting.Scenario), Start.AddSeconds(seconds));
            quiet = await quietMeeting.GetSampleAsync(new WorkContext(), Start.AddSeconds(seconds));
        }
        Assert.True(busy!.StressLevel > quiet!.StressLevel + 5);
        Assert.True(busy.HeartRate > calm.HeartRate);
        Assert.True(busy.HeartRateVariability < calm.HeartRateVariability);
        Assert.True(busy.RecoveryScore < calm.RecoveryScore);
    }

    [Fact]
    public async Task WalkingIncreasesStepsRecoveryAndModeratelyElevatesHeartRate()
    {
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(DemoScenario.RecoveryAfterBreak);
        var samples = await SampleTicks(provider, Start, 120);
        Assert.Equal(96, samples[30].Steps);
        Assert.Equal(1, samples[30].ActiveMinutes);
        Assert.Equal(192, samples[^1].Steps);
        Assert.Equal(2, samples[^1].ActiveMinutes);
        Assert.True(samples[^1].RecoveryScore > samples[0].RecoveryScore + 10);
        Assert.True(samples[^1].HeartRate > samples[0].HeartRate + 10);
        AssertRange(samples[^1].HeartRate, 80, 105);
        provider.SetScenario(DemoScenario.HealthyDay);
        var stopped = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), Start.AddSeconds(120));
        var still = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), Start.AddSeconds(150));
        Assert.Equal(stopped.Steps, still.Steps);
        Assert.Equal(stopped.ActiveMinutes, still.ActiveMinutes);
        Assert.True(still.Calories > stopped.Calories);
    }

    [Fact]
    public async Task PoorSleepPersistsAcrossScenariosAndAffectsEntireDay()
    {
        var poor = new SyntheticWearableProvider(9);
        var rested = new SyntheticWearableProvider(9);
        poor.SetScenario(DemoScenario.PoorSleepDay);
        var first = await poor.GetSampleAsync(Contexts.GetContext(poor.Scenario), Start);
        foreach (var scenario in Enum.GetValues<DemoScenario>().Where(s => s != DemoScenario.PoorSleepDay))
        {
            poor.SetScenario(scenario);
            rested.SetScenario(scenario);
            var time = Start.AddHours((int)scenario + 1);
            await poor.GetSampleAsync(Contexts.GetContext(scenario), time);
            await rested.GetSampleAsync(Contexts.GetContext(scenario), time);
            for (int seconds = 2; seconds <= 120; seconds += 2)
            {
                var low = await poor.GetSampleAsync(Contexts.GetContext(scenario), time.AddSeconds(seconds));
                var normal = await rested.GetSampleAsync(Contexts.GetContext(scenario), time.AddSeconds(seconds));
                Assert.Equal(first.SleepScore, low.SleepScore);
                Assert.Equal(first.TotalSleepMinutes, low.TotalSleepMinutes);
                Assert.True(low.SleepScore < normal.SleepScore);
                Assert.True(low.RecoveryScore < normal.RecoveryScore);
                Assert.True(low.HeartRateVariability < normal.HeartRateVariability);
                Assert.True(low.RestingHeartRate > normal.RestingHeartRate);
            }
        }
        poor.SetScenario(DemoScenario.HealthyDay);
        var nextDay = await poor.GetSampleAsync(new WorkContext(), Start.AddDays(1));
        Assert.True(nextDay.SleepScore > first.SleepScore);
        Assert.True(nextDay.TotalSleepMinutes > first.TotalSleepMinutes);
    }

    [Fact]
    public async Task SelectingPoorSleepRevisesTodaysSleepButLeavingDoesNotUndoIt()
    {
        var provider = new SyntheticWearableProvider(9);
        var healthy = await provider.GetSampleAsync(new WorkContext(), Start);
        provider.SetScenario(DemoScenario.PoorSleepDay);
        var poor = await provider.GetSampleAsync(new WorkContext(), Start.AddSeconds(2));
        provider.SetScenario(DemoScenario.DeepFocusSession);
        var switched = await provider.GetSampleAsync(new WorkContext(), Start.AddSeconds(4));
        Assert.True(poor.SleepScore < healthy.SleepScore);
        Assert.Equal(poor.SleepScore, switched.SleepScore);
        provider.SetScenario(DemoScenario.PoorSleepDay);
        var nextDay = await provider.GetSampleAsync(new WorkContext(), Start.AddDays(1));
        Assert.Equal(poor.SleepScore, nextDay.SleepScore);
    }

    [Fact]
    public async Task BreathingPersistsNinetySecondsAcrossScenarioSwitchesWithoutLosingActivity()
    {
        var provider = new SyntheticWearableProvider(1);
        provider.SetScenario(DemoScenario.RecoveryAfterBreak);
        await SampleTicks(provider, Start, 120);
        provider.SetScenario(DemoScenario.RisingStress);
        var before = (await SampleTicks(provider, Start.AddSeconds(120), 60))[^1];
        provider.CompleteBreathing(before.Timestamp);
        var immediate = await provider.GetSampleAsync(new WorkContext(), before.Timestamp);
        Assert.True(immediate.StressLevel <= before.StressLevel - 20);
        Assert.True(immediate.HeartRate <= before.HeartRate - 7);
        Assert.Equal(before.Steps, immediate.Steps);
        Assert.Equal(before.Calories, immediate.Calories);
        provider.SetScenario(DemoScenario.MeetingOverload);
        for (int seconds = 2; seconds <= 90; seconds += 2)
        {
            var sample = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), before.Timestamp.AddSeconds(seconds));
            Assert.True(sample.StressLevel <= immediate.StressLevel + 0.01);
            Assert.True(sample.HeartRate <= immediate.HeartRate + 0.01);
            Assert.Equal(before.Steps, sample.Steps);
            Assert.True(sample.Calories >= before.Calories);
        }
        var expired = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), before.Timestamp.AddSeconds(120));
        Assert.True(expired.StressLevel > immediate.StressLevel);
    }

    [Fact]
    public async Task SparsePollingDoesNotApplyBreathingExpiryRetroactively()
    {
        var provider = new SyntheticWearableProvider(1);
        provider.SetScenario(DemoScenario.MeetingOverload);
        var before = (await SampleTicks(provider, Start, 120))[^1];
        provider.CompleteBreathing(before.Timestamp);
        var immediate = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), before.Timestamp);
        var after = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), before.Timestamp.AddSeconds(91));
        Assert.InRange(after.StressLevel!.Value - immediate.StressLevel!.Value, 0, 8);
        Assert.InRange(after.HeartRate!.Value - immediate.HeartRate!.Value, 0, 4);
    }

    [Fact]
    public async Task DailyCountersResetAtUtcMidnightAndDoNotResetWhenOffsetChanges()
    {
        var provider = new SyntheticWearableProvider(8);
        provider.SetScenario(DemoScenario.RecoveryAfterBreak);
        var late = new DateTimeOffset(2026, 9, 20, 23, 55, 0, TimeSpan.Zero);
        var samples = await SampleTicks(provider, late, 120);
        var sameInstant = await provider.GetSampleAsync(new WorkContext(), samples[^1].Timestamp.ToOffset(TimeSpan.FromHours(5.5)));
        Assert.Equal(samples[^1].Steps, sameInstant.Steps);
        var midnight = await provider.GetSampleAsync(new WorkContext(), late.AddMinutes(5));
        Assert.Equal(0, midnight.Steps);
        Assert.Equal(0, midnight.ActiveMinutes);
        Assert.Equal(0, midnight.Calories);
        var walking = await provider.GetSampleAsync(new WorkContext(), late.AddMinutes(6));
        Assert.Equal(96, walking.Steps);
        Assert.Equal(1, walking.ActiveMinutes);
    }

    [Fact]
    public async Task SeedIsRepeatableAndTimeMustBeNondecreasing()
    {
        var first = new SyntheticWearableProvider(123);
        var second = new SyntheticWearableProvider(123);
        for (int seconds = 0; seconds <= 30; seconds += 2)
            Assert.Equal(await first.GetSampleAsync(new WorkContext(), Start.AddSeconds(seconds)),
                await second.GetSampleAsync(new WorkContext(), Start.AddSeconds(seconds)));
        var last = await first.GetSampleAsync(new WorkContext(), Start.AddSeconds(30));
        Assert.Equal(last, await first.GetSampleAsync(new WorkContext(), last.Timestamp));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => first.GetSampleAsync(new WorkContext(), Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.CompleteBreathing(Start));
        Assert.Equal(last, await first.GetSampleAsync(new WorkContext(), last.Timestamp));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.SetScenario((DemoScenario)99));
        Assert.Equal(DemoScenario.HealthyDay, new SyntheticWearableProvider().Scenario);
    }

    [Fact]
    public async Task LongSessionsStayBoundedInsteadOfRandomWalking()
    {
        foreach (var scenario in Enum.GetValues<DemoScenario>())
        {
            var provider = new SyntheticWearableProvider(5);
            provider.SetScenario(scenario);
            await SampleTicks(provider, Start, 180);
            var settled = await provider.GetSampleAsync(Contexts.GetContext(scenario), Start.AddMinutes(5));
            for (int hour = 1; hour <= 10; hour++)
            {
                var sample = await provider.GetSampleAsync(Contexts.GetContext(scenario), Start.AddHours(hour));
                Assert.InRange(Math.Abs(sample.StressLevel!.Value - settled.StressLevel!.Value), 0, 3);
                Assert.InRange(Math.Abs(sample.RecoveryScore!.Value - settled.RecoveryScore!.Value), 0, 3);
            }
        }
    }

    [Fact]
    public async Task SyntheticCancellationDoesNotAdvanceState()
    {
        var provider = new SyntheticWearableProvider(5);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetSampleAsync(new WorkContext(), Start.AddHours(1), source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Equal(Start, (await provider.GetSampleAsync(new WorkContext(), Start)).Timestamp);
    }

    [Fact]
    public async Task ConcurrentSyntheticCallsAndMutationsRemainSafe()
    {
        var provider = new SyntheticWearableProvider(5);
        await provider.GetSampleAsync(new WorkContext(), Start);
        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
        {
            provider.SetScenario((DemoScenario)(i % 6));
            if (i % 7 == 0) provider.CompleteBreathing(Start);
            var sample = await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), Start);
            AssertRange(sample.StressLevel, 0, 100);
            Assert.Equal(0, sample.Steps);
        })));
    }

    [Fact]
    public async Task ReplayUsesCallerTimeCopiesInputAndThrowsOnExhaustion()
    {
        var original = new WearableSample { Timestamp = Start.AddYears(-1), HeartRate = 72 };
        var input = new[] { original, new WearableSample() };
        var provider = new ReplayWearableProvider(input);
        input[0] = new WearableSample { HeartRate = 100 };
        var first = await provider.GetSampleAsync(new WorkContext(), Start);
        Assert.Equal(original with { Timestamp = Start }, first);
        var second = await provider.GetSampleAsync(new WorkContext(), Start.AddSeconds(2));
        Assert.Null(second.HeartRate);
        Assert.Null(second.FocusScore);
        Assert.Equal(Start.AddSeconds(2), second.Timestamp);
        Assert.Equal(Start.AddYears(-1), original.Timestamp);
        var exception = await Assert.ThrowsAsync<EndOfStreamException>(() => provider.GetSampleAsync(new WorkContext(), Start));
        Assert.Contains("loop", exception.Message);
        Assert.False(string.IsNullOrWhiteSpace(provider.Name));
    }

    [Fact]
    public async Task ReplayLoopsOnlyWhenRequestedAndCancellationDoesNotConsume()
    {
        var provider = new ReplayWearableProvider(new[] { new WearableSample { HeartRate = 70 }, new WearableSample { HeartRate = 80 } }, loop: true);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSampleAsync(new WorkContext(), Start, source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        for (int index = 0; index < 10; index++)
        {
            var sample = await provider.GetSampleAsync(new WorkContext(), Start.AddSeconds(index * 2));
            Assert.Equal(index % 2 == 0 ? 70d : 80d, sample.HeartRate);
            Assert.Equal(Start.AddSeconds(index * 2), sample.Timestamp);
        }
    }

    [Fact]
    public async Task ConcurrentReplayConsumesEverySampleExactlyOnce()
    {
        var provider = new ReplayWearableProvider(Enumerable.Range(0, 100).Select(i => new WearableSample { Steps = i }));
        var samples = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => provider.GetSampleAsync(new WorkContext(), Start))));
        Assert.Equal(Enumerable.Range(0, 100), samples.Select(s => s.Steps!.Value).Order());
        await Assert.ThrowsAsync<EndOfStreamException>(() => provider.GetSampleAsync(new WorkContext(), Start));
    }

    [Fact]
    public async Task ReplayJsonIsCaseInsensitiveAndPreservesNullableMetrics()
    {
        string path = ScratchPath();
        try
        {
            await File.WriteAllTextAsync(path, """
                [{"timestamp":"2020-01-01T00:00:00Z","HEARTRATE":74,"stressLevel":null,
                  "totalSleepMinutes":420,"deepSleepMinutes":80,"remSleepMinutes":100,"lightSleepMinutes":240}]
                """);
            var provider = new ReplayWearableProvider(path, loop: true);
            for (int index = 0; index < 2; index++)
            {
                var sample = await provider.GetSampleAsync(new WorkContext(), Start.AddSeconds(index * 2));
                Assert.Equal(74d, sample.HeartRate);
                Assert.Null(sample.StressLevel);
                Assert.Null(sample.Steps);
                Assert.Equal(Start.AddSeconds(index * 2), sample.Timestamp);
                Assert.Equal(420, sample.TotalSleepMinutes);
            }
            var finite = new ReplayWearableProvider(path);
            await finite.GetSampleAsync(new WorkContext(), Start);
            await Assert.ThrowsAsync<EndOfStreamException>(() => finite.GetSampleAsync(new WorkContext(), Start));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[{\"heartRate\":\"invalid\"}]")]
    public void ReplayRejectsMalformedJson(string json)
    {
        string path = ScratchPath();
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<JsonException>(() => new ReplayWearableProvider(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{\"stressLevel\":101}]")]
    public void ReplayRejectsInvalidJsonSamples(string json)
    {
        string path = ScratchPath();
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<ArgumentException>(() => new ReplayWearableProvider(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReplayRejectsInvalidListsAndMetrics()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayWearableProvider((IEnumerable<WearableSample>)null!));
        Assert.Throws<ArgumentException>(() => new ReplayWearableProvider(Array.Empty<WearableSample>()));
        Assert.Throws<ArgumentException>(() => new ReplayWearableProvider(new WearableSample[] { null! }));
        foreach (var sample in new[]
        {
            new WearableSample { HeartRate = double.NaN },
            new WearableSample { Calories = double.PositiveInfinity },
            new WearableSample { Steps = -1 },
            new WearableSample { StressLevel = 101 },
            new WearableSample { TotalSleepMinutes = 60, DeepSleepMinutes = 70 },
            new WearableSample { TotalSleepMinutes = 60, DeepSleepMinutes = 20, RemSleepMinutes = 20, LightSleepMinutes = 30 }
        })
            Assert.Throws<ArgumentException>(() => new ReplayWearableProvider(new[] { sample }));
    }

    [Fact]
    public async Task VendorStubsAreExplicitAndHonorCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        foreach (IWearableProvider provider in new IWearableProvider[] { new GarminProviderStub(), new OuraProviderStub() })
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.Name));
            var unsupported = await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetSampleAsync(new WorkContext(), Start));
            Assert.Contains("not implemented", unsupported.Message);
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSampleAsync(new WorkContext(), Start, source.Token));
            Assert.Equal(source.Token, canceled.CancellationToken);
        }
    }

    [Fact]
    public void MockContextsMatchScenarioWorkloads()
    {
        var healthy = Contexts.GetContext(DemoScenario.HealthyDay);
        var focus = Contexts.GetContext(DemoScenario.DeepFocusSession);
        var rising = Contexts.GetContext(DemoScenario.RisingStress);
        var meeting = Contexts.GetContext(DemoScenario.MeetingOverload);
        var walking = Contexts.GetContext(DemoScenario.RecoveryAfterBreak);
        var poorSleep = Contexts.GetContext(DemoScenario.PoorSleepDay);
        Assert.True(focus.AppSwitchCount < healthy.AppSwitchCount);
        Assert.Equal(0, focus.EscalationCount);
        Assert.True(rising.PriorityTasks > healthy.PriorityTasks);
        Assert.True(rising.EscalationCount > healthy.EscalationCount);
        Assert.True(meeting.MeetingsToday > rising.MeetingsToday);
        Assert.True(meeting.AppSwitchCount > rising.AppSwitchCount);
        Assert.Equal("Microsoft Teams", meeting.ActiveApplication);
        Assert.True(walking.IdleTime > TimeSpan.Zero);
        Assert.Equal(0, walking.AppSwitchCount);
        Assert.True(poorSleep.PriorityTasks < rising.PriorityTasks);
        Assert.Throws<ArgumentOutOfRangeException>(() => Contexts.GetContext((DemoScenario)100));
    }

    private static async Task<List<WearableSample>> SampleTicks(SyntheticWearableProvider provider, DateTimeOffset start, int durationSeconds)
    {
        var result = new List<WearableSample>();
        for (int seconds = 0; seconds <= durationSeconds; seconds += 2)
            result.Add(await provider.GetSampleAsync(Contexts.GetContext(provider.Scenario), start.AddSeconds(seconds)));
        return result;
    }

    private static void AssertRange(double? value, double minimum, double maximum)
    {
        Assert.NotNull(value);
        Assert.InRange(value.Value, minimum, maximum);
    }

    private static string ScratchPath()
    {
        return Path.Combine(AppContext.BaseDirectory, $"provider-replay-{Guid.NewGuid():N}.json");
    }
}
