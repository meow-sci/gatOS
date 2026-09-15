using System.Reflection;
using gatOS.GameMod.Game.Ksa;
using gatOS.Logging;
using HarmonyLib;
using KSA;
using KSA.Deformation;

namespace gatOS.GameMod.Game.Ksa.Render;

/// <summary>
///     The <c>/sim/debug/always_render_iva</c> cheat: forces interior (IVA) part meshes to render
///     outside the IVA camera by flipping <c>PartModelModule.Template.Internal</c> to <c>false</c> on
///     every internal template (KSA's render gate skips internal meshes unless the camera is in IVA
///     mode — <c>PartModel.cs</c> <c>(!Template.Internal || viewport.Mode == CameraMode.IVA)</c>).
/// </summary>
/// <remarks>
///     <para>Ported from the sibling <c>unscience</c> mod, with one change: the two Harmony patches are
///     installed <b>only while enabled</b> (on the <c>0→1</c> toggle) and removed on <c>1→0</c>, so the
///     default-off state carries zero patches — minimally invasive, exactly per the feature brief.</para>
///     <para>Game-thread only: <see cref="SetEnabled"/> runs in the command drain, which is the correct
///     thread for both the <see cref="PartModel.Instances"/> bulk flip and Harmony (un)patching. The
///     ctor postfix catches part types first seen after enabling; the (editor-only) AddInstance postfix
///     keeps interiors visible in VAB previews. Flipping the shared <em>template</em> flag is global by
///     design (this is a global cheat); the tracked-template restore + unpatch fully revert it.</para>
/// </remarks>
internal static class IvaForceRender
{
    private static bool _enabled;
    private static Harmony? _harmony;
    private static readonly List<PartModelModule.Template> Mutated = [];

    private static MethodBase? _ctorOriginal;
    private static MethodInfo? _ctorPostfix;
    private static MethodBase? _addInstanceOriginal;
    private static MethodInfo? _addInstancePostfix;

    /// <summary>Whether the cheat is currently on (read into the snapshot for the <c>/sim</c> read-back).</summary>
    public static bool Enabled => _enabled;

    [KsaAnchor("PartModel.Instances; PartModel..ctor(PartModelModule.Template); "
            + "PartModel.AddInstance(PerInstanceData,PerInstanceDent,IViewport,int) private common overload; "
            + "PartModel.ViewportData.Get(PartModel,IViewport).{InstanceList,DentInstanceList}; "
            + "PartModelModule.Template.{Internal,RayTracing}; PartModelModule.RaytracingMode.ShadowProxy; "
            + "Program.{Editor,MainViewport}; IViewport.{Mode,OptionFlags}; ViewportOptionFlags.RenderPartModels; CameraMode.IVA",
        SourceFile = "KSA/PartModel.cs:459-490 / KSA/PartModelModule.cs / KSA/IViewport.cs / KSA/ViewportOptionFlags.cs", Verified = "2026-09-14",
        GameVersion = "2026.9.10.5438", Risk = ChurnRisk.High,
        Notes = "The always_render_iva cheat. Patches are dynamic — installed only while enabled. "
            + "The private common AddInstance overload receives the actual viewport and paired dent "
            + "descriptor; the editor fallback appends both lists, matching KSA's normal path.")]
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

        _addInstanceOriginal = AccessTools.Method(typeof(PartModel), nameof(PartModel.AddInstance),
            [typeof(PartModel.PerInstanceData), typeof(PerInstanceDent), typeof(IViewport), typeof(int)]);
        _addInstancePostfix = typeof(IvaForceRender).GetMethod(nameof(AddInstancePostfix),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony.Patch(_addInstanceOriginal, postfix: new HarmonyMethod(_addInstancePostfix));

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
        _addInstanceOriginal = null;
        _addInstancePostfix = null;
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

    /// <summary>Editor-only: interior previews are never drawn through an IVA camera, so force them in.</summary>
    private static void AddInstancePostfix(PartModel __instance, PartModel.PerInstanceData __0,
        PerInstanceDent __1, IViewport __2)
    {
        try
        {
            // Mirror the common overload's gate so the postfix never adds an instance the engine refused.
            if (!__2.HasAny(ViewportOptionFlags.RenderPartModels))
                return;
            if (Program.Editor is null
                || !__instance.Template.Internal
                || __2.Mode == CameraMode.IVA
                || __instance.Template.RayTracing == PartModelModule.RaytracingMode.ShadowProxy)
                return;
            var viewportData = PartModel.ViewportData.Get(__instance, __2);
            viewportData.InstanceList.Add(__0);
            viewportData.DentInstanceList.Add(__1);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS IVA add-instance postfix error: {ex.Message}");
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
