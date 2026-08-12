using Brutal.Numerics;
using gatOS.Logging;
using KSA;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     Where the composed camera pose should be, asked at the instant the engine uses the answer
///     rather than a phase before it. Implemented by <see cref="CameraDirector"/>.
/// </summary>
/// <remarks>
///     The offset must be measured against <paramref name="followedEcl"/> and nothing else: a
///     separation taken from a sample of the same anchor made anywhere earlier in the frame carries a
///     frame of its motion, which is the fault this seam exists to remove.
/// </remarks>
internal interface ICameraPoseSource
{
    /// <param name="camera">The camera being driven (the main viewport's base camera).</param>
    /// <param name="followedEcl">Where the followed anchor is <i>now</i>, from the engine's pass.</param>
    /// <param name="dt">The engine's frame delta for this viewport pass, seconds.</param>
    /// <param name="offsetFromFollowed">The camera's placement as an offset from the anchor.</param>
    /// <param name="rotationEcl">The camera's final ECL orientation (aim, roll and smoothing applied).</param>
    /// <returns>False to leave the camera riding the anchor on its previous offset and rotation.</returns>
    bool TryPose(KsaCamera camera, double3 followedEcl, double dt,
        out double3 offsetFromFollowed, out doubleQuat rotationEcl);
}

/// <summary>
///     KSA's fixed camera controller, subclassed so gatOS's pose is applied <b>inside the engine's own
///     viewport pass</b> — the only moment in the frame that is in phase with the matrices the scene is
///     drawn through (the KSArmory <c>LevelHorizonController</c> pattern).
/// </summary>
/// <remarks>
///     <para>
///         Installed into the public writable <c>Viewport.FixedController</c> field by
///         <see cref="CameraDirector"/>; the stock instance is saved and put back at shutdown. This is
///         ordinary subclassing of a public unsealed class with a virtual <c>OnFrame</c> — no Harmony.
///         While <see cref="Pose"/> is null the controller is a behavioural clone of the stock one
///         (including the "Fixed Camera" alert, which is only suppressed for gatOS's own silent park),
///         so leaving it installed between ownership sessions changes nothing for the player.
///     </para>
///     <para>
///         <b>The write is the same two the engine makes, in the same order:</b> position first (an
///         offset added to the followed object's position from the <i>same sample</i> the solve was
///         handed), then the rotation. A refusal or a fault leaves both alone — the camera keeps riding
///         the followable on its previous offset, which is a hold, not a fling. Nothing here may throw:
///         this runs inside the engine's loop, where an escaped exception is a crashed game.
///     </para>
/// </remarks>
internal sealed class CameraPoseController(KsaCamera camera) : FixedController(camera)
{
    /// <summary>The in-phase pose source; null makes this controller behave exactly like stock.</summary>
    internal ICameraPoseSource? Pose { get; set; }

    /// <summary>
    ///     Set when the pose source threw. The director treats this as "stand down": the camera keeps
    ///     riding the followable on its last pose until the next prefix hands it back to the game.
    /// </summary>
    internal bool Faulted { get; private set; }

    /// <summary>Clears a fault so a later ownership take can try again.</summary>
    internal void ClearFault() => Faulted = false;

    /// <summary>
    ///     The stock alert fires only when the <i>player</i> switches to the fixed camera. gatOS's own
    ///     park must not draw "Fixed Camera" into the footage.
    /// </summary>
    public override void OnSwitchOn(CameraMode lastMode)
    {
        if (Pose is null)
            base.OnSwitchOn(lastMode);
    }

    [KsaAnchor("Viewport.FixedController (public writable field); FixedController(Camera) unsealed, "
            + ".{CameraOffset,CameraRotation} public fields, .OnFrame/.OnSwitchOn virtual; "
            + "Viewport.GetActiveController() dispatches CameraMode.Fixed => FixedController",
        SourceFile = "KSA/Viewport.cs / KSA/FixedController.cs", Verified = "2026-08-11",
        GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "Viewport.OnFrame runs GetActiveController().OnFrame then GetCamera().OnFrame, so a "
            + "pose written here is consumed by this frame's matrices. base.OnFrame crashes the process "
            + "(DivideByZeroException in double3.Normalized) if CameraRotation is zero, so CameraRotation "
            + "is kept non-zero whenever a fallback to base is reachable. FixedController.OnSwitchOn is "
            + "only the TimedAlert(\"Fixed Camera\").")]
    public override void OnFrame(Viewport inViewport, double inDeltaTime)
    {
        if (Pose is not { } source || Faulted)
        {
            base.OnFrame(inViewport, inDeltaTime);
            return;
        }

        if (Camera.Following is not { } following)
            return;

        try
        {
            var followedEcl = following.GetPositionEcl();
            if (!source.TryPose(Camera, followedEcl, inDeltaTime, out var offset, out var rotation))
                return; // hold: the camera keeps riding the followable on its previous offset

            Camera.PositionEcl = followedEcl + offset;
            Camera.LocalRotation = rotation;

            // Keep the stock input fields coherent with what was applied, so any fallback into
            // base.OnFrame (fault, source cleared) continues from this pose instead of a stale or —
            // fatally — zero CameraRotation.
            CameraOffset = offset;
            var forward = (-double3.UnitZ).Transform(rotation);
            if (forward.IsFinite() && !forward.IsNearlyZero())
                CameraRotation = forward;
        }
        catch (Exception ex)
        {
            // Engine loop: never rethrow. One error line, then hold the last pose until the director
            // notices the fault in the next prefix and hands the camera back.
            Faulted = true;
            ModLog.Log.Error($"gatOS camera: in-phase pose solve failed — {ex.Message}");
        }
    }
}
