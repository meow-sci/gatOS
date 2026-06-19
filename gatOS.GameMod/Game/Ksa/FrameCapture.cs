using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using gatOS.Logging;
using gatOS.SimFs.Display;
using gatOS.Vm;
using KSA;

namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     The render-thread frame capture for the screen stream (STREAM_PLAN.md §4.1): each enabled,
///     throttled tick it GPU-downscales the main viewport's offscreen scene image and reads the small
///     result back into a host buffer, handing the BGRA bytes to the game-free
///     <see cref="DisplaySurface"/> (which encodes + serves them over <c>/sim/display/stream</c>).
/// </summary>
/// <remarks>
///     <para><b>Crash trace + probe ladder.</b> The GPU work runs in native code, so a fault there is
///     a process crash, not a managed exception. Every phase is written (flushed) to
///     <c>&lt;LogsDir&gt;/display-capture.log</c> so the last line before a crash localizes the
///     faulting step. The <c>display_probe</c> config knob isolates the cause:
///     <list type="bullet">
///         <item><b>0</b> — the real capture (blit + readback of the live offscreen).</item>
///         <item><b>1</b> — <c>WaitIdle</c> + an <i>empty</i> command-buffer submit (nothing touches the
///             offscreen). If this still crashes, the bare out-of-band submit is the cause.</item>
///         <item><b>2</b> — a synthetic moving gradient, no GPU at all. Proves the encode → stream →
///             terminal path renders without any capture.</item>
///     </list></para>
///     <para>Engine types (the renderer + allocator) are reached by inference + interface constraints,
///     never named — their assemblies are only transitively visible to gatOS.</para>
/// </remarks>
internal sealed class FrameCapture : IDisposable
{
    // ImageEx/BufferEx are value types, so validity is tracked with a flag rather than nullability.
    private ImageEx _scratch;
    private bool _hasScratch;
    private BufferEx _staging;
    private bool _hasStaging;
    private int _scratchW;
    private int _scratchH;
    private long _stagingBytes;
    private double _sinceLastCapture;
    private bool _disposed;

    private readonly int _probe;
    private byte[] _synthetic = [];
    private long _frame;
    private TextWriter? _trace;
    private bool _traceFailed;

    /// <param name="probe">Diagnostic mode (0 = real capture; 1 = bare submit; 2 = synthetic only).</param>
    public FrameCapture(int probe = 0) => _probe = probe;

    /// <summary>
    ///     Captures one frame if the stream is enabled, has a reader, and the per-frame throttle has
    ///     elapsed. Never throws — a fault is the caller's signal to disable capture for the session.
    /// </summary>
    /// <param name="dt">Seconds since the last frame (drives the cadence throttle).</param>
    /// <param name="surface">The destination feed (its <see cref="DisplaySettings"/> gate this).</param>
    /// <returns><c>true</c> if a frame was captured and submitted this call.</returns>
    public bool TryCapture(double dt, DisplaySurface surface)
    {
        var settings = surface.Settings;
        if (!settings.Enabled || !surface.HasReaders)
        {
            _sinceLastCapture = 0;
            return false;
        }

        _sinceLastCapture += dt;
        if (_sinceLastCapture < 1.0 / settings.Fps)
            return false;
        _sinceLastCapture = 0;

        using (surface.CaptureStat.Measure())
            Capture(settings.Width, settings.Height, surface);
        return true;
    }

    [KsaAnchor("Program.GetRenderer() + Program.MainViewport.OffscreenTarget.ColorImage; "
               + "Allocator.CreateImage/CreateBuffer/CreateStagingPool; CommandBuffer.BlitImage + CopyImageToBuffer",
        SourceFile = "KSA/PlanetMapExporter.cs", Verified = "2026-06-18", Risk = ChurnRisk.Medium,
        Notes = "Readback mirrors PlanetMapExporter; offscreen FinalLayout=ShaderReadOnlyOptimal (RenderTarget.cs:75).")]
    private void Capture(int targetW, int targetH, DisplaySurface surface)
    {
        var frame = ++_frame;
        Trace($"frame {frame}: begin (target {targetW}x{targetH}, probe={_probe})");
        try
        {
            // PROBE 2: no GPU at all — prove the encode → stream → terminal path renders.
            if (_probe == 2)
            {
                SubmitSynthetic(targetW, targetH, surface, frame);
                Trace($"frame {frame}: probe2 (synthetic) done");
                return;
            }

            var renderer = Program.GetRenderer();
            if (renderer is null)
            {
                Trace($"frame {frame}: renderer is null — skip");
                return;
            }

            var allocator = renderer.Allocator;

            Trace($"frame {frame}: device wait idle (drain engine work)");
            renderer.Device.WaitIdle();
            Trace($"frame {frame}: device idle");

            // PROBE 1: WaitIdle + an empty command-buffer submit. Nothing touches the offscreen, so
            // if this still crashes the engine, the bare out-of-band submit is the cause.
            if (_probe == 1)
            {
                Trace($"frame {frame}: PROBE 1 — empty command buffer");
                using (var pool = allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1))
                {
                    var cb = pool.NextCommandBuffer();
                    cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
                    cb.End();
                    Trace($"frame {frame}: empty recorded; SUBMIT+WAIT (GPU)");
                }

                Trace($"frame {frame}: empty submit returned");
                SubmitSynthetic(targetW, targetH, surface, frame);
                Trace($"frame {frame}: probe1 done");
                return;
            }

            // PROBE 0: the real capture.
            if (Program.MainViewport?.OffscreenTarget is not { } offscreen)
            {
                Trace($"frame {frame}: no offscreen target — skip");
                return;
            }

            var srcImage = offscreen.ColorImage.Image;
            var srcExtent = offscreen.Extent;
            if (srcExtent.Width <= 0 || srcExtent.Height <= 0)
            {
                Trace($"frame {frame}: offscreen extent {srcExtent.Width}x{srcExtent.Height} — skip");
                return;
            }

            Trace($"frame {frame}: src {srcExtent.Width}x{srcExtent.Height} fmt={offscreen.ColorImage.Format}");
            EnsureResources(allocator, targetW, targetH);
            var dstImage = _scratch.VkImage;
            Trace($"frame {frame}: resources ready (scratch {_scratchW}x{_scratchH}, staging {_stagingBytes} B)");

            var range = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            Trace($"frame {frame}: creating staging pool");
            using (var pool = allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1))
            {
                var cb = pool.NextCommandBuffer();
                cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
                Trace($"frame {frame}: recording");

                // src (scene): ShaderReadOnly -> TransferSrc.
                Barrier(cb, srcImage, range, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal,
                    VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferReadBit,
                    VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit);
                // scratch: Undefined -> TransferDst.
                Barrier(cb, dstImage, range, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                    VkAccessFlags.None, VkAccessFlags.TransferWriteBit,
                    VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit);

                // GPU downscale: full-res scene -> small image (linear).
                var blit = new VkImageBlit
                {
                    SrcSubresource = new VkImageSubresourceLayers
                        { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
                    DstSubresource = new VkImageSubresourceLayers
                        { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
                };
                blit.SrcOffsets[0] = new VkOffset3D { X = 0, Y = 0, Z = 0 };
                blit.SrcOffsets[1] = new VkOffset3D { X = srcExtent.Width, Y = srcExtent.Height, Z = 1 };
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
                cb.CopyImageToBuffer(dstImage, VkImageLayout.TransferSrcOptimal, _staging.VkBuffer, new[] { region });

                // src (scene): restore TransferSrc -> ShaderReadOnly so the engine finds it as it left it.
                Barrier(cb, srcImage, range, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                    VkAccessFlags.TransferReadBit, VkAccessFlags.ShaderReadBit,
                    VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit);

                cb.End();
                Trace($"frame {frame}: recorded; SUBMIT+WAIT (GPU) — last line before a GPU hang means the submit faulted");
            } // Dispose submits to GraphicsAndCompute and waits on the fence (synchronous).

            Trace($"frame {frame}: submit+wait returned");

            var bytes = (int)(targetW * targetH * 4L);
            var mapped = _staging.Map();
            Trace($"frame {frame}: mapped");
            try
            {
                surface.SubmitFrame(targetW, targetH, mapped.AsSpan<byte>()[..bytes]);
            }
            finally
            {
                mapped.Unmap();
            }

            Trace($"frame {frame}: done");
        }
        catch (Exception ex)
        {
            // A managed exception (vs a native crash). Record it to both sinks and rethrow so the
            // caller disables capture for the session.
            Trace($"frame {frame}: EXCEPTION {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            ModLog.Log.Error($"gatOS display capture threw on frame {frame}", ex);
            throw;
        }
    }

    /// <summary>Feeds the surface a synthetic moving gradient (BGRA) — no GPU, for the probe ladder.</summary>
    private void SubmitSynthetic(int w, int h, DisplaySurface surface, long frame)
    {
        var need = w * h * 4;
        if (_synthetic.Length != need)
            _synthetic = new byte[need];
        var phase = (int)(frame * 6); // scrolls the pattern frame to frame (obvious motion)
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            _synthetic[i] = (byte)(x * 255 / w + phase);     // B (scrolls)
            _synthetic[i + 1] = (byte)(y * 255 / h + phase); // G (scrolls)
            _synthetic[i + 2] = (byte)(x + y + phase);       // R (moving diagonal bands)
            _synthetic[i + 3] = 255;                         // A
        }

        surface.SubmitFrame(w, h, _synthetic);
    }

    // Generic on the allocator: the concrete KSA allocator type lives in an assembly gatOS does not
    // reference directly (only its Brutal.VulkanApi.Abstractions interfaces are nameable here), so the
    // type is reached by inference + these interface constraints rather than by name.
    private void EnsureResources<TAllocator>(TAllocator allocator, int targetW, int targetH)
        where TAllocator : IImageAllocator, IBufferAllocator
    {
        if (!_hasScratch || _scratchW != targetW || _scratchH != targetH)
        {
            if (_hasScratch)
                _scratch.Dispose();
            _scratch = allocator.CreateImage(new ImageEx.CreateInfo
            {
                Name = "gatOS Display Capture",
                ImageType = VkImageType._2D,
                ImageFormat = VkFormat.B8G8R8A8UNorm, // blit converts the HDR scene format down to BGRA8
                ImageExtent = new VkExtent3D(targetW, targetH, 1),
                ImageMipLevels = 1,
                ImageArrayLayers = 1,
                ImageSamples = VkSampleCountFlags._1Bit,
                ImageTiling = VkImageTiling.Optimal,
                ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
                ImageInitialLayout = VkImageLayout.Undefined,
                AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
            });
            _hasScratch = true;
            _scratchW = targetW;
            _scratchH = targetH;
        }

        var needed = targetW * targetH * 4L;
        if (!_hasStaging || _stagingBytes != needed)
        {
            if (_hasStaging)
                _staging.Dispose();
            _staging = allocator.CreateBuffer(new BufferEx.CreateInfo
            {
                Name = "gatOS Display Staging",
                BufferSize = ByteSize.Of(needed),
                BufferUsage = VkBufferUsageFlags.TransferDstBit,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            });
            _hasStaging = true;
            _stagingBytes = needed;
        }
    }

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
    ///     (AutoFlush to the OS file buffer survives a process kill), thread-tagged (to spot a
    ///     cross-thread submit), and never throws — a logging failure must not break or mask capture.
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
                _trace.WriteLine($"=== display capture session {DateTime.UtcNow:O} (probe={_probe}) ===");
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
            if (_hasScratch)
                _scratch.Dispose();
            if (_hasStaging)
                _staging.Dispose();
            _trace?.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS display capture dispose error: {ex.Message}");
        }
    }
}
