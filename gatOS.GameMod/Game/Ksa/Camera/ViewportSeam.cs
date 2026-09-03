using System.Reflection;
using gatOS.Logging;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     The two writes the camera seam makes on the main viewport that KSA's viewport rework (build
///     2026.9.7.5402) took out of the public surface: installing the pose controller into
///     <c>GameViewport.FixedController</c>, and parking/restoring <c>ViewportBase.Mode</c> directly —
///     i.e. without <c>SetCameraMode</c>'s side effects (the controller switch-off/on pair and
///     <c>Program.ControlledVehicle.ClearHeldPlayerInput()</c>, which would drop a guest's latched
///     <c>ctl/translate</c>/<c>ctl/rotate</c> flags every time gatOS took or released the camera).
/// </summary>
/// <remarks>
///     <para>
///         Up to 5348 both were plain public fields on <c>KSA.Viewport</c>. At 5402 that class is gone:
///         <c>Program.MainViewport</c> is an <c>IGameViewport</c> whose concrete type is
///         <c>GameViewport : ViewportBase</c>, and both members are auto-properties with a
///         <b>protected</b> setter. The compiler-generated setters still exist and are what the engine's
///         own <c>SetCameraMode</c> / constructor use, so the write goes through them by reflection —
///         resolved once, and a miss degrades to "cannot own the camera" (the director's
///         <c>EOPNOTSUPP</c> path) or to <c>SetCameraMode</c> rather than throwing.
///     </para>
///     <para>Game thread only; both writers are called from the director inside the command drain or
///     the viewport prefix.</para>
/// </remarks>
internal static class ViewportSeam
{
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly MethodInfo? FixedControllerSetter =
        typeof(GameViewport).GetProperty(nameof(GameViewport.FixedController), AnyInstance)
            ?.GetSetMethod(nonPublic: true);

    private static readonly MethodInfo? ModeSetter =
        typeof(ViewportBase).GetProperty(nameof(ViewportBase.Mode), AnyInstance)
            ?.GetSetMethod(nonPublic: true);

    private static bool _loggedFixedControllerMiss;
    private static bool _loggedModeMiss;

    /// <summary>
    ///     Installs <paramref name="controller"/> as the viewport's fixed-camera controller. False when
    ///     the setter could not be resolved or the viewport is not a <c>GameViewport</c>.
    /// </summary>
    [KsaAnchor("GameViewport.FixedController { get; protected set; } (compiler-generated setter, "
            + "reached by reflection); GameViewport.GetActiveController() dispatches CameraMode.Fixed "
            + "=> FixedController",
        SourceFile = "KSA/GameViewport.cs / KSA/ViewportBase.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "NEW at 5402: Viewport.FixedController was a public writable field through 5348; the "
            + "viewport rework made it a protected-set auto-property on GameViewport. The engine only "
            + "ever assigns it in the GameViewport constructor, so nothing else contends for the slot. "
            + "The write is reflective, hence compiler-blind: a rename or a move to an init-only / "
            + "field-backed property degrades camera/enabled to EOPNOTSUPP (logged once) instead of "
            + "crashing. Program.MainViewport is an IGameViewport; the concrete type is GameViewport "
            + "(ViewportRegistry.CreateGameViewport). Also used by Shutdown to put the stock instance back.")]
    internal static bool TrySetFixedController(IGameViewport viewport, FixedController controller)
    {
        if (viewport is not GameViewport target || FixedControllerSetter is null)
        {
            if (!_loggedFixedControllerMiss)
            {
                _loggedFixedControllerMiss = true;
                ModLog.Log.Warn("gatOS camera: GameViewport.FixedController setter not found in this "
                                + "build; the programmable camera cannot own the view.");
            }
            return false;
        }

        FixedControllerSetter.Invoke(target, [controller]);
        return true;
    }

    /// <summary>
    ///     Writes the viewport's <c>Mode</c> directly (no switch-off/on pair, no held-input clear).
    ///     False when the setter could not be resolved; callers fall back to <c>SetCameraMode</c>.
    /// </summary>
    [KsaAnchor("ViewportBase.Mode { get; protected set; } (compiler-generated setter, reached by "
            + "reflection); GameViewport.SetCameraMode(CameraMode) is the fallback",
        SourceFile = "KSA/ViewportBase.cs / KSA/GameViewport.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "NEW at 5402: Viewport.Mode was a public field through 5348 (the director assigned it "
            + "directly to park in Fixed and to restore the captured mode, deliberately skipping "
            + "SetCameraMode's ClearHeldPlayerInput). The property setter is protected on ViewportBase, "
            + "so the same silent park now goes through reflection. A miss falls back to SetCameraMode, "
            + "which parks correctly but drops latched ctl/translate + ctl/rotate flags (SPEC §3.4.19) "
            + "and runs the controllers' OnSwitchOff/OnSwitchOn — the CameraPoseController still "
            + "suppresses the 'Fixed Camera' alert on that path.")]
    internal static bool TrySetMode(IViewport viewport, CameraMode mode)
    {
        if (viewport.Mode == mode)
            return true;
        if (viewport is not ViewportBase target || ModeSetter is null)
        {
            if (!_loggedModeMiss)
            {
                _loggedModeMiss = true;
                ModLog.Log.Warn("gatOS camera: ViewportBase.Mode setter not found in this build; "
                                + "falling back to SetCameraMode (held thruster input is cleared on park).");
            }
            return false;
        }

        ModeSetter.Invoke(target, [mode]);
        return true;
    }
}
