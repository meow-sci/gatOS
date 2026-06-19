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
///     <para><b>Why the offscreen target.</b> <c>Program.MainViewport.OffscreenTarget</c> is the
///     post-resolve scene color image (no UI), reachable through public API with no reflection. Its
///     render pass leaves it in <see cref="VkImageLayout.ShaderReadOnlyOptimal"/> (RenderTarget.cs:75)
///     and re-enters from <see cref="VkImageLayout.Undefined"/> each frame (line 74), so the transient
///     transition this capture does — restored before it returns — is invisible to the engine.</para>
///     <para><b>Why a GPU blit first.</b> Reading back the full-res image every frame would move tens
///     of MB/s over PCIe; <c>vkCmdBlitImage</c> (linear) resamples into a small reusable image first,
///     so only the downscaled bytes are read back. The blit also converts the HDR
///     <c>R16G16B16A16_SFLOAT</c> scene format to <c>B8G8R8A8_UNORM</c> (pre-tonemap — bright areas
///     clamp; an in-game validation item).</para>
///     <para><b>Threading.</b> Driven only from the game/render thread (gatOS rule 1); the submit is
///     synchronous (StagingPool waits on a fence), so it briefly stalls the GPU. Engine types (the
///     renderer + allocator) are reached by inference + interface constraints, never named — their
///     assemblies are only transitively visible to gatOS.</para>
///     <para><b>Crash trace.</b> The GPU work runs in native code, so a fault there is a process crash,
///     not a managed exception. Every phase is written (flushed) to
///     <c>&lt;LogsDir&gt;/display-capture.log</c> so the last line before a crash localizes the
///     faulting step. See <see cref="Trace"/>.</para>
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

    private long _frame;
    private TextWriter? _trace;
    private bool _traceFailed;

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
        Trace($"frame {frame}: begin (target {targetW}x{targetH})");
        try
        {
            var renderer = Program.GetRenderer();
            if (renderer is null)
            {
                Trace($"frame {frame}: renderer is null — skip");
                return;
            }

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

            var allocator = renderer.Allocator;
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
                _trace.WriteLine($"=== display capture session {DateTime.UtcNow:O} ===");
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
