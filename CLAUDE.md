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
shim, `GatOsPaths`, the vendored purrTTY contract, and CI are in place and green.

**M1 — de-risking spike: DONE.** All three gates passed against a real Alpine 3.24 guest (kernel
6.18): 9p synthetic files `cat`/`tail -f`/Ctrl-C-Tflush from the kernel's v9fs client against a
hand-rolled C# 9P2000.L server; SSH.NET 2025.1.0 shell with **live resize** against dropbear; a
known-good QEMU invocation. The spike's throwaway code was deleted when M2 landed (per plan);
**`spike/NOTES.md` (committed) records the learnings and is REQUIRED READING before
M3/M4/M7/M8 work** — notably: i_size must be truthful (the analysis §3.6 fake-size advice is
wrong on ≥6.11 kernels), a read() completes only on buffer-full or two consecutive 0-byte Rreads,
and "growing-log" (`tail -f`) vs "blocking-event" (`cat`) synthetic files are two distinct models
the M7 VFS must support.

**M2 — guest image pipeline: DONE.** `guest/build-image.sh` reproducibly builds the guest from
pinned Alpine 3.24 mirrors — no setup-alpine, no openrc; busybox init runs the hand-written
`guest/rootfs-overlay/` (static slirp net 10.0.2.15, dropbear key-only, qemu-ga via wrapper,
`sim-mount` 9p supervisor driven by the `gatos.simport=<port>` kernel cmdline, 0/absent = idle).
Artifacts in `guest/out/` (never committed): partitionless-ext4 `base.qcow2` (zstd qcow2),
`vmlinuz-virt`, trimmed `initramfs-virt` (`features="base virtio ext4"`), `manifest.toml` (the
host boot contract: kernel cmdline, ssh user/key, host-key pin = sha256 hex of the raw key blob),
baked ed25519 session keypair, `sha256sums.txt`. Build needs root on Linux (macOS dev: Docker;
both documented in `guest/README.md`); a built-in smoke test (also `--smoke-only`) boots the
artifacts, checks `ssh 'echo ok'`, **verifies the host-key pin**, and powers off — measured cold
boot→sshd **5 s under TCG** on the dev Mac. `.github/workflows/guest-image.yml` builds and
publishes GitHub release `guest-v<N>` (N = `guest/GUEST_VERSION`); consumers obtain artifacts via
`guest/fetch-guest.{sh,ps1}` (checksum-verified, no-op when current).

Everything past M2 is **not yet implemented** — the library projects (`NineP`, `SimFs`, `Vm`,
`Ssh`) and `GameMod` hold placeholder/skeleton types only. Track real progress against the
milestone table in `OS_PLAN.md` Part 3; do not document planned code here as if it exists.

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
guest/                          guest image pipeline (M2, built): build-image.sh,
                                fetch-guest.{sh,ps1}, GUEST_VERSION pin, rootfs-overlay/,
                                README.md; guest/out/ NOT in git (fetch or build it)
tools/                          fetch-qemu.{sh,ps1} (M11)
.github/workflows/build.yml     CI: build + test on every push
.github/workflows/guest-image.yml  CI: build + publish guest-v<N> release (guest/** pushes)
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
  loaded copies over the StarMap ALC (D6), so `GameMod` references them with `<Private>false</Private>`.
- Commits: small, per-task, message starts with the task id (e.g. `T3.4: qemu readiness probe`).

## Instruction Maintenance Mandate (MUST)

Whenever you make meaningful repository changes, you MUST evaluate and update this file in the same
work item if it affects: project structure/dependencies, the host↔guest seam, build/test/deploy
commands, the threading rules, or **milestone/feature status**. As each milestone lands, move it
from "not yet implemented" to documented reality and add the concrete navigation pointers (class
names, files) — prefer verified code paths over the plan when documenting behavior. Remove defunct
guidance immediately. Do not document planned-but-unbuilt code as if it exists.
