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
`debug_namespace`). The whole `camera.*` family is exempt (view-only, routed before vehicle resolution).

> **One write family on this page's surface has no KSA binding at all:** the seven `schedule.*` actions
> behind `/sim/ctl/timed_batch` + `/sim/ctl/schedules/**`. `KsaCatalog` routes them straight to the
> game-free `ScheduleStore.Execute` — there is nothing game-side to re-validate and re-implementing it
> would create a second definition. They are catalogued in
> [`non-ksa-surface.md#scheduler`](non-ksa-surface.md#scheduler), not here; a KSA update cannot break
> them. What they *do* is post ordinary `SimCommand`s into the same queue, so every row on this page is
> reachable from a schedule and inherits that row's phase, errno and health latch unchanged.

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

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
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

| `/sim` path | action key | phase | actuator | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|---|---|
| `ctl/throttle` | `vessel.throttle` | Frame | `ThrottleActuator.Set` | **reflection** `Vehicle._manualControlInputs.EngineThrottle` (no public setter; `GetManualThrottle()` reads it) | `KSA/Vehicle.cs` (`:232,824`) | **High** | ✅² |
| `ctl/stage` | `vessel.stage` | Frame | `StagingActuator.Stage` | `Vehicle.Parts.SequenceList.ActivateNextSequence(vehicle)` + `Vehicle.UpdateAfterPartTreeModification()` | `KSA/SequenceList.cs`, `KSA/Vehicle.cs` | Medium | ✅³ |
| `ctl/rcs` | `vessel.rcs` | Frame | `RcsActuator.SetMaster` | `ThrusterController.SetIsActive(vehicle,bool)` over all | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/translate` | `vessel.translate` | Frame | `TranslateActuator.SetTranslation` | **reflection** `Vehicle._manualControlInputs.ThrusterCommandFlags` (same struct as throttle; translate bits replaced, rotation bits preserved) + `ThrusterMapFlags`; read-back `Vehicle.GetThrusterFlags()`. `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`ManualThrustMode.Direct` → `SelectJetsToFire`; Auto attitude strips only rotation bits, so translation composes with tracking). Sign→flag mapping (+x=`TranslateForward`, +y=`Right`, +z=`Down`) verified against the `KittenBackPackSubPart` nozzle geometry in `Content/Core/PartGameData.xml` | `KSA/Vehicle.cs`, `KSA/ThrusterMapFlags.cs`, `KSA/FlightComputer.cs` (`:454-519,1029`) | **High** | ⚠️ **gated + cleared at 5168** ⁷⁸ |
| `ctl/rotate` | `vessel.rotate` | Frame | `RotateActuator.SetRotation` | **reflection** `Vehicle._manualControlInputs.ThrusterCommandFlags` (same struct as throttle/translate; rotation bits replaced, translation bits preserved) + `ThrusterMapFlags`; read-back `Vehicle.GetThrusterFlags()`. `FlightComputer.ComputeRcsControl` consumes the flags each solver step (`ManualThrustMode.Direct` → `SelectJetsToFire`; `ComputeTvcControl` decodes the same bits for gimbals). **Auto attitude strips the rotation bits** (`WithNoRotation()`) — full authority needs `attitude_mode=manual`, the inverse of translate's compose note. Sign→flag mapping is KSA's own torque decode (`ComputeTvcControl:559-585`): +x=`RollRight`, +y=`PitchUp`, +z=`YawRight` | `KSA/Vehicle.cs`, `KSA/ThrusterMapFlags.cs`, `KSA/FlightComputer.cs` (`:457-524,559-585,1020`) | **High** | ⚠️ **gated + cleared at 5168** ⁷⁸ |
| `ctl/attitude_mode` | `vessel.attitude_mode` | **Solver** | `FlightComputerActuator.SetAttitudeMode` | `FlightComputer.{AttitudeMode,AttitudeTrackTarget}`; `FlightComputerAttitudeMode`/`...TrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅²⁶ |
| `ctl/attitude_frame` | `vessel.attitude_frame` | **Solver** | `…SetAttitudeFrame` | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | `KSA/FlightComputer.cs` | Medium | ✅² |
| `ctl/attitude_target` | `vessel.attitude_target` | **Solver** | `…SetAttitudeTarget` | `FlightComputer.{CustomAttitudeTarget,AttitudeFrame,AttitudeTrackTarget=Custom}`; `VehicleReferenceFrameEx.{GetEclBody2Cci,QuaternionToEulerAngles}` | `KSA/FlightComputer.cs` | Medium | ✅²⁶ |
| `ctl/burn` | `vessel.burn` | **Solver** | `…SetBurn` | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant,DeltaVTargetCci}` | `KSA/BurnTarget.cs` | Medium | ✅² |
| `ctl/rcs_mode` | `vessel.rcs_mode` | **Solver** | `FlightComputerActuator.{SetRcsMode,ReadRcsMode}` | `FlightComputer.RCSMode` (`FlightComputerRCSMode.{Enabled,Disabled}`) — the file twin of the in-game **R** keybind. `Disabled` is a hard master cut-off: `ComputeRcsControl` zeroes the manual `ThrusterCommandFlags` (`:471`) so `ctl/translate`+`ctl/rotate` go dead, and `UpdateRcsParams` zeroes the RCS torque authority (`:884`) so auto attitude holds lose RCS. Solver-phase because `CopyFrom` copies it (`:131`) | `KSA/FlightComputer.cs` (`:41,131,471,884`) | Medium | ➕ added at 5168 (rev 5143) ⁷ |

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

⁶ **4980 semantic drift (no API change)** — two new FC behaviors gatOS inherits: **(a) rev 4946/4949**
adds `FlightComputer.RCSMode` (`FlightComputerRCSMode.{Enabled,Disabled}`, new file; toggled in-game by
the new R keybind, persisted via `FlightComputerData.RCSMode`, and copied by `FlightComputer.CopyFrom`
so the Solver-phase discipline is unaffected). With it `Disabled`, `UpdateActiveControlSystems` skips
the whole per-thruster RCS torque-authority scan, so an **auto** attitude hold on a vessel whose only
attitude authority is RCS **silently stops actuating** (only gimballed TVC survives, and only during an
engine burn) — a new silent-ignore path alongside the `IsControllable` gate². ~~`ctl/rotate`/`ctl/translate`
(manual `ThrusterCommandFlags`) are *not* gated by it. gatOS neither reads nor sets `RCSMode`
(candidate additive control).~~ **SUPERSEDED at 5168 — see ⁷ below: rev 5143 made `RCSMode` gate the
manual flags too, and gatOS now reads *and* sets it as `ctl/rcs_mode`.** **(b) rev 4978**: `FlightComputer.RollMode` default flipped
`Up` → `Decoupled` ("ANY") — a fresh FC no longer actuates roll, so `ctl/attitude_target`'s quaternion
converges in +X pointing but rolls free unless RollMode is set (gatOS does not set it; loaded saves
keep their serialized RollMode). Both flagged for live confirm in `docs/VALIDATION.md`.

⁷ **5168 semantic break (rev 5143) — `RCSMode` is now a HARD master cut-off for MANUAL RCS too.**
This **falsifies** the 4980 note above (⁶a), which stated that manual `ThrusterCommandFlags` were not
gated by `RCSMode`. `FlightComputer.ComputeRcsControl` gained an unconditional zeroing step
(`KSA/FlightComputer.cs:471`):

```csharp
ThrusterMapFlags thrusterMapFlags = inputs.ThrusterCommandFlags.WithCanceledOpposingCommands();
if (RCSMode != FlightComputerRCSMode.Enabled) { thrusterMapFlags = ThrusterMapFlags.None; }
```

and `UpdateRcsParams` now zeroes the thruster authority outright (`:884`) rather than computing it
from `outputs.Thrusters.GlobalState.Authority`. So with RCS toggled off (the in-game **R** key)
**`ctl/translate` and `ctl/rotate` do nothing at all** — and gatOS's read-back
(`Vehicle.GetThrusterFlags()`) still reports the *commanded* flags, so the divergence is invisible
from the command side. **Closed in the same work item:** `FlightComputer.RCSMode` is now a first-class
read + control at **`vessels/by-id/<id>/ctl/rcs_mode`** (`Enabled`/`Disabled`,
`FlightComputerActuator.SetRcsMode`/`ReadRcsMode`, action key `vessel.rcs_mode`). It is a
**Solver-phase** action like every other FC setpoint, because `FlightComputer.CopyFrom` copies
`RCSMode` (`:131`) — a Frame-phase write would be reverted by the in-flight solve.

⁸ **5168 semantic break (rev 5128) — the game now CLEARS the latched manual thruster flags.** gatOS's
`ctl/translate`/`ctl/rotate` contract is "latches until rewritten"; that no longer holds unilaterally.
The new `Vehicle.ClearHeldPlayerInput()` (`KSA/Vehicle.cs:5578`) zeroes `ThrusterCommandFlags` (plus
the new `Sprint` field and `_engineFlags`) and is called from **four** sites:

| Trigger | Call site |
|---|---|
| the controlled vehicle changes (vessel switch) | `KSA/Program.cs:459` |
| the game window **loses focus** | `KSA/Program.cs:1558` |
| camera-mode switch | `KSA/Viewport.cs:343` |
| **every update**, while `!IsControlledVehicleActive` **or `ImGui.GetIO().WantCaptureKeyboard`** **or `Universe.GetSimulationSpeed() > 30.0`** | `KSA/Vehicle.cs:2215` (`PrepareWorker`) |

The last row is the sharp edge: it re-clears *continuously*, so a gatOS flight program holding a
translation during **time warp above 30×**, or while the player is **typing into any ImGui text
field**, gets silently zeroed every tick. `_engineFlags` is the keyboard throttle-ramp state, **not**
gatOS's ignition path — `ctl/throttle` (`EngineThrottle`) and `ctl/ignite` (`EngineOn`) are untouched
by this and keep working. A second, kitten-only clear lives in
`VehicleUpdateTask.FlightComputerInputsFor` (`:1149`): a kitten not in `LocomotionMode.Mmu` has its
thruster flags zeroed. Live confirms queued in `docs/VALIDATION.md`.

⁹ **5168 semantic break (rev 5132) — a decoupler can be DISABLED and then cannot fire.** `Decoupler`
gained `IEnable` + `IsEnabled`/`SetIsEnabled` + save data (players may disable a part's decoupler
module, e.g. turning an adapter into a static fairing), and `Decoupler.SetIsActive` is now gated on it
(`KSA/Decoupler.cs:97`). `DecouplerActuator.Fire` previously checked only `IsActive`, so firing a
disabled decoupler was a **silent no-op reported as success**. Fixed: `Fire` now rejects a disabled
decoupler with **EOPNOTSUPP**, and the state is readable at
**`vessels/by-id/<id>/decouplers/<n>/enabled`**.

> **Why Solver phase?** KSA's async vehicle solver snapshots the whole `FlightComputer` at prepare and
> restores it at apply (`FlightComputer.CopyFrom`). A frame-phase write to a FC setpoint lands *outside*
> that capture and is overwritten by the in-flight solve (the mode flashes on, then snaps back). The
> Solver actions drain in a Harmony `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`
> (`Mod.DrainSolverCommands`). See [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#threading-phases).

## Per-module controls (G4, Frame phase)

`LightActuator.cs`, `DecouplerActuator.cs`, `DockingActuator.cs`, `RcsActuator.cs`.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|---|
| `rcs/<n>/active` | `rcs.active` | `RcsActuator.SetActive` | `ThrusterController.SetIsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `lights/<n>/on` | `light.on` | `LightActuator.SetOn` | `LightModule.Parent.FullPart.LightSwitch.LightIsActive` | `KSA/LightModule.cs` | Medium | ✅ |
| `lights/<n>/brightness` | `light.brightness` | `LightActuator.SetBrightness` | `LightModule.Template.Intensity.Value` (per-instance **clone**) | `KSA/LightModule.cs`, `KSA/FloatReference.cs` | **High** | ✅ |
| `lights/<n>/color` | `light.color` | `LightActuator.SetColor` | `LightModule.Template.ColorRgb.{R,G,B}` + `OnDataLoad` (clone) | `KSA/LightModule.cs`, `KSA/ColorRgbReference.cs` | **High** | ✅ |
| `lights/<n>/outer_angle` | `light.outer_angle` | `LightActuator.SetOuterAngle` | `LightModule.Template.OuterAngle.Value` (deg→rad, clamp `[1e-5, 1.5697963]`) | `KSA/LightModule.cs`, `KSA/Light.cs` (`CreateSpotLight`) | **High** | ✅ |
| `lights/<n>/inner_angle` | `light.inner_angle` | `LightActuator.SetInnerAngle` | `LightModule.Template.InnerAngle.Value` (clamp `[0, OuterAngle]`) | `KSA/LightModule.cs` | **High** | ✅ |
| `decouplers/<n>/fire` | `decoupler.fire` | `DecouplerActuator.Fire` | `Decoupler.{IsActive,IsEnabled,SetIsActive}` (re-fire → `EBUSY`; **disabled → `EOPNOTSUPP`**, since rev 5132 gated `SetIsActive` on `IsEnabled`) | `KSA/Decoupler.cs` (`:33,35,95`) | Medium | ✅⁴ ⁹ |
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
and verified against 4750 — see the docking section below. **4980 (rev 4943) removed
`VehicleDockingInputData.OldMeanRadius`** (the docking camera zoom-jump fix no longer needs the caller
to stash it) — the **only compile break of the 4939 → 4980 pass**; `DockingActuator.Undock` now enqueues
`{Vehicle, DockingPort, Undock}` exactly like the stock UnDock menu item (`DockingPort.cs:145-150`), and
`DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)` is byte-identical. Fixed + re-anchored
2026-07-22.

## Camera focus (Frame phase, authority-exempt) {#camera-focus}

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5168 |
|---|---|---|---|---|---|---|
| `ctl/focus`, `bodies/<id>/focus`, `debug/focus` | `camera.focus` | `CameraActuator.Focus` | `Program.MainViewport.MapCamera.SetFollow(Astronomical, tidalLocking:true, changeControl:false, alert:false)` **and** `…BaseCamera.SetFollow(…)` | `KSA/Program.cs`, `KSA/Viewport.cs`, `KSA/Camera.cs`, `KSA/InputEvents.cs` | Medium | ➕ **rebound 2026-08-06** (C1.4) |

**Rebound at C1.4 — a latent map-desync bug, fixed.** A `Viewport` carries **two** cameras, `BaseCamera`
and `MapCamera`, and `GetCamera()` returns whichever the current mode uses. The actuator used to reach
the active one through `Program.GetMainCamera()`, so a focus issued while *not* in map mode left the
**map view still pointing at the previous target** until the player happened to re-focus from inside map
mode. The game's own follow action sets the target on **both** (`KSA/InputEvents.cs:759-760`); gatOS now
does the same. `alert: false` was added at the same time — the `"Following X"` `TimedAlert` matters now
that this camera surface is used to shoot footage. `changeControl: false` is unchanged and still
load-bearing: the default would null `Program.ControlledVehicle` and drop the player's vessel.

---

## First-class per-vessel nodes (Frame phase, authority-exempt) {#per-vessel-nodes}

`Game/Ksa/Actuators/ScaleActuator.cs` + `Game/Ksa/Render/VesselForceRender.cs`. Both ported from
`unscience` (garrys-torch scaling / i-feel-seen) and **deliberately placed under the regular vessel
area** (`vessels/by-id/<id>/…`), not `/sim/debug` — the per-vessel controls migrated out of the debug
namespace. Exempt from the active-vessel authority gate via `KsaCatalog.AnyVesselActions` (each is a
deliberate by-id operation on an arbitrary vessel). Gated only by the `control_enabled` master.

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
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

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|---|
| `debug/time/warp` | `debug.warp` | Frame | `Universe.SetSimulationSpeed(double, alert:false)` | `KSA/Universe.cs` | Medium | ✅ |
| `debug/control_vessel` | `debug.control_vessel` | Frame | `Program.GetMainCamera().SetFollow(…)`; `Program.ControlledVehicle = vehicle` | `KSA/Program.cs` | Medium | ✅⁶ |
| `debug/focus` | `camera.focus` | Frame | (same as `ctl/focus` — **both** viewport cameras since C1.4) | `KSA/Program.cs`, `KSA/Viewport.cs` | Medium | ✅ |
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

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
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
(`WeldManager.Update`, anchoring `JobSystems.VehicleSolver.Wait()`) is **runtime coupling**, not a write
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

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
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

## IVA cabin physics — `IvaPhysicsManager` (Frame phase, vessel-agnostic) {#iva-physics}

Free-floating objects inside a vessel's interior (plans/IVA_MOVEMENTS.md), exposed **only** on gatOS
surfaces (9p `/sim/debug/iva/` + HTTP + MQTT — no ImGui beyond a perf readout). Part of the `debug.*`
namespace (`[control] debug_namespace`); authority-exempt like the rest of `/sim/debug`. Anchors all under
`gatOS.GameMod/Game/Ksa/Iva/`; `KsaCatalog.Iva` (a private dispatch method, taking an
`IvaPhysicsManager iva` ctor param) routes the seven actions **vessel-agnostically** — the object id
travels in `ordinal`, and `adopt`/`adopt_all` resolve the vessel from the command `Token` via the existing
`ResolveVehicle`.

**`debug.iva_physics` is the master switch and it defaults off.** While it is off the manager is an empty
registry: no `Simulation`, no `BufferPool`, no interior mesh, no per-frame work (`Mod.DriveIvaPhysics`
early-outs on `IvaPhysicsManager.IsIdle`), and no Bepu type is loaded — the Bepu fields live on `CabinSim`,
which is not constructed until the first adopt. Writing `0` releases every object at its exact rest pose
and disposes every simulation. This is the one switch a game update, a bug, or a suspicious player can use
to make the whole feature vanish.

**Why this needs no game-solver write at all.** gatOS runs its **own** `BepuPhysics.Simulation` in the
vessel assembly frame; the *write* into KSA is only the per-frame `Part.PositionParentAsmb` /
`Part.Asmb2ParentAsmb` assignment on driven **SubParts**. gatOS never adds a body to
`ConstraintSim`, never patches `NarrowPhaseCallbacks`, and installs **no Harmony patch** for this feature.
Rationale, with the decompiled evidence, is in [`ksa-runtime-coupling.md#iva-cabin-sim`](ksa-runtime-coupling.md#iva-cabin-sim).

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|---|
| `debug/iva/enabled` | `debug.iva_physics` | `IvaPhysicsManager.SetEnabled` | (registry + simulation lifecycle; the off path calls `RestoreRestPose` below) | — | Low | ✅ |
| `debug/iva/run_outside_iva` | `debug.iva_run_outside_iva` | `IvaPhysicsManager.SetRunOutsideIva` | (park-gate flag — no KSA write) | — | Low | ✅ |
| `debug/iva/adopt` | `debug.iva_adopt` | `IvaPhysicsManager.Adopt` (vessel from `Token`) | `Vehicle.{Id,Parts}`; `Part.{InstanceId,SubParts,PartParent,DisplayName,Template.Id,PositionParentAsmb,Asmb2ParentAsmb,Scale,Modules}`; `ModuleList.Get<PartModelModule>()`; `MeshReference.PositionCompare` (proxy sizing) | `KSA/Vehicle.cs`, `KSA/Part.cs`, `KSA/PartModelModule.cs`, `KSA/MeshReference.cs` | **Medium** | ✅ |
| `debug/iva/adopt_all` | `debug.iva_adopt_all` | `IvaPhysicsManager.AdoptAll` | as `adopt`, plus `PartModelModule.Template.{Internal,RayTracing}` (the interior-prop candidacy test) | `KSA/PartModelModule.cs` | **Medium** | ✅ |
| `debug/iva/<id>/release` | `debug.iva_release` | `IvaPhysicsManager.Release` (id in `ordinal`) | `Part.{PositionParentAsmb,Asmb2ParentAsmb}` (rest-pose restore) | `KSA/Part.cs` | Low | ✅ |
| `debug/iva/clear` | `debug.iva_clear` | `IvaPhysicsManager.Clear` (vessel-agnostic) | as `release`, for every object | `KSA/Part.cs` | Low | ✅ |
| `debug/iva/<id>/nudge` | `debug.iva_nudge` | `IvaPhysicsManager.Nudge` (id in `ordinal`) | (Bepu body velocity — no KSA write) | — | Low | ✅ |

**SubParts only, and this is binding.** `Part.GetReferenceWithChildren` writes a `Transform` element for
top-level parts but only `InstanceOf`/`LocalInstanceId`/`Stage`/`Sequence` for SubParts, so a displaced
SubPart physically **cannot** be serialized into a player's saved vehicle. `IvaPhysicsManager.FindSubPart`
therefore refuses to resolve a top-level part (`ENOENT` by design). Moving a SubPart also does not perturb
vehicle physics — mass properties and the collision compound are recomputed only from
`Vehicle.UpdateAfterPartTreeModification`, which gatOS never calls. **If a future KSA build starts
serializing SubPart transforms, this feature must be re-evaluated before shipping** — that is the single
highest-value break check on this page.

Driving `PositionParentAsmb`/`Asmb2ParentAsmb` per frame is KSA's **own** idiom for a runtime-animated
part transform (`KeyframeAnimationModule` and `SolarTracker` do exactly this off a stored rest pose); both
setters call `Part.ResetCachedPosMatrixValues`, and `PartModelModule.UpdateRenderData` re-reads the
transform every frame, so there is no dirty flag to defeat and rendering/lighting/ray-tracing/IVA gating
all follow for free. gatOS captures the rest pose into its **own** `FloatingObject` fields rather than
KSA's `PositionParentAsmbSafe`/`Asmb2ParentAsmbSafe` pair, so it cannot collide with the animation system.

The `debug/iva/{count,stats,interior}` and `…/<id>/{vessel,part,name,template,position,velocity,
angular_velocity,mass,shape,size,asleep,spec}` reads are a **game-free projection** of
`IvaPhysicsManager.Snapshot()` (`IvaSnapshot`/`IvaObjectSnapshot`/`IvaInteriorSnapshot`/`IvaStatsSnapshot`
records — no KSA read; `TelemetrySampler` projects it into `SimSnapshot.Iva`). Errnos: `EOPNOTSUPP` (master
switch off, or no CPU mesh to size a proxy from), `ENOENT` (vessel/subpart/object gone), `EBUSY` (per-vessel
cap, or already floating), `EINVAL` (bad arity/values, or larger than `iva_max_object_size`), `EIO` (the
cabin simulation is unavailable). Everything is **runtime-only** (never persisted); torn down on unload
(`Mod.TeardownGameCheats`). Anchors verified `2026-07-24` against `2026.7.9.5018`; **live in-game check
pending** (`docs/VALIDATION.md`). The new `BepuPhysics`/`BepuUtilities` DLL references:
[`ksa-assets-and-versions.md`](ksa-assets-and-versions.md).

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

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|---|
| `audio/play` | `audio.play` | `AudioActuator.Play` (+ `CreateOrGetSound` on first play of a clip version) | `GameAudio.System` (public static `FmodSystem`); `Fmod.TryCreateSound(bytes, Mode.OpenMemory\|_2d\|CreateSample/CreateCompressedSample, in CreateSoundExInfo{Length}, out Sound)` — the game's own in-memory recipe (`GameAudio.CreateFmodSound`); `Fmod.TryPlaySound(sound, group, paused:true, out Channel)`; `GameAudio.GetChannelGroup(ChannelGroupType.{Sfx,Music,Ui})`; `Channel.TrySet{Position,Mode,LoopCount,LoopPoints,Volume,Pan,Pitch,Paused}`; `Sound.TryGetLength` | `KSA/GameAudio.cs`, `KSA/ChannelGroupType.cs`, `Brutal.FmodApi/Fmod.cs`, `Brutal.FmodApi/Mode.cs` | Low (FMOD Core P/Invoke surface is upstream-stable; `GameAudio.System`/`GetChannelGroup` are plain public statics) | ✅ |
| `audio/set` | `audio.set` | `AudioActuator.Set` | `Channel.TrySet{Volume,Pan,Pitch,Paused,Position}` | `Brutal.FmodApi/Fmod.cs` | Low | ✅ |
| `audio/stop` | `audio.stop` | `AudioActuator.Stop` | `Channel.TryStop` | `Brutal.FmodApi/Fmod.cs` | Low | ✅ |

**Runtime coupling beyond the writes:** the per-frame tick (`Mod.DriveAudio` → `AudioActuator.Tick`,
`OnBeforeUi` right after the command drain — the same thread that pumps `GameAudio.UpdateAudio` /
`System.Update()`) prunes finished channels (`Channel.TryIsPlaying`), enforces `end=`
(`Channel.TryGetPosition`), releases evicted FMOD sounds (`Sound.TryRelease` — deferred: never while
a channel plays), publishes the `/sim/audio/status` snapshot into the store, and stamps
`audio.finished` events with `Universe.GetElapsedSeconds()`. gatOS never calls
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

## FX editors — engineplume / plumetrail / clouds / terrain (Frame phase, vessel-agnostic) {#fx-editors}

`Game/Ksa/Fx/{FxReflect,PlumeActuator,TrailActuator,CloudActuator,TerrainActuator,FxPristine}.cs`
(plans/FX_EDITORS_PLAN.md; issue #2). The game's four built-in imgui render editors — "Volumetric
Exhausts", "Plume Trails", "Clouds", "Terrain Editor" — exposed as `/sim` filesystems, one writable leaf
per knob. Part of `debug.*` (gated by `[control] debug_namespace`); authority-exempt like the rest of
`/sim/debug`. `KsaCatalog` routes all four families **vessel-agnostically before** vehicle resolution
(the thug_life precedent) — the addressed entity is a render template, the one trail renderer, or a
celestial body, never a vehicle. The field tables, ranges, tree and parse-time validation are
**game-free** (`gatOS.SimFs/Fx/FxCatalog.cs`); `KsaCatalog.FxField` re-validates game-side (arity, range,
finiteness) because `POST /v1/command` / `gatos/command` bypass the 9p parse. Every `*_set` carries the
entity in `Token` and the **concrete field path** in `Aux`.

> **Version tick:** this feature was born on **`2026.7.10.5056`** — every member below was located and
> verified in that build's decomp on **2026-08-01**, so its column reads `5056` (the rest of this page
> ticks the earlier playbook passes, most recently `5018`; the next full pass will fold these rows in).
> ✅ = verified present with the stated semantics on 5056.

**engineplume** — scope is **per template** (shared: one edit repaints every nozzle referencing it):

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 5056 |
|---|---|---|---|---|---|---|
| `debug/engineplume/templates/<id>/**` | `debug.engineplume_set` | Frame | `PlumeActuator.TryWrite` → `VolumetricExhaustTemplate.{LengthWeights,Absorption,Emission,Noise,Quality}`: `DoubleReference.Value` / `BoolReference.Value` in place; `ColorGradient.Color0..3 = new ColorRgbReference(float3)` + `OnDataLoad(Mod.Empty)` | `KSA/VolumetricExhaustTemplate.cs`, `KSA/VolumetricExhaustRenderer.cs:2052-2290` (the editor's write sites) | **High** | ✅ |
| (apply after every write) | — | Frame | `PlumeActuator.Propagate` → `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.RocketNozzles.ModulesAndAllStates`; `RocketNozzleFxState.VolumetricExhaust`; `VolumetricExhaustInstance.{OnSettingsChanged,UpdateModifiers}`; `RocketNozzle.RecomputeGasVisibilityDensity(in …)` | `KSA/VolumetricExhaustRenderer.cs:2316-2337`, `KSA/VolumetricExhaustInstance.cs:179,231`, `KSA/RocketNozzle.cs:156` | **High** | ✅ |
| (id resolution) | — | — | `VolumetricExhaustTemplate.Get(string)` (public static; null ⇒ `ENOENT`) | `KSA/VolumetricExhaustTemplate.cs:48` | Medium | ✅ |
| (propagation args) | — | — | `FxReflect.PlumeModifierArgs` → `Program.VolumetricExhaustRenderer` (public static) + reflected `_currentAtmosphericPressure` / `_debugThrottle` — **best-effort**, falls back to `(0, 1)` | `KSA/VolumetricExhaustRenderer.cs:253,277`, `KSA/Program.cs:421` | **High** | ✅ |
| `debug/engineplume/templates/<id>/reset` | `debug.engineplume_reset` | Frame | `FxPristine.Restore` replays the captured values through `TryWrite`, then `Propagate` | — | **High** | ✅ |

**plumetrail** — scope is the **one global renderer**; all exposed settings are public instance fields the
renderer re-reads every frame, so a write needs **no apply call**:

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 5056 |
|---|---|---|---|---|---|---|
| `debug/plumetrail/render/*` | `debug.plumetrail_set` | Frame | `TrailActuator.TryWrite` → `VolumetricTrailRenderer.{MaxDistance,VoxelDepthFirstSliceThickness,MinStepSize,StepSizeDistanceScale,ErosionMaxDepth,ErosionEdgeSharpness,SelfShadowStepCount,LightBrightness,SkyAmbientBrightness,DebugTrailColor}` (public `float`/`int`/`float4` fields) | `KSA/VolumetricTrailRenderer.cs:172-194` | Medium | ✅ |
| `debug/plumetrail/render/expansion_time` | `debug.plumetrail_set` | Frame | `TrailActuator.TryWrite` → `FxReflect.TrailSettings` → `PlumeTrailSettings.ExpansionTimeSeconds` (**two private hops**) | `KSA/VolumetricTrailRenderer.cs:166` → `KSA/PlumeTrailSegmentsManager.cs:19` → `KSA/PlumeTrailSettings.cs:11` | **High** | ⚠️ **5117: moved off the renderer** (revs 5059/5097), re-bound — see [5117 findings](#5117-findings) |
| (renderer handle) | — | — | `FxReflect.Trail` → reflected `Program.Instance._volumetricTrailRenderer` (the only handle; latch `fx.trail_renderer`) | `KSA/Program.cs:160` | **High** | ✅ |
| `debug/plumetrail/clear` | `debug.plumetrail_clear` | Frame | `Program.Instance.ClearPlumeTrails()` → `VolumetricTrailRenderer.ClearPlumeTrails()` | `KSA/Program.cs:4610`, `KSA/VolumetricTrailRenderer.cs:259` | Medium | ✅¹ |
| `debug/plumetrail/reset` | `debug.plumetrail_reset` | Frame | `FxPristine.Restore` replays through `TryWrite` | — | Medium | ✅ |

**clouds** — scope is **per body → per layer → per cloud type**; the data write needs **no** reflection
(only the render-side apply does):

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 5056 |
|---|---|---|---|---|---|---|
| `debug/clouds/bodies/<id>/**` | `debug.clouds_set` | Frame | `CloudActuator.TryWrite` → `CloudsReference.{OrbitTransitionStartAltitude,OrbitTransitionEndAltitude,MaxShadowsAltitude,Layers}`, `CloudLayerReference.{RotationSpeed,VolumetricCloud,TwoDimensionalCloud}`, `VolumetricCloudReference.{Detail.Size,ColorRgb,Noise.ScrollSpeed,Raymarching,CloudTypes}`, `RaymarchingReference.{Step.{Size,Scale,Maximum},LightDistance,LightSamples}`, `CloudTypeReference.{StartAltitude,Height,Density,EdgeSharpness,MultipleScatteringBrightness,CloudShape.InterpolateShapes}` — `DistanceReference`/`Vector3Reference`/`ColorRgbReference` **construct-new**, `DoubleReference.Value`/`Step.Scale`/`InterpolateShapes` in place | `KSA/CloudsReference.cs`, `CloudLayerReference.cs`, `VolumetricCloudReference.cs`, `TwoDimensionalCloudReference.cs`, `RaymarchingReference.cs`, `CloudTypeReference.cs`, `CloudShapeReference.cs`; editor at `KSA.Atmosphere.Rendering/CloudRenderer.cs:1370-1560` | **High** | ✅² |
| (body resolution) | — | — | `AtmosphericBody.BodyTemplate.CloudsReference` over `Universe.CurrentSystem.All.UnsafeAsList()` | `KSA/AstronomicalTemplate.cs:60`, `KSA/Universe.cs` | Low | ✅ |
| (apply after every write) | — | Frame | `CloudActuator.Apply` → `CloudLayerReference.OnDataLoad(Mod.Empty)`; `CloudRenderer._planetToCloudRenderData` (**public**) keyed on `Astronomical.Hash`; `CloudLayerRenderData.UpdateStaticData(Renderer, AtmosphericBody, CloudLayerReference, float, float, float)`; `CloudShadowsRenderer.PopulatePlanets(…, RenderTarget)` | `KSA.Atmosphere.Rendering/CloudRenderer.cs:1570-1595`, `CloudLayerRenderData.cs:347`, `CloudShadowsRenderer.cs:76` | **High** | ✅ |
| (renderer + apply handles) | — | — | `FxReflect.Clouds` → reflected `Program.Instance._planetTransparenciesRenderer` → `GetCloudRenderer()` (public) — latch `fx.cloud_renderer`; `FxReflect.CloudApply` → reflected `CloudRenderer._renderer` / `_cloudShadowsRenderer` / `_worleyNoise3dTarget` — latch `fx.cloud_apply` | `KSA/Program.cs:152`, `KSA/PlanetTransparenciesRenderer.cs:87`, `KSA.Atmosphere.Rendering/CloudRenderer.cs:95,151,235` | **High** | ✅ |
| `debug/clouds/bodies/<id>/reset` | `debug.clouds_reset` | Frame | `FxPristine.Restore` + `Apply(layer: -1)` (re-uploads every layer) | — | **High** | ✅ |

**terrain** — two tiers: a reflection-free **global** toggle, and per-body **paired** writes:

| `/sim` path | action key | phase | KSA member | Decomp file | Risk | 5056 |
|---|---|---|---|---|---|---|
| `debug/terrain/wireframe` | `debug.terrain_set` (token `""`) | Frame | `PlanetRenderer.Wireframe` (public **instance** field) via `Program.GetPlanetRenderer()` | `KSA/PlanetRenderer.cs:216`, `KSA/Program.cs:491` | Medium | ✅³ |
| `debug/terrain/bodies/<id>/**` | `debug.terrain_set` | Frame | `TerrainActuator.Write` → `Celestial.BodyTemplate.HeightReference.{Minimum,Maximum}` and `BodyTemplate.TerrainReference.BiomeMaterials.{BlendStrength.Value,DetailFadeInStart,DetailFadeInEnd}` (construct-new `DistanceReference`) **plus** the `PlanetUbo`/`MeshUbo` structs at `(NumCelestials*frame + slot)*Stride`, then the frame-in-flight mirror copy | `KSA/PlanetRenderer.cs:2107-2398` (the editor's write + mirror loop), `KSA/AstronomicalTemplate.cs:27,51`, `KSA/BiomeMaterialsReference.cs` | **High** | ✅⁴ |
| (slot resolution) | — | — | `PlanetRenderer.RenderUboSlot(Celestial)` / `MeshUboSlot(Celestial)` (public; `-1` ⇒ no slot ⇒ the body is absent from the tree) | `KSA/PlanetRenderer.cs:374,379` | Medium | ✅ |
| (UBO handles) | — | — | `FxReflect.TerrainUbo` → reflected `PlanetRenderer._renderUboMap` / `_meshUboMap` (`MappedMemory`, host-visible + coherent) with the public `PlanetUboStride`/`MeshUboStride`/`NumCelestials` and `Program.GetRenderer().MaxFramesInFlight` — latch `fx.terrain_ubo` | `KSA/PlanetRenderer.cs:250-252` | **High** | ✅ |
| `debug/terrain/bodies/<id>/reset` | `debug.terrain_reset` | Frame | `FxPristine.Restore` replays through the same paired write | — | **High** | ✅ |

**Discrepancies found while implementing (the things a future break-check must not re-assume).** The
design plan (`plans/FX_EDITORS_PLAN.md`) was written from a first read of the decomp; the code below is
what 5056 actually exposes, and the code wins:

¹ **`ClearPlumeTrails` is a public *instance* method on `Program`, not static** (plan §3 said static) —
reached through the public `Program.Instance`, so no reflection is involved.

² **`CloudTypes` hangs off `layer.VolumetricCloud`**, not off the layer itself; likewise `Detail.Size`,
`ColorRgb`, `Noise.ScrollSpeed` and `Raymarching`. A cloud-type index is therefore only addressable when
the layer has a volumetric cloud. **`NoiseScale` is deliberately unexposed** — it would force
`CloudRenderer.RecreateLayerPipelines()` (private; destroys/rebuilds Vulkan pipelines), so gatOS's apply
path can never rebuild a pipeline.

³ **`PlanetRenderer.Wireframe` is a plain public *instance* field, not static** (plan §5 said
"static-ish") — reached through `Program.GetPlanetRenderer()`. Zero reflection either way.

⁴ **`PlanetUbo.TanMeanSlopeRoughnessRadians` stores plain radians despite the `Tan` prefix** (the game's
editor writes `deg × π/180` into it), so the `slope_roughness_deg` leaf converts on both sides. And the
plan's §5 directive to prefer a public repopulate over raw UBO writes was **investigated and rejected**:
`PlanetRenderer` has no public repopulate/invalidate that re-derives a body's UBO from its reference
objects — the two population loops the plan pointed at (`:684-720`, `:1086-1114`) are inline
**constructor** code that also reallocates descriptor sets. The paired write + mirror loop
(`:2388-2398`) is implemented faithfully instead; **skipping the mirror makes the change flicker**,
appearing only on frames that sample slot 0.

**Degraded behavior (per-capability health latches).** Latch keys: `fx.trail_renderer`,
`fx.plume_templates`, `fx.cloud_renderer`, `fx.cloud_apply`, `fx.terrain_renderer`, `fx.terrain_ubo` (all
surfaced in `/sim/status/accessors`). A degraded `fx.trail_renderer`/`fx.terrain_renderer` answers
`EOPNOTSUPP`; a degraded `fx.cloud_apply`/`fx.cloud_renderer` **still performs the data write and still
returns `Ok`** (only the immediate GPU re-upload is skipped — the next natural repopulate picks it up); a
degraded `fx.terrain_ubo` **empties the per-body terrain roster** (values are read out of the UBO) while
the global `wireframe` leaf stays live; a degraded `fx.plume_templates` falls back to harvesting template
ids off live nozzles. Errnos: `EINVAL` (unknown field path, wrong arity/range/non-finite, or a global
field addressed per body and vice-versa), `ENOENT` (unknown template/body, no terrain render slot,
out-of-range layer/cloud-type index), `EOPNOTSUPP` (latched), `EIO` (KSA threw).

**Reset / teardown.** Every actuator captures a field's **pristine value on the first gatOS write to it**
(read back through the same accessor it writes through) into `FxPristine`; `*_reset` replays those
captures through the normal write path — so a restore runs the exact propagation/apply a set does — and
drops them (reset with nothing captured = `Ok` no-op). Captures are **runtime-only** (session-scoped like
all of `/sim/debug`); `Mod.TeardownGameCheats` calls `FxPristine.RestoreAll()` **first**, while the game
reads are still live, then `FxEditorReader.Reset()`. The feature installs **no Harmony patch**, runs
**no per-frame driver**, and owns **no GPU resources** — the read side is
[`ksa-read-surface.md#fx-editors`](ksa-read-surface.md#fx-editors), the reflection lifecycle is
[`ksa-runtime-coupling.md#fx-accessors`](ksa-runtime-coupling.md#fx-accessors). In-game pass pending
(`docs/VALIDATION.md`).

---

## Camera director — `CameraDirector` (Frame phase, vessel-agnostic) {#camera-director}

`Game/Ksa/Camera/{CameraDirector,CameraFrames,CameraTargets,CameraReader}.cs` over the game-free
`gatOS.SimFs/Camera/**` (plans/CAMERA_ASBUILT.md; CAMERA_CONTROLS_PLAN tasks C0–C5). **Not** part of
`debug.*`: gated by `[camera] camera_enabled` (off ⇒ the whole `/sim/camera` surface vanishes and
`camera.*` answers `EOPNOTSUPP`), plus the `control_enabled` master like every write. Vessel-agnostic —
`KsaCatalog` routes the whole `camera.*` family through one `Camera(SimCommand)` sub-dispatcher
**before** vehicle resolution and **before** the authority gate (the addressed entity is a target
reference or the camera itself, never the controlled vehicle), so it is authority-exempt exactly as
`camera.focus` always was. All 28 action keys are **Frame** phase; none is in
`SimCommand.SolverActions`. Two further gates apply to two sub-families: the interpolated `time`
channel needs **both** `[control] debug_namespace` **and** `[camera] camera_allow_time_channel` (a
closed gate is *ignored with a one-shot warning*, not an error — one warning per ownership session, so
a 60 Hz driver cannot fill the log), and `camera.play`/`set`/`stop` need `[schedule] schedule_enabled`,
because a camera track **is** a `/sim/ctl/schedules` entry (`kind = camera-track`) and without the
registry there is nowhere to put a player — those three answer **`EOPNOTSUPP`** naming the flag, while
every L1/L2 channel keeps working.

**Ownership is what makes the whole feature patch-free.** `Take()` parks `Viewport.Mode =
CameraMode.Fixed` **by direct field assignment** (so `FixedController.OnSwitchOn`'s
`TimedAlert("Fixed Camera")` never draws) and calls `Unfollow(changeControl:false)`. With
`Following == null`, `FixedController.OnFrame`'s entire body is skipped, so the game's camera solver
writes nothing and gatOS — writing at the end of `OnAfterFrame` — is the sole writer of the transform
the next frame's `Program.OnFrameViewports` rebuilds every matrix from. **Consequence to know: while
gatOS owns the camera the player's camera keys do nothing** (a per-frame re-assert undoes any
`Viewport.Mode`/`Following` change), and `camera/mode`, `camera/follow` and `camera/tidal` are refused
(`EOPNOTSUPP`, with a message naming `pose/anchor` + `pose/aim_target` or `camera/enabled 0`).

| `/sim` path | action key | actuator | KSA member | Decomp file | Risk | 5168 |
|---|---|---|---|---|---|---|
| `camera/enabled` (`1`) | `camera.enabled` | `CameraDirector.Take` | `Program.MainViewport`; `Viewport.{Mode,GetCamera,SetCameraMode,BaseCamera}`; `Camera.{PositionEcl,LocalPosition,LocalRotation,NoRotation,Following,TidalLocking,GetFieldOfView,Orthographic,Unfollow}`. Capture comes off `GetCamera()` (which is `MapCamera` in Map mode), FOV converted radians→degrees once here | `KSA/Program.cs` (`:437`), `KSA/Viewport.cs`, `KSA/Camera.cs` | Medium | ➕ new 2026-08-06 |
| `camera/enabled` (`0`), `camera/release` | `camera.enabled` / `camera.release` | `CameraDirector.{Release,Restore}` | `Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,SetOrthographic}`; `Viewport.{Mode,SetCameraMode}`; `Universe.SetSimulationSpeed` (only if the time channel captured). `SetFollow(saved, tidal, changeControl:false, alert:false)` goes **first** because it teleports; mode **last**, direct assignment except restoring *into* Map | `KSA/Camera.cs`, `KSA/Viewport.cs`, `KSA/Universe.cs` | Medium | ➕ new 2026-08-06 |
| (per-frame pose write) | — | `CameraDirector.Apply` | `Camera.{PositionEcl,LocalRotation,LookAtRotation,SetFieldOfView,SetOrthographic,SetOrthoHalfHeight}`. `LocalRotation` — **not** `WorldRotation` — is what `Camera.OnFrame` inverts into `Ego2View`. `SetFieldOfView` takes **degrees**, does **not** clamp (fisheye/telephoto beyond the game's own 15–120 are reachable) and rebuilds+inverts the projection every call, so it is written only on change | `KSA/Camera.cs` | Medium | ➕ new 2026-08-06 |
| (track `time` channel, C4) | — | `CameraDirector.ApplyTimeScale` | `Universe.SetSimulationSpeed(double, alert:false)` (`:1998`), `Universe.GetSimulationSpeed()` (`:2021`), `Universe.IsAutoWarpActive` (`:96`). `alert:false` is load-bearing — the default draws a speed `TimedAlert`, i.e. *in the footage*. Speed is captured **lazily** (first frame the channel is actually driven) and restored only if captured | `KSA/Universe.cs` | Medium | ➕ new 2026-08-06 |
| (release blend) | — | `CameraDirector.RestorePositionEcl` | the `Camera.PositionCce` composition (`LocalPosition` ⇄ `IFollowable.GetBodyFixed2Ecl()` unless `NoRotation`); `IPosition.GetPositionEcl()` — **reproduced**, because the camera being blended is already unfollowed and its own `PositionCce` would not use the captured target. Recomputed every blend frame, so blending back onto a moving follow target lands on the target | `KSA/Camera.cs` | Medium | ➕ new 2026-08-06 |
| `camera/mode` | `camera.mode` | `CameraDirector.SetMode` | `Program.MainViewport`; `Viewport.SetCameraMode(CameraMode)` — which also calls `Program.ControlledVehicle?.ClearHeldPlayerInput()`, dropping latched `ctl/translate`+`ctl/rotate` flags (SPEC §3.4.19) | `KSA/Program.cs`, `KSA/Viewport.cs` | Medium | ➕ new 2026-08-06 |
| `camera/follow` | `camera.follow` | `CameraDirector.SetFollow` | `Program.MainViewport.{BaseCamera,MapCamera}`; `Camera.{SetFollow,Unfollow}` — **both** cameras (the C1.4 rule). Keeps `SetFollow`'s `target + 2.5×MeanRadius×forward` teleport; preserves the current tidal flag; a `part:` reference is `EOPNOTSUPP` (the game follows a whole `IFollowable`) | `KSA/Viewport.cs`, `KSA/Camera.cs`, `KSA/InputEvents.cs` | Medium | ➕ new 2026-08-06 |
| `camera/tidal` | `camera.tidal` | `CameraDirector.SetTidal` | `Camera.{Following,TidalLocking,SetFollow,PositionEcl}` — `TidalLocking` is get-only (`=> _tidalLocking`) and `SetFollow` is its only writer, so the flag change re-issues `SetFollow` and then **re-asserts the captured `PositionEcl`** to undo its unconditional teleport | `KSA/Camera.cs` | Medium | ➕ new 2026-08-06 |
| `camera/map/scope` | `camera.map_scope` | `CameraDirector.SetMapScope` | `Program.MainViewport.MapController`; `MapController.Scope` (`:33`, a plain public `double`, no setter hook). **Not ownership-gated** — like `mode`/`follow`/`tidal` it configures the *game's* camera | `KSA/Viewport.cs`, `KSA/MapController.cs` | Medium | ➕ new 2026-08-06 |
| `camera/pose/{position,frame,orbit/*,aim_frame}` | `camera.{position,frame,orbit_radius,orbit_azimuth,orbit_elevation,aim_frame}` | `CameraFrames.TryFrame2Ecl` | `Vehicle.{GetEnu2Cce,GetLvlh2Cce,Body2Cce,ComputeEnu2Cce,ComputeLvlh2Cce,GetPositionCce,GetVelocityCce}`; `Celestial.{GetCci2Cce,GetCcf2Cce,GetDirCcfFromLatLon,MeanRadius,GetPositionCce,GetVelocityCce}`. `GetEnu2Cce`/`GetLvlh2Cce` are **nullable** and `GetEnu2Cce` dereferences `Orbit.Parent` unguarded — both guarded | `KSA/Vehicle.cs`, `KSA/Celestial.cs` | Medium | ➕ new 2026-08-06 |
| `camera/pose/geo` | `camera.geo` | `CameraFrames.GeoToEcl` | `Celestial.{GetDirCcfFromLatLon,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius,GetPositionEclFromCce}` — gatOS **calls** the game's own lat/lon trigonometry rather than restating it (CCF +Z = north pole, +X = prime meridian on the equator) | `KSA/Celestial.cs` (`:674`), `KSA/Camera.cs` (`SetLatLon` is the model) | Medium | ➕ new 2026-08-06 |

**Runtime coupling beyond the writes:** the per-frame driver (`CameraViewportPatch` →
`Mod.PrepareMainViewportFrame` → `CameraDirector.Update`) is a guarded prefix on the main
`Viewport.OnFrame(double)`. It advances the shared schedule clock, drains due/direct camera commands,
composes `Track ?? Override ?? Baseline`, resolves the current-frame placement + aim, and writes the
pose immediately before the original method runs `Camera.OnFrame`; a postfix publishes KSA's final
clamped transform. gatOS touches no matrix. Anchored placement smooths its relative component while
live anchor translation and aim pass through exactly — lifecycle detail in
[`ksa-runtime-coupling.md#camera-driver`](ksa-runtime-coupling.md#camera-driver). It self-gates to one
branch (`CameraDirector.IsIdle`) while gatOS does not own the camera, which is the default. The read
half (`CameraReader.Sample` → the volatile `CameraStore.Status` every leaf renders from) is
[`ksa-read-surface.md#camera`](ksa-read-surface.md#camera); the store, the compositor, the grammars,
the caps and the whole JSON track parser/evaluator/player are **game-free**
([`non-ksa-surface.md#camera-game-free`](non-ksa-surface.md#camera-game-free)). Despawn pruning
(`CameraDirector.Prune`) rides the sampler's vehicle enumeration beside `VesselForceRender.Prune`, and
`camera.shot`/`camera.finished` events drain into the snapshot beside audio's, IVA's and the
scheduler's. Errnos: `EINVAL` (grammar/range), `ENOENT` (a `vessel:`/`body:`/`part:` reference that is
not live — validated **at write time**, so an anchor cannot be pre-armed for an unspawned vessel),
`EOPNOTSUPP` (an unresolvable frame, a control refused while owned, a `part:` follow, a closed gate),
`EIO` (KSA threw — latches the action). **Behaviours a live pass must confirm, all of them inherited
from the game:** `Camera.ClampCamera()` still runs at the top of every `Camera.OnFrame` and pushes the
camera to *surface + 0.5 m*, so a `pose/geo` altitude below 0.5 m is silently corrected; `MapController`
clamps `Scope` **up** to `Camera.Following.MeanRadius` every map frame and `SetDefaults()` recomputes it
wholesale after a focus change; `Universe.SetSimulationSpeed` does **not** check `IsAutoWarpActive`, so
a driven `time` channel *fights* an active auto-warp (deliberately unguarded — stop the auto-warp before
rolling the shot); and **`ortho_height` cannot be restored** — `Camera` exposes `SetOrthoHalfHeight` but
has **no public getter in 5168**, so there is nothing to capture at take and nothing to put back at
release. It is therefore written only when its channel is explicitly claimed. Anchors verified
`2026-08-06` against `2026.8.5.5168`; in-game pass pending (`docs/VALIDATION.md`).

**Not implemented, and not implementable without a Harmony patch: IVA and Map as ownership contexts**
(tasks C5.1 / the parking half of C5.2). This is a **finding**, not a TODO — see the evidence in
[`ksa-runtime-coupling.md#camera-mode-contexts`](ksa-runtime-coupling.md#camera-mode-contexts). What
C5.2 *does* ship is the `map/scope` row above.

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

## ✅ 5261 write-surface findings (playbook pass 2026-08-11) {#5261-findings}

Full playbook pass 2026-08-11, `2026.8.5.5168` → `2026.8.19.5261` (revs 5169–5258, 90 commits).
**No silent write-surface break.** One compile-visible type migration touched a write path, and the
control-lockout divergence first recorded at 4939 widened. Build + full suite green (0 warnings,
1317 passed).

1. **`SimTime` → `UniverseTime` reaches one write path (rev 5211).** `BurnTarget.ImpulsiveInstant`
   is now `UniverseTime` (Int128 nanoseconds, default `EndOfTime` instead of `PositiveInfinity`), so
   `ctl/burn`'s `new SimTime(ut)` became `new UniverseTime(ut)`. **This is not a pure rename:**
   `UniverseTime`'s constructor **throws `ArgumentException` on NaN**, where `SimTime` silently stored
   it. A guest writing a non-finite `ut` would therefore have thrown inside the actuator and latched it
   **degraded** (`EOPNOTSUPP` at `/sim/status/accessors`) rather than being rejected. **Closed** —
   `FlightComputerActuator.SetBurn` now rejects a non-finite `ut` with `Invalid` before constructing
   the time, matching the finite-component check `debug/teleport` already had.
2. **Rev 5252/5253 widen the control-module lockout — still UI-only, so `/sim` writes are unaffected
   and the 4939 divergence grows.** `ControlsLockout` now also gates **engine shutdown (X)** and, via
   an early `IsLockedOutNoControl` return in **`Vehicle.OnKey`**, *all* keyboard vehicle input on a
   vessel with no control module. The gate sits in the **player key handler**, which returns before
   emitting any `InputEvents` — gatOS never calls `OnKey`/`OnAction`, writing instead straight into
   `Vehicle._manualControlInputs` (reflection), the `InputEvents.*Buffer`s, or module methods. So
   `/sim` still actuates a control-less vessel where the stock UI now refuses even more than at 4939.
   `Vehicle.IsControllable => _overrideIsControllable || Parts.Controls.NumModules > 0` is
   **unchanged**, so `vessels/<id>/controllable` stays truthful and guests can still pre-check.
   **No code change** — documented behavior, flagged for the live pass.
3. **Teleport orientation (rev 5226) does not reach `/sim`.** KSA changed the *default* orientation for
   surface-teleported/launched vehicles ("pitch down is north"). `Vehicle.Teleport(Orbit?, doubleQuat?,
   double3?)` keeps its signature **and** its null semantics (`body2Cce == null` ⇒ leave attitude
   unchanged), and `debug/teleport` passes null — so the new default applies to KSA's own launch path
   only. **No code change.**
4. **Harmony hook targets intact**: `Universe.ExecuteNextVehicleSolvers(double, SimStep)`
   (`Universe.cs:1796`, signature unchanged through the whole 5208–5216 threading rework);
   `Program.DrawProgramMenusHook()` (`Program.cs:3669`); `Program.RenderGame(AcquiredFrame, double)`
   (`:4206`, the one string-resolved target); `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`
   (`:332`); `Vehicle.GetWorldMatrix(Camera)` (`:3516`) / `Vehicle.UpdateRenderData(Viewport,int)`
   (`:3529`); `Viewport.OnFrame(double)`; `PartModel.AddInstance(PerInstanceData,Viewport,int)`;
   `ModLibrary.Find`; `Program.MainViewport`. **Every signature is byte-identical across the two builds.**
5. **`JobSystems.VehicleSolvers` → `VehicleSolver` (revs 5208–5216)** — the vehicle update moved to a
   single orchestrator scheduler plus a new `DynamicWorkerPool VehicleWorkerPool`. The welds/IVA drain
   is a **rename, not a rearchitecture**: waiting the orchestrator still covers the pool (joined inside
   `VehicleUpdateTask` by a `using`-scoped `ParallelBatch`), and KSA's own `PrepareFrame` drains
   identically. ⚠️ `Vehicle.Teleport` now detaches through the **object-pooled** `PhysicsBubble`
   (`RemoveFromBubble`, revs 5215/5220/5237) — the per-frame weld teleport therefore drives a pooled
   bubble split/merge every tick. Compile-clean and logically sound, but **live-only** to confirm.

---

## ⚠️ 5168 write-surface findings (playbook pass 2026-08-05) {#5168-findings}

Full playbook pass 2026-08-05, `2026.8.3.5117` → `2026.8.5.5168` (revs 5118–5168, 49 commits).
**Three silent semantic breaks on the write surface — every one of them a case where a gatOS command
still "succeeded" while the game ignored it.** Two are now enforced with a real errno / exposed as a
control; the third is inherent to the game and is documented as a contract change. Build + suite green
(0 warnings, 772 passed).

1. **`RCSMode` now gates manual RCS (rev 5143)** — footnote ⁷ above. `ctl/translate`/`ctl/rotate`
   silently do nothing while RCS is off. **Closed** by adding `ctl/rcs_mode` (`vessel.rcs_mode`,
   **Solver** phase). This also **falsifies** the 4980 note (⁶a) that manual flags were ungated —
   corrected in place.
2. **The game clears latched thruster flags (rev 5128)** — footnote ⁸ above. `Vehicle.ClearHeldPlayerInput()`
   fires on vessel switch, window focus loss, camera-mode switch, and *every update* while ImGui holds
   keyboard focus or warp > 30×. **No code change** (this is the game's prerogative), but gatOS's
   documented "latches until rewritten" contract for `ctl/translate`/`ctl/rotate` is now conditional
   and both SPEC §3.9 and the tutorials say so.
3. **Disabled decouplers (rev 5132)** — footnote ⁹ above. **Closed**: `decoupler.fire` returns
   **EOPNOTSUPP** for a disabled decoupler instead of a false `Ok`, and `decouplers/<n>/enabled` is
   readable.

**Render write path (rev 5154) — migrated, not broken.** `Program.OffScreenPass` was deleted along with
the whole `RenderPassState`/`OffscreenTarget`/`RenderTarget`/`Framebuffer` set when offscreen rendering
moved to Vulkan dynamic rendering. `ThugLifeQuadRenderer.BuildPipeline` now calls
`Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` — the same call KSA's own
`GenericMeshRenderer`/`PartModelRenderer`/`PartModelGlass` make — which supplies the attachment formats,
nulls the render pass and sets the sample count from the target, so the quad follows the engine's MSAA
(and the new CMAA2 option, rev 5156) automatically. `SuperMeshRenderSystem.RenderMainPass` is unchanged
and is still invoked inside the offscreen target's `BeginRendering`/`EndRendering` scope, so the postfix
still draws into the scene. **Still requires a live in-game pass** — a mis-drawn quad is exactly what
static review cannot catch.

**Verified clean:** all 13 reflection accessors and all 7 Harmony targets resolve;
`Vehicle._manualControlInputs` and the `ThrusterCommandFlags`/`EngineThrottle` fields are unchanged
(`ManualControlInputs` gained only `Sprint`, which the box-mutate-write-back pattern preserves);
`ClearHeldPlayerInput` touches `_engineFlags` (the keyboard throttle-ramp state) but **not**
`EngineOn`/`EngineThrottle`, so `ctl/ignite` and `ctl/throttle` are unaffected.

---

## ⚠️ 5117 write-surface findings (playbook pass 2026-08-01) {#5117-findings}

Full playbook pass 2026-08-01, `2026.7.9.5018` → `2026.8.3.5117` (revs 5019–5116 — the pass spans the
un-audited 5056 drop as well). **One compile break, fixed; every other actuator, Harmony hook and
reflection accessor clean.** Build + full test suite green against the 5117 DLLs (0 warnings,
769 passed / 11 skipped).

- **⚠️ COMPILE BREAK, FIXED — `VolumetricTrailRenderer.ExpansionTimeSeconds` removed (revs 5059/5097).**
  The volumetric-trail subsystem was refactored and split (`PlumeSegmentStore`, `PlumeSegmentMaintenance`,
  `PlumeTimingProfile`, `PlumeTrailSettings`, `PlumeTrailUploadBuilder`, `PlumeTrailEmitterTracker`,
  `TrailCursor*` are all new files); `ExpansionTimeSeconds` moved off the renderer onto the new
  `PlumeTrailSettings`, with the **same default (`5f`) and the same meaning**. The renderer's other ten
  public fields gatOS binds are untouched.

  **Re-bound rather than dropped**, per the standing rule that gatOS should expose what the game's own
  debug windows expose, by the same route they use: the built-in **Plume Trails** window still shows
  `expansionTime` in its "Profile" section — `VolumetricTrailRenderer.OnDrawUi` now delegates that
  section to `PlumeTrailSegmentsManager.OnDrawProfileUi()`, which draws `_settings.ExpansionTimeSeconds`.
  gatOS reaches the identical object via the new `FxReflect.TrailSettings` accessor (two private hops:
  `VolumetricTrailRenderer._plumeTrailSegmentsManager` → `PlumeTrailSegmentsManager._settings`).
  It carries its own health latch (`fx.trail_settings`), so a future move degrades
  `render/expansion_time` **alone** to `EOPNOTSUPP` and leaves the other ten fields healthy.
  No SPEC change — the `/sim` path, type, range and action key are all unchanged.
- **✅ All eight Harmony hook targets intact** — re-checked by hand against the 5117 decomp, since a
  renamed target fails at patch-install time rather than at compile: `Universe.ExecuteNextVehicleSolvers`
  (`:1784`), `Program.DrawProgramMenusHook` (`:3622`), `Program.RenderGame` (`:4173`),
  `Vehicle.GetWorldMatrix` (`:3428`) / `UpdateRenderData` (`:3441`),
  `SuperMeshRenderSystem.RenderMainPass` (`:329`), `PartModel` ctor / `AddInstance` (`:375`).
- **✅ All reflection accessors intact** — the compiler-blind set, re-verified member-by-member:
  `Vehicle._manualControlInputs` → `ManualControlInputs{EngineOn,EngineThrottle,ThrusterCommandFlags}`
  (throttle/rotate/translate), the `KittenEva._renderable` → `KittenRenderable._characterAvatar` → `.Core`
  scale chain, the light-template clone path, and all six FX handles
  (`Program._volumetricTrailRenderer`, `VolumetricExhaustTemplate.References`,
  `VolumetricExhaustRenderer._currentAtmosphericPressure`/`_debugThrottle`,
  `Program._planetTransparenciesRenderer`, `CloudRenderer._renderer`/`_cloudShadowsRenderer`/
  `_worleyNoise3dTarget`, `PlanetRenderer._renderUboMap`/`_meshUboMap`).
- **✅ `thug_life` render internals clean despite a heavy render-side changelog.**
  `SuperMeshRenderSystem.cs` is **byte-identical** across the whole window. `Program.OffScreenPass`
  (`.Pass`/`.SampleCount`) is unchanged, and critically the offscreen render pass is still exactly four
  attachments — the alpha-to-coverage rework (revs 5057/5058) made the A2C attachment conditional on MSAA,
  but it is a **transient attachment that is not a member of that render pass**, so the pipeline's single
  colour-blend attachment stays compatible. `UnlitMeshVert`/`UnlitMeshFrag` and
  `RenderingPresets.ReverseZDepthStencil.DepthTestWrite` are intact. The rev 5058
  `SimplePipelineCreator` alpha-to-coverage stale-state fix cannot reach gatOS — the quad renderer
  supplies its own `VkPipelineMultisampleStateCreateInfo` rather than going through that creator.
- **✅ Terrain FX live-apply clean.** `PlanetRenderer`'s UBO plumbing is **unchanged across the entire
  5018→5117 window**, and gatOS derives its offsets from the public `PlanetUboStride`/`MeshUboStride`/
  `NumCelestials` rather than hardcoding them, so even a future `PlanetUbo` resize is absorbed.
- **✅ Audio actuator clean.** `GameAudio.cs` churned heavily (revs 5047/5050/5051/5054/5064/5069 — engine
  cluster channels became per-vehicle, streamed sounds went non-blocking), but every member gatOS binds is
  untouched: `GameAudio.System` (`FmodSystem`) and `GameAudio.GetChannelGroup(ChannelGroupType)` with the
  same `Master`/`Sfx`/`Ui`/`Music` mapping. The removed `GameAudio.PlaySound(SoundEvent, …)` overload is a
  game-internal path gatOS never used.
- **✅ Cloud FX clean.** The atmosphere push-constants → per-planet UBO migration (rev 5100) is renderer
  shader plumbing; gatOS's cloud editor writes the **template data**
  (`AtmosphericBody.BodyTemplate.CloudsReference`) and applies via `CloudLayerRenderData.UpdateStaticData`
  + `CloudShadowsRenderer.PopulatePlanets`, all compile-verified unchanged.

---

## ✅ 5018 write-surface findings (playbook pass 2026-07-24) {#5018-findings}

Full pass `2026.7.8.4980` → `2026.7.9.5018` (changelog gapless — revs 4981–5018 logged; diff taken
between the two side-by-side assemblies checkouts). **No write-surface break** — the pass's single
compile error was on the read side (`Mole.GetLiquidMass`, see
[read findings](ksa-read-surface.md#5018-findings)). Every bound write member, reflection accessor, and
Harmony hook target is unchanged. Findings:

- **All actuator bindings byte-identical**: `ThrusterController.cs`, `FlightComputer.cs`,
  `DockingPort.cs`, `Decoupler.cs`, `LightModule.cs`, `InputEvents.cs`, `Battery.cs`, `Camera.cs`,
  `Universe.cs` did not change. `SequenceList.ActivateNextSequence(Vehicle)` (staging) keeps its
  signature at the same line. `Vehicle.{Teleport, UpdatePerFrameData, UpdateAfterPartTreeModification,
  SetEnum, IsSet, GetThrusterFlags, LightsOn, GetMatrixAsmb2Ego, IsEditedVehicle, GetWorldMatrix,
  UpdateRenderData}` all unchanged.
- **Reflection accessors re-verified (the compiler is blind here)**: `Vehicle._manualControlInputs`
  and its `EngineThrottle` / `ThrusterCommandFlags` members are unchanged and at the same declarations;
  `ManualControlInputs.cs` is byte-identical. The `LightModule.Template` per-instance clone path
  (`LightModule.cs`) and `PartModel.Instances` / `PartModel..ctor` (`PartModel.cs`,
  `PartModelModule.cs`) are untouched. Still worth an in-game `/sim/status/accessors` glance per the
  standing rule.
- **`debug/refuel` gained SRB coverage for free (rev 4992)** — `Vehicle.RefillConsumables()` →
  `PartTree.RefillConsumables()` → `ResourceManager.RefillAllTanks`, which now enumerates
  `Modules.GetUsing<ISubstanceStore>()` and calls `RefillAll` instead of walking `Modules.Get<Tank>()`.
  The `[KsaAnchor]` target (`Vehicle.RefillConsumables()`) is unchanged; solid grain segments are now
  refilled too. `DepleteAllTanks` was generalized the same way.
- **`ctl/throttle` on a solid motor is inert by physics, not by API** — SRBs are ordinary
  `EngineController`s with `SolidMotor : RocketCore` cores, so `engine/ignite`, `engine/<n>/active`
  and staging all actuate them normally; the manual-throttle reflection write still lands on
  `_manualControlInputs.EngineThrottle` but a solid grain's thrust profile is set by its grain geometry
  (`GrainGeometry`/`BurnRateLaw`), not by throttle. `SolidMotor.UpdateState` forces `Throttle` to 0 or
  1 and there is no shutdown path once lit, so **the new `srb/<n>/` read surface is deliberately
  read-only** — it adds no write points, and `srb/<n>/engine` cross-links each motor to the
  `engines/<n>` entry that ignites it (see the [read page](ksa-read-surface.md#5018-findings)). The
  write surface is unchanged by the SRB feature.
- **Decoupler capability rework is invisible to gatOS (rev 5007)** — `_decouplerConnections` was
  replaced by a per-connector `ConnectorCapability.DecouplerJoint` flag and the symmetry-index crossfeed
  check became a junction/branch comparison. `Decoupler.cs` is byte-identical and `DecouplerActuator`
  binds only `Decoupler.IsActive` / `SetIsActive(Vehicle, true)`, so `decouplers/<n>/fire` is unaffected.
- **Harmony targets + render set**: all verified unchanged — detail in
  [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md) (5018 re-verify notes). Notably
  `SuperMeshRenderSystem.cs` and `Program.RenderGame`'s body are **byte-identical**, so both the
  thug_life main-pass postfix and the `/sim/display` capture transpiler hold. The 4980 hi-res-screenshot
  MSAA transient is unchanged and still open.
- **Global shader bindings shrank (rev 4988, MilkyWay renderer split out)** — `GlobalShaderBindings`
  dropped its MilkyWay texture (descriptor-set layout 11 → 10 bindings, combined-image-sampler pool
  10 → 8). **thug_life is unaffected**: `ThugLifeQuadRenderer` builds its **own** single-binding
  descriptor set layout and pipeline layout, and binds only `Program.OffScreenPass.{Pass,SampleCount}`
  plus the `UnlitMeshVert`/`UnlitMeshFrag` shader ids — all three still present
  (`DefaultAssets.xml:66-67`, `Shaders/Mesh/UnlitMesh.{vert,frag}` unmodified).

---

## ✅ 4980 write-surface findings (playbook pass 2026-07-22) {#4980-findings}

Full pass `2026.7.6.4939` → `2026.7.8.4980` (changelog gapless again — revs 4940–4980 logged; diff taken
between the two side-by-side assemblies checkouts). **One compile break, fixed same-day**; every other
bound write member, reflection accessor, and Harmony hook target is unchanged. Findings:

- **`docking/<n>/undock` — the one break (rev 4943).** `InputEvents.VehicleDockingInputData` lost its
  `OldMeanRadius` field (the docking/decouple camera zoom-jump fix reworked the camera follow so the
  caller no longer stashes the old radius). `DockingActuator.Undock` dropped the field from its enqueue —
  now `{Vehicle, DockingPort, Undock=true}`, still exactly the stock UnDock menu-item enqueue
  (`DockingPort.cs:145-150`). Downstream `DockingPort.Undock` → `Vehicle.Split(Connector,
  PushoffImpulse)` byte-identical; `PushoffImpulse` (write) untouched. Anchor re-verified
  `2026-07-22`/`2026.7.8.4980`. See footnote ⁵ above.
- **New `FlightComputer.RCSMode` gates auto attitude holds (revs 4946/4949, keybind rev 4975)** — and
  **`RollMode` default flipped `Up` → `Decoupled` (rev 4978)**: see footnote ⁶ above. No API change to
  any bound FC member; `FlightComputer.CopyFrom` copies the new `RCSMode` field (line 128), so the
  Solver-phase snapshot/restore rationale is intact. `ctl/attitude_mode`/`ctl/attitude_target` on an
  RCS-only vessel can now be a silent no-op when the player toggles RCS off; `attitude_target` no longer
  holds roll on a fresh FC. Live confirms queued in `docs/VALIDATION.md`; `RCSMode` (and possibly
  `RollMode`) are candidates for an additive `/sim` control + read.
- **Burn UI rework never reaches the write surface (revs 4959/4962/4963)**: the gauge-canvas burn editor,
  `Program.ActiveBurn` selection, and the new `InputEvents.BurnUpdateData.DeleteBurn` are all UI-layer;
  gatOS's `ctl/burn` binds the *onboard* `FlightComputer.Burn` (`BurnTarget{ImpulsiveInstant,
  DeltaVTargetCci}`) — `BurnTarget.cs` is byte-identical.
- **Manual RCS paths unchanged**: `ThrusterController.cs` byte-identical (`ctl/rcs`, `rcs/<n>/active`);
  `ManualControlInputs.cs` byte-identical and `Vehicle._manualControlInputs` still at `Vehicle.cs:232` —
  the throttle/translate/rotate reflection accessors all hold; `ComputeRcsControl`'s `WithNoRotation()`
  strip under auto attitude is still present (so the `ctl/rotate` "full authority needs
  `attitude_mode=manual`" rule stands).
- **Harmony targets + render set**: all verified unchanged — detail in
  [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md) (4980 re-verify notes). One niche transient found:
  the new **scaled-screenshot** path (rev 4942, `GameSettings.SampleCountOverride` +
  `RebuildRenderer`) forces a 1-sample renderer rebuild for hi-res captures, and the thug_life quad
  pipeline is built once against `OffScreenPass.SampleCount` with no rebuild listener — a hi-res
  screenshot taken *while quads are active* can transiently mismatch the pipeline's MSAA state (the
  runtime latch self-disables on fault rather than crashing). Noted in the runtime page; not a code
  change for this pass.
- **Behavior notes (game-side, inherited automatically)**: `Vehicle.Split` now names the separated
  vehicle from the new `Control.VehicleName` stamp when valid (rev 4950) — after `docking.undock` /
  `decoupler.fire` the new vessel's id (our `name`/`vessels/by-id` key) is the persisted control-module
  name instead of an auto-generated split id; massless debris gets a density-based fallback mass
  (rev 4955, `FallbackVehicleDensity=100 kg/m³`); the engine fuel-flow default flipped to
  furthest-to-nearest-by-stage with a persisted per-engine `FlowRule` (revs 4957/4958/4965) — drain
  *order* changes, every tank/engine read stays truthful.

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
  `JobSystems.VehicleSolver` untouched.
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
# Paint writes

Part paint ORs audited free state-flag bits 11..31 in static/dynamic per-instance data and toggles
`Program.RendererRebuildNeeded`; GLSL is compiled from transformed memory and never written to disk.
EVA paint allocates gatOS-owned `MaterialData` clones, writes only their initial upload, and replaces
supported avatar `MaterialIndices` slots. Restore is conditional on the slot still carrying gatOS's
handle; owned AssetMap entries are removed/disposed. Stock MaterialData is never overwritten.

# Clutter texture writes

The entire write surface is one call: `BindlessTextureLibrary.SetTexture(handle, imageView)` — once
per bind (our decoded image) and once per restore (the captured stock `ImageView`). No new bindless
slots are allocated, so KSA's 1024-entry table is untouched and the only budget is VRAM; no Harmony
patch, no shader transform, no pipeline or renderer rebuild, and no stock object is mutated —
`TextureReference` itself is never written, only the descriptor slot it already owns. Nothing in KSA
ever calls `SetTexture` (the engine only ever `AddTexture`/`FreeTexture`s), so gatOS is the sole
writer of an existing slot and no engine code can clobber an override. Desired state is authored
game-free (`paint.texture_bind` / `texture_unbind` / `texture_clear`, all Frame phase, Global target)
and the GPU follows on the next tick; the actions never touch Vulkan. `Dispose` restores every slot
before anything of ours is destroyed. See
[`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`](../plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md).

## Stickers — `paint.sticker_*` (Frame phase) {#stickers}

Projected PNG decals on vehicles, terrain and ground clutter (`/sim/paint/stickers`). Like
`paint.texture_*` and `debug.thug_life_*` the family is **vessel-agnostic**: `PaintManager.Execute`
routes any action whose key starts with `paint.sticker_` straight to `StickerManager.Execute`
**before** any vehicle resolution, because the anchor travels in the command's own `Aux`/`Ordinal`
and the registry is keyed by sticker id, not by vessel. `VesselId` is therefore always empty. Gate
string `control_enabled + paint stickers` (its own gate — neither paint runtime master applies);
absent registry ⇒ `Unsupported` ("stickers are disabled").

As with `thug_life`, **the write path below is small** — every action only edits the game-side
registry's desired state, and no Vulkan call happens in the command drain. The deep coupling is the
per-frame anchor composition and the recorded draw, which is **runtime coupling**, not a write
command — see [`ksa-runtime-coupling.md#stickers-patch`](ksa-runtime-coupling.md#stickers-patch).

| `/sim` path | action key | payload slots | actuator | Risk |
|---|---|---|---|---|
| `paint/stickers/place` | `paint.sticker_place` | `Token` = image name; `Aux` = `vessel <vessel_id> <part_iid>` or `body <body_id>`; `Values` = **12** doubles `[x y z, nx ny nz, rotation, w, h, d, alpha, brightness]`. A body anchor puts `(lat, lon, 0)` in the position slots, leaves the normal zero and reads `rotation` as a **heading** | `StickerManager.Place` → `Universe.CurrentSystem.Get` + `FindPart` (sub-parts included); registry insert at the smallest free id | Low (write); **High** (the draw it enables) |
| `paint/stickers/spray` | `paint.sticker_spray` | `Token` = image name; `Aux` = `camera` \| `cursor`; `Values` = **7** doubles `[range, roll, w, h, d, alpha, brightness]`, where `d == -1` is the **"caller said nothing" sentinel** so the anchor kind's default (0.3 m vessel / 1 m body) is substituted once the ray reports what it hit. The picker's `RotationDeg` is the "reads upright from here" default and the caller's `roll` **adds** to it | `StickerManager.Spray` → `StickerPicker.TryPick` (parts first, terrain behind); registry insert | Low (write); **High** (the draw it enables) |
| `paint/stickers/<id>/remove` | `paint.sticker_remove` | `Ordinal` = sticker id; `Value` = `1` (trigger) | `StickerManager.Remove(id)` — registry op, no KSA write | Low |
| `paint/stickers/clear` | `paint.sticker_clear` | `Ordinal` = `-1` (global); `Value` = `1` (trigger) | `StickerManager.Clear()`; the patch and GPU objects tear down on the resulting `1 → 0` live edge | Low |
| `paint/stickers/<id>/visible` | `paint.sticker_visible` | `Ordinal` = sticker id; `Value` = `0` \| `1` | `StickerEntry.Visible` (entry kept either way) | Low |
| `paint/stickers/<id>/size` | `paint.sticker_size` | `Ordinal` = sticker id; `Values` = **2** doubles `[width, height]`, metres, each in `(0, 1000]` | `StickerEntry.{Width,Height}` | Low |
| `paint/stickers/<id>/depth` | `paint.sticker_depth` | `Ordinal` = sticker id; `Value` = metres in `(0, 100]` | `StickerEntry.Depth` (the projection box's extent along the normal) | Low |
| `paint/stickers/<id>/rotation` | `paint.sticker_rotation` | `Ordinal` = sticker id; `Value` = degrees, any finite (wraps game-side) | `StickerEntry.RotationDeg` — roll about the normal (vessel) or compass heading (body) | Low |
| `paint/stickers/<id>/alpha` | `paint.sticker_alpha` | `Ordinal` = sticker id; `Value` = `[0, 1]` | `StickerEntry.Alpha` (multiplied into the sampled alpha) | Low |
| `paint/stickers/<id>/brightness` | `paint.sticker_brightness` | `Ordinal` = sticker id; `Value` = `(0, 8]` | `StickerEntry.Brightness` (gain on the lighting term) | Low |
| `paint/stickers/<id>/image` | `paint.sticker_image` | `Ordinal` = sticker id; `Token` = an uploaded image name | `StickerEntry.Image`; the binder hot-swaps the bindless slot on the next tick | Low |
| `paint/stickers/debug` | `paint.sticker_debug` | `Ordinal` = `-1` (global); `Value` = `0` \| `1` | draws every decal as a magenta projection-box checker (`texId = 0xFFFFFFFF`) instead of its image — the visual proof that the box, the reverse-Z reconstruction and the ego matrices are right, with no art involved | Low |

**Every argument is re-validated game-side against `StickerRules`**, even though the 9p line grammars
already validated it: `POST /v1/command`, MQTT `gatos/command` and MCP `gatos.paint_sticker` author a
`SimCommand` directly and never touch the parsers. Errnos: `EINVAL` (bad arity/range/anchor keyword,
**and a full registry** — there is no `ENOSPC` in `CommandOutcome`, so the cap is reported like any
other out-of-range argument, with the limit in the message), `ENOENT` (vessel/part/body/id gone, or a
`spray` that hit nothing within `range`), `EOPNOTSUPP` (stickers disabled). There is deliberately no
**position** leaf: the two anchor kinds have different arities, so a move is `cat <id>/spec` →
edit → `> place`, which is also how the guest's save/restore script works.

Every successful `place`/`spray` publishes the `last` line and emits a `paint.sticker_placed` event
(vessel-anchored placements carry the vessel id) so a script that is not polling still learns what
the ray hit. Entries are **runtime-only** — never persisted, dropped at unload
(`PaintManager.Dispose` → `StickerManager.Dispose`). Anchors verified `2026-08-22` against
`2026.8.19.5261`; **the live draw is unvalidated** — see the stickers card in `docs/VALIDATION.md`.
Pipeline, shader and GLSL-layout assumptions:
[`ksa-assets-and-versions.md`](ksa-assets-and-versions.md).
