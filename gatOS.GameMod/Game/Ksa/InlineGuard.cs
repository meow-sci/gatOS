using System.Reflection;
using Brutal.VulkanApi;
using gatOS.GameMod.Game.Ksa.Paint;
using gatOS.Logging;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     Keeps the JIT from inlining the KSA methods gatOS patches <b>lazily</b>, so those features
///     still take effect at every call site when they are switched on mid-session.
/// </summary>
/// <remarks>
///     <para>
///         Since 2026.10.19.5638 (rev 5630) KSA ships with full compiler optimisations
///         (<c>DebuggableAttribute</c> went from DisableOptimizations to IgnoreSymbolStoreSequencePoints
///         only), so the JIT now inlines small KSA methods into their callers. Harmony marks a method
///         non-inlinable when it patches it, but that cannot reach into callers that were already
///         compiled with an inlined copy. The always-on hooks install at load and are unaffected; the
///         lazy ones (<c>always_render</c>, <c>always_render_iva</c>, <c>thug_life</c>, stickers, paint)
///         go in long after the hot render callers have tiered up, and would silently miss every
///         inlined call site.
///     </para>
///     <para>
///         The fix is one empty prefix per lazy target, installed at load before anything else
///         (in particular before the display transpiler compiles its <c>Program.RenderGame</c>
///         replacement, which calls <c>RenderMainPass</c> and <c>ResolveAttachments</c>). Each
///         feature still installs and removes its own patch exactly as before; this guard only
///         pins the targets as patched methods. Cost: one empty call per invocation. A target that
///         fails to resolve is logged and skipped; its feature's own install then reports the miss.
///     </para>
/// </remarks>
internal static class InlineGuard
{
    /// <summary>
    ///     The guard's Harmony owner id. Code that inspects a target's patch list for foreign patches
    ///     (paint's shader-compiler conflict check) must ignore this owner: its prefix is void and
    ///     never skips or alters the original.
    /// </summary>
    internal const string HarmonyId = "gatos.inline_guard";

    private static Harmony? _harmony;

    /// <summary>Number of targets currently guarded (status/logging only).</summary>
    internal static int GuardedCount { get; private set; }

    /// <summary>Installs the guard prefixes. Idempotent; never throws.</summary>
    [KsaAnchor("Inline guard over every lazily patched KSA method: Vehicle.GetWorldMatrix(Camera), "
            + "Vehicle.UpdateRenderData(IViewport,int), PartModel..ctor(PartModelModule.Template), "
            + "SuperMeshRenderSystem.RenderMainPass(IViewport,CommandBuffer), "
            + "RenderTarget.ResolveAttachments(CommandBuffer,bool), "
            + "PartTreeRenderData.WriteState(Batch,int,Part) / WriteDynamicState(DynamicBatch,int,PartModelDynamicModule), "
            + "ShaderModuleUtils.FromFile(Device,string,ref VkShaderStageFlags,CompileOptions?)",
        SourceFile = "KSA/Vehicle.cs:3723,3740 / KSA/PartModel.cs:430 / KSA/SuperMeshRenderSystem.cs:375 / "
            + "KSA.Rendering/RenderTarget.cs / KSA/PartTreeRenderData.cs:1094,1216 / RenderCore/ShaderModuleUtils.cs",
        Verified = "2026-10-23", GameVersion = "2026.10.19.5638", Risk = ChurnRisk.High,
        Notes = "Born at 5638 (rev 5630, optimised KSA build). IL sizes at 5638: GetWorldMatrix 116 B, "
            + "UpdateRenderData 110 B (virtual; per-vehicle-per-frame call in Program, a guarded-devirtualization "
            + "candidate), PartModel ctor 77 B, RenderMainPass 112 B, ResolveAttachments 336 B, WriteState 215 B, "
            + "WriteDynamicState 249 B, FromFile 440 B. KSA.dll and Planet.Render.Core.dll are not ReadyToRun, so "
            + "nothing is precompiled with inlining before load. Must install before InstallDisplayHook. The "
            + "signatures here must match each feature's own resolution (VesselForceRender, IvaForceRender, "
            + "ThugLifeRenderPatches, StickerRenderPatches, PartPaintPatches).")]
    internal static void Install()
    {
        if (_harmony is not null)
            return;

        var harmony = new Harmony(HarmonyId);
        var prefix = new HarmonyMethod(typeof(InlineGuard), nameof(NoOp));
        var guarded = 0;
        foreach (var (label, resolve) in Targets())
        {
            try
            {
                if (resolve() is not { } target)
                {
                    ModLog.Log.Warn($"gatOS inline guard: {label} not found in this build; skipped.");
                    continue;
                }

                harmony.Patch(target, prefix: prefix);
                guarded++;
            }
            catch (Exception ex)
            {
                ModLog.Log.Warn($"gatOS inline guard: could not guard {label}: {ex.Message}");
            }
        }

        _harmony = harmony;
        GuardedCount = guarded;
        ModLog.Log.Info($"gatOS inline guard installed on {guarded} lazily patched KSA method(s).");
    }

    /// <summary>Removes every guard prefix (unload). Never throws.</summary>
    internal static void Remove()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS inline guard unpatch error: {ex.Message}");
        }

        _harmony = null;
        GuardedCount = 0;
    }

    private static IEnumerable<(string Label, Func<MethodBase?> Resolve)> Targets()
    {
        yield return ("Vehicle.GetWorldMatrix",
            () => AccessTools.Method(typeof(Vehicle), nameof(Vehicle.GetWorldMatrix), [typeof(KsaCamera)]));
        yield return ("Vehicle.UpdateRenderData",
            () => AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdateRenderData),
                [typeof(IViewport), typeof(int)]));
        yield return ("PartModel..ctor(Template)",
            () => AccessTools.Constructor(typeof(PartModel), [typeof(PartModelModule.Template)]));
        yield return ("SuperMeshRenderSystem.RenderMainPass",
            () => AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.RenderMainPass)));
        yield return ("RenderTarget.ResolveAttachments",
            () => AccessTools.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments),
                [typeof(CommandBuffer), typeof(bool)]));
        yield return ("PartTreeRenderData.WriteState",
            () => PartTreeMethod("WriteState", "Batch", typeof(Part)));
        yield return ("PartTreeRenderData.WriteDynamicState",
            () => PartTreeMethod("WriteDynamicState", "DynamicBatch", typeof(PartModelDynamicModule)));
        yield return ("ShaderModuleUtils.FromFile", () => PartPaintPatches.FromFileMethod);
    }

    private static MethodBase? PartTreeMethod(string name, string batchType, Type third)
    {
        var batch = typeof(PartTreeRenderData).GetNestedType(batchType, BindingFlags.NonPublic);
        return batch is null ? null : AccessTools.Method(typeof(PartTreeRenderData), name, [batch, typeof(int), third]);
    }

    private static void NoOp()
    {
    }
}
