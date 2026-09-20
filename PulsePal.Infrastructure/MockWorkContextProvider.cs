using PulsePal.Core;

namespace PulsePal.Infrastructure;

public sealed class MockWorkContextProvider
{
    public WorkContext GetContext(DemoScenario scenario) => scenario switch
    {
        DemoScenario.HealthyDay => new WorkContext
        {
            ActiveApplication = "Visual Studio",
            AppSwitchCount = 4,
            MeetingsToday = 2,
            UpcomingMeetings = 1,
            PriorityTasks = 3,
            UnreadImportantItems = 2
        },
        DemoScenario.DeepFocusSession => new WorkContext
        {
            ActiveApplication = "Visual Studio",
            AppSwitchCount = 0,
            MeetingsToday = 0,
            UpcomingMeetings = 0,
            PriorityTasks = 1,
            EscalationCount = 0,
            UnreadImportantItems = 0
        },
        DemoScenario.RisingStress => new WorkContext
        {
            ActiveApplication = "Outlook",
            AppSwitchCount = 12,
            MeetingsToday = 3,
            UpcomingMeetings = 2,
            PriorityTasks = 8,
            EscalationCount = 3,
            UnreadImportantItems = 10
        },
        DemoScenario.MeetingOverload => new WorkContext
        {
            ActiveApplication = "Microsoft Teams",
            AppSwitchCount = 25,
            MeetingsToday = 9,
            UpcomingMeetings = 4,
            PriorityTasks = 8,
            EscalationCount = 5,
            UnreadImportantItems = 18
        },
        DemoScenario.RecoveryAfterBreak => new WorkContext
        {
            ActiveApplication = "Walking break",
            IdleTime = TimeSpan.FromMinutes(15),
            AppSwitchCount = 0,
            MeetingsToday = 1,
            UpcomingMeetings = 0,
            PriorityTasks = 1,
            EscalationCount = 0,
            UnreadImportantItems = 0
        },
        DemoScenario.PoorSleepDay => new WorkContext
        {
            ActiveApplication = "Visual Studio",
            AppSwitchCount = 3,
            MeetingsToday = 1,
            UpcomingMeetings = 1,
            PriorityTasks = 2,
            EscalationCount = 0,
            UnreadImportantItems = 2
        },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown demo scenario.")
    };
}
