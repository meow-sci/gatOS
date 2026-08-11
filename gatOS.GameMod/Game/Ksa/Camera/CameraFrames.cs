using Brutal.Numerics;
using gatOS.SimFs.Camera;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     Turns the frame-relative placement vocabulary of <c>/sim/camera/pose</c> into one absolute
///     ecliptic point per frame (plans/CAMERA_CONTROLS_PLAN.md §4.2), and owns the geodetic
///     (<c>lat lon alt</c>) arithmetic behind <c>pose/geo</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything reduces to "express a point in a chosen frame and re-resolve it every
///         frame".</b> That is what makes R5 fall out for free: a <i>static</i> <c>bodyfixed</c>
///         position rides a planet's rotation, so "hold this spot 30 m above the ocean and let the
///         world turn under the shot" is one write, not a keyframed curve chasing a spin.
///     </para>
///     <para>
///         <b>All six frames share the ecliptic's axes at the anchor.</b> A frame's rotation maps its
///         own axes into CCE — which is ECL-axed, differing only in origin — so composing a position is
///         always <c>anchorPositionEcl + offset.Transform(frame2Ecl)</c>. There is no frame mixing
///         anywhere in this file, deliberately: the geodetic path composes its final point through
///         <c>Celestial.GetPositionEclFromCce</c> rather than adding a CCE offset to an ECL point by
///         hand.
///     </para>
///     <para>
///         <b>An unresolvable frame is an error, never a silent substitution.</b>
///         <c>Vehicle.GetEnu2Cce()</c> and <c>GetLvlh2Cce()</c> both return <c>doubleQuat?</c> and both
///         answer null on degenerate state (a vessel at rest at the origin, a radial-only velocity);
///         <c>GetEnu2Cce()</c> additionally dereferences <c>Orbit.Parent</c> with no guard. Falling back
///         to another frame there would move the camera somewhere the author never asked for, so these
///         report failure and the caller maps it to EOPNOTSUPP (at write time) or holds the last good
///         pose (per frame).
///     </para>
///     <para>Game thread only (threading rule 1).</para>
/// </remarks>
internal static class CameraFrames
{
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    ///     Resolves a frame's rotation into ECL about <paramref name="anchor"/>.
    /// </summary>
    /// <param name="frame">The frame to resolve.</param>
    /// <param name="anchor">The resolved anchor (ignored for <see cref="FrameKind.Ecl"/>).</param>
    /// <param name="latitudeDeg">
    ///     The pose's geodetic latitude — used only by <see cref="FrameKind.Enu"/> against a celestial
    ///     anchor, where "the local horizon" is only defined <i>at a surface point</i>.
    /// </param>
    /// <param name="longitudeDeg">The pose's geodetic longitude, for the same reason.</param>
    /// <param name="frame2Ecl">The resolved rotation (identity on failure).</param>
    /// <param name="error">A human-readable reason on failure (the EOPNOTSUPP message).</param>
    /// <returns>True when the frame resolved.</returns>
    [KsaAnchor("Vehicle.{GetEnu2Cce,GetLvlh2Cce,Body2Cce,ComputeEnu2Cce,ComputeLvlh2Cce,GetPositionCce,"
            + "GetVelocityCce}; Celestial.{GetCci2Cce,GetCcf2Cce,GetDirCcfFromLatLon,MeanRadius,"
            + "GetPositionCce,GetVelocityCce}",
        SourceFile = "KSA/Vehicle.cs / KSA/Celestial.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "GetEnu2Cce/GetLvlh2Cce are NULLABLE and GetEnu2Cce dereferences Orbit.Parent "
            + "unguarded — both are guarded here. ComputeEnu2Cce/ComputeLvlh2Cce are the public static "
            + "halves, reused so a celestial anchor gets the game's own ENU/LVLH construction rather "
            + "than a hand-rolled one.")]
    internal static bool TryFrame2Ecl(FrameKind frame, in CameraTarget anchor,
        double latitudeDeg, double longitudeDeg, out doubleQuat frame2Ecl, out string error)
    {
        frame2Ecl = doubleQuat.Identity;
        error = "";

        // ECL is the identity frame and needs no anchor at all; CCE shares the ecliptic's axes and
        // differs from it only in origin, which the caller adds.
        if (frame is FrameKind.Ecl or FrameKind.Cce)
            return true;

        if (!anchor.Found)
        {
            error = $"frame '{CameraFormat.Frame(frame)}' needs pose/anchor to name a live target";
            return false;
        }

        switch (frame)
        {
            case FrameKind.BodyFixed:
                frame2Ecl = CameraTargets.BodyFixed2Ecl(anchor);
                return true;

            case FrameKind.Chase:
                // The game's chase convention is the vessel body frame; a celestial has no such thing.
                if (anchor.Vessel is null)
                {
                    error = "frame 'chase' needs a vessel or part anchor";
                    return false;
                }

                frame2Ecl = CameraTargets.BodyFixed2Ecl(anchor);
                return true;

            case FrameKind.Enu:
                return TryEnu(anchor, latitudeDeg, longitudeDeg, out frame2Ecl, out error);

            case FrameKind.Lvlh:
                return TryLvlh(anchor, out frame2Ecl, out error);

            default:
                error = $"unknown frame '{(int)frame}'";
                return false;
        }
    }

    /// <summary>
    ///     Resolves the composed pose's placement to one absolute ecliptic point, in the fixed
    ///     precedence <b>orbit → geodetic → Cartesian</b>.
    /// </summary>
    /// <remarks>
    ///     The precedence is what makes the three spellings coexist without a mode flag. A non-zero
    ///     <c>pose/orbit/radius</c> is an explicit "place me on a sphere about the anchor" and therefore
    ///     wins; with radius 0 the orbit channels name no placement at all, so the position channel
    ///     (Cartesian or geodetic, whichever is live) applies. Writing <c>0</c> to the radius is how you
    ///     hand placement back to <c>pose/position</c>.
    /// </remarks>
    /// <param name="pose">The composed pose.</param>
    /// <param name="anchor">The resolved anchor.</param>
    /// <param name="positionEcl">The absolute ecliptic point (zero on failure).</param>
    /// <param name="error">A human-readable reason on failure.</param>
    /// <returns>True when the placement resolved.</returns>
    internal static bool TryResolvePosition(in CameraPose pose, in CameraTarget anchor,
        out double3 positionEcl, out string error)
    {
        if (!TryResolvePlacement(pose, anchor, out var placement, out error))
        {
            positionEcl = double3.Zero;
            return false;
        }

        positionEcl = placement.PositionEcl;
        return true;
    }

    /// <summary>
    ///     Resolves both halves of a placement. Keeping the live origin separate from its authored
    ///     component lets the director smooth the latter while following the former exactly.
    /// </summary>
    internal static bool TryResolvePlacement(in CameraPose pose, in CameraTarget anchor,
        out ResolvedPlacement placement, out string error)
    {
        placement = default;

        // Geodetic placement composes through the celestial itself, so no frame mixing can occur.
        if (pose.OrbitRadius <= 0 && pose.PositionIsGeo)
        {
            if (anchor.Body is not { } body)
            {
                error = "pose/geo needs pose/anchor to name a body (body:<id>)";
                return false;
            }

            var positionEcl = GeoToEcl(body, pose.Latitude, pose.Longitude, pose.Altitude);
            error = "";
            if (!positionEcl.IsFinite())
                return Fail(out error, "the geodetic placement is not finite");
            var originEcl = body.GetPositionEcl();
            placement = new ResolvedPlacement(originEcl, positionEcl - originEcl, Relative: true);
            return true;
        }

        if (!TryFrame2Ecl(pose.Frame, anchor, pose.Latitude, pose.Longitude, out var frame2Ecl, out error))
            return false;

        // The spherical resolution comes from CameraPlacement, the game-free definition a track's
        // "mode": "orbit" placement also uses. That is deliberate and load-bearing: re-deriving the
        // trigonometry here would put a track's circle and a hand-written `echo 90 >
        // pose/orbit/azimuth` in two subtly different places (and would lose the 360°-closure fold
        // that keeps a looping orbit bit-identical at the wrap).
        var authored = pose.OrbitRadius > 0
            ? CameraPlacement.Spherical(pose.OrbitRadius, pose.OrbitAzimuth, pose.OrbitElevation)
            : pose.Position;
        var offset = new double3(authored.X, authored.Y, authored.Z);

        // ECL is absolute: the offset IS the point. Every other frame is anchor-relative.
        var relative = pose.Frame != FrameKind.Ecl && anchor.Found;
        var origin = relative ? CameraTargets.PositionEcl(anchor) : double3.Zero;
        var component = offset.Transform(frame2Ecl);
        placement = new ResolvedPlacement(origin, component, relative);
        return placement.PositionEcl.IsFinite()
               || Fail(out error, "the resolved position is not finite");
    }

    /// <summary>
    ///     Geodetic → absolute ecliptic, reproducing the composition <c>Camera.SetLatLon</c> performs
    ///     (via <c>Celestial.GetPositionEclFromLatLon</c>) with the altitude term
    ///     <c>Camera.SetAltitude</c> adds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         gatOS cannot call either of those: both bail out unless <c>_following is Celestial</c>, and
    ///         the director deliberately unfollows. So the axis convention is taken from the game's own
    ///         <c>Celestial.GetDirCcfFromLatLon</c> — <c>z = sin(lat)</c>, <c>x = cos(lat)·cos(lon)</c>,
    ///         <c>y = cos(lat)·sin(lon)</c>, i.e. CCF +Z is the north pole and +X is the prime meridian on
    ///         the equator — <b>by calling it</b> rather than restating the trigonometry, so a convention
    ///         change lands as a behaviour change we inherit rather than a silent divergence.
    ///     </para>
    ///     <para>
    ///         Altitude is above the <i>terrain</i>, not above the mean sphere:
    ///         <c>GetSurfacePositionEclFromDirCce</c> is literally
    ///         <c>GetPositionEclFromCce(dir · (MeanRadius + terrainHeight))</c>, and this adds the
    ///         requested altitude inside the same expression so the whole point is composed in one frame.
    ///         A body with no heightmap returns 0 m of terrain, so altitude is then above the mean sphere.
    ///     </para>
    ///     <para>
    ///         Note that <c>Camera.ClampCamera()</c> runs at the top of every <c>Camera.OnFrame</c> and
    ///         silently pushes the camera to <c>surface + 0.5 m</c> whenever the frame viewport's
    ///         altitude is at or below that — so an altitude request under 0.5 m is corrected by the
    ///         game. That is the ocean-skimming floor, and it is a feature, not something to work around.
    ///     </para>
    /// </remarks>
    [KsaAnchor("Celestial.{GetDirCcfFromLatLon,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius,"
            + "GetPositionEclFromCce}",
        SourceFile = "KSA/Celestial.cs / KSA/Camera.cs (SetLatLon/SetAltitude are the model)",
        Verified = "2026-08-06", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "GetTerrainHeightFromDirCce returns METRES and 0 for a body with no heightmap. "
            + "GetSurfacePositionEclFromDirCce expects a UNIT direction and does not normalize (its "
            + "FromCce sibling does), so the direction is normalized here.")]
    internal static double3 GeoToEcl(Celestial body, double latitudeDeg, double longitudeDeg,
        double altitudeMetres)
    {
        var dirCce = body.GetDirCcfFromLatLon(latitudeDeg, longitudeDeg)
            .Transform(body.GetCcf2Cce()).NormalizeOrZero();
        if (dirCce == default)
            return body.GetPositionEcl();
        var radius = body.MeanRadius + body.GetTerrainHeightFromDirCce(dirCce) + altitudeMetres;
        return body.GetPositionEclFromCce(dirCce * radius);
    }

    /// <summary>
    ///     Absolute ecliptic → geodetic: the exact inverse of <see cref="GeoToEcl"/>, so
    ///     <c>/sim/camera/pose/geo</c> can report the geodetic form of a Cartesian placement (and vice
    ///     versa) — the "publish both spellings" property the compositor's one position channel promises.
    /// </summary>
    /// <returns>False when the point is at the body's centre, where latitude/longitude are undefined.</returns>
    [KsaAnchor("Celestial.{GetPositionCceFromEcl,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius}",
        SourceFile = "KSA/Celestial.cs", Verified = "2026-08-06", GameVersion = "2026.8.5.5168",
        Risk = ChurnRisk.Medium,
        Notes = "Mirrors GetDirCcfFromLatLon exactly: lat = asin(ccf.Z), lon = atan2(ccf.Y, ccf.X) — "
            + "the same pair KSA's own GetLatitudeFromCce/GetLongitudeFromCcf use.")]
    internal static bool TryEclToGeo(Celestial body, double3 positionEcl,
        out double latitudeDeg, out double longitudeDeg, out double altitudeMetres)
    {
        latitudeDeg = longitudeDeg = altitudeMetres = 0;
        var positionCce = body.GetPositionCceFromEcl(positionEcl);
        var radius = positionCce.Length();
        if (!double.IsFinite(radius) || radius <= 0)
            return false;

        var dirCce = positionCce / radius;
        var dirCcf = dirCce.Transform(body.GetCcf2Cce().Inverse());
        latitudeDeg = Math.Asin(Math.Clamp(dirCcf.Z, -1.0, 1.0)) * RadToDeg;
        longitudeDeg = Math.Atan2(dirCcf.Y, dirCcf.X) * RadToDeg;
        altitudeMetres = radius - (body.MeanRadius + body.GetTerrainHeightFromDirCce(dirCce));
        return double.IsFinite(latitudeDeg) && double.IsFinite(longitudeDeg)
               && double.IsFinite(altitudeMetres);
    }

    /// <summary>
    ///     ENU (east / north / up) at the anchor. A vehicle has one natively; a celestial's is only
    ///     defined at a surface point, so it is built at the pose's own geodetic latitude/longitude
    ///     through the game's own <c>Vehicle.ComputeEnu2Cce</c> — the celestial's CCI +Z <i>is</i> its
    ///     spin axis, which is exactly what that routine wants for "north".
    /// </summary>
    private static bool TryEnu(in CameraTarget anchor, double latitudeDeg, double longitudeDeg,
        out doubleQuat frame2Ecl, out string error)
    {
        frame2Ecl = doubleQuat.Identity;
        error = "";

        if (anchor.Vessel is { } vessel)
        {
            // GetEnu2Cce() dereferences Orbit.Parent with no guard of its own.
            if (vessel.Orbit?.Parent is null)
            {
                error = "frame 'enu' needs the anchor vessel to be orbiting a body";
                return false;
            }

            if (vessel.GetEnu2Cce() is not { } enu2Cce)
            {
                error = "frame 'enu' is degenerate for this vessel (it is at its parent's centre)";
                return false;
            }

            frame2Ecl = enu2Cce;
            return true;
        }

        if (anchor.Body is not { } body)
        {
            error = "frame 'enu' needs a vessel, part or body anchor";
            return false;
        }

        var surfaceCce = body.GetDirCcfFromLatLon(latitudeDeg, longitudeDeg)
            .Transform(body.GetCcf2Cce()) * body.MeanRadius;
        if (Vehicle.ComputeEnu2Cce(surfaceCce, body.GetCci2Cce()) is not { } bodyEnu)
        {
            error = "frame 'enu' is degenerate at this latitude (it is on the rotation axis)";
            return false;
        }

        frame2Ecl = bodyEnu;
        return true;
    }

    /// <summary>
    ///     LVLH (the orbital prograde / radial / normal frame) at the anchor. A vehicle answers
    ///     directly; a celestial is composed from its own position and velocity relative to its parent
    ///     through the same public static the vehicle path uses.
    /// </summary>
    private static bool TryLvlh(in CameraTarget anchor, out doubleQuat frame2Ecl, out string error)
    {
        frame2Ecl = doubleQuat.Identity;
        error = "";

        doubleQuat? lvlh;
        if (anchor.Vessel is { } vessel)
            lvlh = vessel.GetLvlh2Cce();
        else if (anchor.Body is { } body)
            lvlh = Vehicle.ComputeLvlh2Cce(body.GetPositionCce(), body.GetVelocityCce());
        else
            lvlh = null;

        if (lvlh is not { } resolved)
        {
            error = "frame 'lvlh' is degenerate for this anchor (no orbital motion to derive it from)";
            return false;
        }

        frame2Ecl = resolved;
        return true;
    }

    /// <summary>Assigns a failure message inside a boolean expression and reports false.</summary>
    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }
}

/// <summary>An ECL placement split into its live origin and authored/smoothed component.</summary>
internal readonly record struct ResolvedPlacement(double3 OriginEcl, double3 ComponentEcl, bool Relative)
{
    internal double3 PositionEcl => OriginEcl + ComponentEcl;
}
