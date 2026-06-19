using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using gatOS.Logging;
using gatOS.SimFs.Display;
using gatOS.Vm;
using KSA;

namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     The screen-stream frame capture (STREAM_PLAN.md §4.1). Each enabled, throttled frame it
///     <b>records</b> a GPU downscale-blit of the main viewport's offscreen scene image plus a
///     copy-to-host into the engine's <i>own</i> per-frame command buffer (injected right before
///     <c>commandBuffer.End()</c> by <see cref="DisplayRenderPatch"/>), then reads the small result
///     back a few frames later and hands the BGRA bytes to the game-free <see cref="DisplaySurface"/>.
/// </summary>
/// <remarks>
///     <para><b>Why in-band.</b> The capture commands ride the frame's command buffer and are
///     submitted by the engine on the render thread — there is no separate queue submit and no
///     <c>Device.WaitIdle</c>. This is exactly how KSA's own readbacks work (PlanetMapExporter,
///     OceanFFT) and what the engine authors prescribed: doing GPU work out-of-band (a private
///     command buffer submitted alongside the engine's in-flight frames) corrupts the device and
///     crashes the game.</para>
///     <para><b>Deferred readback (no stall).</b> The copy targets a host-visible staging buffer in
///     a ring slot indexed by <c>ResourceFrameIndex</c> (0..<c>MaxFramesInFlight</c>-1). The next time
///     a slot is revisited the engine has already waited on that slot's fence (frames-in-flight reuse),
///     so the prior copy is guaranteed complete and the buffer is safe to map — no fence wait of our
///     own. We read a slot back at the start of its next visit, then overwrite it.</para>
///     <para><b>Threading.</b> Everything here runs on the render thread inside <c>RenderGame</c>'s
///     recording (threading rule 1): resource creation, command recording and the (cheap) map +
///     HDR→BGRA8 conversion. The conversion clamps the half-float HDR scene to [0,1] (bright areas
///     clip — the pre-tonemap caveat).</para>
///     <para>Engine types (the renderer + allocator) are reached by inference + interface constraints,
///     never named — their assemblies are only transitively visible to gatOS.</para>
/// </remarks>
internal sealed class FrameCapture : IDisposable
{
    // Per-slot ring resources (ImageEx/BufferEx are value types; validity tracked with the bool flags).
    private ImageEx[] _scratch = [];
    private bool[] _hasScratch = [];
    private BufferEx[] _staging = [];
    private bool[] _hasStaging = [];
    private int[] _allocW = [];
    private int[] _allocH = [];
    private bool[] _pending = []; // a copy was recorded into this slot and not yet read back
    private int _slots;           // == MaxFramesInFlight once initialized (0 before)

    private byte[] _convert = [];
    private long _lastCaptureTs; // Stopwatch ticks of the last recorded capture (FPS throttle)
    private bool _disposed;

    // One-shot trace breadcrumbs (the GPU work runs in native code; a fault there is a process crash,
    // not a managed exception, so the last line written localizes the faulting step).
    private TextWriter? _trace;
    private bool _traceFailed;
    private bool _loggedRecord;
    private bool _loggedReadback;

    /// <summary>
    ///     Entry point from the render hook (<see cref="DisplayRenderPatch"/>): if the per-FPS throttle
    ///     has elapsed, records this frame's capture into <paramref name="cb"/> (the engine's frame
    ///     command buffer, with the offscreen color image in <c>ShaderReadOnlyOptimal</c> and outside
    ///     any render pass) and reads back the slot's previous frame. Never the caller's hot path when
    ///     throttled — a couple of field reads and a return.
    /// </summary>
    [KsaAnchor("Program.GetRenderer(), Program.MainViewport.OffscreenTarget.ColorImage/.Extent, "
               + "Renderer.Allocator/.MaxFramesInFlight, Program.ResourceFrameIndex; Allocator.CreateImage/"
               + "CreateBuffer, CommandBuffer.PipelineBarrier/BlitImage/CopyImageToBuffer, BufferEx.Map",
        SourceFile = "KSA/Program.cs", Verified = "2026-06-19", Risk = ChurnRisk.Medium,
        Notes = "In-band capture recorded into the engine's frame command buffer; mirrors KSA/PlanetMapExporter.cs "
                + "readback. The offscreen ColorImage is ShaderReadOnlyOptimal (SampledReadVfc) at RenderGame's end "
                + "(Program.cs:4125) and is restored. Deferred readback via frames-in-flight slot reuse (no fence wait).")]
    public void MaybeRecord(Program program, CommandBuffer cb, DisplaySurface surface)
    {
        var settings = surface.Settings;

        var now = Stopwatch.GetTimestamp();
        var minTicks = Stopwatch.Frequency / Math.Max(1, settings.Fps);
        if (_lastCaptureTs != 0 && now - _lastCaptureTs < minTicks)
            return; // throttle to the configured stream FPS, decoupled from the game frame rate

        var renderer = Program.GetRenderer();
        if (renderer is null)
            return;
        if (Program.MainViewport?.OffscreenTarget is not { } offscreen)
            return;

        var srcExtent = offscreen.Extent;
        if (srcExtent.Width <= 0 || srcExtent.Height <= 0)
            return;

        // Clamp the downscale target to the source so the blit only ever shrinks (an upscale would
        // just waste bandwidth re-magnifying the scene).
        var targetW = Math.Min(settings.Width, srcExtent.Width);
        var targetH = Math.Min(settings.Height, srcExtent.Height);
        if (targetW <= 0 || targetH <= 0)
            return;

        using (surface.CaptureStat.Measure()) // render-thread cost: record + the deferred map/convert
            RecordInto(renderer.Allocator, cb, program.ResourceFrameIndex, renderer.MaxFramesInFlight,
                offscreen.ColorImage.Image, srcExtent.Width, srcExtent.Height, targetW, targetH, surface);
        _lastCaptureTs = now;
    }

    // Generic on the allocator: the concrete KSA allocator type lives in an assembly gatOS does not
    // reference directly (only its Brutal.VulkanApi.Abstractions interfaces are nameable here), so the
    // type is reached by inference + these interface constraints rather than by name.
    private void RecordInto<TAllocator>(TAllocator allocator, CommandBuffer cb, int idx, int framesInFlight,
        VkImage srcImage, int srcW, int srcH, int targetW, int targetH, DisplaySurface surface)
        where TAllocator : IImageAllocator, IBufferAllocator
    {
        EnsureArrays(framesInFlight);
        if (idx < 0 || idx >= _slots)
            return; // defensive: ResourceFrameIndex out of the expected range

        // 1) Read back this slot's previous copy. The engine waited on this slot's fence when it
        //    reacquired the slot for the current frame, so that copy is complete — no fence wait here.
        //    Doing this before EnsureSlot also makes a resize safe (the buffer is free to reallocate).
        if (_pending[idx])
        {
            Readback(idx, surface);
            _pending[idx] = false;
        }

        // 2) (Re)allocate the slot's scratch image + host staging buffer for the current target size.
        EnsureSlot(allocator, idx, targetW, targetH);

        // 3) Record the downscale-blit + copy-to-host into the engine's command buffer.
        RecordCopy(cb, idx, srcImage, srcW, srcH, targetW, targetH);
        _pending[idx] = true;

        if (!_loggedRecord)
        {
            _loggedRecord = true;
            Trace($"first capture recorded (slot {idx}/{_slots}, src {srcW}x{srcH} -> {targetW}x{targetH})");
        }
    }

    private void EnsureArrays(int framesInFlight)
    {
        if (_slots == framesInFlight && _slots > 0)
            return;
        // First init (frames-in-flight is fixed for the renderer's life, so this runs once).
        _slots = Math.Max(1, framesInFlight);
        _scratch = new ImageEx[_slots];
        _hasScratch = new bool[_slots];
        _staging = new BufferEx[_slots];
        _hasStaging = new bool[_slots];
        _allocW = new int[_slots];
        _allocH = new int[_slots];
        _pending = new bool[_slots];
    }

    private void EnsureSlot<TAllocator>(TAllocator allocator, int idx, int targetW, int targetH)
        where TAllocator : IImageAllocator, IBufferAllocator
    {
        if (_hasScratch[idx] && _allocW[idx] == targetW && _allocH[idx] == targetH)
            return; // already sized for this slot

        if (_hasScratch[idx])
            _scratch[idx].Dispose();
        _scratch[idx] = allocator.CreateImage(new ImageEx.CreateInfo
        {
            Name = "gatOS Display Capture",
            ImageType = VkImageType._2D,
            // Same format as the offscreen scene image so the downscale blit is a pure resample (no
            // driver-fragile cross-format conversion); the HDR→BGRA8 step happens on the CPU.
            ImageFormat = VkFormat.R16G16B16A16SFloat,
            ImageExtent = new VkExtent3D(targetW, targetH, 1),
            ImageMipLevels = 1,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });
        _hasScratch[idx] = true;

        var needed = (long)targetW * targetH * 8L; // R16G16B16A16_SFLOAT = 8 bytes/pixel
        if (_hasStaging[idx])
            _staging[idx].Dispose();
        _staging[idx] = allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "gatOS Display Staging",
            BufferSize = ByteSize.Of(needed),
            BufferUsage = VkBufferUsageFlags.TransferDstBit,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        });
        _hasStaging[idx] = true;

        _allocW[idx] = targetW;
        _allocH[idx] = targetH;
        Trace($"slot {idx} resized to {targetW}x{targetH} ({needed} B staging)");
    }

    /// <summary>
    ///     Records the capture into the engine's command buffer: barrier the offscreen scene to a
    ///     transfer source, downscale-blit it into the slot's scratch image, copy that to the slot's
    ///     host buffer, and <b>restore</b> the offscreen to <c>ShaderReadOnlyOptimal</c> so the engine
    ///     finds it exactly as it left it (sync1 barriers, mirroring KSA's PlanetMapExporter).
    /// </summary>
    private void RecordCopy(CommandBuffer cb, int idx, VkImage srcImage, int srcW, int srcH, int targetW, int targetH)
    {
        var dstImage = _scratch[idx].VkImage;
        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        // offscreen scene: ShaderReadOnly -> TransferSrc (it was last sampled by the composite's frag shader).
        Barrier(cb, srcImage, range, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferReadBit,
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit);
        // scratch: Undefined -> TransferDst.
        Barrier(cb, dstImage, range, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
            VkAccessFlags.None, VkAccessFlags.TransferWriteBit,
            VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit);

        // GPU downscale: full-res scene -> small scratch (linear filter, same format).
        var blit = new VkImageBlit
        {
            SrcSubresource = new VkImageSubresourceLayers
                { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            DstSubresource = new VkImageSubresourceLayers
                { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
        };
        blit.SrcOffsets[0] = new VkOffset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets[1] = new VkOffset3D { X = srcW, Y = srcH, Z = 1 };
        blit.DstOffsets[0] = new VkOffset3D { X = 0, Y = 0, Z = 0 };
        blit.DstOffsets[1] = new VkOffset3D { X = targetW, Y = targetH, Z = 1 };
        cb.BlitImage(srcImage, VkImageLayout.TransferSrcOptimal, dstImage, VkImageLayout.TransferDstOptimal,
            new[] { blit }, VkFilter.Linear);

        // scratch: TransferDst -> TransferSrc, then copy to the host buffer.
        Barrier(cb, dstImage, range, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.TransferWriteBit, VkAccessFlags.TransferReadBit,
            VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit);
        var region = new VkBufferImageCopy
        {
            BufferOffset = ByteSize.Zero,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers
                { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new VkOffset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new VkExtent3D { Width = targetW, Height = targetH, Depth = 1 },
        };
        cb.CopyImageToBuffer(dstImage, VkImageLayout.TransferSrcOptimal, _staging[idx].VkBuffer, new[] { region });

        // offscreen scene: TransferSrc -> ShaderReadOnly (restore the engine's expected end-of-frame layout).
        Barrier(cb, srcImage, range, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.TransferReadBit, VkAccessFlags.ShaderReadBit,
            VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit);
    }

    /// <summary>
    ///     Maps the slot's host buffer (its copy is complete by the frames-in-flight contract),
    ///     converts the half-float HDR pixels to BGRA8 and submits them to the surface.
    /// </summary>
    private void Readback(int idx, DisplaySurface surface)
    {
        var w = _allocW[idx];
        var h = _allocH[idx];
        var pixels = w * h;
        if (_convert.Length != pixels * 4)
            _convert = new byte[pixels * 4];

        var mapped = _staging[idx].Map();
        try
        {
            var halves = MemoryMarshal.Cast<byte, Half>(mapped.AsSpan<byte>());
            for (var px = 0; px < pixels; px++)
            {
                var h4 = px * 4;
                var o = px * 4;
                _convert[o] = ToByte((float)halves[h4 + 2]);     // B
                _convert[o + 1] = ToByte((float)halves[h4 + 1]); // G
                _convert[o + 2] = ToByte((float)halves[h4]);     // R
                _convert[o + 3] = 255;                           // A
            }

            surface.SubmitFrame(w, h, _convert);
        }
        finally
        {
            mapped.Unmap();
        }

        if (!_loggedReadback)
        {
            _loggedReadback = true;
            Trace($"first readback delivered (slot {idx}, {w}x{h})");
        }
    }

    /// <summary>Clamps an HDR channel to [0,1] and quantizes to a byte.</summary>
    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    private static void Barrier(CommandBuffer cb, VkImage image, VkImageSubresourceRange range,
        VkImageLayout oldLayout, VkImageLayout newLayout, VkAccessFlags srcAccess, VkAccessFlags dstAccess,
        VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage)
    {
        var barrier = new VkImageMemoryBarrier
        {
            OldLayout = oldLayout,
            NewLayout = newLayout,
            Image = image,
            SubresourceRange = range,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };
        cb.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None,
            default(ReadOnlySpan<VkMemoryBarrier>), default(ReadOnlySpan<VkBufferMemoryBarrier>),
            new VkImageMemoryBarrier[1] { barrier });
    }

    /// <summary>
    ///     Appends one flushed line to <c>&lt;LogsDir&gt;/display-capture.log</c>. Crash-resilient
    ///     (AutoFlush survives a process kill) and never throws — a logging failure must not break or
    ///     mask capture. Called only at state transitions (first record/readback, resize, errors), so
    ///     it never spams per frame.
    /// </summary>
    private void Trace(string message)
    {
        if (_traceFailed)
            return;
        try
        {
            if (_trace is null)
            {
                Directory.CreateDirectory(GatOsPaths.LogsDir);
                var path = Path.Combine(GatOsPaths.LogsDir, "display-capture.log");
                _trace = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _trace.WriteLine($"=== display capture session {DateTime.UtcNow:O} (in-band) ===");
            }

            _trace.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} tid={Environment.CurrentManagedThreadId} {message}");
        }
        catch
        {
            _traceFailed = true; // give up on tracing rather than spam; capture itself is unaffected
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            for (var i = 0; i < _slots; i++)
            {
                if (_hasScratch[i])
                    _scratch[i].Dispose();
                if (_hasStaging[i])
                    _staging[i].Dispose();
            }

            _trace?.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS display capture dispose error: {ex.Message}");
        }
    }
}
