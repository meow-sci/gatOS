# Rendering a Textured Quad in KSA, Anchored to a Part/SubPart

How to draw a flat, double-sided, world-space quad that samples a `VkImageView` (e.g. an off-screen render target) and is positioned in 3D relative to a `Part` or `SubPart` on a `Vehicle`. Non–terminal-specific: anything that can produce a sampleable `VkImage` + `VkImageView` + `VkSampler` can drive this.

## What you need from the rest of the engine

- `Renderer renderer = Program.GetRenderer();` — must be live (do this from `OnFullyLoaded` or later, not `OnImmediateLoad`).
- A sampleable color texture (`VkImageView` + `VkSampler`) whose final layout is `ShaderReadOnlyOptimal` by the time the main pass runs.
- A target `Part` or `SubPart` on `Program.ControlledVehicle` (or any `Vehicle`) to anchor against.
- `Program.GetMainCamera()` — provides `camera.MVP.viewProjection` per frame.

KSA already ships an unlit shader pair you can reuse instead of compiling your own:

```csharp
ShaderReference vertRef = ModLibrary.Get<ShaderReference>("UnlitMeshVert");
ShaderReference fragRef = ModLibrary.Get<ShaderReference>("UnlitMeshFrag");
VkShaderModule vertModule = vertRef;  // implicit operator
VkShaderModule fragModule = fragRef;
```

Contract of `UnlitMesh.{vert,frag}`:
- Vertex input: `vec3 pos` at location 0, `vec2 uv` at location 1 (single binding, stride 20).
- Single push constant: `mat4` MVP at offset 0, vertex stage only.
- One combined-image-sampler at descriptor set 0, binding 0, fragment stage.
- The fragment shader applies `gammaToLinear()` to the sampled texel — see the format note in [Texture format gotcha](#texture-format-gotcha).

## GPU resource layout

### Descriptor set (1 binding)

```csharp
var binding = new VkDescriptorSetLayoutBinding {
    Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler,
    DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit,
};
var dsl = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo {
    Bindings = new Span<VkDescriptorSetLayoutBinding>(ref binding),
}, null);

var poolSize = new VkDescriptorPoolSize {
    Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1,
};
var pool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo {
    MaxSets = 1, PoolSizes = new Span<VkDescriptorPoolSize>(ref poolSize),
}, null);
var set = device.AllocateDescriptorSet(pool, dsl);

var imageInfo = new VkDescriptorImageInfo {
    ImageView = sourceImageView,
    ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
    Sampler = sourceSampler,
};
VkWriteDescriptorSet write = new VkWriteDescriptorSet {
    DstSet = set, DstBinding = 0, DescriptorCount = 1,
    DescriptorType = VkDescriptorType.CombinedImageSampler,
    ImageInfo = &imageInfo,
};
device.UpdateDescriptorSets(
    new ReadOnlySpan<VkWriteDescriptorSet>(ref write),
    default(ReadOnlySpan<VkCopyDescriptorSet>));
```

If the source is rendered each frame in another pass on the same command timeline (e.g. a `RenderTarget.CreateRenderPass()`-backed offscreen pass), that pass's `FinalLayout = ShaderReadOnlyOptimal` makes the image safe to sample on this draw — no manual barriers needed.

### Pipeline layout

```csharp
VkPushConstantRange pushRange = new VkPushConstantRange {
    StageFlags = VkShaderStageFlags.VertexBit,
    Offset = ByteSize.Zero,
    Size = ByteSize.Of<float4x4>(),
};
VkDescriptorSetLayout dslHandle = dsl;
var pipelineLayout = device.CreatePipelineLayout(
    new ReadOnlySpan<VkDescriptorSetLayout>(ref dslHandle),
    new ReadOnlySpan<VkPushConstantRange>(ref pushRange),
    null);
```

### Pipeline — **the critical bits**

```csharp
// CRITICAL: bind to Program.OffScreenPass, NOT Program.MainPass.
// Program.MainPass is the SWAPCHAIN pass (1-bit samples, hard-coded).
// The scene — parts, SuperMesh draws, and a postfix on
// SuperMeshRenderSystem.RenderMainPass — actually runs inside Program._offscreenPass,
// whose sample count is GameSettings.GetSampleCount() (e.g. 4x/8x MSAA).
// Binding to MainPass with 1-bit samples while the active framebuffer is 4x
// makes depth state silently misbehave: the quad renders but does not test
// against the depth values parts wrote, so it always paints on top.
var multisample = new VkPipelineMultisampleStateCreateInfo {
    RasterizationSamples = Program.OffScreenPass.SampleCount,
};

var pipelineInfo = new VkGraphicsPipelineCreateInfo {
    Layout             = pipelineLayout,
    RenderPass         = Program.OffScreenPass.Pass,  // NOT Program.MainPass
    Subpass            = 0,
    StageCount         = 2,
    Stages             = stagesArr,                  // {Vert, Frag} from UnlitMesh
    DynamicState       = renderer.DynamicStateInfo,
    ViewportState      = renderer.ViewportState,
    VertexInputState   = vertexInput,
    InputAssemblyState = Presets.InputAssembly.TriangleList,
    RasterizationState = Presets.Rasterization.Fill.CullNone, // double-sided
    DepthStencilState  = RenderingPresets.ReverseZDepthStencil.DepthTestWrite,
    ColorBlendState    = Presets.BlendState.BlendNone,
    MultisampleState   = &multisample,
};
var pipeline = device.CreateGraphicsPipeline(default(VkPipelineCache), pipelineInfo, null);
```

Key choices and why:
- **`Program.OffScreenPass`, not `Program.MainPass`.** The scene draws inside `_offscreenPass`. Binding to the wrong pass passes pipeline-creation validation but produces broken depth at runtime.
- **`RasterizationSamples = Program.OffScreenPass.SampleCount`.** Must match the active framebuffer, including when the user has MSAA on.
- **`RenderingPresets.ReverseZDepthStencil.DepthTestWrite`.** KSA's offscreen pass is reverse-Z (clear value 0, compare op `GreaterOrEqual`). Forward-Z presets will look fine when the quad is the nearest thing on screen and wrong when it isn't.
- **`Presets.Rasterization.Fill.CullNone`.** A single quad whose orientation the user controls: cull none keeps the back face visible so the user never sees a mysteriously-invisible orientation.

### Vertex / index buffers

Quad is in the XY plane with +Z normal, centered at the origin (so user width/height scales symmetrically and rotations spin around the visible center). UVs flip V so the texture's (0,0) top-left aligns with ImGui-written content.

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct QuadVertex { public float3 Pos; public float2 Uv; }

var vertexInput = new VertexInput(1, 2)
    .AddBinding(0, ByteSize.Of<QuadVertex>(), VkVertexInputRate.Vertex)
    .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
    .AddAttribute(1, 0, VkFormat.R32G32SFloat,   ByteSize.Of<float3>())
    .Check();

Span<QuadVertex> verts = stackalloc QuadVertex[4] {
    new QuadVertex { Pos = new float3(-0.5f, -0.5f, 0f), Uv = new float2(0f, 1f) },
    new QuadVertex { Pos = new float3( 0.5f, -0.5f, 0f), Uv = new float2(1f, 1f) },
    new QuadVertex { Pos = new float3( 0.5f,  0.5f, 0f), Uv = new float2(1f, 0f) },
    new QuadVertex { Pos = new float3(-0.5f,  0.5f, 0f), Uv = new float2(0f, 0f) },
};
Span<ushort> indices = stackalloc ushort[6] { 0, 1, 2, 0, 2, 3 };
```

Upload synchronously at construction (it runs once); mirror KSA's `SimpleVkMesh` idiom — one staging pool, one cmd buffer, both copies, `Submit().Wait()`:

```csharp
using var staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
var stagingCmd = staging.NextCommandBuffer();
stagingCmd.Begin();
VkUtils.StageAndUploadToBuffer(staging, vb.VkBuffer, vb.BindOffset, verts, stagingCmd);
VkUtils.StageAndUploadToBuffer(staging, ib.VkBuffer, ib.BindOffset, indices, stagingCmd);
stagingCmd.End();
staging.Submit().Wait();
```

## Drawing into the scene — Harmony postfix on `SuperMeshRenderSystem.RenderMainPass`

There is no public "register a world-space draw" API. The way to inject a draw into the main scene is to postfix the system that runs inside the already-begun offscreen render pass:

```csharp
[HarmonyPatch(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.RenderMainPass))]
internal static class SuperMeshRenderSystem_RenderMainPass_Patch
{
    // RenderMainPass runs inside an already-begun render pass on the supplied
    // command buffer. A postfix appends our draw after KSA's opaque mesh draws,
    // before the caller (Program.RenderGame) calls EndRenderPass.
    static void Postfix(CommandBuffer commandBuffer)
    {
        if (!Active) return;
        try { QuadInstance?.RecordDraw(commandBuffer); }
        catch (Exception ex) { /* log + disable so the loop doesn't spam */ }
    }
}
```

Static flags (`Active`, `QuadInstance`) are toggled from your manager on the main thread; the postfix reads them on the same thread inside the render loop, so no synchronization is needed.

## Per-frame draw

```csharp
public unsafe void RecordDraw(CommandBuffer cmd)
{
    if (!TryComputeModelEgo(out float4x4 modelEgo)) return;  // see below
    var camera = Program.GetMainCamera();
    if (camera == null) return;

    // Row-vector convention (KSA matches .NET / DirectXMath): MVP = model * viewProjection.
    float4x4 mvp = modelEgo * camera.MVP.viewProjection;

    cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
    VkDescriptorSet setCopy = descriptorSet;
    cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, pipelineLayout, 0,
        new ReadOnlySpan<VkDescriptorSet>(ref setCopy),
        default(Span<ByteSize32>));

    // The offscreen pass uses a dynamic viewport set by Program.SetViewport.
    Program.SetViewport(cmd);

    cmd.PushConstants(pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);

    VkBuffer vbHandle = vb.VkBuffer;
    ByteSize64 vbOff = (ByteSize64)vb.BindOffset;
    cmd.BindVertexBuffers(0,
        new ReadOnlySpan<VkBuffer>(ref vbHandle),
        new ReadOnlySpan<ByteSize64>(ref vbOff));
    cmd.BindIndexBuffer(ib.VkBuffer, (ByteSize64)ib.BindOffset, VkIndexType.Uint16);
    cmd.DrawIndexed(6, 1, 0, 0, 0);
}
```

## Positioning the quad relative to a SubPart — ego space

KSA renders the scene in **ego space** (camera-centered local frame, to keep float32 precision near the camera). `camera.MVP.viewProjection` is built for ego-space verts, so the model matrix you push must also be in ego space.

Compose per frame:

```csharp
private bool TryComputeModelEgo(out float4x4 model)
{
    model = float4x4.Identity;
    var camera = Program.GetMainCamera();
    if (camera == null) return false;
    Vehicle? vehicle = Program.ControlledVehicle;
    if (vehicle == null) return false;
    Part? part = FindPart(vehicle, targetPartId);  // walk vehicle.Parts.Parts + p.SubParts
    if (part == null) return false;

    // Pull the SubPart's ego-space rotation + position SEPARATELY. Do NOT use
    // part.MatrixAsmb2Ego — that matrix bakes in the part's own scale, which
    // you almost always want to exclude so your width/height controls are the
    // sole source of quad size.
    double4x4 vehMat   = vehicle.GetMatrixAsmb2Ego(camera);
    double3 partPos    = part.PositionEgo(in vehMat);
    doubleQuat partRot = part.Asmb2Ego(vehicle.Asmb2Ego);

    float4x4 partRotMat   = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRot));
    float4x4 partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
    // Row-vector / left-to-right multiplication order: rotate first, then translate.
    float4x4 partEgo = partRotMat * partTransMat;

    // User-controlled offset + Euler in the SubPart's local frame.
    const float deg2rad = MathF.PI / 180f;
    float4x4 userRot = float4x4.CreateRotationX(rotX * deg2rad)
                     * float4x4.CreateRotationY(rotY * deg2rad)
                     * float4x4.CreateRotationZ(rotZ * deg2rad);
    float4x4 userTrans = float4x4.CreateTranslation(new float3(offX, offY, offZ));
    float4x4 scaleMat  = float4x4.CreateScale(widthMeters, heightMeters, 1f);

    // Chain (row-vector / left-to-right; transforms applied in this order to v):
    //   v_local → scale → userRot → userTrans → partEgo → ego
    model = scaleMat * userRot * userTrans * partEgo;
    return true;
}

private static Part? FindPart(Vehicle vehicle, string id)
{
    foreach (Part p in vehicle.Parts.Parts) {
        if (p.Id == id) return p;
        foreach (Part sub in p.SubParts)
            if (sub.Id == id) return sub;
    }
    return null;
}
```

Key facts:
- `vehicle.GetMatrixAsmb2Ego(camera)` builds the assembly→ego matrix for the current camera.
- `part.PositionEgo(in vehMat)` and `part.Asmb2Ego(vehicle.Asmb2Ego)` give pose **without** the part's `Scale` baked in.
- Row-vector convention: composing as `A * B` means "apply A first, then B" to a row vector `v`. `model * viewProjection` matches the rest of KSA.
- The full chain places a unit-square XY quad at the SubPart's origin, oriented along its axes, then your offset/rotation/scale modifies it in the SubPart's local frame.

## Ray-vs-quad picking (mouse / clicks)

`Cursor.InputRay` is built each frame by `Camera.ScreenToEgoRay` and is in **ego space**. The model matrix above is ego-space too, so they're directly compatible — no extra transform:

```csharp
public bool TryRaycast(Ray ray, out double t)
{
    t = double.MaxValue;
    if (!TryComputeModelEgo(out float4x4 m)) return false;

    // Same local-space corners as the vertex buffer.
    float3 v0L = new float3(-0.5f, -0.5f, 0f);
    float3 v1L = new float3( 0.5f, -0.5f, 0f);
    float3 v2L = new float3( 0.5f,  0.5f, 0f);
    float3 v3L = new float3(-0.5f,  0.5f, 0f);

    // float3.Transform(p, M) computes M * vec4(p,1) using KSA's mat-vector convention.
    double3 v0 = ToD(float3.Transform(v0L, m));
    double3 v1 = ToD(float3.Transform(v1L, m));
    double3 v2 = ToD(float3.Transform(v2L, m));
    double3 v3 = ToD(float3.Transform(v3L, m));

    bool any = false;
    if (ray.RaycastMollerTrumbore(v0, v1, v2, out double t0)) { t = t0; any = true; }
    if (ray.RaycastMollerTrumbore(v0, v2, v3, out double t1)) {
        if (!any || t1 < t) t = t1;
        any = true;
    }
    return any;
}
static double3 ToD(float3 v) => new double3(v.X, v.Y, v.Z);
```

If your raycast verts ever diverge from the rendered vertex buffer, picking will silently land off-target — keep them in sync.

## Texture format gotcha

`UnlitMesh.frag` calls `gammaToLinear()` on the sampled texel, expecting **gamma-encoded** bytes in the image:

- Use **`R8G8B8A8UNorm`** for the source texture so the GPU does no implicit sRGB decode on sample and the shader's single decode is the only one.
- **Do not use an SRGB format** (`R8G8B8A8Srgb`) for the source. The GPU auto-decodes on sample, then the shader decodes again, and the result is noticeably darker than expected.

## Lifecycle / threading

- Build all of this on the main thread, after `OnFullyLoaded` (the renderer must be live).
- Dispose in reverse construction order. `VkShaderModule` handles from `ModLibrary.Get<ShaderReference>` are owned by `ModLibrary` — **do not** destroy them yourself; only destroy the pipeline, pipeline layout, descriptor pool, descriptor set layout, vertex/index buffers, and (if you created one) the sampler.
- Wrap `RecordDraw` in try/catch and disable the feature on the first failure; a render-loop exception will otherwise spam logs every frame and may corrupt Vulkan state.
- For toggle-off: clear the static flag the Harmony postfix reads **before** disposing GPU resources, so an in-flight frame can't dereference freed handles.
