using System.Globalization;
using PulsePal.Core;

namespace PulsePal.Tests;

public sealed class RecoveryPrimitiveTests
{
    [Theory]
    [InlineData(RecoveryActivity.Breathing, 60)]
    [InlineData(RecoveryActivity.ScreenBreak, 300)]
    [InlineData(RecoveryActivity.WaterBreak, 60)]
    [InlineData(RecoveryActivity.StretchBreak, 120)]
    public void AcceleratedActivitiesRequireAllFifteenSecondsAndRetainCatalogDuration(
        RecoveryActivity activity, int representedSeconds)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        var catalogBefore = RecoveryActivities.All.ToArray();
        session.Start(activity, accelerated: true);
        AssertMetadata(session, true, 15, representedSeconds);
        Assert.Equal(activity, session.ActiveActivity);
        AssertTiming(session, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        for (var i = 0; i < 20; i++) Assert.Null(session.TryComplete());

        source.Advance(TimeSpan.FromSeconds(7.5));
        AssertTiming(session, TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(15));
        Assert.Null(session.TryComplete());
        source.Advance(TimeSpan.FromSeconds(7.5) - TimeSpan.FromTicks(1));
        Assert.Null(session.TryComplete());
        Assert.Equal(TimeSpan.FromTicks(1), session.Remaining);
        Assert.True(session.Progress < 1);
        source.Advance(TimeSpan.FromTicks(1));
        Assert.True(session.IsActive);
        AssertTiming(session, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        Assert.Equal(activity, session.TryComplete());
        AssertIdle(session);
        AssertMetadata(session, true, 15, representedSeconds);
        source.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 20; i++) Assert.Null(session.TryComplete());
        AssertMetadata(session, true, 15, representedSeconds);
        Assert.Equal(catalogBefore, RecoveryActivities.All.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(representedSeconds), RecoveryActivities.Get(activity).Duration);
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing, 60)]
    [InlineData(RecoveryActivity.ScreenBreak, 300)]
    [InlineData(RecoveryActivity.WaterBreak, 60)]
    [InlineData(RecoveryActivity.StretchBreak, 120)]
    public void DefaultNormalStartUsesFullCatalogDuration(RecoveryActivity activity, int seconds)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        AssertIdle(session);
        AssertMetadata(session, false, 0, 0);
        session.Start(activity);
        AssertMetadata(session, false, seconds, seconds);
        source.Advance(TimeSpan.FromSeconds(15));
        Assert.Null(session.TryComplete());
        AssertTiming(session, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(seconds));
        source.Advance(TimeSpan.FromSeconds(seconds - 15) - TimeSpan.FromTicks(1));
        Assert.Null(session.TryComplete());
        source.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(activity, session.TryComplete());
        AssertIdle(session);
        AssertMetadata(session, false, seconds, seconds);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedClockPauseAndPresentationSwitchesPreserveRecoveryDeadline(
        bool accelerated, bool initiallyPresentation)
    {
        var source = new ManualTimeProvider();
        var clock = new SessionClock(source);
        var presentationDate = new DateTimeOffset(2030, 4, 5, 9, 0, 0, TimeSpan.FromHours(2));
        if (initiallyPresentation) clock.BeginPresentation(presentationDate);
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.StretchBreak, accelerated);
        var duration = TimeSpan.FromSeconds(accelerated ? 15 : 120);
        source.Advance(TimeSpan.FromSeconds(4));
        AssertTiming(session, TimeSpan.FromSeconds(4), duration);
        clock.Pause();
        clock.Pause();
        var pausedTimestamp = clock.GetTimestamp();
        source.Advance(TimeSpan.FromDays(2));
        Assert.Equal(pausedTimestamp, clock.GetTimestamp());
        AssertTiming(session, TimeSpan.FromSeconds(4), duration);
        Assert.Null(session.TryComplete());

        clock.BeginPresentation(presentationDate);
        Assert.True(clock.IsPresentation);
        Assert.True(clock.IsPaused);
        source.Advance(TimeSpan.FromHours(1));
        Assert.Equal(presentationDate.ToUniversalTime(), clock.GetUtcNow());
        clock.EndPresentation();
        Assert.False(clock.IsPresentation);
        Assert.True(clock.IsPaused);
        Assert.Equal(source.GetUtcNow(), clock.GetUtcNow());
        AssertTiming(session, TimeSpan.FromSeconds(4), duration);
        Assert.Null(session.TryComplete());

        clock.Resume();
        clock.Resume();
        source.Advance(TimeSpan.FromSeconds(3));
        clock.BeginPresentation(presentationDate.AddYears(-10));
        AssertTiming(session, TimeSpan.FromSeconds(7), duration);
        source.Advance(TimeSpan.FromSeconds(2));
        clock.EndPresentation();
        AssertTiming(session, TimeSpan.FromSeconds(9), duration);
        source.Advance(duration - TimeSpan.FromSeconds(9) - TimeSpan.FromTicks(1));
        Assert.Null(session.TryComplete());
        source.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(RecoveryActivity.StretchBreak, session.TryComplete());
        AssertIdle(session);
        AssertMetadata(session, accelerated, (int)duration.TotalSeconds, 120);
    }

    [Fact]
    public void StartingWhileSharedClockIsPausedDoesNotConsumeRecoveryTime()
    {
        var source = new ManualTimeProvider();
        var clock = new SessionClock(source);
        source.Advance(TimeSpan.FromSeconds(37));
        clock.Pause();
        var session = new RecoverySession(clock);
        session.Start(RecoveryActivity.WaterBreak, accelerated: true);
        source.Advance(TimeSpan.FromDays(1));
        AssertTiming(session, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        Assert.Null(session.TryComplete());
        clock.Resume();
        source.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(RecoveryActivity.WaterBreak, session.TryComplete());
    }

    [Fact]
    public void AcceleratedDeadlineIgnoresWallClockJumpsAndClampsLatePolling()
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        session.Start(RecoveryActivity.ScreenBreak, accelerated: true);
        source.WallClock = DateTimeOffset.MaxValue;
        Assert.Null(session.TryComplete());
        source.Advance(TimeSpan.FromSeconds(5));
        source.WallClock = DateTimeOffset.MinValue;
        AssertTiming(session, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        Assert.Null(session.TryComplete());
        source.Advance(TimeSpan.FromHours(1));
        AssertTiming(session, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        Assert.Equal(RecoveryActivity.ScreenBreak, session.TryComplete());
        Assert.Null(session.TryComplete());
        AssertMetadata(session, true, 15, 300);
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing, 14)]
    [InlineData(RecoveryActivity.Breathing, 15)]
    [InlineData(RecoveryActivity.Breathing, 16)]
    [InlineData(RecoveryActivity.ScreenBreak, 14)]
    [InlineData(RecoveryActivity.ScreenBreak, 15)]
    [InlineData(RecoveryActivity.ScreenBreak, 16)]
    [InlineData(RecoveryActivity.WaterBreak, 14)]
    [InlineData(RecoveryActivity.WaterBreak, 15)]
    [InlineData(RecoveryActivity.WaterBreak, 16)]
    [InlineData(RecoveryActivity.StretchBreak, 14)]
    [InlineData(RecoveryActivity.StretchBreak, 15)]
    [InlineData(RecoveryActivity.StretchBreak, 16)]
    public void CancellationBeforeAtOrAfterDeadlineNeverEmitsCompletionAndClearsMetadata(
        RecoveryActivity activity, int elapsedSeconds)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        session.Start(activity, accelerated: true);
        source.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        session.Cancel();
        session.Cancel();
        AssertIdle(session);
        AssertMetadata(session, false, 0, 0);
        source.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 20; i++) Assert.Null(session.TryComplete());
        AssertIdle(session);
        AssertMetadata(session, false, 0, 0);
    }

    [Fact]
    public void CancelClearsRetainedCompletionMetadataAndIsSafeWhenNeverStarted()
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        session.Cancel();
        AssertIdle(session);
        AssertMetadata(session, false, 0, 0);
        session.Start(RecoveryActivity.ScreenBreak, accelerated: true);
        source.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(RecoveryActivity.ScreenBreak, session.TryComplete());
        AssertMetadata(session, true, 15, 300);
        session.Cancel();
        AssertIdle(session);
        AssertMetadata(session, false, 0, 0);
        Assert.Null(session.TryComplete());
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing, 60)]
    [InlineData(RecoveryActivity.ScreenBreak, 300)]
    [InlineData(RecoveryActivity.WaterBreak, 60)]
    [InlineData(RecoveryActivity.StretchBreak, 120)]
    public void ConcurrentAcceleratedCompletionReturnsExactlyOneActivityAndRetainsMetadata(
        RecoveryActivity activity, int representedSeconds)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        session.Start(activity, accelerated: true);
        source.Advance(TimeSpan.FromSeconds(15));
        var results = new RecoveryActivity?[64];
        Parallel.For(0, results.Length, index => results[index] = session.TryComplete());
        Assert.Equal(activity, Assert.Single(results, result => result.HasValue));
        AssertIdle(session);
        AssertMetadata(session, true, 15, representedSeconds);
    }

    [Theory]
    [InlineData(RecoveryActivity.Breathing, 60)]
    [InlineData(RecoveryActivity.ScreenBreak, 300)]
    [InlineData(RecoveryActivity.WaterBreak, 60)]
    [InlineData(RecoveryActivity.StretchBreak, 120)]
    public void NormalRestartAfterAcceleratedCompletionResetsModeDurationAndStartTime(
        RecoveryActivity activity, int seconds)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        session.Start(RecoveryActivity.StretchBreak, accelerated: true);
        source.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(RecoveryActivity.StretchBreak, session.TryComplete());
        source.Advance(TimeSpan.FromHours(1));
        session.Start(activity);
        Assert.Equal(activity, session.ActiveActivity);
        AssertMetadata(session, false, seconds, seconds);
        AssertTiming(session, TimeSpan.Zero, TimeSpan.FromSeconds(seconds));
        source.Advance(TimeSpan.FromSeconds(15));
        Assert.Null(session.TryComplete());
        AssertTiming(session, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(seconds));
        source.Advance(TimeSpan.FromSeconds(seconds - 15));
        Assert.Equal(activity, session.TryComplete());
        AssertMetadata(session, false, seconds, seconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidIdleStartIsAtomicEvenWithRetainedCompletionMetadata(bool previouslyCompleted)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        if (previouslyCompleted)
        {
            session.Start(RecoveryActivity.ScreenBreak, accelerated: true);
            source.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(RecoveryActivity.ScreenBreak, session.TryComplete());
        }
        foreach (var accelerated in new[] { false, true })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Start((RecoveryActivity)999, accelerated));
            AssertIdle(session);
            AssertMetadata(session, previouslyCompleted, previouslyCompleted ? 15 : 0, previouslyCompleted ? 300 : 0);
            Assert.Null(session.TryComplete());
        }
        session.Start(RecoveryActivity.WaterBreak);
        AssertMetadata(session, false, 60, 60);
        AssertTiming(session, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActiveStartRejectionPreservesActivityClockAndMetadataEvenAtDeadline(bool accelerated, bool atDeadline)
    {
        var source = new ManualTimeProvider();
        var session = new RecoverySession(source);
        session.Start(RecoveryActivity.ScreenBreak, accelerated);
        var seconds = accelerated ? 15 : 300;
        var elapsed = TimeSpan.FromSeconds(atDeadline ? seconds : 5);
        source.Advance(elapsed);
        Assert.Throws<InvalidOperationException>(() => session.Start(RecoveryActivity.Breathing, !accelerated));
        Assert.Throws<InvalidOperationException>(() => session.Start((RecoveryActivity)999, !accelerated));
        Assert.Equal(RecoveryActivity.ScreenBreak, session.ActiveActivity);
        AssertMetadata(session, accelerated, seconds, 300);
        AssertTiming(session, elapsed, TimeSpan.FromSeconds(seconds));
        if (!atDeadline) Assert.Null(session.TryComplete());
        source.Advance(TimeSpan.FromSeconds(seconds) - elapsed);
        Assert.Equal(RecoveryActivity.ScreenBreak, session.TryComplete());
        AssertMetadata(session, accelerated, seconds, 300);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CaptureUsesAnalysisStressAndWearableRecoveryAndHeartRateWithoutInventingImprovement(
        bool synthetic, bool accelerated)
    {
        var before = Snapshot(20, 80, 65);
        var after = Snapshot(35, 60, 75);
        var comparison = RecoveryComparison.Capture(before, after, synthetic, accelerated);
        Assert.Equal(20d, comparison.StressBefore);
        Assert.Equal(35d, comparison.StressAfter);
        Assert.Equal(80d, comparison.RecoveryBefore);
        Assert.Equal(60d, comparison.RecoveryAfter);
        Assert.Equal(65d, comparison.HeartRateBefore);
        Assert.Equal(75d, comparison.HeartRateAfter);
        Assert.Equal(15d, comparison.StressDelta);
        Assert.Equal(-20d, comparison.RecoveryDelta);
        Assert.Equal(10d, comparison.HeartRateDelta);
        Assert.Equal(synthetic, comparison.IsSynthetic);
        Assert.Equal(accelerated, comparison.IsAccelerated);
        var label = synthetic ? "Synthetic comparison, not a health outcome."
            : "Reported comparison, not evidence of a health outcome or a break's effect.";
        Assert.Equal(label + (accelerated ? " Accelerated demo." : string.Empty)
            + " Stress: 20 → 35 (Δ +15); recovery: 80 → 60 (Δ -20); heart rate (bpm): 65 → 75 (Δ +10).",
            comparison.Description);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MissingSnapshotsLeaveEachUnavailableSideNull(bool missingBefore, bool missingAfter)
    {
        var comparison = RecoveryComparison.Capture(missingBefore ? null : Snapshot(10, 20, 30),
            missingAfter ? null : Snapshot(40, 50, 60), synthetic: false, accelerated: false);
        Assert.Equal(missingBefore ? (double?)null : 10d, comparison.StressBefore);
        Assert.Equal(missingBefore ? (double?)null : 20d, comparison.RecoveryBefore);
        Assert.Equal(missingBefore ? (double?)null : 30d, comparison.HeartRateBefore);
        Assert.Equal(missingAfter ? (double?)null : 40d, comparison.StressAfter);
        Assert.Equal(missingAfter ? (double?)null : 50d, comparison.RecoveryAfter);
        Assert.Equal(missingAfter ? (double?)null : 60d, comparison.HeartRateAfter);
        Assert.Null(comparison.StressDelta);
        Assert.Null(comparison.RecoveryDelta);
        Assert.Null(comparison.HeartRateDelta);
        Assert.Contains($"Stress: {(missingBefore ? "unavailable" : "10")} → {(missingAfter ? "unavailable" : "40")} (Δ unavailable)", comparison.Description);
        Assert.Contains($"recovery: {(missingBefore ? "unavailable" : "20")} → {(missingAfter ? "unavailable" : "50")} (Δ unavailable)", comparison.Description);
        Assert.Contains($"heart rate (bpm): {(missingBefore ? "unavailable" : "30")} → {(missingAfter ? "unavailable" : "60")} (Δ unavailable)", comparison.Description);
    }

    [Theory]
    [InlineData(double.NaN, true)]
    [InlineData(double.PositiveInfinity, true)]
    [InlineData(double.NegativeInfinity, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void CaptureConvertsNonfiniteMetricsToNullWithoutDiscardingFiniteOppositeSide(double invalid, bool invalidBefore)
    {
        var invalidSnapshot = Snapshot(invalid, invalid, invalid);
        var validSnapshot = Snapshot(25, 50, 75);
        var comparison = RecoveryComparison.Capture(invalidBefore ? invalidSnapshot : validSnapshot,
            invalidBefore ? validSnapshot : invalidSnapshot, synthetic: true, accelerated: true);
        Assert.Equal(invalidBefore ? (double?)null : 25d, comparison.StressBefore);
        Assert.Equal(invalidBefore ? 25d : (double?)null, comparison.StressAfter);
        Assert.Equal(invalidBefore ? (double?)null : 50d, comparison.RecoveryBefore);
        Assert.Equal(invalidBefore ? 50d : (double?)null, comparison.RecoveryAfter);
        Assert.Equal(invalidBefore ? (double?)null : 75d, comparison.HeartRateBefore);
        Assert.Equal(invalidBefore ? 75d : (double?)null, comparison.HeartRateAfter);
        Assert.Null(comparison.StressDelta);
        Assert.Null(comparison.RecoveryDelta);
        Assert.Null(comparison.HeartRateDelta);
        Assert.DoesNotContain("NaN", comparison.Description);
        Assert.DoesNotContain("Infinity", comparison.Description);
        Assert.Contains("Δ unavailable", comparison.Description);
    }

    [Fact]
    public void MissingWearableFieldsStayUnavailableRatherThanFallingBackToOtherScores()
    {
        var comparison = RecoveryComparison.Capture(Snapshot(20, null, 70), Snapshot(25, 80, null),
            synthetic: false, accelerated: false);
        Assert.Equal(5d, comparison.StressDelta);
        Assert.Null(comparison.RecoveryBefore);
        Assert.Equal(80d, comparison.RecoveryAfter);
        Assert.Equal(70d, comparison.HeartRateBefore);
        Assert.Null(comparison.HeartRateAfter);
        Assert.Null(comparison.RecoveryDelta);
        Assert.Null(comparison.HeartRateDelta);
        Assert.Contains("recovery: unavailable → 80 (Δ unavailable)", comparison.Description);
        Assert.Contains("heart rate (bpm): 70 → unavailable (Δ unavailable)", comparison.Description);
    }

    [Theory]
    [InlineData(40, 25, -15, "-15")]
    [InlineData(25, 40, 15, "+15")]
    [InlineData(40, 40, 0, "+0")]
    [InlineData(-5, -10, -5, "-5")]
    public void DeltasAreAfterMinusBeforeWithoutClampingOrInventingChanges(double before, double after, double delta, string signed)
    {
        var comparison = RecoveryComparison.Capture(Snapshot(before, before, before), Snapshot(after, after, after),
            synthetic: false, accelerated: false);
        Assert.Equal(delta, comparison.StressDelta);
        Assert.Equal(delta, comparison.RecoveryDelta);
        Assert.Equal(delta, comparison.HeartRateDelta);
        var pair = FormattableString.Invariant($"{before} → {after} (Δ {signed})");
        Assert.Contains($"Stress: {pair}; recovery: {pair}; heart rate (bpm): {pair}.", comparison.Description);
    }

    [Theory]
    [InlineData(double.MinValue, double.MaxValue)]
    [InlineData(double.MaxValue, double.MinValue)]
    public void OverflowingDifferencesAreUnavailableButFiniteMeasurementsAreRetained(double before, double after)
    {
        var comparison = RecoveryComparison.Capture(Snapshot(before, before, before), Snapshot(after, after, after),
            synthetic: false, accelerated: false);
        Assert.Equal(before, comparison.StressBefore);
        Assert.Equal(after, comparison.StressAfter);
        Assert.Equal(before, comparison.RecoveryBefore);
        Assert.Equal(after, comparison.RecoveryAfter);
        Assert.Equal(before, comparison.HeartRateBefore);
        Assert.Equal(after, comparison.HeartRateAfter);
        Assert.Null(comparison.StressDelta);
        Assert.Null(comparison.RecoveryDelta);
        Assert.Null(comparison.HeartRateDelta);
        var pair = $"{before.ToString("G", CultureInfo.InvariantCulture)} → {after.ToString("G", CultureInfo.InvariantCulture)} (Δ unavailable)";
        Assert.Contains($"Stress: {pair}; recovery: {pair}; heart rate (bpm): {pair}.", comparison.Description);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void DescriptionUsesInvariantNumbersUnicodeArrowAndSignedDeltas(string culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var comparison = RecoveryComparison.Capture(Snapshot(50.5, 60.25, 72.5), Snapshot(48.25, 63.75, 72.5),
                synthetic: true, accelerated: true);
            Assert.Equal("Synthetic comparison, not a health outcome. Accelerated demo. Stress: 50.5 → 48.25 (Δ -2.25); recovery: 60.25 → 63.75 (Δ +3.5); heart rate (bpm): 72.5 → 72.5 (Δ +0).",
                comparison.Description);
            Assert.Equal("Reported comparison, not evidence of a health outcome or a break's effect. Stress: unavailable → unavailable (Δ unavailable); recovery: unavailable → unavailable (Δ unavailable); heart rate (bpm): unavailable → unavailable (Δ unavailable).",
                RecoveryComparison.Capture(null, null, synthetic: false, accelerated: false).Description);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void DirectlyConstructedComparisonAlsoGuardsNonfiniteNumbers(double invalid)
    {
        var comparison = new RecoveryComparison(invalid, 20, 30, invalid, invalid, invalid, false, false);
        Assert.Null(comparison.StressDelta);
        Assert.Null(comparison.RecoveryDelta);
        Assert.Null(comparison.HeartRateDelta);
        Assert.Contains("Stress: unavailable → 20 (Δ unavailable); recovery: 30 → unavailable (Δ unavailable); heart rate (bpm): unavailable → unavailable (Δ unavailable).", comparison.Description);
    }

    private static DemoSnapshot Snapshot(double stress, double? recovery, double? heartRate) => new(
        new WearableSample
        {
            StressLevel = 99,
            RecoveryScore = recovery,
            ReadinessScore = 88,
            BodyBattery = 77,
            HeartRate = heartRate,
            RestingHeartRate = 55
        },
        new WorkContext(),
        new AnalysisResult(11, stress, 22, 33, WellnessState.Balanced, Array.Empty<string>()),
        DemoScenario.HealthyDay, false, null, 0, 0, null);

    private static void AssertMetadata(RecoverySession session, bool accelerated, int actualSeconds, int representedSeconds)
    {
        Assert.Equal(accelerated, session.IsAccelerated);
        Assert.Equal(TimeSpan.FromSeconds(actualSeconds), session.ActualDuration);
        Assert.Equal(TimeSpan.FromSeconds(representedSeconds), session.RepresentedDuration);
    }

    private static void AssertTiming(RecoverySession session, TimeSpan elapsed, TimeSpan duration)
    {
        Assert.True(session.IsActive);
        Assert.Equal(elapsed, session.Elapsed);
        Assert.Equal(duration - elapsed, session.Remaining);
        Assert.Equal(elapsed.TotalSeconds / duration.TotalSeconds, session.Progress, 12);
    }

    private static void AssertIdle(RecoverySession session)
    {
        Assert.False(session.IsActive);
        Assert.Null(session.ActiveActivity);
        Assert.Equal(TimeSpan.Zero, session.Elapsed);
        Assert.Equal(TimeSpan.Zero, session.Remaining);
        Assert.Equal(0d, session.Progress);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 123_456_789;
        public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => WallClock;
        public void Advance(TimeSpan elapsed) => _timestamp = checked(_timestamp + elapsed.Ticks);
    }
}
