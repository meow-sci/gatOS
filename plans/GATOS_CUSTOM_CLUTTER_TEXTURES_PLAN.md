# GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md — `/sim/paint/textures`: user PNGs as ground-clutter textures

> **STATUS (2026-08-22): IMPLEMENTED — code complete, in-game validation pending.** Research base
> verified against KSA `2026.8.19.5261` decomp + shipped GLSL; all five phases below are built and
> unit-tested. The live checklist is `docs/VALIDATION.md` §"Custom clutter textures live KSA
> checklist (2026.8.19.5261)", and **risk #1 below (the out-of-band submit) is still unvalidated** —
> it is item #1 of that checklist. Where the shipped design diverges from what this plan proposed,
> the plan text has been corrected in place and the divergences are summarised in
> [As built — deltas from the original plan](#as-built--deltas-from-the-original-plan).

## Goal

Let a userland program replace the diffuse/normal/PBR texture of any ground-clutter material
(rocks, trees, grass, shrubs) with its own PNG, using nothing but file writes — the same shape as
`/sim/audio/file/<name>`:

```sh
# what can I override?
cat /sim/paint/textures/status
cat /sim/paint/textures/clutter               # one row per overridable stock texture

# upload — bytes held in-memory mod-side, never touch disk
cat mossy-rock.png > /sim/paint/textures/file/mossy-rock.png

# bind it over a stock clutter texture (renders as authored; add 'raw' for byte-for-byte)
echo 'RockDiffuseA mossy-rock.png' > /sim/paint/textures/bind

# inspect / revert
cat /sim/paint/textures/bindings               # each row is a valid bind line
cat /sim/paint/textures/applied                # what actually reached the GPU
echo 'RockDiffuseA' > /sim/paint/textures/unbind
echo 'all'          > /sim/paint/textures/unbind   # == echo 1 > /sim/paint/textures/clear
rm /sim/paint/textures/file/mossy-rock.png    # evict (unbinds first)
```

HTTP and MQTT parity come free from the existing transport machinery, exactly as they did for
`/sim/audio` — with the same single exception: **binary uploads** take dedicated routes,
`GET /v1/paint/texture/files` and `PUT|POST|DELETE /v1/paint/texture/file/{name}[?offset=N&complete=0|1]`
(`/v1/paint/textures/...` is accepted as an alias). Unlike the audio route, this one explicitly
answers **413 EFBIG** when `Content-Length` exceeds the server's 1 MiB request cap instead of
silently committing an empty body — PNGs routinely exceed 1 MiB, so chunking is the normal path.
MQTT carries no binary upload.

Session-only, like all of paint. Nothing is written to the game's asset directories, ever.

---

## Research base (verified 2026-08-22 against `ksa-game-assemblies/current`)

### The three things that make this much easier than vehicle paint

**1. PNG decoding already ships in the engine.** `Brutal.TextureApi.Stb` is linked in, and
`TextureLoader.FormatType` (`Brutal.TextureApi/TextureLoader.cs:14-24`) enumerates
`Bmp, Dds, Hdr, Jpg, Kmg, Ktx, Ktx2, Png, Tga`. `TextureLoader.LoadFromMemory(ReadOnlySpan<byte>,
FormatType, settings)` (`:130`) decodes from a byte span — no temp file, no new decoder
dependency, no new NuGet reference. `TextureReference.DoLoad` already uses the settings pair we
want: `TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, KtxTranscodeFmt.Rgba32)`.

**2. The bindless table is public and re-pointable in place.**
`Program.Instance.BindlessTextures` is a **public field** (`KSA/Program.cs:89`), constructed with
`maxTextures: 1024` (`:774`). `BindlessTextureLibrary.SetTexture(int handle, VkImageView)`
(`RenderCore.Systems/BindlessTextureLibrary.cs:174`) rewrites one slot under a lock, and the
descriptor set layout is built with `UpdateAfterBindBit | PartiallyBoundBit` (`:95-97`) — so the
write is legal while the set is bound and frames are in flight.

**3. The original is recoverable for free.** `TextureReference.ImageView` and
`TextureReference.BindlessHandle` are both public properties (`KSA/TextureReference.cs:66-69`).
Restoring is `SetTexture(handle, originalImageView)`.

That last point is the crucial asymmetry against EVA kitten paint. `PAINT_ASBUILT.md` had to build
a whole clone pool with reference counting because KSA's `MaterialData` buffer is device-local and
cannot legally be read back. Here the "original" is a live C# object property. **No clone pool, no
reference counting, no read-back problem.**

### Discovery needs no new reflection

`PlanetRenderer.GroundClutterRenderer` is a public property (`KSA/PlanetRenderer.cs:366`), and
gatOS already reaches `PlanetRenderer` through the existing `FxReflect.Terrain(out error)` anchor
used by `TerrainActuator`. From there everything is public:

```
GroundClutterRenderer.CelestialsWithGroundClutter   // public readonly List<Celestial>  (:247)
  → celestial.BodyTemplate.GroundClutterReference.Ecotypes
      → ClutterEcotypeReference.Name                 // public string  (ClutterEcotypeReference.cs:14)
      → .MaterialReferences                          // public List<GroundClutterMaterialReference> (:23)
GroundClutterRenderer.UniqueMaterials                // public readonly Dictionary<KeyHash, …> (:167)
  → GroundClutterMaterialReference
      .DiffuseReference / .NormalReference / .PBRMap // PbrMaterialReference.cs:10-16
      .OpacityMap / .ThicknessMap                    // GroundClutterMaterialReference.cs:26-29
        → TextureReference { Id, Width, Height, BindlessHandle, Texture.Format, Texture.MipMapCount }
```

`UniqueMaterials` is the deduplicated set actually uploaded to the GPU, so it is exactly the right
thing to enumerate. **The feature adds zero new `KsaAnchor` reflection sites.**

### gatOS already uploads a texture

`ThugLifeTextureFactory.UploadPixels` (`gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeTextureFactory.cs:56-75`)
does the whole discrete-submit dance today and ships:

```csharp
using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
var cmd = stagingPool.NextCommandBuffer();
cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
… VkUtils.UploadBufferToImage(cmd, in src, Texture.ImageEx.AllocationInfo, mipSizes);
cmd.End();
stagingPool.Submit().Wait();
```

For this feature we do not even need to hand-roll the upload: the public ctor
`SimpleVkTexture(IImageAllocator allocator, StagingPool stagingPool, TextureAsset asset,
CreateOptions options)` (`RenderCore/SimpleVkTexture.cs:245`) does decode-to-GPU including mip
generation, and is what `TextureReference.Bind` itself calls.

---

## The finding that changes what "load a custom PNG" means

**Clutter diffuse textures are not albedo maps.** `Solid.frag:284-300`:

```glsl
// Alpha = 1 is linear and will perturb the terrain colour
// Alpha = 0 is sRGB and should use the exact texture colour
vec4 diffuseSample = texture(sampler2D(globalTextures[materialData.diffuseTextureId], …), inUv);
vec3 diffuseSrgb  = pow(diffuseSample.rgb, vec3(2.2));
diffuseSample.rgb = mix(diffuseSrgb, diffuseSample.rgb, diffuseSample.a);
diffuseSample.rgb = diffuseSample.rgb * 2 - 1;
diffuseSample.rgb += 1;                                    // net effect: rgb * 2
vec3 groundColor  = mix(vec3(averageLuminosity), inColor, diffuseSample.a);
diffuseSample.rgb *= (groundColor / averageLuminosity);
```

Reduced, the effective surface color is:

```
albedo = 2 · decode(t.rgb, t.a) · mix(meanLum, instanceColor, t.a) / meanLum
```

Three consequences of that formula:

- **Mid-grey `0.5` is neutral, not black.** The texture is a modulation map centered on 1.0, not a
  base color. Bytes uploaded untouched make a naive photo-derived PNG come out ~2× too bright.
- **Alpha is not opacity.** It is a *dual* control: colour-space selector (0 = sRGB, 1 = linear)
  *and* the blend weight toward the per-instance terrain-derived tint `inColor`. Actual cutout
  opacity lives in a separate `opacityTextureId`. Leaving `A=255` because "it's opaque" opts the
  texture into full terrain tinting — the same image then reads differently in every biome.
- **Mips are mandatory.** Stock clutter textures are BC-compressed with full mip chains and are
  sampled at range. A 1-mip replacement aliases violently at distance, so `fillMipChain: true` is
  not optional.

**As built, this is solved in-product rather than documented around.** The plan's original answer was
to *tell* the user (docs lead with the caveat, the caveat lives in `/sim/paint/textures/help`, plus a
possible `--neutralize` helper). What shipped instead is a **bind mode**, the optional third token of
`bind` — `echo '<texture-id> <file> [faithful|raw]' > bind`, riding `SimCommand.Value` (0/1) so the
canonical envelope needed no new slot, and appearing as a third column on `bindings` so the row stays
echo-symmetric:

- **`faithful` — the default.** gatOS rewrites the decoded pixels before upload, so an ordinary sRGB
  PNG renders as authored and no user has to be taught the formula. Both halves are required: RGB is
  scaled by `2^(-1/2.2)` ≈ `0.7297` to cancel the `×2` (white `255` stores as `186`; the round-trip
  error is under 0.2% and is entirely 8-bit quantization), and alpha is cleared to `0`, which selects
  the sRGB-decode path **and** collapses `mix(meanLum, inColor, a) / meanLum` to exactly `1` — so the
  image is not also recoloured by whatever biome the clutter stands in. Uniform alpha additionally
  keeps the generated mip chain from averaging between the two decode conventions. The correction is
  in-place over the decoder's own buffer (no copy), and the channel mapping itself is game-free and
  unit-tested against a reduced model of the shader: `TextureStore.FaithfulScale`, tests in
  `gatOS.SimFs.Tests/Paint/TextureStoreTests.cs`.
- **`raw` — the deliberate opt-out.** The decoded bytes go up untouched and the image is interpreted
  exactly as one of KSA's own clutter textures (linear, doubled, biome-tinted at `A=1`). The mode for
  replacing a stock texture like-for-like, and the mode the three consequences above still describe.

A non-RGBA8 decode (some ktx/dds/hdr) cannot be corrected; `MakeFaithful` throws and the binding
lands as a `failed` row in `applied` whose error names `raw` as the fix, with the stock texture still
drawn. Re-binding the same pair in the other mode bumps the reconcile revision, because the uploaded
pixels genuinely differ.

The shader facts are still documented — in `/sim/paint/textures/help`, the SPECs, the site reference,
and the `gatos` skill — but as *why `faithful` is the default and when you would want `raw`*, not as
a trap the reader has to work around. There is still no per-binding `alpha_semantics` field on
`status`: the note is identical for every binding, so a per-row copy would be noise; `status` carries
only live facts (`available bound applied catalog retiring vram_bytes revision error`), and the mode
is visible where it belongs, on `bindings`.

---

## Design

### Chosen mechanism: re-point the bindless slot

Bind = `BindlessTextures.SetTexture(stockRef.BindlessHandle, ourImageView)`.
Unbind = `BindlessTextures.SetTexture(stockRef.BindlessHandle, stockRef.ImageView)`.

One descriptor write each way. No buffer rebuild, no pipeline rebuild, no
`RendererRebuildNeeded`, no shader transform, **and no new bindless slots consumed** — so the 1024
`MaxTextures` ceiling is untouched and the only budget that matters is VRAM.

**Granularity, stated honestly:** this replaces a *texture asset*, so every material referencing
that asset changes. For clutter that is usually exactly the intent ("replace the rock diffuse"),
but the `ls` listing must show usage counts so a shared asset is visible before binding, not
surprising after.

### Rejected: patching `GroundClutterGpuMaterial.DiffuseId`

Per-*material* rather than per-*asset* granularity, but:

- `GroundClutterRenderer.BuildMaterialBuffer` **reallocates** `_materialBuffer` on every call
  (`:497`) without disposing the old buffer and without rewriting the descriptor that points at
  it. Re-calling it leaks and leaves a stale binding. It is not a repopulate.
- The buffer is device-local `TransferDst|StorageBuffer`, so a targeted partial write needs its own
  staging buffer + barrier, and the originals are not readable back.

Deferred to v2 as the precision upgrade, built as a partial copy into the *existing* buffer at
`GetMaterialIndex(mat) * sizeof(GroundClutterGpuMaterial)` — never by re-calling `BuildMaterialBuffer`.

### Ownership boundary (mirrors `PAINT_ASBUILT.md`)

| Layer | Owns |
|---|---|
| `gatOS.SimFs/Paint/` (game-free) | `TextureStore` (name validation, byte/count budgets, container sniff, binding table, revision counter), `TextureDirectory` (the writable `file/` dir), `TextureCommands` (the `bind`/`unbind` grammar) |
| `gatOS.SimFs/` | `/sim/paint/textures` tree (`SimFsTree.TexturePaintRootDir`) + the three `paint.texture_*` catalog descriptors + `Formats` row renderers |
| `gatOS.Mcp/` | `gatos.paint_texture` (new store tool), `gatos.paint_control` extension, `feature="paint_textures"` |
| `gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs` | the **only** KSA-aware file: catalog discovery, decode, `SimpleVkTexture`, `SetTexture`, retire queue |

**Where the store lives, and why it is not `gatOS.Paint/`.** The original plan put `TextureStore` in
`gatOS.Paint/`. It ships in **`gatOS.SimFs/Paint/`** instead: `gatOS.Paint` deliberately carries
**zero project references** (it is pure rules + GLSL text), while the store is a VFS blob store that
needs `gatOS.NineP`'s `Qid`/`VfsNode` types — and every other blob store gatOS has (audio clips,
camera tracks) already lives under `gatOS.SimFs/`. It is no less game-free and no less unit-testable
for living there.

---

## Phases

### P1 — store + VFS surface (no game code) — **BUILT**

`gatOS.SimFs/Paint/TextureStore.cs` modelled directly on `AudioStore` (`gatOS.SimFs/Audio/AudioStore.cs:69`):
in-memory uploads, `IsValidName`, per-file and total byte caps, a container sniff at commit, and a
`Revision` counter that bumps **only** on a real desired-state change. `TextureDirectory` mirrors
`AudioDirectory` including `OpenWrite`/`Remove`; entries are `IsStreaming=true`, so they stay out of
the MQTT scalar mirror and bulk walks. Config keys alongside `paint_max_material_clones`, all flat in
the `[paint]` section:

| Key | Default | Clamp |
|---|---|---|
| `paint_textures_enabled` | `true` | — (false removes the subtree entirely) |
| `paint_texture_max_bytes` | 16 MiB | 64 KiB .. 256 MiB |
| `paint_texture_max_total_bytes` | 128 MiB | >= per-file .. `int.Max` |
| `paint_texture_max_files` | 32 | 1 .. 256 |
| `paint_texture_max_bindings` | 32 | 1 .. 256 |
| `paint_texture_max_dimension` | 4096 | 16 .. 16384 |

`paint_textures_enabled` is a **boot-time wiring decision, not a runtime master switch**. Parts/kitten
paint needs a runtime master because its steady state costs patches and clones; this feature's idle
cost is one `Revision` comparison per frame, so a master switch would buy nothing. That is a
deliberate divergence from the paint precedent.

Fully unit-testable with zero game references, like the rest of the game-free layer.

### P2 — read-only discovery — **BUILT**

`/sim/paint/textures/clutter` (a flat listing file, not a directory tree) renders one row per
overridable stock texture from `FxReflect.Terrain()?.GroundClutterRenderer`. Read-only, so it can
land and be validated in-game before anything mutates GPU state. Each row is
`<texture-id> <slot> <w> <h> <mips> <used_by> <ecotypes-csv>`, where `slot` is
`diffuse|normal|pbr|opacity|thickness`, `used_by` is the number of distinct material slots sharing
the asset, and `ecotypes` renders `-` when none. The bindless handle and the Vulkan format are held
internally rather than published — neither is actionable from userland, and the handle is an
implementation detail of the swap.

Degrade path reuses `FxReflect.Degrade`/`Healthy` so a build where the property moves reports
`degraded` instead of throwing — same contract as the FX editors.

### P3 — bind / unbind / clear — **BUILT**

`ClutterTextureBridge` on the game thread only (the Frame command drain, same place `TerrainActuator`
runs — never an HTTP/MQTT thread):

1. Sniff the container from the upload's magic bytes → an unrecognised container is `EINVAL`.
2. `TextureLoader.LoadFromMemory(bytes, fmt, TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32))`.
   Images larger than `paint_texture_max_dimension` are **downscaled, not rejected**; NPOT is fine.
   Three non-obvious KSA contracts are recorded at the anchor: `TextureAsset.FilePath` must be
   non-empty (the ctor throws), `LoadOptions` forces 4 channels (a 3-channel PNG would otherwise
   decode to an unsupported `R8G8B8_UNORM`), and the decoded `ITexture` is neither `IDisposable` nor
   finalized, so `Destroy()` must be called explicitly.
2b. In `faithful` mode (the default) `MakeFaithful` rewrites the decoded RGBA8 pixels in place —
   RGB through `TextureStore.FaithfulScale`, alpha cleared — so the image renders as authored; a
   decode that is not RGBA8 throws, naming `raw` as the fix, and the binding reports `failed`. In
   `raw` mode the buffer is uploaded untouched.
3. `new SimpleVkTexture(renderer.Allocator, stagingPool, asset, new CreateOptions(maxDim,
   ReductionMethod.Downsample, fillMipChain: true))` inside a `using` staging pool, exactly as
   `ThugLifeTextureFactory` does.
4. Record `(textureRefId, stockHandle, stockImageView)` **before** the first swap — this is the
   `FxPristine.Capture` analogue and the whole restore story.
5. `BindlessTextures.SetTexture(stockHandle, ours.ImageView)`.

Unbind restores the recorded `stockImageView`, then **defers disposal**.

The command grammar landed with **three** action keys, not two: `paint.texture_bind`
(`Token=<stock texture id>`, `Aux=<uploaded file name>`, `Value=0` faithful / `1` raw),
`paint.texture_unbind`
(`Token=<stock texture id>`), and `paint.texture_clear` (the global teardown, `value=1`). Both
`echo all > unbind` and the `clear` trigger normalize to `paint.texture_clear`, so the two spellings
cannot drift. The gate string is **`control_enabled + paint textures store`** — a new gate, distinct
from paint's `control_enabled + paint runtime master`, precisely because there is no runtime master.
Health latches: `paint.clutter_catalog`, `paint.texture_upload`.

### P4 — retire queue (the actual hazard) — **BUILT**

Creating and re-pointing are safe. **Destroying is not**: a `VkImage` still referenced by an
in-flight frame must not be destroyed. Never `Dispose()` inline on unbind. Push to a retire queue
and dispose after `MaxFramesInFlight + 1` frame ticks — the same frames-in-flight reasoning
`FrameCapture` already relies on for its readback ring. On mod unload: restore every slot, then
drain the queue after a device idle.

### P5 — MCP + docs lockstep — **BUILT**

`gatos.paint_control` gained `texture_bind`/`texture_unbind`/`texture_clear` plus a new `file`
parameter (mapped to `SimCommand.Aux`), with `target` carrying the stock texture id and `value`
carrying the bind mode (`0` = `faithful`, `1` = `raw`) — no new parameter was needed for it. Store operations
went to a **new tool**, `gatos.paint_texture` (modelled on `gatos.audio_clip`):
`{operation, name?, offset?:0, complete?:true, data_base64?}` over `list`, `catalog`, `bindings`,
`retrieve`, `upload`, `delete`; `retrieve` returns an `EmbeddedResourceBlock` at
`gatos://paint/textures/<name>`. Base64 inflates 4/3 against the 24 MiB framing cap, so uploads
chunk. Read-back is a **separate feature document** — `gatos.get_runtime_state(feature:"paint_textures")`
and `gatos://runtime/paint_textures` return `{runtime, bindings, applied, clutter, files, revision,
limits}`; `gatos://runtime/paint` is unchanged. Capabilities report `features.paint_textures`. Tool
count went 26 → 27.

Docs: `SPEC_MCP.md` (§1.1, §1.2, §3, §5, §5.1, §6, §6.1, §7), `SPEC_9P_FILESYSTEM.md`,
`docs/VALIDATION.md` checklist, `AGENTS.md`, `docs/MILESTONES.md`, `docs/ARCHITECTURE.md`,
`docs/TUTORIAL_DATA_REFERENCE.md`, `README.md`, the `gatos` skill, and the site reference. The
authoring-math section above is the headline content, not an appendix — it is carried verbatim in
`/sim/paint/textures/help`.

---

## Risks, in order

1. **Out-of-band submit while frames are in flight.** `FrameCapture`'s header states that a private
   command buffer submitted alongside in-flight frames "corrupts the device and crashes the game"
   and that the engine authors prescribed the in-band path. `ThugLifeTextureFactory` nonetheless
   does a discrete `Submit().Wait()` and ships. The reconciliation is that FrameCapture's rule
   governs *per-frame* work touching *in-flight frame resources*, while this is a one-shot upload
   to a fresh image nothing has bound yet — but that reconciliation is reasoning, not evidence.
   **This is validation item #1** — and it shipped unvalidated: it is the first item of the
   `docs/VALIDATION.md` clutter-texture checklist. If it proves unsafe, the fallback is to record the
   upload in-band via the `DisplayRenderPatch` injection point and complete the bind a frame later.
2. **Image destruction timing** (P4). Second most likely crash source; mitigated as built by the
   retire queue (`MaxFramesInFlight + 1`) plus a `WaitIdle()` before the teardown drain. Needs a
   validation-layer run to confirm.
3. **Alpha/gain authoring confusion.** Not a crash, but the most likely source of "it looks wrong"
   reports. Mitigated by docs and the `help` leaf.
4. **Shared-asset surprise.** Mitigated as built by the `used_by` count and the ecotype list in the
   `clutter` listing; fully solved only by v2.
5. **Churn.** Low. The feature adds no reflection anchors, and every API it touches
   (`BindlessTextureLibrary`, `SimpleVkTexture`, `TextureLoader`, `TextureReference`,
   `PlanetRenderer.GroundClutterRenderer`) is public.

## Guard to preserve

`PaintShaderTransform.TryInject` bails when `inStateFlags` is absent
(`gatOS.Paint/PaintShaderTransform.cs:18`). `Solid.frag:300` contains `vec3 sampledColor =
diffuseSample.xyz;` — the exact anchor the transform scans for. That guard is the only thing
keeping vehicle paint out of the clutter shader, and this plan does not touch it. Any future
loosening of the anchor must account for that.

## Out of scope for v1

Per-material overrides (v2); cubemap / terrain biome material textures (a separate plan — those
write a host-visible mapped buffer and are strictly easier); animated or streamed textures; adding
*new* clutter materials or meshes; persisting bindings across sessions.

---

## As built — deltas from the original plan

Everything in the design above shipped as written except these, corrected in place above and listed
here so a reader of the original plan is not misled:

| Planned | As built | Why |
|---|---|---|
| `TextureStore` in `gatOS.Paint/` | `gatOS.SimFs/Paint/{TextureStore,TextureDirectory,TextureCommands}.cs` | `gatOS.Paint` keeps **zero project references** by design; every other blob store (audio, camera tracks) lives in `SimFs`, and the store needs `gatOS.NineP` VFS types. Still game-free and fully unit-tested. |
| `Game/Ksa/Paint/TextureBridge.cs` | `Game/Ksa/Paint/ClutterTextureBridge.cs` | Named for what it bridges; `PaintManager` owns its tick and disposal. |
| `clutter/` as a directory tree with `bind`/`unbind` inside it | `clutter` is a flat listing **file**; `bind`, `unbind`, `clear`, `bindings`, `applied`, `status`, `info`, `help` and `file/` are siblings under `/sim/paint/textures/` | Matches the `/sim/audio` shape and keeps every control leaf at one level for the HTTP/MQTT field mirrors. |
| `bind` line `<id> = <file>` | `bind` line `<id> <file> [faithful\|raw]` | Deliberately identical to a `bindings` row (which grew the same third column), so a listing line can be echoed straight back to re-create the binding. |
| `unbind all` only | `unbind all` **and** a `clear` trigger, both normalizing to `paint.texture_clear` | The trigger-file idiom is what every other gatOS teardown uses; normalizing both spellings to one action key means they cannot drift. |
| Two MCP operations on `paint_control` (`texture_bind`/`texture_unbind`/`texture_list`) | Three control operations (`texture_bind`/`texture_unbind`/`texture_clear`) plus a **separate** `gatos.paint_texture` store tool | Store operations (upload/retrieve/delete/list/catalog) are not commands; `gatos.audio_clip` set the precedent. |
| `gatos://runtime/paint` reports bindings | New `paint_textures` feature document (`gatos://runtime/paint_textures`) | The paint snapshot is an immutable rules projection; texture state is game-thread runtime state with a different lifetime. |
| Clutter row carries format + bindless handle | Row is `<texture-id> <slot> <w> <h> <mips> <used_by> <ecotypes-csv>` | Neither the format nor the handle is actionable from userland. |
| Config: three keys | Six keys, including `paint_textures_enabled` and explicit file/binding counts | Cap every axis the store can exhaust. |
| (unstated) a runtime master switch, by analogy with paint | **No runtime master switch** | Idle cost with nothing bound is one `Revision` comparison per frame; a master would gate nothing. `paint_textures_enabled` is a boot-time wiring decision that removes the subtree. |
| HTTP parity "free" | Free for every control/read leaf, but binary uploads take dedicated `/v1/paint/texture/file/{name}` routes that **413 EFBIG** on an oversize `Content-Length` | The second transport-parity exception after audio; unlike audio it refuses rather than silently committing an empty body. |
| Reject oversize / NPOT images | Oversize images are **downscaled** to `paint_texture_max_dimension`; NPOT is accepted | The decoder handles both, and rejecting a user's photo for being large is hostile. |
| `status` surfaces a per-binding `alpha_semantics` note | No such field. The mode is a column on `bindings`; the shader explanation lives in `/sim/paint/textures/help` | Identical for every binding; `status` stays live facts only. |
| The authoring caveat is **documented**, with a possible `--neutralize` helper | The caveat is **solved in-product**: `bind` takes an optional third token, `faithful` (default) \| `raw`. `faithful` scales RGB by `2^(-1/2.2)` and clears alpha, so a plain sRGB PNG renders as authored and untinted by the biome; `raw` is the like-for-like stock replacement | A correction every user would otherwise have to apply by hand belongs in the product, not in a warning box. It rides `SimCommand.Value` (0/1), so the canonical envelope gained no slot, and it shows as a third `bindings` column, so the row stays echo-symmetric with `bind`. The mapping is game-free (`TextureStore.FaithfulScale`) and unit-tested against a reduced model of `Solid.frag`. |

Two `KsaAnchor` sites were added, both inside `ClutterTextureBridge` — Risk=High for the
bindless/decode/upload path (`BindlessTextures.SetTexture`, `TextureLoader.LoadFromMemory`,
`TextureAsset.LoadOptions`, `SimpleVkTexture`, `Renderer.Allocator.CreateStagingPool`,
`TextureReference.ImageView`) and Risk=Medium for catalog discovery
(`PlanetRenderer.GroundClutterRenderer` → `CelestialsWithGroundClutter` → ecotypes → material refs).
The second reuses the existing `FxReflect.Terrain` accessor, so the prediction that the feature adds
**no new reflection site** held.
