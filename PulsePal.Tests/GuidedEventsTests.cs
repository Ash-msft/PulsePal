using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class GuidedEventsTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly GuidedEvent[] Expected =
    [
        new("project-update", TourStep.Interruptions, TimeSpan.Zero, NotificationKind.FyiEmail,
            "FYI: the project notes are ready for your next review"),
        new("group-chat", TourStep.Interruptions, TimeSpan.FromSeconds(3), NotificationKind.GroupMessage,
            "Team chat: ideas for Friday's demo are in the group thread"),
        new("approval", TourStep.Interruptions, TimeSpan.FromSeconds(6), NotificationKind.ManagerMessage,
            "Manager: {name}, can you approve the revised delivery plan?"),
        new("meeting", TourStep.MeetingStress, TimeSpan.Zero, NotificationKind.MeetingReminder,
            "Meeting reminder: delivery check-in starts in five minutes"),
        new("customer", TourStep.MeetingStress, TimeSpan.FromSeconds(4), NotificationKind.Escalation,
            "Customer escalation: a trial account is blocked; please review the handoff")
    ];

    [Theory]
    [InlineData(TourStep.Interruptions)]
    [InlineData(TourStep.MeetingStress)]
    public void TimedEventsArriveExactlyAtOffsetsAndOnlyOnceDespiteRepeatedTicks(TourStep step)
    {
        var events = new GuidedEvents();
        foreach (var expected in Expected.Where(item => item.Step == step))
        {
            Assert.Empty(events.TakeDue(step, expected.Offset - TimeSpan.FromTicks(1)));
            Assert.Equal(expected, Assert.Single(events.TakeDue(step, expected.Offset)));
            for (int tick = 0; tick < 100; tick++)
                Assert.Empty(events.TakeDue(step, expected.Offset));
        }

        Assert.Empty(events.TakeDue(step, TimeSpan.FromDays(1)));
        Assert.Empty(events.TakeDue(step, TimeSpan.Zero));
        Assert.Empty(events.TakeDue(step, TimeSpan.Zero, finishStep: true));
        var otherStep = step == TourStep.Interruptions ? TourStep.MeetingStress : TourStep.Interruptions;
        Assert.Equal(Expected.Where(item => item.Step == otherStep),
            events.TakeDue(otherStep, TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData(TourStep.Idle)]
    [InlineData(TourStep.Briefing)]
    [InlineData(TourStep.Focus)]
    [InlineData(TourStep.Recovery)]
    [InlineData(TourStep.Comparison)]
    [InlineData(TourStep.Summary)]
    public void UnscriptedStepsNeverDeliverOrConsumeAnotherStepsEvents(TourStep step)
    {
        var events = new GuidedEvents();
        Assert.Empty(events.TakeDue(step, TimeSpan.FromDays(1)));
        Assert.Empty(events.TakeDue(step, TimeSpan.Zero, finishStep: true));
        Assert.Equal(Expected, TakeAll(events));
    }

    [Theory]
    [InlineData(TourStep.Interruptions, -1)]
    [InlineData(TourStep.Interruptions, 0)]
    [InlineData(TourStep.Interruptions, 3)]
    [InlineData(TourStep.Interruptions, 6)]
    [InlineData(TourStep.MeetingStress, -1)]
    [InlineData(TourStep.MeetingStress, 0)]
    [InlineData(TourStep.MeetingStress, 4)]
    public void ManualFlushReturnsOnlyRemainingEventsInOrderRegardlessOfElapsedTime(TourStep step, int seconds)
    {
        var events = new GuidedEvents();
        var elapsed = TimeSpan.FromSeconds(seconds);
        var expected = Expected.Where(item => item.Step == step).ToArray();
        var timed = events.TakeDue(step, elapsed);
        Assert.Equal(expected.Where(item => item.Offset <= elapsed), timed);

        var flushed = events.TakeDue(step, TimeSpan.Zero, finishStep: true);
        Assert.Equal(expected.Where(item => item.Offset > elapsed), flushed);
        Assert.Equal(expected, timed.Concat(flushed));
        for (int tick = 0; tick < 100; tick++)
        {
            Assert.Empty(events.TakeDue(step, TimeSpan.Zero, finishStep: true));
            Assert.Empty(events.TakeDue(step, TimeSpan.FromDays(1)));
        }
    }

    [Fact]
    public void EarlyNextFlushesDepartingStepWithoutConsumingNewStepsTimedEvents()
    {
        var source = new ManualTimeProvider();
        var tour = AtStep(source, TourStep.Interruptions);
        var events = new GuidedEvents();
        var delivered = new List<GuidedEvent>(events.TakeDue(tour.Step, tour.TimeInStep));
        Assert.Equal(Expected[0], Assert.Single(delivered));
        var previous = tour.Step;

        Assert.True(tour.Next());
        Assert.Equal(TourStep.MeetingStress, tour.Step);
        Assert.Equal(TimeSpan.Zero, tour.TimeInStep);
        // Next has reset TimeInStep; flush the previous step using the new zero elapsed time.
        delivered.AddRange(events.TakeDue(previous, tour.TimeInStep, finishStep: true));
        Assert.Equal(Expected.Take(3), delivered);
        Assert.Equal(Expected[3], Assert.Single(events.TakeDue(tour.Step, tour.TimeInStep)));
        delivered.Add(Expected[3]);

        for (int next = 0; next < 100; next++)
        {
            Assert.False(tour.Next(peakReady: true));
            Assert.Empty(events.TakeDue(tour.Step, tour.TimeInStep));
            Assert.Empty(events.TakeDue(previous, tour.TimeInStep, finishStep: true));
        }

        source.Advance(TimeSpan.FromSeconds(4));
        delivered.AddRange(events.TakeDue(tour.Step, tour.TimeInStep));
        Assert.Equal(Expected, delivered);
        Assert.False(tour.Next(peakReady: true));
        source.Advance(TimeSpan.FromSeconds(4));
        previous = tour.Step;
        Assert.True(tour.Next(peakReady: true));
        Assert.Equal(TourStep.Recovery, tour.Step);
        Assert.Empty(events.TakeDue(previous, tour.TimeInStep, finishStep: true));
        Assert.Empty(events.TakeDue(tour.Step, tour.TimeInStep));
    }

    [Theory]
    [InlineData(TourStep.Interruptions, 3)]
    [InlineData(TourStep.MeetingStress, 4)]
    public void PausedSessionFreezesEventDeadlineAndResumeDoesNotCatchUpWallTime(TourStep step, int nextOffset)
    {
        var source = new ManualTimeProvider();
        var engine = new DemoEngine(new SyntheticWearableProvider(7), new SessionClock(source));
        engine.StartPresentation();
        var tour = AtStep(engine.Clock, step);
        var events = new GuidedEvents();
        var expected = Expected.Where(item => item.Step == step).ToArray();
        Assert.Equal(expected[0], Assert.Single(events.TakeDue(step, tour.TimeInStep)));
        source.Advance(TimeSpan.FromSeconds(nextOffset) - TimeSpan.FromTicks(1));
        var frozenElapsed = tour.TimeInStep;
        var frozenNow = engine.Clock.GetUtcNow();

        engine.PausePresentation();
        for (int tick = 0; tick < 100; tick++)
        {
            source.Advance(TimeSpan.FromMinutes(1));
            Assert.True(engine.IsPresentationPaused);
            Assert.Equal(frozenElapsed, tour.TimeInStep);
            Assert.Equal(frozenNow, engine.Clock.GetUtcNow());
            Assert.Empty(events.TakeDue(step, tour.TimeInStep));
        }

        engine.ResumePresentation();
        Assert.False(engine.IsPresentationPaused);
        Assert.Empty(events.TakeDue(step, tour.TimeInStep));
        source.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(expected[1], Assert.Single(events.TakeDue(step, tour.TimeInStep)));
        Assert.Empty(events.TakeDue(step, tour.TimeInStep));
        Assert.Equal(expected.Skip(2), events.TakeDue(step, tour.TimeInStep, finishStep: true));
    }

    [Fact]
    public void ResetReplaysIdenticalIdsTitlesKindsAndOffsetsWithoutChangingEarlierResults()
    {
        var events = new GuidedEvents();
        var first = TakeAll(events);
        Assert.Equal(Expected, first);
        Assert.Empty(TakeAll(events));
        events.Reset();
        events.Reset();

        var replay = new List<GuidedEvent>();
        foreach (var expected in Expected)
            replay.AddRange(events.TakeDue(expected.Step, expected.Offset));

        Assert.Equal(Expected, replay);
        Assert.Equal(first, replay);
        Assert.Equal(Expected, first);
        Assert.Empty(TakeAll(events));
    }

    [Fact]
    public void ResetAfterPartialDeliveryRearmsBothDeliveredAndStillPendingEvents()
    {
        var events = new GuidedEvents();
        Assert.Equal(Expected[0], Assert.Single(events.TakeDue(TourStep.Interruptions, TimeSpan.Zero)));
        Assert.Equal(Expected[3], Assert.Single(events.TakeDue(TourStep.MeetingStress, TimeSpan.Zero)));
        events.Reset();
        Assert.Equal(Expected, TakeAll(events));
        Assert.Empty(TakeAll(events));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptedNotificationsPreservePersonalizedTitlesKindsAndFocusRouting(bool focus)
    {
        var engine = new DemoEngine(new SyntheticWearableProvider(7))
        {
            Preferences = new UserPreferences { DisplayName = "Asha" }
        };
        if (focus) engine.StartFocus(Start);
        var events = new GuidedEvents();
        var decisions = DeliverAll(events, engine, Start);

        Assert.Equal(5, decisions.Length);
        Assert.Equal(5, decisions.Select(item => item.Notification.Id).Distinct().Count());
        for (int i = 0; i < decisions.Length; i++)
        {
            Assert.Equal(Expected[i].Kind, decisions[i].Notification.Kind);
            Assert.Equal(Start, decisions[i].Notification.Timestamp);
            Assert.Equal(!focus || i >= 2, decisions[i].Allowed);
            Assert.Equal(i == 2 ? "Manager: Asha, can you approve the revised delivery plan?" : Expected[i].Title,
                decisions[i].Notification.Title);
        }
        Assert.Equal(decisions, engine.NotificationHistory);
        Assert.Equal(decisions.Where(item => !item.Allowed).Select(item => item.Notification),
            engine.PendingNotifications);
        Assert.Empty(DeliverAll(events, engine, Start));
        var story = engine.Ledger.Snapshot(Start);
        Assert.Equal(focus ? 2 : 0, story.DeferredNotifications);
        Assert.Equal(focus ? 3 : 0, story.UrgentNotifications);
    }

    [Fact]
    public async Task LedgerRetainsGuidedTotalsAfterReleaseAndBothHistoriesEvictOriginalEntries()
    {
        var engine = new DemoEngine(new SyntheticWearableProvider(7));
        var events = new GuidedEvents();
        engine.StartFocus(Start);
        var decisions = DeliverAll(events, engine, Start);
        var firstSample = await engine.TickAsync(Start);
        var summary = engine.EndFocus(Start.AddSeconds(10));
        Assert.NotNull(summary);
        Assert.Equal(2, summary.DelayedCount);
        Assert.Equal(3, summary.UrgentCount);
        Assert.Empty(engine.PendingNotifications);
        foreach (var deferred in decisions.Where(item => !item.Allowed))
            Assert.Contains(engine.NotificationHistory,
                item => item.Allowed && item.Notification == deferred.Notification);

        for (int i = 1; i <= 501; i++)
        {
            var now = Start.AddSeconds(10 + i);
            Assert.True(engine.SimulateNotification(NotificationKind.FyiEmail, now, "Outside focus").Allowed);
            await engine.TickAsync(now);
        }

        Assert.Equal(500, engine.NotificationHistory.Count);
        Assert.Equal(500, engine.History.Count);
        Assert.DoesNotContain(engine.History, item => item.Timestamp == firstSample.Wearable.Timestamp);
        foreach (var decision in decisions)
            Assert.DoesNotContain(engine.NotificationHistory,
                item => item.Notification.Id == decision.Notification.Id);
        Assert.Empty(DeliverAll(events, engine, Start.AddSeconds(511)));
        var story = engine.Ledger.Snapshot(Start.AddSeconds(511));
        Assert.Equal(2, story.DeferredNotifications);
        Assert.Equal(3, story.UrgentNotifications);
        Assert.Equal(1, story.CompletedFocusSessions);
        Assert.Equal(TimeSpan.FromSeconds(10), story.FocusTime);
        Assert.False(story.IsFocusActive);
        Assert.Empty(engine.PendingNotifications);
    }

    private static GuidedEvent[] TakeAll(GuidedEvents events) =>
        events.TakeDue(TourStep.Interruptions, TimeSpan.Zero, finishStep: true)
            .Concat(events.TakeDue(TourStep.MeetingStress, TimeSpan.Zero, finishStep: true)).ToArray();

    private static NotificationDecision[] DeliverAll(GuidedEvents events, DemoEngine engine, DateTimeOffset now) =>
        TakeAll(events).Select(item => engine.SimulateNotification(item.Kind, now,
            item.Title.Replace("{name}", engine.Preferences.DisplayName, StringComparison.Ordinal))).ToArray();

    private static GuidedTour AtStep(TimeProvider clock, TourStep step)
    {
        var tour = new GuidedTour(clock);
        tour.Start();
        while (tour.Step != step) Assert.True(tour.Next());
        return tour;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(_ticks);
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
