using Brutal.Numerics;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using KSA;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     Samples the live game camera into the game-free <see cref="CameraStatus"/> the whole
///     <c>/sim/camera</c> read surface renders from — the read half of the director
///     (plans/CAMERA_CONTROLS_PLAN.md §6.2).
/// </summary>
/// <remarks>
///     <para>
///         <b>Read-back is the composed effective value, not the last thing written.</b> That is the
///         resync-after-restart property AGENTS.md §7 requires: a client that reconnects mid-shot must
///         be able to <c>cat</c> a leaf and learn what the camera is <i>actually</i> doing, and a
///         channel driven by a track (C3) would otherwise report a stale override forever.
///     </para>
///     <para>
///         <b>Both position spellings are published.</b> <c>pose/position</c> and <c>pose/geo</c> are
///         two spellings of one channel, so whichever one the author used, this fills in the other from
///         the resolved ecliptic point — a client can place the camera in Cartesian metres and read back
///         the latitude it landed on.
///     </para>
///     <para>Game thread only (threading rule 1); the result is handed to
///     <c>CameraStore.PublishStatus</c>, a single volatile swap that every transport reads lock-free.</para>
/// </remarks>
internal static class CameraReader
{
    /// <summary>
    ///     Builds this frame's status snapshot.
    /// </summary>
    /// <param name="viewport">The main viewport (the only one gatOS binds — never index a viewport).</param>
    /// <param name="camera">The camera gatOS drives (the viewport's base camera).</param>
    /// <param name="owned">Whether gatOS currently owns the camera (including during a release blend).</param>
    /// <param name="pose">The composed effective pose this frame.</param>
    /// <param name="anchor">The resolved anchor, for the position back-projection.</param>
    /// <param name="resolvedPositionEcl">The absolute ecliptic point the placement resolved to.</param>
    [KsaAnchor("Viewport.Mode (public field); Camera.{Following,TidalLocking,GetFieldOfView,Orthographic}",
        SourceFile = "KSA/Viewport.cs / KSA/Camera.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "GetFieldOfView() returns RADIANS while SetFieldOfView(float) takes DEGREES — the "
            + "asymmetry is converted here, once, at the boundary, so nothing downstream carries a "
            + "radian. Viewport.Mode is read (not GetCameraMode(), which reads the FRAME viewport).")]
    internal static CameraStatus Sample(Viewport viewport, KsaCamera camera, bool owned,
        in CameraPose pose, in CameraTarget anchor, double3 resolvedPositionEcl)
        => new(
            Owned: owned,
            Mode: ModeOf(viewport.Mode),
            Follow: CameraTargets.Describe(camera.Following),
            Tidal: camera.TidalLocking,
            Pose: WithBothSpellings(pose, anchor, resolvedPositionEcl),
            // Track playback is task C3: the player fields stay at their idle values, which is what
            // camera/playback and camera/set already render as "nothing loaded".
            TrackName: "",
            TrackTMs: 0,
            TrackDurationMs: 0,
            ShotName: "",
            ShotIndex: -1,
            Playback: PlaybackState.Done,
            Rate: 1,
            Loop: false);

    /// <summary>
    ///     Fills in whichever of the two position spellings the author did not write, from the point
    ///     the placement actually resolved to. Failure is silent and total: a Cartesian placement about
    ///     a vessel anchor simply has no geodetic form, and a geodetic one whose frame no longer
    ///     resolves keeps the authored Cartesian value rather than inventing one.
    /// </summary>
    private static CameraPose WithBothSpellings(in CameraPose pose, in CameraTarget anchor,
        double3 resolvedPositionEcl)
    {
        if (!resolvedPositionEcl.IsFinite())
            return pose;

        if (pose.PositionIsGeo)
        {
            // Geodetic is authoritative: back-project it into the Cartesian channel's own frame.
            if (!CameraFrames.TryFrame2Ecl(pose.Frame, anchor, pose.Latitude, pose.Longitude,
                    out var frame2Ecl, out _))
                return pose;
            var origin = pose.Frame == FrameKind.Ecl ? double3.Zero : CameraTargets.PositionEcl(anchor);
            var local = (resolvedPositionEcl - origin).Transform(frame2Ecl.Inverse());
            return local.IsFinite() ? pose with { Position = new Vec3(local.X, local.Y, local.Z) } : pose;
        }

        // Cartesian is authoritative: derive the geodetic form, but only against a body — latitude on
        // a vessel is meaningless, and reporting zeros there would read as a real placement.
        if (anchor.Body is not { } body
            || !CameraFrames.TryEclToGeo(body, resolvedPositionEcl, out var lat, out var lon, out var alt))
            return pose;

        return pose with
        {
            Latitude = lat,
            Longitude = CameraRules.NormalizeLongitude(lon),
            Altitude = alt,
        };
    }

    /// <summary>Maps the game's camera mode onto the <c>/sim</c> token vocabulary.</summary>
    [KsaAnchor("CameraMode enum { Orbit, Free, Map, IVA, Fixed }", SourceFile = "KSA/CameraMode.cs",
        Verified = "2026-08-06", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "The /sim CameraModeKind ordinals match this enum one-for-one, but the mapping is "
            + "written out rather than cast: an inserted member upstream would silently re-label every "
            + "mode on the wire.")]
    internal static CameraModeKind ModeOf(CameraMode mode) => mode switch
    {
        CameraMode.Free => CameraModeKind.Free,
        CameraMode.Map => CameraModeKind.Map,
        CameraMode.IVA => CameraModeKind.Iva,
        CameraMode.Fixed => CameraModeKind.Fixed,
        _ => CameraModeKind.Orbit,
    };

    /// <summary>Maps a <c>/sim</c> mode token onto the game's camera mode.</summary>
    internal static CameraMode ModeOf(CameraModeKind mode) => mode switch
    {
        CameraModeKind.Free => CameraMode.Free,
        CameraModeKind.Map => CameraMode.Map,
        CameraModeKind.Iva => CameraMode.IVA,
        CameraModeKind.Fixed => CameraMode.Fixed,
        _ => CameraMode.Orbit,
    };
}
