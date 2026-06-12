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

The architecture and the research behind it are fixed in **`OS_ANALYSIS.md`** (options considered,
why QEMU won); **`OS_PLAN.md`** is the execution plan (milestones M0–M12, fine-grained tasks). Read
`OS_PLAN.md` Part 0 before starting any task — it defines the execution model, repo conventions,
and the decisions locked in (Part 1).

> **Sibling repo:** `../purrtty` is the structural reference (csproj/slnx/CI patterns) and the
> source of truth for the vendored contract assembly. KSA decompiled sources are under
> `thirdparty/ksa/`; the `ksa` skill documents the mod lifecycle and telemetry APIs.

## Current status (what is actually built)

**M0 — repository scaffold: DONE.** The solution, all 11 projects, shared build config, the logging
shim, `GatOsPaths`, the vendored purrTTY contract, and CI are in place and green. Everything past
M0 is **not yet implemented** — the library projects (`NineP`, `SimFs`, `Vm`, `Ssh`) and `GameMod`
hold placeholder/skeleton types only. Track real progress against the milestone table in
`OS_PLAN.md` Part 3; do not document planned code here as if it exists.

## Build and Test Commands

```bash
dotnet build gatos.slnx                          # build the whole solution
dotnet test  gatos.slnx --nologo -v quiet        # full suite (5 test projects)
dotnet build gatOS.Vm                            # one project
```

Every task ends with **both** the build and the test suite green. Keep test output minimal (no
Console spew from passing tests). Integration tests that need a real VM are gated by the `GATOS_IT=1`
env var and self-skip (`Assert.Ignore`) otherwise, so plain `dotnet test` never needs QEMU (this
convention arrives with M3).

## Repository layout & project map

```
gatos.slnx                      XML solution (all 11 projects)
Directory.Build.props           shared build config + KSA/dist path resolution
CLAUDE.md / README.md           this file; user-facing readme
OS_IDEA.md / OS_ANALYSIS.md / OS_PLAN.md   goals / research / execution plan
LICENSE                         MIT (the mod's own code)
THIRD-PARTY-NOTICES.md          QEMU GPLv2, Alpine, SSH.NET, Tomlyn, …
vendor/purrTTY/                 pinned contract DLLs (committed) — see its README for the pin
vendor/qemu/                    NOT in git — fetched win-x64 QEMU (tools/fetch-qemu.*, M11)
guest/                          guest image build pipeline (M2); guest/out/ NOT in git
tools/                          fetch-qemu.{sh,ps1} (M11)
.github/workflows/build.yml     CI: build + test on every push
```

### Projects and the dependency rule

```
gatOS.Logging                    (no deps)            game-free logging shim
gatOS.NineP    → Logging                              9P2000.L codec + server (M7)
gatOS.SimFs    → NineP, Logging                       /sim node tree + snapshot store (M8)
gatOS.Vm       → Logging                              QEMU lifecycle, disks, ports, GatOsPaths (M3)
gatOS.Ssh      → Vm, Logging, vendor/purrTTY, SSH.NET SshShellSession : ICustomShell (M4)
gatOS.GameMod  → Ssh, SimFs, Vm, Logging, vendor/purrTTY,
                  KSA DLLs, StarMap.API, Lib.Harmony, ModMenu.Attributes, Tomlyn   the KSA mod (M6)
```
Each library has a matching `*.Tests` NUnit project (`gatOS.GameMod` has none — it is game-coupled).

> **THE dependency rule (binding):** only `gatOS.GameMod` may reference KSA / Brutal / StarMap
> assemblies. Everything else must build and test on a bare host with no game DLLs present. This is
> what keeps the 9p server, VM manager and SSH session headlessly testable (mirrors purrTTY's
> backend/frontend discipline). KSA references in `GameMod` are condition-guarded
> (`Condition="Exists('$(KSAFolder)/…')"`) so the rest of the solution still builds when the
> assemblies are absent.

### Runtime architecture (recap)

```
KSA game process                                          QEMU subprocess
┌──────────────────────────────────────────────┐         ┌──────────────────────────────┐
│ purrTTY mod (UNMODIFIED except its menu PR)  │         │ Alpine guest (hostname gatos)│
│   TerminalWindow tabs                        │         │   dropbear sshd :22           │
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

1. **Game state is read only on the game thread** (`[StarMapBeforeGui]`). The sampler builds an
   immutable `SimSnapshot` and publishes it with a single volatile reference swap.
2. **9p server threads never touch game state** — they read the latest published snapshot only.
3. SSH I/O runs on SSH.NET's threads; `OutputReceived` may fire on any thread (purrTTY tolerates
   this — its `Surface.Write` is the one thread-safe entrypoint).
4. `VmHost` is an async state machine guarded by one `SemaphoreSlim`; concurrent
   `EnsureStartedAsync` callers await the same in-flight boot task.
5. Nothing in gatOS ever blocks the render thread: menu/draw code reads cached state (volatile
   fields) only; all VM operations are async or background.

## Conventions (decided — do not re-litigate; see OS_PLAN.md Part 1)

- **.NET 10 / C# 13**, `Nullable enable`, `ImplicitUsings enable`, warnings-as-errors except CS1591
  (all from `Directory.Build.props`). Doc-comment `cref`s must resolve from the project's own
  references — use `<c>…</c>` for cross-assembly names a project doesn't reference.
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
  loaded copies over the StarMap ALC (D6), so `GameMod` references them with `<Private>false</Private>`.
- Commits: small, per-task, message starts with the task id (e.g. `T3.4: qemu readiness probe`).

## Instruction Maintenance Mandate (MUST)

Whenever you make meaningful repository changes, you MUST evaluate and update this file in the same
work item if it affects: project structure/dependencies, the host↔guest seam, build/test/deploy
commands, the threading rules, or **milestone/feature status**. As each milestone lands, move it
from "not yet implemented" to documented reality and add the concrete navigation pointers (class
names, files) — prefer verified code paths over the plan when documenting behavior. Remove defunct
guidance immediately. Do not document planned-but-unbuilt code as if it exists.
