# KSA Integration Matrix

> The co-located, at-a-glance record of **every `/sim` path that touches KSA game state** — the
> document you grep first when a new decompiled-source drop lands (KSA_GAME_INTEGRATION_PLAN §3.3).
> Each row mirrors a `[KsaAnchor]` annotation in `gatOS.GameMod/Game/Ksa/**`; the attribute is the
> source of truth for the exact member, this table is the human view. When a decomp drop breaks the
> build, the failing `[KsaAnchor]` sites are the work list — fix them, then update the matching rows
> here (anchor, `Verified`, `GameVersion`).

**Status:** seeded for the **G1 (command pipeline + first controls)** and **G2 (integration-layer
formalization)** surface plus the existing M9 read surface. Rows for G3+ read expansion and the
G4 full control surface are intentionally absent until built.

**Verified:** 2026-06-12, against `thirdparty/ksa` (`VersionInfo.Current` at build time).

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

## The churn playbook (when a decomp drop lands)

1. Update `thirdparty/ksa`, rebuild with the KSA assemblies present. **Build errors in
   `Game/Ksa/**` are the alarm system.**
2. For each break: re-locate the API, fix the accessor, update its `[KsaAnchor]`
   (`Verified`, `GameVersion`, the member path) and the matching row above.
3. Runtime drift without a compile break: the per-accessor try/catch in `KsaCatalog`/`KsaHealth`
   latches the accessor degraded → it returns `EOPNOTSUPP`, logs once, and surfaces in
   `/sim/status/accessors`. The guest *sees* a failed sensor instead of the mod crashing.
4. Re-run the control-surface checklist in `docs/VALIDATION.md`.
