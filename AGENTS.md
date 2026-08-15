# AGENTS.md

This file guides Claude Code (claude.ai/code) when working in this repository. It documents what
**exists today**; the forward plan lives in `OS_PLAN.md`. When this file and the plan disagree
about how the code *currently works*, this file wins and the plan is stale — fix it.

## Project Overview

gatOS is a **standalone KSA mod** that runs a real, minimal **Alpine Linux** inside a **QEMU
microVM** subprocess. Players open terminal sessions into the guest through **purrTTY** — which
stays an unmodified terminal emulator, consumed only via its published `purrTTY.CustomShellContract`
extension point — over SSH. The whole point is to stop hand-writing terminal userland: real `apk`,
real shells, real pipes/jobs/pagers/editors, zero custom guest binaries.

Live KSA vehicle telemetry is exposed to the guest **as a filesystem**: a C#-implemented
**9P2000.L server** that the guest mounts at `/sim`, so the entire unix toolbox (`cat`, `watch`,
`tail -f`, `jq`, awk pipelines) becomes the game API surface. HTTP, MQTT, serial, and the first-class
MCP server project the same snapshot/command model for their respective clients; MCP is the concise,
logical JSON interface for AI agents, deliberately not a filesystem mirror. Persistence is qcow2
overlays, one per save profile, on top of a pristine shipped base image.

> **`/sim` is a published API. Its complete catalog is [`SPEC_9P_FILESYSTEM.md`](SPEC_9P_FILESYSTEM.md)**
> — every path, value format, unit, read/write semantic, command action key, errno, and HTTP `/v1` /
> MQTT mirror. It is the reference for anyone (player, modder, or the `gatos` skill) writing programs
> against the sim. **See the binding constitution in "The `/sim` API contract" below: the SPEC must be
> updated in lockstep with any change to the `/sim` surface.**

The architecture and the research behind it are fixed in **`OS_ANALYSIS.md`** (options considered,
why QEMU won); **`OS_PLAN.md`** is the execution plan (milestones M0–M12, fine-grained tasks). Read
`OS_PLAN.md` Part 0 before starting any task — it defines the execution model, repo conventions,
and the decisions locked in (Part 1).

> **Sibling repo:** `../purrtty` is the structural reference (csproj/slnx/CI patterns) and the
> source of truth for the vendored contract assembly. KSA decompiled sources are under
> `thirdparty/ksa/`; the `ksa` skill documents the mod lifecycle and telemetry APIs.

## Current status (what is actually built)

> Full per-milestone detail, class names, and as-built notes → **[`docs/MILESTONES.md`](docs/MILESTONES.md)**

**All milestones through M9, plus G1–G7 (HTTP/serial/TypeScript SDK), the embedded MQTT
transport, host folder mounts (`/mnt/<name>`), the welds / `always_render_iva` / parts-listing
cheats ported from `unscience`, the `/sim/audio` userland playback feature, the `/sim/debug/iva`
free-floating cabin-object physics, the generic timed command scheduler (`/sim/ctl/timed_batch`)
and the programmable camera (`/sim/camera`), are code-complete.** The only pending work is a set of in-game
passes (T6.6/T9.3/G1–G4, plus the welds/IVA/parts, thug_life, per-vessel scale/always_render, debug
impulse, `ctl/translate`, `/sim/audio`, IVA-cabin-physics, FX-editor, **timed-scheduler and camera**
checklists) that require a live KSA flight; checklists are in [`docs/VALIDATION.md`](docs/VALIDATION.md). The purrTTY tip release is now
cut.

> **Prior pass — `2026.7.9.5018`** (upgrade-ksa playbook pass 2026-07-24, from 4980; superseded as the
> baseline by the 5117 pass below): **one compile
> break, fixed** — rev 4992 (solid rocket motors) generalized propellant storage from liquid-only to a
> new `ISubstanceStore` (`Liquid | Solid`) abstraction, renaming `Mole.GetLiquidMass` → `GetStoredMass`;
> `VesselReader.SampleTanks` was the one call site and the value is unchanged (`Tank` moles are liquids).
> Build + tests green (0 warnings, 681 passed); no other bound member, reflection accessor, or Harmony
> hook target changed (`Program.RenderGame` and `SuperMeshRenderSystem.cs` byte-identical; no `Brutal`
> numerics drift; no celestial body-parameter edits). **One coverage gap opened and closed in the same
> work item:** SRB solid propellant lives on the new `SolidGrainSegment` module — an `ISubstanceStore`
> but **not** a `Tank` — so it is absent from `/sim`'s `tanks/`, while `Vehicle.PropellantMass` (now
> computed from `Parts.SubstanceStores`) *does* include it. gatOS gained a dedicated **`srb/<n>/`** read
> surface (SPEC §3.4.8, `VesselReader.SampleSrbs`, `SrbSnapshot`/`SrbSegmentSnapshot`): grain mass,
> usable mass, fraction, burn time, mass flow, chamber/exit conditions, burning area, stack validity and
> a per-segment `segments/<m>/` breakdown — **read-only**, since KSA forces a solid's throttle to 0 or 1,
> so ignition stays on the engine surface (`srb/<n>/engine` cross-links to `engines/<n>`). Two inherited semantic drifts (no API change): encounter candidacy widened (rev 4991 — small
> moons like Phobos/Deimos now yield `encounters/<n>/` rows) and `Module.List` concrete-type segmentation
> (rev 4990 — API-compatible for every call gatOS makes). Behavior notes (`debug/refuel` now refills SRB
> grains for free; SRBs are ordinary `EngineController`s so `engines/<n>` covers them, though throttle is
> physically inert on a solid; `ModuleBase.OnPartCreated` → `OnFullPartCreated` leaves the `AnimationLinks`
> solar link intact) + the pass record live in [`scope/FULL_SCOPE.md`](scope/FULL_SCOPE.md) §0 / the scope
> pages; live re-check items appended to [`docs/VALIDATION.md`](docs/VALIDATION.md).
>
> **KSA baseline → `2026.8.5.5168`** (upgrade-ksa playbook pass 2026-08-05, from 5117; revs 5118–5168,
> 49 commits — PREVIOUS was an audited baseline and CURRENT's `fromRevision` is 5117, so the trees chain
> with no gap). **Four compile breaks, all fixed; three silent semantic breaks, all closed.** Build +
> tests green (0 warnings, 772 passed).
> - **All four breaks are rev 5154**, which moved offscreen rendering off `VkRenderPass`/framebuffers
>   onto **Vulkan dynamic rendering** and deleted `Program.OffScreenPass`, `KSA.OffscreenTarget`,
>   `KSA.RenderTarget`, `KSA.Framebuffer`, `Core.RenderPassState`, `Core.DynamicRenderState`:
>   `FxReflect`'s cloud worley handle retyped `RenderTarget`→`RenderImage`; `FrameCapture` null-guards
>   the now-nullable `RenderTarget.ColorImage`; `ThugLifeQuadRenderer.BuildPipeline` migrated to
>   **`Program.OffscreenTarget.SetupGraphicsPipeline(ref info)`** (KSA's own pattern — the quad now
>   *tracks* the engine sample count, incl. the new CMAA2 option of rev 5156, instead of hard-binding
>   one). ⚠️ The **new** `KSA.Rendering.RenderTarget` is an *unrelated* class reusing the deleted name —
>   never re-bind by name alone. Render preconditions were re-derived, not assumed:
>   `MainViewport.OffscreenTarget` is still literally `Program._offscreenTarget` (`:1385`), the offscreen
>   colour still ends in `SampledReadVfc` (`:4312`) before the final composite + `End()`, and CMAA2 uses
>   its own target — so the `/sim/display` transpiler and capture both still hold.
> - **`ctl/translate`/`ctl/rotate` silently died when RCS is off** (rev 5143 — `ComputeRcsControl` now
>   zeroes the manual `ThrusterCommandFlags` whenever `FlightComputer.RCSMode != Enabled`). This
>   **falsified an explicit `scope/` claim** and inverted a VALIDATION item. Closed by exposing
>   **`ctl/rcs_mode`** (`Enabled`/`Disabled`; read + **Solver**-phase write, because `CopyFrom` copies it).
> - **`decoupler.fire` returned false success** on a player-disabled decoupler (rev 5132 — `Decoupler`
>   gained `IEnable` and `SetIsActive` is gated on `IsEnabled`). Now **EOPNOTSUPP**, plus a new
>   **`decouplers/<n>/enabled`** read.
> - **The game now clears the latched thruster flags** (rev 5128, `Vehicle.ClearHeldPlayerInput()`): on
>   vessel switch, **window focus loss**, camera-mode switch, and — *every update* — while **ImGui holds
>   keyboard focus** or **warp > 30×**. So `ctl/translate`/`ctl/rotate` no longer latch unconditionally
>   (documented in SPEC §3.4.19; `ctl/throttle`/`ctl/ignite` are unaffected — different fields).
> - **Inherited value drift, no API change:** encounters now predict near-coplanar SOI transfers (rev
>   5141, e.g. Hohmann to Luna), RCS thrust reduced (5119), size-D/E SRB grains resized (5124), part
>   mass/moment-of-inertia computation fixed + tangent-ogive mass type (5166).
> - **Verified clean:** all 13 reflection accessors and all 7 Harmony hook targets, including the
>   `KittenEva._renderable → _characterAvatar → Core → Scale` chain despite heavy kitten-locomotion churn
>   (revs 5128–5144); `SuperMeshRenderSystem.{RenderMainPass,RenderTranslucencyPass}` signatures unchanged.
> - **Sibling repo:** `../purrtty` took the *same* rev-5154 break — an unpatched purrTTY hard-crashes KSA
>   on the first frame with `TypeLoadException: KSA.OffscreenTarget` (it looks like a gatOS crash but is
>   not). Migrated in the same work item: its offscreen target now owns its attachments/render
>   pass/framebuffer outright, its quad pipeline sources formats + samples from `Program.OffscreenTarget`,
>   and its scene barrier uses the tracked-state `PipelineBarrier2`. **Rule: when a KSA update breaks
>   gatOS's render bindings, rebuild purrTTY too before attempting in-game validation.**
>
> Pass record + full detail: [`scope/ksa-assets-and-versions.md`](scope/ksa-assets-and-versions.md#5168-pass),
> [read](scope/ksa-read-surface.md#5168-findings) / [write](scope/ksa-write-surface.md#5168-findings);
> live re-check items appended to [`docs/VALIDATION.md`](docs/VALIDATION.md).

> **Prior baseline — `2026.8.3.5117`** (upgrade-ksa playbook pass 2026-08-01). Because the
> intermediate `2026.7.10.5056` bump was build-only and never audited, this pass diffed the **full
> `5018 → 5117` window** (revs 5019–5116, 97 commits) via the assemblies checkout's own git history —
> closing the 5056 gap and validating 5117 together. **Two compile breaks, both fixed; two silent
> semantic drifts documented; everything else clean.** Build + tests green (0 warnings, 769 passed).
> - **`NavBallData.DeltaVInVacuum` → `DeltaV`** (rev 5114), `VesselReader.SampleNavball` the single call
>   site. The rename was the *visible* half: the same revision also changed **both** navball values'
>   meaning — `deltav` is now the **active staging sequence's** propellant-aware Δv
>   (`Parts.PerformanceSequences.FindActiveSequenceDeltaV()`) instead of the whole-stack vacuum rocket
>   equation, and `twr`'s numerator became `ComputeActiveThrust(AtmosphericPressure)`, so TWR is
>   **atmosphere-corrected** and skips engines that cannot produce thrust. TWR compiles clean — it would
>   have drifted silently. Per maintainer decision this pass applied the **binding fix only**: no
>   `SimSnapshot` field rename and no SPEC rewording, so `NavballSnapshot.DeltaVVacuumMs` and SPEC §3.4.3's
>   "vacuum Δv" wording now overstate the value's provenance.
> - **`VolumetricTrailRenderer.ExpansionTimeSeconds` removed** (revs 5059/5097 split the plume-trail
>   subsystem apart). It moved onto the new `PlumeTrailSettings` with the same default and meaning, and
>   the game's own Plume Trails debug window still exposes it, so the `/sim` node was **re-bound, not
>   dropped** — new two-hop `FxReflect.TrailSettings` accessor (`VolumetricTrailRenderer.
>   _plumeTrailSegmentsManager` → `PlumeTrailSegmentsManager._settings`) with its own `fx.trail_settings`
>   health latch, so a future move degrades `render/expansion_time` alone. No SPEC change.
> - **Semantic drift, no code change:** substance phase **names** (rev 5095 — a new `DefaultPhase` XML
>   attribute makes the default phase render bare, so `tanks/<n>/substance` reports `"Kerosene"` not
>   `"Liquid Kerosene"`, `srb/<n>/substance` `"APCP"` not `"Solid APCP"`; gas-default substances keep
>   `"Liquid O2"`/`"Liquid H2"`); **encounter population** (revs 5106/5110 — final-trajectory-only, plus an
>   excessive-entry fix); **docking identity** (rev 5076 — larger vehicles now absorb smaller ones).
> - **Verified clean:** all eight Harmony hook targets, every reflection accessor, the whole `thug_life`
>   render pipeline (`SuperMeshRenderSystem.cs` **byte-identical**; the conditional alpha-to-coverage
>   attachment of revs 5057/5058 is not a member of the offscreen render pass), the terrain UBO writes,
>   the audio actuator, and the new `Part` matrix cache (rev 5112 — the pose setters gatOS writes through
>   all call `ResetCachedPosMatrixValues()`).
>
> Pass record + full detail: [`scope/ksa-assets-and-versions.md`](scope/ksa-assets-and-versions.md#5117-pass),
> [read](scope/ksa-read-surface.md#5117-findings) / [write](scope/ksa-write-surface.md#5117-findings);
> live re-check items appended to [`docs/VALIDATION.md`](docs/VALIDATION.md).

> **Whole-mod perf pass (2026-07-02):** all seven plans of
> [`plans/GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md`](plans/GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md)
> (GP1–GP7) are landed — zero-steady-state-alloc sampler (+"Sample alloc" tripwire in the status
> window), memoized `/sim` read surface, subscription-gated/paced MQTT, 9p zero-copy reads + inline
> dispatch, zero-alloc SSH pump, display static-frame suppression, HTTP keep-alive. The formal
> before/after measurement pass (plan §6) is still open.

| Milestone | Status | Key entry points |
|---|---|---|
| M0 — scaffold | DONE | `gatos.slnx`, `Directory.Build.props`, `GatOsPaths` |
| M1 — spike | DONE | `spike/NOTES.md` (**required reading** before M3/M4/M7/M8) |
| M2 — guest image | DONE | `guest/build-image.sh`, `guest/fetch-guest.*`, `GUEST_VERSION`=17 |
| M3 — gatOS.Vm | DONE | `VmHost.cs`, `QemuCommandBuilder`, `DiskManager`, `PortAllocator` |
| M4 — gatOS.Ssh | DONE | `SshShellSession.cs`, `VmConnectionBroker.cs` |
| M5 — purrTTY upstream | DONE (tip release cut) | purrtty commits `9fb5e13`/`a56966a` |
| M6 — gatOS.GameMod | Code DONE; T6.6 pending | `Mod.cs`, `Game/Mod.Game.cs`, `Game/TelemetrySampler.cs` |
| M7 — gatOS.NineP | DONE | `NineP/Server/Session.cs`, `NineP/Vfs/`, `Protocol/` |
| M8 — gatOS.SimFs | DONE | `SimFsTree.cs`, `SnapshotStore`, `StreamFile`, `EventsFile` |
| M9 — live telemetry | Code DONE; T9.3 pending | `SimFs/Telemetry/`, `Game/TelemetrySampler.cs` |
| G1–G4 — KSA integration | Code DONE; in-game pending | `Game/Ksa/`, `SimFs/Commands/`, `docs/KSA_INTEGRATION_MATRIX.md` |
| G5 — HTTP `/v1` | DONE | `gatOS.Http/` |
| G6 — TypeScript SDK | DONE | `examples/sdk-ts/` |
| G7 — serial/bus | DONE | `gatOS.Bus/` |
| MQTT transport | DONE | `gatOS.Mqtt/` |
| MCP transport | DONE | `gatOS.Mcp/`, `SimMcpServer`, `SPEC_MCP.md` |
| Host folder mounts | DONE | `NineP/Vfs/HostDirectory.cs`, `HostFile.cs` |
| Welds + `always_render_iva` + parts (ex-`unscience`) | Code DONE; in-game pending | `Game/Ksa/Welds/`, `Game/Ksa/Render/IvaForceRender.cs`, `Game/Ksa/Readers/PartsReader.cs` |
| Per-vessel `scale` + `always_render` nodes (ex-`unscience` garrys-torch scaling / i-feel-seen) | Code DONE; in-game pending | `Game/Ksa/Actuators/ScaleActuator.cs`, `Game/Ksa/Render/VesselForceRender.cs` — first-class vessel nodes outside `/sim/debug`, authority-gate-exempt (`KsaCatalog.AnyVesselActions`) |
| `thug_life` sunglasses quad (ex-`unscience`) | Code DONE; in-game pending | `Game/Ksa/ThugLife/` (GPU quad renderer + dynamic render postfix), `SimFs` `debug/thug_life` |
| IVA cabin physics (`/sim/debug/iva` — free-floating cabin objects; plans/IVA_MOVEMENTS.md) | Code DONE; in-game pending | `gatOS.SimFs/Iva/CabinPhysics.cs` (the game-free forcing field, unit-tested), `SimFs` `debug/iva` registry, `Game/Ksa/Iva/` (`CabinSim`/`CabinCallbacks` = a **gatOS-owned BepuPhysics 2.5 `Simulation`** in the vessel assembly frame; `InteriorGeometry` = collision mesh from the IVA art; `FloatingObject` = the driven SubPart; `IvaPhysicsManager` = registry + `Mod.DriveIvaPhysics` driver) — **off by default behind the `/sim/debug/iva/enabled` master switch**; no Harmony patch; new `BepuPhysics`/`BepuUtilities` refs |
| Custom audio (`/sim/audio` — userland playback through the game's FMOD; plans/GATOS_CUSTOM_AUDIO_PLAN.md P1–P3) | Code DONE; in-game pending | `gatOS.SimFs/Audio/` (store + writable `file/` dir + play/set/stop grammar), `Game/Ksa/Actuators/AudioActuator.cs` (FMOD Sound cache/channels/tick over `GameAudio.System`; new `Brutal.Fmod.dll` ref), HTTP `/v1/audio` binary upload routes, `audio.finished` events; gated by `[audio] audio_enabled` |
| FX editors (`/sim/debug/{engineplume,plumetrail,clouds,terrain}` — the game's four built-in imgui render editors as filesystems; issue #2, `plans/FX_EDITORS_PLAN.md`) | Code DONE; in-game pending | `gatOS.SimFs/Fx/FxCatalog.cs` (the four declarative field tables + the nine action keys) driving the `SimFsTree` debug dirs; `Game/Ksa/Fx/` (`FxReflect` handles + per-capability health latches, Plume/Trail/Cloud/Terrain actuators, `FxEditorReader` sampler, `FxPristine` reset/teardown) — no Harmony patch, gated by `[control] debug_namespace` |
| Timed command scheduler (`/sim/ctl/timed_batch` + `/sim/ctl/schedules/` — offset-scripted command playback on three clock bases, 7 `schedule.*` actions; `plans/SCHEDULER_ASBUILT.md`) | Code DONE; in-game pending | `gatOS.SimFs/Commands/{PlaybackClock,Schedule,Scheduler,ScheduleStore,ScheduleTree,TimedBatchFile}.cs` + `CommandQueue.Post`/`IPostObserver` — **100 % game-free, zero KSA bindings**; game side is only `Mod.TickSchedules` (the game-thread tick) and one `KsaCatalog` routing branch to `ScheduleStore.Execute`; gated by `[schedule] schedule_enabled` |
| Programmable camera (`/sim/camera/**` — ownership take/release, six frames, aim-with-offset, geodetic placement, JSON tracks, the interpolated `time` channel, `map/scope`; `plans/CAMERA_ASBUILT.md`) | Code DONE; same-frame fix pending live recheck | `gatOS.SimFs/Camera/**` (game-free math/compositor/tracks + anchor-relative smoother) + `gatOS.SimFs/SimFsTree.cs`; `Game/Ksa/Camera/{CameraDirector,CameraFrames,CameraTargets,CameraReader,CameraViewportPatch}.cs` + `CameraActuator.cs`. A main-viewport-identity Harmony prefix applies after simulation advance and before `Camera.OnFrame`; the postfix publishes KSA's final applied transform. Shared schedules/drain run before the prefix's camera apply; anchor translation is exact, only the relative component smooths, and aim is exact. Gated by `[camera] camera_enabled`. **IVA/Map ownership contexts remain unsupported** (`scope/ksa-runtime-coupling.md#camera-driver`) |
| Cinematic camera editor (`examples/photog-rs`) | DONE (standalone API consumer) | Rust/ratatui ordered-shot editor + versioned project JSON; compiles render-rate native camera tracks with a shared-clock `timed_batch` sidecar for smoothing/projection/release cues; direct `/sim` + HTTP `/v1`, live target discovery, hybrid playback controls, golden/compiler/transport/TUI tests. No `/sim` contract change. |
| Screen stream (`/sim/display`) | Code DONE; misrender **root-caused + fixed** (purrTTY libghostty `o=z` corruption → default `rgba`, + purrTTY content-hash re-decode; STREAM_PLAN.md §11); **perf/stability P0–P7 of [`plans/PERF_IMPROVEMENT_PLAN.md`](plans/PERF_IMPROVEMENT_PLAN.md) landed 2026-07-02, confirmed working in-game (informal pass)** (SSH read-pump, a=t keyframes, GPU blit downscale, zero-alloc encoder, demand pacing, 9p pooling + msize 512 KiB/guest v15, purrtty consumption fixes, P6: the purrTTY native rebuilt from ghostty main + `purrtty/vt-video-fixes` — the zig-0.15.2 `o=z` flate corruption and the placement-pin leak are FIXED, so `display_encoding` defaults to `rgba-zlib` again, 3–10× less wire; and P7: the native APC bulk lane, 82→1185 MiB/s consumption throughput); formal S6/S9 + P8 soak checklists still open | `SimFs/Display/`, `Game/Ksa/FrameCapture.cs` + `DisplayRenderPatch.cs` (in-band render-hook capture), `STREAM_PLAN.md` |
| `ctl/rotate` (W1, AGC_PLAN §7.4) | Code DONE; in-game pending | `Game/Ksa/Actuators/RotateActuator.cs`, `SimFs/Commands/RotateRules.cs` — manual RCS rotation signs, the translate sibling; full authority needs `attitude_mode=manual` (auto strips rotation bits) |
| AGC (`examples/agc` — Luminary099/yaAGC in-guest, plans/AGC_PLAN.md A0–A7) | Code DONE (host+wire-verified); in-game mission cards pending | one Rust crate: `proto/` wire codec + SocketPort + embedded `agc_engine.c` FFI (`--features embedded`), virtual IMU/PIPA/LR, RCS duty→`ctl/batch` demodulation, THRUST clocking, padload generator (+KSA audit), P27 uplink, downlink recorder, ratatui `dsky`; `tools/agc` launcher; `apollo11-system/` July-1969 Earth/Moon system generator; `APOLLO_11_FLIGHT_GUIDE.md`; mission cards in `docs/VALIDATION.md` |
| T11.1 — QEMU win-x64 | DONE | `tools/fetch-qemu.*`, `vendor/qemu/win-x64/` |
| M10+ | **Not yet implemented** | — |

The full `GATOS_IT=1` integration suite ran 321/321 green (Windows/TCG, guest v3, 2026-06-13).
Since then guest v10 added the `coreutils` package, whose GNU `tail` shadowed busybox `tail` in
PATH and broke `tail -f` on the 9p `/sim` mount (GNU `tail -f` follows via inotify, which v9fs
never delivers for the host-side appends that grow `stream`/`events`/`alarm`) — failing
`SimMountIntegrationTests`; fixed in guest **v14** by the `usr/local/bin/tail` poll-mode shim
(verified against a live mount, Windows/TCG, 2026-06-20); guest **v15** raises the /sim + /mnt mount
msize to 524288 to match the 9p server's raised ceiling (plans/PERF_IMPROVEMENT_PLAN.md P4); guest
**v16** adds `procps-ng`; guest **v17** makes `init-gatos` mount the unified cgroup2 hierarchy with
all controllers delegated (OpenRC's cgroups job on stock Alpine — busybox init must do it by hand),
so container runtimes work: in-guest `apk add podman` runs rootful containers out of the box
(`GuestCgroupIntegrationTests` asserts the mount + delegation). **M10 (persistence & savegame) is next.**

> **`spike/NOTES.md` is REQUIRED READING before any M3/M4/M7/M8 work** — notably: `i_size`
> must be truthful on ≥6.11 kernels; two distinct file models exist (growing-log `tail -f` vs
> blocking-event `cat`).

## Build and Test Commands

```bash
dotnet build                                     # build the whole solution
dotnet test  gatos.slnx --nologo -v quiet        # full suite (9 test projects)
dotnet build gatOS.Vm                            # one project
dotnet build gatOS.GameMod                       # also deploys the mod folder (see below)
```

Every `gatOS.GameMod` build deploys the complete mod folder via its `CopyCustomContent`
target (T6.5): managed payload (all output DLLs except loader-supplied 0Harmony/StarMap.API),
mod.toml + deps.json, **the pre-generated default config (`Configuration/gatos.default.toml` →
`<dist>/gatOS/gatos.default.toml`, the template the live `gatos.toml` is seeded from — see T6.3;
the wipe step excludes `gatos.toml` so a rebuild never deletes the player's config)**,
licenses, `guest/out/**` → `<dist>/gatOS/guest/` (High-importance message when missing — fetch
or build the guest first for an in-game-usable dist), and `vendor/qemu/win-x64/**` when present.
Destination: `GATOS_DIST_DIR` (CI) else the per-OS KSA mods dir (`SelectedDistModDir` in
`Directory.Build.props`). The managed payload is wipe-cleaned each deploy; `guest/`+`qemu/` copy
incrementally (`SkipUnchangedFiles`).

Every task ends with **both** the build and the test suite green. Keep test output minimal (no
Console spew from passing tests). Integration tests that need a real VM are gated by the
`GATOS_IT=1` env var and self-skip (`Assert.Ignore`) otherwise, so plain `dotnet test` never
needs QEMU. To run them locally: `guest/fetch-guest.sh` once (or build the image), have QEMU
available — on Linux/macOS a system install, on Windows run `tools/fetch-qemu.ps1` once (tests
pick up `vendor/qemu/win-x64/` automatically) — then `GATOS_IT=1 dotnet test gatos.slnx`
(PowerShell: `$env:GATOS_IT='1'; dotnet test gatos.slnx`). With `GATOS_IT=1`, missing
prerequisites are hard failures, not skips (see `gatOS.Vm.Tests/TestEnv.cs`); tests needing only
`qemu-img` (DiskManager) also run un-gated whenever QEMU is present. CI runs the full suite with
`GATOS_IT=1` under KVM.

## Repository layout & project map

```
gatos.slnx                      XML solution (19 projects: 10 libs/mod + 9 test projects)
Directory.Build.props           shared build config + KSA/dist path resolution
AGENTS.md / README.md           this file; user-facing readme
AGENTS.md                       the /sim schema-change constitution: the step-by-step playbook for
                                adding/changing a /sim node, action key, config gate or transport
                                mirror (archetypes, addressing modes, actuator + read-back rules,
                                the docs-lockstep matrix, tests, definition of done)
OS_IDEA.md / OS_ANALYSIS.md / OS_PLAN.md   goals / research / execution plan
KSA_GAME_INTEGRATION_PLAN.md    proposed plan: /sim read/write expansion, control files, HTTP +
                                bus transports, KSA-churn integration layer (G-series phases)
SPEC_9P_FILESYSTEM.md           THE catalog of the /sim 9p API surface: every path, format, unit,
                                read/write semantic, command action key, errno, HTTP /v1 + MQTT
                                mirror. The reference for writing programs against /sim; kept in
                                lockstep with the code (its own constitution). The gatos skill
                                (.agents/skills/gatos/) references it.
SPEC_MCP.md                     THE MCP v1 contract: AI-oriented logical JSON resources/tools,
                                canonical command envelope, batch/timed-batch semantics, list
                                pagination, explicit /sim/display exclusion, and MCP coverage
                                maintenance. Kept in lockstep with the shared model and code.
docs/MILESTONES.md              full per-milestone build detail (class names, as-built notes)
docs/ARCHITECTURE.md            runtime architecture, port allocation, telemetry pipeline/tuning
docs/KSA_INTEGRATION_MATRIX.md per-point KSA API reference (G1–G4 + documented deferrals)
docs/VALIDATION.md              in-game validation record (T6.6/T6.7 checklists + results)
docs/KSA_CELESTIAL_COORDINATE_FRAMES.md details on the KSA games coordinate frame systems for frames of reference 
docs/TUTORIAL_DATA_REFERENCE.md the /sim data + file↔HTTP correspondence the site/ tutorials are built
                                from (flight-computer controls, frames, telemetry, pacing/gating);
                                paired with the `tutorials` skill (the authoring guide). Lockstep w/ SPEC.
scope/                          game-integration scope catalog (scope/FULL_SCOPE.md entrypoint + the
                                ksa-*/non-ksa-surface pages): EVERY gatOS feature ↔ its KSA binding,
                                with decompiled-source + Content-asset paths and the game-update
                                break-check playbook. THE reference for "will a game update break
                                gatOS, and where?" Kept in lockstep with the code (see the mandate).
plans/                          active execution plans (e.g. FIX_CURRENT_GAPS_PLAN.md — the gaps a
                                game update introduced and how to close them; IVA_MOVEMENTS.md — the
                                landed free-floating cabin-object physics, incl. the decompiled evidence
                                for why gatOS runs its OWN physics world rather than KSA's ConstraintSim)
site/                           Astro/Starlight docs site — the progressive `guides/` tutorial series
                                teaching flight programs against /sim (+ the HTTP /v1 mirror). Author
                                new tutorials via the `tutorials` skill (.agents/skills/tutorials/):
                                house style, MDX mechanics, the beginner→advanced ladder, reusable
                                snippet library; data facts in docs/TUTORIAL_DATA_REFERENCE.md
LICENSE                         MIT (the mod's own code)
THIRD-PARTY-NOTICES.md          QEMU GPLv2, Alpine, SSH.NET, Tomlyn, …
vendor/purrTTY/                 pinned contract DLLs (committed) — see its README for the pin
vendor/qemu/                    NOT in git — fetched QEMU bundles: win-x64 (T11.1, built;
                                tools/fetch-qemu.*) + linux-x64 planned (T11.6, D5 revision)
guest/                          guest image pipeline (M2, built): build-image.sh,
                                fetch-guest.{sh,ps1}, GUEST_VERSION pin, rootfs-overlay/,
                                README.md; guest/out/ NOT in git (fetch or build it)
tools/                          fetch-qemu.{sh,ps1} + qemu-win64-files.txt (pin + bundle list)
                                + Get-QemuImportClosure.ps1 (T11.1, built)
.github/workflows/build.yml     CI: build + full test suite (GATOS_IT=1, KVM, fetched guest)
.github/workflows/guest-image.yml  CI: build + publish guest-v<N> release (guest/** pushes)
.github/workflows/mod-release.yml  CI: build + publish the mod dist as ONE release with two
                                zips — gatOS-windows-<v> (payload + guest + bundled win-x64
                                QEMU) and gatOS-linux-<v> (payload + guest, system QEMU on PATH;
                                linux-x64 bundle = unbuilt T11.6). Both built on Linux runners
                                (meta→build matrix→publish); main → tip prerelease,
                                release/<v> → release v<v> (T11.4)
.github/workflows/site-deploy.yml  CI: build the site/ Astro/Starlight docs (pnpm) and publish to
                                GitHub Pages (artifact flow) — served at
                                https://meow.science.fail/gatOS/ (base '/gatOS/'). Runs on main
                                pushes touching site/** (or the workflow) + workflow_dispatch. Repo
                                setting: Pages source = "GitHub Actions"
```

### Projects and the dependency rule

```
gatOS.Logging                    (no deps)            game-free logging shim + PerfStat
                                                      (alloc-free single-writer timing accumulator)
gatOS.NineP    → Logging                              9P2000.L codec + server + VFS (M7, built);
                                                      VfsScan (walk/read/write) + VfsFile.IsStreaming
                                                      back the field-level transport mirrors; a
                                                      write/create surface (Tlcreate/Tmkdir/Tunlinkat/
                                                      Trenameat) + HostDirectory/HostFile/HostMountTree
                                                      back the /mnt host-folder passthrough (built)
gatOS.SimFs    → NineP, Logging                       /sim tree, snapshots, stream/events, AlarmFile,
                                                      EventDiffer/SampleClock/Sanitize (M8+M9+G3, built);
                                                      TelemetrySettings (runtime-mutable sample rate +
                                                      per-stream gates the sampler reads each tick);
                                                      Formats + SimJson (the shared JSON projection
                                                      HTTP/MQTT both serve — transport parity; UTF-8
                                                      byte variants for the MQTT push path);
                                                      Commands/ (SimCommand, CommandQueue, Control/Trigger/
                                                      Vector/Enum/Number/Token control files — G1+G4, built;
                                                      + BatchFile: /sim/ctl/batch atomic same-tick command
                                                      groups, drained as ONE unit — SPEC §3.10;
                                                      + the generic timed scheduler: PlaybackClock (the one
                                                      timeline primitive — render/wall/ut bases), Schedule,
                                                      Scheduler (cursor + coalescing catch-up), ScheduleStore
                                                      (the live-player registry + the game-free schedule.*
                                                      executor), ScheduleTree, TimedBatchFile — backing
                                                      /sim/ctl/{timed_batch,schedules/} with ZERO KSA binding);
                                                      Camera/ (the game-free half of /sim/camera: CameraMath/
                                                      Easing/Splines/PoseSmoother, CameraRules, the three-layer
                                                      CameraState compositor, CameraStore + the writable track/
                                                      dir, CameraCommands' action keys + six line grammars,
                                                      CameraFormat, and the JSON track parser/evaluator/
                                                      playback, which registers into the schedules registry as
                                                      kind = camera-track — CAMERA_ASBUILT.md, built; the
                                                      director lives in GameMod's Game/Ksa/Camera/);
                                                      Display/ (the /sim/display screen stream: DisplaySettings,
                                                      KittyEncoder, DisplaySurface, DisplayStreamFile +
                                                      control files — STREAM_PLAN.md, built; capture in GameMod);
                                                      Iva/ (CabinPhysics: the free-floating-cabin-object
                                                   forcing field — pure math over plain vectors, so the
                                                   whole physics model is unit-tested game-free);
                                                   Audio/ (the /sim/audio clip store: AudioStore caps/versioning,
                                                      writable AudioDirectory + upload handles, AudioCommands
                                                      play/set/stop grammar — GATOS_CUSTOM_AUDIO_PLAN, built;
                                                      the FMOD calls live in GameMod's AudioActuator)
gatOS.Http     → SimFs, Logging                       magic HTTP /v1 server (raw TcpListener; G5, built)
gatOS.Bus      → SimFs, Logging                       serial/bus framing CCSDS/NMEA/SCPI + the gatos.serial
                                                      SerialBridge/Connector over QEMU virtio-serial (G7, built)
gatOS.Mqtt     → SimFs, Logging, MQTTnet              embedded MQTT broker over the same store+sink (built)
gatOS.Mcp      → SimFs, Logging, ModelContextProtocol.Core
                                                      loopback Streamable HTTP MCP resources/tools over the
                                                      shared snapshots, command queue, and stores (built)
gatOS.Vm       → Logging, Tomlyn                      QEMU lifecycle, disks, ports, GatOsPaths (M3, built)
gatOS.Ssh      → Vm, Logging, vendor/purrTTY, SSH.NET SshShellSession : ICustomShell (M4, built)
gatOS.GameMod  → Ssh, SimFs, Http, Mqtt, Mcp, Bus, Vm, Logging, vendor/purrTTY,
                  KSA DLLs, StarMap.API, Lib.Harmony, ModMenu.Attributes, Tomlyn   the KSA mod (M6, built)
                  (+ the Brutal.Vulkan(.Abstractions/.Vma) + Planet.Render.Core + Brutal.Core.Memory game
                   DLLs and AllowUnsafeBlocks, for the Game/Ksa/ThugLife GPU quad renderer and the
                   Game/Ksa/FrameCapture screen-stream readback; + Brutal.Fmod for the
                   Game/Ksa/Actuators/AudioActuator FMOD playback; + BepuPhysics/BepuUtilities — KSA's own
                   embedded rigid-body engine — for the Game/Ksa/Iva cabin simulation)
```
`examples/sdk-ts/` is a standalone TypeScript/Bun example SDK (G6, built — not part of the .NET
solution); it talks to either transport behind one typed API.
`examples/photog-rs/` is the standalone ratatui cinematic camera editor (also outside the .NET
solution): its local v1 project format compiles to a native `/sim/camera/track/<name>` plus a
shared-clock `/sim/ctl/timed_batch` cue sidecar, and it runs over direct `/sim` or HTTP `/v1`.
Each library has a matching `*.Tests` NUnit project (`gatOS.GameMod` has none — it is game-coupled).
Test-only edges: `gatOS.SimFs.Tests` references `gatOS.NineP.Tests` (the shared managed 9p test
client), plus `gatOS.Vm`/`gatOS.Ssh` for its in-VM integration fixture.

> **THE dependency rule (binding):** only `gatOS.GameMod` may reference KSA / Brutal / StarMap
> assemblies. Everything else must build and test on a bare host with no game DLLs present. This is
> what keeps the 9p server, VM manager and SSH session headlessly testable (mirrors purrTTY's
> backend/frontend discipline). KSA references in `GameMod` are condition-guarded
> (`Condition="Exists('$(KSAFolder)/…')"`) **and** its game-coupled sources (`Game/**`, the
> partial half of `Mod`) are compile-gated on `KSAFolder/KSA.dll`, so the whole solution —
> `GameMod` included — still builds when the assemblies are absent.
>
> **Stronger form for KSA integration (G2):** a KSA type name may appear **only under
> `gatOS.GameMod/Game/Ksa/`** (`Readers/`, `Actuators/`, `Welds/`, `Render/`, `ThugLife/`, `Iva/`, `Fx/`,
> `Camera/`, `KsaCatalog`, annotated with `[KsaAnchor]`). ⚠️ The `Game/Ksa/Camera/` folder's namespace
> **shadows the simple name `Camera` for every file under `Game/Ksa/`** — any file there that names the
> game's type must alias it (`using KsaCamera = KSA.Camera;`). Transports (9p/HTTP/serial), the `/sim` tree, formats and the command pipeline
> never see one — they speak `SimSnapshot` (reads) and `SimCommand`/`ICommandExecutor` (writes).
> When a decomp drop breaks the build, the diff is confined to that folder + `docs/KSA_INTEGRATION_MATRIX.md`,
> and you MUST also update the matching [`scope/`](scope/FULL_SCOPE.md) page — the break-impact catalog
> and the game-update version-diff playbook (`scope/FULL_SCOPE.md` §0).
>
> **THE transport-parity rule (binding):** the 9p `/sim` tree, the HTTP `/v1` API and the MQTT
> `gatos/` topics must expose the **same** surface — every datum's granularity, every control point,
> and the whole `/sim/debug` cheat surface. This is kept structural, not manual: **reads** all
> project the one `SimSnapshot` through the shared `gatOS.SimFs/SimJson` layer (HTTP and MQTT) /
> `Formats` (9p files), and **writes** all funnel the one `SimCommand` through the single
> `ICommandSink`/`CommandQueue` (so `POST /v1/command` and `gatos/command` reach exactly the action
> set the `/sim` control files build). When you add a `/sim` read, add it to `SimJson` (both
> transports get it); when you add a control/debug action, it is reachable everywhere by
> construction. Do **not** add a transport-specific read or command path. (Two read *shapes* coexist
> deliberately and are both reachable on every transport: the compact per-vessel `telemetry` doc —
> `Formats.VesselTelemetry`, frozen for the SDK — and the full raw-record snapshot via `SimJson`.)
>
> **Field-level parity** (the third shape): HTTP `/v1/fs/<path>` and MQTT `gatos/sim/<path>` mirror
> the `/sim` filesystem **leaf-by-leaf** (one endpoint/topic per scalar/`ctl`/`debug` field, with
> per-value SSE and per-field actuation). These are not a fourth definition — they are produced by
> **walking the one `/sim` VFS tree** the 9p server serves (`VfsScan.Leaves`/`Resolve`/`ReadTextAsync`/
> `WriteTextAsync` in `gatOS.NineP.Vfs`), so adding a `/sim` node lights it up everywhere with no new
> code. Blocking/growing-log files (`stream`/`events`/`alarm`, marked `VfsFile.IsStreaming`) are
> excluded from the bulk walk and keep their dedicated streaming mechanisms.
>
> A future per-data-source TOML toggle will gate which categories each transport serves; the existing
> `[http] http_field_endpoints` / `[mqtt] mqtt_field_topics` / `field_feed_hz` flags are its first
> slice, and the category-segmented `SimJson` methods keep that a localized change.

### Runtime architecture

> Full diagram, port allocation table, slirp networking, disk layout, and config sections reference
> → **[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)**

```
KSA game process                                          QEMU subprocess
┌──────────────────────────────────────────────┐         ┌──────────────────────────────┐
│ purrTTY mod (stock — M5 landed upstream)     │         │ Alpine guest (hostname gatos)│
│   TerminalWindow tabs                        │         │   OpenSSH sshd :22            │
│      ▲ ICustomShell                          │  slirp  │   ash/bash, apk, …            │
│ gatOS mod                                   │         │   /sim ← mount -t 9p tcp      │
│   SshShellSession ──SSH.NET──────────────────┼─127.0.0.1:<pSsh>──► hostfwd → :22       │
│   NinePServer (listens 127.0.0.1:<p9>) ◄─────┼── guest connects out via 10.0.2.2       │
│   SimFsTree ◄ SnapshotStore ◄ TelemetrySampler (game thread, OnBeforeGui)              │
│   VmHost (state machine) → QemuProcess, DiskManager, QgaClient, PortAllocator          │
└──────────────────────────────────────────────┘         └──────────────────────────────┘
```
All host↔guest traffic is plain TCP over QEMU user-mode (slirp) networking — deliberately no
virtio-9p / virtiofs / vsock (none exist on Windows QEMU hosts). One transport, identical on every
host.

## Threading rules (binding for every task)

1. **Game state is read *and mutated* only on the game thread** (`[StarMapBeforeGui]`). The sampler
   builds an immutable `SimSnapshot` and publishes it with a single volatile reference swap; control
   commands are *drained and executed* in the same hook (`CommandQueue.Drain` → `KsaCatalog`), so
   writes obey rule 1 exactly like reads. **Solver-phase writes** — the debug refills **and the
   flight-computer setpoints** (`vessel.attitude_mode`/`attitude_frame`/`attitude_target`/`burn`) —
   drain in a Harmony `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`
   (`Mod.DrainSolverCommands`) — still the game thread, inside the physics step (G4, built). The
   flight computer *must* drain there: KSA's async vehicle solver snapshots the whole `FlightComputer`
   at prepare and restores it at apply (`FlightComputer.CopyFrom`), so a frame-phase write lands
   outside that capture and is overwritten by the in-flight solve (the value flashes on, then reverts
   to manual). Which phase an action uses is **derived from the action key** by `SimCommand.Phase`
   (the `SimCommand.SolverActions` set is the single source of truth — every transport gets it by
   construction); never pass a phase at a construction site. **A third game-thread mutation site** is the
   welds per-frame driver (`Mod.DriveWelds`, run in `[StarMapAfterGui] OnAfterUi` after
   `JobSystems.VehicleSolver.Wait()`) — it teleports each welded source onto its anchor and self-gates to
   a no-op when no welds exist, so it needs **no** Harmony patch. The `always_render_iva` cheat installs its
   own dynamic `Harmony("gatos.iva")` patches **only while the toggle is on** (removed on disable/unload).
   The **`thug_life` cheat** (`Game/Ksa/ThugLife/`) adds gatOS's only **render-thread draw injection**: a
   dynamic `Harmony("gatos.thug_life")` postfix on `SuperMeshRenderSystem.RenderMainPass` (which KSA runs on
   the *main* thread — the same thread as the GUI hooks and the command drain, per `.agents/skills/ksa/quad.md`)
   that records a textured-quad draw per entry; it + its Vulkan GPU resources are installed lazily on the
   first entry and torn down on the last. A fourth game-thread work site, `Mod.UpdateThugLife` (run in
   `OnBeforeUi`), validates/re-resolves entry anchors before the scene renders, self-gating to a no-op when
   empty. A fifth game-thread work site is the **audio tick** (`Mod.DriveAudio` → `AudioActuator.Tick`,
   run in `OnBeforeUi` right after the command drain — the same thread that pumps FMOD via
   `GameAudio.UpdateAudio`): it prunes finished channels, enforces `end=`, releases evicted FMOD sounds
   and publishes the `/sim/audio/status` snapshot into the game-free `AudioStore`; all FMOD calls
   (create/play/set/stop + the tick) happen only there and in the Frame-phase drain, and it self-gates
   to a no-op while no channel or cached sound exists. Audio teardown rides `Mod.TeardownGameCheats`. A
   **sixth game-thread work site** is the **IVA cabin-physics driver** (`Mod.DriveIvaPhysics` →
   `IvaPhysicsManager.Update`, run in `[StarMapAfterGui] OnAfterUi` right after `DriveWelds`): like the
   welds driver it calls `JobSystems.VehicleSolver.Wait()` first so the accelerometer/rates/CoM readings
   feeding its forcing field are the settled values for the step, then steps a **gatOS-owned Bepu
   simulation** (single-threaded — `Timestep` is called with no `IThreadDispatcher`, so every Bepu
   callback runs on this thread) and writes each floating object's pose onto its driven **SubPart**. It
   needs **no** Harmony patch and self-gates to one branch (`IvaPhysicsManager.IsIdle`) while the
   `/sim/debug/iva/enabled` master switch is off or nothing is adopted — which is the default, and in
   that state no physics simulation, interior mesh or buffer pool exists at all. gatOS never adds a body
   to KSA's own `ConstraintSim` (see `scope/ksa-runtime-coupling.md#iva-cabin-sim` for why: a cabin body
   would be ejected through the hull *and* shove the spacecraft, the sim is not stepped for a coasting
   vessel, and it runs on solver worker threads). IVA teardown rides `Mod.TeardownGameCheats`.
   The **per-vessel `always_render` override** (`Game/Ksa/Render/VesselForceRender.cs`) follows the
   same discipline: dynamic `Harmony("gatos.always_render")` prefixes on `Vehicle.GetWorldMatrix`/
   `UpdateRenderData` (the sub-pixel cull bypass) installed **only while ≥ 1 vessel is marked** and removed
   on the last unmark/despawn-prune/unload; its registry is mutated only on the game thread and read by the
   prefixes through one volatile immutable set (despawn pruning rides the sampler's vehicle enumeration).
   A **seventh game-thread work site** is the **schedule tick** (`Mod.TickSchedules` →
   `ScheduleStore.{Activate,AdvanceAll,Tick}`, run in `DrivePerFrame` *immediately before* the command
   drain, so a command that falls due on this frame executes on this frame and not the next): it sources
   all three clock bases (`render` = the frame's `dtPlayer`, `wall` = a host `Stopwatch` parked while the
   registry is empty, `ut` = the sim-time delta, clamped at 0 on a rewind), posts what came due through
   `CommandQueue.Post`, and self-gates to two integer compares while nothing is live. It touches **no KSA
   type** — the whole scheduler is game-free (`gatOS.SimFs/Commands/`) and needs **no** Harmony patch. An
   **eighth game-thread work site** is the **camera director** (`Mod.DriveCamera` →
   `CameraDirector.Update`, run at the *end* of `[StarMapAfterOnFrame] Mod.OnAfterFrame` —
   **unconditionally, on every rendered frame**, unlike every other driver, which stands in only on the
   frames the GUI hooks were skipped). It must run there for two reasons: the pose has to be written
   *after* the render so the **next** frame's `Program.OnFrameViewports` rebuilds the view/projection
   matrices from it — which is precisely what lets gatOS own the camera with **no** Harmony patch — and a
   camera that stopped moving the moment the player hid the UI would be useless for the job it exists to
   do. It self-gates to a single branch (`CameraDirector.IsIdle`) while gatOS does not own the camera —
   the default — and in that state no camera is read, no pose composed and nothing published;
   `CameraState` and the director's fields are game-thread-only with no locks by design, transport threads
   only enqueue `SimCommand`s (no camera action is in `SimCommand.SolverActions`) and read the volatile
   `CameraStore.Status` the director publishes with one swap. Despawn pruning rides the sampler's vehicle
   enumeration (`CameraDirector.Prune`, beside `VesselForceRender.Prune`). Camera teardown rides
   `Mod.TeardownGameCheats`. All cheats are torn down by `Mod.TeardownGameCheats` at `Unload`.

   **The F2 / `DrawUI` fix (C0.1) — why a third StarMap hook exists.** StarMap implements
   `[StarMapBeforeGui]`/`[StarMapAfterGui]` as patches on `Program.OnDrawUiFrame`/`OnDrawUiViewports`,
   whose only call sites sit inside `Program.OnFrame`'s `if (DrawUI)` block — and **F2 toggles `DrawUI`**.
   So hiding the UI used to stop the telemetry sampler, the command drain, the audio tick, the thug-life
   updater, the welds driver and the IVA physics driver *dead*. `[StarMapAfterOnFrame] Mod.OnAfterFrame`
   is a postfix on `Program.OnFrame` itself: it always runs, exactly once per rendered frame. The bodies
   of the two GUI hooks were split into `Mod.DrivePerFrame` (sample → `TickSchedules` → drain →
   `DriveAudio` → `UpdateThugLife`) and `Mod.DrivePostSolver` (`DriveWelds` → `DriveIvaPhysics`), and
   `OnAfterFrame` re-runs **both** — but only on the frames the GUI hooks were skipped, decided by a
   boolean latch `OnBeforeUi` sets and `OnAfterFrame` clears (not a frame-number compare: `Program`
   is unreferenceable from the game-free half of the partial class, and `FrameNumber` is bumped before
   the postfix anyway). `DrawGameUi()` is deliberately **not** re-run — with `DrawUI` false there is no
   ImGui frame to draw into. `DriveCamera` then runs unconditionally, after both.
2. **9p server threads never touch game state** — they read the latest published snapshot, and for
   writes they only *enqueue* an immutable `SimCommand` and await its result (never executing it).
3. SSH I/O runs on SSH.NET's threads; `OutputReceived` may fire on any thread (purrTTY tolerates
   this — its `Surface.Write` is the one thread-safe entrypoint).
4. `VmHost` is an async state machine guarded by one `SemaphoreSlim`; concurrent
   `EnsureStartedAsync` callers await the same in-flight boot task.
5. Nothing in gatOS ever blocks the render thread: menu/draw code reads cached state (volatile
   fields) only; all VM operations are async or background.

## Runtime telemetry tuning (cadence + per-stream gates)

> Full pipeline diagram, per-gate cost table, and config key reference →
> **[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)**

The data feed has **one master cadence** (`sample_rate_hz`, default 10, clamped 1–120) and
**per-stream gates** (`telemetry_enabled` master + `telemetry_vessel_detail` /
`telemetry_vessel_parts` / `telemetry_bodies` / `telemetry_events`), all tunable from config and live
in-game via the Telemetry submenu and status window slider. Gating **at the sampler** is deliberate — a
disabled stream skips its KSA reads *and* shrinks the published snapshot, so every transport serves less
by construction (transport-parity stays structural). `telemetry_vessel_detail` is the big lever: off
drops the entire G3 enrich pass, leaving only core flight telemetry. `telemetry_vessel_parts` gates the
per-vessel `parts/` list — top-level parts with their subparts nested at `parts/<n>/subparts/<m>/`, plus
the `parts/json` whole-tree JSON document (the welds anchor picker; either level's `instance_id` is a
valid weld anchor; cached per vehicle, rebuilt on part-count change or every 10 s — `parts/json`
re-serializes only on that rebuild, memoized by list reference). The status window's Telemetry block shows `PerfStat` readouts (sample-time
avg/max/last, command-drain avg/max, MQTT publish avg/max) recorded allocation-free.

## Conventions (decided — do not re-litigate; see OS_PLAN.md Part 1)

- **.NET 10 / C# 13**, `Nullable enable`, `ImplicitUsings enable` (all from
  `Directory.Build.props`). **Zero-warning policy: no build warnings of any kind are allowed.**
  Compiler (CS) warnings are errors except CS1591, and `MSBuildTreatWarningsAsErrors` makes
  MSBuild-level warnings (e.g. MSB3277 reference-version conflicts) errors too. Fix the cause,
  never suppress: e.g. when a NuGet transitive pin conflicts with the KSA/purrTTY 10.x assemblies,
  lift it with a direct `PackageReference` in the project that owns the dependency (see SSH.NET →
  `Microsoft.Extensions.Logging.Abstractions` 10.0.0 in `gatOS.Ssh.csproj`). Doc-comment `cref`s
  must resolve from the project's own references — use `<c>…</c>` for cross-assembly names a
  project doesn't reference.
- KSA reference DLLs resolve through `KSAFolder` (env `KSA_DLL_DIR` → sibling `ksa-game-assemblies`
  checkout → per-OS default), referenced with `<Private>false</Private>` and guarded by
  `Condition="Exists(...)"`.
- Mod deploy dir honors `GATOS_DIST_DIR` (CI zips it), else the per-OS KSA mods dir, producing
  `<dist>/gatOS/`. Runtime user-writable data lives under
  `MyDocuments/My Games/Kitten Space Agency/mods/gatOS/` — **centralized in `GatOsPaths`
  (`gatOS.Vm`); never hardcode filesystem locations elsewhere.**
- Logging: every game-free library logs through `gatOS.Logging`'s `ModLog` (Console-backed by
  default); `GameMod` swaps in a game-backed sink via `ModLog.SetLogger`. Never take a game-assembly
  dependency from a library project.
- Identity (D11): mod id/folder **`gatOS`**, entry assembly **`gatOS.GameMod`**, shell id
  **`"gatos"`**, guest hostname **`gatos`**.
- The vendored purrTTY contract DLLs (`vendor/purrTTY/`) are the **pinned inter-mod ABI** —
  refresh only deliberately (see `vendor/purrTTY/README.md`). At runtime gatOS shares purrTTY's
  loaded copies over the StarMap ALC (D6) via mod.toml `ImportedAssemblies` — the loader
  consults dependency-mod ALCs before the mod-local resolver. `GameMod` references them with
  `<Private>true</Private>` anyway: that puts the vendored copies in the dist **and** in
  deps.json (StarMap resolves a mod's own files through `AssemblyDependencyResolver`, which
  only sees deps.json entries), so gatOS still loads — registering into a registry nobody
  consumes — when purrTTY is absent.
- Commits: small, per-task, message starts with the task id (e.g. `T3.4: qemu readiness probe`).

## The `/sim` API contract (binding constitution)

`SPEC_9P_FILESYSTEM.md` (repo root) is the **single, authoritative catalog of the `/sim` API**: every
9p path, value format and unit, read/write archetype, command action key (`ordinal`/`value`/`values`/
`token` + Frame/Solver phase), errno mapping, and the HTTP `/v1` + MQTT mirrors. It is a **published,
user-facing API surface** — guests, modders and the `gatos` skill script against it.
`SPEC_MCP.md` is the companion public contract for the MCP server's intentionally non-1:1 logical
JSON resources and tools. It shares the same model but not the filesystem's per-leaf design.

**MUST — keep the SPEC in lockstep with the code.** In the *same change* that you add, remove, rename,
or alter the format/units/phase/semantics of any of the following, you MUST update
`SPEC_9P_FILESYSTEM.md` (and `docs/KSA_INTEGRATION_MATRIX.md` when the KSA binding moves):

- a `/sim` node or directory (`SimFsTree.cs`), including per-module files;
- a value **format** or **unit** (`Formats.cs`, `SimSnapshot` field semantics);
- a `ctl/…` control, a `debug/…` action, or a command **action key** / its argument shape / its
  **phase** (`KsaCatalog.cs`, `SimCommand.SolverActions`, the actuators);
- an HTTP `/v1` route, MQTT topic, MCP resource/tool/schema, or a config gate that changes availability;
- the errno mapping (`CommandResult.cs`) or a file's archetype.

The code wins; the specifications mirror it — they must never disagree. This is structural, not
optional: the transport-parity rule already keeps one read surface (`SimJson`/`Formats`) and one
write surface (`SimCommand`/`CommandQueue`). The MCP capability registry maps that shared model to its
logical resources/tools; update `SPEC_MCP.md` whenever a changed read/action affects that map.
`/sim/display` is the explicit MCP-v1 exclusion. The `gatos` skill (`.agents/skills/gatos/`) and its sidecars (`coordinate-frames.md`,
`flight-programs.md`, `recipes.md`) point at the SPEC; refresh them when a change affects how programs
are written.

### `/sim` schema-change playbook (binding)

The constitution above says when the contract must change. This operational playbook retains the archived implementation safeguards for anyone touching `gatOS.SimFs/SimFsTree.cs`, `gatOS.SimFs/Commands/`, `gatOS.GameMod/Game/Ksa/`, `SPEC_9P_FILESYSTEM.md`, or integration/scope docs.

- Paths and action keys are lower snake_case; collections are plural; a qid is the unique, stable `/sim`-relative path. Prefer one writable knob per leaf over a settings blob.
- Never add a second read or write path. Transport threads enqueue immutable `SimCommand`s through `ICommandSink`/`CommandQueue`; the game thread dispatches through `KsaCatalog`. Reads project immutable snapshots or a volatile-published dedicated store. Derive phase only from `SimCommand.SolverActions`; never set it at construction. MCP is a logical projection of this seam, never a `/sim` path mirror or a second actuator router.
- Choose controls deliberately: `FlagControl` for booleans, `FractionControl` for `[0,1]`, `NumberControl` for finite scalars, `VectorControl` for exact arity, `EnumControl` for fixed tokens, `TokenControlFile` for free tokens, `LineControlFile` for mixed grammars, `TriggerFile` for impulses, and the global `/sim/ctl/batch` for atomic groups. Invalid 9p input must fail with `EINVAL` before enqueue; repeat the validation game-side because `/v1/command` bypasses the file parser. Put shared validation in a game-free `<Feature>Rules` class.
- Use one addressing model: global (empty `VesselId`), registry-keyed (`Ordinal` plus `Token`/`Aux`), or path-implied per-vessel (`VesselId`, with module index in `Ordinal`). Non-debug actions intended for an addressed inactive vessel must be explicitly added to `KsaCatalog.AnyVesselActions`.
- Actuators are game-thread-only `internal static` `CommandResult` methods in `Game/Ksa/` with complete `[KsaAnchor]` metadata. Dynamic Harmony patches install lazily and are removed on final use, despawn prune, and unload. Restore pristine mutable values on reset where applicable; map bad input/missing/degraded/faulted work to `Invalid`/`NotFound`/`Unsupported`/`Fault` and latch health on faults.
- Read back small globals via `SimSnapshot`, registries via `IReadOnlyList<TSnapshot>`, per-vessel state through `VesselSnapshot`, and host-side/off-cadence/bulk state through a dedicated store. Use `Line(...)` for snapshot values and `LiveLine(...)` for values that can change between publishes. Animation-rate leaves must tolerate 10-60 Hz fire-and-forget writes, provide live resync and reset behavior, and use `/sim/ctl/batch` for atomic multi-knob changes.
- A new config key requires a documented `GatOsConfig` property, `Sections` entry, clamp-and-warn load behavior, and a synchronized `gatOS.GameMod/Configuration/gatos.default.toml` block. Debug features normally use `[control] debug_namespace` unless they introduce their own cost/capability boundary.
- In the same change, update the applicable specifications (`SPEC_9P_FILESYSTEM.md` and/or `SPEC_MCP.md`), KSA integration matrix as applicable, `scope/FULL_SCOPE.md` plus relevant scope pages, `docs/VALIDATION.md` for game-coupled validation, `docs/MILESTONES.md`, and this file if status/guidance changes. Refresh `.agents/skills/gatos/` and `docs/TUTORIAL_DATA_REFERENCE.md` when program-authoring behavior changes. For MCP, extend the capability-coverage tests whenever a shared logical read, action, store feature, or intentional exclusion changes.
- In `gatOS.SimFs.Tests`, cover read-back, write-to-the-exact `SimCommand` (including phase), invalid-input `EINVAL` with no submitted command, and table-driven rules validation. Extend `SimFsTreeTests.ControlEnabledTree_ExposesEveryModuleControlStatusAndDebugPath` for every new path. Finish with the full build and test commands green with zero warnings.

## Instruction Maintenance Mandate (MUST)

Whenever you make meaningful repository changes, you MUST evaluate and update **this file and the
relevant `docs/` page** in the same work item if it affects: project structure/dependencies, the
host↔guest seam, build/test/deploy commands, the threading rules, **the `/sim` API surface (update
`SPEC_9P_FILESYSTEM.md` — see the constitution above)**, **any gatOS feature or its KSA integration
binding (update the matching `scope/` page — see below)**, or **milestone/feature status**.
As each milestone lands, update the status table above and add full detail to
[`docs/MILESTONES.md`](docs/MILESTONES.md) — prefer verified code paths over the plan when
documenting behavior. Update [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) when the runtime
shape, port allocation, or telemetry pipeline changes. Keep [`SPEC_MCP.md`](SPEC_MCP.md) in lockstep
when an MCP resource/tool/schema, its shared read/write coverage, config gate, or deliberate
exclusion changes. Remove defunct guidance immediately. Do not
document planned-but-unbuilt code as if it exists.

**`scope/` is binding (MUST).** [`scope/`](scope/FULL_SCOPE.md) is the catalog of every gatOS feature
and exactly how it couples to the KSA game — the reference used to decide whether a game update breaks
gatOS, and where. Keep it in lockstep with the code, in the **same work item**, whenever you:
- add / remove / rename a gatOS feature, a `/sim` node, a transport endpoint, or a config gate → update
  [`scope/FULL_SCOPE.md`](scope/FULL_SCOPE.md) (the inventory) **and** the relevant `scope/*` page;
- add / move / retype / change-the-semantics-of any KSA binding (a `[KsaAnchor]`, a reader/actuator, a
  Harmony hook target, a reflection accessor, a frame/numerics use) → update the matching row **and its
  game-version status** in [`scope/ksa-read-surface.md`](scope/ksa-read-surface.md) /
  [`scope/ksa-write-surface.md`](scope/ksa-write-surface.md) /
  [`scope/ksa-runtime-coupling.md`](scope/ksa-runtime-coupling.md), alongside the `[KsaAnchor]` and
  [`docs/KSA_INTEGRATION_MATRIX.md`](docs/KSA_INTEGRATION_MATRIX.md);
- bump the KSA build or run the version-diff playbook → record decomp/asset/version findings in
  [`scope/ksa-assets-and-versions.md`](scope/ksa-assets-and-versions.md), and capture any resulting gaps
  in a `plans/` plan.

The `[KsaAnchor]` attributes remain the source of truth; `scope/` is the human, cross-referenced mirror
and break-impact view — they must never disagree (the same lockstep discipline the `/sim` SPEC constitution
imposes).
