using PulsePal.Core;

namespace PulsePal.Infrastructure;

public sealed class OuraProviderStub : IWearableProvider
{
    public string Name => "Oura (not connected)";

    public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Oura integration is not implemented. An authorized Oura API connection is required; use ReplayWearableProvider for recorded samples.");
    }
}
