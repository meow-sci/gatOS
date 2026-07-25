using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace gatOS.GameMod.Game.Ksa.Iva;

/// <summary>
///     The uniform forcing state one <see cref="CabinSim"/> step integrates against, refreshed from
///     the vessel on the game thread immediately before <c>Timestep</c>. Everything is in the vessel's
///     assembly frame, which shares orientation with the body frame — so KSA's body-axis
///     accelerometer readings drop straight in (plans/IVA_MOVEMENTS.md §2).
/// </summary>
internal struct CabinField
{
    /// <summary>The vessel's proper (non-gravitational) acceleration, m/s² — <c>Vehicle.AccelerationBody</c>.</summary>
    public Vector3 ProperAcceleration;

    /// <summary>The vessel's angular velocity, rad/s — <c>Vehicle.BodyRates</c>.</summary>
    public Vector3 BodyRates;

    /// <summary>The vessel's angular acceleration, rad/s² — <c>Vehicle.AngularAccelerationBody</c>.</summary>
    public Vector3 AngularAcceleration;

    /// <summary>The vessel's centre of mass in the assembly frame, metres — the rotation centre.</summary>
    public Vector3 CenterOfMass;
}

/// <summary>
///     The pose-integrator callbacks: they apply the §2 apparent-acceleration field to every body in
///     the cabin simulation. Gravity is never modelled — it is already absent from the proper
///     acceleration by construction, which is the whole trick.
/// </summary>
/// <remarks>
///     A Bepu callback struct is passed by value as a generic type argument into an aggressively
///     inlined hot path, so the mutable field lives behind a reference cell the owning
///     <see cref="CabinSim"/> writes before each step. We never touch KSA state from here — the field
///     is snapshotted on the game thread first. (In practice this runs single-threaded on the game
///     thread anyway: <see cref="CabinSim"/> steps with no <c>IThreadDispatcher</c>.)
/// </remarks>
internal readonly struct CabinPoseCallbacks(CabinFieldBox field) : IPoseIntegratorCallbacks
{
    private readonly CabinFieldBox _field = field;

    /// <inheritdoc />
    public AngularIntegrationMode AngularIntegrationMode
        => AngularIntegrationMode.ConserveMomentumWithGyroscopicTorque;

    /// <summary>
    ///     True: a lone prop drifting in a coasting cabin is unconstrained, and it is exactly the case
    ///     the whole feature exists for — it must still receive the forcing field every substep.
    /// </summary>
    public bool AllowSubstepsForUnconstrainedBodies => true;

    /// <inheritdoc />
    public bool IntegrateVelocityForKinematics => false;

    /// <inheritdoc />
    public void Initialize(Simulation simulation)
    {
    }

    /// <inheritdoc />
    public void PrepareForIntegration(float dt)
    {
    }

    /// <inheritdoc />
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        ref var f = ref _field.Value;

        // r_b = position − centre of mass (both already assembly-frame).
        Vector3Wide.Broadcast(f.CenterOfMass, out var com);
        var offset = position - com;

        Vector3Wide.Broadcast(f.BodyRates, out var omega);
        Vector3Wide.Broadcast(f.AngularAcceleration, out var alpha);
        Vector3Wide.Broadcast(f.ProperAcceleration, out var properAccel);

        // a = −a_p − α×r_b − 2 ω×v − ω×(ω×r_b)
        var euler = Vector3Wide.Cross(alpha, offset);
        var coriolis = Vector3Wide.Cross(omega, velocity.Linear) * new Vector<float>(2f);
        var centrifugal = Vector3Wide.Cross(omega, Vector3Wide.Cross(omega, offset));
        var apparent = -properAccel - euler - coriolis - centrifugal;

        // Integrate only the lanes Bepu asked for; the rest keep their velocity untouched.
        var mask = Vector.AsVectorSingle(integrationMask);
        var delta = apparent * dt;
        velocity.Linear.X += Vector.BitwiseAnd(delta.X, mask);
        velocity.Linear.Y += Vector.BitwiseAnd(delta.Y, mask);
        velocity.Linear.Z += Vector.BitwiseAnd(delta.Z, mask);
    }
}

/// <summary>
///     A heap cell holding the current <see cref="CabinField"/>. Bepu takes the callbacks struct by
///     value, so the mutable state has to be reachable through a reference the owner can still write.
/// </summary>
internal sealed class CabinFieldBox
{
    /// <summary>The live field, written on the game thread before each <c>Timestep</c>.</summary>
    public CabinField Value;
}

/// <summary>
///     The narrow-phase callbacks. Because <b>everything</b> in this simulation is ours — our
///     interior static and our own floating objects — there is no filtering to do: the game's own
///     bodies, colliders and terrain are simply not in this world. That is the central reason gatOS
///     runs its own simulation rather than adding bodies to KSA's <c>ConstraintSim</c>, whose
///     <c>NarrowPhaseCallbacks.AllowContactGeneration</c> permits dynamic↔dynamic pairs
///     unconditionally and would have a cabin prop shoving the actual spacecraft around.
/// </summary>
internal readonly struct CabinNarrowPhaseCallbacks(
    float friction, float maximumRecoveryVelocity, SpringSettings contactSpring)
    : INarrowPhaseCallbacks
{
    private readonly float _friction = friction;
    private readonly float _maximumRecoveryVelocity = maximumRecoveryVelocity;
    private readonly SpringSettings _contactSpring = contactSpring;

    /// <inheritdoc />
    public void Initialize(Simulation simulation)
    {
    }

    /// <inheritdoc />
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b,
        ref float speculativeMargin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

    /// <inheritdoc />
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    /// <inheritdoc />
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair,
        ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = _friction;
        pairMaterial.MaximumRecoveryVelocity = _maximumRecoveryVelocity;
        pairMaterial.SpringSettings = _contactSpring;
        return true;
    }

    /// <inheritdoc />
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA,
        int childIndexB, ref ConvexContactManifold manifold)
        => true;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
