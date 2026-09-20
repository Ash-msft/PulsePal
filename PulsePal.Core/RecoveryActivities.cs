namespace PulsePal.Core;

public enum RecoveryActivity { Breathing, ScreenBreak, WaterBreak, StretchBreak }

public sealed record RecoveryOption(RecoveryActivity Activity, string Title, TimeSpan Duration,
    string Instructions, string CompletionText);

public static class RecoveryActivities
{
    public static IReadOnlyList<RecoveryOption> All { get; } = Array.AsReadOnly(new[]
    {
        new RecoveryOption(RecoveryActivity.Breathing, "Guided breathing", TimeSpan.FromSeconds(60),
            "Follow the light: inhale 4 seconds, hold 2, exhale 6. Keep it comfortable and stop if you feel unwell.",
            "You made room for a minute of breathing."),
        new RecoveryOption(RecoveryActivity.ScreenBreak, "Away from the screen", TimeSpan.FromMinutes(5),
            "Step away from your screen for five minutes. Look into the distance or take a comfortable walk. You can hide this timer; I'll let you know when the break ends.",
            "Your five-minute screen break is complete. Return at your own pace."),
        new RecoveryOption(RecoveryActivity.WaterBreak, "Water break", TimeSpan.FromMinutes(1),
            "Pause and get a glass of water if you need one. There is no intake target; follow any personal fluid guidance.",
            "Your water-break timer is complete. A small pause still counts."),
        new RecoveryOption(RecoveryActivity.StretchBreak, "Gentle movement", TimeSpan.FromMinutes(2),
            "Change position, relax your shoulders or move gently in a way that's comfortable for you. Skip movements that hurt; seated movement is welcome.",
            "Your movement-break timer is complete. Ease back in when you're ready.")
    });

    public static RecoveryOption Get(RecoveryActivity activity) => All.FirstOrDefault(option => option.Activity == activity)
        ?? throw new ArgumentOutOfRangeException(nameof(activity));
}
