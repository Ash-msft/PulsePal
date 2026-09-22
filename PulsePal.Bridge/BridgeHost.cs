using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PulsePal.Bridge.Endpoints;
using PulsePal.Bridge.Middleware;
using PulsePal.Bridge.Services;
using PulsePal.Connected;

namespace PulsePal.Bridge;

/// <summary>An explicitly bound, ephemeral HTTPS bridge controlled only by the desktop process.</summary>
public sealed class BridgeHost : IAsyncDisposable
{
    public const int MaximumRequestBytes = 256 * 1024;
    private readonly TimeProvider clock;
    private readonly BridgeService service;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private WebApplication? application;
    private X509Certificate2? certificate;
    private ITimer? expiryTimer;
    private bool disposed;

    public BridgeHost(TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        service = new BridgeService(clock);
    }

    public bool IsRunning => service.IsRunning;
    public string? PairingString => service.PairingString;
    public IReadOnlyList<PendingDevice> PendingDevices => service.PendingDevices;
    public IReadOnlyList<ReceivedHealthRecord> Records => service.Records;
    public IReadOnlyList<CategoryReadStatus> Categories => service.Categories;
    public string? PairedDeviceName => service.PairedDeviceName;
    public void Approve(Guid requestId) => service.Approve(requestId);
    public void Reject(Guid requestId) => service.Reject(requestId);
    public void DeleteRecords() => service.DeleteRecords();
    public void Revoke() => service.Revoke();
    public void RefreshPairing() => service.Refresh();

    /// <summary>Lists currently assigned private IPv4 addresses on operational interfaces, excluding loopback.</summary>
    public static IReadOnlyList<IPAddress> GetBindableAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(n => n.Address).Where(IsPrivate).Distinct().ToArray();

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public async Task StartAsync(IPAddress address, int port, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.Equals(IPAddress.Loopback) && !GetBindableAddresses().Contains(address))
            throw new ArgumentException("Select an assigned private IPv4 interface.", nameof(address));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        await lifecycle.WaitAsync(ct);
        var starting = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (application is not null) throw new InvalidOperationException("Bridge is already running.");
            starting = true;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=PulsePal ephemeral bridge", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(address);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var now = clock.GetUtcNow();
            using var generated = request.CreateSelfSigned(now.AddMinutes(-1), now.AddHours(8));
            // Schannel cannot use memory-only private keys. DefaultKeySet gives its
            // temporary key container certificate-lifetime cleanup, without installing
            // a trusted certificate or opting into PersistKeySet.
            var pfx = generated.Export(X509ContentType.Pfx);
            try
            {
                certificate = OperatingSystem.IsWindows()
                    ? X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet)
                    : X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(BridgeHost).Assembly.GetName().Name
            });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = MaximumRequestBytes;
                options.Limits.MaxConcurrentConnections = 64;
                options.Limits.MaxConcurrentUpgradedConnections = 0;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                options.Listen(address, port, listen => listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                }));
            });
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                var strict = ConnectedJson.CreateOptions();
                options.SerializerOptions.PropertyNamingPolicy = strict.PropertyNamingPolicy;
                options.SerializerOptions.PropertyNameCaseInsensitive = strict.PropertyNameCaseInsensitive;
                options.SerializerOptions.UnmappedMemberHandling = strict.UnmappedMemberHandling;
                options.SerializerOptions.AllowDuplicateProperties = strict.AllowDuplicateProperties;
                options.SerializerOptions.NumberHandling = strict.NumberHandling;
                options.SerializerOptions.MaxDepth = strict.MaxDepth;
                options.SerializerOptions.RespectRequiredConstructorParameters = true;
                foreach (var converter in strict.Converters) options.SerializerOptions.Converters.Add(converter);
            });
            builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            builder.Services.AddSingleton<IBridgeService>(service);
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<SafeExceptionHandler>();
            builder.Services.AddOpenApi();
            application = builder.Build();
            application.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
            application.UseStatusCodePages(context =>
                SafeExceptionHandler.WriteProblem(context.HttpContext, context.HttpContext.Response.StatusCode,
                    context.HttpContext.RequestAborted));
            application.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                var protectedRoute = path.StartsWithSegments("/v1/records", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWithSegments("/v1/session", StringComparison.OrdinalIgnoreCase);
                if (protectedRoute && !service.Authenticate(context.Request.Headers.Authorization))
                {
                    await SafeExceptionHandler.WriteProblem(context, 401, context.RequestAborted);
                    return;
                }
                string? operation = path.StartsWithSegments("/v1/pairing/status", StringComparison.OrdinalIgnoreCase)
                    ? "poll" : path.StartsWithSegments("/v1/pairing", StringComparison.OrdinalIgnoreCase)
                    ? "pair" : protectedRoute ? "sync" : null;
                if (operation is not null && !service.AllowRequest(context.Connection.RemoteIpAddress, operation))
                {
                    await SafeExceptionHandler.WriteProblem(context, 429, context.RequestAborted);
                    return;
                }
                if (context.Request.ContentLength > MaximumRequestBytes)
                {
                    await SafeExceptionHandler.WriteProblem(context, 413, context.RequestAborted);
                    return;
                }
                await next(context);
            });
            application.MapBridge();
            if (application.Environment.IsDevelopment()) application.MapOpenApi();
            await application.StartAsync(ct);
            var endpoint = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            service.Start(endpoint, Convert.ToHexString(SHA256.HashData(certificate.RawData)),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
            expiryTimer = clock.CreateTimer(_ => service.Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        catch when (starting)
        {
            service.Stop();
            if (application is not null) { await application.DisposeAsync(); application = null; }
            certificate?.Dispose();
            certificate = null;
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try { await StopCore(ct); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCore(CancellationToken ct)
    {
        service.Stop();
        if (expiryTimer is not null) { await expiryTimer.DisposeAsync(); expiryTimer = null; }
        var app = application;
        application = null;
        try
        {
            if (app is not null)
            {
                try { await app.StopAsync(ct); }
                finally { await app.DisposeAsync(); }
            }
        }
        finally { certificate?.Dispose(); certificate = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await StopCore(CancellationToken.None);
        }
        finally { lifecycle.Release(); }
    }
}
