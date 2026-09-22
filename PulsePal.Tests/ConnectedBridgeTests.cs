using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PulsePal.Bridge;
using PulsePal.Connected;

namespace PulsePal.Tests;

public sealed class ConnectedBridgeTests
{
    private static readonly JsonSerializerOptions Json = ConnectedJson.CreateOptions();
    private const int MaximumBodyBytes = 256 * 1024;

    [Fact]
    public async Task PairingRequiresDesktopApprovalAndDeliversTokenOnlyOnce()
    {
        await using var bridge = await RunningBridge.Start();
        Assert.True(bridge.Host.IsRunning);
        Assert.Equal(1, bridge.Pairing.Protocol);
        Assert.Matches("^[0-9A-F]{64}$", bridge.Pairing.CertificateSha256);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", bridge.Pairing.Challenge);
        Assert.Equal("https", bridge.Client.BaseAddress!.Scheme);
        Assert.True(bridge.Client.BaseAddress.Port > 0);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(bridge.Client.BaseAddress.Host)));
        Assert.Equal(bridge.Clock.GetUtcNow().AddMinutes(2), bridge.Pairing.ExpiresAt);

        var pending = await bridge.RequestPairing();
        Assert.Null(bridge.Host.PairingString);
        Assert.Null(bridge.Host.PairedDeviceName);
        var device = Assert.Single(bridge.Host.PendingDevices);
        Assert.Equal(pending.RequestId, device.RequestId);
        Assert.Equal("Test phone", device.DeviceName);
        Assert.Equal(pending.ExpiresAt, device.ExpiresAt);
        var waiting = await bridge.Poll(pending);
        Assert.Equal(PairingState.Pending, waiting.State);
        Assert.Null(waiting.DeviceToken);

        bridge.Host.Approve(pending.RequestId);
        Assert.Empty(bridge.Host.PendingDevices);
        Assert.Equal("Test phone", bridge.Host.PairedDeviceName);
        var approved = await bridge.Poll(pending);
        Assert.Equal(PairingState.Approved, approved.State);
        Assert.False(string.IsNullOrWhiteSpace(approved.DeviceToken));
        var again = await bridge.Poll(pending);
        Assert.Equal(PairingState.Approved, again.State);
        Assert.Null(again.DeviceToken);
        using var response = await bridge.SendBatch(bridge.Batch(), approved.DeviceToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChallengeIsSingleUseAndCannotCreateSecondPendingDevice()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        using var replay = await bridge.Post("/v1/pairing",
            new CreatePairingRequest(bridge.Pairing.Challenge, "Second phone"));
        Assert.False(replay.IsSuccessStatusCode);
        Assert.Equal(pending.RequestId, Assert.Single(bridge.Host.PendingDevices).RequestId);
        Assert.Null(bridge.Host.PairingString);
    }

    [Fact]
    public async Task ConcurrentPairingReplayCreatesExactlyOnePendingRequest()
    {
        await using var bridge = await RunningBridge.Start();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => bridge.Post("/v1/pairing",
            new CreatePairingRequest(bridge.Pairing.Challenge, $"Phone {i}"))));
        try
        {
            var response = Assert.Single(responses, r => r.IsSuccessStatusCode);
            var pending = await Read<PendingPairingResponse>(response);
            Assert.Equal(pending.RequestId, Assert.Single(bridge.Host.PendingDevices).RequestId);
            Assert.Null(bridge.Host.PairingString);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentApprovedPollsDeliverExactlyOneToken()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Approve(pending.RequestId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => bridge.Poll(pending)));
        Assert.All(responses, response => Assert.Equal(PairingState.Approved, response.State));
        var token = Assert.Single(responses, response => response.DeviceToken is not null).DeviceToken;
        using var accepted = await bridge.SendBatch(bridge.Batch(), token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task RefreshInvalidatesOldChallengeAndPendingRequest()
    {
        await using var bridge = await RunningBridge.Start();
        var old = bridge.Pairing;
        var pending = await bridge.RequestPairing();
        bridge.Host.RefreshPairing();
        var fresh = bridge.ReadPairing();
        Assert.NotEqual(old.Challenge, fresh.Challenge);
        Assert.Equal(old.Endpoint, fresh.Endpoint);
        Assert.Empty(bridge.Host.PendingDevices);
        AssertExpired(await bridge.Poll(pending));
        using var stale = await bridge.Post("/v1/pairing", new CreatePairingRequest(old.Challenge, "Old phone"));
        Assert.False(stale.IsSuccessStatusCode);
        var replacement = await bridge.RequestPairing(fresh);
        Assert.NotEqual(pending.RequestId, replacement.RequestId);
    }

    [Fact]
    public async Task RefreshWhilePairedThrowsWithoutRevokingSession()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        Assert.Throws<InvalidOperationException>(() => bridge.Host.RefreshPairing());
        using var response = await bridge.SendBatch(bridge.Batch(), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Test phone", bridge.Host.PairedDeviceName);
    }

    [Fact]
    public async Task WrongPollSecretAndUnknownRequestDoNotRevealOrConsumeApproval()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Approve(pending.RequestId);
        AssertExpired(await bridge.Poll(pending with { PollSecret = "incorrect-secret" }));
        AssertExpired(await bridge.Poll(pending with { RequestId = Guid.NewGuid() }));
        var approved = await bridge.Poll(pending);
        Assert.Equal(PairingState.Approved, approved.State);
        Assert.False(string.IsNullOrWhiteSpace(approved.DeviceToken));
    }

    [Fact]
    public async Task RejectionLeavesSecretProtectedTombstoneUntilExpiry()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Reject(pending.RequestId);
        Assert.Empty(bridge.Host.PendingDevices);
        Assert.Null(bridge.Host.PairingString);
        AssertExpired(await bridge.Poll(pending with { PollSecret = "wrong" }));
        var rejected = await bridge.Poll(pending);
        Assert.Equal(PairingState.Rejected, rejected.State);
        Assert.Null(rejected.DeviceToken);
        bridge.Clock.Advance(TimeSpan.FromMinutes(2));
        AssertExpired(await bridge.Poll(pending));
    }

    [Fact]
    public async Task UnusedChallengeExpiresAtTwoMinutesAndRequiresExplicitRefresh()
    {
        await using var bridge = await RunningBridge.Start();
        bridge.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(bridge.Host.PairingString);
        using var expired = await bridge.Post("/v1/pairing",
            new CreatePairingRequest(bridge.Pairing.Challenge, "Too late"));
        Assert.False(expired.IsSuccessStatusCode);
        Assert.Empty(bridge.Host.PendingDevices);
        bridge.Host.RefreshPairing();
        var fresh = bridge.ReadPairing();
        Assert.Equal(bridge.Clock.GetUtcNow().AddMinutes(2), fresh.ExpiresAt);
        await bridge.RequestPairing(fresh);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingExpiryIsSweptByGetterOrRequest(bool readGetterFirst)
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Clock.Advance(TimeSpan.FromMinutes(2));
        if (readGetterFirst)
            Assert.Empty(bridge.Host.PendingDevices);
        AssertExpired(await bridge.Poll(pending));
        Assert.Empty(bridge.Host.PendingDevices);
        Assert.Null(bridge.Host.PairedDeviceName);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("invalid-token", false)]
    [InlineData("invalid-token", true)]
    public async Task AuthenticationPrecedesJsonParsingAndBodySizeChecks(string? token, bool oversized)
    {
        await using var bridge = await RunningBridge.Start();
        await bridge.Pair();
        var body = oversized ? new string('x', MaximumBodyBytes + 1) : "{malformed";
        using var response = await bridge.Raw("/v1/records", body, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(bridge.Host.Records);
        Assert.Empty(bridge.Host.Categories);
    }

    [Fact]
    public async Task PollSecretAndPairingChallengeAreNotBearerTokens()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Approve(pending.RequestId);
        foreach (var token in new[] { pending.PollSecret, bridge.Pairing.Challenge })
        {
            using var response = await bridge.SendBatch(bridge.Batch(), token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(bridge.Host.Records);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("integer-enum")]
    [InlineData("unknown-enum")]
    [InlineData("integer-category")]
    [InlineData("unknown-category")]
    [InlineData("integer-availability")]
    [InlineData("unknown-availability")]
    [InlineData("number-string")]
    [InlineData("nested-unknown")]
    [InlineData("nested-duplicate")]
    [InlineData("null-records")]
    [InlineData("null-categories")]
    [InlineData("null-record")]
    [InlineData("null-category")]
    [InlineData("missing-records")]
    [InlineData("missing-categories")]
    [InlineData("case-mismatch")]
    [InlineData("malformed")]
    [InlineData("null-root")]
    [InlineData("nonfinite-value")]
    public async Task IngestionRejectsStrictJsonViolationsAtomically(string violation)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var json = JsonSerializer.Serialize(bridge.Batch(), Json);
        var node = JsonNode.Parse(json)!.AsObject();
        switch (violation)
        {
            case "unknown": node["extra"] = true; break;
            case "integer-enum": node["records"]![0]!["metric"] = 0; break;
            case "unknown-enum": node["records"]![0]!["metric"] = "Other"; break;
            case "integer-category": node["categories"]![0]!["metric"] = 0; break;
            case "unknown-category": node["categories"]![0]!["metric"] = "Other"; break;
            case "integer-availability": node["categories"]![0]!["availability"] = 0; break;
            case "unknown-availability": node["categories"]![0]!["availability"] = "Other"; break;
            case "number-string": node["records"]![0]!["value"] = "72"; break;
            case "nested-unknown": node["records"]![0]!["extra"] = true; break;
            case "null-records": node["records"] = null; break;
            case "null-categories": node["categories"] = null; break;
            case "null-record": node["records"]![0] = null; break;
            case "null-category": node["categories"]![0] = null; break;
            case "missing-records": node.Remove("records"); break;
            case "missing-categories": node.Remove("categories"); break;
            case "case-mismatch": node["Records"] = node["records"]!.DeepClone(); node.Remove("records"); break;
        }
        var body = violation switch
        {
            "duplicate" => json.Insert(1, "\"records\":[],"),
            "nested-duplicate" => json.Replace("\"value\":", "\"value\":1,\"value\":", StringComparison.Ordinal),
            "malformed" => "{",
            "null-root" => "null",
            "nonfinite-value" => json.Replace("\"value\":72", "\"value\":1e400", StringComparison.Ordinal),
            _ => node.ToJsonString()
        };
        using var response = await bridge.Raw("/v1/records", body, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(bridge.Host.Records);
        Assert.Empty(bridge.Host.Categories);
    }

    [Theory]
    [InlineData("/v1/pairing", "{\"challenge\":\"x\",\"deviceName\":\"Phone\",\"extra\":1}")]
    [InlineData("/v1/pairing", "{\"challenge\":\"x\",\"challenge\":\"y\",\"deviceName\":\"Phone\"}")]
    [InlineData("/v1/pairing/status", "{\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"pollSecret\":\"x\",\"extra\":1}")]
    [InlineData("/v1/pairing/status", "{\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"pollSecret\":\"x\",\"pollSecret\":\"y\"}")]
    public async Task PairingEndpointsAlsoRejectUnknownAndDuplicateJson(string path, string body)
    {
        await using var bridge = await RunningBridge.Start();
        using var response = await bridge.Raw(path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(bridge.Host.PendingDevices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedOversizedBodyReturns413ForKnownAndUnknownLength(bool unknownLength)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var bytes = Encoding.UTF8.GetBytes(new string(' ', MaximumBodyBytes + 1) +
            JsonSerializer.Serialize(bridge.Batch(), Json));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/records");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await bridge.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(bridge.Host.Records);
    }

    [Theory]
    [InlineData("/v1/pairing")]
    [InlineData("/v1/pairing/status")]
    public async Task PairingEndpointsEnforceBodyLimit(string path)
    {
        await using var bridge = await RunningBridge.Start();
        using var response = await bridge.Raw(path, new string(' ', MaximumBodyBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(bridge.Host.PendingDevices);
        Assert.NotNull(bridge.Host.PairingString);
    }

    public static TheoryData<string> InvalidRecordCases => new()
    {
        "zero", "negative", "over-limit", "hr-secondary", "bp-missing", "bp-zero",
        "bp-negative", "bp-over-limit", "negative-version", "old", "future",
        "end-before-start", "end-future", "end-old", "empty-id", "long-id",
        "control-id", "unicode-id", "empty-app", "long-app", "control-app",
        "format-app", "long-device", "control-device", "format-device"
    };

    [Theory]
    [MemberData(nameof(InvalidRecordCases))]
    public async Task InvalidRecordMakesEntireBatchFailWithoutMutation(string violation)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var valid = bridge.Record("original");
        using (var seed = await bridge.SendBatch(bridge.Batch(valid), token))
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var before = Assert.Single(bridge.Host.Records);
        var record = bridge.Record("invalid");
        record = violation switch
        {
            "zero" => record with { Value = 0 },
            "negative" => record with { Value = -1 },
            "over-limit" => record with { Value = 1000.1 },
            "hr-secondary" => record with { SecondaryValue = 80 },
            "bp-missing" => record with { Metric = HealthMetric.BloodPressure },
            "bp-zero" => record with { Metric = HealthMetric.BloodPressure, SecondaryValue = 0 },
            "bp-negative" => record with { Metric = HealthMetric.BloodPressure, SecondaryValue = -1 },
            "bp-over-limit" => record with { Metric = HealthMetric.BloodPressure, SecondaryValue = 1000.1 },
            "negative-version" => record with { Version = -1 },
            "old" => record with { MeasuredAt = bridge.Clock.GetUtcNow().AddDays(-30).AddTicks(-1) },
            "future" => record with { MeasuredAt = bridge.Clock.GetUtcNow().AddMinutes(2).AddTicks(1) },
            "end-before-start" => record with { EndAt = record.MeasuredAt.AddTicks(-1) },
            "end-future" => record with { EndAt = bridge.Clock.GetUtcNow().AddMinutes(2).AddTicks(1) },
            "end-old" => record with { EndAt = bridge.Clock.GetUtcNow().AddDays(-30).AddTicks(-1) },
            "empty-id" => record with { RecordId = "" },
            "long-id" => record with { RecordId = new string('a', 129) },
            "control-id" => record with { RecordId = "id\u007f" },
            "unicode-id" => record with { RecordId = "id\u00e9" },
            "empty-app" => record with { SourceApp = "" },
            "long-app" => record with { SourceApp = new string('a', 513) },
            "control-app" => record with { SourceApp = "app\n" },
            "format-app" => record with { SourceApp = "app\u202e" },
            "long-device" => record with { SourceDevice = new string('d', 257) },
            "control-device" => record with { SourceDevice = "watch\t" },
            "format-device" => record with { SourceDevice = "watch\u200b" },
            _ => throw new InvalidOperationException(violation)
        };
        var batch = new SyncRecordsRequest([bridge.Record("must-not-be-added"), record],
            [new(HealthMetric.HeartRate, ReadAvailability.Available),
             new(HealthMetric.BloodPressure, ReadAvailability.Available)]);
        using var response = await bridge.SendBatch(batch, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, Assert.Single(bridge.Host.Records));
        Assert.Equal(new CategoryReadStatus(HealthMetric.HeartRate, ReadAvailability.Available),
            Assert.Single(bridge.Host.Categories));
    }

    [Fact]
    public async Task InclusiveTechnicalTimestampAndProvenanceBoundsAreAccepted()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var now = bridge.Clock.GetUtcNow();
        var records = new[]
        {
            bridge.Record(new string('i', 128)) with
            {
                MeasuredAt = now.AddDays(-30), EndAt = now.AddDays(-30), Value = 1000,
                SourceApp = new string('a', 128), SourceDevice = new string('d', 128)
            },
            bridge.Record("future") with { MeasuredAt = now.AddMinutes(2), EndAt = now.AddMinutes(2), Value = 0.1 },
            bridge.Record("bp") with { Metric = HealthMetric.BloodPressure, Value = 1000, SecondaryValue = 1000 }
        };
        using var response = await bridge.SendBatch(new(records,
            [new(HealthMetric.HeartRate, ReadAvailability.Available),
             new(HealthMetric.BloodPressure, ReadAvailability.Available)]), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read<SyncRecordsResponse>(response);
        Assert.Equal(3, result.Accepted);
        Assert.Equal(3, result.Retained);
        foreach (var record in records)
            Assert.Contains(bridge.Host.Records, stored => stored.Record == record);
    }

    [Theory]
    [InlineData(ReadAvailability.NoData)]
    [InlineData(ReadAvailability.Denied)]
    [InlineData(ReadAvailability.Unsupported)]
    [InlineData(ReadAvailability.PermissionNotVerifiable)]
    public async Task RecordsRequireAvailableSelectedCategory(ReadAvailability availability)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        using var response = await bridge.SendBatch(new([bridge.Record()],
            [new(HealthMetric.HeartRate, availability)]), token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(bridge.Host.Records);
        using var statusesOnly = await bridge.SendBatch(new([],
            [new(HealthMetric.HeartRate, availability)]), token);
        Assert.Equal(HttpStatusCode.OK, statusesOnly.StatusCode);
        Assert.Equal(availability, Assert.Single(bridge.Host.Categories).Availability);
        Assert.Empty(bridge.Host.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDuplicateCategoriesAreRejected(bool duplicate)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        CategoryReadStatus[] categories = duplicate
            ? [new(HealthMetric.HeartRate, ReadAvailability.Available), new(HealthMetric.HeartRate, ReadAvailability.Available)]
            : [new(HealthMetric.BloodPressure, ReadAvailability.Available)];
        using var response = await bridge.SendBatch(new([bridge.Record()], categories), token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(bridge.Host.Categories);
        Assert.Empty(bridge.Host.Records);
    }

    [Fact]
    public async Task DedupeIgnoresEqualAndOlderVersionsAndDatesCorrectionsAtReceipt()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var original = bridge.Record() with { Version = 4 };
        var first = await bridge.Accept(bridge.Batch(original), token);
        var stored = Assert.Single(bridge.Host.Records);
        Assert.Equal(bridge.Clock.GetUtcNow(), stored.ReceivedAt);
        Assert.Equal(stored.ReceivedAt, first.ReceivedAt);
        bridge.Clock.Advance(TimeSpan.FromSeconds(10));
        foreach (var duplicate in new[]
        {
            original, original with { Value = 999 }, original with { Version = 3, Value = 1 }
        })
        {
            var result = await bridge.Accept(bridge.Batch(duplicate), token);
            Assert.Equal(0, result.Accepted);
            Assert.Equal(1, result.Retained);
            Assert.Equal(stored, Assert.Single(bridge.Host.Records));
        }
        var correction = original with { Version = 5, Value = 75, SourceDevice = "Corrected watch" };
        var corrected = await bridge.Accept(bridge.Batch(correction), token);
        Assert.Equal(1, corrected.Accepted);
        var updated = Assert.Single(bridge.Host.Records);
        Assert.Equal(correction, updated.Record);
        Assert.Equal(bridge.Clock.GetUtcNow(), updated.ReceivedAt);
        Assert.True(updated.ReceivedAt > stored.ReceivedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HighestVersionWinsRegardlessOfBatchOrder(bool reverse)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var original = bridge.Record();
        var records = new[] { original, original with { Version = 2, Value = 90 }, original with { Version = 1, Value = 80 } };
        if (reverse) Array.Reverse(records);
        await bridge.Accept(bridge.Batch(records), token);
        Assert.Equal(original with { Version = 2, Value = 90 }, Assert.Single(bridge.Host.Records).Record);
    }

    [Fact]
    public async Task DedupeKeyIncludesSourceAppMetricAndIdButNotSourceDevice()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var original = bridge.Record("shared");
        var records = new[]
        {
            original,
            original with { SourceApp = "Other app", Value = 81 },
            original with { Metric = HealthMetric.BloodPressure, Value = 120, SecondaryValue = 80 },
            original with { RecordId = "other", Value = 82 },
            original with { SourceDevice = "Other watch", Value = 83 }
        };
        var result = await bridge.Accept(new(records,
            [new(HealthMetric.HeartRate, ReadAvailability.Available),
             new(HealthMetric.BloodPressure, ReadAvailability.Available)]), token);
        Assert.Equal(4, result.Accepted);
        Assert.Equal(4, result.Retained);
        foreach (var record in records.Take(4))
            Assert.Contains(bridge.Host.Records, stored => stored.Record == record);
        Assert.DoesNotContain(bridge.Host.Records, stored => stored.Record.SourceDevice == "Other watch");
    }

    [Fact]
    public async Task BatchLimitIs256AndRetainedStorageIsBoundedAt2048()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var oversized = Enumerable.Range(0, 257).Select(i => bridge.Record($"too-many-{i}")).ToArray();
        using (var response = await bridge.SendBatch(bridge.Batch(oversized), token))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(bridge.Host.Records);
        for (var batch = 0; batch < 9; batch++)
        {
            var records = Enumerable.Range(batch * 256, 256).Select(i => bridge.Record($"record-{i}")).ToArray();
            var result = await bridge.Accept(bridge.Batch(records), token);
            Assert.Equal(256, result.Accepted);
            Assert.Equal(Math.Min((batch + 1) * 256, 2048), result.Retained);
            Assert.Equal(result.Retained, bridge.Host.Records.Count);
            bridge.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(2048, bridge.Host.Records.Count);
        Assert.Equal(2048, bridge.Host.Records.Select(r => r.Record.RecordId).Distinct().Count());
    }

    [Fact]
    public async Task DeleteRecordsKeepsSessionUsable()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        await bridge.Accept(bridge.Batch(), token);
        bridge.Host.DeleteRecords();
        Assert.Empty(bridge.Host.Records);
        Assert.Equal("Test phone", bridge.Host.PairedDeviceName);
        Assert.Equal(1, (await bridge.Accept(bridge.Batch(), token)).Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalRevokeAndAuthenticatedDeleteClearSessionAndRequireExplicitRefresh(bool remote)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        await bridge.Accept(bridge.Batch(), token);
        if (remote)
        {
            using var response = await bridge.DeleteSession(token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        else bridge.Host.Revoke();
        AssertCleared(bridge.Host);
        Assert.True(bridge.Host.IsRunning);
        using (var stale = await bridge.SendBatch(bridge.Batch(), token))
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        bridge.Host.RefreshPairing();
        var replacement = await bridge.Pair(bridge.ReadPairing());
        Assert.NotEqual(token, replacement);
        await bridge.Accept(bridge.Batch(), replacement);
    }

    [Fact]
    public async Task UnauthorizedDeleteCannotRevokeSession()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        await bridge.Accept(bridge.Batch(), token);
        using var response = await bridge.DeleteSession("wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(bridge.Host.Records);
        await bridge.Accept(bridge.Batch(bridge.Record("still-authorized")), token);
    }

    [Fact]
    public async Task RevokeClearsPendingPollCredentials()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Revoke();
        AssertCleared(bridge.Host);
        AssertExpired(await bridge.Poll(pending));
    }

    [Fact]
    public async Task StopClearsStateAndRestartDoesNotResurrectTokens()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        await bridge.Accept(bridge.Batch(), token);
        await bridge.Host.StopAsync();
        Assert.False(bridge.Host.IsRunning);
        AssertCleared(bridge.Host);
        await bridge.Host.StopAsync();
        await bridge.Host.StartAsync(IPAddress.Loopback, 0);
        bridge.ResetClient();
        Assert.True(bridge.Host.IsRunning);
        Assert.NotEqual(bridge.Pairing.Challenge, bridge.ReadPairing().Challenge);
        using var stale = await bridge.SendBatch(bridge.Batch(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        await bridge.Pair(bridge.ReadPairing());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionExpiresAtCertificateExpiryEvenWhenApprovedLater(bool getterFirst)
    {
        await using var bridge = await RunningBridge.Start();
        using var certificate = await bridge.ReadServerCertificate();
        var expiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        bridge.Clock.Advance(TimeSpan.FromHours(1));
        bridge.Host.RefreshPairing();
        var token = await bridge.Pair(bridge.ReadPairing());
        await bridge.Accept(bridge.Batch(), token);
        bridge.Clock.Advance(expiresAt - bridge.Clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        await bridge.Accept(bridge.Batch(bridge.Record("before-expiry")), token);
        bridge.Clock.Advance(TimeSpan.FromSeconds(1));
        if (getterFirst) Assert.Null(bridge.Host.PairedDeviceName);
        using var expired = await bridge.Raw("/v1/records", "{", token);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        AssertCleared(bridge.Host);
    }

    [Fact]
    public async Task ApprovedButUndeliveredTokenCannotBePolledAfterCertificateExpiry()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Approve(pending.RequestId);
        bridge.Clock.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));
        AssertExpired(await bridge.Poll(pending));
        AssertCleared(bridge.Host);
    }

    [Theory]
    [InlineData("/v1/approve")]
    [InlineData("/v1/reject")]
    [InlineData("/v1/revoke")]
    [InlineData("/v1/refresh")]
    [InlineData("/v1/pairing/approve")]
    [InlineData("/v1/admin/approve")]
    public async Task DesktopAdministrationIsNotExposedOverHttp(string path)
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        using var response = await bridge.Raw(path, "{}", token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Test phone", bridge.Host.PairedDeviceName);
    }

    [Fact]
    public async Task PairingRateLimitIsTenPerMinuteAndRecovers()
    {
        await using var bridge = await RunningBridge.Start();
        for (var i = 0; i < 10; i++)
        {
            if (i > 0) bridge.Host.RefreshPairing();
            var pending = await bridge.RequestPairing(bridge.ReadPairing());
            bridge.Host.Reject(pending.RequestId);
        }
        bridge.Host.RefreshPairing();
        var pairing = bridge.ReadPairing();
        using (var limited = await bridge.Post("/v1/pairing", new CreatePairingRequest(pairing.Challenge, "Limited")))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Empty(bridge.Host.PendingDevices);
        bridge.Clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        await bridge.RequestPairing(pairing);
    }

    [Fact]
    public async Task StatusRateLimitIsSixtyPerMinuteAndDoesNotConsumePendingRequest()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        for (var i = 0; i < 60; i++)
            Assert.Equal(PairingState.Pending, (await bridge.Poll(pending)).State);
        using var anotherClient = CreateClient(bridge.Pairing);
        using var content = new StringContent(
            JsonSerializer.Serialize(new PairingStatusRequest(pending.RequestId, pending.PollSecret), Json),
            Encoding.UTF8, "application/json");
        using (var limited = await anotherClient.PostAsync("/v1/pairing/status", content))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        bridge.Clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(PairingState.Pending, (await bridge.Poll(pending)).State);
    }

    [Fact]
    public async Task IngestionRateLimitIs120PerMinuteAndRecoversWithoutMutation()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        for (var i = 0; i < 120; i++)
            await bridge.Accept(bridge.Batch(), token);
        using (var limited = await bridge.SendBatch(bridge.Batch(bridge.Record("limited")), token))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Single(bridge.Host.Records);
        bridge.Clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, (await bridge.Accept(bridge.Batch(bridge.Record("after-limit")), token)).Accepted);
    }

    [Fact]
    public async Task HttpsRequiresExactRawDerPinAndRejectsNameMismatch()
    {
        await using var bridge = await RunningBridge.Start();
        using var certificate = await bridge.ReadServerCertificate();
        Assert.True(certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow);
        Assert.InRange(certificate.NotAfter.ToUniversalTime() - bridge.Clock.GetUtcNow().UtcDateTime,
            TimeSpan.FromHours(8).Subtract(TimeSpan.FromSeconds(5)),
            TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(5)));
        var pin = DecodePin(bridge.Pairing.CertificateSha256);
        Assert.True(CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), pin));
        Assert.False(ValidateCertificate(certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch, pin));
        Assert.False(ValidateCertificate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, pin));
        Assert.False(ValidateCertificate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors, pin));
        pin[0] ^= 1;
        using var wrongPin = CreateClient(bridge.Pairing, pin);
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongPin.GetAsync("/v1/records"));
        using var wrongName = CreateClient(bridge.Pairing);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/records");
        request.Headers.Host = "not-the-paired-host.invalid";
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongName.SendAsync(request));
    }

    [Fact]
    public void CertificateValidatorRejectsExpiredAndNotYetValidCertificatesEvenWithMatchingPins()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var expired = request.CreateSelfSigned(now.AddDays(-2), now.AddDays(-1));
        using var future = request.CreateSelfSigned(now.AddDays(1), now.AddDays(2));
        foreach (var certificate in new[] { expired, future })
            Assert.False(ValidateCertificate(certificate, null, SslPolicyErrors.None, SHA256.HashData(certificate.RawData)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Phone\n")]
    [InlineData("Phone\u007f")]
    [InlineData("Phone\u202e")]
    [InlineData("Phone\u200b")]
    [InlineData("Phone\u2028")]
    public async Task InvalidDeviceNamesDoNotConsumeChallenge(string name)
    {
        await using var bridge = await RunningBridge.Start();
        using var invalid = await bridge.Post("/v1/pairing", new CreatePairingRequest(bridge.Pairing.Challenge, name));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(bridge.Host.PendingDevices);
        Assert.NotNull(bridge.Host.PairingString);
        await bridge.RequestPairing();
    }

    [Fact]
    public async Task DeviceNameLengthBoundaryMatchesClientLimitOf80()
    {
        await using var bridge = await RunningBridge.Start();
        using (var invalid = await bridge.Post("/v1/pairing",
            new CreatePairingRequest(bridge.Pairing.Challenge, new string('p', 81))))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var valid = await bridge.Post("/v1/pairing",
            new CreatePairingRequest(bridge.Pairing.Challenge, new string('p', 80)));
        Assert.True(valid.IsSuccessStatusCode);
        Assert.Equal(new string('p', 80), Assert.Single(bridge.Host.PendingDevices).DeviceName);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("169.254.1.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("127.0.0.2")]
    public async Task UnsafeOrNonExplicitLoopbackAddressesCannotBind(string address)
    {
        await using var host = new BridgeHost();
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(IPAddress.Parse(address), 0));
        Assert.False(host.IsRunning);
        AssertCleared(host);
    }

    [Fact]
    public async Task UnassignedPrivateInterfaceCannotBind()
    {
        var assigned = BridgeHost.GetBindableAddresses();
        var address = Enumerable.Range(1, 254).Select(i => IPAddress.Parse($"10.254.253.{i}"))
            .First(ip => !assigned.Contains(ip));
        await using var host = new BridgeHost();
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(address, 0));
        Assert.False(host.IsRunning);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task InvalidPortCannotBind(int port)
    {
        await using var host = new BridgeHost();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.StartAsync(IPAddress.Loopback, port));
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task SecondStartCannotDestroyTheActiveSession()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        await bridge.Accept(bridge.Batch(), token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.Host.StartAsync(IPAddress.Loopback, 0));
        Assert.True(bridge.Host.IsRunning);
        Assert.Single(bridge.Host.Records);
        await bridge.Accept(bridge.Batch(bridge.Record("still-live")), token);
    }

    [Fact]
    public async Task CancelledStartAndDisposedHostDoNotPublishPairingMaterial()
    {
        var host = new BridgeHost();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.StartAsync(IPAddress.Loopback, 0, cancellation.Token));
        AssertCleared(host);
        await host.DisposeAsync();
        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync(IPAddress.Loopback, 0));
        AssertCleared(host);
    }

    [Fact]
    public async Task UnclaimedApprovalExpiresWithPendingRequestAndClearsSession()
    {
        await using var bridge = await RunningBridge.Start();
        var pending = await bridge.RequestPairing();
        bridge.Host.Approve(pending.RequestId);
        bridge.Clock.Advance(TimeSpan.FromMinutes(2));
        AssertExpired(await bridge.Poll(pending));
        AssertCleared(bridge.Host);
        bridge.Host.RefreshPairing();
        await bridge.Pair(bridge.ReadPairing());
    }

    [Fact]
    public async Task ConcurrentCorrectionsKeepHighestVersionAndNeverExceedOneIdentity()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        var original = bridge.Record();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            bridge.SendBatch(bridge.Batch(original with { Version = i, Value = 70 + i }), token)));
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(original with { Version = 19, Value = 89 }, Assert.Single(bridge.Host.Records).Record);
    }

    [Fact]
    public async Task ErrorsUseSafeProblemDetailsWithoutEchoingRequestSecrets()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        using var response = await bridge.Raw("/v1/records", "{\"privateSecret\":\"do-not-echo-me\"}", token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain("privateSecret", body);
        Assert.DoesNotContain("do-not-echo-me", body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("PulsePal", body);
        var problem = JsonNode.Parse(body)!;
        Assert.Equal(400, problem["status"]!.GetValue<int>());
        Assert.Null(problem["detail"]);
        Assert.Null(problem["instance"]);
    }

    [Fact]
    public async Task GetCannotExposeHealthRecordsAndSnapshotsCannotMutateStore()
    {
        await using var bridge = await RunningBridge.Start();
        var token = await bridge.Pair();
        await bridge.Accept(bridge.Batch(), token);
        var original = Assert.Single(bridge.Host.Records);
        if (bridge.Host.Records is ReceivedHealthRecord[] snapshot)
            snapshot[0] = original with { Record = original.Record with { Value = 999 } };
        if (bridge.Host.Categories is CategoryReadStatus[] categories)
            categories[0] = new(HealthMetric.BloodPressure, ReadAvailability.Available);
        Assert.Equal(original, Assert.Single(bridge.Host.Records));
        Assert.Equal(HealthMetric.HeartRate, Assert.Single(bridge.Host.Categories).Metric);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/records");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await bridge.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.DoesNotContain(original.Record.RecordId, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiIsAvailableOnlyInDevelopment()
    {
        await using var bridge = await RunningBridge.Start();
        using var response = await bridge.Client.GetAsync("/openapi/v1.json");
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            return;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var paths = document["paths"]!.AsObject();
        Assert.Equal(4, paths.Count);
        Assert.NotNull(paths["/v1/pairing"]!["post"]!["responses"]!["201"]);
        Assert.NotNull(paths["/v1/pairing/status"]!["post"]!["responses"]!["200"]);
        Assert.NotNull(paths["/v1/records"]!["post"]!["responses"]!["200"]);
        Assert.NotNull(paths["/v1/session"]!["delete"]!["responses"]!["204"]);
        Assert.All(paths, path => Assert.All(path.Value!.AsObject(),
            operation => Assert.False(string.IsNullOrWhiteSpace(operation.Value!["summary"]?.GetValue<string>()))));
    }

    private static void AssertExpired(PairingStatusResponse response)
    {
        Assert.Equal(PairingState.Expired, response.State);
        Assert.Null(response.DeviceToken);
    }

    private static void AssertCleared(BridgeHost host)
    {
        Assert.Null(host.PairingString);
        Assert.Null(host.PairedDeviceName);
        Assert.Empty(host.PendingDevices);
        Assert.Empty(host.Records);
        Assert.Empty(host.Categories);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), Json)
            ?? throw new Xunit.Sdk.XunitException($"Missing {typeof(T).Name} response.");
    }

    private static byte[] DecodePin(string pin) =>
        pin.Length == 64 && pin.All(Uri.IsHexDigit) ? Convert.FromHexString(pin) : Convert.FromBase64String(pin);

    private static bool ValidateCertificate(X509Certificate2? certificate, X509Chain? chain,
        SslPolicyErrors errors, byte[] expectedPin)
    {
        if (certificate is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            return false;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), expectedPin))
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0)
            return true;
        return chain is not null
            && certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
            && chain.ChainStatus.Length > 0
            && chain.ChainStatus.All(status => status.Status == X509ChainStatusFlags.UntrustedRoot);
    }

    private static HttpClient CreateClient(PairingPayload pairing, byte[]? pinOverride = null)
    {
        var pin = pinOverride ?? DecodePin(pairing.CertificateSha256);
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                ValidateCertificate(certificate, chain, errors, pin)
        };
        return new HttpClient(handler) { BaseAddress = new Uri(pairing.Endpoint), Timeout = TimeSpan.FromSeconds(15) };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long utcTicks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref utcTicks, duration.Ticks);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RunningBridge : IAsyncDisposable
    {
        public ManualTimeProvider Clock { get; } = new();
        public BridgeHost Host { get; }
        public PairingPayload Pairing { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;

        private RunningBridge() => Host = new BridgeHost(Clock);

        public static async Task<RunningBridge> Start()
        {
            var bridge = new RunningBridge();
            try
            {
                await bridge.Host.StartAsync(IPAddress.Loopback, 0);
                bridge.Pairing = bridge.ReadPairing();
                bridge.ResetClient();
                return bridge;
            }
            catch
            {
                await bridge.DisposeAsync();
                throw;
            }
        }

        public PairingPayload ReadPairing() =>
            JsonSerializer.Deserialize<PairingPayload>(Assert.IsType<string>(Host.PairingString), Json)!;

        public void ResetClient()
        {
            Client?.Dispose();
            Client = CreateClient(ReadPairing());
        }

        public HealthRecord Record(string id = "record-1") =>
            new(id, 0, HealthMetric.HeartRate, Clock.GetUtcNow().AddMinutes(-1), null, 72, null, "Health app", "Watch");

        public SyncRecordsRequest Batch(params HealthRecord[] records) =>
            new(records.Length == 0 ? [Record()] : records, [new(HealthMetric.HeartRate, ReadAvailability.Available)]);

        public Task<HttpResponseMessage> Post<T>(string path, T body) =>
            Raw(path, JsonSerializer.Serialize(body, Json));

        public async Task<HttpResponseMessage> Raw(string path, string body, string? token = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> SendBatch(SyncRecordsRequest batch, string? token) =>
            Raw("/v1/records", JsonSerializer.Serialize(batch, Json), token);

        public async Task<SyncRecordsResponse> Accept(SyncRecordsRequest batch, string token)
        {
            using var response = await SendBatch(batch, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await Read<SyncRecordsResponse>(response);
        }

        public async Task<PendingPairingResponse> RequestPairing(PairingPayload? pairing = null)
        {
            using var response = await Post("/v1/pairing", new CreatePairingRequest((pairing ?? Pairing).Challenge, "Test phone"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("/v1/pairing/status", response.Headers.Location?.OriginalString);
            return await Read<PendingPairingResponse>(response);
        }

        public async Task<PairingStatusResponse> Poll(PendingPairingResponse pending)
        {
            using var response = await Post("/v1/pairing/status", new PairingStatusRequest(pending.RequestId, pending.PollSecret));
            return await Read<PairingStatusResponse>(response);
        }

        public async Task<string> Pair(PairingPayload? pairing = null)
        {
            var pending = await RequestPairing(pairing);
            Host.Approve(pending.RequestId);
            var approved = await Poll(pending);
            Assert.Equal(PairingState.Approved, approved.State);
            Assert.False(string.IsNullOrWhiteSpace(approved.DeviceToken));
            return approved.DeviceToken!;
        }

        public async Task<HttpResponseMessage> DeleteSession(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await Client.SendAsync(request);
        }

        public async Task<X509Certificate2> ReadServerCertificate()
        {
            byte[]? raw = null;
            var pin = DecodePin(Pairing.CertificateSha256);
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                {
                    var valid = ValidateCertificate(certificate, chain, errors, pin);
                    if (valid) raw = certificate!.RawData;
                    return valid;
                }
            };
            using var client = new HttpClient(handler) { BaseAddress = Client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("/v1/records");
            return X509CertificateLoader.LoadCertificate(Assert.IsType<byte[]>(raw));
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await Host.DisposeAsync();
        }
    }
}
