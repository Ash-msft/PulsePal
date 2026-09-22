using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsePal.Bridge;
using PulsePal.Connected;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace PulsePal.App;

public sealed class ConnectedHealthWindow : Window
{
    private const string ClipboardOwnerProperty = "PulsePal.ConnectedHealth.ClipboardOwner";
    private readonly BridgeHost _host;
    private readonly Func<Task> _closeAsync;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ComboBox _interfaces = new()
    {
        Header = "Local private IPv4 interface (explicit selection required)",
        PlaceholderText = "Select a trusted local network",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _port = new() { Header = "TCP port (1024–65535)", Text = "47831", MaxLength = 5 };
    private readonly TextBox _pairing = new()
    {
        Header = "One-time pairing payload — sensitive",
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 150,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ListView _pending = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = false,
        MaxHeight = 260,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _status = Body("OFF — listener is not running.");
    private readonly TextBlock _message = Body();
    private readonly TextBlock _networkMessage = Body();
    private readonly TextBlock _pairingExpiry = Body("No pairing payload while OFF.");
    private readonly TextBlock _pairedDevice = Body("No paired phone.");
    private readonly TextBlock _pendingEmpty = Body("No pending requests.");
    private readonly TextBlock _heartRate = Body("Heart rate: Not provided");
    private readonly TextBlock _bloodPressure = Body("Blood pressure: Not provided");
    private readonly TextBlock _recordsEmpty = Body("No desktop records.");
    private readonly StackPanel _recordPanel = new() { Spacing = 12 };
    private readonly List<TextBlock> _recordViews = new();
    private readonly Dictionary<Guid, ListViewItem> _pendingViews = new();
    private readonly Button _rescan = Button("Rescan interfaces");
    private readonly Button _start = Button("Start listener");
    private readonly Button _stop = Button("Stop and clear session");
    private readonly Button _copy = Button("Copy pairing payload");
    private readonly Button _refresh = Button("Refresh pairing challenge");
    private readonly Button _approve = Button("Approve selected request");
    private readonly Button _reject = Button("Reject selected request");
    private readonly Button _revoke = Button("Revoke and disconnect");
    private readonly Button _delete = Button("Delete desktop records");
    private readonly Button _close = Button("Close connected health");
    private Guid? _selectedRequestId;
    private string? _clipboardOwner;
    private bool _updatingPending;
    private bool _running;
    private bool _busy;
    private bool _preparingShutdown;
    private bool _closeRequested;
    private bool _allowNativeClose;
    private bool _snapshotFailed;
    private Task _operation = Task.CompletedTask;
    private Task? _preparation;

    public bool IsClosed { get; private set; }
    internal bool IsReadyForSmoke => Content is FrameworkElement { IsLoaded: true };
    internal bool DisplaysOffForSmoke => _status.Text.StartsWith("OFF", StringComparison.Ordinal) && _pairing.Text.Length == 0;
    internal bool DisplaysPairingForSmoke => _running && _pairing.Text.Length > 0;

    public ConnectedHealthWindow(BridgeHost host, Func<Task> closeAsync)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _closeAsync = closeAsync ?? throw new ArgumentNullException(nameof(closeAsync));
        Title = "PulsePal · Connected health";

        var page = new StackPanel { Spacing = 20, Margin = new Thickness(24), MaxWidth = 1040 };
        page.Children.Add(Heading("Connected health", AutomationHeadingLevel.Level1));
        page.Children.Add(Section("NOT MEDICAL ADVICE · NOT LIVE MONITORING",
            Body("Phone health records are historical measurements, not a live feed, diagnosis, or treatment recommendation. Do not use this window for emergency monitoring."),
            Body("Connected records remain separate from the synthetic demo engine. No stress, readiness, or other demo score is derived from these records.")));
        page.Children.Add(Section("Listener", _status, _message,
            Body("OFF by default. Start only on a trusted private network shared with your phone. Only active local RFC1918 IPv4 interfaces are offered; no wildcard or loopback binding."),
            _interfaces, _port, _networkMessage, Actions(_rescan, _start, _stop)));
        page.Children.Add(Section("Pair a phone", _pairedDevice,
            Body("The payload is sensitive: copy and paste it only into the intended phone app. It is a one-time, expiring challenge. Refresh it if expired; never share it in chat or save it."),
            _pairing, _pairingExpiry, Actions(_copy, _refresh, _revoke),
            Body("The Copy button disables Windows clipboard history and roaming for this payload. Manual keyboard/context-menu copying does not have these protections. PulsePal does not flush clipboard storage. Clearing is best effort and only targets the Copy button's own payload.")));
        page.Children.Add(Section("Requests awaiting your approval",
            Body("Select a request explicitly. The requested name is NOT a trusted identity. Compare the request ID and expiry with your intended phone before approving. Nothing is selected automatically."),
            _pendingEmpty, _pending, Actions(_approve, _reject)));
        page.Children.Add(Section("Category read status", _heartRate, _bloodPressure,
            Body("Status describes the latest phone category response. Previously received records below may remain even when that response returns no data."),
            Body("Available, NoData, Denied, Unsupported, and PermissionNotVerifiable are distinct reported states. HealthKit read permission cannot be inferred as denied from no data. Missing categories are Not provided, never zero."),
            Body("Stress: Unsupported / not provided — not derived from records.")));
        page.Children.Add(Section("Individual desktop records",
            Body("Each measurement keeps its own source and original measurement interval. Desktop receipt time is separate. No aggregates or diagnostic thresholds are applied."),
            Body("Presentation age policy: measured more than 15 minutes ago is “Older record (not live)”. Recent records are still records, not live. A future measurement timestamp indicates the phone time is ahead."),
            _recordsEmpty, _recordPanel, _delete,
            Body("Delete removes desktop records only; it does not change the phone. A new sync may restore deleted records.")));
        page.Children.Add(_close);

        var root = new Grid();
        if (Application.Current.Resources.TryGetValue("ApplicationPageBackgroundThemeBrush", out var background)
            && background is Brush brush)
            root.Background = brush;
        root.Children.Add(new ScrollViewer
        {
            Content = page,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = root;
        AppWindow.Resize(new SizeInt32(920, 850));
        AutomationProperties.SetName(_pending, "Pending phone requests; explicitly select one to approve or reject");
        AutomationProperties.SetName(_interfaces, "Active local private IPv4 network interface");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);

        _interfaces.SelectionChanged += (_, _) => UpdateActions();
        _port.TextChanged += (_, _) => UpdateActions();
        _pending.SelectionChanged += OnPendingSelectionChanged;
        _rescan.Click += async (_, _) => await RunOperationAsync(() =>
        {
            if (!_host.IsRunning)
                ScanInterfaces();
            return Task.CompletedTask;
        });
        _start.Click += async (_, _) => await RunOperationAsync(StartListenerAsync);
        _stop.Click += async (_, _) => await RunOperationAsync(StopListenerAsync);
        _copy.Click += async (_, _) => await RunOperationAsync(CopyPairingAsync);
        _refresh.Click += async (_, _) => await RunOperationAsync(() =>
        {
            ClearOwnedClipboard();
            _pairing.Text = string.Empty;
            ClearPending();
            _host.RefreshPairing();
            SetText(_message, "Pairing challenge refreshed. Use the new payload.");
            return Task.CompletedTask;
        });
        _revoke.Click += async (_, _) => await RunOperationAsync(() =>
        {
            ClearOwnedClipboard();
            ClearSensitiveUi();
            _host.Revoke();
            SetText(_message, "Session revoked. Phone disconnected; tokens and desktop data cleared.");
            return Task.CompletedTask;
        });
        _delete.Click += async (_, _) => await RunOperationAsync(() =>
        {
            _host.DeleteRecords();
            ClearRecords();
            SetText(_message, "Desktop records deleted. Phone data is unchanged; a new sync may restore records.");
            return Task.CompletedTask;
        });
        _approve.Click += async (_, _) => await DecideSelectedRequestAsync(approve: true);
        _reject.Click += async (_, _) => await DecideSelectedRequestAsync(approve: false);
        _close.Click += async (_, _) => await RequestCloseAsync();
        _timer.Tick += OnPoll;
        AppWindow.Closing += OnNativeClosing;
        Closed += OnClosed;

        ScanInterfaces();
        UpdateActions();
        _timer.Start();
    }

    // Called on the window dispatcher before the controller stops or disposes its host.
    internal Task PrepareForShutdownAsync()
    {
        if (_preparation is not null)
            return _preparation;
        _preparingShutdown = true;
        _timer.Stop();
        UpdateActions();
        _preparation = PrepareCoreAsync();
        return _preparation;
    }

    private async Task PrepareCoreAsync()
    {
        try
        {
            await _operation;
        }
        catch
        {
            // Shutdown must still remove sensitive presentation after a failed operation.
        }
        finally
        {
            ClearOwnedClipboard();
            ClearSensitiveUi();
            SetText(_message, string.Empty);
            SetText(_status, "Closing — connected health is unavailable.");
        }
    }

    public void CloseAfterShutdown()
    {
        if (IsClosed)
            return;
        _preparingShutdown = true;
        _allowNativeClose = true;
        _timer.Stop();
        UpdateActions();
        ClearOwnedClipboard();
        ClearSensitiveUi();
        Close();
    }

    private async void OnNativeClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowNativeClose)
            return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    private async Task RequestCloseAsync()
    {
        if (IsClosed || _closeRequested)
            return;
        _closeRequested = true;
        UpdateActions();
        try
        {
            await _closeAsync();
        }
        catch
        {
            if (!IsClosed)
                SetText(_message, "Unable to finish closing connected health. Please try closing again.");
        }
        finally
        {
            _closeRequested = false;
            if (!IsClosed)
                UpdateActions();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _preparingShutdown = true;
        _timer.Stop();
        _timer.Tick -= OnPoll;
        AppWindow.Closing -= OnNativeClosing;
        Closed -= OnClosed;
        ClearOwnedClipboard();
        ClearSensitiveUi();
    }

    private async Task RunOperationAsync(Func<Task> action)
    {
        if (IsClosed || _preparingShutdown || _closeRequested || _busy)
            return;

        // Publish the completion task before invoking an action that could yield to native close.
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _busy = true;
        UpdateActions();
        try
        {
            await action();
        }
        catch
        {
            if (!IsClosed && !_preparingShutdown)
                SetText(_message, "The operation could not be completed. Check the listener and try again.");
        }
        finally
        {
            _busy = false;
            try
            {
                if (!IsClosed && !_preparingShutdown && !_closeRequested)
                    RefreshSnapshots();
                if (!IsClosed)
                    UpdateActions();
            }
            finally
            {
                completion.TrySetResult(true);
            }
        }
    }

    private async Task StartListenerAsync()
    {
        if (_host.IsRunning)
            return;
        if (_interfaces.SelectedItem is not NetworkChoice choice || !TryGetPort(out var port))
        {
            SetText(_message, "Select an active private IPv4 interface and enter a port from 1024 to 65535.");
            return;
        }

        // Revalidate the exact interface/address, not just an address from an earlier scan.
        if (!ReadInterfaces().Any(candidate => candidate.InterfaceId == choice.InterfaceId
            && candidate.Address.Equals(choice.Address)))
        {
            ScanInterfaces();
            SetText(_message, "That interface is no longer active. Select an interface again.");
            return;
        }
        await _host.StartAsync(choice.Address, port);
        SetText(_message, "Listener started. Pairing and each phone approval remain explicit.");
    }

    private async Task StopListenerAsync()
    {
        ClearOwnedClipboard();
        ClearSensitiveUi();
        try
        {
            await _host.StopAsync();
        }
        finally
        {
            try
            {
                _host.Revoke();
            }
            finally
            {
                ClearOwnedClipboard();
                ClearSensitiveUi();
            }
        }
        SetText(_message, "Listener stopped. Session tokens and desktop data cleared.");
    }

    private Task CopyPairingAsync()
    {
        var current = _host.PairingString;
        if (!_host.IsRunning || string.IsNullOrEmpty(current)
            || !string.Equals(current, _pairing.Text, StringComparison.Ordinal)
            || PairingExpired(current, DateTimeOffset.UtcNow))
        {
            SetText(_message, "The pairing payload changed or expired. Refresh before copying.");
            return Task.CompletedTask;
        }

        var owner = Guid.NewGuid().ToString("N");
        var package = new DataPackage();
        package.SetText(current);
        package.Properties[ClipboardOwnerProperty] = owner;
        if (Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false
        }))
        {
            _clipboardOwner = owner;
            SetText(_message, "Sensitive payload copied. Paste only into your intended phone app before expiry.");
        }
        else
        {
            SetText(_message, "Clipboard is unavailable. Try the Copy button again.");
        }
        return Task.CompletedTask;
    }

    private void ClearOwnedClipboard()
    {
        if (_clipboardOwner is null)
            return;
        try
        {
            // Synchronous ownership inspection avoids clearing a newer clipboard after an await.
            var content = Clipboard.GetContent();
            if (content.Properties.TryGetValue(ClipboardOwnerProperty, out var owner)
                && owner is string value && string.Equals(value, _clipboardOwner, StringComparison.Ordinal))
                Clipboard.Clear();
            _clipboardOwner = null;
        }
        catch
        {
            // Clipboard can be locked by another process. Cleanup is deliberately best effort.
        }
    }

    private Task DecideSelectedRequestAsync(bool approve)
    {
        var selectedId = _selectedRequestId;
        var selected = (_pending.SelectedItem as ListViewItem)?.Tag as PendingDevice;
        return RunOperationAsync(() =>
        {
            var current = selectedId is { } id
                ? _host.PendingDevices.FirstOrDefault(request => request.RequestId == id)
                : null;
            if (!_host.IsRunning || current is null || selected is null
                || current != selected || current.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                ClearPendingSelection();
                SetText(_message, "That exact request is no longer pending or has expired. Select a current request.");
                return Task.CompletedTask;
            }
            if (approve)
                _host.Approve(current.RequestId);
            else
                _host.Reject(current.RequestId);
            ClearPendingSelection();
            SetText(_message, approve ? "Approval submitted for the selected request." : "Selected request rejected.");
            return Task.CompletedTask;
        });
    }

    private void OnPoll(object? sender, object args)
    {
        if (!IsClosed && !_busy && !_preparingShutdown && !_closeRequested)
            RefreshSnapshots();
    }

    private void RefreshSnapshots()
    {
        try
        {
            var running = _host.IsRunning;
            var pairing = _host.PairingString;
            var pending = _host.PendingDevices.ToArray();
            var records = _host.Records.ToArray();
            var categories = _host.Categories.ToArray();
            var deviceName = _host.PairedDeviceName;
            var now = DateTimeOffset.UtcNow;
            _running = running;
            _snapshotFailed = false;
            SetText(_status, running ? "ON — private-network listener running. Records are not live."
                : "OFF — listener is not running.");

            if (!running)
            {
                ClearOwnedClipboard();
                ClearSensitiveUi();
                return;
            }

            var visiblePairing = !string.IsNullOrEmpty(pairing) && !PairingExpired(pairing, now)
                ? pairing : string.Empty;
            if (!string.Equals(_pairing.Text, visiblePairing, StringComparison.Ordinal))
            {
                ClearOwnedClipboard();
                _pairing.Text = visiblePairing;
            }
            SetText(_pairingExpiry, PairingExpiryText(pairing, now));
            SetText(_pairedDevice, string.IsNullOrEmpty(deviceName) ? "No paired phone."
                : $"Paired phone's reported name (not trusted identity): {DisplayName(deviceName)}");
            RefreshPending(pending.Where(request => request.ExpiresAt > now).ToArray());
            SetText(_heartRate, CategoryText("Heart rate", HealthMetric.HeartRate, categories));
            SetText(_bloodPressure, CategoryText("Blood pressure", HealthMetric.BloodPressure, categories));
            RefreshRecords(records, now);
        }
        catch
        {
            _snapshotFailed = true;
            ClearOwnedClipboard();
            ClearSensitiveUi();
            SetText(_status, "Status unavailable — listener may still be running.");
            SetText(_message, "Unable to read connected health status. Stop the listener or close this window.");
        }
        finally
        {
            UpdateActions();
        }
    }

    private void RefreshPending(PendingDevice[] requests)
    {
        var retainedId = _selectedRequestId;
        _updatingPending = true;
        try
        {
            if (!requests.Any(request => request.RequestId == retainedId))
            {
                retainedId = null;
                _pending.SelectedIndex = -1;
            }
            var ids = requests.Select(request => request.RequestId).ToHashSet();
            foreach (var id in _pendingViews.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                _pending.Items.Remove(_pendingViews[id]);
                _pendingViews.Remove(id);
            }
            foreach (var request in requests)
            {
                if (!_pendingViews.TryGetValue(request.RequestId, out var item))
                {
                    var label = Body();
                    label.IsTextSelectionEnabled = false;
                    item = new ListViewItem { Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    _pendingViews.Add(request.RequestId, item);
                    _pending.Items.Add(item);
                }
                item.Tag = request;
                var text = $"Requested name (NOT trusted identity): {DisplayName(request.DeviceName)}\n"
                    + $"Request ID: {request.RequestId:D}\nExpires: {Iso(request.ExpiresAt)}";
                SetText((TextBlock)item.Content, text);
                AutomationProperties.SetName(item, text);
            }
            _selectedRequestId = retainedId;
            if (retainedId is { } selectedId && _pendingViews.TryGetValue(selectedId, out var selected))
                _pending.SelectedItem = selected;
            else
                _pending.SelectedIndex = -1;
            SetText(_pendingEmpty, requests.Length == 0 ? "No pending requests." : "Select the exact request you intend to review.");
        }
        finally
        {
            _updatingPending = false;
        }
    }

    private void OnPendingSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingPending)
            return;
        _selectedRequestId = ((_pending.SelectedItem as ListViewItem)?.Tag as PendingDevice)?.RequestId;
        UpdateActions();
    }

    private void ClearPendingSelection()
    {
        _selectedRequestId = null;
        _updatingPending = true;
        try { _pending.SelectedIndex = -1; }
        finally { _updatingPending = false; }
        UpdateActions();
    }

    private void ClearPending()
    {
        ClearPendingSelection();
        _pending.Items.Clear();
        _pendingViews.Clear();
        SetText(_pendingEmpty, "No pending requests.");
    }

    private void RefreshRecords(ReceivedHealthRecord[] records, DateTimeOffset now)
    {
        while (_recordViews.Count > records.Length)
        {
            var index = _recordViews.Count - 1;
            _recordPanel.Children.RemoveAt(index);
            _recordViews.RemoveAt(index);
        }
        for (var index = 0; index < records.Length; index++)
        {
            if (index == _recordViews.Count)
            {
                var view = Body();
                _recordViews.Add(view);
                _recordPanel.Children.Add(view);
            }
            SetText(_recordViews[index], RecordText(records[index], now));
        }
        SetText(_recordsEmpty, records.Length == 0 ? "No desktop records." : $"{records.Length} individual desktop records.");
    }

    private void ClearRecords()
    {
        foreach (var view in _recordViews)
            view.Text = string.Empty;
        _recordPanel.Children.Clear();
        _recordViews.Clear();
        SetText(_recordsEmpty, "No desktop records.");
    }

    private void ClearSensitiveUi()
    {
        _pairing.Text = string.Empty;
        SetText(_pairingExpiry, "No pairing payload displayed.");
        SetText(_pairedDevice, "No paired phone displayed.");
        ClearPending();
        ClearRecords();
        SetText(_heartRate, "Heart rate: Not provided");
        SetText(_bloodPressure, "Blood pressure: Not provided");
    }

    private void UpdateActions()
    {
        var enabled = !IsClosed && !_busy && !_preparingShutdown && !_closeRequested;
        var off = enabled && !_running && !_snapshotFailed;
        var on = enabled && _running && !_snapshotFailed;
        _interfaces.IsEnabled = off;
        _port.IsEnabled = off;
        _rescan.IsEnabled = off;
        _start.IsEnabled = off && _interfaces.SelectedItem is NetworkChoice && TryGetPort(out _);
        _stop.IsEnabled = enabled && (_running || _snapshotFailed);
        _copy.IsEnabled = on && !string.IsNullOrEmpty(_pairing.Text);
        _refresh.IsEnabled = on;
        _revoke.IsEnabled = enabled && (_running || _snapshotFailed);
        _delete.IsEnabled = on;
        _pending.IsEnabled = on;
        var selected = (_pending.SelectedItem as ListViewItem)?.Tag as PendingDevice;
        var canDecide = on && selected is not null && selected.RequestId == _selectedRequestId
            && selected.ExpiresAt > DateTimeOffset.UtcNow;
        _approve.IsEnabled = canDecide;
        _reject.IsEnabled = canDecide;
        _pairing.IsEnabled = enabled;
        _close.IsEnabled = !IsClosed && !_preparingShutdown && !_closeRequested;
    }

    private bool TryGetPort(out int port) =>
        int.TryParse(_port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is >= 1024 and <= 65535;

    private void ScanInterfaces()
    {
        _interfaces.SelectedIndex = -1;
        _interfaces.Items.Clear();
        try
        {
            foreach (var choice in ReadInterfaces())
                _interfaces.Items.Add(choice);
            SetText(_networkMessage, _interfaces.Items.Count == 0
                ? "No active RFC1918 IPv4 interface found. Connect to a trusted private network, then rescan."
                : "No interface selected automatically. Select the address reachable from your phone.");
        }
        catch
        {
            _interfaces.Items.Clear();
            SetText(_networkMessage, "Unable to enumerate network interfaces. Check your network and rescan.");
        }
        _interfaces.SelectedIndex = -1;
    }

    private static NetworkChoice[] ReadInterfaces()
    {
        var choices = new List<NetworkChoice>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up
                || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (IsPrivateIpv4(address))
                    choices.Add(new NetworkChoice(network.Id, network.Name, address));
            }
        }
        return choices.OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Address.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var octets = address.GetAddressBytes();
        return octets[0] == 10 || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168);
    }

    private static string CategoryText(string label, HealthMetric metric, CategoryReadStatus[] categories)
    {
        var category = categories.FirstOrDefault(item => item.Metric == metric);
        var state = category?.Availability switch
        {
            null => "Not provided",
            ReadAvailability.Available => "Available — category reported readable; records are not live",
            ReadAvailability.NoData => "NoData — no data returned; does not imply permission denied",
            ReadAvailability.Denied => "Denied — explicitly reported by the phone",
            ReadAvailability.Unsupported => "Unsupported — not supported by this phone/source",
            ReadAvailability.PermissionNotVerifiable => "PermissionNotVerifiable — read permission cannot be verified",
            _ => "Unrecognized category state"
        };
        return $"{label}: {state}";
    }

    private static string RecordText(ReceivedHealthRecord received, DateTimeOffset now)
    {
        var record = received.Record;
        var measurement = record.Metric switch
        {
            HealthMetric.HeartRate => $"Heart rate: {Number(record.Value)} bpm",
            HealthMetric.BloodPressure => $"Blood pressure: systolic {Number(record.Value)} mmHg; "
                + $"diastolic {(record.SecondaryValue is { } diastolic ? Number(diastolic) + " mmHg" : "Not provided")}",
            _ => "Unsupported measurement category"
        };
        var age = now - record.MeasuredAt;
        var ageText = age < TimeSpan.Zero
            ? "Future timestamp — phone time ahead of desktop; record, not live"
            : age > TimeSpan.FromMinutes(15)
                ? $"Older record (not live) — {Math.Floor(age.TotalMinutes):0} min since measurement"
                : $"Recent record, not live — {Math.Floor(age.TotalMinutes):0} min since measurement";
        return $"{measurement}\nSource app: {DisplayName(record.SourceApp)}\n"
            + $"Source device: {DisplayName(record.SourceDevice)}\n"
            + $"MeasuredAt (original offset): {Iso(record.MeasuredAt)}\n"
            + $"EndAt (original offset): {(record.EndAt is { } end ? Iso(end) : "Not provided")}\n"
            + $"ReceivedAt (desktop receipt): {Iso(received.ReceivedAt)}\n{ageText}\n"
            + $"Record ID: {DisplayName(record.RecordId)} · Version: {record.Version}";
    }

    private static string Number(double value) => double.IsFinite(value)
        ? value.ToString("G", CultureInfo.InvariantCulture) : "Not provided";

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string DisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Not provided";
        // Names are untrusted labels. Bound display and neutralize control/bidi formatting characters.
        return new string(value.Take(256).Select(character =>
            char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format
                ? ' ' : character).ToArray()) + (value.Length > 256 ? "…" : string.Empty);
    }

    private static DateTimeOffset? PairingExpiry(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PairingPayload>(payload, ConnectedJson.CreateOptions())?.ExpiresAt;
        }
        catch
        {
            return null;
        }
    }

    private static bool PairingExpired(string payload, DateTimeOffset now) =>
        PairingExpiry(payload) is { } expiry && expiry <= now;

    private static string PairingExpiryText(string? payload, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(payload))
            return "No pairing payload available. Refresh the challenge while the listener is ON.";
        if (PairingExpiry(payload) is not { } expiry)
            return "Expiring one-time challenge. If the phone reports expiry, refresh and copy the new payload.";
        return expiry <= now ? $"Expired at {Iso(expiry)}. Refresh the challenge."
            : $"Challenge expires: {Iso(expiry)}. Paste only into your intended phone app.";
    }

    private static void SetText(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
            block.Text = text;
    }

    private static TextBlock Body(string text = "") => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontSize = 14
    };

    private static TextBlock Heading(string text, AutomationHeadingLevel level = AutomationHeadingLevel.Level2)
    {
        var block = Body(text);
        block.FontSize = level == AutomationHeadingLevel.Level1 ? 30 : 19;
        block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(block, level);
        return block;
    }

    private static Button Button(string label)
    {
        var button = new Button { Content = Body(label), MinHeight = 40 };
        ((TextBlock)button.Content).IsTextSelectionEnabled = false;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static StackPanel Actions(params Button[] buttons)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var button in buttons)
            panel.Children.Add(button);
        return panel;
    }

    private static Border Section(string title, params UIElement[] elements)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Heading(title));
        foreach (var element in elements)
            panel.Children.Add(element);
        return new Border
        {
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Child = panel
        };
    }

    private sealed record NetworkChoice(string InterfaceId, string Name, IPAddress Address)
    {
        public override string ToString() => $"{DisplayName(Name)} — {Address}";
    }
}
