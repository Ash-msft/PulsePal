using global::Android.App;
using global::Android.OS;
using global::Android.Widget;

namespace PulsePal.Phone.Android;

[Activity(Name = "org.pulsepal.phone.PermissionsRationaleActivity", Exported = true)]
public sealed class PermissionsRationaleActivity : Activity
{
    internal const string Policy =
        "PulsePal reads only the categories you select: heart rate (last 24 hours, bpm) " +
        "and blood pressure (last 7 days, systolic/diastolic mmHg). It never writes health data. " +
        "Read access requires your approval in the Android Health Connect permission screen.\n\n" +
        "Only tapping Sync now while this app is in the foreground reads and sends data to " +
        "the desktop you paired and explicitly approved. Measurements include their timestamps, " +
        "record IDs, source app package and available source device details. There are no " +
        "background jobs or automatic uploads. Each read scans at most four pages of 64 records " +
        "per category and sends at most 128 measurements per category, 256 total. " +
        "Heart-rate sessions may start up to 30 days earlier; only their samples from the last " +
        "24 hours are shared. This includes sessions crossing the 24-hour boundary. " +
        "This is a recent snapshot, not a complete historical export.\n\n" +
        "Transfers use HTTPS pinned to the certificate in the desktop pairing JSON. " +
        "Pairing secrets and tokens remain in memory only. Leaving the main screen, including " +
        "opening permissions or this policy, clears local pairing and results; pair again on return. " +
        "Category choices alone are retained during a permission request. " +
        "Backgrounding does not revoke desktop sessions; revoke old sessions on the desktop. " +
        "Disconnect drops local credentials and results even if the desktop is offline. " +
        "A confirmed disconnect revokes the desktop session and clears its received data. " +
        "If disconnect cannot reach the desktop, revocation and clearing are not confirmed; " +
        "revoke this phone on the desktop. " +
        "This phone app does not save measurements, credentials, or diagnostic request bodies. " +
        "Cleanup releases managed references, not secure memory erasure or native-store deletion. " +
        "The system clipboard is not cleared. Query failures are shown per category, never as denial.\n\n" +
        "You may deny or revoke access in Android Settings > Health Connect. Unchecking a " +
        "category stops reading/sending it but does not revoke its Android permission. " +
        "Denied, unsupported, and no-data states never become zero measurements.\n\n" +
        "Health reads require Android 14 or newer with built-in Health Connect. Android 9–13 " +
        "explicitly report Unsupported, even if the separate Health Connect provider is installed. " +
        "A compatible source app must first write measurements to Health Connect. " +
        "PulsePal is not a medical device and does not diagnose health conditions.";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var scroll = new ScrollView(this);
        var text = new TextView(this) { Text = "PulsePal health data policy\n\n" + Policy, TextSize = 17 };
        text.SetPadding(28, 64, 28, 64);
        scroll.AddView(text);
        SetContentView(scroll);
    }
}
