using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.ThugLife;

/// <summary>
///     Owns the per-draw GPU resources (pipeline, descriptor set, vertex/index buffers, texture) for the
///     thug-life sunglasses quad and records one draw per entry per frame (ported from the sibling
///     <c>unscience</c> mod; see <c>.claude/skills/ksa/quad.md</c> for the why behind every pipeline
///     choice). The model matrix is rebuilt per entry per frame and pushed as a vertex push constant.
/// </summary>
internal sealed unsafe class ThugLifeQuadRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct QuadVertex
    {
        public float3 Pos;
        public float2 Uv;
    }

    private readonly Renderer _renderer;

    private readonly DescriptorSetLayoutEx _descriptorSetLayout;
    private readonly DescriptorPoolEx _descriptorPool;
    private readonly VkDescriptorSet _descriptorSet;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VkPipeline _pipeline;
    private readonly BufferEx _vb;
    private readonly BufferEx _ib;
    private readonly int _indexCount;

    private bool _disposed;

    public bool IsValid => !_disposed;

    public ThugLifeQuadRenderer(Renderer renderer, ThugLifeTextureFactory texture)
    {
        _renderer = renderer;
        var device = renderer.Device;

        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };
        _descriptorSetLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings = new Span<VkDescriptorSetLayoutBinding>(ref binding),
        }, null);

        var poolSize = new VkDescriptorPoolSize
        {
            Type = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
        };
        _descriptorPool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = 1,
            PoolSizes = new Span<VkDescriptorPoolSize>(ref poolSize),
        }, null);
        _descriptorSet = device.AllocateDescriptorSet(_descriptorPool, _descriptorSetLayout);

        var imageInfo = new VkDescriptorImageInfo
        {
            ImageView = texture.ImageView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            Sampler = texture.Sampler,
        };
        var write = new VkWriteDescriptorSet
        {
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &imageInfo,
        };
        device.UpdateDescriptorSets(
            new ReadOnlySpan<VkWriteDescriptorSet>(ref write),
            default(ReadOnlySpan<VkCopyDescriptorSet>));

        var pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = ByteSize.Zero,
            Size = ByteSize.Of<float4x4>(),
        };
        VkDescriptorSetLayout dslHandle = _descriptorSetLayout;
        _pipelineLayout = device.CreatePipelineLayout(
            new ReadOnlySpan<VkDescriptorSetLayout>(ref dslHandle),
            new ReadOnlySpan<VkPushConstantRange>(ref pushRange),
            null);

        _pipeline = BuildPipeline(device, renderer, _pipelineLayout);
        (_vb, _ib, _indexCount) = BuildGeometry(renderer);
    }

    [KsaAnchor("Program.OffScreenPass.{Pass,SampleCount}; ModLibrary.Get<ShaderReference>(\"UnlitMeshVert\"/"
            + "\"UnlitMeshFrag\"); RenderTechnique.CreateShaderStages; Presets/RenderingPresets; "
            + "Renderer.{Device,Allocator,DynamicStateInfo,ViewportState,Graphics}; VkUtils.StageAndUploadToBuffer",
        SourceFile = "KSA/Program.cs / KSA/ModLibrary.cs / KSA/RenderingPresets.cs / Planet.Render.Core / Brutal.Vulkan*",
        Verified = "2026-06-28", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.High,
        Notes = "Builds the GPU pipeline for the thug-life quad against KSA's offscreen pass + stock UnlitMesh "
            + "shaders. Deepest render-internals coupling in gatOS; off by default and self-disables on fault.")]
    private static VkPipeline BuildPipeline(DeviceEx device, Renderer renderer, VkPipelineLayout layout)
    {
        var shaderRefs = new[]
        {
            ModLibrary.Get<ShaderReference>("UnlitMeshVert"),
            ModLibrary.Get<ShaderReference>("UnlitMeshFrag"),
        };
        var stages = RenderTechnique.CreateShaderStages(device, shaderRefs.AsSpan());

        var vertexInput = new VertexInput(1, 2)
            .AddBinding(0, ByteSize.Of<QuadVertex>(), VkVertexInputRate.Vertex)
            .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
            .AddAttribute(1, 0, VkFormat.R32G32SFloat, ByteSize.Of<float3>())
            .Check();

        // CRITICAL (quad.md): bind to Program.OffScreenPass (the MSAA scene pass), NOT Program.MainPass
        // (the 1-sample swapchain pass) — wrong pass passes validation but silently breaks depth.
        var multisample = new VkPipelineMultisampleStateCreateInfo
        {
            RasterizationSamples = Program.OffScreenPass.SampleCount,
        };

        var info = new VkGraphicsPipelineCreateInfo
        {
            Layout = layout,
            RenderPass = Program.OffScreenPass.Pass,
            Subpass = 0,
            StageCount = stages.Count,
            Stages = stages,
            DynamicState = renderer.DynamicStateInfo,
            ViewportState = renderer.ViewportState,
            VertexInputState = vertexInput,
            InputAssemblyState = Presets.InputAssembly.TriangleList,
            RasterizationState = Presets.Rasterization.Fill.CullNone, // double-sided
            DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestWrite, // offscreen pass is reverse-Z
            ColorBlendState = Presets.BlendState.BlendColorAlpha,
            MultisampleState = &multisample,
        };
        return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
    }

    /// <summary>
    ///     Builds geometry as one small quad per opaque pixel of <see cref="ThugLifeTexturePattern"/>.
    ///     Transparent pixels emit no geometry, which is what produces the cut-out blocky sunglasses
    ///     shape — the stock <c>UnlitMeshFrag</c> shader hard-writes <c>alpha = 1.0</c>, so alpha-blend
    ///     transparency is unavailable; cut-out-via-geometry is.
    /// </summary>
    private static (BufferEx vb, BufferEx ib, int indexCount) BuildGeometry(Renderer renderer)
    {
        int w = ThugLifeTexturePattern.Width;
        int h = ThugLifeTexturePattern.Height;

        var verts = new List<QuadVertex>(w * h * 4);
        var indices = new List<ushort>(w * h * 6);

        // Tiny per-texel UV inset so a quad samples the centre of its texel and never bleeds into
        // the next under nearest-neighbour filtering.
        const float uvInset = 0.001f;

        for (var row = 0; row < h; row++)
        {
            var rowStr = ThugLifeTexturePattern.Rows[row];
            for (var col = 0; col < w; col++)
            {
                if (rowStr[col] == '.')
                    continue; // transparent — skip

                var x0 = -0.5f + (float)col / w;
                var x1 = -0.5f + (float)(col + 1) / w;
                // Flip Y so pattern row 0 maps to the top of the quad (+Y).
                var y1 = 0.5f - (float)row / h;
                var y0 = 0.5f - (float)(row + 1) / h;

                var u0 = (col + uvInset) / w;
                var u1 = (col + 1 - uvInset) / w;
                var v0 = (row + uvInset) / h;
                var v1 = (row + 1 - uvInset) / h;

                var baseIdx = (ushort)verts.Count;
                verts.Add(new QuadVertex { Pos = new float3(x0, y0, 0f), Uv = new float2(u0, v1) });
                verts.Add(new QuadVertex { Pos = new float3(x1, y0, 0f), Uv = new float2(u1, v1) });
                verts.Add(new QuadVertex { Pos = new float3(x1, y1, 0f), Uv = new float2(u1, v0) });
                verts.Add(new QuadVertex { Pos = new float3(x0, y1, 0f), Uv = new float2(u0, v0) });

                indices.Add((ushort)(baseIdx + 0));
                indices.Add((ushort)(baseIdx + 1));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 0));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 3));
            }
        }

        var vbSpan = CollectionsMarshal.AsSpan(verts);
        var ibSpan = CollectionsMarshal.AsSpan(indices);

        var vb = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "thug-life-vb",
            BufferUsage = VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit,
            BufferSize = ByteSize.Of<QuadVertex>(vbSpan.Length),
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });
        var ib = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = "thug-life-ib",
            BufferUsage = VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit,
            BufferSize = ByteSize.Of<ushort>(ibSpan.Length),
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });

        using var staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
        var cmd = staging.NextCommandBuffer();
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        VkUtils.StageAndUploadToBuffer(staging, vb.VkBuffer, vb.BindOffset, vbSpan, cmd);
        VkUtils.StageAndUploadToBuffer(staging, ib.VkBuffer, ib.BindOffset, ibSpan, cmd);
        cmd.End();
        staging.Submit().Wait();

        return (vb, ib, ibSpan.Length);
    }

    /// <summary>
    ///     Records the draw for a single entry. The caller must already be inside the offscreen render
    ///     pass (invoked from the postfix on <c>SuperMeshRenderSystem.RenderMainPass</c>).
    /// </summary>
    [KsaAnchor("Program.GetMainCamera().MVP.viewProjection; Program.SetViewport(cmd); "
            + "Vehicle.GetMatrixAsmb2Ego(Camera); Vehicle.Asmb2Ego; Part.PositionEgo(in double4x4); "
            + "Part.Asmb2Ego(doubleQuat); double3.Transform",
        SourceFile = "KSA/Program.cs / KSA/Camera.cs / KSA/Vehicle.cs / KSA/Part.cs",
        Verified = "2026-06-28", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.High,
        Notes = "Per-frame ego-space model matrix + draw for one thug-life quad. Render-internals churn.")]
    public void RecordDraw(CommandBuffer cmd, ThugLifeEntry entry)
    {
        if (_disposed || !entry.Visible)
            return;
        if (!TryComputeModelEgo(entry, out var modelEgo))
            return;

        var camera = Program.GetMainCamera();
        if (camera == null)
            return;

        // Row-vector convention (KSA matches .NET / DirectXMath): MVP = model * viewProjection.
        var mvp = modelEgo * camera.MVP.viewProjection;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        VkDescriptorSet setCopy = _descriptorSet;
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0,
            new ReadOnlySpan<VkDescriptorSet>(ref setCopy),
            default(Span<ByteSize32>));

        Program.SetViewport(cmd);
        cmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);

        VkBuffer vbHandle = _vb.VkBuffer;
        ByteSize64 vbOff = (ByteSize64)_vb.BindOffset;
        cmd.BindVertexBuffers(0,
            new ReadOnlySpan<VkBuffer>(ref vbHandle),
            new ReadOnlySpan<ByteSize64>(ref vbOff));
        cmd.BindIndexBuffer(_ib.VkBuffer, (ByteSize64)_ib.BindOffset, VkIndexType.Uint16);
        cmd.DrawIndexed(_indexCount, 1, 0, 0, 0);
    }

    /// <summary>
    ///     Composes the ego-space model matrix for one entry. The quad is anchored to a top-level part
    ///     (rotation + position pulled separately so the part's own scale is excluded — width/height are
    ///     the sole size control), or, when <see cref="ThugLifeEntry.Part"/> is null (<c>part_iid 0</c>),
    ///     to the vehicle's assembly origin/orientation.
    /// </summary>
    private static bool TryComputeModelEgo(ThugLifeEntry entry, out float4x4 model)
    {
        model = float4x4.Identity;
        var camera = Program.GetMainCamera();
        if (camera == null || entry.Vehicle is not { } vehicle)
            return false;

        var vehMat = vehicle.GetMatrixAsmb2Ego(camera);
        double3 partPos;
        doubleQuat partRot;
        if (entry.Part is { } part)
        {
            partPos = part.PositionEgo(in vehMat);
            partRot = part.Asmb2Ego(vehicle.Asmb2Ego);
        }
        else
        {
            // part_iid 0 → anchor to the vehicle's assembly frame: the origin in ego is vehMat·0.
            partPos = double3.Transform(default, vehMat);
            partRot = vehicle.Asmb2Ego;
        }

        var partRotMat = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRot));
        var partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
        var partEgo = partRotMat * partTransMat;

        const float deg2rad = MathF.PI / 180f;
        var userRot = float4x4.CreateRotationX(entry.Rotation.X * deg2rad)
                      * float4x4.CreateRotationY(entry.Rotation.Y * deg2rad)
                      * float4x4.CreateRotationZ(entry.Rotation.Z * deg2rad);
        var userTrans = float4x4.CreateTranslation(entry.Position);
        var scaleMat = float4x4.CreateScale(entry.Width, entry.Height, 1f);

        // v_local → scale → userRot → userTrans → partEgo → ego
        model = scaleMat * userRot * userTrans * partEgo;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var device = _renderer.Device;
        // NB: the shader modules come from ModLibrary and are owned by it — do NOT destroy them here.
        try { _vb.Dispose(); } catch { /* best-effort */ }
        try { _ib.Dispose(); } catch { /* best-effort */ }
        try { device.DestroyPipeline(_pipeline, null); } catch { /* best-effort */ }
        try { device.DestroyPipelineLayout(_pipelineLayout, null); } catch { /* best-effort */ }
        try { _descriptorPool.Dispose(); } catch { /* best-effort */ }
        try { _descriptorSetLayout.Dispose(); } catch { /* best-effort */ }
    }
}
