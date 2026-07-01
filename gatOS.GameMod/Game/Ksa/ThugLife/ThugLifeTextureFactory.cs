using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.ThugLife;

/// <summary>
///     Builds the small thug-life sunglasses texture programmatically from
///     <see cref="ThugLifeTexturePattern"/> (ported from the sibling <c>unscience</c> mod).
/// </summary>
/// <remarks>
///     Produces an <c>R8G8B8A8UNorm</c> 2D texture so the stock <c>UnlitMeshFrag</c> shader's
///     <c>gammaToLinear()</c> decode is the only color transform applied to the texel — using an SRGB
///     format would double-decode (the GPU decodes on sample, then the shader decodes again → too dark).
///     See <c>.claude/skills/ksa/quad.md</c> "Texture format gotcha".
/// </remarks>
internal sealed class ThugLifeTextureFactory : IDisposable
{
    private readonly DeviceEx _device;
    private bool _disposed;

    public ThugLifeTextureFactory(Renderer renderer)
    {
        _device = renderer.Device;

        Texture = new SimpleVkTexture(
            "thug-life",
            renderer.Allocator,
            ThugLifeTexturePattern.Width,
            ThugLifeTexturePattern.Height,
            depth: 1,
            VkFormat.R8G8B8A8UNorm,
            mipLevels: 1,
            arrayLayers: 1,
            cubeMap: false,
            flags: VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit);

        UploadPixels(renderer);
        Sampler = CreateSampler(_device);
    }

    public SimpleVkTexture Texture { get; }
    public VkSampler Sampler { get; }
    public VkImageView ImageView => Texture.ImageView;

    [KsaAnchor("SimpleVkTexture(name, Renderer.Allocator, …, VkFormat.R8G8B8A8UNorm, …); "
            + "Renderer.Allocator.CreateStagingPool/AddStagingBuffer; VkUtils.UploadBufferToImage; "
            + "DeviceEx.CreateSampler (nearest, ClampToEdge)",
        SourceFile = "RenderCore/SimpleVkTexture.cs / RenderCore/VkUtils.cs / Brutal.VulkanApi.Abstractions",
        Verified = "2026-06-28", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.High,
        Notes = "GPU texture for the thug-life cheat (Planet.Render.Core + Brutal.Vulkan). Render-internals churn.")]
    private unsafe void UploadPixels(Renderer renderer)
    {
        var pixels = BuildPixelBytes();

        using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
        var cmd = stagingPool.NextCommandBuffer();
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        var stagingBuffer = stagingPool.AddStagingBuffer(ByteSize.Of<byte>(pixels.Length));
        using (var mapped = stagingBuffer.Map())
            pixels.AsSpan().CopyTo(mapped.AsSpan());

        Span<int> mipSizes = stackalloc int[1];
        mipSizes[0] = pixels.Length;
        VkBuffer src = stagingBuffer.VkBuffer;
        VkUtils.UploadBufferToImage(cmd, in src, Texture.ImageEx.AllocationInfo, mipSizes);

        cmd.End();
        stagingPool.Submit().Wait();
    }

    private static byte[] BuildPixelBytes()
    {
        var w = ThugLifeTexturePattern.Width;
        var h = ThugLifeTexturePattern.Height;
        var data = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            var row = ThugLifeTexturePattern.Rows[y];
            for (var x = 0; x < w; x++)
            {
                var offset = (y * w + x) * 4;
                switch (row[x])
                {
                    case '#': // black opaque
                        data[offset + 3] = 255;
                        break;
                    case 'W': // white opaque
                        data[offset + 0] = 255;
                        data[offset + 1] = 255;
                        data[offset + 2] = 255;
                        data[offset + 3] = 255;
                        break;
                    // default: left as 0,0,0,0 (transparent — no geometry is emitted there anyway)
                }
            }
        }

        return data;
    }

    private static unsafe VkSampler CreateSampler(DeviceEx device)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest,
            MinFilter = VkFilter.Nearest,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 0f,
            BorderColor = VkBorderColor.FloatTransparentBlack,
        };
        return device.CreateSampler(info, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _device.DestroySampler(Sampler, null);
        }
        catch
        {
            // best-effort teardown
        }

        try
        {
            Texture.Dispose();
        }
        catch
        {
            // best-effort teardown
        }
    }
}
