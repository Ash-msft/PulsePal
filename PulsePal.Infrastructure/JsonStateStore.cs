using System.Text.Json;
using System.Text.Json.Serialization;
using PulsePal.Core;

namespace PulsePal.Infrastructure;

public sealed class JsonStateStore
{
    private const int MaxFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Bounded striped gates coordinate instances without retaining every path forever.
    private static readonly SemaphoreSlim[] PathGates = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SemaphoreSlim _gate;

    public JsonStateStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulsePal", "state.json"))
    {
    }

    public JsonStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _gate = PathGates[(int)((uint)comparer.GetHashCode(FilePath) % (uint)PathGates.Length)];
    }

    public string FilePath { get; }

    public async Task<PersistedAppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                EnsureMissingPathIsAccessible();
                cancellationToken.ThrowIfCancellationRequested();
                return Validate(new PersistedAppState());
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"Cannot read state file '{FilePath}'.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves atomically. An existing malformed or invalid file is first copied unchanged
    /// to a unique sibling ending in .corrupt.bak; backup failures prevent replacement.
    /// Cancellation observed before the commit leaves the destination unchanged.
    /// </summary>
    public async Task SaveAsync(PersistedAppState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Validate(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length > MaxFileBytes)
            throw new JsonException("State must not exceed 1 MiB.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? scratchPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var candidate = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                scratchPath = candidate;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await PreserveCorruptFileAsync(cancellationToken).ConfigureAwait(false);
            await CommitAsync(scratchPath, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"Cannot save state file '{FilePath}'.", exception);
        }
        finally
        {
            try
            {
                if (scratchPath is not null)
                    File.Delete(scratchPath);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Returns a detached snapshot with copied preferences and read-only history.
    /// Rejects null members, undefined enums, nonfinite/out-of-range scores (0..100),
    /// names longer than 100 characters, popup durations outside 1..300 seconds,
    /// and histories exceeding 500 entries (no trimming), with JsonException.
    /// </summary>
    public static PersistedAppState Validate(PersistedAppState state)
    {
        if (state is null || state.Preferences is null || state.History is null)
            throw new JsonException("State, preferences, and history must not be null.");
        if (state.Preferences.DisplayName is null || state.Preferences.DisplayName.Length > 100)
            throw new JsonException("DisplayName must be nonnull and at most 100 characters.");
        if (state.Preferences.PopupSeconds is < 1 or > 300)
            throw new JsonException("PopupSeconds must be between 1 and 300.");
        if (!Enum.IsDefined(state.Scenario) || !Enum.IsDefined(state.Character))
            throw new JsonException("State contains an undefined scenario or character.");
        if (!Enum.IsDefined(state.Preferences.CompanionProfile) || !Enum.IsDefined(state.Preferences.PreferredBreak))
            throw new JsonException("Preferences contain an undefined companion profile or preferred break.");
        if (state.History.Count > 500)
            throw new JsonException("History must not exceed 500 entries.");

        var history = new DemoHistoryEntry[state.History.Count];
        for (var index = 0; index < history.Length; index++)
        {
            var entry = state.History[index];
            if (entry is null)
                throw new JsonException($"History entry {index} must not be null.");
            if (!Enum.IsDefined(entry.Scenario) || !Enum.IsDefined(entry.State))
                throw new JsonException($"History entry {index} contains an undefined enum value.");
            if (!IsScore(entry.StressScore) || !IsScore(entry.FocusScore))
                throw new JsonException($"History entry {index} scores must be finite and between 0 and 100.");
            history[index] = entry with { };
        }

        return state with
        {
            Preferences = state.Preferences with { },
            History = Array.AsReadOnly(history)
        };
    }

    private static bool IsScore(double score) => double.IsFinite(score) && score is >= 0 and <= 100;

    private async Task CommitAsync(string scratchPath, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Same-directory rename avoids File.Replace's metadata merge on cloud-backed files.
                File.Move(scratchPath, FilePath, overwrite: true);
                return;
            }
            catch (IOException exception) when (RetryableRename(exception, attempt))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception) when (RetryableRename(exception, attempt))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Windows scanners/sync clients can briefly deny rename; persistent permissions/sharing errors still surface.
    private static bool RetryableRename(Exception exception, int attempt) => OperatingSystem.IsWindows() &&
        attempt < 6 && (exception.HResult & 0xffff) is 5 or 32 or 33;

    private void EnsureMissingPathIsAccessible()
    {
        for (var parent = Path.GetDirectoryName(FilePath); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(parent);
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException($"State directory '{parent}' is a file.");
            return;
        }
        throw new IOException($"No accessible parent directory exists for '{FilePath}'.");
    }

    private async Task<PersistedAppState> ReadStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxFileBytes)
            throw new JsonException("State file must not exceed 1 MiB.");

        // Bound the actual read as well as checking Length, even if another writer grows the file.
        var buffer = new byte[MaxFileBytes + 1];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (count > MaxFileBytes)
            throw new JsonException("State file must not exceed 1 MiB.");
        var state = JsonSerializer.Deserialize<PersistedAppState>(buffer.AsSpan(0, count), JsonOptions);
        var snapshot = Validate(state!);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    private async Task PreserveCorruptFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // There is no previous state to preserve.
        }
        catch (DirectoryNotFoundException)
        {
            // A missing destination is handled by the subsequent atomic move.
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(FilePath, $"{FilePath}.{Guid.NewGuid():N}.corrupt.bak", overwrite: false);
        }
    }
}
