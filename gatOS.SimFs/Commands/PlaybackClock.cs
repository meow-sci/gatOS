namespace gatOS.SimFs.Commands;

/// <summary>
///     Which notion of "now" a <see cref="PlaybackClock"/> advances against
///     (plans/CAMERA_CONTROLS_PLAN.md §3.2). The three genuinely differ and the choice is the
///     author's, never a default the host picks for them.
/// </summary>
public enum ClockBase
{
    /// <summary>
    ///     Accumulated rendered-frame time — the game's own <c>dtPlayer</c>. <b>Not</b> true wall
    ///     time: KSA clamps the per-frame delta, so during a hitch the game runs in slow motion and
    ///     this clock lags real time and never catches up. Correct for cinematics (the schedule stays
    ///     in sync with rendered motion), wrong for syncing to a host recorder. The default.
    /// </summary>
    Render,

    /// <summary>True elapsed wall time. Can demand a catch-up burst after a stall.</summary>
    Wall,

    /// <summary>Sim time (universal time). Right for mission events, which diverge wildly under warp.</summary>
    Ut,
}

/// <summary>
///     The lifecycle state of a host-side player in the <c>/sim/ctl/schedules/</c> registry — the
///     shared vocabulary of schedules and (later) camera tracks, rendered lower-case in the
///     <c>state</c> leaf.
/// </summary>
public enum PlaybackState
{
    /// <summary>Registered but not yet started (its clock has not been started).</summary>
    Pending,

    /// <summary>Advancing and firing.</summary>
    Running,

    /// <summary>Its clock is paused; nothing advances and nothing fires.</summary>
    Paused,

    /// <summary>Finished (ran to the end, or was explicitly stopped). Stays visible until removed.</summary>
    Done,

    /// <summary>At least one entry failed. The player keeps running; the <i>first</i> error is kept.</summary>
    Failed,
}

/// <summary>
///     <b>The one timeline primitive</b> (plans/CAMERA_CONTROLS_PLAN.md §3.4), shared by the generic
///     command scheduler and — by construction, so the two can never drift — the camera track player.
///     It owns <i>only</i> the timeline: which clock base it rides, the playback rate, looping,
///     pausing, and the scrub cursor. It knows nothing about entries, commands, or curves; consumers
///     read <see cref="PositionMs"/> and react to <see cref="LoopCount"/> / <see cref="ScrubGeneration"/>
///     changing.
/// </summary>
/// <remarks>
///     <para><b>Shared-clock groups.</b> A "group" is simply several players holding the <i>same</i>
///     instance. That is what makes "the dolly move, the light cues and the slow-mo beat are one take"
///     literally true rather than approximately true: a <c>pause</c>/<c>scrub</c>/<c>rate</c> on any
///     member moves every member because there is one clock, not several that agree. The registry that
///     hands out shared instances is therefore also responsible for calling <see cref="Advance"/>
///     <b>exactly once per clock per tick</b> — advancing once per member would run a group at N×.</para>
///     <para><b>Threading.</b> <see cref="Advance"/> and <see cref="Scrub"/> are game-thread calls;
///     every property is published/read through <c>Volatile</c> (doubles as bit-cast <c>long</c>s) so
///     transport threads can render the status leaves lock-free. A torn read is impossible; a
///     <i>stale</i> read is fine — status is a display value.</para>
/// </remarks>
public sealed class PlaybackClock
{
    /// <summary>Lowest accepted <see cref="Rate"/>. Zero is deliberate: it means "frozen", not "invalid".</summary>
    public const double MinRate = 0.0;

    /// <summary>Highest accepted <see cref="Rate"/>.</summary>
    public const double MaxRate = 100.0;

    private long _position;
    private long _duration;
    private long _rate = BitConverter.DoubleToInt64Bits(1.0);
    private int _loopCount;
    private int _scrubGeneration;
    private volatile bool _loop;
    private volatile bool _paused;
    private volatile bool _started;

    /// <param name="clockBase">Which delta <see cref="Advance"/> consumes.</param>
    public PlaybackClock(ClockBase clockBase) => Base = clockBase;

    /// <summary>The clock base this timeline rides (fixed for the clock's life).</summary>
    public ClockBase Base { get; }

    /// <summary>
    ///     The current offset from the player's start, in milliseconds. Written only by
    ///     <see cref="Advance"/>/<see cref="Scrub"/> (game thread), readable from anywhere.
    /// </summary>
    public double PositionMs => Read(ref _position);

    /// <summary>
    ///     The timeline length in milliseconds. The setter takes the <b>maximum</b> and never shrinks:
    ///     a shared group clock's duration is the max over its members, so members can join at any
    ///     time without truncating an already-longer take.
    /// </summary>
    public double DurationMs
    {
        get => Read(ref _duration);
        set
        {
            if (double.IsFinite(value) && value > Read(ref _duration))
                Write(ref _duration, value);
        }
    }

    /// <summary>
    ///     The playback-rate multiplier, clamped to <c>[<see cref="MinRate"/>, <see cref="MaxRate"/>]</c>.
    ///     <c>0</c> freezes the timeline without pausing it (a legal, useful state — the difference
    ///     from <see cref="Paused"/> is intent, and that a rate of 0 still reports <c>running</c>).
    ///     A non-finite value resets it to <c>1</c>.
    /// </summary>
    public double Rate
    {
        get => Read(ref _rate);
        set => Write(ref _rate, double.IsFinite(value) ? Math.Clamp(value, MinRate, MaxRate) : 1.0);
    }

    /// <summary>Whether the timeline wraps at <see cref="DurationMs"/> instead of stopping there.</summary>
    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    /// <summary>Whether <see cref="Advance"/> is a no-op (the timeline is held where it is).</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Whether <see cref="Start"/> has been called; before that <see cref="Advance"/> does nothing.</summary>
    public bool Started => _started;

    /// <summary>
    ///     How many times the timeline has wrapped past <see cref="DurationMs"/>. Consumers compare it
    ///     against the value they last saw to detect a wrap and reset their cursors — a counter rather
    ///     than a flag so a consumer that misses a tick still sees the change.
    /// </summary>
    public int LoopCount => Volatile.Read(ref _loopCount);

    /// <summary>
    ///     Bumped by every <see cref="Scrub"/>. Consumers compare it against the value they last saw
    ///     to know their cursor must be recomputed from <see cref="PositionMs"/> (and that the seek
    ///     must fire nothing — a seek is not playback).
    /// </summary>
    public int ScrubGeneration => Volatile.Read(ref _scrubGeneration);

    /// <summary>Starts the timeline. Idempotent.</summary>
    public void Start() => _started = true;

    /// <summary>
    ///     Game thread, <b>once per clock per tick</b>: advances the timeline by this tick's delta for
    ///     this clock's <see cref="Base"/>, scaled by <see cref="Rate"/>. A no-op while paused, not
    ///     started, or when the selected delta is not a positive finite number. Wraps (bumping
    ///     <see cref="LoopCount"/>, keeping the remainder so a loop does not drift) when
    ///     <see cref="Loop"/> is set and a duration is known; otherwise clamps at the duration.
    /// </summary>
    /// <param name="renderDeltaMs">This frame's rendered-time delta, ms.</param>
    /// <param name="wallDeltaMs">This frame's true wall-time delta, ms.</param>
    /// <param name="utDeltaMs">This frame's sim-time (UT) delta, ms.</param>
    public void Advance(double renderDeltaMs, double wallDeltaMs, double utDeltaMs)
    {
        if (!_started || _paused)
            return;

        var delta = Base switch
        {
            ClockBase.Wall => wallDeltaMs,
            ClockBase.Ut => utDeltaMs,
            _ => renderDeltaMs,
        };
        if (!double.IsFinite(delta) || delta <= 0)
            return;

        var rate = Rate;
        if (rate <= 0)
            return;

        var duration = DurationMs;
        var position = Read(ref _position) + (delta * rate);
        if (_loop && duration > 0)
        {
            var wraps = 0;
            while (position >= duration)
            {
                position -= duration;
                wraps++;
            }

            if (wraps > 0)
                Interlocked.Add(ref _loopCount, wraps);
        }
        else if (duration > 0 && position > duration)
        {
            position = duration;
        }

        Write(ref _position, position);
    }

    /// <summary>
    ///     Seeks the timeline to <paramref name="ms"/> (clamped at 0 below; a seek <i>past</i> the
    ///     duration is allowed and simply parks the player at that offset) and bumps
    ///     <see cref="ScrubGeneration"/> so consumers re-seat their cursors without firing anything.
    ///     Non-finite input is ignored.
    /// </summary>
    public void Scrub(double ms)
    {
        if (!double.IsFinite(ms))
            return;
        Write(ref _position, Math.Clamp(ms, 0, Math.Max(DurationMs, ms)));
        Interlocked.Increment(ref _scrubGeneration);
    }

    // Doubles cannot be `volatile` in C#; publishing them as bit-cast longs gives the same
    // torn-read-free, ordered read/write at the cost of one register move.
    private static double Read(ref long field) => BitConverter.Int64BitsToDouble(Volatile.Read(ref field));

    private static void Write(ref long field, double value)
        => Volatile.Write(ref field, BitConverter.DoubleToInt64Bits(value));
}
