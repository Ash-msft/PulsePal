namespace PulsePal.Core;

/// <summary>A UI-owned recovery timer driven only by monotonic elapsed time.</summary>
public sealed class RecoverySession
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private RecoveryOption? _active;
    private long _startedAt;
    private bool _isAccelerated;
    private TimeSpan _actualDuration;
    private TimeSpan _representedDuration;

    public RecoverySession(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public RecoveryActivity? ActiveActivity { get { lock (_gate) return _active?.Activity; } }
    public bool IsActive { get { lock (_gate) return _active is not null; } }
    public bool IsAccelerated { get { lock (_gate) return _isAccelerated; } }
    public TimeSpan ActualDuration { get { lock (_gate) return _actualDuration; } }
    public TimeSpan RepresentedDuration { get { lock (_gate) return _representedDuration; } }
    public TimeSpan Elapsed { get { lock (_gate) return ElapsedCore(); } }
    public TimeSpan Remaining { get { lock (_gate) return _active is not null ? _actualDuration - ElapsedCore() : TimeSpan.Zero; } }
    public double Progress { get { lock (_gate) return _active is not null ? ElapsedCore().TotalSeconds / _actualDuration.TotalSeconds : 0; } }

    public void Start(RecoveryActivity activity, bool accelerated = false)
    {
        lock (_gate)
        {
            if (_active is not null)
                throw new InvalidOperationException("A recovery session is already active.");
            var option = RecoveryActivities.Get(activity);
            _startedAt = _timeProvider.GetTimestamp();
            _active = option;
            _isAccelerated = accelerated;
            _representedDuration = option.Duration;
            _actualDuration = accelerated ? TimeSpan.FromSeconds(15) : option.Duration;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _active = null;
            _isAccelerated = false;
            _actualDuration = TimeSpan.Zero;
            _representedDuration = TimeSpan.Zero;
        }
    }

    public RecoveryActivity? TryComplete()
    {
        lock (_gate)
        {
            if (_active is not { } active || ElapsedCore() < _actualDuration) return null;
            // Retain duration and mode metadata until the next start or cancellation.
            _active = null;
            return active.Activity;
        }
    }

    private TimeSpan ElapsedCore()
    {
        if (_active is null) return TimeSpan.Zero;
        var elapsed = _timeProvider.GetElapsedTime(_startedAt, _timeProvider.GetTimestamp());
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > _actualDuration ? _actualDuration : elapsed;
    }
}
