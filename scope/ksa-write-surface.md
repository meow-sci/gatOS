# Scope — KSA Write Surface (controls + debug)

> Every control gatOS performs against KSA. Each row: the `/sim` control path, the command **action
> key**, the actuator method, the KSA member it binds to, the threading **phase** (Frame vs Solver), the
> decomp file, churn risk, and **4939 status** (✅ · ⚠️ · ❌).
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

## ✅ Cross-cutting 4750 concern: `Vehicle.IsControllable` (rev 4699) — G3 RESOLVED (2026-06-27) {#iscontrollable}

4750 adds `Vehicle.IsControllable => _overrideIsControllable || Parts.Controls.NumModules > 0`
(`KSA/Vehicle.cs:526`) — a vehicle **without a Control Module** can no longer be controlled by the
player **or the Flight Computer** (control + FC paths now gate through `ControlsLockout`). The asset XML
adds `<Control />` to the capsule (`Content/Core/CoreCommandAGameData.xml`); kittens have one inherently.

**Impact on every write below:** none break the build, but control commands sent to an *uncontrollable*
vessel **silently no-op** — most visibly the Solver-phase flight-computer setpoints (locked out). The
default controlled vessel always has a Control Module (the game wouldn't let you control it otherwise),
so normal single-vessel operation is unaffected.

**G3 resolution (applied 2026-06-27).** (a) gatOS now **reports** controllability — a `controllable` read
(`Vehicle.IsControllable`, anchored in `VesselReader.ReadControllable`) surfaces at
`vessels/<id>/controllable`, in the compact `telemetry` doc, and over every transport (read surface →
[`ksa-read-surface.md#controllable`](ksa-read-surface.md)). (b) **Gating decision: Option A — gatOS does
*not* add its own gate.** It relies on KSA's own `ControlsLockout` to drop the command, and on the new
`controllable` read so guests/autopilots pre-check. Rationale: `IsControllable` already gates inside KSA;
adding a redundant gatOS `EACCES` risks blocking commands in edge states the lockout would actually allow,
and that can't be confirmed without a live flight. Option B (return `EACCES` for the flight-control subset
when `!IsControllable`) remains available if a live flight shows the silent-`Ok` UX is a problem — it would
be a localized change in `KsaCatalog.Execute`. `debug.control_vessel` may itself refuse an uncontrollable
target in 4750 (verify live). Full record: [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md).

---

## Engines & vessel-level (Frame phase)

`Game/Ksa/Actuators/EngineActuator.cs`, `LightActuator.cs`, `AnimationActuator.cs`.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
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

`ThrottleActuator.cs`, `StagingActuator.cs`, `RcsActuator.cs`, `TranslateActuator.cs`,
`RotateActuator.cs`, `FlightComputerActuator.cs`.

| `/sim` path | action key | phase | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|---|
| `ctl/throttle` | `vessel.throttle` | Frame | `ThrottleActuator.Set` | **reflection** `Vehicle._manualControlInputs.EngineThrottle` (no public setter; `GetManualThrottle()` reads it) | `KSA/Vehicle.cs` (`:232,824`) | **High** | ✅² |
| `ctl/stage` | `vessel.stage` | Frame | `StagingActuator.Stage` | `Vehicle.Parts.SequenceList.ActivateNextSequence(vehicle)` + `Vehicle.UpdateAfterPartTreeModification()` | `KSA/SequenceList.cs`, `KSA/Vehicle.cs` | Medium | ✅³ |
| `ctl/rcs` | `vessel.rcs` | Frame | `RcsActuator.SetMaster` | `ThrusterController.SetIsActive(vehicle,bool)` over all | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/translate` | `vessel.translate` | Frame | `TranslateActuator.SetTranslation` | **reflection** `Vehicle._manualControlInputs.ThrusterCommandFlags` (same struct as throttle; translate bits replaced, rotation bits preserved) + `ThrusterMapFlags`; read-back `Vehicle.GetThrusterFlags()`. `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`ManualThrustMode.Direct` → `SelectJetsToFire`; Auto attitude strips only rotation bits, so translation composes with tracking). Sign→flag mapping (+x=`TranslateForward`, +y=`Right`, +z=`Down`) verified against the `KittenBackPackSubPart` nozzle geometry in `Content/Core/PartGameData.xml` | `KSA/Vehicle.cs`, `KSA/ThrusterMapFlags.cs`, `KSA/FlightComputer.cs` (`:454-519,1029`) | **High** | ✅⁴ |
| `ctl/rotate` | `vessel.rotate` | Frame | `RotateActuator.SetRotation` | **reflection** `Vehicle._manualControlInputs.ThrusterCommandFlags` (same struct as throttle/translate; rotation bits replaced, translation bits preserved) + `ThrusterMapFlags`; read-back `Vehicle.GetThrusterFlags()`. `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`ManualThrustMode.Direct` → `SelectJetsToFire`; `ComputeTvcControl` decodes the same bits for gimbals). **Auto attitude strips the rotation bits** (`WithNoRotation()`) — full authority needs `attitude_mode=manual`, the inverse of translate's compose note. Sign→flag mapping is KSA's own torque decode (`ComputeTvcControl:559-585`): +x=`RollRight`, +y=`PitchUp`, +z=`YawRight` | `KSA/Vehicle.cs`, `KSA/ThrusterMapFlags.cs`, `KSA/FlightComputer.cs` (`:457-524,559-585,1020`) | **High** | ✅ (added at 4939) |
| `ctl/attitude_mode` | `vessel.attitude_mode` | **Solver** | `FlightComputerActuator.SetAttitudeMode` | `FlightComputer.{AttitudeMode,AttitudeTrackTarget}`; `FlightComputerAttitudeMode`/`...TrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/attitude_frame` | `vessel.attitude_frame` | **Solver** | `…SetAttitudeFrame` | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/attitude_target` | `vessel.attitude_target` | **Solver** | `…SetAttitudeTarget` | `FlightComputer.{CustomAttitudeTarget,AttitudeFrame,AttitudeTrackTarget=Custom}`; `VehicleReferenceFrameEx.{GetEclBody2Cci,QuaternionToEulerAngles}` | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/burn` | `vessel.burn` | **Solver** | `…SetBurn` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` | `KSA/BurnTarget.cs` | Medium | ✅² |

² Compiles; **`IsControllable`-gated** at runtime (Solver-phase FC setpoints are the most affected). ³
`SequenceList.ActivateNextSequence` is *Sequences* (activation), distinct from "Resource Groups" (the
rev 4732 rename of "Stages"); compiled clean — no change. Re-verified against 4826: the big
`SequenceList.cs`/`StageList.cs` rework (+796/+472) is editor drag/drop UI + a private
`_symmetryGroups`→`_sequenceGroups` rename — `ActivateNextSequence` and `Part.ActivateInStage` are
**byte-identical**; `Vehicle.UpdateAfterPartTreeModification` gained only an additive cosmetic
`UpdateDistantGlintCurves()` call. Re-verified against 4892: the KSA `Staging` *window class* is gone
(`Staging.cs` deleted; the window is now `ResourceGroups`) — irrelevant to gatOS, which binds
`SequenceList`; `ActivateNextSequence(Vehicle)` keeps its signature and body, now ending in a batched
`RemoveSpentSequences()` (rev 4873 perf); sequences are double-buffered for the UI (rev 4880) —
activation semantics unchanged. Re-verified against 4939: the +1137-line `SequenceList.cs` churn is the
in-flight sequence-window redesign (GaugeCanvas dressing, group expand/collapse, fuel bars) —
`ActivateNextSequence(Vehicle)` (`SequenceList.cs:127`) is untouched; note rev 4914 gates the
**staging key** behind `ControlsLockout` (control-module required) but only in `Vehicle`'s key-input
handler — the `ActivateNextSequence` call gatOS binds carries no such gate (see the
[4939 findings](#4939-findings)).

⁴ Added 2026-07-04 (born on 4826): the struct-reflection pattern is the proven throttle anchor; the
flags path (`ComputeRcsControl`/`SelectJetsToFire`, `WithCanceledOpposingCommands`, the
`WithNoRotation` strip under Auto attitude) read directly from the 4826 decomp. The command
**latches** until rewritten (`0 0 0` stops). Only fires thrusters whose `ControlMap` carries
translation axes (e.g. the EVA kitten backpack's six translation jets); in-game pass pending (see
`docs/VALIDATION.md`).

> **Why Solver phase?** KSA's async vehicle solver snapshots the whole `FlightComputer` at prepare and
> restores it at apply (`FlightComputer.CopyFrom`). A frame-phase write to a FC setpoint lands *outside*
> that capture and is overwritten by the in-flight solve (the mode flashes on, then snaps back). The
> Solver actions drain in a Harmony `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`
> (`Mod.DrainSolverCommands`). See [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#threading-phases).

## Per-module controls (G4, Frame phase)

`LightActuator.cs`, `DecouplerActuator.cs`, `DockingActuator.cs`, `RcsActuator.cs`.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `rcs/<n>/active` | `rcs.active` | `RcsActuator.SetActive` | `ThrusterController.SetIsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `lights/<n>/on` | `light.on` | `LightActuator.SetOn` | `LightModule.Parent.FullPart.LightSwitch.LightIsActive` | `KSA/LightModule.cs` | Medium | ✅ |
| `lights/<n>/brightness` | `light.brightness` | `LightActuator.SetBrightness` | `LightModule.Template.Intensity.Value` (per-instance **clone**) | `KSA/LightModule.cs`, `KSA/FloatReference.cs` | **High** | ✅ |
| `lights/<n>/color` | `light.color` | `LightActuator.SetColor` | `LightModule.Template.ColorRgb.{R,G,B}` + `OnDataLoad` (clone) | `KSA/LightModule.cs`, `KSA/ColorRgbReference.cs` | **High** | ✅ |
| `lights/<n>/outer_angle` | `light.outer_angle` | `LightActuator.SetOuterAngle` | `LightModule.Template.OuterAngle.Value` (deg→rad, clamp `[1e-5, 1.5697963]`) | `KSA/LightModule.cs`, `KSA/Light.cs` (`CreateSpotLight`) | **High** | ✅ |
| `lights/<n>/inner_angle` | `light.inner_angle` | `LightActuator.SetInnerAngle` | `LightModule.Template.InnerAngle.Value` (clamp `[0, OuterAngle]`) | `KSA/LightModule.cs` | **High** | ✅ |
| `decouplers/<n>/fire` | `decoupler.fire` | `DecouplerActuator.Fire` | `Decoupler.{IsActive,SetIsActive}` (re-fire → `EBUSY`) | `KSA/Decoupler.cs` | Medium | ✅⁴ |
| `docking/<n>/undock` | `docking.undock` | `DockingActuator.Undock` | `InputEvents.VehicleDockingInputBuffer.Add(VehicleDockingInputData{Undock=true})` → `DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)` | `KSA/DockingPort.cs`, `KSA/InputEvents.cs` | Medium | ✅⁵ |

⁴ `Decoupler.SetIsActive` unchanged; rev 4715 ("decoupler releasing the wrong connector") is a runtime
fix, no API change. 4826: `Decoupler.Decouple` **dropped its fire-time cascade** that walked the
separated vehicle calling `Deactivate()` on every `IActivate` module — `decoupler.fire` inherits the new
behavior automatically (it still matches the game's own decouple exactly); the separated stage's
engine/RCS active flags now persist instead of dropping to false. Coupled with it, `Vehicle.Split` now
copies `_manualControlInputs` + the active sequence to the separated vehicle (see the read-surface
[4826 findings](ksa-read-surface.md#4826-findings)). ⁵ `Undock` itself always compiled (it enqueues an `InputEvents` record, never calls
`Split` directly), but the separation it triggers now applies an **impulse** (`Vehicle.Split(Connector,
double splitImpulse, …)`). **G1 (2026-06-27) re-anchored it** to `Vehicle.Split(Connector, PushoffImpulse)`
and verified against 4750 — see the docking section below.

## Camera focus (Frame phase, authority-exempt)

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `ctl/focus`, `bodies/<id>/focus` | `camera.focus` | `CameraActuator.Focus` | `Program.GetMainCamera().SetFollow(Astronomical, tidalLocking:true, changeControl:false)` | `KSA/Program.cs`, `KSA/Camera.cs` | Medium | ✅ |

---

## First-class per-vessel nodes (Frame phase, authority-exempt) {#per-vessel-nodes}

`Game/Ksa/Actuators/ScaleActuator.cs` + `Game/Ksa/Render/VesselForceRender.cs`. Both ported from
`unscience` (garrys-torch scaling / i-feel-seen) and **deliberately placed under the regular vessel
area** (`vessels/by-id/<id>/…`), not `/sim/debug` — the per-vessel controls migrated out of the debug
namespace. Exempt from the active-vessel authority gate via `KsaCatalog.AnyVesselActions` (each is a
deliberate by-id operation on an arbitrary vessel). Gated only by the `control_enabled` master.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `vessels/by-id/<id>/scale` | `vessel.scale` | `ScaleActuator.Set` (one-shot; > 0 only, `EINVAL` otherwise; KSA resets on vessel rebuild) | recursive `Part.Scale = (f,f,f)` over `Vehicle.Parts.Parts`/`Part.SubParts` (public `double3` setter); KittenEva avatar via reflected `_renderable._characterAvatar.Core.Scale = f*0.01f` | `KSA/Part.cs`, `KSA/PartTree.cs`, `KSA/KittenEva.cs` | **High** (reflection + `GetType().Name` gate) | ✅ |
| `vessels/by-id/<id>/always_render` | `vessel.always_render` | `VesselForceRender.Set` (registry op; installs/removes the `gatos.always_render` prefixes — patches exist **only while ≥ 1 vessel is marked**) | prefixes on `Vehicle.GetWorldMatrix(Camera)` + `Vehicle.UpdateRenderData(Viewport,int)` reproduce the stock bodies minus the `< 1 px` cull: `Camera.GetPositionEgo`, `Vehicle.Body2Cce`, `Vehicle.GetMatrixAsmb2Ego`, `PartTree.UpdateRenderData`, `Vehicle.IsEditedVehicle` | `KSA/Vehicle.cs`, `KSA/Camera.cs`, `KSA/PartTree.cs` | Medium (dynamic Harmony; KittenEva override unaffected) | ✅ |

Read-backs ride `VesselReader.SampleCore` (always on): `scale` ← representative `Part.Scale.X`
(best-effort, `1.0` fallback), `always_render` ← the gatOS registry (no KSA read). The patch lifecycle
detail lives in [`ksa-runtime-coupling.md#always-render-patches`](ksa-runtime-coupling.md#always-render-patches).

---

## `/sim/debug` cheat surface {#debug}

`Game/Ksa/Actuators/DebugActuator.cs` + `DockingActuator.SetPushoffImpulse`. Gated by `[control]
debug_namespace`. Authority-exempt (own opt-in).

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `debug/time/warp` | `debug.warp` | Frame | `Universe.SetSimulationSpeed(double, alert:false)` | `KSA/Universe.cs` | Medium | ✅ |
| `debug/control_vessel` | `debug.control_vessel` | Frame | `Program.GetMainCamera().SetFollow(…)`; `Program.ControlledVehicle = vehicle` | `KSA/Program.cs` | Medium | ✅⁶ |
| `debug/focus` | `camera.focus` | Frame | (same as `ctl/focus`) | `KSA/Program.cs` | Medium | ✅ |
| `debug/vessels/<id>/teleport` | `debug.teleport` | Frame | `Orbit.CreateFromStateCci` + `Vehicle.Teleport` + `Vehicle.UpdatePerFrameData` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | **High** | ✅ |
| `debug/vessels/<id>/impulse` | `debug.impulse` | Frame | `Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,TotalMass,Parent}` + `Orbit.CreateFromStateCci` + `Vehicle.Teleport` + `Vehicle.UpdatePerFrameData` (velocity-bump variant of the teleport pattern; Δv = J/`TotalMass` mirrors `Vehicle.Split`) | `KSA/Vehicle.cs`, `KSA/Orbit.cs` | **High** | ✅⁷ |
| `debug/vessels/<id>/refill_fuel` | `debug.refill_fuel` | **Solver** | `Vehicle.RefillConsumables()` | `KSA/Vehicle.cs` (`:2300`) | Medium | ✅ |
| `debug/vessels/<id>/refill_battery` | `debug.refill_battery` | **Solver** | `Battery.Refill(ref state)` via `Batteries.GetModuleAndAllMutableStatesForInitialization` | `KSA/Battery.cs` (`:59`) | Medium | ✅ |
| `debug/vessels/<id>/docking/<n>/pushoff_impulse` | `debug.docking_pushoff` | Frame | `DockingPort.PushoffImpulse =` (live float, N·s) | `KSA/DockingPort.cs` | Medium | ✅ |

⁶ `Program.ControlledVehicle` setter may itself reject an uncontrollable target in 4750 (see the
`IsControllable` concern) — verify in a live flight.

⁷ Added 2026-07-04 (feature was born on 4826): every member is shared with the teleport / welds /
reader anchors already verified against `2026.7.3.4826`; `Vehicle.Split` (decomp `Vehicle.cs:1081`,
`Δv = impulse/TotalMass` at `:1151-1159`) is the in-engine precedent the math mirrors. In-game pass
pending (see `docs/VALIDATION.md`).

---

## Render & weld cheats — `IvaActuator` + `WeldManager`/`WeldEngine` (Frame phase) {#welds}

Ported from the sibling `unscience` mod, exposed **only** on gatOS surfaces (9p `/sim` + HTTP + MQTT —
no ImGui). Part of the `debug.*` namespace (`[control] debug_namespace`); authority-exempt like the
rest of `/sim/debug`. `Game/Ksa/Actuators/IvaActuator.cs` (→ `Game/Ksa/Render/IvaForceRender.cs`),
`Game/Ksa/Welds/{WeldManager,WeldEngine}.cs`. `KsaCatalog.Dispatch` (now an instance method) routes the
per-source weld actions after vehicle resolution; `always_render_iva` and `weld_clear` are handled
**vessel-agnostically before** resolution; `weld_create`/`weld_here` resolve the **target** from the
command `Token` (the source is the command's `vessel_id`).

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `debug/always_render_iva` | `debug.always_render_iva` | `IvaActuator.SetAlwaysRender`→`IvaForceRender.SetEnabled` | `PartModel.Instances`; `PartModel..ctor(PartModelModule.Template)`; `PartModel.AddInstance(PerInstanceData,Viewport,int)`; `PartModel.ViewportData.Get(...).InstanceList`; `PartModelModule.Template.{Internal,RayTracing}`; `PartModelModule.RaytracingMode.ShadowProxy`; `Program.{Editor,MainViewport}`; `Viewport.Mode`; `CameraMode.IVA` (render gate `PartModel.cs:387`) | `KSA/PartModel.cs`, `KSA/PartModelModule.cs`, `KSA/Viewport.cs` | Medium (dynamic `gatos.iva` Harmony — recheck live) | ✅ |
| `debug/vessels/<id>/weld` | `debug.weld_create` | `WeldManager.Create`→`WeldEngine.UpdateWeld` | `Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,BodyRates,CenterOfMassAsmb,Parent,Orbit,Teleport,UpdatePerFrameData}`; `Orbit.{OrbitLineColor,CreateFromStateCci}`; `IParentBody.GetCci2Cce`; `Universe.GetJobSimStep(double).NextTime`; `Program.GetPlayerDeltaTime`; `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` (subpart-aware). `<part_iid>` resolution (`WeldManager.FindPart`) searches `Vehicle.Parts.Parts` **and** each part's `Part.SubParts` — the anchor may be a top-level part or a subpart | `KSA/Vehicle.cs`, `KSA/Orbit.cs`, `KSA/Universe.cs`, `KSA/Part.cs` | **High** (per-frame `Teleport`) | ✅ |
| `debug/vessels/<id>/weld_here` | `debug.weld_here` | `WeldManager.CreateAtCurrentPose`→`WeldEngine.CapturePose` | inverse transform: `Vehicle.{GetPositionCci,GetBody2Cci,CenterOfMassAsmb}`; `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` | `KSA/Vehicle.cs`, `KSA/Part.cs` | Medium | ✅ |
| `debug/vessels/<id>/unweld` | `debug.weld_remove` | `WeldManager.Remove(vehicle.Id)` | (registry op — no KSA) | — | Low | ✅ |
| `debug/welds/<source>/enabled` | `debug.weld_enable` | `WeldManager.SetEnabled` | (registry op — no KSA) | — | Low | ✅ |
| `debug/welds/clear` | `debug.weld_clear` | `WeldManager.Clear` (vessel-agnostic) | (registry op — no KSA) | — | Low | ✅ |

The orientation offset is stored as an authoritative `doubleQuat` (Euler is display-only); `weld_here`
captures the current source↔anchor pose (the inverse of the per-frame transform). The teleport math is
ported verbatim from `unscience` (stamped with `Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime`
so the body time aligns with the queued solver tick). The **per-frame weld driver** itself
(`WeldManager.Update`, anchoring `JobSystems.VehicleSolvers.Wait()`) is **runtime coupling**, not a write
command — see [`ksa-runtime-coupling.md#welds-driver`](ksa-runtime-coupling.md#welds-driver). The
`debug/welds/<source>/{target,part,offset,rotation,lock_rotation}` registry view is a game-free projection
(`WeldManager.Snapshot()` → `WeldSnapshot`). Welds are **runtime-only** (never persisted); both cheats tear
down on unload (`Mod.TeardownGameCheats`). Errnos: `EBUSY` (source==target, or the two orbit different
bodies), `ENOENT` (target/part gone), `EINVAL` (bad arity/values). Anchors verified `2026-06-28` against
`2026.6.9.4750`; re-verified (static) 2026-07-03 against `2026.7.3.4826` — `Vehicle.Teleport`, `Orbit.cs`,
`JobSystems.cs`, `Universe.GetJobSimStep` all unchanged. Re-verified (static) 2026-07-14 against
`2026.7.5.4892` — `Vehicle.Teleport(Orbit?,doubleQuat?,double3?)` and `Universe.cs` untouched;
`Orbit.CreateFromStateCci` keeps its signature (the big `Orbit.cs` churn is trajectory-drawing /
danger-zone visualization); rev 4867's CCI↔CCF angular-velocity corruption fix *benefits* the weld
teleport path.

2026-07-16 (feature extension, same 4939 baseline): the weld anchor may now be a **subpart** —
`WeldManager.FindPart` also searches each part's `Part.SubParts` (create-time validation **and** the
per-tick re-resolution in the driver, so an animated subpart anchor tracks its live pose). No new
KSA members in the weld math: `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` are subpart-aware in the
game (`IsSubPart` branch composing through `PartParent` — the same properties purrTTY's in-world
quads anchor to subparts with); that branch staying intact is the semantic to watch on future bumps.
Discovery rides the read surface (`parts/<n>/subparts/<m>/`,
[`ksa-read-surface.md#parts`](ksa-read-surface.md#parts)).

---

## Render quad cheat — `thug_life` (Frame phase) {#thug-life}

Ported from the sibling `unscience` mod, exposed **only** on gatOS surfaces (9p `/sim/debug/thug_life/`
+ HTTP + MQTT — no ImGui). Part of the `debug.*` namespace (`[control] debug_namespace`); authority-exempt
like the rest of `/sim/debug`. Anchors all under `gatOS.GameMod/Game/Ksa/ThugLife/`; `KsaCatalog.ThugLife`
(a private dispatch method, taking a `ThugLifeManager thugLife` ctor param) routes the seven actions
**vessel-agnostically** — the entry id travels in `ordinal`, and `add` resolves the anchor vehicle from the
command `Token` via the existing `ResolveVehicle`. **This is gatOS's first custom GPU rendering and its
highest-churn KSA coupling** — the *write* path below is small (it only edits the entry registry); the deep
coupling is the per-frame GPU draw + anchor math, which is **runtime coupling**, not a write command — see
[`ksa-runtime-coupling.md#thug-life-patch`](ksa-runtime-coupling.md#thug-life-patch).

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `debug/thug_life/add` | `debug.thug_life_add` | `ThugLifeManager.Create` (resolves the anchor vehicle from `Token`) | `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.Parts`; `Part.InstanceId` (anchor pick; `0` = vehicle body frame); lazy GPU build (see runtime page) | `KSA/Vehicle.cs`, `KSA/Part.cs`, `KSA/SuperMeshRenderSystem.cs` | **High** (render) | ✅ |
| `debug/thug_life/clear` | `debug.thug_life_clear` | `ThugLifeManager.Clear` (vessel-agnostic; tears down the render postfix + GPU when last) | (registry + GPU lifecycle — no KSA *write*) | — | Low | ✅ |
| `debug/thug_life/<id>/position` | `debug.thug_life_position` | `ThugLifeManager.SetPosition` (id in `ordinal`) | `ThugLifeEntry.Position` (consumed by the per-frame anchor math) | — | Low | ✅ |
| `debug/thug_life/<id>/rotation` | `debug.thug_life_rotation` | `ThugLifeManager.SetRotation` (id in `ordinal`) | `ThugLifeEntry.Rotation` | — | Low | ✅ |
| `debug/thug_life/<id>/size` | `debug.thug_life_size` | `ThugLifeManager.SetSize` (id in `ordinal`) | `ThugLifeEntry.{Width,Height}` | — | Low | ✅ |
| `debug/thug_life/<id>/visible` | `debug.thug_life_visible` | `ThugLifeManager.SetVisible` (id in `ordinal`) | `ThugLifeEntry.Visible` | — | Low | ✅ |
| `debug/thug_life/<id>/remove` | `debug.thug_life_remove` | `ThugLifeManager.Remove(id)` (id in `ordinal`) | (registry op — no KSA) | — | Low | ✅ |

`add` takes `<vessel> <part_iid>` (defaults for pose/size) or the full
`<vessel> <part_iid> x y z pitch yaw roll w h`; the anchor is a **top-level part by `instance_id`** (reuses
the welds `parts/` listing) or `0` = the vehicle body frame. No subparts in v1. The render hook + GPU
resources install **lazily on the first entry**, tear down on the last and at unload (off by default =
zero patches/GPU — the welds/IVA "only active when toggled on" discipline). All seven actions are
**Frame-phase**. The `debug/thug_life/count`, `…/<id>/{vessel,part,spec}` reads are a **game-free
projection** of `ThugLifeManager.Snapshot()` (`ThugLifeSnapshot` records — no KSA read; `TelemetrySampler`
projects it into `SimSnapshot.ThugLife`). Errnos: `ENOENT` (vessel/part/id gone), `EINVAL` (bad
arity/values), `EIO` (renderer unavailable). Entries are **runtime-only** (never persisted); torn down on
unload (`Mod.TeardownGameCheats`). Anchors verified `2026-06-28` against `2026.6.9.4750`; re-verified
(static) 2026-07-03 against `2026.7.3.4826` — the `SuperMeshRenderSystem.cs` diff touches only shader
macro-definition overloads in `Setup*Renderers`, `RenderMainPass(CommandBuffer)` is byte-identical at
line 329, and the `UnlitMesh.{vert,frag}` shader assets are unchanged; re-verified (static) 2026-07-14
against `2026.7.5.4892` — `SuperMeshRenderSystem.cs` entirely untouched, shaders/keys unchanged;
**live quad-draw check still pending** (`docs/VALIDATION.md`). Pipeline
assumptions + the new render-DLL references: [`ksa-assets-and-versions.md`](ksa-assets-and-versions.md).

---

## Userland audio playback — `AudioActuator` (Frame phase, vessel-agnostic) {#audio}

`Game/Ksa/Actuators/AudioActuator.cs` over the game-free `gatOS.SimFs/Audio/AudioStore` (the shared
clip store — GATOS_CUSTOM_AUDIO_PLAN). **Not** part of `debug.*`: gated by `[audio] audio_enabled`
(off ⇒ the `/sim/audio` surface vanishes and `audio.*` answers `EOPNOTSUPP`), plus the
`control_enabled` master like every write. Vessel-agnostic — `KsaCatalog.Execute` routes `audio.*`
**before** vehicle resolution (the target is a clip/channel, never a vehicle), so the authority gate
never applies. Drives **FMOD Core directly** via the public `GameAudio.System` (the game's
higher-level `SoundReference`/`MusicPlayList` API is asset-file-bound and useless for runtime bytes),
but reuses the game's channel groups so the in-game Sfx/Music/UI volume sliders govern playback.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|---|
| `audio/play` | `audio.play` | `AudioActuator.Play` (+ `CreateOrGetSound` on first play of a clip version) | `GameAudio.System` (public static `FmodSystem`); `Fmod.TryCreateSound(bytes, Mode.OpenMemory\|_2d\|CreateSample/CreateCompressedSample, in CreateSoundExInfo{Length}, out Sound)` — the game's own in-memory recipe (`GameAudio.CreateFmodSound`); `Fmod.TryPlaySound(sound, group, paused:true, out Channel)`; `GameAudio.GetChannelGroup(ChannelGroupType.{Sfx,Music,Ui})`; `Channel.TrySet{Position,Mode,LoopCount,LoopPoints,Volume,Pan,Pitch,Paused}`; `Sound.TryGetLength` | `KSA/GameAudio.cs`, `KSA/ChannelGroupType.cs`, `Brutal.FmodApi/Fmod.cs`, `Brutal.FmodApi/Mode.cs` | Low (FMOD Core P/Invoke surface is upstream-stable; `GameAudio.System`/`GetChannelGroup` are plain public statics) | ✅ |
| `audio/set` | `audio.set` | `AudioActuator.Set` | `Channel.TrySet{Volume,Pan,Pitch,Paused,Position}` | `Brutal.FmodApi/Fmod.cs` | Low | ✅ |
| `audio/stop` | `audio.stop` | `AudioActuator.Stop` | `Channel.TryStop` | `Brutal.FmodApi/Fmod.cs` | Low | ✅ |

**Runtime coupling beyond the writes:** the per-frame tick (`Mod.DriveAudio` → `AudioActuator.Tick`,
`OnBeforeUi` right after the command drain — the same thread that pumps `GameAudio.UpdateAudio` /
`System.Update()`) prunes finished channels (`Channel.TryIsPlaying`), enforces `end=`
(`Channel.TryGetPosition`), releases evicted FMOD sounds (`Sound.TryRelease` — deferred: never while
a channel plays), publishes the `/sim/audio/status` snapshot into the store, and stamps
`audio.finished` events with `Universe.GetElapsedSimTime().Seconds()`. gatOS never calls
`System.Update/Close/Release` — the game owns the system lifecycle; gatOS owns only the `Sound`s it
creates (all released at unload via `Mod.TeardownGameCheats` → `AudioActuator.Shutdown`). The
uploads/caps/status files themselves are **game-free** (`gatOS.SimFs/Audio/**` — see
[`non-ksa-surface.md`](non-ksa-surface.md)). Deliberate: playback ignores the game's >10× warp SFX
mute (a raw-Core channel bypasses `GameAudio.PlaySound`'s gate — a master alarm that mutes at warp
defeats the purpose). New game-DLL reference: `Brutal.Fmod.dll`
([`ksa-assets-and-versions.md`](ksa-assets-and-versions.md)). Errnos: `ENOENT` (unknown clip /
no matching channel), `EBUSY` (clip still uploading / channel table full), `EINVAL` (grammar/range),
`EIO` (FMOD refused the bytes / could not start a channel), `EOPNOTSUPP` (audio disabled). Anchors
verified `2026-07-02` against `2026.6.9.4750`; re-verified 2026-07-03 against `2026.7.3.4826`
(`GameAudio.cs` byte-identical; `Brutal.FmodApi` not in the changed set); re-verified 2026-07-14 against
`2026.7.5.4892` (`GameAudio.cs` untouched; the refreshed `Brutal.Fmod.dll` compiles clean against the
same call surface).

---

## ✅ Docking pushoff (G1 FIXED, 2026-06-27) {#docking}

**Was a compile break.** `DockingActuator.cs` did `ports[ordinal].PushoffForce = (float)newtons;` and the
read at `VesselReader.cs` did `port.PushoffForce`. **4750 (rev 4683) renamed the member to
`PushoffImpulse` and changed its meaning from force (N) to impulse (N·s).** Evidence:

- `KSA/DockingPort.cs`: `public required float PushoffImpulse;`, `public required float
  LatchingKineticEnergy;` (was `PushoffForce` / `LatchingImpulse`), and `Undock(...) =>
  oldVehicle.Split(Connector, PushoffImpulse);`.
- `KSA/Vehicle.cs:1013`: `public Vehicle? Split(Part.Connector splitConnector, double splitImpulse,
  string? splitVehicleId = null)`.
- Asset `Content/Core/CoreCouplingAGameData.xml`: `<PushoffImpulse Ns="7000"/>`,
  `<LatchingKineticEnergy J="50"/>` (numerically still 7000, but now **N·s**).

**Applied fix (G1).** Both references rebound to `PushoffImpulse`: the read `VesselReader.cs:542`
(`port.PushoffImpulse`) and the debug setter `DockingActuator.cs:62`
(`ports[ordinal].PushoffImpulse = (float)impulse`; method renamed `SetPushoffForce` →
**`SetPushoffImpulse`**, validation message "must be >= 0 N·s"). The snapshot field
`DockingSnapshot.PushoffForceN` → **`PushoffImpulseNs`**; the `/sim` read leaf and `debug` control leaf
were renamed `pushoff_force` → **`pushoff_impulse`** (unit **N → N·s**) — a deliberate breaking `/sim`
rename, justified because the datum's meaning changed (keeping the name would lie). The action key
`debug.docking_pushoff` is unchanged (no "force" in it → no command-surface churn). All three docking
anchors (the two here + `VesselReader.SampleDocking`) were re-verified to `Verified="2026-06-27"`,
`GameVersion="2026.6.9.4750"`. SPEC rows, the matrix, `sim_openapi.yml` (`pushoff_impulse_ns`) and the
`gatos` skill were updated in lockstep. **Build is green against 4750.** **Live re-check still pending**
(undock applies the impulse; the debug knob changes separation energy) — see
[`../docs/VALIDATION.md`](../docs/VALIDATION.md). Full record:
[`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md).

---

## Items confirmed *not* affected by 4750
- **Staging** (`SequenceList.ActivateNextSequence`) — the "Stages → Resource Groups" rename (rev 4732)
  is about resource groups, not activation sequences; gatOS uses Sequences; compiled clean.
- **Brutal numerics** (rev 4729 package bump) — all `double3`/`doubleQuat`/`float3` usages in the
  actuators compiled clean (the sole 4750 compile failure was `DockingPort.PushoffForce`, now fixed —
  see the docking section above).
- **Lights / animations / decouplers / RCS / engines / flight computer / teleport / refills** — all
  members compiled clean and none appear in the changelog with an API-affecting change.

---

## ✅ 4939 write-surface findings (playbook pass 2026-07-16) {#4939-findings}

Full pass `2026.7.5.4892` → `2026.7.6.4939` (build forced non-incremental + tests green; changelog
gapless for the first time — revs 4893–4939 all logged, diff taken `7cf5c0a..2423a02` inside the
assemblies checkout). **Every bound write member, every reflection accessor, and every Harmony hook
target is UNCHANGED** — no code change required. Highlights:

- **Rev 4914 control-module lockout is UI-only — `/sim` writes are unaffected but now *diverge* from the
  stock UI.** The pre-existing `ControlsLockout` struct (the flight-computer lockout) now also gates the
  **staging key**, the part-window engine/thruster **Active checkboxes**, and the **Decouple menu item**
  on vehicles without a control module. The gate lives in `Vehicle`'s key-input handler and the part
  windows; the module-level entry points gatOS binds — `SequenceList.ActivateNextSequence`,
  `EngineController.SetIsActive(Vehicle,bool)`, `ThrusterController.SetIsActive`,
  `Decoupler.SetIsActive(Vehicle,true)`, `Vehicle.SetEnum(MainIgnite/MainShutdown)` — carry **no** new
  gate (`EngineController.cs`/`Decoupler.cs`/`ThrusterController.cs` untouched by the diff). So `/sim`
  writes still actuate a control-less vessel where the stock UI now refuses — the same divergence that
  has always existed for the FC setpoints (which KSA runtime-gates on `IsControllable`, see ² above)
  extended to the rest of the stock UI. gatOS's authority gate (active-vessel, G-D1) is orthogonal and
  unchanged. Flagged for a live confirm + kept as documented behavior: `/sim` commands any addressed
  vessel modulo `all_vessels`, module-method semantics permitting.
- **Reflection accessors re-verified (static)**: `Vehicle._manualControlInputs` present
  (`Vehicle.cs:232`, `ManualControlInputs.cs` untouched — throttle + translate paths intact); the
  light-template clone path untouched (`LightModule.cs` not in the changed set; `PartTemplate.cs` churn
  is symmetry groups + a volume tooltip, zero light references); KittenEva avatar-scale chain untouched
  (no EVA file in the changed set). Live `/sim/status/accessors` check still advised.
- **Harmony hook targets intact**: `Universe.ExecuteNextVehicleSolvers(double, SimStep)`
  (`Universe.cs:1660` — the whole `Universe.cs` diff is log-line renumbering);
  `Program.DrawProgramMenusHook()` (`Program.cs:3453`); `Program.RenderGame` interior gained
  volumetric-plume-trail + `GizmosRenderer` calls but its **tail is byte-identical** (the final
  `commandBuffer2.End()` and the preceding transitions/composite) — the display transpiler's
  final-`End()` injection site *and* its image-layout assumption hold;
  `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)` (`:329`, file untouched);
  `Vehicle.GetWorldMatrix(Camera)` / `Vehicle.UpdateRenderData(Viewport,int)` untouched (the
  `gatos.always_render` reproduced-body prefixes stay byte-accurate); `PartModel.Instances`/ctor
  untouched (`gatos.iva` — the `PartModelModule.cs` churn is one fuel-flow highlight bit);
  `JobSystems.VehicleSolvers` untouched.
- **Solver-phase rationale unchanged**: `FlightComputer.cs`/`VehicleUpdateData.cs` untouched — the FC
  snapshot/restore window is intact, Solver phase stays mandatory. `VehicleUpdateTask.cs` changes are
  additive (animating vehicles forced off-rails; `Tank.UpdateTransfers` in the module update).
- **thug_life render set re-verified (static)**: `UnlitMeshVert`/`UnlitMeshFrag` keys and the
  `UnlitMesh.{vert,frag}` assets unchanged (the `DefaultAssets.xml` churn is particle/trail/clutter
  shader keys); `Program.OffScreenPass`/`SampleCount` unchanged; the new screenspace-particle +
  volumetric-trail passes are mid-`RenderGame` compute/composite work that doesn't alter the main-pass
  render-pass the quad draws in; `RenderCore.Mesh/SimpleVkMeshAtlas`'s bounding-sphere-radius fix
  affects game-mesh culling only (the quad builds its own buffers). Live draw check still mandatory —
  render-pass compatibility is only provable in-game.
- **Behavior notes (game-side, inherited automatically)**: `animation.goal` writes now have real
  physical effect — colliders follow the animation and the vehicle stays off-rails while animating
  (rev 4930; landing legs work). Refill cheats (`Vehicle.RefillConsumables`, `Battery.Refill`)
  untouched; what refill fills continues to follow tank affinity/assignment, now including the
  propellant-use-disabled state (a disabled tank still fills but won't feed engines).

---

## ✅ 4892 write-surface findings (playbook pass 2026-07-14) {#4892-findings}

Full pass `2026.7.3.4826` → `2026.7.5.4892` (build forced non-incremental + tests green; revs 4827–4859
unlogged in both drops, so the git diff between the drops' commits is authoritative). **Every bound
write member, every reflection accessor, and every Harmony hook target is UNCHANGED** — no code change
required. Highlights:

- **Reflection accessors re-verified (static)**: `Vehicle._manualControlInputs` present
  (`Vehicle.cs:232`, struct `ManualControlInputs` with `EngineOn`/`EngineThrottle`/`ThrusterCommandFlags`
  — `ManualControlInputs.cs` untouched by the diff); the light-template clone path untouched
  (`LightModule.cs` not in the changed set); KittenEva avatar-scale chain untouched (`KittenEva.cs` not
  in the changed set). Live `/sim/status/accessors` check still advised.
- **Harmony hook targets intact**: `Universe.ExecuteNextVehicleSolvers(double, SimStep)`
  (`Universe.cs:1660`, file untouched), `Program.DrawProgramMenusHook()` (`Program.cs:3417`, same shape),
  `Program.RenderGame(AcquiredFrame, double)` (`Program.cs:3965` — interior gained an underwater-render
  call; the display transpiler injects at the method's *final* `Brutal.VulkanApi` `End()`, unaffected).
  `Vehicle.GetWorldMatrix(Camera)` / `Vehicle.UpdateRenderData(Viewport,int)` (the `gatos.always_render`
  prefix targets) keep identical signatures **and stock bodies** (only line-shifted), so the
  reproduced-body prefixes stay byte-accurate.
- **Solver-phase rationale re-confirmed**: the FC snapshot/restore window is intact —
  `VehicleUpdateData.Prepare` snapshots via `NewFlightComputer.CopyFrom(flightComputer)`
  (`VehicleUpdateData.cs:87`) and the apply restores via `FlightComputer.CopyFrom(updateData.NewFlightComputer)`
  (`Vehicle.cs:1991`). Frame-phase FC writes would still be overwritten; the Solver phase stays mandatory.
- **Behavior notes (game-side, inherited automatically)**: `FlightComputer.CommandEngineThrottles` now
  **zeroes `CommandThrottle`/`CommandBurnTime`** on every engine when no burn is commanded — after
  `vessel.burn` completes, per-engine throttle reads drop to an honest 0 (see the
  [read-surface 4892 findings](ksa-read-surface.md#4892-findings)). The rev 4884 combustion→Reactions /
  tank-affinity refactor leaves `Vehicle.RefillConsumables()` and `Battery.Refill` untouched (refill
  cheats unaffected; tanks now auto-assign a propellant mix by affinity, so what refill *fills* follows
  the new game rules). `Decoupler.{IsActive,SetIsActive}` untouched (the `Decoupler.cs` diff is a
  particle-emitter `Handle` refactor inside `Activate`). Rev 4866: vehicles set to ignite with no
  propellant no longer stay off-rails (perf fix; `vessel.ignite` semantics unchanged).

---

## ✅ 4826 write-surface findings (playbook pass 2026-07-03)

Full pass `2026.6.9.4750` → `2026.7.3.4826` (build + tests green; changelog gapped for revs 4751–4823,
so the decomp diff is authoritative). **Every bound write member, every reflection accessor, and every
Harmony hook target is UNCHANGED** — no code change required. Highlights:

- **13 bound decomp files are byte-for-byte identical** to the 4750 baseline: `FlightComputer.cs`,
  `BurnTarget.cs`, `Orbit.cs`, `DockingPort.cs`, `ManualControlInputs.cs`, `EngineController.cs`,
  `ThrusterController.cs`, `LightModule.cs`, `PowerConsumer.cs`, `Battery.cs`, `Camera.cs`,
  `GameAudio.cs`, `KittenEva.cs` (+ `FloatReference`/`ColorRgbReference`/`CelestialSystem`/`Viewport`).
- **Reflection accessors re-verified (static)**: `Vehicle._manualControlInputs` present
  (`Vehicle.cs:232`), its `ManualControlInputs` struct identical; the light-template clone path
  (`LightModule.TemplateData` + `Intensity`/`ColorRgb`/`OuterAngle`/`InnerAngle`) identical — the
  `PartTemplate.cs` +188 churn is the *part* template (symmetry/connectors), zero light references;
  KittenEva avatar-scale chain unchanged. Live `/sim/status/accessors` check still advised.
- **Harmony hook targets intact**: `Universe.ExecuteNextVehicleSolvers(double, SimStep)`
  (`Universe.cs:1660`) and `Program.DrawProgramMenusHook()` (`Program.cs:3379`) — signatures and bodies
  unchanged; the FlightComputer solver snapshot/restore window (`VehicleUpdateState`/`VehicleUpdateTask`
  prepare/apply) that motivates the **Solver phase** is preserved.
- **Behavior notes (game-side, inherited automatically)**: `Decoupler.Decouple` cascade removal +
  `Vehicle.Split` control-input/sequence inheritance (footnote ⁴ above);
  `OrbitController.cs` churn is the **editor camera** controller (middle-mouse zoom-drag), unrelated to
  vehicle orbits/attitude; `Tank.cs`/`PowerManager.cs` changes are ice-particle visuals / a span→array
  refactor — the refill cheats' members (`Vehicle.RefillConsumables`, `Battery.Refill`) are untouched.
