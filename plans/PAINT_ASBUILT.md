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
  `PartPaintPatches` contains every Harmony seam; `EvaPaintBridge` owns material clones/bindings.

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
```

Colours are `r g b`, finite normalized sRGB. HTTP is `GET|POST /v1/fs/<path>`. MQTT is retained
`gatos/sim/<path>` plus writes to `gatos/sim/<path>/set`. MCP uses `gatos.paint_control` with the
stable action suffix as `operation`, optional `vessel_id`, `target`, scalar `value`, and `color`.
The advanced `gatos.command` envelope accepts every `paint.*` action unchanged.

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

Any failed reflection/anchor preflight must degrade the relevant master without partial patches,
stock-material mutation, leaked clone handles, or a render-thread exception.
