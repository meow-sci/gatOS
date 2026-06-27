# Scope — KSA Runtime Coupling

> The KSA touchpoints that are **not** `/sim` reads or writes: how gatOS plugs into the game process
> (StarMap lifecycle, Harmony patches), the threading phases that govern when KSA is touched, the
> coordinate frames & numerics it shares, the reflection accessors the compiler can't guard, the churn
> machinery, and the mod-ecosystem ABIs (purrTTY/StarMap/ModMenu — *not* KSA game state, listed for
> completeness). A KSA update can break the Harmony hook targets, the Brutal numerics, and the reflection
> fields; everything else here degrades gracefully or is decoupled.

---

## Lifecycle (StarMap hooks)

`gatOS.GameMod/Mod.cs` is `[StarMapMod]`. These attributes are the **complete** StarMap ABI (a loader
contract, not KSA game state):

| Hook | gatOS method | What runs | KSA touch |
|---|---|---|---|
| `[StarMapImmediateLoad]` | `OnImmediateLoad` | nothing (renderer not live) | none |
| `[StarMapAllModsLoaded]` | `OnFullyLoaded` | asset validation, config, build `/sim` stack + transports, `VmHost`/broker (no boot), register `"gatos"` shell, **install Harmony hooks** | install-time only |
| `[StarMapBeforeGui]` | `OnBeforeUi(dt)` | `SampleTelemetry(dt)` then `DrainCommands()` (**Frame phase**) | reads + Frame writes |
| `[StarMapAfterGui]` | `OnAfterUi(dt)` | `DrawGameUi()` (ImGui status window) | ImGui only |
| `[StarMapUnload]` | `Unload` | remove hooks, stop serial, dispose broker/servers (bounded) | uninstall only |

The game-coupled hook bodies live in the partial `Game/Mod.Game.cs` and are `[MethodImpl(NoInlining)]`
partial methods, so a missing KSA assembly fails at the *call site* (caught) rather than JIT of the
caller — the whole solution still builds without the game DLLs (CLAUDE.md dependency rule).

---

## Harmony patches (the two KSA hook targets) {#threading-phases}

gatOS installs exactly **two** Harmony patches, both in `Game/Mod.Game.cs`, both via
`AccessTools.Method(...)` with a null-check and try/catch so a missing/renamed target **disables that one
feature with a logged warning instead of crashing**:

| Patch | KSA target | Decomp file | Purpose | If target moves |
|---|---|---|---|---|
| Solver-drain **prefix** (`Priority.First`) | `Universe.ExecuteNextVehicleSolvers` | `KSA/Universe.cs` | drains `CommandPhase.Solver` commands inside the vehicle-solver step (`Mod.DrainSolverCommands`) | solver-phase commands (attitude/frame/target, burn, refills) never drain; logged once |
| Menu **postfix** | `Program.DrawProgramMenusHook` | `KSA/Program.cs` | draws the fallback top-level "gatOS" menu when the ModMenu mod is absent; also touches `Program.MainViewport.MenuBarInUse`, `ModLibrary.Find("ModMenu")` | gatOS menu only reachable via ModMenu; logged once |

**Risk: Medium.** Neither target appears in the 4680→4750 changelog and `Game/Mod.Game.cs` compiled
clean against 4750. These are the non-`/sim` KSA members most worth re-checking on any update (a rename
won't fail the build for the *patched method name string* if it's via `nameof` — it is, so a rename
**would** fail the build here; a signature change to `ExecuteNextVehicleSolvers` could silently change
when the prefix fires).

### Threading phases (binding)
- **Frame phase** — `OnBeforeUi` → `CommandQueue.Drain(CommandPhase.Frame, …)`. Most actions.
- **Solver phase** — Harmony prefix above → `CommandQueue.Drain(CommandPhase.Solver, …)`. The set is
  `SimCommand.SolverActions = { vessel.attitude_mode, vessel.attitude_frame, vessel.attitude_target,
  vessel.burn, debug.refill_fuel, debug.refill_battery }`. Phase is **derived from the action key**
  (`SimCommand.Phase`), the single source of truth — never passed at a construction site, so every
  transport routes identically. Rationale (FlightComputer `CopyFrom` snapshot/restore) is in
  [`ksa-write-surface.md`](ksa-write-surface.md#vessel-control-surface-g4).

Threading rules 1–5 (CLAUDE.md): game state read+mutated **only** on the game thread; 9p/HTTP/MQTT
threads only enqueue `SimCommand` and read the last published snapshot; `VmHost` is one-semaphore async;
nothing blocks the render thread.

---

## Coordinate frames & numerics {#frames-and-numerics}

gatOS reads/writes across KSA's double-precision frames. A frame-math change is the archetypal **silent**
drift (compiles, wrong numbers) — re-verify in a live flight after any update.

| Frame | Meaning | Used by |
|---|---|---|
| **CCI** | Celestial-Centered Inertial | positions/velocities, attitude `Body2Cci`, teleport state vectors |
| **CCE / CCF** | Celestial-Centered (Earth-)Fixed / body-fixed | lat/lon (`GetLlaFromCcf`), `GetCci2Ccf`, FC custom-attitude `GetCce2Cci` |
| **ECL** | Ecliptic | body positions/velocities, vessel `position/ecl` |
| **body** | vehicle local | `Body2Cci` converts to CCI |

Frame conversion members touched: `IParentBody.GetCci2Ccf`/`GetLlaFromCcf`, `Vehicle.GetBody2Cci`,
`Orbit.Parent.GetCce2Cci`, `VehicleReferenceFrameEx.{GetEclBody2Cci,QuaternionToEulerAngles}`. Full
treatment: [`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md).

**Numerics (Brutal).** All vector/quat math uses `Brutal.Numerics` (`double3`, `doubleQuat`, `float3`,
`int3`) from the `Brutal.Core.Numerics.dll` family — a **separate assembly set** from `KSA.dll`. rev 4729
("Update KSA to use the latest Brutal packages") bumped these; gatOS compiled clean against the new
Brutal, so no numerics API moved. **Risk: Medium** — a Brutal change would break broadly across
`Game/Ksa/**` (numerics are everywhere), so it is worth scanning the Brutal changelog too, not just KSA.
Known gotcha preserved in code: `VesselReader` uses the *static* `double3.Transform(...)` to dodge the
extension-method overload that would drag `BepuUtilities` into resolution (CS0012).

---

## Reflection accessors (High risk — no compile guard) {#reflection-accessors}

These bind to KSA via reflection, so a rename/removal **cannot** fail the build — it surfaces only at
runtime as a degraded accessor (`/sim/status/accessors`). **Always re-verify these in a live flight after
an update even when the build is green.**

| Accessor | gatOS site | Reflected member | 4750 status |
|---|---|---|---|
| Manual throttle setter | `ThrottleActuator.cs:17,33` | `Vehicle._manualControlInputs` (private field) → `.EngineThrottle` (public field on the struct) | ✅ present (`Vehicle.cs:232`; `ManualControlInputs.EngineThrottle` at `:702,824`) |
| Light template clone | `LightActuator.cs:127` (`EnsureUnshared`/`ShallowClone`) | generic field-by-field clone of `LightModule.Template` + `Intensity`/`ColorRgb`/`OuterAngle`/`InnerAngle` (the per-instance "red-alert" unshare) | ✅ (the read paths are non-reflective and compiled; the clone is type-generic and resilient) |

The throttle field is the single most fragile binding gatOS has (private field, reflection, High). It was
explicitly confirmed present in 4750. If a future update removes it, `ctl/throttle` writes return
`Unsupported` ("manual throttle field not found in this build") and the read-back falls back to
`GetManualThrottle()` (public, still present).

---

## Churn machinery (how the coupling defends itself)

| Mechanism | File | Role |
|---|---|---|
| `[KsaAnchor]` attribute | `Game/Ksa/KsaAnchor.cs` | documentary marker on every KSA-touching member (`Member`, `SourceFile`, `Verified`, `GameVersion`, `Risk`, `Notes`). The grep target when a build breaks; the **source of truth** this whole `scope/` folder mirrors. |
| `KsaHealth` | `Game/Ksa/KsaHealth.cs` | per-accessor degrade latches (game-thread-only dict); first fault logs once, publishes to `/sim/status/accessors`, a later success clears it. The runtime safety net. |
| `KsaCatalog` | `Game/Ksa/KsaCatalog.cs` | the only place actuators are reached: dispatch table, authority gate, per-command try/catch → `KsaHealth`. |
| `VesselReader.Enrich` guard | `Game/Ksa/Readers/VesselReader.cs:40` | whole detail-pass try/catch: an extension-API drift drops the extension dirs, keeps core telemetry. |
| Build against new DLLs | `KSAFolder` resolution | the **compile-time alarm**: non-reflective renames/removals become errors at the anchor sites. |

Two-layer defense: **build = catches structural breaks; health latch = catches runtime drift.** Neither
catches *semantic* drift (units/frames) — that needs the decomp diff (playbook step 3) + live validation.

---

## Mod-ecosystem ABIs (NOT KSA game state) {#mod-ecosystem-abis}

These are couplings to the **mod ecosystem**, separate from KSA-game churn — they change on their own
schedules and a KSA update does not touch them. Listed so the coupling census is complete.

| ABI | gatOS site | Pin / source | Notes |
|---|---|---|---|
| **purrTTY contract** | `Mod.cs` (`CustomShellRegistry.Instance.RegisterShell`), `gatOS.Ssh/SshShellSession : ICustomShell` | `vendor/purrTTY/` (committed, pinned) | The inter-mod ABI for the terminal. Shared over the StarMap ALC at runtime (D6). A contract change is its own refresh (`vendor/purrTTY/README.md`), independent of KSA. |
| **StarMap loader** | `Mod.cs` `[StarMap*]` attributes; `CustomShellRegistry` resolution | `StarMap.API` (loader-supplied) | Lifecycle attributes only; the loader resolves dependency-mod ALCs. |
| **ModMenu** | `Game/Mod.Game.cs` `[ModMenuEntry("gatOS")]` | `ModMenu.Attributes` (optional companion mod) | Optional — the Harmony menu fallback covers its absence. |
| **ImGui** | `Game/Mod.Game.cs` status window | `Brutal.ImGuiApi` | UI only; render-thread, reads volatile state. |

See [`non-ksa-surface.md`](non-ksa-surface.md) for the rest of the game-free surface, and
[`ksa-assets-and-versions.md`](ksa-assets-and-versions.md) for the KSA assets these couplings resolve
against.
