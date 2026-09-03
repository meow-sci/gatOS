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
| `time/ut` | `TelemetrySampler.cs:92` | `Universe.GetElapsedSeconds()` | `KSA/Universe.cs` | seconds, double | Low | ✅ |
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
| `mass/{total,dry,propellant}` | `:104-106` | `Vehicle.TotalMass` / `InertMass` / `PropellantMass` | `KSA/Vehicle.cs` | kg | Low | ⚠️ ˢ · ✅ **5348** (aggregation moved to `IInertMass`, values unchanged — see [5348 findings](#5348-findings)) |
| `orbit/{apoapsis,periapsis,ecc,inc,sma,period}` | `:75-82` | `Vehicle.Orbit` elements (radii→alt; inc rad→deg) | `KSA/Orbit.cs` | m / – / deg / s | Low | ✅ |
| `battery/{charge,fraction}` | `:86,339` | `Vehicle.Parts.Batteries.GetState(b).Charge.Value()` ÷ `b.MaximumCapacity.Value()` | `KSA/Battery.cs` | fraction 0..1 | Low | ✅ (G2) |
| `ctl/lights` (readback) | `:112` | `Vehicle.LightsOn` | `KSA/Vehicle.cs` | 0/1 | Low | ✅ |
| `ctl/engine` (readback) | `:125` | `Vehicle.IsSet(VehicleEngine.MainIgnite, false)` | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ ᵈ |
| `controllable` | `:133` | `Vehicle.IsControllable` (`_overrideIsControllable \|\| Parts.Controls.NumModules > 0`) | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ (G3, new) |
| `engines/<n>/{active,vac_thrust,isp}` | `:256` | `Vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData.{ThrustMax,MassFlowRateMax}` | `KSA/EngineController.cs` | bool / N / s | Medium | ✅ ᵈ · ⚠️ **5348: `active` moved** (staging is per-module now; RCS no longer self-activates at construction) — see [5348 findings](#5348-findings) |
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
| `navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | `:223` | `Vehicle.NavBallData.{AttitudeAngles(int3 deg),ThrustWeightRatio,DeltaV,Frame,Speed}` | `KSA/NavBallData.cs` | Medium | ⚠️ **5117: renamed + semantic drift** (rev 5114) — see below; ⚠️ **5348: `deltav`/`twr` values corrected** (rev 5318) — see [5348 findings](#5348-findings) |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | `:235` | `Vehicle.PhysicsEnvironment.{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}`; `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`; `Vehicle.AccelerationBody`/`AngularAccelerationBody` | `KSA/PhysicsEnvironment.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | `:199` | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly.Degrees}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `encounters` (NDJSON) | `:573` | `Vehicle.Patch.Encounters`; `Encounter.{Body.Id,GameTime,ClosestDistance}` | `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | Medium | ✅ re-verified (static) 2026-08-23 against `2026.8.22.5348` (rev 5266 changed a *different* list) |

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
| `srb/<n>/*` + `srb/<n>/segments/<m>/*` | `:490` | `Vehicle.Parts.RocketCores.{Modules,GetState}` filtered to `SolidMotor`; `SolidMotor.{Stack,Propellant,DefaultGeometry,UnburnableGrainMass,AreaRatio,ComputeBurningArea}`; `SolidGrainSegment.{Grain,Propellant,InitialGrainMass,UnburnableGrainMass,CasingInnerRadius,Length,GrainVolume,ComputeGrainDepth}`; `RocketCoreState.{Throttle,IsPropellantAvailable,MassFlowRate,ThrustTimeRemaining,Conditions}` | `KSA/SolidMotor.cs`, `KSA/SolidGrainSegment.cs`, `KSA/RocketCoreState.cs` | Medium | ✅ **new (5018)** ˢ · ⚠️ **5348: `area_ratio` is the *sizing* ratio** once `AreaRatioMultiplier != 1` — see [5348 findings](#5348-findings) |
| `rcs/<n>/{active,propellant,map}` | `:387` | `ThrusterController.IsActive`; `ThrusterControllerState.{ControlMap,IsPropellantAvailable}` | `KSA/ThrusterController.cs` | Medium | ✅ ᵈ · ⚠️ **5348: `ctl/stage` no longer flips `active`** (rev 5329) — see [5348 findings](#5348-findings) |
| `solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle}` | `:419` | `SolarPanelState.{Produced,IsOccluded,SunAoA,SunEfficiency}`; `SolarTrackerState.CurrentAngle` | `KSA/SolarPanel.cs`, `KSA/SolarTracker.cs`, `KSA/SolarPanelState.cs` | Medium | ✅ (G2: W) |
| `generators/<n>/{active,produced}` | `:476` | `GeneratorState.{Active,Produced}` | `KSA/Generator.cs`, `KSA/GeneratorState.cs` | Medium | ✅ (G2: W) |
| `lights/<n>/{on,brightness,color,inner_angle,outer_angle}` | `:500` | `LightModule.Template.{Intensity.Value,ColorRgb,OuterAngle.Value,InnerAngle.Value}`; `Parent.FullPart.LightSwitch.LightIsActive` | `KSA/LightModule.cs` | **High** | ✅ |
| `docking/<n>/{docked,docked_to,pushoff_impulse}` | `:540` | `DockingPort.{Docked,DockedToPart.Id,PushoffImpulse}` | `KSA/DockingPort.cs` | Medium | ✅ (fixed) |
| `decouplers/<n>/fired` | `:560` | `Decoupler.IsActive` | `KSA/Decoupler.cs` | Medium | ✅ ᵈ · ⚠️ **5348: `Decoupler` is a multi-instance component module** (ordinals stable on stock content) — see [5348 findings](#5348-findings) |
| `power/produced` | `:360` | Σ `SolarPanelState.Produced.Value()` + `GeneratorState.Produced.Value()` | `KSA/SolarPanelState.cs`, `KSA/GeneratorState.cs` | Medium | ✅ (G2: W) |
| `power/consumed` | `:374` | Σ `Vehicle.Parts.PowerConsumers.GetState(c).Consumed.Value()` | `KSA/PowerConsumerState.cs` | Medium | ✅ (G2: W) · ✅ **5348** (`PowerManager` rebuilt on `ElectricalCircuits`, rev 5326 — shape/unit unaffected; a former `*SameStage` brown-out can now read non-zero) |
| `battery/capacity` | `:342` | Σ `Battery.MaximumCapacity.Value()` | `KSA/Battery.cs` | Low | ✅ (G2) · ✅ re-verified 2026-08-23 against `2026.8.22.5348` (`Battery.cs`/`BatteryState.cs` byte-identical) |

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
the **game thread** in `OnAfterUi` (after `JobSystems.VehicleSolver.Wait()`), which is what makes the
kinematics settled and the reads race-free. All of it is gated behind the `debug/iva/enabled` master
switch: while it is off none of these members is touched at all.

| read | gatOS site | KSA / Brutal member | Decomp file | Risk | 5018 |
|---|---|---|---|---|---|
| forcing terms (the whole physics model) | `IvaPhysicsManager.Update`/`DriveVessel` | `Vehicle.{AccelerationBody,AngularAccelerationBody,BodyRates,CenterOfMassAsmb}` | `KSA/Vehicle.cs` | Low | ✅ |
| park gates | `IvaPhysicsManager.ParkReason` | `Program.{Editor,MainViewport}`; `IViewport.Mode` (5402; the public `Viewport.Mode` field before); `CameraMode.IVA`; `Universe.SimulationSpeed` | `KSA/Program.cs`, `KSA/IViewport.cs`, `KSA/Universe.cs` | Low | ✅ |
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
Re-verified (static) 2026-08-23 against `2026.8.22.5348`: `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`
is still the single overload, so the postfix these reads run inside still installs (its body is now
wrapped in `using (commandBuffer.TagRegion(Profiler.GpuTag.MeshRendererV2))` — a Harmony postfix runs
after the `finally`, so the quad draws are attributed outside that GPU tag; **profiler attribution only,
no mis-draw**). ⚠️ **The crew-portrait viewports are no longer unconditionally `Visible`** (revs
5276/5295), so on those two viewports these reads may simply never run — evidence on the write side,
[`ksa-write-surface.md#5348-findings`](ksa-write-surface.md#5348-findings).

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

## Camera state — `CameraReader` (gated by `[camera] camera_enabled`; per rendered frame) {#camera}

`gatOS.GameMod/Game/Ksa/Camera/CameraReader.cs` (plans/CAMERA_ASBUILT.md). **This read does not go
through `SimSnapshot`.** The camera moves every rendered frame — far faster than the telemetry publish
cadence — so the director samples the live camera at the end of `OnAfterFrame` and publishes a
game-free `CameraStatus` into `CameraStore` with **one volatile swap**; every `/sim/camera` leaf is a
`LiveLine`/`StaticTextFile` that formats from that snapshot per access, never snapshot-memoized. Nothing
here is gated by `telemetry_*`, and nothing here is sampled by `TelemetrySampler` (only the
`camera.shot`/`camera.finished` **events** ride the sampler, gated by `telemetry_events`).

| read | gatOS site | KSA member | Decomp file | Unit/format | Risk | 5168 |
|---|---|---|---|---|---|---|
| `camera/mode`, `camera/status`' `mode` | `CameraReader.Sample` → `ModeOf` | `IViewport.Mode` (a read-only interface property since 5402; a public field on `Viewport` before — **not** `Program.GetCameraMode()`, which reads the *frame* viewport); the `CameraMode` enum `{Orbit,Free,Map,IVA,Fixed}` mapped out member-by-member rather than cast | `KSA/IViewport.cs`, `KSA/CameraMode.cs` | token | Low–Medium | ➕ new 2026-08-06 |
| `camera/target`, `camera/status`' `follow` | `CameraReader.Sample` → `CameraTargets.Describe` | `Camera.Following` → `IFollowable`; `Astronomical.Id`. A `WreckageMarker` or `VehicleEditingSpace` is not addressable ⇒ both report `none` | `KSA/Camera.cs` | `vessel:<id>` \| `body:<id>` \| `none` | Low | ➕ new 2026-08-06 |
| `camera/tidal` | `CameraReader.Sample` | `Camera.TidalLocking` (get-only) | `KSA/Camera.cs` | flag | Medium | ➕ new 2026-08-06 |
| `camera/pose/fov` | `CameraReader.Sample` | `Camera.GetFieldOfView()` — ⚠️ returns **radians** while `SetFieldOfView(float)` takes **degrees**; converted here, once, at the boundary, so nothing downstream carries a radian | `KSA/Camera.cs` | degrees | Medium | ➕ new 2026-08-06 |
| `camera/pose/ortho` | `CameraReader.Sample` | `Camera.Orthographic` | `KSA/Camera.cs` | flag | Medium | ➕ new 2026-08-06 |
| `camera/map/scope`, `camera/status`' `map_scope` | `CameraReader.Sample` (+ `CameraDirector.SetMapScope`'s own publish) | `IGameViewport.MapController` (interface property since 5402); `MapController.Scope` (public `double`, `:34`) — the controller clamps it **up** to the followed object's `MeanRadius` every map frame, so a smaller written value reads back clamped | `KSA/IGameViewport.cs`, `KSA/MapController.cs` | metres | Medium | ➕ new 2026-08-06 |
| `camera/pose/geo` (from a Cartesian placement) | `CameraReader.WithBothSpellings` → `CameraFrames.TryEclToGeo` | `Celestial.{GetPositionCceFromEcl,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius}` — the exact inverse of `GetDirCcfFromLatLon` (`lat = asin(ccf.Z)`, `lon = atan2(ccf.Y, ccf.X)`), the pair KSA's own `GetLatitudeFromCce`/`GetLongitudeFromCcf` use | `KSA/Celestial.cs` | deg / deg / m **above terrain** | Medium | ➕ new 2026-08-06 · ⚠️ **5348: cube-face seam sampler changed** (sub-metre, seams only; the round-trip still inverts exactly) — see [5348 findings](#5348-findings) |
| `camera/pose/position` (from a geodetic placement) | `CameraReader.WithBothSpellings` → `CameraFrames.TryFrame2Ecl` + `CameraTargets.PositionEcl` | back-projection into the Cartesian channel's own frame (same members as the write side — [`ksa-write-surface.md#camera-director`](ksa-write-surface.md#camera-director)) | `KSA/Vehicle.cs`, `KSA/Celestial.cs` | metres, in `pose/frame` | Medium | ➕ new 2026-08-06 |

**Two properties keep this section short.** Every *composed* value (`pose/roll`, `smoothing`,
`orbit/*`, the aim family, `rotation`, `ortho_height`, `time_scale`, and the playback fields) is read
back from gatOS's **own** compositor + track player, not from the game — there is no KSA member to
list, and that is exactly the resync-after-restart property AGENTS.md §7 requires: a client that
reconnects mid-shot reads the *effective* value, not the last thing anyone wrote. And **both position
spellings are always published** — whichever one the author used, the reader fills in the other from
the point the placement actually resolved to, which is what the two back-projection rows above are.
Failure is silent and total: a Cartesian placement about a *vessel* anchor simply has no geodetic form
(latitude on a vessel is meaningless, and zeros would read as a real placement).

**One documented behaviour to keep in mind when reading these leaves:** `camera/target`, `camera/mode`,
`camera/tidal` and `camera/status` report **idle values while gatOS does not own the camera**. That is
the `AudioActuator._publishedEmpty` edge-latch discipline — an idle director publishes once and then
does nothing, which is what keeps the feature genuinely free when off — but it means those four leaves
do not mirror the *player's* camera. `camera/map/scope` is the deliberate exception: `SetMapScope`
publishes its own read-back, because map is precisely the mode gatOS does not own and the leaf would
otherwise report `0` for a value the guest had just written. Verified `2026-08-06` against
`2026.8.5.5168` (new feature; the column above ticks the feature's own verification date while the rest
of this page ticks the playbook passes). Live pass pending — `docs/VALIDATION.md`.

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

## ⚠️ 5402 read-surface findings (playbook pass 2026-09-02) {#5402-findings}

Full playbook pass 2026-09-02, `2026.8.22.5348` → `2026.9.7.5402`. **The changelog is gapped:** CURRENT's
`version.json` has `fromRevision` 5400 / `toRevision` 5402 and logs a single commit (rev 5401, a
thumbnail-stride crash fix), while PREVIOUS ends at 5348 — revs 5349–5399 carry **no commit messages**
in either tree, so per the version-diff method the **full decomp + Content diff was the discovery
mechanism** (294 decomp files, +16 221 / −3 942 lines; `git diff c465abb 57e6040` inside the
assemblies repo, PREVIOUS materialised as a `git worktree` of `c465abb`). Rev numbers are therefore
deliberately not cited below. **The build alarm fired — three compile breaks** (ten declaration-phase
errors hid six body-phase ones, exactly the "iterate to green" trap the playbook warns about): the
`KSA.Viewport` **class was deleted** in a viewport rework, `VolumetricTrailRenderer.DebugTrailColor`
was **removed**, and `Cursor.InputRay` was **removed**. All three are on the write/runtime surface
([write findings](ksa-write-surface.md#5402-findings)); **no bound read changed name, signature, type,
unit or frame** — everything on this page is semantic drift the reads report faithfully, plus new
engine behaviour that changes *what appears* on the surface. Clean `-t:Rebuild` of the whole solution
against the 5402 DLLs: **0 warnings, 0 errors**; `dotnet test gatos.slnx` **1646 passed / 12 skipped /
0 failed**.

The binary-level surface diff was run again (the 5348 technique, [pass record](ksa-assets-and-versions.md#5402-pass)):
**482** of the built `gatOS.GameMod.dll`'s 850 TypeRefs point into game assemblies; **482/482 resolve
in 5402** (474 in 5348 — the eight new ones are the viewport types gatOS now binds), **907/907
MemberRefs** resolve with identical name, parameter list and return/field type, **52 of 474** shared
referenced types changed declared shape (all in `KSA.dll`, 42 of them only by the `Viewport →
IViewport/IGameViewport` rename), and **all 222 referenced `Brutal.*`/`Planet.*`/Bepu types are
byte-identical** in declared surface even though every Brutal DLL was rebuilt. Every reflection string
(25 sites) resolves on the same declaring type with the same visibility; every Harmony target (15) is
present with exactly one overload.

### ⚠️ `parts/<n>/display_name` is now the authored template name (5402)

`Part..ctor` changed `DisplayName = Id;` → `DisplayName = (Template.DisplayName != Template.Id) ?
Template.DisplayName : Id;`. `parts/<n>/display_name` and `subparts/<m>/display_name` now report
`"Parachute Bay"`, `"Drogue Radial A"`, … where the template authors a name, and the runtime `Id`
otherwise — so the value is **no longer unique per instance**. `Id`/`InstanceId` are unchanged and remain
the addressing keys (welds, stickers, IVA adopt, camera `part:` anchors all key on `instance_id`).
`PartsReader.cs:30` re-stamped; SPEC §3.4 rows annotated. *No code change.*

### ⚠️ Crashes now spawn debris and fragment vessels that appear under `vessels/` (5402)

KSA gained a **structural-failure model**: parts carry `CrashTolerancePascals`/`InertMassKg`
(`PartTemplate.CrashTolerance`, e.g. `CorePropulsionAAssets.xml` gives EngineA2…A6 `3e6`),
`PartContactLoad`/`PartStructuralLimits` accumulate contact loads on the solver thread
(`PhysicsBubble.cs` → `PartFailure.Detect(vehicleUpdateState)`), and `PartFailureEvent.Apply` on the
game thread runs `PartFailure.IsolateAndDestroy` → `Vehicle.Split(connection, endpoint, 0.0, …)` per
severed connection, `ShedDebris` → `Vehicle.SpawnSubPartDebris`, and — when ≥ max(2, 50 %) of the
parts fail at once — `Universe.DestroyVehicle(…, CrewDisposition.Kill)`. `DestroyVehicleFromEvent`
sheds up to 12 pieces. The consequences on this surface:

- **Fragments (`<id>_1`, `<id>_2`, … via `GenerateSplitId`) and single-part debris are ordinary
  `Vehicle`s in `CurrentSystem.All`**, so `vessels/by-id` lists them: `controllable` = `0`, one part,
  ordinary flight/orbit reads. `Vehicle.Class` is now `IsDebris ? "Debris" : "Vehicle"`, but
  `VesselSnapshot` carries neither `Class` nor `IsDebris` — **debris is not distinguishable on this
  surface yet.** Coverage-gap follow-up: a `vessels/<id>/debris` flag (and `parts/<n>/crash_tolerance_pa`,
  `inert_mass_kg`).
- **Orbit-event reads on debris are frozen at spawn** — `PhysicsBubble.RunVehiclePostWorkInner` skips
  `RecalculateFlightPlan` and the burn-plan refresh when `vehicleState.IsDebris`, so
  `orbit/time_to_ap`, `time_to_pe`, `next_patch` and `encounters/` stop updating on a debris row while
  its position/velocity keep integrating. Real vessels unaffected (`VesselReader.cs:322,:865`
  re-stamped).
- **The controlled vessel can change with no command written**: `PartFailureEvent.Apply` hands
  `Program.ControlledVehicle` to the largest still-`IsControllable` fragment, and the
  `Program.ControlledVehicle` setter now `ClearHeldPlayerInput()`s the previous vehicle on change.
  `Universe.DestroyVehicle` also `HandOffCameras` (to `FindHeaviestBubbleNeighbour() ?? Parent`) and
  `DestroyVehicleFromEvent` follows the shed debris or a `WreckageMarker`, so `camera/target` can move
  too. `debug/control_vessel` is unchanged.
- A weld / sticker / camera **anchor part can migrate — `InstanceId` intact — into a fragment vehicle**
  between two frames; every driver resolves anchors within the vehicle it was created on, so the
  anchor resolves null and the feature goes **dormant through its existing null path** (never garbage).
  Re-resolving `InstanceId`s across all vehicles is a follow-up option (`WeldManager.cs:187`
  re-stamped).

### ⚠️ Parachutes: a new module family on the part tree (5402, coverage gap)

`Parachute` (+ `ParachuteDeploy`/`ParachuteCut` `ISequenced` modules, an `ActiveChute` physics term,
`ChuteState {Stowed, Armed, LineStretch, Reefed, Disreefing, Full, Cut, Ripped, SeveredLines}` and a
Bepu cloth canopy solved on a new `JobSystems.ClothSolvers` lane). Parts: `RadialParachuteSmallA/B`
(drogue), `RadialParachuteMediumA/B` (main), `ParachuteBayB` (replaces `ParachuteBayA`; two `Parachute`
sub-parts, `DeployAltitudeM = 800`, `CutsDrogues`), `ParachuteAssets.xml`. The module appears in
`Parts.Modules.Get<Parachute>()` and **gatOS's per-module walk does not report it** — exposable reads
are `State`, `Fill`, `DisreefProgress`, `IsDrogue`, `IsEnabled`, `Fitted`, `HasCanopy`,
`CanopyAttached`, `CanopyRadius`, `Tuning.{DiameterM,RiserLengthM,…}`; controls `Arm()`, `Disarm()`,
`Deploy()`, `CutAway()` (the Frame-safe route is the `IActivateInputBuffer`, like staging). Two reads
gatOS *does* make are touched: `acceleration_body` now **includes chute drag** (folded into
`PhysicsStates.Disturbances`; correct, no unit change), and `animations/<n>` on a chute bay mirrors the
chute state machine because `Parachute.Arm/Disarm/Deploy` drive the bay's `KeyframeAnimationModule`
(`SetBayDoorsOpen`). `ctl/stage` now arms/cuts them — [write findings](ksa-write-surface.md#5402-findings).

### ⚠️ Camera read-backs: what changed underneath `camera/**` (5402)

`CameraReader.Sample` now reads `IGameViewport.{Mode,MapController}` (read-only interface properties;
public fields on `Viewport` before) — values unchanged. Three inherited behaviours: (1)
**`Camera.ClampCamera` is camera-local and terrain-aware** — `Program.FindNearbyCelestial(this)` +
`TryGetSurfaceClampPositionEcl(0.5)` lifts the camera to 0.5 m above `MeanRadius + terrain height`
along its own direction, instead of gating on the *frame* viewport's `CurrentAltitudeKm`; the postfix
read-back publishes the clamped transform, so a placement solved below terrain reads back lifted. (2)
`MapController` gained `CanChangeControl => ViewportRegistry.IsMainCamera(Camera)` — no change for the
main viewport gatOS drives; `Scope` and its clamp are unchanged (`camera/map/scope` reads back exactly as
before). (3) `camera/target` can move on a vehicle destruction (`HandOffCameras`, above).

### ⚠️ Render-side reads: viewport classification, first-person head hiding (5402)

Two reads in the render area changed shape without changing what a guest sees, and one new draw-time
behaviour is worth knowing before a live pass:

- **`ThugLifeManager.CurrentPassBit()` classifies off `IViewport.Type` now.** `Program.GetCrewPortraitViewport(int)`
  and `_crewPortraitViewportStart` are gone with the viewport rework; the pass bit is a
  `ReferenceEquals` against `Program.MainViewport` (`:485`, `IGameViewport`) followed by
  `RenderedViewport.Type` (`:491`, `IViewport`) → `CharacterPortrait` ⇒ `Crew`, `Secondary` ⇒ `Other`.
  `PartThumbnail` maps to **no bit**, which is stricter than before and correct — a thumbnail can never
  receive a quad. The 5348 caveat still stands: with crew portraits off or unoccupied those viewports are
  `Visible = false`, `RenderViewport` skips them, and the `Cameras & Crew` bit simply goes unused.
- **EVA paint slots can be invisible in first-person IVA.** `CharacterCore.HeadMeshIndices`
  (`CharacterAvatar.cs:46`; `CharacterAssets.xml` lists meshes 0,1,2,3,5,6,7,8),
  `AnimatedRenderable.{MaskedMeshIndices,HideMaskedMeshes,PrePassIgnoreMeshIndices,SkinningPoseIsViewportInvariant}`
  and `KittenRenderable.HideHead` (`:98`) are new; `IVASeat.IsCameraInThisSeat(viewport)`
  (`viewport.Mode == IVA && gameViewport.IvaController.Seat == this`) sets it. While true, `Draw()` skips
  the masked meshes and the whole `CatFurRenderable` draw, so the occupant's own `body` slots 0–3/5–8 and
  `fur` paint are **not drawn in that viewport**. `EvaPaintBridge` is unaffected structurally: masking is
  draw-time, `MaterialIndices` array lengths and ordinal slot names are unchanged, and hidden meshes stay
  material-bound (clones still allocated, `Uses` accounting unchanged). A `/sim/paint` read still reports
  the rule; only the first-person view omits it.
- **Every `FxReflect` field name still resolves** (12/12, same types); two moved in the
  `VolumetricExhaustRenderer` reshuffle (`_currentAtmosphericPressure :278 → :290`,
  `_debugThrottle :306 → :310`) and the `Program`/`CloudRenderer`/`PlanetRenderer` handles moved lines
  only. Because revs 5349–5399 have no changelog and that renderer grew ~1248 lines, confirm the two
  names against `/sim/debug/fx` health on first launch (`docs/VALIDATION.md`, 5402 card).
- **Coverage gaps** (nothing broken, candidates only): the `ViewportOptionFlags` presets themselves are
  not readable through `/sim`; the new **clutter physics** path
  (`BubbleClutterStatics.GatherNearestInstances`, `ClutterEcotypePhysicalData.ComputeBoundingRadius`,
  per-cell collider draw) adds a ground-contact surface `/sim` does not expose; and `Part.IsAttachedInternal`
  / `AttachedInternal.{InstanceOf,PositionParentAsmb,Asmb2ParentAsmb}` is a **third transform holder** the
  IVA driver does not know about — it classifies interior geometry by `PartModelModule.Template.Internal`
  and only adopts `SubParts`.

### Verified clean

Byte-identical KSA files behind bound reads: `EngineController`, `EngineControllerState`, `Battery`,
`Decoupler`, `Tank`, `Mole`, `ThrusterController(State)`, `KeyframeAnimationModule`, `NavBallData`,
`UniverseTime`, `VersionInfo`, `Encounter`, `SolidMotor`, `SolidGrainSegment`, `RocketCoreState`,
`ManualControlInputs`, `ThrusterMapFlags`, `PhysicsEnvironment`, `BurnTarget`, `VehicleReferenceFrame(Ex)`,
`FloatReference`, `ColorRgbReference`, `SolarTracker`, `SolarPanelState`, `GeneratorState`,
`PowerConsumerState`, `SolarTrackerState`, `VehicleUpdateTask`, `DockingPortTemplate`, `VehicleEngine`,
`ModuleStateful`, `Sequence`, `ISequenced`, `IActivate`, `CharacterAvatar`, `KittenRenderable`,
`IParentBody`, `LookupCollection`; `Astronomicals.xml` has **no diff** (no celestial-parameter drift for
`BodyReader`). Members re-checked by name with no semantic change: every `VesselReader` anchor,
`BodyReader` ×3, `AnimationLinks`, `TelemetrySampler` ×5 (`Universe.{GetElapsedSeconds :2108,
SimulationSpeed :102, GetLastSimStep :2096, GetSimulationSpeeds :2235, IsAutoWarpActive :98,
AutoWarpTime :100}`), `KsaCatalog` ×2, the IVA forcing-term reads (`AccelerationBody :572`, chute drag
folded in), the thug_life anchor math (`Vehicle.GetMatrixAsmb2Ego :1256`, `Part.PositionEgo/Asmb2Ego`),
`Brutal.Numerics` (byte-identical DLL surface). Refactors that are equivalent: `Vehicle.GetSurfaceSpeed()`
is now `GetSurfaceVelocityCci().Length()`; `LightModule.IsActive` became
`!Parent.FullPart.IsLightSwitchedOff()` (same truth table for a same-tree switch);
`SolarTrackingExtension.IsAnimating => false` (never read). `Celestial.cs`'s 64-line diff is terrain
render-data maths (`RenderData.SurfaceRadius` → `MeanRadius` in the modifier LUT) plus viewport-typed
signatures — nothing `BodyReader` or `CameraFrames` binds.

## ⚠️ 5348 read-surface findings (playbook pass 2026-08-23) {#5348-findings}

Full playbook pass 2026-08-23, `2026.8.19.5261` → `2026.8.22.5348` (revs 5262–5348, 85 commits).
PREVIOUS was a fully audited baseline and CURRENT's `fromRevision` is 5261, so the trees chain with no
gap. **Zero compile breaks — the first pass in the project's history with none** (5261 had ten, 5168
had four). Clean `-t:Rebuild` of the whole solution against the 5348 DLLs: **0 warnings, 0 errors**;
`dotnet test gatos.slnx` **1646 passed / 12 skipped / 0 failed**. **No bound read changed name,
signature, type, unit or frame** — every finding below is value drift that the reads report faithfully.

A green build is the census only for the *non-reflective* bindings, so this pass added a **binary-level
surface diff** on top of the decomp diff: all 481 external TypeRefs were extracted from the compiled
`gatOS.GameMod.dll`, and every referenced type's full member surface (public + non-public,
declared-only) was dumped from **both** DLL sets via `MetadataLoadContext` and compared. **63 of 470
referenced types changed shape**, and every one of gatOS's ~15 reflection accessors resolved with a
compatible shape in the real shipping 5348 assemblies — including the `KittenEva._renderable` →
`KittenRenderable._characterAvatar` → `CharacterAvatar.Core` → `Scale` chain (`0.01f` == 1:1,
unchanged) and `Vehicle._manualControlInputs` (`EngineThrottle : float` and
`ThrusterCommandFlags : ThrusterMapFlags` both still public instance fields). This checks the shipping
binaries rather than the decomp, which can lag. The pass is **static plus that metadata diff** — render
correctness and in-flight behaviour still need a live pass (`docs/VALIDATION.md`).

### ⚠️ Staging changed what the post-stage reads report (rev 5329)

`SequenceList.ActivateNextSequence(Vehicle)` keeps its signature, but its body changed
`Parts[n].ActivateInStage(vehicle)` → `Parts[n].ActivateSubtreeInStage(vehicle, sequence.Number)`.
`Part.ActivateInStage` activated **every `IActivate`** on the part; `ActivateSubtreeInStage` walks
`GetSubtreeSequencedModules()` and activates only modules whose own `Sequence` equals the sequence
number being fired. `ISequenced` implementors are exactly `EngineController` and `Decoupler` —
**`ThrusterController` is `IActivate` but NOT `ISequenced`**, so staging no longer flips
`rcs/<n>/active` as a side effect. Three sub-changes, all visible in the reads after a `ctl/stage`:
(a) it is now a **subtree** walk, so engines/decouplers on sub-parts activate where they were
previously skipped; (b) `ISequenced` only, not every `IActivate`; (c) per-module sequence match, so a
part carrying an engine in sequence 2 and a decoupler in sequence 3 needs **two** presses before both
read active. Affects `engines/<n>/active`, `rcs/<n>/active` and `decouplers/<n>/*`. Write side:
[`ksa-write-surface.md#5348-findings`](ksa-write-surface.md#5348-findings).

### ⚠️ `navball/deltav` and `navball/twr` were wrong before; they are right now (rev 5318)

`Vehicle.UpdateNavballData` and `NavBallData` are unchanged — the sequence→parts grouping beneath them
is not. `SequencePerformanceList` went from
`if (part.Sequenceable && _sequenceIdxByNumber.TryGetValue(part.Sequence, …))` to iterating
`part.GetSubtreeSequencedModules()` and matching each module's own `Sequence`; decoupler jettison-mass
attribution moved the same way. This is the upstream "assigning a part to sequence 0 silently zeroed the
vehicle's delta-v and TWR" fix, so on affected vehicles these two leaves now read **different and
correct** values. No unit, frame or member change; the 5117 rewiring onto
`Parts.PerformanceSequences.FindActiveSequenceDeltaV()` ([5117 findings](#5117-findings)) still holds.

### ⚠️ EVA paint slot ordinals swapped under the MMU (rev 5268 era, asset change)

`Content/Core/CharacterAssets.xml`'s `CharacterMMUAttachment` changed source mesh
`Characters/KittenMMU/KSA_Cat_MMU.gltf` → `Characters/KittenMMU/SK_KSA_MMU.glb` — now **skinned**, so
`CharacterAvatar.Attachments.Mmu.MmuMesh` is retyped `StaticMeshRenderable` → `AnimatedRenderable` and
an `AnimationScrubSampler ArmScrub` was added — a `<Transform>` block appeared, and the two
`<Materials>` blocks were **reordered**; the file now carries the comment "Material order follows
SK_KSA_MMU.glb: body first, labels second". `KSA_MMU_Color` is index **0** (was 1) and `KSA_MMU_Texts`
is index **1** (was 0). gatOS names EVA paint slots by array ordinal (`mmu`, `mmu.0`, `mmu.1`, …), so a
saved rule targeting `mmu` now repaints the MMU **body** instead of the label decals. The `.glb` is not
in the repo, so the `MaterialIndices` array **length** could also differ — enumerate the slots live
before trusting a stored rule.

### ⚠️ CPU terrain height sampling changed at cube-face seams (5348)

`Celestial.SampleCubeFacePointR`'s out-of-range branch replaced a 4-tap bilinear seam fetch with
`UnfoldCubeFaceUv` plus a single clamped nearest tap; the private `FetchTexelSeamlessR` was **deleted**;
`GetFaceAndTexelFromDirection`'s texel index changed `(int)Math.Floor(u*w - 0.5)` → `(int)(u*w)`; and
`DirectionToCubemap` went `private` → `internal static`. `GetTerrainHeightFromDirCcf`/`Ccc` themselves
are unchanged. The read impact is **sub-metre and only near cube-face boundaries**: geodetic altitude in
`CameraFrames.GeoToEcl` / `TryEclToGeo` (`camera/pose/geo`), and `StickerAnchors`/`StickerPicker`
terrain anchoring. **The `/sim/camera/pose/geo` round-trip property still holds** — both directions
route through the same sampler, so they remain exact inverses of each other.

### ⚠️ Value drift with no API change

- **Power (rev 5326).** `PowerManager` was rebuilt on the new `ElectricalCircuits` partition:
  `ResourceAvailable` is now "any battery on my circuit has charge", **`FlowRule` is no longer consulted
  for power**, and battery draw starts from a rotating cursor and stops when satisfied instead of
  splitting evenly. gatOS reads the vehicle-wide SoA `vehicle.Parts.Batteries`, **not**
  `PowerManager.Batteries`, so `battery/{charge,capacity,fraction}` and `power/consumed` are unaffected
  in shape and unit. One edge case: a craft whose consumers sat on a `*SameStage` flow rule now stays
  powered where it previously browned out, so `power/consumed` can read non-zero where it read `0`.
- **Inert mass.** `Vehicle.InertMass` aggregation moved from a global push to the self-nominated
  `IInertMass` interface. Implementers are exactly `Tank` and `SolidGrainSegment` — a 1:1 match with the
  three old push sites, and `Rescale` is the identity at `ScaleFactors(1.0)` — so `mass/dry`,
  `mass/total` and `mass/propellant` are unchanged. The trap to keep: a future stock part that gains
  `IInertMass` will move `mass/dry` with **no gatOS-side signal at all**.
- **`srb/<n>/area_ratio`.** `SolidMotorNozzle` gained a player-editable `AreaRatioMultiplier`
  (default `1f`) and `ThroatSizingArea => Config.ExitArea / AreaRatioMultiplier`. At the default the
  arithmetic is identical, so stock craft are unchanged; at a non-default multiplier gatOS publishes the
  **sizing** ratio while the real per-nozzle expansion ratio is `AreaRatio × multiplier`.
- **`decouplers/<n>` ordinals.** `Decoupler` became a multi-instance component module (the
  `template.Decoupler` field is deleted; instances are now constructed from `template.Components`).
  Stock content still carries exactly one decoupler per part, so today's ordinals are stable — but a
  modded or future part with two would produce two ordinals where the addressing model assumed one.
- **`engines/<n>/active` at spawn.** `ThrusterController`'s constructor dropped its
  `part.ActivateInStage(null)` broadcast, so RCS thrusters no longer self-activate at part construction.
  `ThrusterController.IsActive` still defaults `true`, so `rcs/<n>/active` is unchanged — but an engine
  on a part that co-hosts RCS now reads an **honest** default instead of a spurious `true`.
- **Content removals and tuning.** `TreeType13/14/15` are **commented out** in `Astronomicals.xml`
  (rev 5263 — no colliders yet), taking their sole materials `Tree12Cards`/`Tree13Cards`/`Tree14Cards`
  (15 texture slots) out of the clutter catalog walk; a persisted binding to one surfaces as `Failed`
  ("stock texture 'x' is gone"), which is the graceful path. Grass ecotype `ObjectSeparation` went
  1.3 → 1.45 m and `GenerationRange` 170 → 80 m (revs 5306/5345), so an overridden grass texture now
  disappears at 80 m. And vehicle destruction now actually kills crew (`Universe` calls
  `vehicle.KillCrew()` before `DestroyVehicle`, rev 5316, with a `TimedAlert` naming the dead), so crew
  snapshots will start seeing KIA crew where they previously saw stale live crew.

### ⚠️ The clutter texture catalog was publishing nothing — fixed, and this page was wrong about why

**Not a 5348 regression: identical on 5261 and 5348.** Rows were keyed on
`TextureReference.GetRealId()`, which returns `Id` only when `SerializedId.IsReferenceable` is set, and
that is set **only** when the asset XML carries an `Id=` *attribute* (`SerializedId.OnDataLoad`:
`IsReferenceable = !string.IsNullOrEmpty(Id)`). **Not one clutter texture element** in
`Content/Core/GroundClutter/{Grass,GenericRock,EarthTrees}Assets.xml` has one — they are all
`Path=`-only (verified: `grep -cE '<(Diffuse|Normal|Opacity|Thickness|AoRoughMetal|Alpha)[^>]*Id='` →
**0**). Every slot therefore fell through to `walk.Anonymous++`, the catalog published **empty**, and
every `bind` returned `ENOENT`: the feature was completely inert. The `EarthGrassClutterDiffuse`-style
ids previously documented live in an inline `<Material Id="EarthGrassClutterMaterial">` block in
`Content/Core/Astronomicals.xml` that is **never deserialized** —
`ClutterEcotypeReference.MaterialReferences` is `[XmlIgnore]` and is repopulated from
`ClutterObjects → Lods → MaterialReferences` by `PopulateMaterialReferences()`. **That content layout
does not exist in either build.**

The catalog is now keyed on `TextureReference.LocalPath` — the XML `Path` attribute, e.g.
`Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`: install-independent, unique per asset and
space-free, which the space-separated `clutter` listing and `bind` line require. **Not `Id`**, because
`FileReference.OnDataLoad` assigns `Id = ModPath` when not referenceable and `ModPath` is an
**absolute machine path** — it would differ per install and leak the user's filesystem. A single
`KeyOf(TextureReference)` helper is used by both the discovery walk and `Match`/`ResolveStock` so they
cannot diverge. `texture_id` values under `/sim/paint/textures/clutter` are therefore now
content-relative paths; bindings are session-only (nothing is persisted), so there is nothing to
migrate. The walk also picks up the new `PbrMaterialReference.AlphaMap`
(`[XmlElement("Alpha")] public TextureReference? AlphaMap;`, inherited by
`GroundClutterMaterialReference`) as slot `alpha` — no stock clutter material authors one yet, so it is
normally absent from the listing; this is forward-coverage so a future material is not silently
un-overridable.

### Verified clean

- **Frames and numerics are byte-identical.** `Brutal.Numerics` `doubleQuat.cs` / `double3.cs`,
  `KSA/QuaternionEx.cs` and `KSA/Double3Ex.cs` are unchanged, as are every Cci/Cce/Ccf accessor on
  `Celestial`, `CreateOrb2Cci`, `StellarBody.cs`, `CelestialBody.cs`, `CelestialObject.cs`,
  `IParentBody.cs` and `BubbleOrigin.cs`. Handedness, quaternion component order, `Concatenate` argument
  order and `CreateFromAxisAngle` conventions all hold. Rev 5280's new `CelestialFrameMath` helper is a
  **pure refactor** — inlining each of its four helpers reproduces the old expression textually, operand
  order included, so results are bit-identical. `docs/KSA_CELESTIAL_COORDINATE_FRAMES.md` needs no
  correction.
- **Orbits are byte-identical.** `Orbit.CreateFromStateCci(IParentBody, UniverseTime, double3, double3,
  byte4)` is the same line in both trees; every public orbital element is unchanged; `OrbitData.cs`,
  `StateVectors.cs`, `IPatchedConics.cs` and `OrbitalTransfers.cs` have zero diff. `orbit/*` and the
  5261 `UniverseTime.IsSaturated()` handling on `time_to_ap`/`time_to_pe`/`next_patch` are untouched.
- **`encounters` is unaffected by rev 5266.** The target-gauge change — now
  `FlightPlan.TryFindNextClosestApproach(target, now)`, earliest-in-time on the current trajectory,
  replacing a global-minimum scan over `FindFinalFlightPlan().Patches` — writes only
  `PatchedConic._closestApproaches`. gatOS reads `_encounters` (SOI encounters), a **different list**;
  `Vehicle.Patch.Encounters` and `Encounter.{Body,GameTime,ClosestDistance}` are unchanged. (One doc
  consequence outside this page: `Vehicle.FindFinalFlightPlan()` has been **deleted**, so
  `docs/VALIDATION.md`'s claim that the listed approaches "reflect the final trajectory including
  planned burns" — never true of `Patch.Encounters` — must be corrected.)
- **Power/battery module surface.** `Battery.cs`, `BatteryState.cs`, `PowerConsumerState.cs`,
  `GeneratorState.cs`, `SolarPanelState.cs`, `Mole.cs`, `MoleState.cs`, `RocketCoreState.cs`,
  `Joules.cs`, `Watts.cs` and `VehicleProperties.cs` are **byte-identical** — no Joules↔Watts swap
  anywhere — and the `ModuleStateful` SoA pattern (`GetState`, `States`, `Modules`,
  `GetModuleAndAllMutableStatesForInitialization`, `TryGetFrom`, `StatesIdx`) is intact.
- **One trap for any future reflective module walk:** `ModuleBase.Parent` became a **property** (it was
  a field) and `IActivate` now extends `IPartParent`. Every gatOS read of `.Parent` goes through typed
  code, never reflection, so this is source-compatible today.

---

## ⚠️ 5261 read-surface findings (playbook pass 2026-08-11) {#5261-findings}

Full playbook pass 2026-08-11, `2026.8.5.5168` → `2026.8.19.5261` (revs 5169–5258, 90 commits).
PREVIOUS was an audited baseline and CURRENT's `fromRevision` is 5168, so the trees chain with no gap.
**One real semantic break, caught and closed; every other binding verified unchanged.** Build + full
suite green against the 5261 DLLs (0 warnings, 1317 passed).

### ⚠️ The break: `SimTime` → `UniverseTime` moved the "no such event" sentinel (rev 5211)

KSA replaced `SimTime` (a `double` of seconds) with **`UniverseTime`** (`Int128` nanoseconds) to kill
precision loss ahead of multiplayer physics. The type migration is compile-visible, **but the sentinel
change hiding inside it is not**:

| | PREVIOUS (5168) | CURRENT (5261) |
|---|---|---|
| "no such event" value | `SimTime.PositiveInfinity` | `UniverseTime.EndOfTime` (`Int128.MaxValue` ns) |
| `.Seconds()` of that value | `+∞` | **`≈1.7014e29` — finite** |
| `Sanitize.Finite()` result | `0` ✅ | **passes straight through** ❌ |

`Vehicle.NextApoapsisTime`/`NextPeriapsisTime` are now `Orbit.GetNext*Time(...) ?? UniverseTime.EndOfTime`
(`Vehicle.cs:2512-2513`) and `_nextPatchEventTime` defaults to `UniverseTime.EndOfTime` (`:226`, was
`SimTime.PositiveInfinity`). Because gatOS scrubbed the old `+∞` to `0` purely via `Sanitize.Finite`,
the saturated sentinel would have surfaced as a **real-looking timestamp ~5.4×10²¹ years in the
future** on `orbit/time_to_ap`, `orbit/time_to_pe` and `orbit/next_patch` — on every hyperbolic /
escape trajectory and every vessel with no upcoming patch transition. Nothing would have thrown, no
accessor would have latched degraded, and the build was green for the *other* nine call sites.

**Closed** in `VesselReader.FullOrbit`: the apsis/patch times route through `TimeUntil`/`UtOrZero`,
which test `UniverseTime.IsSaturated()` first and return `0`. The established `0` = "no such event"
contract is **preserved**, so `/sim` values and units are unchanged — SPEC §3.4.2 gained only an
explicit statement of the no-event case for `time_to_ap`/`time_to_pe` (`next_patch` already said it).

> **Playbook note — a green build is *even weaker* than it looks.** Roslyn stops before binding method
> bodies while any **declaration-phase** error is outstanding, so the first pass reported exactly one
> error (`TimeUntil`'s `SimTime` *parameter*) and hid the other nine `SimTime`/`GetElapsedSimTime`
> body-level breaks completely. **Fix and rebuild until green rather than trusting the first error
> list as the work list.**

### Verified unchanged

- **Time reads keep their unit and magnitude.** `Universe.GetElapsedSimTime()` is gone; `time/ut` now
  reads `Universe.GetElapsedSeconds()` (still `double` seconds). `UniverseTime.Seconds()` reconstructs
  whole+fraction from nanoseconds, so `/sim` *gains* precision rather than changing meaning.
- **Frames and numerics are byte-identical.** The entire `Brutal.Numerics` decomp tree is unchanged
  across the two builds, so `double3`/`doubleQuat` and every CCI/CCE/CCF/ECL convention are intact.
  Rev 5242's row-major quaternion↔mat3 fix lands only in `AtmosphereRenderer`/`CloudRenderer` method
  bodies — neither declares a member gatOS binds.
- **Battery capacity ×10 (rev 5227) is a value change, not a unit change.** `MaximumCapacity J="1000"`
  → `J="10000"` (and 3000→30000, 100→1000, 500→5000) in `CoreElectricalAGameData.xml` — same `J=`
  attribute, same `Joules` struct, same `.Value()` read. `battery/{charge,capacity,fraction}` stay
  truthful; only the magnitudes a guest sees change. **Worth telling flight programs**: any script with
  a hard-coded absolute charge threshold is now off by 10× (fraction-based logic is unaffected).
- **`Vehicle.IsControllable` unchanged** (`=> _overrideIsControllable || Parts.Controls.NumModules > 0`,
  `Vehicle.cs:580`), so `vessels/<id>/controllable` is still truthful — see
  [`ksa-write-surface.md#5261-findings`](ksa-write-surface.md#5261-findings) item 2 for the widened
  UI-only lockout it now reports against.
- **Reflection accessors structurally intact** (static): `Vehicle._manualControlInputs` (`:236`) with
  `ManualControlInputs` additive-only (rev 5203 added `GrabHeld`; `EngineThrottle` /
  `ThrusterCommandFlags` untouched); `VolumetricExhaustRenderer.{_currentAtmosphericPressure,
  _debugThrottle}` and `PlanetRenderer.{_renderUboMap,_meshUboMap}` unmoved despite rev 5243's exhaust
  pipeline rework; `CloudRenderer`'s declared member surface identical; the KittenEva avatar-scale
  chain (`KittenEva : Vehicle` → `_renderable` → `_characterAvatar` → `Core` → `Scale = 0.01f`)
  unchanged. Live `/sim/status/accessors` check still advised.
- **NavBall, environment, encounters, parts** — no binding moved.

---

## ⚠️ 5168 read-surface findings (playbook pass 2026-08-05) {#5168-findings}

Full playbook pass 2026-08-05, `2026.8.3.5117` → `2026.8.5.5168` (revs 5118–5168, 49 commits). PREVIOUS
was an audited baseline and CURRENT's `fromRevision` is 5117, so the trees chain with no gap.
**Two reads added, one silent read/actual divergence closed, three inherited value drifts; every
existing binding verified.** Build + suite green against the 5168 DLLs (0 warnings, 772 passed).

**Reads added (both close a "the write silently did nothing" blind spot):**

| New node | Source | Why |
|---|---|---|
| `vessels/by-id/<id>/ctl/rcs_mode` | `FlightComputer.RCSMode` (`FlightComputerRCSMode.{Enabled,Disabled}`), `FlightComputerActuator.ReadRcsMode` → `VesselSnapshot.RcsMode` | rev 5143 made this a hard master cut-off for **manual** RCS: with it `Disabled`, `ctl/translate`/`ctl/rotate` do nothing. gatOS's own read-back (`Vehicle.GetThrusterFlags()`) reports the *commanded* flags, so nothing in `/sim` revealed the condition. Also writable — see [write findings](ksa-write-surface.md#5168-findings). |
| `vessels/by-id/<id>/decouplers/<n>/enabled` | `Decoupler.IsEnabled` → `DecouplerSnapshot.Enabled` | rev 5132 lets players disable a part's decoupler module; a disabled one cannot fire. |

**Inherited value drift (API identical — no code change, but `/sim` numbers move):**
- **Encounter population widened again (rev 5141).** The flight plan previously never predicted an SOI
  encounter for **near-coplanar transfers** (e.g. a Hohmann transfer to Luna); two candidate-filter
  gaps were fixed (pe/ap reachability had no margin for the sibling's own SOI, and a patch with zero
  surviving candidates left its expiry at infinity, disabling the periodic re-verification scan), plus
  a live SOI-proximity check now runs on every coasting vehicle's motion update. `encounters/<n>/` rows
  therefore appear where they previously did not. This compounds the 5117 drift (revs 5106/5110).
- **RCS thrust values (rev 5119)** — reduced overall, and small thrusters are now noticeably weaker
  than large ones. Affects `rcs/<n>` derived performance and any Δv budgeting a flight program does.
- **SRB grain geometry (rev 5124)** — the solid-propellant grain built into the **size-D/E** nozzle
  segments was resized, moving `srb/<n>/` masses, burn times and mass flow for those motors.
- **Part mass / moments of inertia (rev 5166)** — several errors in how mass properties were computed
  were fixed, masses were added to all fairing parts, a **tangent-ogive** mass type was added, and
  masses of revolution may now be a sector rather than a full circle. `mass`, `center_of_mass` and
  every inertia-derived navball/attitude figure shift accordingly.

**Verified clean:** every anchored read compiles and resolves; the `KittenEva._renderable` →
`KittenRenderable._characterAvatar` → `CharacterAvatar.Core` → `Scale` reflection chain used by
`ScaleActuator` survives the heavy kitten-locomotion churn of revs 5128–5144 intact;
`ManualControlInputs` gained only a `Sprint` field (the three fields gatOS reflects into are
unchanged, and the box-mutate-write-back pattern preserves it); `NavBallData`, `Tank`,
`SolidGrainSegment`, `DockingPort` and the celestial/orbit surface are structurally unchanged.

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
# Paint reads

Paint reads live vehicles/parts/subparts (`Vehicle.Id`, `Part.InstanceId`, `Part.Template.Id`), shader
references/GLSL source, KittenEva avatar/renderable graphs, protected material-index arrays, and the
material AssetMap for handle-to-source identity. No GPU material read-back is attempted: the buffer
is device-local and lacks TransferSrc. All reflection/anchor failures degrade paint instead of
changing stock rendering. Exact fields and baseline are in `plans/PAINT_ASBUILT.md`.

# Clutter texture reads

Custom clutter textures read the live discovery chain only: `PlanetRenderer.GroundClutterRenderer` →
`CelestialsWithGroundClutter` → `Celestial.BodyTemplate.GroundClutterReference.Ecotypes` →
`ClutterEcotypeReference.{Name, MaterialReferences}` → `GroundClutterMaterialReference.
{DiffuseReference, NormalReference, PBRMap, OpacityMap, ThicknessMap, AlphaMap}` → `TextureReference.
{LocalPath, Width, Height, Texture.MipMapCount, BindlessHandle}`, deduplicated by key with a usage
count so a shared asset is visible before binding. **The key is `TextureReference.LocalPath` — the
asset XML's `Path` attribute (e.g. `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`) — not
`GetRealId()`, and there are no `EarthGrassClutterDiffuse`-style ids anywhere in the shipped content**:
keying on `GetRealId()` published an **empty** catalog on both 5261 and 5348, because no clutter
texture element carries an `Id=` attribute. Corrected 2026-08-23 — full evidence in the
[5348 findings](#5348-findings). `AlphaMap` (`[XmlElement("Alpha")]`, slot `alpha`) is new at 5348 and
no stock material authors one yet, so it is normally absent from the listing.
Both the material reference and each `TextureReference` may be an unresolved reference, so `Get()` is called exactly as `ToGpuMaterial`
does; the walk is ~1 s cadence and skipped entirely until something is uploaded or bound. Unlike EVA
paint's GPU material buffer, the one datum that matters for exact teardown **is** readable here:
`TextureReference.ImageView` and `.BindlessHandle` are public properties, so the pristine stock slot
is captured directly before the first swap rather than reconstructed — a re-bind keeps the original
capture, so restore always returns to stock and never to a previous override. Nothing is read back
from the GPU. Exact members and baseline are in
[`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`](../plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md).

# Sticker reads

Stickers read game state on the game thread in three places, all of them per-frame or per-placement
and none of them going through `SnapshotStore`.

**Aiming (`StickerPicker`, `spray` only, one call per placement).** The aim ray is
`Program.GetMainCamera()` + `Camera.ScreenToEgoRay(FramebufferSize * 0.5f)` for `aim=camera`, or
`Cursor.InputRay` for `aim=cursor`. Both are in **ego** space (origin at the camera, ecliptic axes)
and `Ray`'s constructor normalizes `Direction`; `ScreenToEgoRay` takes framebuffer **pixels, not
NDC**. `Cursor.InputRay` is refreshed once per frame, so it is the previous frame's ray when the
cursor has not moved — which is exactly what the player last saw. Camera aim is the default because
it works headless and `/sim/camera` can point it. The vehicle sweep then walks
`Universe.CurrentSystem.All.UnsafeAsList()`, broad-phasing on `Vehicle.BoundingSphereRadiusBody`
scaled by `ScaleTotal` and then calling `Part.RayCastEgo(in double4x4, Ray, out …)` over
`Vehicle.Parts.Parts` with `Vehicle.GetMatrixAsmb2Ego(Camera)` — the **identical** sweep KSA's own
flight-mode hover picking runs (`Vehicle.cs:2745-2773`), a watertight raycast over the view mesh's
de-indexed `double3[]` triangle soup, so the hit lands on the **art surface**. Bepu raycasts are
deliberately not used: KSA never does, and its colliders are coarse primitives. The hit is anchored
to a **sub-part**'s `InstanceId`. Only if nothing is hit does the terrain march run
(`Camera.NearbyCelestial`, 64 coarse steps + 24 bisections over `Celestial.
GetTerrainHeightFromDirCcf`, the `TerrainImpactFinder.cs:64` shape) with `Celestial.{GetCce2Ccf,
GetCcf2Cce,GetCci2Cce,MeanRadius,GetLatitudeFromCcf,GetLongitudeFromCcf}` and
`Vehicle.ComputeEnu2Cce` to convert the hit to geodetic degrees and a heading; `accurate:false` for
the march (4 bilinear taps, the physics hot path) and `accurate:true` for the final sample (bicubic
+ the CPU procedural-modifier chain). **Ground clutter cannot be aimed at** — it exists only on the
GPU — so a ray passes through a rock to the terrain behind it and the decal box then projects onto
the rock anyway.

**Anchor re-resolution (`StickerManager.ResolveAnchor`, every frame, every entry).**
`Universe.CurrentSystem.Get(string)` → `Vehicle` or `Celestial` — the same id lookup `/sim/camera`
and the game's own follow/control actions use, returning **null for a despawned target rather than
throwing**; then `Vehicle.Parts.Parts` and each `Part.SubParts`, matched on `Part.InstanceId`.
Sub-parts are searched because `Part.RayCastEgo` anchors to one. A null result makes the sticker
dormant (`live=0`) for that frame; it is never pruned, so a vessel that comes back or a staged part
that returns brings the decal back with it.

**Per-frame composition (`StickerAnchors`, every live entry).** Vessel:
`Vehicle.GetMatrixAsmb2Ego(Camera)` then `Part.MatrixAsmb2Ego(in double4x4)`, which **includes the
part's own scale and walks the whole sub-part parent chain** — that is what makes a sub-part
instance id a valid anchor. Body: `Celestial.{GetDirCcfFromLatLon,GetTerrainHeightFromDirCcf,
GetCcf2Cce,GetCci2Cce,MeanRadius}` + `Vehicle.ComputeEnu2Cce(double3, doubleQuat)` +
`Camera.GetPositionEgo(IPosition)`. `GetTerrainHeightFromDirCcf` returns **metres above
`MeanRadius`** and `0` for a body with no heightmap; `ComputeEnu2Cce` builds its quaternion from a
matrix whose **rows** are east/north/up (so under the row-vector convention `UnitX/UnitY/UnitZ`
transform to east/north/up) and returns null **on the spin axis**, where ENU is undefined. The ego
position is composed exactly like KSA's own terrain debug overlay (`Vehicle.cs:4511-4523`) — the
body's ego position plus the body-fixed offset rotated into ecliptic axes, **never an absolute
ecliptic point**. Everything is recomputed every frame and nothing derived is cached across frames,
because ego space is camera-relative and the planet turns; all of it is `double`, including the
inverse, with only the final 3×4 rows packed to `float` for the push constant (inverting the packed
float matrix would lose the surface point to cancellation at kilometre distances).

⚠️ **5348:** the CPU seam sampler beneath `GetTerrainHeightFromDirCcf` changed
(`Celestial.SampleCubeFacePointR`'s out-of-range branch, `FetchTexelSeamlessR` deleted), so terrain
anchoring shifts by **sub-metre amounts near cube-face boundaries** — see
[5348 findings](#5348-findings).

Nothing is read back from the GPU. The CPU terrain height the composition uses omits the GPU's
tessellation displacement, so the surface point can be off by decimetres near the camera — the
projection box's depth absorbs that entirely, which is exactly why this is a projected decal and not
a flat quad. Exact members and baseline are in
[`plans/STICKERS_PLAN.md`](../plans/STICKERS_PLAN.md).
