using Brutal.Numerics;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Welds;

/// <summary>
///     One active weld (the <c>/sim/debug/welds</c> cheat): a source vessel rigidly tracking a part on
///     a target vessel. Ported from the sibling <c>unscience</c> mod's weld model (scale + animation
///     extras dropped — gatOS welds are pose-lock only).
/// </summary>
/// <remarks>
///     The orientation offset is carried as an authoritative <see cref="Orientation"/> quaternion (the
///     weld driver uses it directly — exact, no per-tick Euler round-trip); <see cref="RotationDeg"/> is
///     the Euler form for the <c>/sim</c> read-back only.
/// </remarks>
internal sealed class WeldEntry
{
    /// <summary>The welded (following) vessel.</summary>
    public required Vehicle Source { get; init; }

    /// <summary>The anchor vessel.</summary>
    public required Vehicle Target { get; init; }

    /// <summary>The source id captured at create time (safe to read even if the vehicle is later removed).</summary>
    public required string SourceId { get; init; }

    /// <summary>The target id captured at create time.</summary>
    public required string TargetId { get; init; }

    /// <summary>The anchor part's <c>InstanceId</c>, or <c>0</c> to anchor to the target body/CoM frame.</summary>
    public uint TargetPartInstanceId { get; init; }

    /// <summary>
    ///     The live anchor part, re-resolved from <see cref="TargetPartInstanceId"/> each tick (robust to
    ///     the target being edited/staged); <c>null</c> ⇒ anchor to the target body/CoM frame.
    /// </summary>
    public Part? TargetPart;

    /// <summary>Position offset expressed in the anchor frame, meters.</summary>
    public double3 Position { get; set; }

    /// <summary>Authoritative orientation offset (source body relative to the anchor) — drives the weld.</summary>
    public doubleQuat Orientation { get; set; }

    /// <summary>Euler pitch/yaw/roll degrees, for the <c>/sim</c> read-back only (the quat is authoritative).</summary>
    public double3 RotationDeg { get; set; }

    /// <summary>true ⇒ lock orientation to the anchor; false ⇒ hold position only (source rotates freely).</summary>
    public bool LockRotation { get; set; } = true;

    /// <summary>false ⇒ suspended (kept in the registry, no physics applied).</summary>
    public bool Enabled { get; set; } = true;
}
