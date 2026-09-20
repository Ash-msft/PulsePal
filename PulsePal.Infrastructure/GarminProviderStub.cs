using PulsePal.Core;

namespace PulsePal.Infrastructure;

public sealed class GarminProviderStub : IWearableProvider
{
    public string Name => "Garmin (not connected)";

    public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Garmin integration is not implemented. An authorized Garmin API connection is required; use ReplayWearableProvider for recorded samples.");
    }
}
