using gatOS.Logging;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     Places gatOS's camera work inside the main viewport's frame, immediately before KSA runs its
///     active controller and rebuilds the camera matrices.
/// </summary>
internal static class CameraViewportPatch
{
    private static bool _faulted;

    [KsaAnchor("Viewport.OnFrame(double) (Harmony prefix/postfix); Program.MainViewport",
        SourceFile = "KSA/Viewport.cs / KSA/Program.cs", Verified = "2026-08-09",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "Viewport.OnFrame calls GetActiveController().OnFrame then GetCamera().OnFrame. "
                + "Program has four viewports, so both hooks bind by MainViewport identity. The prefix "
                + "applies the current-frame pose; the postfix samples KSA's final clamped transform.")]
    internal static bool Install(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Viewport), nameof(Viewport.OnFrame), [typeof(double)]);
        if (original is null)
        {
            ModLog.Log.Warn("Camera hook: Viewport.OnFrame(double) not found; programmable camera disabled.");
            return false;
        }

        _faulted = false;
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(CameraViewportPatch), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(CameraViewportPatch), nameof(Postfix)));
        return true;
    }

    private static void Prefix(Viewport __instance, double dt)
    {
        if (_faulted || !ReferenceEquals(__instance, Program.MainViewport))
            return;
        try
        {
            Mod.PrepareMainViewportFrame(dt);
        }
        catch (Exception ex)
        {
            _faulted = true;
            ModLog.Log.Error($"gatOS camera viewport prefix disabled after an error: {ex.Message}");
        }
    }

    private static void Postfix(Viewport __instance)
    {
        if (_faulted || !ReferenceEquals(__instance, Program.MainViewport))
            return;
        try
        {
            Mod.PublishMainViewportCameraStatus();
        }
        catch (Exception ex)
        {
            _faulted = true;
            ModLog.Log.Error($"gatOS camera viewport postfix disabled after an error: {ex.Message}");
        }
    }
}
