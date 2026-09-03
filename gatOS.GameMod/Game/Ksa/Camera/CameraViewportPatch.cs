using gatOS.Logging;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     Places gatOS's per-frame bookkeeping inside the main viewport's frame, immediately before KSA
///     runs its active controller and rebuilds the camera matrices.
/// </summary>
/// <remarks>
///     Since the KSArmory-pattern re-implementation (2026-08-11) the prefix no longer writes the
///     camera: it advances shared schedules, drains commands and lets the director <i>prepare</i> this
///     frame's composed pose. The pose itself is applied by <see cref="CameraPoseController"/> — the
///     <c>FixedController</c> subclass the director installs — which KSA's own
///     <c>GameViewport.OnFrame</c> runs right after this prefix, in phase with the matrix build. The
///     postfix still samples KSA's final clamped transform for read-back.
/// </remarks>
internal static class CameraViewportPatch
{
    private static bool _faulted;

    [KsaAnchor("GameViewport.OnFrame(double) (Harmony prefix/postfix; the override of abstract ViewportBase.OnFrame); Program.MainViewport (IGameViewport)",
        SourceFile = "KSA/GameViewport.cs / KSA/ViewportBase.cs / KSA/ViewportRegistry.cs / KSA/Program.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "GameViewport.OnFrame calls GetActiveController().OnFrame then GetCamera().OnFrame, then ViewportAudio only when the viewport HasAudio (main only). "
                + "Program.Viewports holds SIX viewports (the two crew-portrait ones start at "
                + "_crewPortraitViewportStart = 4), so both hooks bind by MainViewport identity. The "
                + "prefix applies the current-frame pose; the postfix samples KSA's final clamped "
                + "transform. 5348: OnFrame(double) is verified unchanged and still the only overload. "
                + "Viewport gained ShouldRenderStars and LightMode (EViewportLightMode) — MainViewport "
                + "is Clustered and secondaries Forward, which evaluate to exactly the previous "
                + "hardcoded UseShadows/UseLightPrePass values, so nothing changes here. The earlier "
                + "'four viewports' count was wrong in both builds; it has always been 6."
            + "5402 (viewport rework): KSA.Viewport is GONE. Program.MainViewport is ViewportRegistry.MainViewport (an IGameViewport whose concrete type is GameViewport); the registry holds 8 = MAX_VIEWPORTS: 1 Main + 1 PartThumbnail (a ViewportBase that is NOT a GameViewport and has its own OnFrame override, so this patch never fires for it) + 4 Secondary + 2 CharacterPortrait, ids 1..8, ShaderSlot from a FreeListIndexPool. Identity binding by MainViewport is unchanged and still the only correct test. OnFrame(double) is still the single overload on GameViewport.")]
    internal static bool Install(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(GameViewport), nameof(GameViewport.OnFrame), [typeof(double)]);
        if (original is null)
        {
            ModLog.Log.Warn("Camera hook: GameViewport.OnFrame(double) not found; programmable camera disabled.");
            return false;
        }

        _faulted = false;
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(CameraViewportPatch), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(CameraViewportPatch), nameof(Postfix)));
        return true;
    }

    private static void Prefix(GameViewport __instance, double dt)
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

    private static void Postfix(GameViewport __instance)
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
