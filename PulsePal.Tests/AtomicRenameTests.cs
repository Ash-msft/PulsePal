using PulsePal.Core;
using PulsePal.Infrastructure;

namespace PulsePal.Tests;

public sealed class AtomicRenameTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "AtomicRenameTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TransientWindowsDeleteLockAllowsAtomicSaveAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = await CreateStore();
        var before = await File.ReadAllBytesAsync(store.FilePath);
        Task save;
        using (var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            save = store.SaveAsync(UpdatedState());
            await Task.Delay(100);
            Assert.False(save.IsCompleted);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
        }
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Updated", (await store.LoadAsync()).Preferences.DisplayName);
        Assert.Equal(new[] { store.FilePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationDuringWindowsRenameRetryPreservesDestinationAndCleansScratch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = await CreateStore();
        var before = await File.ReadAllBytesAsync(store.FilePath);
        using var cancellation = new CancellationTokenSource();
        using (var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var save = store.SaveAsync(UpdatedState(), cancellation.Token);
            await Task.Delay(100);
            Assert.False(save.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
        }
        Assert.Equal(new[] { store.FilePath }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task PersistentWindowsDeleteLockPropagatesIOExceptionWithoutDestroyingState()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = await CreateStore();
        var before = await File.ReadAllBytesAsync(store.FilePath);
        using (var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(UpdatedState()).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
        }
        Assert.Equal(new[] { store.FilePath }, Directory.GetFiles(_directory));
    }

    private async Task<JsonStateStore> CreateStore()
    {
        var store = new JsonStateStore(Path.Combine(_directory, "state.json"));
        await store.SaveAsync(new PersistedAppState());
        return store;
    }

    private static PersistedAppState UpdatedState() => new()
    {
        Preferences = new UserPreferences { DisplayName = "Updated" }
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
