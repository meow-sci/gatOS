using Brutal.Numerics;
using gatOS.SimFs.Paint.Stickers;
using KSA;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     Turns "where the camera (or cursor) is pointing" into a sticker anchor: a mesh-precise hit on
///     a vehicle part, else a terrain hit on the nearby celestial (STICKERS_PLAN §3.6).
/// </summary>
/// <remarks>
///     <para><b>Vehicles first, terrain behind.</b> The vehicle sweep uses KSA's own watertight
///     triangle raycast — the same call flight-mode hover picking makes — so the hit point is on the
///     art surface, not on a collider primitive. Only if nothing was hit does the terrain march run.
///     Ground clutter cannot be aimed at in v1 (it exists only on the GPU, §1.2); a ray passes
///     through a rock to the terrain behind it, and the decal box then projects onto the rock
///     anyway, which is the whole reason this is a projected decal.</para>
///     <para>Game thread only — every call here reads live game state.</para>
/// </remarks>
internal static class StickerPicker
{
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>Coarse march steps over the ray before bisection brackets the terrain crossing.</summary>
    private const int TerrainMarchSteps = 64;

    /// <summary>Bisections after the bracket — 2^-24 of the step, i.e. sub-millimetre at 2 km.</summary>
    private const int TerrainBisections = 24;

    /// <summary>What a successful aim resolved to, in the frame the anchor will be stored in.</summary>
    /// <param name="Kind">Vessel or body — which of the two anchor frames the rest describes.</param>
    /// <param name="Vehicle">The hit vehicle (vessel anchors only).</param>
    /// <param name="Part">The hit <b>sub-part</b>, whose local frame <paramref name="Position"/> is in.</param>
    /// <param name="Body">The hit celestial (body anchors only).</param>
    /// <param name="Position">Vessel: part-local metres. Body: <c>(latitudeDeg, longitudeDeg, 0)</c>.</param>
    /// <param name="Normal">Vessel: the part-local unit surface normal. Body: zero (up is the radial).</param>
    /// <param name="Distance">Distance along the ray to the hit, metres.</param>
    /// <param name="RotationDeg">The roll/heading that makes the PNG read upright from here.</param>
    internal readonly record struct PickResult(
        StickerAnchorKind Kind,
        Vehicle? Vehicle,
        Part? Part,
        Celestial? Body,
        double3 Position,
        double3 Normal,
        double Distance,
        double RotationDeg);

    /// <summary>
    ///     Casts the aim ray and returns the nearest anchor within <paramref name="range"/> metres.
    /// </summary>
    /// <param name="cursor">True aims down the mouse cursor's picking ray; false down the screen centre.</param>
    /// <param name="range">Maximum hit distance in metres.</param>
    /// <param name="result">The resolved anchor; undefined when this returns false.</param>
    /// <returns>False when nothing was hit — the caller maps that to ENOENT.</returns>
    [KsaAnchor("Program.GetMainCamera(); Camera.{ScreenToEgoRay(float2),FramebufferSize}; Cursor.InputRay",
        SourceFile = "KSA/Program.cs:569 / KSA/Camera.cs:688,47 / KSA/Cursor.cs:25",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "Both rays are in EGO space (origin at the camera, ecliptic axes) and Ray's "
            + "constructor normalizes Direction (KSA/Ray.cs:11). ScreenToEgoRay takes framebuffer "
            + "pixels, not NDC, and Cursor.InputRay is refreshed from Cursor.UpdateInputRay each "
            + "frame — it is the stale ray of the previous frame if the cursor has not moved, which "
            + "is exactly what the player last saw. The camera aim is the default because it works "
            + "headless and /sim/camera can point it.")]
    internal static bool TryPick(bool cursor, double range, out PickResult result)
    {
        result = default;
        if (Program.GetMainCamera() is not { } camera)
            return false;

        var ray = cursor
            ? Cursor.InputRay
            : camera.ScreenToEgoRay(new float2(camera.FramebufferSize.X * 0.5f, camera.FramebufferSize.Y * 0.5f));
        if (!double.IsFinite(ray.Direction.X) || ray.Direction.LengthSquared() <= 0)
            return false;

        return TryPickVehicle(camera, ray, range, out result)
               || TryPickTerrain(camera, ray, range, out result);
    }

    /// <summary>
    ///     Sweeps every live vehicle within range and keeps the nearest triangle hit.
    /// </summary>
    /// <remarks>
    ///     <para><b>Which frame the hit is in.</b> <c>Part.RayCastEgo</c> loops over the part's
    ///     <c>SubParts</c> and delegates to <c>RayCastEgoSubPart</c>
    ///     (<c>KSA/Part.cs:1918-1952</c>), which raycasts that <i>sub-part's</i> view mesh with
    ///     <c>vertexOffset = subPart.MatrixAsmb2Ego(...)</c> and then inverts exactly that matrix to
    ///     produce <c>nearIntersectionPositionAsmb</c>. So the returned position and normal are in
    ///     the local frame of <c>closestSubPart</c> — <b>not</b> of the top-level part the call was
    ///     made on. The anchor is therefore stored against <c>closestSubPart</c>'s
    ///     <c>InstanceId</c>, and <see cref="StickerAnchors"/> re-derives the same matrix from that
    ///     same object every frame. (A top-level part with no sub-parts cannot be hit at all: the
    ///     loop never runs and the method returns false.)</para>
    ///     <para>The normal is the mesh normal of the triangle's <i>first</i> vertex — flat, not
    ///     barycentrically interpolated (<c>Part.cs:1949</c>). For a projection box a decimetre deep
    ///     that is entirely good enough; it only sets the box's orientation, never the shading, which
    ///     the fragment shader derives from the reconstructed depth instead.</para>
    /// </remarks>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.{BoundingSphereRadiusBody,Parts.Parts,"
            + "GetMatrixAsmb2Ego(Camera)}; Part.{RayCastEgo(in double4x4, Ray, out ...),InstanceId}; "
            + "Camera.GetPositionEgo(IPosition)",
        SourceFile = "KSA/Universe.cs / KSA/Vehicle.cs:518,1202 / KSA/Part.cs:1884,1918 / KSA/Camera.cs:231",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "The identical sweep KSA's own flight-mode hover picking runs (Vehicle.cs:2745-2773): "
            + "broad-phase against the part's bounding sphere scaled by ScaleTotal, then "
            + "Ray.RaycastWatertight over the view mesh's de-indexed double3[] triangle soup. "
            + "minDistance is the ray parameter in ego metres. Bepu raycasts are deliberately NOT used "
            + "— KSA never does, and its colliders are coarse primitives, not the art surface.")]
    private static bool TryPickVehicle(KsaCamera camera, Ray ray, double range, out PickResult result)
    {
        result = default;
        if (Universe.CurrentSystem is not { } system)
            return false;

        var found = false;
        var best = range;
        foreach (var astronomical in system.All.UnsafeAsList())
        {
            if (astronomical is not Vehicle vehicle)
                continue;
            var centre = camera.GetPositionEgo(vehicle);
            if (centre.Length() - vehicle.BoundingSphereRadiusBody > range)
                continue;

            var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
            foreach (var part in vehicle.Parts.Parts)
            {
                if (!part.RayCastEgo(in vehicleMatrix, ray, out var distance, out _,
                        out var positionAsmb, out var normalAsmb, out _, out _,
                        out var closestSubPart, out _))
                    continue;
                if (!(distance >= 0) || !(distance < best))
                    continue;

                var normalLength = normalAsmb.Length();
                if (!double.IsFinite(normalLength) || normalLength <= 0)
                    continue;

                best = distance;
                found = true;
                result = new PickResult(StickerAnchorKind.Vessel, vehicle, closestSubPart ?? part, null,
                    positionAsmb, normalAsmb / normalLength, distance, StickerRules.DefaultRotation);
            }
        }

        return found;
    }

    /// <summary>
    ///     Marches the ray against the CPU height field of the camera's nearby celestial and bisects
    ///     the first crossing.
    /// </summary>
    /// <remarks>
    ///     KSA has no CPU ray-vs-terrain routine (every <c>Ray</c> consumer is a part, gizmo or
    ///     sphere), so this is the shape of <c>TerrainImpactFinder.TryFind</c> — a coarse march plus
    ///     24 bisections over <c>GetTerrainHeightFromDirCcf</c> — driven by a straight line instead of
    ///     a trajectory. All of it happens in body-fixed coordinates (CCF), the one frame in which
    ///     the height field is defined and in which the answer stays valid as the planet turns.
    /// </remarks>
    [KsaAnchor("Camera.{NearbyCelestial,GetPositionEgo}; Celestial.{GetCce2Ccf,GetCcf2Cce,GetCci2Cce,"
            + "MeanRadius,GetTerrainHeightFromDirCcf,GetLatitudeFromCcf,GetLongitudeFromCcf}; "
            + "Vehicle.ComputeEnu2Cce",
        SourceFile = "KSA/Camera.cs:71,231 / KSA/Celestial.cs:540,534,522,91,792,708,743 / "
            + "KSA/Vehicle.cs:2997 / KSA/TerrainImpactFinder.cs:64 (the march+bisect shape)",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "accurate:false is 4 bilinear texel taps (the physics hot path); the final sample uses "
            + "accurate:true, which adds bicubic filtering and the CPU procedural-modifier chain. "
            + "GetLatitudeFromCcf/GetLongitudeFromCcf are STATIC and return DEGREES; both normalize "
            + "internally, so an unnormalized CCF point is fine. Ego axes are ecliptic axes and CCE "
            + "axes are ecliptic axes too, so the ray direction converts to CCF with GetCce2Ccf alone "
            + "— no translation is involved for a direction.")]
    private static bool TryPickTerrain(KsaCamera camera, Ray ray, double range, out PickResult result)
    {
        result = default;
        if (camera.NearbyCelestial is not { } body)
            return false;

        var cce2Ccf = body.GetCce2Ccf();
        // The ray is in ego; the body centre is at bodyEgo, so (rayOrigin - bodyEgo) is the origin
        // relative to the body centre, still in ecliptic axes = CCE. Rotating by cce2Ccf lands in the
        // body-fixed frame the height field is defined in.
        var originCcf = (ray.Origin - camera.GetPositionEgo(body)).Transform(cce2Ccf);
        var directionCcf = ray.Direction.Transform(cce2Ccf);
        if (!double.IsFinite(originCcf.X) || !double.IsFinite(directionCcf.X))
            return false;

        // Starting underground means the camera is inside the terrain; there is no forward crossing
        // to find and marching would report the far wall of the hole.
        if (Depth(body, originCcf, accurate: false) <= 0)
            return false;

        var above = 0.0;
        var below = double.NaN;
        for (var step = 1; step <= TerrainMarchSteps; step++)
        {
            var t = range * step / TerrainMarchSteps;
            if (Depth(body, originCcf + directionCcf * t, accurate: false) <= 0)
            {
                below = t;
                break;
            }

            above = t;
        }

        if (double.IsNaN(below))
            return false;

        for (var i = 0; i < TerrainBisections; i++)
        {
            var middle = 0.5 * (above + below);
            var accurate = i == TerrainBisections - 1;
            if (Depth(body, originCcf + directionCcf * middle, accurate) <= 0)
                below = middle;
            else
                above = middle;
        }

        var hitCcf = originCcf + directionCcf * below;
        var latitude = Celestial.GetLatitudeFromCcf(hitCcf);
        var longitude = Celestial.GetLongitudeFromCcf(hitCcf);
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
            return false;

        result = new PickResult(StickerAnchorKind.Body, null, null, body,
            new double3(latitude, longitude, 0), default, below,
            Heading(body, hitCcf, ray.Direction));
        return true;
    }

    /// <summary>Signed metres of the point above the terrain surface (negative = underground).</summary>
    private static double Depth(Celestial body, double3 pointCcf, bool accurate)
    {
        var radius = pointCcf.Length();
        if (!double.IsFinite(radius) || radius <= 0)
            return double.NaN; // NaN <= 0 is false, so a degenerate sample never reports a hit
        return radius - (body.MeanRadius + body.GetTerrainHeightFromDirCcf(pointCcf / radius, accurate));
    }

    /// <summary>
    ///     The compass bearing that points the PNG's "up" away from the camera along the ground, so a
    ///     sprayed tag reads upright from where the player is standing.
    /// </summary>
    private static double Heading(Celestial body, double3 hitCcf, double3 forwardEgo)
    {
        var hitCce = hitCcf.Transform(body.GetCcf2Cce());
        if (Vehicle.ComputeEnu2Cce(hitCce, body.GetCci2Cce()) is not { } enu2Cce)
            return StickerRules.DefaultRotation;
        var east = double3.UnitX.Transform(enu2Cce);
        var north = double3.UnitY.Transform(enu2Cce);
        var heading = Math.Atan2(double3.Dot(east, forwardEgo), double3.Dot(north, forwardEgo)) * Rad2Deg;
        return double.IsFinite(heading) ? heading : StickerRules.DefaultRotation;
    }
}
