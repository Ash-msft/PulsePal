using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PulsePal.App.Services;
using PulsePal.Connected;

namespace PulsePal.App;

public sealed partial class AppController
{
    // This smoke path uses the real bridge on loopback; it never bypasses pairing or authorization.
    internal async Task RunConnectedSmokeTestAsync()
    {
        var checks = new Dictionary<string, bool>();
        try
        {
            _window!.ViewModel.ShowConnectedHealthCommand.Execute(null);
            await Task.Delay(1200);
            var window = _connectedWindow ?? throw new InvalidOperationException();
            var host = _connectedHost ?? throw new InvalidOperationException();
            checks["PopupCommandOpensOff"] = window.IsReadyForSmoke && window.DisplaysOffForSmoke && !host.IsRunning;
            HandleTrayCommand("connected");
            checks["TrayReusesWindow"] = ReferenceEquals(window, _connectedWindow);

            int port;
            using (var reservation = new TcpListener(IPAddress.Loopback, 0))
            {
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            await host.StartAsync(IPAddress.Loopback, port);
            await Task.Delay(1200);
            checks["RealLoopbackListenerStarted"] = host.IsRunning;
            checks["PairingPayloadDisplayed"] = window.DisplaysPairingForSmoke;
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
                checks["LoopbackPortResponds"] = client.Connected;
            }
            var payload = JsonSerializer.Deserialize<PairingPayload>(host.PairingString!, ConnectedJson.CreateOptions())
                ?? throw new InvalidOperationException();
            var endpoint = new Uri(payload.Endpoint);
            if (endpoint.Scheme != "https" || endpoint.Host != IPAddress.Loopback.ToString() || endpoint.Port != port)
                throw new InvalidOperationException();
            try
            {
                using var handler = new HttpClientHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                        certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.RawData))
                            .Equals(payload.CertificateSha256, StringComparison.OrdinalIgnoreCase)
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await client.GetAsync(new Uri(endpoint, "/v1/records"));
                checks["PinnedHttpsRequiresAuthorization"] = response.StatusCode == HttpStatusCode.Unauthorized;
            }
            catch
            {
                checks["PinnedHttpsRequiresAuthorization"] = false;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            checks["NativeClosePosted"] = PostConnectedSmokeClose(hwnd, 0x0010, 0, 0);
            for (var attempt = 0; attempt < 100 && !window.IsClosed; attempt++)
                await Task.Delay(100);
            checks["NativeCloseStopsAndClears"] = window.IsClosed && !host.IsRunning
                && host.PairingString is null && host.Records.Count == 0 && host.PendingDevices.Count == 0;
            HandleTrayCommand("connected");
            await Task.Delay(1200);
            checks["ReopenIsOff"] = _connectedWindow is { IsReadyForSmoke: true, DisplaysOffForSmoke: true }
                && _connectedHost is { IsRunning: false } && !ReferenceEquals(host, _connectedHost);
            var reopenedHost = _connectedHost ?? throw new InvalidOperationException();
            await reopenedHost.StartAsync(IPAddress.Loopback, 0);
            await ShutdownAsync();
            checks["ControllerShutdownStopsListener"] = !reopenedHost.IsRunning
                && _connectedWindow is null && _connectedHost is null;
        }
        catch
        {
            checks["NoTestException"] = false;
        }
        finally
        {
            try { await CloseConnectedHealthAsync(); }
            catch { checks["FinalCleanup"] = false; }
            var passed = checks.Count > 0 && checks.Values.All(value => value);
            Environment.ExitCode = passed ? 0 : 1;
            try
            {
                Directory.CreateDirectory(AppLog.DirectoryPath);
                await File.WriteAllTextAsync(Path.Combine(AppLog.DirectoryPath, "smoke-test-connected.json"),
                    JsonSerializer.Serialize(new { Passed = passed, Checks = checks },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { RequestExit(); }
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostConnectedSmokeClose(nint hwnd, uint message, nuint wParam, nint lParam);
}
