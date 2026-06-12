namespace gatOS.SimFs.Telemetry;

/// <summary>
///     The sampler's frame-dt rate limiter (OS_PLAN.md T9.1, pure part): accumulate dt and
///     fire at most once per call when an interval has elapsed. Missed intervals after a long
///     frame are <b>dropped, not back-filled</b> — telemetry wants the latest state, and one
///     sample per frame is all a snapshot swap can usefully publish anyway.
/// </summary>
public sealed class SampleClock
{
    private readonly double _intervalSeconds;
    private double _accumulator;

    /// <param name="rateHz">Samples per second (caller clamps; gatos.toml allows 1–120).</param>
    public SampleClock(double rateHz)
    {
        if (rateHz <= 0 || !double.IsFinite(rateHz))
            throw new ArgumentOutOfRangeException(nameof(rateHz), rateHz, "Sample rate must be positive.");
        _intervalSeconds = 1.0 / rateHz;
    }

    /// <summary>Advances by one frame; true = take a sample now.</summary>
    public bool Tick(double dt)
    {
        if (dt > 0 && double.IsFinite(dt))
            _accumulator += dt;
        if (_accumulator < _intervalSeconds)
            return false;

        // Keep sub-interval phase for drift-free cadence, but a backlog of more than one
        // interval (a long frame hitch) is dropped outright, never burst-replayed.
        _accumulator -= _intervalSeconds;
        if (_accumulator >= _intervalSeconds)
            _accumulator = 0;
        return true;
    }

    /// <summary>
    ///     Drops accumulated time (used when sampling was gated off, so reactivation does not
    ///     fire instantly from a stale accumulator — harmless, but keeps cadence honest).
    /// </summary>
    public void Reset() => _accumulator = 0;
}
