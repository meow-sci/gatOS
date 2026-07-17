# Scope — KSA Read Surface (sensors)

> Every telemetry read gatOS performs against KSA. Each row: the `/sim` path it feeds, the gatOS code
> site, the KSA member it binds to, the decompiled-source file that defines that member, the unit/format,
> the churn risk, and the **4939 status** (✅ unaffected · ⚠️ silent semantic/unit drift · ❌ compile break).
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

| `/sim` path | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4939 |
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

| `/sim` path (under `vessels/by-id/<id>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4939 |
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

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|
| `position/ecl`, `velocity/cci`, `com` | `:154-156` | `Vehicle.GetPositionEcl()`, `GetVelocityCci()`, `CenterOfMassAsmb` | `KSA/Vehicle.cs` | Low | ✅ |
| `navball/{pitch,yaw,roll,twr,deltav,frame,speed}` | `:223` | `Vehicle.NavBallData.{AttitudeAngles(int3 deg),ThrustWeightRatio,DeltaVInVacuum,Frame,Speed}` | `KSA/NavBallData.cs` | Medium | ✅ |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}` | `:235` | `Vehicle.PhysicsEnvironment.{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}`; `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`; `Vehicle.AccelerationBody`/`AngularAccelerationBody` | `KSA/PhysicsEnvironment.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `orbit/{lan,argpe,true_anomaly,time_to_ap,time_to_pe,next_patch}` | `:199` | `Orbit.{LongitudeOfAscendingNode,ArgumentOfPeriapsis,StateVectors.TrueAnomaly.Degrees}`; `Vehicle.Next{Apoapsis,Periapsis,PatchEvent}Time` | `KSA/Orbit.cs`, `KSA/Vehicle.cs` | Low | ✅ |
| `encounters` (NDJSON) | `:573` | `Vehicle.Patch.Encounters`; `Encounter.{Body.Id,GameTime,ClosestDistance}` | `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | Medium | ✅ |

### Writable-setpoint read-backs (so `ctl/*` files report the real state)

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4939 |
|---|---|---|---|---|---|
| `ctl/throttle` | `:169` | `Vehicle.GetManualThrottle()` | `KSA/Vehicle.cs` (`:824`) | Medium | ✅ ᵈ |
| `ctl/rcs` | `:170` | any `ThrusterController.IsActive` | `KSA/ThrusterController.cs` | Medium | ✅ |
| `ctl/attitude_mode` | `:171` | `FlightComputer.AttitudeMode` / `AttitudeTrackTarget` | `KSA/FlightComputer.cs` | Medium | ✅ |
| `ctl/attitude_frame` | `:172` | `FlightComputer.AttitudeFrame` | `KSA/FlightComputer.cs` | Medium | ✅ |

### Per-module reads

| `/sim` path | gatOS site | KSA member | Decomp file | Risk | 4939 |
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

`gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs`. Surfaces a vehicle's top-level parts at
`vessels/by-id/<id>/parts/<n>/`, **each with its subparts nested at `parts/<n>/subparts/<m>/`**
(2026-07-16: a subpart is a full `Part` with its own `InstanceId`) — the anchor picker for the
**welds** write surface ([`ksa-write-surface.md#welds`](ksa-write-surface.md#welds)); either level's
`instance_id` is a valid weld anchor. Cached per vehicle in a `ConditionalWeakTable<Vehicle,…>`
(collected with the vehicle, no leak, game-thread only); rebuilt only on a `Vehicle.Parts.Count`
change (the cheap "vehicle was edited" signal — KSA exposes no part-tree version/dirty flag; subpart
counts are template-fixed, so the top-level count stays the right signal) or every 10 s of sim time.
Hot path = one `Parts.Count` read per vehicle per tick. Anchor: `PartsReader.cs:30`.

| `/sim` path (under `…/parts/<n>/`) | gatOS site | KSA member | Decomp file | Unit/format | Risk | 4939 |
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

## thug_life anchor math — `ThugLifeQuadRenderer` (per-frame, render thread) {#thug-life}

`gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs` (`TryComputeModelEgo`) + `ThugLifeManager.cs`.
These are **render-frame transform reads** performed each frame inside the `gatos.thug_life` render postfix
(on the **main thread**) to place the quad on its anchor part — *not* sampler reads, so they do **not** go
through `SimSnapshot`. They are the read half of gatOS's **highest-churn KSA coupling** (render-pipeline
internals; see [`ksa-runtime-coupling.md#thug-life-patch`](ksa-runtime-coupling.md#thug-life-patch) and the
write side [`ksa-write-surface.md#thug-life`](ksa-write-surface.md#thug-life)). A rename **does** fail the
build at the `[KsaAnchor]` site (these are non-reflective), so they are caught at compile time — but
frame-math is the classic *silent* drift, so re-verify the quad's pose in a live flight after any update.

| read | gatOS site | KSA / Brutal member | Decomp file | Risk | 4939 |
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
  sequences are double-buffered for the UI — gatOS reads neither (`navball/deltav` comes from
  `Vehicle.NavBallData.DeltaVInVacuum`, unchanged).
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

| `/sim` path | gatOS site | KSA member | Decomp file | Unit | Risk | 4939 |
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
