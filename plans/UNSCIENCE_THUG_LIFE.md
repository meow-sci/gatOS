# Plan: `thug_life` debug feature (anchored sunglasses quad)

Status: **implemented** — code-complete, build + tests green (8 projects, +15 thug-life tree tests);
in-game validation pending (§9, recorded in `docs/VALIDATION.md`). Author pass: 2026-06-28.

Bring the sibling **unscience** mod's *thug-life* meme into gatOS, exposed **only** through the gatOS
surfaces (`/sim` 9p + HTTP `/v1` + MQTT) — **no ImGui UI**. It lives under `/sim/debug` because it is
a pure cosmetic cheat.

**What it does:** renders a flat, world-space textured quad of the "thug life" sunglasses meme (blocky
black lenses with white glare dots) anchored to a chosen **part** on a vehicle, tracking that part every
frame, with per-entry position/rotation offset (in the part's local frame), size (width/height), and a
visible toggle. Players discover parts under `/sim/vessels/by-id/<id>/parts/` (the listing the welds
feature already added) and create/tune sunglasses via `/sim/debug/thug_life/`.

This is the third unscience port, after `welds` and `always_render_iva` (see
[`IVA_AND_WELDING.md`](IVA_AND_WELDING.md)). It reuses every pattern those established — the
`/sim/debug` registry, the `SimCommand`/`CommandQueue` write path, the snapshot projection, the
dynamic-Harmony-only-while-active discipline, the per-vehicle parts listing — and adds **one genuinely
new capability to gatOS: custom GPU rendering** (a Vulkan quad drawn into KSA's scene). That is the
defining risk and the reason this plan is careful about coupling and teardown.

> **Naming:** the feature is **thug-life** everywhere a human reads it, **`thug_life`** in `/sim`
> paths / action keys (snake_case, like every other `/sim` node), and **`ThugLife`** in C# namespaces /
> types. (The user named it directly; unlike garrys-torch→welds there is no rename — just the casing
> convention above.)

---

## 0. Source-of-truth research (verified, with file:line)

### 0.1 unscience — the thug-life renderer (`thug-life.lib/`)

Six files; the UI (`ThugLifeSubmod.cs`) and host (`thug-life/Mod.cs`, `Patcher.cs`) are **excluded**
(ImGui + hotkeys). The reusable core we port:

- **`ThugLifeTexturePattern.cs`** — a static `26×5` char grid (`'#'`=black, `'W'`=white glare,
  `'.'`=transparent). Pure data; **port verbatim**.
- **`ThugLifeTextureFactory.cs`** — builds an `R8G8B8A8UNorm` `26×5` texture from the pattern + a
  nearest-filter `ClampToEdge` sampler. `R8G8B8A8UNorm` (not SRGB) is deliberate: `UnlitMesh.frag`
  already does one `gammaToLinear()` decode, and an SRGB source would double-decode (darker). **Port.**
- **`ThugLifeQuadRenderer.cs`** (`unsafe`) — owns the GPU pipeline, descriptor set (1 combined
  image-sampler), pipeline layout (one `float4x4` vertex push-constant = MVP), and vertex/index
  buffers. **Geometry is one tiny quad per *opaque* pixel** of the pattern (transparent pixels emit no
  geometry → the blocky cut-out shape; necessary because `UnlitMesh.frag` hard-writes `alpha = 1.0`, so
  alpha-blend transparency is unavailable). `RecordDraw(cmd, entry)` rebuilds the model matrix per entry
  per frame and records one `DrawIndexed`. **Port** (this is the bulk of the new code).
- **`ThugLifeEntry.cs`** — data model: `Vehicle`, `Part` (anchor), `float3 Position` (part-local m),
  `float3 Rotation` (pitch/yaw/roll deg), `float Width`, `float Height`, `bool Visible`. unscience also
  has no Scale beyond width/height and **no animation** — nothing extra to drop. **Port** (adapted: add
  a stable int `Id` + `PartInstanceId` for the filesystem handle; keep vehicle/part as re-resolvable refs).
- **`ThugLifeRenderManager.cs`** — holds the entry list + GPU resources; static `Active`/`Instance` the
  render postfix reads; `RecordDraws(cmd)` iterates entries. **Port + fold in the registry/lifecycle**
  (gatOS's manager also does add/remove/edit, snapshot projection, liveness validation, and dynamic
  patch/GPU lifecycle — the union of unscience's `ThugLifeRenderManager` and its UI's create/edit logic).
- **`ThugLifeRenderPatches.cs`** — Harmony **postfix on `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`**
  (the only injection point into the already-begun offscreen scene pass). **Port** (made dynamic — §5.1).

unscience's host (`thug-life/Mod.cs`) constructs the manager (→ `Program.GetRenderer()`) and applies the
render patch **at `[StarMapAllModsLoaded]` (OnFullyLoaded)**, confirming the renderer is live by then.

### 0.2 The gatOS quad-rendering skill (`.claude/skills/ksa/quad.md`)

The repo already carries a **curated, working-mod-derived how-to** for exactly this (anchoring a textured
quad to a part). It is the authoritative reference and matches the unscience code. Load-bearing facts:

- **`Program.GetRenderer()` must be live → call from `OnFullyLoaded` or later, never `OnImmediateLoad`.**
- **Bind the pipeline to `Program.OffScreenPass`, NOT `Program.MainPass`** (the scene draws in the
  offscreen MSAA pass; MainPass is the 1-sample swapchain pass → silently broken depth).
- **`RasterizationSamples = Program.OffScreenPass.SampleCount`** (match the active framebuffer incl. MSAA).
- **`RenderingPresets.ReverseZDepthStencil.DepthTestWrite`** (KSA's offscreen pass is reverse-Z).
- **`Presets.Rasterization.Fill.CullNone`** (double-sided — the user controls orientation).
- Reuse KSA's shipped **`UnlitMeshVert`/`UnlitMeshFrag`** via `ModLibrary.Get<ShaderReference>(...)`;
  vertex input `vec3 pos`@0 + `vec2 uv`@1 (stride 20); one `float4x4` MVP push-constant (vertex stage);
  one combined-image-sampler @set0/binding0 (fragment). **Do not destroy the shader modules** —
  `ModLibrary` owns them; dispose only the pipeline, layout, pool, set-layout, buffers, sampler.
- **MVP = `modelEgo * camera.MVP.viewProjection`** (row-vector convention; the scene renders in
  **ego space** — camera-centred — so the model matrix must be ego-space too).
- **Threading:** *"Static flags (`Active`, `QuadInstance`) are toggled from your manager on the main
  thread; the postfix reads them on the same thread inside the render loop, so no synchronization is
  needed."* → **`RenderMainPass` runs on the main/game thread**, the same thread as the StarMap GUI
  hooks and our command drain. (We still publish an immutable entry array and gate on a volatile
  `Active` flag — cheap belt-and-suspenders, and it makes the dispose-on-teardown ordering bulletproof.)
- Wrap `RecordDraw` in try/catch and **disable on first failure** (a per-frame render exception would
  spam logs and can corrupt Vulkan state). On disable, **clear `Active` before disposing GPU resources**.

### 0.3 Current KSA APIs (verified in `ksa-game-assemblies`, build 2026.6.9.4750, 2026-06-28)

| Need | API | file:line | status |
|---|---|---|---|
| Patch target | `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)` (KSA) | `KSA/SuperMeshRenderSystem.cs:329` | ✓ |
| Renderer | `Program.GetRenderer()` → `Core.Renderer` | `KSA/Program.cs:450` | ✓ |
| Camera + MVP | `Program.GetMainCamera()` → `KSA.Camera`; `Camera.MVP` → `ViewProjection`; `.viewProjection` (`float4x4`) | `KSA/Program.cs:489`, `Camera.cs:53`, `ViewProjection.cs:13` | ✓ |
| Offscreen pass | `Program.OffScreenPass` → `Core.RenderPassState`; `.Pass` (`VkRenderPass`), `.SampleCount` (`VkSampleCountFlags`) | `KSA/Program.cs:375`, `Core/RenderPassState.cs:11,26` | ✓ |
| Viewport | `Program.SetViewport(CommandBuffer)` (static) | `KSA/Program.cs:3781` | ✓ |
| Editor / viewport | `Program.Editor` (`VehicleEditor?`), `Program.MainViewport` (`Viewport`) | `KSA/Program.cs:194,403` | ✓ (not needed for thug-life draw) |
| Shaders | `ModLibrary.Get<T>(string) where T:IKeyed`; `ShaderReference` (impl. `→ VkShaderModule`); keys `"UnlitMeshVert"`/`"UnlitMeshFrag"` | `KSA/ModLibrary.cs:968`, `KSA/ShaderReference.cs:20`; shaders `Content/Core/Shaders/Mesh/UnlitMesh.{vert,frag}` | ✓ (keys resolved at runtime; shaders ship) |
| Shader stages | `RenderTechnique.CreateShaderStages(Device, Span<ShaderReference>, …)` (RenderCore) | `Planet.Render.Core: RenderCore/RenderTechnique.cs:37` | ✓ |
| Frag behavior | `UnlitMesh.frag`: `gammaToLinear()` decode + `outColor=vec4(rgb,1.0)` (alpha forced) | `Content/Core/Shaders/Mesh/UnlitMesh.frag:13,16` | ✓ confirms cut-out-via-geometry |
| Anchor math | `Vehicle.GetMatrixAsmb2Ego(Camera)` → `double4x4`; `Vehicle.Asmb2Ego` (`doubleQuat`) | `KSA/Vehicle.cs:833,449` | ✓ |
| | `Part.PositionEgo(ref readonly double4x4)` → `double3`; `Part.Asmb2Ego(doubleQuat)` → `doubleQuat` | `KSA/Part.cs:677,682` | ⚠ `PositionEgo` param is `ref readonly` (call with `in`) |
| Part listing | `Part.{InstanceId(uint),Id,DisplayName,Template.Id,PartParent,SubParts,PositionVehicleAsmb}`; `Vehicle.Parts`→`PartTree.Parts`(`ReadOnlySpan<Part>`),`.Count` | `KSA/Part.cs:321,411,413,323,381,655,415`, `PartTree.cs:67,65` | ✓ (already used by welds/PartsReader) |
| GPU types | `Renderer`,`RenderPassState`,`SimpleVkTexture`,`RenderTechnique`,`VkUtils` (`Core`/`RenderCore`) | `Planet.Render.Core.dll` | ✓ |
| | `DeviceEx`,`BufferEx`,`DescriptorSetLayoutEx`,`DescriptorPoolEx`,`VertexInput`,`Presets` (`Brutal.VulkanApi.Abstractions`) | `Brutal.Vulkan.Abstractions.dll` | ✓ |
| | `Vk*` structs/enums (`Brutal.VulkanApi`) | `Brutal.Vulkan.dll` | ✓ |
| | `ByteSize`/pointer ext. (`Brutal`/`Brutal.Pointers.Extensions`) | `Brutal.Core.Memory.dll` | ✓ |

**DLLs present in `…/ksa-game-assemblies/current/dll/`** (verified): `Brutal.Vulkan.dll`,
`Brutal.Vulkan.Abstractions.dll`, `Brutal.Vulkan.Vma.dll`, `Brutal.Core.Memory.dll`,
`Planet.Render.Core.dll`, `Brutal.Core.Numerics.dll`, `KSA.dll`, … (the exact set the unscience
`thug-life.lib.csproj` references). **There is no standalone `Core.dll`/`RenderCore.dll`** — the
`Core` and `RenderCore` namespaces live inside `Planet.Render.Core.dll`.

### 0.4 gatOS integration points (verified, with file:line)

- **Write pipeline / control archetypes / `/sim/debug` gating** — identical to welds (see
  `IVA_AND_WELDING.md §0.4`): `SimCommand`→`CommandQueue`→`KsaCatalog.Execute/Dispatch`→actuators;
  `LineControlFile`/`VectorControlFile`/`ControlFile.Flag`/`TriggerFile`; `DebugDir()`
  (`SimFsTree.cs:539`) gated by `_commands is { DebugEnabled: true }`. A **global** control passes
  `vesselId = ""`.
- **Vessel-agnostic actions** are dispatched in `KsaCatalog.Execute` **before** vehicle resolution
  (`KsaCatalog.cs:31-52`): `debug.warp`, `debug.always_render_iva`, `debug.weld_clear`, `camera.focus`.
  Every `debug.thug_life_*` action joins them (they are **registry-keyed by entry id in `Ordinal`**, not
  vessel-keyed — the anchor vehicle is resolved inside the actuator from the add command's `Token`).
- **Snapshot** (`SimSnapshot.cs`): `SimSnapshot` already carries `Welds`/`AlwaysRenderIva`; add a
  `ThugLife` list the same way (init-only, default `[]` → no construction-site churn).
- **Sampler** (`TelemetrySampler.cs:139-150`): already projects `Welds = _welds.Snapshot()` and
  `AlwaysRenderIva = IvaForceRender.Enabled` into the snapshot — add `ThugLife = _thugLife.Snapshot()`.
- **Lifecycle** (`Mod.cs`): `OnFullyLoaded` (init), `OnBeforeUi` (`SampleTelemetry`+`DrainCommands`,
  game thread), `OnAfterUi` (`DriveWelds`+UI), `Unload` (`TeardownGameCheats`). `EnsureControlObjects`
  (`Mod.Game.cs:208`) lazily builds `_health`/`_weldManager`/`_catalog`. We add `_thugLife` there and a
  game-thread `UpdateThugLife()` validation seam (drop dead entries, re-resolve parts) + teardown.
- **Transport parity (free):** HTTP `/v1/fs/<path>` and MQTT `gatos/sim/<path>` mirror every
  non-streaming `/sim` leaf by walking the one tree; full-snapshot JSON via `SimJson` (add `thug_life`
  there). The frozen compact `Formats.VesselTelemetry` is untouched.

---

## 1. Scope

**In:**
- `/sim/debug/thug_life/` registry: `add`, `clear`, `count`, and per-entry
  `<id>/{vessel,part,position,rotation,size,visible,remove,spec}`.
- Procedural sunglasses texture (`26×5`) + per-opaque-pixel cut-out quad geometry.
- Anchoring to a **top-level part** by `instance_id` (the welds `parts/` listing), or `0` = the
  vehicle's body/assembly frame.
- A **dynamically installed** render postfix on `SuperMeshRenderSystem.RenderMainPass` — present only
  while ≥1 entry exists; lazy GPU init on the first entry; GPU dispose + unpatch when the last entry is
  removed and at unload.
- Snapshot projection (`ThugLifeSnapshot`) → all three transports (free via parity).
- SPEC + scope + matrix + ARCHITECTURE + CLAUDE.md + MILESTONES + VALIDATION + gatos-skill updates;
  game-free tests; an in-game validation checklist.

**Out (explicitly):**
- No ImGui / menu / hotkey surface.
- **No subpart anchoring / subpart listing in v1.** Anchor is a top-level `Part` (instance_id) or `0`
  = body frame — exactly the welds anchor model, reusing its `parts/` listing unchanged. (unscience
  offers an optional subpart; deferred — see §11. The common case, sunglasses on a command pod, is a
  top-level part, and a part-local position offset reaches any spot on it.)
- No animation, no scale beyond width/height.
- No persistence across save/load (live runtime cheat; cleared on unload — like welds).
- No ray-picking / mouse interaction (that is a UI concern; gatOS is headless).
- Not added to the frozen compact `telemetry` doc (tree + full-JSON only, like parts/welds).

---

## 2. The `/sim` surface (authoritative — mirror into SPEC in lockstep)

A **global registry** under `/sim/debug/thug_life/` (the anchor model is many-quads-per-vehicle, so a
vessel id is not a unique key like a weld source is — entries get an integer **id**: the smallest free
slot at create, reused after `remove`/`clear` so the numbering tracks the live set, not lifetime adds).

```
/sim/debug/thug_life/                 directory: add, clear, count, one <id>/ per entry
/sim/debug/thug_life/add              STATE line  write "<vessel> <part_iid>"
                                                  or  "<vessel> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <w> <h>"
                                                  read "" ; creates a new entry      action debug.thug_life_add
/sim/debug/thug_life/clear            TRIGGER  write 1 → remove ALL entries          action debug.thug_life_clear
/sim/debug/thug_life/count            read  number of active entries
/sim/debug/thug_life/<id>/vessel      read  anchor vessel id
/sim/debug/thug_life/<id>/part        read  anchor part instance_id (0 = body frame)
/sim/debug/thug_life/<id>/position    STATE vec3  read/write "x y z"   (part-local m) action debug.thug_life_position
/sim/debug/thug_life/<id>/rotation    STATE vec3  read/write "pitch yaw roll" (deg)   action debug.thug_life_rotation
/sim/debug/thug_life/<id>/size        STATE vec2  read/write "width height" (m)       action debug.thug_life_size
/sim/debug/thug_life/<id>/visible     STATE flag  read/write 0|1                      action debug.thug_life_visible
/sim/debug/thug_life/<id>/remove      TRIGGER  write 1 → remove this entry            action debug.thug_life_remove
/sim/debug/thug_life/<id>/spec        read  full write-compatible spec (echo to add)
```

- `<part_iid>` = a part `instance_id` from `/sim/vessels/by-id/<id>/parts/<n>/instance_id`, or `0` to
  anchor at the vehicle's body/assembly frame.
- `add` with 2 tokens uses defaults: position `0 0 0`, rotation `0 0 0`, width `0.975`, height `0.1875`
  (unscience defaults — keeps the texture's `26:5` aspect at a uniform block size). With 10 tokens, all
  explicit. Any other arity / non-finite number / non-integer-negative `part_iid` → `EINVAL`.
- `<id>/spec` returns `"<vessel> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <w> <h>"` — echo it to
  `add` to recreate (a *new* id; `spec` is for recreation, live tuning is the per-leaf controls).
- Reads are snapshot-backed (the sampler projects the registry each tick); writes funnel through the one
  `SimCommand`/`CommandQueue`.

**Action-key catalog (all Frame phase; all vessel-agnostic / registry-keyed):**

| action | VesselId | Token | Ordinal | Values | meaning |
|---|---|---|---|---|---|
| `debug.thug_life_add` | — | vessel id | — | `[part_iid,x,y,z,pitch,yaw,roll,w,h]` | create a new entry |
| `debug.thug_life_remove` | — | — | entry id | — | remove one entry |
| `debug.thug_life_clear` | — | — | — | — | remove all entries |
| `debug.thug_life_position` | — | — | entry id | `[x,y,z]` | set position offset |
| `debug.thug_life_rotation` | — | — | entry id | `[pitch,yaw,roll]` | set rotation offset (deg) |
| `debug.thug_life_size` | — | — | entry id | `[width,height]` | set size (m) |
| `debug.thug_life_visible` | — | — | entry id | `Value`=0/1 | show/hide (keeps the entry) |

Errnos (via `CommandResult`): `EACCES` control/debug off, `ENOENT` vessel/part/entry-id gone, `EINVAL`
bad arity/values, `EIO` KSA/GPU fault (e.g. renderer unavailable). All Frame phase (they mutate only the
`ThugLifeManager` registry; the *draw* is the render postfix, not a command — no `SolverActions` entry).

> All of the above MUST be added to **`SPEC_9P_FILESYSTEM.md`** in the same change (the SPEC
> constitution). HTTP `/v1/fs` + MQTT `gatos/sim/` leaf mirrors and `POST /v1/command` / `gatos/command`
> action keys come for free via the transport-parity rule.

---

## 3. Code layout (respecting the dependency + G2 rules)

```
gatOS.SimFs (game-free)
  Snapshots/SimSnapshot.cs        + ThugLifeSnapshot record; SimSnapshot.ThugLife list
  Formats.cs                      + ThugLifeSpec(ThugLifeSnapshot) line formatter (reuse Scalar/Vector/UInt)
  SimJson.cs                      + thug_life array in the full-snapshot JSON (parity)
  SimFsTree.cs                    + ThugLifeDir/ThugLifeEntryDir under DebugDir; add/clear/count;
                                    ParseThugLifeAdd; ThugLife(id) accessor; SanitizedThugLife
  (LineControlFile / VectorControlFile / ControlFile.Flag / TriggerFile — reused as-is)

gatOS.GameMod (game-coupled; KSA/Brutal/Vulkan types ONLY under Game/Ksa/)
  Game/Ksa/ThugLife/ThugLifeTexturePattern.cs   NEW: the 26×5 char grid (verbatim)
  Game/Ksa/ThugLife/ThugLifeTextureFactory.cs   NEW: R8G8B8A8 texture + sampler (port)
  Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs     NEW: GPU pipeline/buffers/descriptor + RecordDraw (unsafe; port)
  Game/Ksa/ThugLife/ThugLifeEntry.cs            NEW: entry (Id, VesselId, PartInstanceId, Vehicle, Part, transform, Visible)
  Game/Ksa/ThugLife/ThugLifeRenderPatches.cs    NEW: dynamic Harmony postfix on SuperMeshRenderSystem.RenderMainPass
  Game/Ksa/ThugLife/ThugLifeManager.cs          NEW: registry + GPU lifecycle + dynamic patch + RecordDraws + Snapshot + Validate
  Game/Ksa/Actuators/ThugLifeActuator.cs        NEW: Add/Remove/Clear/SetPosition/Rotation/Size/Visible → ThugLifeManager (game thread)
  Game/Ksa/KsaCatalog.cs                        + vessel-agnostic dispatch of the 7 debug.thug_life_* actions ([KsaAnchor] on KSA calls)
  Game/TelemetrySampler.cs                      + ThugLife = _thugLife.Snapshot() in the published snapshot
  Mod.cs / Game/Mod.Game.cs                     + _thugLife lifecycle: create in EnsureControlObjects/sampler; UpdateThugLife() in
                                                  OnBeforeUi; clear+dispose+unpatch in TeardownGameCheats
  gatOS.GameMod.csproj                          + Brutal.Vulkan, Brutal.Vulkan.Abstractions, Brutal.Vulkan.Vma,
                                                  Brutal.Core.Memory, Planet.Render.Core refs; AllowUnsafeBlocks=true
```

`[KsaAnchor]` every KSA/Brutal-render member touched, all under `Game/Ksa/ThugLife/` +
`ThugLifeActuator` + the `KsaCatalog` dispatch. Update `docs/KSA_INTEGRATION_MATRIX.md` and the `scope/`
pages alongside (this is the **highest-churn** anchor set gatOS has — render-pipeline internals).

---

## 4. Reads: snapshot projection

### 4.1 Snapshot record (`SimSnapshot.cs`)
```csharp
public sealed record ThugLifeSnapshot(
    int Id, string VesselId, uint PartInstanceId,
    double3Snap Position, double3Snap Rotation, double Width, double Height, bool Visible);

// On SimSnapshot (init-only, default empty — no construction-site churn):
public IReadOnlyList<ThugLifeSnapshot> ThugLife { get; init; } = [];
```

### 4.2 `Formats.ThugLifeSpec`
```csharp
public static string ThugLifeSpec(ThugLifeSnapshot t)
    => $"{t.VesselId} {UInt(t.PartInstanceId)} "
       + $"{Scalar(t.Position.X)} {Scalar(t.Position.Y)} {Scalar(t.Position.Z)} "
       + $"{Scalar(t.Rotation.X)} {Scalar(t.Rotation.Y)} {Scalar(t.Rotation.Z)} "
       + $"{Scalar(t.Width)} {Scalar(t.Height)}";
```

### 4.3 `ThugLifeManager.Snapshot()` (game thread, called by the sampler)
Projects the live `_entries` into `ThugLifeSnapshot`s (empty list when none → the `thug_life` subtree
vanishes on every transport by construction). Mirrors `WeldManager.Snapshot()`.

The registry view is **snapshot-backed** (9p threads read only the published `SimSnapshot`, rule 2). The
live manager list is game/render-thread-internal (§5.3).

---

## 5. The careful bits: GPU rendering + Harmony + threading

### 5.1 Dynamic render postfix — installed only while entries exist
`ThugLifeRenderPatches` owns the postfix on `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`. The
`ThugLifeManager` installs it (on its own `Harmony("gatos.thug_life")`) **on the first `add`** and
removes it **when the last entry is removed** (and at unload). Default-off ⇒ **zero patches, zero GPU
allocation** — exactly the welds/IVA discipline, and literally "only active when toggled on".

GPU resources (texture, pipeline, descriptor set, buffers) are created **lazily on the first `add`**
(`Program.GetRenderer()` must be live — it is, by `OnFullyLoaded`), and disposed when the registry
empties or at unload. The static `Active`/`Instance` the postfix reads are flipped on the game thread.

The postfix is tiny and self-guarded:
```csharp
static void RenderMainPassPostfix(CommandBuffer commandBuffer)
{
    if (!ThugLifeManager.Active) return;
    try { ThugLifeManager.Instance?.RecordDraws(commandBuffer); }
    catch (Exception ex) { /* Debug-log once + disable (Active=false) so the loop never spams */ }
}
```

### 5.2 GPU lifecycle ordering (the dispose-vs-render race)
Per `quad.md`: **clear `Active` before disposing GPU handles**, so an in-flight frame's postfix cannot
dereference freed Vulkan handles. Because `RenderMainPass` runs on the **main/game thread** (the same
thread as the command drain that triggers teardown — §0.2), the postfix and the dispose **cannot run
concurrently**, so the `Active`-then-dispose order is sufficient and safe. Teardown sequence
(`Remove` to empty / `Clear` / `Unload`, all game thread):
1. `Active = false` (postfix now bails);
2. unpatch `gatos.thug_life`;
3. dispose the quad renderer + texture (pipeline, layout, pool, set-layout, vb/ib, sampler — **not** the
   `ModLibrary`-owned shader modules);
4. `Instance = null`, clear `_entries`, publish empty.

A first-failure inside `RecordDraw`/`RecordDraws` sets `Active=false` and latches `_gpuFailed` (one
Debug log), so the feature self-disables instead of spamming — exactly the welds/sampler "degrade, don't
crash" discipline.

### 5.3 Entry registry & threading
The manager keeps `List<ThugLifeEntry> _entries` (the game-thread working set) and publishes a
`volatile ThugLifeEntry[] _published` (swapped on **structural** change — add/remove). The postfix reads
`_published` (so it never iterates a list being mutated — the one race that actually matters). Transform
edits and part re-resolution mutate entry fields **in place** (a `float`/reference write is atomic; a
torn 3-component read during a concurrent edit is one frame of a slightly-off transform — visually nil,
and `RenderMainPass` is same-thread anyway so it cannot even occur). This mirrors unscience exactly and
stays within gatOS's snapshot discipline for the structural set.

`ThugLifeEntry` holds both the stable identity (`Id`, `VesselId`, `PartInstanceId`) **and** the resolved
`Vehicle`/`Part` refs the render math needs. The refs are refreshed by a game-thread `Update()`:

### 5.4 Liveness validation & part re-resolve (`ThugLifeManager.Update()`, game thread)
Called once per frame from `OnBeforeUi` (after the command drain — before the scene renders this frame),
self-gated to a no-op when `_entries` is empty. Per entry (mirrors `WeldManager.Update`):
- drop the entry if its `Vehicle` is no longer in `Universe.CurrentSystem.All` (vessel gone);
- re-resolve `Part` by `InstanceId` from `Vehicle.Parts.Parts` (robust to staging; a removed anchor part
  falls back to body-frame so the sunglasses survive staging of unrelated parts rather than vanishing);
- if the set shrank, republish + (if now empty) tear down GPU + unpatch (§5.2).

The render postfix then reads stable, freshly-validated refs the same frame. (Re-resolving every frame
is O(parts) per entry — trivial; entries are few. The parts *listing* cache from welds is unrelated —
the manager resolves parts live, never via that cache.)

> **New mutation/work site (document it):** this adds gatOS's **first render-thread draw injection**
> (the `RenderMainPass` postfix) and a fourth game-thread work site (`UpdateThugLife` in `OnBeforeUi`).
> Both obey the engine's main-thread model and the "degrade, never crash" rule. Note in
> `docs/ARCHITECTURE.md` and the threading section of `CLAUDE.md`.

---

## 6. The render math (ported from `ThugLifeQuadRenderer`)

Per visible entry, in **ego space** (`quad.md §"Positioning…"`; pull part rotation + position
**separately** so the part's own scale is excluded and width/height are the sole size control):
```csharp
private static bool TryComputeModelEgo(ThugLifeEntry e, out float4x4 model)
{
    model = float4x4.Identity;
    var camera = Program.GetMainCamera();
    if (camera is null || e.Vehicle is null) return false;

    double4x4 vehMat = e.Vehicle.GetMatrixAsmb2Ego(camera);
    double3 partPos; doubleQuat partRot;
    if (e.Part is { } part) {                         // anchor to a top-level part
        partPos = part.PositionEgo(in vehMat);
        partRot = part.Asmb2Ego(e.Vehicle.Asmb2Ego);
    } else {                                          // part_iid 0 → vehicle body/assembly frame
        partPos = e.Vehicle.GetMatrixAsmb2Ego(double3.Zero).Translation(); // origin in ego
        partRot = e.Vehicle.Asmb2Ego;
    }

    float4x4 partRotMat   = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRot));
    float4x4 partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
    float4x4 partEgo = partRotMat * partTransMat;     // rotate then translate (row-vector)

    const float deg2rad = MathF.PI / 180f;
    float4x4 userRot = float4x4.CreateRotationX(e.Rotation.X * deg2rad)
                     * float4x4.CreateRotationY(e.Rotation.Y * deg2rad)
                     * float4x4.CreateRotationZ(e.Rotation.Z * deg2rad);
    float4x4 userTrans = float4x4.CreateTranslation(e.Position);
    float4x4 scaleMat  = float4x4.CreateScale(e.Width, e.Height, 1f);

    // v_local → scale → userRot → userTrans → partEgo → ego
    model = scaleMat * userRot * userTrans * partEgo;
    return true;
}
// RecordDraw: mvp = modelEgo * camera.MVP.viewProjection; bind pipeline+set; Program.SetViewport(cmd);
// push mvp; bind vb/ib; DrawIndexed(indexCount).
```
The body-frame branch (`e.Part is null`) is the only addition vs. unscience (which always requires a
part); verify `double4x4.Translation()`/equivalent during impl (else extract the matrix's row 3 / use
`partPos = double3.Transform(double3.Zero, vehMat)`).

Geometry, texture, pipeline, descriptor — **ported verbatim** from `ThugLifeQuadRenderer`/
`ThugLifeTextureFactory`/`ThugLifeTexturePattern`, with the `quad.md` choices (OffScreenPass,
SampleCount, ReverseZ DepthTestWrite, CullNone, R8G8B8A8UNorm, nearest sampler, per-opaque-pixel quads).

---

## 7. Write path wiring

### 7.1 `SimFsTree` — `ThugLifeDir` under `DebugDir()`
Add `ThugLifeDir()` to the `DebugDir()` child list (next to `WeldsDir()`). It mirrors `WeldsDir`:
- `add` — `LineControlFile.Create("add", …, sink, () => "", ParseThugLifeAdd)`.
- `clear` — `TriggerFile("clear", …, new SimCommand("", "debug.thug_life_clear", NoOrdinal, 1))`.
- `count` — `Line(… () => _store.Current.ThugLife.Count …)`.
- per entry `<id>/`: `vessel`/`part`/`spec` as read-only `Line`s; `position`/`rotation` as
  `VectorControl("", "debug.thug_life_position"/"_rotation", id, arity 3, read)`; `size` as
  `VectorControl("", "debug.thug_life_size", id, arity 2, read)`; `visible` as
  `FlagControl("", "debug.thug_life_visible", id, read)`; `remove` as
  `TriggerFile(… new SimCommand("", "debug.thug_life_remove", id, 1))`.
  (Entry dir name = the decimal id; lookup parses the int and checks it exists in `_store.Current.ThugLife`.)
- Accessor `ThugLife(int id)` → the snapshot entry or `ENOENT` (mirrors `Weld(sourceId)`).

`ParseThugLifeAdd(line)` (static, in `SimFsTree`): split on whitespace; accept **2** tokens (vessel +
part_iid, transform defaulted) or **10** tokens (vessel + part_iid + x y z pitch yaw roll w h). token[0]
non-empty string; part_iid a non-negative integer; the rest finite doubles. Build
`new SimCommand("", "debug.thug_life_add", NoOrdinal, 0){ Token = vessel, Values = [iid, x,y,z,p,y,r,w,h] }`
(defaults `0,0,0,0,0,0,0.975,0.1875`). Return null ⇒ EINVAL on any malformed token.

> Note: `VectorControl`/`FlagControl`/`NumberControl` already accept any `vesselId` string; passing `""`
> + the entry id in `ordinal` is exactly how a registry-keyed (non-vessel) control is expressed. No new
> archetype is needed — `size` (arity 2) is the first 2-vector control; `VectorControlFile` already
> supports arbitrary arity.

### 7.2 `KsaCatalog` — vessel-agnostic dispatch
In `Execute`, before vehicle resolution (next to `debug.weld_clear`), route every `debug.thug_life_*` to
`ThugLifeActuator` on the injected `ThugLifeManager`:
- `debug.thug_life_add` → resolve vehicle by `c.Token` (ENOENT if gone) → `manager.Add(vehicle, (uint)v[0], pos, rot, w, h)`.
- `debug.thug_life_remove` → `manager.Remove(c.Ordinal)`.
- `debug.thug_life_clear` → `manager.Clear()`.
- `debug.thug_life_position`/`_rotation` → `manager.SetPosition/SetRotation(c.Ordinal, vec3(c.Values))`.
- `debug.thug_life_size` → `manager.SetSize(c.Ordinal, c.Values[0], c.Values[1])`.
- `debug.thug_life_visible` → `manager.SetVisible(c.Ordinal, c.Value > 0.5)`.

`KsaCatalog`'s ctor gains a `ThugLifeManager thugLife` param (alongside `WeldManager welds`); the
actuator façade is thin enough to inline into the manager, or live in `ThugLifeActuator` for parity with
the other actuators (preferred — keeps `[KsaAnchor]` on the KSA-touching add/resolve in `Game/Ksa/`).

### 7.3 `Mod` lifecycle (`Mod.cs` / `Mod.Game.cs`)
- `EnsureControlObjects` + `SampleTelemetry`: create `_thugLife ??= new ThugLifeManager()` (no GPU yet —
  GPU is lazy on first `add`). Pass it into the new `KsaCatalog(_health, allVessels, _weldManager, _thugLife)`.
- `OnBeforeUi`: add `UpdateThugLife()` (partial seam) after `DrainCommands()` — validates/re-resolves on
  the game thread before the scene renders (§5.4); self-gates to no-op when empty; degrade-on-error.
- `TeardownGameCheats` (`Unload`): `_thugLife?.Clear()` (sets Active=false, unpatches, disposes GPU) —
  alongside the existing weld clear + IVA restore.
- Sampler: `ThugLife = _thugLife.Snapshot()` in the published snapshot.

---

## 8. Implementation order (each step ends green: `dotnet build gatos.slnx` + `dotnet test`)

Game-free first (unit-tested), then game-coupled (compile-gated on `KSA.dll`, covered by the in-game
checklist) — same staging as the welds work.

1. **Snapshot + Formats** — `ThugLifeSnapshot`, `SimSnapshot.ThugLife`, `Formats.ThugLifeSpec`. (tests)
2. **SimFsTree** — `ThugLifeDir`/`ThugLifeEntryDir`, `add`/`clear`/`count`, `ParseThugLifeAdd`,
   `ThugLife(id)`. Tests over a live `NinePServer` with a synthetic snapshot: tree shape + values +
   that `add`/edits/remove submit the right `SimCommand` (mirror `IvaWeldsPartsTreeTests`).
3. **SimJson** — add the `thug_life` array to the full snapshot JSON; parity test.
4. **csproj** — add the 5 render DLL refs + `AllowUnsafeBlocks=true`; confirm the solution still builds
   with the KSA assemblies present and (CI path) without them.
5. **ThugLifeTexturePattern / TextureFactory / QuadRenderer** — port (unsafe), `[KsaAnchor]`.
6. **ThugLifeEntry / ThugLifeRenderPatches / ThugLifeManager** — registry + dynamic patch + GPU
   lifecycle + RecordDraws + Snapshot + Update; `[KsaAnchor]`.
7. **ThugLifeActuator + KsaCatalog dispatch** — the 7 actions; ctor wiring.
8. **Sampler + Mod lifecycle** — `Snapshot()` projection; `UpdateThugLife()` in OnBeforeUi; teardown.
9. **Docs**: SPEC, KSA_INTEGRATION_MATRIX, scope (read/write/runtime/assets + FULL_SCOPE), ARCHITECTURE,
   CLAUDE.md (status + threading), MILESTONES, VALIDATION checklist, gatos skill (+ a recipe).

---

## 9. Testing & validation

**Game-free (NUnit):**
- `ParseThugLifeAdd`: 2-token (defaults) + 10-token (explicit) → exact `SimCommand`; bad arity / NaN /
  negative-or-fractional part_iid → null/EINVAL, no submit.
- Tree: a synthetic `SimSnapshot` with thug-life entries → `thug_life/count`, `<id>/{vessel,part,
  position,rotation,size,visible,spec}` present with correct values; `add`/`position`/`rotation`/`size`/
  `visible`/`remove`/`clear` route to the sink with the right action/ordinal/values/phase (Frame).
- `Formats.ThugLifeSpec` round-trips with `ParseThugLifeAdd` (spec → add → same values).
- Transport parity: thug-life leaves appear under `/v1/fs` + `gatos/sim/` (existing harness); `SimJson`
  includes the `thug_life` array.

**In-game (add to `docs/VALIDATION.md`; needs a live flight):**
- `echo "<vessel> <part_iid>" > …/thug_life/add` → sunglasses appear on the part, correctly oriented and
  depth-tested (occluded by nearer geometry, not painted on top — verifies OffScreenPass + reverse-Z).
- Tune `position`/`rotation`/`size` live → quad moves/rotates/resizes; `visible 0/1` hides/shows.
- Multiple entries on one and several vessels; track correctly through maneuvers, rotation, **time-warp**,
  and camera changes; **stage** an unrelated part on the anchor vessel (entry survives; if the anchor
  part itself is staged, it falls back to body frame, not a crash).
- MSAA on (4×/8×) → no depth artifacts (verifies `SampleCount`).
- `remove` last entry / `clear` → quads vanish, **patch removed + GPU freed** (confirm via log: no
  per-frame cost, no `gatos.thug_life` patch when empty). Toggle on/off repeatedly (no leak, no double-
  patch). Clean `Unload` (no Vulkan validation errors, no residual draw).
- Confirm zero per-frame cost with no entries (PerfStat / the postfix absent).

---

## 10. Documentation mandate (same work item)

- **`SPEC_9P_FILESYSTEM.md`** — all §2 paths/formats/actions/errno/phase + HTTP/MQTT mirrors. (binding)
- **`docs/KSA_INTEGRATION_MATRIX.md`** — new bindings: `SuperMeshRenderSystem.RenderMainPass` postfix,
  `Program.{GetRenderer,GetMainCamera,OffScreenPass,SetViewport}`, `ModLibrary.Get<ShaderReference>`,
  `RenderTechnique.CreateShaderStages`, `Vehicle.GetMatrixAsmb2Ego`/`Asmb2Ego`, `Part.PositionEgo`/
  `Asmb2Ego`, and the Brutal.Vulkan/Planet.Render.Core GPU surface. **Flag the render set High churn.**
- **`scope/`** — `ksa-runtime-coupling.md` (the render postfix + dynamic Harmony + GPU lifecycle),
  `ksa-write-surface.md` (the thug-life actuator), `ksa-read-surface.md` (the anchor-math reads),
  `ksa-assets-and-versions.md` (the new render-DLL refs + shader-key dependency), and the
  `scope/FULL_SCOPE.md` inventory. Each with its game-version status. (binding)
- **`docs/ARCHITECTURE.md`** — the render-thread draw-injection site, the `gatos.thug_life` dynamic
  Harmony instance + lazy GPU lifecycle, the `UpdateThugLife` validation site.
- **`CLAUDE.md`** — status table row + threading section (render-injection + 4th game-thread work site);
  the new GameMod render-DLL deps + `AllowUnsafeBlocks`; detail in `docs/MILESTONES.md`.
- **`.claude/skills/gatos/`** — mention thug-life and point at the SPEC; a `recipes.md` entry
  (anchor sunglasses to a part). The `ksa/quad.md` skill is the upstream how-to — cross-link it.

---

## 11. Risks & open questions

1. **Render-internals churn (HIGH — the headline risk).** This is gatOS's deepest KSA coupling:
   `SuperMeshRenderSystem.RenderMainPass`, `Program.OffScreenPass`, the `UnlitMesh*` shader keys, the
   Brutal.Vulkan abstraction surface, and the ego-space anchor math. Render-pipeline internals change
   more often than gameplay APIs, and a break here is a compile error (good) **or** a silent visual
   glitch (depth/MSAA — caught only in-game). Mitigation: everything is under `Game/Ksa/ThugLife/` +
   `[KsaAnchor]`, the feature self-disables on any runtime fault, and it is **off by default** so a
   regression never affects the rest of gatOS. The version-diff playbook (`scope/FULL_SCOPE.md §0`) must
   call this set out explicitly.
2. **`unsafe` in `gatOS.GameMod` (low).** The quad renderer needs it (pointers to `Vk*` infos). Flipping
   `AllowUnsafeBlocks=true` is project-wide for GameMod but the unsafe code is confined to
   `ThugLifeQuadRenderer`/`ThugLifeTextureFactory`. Acceptable and necessary (it cannot move to a
   game-free project — the dependency rule forbids non-GameMod projects referencing Brutal/KSA).
3. **GPU dispose vs. in-flight render (low, given same-thread).** `RenderMainPass` is main-thread
   (`quad.md`), so dispose (game thread) and the postfix cannot overlap. The `Active`-before-dispose
   order + try/catch is belt-and-suspenders. If a future KSA build moved scene recording onto a worker
   thread, this assumption breaks — re-verify on render-pipeline churn; the fallback is to keep GPU
   resources alive until `Unload` (only flip `Active`) instead of disposing on empty.
4. **Body-frame anchor math (low).** The `part_iid 0` branch (vehicle-frame anchor) is the only math not
   copied verbatim from unscience; verify the origin-in-ego extraction in-game (otherwise sunglasses sit
   at the wrong spot — harmless, tunable via offset).
5. **Subparts deferred (low).** v1 anchors to top-level parts only. A cockpit window is often a subpart;
   a part-local position offset on the parent part reaches it, so this is a usability nicety, not a
   blocker. Future: extend `parts/<n>/subparts/<m>/` discovery + a subpart-aware resolver (one nesting
   level, like unscience).
6. **Entry id reuse (resolved).** Ids are the smallest free slot, reused after `remove`/`clear`, so the
   numbering tracks the live set and never grows unbounded (a removed/cleared id frees up for the next
   `add`). The id rides in the command `Ordinal`. No concern.
