using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using gatOS.Logging;
using gatOS.SimFs.Display;
using gatOS.Vm;
using KSA;
using KSA.Rendering;

namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     The screen-stream frame capture (STREAM_PLAN.md §4.1; PERF_IMPROVEMENT_PLAN.md P1). Each
///     enabled, throttled frame it <b>records</b> a GPU downscale of the main viewport's offscreen
///     scene image into a host-visible buffer, in the engine's <i>own</i> per-frame command buffer
///     (injected right before <c>commandBuffer.End()</c> by <see cref="DisplayRenderPatch"/>), then
///     reads it back a few frames later and hands the BGRA bytes to the game-free
///     <see cref="DisplaySurface"/>.
/// </summary>
/// <remarks>
///     <para><b>Why in-band.</b> The capture commands ride the frame's command buffer and are
///     submitted by the engine on the render thread — there is no separate queue submit and no
///     <c>Device.WaitIdle</c>. Doing GPU work out-of-band (a private command buffer submitted alongside
///     the engine's in-flight frames) corrupts the device and crashes the game; the engine authors
///     prescribed the in-band path.</para>
///     <para><b>GPU downscale + convert (the P1 path).</b> A <c>vkCmdBlitImage</c> (linear filter)
///     resamples the full-resolution half-float scene into a small per-slot
///     <c>B8G8R8A8_UNORM</c> scratch image — one GPU op that does the downscale, the float→byte
///     conversion, and the [0,1] clamp (UNORM conversion clamps) — and only the <i>small</i> image is
///     copied to host memory (<c>dstW×dstH×4</c> bytes instead of <c>srcW×srcH×8</c>). Readback then
///     is a single bulk span hand-off with <b>zero per-pixel CPU work</b>. The previous full-frame
///     variant read the whole 8 B/px image back and converted per pixel on the render thread —
///     30–80 ms per captured frame, the single largest cause of the in-game fps collapse.</para>
///     <para><b>Fallback.</b> Blit support for the format pair is queried once
///     (<c>GetPhysicalDeviceFormatProperties</c>); on the (desktop-unheard-of) miss the original
///     full-frame copy + CPU nearest-neighbour convert path is used instead, so the stream still
///     works everywhere.</para>
///     <para><b>Deferred readback (no stall).</b> The copy targets a host-visible staging buffer in a
///     ring slot indexed by <c>ResourceFrameIndex</c> (0..<c>MaxFramesInFlight</c>-1). The next time a
///     slot is revisited the engine has already waited on that slot's fence (frames-in-flight reuse), so
///     the prior copy is complete — no fence wait of our own. Each buffer is mapped once and kept mapped
///     (the engine's pattern; re-mapping per frame can unmap a shared VMA block). Staging prefers
///     <c>HOST_CACHED</c> memory so the CPU-side read is a cached-memory read, not an uncached
///     PCIe/write-combined one.</para>
///     <para><b>Threading.</b> Everything runs on the render thread inside <c>RenderGame</c>'s recording
///     (threading rule 1). Engine types (the renderer + allocator) are reached by inference + interface
///     constraints, never named — their assemblies are only transitively visible to gatOS.</para>
/// </remarks>
internal sealed class FrameCapture : IDisposable
{
    /// <summary>The scratch/wire pixel format: BGRA8, matching the <see cref="DisplaySurface"/> seam.</summary>
    private const VkFormat StreamFormat = VkFormat.B8G8R8A8UNorm;

    private enum CaptureMode : byte
    {
        Undecided = 0,
        GpuBlit,    // blit-downscale into a small scratch image; readback is a bulk memcpy
        CpuFullRes, // legacy: full-res 8 B/px copy + per-pixel CPU downscale/convert (fallback)
    }

    // Per-slot ring resources (BufferEx/ImageEx are value types; validity tracked with the bool
    // flags). In GpuBlit mode the staging buffer holds the SMALL converted image (dstW×dstH×4) and
    // _scratch is its blit target; in CpuFullRes mode the staging holds the FULL offscreen image
    // (srcW×srcH×8) and the downscale happens on the CPU at readback.
    private BufferEx[] _staging = [];
    private bool[] _hasStaging = [];
    // Each staging buffer is mapped ONCE at creation and kept mapped for its lifetime (the engine's own
    // pattern for host-visible buffers, e.g. PlanetMapExporter._phase0UboMap). Valid when _hasStaging[idx].
    private MappedMemory[] _mapped = [];
    private ImageEx[] _scratch = [];
    private bool[] _hasScratch = [];
    private int[] _sizedW = []; // dims the slot's staging (+scratch) are sized for (dst in blit mode, src in cpu mode)
    private int[] _sizedH = [];
    private int[] _srcW = [];   // full offscreen dims recorded for the slot's pending copy (cpu-mode convert)
    private int[] _srcH = [];
    private int[] _dstW = [];   // downscale target recorded for the slot's pending copy
    private int[] _dstH = [];
    private bool[] _pending = []; // a copy was recorded into this slot and not yet read back
    private int _slots;           // == MaxFramesInFlight once initialized (0 before)

    private CaptureMode _mode; // decided on the first record via a one-time format-feature query
    private byte[] _convert = [];
    private long _lastCaptureTs; // Stopwatch ticks of the last recorded capture (FPS throttle)
    private bool _disposed;

    // Trace breadcrumbs (the GPU work runs in native code; a fault there is a process crash, not a
    // managed exception, so the last line written localizes the faulting step).
    private TextWriter? _trace;
    private bool _traceFailed;
    private bool _loggedRecord;
    private bool _loggedReadback;
    private long _captureCount;

    /// <summary>
    ///     Entry point from the render hook (<see cref="DisplayRenderPatch"/>): if the per-FPS throttle
    ///     has elapsed, records this frame's downscale-blit + copy-to-host into <paramref name="cb"/>
    ///     (the engine's frame command buffer, with the offscreen color image in
    ///     <c>ShaderReadOnlyOptimal</c> and outside any render pass) and reads back the slot's
    ///     previous frame.
    /// </summary>
    [KsaAnchor("Program.GetRenderer(), Program.MainViewport.OffscreenTarget.ColorImage/.Extent, "
               + "Renderer.Allocator/.MaxFramesInFlight/.PhysicalDevice, Program.ResourceFrameIndex; "
               + "Allocator.CreateBuffer/CreateImage, CommandBufferEx.TransitionImages2 + "
               + "ImageBarrierInfo.Presets + ImageTransition, CommandBuffer.BlitImage, "
               + "CommandBuffer.CopyImageToBuffer, BufferEx.Map, PhysicalDevice.GetFormatProperties",
        SourceFile = "KSA/Program.cs", Verified = "2026-07-02", Risk = ChurnRisk.Medium,
        Notes = "In-band GPU downscale capture (perf plan P1): barrier offscreen->TransferSrc + scratch "
                + "Undefined->TransferDst, BlitImage(offscreen->B8G8R8A8 scratch, LINEAR — downscale + "
                + "float->UNORM clamp in one op), CopyImageToBuffer(small scratch->host), restore "
                + "offscreen to SampledReadVfc. All layout moves use the engine's OWN sync2 "
                + "TransitionImages2 + ImageBarrierInfo.Presets (no sync1/sync2 mixing). Blit support is "
                + "format-feature-queried once; the miss falls back to the previous full-frame copy + "
                + "CPU convert. Deferred readback via frames-in-flight reuse (no fence wait).")]
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

        if (_mode == CaptureMode.Undecided)
        {
            _mode = DetectMode(renderer.PhysicalDevice, offscreen.ColorImage.Format);
            Trace($"capture mode: {_mode} (offscreen format {offscreen.ColorImage.Format})");
        }

        // Clamp the downscale target to the source so we only ever shrink.
        var dstW = Math.Clamp(settings.Width, 1, srcExtent.Width);
        var dstH = Math.Clamp(settings.Height, 1, srcExtent.Height);

        using (surface.CaptureStat.Measure()) // render-thread cost: record + the deferred copy hand-off
            RecordInto(renderer.Allocator, cb, program.ResourceFrameIndex, renderer.MaxFramesInFlight,
                offscreen.ColorImage.Image, srcExtent.Width, srcExtent.Height, dstW, dstH, surface);
        _lastCaptureTs = now;
    }

    /// <summary>
    ///     One-time capability probe: the blit path needs BLIT_SRC + linear filtering on the offscreen
    ///     format and BLIT_DST on <see cref="StreamFormat"/> (universal on desktop GPUs; the query
    ///     keeps a correctness net under exotic drivers).
    /// </summary>
    private static CaptureMode DetectMode(PhysicalDevice physicalDevice, VkFormat srcFormat)
    {
        const VkFormatFeatureFlags srcNeeds =
            VkFormatFeatureFlags.BlitSrcBit | VkFormatFeatureFlags.SampledImageFilterLinearBit;
        var src = physicalDevice.GetFormatProperties(srcFormat).OptimalTilingFeatures;
        var dst = physicalDevice.GetFormatProperties(StreamFormat).OptimalTilingFeatures;
        return (src & srcNeeds) == srcNeeds && (dst & VkFormatFeatureFlags.BlitDstBit) != 0
            ? CaptureMode.GpuBlit
            : CaptureMode.CpuFullRes;
    }

    // Generic on the allocator: the concrete KSA allocator type lives in an assembly gatOS does not
    // reference directly (only its Brutal.VulkanApi.Abstractions interfaces are nameable here), so the
    // type is reached by inference + these interface constraints rather than by name.
    private void RecordInto<TAllocator>(TAllocator allocator, CommandBuffer cb, int idx, int framesInFlight,
        VkImage srcImage, int srcW, int srcH, int dstW, int dstH, DisplaySurface surface)
        where TAllocator : IBufferAllocator, IImageAllocator
    {
        EnsureArrays(framesInFlight);
        if (idx < 0 || idx >= _slots)
            return; // defensive: ResourceFrameIndex out of the expected range

        // 1) Read back this slot's previous copy (complete by the frames-in-flight contract — the engine
        //    waited this slot's fence when it reacquired the slot for the current frame). Before the
        //    Ensure*Slot below so a resize can free the buffer safely.
        if (_pending[idx])
        {
            Readback(idx, surface);
            _pending[idx] = false;
        }

        // 2) (Re)size the slot's resources, then 3) record the capture into the engine's command buffer.
        if (_mode == CaptureMode.GpuBlit)
        {
            EnsureBlitSlot(allocator, idx, dstW, dstH);
            RecordBlit(cb, idx, srcImage, srcW, srcH, dstW, dstH);
        }
        else
        {
            EnsureCpuSlot(allocator, idx, srcW, srcH);
            RecordFullResCopy(cb, idx, srcImage, srcW, srcH);
        }

        _srcW[idx] = srcW;
        _srcH[idx] = srcH;
        _dstW[idx] = dstW;
        _dstH[idx] = dstH;
        _pending[idx] = true;
        _captureCount++;

        if (!_loggedRecord)
        {
            _loggedRecord = true;
            Trace($"first capture recorded (slot {idx}/{_slots}, {_mode}, full {srcW}x{srcH} -> stream {dstW}x{dstH})");
        }
        else if (_captureCount % 10 == 0)
        {
            Trace($"heartbeat: {_captureCount} captures recorded (slot {idx})");
        }
    }

    private void EnsureArrays(int framesInFlight)
    {
        if (_slots == framesInFlight && _slots > 0)
            return;
        // First init (frames-in-flight is fixed for the renderer's life, so this runs once).
        _slots = Math.Max(1, framesInFlight);
        _staging = new BufferEx[_slots];
        _hasStaging = new bool[_slots];
        _mapped = new MappedMemory[_slots];
        _scratch = new ImageEx[_slots];
        _hasScratch = new bool[_slots];
        _sizedW = new int[_slots];
        _sizedH = new int[_slots];
        _srcW = new int[_slots];
        _srcH = new int[_slots];
        _dstW = new int[_slots];
        _dstH = new int[_slots];
        _pending = new bool[_slots];
    }

    /// <summary>Blit mode: a small scratch image + a small host staging buffer, keyed on the dst dims.</summary>
    private void EnsureBlitSlot<TAllocator>(TAllocator allocator, int idx, int dstW, int dstH)
        where TAllocator : IBufferAllocator, IImageAllocator
    {
        if (_hasStaging[idx] && _sizedW[idx] == dstW && _sizedH[idx] == dstH)
            return; // already sized for this stream resolution

        FreeSlot(idx);

        _scratch[idx] = allocator.CreateImage(new ImageEx.CreateInfo
        {
            Name = "gatOS Display Scratch",
            ImageType = VkImageType._2D,
            ImageFormat = StreamFormat,
            ImageExtent = new VkExtent3D { Width = dstW, Height = dstH, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
        });
        _hasScratch[idx] = true;

        CreateStaging(allocator, idx, (long)dstW * dstH * 4L);
        _sizedW[idx] = dstW;
        _sizedH[idx] = dstH;
        Trace($"slot {idx} blit resources sized to {dstW}x{dstH} ({(long)dstW * dstH * 4L} B staging)");
    }

    /// <summary>CPU-fallback mode: a full-resolution 8 B/px host staging buffer, keyed on the src dims.</summary>
    private void EnsureCpuSlot<TAllocator>(TAllocator allocator, int idx, int srcW, int srcH)
        where TAllocator : IBufferAllocator
    {
        if (_hasStaging[idx] && _sizedW[idx] == srcW && _sizedH[idx] == srcH)
            return; // already sized for this offscreen resolution

        FreeSlot(idx);
        CreateStaging(allocator, idx, (long)srcW * srcH * 8L); // R16G16B16A16_SFLOAT = 8 bytes/pixel
        _sizedW[idx] = srcW;
        _sizedH[idx] = srcH;
        Trace($"slot {idx} staging sized to {srcW}x{srcH} ({(long)srcW * srcH * 8L} B)");
    }

    private void CreateStaging<TAllocator>(TAllocator allocator, int idx, long bytes)
        where TAllocator : IBufferAllocator
    {
        _staging[idx] = allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "gatOS Display Staging",
            BufferSize = ByteSize.Of(bytes),
            BufferUsage = VkBufferUsageFlags.TransferDstBit,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            // Prefer cached host memory: the CPU READS this buffer every captured frame, and on a
            // BAR/write-combined type those reads are uncached PCIe round-trips (catastrophic for
            // the fallback's scattered reads, still slow for the bulk copy).
            AllocPreferredProperties = VkMemoryPropertyFlags.HostCachedBit,
        });
        // Map once and keep it (host-coherent, so GPU writes are visible without invalidate).
        _mapped[idx] = _staging[idx].Map();
        _hasStaging[idx] = true;
    }

    private void FreeSlot(int idx)
    {
        if (_hasStaging[idx])
        {
            _mapped[idx].Unmap();
            _staging[idx].Dispose();
            _hasStaging[idx] = false;
        }

        if (_hasScratch[idx])
        {
            _scratch[idx].Dispose();
            _hasScratch[idx] = false;
        }
    }

    /// <summary>
    ///     Records the blit-downscale capture into the engine's command buffer: move the offscreen to a
    ///     transfer source and the slot's scratch to a transfer destination (its old contents are
    ///     discardable — the blit overwrites every texel), blit the <b>whole</b> offscreen into the
    ///     small scratch (linear filter; the float→UNORM conversion clamps to [0,1]), copy the scratch
    ///     into the slot's host buffer, and restore the offscreen to <c>SampledReadVfc</c> as the engine
    ///     left it. All moves use the engine's <b>own</b> sync2 <see cref="CommandBufferEx.TransitionImages2"/>
    ///     + <c>ImageBarrierInfo.Presets</c> — no sync1/sync2 mixing on the shared image.
    /// </summary>
    private void RecordBlit(CommandBuffer cb, int idx, VkImage srcImage, int srcW, int srcH, int dstW, int dstH)
    {
        cb.TransitionImages2(new[]
        {
            new ImageTransition(srcImage, ImageBarrierInfo.Presets.SampledReadVfc, ImageBarrierInfo.Presets.TransferSrc),
            new ImageTransition(_scratch[idx].VkImage, ImageBarrierInfo.Presets.Undefined, ImageBarrierInfo.Presets.TransferDst),
        });

        var layers = new VkImageSubresourceLayers
            { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 };
        var blit = new VkImageBlit { SrcSubresource = layers, DstSubresource = layers };
        blit.SrcOffsets[1] = new VkOffset3D { X = srcW, Y = srcH, Z = 1 };
        blit.DstOffsets[1] = new VkOffset3D { X = dstW, Y = dstH, Z = 1 };
        cb.BlitImage(srcImage, VkImageLayout.TransferSrcOptimal,
            _scratch[idx].VkImage, VkImageLayout.TransferDstOptimal, new[] { blit }, VkFilter.Linear);

        cb.TransitionImages2(new[]
        {
            new ImageTransition(_scratch[idx].VkImage, ImageBarrierInfo.Presets.TransferDst, ImageBarrierInfo.Presets.TransferSrc),
        });

        var region = new VkBufferImageCopy
        {
            BufferOffset = ByteSize.Zero,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = layers,
            ImageOffset = new VkOffset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new VkExtent3D { Width = dstW, Height = dstH, Depth = 1 },
        };
        cb.CopyImageToBuffer(_scratch[idx].VkImage, VkImageLayout.TransferSrcOptimal,
            _staging[idx].VkBuffer, new[] { region });

        // offscreen: TransferSrc -> SampledReadVfc (restore the engine's expected end-of-frame layout).
        // The scratch stays TransferSrc; its next use discards via the Undefined transition above.
        cb.TransitionImages2(new[]
        {
            new ImageTransition(srcImage, ImageBarrierInfo.Presets.TransferSrc, ImageBarrierInfo.Presets.SampledReadVfc),
        });
    }

    /// <summary>
    ///     CPU-fallback record (developer-one pattern): transition the offscreen to a transfer source,
    ///     copy the <b>whole</b> image into the slot's host buffer, and restore the offscreen.
    /// </summary>
    private void RecordFullResCopy(CommandBuffer cb, int idx, VkImage srcImage, int srcW, int srcH)
    {
        cb.TransitionImages2(new[]
        {
            new ImageTransition(srcImage, ImageBarrierInfo.Presets.SampledReadVfc, ImageBarrierInfo.Presets.TransferSrc),
        });

        var region = new VkBufferImageCopy
        {
            BufferOffset = ByteSize.Zero,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers
                { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new VkOffset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new VkExtent3D { Width = srcW, Height = srcH, Depth = 1 },
        };
        cb.CopyImageToBuffer(srcImage, VkImageLayout.TransferSrcOptimal, _staging[idx].VkBuffer, new[] { region });

        cb.TransitionImages2(new[]
        {
            new ImageTransition(srcImage, ImageBarrierInfo.Presets.TransferSrc, ImageBarrierInfo.Presets.SampledReadVfc),
        });
    }

    /// <summary>
    ///     Reads the slot's persistent host mapping (its copy is complete by the frames-in-flight
    ///     contract) and submits BGRA8 bytes to the surface. Blit mode: the staging already holds the
    ///     downscaled BGRA8 image — one bulk span hand-off, no per-pixel work. CPU mode: nearest-
    ///     neighbour downscale + half-float→BGRA8 convert (the pre-P1 path, kept as the fallback).
    /// </summary>
    private void Readback(int idx, DisplaySurface surface)
    {
        var dstW = _dstW[idx];
        var dstH = _dstH[idx];

        if (_mode == CaptureMode.GpuBlit)
        {
            var bytes = dstW * dstH * 4;
            surface.SubmitFrame(dstW, dstH, _mapped[idx].AsSpan<byte>()[..bytes]);
        }
        else
        {
            var srcW = _srcW[idx];
            var srcH = _srcH[idx];
            if (_convert.Length != dstW * dstH * 4)
                _convert = new byte[dstW * dstH * 4];

            // Host-coherent: the completed GPU copy is already visible through the persistent mapping.
            var halves = MemoryMarshal.Cast<byte, Half>(_mapped[idx].AsSpan<byte>());
            for (var y = 0; y < dstH; y++)
            {
                var sy = y * srcH / dstH;
                var srcRow = sy * srcW;
                var dstRow = y * dstW;
                for (var x = 0; x < dstW; x++)
                {
                    var s = (srcRow + x * srcW / dstW) * 4; // half index of the source RGBA pixel
                    var o = (dstRow + x) * 4;
                    _convert[o] = ToByte((float)halves[s + 2]);     // B
                    _convert[o + 1] = ToByte((float)halves[s + 1]); // G
                    _convert[o + 2] = ToByte((float)halves[s]);     // R
                    _convert[o + 3] = 255;                          // A
                }
            }

            surface.SubmitFrame(dstW, dstH, _convert);
        }

        if (!_loggedReadback)
        {
            _loggedReadback = true;
            Trace($"first readback delivered (slot {idx}, {_mode}, -> {dstW}x{dstH})");
        }
    }

    /// <summary>Clamps an HDR channel to [0,1] and quantizes to a byte.</summary>
    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    /// <summary>
    ///     Appends one flushed line to <c>&lt;LogsDir&gt;/display-capture.log</c>. Crash-resilient
    ///     (AutoFlush survives a process kill) and never throws — a logging failure must not break or
    ///     mask capture. Called only at state transitions (first record/readback, resize, heartbeat,
    ///     errors), so it never spams per frame.
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
                _trace.WriteLine($"=== display capture session {DateTime.UtcNow:O} (in-band, GPU downscale) ===");
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
                FreeSlot(i);

            _trace?.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS display capture dispose error: {ex.Message}");
        }
    }
}
