# Telemetry, Resources & Flight State

Reading vehicle flight data, refilling fuel/electricity, and detecting flight events. All types in namespace `KSA`. Most reads can NaN/Inf — guard with `double.IsFinite`.

## Getting vehicles

```csharp
// ksa-abstractions.lib providers wrap these raw globals:
Vehicle? controlled = Program.ControlledVehicle;                 // null in editor/menu
List<Vehicle> all = Universe.CurrentSystem?.All.UnsafeAsList()
    .OfType<Vehicle>().ToList() ?? new();
```

`Universe.CurrentSystem` is null outside flight — always null-guard. `Vehicle.Id` (string) is the stable identity key used everywhere.

## Reading telemetry

These are direct `Vehicle` members. Note which are **methods** vs **properties**:

```csharp
// --- Altitude (methods) ---
double baroAlt  = vehicle.GetBarometricAltitude();   // sea-level relative
double radarAlt = vehicle.GetRadarAltitude();        // terrain relative

// --- Speed (mixed) ---
double orbSpeed   = vehicle.OrbitalSpeed;            // property; = GetVelocityCci().Length()
double surfSpeed  = vehicle.GetSurfaceSpeed();       // method
double inertSpeed = vehicle.GetInertialSpeed();      // method

// --- Mass (float properties; non-obvious names) ---
float total = vehicle.TotalMass;       // full wet mass (kg)
float dry   = vehicle.InertMass;       // dry mass
float prop  = vehicle.PropellantMass;  // = TotalPropellantMass

// --- Acceleration / g-force (body frame) ---
double3 acc = vehicle.AccelerationBody;              // m/s²; X=longitudinal(thrust), Y=lateral, Z=normal
double g = acc.Length() / 9.80665;                   // StandardGravity

// --- TWR (precomputed, don't hand-roll) ---
double twr = vehicle.NavBallData.ThrustWeightRatio;  // ref readonly property
float vacThrustN = vehicle.FlightComputer.VehicleConfig.TotalEngineVacuumThrust;

// --- Position (ecliptic, for distance math) ---
double3 posEcl = vehicle.GetPositionEcl();           // also overload taking a SimTime
```

### Orbital parameters — `vehicle.Orbit`

```csharp
vehicle.Orbit.Apoapsis      // RADIUS from body CENTER (not altitude!)
vehicle.Orbit.Periapsis     // RADIUS from body center
vehicle.Orbit.Eccentricity  // >= 1.0 ⇒ hyperbolic / escape trajectory
vehicle.Orbit.Inclination
vehicle.Orbit.Period
vehicle.Orbit.SemiMajorAxis
vehicle.Orbit.Parent        // the IParentBody
```

**Gotcha:** Apoapsis/Periapsis are radii from the body center. `altitude = Apoapsis - parent.MeanRadius`. **Every** orbital field can be NaN/Inf (e.g. apoapsis on an escape trajectory) — guard each read with `double.IsFinite`.

### Parent body / SoI / atmosphere

`vehicle.Parent` is `IParentBody` (`=> Orbit.Parent`):

```csharp
IParentBody? parent = vehicle.Parent;
string bodyId      = parent?.Id;
double meanRadius  = parent?.MeanRadius;
double mass        = parent?.Mass;             // surface gravity: G*Mass/r², G = 6.6743e-11

// Atmosphere (null ⇒ vacuum body):
AtmosphereReference? atmo = parent?.GetAtmosphereReference();
double height = atmo.Physical.Height.InMeters();                          // note .InMeters() unit unwrap
double p = atmo.Physical.GetAtmosphericPressureAtAltitude(baroAlt);
double d = atmo.Physical.GetAtmosphericDensityAtAltitude(baroAlt);
// "in atmosphere" is derived, not a flag: hasAtmo && baroAlt >= 0 && baroAlt < height
```

KSA wraps physical quantities in types — call `.InMeters()` / `.Seconds()` rather than assuming raw doubles. SoI changes are detected by comparing `parent.Id` between samples.

### Situation enum

`vehicle.Situation` is a `Situation` enum with exactly 6 values encoding contact + on-rails-ness:

```
Freefall, Maneuvering, Rolling, Landed, Sailing, Floating
```

Use the `SituationEx` extension helpers rather than enumerating:

```csharp
sit.HasAnyContact()      // Rolling/Landed/Sailing/Floating
sit.HasTerrainContact()  // Rolling/Landed
sit.HasOceanContact()    // Sailing/Floating
sit.IsOnRails()          // Freefall/Landed/Floating
```

## Refilling resources

### Fuel — `RefillConsumables()` (the console-refill equivalent)

```csharp
vehicle.RefillConsumables();
```

This bundles three required steps: `Parts.RefillConsumables()` (refill all tanks) + `RecomputeMassProperties(...)` + `FlightComputer.ReadUpdatedVehicleConfiguration(this)`. **If you mutate tanks/mass manually you must do all three** or derived values (TWR, total thrust, mass config) go stale.

### Electricity — the `ModuleStateful` SoA pattern

Batteries are NOT plain objects. KSA stores the module (behavior) and its mutable state (data) **separately** in a struct-of-arrays. You cannot set a property — you write state through a `ref`-returning accessor:

```csharp
var batteryStates = vehicle.Parts.Batteries;   // ModuleStateful<Battery,BatteryState,...>.StateList
if (batteryStates.NumModules == 0) return;
var modules = batteryStates.Modules;
for (int i = 0; i < modules.Length; i++)
{
    var battery = modules[i];
    var mutableRef = batteryStates.GetModuleAndAllMutableStatesForInitialization(battery);
    mutableRef.Module.Refill(ref mutableRef.State);   // Battery.Refill sets State.Charge = MaximumCapacity (Joules)
}
```

`GetModuleAndAllMutableStatesForInitialization(module)` returns a ref exposing `.Module` and a by-ref `.State` — the general pattern for writing any module's mutable state (tanks/`Moles`, animations, etc.).

**Electricity must be refilled in a `Universe.ExecuteNextVehicleSolvers` prefix**, not the render loop, or the sim step won't see it (see lifecycle.md). Fuel refill can run in `Update`.

## Sampling loop pattern

Used identically across geeforce (40 Hz), average-twr (100 Hz), steely-eyed (2 Hz). Use `while`, not `if`, to drain multiple intervals when a frame is long:

```csharp
_accumulator += dt;
while (_accumulator >= SampleIntervalSec)
{
    _accumulator -= SampleIntervalSec;
    // sample once per fixed interval
}
```

Timestamp samples with `Universe.GetElapsedSimTime().Seconds()` (sim time, correct under time-warp), not frame dt.

## Flight-event / transition detection (snapshot diff)

Stateless detector comparing prev vs current snapshot, with a per-event-type debounce (~2s). Rules worth reusing:

- **SoI change:** `prev.ParentBodyId != curr.ParentBodyId`
- **Liftoff:** was landed, now `!HasSurfaceContact` and not Floating/Sailing
- **Landing:** was airborne, now `Landed` or `Rolling`
- **Splashdown:** was airborne, now `Floating`/`Sailing`
- **Atmosphere entry/exit:** flip of the derived `IsInAtmosphere` bool
- **Stable orbit:** `Eccentricity < 1` AND periapsis-altitude crossed above atmosphere height
- **Escape:** eccentricity crossed `<1 → >=1` (guard with `double.IsFinite`)

## Gotchas

- **NaN/Inf** in orbital data is normal — guard every `Orbit.*` read.
- **Wrap every per-vehicle sample in try/catch** — a vehicle mid-teardown can throw and kill the whole loop.
- **Prune stale vehicles** — diff the current `Id` set against your tracked-state dict each tick; vehicles get destroyed/unloaded.
- **Game-thread only** for all mutations; use the `GameThread` scheduler for off-thread callers (see lifecycle.md).
