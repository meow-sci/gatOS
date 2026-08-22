using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using gatOS.Logging;
using KSA;
using KSA.Rendering;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     The GPU half of stickers: one pipeline, one unit-cube mesh and a per-frame ring of scene-depth
///     descriptor sets, recording one projected-decal draw per live sticker into the main viewport's
///     colour image right after KSA resolves its attachments (STICKERS_PLAN §3.2/§3.3).
/// </summary>
/// <remarks>
///     <para><b>Why here and nowhere else.</b> Inside the opaque scope the depth attachment is being
///     written and is not sampleable, and there is no copy of the full opaque scene's depth until
///     that scope ends. After <c>RenderTarget.ResolveAttachments</c> the resolved single-sample
///     <c>DepthImage</c> and <c>ColorImage</c> are both current and free — which is exactly the
///     window KSA's own <c>GridPass</c> draws in, and this pass is a near-verbatim port of it.</para>
///     <para><b>Why a box and not a quad.</b> The fragment shader reconstructs the scene position
///     under each pixel from that depth and projects it into decal space, so the decal conforms to
///     whatever geometry is there — hull curvature, tessellated terrain, and ground clutter, which
///     has no CPU-addressable transform at all (§1.2) and therefore cannot be reached any other
///     way.</para>
///     <para><b>Threading.</b> Constructed and disposed on the game thread; <see cref="RecordPass"/>
///     runs on the main thread inside <c>Program.RenderGame</c>'s recording — the same thread, so the
///     published entry array needs no locking.</para>
/// </remarks>
internal sealed unsafe class StickerDecalRenderer : IDisposable
{
    /// <summary>Sentinel <c>texId</c> that makes the shader draw a magenta checker instead of sampling.</summary>
    private const uint DebugTextureId = uint.MaxValue;

    /// <summary>
    ///     Below this cosine between the receiving surface and the decal's outward axis the decal
    ///     fades out and then stops drawing: a projected decal stretches without bound at grazing
    ///     angles, and this is the standard cut-off that hides it.
    /// </summary>
    private const float NormalCutoff = 0.2f;

    /// <summary>Push-constant block size in bytes — 6 × <c>vec4</c> + 4 × 4 B, inside the 128 B Vulkan minimum.</summary>
    private const int PushConstantBytes = 112;

    /// <summary>Indices in the unit cube (12 triangles).</summary>
    private const int CubeIndexCount = 36;

    /// <summary>
    ///     The per-draw push block. 112 bytes, <c>VertexBit | FragmentBit</c>.
    /// </summary>
    /// <remarks>
    ///     <para><b>The 3×4 packing.</b> KSA matrices are row-vector (<c>double3.Transform(p, M)</c>
    ///     is <c>M.X·p.x + M.Y·p.y + M.Z·p.z + M.W</c>, <c>Brutal.Numerics/double3.cs:751</c>), so
    ///     component <c>i</c> of the result is the dot of <c>(p, 1)</c> with <b>column</b> <c>i</c>.
    ///     Each <c>vec4</c> here is therefore one column of the row-vector matrix, which makes the
    ///     shader's <c>vec3(dot(d2e0,v4), dot(d2e1,v4), dot(d2e2,v4))</c> reproduce
    ///     <c>float3.Transform(pos, DecalToEgo)</c> exactly. A useful consequence:
    ///     <c>vec3(d2e0.z, d2e1.z, d2e2.z)</c> is row 2 of the row-vector matrix, i.e. the decal's
    ///     <c>+z</c> axis in ego — which is how the fragment shader gets its facing reference without
    ///     a 7th vec4.</para>
    ///     <para><b>The debug flag.</b> 112 bytes is exactly full, so there is no room for a flags
    ///     word: debug draw is signalled by <see cref="TextureId"/> = <c>0xFFFFFFFF</c>, a value the
    ///     bindless table can never hand out (it has 1024 slots).</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StickerPush
    {
        public float4 DecalToEgo0;
        public float4 DecalToEgo1;
        public float4 DecalToEgo2;
        public float4 EgoToDecal0;
        public float4 EgoToDecal1;
        public float4 EgoToDecal2;
        public uint TextureId;
        public float Alpha;
        public float Brightness;
        public float NormalCutoffCos;
    }

    private readonly Renderer _renderer;
    private readonly double _maxViewDistance;

    private readonly DescriptorSetLayoutEx _depthSetLayout;
    private readonly DescriptorPoolEx _depthPool;
    private readonly VkDescriptorSet[] _depthSets;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VkPipeline _pipeline;
    private readonly BufferEx _vertexBuffer;
    private readonly BufferEx _indexBuffer;

    private bool _disposed;
    private bool _constructed;

    /// <param name="renderer">The live renderer (device, allocator, queue and dynamic state come from it).</param>
    /// <param name="maxViewDistanceMetres">Beyond this camera distance a sticker is not drawn at all.</param>
    internal StickerDecalRenderer(Renderer renderer, double maxViewDistanceMetres)
    {
        _renderer = renderer;
        _maxViewDistance = maxViewDistanceMetres;
        var device = renderer.Device;

        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };
        _depthSetLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings = new Span<VkDescriptorSetLayoutBinding>(ref binding),
        }, null);

        // From here on partial construction is possible, so anything already created is released by
        // Dispose() before the exception leaves the constructor: an init failure must not leak a
        // pipeline layout or a descriptor pool for the rest of the session.
        try
        {
            // One set per frame in flight: the set for slot i is rewritten only when the engine has
            // already waited on slot i's fence, so no in-flight command buffer can be reading it
            // (the frames-in-flight reuse argument from Game/Ksa/FrameCapture.cs:40-46).
            var frames = Math.Max(1, renderer.MaxFramesInFlight);
            var poolSize = new VkDescriptorPoolSize
            {
                Type = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = frames,
            };
            _depthPool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
            {
                MaxSets = frames,
                PoolSizes = new Span<VkDescriptorPoolSize>(ref poolSize),
            }, null);
            _depthSets = new VkDescriptorSet[frames];
            for (var i = 0; i < frames; i++)
                _depthSets[i] = device.AllocateDescriptorSet(_depthPool, _depthSetLayout);

            _pipelineLayout = BuildPipelineLayout(device, _depthSetLayout);
            _pipeline = BuildPipeline(device, renderer, _pipelineLayout);
            (_vertexBuffer, _indexBuffer) = BuildGeometry(renderer);
            _constructed = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>True while the renderer is fully built and not yet disposed — the "can I draw" test.</summary>
    internal bool IsValid => _constructed && !_disposed;

    // ---- pipeline ------------------------------------------------------------------------------

    /// <summary>
    ///     Three descriptor sets — KSA's global UBO block, our scene-depth sampler, KSA's bindless
    ///     texture table — plus the one push-constant range.
    /// </summary>
    [KsaAnchor("GlobalShaderBindings.DescriptorSetLayout; Program.Instance.BindlessTextures."
            + "DescriptorSetLayout",
        SourceFile = "KSA/GlobalShaderBindings.cs:55 / RenderCore.Systems/BindlessTextureLibrary.cs:38",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "Set 0 is the game-wide Camera/GlobalLighting/Celestial/Vessel UBO block with a DYNAMIC "
            + "offset per viewport (Content/Core/Shaders/Common/Global.glsl:144). Set 1 is ours. Set 2 "
            + "is the bindless table, declared UpdateAfterBind|PartiallyBound, which is why our shader "
            + "may index a slot the game never touches. Set indices are baked into the GLSL "
            + "(SET_GLOBAL defaults to 0, SET_TEXTURE is #defined to 2), so this order is load-bearing.")]
    private static VkPipelineLayout BuildPipelineLayout(DeviceEx device, DescriptorSetLayoutEx depthSetLayout)
    {
        if (Program.Instance?.BindlessTextures is not { } bindless)
            throw new InvalidOperationException("the bindless texture table is not available yet");

        if (sizeof(StickerPush) != PushConstantBytes)
            throw new InvalidOperationException(
                $"the sticker push block is {sizeof(StickerPush)} B, but the GLSL declares {PushConstantBytes}");

        Span<VkDescriptorSetLayout> setLayouts = stackalloc VkDescriptorSetLayout[3];
        setLayouts[0] = GlobalShaderBindings.DescriptorSetLayout;
        setLayouts[1] = depthSetLayout;
        setLayouts[2] = bindless.DescriptorSetLayout;

        var pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Offset = ByteSize.Zero,
            Size = ByteSize.Of<StickerPush>(),
        };
        return device.CreatePipelineLayout(setLayouts,
            new ReadOnlySpan<VkPushConstantRange>(ref pushRange), null);
    }

    /// <summary>
    ///     Builds the decal pipeline against the resolved offscreen colour image, exactly as
    ///     <c>GridPass.BuildPipeline</c> does for the map grid.
    /// </summary>
    /// <remarks>
    ///     <para><b>Hand-built rendering info, not <c>SetupGraphicsPipeline</c>.</b> The target's
    ///     helper stamps <c>RasterizationSamples</c> from its own MSAA state and both attachment
    ///     formats; this pass draws <i>after</i> the resolve, into the single-sample output image and
    ///     with no depth attachment at all, so it must declare that itself.</para>
    ///     <para><b><c>CullFront</c>.</b> The cube is wound counter-clockwise seen from outside (the
    ///     glTF convention every KSA mesh renderer assumes with
    ///     <c>CullMode=BackBit, FrontFace=CounterClockwise</c>, e.g.
    ///     <c>KSA/PartModelRenderer.cs:166-167</c>), so culling the front faces leaves the far faces —
    ///     which means the box still covers its screen footprint when the camera is <i>inside</i> it,
    ///     the case a near-face-only draw would clip away. Same reason KSA draws the planet with
    ///     <c>CullFront</c> (<c>KSA/PlanetRenderer.cs:1528</c>).</para>
    ///     <para><b>No depth test.</b> There is no depth attachment; occlusion is decided per fragment
    ///     from the sampled scene depth, which is also what lets the decal wrap around geometry the
    ///     box merely intersects.</para>
    /// </remarks>
    [KsaAnchor("ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte>, VkShaderStageFlags, "
            + "CompileOptions?, ReadOnlySpan<byte>); ModLibrary.Get<ShaderReference>(\"GridFrag\").ModPath; "
            + "Program.Instance.ColorFormat; Presets.{InputAssembly.TriangleList,Rasterization.Fill.CullFront}; "
            + "RenderingPresets.{ReverseZDepthStencil.NoDepthTest,BlendState.BlendColorAlphaOver}; "
            + "Renderer.{Device,DynamicStateInfo,ViewportState}",
        SourceFile = "RenderCore/ShaderModuleUtils.cs:77 / KSA/ModLibrary.cs / KSA/FileReference.cs:24 / "
            + "KSA/Program.cs:199 / Brutal.VulkanApi.Abstractions/Presets.cs:167,213 / "
            + "KSA/RenderingPresets.cs:63,95 / Core/Renderer.cs:21-23 / KSA/GridPass.cs:137-198",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "A null CompileOptions uses ShaderModuleUtils' own defaults, which already carry the "
            + "device's Vulkan/SPIR-V target and the default include callbacks "
            + "(ShaderModuleUtils.cs:16-22). #include resolves relative to the DIRECTORY OF THE "
            + "debugName (Brutal.ShaderCApi/ShaderC.cs:253 Utf8.Path.Combine(GetDirectoryName("
            + "requestingSource), source)), so the debug name is a real path next to Grid.frag — "
            + "found through the shipped GridFrag asset (Content/Core/DefaultAssets.xml:367) rather "
            + "than hard-coded — and it MUST be NUL-terminated, like Game/Ksa/Paint/PartPaintPatches"
            + ".cs:56-59. Modules we compile are OURS to destroy (unlike ModLibrary's), which happens "
            + "as soon as the pipeline is created. Program.Instance.ColorFormat is the format the main "
            + "offscreen target is constructed with (Program.cs:1427), i.e. R16G16B16A16_SFLOAT.")]
    private static VkPipeline BuildPipeline(DeviceEx device, Renderer renderer, VkPipelineLayout layout)
    {
        var directory = ShaderIncludeDirectory();
        var vertexModule = Compile(device, VertexShader, VkShaderStageFlags.VertexBit,
            Path.Combine(directory, "gatos_sticker.vert"));
        VkShaderModule fragmentModule;
        try
        {
            fragmentModule = Compile(device, FragmentShader, VkShaderStageFlags.FragmentBit,
                Path.Combine(directory, "gatos_sticker.frag"));
        }
        catch
        {
            device.DestroyShaderModule(vertexModule, null);
            throw;
        }

        try
        {
            Span<VkPipelineShaderStageCreateInfo> stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo
            {
                Name = "main"u8.AsPointer(),
                Module = vertexModule,
                Stage = VkShaderStageFlags.VertexBit,
            };
            stages[1] = new VkPipelineShaderStageCreateInfo
            {
                Name = "main"u8.AsPointer(),
                Module = fragmentModule,
                Stage = VkShaderStageFlags.FragmentBit,
            };

            var vertexInput = new VertexInput(1, 1)
                .AddBinding(0, ByteSize.Of<float3>(), VkVertexInputRate.Vertex)
                .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
                .Check();

            var multisample = new VkPipelineMultisampleStateCreateInfo
            {
                RasterizationSamples = VkSampleCountFlags._1Bit,
            };
            var colorFormat = Program.Instance?.ColorFormat ?? VkFormat.R16G16B16A16SFloat;
            var rendering = new VkPipelineRenderingCreateInfo
            {
                ColorAttachmentCount = 1,
                ColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = VkFormat.Undefined,
                StencilAttachmentFormat = VkFormat.Undefined,
                ViewMask = 0,
            };
            var info = new VkGraphicsPipelineCreateInfo
            {
                Layout = layout,
                Next = &rendering,
                RenderPass = VkRenderPass.NullHandle,
                StageCount = stages.Length,
                Stages = stages.AsPointer(),
                DynamicState = renderer.DynamicStateInfo,
                ViewportState = renderer.ViewportState,
                VertexInputState = vertexInput,
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                RasterizationState = Presets.Rasterization.Fill.CullFront,
                DepthStencilState = RenderingPresets.ReverseZDepthStencil.NoDepthTest,
                ColorBlendState = RenderingPresets.BlendState.BlendColorAlphaOver,
                MultisampleState = &multisample,
            };
            return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
        }
        finally
        {
            // Ours, unlike ModLibrary's modules: destroy them the moment the pipeline holds the code.
            device.DestroyShaderModule(vertexModule, null);
            device.DestroyShaderModule(fragmentModule, null);
        }
    }

    /// <summary>
    ///     The directory KSA's own shaders live in, taken from a shipped asset so it follows the
    ///     install rather than being guessed. Every <c>#include</c> in our two shaders resolves
    ///     relative to it.
    /// </summary>
    private static string ShaderIncludeDirectory()
    {
        var reference = ModLibrary.Get<ShaderReference>("GridFrag")
                        ?? throw new InvalidOperationException("the 'GridFrag' shader asset is missing");
        return Path.GetDirectoryName(reference.ModPath)
               ?? throw new InvalidOperationException($"'{reference.ModPath}' has no directory");
    }

    /// <summary>Compiles one GLSL string, turning a shaderc failure into a message with the full log.</summary>
    private static VkShaderModule Compile(DeviceEx device, string source, VkShaderStageFlags stage, string debugPath)
    {
        try
        {
            // The NUL is required: the include resolver reads debugName as a C string.
            return ShaderModuleUtils.FromString(device, Encoding.UTF8.GetBytes(source), stage, null,
                Encoding.UTF8.GetBytes(debugPath + "\0"));
        }
        catch (Brutal.ShaderCApi.ShaderException ex)
        {
            throw new InvalidOperationException(
                $"gatOS sticker shader '{Path.GetFileName(debugPath)}' failed to compile: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Uploads the unit cube: 8 corners of <c>[-0.5, 0.5]³</c> and 36 indices, wound
    ///     counter-clockwise seen from outside.
    /// </summary>
    [KsaAnchor("Renderer.{Allocator,Graphics}; BufferEx.CreateInfo; VkUtils.StageAndUploadToBuffer",
        SourceFile = "Core/Renderer.cs / RenderCore/VkUtils.cs / Planet.Render.Core",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "The identical one-shot staging upload ThugLifeQuadRenderer.BuildGeometry does: a "
            + "private command buffer submitted out of band and waited on. This is the known "
            + "validation item shared with the clutter-texture upload path (STICKERS_PLAN §5 risk 3); "
            + "it happens exactly once, when the first sticker goes live.")]
    private static (BufferEx Vertices, BufferEx Indices) BuildGeometry(Renderer renderer)
    {
        Span<float3> vertices =
        [
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f),
        ];
        Span<ushort> indices =
        [
            4, 5, 6, 4, 6, 7, // +z
            0, 3, 2, 0, 2, 1, // -z
            1, 2, 6, 1, 6, 5, // +x
            0, 4, 7, 0, 7, 3, // -x
            3, 7, 6, 3, 6, 2, // +y
            0, 1, 5, 0, 5, 4, // -y
        ];

        var vertexBuffer = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "gatos-sticker-vb",
            BufferUsage = VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit,
            BufferSize = ByteSize.Of<float3>(vertices.Length),
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });
        var indexBuffer = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "gatos-sticker-ib",
            BufferUsage = VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit,
            BufferSize = ByteSize.Of<ushort>(indices.Length),
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });

        using var staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
        var command = staging.NextCommandBuffer();
        command.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        VkUtils.StageAndUploadToBuffer(staging, vertexBuffer.VkBuffer, vertexBuffer.BindOffset, vertices, command);
        VkUtils.StageAndUploadToBuffer(staging, indexBuffer.VkBuffer, indexBuffer.BindOffset, indices, command);
        command.End();
        staging.Submit().Wait();
        return (vertexBuffer, indexBuffer);
    }

    // ---- the pass ------------------------------------------------------------------------------

    /// <summary>
    ///     Records one draw per drawable sticker into the resolved colour image. Called from the
    ///     <c>ResolveAttachments</c> postfix, on the main thread, inside the frame's command buffer.
    /// </summary>
    /// <param name="commandBuffer">The command buffer KSA is recording the frame into.</param>
    /// <param name="entries">The manager's published immutable array (never mutated here).</param>
    /// <param name="debug">Draw the magenta box checker instead of sampling the image.</param>
    [KsaAnchor("Program.{OffscreenTarget,SetViewport,Instance.ResourceFrameIndex,PointClampedSampler,"
            + "MainViewport}; RenderTarget.{DepthImage,ColorImage,Extent}; BarrierBatch; "
            + "ImageBarrierInfo.Presets.{DepthSampledReadF,ColorAttachmentReadWrite}; "
            + "GlobalShaderBindings.{DescriptorSet,DynamicOffset}; Program.Instance.BindlessTextures.DescriptorSet",
        SourceFile = "KSA/Program.cs:432,442,458,195,4062 / KSA.Rendering/RenderTarget.cs:36,38,48 / "
            + "KSA.Rendering/BarrierBatch.cs / KSA.Rendering/ImageBarrierInfo.cs:18,41 / "
            + "KSA/GlobalShaderBindings.cs:57,64 / KSA/GridPass.cs:427-471",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "A near-verbatim port of GridPass.Run, the engine's own post-resolve overlay. Depth is "
            + "moved to DepthSampledReadF and LEFT there, exactly as GridPass leaves it — the engine's "
            + "tracked-state barriers tolerate that for the rest of the frame and next frame's "
            + "ClearDepthImages barriers from the tracked state. The scene depth is REVERSE-Z, so 0 is "
            + "the far plane and 'nothing was drawn' (Content/Core/Shaders/Grid.frag:67-72). The "
            + "descriptor set for this frame's slot is safe to rewrite because the engine has already "
            + "waited on that slot's fence (Program.cs:2123-2138 advances ResourceFrameIndex modulo "
            + "MaxFramesInFlight). The depth descriptor is written with DepthReadOnlyOptimal and the "
            + "point-clamped sampler, both copied from GridPass.UpdateDescriptorSet (:120-135).")]
    internal void RecordPass(CommandBuffer commandBuffer, ReadOnlySpan<StickerEntry> entries, bool debug)
    {
        if (_disposed || entries.Length == 0)
            return;
        if (Program.OffscreenTarget is not { } target)
            return;
        if (target.DepthImage is not { } depthImage || target.ColorImage is not { } colorImage)
            return;

        var drawable = 0;
        foreach (var entry in entries)
            if (IsDrawable(entry))
                drawable++;
        if (drawable == 0)
            return;

        UpdateDepthDescriptor(depthImage, out var depthSet);

        Span<VkImageMemoryBarrier2> barrierImages = stackalloc VkImageMemoryBarrier2[2];
        var barriers = new BarrierBatch(barrierImages);
        barriers.Add(depthImage, ImageBarrierInfo.Presets.DepthSampledReadF);
        barriers.Add(colorImage, ImageBarrierInfo.Presets.ColorAttachmentReadWrite, 0, inForceBarrier: true);
        barriers.SubmitAndFlush(commandBuffer);

        var attachment = new VkRenderingAttachmentInfo
        {
            ImageView = colorImage.ImageView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            ResolveMode = VkResolveModeFlags.None,
            LoadOp = VkAttachmentLoadOp.Load,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var renderingInfo = new VkRenderingInfo
        {
            RenderArea = new VkRect2D { Extent = target.Extent },
            LayerCount = 1,
            ViewMask = 0,
            ColorAttachmentCount = 1,
            ColorAttachments = &attachment,
        };
        commandBuffer.BeginRendering(in renderingInfo);
        try
        {
            commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            Program.SetViewport(commandBuffer);

            var globalOffset = (ByteSize32)GlobalShaderBindings.DynamicOffset(Program.MainViewport.Index);
            var globalSet = GlobalShaderBindings.DescriptorSet;
            commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0,
                new ReadOnlySpan<VkDescriptorSet>(ref globalSet),
                new Span<ByteSize32>(ref globalOffset));
            commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 1,
                new ReadOnlySpan<VkDescriptorSet>(ref depthSet), default(Span<ByteSize32>));
            if (Program.Instance?.BindlessTextures is { } bindless)
            {
                var bindlessSet = bindless.DescriptorSet;
                commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 2,
                    new ReadOnlySpan<VkDescriptorSet>(ref bindlessSet), default(Span<ByteSize32>));
            }

            VkBuffer vertexHandle = _vertexBuffer.VkBuffer;
            var vertexOffset = (ByteSize64)_vertexBuffer.BindOffset;
            commandBuffer.BindVertexBuffers(0,
                new ReadOnlySpan<VkBuffer>(ref vertexHandle),
                new ReadOnlySpan<ByteSize64>(ref vertexOffset));
            commandBuffer.BindIndexBuffer(_indexBuffer.VkBuffer, (ByteSize64)_indexBuffer.BindOffset,
                VkIndexType.Uint16);

            foreach (var entry in entries)
            {
                if (!IsDrawable(entry))
                    continue;
                var push = Push(entry, debug);
                commandBuffer.PushConstants(_pipelineLayout,
                    VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);
                commandBuffer.DrawIndexed(CubeIndexCount, 1, 0, 0, 0);
            }
        }
        finally
        {
            commandBuffer.EndRendering();
        }
    }

    private bool IsDrawable(StickerEntry entry)
        => entry.Visible && entry.Live && entry.TextureHandle >= 0
           && entry.DistanceEgo <= _maxViewDistance;

    private StickerPush Push(StickerEntry entry, bool debug) => new()
    {
        DecalToEgo0 = Column(in entry.DecalToEgo, 0),
        DecalToEgo1 = Column(in entry.DecalToEgo, 1),
        DecalToEgo2 = Column(in entry.DecalToEgo, 2),
        EgoToDecal0 = Column(in entry.EgoToDecal, 0),
        EgoToDecal1 = Column(in entry.EgoToDecal, 1),
        EgoToDecal2 = Column(in entry.EgoToDecal, 2),
        TextureId = debug ? DebugTextureId : (uint)entry.TextureHandle,
        Alpha = (float)entry.Alpha,
        Brightness = (float)entry.Brightness,
        NormalCutoffCos = NormalCutoff,
    };

    /// <summary>One column of a row-vector matrix — see <see cref="StickerPush"/> for why.</summary>
    private static float4 Column(ref readonly float4x4 matrix, int index) => index switch
    {
        0 => new float4(matrix.X.X, matrix.Y.X, matrix.Z.X, matrix.W.X),
        1 => new float4(matrix.X.Y, matrix.Y.Y, matrix.Z.Y, matrix.W.Y),
        _ => new float4(matrix.X.Z, matrix.Y.Z, matrix.Z.Z, matrix.W.Z),
    };

    /// <summary>Points this frame's ring slot at the live resolved depth image.</summary>
    private void UpdateDepthDescriptor(RenderImage depthImage, out VkDescriptorSet set)
    {
        var slot = Program.Instance is { } program
            ? (uint)program.ResourceFrameIndex % (uint)_depthSets.Length
            : 0u;
        set = _depthSets[slot];

        var imageInfo = new VkDescriptorImageInfo
        {
            ImageLayout = VkImageLayout.DepthReadOnlyOptimal,
            ImageView = depthImage.ImageView,
            Sampler = Program.PointClampedSampler,
        };
        var write = new VkWriteDescriptorSet
        {
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            ImageInfo = &imageInfo,
        };
        _renderer.Device.UpdateDescriptorSets(
            new ReadOnlySpan<VkWriteDescriptorSet>(ref write),
            default(ReadOnlySpan<VkCopyDescriptorSet>));
    }

    /// <summary>
    ///     Frees every GPU object, in reverse creation order and best-effort. The caller has already
    ///     cleared <c>StickerManager.Active</c> and waited for the device to go idle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var device = _renderer.Device;
        // The shader modules were destroyed at pipeline creation — they are not held here. Every step
        // is guarded because this also runs from a partially built constructor.
        if (_constructed)
        {
            try { _vertexBuffer.Dispose(); } catch (Exception ex) { Note(ex); }
            try { _indexBuffer.Dispose(); } catch (Exception ex) { Note(ex); }
        }

        try { device.DestroyPipeline(_pipeline, null); } catch (Exception ex) { Note(ex); }
        try { device.DestroyPipelineLayout(_pipelineLayout, null); } catch (Exception ex) { Note(ex); }
        // The pool may be unset when a throw beat its creation; the layout always exists by then.
        try { _depthPool?.Dispose(); } catch (Exception ex) { Note(ex); }
        try { _depthSetLayout.Dispose(); } catch (Exception ex) { Note(ex); }
    }

    private static void Note(Exception ex)
        => ModLog.Log.Debug($"gatOS sticker renderer teardown step failed: {ex.Message}");

    // ---- shaders -------------------------------------------------------------------------------

    /// <summary>
    ///     The push block, spelled identically in both stages (Vulkan requires the layouts to match).
    /// </summary>
    private const string PushBlock =
        """
        layout(push_constant) uniform Sticker {
            vec4 d2e0; vec4 d2e1; vec4 d2e2;
            vec4 e2d0; vec4 e2d1; vec4 e2d2;
            uint texId;
            float alpha;
            float brightness;
            float normalCutoff;
        } st;
        """;

    /// <summary>
    ///     Transforms the unit cube's corners into ego and projects them. Nothing is interpolated:
    ///     the fragment shader works entirely from the depth buffer and the push constants.
    /// </summary>
    private const string VertexShader =
        $$"""
          #version 450

          #include "Common/Camera.glsl"

          layout(location = 0) in vec3 inPos;

          {{PushBlock}}

          void main()
          {
              // Row-vector 3x4: each d2e row is a COLUMN of DecalToEgo, so this is float3.Transform.
              vec4 p = vec4(inPos, 1.0);
              vec3 ego = vec3(dot(st.d2e0, p), dot(st.d2e1, p), dot(st.d2e2, p));
              gl_Position = global.camera.viewProjection * vec4(ego, 1.0);
          }
          """;

    /// <summary>
    ///     Reconstructs the scene position under the pixel from the resolved reverse-Z depth, rejects
    ///     it if it falls outside the decal box or the surface faces the wrong way, and shades the
    ///     sampled texel with a single sun term plus planetshine.
    /// </summary>
    private const string FragmentShader =
        $$"""
          #version 450

          // Must precede the include: TextureSet.glsl declares globalTextures[]/samplers[] at this set.
          #define SET_TEXTURE 2
          #include "Common/TextureSet.glsl"
          #include "Common/Camera.glsl"

          layout(set = 1, binding = 0) uniform sampler2D sceneDepth;

          {{PushBlock}}

          layout(location = 0) out vec4 outColor;

          // The fast 2.2 approximation from Common/Shared.glsl:203, inlined rather than including
          // Shared.glsl, which would pull in four more files for one pow().
          vec3 StickerGammaToLinear(vec3 sRGBValue)
          {
              return pow(sRGBValue, vec3(2.2));
          }

          void main()
          {
              // Screen-sized and single-sample after ResolveAttachments: exactly one texel per fragment.
              ivec2 size = textureSize(sceneDepth, 0);
              float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;

              // Same convention as Camera.ScreenToEgoNearPlane (KSA/Camera.cs:658-671): ndc = 2*p/size - 1
              // on BOTH axes, no Y flip -- the projection already carries it (M22 is negated in
              // ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ).
              vec2 ndc = (gl_FragCoord.xy / vec2(size)) * 2.0 - 1.0;
              vec4 v = global.camera.inverseProjection * vec4(ndc, z, 1.0);
              v /= v.w;

              // The view matrix is rotation-only (KSA/Camera.cs:482-492), so undoing it lands in ego.
              vec3 pEgo = (global.camera.inverseView * vec4(v.xyz, 1.0)).xyz;

              // The receiving surface's normal, from the reconstructed position's screen derivatives.
              // Taken BEFORE any discard: derivatives are only defined in uniform control flow, and a
              // neighbour that has already been discarded would otherwise make this undefined rather
              // than merely noisy. (Noisy it still is at a depth discontinuity -- that is the known
              // one-pixel edge artifact of a projected decal, and the NaN-safe tests below eat it.)
              vec3 n = normalize(cross(dFdx(pEgo), dFdy(pEgo)));

              // Reverse-Z: 0 is the far plane AND what untouched background reads as. A decal has
              // nothing to stick to there.
              if (z <= 0.0) discard;

              vec4 p4 = vec4(pEgo, 1.0);
              vec3 pDec = vec3(dot(st.e2d0, p4), dot(st.e2d1, p4), dot(st.e2d2, p4));
              // Negated form so a NaN coordinate (a degenerate reconstruction) discards too.
              if (!all(lessThanEqual(abs(pDec), vec3(0.5)))) discard;

              // Decal +z in ego = row 2 of the row-vector matrix = the z of each packed column.
              vec3 axisZ = normalize(vec3(st.d2e0.z, st.d2e1.z, st.d2e2.z));

              // The winding the derivatives produce is arbitrary, so orient the normal towards the
              // decal instead of trusting its sign.
              float facing = dot(n, axisZ);
              if (facing < 0.0) { n = -n; facing = -facing; }
              // Negated again: a NaN normal must discard, not sail through as "not less than".
              if (!(facing >= st.normalCutoff)) discard;

              // Debug: an 8x8 magenta checker in decal space proves the box, the reverse-Z
              // reconstruction and the NDC convention without involving any art.
              if (st.texId == 0xFFFFFFFFu)
              {
                  vec2 cell = floor(pDec.xy * 8.0);
                  float checker = mod(cell.x + cell.y, 2.0);
                  outColor = vec4(1.0, 0.0, 1.0, 0.35 + 0.3 * checker);
                  return;
              }

              // Sampler 0 is the table's linear-clamped, full-mip sampler. PNG row 0 is the TOP, so v
              // is flipped to keep decal +y pointing at the top of the image.
              vec4 texel = SAMPLE_TEXTURE(st.texId, 0, pDec.xy * vec2(1.0, -1.0) + 0.5);
              if (texel.a < 0.004) discard;

              // sunPosition is the sun's EGO position (Program.UpdateShaderData, KSA/Program.cs:2594-2603),
              // and sunColor is the star's light colour (Universe.SunlightColor). planetColor is the
              // nearby atmospheric body's lit colour and is ZERO for an airless body or a camera in
              // shadow, so the small constant is what keeps a night-side sticker from going black.
              vec3 L = normalize(global.lighting.sunPosition.xyz - pEgo);
              vec3 ambient = 0.12 * global.lighting.planetColor.rgb + vec3(0.02);
              vec3 lit = StickerGammaToLinear(texel.rgb)
                  * (global.lighting.sunColor.rgb * max(dot(n, L), 0.0) + ambient)
                  * st.brightness;

              outColor = vec4(lit,
                  texel.a * st.alpha * smoothstep(st.normalCutoff, st.normalCutoff + 0.2, facing));
          }
          """;
}
