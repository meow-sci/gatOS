using Brutal.Numerics;
using gatOS.SimFs.Paint.Stickers;
using KSA;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     Per-frame composition of a sticker's <b>decal space</b> — the unit cube the fragment shader
///     projects the reconstructed scene position into (STICKERS_PLAN §3.3/§3.5).
/// </summary>
/// <remarks>
///     <para><b>Decal space.</b> A <c>[-0.5, 0.5]³</c> cube centred on the surface point:
///     <c>x</c> = width (right), <c>y</c> = height ("up" in the PNG), <c>z</c> = the outward
///     surface normal. Scaled by <c>(width, height, depth)</c> metres. The matrix is composed
///     <c>S · R · T · parent</c> in KSA's <b>row-vector</b> convention (<c>v * M</c>, the same order
///     <c>ThugLifeQuadRenderer.TryComputeModelEgo</c> uses), so reading it left to right is the order
///     the operations happen in.</para>
///     <para><b>Everything is recomputed every frame.</b> Ego space is camera-relative and the
///     planet turns; nothing derived here may be cached across frames. A body anchor's <i>stored</i>
///     state is geodetic and a vessel anchor's is part-local, which is what makes them survive
///     bubble-frame switches, floating-origin shifts and time warp (§1.4).</para>
///     <para><b>Precision.</b> All of it is <c>double</c> — including the inverse — and only the
///     final 3×4 rows are packed to <c>float</c> for the push constant. Inverting the packed float
///     matrix instead would lose the surface point to cancellation at kilometre distances.</para>
///     <para>Game thread only.</para>
/// </remarks>
internal static class StickerAnchors
{
    private const double Deg2Rad = Math.PI / 180.0;

    // Below this the "up" reference is parallel to the decal normal and its projection onto the
    // tangent plane is numerical noise, so the fallback reference is used instead.
    private const double DegenerateUp = 1e-9;

    /// <summary>
    ///     Rebuilds <paramref name="entry"/>'s <see cref="StickerEntry.DecalToEgo"/>,
    ///     <see cref="StickerEntry.EgoToDecal"/>, <see cref="StickerEntry.AxisZEgo"/> and
    ///     <see cref="StickerEntry.DistanceEgo"/> for this frame.
    /// </summary>
    /// <returns>
    ///     False when the anchor cannot be composed (missing anchor, degenerate normal, a
    ///     non-finite terrain radius, or a singular matrix) — the caller then leaves the sticker
    ///     dormant for this frame rather than drawing garbage.
    /// </returns>
    internal static bool TryCompose(StickerEntry entry, KsaCamera camera)
        => entry.Kind == StickerAnchorKind.Body
            ? TryComposeBody(entry, camera)
            : TryComposeVessel(entry, camera);

    /// <summary>
    ///     Geodetic anchor: the decal sits on the terrain at (lat, lon), its <c>+z</c> along the
    ///     local radial and its <c>+y</c> rotated from north by the stored heading, so it rides the
    ///     planet's spin for free.
    /// </summary>
    /// <remarks>
    ///     The CPU terrain height is sampled with <c>accurate: false</c> — four texel taps, the same
    ///     call the physics hot path uses (<c>KSA/TerrainImpactFinder.cs:64</c>). The GPU adds
    ///     tessellation displacement the CPU never sees, so the surface point can be off by
    ///     decimetres near the camera; the projection box's depth absorbs that entirely, which is
    ///     exactly why this is a projected decal and not a flat quad (§1.4).
    /// </remarks>
    [KsaAnchor("Celestial.{GetDirCcfFromLatLon,GetTerrainHeightFromDirCcf,GetCcf2Cce,GetCci2Cce,"
            + "MeanRadius}; Vehicle.ComputeEnu2Cce(double3, doubleQuat); Camera.GetPositionEgo(IPosition)",
        SourceFile = "KSA/Celestial.cs:670,792,534,522,91 / KSA/Vehicle.cs:2997 / KSA/Camera.cs:231",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "GetTerrainHeightFromDirCcf returns METRES above MeanRadius and 0 for a body with no "
            + "heightmap (Celestial.cs:794-797). ComputeEnu2Cce builds its quaternion from a matrix "
            + "whose ROWS are east/north/up (Vehicle.cs:3014), so under the row-vector convention "
            + "UnitX/UnitY/UnitZ transform to east/north/up respectively. It returns null on the spin "
            + "axis, where ENU is undefined. The ego position is composed exactly like KSA's own "
            + "terrain debug overlay (Vehicle.cs:4511-4523): body ego position + the body-fixed offset "
            + "rotated into ecliptic axes, never an absolute ecliptic point.")]
    private static bool TryComposeBody(StickerEntry entry, KsaCamera camera)
    {
        if (entry.Body is not { } body)
            return false;

        var dirCcf = body.GetDirCcfFromLatLon(entry.Position.X, entry.Position.Y);
        var radius = body.MeanRadius + body.GetTerrainHeightFromDirCcf(dirCcf, accurate: false);
        if (!double.IsFinite(radius))
            return false;

        var ccf2Cce = body.GetCcf2Cce();
        var surfaceCce = (dirCcf * radius).Transform(ccf2Cce);

        double3 east, north, up;
        if (Vehicle.ComputeEnu2Cce(surfaceCce, body.GetCci2Cce()) is { } enu2Cce)
        {
            east = double3.UnitX.Transform(enu2Cce);
            north = double3.UnitY.Transform(enu2Cce);
            up = double3.UnitZ.Transform(enu2Cce);
        }
        else
        {
            // Exactly on the spin axis ENU has no north and ComputeEnu2Cce returns null — but
            // lat = ±90 is a value StickerRules accepts, so a pole sticker gets an arbitrary but
            // frame-stable tangent basis about the radial instead of being dormant forever.
            var length = surfaceCce.Length();
            if (!double.IsFinite(length) || length <= 0)
                return false;
            up = surfaceCce / length;
            if (!TryTangent(double3.UnitX, up, out north) && !TryTangent(double3.UnitY, up, out north))
                return false;
            east = double3.Cross(north, up);
        }

        // heading 0 = the PNG's up points north; +90 = east (a compass bearing).
        var heading = entry.RotationDeg * Deg2Rad;
        var cos = Math.Cos(heading);
        var sin = Math.Sin(heading);
        var axisY = north * cos + east * sin;
        var axisX = east * cos - north * sin;

        var positionEgo = camera.GetPositionEgo(body) + surfaceCce;
        return Finish(entry, Basis(axisX, axisY, up), positionEgo, double4x4.Identity, useParent: false);
    }

    /// <summary>
    ///     Vessel anchor: the decal sits in the anchor part's local frame, so it follows the part
    ///     through staging animations, gimbals and robotics without any per-frame bookkeeping.
    /// </summary>
    /// <remarks>
    ///     Unlike the thug-life quad — which pulls the part's rotation and position apart precisely to
    ///     <i>exclude</i> <c>Part.Scale</c> — a decal is stuck to the rendered surface, so it wants the
    ///     full matrix <b>including</b> scale: a part instanced at 2× has its art at 2×, and the hit
    ///     point stored in that part's local frame is only correct under the same transform.
    /// </remarks>
    [KsaAnchor("Vehicle.GetMatrixAsmb2Ego(Camera); Part.MatrixAsmb2Ego(in double4x4)",
        SourceFile = "KSA/Vehicle.cs:1202 / KSA/Part.cs:1041",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "Part.MatrixAsmb2Ego is CreateScale(Scale) * CreateFromQuaternion(Asmb2ParentAsmb) * "
            + "CreateTranslation(PositionParentAsmb) * MatrixParentAsmb2Ego — i.e. it INCLUDES the "
            + "part's own scale and walks the whole sub-part parent chain, which is what makes a "
            + "sub-part instance id a valid anchor. Row-vector convention throughout (v * M), so the "
            + "decal matrix is composed S * R * T * partMat and read left to right.")]
    private static bool TryComposeVessel(StickerEntry entry, KsaCamera camera)
    {
        if (entry.Vehicle is not { } vehicle || entry.Part is not { } part)
            return false;

        var normalLength = entry.Normal.Length();
        if (!double.IsFinite(normalLength) || normalLength <= 0)
            return false;
        var axisZ = entry.Normal / normalLength;

        // "PNG up" defaults to the part's own +Y projected onto the decal's tangent plane, so a
        // sticker sprayed on the side of a stack reads upright along the stack. When +Y IS the
        // normal (a decal on a nose cone's tip, say) that projection vanishes and +X stands in.
        if (!TryTangent(double3.UnitY, axisZ, out var axisY) && !TryTangent(double3.UnitX, axisZ, out axisY))
            return false;
        var axisX = double3.Cross(axisY, axisZ);

        var roll = entry.RotationDeg * Deg2Rad;
        var cos = Math.Cos(roll);
        var sin = Math.Sin(roll);
        var rolledX = axisX * cos + axisY * sin;
        var rolledY = axisY * cos - axisX * sin;

        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
        var partMatrix = part.MatrixAsmb2Ego(in vehicleMatrix);
        return Finish(entry, Basis(rolledX, rolledY, axisZ), entry.Position, partMatrix, useParent: true);
    }

    /// <summary>Composes <c>S · R · T · parent</c>, inverts it in double, and packs both.</summary>
    private static bool Finish(
        StickerEntry entry, double4x4 rotation, double3 translation, double4x4 parent, bool useParent)
    {
        var scale = double4x4.CreateScale(entry.Width, entry.Height, entry.Depth);
        var local = scale * rotation * double4x4.CreateTranslation(translation);
        var decalToEgo = useParent ? local * parent : local;
        if (!double.IsFinite(decalToEgo.W.X) || !double.IsFinite(decalToEgo.W.Y)
            || !double.IsFinite(decalToEgo.W.Z))
            return false;
        if (!double4x4.Invert(decalToEgo, out var egoToDecal))
            return false;

        var originEgo = decalToEgo.W.XYZ;
        var outward = decalToEgo.Z.XYZ;
        var outwardLength = outward.Length();
        if (!double.IsFinite(outwardLength) || outwardLength <= 0)
            return false;

        entry.DecalToEgo = float4x4.Pack(in decalToEgo);
        entry.EgoToDecal = float4x4.Pack(in egoToDecal);
        entry.AxisZEgo = float3.Pack(outward / outwardLength);
        entry.DistanceEgo = originEgo.Length();
        return double.IsFinite(entry.DistanceEgo);
    }

    /// <summary>Projects <paramref name="reference"/> onto the plane perpendicular to <paramref name="axisZ"/>.</summary>
    private static bool TryTangent(double3 reference, double3 axisZ, out double3 tangent)
    {
        tangent = reference - axisZ * double3.Dot(reference, axisZ);
        var length = tangent.Length();
        if (!double.IsFinite(length) || length <= DegenerateUp)
            return false;
        tangent /= length;
        return true;
    }

    /// <summary>
    ///     A rotation whose <b>rows</b> are the decal axes expressed in the parent frame — the
    ///     row-vector form, so <c>UnitX * R = axisX</c> (the same shape
    ///     <c>Vehicle.ComputeEnu2Cce</c> builds before handing it to <c>CreateFromRotationMatrix</c>).
    /// </summary>
    private static double4x4 Basis(double3 axisX, double3 axisY, double3 axisZ)
        => new(
            axisX.X, axisX.Y, axisX.Z, 0,
            axisY.X, axisY.Y, axisY.Z, 0,
            axisZ.X, axisZ.Y, axisZ.Z, 0,
            0, 0, 0, 1);
}
