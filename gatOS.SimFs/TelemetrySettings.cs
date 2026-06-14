namespace gatOS.SimFs;

/// <summary>
///     The runtime-mutable telemetry cadence and per-category stream gates, shared between the
///     game-thread sampler (which reads them every tick to decide its rate and which KSA reads to
///     perform) and the gatOS menu / status window (which mutate them live). Game-free so it lives
///     in the shared <c>gatOS.SimFs</c> hub rather than the game-coupled mod; the config file
///     (<c>gatos.toml</c>) seeds it at startup and the menu writes back.
/// </summary>
/// <remarks>
///     <para>Every field is <c>volatile</c>: the menu (render thread) is the writer and the sampler
///     (game thread) is the reader. A bool/int store is atomic on the CLR, and <c>volatile</c> adds
///     the ordering guarantee so a toggle flipped in the UI is observed by the next sample — no lock,
///     no allocation, no contention on the hot path.</para>
///     <para>Gating at the sampler is deliberately the single cheapest place to switch a stream off:
///     a disabled category skips its (often expensive) KSA reads <i>and</i> shrinks the published
///     snapshot, so every downstream transport (9p, HTTP, MQTT, serial) serves less by construction —
///     the transport-parity rule kept structural, not re-implemented per transport.</para>
/// </remarks>
public sealed class TelemetrySettings
{
    /// <summary>The widest cadence the sampler accepts (matches the config clamp).</summary>
    public const int MinRateHz = 1;

    /// <summary>The fastest cadence the sampler accepts (matches the config clamp).</summary>
    public const int MaxRateHz = 120;

    private volatile int _sampleRateHz;
    private volatile bool _enabled;
    private volatile bool _vesselDetail;
    private volatile bool _bodies;
    private volatile bool _events;

    /// <param name="sampleRateHz">Initial sample cadence in Hz (already clamped by the config).</param>
    /// <param name="enabled">Master gate — when false the sampler idles entirely.</param>
    /// <param name="vesselDetail">Sample the per-vessel G3 detail (navball, environment, per-module).</param>
    /// <param name="bodies">Sample the celestial-body catalog and the system summary.</param>
    /// <param name="events">Diff snapshots into <c>/sim/events</c> entries.</param>
    public TelemetrySettings(int sampleRateHz = 10, bool enabled = true,
        bool vesselDetail = true, bool bodies = true, bool events = true)
    {
        _sampleRateHz = Clamp(sampleRateHz);
        _enabled = enabled;
        _vesselDetail = vesselDetail;
        _bodies = bodies;
        _events = events;
    }

    /// <summary>Sample cadence in Hz; setting clamps to <see cref="MinRateHz"/>..<see cref="MaxRateHz"/>.</summary>
    public int SampleRateHz
    {
        get => _sampleRateHz;
        set => _sampleRateHz = Clamp(value);
    }

    /// <summary>Master gate: when false the sampler does no work (telemetry freezes at the last frame).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    ///     Sample the per-vessel detail pass (the G3 read-surface: navball, environment, RCS, solar,
    ///     generators, lights, docking, decouplers, encounters, orbit extras, throttle/power read-backs).
    ///     This is the single most expensive per-vessel work; off keeps only the core flight telemetry.
    /// </summary>
    public bool VesselDetail
    {
        get => _vesselDetail;
        set => _vesselDetail = value;
    }

    /// <summary>Sample the celestial-body catalog + system summary (<c>/sim/bodies</c>, <c>/sim/system</c>).</summary>
    public bool Bodies
    {
        get => _bodies;
        set => _bodies = value;
    }

    /// <summary>Diff consecutive snapshots into discrete events (<c>/sim/events</c> and the event topics/streams).</summary>
    public bool Events
    {
        get => _events;
        set => _events = value;
    }

    private static int Clamp(int rate) => Math.Clamp(rate, MinRateHz, MaxRateHz);
}
