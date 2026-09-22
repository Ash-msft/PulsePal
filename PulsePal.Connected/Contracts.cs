using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulsePal.Connected;

public enum HealthMetric { HeartRate, BloodPressure }
public enum ReadAvailability { Available, NoData, Denied, Unsupported, PermissionNotVerifiable }
public enum PairingState { Pending, Approved, Rejected, Expired }

/// <summary>A source measurement, never a derived stress estimate. BP uses Value/SecondaryValue in mmHg; HR uses Value in bpm.</summary>
public sealed record HealthRecord(
    string RecordId, long Version, HealthMetric Metric,
    DateTimeOffset MeasuredAt, DateTimeOffset? EndAt,
    double Value, double? SecondaryValue,
    string SourceApp, string? SourceDevice);

/// <summary>Read status for an explicitly selected category; absence never means zero.</summary>
public sealed record CategoryReadStatus(HealthMetric Metric, ReadAvailability Availability);

/// <summary>A bounded foreground read of selected native health categories.</summary>
public sealed record SyncRecordsRequest(HealthRecord[] Records, CategoryReadStatus[] Categories);

/// <summary>A stored measurement with a distinct desktop receipt timestamp.</summary>
public sealed record ReceivedHealthRecord(HealthRecord Record, DateTimeOffset ReceivedAt);

/// <summary>Result of idempotently accepting a batch.</summary>
public sealed record SyncRecordsResponse(int Accepted, int Retained, DateTimeOffset ReceivedAt);

/// <summary>One-time desktop pairing material. The certificate hash, not public PKI, identifies the ephemeral desktop.</summary>
public sealed record PairingPayload(int Protocol, string Endpoint, string CertificateSha256, string Challenge, DateTimeOffset ExpiresAt);

/// <summary>A phone requesting explicit desktop approval.</summary>
public sealed record CreatePairingRequest(string Challenge, string DeviceName);

/// <summary>A secret used only to retrieve an approved session token.</summary>
public sealed record PendingPairingResponse(Guid RequestId, string PollSecret, DateTimeOffset ExpiresAt);

/// <summary>A pending phone polls without putting secrets in URLs.</summary>
public sealed record PairingStatusRequest(Guid RequestId, string PollSecret);

/// <summary>The session token is returned only after desktop approval, and only once.</summary>
public sealed record PairingStatusResponse(PairingState State, string? DeviceToken);

/// <summary>Read-only view for the desktop approval UI.</summary>
public sealed record PendingDevice(Guid RequestId, string DeviceName, DateTimeOffset ExpiresAt);

public static class HealthMetadata
{
    public static bool IsValid(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.EnumerateRunes().All(r => Rune.GetUnicodeCategory(r) is not
            (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
             or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));
}

public static class ConnectedJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
