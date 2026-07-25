using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;
using gatOS.Logging;

namespace gatOS.GameMod.Game.Ksa.Iva;

/// <summary>
///     One vessel's cabin: a gatOS-owned <see cref="Simulation"/> running in that vessel's
///     <b>assembly frame</b>, with our own <see cref="BufferPool"/> and <see cref="Shapes"/>
///     (deliberately never <c>ConstraintSim.GlobalShapes</c>, which the game's solver threads share).
///     Nothing in it belongs to KSA, and it can therefore neither perturb the vessel nor corrupt a
///     save — worst case it looks wrong and gets switched off.
/// </summary>
/// <remarks>
///     <para>
///         Working in the assembly frame is what makes this cheap and exact: the interior is then
///         <i>static geometry that never moves</i>, so it is built once and cached, and body poses come
///         out in exactly the coordinates <c>Part.PositionParentAsmb</c> wants. Coordinates stay within
///         a couple of metres of the origin, so Bepu's float32 solver has precision to spare — unlike
///         the game's own bubble-relative frame.
///     </para>
///     <para>
///         Steps are driven with no <c>IThreadDispatcher</c>, so every Bepu callback runs
///         single-threaded on the calling (game) thread. This type is <b>not</b> thread-safe and is
///         only ever touched from the game thread, per threading rule 1.
///     </para>
///     <para>
///         This is the <i>only</i> file that knows the simulation is Bepu (with
///         <see cref="CabinCallbacks"/>); everything above it speaks poses, velocities and handles.
///         plans/IVA_MOVEMENTS.md §3 Option C (a hand-rolled sphere-vs-triangle solver) stays a
///         drop-in replacement behind this seam.
///     </para>
/// </remarks>
internal sealed class CabinSim : IDisposable
{
    private readonly CabinTuning _tuning;
    private readonly CabinFieldBox _field = new();

    private BufferPool? _pool;
    private Simulation? _simulation;

    // The interior static: its handle, its shape index, and the mesh itself (needing an explicit
    // Dispose of the tree buffers it allocated from our pool).
    private StaticHandle _interiorStatic;
    private TypedIndex _interiorShape;
    private Mesh _interiorMesh;
    private bool _hasInterior;

    private double _accumulator;

    /// <summary>Substeps executed on the most recent <see cref="Step"/> call.</summary>
    public int LastSubsteps { get; private set; }

    /// <summary>True once <see cref="Dispose"/> has run; the owner drops the reference.</summary>
    public bool IsDisposed => _simulation is null;

    public CabinSim(CabinTuning tuning)
    {
        _tuning = tuning;
        // A modest block size: a cabin holds a handful of bodies and a few thousand triangles.
        _pool = new BufferPool(8192);
        var narrowPhase = new CabinNarrowPhaseCallbacks(
            (float)tuning.Friction,
            (float)tuning.Restitution,
            new SpringSettings((float)tuning.ContactFrequency, (float)tuning.ContactDamping));
        var poseIntegrator = new CabinPoseCallbacks(_field);
        // 8 velocity iterations / 1 substep matches KSA's own ConstraintSim solve quality; our own
        // fixed substepping (the driver's accumulator) provides the temporal resolution.
        _simulation = Simulation.Create(_pool, narrowPhase, poseIntegrator, new SolveDescription(8, 1));
    }

    /// <summary>
    ///     Replaces the interior collision geometry from an assembly-frame triangle soup (three
    ///     consecutive vertices per triangle). Cheap to call rarely, so the manager rebuilds only when
    ///     the part tree or the adopted set changes.
    /// </summary>
    public void SetInterior(IReadOnlyList<Vector3> triangleVertices)
    {
        if (_simulation is not { } simulation || _pool is not { } pool)
            return;

        RemoveInterior();

        var triangleCount = triangleVertices.Count / 3;
        if (triangleCount == 0)
            return;

        pool.Take<Triangle>(triangleCount, out var triangles);
        for (var i = 0; i < triangleCount; i++)
            triangles[i] = new Triangle(
                triangleVertices[3 * i], triangleVertices[3 * i + 1], triangleVertices[3 * i + 2]);

        _interiorMesh = new Mesh(triangles, Vector3.One, pool);
        _interiorShape = simulation.Shapes.Add(in _interiorMesh);
        var description = new StaticDescription(RigidPose.Identity, _interiorShape);
        _interiorStatic = simulation.Statics.Add(in description);
        _hasInterior = true;
    }

    /// <summary>
    ///     Adds a box-proxied dynamic body. Returns its handle, or <c>null</c> when the simulation is
    ///     already disposed.
    /// </summary>
    /// <param name="size">Full extents of the box proxy, metres.</param>
    /// <param name="massKg">The body's mass, kg.</param>
    /// <param name="position">Initial position in the assembly frame, metres.</param>
    /// <param name="orientation">Initial orientation.</param>
    /// <param name="velocity">Initial velocity relative to the cabin, m/s.</param>
    public BodyHandle? AddBox(Vector3 size, double massKg, Vector3 position, Quaternion orientation,
        Vector3 velocity)
    {
        if (_simulation is not { } simulation)
            return null;

        var box = new Box(size.X, size.Y, size.Z);
        var shape = simulation.Shapes.Add(in box);
        var description = BodyDescription.CreateDynamic(
            new RigidPose(position, orientation),
            new BodyVelocity(velocity),
            box.ComputeInertia((float)massKg),
            new CollidableDescription(shape),
            // A generous minimum-timestep count keeps a prop from napping between two nudges of a
            // slow manoeuvre; the threshold itself is what actually saves the work.
            new BodyActivityDescription((float)_tuning.SleepThreshold, 32));
        return simulation.Bodies.Add(in description);
    }

    /// <summary>Removes a body and disposes the shape created for it.</summary>
    public void Remove(BodyHandle handle)
    {
        if (_simulation is not { } simulation || _pool is not { } pool)
            return;
        if (!simulation.Bodies.BodyExists(handle))
            return;

        var shape = simulation.Bodies[handle].Collidable.Shape;
        simulation.Bodies.Remove(handle);
        if (shape.Exists)
            simulation.Shapes.RecursivelyRemoveAndDispose(shape, pool);
    }

    /// <summary>Reads a body's current pose, velocity and sleep state.</summary>
    public bool TryRead(BodyHandle handle, out Vector3 position, out Quaternion orientation,
        out Vector3 velocity, out Vector3 angularVelocity, out bool asleep)
    {
        position = default;
        orientation = Quaternion.Identity;
        velocity = default;
        angularVelocity = default;
        asleep = false;
        if (_simulation is not { } simulation || !simulation.Bodies.BodyExists(handle))
            return false;

        var body = simulation.Bodies[handle];
        position = body.Pose.Position;
        orientation = body.Pose.Orientation;
        velocity = body.Velocity.Linear;
        angularVelocity = body.Velocity.Angular;
        asleep = !body.Awake;
        return true;
    }

    /// <summary>Adds to a body's linear velocity and wakes it (the <c>nudge</c> control).</summary>
    public bool TryNudge(BodyHandle handle, Vector3 deltaVelocity)
    {
        if (_simulation is not { } simulation || !simulation.Bodies.BodyExists(handle))
            return false;
        var body = simulation.Bodies[handle];
        body.Awake = true;
        body.Velocity.Linear += deltaVelocity;
        return true;
    }

    /// <summary>Teleports a body and zeroes its motion (the leash's escape recovery).</summary>
    public bool TryReset(BodyHandle handle, Vector3 position)
    {
        if (_simulation is not { } simulation || !simulation.Bodies.BodyExists(handle))
            return false;
        var body = simulation.Bodies[handle];
        body.Awake = true;
        body.Pose.Position = position;
        body.Velocity.Linear = default;
        body.Velocity.Angular = default;
        return true;
    }

    /// <summary>
    ///     Freezes every body in place (the time-warp / left-IVA / editor park). Velocities are zeroed
    ///     rather than the sim being merely un-stepped, so un-parking resumes from rest instead of
    ///     resuming a stale velocity into a world that moved on.
    /// </summary>
    public void Park()
    {
        _accumulator = 0;
        LastSubsteps = 0;
        if (_simulation is not { } simulation)
            return;
        ref var set = ref simulation.Bodies.ActiveSet;
        for (var i = 0; i < set.Count; i++)
        {
            var body = simulation.Bodies[set.IndexToHandle[i]];
            body.Velocity.Linear = default;
            body.Velocity.Angular = default;
        }
    }

    /// <summary>
    ///     Advances the cabin by <paramref name="dt"/> real seconds in fixed substeps, applying
    ///     <paramref name="field"/> uniformly. Returns the largest per-body speed change seen across
    ///     the pass keyed by handle — the manager turns those into <c>iva.impact</c> events without
    ///     any narrow-phase bookkeeping.
    /// </summary>
    public void Step(double dt, in CabinField field, Action<BodyHandle, double>? onSpeedChange = null)
    {
        LastSubsteps = 0;
        if (_simulation is not { } simulation)
            return;

        _field.Value = field;

        // Cap the catch-up so a hitch (or a load screen) cannot spiral: run at most
        // MaxSubstepsPerFrame and throw the rest away.
        _accumulator += Math.Clamp(dt, 0, 0.25);
        var step = _tuning.SubstepSeconds;
        var budget = _tuning.MaxSubstepsPerFrame;
        var maxSpeed = (float)_tuning.MaxSpeed;

        while (_accumulator >= step && LastSubsteps < budget)
        {
            _accumulator -= step;
            LastSubsteps++;

            if (onSpeedChange is not null)
                CaptureSpeeds(simulation);

            simulation.Timestep((float)step);

            ClampSpeeds(simulation, maxSpeed);
            if (onSpeedChange is not null)
                ReportSpeedChanges(simulation, onSpeedChange);
        }

        if (_accumulator > budget * step)
            _accumulator = 0; // fell too far behind; resync rather than sprint
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var simulation = _simulation;
        var pool = _pool;
        _simulation = null;
        _pool = null;
        if (simulation is null || pool is null)
            return;

        try
        {
            RemoveInteriorCore(simulation, pool);
            simulation.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS iva: cabin sim dispose error: {ex.Message}");
        }
        finally
        {
            // Frees every buffer the simulation, its shapes and the interior mesh took from us.
            pool.Clear();
        }
    }

    private void RemoveInterior()
    {
        if (_simulation is { } simulation && _pool is { } pool)
            RemoveInteriorCore(simulation, pool);
    }

    private void RemoveInteriorCore(Simulation simulation, BufferPool pool)
    {
        if (!_hasInterior)
            return;
        _hasInterior = false;
        simulation.Statics.Remove(_interiorStatic);
        simulation.Shapes.Remove(_interiorShape);
        _interiorMesh.Dispose(pool);
        _interiorMesh = default;
        _interiorShape = default;
    }

    // ---- impact detection: one speed sample either side of a substep -------------------------
    //
    // Watching |Δv| per substep is a far simpler and more robust impact signal than instrumenting the
    // narrow phase: it catches wall hits, object-object hits and hard landings alike, needs no
    // manifold state, and cannot fire on a contact that the solver later discards.

    private float[] _speedsBefore = [];
    private BodyHandle[] _speedHandles = [];
    private int _speedCount;

    private void CaptureSpeeds(Simulation simulation)
    {
        ref var set = ref simulation.Bodies.ActiveSet;
        _speedCount = set.Count;
        if (_speedsBefore.Length < _speedCount)
        {
            _speedsBefore = new float[Math.Max(8, _speedCount * 2)];
            _speedHandles = new BodyHandle[_speedsBefore.Length];
        }

        for (var i = 0; i < _speedCount; i++)
        {
            var handle = set.IndexToHandle[i];
            _speedHandles[i] = handle;
            _speedsBefore[i] = simulation.Bodies[handle].Velocity.Linear.Length();
        }
    }

    private void ReportSpeedChanges(Simulation simulation, Action<BodyHandle, double> onSpeedChange)
    {
        for (var i = 0; i < _speedCount; i++)
        {
            var handle = _speedHandles[i];
            if (!simulation.Bodies.BodyExists(handle))
                continue;
            var after = simulation.Bodies[handle].Velocity.Linear.Length();
            var delta = Math.Abs(after - _speedsBefore[i]);
            if (delta > 0)
                onSpeedChange(handle, delta);
        }
    }

    private static void ClampSpeeds(Simulation simulation, float maxSpeed)
    {
        ref var set = ref simulation.Bodies.ActiveSet;
        for (var i = 0; i < set.Count; i++)
        {
            var body = simulation.Bodies[set.IndexToHandle[i]];
            var speed = body.Velocity.Linear.Length();
            if (speed > maxSpeed)
                body.Velocity.Linear *= maxSpeed / speed;
        }
    }
}
