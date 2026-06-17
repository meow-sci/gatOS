using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Camera focus (<c>ctl/focus</c> on a vessel, <c>bodies/&lt;id&gt;/focus</c> on a celestial):
///     point the main view at any <see cref="Astronomical"/> — vehicle or body — the way the game's
///     own <c>follow</c> terminal action does (<see cref="Camera.SetFollow"/>). Uses the deterministic
///     <see cref="Program.GetMainCamera"/> (not the mouse-hovered viewport, which is meaningless when
///     the player is typing in the SSH terminal) and <c>changeControl: false</c> so it only moves the
///     camera — it never switches or clears the controlled vessel (that is <c>debug.control_vessel</c>).
///     A pure view op, so it is exempt from the authority gate. Game-thread only.
/// </summary>
internal static class CameraActuator
{
    [KsaAnchor("Program.GetMainCamera().SetFollow(IFollowable, tidalLocking:true, changeControl:false)",
        SourceFile = "KSA/Program.cs / KSA/Camera.cs", Verified = "2026-06-16", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the game's `follow` terminal action; Astronomical (Vehicle and celestials) is "
            + "IFollowable. changeControl:false leaves Program.ControlledVehicle untouched.")]
    internal static CommandResult Focus(Astronomical target)
    {
        var camera = Program.GetMainCamera();
        if (camera is null)
            return new CommandResult(CommandOutcome.Busy, "no active camera to move");
        camera.SetFollow(target, tidalLocking: true, changeControl: false);
        return CommandResult.Ok;
    }
}
