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
        return true;
    }

    /// <summary>
    ///     The injected call (render thread, end of <c>RenderGame</c> recording). Records this frame's
    ///     capture into the engine's command buffer when the stream is enabled and being read. A
    ///     managed fault disables the feature for the session (one error log); a record fault never
    ///     escapes into the engine's frame.
    /// </summary>
    internal static void OnRenderGameRecorded(Program program, CommandBuffer cb)
    {
        if (!_active || _faulted)
            return;
        var capture = _capture;
        var surface = _surface;
        if (capture is null || surface is null)
            return;
        var settings = surface.Settings;
        if (!settings.Enabled || !surface.HasReaders)
            return;

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
        SourceFile = "KSA/Program.cs", Verified = "2026-06-19", Risk = ChurnRisk.Medium,
        Notes = "Injects the capture call before the frame's final commandBuffer.End() (Program.cs:4130), where the "
                + "offscreen ColorImage is ShaderReadOnlyOptimal and recording is outside any render pass. Matches the "
                + "single 1-arg End extension; degrades to no injection (feature dark) if the site moves.")]
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
