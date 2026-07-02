# CLAUDE.md

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
`tail -f`, `jq`, awk pipelines) becomes the game API surface. Persistence is qcow2 overlays, one
per save profile, on top of a pristine shipped base image.

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
transport, host folder mounts (`/mnt/<name>`), and the welds / `always_render_iva` / parts-listing
cheats ported from `unscience`, are code-complete.** The only pending work is a set of in-game passes
(T6.6/T9.3/G1–G4, plus the welds/IVA/parts checklist) that require a live KSA flight; checklists are in
[`docs/VALIDATION.md`](docs/VALIDATION.md). The purrTTY tip release is now cut.

| Milestone | Status | Key entry points |
|---|---|---|
| M0 — scaffold | DONE | `gatos.slnx`, `Directory.Build.props`, `GatOsPaths` |
| M1 — spike | DONE | `spike/NOTES.md` (**required reading** before M3/M4/M7/M8) |
| M2 — guest image | DONE | `guest/build-image.sh`, `guest/fetch-guest.*`, `GUEST_VERSION`=15 |
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
| Host folder mounts | DONE | `NineP/Vfs/HostDirectory.cs`, `HostFile.cs` |
| Welds + `always_render_iva` + parts (ex-`unscience`) | Code DONE; in-game pending | `Game/Ksa/Welds/`, `Game/Ksa/Render/IvaForceRender.cs`, `Game/Ksa/Readers/PartsReader.cs` |
| `thug_life` sunglasses quad (ex-`unscience`) | Code DONE; in-game pending | `Game/Ksa/ThugLife/` (GPU quad renderer + dynamic render postfix), `SimFs` `debug/thug_life` |
| Screen stream (`/sim/display`) | Code DONE; misrender **root-caused + fixed** (purrTTY libghostty `o=z` corruption → default `rgba`, + purrTTY content-hash re-decode; STREAM_PLAN.md §11); **perf/stability P0–P7 of [`plans/PERF_IMPROVEMENT_PLAN.md`](plans/PERF_IMPROVEMENT_PLAN.md) landed 2026-07-02, confirmed working in-game (informal pass)** (SSH read-pump, a=t keyframes, GPU blit downscale, zero-alloc encoder, demand pacing, 9p pooling + msize 512 KiB/guest v15, purrtty consumption fixes, P6: the purrTTY native rebuilt from ghostty main + `purrtty/vt-video-fixes` — the zig-0.15.2 `o=z` flate corruption and the placement-pin leak are FIXED, so `display_encoding` defaults to `rgba-zlib` again, 3–10× less wire; and P7: the native APC bulk lane, 82→1185 MiB/s consumption throughput); formal S6/S9 + P8 soak checklists still open | `SimFs/Display/`, `Game/Ksa/FrameCapture.cs` + `DisplayRenderPatch.cs` (in-band render-hook capture), `STREAM_PLAN.md` |
| T11.1 — QEMU win-x64 | DONE | `tools/fetch-qemu.*`, `vendor/qemu/win-x64/` |
| M10+ | **Not yet implemented** | — |

The full `GATOS_IT=1` integration suite ran 321/321 green (Windows/TCG, guest v3, 2026-06-13).
Since then guest v10 added the `coreutils` package, whose GNU `tail` shadowed busybox `tail` in
PATH and broke `tail -f` on the 9p `/sim` mount (GNU `tail -f` follows via inotify, which v9fs
never delivers for the host-side appends that grow `stream`/`events`/`alarm`) — failing
`SimMountIntegrationTests`; fixed in guest **v14** by the `usr/local/bin/tail` poll-mode shim
(verified against a live mount, Windows/TCG, 2026-06-20); guest **v15** raises the /sim + /mnt mount
msize to 524288 to match the 9p server's raised ceiling (plans/PERF_IMPROVEMENT_PLAN.md P4). **M10 (persistence & savegame) is next.**

> **`spike/NOTES.md` is REQUIRED READING before any M3/M4/M7/M8 work** — notably: `i_size`
> must be truthful on ≥6.11 kernels; two distinct file models exist (growing-log `tail -f` vs
> blocking-event `cat`).

## Build and Test Commands

```bash
dotnet build gatos.slnx                          # build the whole solution
dotnet test  gatos.slnx --nologo -v quiet        # full suite (5 test projects)
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
gatos.slnx                      XML solution (17 projects: 9 libs/mod + 8 test projects)
Directory.Build.props           shared build config + KSA/dist path resolution
CLAUDE.md / README.md           this file; user-facing readme
OS_IDEA.md / OS_ANALYSIS.md / OS_PLAN.md   goals / research / execution plan
KSA_GAME_INTEGRATION_PLAN.md    proposed plan: /sim read/write expansion, control files, HTTP +
                                bus transports, KSA-churn integration layer (G-series phases)
SPEC_9P_FILESYSTEM.md           THE catalog of the /sim 9p API surface: every path, format, unit,
                                read/write semantic, command action key, errno, HTTP /v1 + MQTT
                                mirror. The reference for writing programs against /sim; kept in
                                lockstep with the code (its own constitution). The gatos skill
                                (.claude/skills/gatos/) references it.
docs/MILESTONES.md              full per-milestone build detail (class names, as-built notes)
docs/ARCHITECTURE.md            runtime architecture, port allocation, telemetry pipeline/tuning
docs/KSA_INTEGRATION_MATRIX.md per-point KSA API reference (G1–G4 + documented deferrals)
docs/VALIDATION.md              in-game validation record (T6.6/T6.7 checklists + results)
docs/KSA_CELESTIAL_COORDINATE_FRAMES.md details on the KSA games coordinate frame systems for frames of reference 
scope/                          game-integration scope catalog (scope/FULL_SCOPE.md entrypoint + the
                                ksa-*/non-ksa-surface pages): EVERY gatOS feature ↔ its KSA binding,
                                with decompiled-source + Content-asset paths and the game-update
                                break-check playbook. THE reference for "will a game update break
                                gatOS, and where?" Kept in lockstep with the code (see the mandate).
plans/                          active execution plans (e.g. FIX_CURRENT_GAPS_PLAN.md — the gaps a
                                game update introduced and how to close them)
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
                                                      Vector/Enum/Number/Token control files — G1+G4, built);
                                                      Display/ (the /sim/display screen stream: DisplaySettings,
                                                      KittyEncoder, DisplaySurface, DisplayStreamFile +
                                                      control files — STREAM_PLAN.md, built; capture in GameMod)
gatOS.Http     → SimFs, Logging                       magic HTTP /v1 server (raw TcpListener; G5, built)
gatOS.Bus      → SimFs, Logging                       serial/bus framing CCSDS/NMEA/SCPI + the gatos.serial
                                                      SerialBridge/Connector over QEMU virtio-serial (G7, built)
gatOS.Mqtt     → SimFs, Logging, MQTTnet              embedded MQTT broker over the same store+sink (built)
gatOS.Vm       → Logging, Tomlyn                      QEMU lifecycle, disks, ports, GatOsPaths (M3, built)
gatOS.Ssh      → Vm, Logging, vendor/purrTTY, SSH.NET SshShellSession : ICustomShell (M4, built)
gatOS.GameMod  → Ssh, SimFs, Http, Mqtt, Bus, Vm, Logging, vendor/purrTTY,
                  KSA DLLs, StarMap.API, Lib.Harmony, ModMenu.Attributes, Tomlyn   the KSA mod (M6, built)
                  (+ the Brutal.Vulkan(.Abstractions/.Vma) + Planet.Render.Core + Brutal.Core.Memory game
                   DLLs and AllowUnsafeBlocks, for the Game/Ksa/ThugLife GPU quad renderer and the
                   Game/Ksa/FrameCapture screen-stream readback)
```
`examples/sdk-ts/` is a standalone TypeScript/Bun example SDK (G6, built — not part of the .NET
solution); it talks to either transport behind one typed API.
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
> `gatOS.GameMod/Game/Ksa/`** (`Readers/`, `Actuators/`, `Welds/`, `Render/`, `ThugLife/`, `KsaCatalog`,
> annotated with `[KsaAnchor]`). Transports (9p/HTTP/serial), the `/sim` tree, formats and the command pipeline
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
   `JobSystems.VehicleSolvers.Wait()`) — it teleports each welded source onto its anchor and self-gates to
   a no-op when no welds exist, so it needs **no** Harmony patch. The `always_render_iva` cheat installs its
   own dynamic `Harmony("gatos.iva")` patches **only while the toggle is on** (removed on disable/unload).
   The **`thug_life` cheat** (`Game/Ksa/ThugLife/`) adds gatOS's only **render-thread draw injection**: a
   dynamic `Harmony("gatos.thug_life")` postfix on `SuperMeshRenderSystem.RenderMainPass` (which KSA runs on
   the *main* thread — the same thread as the GUI hooks and the command drain, per `.claude/skills/ksa/quad.md`)
   that records a textured-quad draw per entry; it + its Vulkan GPU resources are installed lazily on the
   first entry and torn down on the last. A fourth game-thread work site, `Mod.UpdateThugLife` (run in
   `OnBeforeUi`), validates/re-resolves entry anchors before the scene renders, self-gating to a no-op when
   empty. All cheats are torn down by `Mod.TeardownGameCheats` at `Unload`.
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
per-vessel top-level `parts/` list (the welds anchor picker; cached per vehicle, rebuilt on part-count
change or every 10 s). The status window's Telemetry block shows `PerfStat` readouts (sample-time
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

**MUST — keep the SPEC in lockstep with the code.** In the *same change* that you add, remove, rename,
or alter the format/units/phase/semantics of any of the following, you MUST update
`SPEC_9P_FILESYSTEM.md` (and `docs/KSA_INTEGRATION_MATRIX.md` when the KSA binding moves):

- a `/sim` node or directory (`SimFsTree.cs`), including per-module files;
- a value **format** or **unit** (`Formats.cs`, `SimSnapshot` field semantics);
- a `ctl/…` control, a `debug/…` action, or a command **action key** / its argument shape / its
  **phase** (`KsaCatalog.cs`, `SimCommand.SolverActions`, the actuators);
- an HTTP `/v1` route or MQTT topic, or a config gate that changes availability;
- the errno mapping (`CommandResult.cs`) or a file's archetype.

The code wins; the SPEC mirrors it — they must never disagree. This is structural, not optional: the
transport-parity rule already keeps one read surface (`SimJson`/`Formats`) and one write surface
(`SimCommand`/`CommandQueue`), so a `/sim` change is a single place in code **and** a single place in
the SPEC. The `gatos` skill (`.claude/skills/gatos/`) and its sidecars (`coordinate-frames.md`,
`flight-programs.md`, `recipes.md`) point at the SPEC; refresh them when a change affects how programs
are written.

## Instruction Maintenance Mandate (MUST)

Whenever you make meaningful repository changes, you MUST evaluate and update **this file and the
relevant `docs/` page** in the same work item if it affects: project structure/dependencies, the
host↔guest seam, build/test/deploy commands, the threading rules, **the `/sim` API surface (update
`SPEC_9P_FILESYSTEM.md` — see the constitution above)**, **any gatOS feature or its KSA integration
binding (update the matching `scope/` page — see below)**, or **milestone/feature status**.
As each milestone lands, update the status table above and add full detail to
[`docs/MILESTONES.md`](docs/MILESTONES.md) — prefer verified code paths over the plan when
documenting behavior. Update [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) when the runtime
shape, port allocation, or telemetry pipeline changes. Remove defunct guidance immediately. Do not
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
