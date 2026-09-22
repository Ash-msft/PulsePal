using System.Diagnostics.CodeAnalysis;
using PulsePal.Connected;

namespace PulsePal.Bridge.Services;

internal static class BridgeValidator
{
    public static bool Text(string? value, int maximum = 128) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.All(c => c is >= ' ' and <= '~');

    public static void Require([DoesNotReturnIf(false)] bool valid)
    {
        if (!valid) throw new BridgeRequestException(400);
    }

    public static void Validate(SyncRecordsRequest request, DateTimeOffset now)
    {
        Require(request.Records is { Length: <= 256 } && request.Categories is { Length: <= 2 });
        var selected = new Dictionary<HealthMetric, ReadAvailability>();
        foreach (var category in request.Categories)
        {
            Require(category is not null && Enum.IsDefined(category.Metric) &&
                Enum.IsDefined(category.Availability));
            Require(selected.TryAdd(category!.Metric, category.Availability));
        }

        bool ValidTime(DateTimeOffset time) => time >= now.AddDays(-30) && time <= now.AddMinutes(2);
        foreach (var record in request.Records)
        {
            Require(record is not null);
            Require(Text(record!.RecordId) && HealthMetadata.IsValid(record.SourceApp, 512) &&
                (record.SourceDevice is null || HealthMetadata.IsValid(record.SourceDevice, 256)) &&
                record.Version >= 0 && Enum.IsDefined(record.Metric));
            Require(selected.TryGetValue(record.Metric, out var availability) &&
                availability == ReadAvailability.Available);
            Require(ValidTime(record.MeasuredAt) &&
                (record.EndAt is null || (ValidTime(record.EndAt.Value) && record.EndAt >= record.MeasuredAt)));
            Require(double.IsFinite(record.Value) && record.Value is > 0 and <= 1000);
            Require(record.Metric == HealthMetric.HeartRate
                ? record.SecondaryValue is null
                : record.SecondaryValue is > 0 and <= 1000 && double.IsFinite(record.SecondaryValue.Value));
        }
    }
}
