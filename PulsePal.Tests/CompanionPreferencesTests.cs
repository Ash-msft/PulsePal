using System.Text.Json;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class CompanionPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "CompanionPreferenceTestData", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private string StatePath => Path.Combine(_directory, "state.json");

    public CompanionPreferencesTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"preferences\":{}}")]
    [InlineData("{\"preferences\":{\"displayName\":\"Ada\",\"popupSeconds\":25,\"hydrationReminders\":false}}")]
    public async Task ExistingJsonWithoutNewPropertiesLoadsNovaAndScreenBreak(string json)
    {
        await File.WriteAllTextAsync(StatePath, json);
        var state = await new JsonStateStore(StatePath).LoadAsync();
        Assert.Equal(CompanionProfileId.Nova, state.Preferences.CompanionProfile);
        Assert.Equal(RecoveryActivity.ScreenBreak, state.Preferences.PreferredBreak);
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    public static IEnumerable<object[]> Preferences =>
        from profile in Enum.GetValues<CompanionProfileId>()
        from activity in Enum.GetValues<RecoveryActivity>()
        select new object[] { profile, activity };

    [Theory]
    [MemberData(nameof(Preferences))]
    public async Task AllProfilesAndBreakChoicesRoundTripAsStringEnums(CompanionProfileId profile, RecoveryActivity activity)
    {
        var preferences = new UserPreferences { DisplayName = "Ada", CompanionProfile = profile, PreferredBreak = activity };
        var store = new JsonStateStore(StatePath);
        await store.SaveAsync(new PersistedAppState { Preferences = preferences });
        var state = await store.LoadAsync();
        Assert.Equal(preferences, state.Preferences);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath));
        var saved = json.RootElement.GetProperty("preferences");
        Assert.Equal(profile.ToString(), saved.GetProperty("companionProfile").GetString());
        Assert.Equal(activity.ToString(), saved.GetProperty("preferredBreak").GetString());
        var engine = new DemoEngine();
        engine.Restore(state);
        Assert.Equal(preferences, engine.Preferences);
        Assert.Equal(preferences, engine.ExportState(Start, CharacterState.Normal).Preferences);
    }

    [Theory]
    [InlineData("{\"preferences\":{\"companionProfile\":999}}")]
    [InlineData("{\"preferences\":{\"companionProfile\":-1}}")]
    [InlineData("{\"preferences\":{\"companionProfile\":\"Unknown\"}}")]
    [InlineData("{\"preferences\":{\"companionProfile\":null}}")]
    [InlineData("{\"preferences\":{\"preferredBreak\":999}}")]
    [InlineData("{\"preferences\":{\"preferredBreak\":-1}}")]
    [InlineData("{\"preferences\":{\"preferredBreak\":\"Unknown\"}}")]
    [InlineData("{\"preferences\":{\"preferredBreak\":null}}")]
    public async Task InvalidPreferenceJsonIsRejectedWithoutOverwritingOriginal(string json)
    {
        await File.WriteAllTextAsync(StatePath, json);
        await Assert.ThrowsAsync<JsonException>(() => new JsonStateStore(StatePath).LoadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidPreferencesCannotBeSavedAssignedOrRestored(bool profile)
    {
        var invalid = profile
            ? new UserPreferences { CompanionProfile = (CompanionProfileId)999 }
            : new UserPreferences { PreferredBreak = (RecoveryActivity)999 };
        var state = new PersistedAppState { Preferences = invalid };
        var store = new JsonStateStore(StatePath);
        await store.SaveAsync(new PersistedAppState());
        var original = await File.ReadAllBytesAsync(StatePath);
        Assert.Throws<JsonException>(() => JsonStateStore.Validate(state));
        await Assert.ThrowsAsync<JsonException>(() => store.SaveAsync(state));
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        var engine = new DemoEngine();
        var preferences = engine.Preferences;
        await engine.TickAsync(Start);
        engine.SetRecoveryActive(true);
        var snapshot = engine.Current;
        Assert.Throws<JsonException>(() => engine.Preferences = invalid);
        Assert.Throws<JsonException>(() => engine.Restore(state));
        Assert.Equal(preferences, engine.Preferences);
        Assert.Same(snapshot, engine.Current);
    }

    [Theory]
    [InlineData(CompanionProfileId.Nova)]
    [InlineData(CompanionProfileId.Lumi)]
    [InlineData(CompanionProfileId.Kairo)]
    public async Task ProfilesPersonalizeMorningStressAndRecoveryWithoutChangingFactsOrCategories(CompanionProfileId id)
    {
        var profile = CompanionProfiles.Get(id);
        var provider = new SyntheticWearableProvider(7);
        provider.SetScenario(DemoScenario.RisingStress);
        var engine = new DemoEngine(provider) { Preferences = new UserPreferences { DisplayName = "Ada", CompanionProfile = id } };
        await engine.TickAsync(Start);
        var briefing = engine.MorningBriefing();
        Assert.Equal("Good morning, Ada", briefing.Title);
        Assert.Equal("morning-briefing", briefing.Category);
        Assert.Contains(profile.Greeting, briefing.Text);
        Assert.Contains("3 meetings and 8 priority tasks", briefing.Text);
        Assert.Contains("Reported sleep: 7h 45m", briefing.Text);
        Assert.Contains("non-medical", briefing.Text);
        CompanionMessage? mild = null, peak = null;
        for (int second = 2; second <= 80; second += 2)
        {
            var message = (await engine.TickAsync(Start.AddSeconds(second))).Message;
            if (message?.Category == "sustained-stress") mild = message;
            if (message?.Category == "peak-stress") peak = message;
        }
        Assert.NotNull(mild);
        Assert.NotNull(peak);
        foreach (var message in new[] { mild, peak })
        {
            Assert.Contains(profile.RecoveryEncouragement, message.Text);
            Assert.Contains("not a diagnosis", message.Text);
            Assert.Equal(CharacterState.Concerned, message.Character);
        }
        Assert.Contains("90/100", peak.Text);
        engine.CompleteRecovery(RecoveryActivity.ScreenBreak, Start.AddSeconds(80));
        CompanionMessage? recovery = null;
        for (int second = 82; second <= 150; second += 2)
        {
            var message = (await engine.TickAsync(Start.AddSeconds(second))).Message;
            if (message?.Category == "recovery") recovery = message;
        }
        Assert.NotNull(recovery);
        Assert.Contains(profile.RecoveryEncouragement, recovery.Text);
        Assert.Contains("does not erase last night's sleep", recovery.Text);
        Assert.Contains("synthetic", recovery.Text);
        Assert.Equal(CharacterState.Happy, recovery.Character);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
