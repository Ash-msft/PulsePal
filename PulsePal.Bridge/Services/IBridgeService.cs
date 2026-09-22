using System.Net;
using PulsePal.Connected;

namespace PulsePal.Bridge.Services;

internal interface IBridgeService
{
    bool Authenticate(string? authorization);
    bool AllowRequest(IPAddress? address, string operation);
    PendingPairingResponse Pair(CreatePairingRequest request, CancellationToken ct);
    PairingStatusResponse Poll(PairingStatusRequest request, CancellationToken ct);
    SyncRecordsResponse Sync(string? authorization, SyncRecordsRequest request, CancellationToken ct);
    void Disconnect(string? authorization, CancellationToken ct);
}

internal sealed class BridgeRequestException(int statusCode) : Exception
{
    public int StatusCode { get; } = statusCode;
}
