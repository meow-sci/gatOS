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
| `…/ksa-game-assemblies` | **2026.7.8.4980** | 2026-07-22 | 4939 → 4980 (**gapless** — `fromRevision` = the prior baseline) | **current / verified baseline** — full playbook pass 2026-07-22: one compile break (docking `OldMeanRadius`) fixed, everything else clean; `KSAFolder` default resolves here. The checkout is a **git repo whose history holds every prior drop** (`7cf5c0a` = 4892, `1265373` = 4826, `6fa343d` = 4750, …) — diff drops with `git diff <old>..<new>` inside it |
| `…/ksa-game-assemblies_prev` | 2026.7.6.4939 | 2026-07-16 | 4892 → 4939 | prior verified baseline kept side-by-side; the 4980 pass diffed the two checkouts' `current/decomp` + `current/Content` trees directly |

gatOS was originally built against the 4680-era sources (most `[KsaAnchor]` `Verified` dates span
2026-06-12…2026-06-23). The **4680 → 4750** diff was run through the playbook on 2026-06-27; the touched
anchors carry `GameVersion="2026.6.9.4750"` (see
[`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md)).

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

> The ksa skill (`.claude/skills/ksa/`) also points at decompiled sources under `decomp/ksa/` (and a
> working copy lives at `…/unscience/decomp/ksa`). Any of these decomp trees is readable; for
> break-checking use the one **versioned with the DLLs you're building against** (the game-assemblies
> checkout) so source and binary match. (`[KsaAnchor].SourceFile`'s docstring says "under
> `thirdparty/ksa`" — that pointer is stale; the values are relative paths like `KSA/Vehicle.cs` that
> resolve under any decomp checkout's `current/decomp/`.)

### How gatOS consumes each artifact

| Artifact | Consumed by | Mechanism |
|---|---|---|
| `current/dll/KSA.dll`, `Brutal*.dll`, `Planet*.dll` | `gatOS.GameMod` compile | `KSAFolder` in `Directory.Build.props`; `<Reference Private=false Condition=Exists(...)>` (guarded, so game-free projects build without them) |
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
| Part template ids (dynamic add) | `Core/Core*GameData.xml` `PartGameData Id="…"` | e.g. `CoreCouplingA_Prefab_DockingPort1WA` | the string ids `ModLibrary.Get<PartTemplate>(id)` resolves (not used by `/sim` reads; reference). |
| **`thug_life` quad shaders** | `Core/Shaders/Mesh/UnlitMesh.{vert,frag}` | the `"UnlitMeshVert"`/`"UnlitMeshFrag"` `ShaderReference` keys `ThugLifeQuadRenderer.BuildPipeline` resolves via `ModLibrary.Get<ShaderReference>(...)` | the world-space quad reuses KSA's stock unlit-mesh shaders; if these keys/assets are renamed/removed the pipeline build fails (caught, feature self-disables). |

---

## Render-internals references — `thug_life` (the deepest, highest-churn coupling) {#render-refs}

The `thug_life` cheat (`Game/Ksa/ThugLife/`, ported from `unscience`) is gatOS's **first custom GPU
rendering** and its **deepest coupling into KSA's render-pipeline internals** — render internals churn far
faster than the gameplay APIs the rest of the surface binds, so this set is **High churn** and the one most
worth re-verifying on any game update. It pulled in **new reference DLLs** and a project-level flag:

| Added to `gatOS.GameMod.csproj` | Why | Notes |
|---|---|---|
| `Brutal.Vulkan`, `Brutal.Vulkan.Abstractions`, `Brutal.Vulkan.Vma` | the Vulkan pipeline/descriptor/buffer/staging surface (`SimpleVkTexture`, `VkUtils.{UploadBufferToImage,StageAndUploadToBuffer}`, `DeviceEx.CreateSampler`, allocator/VMA staging pools) | `<Private>false</Private>`, condition-guarded on `$(KSAFolder)` like the other KSA refs |
| `Planet.Render.Core` | `Renderer` (Device/Allocator/DynamicStateInfo/ViewportState/Graphics), `RenderTechnique.CreateShaderStages`, `Presets`/`RenderingPresets`, `Program.{GetRenderer,OffScreenPass,SetViewport}` | `<Private>false</Private>`, guarded |
| `Brutal.Core.Memory` | unmanaged buffer/staging helpers for the GPU upload path | `<Private>false</Private>`, guarded |
| `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` | `ThugLifeQuadRenderer` is `unsafe` (raw pointer work for the Vulkan buffer uploads / descriptor writes) | first use of `unsafe` in gatOS |

**Pipeline assumptions baked into `ThugLifeQuadRenderer.BuildPipeline`** (any of these moving silently
breaks the draw — re-verify live):
- Reuses KSA's stock unlit-mesh shaders via the `"UnlitMeshVert"`/`"UnlitMeshFrag"` `ShaderReference` keys
  (assets `Content/Core/Shaders/Mesh/UnlitMesh.{vert,frag}` — see the asset table above).
- Texture format `R8G8B8A8UNorm` (the sunglasses texture, built from a static 26×5 char grid in
  `ThugLifeTexturePattern` → `ThugLifeTextureFactory`).
- **Reverse-Z** depth convention and the **`Program.OffScreenPass.{Pass,SampleCount}`** render-pass /
  MSAA sample count (the quad must be depth-tested and MSAA-resolved consistently with the scene).
- Draw injected via a Harmony postfix on `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`
  (`KSA/SuperMeshRenderSystem.cs:329`) — the runtime coupling, see
  [`ksa-runtime-coupling.md#thug-life-patch`](ksa-runtime-coupling.md#thug-life-patch).

Full anchor list: [`ksa-read-surface.md#thug-life`](ksa-read-surface.md#thug-life) (anchor math),
[`ksa-write-surface.md#thug-life`](ksa-write-surface.md#thug-life) (the seven actions),
[`../docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md) (render set). **Re-verified
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
5. **Record** — update the `[KsaAnchor]`s, this `scope/` folder, the matrix, the SPEC, and re-run
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
