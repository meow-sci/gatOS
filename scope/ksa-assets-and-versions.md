# Scope — KSA Assets & Versions

> The concrete KSA artifacts gatOS depends on (reference DLLs, decompiled sources, Content XML), the
> exact asset files that seed the runtime values gatOS reads, the version pins, and the mechanical
> procedure for diffing one game build against another. This is the "where do I look" companion to the
> playbook in [`FULL_SCOPE.md`](FULL_SCOPE.md#0-how-to-use-this-folder-when-a-game-update-lands-the-break-check-playbook).

---

## The game-assemblies checkout layout

gatOS builds against, and is break-checked against, **game-assemblies checkouts** that sit next to the
repo. Each checkout's `copy-ksa.ts` copies a specific game build out of the install dir
(`C:\Program Files\Kitten Space Agency`) into a self-contained, versioned snapshot:

```
<checkout>/
  copy-ksa.ts                 the extractor (Brutal*.dll, KSA.dll, Planet*.dll + Content/)
  current/
    version.json              build id + date + FULL per-revision commit log (the changelog)
    dll/                      reference assemblies gatOS compiles against (KSAFolder → here)
    decomp/                   decompiled C# sources (human-readable; what [KsaAnchor].SourceFile names)
    Content/                  game data XML (part templates that seed the values gatOS reads)
```

Two checkouts are kept side by side for diffing:

| Checkout dir | Build | Date | Revisions | Role |
|---|---|---|---|---|
| `…/ksa-game-assemblies` | **2026.9.7.5402** | 2026-09-02 | 5348 → 5402 (**changelog gapped**: `version.json` logs only rev 5401, `fromRevision` 5400) | **current / verified baseline** — full playbook pass 2026-09-02 (see [`#5402-pass`](#5402-pass)): **three compile breaks** (the `Viewport` class rework, `DebugTrailColor` removed, `Cursor.InputRay` removed) fixed, one `/sim` node retired, two new High-risk reflection accessors, binary surface diff 907/907 member refs resolved. `KSAFolder` default resolves here (commit `57e6040`). PREVIOUS for the diff was **`git worktree add <tmp> c465abb`** (the 5348 drop) — the git history is the `_prev` checkout now |
| (git `c465abb`) | 2026.8.22.5348 | 2026-08-23 | 5261 → 5348 (85 commits, revs 5262–5348) | prior baseline — full playbook pass 2026-08-23 (see [`#5348-pass`](#5348-pass)): **zero compile breaks — the first pass in the project's history with none** (5261 had ten, 5168 had four); three real breaks the compiler could not see found and fixed, plus one long-standing **pre-existing** bug diagnosed and fixed. `KSAFolder` default resolves here (commit `c465abb`). The checkout is a **git repo whose history holds every prior drop** (`1401af7` = 5261, `13595c1` = 5056, `3106557` = 5018, `cdb7391` = 4980, `7cf5c0a` = 4892, …) — diff drops with `git diff <old>..<new>` inside it |
| `…/ksa-game-assemblies_prev` | 2026.8.19.5261 | 2026-08-11 | 5168 → 5261 | prior side-by-side checkout — itself a **fully audited baseline** (the 5261 pass closed its own findings, [`#5261-pass`](#5261-pass)), and CURRENT's `fromRevision` is 5261, so the two trees **chained with no gap** and the 5348 pass diffed them directly (no git-history fallback needed) |

gatOS was originally built against the 4680-era sources (most `[KsaAnchor]` `Verified` dates span
2026-06-12…2026-06-23). The **4680 → 4750** diff was run through the playbook on 2026-06-27; the touched
anchors carry `GameVersion="2026.6.9.4750"` (see
[`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md)).

**The 5348 → 5402 pass (2026-09-02) — three compile breaks fixed (one viewport-class rework, two
removed members), one `/sim` node retired, two new High-risk reflection accessors, no silent breaks
found.** {#5402-pass}
PREVIOUS (`2026.8.22.5348`, commit `c465abb`, materialised with `git worktree`) was a fully audited
baseline. CURRENT's `version.json` is **gapped**: `fromRevision` 5400, `toRevision` 5402, one logged
commit (rev 5401, "Fixed crash for incorrect data stride for thumbnail rendering") — revs 5349–5399
carry no messages anywhere, so the **full tree diff was the discovery mechanism** (`git diff c465abb
57e6040`: 350 files, 294 decomp files +16 221/−3 942; `KSA.dll` 4 564 056 → 4 798 552 bytes,
`Planet.Render.Core.dll` +512 bytes, every `Brutal.*.dll` rebuilt with identical sizes). Rev numbers
are therefore not cited for this window.

**The alarm rang — and hid half of itself.** The first build reported **10** errors, all `CS0246
'Viewport' could not be found` at declaration sites (`CameraDirector`, `CameraPoseController`,
`CameraFollowable`, `CameraViewportPatch`, `CameraReader`, `IvaForceRender`, `VesselForceRender`); with
those fixed the body phase surfaced **6** more (five `VolumetricTrailRenderer.DebugTrailColor`, one
`Cursor.InputRay`). Three distinct breaks, 16 errors, two iterations to green — the step-2 "iterate to
green" warning, again. Clean `-t:Rebuild` of the whole solution against the 5402 DLLs: **0 warnings, 0
errors**; `dotnet test gatos.slnx` **1646 passed / 12 skipped / 0 failed** (three trail-colour tests
re-pointed, none dropped).

> **Binary-level member-surface diff, second run** (the 5348 technique; tool and raw dumps kept out of
> the repo). Of the built `gatOS.GameMod.dll`'s 850 TypeRefs, **482** point into game assemblies (KSA
> 260, Brutal.Vulkan 80, BepuPhysics 34, …): **482/482 resolve in 5402** (474/482 in 5348 — the eight
> missing there are the viewport types gatOS now binds), **907/907 MemberRefs** resolve with identical
> name/parameters/return type, **0 type-level breaks**, **52 of 474** shared referenced types changed
> declared shape — every one in `KSA.dll`, 42 of them only by `Viewport → IViewport/IGameViewport` —
> and **all 222 referenced `Brutal.*`/`Planet.*`/Bepu/CommunityToolkit types are byte-identical** in
> declared surface despite the rebuilt DLLs. All 25 reflection strings resolve on the same declaring
> type with the same visibility (the one "not found" is the never-taken `GetProperty("Scale")`
> fallback; `CharacterCore.Scale` is a public float field in both builds). All 15 Harmony targets are
> present, each with **exactly one overload** (`GameViewport.OnFrame(double)` public virtual;
> `Program.RenderGame(AcquiredFrame,double)` private; `PartModel..ctor(PartModelModule+Template)`
> protected; …). The two protected setters `ViewportSeam` now reaches — `GameViewport.FixedController`
> and `ViewportBase.Mode`, both `get:public set:protected` — exist. Struct layouts gatOS mirrors are
> identical (`PlanetUbo` 31 fields, `MeshUbo` 19 incl. the four `DirAnchor*` split-doubles,
> `ManualControlInputs`, `VolumetricTrailPushConstants`); `VolumetricTrailData` lost `TrailColor` and
> `PlumeTrailSettings` swapped `SegmentLifetimeSeconds` for `PropertyCommitDelta` — both already in
> the retired-node finding below.

**The three breaks, all fixed:**

| # | Break | Fix |
|---|---|---|
| C1 | **Viewport rework.** `KSA.Viewport` deleted; `IViewport`/`IGameViewport` interfaces, `ViewportBase`/`GameViewport`/`PartThumbnailViewport`, static `ViewportRegistry` (8 slots: 1 main, 1 part-thumbnail, 4 secondary, 2 crew-portrait; `Index` → `ShaderSlot`). `Program.MainViewport` is an `IGameViewport`, `RenderedViewport` an `IViewport`; `Controller.OnFrame`, `IOrientation.DrawAxes`, `Vehicle.UpdateRenderData`, `PartTree.UpdateRenderData`, `PartModel.AddInstance` all take `IViewport`; **`Mode` and `FixedController` became protected-set properties**, `MenuBarInUse` read-only (writer = explicit `IGameViewportLifecycle.SetMenuBarInUse`). | Nine files retyped; `GameViewport.OnFrame` is the camera hook target; **new `Game/Ksa/Camera/ViewportSeam.cs`** reaches the two protected setters by reflection (2 anchors, Risk High, degrade to `EOPNOTSUPP` / `SetCameraMode`); `IvaForceRender` mirrors `AddInstance`'s new `RenderPartModels` gate; `StickerDecalRenderer` binds `ShaderSlot`; `ThugLifeManager` classifies passes by `ViewportType` (a part-thumbnail pass gets no bit). |
| C2 | **`VolumetricTrailRenderer.DebugTrailColor` removed** (with its debug-window row and UBO slot); colour/density/lifetime are per-`PlumeTrailTemplate` asset now (`Color`/`DensityMultiplier`/`Lifetime`, `PlumeTrailAssets.xml` gained `LiquidEnginePlumeTrail`). | `/sim/debug/plumetrail/render/trail_color` **retired** (nothing global left to bind; the debug window no longer exposes it): `FxCatalog`, help text, `TrailActuator`, 3 tests, SPEC/matrix/scope/VALIDATION. Follow-up candidate: a per-template subtree. |
| C3 | **`Cursor.InputRay` removed**; `Cursor` rewritten on `float2` desktop coordinates with `GetEgoRay(IViewport)`. | `StickerPicker` aims with `Cursor.GetEgoRay(Program.MainViewport)` — the ray is now computed live rather than cached from the previous frame. |

**No compiler-invisible break was found this time.** The candidates were checked one by one: the
`RenderMainPass` postfix (still one overload, three call sites `:4395/:4656/:4856`; the body gained
two-sided skinned techniques inside their own GPU tag), `ResolveAttachments` (`:4430/:4737/:4864`,
`RenderTarget.cs` unchanged), the `RenderGame` transpiler anchor, `UiPixelCulling()`, the terrain UBO
mirror (`MeshUbo`/`PlanetUbo` identical), the plume/cloud/trail reflection handles, the throttle/RCS
struct fields, the EVA scale chain, the solver-drain window (a new cloth-solver lane runs alongside but
touches nothing gatOS writes), and the threading phases (`SolverActions` unchanged).

**Semantic drift and new engine behaviour, documented, no code change** (detail on
[read](ksa-read-surface.md#5402-findings) / [write](ksa-write-surface.md#5402-findings)): a
**structural-failure/debris system** (parts carry crash tolerances; a crash splits a vessel into
`<id>_N` fragments plus debris that are ordinary vehicles — so `vessels/` lists them, their orbit
events are frozen, and control/camera can move to a fragment with no command); **parachutes** (a new
module family gatOS does not report; `ctl/stage` now arms/cuts them; chute bays drive their own door
animation); `Part.DisplayName` is now the **authored** template name (not unique);
`Camera.ClampCamera` is camera-local and terrain-aware; `MapController` only juggles control for the
main viewport; `Program.ControlledVehicle`'s setter clears held input.

**Render internals held completely.** `KSA.Rendering/RenderTarget.cs` is **byte-identical**, so both
GPU seams that hang off it — the sticker `ResolveAttachments` postfix and the thug_life
`SetupGraphicsPipeline` stamp — are unchanged; so are `GameSettings.cs`/`UiCoverageMaskSystem.cs`
(the `/sim/display` mask prefix), `BindlessTextureLibrary.cs` (clutter + sticker texture binding),
`GlobalShaderBindings` (`DescriptorSetLayout`/`DescriptorSet`/`DynamicOffset(int)`, slot count now a
constant 8), `ShaderModuleUtils`, `RenderTechnique`, `Presets`/`RenderingPresets`, `Core/Renderer.cs`,
`TextureLoader`/`TextureAsset`/`SimpleVkTexture`/`VkUtils`, and every shader gatOS touches or includes
(`MeshIndirect(.Raytraced).frag`, `UnlitMesh.{vert,frag}`, `Grid.{vert,frag}`,
`Common/{Global,Camera,TextureSet,Extensions,Shared}.glsl`). Only asset-line and `Program.cs` member
lines moved (`GridFrag` `:373 → :374` behind the new `StaticObjectPrePassIndirectFrag` at `:62`;
`OffscreenTarget :457`, `PointClampedSampler :469`, `MainViewport :485`, `ResourceFrameIndex :218`,
`ColorFormat :222`, `GetRenderer :558`, `GetRenderCamera :642`, `SetViewport :4293`, the `RenderGame`
final `End()` `:4595 → :4764`). `GridPass` did change — per-`ShaderSlot` `SceneDepthDescriptorSets[8]`
with `Rebuild(IViewport)` from `Program.RebuildViewport :4909` — but gatOS binds no `GridPass` member;
it ports the pattern and writes its own descriptor against `Program.OffscreenTarget.DepthImage`, still
the main viewport's shared target.

**Render-area coverage gaps opened at 5402** (candidates, nothing broken): per-`PlumeTrailTemplate`
colour/density/lifetime (the C2 follow-up); the volumetric-exhaust **plume bend/fold** deformation
(`ExhaustPlumeDeformation.cs`, `ExhaustPlumeGasDynamics.cs`, `PlumeBend.glsl`, spec constants 22–24,
permutation count 32) — air-velocity driven with no template knobs, so `/sim/debug/engineplume` still
covers everything author-visible; **first-person head hiding** (`CharacterCore.HeadMeshIndices`,
`AnimatedRenderable.MaskedMeshIndices`, `KittenRenderable.HideHead`, `IVASeat.IsCameraInThisSeat`),
which hides an EVA-paint subject's own `body`/`fur` slots in the seat's own viewport only;
**attached-internal parts** (`Part.IsAttachedInternal`, `AttachedInternal.{InstanceOf,PositionParentAsmb,
Asmb2ParentAsmb}`), a third transform holder the IVA driver — which adopts only `SubParts` — does not
know about; **clutter physics** (`BubbleClutterStatics.GatherNearestInstances`,
`ClutterEcotypePhysicalData.ComputeBoundingRadius`, per-cell collider draw); and the
`ViewportOptionFlags` presets themselves.

**Lockstep:** 47 anchors re-stamped + 2 new (**177** total); `#5402-findings` on both scope surfaces;
this record; FULL_SCOPE §0; the matrix header + camera/trail/IVA/always-render rows; the
runtime-coupling hook table, camera-driver section and reflection-accessor table; SPEC §3.4 (`vessels/`
debris note, `display_name`, `ctl/stage`), §3.7 (trail row), §3.11 (camera hook); `docs/VALIDATION.md`
(a 5402 card + item 5 of the FX card); `plans/FX_EDITORS_PLAN.md`; the `gatos` skill's version notes;
and the `ksa`/`harmony` skills' viewport, menu-bar, quad and vehicle-API passages (the KSA API they
document changed). **Live re-checks queued in [`../docs/VALIDATION.md`](../docs/VALIDATION.md).**
**5402 is now the verified baseline.**

**The 5261 → 5348 pass (2026-08-23) — ZERO compile breaks (a first), three compiler-invisible breaks
found and fixed, one pre-existing bug diagnosed and fixed.** {#5348-pass}
PREVIOUS (`2026.8.19.5261`) was itself a fully audited baseline and CURRENT's `fromRevision` is 5261,
so the two trees chain with no gap and were diffed directly (revs 5262–5348, 85 commits).

**The build said nothing.** A clean `-t:Rebuild` of the whole solution against the 5348 DLLs produced
**0 warnings and 0 errors** — the first pass in the project's history where the compile-as-alarm step
did not fire at all (5261 had ten breaks, 5168 four, 5117 two, 5018/4980 one each). The suite is green:
**1646 passed / 12 skipped / 0 failed**. That makes this the pass that proves the playbook's own
premise: *the alarm is necessary, never sufficient.* Everything below was found by reading diffs.

> **New technique — a binary-level member-surface diff.** Beyond the skill's decomp-diff procedure,
> all 481 external TypeRefs were extracted from the compiled `gatOS.GameMod.dll`, every referenced
> type's full member surface (public + non-public, declared-only) was dumped from **both** DLL sets via
> `MetadataLoadContext`, and the two dumps diffed. **63 of 470 referenced types changed shape.** This
> checks the *shipping binaries* rather than the decomp, which can lag them (the warning at the foot of
> the artifact table has always said so; this is the first pass that acted on it). Every one of gatOS's
> ~15 reflection accessors and every Harmony target was confirmed present with a compatible shape in
> the real 5348 assemblies — not merely in the decomp. See
> [runtime](ksa-runtime-coupling.md#reflection-accessors).

**Three real breaks the compiler could not see — all fixed:**

| # | Break | Why the compiler was blind | Fix |
|---|---|---|---|
| C1 | Rev **5283**'s new `UiCoverageMaskSystem` stamps the reverse-Z **near plane** into the pre-pass depth wherever fully-opaque ImGui UI covers the screen (`UiCoverageMask.RecordDepthStamp`, first thing inside `PrePassRenderer.Render`); `CopyDepthImageToSrc` carries it into `_offscreenTarget`, whose scene pass loads that depth — so every later `GreaterOrEqual` test under the UI fails via early-Z. `FrameCapture` reads the offscreen colour **before** the UI composite, so `/sim/display` streamed the local player's window chrome as unshaded black. Invisible locally; only a remote reader sees it. | pure new engine subsystem; no gatOS symbol moved | a Harmony **prefix on `GameSettings.UiPixelCulling()`** returning `false` while the stream is live — the getter has exactly **one** game-side caller, which re-reads it per frame and zero-clears the tile masks, suppressing the stamp *and* every consumer early-out. Not the field: `GameSettings.Current.Graphics.UiPixelCulling` is the player's persisted `[TomlField]` setting. **A brand-new KSA coupling born at 5348** — new `[KsaAnchor]`, Risk High |
| C2 | Revs **5331/5339** moved physics-bubble ownership entirely into `VehicleUpdateTask.Run()` — `TrimBubbles()`/`IntakeOrphans()`/`MergeBubbles()`/`SplitBubbles()` now mutate the bubble list **and object pool** on the solver thread; `Universe.{MergeVehicleTasks,TrimPhysicsBubbles,AddVehiclesToTasks}` and `Universe._physicsBubbles` are all **deleted**. `debug.teleport`/`debug.impulse` reach `PhysicsBubble.RemoveVehicle` through `Vehicle.Teleport`, and they were Frame-phase | the deleted methods were ones gatOS never called; the surviving path is signature-identical | both actions moved into `SimCommand.SolverActions`. Timing proof and the engine's own invariant: [runtime](ksa-runtime-coupling.md#threading-phases) |
| C3 | Revs **5319–5325** (terrain precision rework) gave `PlanetRenderer.MeshUbo` four new split-double anchor fields (`DirAnchor{Hi,Lo}`, `DirAnchorUv{Hi,Lo}`) that `GenerateMeshData` writes **per frame index, every frame**, from the live camera. `TerrainActuator.Mirror` whole-struct-copied frame 0 into every other frame-in-flight, so every `/sim/debug/terrain` write stamped frame 0's terrain anchor over the other frames' live values | additive fields on a struct gatOS copies wholesale — nothing to bind, nothing to break | `Mirror` is now **field-wise**: only the eight `PlanetUbo` and three `MeshUbo` fields gatOS actually writes. Also immune to the next such field addition on either struct |

**Plus one pre-existing bug, diagnosed and fixed (C4).** `ClutterTextureBridge` keyed its catalog on
`TextureReference.GetRealId()`, which returns `Id` only when `SerializedId.IsReferenceable` — set
**only** when the asset XML carries an `Id=` *attribute*. **No clutter texture element has one.** Every
slot fell through to `walk.Anonymous++`, the catalog published **empty**, and every `bind` returned
ENOENT: the feature was completely inert, identically on 5261 and 5348. This is the bug commit
`1e97269` was hunting. The catalog is now keyed on `TextureReference.LocalPath` — and the asset layout
this document previously described **never existed**; see
[clutter texture assets](#clutter-assets) below for the correction.

Two smaller code changes rode along: the clutter walk gained the new `PbrMaterialReference.AlphaMap`
slot (`[XmlElement("Alpha")]`, added in 5348 — no stock clutter material authors one yet, so this is
forward-coverage), and `FaceFxManager.KittenFaceAsmb` tracked rev **5270**'s
`CrewPortraitPanel.FACE_HEIGHT_OFFSET_EVA` `0.85 → 0.70`, which had `/sim/debug/fx` face bursts landing
~0.15 m too high.

**Silent semantic drift — `/sim` meaning moved with no code change** (nine items, detailed on
[read](ksa-read-surface.md#5348-findings) / [write](ksa-write-surface.md#5348-findings)): rev **5329**
made `ctl/stage` walk `GetSubtreeSequencedModules()` and activate only `ISequenced` modules whose own
`Sequence` matches, so **staging no longer flips `rcs/<n>/active`** (`ThrusterController` is `IActivate`
but not `ISequenced`) and a part with modules in two sequences now needs two presses; rev **5317**
inverted the FC's min-throttle fold (`Min`→`Max`, seed `1f`→`0f`), so on a multi-engine stack the
*effective* floor is now set by the most restrictive engine; rev **5318** corrected `navball/deltav`
and `navball/twr` on vehicles with a part in sequence 0 (the values were **wrong before**); rev
**5317** retuned burn timing, the TVC gain matrix and the auto-burn abandonment latch; rev **5268**'s
MMU material reorder swapped the EVA paint slot ordinals (D5, below); `Part.ScaleTotal` composition
went additive → multiplicative and rev **5329**'s new `IRescale` made the game's own editor scaling
*physical* and clamped 0.5×–2×, which gatOS's `/sim` scale deliberately is not; and the terrain
cube-face **seam** sampler changed (D8 — sub-metre, round-trip property preserved,
[runtime](ksa-runtime-coupling.md#frames-and-numerics)).

**Asset-level changes this pass:**
- `Astronomicals.xml`: **`TreeType13/14/15` commented out** (rev 5263 — no colliders yet). Their sole
  materials `Tree12Cards`/`Tree13Cards`/`Tree14Cards` (15 texture slots) leave the clutter catalog
  walk. A persisted binding to one surfaces as `Failed` ("stock texture 'x' is gone") — graceful.
- Grass ecotype (revs 5306/5345): `ObjectSeparation` **1.3 → 1.45 m**, `GenerationRange`
  **170 → 80 m**. An overridden grass texture now disappears at 80 m.
- `CharacterAssets.xml` `CharacterMMUAttachment` (rev 5268 era): source mesh
  `Characters/KittenMMU/KSA_Cat_MMU.gltf` → `SK_KSA_MMU.glb` (now **skinned** — `MmuMesh` retyped
  `StaticMeshRenderable` → `AnimatedRenderable`, plus an `AnimationScrubSampler ArmScrub` and a new
  `<Transform>` block), and the two `<Materials>` blocks were **reordered** to follow the glb
  ("body first, labels second"): `KSA_MMU_Color` is now index **0** (was 1), `KSA_MMU_Texts` index
  **1** (was 0). gatOS names EVA paint slots by array ordinal, so a saved rule targeting `mmu` now
  repaints the MMU **body** instead of the label decals. The `.glb` is not in the repo, so the
  `MaterialIndices` **length** could also differ — live check queued.
- **New** `Content/Core/GroundClutter/_GameData.xml` and `_Materials.xml`: purely additive
  `ClutterObjectGameData` substance/volume entries and `Rock.*` substance definitions
  (density/temperature) for clutter collisions. They **replace nothing, rename nothing, and move no
  texture key** — the three `*Assets.xml` files remain the only clutter *texture* source.
- `PlanetUbo.TessellationRangeMeters` default **220 → 50** and the shader's displacement falloff moved
  from `range*0.1 … range*0.95` to `range*0.75 … range*0.975`. Field name/type/offset unchanged and
  gatOS's `1..20000` clamp still admits the new default — but the documented example values for
  `/sim/debug/terrain tessellation/range_m` are now misleading.

**Verified clean, stated plainly:** the whole `Brutal.Numerics` tree is **byte-identical** and rev
5280's new `CelestialFrameMath` is a **pure refactor with bit-identical results**, so
[`../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md) needs no
correction; `Orbit.CreateFromStateCci` and the whole orbits set are byte-identical; the solver hook is
unchanged in every respect that matters; `KSA.Rendering/RenderTarget.cs`, `UnlitMesh.{vert,frag}`,
`Common/Shared.glsl` and `Grid.{vert,frag}` are **untouched**; the Vulkan 1.3 → 1.4 bump (rev 5315) is
a no-op for gatOS because `ShaderModuleUtils` still maps to SPIR-V `_1_6`; the rev-5301 lighting-UBO
reshape cannot reach gatOS's shaders by construction; the bindless override mechanism is zero-diff and
gatOS remains the **sole** caller of `BindlessTextureLibrary.SetTexture` in the process; and the
rev-5288 clutter GPU repack does not reach the bridge. Full detail:
[read](ksa-read-surface.md#5348-findings) / [write](ksa-write-surface.md#5348-findings) /
[render refs](#render-refs) / [runtime](ksa-runtime-coupling.md).
Build + full suite green against 5348 (0 warnings, 1646 passed / 12 skipped). **5348 is now the
verified baseline.** **Still pending: the live in-game pass** — 15 items queued in
[`../docs/VALIDATION.md`](../docs/VALIDATION.md).

**The 5168 → 5261 pass (2026-08-11) — ten compile breaks fixed (one type migration), one silent
semantic break found and closed.** {#5261-pass}
PREVIOUS (`2026.8.5.5168`) was itself a fully audited baseline and CURRENT's `fromRevision` is 5168,
so the two trees chain with no gap and were diffed directly (revs 5169–5258, 90 commits).

Rev **5211** replaced `SimTime` (a `double` of seconds) with **`UniverseTime`** (`Int128` nanoseconds,
a prelude to 64-bit-ns `BubbleTime` and multiplayer physics), and revs **5208–5216** rebuilt the
vehicle-update threading model (`JobSystems.VehicleSolvers` → `VehicleSolver` + a new
`DynamicWorkerPool VehicleWorkerPool`; `VehicleUpdateTask` orchestrating `PhysicsBubble` islands).
Together those produced **ten compile errors** across `VesselReader`, `FlightComputerActuator`,
`WeldEngine`, `WeldManager`, `IvaPhysicsManager`, `DebugActuator`, `AudioActuator`, `FxReflect`,
`KsaCatalog`, `Mod.Game` and `TelemetrySampler` — all mechanical once the replacements were identified
(`GetElapsedSimTime()` → `GetElapsedSeconds()`/`GetElapsedTime()`, `VehicleSolvers` → `VehicleSolver`,
`SimTime` → `UniverseTime`).

> **⚠️ Playbook correction — "every compile error *is* the work list" is only true after the build goes
> green.** Roslyn does not bind method bodies while any **declaration-phase** error is outstanding, so
> the first build reported **exactly one** error (a `SimTime` *parameter* on `VesselReader.TimeUntil`)
> and silently hid the other nine, which were all inside method bodies. Treating that first list as the
> work list would have understated the break by 9×. **Fix and rebuild iteratively until green.**

The one break the compiler could *not* have caught is documented in full at
[read](ksa-read-surface.md#5261-findings): `UniverseTime.EndOfTime` (`Int128.MaxValue` ns) replaced
`SimTime.PositiveInfinity` as the "no such event" sentinel, and its `.Seconds()` is a **finite**
~1.7014e29 — so gatOS's `Sanitize.Finite` scrub silently stopped working for `orbit/time_to_ap`,
`orbit/time_to_pe` and `orbit/next_patch`. Closed with `IsSaturated()` guards that preserve the
existing `0` = "no such event" contract. A second, smaller trap in the same migration:
`UniverseTime`'s constructor **throws on NaN** where `SimTime` stored it silently, so `ctl/burn` now
validates its `ut` up front rather than latching the accessor degraded — see
[write](ksa-write-surface.md#5261-findings).

Everything else verified unchanged: **all ten Harmony hook targets** are signature-identical (including
through the threading rework), the **entire `Brutal.Numerics` tree is byte-identical** (frames/numerics
intact), every **reflection accessor** resolves structurally (`ManualControlInputs` gained only
`GrabHeld`; the `KittenEva._renderable → _characterAvatar → Core → Scale` chain is untouched), and the
**`thug_life` render set** holds — `SuperMeshRenderSystem.cs` changed for the first time since 5018 but
neither change (rev 5241's `Program.SetViewport` at the head of `RenderMainPass`, rev 5236's
`DescriptorDynamicOffset` → `null`) reaches the postfix; gatOS already called `Program.SetViewport(cmd)`
itself. Content XML: rev 5227's battery capacities are **×10 in value, identical in unit** (`J=`), so
the reads stay truthful while absolute thresholds in guest flight programs shift.
Build + full suite green against 5261 (0 warnings, 1317 passed / 11 skipped). 5261 is now the verified
baseline. **Still pending: the live in-game pass** — see [`../docs/VALIDATION.md`](../docs/VALIDATION.md).

**The 5117 → 5168 pass (2026-08-05) — four compile breaks fixed (one render refactor), three silent
semantic breaks found and closed.** {#5168-pass}
PREVIOUS (`2026.8.3.5117`) was itself a fully audited baseline and CURRENT's `fromRevision` is 5117,
so the two trees chain with no gap and were diffed directly (revs 5118–5168, 49 commits).

**Compile breaks — all four trace to rev 5154**, which moved offscreen rendering off
`VkRenderPass`/framebuffers onto **Vulkan dynamic rendering** and deleted `KSA.OffscreenTarget`,
`KSA.RenderTarget`, `KSA.Framebuffer`, `Core.RenderPassState` and `Core.DynamicRenderState`:

| Site | Break | Resolution |
|---|---|---|
| `Fx/FxReflect.cs:77,270` | `CloudRenderer._worleyNoise3dTarget` retyped `RenderTarget` → `RenderImage` (and `CloudShadowsRenderer.PopulatePlanets`'s parameter with it) | re-typed + re-anchored |
| `ThugLife/ThugLifeQuadRenderer.cs:131,137` | `Program.OffScreenPass` **deleted** | migrated to `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` — KSA's own pattern; the quad now tracks the engine sample count instead of hard-binding one |
| `FrameCapture.cs:145,162` | `RenderTarget.ColorImage` is now nullable (`RenderImage?`) | null-guarded (a depth-only target has no colour to capture) |

> **Name-collision trap:** the **new** `KSA.Rendering.RenderTarget` is an *unrelated* class that
> reuses the deleted `KSA.RenderTarget` name. Do not re-bind by name alone — the anchors say so.

**Render preconditions re-verified rather than assumed** (the compiler cannot see these):
`Program.MainViewport.OffscreenTarget` is still literally `Program._offscreenTarget`
(`Program.cs:1385`); the offscreen colour image still ends the frame in `SampledReadVfc`
(`:4312`) before the final composite and `commandBuffer2.End()`, so the `DisplayRenderPatch`
transpiler's injection point and `FrameCapture`'s layout assumption both still hold; and the new
**CMAA2** anti-aliasing path (rev 5156) renders into *its own* `_sceneLdrTarget` and only *samples*
the offscreen image, so it does not disturb that layout in either AA mode.
`SuperMeshRenderSystem.{RenderMainPass,RenderTranslucencyPass}` signatures are unchanged, and
`RenderMainPass` is still called inside the offscreen target's `BeginRendering`/`EndRendering` scope.

**Three silent semantic breaks — all "a gatOS write now does nothing, with no read to explain it":**
1. **rev 5143 — `FlightComputer.RCSMode` now gates MANUAL RCS.** `ComputeRcsControl` zeroes
   `ThrusterCommandFlags` outright when RCS is off (`FlightComputer.cs:471`), so `ctl/translate` and
   `ctl/rotate` become no-ops. **This falsified an explicit claim in `ksa-write-surface.md`** (that
   manual flags were *not* gated by `RCSMode`) and inverted a `docs/VALIDATION.md` checklist item.
   Closed by exposing `ctl/rcs_mode` (read + Solver-phase write).
2. **rev 5128 — the game now clears the latched thruster flags** via the new
   `Vehicle.ClearHeldPlayerInput()`, including *every update* while ImGui holds keyboard focus or
   time warp exceeds 30×. gatOS's "latches until rewritten" contract no longer holds unilaterally.
   Documented (no code change); throttle/ignite are unaffected.
3. **rev 5132 — a decoupler can be disabled** (`Decoupler` gained `IEnable`; `SetIsActive` is gated on
   `IsEnabled`), so `decoupler.fire` was a silent no-op **reported as success**. Closed: `Fire`
   returns **EOPNOTSUPP** and `decouplers/<n>/enabled` is now readable.

**Inherited drift, no code change:** encounter *population* widened again (rev 5141 — near-coplanar
SOI encounters such as a Hohmann transfer to Luna are now predicted; the API is identical), RCS thrust
reduced overall with small thrusters weakened (rev 5119), size-D/E SRB nozzle grain sizes corrected
(rev 5124), and part mass/moment-of-inertia computation fixed plus a new tangent-ogive mass type
(rev 5166) — all of which move `/sim` *values* without moving the surface.

Clean bill for every other binding: all 13 reflection accessors and all 7 Harmony targets resolve
against 5168 (including the `KittenEva._renderable → _characterAvatar → Core → Scale` chain, despite
heavy kitten-locomotion churn in revs 5128–5144). Full detail:
[read](ksa-read-surface.md#5168-findings) / [write](ksa-write-surface.md#5168-findings).
Build + suite green against 5168 (0 warnings, 772 passed / 11 skipped); the sibling **purrTTY** mod
needed the same rev-5154 migration and was fixed alongside (see `ksa-runtime-coupling.md`).
5168 is now the verified baseline.

---

**The 5018 → 5117 pass (2026-08-01) — one compile break fixed, one High-risk binding re-routed, two
silent semantic drifts documented.** {#5117-pass}
The drop supplied as PREVIOUS was `2026.7.10.5056`, but 5056 had only ever been build-checked — the
changelog/decomp/Content diff was never run — so **5018 was the last fully-audited baseline** and a
5056→5117 diff would have silently skipped 37 revisions. The pass instead diffed
**`git diff 3106557 HEAD`** inside the main checkout (5018 → 5117, revs 5019–5116, 97 commits), which
both closes the 5056 gap and validates 5117 in one sweep. *Method note for next time: prefer the main
checkout's git history over the `_prev` sibling whenever `_prev` is not itself an audited baseline.*

Build-as-alarm caught one break: **rev 5114 renamed `NavBallData.DeltaVInVacuum` → `DeltaV`**
(`VesselReader.SampleNavball`, the single call site). The same revision silently changed **both**
navball performance values — `DeltaV` is now the active staging sequence's propellant-aware Δv
(`Parts.PerformanceSequences.FindActiveSequenceDeltaV()`) rather than the whole-stack vacuum rocket
equation, and `ThrustWeightRatio`'s numerator became `ComputeActiveThrust(AtmosphericPressure)`, making
it atmosphere-corrected; the latter compiles clean and would otherwise have gone unnoticed. Per the
maintainer's call this pass applied the **binding fix only** (no `SimSnapshot` field rename, no SPEC
rewording), so `NavballSnapshot.DeltaVVacuumMs` and SPEC §3.4.3's "vacuum Δv" wording now overstate the
value's provenance.

The second fix was **revs 5059/5097**, which split the volumetric-trail subsystem apart and moved
`VolumetricTrailRenderer.ExpansionTimeSeconds` onto the new `PlumeTrailSettings`. Because the game's own
Plume Trails debug window still exposes that field (its "Profile" section now delegates to
`PlumeTrailSegmentsManager.OnDrawProfileUi`), the node was **re-bound rather than dropped**, via the new
two-hop `FxReflect.TrailSettings` accessor with its own `fx.trail_settings` health latch — no `/sim` or
SPEC change. Beyond that: two **semantic drifts with no code change** (substance phase names, rev 5095;
encounter population, revs 5106/5110), one docking-identity behaviour change (rev 5076), and a clean
bill for every Harmony hook, every reflection accessor, the `thug_life` render pipeline
(`SuperMeshRenderSystem.cs` byte-identical), the terrain UBO writes and the audio actuator.
Full detail: [read](ksa-read-surface.md#5117-findings) / [write](ksa-write-surface.md#5117-findings).
Build + suite green against 5117 (0 warnings, 769 passed / 11 skipped). 5117 is now the verified baseline.

---

**The 4980 → 5018 pass (2026-07-24) — one compile break, fixed; one coverage gap opened.** {#5018-pass}
The 5018 drop's `version.json` is gapless (`fromRevision` 4980 = the prior baseline; revs 4981–5018
logged). Build-as-alarm caught the one break: **rev 4992 (solid rocket motors) renamed
`Mole.GetLiquidMass` → `Mole.GetStoredMass`** as part of generalizing propellant storage from
liquid-only to a new `ISubstanceStore` (`Liquid | Solid`) abstraction — `GetLiquidVolume` →
`GetStoredVolume`, `ConsumeLiquid`/`ProduceLiquid` → `ConsumeStored`/`ProduceStored`, `ContainsLiquid`
deleted, `Mole` gained `IsSolid`/`Solid`/`IsStorable`. `VesselReader.SampleTanks` was the single call
site; after the one-word fix the full solution compiles 0-warning against the 5018 DLLs and the whole
suite passes (681 passed / 11 skipped / 0 failed). **Values are unchanged** — `Tank` moles are liquids.

**The coverage gap it opened — closed in the same work item.** Solid propellant lives on the **new
`SolidGrainSegment` module**, which implements `ISubstanceStore` but is **not a `Tank`**, so it is
absent from `/sim`'s `tanks/`; meanwhile `Vehicle.PropellantMass` is now recomputed from
`Parts.SubstanceStores` (`VehicleProperties.RecomputeMassProperties` retyped `ReadOnlySpan<Tank>` →
`ReadOnlySpan<ISubstanceStore>`) and **does** include grain mass — so on an SRB vessel
`mass/propellant` > Σ `tanks/<r>/amount`. gatOS gained a dedicated **`srb/<n>/`** read surface
(`VesselReader.SampleSrbs`, SPEC §3.4.8) binding `Parts.RocketCores.{Modules,GetState}` filtered to
`SolidMotor` plus the `SolidGrainSegment` geometry/mass members: grain mass, usable mass (net of the
unburnable sliver), fraction, burn time, mass flow, chamber + exit conditions, burning area, nozzle
area ratio, stack validity, and a per-segment `segments/<m>/` breakdown. **Read-only** — KSA forces a
solid's throttle to 0 or 1 (`SolidMotor.UpdateState`), so ignition stays on the engine surface and
`srb/<n>/engine` cross-links to the `engines/<n>` entry that lights the motor.

Everything else verified unchanged: `Universe.cs`, `FlightComputer.cs`, `DockingPort.cs`, `Decoupler.cs`,
`LightModule.cs`, `ThrusterController.cs`, `InputEvents.cs`, `Battery.cs`, `Camera.cs`, `Orbit.cs`,
`NavBallData.cs`, `Encounter.cs`, `RocketControllerData.cs`, `EngineControllerState.cs`,
`ManualControlInputs.cs`, `PartModel*.cs`, `SuperMeshRenderSystem.cs` are **byte-identical**;
`Vehicle._manualControlInputs` still `:232`; **`Program.RenderGame`'s body is byte-identical**, so the
display transpiler's final-`End()` site is untouched; the two Harmony hook targets only moved lines
(`DrawProgramMenusHook` `:3690`, `MainViewport` `:439`, `ModLibrary.Find` `:175` — all resolved by name).
No `Brutal.*` decomp namespace changed except `RenderCore/SimpleVkTexture.cs` (a `[Conditional("DEBUG")]`
logging helper removed) — **no frames/numerics drift**. **Semantic drift inherited, no API change**:
encounter candidacy widened (rev 4991 — the flat SOI ≤ 10⁶·⁷ m cutoff became an orbital-geometry +
approximate-MOID test, so small moons like Phobos/Deimos now produce `encounters/<n>/` rows that 4980
skipped); `Module.List` now stores same-concrete-type modules in contiguous segments (rev 4990 —
`IModuleTypeList.GetUsing<T>()` → `GetSegmentUsing<T>(int, out bool)`, but `ModuleList.Get<T>()`/
`HasAny<T>()`/`GetState`/`TryGetFrom` are signature- and semantics-identical, so gatOS's positional
`/sim` indices are unaffected). Behavior notes: `/sim/debug/refuel` now refills SRB grains for free
(`ResourceManager.RefillAllTanks` walks `ISubstanceStore`); SRBs *are* `EngineController`s
(`SolidMotor : RocketCore`) so `engines/<n>` covers them, though throttle commands are physically inert
on a solid; `ModuleBase.OnPartCreated` → `OnFullPartCreated` is a rename of a virtual gatOS never
overrides (the `Part.cs` call site is unchanged, so `AnimationLinks`' solar-panel link still forms);
`Part.Connector` gained `Capabilities`/`EndpointCapabilities` and rev 5007 swapped `_decouplerConnections`
for a `DecouplerJoint` flag — both additive, `Decoupler.cs` byte-identical. Content: `Astronomicals.xml`
changed **only** in Earth ground-clutter/tree authoring — **no body mass/radius/SOI/orbital-element
edits**, so `/sim/bodies/*` is untouched; `UnlitMeshVert`/`UnlitMeshFrag` and `Shaders/Mesh/UnlitMesh.*`
(the thug_life quad) are unchanged, and rev 4988's MilkyWay split shrank `GlobalShaderBindings`
(11 → 10 layout bindings) which the quad does not use. Findings detail:
[read](ksa-read-surface.md#5018-findings) / [write](ksa-write-surface.md#5018-findings) 5018 sections.
Live re-check items: `docs/VALIDATION.md`. 5018 is now the verified baseline.

**The 4939 → 4980 pass (2026-07-22) — one compile break, fixed; otherwise clean.** The 4980 drop's
`version.json` is gapless (`fromRevision` 4939 = the prior baseline; revs 4940–4980 logged, last logged
commit 4978). Build-as-alarm caught the one break: **rev 4943 removed
`InputEvents.VehicleDockingInputData.OldMeanRadius`** (docking/decouple camera zoom-jump fix — the
camera follow no longer needs the stashed radius); `DockingActuator.Undock` dropped the field
(the game's own UnDock enqueue is now `{Vehicle, DockingPort, Undock}`, `DockingPort.cs:145-150`;
`DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)` byte-identical). After the one-line
fix the full solution compiles 0-warning against the 4980 DLLs and all tests pass. Everything else
verified unchanged: `Tank.cs`/`Mole.cs`/`EngineControllerState.cs`/`ManualControlInputs.cs`/
`ThrusterController.cs`/`BurnTarget.cs` byte-identical; `Vehicle._manualControlInputs` still `:232`;
both Harmony hook targets and the display transpiler's final-`End()` site intact (rev 4942's
`ScreenshotCapture` inserts *before* it, additively); `RenderMainPass` untouched (the shadow rework is
cascade-path only); `Orbit.cs` churn is map-hover picking (unbound). **Semantic drift inherited, no API
change**: `FlightComputer.RCSMode` (revs 4946/4949/4975 — RCS toggle gates auto attitude holds; RCS-only
vessels silently stop actuating when toggled off; `CopyFrom` copies it, Solver discipline intact) and
the `RollMode` default flip `Up`→`Decoupled` (rev 4978 — fresh FCs no longer hold `attitude_target`
roll). Behavior notes: control-module name stamps survive splits (4950), density fallback mass (4955),
fuel-flow default furthest-to-nearest-by-stage + persisted per-engine `FlowRule` (4957/4958/4965 —
drain order only), the verlet/CCI-drag fix changes high-warp physics values (4977). Content: the only
schema-adjacent change is texture `Category="Terrain"`→`"TerrainHeight"` (rev 4947) — celestial texture
elements only, orbital/body schema untouched (the `apollo11-system` generator emits no texture
`Category`). Findings detail: [read](ksa-read-surface.md#4980-findings) /
[write](ksa-write-surface.md#4980-findings) 4980 sections. No `plans/` gap plan needed. Live re-check
items: `docs/VALIDATION.md`. 4980 is now the verified baseline.

**The 4892 → 4939 pass (2026-07-16) — clean, no code changes.** The 4939 drop's `version.json` is
**gapless** for the first time (`fromRevision` 4892 = the prior verified baseline; revs 4893–4939 all
logged), so playbook step 1 worked as designed; the decomp/Content diff (`git diff 7cf5c0a..2423a02`
inside the checkout) confirmed it. Result: full solution compiles 0-warning against the 4939 DLLs
(forced non-incremental), all tests green, and **no bound member changed name, signature, type, unit,
frame, or gating** — `EngineController.cs`, `FlightComputer.cs`, `DockingPort.cs`, `Decoupler.cs`,
`ThrusterController.cs`, `LightModule.cs`, `Battery.cs`, `SolarPanel.cs`, `Orbit.cs`, `Camera.cs`,
`SuperMeshRenderSystem.cs`, `GameAudio.cs`, `Mole.cs`, `ManualControlInputs.cs` and every `Brutal*`
numerics file are entirely untouched. The headline upstream changes are all additive or UI-layer: the
**fuel-line / tank-transfer / propellant-use system** (revs 4903–4938 — `Tank` gains
`PropellantUseEnabled`/`TransferMode`/transfer statics; `Tank.Moles` and the whole moles read path
untouched; tank game data moved `PartGameData.xml` → `CoreFuelTankAGameData.xml` with the identical
`<Tank>` schema; volume *display* switched to liters — `VolumeReference`/`Constants` formatters only);
the **rev 4914 control-module lockout** (staging key / engine Active checkboxes / Decouple menu now
`ControlsLockout`-gated) lands **only in UI/input paths** — the module methods gatOS binds carry no new
gate; the in-flight **Sequence UI rework** (+1137 lines in `SequenceList.cs`, all window drawing —
`ActivateNextSequence` intact); **animating parts now update colliders and force off-rails** (rev 4930);
and heavy render churn (screenspace particles, volumetric plume trails, ground-clutter culling) that
never reaches a gatOS binding (`RenderGame`'s tail — the display transpiler's injection site — is
byte-identical; `UnlitMesh` keys/assets unchanged). Rev 4915 removes the old service-module parts —
**save-breaking upstream** (the second, after 4884). Findings detail:
[read](ksa-read-surface.md#4939-findings) / [write](ksa-write-surface.md#4939-findings) 4939 sections.
No `plans/` gap plan was needed. Live re-check items: `docs/VALIDATION.md`. 4939 is now the verified
baseline.

**The 4826 → 4892 pass (2026-07-14) — clean, no code changes.** ⚠️ **Changelog gap:** the 4892 drop's
`version.json` covers only revs 4860→4892 and the 4826 drop's only 4824→4826 — **revs 4827–4859 have no
changelog anywhere**, so the pass was driven by `git diff 1265373..7cf5c0a` (4826 → 4892) over
`current/decomp` + `current/Content` inside the assemblies checkout. Result: full solution compiles
0-warning against the 4892 DLLs (forced non-incremental), all tests green, and **no bound member changed
name, signature, type, unit, frame, or gating**. The headline upstream change — rev 4884's save-breaking
**combustion→Reactions / tank-affinity refactor** (`ModLibrary` now registers `ReactionTemplate`; `Tank`
gains `RoleAffinity`/`AssignedMix`/`Assign`; `PartTemplate.Tank` removed; substances
Nepetalactone/Actinidine removed, methalox/hydrazine/APCP added) — is **additive to every gatOS binding**
(`Tank.Moles`, `Mole`/`MoleState`, `FilledFraction`, `PartTree.RefillConsumables` all untouched; gatOS
never referenced `PartTemplate.Tank` or the combustion templates). Other churn checked and cleared:
`Staging.cs` deleted (the staging *window* became `ResourceGroups` — gatOS binds
`SequenceList.ActivateNextSequence`, intact, now with batched `RemoveSpentSequences`); the 4873/4880/4890
decoupling perf refactor (incl. a module id-lookup fix that *improves* post-split `Modules.Get<T>`
correctness); `FlightComputer.CommandEngineThrottles` zeroing (see the write page); additive
`EngineController.SeaLevelData`, `PhysicsEnvironment.AtmosphereRadius`, `Camera` orthographic mode,
`SimplePipelineCreator.AlphaToCoverageEnable`, `ReverseDepthBufferUtils.CreateOrthographicReverseZ`;
particle-emitter Handle refactor (`Celestial`/`Decoupler`/`Tank` emitter plumbing gatOS never touches).
Findings detail: [read](ksa-read-surface.md#4892-findings) / [write](ksa-write-surface.md) 4892 sections.
No `plans/` gap plan was needed. Live re-check items: `docs/VALIDATION.md`. 4892 is now the verified
baseline.

**The 4750 → 4826 pass (2026-07-03) — clean, no code changes.** ⚠️ **Changelog gap:** the 4826
checkout's `version.json` is an *incremental* log covering only revs 4824→4826 (the terrain-sampling
perf work) — **revs 4751–4823 have no changelog in either checkout**, so the pass was driven by a full
`git diff --no-index` of the two `decomp/` + `Content/` trees instead of the commit log (playbook step 1
is blind for this jump). Result: `gatOS.GameMod` + the full solution compile 0-warning against the 4826
DLLs, all tests green, and **no bound member changed name, signature, type, unit, frame, or gating** —
13 bound decomp files are byte-identical to 4750, and the heavy churn (staging editor rework, `Part.cs`
symmetry infrastructure, terrain-impact prediction, ice/wetness FX) misses the gatOS surface. Findings
(all game-behavior notes, no drift): post-decouple control-state inheritance
([read 4826 findings](ksa-read-surface.md#4826-findings)), the `Decoupler.Decouple` deactivation-cascade
removal, a near-SoI gravitation-refactor nuance, and the solar-cell stock value 50→100 W (below). No
`plans/` gap plan was needed. Live re-check items: `docs/VALIDATION.md`. 4826 is now the verified baseline.

> The ksa skill (`.agents/skills/ksa/`) also points at decompiled sources under `decomp/ksa/` (and a
> working copy lives at `…/unscience/decomp/ksa`). Any of these decomp trees is readable; for
> break-checking use the one **versioned with the DLLs you're building against** (the game-assemblies
> checkout) so source and binary match. (`[KsaAnchor].SourceFile`'s docstring says "under
> `thirdparty/ksa`" — that pointer is stale; the values are relative paths like `KSA/Vehicle.cs` that
> resolve under any decomp checkout's `current/decomp/`.)

### How gatOS consumes each artifact

| Artifact | Consumed by | Mechanism |
|---|---|---|
| `current/dll/KSA.dll`, `Brutal*.dll`, `Planet*.dll` | `gatOS.GameMod` compile | `KSAFolder` in `Directory.Build.props`; `<Reference Private=false Condition=Exists(...)>` (guarded, so game-free projects build without them) |
| `current/dll/Bepu{Physics,Utilities}.dll` | `gatOS.GameMod` compile (IVA cabin physics only) | same mechanism. **KSA's own embedded rigid-body engine (BepuPhysics 2.5.0-beta.29), already loaded in-process** — gatOS creates its *own* `Simulation` against the exact same assembly rather than vendoring a physics engine. The sibling `ksa-game-assemblies` `copy-ksa.ts` glob list gained `Bepu*.dll` for this; a `KSAFolder` that has `KSA.dll` but not `BepuPhysics.dll` is *stale*, and the `VerifyBepuReference` MSBuild target fails the build with that fix rather than emitting 200 type errors. If a future KSA build drops or renames these, the fallback is the hand-rolled solver behind the `CabinSim` seam (plans/IVA_MOVEMENTS.md §3 Option C) |
| `current/decomp/KSA/*.cs` | humans (break-check) | the file each `[KsaAnchor].SourceFile` names; diff old vs new here for semantic drift |
| `current/decomp/Brutal*/`, `Planet*/` | humans (numerics/terrain) | Brutal numerics live here (rev 4729 bump); see [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#frames-and-numerics) |
| `current/Content/**.xml` | humans (units/values) + KSA at runtime | part templates the KSA modules deserialize; they define the **field names, units and stock magnitudes** the gatOS reads return |
| `current/version.json` | humans (changelog) | the per-revision commit log — playbook step 1 |

> **Decomp may lag the shipping binary.** Field names in decomp can differ from the runtime DLL. When a
> read returns null / a count is `-1` / reflection misses, use KSA's runtime reflection-dump strategy
> (ksa skill `debug.md`) to discover the real structure. The DLLs in `current/dll/` are authoritative;
> the decomp is the readable approximation.

---

## Asset XML that backs gatOS integration points

gatOS does not read Content XML directly, but these files **define** the runtime values its sensors
report and the field names the KSA modules expect. They are the ground truth for units and stock values —
and the fastest way to confirm a rename actually landed. Concrete files (current/4826):

| gatOS integration point | Asset file (`current/Content/…`) | Relevant XML | Confirms |
|---|---|---|---|
| Docking pushoff / latching | `Core/CoreCouplingAGameData.xml` | `<DockingPort><LatchingKineticEnergy J="50"/><PushoffImpulse Ns="7000"/></DockingPort>` | rev 4683 rename + units: pushoff is **N·s** (impulse), latching is **J** (kinetic energy). Stock value numerically 7000 but now N·s. |
| Battery capacity | `Core/CoreElectricalAGameData.xml` | `<Battery HasStatusLight="true"><MaximumCapacity J="1000"/></Battery>` (also 3000/100/500) | capacity is **Joules** — `battery/capacity` unit unchanged. |
| Solar / generator production | `Core/CoreElectricalAGameData.xml` | `<SolarPanel><Produced W="200"/></SolarPanel>` (cells `W="100"` — 4826 doubled the stock `SolarPanelB_CellA` value from `W="50"`, same unit) | rev 4681: production authored in **Watts** — confirms `power/produced`, `solar/<n>/produced` are instantaneous W. |
| Control authority (`IsControllable`) | `Core/CoreCommandAGameData.xml` | `CoreCommandA_Prefab_MediumCapsuleVariantA` has `<Control />` | rev 4699: the new Control Module is on the capsule in XML; vehicles without `<Control />` are not controllable. |
| Engines / tanks / lights / RCS / decouplers / animations | `Core/Core*GameData.xml` (Propulsion, Electrical, Coupling, …) | `<EngineController>`, `<Tank>`/`<Mole>`, `<LightModule>`, `<ThrusterController>`, `<Decoupler>`, `<KeyframeAnimation>` | the module element names the readers/actuators bind to; no 4750 changes; 4826 adds only `<ConnectorRef>`/`<Aligned>` (the new symmetry connectors) + `<CombustionProcess>` entries; 4892 (rev 4884) migrates `<Combustion Id="…"/>` → `<Reaction Id="…">` and adds `<RoleAffinity>` on tanks (`PartGameData.xml`); 4939 (rev 4934) moves all `<Tank>` game data out of `PartGameData.xml` into `CoreFuelTankAGameData.xml` (identical element schema) and adds the `<FuelPort>` module — template *configuration* churn only, no module element gatOS binds changed. |
| **Substance / propellant display names** (`tanks/<n>/substance`, `srb/<n>/substance`) | `Core/Volatiles.xml`, `Core/SolidPropellants.xml` | `<Substance Id="Kerosene" DefaultPhase="Liquid">`, `<Substance Id="He" DefaultPhase="Gas">`, `<Substance Id="APCP" DefaultPhase="Solid">`, plus the new `<Color>` element | **rev 5095 changed the published strings.** The new `DefaultPhase` attribute drives `SubstanceTemplate.BuildPhaseName`: the default phase renders **bare** and non-default phases take a qualifier (`Gas` → `"X Vapor"`, `Liquid` → `"Liquid X"`, `Solid` → `"X Ice"`), replacing the old unconditional `"Solid "`/`"Liquid "`/`"Gaseous "` prefixes. Net: `"Liquid Kerosene"` → `"Kerosene"`, `"Solid APCP"` → `"APCP"`; gas-default substances keep `"Liquid O2"`/`"Liquid H2"`/`"Liquid CH4"`. gatOS passes the string through verbatim — no code change, but string-matching guest programs break. |
| Part template ids (dynamic add) | `Core/Core*GameData.xml` `PartGameData Id="…"` | e.g. `CoreCouplingA_Prefab_DockingPort1WA` | the string ids `ModLibrary.Get<PartTemplate>(id)` resolves (not used by `/sim` reads; reference). |
| **`thug_life` quad shaders** | `Core/Shaders/Mesh/UnlitMesh.{vert,frag}` | the `"UnlitMeshVert"`/`"UnlitMeshFrag"` `ShaderReference` keys `ThugLifeQuadRenderer.BuildPipeline` resolves via `ModLibrary.Get<ShaderReference>(...)` | the world-space quad reuses KSA's stock unlit-mesh shaders; if these keys/assets are renamed/removed the pipeline build fails (caught, feature self-disables). |
| **Sticker decal shader includes** | `Core/DefaultAssets.xml:367` (`<Shader Id="GridFrag" Path="Shaders/Grid.frag" />`); `Core/Shaders/Common/{Camera,Global,TextureSet,Extensions}.glsl` | `StickerDecalRenderer.ShaderIncludeDirectory()` resolves `ModLibrary.Get<ShaderReference>("GridFrag").ModPath` and takes only its **directory** — the shaders themselves are gatOS C# string constants, compiled by `ShaderModuleUtils.FromString` | gatOS writes its own GLSL but **includes KSA's**: `#include "Common/Camera.glsl"` (which includes `Global.glsl`) and `#include "Common/TextureSet.glsl"`. `shaderc` resolves an `#include` relative to the **directory of the debug name**, so the debug name must be a real path next to `Grid.frag`; the `GridFrag` asset is used purely to **discover that directory from the install** rather than hard-coding it. Renaming/removing the asset fails the pipeline build (caught, `renderer=degraded`); moving the `Common/` headers or changing their contents is a **silent** compile failure at the same point. |

---

## Render-internals references — `thug_life` (the deepest, highest-churn coupling) {#render-refs}

The `thug_life` cheat (`Game/Ksa/ThugLife/`, ported from `unscience`) is gatOS's **first custom GPU
rendering** and its **deepest coupling into KSA's render-pipeline internals** — render internals churn far
faster than the gameplay APIs the rest of the surface binds, so this set is **High churn** and the one most
worth re-verifying on any game update. It pulled in **new reference DLLs** and a project-level flag:

| Added to `gatOS.GameMod.csproj` | Why | Notes |
|---|---|---|
| `Brutal.Vulkan`, `Brutal.Vulkan.Abstractions`, `Brutal.Vulkan.Vma` | the Vulkan pipeline/descriptor/buffer/staging surface (`SimpleVkTexture`, `VkUtils.{UploadBufferToImage,StageAndUploadToBuffer}`, `DeviceEx.CreateSampler`, allocator/VMA staging pools) | `<Private>false</Private>`, condition-guarded on `$(KSAFolder)` like the other KSA refs |
| `Planet.Render.Core` | `Renderer` (Device/Allocator/DynamicStateInfo/ViewportState/Graphics), `RenderTechnique.CreateShaderStages`, `Presets`/`RenderingPresets`, `Program.{GetRenderer,OffscreenTarget,SetViewport}` (was `OffScreenPass` before 5168/rev 5154) | `<Private>false</Private>`, guarded |
| `Brutal.Core.Memory` | unmanaged buffer/staging helpers for the GPU upload path | `<Private>false</Private>`, guarded |
| `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` | `ThugLifeQuadRenderer` is `unsafe` (raw pointer work for the Vulkan buffer uploads / descriptor writes) | first use of `unsafe` in gatOS |

**Pipeline assumptions baked into `ThugLifeQuadRenderer.BuildPipeline`** (any of these moving silently
breaks the draw — re-verify live):
- Reuses KSA's stock unlit-mesh shaders via the `"UnlitMeshVert"`/`"UnlitMeshFrag"` `ShaderReference` keys
  (assets `Content/Core/Shaders/Mesh/UnlitMesh.{vert,frag}` — see the asset table above).
- Texture format `R8G8B8A8UNorm` (the sunglasses texture, built from a static 26×5 char grid in
  `ThugLifeTexturePattern` → `ThugLifeTextureFactory`).
- **Reverse-Z** depth convention and — **since 5168 (rev 5154)** — the
  **`Program.OffscreenTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo)`** call that
  stamps the pipeline's attachment formats and MSAA sample count (the quad must be depth-tested and
  MSAA-resolved consistently with the scene).
  > **5168 BREAKING (rev 5154):** offscreen rendering moved off `VkRenderPass`/framebuffers onto
  > **Vulkan dynamic rendering**, and `Program.OffScreenPass` — together with the whole
  > `Core.RenderPassState`, `Core.DynamicRenderState`, `KSA.OffscreenTarget`, `KSA.RenderTarget` and
  > `KSA.Framebuffer` set — was **deleted**. `BuildPipeline` no longer sets `RenderPass`/`Subpass` or
  > builds its own `VkPipelineMultisampleStateCreateInfo`; it calls
  > `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` last, exactly as KSA's own
  > `GenericMeshRenderer`/`PartModelRenderer`/`PartModelGlass` do. That helper supplies
  > `VkPipelineRenderingCreateInfo` (colour/depth/stencil formats), forces `RenderPass = NullHandle`
  > and sets `RasterizationSamples` from the target — so the quad now **tracks** the engine's sample
  > count (including the new CMAA2 option, rev 5156) instead of hard-binding one. Beware the name
  > collision: the **new** `KSA.Rendering.RenderTarget` is an unrelated class that reuses the deleted
  > one's name. Verified `RenderMainPass` is still called inside `_offscreenTarget.BeginRendering(…)`
  > … `EndRendering(…)` (`KSA/Program.cs:4185/4226/4233`), so the postfix still draws into the scene
  > target.
  **5117 note (revs 5057/5058):** the alpha-to-coverage attachment became conditional
  (`OffscreenTarget.HasAlphaToCoverageAttachment` — MSAA-only — and its format was pinned to a new
  `OffscreenTarget.AlphaToCoverageFormat = R8UNorm`). This does **not** affect the quad: A2C is a
  transient attachment that is *not* a member of the offscreen render pass, whose
  `OffscreenTarget.CreateRenderPass` is still the same four attachments (resolve colour/depth + MSAA
  colour/depth), so the pipeline's single colour-blend attachment stays compatible. The rev 5058
  `SimplePipelineCreator` A2C stale-state fix is likewise out of reach — `BuildPipeline` supplies its own
  `VkPipelineMultisampleStateCreateInfo` instead of going through that creator.
- Draw injected via a Harmony postfix on `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`
  (`KSA/SuperMeshRenderSystem.cs:329`) — the runtime coupling, see
  [`ksa-runtime-coupling.md#thug-life-patch`](ksa-runtime-coupling.md#thug-life-patch).

Full anchor list: [`ksa-read-surface.md#thug-life`](ksa-read-surface.md#thug-life) (anchor math),
[`ksa-write-surface.md#thug-life`](ksa-write-surface.md#thug-life) (the seven actions),
[`../docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md) (render set).
**Re-verified (static) 2026-08-23 against `2026.8.22.5348`** (revs 5262–5348, 85 commits) — the
render set survived a heavy render-internals window with **no compile break and no re-bind**:

- **`KSA.Rendering/RenderTarget.cs` is untouched.** `ResolveAttachments(CommandBuffer)` and
  `SetupGraphicsPipeline` are identical, so both the `gatos.stickers` postfix target and the quad's
  pipeline-format handshake hold. `KSA/RenderingPresets.cs` and
  `Brutal.VulkanApi.Abstractions/Presets.cs` (reverse-Z depth, blend, rasterization presets) are
  likewise untouched.
- **`Content/Core/Shaders/Mesh/UnlitMesh.{vert,frag}` and `Common/Shared.glsl` are untouched** —
  push-constant layout, vertex inputs and the single combined-image-sampler binding all still match
  the `thug_life` pipeline. `Shared.glsl` does **not** include `Global.glsl`, so the lighting-UBO
  rework below cannot reach the quad at all. `Grid.{vert,frag}` untouched (the sticker include
  directory's discovery asset).
- **`SuperMeshRenderSystem.RenderMainPass(CommandBuffer)` is still exactly one overload**, so both
  gatOS lookups (Apply and Remove) stay unambiguous. Its body is now wrapped in
  `using (commandBuffer.TagRegion(Profiler.GpuTag.MeshRendererV2))`; a Harmony postfix runs *after*
  the `finally`, so the quad draws are attributed **outside** that GPU tag. **Profiler attribution
  only — no mis-draw, and the patch still installs.**
- **The `Program.RenderGame` transpiler still lands.** The new tail is
  `_screenshotCapture.OnRenderGameSwapchainGrab(…); Profiler.Gpu.EndFrame(commandBuffer2);
  commandBuffer2.End();` — `EndFrame` is not named `End` and `Profiler` is in namespace `KSA`, so both
  transpiler filters reject it; the new `TagRegion` `using` blocks emit `GpuRegion.Dispose()`, never an
  inlined `End`. `codes[callIdx-1]` is still the `ldloc` of `commandBuffer2`, and
  `VkDeviceExtensions.End<T>` is zero-diff.
- **Vulkan 1.3 → 1.4 (rev 5315) is a no-op for gatOS.** It declares no API version, no extensions and
  no features, and reuses `Program.GetRenderer().Device`; `ShaderModuleUtils` maps 1.4 to SPIR-V
  **`_1_6` — the SPIR-V target is unchanged**, so the runtime-compiled sticker GLSL and the
  `UnlitMesh*` shaders produce the same SPIR-V, and every Vulkan struct gatOS fills is unchanged.
  *Environment note only:* the mod now inherits a Vulkan 1.4 device requirement.
- **The lighting-UBO reshape (rev 5301) is safe by construction.** `UboLightingData` swapped four
  portrait-light arrays for 16-entry forward-light arrays and `Global.glsl` matched, growing the UBO
  stride. gatOS compiles its GLSL **at runtime against the shipped headers** and takes the dynamic
  offset from `GlobalShaderBindings.DynamicOffset(…)`; the fields its shader reads
  (`global.camera.*`, `global.lighting.{sunPosition,planetColor,sunColor}`) are the struct's leading
  members and are untouched. Re-verify live only because a **stale SPIR-V cache** would be fatal.
  Per-viewport light modes (`Viewport.LightMode : EViewportLightMode`, +4 lines) evaluate to exactly
  the previous hardcoded `UseShadows`/`UseLightPrePass` constants.
- **NEW coupling — `UiCoverageMaskSystem` (rev 5283).** Not a render-*set* break, but the one render
  change this pass that reached a gatOS feature: it stamps the reverse-Z near plane into the pre-pass
  depth under opaque ImGui UI, which `/sim/display` was capturing as black chrome. Closed by a Harmony
  prefix on `GameSettings.UiPixelCulling()` — see [`#5348-pass`](#5348-pass) C1 and
  [`ksa-runtime-coupling.md#threading-phases`](ksa-runtime-coupling.md#threading-phases).
- Also cleared: `PartModel.AddInstance` gained a `viewport == Program.MainViewport` guard (rev 5308) —
  same signature, still one overload, so the positional `__0`/`__1` args bind and the narrowing is
  *helpful*; `Utils.{Begin,End}GpuDebugLabel` were **deleted** (rev 5300, replaced by `TagRegion`) and
  gatOS never called them; the rev-5288 clutter GPU repack and the revs-5287/5289 exclusion-mask
  descriptor growth do not reach `ClutterTextureBridge` (it only swaps a bindless descriptor) or
  `StickerDecalRenderer` (it reconstructs the surface from the resolved depth buffer); and the
  multi-viewport leak audit found **no gatOS injection leaks** — `RenderMainPass` call count is still
  3 and `ResolveAttachments` still 3. ⚠️ One claim in the `thug_life` anchors **became false**: crew
  portrait viewports are no longer always `Visible` (revs 5276/5295 gate them on
  `GameSettings.ShowCrewPortraitCameras()` and occupancy), so the `Cameras & Crew` pass bit simply goes
  unused; `Program.GetCrewPortraitViewport(0|1)` and `_crewPortraitViewportStart = 4` are unchanged, so
  `ThugLifeManager.CurrentPassBit()` still classifies correctly.

Prior stamp — **Re-verified (static) 2026-08-01 against `2026.8.3.5117`**: `SuperMeshRenderSystem.cs` is
**byte-identical** across the whole 5018→5117 window (so `RenderMainPass` and the postfix target are
untouched); `Program.OffScreenPass` and `OffscreenTarget.CreateRenderPass` unchanged; the
`UnlitMeshVert`/`UnlitMeshFrag` keys still resolve (`NavBallRenderer` uses the same pair) and
`RenderingPresets.ReverseZDepthStencil.DepthTestWrite` is intact — see the A2C note above for the one
render-side change that *looked* relevant and is not. Prior stamp — **Re-verified
(static) 2026-07-22 against `2026.7.8.4980`**: `RenderMainPass(CommandBuffer)` and its body
**identical** — the whole `SuperMeshRenderSystem.cs` diff is the cascaded-shadow rework
(`RenderShadowPass` gained an `int cascadeIndex` param, depth pipelines an int push-constant, PBR
pipelines a CSM-filter fragment specialization constant, ID 10) and none of it touches the main color
pass; `Program.OffScreenPass` (`:403`), `ColorFormat=R16G16B16A16SFloat`,
`SampleCount=GameSettings.GetSampleCount()`, reverse-Z and `CreateRenderPass(Clear, Load)` all
unchanged; `UboVesselData`'s new navball-marker float4s and `MeshDrawContext`/`MeshRenderTechnique`'s
cascade-index plumbing never reach the quad's own pipeline/descriptors; `Part` ego members
(`PositionEgo`, `Asmb2Ego`, `Asmb2VehicleAsmb`) untouched. ⚠ New (rev 4942): the scaled-screenshot
path (`GameSettings.SampleCountOverride` + `RebuildRenderer`) can transiently rebuild the renderer at
1 sample while the quad pipeline (built once, no rebuild listener) still assumes the old
`OffScreenPass.SampleCount` — see
[`ksa-runtime-coupling.md#thug-life-patch`](ksa-runtime-coupling.md#thug-life-patch). **Re-verified
(static) 2026-07-16 against `2026.7.6.4939`**: `SuperMeshRenderSystem.cs` entirely untouched by the
4892→4939 diff (`RenderMainPass(CommandBuffer)` at `:329`), the `UnlitMesh.{vert,frag}` assets + the
`"UnlitMeshVert"`/`"UnlitMeshFrag"` `DefaultAssets.xml` keys unchanged (the DefaultAssets churn is
particle/volumetric-trail/ground-clutter shader keys), `Program.OffScreenPass`/`SampleCount` unchanged,
and the new screenspace-particle + volumetric-plume-trail renderers (revs 4894–4932) are mid-frame
compute/composite passes that do not alter the main render pass the quad draws in.
`RenderCore.Mesh/SimpleVkMeshAtlas`'s bounding-sphere-radius fix affects game-mesh culling only (the
quad builds its own vertex buffers). **Re-verified
(static) 2026-07-14 against `2026.7.5.4892`**: `SuperMeshRenderSystem.cs` untouched by the 4826→4892
diff (`RenderMainPass(CommandBuffer)` intact), `UnlitMesh.{vert,frag}` assets + the
`"UnlitMeshVert"`/`"UnlitMeshFrag"` `DefaultAssets.xml` keys unchanged (the DefaultAssets churn is
particle-updater shader renames), `Program.OffScreenPass`/`SampleCount` unchanged, reverse-Z
*perspective* path unchanged (`CreateOrthographicReverseZ` is a new additive helper for the editor's
new ortho camera), and `SimplePipelineCreator.AlphaToCoverageEnable` (new, default `false`) matches the
prior multisample behavior — gatOS builds its pipeline without SPC anyway. The 4861–4864/4886–4889
ground-clutter multi-material overhaul does not touch the quad's pipeline. **Verified
`2026-06-28` against `2026.6.9.4750`; re-verified (static) 2026-07-03 against `2026.7.3.4826`**:
`RenderMainPass(CommandBuffer)` byte-identical (the `SuperMeshRenderSystem.cs` diff only swaps
`AddMacroDefinition` overloads in `Setup*Renderers`), the `UnlitMesh.{vert,frag}` assets and
`"UnlitMeshVert"`/`"UnlitMeshFrag"` keys unchanged, `Program.OffScreenPass`/`RenderPassState` unchanged,
and the `Part` ego members untouched by the `Part.cs` symmetry churn. The 4826 shader-compiler churn
(`Brutal.ShaderCApi` delegates, `GlobalShaderBindings` "Frost" global binding, the new
`NormalTextureForSampling` pre-pass G-buffer) does not reach the quad's own descriptor set/pipeline.
Vulkan render-pass *compatibility* at draw time is only provable live — render
internals are not as reliably changelog-covered as the gameplay APIs, so this set leans on the
build-as-alarm + live re-verification (`docs/VALIDATION.md`).

---

## Subsystem → decomp/asset quick map

When a changelog line mentions a subsystem, open these. (Decomp paths relative to `current/decomp/`.)

| Subsystem | Decomp source(s) | Content XML | gatOS scope page |
|---|---|---|---|
| Vehicle core / control / throttle / situation / teleport | `KSA/Vehicle.cs` | (vehicle-wide) | reads, writes, runtime |
| Orbits / patches / encounters | `KSA/Orbit.cs`, `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | — | reads |
| Celestials / system / atmosphere / ocean | `KSA/Celestial.cs`, `KSA/StellarBody.cs`, `KSA/CelestialSystem.cs`, `KSA/IParentBody.cs`, `KSA/AtmosphereReference.cs`, `KSA/OceanReference.cs` | `Planets/…` | reads |
| Time / warp / universe / solver hook | `KSA/Universe.cs` | — | reads, runtime |
| Engines | `KSA/EngineController.cs`, `KSA/EngineControllerState.cs` | `Core/CorePropulsion*GameData.xml` | reads, writes |
| Tanks / resources | `KSA/Tank.cs`, `KSA/Mole.cs` | `Core/…` | reads |
| **Electrical (power/battery/solar/gen)** | `KSA/Battery.cs`, `KSA/SolarPanel*.cs`, `KSA/Generator*.cs`, `KSA/PowerConsumerState.cs`, **`KSA/Joules.cs`, `KSA/Watts.cs`, `KSA/EnergyReference.cs`, `KSA/PowerReference.cs`** | `Core/CoreElectricalAGameData.xml` | reads ✅ (G2: now W) |
| **Docking** | `KSA/DockingPort.cs`, `KSA/InputEvents.cs` | `Core/CoreCouplingAGameData.xml` | reads ✅, writes ✅ (G1 fixed) |
| Flight computer / attitude / burn | `KSA/FlightComputer.cs`, `KSA/BurnTarget.cs`, `KSA/NavBallData.cs` | — | writes |
| Staging / sequences | `KSA/SequenceList.cs` | `Core/…` | writes |
| Lights | `KSA/LightModule.cs`, `KSA/Light.cs`, `KSA/FloatReference.cs`, `KSA/ColorRgbReference.cs` | `Core/CoreElectricalAGameData.xml` | reads, writes |
| RCS / thrusters | `KSA/ThrusterController.cs` | `Core/…` | reads, writes |
| Decouplers | `KSA/Decoupler.cs` | `Core/CoreCouplingAGameData.xml` | reads, writes |
| Animations / solar deploy | `KSA/KeyframeAnimationModule.cs`, `KSA/SolarTracker.cs` | `Core/…` | reads, writes |
| Camera / menu hooks | `KSA/Program.cs`, `KSA/Camera.cs` | — | writes, runtime |
| **Audio (FMOD playback — `/sim/audio`)** | `KSA/GameAudio.cs` (`System`, `GetChannelGroup`, the in-memory `CreateFmodSound` recipe), `KSA/ChannelGroupType.cs`; `Brutal.FmodApi/{Fmod,Mode,TimeUnit,CreateSoundExInfo,Sound,Channel,ChannelGroup}.cs` — **new `Brutal.Fmod.dll` reference** (`<Private>false</Private>`, condition-guarded like the rest) | — | writes ([`ksa-write-surface.md#audio`](ksa-write-surface.md#audio)); Low churn (FMOD Core P/Invoke mirrors upstream FMOD 5) |
| **Render internals (`thug_life` quad)** | `KSA/SuperMeshRenderSystem.cs`, `KSA/Program.cs` (`GetRenderer`/`OffScreenPass`/`SetViewport`), `KSA/Camera.cs`, `KSA/Part.cs` (ego transforms); **Planet.Render.Core**, **Brutal.Vulkan(.Abstractions/.Vma)**, **Brutal.Core.Memory** | `Core/Shaders/Mesh/UnlitMesh.{vert,frag}` | reads (anchor math), writes (actions), runtime (render postfix) — **deepest / highest-churn coupling**; see [render refs](#render-refs) |
| **Render internals (sticker decals — `/sim/paint/stickers`)** | `KSA.Rendering/RenderTarget.cs` (`ResolveAttachments`, `DepthImage`, `ColorImage`, `Extent`), `KSA/GridPass.cs` (the pass this is a port of), `KSA/Program.cs` (`OffscreenTarget`/`RenderedViewport`/`MainViewport`/`SetViewport`/`ResourceFrameIndex`/`PointClampedSampler`/`ColorFormat`/`GetRenderer`), `KSA/GlobalShaderBindings.cs`, `KSA.Rendering/{BarrierBatch,ImageBarrierInfo}.cs`, `RenderCore.Systems/BindlessTextureLibrary.cs`, `RenderCore/{ShaderModuleUtils,VkUtils}.cs`, `KSA/{Celestial,Vehicle,Part,Camera,Cursor,Ray}.cs` (anchors + picking) | `Core/DefaultAssets.xml` (`GridFrag`, for the include directory only), `Core/Shaders/Common/{Camera,Global,TextureSet}.glsl` | reads ([`ksa-read-surface.md`](ksa-read-surface.md)), writes ([`ksa-write-surface.md#stickers`](ksa-write-surface.md#stickers)), runtime ([`ksa-runtime-coupling.md#stickers-patch`](ksa-runtime-coupling.md#stickers-patch)) — gatOS's **second** render-thread draw injection; see [sticker assets](#sticker-assets) |
| **IVA cabin physics (`/sim/debug/iva`)** | `KSA/Part.cs` (SubPart transforms + `MatrixAsmb2VehicleAsmb`), `KSA/PartModelModule.cs` + `KSA/PartModel.cs` (the `Internal` interior classifier), `KSA/MeshReference.cs` (`PositionCompare` triangle soup), `KSA/Vehicle.cs` (accelerometer/rates/CoM), `KSA/IVASeat.cs`, `KSA/Viewport.cs`; **BepuPhysics + BepuUtilities** (gatOS's own `Simulation`, never `ConstraintSim`). Context for *why* not the game's solver: `KSA/ConstraintSim.cs`, `KSA/NarrowPhaseCallbacks.cs`, `KSA/VehicleUpdateTask.cs` | `Core/CoreIVASpaceAGameData.xml`, `Core/CoreIVAPropAAssets.xml`, `Core/CoreCommandAGameData.xml` (the collider blob that rules out the game's sim), `Core/defaultvehicles/Gemini7/vehicle.xml` (the shipped prop set) | reads ([`ksa-read-surface.md#iva-physics`](ksa-read-surface.md#iva-physics)), writes ([`ksa-write-surface.md#iva-physics`](ksa-write-surface.md#iva-physics)), runtime ([`ksa-runtime-coupling.md#iva-cabin-sim`](ksa-runtime-coupling.md#iva-cabin-sim)) — **no Harmony patch, no game-solver mutation** |
| Numerics | `Brutal.Core.Numerics/` (decomp), `Brutal.Core.Numerics.dll` | — | runtime |

---

## Version-diff method (concrete)

1. **Changelog scan** — the commit log lives at `…/ksa-game-assemblies/current/version.json` (`commits[]`,
   each with `rev`, `date`, `author`, `lines[]`). Read it; flag any line matching a subsystem above.
   ⚠️ **Check `fromRevision` first**: the log can be *incremental* (the 4826 checkout only covers
   4824→4826, leaving 4751–4823 unlogged). If `fromRevision` > the previous baseline's `toRevision`,
   the changelog is gapped — fall back to a full tree diff
   (`git diff --no-index <prev>/current/decomp <new>/current/decomp`) as the discovery mechanism.
2. **Decomp diff** — for each flagged subsystem, compare the file in both trees:
   ```
   …/ksa-game-assemblies/current/decomp/KSA/<File>.cs            (new)
   …/ksa-game-assemblies_<old>/current/decomp/KSA/<File>.cs      (old)
   ```
   Look for renamed members, changed field **types** (e.g. `Joules`→`Watts`), changed method
   **signatures**, and new gating (e.g. `IsControllable`).
3. **Asset diff** — for value/unit questions, compare the Content XML (field names + unit attributes like
   `Ns=`/`J=`/`W=` make unit changes obvious).
4. **Build** — `dotnet build gatOS.GameMod` against the new `dll/` to get the compile-break work list.
   Iterate to green: Roslyn hides body-phase errors behind any outstanding declaration-phase error.
5. **Binary-level member-surface diff** (added 2026-08-23, `#5348-pass`) — the build is an alarm, not
   a survey, and the decomp can lag the shipping DLLs. Extract every external TypeRef from the built
   `gatOS.GameMod.dll`, then dump each referenced type's full member surface (public + non-public,
   declared-only) from **both** `dll/` sets via `MetadataLoadContext` and diff the two dumps. This is
   what proves the reflection accessors and Harmony targets resolve against the **real** assemblies
   rather than the decomp's approximation of them. At 5348: 481 TypeRefs, 470 resolvable,
   **63 changed shape** — with zero compile breaks.
6. **Record** — update the `[KsaAnchor]`s, this `scope/` folder, the matrix, the SPEC, and re-run
   `docs/VALIDATION.md`.

The applied 4680→4750 result: [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md).
The applied 4750→4826 result: clean pass, no code changes — recorded in the checkout table above, the
[read](ksa-read-surface.md#4826-findings) / [write](ksa-write-surface.md) 4826 findings sections, and
the matrix header; live re-check items in [`../docs/VALIDATION.md`](../docs/VALIDATION.md).
The applied 4826→4892 result: clean pass, no code changes — recorded in the pass paragraph above, the
[read](ksa-read-surface.md#4892-findings) / [write](ksa-write-surface.md#4892-findings) 4892 findings
sections, and the matrix header; live re-check items in [`../docs/VALIDATION.md`](../docs/VALIDATION.md).
The applied 4892→4939 result: clean pass, no code changes — recorded in the pass paragraph above, the
[read](ksa-read-surface.md#4939-findings) / [write](ksa-write-surface.md#4939-findings) 4939 findings
sections, and the matrix header; live re-check items in [`../docs/VALIDATION.md`](../docs/VALIDATION.md).
Since the assemblies checkout is a git repo holding every drop, prefer `git diff <oldCommit>..<newCommit>`
inside it over the two-checkout `--no-index` diff.
# Paint assets at 2026.8.19.5261

Required shader reference/path: `MeshIndirectFrag` / `MeshIndirect.frag`. Optional raytraced:
`MeshIndirectRaytracedFrag` / `MeshIndirectRaytraced.frag`. Both must contain the `vec3 sampledColor`
anchor, `inStateFlags`, and `gammaToLinear`. EVA clones reconstruct standard `PbrMaterialReference`
assets and KSA's generated `*_FurMaterial` recipe. These are high-churn assets; re-audit the complete
checklist in `plans/PAINT_ASBUILT.md` on every KSA revision.

# Clutter texture assets at 2026.8.22.5348 {#clutter-assets}

Decoding rides KSA's own texture stack, not a gatOS decoder: `Brutal.TextureApi.TextureLoader`
dispatches `LoadFromMemory(bytes, FormatType, settings)` to one of three loaders by the extension its
`FormatType` maps to — `Brutal.TextureApi.Stb` (png/jpg/bmp/hdr/tga), `Brutal.TextureApi.Ktx`
(ktx/ktx2), `Brutal.TextureApi.Gli` (dds/kmg). That is exactly the container set gatOS sniffs at
commit, and it is why cleanup is a three-way type switch: `ITexture` is neither `IDisposable` nor
finalized, and only the concrete `StbTexture`/`KtxTexture`/`GliTexture` expose `Destroy()`. Settings
come from `RenderCore.TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, KtxTranscodeFmt.Rgba32)`,
which returns the **two-element pair** the loaders pick from — an stb `LoadSettings` whose
`ForceChannels` is derived from the VkFormat (4, so a 3-channel PNG cannot decode to the widely
unsupported `R8G8B8_UNorm`) and a ktx `LoadSettings` carrying `SuperCompressionTranscodeFormat`. That is the exact
pair `TextureReference.DoLoad` falls back to for the game's own assets when no `TextureManifest`
overrides `Format`/`SuperCompressionBlockFormatFamily`.

> **⚠️ Correction (2026-08-23, C4). The content layout this section previously described does not
> exist — in *either* build.** It claimed the override targets were an inline
> `<Material Id="EarthGrassClutterMaterial"><Diffuse Id="EarthGrassClutterDiffuse" …/>` chain under
> `<GroundClutter><Ecotype Name="Grass">` in `Content/Core/Astronomicals.xml`, and that those
> `Id` attributes were the `TextureReference.GetRealId()` strings a `bind` names. **That block is
> present in the XML but is never deserialized**: `ClutterEcotypeReference.MaterialReferences` is
> `[XmlIgnore]` and is repopulated wholesale from `ClutterObjects → Lods → MaterialReferences` by
> `PopulateMaterialReferences()`. No `EarthGrassClutterDiffuse`-style id ever reaches the runtime.
> This was not a 5348 regression; the description was wrong on 5261 too, and acting on it is what made
> the feature inert (see [`#5348-pass`](#5348-pass) C4).

**The real override targets** are the `GroundClutterMaterial` assets in
`Content/Core/GroundClutter/{Grass,GenericRock,EarthTrees}Assets.xml` — autogenerated from the source
`.glb`s and reached at runtime through the `ClutterObject` → `LODs` → `<Material Id="…"/>` chain. The
*material* element carries an `Id` (e.g. `<GroundClutterMaterial Id="Grass">`); its **texture child
elements do not**:

```xml
<GroundClutterMaterial Id="Grass">
  <Diffuse       Path="Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2"   Category="Terrain" />
  <Normal        Path="Textures/Planets/Earth/GroundClutter/Grass_Normal.ktx2"    Category="Terrain" />
  <AoRoughMetal  Path="Textures/Planets/Earth/GroundClutter/Grass_Pbr.ktx2"       Category="Terrain" />
  <Opacity       Path="Textures/Planets/Earth/GroundClutter/Grass_Opacity.ktx2"   Category="Terrain" />
  <Thickness     Path="Textures/Planets/Earth/GroundClutter/Grass_Thickness.ktx2" Category="Terrain" />
  …
</GroundClutterMaterial>
```

`Path=`-only, every one of them, in all three files
(`grep -cE '<(Diffuse|Normal|Opacity|Thickness|AoRoughMetal|Alpha)[^>]*Id=' *Assets.xml` → **0**).

**So the catalog is keyed on `TextureReference.LocalPath`, not `Id`.** `LocalPath` *is* that XML
`Path` attribute — e.g. `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`. It is
install-independent, unique per asset, and space-free, which the space-separated `clutter` listing and
`bind` line require. One `KeyOf(TextureReference)` helper
(`Game/Ksa/Paint/ClutterTextureBridge.cs`) is used by **both** the discovery walk and
`Match`/`ResolveStock`, so the two cannot diverge.

**Why not `Id`.** `TextureReference.GetRealId()` returns `Id` only when `SerializedId.IsReferenceable`,
and `SerializedId.OnDataLoad` sets that flag **only** if the XML supplied an `Id=` attribute —
`IsReferenceable = !string.IsNullOrEmpty(Id)`. For a `Path=`-only element it is false, and
`FileReference.OnDataLoad` then assigns `Id = ModPath`, an **absolute machine path**. Keying on `Id`
would therefore differ per install *and* leak the user's filesystem into `/sim`.

The slot set the walk covers is `{Diffuse, Normal, PBRMap, OpacityMap, ThicknessMap, AlphaMap}`;
`AlphaMap` (`[XmlElement("Alpha")]` on `PbrMaterialReference`) is **new at 5348** and no stock clutter
material authors one yet, so it is normally absent from the listing. One material shared across
ecotypes is exactly what `used_by` warns about. These paths are **content, not API**: a rename or a
removal (rev 5263 commented out `TreeType13/14/15`, taking `Tree12Cards`/`Tree13Cards`/`Tree14Cards`
and their 15 slots out of the walk) breaks saved bindings as a `Failed` row or an ENOENT at bind time,
never a crash — and bindings are session-only, so there is nothing to migrate. Re-audit with the
checklist in
[`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`](../plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md).

> **Trap recorded, not acted on:** `GroundClutterMaterialReference.PopulateShaderMacrosFromFlags`
> **gained a second overload** in 5348. gatOS calls neither, but a future `AccessTools.Method` on it by
> name alone would now throw `AmbiguousMatchException`.

# Sticker assets at 2026.8.19.5261 {#sticker-assets}

Sticker decals ship **no asset of their own**: both shaders are C# string constants in
`StickerDecalRenderer` (`VertexShader`, `FragmentShader`), compiled at pipeline build by
`ShaderModuleUtils.FromString(device, utf8, stage, null, debugName)`. A null `CompileOptions` uses
`ShaderModuleUtils`' own defaults, which already carry the device's Vulkan/SPIR-V target **and the
default include callbacks** (`RenderCore/ShaderModuleUtils.cs:16-22`). What they depend on is
therefore KSA's *shipped GLSL headers and descriptor-set layout*, not KSA's shipped shader programs.

**The `GridFrag` trick.** `shaderc` resolves an `#include` relative to the **directory of the
requesting source's debug name** (`Brutal.ShaderCApi/ShaderC.cs:253`,
`Utf8.Path.Combine(GetDirectoryName(requestingSource), source)`), and gatOS's shaders are strings
with no file on disk. `StickerDecalRenderer.ShaderIncludeDirectory()` therefore resolves
`ModLibrary.Get<ShaderReference>("GridFrag")` (declared at
`Content/Core/DefaultAssets.xml:367` — `<Shader Id="GridFrag" Path="Shaders/Grid.frag" />`) and takes
**only `Path.GetDirectoryName(reference.ModPath)`**, i.e. the install's real `…/Shaders/` directory.
The debug names passed to the compiler are then `…/Shaders/gatos_sticker.vert` and `.frag` — files
that do not exist, but whose *directory* does. The asset's content is never read. The name must also
be **NUL-terminated**, because the include resolver reads it as a C string (the same requirement
`Game/Ksa/Paint/PartPaintPatches.cs:56-59` documents). If the `GridFrag` id is renamed or dropped,
the pipeline build throws at install and the feature reports `renderer=degraded` — it does not crash.

**GLSL headers included from KSA** (a content change here is a *silent* compile failure at the same
point, so re-read them on any shader-asset churn):

| Header (`Content/Core/Shaders/Common/`) | What the sticker shaders take from it | Break impact |
|---|---|---|
| `Camera.glsl` (→ `#include "Global.glsl"`) | `global.camera.{viewProjection,inverseProjection,inverseView}` for the vertex transform and the reverse-Z scene-position reconstruction; `global.lighting.{sunPosition,sunColor,planetColor}` for the shading term | a member rename/reorder inside the `Camera`/`GlobalLighting` UBO structs breaks the compile (caught) or, worse, silently reinterprets a field |
| `Global.glsl:144` | the `layout(set = SET_GLOBAL, binding = 0) uniform Global { Camera camera; GlobalLighting lighting; Celestial celestial; Vessel vessel; } global;` block — **set 0 with a dynamic offset per viewport** | the pipeline layout's set 0 is `GlobalShaderBindings.DescriptorSetLayout` and the draw binds it with `GlobalShaderBindings.DynamicOffset(Program.MainViewport.Index)`; a layout change desynchronises both |
| `TextureSet.glsl` | `SAMPLE_TEXTURE(texID, samplerID, uv)` over `globalTextures[]`/`samplers[]`. gatOS `#define SET_TEXTURE 2` **before** the include (the header defaults it to 1), so the bindless table is set 2 | the macro/array names and the `SET_TEXTURE` override are load-bearing; sampler slot **0** is assumed to be the library's linear-clamped, full-mip sampler (`BindlessTextureLibrary.cs:127-130`) |
| `Extensions.glsl` (via `TextureSet.glsl`) | the non-uniform-indexing extension pragmas the bindless table needs | transitive; breaks the compile if removed |

> **5348 note (rev 5301).** `GlobalLighting` *did* reshape — four portrait-light arrays became
> 16-entry forward-light arrays and `Global.glsl` matched, growing the UBO stride. The sticker shaders
> are unaffected **by construction**, not by luck: they are compiled at runtime against the shipped
> headers, they bind set 0 with `GlobalShaderBindings.DynamicOffset(...)` rather than a computed
> stride, and the members they read (`global.camera.*`,
> `global.lighting.{sunPosition,sunColor,planetColor}`) are the leading members and are untouched. The
> residual risk is a **stale SPIR-V cache**, which is a live check, not a static one.

**Descriptor-set order is baked into the GLSL** — set 0 = KSA's global UBO block (`SET_GLOBAL`
defaults to 0), set 1 = our scene-depth `sampler2D`, set 2 = KSA's bindless table (`SET_TEXTURE` is
`#define`d to 2) — so `BuildPipelineLayout`'s three-element `setLayouts` span must stay in exactly
that order. The bindless table is declared `UpdateAfterBind | PartiallyBound`
(`RenderCore.Systems/BindlessTextureLibrary.cs:95-99`), which is what makes it legal for our shader
to index a slot the game never touches, and for the binder to write a slot while command buffers
referencing other slots are in flight.

**Other build-time assumptions** (all in `BuildPipeline`, any of them moving silently breaks the
draw — re-verify live): `Program.Instance.ColorFormat` is the format the main offscreen target is
constructed with (`KSA/Program.cs:1427`), i.e. **`R16G16B16A16_SFLOAT`**, and the pass declares it
itself rather than calling `SetupGraphicsPipeline` — it draws *after* the resolve, into the
single-sample output image, with **no depth attachment at all**;
`Presets.{InputAssembly.TriangleList,Rasterization.Fill.CullFront}` (front faces culled so the box
still covers its footprint when the camera is **inside** it — the same reason KSA draws the planet
with `CullFront`, `KSA/PlanetRenderer.cs:1528`); `RenderingPresets.{ReverseZDepthStencil.NoDepthTest,
BlendState.BlendColorAlphaOver}`; `Renderer.{Device,DynamicStateInfo,ViewportState}`. The push block
is fixed at **112 bytes** and asserted against `sizeof(StickerPush)` at build time, inside the 128-byte
Vulkan minimum. Modules gatOS compiles are **gatOS's** to destroy (unlike `ModLibrary`'s), which
happens as soon as the pipeline holds the code.

**No new reference DLL and no csproj change at all.** Stickers reuse the
`Brutal.Vulkan(.Abstractions/.Vma)`, `Planet.Render.Core`, `Brutal.Core.Memory` and
`<AllowUnsafeBlocks>` set the `thug_life` quad already pulled in ([render refs](#render-refs)), the
`Brutal.ShaderC` reference vehicle paint already needed (namespace `Brutal.ShaderCApi`, for
`ShaderException`), and the `Brutal.Texture`/`Brutal.Ktx` decode set the clutter overrides added.
Re-audit with the checklist in [`plans/STICKERS_PLAN.md`](../plans/STICKERS_PLAN.md) and the shared
paint audit in [`plans/PAINT_ASBUILT.md`](../plans/PAINT_ASBUILT.md).
