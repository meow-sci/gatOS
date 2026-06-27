# Scope — KSA Write Surface (controls + debug)

> Every control gatOS performs against KSA. Each row: the `/sim` control path, the command **action
> key**, the actuator method, the KSA member it binds to, the threading **phase** (Frame vs Solver), the
> decomp file, churn risk, and **4750 status** (✅ · ⚠️ · ❌).
>
> Source of truth = `[KsaAnchor]` in `gatOS.GameMod/Game/Ksa/Actuators/**` and the dispatch table in
> `KsaCatalog.cs`. Action keys + arg shapes + errno = [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md).
> Anchor mirror = [`docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md).

## How a write flows (and why KSA can't leak)
A transport (9p `/sim/ctl/*` file write, `POST /v1/command`, `gatos/command`, serial SCPI) builds an
immutable `SimCommand` (action key + `Ordinal`/`Value`/`Values`/`Token`) and enqueues it on
`CommandQueue`. The game thread drains it through `KsaCatalog.Execute` → the matching actuator. The
actuator is the only code that sees a `Vehicle`. Every execution is wrapped in `KsaCatalog`'s try/catch →
on throw the action is latched degraded in `KsaHealth` (`EOPNOTSUPP`, surfaced in `/sim/status/accessors`).
Phase is **derived from the action key** by `SimCommand.Phase` (`SolverActions` set) — never passed at a
call site, so every transport routes identically.

| errno | when |
|---|---|
| `EINVAL` | unparseable / out-of-range argument |
| `ENOENT` | vessel / module ordinal vanished |
| `EACCES` | control globally disabled (`control_enabled=false`) or authority gate (`control_all_vessels=false`) |
| `EBUSY` | action can't fire now (e.g. undock a non-docked port, re-fire a decoupler) |
| `EIO` | KSA threw (latches the accessor) |
| `ETIMEDOUT` | game thread didn't drain within `command_timeout_ms` |
| `EOPNOTSUPP` | accessor latched degraded (reflection field missing, prior fault) |

**Authority gate (G-D1):** with `control_all_vessels=false` (default), only `Program.ControlledVehicle`
is commandable (`KsaCatalog.cs:53`); the `debug.*` namespace is exempt (its own opt-in via
`debug_namespace`). `camera.focus` is exempt (view-only).

---

## ⚠️ Cross-cutting 4750 concern: `Vehicle.IsControllable` (rev 4699)

4750 adds `Vehicle.IsControllable => _overrideIsControllable || Parts.Controls.NumModules > 0`
(`KSA/Vehicle.cs:526`) — a vehicle **without a Control Module** can no longer be controlled by the
player **or the Flight Computer** (control + FC paths now gate through `ControlsLockout`). The asset XML
adds `<Control />` to the capsule (`Content/Core/CoreCommandAGameData.xml`); kittens have one inherently.

**Impact on every write below:** none break the build, but control commands sent to an *uncontrollable*
vessel may **silently no-op** — most visibly the Solver-phase flight-computer setpoints (locked out). The
default controlled vessel always has a Control Module (the game wouldn't let you control it otherwise),
so normal single-vessel operation is unaffected; the gap is (a) gatOS has no way to *report*
controllability, and (b) commands to an uncontrollable vessel return `Ok` while doing nothing. Proposed
remediation (expose `controllable` read + optionally gate control actuators with `EACCES`) is in
[`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md).

---

## Engines & vessel-level (Frame phase)

`Game/Ksa/Actuators/EngineActuator.cs`, `LightActuator.cs`, `AnimationActuator.cs`.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|---|
| `ctl/ignite` | `vessel.ignite` | `EngineActuator.Ignite` | `Vehicle.SetEnum(VehicleEngine.MainIgnite)` | `KSA/Vehicle.cs` | Medium | ✅¹ |
| `ctl/shutdown` | `vessel.shutdown` | `EngineActuator.Shutdown` | `Vehicle.SetEnum(VehicleEngine.MainShutdown)` | `KSA/Vehicle.cs` | Medium | ✅¹ |
| `ctl/engine` | `vessel.engine` | `EngineActuator.SetEngineOn` | ignite/shutdown by flag | `KSA/Vehicle.cs` | Medium | ✅¹ |
| `engines/<n>/active` | `engine.active` | `EngineActuator.SetActive` | `EngineController.SetIsActive(vehicle,bool)` | `KSA/EngineController.cs` | Low | ✅ |
| `engines/<n>/min_throttle` | `engine.min_throttle` | `EngineActuator.SetMinThrottle` | `EngineController.MinimumThrottle` (float) | `KSA/EngineController.cs` | Medium | ✅ |
| `ctl/lights` | `vessel.lights` | `LightActuator.SetMaster` | `Vehicle.LightsOn`; `PowerConsumer.{LightSwitch,LightIsActive}` | `KSA/Vehicle.cs`, `KSA/LightModule.cs` | Low | ✅ |
| `animations/<n>/goal`, `solar/<n>/goal`, `lights/<n>/goal` | `animation.goal` | `AnimationActuator.SetGoal` | `KeyframeAnimationModule.TimeGoal = f × Shared.Duration` | `KSA/KeyframeAnimationModule.cs` | Low | ✅ |

¹ `SetEnum`/`MainIgnite` compile clean; subject to the `IsControllable` gate above at runtime.

## Vessel control surface (G4)

`ThrottleActuator.cs`, `StagingActuator.cs`, `RcsActuator.cs`, `FlightComputerActuator.cs`.

| `/sim` path | action key | phase | actuator | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|---|---|
| `ctl/throttle` | `vessel.throttle` | Frame | `ThrottleActuator.Set` | **reflection** `Vehicle._manualControlInputs.EngineThrottle` (no public setter; `GetManualThrottle()` reads it) | `KSA/Vehicle.cs` (`:232,824`) | **High** | ✅² |
| `ctl/stage` | `vessel.stage` | Frame | `StagingActuator.Stage` | `Vehicle.Parts.SequenceList.ActivateNextSequence(vehicle)` + `Vehicle.UpdateAfterPartTreeModification()` | `KSA/SequenceList.cs`, `KSA/Vehicle.cs` | Medium | ✅³ |
| `ctl/rcs` | `vessel.rcs` | Frame | `RcsActuator.SetMaster` | `ThrusterController.SetIsActive(vehicle,bool)` over all | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/attitude_mode` | `vessel.attitude_mode` | **Solver** | `FlightComputerActuator.SetAttitudeMode` | `FlightComputer.{AttitudeMode,AttitudeTrackTarget}`; `FlightComputerAttitudeMode`/`...TrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/attitude_frame` | `vessel.attitude_frame` | **Solver** | `…SetAttitudeFrame` | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/attitude_target` | `vessel.attitude_target` | **Solver** | `…SetAttitudeTarget` | `FlightComputer.{CustomAttitudeTarget,AttitudeFrame,AttitudeTrackTarget=Custom}`; `VehicleReferenceFrameEx.{GetEclBody2Cci,QuaternionToEulerAngles}` | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/burn` | `vessel.burn` | **Solver** | `…SetBurn` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` | `KSA/BurnTarget.cs` | Medium | ✅² |

² Compiles; **`IsControllable`-gated** at runtime (Solver-phase FC setpoints are the most affected). ³
`SequenceList.ActivateNextSequence` is *Sequences* (activation), distinct from "Resource Groups" (the
rev 4732 rename of "Stages"); compiled clean — no change.

> **Why Solver phase?** KSA's async vehicle solver snapshots the whole `FlightComputer` at prepare and
> restores it at apply (`FlightComputer.CopyFrom`). A frame-phase write to a FC setpoint lands *outside*
> that capture and is overwritten by the in-flight solve (the mode flashes on, then snaps back). The
> Solver actions drain in a Harmony `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`
> (`Mod.DrainSolverCommands`). See [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#threading-phases).

## Per-module controls (G4, Frame phase)

`LightActuator.cs`, `DecouplerActuator.cs`, `DockingActuator.cs`, `RcsActuator.cs`.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|---|
| `rcs/<n>/active` | `rcs.active` | `RcsActuator.SetActive` | `ThrusterController.SetIsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `lights/<n>/on` | `light.on` | `LightActuator.SetOn` | `LightModule.Parent.FullPart.LightSwitch.LightIsActive` | `KSA/LightModule.cs` | Medium | ✅ |
| `lights/<n>/brightness` | `light.brightness` | `LightActuator.SetBrightness` | `LightModule.Template.Intensity.Value` (per-instance **clone**) | `KSA/LightModule.cs`, `KSA/FloatReference.cs` | **High** | ✅ |
| `lights/<n>/color` | `light.color` | `LightActuator.SetColor` | `LightModule.Template.ColorRgb.{R,G,B}` + `OnDataLoad` (clone) | `KSA/LightModule.cs`, `KSA/ColorRgbReference.cs` | **High** | ✅ |
| `lights/<n>/outer_angle` | `light.outer_angle` | `LightActuator.SetOuterAngle` | `LightModule.Template.OuterAngle.Value` (deg→rad, clamp `[1e-5, 1.5697963]`) | `KSA/LightModule.cs`, `KSA/Light.cs` (`CreateSpotLight`) | **High** | ✅ |
| `lights/<n>/inner_angle` | `light.inner_angle` | `LightActuator.SetInnerAngle` | `LightModule.Template.InnerAngle.Value` (clamp `[0, OuterAngle]`) | `KSA/LightModule.cs` | **High** | ✅ |
| `decouplers/<n>/fire` | `decoupler.fire` | `DecouplerActuator.Fire` | `Decoupler.{IsActive,SetIsActive}` (re-fire → `EBUSY`) | `KSA/Decoupler.cs` | Medium | ✅⁴ |
| `docking/<n>/undock` | `docking.undock` | `DockingActuator.Undock` | `InputEvents.VehicleDockingInputBuffer.Add(VehicleDockingInputData{Undock=true})` → `DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)` | `KSA/DockingPort.cs`, `KSA/InputEvents.cs` | Medium | ⚠️⁵ |

⁴ `Decoupler.SetIsActive` unchanged; rev 4715 ("decoupler releasing the wrong connector") is a runtime
fix, no API change. ⁵ `Undock` itself compiles (it enqueues an `InputEvents` record, never calls `Split`
directly), but the separation it triggers now applies an **impulse** (`Vehicle.Split(Connector, double
splitImpulse, …)`); see the docking section below — the anchor's documented `Member` string references
the old `PushoffForce` and must be re-anchored.

## Camera focus (Frame phase, authority-exempt)

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|---|
| `ctl/focus`, `bodies/<id>/focus` | `camera.focus` | `CameraActuator.Focus` | `Program.GetMainCamera().SetFollow(Astronomical, tidalLocking:true, changeControl:false)` | `KSA/Program.cs`, `KSA/Camera.cs` | Medium | ✅ |

---

## `/sim/debug` cheat surface {#debug}

`Game/Ksa/Actuators/DebugActuator.cs` + `DockingActuator.SetPushoffForce`. Gated by `[control]
debug_namespace`. Authority-exempt (own opt-in).

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|---|
| `debug/time/warp` | `debug.warp` | Frame | `Universe.SetSimulationSpeed(double, alert:false)` | `KSA/Universe.cs` | Medium | ✅ |
| `debug/control_vessel` | `debug.control_vessel` | Frame | `Program.GetMainCamera().SetFollow(…)`; `Program.ControlledVehicle = vehicle` | `KSA/Program.cs` | Medium | ✅⁶ |
| `debug/focus` | `camera.focus` | Frame | (same as `ctl/focus`) | `KSA/Program.cs` | Medium | ✅ |
| `debug/vessels/<id>/teleport` | `debug.teleport` | Frame | `Orbit.CreateFromStateCci` + `Vehicle.Teleport` + `Vehicle.UpdatePerFrameData` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | **High** | ✅ |
| `debug/vessels/<id>/refill_fuel` | `debug.refill_fuel` | **Solver** | `Vehicle.RefillConsumables()` | `KSA/Vehicle.cs` (`:2300`) | Medium | ✅ |
| `debug/vessels/<id>/refill_battery` | `debug.refill_battery` | **Solver** | `Battery.Refill(ref state)` via `Batteries.GetModuleAndAllMutableStatesForInitialization` | `KSA/Battery.cs` (`:59`) | Medium | ✅ |
| `debug/vessels/<id>/docking/<n>/pushoff_force` | `debug.docking_pushoff` | Frame | `DockingPort.PushoffForce =` (live float) | `KSA/DockingPort.cs` | Medium | ❌ **break** |

⁶ `Program.ControlledVehicle` setter may itself reject an uncontrollable target in 4750 (see the
`IsControllable` concern) — verify in a live flight.

---

## ❌ Docking pushoff (COMPILE BREAK) {#docking}

`DockingActuator.cs:58` (`SetPushoffForce`) does `ports[ordinal].PushoffForce = (float)newtons;` and the
read at `VesselReader.cs:539` does `port.PushoffForce`. **4750 (rev 4683) renamed the member to
`PushoffImpulse` and changed its meaning from force (N) to impulse (N·s).** Evidence:

- `KSA/DockingPort.cs`: `public required float PushoffImpulse;`, `public required float
  LatchingKineticEnergy;` (was `PushoffForce` / `LatchingImpulse`), and `Undock(...) =>
  oldVehicle.Split(Connector, PushoffImpulse);`.
- `KSA/Vehicle.cs:1013`: `public Vehicle? Split(Part.Connector splitConnector, double splitImpulse,
  string? splitVehicleId = null)`.
- Asset `Content/Core/CoreCouplingAGameData.xml`: `<PushoffImpulse Ns="7000"/>`,
  `<LatchingKineticEnergy J="50"/>` (numerically still 7000, but now **N·s**).

**Required changes** (full detail in [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md)):
the two `PushoffForce` references → `PushoffImpulse`; the snapshot field `DockingSnapshot.PushoffForceN`
→ impulse semantics; the `/sim` leaf `pushoff_force` (read + `debug` write) → recommend rename to
`pushoff_impulse`, unit **N → N·s**; the `debug.docking_pushoff` validation message and SPEC rows
(SPEC lines ~336, ~415, ~501); the two `DockingActuator` anchors + the `VesselReader.SampleDocking`
anchor; the matrix docking rows.

---

## Items confirmed *not* affected by 4750
- **Staging** (`SequenceList.ActivateNextSequence`) — the "Stages → Resource Groups" rename (rev 4732)
  is about resource groups, not activation sequences; gatOS uses Sequences; compiled clean.
- **Brutal numerics** (rev 4729 package bump) — all `double3`/`doubleQuat`/`float3` usages in the
  actuators compiled clean (only `DockingPort.PushoffForce` failed).
- **Lights / animations / decouplers / RCS / engines / flight computer / teleport / refills** — all
  members compiled clean and none appear in the changelog with an API-affecting change.
