using PulsePal.Bridge;

namespace PulsePal.App;

public sealed partial class AppController
{
    private BridgeHost? _connectedHost;
    private ConnectedHealthWindow? _connectedWindow;
    private Task? _connectedClose;

    public void ShowConnectedHealth()
    {
        if (_exiting || _disposed || _connectedClose is { IsCompleted: false }) return;
        try
        {
            _connectedHost ??= new BridgeHost();
            if (_connectedWindow is null || _connectedWindow.IsClosed)
                _connectedWindow = new ConnectedHealthWindow(_connectedHost, CloseConnectedHealthAsync);
            _connectedWindow.Activate();
        }
        catch
        {
            // Never forward bridge exceptions, payloads, or records to demo logging/storage.
            _window?.SetError("Connected health could not open. No listener is started by opening this window.");
        }
    }

    private Task CloseConnectedHealthAsync()
    {
        if (_connectedClose is { IsCompleted: false }) return _connectedClose;
        return _connectedClose = CloseConnectedHealthCoreAsync();
    }

    private async Task CloseConnectedHealthCoreAsync()
    {
        var window = _connectedWindow;
        var host = _connectedHost;
        if (window is not null) await window.PrepareForShutdownAsync();
        if (host is not null)
        {
            var failed = false;
            try { await host.StopAsync(); }
            catch { failed = true; }
            try { host.Revoke(); }
            catch { failed = true; }
            try { await host.DisposeAsync(); }
            catch { failed = true; }
            if (failed)
                throw new InvalidOperationException("Connected health cleanup could not be completed.");
        }
        window?.CloseAfterShutdown();
        _connectedWindow = null;
        _connectedHost = null;
    }
}
