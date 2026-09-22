using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PulsePal.Tests")]

namespace PulsePal.Connected;

/// <summary>Validates a manually transferred, short-lived desktop identity before any network request.</summary>
public static class PairingPolicy
{
    public static PairingPayload Parse(string pairingString) => Parse(pairingString, false, TimeProvider.System);

    internal static PairingPayload Parse(string pairingString, bool allowLoopback, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(pairingString) || pairingString.Length > 8192)
            throw new ArgumentException("A desktop pairing payload is required.");
        PairingPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PairingPayload>(pairingString, ConnectedJson.CreateOptions())
                ?? throw new JsonException();
        }
        catch (JsonException) { throw new ArgumentException("Invalid pairing payload."); }
        var now = clock.GetUtcNow();
        if (payload.Protocol != 1 || payload.ExpiresAt <= now || payload.ExpiresAt > now.AddMinutes(5) ||
            !IsSecret(payload.Challenge) || payload.CertificateSha256 is null ||
            payload.CertificateSha256.Length != 64 || !payload.CertificateSha256.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(payload.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Port is < 1 or > 65535 || uri.HostNameType != UriHostNameType.IPv4 ||
            !IPAddress.TryParse(uri.Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !(IsPrivate(address) || (allowLoopback && address.Equals(IPAddress.Loopback))) ||
            // Reject URI parser aliases (integer/octal IPv4, escaped hosts, or backslashes).
            (payload.Endpoint != $"https://{address}:{uri.Port}" &&
             payload.Endpoint != $"https://{address}:{uri.Port}/"))
            throw new ArgumentException("Pairing requires an unexpired private IPv4 HTTPS desktop identity.");
        return payload;
    }

    internal static bool IsSecret(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors,
        string host, byte[] expectedHash, DateTimeOffset now)
    {
        if (certificate is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;
        try
        {
            using var cert = new X509Certificate2(certificate);
            return cert.NotBefore.ToUniversalTime() <= now.UtcDateTime &&
                cert.NotAfter.ToUniversalTime() > now.UtcDateTime &&
                cert.NotAfter.ToUniversalTime() - cert.NotBefore.ToUniversalTime() <= TimeSpan.FromHours(9) &&
                cert.MatchesHostname(host, allowWildcards: false, allowCommonName: false) &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), expectedHash);
        }
        catch (CryptographicException) { return false; }
    }
}

/// <summary>A foreground-only client. Tokens and pending secrets remain only in this instance's memory.</summary>
public sealed class PinnedBridgeClient : IDisposable
{
    private readonly PairingPayload payload;
    private readonly TimeProvider clock;
    private readonly HttpClient client;
    private readonly TimeSpan operationTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private PendingPairingResponse? pending;
    private string? token;
    private bool disposed;
    public bool IsPaired => !disposed && token is not null;

    public PinnedBridgeClient(string pairingString) : this(pairingString, false, TimeProvider.System) { }

    internal PinnedBridgeClient(string pairingString, bool allowLoopback, TimeProvider? timeProvider = null,
        TimeSpan? operationTimeout = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        payload = PairingPolicy.Parse(pairingString, allowLoopback, clock);
        var uri = new Uri(payload.Endpoint);
        var pin = Convert.FromHexString(payload.CertificateSha256);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    PairingPolicy.ValidateCertificate(cert, errors, uri.Host, pin, clock.GetUtcNow())
            }
        };
        client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<PendingPairingResponse> RequestPairingAsync(string deviceName, CancellationToken ct = default)
    {
        using var linked = Link(ct);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (payload.ExpiresAt <= clock.GetUtcNow()) throw new InvalidOperationException("Pairing expired.");
            if (pending is not null || token is not null) throw new InvalidOperationException("Pairing already requested.");
            if (!HealthMetadata.IsValid(deviceName, 80))
                throw new ArgumentException("A short device name is required.");
            var result = await SendAsync<PendingPairingResponse>(HttpMethod.Post, "v1/pairing",
                new CreatePairingRequest(payload.Challenge, deviceName), false, linked.Token);
            if (result.RequestId == Guid.Empty || !PairingPolicy.IsSecret(result.PollSecret) ||
                result.ExpiresAt <= clock.GetUtcNow() || result.ExpiresAt > clock.GetUtcNow().AddMinutes(5))
                throw new InvalidOperationException("Invalid pairing response.");
            pending = result;
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<PairingState> PollApprovalAsync(CancellationToken ct = default)
    {
        using var linked = Link(ct);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (token is not null) return PairingState.Approved;
            var request = pending ?? throw new InvalidOperationException("Request pairing first.");
            if (request.ExpiresAt <= clock.GetUtcNow()) { pending = null; return PairingState.Expired; }
            var response = await SendAsync<PairingStatusResponse>(HttpMethod.Post, "v1/pairing/status",
                new PairingStatusRequest(request.RequestId, request.PollSecret), false, linked.Token);
            if (response.State == PairingState.Approved)
            {
                if (!PairingPolicy.IsSecret(response.DeviceToken))
                    throw new InvalidOperationException("Invalid approval response.");
                token = response.DeviceToken;
                pending = null;
            }
            else if (response.State is PairingState.Expired or PairingState.Rejected)
                pending = null;
            else if (response.State != PairingState.Pending || response.DeviceToken is not null)
                throw new InvalidOperationException("Invalid approval response.");
            return response.State;
        }
        finally { gate.Release(); }
    }

    public async Task<SyncRecordsResponse> SyncAsync(SyncRecordsRequest request, CancellationToken ct = default)
    {
        using var linked = Link(ct);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (request.Records is null || request.Categories is null || request.Records.Length > 256 ||
                request.Categories.Length is < 1 or > 2)
                throw new ArgumentException("Invalid health batch.");
            return await SendAsync<SyncRecordsResponse>(HttpMethod.Post, "v1/records", request, true, linked.Token);
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        using var linked = Link(ct);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (token is not null)
                await SendAsync<object>(HttpMethod.Delete, "v1/session", null, true, linked.Token);
        }
        finally { token = null; pending = null; gate.Release(); }
    }

    private CancellationTokenSource Link(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        // ResponseHeadersRead stops HttpClient.Timeout at headers; cover body reads and gate waits too.
        linked.CancelAfter(operationTimeout);
        return linked;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authenticated)
        {
            if (token is null) throw new InvalidOperationException("Desktop approval is required.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, ConnectedJson.CreateOptions());
            if (bytes.Length > 256 * 1024) throw new ArgumentException("Health batch is too large.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) token = null;
        // Redirects are deliberately treated as failures. Never include response content or URLs in exceptions.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Desktop rejected the request.", null, response.StatusCode);
        if (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NoContent) return default!;
        var expected = path == "v1/pairing" ? HttpStatusCode.Created : HttpStatusCode.OK;
        if (response.StatusCode != expected) throw new InvalidOperationException("Invalid desktop response.");
        if (response.Content.Headers.ContentLength > 8192) throw new InvalidOperationException("Invalid desktop response.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 8192) throw new InvalidOperationException("Invalid desktop response.");
            buffer.Write(chunk, 0, count);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), ConnectedJson.CreateOptions())
            ?? throw new InvalidOperationException("Invalid desktop response.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        token = null;
        pending = null;
        client.Dispose();
        // The semaphore and cancellation source may still be in use by an interrupted foreground operation.
    }
}
