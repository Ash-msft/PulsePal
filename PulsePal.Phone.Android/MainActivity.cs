using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.OS;
using global::Android.Text;
using global::Android.Views;
using global::Android.Widget;
using PulsePal.Connected;

namespace PulsePal.Phone.Android;

[Activity(Name = "org.pulsepal.phone.MainActivity", Label = "PulsePal Phone",
    MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private const int PermissionRequest = 41;
    private readonly List<View> controls = [];
    private NativeHealthReader reader = null!;
    private EditText pairing = null!;
    private CheckBox heartRate = null!;
    private CheckBox bloodPressure = null!;
    private TextView status = null!;
    private TextView availability = null!;
    private TextView pairingRequest = null!;
    private PinnedBridgeClient? client;
    private CancellationTokenSource? operation;
    private bool foreground;
    private bool busy;
    private bool permissionPending;
    private int sessionGeneration;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Secure);
        reader = new NativeHealthReader(this);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical, SaveEnabled = false };
        layout.SetPadding(24, 64, 24, 64);
        layout.AddView(new TextView(this) { Text = "PulsePal Phone", TextSize = 26 });
        layout.AddView(new TextView(this)
        {
            Text = "Android 14+ Health Connect • manual foreground sync only\n" +
                   "Choose categories, grant read access, then pair with your desktop.",
            TextSize = 16
        });
        heartRate = new CheckBox(this) { Text = "Heart rate — last 24 hours (bpm)", SaveEnabled = false };
        bloodPressure = new CheckBox(this) { Text = "Blood pressure — last 7 days (mmHg)", SaveEnabled = false };
        AddControl(layout, heartRate);
        AddControl(layout, bloodPressure);
        heartRate.CheckedChange += (_, _) => RefreshAvailability();
        bloodPressure.CheckedChange += (_, _) => RefreshAvailability();
        availability = new TextView(this) { TextSize = 15 };
        layout.AddView(availability);
        AddButton(layout, "Grant selected health permissions", RequestHealthPermissions);
        AddButton(layout, "Health data policy", () => StartActivity(typeof(PermissionsRationaleActivity)));
        pairing = new EditText(this)
        {
            Hint = "Paste desktop pairing JSON",
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagNoSuggestions,
            SaveEnabled = false,
            ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants
        };
        pairing.SetMinLines(3);
        pairing.SetMaxLines(5);
        pairing.SetFilters([new InputFilterLengthFilter(8192)]);
        AddControl(layout, pairing);
        AddButton(layout, "Pair", () => _ = RunOperationAsync(PairAsync));
        pairingRequest = new TextView(this) { TextSize = 16, SaveEnabled = false };
        layout.AddView(pairingRequest);
        AddButton(layout, "Check desktop approval", () => _ = RunOperationAsync(CheckApprovalAsync));
        AddButton(layout, "Sync now", () => _ = RunOperationAsync(SyncAsync));
        AddButton(layout, "Disconnect", () => _ = RunOperationAsync(DisconnectAsync));
        status = new TextView(this) { Text = "Not paired. No categories selected.", TextSize = 16, SaveEnabled = false };
        layout.AddView(status);
        var scroll = new ScrollView(this) { SaveEnabled = false };
        scroll.AddView(layout);
        SetContentView(scroll);
        RefreshAvailability();
    }

    private void AddControl(LinearLayout layout, View control)
    {
        controls.Add(control);
        layout.AddView(control);
    }

    private void AddButton(LinearLayout layout, string label, Action action)
    {
        var button = new Button(this) { Text = label };
        button.Click += (_, _) => action();
        AddControl(layout, button);
    }

    private HealthMetric[] SelectedMetrics() =>
        new[] { HealthMetric.HeartRate, HealthMetric.BloodPressure }
            .Where(metric => metric == HealthMetric.HeartRate ? heartRate.Checked : bloodPressure.Checked)
            .ToArray();

    private void RefreshAvailability()
    {
        if (availability is null) return;
        if (!reader.IsSupported)
        {
            availability.Text = "Unsupported: health reads require Android 14+ with built-in Health Connect. " +
                                "Android 9–13's separate provider is not supported by this build.";
            return;
        }
        var selected = SelectedMetrics();
        availability.Text = selected.Length == 0 ? "Select the categories you want to share." :
            string.Join("\n", selected.Select(metric =>
                $"{metric}: {(CheckSelfPermission(NativeHealthReader.PermissionFor(metric)) == Permission.Granted ? "Read permission granted" : "Denied / not yet granted")}"));
    }

    private void RequestHealthPermissions()
    {
        if (!foreground || busy || permissionPending) return;
        if (!reader.IsSupported)
        {
            RefreshAvailability();
            return;
        }
        var permissions = SelectedMetrics().Select(NativeHealthReader.PermissionFor)
            .Where(permission => CheckSelfPermission(permission) != Permission.Granted).ToArray();
        if (permissions.Length == 0)
        {
            status.Text = SelectedMetrics().Length == 0 ? "Select a category first." : "Selected read permissions are already granted.";
            return;
        }
        try
        {
            permissionPending = true;
            SetControlsEnabled(false);
            RequestPermissions(permissions, PermissionRequest);
        }
        catch (Exception)
        {
            permissionPending = false;
            SetControlsEnabled(true);
            status.Text = "The system permission screen is unavailable. Check Android Health Connect settings.";
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != PermissionRequest) return;
        permissionPending = false;
        SetControlsEnabled(foreground && !busy);
        RefreshAvailability();
        if (foreground) status.Text = "Permission response received. Denied categories are never read. " +
                      "If access remains denied, review this app in Android Settings > Health Connect.";
    }

    private async Task PairAsync(CancellationToken ct)
    {
        string json = pairing.Text?.Trim() ?? "";
        pairing.Text = "";
        client?.Dispose();
        client = null;
        pairingRequest.Text = "";
        if (json.Length == 0 || json.Length > 8192)
        {
            status.Text = "Paste a valid, unexpired desktop pairing JSON (at most 8 KB).";
            return;
        }
        var next = new PinnedBridgeClient(json);
        client = next;
        try
        {
            var pending = await next.RequestPairingAsync("PulsePal Android", ct);
            CheckForeground(ct);
            pairingRequest.Text = $"Compare this request ID with the desktop before approving:\n{pending.RequestId:D}\n" +
                                  $"Expires: {pending.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
            status.Text = "Request sent. Approve only if the request IDs match, then tap Check desktop approval.";
        }
        catch
        {
            next.Dispose();
            if (ReferenceEquals(client, next)) client = null;
            throw;
        }
    }

    private async Task CheckApprovalAsync(CancellationToken ct)
    {
        if (client is null)
        {
            status.Text = "Pair with the desktop first.";
            return;
        }
        if (client.IsPaired)
        {
            status.Text = "Paired. Sync now sends only your selected categories.";
            return;
        }
        var state = await client.PollApprovalAsync(ct);
        CheckForeground(ct);
        status.Text = state switch
        {
            PairingState.Approved => "Approved. Tap Sync now to share selected measurements.",
            PairingState.Pending => "Still awaiting desktop approval. Tap Check desktop approval again after approving.",
            PairingState.Rejected => "Desktop rejected this request. Paste a new pairing JSON to retry.",
            _ => "Pairing expired. Obtain a fresh desktop pairing JSON."
        };
        if (state != PairingState.Pending) pairingRequest.Text = "";
        if (state is PairingState.Rejected or PairingState.Expired)
        {
            client.Dispose();
            client = null;
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        if (client?.IsPaired != true)
        {
            status.Text = "Pair and obtain desktop approval first.";
            return;
        }
        if (SelectedMetrics().Length == 0)
        {
            status.Text = "Select at least one category. Nothing sent.";
            return;
        }
        var selected = SelectedMetrics();
        var records = new List<HealthRecord>();
        var categories = new List<CategoryReadStatus>();
        var errors = new List<string>();
        SyncRecordsRequest? request = null;
        try
        {
            status.Text = "Reading a bounded recent snapshot from Health Connect…";
            foreach (var metric in selected)
            {
                SyncRecordsRequest? read = null;
                try
                {
                    read = await reader.ReadAsync(metric == HealthMetric.HeartRate,
                        metric == HealthMetric.BloodPressure, ct);
                    CheckForeground(ct);
                    records.AddRange(read.Records);
                    categories.AddRange(read.Categories);
                }
                catch (System.OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Intentional per-category UI boundary: never expose native health details.
                    CheckForeground(ct);
                    errors.Add($"{metric}: query failed; not sent. Check Health Connect and retry.");
                }
                finally
                {
                    if (read is not null) Array.Clear(read.Records);
                }
            }
            CheckForeground(ct);
            // Permission can change while the other category is being read.
            for (int index = 0; index < categories.Count; index++)
            {
                var category = categories[index];
                if (category.Availability != ReadAvailability.Unsupported &&
                    CheckSelfPermission(NativeHealthReader.PermissionFor(category.Metric)) != Permission.Granted)
                {
                    records.RemoveAll(record => record.Metric == category.Metric);
                    categories[index] = new(category.Metric, ReadAvailability.Denied);
                }
            }
            var summary = string.Join("\n", categories.Select(category =>
                $"{category.Metric}: {category.Availability}").Concat(errors));
            if (categories.Count == 0)
            {
                status.Text = summary + "\nNothing sent.";
                return;
            }
            request = new(records.ToArray(), categories.ToArray());
            records.Clear();
            try
            {
                var response = await client!.SyncAsync(request, ct);
                CheckForeground(ct);
                status.Text = summary +
                    $"\nSent {request.Records.Length} measurements; desktop accepted {response.Accepted}. " +
                    "Snapshot capped at 128 per category. No data is not a zero reading.";
            }
            catch (System.OperationCanceledException) { throw; }
            catch (Exception)
            {
                CheckForeground(ct);
                status.Text = summary + "\nUpload unconfirmed for these categories. Check the desktop and secure connection; retry is safe for the same samples.";
            }
        }
        finally
        {
            records.Clear();
            if (request is not null) Array.Clear(request.Records);
        }
    }

    private async Task DisconnectAsync(CancellationToken ct)
    {
        var generation = sessionGeneration;
        var previous = client;
        var hadApprovedSession = previous?.IsPaired == true;
        client = null;
        ClearSessionDisplay();
        try
        {
            if (previous is not null) await previous.DisconnectAsync(ct);
            CheckForeground(ct);
            status.Text = hadApprovedSession
                ? "Disconnected. Local credentials discarded; desktop session revoked and received session data cleared."
                : "Disconnected locally. No approved desktop session was revoked; dismiss any pending request on the desktop.";
        }
        catch (Exception)
        {
            if (foreground && generation == sessionGeneration)
                status.Text = "Local credentials discarded. Desktop revocation and data clearing could not be confirmed; revoke this phone on the desktop.";
        }
        finally
        {
            previous?.Dispose();
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (busy || permissionPending || !foreground) return;
        var generation = sessionGeneration;
        busy = true;
        SetControlsEnabled(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        operation = cancellation;
        try
        {
            await action(cancellation.Token);
        }
        catch (System.OperationCanceledException)
        {
            if (foreground && generation == sessionGeneration)
                status.Text = "Operation stopped or timed out. No background retry. Check desktop status before retrying.";
        }
        catch (Exception)
        {
            // Never render exception messages: they may contain endpoints, tokens or response bodies.
            if (foreground && generation == sessionGeneration)
                status.Text = "Operation failed. Verify pairing expiry, desktop approval, same-network HTTPS connectivity, " +
                          "and Health Connect permissions. No insecure connection fallback is used.";
        }
        finally
        {
            operation = null;
            busy = false;
            if (!IsDestroyed)
            {
                SetControlsEnabled(foreground && !permissionPending);
                RefreshAvailability();
            }
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        foreach (var control in controls) control.Enabled = enabled;
    }

    protected override void OnResume()
    {
        base.OnResume();
        foreground = true;
        SetControlsEnabled(!busy && !permissionPending);
        RefreshAvailability();
    }

    protected override void OnPause()
    {
        foreground = false;
        sessionGeneration++;
        operation?.Cancel();
        client?.Dispose();
        client = null;
        ClearSessionDisplay(keepSelection: permissionPending);
        status.Text = "Screen left: local pairing and results cleared. Pair again to sync. " +
                      "Remote revocation was not attempted; revoke old sessions on the desktop.";
        SetControlsEnabled(false);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        operation?.Cancel();
        client?.Dispose();
        client = null;
        ClearSessionDisplay();
        base.OnDestroy();
    }

    private void CheckForeground(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!foreground || IsDestroyed) throw new System.OperationCanceledException(ct);
    }

    private void ClearSessionDisplay(bool keepSelection = false)
    {
        pairing.Text = "";
        pairingRequest.Text = "";
        availability.Text = "";
        if (!keepSelection) heartRate.Checked = bloodPressure.Checked = false;
    }
}
