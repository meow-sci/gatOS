using Brutal.VulkanApi;
using gatOS.Logging;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.ThugLife;

/// <summary>
///     The Harmony postfix that injects the per-frame thug-life quad draws into KSA's offscreen main
///     scene pass (ported from the sibling <c>unscience</c> mod). <c>SuperMeshRenderSystem.RenderMainPass</c>
///     (<c>(IViewport, CommandBuffer)</c> since 5482) runs inside the already-begun offscreen render pass on
///     the supplied command buffer, so a postfix
///     appends our draws after KSA's opaque mesh draws and before the caller ends the pass. Installed by
///     <see cref="ThugLifeManager"/> only while ≥1 entry exists, removed when the last is gone.
/// </summary>
internal static class ThugLifeRenderPatches
{
    private static bool _loggedFault;

    [KsaAnchor("SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer) — Harmony postfix",
        SourceFile = "KSA/SuperMeshRenderSystem.cs:364 / KSA/Program.cs:4496,4756,4964", Verified = "2026-09-25",
        GameVersion = "2026.9.22.5482", Risk = ChurnRisk.High,
        Notes = "The only injection point for a world-space draw into KSA's offscreen scene pass. "
            + "Dynamic — installed only while a thug-life entry exists. 5348: re-verified — still "
            + "exactly ONE RenderMainPass overload in the whole tree, so both AccessTools lookups "
            + "(Apply and Remove) stay unambiguous; signature RenderMainPass(CommandBuffer) unchanged. "
            + "The body is now wrapped in using (commandBuffer.TagRegion(Profiler.GpuTag.MeshRendererV2)) "
            + "and a Harmony postfix runs after that finally, so our draws are attributed outside the "
            + "MeshRendererV2 GPU tag — profiler attribution only, recording after the end timestamp is "
            + "legal and nothing mis-draws."
            + "5402: still exactly one RenderMainPass overload (now :347) and three call sites; the body gained the two-sided skinned technique (MeshRendererSkinnedPbrTwoSided, drawn inside its own SkinnedTwoSidedMeshes GPU tag) — additional opaque draws before this postfix, nothing that changes the pass state the quad draws into."
            + " 5482 (rev 5474 bucket rework): signature is now RenderMainPass(IViewport viewport, CommandBuffer "
            + "commandBuffer) and the body delegates to RenderPass(ViewHandle, PassId…) over the new MeshPassBucketSystem "
            + "(MeshBucketSystem/ShadowBucketSystem deleted). Still exactly one overload, so the name-only lookup stays "
            + "unambiguous, and the postfix binds commandBuffer BY NAME, so it resolves to argument 1 unchanged. "
            + "RenderPass's new drawCommandCount==0 early return is inside the callee, so this postfix still runs. "
            + "Three call sites unchanged in role (RenderViewport/RenderGame/RenderEditor), now passing RenderedViewport.")]
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(
            typeof(SuperMeshRenderSystem),
            nameof(SuperMeshRenderSystem.RenderMainPass));
        if (original is null)
            throw new MissingMethodException(
                typeof(SuperMeshRenderSystem).FullName,
                nameof(SuperMeshRenderSystem.RenderMainPass));

        var postfix = AccessTools.Method(typeof(ThugLifeRenderPatches), nameof(RenderMainPassPostfix));
        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
    }

    public static void Remove(Harmony harmony)
    {
        var original = AccessTools.Method(
            typeof(SuperMeshRenderSystem),
            nameof(SuperMeshRenderSystem.RenderMainPass));
        var postfix = AccessTools.Method(typeof(ThugLifeRenderPatches), nameof(RenderMainPassPostfix));
        if (original is not null && postfix is not null)
            harmony.Unpatch(original, postfix);
    }

    private static void RenderMainPassPostfix(CommandBuffer commandBuffer)
    {
        if (!ThugLifeManager.Active)
            return;
        try
        {
            ThugLifeManager.Instance?.RecordDraws(commandBuffer);
        }
        catch (Exception ex)
        {
            // A per-frame render exception would spam; log once and let the manager self-disable.
            if (!_loggedFault)
            {
                _loggedFault = true;
                ModLog.Log.Debug($"gatOS thug-life render postfix error (logged once): {ex.Message}");
            }
        }
    }
}
