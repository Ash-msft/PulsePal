namespace PulsePal.Core;

public enum DemoScenario { HealthyDay, DeepFocusSession, RisingStress, MeetingOverload, RecoveryAfterBreak, PoorSleepDay }
public enum WellnessState { Balanced, Focused, Stressed, HighStress, Fatigued, Recovering, BurnoutRisk }
public enum CharacterState { Normal, Focused, Happy, Encouraging, Concerned, Thinking, Celebrating, Resting }
public enum NotificationKind { ManagerMessage, Escalation, CriticalAlert, MeetingReminder, FyiEmail, GroupMessage, LowPriorityAlert }

public sealed record WearableSample
{
    public DateTimeOffset Timestamp { get; init; }
    public double? HeartRate { get; init; }
    public double? RestingHeartRate { get; init; }
    public double? HeartRateVariability { get; init; }
    public double? StressLevel { get; init; }
    public int? Steps { get; init; }
    public double? Calories { get; init; }
    public int? ActiveMinutes { get; init; }
    public double? SleepScore { get; init; }
    public int? TotalSleepMinutes { get; init; }
    public int? DeepSleepMinutes { get; init; }
    public int? RemSleepMinutes { get; init; }
    public int? LightSleepMinutes { get; init; }
    public double? ReadinessScore { get; init; }
    public double? RecoveryScore { get; init; }
    public double? BloodOxygen { get; init; }
    public double? RespirationRate { get; init; }
    public double? BodyBattery { get; init; }
    public double? FocusScore { get; init; }
}

public sealed record WorkContext
{
    public string ActiveApplication { get; init; } = "Visual Studio";
    public TimeSpan IdleTime { get; init; }
    public int AppSwitchCount { get; init; }
    public int MeetingsToday { get; init; }
    public int UpcomingMeetings { get; init; }
    public int PriorityTasks { get; init; }
    public int EscalationCount { get; init; }
    public int UnreadImportantItems { get; init; }
}

public sealed record AnalysisResult(double FocusScore, double StressScore, double FatigueScore,
    double BurnoutRiskScore, WellnessState State, IReadOnlyList<string> Reasons);
public sealed record CompanionMessage(string Title, string Text, CharacterState Character, string Category);
public sealed record AttentionNotification(Guid Id, DateTimeOffset Timestamp, NotificationKind Kind, string Title);
public sealed record NotificationDecision(AttentionNotification Notification, bool Allowed, string Reason);
public sealed record FocusSessionSummary(TimeSpan Duration, int DelayedCount, int UrgentCount);
public sealed record DemoSnapshot(WearableSample Wearable, WorkContext Context, AnalysisResult Analysis,
    DemoScenario Scenario, bool IsFocusActive, DateTimeOffset? FocusStartedAt, int QueuedNotifications,
    int UrgentNotifications, CompanionMessage? Message)
{
    public long Generation { get; init; }
}

public interface IWearableProvider
{
    string Name { get; }
    Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed record UserPreferences
{
    public string DisplayName { get; init; } = "Ashwani";
    public bool HydrationReminders { get; init; } = true;
    public int PopupSeconds { get; init; } = 18;
    public CompanionProfileId CompanionProfile { get; init; } = CompanionProfileId.Nova;
    public RecoveryActivity PreferredBreak { get; init; } = RecoveryActivity.ScreenBreak;
}
public sealed record DemoHistoryEntry(DateTimeOffset Timestamp, DemoScenario Scenario, WellnessState State, double StressScore, double FocusScore);
public sealed record PersistedAppState
{
    public UserPreferences Preferences { get; init; } = new();
    public DemoScenario Scenario { get; init; } = DemoScenario.HealthyDay;
    public CharacterState Character { get; init; } = CharacterState.Normal;
    public DateTimeOffset? LastSaved { get; init; }
    public IReadOnlyList<DemoHistoryEntry> History { get; init; } = Array.Empty<DemoHistoryEntry>();
}
