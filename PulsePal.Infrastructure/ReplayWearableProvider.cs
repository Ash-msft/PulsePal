using System.Text.Json;
using PulsePal.Core;

namespace PulsePal.Infrastructure;

public sealed class ReplayWearableProvider : IWearableProvider
{
    private readonly WearableSample[] _samples;
    private readonly bool _loop;
    private readonly object _gate = new();
    private int _index;

    public string Name => "Replay";

    public ReplayWearableProvider(string path, bool loop = false)
        : this(ReadSamples(path), loop)
    {
    }

    public ReplayWearableProvider(IEnumerable<WearableSample> samples, bool loop = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _samples = samples.ToArray();
        if (_samples.Length == 0)
        {
            throw new ArgumentException("Replay requires at least one wearable sample.", nameof(samples));
        }

        for (var index = 0; index < _samples.Length; index++)
        {
            ValidateSample(_samples[index], index);
        }

        _loop = loop;
    }

    public Task<WearableSample> GetSampleAsync(WorkContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index == _samples.Length)
            {
                throw new EndOfStreamException("Replay is exhausted. Construct with loop: true to repeat samples.");
            }

            var sample = _samples[_index] with { Timestamp = now };
            _index++;
            if (_loop && _index == _samples.Length)
            {
                _index = 0;
            }

            return Task.FromResult(sample);
        }
    }

    private static WearableSample[] ReadSamples(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<WearableSample[]>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Expected a JSON array of wearable samples, not null.");
        }
        catch (JsonException exception)
        {
            throw new JsonException($"Invalid wearable replay JSON in '{path}': {exception.Message}", exception);
        }
    }

    private static void ValidateSample(WearableSample? sample, int index)
    {
        if (sample is null)
        {
            throw new ArgumentException($"Replay sample at index {index} must not be null.", "samples");
        }

        Check(sample.HeartRate, 20, 250, nameof(sample.HeartRate));
        Check(sample.RestingHeartRate, 20, 250, nameof(sample.RestingHeartRate));
        Check(sample.HeartRateVariability, 0, 500, nameof(sample.HeartRateVariability));
        Check(sample.StressLevel, 0, 100, nameof(sample.StressLevel));
        Check(sample.Steps, 0, 200_000, nameof(sample.Steps));
        Check(sample.Calories, 0, 20_000, nameof(sample.Calories));
        Check(sample.ActiveMinutes, 0, 1_440, nameof(sample.ActiveMinutes));
        Check(sample.SleepScore, 0, 100, nameof(sample.SleepScore));
        Check(sample.TotalSleepMinutes, 0, 1_440, nameof(sample.TotalSleepMinutes));
        Check(sample.DeepSleepMinutes, 0, 1_440, nameof(sample.DeepSleepMinutes));
        Check(sample.RemSleepMinutes, 0, 1_440, nameof(sample.RemSleepMinutes));
        Check(sample.LightSleepMinutes, 0, 1_440, nameof(sample.LightSleepMinutes));
        Check(sample.ReadinessScore, 0, 100, nameof(sample.ReadinessScore));
        Check(sample.RecoveryScore, 0, 100, nameof(sample.RecoveryScore));
        Check(sample.BloodOxygen, 0, 100, nameof(sample.BloodOxygen));
        Check(sample.RespirationRate, 1, 100, nameof(sample.RespirationRate));
        Check(sample.BodyBattery, 0, 100, nameof(sample.BodyBattery));
        Check(sample.FocusScore, 0, 100, nameof(sample.FocusScore));

        if (sample.TotalSleepMinutes is int total)
        {
            if (sample.DeepSleepMinutes > total || sample.RemSleepMinutes > total || sample.LightSleepMinutes > total)
            {
                throw new ArgumentException($"Replay sample at index {index}: sleep stages cannot exceed TotalSleepMinutes.", "samples");
            }

            if (sample.DeepSleepMinutes is int deep && sample.RemSleepMinutes is int rem &&
                sample.LightSleepMinutes is int light && deep + rem + light != total)
            {
                throw new ArgumentException($"Replay sample at index {index}: DeepSleepMinutes + RemSleepMinutes + LightSleepMinutes must equal TotalSleepMinutes.", "samples");
            }
        }

        void Check(double? value, double minimum, double maximum, string metric)
        {
            if (value is double number && (!double.IsFinite(number) || number < minimum || number > maximum))
            {
                throw new ArgumentException($"Replay sample at index {index}: {metric} must be finite and between {minimum} and {maximum}, inclusive, or null.", "samples");
            }
        }
    }
}
