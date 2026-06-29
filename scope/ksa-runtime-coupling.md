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
| `[StarMapAfterGui]` | `OnAfterUi(dt)` | `DriveWelds(dt)` (the welds per-frame `Teleport` — a **game-thread mutation**, runs first and independently of the UI, self-gated to a no-op when no welds exist) then `DrawGameUi()` (ImGui status window) | weld `Teleport` + ImGui |
| `[StarMapUnload]` | `Unload` | `TeardownGameCheats` (clear welds + restore/unpatch IVA), remove hooks, stop serial, dispose broker/servers (bounded) | weld/IVA teardown + uninstall |

The game-coupled hook bodies live in the partial `Game/Mod.Game.cs` and are `[MethodImpl(NoInlining)]`
partial methods, so a missing KSA assembly fails at the *call site* (caught) rather than JIT of the
caller — the whole solution still builds without the game DLLs (CLAUDE.md dependency rule).

---

## Harmony patches (the two KSA hook targets) {#threading-phases}

gatOS installs **two permanent** Harmony patches, both in `Game/Mod.Game.cs`, both via
`AccessTools.Method(...)` with a null-check and try/catch so a missing/renamed target **disables that one
feature with a logged warning instead of crashing** (two further dynamic instances — `gatos.iva` for the
IVA cheat and `gatos.thug_life` for the world-space quad cheat — are each installed only while their
feature is active; see below):

| Patch | KSA target | Decomp file | Purpose | If target moves |
|---|---|---|---|---|
| Solver-drain **prefix** (`Priority.First`) | `Universe.ExecuteNextVehicleSolvers` | `KSA/Universe.cs` | drains `CommandPhase.Solver` commands inside the vehicle-solver step (`Mod.DrainSolverCommands`) | solver-phase commands (attitude/frame/target, burn, refills) never drain; logged once |
| Menu **postfix** | `Program.DrawProgramMenusHook` | `KSA/Program.cs` | draws the fallback top-level "gatOS" menu when the ModMenu mod is absent; also touches `Program.MainViewport.MenuBarInUse`, `ModLibrary.Find("ModMenu")` | gatOS menu only reachable via ModMenu; logged once |

**Risk: Medium.** Neither target appears in the 4680→4750 changelog and `Game/Mod.Game.cs` compiled
clean against 4750. These are the non-`/sim` KSA members most worth re-checking on any update (a rename
won't fail the build for the *patched method name string* if it's via `nameof` — it is, so a rename
**would** fail the build here; a signature change to `ExecuteNextVehicleSolvers` could silently change
when the prefix fires).

### Dynamic IVA patches (`gatos.iva`) {#iva-patches}

The `debug/always_render_iva` cheat (`Game/Ksa/Render/IvaForceRender.cs`, ported from `unscience`)
installs **two more** Harmony patches on its **own** `Harmony("gatos.iva")` instance — a postfix on
`PartModel..ctor(PartModelModule.Template)` and an editor-only postfix on
`PartModel.AddInstance(PerInstanceData,Viewport,int)` — but **only while the toggle is on**: enabling
bulk-flips `PartModelModule.Template.Internal=false` over `PartModel.Instances` (tracking each) and
installs the patches; disabling restores the tracked templates and `UnpatchAll("gatos.iva")`. So the
default-off state carries **zero** IVA patches. The patch targets are `[KsaAnchor]`-documented in
`IvaForceRender` (Risk Medium; verified `2026-06-28` / `2026.6.9.4750`); a ctor/`AddInstance` signature
change surfaces at install time (caught, logged). (Un)patching runs on the game thread (the command
drain / unload). Torn down by `Mod.TeardownGameCheats`.

### Welds per-frame driver (no patch) {#welds-driver}

The **welds** cheat (`Game/Ksa/Welds/`, ported from `unscience`) needs **no** Harmony patch.
`WeldManager.Update(dt)` runs from `OnAfterUi` (`Mod.DriveWelds`, `[StarMapAfterGui]`) — the game thread,
after the per-frame vehicle-solver workers; it calls `JobSystems.VehicleSolvers.Wait()` first (anchored,
`WeldManager.cs`) to ensure those workers have finished, then teleports each welded source onto its anchor
(`WeldEngine.UpdateWeld` → `Vehicle.Teleport`, stamped with `Universe.GetJobSimStep(…).NextTime`). This is
the **third game-thread mutation site** (beside the Frame-phase drain in `OnBeforeUi` and the Solver-phase
prefix on `Universe.ExecuteNextVehicleSolvers`); it **self-gates to a no-op when no welds exist**
(`WeldManager.IsEmpty`), so it costs nothing when unused and never touches game state unprompted. A driver
fault disables welds for the session (`_weldsDead` latch, one error log). The weld *control* writes and the
IVA toggle are ordinary Frame-phase commands; see [`ksa-write-surface.md#welds`](ksa-write-surface.md#welds).

### Dynamic thug_life render patch (`gatos.thug_life`) — render-thread draw injection {#thug-life-patch}

The `thug_life` cheat (`Game/Ksa/ThugLife/`, ported from `unscience`) is gatOS's **first custom GPU
rendering** and its **highest-churn KSA coupling** (render-pipeline internals churn far faster than the
gameplay APIs). It draws a flat, world-space textured quad (the "thug life" sunglasses meme) anchored to a
part, tracked each frame. `ThugLifeRenderPatches.Apply` installs a **dynamic Harmony postfix on
`SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`** (`KSA/SuperMeshRenderSystem.cs:329`) on its **own**
`Harmony("gatos.thug_life")` instance — the only injection point for a world-space draw. It is installed
**lazily on the first entry** (along with the Vulkan pipeline/texture/buffers — `ThugLifeQuadRenderer` +
`ThugLifeTextureFactory`, via `Program.GetRenderer()`) and **removed with the last entry / at unload**, so
the default-off state carries **zero** patches and **zero** GPU resources. The patch targets + the GPU
build are `[KsaAnchor]`-documented in `ThugLifeRenderPatches`/`ThugLifeQuadRenderer`/`ThugLifeTextureFactory`
(Risk **High**; verified `2026-06-28` / `2026.6.9.4750`); a `RenderMainPass`/pipeline signature change
surfaces at install time (caught, logged, feature self-disables).

KSA runs `SuperMeshRenderSystem.RenderMainPass` on the **main thread** (the same thread as the GUI hooks
and the command drain — per the ksa skill `quad.md`), so the render postfix, the command drain, and entry
edits are **all one thread** — no cross-thread game-state access. The manager publishes an immutable
`ThugLifeEntry[]` (swapped on add/remove) that the postfix reads, and **self-disables (`Active=false`) on
any GPU fault**. This is also a **fourth game-thread work site**: `UpdateThugLife()` runs from `OnBeforeUi`
(`[StarMapBeforeGui]`, game thread) to revalidate / re-resolve each entry's anchor part per frame (a staged
anchor part falls back to the vehicle body frame rather than dropping). Dispose order on teardown
(`Mod.TeardownGameCheats`, `_thugLife?.Clear()`): clear `Active` → unpatch → dispose GPU (safe because
same-thread). The per-frame anchor math and GPU surface are **runtime coupling**, not write commands; the
seven `debug.thug_life_*` control writes are ordinary Frame-phase commands — see
[`ksa-write-surface.md#thug-life`](ksa-write-surface.md#thug-life). Welds, IVA, and thug_life are all
**runtime-only** (never persisted).

### Threading phases (binding)
- **Frame phase** — `OnBeforeUi` → `CommandQueue.Drain(CommandPhase.Frame, …)`. Most actions (incl. the
  weld create/remove/enable/clear and `always_render_iva` toggle).
- **Solver phase** — Harmony prefix above → `CommandQueue.Drain(CommandPhase.Solver, …)`. The set is
  `SimCommand.SolverActions = { vessel.attitude_mode, vessel.attitude_frame, vessel.attitude_target,
  vessel.burn, debug.refill_fuel, debug.refill_battery }`. Phase is **derived from the action key**
  (`SimCommand.Phase`), the single source of truth — never passed at a construction site, so every
  transport routes identically. Rationale (FlightComputer `CopyFrom` snapshot/restore) is in
  [`ksa-write-surface.md`](ksa-write-surface.md#vessel-control-surface-g4).
- **Weld driver** — *not* a `CommandQueue` phase: a separate per-frame `Vehicle.Teleport` in `OnAfterUi`
  (the third mutation site, [`#welds-driver`](#welds-driver) above).
- **thug_life draw + validation** — *not* a `CommandQueue` phase: a per-frame draw recorded in the
  `gatos.thug_life` render postfix on `SuperMeshRenderSystem.RenderMainPass`, plus a per-frame
  `UpdateThugLife()` anchor-revalidation in `OnBeforeUi` (the fourth game-thread work site,
  [`#thug-life-patch`](#thug-life-patch) above). All on the main thread.

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
