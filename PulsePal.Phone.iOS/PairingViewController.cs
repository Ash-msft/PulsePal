using System.Net.Http;
using System.Text.Json;
using Foundation;
using PulsePal.Connected;
using UIKit;

namespace PulsePal.Phone.iOS;

public sealed class PairingViewController : UIViewController
{
    private readonly HealthKitReader health = new();
    private readonly UITextView pairingText = new()
    {
        AccessibilityLabel = "Desktop pairing JSON",
        Font = UIFont.SystemFontOfSize(14),
        AutocorrectionType = UITextAutocorrectionType.No,
        AutocapitalizationType = UITextAutocapitalizationType.None,
        SpellCheckingType = UITextSpellCheckingType.No,
        SmartQuotesType = UITextSmartQuotesType.No,
        SmartDashesType = UITextSmartDashesType.No,
        BackgroundColor = UIColor.SecondarySystemBackground
    };
    private readonly UISwitch heartRate = new();
    private readonly UISwitch bloodPressure = new();
    private readonly UILabel heartRateStatus = Label("Heart rate: not selected.");
    private readonly UILabel bloodPressureStatus = Label("Blood pressure: not selected.");
    private readonly UILabel pendingDetails = Label("");
    private readonly UILabel status = Label("Not paired. Paste fresh pairing JSON from your desktop.");
    private readonly UIButton paste = Button("Paste pairing JSON");
    private readonly UIButton pair = Button("Pair / request desktop approval");
    private readonly UIButton poll = Button("Check approval");
    private readonly UIButton authorize = Button("Authorize selected categories");
    private readonly UIButton sync = Button("Sync now");
    private readonly UIButton disconnect = Button("Disconnect");
    private readonly UIButton cancel = Button("Cancel current operation");
    private PinnedBridgeClient? client;
    private CancellationTokenSource? activeOperation;
    private bool pendingApproval;
    private bool disposed;
    private bool backgrounded;
    private int sessionGeneration;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemBackground;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false };
        var stack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 14,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        View.AddSubview(scroll);
        scroll.AddSubview(stack);
        var safe = View.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.TopAnchor.ConstraintEqualTo(safe.TopAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor),
            stack.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor, 20),
            stack.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor, -20),
            stack.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor, 20),
            stack.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor, -20),
            stack.WidthAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.WidthAnchor, 1, -40),
            pairingText.HeightAnchor.ConstraintEqualTo(150)
        });
        stack.AddArrangedSubview(Label("PulsePal Phone", 26));
        stack.AddArrangedSubview(Label(
            "Read-only HealthKit → your approved desktop. Nothing syncs automatically or in the background."));
        stack.AddArrangedSubview(pairingText);
        stack.AddArrangedSubview(paste);
        stack.AddArrangedSubview(pair);
        stack.AddArrangedSubview(pendingDetails);
        stack.AddArrangedSubview(poll);
        stack.AddArrangedSubview(Category("Heart rate • last 24 hours • bpm", heartRate));
        stack.AddArrangedSubview(heartRateStatus);
        stack.AddArrangedSubview(Category("Blood pressure • last 7 days • mmHg", bloodPressure));
        stack.AddArrangedSubview(bloodPressureStatus);
        stack.AddArrangedSubview(Label(
            "Up to 128 latest records per category. Blood pressure requires a systolic/diastolic HealthKit correlation. Stress is unsupported; no stress values are provided or inferred."));
        stack.AddArrangedSubview(authorize);
        stack.AddArrangedSubview(sync);
        stack.AddArrangedSubview(Label(
            "An authorization sheet completing does not confirm read access. Empty results mean no readable data; read permission cannot be verified. Manage access in Health or Settings."));
        stack.AddArrangedSubview(Label(
            "Pairing and health data are session-only. Backgrounding or disconnecting clears local credentials and category results. Pair again after returning; revoke old sessions on the desktop."));
        stack.AddArrangedSubview(status);
        stack.AddArrangedSubview(cancel);
        stack.AddArrangedSubview(disconnect);

        paste.TouchUpInside += (_, _) =>
        {
            pairingText.Text = UIPasteboard.General.String ?? "";
            SetStatus("Pairing text pasted. Request pairing, then approve this phone on the desktop.");
        };
        pair.TouchUpInside += async (_, _) => await RunAsync(RequestPairingAsync);
        poll.TouchUpInside += async (_, _) => await RunAsync(CheckApprovalAsync);
        authorize.TouchUpInside += async (_, _) => await RunAsync(AuthorizeAsync);
        sync.TouchUpInside += async (_, _) => await RunAsync(SyncAsync);
        disconnect.TouchUpInside += async (_, _) => await RunAsync(DisconnectAsync);
        cancel.TouchUpInside += (_, _) => activeOperation?.Cancel();
        heartRate.ValueChanged += (_, _) => UpdateControls();
        bloodPressure.ValueChanged += (_, _) => UpdateControls();
        UpdateControls();
    }

    private async Task RequestPairingAsync(CancellationToken ct)
    {
        var text = pairingText.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 8192)
        {
            SetStatus("Paste a complete, fresh desktop pairing JSON (maximum 8192 characters).");
            return;
        }
        var requestedClient = new PinnedBridgeClient(text);
        client = requestedClient;
        pairingText.Text = "";
        var established = false;
        try
        {
            // The user-assigned device name can exceed the shared protocol's limit.
            var deviceName = new string(UIDevice.CurrentDevice.Name.Where(c => !char.IsControl(c)).Take(80).ToArray());
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = "PulsePal iOS";
            var pending = await requestedClient.RequestPairingAsync(deviceName, ct);
            CheckForeground(ct);
            pendingDetails.Text = $"Request GUID: {pending.RequestId:D}\n" +
                $"Expires (UTC): {pending.ExpiresAt.UtcDateTime:u}\n" +
                "Compare this entire request GUID with the desktop before approving. " +
                "Approve only if they match, then tap Check approval.";
            pendingApproval = true;
            established = true;
            SetStatus("Request sent. Compare the request GUID shown above with the desktop.");
        }
        finally
        {
            if (!established)
            {
                requestedClient.Dispose();
                if (ReferenceEquals(client, requestedClient))
                    ForgetClient();
            }
        }
    }

    private async Task CheckApprovalAsync(CancellationToken ct)
    {
        var state = await client!.PollApprovalAsync(ct);
        CheckForeground(ct);
        SetStatus(state switch
        {
            PairingState.Approved => "Paired. Choose categories, authorize access, then tap Sync now.",
            PairingState.Pending => "Still awaiting explicit desktop approval.",
            PairingState.Rejected => "Desktop rejected pairing. Paste fresh pairing JSON to try again.",
            PairingState.Expired => "Pairing expired. Paste fresh pairing JSON to try again.",
            _ => "Unrecognized pairing state. Disconnect and try again."
        });
        pendingApproval = state == PairingState.Pending;
        if (state is PairingState.Rejected or PairingState.Expired)
            ForgetClient();
    }

    private async Task AuthorizeAsync(CancellationToken ct)
    {
        if (!health.IsSupported)
        {
            SetStatus("HealthKit is unsupported on this device. No health records can be read.");
            return;
        }
        await health.AuthorizeAsync(heartRate.On, bloodPressure.On, ct);
        CheckForeground(ct);
        SetStatus("Authorization request completed; read permission is not verifiable. Tap Sync now to read selected categories.");
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        var records = new List<HealthRecord>();
        var categories = new List<CategoryReadStatus>();
        var results = new List<(UILabel Label, string ReadStatus)>();
        SyncRecordsRequest? batch = null;
        var uploaded = false;
        heartRateStatus.Text = heartRate.On ? "Heart rate: reading…" : "Heart rate: not selected.";
        bloodPressureStatus.Text = bloodPressure.On ? "Blood pressure: reading…" : "Blood pressure: not selected.";
        try
        {
            if (heartRate.On)
                await ReadCategoryAsync(HealthMetric.HeartRate, heartRateStatus);
            if (bloodPressure.On)
                await ReadCategoryAsync(HealthMetric.BloodPressure, bloodPressureStatus);
            CheckForeground(ct);
            if (categories.Count == 0)
            {
                SetStatus("No categories could be read. Nothing sent. Authorize selected categories and retry; no permission decision is inferred.");
                return;
            }
            batch = new(records.ToArray(), categories.ToArray());
            records.Clear();
            var response = await client!.SyncAsync(batch, ct);
            CheckForeground(ct);
            uploaded = true;
            SetStatus($"Sync completed: {response.Accepted} accepted, {response.Retained} retained. See each category's result below its switch.");
        }
        finally
        {
            records.Clear();
            if (batch is not null)
                Array.Clear(batch.Records);
            if (!disposed && !backgrounded && !ct.IsCancellationRequested)
                foreach (var result in results)
                    result.Label.Text = result.ReadStatus + (uploaded
                        ? " Desktop acknowledged this category."
                        : " Upload not confirmed; retry is safe for the same samples.");
        }

        async Task ReadCategoryAsync(HealthMetric metric, UILabel label)
        {
            SyncRecordsRequest? read = null;
            var name = metric == HealthMetric.HeartRate ? "Heart rate" : "Blood pressure";
            try
            {
                read = await health.ReadAsync(metric == HealthMetric.HeartRate,
                    metric == HealthMetric.BloodPressure, ct);
                CheckForeground(ct);
                records.AddRange(read.Records);
                categories.AddRange(read.Categories);
                var availability = read.Categories.Single().Availability;
                var message = $"{name}: " + (availability switch
                {
                    ReadAvailability.Available => $"{read.Records.Length} readable records.",
                    ReadAvailability.Unsupported => "HealthKit unavailable on this device.",
                    _ => "no readable data; permission not verifiable."
                });
                label.Text = message + " Not yet sent.";
                results.Add((label, message));
            }
            catch (HealthKitReadException)
            {
                CheckForeground(ct);
                label.Text = $"{name}: HealthKit query failed; not sent. Authorize and retry. Read permission cannot be inferred.";
            }
            finally
            {
                if (read is not null)
                    Array.Clear(read.Records);
            }
        }
    }

    private async Task DisconnectAsync(CancellationToken ct)
    {
        // Always forget local secrets, including when the desktop cannot be reached.
        try
        {
            await client!.DisconnectAsync(ct);
            CheckForeground(ct);
            SetStatus("Disconnected. In-memory pairing credentials cleared.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Intentional sanitized UI boundary; never expose transport or credential details.
            SetStatus("Local credentials cleared. Desktop revocation was not confirmed; revoke this phone on the desktop.");
        }
        finally
        {
            ForgetClient();
            if (!disposed)
                ClearSessionDisplay();
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (disposed || backgrounded || activeOperation is not null)
            return;
        var generation = sessionGeneration;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        activeOperation = operation;
        View?.EndEditing(true);
        UpdateControls();
        SetStatus("Working…");
        try
        {
            RequireForeground();
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!disposed && generation == sessionGeneration)
            {
                status.Text = "Operation cancelled or timed out. Upload may already have reached the desktop; nothing resumes automatically. If disconnecting, remote revocation is unconfirmed; revoke on the desktop.";
                heartRateStatus.Text = bloodPressureStatus.Text = "Result cleared after cancellation. Retry in the foreground.";
            }
        }
        catch (HealthKitReadException)
        {
            SetStatus("HealthKit could not complete the request. Check Health access and retry; no permission decision is inferred.");
        }
        catch (Exception error) when (error is ArgumentException or JsonException or FormatException)
        {
            SetStatus("Invalid pairing data or request. Use fresh pairing JSON from the desktop.");
        }
        catch (HttpRequestException)
        {
            SetStatus("Secure connection failed. Check desktop availability, local-network access and pairing expiry. Certificate validation is never bypassed.");
        }
        catch (Exception)
        {
            // Never display native/server exception text: it may contain identifiers or credentials.
            SetStatus("The operation failed. Check the desktop and permissions, or disconnect and pair again.");
        }
        finally
        {
            activeOperation = null;
            if (!disposed)
                UpdateControls();
        }
    }

    public void CancelForeground()
    {
        if (disposed)
            return;
        backgrounded = true;
        sessionGeneration++;
        activeOperation?.Cancel();
        ForgetClient();
        ClearSessionDisplay();
        if (!disposed)
        {
            status.Text = "Backgrounded: local pairing and results cleared. Pair again to sync. Remote revocation was not attempted; revoke the old session on the desktop.";
            UpdateControls();
        }
    }

    internal void ResumeForeground()
    {
        if (disposed)
            return;
        backgrounded = false;
        UpdateControls();
    }

    private void ClearSessionDisplay()
    {
        pairingText.Text = "";
        pendingDetails.Text = "";
        heartRate.On = bloodPressure.On = false;
        heartRateStatus.Text = "Heart rate: not selected.";
        bloodPressureStatus.Text = "Blood pressure: not selected.";
    }

    private static void CheckForeground(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireForeground();
    }

    private static void RequireForeground()
    {
        if (UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
            throw new OperationCanceledException();
    }

    private void ForgetClient()
    {
        client?.Dispose();
        client = null;
        pendingApproval = false;
    }

    private void SetStatus(string text)
    {
        if (!disposed && !backgrounded && activeOperation?.IsCancellationRequested != true)
            status.Text = text;
    }

    private void UpdateControls()
    {
        var idle = activeOperation is null && !backgrounded;
        var selected = heartRate.On || bloodPressure.On;
        pendingDetails.Hidden = !pendingApproval;
        if (!pendingApproval)
            pendingDetails.Text = "";
        paste.Enabled = pair.Enabled = idle && client is null;
        pairingText.Editable = idle && client is null;
        poll.Enabled = idle && pendingApproval;
        authorize.Enabled = idle && selected;
        sync.Enabled = idle && selected && client?.IsPaired == true;
        disconnect.Enabled = idle && client is not null;
        cancel.Enabled = activeOperation is not null && !backgrounded;
        heartRate.Enabled = bloodPressure.Enabled = idle;
    }

    private static UILabel Label(string text, float size = 16) => new()
    {
        Text = text,
        Lines = 0,
        Font = UIFont.SystemFontOfSize(size) ?? throw new InvalidOperationException("System font is unavailable."),
        TextColor = UIColor.Label
    };

    private static UIButton Button(string title)
    {
        var button = new UIButton(UIButtonType.System);
        button.SetTitle(title, UIControlState.Normal);
        button.TitleLabel!.Lines = 0;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        return button;
    }

    private static UIStackView Category(string title, UISwitch toggle)
    {
        toggle.AccessibilityLabel = title;
        return new UIStackView(new UIView[] { Label(title), toggle })
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            activeOperation?.Cancel();
            ForgetClient();
            ClearSessionDisplay();
            health.Dispose();
        }
        base.Dispose(disposing);
    }
}
