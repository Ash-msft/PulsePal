namespace PulsePal.Core;

public enum CompanionProfileId { Nova, Lumi, Kairo }

public sealed record CompanionProfile(CompanionProfileId Id, string Name, string Personality,
    string Description, uint AccentRgb, string Greeting, string FocusEncouragement, string RecoveryEncouragement);

public static class CompanionProfiles
{
    public static IReadOnlyList<CompanionProfile> All { get; } = Array.AsReadOnly(new[]
    {
        new CompanionProfile(CompanionProfileId.Nova, "Nova", "Calm strategist",
            "Clear, thoughtful guidance. A cyan star-crowned companion who makes room for one thing at a time.",
            0x67E8F9, "Let's find a steady rhythm for your day.",
            "One task. Fewer interruptions. I'll keep watch.", "A deliberate pause is part of the plan."),
        new CompanionProfile(CompanionProfileId.Lumi, "Lumi", "Gentle encourager",
            "Warm, patient support. A soft blue companion with a flowing fringe and crescent hair clip.",
            0xA9C7FF, "There's room to make today a little gentler.",
            "Take it one small step at a time. I'm here with you.", "You don't have to earn a moment of rest."),
        new CompanionProfile(CompanionProfileId.Kairo, "Kairo", "Upbeat teammate",
            "Friendly, practical momentum. An electric-blue companion with a swept crest and tech headset.",
            0x55B8FF, "Let's make progress and leave some energy for you.",
            "You've got this. Let's give the next task some space.", "A quick reset can be a good next move.")
    });

    public static CompanionProfile Get(CompanionProfileId id) => All.FirstOrDefault(profile => profile.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id));
}
