namespace PulsePal.Core;

/// <summary>A UI-owned recovery timer driven only by monotonic elapsed time.</summary>
public sealed class RecoverySession
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private RecoveryOption? _active;
    private long _startedAt;

    public RecoverySession(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public RecoveryActivity? ActiveActivity { get { lock (_gate) return _active?.Activity; } }
    public bool IsActive { get { lock (_gate) return _active is not null; } }
    public TimeSpan Elapsed { get { lock (_gate) return ElapsedCore(); } }
    public TimeSpan Remaining { get { lock (_gate) return _active is { } active ? active.Duration - ElapsedCore() : TimeSpan.Zero; } }
    public double Progress { get { lock (_gate) return _active is { } active ? ElapsedCore().TotalSeconds / active.Duration.TotalSeconds : 0; } }

    public void Start(RecoveryActivity activity)
    {
        lock (_gate)
        {
            if (_active is not null)
                throw new InvalidOperationException("A recovery session is already active.");
            var option = RecoveryActivities.Get(activity);
            _startedAt = _timeProvider.GetTimestamp();
            _active = option;
        }
    }

    public void Cancel()
    {
        lock (_gate) _active = null;
    }

    public RecoveryActivity? TryComplete()
    {
        lock (_gate)
        {
            if (_active is not { } active || ElapsedCore() < active.Duration) return null;
            _active = null;
            return active.Activity;
        }
    }

    private TimeSpan ElapsedCore()
    {
        if (_active is not { } active) return TimeSpan.Zero;
        var elapsed = _timeProvider.GetElapsedTime(_startedAt, _timeProvider.GetTimestamp());
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > active.Duration ? active.Duration : elapsed;
    }
}
