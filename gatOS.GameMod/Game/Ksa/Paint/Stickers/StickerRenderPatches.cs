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
    [KsaAnchor("KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer,bool) — Harmony postfix",
        SourceFile = "KSA.Rendering/RenderTarget.cs:315-321 / KSA/Program.cs:4452,4737,4765,4887",
        Verified = "2026-09-14", GameVersion = "2026.9.10.5438", Risk = ChurnRisk.High,
        Notes = "The new inResolveDepth=false call at Program.cs:4737 resolves colour only before the "
            + "final full resolve at :4765; the postfix skips that early call so stickers sample only "
            + "fully resolved depth. Main-target identity and the editor exclusion retain the existing "
            + "main-flight-only scope.")]
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments),
            [typeof(CommandBuffer), typeof(bool)]);
        if (original is null)
            throw new MissingMethodException(typeof(RenderTarget).FullName,
                nameof(RenderTarget.ResolveAttachments));

        var postfix = AccessTools.Method(typeof(StickerRenderPatches), nameof(Postfix));
        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
    }

    /// <summary>Removes the postfix; safe to call when it was never installed.</summary>
    public static void Remove(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments),
            [typeof(CommandBuffer), typeof(bool)]);
        var postfix = AccessTools.Method(typeof(StickerRenderPatches), nameof(Postfix));
        if (original is not null && postfix is not null)
            harmony.Unpatch(original, postfix);
    }

    private static void Postfix(RenderTarget __instance, CommandBuffer inCmdBuffer, bool __1)
    {
        if (!__1 || !StickerManager.Active)
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
