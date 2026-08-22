using Brutal.Numerics;
using gatOS.SimFs.Paint.Stickers;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     One live sticker in the game-side registry (STICKERS_PLAN §3.5): the user's desired state,
///     the anchor resolution refreshed once per frame, and the per-frame matrices the render pass
///     consumes. The <c>ThugLifeEntry</c> shape — a mutable class, not a record — because the
///     published array holds the same objects the driver mutates in place.
/// </summary>
/// <remarks>
///     <para>Mutated on the game thread only (the Frame command drain and
///     <see cref="StickerManager.Tick"/>); read by the render postfix on the same main thread, so
///     no locking is required (<c>.agents/skills/ksa/quad.md</c>).</para>
///     <para>Nothing here is ever stored in ecliptic or ego coordinates: a vessel anchor lives in
///     its part's local frame and a body anchor in geodetic lat/lon, so both survive the bubble
///     frame switches, floating-origin shifts and planet rotation that make an absolute position
///     meaningless one frame later (§1.4).</para>
/// </remarks>
internal sealed class StickerEntry
{
    /// <summary>Registry id — the smallest free slot at create; the <c>/sim/paint/stickers/&lt;id&gt;</c> dir name.</summary>
    public int Id { get; init; }

    /// <summary>The <c>/sim/paint/textures/file/</c> image this sticker draws (hot-swappable).</summary>
    public string Image = "";

    /// <summary>Which frame <see cref="Position"/>/<see cref="Normal"/> are expressed in.</summary>
    public StickerAnchorKind Kind { get; init; }

    /// <summary>The anchor vehicle id or celestial body id (stable across despawn/respawn).</summary>
    public string TargetId { get; init; } = "";

    /// <summary>The anchor part's <c>InstanceId</c> (vessel anchors only; 0 for a body anchor).</summary>
    public uint PartInstanceId { get; init; }

    /// <summary>Vessel: part-local metres. Body: <c>(latitudeDeg, longitudeDeg, 0)</c>.</summary>
    public double3 Position;

    /// <summary>Vessel: the part-local surface normal the decal box points down. Body: zero (up is the radial).</summary>
    public double3 Normal;

    /// <summary>Vessel: roll about <see cref="Normal"/>. Body: compass heading. Degrees.</summary>
    public double RotationDeg;

    /// <summary>Decal width in metres (the decal-space x extent).</summary>
    public double Width = StickerRules.DefaultWidth;

    /// <summary>Decal height in metres (the decal-space y extent, "up" in the PNG).</summary>
    public double Height = StickerRules.DefaultHeight;

    /// <summary>Projection-box depth along the normal, in metres (the decal-space z extent).</summary>
    public double Depth = StickerRules.DefaultDepthVessel;

    /// <summary>Opacity in <c>[0, 1]</c>, multiplied into the sampled alpha.</summary>
    public double Alpha = StickerRules.DefaultAlpha;

    /// <summary>Gain on the lighting term in <c>(0, 8]</c>.</summary>
    public double Brightness = StickerRules.DefaultBrightness;

    /// <summary>False hides the sticker without removing it from the registry.</summary>
    public bool Visible = true;

    // ---- resolved once per frame on the game thread ------------------------------------------

    /// <summary>The resolved anchor vehicle, or null while it is despawned (⇒ dormant, not pruned).</summary>
    public Vehicle? Vehicle;

    /// <summary>The resolved anchor part (or sub-part), re-resolved by <see cref="PartInstanceId"/> each frame.</summary>
    public Part? Part;

    /// <summary>The resolved anchor body, or null while the system does not contain it.</summary>
    public Celestial? Body;

    /// <summary>The bindless slot the image occupies, or <c>-1</c> while it has none.</summary>
    public int TextureHandle = -1;

    /// <summary>How the image resolved this frame (the <c>status</c> row's texture column).</summary>
    public StickerTextureState TextureState = StickerTextureState.Missing;

    /// <summary>Anchor resolved <b>and</b> texture resident — the only state that draws (and that keeps the patch installed).</summary>
    public bool Live;

    // ---- per-frame render outputs, filled by StickerAnchors on the game thread ----------------

    /// <summary>Decal unit cube → ego, row-vector convention (<c>v * M</c>). Rebuilt every frame.</summary>
    public float4x4 DecalToEgo = float4x4.Identity;

    /// <summary>The exact inverse of <see cref="DecalToEgo"/>, computed in double before packing.</summary>
    public float4x4 EgoToDecal = float4x4.Identity;

    /// <summary>The decal's outward (+z) axis in ego, normalised — the fragment shader's facing reference.</summary>
    public float3 AxisZEgo = float3.UnitZ;

    /// <summary>Distance from the camera to the decal origin, metres (the distance cull).</summary>
    public double DistanceEgo;
}
