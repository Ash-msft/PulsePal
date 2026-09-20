using System;
using System.Collections.Generic;

namespace PulsePal.Core;

public sealed class CooldownPolicy
{
    private readonly object _gate = new();
    private readonly TimeSpan _cooldown;
    private readonly Dictionary<string, DateTimeOffset> _lastAllowedByCategory = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastAllowed;
    private DateTimeOffset? _snoozedUntil;

    public CooldownPolicy(TimeSpan? cooldown = null)
    {
        _cooldown = cooldown ?? TimeSpan.FromSeconds(60);
        if (_cooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cooldown), "Cooldown must be nonnegative.");
    }

    public bool TryAllow(string category, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category must not be null, empty, or whitespace.", nameof(category));

        lock (_gate)
        {
            if (_snoozedUntil is { } snoozedUntil && now < snoozedUntil)
                return false;

            if (_lastAllowed is { } lastAllowed && now - lastAllowed < _cooldown)
                return false;

            if (_lastAllowedByCategory.TryGetValue(category, out var lastCategoryAllowed)
                && now - lastCategoryAllowed < _cooldown)
                return false;

            _lastAllowed = now;
            _lastAllowedByCategory[category] = now;
            return true;
        }
    }

    /// <summary>Peak stress may escalate past a mild cue, but never past snooze or its own two-minute repeat limit.</summary>
    public bool TryAllowPeakStress(DateTimeOffset now)
    {
        const string category = "peak-stress";
        lock (_gate)
        {
            if (_snoozedUntil is { } snoozedUntil && now < snoozedUntil)
                return false;
            if (_lastAllowed is { } lastAllowed && now < lastAllowed)
                return false;
            if (_lastAllowedByCategory.TryGetValue(category, out var lastPeak)
                && now - lastPeak < TimeSpan.FromSeconds(120))
                return false;
            _lastAllowed = now;
            _lastAllowedByCategory[category] = now;
            return true;
        }
    }

    public void Snooze(TimeSpan duration, DateTimeOffset now)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Snooze duration must be nonnegative.");
        if (duration == TimeSpan.Zero)
            return;

        lock (_gate)
        {
            // UTC arithmetic avoids overflowing the local date for nonzero offsets.
            var until = duration >= DateTimeOffset.MaxValue - now
                ? DateTimeOffset.MaxValue
                : now.ToUniversalTime().Add(duration);

            if (!_snoozedUntil.HasValue || until > _snoozedUntil.Value)
                _snoozedUntil = until;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastAllowed = null;
            _snoozedUntil = null;
            _lastAllowedByCategory.Clear();
        }
    }
}
