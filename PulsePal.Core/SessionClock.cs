namespace PulsePal.Core;

/// <summary>A pauseable monotonic clock with an optional presentation wall date.</summary>
public sealed class SessionClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _source;
    private readonly long _sourceFrequency;
    private long _sourceAnchor;
    private long _elapsedTicks;
    private long _lastTimestamp;
    private long _presentationStartedAt;
    private DateTimeOffset _presentationStart;
    private bool _isPresentation;
    private bool _isPaused;

    public SessionClock(TimeProvider? source = null)
    {
        _source = source ?? TimeProvider.System;
        _sourceFrequency = _source.TimestampFrequency;
        if (_sourceFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Timestamp frequency must be positive.");
        _sourceAnchor = _source.GetTimestamp();
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public bool IsPresentation { get { lock (_gate) return _isPresentation; } }
    public bool IsPaused { get { lock (_gate) return _isPaused; } }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _isPresentation
                ? _presentationStart.AddTicks(TimestampCore() - _presentationStartedAt)
                : _source.GetUtcNow();
    }

    public override long GetTimestamp()
    {
        lock (_gate) return TimestampCore();
    }

    public void BeginPresentation(DateTimeOffset start)
    {
        lock (_gate)
        {
            _presentationStartedAt = TimestampCore();
            _presentationStart = start.ToUniversalTime();
            _isPresentation = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_isPaused) return;
            _elapsedTicks = TimestampCore();
            _isPaused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_isPaused) return;
            _sourceAnchor = _source.GetTimestamp();
            _isPaused = false;
        }
    }

    public void EndPresentation()
    {
        lock (_gate) _isPresentation = false;
    }

    private long TimestampCore()
    {
        if (_isPaused) return _elapsedTicks;
        // Convert from the fixed anchor so polling never loses fractional source ticks.
        var ticks = ((decimal)_source.GetTimestamp() - _sourceAnchor)
            * TimeSpan.TicksPerSecond / _sourceFrequency;
        var timestamp = (long)Math.Clamp(_elapsedTicks + ticks, 0m, long.MaxValue);
        _lastTimestamp = Math.Max(_lastTimestamp, timestamp);
        return _lastTimestamp;
    }
}
