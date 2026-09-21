using System.Globalization;

namespace PulsePal.Core;

public sealed record RecoveryComparison(double? StressBefore, double? StressAfter,
    double? RecoveryBefore, double? RecoveryAfter, double? HeartRateBefore, double? HeartRateAfter,
    bool IsSynthetic, bool IsAccelerated)
{
    public static RecoveryComparison Capture(DemoSnapshot? before, DemoSnapshot? after,
        bool synthetic, bool accelerated) => new(
            Finite(before?.Analysis.StressScore), Finite(after?.Analysis.StressScore),
            Finite(before?.Wearable.RecoveryScore), Finite(after?.Wearable.RecoveryScore),
            Finite(before?.Wearable.HeartRate), Finite(after?.Wearable.HeartRate),
            synthetic, accelerated);

    public double? StressDelta => Delta(StressBefore, StressAfter);
    public double? RecoveryDelta => Delta(RecoveryBefore, RecoveryAfter);
    public double? HeartRateDelta => Delta(HeartRateBefore, HeartRateAfter);

    public string Description =>
        (IsSynthetic ? "Synthetic comparison, not a health outcome." : "Reported comparison, not evidence of a health outcome or a break's effect.")
        + (IsAccelerated ? " Accelerated demo." : string.Empty)
        + $" Stress: {Pair(StressBefore, StressAfter)}; recovery: {Pair(RecoveryBefore, RecoveryAfter)}; heart rate (bpm): {Pair(HeartRateBefore, HeartRateAfter)}.";

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static double? Delta(double? before, double? after) =>
        Finite(before) is { } first && Finite(after) is { } last ? Finite(last - first) : null;

    private static string Number(double? value) => Finite(value) is { } number
        ? number.ToString("G", CultureInfo.InvariantCulture) : "unavailable";

    private static string Pair(double? before, double? after)
    {
        var delta = Delta(before, after);
        var change = delta is { } number ? (number >= 0 ? "+" : string.Empty) + Number(number) : "unavailable";
        return $"{Number(before)} → {Number(after)} (Δ {change})";
    }
}
