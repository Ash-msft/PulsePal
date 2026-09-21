using System.Globalization;

namespace PulsePal.Core;

public sealed record RecoveryCompletion(RecoveryActivity Activity, TimeSpan ActualElapsed,
    TimeSpan RepresentedDuration, bool IsAccelerated, RecoveryComparison Comparison);

public sealed class SessionLedger
{
    private readonly object _gate = new();
    private readonly List<RecoveryCompletion> _completedBreaks = new();
    private DateTimeOffset? _focusStartedAt;
    private TimeSpan _focusTime;
    private int _completedFocusSessions;
    private int _deferredNotifications;
    private int _urgentNotifications;
    private int _cancelledBreaks;

    public void StartFocus(DateTimeOffset now)
    {
        lock (_gate)
        {
            _focusStartedAt ??= now;
        }
    }

    public void EndFocus(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_focusStartedAt is not { } startedAt)
                return;

            _focusTime += Elapsed(startedAt, now);
            _completedFocusSessions++;
            _focusStartedAt = null;
        }
    }

    public void RecordNotification(NotificationDecision decision, bool duringFocus)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            if (!duringFocus)
                return;

            if (!decision.Allowed)
                _deferredNotifications++;
            else if (decision.Notification.Kind is NotificationKind.ManagerMessage
                or NotificationKind.Escalation
                or NotificationKind.CriticalAlert
                or NotificationKind.MeetingReminder)
                _urgentNotifications++;
        }
    }

    public void CompleteRecovery(RecoveryCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            _completedBreaks.Add(completion);
        }
    }

    public void CancelRecovery()
    {
        lock (_gate)
        {
            _cancelledBreaks++;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _focusStartedAt = null;
            _focusTime = TimeSpan.Zero;
            _completedFocusSessions = 0;
            _deferredNotifications = 0;
            _urgentNotifications = 0;
            _completedBreaks.Clear();
            _cancelledBreaks = 0;
        }
    }

    public SessionStory Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            var focusTime = _focusTime + (_focusStartedAt is { } startedAt
                ? Elapsed(startedAt, now) : TimeSpan.Zero);
            return new SessionStory(focusTime, _completedFocusSessions, _deferredNotifications,
                _urgentNotifications, _completedBreaks.ToArray(), _cancelledBreaks,
                _focusStartedAt.HasValue);
        }
    }

    private static TimeSpan Elapsed(DateTimeOffset start, DateTimeOffset end) =>
        end > start ? end - start : TimeSpan.Zero;
}

public sealed class SessionStory
{
    internal SessionStory(TimeSpan focusTime, int completedFocusSessions, int deferredNotifications,
        int urgentNotifications, RecoveryCompletion[] completedBreaks, int cancelledBreaks,
        bool isFocusActive)
    {
        FocusTime = focusTime;
        CompletedFocusSessions = completedFocusSessions;
        DeferredNotifications = deferredNotifications;
        UrgentNotifications = urgentNotifications;
        CompletedBreaks = Array.AsReadOnly(completedBreaks);
        CancelledBreaks = cancelledBreaks;
        IsFocusActive = isFocusActive;
    }

    public TimeSpan FocusTime { get; }
    public int CompletedFocusSessions { get; }
    public int DeferredNotifications { get; }
    public int UrgentNotifications { get; }
    public IReadOnlyList<RecoveryCompletion> CompletedBreaks { get; }
    public int CancelledBreaks { get; }
    public bool IsFocusActive { get; }

    public string Description
    {
        get
        {
            var summary = $"Session-only summary. Focus {(IsFocusActive ? "active" : "inactive")}; "
                + $"total focus time: {Duration(FocusTime)} (includes active elapsed); "
                + $"completed focus sessions: {CompletedFocusSessions}. "
                + $"SYNTHETIC notifications during focus: {DeferredNotifications} deferred, "
                + $"{UrgentNotifications} urgent allowed. "
                + $"Completed breaks: {CompletedBreaks.Count}; cancelled breaks: {CancelledBreaks}.";

            var groups = CompletedBreaks.GroupBy(completion => completion.Activity).Select(group =>
            {
                var modes = group.GroupBy(completion => completion.IsAccelerated).Select(mode =>
                    $"{(mode.Key ? "Accelerated demo" : "Real-time")}: {mode.Count()} completed, "
                    + $"{Duration(mode.Aggregate(TimeSpan.Zero, (total, item) => total + item.ActualElapsed))} actual elapsed, "
                    + $"{Duration(mode.Aggregate(TimeSpan.Zero, (total, item) => total + item.RepresentedDuration))} represented");
                return $"{ActivityName(group.Key)}: {group.Count()} completed ({string.Join("; ", modes)}).";
            });
            var breaks = string.Join(" ", groups);
            return breaks.Length == 0 ? summary : $"{summary} {breaks}";
        }
    }

    private static string Duration(TimeSpan duration) => duration.ToString("c", CultureInfo.InvariantCulture);

    private static string ActivityName(RecoveryActivity activity) => activity switch
    {
        RecoveryActivity.Breathing => "Guided breathing",
        RecoveryActivity.ScreenBreak => "Screen break",
        RecoveryActivity.WaterBreak => "Water break",
        RecoveryActivity.StretchBreak => "Stretch break",
        _ => activity.ToString()
    };
}
