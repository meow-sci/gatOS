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
| `…/ksa-game-assemblies` | **2026.8.19.5261** | 2026-08-11 | 5168 → 5261 | **current / verified baseline** — full playbook pass 2026-08-11 (see below): **ten compile breaks** (all from rev 5211's `SimTime`→`UniverseTime` migration + revs 5208–5216's solver rename) fixed, **one silent semantic break** found and closed (the `EndOfTime` sentinel becoming a finite ~1.7e29 s); `KSAFolder` default resolves here. The checkout is a **git repo whose history holds every prior drop** (`13595c1` = 5056, `3106557` = 5018, `cdb7391` = 4980, `7cf5c0a` = 4892, …) — diff drops with `git diff <old>..<new>` inside it |
| `…/ksa-game-assemblies_prev` | 2026.8.5.5168 | 2026-08-05 | 5117 → 5168 | prior side-by-side checkout — a **genuinely audited baseline** (the 5168 pass closed its own findings), so the 5261 pass diffed the two trees directly (no git-history fallback needed) |

gatOS was originally built against the 4680-era sources (most `[KsaAnchor]` `Verified` dates span
2026-06-12…2026-06-23). The **4680 → 4750** diff was run through the playbook on 2026-06-27; the touched
anchors carry `GameVersion="2026.6.9.4750"` (see
[`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md)).

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
**Re-verified (static) 2026-08-01 against `2026.8.3.5117`**: `SuperMeshRenderSystem.cs` is
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
# Paint assets at 2026.8.19.5261

Required shader reference/path: `MeshIndirectFrag` / `MeshIndirect.frag`. Optional raytraced:
`MeshIndirectRaytracedFrag` / `MeshIndirectRaytraced.frag`. Both must contain the `vec3 sampledColor`
anchor, `inStateFlags`, and `gammaToLinear`. EVA clones reconstruct standard `PbrMaterialReference`
assets and KSA's generated `*_FurMaterial` recipe. These are high-churn assets; re-audit the complete
checklist in `plans/PAINT_ASBUILT.md` on every KSA revision.

# Clutter texture assets at 2026.8.19.5261

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
overrides `Format`/`SuperCompressionBlockFormatFamily`. The override targets are
the stock clutter chain in `Content/Core/Astronomicals.xml`: `<GroundClutter><Ecotype Name="Grass">
<Material Id="EarthGrassClutterMaterial"><Diffuse Id="EarthGrassClutterDiffuse"
Path="Textures/Planets/Earth/GroundClutter/Grass_Diffuse.dds" Category="Terrain"/>` and its
`<Normal>`/`<AoRoughMetal>`/`<Opacity>`/`<Thickness>` siblings — the `Id` attribute is the
`TextureReference.GetRealId()` string a bind names, the element is the slot the `clutter` listing
reports, and one `Material` shared across ecotypes is exactly what `used_by` warns about. Shared
`GroundClutterMaterial` assets also live in `Content/Core/GroundClutter/*Assets.xml` (Grass,
GenericRock, EarthTrees), authored as `.ktx2` there and `.dds` in `Astronomicals.xml`. These ids are
content, not API: a rename breaks saved bindings (ENOENT at bind time, never a crash). Re-audit with
the checklist in
[`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`](../plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md).
