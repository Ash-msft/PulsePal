namespace PulsePal.Core;

public enum TourStep
{
    Idle,
    Briefing,
    Focus,
    Interruptions,
    MeetingStress,
    Recovery,
    Comparison,
    Summary
}

/// <summary>A companion-led synthetic tour driven by an injectable monotonic clock.</summary>
public sealed class GuidedTour
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private TourStep _step;
    private long _startedAt;
    private bool _peakReady;
    private bool _recoveryActive;
    private bool _recoveryResolved;

    public GuidedTour(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public TourStep Step { get { lock (_gate) return _step; } }
    public bool IsActive { get { lock (_gate) return _step is >= TourStep.Briefing and <= TourStep.Comparison; } }
    public TimeSpan TimeInStep { get { lock (_gate) return ElapsedCore(); } }

    public string Title => Step switch
    {
        TourStep.Idle => "Your companion is here",
        TourStep.Briefing => "Let's take a gentle tour",
        TourStep.Focus => "Finding a little focus",
        TourStep.Interruptions => "When interruptions arrive",
        TourStep.MeetingStress => "A demanding moment",
        TourStep.Recovery => "Take the time you need",
        TourStep.Comparison => "Reflecting together",
        TourStep.Summary => "Carry a little care forward",
        _ => string.Empty
    };

    public string Narrative => Step switch
    {
        TourStep.Idle => "I'm here when you're ready to explore a simulated day together.",
        TourStep.Briefing => "I'll walk beside you through focus, interruptions, and a recovery break. This tour uses synthetic signals, not measurements of your body or a medical assessment.",
        TourStep.Focus => "Let's begin with a quieter stretch of our simulated day. I'll stay nearby while you settle into one thing at a time.",
        TourStep.Interruptions => "Our scenario now introduces interruptions. You don't have to respond to everything at once; we can notice the change together.",
        TourStep.MeetingStress => "This simulated meeting introduces a more demanding moment. I'll wait for the scenario's peak context before we explore a supportive break; it isn't a reading of your stress.",
        TourStep.Recovery => "Let's choose a recovery activity that feels comfortable. There's no need to rush: I'll stay here until the recovery is resolved and no activity is running.",
        TourStep.Comparison => "Let's reflect on the simulated before-and-after context together. These synthetic signals illustrate the experience; they don't establish that a break changed your health.",
        TourStep.Summary => "Thanks for exploring with me. In your own day, you can choose a small pause when it suits you. This was a synthetic demonstration, not a health assessment.",
        _ => string.Empty
    };

    /// <summary>Explains the current wait using readiness from the latest Next or Update call.</summary>
    public string WaitingReason
    {
        get
        {
            lock (_gate)
            {
                return _step switch
                {
                    TourStep.Briefing => "Take a moment to settle in, or continue when you're ready.",
                    TourStep.Focus => "We're giving this quieter moment some space; you can continue when ready.",
                    TourStep.Interruptions => "We're allowing time to notice the simulated interruptions; you can continue when ready.",
                    TourStep.MeetingStress when ElapsedCore() < TimeSpan.FromSeconds(8) => "We're allowing at least eight seconds for the simulated meeting context.",
                    TourStep.MeetingStress when !_peakReady => "We're waiting for the simulated peak context to be ready.",
                    TourStep.Recovery when _recoveryActive => "Your recovery activity is still running. Take the time you need.",
                    TourStep.Recovery when !_recoveryResolved => "Choose a recovery activity; we'll continue only when recovery is resolved.",
                    TourStep.Comparison => "Take a moment to reflect, or continue when you're ready.",
                    _ => string.Empty
                };
            }
        }
    }

    /// <summary>Starts or restarts the tour at Briefing with a fresh timestamp.</summary>
    public void Start()
    {
        lock (_gate) EnterCore(TourStep.Briefing);
    }

    public void Stop()
    {
        lock (_gate) EnterCore(TourStep.Idle);
    }

    /// <summary>Advances one step manually, without bypassing meeting or recovery gates.</summary>
    public bool Next(bool peakReady = false, bool recoveryActive = false, bool recoveryResolved = false)
        => Advance(true, peakReady, recoveryActive, recoveryResolved);

    /// <summary>Advances at most one eligible step. Callers pause by skipping calls and freezing the supplied clock.</summary>
    public bool Update(bool peakReady = false, bool recoveryActive = false, bool recoveryResolved = false)
        => Advance(false, peakReady, recoveryActive, recoveryResolved);

    private bool Advance(bool manual, bool peakReady, bool recoveryActive, bool recoveryResolved)
    {
        lock (_gate)
        {
            _peakReady = peakReady;
            _recoveryActive = recoveryActive;
            _recoveryResolved = recoveryResolved;
            bool ready = _step switch
            {
                TourStep.Briefing or TourStep.Focus or TourStep.Comparison => manual || ElapsedCore() >= TimeSpan.FromSeconds(8),
                TourStep.Interruptions => manual || ElapsedCore() >= TimeSpan.FromSeconds(12),
                TourStep.MeetingStress => peakReady && ElapsedCore() >= TimeSpan.FromSeconds(8),
                TourStep.Recovery => recoveryResolved && !recoveryActive,
                _ => false
            };
            if (!ready) return false;
            EnterCore((TourStep)((int)_step + 1));
            return true;
        }
    }

    private void EnterCore(TourStep step)
    {
        _startedAt = _timeProvider.GetTimestamp();
        _step = step;
        _peakReady = false;
        _recoveryActive = false;
        _recoveryResolved = false;
    }

    private TimeSpan ElapsedCore()
    {
        if (_step == TourStep.Idle) return TimeSpan.Zero;
        var elapsed = _timeProvider.GetElapsedTime(_startedAt, _timeProvider.GetTimestamp());
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}
