using System;
using System.Collections.Generic;

namespace PulsePal.Core;

public sealed class AttentionShield
{
    private const int HistoryLimit = 500;
    private readonly object _gate = new();
    private readonly List<AttentionNotification> _pending = new();
    private readonly Queue<NotificationDecision> _history = new();
    private DateTimeOffset? _focusStartedAt;
    private int _urgentCount;

    public bool IsFocusActive
    {
        get { lock (_gate) { return _focusStartedAt.HasValue; } }
    }

    public DateTimeOffset? FocusStartedAt
    {
        get { lock (_gate) { return _focusStartedAt; } }
    }

    public int UrgentCount
    {
        get { lock (_gate) { return _urgentCount; } }
    }

    public IReadOnlyList<AttentionNotification> PendingNotifications
    {
        get { lock (_gate) { return new List<AttentionNotification>(_pending).AsReadOnly(); } }
    }

    public IReadOnlyList<NotificationDecision> History
    {
        get { lock (_gate) { return Array.AsReadOnly(_history.ToArray()); } }
    }

    public void StartFocus(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_focusStartedAt.HasValue)
                return;

            _focusStartedAt = now;
            _urgentCount = 0;
        }
    }

    public FocusSessionSummary? EndFocus(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_focusStartedAt is not { } startedAt)
                return null;

            var duration = now - startedAt;
            var summary = new FocusSessionSummary(
                duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                _pending.Count,
                _urgentCount);

            foreach (var notification in _pending)
                Record(new NotificationDecision(notification, true, "Released after focus session ended."));

            _pending.Clear();
            _focusStartedAt = null;
            return summary;
        }
    }

    public NotificationDecision Decide(AttentionNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        lock (_gate)
        {
            bool urgent = notification.Kind is NotificationKind.ManagerMessage
                or NotificationKind.Escalation
                or NotificationKind.CriticalAlert
                or NotificationKind.MeetingReminder;
            bool active = _focusStartedAt.HasValue;
            bool allowed = !active || urgent;

            if (active && urgent)
                _urgentCount++;
            if (!allowed)
                _pending.Add(notification);

            var decision = new NotificationDecision(notification, allowed,
                !active ? "Allowed outside focus session."
                : urgent ? "Allowed priority notification during focus."
                : "Deferred until focus session ends.");
            Record(decision);
            return decision;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _focusStartedAt = null;
            _urgentCount = 0;
            _pending.Clear();
            _history.Clear();
        }
    }

    private void Record(NotificationDecision decision)
    {
        _history.Enqueue(decision);
        if (_history.Count > HistoryLimit)
            _history.Dequeue();
    }
}
