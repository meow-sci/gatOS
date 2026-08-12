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
`gatos.serial` virtio-serial bridge) and MQTT transports and the TypeScript SDK (G6) are all built;
they add **no** KSA coupling — every transport speaks the same `SnapshotStore` (reads) and
`ICommandSink`/`SimCommand` (writes), so this matrix (the KSA-touching surface) is unaffected by them.

**Verified:** **2026-08-05 against `2026.8.5.5168`** (full solution build green, 0 warnings; full test
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

Every row below is reachable over **all** transports — there is one read surface and one write
surface, projected per transport, never re-implemented:

| Surface | 9p `/sim` | HTTP `/v1` | MQTT `gatos/` |
|---|---|---|---|
| Data (granular + atomic) | scalar files + `telemetry` doc | `snapshot`/`system`/`bodies[/{id}]`/`vessels/{id}[/telemetry]` | `snapshot`/`system`/`bodies`/`time`/`status` + `vessels/<id>/{telemetry,snapshot}` |
| Field-level (per leaf) | the file tree itself | `GET /v1/fs/<path>` (+ `?stream=1` SSE) | retained `gatos/sim/<path>` (one topic/leaf) |
| Streaming | `stream` / `events` / `time/alarm` | `vessels/{id}/stream` / `events` (SSE) / `time/wait` | retained `vessels/<id>/*` / `events` topics |
| Control + debug | `ctl/…`, per-module files, `debug/…` | `POST /v1/command`, `POST /v1/fs/<path>` | publish `gatos/command`, `gatos/sim/<path>/set` |

Aggregate reads project the one `SimSnapshot` through `gatOS.SimFs/SimJson` (HTTP + MQTT) or
`Formats` (9p); the field-level mirror **walks the one `/sim` VFS tree** (`VfsScan`) the 9p server
serves; writes funnel the one `SimCommand` through the single `ICommandSink`. Add a read to `SimJson`
/ a `/sim` node / an action to the command table once — every transport gets it. See AGENTS.md
"THE transport-parity rule".

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
| `…/engines/<n>/{active,vac_thrust,isp}` | S | `vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData` | Medium |
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
| `…/navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | S | `Vehicle.NavBallData` (`AttitudeAngles` int3 deg; `DeltaV` — renamed from `DeltaVInVacuum` at rev 5114, and **both `deltav` and `twr` changed meaning**: active-sequence Δv, atmosphere-corrected TWR) | M |
| `…/environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | S | `Vehicle.PhysicsEnvironment`; `PhysicalAtmosphereReference.GetDynamicPressure`; `AccelerationBody`/`AngularAccelerationBody` | L |
| `…/orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | S | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` — **`UniverseTime` since rev 5211**, whose "no such event" sentinel (`EndOfTime`) is *finite* (~1.7e29 s), so all three go through `IsSaturated()` guards to keep the `0` contract | L |
| `…/encounters` | S | `Vehicle.Patch.Encounters` (`Encounter.{Body.Id,GameTime,ClosestDistance}`), NDJSON | M |
| `…/engines/<n>/{throttle,propellant,min_throttle}` | S/St | `EngineControllerState.{CommandThrottle,IsPropellantAvailable}`; `EngineController.MinimumThrottle` | M |
| `…/tanks/<r>/fraction` | S | `Mole.FilledFraction(state)` | L |
| `…/battery/{fraction,capacity}` | S | `Battery.MaximumCapacity` (sum); charge/capacity | L |
| `…/power/{produced,consumed}` | S | Σ `SolarPanelState.Produced`+`GeneratorState.Produced`; Σ `PowerConsumerState.Consumed` (instantaneous **W** — 4750/rev 4681 `Joules`→`Watts`) | M |
| `…/solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle,state,current,goal}` | S/St | `SolarPanelState.*` (`Produced` = instantaneous **W**, 4750 `Joules`→`Watts`); `SolarTrackerState.CurrentAngle` (1:1 by index); deploy via linked `KeyframeAnimationModule` | M |
| `…/generators/<n>/{active,produced}` | S | `GeneratorState.{Active,Produced}` (`Produced` = instantaneous **W**, 4750 `Joules`→`Watts`) | M |
| `…/lights/<n>/{on,brightness,color,inner_angle,outer_angle}` | S/St | `PowerConsumer.LightIsActive`; `LightModule.Template.{Intensity,ColorRgb,InnerAngle,OuterAngle}` (inner/outer_angle = the cone half-angles, `rad→deg`) | M (template H) |
| `…/lights/<n>/{goal,current,state}` | S/St | actuate animation via linked `KeyframeAnimationModule` (`Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>()`, same scan `SolarPanel.OnPartCreated` uses); only when the light part has one | M |
| `…/docking/<n>/{docked,docked_to,pushoff_impulse}` | S | `DockingPort.Docked`/`DockedToPart.Id`/`PushoffImpulse` (N·s) | M |
| `…/decouplers/<n>/{fired,fire}` | S/T | `Decoupler.IsActive` | M |

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
| `…/ctl/stage` | T | `1` | `Parts.SequenceList.ActivateNextSequence` + `UpdateAfterPartTreeModification` | M | Frame |
| `…/ctl/rcs` | St | `0`/`1` | `ThrusterController.SetIsActive` over all controllers | M | Frame |
| `…/ctl/translate` | St | `x y z` (signs) | `Vehicle._manualControlInputs.ThrusterCommandFlags` (reflection — same struct as throttle; translate bits only, rotation bits preserved). `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`Direct` mode → `SelectJetsToFire`); sign→flag mapping verified against the `KittenBackPackSubPart` nozzle geometry (+x=`TranslateForward`, +y=`Right`, +z=`Down`). Latches until rewritten. Added 2026-07-04 | H | Frame |
| `…/ctl/rotate` | St | `x y z` (signs) | `Vehicle._manualControlInputs.ThrusterCommandFlags` (reflection — same struct as throttle/translate; rotation bits only, translation bits preserved). `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`Direct` mode → `SelectJetsToFire`; `ComputeTvcControl` decodes the same bits for gimbals); sign→flag mapping is KSA's own torque decode (+x=`RollRight`, +y=`PitchUp`, +z=`YawRight`). **Auto attitude strips rotation bits — full authority needs `attitude_mode=manual`.** Latches until rewritten. Added 2026-07-22 (W1, AGC_PLAN §7.4) | H | Frame |
| `…/ctl/attitude_mode` | St | token | `FlightComputer.AttitudeMode`/`AttitudeTrackTarget` (`manual` → Manual; else Auto+track) | M | **Solver** |
| `…/ctl/attitude_frame` | St | token | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | M | **Solver** |
| `…/ctl/attitude_target` | St | `x y z w` | `FlightComputer.AttitudeTarget = {Target2Cci,RatesCci}` (+Custom track) | M | **Solver** |
| `…/ctl/burn` | St | `ut dvx dvy dvz` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` | M | **Solver** |
| `…/ctl/rcs_mode` | St | token | `FlightComputer.RCSMode` (`FlightComputerRCSMode.{Enabled,Disabled}`) — the in-game **R** keybind. Since 5168/rev 5143 this is a hard cut-off for **manual** RCS too: `ComputeRcsControl` zeroes `ThrusterCommandFlags` (`:471`) so `ctl/translate`+`ctl/rotate` go dead, and `UpdateRcsParams` zeroes the RCS torque authority (`:884`). **Solver** phase because `CopyFrom` copies it. Added 2026-08-05 | M | **Solver** |
| `…/engines/<n>/min_throttle` | St | `0..1` | `EngineController.MinimumThrottle` | M | Frame |
| `…/rcs/<n>/active` | St | `0`/`1` | `ThrusterController.SetIsActive` | M | Frame |
| `…/lights/<n>/on` | St | `0`/`1` | `PowerConsumer.LightIsActive` | M | Frame |
| `…/lights/<n>/brightness` | St | number | `Template.Intensity.Value` (per-instance clone) | H | Frame |
| `…/lights/<n>/color` | St | `r g b` | `Template.ColorRgb.{R,G,B}`+`OnDataLoad` (per-instance clone) | H | Frame |
| `…/lights/<n>/outer_angle` | St | number (deg) | `Template.OuterAngle.Value` (radians, per-instance clone); write clamped to `Light.CreateSpotLight`'s `[1E-05, 1.5697963]` rad, and lowers `InnerAngle` to ≤ outer (else CreateSpotLight swaps them) | H | Frame |
| `…/lights/<n>/inner_angle` | St | number (deg) | `Template.InnerAngle.Value` (radians, per-instance clone); write clamped to `[0, OuterAngle]` | H | Frame |
| `…/decouplers/<n>/fire` | T | `1` | `Decoupler.SetIsActive` (re-fire → EBUSY; **disabled → EOPNOTSUPP** — since 5168/rev 5132 `SetIsActive` is gated on the new `Decoupler.IsEnabled`, so an unguarded call was a silent no-op) | M | Frame |
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
| `vessels/by-id/<id>/scale` | St | value > 0 | `ScaleActuator.Set`: recursive `Part.Scale = (f,f,f)` over `Vehicle.Parts.Parts`/`Part.SubParts` (public `double3` setter; invalidates cached transform matrices), one-shot — KSA resets it on vessel rebuild; KittenEva avatar via reflected `_renderable._characterAvatar.Core.Scale = f*0.01f` (0.01 == 1:1) | H (reflection + `GetType().Name` gate) | Frame |
| `vessels/by-id/<id>/always_render` | St | `0`/`1` | `VesselForceRender.Set`: registry op; installs **two Harmony prefixes on its own `Harmony("gatos.always_render")` instance only while ≥ 1 vessel is marked** — `Vehicle.GetWorldMatrix(Camera)` + `Vehicle.UpdateRenderData(Viewport,int)` — reproducing the stock bodies minus the `GetObjectDiameterPixelsAsDouble < 1.0` sub-pixel cull (`Camera.GetPositionEgo`, `Vehicle.Body2Cce`, `Vehicle.GetMatrixAsmb2Ego`, `PartTree.UpdateRenderData`, `Vehicle.IsEditedVehicle`) | M (dynamic Harmony; `UpdateRenderData` is virtual — KittenEva's override renders via its own path and is **not** affected) | Frame |

Read-backs are sampled in `VesselReader.SampleCore` (always on — not gated by the detail pass):
`scale` ← a representative `Part.Scale.X` (best-effort, `1.0` fallback; anchor `ScaleActuator.Read`);
`always_render` ← the gatOS-owned `VesselForceRender` registry (no KSA read). `always_render` marks
key on the vessel **id** (they survive scene rebuilds; `scale` does not — KSA resets `Part.Scale` on
rebuild) and are pruned when the vessel despawns (`VesselForceRender.Prune`, riding the sampler's
vehicle enumeration; pruning the last mark also removes the patches).

`/sim/debug/` (G-D2; gated by `[control] debug_namespace`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/vessels/<id>/teleport` | T | `px py pz vx vy vz` | `Orbit.CreateFromStateCci`+`Vehicle.Teleport`+`UpdatePerFrameData` | H | Frame |
| `debug/vessels/<id>/impulse` | St | `x y z [cci\|body] [ns\|dv]` | `Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,TotalMass,Parent}` + `Orbit.CreateFromStateCci`+`Vehicle.Teleport`+`UpdatePerFrameData` — the velocity-bump variant of the teleport pattern; Δv = J/`TotalMass` (the `Vehicle.Split` separation-impulse math); `body` rotates via `double3.Transform(v, GetBody2Cci())` | H | Frame |
| `debug/vessels/<id>/refill_fuel` | T | `1` | `Vehicle.RefillConsumables()` | M | **Solver** |
| `debug/vessels/<id>/refill_battery` | T | `1` | `Battery.Refill(ref state)` via `GetModuleAndAllMutableStatesForInitialization` | M | **Solver** |
| `debug/vessels/<id>/docking/<n>/pushoff_impulse` | St | N·s (≥0) | `DockingPort.PushoffImpulse` (live float; stock 7000 N·s from XML; 4750/rev 4683 rename, was `PushoffForce` N) | M | Frame |
| `debug/time/warp` | St | factor | `Universe.SetSimulationSpeed(double, alert:false)` (public) | M | Frame |
| `debug/focus` | St | vehicle/body id | `camera.focus` by id (view-only; same action as `ctl/focus`) | M | Frame |
| `debug/control_vessel` | St | vehicle id | `Program.GetMainCamera().SetFollow(vehicle)` + `Program.ControlledVehicle = vehicle` (focus **and** control) | M | Frame |
| `debug/always_render_iva` | St | `0`/`1` | `IvaActuator`→`IvaForceRender.SetEnabled`: flips `PartModelModule.Template.Internal=false` over `PartModel.Instances`; installs/removes its own `gatos.iva` Harmony patches (`PartModel..ctor`/`AddInstance` postfixes) only while on (vessel-agnostic) | M (dynamic Harmony) | Frame |
| `debug/vessels/<id>/weld` | St | `<target> <piid> x y z pitch yaw roll lock` | `WeldManager.Create`→`WeldEngine.UpdateWeld`: `Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,BodyRates,CenterOfMassAsmb,Parent,Orbit,Teleport,UpdatePerFrameData}`, `Orbit.CreateFromStateCci`, `IParentBody.GetCci2Cce`, `Universe.GetJobSimStep(double).NextTime`, `Program.GetPlayerDeltaTime`, `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` (subpart-aware). `<piid>` resolves over `Vehicle.Parts.Parts` **and** each part's `Part.SubParts` (`WeldManager.FindPart`), so a top-level part or a subpart anchors — an animated subpart tracks its live pose | H | Frame |
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
check remains pending (`docs/VALIDATION.md`).

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
| `IvaPhysicsManager.Update` | `JobSystems.VehicleSolver.Wait()`; `Universe.{CurrentSystem.All.UnsafeAsList,SimulationSpeed}`; `Program.{Editor,MainViewport}`; `Viewport.Mode`; `CameraMode.IVA`; `Vehicle.{Id,AccelerationBody,AngularAccelerationBody,BodyRates,CenterOfMassAsmb,Parts.Count}` | L | The per-frame driver (`Mod.DriveIvaPhysics`, `OnAfterUi` — the sixth game-thread work site), after the solver workers so the kinematics are settled. **`AccelerationBody` is a true accelerometer in every flight situation** (zero in `Freefall`; the `GM/r²` normal force when `Landed`/`Floating`; thrust+drag with gravity excluded when `Maneuvering`), which is why one formula covers pad, coast, burn and landing. Parks under warp, in the editor (`Program.Editor != null` disables `Part` transform caching), and outside the IVA camera unless `run_outside_iva`. |
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
`2026.7.10.5056`**; the feature is code-complete with the in-game pass pending (`docs/VALIDATION.md`).

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
| `debug/plumetrail/render/*` | St | per-field (see SPEC §3.7) | `TrailActuator.TryRead`/`TryWrite`: `VolumetricTrailRenderer.{MaxDistance,VoxelDepthFirstSliceThickness,MinStepSize,StepSizeDistanceScale,ErosionMaxDepth,ErosionEdgeSharpness,SelfShadowStepCount,LightBrightness,SkyAmbientBrightness,DebugTrailColor}` (public fields, `float`/`float4`) | M | Frame |
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
| `debug/terrain/bodies/<id>/**` | St | per-field (see SPEC §3.7) | `TerrainActuator.Write`: `Celestial.BodyTemplate.HeightReference.{Minimum,Maximum}` + `TerrainReference.BiomeMaterials.{BlendStrength.Value,DetailFadeInStart,DetailFadeInEnd}` (construct-new `DistanceReference`) **and** the `PlanetUbo`/`MeshUbo` structs at `(NumCelestials*frame + slot)*Stride` over the mapped memory, then the frame-in-flight mirror copy (`PlanetRenderer.cs:2388-2398`) | **H** | Frame |
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
One guarded Harmony prefix/postfix targets public `Viewport.OnFrame(double)` and binds only
`Program.MainViewport` by identity (see
[`scope/ksa-runtime-coupling.md#camera-driver`](../scope/ksa-runtime-coupling.md#camera-driver)). The
prefix applies against current-frame target state immediately before KSA builds matrices; the postfix
publishes the final clamped transform. Ownership parks `CameraMode.Fixed` and unfollows, and
`FixedController.OnFrame` wraps its entire body in `if (following != null)`. **IVA and Map ownership
contexts are NOT implemented and are not implementable without a Harmony patch** — evidence in
[`scope/ksa-runtime-coupling.md#camera-mode-contexts`](../scope/ksa-runtime-coupling.md#camera-mode-contexts).
Original anchors verified **2026-08-06**, viewport hook verified **2026-08-09**, against
`2026.8.5.5168`; live recheck pending
(`docs/VALIDATION.md`).

**Ownership + the live game camera** (`Game/Ksa/Camera/CameraDirector.cs`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `camera/enabled` (`1`) | St | `camera.enabled` | `CameraDirector.Take`: `Program.MainViewport`; `Viewport.{Mode,GetCamera,SetCameraMode,BaseCamera}`; `Camera.{PositionEcl,LocalPosition,LocalRotation,NoRotation,Following,TidalLocking,GetFieldOfView,Orthographic,Unfollow}`. `Viewport.Mode` is assigned **directly** so `FixedController.OnSwitchOn`'s `TimedAlert("Fixed Camera")` never draws; `Unfollow(changeControl:false)` (the default `true` would null `Program.ControlledVehicle`) | M | Frame |
| `camera/enabled` (`0`), `camera/release` | St / T | `camera.enabled` / `camera.release` | `CameraDirector.Restore`: `Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,SetOrthographic}`; `Viewport.{Mode,SetCameraMode}`; `Universe.SetSimulationSpeed` (conditional). `SetFollow` **first** (it teleports), then `NoRotation`→`LocalPosition`→`LocalRotation`, projection, mode **last**; restoring *into* Map goes through `SetCameraMode` so `MapController.OnSwitchOn` re-establishes `NoRotation` + the map's control state | M | Frame |
| (per-frame pose apply) | — | — | `CameraDirector.Apply`: `Camera.{PositionEcl,LocalRotation,LookAtRotation,SetFieldOfView,SetOrthographic,SetOrthoHalfHeight}`. `LocalRotation` (not `WorldRotation`) is what `Camera.OnFrame` builds the view matrix from; `SetFieldOfView` takes **degrees** and does **not** clamp (which is what puts fisheye/telephoto in reach) and rebuilds+inverts the projection on every call, so it is written only on change. `ortho_height` has **no public getter in 5168** — nothing to capture, nothing to restore | M | Frame |
| (track `time` channel, C4) | — | — | `CameraDirector.ApplyTimeScale`: `Universe.SetSimulationSpeed(double, alert:false)` (`:1998`), `Universe.GetSimulationSpeed()` (`:2021`), `Universe.IsAutoWarpActive` (`:96`). `alert:false` is load-bearing (the default draws a speed alert *in the footage*). Speed is captured **lazily**, the first frame the channel is driven, and restored only if captured. Neither public `SetSimulationSpeed` overload checks `IsAutoWarpActive`, so a driven `time` channel **fights** an active auto-warp — deliberately unguarded, documented | M | Frame |
| (release blend) | — | — | `CameraDirector.RestorePositionEcl`: the `Camera.PositionCce` composition (`LocalPosition` ⇄ `IFollowable.GetBodyFixed2Ecl` unless `NoRotation`); `IPosition.GetPositionEcl()`. Reproduced rather than called: the camera being blended is already unfollowed, so its own `PositionCce` would not use the captured target | M | Frame |
| `camera/mode` | St | `camera.mode` | `CameraDirector.SetMode`: `Program.MainViewport`; `Viewport.SetCameraMode(CameraMode)`. Refused (`EOPNOTSUPP`) while gatOS owns the camera. Note the side effect: `SetCameraMode` calls `Program.ControlledVehicle?.ClearHeldPlayerInput()`, dropping latched `ctl/translate`+`ctl/rotate` flags (SPEC §3.4.19) | M | Frame |
| `camera/follow` | St | `camera.follow` | `CameraDirector.SetFollow`: `Program.MainViewport.{BaseCamera,MapCamera}`; `Camera.{SetFollow,Unfollow}` — **both** cameras, like the game's own follow. Keeps `SetFollow`'s `target + 2.5×MeanRadius×forward` teleport (that is what "go look at this" means); preserves the current tidal flag; a `part:` reference is `EOPNOTSUPP` (the game follows a whole `IFollowable`). Refused while owned | M | Frame |
| `camera/tidal` | St | `camera.tidal` | `CameraDirector.SetTidal`: `Camera.{Following,TidalLocking,SetFollow,PositionEcl}`. `TidalLocking` is get-only and `SetFollow` is its only writer, so the flag change re-issues `SetFollow` and then **re-asserts the captured `PositionEcl`** to undo its unconditional teleport. Refused while owned | M | Frame |
| `camera/map/scope` | St | `camera.map_scope` | `CameraDirector.SetMapScope`: `Program.MainViewport.MapController`; `MapController.Scope` (`:33`, a plain public `double`). **Not ownership-gated** — it configures the game's own map camera. Three inherited game behaviours: `MapController.OnFrame` clamps it **up** to `Camera.Following.MeanRadius` every map frame; `OnSwitchOn`→`SetDefaults()` recomputes it wholesale after a focus change; it has no visible effect outside `map` mode | M | Frame |

**Frames + targets** (`Game/Ksa/Camera/CameraFrames.cs`, `CameraTargets.cs`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `camera/pose/{frame,aim_frame}`, `pose/position`, `pose/orbit/*` | St | `camera.{frame,aim_frame,position,orbit_*}` | `CameraFrames.TryFrame2Ecl`: `Vehicle.{GetEnu2Cce,GetLvlh2Cce,Body2Cce,ComputeEnu2Cce,ComputeLvlh2Cce,GetPositionCce,GetVelocityCce}`; `Celestial.{GetCci2Cce,GetCcf2Cce,GetDirCcfFromLatLon,MeanRadius,GetPositionCce,GetVelocityCce}`. `GetEnu2Cce`/`GetLvlh2Cce` are **nullable** and `GetEnu2Cce` dereferences `Orbit.Parent` unguarded — both guarded here; the `Compute…` public statics are reused so a celestial anchor gets the game's own ENU/LVLH construction. Nothing ever silently falls back to another frame: unresolvable ⇒ `EOPNOTSUPP` at write time, and per frame the director **holds the last good pose** and logs the reason once | M | Frame |
| `camera/pose/geo` | St | `camera.geo` | `CameraFrames.GeoToEcl`: `Celestial.{GetDirCcfFromLatLon,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius,GetPositionEclFromCce}`. gatOS **calls** `GetDirCcfFromLatLon` rather than restating its trigonometry (CCF +Z = north pole, +X = prime meridian), so a convention change is inherited, not diverged from. `GetTerrainHeightFromDirCce` returns **metres** (`0` with no heightmap) ⇒ altitude is **above terrain**, degrading to above-mean-sphere; the direction is explicitly normalized because `GetSurfacePositionEclFromDirCce` does not | M | Frame |
| (geodetic read-back) | S | — | `CameraFrames.TryEclToGeo`: `Celestial.{GetPositionCceFromEcl,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius}` — the exact inverse (`lat = asin(ccf.Z)`, `lon = atan2(ccf.Y, ccf.X)`), the same pair KSA's own `GetLatitudeFromCce`/`GetLongitudeFromCcf` use. This is what lets the reader publish **both** position spellings | M | — |
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
| every `camera/**` read leaf | S | — | `CameraReader.Sample`: `Viewport.{Mode,MapController}` (public fields); `MapController.Scope`; `Camera.{Following,TidalLocking,GetFieldOfView,Orthographic}`. ⚠️ `GetFieldOfView()` returns **radians** while `SetFieldOfView(float)` takes **degrees** — converted here, once, at the boundary. `Viewport.Mode` is read directly, never `Program.GetCameraMode()` (which reads the *frame* viewport) | M | — |
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
is media, the controls are plain `DisplaySettings` mutators). Confined to a single `[KsaAnchor]`.

| Anchor (`Game/Ksa/`) | KSA members | Risk | Notes |
|---|---|---|---|
| `DisplayRenderPatch.Transpiler` | `Program.RenderGame` (Harmony transpiler), `Brutal.VulkanApi.VkDeviceExtensions.End` | M | Injects the capture call just before the frame's final `commandBuffer.End()` (`Program.cs:4130`), where the offscreen `ColorImage` is `ShaderReadOnlyOptimal` and recording is outside any render pass. Matches the single 1-arg `End` extension; degrades to **no injection** (feature dark) if the site moves — never corrupts the method. |
| `FrameCapture.MaybeRecord` | `Program.GetRenderer()`, `Program.MainViewport.OffscreenTarget.ColorImage`/`.Extent`/`.Format`, `Renderer.Allocator`/`.MaxFramesInFlight`/`.PhysicalDevice`, `Program.ResourceFrameIndex`, `Allocator.CreateImage`/`CreateBuffer`, `CommandBufferEx.TransitionImages2` + `ImageBarrierInfo.Presets` + `ImageTransition` (`KSA.Rendering`), `CommandBuffer.BlitImage`/`CopyImageToBuffer`, `BufferEx.Map`, `PhysicalDevice.GetFormatProperties` | M | Records the downscale **into the engine's own frame command buffer** (no out-of-band submit, no `WaitIdle` — that crashed the game): `BlitImage` (LINEAR) resamples the full `R16G16B16A16_SFLOAT` scene into a small per-slot `B8G8R8A8_UNORM` scratch (downscale + float→byte clamp in one GPU op — PERF_IMPROVEMENT_PLAN.md P1), then `CopyImageToBuffer` moves only the small image to `HOST_CACHED`-preferred staging; readback is a bulk span hand-off (zero per-pixel CPU work). The offscreen is moved with the engine's **own sync2** `TransitionImages2` + `ImageBarrierInfo.Presets` (`SampledReadVfc`↔`TransferSrc`) so there is no sync1/sync2 mixing on the shared image; restored to `SampledReadVfc` as the engine left it (`Program.cs:4125`). Blit support is format-feature-queried once; a miss falls back to the original full-res copy + CPU nearest-neighbour convert. Deferred readback via the `ResourceFrameIndex` frames-in-flight slot ring (the slot's prior copy is complete when reused — no fence wait). Engine types reached by inference + interface constraints. |

## The churn playbook (when a decomp drop lands)

1. Update `thirdparty/ksa`, rebuild with the KSA assemblies present. **Build errors in
   `Game/Ksa/**` are the alarm system.**
2. For each break: re-locate the API, fix the accessor, update its `[KsaAnchor]`
   (`Verified`, `GameVersion`, the member path) and the matching row above.
3. Runtime drift without a compile break: the per-accessor try/catch in `KsaCatalog`/`KsaHealth`
   latches the accessor degraded → it returns `EOPNOTSUPP`, logs once, and surfaces in
   `/sim/status/accessors`. The guest *sees* a failed sensor instead of the mod crashing.
4. Re-run the control-surface checklist in `docs/VALIDATION.md`.
