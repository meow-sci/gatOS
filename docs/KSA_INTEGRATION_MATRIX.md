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

**Verified:** **2026-07-16 against `2026.7.6.4939`** (full solution build green, 0 warnings, forced
non-incremental; full decomp + Content diff via `git diff 7cf5c0a..2423a02` between the 4892 and 4939
drops inside the assemblies checkout — the 4939 changelog is gapless, `fromRevision` 4892 = the prior
baseline). **Clean pass: no bound member, reflection accessor, or Harmony hook target changed** — no
code change required. Behavior notes (the fuel-line/tank-transfer/propellant-use system — additive on
`Tank`, changes *when* engines see fuel; the rev 4914 control-module lockout is UI-only — the module
methods gatOS binds stay ungated; animating parts now update colliders + force off-rails; rev 4915
removes the old service-module parts, save-breaking upstream) are catalogued in the
[read-surface 4939 findings](../scope/ksa-read-surface.md#4939-findings) /
[write-surface 4939 findings](../scope/ksa-write-surface.md#4939-findings). Rows showing an earlier
per-member `Verified` date bind to members **unchanged** in 4939 (their compatibility is confirmed by
the green build + the diff). Prior passes: 2026-07-14 against `2026.7.5.4892` (clean; rev 4884
combustion→Reactions notes), 2026-07-03 against `2026.7.3.4826` (clean; post-decouple control-state
inheritance notes), 2026-06-27 against `2026.6.9.4750` (the G1–G4 fix-pass).
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
/ a `/sim` node / an action to the command table once — every transport gets it. See CLAUDE.md
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
| `time/{ut,warp}` | S | `Universe.GetElapsedSimTime().Seconds()`, `Universe.SimulationSpeed` | Low |
| `vessels/by-id/<id>/{id,name,situation,parent}` | S | `Vehicle.Id/.Situation`, `Vehicle.Parent.Id` | Low |
| `…/position/{cci,lat,lon}` | S | `Vehicle.GetPositionCci()`; `IParentBody.GetLlaFromCcf` | Low |
| `…/velocity/{orbital,surface,inertial}` | S | `Vehicle.OrbitalSpeed/.GetSurfaceSpeed()/.GetInertialSpeed()` | Low |
| `…/attitude/{quat,rates}` | S | `Vehicle.GetBody2Cci()`, `Vehicle.BodyRates` | Low |
| `…/altitude/{barometric,radar}` | S | `Vehicle.GetBarometricAltitude()/.GetRadarAltitude()` | Low |
| `…/mass/{total,dry,propellant}` | S | `Vehicle.TotalMass/.InertMass/.PropellantMass` | Low |
| `…/orbit/{apoapsis,periapsis,ecc,inc,sma,period}` | S | `Vehicle.Orbit` elements (radii→altitudes; inc rad→deg) | Low |
| `…/battery/charge` | S | `Vehicle.Parts.Batteries.GetState(b).Charge`, `b.MaximumCapacity` | Low |
| `…/engines/<n>/{active,vac_thrust,isp}` | S | `vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData` | Medium |
| `…/tanks/<resource>/{amount,capacity}` | S | `vehicle.Parts.Modules.Get<Tank>().Moles`; `Parts.Moles.GetState(mole).Mass` | Low |
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
| `…/navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | S | `Vehicle.NavBallData` (`AttitudeAngles` int3 deg) | M |
| `…/environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | S | `Vehicle.PhysicsEnvironment`; `PhysicalAtmosphereReference.GetDynamicPressure`; `AccelerationBody`/`AngularAccelerationBody` | L |
| `…/orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | S | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | L |
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
| `…/ctl/attitude_mode` | St | token | `FlightComputer.AttitudeMode`/`AttitudeTrackTarget` (`manual` → Manual; else Auto+track) | M | **Solver** |
| `…/ctl/attitude_frame` | St | token | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | M | **Solver** |
| `…/ctl/attitude_target` | St | `x y z w` | `FlightComputer.AttitudeTarget = {Target2Cci,RatesCci}` (+Custom track) | M | **Solver** |
| `…/ctl/burn` | St | `ut dvx dvy dvz` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` | M | **Solver** |
| `…/engines/<n>/min_throttle` | St | `0..1` | `EngineController.MinimumThrottle` | M | Frame |
| `…/rcs/<n>/active` | St | `0`/`1` | `ThrusterController.SetIsActive` | M | Frame |
| `…/lights/<n>/on` | St | `0`/`1` | `PowerConsumer.LightIsActive` | M | Frame |
| `…/lights/<n>/brightness` | St | number | `Template.Intensity.Value` (per-instance clone) | H | Frame |
| `…/lights/<n>/color` | St | `r g b` | `Template.ColorRgb.{R,G,B}`+`OnDataLoad` (per-instance clone) | H | Frame |
| `…/lights/<n>/outer_angle` | St | number (deg) | `Template.OuterAngle.Value` (radians, per-instance clone); write clamped to `Light.CreateSpotLight`'s `[1E-05, 1.5697963]` rad, and lowers `InnerAngle` to ≤ outer (else CreateSpotLight swaps them) | H | Frame |
| `…/lights/<n>/inner_angle` | St | number (deg) | `Template.InnerAngle.Value` (radians, per-instance clone); write clamped to `[0, OuterAngle]` | H | Frame |
| `…/decouplers/<n>/fire` | T | `1` | `Decoupler.SetIsActive` (re-fire → EBUSY) | M | Frame |
| `…/docking/<n>/undock` | T | `1` | `InputEvents.VehicleDockingInputData{Undock=true}` → `DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)` (not docked → EBUSY) | M | Frame |
| `…/ctl/focus` | T | `1` | `Program.GetMainCamera().SetFollow(vehicle, tidalLocking:true, changeControl:false)` — moves the view only | M | Frame |
| `bodies/<id>/focus` | T | `1` | same `camera.focus` action on a celestial (`CurrentSystem.Get(id)` → `Astronomical`); view-only, exempt from the authority gate | M | Frame |

A STATE control file's **read** returns the current setpoint, so the vessel-level ones need a reader
that samples it back. These are populated in `VesselReader.BuildFull` (anchor `SampleFlightComputer` +
`GetManualThrottle`): `ctl/throttle` ← `Vehicle.GetManualThrottle()`, `ctl/rcs` ← any
`ThrusterController.IsActive`, `ctl/translate` ← `Vehicle.GetThrusterFlags()` decoded to body-axis
signs (anchor `TranslateActuator.Read`), `ctl/attitude_mode` ← `FlightComputer.AttitudeMode`/`AttitudeTrackTarget`
(`manual` when Manual, else the track-target name), `ctl/attitude_frame` ← `FlightComputer.AttitudeFrame`.
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
onto its anchor in `OnAfterUi` (`Mod.DriveWelds`, game thread, after `JobSystems.VehicleSolvers.Wait()`)
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
| `Tick` | `Channel.TryIsPlaying/TryGetPosition/TryStop`; `Sound.TryRelease`; `Universe.GetElapsedSimTime().Seconds()` (event stamps) | L | Per-frame (`Mod.DriveAudio`, `OnBeforeUi` after the drain): prunes finished channels (a recycled FMOD handle answers non-Ok — that *is* the completion signal), enforces `end=`, releases evicted sounds, publishes `/sim/audio/status`, emits `audio.finished`. |

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
