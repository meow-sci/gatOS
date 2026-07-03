# Scope — KSA Read Surface (sensors)

> Every telemetry read gatOS performs against KSA. Each row: the `/sim` path it feeds, the gatOS code
> site, the KSA member it binds to, the decompiled-source file that defines that member, the unit/format,
> the churn risk, and the **4826 status** (✅ unaffected · ⚠️ silent semantic/unit drift · ❌ compile break).
>
> Source of truth = the `[KsaAnchor]` attributes in the cited files. API catalog = [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md).
> Anchor mirror = [`docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md). Decomp paths are
> relative to a game-assemblies checkout's `current/decomp/` (e.g. `KSA/Vehicle.cs` →
> `…/ksa-game-assemblies/current/decomp/KSA/Vehicle.cs`); see [`ksa-assets-and-versions.md`](ksa-assets-and-versions.md).

All reads run on the **game thread** inside `TelemetrySampler.Tick`, build an immutable `SimSnapshot`,
and publish it with one volatile swap (`SnapshotStore.Publish`). Every `double` is scrubbed through
`gatOS.SimFs/Sanitize` (NaN/Inf → 0). Transports never read KSA — they project the published snapshot.

---

## Sampler-direct reads (time / system) {#sampler-direct-reads}

Performed directly in `gatOS.GameMod/Game/TelemetrySampler.cs` (not in a reader). **Anchored (G4,
2026-06-27):** the sampler methods now carry `[KsaAnchor]`s, so the census is complete. All `Universe`
statics (+ `VersionInfo.Current`).

| `/sim` path | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4826 |
|---|---|---|---|---|---|---|
| `time/ut` | `TelemetrySampler.cs:92` | `Universe.GetElapsedSimTime().Seconds()` | `KSA/Universe.cs` | seconds, double | Low | ✅ |
| `time/warp` | `:93` | `Universe.SimulationSpeed` | `KSA/Universe.cs` | factor | Low | ✅ |
| `time/sim_dt` | `:131` | `Universe.GetLastSimStep().DeltaTime` | `KSA/Universe.cs` | seconds | Medium | ✅ |
| `time/warp_speeds` | `:171` | `Universe.GetSimulationSpeeds()` | `KSA/Universe.cs` | factor list | Medium | ✅ |
| `time/auto_warp` | `:188,203` | `Universe.IsAutoWarpActive`, `Universe.AutoWarpTime` | `KSA/Universe.cs` | flag + UT | Medium | ✅ |
| `status/game_version` | `:214` | `VersionInfo.Current.VersionString` | `KSA/VersionInfo.cs` | string | Low | ✅ |
| (active vessel id) | `:94` | `Program.ControlledVehicle?.Id` | `KSA/Program.cs` | string | Low | ✅ |
| (vessel set) | `:100` | `Universe.CurrentSystem.All.UnsafeAsList()` | `KSA/Universe.cs`, `KSA/CelestialSystem.cs` | enumeration | Low | ✅ |

> Re-verified 2026-07-03 against `2026.7.3.4826` (full decomp diff — `Universe.cs`'s only changes are a
> `Program.EditorFlag` reset in a system-unload path plus log-line churn; `VersionInfo.cs` unchanged).
> **G4 (2026-06-27):** these sampler-direct reads were the one un-anchored corner of the KSA surface; they
> now carry `[KsaAnchor]`s (on `Sample` / `SampleWarpSpeeds` / `SafeAutoWarp*` / `GameVersion`), so the
> census is complete — a rename errors in the sampler, still caught by the build.

---

## Vessel core reads — `VesselReader.ReadBasics`/`BuildCore` (always sampled)

`gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs`. Sampled for every vessel regardless of the
`telemetry_vessel_detail` gate. Anchor: `VesselReader.Sample`.

| `/sim` path (under `vessels/by-id/<id>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4826 |
|---|---|---|---|---|---|---|
| `id`, `name` | `:89,90` | `Vehicle.Id` (name = id; KSA has no display name) | `KSA/Vehicle.cs` | string | Low | ✅ |
| `situation` | `:91` | `Vehicle.Situation.ToString()` | `KSA/Vehicle.cs`, `KSA/Situation*.cs` | string | Low | ⚠️ flags |
| `position/cci` | `:59` | `Vehicle.GetPositionCci()` | `KSA/Vehicle.cs` | `x y z` m (CCI) | Low | ✅ |
| `position/{lat,lon}` | `:67,68` | `IParentBody.GetCci2Ccf()`, `GetLlaFromCcf()` | `KSA/IParentBody.cs` | degrees | Low | ✅ |
| `velocity/{orbital,surface,inertial}` | `:95-97` | `Vehicle.OrbitalSpeed` / `GetSurfaceSpeed()` / `GetInertialSpeed()` | `KSA/Vehicle.cs` | m/s | Low | ✅ |
| `attitude/quat` | `:84` | `Vehicle.GetBody2Cci()` | `KSA/Vehicle.cs` | quat `x y z w` | Low | ✅ |
| `attitude/rates` | `:85` | `Vehicle.BodyRates` | `KSA/Vehicle.cs` | rad/s `x y z` | Low | ✅ |
| `altitude/{barometric,radar}` | `:102,103` | `Vehicle.GetBarometricAltitude()` / `GetRadarAltitude()` | `KSA/Vehicle.cs` | m | Low | ✅ |
| `mass/{total,dry,propellant}` | `:104-106` | `Vehicle.TotalMass` / `InertMass` / `PropellantMass` | `KSA/Vehicle.cs` | kg | Low | ✅ |
| `orbit/{apoapsis,periapsis,ecc,inc,sma,period}` | `:75-82` | `Vehicle.Orbit` elements (radii→alt; inc rad→deg) | `KSA/Orbit.cs` | m / – / deg / s | Low | ✅ |
| `battery/{charge,fraction}` | `:86,339` | `Vehicle.Parts.Batteries.GetState(b).Charge.Value()` ÷ `b.MaximumCapacity.Value()` | `KSA/Battery.cs` | fraction 0..1 | Low | ✅ (G2) |
| `ctl/lights` (readback) | `:112` | `Vehicle.LightsOn` | `KSA/Vehicle.cs` | 0/1 | Low | ✅ |
| `ctl/engine` (readback) | `:125` | `Vehicle.IsSet(VehicleEngine.MainIgnite, false)` | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ ᵈ |
| `controllable` | `:133` | `Vehicle.IsControllable` (`_overrideIsControllable \|\| Parts.Controls.NumModules > 0`) | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ (G3, new) |
| `engines/<n>/{active,vac_thrust,isp}` | `:256` | `Vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData.{ThrustMax,MassFlowRateMax}` | `KSA/EngineController.cs` | bool / N / s | Medium | ✅ ᵈ |
| `tanks/<r>/{amount,capacity,fraction}` | `:312` | `Tank.Moles`; `Parts.Moles.GetState(mole).Mass`; `mole.GetLiquidMass(ContainerVolume)`; `mole.FilledFraction` | `KSA/Tank.cs`, `KSA/Mole.cs` | kg / kg / 0..1 | Low | ✅ |
| `animations/<n>/{current,state,goal}` | `:596` | `KeyframeAnimationModule.{TimeGoal,Shared.Duration}`; `State.{TimeCurrent,DeploymentState}` via `ModuleStateful` | `KSA/KeyframeAnimationModule.cs` | 0..1 / enum | Medium | ✅ |

**⚠️ `situation`** — `Vehicle.Situation` became a `[Flags]` bitfield (KSA rev 4645, *already* in the
4680 baseline), so `.ToString()` can yield composite values (e.g. `"Landed, ..."`); rev 4704 lets
atmospheric floaters enter `Landed` (aerostats). gatOS passes the string through unchanged, so this is a
*value-shape* consideration for guest parsers, not a code break. Documented here so it isn't mistaken for
a new 4750 regression.

**✅ `battery` (G2 re-verified 2026-06-27)** — `Battery.MaximumCapacity` and `BatteryState.Charge` are now
the strongly-typed `Joules` struct (rev 4681); `.Value()` still returns the joule float, and the reported
**fraction is unit-independent**, so battery reads are numerically unaffected. See power section below.

**✅ ᵈ post-decouple state inheritance (4826)** — the marked members are unchanged, but a freshly
decoupled/undocked stage now *inherits* the parent's control state instead of resetting to defaults —
see the [4826 findings](#4826-findings) below.

---

## Vessel detail reads — `VesselReader.BuildFull` (gated by `telemetry_vessel_detail`)

Same file; the full single-pass build runs inside a whole-pass try/catch in `VesselReader.Sample` — if
any extension API drifts, the vessel falls back to `BuildCore` (core telemetry only) and the extension
dirs vanish (logged once). The structural animation↔module links (IsSolar, solar/light AnimationIndex)
are cached per vehicle in `Readers/AnimationLinks.cs` (GP3), rebuilt on module-count change or every 10 s.

### Position / navball / environment

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4826 |
|---|---|---|---|---|---|
| `position/ecl`, `velocity/cci`, `com` | `:154-156` | `Vehicle.GetPositionEcl()`, `GetVelocityCci()`, `CenterOfMassAsmb` | `KSA/Vehicle.cs` | Low | ✅ |
| `navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | `:223` | `Vehicle.NavBallData.{AttitudeAngles(int3 deg),ThrustWeightRatio,DeltaVInVacuum,Frame,Speed}` | `KSA/NavBallData.cs` | Medium | ✅ |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | `:235` | `Vehicle.PhysicsEnvironment.{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}`; `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`; `Vehicle.AccelerationBody`/`AngularAccelerationBody` | `KSA/PhysicsEnvironment.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | `:199` | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly.Degrees}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `encounters` (NDJSON) | `:573` | `Vehicle.Patch.Encounters`; `Encounter.{Body.Id,GameTime,ClosestDistance}` | `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | Medium | ✅ |

### Writable-setpoint read-backs (so `ctl/*` files report the real state)

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4826 |
|---|---|---|---|---|---|
| `ctl/throttle` | `:169` | `Vehicle.GetManualThrottle()` | `KSA/Vehicle.cs` (`:824`) | Medium | ✅ ᵈ |
| `ctl/rcs` | `:170` | any `ThrusterController.IsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/attitude_mode` | `:171` | `FlightComputer.AttitudeMode` / `AttitudeTrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅ |
| `ctl/attitude_frame` | `:172` | `FlightComputer.AttitudeFrame` | `KSA/FlightComputer.cs` | Medium | ✅ |

### Per-module reads

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4826 |
|---|---|---|---|---|---|
| `engines/<n>/{throttle,propellant,min_throttle}` | `:278` | `EngineControllerState.{CommandThrottle,IsPropellantAvailable}`; `EngineController.MinimumThrottle` | `KSA/EngineControllerState.cs` | Medium | ✅ |
| `rcs/<n>/{active,propellant,map}` | `:387` | `ThrusterController.IsActive`; `ThrusterControllerState.{ControlMap,IsPropellantAvailable}` | `KSA/ThrusterController.cs` | Medium | ✅ ᵈ |
| `solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle}` | `:419` | `SolarPanelState.{Produced,IsOccluded,SunAoA,SunEfficiency}`; `SolarTrackerState.CurrentAngle` | `KSA/SolarPanel.cs`, `KSA/SolarTracker.cs`, `KSA/SolarPanelState.cs` | Medium | ✅ (G2: W) |
| `generators/<n>/{active,produced}` | `:476` | `GeneratorState.{Active,Produced}` | `KSA/Generator.cs`, `KSA/GeneratorState.cs` | Medium | ✅ (G2: W) |
| `lights/<n>/{on,brightness,color,inner_angle,outer_angle}` | `:500` | `LightModule.Template.{Intensity.Value,ColorRgb,OuterAngle.Value,InnerAngle.Value}`; `Parent.FullPart.LightSwitch.LightIsActive` | `KSA/LightModule.cs` | **High** | ✅ |
| `docking/<n>/{docked,docked_to,pushoff_impulse}` | `:540` | `DockingPort.{Docked,DockedToPart.Id,PushoffImpulse}` | `KSA/DockingPort.cs` | Medium | ✅ (fixed) |
| `decouplers/<n>/fired` | `:560` | `Decoupler.IsActive` | `KSA/Decoupler.cs` | Medium | ✅ ᵈ |
| `power/produced` | `:360` | Σ `SolarPanelState.Produced.Value()` + `GeneratorState.Produced.Value()` | `KSA/SolarPanelState.cs`, `KSA/GeneratorState.cs` | Medium | ✅ (G2: W) |
| `power/consumed` | `:374` | Σ `Vehicle.Parts.PowerConsumers.GetState(c).Consumed.Value()` | `KSA/PowerConsumerState.cs` | Medium | ✅ (G2: W) |
| `battery/capacity` | `:342` | Σ `Battery.MaximumCapacity.Value()` | `KSA/Battery.cs` | Low | ✅ (G2) |

---

## Parts — `PartsReader` (gated by `telemetry_vessel_parts`) {#parts}

`gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs`. Surfaces a vehicle's **top-level** parts (subparts
are deliberately not surfaced) at `vessels/by-id/<id>/parts/<n>/` — the anchor picker for the **welds**
write surface ([`ksa-write-surface.md#welds`](ksa-write-surface.md#welds)). Cached per vehicle in a
`ConditionalWeakTable<Vehicle,…>` (collected with the vehicle, no leak, game-thread only); rebuilt only
on a `Vehicle.Parts.Count` change (the cheap "vehicle was edited" signal — KSA exposes no part-tree
version/dirty flag) or every 10 s of sim time. Hot path = one `Parts.Count` read per vehicle per tick.
Anchor: `PartsReader.cs:28`.

| `/sim` path (under `…/parts/<n>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4826 |
|---|---|---|---|---|---|---|
| `instance_id` | `PartsReader.cs:57` | `Part.InstanceId` | `KSA/Part.cs` | uint (the **stable** weld handle) | Low | ✅ |
| `id` | `:57` | `Part.Id` | `KSA/Part.cs` | string (can collide across instances) | Low | ✅ |
| `display_name` | `:57` | `Part.DisplayName` | `KSA/Part.cs` | string | Low | ✅ |
| `template` | `:57` | `Part.Template.Id` | `KSA/Part.cs` | string | Low | ✅ |
| `is_root` | `:58` | `Part.PartParent is null` | `KSA/Part.cs` | flag | Low | ✅ |
| `subpart_count` | `:58` | `Part.SubParts.Length` | `KSA/Part.cs` | int | Low | ✅ |
| `position` | `:55,59` | `Part.PositionVehicleAsmb` | `KSA/Part.cs` | `x y z` m (vehicle assembly frame) | Low | ✅ |
| (enumeration) | `:37,50` | `Vehicle.Parts.{Count,Parts}` | `KSA/Vehicle.cs`, `KSA/PartTree.cs` | span of `Part` | Low | ✅ |

Verified `2026-06-28` against `2026.6.9.4750` (new feature; compiled clean — none of these
`Part`/`PartTree` members appear in the 4680→4750 changelog). Re-verified 2026-07-03 against
`2026.7.3.4826`: the `Part.cs`/`PartTree.cs` churn (+438/+48) is additive symmetry/sequence-group
infrastructure (`PartSymmetryInstance`, `SequenceGroup`/`SequenceOrder`, `AlignedConnectors`, a new
`PartTree.Decouplers` hot-path list) — every bound member above is unchanged.

---

## thug_life anchor math — `ThugLifeQuadRenderer` (per-frame, render thread) {#thug-life}

`gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs` (`TryComputeModelEgo`) + `ThugLifeManager.cs`.
These are **render-frame transform reads** performed each frame inside the `gatos.thug_life` render postfix
(on the **main thread**) to place the quad on its anchor part — *not* sampler reads, so they do **not** go
through `SimSnapshot`. They are the read half of gatOS's **highest-churn KSA coupling** (render-pipeline
internals; see [`ksa-runtime-coupling.md#thug-life-patch`](ksa-runtime-coupling.md#thug-life-patch) and the
write side [`ksa-write-surface.md#thug-life`](ksa-write-surface.md#thug-life)). A rename **does** fail the
build at the `[KsaAnchor]` site (these are non-reflective), so they are caught at compile time — but
frame-math is the classic *silent* drift, so re-verify the quad's pose in a live flight after any update.

| read | gatOS site | KSA / Brutal member | Decomp file | Risk | 4826 |
|---|---|---|---|---|---|
| camera view-projection | `ThugLifeQuadRenderer.TryComputeModelEgo` | `Program.GetMainCamera()`; `Camera.MVP.viewProjection`; `Program.SetViewport` | `KSA/Program.cs`, `KSA/Camera.cs` | **High** | ✅ |
| vehicle ego transform | same | `Vehicle.GetMatrixAsmb2Ego(Camera)`; `Vehicle.Asmb2Ego` | `KSA/Vehicle.cs` | **High** | ✅ |
| part ego pose (anchor) | same | `Part.PositionEgo(in double4x4)`; `Part.Asmb2Ego(doubleQuat)`; `double3.Transform` | `KSA/Part.cs`, `Brutal.Core.Numerics/` | **High** | ✅ |
| live-entry validation | `ThugLifeManager.{Update,IsLive}` | `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.Parts`; `Part.InstanceId` | `KSA/Universe.cs`, `KSA/Vehicle.cs`, `KSA/Part.cs` | Low | ✅ |

The `debug/thug_life/count`, `…/<id>/{vessel,part,spec}` reads under `/sim` are **not** KSA reads — they
are a game-free projection of `ThugLifeManager.Snapshot()` (`ThugLifeSnapshot` records), which
`TelemetrySampler` copies into `SimSnapshot.ThugLife = _thugLife.Snapshot()`. Verified `2026-06-28` against
`2026.6.9.4750` (new feature; compiled clean — none of these `Vehicle`/`Part`/`Camera`/`Program` members
appear in the 4680→4750 changelog, though render internals are not changelog-covered as reliably as the
gameplay APIs). Re-verified (static) 2026-07-03 against `2026.7.3.4826`: `Camera.cs` unchanged;
`Part.PositionEgo`/`Asmb2Ego`, `Vehicle.GetMatrixAsmb2Ego`/`Asmb2Ego` bodies unchanged in the diff —
live pose check still advised (render internals; see `docs/VALIDATION.md`).

---

## ✅ 4750 read-surface findings (detail)

### ✅ Docking pushoff — `docking/<n>/pushoff_impulse` (G1 FIXED, 2026-06-27) {#docking}
**Was a compile break.** `VesselReader.cs` read `port.PushoffForce`; in 4750 (rev 4683) the member was
renamed **`PushoffForce` → `PushoffImpulse`** and changed from a **force (N)** to an **impulse (N·s)**
(the latching threshold member changed too: `LatchingImpulse` → `LatchingKineticEnergy`). Confirmed in
`KSA/DockingPort.cs` (`public required float PushoffImpulse;`, `Undock → Split(Connector, PushoffImpulse)`)
and the asset XML `Content/Core/CoreCouplingAGameData.xml` (`<PushoffImpulse Ns="7000"/>`,
`<LatchingKineticEnergy J="50"/>`). **Applied fix (G1):** `VesselReader.cs:542` now reads
`port.PushoffImpulse`; the snapshot field `DockingSnapshot.PushoffForceN` → **`PushoffImpulseNs`**; the
`/sim` read leaf and the `debug` control leaf were renamed `pushoff_force` → **`pushoff_impulse`** (unit
**N → N·s**) — a deliberate breaking `/sim` rename, since the datum's meaning changed and keeping the old
name would lie. Full record in [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md);
write side in [`ksa-write-surface.md#docking`](ksa-write-surface.md#docking). **Live re-check still
pending** (undock applies the impulse; the debug knob changes separation energy) — see
[`../docs/VALIDATION.md`](../docs/VALIDATION.md).

### ✅ Power production/consumption — `power/*`, `solar/<n>/produced`, `generators/<n>/produced` (G2 RE-LABELLED, 2026-06-27) {#power}
**Was a silent unit change.** rev 4681 ("Fixed the conflation of power and energy") retyped
`SolarPanelState.Produced/Stored`, `GeneratorState.Produced`, `PowerConsumerState.Consumed` from
`Joules` → **`Watts`**. gatOS calls `.Value()` on each (both `Joules.Value()` and `Watts.Value()` return
the backing `float`, verified in `KSA/Watts.cs`/`KSA/Joules.cs`), so it **compiled silently** — but the
emitted numbers now mean **instantaneous power (W)** instead of energy accumulated per sample (J). The
`/sim` fields were already named/specced in watts (`PowerProducedW`, SPEC said "W") and the asset XML
authors `<Produced W="200"/>`, so 4750 makes these values *correct*. **Applied fix (G2):** verified no
gatOS reader scales by `dt` or accumulates (`SamplePowerConsumed` / `SampleSolar` / `SampleGenerators`
sum `.Value()` straight through; since GP3 the vessel `PowerProducedW` total is accumulated inside the
solar/generator passes — same members, one read); re-labelled the five
power/battery `[KsaAnchor]` `Notes` (Joules→Watts) and bumped them to `2026-06-27` / `2026.6.9.4750`;
dropped the stale "this sample" phrasing from the SPEC, matrix, and snapshot field docs. **No functional
code change** — but **guests will see different magnitudes** than the 4680 era (instantaneous W, not
per-sample J). Still **open as a separate additive task** (not a gap): surface the new
`SolarPanelState.Stored` (Watts), `DistanceToSun`, and per-source `Active`. Details in the plan.

### ✅ Battery capacity — `battery/capacity` (G2: TYPE TIGHTENED, value unchanged, re-verified 2026-06-27)
`Battery.MaximumCapacity` / `BatteryState.Charge` are now the `Joules` struct (was a reference type);
`.Value()` still returns joules, so `battery/capacity` and the unit-independent `battery/fraction` are
numerically unchanged. Anchor `Notes` re-labelled and bumped to `2026.6.9.4750`.

---

## ✅ 4826 read-surface findings (detail) {#4826-findings}

Full playbook pass 2026-07-03, `2026.6.9.4750` → `2026.7.3.4826`. Build + full test suite green;
**no bound member changed name, signature, type, unit, frame, or gating.** The CURRENT `version.json`
only logs revs 4825–4826 (revs 4751–4823 have **no changelog** in either tree), so this pass was driven
by the full decomp + Content diff, not the commit log. Findings — all *game-behavior* changes the reads
report faithfully, none a member drift:

- **ᵈ Post-decouple control-state inheritance** (`ctl/engine`, `ctl/throttle`, `engines/<n>/active`,
  `rcs/<n>/active`, `decouplers/<n>/fired`, and `navball/twr` via `EngineThrottle`): `Vehicle.Split` now
  copies `_manualControlInputs` (engine-on + throttle) and the active staging sequence to the separated
  vehicle, and `Decoupler.Decouple` dropped its cascade that force-`Deactivate()`d every `IActivate`
  module on the separated stage. A just-decoupled/undocked stage therefore **inherits** the parent's
  control state instead of reporting off/0. Members and units unchanged; guests observing a fresh stage
  see different (truthful) values than the 4750 era. The affected anchors carry a `4826:` note.
- **Gravitation refactor** (`environment/{accel,angular_accel,g_force}`): the multi-body correction moved
  from `PhysicsStates` into `PhysicsEnvironment.ComputeGravitationBub`. Numerically identical in the
  single-dominant-body case; near SoI boundaries the acceleration magnitude may shift marginally (a
  physics-accuracy change, same body-frame m/s² semantics). Live sanity check listed in
  `docs/VALIDATION.md`.
- **PatchedConic terrain impact** (`orbit/next_patch`, `encounters`): new `TryFindTerrainImpact` marches
  impact-bound trajectories against the heightmap and terminates the patch with
  `PatchTransition.Impact` — which `Vehicle.NextPatchEventTime` ignores (only Escape/Encounter feed
  `next_patch`), so the read is unaffected. Edge case: an impact-terminated patch can truncate the
  downstream patch chain, marginally changing which `encounters` exist (improved prediction, not drift).
- **Power re-confirmed stable**: the `PowerReference.cs`/`PowerManager.cs` churn is a display formatter
  (`ToNearest` with W/kW/… suffixes) + a span→array refactor — **no re-unit**; the 4750 Watts convention
  holds.
- **Content value tweak**: `CoreElectricalAGameData.xml` solar cell `SolarPanelB_CellA`
  `<Produced W="50"/>` → `W="100"` — same unit, read at runtime, so `solar/<n>/produced` simply reports
  the new stock value.

---

## Events {#events}
`/sim/events` and per-vessel `stream` are **not** direct KSA reads — they are produced by
`gatOS.SimFs/EventDiffer` and `StreamFile` diffing successive `SimSnapshot`s (KSA has no native event
bus). They inherit the reads above: an event type only exists if its underlying field is sampled (so
turning off `telemetry_vessel_detail` drops module-level events). Emitted types: `engine-state`,
`flameout`, `docked`, `undocked`, `decoupled`, `animation-complete`, `battery-depleted`,
`battery-charged`. KSA coupling: none beyond the reads they observe → a game update cannot break the
differ, only change the values it observes. See [`non-ksa-surface.md`](non-ksa-surface.md).

---

## Celestial bodies & system — `BodyReader` (gated by `telemetry_bodies`)

`gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs`. Most reads go through the `IParentBody` interface
(implemented by both `Celestial` and `StellarBody`), so a body-type rename surfaces in one place.

| `/sim` path | gatOS site | KSA member | Decomp file | Unit | Risk | 4826 |
|---|---|---|---|---|---|---|
| (catalog) | `:24` | `Universe.CurrentSystem.All.UnsafeAsList()` → `Celestial`; `Universe.WorldSun` (`StellarBody`); `CelestialSystem.HomeBody` | `KSA/CelestialSystem.cs`, `KSA/Universe.cs` | – | Low | ✅ |
| `system/{name,home,sun}` | `:35` | `WorldSun.Id`, `HomeBody.Id` | `KSA/Universe.cs` | string | Low | ✅ |
| `bodies/<id>/{id,class,parent,children,mass,radius,mu,soi,rotation_rate}` | `:42` | `Celestial.{Id,Class,Parent,Children,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity}`; `IParentBody.Mu` | `KSA/Celestial.cs`, `KSA/IParentBody.cs` | mixed SI | Low | ✅ |
| `bodies/<id>/position/ecl`, `velocity/ecl` | `:71,72` | `Celestial.GetPositionEcl()` / `GetVelocityEcl()` | `KSA/Celestial.cs` | m, m/s (ECL) | Low | ✅ |
| `bodies/<id>/orbit/{...}` | `:48` | `Celestial.Orbit` elements (radii→alt; angles rad→deg) | `KSA/Orbit.cs` | m / deg / s | Low | ✅ |
| `bodies/<id>/atmosphere/{present,height,scale_height,sea_level_pressure,sea_level_density}` | `:98` | `IParentBody.GetAtmosphereReference().Physical.{Height,ScaleHeight,SeaLevelPressure,SeaLevelDensity}` | `KSA/AtmosphereReference.cs` | SI | Medium | ✅ |
| `bodies/<id>/ocean/{present,density}` | `:110` | `IParentBody.GetOceanReference().Density` | `KSA/OceanReference.cs` | kg/m³ | Medium | ✅ |
| (star) | `:81` | `StellarBody.{Id,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity}`; `IParentBody.Mu` | `KSA/StellarBody.cs` | SI | Low | ✅ |

No body/celestial members appear in the 4680→4750 changelog; all compiled clean. rev 4688 ("particle
effects parented to the celestial") is render-only and exposes nothing through `BodyReader`.
Re-verified 2026-07-03 against `2026.7.3.4826`: the `Celestial.cs` +132 lines are **entirely additive**
terrain-height members for the new terrain-impact prediction (`Min/MaxTerrainHeightApprox`,
`HasTerrainHeightmap`, `MaxTerrainRadius`, `TrySpawnWaterSplash`); every catalog member above —
`MeanRadius` included — is untouched.

---

## Coordinate frames (reference)
Reads cross several KSA frames — CCI (inertial), CCE/CCF (body-fixed), ECL (ecliptic), body frame. The
frame math (`GetCci2Ccf`, `GetBody2Cci`, `GetCce2Cci`, `GetLlaFromCcf`) is summarized in
[`ksa-runtime-coupling.md#frames-and-numerics`](ksa-runtime-coupling.md#frames-and-numerics) and detailed
in [`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md). A frame-math
change is the classic *silent* drift (compiles, wrong numbers) — re-verify against a live flight per the
playbook step 5.
