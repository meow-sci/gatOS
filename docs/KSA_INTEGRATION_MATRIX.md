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

**Verified:** 2026-06-12, against `thirdparty/ksa` (`VersionInfo.Current` at build time).

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
| `…/ctl/lights` | St | `0`/`1` | `Vehicle.LightsOn` + per-`PowerConsumer.LightIsActive` | Low | Frame |
| `…/engines/<n>/active` | St | `0`/`1` | `EngineController.SetIsActive(vehicle, bool)` | Low | Frame |
| `…/animations/<n>/goal` | St | `0..1` | `KeyframeAnimationModule.TimeGoal = f × Shared.Duration` | Low | Frame |
| `…/solar/<n>/goal` | St | `0..1` | same as `animations/<n>/goal` (solar-filtered view, same ordinal) | Low | Frame |

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
| `…/position/ecl`, `…/velocity/cci` | S | `Vehicle.GetPositionEcl()`, `Vehicle.GetVelocityCci()` (vectors) | L |
| `…/navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | S | `Vehicle.NavBallData` (`AttitudeAngles` int3 deg) | M |
| `…/environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | S | `Vehicle.PhysicsEnvironment`; `PhysicalAtmosphereReference.GetDynamicPressure`; `AccelerationBody`/`AngularAccelerationBody` | L |
| `…/orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | S | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | L |
| `…/encounters` | S | `Vehicle.Patch.Encounters` (`Encounter.{Body.Id,GameTime,ClosestDistance}`), NDJSON | M |
| `…/engines/<n>/{throttle,propellant,min_throttle}` | S/St | `EngineControllerState.{CommandThrottle,IsPropellantAvailable}`; `EngineController.MinimumThrottle` | M |
| `…/tanks/<r>/fraction` | S | `Mole.FilledFraction(state)` | L |
| `…/battery/{fraction,capacity}` | S | `Battery.MaximumCapacity` (sum); charge/capacity | L |
| `…/power/{produced,consumed}` | S | Σ `SolarPanelState.Produced`+`GeneratorState.Produced`; Σ `PowerConsumerState.Consumed` (per-sample energy proxy) | M |
| `…/solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle,state,current,goal}` | S/St | `SolarPanelState.*`; `SolarTrackerState.CurrentAngle` (1:1 by index); deploy via linked `KeyframeAnimationModule` | M |
| `…/generators/<n>/{active,produced}` | S | `GeneratorState.{Active,Produced}` | M |
| `…/lights/<n>/{on,brightness,color}` | S/St | `PowerConsumer.LightIsActive`; `LightModule.Template.{Intensity,ColorRgb}` | M (template H) |
| `…/docking/<n>/{docked,docked_to}` | S | `DockingPort.Docked`/`DockedToPart.Id` | M |
| `…/decouplers/<n>/{fired,fire}` | S/T | `Decoupler.IsActive` | M |

New `/sim/events` types (snapshot diff in `EventDiffer`): `engine-state`, `flameout`, `docked`,
`undocked`, `decoupled`, `animation-complete`, `battery-depleted`, `battery-charged`.

## Control surface — G4 expansion

Anchors in `Game/Ksa/Actuators/**`; routed by `KsaCatalog`. Frame phase unless noted.

| Path | A | Write | KSA anchor (actuator) | Risk | Phase |
|---|---|---|---|---|---|
| `…/ctl/throttle` | St | `0..1` | `Vehicle._manualControlInputs.EngineThrottle` (reflection — no public setter) | H | Frame |
| `…/ctl/stage` | T | `1` | `Parts.SequenceList.ActivateNextSequence` + `UpdateAfterPartTreeModification` | M | Frame |
| `…/ctl/rcs` | St | `0`/`1` | `ThrusterController.SetIsActive` over all controllers | M | Frame |
| `…/ctl/attitude_mode` | St | token | `FlightComputer.AttitudeMode`/`AttitudeTrackTarget` (`manual` → Manual; else Auto+track) | M | **Solver** |
| `…/ctl/attitude_frame` | St | token | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | M | **Solver** |
| `…/ctl/attitude_target` | St | `x y z w` | `FlightComputer.AttitudeTarget = {Target2Cci,RatesCci}` (+Custom track) | M | **Solver** |
| `…/ctl/burn` | St | `ut dvx dvy dvz` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` | M | **Solver** |
| `…/engines/<n>/min_throttle` | St | `0..1` | `EngineController.MinimumThrottle` | M | Frame |
| `…/rcs/<n>/active` | St | `0`/`1` | `ThrusterController.SetIsActive` | M | Frame |
| `…/lights/<n>/on` | St | `0`/`1` | `PowerConsumer.LightIsActive` | M | Frame |
| `…/lights/<n>/brightness` | St | number | `Template.Intensity.Value` (per-instance clone) | H | Frame |
| `…/lights/<n>/color` | St | `r g b` | `Template.ColorRgb.{R,G,B}`+`OnDataLoad` (per-instance clone) | H | Frame |
| `…/decouplers/<n>/fire` | T | `1` | `Decoupler.SetIsActive` (re-fire → EBUSY) | M | Frame |

`/sim/debug/` (G-D2; gated by `[control] debug_namespace`):

| Path | A | Write | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `debug/vessels/<id>/teleport` | T | `px py pz vx vy vz` | `Orbit.CreateFromStateCci`+`Vehicle.Teleport`+`UpdatePerFrameData` | H | Frame |
| `debug/vessels/<id>/refill_fuel` | T | `1` | `Vehicle.RefillConsumables()` | M | **Solver** |
| `debug/vessels/<id>/refill_battery` | T | `1` | `Battery.Refill(ref state)` via `GetModuleAndAllMutableStatesForInitialization` | M | **Solver** |
| `debug/time/warp` | St | factor | `Universe.SetSimulationSpeed(double, alert:false)` (public) | M | Frame |
| `debug/switch_vessel` | St | vessel id | `Program.ControlledVehicle` (public static field) | M | Frame |

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

## The churn playbook (when a decomp drop lands)

1. Update `thirdparty/ksa`, rebuild with the KSA assemblies present. **Build errors in
   `Game/Ksa/**` are the alarm system.**
2. For each break: re-locate the API, fix the accessor, update its `[KsaAnchor]`
   (`Verified`, `GameVersion`, the member path) and the matching row above.
3. Runtime drift without a compile break: the per-accessor try/catch in `KsaCatalog`/`KsaHealth`
   latches the accessor degraded → it returns `EOPNOTSUPP`, logs once, and surfaces in
   `/sim/status/accessors`. The guest *sees* a failed sensor instead of the mod crashing.
4. Re-run the control-surface checklist in `docs/VALIDATION.md`.
