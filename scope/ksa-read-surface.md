# Scope — KSA Read Surface (sensors)

> Every telemetry read gatOS performs against KSA. Each row: the `/sim` path it feeds, the gatOS code
> site, the KSA member it binds to, the decompiled-source file that defines that member, the unit/format,
> the churn risk, and the **5018 status** (✅ unaffected · ⚠️ silent semantic/unit drift · ❌ compile break).
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

| `/sim` path | gatOS site | KSA member | Decomp file | Unit/format | Risk | 5018 |
|---|---|---|---|---|---|---|
| `time/ut` | `TelemetrySampler.cs:92` | `Universe.GetElapsedSimTime().Seconds()` | `KSA/Universe.cs` | seconds, double | Low | ✅ |
| `time/warp` | `:93` | `Universe.SimulationSpeed` | `KSA/Universe.cs` | factor | Low | ✅ |
| `time/sim_dt` | `:131` | `Universe.GetLastSimStep().DeltaTime` | `KSA/Universe.cs` | seconds | Medium | ✅ |
| `time/warp_speeds` | `:171` | `Universe.GetSimulationSpeeds()` | `KSA/Universe.cs` | factor list | Medium | ✅ |
| `time/auto_warp` | `:188,203` | `Universe.IsAutoWarpActive`, `Universe.AutoWarpTime` | `KSA/Universe.cs` | flag + UT | Medium | ✅ |
| `status/game_version` | `:214` | `VersionInfo.Current.VersionString` | `KSA/VersionInfo.cs` | string | Low | ✅ |
| (active vessel id) | `:94` | `Program.ControlledVehicle?.Id` | `KSA/Program.cs` | string | Low | ✅ |
| (vessel set) | `:100` | `Universe.CurrentSystem.All.UnsafeAsList()` | `KSA/Universe.cs`, `KSA/CelestialSystem.cs` | enumeration | Low | ✅ |

> Re-verified 2026-07-16 against `2026.7.6.4939` (the `Universe.cs` diff is log-line renumbering only —
> zero member-level changes; `VersionInfo.cs` untouched; `Program.ControlledVehicle` intact). Previously
> re-verified 2026-07-14 against `2026.7.5.4892` and 2026-07-03 against `2026.7.3.4826`.
> **G4 (2026-06-27):** these sampler-direct reads were the one un-anchored corner of the KSA surface; they
> now carry `[KsaAnchor]`s (on `Sample` / `SampleWarpSpeeds` / `SafeAutoWarp*` / `GameVersion`), so the
> census is complete — a rename errors in the sampler, still caught by the build.

---

## Vessel core reads — `VesselReader.ReadBasics`/`BuildCore` (always sampled)

`gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs`. Sampled for every vessel regardless of the
`telemetry_vessel_detail` gate. Anchor: `VesselReader.Sample`.

| `/sim` path (under `vessels/by-id/<id>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 5018 |
|---|---|---|---|---|---|---|
| `id`, `name` | `:89,90` | `Vehicle.Id` (name = id; KSA has no display name) | `KSA/Vehicle.cs` | string | Low | ✅ |
| `situation` | `:91` | `Vehicle.Situation.ToString()` | `KSA/Vehicle.cs`, `KSA/Situation*.cs` | string | Low | ⚠️ flags |
| `position/cci` | `:59` | `Vehicle.GetPositionCci()` | `KSA/Vehicle.cs` | `x y z` m (CCI) | Low | ✅ |
| `position/{lat,lon}` | `:67,68` | `IParentBody.GetCci2Ccf()`, `GetLlaFromCcf()` | `KSA/IParentBody.cs` | degrees | Low | ✅ |
| `velocity/{orbital,surface,inertial}` | `:95-97` | `Vehicle.OrbitalSpeed` / `GetSurfaceSpeed()` / `GetInertialSpeed()` | `KSA/Vehicle.cs` | m/s | Low | ✅ |
| `attitude/quat` | `:84` | `Vehicle.GetBody2Cci()` | `KSA/Vehicle.cs` | quat `x y z w` | Low | ✅ |
| `attitude/rates` | `:85` | `Vehicle.BodyRates` | `KSA/Vehicle.cs` | rad/s `x y z` | Low | ✅ |
| `altitude/{barometric,radar}` | `:102,103` | `Vehicle.GetBarometricAltitude()` / `GetRadarAltitude()` | `KSA/Vehicle.cs` | m | Low | ✅ |
| `mass/{total,dry,propellant}` | `:104-106` | `Vehicle.TotalMass` / `InertMass` / `PropellantMass` | `KSA/Vehicle.cs` | kg | Low | ⚠️ ˢ |
| `orbit/{apoapsis,periapsis,ecc,inc,sma,period}` | `:75-82` | `Vehicle.Orbit` elements (radii→alt; inc rad→deg) | `KSA/Orbit.cs` | m / – / deg / s | Low | ✅ |
| `battery/{charge,fraction}` | `:86,339` | `Vehicle.Parts.Batteries.GetState(b).Charge.Value()` ÷ `b.MaximumCapacity.Value()` | `KSA/Battery.cs` | fraction 0..1 | Low | ✅ (G2) |
| `ctl/lights` (readback) | `:112` | `Vehicle.LightsOn` | `KSA/Vehicle.cs` | 0/1 | Low | ✅ |
| `ctl/engine` (readback) | `:125` | `Vehicle.IsSet(VehicleEngine.MainIgnite, false)` | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ ᵈ |
| `controllable` | `:133` | `Vehicle.IsControllable` (`_overrideIsControllable \|\| Parts.Controls.NumModules > 0`) | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ (G3, new) |
| `engines/<n>/{active,vac_thrust,isp}` | `:256` | `Vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData.{ThrustMax,MassFlowRateMax}` | `KSA/EngineController.cs` | bool / N / s | Medium | ✅ ᵈ |
| `tanks/<r>/{amount,capacity,fraction}` | `:312` | `Tank.Moles`; `Parts.Moles.GetState(mole).Mass`; `mole.GetStoredMass(ContainerVolume)`; `mole.FilledFraction` | `KSA/Tank.cs`, `KSA/Mole.cs` | kg / kg / 0..1 | Low | ⚠️ **fixed** ˢ |
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

**ˢ solid rocket motors (5018, rev 4992)** — KSA generalized propellant storage from liquid-only to
`ISubstanceStore` (`Liquid | Solid`). Three consequences:
- **Compile break, fixed (the pass's only one):** `Mole.GetLiquidMass`/`GetLiquidVolume` were renamed
  `GetStoredMass`/`GetStoredVolume` (and `ConsumeLiquid`/`ProduceLiquid` → `ConsumeStored`/`ProduceStored`;
  `ContainsLiquid` deleted). `VesselReader.SampleTanks` was updated. **Values are unchanged** — `Tank`
  moles are liquids, and for a liquid the new method is the old one.
- **Coverage gap, closed by the new `srb/<n>/` surface:** solid propellant lives on the **new
  `SolidGrainSegment` module**, which is an `ISubstanceStore` but **not a `Tank`**, so it is invisible
  to `Modules.Get<Tank>()` and therefore **absent from `tanks/`** — while `Vehicle.PropellantMass`
  (now recomputed from `Parts.SubstanceStores`) **does** include grain mass, making
  **`mass/propellant` > Σ `tanks/<r>/amount`** on a booster vessel. `VesselReader.SampleSrbs` closes
  it: `srb/<n>/` (SPEC §3.4.8) reports each motor's grain mass / usable mass / fraction / burn time /
  mass flow, chamber + exit conditions, burning area and stack validity, with a per-segment
  `segments/<m>/` breakdown — so `mass/propellant` − Σ `tanks/` = Σ `srb/<n>/mass` is checkable from
  `/sim`. **Read-only**: KSA forces a solid's throttle to 0 or 1 (`SolidMotor.UpdateState`), so
  ignition stays on the engine surface and `srb/<n>/engine` cross-links to `engines/<n>`.
- **Free win:** `/sim/debug/refuel` (`Vehicle.RefillConsumables()`) now walks `ISubstanceStore` instead of
  `Tank`, so it refills SRB grains too — no gatOS change needed.

---

## Vessel detail reads — `VesselReader.BuildFull` (gated by `telemetry_vessel_detail`)

Same file; the full single-pass build runs inside a whole-pass try/catch in `VesselReader.Sample` — if
any extension API drifts, the vessel falls back to `BuildCore` (core telemetry only) and the extension
dirs vanish (logged once). The structural animation↔module links (IsSolar, solar/light AnimationIndex)
are cached per vehicle in `Readers/AnimationLinks.cs` (GP3), rebuilt on module-count change or every 10 s.

### Position / navball / environment

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|
| `position/ecl`, `velocity/cci`, `com` | `:154-156` | `Vehicle.GetPositionEcl()`, `GetVelocityCci()`, `CenterOfMassAsmb` | `KSA/Vehicle.cs` | Low | ✅ |
| `navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | `:223` | `Vehicle.NavBallData.{AttitudeAngles(int3 deg),ThrustWeightRatio,DeltaV,Frame,Speed}` | `KSA/NavBallData.cs` | Medium | ⚠️ **5117: renamed + semantic drift** (rev 5114) — see below |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | `:235` | `Vehicle.PhysicsEnvironment.{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}`; `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`; `Vehicle.AccelerationBody`/`AngularAccelerationBody` | `KSA/PhysicsEnvironment.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | `:199` | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly.Degrees}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `encounters` (NDJSON) | `:573` | `Vehicle.Patch.Encounters`; `Encounter.{Body.Id,GameTime,ClosestDistance}` | `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | Medium | ✅ |

### Writable-setpoint read-backs (so `ctl/*` files report the real state)

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|
| `ctl/throttle` | `:169` | `Vehicle.GetManualThrottle()` | `KSA/Vehicle.cs` (`:824`) | Medium | ✅ ᵈ |
| `ctl/rcs` | `:170` | any `ThrusterController.IsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/attitude_mode` | `:171` | `FlightComputer.AttitudeMode` / `AttitudeTrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅ |
| `ctl/attitude_frame` | `:172` | `FlightComputer.AttitudeFrame` | `KSA/FlightComputer.cs` | Medium | ✅ |

### Per-module reads

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|
| `engines/<n>/{throttle,propellant,min_throttle}` | `:278` | `EngineControllerState.{CommandThrottle,IsPropellantAvailable}`; `EngineController.MinimumThrottle` | `KSA/EngineControllerState.cs` | Medium | ✅ |
| `srb/<n>/*` + `srb/<n>/segments/<m>/*` | `:490` | `Vehicle.Parts.RocketCores.{Modules,GetState}` filtered to `SolidMotor`; `SolidMotor.{Stack,Propellant,DefaultGeometry,UnburnableGrainMass,AreaRatio,ComputeBurningArea}`; `SolidGrainSegment.{Grain,Propellant,InitialGrainMass,UnburnableGrainMass,CasingInnerRadius,Length,GrainVolume,ComputeGrainDepth}`; `RocketCoreState.{Throttle,IsPropellantAvailable,MassFlowRate,ThrustTimeRemaining,Conditions}` | `KSA/SolidMotor.cs`, `KSA/SolidGrainSegment.cs`, `KSA/RocketCoreState.cs` | Medium | ✅ **new (5018)** ˢ |
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

`gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs`. Surfaces a vehicle's top-level parts at
`vessels/by-id/<id>/parts/<n>/`, **each with its subparts nested at `parts/<n>/subparts/<m>/`**
(2026-07-16: a subpart is a full `Part` with its own `InstanceId`) — the anchor picker for the
**welds** write surface ([`ksa-write-surface.md#welds`](ksa-write-surface.md#welds)); either level's
`instance_id` is a valid weld anchor. Cached per vehicle in a `ConditionalWeakTable<Vehicle,…>`
(collected with the vehicle, no leak, game-thread only); rebuilt only on a `Vehicle.Parts.Count`
change (the cheap "vehicle was edited" signal — KSA exposes no part-tree version/dirty flag; subpart
counts are template-fixed, so the top-level count stays the right signal) or every 10 s of sim time.
Hot path = one `Parts.Count` read per vehicle per tick. Anchor: `PartsReader.cs:30`.

| `/sim` path (under `…/parts/<n>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 5018 |
|---|---|---|---|---|---|---|
| `instance_id` | `PartsReader.cs:60` | `Part.InstanceId` | `KSA/Part.cs` | uint (the **stable** weld handle) | Low | ✅ |
| `id` | `:60` | `Part.Id` | `KSA/Part.cs` | string (can collide across instances) | Low | ✅ |
| `display_name` | `:60` | `Part.DisplayName` | `KSA/Part.cs` | string | Low | ✅ |
| `template` | `:60` | `Part.Template.Id` | `KSA/Part.cs` | string | Low | ✅ |
| `is_root` | `:61` | `Part.PartParent is null` | `KSA/Part.cs` | flag | Low | ✅ |
| `subpart_count` | `:61` | `Part.SubParts.Length` | `KSA/Part.cs` | int | Low | ✅ |
| `position` | `:58,62` | `Part.PositionVehicleAsmb` | `KSA/Part.cs` | `x y z` m (vehicle assembly frame) | Low | ✅ |
| `subparts/<m>/{instance_id,id,display_name,template}` | `:71-83` | `Part.SubParts` → `Part.{InstanceId,Id,DisplayName,Template.Id}` | `KSA/Part.cs` | as above | Low | ✅ |
| `subparts/<m>/position` | `:80` | `Part.PositionVehicleAsmb` (subpart-aware: composes through `PartParent.MatrixAsmb2VehicleAsmb`) | `KSA/Part.cs` | `x y z` m (vehicle assembly frame) | Low | ✅ |
| (enumeration) | `:40,53` | `Vehicle.Parts.{Count,Parts}` | `KSA/Vehicle.cs`, `KSA/PartTree.cs` | span of `Part` | Low | ✅ |

Verified `2026-06-28` against `2026.6.9.4750` (new feature; compiled clean — none of these
`Part`/`PartTree` members appear in the 4680→4750 changelog). Re-verified 2026-07-03 against
`2026.7.3.4826`: the `Part.cs`/`PartTree.cs` churn (+438/+48) is additive symmetry/sequence-group
infrastructure (`PartSymmetryInstance`, `SequenceGroup`/`SequenceOrder`, `AlignedConnectors`, a new
`PartTree.Decouplers` hot-path list) — every bound member above is unchanged. Re-verified 2026-07-14
against `2026.7.5.4892`: the `Part.cs`/`PartTree.cs` churn is the decoupling perf refactor (bulk-change
guards, single-pass subtree transfer, the `_moduleIdxsById` swap-removal **fix** — stale-index lookups
after part removal now impossible, an upstream correctness *improvement* for all `Modules.Get<T>` reads)
plus fuel-line plumbing; every bound member above is unchanged. Re-verified 2026-07-16 against
`2026.7.6.4939`: the `Part.cs` churn is symmetry-group XML expansion (`SymmetryGroupRef`), fuel-flow
highlight plumbing, and the tank-transfer UI buttons; `PartTree.cs` gains fuel-line/resource-manager
rebuild plumbing — every bound member above is unchanged.

2026-07-16 (feature extension, same 4939 baseline): subparts are now surfaced under
`parts/<n>/subparts/<m>/` — no **new** KSA members (the anchor already listed `Part.SubParts` for
`subpart_count`); the reader now also reads `InstanceId`/`Id`/`DisplayName`/`Template.Id`/
`PositionVehicleAsmb` **on the subpart instances**. The one semantic to watch on future bumps:
`Part.PositionVehicleAsmb` must stay subpart-aware (the `IsSubPart` branch composing through
`PartParent.MatrixAsmb2VehicleAsmb`, `KSA/Part.cs`) — it is what makes subpart rows (and subpart weld
anchors) truthful.

`parts/json` (same date) is a **game-free projection** — the `SimJson` serialization of the sampled
`PartSnapshot` list (memoized on the list reference in `SimFsTree.PartsJsonFile`; re-serialized only
when the reader rebuilds). No KSA coupling of its own, so no row: it breaks only if the rows above do.

---

## IVA cabin physics — `InteriorGeometry` + the driver's forcing terms (per-frame, game thread) {#iva-physics}

`gatOS.GameMod/Game/Ksa/Iva/`. Like the thug_life anchor math below, these are **per-frame reads inside a
driver**, not sampler reads, so they do **not** go through `SimSnapshot` — but unlike thug_life they run on
the **game thread** in `OnAfterUi` (after `JobSystems.VehicleSolvers.Wait()`), which is what makes the
kinematics settled and the reads race-free. All of it is gated behind the `debug/iva/enabled` master
switch: while it is off none of these members is touched at all.

| read | gatOS site | KSA / Brutal member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|
| forcing terms (the whole physics model) | `IvaPhysicsManager.Update`/`DriveVessel` | `Vehicle.{AccelerationBody,AngularAccelerationBody,BodyRates,CenterOfMassAsmb}` | `KSA/Vehicle.cs` | Low | ✅ |
| park gates | `IvaPhysicsManager.ParkReason` | `Program.{Editor,MainViewport}`; `Viewport.Mode`; `CameraMode.IVA`; `Universe.SimulationSpeed` | `KSA/Program.cs`, `KSA/Viewport.cs`, `KSA/Universe.cs` | Low | ✅ |
| interior collision geometry | `InteriorGeometry.Build`/`Emit` | `Vehicle.Parts.Parts`; `Part.{SubParts,InstanceId,Modules,MatrixAsmb2VehicleAsmb}`; `ModuleList.Get<PartModelModule>()`; `PartModelModule.PartModel.Template`; `PartModelModule.Template.{Internal,RayTracing,Mesh}`; `MeshReference.PositionCompare`; `double3.Transform(double3,double4x4)` | `KSA/Part.cs`, `KSA/PartModelModule.cs`, `KSA/PartModel.cs`, `KSA/MeshReference.cs` | **Medium** | ✅ |
| fallback room (no interior meshes) | `InteriorGeometry.BuildFallbackRoom` | `PartTree.Modules.Get<IVASeat>()`; `IVASeat.PositionAsmb` | `KSA/IVASeat.cs`, `KSA/PartTree.cs` | Low | ✅ |
| collision-proxy sizing | `IvaPhysicsManager.TryMeasure` | `Part.{Modules,Scale}`; `MeshReference.PositionCompare` | `KSA/Part.cs`, `KSA/MeshReference.cs` | **Medium** | ✅ |
| adopt-time seed pose | `FloatingObject.ReadBodyPose` | `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` (subpart-aware) | `KSA/Part.cs` | Low | ✅ |
| liveness / subpart lookup | `IvaPhysicsManager.{IsLive,FindSubPart}` | `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.Parts`; `Part.{SubParts,InstanceId}` | `KSA/Universe.cs`, `KSA/Part.cs` | Low | ✅ |

**Two load-bearing semantic assumptions**, both worth re-checking on any game update because a *silent*
change would be worse than a rename (which the build catches at the `[KsaAnchor]` site):

1. **`Vehicle.AccelerationBody` is true proper (non-gravitational) acceleration in every flight
   situation.** `VehicleUpdateTask` leaves it **zero** in `Freefall` (`ApplyFreefallMotion`), sets it to
   the `GM/r²` normal-force reading rotated into body axes when `Landed`/`Floating` (`ApplySurfaceMotion`),
   and accumulates thrust + drag + buoyancy with gravity excluded when `Maneuvering`/`Rolling`/`Sailing`
   (`IntegrateVelocityVerlet`); it is normalized to m/s² at the end of the step
   (`AccelerationBody /= SimStep.DeltaTime`). This is the single fact that lets one formula cover pad,
   coast, burn and landing with no `Situation` switch. **A live sanity check exists with zero new code:**
   `cat /sim/vessels/by-id/<id>/environment/g_force` must read ≈ 1.0 on the launch pad.
2. **`PartModelModule.Template.Internal` means "renders only through the IVA camera"** — `PartModel`'s
   render gate is `(!Template.Internal || viewport.Mode == CameraMode.IVA)`, the same flag the
   `always_render_iva` cheat flips. That is what makes it an exact, art-driven classifier for "surfaces a
   person inside the cabin can touch", so interior collision geometry needs no hand-authored volumes and
   adapts to modded interiors for free. If the flag's meaning changes, the interior mesh silently becomes
   wrong — `cat /sim/debug/iva/interior` (triangle/part counts + AABB) is the in-game diagnosis.

`MeshReference.PositionCompare` is a de-indexed `double3[]` triangle soup in part-local coordinates, built
at load and **retained forever** because KSA's own mouse picking (`Part.RayCastEgoSubPart` →
`ray.RaycastWatertight(meshView.PositionCompare, …)`) needs it — so reading it costs nothing and cannot be
null-but-loaded. The `debug/iva/**` reads under `/sim` are **not** KSA reads: they are a game-free
projection of `IvaPhysicsManager.Snapshot()`, which `TelemetrySampler` copies into `SimSnapshot.Iva`.
Verified `2026-07-24` against `2026.7.9.5018` (new feature; compiled clean). **Live in-game check
pending** — see `docs/VALIDATION.md`.

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

| read | gatOS site | KSA / Brutal member | Decomp file | Risk | 5018 |
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
live pose check still advised (render internals; see `docs/VALIDATION.md`). Re-verified (static)
2026-07-14 against `2026.7.5.4892`: `Camera` gains an additive **orthographic** mode (editor gizmo use;
the in-flight main camera stays perspective, `MVP`/`viewProjection` shape unchanged);
`Vehicle.GetMatrixAsmb2Ego` and the `Part` ego members untouched — live pose check still advised.

---

## FX editors — `FxEditorReader` (gated by `[control] debug_namespace`) {#fx-editors}

`gatOS.GameMod/Game/Ksa/Fx/FxEditorReader.cs` (plans/FX_EDITORS_PLAN.md; issue #2). Samples the four FX
families into `SimSnapshot.FxEditors` (`FxEditorsSnapshot`: `PlumeTemplates`, `Trail`, `CloudBodies`,
`TerrainBodies`, `TerrainGlobal` — each an `FxEntitySnapshot` of **concrete field path → components**),
which is what materializes the `/sim/debug/{engineplume,plumetrail,clouds,terrain}` leaves. Off ⇒ the
families are never read and `FxEditors` stays null, so no transport serves them.

Two properties make this section short: **every value is read through the same accessor the write goes
through** (`PlumeActuator.TryRead`, `TrailActuator.TryRead`, `CloudActuator.TryRead`,
`TerrainActuator.Read` — so read-back round-trips a write, and the pristine capture records a real live
value), and the **write-surface page already lists those members** —
[`ksa-write-surface.md#fx-editors`](ksa-write-surface.md#fx-editors). The rows below are the reads that
are *only* the reader's: the rosters and the two reflective handles it needs.

| read | gatOS site | KSA member | Decomp file | Unit/format | Risk | 5056 |
|---|---|---|---|---|---|---|
| plume-template roster | `FxEditorReader.SamplePlumeTemplates` | `FxReflect.PlumeTemplates` → reflected `VolumetricExhaustTemplate.References` (internal static `SerializedCollection<…>`) `.GetList()`; latch `fx.plume_templates` | `KSA/VolumetricExhaustTemplate.cs:37`, `KSA/SerializedCollection.cs` | list of templates | **High** | ✅ |
| plume-template roster (fallback) | `FxEditorReader.HarvestPlumeTemplates` | `Universe.CurrentSystem.All.UnsafeAsList()`; `Vehicle.Parts.RocketNozzles.ModulesAndAllStates`; `RocketNozzle.ReactionPlumes[].VolumetricExhaust.Id`; `VolumetricExhaustTemplate.Get(string)` | `KSA/RocketNozzle.cs:15,40`, `KSA/VolumetricExhaustReference.cs`, `KSA/VolumetricExhaustTemplate.cs:48` | ids of templates in use | Medium | ✅ |
| trail settings | `FxEditorReader.SampleTrail` | `FxReflect.Trail` (reflected `Program.Instance._volumetricTrailRenderer`) → `TrailActuator.TryRead` public fields | `KSA/Program.cs:160`, `KSA/VolumetricTrailRenderer.cs:173-196` | floats (single precision) | **High** | ✅ |
| cloud-body roster + fields | `FxEditorReader.SampleCloudBodies` / `ExpandClouds` | `AtmosphericBody.BodyTemplate.CloudsReference` over `Universe.CurrentSystem.All.UnsafeAsList()`; layer count and `layer.VolumetricCloud.CloudTypes.Count` define which indexed leaves exist | `KSA/AstronomicalTemplate.cs:60`, `KSA/CloudsReference.cs`, `KSA/VolumetricCloudReference.cs` | km / m / m/s / rgb / vec3 (per leaf) | Low | ✅ |
| terrain-body roster + fields | `FxEditorReader.SampleTerrainBodies` | `Universe.CurrentSystem.All.UnsafeAsList()`; `PlanetRenderer.RenderUboSlot`/`MeshUboSlot` (only slotted bodies); values via `TerrainActuator.Read` out of the reflected `_renderUboMap`/`_meshUboMap` rings (latch `fx.terrain_ubo`) | `KSA/PlanetRenderer.cs:37-125,250-252,374,379` | m / deg / px / – | **High** | ✅ |
| terrain global | `FxEditorReader.SampleTerrainGlobal` | `PlanetRenderer.Wireframe` (public **instance** field) via `Program.GetPlanetRenderer()` | `KSA/PlanetRenderer.cs:216`, `KSA/Program.cs:491` | flag | Medium | ✅ |

**Terrain reads come out of the UBO**, i.e. the struct the GPU actually samples (frame slot 0 at the
body's slot index) — not from the reference objects — which is why read-back is the live truth and why a
degraded `fx.terrain_ubo` **empties the per-body roster** while `wireframe` stays live.

**Memoization (the `parts/json` precedent, on steroids).** The whole surface is rebuilt only when an FX
write landed since the last build (`FxEditorReader.Invalidate`, bumped by every successful actuator
write) **or** 2 s elapsed (`Stopwatch.Frequency * 2` — the beat that catches edits made in the game's own
imgui editors). Otherwise the previous `FxEditorsSnapshot` is republished **by reference**, so an idle
tick costs one comparison and allocates nothing; the `<entity>/json` documents memoize on the field
dictionary's reference on top of that. The sampler call is wrapped in its own try/catch (logged once via
`_fxErrorLogged`) and can never fail a tick — a family that cannot be read is simply empty/absent.

Values are **32-bit floats** in the game, so a read-back is single-precision, and integer-valued counts
are rounded on apply (documented in [SPEC §3.7](../SPEC_9P_FILESYSTEM.md)). Verified `2026-08-01` against
`2026.7.10.5056` (new feature; the column above ticks `5056` while the rest of this page still ticks the
`5018` playbook pass). Live pass pending — `docs/VALIDATION.md`.

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

## ⚠️ 5117 read-surface findings (playbook pass 2026-08-01) {#5117-findings}

Full playbook pass 2026-08-01, `2026.7.9.5018` → `2026.8.3.5117`. The pass deliberately spans **both**
the 5056 and 5117 drops (revs 5019–5116, 97 revisions): 5056 was a build-only bump whose changelog/decomp
diff was never run, so 5018 was the last fully-audited baseline. **One compile break, fixed; two silent
semantic drifts found; everything else clean.** Build + full test suite green against the 5117 DLLs
(0 warnings, 769 passed / 11 skipped).

- **⚠️ COMPILE BREAK + SEMANTIC DRIFT, FIXED — `NavBallData.DeltaVInVacuum` → `DeltaV` (rev 5114).**
  `VesselReader.SampleNavball` was the single call site; anchor re-verified
  `2026-08-01`/`2026.8.3.5117`. The rename is the *visible* half — **both** navball performance values
  changed meaning at the same revision, and only one of them failed to compile:
  - `navball/deltav`: was `TotalEngineExhaustVelocity × ln(TotalMass / InertMass)` — a naive whole-stack
    vacuum rocket equation — now `Parts.PerformanceSequences.FindActiveSequenceDeltaV()`, i.e. the
    **currently active staging sequence's** propellant-aware Δv (revs 5035/5036/5114 made the flight
    computer stop crediting thrust from engines that are out of propellant, and made sequences end at
    the first decoupling engine group).
  - `navball/twr`: **compiles clean, value silently changed.** The numerator moved from
    `FlightComputer.VehicleConfig.TotalEngineVacuumThrust` to
    `ComputeActiveThrust(_environment.AtmosphericPressure)` — so TWR is now atmosphere-corrected
    (lower at sea level) and excludes engines incapable of producing thrust.

  Per the maintainer's call (2026-08-01) this pass made the **binding fix only**: no `SimSnapshot` field
  rename and no SPEC rewording. Consequence to be aware of: `NavballSnapshot.DeltaVVacuumMs` and
  SPEC §3.4.3's "Remaining vacuum Δv" now describe a value that is neither vacuum-based nor whole-stack.
- **⚠️ SEMANTIC DRIFT — substance phase *names* changed (rev 5095).** `SubstanceTemplate` gained a
  `DefaultPhase` XML attribute and a `BuildPhaseName` helper: the default phase now renders **bare**,
  and the non-default phases take a qualifier (`Gas` → `"X Vapor"`, `Liquid` → `"Liquid X"`, `Solid` →
  `"X Ice"`). Previously every phase was prefixed (`"Solid X"` / `"Liquid X"` / `"Gaseous X"`).
  `Content/Core/Volatiles.xml` + `SolidPropellants.xml` confirm the assignments. Net effect on the
  published `/sim` strings (`tanks/<n>/substance` ← `Mole.SubstancePhase.Name`, `srb/<n>/substance` ←
  `SolidMotor.Propellant.Name`): `"Liquid Kerosene"` → **`"Kerosene"`**, `"Solid APCP"` → **`"APCP"`**,
  while gas-default substances keep their liquid names (`"Liquid O2"`, `"Liquid H2"`, `"Liquid CH4"`).
  **No gatOS code change** — the game's string is passed through verbatim — but any guest program,
  example or tutorial matching substance names by string breaks. Flagged for live confirmation.
- **⚠️ BEHAVIOURAL DRIFT — encounter population (revs 5106/5110).** The `Encounter` struct gatOS binds
  (`Body`, `GameTime`, `ClosestDistance`) is intact and gained `TaMainOrbit`, but *which* closest
  approaches are published changed: only those from the final trajectory (including planned burns), plus
  a fix for excessive entries when the orbiter's period greatly exceeds the target's. Affects the row
  count and contents of `encounters/<n>/`. No code change; live re-verify.
- **⚠️ BEHAVIOURAL DRIFT — docking absorbs by size (rev 5076).** "Larger vehicles absorb smaller vehicles
  when docking", plus a contact-docking origin-snap fix (rev 5061). gatOS's `InputEvents`-mediated
  docking path is unchanged, but **which vessel id survives a dock** can now differ — a `/sim/vessels/<id>`
  identity consideration for docking programs. No code change; live re-verify.
- **✅ Part matrix caching (rev 5112) — safe.** `Part` gained a cached `_matrixAsmb2VehicleAsmb` (a real
  time-warp win), and the existing `_positionVehicleAsmb`/`_asmb2VehicleAsmb` caches switched from
  identity- to NaN-sentinels. This *could* have silently served stale poses to the IVA cabin driver,
  which writes part poses every frame — but the `PositionParentAsmb`, `Asmb2ParentAsmb` and `Scale`
  setters all call `ResetCachedPosMatrixValues()`, which is exactly the path `FloatingObject` and
  `ScaleActuator` write through. No change required.
- **✅ Additive-only on the bound module surface**: `Vehicle` gained `Crew`/`SeatCount`/`HasLaunched`/
  `CanRecover`/`AddCrew`/`RemoveCrew` (kitten roster, revs 5074–5105) and `SolidMotor` gained
  `FilledFraction`/`DrawFillBar` (rev 5084) — no bound member moved. `Vehicle.PropellantMass`,
  `IsControllable`, `GetManualThrottle`, `Parts`, and every `Modules.Get<T>` state struct are unchanged.
- **⚠️ Not yet surfaced (new game features, no action required)**: vehicle **destruction** by structural
  g-limit / dynamic-pressure limit (rev 5115, `VehicleStructuralLimits.cs`, `VehicleDestructionCause.cs`,
  `WreckageMarker.cs`), vehicle **recovery** (rev 5101), and the **kitten roster / crew** model
  (revs 5074–5105). All are candidates for future additive reads; note that destruction gives vessels a
  new way to disappear mid-flight, which the despawn-pruning paths already tolerate.

---

## ⚠️ 5018 read-surface findings (playbook pass 2026-07-24) {#5018-findings}

Full playbook pass 2026-07-24, `2026.7.8.4980` → `2026.7.9.5018` (changelog gapless — `fromRevision`
4980, revs 4981–5018 logged). **One compile break, fixed; one coverage gap opened; everything else
clean.** Build + full test suite green against the 5018 DLLs (0 warnings, 681 passed / 11 skipped).

- **⚠️ COMPILE BREAK, FIXED — `Mole.GetLiquidMass` → `Mole.GetStoredMass` (rev 4992, solid rocket
  motors).** The whole propellant-storage layer was generalized from `Liquid` to `Liquid | Solid` behind
  a new `ISubstanceStore` interface: `Mole` gained `IsSolid`/`Solid`/`IsStorable`,
  `GetLiquidMass`/`GetLiquidVolume` became `GetStoredMass`/`GetStoredVolume`,
  `ConsumeLiquid`/`ProduceLiquid` became `ConsumeStored`/`ProduceStored`, and `ContainsLiquid` was
  deleted. `VesselReader.SampleTanks` (`tanks/<r>/capacity`) was the single call site;
  anchor re-verified `2026-07-24`/`2026.7.9.5018`. **Zero value change** — `Tank` moles are liquids and
  `GetStoredMass` reduces to `Liquid.ComputeMass` for them.
- **✅ COVERAGE GAP CLOSED — the new `srb/<n>/` read surface.** The new `SolidGrainSegment` module
  (`KSA/SolidGrainSegment.cs`) holds one solid `Mole` and implements `ISubstanceStore`, but it is
  **not a `Tank`**, so `Modules.Get<Tank>()` never sees it and `tanks/` cannot show a booster. At the
  same time `Vehicle.PropellantMass` is now recomputed from `Parts.SubstanceStores`
  (`VehicleProperties.RecomputeMassProperties` retyped `ReadOnlySpan<Tank>` →
  `ReadOnlySpan<ISubstanceStore>`) and **does** include grain mass — so on a booster vessel
  `mass/propellant` > Σ `tanks/<r>/amount`. `VesselReader.SampleSrbs` closes this with a dedicated
  `srb/<n>/` tree (SPEC §3.4.8): stack validity, propellant/grain identity, mass / usable mass /
  fraction / burn time / mass flow, chamber + exit conditions, burning area, and a per-segment
  `segments/<m>/` breakdown — so `mass/propellant` − Σ `tanks/` = Σ `srb/<n>/mass` is now checkable
  from `/sim`. Read-only by design (see the ˢ footnote).
- **Encounter candidacy widened (rev 4991)** — `PatchedConic` replaced the flat
  `SphereOfInfluence <= 10_000_000` cutoff with an orbital-geometry test (radius-band overlap + an
  approximate MOID at the mutual nodes, `ENCOUNTER_MOID_SOI_MARGIN = 4.0`), and also excludes siblings
  orbiting entirely inside our periapsis. `FlightPlan.FAST_SOI_PATCH_SIZE` was removed (unbound).
  `Encounter.cs` is byte-identical and `Vehicle.Patch.Encounters` is unchanged, so this is **more rows,
  same shape**: small-SOI moons (Phobos, Deimos) now produce `encounters/<n>/` entries that 4980 silently
  skipped. Guest programs that assumed "no encounter entry ⇒ no approach" for small moons should be
  re-checked. Rev 4989 additionally ends a patch on a NaN `timeToFirstEncounter` (was infinity-only).
- **Module storage restructured, API-compatible (rev 4990)** — `Module.List` now stores same-concrete-type
  modules in contiguous segments (`IModuleTypeList.GetUsing<T>()` → `GetSegmentUsing<T>(int, out bool)`,
  new `ModuleList.SegmentEnumerator<T>`, `ModuleStateful.StateList` sync callbacks). **gatOS is
  unaffected**: it only calls `ModuleList.Get<TModule>()` / `HasAny<TModule>()` / `GetState` /
  `TryGetFrom` / `GetModuleAndAllMutableStatesForInitialization`, all signature- and semantics-identical.
  Worth noting for the positional `/sim` indices (`engines/<n>`, `rcs/<n>`, …): those come from a single
  concrete-type span, which segmentation does not reorder.
- **`ModuleBase.OnPartCreated` → `OnFullPartCreated`** — a virtual gatOS never overrides; the `Part.cs`
  call site is unchanged, so the `SolarPanel.KeyframeAnimationModule` link that `AnimationLinks` reads is
  still established at the same point.
- **Power modules gained `IFlowManagerHost` (additive)** — `SolarPanel`, `Generator`, `PowerConsumer` now
  implement it (a `RecreateManager`/`OnDrawUi` pair hoisted out of `Vehicle.OnDrawUi`). Every bound state
  field (`SolarPanelState`, `GeneratorState`, `PowerConsumerState.Consumed`) is untouched.
- **`Part.Connector` gained `Capabilities`/`EndpointCapabilities` (rev 4992/5007, additive)** — new
  `ConnectorCapability` flags (Electricity / BulkFluid / ServiceFluid / SolidMotorCase / DecouplerJoint).
  rev 5007 swapped `_decouplerConnections` for the `DecouplerJoint` flag, but `Decoupler.cs` is
  byte-identical and gatOS binds only `Decoupler.IsActive`/`SetIsActive`, so decoupler reads and writes
  are unaffected. `PartsReader`'s bound members (`InstanceId`/`Id`/`DisplayName`/`SubParts`/`Scale`/
  `PositionVehicleAsmb`/`Asmb2VehicleAsmb`) are unchanged.
- **No frames/numerics drift** — nothing under `Brutal.Core.Numerics` (or any `Brutal*` decomp namespace
  except `RenderCore/SimpleVkTexture.cs`, which only lost a `[Conditional("DEBUG")]` logging helper)
  changed. `Universe.cs`, `Orbit.cs`, `Celestial.cs` member surface, `NavBallData.cs`, `Encounter.cs`,
  `Battery.cs`, `RocketControllerData.cs`, `EngineControllerState.cs`, `ThrusterController.cs`,
  `FlightComputer.cs`, `DockingPort.cs`, `Decoupler.cs`, `LightModule.cs`, `InputEvents.cs`, `Camera.cs`
  are byte-identical. `Content/Core/Astronomicals.xml` changed only in Earth ground-clutter/tree
  authoring — **no body mass, radius, SOI, or orbital-element edits**, so `/sim/bodies/*` is untouched.
- **`EngineController` covers SRBs for free** — `SolidMotor : RocketCore`, and an SRB is still an
  `EngineController` with `SolidMotor` cores, so `engines/<n>/{active,vac_thrust,isp}` populates for
  boosters. `EngineController`'s only diff is a `Combustor` type-test in save-data flow-rule handling;
  `MinimumThrottle`/`IsActive`/`VacuumData`/`SetIsActive` are unchanged. (Throttle *commands* to a solid
  are inert by physics, not by API — see the [write page](ksa-write-surface.md#5018-findings).)

---

## ✅ 4980 read-surface findings (playbook pass 2026-07-22) {#4980-findings}

Full playbook pass 2026-07-22, `2026.7.6.4939` → `2026.7.8.4980`. Build + full test suite green after
the one write-side fix (docking, see the [write page](ksa-write-surface.md#4980-findings)); **no bound
read member changed name, signature, type, unit, frame, or gating.** `Tank.cs`, `Mole.cs`,
`EngineControllerState.cs`, `ManualControlInputs.cs`, `ThrusterController.cs`, `BurnTarget.cs` are
byte-identical; `Orbit.cs`'s only churn is map-hover picking (`GetNearestPoint`/`GetNearestPosition`
gained a `spliceVehicleFromNow` param, `GetNearestPointIndex` removed — none bound); every element/
anomaly/period read and all `Celestial` atmosphere/ocean/rotation/radius accessors are untouched.
Findings — game-behavior changes the reads report faithfully, none a drift:

- **Undocked/decoupled vessels keep their names (rev 4950)**: new `Control.VehicleName` stamp +
  `Control.FindWinning`; `Vehicle.Split` uses the stored name for the separated vehicle when it is a
  valid unique id, else falls back to `GenerateSplitId`. gatOS reads `Name: vehicle.Id` — unchanged API,
  friendlier values: after undock/decouple the new vessel's `name` (and `vessels/by-id/<id>` key) is the
  persisted control-module name instead of `<parent>-<n>`.
- **Density-based fallback mass (rev 4955)**: a vehicle whose parts define no mass now reports
  `TotalMass`/`InertMass` from a 100 kg/m³ × bounding-box cuboid (`ComputeFallbackVehicleMassPropsAsmb`)
  instead of the old `EnsureNonzero()` floor — `mass/*` reads pass the new values through.
- **Physics-value drift, not read drift (rev 4977)**: the velocity-verlet fix (stale positions fed
  gravitation/drag; atmospheric forces now handled in the CCI frame — `PhysicsStates.ComputeDrag` gained
  an `airVelocityBub` param, `RecomputePositionalEnvironmentValues` moved after integrate) changes the
  *numbers* `acceleration`/`dynamic_pressure`/environment reads report at high warp, truthfully; members
  and units unchanged. Hard-coded rate constants replaced the `GameSettings.Current.Simulation.*` knobs
  (rev 4956) — none were bound.
- **Fuel-flow rework is drain-order only (revs 4957/4958/4965)**: new `FlowRule` enum + persisted
  `EngineController.PersistedFlowRule` + `CoreDrainState` govern *which tanks drain when* (default now
  furthest-to-nearest by stage); `tanks/<r>/{amount,capacity,fraction}`, `engines/<n>/propellant`
  (`IsPropellantAvailable`) and the moles read path are untouched and stay honest instantaneous reads.
- **Navball markers are additive (rev 4970)**: new `Vehicle.NavBallMarkerData`/`UpdateNavBallMarkers` +
  `NavballMarkers` shader lane; the bound `Vehicle.NavBallData` struct (`navball/*`) is untouched.
- **`FlightComputer.RCSMode` / `RollMode` (write-surface finding)**: no read gatOS samples reflects
  them today — `ctl/attitude_mode` read-back reports the mode gatOS set even while RCS-disabled
  physically ignores it. Candidate additive read; see the
  [write 4980 findings](ksa-write-surface.md#4980-findings).
- **Content XML**: the only schema-adjacent change is texture `Category="Terrain"` →
  `"TerrainHeight"` (rev 4947, streaming/collision alignment) on celestial texture elements — no
  orbital/body/module data change; `Astronomicals.xml`/`SolSystem*.xml` orbital schema untouched (the
  `apollo11-system` generator emits no texture `Category`, so its output stays valid). Default-vehicle
  XML fix-ups (rev 4941) are content-only.

---

## ✅ 4939 read-surface findings (playbook pass 2026-07-16) {#4939-findings}

Full playbook pass 2026-07-16, `2026.7.5.4892` → `2026.7.6.4939`. Build (forced non-incremental) + full
test suite green; **no bound member changed name, signature, type, unit, frame, or gating.** For the
first time the changelog is gapless (`fromRevision` 4892 = the prior baseline; revs 4893–4939 all
logged), and the decomp diff was taken between the two drops' commits inside the assemblies checkout
(`7cf5c0a..2423a02`). Findings — all *game-behavior* changes the reads report faithfully, none a drift:

- **Fuel-line / tank-transfer / propellant-use system (revs 4903/4907/4917/4936/4937/4938)**
  (`tanks/<r>/*`, `engines/<n>/propellant`, `mass/propellant`, `power/consumed`): the new `FuelPort`
  module, tank-to-tank in-flight transfer, and the per-tank propellant-use toggle are **additive** on
  `Tank` (`PropellantUseEnabled`, `TransferMode`, transfer statics) — **`Tank.Moles`, `Mole`/`MoleState`,
  `FilledFraction` and the whole moles read path are untouched**, so `tanks/<r>/{amount,capacity,fraction}`
  formats and units are unchanged. What changes is *when* engines see fuel: crossfeed no longer crosses a
  decoupler into a different stage (4917), fuel-line-fed stacks and daisy chains are now drainable
  (4903/4917), and a propellant-use-disabled tank walls its contents off from engines/thrusters/transfer
  (4938) — `engines/<n>/propellant` (`IsPropellantAvailable`) flips per the new rules, truthfully. An
  active tank transfer draws **20 W per draining tank** from the batteries (4907), visible in
  `power/consumed`.
- **Tank volume now displays in liters (rev 4934)**: a UI formatter change (`VolumeReference` liter
  units + new `Constants` conversions); tank *game data* moved from `PartGameData.xml` to
  `CoreFuelTankAGameData.xml` with the identical `<Tank>` element schema. gatOS reports kg / kg / 0..1 —
  no unit or SPEC change. `Sequence.TankMatchesMix` was removed (rev 4911) in favor of
  `Tank.HoldsMixSubstances` — gatOS bound neither.
- **Animating parts get real physics (rev 4930 + `VehicleUpdateTask`)**: `KeyframeAnimationModule` gains
  `AnyAnimating`; a vehicle with a running animation (or a solar tracker moving ≥ 2°, via the new
  `IKeyframeAnimationExtension.IsAnimating`) is now forced **off-rails**, and animations mark their
  subtree colliders for update (landing legs finally collide correctly). `animation/<n>/*` read members
  (`TimeGoal`, `Shared.Duration`, `State.{TimeCurrent,DeploymentState}`) are untouched; guests will
  observe `situation` staying physics-simulated while animations run.
- **In-flight Sequence UI rework (revs 4893/4899/4905/4906/4919)**: the `SequenceList.cs` +1137-line
  churn is entirely window drawing (GaugeCanvas integration, group expand/collapse, per-engine fuel
  bars); `ActivateNextSequence(Vehicle)` (`SequenceList.cs:127`) is byte-compatible. Sequences can now be
  re-ordered in flight — a game capability, not an API change.
- **CelestialSystem nearest-orbit-point fix (rev 4931)**: a hover-marker locals/threading refactor;
  `CurrentSystem.All`/`Get`/`HomeBody` untouched. `Celestial.cs`'s one change is a particle-emitter
  field rename (`Extra.Z` → `Opacity`) — not bound.
- **Electrical XML re-indent only**: `CoreElectricalAGameData.xml` was reformatted (rev 4918 asset
  update) — every `MaximumCapacity J=` / `Produced W=` / `Consumed W=` stock value is identical.
  `Content/Core/Astronomicals.xml` churn is ground-clutter LOD tuning only — no celestial physics data.
- **Old service-module parts removed (rev 4915, save-breaking upstream)**: vehicles using them break on
  load — live validation should start from fresh vehicles (second save-breaker after 4884).
- **Additive members, not yet surfaced**: `Tank.{TransferMode,PropellantUseEnabled}` and the fuel-line
  graph are candidates for future additive reads/controls; no action required.

---

## ✅ 4892 read-surface findings (detail) {#4892-findings}

Full playbook pass 2026-07-14, `2026.7.3.4826` → `2026.7.5.4892`. Build (forced non-incremental) + full
test suite green; **no bound member changed name, signature, type, unit, frame, or gating.** Revs
4827–4859 have **no changelog** in either drop (the 4892 log covers 4860–4892), so the pass was driven by
`git diff` between the two drops' commits inside the assemblies checkout. Findings — all *game-behavior*
changes the reads report faithfully, none a member drift:

- **Combustion→Reactions / tank-affinity refactor (rev 4884, save-breaking upstream)**
  (`tanks/<r>/{amount,capacity,fraction}`, `mass/propellant`): `Tank` gains `RoleAffinity`,
  `AssignedMix`, `IsManuallyAssigned`, `Assign()`; tanks now auto-fill with the most sensible propellant
  mix for their affinity unless manually overridden. **`Tank.Moles`, `Mole`/`MoleState`,
  `Mole.FilledFraction` and the whole moles read path are untouched** — additive only. What guests *see*
  changes with the substance catalog: Nepetalactone/Actinidine (and the LR91 Dev engine) are gone,
  methalox/monoprop-hydrazine/APCP substances are new (revs 4884/4885), so tank resource *names* on new
  vehicles differ from the 4826 era. Formats and units (kg / kg / 0..1) unchanged — no SPEC change.
- **Honest per-engine throttle zero** (`engines/<n>/throttle`): `FlightComputer.CommandEngineThrottles`
  now explicitly writes `CommandThrottle = 0` / `CommandBurnTime = 0` to every engine when no burn is
  commanded (previously left stale). After a burn ends or on throttle cut, `engines/<n>/throttle` reads
  a truthful `0` instead of the last commanded value. Members/units unchanged.
- **On-rails behavior changes (rev 4866)** (`situation`, engine telemetry at high warp): vehicles set to
  "ignite" with **no propellant** no longer stay off-rails; far-away ocean-bound vehicles fast-path into
  the floating (on-rails) state; the "bottomed" seabed state now engages properly. `Situation` value
  *shape* unchanged — but guests at high warp will observe on-rails transitions in states that
  previously stayed physics-simulated (truthful reporting of new game behavior).
- **Module id-lookup fix (rev 4873)**: `_moduleIdxsById` swap-removal no longer leaves stale indices —
  an upstream **correctness improvement** for every `Modules.Get<T>`-backed read after parts are removed
  (previously a post-decouple lookup could return the wrong module).
- **Additive members, not yet surfaced**: `EngineController.SeaLevelData` (live Isp/dV work, rev 4868 —
  `VacuumData` reads unchanged), `PhysicsEnvironment.AtmosphereRadius` — both candidates for future
  additive reads, no action required.
- **SequencePerformance live recompute (revs 4868/4880)**: `SequencePerformanceList` is new and
  sequences are double-buffered for the UI — gatOS read neither at 4892. **Superseded at 5117**: rev 5114
  rewired `NavBallData.DeltaV` onto `Parts.PerformanceSequences.FindActiveSequenceDeltaV()`, so
  `navball/deltav` is now sourced from exactly this machinery — see
  [5117 findings](#5117-findings).
- **EVA spawn tweak (rev 4869)**: kittens now spawn just *outside* the door part (pushed along the
  door direction) and `KittenBackPackPart` gained a real 0.35 m collider — affects where a fresh EVA
  kitten vessel appears in position reads (benign; fixes the old spawn-spin).

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

| `/sim` path | gatOS site | KSA member | Decomp file | Unit | Risk | 5018 |
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
`MeanRadius` included — is untouched. Re-verified 2026-07-14 against `2026.7.5.4892`: the
`Celestial.cs`/`CelestialSystem.cs` changes are the particle-emitter `Handle` refactor + a
draw-ordering tweak (the controlled vehicle sorts first) — no catalog member touched;
`PhysicsEnvironment` gains an additive `AtmosphereRadius` field (not yet surfaced; candidate for a
future additive read).

---

## Coordinate frames (reference)
Reads cross several KSA frames — CCI (inertial), CCE/CCF (body-fixed), ECL (ecliptic), body frame. The
frame math (`GetCci2Ccf`, `GetBody2Cci`, `GetCce2Cci`, `GetLlaFromCcf`) is summarized in
[`ksa-runtime-coupling.md#frames-and-numerics`](ksa-runtime-coupling.md#frames-and-numerics) and detailed
in [`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md). A frame-math
change is the classic *silent* drift (compiles, wrong numbers) — re-verify against a live flight per the
playbook step 5.
