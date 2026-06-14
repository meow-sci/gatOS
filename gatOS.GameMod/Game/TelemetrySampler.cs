using gatOS.GameMod.Game.Ksa;
using gatOS.GameMod.Game.Ksa.Readers;
using gatOS.Logging;
using gatOS.SimFs;
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
    private int _appliedRateHz;
    private IReadOnlyList<double> _warpSpeeds = [];
    private SimSnapshot? _previous;
    private long _sequence;
    private bool _vehicleErrorLogged;
    private bool _bodyErrorLogged;
    private string _gameVersion = "";

    /// <param name="store">The exchange the 9p tree reads from.</param>
    /// <param name="settings">
    ///     The runtime-mutable cadence + per-stream gates (seeded from config, retuned in-game).
    ///     Read every tick so a menu change takes effect on the next sample.
    /// </param>
    /// <param name="health">Accessor-health latches, shared with the command executor.</param>
    /// <param name="sampleStats">Timing accumulator for one <see cref="Sample"/> (the status window reads it).</param>
    internal TelemetrySampler(SnapshotStore store, TelemetrySettings settings, KsaHealth health, PerfStat sampleStats)
    {
        _store = store;
        _settings = settings;
        _appliedRateHz = settings.SampleRateHz;
        _clock = new SampleClock(_appliedRateHz);
        _health = health;
        _sampleStats = sampleStats;
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
                Sample();
    }

    private void Sample()
    {
        var ut = Sanitize.Finite(Universe.GetElapsedSimTime().Seconds());
        var warp = Sanitize.Finite(Universe.SimulationSpeed);
        var activeId = Program.ControlledVehicle?.Id;
        var detail = _settings.VesselDetail;

        var vessels = new List<VesselSnapshot>();
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
                    vessels.Add(VesselReader.Sample(vehicle, activeId, ut, detail));
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

            if (_settings.Bodies)
                (bodies, systemSummary) = SampleBodies(system);
        }

        var events = _settings.Events
            ? EventDiffer.Diff(_previous, ut, warp, activeId, vessels)
            : [];
        var snapshot = new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events,
            GameVersion(), _appliedRateHz, _health.Snapshot())
        {
            SimDtSeconds = Sanitize.Finite(Universe.GetLastSimStep().DeltaTime),
            WarpSpeeds = SampleWarpSpeeds(),
            AutoWarpActive = SafeAutoWarpActive(),
            AutoWarpTargetUt = SafeAutoWarpTarget(),
            Bodies = bodies,
            System = systemSummary,
        };
        _previous = snapshot;
        _store.Publish(snapshot);
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
