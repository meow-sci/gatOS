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
| `[StarMapBeforeGui]` | `OnBeforeUi(dt)` | sets the "ran this frame" latch, then `DrivePerFrame(dt)` = `SampleTelemetry` → `TickSchedules` → `DrainCommands` (**Frame phase**) → `DriveAudio` → `UpdateThugLife` | reads + Frame writes |
| `[StarMapAfterGui]` | `OnAfterUi(dt)` | `DrivePostSolver(dt)` = `DriveWelds(dt)` → `DriveIvaPhysics(dt)` (both **game-thread mutations**, run first and independently of the UI, each self-gated to a no-op when its registry is empty) then `DrawGameUi()` (ImGui status window) | weld `Teleport` + IVA SubPart poses + ImGui |
| `[StarMapAfterOnFrame]` | `OnAfterFrame(t, dtPlayer)` | **the F2 fallback hook (C0.1)** — a postfix on `Program.OnFrame`: when GUI hooks were skipped it completes the per-frame non-camera drivers. The main-viewport prefix has already sampled/ticked/drained when available, so the latch prevents a double step | F2 fallback only; no camera pose write |
| Harmony prefix/postfix: `Viewport.OnFrame(double)` | `CameraViewportPatch` | Bound strictly by `ReferenceEquals(viewport, Program.MainViewport)`. Prefix samples, advances schedules, drains frame commands and applies the camera after simulation advance; original method runs controller + `Camera.OnFrame`; postfix publishes the final clamped transform | same-render camera ownership + applied read-back |
| `[StarMapUnload]` | `Unload` | `TeardownGameCheats` (clear welds, restore/unpatch IVA + thug_life + always_render, FX pristine restore, audio shutdown, `ScheduleStore.Clear()`, `CameraDirector.Shutdown()`), remove hooks, stop serial, dispose broker/servers (bounded) | cheat/camera/schedule teardown + uninstall |

The game-coupled hook bodies live in the partial `Game/Mod.Game.cs` and are `[MethodImpl(NoInlining)]`
partial methods, so a missing KSA assembly fails at the *call site* (caught) rather than JIT of the
caller — the whole solution still builds without the game DLLs (AGENTS.md dependency rule).

### The `DrawUI` / F2 hook change (C0.1) — the third StarMap hook {#f2-hook}

**Both GUI hooks are F2-gated, and that used to stop gatOS dead.** StarMap implements
`[StarMapBeforeGui]` as a prefix on `Program.OnDrawUiFrame` and `[StarMapAfterGui]` as a postfix on
`Program.OnDrawUiViewports`; the **only** call site of each sits inside `Program.OnFrame`'s
`if (DrawUI)` block, and **F2 toggles `DrawUI`**. So with the UI hidden the telemetry sampler, the
command drain, the audio tick, the thug-life anchor updater, the welds driver and the IVA physics
driver all simply stopped running — silently, for as long as the player kept the UI off. That is
unacceptable for a mod whose whole point is a camera and a scriptable sim.

**The fix adds a third StarMap hook and splits the two bodies.** `[StarMapAfterOnFrame]
Mod.OnAfterFrame` is a postfix on `Program.OnFrame` itself — outside the `if (DrawUI)` block, so it
runs unconditionally, exactly once per rendered frame. The GUI hooks' work was factored into
`Mod.DrivePerFrame` (sample → schedules → drain → audio → thug_life) and `Mod.DrivePostSolver`
(welds → IVA physics), and `OnAfterFrame` re-runs **both, in their original order, only when the GUI
hooks did not** — decided by a plain boolean latch that `OnBeforeUi` sets and `OnAfterFrame` clears.
`DrawGameUi()` is deliberately **not** re-run: with `DrawUI` false there is no ImGui frame to draw
into. `DriveCamera` then runs unconditionally, after both (see [#camera-driver](#camera-driver)).

Two implementation notes a future reader will otherwise re-derive: the latch is used **instead of a
frame-number comparison** because this half of the partial class must still compile with no KSA
assemblies present (`Program.FrameNumber` is unreferenceable there) *and* because `FrameNumber` is
incremented before the postfix fires, so an equality test would not suppress the duplicate run anyway.
And with the UI visible `OnAfterFrame` does nothing but clear the latch and drive the camera — the
normal path is unchanged, which is why this carries no measurable cost.

**Break impact.** `[StarMapAfterOnFrame]` is a **StarMap loader ABI**, not KSA game state — a KSA
update cannot move it; a StarMap update that dropped the attribute would fail the build. The hazard is
the *semantic* one: if StarMap ever moved `[StarMapBeforeGui]`/`[StarMapAfterGui]` out of the
`if (DrawUI)` block, the latch would keep everything correct (the drives simply never stand in), so the
change is safe in both directions.

---

## Harmony patches (the three permanent KSA hook targets) {#threading-phases}

gatOS installs **three permanent** Harmony patches, all via `AccessTools.Method(...)` with a null-check
and try/catch (or a degrade-to-no-injection transpiler) so a missing/renamed target **disables that one
feature with a logged warning instead of crashing** (three further dynamic instances — `gatos.iva` for
the IVA cheat, `gatos.thug_life` for the world-space quad cheat, and `gatos.always_render` for the
per-vessel render-distance override — are each installed only while their feature is active; see below):

| Patch | KSA target | Decomp file | Purpose | If target moves |
|---|---|---|---|---|
| Solver-drain **prefix** (`Priority.First`) | `Universe.ExecuteNextVehicleSolvers` | `KSA/Universe.cs` | drains `CommandPhase.Solver` commands inside the vehicle-solver step (`Mod.DrainSolverCommands`) | solver-phase commands (attitude/frame/target, burn, refills) never drain; logged once |
| Menu **postfix** | `Program.DrawProgramMenusHook` | `KSA/Program.cs` | draws the fallback top-level "gatOS" menu when the ModMenu mod is absent; also touches `Program.MainViewport.MenuBarInUse`, `ModLibrary.Find("ModMenu")` | gatOS menu only reachable via ModMenu; logged once |
| Screen-stream capture **transpiler** (`DisplayRenderPatch`) | `Program.RenderGame` — inserts a call before the frame's final 1-arg `Brutal.VulkanApi` `End()` | `KSA/Program.cs` | records the `/sim/display` capture into the engine's own frame command buffer (see [the capture section](#display-capture)) | no `End()` site found → **no injection** (stream dark, warning logged); the method is never corrupted |

**Risk: Medium.** Neither target appears in the 4680→4750 changelog and `Game/Mod.Game.cs` compiled
clean against 4750. These are the non-`/sim` KSA members most worth re-checking on any update (a rename
won't fail the build for the *patched method name string* if it's via `nameof` — it is, so a rename
**would** fail the build here; a signature change to `ExecuteNextVehicleSolvers` could silently change
when the prefix fires). Re-verified 2026-07-03 against `2026.7.3.4826`:
`Universe.ExecuteNextVehicleSolvers(double, SimStep)` at `Universe.cs:1660` and
`Program.DrawProgramMenusHook()` at `Program.cs:3379` — signatures **and bodies** unchanged;
`Program.RenderGame`'s two-`End()` structure is identical (shifted ~12 lines, absorbed by the
pattern-matching transpiler); the solver's FlightComputer snapshot/restore window
(`VehicleUpdateState`/`VehicleUpdateTask` prepare/apply) is preserved, so the Solver phase stays valid.
Re-verified 2026-07-14 against `2026.7.5.4892`: `Universe.cs` untouched by the 4826→4892 diff
(`ExecuteNextVehicleSolvers` still `Universe.cs:1660`); `Program.DrawProgramMenusHook()` at
`Program.cs:3417`, same shape (its interior swaps the `Staging` window class for `ResourceGroups` —
irrelevant to the postfix); `Program.RenderGame(AcquiredFrame, double)` at `Program.cs:3965` gained an
interior underwater-render call, but the transpiler anchors on the method's **final** 1-arg
`Brutal.VulkanApi` `End()` — injection site unaffected; the FC snapshot/restore window moved to
`VehicleUpdateData.Prepare` (`NewFlightComputer.CopyFrom(flightComputer)`, `VehicleUpdateData.cs:87`) +
apply at `Vehicle.cs:1991` (`FlightComputer.CopyFrom(updateData.NewFlightComputer)`) — same discipline,
Solver phase stays valid. Re-verified 2026-07-16 against `2026.7.6.4939`: the whole `Universe.cs` diff
is log-line renumbering (`ExecuteNextVehicleSolvers(double, SimStep)` still `Universe.cs:1660`);
`Program.DrawProgramMenusHook()` at `Program.cs:3453`, same shape; `Program.RenderGame` gained interior
volumetric-plume-trail + `GizmosRenderer` calls but its **tail is byte-identical** (final
`commandBuffer2.End()` + preceding transitions), so the final-`End()` injection site and its
image-layout assumption both hold; `FlightComputer.cs`/`VehicleUpdateData.cs` untouched (the
`VehicleUpdateTask.cs` changes are additive off-rails-while-animating + tank transfers) — Solver phase
stays valid. Re-verified 2026-07-22 against `2026.7.8.4980`: `ExecuteNextVehicleSolvers(double, SimStep)`
byte-identical at `Universe.cs:1660`; `Program.DrawProgramMenusHook()` moved to `Program.cs:3635`, same
no-arg instance shape; `Program.MainViewport` (`:433`) and `ModLibrary.Find(string)` (`:173`) unchanged;
`FlightComputer.CopyFrom` gained one line — it now also copies the new `RCSMode` field (`:128`) — so the
snapshot/restore window still captures everything gatOS mutates, Solver phase stays valid.
Re-verified 2026-07-24 against `2026.7.9.5018`: `Universe.cs` is **byte-identical**
(`ExecuteNextVehicleSolvers(double, SimStep)` still `:1660`), as is `FlightComputer.cs`;
`Program.DrawProgramMenusHook()` moved to `Program.cs:3690` (same no-arg instance shape),
`Program.MainViewport` to `:439`, `ModLibrary.Find(string)` to `:175` — all resolve by name via
`AccessTools`, so the line moves are cosmetic. **`Program.RenderGame`'s body is byte-identical**
(`Program.cs:4238`), so the `/sim/display` transpiler's final-`End()` injection site and its
image-layout assumption are untouched. `VehicleUpdateData.cs`/`VehicleUpdateTask.cs` changed only to
carry `ISubstanceStore[] SubstanceStores` alongside tanks (rev 4992, solid motors) — nothing gatOS
binds.

**Re-verified 2026-08-05 against `2026.8.5.5168`** — this is the pass where `Program.RenderGame`
finally *did* change, so the assumptions above were re-derived rather than re-asserted. Rev 5154 moved
offscreen rendering onto Vulkan **dynamic rendering**, rewriting the body (275 → 233 lines: the
`BeginRenderPass`/`EndRenderPass` pair around the scene became
`_offscreenTarget.BeginRendering`/`EndRendering`, and the explicit `TransitionImages2` calls became
`BarrierBatch`). Both transpiler preconditions nevertheless **still hold**:

- **The injection anchor survives.** The transpiler deliberately matches the *last* 1-arg
  `Brutal.VulkanApi` `End()` rather than a fixed offset; `commandBuffer2.End()` is still the final call
  (`Program.cs:4320`), still after `EndRenderPass()` — i.e. outside any render pass.
- **The layout assumption survives.** `commandBuffer2.PipelineBarrier2(_offscreenTarget.ColorImage,
  ImageBarrierInfo.Presets.SampledReadVfc)` (`:4312`) still leaves the offscreen colour image in
  `ShaderReadOnlyOptimal` at that point, and **the new CMAA2 pass (rev 5156) does not disturb it** —
  `Cmaa2Renderer` renders into its own `_sceneLdrTarget` and only *samples* the offscreen image
  (`Cmaa2Renderer.cs:166,319,324`), so the state is identical whether AA is off, MSAA, or CMAA2.
- **The captured image is still the right one.** `Program.MainViewport.OffscreenTarget` is assigned
  directly from `Program._offscreenTarget` (`Program.cs:1385`), so `FrameCapture`'s per-viewport read
  and the global barrier above refer to the same object — as before.

The one required change was nullability: `KSA.Rendering.RenderTarget.ColorImage` is `RenderImage?`
(a depth-only target has no colour), so `FrameCapture` now returns early instead of dereferencing it.
`Universe.ExecuteNextVehicleSolvers`, `Program.DrawProgramMenusHook`, `Program.MainViewport` and
`ModLibrary.Find` are all unchanged in shape and still resolve by name.

**Re-verified 2026-08-11 against `2026.8.19.5261`** (revs 5169–5258, 90 commits). **Every Harmony hook
target is signature-identical**, despite revs 5208–5216 rewriting the whole vehicle-update threading
model: `Universe.ExecuteNextVehicleSolvers(double, SimStep)` (`Universe.cs:1796`),
`Program.DrawProgramMenusHook()` (`Program.cs:3669`), `Program.RenderGame(AcquiredFrame, double)`
(`:4206`), `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)` (`:332`),
`Vehicle.GetWorldMatrix(Camera)` (`:3516`), `Vehicle.UpdateRenderData(Viewport,int)` (`:3529`),
`Viewport.OnFrame(double)` (`:141`), `PartModel.AddInstance(PerInstanceData,Viewport,int)` (`:376`),
`Program.MainViewport` (`:458`), `ModLibrary.Find(string)` (`:177`).

- **Both `/sim/display` transpiler preconditions still hold.** `commandBuffer2.End()` is still the
  final 1-arg `Brutal.VulkanApi` `End()` in `RenderGame`, and
  `PipelineBarrier2(_offscreenTarget.ColorImage, SampledReadVfc)` still sits 8 lines above it — the
  same shape as 5168, so both the injection anchor and the `ShaderReadOnlyOptimal` layout assumption
  are intact. `MainViewport.OffscreenTarget = _offscreenTarget` moved to `Program.cs:1442`, so
  `FrameCapture` still reads the object the barrier refers to.
- **`SuperMeshRenderSystem.cs` changed for the first time since 5018** — but neither change reaches the
  `thug_life` postfix. Rev 5241 added `Program.SetViewport(commandBuffer)` to the head of four render
  methods (including `RenderMainPass`, `:359`) to fix a stale viewport in supersampled screenshots;
  **gatOS already calls `Program.SetViewport(cmd)` itself** before drawing the quad
  (`ThugLifeQuadRenderer.cs:271`), so this is KSA converging on what the quad already did. Rev 5236
  flipped the private `DescriptorDynamicOffset` from `ByteSize.Zero` to `null` (a Vulkan-validation
  fix) — an internal member of the system's own context struct that gatOS does not touch. The
  postfix still runs inside `OffscreenTarget.BeginRendering(…)`/`EndRendering(…)`
  (`Program.cs:4308/4350/4357`), and the `UnlitMeshVert`/`UnlitMeshFrag` keys,
  `RenderingPresets.ReverseZDepthStencil.DepthTestWrite` and
  `RenderTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo)` are all unchanged.
- **The solver-drain rename is mechanical, the lifecycle underneath is not.**
  `JobSystems.VehicleSolvers` (a many-runner `JobScheduler`) became `JobSystems.VehicleSolver` (a
  single orchestrator) plus a new `DynamicWorkerPool VehicleWorkerPool`; `VehicleUpdateTask` is now the
  overall orchestrator with `PhysicsBubble`s as parallel islands (rev 5215). `VehicleSolver.Wait()` is
  still the correct and sufficient drain for `WeldManager.Update`/`IvaPhysicsManager.Update` — the pool
  is joined inside the task by a `using`-scoped `ParallelBatch`, and KSA's own `PrepareFrame` drains
  exactly this way. ⚠️ **Live-only risk:** `Vehicle.Teleport` now detaches via the **object-pooled**
  `PhysicsBubble` (`RemoveFromBubble`; pooling added rev 5220, stale-handle crash fixed rev 5237), so
  the per-frame weld teleport drives a pooled bubble split/merge every tick.
- **`Universe.GetElapsedSimTime()` was removed** (rev 5211) — the schedule tick, audio event stamps,
  `KsaCatalog`, `FxReflect` and the sampler now read `Universe.GetElapsedSeconds()`; `WeldEngine` still
  stamps with `Universe.GetJobSimStep(dt).NextTime`, whose body is unchanged apart from the
  `SimTime`→`UniverseTime` type, so the next-tick rationale holds verbatim.
- **Frames and numerics unchanged**: the whole `Brutal.Numerics` decomp tree is byte-identical across
  the two builds.

### Dynamic IVA patches (`gatos.iva`) {#iva-patches}

The `debug/always_render_iva` cheat (`Game/Ksa/Render/IvaForceRender.cs`, ported from `unscience`)
installs **two more** Harmony patches on its **own** `Harmony("gatos.iva")` instance — a postfix on
`PartModel..ctor(PartModelModule.Template)` and an editor-only postfix on
`PartModel.AddInstance(PerInstanceData,Viewport,int)` — but **only while the toggle is on**: enabling
bulk-flips `PartModelModule.Template.Internal=false` over `PartModel.Instances` (tracking each) and
installs the patches; disabling restores the tracked templates and `UnpatchAll("gatos.iva")`. So the
default-off state carries **zero** IVA patches. The patch targets are `[KsaAnchor]`-documented in
`IvaForceRender` (Risk Medium; verified `2026-06-28` / `2026.6.9.4750`; re-verified 2026-07-03 against
`2026.7.3.4826` — the render gate at `PartModel.cs:387` and all patched members are unchanged; the only
`PerInstanceData` change is a struct pad → `Wetness` float, passed through opaquely; re-verified
2026-07-14 against `2026.7.5.4892` — `PartModel.cs`/`Viewport.cs` untouched; the
`PartModelModule`/`PartModelDynamicModule` diff only adds selection-highlight flag bits, `Template`
untouched; re-verified 2026-07-16 against `2026.7.6.4939` — `PartModel.cs` again untouched, the
`PartModelModule`/`PartModelDynamicModule` diff is one added fuel-flow highlight bit; re-verified
2026-07-22 against `2026.7.8.4980` — neither `PartModel.cs` nor `PartModelModule.cs` is in the changed
set; re-verified 2026-07-24 against `2026.7.9.5018` — `PartModel.cs`, `PartModelModule.cs`,
`PartModelDynamicModule.cs` and `Viewport.cs` are all byte-identical again); a ctor/`AddInstance`
signature change surfaces at install time (caught, logged). (Un)patching runs on the game thread (the command
drain / unload). Torn down by `Mod.TeardownGameCheats`.

### Dynamic vessel force-render patches (`gatos.always_render`) {#always-render-patches}

The per-vessel `vessels/by-id/<id>/always_render` node (`Game/Ksa/Render/VesselForceRender.cs`, ported
from `unscience` i-feel-seen) bypasses KSA's sub-pixel cull — a vehicle whose projected diameter falls
under one pixel (`Camera.GetObjectDiameterPixelsAsDouble < 1.0`) is normally not drawn. It installs
**two more** Harmony patches on its **own** `Harmony("gatos.always_render")` instance — prefixes on
`Vehicle.GetWorldMatrix(Camera)` and `Vehicle.UpdateRenderData(Viewport,int)`, each reproducing the
stock body minus the cull check — but **only while ≥ 1 vessel is marked**: the first mark installs, the
last unmark (write `0`, despawn prune, or unload) removes, so the default-off state carries **zero**
patches. The registry is mutated only on the game thread (Frame drain / sampler prune / unload) and the
prefixes read one volatile immutable id set, safe on the render-prep path at two hash lookups per
vehicle per frame. Marks key on the vessel **id** (survive scene rebuilds); despawned ids are pruned by
`VesselForceRender.Prune`, riding the sampler's vehicle enumeration. The patch targets + reproduced
members are `[KsaAnchor]`-documented in `VesselForceRender` (Risk Medium; verified `2026-07-02` /
`2026.6.9.4750`; re-verified 2026-07-03 against `2026.7.3.4826` — the stock `GetWorldMatrix`/
`UpdateRenderData` bodies are **byte-identical** to gatOS's reproductions, only shifted ~72 lines by
unrelated Vehicle.cs additions; re-verified 2026-07-14 against `2026.7.5.4892` — both stock bodies again
untouched by the diff, only line-shifted, so the reproductions remain byte-accurate; re-verified
2026-07-16 against `2026.7.6.4939` — the `Vehicle.cs` diff (staging-key `ControlsLockout`, plume-trail
emitters, fuel-transfer statics, collider flags) leaves both stock bodies untouched; re-verified
2026-07-22 against `2026.7.8.4980` — the `Vehicle.cs` diff (Control name stamps, fallback mass,
collision-avoidance pairs, navball markers) again leaves `GetWorldMatrix`/`UpdateRenderData` untouched;
re-verified 2026-07-24 against `2026.7.9.5018` — the `Vehicle.cs` diff (`Parts.Tanks` →
`Parts.SubstanceStores` in mass recompute, the `IFlowManagerHost` UI loop, plume-trail LOD and the
`AverageThrottle` → `AverageThrustFraction` FX rename) once more leaves both stock bodies untouched);
a missing target
throws at install time (caught by `KsaCatalog` → the
actuator latches degraded, EOPNOTSUPP), and a prefix fault logs once and falls back to the stock cull.
`UpdateRenderData` is **virtual** — the patch binds `Vehicle`'s implementation, so overrides (KittenEva
renders via its own `KittenRenderable`) are **not** force-rendered, same as the unscience original.
Torn down by `Mod.TeardownGameCheats`.

### Welds per-frame driver (no patch) {#welds-driver}

The **welds** cheat (`Game/Ksa/Welds/`, ported from `unscience`) needs **no** Harmony patch.
`WeldManager.Update(dt)` runs from `OnAfterUi` (`Mod.DriveWelds`, `[StarMapAfterGui]`) — the game thread,
after the per-frame vehicle-solver workers; it calls `JobSystems.VehicleSolver.Wait()` first (anchored,
`WeldManager.cs`) to ensure those workers have finished, then teleports each welded source onto its anchor
(`WeldEngine.UpdateWeld` → `Vehicle.Teleport`, stamped with `Universe.GetJobSimStep(…).NextTime`). This is
the **third game-thread mutation site** (beside the Frame-phase drain in `OnBeforeUi` and the Solver-phase
prefix on `Universe.ExecuteNextVehicleSolvers`); it **self-gates to a no-op when no welds exist**
(`WeldManager.IsEmpty`), so it costs nothing when unused and never touches game state unprompted. Each tick
the driver re-resolves the anchor by `InstanceId` over `Vehicle.Parts.Parts` **and** each part's
`Part.SubParts` (`WeldManager.FindPart`; 2026-07-16 — subpart anchors supported, and an animated subpart
anchor tracks because the re-resolved `Part`'s pose properties compose through `PartParent`); a vanished
anchor falls back to body-frame anchoring rather than dropping the weld. A driver
fault disables welds for the session (`_weldsDead` latch, one error log). The weld *control* writes and the
IVA toggle are ordinary Frame-phase commands; see [`ksa-write-surface.md#welds`](ksa-write-surface.md#welds).

### IVA cabin physics per-frame driver (no patch) {#iva-cabin-sim}

The **IVA cabin-physics** feature (`Game/Ksa/Iva/`, plans/IVA_MOVEMENTS.md) also needs **no** Harmony
patch. `IvaPhysicsManager.Update(dt)` runs from `OnAfterUi` (`Mod.DriveIvaPhysics`, `[StarMapAfterGui]`) —
the game thread, after the per-frame vehicle-solver workers; like the welds driver it calls
`JobSystems.VehicleSolver.Wait()` first (anchored) so the accelerometer/rates/CoM readings it feeds into
the forcing field are the settled values for the step, then writes each floating object's pose onto its
driven **SubPart** (`Part.PositionParentAsmb` / `Part.Asmb2ParentAsmb`). This is the **sixth game-thread
work site**. It **self-gates to a single branch** when the master switch is off or nothing is adopted
(`IvaPhysicsManager.IsIdle`), so it costs nothing when unused and never touches game state unprompted.

**gatOS runs its own physics world, and that is a deliberate isolation decision — not an implementation
convenience.** The simulation is a gatOS-owned `BepuPhysics.Simulation` (against KSA's own embedded
BepuPhysics 2.5, already loaded in-process) with its own `BufferPool` and `Shapes` — deliberately **never**
`ConstraintSim.GlobalShapes`, which the game's solver threads share — running in the vessel **assembly
frame**. Three facts in the decompiled sources rule out adding bodies to KSA's `ConstraintSim`:

1. **A cabin body would be ejected *and* would shove the spacecraft.** KSA represents each vehicle as one
   dynamic body whose shape is a `BigCompound` of a few coarse convex primitives approximating the
   *outside* of the whole vehicle (the Gemini-class pod's entire collision representation is a 2 m cylinder
   plus a 0.89 m sphere; the IVA interior part declares **no** `<Collider>` at all). Anything placed where a
   crew member sits is deep inside that collider, so penetration recovery ejects it through the hull — and
   because `NarrowPhaseCallbacks.AllowContactGeneration` permits dynamic↔dynamic pairs unconditionally and
   the vehicle body *is* dynamic, the contact constraint pushes the real spacecraft. Suppressing that pair
   would mean patching a method on a `readonly struct` passed as a generic type argument into Bepu's
   aggressively-inlined hot path — a Harmony patch there is unlikely to take and impossible to rely on.
2. **It is not stepped when we need it.** `VehicleUpdateTask` branches on `ConstraintSim.IsAnyConstrained`;
   a lone vessel in orbit runs `FullPhysicsUnconstrainedStep` and Bepu never ticks — precisely the coasting
   case this feature exists for.
3. **It runs on worker threads.** The solve executes on `JobSystems.VehicleSolver`, so every mutation
   would be a data race against the game's solver, violating threading rule 1.

The gatOS-owned world has none of those problems: it cannot perturb the vessel, cannot corrupt a save,
cannot race the solver, and — because the frame is the assembly frame — the interior is static geometry
that never moves and poses come out in exactly the coordinates `Part.PositionParentAsmb` wants. Coordinates
stay within metres of the origin, so Bepu's float32 solver has precision to spare (unlike the game's own
bubble-relative frame). `Simulation.Timestep` is called with **no `IThreadDispatcher`**, so every Bepu
callback runs single-threaded on the game thread. Coupling is one-way by design (vessel → objects);
plans/IVA_MOVEMENTS.md §7 R4 sketches the opt-in back-reaction path if it is ever wanted.

Break impact is therefore unusually contained: a KSA change to `ConstraintSim`, `NarrowPhaseCallbacks`,
`PoseIntegratorCallbacks`, the collider XML or the bubble/`Phys` frame **cannot** affect this feature.
What *can*: the interior-mesh reads and the `Part` transform driver
([`ksa-read-surface.md#iva-physics`](ksa-read-surface.md#iva-physics),
[`ksa-write-surface.md#iva-physics`](ksa-write-surface.md#iva-physics)), and a KSA build that dropped or
renamed `BepuPhysics.dll`/`BepuUtilities.dll` — guarded by a condition-guarded reference plus an explicit
`VerifyBepuReference` build error, with a hand-rolled sphere/capsule-vs-triangle solver documented as the
fallback behind the `CabinSim` seam (plans/IVA_MOVEMENTS.md §3 Option C). A per-vessel driver fault drops
that cabin; a whole-driver fault releases every object and latches the feature off for the session
(`_ivaDead`).

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
(Risk **High**; verified `2026-06-28` / `2026.6.9.4750`; re-verified statically 2026-07-03 against
`2026.7.3.4826` — `RenderMainPass(CommandBuffer)` byte-identical at line 329, `UnlitMeshVert`/`Frag`
shader keys + assets unchanged, `Program.OffScreenPass`/`RenderPassState` unchanged; re-verified
statically 2026-07-14 against `2026.7.5.4892` — `SuperMeshRenderSystem.cs` entirely untouched by the
diff, shaders/keys/`OffScreenPass` unchanged, and the 4861–4889 ground-clutter pipeline overhaul does
not reach the quad's pipeline; re-verified statically 2026-07-16 against `2026.7.6.4939` —
`SuperMeshRenderSystem.cs` again entirely untouched (`RenderMainPass(CommandBuffer)` at `:329`),
`UnlitMesh` keys/assets unchanged (the `DefaultAssets.xml` churn is particle/trail/clutter shader
keys), `OffScreenPass`/`SampleCount` unchanged, and the new screenspace-particle / volumetric-trail /
ground-clutter-culling passes (revs 4894–4932) are mid-frame compute/composite work that does not alter
the main pass the quad draws in (`SimpleVkMeshAtlas`'s bounding-radius fix affects game-mesh culling
only — the quad builds its own buffers); re-verified statically 2026-07-22 against `2026.7.8.4980` —
`RenderMainPass(CommandBuffer)` and its body are **identical** (the `SuperMeshRenderSystem.cs` diff is
shadow-path only: `RenderShadowPass` gained a `cascadeIndex` param, depth pipelines a push-constant, PBR
pipelines a CSM-filter specialization constant — none touch the main color pass), offscreen
`ColorFormat=R16G16B16A16SFloat` / `SampleCount=GameSettings.GetSampleCount()` / reverse-Z / `CreateRenderPass(Clear, Load)`
all unchanged. ⚠ One new transient hazard (rev 4942): the scaled-**screenshot** path
(`GameSettings.SampleCountOverride` + `RebuildRenderer`) forces a 1-sample renderer rebuild for hi-res
captures; the quad pipeline is built once against `OffScreenPass.SampleCount` with **no rebuild
listener**, so a hi-res in-game screenshot taken while quads are active can transiently mismatch the
pipeline's sample count — the GPU-fault latch self-disables the feature rather than crashing; a rebuild
listener is the fix if this coincidence matters. Vulkan render-pass *compatibility* at draw time is only
provable live — check pending in `docs/VALIDATION.md`; re-verified statically 2026-07-24 against
`2026.7.9.5018` — `SuperMeshRenderSystem.cs` is **byte-identical** (`RenderMainPass(CommandBuffer)` at
`:329`), `Program.OffScreenPass` (`:409`) / `OffscreenTarget` (`:415`) / `LinearRepeatSampler` (`:425`)
unchanged, and `UnlitMeshVert`/`UnlitMeshFrag` (`DefaultAssets.xml:66-67` → `Shaders/Mesh/UnlitMesh.{vert,frag}`)
are untouched. The 5018 render churn is elsewhere: the MilkyWay renderer split out of
`InstancedStarTechnique` (rev 4988) **shrank `GlobalShaderBindings`** (descriptor-set layout 11 → 10
bindings, sampler pool 10 → 8) — the quad builds its **own** descriptor-set + pipeline layout, so it is
unaffected; plume-trail LOD (revs 4996–4998, 5013) and ground-clutter shadow work (revs 5008–5016) are
separate passes. The rev-4942 screenshot transient above is unchanged and still open); a
`RenderMainPass`/pipeline signature change surfaces at install time (caught, logged, feature
self-disables).

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

### Screen-stream capture (`DisplayRenderPatch` + `FrameCapture`) — in-band GPU readback {#display-capture}

The `/sim/display` screen stream (STREAM_PLAN.md; `Game/Ksa/{DisplayRenderPatch,FrameCapture}.cs`, both
`[KsaAnchor]`, Risk **Medium**, verified `2026-07-02`; re-verified statically 2026-07-03 against
`2026.7.3.4826` — `RenderGame` present with the same two-`End()` structure, `GetRenderer`/`MainViewport`/
`OffscreenTarget`/`ResourceFrameIndex` all unchanged; re-verified statically 2026-07-14 against
`2026.7.5.4892` — `RenderGame` gained an interior underwater-render call but the final-`End()` injection
site is unaffected, and all touched members above are unchanged; re-verified statically 2026-07-16
against `2026.7.6.4939` — `RenderGame`'s interior gained volumetric-trail + gizmos calls but its tail
(final `End()` + the preceding `SampledReadVfc` transition and composite pass) is **byte-identical**,
and `GetRenderer`/`MainViewport`/`OffscreenTarget`/`ResourceFrameIndex` are all unchanged; re-verified
statically 2026-07-22 against `2026.7.8.4980` — rev 4942's `ScreenshotCapture` is purely **additive** to
`RenderGame`: `OnRenderGameCapture(...)` at `:4441` and `OnRenderGameSwapchainGrab(...)` at `:4446`
insert immediately before the still-unique final 1-arg `End()` (`:4447`), the
`ColorAttachmentWrite → SampledReadVfc` transition at `:4440` is intact, and the screenshot reads
different images (`OffscreenTarget.ColorImage.ImageView` hi-res / swapchain grab) than gatOS's blit —
the transpiler's final-`End()` anchor and image-layout assumption both hold; re-verified statically
2026-07-24 against `2026.7.9.5018` — **`RenderGame`'s entire body is byte-identical** to 4980
(`Program.cs:4238`), so every injection-site and image-layout assumption carries over verbatim, and
`GetRenderer` (`:486`) / `MainViewport` (`:439`) / `OffscreenTarget` (`:415`) / `SetViewport` (`:4080`)
are unchanged) taps the **public** offscreen scene target and
rides the engine's own frame command buffer — no private queue submit, no `WaitIdle` (an out-of-band
variant corrupted the device). Per throttled frame (default 15 fps, gated on `enabled` **and** ≥1 open
reader — near-zero cost otherwise), `FrameCapture.MaybeRecord` records, in-band:
offscreen `SampledReadVfc→TransferSrc` + per-slot scratch `Undefined→TransferDst` (sync2
`TransitionImages2` + `ImageBarrierInfo.Presets` only — no sync1/sync2 mixing), a `BlitImage`
(LINEAR) of the full `R16G16B16A16_SFLOAT` scene into a small `B8G8R8A8_UNORM` scratch (downscale +
float→byte clamp in one GPU op — PERF_IMPROVEMENT_PLAN.md P1), `CopyImageToBuffer`(scratch→host
staging, preferring `HOST_CACHED`), and the offscreen restored to `SampledReadVfc`. Readback is
deferred one slot revisit (frames-in-flight fence contract — no fence wait) and is a bulk span
hand-off to the game-free `DisplaySurface`. Blit support is format-feature-queried once
(`PhysicalDevice.GetFormatProperties`); a miss falls back to the previous full-res copy + CPU
nearest-neighbour convert. **KSA members touched:** `Program.GetRenderer/MainViewport/ResourceFrameIndex`,
`OffscreenTarget.ColorImage/.Extent`, `Renderer.Allocator/.MaxFramesInFlight/.PhysicalDevice`,
`CommandBufferEx.TransitionImages2`+`ImageBarrierInfo.Presets`+`ImageTransition`, allocator
`CreateBuffer`/`CreateImage`, `CommandBuffer.BlitImage`/`CopyImageToBuffer`, `BufferEx.Map` — full row in
[`docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md). Break behavior: the transpiler
degrades to no-injection; a capture-time managed fault latches the feature off for the session
(`DisplayRenderPatch._faulted`, one error log).

### Camera director same-frame viewport driver {#camera-driver}

The **programmable camera** (`Game/Ksa/Camera/`, plans/CAMERA_ASBUILT.md) installs one Harmony
prefix/postfix pair on public `Viewport.OnFrame(double)`. Both bind strictly by
`ReferenceEquals(__instance, Program.MainViewport)` because KSA owns four viewports. The prefix calls
`Mod.PrepareMainViewportFrame(dt)`: sample telemetry, advance shared schedule clocks, post and drain due
commands, then run `CameraDirector.Update(dt)`. It composes `Track ?? Override ?? Baseline`, resolves
current-frame placement/aim, and writes `Camera.PositionEcl`, `Camera.LocalRotation` and projection.
The original method then runs the parked controller and immediately `Camera.OnFrame`, rebuilding every
matrix from that transform; the postfix publishes the final position/rotation after KSA's clamp. gatOS
never touches a matrix or renderer. The obsolete after-render design was one simulation frame behind a
moving ECL target and was removed after the first live validation exposed it.

**Why the parked controller remains important.** `Viewport.OnFrame` (`KSA/Viewport.cs:139`) is
literally `GetActiveController().OnFrame(this, dt); GetCamera().OnFrame(dt);`. The prefix must leave no
game controller that overwrites its transform before the matrix build. That is true of exactly one:
`FixedController.OnFrame` (`KSA/FixedController.cs:18-35`) wraps its **entire body** in
`if (following != null)`, and ownership unfollows. Hence `CameraDirector.Take()` parks
`Viewport.Mode = CameraMode.Fixed` (**by direct field assignment**, so `FixedController.OnSwitchOn`'s
`TimedAlert("Fixed Camera")` never draws in the footage) and calls
`Unfollow(changeControl: false)` — the default `true` would null `Program.ControlledVehicle` and drop
the player's vessel. Every frame the driver **re-asserts** both (`Viewport.Mode != Fixed` or
`Following != null` is undone), because a camera hotkey or another mod could otherwise wake
`FixedController.OnFrame` and leave two writers fighting over one transform. The visible consequence:
**while gatOS owns the camera, the player's camera keys do nothing.**

The driver **self-gates to a single branch** (`CameraDirector.IsIdle`) while gatOS does not own the
camera — the default — and in that state no camera is read, no pose composed and nothing published.
`CameraState` and the director's own fields are **game-thread only, with no locks by design** (adding
one would only hide a rule-1 violation); transport threads enqueue `SimCommand`s and read the volatile
`CameraStore.Status` the director publishes with one swap. Despawn pruning (`CameraDirector.Prune`)
rides the sampler's vehicle enumeration beside `VesselForceRender.Prune`. **Failure mode:** a driver
throw calls `Restore()` first — the player never keeps a camera gatOS stopped driving — and only then
latches `_cameraDead` for the session with one error log; an *unresolvable frame* is not a failure at
all (the director holds the last good pose and logs the reason once, de-duplicated by message, so a
despawned anchor cannot write 60 lines a second), and a non-finite result is treated as unresolvable so
no NaN can reach the view matrix. Teardown rides `Mod.TeardownGameCheats` (`Shutdown()` = `Restore()` +
`CameraStore.Clear()` + the track player's `Clear()`).

⚠️ **Two side effects worth knowing.** `KittenEva.PrepareWorker` feeds
`Program.GetMainCamera().GetForwardEcl()`/`GetRightEcl()`/`GetUpEcl()` into EVA locomotion, so **while
gatOS holds the camera, "forward" for a kittenaut on EVA is wherever the shot is facing** — documented
rather than worked around, because silently taking EVA control away would be the more surprising side
effect. And leaving `CameraMode.Map` is the one transition that goes through `SetCameraMode` (accepting
its three-second alert), because `MapController.OnSwitchOn` also clears
`Program.IsControlledVehicleActive` and stashes `PreviouslyControlledVehicle`, and only `OnSwitchOff`
restores them — bypassing it would strand the player with an **uncontrollable vessel**.

The camera's KSA members are all `[KsaAnchor]`-documented in `Game/Ksa/Camera/` (21 anchors, Risk
Low–Medium, verified `2026-08-06` / `2026.8.5.5168`); the writes are
[`ksa-write-surface.md#camera-director`](ksa-write-surface.md#camera-director), the reads
[`ksa-read-surface.md#camera`](ksa-read-surface.md#camera).

### IVA and Map as ownership contexts — NOT implementable without a Harmony patch {#camera-mode-contexts}

A **finding**, recorded here because it is a design constraint discovered by implementation, not a
backlog item. Plan §7 C5.1/C5.2 assumed "gatOS writes last, so the IVA seat-pin and the map's own
solution are bypassed". The decomp says otherwise, and the reason is the frame order above: gatOS's
write only survives because the *next* frame's controller writes nothing. Neither of these does:

- **`IVAController.OnFrame`** (`KSA/IVAController.cs:27-118`) — lines 29-38 read
  `if (!(Camera.Following is Vehicle vehicle)) { Program.HoveredViewport.NextCameraMode(); return; }`
  (and the same for a changed vehicle). gatOS's ownership *requires* `Following == null`, so this fires
  **immediately** and cycles the mode straight back out of IVA — on the **hovered** viewport, which need
  not even be the one gatOS bound. Line 41 then assigns `Camera.PositionEcl` from the seat
  **unconditionally**, and line 112 assigns `Camera.LocalRotation` on every frame but the switch frame,
  after the cone clamp (lines 82-108). The seat pin is not something a later writer can win; it is the
  *first* thing written each frame.
- **`MapController.OnFrame`** (`KSA/MapController.cs:124-289`) — lines 126-130:
  `if (Camera.Following == null) { Program.SetCameraMode(CameraMode.Free); return; }`; and lines 281-282
  assign **both** `Camera.PositionEcl` and `Camera.LocalRotation` from its own scope/orbit-view
  solution, unconditionally, at the end of every frame.

So making either an ownership context requires a Harmony patch that suppresses a controller's
`OnFrame` — precisely what this feature's design exists to avoid (and what the `unscience` cautionary
tale warns about: a patch there froze the other viewports outright). **Neither is offered**, and the
reasoning is written into `CameraDirector`'s own type remarks so the next reader does not re-derive it.
What C5.2 *does* ship is `MapController.Scope` as a first-class leaf (`/sim/camera/map/scope`) — the
part with standalone value.

One consequence worth recording: **hazard 10 (`Camera.NoRotation` changing what `PositionCce` and
`LocalPosition` mean) is moot for the owned camera.** `Take` sets `NoRotation = false` on the base
camera and unfollows, and with `_following == null` *and* `Parent == null` both `Camera.PositionEcl`
(`KSA/Camera.cs:106-127`) and `WorldRotation` (`:130-146`) bypass the flag entirely. It still matters on
the **restore** path, where it is already handled (`NoRotation` is written before `LocalPosition`, and
`CameraDirector.RestorePositionEcl` reproduces the `PositionCce` composition).

### Schedule tick (no patch, no KSA binding) {#schedule-tick}

The **timed command scheduler** (`gatOS.SimFs/Commands/`, plans/SCHEDULER_ASBUILT.md) is listed here
only because it adds a game-thread work site — it adds **no** Harmony patch and **no** `[KsaAnchor]`,
because it holds no KSA type at all. `Mod.TickSchedules(dt)` runs inside `DrivePerFrame`
**immediately before** the command drain — the **seventh game-thread work site** — so a command that
falls due on this frame is posted into the queue the very frame it executes, and a schedule's timing is
the frame grid rather than the frame grid plus one. Each tick: `ScheduleStore.Activate(ut)` (drain
transport-thread commits into live players, evicting *finished* players oldest-first **only under cap
pressure**), `AdvanceAll(renderMs, wallMs, utMs)` (each distinct clock advanced exactly once — shared
`@group` clocks included), `Tick(due, ut)`, then `CommandQueue.Post` per due entry.

**The three clock bases are sourced here and only here**, which is the one place a game update could
change *behaviour* without changing a binding: `render` is the frame's `dtPlayer`, which KSA clamps to
`1/MinTargetFrameRate` — so it lags true elapsed time after a hitch and never catches up (correct for
cinematics, and exactly why the other two exist); `wall` is gatOS's own `Stopwatch`, **stopped while
the registry is empty** so an idle gap is never banked into the first tick of the next schedule; `ut`
is the sim-time delta off `Universe.GetElapsedTime()` (through the sampler's already-anchored time
read, wrapped so a pre-first-step throw degrades to `0`), unavoidably discontinuous — a scene load
rewinds it, warp leaps it forward — so a backwards step is **clamped to zero** rather than rewinding a
player's timeline.

It **self-gates to two integer compares** while nothing is live (`_runners.Count == 0`,
`_ids.Count < MaxLive`), and `Scheduler.Tick` allocates nothing on a tick with 0 or 1 entries due.
Transport threads only ever touch a `ConcurrentDictionary`, a `ConcurrentQueue` and one volatile
immutable array (`ReserveId`/`Submit`/`Find`/`Players`/`IsIdLive`/`Count`); `Activate`/`AdvanceAll`/
`Tick`/`Execute`/`Clear` are game-thread only, and `IPostObserver.OnCommandResult` fires **inline on
the game thread** inside `CommandQueue.Drain`. **Failure mode:** a tick throw sets `_schedulesDead` for
the session with one error log — scheduling stops, nothing else does. Teardown rides
`Mod.TeardownGameCheats` (`ScheduleStore.Clear()`). Because scheduled commands go through the ordinary
executor, they inherit every action's own phase, errno, health latch and per-frame command budget
(`max_commands_per_frame`) unchanged — a schedule can therefore be starved by that budget under a
catch-up burst, which is what the coalescing policy and the `<id>/dropped` counter exist to bound.
Full inventory: [`non-ksa-surface.md#scheduler`](non-ksa-surface.md#scheduler).

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
- **IVA cabin-physics driver** — *not* a `CommandQueue` phase: a per-frame step of a gatOS-owned physics
  world plus `Part.PositionParentAsmb`/`Asmb2ParentAsmb` writes on driven SubParts, in `OnAfterUi` (the
  sixth game-thread work site, [`#iva-cabin-sim`](#iva-cabin-sim) above). Off by default; the
  `debug.iva_*` registry writes themselves are ordinary Frame-phase commands.
- **always_render prefixes** — *not* a `CommandQueue` phase: read-only prefixes on the render-prep path
  (`Vehicle.GetWorldMatrix`/`UpdateRenderData`) consulting a volatile immutable id set — they mutate no
  game state beyond what the stock methods do ([`#always-render-patches`](#always-render-patches) above);
  the `vessel.always_render` registry write itself is an ordinary Frame-phase command.
- **Schedule tick** — *not* a `CommandQueue` phase, but the thing that *feeds* it: it runs in
  `DrivePerFrame` **immediately before** the Frame drain and `Post`s due commands into the same queue,
  so each one routes to its own phase by action key exactly as if a transport had submitted it (the
  seventh game-thread work site, [`#schedule-tick`](#schedule-tick) above). The seven `schedule.*`
  actions themselves are ordinary Frame-phase commands answered game-free.
- **Camera driver** — *not* a `CommandQueue` phase: a per-frame `Camera.PositionEcl`/`LocalRotation`/
  projection write at the end of `OnAfterFrame`, on **every** rendered frame (the eighth game-thread
  work site, [`#camera-driver`](#camera-driver) above). It self-gates to one branch while unowned; the
  28 `camera.*` writes themselves are ordinary Frame-phase commands, and **no camera action is in
  `SolverActions`** — nothing about the camera is visible to the vehicle solver.

Threading rules 1–5 (AGENTS.md): game state read+mutated **only** on the game thread; 9p/HTTP/MQTT
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

| Accessor | gatOS site | Reflected member | 5018 status |
|---|---|---|---|
| Manual throttle setter | `ThrottleActuator.cs:17,33` | `Vehicle._manualControlInputs` (private field) → `.EngineThrottle` (public field on the struct) | ✅ present (`Vehicle.cs:232` in 4939, 4980 **and 5018**; `ManualControlInputs.cs` untouched since 4750) |
| Light template clone | `LightActuator.cs:127` (`EnsureUnshared`/`ShallowClone`) | generic field-by-field clone of `LightModule.Template` + `Intensity`/`ColorRgb`/`OuterAngle`/`InnerAngle` (the per-instance "red-alert" unshare) | ✅ (`LightModule.cs` untouched since 4750 — not in the 4980 **or 5018** changed set) |

The throttle field is the single most fragile binding gatOS has (private field, reflection, High). It was
explicitly confirmed present in 4750 and re-confirmed in 4826 (decomp diff, 2026-07-03), 4892
(decomp diff, 2026-07-14), 4939 (decomp diff, 2026-07-16), 4980 (decomp diff, 2026-07-22) and 5018
(decomp diff, 2026-07-24). If a future
update removes it, `ctl/throttle` writes return
`Unsupported` ("manual throttle field not found in this build") and the read-back falls back to
`GetManualThrottle()` (public, still present). A live `/sim/status/accessors` check after each update
remains standard practice (decomp can lag the shipping binary).

### FX-editor reflection accessors — `FxReflect` (no patches, no driver) {#fx-accessors}

The four FX editors (`/sim/debug/{engineplume,plumetrail,clouds,terrain}`,
`Game/Ksa/Fx/FxReflect.cs`, plans/FX_EDITORS_PLAN.md) are gatOS's **largest reflection surface** — but
also its most contained one: **no Harmony patch, no per-frame driver, no GPU resources**. All the
reflection does is reach a handful of handles KSA keeps private; everything gatOS then reads or writes
through those handles is public API. The write/read rows are on
[`ksa-write-surface.md#fx-editors`](ksa-write-surface.md#fx-editors) /
[`ksa-read-surface.md#fx-editors`](ksa-read-surface.md#fx-editors).

| Latch key | gatOS site | Reflected member | 5056 status |
|---|---|---|---|
| `fx.trail_renderer` | `FxReflect.Trail` | `Program.Instance._volumetricTrailRenderer` (private instance field) — the only handle on the one `VolumetricTrailRenderer` | ✅ present (`Program.cs:160`) |
| `fx.plume_templates` | `FxReflect.PlumeTemplates` | `VolumetricExhaustTemplate.References` (internal static `SerializedCollection<…>`) → public `GetList()` | ✅ present (`VolumetricExhaustTemplate.cs:37`); degrade falls back to harvesting ids off live nozzles |
| (best-effort, unlatched) | `FxReflect.PlumeModifierArgs` | `VolumetricExhaustRenderer._currentAtmosphericPressure` / `_debugThrottle` (private) off the public `Program.VolumetricExhaustRenderer` | ✅ present (`VolumetricExhaustRenderer.cs:253,277`); falls back to `(0, 1)` — the per-frame draw path recomputes both for every live nozzle, so it cannot disturb a plume |
| `fx.cloud_renderer` | `FxReflect.Clouds` | `Program.Instance._planetTransparenciesRenderer` (private) → `GetCloudRenderer()` (public) | ✅ present (`Program.cs:152`, `PlanetTransparenciesRenderer.cs:87`) |
| `fx.cloud_apply` | `FxReflect.CloudApply` | `CloudRenderer._renderer` / `_cloudShadowsRenderer` / `_worleyNoise3dTarget` (private) — the three arguments the layer re-upload needs (`_planetToCloudRenderData` itself is public) | ✅ present (`CloudRenderer.cs:95,151,235`) |
| `fx.terrain_renderer` | `FxReflect.Terrain` | *(none — `Program.GetPlanetRenderer()` is public)*; the latch exists so a missing renderer degrades terrain alone | ✅ present (`Program.cs:491`) |
| `fx.terrain_ubo` | `FxReflect.TerrainUbo` | `PlanetRenderer._renderUboMap` / `_meshUboMap` (private readonly `MappedMemory`, host-visible + coherent); strides/counts from the public `PlanetUboStride`/`MeshUboStride`/`NumCelestials`, frames from `Program.GetRenderer().MaxFramesInFlight` | ✅ present (`PlanetRenderer.cs:250-252`) |

**Lifecycle.** Each `FieldInfo` is resolved **lazily on first use, once, and cached** in a plain static
(resolution is idempotent, so a torn read at worst re-resolves); every accessor is **null-tolerant** —
it returns `null` plus a human-readable reason instead of throwing, and the caller turns that into a
`KsaHealth` latch (`FxReflect.Degrade` → `EOPNOTSUPP` for that capability only) or, for the cloud apply,
into a **silent degrade that still reports `Ok`** because the data write already landed. A later success
clears the latch (`FxReflect.Healthy`). All resolution and use is **game-thread only** — the Frame
command drain and the sampler tick. **Teardown** is `Mod.TeardownGameCheats` → `FxPristine.RestoreAll()`
(replays every captured pristine value through the actuators' own write paths, so a restore runs the same
propagation/apply a set does) then `FxEditorReader.Reset()` (drops the sampler memo). There is nothing
else to unwind: no patch to remove, no GPU object to free.

The terrain UBO write is the one place gatOS writes **GPU-mapped memory** (unsafe `ref` spans over the
reflected `MappedMemory`, frame slot 0 + the frames-in-flight mirror copy) — on the same main thread the
game's own Terrain Editor writes it from, into host-visible + host-coherent memory. Verified
`2026-08-01` against `2026.7.10.5056`; because none of these can fail the build, they belong on the
"re-verify live after every update" list with the throttle field — live pass pending in
[`../docs/VALIDATION.md`](../docs/VALIDATION.md).

---

## Churn machinery (how the coupling defends itself)

| Mechanism | File | Role |
|---|---|---|
| `[KsaAnchor]` attribute | `Game/Ksa/KsaAnchor.cs` | documentary marker on every KSA-touching member (`Member`, `SourceFile`, `Verified`, `GameVersion`, `Risk`, `Notes`). The grep target when a build breaks; the **source of truth** this whole `scope/` folder mirrors. |
| `KsaHealth` | `Game/Ksa/KsaHealth.cs` | per-accessor degrade latches (game-thread-only dict); first fault logs once, publishes to `/sim/status/accessors`, a later success clears it. The runtime safety net. |
| `KsaCatalog` | `Game/Ksa/KsaCatalog.cs` | the only place actuators are reached: dispatch table, authority gate, per-command try/catch → `KsaHealth`. |
| `VesselReader.BuildFull` guard | `Game/Ksa/Readers/VesselReader.cs` (`Sample`) | whole detail-pass try/catch: an extension-API drift falls back to `BuildCore`, keeping core telemetry and dropping the extension dirs. |
| Build against new DLLs | `KSAFolder` resolution | the **compile-time alarm**: non-reflective renames/removals become errors at the anchor sites. |

Two-layer defense: **build = catches structural breaks; health latch = catches runtime drift.** Neither
catches *semantic* drift (units/frames) — that needs the decomp diff (playbook step 3) + live validation.

---

## Mod-ecosystem ABIs (NOT KSA game state) {#mod-ecosystem-abis}

These are couplings to the **mod ecosystem**, separate from KSA-game churn — they change on their own
schedules and a KSA update does not touch them. Listed so the coupling census is complete.

> ⚠️ **Refined at 5168 — "a KSA update does not touch them" is about the *ABI*, not the *companion
> mod*.** The purrTTY **contract** is indeed KSA-independent, but purrTTY the mod has its own
> game-coupled render patches, and rev 5154 broke them: `purrTTY.GameMod`'s
> `RenderTranslucencyPassPatch` and `OffscreenRenderTarget` bound to the deleted `KSA.OffscreenTarget`
> / `KSA.RenderTarget` / `Program.OffScreenPass`, so an unpatched purrTTY **hard-crashes KSA on the
> first rendered frame** with `TypeLoadException: Could not load type 'KSA.OffscreenTarget'` — a
> failure that looks like a gatOS crash (gatOS is loaded and in the log) but is not. **Operational
> rule: when a KSA update breaks gatOS's render bindings, assume purrTTY needs the same migration and
> rebuild both before attempting any in-game validation.** Fixed 2026-08-05: purrTTY's offscreen target
> now owns its attachments/render pass/framebuffer outright (rather than borrowing KSA's deleted
> helpers), its quad pipeline sources formats + sample count from `Program.OffscreenTarget`, and its
> scene barrier uses the tracked-state `PipelineBarrier2(IRenderImage, ImageBarrierInfo)`.

| ABI | gatOS site | Pin / source | Notes |
|---|---|---|---|
| **purrTTY contract** | `Mod.cs` (`CustomShellRegistry.Instance.RegisterShell`), `gatOS.Ssh/SshShellSession : ICustomShell` | `vendor/purrTTY/` (committed, pinned) | The inter-mod ABI for the terminal. Shared over the StarMap ALC at runtime (D6). A contract change is its own refresh (`vendor/purrTTY/README.md`), independent of KSA. |
| **StarMap loader** | `Mod.cs` `[StarMap*]` attributes; `CustomShellRegistry` resolution | `StarMap.API` (loader-supplied) | Lifecycle attributes only; the loader resolves dependency-mod ALCs. |
| **ModMenu** | `Game/Mod.Game.cs` `[ModMenuEntry("gatOS")]` | `ModMenu.Attributes` (optional companion mod) | Optional — the Harmony menu fallback covers its absence. |
| **ImGui** | `Game/Mod.Game.cs` status window | `Brutal.ImGuiApi` | UI only; render-thread, reads volatile state. |

See [`non-ksa-surface.md`](non-ksa-surface.md) for the rest of the game-free surface, and
[`ksa-assets-and-versions.md`](ksa-assets-and-versions.md) for the KSA assets these couplings resolve
against.
