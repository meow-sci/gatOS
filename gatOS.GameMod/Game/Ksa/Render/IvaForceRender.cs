using System.Reflection;
using gatOS.GameMod.Game.Ksa;
using gatOS.Logging;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Render;

/// <summary>
///     The <c>/sim/debug/always_render_iva</c> cheat: forces interior (IVA) part meshes to render
///     outside the IVA camera by flipping <c>PartModelModule.Template.Internal</c> to <c>false</c> on
///     every internal template (KSA's render gate skips internal meshes unless the camera is in IVA
///     mode — since 5482 re-evaluated per batch, per frame in <c>PartTreeRenderData.Compose</c>
///     <c>(!Template.Internal || viewport.RendersInterior())</c>; the raytraced-IVA branch still
///     routes through <c>PartModel.AddInstance</c>'s identical gate. Since 5638 <c>RendersInterior()</c>
///     is IVA mode <i>or</i> a viewport flagged <c>InsideVehicle</c>, e.g. a crew portrait).
/// </summary>
/// <remarks>
///     <para>Ported from the sibling <c>unscience</c> mod, with one change: the Harmony patch is
///     installed <b>only while enabled</b> (on the <c>0→1</c> toggle) and removed on <c>1→0</c>, so the
///     default-off state carries zero patches — minimally invasive, exactly per the feature brief.</para>
///     <para>Game-thread only: <see cref="SetEnabled"/> runs in the command drain, which is the correct
///     thread for both the <see cref="PartModel.Instances"/> bulk flip and Harmony (un)patching. The
///     ctor postfix catches part types first seen after enabling. The flip alone covers VAB previews too:
///     editor trees render through the same <c>PartTree.UpdateRenderData</c> → <c>Compose</c> path. (The
///     editor-only <c>AddInstance</c> re-add postfix unscience keeps was dropped at 5482 — <c>Compose</c>
///     no longer calls <c>AddInstance</c> on the rasterized path, and while enabled no flipped template
///     reaches that postfix's <c>Template.Internal</c> condition anyway.) Flipping the shared
///     <em>template</em> flag is global by design (this is a global cheat); the tracked-template restore +
///     unpatch fully revert it.</para>
/// </remarks>
internal static class IvaForceRender
{
    private static bool _enabled;
    private static Harmony? _harmony;
    private static readonly List<PartModelModule.Template> Mutated = [];

    private static MethodBase? _ctorOriginal;
    private static MethodInfo? _ctorPostfix;

    /// <summary>Whether the cheat is currently on (read into the snapshot for the <c>/sim</c> read-back).</summary>
    public static bool Enabled => _enabled;

    [KsaAnchor("PartModel.Instances; PartModel..ctor(PartModelModule.Template) (protected); "
            + "PartModelModule.Template.Internal; the render gate (!Template.Internal || viewport.RendersInterior()) "
            + "in PartTreeRenderData.Compose and PartModel.AddInstance",
        SourceFile = "KSA/PartTreeRenderData.cs:918,1170,1271 / KSA/PartModel.cs:430,486 / "
            + "KSA/ViewportEx.cs:88-95 / KSA/PartModelModule.cs:31", Verified = "2026-10-23",
        GameVersion = "2026.10.19.5638", Risk = ChurnRisk.High,
        Notes = "The always_render_iva cheat. The ctor patch is dynamic — installed only while enabled. "
            + "5482 (rev 5456): part rendering moved into the cached PartTreeRenderData. RebuildAll batches "
            + "EVERY PartModelModule (internals included) and Compose re-reads model.Template.Internal per batch "
            + "every frame, so the flip takes effect with no cache invalidation. Compose writes the rasterized "
            + "instances straight into ViewportData.InstanceList (AddInstance is reached only on the "
            + "raytraced-IVA branch, where the gate passes anyway), so the former editor-only AddInstance re-add "
            + "postfix could never fire and was removed; editor trees use the same Compose path. "
            + " 5638: the gate's second operand is now viewport.RendersInterior() (IVA mode, or "
            + "ViewportStateFlags.InsideVehicle — set by CrewPortraitPanel), at all three PartTreeRenderData "
            + "sites and PartModel.AddInstance. Flipping Template.Internal still clears the first operand, so the "
            + "cheat is unchanged. The ctor (77 B IL) is pinned non-inlinable by InlineGuard because KSA is now "
            + "JIT-optimised and this patch installs mid-session.")]
    public static void SetEnabled(bool value)
    {
        if (_enabled == value)
            return;
        _enabled = value;
        if (value)
        {
            InstallPatches();
            ForceInternalVisible();
        }
        else
        {
            RestoreInternalHidden();
            RemovePatches();
        }
    }

    private static void InstallPatches()
    {
        if (_harmony is not null)
            return;
        _harmony = new Harmony("gatos.iva");

        _ctorOriginal = AccessTools.Constructor(typeof(PartModel), [typeof(PartModelModule.Template)]);
        _ctorPostfix = typeof(IvaForceRender).GetMethod(nameof(CtorPostfix),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony.Patch(_ctorOriginal, postfix: new HarmonyMethod(_ctorPostfix));

        ModLog.Log.Info("gatOS IVA force-render patches installed.");
    }

    private static void RemovePatches()
    {
        try
        {
            _harmony?.UnpatchAll("gatos.iva");
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS IVA unpatch error: {ex.Message}");
        }

        _harmony = null;
        _ctorOriginal = null;
        _ctorPostfix = null;
        ModLog.Log.Info("gatOS IVA force-render patches removed.");
    }

    /// <summary>Catch part types built after the toggle was enabled (templates not present at enable time).</summary>
    private static void CtorPostfix(PartModel __instance)
    {
        try
        {
            if (!_enabled || !__instance.Template.Internal)
                return;
            __instance.Template.Internal = false;
            if (!Mutated.Contains(__instance.Template))
                Mutated.Add(__instance.Template);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS IVA ctor postfix error: {ex.Message}");
        }
    }

    private static void ForceInternalVisible()
    {
        Mutated.Clear();
        foreach (var pm in PartModel.Instances)
            if (pm.Template.Internal)
            {
                Mutated.Add(pm.Template);
                pm.Template.Internal = false;
            }

        ModLog.Log.Info($"gatOS IVA force-render: {Mutated.Count} internal templates made visible.");
    }

    private static void RestoreInternalHidden()
    {
        foreach (var t in Mutated)
            t.Internal = true;
        ModLog.Log.Info($"gatOS IVA force-render: {Mutated.Count} internal templates restored.");
        Mutated.Clear();
    }
}
