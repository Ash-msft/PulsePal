using System.Text;
using System.Text.Json;
using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "PersistenceTestData", Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_directory);

    private string StatePath => Path.Combine(_directory, "state.json");

    [Fact]
    public void ConstructorsExposeAbsoluteStablePathsWithoutDefaultPathIo()
    {
        var first = new JsonStateStore();
        var second = new JsonStateStore();
        Assert.True(Path.IsPathFullyQualified(first.FilePath));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulsePal", "state.json"), first.FilePath);
        Assert.Equal(first.FilePath, second.FilePath);

        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), StatePath);
        Assert.Equal(Path.GetFullPath(relativePath), new JsonStateStore(relativePath).FilePath);
    }

    [Fact]
    public async Task MissingFileReturnsDefaultsWithoutCreatingFile()
    {
        AssertStateEqual(new PersistedAppState(), await new JsonStateStore(StatePath).LoadAsync());
        Assert.False(File.Exists(StatePath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"preferences\":{}}")]
    public async Task MissingJsonPropertiesUseDefaults(string json)
    {
        await File.WriteAllTextAsync(StatePath, json);
        AssertStateEqual(new PersistedAppState(), await new JsonStateStore(StatePath).LoadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task RoundTripPreservesEveryFieldAndWritesCamelCaseWithStringEnums()
    {
        var expected = CreateState(7);
        var store = new JsonStateStore(StatePath);
        await store.SaveAsync(expected);
        AssertStateEqual(expected, await new JsonStateStore(StatePath).LoadAsync());

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(StatePath));
        var root = document.RootElement;
        Assert.Equal(expected.Preferences.DisplayName, root.GetProperty("preferences").GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("scenario").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("character").ValueKind);
        var entry = root.GetProperty("history")[0];
        Assert.Equal(JsonValueKind.String, entry.GetProperty("scenario").ValueKind);
        Assert.Equal(JsonValueKind.String, entry.GetProperty("state").ValueKind);
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task LoadAcceptsCaseInsensitivePropertyNames()
    {
        const string json = "{\"PREFERENCES\":{\"DISPLAYNAME\":\"Ada\",\"HYDRATIONREMINDERS\":false,\"POPUPSECONDS\":25},\"SCENARIO\":\"RisingStress\",\"CHARACTER\":\"Concerned\",\"LASTSAVED\":\"2026-09-20T12:00:00+05:30\",\"HISTORY\":[{\"TIMESTAMP\":\"2026-09-20T12:00:00+05:30\",\"SCENARIO\":\"PoorSleepDay\",\"STATE\":\"Fatigued\",\"STRESSSCORE\":12.5,\"FOCUSSCORE\":87.5}]}";
        await File.WriteAllTextAsync(StatePath, json);
        var timestamp = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(5.5));
        AssertStateEqual(new PersistedAppState
        {
            Preferences = new UserPreferences { DisplayName = "Ada", HydrationReminders = false, PopupSeconds = 25 },
            Scenario = DemoScenario.RisingStress,
            Character = CharacterState.Concerned,
            LastSaved = timestamp,
            History = new[] { new DemoHistoryEntry(timestamp, DemoScenario.PoorSleepDay, WellnessState.Fatigued, 12.5, 87.5) }
        }, await new JsonStateStore(StatePath).LoadAsync());
    }

    [Fact]
    public async Task MalformedJsonPreservesExactOriginalBytes()
    {
        byte[] original = [0x7b, 0x22, 0xff, 0x00, 0x0d, 0x0a];
        await File.WriteAllBytesAsync(StatePath, original);
        await Assert.ThrowsAsync<JsonException>(() => new JsonStateStore(StatePath).LoadAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"preferences\":null}")]
    [InlineData("{\"preferences\":[]}")]
    [InlineData("{\"preferences\":{\"displayName\":null}}")]
    [InlineData("{\"preferences\":{\"popupSeconds\":0}}")]
    [InlineData("{\"preferences\":{\"popupSeconds\":301}}")]
    [InlineData("{\"history\":null}")]
    [InlineData("{\"history\":{}}")]
    [InlineData("{\"history\":[null]}")]
    [InlineData("{\"scenario\":999}")]
    [InlineData("{\"character\":999}")]
    [InlineData("{\"scenario\":\"NotAScenario\"}")]
    [InlineData("{\"history\":[{\"scenario\":999}]}")]
    [InlineData("{\"history\":[{\"state\":999}]}")]
    [InlineData("{\"history\":[{\"stressScore\":-1}]}")]
    [InlineData("{\"history\":[{\"stressScore\":101}]}")]
    [InlineData("{\"history\":[{\"focusScore\":-1}]}")]
    [InlineData("{\"history\":[{\"focusScore\":101}]}")]
    [InlineData("{\"history\":[{\"stressScore\":1e400}]}")]
    [InlineData("{\"history\":[{\"focusScore\":\"NaN\"}]}")]
    public async Task InvalidJsonShapeOrValuesAreRejectedWithoutChangingFile(string json)
    {
        var original = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(StatePath, original);
        await Assert.ThrowsAsync<JsonException>(() => new JsonStateStore(StatePath).LoadAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("preferences")]
    [InlineData("history")]
    [InlineData("entry")]
    [InlineData("name-null")]
    [InlineData("name-long")]
    [InlineData("popup-low")]
    [InlineData("popup-high")]
    [InlineData("scenario")]
    [InlineData("character")]
    [InlineData("entry-scenario")]
    [InlineData("entry-state")]
    [InlineData("history-long")]
    public async Task ValidateAndSaveRejectInvalidStatesWithoutTouchingExistingFile(string invalidCase)
    {
        var valid = CreateState(1);
        var invalid = invalidCase switch
        {
            "state" => null!,
            "preferences" => valid with { Preferences = null! },
            "history" => valid with { History = null! },
            "entry" => valid with { History = new DemoHistoryEntry[] { null! } },
            "name-null" => valid with { Preferences = valid.Preferences with { DisplayName = null! } },
            "name-long" => valid with { Preferences = valid.Preferences with { DisplayName = new string('x', 101) } },
            "popup-low" => valid with { Preferences = valid.Preferences with { PopupSeconds = 0 } },
            "popup-high" => valid with { Preferences = valid.Preferences with { PopupSeconds = 301 } },
            "scenario" => valid with { Scenario = (DemoScenario)(-1) },
            "character" => valid with { Character = (CharacterState)int.MaxValue },
            "entry-scenario" => valid with { History = new[] { valid.History[0] with { Scenario = (DemoScenario)999 } } },
            "entry-state" => valid with { History = new[] { valid.History[0] with { State = (WellnessState)999 } } },
            "history-long" => valid with { History = Enumerable.Repeat(valid.History[0], 501).ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };
        Assert.Throws<JsonException>(() => JsonStateStore.Validate(invalid));
        await AssertRejectedSavePreservesFile(invalid);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task BothScoresMustBeFiniteAndInRangeInValidateAndSave(double score)
    {
        var valid = CreateState(1);
        foreach (var entry in new[]
        {
            valid.History[0] with { StressScore = score },
            valid.History[0] with { FocusScore = score }
        })
        {
            var invalid = valid with { History = new[] { entry } };
            Assert.Throws<JsonException>(() => JsonStateStore.Validate(invalid));
            await AssertRejectedSavePreservesFile(invalid);
        }
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(100, 300, 500)]
    public async Task BoundaryValuesAreAcceptedWithoutTrimming(int nameLength, int popupSeconds, int historyCount)
    {
        var state = CreateState(1) with
        {
            Preferences = new UserPreferences { DisplayName = new string('x', nameLength), PopupSeconds = popupSeconds },
            History = Enumerable.Range(0, historyCount)
                .Select(i => new DemoHistoryEntry(DateTimeOffset.UnixEpoch.AddMinutes(i), DemoScenario.HealthyDay,
                    WellnessState.Balanced, i % 2 == 0 ? 0 : 100, i % 2 == 0 ? 100 : 0)).ToArray()
        };
        AssertStateEqual(state, JsonStateStore.Validate(state));
        var store = new JsonStateStore(StatePath);
        await store.SaveAsync(state);
        AssertStateEqual(state, await store.LoadAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadRejectsOversizedHistoryAndDisplayName(bool oversizedHistory)
    {
        var state = CreateState(1);
        state = oversizedHistory
            ? state with { History = Enumerable.Repeat(state.History[0], 501).ToArray() }
            : state with { Preferences = state.Preferences with { DisplayName = new string('x', 101) } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        await File.WriteAllBytesAsync(StatePath, bytes);
        await Assert.ThrowsAsync<JsonException>(() => new JsonStateStore(StatePath).LoadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StatePath));
    }

    [Fact]
    public void ValidateReturnsDetachedRecordsAndReadOnlyHistory()
    {
        var state = CreateState(1);
        var source = state.History.ToArray();
        state = state with { History = source };
        var validated = JsonStateStore.Validate(state);
        AssertStateEqual(state, validated);
        Assert.NotSame(state, validated);
        Assert.NotSame(state.Preferences, validated.Preferences);
        Assert.NotSame(source, validated.History);
        for (var i = 0; i < source.Length; i++)
            Assert.NotSame(source[i], validated.History[i]);

        var original = validated.History[0];
        source[0] = source[0] with { StressScore = 99 };
        Assert.Equal(original, validated.History[0]);
        Assert.False(validated.History is DemoHistoryEntry[]);
        if (validated.History is IList<DemoHistoryEntry> list)
        {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = source[0]);
            Assert.Throws<NotSupportedException>(() => list.Add(source[0]));
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledSaveAndLoadLeaveExistingFileUntouched(bool corrupt)
    {
        var store = new JsonStateStore(StatePath);
        if (corrupt)
            await File.WriteAllTextAsync(StatePath, "{corrupt\r\n");
        else
            await store.SaveAsync(CreateState(1));
        var original = await File.ReadAllBytesAsync(StatePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(CreateState(2), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task DirectoryAsFilePathPropagatesIOException()
    {
        var store = new JsonStateStore(_directory);
        await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(CreateState(1)));
        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public async Task FileAsParentDirectoryPropagatesIOException()
    {
        var parent = Path.Combine(_directory, "parent-file");
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(parent, original);
        var store = new JsonStateStore(Path.Combine(parent, "state.json"));
        await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(CreateState(1)));
        Assert.Equal(original, await File.ReadAllBytesAsync(parent));
    }

    [Fact]
    public async Task LockedFilePropagatesIOExceptionAndCleansUpScratchFile()
    {
        var store = new JsonStateStore(StatePath);
        await store.SaveAsync(CreateState(1));
        var original = await File.ReadAllBytesAsync(StatePath);
        using (var locked = new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(CreateState(2)));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
        await store.SaveAsync(CreateState(3));
        AssertStateEqual(CreateState(3), await store.LoadAsync());
    }

    [Fact]
    public async Task SaveCreatesMissingParentDirectories()
    {
        var store = new JsonStateStore(Path.Combine(_directory, "nested", "deeper", "state.json"));
        AssertStateEqual(new PersistedAppState(), await store.LoadAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(store.FilePath)));
        var state = CreateState(1);
        await store.SaveAsync(state);
        AssertStateEqual(state, await store.LoadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingOverCorruptFileBacksUpExactBytesThenReplacesIt(bool invalidShape)
    {
        var original = invalidShape ? Encoding.UTF8.GetBytes("{\"preferences\":null}\r\n") : new byte[] { 0xff, 0x00, 0x7b, 0x0d, 0x0a };
        await File.WriteAllBytesAsync(StatePath, original);
        var store = new JsonStateStore(StatePath);
        var expected = CreateState(2);
        await store.SaveAsync(expected);
        AssertStateEqual(expected, await store.LoadAsync());
        var backup = Assert.Single(Directory.GetFiles(_directory, "state.json.*.corrupt.bak"));
        var suffix = backup[(StatePath.Length + 1)..^".corrupt.bak".Length];
        Assert.True(Guid.TryParse(suffix, out _));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);

        await store.SaveAsync(CreateState(3));
        Assert.Single(Directory.GetFiles(_directory, "state.json.*.corrupt.bak"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        AssertStateEqual(CreateState(3), await store.LoadAsync());
    }

    [Fact]
    public async Task LoadRejectsFileOverOneMiBEvenWhenJsonWouldOtherwiseBeValid()
    {
        var original = Encoding.UTF8.GetBytes("{}" + new string(' ', 1024 * 1024 - 1));
        Assert.Equal(1024 * 1024 + 1, original.Length);
        await File.WriteAllBytesAsync(StatePath, original);
        await Assert.ThrowsAsync<JsonException>(() => new JsonStateStore(StatePath).LoadAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MultipleInstancesConcurrentlySaveAndLoadOnlyCompleteSnapshots()
    {
        var snapshots = Enumerable.Range(0, 24).Select(CreateState).ToArray();
        await new JsonStateStore(StatePath).SaveAsync(snapshots[0]);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = snapshots.Select(async snapshot =>
        {
            var store = new JsonStateStore(StatePath);
            await start.Task;
            for (var i = 0; i < 3; i++)
            {
                await store.SaveAsync(snapshot);
                await Task.Yield();
            }
        });
        var readers = Enumerable.Range(0, 6).Select(async _ =>
        {
            var store = new JsonStateStore(StatePath);
            await start.Task;
            for (var i = 0; i < 30; i++)
            {
                var loaded = await store.LoadAsync();
                var expected = Assert.Single(snapshots, s => s.Preferences.DisplayName == loaded.Preferences.DisplayName);
                AssertStateEqual(expected, loaded);
                await Task.Yield();
            }
        });
        var tasks = writers.Concat(readers).ToArray();
        start.SetResult(true);
        await Task.WhenAll(tasks);
        var final = await new JsonStateStore(StatePath).LoadAsync();
        AssertStateEqual(Assert.Single(snapshots, s => s.Preferences.DisplayName == final.Preferences.DisplayName), final);
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
    }

    private async Task AssertRejectedSavePreservesFile(PersistedAppState invalid)
    {
        var store = new JsonStateStore(StatePath);
        await store.SaveAsync(CreateState(0));
        var original = await File.ReadAllBytesAsync(StatePath);
        await Assert.ThrowsAsync<JsonException>(() => store.SaveAsync(invalid));
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));

        var corrupt = Encoding.UTF8.GetBytes("{corrupt\r\n");
        await File.WriteAllBytesAsync(StatePath, corrupt);
        await Assert.ThrowsAsync<JsonException>(() => store.SaveAsync(invalid));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(StatePath));
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_directory));
        await File.WriteAllBytesAsync(StatePath, original);
    }

    private static PersistedAppState CreateState(int marker)
    {
        var timestamp = new DateTimeOffset(2026, 9, 20, 12, 34, 56, TimeSpan.FromHours(5.5)).AddMinutes(marker);
        return new PersistedAppState
        {
            Preferences = new UserPreferences { DisplayName = $"User {marker} \u03a9", HydrationReminders = marker % 2 == 0, PopupSeconds = marker + 1 },
            Scenario = DemoScenario.DeepFocusSession,
            Character = CharacterState.Thinking,
            LastSaved = timestamp,
            History = new[]
            {
                new DemoHistoryEntry(timestamp.AddHours(-1), DemoScenario.RisingStress, WellnessState.Stressed, marker + 0.25, 99.5 - marker),
                new DemoHistoryEntry(timestamp, DemoScenario.RecoveryAfterBreak, WellnessState.Recovering, marker + 0.5, 100 - marker)
            }
        };
    }

    private static void AssertStateEqual(PersistedAppState expected, PersistedAppState actual)
    {
        Assert.Equal(expected.Preferences, actual.Preferences);
        Assert.Equal(expected.Scenario, actual.Scenario);
        Assert.Equal(expected.Character, actual.Character);
        Assert.Equal(expected.LastSaved, actual.LastSaved);
        Assert.Equal(expected.LastSaved?.Offset, actual.LastSaved?.Offset);
        Assert.Equal(expected.History.ToArray(), actual.History.ToArray());
        Assert.Equal(expected.History.Select(e => e.Timestamp.Offset).ToArray(), actual.History.Select(e => e.Timestamp.Offset).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
