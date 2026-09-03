using System.Reflection;
using System.Reflection.Emit;
using Brutal.VulkanApi;
using gatOS.Logging;
using gatOS.SimFs.Display;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     Injects the screen-stream capture into KSA's render loop (STREAM_PLAN.md §4.1): a Harmony
///     transpiler on <c>Program.RenderGame</c> inserts a call to <see cref="OnRenderGameRecorded"/>
///     immediately before the frame's final <c>commandBuffer.End()</c>. At that point the main
///     viewport's offscreen color image sits in <c>ShaderReadOnlyOptimal</c> (the composite has
///     already sampled it) and recording is outside any render pass, so the capture's transfer
///     commands are legal and ride the engine's own command-buffer submission — no out-of-band
///     queue work, which is what crashed the game.
/// </summary>
/// <remarks>
///     The hook is bound to a live <see cref="FrameCapture"/> + <see cref="DisplaySurface"/> only while
///     gatOS is initialized; until then (and after a capture fault) it is a cheap no-op, so the
///     always-installed patch costs a branch per frame when the stream is off.
/// </remarks>
internal static class DisplayRenderPatch
{
    private static volatile bool _active;
    private static volatile bool _faulted;
    private static FrameCapture? _capture;
    private static DisplaySurface? _surface;

    /// <summary>Binds the hook to the live capture + surface (call after both are constructed).</summary>
    internal static void Bind(FrameCapture capture, DisplaySurface surface)
    {
        _capture = capture;
        _surface = surface;
        _faulted = false;
        _active = true;
    }

    /// <summary>Detaches the hook (the injected call then returns immediately).</summary>
    internal static void Unbind()
    {
        _active = false;
        _capture = null;
        _surface = null;
    }

    /// <summary>
    ///     Whether a frame recorded right now would actually be captured — the hook is bound, has not
    ///     faulted, the stream is enabled and somebody is reading it. Shared by the injected capture
    ///     call and <see cref="UiPixelCullingPrefix"/> so both agree, frame by frame, on whether the
    ///     offscreen color image has to be complete.
    /// </summary>
    private static bool IsCapturing
        => _active && !_faulted && _capture is not null
           && _surface is { Settings.Enabled: true, HasReaders: true };

    /// <summary>
    ///     Patches <c>Program.RenderGame</c> with the capture transpiler. Returns <c>true</c> if the
    ///     patch was applied (the injection site was found); logs and returns <c>false</c> otherwise.
    /// </summary>
    internal static bool Install(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Program), "RenderGame");
        if (original is null)
        {
            ModLog.Log.Warn("Display hook: Program.RenderGame not found; the screen stream is disabled.");
            return false;
        }

        var transpiler = new HarmonyMethod(typeof(DisplayRenderPatch), nameof(Transpiler));
        harmony.Patch(original, transpiler: transpiler);
        InstallUiCullingSuppression(harmony);
        return true;
    }

    /// <summary>
    ///     Suppresses KSA's UI pixel-coverage culling for frames the screen stream captures. Best
    ///     effort: if the target is gone the stream still runs, it just carries the UI-shaped holes
    ///     described on <see cref="UiPixelCullingPrefix"/>.
    /// </summary>
    private static void InstallUiCullingSuppression(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(GameSettings), nameof(GameSettings.UiPixelCulling));
            if (target is null)
            {
                ModLog.Log.Warn("Display hook: GameSettings.UiPixelCulling not found; captured frames "
                                + "may show unshaded gaps where opaque game UI covers the scene.");
                return;
            }

            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(DisplayRenderPatch), nameof(UiPixelCullingPrefix)));
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn("Display hook: could not suppress UI pixel culling for the screen stream "
                            + $"(captured frames may show unshaded gaps): {ex.Message}");
        }
    }

    /// <summary>
    ///     Reports UI pixel culling as off while the screen stream is live.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         KSA 2026.8.22.5348 (rev 5283) added <c>UiCoverageMaskSystem</c>: every screen tile fully
    ///         covered by <b>opaque</b> ImGui UI gets the reverse-Z near plane stamped into the opaque
    ///         pre-pass depth (<c>PrePassRenderer.Render</c> → <c>UiMaskDepthStamp.frag</c>). That depth
    ///         is copied into the main offscreen target (<c>PrePassRenderer.CopyDepthImageToSrc</c>) and
    ///         the scene pass loads it, so every later <c>GreaterOrEqual</c> test under the UI fails via
    ///         early-Z — and clouds, the light pre-pass and the sunbloom merge early-out on the same
    ///         mask. The pixels are invisible locally (the UI is drawn over them) but gatOS captures the
    ///         offscreen color <i>before</i> the UI composite, so a remote reader would see the local
    ///         player's window chrome punched out of the scene as unshaded black.
    ///     </para>
    ///     <para>
    ///         The engine has exactly this problem with its own screenshots and solves it the same way,
    ///         by gating the mask off (<c>!Program.IsScreenshotCaptureActive</c>). We cannot reuse that
    ///         flag (it is driven by <c>ScreenshotCapture</c>), and clearing <c>ActiveThisFrame</c> is
    ///         not enough because consumers sample the tile texture directly — but
    ///         <c>RecordMaskGeneration</c> re-reads this setting every frame and zero-clears the tile
    ///         masks when it is false, which suppresses the stamp <i>and</i> every consumer early-out.
    ///     </para>
    ///     <para>
    ///         Scope is narrow by construction: <c>GameSettings.UiPixelCulling()</c> has a single caller
    ///         in the whole game (<c>UiCoverageMaskSystem.RecordMaskGeneration</c>). Patching the getter
    ///         rather than the backing field deliberately leaves the player's saved setting untouched —
    ///         the optimization returns for free the moment the stream stops.
    ///     </para>
    /// </remarks>
    [KsaAnchor("GameSettings.UiPixelCulling() (Harmony prefix)",
        SourceFile = "KSA/GameSettings.cs:3154 / KSA/UiCoverageMaskSystem.cs:466 / KSA/PrePassRenderer.cs",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "Born at 2026.8.22.5348 (rev 5283). Setting defaults ON, so without this the screen "
                + "stream ships UI-shaped unshaded holes. Single game-side caller "
                + "(UiCoverageMaskSystem.RecordMaskGeneration), which re-reads it per frame and clears "
                + "the tile masks when false. Install is best-effort: a missing target only costs "
                + "capture fidelity, never the stream. "
                + "5402: unchanged — UiPixelCulling() is still a single overload at GameSettings.cs:3154 "
                + "and RecordMaskGeneration (UiCoverageMaskSystem.cs:466) is still its only caller.")]
    private static bool UiPixelCullingPrefix(ref bool __result)
    {
        if (!IsCapturing)
            return true; // stream idle — let the engine keep its optimization

        __result = false;
        return false;
    }

    /// <summary>
    ///     The injected call (render thread, end of <c>RenderGame</c> recording). Records this frame's
    ///     capture into the engine's command buffer when the stream is enabled and being read. A
    ///     managed fault disables the feature for the session (one error log); a record fault never
    ///     escapes into the engine's frame.
    /// </summary>
    internal static void OnRenderGameRecorded(Program program, CommandBuffer cb)
    {
        if (!IsCapturing)
            return;
        var capture = _capture;
        var surface = _surface;
        if (capture is null || surface is null)
            return; // Unbind() raced the check above

        try
        {
            capture.MaybeRecord(program, cb, surface);
        }
        catch (Exception ex)
        {
            _faulted = true;
            ModLog.Log.Error("gatOS screen stream disabled after a capture error "
                             + "(see <LogsDir>/display-capture.log for the per-step trace)", ex);
        }
    }

    /// <summary>
    ///     Inserts <c>OnRenderGameRecorded(this, commandBuffer)</c> just before the method's final
    ///     <c>CommandBuffer.End()</c> call. Robust to the surrounding churn: it locates the single
    ///     1-arg <c>End</c> extension call in the <c>Brutal.VulkanApi</c> namespace and reuses the
    ///     instruction that loads its receiver. If the site is not found the body is returned
    ///     unchanged (the feature simply stays dark) rather than corrupting the method.
    /// </summary>
    [KsaAnchor("Program.RenderGame (Harmony transpiler) + Brutal.VulkanApi.VkDeviceExtensions.End",
        SourceFile = "KSA/Program.cs:4764", Verified = "2026-09-02", GameVersion = "2026.9.7.5402",
        Risk = ChurnRisk.Medium,
        Notes = "Injects the capture call before the frame's final commandBuffer.End() (Program.cs:4764), where the "
                + "offscreen ColorImage is ShaderReadOnlyOptimal and recording is outside any render pass. Matches the "
                + "single 1-arg End extension; degrades to no injection (feature dark) if the site moves. "
                + "RenderGame's tail is _screenshotCapture.OnRenderGameSwapchainGrab(...); Profiler.Gpu.EndFrame("
                + "commandBuffer2); commandBuffer2.End() — EndFrame is not named End and KSA.Profiler is not in "
                + "the Brutal.VulkanApi namespace, so both filters reject it, and the TagRegion using blocks "
                + "emit GpuRegion.Dispose() rather than an inlined End. "
                + "5402: line move only (:4595 -> :4764). Scanning RenderGame (:4470-4765) backwards there is "
                + "still no other Call to a 1-arg End declared in Brutal.VulkanApi — the new UiCoverageMask."
                + "Profiler.End(cb, idx, Section) calls are 3-arg KSA methods, Profiler.MainThread.End() is "
                + "0-arg KSA, and EndRendering/EndRenderPass are differently named. codes[callIdx-1] is still "
                + "the ldloc of commandBuffer2, VkDeviceExtensions.cs has zero diff, RenderGame is still "
                + "single-declared, and RenderEditor's own End() (:4887) is not patched.")]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var callIdx = -1;
        for (var i = codes.Count - 1; i >= 1; i--)
        {
            if (codes[i].opcode == OpCodes.Call
                && codes[i].operand is MethodInfo mi
                && mi.Name == "End"
                && mi.GetParameters().Length == 1
                && mi.DeclaringType?.Namespace == "Brutal.VulkanApi")
            {
                callIdx = i;
                break;
            }
        }

        if (callIdx < 1)
        {
            ModLog.Log.Warn("Display hook: CommandBuffer.End() call not found in RenderGame; "
                            + "the screen stream is disabled (capture not injected).");
            return codes;
        }

        var receiverLoad = codes[callIdx - 1]; // loads commandBuffer2 (the End receiver) by value
        var hook = AccessTools.Method(typeof(DisplayRenderPatch), nameof(OnRenderGameRecorded));
        var inserts = new List<CodeInstruction>
        {
            new(OpCodes.Ldarg_0),                                      // Program this
            new(receiverLoad.opcode, receiverLoad.operand),           // commandBuffer2
            new(OpCodes.Call, hook),
        };

        // Carry any branch labels from the receiver load onto our first inserted instruction so a jump
        // to that position still runs the hook (then falls through into the original End sequence).
        inserts[0].labels.AddRange(receiverLoad.labels);
        receiverLoad.labels.Clear();

        codes.InsertRange(callIdx - 1, inserts);
        return codes;
    }
}
