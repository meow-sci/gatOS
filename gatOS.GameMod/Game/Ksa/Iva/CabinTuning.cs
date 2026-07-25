namespace gatOS.GameMod.Game.Ksa.Iva;

/// <summary>
///     The IVA cabin simulation's tuning, snapshotted from <c>gatos.toml</c>'s <c>[iva]</c> section
///     when the feature is switched on. A plain immutable record with no game or physics types, so it
///     crosses the <see cref="CabinSim"/> seam without dragging Bepu into the manager.
/// </summary>
/// <param name="SubstepHz">
///     Fixed integration rate. A variable-dt rigid-body sim with contacts is not stable, so the
///     driver accumulates real frame time and runs whole substeps of exactly <c>1/SubstepHz</c>.
/// </param>
/// <param name="MaxSubstepsPerFrame">
///     Upper bound on substeps in one driver pass — the spiral-of-death guard after a hitch. Excess
///     accumulated time is discarded (the cabin runs slow for one frame rather than exploding).
/// </param>
/// <param name="Friction">Contact friction coefficient — high enough that a settled object stays put.</param>
/// <param name="Restitution">
///     Bounciness, expressed as Bepu's maximum recovery velocity (m/s): how fast penetration is
///     pushed out, which reads as bounce. Low keeps small props from pinging around a metal cabin.
/// </param>
/// <param name="ContactFrequency">Contact spring frequency, Hz (stiffness of the contact constraint).</param>
/// <param name="ContactDamping">Contact spring damping ratio (1 = critically damped, no ring).</param>
/// <param name="SleepThreshold">
///     Kinetic-energy threshold below which a body sleeps. A sleeping object costs nothing, which is
///     why "16 props resting on the floor on the pad" is free.
/// </param>
/// <param name="MaxSpeed">
///     Hard velocity clamp, m/s. Art meshes are thin and gapped; a fast object can tunnel through
///     one. Clamping is cheaper and more robust than continuous collision on every prop.
/// </param>
/// <param name="LeashMargin">
///     How far outside the interior bounding box an object may stray, metres, before it is teleported
///     back to the cabin centroid with zero velocity (and an <c>iva.escape</c> event). The backstop
///     for the tunnelling that the speed clamp does not prevent.
/// </param>
/// <param name="DensityKgM3">
///     Default density used to derive an object's mass from its collision-proxy volume. ~300 kg/m³
///     reads as "light cabin junk" (a plastic case, a note, a tin) rather than solid steel.
/// </param>
/// <param name="MaxObjectsPerVessel">Cap on floating objects per vessel (an adopt past it fails EBUSY).</param>
/// <param name="MaxObjectSize">
///     Largest bounding-box extent, metres, a SubPart may have and still be adoptable. Keeps
///     <c>adopt_all</c> — and a mistyped instance id — from cutting a hull panel or a seat loose.
/// </param>
/// <param name="MinObjectSize">
///     Floor applied to each collision-proxy extent, metres, so a flat prop (a photo, a note) still
///     has a collidable thickness instead of a degenerate zero-depth box.
/// </param>
/// <param name="ImpactSpeed">
///     Velocity change within one substep, m/s, above which an <c>iva.impact</c> event is emitted —
///     the "clunk" signal userland wires to <c>/sim/audio</c>.
/// </param>
/// <param name="DoubleSidedInterior">
///     Emit every interior triangle in both windings. Bepu meshes are effectively one-sided, and
///     whether the shipped interior art winds inward is an art decision we do not control — so by
///     default we make the question moot at the cost of 2× (still trivial) triangles. Turn it off
///     only to measure.
/// </param>
internal sealed record CabinTuning(
    int SubstepHz,
    int MaxSubstepsPerFrame,
    double Friction,
    double Restitution,
    double ContactFrequency,
    double ContactDamping,
    double SleepThreshold,
    double MaxSpeed,
    double LeashMargin,
    double DensityKgM3,
    int MaxObjectsPerVessel,
    double MaxObjectSize,
    double MinObjectSize,
    double ImpactSpeed,
    bool DoubleSidedInterior)
{
    /// <summary>The substep length in seconds.</summary>
    public double SubstepSeconds => 1.0 / SubstepHz;

    /// <summary>The shipped defaults, used when no config is supplied (tests, and a failed config load).</summary>
    public static CabinTuning Default { get; } = new(
        SubstepHz: 120,
        MaxSubstepsPerFrame: 8,
        Friction: 0.6,
        Restitution: 1.0,
        ContactFrequency: 30,
        ContactDamping: 1,
        SleepThreshold: 0.002,
        MaxSpeed: 15,
        LeashMargin: 0.5,
        DensityKgM3: 300,
        MaxObjectsPerVessel: 16,
        MaxObjectSize: 0.5,
        MinObjectSize: 0.01,
        ImpactSpeed: 0.4,
        DoubleSidedInterior: true);
}
