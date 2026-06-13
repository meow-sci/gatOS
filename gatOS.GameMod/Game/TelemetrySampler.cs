using gatOS.GameMod.Game.Ksa;
using gatOS.GameMod.Game.Ksa.Readers;
using gatOS.Logging;
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
    private readonly double _rateHz;
    private SimSnapshot? _previous;
    private long _sequence;
    private bool _vehicleErrorLogged;
    private bool _bodyErrorLogged;
    private string _gameVersion = "";

    /// <param name="store">The exchange the 9p tree reads from.</param>
    /// <param name="rateHz">Config <c>sample_rate_hz</c> (already clamped to 1–120).</param>
    /// <param name="health">Accessor-health latches, shared with the command executor.</param>
    internal TelemetrySampler(SnapshotStore store, double rateHz, KsaHealth health)
    {
        _store = store;
        _clock = new SampleClock(rateHz);
        _health = health;
        _rateHz = rateHz;
    }

    /// <summary>
    ///     Per-frame tick, game thread only. <paramref name="active"/> gates the work: while the
    ///     VM is down and no 9p session exists there is nobody to read <c>/sim</c>, so the sampler
    ///     idles for free (T9.1).
    /// </summary>
    internal void Tick(double dt, bool active)
    {
        if (!active)
        {
            _clock.Reset();
            return;
        }

        if (_clock.Tick(dt))
            Sample();
    }

    private void Sample()
    {
        var ut = Sanitize.Finite(Universe.GetElapsedSimTime().Seconds());
        var warp = Sanitize.Finite(Universe.SimulationSpeed);
        var activeId = Program.ControlledVehicle?.Id;

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
                    vessels.Add(VesselReader.Sample(vehicle, activeId, ut));
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

            (bodies, systemSummary) = SampleBodies(system);
        }

        var events = EventDiffer.Diff(_previous, ut, warp, activeId, vessels);
        var snapshot = new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events,
            GameVersion(), _rateHz, _health.Snapshot())
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

    private static IReadOnlyList<double> SampleWarpSpeeds()
    {
        try
        {
            return Universe.GetSimulationSpeeds().Select(s => Sanitize.Finite(s.Value)).ToArray();
        }
        catch
        {
            return [];
        }
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
