# gatOS paint — as built and maintenance contract

Status: code complete; live KSA validation pending. Baseline audited: KSA `2026.8.19.5261`.

## Purpose and ownership

Paint is a vertical slice with a deliberately narrow game boundary:

- `gatOS.Paint/` is game-free: normalized sRGB, `multiply|tint|replace`, 7:7:7 state-bit encoding,
  immutable snapshots, precedence, and the pure GLSL source transformation.
- `gatOS.SimFs/SimFsTree.cs` owns the `/sim/paint` and per-vessel/per-part filesystem grammar. HTTP
  `/v1/fs/...` and MQTT `gatos/sim/...` derive from that tree; they do not implement paint again.
- `gatOS.Mcp/` presents the same store through `gatos.paint_control`, `feature="paint"`, and
  `gatos://runtime/paint`.
- `gatOS.GameMod/Game/Ksa/Paint/` is the only KSA-aware layer. `PaintManager` is the lifecycle owner;
  `PartPaintPatches` contains every Harmony seam for part paint; `EvaPaintBridge` owns material
  clones/bindings.

Paint has since grown two further subsystems that `PaintManager` owns and ticks the same way but
which share no state with the two colour mechanisms above:

- **Ground-clutter texture overrides** (`/sim/paint/textures`, `TextureStore` +
  `ClutterTextureBridge`) — one bindless descriptor write per bind, no patch and no pipeline.
- **Stickers** (`/sim/paint/stickers`, `StickerStore` + `Game/Ksa/Paint/Stickers/`) — see below.
  `Game/Ksa/Paint/UserTextureGpu.cs` is the decode → `SimpleVkTexture` → retire-ring helper both of
  them share; neither owns its own decoder.

Neither has a runtime master, because both cost nothing when nothing is bound or placed. Both carry
their **own** capability gate (`control_enabled + paint textures store`,
`control_enabled + paint stickers`) rather than either paint master.

Rules are session-only. Disabling a runtime master retains desired rules for re-enable; explicit
`clear` removes them. Mod unload clears all rules and restores stock rendering.

## Vehicle part paint

The part master is `/sim/paint/parts/enabled`, default `0`. Enabling is transactional:

1. resolve the exact four-argument `ShaderModuleUtils.FromFile(Device,string,out
   VkShaderStageFlags,CompileOptions?)` overload and all PartModel methods;
2. reject activation if any non-gatOS `FromFile` prefix is installed (notably standalone
   humble-arteest), because two prefixes that can skip the global compiler cannot compose safely;
3. resolve and transform required `MeshIndirect.frag`; probe optional
   `MeshIndirectRaytraced.frag`;
4. install all exact Harmony methods; if any patch fails, remove the methods already installed;
5. arm interception and set `Program.RendererRebuildNeeded = true`.

The shader files on disk are never written. The prefix compiles transformed UTF-8 source through
`ShaderModuleUtils.FromString`, preserving KSA's compile options and original path. The transform
inserts after the `vec3 sampledColor ...;` declaration and requires the `inStateFlags` varying.
Failure disarms paint immediately, falls back to the stock compile for that invocation, schedules a
second deferred stock rebuild, and exposes `degraded` plus `last_error` in paint status.

Colour is quantized in sRGB to 7:7:7 and packed into `PartModel.PerInstanceData.StateBitFlag` bits
11..31. Zero means unpainted, so literal black encodes as packed value `1`. The current game uses
only bits 0..10. Static and dynamic `UpdateRenderData` enter a thread-static Part scope; Harmony
finalizers restore the prior scope even when KSA throws. The matching static/dynamic `AddInstance`
prefix consumes that Part and ORs the bits. Glass has a separate shader and remains stock.

Part precedence is:

1. `(vessel id, Part.InstanceId)`;
2. live vessel id (new/staged parts inherit while they remain in that vessel);
3. `Part.Template.Id`;
4. global;
5. stock.

Top-level parts and recursive subparts are indexed. Per-instance rules are pruned after their part
despawns. The individual `/sim/.../parts/<n>/paint` paths require `telemetry_vessel_parts=true` so
the stable `instance_id` is discoverable; canonical commands/MCP may use the id directly.

## EVA kitten paint

The kitten master is `/sim/paint/kittens/enabled`, default `0`. gatOS never overwrites shared stock
`MaterialData`: KSA's fixed material buffer is device-local `TransferDst|StorageBuffer`, so legal
read-back is unavailable and a reset-to-white scheme cannot preserve another mod's value.

Instead `EvaPaintBridge` discovers each live `KittenEva._renderable ->
KittenRenderable._characterAvatar`, captures the protected `MaterialIndices` slots, creates
gatOS-owned clones through `GpuMaterialSystem.CreateObject`, and rebinds the arrays. Supported
semantic names are `body[.n]`, `fur[.n]`, `helmet[.n]`, `visor[.n]`, and `mmu[.n]`. Sclera slots and
cosmetics stay stock. RGB alpha is always `1`.

Clones are pooled by `(source material handle, quantized colour)`, reference-counted each frame,
and capped by `paint_max_material_clones` (default 64, KSA buffer capacity 512). Standard PBR data is
reconstructed from the source `PbrMaterialReference`; fur data follows KSA's
`CharacterRenderResources.CreateFurMaterial` recipe. On disable, avatar replacement, despawn, or
unload, a slot is restored only if it still contains gatOS's handle (interop-safe conditional
restore). The owned `AssetMap` entry is then removed and its `GpuObjectAssetRef` disposed, returning
the handle to KSA's fixed allocator.

EVA precedence is:

1. individual EVA + semantic material;
2. individual EVA default;
3. shared semantic material;
4. shared default;
5. stock.

“Shared” therefore means one rule applied to every live EVA through safe clones—not mutation of a
game-wide shared material. An avatar rebuilt by KSA is detected by reference identity and rebound.

## Stickers

Stickers project a user PNG onto whatever opaque geometry is inside a box anchored to a vessel part
or a geodetic point — vehicles, terrain and ground clutter alike. The registry is game-side
(`StickerManager`), because an anchor can only be resolved against live game state; `StickerStore` in
SimFs is the game-free read model the transports render.

`Game/Ksa/Paint/Stickers/` is the only KSA-aware layer and holds **15** `[KsaAnchor]` sites across
seven files: `StickerManager` (registry, per-frame driver, lazy GPU/patch lifecycle, teardown),
`StickerEntry` (one mutable registry row), `StickerTextureBinder` (`(name, version)` → a bindless
slot via `AddTexture`/`FreeTexture` + a retire queue), `StickerAnchors` (per-frame decal-space
composition, all in `double`), `StickerPicker` (the `spray` ray: vehicle raycast first, terrain
march+bisect behind it), `StickerDecalRenderer` (pipeline, unit-cube mesh, depth-descriptor ring and
the pass itself) and `StickerRenderPatches` (the Harmony seam).

Two lifecycle invariants make it free when unused and safe when torn down:

1. **Nothing runs while nothing is live.** `StickerManager.Tick` is one drain call and one `IsEmpty`
   branch with an empty registry. The pipeline, the descriptor pool, the unit cube and the Harmony
   patch all come up on the `0 → 1` live transition and go away on `1 → 0`. Dormant entries (vessel
   despawned, image evicted) keep the registry non-empty but do **not** keep the patch installed.
2. **Dormant, never pruned.** Only `remove`, `clear` and unload delete entries, so `<id>/spec` stays
   readable and a guest-side save/restore script keeps working.

The draw is gatOS's **second** render-thread injection (thug_life's is the first, on a different
method with a different Harmony instance): a postfix on `RenderTarget.ResolveAttachments` under the
Harmony id `gatos.stickers`, filtered to `Program.OffscreenTarget` **and**
`Program.RenderedViewport == Program.MainViewport`. It is the same post-resolve window KSA's own
`GridPass` draws in, and the pass is a near-verbatim port of it: barrier depth to sampled-read and
colour to attachment read/write, `BeginRendering` on the resolved single-sample colour image with
`LoadOp.Load`, bind set 0 (KSA's global UBO block with the per-viewport dynamic offset), set 1 (our
scene-depth sampler, from a `MaxFramesInFlight` ring indexed by `Program.Instance.ResourceFrameIndex`)
and set 2 (KSA's bindless table), then one 36-index unit-cube draw per drawable sticker with a
112-byte push block. There is no depth attachment and no depth test: occlusion is decided per
fragment from the sampled reverse-Z scene depth, which is exactly what lets the decal conform to
hull curvature, tessellated terrain and ground clutter that has no CPU-addressable transform at all.

Faults latch like part paint's: one log, `StickerManager.Active = false`, `renderer=degraded` and the
text in `last_error`. Teardown clears `Active` first, unpatches, then `GraphicsAndCompute.WaitIdle()`
before destroying anything — KSA has no deferred-destroy helper.

## Public surface

Core files:

```text
/sim/paint/status
/sim/paint/parts/{enabled,blend,clear,global/{enabled,color,clear},templates/<id>/...}
/sim/paint/kittens/{enabled,clear,shared/{enabled,color,clear},materials/<name>/...}
/sim/vessels/by-id/<id>/paint/parts/{enabled,color,clear}
/sim/vessels/by-id/<eva-id>/paint/kitten/{default/... ,materials/<name>/...}
/sim/vessels/by-id/<id>/parts/<n>/paint/{enabled,color,clear}
/sim/vessels/by-id/<id>/parts/<n>/subparts/<m>/paint/{enabled,color,clear}
/sim/paint/textures/{file/,files,bind,unbind,clear,bindings,applied,clutter,status,info,help}
/sim/paint/stickers/{help,info,status,last,last_error,count,place,spray,clear,debug}
/sim/paint/stickers/<id>/{spec,anchor,live,image,visible,size,depth,rotation,alpha,brightness,remove}
```

Colours are `r g b`, finite normalized sRGB. HTTP is `GET|POST /v1/fs/<path>`. MQTT is retained
`gatos/sim/<path>` plus writes to `gatos/sim/<path>/set`. MCP uses `gatos.paint_control` with the
stable action suffix as `operation`, optional `vessel_id`, `target`, scalar `value`, and `color`.
The advanced `gatos.command` envelope accepts every `paint.*` action unchanged.

The two later subsystems have their own MCP tools and runtime feature documents — `gatos.paint_texture`
/ `feature="paint_textures"` and `gatos.paint_sticker` / `feature="paint_stickers"` — because neither
fits the colour-rule shape of `gatos.paint_control`. Sticker **images** are entries of the texture
store, so there is no sticker upload route on any transport.

## KSA upgrade audit (mandatory)

On every KSA baseline change, re-audit all of these even if compilation is green:

1. `ShaderModuleUtils.FromFile` and `FromString` signatures, `CompileOptions` assembly, shader-stage
   out semantics, and whether prefixes on the global compiler remain the correct seam.
2. `MeshIndirect.frag` and optional raytraced path/id, the `vec3 sampledColor` anchor,
   `inStateFlags`, `gammaToLinear`, include behavior, and all feature variants.
3. every stock write/use of `StateBitFlag`; bits 11..31 must remain free. Confirm the static/dynamic
   `PerInstanceData` layout/stride and signed OR behavior.
4. exact signatures and call topology of `PartModelModule.UpdateRenderData`,
   `PartModelDynamicModule.UpdateRenderData`, `PartModel.AddInstance`, and
   `PartModelDynamic.AddInstance`. Confirm one scoped Part maps to the intended submission.
5. `Program.RendererRebuildNeeded` remains the safe deferred pipeline boundary. Never replace it
   with an inline `ColorData.Rebuild()`.
6. `KittenEva._renderable`, `KittenRenderable._characterAvatar`, `CharacterAvatar` core/fur/
   attachments, and every protected `MaterialIndices` field. Re-check semantic slot exclusions.
7. `GpuMaterialSystem` capacity and `MaterialData` layout; `GpuObjectSystem.CreateObject/Free`,
   `AssetManager.AssetMap`, `GpuObjectAssetRef.Dispose`, texture/sampler handles, Vulkan upload
   barrier behavior, and the fur-material construction recipe.
8. Live-test conflict refusal with humble-arteest, disable/enable cycles, scene changes, EVA avatar
   replacement, clone-cap failure, and conditional restore beside another material-rebinding mod.
9. **Stickers, render seam:** `RenderTarget.ResolveAttachments(CommandBuffer)` still exists, is still
   called unconditionally for the main viewport in `Program.RenderGame`, and `Program.OffscreenTarget`
   is still the main viewport's target; `Program.{RenderedViewport,MainViewport,SetViewport,
   PointClampedSampler,ColorFormat}` and `Program.Instance.ResourceFrameIndex` unchanged;
   `RenderTarget.{DepthImage,ColorImage,Extent}`, `BarrierBatch` and
   `ImageBarrierInfo.Presets.{DepthSampledReadF,ColorAttachmentReadWrite}` unchanged. Compare against
   `GridPass` — if the engine's own post-resolve overlay moved, this pass moves with it.
10. **Stickers, shader layout:** the descriptor-set order is baked into the GLSL (set 0 = the global
   Camera/GlobalLighting/Celestial UBO block with its dynamic per-viewport offset, set 1 = ours,
   set 2 = the bindless table via `SET_TEXTURE`). Re-verify `GlobalShaderBindings.DescriptorSetLayout`
   and `.DynamicOffset`, `BindlessTextureLibrary.{DescriptorSetLayout,DescriptorSet,AddTexture,
   FreeTexture}`, and the field layouts of `Common/{Global,Camera,TextureSet}.glsl` — in particular
   `camera.inverseProjection`/`inverseView`, the reverse-Z convention (0 = far plane) and
   `lighting.{sunPosition,sunColor,planetColor}`. Also confirm the `GridFrag` asset still resolves a
   real path next to `Grid.frag` (`Content/Core/DefaultAssets.xml:367`), because that is how our
   `#include` directory is derived rather than hard-coded, and that the 112-byte push block still
   fits the device's push-constant limit.
11. **Stickers, image path:** `TextureLoader.LoadFromMemory`, `TextureAsset.LoadOptions`,
   `SimpleVkTexture`'s constructor and `Renderer.Allocator.CreateStagingPool` — shared with the
   clutter overrides through `UserTextureGpu`, so a break there breaks both. The one-shot
   `stagingPool.Submit().Wait()` remains the shared unvalidated risk (`docs/VALIDATION.md`).

Any failed reflection/anchor preflight must degrade the relevant master without partial patches,
stock-material mutation, leaked clone handles, or a render-thread exception.
