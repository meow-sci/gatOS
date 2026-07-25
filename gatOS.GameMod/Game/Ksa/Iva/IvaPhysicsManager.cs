using System.Numerics;
using BepuPhysics;
using Brutal.Numerics;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Iva;

/// <summary>
///     The authoritative registry of free-floating cabin objects, their per-vessel simulations, and
///     the per-frame driver (plans/IVA_MOVEMENTS.md). Lives entirely on the game thread: switched on
///     and mutated in the command drain, driven from the after-GUI hook (<see cref="Update"/>), and
///     projected for telemetry by the sampler (<see cref="Snapshot"/>).
/// </summary>
/// <remarks>
///     <para>
///         <b>Off by default, and off means nothing exists.</b> While <see cref="Enabled"/> is false
///         there is no physics simulation, no interior geometry, no buffer pool and no per-frame work
///         — <see cref="Update"/> is a single branch, and no Bepu type is ever even loaded (the Bepu
///         fields live on <see cref="CabinSim"/>, which is not constructed until the first adopt).
///         Writing <c>0</c> to <c>/sim/debug/iva/enabled</c> releases every object at its exact rest
///         pose and disposes every simulation, returning to that state.
///     </para>
///     <para>
///         <b>Why gatOS runs its own physics world.</b> KSA represents each vehicle as a single
///         dynamic Bepu body whose shape is a handful of coarse convex primitives approximating the
///         <i>outside</i> of the whole vehicle (a Gemini pod is a 2 m cylinder plus a 0.89 m sphere —
///         a solid blob), and its <c>NarrowPhaseCallbacks.AllowContactGeneration</c> permits
///         dynamic↔dynamic pairs unconditionally. Anything placed where a crew member sits is
///         therefore deep inside the vehicle's own collider: penetration recovery would eject it
///         through the hull <i>and</i> the contact constraint would shove the actual spacecraft.
///         Worse, KSA only steps that simulation when <c>IsAnyConstrained</c> — never for a coasting
///         vessel, which is the case this feature is for. Hence a separate, gatOS-owned simulation in
///         the assembly frame, which cannot perturb the vessel, cannot corrupt a save and cannot race
///         the solver.
///     </para>
///     <para>
///         Coupling is deliberately one-way (vessel → objects). A 0.1 kg pen hitting a 1800 kg capsule
///         changes its trajectory by ~5×10⁻⁵ of the pen's Δv; feeding that back would risk perturbing
///         a saved, on-rails orbit for no visible gain.
///     </para>
/// </remarks>
internal sealed class IvaPhysicsManager
{
    private readonly List<CabinVessel> _vessels = [];
    private readonly List<SimEvent> _pendingEvents = [];

    private CabinTuning _tuning = CabinTuning.Default;
    private int _lastSubsteps;
    private bool _parked;
    private string _parkReason = "";
    private bool _driveFaultLogged;
    private IvaSnapshot? _idleSnapshot;

    /// <summary>
    ///     The master switch (<c>/sim/debug/iva/enabled</c>). False is the shipped default and the
    ///     state after teardown; see the remarks on this type for what "off" guarantees.
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether the sim keeps stepping while no viewport is in the IVA camera.</summary>
    public bool RunOutsideIva { get; private set; }

    /// <summary>
    ///     True when the driver has nothing to do — the cheap early-out the per-frame hook checks
    ///     before any KSA call. Game thread.
    /// </summary>
    public bool IsIdle => !Enabled || _vessels.Count == 0;

    /// <summary>Seeds the tuning from config at startup; a later <c>enabled=1</c> picks it up.</summary>
    public void Configure(CabinTuning tuning) => _tuning = tuning;

    /// <summary>Seeds <see cref="RunOutsideIva"/> from config (no side effects; nothing runs yet).</summary>
    public void SeedRunOutsideIva(bool value) => RunOutsideIva = value;

    // ---- master switch ------------------------------------------------------------------------

    /// <summary>
    ///     Starts or ends the whole feature. Turning it off releases every object (restoring exact rest
    ///     poses) and disposes every simulation, so the mod returns to costing nothing. Idempotent.
    ///     Game thread only (the Frame command drain).
    /// </summary>
    public CommandResult SetEnabled(bool on)
    {
        if (Enabled == on)
            return CommandResult.Ok;

        Enabled = on;
        if (on)
        {
            ModLog.Log.Info("gatOS IVA physics enabled (no objects yet — adopt a SubPart to start).");
            return CommandResult.Ok;
        }

        var released = ReleaseAll();
        ModLog.Log.Info($"gatOS IVA physics disabled; {released} object(s) restored, "
                        + "every cabin simulation disposed.");
        return CommandResult.Ok;
    }

    /// <summary>Whether the sim keeps running outside the IVA camera. Game thread only.</summary>
    public CommandResult SetRunOutsideIva(bool on)
    {
        RunOutsideIva = on;
        return CommandResult.Ok;
    }

    // ---- adopt / release ----------------------------------------------------------------------

    /// <summary>
    ///     Cuts one SubPart loose: captures its rest pose, sizes a collision proxy from its mesh, and
    ///     adds a rigid body seeded at its current pose (plus an optional starting velocity, in the
    ///     vessel assembly frame). Game thread only.
    /// </summary>
    [KsaAnchor("Vehicle.{Id,Parts}; Part.{InstanceId,SubParts,PartParent,DisplayName,Template.Id,"
            + "PositionParentAsmb,Asmb2ParentAsmb,Scale,Modules}",
        SourceFile = "KSA/Vehicle.cs / KSA/Part.cs", Verified = "2026-07-24",
        GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Low,
        Notes = "SubPart lookup + rest-pose capture for an IVA floating object. Top-level parts are "
            + "REFUSED: their transform is serialized into the save, a SubPart's is not.")]
    public CommandResult Adopt(Vehicle vehicle, uint subPartInstanceId, double3 velocity)
    {
        if (!Enabled)
            return new CommandResult(CommandOutcome.Unsupported,
                "IVA physics is off — echo 1 > /sim/debug/iva/enabled first");

        var part = FindSubPart(vehicle, subPartInstanceId);
        if (part is null)
            return new CommandResult(CommandOutcome.NotFound,
                $"subpart {subPartInstanceId} not found on '{vehicle.Id}' "
                + "(top-level parts cannot float — their transform is saved)");

        var cabin = GetOrCreateVessel(vehicle);
        if (cabin.Objects.Count >= _tuning.MaxObjectsPerVessel)
            return Fail(cabin, new CommandResult(CommandOutcome.Busy,
                $"'{vehicle.Id}' already has {cabin.Objects.Count} floating objects "
                + $"(iva_max_objects = {_tuning.MaxObjectsPerVessel})"));
        if (cabin.Objects.Any(o => o.PartInstanceId == subPartInstanceId))
            return Fail(cabin,
                new CommandResult(CommandOutcome.Busy, $"subpart {subPartInstanceId} already floats"));

        if (!TryMeasure(part, out var size, out var center))
            return Fail(cabin, new CommandResult(CommandOutcome.Unsupported,
                $"subpart {subPartInstanceId} has no CPU mesh to size a collision proxy from"));
        var extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (extent > _tuning.MaxObjectSize)
            return Fail(cabin, new CommandResult(CommandOutcome.Invalid,
                $"subpart {subPartInstanceId} is {extent:0.###} m across — bigger than "
                + $"iva_max_object_size ({_tuning.MaxObjectSize:0.###} m); it is cabin structure, not a prop"));

        var sim = EnsureSim(cabin);
        var mass = Math.Max(0.001, _tuning.DensityKgM3 * size.X * size.Y * size.Z);
        var (position, orientation) = FloatingObject.ReadBodyPose(part, center);
        var handle = sim.AddBox(size, mass, position, orientation,
            new Vector3((float)velocity.X, (float)velocity.Y, (float)velocity.Z));
        if (handle is not { } body)
            return Fail(cabin, new CommandResult(CommandOutcome.Fault, "the cabin simulation is unavailable"));

        cabin.Objects.Add(new FloatingObject
        {
            Id = SmallestFreeId(),
            VesselId = vehicle.Id,
            Part = part,
            PartInstanceId = subPartInstanceId,
            Name = part.DisplayName,
            Template = part.Template.Id,
            Body = body,
            Size = size,
            MassKg = mass,
            ShapeOffsetLocal = center,
            RestPosition = part.PositionParentAsmb,
            RestOrientation = part.Asmb2ParentAsmb,
            Position = position,
        });
        // The adopted subpart must leave the static interior: an object cannot collide with a frozen
        // copy of itself.
        cabin.InteriorDirty = true;
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Cuts loose the smallest eligible loose props on a vessel, up to <paramref name="max"/>
    ///     (0 = the per-vessel cap) and optionally filtered by a template substring. Smallest-first is
    ///     the heuristic that makes this do the obvious thing: bolts, screws, notes and sardine tins
    ///     float before anything structural gets near the cap. Game thread only.
    /// </summary>
    public CommandResult AdoptAll(Vehicle vehicle, int max, string? templateFilter)
    {
        if (!Enabled)
            return new CommandResult(CommandOutcome.Unsupported,
                "IVA physics is off — echo 1 > /sim/debug/iva/enabled first");

        var cabin = GetOrCreateVessel(vehicle);
        var budget = max > 0
            ? Math.Min(max, _tuning.MaxObjectsPerVessel - cabin.Objects.Count)
            : _tuning.MaxObjectsPerVessel - cabin.Objects.Count;
        if (budget <= 0)
            return Fail(cabin, new CommandResult(CommandOutcome.Busy,
                $"'{vehicle.Id}' is already at the {_tuning.MaxObjectsPerVessel}-object cap"));

        var candidates = new List<(double Extent, uint InstanceId)>();
        foreach (var part in vehicle.Parts.Parts)
        foreach (var sub in part.SubParts)
        {
            if (cabin.Objects.Any(o => o.PartInstanceId == sub.InstanceId))
                continue;
            if (templateFilter is { Length: > 0 } filter
                && sub.Template.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!IsInteriorProp(sub) || !TryMeasure(sub, out var size, out _))
                continue;
            var extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (extent > _tuning.MaxObjectSize)
                continue;
            candidates.Add((extent, sub.InstanceId));
        }

        if (candidates.Count == 0)
            return Fail(cabin, new CommandResult(CommandOutcome.NotFound,
                $"no adoptable interior props on '{vehicle.Id}'"
                + (templateFilter is { Length: > 0 } f ? $" matching '{f}'" : "")));

        candidates.Sort(static (a, b) => a.Extent.CompareTo(b.Extent));
        var adopted = 0;
        foreach (var (_, instanceId) in candidates)
        {
            if (adopted >= budget)
                break;
            if (Adopt(vehicle, instanceId, double3.Zero).IsSuccess)
                adopted++;
        }

        return adopted > 0
            ? CommandResult.Ok
            : Fail(cabin, new CommandResult(CommandOutcome.Fault, "no candidate could be adopted"));
    }

    /// <summary>Un-adopts one object: restores its exact rest pose and drops the body. Game thread only.</summary>
    public CommandResult Release(int id)
    {
        foreach (var cabin in _vessels)
        foreach (var obj in cabin.Objects)
            if (obj.Id == id)
            {
                ReleaseObject(cabin, obj);
                cabin.Objects.Remove(obj);
                cabin.InteriorDirty = true;
                if (cabin.Objects.Count == 0)
                    DisposeVessel(cabin);
                return CommandResult.Ok;
            }

        return new CommandResult(CommandOutcome.NotFound, $"iva object {id} is gone");
    }

    /// <summary>Releases every object on every vessel (the sim itself stays enabled). Game thread only.</summary>
    public CommandResult Clear()
    {
        ReleaseAll();
        return CommandResult.Ok;
    }

    /// <summary>Adds to one object's velocity, in the vessel assembly frame, m/s. Game thread only.</summary>
    public CommandResult Nudge(int id, double3 deltaVelocity)
    {
        foreach (var cabin in _vessels)
        foreach (var obj in cabin.Objects)
            if (obj.Id == id)
                return cabin.Sim?.TryNudge(obj.Body,
                    new Vector3((float)deltaVelocity.X, (float)deltaVelocity.Y, (float)deltaVelocity.Z)) == true
                    ? CommandResult.Ok
                    : new CommandResult(CommandOutcome.Fault, $"iva object {id} has no live body");

        return new CommandResult(CommandOutcome.NotFound, $"iva object {id} is gone");
    }

    // ---- the per-frame driver -----------------------------------------------------------------

    /// <summary>
    ///     Game-thread driver, once per frame from the after-GUI hook — after the vehicle-solver
    ///     workers have finished, so <c>AccelerationBody</c>/<c>BodyRates</c>/<c>CenterOfMassAsmb</c>
    ///     are the settled values for this step. Self-gates to nothing when the feature is off or
    ///     nothing is adopted.
    /// </summary>
    /// <remarks>
    ///     Deliberately <b>not</b> hooked into <c>Universe.ExecuteNextVehicleSolvers</c>: that runs
    ///     inside the physics step, where <c>dt</c> follows time warp. Cabin physics tracks wall-clock
    ///     — it is what the player is looking at, not part of the flight simulation.
    /// </remarks>
    [KsaAnchor("JobSystems.VehicleSolvers.Wait(); Universe.{CurrentSystem.All.UnsafeAsList,SimulationSpeed}; "
            + "Program.{Editor,MainViewport}; Viewport.Mode; CameraMode.IVA; "
            + "Vehicle.{Id,AccelerationBody,AngularAccelerationBody,BodyRates,CenterOfMassAsmb,Parts.Count}",
        SourceFile = "KSA/Universe.cs / KSA/Program.cs / KSA/Viewport.cs / KSA/Vehicle.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Low,
        Notes = "The IVA cabin driver's forcing-term reads. AccelerationBody is a true accelerometer "
            + "in every flight situation (VehicleUpdateTask: zero in Freefall, GM/r² normal force when "
            + "Landed/Floating, thrust+drag when Maneuvering; normalized to m/s² at the end of the "
            + "step), which is why one formula covers pad, coast, burn and landing. Parks in the "
            + "editor: Program.Editor != null disables Part's transform caching.")]
    public void Update(double dt)
    {
        if (IsIdle)
            return;

        // Cheap once the solver workers (queued in PrepareFrame) have finished, which they have by
        // this point in the frame — the same drain the welds driver does before mutating state.
        JobSystems.VehicleSolvers.Wait();

        _parkReason = ParkReason();
        _parked = _parkReason.Length > 0;
        _lastSubsteps = 0;

        for (var i = _vessels.Count - 1; i >= 0; i--)
        {
            var cabin = _vessels[i];
            try
            {
                if (!IsLive(cabin.Vehicle))
                {
                    // The vessel is gone: the parts went with it, so drop the bodies without trying
                    // to restore a rest pose onto a dead part tree.
                    DisposeVessel(cabin);
                    _vessels.RemoveAt(i);
                    continue;
                }

                DriveVessel(cabin, dt);
                if (cabin.Objects.Count == 0)
                {
                    DisposeVessel(cabin);
                    _vessels.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                if (!_driveFaultLogged)
                {
                    _driveFaultLogged = true;
                    ModLog.Log.Warn($"gatOS IVA physics: '{cabin.VesselId}' dropped after an error "
                                    + $"(logged once): {ex.Message}");
                }

                DisposeVessel(cabin);
                _vessels.RemoveAt(i);
            }
        }
    }

    private void DriveVessel(CabinVessel cabin, double dt)
    {
        // Drop objects whose SubPart was staged/decoupled away before touching any transform.
        for (var i = cabin.Objects.Count - 1; i >= 0; i--)
        {
            var obj = cabin.Objects[i];
            if (FindSubPart(cabin.Vehicle, obj.PartInstanceId) is not null)
                continue;
            cabin.Sim?.Remove(obj.Body);
            cabin.Objects.RemoveAt(i);
            cabin.InteriorDirty = true;
            Emit("iva.release", cabin.VesselId, $"object {obj.Id} ({obj.Name}) lost its part");
        }

        if (cabin.Objects.Count == 0 || cabin.Sim is not { } sim)
            return;

        RebuildInteriorIfNeeded(cabin, sim);

        if (_parked)
        {
            sim.Park();
        }
        else
        {
            var vehicle = cabin.Vehicle;
            var field = new CabinField
            {
                ProperAcceleration = SafeVector(vehicle.AccelerationBody),
                BodyRates = SafeVector(vehicle.BodyRates),
                AngularAcceleration = SafeVector(vehicle.AngularAccelerationBody),
                CenterOfMass = SafeVector(vehicle.CenterOfMassAsmb),
            };
            sim.Step(dt, field, cabin.OnSpeedChange);
            _lastSubsteps = Math.Max(_lastSubsteps, sim.LastSubsteps);
            DrainImpacts(cabin);
        }

        // Publish poses onto the driven SubParts (and read back state for /sim).
        var interior = cabin.Interior;
        var margin = (float)_tuning.LeashMargin;
        foreach (var obj in cabin.Objects)
        {
            if (!sim.TryRead(obj.Body, out var position, out var orientation, out var velocity,
                    out var angularVelocity, out var asleep))
                continue;

            // Leash (plans/IVA_MOVEMENTS.md R3): art meshes are thin and gapped, so a fast object can
            // still slip through one. Put an escapee back in the middle of the cabin rather than let
            // it drift off into the scene.
            if (interior is not null && !interior.Contains(position, margin))
            {
                sim.TryReset(obj.Body, interior.Center);
                position = interior.Center;
                orientation = Quaternion.Identity;
                velocity = default;
                angularVelocity = default;
                Emit("iva.escape", cabin.VesselId, $"object {obj.Id} ({obj.Name}) left the cabin; recentred");
            }

            obj.Position = position;
            obj.Velocity = velocity;
            obj.AngularVelocity = angularVelocity;
            obj.Asleep = asleep;
            obj.ApplyPose(position, orientation);
        }
    }

    private void RebuildInteriorIfNeeded(CabinVessel cabin, CabinSim sim)
    {
        // Rebuild on a part-count change (the cheap "vehicle was edited" signal — KSA exposes no
        // part-tree version) or whenever the adopted set changed, which alters the exclusions.
        //
        // Deliberately NOT on a timer, unlike PartsReader's 10 s backstop: that rebuilds a list of
        // records, whereas this rebuilds a Bepu bounding tree over tens of thousands of triangles. A
        // periodic multi-millisecond hitch is far worse than the geometry going stale after the rare
        // count-preserving interior edit — and toggling `enabled` off/on rebuilds it on demand.
        var liveCount = cabin.Vehicle.Parts.Count;
        if (!cabin.InteriorDirty && cabin.PartCount == liveCount)
            return;

        cabin.Excluded.Clear();
        foreach (var obj in cabin.Objects)
            cabin.Excluded.Add(obj.PartInstanceId);

        var result = InteriorGeometry.Build(cabin.Vehicle, cabin.Excluded, _tuning.DoubleSidedInterior);
        sim.SetInterior(result.Vertices);
        cabin.Interior = result;
        cabin.InteriorDirty = false;
        cabin.PartCount = liveCount;
    }

    private void DrainImpacts(CabinVessel cabin)
    {
        if (cabin.Impacts.Count == 0)
            return;
        foreach (var (handle, speed) in cabin.Impacts)
        {
            if (speed < _tuning.ImpactSpeed)
                continue;
            foreach (var obj in cabin.Objects)
                if (obj.Body.Value == handle)
                {
                    Emit("iva.impact", cabin.VesselId,
                        $"object {obj.Id} ({obj.Name}) hit at {speed:0.###} m/s");
                    break;
                }
        }

        cabin.Impacts.Clear();
    }

    // ---- projection ---------------------------------------------------------------------------

    /// <summary>Immutable projection for the <c>/sim/debug/iva</c> view (game thread, from the sampler).</summary>
    public IvaSnapshot Snapshot(PerfStat driveStats)
    {
        // Nothing running: report the two flags (a guest must be able to read back what it wrote,
        // and the config seed, while the feature is dark) from a memo, so the idle path allocates
        // nothing per sample tick — the GP3 alloc tripwire watches this.
        if (_vessels.Count == 0)
            return _idleSnapshot is { } memo && memo.Enabled == Enabled && memo.RunOutsideIva == RunOutsideIva
                ? memo
                : _idleSnapshot = new IvaSnapshot(Enabled, RunOutsideIva, [], [], IvaStatsSnapshot.Zero);

        var objects = new List<IvaObjectSnapshot>();
        var interiors = new List<IvaInteriorSnapshot>();
        var sleeping = 0;
        foreach (var cabin in _vessels)
        {
            if (cabin.Interior is { } interior)
                interiors.Add(new IvaInteriorSnapshot(cabin.VesselId, interior.Triangles,
                    interior.SourceParts, ToSnap(interior.Min), ToSnap(interior.Max), interior.Fallback));
            foreach (var obj in cabin.Objects)
            {
                if (obj.Asleep)
                    sleeping++;
                objects.Add(new IvaObjectSnapshot(
                    obj.Id, obj.VesselId, obj.PartInstanceId, obj.Name, obj.Template,
                    ToSnap(obj.Position), ToSnap(obj.Velocity), ToSnap(obj.AngularVelocity),
                    Sanitize.Finite(obj.MassKg), "box", ToSnap(obj.Size), obj.Asleep));
            }
        }

        objects.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return new IvaSnapshot(Enabled, RunOutsideIva, objects, interiors, new IvaStatsSnapshot(
            _vessels.Count, objects.Count, sleeping, _lastSubsteps,
            Sanitize.Finite(driveStats.AvgMicros / 1000.0), Sanitize.Finite(driveStats.MaxMicros / 1000.0),
            _parked, _parkReason));
    }

    /// <summary>
    ///     Drains queued <c>iva.impact</c>/<c>iva.escape</c>/<c>iva.release</c> events for the snapshot
    ///     (the same pattern the audio store uses). Game thread only.
    /// </summary>
    public IReadOnlyList<SimEvent> DrainEvents()
    {
        if (_pendingEvents.Count == 0)
            return [];
        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return events;
    }

    /// <summary>
    ///     Unload teardown: releases everything and disposes every simulation. Safe to call twice, and
    ///     safe when the feature was never switched on. Game thread only.
    /// </summary>
    public void Teardown()
    {
        ReleaseAll();
        Enabled = false;
    }

    // ---- internals ----------------------------------------------------------------------------

    private int ReleaseAll()
    {
        var released = 0;
        foreach (var cabin in _vessels)
        {
            foreach (var obj in cabin.Objects)
            {
                ReleaseObject(cabin, obj);
                released++;
            }

            cabin.Objects.Clear();
            DisposeVessel(cabin);
        }

        _vessels.Clear();
        return released;
    }

    private void ReleaseObject(CabinVessel cabin, FloatingObject obj)
    {
        try
        {
            cabin.Sim?.Remove(obj.Body);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS iva: body removal error: {ex.Message}");
        }

        try
        {
            // Only if the part is still in the tree — restoring onto a staged-away part is pointless
            // and its parent may already be gone.
            if (FindSubPart(cabin.Vehicle, obj.PartInstanceId) is not null)
                obj.RestoreRestPose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS iva: rest-pose restore error: {ex.Message}");
        }
    }

    private static void DisposeVessel(CabinVessel cabin)
    {
        cabin.Sim?.Dispose();
        cabin.Sim = null;
        cabin.Interior = null;
        cabin.InteriorDirty = true;
        cabin.PartCount = -1;
    }

    /// <summary>
    ///     Returns <paramref name="failure"/>, dropping the cabin entry first when the failed operation
    ///     was what created it — so a rejected adopt leaves the registry exactly as it found it rather
    ///     than an empty cabin for the driver to clean up a frame later.
    /// </summary>
    private CommandResult Fail(CabinVessel cabin, CommandResult failure)
    {
        if (cabin.Objects.Count == 0)
        {
            DisposeVessel(cabin);
            _vessels.Remove(cabin);
        }

        return failure;
    }

    private CabinVessel GetOrCreateVessel(Vehicle vehicle)
    {
        foreach (var cabin in _vessels)
            if (ReferenceEquals(cabin.Vehicle, vehicle))
                return cabin;
        var created = new CabinVessel { Vehicle = vehicle, VesselId = vehicle.Id };
        _vessels.Add(created);
        return created;
    }

    private CabinSim EnsureSim(CabinVessel cabin) => cabin.Sim ??= new CabinSim(_tuning);

    private int SmallestFreeId()
    {
        var id = 0;
        while (_vessels.Any(v => v.Objects.Any(o => o.Id == id)))
            id++;
        return id;
    }

    private void Emit(string type, string vesselId, string detail)
    {
        if (_pendingEvents.Count > 256)
            return; // a stuck object must not grow the queue without bound
        _pendingEvents.Add(new SimEvent(SafeUt(), type, vesselId, detail));
    }

    /// <summary>
    ///     Why the cabin is parked, or "" while it runs. Parking zeroes velocities and freezes poses,
    ///     which is much better behaved than integrating a 1000× warp step or a zero-dt editor frame.
    ///     A read fault parks rather than propagating: freezing is always safe, and a gate read must
    ///     never be what disables the feature for the session.
    /// </summary>
    private string ParkReason()
    {
        try
        {
            return ParkReasonCore();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS iva: park-gate read failed, parking: {ex.Message}");
            return "unknown";
        }
    }

    private string ParkReasonCore()
    {
        // Q5: Program.Editor != null disables Part's transform caching entirely — the feature must be
        // inert in the VAB.
        if (Program.Editor is not null)
            return "editor";
        var speed = Universe.SimulationSpeed;
        // Paused: the vessel's kinematics are frozen, so integrating wall-clock time would have props
        // keep falling in a stopped world.
        if (!(speed > 0))
            return "paused";
        if (speed > 1.0001)
            return "warp";
        if (!RunOutsideIva && Program.MainViewport.Mode != CameraMode.IVA)
            return "not-iva";
        return "";
    }

    /// <summary>
    ///     Sizes a box collision proxy from the SubPart's own CPU mesh: full extents (scaled by the
    ///     part's scale, floored at <c>MinObjectSize</c> so a photo still has thickness) plus the
    ///     bounding box's centre in part-local coordinates, which is what keeps the rendered model
    ///     centred on the proxy.
    /// </summary>
    [KsaAnchor("Part.{Modules,Scale}; ModuleList.Get<PartModelModule>(); PartModelModule.PartModel.Template; "
            + "PartModelModule.Template.Mesh; MeshReference.PositionCompare",
        SourceFile = "KSA/Part.cs / KSA/PartModelModule.cs / KSA/MeshReference.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Medium,
        Notes = "Collision-proxy sizing from the retained CPU triangle soup. Box only in this build: "
            + "Bepu's Box needs no shape-local rotation, so the body orientation IS the part "
            + "orientation — convex hulls are the documented later refinement.")]
    private bool TryMeasure(Part part, out Vector3 size, out Vector3 center)
    {
        size = default;
        center = default;
        var models = part.Modules.Get<PartModelModule>();
        if (models.Length == 0 || models[0].PartModel.Template.Mesh?.PositionCompare is not { Length: >= 3 } soup)
            return false;

        var min = new double3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new double3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var v in soup)
        {
            if (!double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z))
                continue;
            min = double3.Min(min, v);
            max = double3.Max(max, v);
        }

        if (min.X > max.X)
            return false;

        var scale = part.Scale;
        var floor = _tuning.MinObjectSize;
        size = new Vector3(
            (float)Math.Max(floor, (max.X - min.X) * Math.Abs(NonZero(scale.X))),
            (float)Math.Max(floor, (max.Y - min.Y) * Math.Abs(NonZero(scale.Y))),
            (float)Math.Max(floor, (max.Z - min.Z) * Math.Abs(NonZero(scale.Z))));
        center = new Vector3(
            (float)((min.X + max.X) * 0.5 * NonZero(scale.X)),
            (float)((min.Y + max.Y) * 0.5 * NonZero(scale.Y)),
            (float)((min.Z + max.Z) * 0.5 * NonZero(scale.Z)));
        return float.IsFinite(size.X) && float.IsFinite(size.Y) && float.IsFinite(size.Z)
                                      && float.IsFinite(center.X) && float.IsFinite(center.Y)
                                      && float.IsFinite(center.Z);
    }

    /// <summary>Whether a SubPart renders as interior geometry — the <c>adopt_all</c> candidacy test.</summary>
    [KsaAnchor("ModuleList.Get<PartModelModule>(); PartModelModule.Template.{Internal,RayTracing}",
        SourceFile = "KSA/PartModelModule.cs", Verified = "2026-07-24", GameVersion = "2026.7.9.5018",
        Risk = ChurnRisk.Low, Notes = "Template.Internal == 'renders only through the IVA camera', the "
            + "same classifier InteriorGeometry and the always_render_iva cheat use.")]
    private static bool IsInteriorProp(Part part)
    {
        var models = part.Modules.Get<PartModelModule>();
        if (models.Length == 0)
            return false;
        var template = models[0].PartModel.Template;
        return template.Internal
               && template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy;
    }

    [KsaAnchor("Vehicle.Parts.Parts; Part.{SubParts,InstanceId}", SourceFile = "KSA/Part.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Low,
        Notes = "SubPart-only lookup: a top-level part is deliberately NOT resolvable here, because "
            + "its transform is serialized into the player's saved vehicle.")]
    private static Part? FindSubPart(Vehicle vehicle, uint instanceId)
    {
        foreach (var part in vehicle.Parts.Parts)
        foreach (var sub in part.SubParts)
            if (sub.InstanceId == instanceId)
                return sub;
        return null;
    }

    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList()", SourceFile = "KSA/Universe.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Low,
        Notes = "Liveness check for cabin vessels (same enumeration the sampler and welds use).")]
    private static bool IsLive(Vehicle vehicle)
    {
        if (Universe.CurrentSystem is not { } system)
            return false;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (ReferenceEquals(astronomical, vehicle))
                return true;
        return false;
    }

    private static double NonZero(double value) => double.IsFinite(value) && value != 0 ? value : 1;

    private static Vector3 SafeVector(double3 v)
        => new(
            double.IsFinite(v.X) ? (float)v.X : 0,
            double.IsFinite(v.Y) ? (float)v.Y : 0,
            double.IsFinite(v.Z) ? (float)v.Z : 0);

    private static double3Snap ToSnap(Vector3 v)
        => new(Sanitize.Finite(v.X), Sanitize.Finite(v.Y), Sanitize.Finite(v.Z));

    private static double SafeUt()
    {
        try
        {
            return Universe.GetElapsedSimTime().Seconds();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>One vessel's cabin: its simulation, its objects and its cached interior geometry.</summary>
    private sealed class CabinVessel
    {
        public required Vehicle Vehicle { get; init; }
        public required string VesselId { get; init; }
        public CabinSim? Sim;
        public InteriorGeometry.Result? Interior;
        public bool InteriorDirty = true;
        public int PartCount = -1;
        public readonly HashSet<uint> Excluded = [];
        public readonly List<FloatingObject> Objects = [];
        public readonly List<(int Handle, double Speed)> Impacts = [];

        /// <summary>Bound once and reused, so the step callback allocates no closure per frame.</summary>
        public Action<BodyHandle, double> OnSpeedChange => _onSpeedChange ??= (handle, speed) =>
        {
            if (Impacts.Count < 64)
                Impacts.Add((handle.Value, speed));
        };

        private Action<BodyHandle, double>? _onSpeedChange;
    }
}
