using Brutal.Numerics;
using gatOS.GameMod.Game.Ksa;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Welds;

/// <summary>
///     The authoritative registry of active welds and their per-frame driver. Lives entirely on the
///     game thread: created/removed in the command drain (<see cref="Create"/>/<see cref="Remove"/>/…),
///     driven in the after-GUI hook (<see cref="Update"/>), and projected for telemetry by the sampler
///     (<see cref="Snapshot"/>). Empty by default — <see cref="Update"/> is a no-op until a weld exists,
///     so the feature costs nothing when unused (and needs no Harmony patch).
/// </summary>
internal sealed class WeldManager
{
    private List<WeldEntry> _welds = [];

    /// <summary>True when no welds are active — the driver's cheap early-out.</summary>
    public bool IsEmpty => _welds.Count == 0;

    /// <summary>Create/replace a weld with an explicit pose (offset meters + Euler degrees).</summary>
    public CommandResult Create(Vehicle source, Vehicle target, uint partInstanceId,
        double3 position, double3 rotationDeg, bool lockRotation)
    {
        if (Validate(source, target, partInstanceId, out var anchorPart) is { } failure)
            return failure;

        AddOrReplace(new WeldEntry
        {
            Source = source,
            Target = target,
            SourceId = source.Id,
            TargetId = target.Id,
            TargetPartInstanceId = partInstanceId,
            TargetPart = anchorPart,
            Position = position,
            RotationDeg = rotationDeg,
            Orientation = WeldEngine.EulerDegreesToQuat(rotationDeg.X, rotationDeg.Y, rotationDeg.Z),
            LockRotation = lockRotation,
        });
        return CommandResult.Ok;
    }

    /// <summary>Create/replace a weld capturing the source's CURRENT relative pose (<c>weld_here</c>).</summary>
    public CommandResult CreateAtCurrentPose(Vehicle source, Vehicle target, uint partInstanceId, bool lockRotation)
    {
        if (Validate(source, target, partInstanceId, out var anchorPart) is { } failure)
            return failure;

        var (position, orientation, rotationDeg) = WeldEngine.CapturePose(source, target, anchorPart);
        AddOrReplace(new WeldEntry
        {
            Source = source,
            Target = target,
            SourceId = source.Id,
            TargetId = target.Id,
            TargetPartInstanceId = partInstanceId,
            TargetPart = anchorPart,
            Position = position,
            Orientation = orientation,
            RotationDeg = rotationDeg,
            LockRotation = lockRotation,
        });
        return CommandResult.Ok;
    }

    /// <summary>Remove the weld whose source is <paramref name="sourceId"/>.</summary>
    public CommandResult Remove(string sourceId)
        => _welds.RemoveAll(w => w.SourceId == sourceId) > 0
            ? CommandResult.Ok
            : new CommandResult(CommandOutcome.NotFound, $"'{sourceId}' is not welded");

    /// <summary>Suspend/resume the weld whose source is <paramref name="sourceId"/> (keeps the entry).</summary>
    public CommandResult SetEnabled(string sourceId, bool enabled)
    {
        var entry = _welds.FirstOrDefault(w => w.SourceId == sourceId);
        if (entry is null)
            return new CommandResult(CommandOutcome.NotFound, $"'{sourceId}' is not welded");
        entry.Enabled = enabled;
        return CommandResult.Ok;
    }

    /// <summary>Remove every weld.</summary>
    public CommandResult Clear()
    {
        _welds.Clear();
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Game-thread driver, called once per frame from the after-GUI hook (after the vehicle-solver
    ///     workers have had the render to finish). Drains them, then teleports each source to its anchor.
    /// </summary>
    [KsaAnchor("JobSystems.VehicleSolvers.Wait()", SourceFile = "KSA/Universe.cs / KSA/JobScheduler.cs",
        Verified = "2026-06-28", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Drains in-flight vehicle-solver workers before the weld teleports mutate vehicle state.")]
    public void Update(double dt)
    {
        if (_welds.Count == 0)
            return;

        // Cheap once the workers (queued in PrepareFrame) have finished, which they have by this point.
        JobSystems.VehicleSolvers.Wait();

        List<WeldEntry>? toRemove = null;
        foreach (var entry in _welds)
        {
            try
            {
                if (!IsLive(entry.Source) || !IsLive(entry.Target))
                {
                    (toRemove ??= []).Add(entry);
                    continue;
                }

                // Re-resolve the anchor each tick: robust to the target being edited/staged. A removed
                // anchor part falls back to body-frame anchoring rather than dropping the weld.
                if (entry.TargetPartInstanceId != 0)
                    entry.TargetPart = FindPart(entry.Target, entry.TargetPartInstanceId);

                if (!WeldEngine.UpdateWeld(entry))
                    (toRemove ??= []).Add(entry);
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"gatOS weld '{entry.SourceId}'→'{entry.TargetId}' update error: {ex.Message}");
                (toRemove ??= []).Add(entry);
            }
        }

        if (toRemove is null)
            return;
        foreach (var entry in toRemove)
            _welds.Remove(entry);
    }

    /// <summary>Immutable projection for the <c>/sim/debug/welds</c> registry view (game thread).</summary>
    public IReadOnlyList<WeldSnapshot> Snapshot()
    {
        if (_welds.Count == 0)
            return [];
        var list = new List<WeldSnapshot>(_welds.Count);
        foreach (var w in _welds)
            list.Add(new WeldSnapshot(
                w.SourceId, w.TargetId, w.TargetPartInstanceId,
                new double3Snap(w.Position.X, w.Position.Y, w.Position.Z),
                new double3Snap(w.RotationDeg.X, w.RotationDeg.Y, w.RotationDeg.Z),
                w.LockRotation, w.Enabled));
        return list;
    }

    private static CommandResult? Validate(Vehicle source, Vehicle target, uint partInstanceId,
        out Part? anchorPart)
    {
        anchorPart = null;
        if (ReferenceEquals(source, target))
            return new CommandResult(CommandOutcome.Busy, "cannot weld a vessel to itself");
        if (source.Parent != target.Parent)
            return new CommandResult(CommandOutcome.Busy, "source and target orbit different bodies");
        if (partInstanceId != 0)
        {
            anchorPart = FindPart(target, partInstanceId);
            if (anchorPart is null)
                return new CommandResult(CommandOutcome.NotFound,
                    $"part {partInstanceId} not found on '{target.Id}'");
        }

        return null;
    }

    private void AddOrReplace(WeldEntry entry)
    {
        _welds.RemoveAll(w => w.SourceId == entry.SourceId);
        _welds.Add(entry);
        _welds = WeldEngine.TopologicalSort(_welds); // keep targets ahead of their dependents (chains)
    }

    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.Parts; Part.InstanceId",
        SourceFile = "KSA/Universe.cs / KSA/Part.cs", Verified = "2026-06-28", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Low, Notes = "Liveness check + anchor-part re-resolution for the weld driver.")]
    private static bool IsLive(Vehicle vehicle)
    {
        if (Universe.CurrentSystem is not { } system)
            return false;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (ReferenceEquals(astronomical, vehicle))
                return true;
        return false;
    }

    private static Part? FindPart(Vehicle vehicle, uint instanceId)
    {
        foreach (var part in vehicle.Parts.Parts)
            if (part.InstanceId == instanceId)
                return part;
        return null;
    }
}
