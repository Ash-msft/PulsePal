namespace PulsePal.Core;

public sealed record GuidedEvent(string Id, TourStep Step, TimeSpan Offset, NotificationKind Kind, string Title);

/// <summary>Exactly-once synthetic interruptions; manual advancement flushes the departing step.</summary>
public sealed class GuidedEvents
{
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private static readonly GuidedEvent[] Script =
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

    public IReadOnlyList<GuidedEvent> TakeDue(TourStep step, TimeSpan elapsed, bool finishStep = false)
    {
        lock (_delivered)
            return Script.Where(item => item.Step == step && (finishStep || item.Offset <= elapsed) &&
                _delivered.Add(item.Id)).ToArray();
    }

    public void Reset()
    {
        lock (_delivered) _delivered.Clear();
    }
}
