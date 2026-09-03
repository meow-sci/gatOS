using Brutal.Numerics;
using gatOS.SimFs.Camera;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     The camera's anchor, presented to the engine as something a camera can follow — the KSArmory
///     follow methodology. While gatOS owns the camera, the main viewport's camera <b>follows this</b>
///     rather than being unfollowed, so the engine resolves the anchor's position inside its own
///     viewport pass and every pose gatOS writes is an <i>offset from the followed object</i>, never an
///     absolute ecliptic point sampled in a different frame phase.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists at all.</b> Near a planet every absolute ECL position carries the
///         ~29.8 km/s the whole world shares — ~600 m per 20 ms frame. A camera placed from an absolute
///         point sampled in one phase and rendered in another sits one frame out of register with the
///         scene, and the follow bookkeeping the engine keeps per camera (<c>NearbyCelestial</c>,
///         <c>CurrentAltitudeKm</c> — the inputs to <c>Camera.ClampCamera</c>'s surface teleport) goes
///         stale when a camera is moved by raw <c>PositionEcl</c> writes. Following an engine-native
///         <c>IFollowable</c> keeps all of it coherent because the engine's own pass does the resolving.
///         <c>KSA.WreckageMarker</c> is the engine's own proof that an <c>IFollowable</c> need not be a
///         vehicle or be registered anywhere.
///     </para>
///     <para>
///         <b><c>GetPositionEcl</c> re-resolves the live anchor on every call.</b> The engine calls it
///         in its own frame pass, so resolving here — not caching a position computed in the command
///         drain — is what puts the answer in the engine's epoch. An anchor that despawns mid-shot
///         holds its last live sample, which leaves the camera exactly where it was (the least
///         surprising failure, and the only one recoverable by writing a new anchor).
///     </para>
///     <para>
///         <b><c>GetBodyFixed2Ecl</c> is identity on purpose:</b> <c>Camera.PositionCce</c> transforms
///         the camera's <c>LocalPosition</c> through the followed object's body-fixed frame unless it is
///         identity, and a camera offset that turned with a rolling vessel would corkscrew every
///         anchored shot. Frame-relative placement (<c>bodyfixed</c>/<c>enu</c>/<c>lvlh</c>/<c>chase</c>)
///         is composed by <see cref="CameraFrames"/> in the pose solve instead, where it is explicit.
///     </para>
///     <para>Game thread only (threading rule 1): mutated by the director in the viewport prefix and
///     read by the engine inside the same viewport pass.</para>
/// </remarks>
internal sealed class CameraFollowable : IFollowable
{
    /// <summary>Non-zero: the engine divides by the followed object's radius on focus changes.</summary>
    private const double Radius = 1.0;

    private readonly OrbitView _orbitView = new(CameraReferenceFrame.Stars);

    private TargetRef _anchor = TargetRef.None;
    private double3 _heldEcl;

    /// <summary>Where the anchor was last resolved, for when it stops existing mid-frame.</summary>
    internal double3 LastPositionEcl { get; private set; }

    /// <summary>Points the followable at a live anchor (<c>vessel:</c>/<c>body:</c>/<c>part:</c>).</summary>
    internal void Follow(in TargetRef anchor) => _anchor = anchor;

    /// <summary>
    ///     Holds a fixed absolute ecliptic point — the un-anchored (<c>ecl</c> frame) placement, and
    ///     the state the director seeds at ownership take.
    /// </summary>
    internal void Hold(double3 positionEcl)
    {
        _anchor = TargetRef.None;
        _heldEcl = positionEcl;
        LastPositionEcl = positionEcl;
    }

    public string Id => "gatos.camera.anchor";

    public KeyHash Hash => KeyHash.Make(Id.AsSpan());

    public string Class => "GatosCameraAnchor";

    public double MeanRadius => Radius;

    public OrbitView OrbitView => _orbitView;

    public bool ShowAxes { get; set; }

    /// <summary>
    ///     Where the anchor is <i>now</i>, resolved in the caller's (the engine's) own epoch. Every
    ///     offset the director hands the pose controller is measured against this same sample.
    /// </summary>
    [KsaAnchor("IFollowable : IObjectId, IPosition, IVelocity, IOrientation, IRadius { OrbitView }; "
            + "OrbitView(CameraReferenceFrame); KeyHash.Make(ReadOnlySpan<char>)",
        SourceFile = "KSA/IFollowable.cs / KSA/OrbitView.cs / KSA/KeyHash.cs", Verified = "2026-08-11",
        GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "MeanRadius must be non-zero — the engine divides by it when a camera changes focus. "
            + "OrbitView must be one stable instance (OrbitController/FixedController dereference it "
            + "unguarded). SetFollow teleports to target + 2.5 × MeanRadius × forward, so a radius of "
            + "1 m keeps that jump at 2.5 m before the director re-writes the transform over it.")]
    public double3 GetPositionEcl()
    {
        if (!_anchor.HasTarget)
        {
            LastPositionEcl = _heldEcl;
        }
        else if (CameraTargets.TryResolve(_anchor, out var target))
        {
            LastPositionEcl = CameraTargets.PositionEcl(target);
        }

        // A despawned anchor keeps the last live sample: hold, don't fling.
        return LastPositionEcl;
    }

    public double3 GetVelocityEcl()
        => _anchor.HasTarget && CameraTargets.TryResolve(_anchor, out var target)
            ? CameraTargets.VelocityEcl(target)
            : double3.Zero;

    public double3 GetPositionEclFromCce(double3 positionCce) => GetPositionEcl() + positionCce;

    public double3 GetPositionCceFromEcl(double3 positionEcl) => positionEcl - GetPositionEcl();

    /// <summary>Identity, so a camera offset does not turn with the anchor (see the type remarks).</summary>
    public doubleQuat GetBodyFixed2Ecl() => doubleQuat.Identity;

    public double3 GetBodyRates() => double3.Zero;

    public bool IsMoon() => false;

    public bool IsStar() => false;

    public bool HasOrbit() => false;

    public void DrawAxes(IViewport viewport)
    {
    }
}
