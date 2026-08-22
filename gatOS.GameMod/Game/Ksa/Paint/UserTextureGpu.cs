using Brutal.TextureApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using gatOS.Logging;
using gatOS.SimFs.Paint;
using KSA;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>
///     The shared GPU half of user-uploaded images: decode uploaded bytes into a gatOS-owned
///     <c>SimpleVkTexture</c>, and destroy those images only once no frame in flight can still
///     sample them. Every consumer of <see cref="TextureStore"/> goes through here, so there is
///     exactly one implementation of the decode/upload idiom and one deferred-destroy policy.
/// </summary>
/// <remarks>
///     <para><b>Consumers.</b> <see cref="ClutterTextureBridge"/> uploads faithful-corrected copies
///     and re-points stock bindless slots at them; stickers (STICKERS_PLAN) upload raw copies into
///     newly allocated slots. The same file may be held by both at once — each owns an independent
///     image by design, because the pixel corrections differ.</para>
///     <para><b>Threading.</b> Game thread only (the Frame command drain). Nothing here is safe to
///     call from a transport thread or from inside a render hook.</para>
/// </remarks>
internal static class UserTextureGpu
{
    /// <summary>
    ///     Decodes one uploaded image and uploads it as a gatOS-owned GPU image. The caller owns the
    ///     result and must eventually hand it to a <see cref="RetireQueue"/>.
    /// </summary>
    /// <param name="renderer">The live renderer (the allocator and graphics queue come from it).</param>
    /// <param name="file">The committed upload — its bytes are never mutated.</param>
    /// <param name="maxDimension">Longest edge kept; larger sources are downscaled, not rejected.</param>
    /// <param name="faithful">
    ///     Whether to apply the clutter-shader correction (<see cref="MakeFaithful"/>). Only the
    ///     clutter bridge wants it: the correction cancels <c>Solid.frag</c>'s ×2/tint semantics and
    ///     is meaningless — actively wrong — for a shader that decodes sRGB itself.
    /// </param>
    /// <param name="debugNamePrefix">
    ///     Prefix for the Vulkan image name (e.g. <c>"gatos:paint/textures/"</c>); the file name is
    ///     appended. Must make the result non-empty — the <c>SimpleVkTexture</c> ctor throws otherwise.
    /// </param>
    /// <exception cref="InvalidOperationException">The container is not a recognised image.</exception>
    [KsaAnchor("TextureLoader.LoadFromMemory; TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32); "
            + "new SimpleVkTexture(Allocator, StagingPool, TextureAsset, CreateOptions); "
            + "Renderer.Allocator.CreateStagingPool(Renderer.Graphics, 1)",
        SourceFile = "Brutal.TextureApi/TextureLoader.cs:130 / RenderCore/TextureAsset.cs:35 / "
            + "RenderCore/SimpleVkTexture.cs:245 / Core/Renderer.cs",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "All public; no reflection. Three non-obvious contracts: TextureAsset.FilePath must be "
            + "non-empty or the SimpleVkTexture ctor throws ArgumentException; LoadOptions' stb format "
            + "forces 4 channels (a 3-channel PNG would otherwise decode to the widely unsupported "
            + "R8G8B8_UNorm); and the decoded ITexture is neither IDisposable nor finalized, so its "
            + "public Destroy() must be called or the native buffer leaks. Mips are generated "
            + "automatically when the source has one level and FillMipChain is set.")]
    internal static SimpleVkTexture Upload(
        Core.Renderer renderer, TextureFile file, int maxDimension, bool faithful, string debugNamePrefix)
    {
        var kind = file.Kind switch
        {
            TextureImageKind.Png => TextureLoader.FormatType.Png,
            TextureImageKind.Jpeg => TextureLoader.FormatType.Jpg,
            TextureImageKind.Bmp => TextureLoader.FormatType.Bmp,
            TextureImageKind.Hdr => TextureLoader.FormatType.Hdr,
            TextureImageKind.Dds => TextureLoader.FormatType.Dds,
            TextureImageKind.Ktx => TextureLoader.FormatType.Ktx,
            TextureImageKind.Ktx2 => TextureLoader.FormatType.Ktx2,
            _ => throw new InvalidOperationException($"'{file.Name}' is not a recognised image"),
        };

        // R8G8B8A8UNorm forces stb to 4 channels; Rgba32 is the ktx transcode target. This is the
        // exact settings pair TextureReference.DoLoad uses for the game's own assets.
        var decoded = TextureLoader.LoadFromMemory(file.Bytes, kind,
            TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
        try
        {
            // Inside the guard: MakeFaithful throws by design for any format it cannot rewrite, and
            // the decoded native buffer has to be freed on that path too.
            if (faithful)
                MakeFaithful(decoded, file.Name);

            // FilePath must be non-empty (the ctor throws otherwise) and names the Vulkan image.
            var asset = new TextureAsset(decoded, $"{debugNamePrefix}{file.Name}");
            using var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
            var image = new SimpleVkTexture(renderer.Allocator, pool, asset,
                new SimpleVkTexture.CreateOptions(maxDimension,
                    SimpleVkTexture.CreateOptions.ReductionMethod.Downsample, fillMipChain: true));
            pool.Submit().Wait();
            return image;
        }
        finally
        {
            DestroyDecoded(decoded);
        }
    }

    /// <summary>The device memory one of our images actually costs (the <c>vram_bytes</c> lines).</summary>
    internal static long VramBytes(SimpleVkTexture image)
        => (long)image.ImageEx.AllocationInfo.MemAllocationInfo.AllocSize;

    /// <summary>
    ///     Rewrites the decoded pixels so the image renders as authored rather than as a modulation
    ///     map. Two independent corrections, both required (<c>Solid.frag:284-300</c>):
    ///     <list type="number">
    ///         <item>RGB is scaled by <c>2^(-1/2.2)</c> to cancel the shader's <c>×2</c>.</item>
    ///         <item>Alpha is cleared, which selects the shader's sRGB-decode path <i>and</i> collapses
    ///         the per-instance terrain tint to exactly 1 — without this an ordinary PNG is also
    ///         recoloured by whatever biome the clutter stands in.</item>
    ///     </list>
    ///     In place, over the decoder's own native buffer: no copy, no allocation. The channel
    ///     mapping itself lives game-free in <see cref="TextureStore.FaithfulScale"/>. Uniform alpha also keeps the generated mip chain honest — a mixed-alpha
    ///     image would have its mips average between the two decode conventions.
    /// </summary>
    private static void MakeFaithful(Brutal.TextureApi.ITexture decoded, string name)
    {
        if (decoded.Format != TextureFormat.R8G8B8A8_UNorm)
            throw new InvalidOperationException(
                $"'{name}' decoded as {decoded.Format}, which gatOS cannot correct; "
                + "bind it with mode 'raw' to upload it untouched");

        var pixels = decoded.Data;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = TextureStore.FaithfulScale(pixels[i]);
            pixels[i + 1] = TextureStore.FaithfulScale(pixels[i + 1]);
            pixels[i + 2] = TextureStore.FaithfulScale(pixels[i + 2]);
            pixels[i + 3] = 0;
        }
    }

    /// <summary>
    ///     Frees the decoded CPU-side image. <c>ITexture</c> is neither <c>IDisposable</c> nor
    ///     finalized — only the concrete loaders expose a public <c>Destroy()</c> — so without this
    ///     every upload leaks its native decode buffer for the life of the process.
    /// </summary>
    private static void DestroyDecoded(Brutal.TextureApi.ITexture texture)
    {
        try
        {
            switch (texture)
            {
                case Brutal.TextureApi.Stb.StbTexture stb: stb.Destroy(); break;
                case Brutal.TextureApi.Ktx.KtxTexture ktx: ktx.Destroy(); break;
                case Brutal.TextureApi.Gli.GliTexture gli: gli.Destroy(); break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS user texture decode cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     The deferred-destroy queue for gatOS-owned GPU images. Destroying an image the moment it
    ///     stops being referenced corrupts the device: the descriptor was only just rewritten, and
    ///     frames already recorded may still sample the old image. Each retired image therefore waits
    ///     out <c>MaxFramesInFlight + 1</c> ticks. This is the single real hazard in the feature.
    /// </summary>
    /// <remarks>Game thread only; each consumer owns its own instance.</remarks>
    internal sealed class RetireQueue
    {
        private readonly List<Retired> _retiring = [];

        private sealed record Retired(SimpleVkTexture Image, int TicksRemaining)
        {
            internal int TicksRemaining { get; set; } = TicksRemaining;
        }

        /// <summary>How many images are still waiting out their frames (the <c>retiring</c> status).</summary>
        internal int Count => _retiring.Count;

        /// <summary>
        ///     Queues one image for destruction. Never destroy inline: the slot referencing it was
        ///     only just rewritten, and frames already recorded may still sample it. Null is ignored,
        ///     so callers can pass "the image this replaced" without a branch.
        /// </summary>
        internal void Retire(SimpleVkTexture? image)
        {
            if (image is null)
                return;
            var frames = Program.GetRenderer()?.MaxFramesInFlight ?? 3;
            _retiring.Add(new Retired(image, frames + 1));
        }

        /// <summary>One tick: ages the queue and destroys whatever has outlived every frame in flight.</summary>
        internal void Drain()
        {
            if (_retiring.Count == 0)
                return;
            for (var i = _retiring.Count - 1; i >= 0; i--)
            {
                var retired = _retiring[i];
                if (--retired.TicksRemaining > 0)
                    continue;
                _retiring.RemoveAt(i);
                Dispose(retired.Image, "gatOS user texture disposal failed");
            }
        }

        /// <summary>
        ///     Destroys everything immediately, regardless of remaining ticks. Only legal once the
        ///     device is idle — the teardown path calls <c>WaitIdle</c> first.
        /// </summary>
        internal void DrainAll()
        {
            foreach (var retired in _retiring)
                Dispose(retired.Image, "gatOS user texture teardown disposal failed");
            _retiring.Clear();
        }

        private static void Dispose(SimpleVkTexture image, string failureMessage)
        {
            try
            {
                image.Dispose();
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"{failureMessage}: {ex.Message}");
            }
        }
    }
}
