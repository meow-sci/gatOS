# KSA Particle System — Deep Dive & gatOS Integration Analysis

> **Status:** research/analysis only. Nothing below is built yet. If any of the proposed
> `/sim` surface is implemented, `SPEC_9P_FILESYSTEM.md` and
> `docs/KSA_INTEGRATION_MATRIX.md` MUST be updated in lockstep (see CLAUDE.md "The `/sim` API
> contract"). All KSA type names proposed here live **only** under
> `gatOS.GameMod/Game/Ksa/` per the G2 dependency rule.

All file/line references are to the decompiled sources under `thirdparty/ksa/`.

---

## 1. Executive summary

KSA's particle system is a **GPU-compute-driven, pool-allocated emitter system**. The whole
thing is one singleton, `Program.Instance.ParticleSystem`, of concrete type
`ParticleSystem<ParticleUpdateData, ParticleRenderData>` (`KSA/Program.cs:101`). It owns:

- a fixed-capacity **pool of pre-allocated `ParticleEmitter` objects** (one GPU buffer slot each);
- six **renderers** (how particles draw) and six **updaters** (compute shaders that move/age them);
- a set of **named, XML-defined emitter templates** (`ParticleEmitterReference`) registered in
  `ModLibrary` and looked up by string id.

The runtime spawn flow, used everywhere in-game, is a single call:

```csharp
Program.Instance.ParticleSystem.GetAndInitializeEmitters("ThrusterSparks", out var emitters);
// then per-emitter: set placement (LocalOffset), Context (Vehicle/Part), Origin, scale knobs…
foreach (var e in emitters) vehicle.AddEmitter(e);
```

Each frame, on the render path (`Program.OnPreRender`, `KSA/Program.cs:2042-2049`), the game calls
`vehicle.SpawnParticles(dt)` for every in-frame vehicle (which spawns the built-in effects) and then
`ParticleSystem.UpdateEmitters(dt)` which ticks **every emitter in the pool**. Emitters self-retire
when their spawning is complete and their longest-lived particle has decayed, returning their slot to
the pool.

**Bottom line for gatOS:** spawning/killing emitters programmatically is very feasible. It is a
game-thread mutation (exactly like our existing actuators), reachable through the same
`SimCommand → CommandQueue.Drain → KsaCatalog → Actuator` pipeline. Three tiers of control exist,
from "reuse the 9 built-in effects by name" (zero XML) up to "fully hand-built custom emitter in C#"
(also zero XML — the renderers, updaters, shapes and spawn presets are all public). Shipping our own
XML emitter templates is an optional middle tier.

---

## 2. Architecture map

```
Program.Instance.ParticleSystem : ParticleSystem<ParticleUpdateData, ParticleRenderData>
│
├── ParticleDataBuffer            GPU storage: one big state buffer + per-frame render buffers
│     • Capacity = emitterCount   (pool slots)
│     • MaxParticlesPerEmitter    (particles per slot)
│     • AcquireSlot()/ReleaseSlot(), QueueParticleCreation() → staging copy to GPU
│
├── EmitterPool : List<ParticleEmitter>   pre-allocated, Capacity == buffer Capacity
│     • Get(count) → grabs `count` free emitters, marks CanAcquireFromPool=false
│     • ResetAll(), Dispose()
│
├── Renderers (IParticleRenderer)         pick via ParticleEmitterReference.Renderer enum
│     SimpleColorRenderer  → "default"   SimpleParticleVert/Frag       (additive-ish color quads)
│     PbrRenderer          → "pbr"       PbrParticleVert/Frag          (lit mesh, e.g. ice rocks)
│     PbrNoCullRenderer    → "pbr" no-cull (foam planes — two-sided)
│     PlanetColorRenderer  → "planetColor" PlanetColorParticleFrag     (samples planet albedo)
│     VolumetricRenderer   → "volumetric"  VolumetricParticle*         (soft dust/smoke, transmittance blend)
│     BillboardRenderer    → "billboard"   BillboardParticle*          (camera-facing textured quad)
│
└── Updaters (IParticleUpdater = compute pipeline)   pick via ParticleEmitterReference.Updaters list
      SimpleMovementUpdater          "SimpleMovementParticleUpdater"          gravity+force+velocity
      VehicleAttachedMovementUpdater "VehicleAttachedMovementParticleUpdater" particles ride the vehicle frame
      ScreenSpaceCollisionUpdater    "CollisionParticleUpdater"               depth-buffer collision (debris bounce)
      PlanetColorUpdater             "PlanetColorParticleUpdater"             tints from planet surface color
      HueShiftUpdater                "HueShiftParticleUpdater"                animates hue over life
      VolumetricFadeUpdater          "VolumetricFadeUpdater"                  soft fade in/out for smoke
```

Source: `ParticleSystem.cs:63-172` (construction), `:174-226` (`InitializeEmitter` — maps the
reference's `Renderer`/`Updaters` enums to the concrete objects).

### 2.1 The two data structs (GPU layout)

- **`ParticleUpdateData`** (`ParticleUpdateData.cs`) — per-particle *simulation* state the compute
  shaders read/write: `Position`, `Velocity`, `Age`, `Lifespan`, `Color`, `AngularVelocity`,
  `PackedRotation`, `InitialScale`, `ColorShift`, `Extra`. This is `TUpdate`.
- **`ParticleRenderData`** (`ParticleRenderData.cs`) — per-particle *render* output the compute
  shaders produce and the vertex shader consumes: `Color`, `Model` (float4x4), `Extra`,
  `MaterialIndex`. This is `TRender`.

The system is generic over these (`ParticleSystem<TUpdate, TRender>`), but the game only ever
instantiates the one concrete pairing, so in practice you treat them as fixed.

### 2.2 GPU pipeline (per frame, render thread)

1. `ParticleDataBuffer.Update` (`ParticleDataBuffer.cs:107`) flushes newly spawned particles from a
   CPU staging buffer into the GPU state buffer (one `VkBufferCopy` per spawn) with a barrier.
2. Each updater's `UpdateParticles` (`ParticleUpdater.cs:148`) writes a `GpuEmitterParams` record per
   active emitter (delta time, particle count, freeze flag, model matrix, gravity, force, pending
   pos/vel offsets, `Extra`), uploads the active-slot list, then dispatches the compute shader
   (`64`-thread groups over `activeEmitters * MaxParticlesPerEmitter`).
3. Renderers issue **indirect** indexed draws: `ParticleEmitter.WriteInstancesToGpu`
   (`ParticleEmitter.cs:414`) builds a `VkDrawIndexedIndirectCommand` with `InstanceCount =
   ActiveParticles` and `FirstInstance = BufferIndex * MaxParticlesPerEmitter`.

The whole feed is gated by `GameSettings.Current.Graphics.Particles` (master on/off) and scaled by
`GameSettings.Current.Graphics.ParticleQuality` (divides `MaximumParticleCount`,
`ParticleEmitter.cs:201-213`). **Caveat for gatOS: if the player disables particles in graphics
settings, programmatic spawns won't render** — `UpdateEmitters`/`UpdateParticles` early-return
(`ParticleSystem.cs:256, 268, 293`).

---

## 3. The emitter template model (`ParticleEmitterReference`)

`ParticleEmitterReference` (`ParticleEmitterReference.cs`) is a `SerializedId` — i.e. it is loaded
from XML and registered in `ModLibrary`. It is the *template*: a bag of `*Reference` config values
plus four enums that select behavior. Built-in templates live in
`Content/Core/ParticleEmitterAssets.xml`.

### 3.1 The four enums (the behavioral selectors)

| Enum | Values | Meaning |
|---|---|---|
| `EmitterSpawnLogic` (`ParticleEmitterShape`) | `Point, Sphere, Box, Cone, Torus, Capsule, VehicleSurface, PrecomputedTransform` | **where** particles spawn (the emission volume) — see `EmitterSpawnPresets.cs` |
| `ParticleSpawnLogic` (`ParticleSpawnType`) | `Basic, MoveAwayFromCenter, Fountain, ScatterFromCenter, SurfaceNormalRandomRotation, SurfaceNormalAlignedRotation` | **initial velocity/rotation** of each particle — see `ParticleSpawnPresets.cs` |
| `Renderer` | one of the 6 renderers above | how it draws |
| `Updaters` | a **list** of the 6 updaters | how it moves/ages (composable) |

`SpawnMode` (`EmitterSpawnMode.cs`): `None, Burst, OverTime, Endless`.
- **`Burst`** — emit `SpawnRate` particles once, then `_spawningComplete` (one-shot puffs: sparks,
  impact, decouple).
- **`OverTime`** — emit `SpawnRate`/sec until `SpawnDuration` elapses.
- **`Endless`** — emit `SpawnRate`/sec forever (until killed) — used for tank ice/frost.

Spawn logic: `ParticleEmitter.TrySpawnParticles` (`ParticleEmitter.cs:429`) and `SpawnParticles`
(`:465`).

### 3.2 The config fields (all `*Reference`, i.e. XML-bindable)

`SpawnRate`, `SpawnDuration`, `Force` (constant accel), `Mesh` (particle mesh id), `EmitterSize`
(radius), `EmitterExtents` (float3 — shape dimensions), `ParticleColor`, `ParticleLifespan` (min/max),
`ParticleVelocity`, `ParticleSize` (min/max), `ParticleVelocityShift`, `ParticleColorShift`,
`ParticleExtra`/`EmitterExtra` (float4 — shader-specific knobs), `GravityStrength`,
`ParticleAngularVelocity`, `MaxParticles`, `MaterialId`, `VehicleIsParent`, `InheritVelocity`,
`EmitterRelative`, and a **nested `ParticleEmitters` list** (child emitters — composite effects).

`ApplyTo<TUpdate,TRender>` (`ParticleEmitterReference.cs:174`) copies all of this onto a live
`ParticleEmitter`, resolving the two enums into delegate functions (`ResolveShapeLogic`,
`ResolveSpawnLogic`) and resolving a `ParentEmitter` reference first (inheritance).

### 3.3 Composite (nested) emitters

A template can nest children in `<ParticleEmitters>`. `GetTotalEmitterCount` (`:216`) counts the
whole tree; `GetAndInitializeEmitters` acquires that many pool slots and
`InitializeEmittersRecursive` (`ParticleSystem.cs:241`) configures each. Example: `PlanetImpact`
(rock debris) nests a volumetric dust child (`ParticleEmitterAssets.xml:199-254`).

### 3.4 The built-in templates (what already exists)

From `Content/Core/ParticleEmitterAssets.xml`:

| Id | Renderer / Updaters | Mode | Used by |
|---|---|---|---|
| `ThrusterSparks` | SimpleColor / SimpleMovement+HueShift | Burst | engine ignition |
| `Decouple` | SimpleColor / SimpleMovement | Burst | decoupler separation |
| `IceDebrisSolid` | Pbr / VehicleAttachedMovement | Endless | tank frost (solid rocks) |
| `IceDebrisSolidBurst` | Pbr / VehicleAttachedMovement | Burst | tank frost (initial puff) |
| `IceDebrisVolumetric` | Volumetric / VolumetricFade | Endless | tank frost (defined, vapor) |
| `InsulationDebris` | PbrNoCull / VehicleAttachedMovement | Burst | tank foam shedding |
| `PlanetImpact` | PlanetColor / SimpleMovement+PlanetColor+HueShift+ScreenSpaceCollision (+volumetric child) | Burst | ground impact dirt/dust |
| `Volumetric` | Volumetric / SimpleMovement | Burst | generic smoke template |
| `Billboard` | Billboard / SimpleMovement | Burst | generic billboard template |
| `Debug_SphericalBurst` | Pbr / SimpleMovement+ScreenSpaceCollision+HueShift (+3 children) | Burst | debug/testing |

`EmitterExtra`/`ParticleExtra` semantics are renderer/updater-specific; the XML documents the ice
case inline (`xml:157-159`): `Extra.X` = "start attached", `Extra.Y` = scale fade-in fraction,
`Extra.W` = dislodge rate.

---

## 4. How the live game spawns effects (the real call sites)

All live spawning is driven from `Vehicle.SpawnParticles(dt)` (`KSA/Vehicle.cs:4217`), called per
in-frame vehicle from `Program.OnPreRender` (`Program.cs:2047`).

### 4.1 Rocket nozzle ignition "sparks/ice"
`Vehicle.SpawnThrusterSparks` (`Vehicle.cs:4274`). Trigger: `RocketNozzleState.ActivatedThisFrame`.
Guards on `NozzleExitRadius >= 0.25`. Scales `Size`/`SpawnRate`/`Radius` by nozzle radius, builds a
`LocalOffset` from the exhaust direction+location (`FxExhaustDirectionVehicleAsmb` /
`FxExhaustLocationVehicleAsmb`), sets `Context.Vehicle`/`Context.Part`/`Origin`, then `AddEmitter`.

### 4.2 Tank ice / foam / frost
`Vehicle.TrySpawnIceParticles` (`Vehicle.cs:4369`) — driven from
`TrySpawnAndUpdateLaunchParticles` (`:4365`). Triggers are velocity-gated:
- velocity ≤ 25 m/s and not yet spawned → `SpawnInsulationDebris("InsulationDebris", tank)` (foam);
- velocity ≤ 5 m/s and the tank holds substance (`MoleState.Mass > 0`) → `SpawnIceDebris("IceDebrisSolid")`
  + `SpawnIceDebris("IceDebrisSolidBurst")`.

These are **Endless** emitters whose handles are retained on the `Tank` module
(`Tank.IceEmitters` / `Tank.InsulationEmitters`, each an `EmitterRef` holding a
`ParticleEmitter.Handle`). `Vehicle.UpdateIceDebrisParticles` (`Vehicle.cs:4296`) then modulates them
live each frame: above 5 m/s it sets `SpawnRate = 0` and `FreezeAge = false` (let frost blow off);
at/below it restores `SpawnRate` and sets `FreezeAge = true` (frost clings). This is the canonical
**long-lived, runtime-controlled emitter** pattern — exactly the shape gatOS needs.

`SpawnIceDebris`/`SpawnInsulationDebris` (`Vehicle.cs:4414`/`:4436`) use
`Context.PrecomputedSpawnTransforms = tank.ParticleSpawnTransforms` — the tank pre-samples its own
mesh surface into spawn transforms (the `PrecomputedTransform` emitter shape consumes them,
`EmitterSpawnPresets.cs:70`).

### 4.3 Ground/terrain impact dirt
`Vehicle.TrySpawnGroundImpact(GroundImpactEvent)` (`Vehicle.cs:4231`), fired from
`GroundImpactEvent.cs:19`. Guards on impact speed ≥ 2 m/s and not within 5 s of a teleport. Spawns
`"PlanetImpact"` (the composite), orients a basis from the impact direction, places it at the impact
point, and scales velocity/size/lifespan/count by impact speed. One-shot `Burst`.

### 4.4 Decoupler separation
`Decoupler.cs:98` spawns `"Decouple"`, scaled by connector radius. One-shot `Burst`.

### 4.5 Emitter ↔ vehicle bookkeeping
`Vehicle.AddEmitter`/`RemoveEmitter` (`Vehicle.cs:533/538`) just track emitters in `_vehicleEmitters`.
That list is **only** used to apply bubble-reorigin position/velocity deltas to in-flight particles
(`UpdateParticles(posOffset, velOffset)` at `Vehicle.cs:1586,1612,1716`) so particles don't jump when
the floating origin snaps. **The actual per-frame ticking is pool-wide** — `UpdateEmitters` iterates
`EmitterPool.Emitters`, not `_vehicleEmitters` (`ParticleSystem.cs:260`). So:
- a **vehicle-attached** effect should be `AddEmitter`'d (gets origin-snap correctness);
- a **free/world** effect just needs `Origin` set and is ticked anyway.

---

## 5. Emitter lifecycle & the control surface

`ParticleEmitter` (`ParticleEmitter.cs`) is the live, mutable object. The fields/methods that matter
for programmatic control:

**Placement / context**
- `LocalOffset` (float4x4) — spawn transform relative to the vehicle/origin.
- `Context` (`EmitterContext`) — `Vehicle`, `Part`, `Astronomical`, `PrecomputedSpawnTransforms`.
- `Origin` (`BubbleOrigin?`) — the floating-origin anchor; required for the emitter to render
  (`UpdateRenderData` skips emitters with no `Origin`, `ParticleSystem.cs:282`).
- `EmitterRelative`, `VehicleIsParent`, `GravityStrength`, `Force`, `Extra`, `MaterialIndex`.

**Emission knobs (live-tunable)**
- `SpawnRate`, `SpawnDuration`, `SpawnMode`, `MaximumParticleCount`.
- `ParticleInfo` (`ParticleSpawnInfo`: lifespan, color, size, velocity, …),
  `EmitterSpawnInfo` (`EmitterShapeInfo`: radius, extents, the shape delegate).

**Lifecycle**
- `MaximumParticleCount` is clamped by `ParticleQuality` and the pool's `MaxParticlesPerEmitter`.
- `FreezeAge` (`:153`) — pause/resume particle aging (frost cling vs blow-off).
- `ForceSpawningComplete()` (`:533`) — stop emitting; existing particles live out their life.
- `Kill()` (`:538`) — stop emitting **and** zero the longest-decay time so the emitter retires ASAP.
- `Update(dt)` (`:329`) — advances sim time, `UpdateUniforms`, `TrySpawnParticles`; also runs
  `TryUnregisterEmitter` once `HasCompletedSimulation` (spawning done + all particles decayed), which
  after `MaxFramesInFlight` frames calls `ResetEmitter()` → returns slot to pool, bumps
  `InitializationId`.
- `CreateHandle()` (`:233`) → `Handle<TUpdate,TRender>` — a **generation-checked weak reference**
  (`IsValid` compares `InitializationId`). This is how the game safely keeps a reference to a pooled
  emitter that may have been recycled. **gatOS must use handles**, never raw emitter references.

**Key safety property:** emitters are a *finite shared pool*. The game and any gatOS spawns draw from
the same `EmitterPool`. `Get()` returns `false` if not enough free slots
(`ParticleSystem.cs:23-44`). Heavy programmatic spawning can starve in-game effects — gatOS must
budget/cap its own emitters and surface the failure.

---

## 6. Three tiers of programmatic control

### Tier 1 — reuse a built-in template by id (zero XML, lowest effort)
```csharp
var ps = Program.Instance.ParticleSystem;
if (ps.GetAndInitializeEmitters("Debug_SphericalBurst", out var emitters)) {
    foreach (var e in emitters) {
        e.Context.Vehicle = vehicle;
        e.Origin = vehicle.BubbleOrigin;
        e.LocalOffset = float4x4.CreateTranslation(localPos);  // where on/near the vessel
        vehicle.AddEmitter(e);
    }
}
```
You get any of the 10 built-ins (§3.4). Good for "puff at the vessel", "spawn impact dust", etc.
Limited to existing looks.

### Tier 2 — ship our own XML templates (the "pre-defined mod data" path)
KSA loads `ParticleEmitterReference` from XML via `OnDataLoad` → `ModLibrary.Register`
(`ParticleEmitterReference.cs:133-172`). If we add an asset XML to the gatOS mod content (following
`Content/Core/ParticleEmitterAssets.xml`'s schema) that defines new `<ParticleEmitter Id="…">`
entries, they become available to `GetAndInitializeEmitters("…")` like any built-in. This gives full
artistic control (custom color/size/mesh/material/shape/updaters) **without C# render code**.
*Open question to verify in-game:* whether gatOS-mod content XML is auto-discovered by KSA's
`ModLibrary` loader, or needs a manifest entry — to be confirmed against the `ksa` skill's mod
lifecycle docs during implementation.

### Tier 3 — fully hand-built emitter in C# (zero XML, maximum control)
Everything `ParticleEmitterReference.ApplyTo` does is reachable directly, because the renderers,
updaters, shape presets and spawn presets are all **public**:
```csharp
var ps = Program.Instance.ParticleSystem;
if (ps.EmitterPool.Get(1, out var list)) {
    var e = list[0];
    e.UpdateMesh("ParticleSphere");
    e.RegisterRenderer(ps.SimpleColorRenderer);          // any of the 6
    e.RegisterComputePipeline(ps.SimpleMovementUpdater); // any subset, in order
    e.SpawnMode = EmitterSpawnMode.Burst;
    e.SpawnRate = 128;
    e.MaximumParticleCount = 128;
    e.EmitterSpawnInfo = new() { EmitterSpawnLogic = EmitterSpawnPresets.SphericalEmitter, Radius = 2f };
    e.ParticleInfo = new() {
        ParticleSpawnLogic = ParticleSpawnPresets.Fountain,
        Lifespan = new float2(1,3), Size = new float2(0.05f,0.1f),
        Color = new float4(15,11,6,1), Velocity = new float3(1.25f),
    };
    e.Context.Vehicle = vehicle; e.Origin = vehicle.BubbleOrigin;
    e.LocalOffset = float4x4.CreateTranslation(localPos);
    vehicle.AddEmitter(e);
}
```
This is the most flexible and the cleanest dependency story (no content pipeline), at the cost of
hard-coding the look in C#. **Recommended default for gatOS** — pair it with a small set of
parameterized "effect profiles" exposed over `/sim`.

> Note: `GetAndInitializeEmitters` is just `EmitterPool.Get` + `InitializeEmitter` (=`ApplyTo` +
> register renderer/updaters). Tier 3 is literally inlining that, which is why no XML is needed.

---

## 7. Proposed gatOS integration

### 7.1 Where it slots into the existing architecture
This is a **write/actuation** surface, identical in shape to lights/RCS/staging:

```
/sim control file  →  SimCommand (game-free: action key + token + ordinal + values)
                   →  CommandQueue (transport thread only enqueues)
                   →  CommandQueue.Drain  (game thread, [StarMapBeforeGui])
                   →  KsaCatalog.Dispatch →  ParticleActuator  (new, under Game/Ksa/Actuators/)
                   →  Program.Instance.ParticleSystem.*
```

- **Phase:** `Frame` (the default). Particle spawning runs on the render path *after* the
  game-thread `BeforeGui` hook within the same frame, and touches only the particle pool + the
  vehicle's emitter list — it does **not** need to be solver-visible, so it does **not** belong in
  `SimCommand.SolverActions`. Draining in the normal frame hook is correct and safe: both the drain
  and `OnPreRender` are on the main thread and sequential, so an emitter acquired/configured in the
  hook is picked up by that frame's `UpdateEmitters`.
- **Dependency rule (G2):** all KSA particle types (`ParticleEmitter`, `ParticleSystem`,
  `EmitterSpawnPresets`, …) appear **only** in a new `Game/Ksa/Actuators/ParticleActuator.cs`
  (+ a `Readers` entry if we expose status), annotated `[KsaAnchor]`. `SimCommand`, the `/sim`
  tree, and `SimJson`/`Formats` stay game-free (string action keys + token/ordinal/values).

### 7.2 The handle-registry problem (the one new concept)
Unlike lights (addressed by a stable module ordinal), a spawned emitter has **no natural stable id** —
it's a recycled pool slot behind a generation-checked `Handle`. To let a program spawn an effect and
later modify/kill it, gatOS needs a **game-side registry** in `GameMod`:

```
int gatosHandleId  →  ParticleEmitter.Handle   (+ the owning Vehicle.Id, the template/profile)
```
- `particle.spawn` allocates the next `gatosHandleId`, spawns, stores the `Handle`, returns the id
  (via the command result / a readback file).
- `particle.kill` / `particle.set_rate` / `particle.freeze` look up the `Handle`, check `IsValid`
  (silently drop if the pool recycled it), and mutate.
- A periodic sweep drops invalid handles (mirrors `Tank.UpdateIceDebrisParticles`' compaction).

This registry is game-coupled state; it lives next to `KsaCatalog`, not in `gatOS.SimFs`.

### 7.3 Candidate `/sim` surface (illustrative — NOT yet a spec change)
A `/sim/vessel/<id>/fx/` (or a vessel-agnostic `/sim/fx/`) control directory:

| Path | Archetype | Action key | Payload | Effect |
|---|---|---|---|---|
| `fx/spawn` | token+vector ctl | `particle.spawn` | token = profile/template id; values = `[x y z]` local offset (+ optional scale) | acquire+configure+`AddEmitter`; returns handle id |
| `fx/<h>/kill` | trigger | `particle.kill` | — | `Handle.TryGet()?.Kill()` |
| `fx/<h>/rate` | number ctl | `particle.rate` | value = spawn rate | live `SpawnRate` |
| `fx/<h>/freeze` | trigger/bool | `particle.freeze` | 0/1 | `FreezeAge` |
| `fx/active` | read file | — | — | list of live gatOS handle ids (from the registry) |

`particle.spawn` fits the existing **token control file** (template id) augmented with a **vector**
(placement) — or a small dedicated control file. Because reads of GPU particle state aren't
meaningful, the read side is just "what have I spawned and is it still alive", served from the
registry (no KSA read needed → can stay in a thin reader).

A small fixed catalog of **named profiles** (Tier-3 hand-built looks: `spark`, `dust`, `smoke`,
`debris`, `vapor`) keeps the token space stable and documented, decoupled from KSA's internal
template ids.

### 7.4 Constraints & caveats to encode
1. **Finite shared pool** — cap gatOS's concurrent emitters; return `EAGAIN`/`Unsupported` when
   `Get()` fails so a program can back off. Never starve the game's own effects.
2. **Graphics gate** — if `GameSettings.Current.Graphics.Particles` is off, spawns silently don't
   render. Surface this (e.g. a `fx/available` flag) rather than failing opaquely.
3. **Origin required** — an emitter with no `Origin` never renders. Vehicle-attached spawns
   (`Origin = vehicle.BubbleOrigin`, `Context.Vehicle = vehicle`) are the simplest correct mode and
   should be the primary supported placement. World-anchored placement (arbitrary `BubbleOrigin` +
   `ModelMatrix`) is possible but materially more complex (floating-origin math in
   `UpdateUniforms`, `ParticleEmitter.cs:369`) — defer it.
4. **Authority gate (G-D1)** — route `particle.*` through the same `allVessels`/`ControlledVehicle`
   check `KsaCatalog` already applies; treat free-form spawning as a cosmetic-but-still-gated action
   (likely fine on the controlled vessel; consider the `debug.` namespace for unrestricted spawning).
5. **Churn risk** — `ParticleEmitter`'s public field surface is broad and decompiled; annotate the
   actuator with `[KsaAnchor(... Risk = ChurnRisk.Medium/High)]` and a health latch so a future KSA
   drop degrades the sensor instead of crashing (the established pattern).

### 7.5 Recommended path
- **MVP:** Tier 1 + Tier 3 hybrid. A `ParticleActuator` with a handful of hard-coded profiles
  (Tier 3 hand-built) plus a passthrough to the 10 built-in ids (Tier 1). One `particle.spawn`
  action (token=profile, values=placement) + `particle.kill` + `particle.rate` + `particle.freeze`,
  a `gatosHandleId` registry, and a `fx/active` readback.
- **Later:** Tier 2 XML profiles if artists/modders want richer looks without code, and
  world-anchored placement if a use case needs untethered effects.
- **On implementation:** update `SPEC_9P_FILESYSTEM.md` (new `/sim` nodes + `particle.*` action keys
  + their phase = Frame) and `docs/KSA_INTEGRATION_MATRIX.md` (the new `[KsaAnchor]` points), and
  refresh the `gatos` skill's recipes — all in the same change (the constitution).

---

## 8. Key source references

| Concern | File:line |
|---|---|
| Singleton | `KSA/Program.cs:101` |
| Per-frame drive | `KSA/Program.cs:2042-2049` (`OnPreRender`) |
| System (pool, renderers, updaters, spawn entry) | `KSA.Rendering.Particles/ParticleSystem.cs` |
| Spawn entry point | `ParticleSystem.cs:228` (`GetAndInitializeEmitters`) |
| Emitter (live object, lifecycle, control fields) | `KSA.Rendering.Particles/ParticleEmitter.cs` |
| Handle (generation-checked ref) | `ParticleEmitter.cs:53-71`, `:233` |
| Kill / freeze / force-complete | `ParticleEmitter.cs:153, 533, 538` |
| Template (XML model) | `KSA.Rendering.Particles/ParticleEmitterReference.cs` |
| Apply template → emitter | `ParticleEmitterReference.cs:174` (`ApplyTo`) |
| Emission shapes | `KSA.Rendering.Particles/EmitterSpawnPresets.cs` |
| Initial velocity/rotation presets | `KSA.Rendering.Particles/ParticleSpawnPresets.cs` |
| GPU storage + spawn staging | `KSA.Rendering.Particles/ParticleDataBuffer.cs` |
| Compute updater (params upload + dispatch) | `KSA.Rendering.Particles/ParticleUpdater.cs` |
| Built-in templates (XML) | `Content/Core/ParticleEmitterAssets.xml` |
| Live effect call sites | `KSA/Vehicle.cs:4217-4455`, `KSA/Decoupler.cs:98`, `KSA/GroundImpactEvent.cs:19` |
| gatOS command model | `gatOS.SimFs/Commands/SimCommand.cs`, `CommandQueue.cs` |
| gatOS actuator pattern | `gatOS.GameMod/Game/Ksa/KsaCatalog.cs`, `Actuators/LightActuator.cs` |
