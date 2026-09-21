using PulsePal.Core;

namespace PulsePal.Tests;

public sealed class SessionLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private static NotificationDecision Decision(NotificationKind kind, bool allowed) =>
        new(new AttentionNotification(Guid.NewGuid(), Now, kind, "Synthetic test notification"),
            allowed, "Test routing decision");

    private static RecoveryCompletion Completion(RecoveryActivity activity = RecoveryActivity.ScreenBreak,
        double actualSeconds = 5, double representedSeconds = 300, bool accelerated = true) =>
        new(activity, TimeSpan.FromSeconds(actualSeconds), TimeSpan.FromSeconds(representedSeconds),
            accelerated, new RecoveryComparison(70, 40, 30, 60, 90, 72, true, accelerated));

    [Fact]
    public void NewLedgerIsEmptyAndSessionOnly()
    {
        var story = new SessionLedger().Snapshot(Now);
        Assert.Empty(story.CompletedBreaks);
        Assert.Equal(TimeSpan.Zero, story.FocusTime);
        Assert.Equal(0, story.CompletedFocusSessions);
        Assert.Equal(0, story.DeferredNotifications);
        Assert.Equal(0, story.UrgentNotifications);
        Assert.Equal(0, story.CancelledBreaks);
        Assert.False(story.IsFocusActive);
        Assert.Contains("Session-only", story.Description);
        Assert.Contains("SYNTHETIC notifications during focus", story.Description);
        Assert.Contains("Focus inactive", story.Description);
    }

    [Fact]
    public void ActiveFocusAccumulatesWithoutMutatingCompletedTotals()
    {
        var ledger = new SessionLedger();
        ledger.StartFocus(Now);
        var first = ledger.Snapshot(Now.AddMinutes(2));
        var second = ledger.Snapshot(Now.AddMinutes(3));
        Assert.Equal(TimeSpan.FromMinutes(2), first.FocusTime);
        Assert.Equal(TimeSpan.FromMinutes(3), second.FocusTime);
        Assert.Equal(0, second.CompletedFocusSessions);
        Assert.True(second.IsFocusActive);
        Assert.Contains("Focus active", second.Description);
        Assert.Contains("total focus time: 00:03:00", second.Description);

        ledger.EndFocus(Now.AddMinutes(4));
        ledger.StartFocus(Now.AddMinutes(10));
        var next = ledger.Snapshot(Now.AddMinutes(12));
        Assert.Equal(TimeSpan.FromMinutes(6), next.FocusTime);
        Assert.Equal(1, next.CompletedFocusSessions);
        ledger.EndFocus(Now.AddMinutes(13));
        var final = ledger.Snapshot(Now.AddDays(1));
        Assert.Equal(TimeSpan.FromMinutes(7), final.FocusTime);
        Assert.Equal(2, final.CompletedFocusSessions);
        Assert.False(final.IsFocusActive);
    }

    [Fact]
    public void DuplicateStartsPreserveOriginalStartAndDuplicateEndsGiveNoCredit()
    {
        var ledger = new SessionLedger();
        ledger.EndFocus(Now);
        Assert.Equal(0, ledger.Snapshot(Now).CompletedFocusSessions);
        ledger.StartFocus(Now);
        ledger.StartFocus(Now.AddMinutes(2));
        ledger.StartFocus(Now.AddMinutes(-2));
        ledger.EndFocus(Now.AddMinutes(5));
        ledger.EndFocus(Now.AddMinutes(20));
        Assert.Equal(TimeSpan.FromMinutes(5), ledger.Snapshot(Now).FocusTime);
        Assert.Equal(1, ledger.Snapshot(Now).CompletedFocusSessions);
    }

    [Fact]
    public void BackwardTimesAreClampedWithoutDiscardingPriorFocus()
    {
        var ledger = new SessionLedger();
        ledger.StartFocus(Now);
        ledger.EndFocus(Now.AddMinutes(2));
        ledger.StartFocus(Now.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(2), ledger.Snapshot(Now).FocusTime);
        ledger.EndFocus(Now.AddMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(2), ledger.Snapshot(Now).FocusTime);
        Assert.Equal(2, ledger.Snapshot(Now).CompletedFocusSessions);
        Assert.False(ledger.Snapshot(Now).IsFocusActive);
    }

    [Fact]
    public void FocusUsesInstantsRatherThanLocalTimeOffsets()
    {
        var ledger = new SessionLedger();
        ledger.StartFocus(Now.ToOffset(TimeSpan.FromHours(5.5)));
        ledger.EndFocus(Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(-7)));
        Assert.Equal(TimeSpan.FromMinutes(1), ledger.Snapshot(Now).FocusTime);
    }

    [Theory]
    [InlineData(NotificationKind.ManagerMessage, true)]
    [InlineData(NotificationKind.Escalation, true)]
    [InlineData(NotificationKind.CriticalAlert, true)]
    [InlineData(NotificationKind.MeetingReminder, true)]
    [InlineData(NotificationKind.FyiEmail, false)]
    [InlineData(NotificationKind.GroupMessage, false)]
    [InlineData(NotificationKind.LowPriorityAlert, false)]
    public void NotificationAccountingMatchesRouterPriority(NotificationKind kind, bool urgent)
    {
        var shield = new AttentionShield();
        var ledger = new SessionLedger();
        shield.StartFocus(Now);
        var routed = shield.Decide(new AttentionNotification(Guid.NewGuid(), Now, kind, "Synthetic"));
        ledger.RecordNotification(routed, duringFocus: true);
        var story = ledger.Snapshot(Now);
        Assert.Equal(urgent ? 1 : 0, story.UrgentNotifications);
        Assert.Equal(urgent ? 0 : 1, story.DeferredNotifications);
    }

    [Fact]
    public void AllowedDecisionAndExplicitDuringFocusControlAccounting()
    {
        var ledger = new SessionLedger();
        ledger.StartFocus(Now);
        ledger.RecordNotification(Decision(NotificationKind.CriticalAlert, true), false);
        ledger.RecordNotification(Decision(NotificationKind.FyiEmail, false), false);
        ledger.EndFocus(Now);
        ledger.RecordNotification(Decision(NotificationKind.CriticalAlert, false), true);
        ledger.RecordNotification(Decision(NotificationKind.FyiEmail, true), true);
        ledger.RecordNotification(Decision(NotificationKind.ManagerMessage, true), true);
        var story = ledger.Snapshot(Now);
        Assert.Equal(1, story.DeferredNotifications);
        Assert.Equal(1, story.UrgentNotifications);
    }

    [Fact]
    public void CountsAndCompletionsOutliveBoundedRouterHistoryAndIndividualFocusSessions()
    {
        var ledger = new SessionLedger();
        var shield = new AttentionShield();
        var completion = Completion();
        for (var i = 0; i < 1200; i++)
        {
            ledger.StartFocus(Now);
            shield.StartFocus(Now);
            foreach (var kind in new[] { NotificationKind.FyiEmail, NotificationKind.CriticalAlert })
                ledger.RecordNotification(shield.Decide(
                    new AttentionNotification(Guid.NewGuid(), Now, kind, "Synthetic")), true);
            shield.EndFocus(Now.AddSeconds(1));
            ledger.EndFocus(Now.AddSeconds(1));
            ledger.CompleteRecovery(completion);
            ledger.CancelRecovery();
        }
        var story = ledger.Snapshot(Now);
        Assert.Equal(500, shield.History.Count);
        Assert.Equal(1200, story.DeferredNotifications);
        Assert.Equal(1200, story.UrgentNotifications);
        Assert.Equal(1200, story.CompletedFocusSessions);
        Assert.Equal(TimeSpan.FromSeconds(1200), story.FocusTime);
        Assert.Equal(1200, story.CompletedBreaks.Count);
        Assert.Equal(1200, story.CancelledBreaks);
    }

    [Fact]
    public void CancellationGivesNoCompletionCreditAndComparisonsRemainAccessible()
    {
        var ledger = new SessionLedger();
        ledger.CancelRecovery();
        Assert.Empty(ledger.Snapshot(Now).CompletedBreaks);
        var completion = Completion();
        ledger.CompleteRecovery(completion);
        ledger.CancelRecovery();
        var story = ledger.Snapshot(Now);
        Assert.Same(completion, Assert.Single(story.CompletedBreaks));
        Assert.Same(completion.Comparison, story.CompletedBreaks[0].Comparison);
        Assert.Equal(TimeSpan.FromSeconds(5), story.CompletedBreaks[0].ActualElapsed);
        Assert.Equal(TimeSpan.FromMinutes(5), story.CompletedBreaks[0].RepresentedDuration);
        Assert.Equal(2, story.CancelledBreaks);
    }

    [Fact]
    public void SnapshotsCannotBeMutatedAndRemainUnchangedAfterWritesAndReset()
    {
        var ledger = new SessionLedger();
        var first = Completion();
        ledger.CompleteRecovery(first);
        ledger.StartFocus(Now);
        var story = ledger.Snapshot(Now.AddMinutes(1));
        var description = story.Description;
        var list = Assert.IsAssignableFrom<IList<RecoveryCompletion>>(story.CompletedBreaks);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(Completion()));
        Assert.Throws<NotSupportedException>(() => list[0] = Completion());
        Assert.Throws<NotSupportedException>(() => list.Clear());
        ledger.CompleteRecovery(Completion(RecoveryActivity.WaterBreak));
        ledger.EndFocus(Now.AddMinutes(2));
        ledger.Reset();
        Assert.Same(first, Assert.Single(story.CompletedBreaks));
        Assert.Equal(TimeSpan.FromMinutes(1), story.FocusTime);
        Assert.True(story.IsFocusActive);
        Assert.Equal(description, story.Description);
    }

    [Fact]
    public void ResetClearsAllLifetimeAccountingAndAllowsFreshFocus()
    {
        var ledger = new SessionLedger();
        ledger.StartFocus(Now);
        ledger.EndFocus(Now.AddMinutes(2));
        ledger.StartFocus(Now.AddMinutes(3));
        ledger.RecordNotification(Decision(NotificationKind.FyiEmail, false), true);
        ledger.RecordNotification(Decision(NotificationKind.CriticalAlert, true), true);
        ledger.CompleteRecovery(Completion());
        ledger.CancelRecovery();
        ledger.Reset();
        var story = ledger.Snapshot(Now.AddDays(1));
        Assert.Equal(TimeSpan.Zero, story.FocusTime);
        Assert.Equal(0, story.CompletedFocusSessions);
        Assert.Equal(0, story.DeferredNotifications);
        Assert.Equal(0, story.UrgentNotifications);
        Assert.Empty(story.CompletedBreaks);
        Assert.Equal(0, story.CancelledBreaks);
        Assert.False(story.IsFocusActive);
        ledger.EndFocus(Now.AddDays(1));
        ledger.StartFocus(Now);
        ledger.EndFocus(Now.AddSeconds(1));
        Assert.Equal(1, ledger.Snapshot(Now).CompletedFocusSessions);
        Assert.Equal(TimeSpan.FromSeconds(1), ledger.Snapshot(Now).FocusTime);
    }

    [Fact]
    public void DescriptionGroupsActivitiesAndSeparatesActualFromRepresentedTimeAndModes()
    {
        var ledger = new SessionLedger();
        ledger.CompleteRecovery(Completion());
        ledger.CompleteRecovery(Completion());
        ledger.CompleteRecovery(Completion(actualSeconds: 300, accelerated: false));
        ledger.CompleteRecovery(Completion(RecoveryActivity.Breathing, 60, 60, false));
        ledger.CompleteRecovery(Completion(RecoveryActivity.WaterBreak, 60, 60, false));
        ledger.CompleteRecovery(Completion(RecoveryActivity.StretchBreak, 120, 120, false));
        ledger.CancelRecovery();
        var description = ledger.Snapshot(Now).Description;
        Assert.Contains("Session-only", description);
        Assert.Contains("SYNTHETIC notifications", description);
        Assert.Contains("Completed breaks: 6; cancelled breaks: 1", description);
        Assert.Contains("Screen break: 3 completed", description);
        Assert.Equal(1, description.Split("Screen break:").Length - 1);
        Assert.Contains("Accelerated demo: 2 completed, 00:00:10 actual elapsed, 00:10:00 represented", description);
        Assert.Contains("Real-time: 1 completed, 00:05:00 actual elapsed, 00:05:00 represented", description);
        Assert.Contains("Guided breathing: 1 completed", description);
        Assert.Contains("Water break: 1 completed", description);
        Assert.Contains("Stretch break: 1 completed", description);
    }

    [Fact]
    public void RealTimeBreaksAreNotDescribedAsAccelerated()
    {
        var ledger = new SessionLedger();
        ledger.CompleteRecovery(Completion(actualSeconds: 300, accelerated: false));
        Assert.DoesNotContain("Accelerated demo", ledger.Snapshot(Now).Description);
    }

    [Fact]
    public void NullInputsAreRejectedWithoutChangingAccounting()
    {
        var ledger = new SessionLedger();
        Assert.Throws<ArgumentNullException>(() => ledger.RecordNotification(null!, true));
        Assert.Throws<ArgumentNullException>(() => ledger.CompleteRecovery(null!));
        Assert.Empty(ledger.Snapshot(Now).CompletedBreaks);
        Assert.Equal(0, ledger.Snapshot(Now).DeferredNotifications);
    }

    [Fact]
    public void ConcurrentAccountingDoesNotLoseUpdatesAndSnapshotsAreConsistent()
    {
        var ledger = new SessionLedger();
        var deferred = Decision(NotificationKind.FyiEmail, false);
        var urgent = Decision(NotificationKind.CriticalAlert, true);
        var completion = Completion();
        Parallel.For(0, 2000, _ =>
        {
            ledger.RecordNotification(deferred, true);
            ledger.RecordNotification(urgent, true);
            ledger.CompleteRecovery(completion);
            ledger.CancelRecovery();
            var snapshot = ledger.Snapshot(Now);
            Assert.True(snapshot.DeferredNotifications >= snapshot.UrgentNotifications);
            Assert.True(snapshot.UrgentNotifications >= snapshot.CompletedBreaks.Count);
            Assert.True(snapshot.CompletedBreaks.Count >= snapshot.CancelledBreaks);
        });
        var story = ledger.Snapshot(Now);
        Assert.Equal(2000, story.DeferredNotifications);
        Assert.Equal(2000, story.UrgentNotifications);
        Assert.Equal(2000, story.CompletedBreaks.Count);
        Assert.Equal(2000, story.CancelledBreaks);
    }

    [Fact]
    public void ConcurrentDuplicateFocusOperationsOnlyCreditOneSession()
    {
        var ledger = new SessionLedger();
        Parallel.For(0, 1000, _ => ledger.StartFocus(Now));
        Parallel.For(0, 1000, _ => ledger.EndFocus(Now.AddMinutes(1)));
        var story = ledger.Snapshot(Now);
        Assert.Equal(1, story.CompletedFocusSessions);
        Assert.Equal(TimeSpan.FromMinutes(1), story.FocusTime);
        Assert.False(story.IsFocusActive);
    }

    [Fact]
    public void ConcurrentResetAndWritesKeepSnapshotsValid()
    {
        var ledger = new SessionLedger();
        Parallel.For(0, 500, index =>
        {
            if (index % 3 == 0)
                ledger.Reset();
            else
            {
                ledger.StartFocus(Now);
                ledger.EndFocus(Now.AddSeconds(1));
                ledger.CompleteRecovery(Completion());
                ledger.CancelRecovery();
            }
            var story = ledger.Snapshot(Now.AddSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(story.CompletedFocusSessions + (story.IsFocusActive ? 1 : 0)),
                story.FocusTime);
            Assert.All(story.CompletedBreaks, completion => Assert.NotNull(completion.Comparison));
        });
        ledger.Reset();
        Assert.Equal(TimeSpan.Zero, ledger.Snapshot(Now).FocusTime);
        Assert.Empty(ledger.Snapshot(Now).CompletedBreaks);
    }
}
