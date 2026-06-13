# KSA Game Integration Plan — telemetry, control, and virtual hardware

> Status: **G1–G6 BUILT + G7 codec core BUILT** (command pipeline, controls, integration layer, read
> + write catalogs, magic HTTP, TypeScript SDK, serial/bus framing; 2026-06-12). Parts 1–8 are
> implemented (Part 6 T3/T4's live virtio-serial wiring + guest exposure ride the next guest image
> v3; the codecs + command port are done). As-built reality: `CLAUDE.md` + `docs/KSA_INTEGRATION_MATRIX.md`.
> Remaining: the guest v3 image build, the in-game pass, and a user-requested **MQTT transport** (MQTTnet).
> This plan expands and supersedes the one-paragraph M12 P1 sketch ("writable control files")
> in `OS_PLAN.md`. It is the central, co-located reference for **every KSA data point we read
> and every KSA mutation we perform** — the document you update first when a new decompiled
> source drop lands. Companion docs: `OS_PLAN.md` (VM/terminal execution plan), `OS_ANALYSIS.md`
> (architecture research), `CLAUDE.md` (as-built reality).

Decisions already made with the project owner (2026-06-12):

| # | Decision |
|---|---|
| **G-D1** | **Control authority: all vessels.** The guest can command any vessel in the save — the VM is the agency's mission-control network, not one craft's computer. A config restriction knob can come later (ties into OS_PLAN D10 multi-computer fiction). |
| **G-D2** | **Cheat-tier mutations live in a separate `/sim/debug/` namespace, enabled by default.** Teleport, instant refills, warp-set are clearly fenced off from the "realistic hardware" surface but available out of the box (`gatos.toml` flag can disable). |
| **G-D3** | **No custom guest binaries, ever** (inherited from the project's founding principle). Everything below is reachable from a stock Alpine userland: shell redirects, `cat`, `curl`, any language's stdlib file/socket/HTTP APIs. Guest-side additions are limited to shell scripts in `rootfs-overlay/` (same class as the existing `sim-mount`). |

---

## Part 1 — Is the device-file idea "real Linux"? (realism assessment)

**Yes — almost exactly.** The off-the-cuff `/sim` design independently reinvented the dominant
Linux hardware idiom: **sysfs attribute files** — one value per file, plain ASCII, newline
terminated, read for sensors, write for actuation. This is not a toy approximation; it is how
mainline Linux exposes most slow-path hardware to userspace:

| Real Linux | What it is | gatOS analogue |
|---|---|---|
| `/sys/class/hwmon/hwmon0/temp1_input` | sensor value, plain text, changes every read | `/sim/vessels/active/altitude/barometric` |
| `/sys/bus/iio/devices/iio:device0/in_accel_x_raw` | IIO accelerometer channel | `/sim/.../environment/accel` |
| `/sys/class/leds/led0/brightness` | **writable** setpoint (0..max) | `/sim/.../lights/0/brightness` |
| `/sys/class/gpio/gpioN/value` | read state / write to drive the pin | `/sim/.../engines/0/active` |
| `/sys/class/pwm/pwmchip0/pwm0/duty_cycle` | continuous actuator drive | `/sim/.../solar/0/goal` (deploy fraction) |
| `echo 1 > /sys/.../device/reset`, sysfs `bind`/`unbind`, sysrq-trigger | **one-shot trigger by write** | `/sim/.../ctl/ignite`, `/sim/.../decouplers/0/fire` |
| `/dev/ttyS0` carrying NMEA from a GPS receiver | streaming char device | virtio-serial telemetry port (Part 6, T3) |
| netlink uevents / `/dev/input/event0` | kernel→user event push | `/sim/events` (blocking-read file) |
| errno from `write(2)` | actuation failure reporting | `echo 1 > ignite` → `EINVAL`/`ENOENT`/`EIO`, nonzero shell exit |

The pattern the user guessed at ("standard embedded microcontroller stuff") is accurate: on an
embedded board, igniting an engine *would* be `gpioset`/`echo 1 > value` on a GPIO wired to an
ignition relay, and reading a sensor *would* be `cat`ing an hwmon/IIO file. Real spacecraft add
buses on top — **MIL-STD-1553, SpaceWire, CAN, RS-422 serial, CCSDS packet telemetry** — which we
offer as the *realism tier* (Part 6, T4) rather than the default, because raw bus framing is
hostile to casual shell use.

**Realism vs usability verdict** (the design stance for everything below):

1. **Files are the primary surface** (keep `/sim`). Highest realism-per-usability: every language
   on earth can `open/read/write`, every shell can `cat`/`echo`. Writes-as-actuation with errno
   feedback is *more* faithful to Linux than any RPC would be.
2. **Don't cosplay raw hardware in the default surface.** No fake ADC counts, no register maps,
   no `_raw`+`_scale` pairs. Values are engineering units in ASCII (already frozen in `Formats`).
   Realism here means *idiomatic Linux*, not *hostile bit-twiddling*.
3. **HTTP is the structured/bulk/atomic tier** — semantically like a modern flight-software
   ground API (and like KSP's kRPC). It solves the two real weaknesses of file trees: atomic
   multi-value snapshots and parameterized queries (e.g. "state vectors at t").
4. **Serial/bus protocols are the flavor tier** — opt-in experiments (NMEA/SCPI-style lines,
   CCSDS space packets, MIL-STD-1553 framing) on extra virtio-serial ports, for players who want
   to write a *real* packet parser.

---

## Part 2 — The hardware model: four interaction archetypes

Every data point in the catalog (Parts 4–5) is classified as one of these. Each archetype has a
fixed contract so SDKs can treat them generically.

| Archetype | Linux idiom | Read | Write | gatOS contract |
|---|---|---|---|---|
| **SENSOR** | hwmon/IIO attribute | current value, changes every open | — | `StaticTextFile` (snapshot per open, existing). One value + LF. |
| **STATE** | gpio `value`, led `brightness`, RW sysfs attr | current setting | new setting (`0`/`1`, or `0..1` fraction) | New `ControlFile`: read = live value; write = enqueue command, block until applied, errno on failure. Idempotent. |
| **TRIGGER** | sysfs one-shot (`reset`, `bind`, sysrq) | arm/fired status (or `0`) | exact token (usually `1`) fires the action once | `ControlFile` with one-shot semantics. Non-idempotent; `EINVAL` for wrong token, `EBUSY`/`EIO` when the action can't fire. |
| **STREAM/EVENT** | tty, uevent, input device | growing log (`tail -f`) or blocking read | (T3+: line commands on serial) | Existing `StreamFile` / `EventsFile` models (spike-validated). |

Write semantics (all transports): commands are **synchronous** — the `write(2)` (or HTTP request)
does not complete until the game thread has executed the mutation and reported success/failure.
A shell user gets real feedback: `echo 1 > ignite` either succeeds or prints
`sh: write error: Invalid argument` with a nonzero exit code. Timeout (default 2 s) → `ETIMEDOUT`.

### Errno vocabulary (frozen once shipped)

| errno | Meaning in gatOS |
|---|---|
| `EINVAL` | Unparseable or out-of-range value written |
| `ENOENT` | Vessel/part/module vanished between walk and write |
| `EACCES` | Control disabled by config (authority/debug gating) |
| `EBUSY` | Action not currently possible (e.g. decoupler already fired) |
| `EIO` | KSA API threw — integration layer fault (also flips the accessor's health latch) |
| `ETIMEDOUT` | Game thread didn't drain the command in time (paused? load screen?) |
| `EOPNOTSUPP` | Accessor disabled by health latch after a previous fault |

### 2.1 Time, timewarp, and feed semantics

The guest VM runs in **wall-clock time** (QEMU does not warp), while KSA's sim time advances by
variable-size ticks (`Universe.GetLastSimStep().DeltaTime`) scaled by `Universe.SimulationSpeed`.
Every interface must make this dual-clock reality explicit instead of pretending it away:

- **Two clocks, always labeled.** Sim values are timestamped with `ut` (sim seconds); nothing in
  the surface is keyed to wall time. The sampler keeps its wall-clock cadence (`SampleRateHz`
  accumulating render dt — existing `SampleClock`), so under 10 000× warp consecutive samples are
  ~0.1 s apart in wall time but thousands of sim-seconds apart. That is *correct* — the guest is
  ground software watching a fast-forwarding spacecraft — but consumers need the metadata to
  detect it.
- **Feed metadata (G1/G3):** every `stream` NDJSON line and `/v1` snapshot gains `warp` and
  `sim_dt` (sim seconds elapsed since the previous published sample) alongside the existing
  `seq`/`ut`. A gap in `seq` or a large `sim_dt` tells an SDK "you are seeing a decimated
  fast-forward, not 10 Hz continuous data".
- **Aliasing is inherent and documented:** diff-based events (`EventDiffer`) compare published
  snapshots; a state that flips and flips back inside one sample interval (easy at high warp) is
  invisible. Events carry the `ut` of the snapshot that *detected* them, which under warp can lag
  the physical moment by up to one sample interval × warp. Scripts needing exactness must manage
  warp (below) — same constraint a real mission has: you don't fly a landing at 10 000×.
- **Sim-time scheduling primitive — the alarm device (G3):** real Linux exposes RTC wake alarms
  (`/dev/rtc0`); we mirror it at `/sim/time/alarm` — write a target `ut`, then `read` blocks
  until sim time reaches it (re-armed per open; reuses the proven blocking-event file model).
  HTTP twin: `GET /v1/time/wait?until=<ut>` long-poll. This is the correct way to "sleep 5 sim
  seconds" or "wake me at periapsis − 60 s" regardless of warp — never `sleep(5)` in the guest.
- **Closed-loop control vs warp:** commands execute on the next game tick, so wall-clock control
  loops (read → compute → write) are only sound near 1× warp. The plan's answer, in order of
  realism: (1) use **onboard setpoints** that the sim integrates itself — FlightComputer attitude
  and burn targets, animation goals — which behave correctly at any warp (the autopilot flies the
  burn, the guest is mission control); (2) gate scripts on the alarm device + `time/warp` reads;
  (3) for full authority, `/sim/debug/time/warp` lets a script drop to 1× before a hand-flown
  maneuver. The SDK encodes this guidance (`waitUntilUt()`, `atWarp(1, fn)` helpers).
- **Pause:** the source survey found no public pause/`IsPaused` API; `sim_dt = 0` across samples
  is the observable signal (documented; revisit when KSA exposes it).

---

## Part 3 — Architecture: the KSA Integration Layer

The single most important requirement: **KSA churns constantly; gatOS must absorb that churn in
one place.** Today the read side already has this property by accident — `TelemetrySampler.cs` is
the only file touching KSA telemetry APIs. This plan formalizes it and extends it to writes.

### 3.1 The two one-way pipes

```
                 game thread (OnBeforeGui)                      9p / HTTP / serial threads
   ┌─────────────────────────────────────────┐              ┌────────────────────────────┐
   │  KsaReaders  ──build──►  SimSnapshot    │──volatile──► │  read paths (existing)     │
   │  (Game/Ksa/, game-coupled, compile-gated)│   swap       │  SnapshotStore.Current     │
   │                                         │              │                            │
   │  KsaActuators ◄──drain── CommandQueue   │◄──enqueue──  │  write paths (new)         │
   │  (execute, set TCS result)              │  + await TCS │  ControlFile / HTTP PUT    │
   └─────────────────────────────────────────┘              └────────────────────────────┘
        + solver-phase queue drained by Harmony Priority.First prefix
          on Universe.ExecuteNextVehicleSolvers (only for solver-visible writes)
```

- **Reads** (existing, unchanged): game thread samples → immutable `SimSnapshot` → volatile swap →
  server threads read the latest snapshot. Threading rules 1–2 of `CLAUDE.md` untouched.
- **Writes** (new): server threads enqueue an immutable `SimCommand` carrying a
  `TaskCompletionSource<CommandResult>`; the game thread drains the queue each frame (bounded,
  default 64/frame), executes via the actuator catalog, completes the TCS. The 9p/HTTP handler
  awaits it with timeout. **Game state is still only ever touched on the game thread** —
  threading rule 1 extends to mutation untouched.
- **Solver-phase writes**: a few mutations must be visible to the physics solvers *within* a sim
  step (battery/resource refills; future robotics) — per the unscience `eternal-flame`/`flexo`
  pattern, these drain in a Harmony `Priority.First` prefix on
  `Universe.ExecuteNextVehicleSolvers` instead of `OnBeforeGui`. `Lib.Harmony` is already a
  GameMod dependency. Commands declare their phase; the router picks the queue.

### 3.2 Code layout (the churn firewall)

Game-free (testable headless, no KSA DLLs):

```
gatOS.SimFs/
  Snapshots/        SimSnapshot + records (existing, extended per Part 4)
  Commands/         NEW  SimCommand records, CommandResult, CommandQueue,
                         ICommandExecutor (the seam the game half implements)
  SimFsTree.cs      tree builder (extended per Part 4/5)
  ControlFile.cs    NEW  writable VfsFile archetypes (STATE / TRIGGER)
gatOS.NineP/        Tlopen/Twrite/Tsetattr write path (Part 6, T1)
gatOS.Http/         NEW project (Part 6, T2) — GenHTTP server over SnapshotStore + CommandQueue
gatOS.Bus/          NEW project, later (Part 6, T3/T4) — serial/CCSDS/1553 framing
```

Game-coupled — **the only KSA-touching code in the repo**, all compile-gated on
`KSAFolder/KSA.dll` exactly like today's `Game/**`:

```
gatOS.GameMod/Game/Ksa/
  KsaAnchor.cs           [KsaAnchor] attribute (see 3.3)
  Readers/
    VesselReader.cs      (refactored out of TelemetrySampler.cs)
    BodyReader.cs        EnvironmentReader.cs  OrbitReader.cs  PartModuleReaders.cs ...
  Actuators/
    EngineActuator.cs    LightActuator.cs  AnimationActuator.cs  FlightComputerActuator.cs
    StagingActuator.cs   DebugActuator.cs  ...
  KsaCatalog.cs          registry binding command types → actuators, snapshot fields → readers;
                         owns per-accessor health latches
```

Rule (extends the binding dependency rule in `CLAUDE.md`): **a KSA type name may appear only
under `gatOS.GameMod/Game/Ksa/`**. Transports, trees, formats, SDKs never see one. When a new
decomp drop breaks the build, the diff is confined to this folder + this document.

### 3.3 Per-data-point documentation: `[KsaAnchor]` + the integration matrix

Every reader/actuator method gets a machine-checkable annotation:

```csharp
[KsaAnchor("KSA.Vehicle.GetBarometricAltitude()", SourceFile = "KSA/Vehicle.cs",
           Verified = "2026-06-12", GameVersion = "<VersionInfo.Current at verification>",
           Risk = ChurnRisk.Low,
           Notes = "Meters above sea level. NaN when no atmosphere reference — Sanitize.Finite.")]
public static double BarometricAltitude(Vehicle v) => v.GetBarometricAltitude();
```

A maintained table, `docs/KSA_INTEGRATION_MATRIX.md` (seeded from the catalogs in Parts 4–5 of
this plan at G2), lists every exposed path/endpoint with: archetype, format/units, KSA anchor,
threading phase, churn risk, verified date + game version, and fallback behavior. A small test in
`gatOS.GameMod`'s build (reflection over `[KsaAnchor]`, runs only when game DLLs are present)
asserts matrix ↔ attribute consistency so the doc can't silently rot.

### 3.4 The churn playbook (what to do when a new decomp drop lands)

1. Update `thirdparty/ksa`, rebuild. **Compile errors in `Game/Ksa/` are the alarm system** —
   this is why readers call KSA members directly (no reflection) wherever possible.
2. For each break: re-locate the API in the new sources, fix the accessor, update its
   `[KsaAnchor]` (`Verified`, `GameVersion`, new member path).
3. `grep` the matrix for rows whose anchors changed; update rows.
4. Runtime drift without compile breaks (the decomp may lag the shipping binary — see the `ksa`
   skill's debug.md): per-accessor try/catch flips a **health latch** → that accessor returns
   `EOPNOTSUPP`/omits its field, logs once, and surfaces in `/sim/status/accessors` (Part 4) —
   the guest *sees* the degraded sensor, like real hardware failure, instead of the mod crashing.
5. Re-run the in-game checklist (extend `docs/VALIDATION.md` with a control-surface section).

Stability ratings from the 2026-06 source survey, encoded as `ChurnRisk` defaults:
**Low** — `Vehicle` core state, `Orbit`/`OrbitData`/`StateVectors`, `PartTree` typed lists,
`ModuleStateful` struct-of-arrays access pattern, `Universe`/`SimTime`. **Medium** —
`FlightComputer` (modes/targets look settable but control-loop internals are tuning-grade),
`InputEvents`-mediated operations (staging, `Vehicle.SetEnum`), `NavBallData`. **High** —
combustion internals (`RocketCoreConditions`, `GasConditions`), plume/FX data, anything
compiler-generated, anything reached by reflection (light template internals, private setters).

---

## Part 4 — READ catalog (sensors and data)

Conventions (existing `Formats` rules are frozen and unchanged): one value + LF per scalar file;
G9 invariant doubles; vectors/quats space-separated; flags `0`/`1`; SI units (m, m/s, kg, s, N, J,
Pa, W); angles in **degrees** where humans read them (lat/lon/inclination/euler), quaternions raw;
NDJSON for streams/events. New aggregate `telemetry`/`snapshot` files are single JSON documents
(one read = one consistent snapshot — the atomicity answer for file consumers).

Columns: **A** = archetype (S sensor, St state, T trigger, Sm stream), **W** = also writable
(→ Part 5), **Risk** = churn risk. KSA anchors name the member the reader wraps; file paths into
`thirdparty/ksa` live in the matrix, not here.

### 4.1 `/sim/status/` — integration health (NEW; no KSA anchors — self-describing)

| Path | A | Contents |
|---|---|---|
| `status/game_version` | S | `VersionInfo.Current.VersionString` (Risk L) |
| `status/sampler` | S | `ok <hz>` or `disabled <reason>` |
| `status/accessors` | S | NDJSON: one line per degraded accessor (`{"name":…,"since_ut":…,"error":…}`); empty when healthy |
| `status/guest`/`status/transports` | S | which transports are bound (9p port, http port, serial ports) |

### 4.2 `/sim/time/` (existing + extensions)

| Path | A | W | KSA anchor | Risk |
|---|---|---|---|---|
| `time/ut` | S | | `Universe.GetElapsedSimTime().Seconds()` (existing) | L |
| `time/warp` | S | dbg | `Universe.SimulationSpeed` (existing read; write = private `SetSimulationSpeed` via reflection → debug only) | L read / H write |
| `time/warp_speeds` NEW | S | | `Universe.GetSimulationSpeeds()` | M |
| `time/auto_warp` NEW | S | | `Universe.IsAutoWarpActive`, `Universe.AutoWarpTime` | M |
| `time/sim_dt` NEW | S | | `Universe.GetLastSimStep().DeltaTime` — sim seconds advanced by the last tick; `0` ⇒ effectively paused (see 2.1) | M |
| `time/alarm` NEW | St | ✓ | no KSA anchor — write target `ut`, read blocks until `SnapshotStore` publishes `ut ≥ target` (blocking-event file model; the `/dev/rtc` analogue from 2.1) | — |

### 4.3 `/sim/system/` and `/sim/bodies/<id>/` (NEW — celestial catalog)

Bodies enumerated via `Universe.CurrentSystem.All.OfType<Celestial>()` (+ `StellarBody`); same
qid-interning and `[A-Za-z0-9._-]`/`~N` sanitization rules as vessels.

| Path | A | KSA anchor | Risk |
|---|---|---|---|
| `system/{name,home,sun}` | S | `CurrentSystem`, `.HomeBody`, `Universe.WorldSun` | L |
| `bodies/<id>/{id,class,parent,children}` | S | `Celestial.Id/.Class/.Parent`, `.Children` | L |
| `bodies/<id>/{mass,radius,mu,soi}` | S | `.Mass`, `.MeanRadius`, `.Mu`, `.SphereOfInfluence` | L |
| `bodies/<id>/rotation_rate` | S | `.GetAngularVelocity()` (rad/s) | L |
| `bodies/<id>/position/{cci,ecl}`, `velocity/{cci,ecl}` | S | `.GetPositionCci/Ecl()`, `.GetVelocityCci/Ecl()` | L |
| `bodies/<id>/orbit/{sma,ecc,inc,lan,argpe,period,apoapsis,periapsis,t_pe}` | S | `.Orbit` elements (`SemiMajorAxis`, `Eccentricity`, `Inclination`→deg, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis`, `Period`, `Periapsis`, `Apoapsis`, `TimeAtPeriapsis`) | L |
| `bodies/<id>/atmosphere/{present,height,scale_height,sea_level_pressure,sea_level_density}` | S | `.GetAtmosphereReference()?.Physical.*` | M |
| `bodies/<id>/ocean/{present,density}` | S | `.GetOceanReference()` (read API partially unexplored — verify at G3) | M |

Not exposed as static files (parameterized — HTTP only, see T2): terrain height at lat/lon
(`GetTerrainHeightFromDirCcf`), pressure/density at arbitrary altitude
(`Physical.GetAtmospheric*AtAltitude`), body position at arbitrary time.

### 4.4 `/sim/vessels/by-id/<id>/` — existing files (unchanged, anchors already verified M9)

`id name situation parent`, `position/{cci,lat,lon}`, `velocity/{orbital,surface,inertial}`,
`attitude/{quat,rates}`, `altitude/{barometric,radar}`, `mass/{total,dry,propellant}`,
`orbit/{apoapsis,periapsis,ecc,inc,sma,period}`, `battery/charge`,
`engines/<n>/{active,vac_thrust,isp}`, `tanks/<resource>/{amount,capacity}`, `stream`.
`/sim/vessels/active` alias semantics unchanged.

### 4.5 `/sim/vessels/by-id/<id>/` — read extensions (NEW)

| Path | A | KSA anchor | Risk |
|---|---|---|---|
| `telemetry` | S | whole `VesselSnapshot` as one JSON doc (atomic read) — no new anchors | — |
| `position/ecl`, `velocity/{cci,vector_surface}` | S | `GetPositionEcl()`, `GetVelocityCci()` etc. (vectors, not just speeds) | L |
| `attitude/{pitch,yaw,roll}` | S | `NavBallData.AttitudeAngles` (int3, deg) | M |
| `navball/{twr,deltav,frame,speed_mode}` | S | `NavBallData.ThrustWeightRatio/.DeltaVInVacuum/.Frame` | M |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius}` | S | `Vehicle.PhysicsEnvironment` fields; `NavBallData.DynamicPressure` | L |
| `environment/{accel,g_force,angular_accel}` | S | `AccelerationBody` (+ magnitude/g₀), `AngularAccelerationBody` — NaN-guard | L |
| `orbit/{true_anomaly,mean_anomaly,time_to_ap,time_to_pe}` | S | `Orbit.GetTrueAnomaly(...)`, `Vehicle.NextApoapsisTime/.NextPeriapsisTime` − ut | L |
| `orbit/next_patch` | S | `Vehicle.NextPatchEventTime` | M |
| `encounters` | S | NDJSON from `Vehicle.Patch` (`PatchedConic.Encounters` / `.ClosestApproaches`: body, ut, distance) | M |
| `aero/cda` | S | `Vehicle.AerodynamicCdABody` (per-axis drag area) | H |
| `com` | S | `Vehicle.CenterOfMassAsmb` | L |
| `controlled` | S | `Program.ControlledVehicle?.Id == v.Id` flag | L |

### 4.6 Per-module read surfaces (NEW; module collections keep ordinal indices `<n>`, ordered by
PartTree enumeration; each dir gains a `part` file naming the owning `Part.InstanceId` so scripts
can correlate; renumbering only happens if the vehicle is structurally edited — documented caveat)

| Path | A | W | KSA anchor | Risk |
|---|---|---|---|---|
| `engines/<n>/throttle` | S | G4 | `RocketCore.Throttle` (current 0..1) | M |
| `engines/<n>/thrust` | S | | `RocketNozzle.Performance.TotalThrust` (N, sum nozzles) | M |
| `engines/<n>/propellant` | S | | `EngineControllerState.IsPropellantAvailable` | M |
| `engines/<n>/{burn_time_remaining,mass_flow}` | S | | `RocketCore.ThrustTimeRemaining/.MassFlowRate` | H |
| `engines/<n>/gimbal/{y,z}` | S | | `GimbalState.AngleY/.AngleZ` (rad) | M |
| `rcs/<n>/{active,map,propellant}` | St/S | ✓ | `ThrusterController.IsActive`, `ThrusterControllerState.ControlMap` (flag names), `.IsPropellantAvailable` | M |
| `tanks/<resource>/fraction` | S | | `Mole.FilledFraction(ref state)` (existing amount/capacity kept) | L |
| `battery/{capacity,fraction}` | S | | `Battery.MaximumCapacity`, `.FilledFraction(state)` (existing `charge` kept) | L |
| `power/{produced,consumed}` | S | | sums over `SolarPanelState.Produced`, `GeneratorState.Produced`, `PowerConsumerState.Consumed` | M |
| `solar/<n>/{produced,occluded,sun_aoa,efficiency,state}` | S | | `SolarPanelState.Produced`, `.IsOccluded`, `.SunAoA`, `.SunEfficiency`; deploy state via linked `KeyframeAnimationModule.DeploymentState` | M |
| `solar/<n>/tracker/{angle,active}` | S | | `SolarTrackerState.CurrentAngle`, `.Active` | M |
| `generators/<n>/{active,produced}` | S | | `GeneratorState` (linked to engine by InstanceId) | M |
| `lights/<n>/{on,brightness,color}` | St | ✓ | `PowerConsumer.LightIsActive`; `LightModule.TemplateData.Intensity/.ColorRgb` | M (template internals H) |
| `animations/<n>/{state,duration,current,goal}` | S/St | goal | `KeyframeAnimationModule.DeploymentState/.Shared.Duration/.TimeCurrent/.TimeGoal` | M |
| `docking/<n>/{docked,docked_to}` | S | | `DockingPort.Docked` (`Connector.Connection != null`), `.DockedToPart` | M |
| `decouplers/<n>/fired` | S | T | `Decoupler.IsActive` | M |
| `parts/<instanceId>/{id,name,stage,template}` (G4) | S | | `Part.Id/.DisplayName/.Stage/.Template`; tree shape via subdirs | M |

### 4.7 `/sim/events` — event types (existing 6 + NEW, all from snapshot diffing in `EventDiffer`;
KSA has **no native event bus** per the source survey, so polling-diff is the mechanism)

Existing: `warp-changed`, `active-changed`, `vessel-appeared`, `vessel-removed`,
`situation-change`, `soi-changed`. New (G3): `engine-state` (active flip), `flameout`
(`IsPropellantAvailable` falling edge while active), `staged`, `docked`/`undocked`, `decoupled`,
`animation-complete` (DeploymentState settles), `battery-depleted`/`battery-charged` (fraction
crossing 0/1), `encounter` (next-patch body change). Format unchanged (NDJSON
`{"ut":…,"type":…,"vessel":…,"detail":…}`).

---

## Part 5 — WRITE catalog (controls)

All writes flow through the command pipeline (3.1): parse on the transport thread, execute on the
game thread (or solver phase), synchronous result. Per **G-D1** any vessel is addressable.

### 5.1 Vessel-level controls — `/sim/vessels/<id>/ctl/` (NEW)

| Path | Archetype | Write format | KSA anchor (actuator) | Risk | Phase |
|---|---|---|---|---|---|
| `ctl/ignite` | TRIGGER | `1` | `Vehicle.SetEnum(VehicleEngine.MainIgnite)` (proven in unscience unladen-swallow) | M | G1 |
| `ctl/shutdown` | TRIGGER | `1` | `Vehicle.SetEnum(VehicleEngine.MainShutdown)` | M | G1 |
| `ctl/throttle` | STATE | `0..1` | manual-throttle path — **verify**: likely `InputEvents`/FlightComputer-mediated; survey found `EngineControllerState.CommandThrottle` + a `GetManualThrottle()` | M | G4 |
| `ctl/stage` | TRIGGER | `1` | `InputEvents.StageChangeData{…}.Apply()` / `Part.ActivateInStage` — **verify** exact next-stage call | M | G4 |
| `ctl/lights` | STATE | `0/1` | `Vehicle.LightsOn` master + per-consumer `LightIsActive` | M | G1 |
| `ctl/attitude_mode` | STATE | enum token (`manual`, `stabilize`, `prograde`, …) | `FlightComputer.AttitudeMode` (`FlightComputerAttitudeMode`) | M | G4 |
| `ctl/attitude_frame` | STATE | enum token | `FlightComputer.AttitudeFrame` (`VehicleReferenceFrame`) | M | G4 |
| `ctl/attitude_target` | STATE | `x y z w` (quat, CCI) | `FlightComputer.AttitudeTarget = new AttitudeTarget{Target2Cci, RatesCci}` | M | G4 |
| `ctl/burn` | STATE | `ut dvx dvy dvz` (CCI) | `FlightComputer.Burn = BurnTarget{ImpulsiveInstant, DeltaVTargetCci}`; `burn_mode` ← `FlightComputerBurnMode` | M | G4 |
| `ctl/rcs` | STATE | `0/1` | `ThrusterController.SetIsActive` over all RCS — **verify** vehicle-level toggle | M | G4 |

### 5.2 Per-module controls

| Path | Archetype | Write format | KSA anchor | Risk | Phase |
|---|---|---|---|---|---|
| `engines/<n>/active` | STATE | `0/1` | `EngineController.SetIsActive(null, bool)` (+`MinimumThrottle` read-before, per skill notes) | L | G1 |
| `engines/<n>/min_throttle` | STATE | `0..1` | `EngineController.MinimumThrottle` | M | G4 |
| `engines/<n>/gimbal/command` | STATE | `y z` (−1..1) | `GimbalControllerState.CommandY/.CommandZ` — struct-of-arrays ref write; likely **solver phase** | H | G4 |
| `rcs/<n>/active` | STATE | `0/1` | `ThrusterController.SetIsActive` | M | G4 |
| `rcs/<n>/pulse` | TRIGGER | seconds | `ThrusterControllerState.CommandPulseTime` — **verify** it fires outside the flight-computer loop | H | G4 |
| `lights/<n>/on` | STATE | `0/1` | `PowerConsumer.LightIsActive` (proven: unscience zippo/red-alert) | L | G1 |
| `lights/<n>/brightness` | STATE | `0..1` | `LightModule.TemplateData.Intensity.Value` — **per-instance requires template clone** (red-alert `ConditionalWeakTable` pattern) | H | G3 |
| `lights/<n>/color` | STATE | `r g b` (0..1) | `TemplateData.ColorRgb.R/G/B` + mandatory `OnDataLoad(null)` (zippo pattern) | H | G3 |
| `animations/<n>/goal` | STATE | `0..1` fraction | `KeyframeAnimationModule.TimeGoal = f × Shared.Duration` (proven; PWM-duty-cycle semantics) | L | G1 |
| `solar/<n>/goal` | STATE | `0..1` | same module, filtered to parts with `SolarPanel` (deploy=1/retract=0; the "send power to a motor" archetype) | L | G1 |
| `decouplers/<n>/fire` | TRIGGER | `1` | `Decoupler.SetIsActive(vehicle, true)` (queues via InputEvents; irreversible → `EBUSY` if fired) | M | G4 |

### 5.3 `/sim/debug/` — cheat namespace (per **G-D2**: separate, enabled by default,
`gatos.toml` can disable; everything here may use reflection/private APIs, Risk H by definition)

| Path | Archetype | Write format | KSA anchor |
|---|---|---|---|
| `debug/vessels/<id>/teleport` | TRIGGER | `px py pz vx vy vz` (CCI) | `Orbit.CreateFromStateCci` + `Vehicle.Teleport(orbit, body2cce, rates)` + `UpdatePerFrameData()` (garrys-torch pattern, NaN-guarded) |
| `debug/vessels/<id>/refill_fuel` | TRIGGER | `1` | `Vehicle.RefillConsumables()` |
| `debug/vessels/<id>/refill_battery` | TRIGGER | `1` | `Battery.Refill(ref state)` — **solver phase** (eternal-flame pattern) |
| `debug/time/warp` | STATE | factor | `Universe.SetSimulationSpeed` (private; reflection) |
| `debug/switch_vessel` | TRIGGER | vessel id | `Program.ControlledVehicle` static (no official setter; reflection; verify side effects) |

### 5.4 Explicitly out of scope (and why)

- **Part-tree surgery** (Merge/Split/dynamic part spawning): proven possible (unscience/blinky),
  but it is construction, not hardware control; revisit as its own plan if wanted.
- **Robotics** (hinge/rotor via `Asmb2ParentAsmb` composition): unscience flexo shows it works but
  it's heavy solver-phase machinery with cache-invalidation gotchas; defer until KSA grows a
  first-class robotics module.
- **Save/load, scene switching**: no stable mod-facing API found.
- **Time-warp as a non-debug control**: setter is private; keeping it in `/sim/debug` is honest.

---

## Part 6 — Transports (the "try a bunch of options" matrix)

| | T1 9p control files | T2 magic HTTP | T3 serial text | T4 space buses |
|---|---|---|---|---|
| Realism flavor | embedded Linux sysfs | modern flight software / ground API | GPS/instrument UART (NMEA/SCPI) | actual spacecraft avionics (CCSDS/1553/SpaceWire) |
| Usability | shell-native | every language's HTTP client | `cat`/`screen`-able | parser required (SDK ships one) |
| Atomic multi-read | per-file no; `telemetry` JSON yes | yes | per-frame yes | per-frame yes |
| Parameterized queries | no | yes | no | subaddress-limited |
| Phase | **G1** | **G5** | G7 | G7 |

### T1 — 9p writable control files (primary; phase G1)

Protocol work in `gatOS.NineP` (today: `Session.cs` rejects `O_WRONLY/O_RDWR` at Tlopen and
answers Twrite with EACCES):

1. `Vfs`: add `IVfsWritableFileHandle : IVfsFileHandle` with
   `ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken)`; add
   `VfsFile.IsWritable` + `OpenWrite()`. Mode bits: writable files stat `0644`-equivalent instead
   of `0444` (kernel pre-checks permissions from getattr — without this, `echo` fails before any
   Twrite reaches us).
2. `Tlopen`: allow `O_WRONLY/O_RDWR` (+`O_TRUNC`) when the node is writable; keep EACCES otherwise.
3. `Twrite`: dispatch to the handle; success → `Rwrite(count)`, failure → `Rlerror(errno)` per the
   Part 2 vocabulary. Writes on control files are line-buffered per handle: execute on first LF
   or on clunk (covers `echo`, `printf '%s' 1`, and `>file` redirects from any runtime).
4. `Tsetattr`: accept size-only truncate on writable files (kernel sends it for `O_TRUNC`);
   `Tfsync`: trivially succeed. Everything else stays EOPNOTSUPP.
5. `gatOS.SimFs`: `ControlFile` (STATE) and `TriggerFile` (TRIGGER) — parse, build `SimCommand`,
   enqueue, await with timeout, map result→errno. Reads of a STATE file show the live value from
   the snapshot.
6. Tests: codec/golden frames for the new messages; conformance via the existing managed test
   client; `GATOS_IT` fixture proving from a real guest shell:
   `echo 1 > /sim/vessels/active/engines/0/active` flips the engine and `echo bogus > …` exits
   nonzero with EINVAL. (Mount note: v9fs with `cache=none` — already our mount options — sends
   writes straight through; no client caching surprises.)

### T2 — the magic HTTP server (phase G5; new game-free project `gatOS.Http`, GenHTTP library —
a `genhttp` skill exists in this workspace for the implementer)

Host-side server on `127.0.0.1:<port>` (server lives in the game process — **no guest daemon**,
per G-D3); the guest reaches it via slirp at `10.0.2.2`. Magic-address plumbing (guest image v3):

- Host binds **4242 first**, falls back to ephemeral on collision. The actual port is advertised
  on the kernel cmdline (`gatos.httpport=<port>`, same proven mechanism as `gatos.simport`).
- Guest rootfs-overlay: `/etc/hosts` gains `10.0.2.2 sim`; a boot script writes
  `/run/gatos/http-port` and exports `GATOS_HTTP=http://sim:<port>` via `/etc/profile.d/gatos.sh`.
- Net effect: `curl http://sim:4242/v1/...` works in the common case; `$GATOS_HTTP` is the
  always-correct form for scripts. (slirp `guestfwd` was considered for a fixed in-VM IP but its
  chardev forwarding is effectively single-stream — wrong tool for HTTP.)

API sketch (`/v1`, JSON; semantics mirror the file tree so the matrix documents both at once):

```
GET  /v1/snapshot                          whole SimSnapshot (atomic)   GET /v1/status
GET  /v1/time         GET /v1/bodies[/{id}[/orbit|/atmosphere]]
GET  /v1/vessels[/{id}[/telemetry|/orbit|/engines[/{n}]|...]]          (subtree granularity)
PUT  /v1/vessels/{id}/engines/{n}/active        body: {"value":true}  or text/plain "1"
POST /v1/vessels/{id}/ctl/ignite                (TRIGGER → POST; STATE → PUT; reads → GET)
GET  /v1/events                                  Server-Sent Events (curl-able, EventSource-able)
GET  /v1/time/wait?until=<ut>                    long-poll sim-time alarm (see 2.1)
GET  /v1/vessels/{id}/orbit/state?t=<ut>         parameterized: Orbit.GetStateVectorsAt(SimTime)
GET  /v1/bodies/{id}/atmosphere?alt=<m>          GetAtmospheric{Pressure,Density}AtAltitude
GET  /v1/openapi.json                            generated spec → players codegen SDKs in any language
POST /v1/debug/...                               cheat namespace, same gating as /sim/debug
```

Errors: HTTP status + `{"errno":"EINVAL","message":…}` reusing the Part 2 vocabulary. No auth
(loopback + slirp only reach it from this machine/guest); rate limiting unnecessary for MVP.

### T3 — serial text feeds (phase G7; realism tier, cheap)

QEMU already carries a `virtio-serial-pci` controller (QGA uses one port). Add named ports —
`-chardev socket,id=…,host=127.0.0.1,port=<p>,server=on,wait=off` +
`-device virtserialport,chardev=…,name=gatos.tlm.0` — which appear in-guest as
`/dev/virtio-ports/gatos.tlm.0` (busybox mdev may need a tiny symlink script from
`/sys/class/virtio-ports/*/name` — shell-only, rootfs-overlay, guest v3).

- **Telemetry feed** (`gatos.tlm.0`, read-only): one NDJSON frame per sample tick — `cat` it,
  pipe to `jq`. Optional flavor mode: NMEA-0183-style sentences (`$KSGGA,…*checksum`) because
  "GPS receiver on a UART" is the single most authentic embedded-Linux sensor experience.
- **Command port** (`gatos.cmd.0`, read-write): SCPI-flavored line protocol
  (`MEAS:ALT? V123` / `CTL:ENG0:ACT 1` → `OK`/`ERR EINVAL`), exactly how lab and flight
  instruments actually talk. Same command pipeline underneath.

### T4 — space-bus emulation: CCSDS, MIL-STD-1553, SpaceWire (phase G7; deep-realism experiments)

Carried over additional virtio-serial ports (binary-clean), host side in the new game-free
`gatOS.Bus` project; the TS SDK ships reference encoders/decoders.

- **CCSDS Space Packets** (recommended first — it is what real ground segments parse): TM stream
  of 6-byte-primary-header packets (version/type/APID/sequence/length), one APID per subsystem
  (nav, power, propulsion, …); TC packets carry commands routed into the same command pipeline.
  Trivially documented, parseable in ~30 lines of any language.
- **MIL-STD-1553** (per project-owner suggestion): model the guest as **Bus Controller**, each
  vessel as a **Remote Terminal** (RT address 0–30), subsystems as subaddresses; BC→RT transfers
  carry command words + 16-bit data words (commands), RT→BC transfers return telemetry words +
  status word. We emulate the framing (command/status/data words over the stream), not the
  1 MHz Manchester physical layer. Great teaching artifact; word-size quantization is a real
  constraint players must engineer around, exactly like real avionics integration.
- **SpaceWire** (stretch): packet = path/logical address + cargo + EOP, typically carrying RMAP
  or CCSDS on top. More plumbing for less semantic difference than 1553/CCSDS — do it only if the
  first two prove fun.

### Rejected transport options (documented so we don't re-litigate)

- **Custom QEMU device models** (virtual i2c/SPI/PCI sensor chips): requires forking and
  rebuilding QEMU on every platform we bundle (breaks OS_PLAN D5 pinned-Weil-installer bundling),
  plus guest kernel driver expectations. The sysfs-style file tree delivers the same UX legally.
- **vsock**: no Windows-host QEMU support (already ruled out in OS_ANALYSIS; the architecture's
  "one transport, plain TCP over slirp" rule stands — every transport above is TCP or virtio-serial).
- **In-guest daemons/agents**: violates G-D3 (zero custom guest binaries).
- **Real `/sys` integration** (gpio-sim, iio dummy drivers): needs kernel modules + custom wiring
  to the host; our 9p tree mimics the *layout contract* instead, which is what scripts care about.

---

## Part 7 — Configuration additions (`gatos.toml`)

```toml
[control]
enabled = true              # master switch for all writes (false → every write EACCES)
all_vessels = true          # G-D1; false restricts writes to the active vessel
debug_namespace = true      # G-D2; /sim/debug + /v1/debug visibility
command_timeout_ms = 2000
max_commands_per_frame = 64

[http]
enabled = true              # G5+
preferred_port = 4242       # 0 = ephemeral only

[serial]                    # G7+
telemetry_port = false
command_port = false
bus_ccsds = false
bus_1553 = false
```

Same Tomlyn snake_case/clamp/atomic-save discipline as the existing `GatOsConfig` (T6.3).

---

## Part 8 — Example SDK: TypeScript on Bun (phase G6)

`examples/sdk-ts/` — package `gatos-sdk`, runnable **inside the guest**. Alpine is musl: Bun
ships `bun-linux-x64-musl` builds (document the install one-liner); SDK core avoids Bun-only
APIs so `apk add nodejs` works too. (A `bun` skill exists in this workspace for the implementer.)

- **Transports**: `FsTransport` (reads/writes `/sim` — default, zero deps) and `HttpTransport`
  (`$GATOS_HTTP`); identical typed API on top, proving the abstraction holds.
- **Typed models**: `VesselTelemetry`, `Body`, `EngineState`, … hand-written for FsTransport,
  regenerated from `/v1/openapi.json` for HTTP — demonstrating the "players build their own SDK
  in any stack" story.
- **Reactive reads**: `for await (const line of sdk.vessel("x").stream())` (tails `stream`),
  `sdk.events()` (blocking-read `/sim/events` or SSE).
- **Warp-aware time helpers** (per 2.1): `sdk.time.waitUntilUt(ut)` (alarm device / long-poll),
  `sdk.time.sleepSim(seconds)`, `sdk.atWarp(1, fn)` (debug warp-set around a closed-loop
  maneuver); stream consumers get `sim_dt`/`warp` on every frame.
- **Commands with real errors**: `await sdk.vessel("x").engine(0).activate()` → throws
  `GatosError{errno:"ENOENT"}` mapped from write-errno/HTTP body.
- **Example scripts** (each ~a page, the marketing material): `orbit-watch.ts` (live TUI),
  `solar-keeper.ts` (deploy panels when `occluded` flips 0 — the sensor→actuator loop),
  `launch-monitor.ts` (events + flameout abort), `burn-exec.ts` (G4: attitude+burn target, then
  throttle), plus a 20-line **pure-shell** equivalent in the README to prove no SDK is *needed*.

---

## Part 9 — Phasing (G-series; independent of OS_PLAN M-numbering)

Each phase ends with build + full test suite green (game-free unit tests + `GATOS_IT` guest
fixtures), matrix/doc updates, and a `CLAUDE.md` status touch — same discipline as M-tasks.
Suggested ordering below; G1–G2 are the foundation and should precede or interleave with M10/M11
at the owner's discretion (no hard dependency either way; G5's guest v3 should ride the same
image bump as any M10 guest changes to avoid churning `GUEST_VERSION` twice).

| Phase | Delivers | Exit criterion |
|---|---|---|
| **G1 — command pipeline + first controls** ✅ **BUILT** | `Commands/` model+queue, 9p write path (Tlopen/Twrite/Tsetattr/mode bits), `ControlFile`/`TriggerFile`, actuators: engine `active`, vessel `ignite`/`shutdown`/`lights`, animation/solar `goal`; `[control]` config | From a real guest shell: ignite, shut down, deploy panels, toggle lights; bad writes return correct errnos; suite green. **Done** (unit + 9p-client + `GATOS_IT` fixtures green; in-game pass pending the purrTTY tip release, as for T6.6) |
| **G2 — integration layer formalization** ✅ **BUILT** | `Game/Ksa/` Readers/Actuators/Catalog refactor (TelemetrySampler absorbed), `[KsaAnchor]`, `docs/KSA_INTEGRATION_MATRIX.md` seeded, health latches + `/sim/status/` | Matrix covers the exposed points; a faulting accessor degrades gracefully and surfaces in `/sim/status/accessors`. **Done** (the `[KsaAnchor]`↔matrix consistency *test* is deferred — `gatOS.GameMod` has no test project; the matrix is maintained by hand) |
| **G3 — read-surface expansion** ✅ **BUILT** | `/sim/bodies`, `/sim/system`, `time/{sim_dt,warp_speeds,auto_warp,alarm}`, vessel `telemetry` JSON, environment/navball/orbit-extras/encounters, solar/lights/animations/docking/decoupler/generator/rcs read views, new event types, light colour/brightness writes (template-clone) | New tree verified over the 9p client; `EventDiffer` tests per new event. **Done** (in-guest pass pending the purrTTY tip release; aero `cda` deferred — private) |
| **G4 — full control surface** ✅ **BUILT** | throttle (reflection), attitude/burn (FlightComputer), RCS, staging, decouplers, solver-phase queue (Harmony prefix on `ExecuteNextVehicleSolvers`), `/sim/debug/*` (teleport/refill/warp/switch) | Control surface verified over the 9p client + `[control]` config. **Done** (gimbal command + `parts/<instanceId>` tree + RCS pulse deferred — see matrix; scripted-burn in-game pass pending) |
| **G5 — HTTP transport** ✅ **BUILT** | `gatOS.Http` (raw `TcpListener`, **not** GenHTTP/HttpListener — see CLAUDE.md), REST snapshot projections + SSE + `time/wait` long-poll + OpenAPI + generic `POST /v1/command`, errno→status; `[http]` config; `gatos.httpport` cmdline; guest v3 plumbing written (hosts `sim`, `$GATOS_HTTP`) | `gatOS.Http.Tests` (13, HttpClient over the live socket) green. **Done** host-side; in-guest `curl http://sim:<port>/v1/...` pass rides guest v3 |
| **G6 — SDK + player docs** ✅ **BUILT** | `examples/sdk-ts` (Bun/Node): `FsTransport`+`HttpTransport` behind one typed `GatosClient`, reactive events, warp-aware time helpers, `GatosError` errno, example scripts + pure-shell README | Type-careful TS; runs in-guest against both transports (Bun musl). **Done** |
| **G7 — bus experiments** ◐ **codecs BUILT** | `gatOS.Bus`: `Ccsds` TM packets, `Nmea` sentences+checksum, `ScpiCommandPort`→`SimCommand`, `SerialTelemetry` (NDJSON/NMEA/CCSDS); `[serial]` config | `gatOS.Bus.Tests` (15) green. **Codecs + command port done**; the QEMU virtio-serial port wiring + guest `/dev/virtio-ports` exposure (live bridge) + 1553/SpaceWire ride guest v3 |

## Part 10 — Open implementation questions (resolve during the listed phase)

1. **Throttle write path** (G4): exact public-ish API for manual throttle — `InputEvents`?
   `EngineControllerState.CommandThrottle` per engine? Needs in-game verification.
2. **Staging trigger** (G4): `InputEvents.StageChangeData` vs a higher-level "activate next
   stage" entry point.
3. **RCS pulse semantics** (G4): does `CommandPulseTime` fire outside the flight-computer loop?
4. **Module ordinal stability** (G3): freeze ordinal-vs-InstanceId addressing rules after
   observing renumbering behavior across docking/decoupling.
5. **Light brightness scale** (G3): confirm `Intensity.Value` range before freezing `0..1`.
6. **`Program.ControlledVehicle` reflection write** (G4, debug): side effects on camera/UI.
7. **Multiplayer** (G4): KSA.Networking exists; assume mutations are local-authoritative for now,
   note in docs.
8. **Per-vessel authority / multi-VM** (post-G7): when OS_PLAN D10 multi-computer lands, the
   `[control]` gate grows a per-VM vessel binding.
9. **High-warp stream decimation policy** (G3): today one stream line per published sample
   regardless of warp; decide whether to optionally emit interpolated/per-sim-step lines (likely
   no — `sim_dt` metadata + the alarm device cover the use cases without inventing data).
10. **Stream format extension vs frozen Formats** (G1): adding `warp`/`sim_dt` keys to stream
    NDJSON lines is additive (JSON consumers ignore unknown keys), but `Formats` is documented as
    frozen — confirm the additive-keys exception and record it in the matrix.
