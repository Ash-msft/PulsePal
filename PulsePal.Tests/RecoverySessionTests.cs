using PulsePal.Core;

namespace PulsePal.Tests;

public sealed class RecoverySessionTests
{
    [Theory]
    [InlineData(RecoveryActivity.Breathing, 60)]
    [InlineData(RecoveryActivity.ScreenBreak, 300)]
    [InlineData(RecoveryActivity.WaterBreak, 60)]
    [InlineData(RecoveryActivity.StretchBreak, 120)]
    public void EveryOptionRequiresItsEntireMonotonicDurationAndCompletesExactlyOnce(RecoveryActivity activity, int seconds)
    {
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        AssertIdle(session);
        session.Start(activity);
        Assert.True(session.IsActive);
        Assert.Equal(activity, session.ActiveActivity);
        Assert.Equal(TimeSpan.FromSeconds(seconds), session.Remaining);
        Assert.Equal(0, session.Progress);
        for (int i = 0; i < 100; i++) Assert.Null(session.TryComplete());
        clock.Advance(TimeSpan.FromSeconds(seconds / 2.0));
        Assert.Equal(0.5, session.Progress);
        Assert.Equal(TimeSpan.FromSeconds(seconds / 2.0), session.Elapsed);
        Assert.Equal(session.Elapsed, session.Remaining);
        clock.Advance(TimeSpan.FromSeconds(seconds / 2.0) - TimeSpan.FromMilliseconds(50));
        Assert.Null(session.TryComplete());
        Assert.Equal(TimeSpan.FromMilliseconds(50), session.Remaining);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(session.IsActive);
        Assert.Equal(1, session.Progress);
        Assert.Equal(TimeSpan.Zero, session.Remaining);
        Assert.Equal(activity, session.TryComplete());
        Assert.Null(session.TryComplete());
        AssertIdle(session);
    }

    [Fact]
    public void FiveMinuteScreenBreakIgnoresWallClockJumpsInBothDirections()
    {
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.ScreenBreak);
        clock.WallClock = DateTimeOffset.MaxValue;
        Assert.Null(session.TryComplete());
        Assert.Equal(TimeSpan.FromMinutes(5), session.Remaining);
        clock.Advance(TimeSpan.FromMinutes(4));
        clock.WallClock = DateTimeOffset.MinValue;
        Assert.Null(session.TryComplete());
        Assert.Equal(TimeSpan.FromMinutes(1), session.Remaining);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(RecoveryActivity.ScreenBreak, session.TryComplete());
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing)]
    [InlineData(RecoveryActivity.ScreenBreak)]
    [InlineData(RecoveryActivity.WaterBreak)]
    [InlineData(RecoveryActivity.StretchBreak)]
    public void CancelNeverCompletesOrRetainsElapsedTime(RecoveryActivity activity)
    {
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Cancel();
        session.Start(activity);
        clock.Advance(TimeSpan.FromSeconds(20));
        session.Cancel();
        session.Cancel();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(session.TryComplete());
        AssertIdle(session);
        session.Start(activity);
        Assert.Equal(TimeSpan.Zero, session.Elapsed);
        Assert.Equal(RecoveryActivities.Get(activity).Duration, session.Remaining);
    }

    [Fact]
    public void CancelEvenAfterDeadlineDoesNotReportCompletion()
    {
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.ScreenBreak);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromMinutes(5), session.Elapsed);
        Assert.Equal(TimeSpan.Zero, session.Remaining);
        Assert.Equal(1, session.Progress);
        session.Cancel();
        Assert.Null(session.TryComplete());
    }

    [Fact]
    public void StartWhileActiveThrowsWithoutReplacingTheSessionEvenAtDeadline()
    {
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.Breathing);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidOperationException>(() => session.Start(RecoveryActivity.WaterBreak));
        Assert.Equal(RecoveryActivity.Breathing, session.ActiveActivity);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Elapsed);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Throws<InvalidOperationException>(() => session.Start(RecoveryActivity.ScreenBreak));
        Assert.Equal(RecoveryActivity.Breathing, session.TryComplete());
        session.Start(RecoveryActivity.ScreenBreak);
        Assert.Equal(TimeSpan.Zero, session.Elapsed);
    }

    [Fact]
    public void InvalidActivityIsRejectedAtomicallyAndDefaultClockDoesNotCompleteInstantly()
    {
        var session = new RecoverySession();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Start((RecoveryActivity)999));
        AssertIdle(session);
        session.Start(RecoveryActivity.ScreenBreak);
        Assert.Null(session.TryComplete());
        Assert.True(session.IsActive);
        session.Cancel();
    }

    [Fact]
    public void ConcurrentCompletionIsExactlyOnce()
    {
        var clock = new ManualTimeProvider();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.StretchBreak);
        clock.Advance(TimeSpan.FromMinutes(2));
        int completions = 0;
        Parallel.For(0, 100, _ =>
        {
            if (session.TryComplete() == RecoveryActivity.StretchBreak) Interlocked.Increment(ref completions);
        });
        Assert.Equal(1, completions);
        AssertIdle(session);
    }

    private static void AssertIdle(RecoverySession session)
    {
        Assert.False(session.IsActive);
        Assert.Null(session.ActiveActivity);
        Assert.Equal(TimeSpan.Zero, session.Elapsed);
        Assert.Equal(TimeSpan.Zero, session.Remaining);
        Assert.Equal(0, session.Progress);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 123456;
        public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => 1_000_000;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => WallClock;
        public void Advance(TimeSpan elapsed) => _timestamp += (long)(elapsed.TotalSeconds * TimestampFrequency);
    }
}
