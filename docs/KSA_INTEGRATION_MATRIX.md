# KSA Integration Matrix

> The co-located, at-a-glance record of **every `/sim` path that touches KSA game state** — the
> document you grep first when a new decompiled-source drop lands (KSA_GAME_INTEGRATION_PLAN §3.3).
> Each row mirrors a `[KsaAnchor]` annotation in `gatOS.GameMod/Game/Ksa/**`; the attribute is the
> source of truth for the exact member, this table is the human view. When a decomp drop breaks the
> build, the failing `[KsaAnchor]` sites are the work list — fix them, then update the matching rows
> here (anchor, `Verified`, `GameVersion`).

**Status:** covers the **G1** (command pipeline + first controls), **G2** (integration-layer
formalization), **G3** (read-surface expansion: bodies/system, vessel extensions, per-module reads,
new events) and **G4** (full control surface: throttle/staging/attitude/burn, RCS, per-light, decouplers,
`/sim/debug`, solver-phase queue) surfaces. The HTTP (G5), serial-bus (G7 — codecs + the live
`gatos.serial` virtio-serial bridge), MQTT, and MCP transports and the TypeScript SDK (G6) are all
built; they add **no** KSA coupling — every transport speaks the same `SnapshotStore` (reads) and
`ICommandSink`/`SimCommand` (writes), so this matrix (the KSA-touching surface) is unaffected by them.

**Verified:** **2026-09-02 against `2026.9.7.5402`** (clean `-t:Rebuild` of the whole solution against
the 5402 DLLs — 0 warnings; full test suite 1646 passed / 12 skipped / 0 failed; **the changelog was
gapped** — CURRENT's `version.json` logs only rev 5401 — so the full decomp + Content diff against a
`git worktree` of the 5348 drop was the discovery mechanism; **plus the binary-level surface diff**:
482/482 game TypeRefs and 907/907 MemberRefs of the compiled `gatOS.GameMod.dll` resolve in the
shipping 5402 assemblies, 52 of 474 shared referenced types changed shape (all `KSA.dll`), all 25
reflection strings and 15 Harmony targets present with single overloads.) **Three compile breaks,
all fixed:** KSA deleted the `Viewport` class (→ `IViewport`/`IGameViewport`/`GameViewport` +
`ViewportRegistry`; `Program.MainViewport` is an `IGameViewport`, `Index` → `ShaderSlot`, and the
`Mode`/`FixedController` fields the camera director wrote became **protected-set properties** — hence
**two new High-risk reflection anchors** in `Game/Ksa/Camera/ViewportSeam.cs` and the camera hook now
on `GameViewport.OnFrame(double)`); `VolumetricTrailRenderer.DebugTrailColor` was removed (→
`debug/plumetrail/render/trail_color` **retired**, see the trail row); `Cursor.InputRay` was removed
(→ `Cursor.GetEgoRay(IViewport)` in `StickerPicker`). No compiler-invisible break was found. **Drift
documented, no code change:** a structural-failure/debris system (fragment vehicles appear under
`vessels/`, control can move to one), parachutes (`ctl/stage` arms/cuts them), authored
`parts/<n>/display_name`, terrain-aware `Camera.ClampCamera`. Detail:
[`scope/ksa-assets-and-versions.md#5402-pass`](../scope/ksa-assets-and-versions.md#5402-pass).

**Prior pass — 2026-08-23 against `2026.8.22.5348`** (clean `-t:Rebuild` of the whole solution against
the 5348 DLLs — 0 warnings, **zero compile breaks**, the first pass in the project's history with none
(5261 had ten, 5168 four); full test suite 1646 passed / 12 skipped / 0 failed; direct diff of the
side-by-side 5261 and 5348 checkouts — gapless, CURRENT's `fromRevision` 5261 = the prior audited
baseline, revs 5262–5348, 85 commits; **plus a binary-level surface diff**: all 481 external TypeRefs
of the compiled `gatOS.GameMod.dll` were dumped from *both* DLL sets via `MetadataLoadContext` and
diffed — 63 of 470 referenced types changed shape, and every one of gatOS's ~15 reflection accessors
and all Harmony targets were confirmed present with compatible shapes in the real 5348 assemblies,
i.e. checked against the shipping binaries rather than the decomp.) **One brand-new KSA coupling:**
rev 5283's `UiCoverageMaskSystem` stamps the reverse-Z near plane into the opaque pre-pass wherever
fully-opaque ImGui UI covers the screen, so `/sim/display` streamed the local player's window chrome
as unshaded black → a new Harmony **prefix on `GameSettings.UiPixelCulling()`** returning `false`
while the stream is live (new row under [Screen stream](#screen-stream-stream_planmd)). **Five other
code changes, none of them a compile break:** `debug.teleport`/`debug.impulse` moved Frame → **Solver**
phase (revs 5331/5339 moved physics-bubble trim/intake/merge/split ownership into `VehicleUpdateTask`,
i.e. onto the solver thread, and deleted `Universe.{MergeVehicleTasks,TrimPhysicsBubbles,AddVehiclesToTasks}`
and `Universe._physicsBubbles`); `TerrainActuator`'s frame-in-flight mirror is now **field-wise** (revs
5319–5325 gave `MeshUbo` four per-frame split-double terrain anchors that a whole-struct copy was
stamping over); the clutter texture catalog is re-keyed on **`TextureReference.LocalPath`** instead of
`GetRealId()` — a **pre-existing** bug, identical on 5261: no clutter texture element carries an `Id=`
attribute, so `SerializedId.IsReferenceable` was never set, the catalog published empty and every
`bind` answered `ENOENT`; the clutter walk gained the new `PbrMaterialReference.AlphaMap` slot; and
`FaceFxManager.KittenFaceAsmb` tracks rev 5270's EVA face-cam offset −0.85 → −0.70. **Nine silent
semantic drifts, documented not fixed:** `ctl/stage` no longer activates RCS (rev 5329 —
`ActivateSubtreeInStage` walks `ISequenced` modules only and `ThrusterController` is not `ISequenced`);
`engines/<n>/min_throttle`'s *effective* floor inverted (rev 5317 — the FC's fold over active engines
went `Min` → `Max`, empty-set default 1.0 → 0.0); `navball/{deltav,twr}` corrected by the same
per-module sequence grouping (rev 5318); `ctl/burn` timing/throttle profile moved (rev 5317 —
`BurnTarget` gained `Throttle` + `LastIgnitionDenied`, TVC gains retuned); EVA `mmu`/`mmu.N` paint-slot
ordinals swapped (rev 5268 — the MMU mesh became a skinned `.glb` and its two material blocks were
reordered); `Part.ScaleTotal` composition additive → multiplicative, and the game's own editor scale
became *physical* + clamped 0.5×–2× via the new `IRescale` (rev 5329) while gatOS's `/sim` scale stays
visual-only and unclamped; CPU terrain height sampling changed at cube-face seams (sub-metre); plus
inherited value drift (tessellation range default 220 → 50 m, grass `GenerationRange` 170 → 80 m,
`TreeType13/14/15` commented out of `Astronomicals.xml`, destroyed vehicles now kill their crew,
`PowerManager` rebuilt on the new `ElectricalCircuits` partition, `Decoupler` now a multi-instance
component module, and `ThrusterController` no longer self-activating at construction). **Verified
clean — state plainly, there is no risk here:** `Brutal.Numerics` (`doubleQuat`/`double3`),
`QuaternionEx`, `Double3Ex`, every `Celestial` Cci/Cce/Ccf accessor and `Orbit.CreateFromStateCci` are
**byte-identical** (rev 5280's `CelestialFrameMath` is a pure refactor — inlining reproduces the old
expressions textually), so `KSA_CELESTIAL_COORDINATE_FRAMES.md` needs **no** correction;
`KSA.Rendering/RenderTarget.cs`, `RenderingPresets`, `Presets`, `UnlitMesh.{vert,frag}`, `Shared.glsl`
and `Grid.{vert,frag}` are **untouched**, so both render seams hold; `Universe.ExecuteNextVehicleSolvers`
is unchanged (the new multithreading is *inside* the job it queues); the `Program.RenderGame` transpiler
still lands (the new tail's `Profiler.Gpu.EndFrame` is rejected by both filters, and `TagRegion`'s
`using` emits `GpuRegion.Dispose()`, never an inlined `End`); Vulkan 1.3 → 1.4 (rev 5315) leaves the
SPIR-V target unchanged (environment note only: the mod now inherits a Vulkan 1.4 device requirement);
the rev-5301 lighting-UBO reshape cannot reach gatOS's runtime-compiled GLSL; and the rev-5288 clutter
GPU repack does not reach `ClutterTextureBridge` or `StickerDecalRenderer`. Full detail:
[pass record](../scope/ksa-assets-and-versions.md#5348-pass). **Fifteen live re-check items** (render
correctness and in-flight behaviour genuinely need a live pass — this was a static + binary-metadata
review): `docs/VALIDATION.md`.

Prior: **2026-08-05 against `2026.8.5.5168`** (full solution build green, 0 warnings; full test
suite 772 passed / 11 skipped / 0 failed; full decomp + Content diff between the side-by-side 5117 and
5168 checkouts — chainless-gap: CURRENT's `fromRevision` 5117 = the prior audited baseline, revs
5118–5168). **Four compile breaks, all fixed — every one from rev 5154**, which moved offscreen
rendering off `VkRenderPass`/framebuffers onto Vulkan **dynamic rendering** and deleted
`Program.OffScreenPass`, `KSA.OffscreenTarget`, `KSA.RenderTarget`, `KSA.Framebuffer`,
`Core.RenderPassState` and `Core.DynamicRenderState`: `FxReflect.CloudApply`'s worley-noise handle
retyped `RenderTarget` → `RenderImage` (as did `CloudShadowsRenderer.PopulatePlanets`'s parameter),
`FrameCapture` null-guards the now-nullable `RenderTarget.ColorImage`, and
`ThugLifeQuadRenderer.BuildPipeline` migrated to `Program.OffscreenTarget.SetupGraphicsPipeline(ref
info)` (KSA's own pattern — the quad now tracks the engine sample count, incl. the new CMAA2 option of
rev 5156, instead of hard-binding one). ⚠️ The **new** `KSA.Rendering.RenderTarget` is an unrelated
class reusing the deleted name — do not re-bind by name. **Three silent semantic breaks, all closed:**
rev 5143 made `FlightComputer.RCSMode` gate **manual** RCS (so `ctl/translate`/`ctl/rotate` do nothing
while RCS is off) → new **`ctl/rcs_mode`** read + Solver-phase control; rev 5132 let players disable a
decoupler and gated `SetIsActive` on it (making `decoupler.fire` a silent no-op reported as success) →
`EOPNOTSUPP` + new **`decouplers/<n>/enabled`** read; rev 5128's new
`Vehicle.ClearHeldPlayerInput()` clears the latched thruster flags on vessel switch / window focus loss
/ camera switch / — every update — ImGui keyboard capture or warp > 30× (documented; throttle+ignite
unaffected). All 13 reflection accessors and all 7 Harmony targets verified; the display transpiler's
`End()` anchor and image-layout assumption re-derived against the rewritten `Program.RenderGame` and
still hold. Inherited value drift (no API change): encounters widened for near-coplanar transfers (rev
5141), RCS thrust reduced (rev 5119), size-D/E SRB grains resized (rev 5124), mass/inertia computation
fixed (rev 5166). Full detail: [read 5168](../scope/ksa-read-surface.md#5168-findings) /
[write 5168](../scope/ksa-write-surface.md#5168-findings) /
[pass record](../scope/ksa-assets-and-versions.md#5168-pass). **The sibling purrTTY mod took the same
rev-5154 break and was migrated alongside** — an unpatched purrTTY hard-crashes KSA at the first frame.

Prior: **2026-07-24 against `2026.7.9.5018`** (full solution build green, 0 warnings; full test
suite 681 passed / 11 skipped / 0 failed; full decomp + Content diff between the side-by-side 4980 and
5018 checkouts — the 5018 changelog is gapless, `fromRevision` 4980 = the prior baseline). **One compile
break, fixed**: rev 4992 (solid rocket motors) generalized propellant storage from liquid-only to a new
`ISubstanceStore` (`Liquid | Solid`) abstraction and renamed `Mole.GetLiquidMass` → `Mole.GetStoredMass`
(also `GetLiquidVolume` → `GetStoredVolume`, `Consume`/`ProduceLiquid` → `…Stored`, `ContainsLiquid`
deleted) — `VesselReader.SampleTanks` (`tanks/<r>/capacity`) was the one call site, and the **value is
unchanged** because `Tank` moles are liquids. No other bound member, reflection accessor, or Harmony
hook target changed (`Program.RenderGame` and `SuperMeshRenderSystem.cs` are byte-identical; the two
hook targets only moved lines and resolve by name). **One coverage gap opened and closed in the same
work item**: SRB solid propellant lives on the new **`SolidGrainSegment`** module — an
`ISubstanceStore`, **not** a `Tank` — so it is absent from `tanks/`, while `Vehicle.PropellantMass`
(now computed from `Parts.SubstanceStores`) *does* include it. gatOS gained a dedicated **`srb/<n>/`**
read surface (SPEC §3.4.8, `VesselReader.SampleSrbs`) covering each motor's grain mass / usable mass /
fraction / burn time / mass flow, chamber + exit conditions, burning area, stack validity and a
per-segment `segments/<m>/` breakdown — read-only, because KSA forces a solid's throttle to 0 or 1, so
ignition stays on the engine surface (`srb/<n>/engine` cross-links to `engines/<n>`). Two inherited semantic
drifts (no API change): encounter candidacy widened (rev 4991 — small moons like Phobos/Deimos now
produce `encounters/<n>/` rows) and `Module.List` concrete-type segmentation (rev 4990 —
API-compatible for every call gatOS makes). Behavior notes (`/sim/debug/refuel` now refills SRB grains;
SRBs are ordinary `EngineController`s so `engines/<n>` covers them but throttle is physically inert on a
solid; `ModuleBase.OnPartCreated` → `OnFullPartCreated` leaves the `AnimationLinks` solar link intact;
`Part.Connector` capability flags are additive) are catalogued in the
[read-surface 5018 findings](../scope/ksa-read-surface.md#5018-findings) /
[write-surface 5018 findings](../scope/ksa-write-surface.md#5018-findings). Rows showing an earlier
per-member `Verified` date bind to members **unchanged** in 5018 (their compatibility is confirmed by
the green build + the diff). Prior passes: 2026-07-22 against `2026.7.8.4980` (one break — docking
`OldMeanRadius`; FC `RCSMode`/`RollMode` drifts), 2026-07-16 against `2026.7.6.4939` (clean; fuel-line/
tank-transfer notes, UI-only control-module lockout), 2026-07-14 against `2026.7.5.4892` (clean; rev
4884 combustion→Reactions notes), 2026-07-03 against `2026.7.3.4826` (clean; post-decouple
control-state inheritance notes), 2026-06-27 against `2026.6.9.4750` (the G1–G4 fix-pass).
Live in-flight checklist: `docs/VALIDATION.md`.

## Transport parity (binding)

Every row below is reachable through the shared model — there is one read surface and one write
surface, projected per transport, never re-implemented. MCP intentionally presents logical JSON
resources/tools rather than a leaf-for-leaf filesystem mirror; its v1 contract and explicit
`/sim/display` exclusion are in [`SPEC_MCP.md`](../SPEC_MCP.md).

| Surface | 9p `/sim` | HTTP `/v1` | MQTT `gatos/` | MCP |
|---|---|---|---|---|
| Data (granular + atomic) | scalar files + `telemetry` doc | `snapshot`/`system`/`bodies[/{id}]`/`vessels/{id}[/telemetry]` | `snapshot`/`system`/`bodies`/`time`/`status` + `vessels/<id>/{telemetry,snapshot}` | `gatos://` world/celestial/vessel/kitten/runtime resources + logical JSON read tools |
| Field-level (per leaf) | the file tree itself | `GET /v1/fs/<path>` (+ `?stream=1` SSE) | retained `gatos/sim/<path>` (one topic/leaf) | deliberately not mirrored; grouped domain documents instead |
| Streaming | `stream` / `events` / `time/alarm` | `vessels/{id}/stream` / `events` (SSE) / `time/wait` | retained `vessels/<id>/*` / `events` topics | `gatos.wait` for later snapshots, matching events, or simulation time |
| Control + debug | `ctl/…`, per-module files, `debug/…` | `POST /v1/command`, `POST /v1/fs/<path>` | publish `gatos/command`, `gatos/sim/<path>/set` | logical control tools + canonical `gatos.command`, `gatos.execute_batch`, and `gatos.schedule_batch` |

Aggregate reads project the one `SimSnapshot` through `gatOS.SimFs/SimJson` (HTTP, MQTT, and MCP)
or `Formats` (9p); the field-level mirror **walks the one `/sim` VFS tree** (`VfsScan`) the 9p server
serves; writes funnel the one `SimCommand` through the single `ICommandSink`. The MCP capability
registry maps those same snapshots/actions to its logical contract and is coverage-tested against
them. Add a read to `SimJson` / a `/sim` node / an action to the command table once — every transport
gets it in the projection appropriate to that transport. See AGENTS.md "THE transport-parity rule".

## Archetypes (KSA_GAME_INTEGRATION_PLAN Part 2)

| Code | Archetype | Read | Write |
|---|---|---|---|
| S  | SENSOR  | current value, one line + LF | — |
| St | STATE   | current setpoint | `0`/`1` flag or `0..1` fraction (idempotent) |
| T  | TRIGGER | status (`0`) | exact token `1` (one-shot) |
| Sm | STREAM  | growing-log / blocking-event NDJSON | — |

## errno vocabulary (frozen)

`EINVAL` unparseable/out-of-range · `ENOENT` vessel/module vanished · `EACCES` control disabled ·
`EBUSY` action can't fire now · `EIO` KSA threw (latches the accessor) · `ETIMEDOUT` game thread
didn't drain in time · `EOPNOTSUPP` accessor latched degraded.

## Threading phase

`Frame` = drained on the per-frame game-thread hook (`OnBeforeUi`). `Solver` = drained in a Harmony
prefix on the vehicle-solver phase (reserved for G4 refills/robotics). Reads are sampled on the
game thread and published via one volatile snapshot swap (threading rules 1–2).

---

## Read surface (sensors)

Anchors live in `gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs`. Formats are frozen in
`gatOS.SimFs/Formats.cs` (G9 doubles, `0`/`1` flags, space-separated vectors/quats, NDJSON streams).

| Path | A | KSA anchor | Risk |
|---|---|---|---|
| `time/{ut,warp}` | S | `Universe.GetElapsedSeconds()`, `Universe.SimulationSpeed` | Low |
| `vessels/by-id/<id>/{id,name,situation,parent}` | S | `Vehicle.Id/.Situation`, `Vehicle.Parent.Id` | Low |
| `…/position/{cci,lat,lon}` | S | `Vehicle.GetPositionCci()`; `IParentBody.GetLlaFromCcf` | Low |
| `…/velocity/{orbital,surface,inertial}` | S | `Vehicle.OrbitalSpeed/.GetSurfaceSpeed()/.GetInertialSpeed()` | Low |
| `…/attitude/{quat,rates}` | S | `Vehicle.GetBody2Cci()`, `Vehicle.BodyRates` | Low |
| `…/altitude/{barometric,radar}` | S | `Vehicle.GetBarometricAltitude()/.GetRadarAltitude()` | Low |
| `…/mass/{total,dry,propellant}` | S | `Vehicle.TotalMass/.InertMass/.PropellantMass` — **5018: `PropellantMass` includes SRB solid-grain mass, so it exceeds Σ `tanks/` by Σ `srb/<n>/mass`** | Low |
| `…/orbit/{apoapsis,periapsis,ecc,inc,sma,period}` | S | `Vehicle.Orbit` elements (radii→altitudes; inc rad→deg) | Low |
| `…/battery/charge` | S | `Vehicle.Parts.Batteries.GetState(b).Charge`, `b.MaximumCapacity` | Low |
| `…/engines/<n>/{active,vac_thrust,isp}` | S | `vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData` — **5348: `ThrusterController` no longer self-activates at part construction** (its ctor dropped the `part.ActivateInStage(null)` broadcast), so `active` on a part that co-hosts RCS reads an honest default at spawn instead of a spurious `1`; RCS state itself is unchanged (`ThrusterController.IsActive` still defaults `true`) | Medium |
| `…/tanks/<resource>/{amount,capacity}` | S | `vehicle.Parts.Modules.Get<Tank>().Moles`; `Parts.Moles.GetState(mole).Mass`; `Mole.GetStoredMass` — **liquids only; SRB grains live under `srb/` (5018)** | Low |
| `…/srb/<n>/*`, `…/srb/<n>/segments/<m>/*` | S | `Parts.RocketCores.{Modules,GetState}` filtered to `SolidMotor`; `SolidMotor.{Stack,Propellant,DefaultGeometry,UnburnableGrainMass,AreaRatio,ComputeBurningArea}`; `SolidGrainSegment.{Grain,InitialGrainMass,UnburnableGrainMass,CasingInnerRadius,Length,GrainVolume,ComputeGrainDepth}`; `RocketCoreState.*` — **read-only** (a lit solid has no throttle) | Medium |
| `…/animations/<n>/{current,state}` | S | `KeyframeAnimationModule` State via `ModuleStateful.TryGetFrom(Parts.States,…)` | Medium |
| `…/stream` | Sm | whole `VesselSnapshot` (growing-log) | — |
| `events` | Sm | snapshot-diff (`EventDiffer`); KSA has no native event bus | — |
| `status/game_version` | S | `VersionInfo.Current.VersionString` | Low |
| `status/sampler` | S | — (sampler cadence) | — |
| `status/accessors` | S | — (degraded-accessor latches, NDJSON) | — |
| `status/transports` | S | — (bound 9p port + control on/off) | — |

## Control surface (G1)

Anchors live in `gatOS.GameMod/Game/Ksa/Actuators/**`; routed by `KsaCatalog`. Every write flows
through `CommandQueue` (transport thread enqueues, game thread drains) — synchronous with errno
feedback. Authority gate (G-D1): `control_all_vessels=false` restricts to the active vessel.

| Path | A | Write | KSA anchor (actuator) | Risk | Phase |
|---|---|---|---|---|---|
| `…/ctl/ignite` | T | `1` | `Vehicle.SetEnum(VehicleEngine.MainIgnite)` | Medium | Frame |
| `…/ctl/shutdown` | T | `1` | `Vehicle.SetEnum(VehicleEngine.MainShutdown)` | Medium | Frame |
| `…/ctl/engine` | St | `0`/`1` | `EngineActuator.SetEngineOn` (ignite/shutdown); reads `EngineOn` (see read surface) | Medium | Frame |
| `…/ctl/lights` | St | `0`/`1` | `Vehicle.LightsOn` + per-`PowerConsumer.LightIsActive` | Low | Frame |
| `…/engines/<n>/active` | St | `0`/`1` | `EngineController.SetIsActive(vehicle, bool)` | Low | Frame |
| `…/animations/<n>/goal` | St | `0..1` | `KeyframeAnimationModule.TimeGoal = f × Shared.Duration` | Low | Frame |
| `…/solar/<n>/goal` | St | `0..1` | same as `animations/<n>/goal` (solar-filtered view, same ordinal) | Low | Frame |
| `…/lights/<n>/goal` | St | `0..1` | same as `animations/<n>/goal` (light-filtered view, same ordinal; only when the light part has an animation) | Low | Frame |

`vessels/active/…` is an alias for the controlled vessel and accepts the same writes.

## Read surface — G3 expansion

Anchors in `Game/Ksa/Readers/{VesselReader,BodyReader}.cs`. The reader builds the M9 core first,
then a guarded enrichment pass adds the rows below; if an extension API drifts, the vessel keeps its
core telemetry and the extension dirs vanish (logged once) rather than the sample failing.

| Path | A | KSA anchor | Risk |
|---|---|---|---|
| `time/{sim_dt,warp_speeds,auto_warp}` | S | `Universe.GetLastSimStep().DeltaTime`, `GetSimulationSpeeds()`, `IsAutoWarpActive`/`AutoWarpTime` | M |
| `time/alarm` | St | none — write a target `ut`, read parks on `SnapshotStore` until reached (blocking-event model) | — |
| `system/{name,home,sun}` | S | `Universe.WorldSun` (names the system), `CelestialSystem.HomeBody` | L |
| `bodies/<id>/{id,class,parent,children,mass,radius,mu,soi,rotation_rate}` | S | `Celestial`/`StellarBody`; `IParentBody.{Mass,Mu}`, `GetAngularVelocity` | L |
| `bodies/<id>/position/ecl`, `velocity/ecl` | S | `Astronomical.GetPositionEcl()/GetVelocityEcl()` | L |
| `bodies/<id>/orbit/{apoapsis,periapsis,ecc,inc,lan,argpe,sma,period}` | S | `Celestial.Orbit` (radii→altitude about parent; angles rad→deg) | L |
| `bodies/<id>/atmosphere/{present,height,scale_height,sea_level_pressure,sea_level_density}` | S | `IParentBody.GetAtmosphereReference().Physical.*` (implicit `double`) | M |
| `bodies/<id>/ocean/{present,density}` | S | `IParentBody.GetOceanReference().Density` | M |
| `…/telemetry` | S | whole `VesselSnapshot` as one JSON doc (atomic read) | — |
| `…/controlled`, `…/com` | S | `Program.ControlledVehicle`; `Vehicle.CenterOfMassAsmb` | L |
| `…/controllable` | S | `Vehicle.IsControllable` (`_overrideIsControllable \|\| Parts.Controls.NumModules > 0`; 4750/rev 4699) | M |
| `…/position/ecl`, `…/velocity/cci` | S | `Vehicle.GetPositionEcl()`, `Vehicle.GetVelocityCci()` (vectors) | L |
| `…/navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | S | `Vehicle.NavBallData` (`AttitudeAngles` int3 deg; `DeltaV` — renamed from `DeltaVInVacuum` at rev 5114, and **both `deltav` and `twr` changed meaning**: active-sequence Δv, atmosphere-corrected TWR). **5348/rev 5318: the values are corrected again** — `Vehicle.UpdateNavballData` and `NavBallData` are unchanged, but the sequence→parts grouping beneath them now iterates `part.GetSubtreeSequencedModules()` and matches each *module's* own `Sequence` (decoupler jettison-mass attribution likewise) instead of `part.Sequenceable`/`part.Sequence`. This is the "a part assigned to sequence 0 silently zeroed the vehicle's Δv and TWR" fix: the values were **wrong before**, so affected vehicles read differently and correctly (D3) | M |
| `…/environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | S | `Vehicle.PhysicsEnvironment`; `PhysicalAtmosphereReference.GetDynamicPressure`; `AccelerationBody`/`AngularAccelerationBody` | L |
| `…/orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | S | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` — **`UniverseTime` since rev 5211**, whose "no such event" sentinel (`EndOfTime`) is *finite* (~1.7e29 s), so all three go through `IsSaturated()` guards to keep the `0` contract | L |
| `…/encounters` | S | `Vehicle.Patch.Encounters` (`Encounter.{Body.Id,GameTime,ClosestDistance}`), NDJSON — **SOI encounters off `PatchedConic._encounters`**; unchanged in 5348 (`OrbitData.cs`/`IPatchedConics.cs` zero diff). Deliberately **not** the target gauge's list: rev 5266 rewired that onto `FlightPlan.TryFindNextClosestApproach(target, now)`, which writes only `_closestApproaches`. These rows have never reflected a *final* trajectory including planned burns, and `Vehicle.FindFinalFlightPlan()` — the API that would have described one — was **deleted in 5348** | M |
| `…/engines/<n>/{throttle,propellant,min_throttle}` | S/St | `EngineControllerState.{CommandThrottle,IsPropellantAvailable}`; `EngineController.MinimumThrottle` — the member is unchanged in 5348, but rev 5317 inverted how the FC *aggregates* it (see the write row) | M |
| `…/tanks/<r>/fraction` | S | `Mole.FilledFraction(state)` | L |
| `…/battery/{fraction,capacity}` | S | `Battery.MaximumCapacity` (sum); charge/capacity | L |
| `…/power/{produced,consumed}` | S | Σ `SolarPanelState.Produced`+`GeneratorState.Produced`; Σ `PowerConsumerState.Consumed` (instantaneous **W** — 4750/rev 4681 `Joules`→`Watts`) | M |
| `…/solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle,state,current,goal}` | S/St | `SolarPanelState.*` (`Produced` = instantaneous **W**, 4750 `Joules`→`Watts`); `SolarTrackerState.CurrentAngle` (1:1 by index); deploy via linked `KeyframeAnimationModule` | M |
| `…/generators/<n>/{active,produced}` | S | `GeneratorState.{Active,Produced}` (`Produced` = instantaneous **W**, 4750 `Joules`→`Watts`) | M |
| `…/lights/<n>/{on,brightness,color,inner_angle,outer_angle}` | S/St | `PowerConsumer.LightIsActive`; `LightModule.Template.{Intensity,ColorRgb,InnerAngle,OuterAngle}` (inner/outer_angle = the cone half-angles, `rad→deg`) | M (template H) |
| `…/lights/<n>/{goal,current,state}` | S/St | actuate animation via linked `KeyframeAnimationModule` (`Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>()`, same scan `SolarPanel.OnPartCreated` uses); only when the light part has one | M |
| `…/docking/<n>/{docked,docked_to,pushoff_impulse}` | S | `DockingPort.Docked`/`DockedToPart.Id`/`PushoffImpulse` (N·s) | M |
| `…/decouplers/<n>/{fired,fire}` | S/T | `Decoupler.IsActive` — **5348: `Decoupler` is now a multi-instance component module** (`PartTemplate.Decoupler` deleted; instances are constructed from `template.Components`). Stock content still has exactly one decoupler per part, so `<n>` ordinals are stable today; a modded/future part with two would produce two ordinals where the addressing model assumed one | M |

New `/sim/events` types (snapshot diff in `EventDiffer`): `engine-state`, `flameout`, `docked`,
`undocked`, `decoupled`, `animation-complete`, `battery-depleted`, `battery-charged`.

## Read surface — parts (welds anchor picker; gated by `telemetry_vessel_parts`)

Anchor in `Game/Ksa/Readers/PartsReader.cs`. Top-level parts **with their subparts nested under
`subparts/<m>/`** (a subpart is a full `Part` with its own `InstanceId`); the welds anchor picker —
either level's `instance_id` is a valid weld anchor. Cached per vehicle
(`ConditionalWeakTable<Vehicle,…>`), rebuilt on `Vehicle.Parts.Count` change or every 10 s (sim
seconds). `<n>`/`<m>` are 0-based indexes; `instance_id` is the **stable** handle a weld uses.

| Path | A | KSA anchor | Risk |
|---|---|---|---|
| `vessels/by-id/<id>/parts/<n>/{instance_id,id,display_name,template,is_root,subpart_count,position}` | S | `Vehicle.Parts.{Parts,Count}`; `Part.{InstanceId,Id,DisplayName,Template.Id,PartParent,SubParts,PositionVehicleAsmb}` | Low |
| `vessels/by-id/<id>/parts/<n>/subparts/<m>/{instance_id,id,display_name,template,position}` | S | `Part.SubParts` → `Part.{InstanceId,Id,DisplayName,Template.Id,PositionVehicleAsmb}` (subpart-aware: composes through `PartParent`) | Low |

## Control surface — G4 expansion

Anchors in `Game/Ksa/Actuators/**`; routed by `KsaCatalog`. Frame phase unless noted.

| Path | A | Write | KSA anchor (actuator) | Risk | Phase |
|---|---|---|---|---|---|
| `…/ctl/throttle` | St | `0..1` | `Vehicle._manualControlInputs.EngineThrottle` (reflection — no public setter) | H | Frame |
| `…/ctl/stage` | T | `1` | `Parts.SequenceList.ActivateNextSequence` + `UpdateAfterPartTreeModification` — signature unchanged, **semantics moved at 5348/rev 5329 (D1)**: the body went `Parts[n].ActivateInStage(vehicle)` → `Parts[n].ActivateSubtreeInStage(vehicle, sequence.Number)`, which walks `GetSubtreeSequencedModules()` and activates only modules whose own `Sequence` matches. Three consequences: it is now a **subtree** walk (engines/decouplers on sub-parts are staged where they were skipped); only `ISequenced` implementors fire, which is exactly `EngineController` and `Decoupler`, so **`ctl/stage` no longer flips `rcs/<n>/active` as a side effect** (`ThrusterController` is `IActivate` but not `ISequenced`); and a part with an engine in seq 2 and a decoupler in seq 3 now needs two presses. ⚠️ `StagingActuator.Stage` still calls `UpdateAfterPartTreeModification()` from the **Frame** lane, which mutates `PhysicsStates`/`UpdateCollisionGeometry()` in the same window `debug.teleport` vacated — pre-existing, **not widened** by 5348, recorded not fixed | M | Frame |
| `…/ctl/rcs` | St | `0`/`1` | `ThrusterController.SetIsActive` over all controllers | M | Frame |
| `…/ctl/translate` | St | `x y z` (signs) | `Vehicle._manualControlInputs.ThrusterCommandFlags` (reflection — same struct as throttle; translate bits only, rotation bits preserved). `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`Direct` mode → `SelectJetsToFire`); sign→flag mapping verified against the `KittenBackPackSubPart` nozzle geometry (+x=`TranslateForward`, +y=`Right`, +z=`Down`). Latches until rewritten. Added 2026-07-04 | H | Frame |
| `…/ctl/rotate` | St | `x y z` (signs) | `Vehicle._manualControlInputs.ThrusterCommandFlags` (reflection — same struct as throttle/translate; rotation bits only, translation bits preserved). `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`Direct` mode → `SelectJetsToFire`; `ComputeTvcControl` decodes the same bits for gimbals); sign→flag mapping is KSA's own torque decode (+x=`RollRight`, +y=`PitchUp`, +z=`YawRight`). **Auto attitude strips rotation bits — full authority needs `attitude_mode=manual`.** Latches until rewritten. Added 2026-07-22 (W1, AGC_PLAN §7.4) | H | Frame |
| `…/ctl/attitude_mode` | St | token | `FlightComputer.AttitudeMode`/`AttitudeTrackTarget` (`manual` → Manual; else Auto+track) | M | **Solver** |
| `…/ctl/attitude_frame` | St | token | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | M | **Solver** |
| `…/ctl/attitude_target` | St | `x y z w` | `FlightComputer.AttitudeTarget = {Target2Cci,RatesCci}` (+Custom track) | M | **Solver** |
| `…/ctl/burn` | St | `ut dvx dvy dvz` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` — both members still land, but **rev 5317 moved the burn's timing and throttle profile (D4)**: `BurnTarget` gained `float? Throttle` (auto-burn throttle is now latched on the target, `Burn?.Throttle ?? SolveMinimumDurationThrottleCap()`, seeded from `PlannedBurnThrottle`) and `bool LastIgnitionDenied`; `MAX_TRANSIENT_BURN_FRACTION` 0.3f → 0.5f; `TVC_SETTLE_TOLERANCE = 0.02` → `TVC_SETTLE_POINTING_TOLERANCE = 0.0017453` (0.1°); the TVC gain matrix went `(10000, 1000)` → `(50, 100)` alongside a row-vs-column multiplication **bug fix** (`vector.MultiplyAsRow`); and `HasAnyPropellant` was deleted for a two-consecutive-denials latch on `LastIgnitionDenied`. Burn duration, throttle profile and the point at which the FC abandons an auto burn all differ from the 5261 baseline for the same commanded Δv | M | **Solver** |
| `…/ctl/rcs_mode` | St | token | `FlightComputer.RCSMode` (`FlightComputerRCSMode.{Enabled,Disabled}`) — the in-game **R** keybind. Since 5168/rev 5143 this is a hard cut-off for **manual** RCS too: `ComputeRcsControl` zeroes `ThrusterCommandFlags` (`:471`) so `ctl/translate`+`ctl/rotate` go dead, and `UpdateRcsParams` zeroes the RCS torque authority (`:884`). **Solver** phase because `CopyFrom` copies it. Added 2026-08-05 | M | **Solver** |
| `…/engines/<n>/min_throttle` | St | `0..1` | `EngineController.MinimumThrottle` — the member is unchanged and the write still lands, but **rev 5317 inverted its effect (D2)**: `FlightComputer.ComputeActiveEnginePerformance`'s fold over active engines went seed `1f` → `0f` and `MathF.Min(num, …MinimumThrottle)` → `MathF.Max(…)`, and `ActiveEnginePerformance.MinThrottle` is the clamp floor in both `SolveBurnThrottle` and the manual path. On a multi-engine stack the **effective** floor is now set by the *most* restrictive engine instead of the least; the empty-set default flipped 1.0 → 0.0 | M | Frame |
| `…/rcs/<n>/active` | St | `0`/`1` | `ThrusterController.SetIsActive` — unchanged, but two 5348 notes: the ctor **no longer broadcasts `part.ActivateInStage(null)`** at construction (`IsActive` still defaults `true`, so RCS state is unchanged), and **`ctl/stage` no longer flips this flag** because `ThrusterController` is `IActivate` but not `ISequenced` (D1). `ThrusterControllerState.Zero` went from a static property to a method taking `UniverseTime commandTime` (rev 5333); gatOS never calls it | M | Frame |
| `…/lights/<n>/on` | St | `0`/`1` | `PowerConsumer.LightIsActive` | M | Frame |
| `…/lights/<n>/brightness` | St | number | `Template.Intensity.Value` (per-instance clone) | H | Frame |
| `…/lights/<n>/color` | St | `r g b` | `Template.ColorRgb.{R,G,B}`+`OnDataLoad` (per-instance clone) | H | Frame |
| `…/lights/<n>/outer_angle` | St | number (deg) | `Template.OuterAngle.Value` (radians, per-instance clone); write clamped to `Light.CreateSpotLight`'s `[1E-05, 1.5697963]` rad, and lowers `InnerAngle` to ≤ outer (else CreateSpotLight swaps them) | H | Frame |
| `…/lights/<n>/inner_angle` | St | number (deg) | `Template.InnerAngle.Value` (radians, per-instance clone); write clamped to `[0, OuterAngle]` | H | Frame |
| `…/decouplers/<n>/fire` | T | `1` | `Decoupler.SetIsActive` (re-fire → EBUSY; **disabled → EOPNOTSUPP** — since 5168/rev 5132 `SetIsActive` is gated on the new `Decoupler.IsEnabled`, so an unguarded call was a silent no-op). **5348: `Decoupler` is now a multi-instance component module** (`PartTemplate.Decoupler` deleted, instances constructed from `template.Components`); stock content still has one per part so `<n>` ordinals are stable, but a part with two would produce two ordinals | M | Frame |
| `…/docking/<n>/undock` | T | `1` | `InputEvents.VehicleDockingInputData{Undock=true}` → `DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)` (not docked → EBUSY) | M | Frame |
| `…/ctl/focus` | T | `1` | `Program.GetMainCamera().SetFollow(vehicle, tidalLocking:true, changeControl:false)` — moves the view only | M | Frame |
| `bodies/<id>/focus` | T | `1` | same `camera.focus` action on a celestial (`CurrentSystem.Get(id)` → `Astronomical`); view-only, exempt from the authority gate | M | Frame |

A STATE control file's **read** returns the current setpoint, so the vessel-level ones need a reader
that samples it back. These are populated in `VesselReader.BuildFull` (anchor `SampleFlightComputer` +
`GetManualThrottle`): `ctl/throttle` ← `Vehicle.GetManualThrottle()`, `ctl/rcs` ← any
`ThrusterController.IsActive`, `ctl/translate` ← `Vehicle.GetThrusterFlags()` decoded to body-axis
signs (anchor `TranslateActuator.Read`), `ctl/rotate` ← the same flags' rotation bits decoded to
body-axis torque signs (anchor `RotateActuator.Read`), `ctl/attitude_mode` ← `FlightComputer.AttitudeMode`/`AttitudeTrackTarget`
(`manual` when Manual, else the track-target name), `ctl/attitude_frame` ← `FlightComputer.AttitudeFrame`,
`ctl/rcs_mode` ← `FlightComputer.RCSMode` (anchor `FlightComputerActuator.ReadRcsMode`).
Note the `ctl/translate`/`ctl/rotate` read-backs report the **commanded** signs, not whether jets
actually fire — with `ctl/rcs_mode = Disabled` the command reads back intact while the game ignores it,
which is precisely why `rcs_mode` is exposed.
(Before this wiring the snapshot reported the record defaults — throttle `0`, attitude `""` — on every
transport regardless of the real state.)

The `ctl/engine` ignition master is read in `VesselReader.ReadBasics` (anchor `ReadEngineOn`, always
on — not gated by the detail pass): `ctl/engine` ← `Vehicle.IsSet(VehicleEngine.MainIgnite, false)`
(= `_manualControlInputs.EngineOn`, the live state `ctl/ignite`/`ctl/shutdown` set — the same boolean
the game's ignite button reads). This is distinct from the per-engine `engines/<n>/active`
"allowed to fire" flag.

> **4826 behavior note (post-decouple inheritance):** `Vehicle.Split` now copies
> `_manualControlInputs` (engine-on + throttle) and the active staging sequence to the separated
> vehicle, and `Decoupler.Decouple` no longer force-deactivates the separated stage's `IActivate`
> modules — so a freshly decoupled/undocked stage reports **inherited** `ctl/engine`, `ctl/throttle`,
> `engines/<n>/active`, `rcs/<n>/active`, `decouplers/<n>/fired` values instead of off/0. Members and
> units are unchanged (the reads are faithful); see the
> [read-surface 4826 findings](../scope/ksa-read-surface.md#4826-findings).

## Control surface — first-class per-vessel nodes (outside `/sim/debug`)

Anchors in `Game/Ksa/Actuators/ScaleActuator.cs` and `Game/Ksa/Render/VesselForceRender.cs`; routed by
`KsaCatalog` and **exempt from the active-vessel authority gate** (`KsaCatalog.AnyVesselActions`) —
each is a deliberate by-id operation on an arbitrary vessel, intentionally placed under the regular
vessel area rather than `/sim/debug`. Both ported from `unscience` (garrys-torch scaling /
i-feel-seen).

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `vessels/by-id/<id>/scale` | St | value > 0 | `ScaleActuator.Set`: recursive `Part.Scale = (f,f,f)` over `Vehicle.Parts.Parts`/`Part.SubParts` (public `double3` setter; invalidates cached transform matrices), one-shot — KSA resets it on vessel rebuild; KittenEva avatar via reflected `_renderable._characterAvatar.Core.Scale = f*0.01f` (0.01 == 1:1). **5348 (D6): `Part.ScaleTotal` composition went additive → multiplicative** (`Scale + PartParent.ScaleTotal.Transform(inverse(Asmb2ParentAsmb))` → componentwise `Scale * PartParent.ScaleTotal`), so a subpart's `ScaleTotal` is now `factor²` where it was ≈`2·factor`. **Bounded:** the `Part.Scale` setter still only calls `ResetCachedPosMatrixValues()` — it does *not* call the new `RefreshScale()` (reached solely from the `Part` ctor and `VehicleEditor`), so gatOS's write stays visual/transform-only exactly as on 5261, and `StickerPicker` bypasses the `ScaleTotal`-scaled bounding sphere by raycasting the mesh. **New divergence:** rev 5329's `IRescale` made the game's own editor scaling *physical* (`RefreshScale()` rebuilds BEPU colliders, tank `StorageVolume`, inert mass, nozzle areas, decoupler separation force, connector offsets, then `Tree.RefreshStaticMass()`) and clamped to **0.5×–2×** with 0.25 m diameter quantization. gatOS's `/sim` scale does none of that and admits any finite value > 0 — a deliberate cheat-mod divergence, but it no longer means what the in-game gizmo means | H (reflection + `GetType().Name` gate) | Frame |
| `vessels/by-id/<id>/always_render` | St | `0`/`1` | `VesselForceRender.Set`: registry op; installs **two Harmony prefixes on its own `Harmony("gatos.always_render")` instance only while ≥ 1 vessel is marked** — `Vehicle.GetWorldMatrix(Camera)` + `Vehicle.UpdateRenderData(IViewport,int)` (`Viewport` through 5348) — reproducing the stock bodies minus the `GetObjectDiameterPixelsAsDouble < 1.0` sub-pixel cull (`Camera.GetPositionEgo`, `Vehicle.Body2Cce`, `Vehicle.GetMatrixAsmb2Ego`, `PartTree.UpdateRenderData`, `Vehicle.IsEditedVehicle`) | M (dynamic Harmony; `UpdateRenderData` is virtual — KittenEva's override renders via its own path and is **not** affected) | Frame |

Read-backs are sampled in `VesselReader.SampleCore` (always on — not gated by the detail pass):
`scale` ← a representative `Part.Scale.X` (best-effort, `1.0` fallback; anchor `ScaleActuator.Read`);
`always_render` ← the gatOS-owned `VesselForceRender` registry (no KSA read). `always_render` marks
key on the vessel **id** (they survive scene rebuilds; `scale` does not — KSA resets `Part.Scale` on
rebuild) and are pruned when the vessel despawns (`VesselForceRender.Prune`, riding the sampler's
vehicle enumeration; pruning the last mark also removes the patches).

`/sim/debug/` (G-D2; gated by `[control] debug_namespace`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/vessels/<id>/teleport` | T | `px py pz vx vy vz` | `Orbit.CreateFromStateCci`+`Vehicle.Teleport`+`UpdatePerFrameData` — `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` keeps its signature and null-semantics in 5348; its only body delta is a `RemoveFromCurrentBubble()` refactor. **Moved to Solver phase at 5348 (C2)**: revs 5331/5339 put `TrimBubbles()`/`IntakeOrphans()`/`MergeBubbles()`/`SplitBubbles()` — structural bubble-list *and* object-pool mutation — inside `VehicleUpdateTask.Run()` on the solver thread, and the teleport path reaches `PhysicsBubble.RemoveVehicle` (`_vehicleStates.Remove`, `TopologyVersion++`, `ConstraintSim.RemoveVehicle`). The job is in flight from the tail of `Program.PrepareFrame` (`Program.cs:2047`) until the next frame's `JobSystems.VehicleSolver.Wait()` (`:2010`) — i.e. across the whole GUI phase where the Frame lane drains; the engine states the invariant itself (`VehicleUpdateTask.SyncWindowBubbles` throws unless the task is idle). The gatOS solver prefix runs after that `Wait()` and before the re-queue: the one provably safe window | H | **Solver** |
| `debug/vessels/<id>/impulse` | St | `x y z [cci\|body] [ns\|dv]` | `Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,TotalMass,Parent}` + `Orbit.CreateFromStateCci`+`Vehicle.Teleport`+`UpdatePerFrameData` — the velocity-bump variant of the teleport pattern; Δv = J/`TotalMass` (the `Vehicle.Split` separation-impulse math); `body` rotates via `double3.Transform(v, GetBody2Cci())`. **Moved to Solver phase at 5348 (C2)** — same rationale as `teleport` above: it routes through `Vehicle.Teleport` → `RemoveFromCurrentBubble()` → `PhysicsBubble.RemoveVehicle`, and bubble structure is now mutated by `VehicleUpdateTask` on the solver thread | H | **Solver** |
| `debug/vessels/<id>/refill_fuel` | T | `1` | `Vehicle.RefillConsumables()` | M | **Solver** |
| `debug/vessels/<id>/refill_battery` | T | `1` | `Battery.Refill(ref state)` via `GetModuleAndAllMutableStatesForInitialization` | M | **Solver** |
| `debug/vessels/<id>/docking/<n>/pushoff_impulse` | St | N·s (≥0) | `DockingPort.PushoffImpulse` (live float; stock 7000 N·s from XML; 4750/rev 4683 rename, was `PushoffForce` N) | M | Frame |
| `debug/time/warp` | St | factor | `Universe.SetSimulationSpeed(double, alert:false)` (public) | M | Frame |
| `debug/focus` | St | vehicle/body id | `camera.focus` by id (view-only; same action as `ctl/focus`) | M | Frame |
| `debug/control_vessel` | St | vehicle id | `Program.GetMainCamera().SetFollow(vehicle)` + `Program.ControlledVehicle = vehicle` (focus **and** control) | M | Frame |
| `debug/always_render_iva` | St | `0`/`1` | `IvaActuator`→`IvaForceRender.SetEnabled`: flips `PartModelModule.Template.Internal=false` over `PartModel.Instances`; installs/removes its own `gatos.iva` Harmony patches (`PartModel..ctor`/`AddInstance` postfixes) only while on (vessel-agnostic) | M (dynamic Harmony) | Frame |
| `debug/vessels/<id>/weld` | St | `<target> <piid> x y z pitch yaw roll lock` | `WeldManager.Create`→`WeldEngine.UpdateWeld`: `Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,BodyRates,CenterOfMassAsmb,Parent,Orbit,Teleport,UpdatePerFrameData}`, `Orbit.CreateFromStateCci`, `IParentBody.GetCci2Cce`, `Universe.GetJobSimStep(double).NextTime`, `Program.GetPlayerDeltaTime`, `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` (subpart-aware). `<piid>` resolves over `Vehicle.Parts.Parts` **and** each part's `Part.SubParts` (`WeldManager.FindPart`), so a top-level part or a subpart anchors — an animated subpart tracks its live pose. **5348:** `Universe.GetJobSimStep` has **zero diff**, so `WeldEngine`'s `NextTime` rationale holds exactly, and `WeldManager.Update` already calls `JobSystems.VehicleSolver.Wait()` first, so welds are unaffected by the phase move that hit `debug.teleport`/`debug.impulse`. What *did* change is the model underneath: bubble merge/split/trim is now worker-side in `VehicleUpdateTask` with a 2.0× split hysteresis and a cached pair-clearance dictionary, `Universe.{MergeVehicleTasks,TrimPhysicsBubbles,AddVehiclesToTasks}` and `Universe._physicsBubbles` are **deleted**, and `RemoveFromBubble` is reached via the private `RemoveFromCurrentBubble()`. A per-tick weld teleport therefore orphans and re-intakes the vessel every tick on the worker — the highest live-risk item of the pass | H | Frame |
| `debug/vessels/<id>/weld_here` | St | `<target> <piid> [lock]` | `WeldManager.CreateAtCurrentPose`→`WeldEngine.CapturePose` (inverse transform of the above) | M | Frame |
| `debug/vessels/<id>/unweld` | T | `1` | `WeldManager.Remove(vehicle.Id)` (registry op — no KSA) | L | Frame |
| `debug/welds/clear` | T | `1` | `WeldManager.Clear` (vessel-agnostic) | L | Frame |
| `debug/welds/<source>/enabled` | St | `0`/`1` | `WeldManager.SetEnabled` (suspend/resume; keeps the entry) | L | Frame |
| `debug/thug_life/add` | St | `<vessel> <piid>` or `<vessel> <piid> x y z pitch yaw roll w h` | `ThugLifeManager.Create` → lazy GPU build + per-frame world-space quad draw (see the render set below); anchor vehicle resolved from `Token` via `ResolveVehicle` (vessel-agnostic) | **H** (render) | Frame |
| `debug/thug_life/clear` | T | `1` | `ThugLifeManager.Clear` (vessel-agnostic; tears down the render postfix + GPU resources when the last entry goes) | L | Frame |
| `debug/thug_life/<id>/position` | St | `x y z` | `ThugLifeEntry.Position` (id in `ordinal`; consumed by the per-frame anchor math) | **H** (render) | Frame |
| `debug/thug_life/<id>/rotation` | St | `pitch yaw roll` | `ThugLifeEntry.Rotation` (id in `ordinal`) | **H** (render) | Frame |
| `debug/thug_life/<id>/size` | St | `w h` | `ThugLifeEntry.{Width,Height}` (id in `ordinal`) | L | Frame |
| `debug/thug_life/<id>/visible` | St | `0`/`1` | `ThugLifeEntry.Visible` (id in `ordinal`) | L | Frame |
| `debug/thug_life/<id>/remove` | T | `1` | `ThugLifeManager.Remove(id)` (id in `ordinal`) | L | Frame |

The `debug/welds/<source>/{target,part,offset,rotation,lock_rotation}` registry view is a **game-free
projection** of `WeldManager.Snapshot()` (`WeldSnapshot` records — no KSA read). Likewise the
`debug/thug_life/count`, `…/<id>/{vessel,part,spec}` reads are a **game-free projection** of
`ThugLifeManager.Snapshot()` (`ThugLifeSnapshot` records); only the per-frame anchor math + GPU draw
touch KSA (the render set below).

**Render & weld cheats (ported from `unscience`, exposed only on gatOS surfaces — no ImGui).**
`debug.always_render_iva` toggles `IvaForceRender`, which installs **two Harmony patches on its own
`Harmony("gatos.iva")` instance only while enabled** (a `PartModel..ctor(PartModelModule.Template)`
postfix + an editor-only `PartModel.AddInstance` postfix) and bulk-flips
`PartModelModule.Template.Internal=false` over `PartModel.Instances`; disable restores the flags and
unpatches. The **welds** registry (`WeldManager`) drives a per-frame `Vehicle.Teleport` of each source
onto its anchor in `OnAfterUi` (`Mod.DriveWelds`, game thread, after `JobSystems.VehicleSolver.Wait()`)
— a **third game-thread mutation site** beside the Frame and Solver drains; it self-gates to a no-op
when no welds exist, so it needs **no** Harmony patch. Both tear down on unload
(`Mod.TeardownGameCheats`). All weld create/remove/enable/clear and the IVA toggle are **Frame-phase**.
Anchors verified `2026-06-28` against `2026.6.9.4750`; re-verified (static) 2026-07-03 against
`2026.7.3.4826` (`Vehicle.Teleport`/`JobSystems`/`Orbit.CreateFromStateCci` and the IVA render gate all
unchanged). Subpart anchoring added 2026-07-16 against `2026.7.6.4939`: `WeldManager.FindPart` now also
searches each part's `Part.SubParts` (a subpart is a full `Part` with its own `InstanceId`; the
`PositionVehicleAsmb`/`Asmb2VehicleAsmb` members the weld math uses are subpart-aware — they compose
through `PartParent`, the same properties purrTTY's in-world quads track animated subparts with), and
`PartsReader` surfaces subparts under `parts/<n>/subparts/<m>/` for discovery.

**`thug_life` — gatOS's first custom GPU rendering (⚠️ HIGHEST-CHURN KSA COUPLING).** The
`debug.thug_life_*` actions (ported from `unscience`, exposed only on gatOS surfaces) anchor a flat,
world-space textured quad — the "thug life" sunglasses meme — to a part on a vehicle, tracking it each
frame. It is the **deepest coupling gatOS has into KSA's render-pipeline internals**: a Vulkan textured
quad built and recorded directly into KSA's scene. All anchors live under
`gatOS.GameMod/Game/Ksa/ThugLife/` and are **Risk High** unless noted. The render set is the **one set
most likely to break on any game update** (render internals churn far faster than the gameplay APIs the
rest of the matrix binds), and unlike the reflective accessors a render-API rename **does** fail the
build at the `[KsaAnchor]` site.

| Anchor site | KSA / Brutal members | Assemblies | Risk |
|---|---|---|---|
| `ThugLifeRenderPatches.Apply` | dynamic Harmony **postfix on `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`** (`KSA/SuperMeshRenderSystem.cs:329`) — the only injection point for a world-space draw; installed lazily on the first entry, removed with the last/at unload | KSA | **H** |
| `ThugLifeQuadRenderer.BuildPipeline` (`unsafe`) | `Program.OffScreenPass.{Pass,SampleCount}`; `ModLibrary.Get<ShaderReference>("UnlitMeshVert"/"UnlitMeshFrag")`; `RenderTechnique.CreateShaderStages`; `Presets`/`RenderingPresets`; `Renderer.{Device,Allocator,DynamicStateInfo,ViewportState,Graphics}`; `VkUtils.StageAndUploadToBuffer` | Planet.Render.Core, Brutal.Vulkan(.Abstractions) | **H** |
| `ThugLifeQuadRenderer.RecordDraw` (per-frame draw + ego-space anchor math, in `TryComputeModelEgo`) | `Program.GetMainCamera()`/`Camera.MVP.viewProjection`; `Vehicle.GetMatrixAsmb2Ego(Camera)`; `Vehicle.Asmb2Ego`; `Part.PositionEgo(in double4x4)`; `Part.Asmb2Ego(doubleQuat)`; `double3.Transform`; `Program.SetViewport` | KSA, Brutal numerics | **H** |
| `ThugLifeTextureFactory.UploadPixels` | `SimpleVkTexture` ctor; `Renderer.Allocator.CreateStagingPool`/`AddStagingBuffer`; `VkUtils.UploadBufferToImage`; `DeviceEx.CreateSampler` (builds an `R8G8B8A8UNorm` texture + sampler) | Planet.Render.Core, Brutal.Vulkan(.Abstractions) | **H** |
| `ThugLifeManager.{Update,IsLive}` | `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.Parts`; `Part.InstanceId` (per-frame validation / anchor re-resolve) | KSA | **L** |
| `ThugLifeManager.EnsureGpu` | `Program.GetRenderer()` (lazy GPU lifecycle) | Planet.Render.Core | M |

The render postfix, the command drain, and entry edits all run on the **main thread**
(`SuperMeshRenderSystem.RenderMainPass` runs there — see the ksa skill's `quad.md`), so there is no
cross-thread game-state access; the manager publishes an immutable `ThugLifeEntry[]` (swapped on
add/remove) that the postfix reads. The whole feature is **off by default = zero patches/GPU** (the
welds/IVA "only active while toggled on" discipline) and **runtime-only** (never persisted); a GPU fault
self-disables it (`Active=false`). `UpdateThugLife()` (game thread, `OnBeforeUi`) revalidates/re-resolves
each entry per frame; `_thugLife?.Clear()` in `Mod.TeardownGameCheats` tears it down at unload. Pipeline
assumptions (the `"UnlitMeshVert"/"UnlitMeshFrag"` shader keys, `R8G8B8A8UNorm`, reverse-Z depth,
`Program.OffScreenPass` MSAA sample count) and the new render-DLL references are catalogued in
[`../scope/ksa-assets-and-versions.md`](../scope/ksa-assets-and-versions.md). Anchors verified
`2026-06-28` against `2026.6.9.4750`; re-verified (static) 2026-07-03 against `2026.7.3.4826` —
`RenderMainPass` byte-identical, shader keys/assets and `OffScreenPass` unchanged; the live quad-draw
check remains pending (`docs/VALIDATION.md`). Re-verified (static) **2026-08-23 against
`2026.8.22.5348`**: `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)` is still exactly **one**
overload, so both lookups (Apply and Remove) stay unambiguous; its body is now wrapped in
`using (commandBuffer.TagRegion(Profiler.GpuTag.MeshRendererV2))`, and a Harmony postfix runs after the
`finally`, so gatOS's quad draws are attributed **outside** that GPU tag — profiler attribution only, no
mis-draw, patch still installs. `Program.GetCrewPortraitViewport(0|1)` and `_crewPortraitViewportStart = 4`
are unchanged so `ThugLifeManager.CurrentPassBit()` still classifies correctly, **but the long-standing
claim that the crew-portrait viewports are always `Visible` is now false** (D7): revs 5276/5295 wrapped
the portrait update in `if (GameSettings.ShowCrewPortraitCameras())` (else both viewports are forced
`Visible = false`) and `CrewPortraitPanel.Update` itself sets `Visible = k < _visibleCount`. Since
`RenderViewport` is gated on `viewport.Visible`, with portraits off or unoccupied `RenderMainPass` — and
therefore the thug_life postfix — never runs for those viewports; the `Cameras & Crew` pass bit simply
goes unused. `PartModel.AddInstance` also gained a `viewport == Program.MainViewport` guard (rev 5308) —
signature and single-overload-ness unchanged, so the positional `__0`/`__1` args still bind, and the
narrowing is *helpful*. Multi-viewport leak audit re-run: `RenderMainPass` call count is still 3 and
`ResolveAttachments` still 3, with no gatOS injection leaks.

**`stickers` — gatOS's second render-thread draw injection.** The `paint.sticker_*` actions
(`/sim/paint/stickers`) project a user PNG onto whatever geometry sits inside a box anchored to a
vehicle part or a geodetic point on a body. It is the second — and only other — place gatOS records
its own draws into KSA's frame, and it deliberately shares **nothing** with `thug_life`: a different
method (`KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer)`, not
`SuperMeshRenderSystem.RenderMainPass`), a different Harmony instance (`gatos.stickers`, so an
unpatch of one cannot disturb the other), a different pass shape (a projected decal volume reading
the resolved scene depth, not a flat world-space quad), and its own pipeline, mesh, descriptor pool
and texture ring. The pass is a near-verbatim port of KSA's own post-resolve overlay
(`GridPass.Run`), which is what makes the seam defensible: the engine draws in that same window for
the same reason. The postfix is installed **only while at least one sticker is live** and removed on
the last one, so with nothing placed there is no patch, no pipeline and no GPU object at all — the
`thug_life`/welds/IVA "only active while toggled on" discipline. Two identity filters keep it to the
main viewport (`__instance` must be `Program.OffscreenTarget` **and** `Program.RenderedViewport` must
be `Program.MainViewport`); crew-portrait viewports resolve their own targets with their own cameras
and the decal matrices were composed against the main camera this frame. A draw fault latches
`Active=false` + `renderer=degraded` + `last_error` and logs once, exactly as `thug_life` does. The
fifteen anchors live under `gatOS.GameMod/Game/Ksa/Paint/Stickers/` and are tabulated in
[Paint](#paint) below; health latches are `paint.sticker_texture` (decode/upload/bindless) and
`paint.sticker_renderer` (pipeline/patch/draw). Anchors verified `2026-08-22` against
`2026.8.19.5261`; re-verified (static) **2026-08-23 against `2026.8.22.5348`** — `KSA.Rendering/RenderTarget.cs`
is **untouched** (`ResolveAttachments(CommandBuffer)` and `SetupGraphicsPipeline` identical), the
reverse-Z/blend/rasterization presets and `Grid.{vert,frag}` are untouched, and the rev-5288 clutter GPU
repack cannot reach a pass that reconstructs its receiving surface from the resolved depth buffer, so the
seam holds; only the cited `KSA/Program.cs` call-site **line numbers** moved (~+95..+150). KSA's own new
"Decal" (revs 5335–5337) does **not** overlap gatOS stickers — `DecalModifierReference` deforms terrain
*height* via a `HeightMap`, it does not paint colour. The live pass is still **unvalidated** — see the
stickers card in `docs/VALIDATION.md`.

Solver-phase commands drain in a Harmony `Priority.First` prefix on
`Universe.ExecuteNextVehicleSolvers` (`Mod.DrainSolverCommands`), which runs **immediately before** the
per-vehicle solver snapshots state. The four `ctl/attitude_*`/`ctl/burn` setpoints need it because KSA's
async vehicle solver copies the whole `FlightComputer` into the solver input at prepare and back over the
live one at apply (`FlightComputer.CopyFrom`); a frame-phase write lands outside that capture and is
overwritten by the in-flight solve (the mode flashes on, then snaps back to manual). The phase is derived
from the action key (`SimCommand.Phase`/`SolverActions`), so all transports route it identically.

### Deferred (documented, per plan §5.4 / open questions)

Writes:
- **Aero `cda`** — `Vehicle._aerodynamicCdABody` is private; no public read.
- **`parts/<instanceId>` tree** — construction-grade; deferred.
- **Engine per-nozzle thrust/burn_time/mass_flow, gimbal read/command** — nozzle/gimbal SoA internals (M/H);
  gimbal command is transient solver state.
- **RCS pulse** — `CommandPulseTime` fires inside the flight-computer loop; deferred.

Reads/events the plan catalogs promise but that are not yet built (the plan is aspirational here;
this matrix and the code are the truth):
- **`staged` and `encounter` events** (plan §4.7) — `EventDiffer` emits the other 11 types; `staged`
  needs a per-vessel stage counter in the snapshot and `encounter` needs the next-patch body id,
  both game-coupled sampler additions. Deferred until wanted.
- **`bodies/<id>/position/cci` + `velocity/cci`** (plan §4.3) — only the ecliptic-frame body vectors
  are sampled (`BodySnapshot.Position/VelocityEcl`); the CCI-frame ones are not.
- **`bodies/<id>/orbit/t_pe`** (plan §4.3) and **`orbit/mean_anomaly`** (plan §4.5) — not carried on
  `OrbitSnapshot`; the other orbit elements/anomalies are.
- **`solar/<n>/tracker/{angle,active}` shape** (plan §4.6) — surfaced as a flat `tracker_angle` file
  (no `active`, no subdir) when `HasTracker`; the subdir/`active` split is deferred.

## Audio playback (GATOS_CUSTOM_AUDIO_PLAN — `/sim/audio`)

Userland audio through the game's FMOD Core system. **Vessel-agnostic** (routed before vehicle
resolution — the target is a clip/channel) and **outside** `debug.*`: gated by `[audio]
audio_enabled` (off ⇒ the surface vanishes; `audio.*` via `/v1/command`/`gatos/command` answers
`EOPNOTSUPP`). The upload store, grammars, status/info files and caps are **game-free**
(`gatOS.SimFs/Audio/**`); only the three anchors below touch the game. gatOS never calls
`System.Update/Close/Release` (the game owns the FMOD system and pumps it on the same thread the
drain + tick run on); gatOS owns every `Sound` it creates and releases them deferred (never while a
channel plays) and at unload (`Mod.TeardownGameCheats` → `AudioActuator.Shutdown`). Deliberate:
playback ignores the >10× warp SFX mute (raw-Core channels bypass `GameAudio.PlaySound`'s gate).
New condition-guarded reference: **`Brutal.Fmod.dll`**.

| Anchor (`Game/Ksa/Actuators/AudioActuator.cs`) | KSA / Brutal members | Risk | Notes |
|---|---|---|---|
| `Play` | `GameAudio.System` (public static `FmodSystem`); `GameAudio.GetChannelGroup(ChannelGroupType.{Sfx,Music,Ui})`; `Fmod.TryPlaySound(sound, group, paused, out Channel)`; `Channel.TrySet{Position,Mode,LoopCount,LoopPoints,Volume,Pan,Pitch,Paused}` | L | The game's own anti-pop idiom (play paused → configure → unpause). Group routing puts the channel under the matching in-game volume slider (the groups are *siblings* — the Master slider does not cascade). |
| `CreateOrGetSound` | `Fmod.TryCreateSound(bytes, Mode.OpenMemory \| _2d \| CreateSample/CreateCompressedSample, in CreateSoundExInfo{Length}, out Sound)`; `Sound.TryGetLength/TryRelease` | L | The in-memory recipe `GameAudio.CreateFmodSound` itself uses — FMOD copies the buffer and sniffs the container (mp3/ogg/wav/flac). ≤ 1 MiB ⇒ `CreateSample` (full decode); larger ⇒ `CreateCompressedSample` (decode during mix — cheap create, ≈ file-size memory, concurrent plays OK). Cached per (clip, version). |
| `Tick` | `Channel.TryIsPlaying/TryGetPosition/TryStop`; `Sound.TryRelease`; `Universe.GetElapsedSeconds()` (event stamps) | L | Per-frame (`Mod.DriveAudio`, `OnBeforeUi` after the drain): prunes finished channels (a recycled FMOD handle answers non-Ok — that *is* the completion signal), enforces `end=`, releases evicted sounds, publishes `/sim/audio/status`, emits `audio.finished`. |

## IVA cabin physics (plans/IVA_MOVEMENTS.md — `/sim/debug/iva`)

Free-floating objects inside a vessel's interior. **Vessel-agnostic** (routed before vehicle
resolution — the target is a registry object) and part of `debug.*`, so it needs `[control]
debug_namespace` and is authority-exempt. **`debug.iva_physics` is the master switch and defaults
off:** while it is off the manager is an empty registry — no physics simulation, no interior mesh, no
buffer pool, no per-frame work, and no Bepu type loaded. Writing `0` releases every object at its exact
rest pose and disposes every simulation.

The physics itself is **game-free** (`gatOS.SimFs/Iva/CabinPhysics.cs`, unit-tested on a bare host) and
the simulation is a **gatOS-owned** `BepuPhysics.Simulation` in the vessel assembly frame — gatOS never
adds a body to KSA's `ConstraintSim`, never patches its callbacks, and installs **no Harmony patch** for
this feature (rationale: [`scope/ksa-runtime-coupling.md#iva-cabin-sim`](../scope/ksa-runtime-coupling.md#iva-cabin-sim)).
New condition-guarded references: **`BepuPhysics.dll` + `BepuUtilities.dll`** (KSA's own embedded engine,
already loaded in-process).

| Anchor (`Game/Ksa/Iva/`) | KSA / Brutal members | Risk | Notes |
|---|---|---|---|
| `IvaPhysicsManager.Update` | `JobSystems.VehicleSolver.Wait()`; `Universe.{CurrentSystem.All.UnsafeAsList,SimulationSpeed}`; `Program.{Editor,MainViewport}`; `IViewport.Mode`; `CameraMode.IVA`; `Vehicle.{Id,AccelerationBody,AngularAccelerationBody,BodyRates,CenterOfMassAsmb,Parts.Count}` | L | The per-frame driver (`Mod.DriveIvaPhysics`, `OnAfterUi` — the sixth game-thread work site), after the solver workers so the kinematics are settled. **`AccelerationBody` is a true accelerometer in every flight situation** (zero in `Freefall`; the `GM/r²` normal force when `Landed`/`Floating`; thrust+drag with gravity excluded when `Maneuvering`), which is why one formula covers pad, coast, burn and landing. Parks under warp, in the editor (`Program.Editor != null` disables `Part` transform caching), and outside the IVA camera unless `run_outside_iva`. |
| `InteriorGeometry.Build` | `Vehicle.Parts.Parts`; `Part.{SubParts,InstanceId,Modules,MatrixAsmb2VehicleAsmb}`; `ModuleList.Get<PartModelModule>()`; `PartModelModule.PartModel.Template`; `PartModelModule.Template.{Internal,RayTracing,Mesh}`; `MeshReference.PositionCompare`; `IVASeat.PositionAsmb`; `double3.Transform` | M | Builds exact interior collision geometry from the vessel's own IVA meshes. `Template.Internal` **is** "renders only through the IVA camera" (`PartModel`'s gate is `(!Template.Internal \|\| viewport.Mode == CameraMode.IVA)` — the flag `always_render_iva` flips), so it is a free, art-driven classifier for interior surfaces. `PositionCompare` is a de-indexed `double3[]` triangle soup retained forever for KSA's own picking raycasts. Read-only; never mutates a part. Falls back to a synthetic box room around the `IVASeat`s. |
| `FloatingObject.ApplyPose` | `Part.{PositionParentAsmb(set),Asmb2ParentAsmb(set),PartParent,PositionVehicleAsmb,Asmb2VehicleAsmb,Scale}`; `double3.Transform`; `doubleQuat.{Concatenate,Conjugate,NormalizeOrZero}` | M | The per-frame transform driver. Both setters call `ResetCachedPosMatrixValues` and `PartModelModule.UpdateRenderData` re-reads them every frame, so rendering/lighting/ray-tracing/IVA gating follow for free — this is KSA's **own** idiom (`KeyframeAnimationModule`, `SolarTracker`). **SubParts only, binding:** `Part.GetReferenceWithChildren` serializes a `Transform` for top-level parts but not SubParts, so a displaced object cannot leak into a save. |
| `FloatingObject.{ReadBodyPose,RestoreRestPose}` | `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb,PositionParentAsmb(set),Asmb2ParentAsmb(set)}` | L | Adopt-time seed pose and exact rest-pose restore. The rest pose is captured into gatOS's own fields, **not** KSA's `PositionParentAsmbSafe`/`Asmb2ParentAsmbSafe` (which belong to the animation system). |
| `IvaPhysicsManager.{Adopt,TryMeasure,IsInteriorProp,FindSubPart,IsLive}` | `Vehicle.{Id,Parts}`; `Part.{InstanceId,SubParts,PartParent,DisplayName,Template.Id,PositionParentAsmb,Asmb2ParentAsmb,Scale,Modules}`; `ModuleList.Get<PartModelModule>()`; `PartModelModule.Template.{Internal,RayTracing,Mesh}`; `MeshReference.PositionCompare`; `Universe.CurrentSystem.All.UnsafeAsList()` | L–M | SubPart lookup (top-level parts refused by design), collision-proxy sizing from the mesh AABB (box only in this build — Bepu's `Box` needs no shape-local rotation, so body orientation *is* part orientation), interior-prop candidacy for `adopt_all`, and liveness. |

## FX editors (`plans/FX_EDITORS_PLAN.md` — `/sim/debug/{engineplume,plumetrail,clouds,terrain}`)

The game's four built-in imgui render editors ("Volumetric Exhausts", "Plume Trails", "Clouds",
"Terrain Editor") as `/sim` filesystems. Part of `debug.*` (gated by `[control] debug_namespace`,
authority-exempt); routed **vessel-agnostically before** vehicle resolution — the addressed entity is a
render template, the one trail renderer, or a celestial body, never a vehicle. Anchors live under
`gatOS.GameMod/Game/Ksa/Fx/` (`FxReflect`, `PlumeActuator`, `TrailActuator`, `CloudActuator`,
`TerrainActuator`, `FxEditorReader`; `FxPristine` holds no KSA type). The field tables, the tree, the
ranges and the validation are **game-free** (`gatOS.SimFs/Fx/FxCatalog.cs` — one row per field drives
both the tree and the game-side re-validation). Anchors verified **2026-08-01 against
`2026.7.10.5056`**; `TerrainActuator.Write` re-stamped **2026-08-23 / `2026.8.22.5348`** (the mirror copy
is now field-wise — see the terrain table). Two inherited value drifts at 5348, no API change:
`PlanetUbo.TessellationRangeMeters`' default went **220 → 50** and the shader's displacement falloff
moved from `range*0.1 … range*0.95` to `range*0.75 … range*0.975` (field name/type/offset unchanged and
gatOS's `1..20000` clamp still admits the new default, but the documented example values for
`debug/terrain tessellation/range_m` are now misleading); and the grass ecotype's `ObjectSeparation`
went 1.3 → 1.45 m with `GenerationRange` 170 → 80 m (revs 5306/5345). The feature is code-complete with
the in-game pass pending (`docs/VALIDATION.md`).

**engineplume** — scope is **per template** (shared by every nozzle referencing it):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/engineplume/templates/<id>/**` | St | per-field (see SPEC §3.7) | `PlumeActuator.TryWrite`: `VolumetricExhaustTemplate.{LengthWeights,Absorption,Emission,Noise,Quality}` → `DoubleReference.Value`/`BoolReference.Value` in place; `ColorGradient.Color0..3 = new ColorRgbReference(float3)` + `OnDataLoad(Mod.Empty)` (the `Value` setter is `protected` — in-place colour writes silently do nothing) | **H** | Frame |
| (apply, after every write) | — | — | `PlumeActuator.Propagate`: `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.RocketNozzles.ModulesAndAllStates`; `RocketNozzleFxState.VolumetricExhaust`; `VolumetricExhaustInstance.{OnSettingsChanged,UpdateModifiers}`; `RocketNozzle.RecomputeGasVisibilityDensity(in …)` — the in-game editor's own post-edit loop | **H** | Frame |
| (read-back / pristine capture) | S | — | `PlumeActuator.TryRead` (same members, read half; colours off the serialized `R`/`G`/`B`) | **H** | — |
| (id resolution) | — | — | `PlumeActuator.Resolve`: `VolumetricExhaustTemplate.Get(string)` (public static; null ⇒ `ENOENT`) | M | — |
| (roster) | S | — | `FxReflect.PlumeTemplates`: `VolumetricExhaustTemplate.References` (internal static) `.GetList()`; fallback `FxEditorReader.HarvestPlumeTemplates` harvests ids off live nozzles (`RocketNozzle.ReactionPlumes[].VolumetricExhaust.Id`) | **H** / M | — |
| (propagation args) | — | — | `FxReflect.PlumeModifierArgs`: `Program.VolumetricExhaustRenderer` (public static) + `_currentAtmosphericPressure`/`_debugThrottle` (private) — **best-effort**, falls back to `(0, 1)`; the per-frame draw path recomputes both for every live nozzle | **H** | — |
| `debug/engineplume/templates/<id>/reset` | T | `1` | `PlumeActuator.Reset` → `FxPristine.Restore` (replays captures through `TryWrite`) + `Propagate` | **H** | Frame |

**plumetrail** — scope is the one **global** renderer; fields are public instance fields the renderer
re-reads each frame, so there is **no apply call**:

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/plumetrail/render/*` | St | per-field (see SPEC §3.7) | `TrailActuator.TryRead`/`TryWrite`: `VolumetricTrailRenderer.{MaxDistance,VoxelDepthFirstSliceThickness,MinStepSize,StepSizeDistanceScale,ErosionMaxDepth,ErosionEdgeSharpness,SelfShadowStepCount,LightBrightness,SkyAmbientBrightness}` (public fields, `float`/`int`). ⚠️ **`render/trail_color` retired at 5402** — `DebugTrailColor` was removed from the renderer (colour is per-`PlumeTrailTemplate` asset now) | M | Frame |
| `debug/plumetrail/render/expansion_time` | St | number, `0.001..10000` s | `TrailActuator` → `FxReflect.TrailSettings` → `PlumeTrailSettings.ExpansionTimeSeconds`; two private hops (`VolumetricTrailRenderer._plumeTrailSegmentsManager` → `PlumeTrailSegmentsManager._settings`), moved off the renderer at revs 5059/5097. Latch `fx.trail_settings` | **H** | Frame |
| (renderer handle) | — | — | `FxReflect.Trail`: `Program.Instance._volumetricTrailRenderer` (private field — the only handle) | **H** | — |
| `debug/plumetrail/clear` | T | `1` | `TrailActuator.Clear`: `Program.Instance.ClearPlumeTrails()` — **public instance** method (no reflection) | M | Frame |
| `debug/plumetrail/reset` | T | `1` | `TrailActuator.Reset` → `FxPristine.Restore` (replays through `TryWrite`) | M | Frame |

**clouds** — scope is **per body → per layer → per cloud type**; the data itself needs **no** reflection:

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/clouds/bodies/<id>/**` | St | per-field (see SPEC §3.7) | `CloudActuator.TryWrite`: `CloudsReference` / `CloudLayerReference` / `VolumetricCloudReference` / `TwoDimensionalCloudReference` / `RaymarchingReference` / `CloudTypeReference` members — `DistanceReference`/`Vector3Reference`/`ColorRgbReference` **construct-new** (they cache a derived value / have a protected setter), `DoubleReference.Value` + `Step.Scale` + `CloudShape.InterpolateShapes` in place | **H** | Frame |
| (apply, after every write) | — | — | `CloudActuator.Apply`: `CloudLayerReference.OnDataLoad(Mod.Empty)`; `CloudRenderer._planetToCloudRenderData` (public) keyed on `Astronomical.Hash`; `CloudLayerRenderData.UpdateStaticData(Renderer, AtmosphericBody, CloudLayerReference, float, float, float)`; `CloudShadowsRenderer.PopulatePlanets(…, RenderTarget)` — the editor's exact sequence; never rebuilds a pipeline (`NoiseScale` is deliberately unexposed) | **H** | Frame |
| (renderer + apply handles) | — | — | `FxReflect.Clouds`: `Program.Instance._planetTransparenciesRenderer` (private) → `GetCloudRenderer()` (public). `FxReflect.CloudApply`: `CloudRenderer._renderer`/`_cloudShadowsRenderer`/`_worleyNoise3dTarget` (private) | **H** | — |
| (read-back / roster) | S | — | `CloudActuator.TryRead` (same members, read half); `FxEditorReader.SampleCloudBodies`: `AtmosphericBody.BodyTemplate.CloudsReference` over `Universe.CurrentSystem.All.UnsafeAsList()` | **H** / L | — |
| `debug/clouds/bodies/<id>/reset` | T | `1` | `CloudActuator.Reset` → `FxPristine.Restore` + `Apply(layer: -1)` (re-uploads every layer) | **H** | Frame |

**terrain** — two tiers: a **global** toggle with no reflection, and per-body **paired** writes:

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/terrain/wireframe` | St | `0`/`1` | `PlanetRenderer.Wireframe` (public **instance** field) via `Program.GetPlanetRenderer()` | M | Frame |
| `debug/terrain/bodies/<id>/**` | St | per-field (see SPEC §3.7) | `TerrainActuator.Write`: `Celestial.BodyTemplate.HeightReference.{Minimum,Maximum}` + `TerrainReference.BiomeMaterials.{BlendStrength.Value,DetailFadeInStart,DetailFadeInEnd}` (construct-new `DistanceReference`) **and** the `PlanetUbo`/`MeshUbo` structs at `(NumCelestials*frame + slot)*Stride` over the mapped memory, then a **field-wise** frame-in-flight mirror (`PlanetRenderer.cs:2388-2398`). **5348 (C3):** revs 5319–5325 (terrain precision rework) gave `MeshUbo` four new split-double anchor fields — `DirAnchorHi`, `DirAnchorLo`, `DirAnchorUvHi`, `DirAnchorUvLo` — that `PlanetRenderer.GenerateMeshData` writes **per frame index, every frame**, from the live camera. The old whole-struct copy of frame 0 into every other frame-in-flight therefore stamped frame 0's terrain anchor over the others' live values (one frame of wrong anchor per mirrored frame, self-healing but real). The mirror now copies only the fields gatOS writes — `PlanetUbo.{TanMeanSlopeRoughnessRadians,HapkeMeanAlbedo,BiomeBlendStrength,DetailFadeStartMeters,DetailFadeEndMeters,TessellationEdgeLengthPixels,TessellationFactor,TessellationRangeMeters}` and `MeshUbo.{MinHeight,MaxHeight,BiomeBlendStrength}` — which is also immune to the next such field addition on either struct | **H** | Frame |
| (slot resolution) | — | — | `TerrainActuator.Resolve`/`HasSlot`: `PlanetRenderer.RenderUboSlot(Celestial)`/`MeshUboSlot(Celestial)` (public; `-1` ⇒ unslotted ⇒ absent from the tree) | M | — |
| (UBO handles) | — | — | `FxReflect.TerrainUbo`: `PlanetRenderer._renderUboMap`/`_meshUboMap` (private `MappedMemory`, host-visible + coherent); strides/counts from the public `PlanetUboStride`/`MeshUboStride`/`NumCelestials`; frame count from `Program.GetRenderer().MaxFramesInFlight` | **H** | — |
| (read-back) | S | — | `TerrainActuator.Read`: `PlanetUbo.{TanMeanSlopeRoughnessRadians,HapkeMeanAlbedo,BiomeBlendStrength,DetailFadeStartMeters,DetailFadeEndMeters,Tessellation*}`, `MeshUbo.{MinHeight,MaxHeight}` — read out of the UBO, i.e. what the GPU samples | **H** | — |
| `debug/terrain/bodies/<id>/reset` | T | `1` | `TerrainActuator.Reset` → `FxPristine.Restore` (replays through the same paired write) | **H** | Frame |

**Health latches and degraded behavior.** Each reflective handle carries its own `KsaHealth` key, so a
decomp drift degrades one capability rather than the feature: **`fx.trail_renderer`**,
**`fx.plume_templates`**, **`fx.cloud_renderer`**, **`fx.cloud_apply`**, **`fx.terrain_renderer`**,
**`fx.terrain_ubo`** (all visible in `/sim/status/accessors`). Degrade behaviors are deliberately
different per family:
- `fx.plume_templates` → the roster falls back to templates harvested off live nozzles (narrower, but the
  family keeps working; `VolumetricExhaustTemplate.Get(id)` is public and unaffected).
- `fx.trail_renderer` / `fx.terrain_renderer` → the family's writes answer `EOPNOTSUPP`.
- `fx.cloud_apply` (or `fx.cloud_renderer`) → the write is **still performed on the reference data and
  still returns `Ok`**; only the immediate GPU re-upload is skipped, so the change appears on the
  renderer's next natural repopulate. The latch still records the degrade.
- `fx.terrain_ubo` → the **per-body roster empties** (`debug/terrain/bodies/` lists nothing, since values
  are read out of the UBO) while the global `wireframe` leaf stays fully live.

Reads are sampled by `FxEditorReader` on the game thread inside `TelemetrySampler`, gated on
`[control] debug_namespace` (off ⇒ the families are never read and `SimSnapshot.FxEditors` stays null),
and memoized: rebuilt only when an FX write landed (`FxEditorReader.Invalidate`, bumped by every
successful actuator write) or after 2 s (which catches edits made in the game's own imgui editors),
otherwise republished by reference. Every field is read back through the **same accessor it is written
through**, which is also what the pristine capture records. Teardown: `Mod.TeardownGameCheats` runs
`FxPristine.RestoreAll()` **first** (while the game reads are still live) and then
`FxEditorReader.Reset()`. No Harmony patches, no per-frame driver, no GPU resources of gatOS's own.

## Programmable camera (`plans/CAMERA_ASBUILT.md` — `/sim/camera/**`)

gatOS as the sole writer of the main viewport's camera: ownership take/release, six reference frames,
aim-with-offset, geodetic placement, JSON tracks, the interpolated `time` channel, and the map's own
zoom. **Vessel-agnostic** — `KsaCatalog` routes the whole `camera.*` family through one `Camera(...)`
sub-dispatcher **before** vehicle resolution and before the authority gate (the addressed entity is a
target reference or the camera itself, never the controlled vehicle), so it is authority-exempt like
`camera.focus` always was. Gated by `[camera] camera_enabled` (off ⇒ the `/sim/camera` surface never
exists and `camera.*` answers `EOPNOTSUPP`); the `time` channel additionally needs **both**
`[control] debug_namespace` **and** `[camera] camera_allow_time_channel`, and `camera.play`/`set`/`stop`
need `[schedule] schedule_enabled` (a camera track *is* a `/sim/ctl/schedules` entry with
`kind = camera-track`) — without it those three answer `EOPNOTSUPP` while every L1/L2 channel keeps
working. All 28 action keys on `CameraCommands` — plus the pre-existing `camera.focus` — are **Frame**
phase; none is in `SimCommand.SolverActions` (nothing about the camera is visible to the vehicle solver).

Everything except the game anchors below is **game-free** (`gatOS.SimFs/Camera/**`: the math
primitives, the validation rules, the three-layer `Track ?? Override ?? Baseline` compositor, the
store + writable `track/` dir, the line grammars, and the whole JSON track parser/evaluator/player).
One guarded Harmony prefix/postfix targets public `GameViewport.OnFrame(double)` (`Viewport.OnFrame`
through 5348) and binds only `Program.MainViewport` by identity (see
[`scope/ksa-runtime-coupling.md#camera-driver`](../scope/ksa-runtime-coupling.md#camera-driver)). The
prefix applies against current-frame target state immediately before KSA builds matrices; the postfix
publishes the final clamped transform. Ownership parks `CameraMode.Fixed` and unfollows, and
`FixedController.OnFrame` wraps its entire body in `if (following != null)`. **IVA and Map ownership
contexts are NOT implemented and are not implementable without a Harmony patch** — evidence in
[`scope/ksa-runtime-coupling.md#camera-mode-contexts`](../scope/ksa-runtime-coupling.md#camera-mode-contexts).
Original anchors verified **2026-08-06**, viewport hook verified **2026-08-09**, against
`2026.8.5.5168`; re-verified (static) **2026-08-23 against `2026.8.22.5348`** — `Viewport.OnFrame(double)`
unchanged, and `Viewport` only *gained* `ShouldRenderStars` and `LightMode : EViewportLightMode` (+4
lines, 0 removed), where `MainViewport.LightMode = Clustered` and secondary viewports' `Forward`
evaluate to exactly the previous hardcoded `UseShadows`/`UseLightPrePass` values, so nothing about the
camera driver changes. Correction carried in from the anchor re-stamp: the comment claiming "Program has
four viewports" has been **wrong in both builds** — it is **6**. **Re-verified 2026-09-02 against
`2026.9.7.5402` — the viewport rework was a compile break here:** `KSA.Viewport` is gone; the hook
target is now `GameViewport.OnFrame(double)`, `Program.MainViewport` is an `IGameViewport`, the
registry holds **8** viewports (1 main, 1 part-thumbnail, 4 secondary, 2 crew-portrait), and
`FixedController`/`Mode` became **protected-set properties** — so the controller install and the
silent Fixed park now go through `ViewportSeam` (two new High-risk reflection accessors, below). Live
recheck pending (`docs/VALIDATION.md`).

**Ownership + the live game camera** (`Game/Ksa/Camera/CameraDirector.cs`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `camera/enabled` (`1`) | St | `camera.enabled` | `CameraDirector.Take`: `Program.MainViewport` (`IGameViewport`); `IGameViewport.{Mode,GetCamera,SetCameraMode,BaseCamera,FixedController}`; **`ViewportSeam.TrySetFixedController` → `GameViewport.FixedController` protected setter, `ViewportSeam.TrySetMode` → `ViewportBase.Mode` protected setter (reflection, new at 5402, Risk H)**; `Camera.{PositionEcl,LocalPosition,LocalRotation,NoRotation,Following,TidalLocking,GetFieldOfView,Orthographic,Unfollow}`. `Mode` is written **directly** (through the seam) so `FixedController.OnSwitchOn`'s `TimedAlert("Fixed Camera")` never draws and no held input is cleared; `Unfollow(changeControl:false)` (the default `true` would null `Program.ControlledVehicle`) | M | Frame |
| `camera/enabled` (`0`), `camera/release` | St / T | `camera.enabled` / `camera.release` | `CameraDirector.Restore`: `Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,SetOrthographic}`; `IGameViewport.{Mode,SetCameraMode}` + the `ViewportSeam` mode write; `Universe.SetSimulationSpeed` (conditional). `SetFollow` **first** (it teleports), then `NoRotation`→`LocalPosition`→`LocalRotation`, projection, mode **last**; restoring *into* Map goes through `SetCameraMode` so `MapController.OnSwitchOn` re-establishes `NoRotation` + the map's control state | M | Frame |
| (per-frame pose apply) | — | — | `CameraDirector.Apply`: `Camera.{PositionEcl,LocalRotation,LookAtRotation,SetFieldOfView,SetOrthographic,SetOrthoHalfHeight}`. `LocalRotation` (not `WorldRotation`) is what `Camera.OnFrame` builds the view matrix from; `SetFieldOfView` takes **degrees** and does **not** clamp (which is what puts fisheye/telephoto in reach) and rebuilds+inverts the projection on every call, so it is written only on change. `ortho_height` has **no public getter in 5168** — nothing to capture, nothing to restore | M | Frame |
| (track `time` channel, C4) | — | — | `CameraDirector.ApplyTimeScale`: `Universe.SetSimulationSpeed(double, alert:false)` (`:1998`), `Universe.GetSimulationSpeed()` (`:2021`), `Universe.IsAutoWarpActive` (`:96`). `alert:false` is load-bearing (the default draws a speed alert *in the footage*). Speed is captured **lazily**, the first frame the channel is driven, and restored only if captured. Neither public `SetSimulationSpeed` overload checks `IsAutoWarpActive`, so a driven `time` channel **fights** an active auto-warp — deliberately unguarded, documented | M | Frame |
| (release blend) | — | — | `CameraDirector.RestorePositionEcl`: the `Camera.PositionCce` composition (`LocalPosition` ⇄ `IFollowable.GetBodyFixed2Ecl` unless `NoRotation`); `IPosition.GetPositionEcl()`. Reproduced rather than called: the camera being blended is already unfollowed, so its own `PositionCce` would not use the captured target | M | Frame |
| `camera/mode` | St | `camera.mode` | `CameraDirector.SetMode`: `Program.MainViewport`; `IGameViewport.SetCameraMode(CameraMode)`. Refused (`EOPNOTSUPP`) while gatOS owns the camera. Note the side effect: `SetCameraMode` calls `Program.ControlledVehicle?.ClearHeldPlayerInput()`, dropping latched `ctl/translate`+`ctl/rotate` flags (SPEC §3.4.19) | M | Frame |
| `camera/follow` | St | `camera.follow` | `CameraDirector.SetFollow`: `Program.MainViewport.{BaseCamera,MapCamera}`; `Camera.{SetFollow,Unfollow}` — **both** cameras, like the game's own follow. Keeps `SetFollow`'s `target + 2.5×MeanRadius×forward` teleport (that is what "go look at this" means); preserves the current tidal flag; a `part:` reference is `EOPNOTSUPP` (the game follows a whole `IFollowable`). Refused while owned | M | Frame |
| `camera/tidal` | St | `camera.tidal` | `CameraDirector.SetTidal`: `Camera.{Following,TidalLocking,SetFollow,PositionEcl}`. `TidalLocking` is get-only and `SetFollow` is its only writer, so the flag change re-issues `SetFollow` and then **re-asserts the captured `PositionEcl`** to undo its unconditional teleport. Refused while owned | M | Frame |
| `camera/map/scope` | St | `camera.map_scope` | `CameraDirector.SetMapScope`: `Program.MainViewport.MapController`; `MapController.Scope` (`:34`, a plain public `double`). **Not ownership-gated** — it configures the game's own map camera. 5402: `MapController.CanChangeControl => ViewportRegistry.IsMainCamera(Camera)` limits the control juggling to the main viewport (no change for gatOS). Three inherited game behaviours: `MapController.OnFrame` clamps it **up** to `Camera.Following.MeanRadius` every map frame; `OnSwitchOn`→`SetDefaults()` recomputes it wholesale after a focus change; it has no visible effect outside `map` mode | M | Frame |

**Frames + targets** (`Game/Ksa/Camera/CameraFrames.cs`, `CameraTargets.cs`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `camera/pose/{frame,aim_frame}`, `pose/position`, `pose/orbit/*` | St | `camera.{frame,aim_frame,position,orbit_*}` | `CameraFrames.TryFrame2Ecl`: `Vehicle.{GetEnu2Cce,GetLvlh2Cce,Body2Cce,ComputeEnu2Cce,ComputeLvlh2Cce,GetPositionCce,GetVelocityCce}`; `Celestial.{GetCci2Cce,GetCcf2Cce,GetDirCcfFromLatLon,MeanRadius,GetPositionCce,GetVelocityCce}`. `GetEnu2Cce`/`GetLvlh2Cce` are **nullable** and `GetEnu2Cce` dereferences `Orbit.Parent` unguarded — both guarded here; the `Compute…` public statics are reused so a celestial anchor gets the game's own ENU/LVLH construction. Nothing ever silently falls back to another frame: unresolvable ⇒ `EOPNOTSUPP` at write time, and per frame the director **holds the last good pose** and logs the reason once | M | Frame |
| `camera/pose/geo` | St | `camera.geo` | `CameraFrames.GeoToEcl`: `Celestial.{GetDirCcfFromLatLon,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius,GetPositionEclFromCce}`. gatOS **calls** `GetDirCcfFromLatLon` rather than restating its trigonometry (CCF +Z = north pole, +X = prime meridian), so a convention change is inherited, not diverged from. `GetTerrainHeightFromDirCce` returns **metres** (`0` with no heightmap) ⇒ altitude is **above terrain**, degrading to above-mean-sphere; the direction is explicitly normalized because `GetSurfacePositionEclFromDirCce` does not. **5348 (D8):** `GetTerrainHeightFromDirCcf`/`Ccc` are unchanged, but `Celestial.SampleCubeFacePointR`'s out-of-range branch replaced a 4-tap bilinear seam fetch with `UnfoldCubeFaceUv` + a single clamped nearest tap (the private `FetchTexelSeamlessR` was deleted) and `GetFaceAndTexelFromDirection`'s texel index went `(int)Math.Floor(u*w - 0.5)` → `(int)(u*w)`. Sub-metre, only near cube-face boundaries | M | Frame |
| (geodetic read-back) | S | — | `CameraFrames.TryEclToGeo`: `Celestial.{GetPositionCceFromEcl,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius}` — the exact inverse (`lat = asin(ccf.Z)`, `lon = atan2(ccf.Y, ccf.X)`), the same pair KSA's own `GetLatitudeFromCce`/`GetLongitudeFromCcf` use. This is what lets the reader publish **both** position spellings. **5348 (D8):** the geodetic altitude shifts sub-metre near cube-face seams, but `GeoToEcl` and `TryEclToGeo` route through the *same* sampler, so they remain exact inverses and the `/sim/camera/pose/geo` round-trip property still holds | M | — |
| `camera/{follow,pose/anchor,pose/aim_target,pose/aim}`, `pose/geo`'s `body:` tail | St | — | `CameraTargets.TryResolve`: `Universe.CurrentSystem.Get(id)` → `Astronomical`; `Vehicle`; `Celestial` — the same lookup `camera.focus` and the game's own follow/control terminal actions use. Existence is validated **at write time** (a reference naming nothing live ⇒ `ENOENT`), so an anchor cannot be pre-armed for an unspawned vessel. `part:` anchors reuse the welds anchor resolver (`WeldManager.FindPart`, now `internal`) | L | Frame |
| (per-frame placement/aim) | — | — | `CameraTargets.PositionEcl`: `Astronomical.GetPositionEcl()`; `Vehicle.{CenterOfMassAsmb,GetBodyFixed2Ecl}`; `Part.PositionVehicleAsmb` (the game's own part-position idiom, `KSA/DockingPort.cs:409`) | L | — |
| (aim up = `velocity`) | — | — | `CameraTargets.VelocityEcl`: `Astronomical.GetVelocityEcl()` — `IVelocity`'s single member, overridden by both `Vehicle` and `Celestial` | L | — |
| (`bodyfixed`/`chase` frames) | — | — | `CameraTargets.BodyFixed2Ecl`: `IOrientation.GetBodyFixed2Ecl()` (declared on `IOrientation`, **not** on `IFollowable`); `Part.Asmb2VehicleAsmb`; `doubleQuat.{Concatenate,NormalizeOrZero}`. `Celestial` returns `GetCcf2Cce()`, `Vehicle` returns `Body2Cce`; the part composition is the welds engine's anchor transform | L | — |
| `camera/pose/aim_up` (`target`) | — | — | `CameraTargets.UpEcl`: `Celestial.GetRotationAxisCce()` (literally `double3.UnitZ.Transform(GetCcf2Cce())`); the `Vehicle.ComputeBody2Cce` axis convention (**+X fwd, +Y right, −Z up**), read off its rotation-matrix rows. ⚠️ A convention change here **inverts subject-locked shots and the build cannot catch it** | **M** | — |
| `camera/target`, `camera/status`' `follow` | S | — | `CameraTargets.Describe`: `Camera.Following` → `IFollowable`; `Astronomical.Id`. `Following` can also be a `WreckageMarker` or a `VehicleEditingSpace` — neither is addressable, so both report `none` | L | — |
| (restore-target liveness / despawn prune) | — | — | `CameraTargets.IsLive`: `Universe.CurrentSystem.All.UnsafeAsList()` — the same enumeration the sampler and the welds liveness check use. A despawned restore target degrades `Restore` to `Unfollow(changeControl:false)` rather than throwing | L | — |

**Read-back** (`Game/Ksa/Camera/CameraReader.cs`) — published into the game-free `CameraStore.Status`
with one volatile swap, which every `/sim/camera` leaf renders from:

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| every `camera/**` read leaf | S | — | `CameraReader.Sample`: `IGameViewport.{Mode,MapController}` (interface properties since 5402); `MapController.Scope`; `Camera.{Following,TidalLocking,GetFieldOfView,Orthographic}`. ⚠️ `GetFieldOfView()` returns **radians** while `SetFieldOfView(float)` takes **degrees** — converted here, once, at the boundary. `IViewport.Mode` is read directly, never `Program.GetCameraMode()` (which reads the *frame* viewport) | M | — |
| `camera/mode`, `camera/status`' `mode` | S | — | `CameraReader.ModeOf(CameraMode)`: the `CameraMode` enum `{Orbit, Free, Map, IVA, Fixed}`. The `/sim` `CameraModeKind` ordinals match one-for-one but the mapping is **written out, not cast** — an inserted member upstream would otherwise silently re-label every mode on the wire | L | — |

**Focus** (`Game/Ksa/Actuators/CameraActuator.cs`, pre-existing, **rebound** by task C1.4):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `ctl/focus`, `bodies/<id>/focus`, `debug/focus` | T | `camera.focus` | `CameraActuator.Focus`: `Program.MainViewport.{BaseCamera,MapCamera}`; `Camera.SetFollow(IFollowable, tidalLocking:true, changeControl:false, alert:false)`. **Now sets follow on both of the viewport's cameras**, as the game's own follow action does (`KSA/InputEvents.cs:759-760`) — setting only the active one (the old `Program.GetMainCamera()` path) left the **map** view on the previous target until the player re-focused from inside map mode. `alert:false` keeps the "Following X" `TimedAlert` out of the footage | M | Frame |

**Unchanged and still correct:** `KsaCatalog.{ResolveAstronomical,ResolveVehicle}` (their existing
anchors cover the camera's use) and `WeldManager.FindPart` (its existing anchor now also covers the
camera's `part:` resolution). `VesselForceRender`'s two prefixes were touched only to alias the
now-shadowed type name `Camera` — no binding moved.

**The timed scheduler adds no KSA anchor at all.** `/sim/ctl/timed_batch`, `/sim/ctl/schedules/**` and
the seven `schedule.*` actions are entirely game-free (`gatOS.SimFs/Commands/`); `KsaCatalog` routes
the whole family straight to `ScheduleStore.Execute` without touching a game type, and its per-frame
tick (`Mod.TickSchedules`) reads only the frame delta and `Universe.GetElapsedTime()` through the
sampler's already-anchored time read. There is deliberately no row for it — see
[`scope/non-ksa-surface.md`](../scope/non-ksa-surface.md).

## Screen stream (STREAM_PLAN.md)

The one KSA binding for the `/sim/display` screen stream — a render-thread GPU readback, not a
`SnapshotStore` read or a `SimCommand`, so it sits outside the transport-parity machinery (the stream
is media, the controls are plain `DisplaySettings` mutators). Three `[KsaAnchor]` sites — the
transpiler that finds the injection point, the capture itself, and (new at 5348) the UI-coverage-mask
suppression that keeps the captured frame complete.

| Anchor (`Game/Ksa/`) | KSA members | Risk | Notes |
|---|---|---|---|
| `DisplayRenderPatch.Transpiler` | `Program.RenderGame` (Harmony transpiler), `Brutal.VulkanApi.VkDeviceExtensions.End` | M | Injects the capture call just before the frame's final `commandBuffer.End()` (`Program.cs:4130`), where the offscreen `ColorImage` is `ShaderReadOnlyOptimal` and recording is outside any render pass. Matches the single 1-arg `End` extension; degrades to **no injection** (feature dark) if the site moves — never corrupts the method. **Re-verified 2026-08-23 / 5348:** the injection site holds — the cited `Program.cs:4130` is now ~`:4595`; the new tail is `_screenshotCapture.OnRenderGameSwapchainGrab(...); Profiler.Gpu.EndFrame(commandBuffer2); commandBuffer2.End();`, and both transpiler filters reject `EndFrame` (not named `End`, and `Profiler` is in namespace `KSA`), while the new `TagRegion` `using` blocks emit `GpuRegion.Dispose()` rather than an inlined `End`; `codes[callIdx-1]` is still the `ldloc` of `commandBuffer2` and `VkDeviceExtensions.End<T>` has zero diff. ⚠️ Unlike the solver hook, this target is a `"RenderGame"` **string literal** — it is in the silent-failure class. |
| `FrameCapture.MaybeRecord` | `Program.GetRenderer()`, `Program.MainViewport.OffscreenTarget.ColorImage`/`.Extent`/`.Format`, `Renderer.Allocator`/`.MaxFramesInFlight`/`.PhysicalDevice`, `Program.ResourceFrameIndex`, `Allocator.CreateImage`/`CreateBuffer`, `CommandBufferEx.TransitionImages2` + `ImageBarrierInfo.Presets` + `ImageTransition` (`KSA.Rendering`), `CommandBuffer.BlitImage`/`CopyImageToBuffer`, `BufferEx.Map`, `PhysicalDevice.GetFormatProperties` | M | Records the downscale **into the engine's own frame command buffer** (no out-of-band submit, no `WaitIdle` — that crashed the game): `BlitImage` (LINEAR) resamples the full `R16G16B16A16_SFLOAT` scene into a small per-slot `B8G8R8A8_UNORM` scratch (downscale + float→byte clamp in one GPU op — PERF_IMPROVEMENT_PLAN.md P1), then `CopyImageToBuffer` moves only the small image to `HOST_CACHED`-preferred staging; readback is a bulk span hand-off (zero per-pixel CPU work). The offscreen is moved with the engine's **own sync2** `TransitionImages2` + `ImageBarrierInfo.Presets` (`SampledReadVfc`↔`TransferSrc`) so there is no sync1/sync2 mixing on the shared image; restored to `SampledReadVfc` as the engine left it (`Program.cs:4125`). Blit support is format-feature-queried once; a miss falls back to the original full-res copy + CPU nearest-neighbour convert. Deferred readback via the `ResourceFrameIndex` frames-in-flight slot ring (the slot's prior copy is complete when reused — no fence wait). Engine types reached by inference + interface constraints. **Re-verified 2026-08-23 / 5348:** the offscreen colour/barrier path is unchanged — but the capture now additionally depends on the `UiPixelCulling` suppression below for a *complete* frame, because it reads `OffscreenTarget.ColorImage` before the UI composite. |
| `DisplayRenderPatch.UiPixelCullingPrefix` | Harmony **prefix on `GameSettings.UiPixelCulling()`** (`KSA/GameSettings.cs:388`); `KSA/UiCoverageMaskSystem.cs:466`; `KSA/PrePassRenderer.cs` | **H** | **New coupling, born at `2026.8.22.5348`** (rev 5283 added `UiCoverageMaskSystem`). `UiCoverageMask.RecordDepthStamp` — the first thing the opaque pre-pass does — stamps the reverse-Z **near plane** into the pre-pass depth wherever fully-opaque ImGui UI covers the screen; `PrePassRenderer.CopyDepthImageToSrc` copies that depth into `_offscreenTarget`, whose scene pass loads it (`BeginRendering(…, depth VkAttachmentLoadOp.Load)`), so every later `GreaterOrEqual` test under the UI fails via early-Z — and clouds, the light pre-pass and the sunbloom merge early-out on the same tile mask. `FrameCapture` reads the offscreen colour **before** the UI composite, so the streamed frame carried the local player's window chrome punched out as unshaded black: invisible locally, visible only to a remote reader. **Fix:** the prefix returns `false` while the stream is live (`DisplayRenderPatch.IsCapturing`, shared with the capture call), installed on the existing `gatos.display` Harmony instance inside `DisplayRenderPatch.Install` so it unpatches with the transpiler; best-effort — a missing target costs capture fidelity, never the stream. **The getter, not the field:** `GameSettings.Current.Graphics.UiPixelCulling` is the player's saved setting (`[TomlField("uiPixelCulling")]`, defaults **`true`**) and mutating it would corrupt their config and show in the settings UI; the getter has **exactly one caller in the whole game** (`UiCoverageMaskSystem.RecordMaskGeneration`), which re-reads it per frame and zero-clears the tile masks when false, suppressing the stamp *and* every consumer early-out. Patching `ActiveThisFrame` alone would **not** work — consumers sample the tile texture directly, not the flag. KSA exempts only its own screenshot capture (`!Program.Instance.IsScreenshotCaptureActive`) |

## The churn playbook (when a decomp drop lands)

1. Update `thirdparty/ksa`, rebuild with the KSA assemblies present. **Build errors in
   `Game/Ksa/**` are the alarm system.**
2. For each break: re-locate the API, fix the accessor, update its `[KsaAnchor]`
   (`Verified`, `GameVersion`, the member path) and the matching row above.
3. Runtime drift without a compile break: the per-accessor try/catch in `KsaCatalog`/`KsaHealth`
   latches the accessor degraded → it returns `EOPNOTSUPP`, logs once, and surfaces in
   `/sim/status/accessors`. The guest *sees* a failed sensor instead of the mod crashing.
4. Re-run the control-surface checklist in `docs/VALIDATION.md`.

## Paint

| Area | KSA members | Behavior | Risk |
|---|---|---|---|
| Paint: vehicle shader lifecycle | `ShaderModuleUtils.FromFile/FromString`; `ShaderReference.ModPath`; `Program.RendererRebuildNeeded` | Dynamic Harmony, only while `/sim/paint/parts/enabled=1`; GLSL transformed in memory; foreign compiler prefixes rejected | High |
| Paint: per-part instance data | static/dynamic `PartModel*Module.UpdateRenderData`; static/dynamic `PartModel*.AddInstance`; `StateBitFlag` bits 11..31 | scoped Part handoff + 7:7:7 sRGB OR; glass stock | High |
| Paint: EVA material clones | `KittenEva._renderable`; `CharacterAvatar`; protected `MaterialIndices`; `GpuMaterialSystem.CreateObject`; `AssetMap`; `GpuObjectAssetRef.Dispose` | gatOS-owned pooled clones, conditional restore, fixed cap; never mutates stock shared material. **5348 (D5): the MMU slot ordinals swapped.** `Content/Core/CharacterAssets.xml`'s `CharacterMMUAttachment` moved from `Characters/KittenMMU/KSA_Cat_MMU.gltf` to the **skinned** `Characters/KittenMMU/SK_KSA_MMU.glb` (`CharacterAvatar.Attachments.Mmu.MmuMesh` retyped `StaticMeshRenderable` → `AnimatedRenderable`, an `AnimationScrubSampler ArmScrub` added, a `<Transform>` block added), and the two `<Materials>` blocks were **reordered** — the file now carries the comment "Material order follows SK_KSA_MMU.glb: body first, labels second", so `KSA_MMU_Color` is index **0** (was 1) and `KSA_MMU_Texts` is index **1** (was 0). gatOS names EVA paint slots by array ordinal (`mmu`, `mmu.0`, `mmu.1`, …), so a saved rule targeting `mmu` now repaints the MMU **body** instead of the label decals. The `.glb` is not in the repo, so the `MaterialIndices` array **length** could also differ — live check required | High |
| Paint: clutter texture bind | `Program.Instance.BindlessTextures` → `BindlessTextureLibrary.SetTexture`; `TextureLoader.LoadFromMemory`; `TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32)`; `new SimpleVkTexture(Allocator, StagingPool, TextureAsset, CreateOptions)`; `Renderer.Allocator.CreateStagingPool(Renderer.Graphics, 1)`; `TextureReference.ImageView` | one descriptor write per bind and per restore, re-pointing an **existing** slot: no Harmony, no shader transform, no renderer rebuild, **no new bindless slots**. The stock `ImageView` is captured before the first swap (a re-bind keeps the original capture), and our images are never destroyed inline. All public, no reflection; three non-obvious contracts: `TextureAsset.FilePath` must be non-empty or the `SimpleVkTexture` ctor throws, `LoadOptions` forces stb to 4 channels (a 3-channel PNG would decode to the unsupported `R8G8B8_UNorm`), and the decoded `ITexture` is neither `IDisposable` nor finalized so its `Destroy()` must be called | High |
| Paint: clutter texture catalog | `PlanetRenderer.GroundClutterRenderer` → `CelestialsWithGroundClutter` → `Celestial.BodyTemplate.GroundClutterReference.Ecotypes` → `ClutterEcotypeReference.{Name,MaterialReferences}` → `GroundClutterMaterialReference.{DiffuseReference,NormalReference,PBRMap,OpacityMap,ThicknessMap,AlphaMap}` → `TextureReference.{LocalPath,Width,Height,BindlessHandle}` | read-only discovery walk publishing `/sim/paint/textures/clutter` (id, slot, size, mips, `used_by`, ecotypes) on a ~1 s cadence and only once something has been uploaded or bound; both the material ref and the `TextureReference` may need `Get()` resolution, exactly as `ToGpuMaterial` does. Every member is public and the `PlanetRenderer` handle **reuses the existing `FxReflect.Terrain` accessor, so this adds no new reflection site**. **Re-keyed on `LocalPath` at 2026-08-23 / 5348 (C4) — this fixed a PRE-EXISTING bug, identical on 5261:** rows were keyed on `TextureReference.GetRealId()`, which returns `Id` only when `SerializedId.IsReferenceable`, and that is set only when the asset XML carries an `Id=` **attribute**. Not one clutter texture element in `Content/Core/GroundClutter/{Grass,GenericRock,EarthTrees}Assets.xml` has one — they are all `Path=`-only — so every slot fell through to `walk.Anonymous++`, the catalog published **empty** and every `bind` returned `ENOENT`; the feature was completely inert. The `EarthGrassClutterDiffuse`-style ids previously documented live in an inline `<Material Id="EarthGrassClutterMaterial">` block in `Content/Core/Astronomicals.xml` that is **never deserialized** (`ClutterEcotypeReference.MaterialReferences` is `[XmlIgnore]`, repopulated from `ClutterObjects → Lods → MaterialReferences` by `PopulateMaterialReferences()`) — that content layout does not exist in either build. A single `KeyOf(TextureReference) => texture.LocalPath` helper now serves **both** the discovery walk and `Match`/`ResolveStock` so they cannot diverge; `LocalPath` is the XML `Path` attribute (e.g. `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`) — install-independent, unique per asset and space-free, which the space-separated `clutter` listing and `bind` line require. **Not `Id`:** `FileReference.OnDataLoad` assigns `Id = ModPath` when not referenceable, and `ModPath` is an absolute machine path that would differ per install and leak the user's filesystem. User-facing: `texture_id` values are now content-relative paths; bindings are session-only, so there is nothing to migrate. The walk also gained the new `[XmlElement("Alpha")] PbrMaterialReference.AlphaMap` slot (slot name `alpha`, C5) — no stock clutter material authors one yet, so it is normally absent from the listing; this is forward-coverage. ⚠️ Trap recorded, not acted on: `GroundClutterMaterialReference.PopulateShaderMacrosFromFlags` **gained a second overload** in 5348, so any future `AccessTools.Method` on it by name alone would throw `AmbiguousMatchException` | Medium |

Both sites live in `Game/Ksa/Paint/ClutterTextureBridge.cs`; the decode/upload half of the bind row
(`TextureLoader.LoadFromMemory`, `TextureAsset.LoadOptions`, `CreateStagingPool`, the `SimpleVkTexture`
ctor) carries its own High anchor in `Game/Ksa/Paint/UserTextureGpu.cs`, the shared helper stickers
reuse. Their health latches are `paint.clutter_catalog` and `paint.texture_upload`. Seams, caveats and the upgrade
audit: [`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`](../plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md).

### Stickers — `paint.sticker_*` (⚠️ render internals; see the `stickers` block above)

Projected PNG decals on vehicles, terrain and ground clutter (`/sim/paint/stickers`,
[`plans/STICKERS_PLAN.md`](../plans/STICKERS_PLAN.md)). **Fifteen** `[KsaAnchor]` sites, all under
`gatOS.GameMod/Game/Ksa/Paint/Stickers/`; the decode/upload half is the shared
`Game/Ksa/Paint/UserTextureGpu.cs` anchor listed with the clutter rows above, so stickers add **no**
second decode site. The renderer/binder set is the second-highest-churn coupling gatOS has after
`thug_life`, and like it a render-API rename **fails the build** at the anchor site rather than
degrading silently.

| Anchor site | KSA / Brutal members | Behavior | Risk |
|---|---|---|---|
| `StickerRenderPatches.Apply` | dynamic Harmony **postfix on `KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer)`** (`KSA.Rendering/RenderTarget.cs:315`, called unconditionally from `Program.RenderGame` at `KSA/Program.cs:4418` — 5261 line refs; the `Program.cs` call sites moved ~+95..+150 in 5348, the method itself is byte-identical) | the one window where the resolved single-sample scene depth and colour are both current and neither is bound as an attachment. The method **body** is MSAA-gated, but a postfix fires either way, which makes the seam reliable at every MSAA setting. Installed on the `0 → 1` live transition, removed on `1 → 0` and at unload; Harmony id `gatos.stickers`, separate from `thug_life`'s. `__instance == Program.OffscreenTarget` **and** `Program.RenderedViewport == Program.MainViewport` **and** `!Program.EditorFlag` are all required — crew-portrait viewports have their own targets and cameras, stickers are main-viewport-only in v1, and `Program.RenderEditor` (`KSA/Program.cs:4527`) calls `ResolveAttachments` on the same target with the main viewport index (`:4468`), so without the editor bail body-anchored stickers would draw over the VAB | **H** |
| `StickerDecalRenderer.RecordPass` | `Program.{OffscreenTarget,SetViewport,Instance.ResourceFrameIndex,PointClampedSampler,MainViewport}`; `RenderTarget.{DepthImage,ColorImage,Extent}`; `BarrierBatch`; `ImageBarrierInfo.Presets.{DepthSampledReadF,ColorAttachmentReadWrite}`; `GlobalShaderBindings.{DescriptorSet,DynamicOffset}`; `Program.Instance.BindlessTextures.DescriptorSet` | a near-verbatim port of `GridPass.Run` (`KSA/GridPass.cs:427-471`), the engine's own post-resolve overlay. Depth is moved to `DepthSampledReadF` and **left** there, exactly as `GridPass` leaves it — the engine's tracked-state barriers tolerate that for the rest of the frame. Scene depth is **reverse-Z**, so `0` is the far plane and "nothing was drawn". This frame's descriptor-ring slot is safe to rewrite because the engine already waited on that slot's fence (`Program.cs:2123-2138` advances `ResourceFrameIndex` modulo `MaxFramesInFlight`); the depth descriptor is written `DepthReadOnlyOptimal` + the point-clamped sampler, both copied from `GridPass.UpdateDescriptorSet` | **H** |
| `StickerDecalRenderer.BuildPipelineLayout` | `GlobalShaderBindings.DescriptorSetLayout` (`KSA/GlobalShaderBindings.cs:55`); `Program.Instance.BindlessTextures.DescriptorSetLayout` (`RenderCore.Systems/BindlessTextureLibrary.cs:38`) | set 0 is the game-wide Camera/GlobalLighting/Celestial/Vessel UBO block with a **dynamic offset per viewport** (`Content/Core/Shaders/Common/Global.glsl:144`); set 1 is ours (the scene-depth sampler); set 2 is the bindless table, declared `UpdateAfterBind\|PartiallyBound`, which is why our shader may index a slot the game never touches. The set indices are baked into the GLSL (`SET_GLOBAL` defaults to 0, `SET_TEXTURE` is `#define`d to 2), so **this order is load-bearing** | **H** |
| `StickerDecalRenderer.BuildPipeline` | `ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte>, VkShaderStageFlags, CompileOptions?, ReadOnlySpan<byte>)`; `ModLibrary.Get<ShaderReference>("GridFrag").ModPath`; `Program.Instance.ColorFormat`; `Presets.{InputAssembly.TriangleList,Rasterization.Fill.CullFront}`; `RenderingPresets.{ReverseZDepthStencil.NoDepthTest,BlendState.BlendColorAlphaOver}`; `Renderer.{Device,DynamicStateInfo,ViewportState}` | a null `CompileOptions` uses `ShaderModuleUtils`' own defaults (device Vulkan/SPIR-V target + the default include callbacks). `#include` resolves relative to the **directory of the debugName**, so the debug name is a real path next to `Grid.frag` — found through the shipped `GridFrag` asset (`Content/Core/DefaultAssets.xml:367`) rather than hard-coded — and it **must be NUL-terminated**. Modules we compile are **ours** to destroy (unlike `ModLibrary`'s), which happens as soon as the pipeline is created. `Program.Instance.ColorFormat` is the format the main offscreen target is constructed with (`Program.cs:1427`), i.e. `R16G16B16A16_SFLOAT`. `CullFront` leaves the far faces so the box still covers its footprint from **inside**; no depth test at all — occlusion is decided per fragment from the sampled scene depth | **H** |
| `StickerDecalRenderer.BuildGeometry` | `Renderer.{Allocator,Graphics}`; `BufferEx.CreateInfo`; `VkUtils.StageAndUploadToBuffer` | the unit cube (8 corners of `[-0.5, 0.5]³`, 36 indices) uploaded through the identical one-shot staging submit `ThugLifeQuadRenderer.BuildGeometry` does — a private command buffer submitted out of band and waited on. **The known validation item shared with the clutter-texture upload path**; it happens exactly once, when the first sticker goes live | **H** |
| `StickerTextureBinder.Bind` | `Program.GetRenderer()`; `Program.Instance.BindlessTextures` (public field) → `BindlessTextureLibrary.AddTexture(VkImageView)` (`RenderCore.Systems/BindlessTextureLibrary.cs:155`) | **allocates** a new bindless slot per `(name, version)` — the opposite of the clutter bridge, which re-points an existing one. Legal under the table's `UpdateAfterBind\|PartiallyBound` layout: writing a slot while command buffers referencing **other** slots are in flight is the whole point of the flags. The table has 1024 slots (`Program.cs:774`) shared with the game; stickers are capped by `paint_texture_max_files` (32 by default). Sampler slot 0 is linear-clamped with `MaxLod = 1000`, exactly the sampler a mip-mapped clamp-to-edge decal wants, which is why the shader passes `samplerId 0` | **H** |
| `StickerTextureBinder.Release` | `Program.Instance.BindlessTextures` → `BindlessTextureLibrary.FreeTexture(int)` (`RenderCore.Systems/BindlessTextureLibrary.cs:198`) | `FreeTexture` writes the slot back to the library's empty texture/sampler and returns the index to the free list, so a draw already recorded against that slot samples a 1×1 white texel instead of a destroyed image. Destroying the **image** still waits `MaxFramesInFlight + 1` ticks — that is what the shared `UserTextureGpu.RetireQueue` is for | **H** |
| `StickerAnchors.TryComposeBody` | `Celestial.{GetDirCcfFromLatLon,GetTerrainHeightFromDirCcf,GetCcf2Cce,GetCci2Cce,MeanRadius}`; `Vehicle.ComputeEnu2Cce(double3, doubleQuat)`; `Camera.GetPositionEgo(IPosition)` | geodetic anchor → ego, recomposed every frame so it rides the planet's spin. `GetTerrainHeightFromDirCcf` returns **metres above `MeanRadius`** and `0` for a body with no heightmap. `ComputeEnu2Cce` builds its quaternion from a matrix whose **rows** are east/north/up, so under the row-vector convention `UnitX/UnitY/UnitZ` transform to east/north/up; it returns null **on the spin axis**, where ENU is undefined. The ego position is composed exactly like KSA's own terrain debug overlay (`Vehicle.cs:4511-4523`) — body ego position + the body-fixed offset rotated into ecliptic axes, **never an absolute ecliptic point** | M |
| `StickerAnchors.TryComposeVessel` | `Vehicle.GetMatrixAsmb2Ego(Camera)` (`KSA/Vehicle.cs:1202`); `Part.MatrixAsmb2Ego(in double4x4)` (`KSA/Part.cs:1041`) | part-local anchor → ego. `Part.MatrixAsmb2Ego` is `CreateScale(Scale) * CreateFromQuaternion(Asmb2ParentAsmb) * CreateTranslation(PositionParentAsmb) * MatrixParentAsmb2Ego` — it **includes the part's own scale and walks the whole sub-part parent chain**, which is what makes a sub-part instance id a valid anchor. Row-vector convention throughout (`v * M`), so the decal matrix is composed `S * R * T * partMat` and read left to right | M |
| `StickerPicker.TryPick` | `Program.GetMainCamera()`; `Camera.{ScreenToEgoRay(float2),FramebufferSize}`; `Cursor.InputRay` | both rays are in **ego** space (origin at the camera, ecliptic axes) and `Ray`'s constructor normalizes `Direction`. `ScreenToEgoRay` takes framebuffer **pixels, not NDC**, and `Cursor.InputRay` is refreshed each frame — it is the previous frame's ray if the cursor has not moved, which is exactly what the player last saw. The camera aim is the default because it works headless and `/sim/camera` can point it | M |
| `StickerPicker.TryPickVehicle` | `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.{BoundingSphereRadiusBody,Parts.Parts,GetMatrixAsmb2Ego(Camera)}`; `Part.{RayCastEgo(in double4x4, Ray, out …),InstanceId}`; `Camera.GetPositionEgo(IPosition)` | the identical sweep KSA's own flight-mode hover picking runs (`Vehicle.cs:2745-2773`): broad-phase against the part's bounding sphere scaled by `ScaleTotal`, then `Ray.RaycastWatertight` over the view mesh's de-indexed `double3[]` triangle soup, so the hit is on the **art surface**. `minDistance` is the ray parameter in ego metres. Bepu raycasts are deliberately **not** used — KSA never does, and its colliders are coarse primitives | M |
| `StickerPicker.TryPickTerrain` | `Camera.{NearbyCelestial,GetPositionEgo}`; `Celestial.{GetCce2Ccf,GetCcf2Cce,GetCci2Cce,MeanRadius,GetTerrainHeightFromDirCcf,GetLatitudeFromCcf,GetLongitudeFromCcf}`; `Vehicle.ComputeEnu2Cce` | 64-step march + 24 bisections, the shape `KSA/TerrainImpactFinder.cs:64` uses. `accurate:false` is 4 bilinear texel taps (the physics hot path); the final sample uses `accurate:true`, adding bicubic filtering and the CPU procedural-modifier chain. `GetLatitudeFromCcf`/`GetLongitudeFromCcf` are **static** and return **degrees**, normalizing internally. Ego and CCE axes are both ecliptic axes, so a ray **direction** converts to CCF with `GetCce2Ccf` alone — no translation involved | M |
| `StickerManager.ResolveAnchor` | `Universe.CurrentSystem.Get(string)` → `Astronomical`/`Vehicle`/`Celestial`; `Vehicle.Parts.Parts`; `Part.{SubParts,InstanceId}` | the same id lookup `/sim/camera` and the game's own follow/control actions use; returns **null for a despawned target rather than throwing**, which is what makes a sticker dormant instead of deleted. Sub-parts are searched too because `Part.RayCastEgo` anchors to a **sub-part**, so a sprayed sticker names a sub-part's `InstanceId` | **L** |
| `StickerManager.EnsureGpu` | `Program.GetRenderer()` (`KSA/Program.cs:525`) | lazy GPU init on the `0 → 1` live transition; the renderer is live from `OnFullyLoaded` onwards, well before any command can be drained | M |
| `StickerManager.WaitIdle` | `Program.GetRenderer().GraphicsAndCompute.WaitIdle()` (`Core/Renderer.cs:53`) | the same queue drain the display-capture teardown uses (`Game/Mod.Game.cs:792-821`). KSA has **no deferred-destroy helper at all**, so this is the only way to know that no recorded frame can still reference the pipeline or the images | M |

Health latches: `paint.sticker_texture` (decode/upload/bindless) and `paint.sticker_renderer`
(pipeline/patch/draw). Shader-asset and GLSL-layout dependencies are catalogued in
[`../scope/ksa-assets-and-versions.md`](../scope/ksa-assets-and-versions.md); the patch lifecycle,
barriers and teardown order in
[`../scope/ksa-runtime-coupling.md`](../scope/ksa-runtime-coupling.md#stickers-patch). Seams,
caveats and the upgrade audit: [`plans/STICKERS_PLAN.md`](../plans/STICKERS_PLAN.md) and
[`plans/PAINT_ASBUILT.md`](../plans/PAINT_ASBUILT.md).
