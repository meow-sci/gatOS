# GPU Materials, Shaders & Kitten Spawning

Tinting characters via the GPU material buffer, painting vehicle parts via runtime shader swaps, engine emissive control, and spawning KittenEva entities. All GPU work is **game-thread only**.

## Three separate render paths (know which affects what)

| Object | Shader | Material source | Color mechanism |
|---|---|---|---|
| Static vehicle parts | `MeshIndirect.vert/frag` | `PerDrawData` bindless texture indices | none native → padding hijack + shader patch |
| Dynamic parts (engines) | `DynamicMeshIndirect.vert/frag` | PerDrawData + Temperature LUT | Temperature/TFI per-instance (native) |
| Kitten characters | `ModelPbr.frag` | `GpuMaterialSystem` BigBuffer (`MaterialData`) | `AlbedoColor` multiply (native) |

**Vehicle parts never read `GpuMaterialSystem`** — that's why kitten `AlbedoColor` writes don't affect parts, and why parts need the shader-patch approach.

## GpuMaterialSystem / BigBuffer (kitten tinting)

The game packs all `MaterialData` structs sequentially into one big GPU storage buffer. Access it via reflection from `Program.Instance.MaterialSystem` (no hard reference; `Program.Instance` is null until fully loaded).

```csharp
var programType  = typeof(Part).Assembly.GetType("KSA.Program");
var program      = programType.GetProperty("Instance", BindingFlags.Public|BindingFlags.Static).GetValue(null);
var materialSystem = /* GetFieldOrProp(programType, program, "MaterialSystem") */;   // GpuMaterialSystem
```

Key members (most live on base `GpuObjectSystem<MaterialData>` / `AssetManager`, so **walk `BaseType`** with `DeclaredOnly`):

- `BufferEx BigBuffer { get; }` — `.VkBuffer` handle, `.BufferSize`. Holds all materials.
- `IVulkanContext DeviceCtx` — `.Device`, `.MainQueue`.
- `bool CreateObject(AssetName id, MaterialData element)` — allocates a slot and stages the struct. False if name exists.
- `AssetMap` — `ConcurrentDictionary<AssetName, GpuObjectAssetRef>`; each value's `int Handle` is the slot index.
- `GetOrLoad(AssetName)` → asset ref; `.Handle` is the BigBuffer index.

### MaterialData layout (decomp is authoritative; `[StructLayout(Sequential, Pack=1)]`)

```
0   int   AlbedoTexture
4   int   NormalTexture
8   int   RoughMetallicAOTexture
12  int   Sampler
16  float4 AlbedoColor              ← tint target
32  float4 RoughnessMetalScale
48  float4 ExtraData                ← fur uses (FurTex, FurSampler, FurMask, 0)
64  int   EmissiveTexture
68  int   Padding0/1/2
```

Always compute offsets with `Marshal.OffsetOf<MaterialData>(...)` / `ByteSize.Of<MaterialData>()` — robust to reordering. (The humble-arteest README's field order is wrong; trust the decomp.)

### Writing AlbedoColor — staged Vulkan upload

```csharp
int colorOffset = (int)Marshal.OffsetOf<MaterialData>(nameof(MaterialData.AlbedoColor));
ByteSize targetOffset = handle * ByteSize.Of<MaterialData>() + colorOffset;

using var stagingPool = deviceCtx.Device.CreateStagingPool(deviceCtx.MainQueue, 1);
var cmd = stagingPool.NextCommandBuffer();
float4 colorCopy = color;
var span = new Span<float4>(ref colorCopy);
cmd.Begin();
VkUtils.StageAndUploadToBuffer(stagingPool, bigBuffer.VkBuffer, targetOffset, span.AsBytes(), cmd);
cmd.End();
// StagingPool disposal flushes; no explicit submit/wait shown.
```

This mirrors the engine's own `CreateObject` upload. Reset = write `(1,1,1,1)`. **Alpha gotcha:** `ModelPbr.frag` does `if (alpha < 0.1) discard;` — `AlbedoColor.W < 0.1` makes the kitten invisible.

### Cloning a material per-kitten (doh)

Don't mutate the shared game material — `CreateObject` a new named material reusing the source's bindless texture handles but a custom `AlbedoColor`:

```csharp
var matData = new MaterialData {
    AlbedoTexture = albedoTex, NormalTexture = normalTex, RoughMetallicAOTexture = pbrTex,
    Sampler = samplerHandle, AlbedoColor = tintColor, RoughnessMetalScale = float4.One,
    EmissiveTexture = emissiveTex, ExtraData = float4.Zero
};
materialSystem.CreateObject((AssetName)$"doh_{id:D4}_m{idx}", matData);
int handle = materialSystem.GetOrLoad((AssetName)name).Handle;
```

Then repoint the kitten's renderables by overwriting their `int[] MaterialIndices` arrays (CharacterModel, Fur, Helmet, Visor, MMU each have one indexing into BigBuffer). Default texture/sampler handles come from `SuperMeshRenderSystem.TextureSystem` (`SamplerRepeatHandle`, `DefaultWhiteTexture.BindlessHandle`, …) and `GltfSystem.BlankMaterialTexture`. Fur globals live on `Program.CharacterRenderSystem._resources` (note fur sampler uses `BindlessIndex`, not `BindlessHandle`).

## Spawning a KittenEva (replicating EVADoor.CreateKittenEva)

```csharp
// 1. Backpack part (required ctor arg)
var partTemplate = /* ModLibrary.AllParts (internal).Find(KeyHash) */;
var part = new Part(partTemplate.Id, partTemplate);
part.Tree.ReinitializeDerivedValues();
var mix = SubstanceLibrary.TryGetCombustionProcess(KeyHash.Make("MMH_NTO_1.6".AsSpan()));
foreach (var tank in part.SubtreeModules.Get<Tank>()) tank.ConfigureFor(mix);
part.Tree.RefillConsumables();

// 2. Construct
var kittenEva = new KittenEva(
    Universe.CurrentSystem,   // CelestialSystem
    characterId,              // from ModLibrary.AllCharacters (internal).GetList()
    body2Cce,                 // doubleQuat orientation
    bodyRates,                // double3, NaN-guarded -> zero
    parent,                   // IParentBody
    uniqueName,               // must not collide in system.All
    backpackPart,
    referenceOrbit);

// 3. Orbit + teleport into place
var orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedSimTime(), posCci, velCci, orbitColor);
kittenEva.Teleport(orbit, null, null);

// 4. Register into the scene graph
parent.Children.Add(kittenEva);
kittenEva.UpdatePerFrameData();
```

`ModLibrary.AllParts` / `ModLibrary.AllCharacters` are **internal static** — reach via reflection. Despawn: `Universe.CurrentSystem.All.TryGet(id, out Astronomical)`; if a `Vehicle`, `system.Deregister(vehicle); vehicle.Dispose();`.

## Vehicle part painting — PerInstanceData padding hijack + shader swap

Static parts have no native tint. `PartModel.PerInstanceData` is 80 bytes with 3 unused trailing ints (bytes 68–79). A Harmony **prefix on `PartModel.AddInstance(ref PerInstanceData)`** reinterprets the struct and writes RGB into those bytes; a runtime-patched shader reads them.

```csharp
[StructLayout(LayoutKind.Sequential)]
private struct PaintablePerInstanceData {
    public float4x4 ModelMatrix;  // 0
    public int StateBitFlag;      // 64
    public float PaintR, PaintG, PaintB;  // 68/72/76 — were packing ints
}

static void AddInstancePrefix(PartModel __instance, ref PartModel.PerInstanceData instanceData) {
    if (!VehiclePaint.TryGetEffectiveColor(__instance, out var color)) return;
    ref var p = ref Unsafe.As<PartModel.PerInstanceData, PaintablePerInstanceData>(ref instanceData);
    p.PaintR = color.X; p.PaintG = color.Y; p.PaintB = color.Z;
}
```

### Runtime GLSL compile + swap

```csharp
var device = Program.GetRenderer().Device;
CompileAndSwapShader("MeshIndirectVert", ModifyVertexShader,   device);
CompileAndSwapShader("MeshIndirectFrag", ModifyFragmentShader, device);
PartModelRenderer.ColorData.Rebuild();   // REQUIRED — recreates the Vulkan pipelines
```

`CompileAndSwapShader` steps:
1. `ModLibrary.Get<ShaderReference>(id)` (ids `"MeshIndirectVert"`/`"MeshIndirectFrag"`).
2. Resolve on-disk path (`ShaderReference.ModPath`/`LocalPath`), read original GLSL.
3. **String-replace** edits in memory (abort if expected strings not found — your post-game-update breakage detector).
4. Write a temp file **in the same directory** (so `#include` resolves), UTF-8 no-BOM.
5. Compile via reflection: `RenderCore.ShaderModuleUtils.FromFile(Device, string path, out VkShaderStageFlags, CompileOptions?)` → `VkShaderModule` (reflection avoids a hard `Brutal.ShaderCompiler` dep).
6. Swap onto `ShaderReference.Shader` (private setter / `<Shader>k__BackingField`).
7. `device.DestroyShaderModule(oldModule, null)` **after** the swap (capture old first).
8. `finally` delete the temp file.

The GLSL edits add `PaintR/G/B` to the vertex `InstanceData` struct, pass them through as `out`/`in` locations 6/7/8, and in the fragment shader multiply the sampled color by the paint tint when non-zero (white = no change).

**Restore:** call `ShaderReference.DoLoad()` (nonpublic) on both refs to recompile from untouched game files, then `PartModelRenderer.ColorData.Rebuild()` again. The mod never edits real game files.

## Engine emissive — Temperature/TFI override

Dynamic parts already wire `Temperature` into `DynamicMeshIndirect.frag` (samples an emissive LUT) — **no shader edit needed**, just override the field via a prefix on `PartModelDynamic.AddInstance`:

```csharp
[StructLayout(LayoutKind.Sequential)]
private struct WritablePerInstanceData {
    public float4x4 ModelMatrix;  // 0
    public int StateBitFlag;      // 64
    public float Temperature;     // 68 ← override
    public float TfiThickness;    // 72 ← override (thin-film interference)
    public int packing1;          // 76
}
static void AddInstancePrefix(PartModelDynamic __instance, ref PartModelDynamic.PerInstanceData inInstanceData) {
    if (!EngineEmissive.TryGetEffective(__instance, out var temp, out var tfi)) return;
    ref var w = ref Unsafe.As<PartModelDynamic.PerInstanceData, WritablePerInstanceData>(ref inInstanceData);
    w.Temperature = temp; w.TfiThickness = tfi;
}
```

**Static vs dynamic layouts differ** (static reuses 68–79 as 3 free ints for paint; dynamic uses 68=Temperature, 72=TfiThickness). They are NOT interchangeable. Per-engine targeting: walk parts, `part.Modules.Get<PartModelDynamicModule>()`, read `module.PartModelDynamic`; dedup with `HashSet<PartModelDynamic>(ReferenceEqualityComparer.Instance)`.

## Lifecycle / threading gotchas

- **Game-thread only** for every material write / spawn. Off-thread (RPC) callers marshal via `GameThread.Scheduler.Schedule(...)`.
- **`Program.Instance` is null** before the game finishes loading — guard every accessor.
- **Reflection must walk `BaseType`** for `GpuMaterialSystem` members (BigBuffer/DeviceCtx/CreateObject/GetOrLoad live on base classes).
- **Destroy old shader module AFTER swap**, not before.
- **`PartModelRenderer.ColorData.Rebuild()`** is mandatory after any shader-module swap or the new module is ignored.
- **Cleanup on unload** before unpatching Harmony: deactivate shaders (restore vanilla pipelines), reset material colors. Kitten color uses no Harmony patches (pure GPU buffer writes).
- Don't destroy `VkShaderModule`s owned by `ModLibrary` (those from `Get<ShaderReference>`) — only ones you compiled.
