using Brutal.Numerics;
using KSA;

namespace gatOS.GameMod.Game.Ksa.ThugLife;

/// <summary>
///     One anchored thug-life sunglasses quad. Stable identity (<see cref="Id"/>, <see cref="VesselId"/>,
///     <see cref="PartInstanceId"/>) is set at create; the transform is live-tunable; the resolved
///     <see cref="Vehicle"/>/<see cref="Part"/> refs are refreshed by the game-thread driver and consumed
///     by the render postfix. <see cref="Position"/>/<see cref="Rotation"/> are interpreted in the anchor
///     part's local frame (or the vehicle assembly frame when <see cref="Part"/> is null / part_iid 0).
/// </summary>
internal sealed class ThugLifeEntry
{
    /// <summary>Integer handle — the smallest free slot at create (reused after remove/clear); the <c>/sim</c> dir name.</summary>
    public int Id { get; init; }

    /// <summary>The anchor vessel id (stable; the resolved <see cref="Vehicle"/> ref is refreshed each frame).</summary>
    public string VesselId { get; init; } = "";

    /// <summary>The anchor part's <c>InstanceId</c>, or <c>0</c> to anchor to the vehicle assembly frame.</summary>
    public uint PartInstanceId { get; init; }

    /// <summary>Resolved anchor vehicle (refreshed/validated on the game thread; null ⇒ entry is dropped).</summary>
    public Vehicle? Vehicle;

    /// <summary>Resolved anchor part (re-resolved by InstanceId each frame; null ⇒ vehicle-frame anchor).</summary>
    public Part? Part;

    /// <summary>Offset in the anchor's local frame, meters.</summary>
    public float3 Position;

    /// <summary>Pitch/Yaw/Roll in degrees, in the anchor's local frame.</summary>
    public float3 Rotation;

    /// <summary>Quad width in meters.</summary>
    public float Width = 0.975f;

    /// <summary>Quad height in meters (default keeps the 26:5 texture aspect at a uniform block size).</summary>
    public float Height = 0.1875f;

    /// <summary>When false the entry stays in the registry but is skipped while drawing.</summary>
    public bool Visible = true;
}
