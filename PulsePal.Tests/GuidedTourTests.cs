using PulsePal.Core;

namespace PulsePal.Tests;

public sealed class GuidedTourTests
{
    [Fact]
    public void DefaultClockAndIdleAreSafe()
    {
        var tour = new GuidedTour();
        Assert.Equal(TourStep.Idle, tour.Step);
        Assert.False(tour.IsActive);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        Assert.Equal(string.Empty, tour.WaitingReason);
        Assert.False(tour.Next(true, false, true));
        Assert.False(tour.Update(true, false, true));
        tour.Start();
        Assert.Equal(TourStep.Briefing, tour.Step);
        Assert.True(tour.IsActive);
        Assert.True(tour.TimeInStep >= TimeSpan.Zero);
    }

    [Theory]
    [InlineData(TourStep.Briefing, 8)]
    [InlineData(TourStep.Focus, 8)]
    [InlineData(TourStep.Interruptions, 12)]
    [InlineData(TourStep.Comparison, 8)]
    public void AutomaticOrdinaryStepsAdvanceExactlyAtTheirDeadline(TourStep step, int seconds)
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, step);
        for (int i = 0; i < 100; i++) Assert.False(tour.Update());
        clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        Assert.False(tour.Update());
        Assert.Equal(step, tour.Step);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(tour.Update());
        Assert.Equal((TourStep)((int)step + 1), tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        for (int i = 0; i < 100; i++) Assert.False(tour.Update());
    }

    [Theory]
    [InlineData(TourStep.Briefing)]
    [InlineData(TourStep.Focus)]
    [InlineData(TourStep.Interruptions)]
    [InlineData(TourStep.Comparison)]
    public void ManualNextAdvancesOrdinaryStepsEarlyAndOnlyOneStep(TourStep step)
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, step);
        Assert.True(tour.Next(true, false, true));
        Assert.Equal((TourStep)((int)step + 1), tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MeetingRequiresBothEightSecondsAndCurrentPeakReadiness(bool manual, bool peakReady)
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.MeetingStress);
        for (int i = 0; i < 100; i++)
            Assert.False(Advance(tour, manual, peakReady, false, true));
        clock.Advance(TimeSpan.FromSeconds(8) - TimeSpan.FromTicks(1));
        Assert.False(Advance(tour, manual, peakReady, false, true));
        Assert.Contains("eight seconds", tour.WaitingReason);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(peakReady, Advance(tour, manual, peakReady, false, true));
        Assert.Equal(peakReady ? TourStep.Recovery : TourStep.MeetingStress, tour.Step);
        if (!peakReady)
        {
            Assert.Contains("peak context", tour.WaitingReason);
            clock.Advance(TimeSpan.FromDays(1));
            Assert.False(Advance(tour, manual));
            Assert.True(Advance(tour, manual, peakReady: true));
        }
        Assert.Equal(TourStep.Recovery, tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        Assert.False(Advance(tour, manual));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlierPeakReadinessIsNotLatched(bool manual)
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.MeetingStress);
        Assert.False(Advance(tour, manual, peakReady: true));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.False(Advance(tour, manual));
        Assert.Equal(TourStep.MeetingStress, tour.Step);
        Assert.True(Advance(tour, manual, peakReady: true));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void RecoveryUsesOnlyResolutionAndInactivityNotTimeOrPeak(bool manual, bool active, bool resolved)
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.Recovery);
        bool expected = resolved && !active;
        Assert.Equal(expected, Advance(tour, manual, true, active, resolved));
        if (!expected)
        {
            clock.Advance(TimeSpan.FromDays(10));
            for (int i = 0; i < 100; i++)
                Assert.False(Advance(tour, manual, true, active, resolved));
            Assert.Equal(TourStep.Recovery, tour.Step);
            Assert.Contains(active ? "still running" : "Choose", tour.WaitingReason);
            Assert.True(Advance(tour, manual, recoveryResolved: true));
        }
        Assert.Equal(TourStep.Comparison, tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        Assert.False(tour.Update(true, false, true));
    }

    [Fact]
    public void RepeatedNextCannotFastForwardGatedStepsAndSummaryIsTerminal()
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.Briefing);
        Assert.True(tour.Next());
        Assert.True(tour.Next());
        Assert.True(tour.Next());
        for (int i = 0; i < 100; i++) Assert.False(tour.Next(true, false, true));
        Assert.Equal(TourStep.MeetingStress, tour.Step);
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(tour.Next(true, false, true));
        Assert.Equal(TourStep.Recovery, tour.Step);
        for (int i = 0; i < 100; i++) Assert.False(tour.Next(true, true, true));
        Assert.True(tour.Next(recoveryResolved: true));
        Assert.Equal(TourStep.Comparison, tour.Step);
        Assert.True(tour.Next());
        Assert.Equal(TourStep.Summary, tour.Step);
        Assert.False(tour.IsActive);
        Assert.Empty(tour.WaitingReason);
        clock.Advance(TimeSpan.FromDays(1));
        var elapsed = tour.TimeInStep;
        for (int i = 0; i < 100; i++)
        {
            Assert.False(tour.Next(true, false, true));
            Assert.False(tour.Update(true, false, true));
        }
        Assert.Equal(TourStep.Summary, tour.Step);
        Assert.Equal(elapsed, tour.TimeInStep);
    }

    [Fact]
    public void OverdueUpdateDoesNotSkipStepsOrCarryExcessTimeForward()
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.Briefing);
        while (tour.IsActive)
        {
            var step = tour.Step;
            clock.Advance(TimeSpan.FromDays(1));
            Assert.True(tour.Update(true, false, true));
            Assert.Equal((TourStep)((int)step + 1), tour.Step);
            Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
            Assert.False(tour.Update());
        }
        Assert.Equal(TourStep.Summary, tour.Step);
    }

    [Theory]
    [InlineData(TourStep.Idle)]
    [InlineData(TourStep.Briefing)]
    [InlineData(TourStep.Focus)]
    [InlineData(TourStep.Interruptions)]
    [InlineData(TourStep.MeetingStress)]
    [InlineData(TourStep.Recovery)]
    [InlineData(TourStep.Comparison)]
    [InlineData(TourStep.Summary)]
    public void StopAndRestartResetEveryStepAndItsTimestamp(TourStep step)
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, step);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(step is >= TourStep.Briefing and <= TourStep.Comparison, tour.IsActive);
        Assert.False(string.IsNullOrWhiteSpace(tour.Title));
        Assert.False(string.IsNullOrWhiteSpace(tour.Narrative));
        tour.Stop();
        tour.Stop();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(TourStep.Idle, tour.Step);
        Assert.False(tour.IsActive);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        Assert.Empty(tour.WaitingReason);
        Assert.False(tour.Next(true, false, true));
        Assert.False(tour.Update(true, false, true));
        tour.Start();
        Assert.Equal(TourStep.Briefing, tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        clock.Advance(TimeSpan.FromSeconds(7));
        tour.Start();
        Assert.Equal(TourStep.Briefing, tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        Assert.False(tour.Update());
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(tour.Update());
        Assert.Equal(TourStep.Focus, tour.Step);
    }

    [Fact]
    public void WallClockJumpsDoNotAffectElapsedTimeOrGates()
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.Briefing);
        clock.WallClock = DateTimeOffset.MaxValue;
        Assert.False(tour.Update());
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        clock.Advance(TimeSpan.FromSeconds(7));
        clock.WallClock = DateTimeOffset.MinValue;
        Assert.Equal(TimeSpan.FromSeconds(7), tour.TimeInStep);
        Assert.False(tour.Update());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tour.Update());
        Assert.True(tour.Next());
        Assert.True(tour.Next());
        clock.WallClock = DateTimeOffset.MaxValue;
        Assert.False(tour.Next(peakReady: true));
        clock.Advance(TimeSpan.FromSeconds(8));
        clock.WallClock = DateTimeOffset.MinValue;
        Assert.True(tour.Update(peakReady: true));
        Assert.Equal(TourStep.Recovery, tour.Step);
    }

    [Fact]
    public void FrozenSharedClockPreservesTourAndRecoveryTimeWhileCallerSkipsUpdates()
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.Recovery);
        var recovery = new RecoverySession(clock);
        recovery.Start(RecoveryActivity.Breathing);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(tour.Update(recoveryActive: true));
        clock.IsPaused = true;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(10), tour.TimeInStep);
        Assert.Equal(TimeSpan.FromSeconds(10), recovery.Elapsed);
        Assert.Equal(TourStep.Recovery, tour.Step);
        clock.IsPaused = false;
        clock.Advance(TimeSpan.FromSeconds(49));
        Assert.Null(recovery.TryComplete());
        Assert.False(tour.Update(recoveryActive: true));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RecoveryActivity.Breathing, recovery.TryComplete());
        Assert.True(tour.Update(recoveryActive: recovery.IsActive, recoveryResolved: true));
        Assert.Equal(TourStep.Comparison, tour.Step);
        clock.Advance(TimeSpan.FromSeconds(3));
        clock.IsPaused = true;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(3), tour.TimeInStep);
        clock.IsPaused = false;
        Assert.False(tour.Update());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(tour.Update());
        Assert.Equal(TourStep.Summary, tour.Step);
    }

    [Fact]
    public void ConcurrentUpdatesAtOneDeadlineProduceExactlyOneTransition()
    {
        var clock = new ManualTimeProvider();
        var tour = AtStep(clock, TourStep.Briefing);
        clock.Advance(TimeSpan.FromSeconds(8));
        int transitions = 0;
        Parallel.For(0, 100, _ =>
        {
            if (tour.Update()) Interlocked.Increment(ref transitions);
        });
        Assert.Equal(1, transitions);
        Assert.Equal(TourStep.Focus, tour.Step);
    }

    private static bool Advance(GuidedTour tour, bool manual, bool peakReady = false, bool recoveryActive = false, bool recoveryResolved = false)
        => manual ? tour.Next(peakReady, recoveryActive, recoveryResolved) : tour.Update(peakReady, recoveryActive, recoveryResolved);

    private static GuidedTour AtStep(ManualTimeProvider clock, TourStep step)
    {
        var tour = new GuidedTour(clock);
        if (step == TourStep.Idle) return tour;
        tour.Start();
        while (tour.Step != step)
        {
            if (tour.Step == TourStep.MeetingStress) clock.Advance(TimeSpan.FromSeconds(8));
            Assert.True(tour.Next(true, false, true));
        }
        return tour;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 123456;
        public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UnixEpoch;
        public bool IsPaused { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => WallClock;

        public void Advance(TimeSpan elapsed)
        {
            if (!IsPaused) _timestamp += elapsed.Ticks;
        }
    }
}
