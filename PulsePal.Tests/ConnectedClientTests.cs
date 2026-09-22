using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PulsePal.Bridge;
using PulsePal.Connected;

namespace PulsePal.Tests;

public sealed class ConnectedClientTests
{
    [Fact]
    public async Task UnicodePhoneAndSourceNamesSurvivePairingAndSync()
    {
        await using var bridge = await LiveBridge.Start();
        const string name = "Jos\u00e9\u2019s iPhone";
        var pending = await bridge.Client.RequestPairingAsync(name, bridge.Ct);
        Assert.Equal(name, Assert.Single(bridge.Host.PendingDevices).DeviceName);
        bridge.Host.Approve(pending.RequestId);
        Assert.Equal(PairingState.Approved, await bridge.Client.PollApprovalAsync(bridge.Ct));
        var batch = Batch(bridge.Clock);
        batch = batch with
        {
            Records = batch.Records.Select(record => record with
            {
                SourceApp = "\u5065\u5eb7" + new string('a', 510),
                SourceDevice = "\u00e9" + new string('d', 255)
            }).ToArray()
        };
        var response = await bridge.Client.SyncAsync(batch, bridge.Ct);
        Assert.Equal(2, response.Accepted);
        CheckRecords(bridge.Host, batch.Records, response.ReceivedAt);
    }

    [Theory]
    [InlineData("Name\u202e")]
    [InlineData("Name\u200b")]
    [InlineData("Name\u2028")]
    public async Task InvisibleFormattingInDeviceNamesIsRejectedBeforeSending(string name)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await Fails<ArgumentException>(() => client.RequestPairingAsync(name, server.Ct));
        Assert.Equal(0, server.Hits);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("poll")]
    [InlineData("sync")]
    public async Task StalledResponseBodyTimesOutAndReleasesClientForDisconnect(string phase)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client(TimeSpan.FromSeconds(2));
        await Prepare(client, server, phase);
        server.Override = async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 4096;
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        };
        await Fails<OperationCanceledException>(() => Invoke(client, server, phase).WaitAsync(TimeSpan.FromSeconds(10)));
        server.Override = context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        };
        await client.DisconnectAsync(server.Ct).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsPaired);
    }

    [Fact]
    public async Task RealBridgeRequiresApprovalCachesOneTimeTokenAndPreservesVersionedProvenance()
    {
        await using var bridge = await LiveBridge.Start();
        var client = bridge.Client;
        var batch = Batch(bridge.Clock);
        Assert.False(client.IsPaired);
        await Fails<InvalidOperationException>(() => client.PollApprovalAsync(bridge.Ct));
        await Fails<InvalidOperationException>(() => client.SyncAsync(batch, bridge.Ct));
        var pending = await client.RequestPairingAsync("Test phone", bridge.Ct);
        Assert.True(pending.RequestId != Guid.Empty, "A pending request needs an identifier.");
        Assert.True(PairingPolicy.IsSecret(pending.PollSecret), "A pending request needs a valid poll secret.");
        AssertCount(1, bridge.Host.PendingDevices.Count);
        Assert.True(bridge.Host.PairedDeviceName is null, "Pending must not create a paired device.");
        Assert.Equal(PairingState.Pending, await client.PollApprovalAsync(bridge.Ct));
        Assert.Equal(PairingState.Pending, await client.PollApprovalAsync(bridge.Ct));
        Assert.False(client.IsPaired);
        await Fails<InvalidOperationException>(() => client.SyncAsync(batch, bridge.Ct));
        await Fails<InvalidOperationException>(() => client.RequestPairingAsync("Repeated phone", bridge.Ct));
        AssertCount(1, bridge.Host.PendingDevices.Count);

        bridge.Host.Approve(pending.RequestId);
        Assert.Equal(PairingState.Approved, await client.PollApprovalAsync(bridge.Ct));
        Assert.True(client.IsPaired);
        // BridgeService emits the token once; another network poll cannot supply it again.
        Assert.Equal(PairingState.Approved, await client.PollApprovalAsync(bridge.Ct));
        await Fails<InvalidOperationException>(() => client.RequestPairingAsync("Repeated phone", bridge.Ct));
        var first = await client.SyncAsync(batch, bridge.Ct);
        Assert.Equal(2, first.Accepted);
        Assert.Equal(2, first.Retained);
        CheckRecords(bridge.Host, batch.Records, first.ReceivedAt);
        var retry = await client.SyncAsync(batch, bridge.Ct);
        Assert.Equal(0, retry.Accepted);
        Assert.Equal(2, retry.Retained);

        bridge.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = batch.Records.Select(r => r with
        {
            Version = r.Version + 1,
            Value = r.Value + 1,
            SourceDevice = "Replacement watch"
        }).ToArray();
        var updated = await client.SyncAsync(batch with { Records = newer }, bridge.Ct);
        Assert.Equal(2, updated.Accepted);
        CheckRecords(bridge.Host, newer, updated.ReceivedAt);
        var stale = await client.SyncAsync(batch, bridge.Ct);
        Assert.Equal(0, stale.Accepted);
        CheckRecords(bridge.Host, newer, updated.ReceivedAt);
        Assert.True(bridge.Host.Categories.SequenceEqual(batch.Categories), "Selected category statuses must survive sync.");

        await client.DisconnectAsync(bridge.Ct);
        Assert.False(client.IsPaired);
        AssertCleared(bridge.Host);
        await client.DisconnectAsync(bridge.Ct);
        await Fails<InvalidOperationException>(() => client.SyncAsync(batch, bridge.Ct));
        await Fails<InvalidOperationException>(() => client.PollApprovalAsync(bridge.Ct));
    }

    [Fact]
    public async Task ApprovedPollIsCachedWithoutAnotherHttpsRequest()
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await client.RequestPairingAsync("Test phone", server.Ct);
        Assert.Equal(PairingState.Approved, await client.PollApprovalAsync(server.Ct));
        server.Override = context => Reply(context, Serialize(new PairingStatusResponse(PairingState.Approved, null)));
        Assert.Equal(PairingState.Approved, await client.PollApprovalAsync(server.Ct));
        Assert.Equal(1, server.PollHits);
        Assert.True(client.IsPaired);
    }

    [Fact]
    public async Task PairingAndPendingPollUsePostBodiesWithoutBearerCookiesOrUrlSecrets()
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        var validRequests = 0;
        server.Override = async context =>
        {
            var pairing = context.Request.Path == "/v1/pairing";
            var expectedPath = pairing ? "/v1/pairing" : "/v1/pairing/status";
            if (context.Request.Method == HttpMethods.Post &&
                context.Request.Path == expectedPath &&
                !context.Request.QueryString.HasValue &&
                context.Request.Headers.Authorization.Count == 0 &&
                context.Request.Headers.Cookie.Count == 0 &&
                context.Request.ContentType == "application/json")
                Interlocked.Increment(ref validRequests);
            await Reply(context, pairing ? server.Body("pair") :
                Serialize(new PairingStatusResponse(PairingState.Pending, null)));
        };

        await client.RequestPairingAsync("Test phone", server.Ct);
        Assert.Equal(PairingState.Pending, await client.PollApprovalAsync(server.Ct));
        Assert.False(client.IsPaired);
        Assert.Equal(2, Volatile.Read(ref validRequests));
        Assert.Equal(1, server.PairHits);
        Assert.Equal(1, server.PollHits);
        await Fails<InvalidOperationException>(() => client.SyncAsync(EmptyBatch(), server.Ct));
        Assert.Equal(2, server.Hits);
    }

    [Fact]
    public async Task ConcurrentPairingAndSyncRetriesDoNotDuplicateRequestsOrRecords()
    {
        await using var bridge = await LiveBridge.Start();
        var first = bridge.Client.RequestPairingAsync("Test phone", bridge.Ct);
        var duplicate = bridge.Client.RequestPairingAsync("Repeated phone", bridge.Ct);
        var pending = await first;
        await Fails<InvalidOperationException>(() => duplicate);
        AssertCount(1, bridge.Host.PendingDevices.Count);
        Assert.True(bridge.Host.PendingDevices[0].RequestId == pending.RequestId,
            "Concurrent pairing must keep the original pending request.");
        bridge.Host.Approve(pending.RequestId);
        Assert.Equal(PairingState.Approved, await bridge.Client.PollApprovalAsync(bridge.Ct));
        var batch = Batch(bridge.Clock);
        var results = await Task.WhenAll(
            bridge.Client.SyncAsync(batch, bridge.Ct), bridge.Client.SyncAsync(batch, bridge.Ct));
        Assert.Equal(2, results.Sum(r => r.Accepted));
        Assert.All(results, result => Assert.Equal(2, result.Retained));
        CheckRecords(bridge.Host, batch.Records, results[0].ReceivedAt);
    }

    [Theory]
    [InlineData(ReadAvailability.NoData)]
    [InlineData(ReadAvailability.Denied)]
    [InlineData(ReadAvailability.Unsupported)]
    [InlineData(ReadAvailability.PermissionNotVerifiable)]
    public async Task EmptyCategoryReadsNeverInventZeroMeasurements(ReadAvailability availability)
    {
        await using var bridge = await LiveBridge.Start();
        await bridge.Pair();
        var request = new SyncRecordsRequest([], [
            new(HealthMetric.HeartRate, availability), new(HealthMetric.BloodPressure, availability)]);
        var result = await bridge.Client.SyncAsync(request, bridge.Ct);
        Assert.Equal(0, result.Accepted);
        Assert.Equal(0, result.Retained);
        AssertCount(0, bridge.Host.Records.Count);
        Assert.True(bridge.Host.Categories.SequenceEqual(request.Categories), "Empty read statuses must be retained verbatim.");
    }

    [Fact]
    public async Task RevocationReturnsUnauthorizedAndClearsLocalToken()
    {
        await using var bridge = await LiveBridge.Start();
        await bridge.Pair();
        await bridge.Client.SyncAsync(Batch(bridge.Clock), bridge.Ct);
        bridge.Host.Revoke();
        var error = await Fails<HttpRequestException>(() => bridge.Client.SyncAsync(Batch(bridge.Clock), bridge.Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.False(bridge.Client.IsPaired);
        AssertCleared(bridge.Host);
        await Fails<InvalidOperationException>(() => bridge.Client.SyncAsync(Batch(bridge.Clock), bridge.Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectClearsLocalStateEvenWhenServerRevoked(bool paired)
    {
        await using var bridge = await LiveBridge.Start();
        if (paired) await bridge.Pair();
        else await bridge.Client.RequestPairingAsync("Test phone", bridge.Ct);
        bridge.Host.Revoke();
        if (paired)
            await Fails<HttpRequestException>(() => bridge.Client.DisconnectAsync(bridge.Ct));
        else
            await bridge.Client.DisconnectAsync(bridge.Ct);
        Assert.False(bridge.Client.IsPaired);
        await Fails<InvalidOperationException>(() => bridge.Client.PollApprovalAsync(bridge.Ct));
    }

    [Fact]
    public async Task PendingDisconnectClearsLocalRequestWithoutContactingServer()
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await client.RequestPairingAsync("Test phone", server.Ct);
        await client.DisconnectAsync(server.Ct);
        Assert.Equal(1, server.Hits);
        Assert.False(client.IsPaired);
        await Fails<InvalidOperationException>(() => client.PollApprovalAsync(server.Ct));
        await client.RequestPairingAsync("Test phone", server.Ct);
        Assert.Equal(2, server.PairHits);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("server-expired")]
    [InlineData("client-expired")]
    public async Task TerminalPollingClearsPendingState(string outcome)
    {
        await using var bridge = await LiveBridge.Start();
        var pending = await bridge.Client.RequestPairingAsync("Test phone", bridge.Ct);
        if (outcome == "rejected") bridge.Host.Reject(pending.RequestId);
        if (outcome == "server-expired") bridge.Clock.Advance(TimeSpan.FromMinutes(2));
        if (outcome == "client-expired") bridge.ClientClock.Advance(TimeSpan.FromMinutes(2));
        var expected = outcome == "rejected" ? PairingState.Rejected : PairingState.Expired;
        Assert.Equal(expected, await bridge.Client.PollApprovalAsync(bridge.Ct));
        Assert.False(bridge.Client.IsPaired);
        await Fails<InvalidOperationException>(() => bridge.Client.PollApprovalAsync(bridge.Ct));
        await Fails<InvalidOperationException>(() => bridge.Client.SyncAsync(Batch(bridge.Clock), bridge.Ct));
    }

    [Fact]
    public async Task ClientPendingExpiryIsLocalAndDoesNotPollServer()
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        var pending = await client.RequestPairingAsync("Test phone", server.Ct);
        clock.Set(pending.ExpiresAt);
        Assert.Equal(PairingState.Expired, await client.PollApprovalAsync(server.Ct));
        Assert.Equal(0, server.PollHits);
        await Fails<InvalidOperationException>(() => client.PollApprovalAsync(server.Ct));
    }

    [Fact]
    public async Task ChallengeExpiryIsCheckedAgainBeforeSendingRequest()
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        var payload = server.Payload();
        using var client = new PinnedBridgeClient(Serialize(payload), true, clock);
        clock.Set(payload.ExpiresAt);
        await Fails<InvalidOperationException>(() => client.RequestPairingAsync("Test phone", server.Ct));
        Assert.Equal(0, server.Hits);
        AssertRejectedPayload(Serialize(payload), clock, true);
    }

    [Fact]
    public async Task ServerChallengeExpiryRejectsAClientWhoseClockHasNotAdvanced()
    {
        await using var bridge = await LiveBridge.Start();
        bridge.Clock.Advance(TimeSpan.FromMinutes(2));
        var error = await Fails<HttpRequestException>(() => bridge.Client.RequestPairingAsync("Test phone", bridge.Ct));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.False(bridge.Client.IsPaired);
        AssertCount(0, bridge.Host.PendingDevices.Count);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("pending")]
    [InlineData("paired")]
    public async Task DisposedClientRejectsEveryOperation(string state)
    {
        await using var bridge = await LiveBridge.Start();
        if (state == "pending") await bridge.Client.RequestPairingAsync("Test phone", bridge.Ct);
        if (state == "paired") await bridge.Pair();
        bridge.Client.Dispose();
        bridge.Client.Dispose();
        Assert.False(bridge.Client.IsPaired);
        await Fails<ObjectDisposedException>(() => bridge.Client.RequestPairingAsync("Test phone", bridge.Ct));
        await Fails<ObjectDisposedException>(() => bridge.Client.PollApprovalAsync(bridge.Ct));
        await Fails<ObjectDisposedException>(() => bridge.Client.SyncAsync(Batch(bridge.Clock), bridge.Ct));
        await Fails<ObjectDisposedException>(() => bridge.Client.DisconnectAsync(bridge.Ct));
    }

    [Fact]
    public async Task PrecancelledOperationsDoNotReachHttpsServer()
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Fails<OperationCanceledException>(() => client.RequestPairingAsync("Test phone", cancelled.Token));
        await Fails<OperationCanceledException>(() => client.PollApprovalAsync(cancelled.Token));
        await Fails<OperationCanceledException>(() => client.SyncAsync(EmptyBatch(), cancelled.Token));
        await Fails<OperationCanceledException>(() => client.DisconnectAsync(cancelled.Token));
        Assert.Equal(0, server.Hits);
        await client.RequestPairingAsync("Test phone", server.Ct);
        Assert.Equal(1, server.Hits);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("long")]
    [InlineData("control")]
    public async Task InvalidDeviceNamesAreRejectedBeforeNetwork(string kind)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        var name = kind switch { "empty" => "", "whitespace" => " ", "long" => new string('x', 81), _ => "phone\n" };
        await Fails<ArgumentException>(() => client.RequestPairingAsync(name, server.Ct));
        Assert.Equal(0, server.Hits);
    }

    [Theory]
    [InlineData("10.0.0.0", 1, false)]
    [InlineData("10.255.255.255", 65535, true)]
    [InlineData("172.16.0.0", 443, false)]
    [InlineData("172.31.255.255", 8443, true)]
    [InlineData("192.168.0.0", 1, true)]
    [InlineData("192.168.255.255", 65535, false)]
    public void CanonicalPrivateIpv4BoundariesParseWithoutNetworking(string address, int port, bool slash)
    {
        var clock = new ManualClock();
        var payload = Payload(clock) with { Endpoint = $"https://{address}:{port}{(slash ? "/" : "")}" };
        var parsed = PairingPolicy.Parse(Serialize(payload), false, clock);
        Assert.True(parsed == payload, "Parsing must preserve the transferred identity.");
        using var client = new PinnedBridgeClient(Serialize(payload));
        Assert.False(client.IsPaired);
    }

    public static IEnumerable<object[]> InvalidEndpointCases()
    {
        foreach (var label in new[]
        {
            "public", "metadata", "loopback", "loopback-other", "loopback-range", "localhost", "dns",
            "ipv6", "mapped", "userinfo", "path", "query", "fragment", "integer", "octal", "hex", "short",
            "leading-zero", "backslash", "escaped-host", "escaped-slash", "http", "missing-port", "zero-port",
            "large-port", "negative-port", "empty-port", "leading-zero-port", "uppercase", "trailing-dot",
            "below-10", "above-10", "below-172", "above-172", "below-192", "above-192", "link-local",
            "unspecified", "multicast", "null"
        })
            yield return [label];
    }

    [Theory]
    [MemberData(nameof(InvalidEndpointCases))]
    public void UnsafeOrNoncanonicalEndpointIsRejectedWithoutNetworking(string kind)
    {
        var endpoint = kind switch
        {
            "public" => "https://8.8.8.8:443", "metadata" => "https://169.254.169.254:443",
            "loopback" => "https://127.0.0.1:443", "loopback-other" => "https://127.0.0.2:443",
            "loopback-range" => "https://127.255.255.255:443", "localhost" => "https://localhost:443",
            "dns" => "https://desktop.example:443", "ipv6" => "https://[::1]:443",
            "mapped" => "https://[::ffff:10.0.0.1]:443", "userinfo" => $"https://{Secret()}@10.0.0.1:443",
            "path" => "https://10.0.0.1:443/v1", "query" => "https://10.0.0.1:443/?x=1",
            "fragment" => "https://10.0.0.1:443/#x", "integer" => "https://167772161:443",
            "octal" => "https://012.0.0.1:443", "hex" => "https://0x0a000001:443",
            "short" => "https://10.1:443", "leading-zero" => "https://010.000.000.001:443",
            "backslash" => "https://10.0.0.1:443\\", "escaped-host" => "https://%31%30.0.0.1:443",
            "escaped-slash" => "https://10.0.0.1:443/%2f", "http" => "http://10.0.0.1:443",
            "missing-port" => "https://10.0.0.1", "zero-port" => "https://10.0.0.1:0",
            "large-port" => "https://10.0.0.1:65536", "negative-port" => "https://10.0.0.1:-1",
            "empty-port" => "https://10.0.0.1:", "leading-zero-port" => "https://10.0.0.1:0443",
            "uppercase" => "HTTPS://10.0.0.1:443", "trailing-dot" => "https://10.0.0.1.:443",
            "below-10" => "https://9.255.255.255:443", "above-10" => "https://11.0.0.0:443",
            "below-172" => "https://172.15.255.255:443", "above-172" => "https://172.32.0.0:443",
            "below-192" => "https://192.167.255.255:443", "above-192" => "https://192.169.0.0:443",
            "link-local" => "https://169.254.1.1:443", "unspecified" => "https://0.0.0.0:443",
            "multicast" => "https://224.0.0.1:443", "null" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var clock = new ManualClock();
        var json = Serialize(Payload(clock) with { Endpoint = endpoint! });
        AssertRejectedPayload(json, clock);
        Assert.True(Record.Exception(() => new PinnedBridgeClient(json)) is ArgumentException,
            "The public client must reject unsafe transferred endpoints.");
        if (kind != "loopback") AssertRejectedPayload(json, clock, true);
    }

    [Fact]
    public void LoopbackRequiresExplicitTestSeamAndPinAllowsLowercaseHex()
    {
        var clock = new ManualClock();
        var payload = Payload(clock) with { Endpoint = "https://127.0.0.1:443" };
        var json = Serialize(payload);
        AssertRejectedPayload(json, clock);
        Assert.True(PairingPolicy.Parse(json, true, clock) == payload, "Only the explicit loopback seam may accept this identity.");
        Assert.True(Record.Exception(() => new PinnedBridgeClient(json)) is ArgumentException,
            "The public constructor must not enable loopback.");
        using var client = new PinnedBridgeClient(json, true, clock);
        var lower = Payload(clock) with { CertificateSha256 = payload.CertificateSha256.ToLowerInvariant() };
        Assert.True(PairingPolicy.Parse(Serialize(lower), false, clock) == lower, "Hex pins are case insensitive.");
    }

    public static IEnumerable<object[]> InvalidPayloadCases()
    {
        foreach (var label in new[]
        {
            "challenge-null", "challenge-short", "challenge-long", "challenge-symbol", "challenge-unicode",
            "pin-null", "pin-short", "pin-long", "pin-nonhex", "protocol-zero", "protocol-future",
            "missing-protocol", "missing-endpoint", "missing-pin", "missing-challenge", "missing-expiry",
            "expired", "expires-now", "too-far", "unknown", "duplicate", "wrongcase", "malformed",
            "null-root", "array-root", "oversized", "empty", "whitespace", "null-input", "wrong-type"
        })
            yield return [label];
    }

    [Theory]
    [MemberData(nameof(InvalidPayloadCases))]
    public void StrictPairingPayloadRejectsMalformedAndExpiredMaterial(string kind)
    {
        var clock = new ManualClock();
        var node = JsonSerializer.SerializeToNode(Payload(clock), ConnectedJson.CreateOptions())!.AsObject();
        switch (kind)
        {
            case "challenge-null": node["challenge"] = null; break;
            case "challenge-short": node["challenge"] = new string('a', 42); break;
            case "challenge-long": node["challenge"] = new string('a', 44); break;
            case "challenge-symbol": node["challenge"] = new string('a', 42) + "="; break;
            case "challenge-unicode": node["challenge"] = new string('a', 42) + "é"; break;
            case "pin-null": node["certificateSha256"] = null; break;
            case "pin-short": node["certificateSha256"] = new string('a', 63); break;
            case "pin-long": node["certificateSha256"] = new string('a', 65); break;
            case "pin-nonhex": node["certificateSha256"] = new string('g', 64); break;
            case "protocol-zero": node["protocol"] = 0; break;
            case "protocol-future": node["protocol"] = 2; break;
            case "missing-protocol": node.Remove("protocol"); break;
            case "missing-endpoint": node.Remove("endpoint"); break;
            case "missing-pin": node.Remove("certificateSha256"); break;
            case "missing-challenge": node.Remove("challenge"); break;
            case "missing-expiry": node.Remove("expiresAt"); break;
            case "expired": node["expiresAt"] = JsonValue.Create(clock.GetUtcNow().AddTicks(-1)); break;
            case "expires-now": node["expiresAt"] = JsonValue.Create(clock.GetUtcNow()); break;
            case "too-far": node["expiresAt"] = JsonValue.Create(clock.GetUtcNow().AddMinutes(5).AddTicks(1)); break;
            case "unknown": node["unexpected"] = 1; break;
            case "wrongcase": node["Protocol"] = 1; node.Remove("protocol"); break;
            case "wrong-type": node["protocol"] = "1"; break;
        }
        var json = kind switch
        {
            "duplicate" => node.ToJsonString().Insert(1, "\"protocol\":1,"),
            "malformed" => "{", "null-root" => "null", "array-root" => "[]",
            "oversized" => node.ToJsonString() + new string(' ', 8193),
            "empty" => "", "whitespace" => " ", "null-input" => null, _ => node.ToJsonString()
        };
        AssertRejectedPayload(json!, clock);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3000000000L)]
    public void ChallengeExpiryAcceptsStrictlyFutureThroughExactlyFiveMinutes(long ticks)
    {
        var clock = new ManualClock();
        var payload = Payload(clock) with { ExpiresAt = clock.GetUtcNow().AddTicks(ticks) };
        Assert.True(PairingPolicy.Parse(Serialize(payload), false, clock) == payload, "Valid expiry boundary must parse.");
    }

    [Fact]
    public void DirectCertificateValidationEnforcesTimePinHostAndPolicyFlags()
    {
        var now = WholeSecond(DateTimeOffset.UtcNow);
        using var certificate = Certificate(now, now.AddHours(9), IPAddress.Loopback);
        var pin = SHA256.HashData(certificate.RawData);
        bool Valid(DateTimeOffset time, SslPolicyErrors errors = SslPolicyErrors.None) =>
            PairingPolicy.ValidateCertificate(certificate, errors, "127.0.0.1", pin, time);
        Assert.True(Valid(now), "NotBefore is inclusive and exactly nine hours is permitted.");
        Assert.True(Valid(now.AddHours(9).AddTicks(-1)), "A certificate remains valid strictly before NotAfter.");
        Assert.False(Valid(now.AddTicks(-1)));
        Assert.False(Valid(now.AddHours(9)));
        Assert.True(Valid(now, SslPolicyErrors.RemoteCertificateChainErrors), "A matching ephemeral pin tolerates chain errors alone.");
        Assert.False(Valid(now, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(Valid(now, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(Valid(now, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(Valid(now, SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(PairingPolicy.ValidateCertificate(null, SslPolicyErrors.None, "127.0.0.1", pin, now));
        Assert.False(PairingPolicy.ValidateCertificate(certificate, SslPolicyErrors.None, "127.0.0.2", pin, now));
        var wrong = pin.ToArray();
        wrong[0] ^= 1;
        Assert.False(PairingPolicy.ValidateCertificate(certificate, SslPolicyErrors.None, "127.0.0.1", wrong, now));
        Assert.False(PairingPolicy.ValidateCertificate(certificate, SslPolicyErrors.None, "127.0.0.1", [], now));
        using var tooLong = Certificate(now, now.AddHours(9).AddSeconds(1), IPAddress.Loopback);
        Assert.False(PairingPolicy.ValidateCertificate(tooLong, SslPolicyErrors.None, "127.0.0.1",
            SHA256.HashData(tooLong.RawData), now));
    }

    [Theory]
    [InlineData("wrong-pin")]
    [InlineData("wrong-san")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("long-lifetime")]
    public async Task RealTlsRejectsInvalidPinnedCertificateBeforeAnyHttpRequest(string kind)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock, kind);
        if (kind == "expired") clock.Set(new DateTimeOffset(server.Certificate.NotAfter.ToUniversalTime()));
        if (kind == "not-yet-valid") clock.Set(new DateTimeOffset(server.Certificate.NotBefore.ToUniversalTime()).AddSeconds(-1));
        var payload = server.Payload();
        if (kind == "wrong-pin")
        {
            var hash = Convert.FromHexString(payload.CertificateSha256);
            hash[0] ^= 1;
            payload = payload with { CertificateSha256 = Convert.ToHexString(hash) };
        }
        using var client = new PinnedBridgeClient(Serialize(payload), true, clock);
        await Fails<HttpRequestException>(() => client.RequestPairingAsync("Test phone", server.Ct));
        Assert.Equal(0, server.Hits);
        Assert.False(client.IsPaired);
    }

    public static IEnumerable<object[]> RedirectCases()
    {
        foreach (var phase in new[] { "pair", "poll", "sync" })
        foreach (var status in new[] { 301, 302, 303, 307, 308 })
            yield return [phase, status];
    }

    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task RedirectsNeverForwardRequestsEvenToAnotherServerWithTheSamePin(string phase, int status)
    {
        var clock = new ManualClock();
        await using var target = await HttpsServer.Start(clock);
        // The destination presents the SAME certificate: pin rejection cannot mask redirect following.
        await using var source = await HttpsServer.Start(clock, sharedCertificate: target.Certificate);
        using var client = source.Client();
        await Prepare(client, source, phase);
        var marker = Secret();
        source.Override = async context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = target.Endpoint + "/redirected?marker=" + marker;
            await Reply(context, marker);
        };
        var before = source.Hits;
        var error = await Fails<HttpRequestException>(() => Invoke(client, source, phase));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(before + 1, source.Hits);
        Assert.Equal(0, target.Hits);
        Assert.True(!error.ToString().Contains(marker, StringComparison.Ordinal), "Redirect exceptions must not expose response secrets.");
        Assert.True(!error.ToString().Contains(target.Endpoint, StringComparison.Ordinal), "Redirect exceptions must not expose destination URLs.");
    }

    public static IEnumerable<object[]> HostileResponseCases()
    {
        foreach (var phase in new[] { "pair", "poll", "sync" })
        foreach (var kind in new[] { "unknown", "duplicate", "wrongcase", "malformed", "null-root" })
            yield return [phase, kind];
        foreach (var kind in new[]
        {
            "empty-id", "invalid-id", "null-secret", "short-secret", "long-secret", "invalid-secret",
            "expired", "too-far", "missing-id", "missing-secret", "missing-expiry"
        })
            yield return ["pair", kind];
        foreach (var kind in new[]
        {
            "integer-enum", "unknown-enum", "null-token", "short-token", "long-token", "invalid-token", "pending-token"
        })
            yield return ["poll", kind];
    }

    [Theory]
    [MemberData(nameof(HostileResponseCases))]
    public async Task HostileHttpsResponseSchemasAndInvalidSecretsAreRejected(string phase, string kind)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await Prepare(client, server, phase);
        var node = JsonNode.Parse(server.Body(phase))!.AsObject();
        var field = phase switch { "pair" => "requestId", "poll" => "state", _ => "accepted" };
        switch (kind)
        {
            case "unknown": node["unexpected"] = true; break;
            case "wrongcase":
                var value = node[field]!.DeepClone();
                node.Remove(field);
                node[char.ToUpperInvariant(field[0]) + field[1..]] = value;
                break;
            case "empty-id": node["requestId"] = Guid.Empty; break;
            case "invalid-id": node["requestId"] = 1; break;
            case "null-secret": node["pollSecret"] = null; break;
            case "short-secret": node["pollSecret"] = new string('a', 42); break;
            case "long-secret": node["pollSecret"] = new string('a', 44); break;
            case "invalid-secret": node["pollSecret"] = new string('a', 42) + "!"; break;
            case "expired": node["expiresAt"] = JsonValue.Create(clock.GetUtcNow()); break;
            case "too-far": node["expiresAt"] = JsonValue.Create(clock.GetUtcNow().AddMinutes(5).AddTicks(1)); break;
            case "missing-id": node.Remove("requestId"); break;
            case "missing-secret": node.Remove("pollSecret"); break;
            case "missing-expiry": node.Remove("expiresAt"); break;
            case "integer-enum": node["state"] = 1; break;
            case "unknown-enum": node["state"] = "Unknown"; break;
            case "null-token": node["deviceToken"] = null; break;
            case "short-token": node["deviceToken"] = new string('a', 42); break;
            case "long-token": node["deviceToken"] = new string('a', 44); break;
            case "invalid-token": node["deviceToken"] = new string('a', 42) + "!"; break;
            case "pending-token": node["state"] = "Pending"; break;
        }
        var body = kind switch
        {
            "duplicate" => node.ToJsonString().Insert(1, $"\"{field}\":{node[field]!.ToJsonString()},"),
            "malformed" => "{", "null-root" => "null", _ => node.ToJsonString()
        };
        server.Override = context => Reply(context, body);
        await RejectResponse(() => Invoke(client, server, phase), "Malformed response must not be accepted.");
        Assert.Equal(phase == "sync", client.IsPaired);
        if (phase == "pair")
        {
            server.Override = null;
            await client.RequestPairingAsync("Retry phone", server.Ct);
            Assert.Equal(2, server.PairHits);
        }
    }

    [Theory]
    [InlineData("poll", "state")]
    [InlineData("sync", "accepted")]
    [InlineData("sync", "retained")]
    [InlineData("sync", "receivedAt")]
    [InlineData("sync", "all")]
    public async Task Regression_MissingRequiredResponseFieldsMustNotBecomeSuccessfulDefaults(string phase, string field)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await Prepare(client, server, phase);
        var node = JsonNode.Parse(server.Body(phase))!.AsObject();
        if (phase == "poll") node["deviceToken"] = null;
        if (field == "all") node.Clear();
        else node.Remove(field);
        server.Override = context => Reply(context, node.ToJsonString());
        await RejectResponse(() => Invoke(client, server, phase),
            $"Missing required {phase} field ({field}) must be rejected, not silently defaulted.");
    }

    [Theory]
    [InlineData("pair", 200)]
    [InlineData("pair", 202)]
    [InlineData("poll", 201)]
    [InlineData("sync", 202)]
    public async Task UnexpectedSuccessStatusDoesNotSatisfyResponseContract(string phase, int status)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await Prepare(client, server, phase);
        server.Override = context =>
        {
            context.Response.StatusCode = status;
            return Reply(context, server.Body(phase));
        };
        await Fails<InvalidOperationException>(() => Invoke(client, server, phase));
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("poll")]
    [InlineData("sync")]
    public async Task Regression_NoContentMustRejectMissingResponseRatherThanSucceedOrDereferenceNull(string phase)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await Prepare(client, server, phase);
        server.Override = context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        };
        await RejectResponse(() => Invoke(client, server, phase),
            $"HTTP 204 cannot satisfy the required {phase} response contract and must produce a controlled rejection.");
    }

    [Theory]
    [InlineData("pair", false)]
    [InlineData("pair", true)]
    [InlineData("poll", false)]
    [InlineData("poll", true)]
    [InlineData("sync", false)]
    [InlineData("sync", true)]
    public async Task OversizedHttpsResponsesAreBoundedWithOrWithoutContentLength(string phase, bool streamed)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        await Prepare(client, server, phase);
        server.Override = context => Reply(context, new string(' ', 8193), streamed);
        await Fails<InvalidOperationException>(() => Invoke(client, server, phase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exactly8192ResponseBytesAreAccepted(bool streamed)
    {
        var clock = new ManualClock();
        await using var server = await HttpsServer.Start(clock);
        using var client = server.Client();
        var body = server.Body("pair");
        body += new string(' ', 8192 - Encoding.UTF8.GetByteCount(body));
        server.Override = context => Reply(context, body, streamed);
        var pending = await client.RequestPairingAsync("Test phone", server.Ct);
        Assert.True(pending.RequestId != Guid.Empty, "A valid bounded response must be accepted.");
        Assert.False(client.IsPaired);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ConnectedJson.CreateOptions());
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static PairingPayload Payload(ManualClock clock) =>
        new(1, "https://10.0.0.1:8443", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Secret(), clock.GetUtcNow().AddMinutes(2));
    private static void AssertRejectedPayload(string json, ManualClock clock, bool allowLoopback = false) =>
        Assert.True(Record.Exception(() => PairingPolicy.Parse(json, allowLoopback, clock)) is ArgumentException,
            "Unsafe or malformed pairing material must be rejected without network access.");

    private static async Task<T> Fails<T>(Func<Task> operation) where T : Exception
    {
        var error = await Record.ExceptionAsync(operation);
        Assert.True(error is T, $"Expected {typeof(T).Name}; actual exception type: {error?.GetType().Name ?? "none"}.");
        return (T)error!;
    }

    private static async Task RejectResponse(Func<Task> operation, string reason)
    {
        var error = await Record.ExceptionAsync(operation);
        Assert.True(error is JsonException or InvalidOperationException,
            $"{reason} Actual exception type: {error?.GetType().Name ?? "none"}.");
    }

    // Collection assertions must never render secret-bearing DTOs on failure.
    private static void AssertCount(int expected, int actual) => Assert.Equal(expected, actual);

    private static SyncRecordsRequest EmptyBatch() =>
        new([], [new(HealthMetric.HeartRate, ReadAvailability.NoData), new(HealthMetric.BloodPressure, ReadAvailability.Denied)]);

    private static SyncRecordsRequest Batch(ManualClock clock) => new([
        new("source-record-hr", 2, HealthMetric.HeartRate, clock.GetUtcNow().AddMinutes(-3),
            clock.GetUtcNow().AddMinutes(-2), 72, null, "Source health app", "Watch"),
        new("source-record-bp", 3, HealthMetric.BloodPressure, clock.GetUtcNow().AddMinutes(-1),
            null, 120, 80, "Source health app", null)
    ], [new(HealthMetric.HeartRate, ReadAvailability.Available), new(HealthMetric.BloodPressure, ReadAvailability.Available)]);

    private static void CheckRecords(BridgeHost host, HealthRecord[] expected, DateTimeOffset received)
    {
        var stored = host.Records;
        Assert.Equal(expected.Length, stored.Count);
        foreach (var source in expected)
        {
            var matches = stored.Where(r => r.Record.RecordId == source.RecordId && r.Record.SourceApp == source.SourceApp &&
                r.Record.Metric == source.Metric).ToArray();
            AssertCount(1, matches.Length);
            // Record equality covers every source field without rendering health or provenance values on failure.
            Assert.True(matches[0].Record == source, "All source fields, versions and provenance must remain unchanged.");
            Assert.True(matches[0].ReceivedAt == received, "Desktop receipt time must match the sync response.");
            Assert.True(matches[0].ReceivedAt != source.MeasuredAt, "Receipt time must be distinct from measurement time.");
        }
    }

    private static void AssertCleared(BridgeHost host)
    {
        AssertCount(0, host.Records.Count);
        AssertCount(0, host.Categories.Count);
        AssertCount(0, host.PendingDevices.Count);
        Assert.True(host.PairedDeviceName is null, "Disconnect must clear the device identity.");
        Assert.True(host.PairingString is null, "Disconnect must clear pairing material.");
    }

    private static async Task Prepare(PinnedBridgeClient client, HttpsServer server, string phase)
    {
        if (phase == "pair") return;
        await client.RequestPairingAsync("Test phone", server.Ct);
        if (phase == "sync") Assert.Equal(PairingState.Approved, await client.PollApprovalAsync(server.Ct));
    }

    private static Task Invoke(PinnedBridgeClient client, HttpsServer server, string phase) => phase switch
    {
        "pair" => client.RequestPairingAsync("Test phone", server.Ct),
        "poll" => client.PollApprovalAsync(server.Ct),
        "sync" => client.SyncAsync(EmptyBatch(), server.Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private static async Task Reply(HttpContext context, string body, bool streamed = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        if (!streamed) context.Response.ContentLength = bytes.Length;
        else await context.Response.StartAsync(context.RequestAborted);
        for (var offset = 0; offset < bytes.Length; offset += 1024)
        {
            await context.Response.Body.WriteAsync(bytes.AsMemory(offset, Math.Min(1024, bytes.Length - offset)),
                context.RequestAborted);
            if (streamed) await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static DateTimeOffset WholeSecond(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static X509Certificate2 Certificate(DateTimeOffset before, DateTimeOffset after, IPAddress address)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PulsePal client tests", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(before, after);
        return LoadPrivateCertificate(generated);
    }

    private static X509Certificate2 LoadPrivateCertificate(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            // Schannel needs a temporary key container, not an EphemeralKeySet private key.
            return X509CertificateLoader.LoadPkcs12(pfx, null, OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public ManualClock(DateTimeOffset? start = null) => ticks = (start ?? DateTimeOffset.UtcNow).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class LiveBridge : IAsyncDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        public ManualClock Clock { get; } = new();
        public ManualClock ClientClock { get; }
        public BridgeHost Host { get; }
        public PinnedBridgeClient Client { get; private set; } = null!;
        public CancellationToken Ct => timeout.Token;

        private LiveBridge()
        {
            ClientClock = new(Clock.GetUtcNow());
            Host = new BridgeHost(Clock);
        }

        public static async Task<LiveBridge> Start()
        {
            var bridge = new LiveBridge();
            try
            {
                await bridge.Host.StartAsync(IPAddress.Loopback, 0, bridge.Ct);
                Assert.True(bridge.Host.PairingString is not null, "A running bridge must publish pairing material.");
                bridge.Client = new PinnedBridgeClient(bridge.Host.PairingString!, true, bridge.ClientClock);
                return bridge;
            }
            catch { await bridge.DisposeAsync(); throw; }
        }

        public async Task Pair()
        {
            var pending = await Client.RequestPairingAsync("Test phone", Ct);
            Host.Approve(pending.RequestId);
            Assert.Equal(PairingState.Approved, await Client.PollApprovalAsync(Ct));
            Assert.True(Client.IsPaired);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Host.StopAsync(stop.Token); }
            finally { await Host.DisposeAsync(); timeout.Dispose(); }
        }
    }

    private sealed class HttpsServer : IAsyncDisposable
    {
        private readonly ManualClock clock;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        private readonly string pollSecret = Secret();
        private readonly string deviceToken = Secret();
        private readonly Guid requestId = Guid.NewGuid();
        private WebApplication? app;
        private bool ownsCertificate;
        private int hits, pairHits, pollHits;
        public X509Certificate2 Certificate { get; private set; } = null!;
        public string Endpoint { get; private set; } = "";
        public CancellationToken Ct => timeout.Token;
        public int Hits => Volatile.Read(ref hits);
        public int PairHits => Volatile.Read(ref pairHits);
        public int PollHits => Volatile.Read(ref pollHits);
        public Func<HttpContext, Task>? Override { get; set; }

        private HttpsServer(ManualClock clock) => this.clock = clock;

        public static async Task<HttpsServer> Start(ManualClock clock, string certificateKind = "valid",
            X509Certificate2? sharedCertificate = null)
        {
            var server = new HttpsServer(clock);
            try
            {
                var now = WholeSecond(clock.GetUtcNow());
                server.ownsCertificate = sharedCertificate is null;
                server.Certificate = sharedCertificate is null
                    ? ConnectedClientTests.Certificate(now.AddMinutes(-1),
                        certificateKind == "long-lifetime" ? now.AddHours(10) : now.AddHours(8),
                        certificateKind == "wrong-san" ? IPAddress.Parse("127.0.0.2") : IPAddress.Loopback)
                    : sharedCertificate;
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    Args = [], ApplicationName = typeof(ConnectedClientTests).Assembly.GetName().Name
                });
                builder.Configuration.Sources.Clear();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.AddServerHeader = false;
                    options.Limits.MaxRequestBodySize = BridgeHost.MaximumRequestBytes;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                    options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
                    {
                        https.ServerCertificate = server.Certificate;
                        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    }));
                });
                server.app = builder.Build();
                server.app.Run(async context =>
                {
                    Interlocked.Increment(ref server.hits);
                    var phase = context.Request.Path.Value switch
                    {
                        "/v1/pairing" => "pair", "/v1/pairing/status" => "poll", _ => "sync"
                    };
                    if (phase == "pair") Interlocked.Increment(ref server.pairHits);
                    if (phase == "poll") Interlocked.Increment(ref server.pollHits);
                    context.Response.StatusCode = phase == "pair"
                        ? StatusCodes.Status201Created : StatusCodes.Status200OK;
                    if (server.Override is { } handler) await handler(context);
                    else await Reply(context, server.Body(phase));
                });
                await server.app.StartAsync(server.Ct);
                server.Endpoint = server.app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }

        public PairingPayload Payload() => ConnectedClientTests.Payload(clock) with
        {
            Endpoint = Endpoint, CertificateSha256 = Convert.ToHexString(SHA256.HashData(Certificate.RawData))
        };
        public PinnedBridgeClient Client(TimeSpan? operationTimeout = null) =>
            new(Serialize(Payload()), true, clock, operationTimeout);
        public string Body(string phase) => phase switch
        {
            "pair" => Serialize(new PendingPairingResponse(requestId, pollSecret, clock.GetUtcNow().AddMinutes(2))),
            "poll" => Serialize(new PairingStatusResponse(PairingState.Approved, deviceToken)),
            _ => Serialize(new SyncRecordsResponse(0, 0, clock.GetUtcNow()))
        };

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (app is not null)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await app.StopAsync(stop.Token); }
                    finally { await app.DisposeAsync(); }
                }
            }
            finally
            {
                if (ownsCertificate) Certificate?.Dispose();
                timeout.Dispose();
            }
        }
    }
}
