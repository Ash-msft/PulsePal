using PulsePal.Core;

namespace PulsePal.Tests;

public sealed class SessionClockTests
{
    [Fact]
    public void OrdinaryModeFollowsWallClockButTimestampDoesNot()
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        Assert.False(clock.IsPresentation);
        Assert.False(clock.IsPaused);
        Assert.Equal(TimeSpan.TicksPerSecond, clock.TimestampFrequency);
        var initial = clock.GetTimestamp();
        source.Advance(TimeSpan.FromSeconds(3));
        source.Wall = DateTimeOffset.MaxValue;
        Assert.Equal(source.Wall, clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(3), clock.GetElapsedTime(initial));
        source.Wall = DateTimeOffset.MinValue;
        Assert.Equal(source.Wall, clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(3), clock.GetElapsedTime(initial));
    }

    [Fact]
    public void PresentationUsesMonotonicElapsedAndUtcDateWithoutResettingTimestamp()
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        source.Advance(TimeSpan.FromHours(1));
        var timestamp = clock.GetTimestamp();
        var start = new DateTimeOffset(2030, 1, 2, 9, 0, 0, TimeSpan.FromHours(5));
        clock.BeginPresentation(start);
        Assert.True(clock.IsPresentation);
        Assert.Equal(timestamp, clock.GetTimestamp());
        Assert.Equal(start, clock.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow().Offset);
        source.Advance(TimeSpan.FromSeconds(7));
        source.Wall = DateTimeOffset.MinValue;
        Assert.Equal(start.AddSeconds(7), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(7), clock.GetElapsedTime(timestamp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseAndResumeAreIdempotentAndExcludeAllPausedTime(bool presentation)
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        var start = DateTimeOffset.UnixEpoch.AddDays(1);
        if (presentation) clock.BeginPresentation(start);
        clock.Resume();
        source.Advance(TimeSpan.FromSeconds(5));
        clock.Pause();
        var frozenTimestamp = clock.GetTimestamp();
        var frozenDate = clock.GetUtcNow();
        source.Advance(TimeSpan.FromHours(2));
        clock.Pause();
        source.Advance(TimeSpan.FromHours(3));
        Assert.True(clock.IsPaused);
        Assert.Equal(frozenTimestamp, clock.GetTimestamp());
        Assert.Equal(presentation ? frozenDate : source.Wall, clock.GetUtcNow());
        clock.Resume();
        Assert.False(clock.IsPaused);
        Assert.Equal(frozenTimestamp, clock.GetTimestamp());
        source.Advance(TimeSpan.FromSeconds(2));
        clock.Resume();
        source.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.GetElapsedTime(frozenTimestamp));
        if (presentation) Assert.Equal(start.AddSeconds(10), clock.GetUtcNow());
    }

    [Fact]
    public void ModeSwitchesNeverResetTimestampOrIncludePreviouslyPausedTime()
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        var initial = clock.GetTimestamp();
        for (int index = 0; index < 10; index++)
        {
            source.Advance(TimeSpan.FromSeconds(1));
            var previous = clock.GetTimestamp();
            clock.BeginPresentation(DateTimeOffset.UnixEpoch.AddDays(index));
            Assert.Equal(previous, clock.GetTimestamp());
            source.Advance(TimeSpan.FromSeconds(1));
            clock.Pause();
            var paused = clock.GetTimestamp();
            source.Advance(TimeSpan.FromDays(1));
            clock.EndPresentation();
            Assert.False(clock.IsPresentation);
            Assert.True(clock.IsPaused);
            Assert.Equal(source.Wall, clock.GetUtcNow());
            Assert.Equal(paused, clock.GetTimestamp());
            clock.BeginPresentation(DateTimeOffset.UnixEpoch);
            source.Advance(TimeSpan.FromDays(1));
            Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
            Assert.Equal(paused, clock.GetTimestamp());
            clock.Resume();
            clock.EndPresentation();
            clock.EndPresentation();
        }
        Assert.Equal(TimeSpan.FromSeconds(20), clock.GetElapsedTime(initial));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1_000)]
    [InlineData(1_000_000)]
    [InlineData(1_000_000_000)]
    public void SourceFrequencyIsScaledWithoutPollingRoundingLoss(long frequency)
    {
        var source = new ManualClock(frequency);
        var clock = new SessionClock(source);
        var initial = clock.GetTimestamp();
        for (int index = 0; index < 30; index++)
        {
            source.Timestamp++;
            Assert.Equal((long)((decimal)(index + 1) * TimeSpan.TicksPerSecond / frequency), clock.GetTimestamp() - initial);
        }
        source.Timestamp += frequency;
        Assert.Equal((long)((decimal)(frequency + 30) * TimeSpan.TicksPerSecond / frequency), clock.GetTimestamp() - initial);
    }

    [Fact]
    public void SourceRegressionNeverDecreasesExposedTimestamp()
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        source.Advance(TimeSpan.FromSeconds(5));
        var timestamp = clock.GetTimestamp();
        source.Advance(TimeSpan.FromSeconds(-2));
        Assert.Equal(timestamp, clock.GetTimestamp());
        clock.BeginPresentation(DateTimeOffset.UnixEpoch);
        Assert.Equal(timestamp, clock.GetTimestamp());
        clock.Pause();
        clock.Resume();
        source.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), clock.GetElapsedTime(timestamp));
    }

    [Fact]
    public void PresentationCanRestartWithoutRestartingMonotonicTime()
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        clock.BeginPresentation(DateTimeOffset.UnixEpoch);
        source.Advance(TimeSpan.FromSeconds(3));
        var timestamp = clock.GetTimestamp();
        var restart = DateTimeOffset.UnixEpoch.AddYears(5);
        clock.BeginPresentation(restart);
        Assert.Equal(timestamp, clock.GetTimestamp());
        Assert.Equal(restart, clock.GetUtcNow());
        source.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(restart.AddSeconds(2), clock.GetUtcNow());
        clock.EndPresentation();
        source.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), clock.GetElapsedTime(timestamp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidSourceFrequency(long frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionClock(new ManualClock(frequency)));
    }

    [Fact]
    public void DefaultClockUsesSystemWallTime()
    {
        var before = DateTimeOffset.UtcNow;
        var clock = new SessionClock();
        Assert.InRange(clock.GetUtcNow(), before, DateTimeOffset.UtcNow);
        Assert.True(clock.GetTimestamp() >= 0);
    }

    [Fact]
    public void ConcurrentFrozenReadsAndIdempotentPauseAreStable()
    {
        var source = new ManualClock();
        var clock = new SessionClock(source);
        clock.BeginPresentation(DateTimeOffset.UnixEpoch);
        source.Advance(TimeSpan.FromSeconds(1));
        clock.Pause();
        var timestamp = clock.GetTimestamp();
        Parallel.For(0, 100, _ =>
        {
            clock.Pause();
            Assert.Equal(timestamp, clock.GetTimestamp());
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), clock.GetUtcNow());
        });
    }

    private sealed class ManualClock(long frequency = 1_000_000) : TimeProvider
    {
        public long Timestamp { get; set; } = 123456;
        public DateTimeOffset Wall { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => frequency;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Wall;
        public void Advance(TimeSpan elapsed)
        {
            Timestamp += (long)((decimal)elapsed.Ticks * frequency / TimeSpan.TicksPerSecond);
            Wall += elapsed;
        }
    }
}
