# Scope — KSA Read Surface (sensors)

> Every telemetry read gatOS performs against KSA. Each row: the `/sim` path it feeds, the gatOS code
> site, the KSA member it binds to, the decompiled-source file that defines that member, the unit/format,
> the churn risk, and the **4750 status** (✅ unaffected · ⚠️ silent semantic/unit drift · ❌ compile break).
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

Performed directly in `gatOS.GameMod/Game/TelemetrySampler.cs` (not in a reader, **not individually
`[KsaAnchor]`-attributed** — the matrix `time/*` rows are their record). All `Universe` statics.

| `/sim` path | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4750 |
|---|---|---|---|---|---|---|
| `time/ut` | `TelemetrySampler.cs:85` | `Universe.GetElapsedSimTime().Seconds()` | `KSA/Universe.cs` | seconds, double | Low | ✅ |
| `time/warp` | `:86` | `Universe.SimulationSpeed` | `KSA/Universe.cs` | factor | Low | ✅ |
| `time/sim_dt` | `:124` | `Universe.GetLastSimStep().DeltaTime` | `KSA/Universe.cs` | seconds | Medium | ✅ |
| `time/warp_speeds` | `:161` | `Universe.GetSimulationSpeeds()` | `KSA/Universe.cs` | factor list | Medium | ✅ |
| `time/auto_warp` | `:175,187` | `Universe.IsAutoWarpActive`, `Universe.AutoWarpTime` | `KSA/Universe.cs` | flag + UT | Medium | ✅ |
| (active vessel id) | `:87` | `Program.ControlledVehicle?.Id` | `KSA/Program.cs` | string | Low | ✅ |
| (vessel set) | `:93` | `Universe.CurrentSystem.All.UnsafeAsList()` | `KSA/Universe.cs`, `KSA/CelestialSystem.cs` | enumeration | Low | ✅ |

> None of the `Universe` time/warp members appear in the 4680→4750 changelog and all compiled clean.
> **Gap note:** these sampler-direct reads have no `[KsaAnchor]`, so a rename here would surface as a
> compile error in `TelemetrySampler.cs` rather than at an anchor — still caught by the build, just
> outside the anchor census. Consider anchoring them (tracked in the fix plan as a hygiene item).

---

## Vessel core reads — `VesselReader.SampleCore` (always sampled)

`gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs`. Sampled for every vessel regardless of the
`telemetry_vessel_detail` gate. Anchor: `VesselReader.cs:28`.

| `/sim` path (under `vessels/by-id/<id>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4750 |
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
| `battery/{charge,fraction}` | `:86,330` | `Vehicle.Parts.Batteries.GetState(b).Charge.Value()` ÷ `b.MaximumCapacity.Value()` | `KSA/Battery.cs` | fraction 0..1 | Low | ⚠️ type |
| `ctl/lights` (readback) | `:112` | `Vehicle.LightsOn` | `KSA/Vehicle.cs` | 0/1 | Low | ✅ |
| `ctl/engine` (readback) | `:124` | `Vehicle.IsSet(VehicleEngine.MainIgnite, false)` | `KSA/Vehicle.cs` | 0/1 | Medium | ✅ |
| `engines/<n>/{active,vac_thrust,isp}` | `:247` | `Vehicle.Parts.Modules.Get<EngineController>()`; `.IsActive`, `.VacuumData.{ThrustMax,MassFlowRateMax}` | `KSA/EngineController.cs` | bool / N / s | Medium | ✅ |
| `tanks/<r>/{amount,capacity,fraction}` | `:303` | `Tank.Moles`; `Parts.Moles.GetState(mole).Mass`; `mole.GetLiquidMass(ContainerVolume)`; `mole.FilledFraction` | `KSA/Tank.cs`, `KSA/Mole.cs` | kg / kg / 0..1 | Low | ✅ |
| `animations/<n>/{current,state,goal}` | `:584` | `KeyframeAnimationModule.{TimeGoal,Shared.Duration}`; `State.{TimeCurrent,DeploymentState}` via `ModuleStateful` | `KSA/KeyframeAnimationModule.cs` | 0..1 / enum | Medium | ✅ |

**⚠️ `situation`** — `Vehicle.Situation` became a `[Flags]` bitfield (KSA rev 4645, *already* in the
4680 baseline), so `.ToString()` can yield composite values (e.g. `"Landed, ..."`); rev 4704 lets
atmospheric floaters enter `Landed` (aerostats). gatOS passes the string through unchanged, so this is a
*value-shape* consideration for guest parsers, not a code break. Documented here so it isn't mistaken for
a new 4750 regression.

**⚠️ `battery`** — `Battery.MaximumCapacity` and `BatteryState.Charge` are now the strongly-typed
`Joules` struct (rev 4681); `.Value()` still returns the joule float, and the reported **fraction is
unit-independent**, so battery reads are numerically unaffected. See power section below.

---

## Vessel detail reads — `VesselReader.Enrich` (gated by `telemetry_vessel_detail`)

Same file; runs inside a whole-pass try/catch (`VesselReader.cs:40`) — if any extension API drifts, the
vessel keeps its core telemetry and the extension dirs vanish (logged once).

### Position / navball / environment

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|
| `position/ecl`, `velocity/cci`, `com` | `:145-147` | `Vehicle.GetPositionEcl()`, `GetVelocityCci()`, `CenterOfMassAsmb` | `KSA/Vehicle.cs` | Low | ✅ |
| `navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | `:214` | `Vehicle.NavBallData.{AttitudeAngles(int3 deg),ThrustWeightRatio,DeltaVInVacuum,Frame,Speed}` | `KSA/NavBallData.cs` | Medium | ✅ |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | `:226` | `Vehicle.PhysicsEnvironment.{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}`; `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`; `Vehicle.AccelerationBody`/`AngularAccelerationBody` | `KSA/PhysicsEnvironment.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | `:190` | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly.Degrees}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `encounters` (NDJSON) | `:561` | `Vehicle.Patch.Encounters`; `Encounter.{Body.Id,GameTime,ClosestDistance}` | `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | Medium | ✅ |

### Writable-setpoint read-backs (so `ctl/*` files report the real state)

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|
| `ctl/throttle` | `:160` | `Vehicle.GetManualThrottle()` | `KSA/Vehicle.cs` (`:824`) | Medium | ✅ |
| `ctl/rcs` | `:131-140` | any `ThrusterController.IsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/attitude_mode` | `:178` | `FlightComputer.AttitudeMode` / `AttitudeTrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅ |
| `ctl/attitude_frame` | `:178` | `FlightComputer.AttitudeFrame` | `KSA/FlightComputer.cs` | Medium | ✅ |

### Per-module reads

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4750 |
|---|---|---|---|---|---|
| `engines/<n>/{throttle,propellant,min_throttle}` | `:269` | `EngineControllerState.{CommandThrottle,IsPropellantAvailable}`; `EngineController.MinimumThrottle` | `KSA/EngineControllerState.cs` | Medium | ✅ |
| `rcs/<n>/{active,propellant,map}` | `:378` | `ThrusterController.IsActive`; `ThrusterControllerState.{ControlMap,IsPropellantAvailable}` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle}` | `:410` | `SolarPanelState.{Produced,IsOccluded,SunAoA,SunEfficiency}`; `SolarTrackerState.CurrentAngle` | `KSA/SolarPanel.cs`, `KSA/SolarTracker.cs`, `KSA/SolarPanelState.cs` | Medium | ⚠️ unit |
| `generators/<n>/{active,produced}` | `:467` | `GeneratorState.{Active,Produced}` | `KSA/Generator.cs`, `KSA/GeneratorState.cs` | Medium | ⚠️ unit |
| `lights/<n>/{on,brightness,color,inner_angle,outer_angle}` | `:491` | `LightModule.Template.{Intensity.Value,ColorRgb,OuterAngle.Value,InnerAngle.Value}`; `Parent.FullPart.LightSwitch.LightIsActive` | `KSA/LightModule.cs` | **High** | ✅ |
| `docking/<n>/{docked,docked_to,pushoff_force}` | `:528` | `DockingPort.{Docked,DockedToPart.Id,PushoffForce}` | `KSA/DockingPort.cs` | Medium | ❌ **break** |
| `decouplers/<n>/fired` | `:548` | `Decoupler.IsActive` | `KSA/Decoupler.cs` | Medium | ✅ |
| `power/produced` | `:351` | Σ `SolarPanelState.Produced.Value()` + `GeneratorState.Produced.Value()` | `KSA/SolarPanelState.cs`, `KSA/GeneratorState.cs` | Medium | ⚠️ unit |
| `power/consumed` | `:365` | Σ `Vehicle.Parts.PowerConsumers.GetState(c).Consumed.Value()` | `KSA/PowerConsumerState.cs` | Medium | ⚠️ unit |
| `battery/capacity` | `:333` | Σ `Battery.MaximumCapacity.Value()` | `KSA/Battery.cs` | Low | ⚠️ type |

---

## ❌/⚠️ 4750 read-surface findings (detail)

### ❌ Docking pushoff — `docking/<n>/pushoff_force` (COMPILE BREAK)
`VesselReader.cs:539` reads `port.PushoffForce`. In 4750 (rev 4683) the member is renamed
**`PushoffForce` → `PushoffImpulse`** and is now an **impulse (N·s), not a force (N)**; the latching
threshold member also changed (`LatchingImpulse` → `LatchingKineticEnergy`). Confirmed in
`KSA/DockingPort.cs` (`public required float PushoffImpulse;`, `Undock → Split(Connector, PushoffImpulse)`)
and the asset XML `Content/Core/CoreCouplingAGameData.xml` (`<PushoffImpulse Ns="7000"/>`,
`<LatchingKineticEnergy J="50"/>`). Fix + the read/write/SPEC/units changes are specced in
[`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md). See also
[`ksa-write-surface.md#docking`](ksa-write-surface.md#docking).

### ⚠️ Power production/consumption — `power/*`, `solar/<n>/produced`, `generators/<n>/produced` (SILENT UNIT CHANGE)
rev 4681 ("Fixed the conflation of power and energy") retyped `SolarPanelState.Produced/Stored`,
`GeneratorState.Produced`, `PowerConsumerState.Consumed` from `Joules` → **`Watts`**. gatOS calls
`.Value()` on each (both `Joules.Value()` and `Watts.Value()` return the backing `float`), so it
**compiles** — but the emitted numbers now mean **instantaneous power (W)** instead of energy
accumulated per sample (J). The `/sim` fields are already named/specced in watts (`PowerProducedW`,
SPEC says "W"), and the asset XML authors `<Produced W="200"/>`, so 4750 actually makes these values
*correct*. Required change is mostly documentation: the `[KsaAnchor]` `Notes` ("Joules per sample") and
the matrix/SPEC "this sample" phrasing are now stale. Consumers will see different magnitudes. Optional
enhancement: surface the new `SolarPanelState.Stored` (Watts) and `DistanceToSun`. Details in the plan.

### ⚠️ Battery capacity — `battery/capacity` (TYPE TIGHTENED, value unchanged)
`Battery.MaximumCapacity` / `BatteryState.Charge` are now the `Joules` struct (was a reference type);
`.Value()` still returns joules, so `battery/capacity` and the unit-independent `battery/fraction` are
numerically unchanged. Update the anchor `Notes` only.

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

| `/sim` path | gatOS site | KSA member | Decomp file | Unit | Risk | 4750 |
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

---

## Coordinate frames (reference)
Reads cross several KSA frames — CCI (inertial), CCE/CCF (body-fixed), ECL (ecliptic), body frame. The
frame math (`GetCci2Ccf`, `GetBody2Cci`, `GetCce2Cci`, `GetLlaFromCcf`) is summarized in
[`ksa-runtime-coupling.md#frames-and-numerics`](ksa-runtime-coupling.md#frames-and-numerics) and detailed
in [`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md). A frame-math
change is the classic *silent* drift (compiles, wrong numbers) — re-verify against a live flight per the
playbook step 5.
