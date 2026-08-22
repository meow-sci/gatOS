using Brutal.VulkanApi;
using gatOS.Logging;
using HarmonyLib;
using KSA;
using KSA.Rendering;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     The Harmony postfix that injects the sticker decal pass into KSA's frame, immediately after
///     the main viewport's offscreen target resolves its MSAA attachments (STICKERS_PLAN §3.2).
/// </summary>
/// <remarks>
///     <para>This is the one moment in <c>Program.RenderGame</c> where the resolved single-sample
///     scene depth and the scene colour are both current and neither is bound as an attachment —
///     the window KSA's own <c>GridPass</c> uses. It is also gatOS's <b>second</b> render-thread
///     draw injection, alongside <c>thug_life</c>'s (which hooks a different method, uses a different
///     Harmony id and shares nothing with this).</para>
///     <para>Installed by <see cref="StickerManager"/> only while at least one sticker is live and
///     removed on the last one, so with no stickers placed there is no patch at all.</para>
/// </remarks>
internal static class StickerRenderPatches
{
    private static bool _loggedFault;

    /// <summary>Installs the postfix. Throws <see cref="MissingMethodException"/> if the seam moved.</summary>
    [KsaAnchor("KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer) — Harmony postfix",
        SourceFile = "KSA.Rendering/RenderTarget.cs:315 / KSA/Program.cs:4418",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "Program.RenderGame calls RenderedViewport.OffscreenTarget.ResolveAttachments("
            + "commandBuffer) UNCONDITIONALLY at Program.cs:4418 (and at :4174 for secondary "
            + "viewports). The method BODY is MSAA-gated — it does nothing when neither attachment is "
            + "multisampled — but a postfix fires either way, which is exactly what makes this a "
            + "reliable seam at every MSAA setting. It is an instance method, so __instance identifies "
            + "the target: the main viewport's is literally Program.OffscreenTarget (Program.cs:432, "
            + "assigned to MainViewport.OffscreenTarget at :1442). Both that identity check and the "
            + "RenderedViewport == MainViewport check are required — crew-portrait viewports have "
            + "their own targets and their own cameras, and stickers are main-viewport-only in v1. "
            + "There is a THIRD call site: RenderEditor (Program.cs:4447) resolves the SAME "
            + "_offscreenTarget at :4527 after setting _renderedViewportIndex = _mainViewportIndex at "
            + ":4468, so both identity checks pass in the VAB — Program.EditorFlag (:201, the flag "
            + "OnFrame picks the path on at :2221) is the only thing that separates the two.")]
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments));
        if (original is null)
            throw new MissingMethodException(typeof(RenderTarget).FullName,
                nameof(RenderTarget.ResolveAttachments));

        var postfix = AccessTools.Method(typeof(StickerRenderPatches), nameof(Postfix));
        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
    }

    /// <summary>Removes the postfix; safe to call when it was never installed.</summary>
    public static void Remove(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments));
        var postfix = AccessTools.Method(typeof(StickerRenderPatches), nameof(Postfix));
        if (original is not null && postfix is not null)
            harmony.Unpatch(original, postfix);
    }

    private static void Postfix(RenderTarget __instance, CommandBuffer inCmdBuffer)
    {
        if (!StickerManager.Active)
            return;
        try
        {
            // The editor renders the SAME offscreen target through the main viewport index, so both
            // identity checks below pass in the VAB. Stickers are a flight-scene feature: a body
            // anchor still resolves in the editor and would otherwise draw over the hangar.
            if (Program.EditorFlag)
                return;
            // Main viewport only: every other viewport resolves its own target with its own camera,
            // and the decal matrices were composed against the main camera this frame.
            if (!ReferenceEquals(__instance, Program.OffscreenTarget)
                || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport))
                return;
            StickerManager.Instance?.RecordPass(inCmdBuffer);
        }
        catch (Exception ex)
        {
            // A per-frame render exception would spam; log once and let the manager self-disable.
            if (!_loggedFault)
            {
                _loggedFault = true;
                ModLog.Log.Debug($"gatOS sticker render postfix error (logged once): {ex.Message}");
            }
        }
    }
}
