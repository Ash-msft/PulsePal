using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class ReplayFixtureTests
{
    [Fact]
    public async Task ShippedJsonFixturePreservesCorrelationsMissingMetricsAndFiniteStream()
    {
        var provider = new ReplayWearableProvider(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wearable-replay.json"));
        var engine = new DemoEngine(provider);
        var start = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
        DemoSnapshot? previous = null;
        for (int index = 0; index < 3; index++)
        {
            var snapshot = await engine.TickAsync(start.AddSeconds(index * 2));
            Assert.Equal(start.AddSeconds(index * 2), snapshot.Wearable.Timestamp);
            Assert.Null(snapshot.Wearable.FocusScore);
            Assert.Equal(465, snapshot.Wearable.TotalSleepMinutes);
            if (previous is not null)
            {
                Assert.True(snapshot.Wearable.HeartRate > previous.Wearable.HeartRate);
                Assert.True(snapshot.Wearable.StressLevel > previous.Wearable.StressLevel);
                Assert.True(snapshot.Wearable.HeartRateVariability < previous.Wearable.HeartRateVariability);
                Assert.True(snapshot.Wearable.RecoveryScore < previous.Wearable.RecoveryScore);
            }
            previous = snapshot;
        }
        Assert.Null(previous!.Wearable.BloodOxygen);
        await Assert.ThrowsAsync<EndOfStreamException>(() => engine.TickAsync(start.AddSeconds(6)));
        Assert.Same(previous, engine.Current);
        Assert.Equal(3, engine.History.Count);
    }
}
