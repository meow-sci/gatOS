# STICKERS_PLAN.md — `/sim/paint/stickers`: spray-paint decals from user PNGs onto vehicles, terrain and ground clutter

> **STATUS (2026-08-22): IMPLEMENTED — code complete, in-game validation pending.** S0–S5 are
> built: the game-free store/grammar/tree, the game-side registry, the bindless image binder, the
> decal renderer and its lazy Harmony patch, the MCP tool and the doc lockstep. Research base
> verified against KSA `2026.8.19.5261` decomp (`../ksa-game-assemblies/current/decomp`) + shipped
> GLSL (`../ksa-game-assemblies/current/Content/Core/Shaders`); every `[KsaAnchor]` carries that
> baseline. **S6 — the live KSA pass — has not been run**: nothing below has been seen drawing in a
> real session. The checklist is `docs/VALIDATION.md` → *Stickers live KSA checklist*. Deltas
> between this plan and what was actually built are tabulated at the end of this document, and the
> code is the source of truth wherever they disagree.
>
> **Prerequisite landed:** commit `12dfa43` *feat(paint): custom PNG overrides for ground-clutter
> textures* (`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`, code complete, live validation pending).
> It ships the user-image store (`/sim/paint/textures/file/`), the decode → `SimpleVkTexture` →
> retire-ring GPU path, the HTTP upload routes, the `gatos.paint_texture` MCP tool and the
> `paint_texture_*` config. Stickers are the **second consumer** of that store and GPU path and add
> **no** second upload surface. §0 lists exactly what is reused, what must be refactored out of the
> clutter bridge first, and the two small store additions stickers need.

## Goal

The "spray a logo on the world" feature from games with spray tags: a userland program uploads a
PNG, then *sprays* it where the camera (or cursor) is pointing, or *places* it by coordinates. The
sticker stays put in the world — on the rocket as it flies, on the ground as the planet rotates —
until removed. Many stickers, many textures, all ad-hoc, all cleanable, and **nothing runs at all
while no sticker exists**.

```sh
# upload — the SAME user-texture store the clutter overrides use; bytes held in-memory mod-side
cat meow.png > /sim/paint/textures/file/meow.png
cat /sim/paint/textures/files                          # meow.png 48213 png 512x512 v1 ready

# spray it where the camera is looking (the /sim/camera feature can aim for you)
echo 'meow.png'            > /sim/paint/stickers/spray
echo 'meow.png w=2 h=2'    > /sim/paint/stickers/spray        # 2 m sticker
cat /sim/paint/stickers/last                                  # 3 vessel Kitten-1 part 1187 hit 4.1m

# place by coordinates — scripted, deterministic, MCP-friendly
echo 'meow.png body Mun 12.03 -41.88 heading=90 w=5 h=5' > /sim/paint/stickers/place
echo 'meow.png vessel Kitten-1 1187 0 0.52 -1.4 0 1 0 roll=15 w=0.6 h=0.3' > /sim/paint/stickers/place

# tune / inspect / remove
echo '0.5'  > /sim/paint/stickers/3/alpha
echo '1 0.5'> /sim/paint/stickers/3/size
cat /sim/paint/stickers/3/spec                                # write-compatible line (echo to place)
echo 1      > /sim/paint/stickers/3/remove
echo 1      > /sim/paint/stickers/clear
rm /sim/paint/textures/file/meow.png                          # evict the image (stickers using it go dormant)
```

HTTP `/v1/fs/paint/stickers/...` field mirrors and MQTT `gatos/sim/paint/stickers/...` come free
from the VFS walk; the binary upload is the existing `PUT/POST/DELETE /v1/paint/texture/file/<name>`
and MCP `gatos.paint_texture`; stickers add one tool, `gatos.paint_sticker`, and a `stickers`
section in `gatos://runtime/paint`.

Session-only by default (like every other gatOS world mutation); see §9 for persistence.

---

## 0. What `12dfa43` landed, and what it changes for this plan

Verified against the working tree at `HEAD` (`gatOS.SimFs/Paint/`, `Game/Ksa/Paint/ClutterTextureBridge.cs`,
`PaintManager.cs`, `Mod.cs`, `SimFsTree.cs`, SPEC §"Textures", `McpRegistry`/`McpPresenters`):

| Landed | Where | Stickers… |
|---|---|---|
| `TextureStore` — uploads keyed by name with `Version`, `SniffKind` (png/jpeg/bmp/hdr/dds/ktx/ktx2), caps (`MaxFileBytes`/`MaxTotalBytes`/`MaxFiles`/`MaxBindings`/`MaxDimension`), `TryGet(name) → TextureLookup.{Ready,Uploading,Missing}` + `TextureFile(Name, Bytes, Kind, Version)`, `HttpUpload`, `Delete` (drops clutter bindings for that file), `Clear`, `FileCount`/`Usage`, volatile `Catalog`/`Applied`/`Runtime` publish slots | `gatOS.SimFs/Paint/TextureStore.cs:188-652` | **reuse as-is** for images. Two additions (§3.4): `CurrentVersion(name)` (alloc-free) and `ContentRevision` (bumps on *any* commit/delete/clear — today's `Revision` bumps only for *bound* files, `TextureUpload.Commit → BumpIfBoundLocked`, `:851-868`). |
| `TextureDirectory`/`TextureFileNode` (`IsStreaming => true`), `/sim/paint/textures/{file/,files,status,info,help,bindings,applied,clutter,bind,unbind,clear}` as a flat sibling set under `PaintDir()` | `TextureDirectory.cs`, `SimFsTree.cs:267-285` (`PaintDir`), `:506` (`TexturePaintRootDir`) | **reuse**; `/sim/paint/stickers/` is a new sibling added in `PaintDir()` when `_textures is not null` and the stickers gate is on |
| `ClutterTextureBridge.Apply` — the decode/upload idiom: `TextureLoader.LoadFromMemory(bytes, kind, TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32))` → optional `MakeFaithful` → `new TextureAsset(decoded, "gatos:paint/textures/<name>")` → `using` staging pool → `new SimpleVkTexture(Allocator, pool, asset, new CreateOptions(maxDim, Downsample, fillMipChain: true))` → `pool.Submit().Wait()` → `DestroyDecoded`; plus `RetireImage`/`DrainRetired` (`MaxFramesInFlight + 1` ticks) and the unload drain | `ClutterTextureBridge.cs:225-283, 307-336, 519` | **factor out** into `Game/Ksa/Paint/UserTextureGpu.cs` (decode+upload, retire queue) in S0 so the bridge and the sticker binder share one implementation. **Stickers must not apply `MakeFaithful`** — that correction cancels `Solid.frag`'s ×2/tint semantics and is meaningless for our shader, which decodes sRGB itself. |
| `PaintManager` owns the bridge: routes `paint.texture_*` before any vessel resolution, calls `_textures?.Tick()` from its own `Tick()` (run by `Mod.DrivePaint` in `DrivePerFrame`, `Mod.cs:454`), disposes it in `Dispose()` (called by `TeardownGameCheats`) | `PaintManager.cs:106-110, 326-328`; `Mod.Game.cs` `EnsureControlObjects` constructs the bridge from `_textureStore` | **same ownership for `StickerManager`** — no new `Mod` partial, no new `KsaCatalog` parameter; `DrivePaint` already runs before the frame renders, which is the ordering the anchor re-resolution needs |
| `paint_textures_enabled` (boot-time wiring, no runtime master), `paint_texture_max_{bytes,total_bytes,files,bindings,dimension}` | `GatOsConfig.cs`, `gatos.default.toml:170-185` | **reuse** image caps; add `paint_stickers_enabled`, `paint_stickers_max_count`, `paint_stickers_max_view_distance_m` to the same PAINT block |
| `/v1/paint/texture/files`, `PUT|POST|DELETE /v1/paint/texture/file/{name}` with **413 EFBIG** on oversize `Content-Length` (`/v1/paint/textures/…` alias); `TextureHttpRoutesTests` | `SimHttpServer.cs`, `HttpRequestLine.cs` | **no HTTP work** for stickers; every sticker leaf is a field mirror |
| MCP: `gatos.paint_texture` store tool (list/catalog/bindings/retrieve/upload/delete, `EmbeddedResourceBlock` at `gatos://paint/textures/<name>`), three `texture_*` ops on `gatos.paint_control` (`file` param → `Aux`, `value` → mode), runtime feature doc `paint_textures` (`gatos://runtime/paint_textures`), `features.paint_textures` in capabilities, tool count 27 | `McpRegistry.cs`, `McpToolHandlers.cs:149`, `McpPresenters.cs:150,181` | stickers add **one** tool `gatos.paint_sticker` (the placement grammar is too wide for `paint_control`'s scalar slots), a `paint_stickers` runtime feature doc, `features.paint_stickers`; tool count 27 → 28 |
| Gate string `control_enabled + paint textures store`; health latches `paint.clutter_catalog`, `paint.texture_upload`; `KsaAnchor` ×2 (`Risk=High` upload path, `Medium` catalog) | `CommandCatalog.cs:265-267`, `ClutterTextureBridge.cs:38-41` | mirror: gate `control_enabled + paint stickers`, latches `paint.sticker_renderer` / `paint.sticker_texture`, anchors in §5 |
| SPEC: top-level `## Textures (/sim/paint/textures)` section (`SPEC_9P_FILESYSTEM.md:1810`), action rows at `:1523-1525`, config row `:147`, errno rows `:111-119`; `SPEC_MCP.md` §1.1/§1.2/§3/§5/§5.1/§6/§6.1/§7; `docs/VALIDATION.md` "Custom clutter textures live KSA checklist" | — | add a sibling `## Stickers (/sim/paint/stickers)` SPEC section and the matching rows; a stickers card in VALIDATION |
| **Unvalidated risk carried over:** the discrete `stagingPool.Submit().Wait()` upload is item #1 of the clutter checklist | plan §Risks #1 | stickers use the identical call through the shared helper — validating it once (on the clutter feature, which needs no sticker code) clears it for both; do that before S2 if a live session is available |

Scope note: `12dfa43` covers **ground clutter** textures only; terrain biome/cubemap textures remain
out of scope there and are unrelated to stickers (a sticker on terrain is a projected decal, not a
texture override).

---

## 1. Research base (verified 2026-08-22, file:line against `current/decomp` and `current/Content`)

### 1.1 The frame, and the one window where a decal can see scene depth

`Program.RenderGame` (`KSA/Program.cs:4206-4445`) renders the main viewport as: shadow/prepass
work → `_offscreenTarget.ClearDepthImages` (`:4294`) → part depth copied in from the opaque prepass
(`PrePassRenderer.CopyDepthImageToSrc`, `:4304`) → **ground-clutter depth pre-pass** into the same
depth attachment (`:4305-4307`) → **one dynamic-rendering scope** `_offscreenTarget.BeginRendering`
(`:4308`, depth `LoadOp.Load`) containing parts (`PartModelRenderer.ColorData.WriteCommands`, `:4342`),
**terrain + clutter draw** (`_planetRenderer.Render`, `:4344-4348`, clutter at
`KSA/PlanetRenderer.cs:1922`), then `SuperMeshRenderSystem.RenderMainPass` (`:4350` — the thug_life
seam) → `EndRendering` (`:4357`, MSAA depth resolves with `VkResolveModeFlags.MaxBit`,
`KSA.Rendering/RenderTarget.cs:101`) → particles, ocean, atmosphere/clouds, exhaust, bloom, glass,
`RenderTranslucencyPass`, orbit lines, gizmos, each in its own scope → **`RenderedViewport.OffscreenTarget.ResolveAttachments(cb)`
(`:4418`)** → underwater → `GridPass.Run` (`:4424-4427`, gated on `GridFlag && DrawUI && ShowMapGrid()`)
→ gauges → colour to `SampledReadVfc` (`:4436`) → screenshot → CMAA2 → final composite.

Facts that decide the design:

- **Inside the opaque scope the depth attachment is being written, not sampleable.** There is no
  depth copy of the full opaque scene (parts + terrain + clutter) until the scope ends.
- **After `ResolveAttachments` (`:4418`) the resolved single-sample `RenderTarget.DepthImage`
  (`KSA.Rendering/RenderTarget.cs:38`, usage `SampledBit | DepthStencilAttachmentBit`, `:120-136`)
  and `ColorImage` are both current and free.** `GridPass.Run` (`KSA/GridPass.cs:427-470`) is the
  stock template for drawing there: a `BarrierBatch` putting `DepthImage` in
  `ImageBarrierInfo.Presets.DepthSampledReadF` and `ColorImage` in `ColorAttachmentReadWrite`, then
  its own `VkRenderingInfo` on `ColorImage` (`ColorAttachmentOptimal`, `ResolveMode.None`,
  `Load/Store`, no depth attachment), `Program.SetViewport`, set 0 = `GlobalShaderBindings.DescriptorSet`
  with `GlobalShaderBindings.DynamicOffset(viewport.Index)`, set 1 = a `CombinedImageSampler` of
  `DepthImage` with `Program.PointClampedSampler` (`:130-134`), 1-sample pipeline (`:474-476`).
- The reverse-Z linearisation is in `Content/Core/Shaders/Grid.frag:63-77`:
  `sceneDistance = (near*far) / (depth*(far-near) + near)`, with `depth <= 0` meaning "nothing drawn";
  camera constants come from `global.camera.{nearPlane,farPlane,inverseProjection,inverseView}`
  (`Common/Global.glsl:12-32`). The lighting UBO in the same set exposes `global.lighting.sunPosition`
  (xyz + radius), `sunColor`, `planetPosition`, `planetColor`, `nearestCelestialColor`
  (`Global.glsl:35-46`).
- `ResolveAttachments` (`RenderTarget.cs:315-343`) is **called unconditionally** at `:4418` (and at
  `:4174` for secondary viewports); its body is MSAA-gated but a Harmony postfix fires either way.
  It is an instance method, so `__instance` identifies the target: the main viewport's is literally
  `Program._offscreenTarget` (`Program.OffscreenTarget`, `Program.cs:432`).
- Ego space = ecliptic axes, camera at the origin; `Camera.MVP.view` is rotation-only
  (`KSA/Camera.cs:482-492`), so every position handed to the GPU is `double3 → float3` after
  subtracting the camera (`Camera.EclToEgo`, `:224`; `Camera.GetPositionEgo(IPosition)`, `:231`).
- Depth bias is **not** a shared dynamic state (`Core/Renderer.cs:61-65` is `{Viewport, Scissor}`
  only) — irrelevant for a projected decal (no depth attachment), fatal for a flat-quad design.

### 1.2 Ground clutter cannot be addressed from the CPU — but it is in the depth buffer

Clutter placement is 100 % GPU: `Generate.comp` (`Content/Core/Shaders/Planet/GroundClutter/`) writes
`ClutterCubeCellGrid.ObjectData` into a device-local buffer (`KSA/ClutterCubeCellGrid.cs:40,199`),
`PrepareInstances.comp` turns it into ego transforms (`:209`), both without transfer-src usage. The only
CPU-visible positional data is the per-cell anchor (`ClutterCubeCellGrid.UpdateCellAnchors`, `:927`).
The readback path (`KSA.Terrain.Physics/ClutterEcotypePhysicalData.cs:903 ReadbackGroundClutter`) is
complete but **never constructed** in this build (no `new ClutterEcotypePhysicalData` anywhere).
Clutter meshes *are* CPU-resident (`MeshReference.PositionsCompare`) but without instance transforms
that is useless.

**Consequence:** no flat-quad or mesh-clipping approach can ever put a sticker on a rock. A
depth-buffer-projected decal does it for free, because the clutter pre-pass and draw both write the
scene depth the decal reconstructs from. This single fact picks the architecture.

### 1.3 Vehicle parts: CPU meshes, a mesh-precise raycast, and the pose chain

- Every part mesh keeps its CPU data: `MeshReference.PositionsCompare` (de-indexed `double3[]`
  triangle soup, part-local metres, `KSA/MeshReference.cs:23,49`), `HostPrimitives` (indexed
  `MeshAsset` with Position/Normal/Uv0, `:40`), `BoundingSphereRadius` (`:20`);
  `DeviceMeshInterleaved.HostMesh` (`KSA/DeviceMeshInterleaved.cs:105`) holds it forever. gatOS's
  `Game/Ksa/Iva/InteriorGeometry.cs:106-125` already consumes exactly this.
- **`Part.RayCastEgo`** (`KSA/Part.cs:1884`) → `RayCastEgoSubPart` (`:1918-1952`): broad-phase sphere,
  then `Ray.RaycastWatertight` over the `_VM` view mesh (`MeshViewModule.MeshView`), returning
  `nearIntersectionPositionAsmb` **already in part-local space** and a (flat, first-vertex) normal,
  plus `closestSubPart`. This is what flight-mode hover picking uses (`KSA/Vehicle.cs:2745-2773`,
  `CameraMode.Orbit` only). The ray comes from `Camera.ScreenToEgoRay(float2)` (`KSA/Camera.cs:688`)
  or `Cursor.InputRay` (`KSA/Cursor.cs:25`). Bepu raycasts exist but KSA never uses them and
  colliders are coarse primitives (`KSA/ColliderModule.cs:14-19`) — not the art surface.
- Pose: `Vehicle.GetMatrixAsmb2Ego(Camera)` (`KSA/Vehicle.cs:1202`) then
  `Part.MatrixAsmb2Ego(in vehMat)` (`KSA/Part.cs:1041`, **includes `Scale`** — wanted for a decal
  stuck to the surface, unlike thug_life's split form which excludes it). Parts are re-resolved per
  frame by `Part.InstanceId` (`ThugLifeManager.FindPart`).
- Local bounds for sanity checks: `MeshAsset.PositionMinimum/PositionMaximum`
  (`RenderCore/MeshAsset.cs`), not `Part.Radius` (origin-to-corner distance, `Part.cs:1068-1083`).

### 1.4 Terrain: CPU height field, body-fixed frame, no mouse-vs-terrain raycast

- `Celestial.GetTerrainHeightFromDirCcf(double3 dirCcf, bool accurate = true)`
  (`KSA/Celestial.cs:792`) — metres above `MeanRadius`, `0` for heightmap-less bodies;
  `accurate:false` is 4 texel taps (the physics hot path, `KSA/TerrainImpactFinder.cs:64`),
  `accurate:true` adds bicubic + the CPU procedural-modifier chain (float precision). The GPU adds
  tessellation displacement the CPU never sees (`PlanetTessEvaluation.tese:153-155`) and streams
  height mips, so expect decimetre disagreement near the camera — the game's own terrain debug
  lines lift by `+0.1 m` (`KSA/Vehicle.cs:4511-4523`). A projected decal with a ±0.5–1 m box depth
  absorbs this entirely; a flat quad would not.
- Frames: `GetDirCcfFromLatLon` (`:670`), `GetCcf2Cce()` (`:534`), ENU via
  `Vehicle.ComputeEnu2Cce(surfaceCce, body.GetCci2Cce())` — all already wrapped by gatOS in
  `Game/Ksa/Camera/CameraFrames.cs:223 GeoToEcl`, `:245 TryEclToGeo`, `:269 TryEnu` and
  `Game/Ksa/Camera/CameraTargets.cs:75 TryResolve` (body lookup = `Universe.CurrentSystem?.Get(id)`).
- **No CPU ray-vs-terrain exists** (every `Ray` consumer is a part/gizmo/sphere). The closest thing is
  `TerrainImpactFinder.TryFind` (coarse march + 24 bisections over `GetTerrainHeightFromDirCcf`) driven
  by an `ITerrainTrajectory` (`KSA/ITerrainTrajectory.cs`, one member `PositionCcf(UniverseTime)`) — a
  straight-line implementation gives an exact terrain hit in ~30 height samples.
- A landed vessel is pinned by a bubble frame switch to CCF (`KSA/BubbleFrame.cs`,
  `PhysicsStates.UpdateFromSurface`, `KSA/PhysicsStates.cs:662`); the invariant is a CCF position. **A
  ground sticker must store `(body, dirCcf/lat-lon, heading)` and recompose per frame** — never an
  ECL/ego position.

### 1.5 PNG decode, textures, bindless, shader compile — all in-engine, no new dependencies

- **Decode:** `Brutal.TextureApi.TextureLoader.LoadFromMemory(ReadOnlySpan<byte>, FormatType,
  params TextureLoadSettings?[])` (`Brutal.TextureApi/TextureLoader.cs:130`, `FormatType` = Bmp/Dds/
  Hdr/Jpg/Kmg/Ktx/Ktx2/Png/Tga) wrapped by `RenderCore.TextureAsset` — the exact path
  `TextureReference.DoLoad` uses for the game's own textures (`KSA/TextureReference.cs:84-108`) and
  the one the in-flight `ClutterTextureBridge` already calls. Assembly `Brutal.Texture.dll`, **already
  referenced by the in-flight csproj change**. ⚠️ `TextureAsset.Dispose()` is an empty body
  (`RenderCore/TextureAsset.cs:391`) — free the decoded pixels with `TextureLoader.Unload(asset.Texture)`.
  (The raw `Brutal.StbApi.Stb.LoadFromMemory(bytes, 4)` → `StbImage8` route in `Brutal.Stb.dll` is
  the smaller alternative; not needed.)
- **Texture:** `SimpleVkTexture(IImageAllocator, StagingPool, TextureAsset, CreateOptions)`
  (`RenderCore/SimpleVkTexture.cs:245`) with `new CreateOptions(maxDim, ReductionMethod.Downsample,
  fillMipChain: true)` builds the image, uploads level 0 and **generates the mip chain**
  (`VkUtils.GenerateMipmaps` blit chain, `RenderCore/VkUtils.cs:289`), leaving it in
  `ShaderReadOnlyOptimal`. (The raw ctor at `:170` + `UploadData :374` is the hand-rolled
  equivalent `ThugLifeTextureFactory` uses.) **UNorm, never Srgb** — the shader decodes gamma
  itself (`.agents/skills/ksa/quad.md`).
- **Bindless:** `Program.Instance.BindlessTextures` (`KSA/Program.cs:89`, 1024 slots, `:774`):
  `int AddTexture(VkImageView)` / `void FreeTexture(int)` / `SetTexture`
  (`RenderCore.Systems/BindlessTextureLibrary.cs:155,198,178`), layout
  `UpdateAfterBind | PartiallyBound` (`:95-99`), sampler slot **0 = linear-clamped with
  `MaxLod = 1000`** (`:127-130`) — exactly the sampler a mip-mapped clamp-to-edge sticker wants.
  GLSL side `Common/TextureSet.glsl`: `globalTextures[]` at `(set = SET_TEXTURE, binding 0)`,
  `samplers[]` at binding 1, `SAMPLE_TEXTURE(texId, samplerId, uv)`. Descriptor-indexing features
  are enabled device-wide (`Core/KSADeviceContextEx.cs:296-303`).
- **Shader compile:** `ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte> source,
  VkShaderStageFlags, CompileOptions? = null, ReadOnlySpan<byte> debugName)`
  (`RenderCore/ShaderModuleUtils.cs:77`); null options = the engine's defaults (Vulkan 1.3, SPIR-V
  1.6, include callbacks). Errors throw `Brutal.ShaderCApi.ShaderException` subclasses with the
  shaderc log as `Message`. **`#include` resolves relative to the directory of `debugName`**
  (`Brutal.ShaderCApi/ShaderC.cs:253`), which is why gatOS paint passes the real file path with a
  trailing NUL (`Game/Ksa/Paint/PartPaintPatches.cs:56-59`). A module we compile is **ours to
  destroy**; `ModLibrary` modules are not.
- The stock `UnlitMesh.frag` hard-writes `alpha = 1.0` (`Content/Core/Shaders/Mesh/UnlitMesh.frag:16`)
  — the reason thug_life does cut-out-by-geometry, and the reason stickers need their own shader.
- **No deferred-destroy helper exists** in KSA (no `DeferDispose`, no retire queue); the engine
  relies on `Device.WaitIdle` at rebuild points. gatOS's precedents: `FrameCapture`'s
  `ResourceFrameIndex`-keyed ring (`Game/Ksa/FrameCapture.cs:40-46`) and the
  `GraphicsAndCompute.WaitIdle()` drain at unload (`Game/Mod.Game.cs:792-821`).
  `Renderer.MaxFramesInFlight = 2` (`Core/Renderer.cs:27`).

### 1.6 gatOS paved road (copy, don't invent)

| Need | Precedent |
|---|---|
| Image upload dir + caps + versioning + container sniff + HTTP chunked upload + MCP upload | **landed in `12dfa43`**: `gatOS.SimFs/Paint/TextureStore.cs` (`IsValidName :257`, `SniffKind :272`, `TextureFile(Name, Bytes, Kind, Version) :58`, `TryGet :402`, `OpenUpload :430`, `Delete :447`, `HttpUpload :587`, `Revision :240` — binding-scoped), `TextureDirectory.cs` (`IsStreaming => true`), `/sim/paint/textures/*` (`SimFsTree.cs:506`), `/v1/paint/texture(s)/…` (`SimHttpServer.cs`), `gatos.paint_texture` (`McpToolHandlers.cs:149`) |
| Game-side decode → `SimpleVkTexture` (mips) + `(name, version)` keying + retire ring | **landed in `12dfa43`**: `Game/Ksa/Paint/ClutterTextureBridge.cs` `Apply :225-283` (the `[KsaAnchor]` at `:211-224` records the three non-obvious contracts: `TextureAsset.FilePath` non-empty, `LoadOptions` forces 4 channels, the decoded `ITexture` must be `Destroy()`ed), `RetireImage/DrainRetired :307-336`, `Override(... Version ...)` keying `:67`; evict-on-version-change model = `Game/Ksa/Actuators/AudioActuator.cs:332 ReleaseEvictedSounds` |
| Registry + lazy dynamic Harmony + lazy GPU + teardown-on-last + `Update()` self-gate + `Snapshot()` | `Game/Ksa/ThugLife/ThugLifeManager.cs` (`EnsureGpu :260`, `EnsurePatch :283`, `Teardown :299`, `Update :132`, `RecordDraws :168`), `ThugLifeRenderPatches.cs` |
| Per-frame driver slot + dead-latch + teardown | `PaintManager.Tick()` / `Dispose()` (`PaintManager.cs:106-110, 326-328`, run by `Mod.DrivePaint` at `Mod.cs:454` and by `TeardownGameCheats`) — the landed owner of the clutter bridge; the self-gate + `_dead` latch idiom is `Mod.Game.cs:615-638 UpdateThugLife` |
| `/sim` tree helpers | `SimFsTree.cs` `ThugLifeDir :1813`, `ThugLifeEntryDir :1916`, `ParseThugLifeAdd :2008`, `AudioDir :662`; `LineControlFile`, `FlagControl`, `VectorControl`, `RangedControl`, `TriggerFile`, `LiveLine` |
| Routing + actions + catalog | `Game/Ksa/KsaCatalog.cs:53-55,247-296`, `gatOS.SimFs/Commands/SimActions.cs:65-72`, `CommandCatalog.cs:26-57,177-194` |
| Geodetic + body/vessel/part resolution | `Game/Ksa/Camera/CameraFrames.cs`, `CameraTargets.cs` |
| MCP two-tool shape + runtime resource + gate | `gatOS.Mcp/McpRegistry.cs:58-59`, `McpToolHandlers.cs:87-93,222-261`, `McpPresenters.cs:130-152,312-320` |
| Config block | `GatOsConfig.cs:371-386,123-131,712-716`, `Configuration/gatos.default.toml:169-185` (flat `audio_*` keys) |
| Tests to mirror | `gatOS.SimFs.Tests/Commands/ThugLifeTreeTests.cs`, `SimFsTreeTests.ControlEnabledTree_ExposesEveryModuleControlStatusAndDebugPath`, `gatOS.Mcp.Tests/McpPresenterTests.cs:12` (hard tool count = 26) |

---

## 2. Architecture: options and decision

| | A. Flat quads in the opaque scope (thug_life + alpha shader) | B. CPU mesh-conforming decals in the opaque scope | **C. Screen-space projected decals after resolve ✅** | D. Extend `MeshIndirect.frag` per instance |
|---|---|---|---|---|
| Vehicles | z-fights/clips on curved parts; needs depth bias we don't have dynamically | excellent (clip `PositionsCompare` against the box) | conforms to any surface | excellent, lit by the real PBR |
| Terrain | clips into bumps; CPU/GPU height mismatch | height-sampled patch; LOD/tessellation mismatch remains | conforms incl. tessellation displacement | n/a |
| Ground clutter | **impossible** (§1.2) | **impossible** | **works** (it's in depth) | n/a |
| Lighting / compositing | unlit unless we light it; MSAA; under atmosphere/plumes | lit by our shader (per-vertex normals); MSAA; under atmosphere/plumes | post-lit: our own sun term; single-sample; draws over translucents in front; no aerial perspective | perfect |
| Code | smallest (exists) | clipping code + per-anchor geometry + two spaces | one box mesh, one shader, depth reconstruct | all 8 descriptor sets taken (`KSA/PartModelRenderer.cs:91-100`), new binding on set 3, transpile `WriteInstancesToGpu`, compose with paint's anchor — deepest coupling in gatOS |
| Per-viewport | main + crew cams | main + crew cams | main only (secondary viewports have no terrain/clutter anyway) | all |

**Decision: C.** It is the only mechanism that hits all three targets the feature is for, it needs no
per-target geometry at all (placement is just a hit point + normal), it is immune to terrain LOD,
streaming and tessellation drift, and it is the textbook way spray decals are done. Its costs —
approximate lighting, drawing after translucents, main-viewport-only — are cosmetic at sticker
scale and are listed as v2 upgrades (§10). B is the v2 "premium vehicle decal" if anyone ever wants
MSAA-perfect, fully-lit logos on a rocket in the crew cam.

Why not reuse thug_life's `RenderMainPass` seam: scene depth is not sampleable inside that scope, so
a projected decal cannot be drawn there. The two features stay separate (`gatos.thug_life` vs
`gatos.stickers` Harmony ids, separate pipelines); nothing about thug_life changes.

---

## 3. Design

### 3.1 Ownership boundary (the dependency rule, §AGENTS)

| Layer | Owns |
|---|---|
| `gatOS.SimFs/Paint/TextureStore.cs` (landed) | the images: bytes, caps, versions, container sniff, upload handles — **shared with clutter overrides**; gains `CurrentVersion(name)` + `ContentRevision` (§3.4) |
| `gatOS.SimFs/Paint/Stickers/` (game-free, new) | `StickerStore` (the desired-state sticker table + the volatile published `StickerStatus`), `StickerRules` (number/arity validation, placement grammar), `StickerCommands` (the `place`/`spray` line parsers → `SimCommand`), `StickerSpec` formatter |
| `gatOS.SimFs/SimFsTree.cs` | the `/sim/paint/stickers` tree |
| `gatOS.Http/` | nothing new — uploads are `/v1/paint/texture/file/<name>` (landed), controls are field mirrors |
| `gatOS.Mcp/` | `gatos.paint_sticker` tool, `paint_stickers` runtime feature document (`gatos://runtime/paint_stickers`, the `paint_textures` precedent), `features.paint_stickers` |
| `gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs` (factored out of `ClutterTextureBridge` in S0) | `Upload(renderer, TextureFile, maxDim, faithful: bool) → SimpleVkTexture` (decode → `TextureAsset` → staging pool → `SimpleVkTexture` with mips → destroy decoded) and `RetireQueue { Retire(image); Drain(); DrainAll() }`; the bridge keeps only its `SetTexture` pristine-capture logic |
| `gatOS.GameMod/Game/Ksa/Paint/Stickers/` — **the only sticker-specific KSA-aware code** | `StickerManager` (registry + lifecycle; a `ThugLifeManager` port, owned and ticked by `PaintManager` exactly like the bridge), `StickerTextureBinder` (image → bindless handle via `AddTexture`/`FreeTexture`, on top of `UserTextureGpu`), `StickerDecalRenderer` (pipeline, shader, box mesh, per-frame depth descriptor ring, `RecordPass`), `StickerRenderPatches` (the dynamic postfix), `StickerAnchors` (ego composition for the two anchor kinds), `StickerPicker` (ray → vehicle/terrain hit) |

### 3.2 The render hook — installed only while ≥ 1 sticker is live

```
Harmony("gatos.stickers") postfix on KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer)
  if (!StickerManager.Active) return;                                   // volatile, cleared before any teardown
  if (!ReferenceEquals(__instance, Program.OffscreenTarget)) return;    // main viewport's target only
  if (!ReferenceEquals(Program.RenderedViewport, Program.MainViewport)) return;
  try { StickerManager.Instance?.RecordPass(cb); } catch { log once; self-disable }
```

`RecordPass` is a near-verbatim `GridPass.Run` (§1.1): `BarrierBatch` → `DepthImage:DepthSampledReadF`,
`ColorImage:ColorAttachmentReadWrite(force)` → `BeginRendering` on `ColorImage` (Load/Store, no
depth) → bind pipeline → `Program.SetViewport(cb)` → set 0 `GlobalShaderBindings.DescriptorSet`
with `DynamicOffset(MainViewport.Index)`, set 1 this frame's depth descriptor set, set 2
`Program.Instance.BindlessTextures.DescriptorSet` → for each live, visible, in-range sticker:
`PushConstants` + `DrawIndexed(36)` → `EndRendering`. Depth is left in `DepthSampledReadF` exactly as
`GridPass` leaves it, so the engine's tracked-state barriers already tolerate that state for the rest
of the frame (`ClearDepthImages` next frame barriers from tracked state).

Runs on the main thread (the same thread as the command drain — `.agents/skills/ksa/quad.md`), so the
registry needs no locks; the postfix reads a volatile immutable array like thug_life.

**Gating contract (the user's requirement):**

- `PaintManager.Tick()` (already run by `Mod.DrivePaint` in `DrivePerFrame`, before the frame renders)
  adds `_stickers?.Tick()` beside `_textures?.Tick()`; `StickerManager.Tick` is `if (IsEmpty) return;`
  — one branch per frame when nothing exists. Faults latch `_dead` inside the manager (one log,
  `renderer=degraded`), the `UpdateThugLife` idiom.
- The Harmony patch is installed on the **0 → 1 live** transition and removed on **1 → 0** (live =
  anchor resolved this frame *and* texture ready). Dormant entries (vessel despawned, image evicted)
  keep the registry non-empty but do **not** keep the patch installed.
- GPU pipeline/shader/box mesh are created lazily on first live sticker and destroyed on the last
  removal/clear/unload (after `GraphicsAndCompute.WaitIdle()`, the `DisposeDisplayCapture` pattern).
  Textures are destroyed through the retire ring (§3.4). With zero stickers there is no pipeline, no
  texture, no descriptor pool, no patch — identical to the thug_life/IVA/always_render discipline.

### 3.3 The decal: box volume + depth reconstruction

Decal space: unit cube centred on the surface point, `x` = width (right), `y` = height (up, "top of
the PNG"), `z` = outward normal; scaled by `(w, h, d)` metres. The fragment shader reconstructs the
scene position under each pixel and projects it into decal space.

Push constants (112 B ≤ the 128 B Vulkan minimum; stages Vertex | Fragment):

```glsl
layout(push_constant) uniform Sticker {
    vec4 decalToEgo[3];   // 3x4 row-major, vertex: box corner → ego
    vec4 egoToDecal[3];   // 3x4, fragment: reconstructed ego point → decal [-0.5,0.5]^3
    uint textureId;       // bindless slot
    float alpha;          // user opacity
    float brightness;     // user gain on the lighting term
    float normalCutoff;   // cos(angle) below which the decal fades (grazing/backfacing surfaces)
} sticker;
```

```glsl
// gatos_sticker.vert  — #include "../Common/Camera.glsl" (set 0, compiled with a debugName under
// Content/Core/Shaders/Mesh/ so the include base resolves exactly like Grid.vert's)
vec3 ego = vec3(dot(decalToEgo[0], vec4(inPos,1)), dot(decalToEgo[1], …), dot(decalToEgo[2], …));
gl_Position = global.camera.viewProjection * vec4(ego, 1.0);
```

```glsl
// gatos_sticker.frag
float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;   // resolved, 1 texel per fragment
if (z <= 0.0) discard;                                            // reverse-Z: nothing drawn here
vec2 ndc = (gl_FragCoord.xy / viewportSize) * 2.0 - 1.0;         // Y convention verified in S3
vec4 v = global.camera.inverseProjection * vec4(ndc, z, 1.0);  v /= v.w;
vec3 pEgo = (global.camera.inverseView * vec4(v.xyz, 1.0)).xyz;  // view is rotation-only → ego
vec3 pDec = egoToDecal * pEgo;
if (any(greaterThan(abs(pDec), vec3(0.5)))) discard;             // outside the box
vec3 n = normalize(cross(dFdx(pEgo), dFdy(pEgo)));               // receiving-surface normal
float facing = dot(n, decalAxisZ);                               // fade on steep / back faces
if (facing < normalCutoff) discard;
vec4 t = SAMPLE_TEXTURE(textureId, 0, pDec.xy + 0.5);            // clamp-to-edge, mips, aniso-free
if (t.a < 0.004) discard;
vec3 L = normalize(global.lighting.sunPosition.xyz - pEgo);      // sun in ego (camera at origin)
vec3 lit = gammaToLinear(t.rgb) * (global.lighting.sunColor.rgb * max(dot(n, L), 0.0) + ambient) * brightness;
outColor = vec4(lit, t.a * alpha * smoothstep(normalCutoff, normalCutoff + 0.2, facing));
```

Pipeline: vertex input = `float3` box corners (36 indices), `CullFront` (the camera can be inside the
box), `RenderingPresets.ReverseZDepthStencil.NoDepthTest` (no depth attachment at all),
`RenderingPresets.BlendState.BlendColorAlphaOver`, 1 sample, colour format
`Program.Instance.ColorFormat` (`R16G16B16A16SFloat`), hand-built `VkPipelineRenderingCreateInfo` as
`GridPass.BuildPipeline` does (`KSA/GridPass.cs:474-476`) — **not** `OffscreenTarget.SetupGraphicsPipeline`,
which would stamp the MSAA sample count. The `ambient` term is a fraction of `nearestCelestialColor` /
`planetColor` (planetshine) so stickers are not black on the night side; exact weights are a
validation tuning item. The lighting is an approximation (no cast shadows, no atmosphere): §10 has
the v2 that inherits the scene's lighting exactly.

Mip selection comes from the reconstructed position's derivatives, so depth discontinuities at the
decal edge produce a one-pixel noisy mip — acceptable; `textureGrad` with clamped derivatives is the
fallback if it shows.

### 3.4 Textures: the landed store → shared GPU helper → bindless, with honest destruction

- **Store (landed, two additions):** `TextureStore` at `/sim/paint/textures/file/` as-is — name
  charset, `paint_texture_max_{bytes,total_bytes,files}` caps, `SniffKind` on commit, `(Name, Version)`
  identity with immutable committed arrays, `TryGet → Ready|Uploading|Missing`. Stickers need to
  notice a re-commit or deletion of a file they use, but `Revision` is deliberately binding-scoped
  (`BumpIfBoundLocked`, `:868`) and `Delete` only drops *clutter bindings* (`:447-460`). Add, game-free
  and unit-tested: **`int ContentRevision`** (bumped under the lock on every `Commit`, `Delete`, `Clear`)
  and **`int? CurrentVersion(string name)`** (alloc-free; `null` = missing/uploading). `StickerManager.Tick`
  reconciles textures only when `ContentRevision` moved — the same "one integer compare while idle"
  contract the bridge has with `Revision`. `paint_texture_max_bindings` does **not** count stickers;
  `paint_stickers_max_count` does.
- **GPU (`UserTextureGpu`, factored out of `ClutterTextureBridge.Apply` in S0):**
  `Upload(renderer, TextureFile file, int maxDim, bool faithful)` = the landed sequence verbatim —
  `TextureLoader.LoadFromMemory(bytes, kind, TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32))` →
  `MakeFaithful` **only when `faithful`** → `new TextureAsset(decoded, "gatos:paint/…/<name>")` →
  `using` staging pool → `new SimpleVkTexture(Allocator, pool, asset, new CreateOptions(maxDim,
  Downsample, fillMipChain: true))` → `pool.Submit().Wait()` → `DestroyDecoded`. Stickers call it
  with `faithful: false` (the PNG's sRGB bytes go up untouched; the sticker shader applies
  `gammaToLinear`; alpha is real opacity) and `maxDim = min(paint_texture_max_dimension, 2048)`
  (a 2048² RGBA8 mip chain ≈ 22 MiB; 4096² is 89 MiB — too much for a sticker). The retire queue
  (`Retired(Image, TicksRemaining)`, `MaxFramesInFlight + 1` ticks, `WaitIdle` + drain at unload)
  moves into the helper as `RetireQueue`; the bridge and the binder each own one instance. Done on
  the game thread in `PaintManager.Tick` (Frame-phase), never inside the render postfix.
- **Bindless (`StickerTextureBinder`):** `(name, version) → { SimpleVkTexture, int Handle, W, H,
  VramBytes }`; a sticker image additionally gets a slot — `BindlessTextures.AddTexture(image.ImageView)`
  → `textureId` for the push constant. (Clutter overrides *re-point existing* slots with
  `SetTexture`; stickers *allocate* new ones. Both are legal under the library's
  `UpdateAfterBind | PartiallyBound` layout.) `FreeTexture(handle)` rewrites the slot to the engine's
  empty texture immediately — the slot is safe the moment it is freed — and the image then rides
  the retire queue. Cap = `paint_texture_max_files` slots worst case (32 of 1024).
- **Eviction** mirrors `AudioActuator.ReleaseEvictedSounds`: on a `ContentRevision` change, an
  entry whose `CurrentVersion(name) != version` and which no sticker references is freed + retired.
  Re-uploading `logo.png` bumps its version → every sticker using it hot-swaps next tick. Deleting
  it makes those stickers dormant (`texture=missing`), never removed — re-upload resurrects them.
  The same file may simultaneously be bound over a clutter texture (faithful-corrected copy) and
  used by stickers (raw copy); the two owners hold independent `SimpleVkTexture`s by design.

### 3.5 Anchors and per-frame composition (`StickerAnchors`)

Two kinds, both stored in the frame that is invariant for the thing they are stuck to:

| Kind | Stored | Per-frame `decalToEgo` | Re-resolution |
|---|---|---|---|
| `vessel` | `VesselId`, `PartInstanceId` (sub-part), `PosLocal`, `NormalLocal`, `RollDeg` (part-local metres / unit vector) | `S(w,h,d) · R(frame from NormalLocal + roll) · T(PosLocal) · part.MatrixAsmb2Ego(in vehicle.GetMatrixAsmb2Ego(cam))` | `Universe.CurrentSystem.Get(VesselId) is Vehicle` + `FindPart(iid)` each frame (cheap); missing → dormant, not pruned (world persistence) |
| `ground` | `BodyId`, `LatDeg`, `LonDeg`, `HeadingDeg` | `dir = GetDirCcfFromLatLon`; `r = MeanRadius + GetTerrainHeightFromDirCcf(dir, accurate:false)`; frame = ENU at (lat,lon) rotated by heading, up = dir; `posEgo = cam.GetPositionEgo(body) + (dir·r).Transform(body.GetCcf2Cce())` | body by id each frame; rides the spin for free |

Both are `double` until the final `float3.Pack` of the 3×4 matrices. A distance cull
(`paint_stickers_max_view_distance_m`, default 5 km, per-sticker `fade`) skips draws that would be sub-pixel
and bounds per-frame cost to "a few dozen tiny draws".

### 3.6 Placement (`StickerPicker`) — `place` is exact, `spray` is aimed

- `place <image> vessel <vessel> <part_iid> x y z nx ny nz [roll=] [w=] [h=] [d=]` — everything
  explicit, part-local (`parts/<n>/` in `/sim` exposes the `instance_id`s; `d` default 0.3 m).
- `place <image> body <body> <lat> <lon> [heading=] [w=] [h=] [d=]` — `d` default 1.0 m (terrain).
- `spray <image> [aim=camera|cursor] [range=] [roll=] [w=] [h=] [d=]` — ray = the main camera's
  forward axis (default; headless-friendly, and `/sim/camera` can aim it) or `Cursor.InputRay`.
  1. Vehicles: for every live `Vehicle` whose bounding sphere is within `range` (default 2 km),
     `part.RayCastEgo(in vehMat, ray, …)` over `Parts.Parts`; nearest `closestSubPart` wins; the hit
     point is already part-local; the normal is re-derived by interpolating the hit triangle (the
     stock one is the flat first-vertex normal) — or, for precision against the `_VM` hull, re-cast
     the render mesh (`PartModel(.Dynamic).Template.Mesh.PositionCompare`) with the same
     `Ray.RaycastWatertight`. Roll defaults to "PNG up = part +Y projected onto the surface".
  2. Terrain: a straight-line `ITerrainTrajectory` into `TerrainImpactFinder.TryFind` (or an
     equivalent 32-step march + bisection over `GetTerrainHeightFromDirCcf`) on
     `camera.NearbyCelestial`; convert the hit CCF point to lat/lon; heading defaults to the
     camera's azimuth so the PNG reads upright from where you stand.
  3. Nothing hit → `NotFound`. Clutter: a ray passes through rocks to the terrain behind, and the
     decal then projects onto whatever is inside its box — rocks included. You cannot *aim at a
     rock* in v1 (§10 depth-pick fixes this).
- The result (`id`, anchor kind, target, distance) is published to `/sim/paint/stickers/last` and the
  `CommandResult` message, and a `paint.sticker_placed` event is emitted (scripts `cat events`).

### 3.7 `/sim/paint/stickers` surface (SPEC lockstep, §AGENTS constitution)

```
/sim/paint/stickers/help                      S   console readme (tested like thug_life's)
/sim/paint/stickers/info                      S   enabled=1 stickers=N live=M images=K vram_bytes=B stickers_max=… patch=0|1 renderer=idle|active|degraded
/sim/paint/stickers/status                    S   one line per sticker: <id> <image> <kind> <target> live=0|1 texture=ready|missing
/sim/paint/stickers/last                      S   result of the last place/spray
/sim/paint/stickers/last_error                S   renderer/texture fault text (empty when healthy)
/sim/paint/stickers/place                     St  line grammar → paint.sticker_place
/sim/paint/stickers/spray                     St  line grammar → paint.sticker_spray
/sim/paint/stickers/clear                     T   paint.sticker_clear
/sim/paint/stickers/count                     S
/sim/paint/stickers/<id>/spec                 S   write-compatible place line (round-trips)
/sim/paint/stickers/<id>/image                St  rebind to another uploaded image → paint.sticker_image
/sim/paint/stickers/<id>/anchor               S   "vessel <id> <iid> x y z nx ny nz" | "body <id> lat lon"
/sim/paint/stickers/<id>/live                 S   0|1
/sim/paint/stickers/<id>/visible              St  flag → paint.sticker_visible
/sim/paint/stickers/<id>/size                 St  "w h" → paint.sticker_size
/sim/paint/stickers/<id>/depth                St  m → paint.sticker_depth
/sim/paint/stickers/<id>/rotation             St  deg (roll / heading) → paint.sticker_rotation
/sim/paint/stickers/<id>/alpha                St  [0,1] → paint.sticker_alpha
/sim/paint/stickers/<id>/brightness           St  (0,8] → paint.sticker_brightness
/sim/paint/stickers/<id>/remove               T   paint.sticker_remove
(images: the existing /sim/paint/textures/{file/,files} — no sticker-specific file surface)
```

Addressing = registry-keyed (sticker id in `Ordinal`, image name in `Token`, anchor descriptor in
`Aux`, numbers in `Values`), all Frame phase, vessel-agnostic (handled in `KsaCatalog` before vessel
resolution, like `debug.thug_life_*` and `paint.texture_*`). Position edits are deliberately **not**
a leaf in v1 — the two anchor kinds have different arities; re-`place` from `spec` instead.

Config (flat keys appended to the landed PAINT block, `GatOsConfig` + `Sections` + clamp-and-warn +
`gatos.default.toml:170-185`): `paint_stickers_enabled = true` (a boot-time wiring decision like
`paint_textures_enabled`, no runtime master — idle cost is one `IsEmpty` branch),
`paint_stickers_max_count = 256` (1..4096), `paint_stickers_max_view_distance_m = 5000` (10..1e6).
Image caps are the landed `paint_texture_*` keys; the subtree requires `paint_textures_enabled`
too (no store → no images → no stickers: `EOPNOTSUPP`). Gate string: `control_enabled + paint
stickers` (own gate, like `paint textures store` — not either paint master). Health latches
`paint.sticker_texture` (decode/upload/bindless) and `paint.sticker_renderer` (pipeline/patch/draw).

### 3.8 Threading summary

Transports enqueue `SimCommand`s and read the volatile `StickerStore` status; the game thread
(Frame-phase drain → `PaintManager.Execute`, then `PaintManager.Tick` → `StickerManager.Tick`)
mutates the registry, decodes/uploads textures, resolves anchors and publishes status; the render
postfix (main thread, inside `RenderGame`) reads the published immutable array and records draws.
Uploads to the VFS happen on 9p/HTTP threads into the locked `TextureStore` exactly as today; the
GPU never sees bytes until the game thread asks for them.

---

## 4. Phases

### S0 — refactor the landed GPU path and extend the store (behaviour-preserving)
1. Extract `Game/Ksa/Paint/UserTextureGpu.cs` from `ClutterTextureBridge.Apply`/`RetireImage`/
   `DrainRetired`: `Upload(renderer, file, maxDim, faithful)` + `RetireQueue`. The bridge keeps its
   `[KsaAnchor]` for `SetTexture`/`TextureReference.ImageView`; the decode/upload anchor moves with
   the code. The 100 tests from `12dfa43` stay green; `/sim/paint/textures` behaviour is unchanged.
2. `TextureStore.ContentRevision` + `CurrentVersion(name)` with `TextureStoreTests` coverage
   (commit/delete/clear bump it; a bind/unbind does not; `Revision` is untouched).
3. If a live KSA session is available: run the clutter checklist's item #1 (the discrete
   `Submit().Wait()` upload) — it validates the shared helper for both features at once.

### S1 — game-free sticker table, grammar, tree, tests (no KSA code)
`gatOS.SimFs/Paint/Stickers/{StickerStore,StickerRules,StickerCommands}.cs` (`place`/`spray`
parsers, key=value tokens), `SimActions.PaintSticker*` + `CommandCatalog` descriptors (`Gate` →
`control_enabled + paint stickers`, `LogicalTool` → `gatos.paint_sticker`), `SimFsTree`
`StickerPaintRootDir()` added in `PaintDir()` beside `TexturePaintRootDir()`, `Formats.StickerSpec`,
the published `StickerStatus` record, config keys, the `help` readme. Tests: `StickerTreeTests` (the
thug_life/`TextureTreeTests` set, one-for-one: grammar → exact `SimCommand` incl. `Phase == Frame`,
EINVAL with `Submits == 0`, ENOENT, round-trip `spec`), `StickerRulesTests` (table-driven), extend
`ControlEnabledTree_ExposesEveryModuleControlStatusAndDebugPath` with every path, MQTT parity row.

### S2 — bindless binding in-game (no drawing yet)
`StickerTextureBinder` over `UserTextureGpu`: image → `AddTexture` handle, `(name, version)`
tracking off `ContentRevision`, `FreeTexture` + retire on evict/re-upload; `StickerManager` skeleton
owned by `PaintManager` (constructed in `EnsureControlObjects` from `_textureStore`, routed on
`paint.sticker_`, ticked, disposed); `info` shows `images=K vram_bytes=B renderer=idle`. Validate:
upload/re-upload/delete cycles, VRAM returns, validation layers clean.

### S3 — the renderer and ground anchors
`StickerDecalRenderer` (shader from embedded string via `FromString`, pipeline, box mesh, per-frame
depth descriptor ring), `StickerRenderPatches` (postfix on `ResolveAttachments`), `StickerAnchors`
ground path, `place … body …`. A `debug=1` token draws the box edges/UV checker so NDC-Y, reverse-Z
reconstruction and the ego matrices are verified visually before any art is involved. Lazy
install/teardown proven with the patch-count visible in `info`.

### S4 — vessel anchors and `spray`
`StickerAnchors` vessel path (`MatrixAsmb2Ego`, scale included), `place … vessel …`, `StickerPicker`
(`Part.RayCastEgo` sweep → terrain march), `last` + `paint.sticker_placed` event, dormancy on despawn/evict,
live-count patch gating.

### S5 — MCP + docs lockstep
No new HTTP routes (uploads landed; controls are field mirrors) — only OpenAPI prose. MCP
`gatos.paint_sticker` (`operation` ∈ place/spray/set/remove/clear/list with explicit typed fields —
`image`, `anchor` (`vessel`|`body`), `vessel_id`, `part_iid`, `position[3]`, `normal[3]`, `body`,
`lat`, `lon`, `heading`, `roll`, `width`, `height`, `depth`, `alpha`, `brightness`, `id` — mapped to
the canonical `paint.sticker_*` envelope, the `gatos.paint_texture` style of operation-shaped
docs), runtime feature document `paint_stickers` in `GetRuntimeState` + `features.paint_stickers`
in capabilities (the `paint_textures` precedent at `McpPresenters.cs:150,181`), tool-count test 27 →
28, `mcp-reference.ts` entry + `site/src/content/docs/mcp/tools/paint-sticker.mdx`, `SPEC_MCP.md`
(§1.1, §1.2, §3, §5, §5.1, §6.1, §7 — the same seven spots `12dfa43` touched);
`SPEC_9P_FILESYSTEM.md` (a `## Stickers (/sim/paint/stickers)` section beside `## Textures` at
`:1810`, config rows beside `:147`, action rows beside `:1523`, the `paint.sticker_placed` event
row, the `last`/`spec` round-trip note), `docs/KSA_INTEGRATION_MATRIX.md`, `scope/FULL_SCOPE.md` +
`ksa-write-surface.md` + `ksa-runtime-coupling.md` (new `gatos.stickers` dynamic patch section) +
`ksa-read-surface.md`, `docs/MILESTONES.md`, `docs/ARCHITECTURE.md` (the paint tick now drives
three things), `docs/VALIDATION.md` card, `site/src/content/docs/reference/paint.mdx` section +
`guides/visual-cheats.mdx`, `.agents/skills/gatos/SKILL.md` recipe, `plans/PAINT_ASBUILT.md`
(stickers join the maintenance contract + the KSA upgrade audit list), `README.md`, AGENTS.md
status row + the threading-rules paragraph (the paint tick's third self-gated driver and gatOS's
second render-thread draw injection).

### S6 — live validation pass (docs/VALIDATION.md card)
See §6.

---

## 5. Risks and mitigations (in order)

1. **Render-internals churn (High).** New `[KsaAnchor]`s on `RenderTarget.ResolveAttachments`,
   `RenderTarget.{DepthImage,ColorImage,Extent}`, `ImageBarrierInfo.Presets.{DepthSampledReadF,
   ColorAttachmentReadWrite}`, `BarrierBatch`, `GlobalShaderBindings.{DescriptorSet,DynamicOffset}`,
   `Program.{OffscreenTarget,RenderedViewport,MainViewport,PointClampedSampler,SetViewport,
   ResourceFrameIndex}`, `BindlessTextureLibrary.{AddTexture,FreeTexture,DescriptorSet,
   DescriptorSetLayout}`, `Global.glsl` UBO layout, `Stb.LoadFromMemory`, `SimpleVkTexture.UploadData`.
   Rev 5154 deleted the previous offscreen API wholesale; assume it can happen again. Mitigation: the
   same fault-latch discipline as thug_life (one log, `Active=false`, `renderer=degraded` +
   `last_error`), `MissingMethodException` at install, and a `scope/` section so the upgrade playbook
   re-verifies every anchor.
2. **Descriptor/lifetime hazards.** The depth view changes on resize/MSAA change (`GridPass.Rebuild`
   exists for this): a per-frame ring of depth descriptor sets rewritten each frame from the live
   `DepthImage.ImageView` (a set is only rewritten when its frame slot is not in flight — the
   FrameCapture argument). Texture destruction only through the retire ring; pipeline destruction only
   after a queue drain.
3. **Out-of-band upload submit.** `ThugLifeTextureFactory` ships doing exactly this and the clutter
   plan already flags it as validation item #1; if it proves unsafe the fallback is recording the
   upload in-band from our own postfix (we are already inside the frame's command buffer there) and
   binding a frame later.
4. **Reconstruction conventions.** NDC Y direction under `Program.SetViewport`, reverse-Z,
   `inverseView` being rotation-only — all verifiable in S3 with the debug box before any texture.
5. **Cosmetics inherent to C.** Grazing-angle stretch (normal cutoff fade), decals drawn over a plume
   or visor that is *in front* of them (rare; document), no aerial perspective (distance cull makes
   it moot), lighting approximate (§10 v2). None are correctness issues.
6. **Budgets.** Bindless slots are shared with the game (1024): cap images at 32 by default. VRAM:
   2048² RGBA8 + mips ≈ 22 MiB; `stickers_max_total_bytes` bounds the PNG side and `info` reports
   the GPU side. CPU: ≤ 256 stickers × (1 height sample + 2 matrix builds) per frame.
7. **Interplay.** Independent of part/EVA paint (different seams) and of the clutter overrides (shared store and shared `UserTextureGpu`, separate GPU images and separate retire queues), thug_life (different Harmony id and pass),
   display capture (stickers are in the colour image before `SampledReadVfc` at `:4436`, so
   `/sim/display` shows them), camera (a `/sim/camera` track + `spray` is the scripted workflow).

---

## 6. Verification

Unit (game-free): grammar/EINVAL/spec round-trip, store caps + errno + versioning + header sniff,
tree path census, HTTP route tests (single PUT, chunked POST, EFBIG 413, ENOSPC 507, disabled 404),
MCP discovery/schema + runtime-feature test.

In-game (`docs/VALIDATION.md` card): (1) zero-sticker steady state — `info patch=0
renderer=idle`, no GPU objects, `/sim/paint/textures` behaviour identical to before S0; (2) upload a 512² PNG with alpha, `/sim/paint/textures/files` shows `ready` and `info images=1`; (3) `place
body` at the pad — sticker hugs the ground, rides the planet rotation across a warp, survives a
vessel switch; (4) `spray` at a tank — conforms to the cylinder, moves with the vessel, gimbal part
decal follows the gimbal; (5) sticker across a rock field — projects onto rocks; (6) re-upload the
PNG → hot-swap, `rm` → dormant, re-upload → back; (7) `remove` last sticker → `patch=0`, GPU freed
(validation layers clean); (8) MSAA on/off + resolution change + CMAA2 mid-session — no crash, decal
still aligned; (9) night side / shadowed side brightness sanity; (10) F2 (UI hidden) — still drawn;
(11) crew-cam portraits unaffected; (12) unload with stickers present → clean.

---

## 7. Touch list

```
S0: gatOS.GameMod/Game/Ksa/Paint/{UserTextureGpu (new), ClutterTextureBridge (shrinks)}.cs
    gatOS.SimFs/Paint/TextureStore.cs (+ContentRevision, +CurrentVersion)  gatOS.SimFs.Tests/Paint/TextureStoreTests.cs
S1: gatOS.SimFs/Paint/Stickers/{StickerStore,StickerRules,StickerCommands,StickerStatus}.cs
    gatOS.SimFs/{SimFsTree,Formats}.cs  gatOS.SimFs/Commands/{SimActions,CommandCatalog}.cs
    gatOS.SimFs.Tests/Paint/Stickers/*.cs  gatOS.SimFs.Tests/SimFsTreeTests.cs  gatOS.Mqtt.Tests/MqttBrokerTests.cs
    gatOS.GameMod/Configuration/{GatOsConfig.cs,gatos.default.toml}
S2–S4: gatOS.GameMod/Game/Ksa/Paint/Stickers/{StickerManager,StickerTextureBinder,StickerDecalRenderer,
    StickerRenderPatches,StickerAnchors,StickerPicker}.cs  + the two shaders as C# string constants
    gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs (owner: route/tick/dispose)  gatOS.GameMod/Game/Mod.Game.cs (construction only)
S5: gatOS.Mcp/{McpRegistry,McpToolHandlers,McpPresenters}.cs  gatOS.Mcp.Tests/McpPresenterTests.cs
    gatOS.Http/OpenApi.cs  sim_openapi.yml (prose only)
    SPEC_9P_FILESYSTEM.md  SPEC_MCP.md  AGENTS.md  README.md
    docs/{KSA_INTEGRATION_MATRIX,MILESTONES,VALIDATION,ARCHITECTURE,TUTORIAL_DATA_REFERENCE}.md
    scope/{FULL_SCOPE,ksa-write-surface,ksa-read-surface,ksa-runtime-coupling}.md  plans/PAINT_ASBUILT.md
    site/src/content/docs/reference/paint.mdx  site/src/content/docs/guides/visual-cheats.mdx
    site/src/content/docs/mcp/tools/{index,paint-sticker}.mdx  site/src/data/mcp-reference.ts
    .agents/skills/gatos/SKILL.md
```

---

## 8. Why this shape and not a bigger one

The feature is a thin vertical slice through machinery that already exists: the upload store, HTTP
upload, MCP store tool and GPU texture path landed in `12dfa43`, the registry/patch lifecycle is
thug_life's, the owner/tick/dispose seam is `PaintManager`'s, the geodetic math is the camera's, the
transport parity is structural. The only genuinely new code is ~400 lines of Vulkan + GLSL in one
folder, anchored on a pass the engine itself uses for a post-resolve overlay (`GridPass`). Everything
else is grammar, docs and tests.

## 9. Persistence

v1 is runtime-only like every gatOS world mutation, **but** every sticker has a write-compatible
`spec` line and every image is a plain file, so the guest's own persistent disk is the save game:

```sh
# ~/stickers/save.sh   (guest side)
mkdir -p ~/stickers/img && for f in /sim/paint/textures/file/*; do cp "$f" ~/stickers/img/; done
for d in /sim/paint/stickers/[0-9]*; do cat "$d/spec"; done > ~/stickers/specs
# ~/stickers/restore.sh — run from /etc/local.d or a cron @reboot once /sim is mounted
for f in ~/stickers/img/*; do cat "$f" > "/sim/paint/textures/file/$(basename "$f")"; done
while read -r line; do echo "$line" > /sim/paint/stickers/place; done < ~/stickers/specs
```

That is the "zero custom guest binaries, the unix toolbox is the API" answer. A host-side
`GatOsPaths.StickersDir/stickers.json` autosave is a small v2 (§10) and would be the first gatOS
feature to persist world state across sessions — call it out in SPEC/scope if it lands.

## 10. v2 candidates (not committed)

- **Depth pick for exact aiming at clutter:** copy the one depth texel under the cursor/centre to a
  host-visible ring buffer in-band (the FrameCapture idiom), read it next frame, reconstruct the hit
  → `spray` lands precisely on a rock face, and `last` can report `hit=clutter`.
- **Inherit scene lighting exactly:** copy `ColorImage` to a scratch image before the pass and output
  `decal.rgb * luminance(scene) * gain` — shadows and sun direction come from the pixel itself.
- **Mesh-conforming vehicle decals in the opaque scope (architecture B):** clip
  `PositionsCompare` against the box, draw with a lit alpha shader and a static depth bias in the
  `RenderMainPass` postfix — MSAA-perfect, under atmosphere/plumes, visible in crew cams.
- **Host JSON autosave**, **secondary-viewport support**, **`rotation`/`offset` nudging**, a
  `mode=multiply` blend for "stencil on white paint".

---

## 11. As built — deltas from the plan

Everything above is the plan as written on 2026-08-22. What actually landed differs in the following
places; **the code is the source of truth**, and `SPEC_9P_FILESYSTEM.md` / `SPEC_MCP.md` /
`plans/PAINT_ASBUILT.md` document the built behaviour, not this plan.

| # | Area | Planned | As built | Why |
|---|---|---|---|---|
| 1 | Files | S1 listed `StickerStatus.cs`; S2–S4 listed six game-side files | No `StickerStatus.cs` — the projection types (`StickerSnapshot`, `StickerRuntime`, `StickerAnchorKind`, `StickerTextureState`) live in `StickerStore.cs`, and the rendering lives in `Formats.cs`. Seven game-side files, not six: `StickerEntry.cs` was split out of `StickerManager.cs` | One record set, one owner; the entry is a mutable class the driver edits in place and the published array points at, so it earns its own file |
| 2 | Config | `paint_stickers_max_view_distance_m` | Key spelling unchanged; the C# property is `PaintStickersMaxViewDistanceM` (not `…Metres`), so the config key ↔ property mapping stays mechanical | `GatOsConfig`'s convention is the key's own words |
| 3 | Formatting | Unspecified | `Formats.StickerSpec` **delegates** to `StickerCommands.FormatSpec`, and every scalar goes through `Formats.Scalar`. `Formats.StickerStatusRow` owns the `status` row | The grammar that parses `place` also renders `spec`, so the round trip cannot drift; `Formats` stays the single vocabulary for the tree |
| 4 | Tree archetypes | Implied the ordinary control archetypes | `depth` and `brightness` carry **exclusive** lower bounds, which no fixed archetype expresses, so they are `LineControl`s validated against `StickerRules` (`StickerScalarControl`). `alpha` uses a new `RangedControl` overload that takes a registry **ordinal** | EINVAL before enqueue, and the same bounds on every transport |
| 5 | Validation | Not called out | New `StickerRules.IsValidTarget` — 1..64 non-whitespace, non-control chars — for vessel/body ids. Deliberately wider than the `/sim` sanitized-path charset (a command carries the *raw* game id) but narrow enough that an id can never break the whitespace-split `spec` round trip | `place` is authored directly by HTTP/MQTT/MCP, which never see the path layer |
| 6 | Actions | Eleven `paint.sticker_*` actions; the box checker was an S3 development aid only | **Twelve**: `paint.sticker_debug` and a `/sim/paint/stickers/debug` flag leaf are part of the shipped surface, and `debug` is a seventh MCP operation | It is the only way to verify the box, the reverse-Z reconstruction and the anchor matrices in a live session, which is exactly what S6 has to do |
| 7 | Texture states | `texture=ready\|missing` | Four states — `ready`, `missing`, `uploading`, `failed` — returned by `StickerTextureBinder.Resolve(image, out state)`, with failures latched in a `(name, version)` set so a broken image is retried **once per content version** rather than every reconcile | "Nothing is drawing" needs to distinguish *not committed yet* from *the decode threw* |
| 8 | `spray` rotation | `roll=` sets the roll | `roll=` **adds to** the picker's "reads upright from here" rotation | Replacing it would make every sprayed decal ignore the surface it landed on |
| 9 | `spray` depth | Unspecified | The 7-slot payload carries `d = -1` (`StickerCommands.DepthUnset`) when the caller passed no `d=`, and the game side substitutes the anchor kind's default *after* the ray says what it hit | The vessel and body defaults differ (0.3 m vs 1 m) and the kind is not known until the ray returns |
| 10 | Registry cap | `ENOSPC` past `paint_stickers_max_count` | `CommandOutcome.Invalid` (**EINVAL**) with the cap in the message | There is no `ENOSPC` in the command-outcome vocabulary — it is a 9p/HTTP *write* errno, which is why `TextureStore` can throw it and the registry cannot |
| 11 | Image budget | "cap images at 32 by default" (a count cap, for bindless slots) | No sticker image-count key. Instead a hard `StickerTextureBinder.MaxStickerDimension = 2048` ceiling on the longest edge, independent of `paint_texture_max_dimension`, and the image count is bounded transitively by `paint_texture_max_files` | Distinct images are bounded by the upload store already; the real cost is VRAM per image (2048² RGBA8 + mips ≈ 22 MiB, 4096² ≈ 89 MiB) |
| 12 | Pipeline winding | Not called out | `Presets.Rasterization.Fill.CullFront` — the cube is wound CCW seen from outside, so culling the **front** faces leaves the far faces and the box still covers its screen footprint when the camera is **inside** it. Same reason KSA draws the planet with `CullFront` | A near-face-only draw clips the decal away the moment you walk into its box |
| 13 | Shader | Include `Common/Shared.glsl` for `gammaToLinear` | Inlined as a two-line `StickerGammaToLinear` | Including it would pull four more files in for one `pow()` |
| 14 | Shader | Not called out | `dFdx`/`dFdy` of the reconstructed position are taken **before** any `discard`, and every rejection test is written in negated form (`if (!(facing >= cutoff))`) | Derivatives are only defined in uniform control flow, and a NaN must discard rather than sail through a non-negated comparison |
| 15 | Surface | `place`/`spray`/`clear` + per-id leaves | Also `count`, `last_error`, and an `info` line carrying `stickers_max` and `max_view_distance_m` | The parsers' limits have to be discoverable from the tree, not only from the config file |
| 16 | Events | Not specified beyond "emit" | `paint.sticker_placed` carries the *same* line `stickers/last` publishes, and its `vessel` field is the anchor vessel id for a vessel anchor and **omitted** for a body anchor | A body anchor names no vessel, and the event field is the vessel filter every consumer already has |
| 17 | Anchor re-resolution | By vehicle + part | `FindPart` searches **sub-parts** too, because `Part.RayCastEgo` anchors to a sub-part, so a sprayed sticker normally names a sub-part's `InstanceId` | Otherwise every sprayed vessel sticker would go dormant on the next frame |
| 18 | `place` grammar | One rotation key | Anchor-specific: `roll=` on a vessel anchor, `heading=` on a body anchor, and neither spelling is accepted against the other kind | The two mean different things; silently accepting the wrong one would place the decal wrong with no error |
| 19 | MCP | `operation` ∈ place/spray/set/remove/clear/list | Plus `debug`; `set` takes `id` **plus exactly one** knob (zero or two is EINVAL), `list` is a read that submits no command, and `anchor` may be omitted — it is inferred from whichever of `vessel_id`/`body` is filled | One tool call = one canonical action, the same one-file-one-action shape the filesystem has |
| 20 | Teardown | Wait-idle before destroy | Unchanged, plus: `Teardown()` leaves the **binder's images alone** (they retire on their own rules) so a re-placement does not have to decode them again; only `Dispose()` frees them | Toggling the last sticker off and on again is a normal editing action |

Unchanged from the plan and worth restating because they are load-bearing: nothing runs while
nothing is placed (one drain call and one `IsEmpty` branch), the patch and the GPU objects come up on
the `0 → 1` live edge and go away on `1 → 0`, dormant entries are never pruned, `<id>/spec` is a
write-compatible `place` line, and the whole feature is main-viewport-only in v1.
