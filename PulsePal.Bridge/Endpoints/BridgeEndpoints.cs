using Microsoft.AspNetCore.Http.HttpResults;
using PulsePal.Bridge.Services;
using PulsePal.Connected;

namespace PulsePal.Bridge.Endpoints;

internal static class BridgeEndpoints
{
    public static void MapBridge(this WebApplication app)
    {
        var group = app.MapGroup("/v1").WithTags("Connected bridge");
        group.MapPost("/pairing", Created<PendingPairingResponse> (
            CreatePairingRequest request, IBridgeService service, CancellationToken ct) =>
            TypedResults.Created("/v1/pairing/status", service.Pair(request, ct)))
            .WithName("RequestPairing").WithSummary("Request explicit desktop approval")
            .WithDescription("Consumes a one-time challenge. Approval is available only in the desktop process.")
            .ProducesProblem(400).ProducesProblem(409).ProducesProblem(429);
        group.MapPost("/pairing/status", Ok<PairingStatusResponse> (
            PairingStatusRequest request, IBridgeService service, CancellationToken ct) =>
            TypedResults.Ok(service.Poll(request, ct)))
            .WithName("PollPairing").WithSummary("Poll desktop approval")
            .WithDescription("Requires the poll secret; returns the approved token only once.")
            .ProducesProblem(400).ProducesProblem(429);
        group.MapPost("/records", Ok<SyncRecordsResponse> (
            SyncRecordsRequest request, HttpContext context, IBridgeService service, CancellationToken ct) =>
            TypedResults.Ok(service.Sync(context.Request.Headers.Authorization, request, ct)))
            .WithName("SyncRecords").WithSummary("Synchronize selected source measurements")
            .WithDescription("Authenticated version-aware upserts preserve original source provenance.")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(429);
        group.MapDelete("/session", NoContent (
            HttpContext context, IBridgeService service, CancellationToken ct) =>
            {
                service.Disconnect(context.Request.Headers.Authorization, ct);
                return TypedResults.NoContent();
            })
            .WithName("DisconnectDevice").WithSummary("Disconnect and clear the desktop session")
            .WithDescription("Requires the current device token. Clears all in-memory measurements and pairing state.")
            .ProducesProblem(401);
    }
}
