using System.Diagnostics;
using gatOS.GameMod.Game.Ksa;
using gatOS.GameMod.Game.Ksa.Camera;
using gatOS.GameMod.Game.Ksa.Fx;
using gatOS.GameMod.Game.Ksa.Iva;
using gatOS.GameMod.Game.Ksa.Readers;
using gatOS.GameMod.Game.Ksa.Render;
using gatOS.GameMod.Game.Ksa.ThugLife;
using gatOS.GameMod.Game.Ksa.Welds;
using gatOS.Logging;
using gatOS.SimFs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;
using KSA;

namespace gatOS.GameMod.Game;

/// <summary>
///     The game-thread telemetry sampler (OS_PLAN.md T9.1): rate-limited by frame dt, it reads
///     each vehicle through the <see cref="VesselReader"/> integration layer
///     (KSA_GAME_INTEGRATION_PLAN §3.2 — the only place KSA telemetry APIs are touched), builds an
///     immutable <see cref="SimSnapshot"/> and publishes it with one volatile swap (threading
///     rule 1). 9p server threads only ever see published snapshots (rule 2).
/// </summary>
/// <remarks>
///     Every vehicle is sampled inside its own try/catch (a mid-teardown vehicle must not kill
///     the loop — ksa skill gotcha). The published snapshot also carries the game version, the
///     sampler cadence, and the live accessor-health list (shared with <see cref="KsaCatalog"/>)
///     for the <c>/sim/status</c> tree. This file compiles only when the KSA reference assemblies
///     are present (csproj Game/** gate).
/// </remarks>
internal sealed class TelemetrySampler
{
    private readonly SnapshotStore _store;
    private readonly SampleClock _clock;
    private readonly KsaHealth _health;
    private readonly TelemetrySettings _settings;
    private readonly PerfStat _sampleStats;
    private readonly ValueStat _allocStats;
    private readonly WeldManager _welds;
    private readonly ThugLifeManager _thugLife;
    private readonly FaceFxManager? _faceFx;
    private readonly IvaPhysicsManager _iva;
    private readonly PerfStat _ivaStats;
    private readonly AudioStore? _audio;
    private readonly ScheduleStore? _schedules;
    private readonly CameraDirector? _camera;
    private readonly bool _debugNamespace;
    private int _appliedRateHz;
    private IReadOnlyList<double> _warpSpeeds = [];
    private SimSnapshot? _previous;
    private long _sequence;
    private bool _vehicleErrorLogged;
    private bool _bodyErrorLogged;
    private bool _fxErrorLogged;
    private string _gameVersion = "";

    // Bodies sub-cadence (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3): when
    // telemetry_bodies_rate_hz is below the master rate, the ticks in between re-publish the SAME
    // bodies/system objects by reference — no KSA reads, no allocation, and consumers get a
    // reference-equality "unchanged" signal. Wall-clock (Stopwatch) paced, like the master clock.
    private long _lastBodiesTimestamp;
    private IReadOnlyList<BodySnapshot> _lastBodies = [];
    private SystemSnapshot? _lastSystem;

    /// <param name="store">The exchange the 9p tree reads from.</param>
    /// <param name="settings">
    ///     The runtime-mutable cadence + per-stream gates (seeded from config, retuned in-game).
    ///     Read every tick so a menu change takes effect on the next sample.
    /// </param>
    /// <param name="health">Accessor-health latches, shared with the command executor.</param>
    /// <param name="sampleStats">Timing accumulator for one <see cref="Sample"/> (the status window reads it).</param>
    /// <param name="allocStats">Bytes allocated by one <see cref="Sample"/> (the status window reads it).</param>
    /// <param name="welds">The weld registry — projected into the snapshot for the <c>/sim/debug/welds</c> view.</param>
    /// <param name="thugLife">The thug-life registry — projected for the <c>/sim/debug/thug_life</c> view.</param>
    /// <param name="iva">
    ///     The IVA cabin-physics registry — projected for the <c>/sim/debug/iva</c> view, and the
    ///     source of the <c>iva.impact</c>/<c>iva.escape</c>/<c>iva.release</c> events folded into each
    ///     snapshot.
    /// </param>
    /// <param name="ivaStats">Driver-time accumulator, reported through <c>/sim/debug/iva/stats</c>.</param>
    /// <param name="audio">
    ///     The audio store — its pending <c>audio.finished</c> events fold into each snapshot's
    ///     <see cref="SimSnapshot.NewEvents"/> (so they reach <c>/sim/events</c> and every event
    ///     transport). Null when audio is disabled.
    /// </param>
    /// <param name="schedules">
    ///     The timed-command scheduler's registry — its pending <c>schedule.started</c>/
    ///     <c>finished</c>/<c>failed</c>/<c>dropped</c>/<c>evicted</c> events fold into each snapshot
    ///     the same way audio's and IVA's do. Null when scheduling is disabled.
    /// </param>
    /// <param name="camera">
    ///     The camera director — its pending <c>camera.*</c> events fold into each snapshot the same way
    ///     audio's and the scheduler's do, and its captured follow target is pruned against the vessel
    ///     list this sampler just enumerated. Null when the camera surface is disabled.
    /// </param>
    /// <param name="debugNamespace">
    ///     <c>[control] debug_namespace</c>: gates the FX-editor sample (<c>/sim/debug/{engineplume,
    ///     plumetrail,clouds,terrain}</c>). Off ⇒ the families are never read and
    ///     <see cref="SimSnapshot.FxEditors"/> stays null, so no transport serves them.
    /// </param>
    internal TelemetrySampler(SnapshotStore store, TelemetrySettings settings, KsaHealth health,
        PerfStat sampleStats, ValueStat allocStats, WeldManager welds, ThugLifeManager thugLife,
        IvaPhysicsManager iva, PerfStat ivaStats, AudioStore? audio = null,
        ScheduleStore? schedules = null, CameraDirector? camera = null, bool debugNamespace = false,
        FaceFxManager? faceFx = null)
    {
        _faceFx = faceFx;
        _debugNamespace = debugNamespace;
        _store = store;
        _settings = settings;
        _appliedRateHz = settings.SampleRateHz;
        _clock = new SampleClock(_appliedRateHz);
        _health = health;
        _sampleStats = sampleStats;
        _allocStats = allocStats;
        _welds = welds;
        _thugLife = thugLife;
        _iva = iva;
        _ivaStats = ivaStats;
        _audio = audio;
        _schedules = schedules;
        _camera = camera;
    }

    /// <summary>
    ///     Per-frame tick, game thread only. <paramref name="active"/> gates the work: while the
    ///     VM is down and no transport client exists there is nobody to read <c>/sim</c>, so the
    ///     sampler idles for free (T9.1). The master <c>telemetry_enabled</c> gate idles it too.
    /// </summary>
    internal void Tick(double dt, bool active)
    {
        if (!active || !_settings.Enabled)
        {
            _clock.Reset();
            return;
        }

        // Pick up an in-game rate change (cheap int read; only touches the clock when it moved).
        var rate = _settings.SampleRateHz;
        if (rate != _appliedRateHz)
        {
            _appliedRateHz = rate;
            _clock.SetRate(rate);
        }

        if (_clock.Tick(dt))
            using (_sampleStats.Measure()) // two timestamp reads; alloc-free
            {
                // Alloc/tick tripwire (GP3): a thread-local counter read before/after — alloc-free
                // to record, and the number the status window shows as "sample alloc".
                var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                Sample();
                _allocStats.Add(GC.GetAllocatedBytesForCurrentThread() - allocBefore);
            }
    }

    [KsaAnchor("Universe.GetElapsedSeconds(); Universe.SimulationSpeed; Universe.GetLastSimStep().DeltaTime; "
            + "Program.ControlledVehicle?.Id; Universe.CurrentSystem.All.UnsafeAsList()",
        SourceFile = "KSA/Universe.cs / KSA/Program.cs / KSA/CelestialSystem.cs", Verified = "2026-08-11",
        GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Low,
        Notes = "Sampler-direct time/warp/system reads (the /sim time/* rows + the vessel enumeration). "
            + "Anchored (G4) so the census is complete — a rename errors in the sampler, still caught by "
            + "the build, just outside the actuator/reader anchor set. 5261: rev 5211 replaced SimTime "
            + "with UniverseTime (Int128 ns) and removed Universe.GetElapsedSimTime(); this now reads the "
            + "double-valued Universe.GetElapsedSeconds() directly. time/ut is unchanged in unit "
            + "(seconds) and magnitude — UniverseTime.Seconds() reconstructs whole+fraction from "
            + "nanoseconds, so /sim gains precision rather than changing meaning.")]
    private void Sample()
    {
        var ut = Sanitize.Finite(Universe.GetElapsedSeconds());
        var warp = Sanitize.Finite(Universe.SimulationSpeed);
        var activeId = Program.ControlledVehicle?.Id;
        var detail = _settings.VesselDetail;

        var vessels = new List<VesselSnapshot>(_previous?.Vessels.Count ?? 4);
        IReadOnlyList<BodySnapshot> bodies = [];
        SystemSnapshot? systemSummary = null;
        if (Universe.CurrentSystem is { } system)
        {
            foreach (var astronomical in system.All.UnsafeAsList())
            {
                if (astronomical is not Vehicle vehicle)
                    continue;
                try
                {
                    var vessel = VesselReader.Sample(vehicle, activeId, ut, detail);
                    // The parts list is its own gate + cache (the welds anchor picker), separate from
                    // the G3 detail pass so it can be enabled without the heavier per-module reads.
                    if (_settings.VesselParts)
                        vessel = vessel with { Parts = PartsReader.Sample(vehicle, ut) };
                    vessels.Add(vessel);
                }
                catch (Exception ex)
                {
                    // One vehicle mid-teardown must not kill the snapshot; log the first only.
                    if (!_vehicleErrorLogged)
                    {
                        _vehicleErrorLogged = true;
                        ModLog.Log.Debug($"telemetry: a vehicle sample failed (logged once): {ex.Message}");
                    }
                }
            }

            // Drop force-render marks whose vessel despawned (tears the render patches down when the
            // last one goes) — the welds/thug_life validation discipline, riding the vehicle
            // enumeration that just happened instead of a per-frame hook. Self-gates when unmarked.
            VesselForceRender.Prune(vessels);

            // Same discipline for the camera director's captured restore target: a vessel that
            // despawned while gatOS held the camera must not be handed back to SetFollow. Self-gates
            // while the camera is unowned.
            _camera?.Prune(vessels);

            if (_settings.Bodies)
                (bodies, systemSummary) = SampleBodiesPaced(system);
            else
                _lastBodiesTimestamp = 0; // gate off: a re-enable resamples immediately
        }

        var events = _settings.Events
            ? EventDiffer.Diff(_previous, ut, warp, activeId, vessels)
            : [];
        // Fold in pending audio.finished events (drained even when gated off, so they never pile up).
        if (_audio?.DrainEvents() is { Count: > 0 } audioEvents && _settings.Events)
            events = events.Count == 0 ? audioEvents : [.. events, .. audioEvents];
        // Same for the IVA cabin-physics events (iva.impact / iva.escape / iva.release).
        if (_iva.DrainEvents() is { Count: > 0 } ivaEvents && _settings.Events)
            events = events.Count == 0 ? ivaEvents : [.. events, .. ivaEvents];
        // Same for the scheduler's player-lifecycle events (schedule.started / finished / failed /
        // dropped / evicted) — the only way a fire-and-forget schedule reports itself.
        if (_schedules?.DrainEvents() is { Count: > 0 } scheduleEvents && _settings.Events)
            events = events.Count == 0 ? scheduleEvents : [.. events, .. scheduleEvents];
        // Same for the camera surface's events (camera.shot / camera.finished — emitted once the C3
        // track player lands; the queue is drained unconditionally so it can never pile up meanwhile).
        if (_camera?.DrainEvents() is { Count: > 0 } cameraEvents && _settings.Events)
            events = events.Count == 0 ? cameraEvents : [.. events, .. cameraEvents];
        var snapshot = new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events,
            GameVersion(), _appliedRateHz, _health.Snapshot())
        {
            SimDtSeconds = Sanitize.Finite(Universe.GetLastSimStep().DeltaTime),
            WarpSpeeds = SampleWarpSpeeds(),
            AutoWarpActive = SafeAutoWarpActive(),
            AutoWarpTargetUt = SafeAutoWarpTarget(),
            Bodies = bodies,
            System = systemSummary,
            Welds = _welds.Snapshot(),
            AlwaysRenderIva = IvaForceRender.Enabled,
            ThugLife = _thugLife.Snapshot(),
            // Also the per-sample sweep: LiveCount drops handles whose burst self-retired.
            FaceFxLive = _faceFx?.LiveCount() ?? 0,
            Iva = _iva.Snapshot(_ivaStats),
            // FX editors: gated by the debug namespace and memoized inside the reader — an idle tick
            // republishes the previous instance by reference (no KSA reads, no allocation).
            FxEditors = _debugNamespace ? SampleFxEditors() : null,
        };
        _previous = snapshot;
        _store.Publish(snapshot);
    }

    /// <summary>
    ///     Samples the FX-editor surface (plans/FX_EDITORS_PLAN.md). Never fails the tick: a family
    ///     that cannot be read is simply absent, and the memoized instance is reused while nothing
    ///     changed. Logged once on first failure, like the other per-stream readers.
    /// </summary>
    private FxEditorsSnapshot? SampleFxEditors()
    {
        try
        {
            return FxEditorReader.Sample(_health);
        }
        catch (Exception ex)
        {
            if (!_fxErrorLogged)
            {
                _fxErrorLogged = true;
                ModLog.Log.Debug($"telemetry: the fx-editor sample failed (logged once): {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    ///     Samples the celestial catalog at its own (optional) sub-cadence: when
    ///     <see cref="TelemetrySettings.BodiesRateHz"/> is below the master rate, the ticks in
    ///     between carry the previous bodies/system <b>by reference</b> — zero KSA reads, zero
    ///     allocation, and a reference-equality "unchanged" signal for consumers (GP3).
    /// </summary>
    private (IReadOnlyList<BodySnapshot>, SystemSnapshot?) SampleBodiesPaced(CelestialSystem system)
    {
        var rate = _settings.BodiesRateHz;
        var now = Stopwatch.GetTimestamp();
        if (rate > 0 && _lastBodiesTimestamp != 0 && now - _lastBodiesTimestamp < Stopwatch.Frequency / rate)
            return (_lastBodies, _lastSystem);

        var (bodies, summary) = SampleBodies(system);
        _lastBodies = bodies;
        _lastSystem = summary;
        _lastBodiesTimestamp = now;
        return (bodies, summary);
    }

    private (IReadOnlyList<BodySnapshot>, SystemSnapshot?) SampleBodies(CelestialSystem system)
    {
        try
        {
            return BodyReader.Sample(system);
        }
        catch (Exception ex)
        {
            if (!_bodyErrorLogged)
            {
                _bodyErrorLogged = true;
                ModLog.Log.Debug($"telemetry: body catalog sample failed (logged once): {ex.Message}");
            }

            return ([], null);
        }
    }

    [KsaAnchor("Universe.GetSimulationSpeeds() (SimulationSpeed.Value per warp rung)",
        SourceFile = "KSA/Universe.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "The fixed warp ladder (time/warp_speeds); cached after the first successful read.")]
    private IReadOnlyList<double> SampleWarpSpeeds()
    {
        // The warp ladder is a fixed per-session list; cache it after the first successful read so
        // we stop allocating an array (+ a LINQ enumerator and delegate) every single sample.
        if (_warpSpeeds.Count > 0)
            return _warpSpeeds;
        try
        {
            _warpSpeeds = Universe.GetSimulationSpeeds().Select(s => Sanitize.Finite(s.Value)).ToArray();
        }
        catch
        {
            _warpSpeeds = [];
        }

        return _warpSpeeds;
    }

    [KsaAnchor("Universe.IsAutoWarpActive",
        SourceFile = "KSA/Universe.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "time/auto_warp active flag.")]
    private static bool SafeAutoWarpActive()
    {
        try
        {
            return Universe.IsAutoWarpActive;
        }
        catch
        {
            return false;
        }
    }

    [KsaAnchor("Universe.AutoWarpTime?.Seconds()",
        SourceFile = "KSA/Universe.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "time/auto_warp target UT; nullable when no auto-warp scheduled.")]
    private static double SafeAutoWarpTarget()
    {
        try
        {
            return Universe.AutoWarpTime is { } t ? Sanitize.Finite(t.Seconds()) : 0;
        }
        catch
        {
            return 0;
        }
    }

    [KsaAnchor("VersionInfo.Current.VersionString",
        SourceFile = "KSA/VersionInfo.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Low,
        Notes = "status/game_version; cached after the first read.")]
    private string GameVersion()
    {
        if (_gameVersion.Length > 0)
            return _gameVersion;
        try
        {
            _gameVersion = VersionInfo.Current.VersionString;
        }
        catch
        {
            _gameVersion = "";
        }

        return _gameVersion;
    }
}
