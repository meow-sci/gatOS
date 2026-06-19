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
///     The screen-stream frame capture (STREAM_PLAN.md §4.1). Each enabled, throttled frame it
///     <b>records</b> a copy of the main viewport's offscreen scene image into a host-visible buffer,
///     in the engine's <i>own</i> per-frame command buffer (injected right before
///     <c>commandBuffer.End()</c> by <see cref="DisplayRenderPatch"/>), then reads it back a few frames
///     later, downscales it to the stream size and hands the BGRA bytes to the game-free
///     <see cref="DisplaySurface"/>.
/// </summary>
/// <remarks>
///     <para><b>Why in-band.</b> The capture commands ride the frame's command buffer and are
///     submitted by the engine on the render thread — there is no separate queue submit and no
///     <c>Device.WaitIdle</c>. Doing GPU work out-of-band (a private command buffer submitted alongside
///     the engine's in-flight frames) corrupts the device and crashes the game; the engine authors
///     prescribed the in-band path.</para>
///     <para><b>Full-frame copy (engine-author pattern).</b> Exactly as developer one described: a
///     single image barrier to <c>TransferSrc</c>, one <c>CopyImageToBuffer</c> of the whole offscreen
///     image, and a barrier back to the previous layout — no blit, no scratch image touching the live
///     offscreen. The downscale to the stream size happens on the CPU at readback.</para>
///     <para><b>Deferred readback (no stall).</b> The copy targets a host-visible staging buffer in a
///     ring slot indexed by <c>ResourceFrameIndex</c> (0..<c>MaxFramesInFlight</c>-1). The next time a
///     slot is revisited the engine has already waited on that slot's fence (frames-in-flight reuse), so
///     the prior copy is complete — no fence wait of our own. Each buffer is mapped once and kept mapped
///     (the engine's pattern; re-mapping per frame can unmap a shared VMA block).</para>
///     <para><b>Threading.</b> Everything runs on the render thread inside <c>RenderGame</c>'s recording
///     (threading rule 1). The HDR half-float scene is clamped to [0,1] on the CPU (bright areas clip —
///     the pre-tonemap caveat).</para>
///     <para>Engine types (the renderer + allocator) are reached by inference + interface constraints,
///     never named — their assemblies are only transitively visible to gatOS.</para>
/// </remarks>
internal sealed class FrameCapture : IDisposable
{
    // Per-slot ring resources (BufferEx is a value type; validity tracked with the bool flag). The
    // staging buffer holds the FULL offscreen image (sized to _srcW x _srcH x 8 bytes); the downscale
    // to the stream size (_dstW x _dstH) is done on the CPU at readback.
    private BufferEx[] _staging = [];
    private bool[] _hasStaging = [];
    // Each staging buffer is mapped ONCE at creation and kept mapped for its lifetime (the engine's own
    // pattern for host-visible buffers, e.g. PlanetMapExporter._phase0UboMap). Valid when _hasStaging[idx].
    private MappedMemory[] _mapped = [];
    private int[] _srcW = []; // full offscreen size the slot's staging buffer is sized to / holds
    private int[] _srcH = [];
    private int[] _dstW = []; // downscale target recorded for this slot's pending copy
    private int[] _dstH = [];
    private bool[] _pending = []; // a copy was recorded into this slot and not yet read back
    private int _slots;           // == MaxFramesInFlight once initialized (0 before)

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
    ///     has elapsed, records this frame's full-offscreen copy into <paramref name="cb"/> (the engine's
    ///     frame command buffer, with the offscreen color image in <c>ShaderReadOnlyOptimal</c> and
    ///     outside any render pass) and reads back the slot's previous frame.
    /// </summary>
    [KsaAnchor("Program.GetRenderer(), Program.MainViewport.OffscreenTarget.ColorImage/.Extent, "
               + "Renderer.Allocator/.MaxFramesInFlight, Program.ResourceFrameIndex; Allocator.CreateBuffer, "
               + "CommandBufferEx.TransitionImages2 + ImageBarrierInfo.Presets + ImageTransition, "
               + "CommandBuffer.CopyImageToBuffer, BufferEx.Map",
        SourceFile = "KSA/Program.cs", Verified = "2026-06-19", Risk = ChurnRisk.Medium,
        Notes = "In-band full-frame capture (developer-one pattern): barrier offscreen->TransferSrc, "
                + "CopyImageToBuffer(whole offscreen->host), barrier back to SampledReadVfc. The offscreen is "
                + "moved with the engine's OWN sync2 TransitionImages2 + ImageBarrierInfo.Presets (no sync1/sync2 "
                + "mixing). Downscale to the stream size is on the CPU. Deferred readback via frames-in-flight reuse.")]
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

        // Clamp the downscale target to the source so we only ever shrink.
        var dstW = Math.Clamp(settings.Width, 1, srcExtent.Width);
        var dstH = Math.Clamp(settings.Height, 1, srcExtent.Height);

        using (surface.CaptureStat.Measure()) // render-thread cost: record + the deferred copy/convert
            RecordInto(renderer.Allocator, cb, program.ResourceFrameIndex, renderer.MaxFramesInFlight,
                offscreen.ColorImage.Image, srcExtent.Width, srcExtent.Height, dstW, dstH, surface);
        _lastCaptureTs = now;
    }

    // Generic on the allocator: the concrete KSA allocator type lives in an assembly gatOS does not
    // reference directly (only its Brutal.VulkanApi.Abstractions interfaces are nameable here), so the
    // type is reached by inference + this interface constraint rather than by name.
    private void RecordInto<TAllocator>(TAllocator allocator, CommandBuffer cb, int idx, int framesInFlight,
        VkImage srcImage, int srcW, int srcH, int dstW, int dstH, DisplaySurface surface)
        where TAllocator : IBufferAllocator
    {
        EnsureArrays(framesInFlight);
        if (idx < 0 || idx >= _slots)
            return; // defensive: ResourceFrameIndex out of the expected range

        // 1) Read back this slot's previous copy (complete by the frames-in-flight contract — the engine
        //    waited this slot's fence when it reacquired the slot for the current frame). Before EnsureSlot
        //    so a resize can free the buffer safely.
        if (_pending[idx])
        {
            Readback(idx, surface);
            _pending[idx] = false;
        }

        // 2) (Re)allocate the slot's host buffer for the full offscreen size if it changed.
        EnsureSlot(allocator, idx, srcW, srcH);
        _dstW[idx] = dstW;
        _dstH[idx] = dstH;

        // 3) Record the full-offscreen copy-to-host into the engine's command buffer.
        RecordCopy(cb, idx, srcImage, srcW, srcH);
        _pending[idx] = true;
        _captureCount++;

        if (!_loggedRecord)
        {
            _loggedRecord = true;
            Trace($"first capture recorded (slot {idx}/{_slots}, full {srcW}x{srcH} -> stream {dstW}x{dstH})");
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
        _srcW = new int[_slots];
        _srcH = new int[_slots];
        _dstW = new int[_slots];
        _dstH = new int[_slots];
        _pending = new bool[_slots];
    }

    private void EnsureSlot<TAllocator>(TAllocator allocator, int idx, int srcW, int srcH)
        where TAllocator : IBufferAllocator
    {
        if (_hasStaging[idx] && _srcW[idx] == srcW && _srcH[idx] == srcH)
            return; // already sized for this offscreen resolution

        if (_hasStaging[idx])
        {
            _mapped[idx].Unmap();
            _staging[idx].Dispose();
        }

        var needed = (long)srcW * srcH * 8L; // R16G16B16A16_SFLOAT = 8 bytes/pixel
        _staging[idx] = allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "gatOS Display Staging",
            BufferSize = ByteSize.Of(needed),
            BufferUsage = VkBufferUsageFlags.TransferDstBit,
            AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
        });
        // Map once and keep it (host-coherent, so GPU writes are visible without invalidate).
        _mapped[idx] = _staging[idx].Map();
        _hasStaging[idx] = true;
        _srcW[idx] = srcW;
        _srcH[idx] = srcH;
        Trace($"slot {idx} staging sized to {srcW}x{srcH} ({needed} B)");
    }

    /// <summary>
    ///     Records the capture into the engine's command buffer (developer-one pattern): transition the
    ///     offscreen to a transfer source, copy the <b>whole</b> image into the slot's host buffer, and
    ///     restore the offscreen to <c>SampledReadVfc</c> as the engine left it. The offscreen is moved
    ///     with the engine's <b>own</b> sync2 <see cref="CommandBufferEx.TransitionImages2"/> +
    ///     <c>ImageBarrierInfo.Presets</c> — no sync1/sync2 mixing on the shared image.
    /// </summary>
    private void RecordCopy(CommandBuffer cb, int idx, VkImage srcImage, int srcW, int srcH)
    {
        // offscreen: SampledReadVfc (ShaderReadOnly) -> TransferSrc.
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

        // offscreen: TransferSrc -> SampledReadVfc (restore the engine's expected end-of-frame layout).
        cb.TransitionImages2(new[]
        {
            new ImageTransition(srcImage, ImageBarrierInfo.Presets.TransferSrc, ImageBarrierInfo.Presets.SampledReadVfc),
        });
    }

    /// <summary>
    ///     Reads the slot's persistent host mapping (its copy is complete by the frames-in-flight
    ///     contract), nearest-neighbour downscales the full-resolution half-float HDR image to the
    ///     stream size, converts to BGRA8 and submits it to the surface.
    /// </summary>
    private void Readback(int idx, DisplaySurface surface)
    {
        var srcW = _srcW[idx];
        var srcH = _srcH[idx];
        var dstW = _dstW[idx];
        var dstH = _dstH[idx];
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

        if (!_loggedReadback)
        {
            _loggedReadback = true;
            Trace($"first readback delivered (slot {idx}, {srcW}x{srcH} -> {dstW}x{dstH})");
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
                _trace.WriteLine($"=== display capture session {DateTime.UtcNow:O} (in-band, full-frame) ===");
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
                if (_hasStaging[i])
                {
                    _mapped[i].Unmap();
                    _staging[i].Dispose();
                }
            }

            _trace?.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS display capture dispose error: {ex.Message}");
        }
    }
}
