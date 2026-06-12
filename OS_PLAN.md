# OS_PLAN — gatOS implementation plan

*Authored 2026-06-11. Companion to `OS_IDEA.md` (the goals) and `OS_ANALYSIS.md` (the research
and architecture decision). This document is the **execution plan**: fine-grained, incremental
tasks intended to be handed to coding agents one at a time (or a few in parallel where the
dependency graph allows), starting from an **empty repository**.*

**What is being built (one paragraph):** gatOS is a **standalone KSA mod** that runs a real,
minimal Alpine Linux inside a QEMU microVM subprocess. Players open terminal sessions into it
through **purrTTY** (which stays an unmodified terminal emulator, consumed only via its published
`purrTTY.CustomShellContract` extension point) over SSH. Live vehicle telemetry is exposed to the
guest as a filesystem: a C#-implemented **9P2000.L server** that the guest mounts at `/sim`, so
the entire unix toolbox (`cat`, `watch`, `tail -f`, `jq`, awk pipelines) becomes the game API.
Persistence is qcow2 overlays. Everything below follows the architecture fixed in
`OS_ANALYSIS.md` §3 — when this plan and that analysis disagree on a detail, this plan wins (it
post-dates it and resolves its open questions).

---

## Part 0 — How to use this document (read first, every agent)

### 0.1 Execution model

- The plan is divided into **milestones M0–M12**, each split into **tasks** (`T<m>.<n>`). A task
  is sized for one focused agent session: it names the files to create, the exact API surface,
  the behavior spec, and the acceptance criteria.
- **Do tasks in order within a milestone.** Milestones may be parallelized only as the
  dependency graph (§4.3) allows.
- Every task ends with the **full solution building and all tests passing**:
  ```bash
  dotnet build gatos.slnx
  dotnet test gatos.slnx --nologo -v quiet
  ```
- Keep test output minimal (no Console spew from passing tests).
- When a task says "verify against X at implementation time", that means the spec here is
  believed-correct but the agent must confirm the exact signature/format from the named source
  before coding against it, and fix this document if it differs.

### 0.2 Repository conventions (mirror of the purrTTY repo, adjusted)

These are decided; do not re-litigate:

- **.NET 10 / C# 13**, `Nullable enable`, `ImplicitUsings enable`, warnings-as-errors except
  CS1591. One repo-root `Directory.Build.props` (content given in T0.2).
- Solution file: `gatos.slnx` (XML solution format, same as `purrtty.slnx`).
- Project naming: `gatOS.<Area>`; test projects `gatOS.<Area>.Tests` (NUnit + 
  `Microsoft.NET.Test.Sdk` + `NUnit3TestAdapter`).
- Logging: every game-free library logs through `gatOS.Logging`'s `ModLog` (Console-backed
  fallback, see T0.4) — never take a dependency on game assemblies from a library project.
- KSA reference DLLs resolve through `KSAFolder` exactly like purrTTY (env var
  `KSA_DLL_DIR` → sibling `ksa-game-assemblies` checkout → per-OS default). Referenced with
  `<Private>false</Private>`, always guarded by `Condition="Exists(...)"`.
- Mod deploy dir: `GATOS_DIST_DIR` env var → per-OS KSA mods dir, producing
  `<dist>/gatOS/` (the mod folder). The dist is **platform-agnostic**: one build bundles
  everything for Windows + Linux + macOS.
- Mod data dir (runtime, user-writable):
  `MyDocuments/My Games/Kitten Space Agency/mods/gatOS/` — disks, logs, config. Centralized in
  one class (`GatOsPaths`, T0.4); never hardcode elsewhere.
- Commit style: small, per-task commits; message starts with the task id (e.g.
  `T3.4: qemu readiness probe`).
- **CLAUDE.md maintenance mandate**: the new repo gets a CLAUDE.md (T0.6). Every task that
  changes structure, commands, or behavior contracts must update it in the same commit.

### 0.3 Reference material available to agents

- The purrTTY repo (sibling checkout, `../purrtty`) — structural reference for csproj/slnx/CI
  patterns, and the **source of truth for the contract assembly** (`purrTTY.CustomShellContract`).
- `OS_ANALYSIS.md` in the purrtty repo — research, links, rejected alternatives.
- KSA decompiled sources + the `ksa` skill docs (mod lifecycle, telemetry APIs, ALC sharing).
- diod's `protocol.md` (the 9P2000.L spec): https://github.com/chaos/diod/blob/master/protocol.md
- QEMU docs: https://www.qemu.org/docs/master/ (WHPX, slirp networking, qcow2).

---

## Part 1 — Decisions locked in (resolving OS_ANALYSIS §12)

| # | Question | Decision |
|---|---|---|
| D1 | Distro | **Alpine 3.22+ x86_64**, `linux-virt` kernel (≥6.12; the 9p netfslib fix landed in 6.11). Image build script stays distro-parameterized; Debian-minbase is a M12 variant. |
| D2 | VM lifecycle | **Lazy boot** on first gatOS session open; VM survives session closes; shut down at mod unload (game exit). One VM per game process for MVP. Per-save disk selection is M10. |
| D3 | Network posture | **Open slirp NAT by default** (real apk mirrors work). `restrict = true` available in `gatos.toml` (M12 wires it to `-netdev user,restrict=on` + `guestfwd` for 9p). |
| D4 | Sim write access | **Read-only `/sim` for MVP.** Writable control files are M12, gated on a gameplay decision. |
| D5 | QEMU distribution | **Bundle QEMU for win-x64** in the mod dist (trimmed Weil build, fetched+pinned by script, not committed to git). **Linux/macOS: system QEMU required** (`qemu-system-x86_64` on PATH; clear in-game error with install hint if missing). |
| D6 | Inter-mod contract | gatOS takes a **compile-time reference to vendored copies** of `purrTTY.CustomShellContract.dll` + `purrTTY.Logging.dll` (pinned, in `vendor/purrTTY/`), and at **runtime** shares purrTTY's loaded copies via StarMap ALC `ImportedAssemblies` (§ T6.1). `Optional = true`: without purrTTY installed, gatOS loads its own copies and idles headless (VM/9p still functional, no terminal UI). |
| D7 | SSH library | **SSH.NET (Renci.SshNet) ≥ 2025.1.0** (has `ShellStream.ChangeWindowSize`). Fallbacks if it disappoints: Tmds.Ssh, or `ssh.exe` under purrTTY's ConPTY. |
| D8 | Guest auth | Image-build-time **baked ed25519 keypair** (private key ships in the mod dist, `hostfwd` bound to 127.0.0.1 only). Pre-generated dropbear host key, fingerprint pinned in the manifest. Per-install key rotation is M12. |
| D9 | Guest artifacts in git | **Not committed.** Built by `guest/build-image.sh` (Linux/CI), published as `guest-v<N>` GitHub releases of the gatos repo; dev machines and the dist build fetch the pinned version (T2.6/T11.2). |
| D10 | Multiple computers | MVP: one VM, many sessions (purrTTY tabs). The multi-computer *fiction* (per-vessel chroot/unshare) is M12. |
| D11 | Mod identity | Mod id/folder **`gatOS`**, entry assembly **`gatOS.GameMod`**, shell id **`"gatos"`**, shell display name **"gatOS"**, guest hostname **`gatos`**. New repo named **`gatos`**, sibling to `purrtty`. |
| D12 | Instant resume | `savevm`/`loadvm` is a M12 experiment only. Cold boot is the plan of record. |

---

## Part 2 — Target repository layout

```
gatos/
├── gatos.slnx
├── Directory.Build.props
├── CLAUDE.md
├── README.md
├── LICENSE                          # MIT (the mod's own code)
├── THIRD-PARTY-NOTICES.md           # QEMU GPLv2, Alpine components, SSH.NET, Tomlyn…
├── third-party-licenses/            # full license texts copied into the dist
├── .github/workflows/
│   ├── build.yml                    # mirror of purrtty release.yml (T11.4)
│   └── guest-image.yml              # builds + releases guest artifacts (T2.6)
├── vendor/
│   ├── purrTTY/                     # pinned contract DLLs + README (T0.5)
│   │   ├── purrTTY.CustomShellContract.dll
│   │   ├── purrTTY.Logging.dll
│   │   └── README.md                # provenance: purrTTY version/commit pin
│   └── qemu/                        # NOT in git; populated by tools/fetch-qemu.* (T11.1)
│       └── win-x64/...
├── guest/                           # guest image build pipeline (M2)
│   ├── build-image.sh               # the one entrypoint (Linux; CI or dev)
│   ├── fetch-guest.sh               # downloads pinned guest-v<N> release artifacts
│   ├── GUEST_VERSION                # single line: the pinned guest release number
│   ├── rootfs-overlay/              # files copied verbatim into the image (inittab, scripts…)
│   └── out/                         # NOT in git: base.qcow2, vmlinuz-virt, initramfs-virt, manifest.toml, keys
├── tools/
│   ├── fetch-qemu.sh                # pinned QEMU win-x64 fetch + trim + checksum
│   └── fetch-qemu.ps1
├── gatOS.Logging/
├── gatOS.NineP/                    # 9P2000.L codec + server + VFS abstraction (game-free)
├── gatOS.NineP.Tests/
├── gatOS.SimFs/                    # /sim node tree + snapshot store (game-free)
├── gatOS.SimFs.Tests/
├── gatOS.Vm/                       # QEMU lifecycle, disks, QGA, ports (game-free)
├── gatOS.Vm.Tests/
├── gatOS.Ssh/                      # SshShellSession : ICustomShell (game-free; refs vendor/purrTTY)
├── gatOS.Ssh.Tests/
└── gatOS.GameMod/                  # the KSA mod: lifecycle, sampler, menus, config
    ├── mod.toml
    ├── Mod.cs
    └── ...
```

### 2.1 Project dependency graph

```
gatOS.Logging                       (no deps)
gatOS.NineP      → Logging
gatOS.SimFs      → NineP, Logging
gatOS.Vm         → Logging
gatOS.Ssh        → Vm, Logging, vendor/purrTTY DLLs, SSH.NET (NuGet)
gatOS.GameMod    → Ssh, SimFs, Vm, Logging, vendor/purrTTY DLLs,
                    KSA DLLs (<Private>false), StarMap.API + Lib.Harmony + ModMenu.Attributes
                    + Tomlyn (NuGet)
```

Rule: **only `gatOS.GameMod` may reference KSA/Brutal/StarMap assemblies.** Everything else
must run on a bare test host (this is what makes the 9p server, VM manager, and SSH session
headlessly testable, mirroring purrTTY's backend/frontend discipline).

### 2.2 Runtime architecture (recap, with gatOS class names)

```
KSA game process                                          QEMU subprocess
┌──────────────────────────────────────────────┐         ┌──────────────────────────────┐
│ purrTTY mod (UNMODIFIED except T5.x menu PR) │         │ Alpine guest (hostname gatos)│
│   TerminalWindow tabs                        │         │   dropbear sshd :22           │
│      ▲ ICustomShell                          │  slirp  │   ash/bash, apk, …            │
├──────┼───────────────────────────────────────┤         │   /sim ← mount -t 9p tcp      │
│ gatOS mod                                   │         │        10.0.2.2:<p9>          │
│   SshShellSession ──SSH.NET──────────────────┼─127.0.0.1:<pSsh>──► hostfwd → :22       │
│   NinePServer (listens 127.0.0.1:<p9>) ◄─────┼── guest connects out via 10.0.2.2       │
│   SimFsTree ◄── SnapshotStore ◄── TelemetrySampler (game thread, OnBeforeGui)          │
│   VmHost (state machine) → QemuProcess, DiskManager, QgaClient, PortAllocator          │
└──────────────────────────────────────────────┘         └──────────────────────────────┘
```

Threading rules (binding for every task):

1. **Game state is read only on the game thread** (`[StarMapBeforeGui]`). The sampler builds an
   immutable `SimSnapshot` and publishes it with a single volatile reference swap.
2. **9p server threads never touch game state** — they read the latest snapshot only.
3. SSH I/O happens on SSH.NET's threads; `OutputReceived` events may fire on any thread (purrTTY
   already tolerates this — its `Surface.Write` is the one thread-safe entrypoint).
4. `VmHost` is an async state machine guarded by one `SemaphoreSlim`; concurrent
   `EnsureStartedAsync` callers await the same boot task.
5. Nothing in gatOS ever blocks the render thread: menu/draw code reads cached state
   (volatile fields) only; all VM operations are async or background.

---

## Part 3 — Milestones overview

| Milestone | Deliverable | Exit criterion |
|---|---|---|
| **M0** Repo scaffold | Solution, props, logging, vendored contract, CI skeleton, CLAUDE.md | `dotnet test` green on empty-ish projects; CI runs |
| **M1** De-risking spike | Throwaway scripts/console apps proving the 2 novel channels | 9p synthetic file `cat`/`tail -f`/Ctrl-C from a real guest; SSH shell with live resize from C# |
| **M2** Guest image pipeline | Reproducible `build-image.sh` → base.qcow2 + kernel + initrd + manifest + keys; guest-release workflow | Image boots to dropbear in <2 s accelerated; artifacts published as `guest-v1` |
| **M3** gatOS.Vm | QemuProcess, accel ladder, ports, readiness, shutdown ladder, DiskManager, QgaClient | Integration test boots/destroys a real VM on dev + CI (KVM) |
| **M4** gatOS.Ssh | `SshShellSession : ICustomShell` + VmConnectionBroker | Headless test drives a real shell over SSH incl. resize |
| **M5** Upstream purrTTY changes | Exported assemblies + dynamic custom-shell menu (in the **purrtty** repo) | purrTTY tip release with both changes |
| **M6** GameMod skeleton | mod.toml, lifecycle, registration, config, diagnostics menu, dist packaging | In-game: New Tab → gatOS → live `apk add htop` on Win + Linux |
| **M7** gatOS.NineP | Full 9P2000.L read-only server + protocol tests | Conformance suite green; Linux-mount smoke test green |
| **M8** gatOS.SimFs | `/sim` tree, snapshot store, stream/events files | Headless: mounted `/sim` shows fake telemetry; `tail -f stream` works |
| **M9** Telemetry sampler | Game-thread sampler → live `/sim` in-game | In-game: `watch -n1 cat /sim/vessels/active/altitude/radar` |
| **M10** Persistence | Overlay-per-profile, base versioning, reset UX | OS state survives game restart; reset works; update keeps overlays valid |
| **M11** Packaging & CI | QEMU fetch/trim, guest in dist, licenses, release workflow | One-zip release installs and runs on a clean Windows machine |
| **M12** Polish (à la carte) | restrict mode, control files, multi-computer, Debian, savevm, key rotation, WHP UX | per item |

### 3.1 Dependency graph between milestones

```
M0 ──► M1 ──► M2 ──► M3 ──► M4 ──► M6 ──► M9 ──► M10 ──► M11 ──► M12
              │                    ▲
              │     M5 (purrtty) ──┘
              └──► M7 ──► M8 ──────────► M9
```

Parallelizable: **M5** any time after M0; **M7/M8** in parallel with M3–M6 (different agents —
no shared files). M1 informs M2/M3/M7 but its code is throwaway.

---

# M0 — Repository scaffold

## T0.1 — Initialize repo + solution

**Goal:** empty but building solution with all projects and test projects.

**Steps:**
1. `git init gatos` (sibling of `purrtty`), default branch `main`, add `.gitignore` (standard
   dotnet + `guest/out/`, `vendor/qemu/`, `.tmp/`).
2. Create all 11 projects from §2 layout (`dotnet new classlib` / `dotnet new nunit`), wire into
   `gatos.slnx` using the XML `<Solution>` format (copy the shape of `../purrtty/purrtty.slnx`).
3. Each csproj: minimal, inherits everything from `Directory.Build.props` (next task). Library
   csprojs contain only `RootNamespace`/`AssemblyName`/`Description` and ProjectReferences per
   §2.1. Test csprojs: `<IsPackable>false</IsPackable>`, NUnit packages
   (`NUnit` 4.x, `NUnit3TestAdapter`, `Microsoft.NET.Test.Sdk`), reference to the project under
   test. Add one placeholder test per test project (`Assert.Pass()`), to be deleted as real
   tests arrive.

**Accept:** `dotnet build gatos.slnx` and `dotnet test gatos.slnx --nologo -v quiet` both green.

## T0.2 — Directory.Build.props

**Goal:** shared build config + KSA/dist path resolution, copied from purrTTY with renames.

Copy `../purrtty/Directory.Build.props` verbatim, then change:
- `PURRTTY_DIST_DIR` → `GATOS_DIST_DIR` (property `SelectedDistModDir` honors it identically).
- Keep `KSAFolder` resolution **identical** (env `KSA_DLL_DIR` → `../ksa-game-assemblies/current/dll/`
  → per-OS defaults) — gatos sits next to purrtty so the sibling checkout tier works unchanged.

**Accept:** building with `-p:KSA_DLL_DIR=/nonexistent` still succeeds for all game-free
projects (KSA references are condition-guarded and only used by GameMod).

## T0.3 — gatOS.Logging

**Goal:** the logging shim every library uses, modeled on `purrTTY.Logging`.

Files: `gatOS.Logging/ModLog.cs`.

Spec:
```csharp
namespace gatOS.Logging;
public interface IModLogger
{
    void Debug(string message); void Info(string message);
    void Warn(string message);  void Error(string message, Exception? ex = null);
}
public static class ModLog
{
    public static IModLogger Log { get; private set; } = new ConsoleLogger(); // default
    public static void SetLogger(IModLogger logger);    // GameMod swaps in a game-backed logger
}
internal sealed class ConsoleLogger : IModLogger    // Console.WriteLine("gatOS [LVL]: ...")
```
All log lines prefix `gatOS`. No file I/O here (the VM serial/qemu logs are handled by
gatOS.Vm separately).

**Accept:** unit test swaps logger, captures output.

## T0.4 — GatOsPaths + config skeleton

**Goal:** one home for filesystem locations and the user config.

Files: `gatOS.Vm/GatOsPaths.cs` (lives in Vm so game-free code can use it),
`gatOS.GameMod/Configuration/GatOsConfig.cs` (Tomlyn-backed; can be stubbed until M6 and
fleshed there — for now just create `GatOsPaths`).

```csharp
namespace gatOS.Vm;
public static class GatOsPaths
{
    // MyDocuments/My Games/Kitten Space Agency/mods/gatOS/  (created on demand)
    public static string DataDir { get; }
    public static string DisksDir   => Path.Combine(DataDir, "disks");
    public static string LogsDir    => Path.Combine(DataDir, "logs");
    public static string ConfigFile => Path.Combine(DataDir, "gatos.toml");
    // The installed mod folder (where mod.toml/guest assets live) — set once at mod init:
    public static string? ModDir { get; set; }
    public static string GuestAssetsDir => Path.Combine(ModDir!, "guest");
    public static string BundledQemuDir => Path.Combine(ModDir!, "qemu");
    public static void OverrideDataDirForTests(string dir);   // test hook
}
```

**Accept:** unit tests cover dir creation + test override.

## T0.5 — Vendor the purrTTY contract DLLs

**Goal:** compile-time reference for `gatOS.Ssh`/`gatOS.GameMod` without a cross-repo
ProjectReference.

**Steps:**
1. `dotnet build ../purrtty/purrTTY.CustomShellContract -c Release`; copy
   `purrTTY.CustomShellContract.dll` and `purrTTY.Logging.dll` into `vendor/purrTTY/`
   (these two ARE committed — they are small and pin the inter-mod ABI).
2. `vendor/purrTTY/README.md`: record source repo, commit hash, purrTTY version, the refresh
   command, and the rule: **refresh only deliberately; this pins the inter-mod API**.
3. In `gatOS.Ssh.csproj` + `gatOS.GameMod.csproj`:
   ```xml
   <Reference Include="purrTTY.CustomShellContract">
     <HintPath>$(MSBuildThisFileDirectory)../vendor/purrTTY/purrTTY.CustomShellContract.dll</HintPath>
   </Reference>
   <Reference Include="purrTTY.Logging">
     <HintPath>$(MSBuildThisFileDirectory)../vendor/purrTTY/purrTTY.Logging.dll</HintPath>
   </Reference>
   ```
   (As built: `Private=true` in **both** projects. `gatOS.Ssh` needs the copies so headless
   tests run. `gatOS.GameMod` was `Private=false` until T6.5, when the real StarMap loader
   source showed that is wrong: `ModAssemblyLoadContext` resolves a mod's own dependencies via
   an `AssemblyDependencyResolver` over the mod's deps.json, and `Private=false` refs get no
   deps.json entry — the T6.1 no-purrTTY fallback could never load. `Private=true` puts the
   vendored copies in the dist **and** deps.json; D6's type-identity sharing still holds
   because the loader consults dependency-mod ALCs (mod.toml `ImportedAssemblies`) *before*
   the local resolver, so with purrTTY installed the local copies never load.)

Key facts about the contract (verified against purrtty today; namespace
`purrTTY.Core.Terminal`):
- `ICustomShell`: `Metadata` (`CustomShellMetadata` record: Name/Description/Version/Author/
  SupportedFeatures), `IsRunning`, events `OutputReceived(ShellOutputEventArgs: ReadOnlyMemory<byte> Data)` /
  `Terminated(ShellTerminatedEventArgs: int ExitCode, string? Reason)`,
  `StartAsync(CustomShellStartOptions, CancellationToken)`, `StopAsync(CancellationToken)`,
  `WriteInputAsync(ReadOnlyMemory<byte>, CancellationToken)`,
  `NotifyTerminalResize(int width, int height)`, `RequestCancellation()`, `SendInitialOutput()`,
  `IDisposable`.
- `CustomShellStartOptions`: `InitialWidth`/`InitialHeight` (cols/rows), `WorkingDirectory`,
  `EnvironmentVariables`, `Configuration` dictionaries.
- `CustomShellRegistry.Instance.RegisterShell<T>(string shellId, Func<T> factory)` —
  **registration instantiates a probe instance via the factory, validates `Metadata`
  (all fields non-empty, `IsRunning == false`), and Disposes it.** Therefore the shell's
  constructor must be trivial and `Dispose()` must be safe on a never-started instance.
- purrTTY launches a registered shell with
  `ProcessLaunchOptions.CreateCustomGame("<shellId>")` (`ShellType.CustomGame` + `CustomShellId`).

**Accept:** solution builds with the references in place.

## T0.6 — CLAUDE.md + README + CI skeleton

**Goal:** working agreements + a CI that runs tests on every push.

1. `CLAUDE.md`: project overview (3 paragraphs from Part 0/2 of this plan), build/test commands,
   project map, the threading rules (§2.2), the "only GameMod touches game DLLs" rule, and the
   instruction-maintenance mandate (copy the wording from purrtty's CLAUDE.md).
2. `README.md`: what gatOS is, requirements (purrTTY mod, QEMU on Linux/macOS, WHP feature note
   for Windows), install steps, links to OS_ANALYSIS in the purrtty repo.
3. `.github/workflows/build.yml`: copy the **shape** of `../purrtty/.github/workflows/release.yml`
   but in M0 only: checkout, checkout `meow-sci/ksa-game-assemblies` (same
   `KSA_GAME_ASSEMBLIES_PAT` secret pattern), setup-dotnet 10, `dotnet test gatos.slnx` with TRX
   + `dorny/test-reporter`. Release packaging is added in T11.4.

**Accept:** push to GitHub (`meow-sci/gatos`), workflow green.

---

# M1 — De-risking spike (throwaway code)

> Everything in M1 lives under `spike/` and is **deleted at the end of M2** (keep the learnings
> in `spike/NOTES.md`, which T2.x/T3.x/T7.x must read). Do not polish spike code.

## T1.1 — Hand-built guest + manual QEMU launch

**Goal:** a bootable Alpine qcow2 on the dev machine and a known-good QEMU invocation.

**Steps (document each command in `spike/NOTES.md`):**
1. Download Alpine **virt** ISO (3.22+, x86_64). Create `spike/alpine.qcow2` (2 GiB). Boot the
   ISO in QEMU with the disk attached, run `setup-alpine` (disk install, no swap fine).
2. Inside the guest: `apk add dropbear qemu-guest-agent`; configure dropbear to start at boot
   (`rc-update add dropbear`); set root `authorized_keys` from a freshly generated
   `ssh-keygen -t ed25519` pair kept in `spike/`.
3. Extract `/boot/vmlinuz-virt` + `/boot/initramfs-virt` out of the image
   (`virt-copy-out` or mount via `qemu-nbd`) for direct kernel boot.
4. Launch headless with direct kernel boot (the M3 baseline — record exact working cmdline):
   ```
   qemu-system-x86_64 \
     -accel hvf -accel tcg               # (kvm on Linux; whpx on Windows) \
     -M q35 -cpu host -m 256 -smp 2 \
     -kernel vmlinuz-virt -initrd initramfs-virt \
     -append "console=ttyS0 root=/dev/vda3 rw quiet modules=virtio,ext4" \
     -drive file=alpine.qcow2,if=virtio,format=qcow2 \
     -netdev user,id=n0,hostfwd=tcp:127.0.0.1:2222-:22 \
     -device virtio-net-pci,netdev=n0 \
     -display none -serial file:serial.log -monitor none
   ```
   (root partition number depends on setup-alpine's layout — note what it actually is.)
5. `ssh -p 2222 root@127.0.0.1` works; record cold-boot wall time to sshd.

**Exit:** ssh into the guest; boot time recorded; exact cmdline + partition layout in NOTES.

## T1.2 — Throwaway 9p mini-server: prove kernel-mount of synthetic files

**Goal:** validate the single riskiest novel piece — the Linux v9fs client against a hand-rolled
C# 9P2000.L server over TCP — before investing in the real server.

Files: `spike/Spike9p/` (console app, ~300–500 lines, ugly is fine).

Implement just enough of 9P2000.L (little-endian, `size[4] type[1] tag[2]` framing) to serve a
hardcoded tree `/hello` (static text), `/ticks` (current `Environment.TickCount64` per read),
`/stream` (blocking read: completes with one `tick=<n>\n` line per second):
handlers `Tversion Tattach Twalk Tlopen Tgetattr Tread Treaddir Tclunk Tflush Tstatfs`,
everything else → `Rlerror(ENOTSUP=95)`. Listen on `0.0.0.0:5640`.

**Validation, from the T1.1 guest:**
```
modprobe 9p
mount -t 9p -o trans=tcp,port=5640,version=9p2000.L,cache=none 10.0.2.2 /mnt
cat /mnt/hello             # exact content
cat /mnt/ticks; cat /mnt/ticks   # different values (cache=none ⇒ live reads)
ls -la /mnt                # names + sizes (report fake size 4096, NOT 0 — see analysis §3.6)
tail -f /mnt/stream        # one line per second
^C                         # returns to prompt promptly  ⇒ Tflush handled
```
Also: kill the server mid-`tail` and document guest behavior (EIO expected); remount works.

**Exit (hard gate for M7):** all five behaviors confirmed; per-message byte-layout learnings
written to `spike/NOTES.md` (esp. Rgetattr field order, Rreaddir dirent packing, msize actually
requested by the kernel, the offset pattern `cat`/`tail` use on reads).

## T1.3 — Throwaway SSH session via SSH.NET incl. resize

**Goal:** validate SSH.NET's interactive shell + window-change against dropbear.

Files: `spike/SpikeSsh/` console app.

1. NuGet `SSH.NET` ≥ 2025.1.0. Connect with `PrivateKeyFile` (the spike ed25519 key) to
   127.0.0.1:2222.
2. `client.CreateShellStream("xterm-256color", 120, 30, 0, 0, 8192)` — **verify the exact
   signature/parameter meaning at implementation time** (pixel w/h args; release notes for
   2025.1.0 / PR #1646).
3. Bridge: stdin → `ShellStream.Write`; `ShellStream.DataReceived`(or read loop) → stdout. Run
   `htop` (after `apk add htop` in the guest), confirm it draws at 120×30.
4. Call `shellStream.ChangeWindowSize(80, 24, 0, 0)` from another thread; confirm htop reflows
   to 80×24 **live**.
5. Note observed disposal semantics (what closing the stream does to the channel, what
   `ErrorOccurred` fires on daemon death) in NOTES.

**Exit (hard gate for M4):** live resize confirmed working end-to-end.

---

# M2 — Guest image build pipeline

> Produces, reproducibly: `base.qcow2` (zstd-compressed qcow2), `vmlinuz-virt`,
> `initramfs-virt`, `manifest.toml`, `id_ed25519`(+`.pub`), `dropbear_host_key_fingerprint`.
> Runs on Linux (CI runner or any Linux box/VM; macOS dev runs it in Docker `--privileged` or a
> Lima VM — document both in `guest/README.md`).

## T2.1 — Rootfs construction script

**Goal:** `guest/build-image.sh` stage 1: a complete Alpine rootfs directory, **without**
booting anything (no setup-alpine; fully scripted ≠ T1.1's hand-built image).

**Spec (implement exactly; bash, `set -euo pipefail`, root required — document `sudo`):**
1. Pin `ALPINE_VERSION=3.24` (as built; the spike validated 3.24/kernel 6.18) and
   `ALPINE_MIRROR=https://dl-cdn.alpinelinux.org/alpine` at the top. Download `apk-tools-static`
   **and `alpine-keys`** at pinned versions, verify pinned sha256s (the keys seed
   `$ROOTFS/etc/apk/keys` so apk verifies everything else it installs).
2. ```bash
   ./apk.static --arch x86_64 \
     -X $MIRROR/v$VER/main -X $MIRROR/v$VER/community \
     -U --root "$ROOTFS" --initdb add \
     alpine-baselayout alpine-release busybox busybox-suid musl musl-utils alpine-keys \
     apk-tools linux-virt dropbear dropbear-scp openssh-sftp-server qemu-guest-agent \
     ca-certificates
   ```
   Deliberately **no openrc** — busybox init + a hand-written inittab (T2.2) for minimal boot.
   (As built: no extra packages were needed for `linux-virt`; `alpine-release` was added because
   modern Alpine splits `/etc/alpine-release`+`/etc/os-release` out of baselayout and the
   manifest stamps the exact release.)
3. Generate keys into `guest/out/`:
   `ssh-keygen -t ed25519 -N '' -f out/id_ed25519 -C gatos`;
   `dropbearkey -t ed25519` via **chroot** (host needs no dropbear) for
   `/etc/dropbear/dropbear_ed25519_host_key`; record the pin as **sha256 hex of the raw
   ssh-ed25519 public key blob** to `out/host_key_fingerprint.txt` (this is exactly what
   SSH.NET's `HostKeyReceived.HostKey` hashes to in M4).
4. Install `out/id_ed25519.pub` as `$ROOTFS/root/.ssh/authorized_keys` (mode 0600, dir 0700).
5. Copy `guest/rootfs-overlay/` over the rootfs (T2.2 defines its contents).
6. Write `$ROOTFS/etc/hostname` = `gatos`; `/etc/motd` = short gatOS banner;
   `/etc/resolv.conf` = `nameserver 10.0.2.3`; root password locked (`passwd -l` equivalent in
   `/etc/shadow`: `root:!:...`).
7. Set apk repositories file to the pinned mirrors so `apk add` works in-game.

## T2.2 — rootfs-overlay: init, networking, 9p supervisor

Files under `guest/rootfs-overlay/`:

**`etc/inittab`** (busybox init):
```
::sysinit:/sbin/init-gatos
::respawn:/usr/sbin/dropbear -F -E -s
::respawn:/sbin/qga-gatos
::respawn:/sbin/sim-mount
::ctrlaltdel:/sbin/reboot
::shutdown:/sbin/shutdown-gatos
```
- dropbear flags: `-F` foreground (respawn-managed), `-E` log to stderr, `-s` disable password
  auth (key-only).
- (As built) qemu-ga runs via the **`sbin/qga-gatos`** wrapper: it waits for
  `/dev/virtio-ports/org.qemu.guest_agent.0` to exist, then execs
  `qemu-ga -m virtio-serial -p <port>`. Exec'ing qemu-ga directly trips busybox init's
  respawn-throttling whenever the device is absent (e.g. a launch without virtio-serial) or slow
  to probe. `init-gatos` creates the `/dev/virtio-ports/<name>` symlinks (mdev only makes
  `/dev/vportNpM`).

**`sbin/init-gatos`** (mode 0755): mount `proc`/`sysfs`/`devtmpfs`/`devpts`/`tmpfs`es,
`mdev -s` + `modprobe virtio_net virtio_console` + virtio-port symlinks, hostname from
`/etc/hostname`, `ifconfig lo up`, **static slirp network** (no DHCP — faster + deterministic):
`ifconfig eth0 10.0.2.15 netmask 255.255.255.0 up; route add default gw 10.0.2.2`,
`mount -o remount,rw /` (belt-and-braces), create `/sim` dir. Every step best-effort so dropbear
always starts.

**`sbin/sim-mount`** (mode 0755) — the 9p remount supervisor (analysis §3.6):
```sh
#!/bin/sh
# Mount /sim from the host 9p server; retry forever. PORT injected via kernel cmdline.
PORT="$(sed -n 's/.*gatos\.simport=\([0-9][0-9]*\).*/\1/p' /proc/cmdline)"
# absent OR 0 ⇒ 9p disabled (T3.3 appends gatos.simport=0 until M8): idle, don't exit
[ -z "$PORT" ] || [ "$PORT" = 0 ] && exec sleep 2147483647
modprobe 9p 2>/dev/null
while :; do
  if ! mountpoint -q /sim; then
    mount -t 9p -o trans=tcp,port=$PORT,version=9p2000.L,cache=none 10.0.2.2 /sim
  fi
  sleep 2
done
```
(The host passes `gatos.simport=<p9>` on the kernel command line — see T3.3. Mount options are
the exact spike-validated set — no `uname`/`aname` needed, spike/NOTES.md T1.2.)

**`sbin/shutdown-gatos`**: `umount -a -r` best effort.

**Optional getty:** none (serial console is a log file). Emergency access is SSH or QGA.

## T2.3 — Image assembly + artifacts

**Goal:** `build-image.sh` stage 2: rootfs → bootable artifacts.

1. Copy `$ROOTFS/boot/vmlinuz-virt` and `$ROOTFS/boot/initramfs-virt` to `guest/out/`; delete
   `$ROOTFS/boot/*` from the rootfs (direct kernel boot; keeps image small).
2. Build a **partitionless ext4 root**: `truncate -s 1536M disk.raw`, then
   `mkfs.ext4 -d "$ROOTFS" -L gatos-root disk.raw` (populates without mounting — works with
   e2fsprogs ≥1.43; fall back to mount+rsync under sudo if `-d` misbehaves with the overlay's
   special perms).
3. `qemu-img convert -f raw -O qcow2 -c -o compression_type=zstd disk.raw out/base.qcow2`.
4. Write `out/manifest.toml`:
   ```toml
   schema = 1
   guest_version = <N from guest/GUEST_VERSION>
   alpine_version = "3.24.x"
   kernel = "vmlinuz-virt"
   initrd = "initramfs-virt"
   base_image = "base.qcow2"
   kernel_cmdline = "console=ttyS0 root=/dev/vda rw quiet rootfstype=ext4 modules=virtio,ext4"
   ssh_user = "root"
   ssh_key = "id_ed25519"
   host_key_sha256 = "<sha256 hex of the raw host public key blob>"
   built_utc = "<stamp>"
   ```
   (`root=/dev/vda` — whole-disk ext4, no partitions; `rootfstype=ext4` from the spike's
   known-good cmdline.)
5. Smoke-test inside the script: boot the produced artifacts (dynamically allocated host port,
   `snapshot=on` so base.qcow2 stays pristine; KVM when `/dev/kvm` is usable, else TCG), wait
   for the SSH banner, `ssh 'echo ok'`, **verify the presented host key hashes to the
   manifest's `host_key_sha256`** (ssh-keyscan), then `poweroff` and wait for QEMU exit. Fail
   if not ok within 180 s (env-overridable). Also runs standalone via
   `build-image.sh --smoke-only` (e.g. natively on macOS against Docker-built artifacts).

**Accept:** script runs clean twice from scratch on a Linux host; second run reproduces a
working image. Total `out/` size recorded (~expect 60–120 MB).

## T2.4 — Boot-speed pass

**Goal:** keep cold boot ≈1 s accelerated.

Measure (script prints) kernel-start→sshd-accept. Apply, in order, while keeping the image
booting: `quiet` (already), drop unused kernel modules from initramfs if mkinitfs config allows
(`features="base virtio ext4"` — rebuild initramfs with a trimmed `mkinitfs.conf` **inside the
rootfs before extracting**), confirm no DHCP wait, no openrc. Record final numbers in
`guest/README.md`. Do not over-engineer; <2 s accelerated is the bar (TCG: whatever it is).

## T2.5 — guest/fetch-guest.sh + GUEST_VERSION

**Goal:** consumers (dev build, dist CI) obtain `guest/out/` without building it.

- `guest/GUEST_VERSION`: single integer (`1`).
- `fetch-guest.sh` (+ `.ps1`): downloads the assets of GitHub release tag
  `guest-v$(cat GUEST_VERSION)` from `meow-sci/gatos` into `guest/out/`, verifies
  `sha256sums.txt` (uploaded with the release). No-op if `out/manifest.toml` already matches the
  pinned version.

## T2.6 — guest-image.yml workflow

**Goal:** CI builds and publishes guest releases.

`.github/workflows/guest-image.yml`: `workflow_dispatch` + on push to `main` touching
`guest/**`. ubuntu-latest: run `build-image.sh` (sudo ok), then create/update release
`guest-v<N>` with the `out/` artifacts + `sha256sums.txt`. Bumping `GUEST_VERSION` is a manual
commit. Smoke test runs under KVM if `/dev/kvm` exists on the runner, else TCG.

**Accept:** `guest-v1` release exists with all eight artifacts (base.qcow2, vmlinuz-virt,
initramfs-virt, manifest.toml, id_ed25519, id_ed25519.pub, host_key_fingerprint.txt,
sha256sums.txt); `fetch-guest.sh` round-trips.

---

# M3 — gatOS.Vm: the QEMU lifecycle layer — **DONE**

> Game-free. All integration tests gated by env `GATOS_IT=1` and skipped otherwise
> (`Assert.Ignore`) so plain `dotnet test` never needs QEMU. CI sets `GATOS_IT=1` (ubuntu
> runners have `/dev/kvm`; tests must still pass via TCG fallback if not).
> (As built: `build.yml` installs qemu-system-x86 + qemu-utils, opens `/dev/kvm` via a udev
> rule, runs `guest/fetch-guest.sh`, and runs the whole suite with `GATOS_IT=1`. With the gate
> set, missing prerequisites are hard failures, not skips — `gatOS.Vm.Tests/TestEnv.cs`.)

## T3.1 — PortAllocator + QemuLocator

Files: `gatOS.Vm/PortAllocator.cs`, `gatOS.Vm/QemuLocator.cs`.

- `PortAllocator.AllocateLoopbackPort()`: bind `TcpListener(IPAddress.Loopback, 0)`, read the
  port, dispose, return. Allocate all ports for one VM in one call
  (`AllocatePorts(count)` → distinct ports) to shrink the reuse race window; document the
  residual race (acceptable: VM start fails → retried once with fresh ports by VmHost).
- `QemuLocator.Find()` → `QemuBinaries(string SystemEmulator, string QemuImg)`:
  - Windows: `GatOsPaths.BundledQemuDir/win-x64/qemu-system-x86_64.exe` (+`qemu-img.exe`);
    error if missing (as built: also the typed `QemuNotFoundException` — not
    `InvalidOperationException` — when `GatOsPaths.ModDir` is unset, i.e. headless test hosts,
    so the test skip-gate works on Windows; tests resolve `vendor/qemu/win-x64` via
    `QemuLocator.OverridePath`, see the T11.1 as-built note).
  - Linux/macOS: probe PATH (`qemu-system-x86_64`, `qemu-img`); on macOS also
    `/opt/homebrew/bin` (as built: and `/usr/local/bin` — the game process PATH may lack the
    brew prefix). Returns a typed error (`QemuNotFoundException` with a per-OS
    install-hint message) — GameMod surfaces this text in-UI later.
- `QemuLocator.GetVersion(path)`: parse `qemu-system-x86_64 --version` → `Version`; warn (log)
  if < 11.0 on Windows (WHPX fixes — analysis §3.2).

**Tests:** port allocator returns distinct, bindable ports; locator honors a test override
(`QemuLocator.OverridePath`).

## T3.2 — DiskManager (qemu-img wrapper)

Files: `gatOS.Vm/DiskManager.cs`.

Spec:
- `EnsureBaseInstalled()`: copy `GuestAssetsDir/base.qcow2` →
  `DisksDir/base-v<guestVersion>.qcow2` (+ kernel/initrd/manifest alongside under
  `DisksDir/guest-v<N>/`) if not present. **Never delete old base versions automatically**
  (existing overlays back onto them). (As built: the manifest is parsed by
  `gatOS.Vm/GuestManifest.cs` — Tomlyn lives in gatOS.Vm, not just GameMod, because
  manifest.toml is the boot contract; the ssh private key is installed into
  `DisksDir/guest-v<N>/` too (0600) and `VmEndpoints.PrivateKeyPath` points there;
  `EnsureBaseInstalled` returns an `InstalledGuest` record with the resolved paths.)
- `CreateOverlay(string profile)` → path `DisksDir/<profile>.qcow2`:
  `qemu-img create -f qcow2 -b base-v<N>.qcow2 -F qcow2 <profile>.qcow2` run with
  **working directory = DisksDir** so the stored backing ref is the **bare relative filename**
  (portable; analysis §3.7). Record `guest_version` next to it in `<profile>.toml`.
- `GetOrCreateOverlay(profile)`, `DeleteOverlay(profile)`, `ListOverlays()`.
- `RunQemuImg(args)`: Process wrapper, captures stderr, throws `DiskOperationException` with it.
- **Single-writer enforcement** (QEMU image locking is absent on Windows): a lock file
  `<profile>.lock` containing the PID, created O_EXCL at VM start, removed at clean stop; stale
  lock (PID dead) is reclaimed with a log line.
- Never use `qemu-img commit` anywhere (corrupts sibling overlays' shared base).

**Tests (need only qemu-img, available on CI):** create/list/delete overlay; backing file
recorded relative (`qemu-img info --output=json` assertion); lock semantics incl. stale
reclaim.

## T3.3 — QemuCommandBuilder

Files: `gatOS.Vm/QemuCommandBuilder.cs`, `gatOS.Vm/VmLaunchSpec.cs`.

```csharp
public sealed record VmLaunchSpec(
    string OverlayPath, string KernelPath, string InitrdPath, string KernelCmdlineBase,
    int MemoryMb,          // default 256
    int Cpus,              // default 2
    int SshHostPort, int QgaPort, int QmpPort,
    int? SimPort,          // 9p server port; null until M8 wires it
    bool RestrictNetwork,  // D3; false default
    string SerialLogPath, string AccelOverride /* "" = auto ladder */);
```

`Build(VmLaunchSpec)` → `(string exePath, IReadOnlyList<string> args)` using **per-OS accel
ladders** (do NOT pass foreign accel names — qemu errors on unknown accelerators):
Windows `whpx,tcg`; Linux `kvm,tcg`; macOS `hvf,tcg` — emitted as repeated `-accel` flags
(first available wins). (As built: on a non-x64 host the ladder collapses to `tcg` —
KVM/WHPX/HVF only virtualize same-arch guests, e.g. Apple Silicon dev Macs run the x86-64
guest under TCG, spike/NOTES.md.) Args (one list entry each; never string-join — Process
`ArgumentList`):

```
-M q35  -cpu <"host" when first accel != tcg, else "max">  -m {MemoryMb} -smp {Cpus}
-kernel {KernelPath} -initrd {InitrdPath}
-append "{KernelCmdlineBase} gatos.simport={SimPort ?? 0}"
-drive file={OverlayPath},if=virtio,format=qcow2
-netdev user,id=n0,hostfwd=tcp:127.0.0.1:{SshHostPort}-:22[,restrict=on]
-device virtio-net-pci,netdev=n0
-device virtio-serial-pci
-chardev socket,id=qga0,host=127.0.0.1,port={QgaPort},server=on,wait=off
-device virtserialport,chardev=qga0,name=org.qemu.guest_agent.0
-qmp tcp:127.0.0.1:{QmpPort},server=on,wait=off
-display none -serial file:{SerialLogPath} -monitor none -no-reboot
```

Notes: `-no-reboot` makes guest `poweroff`/`reboot` exit the process (clean lifecycle signal).
When the resolved ladder starts with `tcg` (or `AccelOverride == "tcg"`), use `-cpu max`
(`-cpu host` requires acceleration). `pic=off` from the analysis sketch is **not** included
until validated on WHPX in T6.7 — start maximally compatible, optimize later.

**Tests:** golden-args tests per OS branch (parameterize the OS check via injectable
`OperatingSystemFacts`), restrict flag, simport injection, tcg cpu fallback.

## T3.4 — QemuProcess: spawn + supervise

Files: `gatOS.Vm/QemuProcess.cs`.

- `StartAsync(spec)`: locate binaries, build args, `Process.Start` with redirected
  stdout/stderr appended to `LogsDir/qemu-<utc>.log` (+ retain last 5 logs, delete older).
  `EnableRaisingEvents`, `Exited` → raises `ProcessExited(int exitCode, string lastStderrTail)`.
- **C#-side accel fallback**: if the process exits within 3 s AND stderr matches an
  accelerator-init failure (substring match: `whpx`, `kvm`, `hvf`, `failed to initialize`,
  `No accelerator found`), rebuild with the next ladder entry forced via `AccelOverride` and
  retry (max one retry → `tcg`). Record `EffectiveAccel` for diagnostics. (The in-qemu `-accel a -accel b`
  list already falls back at init; this C# retry catches the cases where it doesn't —
  e.g. WHP feature absent errors, or `-cpu host` rejected after an in-qemu fallback to tcg —
  and tells us what actually ran.) (As built: `StartAsync` waits out the full 3 s survival
  window — `WhenAny(exit, delay)` — adding ≤3 s before the readiness probe starts; tune in M6
  if boot latency matters. `EffectiveAccel` is best-effort: the forced accel after a retry,
  else the first ladder entry; an in-qemu silent fallback is not detectable from outside.)
- `Stop(TimeSpan grace)`: see shutdown ladder in T3.7.
- Keep a ring buffer (last 100 lines) of stderr for error surfacing.

**Tests:** unit-test the accel-failure classifier on canned stderr samples; integration
(`GATOS_IT=1`): start with guest artifacts (fetched via `guest/fetch-guest.sh` — test fixture
asserts presence and `Assert.Ignore`s with a helpful message if absent), see process alive,
kill, see `ProcessExited`.

## T3.5 — Readiness probe + QgaClient

Files: `gatOS.Vm/ReadinessProbe.cs`, `gatOS.Vm/QgaClient.cs`.

- `ReadinessProbe.WaitForSshAsync(port, timeout, ct)`: loop {TCP connect; read ≥1 banner byte
  starting `SSH-`} with 250 ms delay between attempts; overall timeout from config
  (default 60 s accelerated / 300 s when `EffectiveAccel == "tcg"`).
- `QgaClient` (connect to the QGA chardev TCP port; **the host is the socket client** since
  qemu has `server=on`): newline-delimited JSON. Implement: `PingAsync()`
  (`{"execute":"guest-ping"}`), `ShutdownAsync()` (`guest-shutdown`), with a
  **sync-escape preamble** per QGA docs: send `\xFF` + `guest-sync-delimited` with a random id
  and discard until the matching response — verify exact procedure against
  https://www.qemu.org/docs/master/interop/qemu-ga.html at implementation time. 5 s default
  per-command timeout; all failures are soft (return false / log) — QGA is best-effort.

**Tests:** probe against a fake TCP server emitting `SSH-2.0-test`; QgaClient against a fake
JSON echo server.

## T3.6 — VmHost: the state machine (the heart of M3)

Files: `gatOS.Vm/VmHost.cs`, `gatOS.Vm/VmHostOptions.cs`, `gatOS.Vm/VmStatus.cs`.

```csharp
public enum VmState { Stopped, Starting, Running, Stopping, Faulted }
public sealed record VmStatus(VmState State, string? EffectiveAccel, int? SshPort, int? SimPort,
                              DateTime? StartedUtc, string? FaultReason);
public sealed class VmHost : IAsyncDisposable
{
    public VmHost(VmHostOptions options);   // options: profile name, memory, cpus, restrict,
                                            // sim port provider (Func<int?>), timeouts
    public VmStatus Status { get; }                      // volatile snapshot for UI
    public event EventHandler<VmStatus>? StatusChanged;
    public Task<VmEndpoints> EnsureStartedAsync(CancellationToken ct);  // coalesced
    public Task StopAsync(TimeSpan grace);
}
public sealed record VmEndpoints(int SshPort, string SshUser, string PrivateKeyPath,
                                 string HostKeySha256);
```

Behavior:
- `EnsureStartedAsync` under one `SemaphoreSlim`: if Running → return endpoints; if Starting →
  await the in-flight task (cache `Task<VmEndpoints>`); if Stopped/Faulted → run the boot
  sequence: `DiskManager.EnsureBaseInstalled` → `GetOrCreateOverlay(profile)` + lock →
  allocate 3 ports (+ read sim port from provider) → `QemuProcess.StartAsync` →
  `ReadinessProbe.WaitForSsh` → state Running.
- Boot failure: classify (qemu missing / accel / timeout / process died) into
  `VmStartException.UserMessage` (one readable sentence + log path) — this string is what the
  terminal tab will show. State → Faulted (retryable: next EnsureStarted tries again).
- Unexpected `ProcessExited` while Running → state Faulted("VM exited: …"), release disk lock,
  raise StatusChanged (sessions react in M4).
- `StopAsync`: ladder in T3.7; always releases lock; idempotent.

**Tests:** state machine unit tests with a fake QemuProcess/probe (interface-extract
`IQemuProcess` for this); concurrent EnsureStarted callers get the same boot. Integration
(`GATOS_IT=1`): full real boot → Running → StopAsync → Stopped, on CI under KVM.
(As built: `IDiskManager` and `IQgaClient` are extracted too; the test seams enter through an
internal `VmHost` ctor + `InternalsVisibleTo`. The T3.1 residual port race is closed here:
a start failing on `Could not set up host forwarding`/`Address already in use` is retried
once with fresh ports inside `BootAsync`.)

## T3.7 — Shutdown ladder

In `VmHost.StopAsync(grace = 10 s)`:
1. `QgaClient.ShutdownAsync()` → wait up to `grace` for process exit (with `-no-reboot`,
   poweroff exits qemu).
2. Else QMP: connect `QmpPort`, handshake (`qmp_capabilities` after greeting), send
   `{"execute":"quit"}` — wait 3 s. (Minimal QMP client inline in `QemuProcess`; ~50 lines.)
3. Else `Process.Kill(entireProcessTree: true)`.
Log which rung fired. The overlay is crash-consistent qcow2 either way; rung 1 just gives the
guest fs a clean unmount.

**Tests:** integration: boot + StopAsync completes <grace via rung 1; kill-path covered by
faking QGA/QMP failure.

---

# M4 — gatOS.Ssh: the ICustomShell implementation — **DONE**

> Game-free; references vendor/purrTTY DLLs + SSH.NET + gatOS.Vm.
> (As built: the session is fake-testable through internal `IShellBroker`/`IShellChannel`
> seams — the M3 seam pattern — implemented by `VmConnectionBroker` and the `SshShellChannel`
> adapter (which owns the per-session SshClient+ShellStream pair). 19 unit tests run without
> SSH.NET or a VM; the two `GATOS_IT=1` integration fixtures boot the real guest.)

## T4.1 — VmConnectionBroker

Files: `gatOS.Ssh/VmConnectionBroker.cs`.

One broker per process owns the shared `VmHost` and hands out SSH connections:

```csharp
public sealed class VmConnectionBroker(VmHost vmHost) : IAsyncDisposable
{
    // Boots VM if needed, then connects a NEW SshClient (one per session keeps
    // channel handling simple and matches dropbear's per-connection model).
    public Task<SshClient> ConnectAsync(CancellationToken ct);
}
```
- Build `ConnectionInfo` with `PrivateKeyAuthenticationMethod` from
  `VmEndpoints.PrivateKeyPath`; host `127.0.0.1`, the forwarded port; username from endpoints.
- **Host-key pinning**: subscribe `HostKeyReceived`, compute SHA256 of `e.HostKey`, compare to
  `VmEndpoints.HostKeySha256` (from the manifest); `e.CanTrust = match`. Mismatch → exception
  with a clear message.
- Retry once on connection-refused within 2 s (dropbear may be a beat behind the banner probe).

**Tests:** integration (`GATOS_IT=1`): ConnectAsync against a real VM; `RunCommand("echo ok")`.

> (As built: a pin mismatch raises the dedicated `HostKeyMismatchException`;
> `ConnectOnceAsync` is internal so the integration test drives the pin against the live guest
> with tampered endpoints. `DisposeAsync` stops the VM — the broker owns the shared `VmHost`.
> The broker also implements the internal `IShellBroker` seam: `OpenShellAsync` =
> ConnectAsync + `CreateShellStream` wrapped in `SshShellChannel`, and `VmStatusChanged`
> forwards `VmHost.StatusChanged` so sessions can watch for Faulted.)

## T4.2 — SshShellSession : ICustomShell

Files: `gatOS.Ssh/SshShellSession.cs`, `gatOS.Ssh/ShellInputQueue.cs`.

The contract mapping (from OS_ANALYSIS §3.5, now concrete):

| Member | Implementation |
|---|---|
| ctor | **trivial** — store the broker reference only (registry probe-instantiates and Disposes! T0.5) |
| `Metadata` | `CustomShellMetadata.Create("gatOS", "Shell session into the gatOS virtual computer", new Version(major,minor,patch), "meow sci", "colors","resize")` |
| `StartAsync(options, ct)` | `_client = await broker.ConnectAsync(ct)` (boots VM lazily — may take seconds; purrTTY tolerates slow starts and shows failures via its own UI); then `_stream = _client.CreateShellStream("xterm-256color", (uint)options.InitialWidth, (uint)options.InitialHeight, 0, 0, 8192)`; hook `DataReceived`/`ErrorOccurred`/`Closed`; start input-writer thread; `IsRunning = true`. On `VmStartException` rethrow as `CustomShellStartException(userMessage)` (the contract's start-failure type). |
| `WriteInputAsync(data, ct)` | enqueue into `ShellInputQueue` (bounded 1 MiB; overflow drops chunk + logs once per episode — mirrors purrTTY's PtyInputQueue discipline); dedicated writer thread does `_stream.Write(buf); _stream.Flush()`. Never write on the caller's thread. |
| `OutputReceived` | from `ShellStream.DataReceived` → raise with the received bytes (copy into a fresh array; do not assume the buffer survives). Alternatively a dedicated read loop if `DataReceived` proves lossy in T1.3 — follow spike NOTES. |
| `NotifyTerminalResize(w, h)` | if running: `_stream.ChangeWindowSize((uint)w, (uint)h, 0, 0)` in try/catch (log-only). Before start: store as pending initial size. |
| `RequestCancellation()` | no-op (Ctrl-C travels in-band as 0x03 through WriteInput). |
| `SendInitialOutput()` | no-op (the guest motd/prompt is the banner). |
| `StopAsync(ct)` | stop writer thread (join ≤2 s), close stream + disconnect/dispose client. **Never stops the VM** (other sessions/tabs share it). Raise `Terminated(0, "closed")` if not already raised. |
| `Terminated` | raised on `Closed`/`ErrorOccurred` (exit code 0 / 1 + reason) and when `VmHost.StatusChanged` reports Faulted (subscribe while running; reason = fault reason). |
| `Dispose()` | safe on never-started instance; idempotent; calls StopAsync best-effort sync (bounded 2 s). |

Concurrency: all mutable state behind one lock; events raised outside it.

> (As built: `ShellInputQueue` splits the callbacks — overflow logs once per episode and only
> drops; the first **write failure** terminates the session (exit 1) and later chunks are
> dropped; the queue never self-joins (the failure path reaches Dispose on the writer thread).
> One `Terminate` path raises `Terminated` exactly once; teardown is deferred to the thread
> pool when triggered from a connection-layer callback (no SSH.NET re-entrancy) but runs
> synchronously from `StopAsync`/`Dispose`. Channel events are hooked before the session
> publishes its refs so an instantly-dying channel is not missed, and a `Dispose` that lands
> mid-`StartAsync` backs out and closes the late-arriving channel.)

`gatOS.Ssh.Tests/SshShellSessionIntegrationTests.cs` (`GATOS_IT=1`):
1. Start session (80×24) → collect output until prompt regex (`# ` within 90 s).
2. `WriteInputAsync("stty size\n")` → expect `24 80`.
3. `NotifyTerminalResize(120, 30)` → `stty size` → `30 120` (proves live SIGWINCH).
4. `WriteInputAsync("echo $TERM\n")` → `xterm-256color`.
5. Open a **second** session concurrently → both interactive (one VM, two channels).
6. Stop both; VM still Running; `VmHost.StopAsync` clean.
Also a unit test: ctor+Dispose without StartAsync does nothing (registry-probe safety).

> (As built: every expectation is anchored to output produced after its own command (per-step
> marks), and `echo M_$((6*7))` → `M_42`-style arithmetic markers keep expected text out of the
> echoed command line. Verified on the dev Mac under TCG: the full script completes in ~8 s
> wall-clock after boot; the whole Ssh suite incl. both VM boots ≈ 15 s.)

---

# M5 — Upstream purrTTY changes (executed in the `purrtty` repo) — **DONE**

> Two small changes; keep them independent commits. They ship in a normal purrTTY tip release
> **before** M6 in-game testing. Follow purrtty's CLAUDE.md mandate (update its docs).

> (As built: purrtty commits `9fb5e13` (T5.1) and `a56966a` (T5.2) on its current working
> branch; full purrtty suite green (325 tests, 0 warnings). The **tip release cut is the one
> remaining step** — it happens on the next push to purrtty `main`, which is the user's call.)

## T5.1 — Export the contract assemblies over the mod ALC

In `purrTTY.GameMod/mod.toml` add:
```toml
[StarMap]
EntryAssembly = "purrTTY.GameMod"
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```
(`purrTTY.Logging` is exported because the contract assembly references it — an importer needs
both resolved from the same ALC for one coherent type identity. Per the StarMap resolution
matrix, with both sides listing, the share set is the intersection.)

**Accept:** purrTTY builds + runs unchanged; existing tests green.

> (As built: exactly the snippet above, plus a mod.toml comment documenting the why; verified
> against CI's version stamping (a line-anchored sed on `version = `, untouched).)

## T5.2 — New Tab / New Window menus enumerate registered custom shells

Today `DrawShellItems` renders a hardcoded "Game Console" (`ShellType.CustomGame` +
`GameConsoleShell`) and `ShellMenuCache` snapshots entries **once at init** — a shell registered
by another mod (load order undefined) would never appear.

Change (in `purrTTY.GameMod/TerminalMod.cs` `DrawShellItems`): after the existing entries,
enumerate **live** per draw:
```csharp
foreach (var (id, meta) in CustomShellRegistry.Instance.GetAvailableShells())
{
    if (id == nameof(GameConsoleShell)) continue;          // already has its entry
    if (ImGui.MenuItem(meta.Name))
        open(ProcessLaunchOptions.CreateCustomGame(id));
}
```
Rationale (encode in a code comment): the registry read is a ConcurrentDictionary snapshot —
cheap and probe-free, so the ShellMenuCache "never detect on the draw path" rule is not
violated; reading live instead of caching solves cross-mod registration timing without a
refresh hook.

**Accept:** purrTTY test suite green; in-game smoke: Game Console still opens; a dummy shell
registered from a test mod appears in both menus. Update purrtty CLAUDE.md (menu section) +
tip release cut.

> (As built: the loop lives in a `DrawRegisteredCustomShellItems` helper called from **both**
> `DrawShellItems` paths (normal + the detection-still-running fallback), so a registered shell
> is launchable even before the WSL/unix-shell snapshot lands. Probe-freedom was verified
> against the init order: `InitializeTerminal` runs `EnsureGameShellsDiscovered()` synchronously
> before publishing `MenuController`, and the menus are unreachable until `MenuController` is
> non-null — so `GetAvailableShells()` on the draw path can never trigger discovery. purrtty
> CLAUDE.md updated (menu section, custom-shell how-to, and the allocation-free claim now
> carves out this LINQ read, which only runs while a New Tab/New Window menu is open). The
> in-game smoke (Game Console + a cross-mod shell in both menus) rides M6's first in-game test;
> headlessly the suite is green.)

---

# M6 — gatOS.GameMod: in-game integration

## T6.1 — mod.toml + Mod.cs lifecycle skeleton

Files: `gatOS.GameMod/mod.toml`, `Mod.cs`.

**mod.toml:**
```toml
name = "gatOS"
description = "A real (tiny) Linux computer inside KSA — terminal via purrTTY, telemetry as files"
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "gatOS.GameMod"

[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```
With purrTTY installed, StarMap resolves the two assemblies from purrTTY's ALC → **the same
`CustomShellRegistry.Instance` static** purrTTY reads. Without it, gatOS's own vendored copies
load (registration succeeds into a registry nobody consumes — harmless; gatOS logs
"purrTTY not detected: terminal UI unavailable" once).

**Mod.cs** (per the ksa-skill scaffold; every hook body try/catch + `Console.WriteLine("gatOS: …")`):
```csharp
[StarMapMod]
public class Mod
{
    public bool ImmediateUnload => false;
    [StarMapImmediateLoad] public void OnImmediateLoad() { }     // nothing (renderer not live)
    [StarMapAllModsLoaded] public void OnFullyLoaded()           // init everything (T6.2)
    [StarMapBeforeGui]     public void OnBeforeUi(double dt)     // sampler tick (M9)
    [StarMapAfterGui]      public void OnAfterUi(double dt)      // diagnostics window (T6.4)
    [StarMapUnload]        public void Unload()                  // StopAsync ladder + dispose
}
```
No Harmony in MVP (no patches needed; no text-input windows → no HotkeyGuard requirement —
revisit if M12 UI adds InputText).

`OnFullyLoaded` order: resolve `GatOsPaths.ModDir` (from the executing assembly's location),
load config (T6.3), construct `VmHost` + `VmConnectionBroker` (no boot!), register the shell:
```csharp
CustomShellRegistry.Instance.RegisterShell("gatos", () => new SshShellSession(_broker));
```
`Unload`: `VmHost.StopAsync(10s)` synchronously bounded (`.GetAwaiter().GetResult()` with an
outer 15 s timeout), dispose broker.

## T6.2 — Mod-dir asset resolution + first-run install

- Locate `guest/` + `qemu/` beside the mod DLL; validate `manifest.toml` (schema=1) at init;
  any missing asset → log + Faulted-style status the diagnostics window can show (do not throw
  out of the lifecycle hook).
- `DiskManager.EnsureBaseInstalled()` runs on first `EnsureStartedAsync` (not at game launch —
  no I/O before the player asks for a terminal).

## T6.3 — gatos.toml config (Tomlyn)

Files: `gatOS.GameMod/Configuration/GatOsConfig.cs`.

```toml
schema = 1
memory_mb = 256
cpus = 2
restrict_network = false
accel_override = ""        # "", "whpx", "kvm", "hvf", "tcg"
sample_rate_hz = 10
boot_timeout_seconds = 0   # 0 = auto (60 accel / 300 tcg)
```
Load with `Toml.TryToModel` (diagnostics logged, fall back to defaults — never crash on a bad
file); write through an atomic temp+rename helper (copy the pattern from purrTTY's
`Configuration/AtomicFile`). Created with defaults on first run.

## T6.4 — Diagnostics menu + status window

- `[ModMenuEntry("gatOS")]` static menu (ModMenu.Attributes package, same as purrTTY):
  items: **Status** (toggle window), **Start VM**, **Shut down VM**, **Open data folder**
  (Process.Start explorer/open/xdg-open), **Reset disk…** (confirm modal → StopAsync +
  `DiskManager.DeleteOverlay("default")`).
- Status window (ImGui, `OnAfterUi`, hidden by default): VmState, effective accel, ssh/sim
  ports, uptime, guest version, last fault reason, path of newest qemu log. Reads only the
  volatile `VmHost.Status` — zero blocking. (No transpiler fallback menu in MVP; ModMenu-less
  users still get full function via purrTTY's menus — document in README.)

## T6.5 — Dist packaging (CopyCustomContent)

In `gatOS.GameMod.csproj`, mirror purrTTY's `CopyCustomContent` target with
`<DistDir>$(SelectedDistModDir)gatOS\</DistDir>`:
- Copy: `mod.toml`, deps.json, `gatOS.GameMod.dll`, `gatOS.{Logging,NineP,SimFs,Vm,Ssh}.dll`,
  `Renci.SshNet.dll`, `Tomlyn.dll`, `ModMenu.Attributes.dll`, **vendored**
  `purrTTY.CustomShellContract.dll` + `purrTTY.Logging.dll` (the Optional-dependency fallback,
  D6), `LICENSE`, `third-party-licenses/`.
- Copy `guest/out/**` → `$(DistDir)guest\` **if it exists** (devs run `guest/fetch-guest.sh`
  once; emit an MSBuild warning when absent so a dist without a guest is loud).
- Copy `vendor/qemu/win-x64/**` → `$(DistDir)qemu\win-x64\` if present (same pattern; T11.1).

**Accept:** `dotnet build gatOS.GameMod` deploys a complete `gatOS/` folder into the KSA
mods dir on the dev machine.

> (As built — **done early**, pulled ahead of T6.1 to unblock in-game testing. Deviations from
> the list above, each verified against a real deploy:
> **Glob + deny-list, not an allow-list**: the managed payload is `$(TargetDir)*.dll` minus the
> loader-supplied `0Harmony.dll`/`StarMap.API.dll`, so a new NuGet dependency ships
> automatically — the hand-list above already missed SSH.NET's transitive
> `BouncyCastle.Cryptography` + `Microsoft.Extensions.DependencyInjection.Abstractions`
> (+ the lifted `Microsoft.Extensions.Logging.Abstractions`), which all ship now.
> **Vendored purrTTY DLLs ship via `Private=true`** (see the D6 as-built note, §2 above) — copied
> from the build output, not from `vendor/`, so deps.json entries exist and the no-purrTTY
> fallback actually resolves.
> **Missing guest emits a High-importance `Message`, not a `Warning`**:
> `MSBuildTreatWarningsAsErrors` (zero-warning policy) would turn the planned warning into a
> failed build on a fresh clone, and plain builds must never require the guest.
> **`guest/` and `qemu/win-x64/` are not wiped**: the wipe covers the managed payload (the part
> with stale-DLL/ALC hazards); the large content-stable blobs (59 MB guest, ~90 MB QEMU) copy
> with `SkipUnchangedFiles="true"` so `dotnet test` runs don't recopy them every build.
> `THIRD-PARTY-NOTICES.md` ships alongside `LICENSE`; `third-party-licenses/` copies when it
> exists (M11 creates it). Verified: stale dist DLLs are deleted on rebuild, guest survives,
> 0 warnings, full suite green.)

## T6.6 — In-game validation pass #1 (the M6 exit)

Manual checklist (record results in `docs/VALIDATION.md`):
1. Launch KSA with purrTTY (≥ the T5.x release) + gatOS installed.
2. purrTTY menu → New Tab → **gatOS** → tab opens, boot takes ~1–3 s, motd + root prompt.
3. `stty size` matches the window; resize the purrTTY window → `stty size` follows.
4. `apk add htop` (real network through slirp) → `htop` draws correctly; Ctrl-C works.
5. Second gatOS tab concurrently. Close tabs → VM stays up (diagnostics window).
6. Quit game → qemu process gone (check task manager), disk lock released.
7. gatOS without purrTTY installed → game loads clean, one log line, no crash.

## T6.7 — Windows validation pass (the schedule-risk item — timebox 2–3 days)

On a real Windows 11 machine (and ideally one AMD box):
1. WHP feature enabled: boot under WHPX, record boot time + `EffectiveAccel`.
2. WHP feature **disabled**: confirm the C# accel fallback lands on TCG, the session is usable,
   and the diagnostics window shows accel=tcg with a hint string ("enable Windows Hypervisor
   Platform for full speed: DISM command…" — exact text in the status window).
3. The full T6.6 checklist on Windows. Known-wart watchlist from analysis §3.2: if WHPX is
   unstable, try `-M q35,pic=off` and/or `-accel whpx,ssd=off` and record findings in
   `docs/VALIDATION.md`; pin the working flag set into `QemuCommandBuilder` behind
   `OperatingSystemFacts`.

---

# M7 — gatOS.NineP: the 9P2000.L server

> Game-free. Build against the **spike learnings** (T1.2 NOTES are required reading).
> Reference spec: diod `protocol.md`. Everything little-endian. Strings = `len[2]` + UTF-8.
> This milestone can run in parallel with M3–M6 (different agent, no shared files).

## T7.1 — VFS abstraction (the seam SimFs implements)

Files: `gatOS.NineP/Vfs/IVfsNode.cs`, `VfsDirectory.cs`, `VfsFile.cs`.

```csharp
public abstract class VfsNode
{
    public abstract string Name { get; }
    public ulong QidPath { get; }            // unique per node, assigned by the tree
    public abstract bool IsDirectory { get; }
}
public abstract class VfsDirectory : VfsNode
{
    public abstract IReadOnlyList<VfsNode> List();          // stable order
    public abstract VfsNode? Lookup(string name);           // may build nodes dynamically
}
public abstract class VfsFile : VfsNode
{
    public virtual long Size => 4096;                       // fake size — never 0 (v9fs gotcha)
    // Open returns a per-fid handle; offset-based reads; ct fires on Tflush/clunk.
    public abstract IVfsFileHandle Open();
}
public interface IVfsFileHandle : IDisposable
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct);
    // return empty memory = EOF for this offset
}
```
Plus ready-made impls: `StaticTextFile(Func<string> contentProvider)` (content snapshotted per
**open**, served by offset — so `cat` gets a consistent read), `DelegateDirectory`.

## T7.2 — Wire codec

Files: `gatOS.NineP/Protocol/NinePReader.cs`, `NinePWriter.cs`, `MessageType.cs`,
`Qid.cs`, `LinuxErrno.cs`.

- `MessageType : byte` enum with the 9P2000.L numbers (header `size[4] type[1] tag[2]`):
  `Rlerror=7, Tstatfs=8, Rstatfs=9, Tlopen=12, Rlopen=13, Tgetattr=24, Rgetattr=25,
  Txattrwalk=30, Treaddir=40, Rreaddir=41, Tversion=100, Rversion=101, Tattach=104,
  Rattach=105, Tflush=108, Rflush=109, Twalk=110, Rwalk=111, Tread=116, Rread=117,
  Twrite=118, Rwrite=119, Tclunk=120, Rclunk=121` (+ the remaining T-types for the
  reject-with-ENOTSUP default arm). Cross-check every number against diod protocol.md at
  implementation time.
- Reader/Writer over `Span<byte>`/`BinaryPrimitives`: u8/u16/u32/u64, string (`u16` + bytes),
  qid (`type[1] version[4] path[8]`).
- `LinuxErrno`: `ENOENT=2, EIO=5, EBADF=9, EACCES=13, ENOTDIR=20, EISDIR=21, EINVAL=22,
  EOPNOTSUPP=95`.

**Tests:** round-trip every primitive; golden-byte tests for at least Rversion, Rgetattr,
Rreaddir built from hand-computed buffers (use the spike's captured hexdumps if available).

## T7.3 — Connection state: fids, tags, msize

Files: `gatOS.NineP/Server/FidTable.cs`, `Session.cs`.

- `FidEntry { VfsNode Node; IVfsFileHandle? OpenHandle; }` in a `Dictionary<uint, FidEntry>`
  (per-connection, no lock needed if the connection is processed by one reader loop that
  dispatches handler tasks — see T7.4 concurrency note).
- msize: respond `min(clientMsize, 131072)`; enforce on every Rread
  (`count ≤ msize - 11`).
- Version: accept exactly `"9P2000.L"`; anything else → `Rversion` with version `"unknown"`.
- Tag table: in-flight requests `Dictionary<ushort, CancellationTokenSource>` for Tflush.

## T7.4 — Server loop + dispatcher

Files: `gatOS.NineP/Server/NinePServer.cs`.

```csharp
public sealed class NinePServer(VfsDirectory root) : IAsyncDisposable
{
    public int Port { get; }                       // actual port after StartAsync
    public Task StartAsync(int port = 0);          // TcpListener on IPAddress.Any (guest connects
                                                   // from slirp; OS firewall caveat → README)
    public ValueTask DisposeAsync();               // stop listener, cancel all sessions
}
```
- Per accepted connection: read-loop frames messages (`size[4]` prefix; reject > 1 MiB),
  dispatches each to an async handler. **Handlers for potentially-blocking reads (Tread on
  stream files) run as fire-and-forget tasks** so the loop keeps consuming (pipelining +
  Tflush must work while a read is parked); short handlers may run inline.
  Responses are serialized through a per-connection write lock (one writer at a time).
- **Tflush**: cancel the CTS of `oldtag` if present; the parked handler observes the
  cancellation and replies to its own tag (an `Rread` with 0 bytes is acceptable per spec
  practice — verify the flush(5) sequencing rule: the flushed reply must be sent **before**
  `Rflush`); then send `Rflush`.
- Unknown/unsupported T-message → `Rlerror(EOPNOTSUPP)`.
- Connection teardown: dispose all fid handles, cancel parked reads.

### Handler semantics (read-only server)

| Msg | Behavior |
|---|---|
| Tversion | as T7.3; resets the session |
| Tattach | fid := root; `Rattach(rootQid)`; ignore afid/uname/aname |
| Twalk | up to 16 names; walk via `Lookup`; `..` at root = root; partial success per spec (Rwalk with the qids that resolved); newfid only bound on full success |
| Tlopen | dirs: any flags → ok; files: flags & O_ACCMODE must be O_RDONLY else `EACCES`; calls `VfsFile.Open()`; `Rlopen(qid, iounit=0)` |
| Tgetattr | `Rgetattr` with valid mask = the basic bits; mode = `S_IFDIR|0755` (dirs) / `S_IFREG|0444`; uid=gid=0; nlink=1; size = `VfsFile.Size` (4096 default) or 0 for dirs; times = server start time; field order per protocol.md (valid, qid, mode, uid, gid, nlink, rdev, size, blksize=4096, blocks, atime/mtime/ctime sec+nsec, btime, gen, data_version) |
| Treaddir | pack dirents `qid[13] offset[8] type[1] name[s]` until count budget; `offset` = opaque cookie (index+1); honor resume-from-offset; include neither `.` nor `..` (kernel synthesizes) — **verify against spike notes**; type: DT_DIR=4 / DT_REG=8 |
| Tread | dirs → `EISDIR`; files → `handle.ReadAsync(offset, count, ctFromTag)`; empty result → `Rread(count=0)` (EOF) |
| Tclunk | dispose handle, drop fid; always `Rclunk` |
| Tstatfs | static plausible numbers (type 0x01021997 = V9FS_MAGIC, bsize 4096, large free counts) |
| Tflush | as above |
| Twrite | `EACCES` (until M12 control files) |
| Txattrwalk | `EOPNOTSUPP` (kernel probes it; a clean error is expected and harmless — confirmed in spike) |

**Tests:** an in-repo **managed 9p test client** (`gatOS.NineP.Tests/TestClient/`) that speaks
the same codec: full happy-path walk/open/read/readdir; partial walk; EACCES on write-open;
msize clamp; large-directory readdir paging; blocking-read + Tflush (assert reply ordering);
two concurrent fids on one connection; malformed frame → connection closed without server
crash. These tests are the conformance suite and must be thorough (this is the highest-risk
component).

## T7.5 — Linux-mount smoke test (CI)

`gatOS.NineP.Tests/scripts/mount-smoke.sh` + a CI job step (ubuntu runner, sudo available):
start a sample server (small console host `gatOS.NineP.SampleHost` with the T1.2 tree —
`dotnet run`), then:
`sudo modprobe 9pnet_tcp 9p` (install `linux-modules-extra-$(uname -r)` if needed),
`sudo mount -t 9p -o trans=tcp,port=<p>,version=9p2000.L,cache=none 127.0.0.1 /mnt/t`,
then assert `cat hello`, `ls -la` sizes, `timeout 3 tail -f stream | head -2`, unmount.
Wrap in `if ! modprobe …; then echo "::warning:: 9p modules unavailable, skipping"; fi` so the
suite degrades gracefully on exotic runners.

---

# M8 — gatOS.SimFs: the `/sim` tree

> Game-free: SimFs defines the **snapshot types** and builds the VFS; the game-side sampler
> (M9) produces snapshots. Headless tests use hand-built snapshots.

## T8.1 — Snapshot model + store

Files: `gatOS.SimFs/Snapshots/SimSnapshot.cs`, `SnapshotStore.cs`.

```csharp
// ALL immutable records, plain doubles/strings — no game types, no NaN (sampler sanitizes).
public sealed record SimSnapshot(
    long Sequence, double UtSeconds, double WarpFactor,
    string? ActiveVesselId, IReadOnlyList<VesselSnapshot> Vessels,
    IReadOnlyList<SimEvent> NewEvents);
public sealed record VesselSnapshot(
    string Id, string Name, string Situation,
    double3Snap PositionCci, double LatitudeDeg, double LongitudeDeg,
    double OrbitalSpeed, double SurfaceSpeed, double InertialSpeed,
    QuatSnap AttitudeBody2Cci, double3Snap BodyRatesRadS,
    double BarometricAltitude, double RadarAltitude,
    double MassTotal, double MassDry, double MassPropellant,
    OrbitSnapshot? Orbit, IReadOnlyList<EngineSnapshot> Engines,
    IReadOnlyList<TankSnapshot> Tanks, double? BatteryChargeFraction,
    string? ParentBodyName);
public sealed record OrbitSnapshot(double ApoapsisAltitude, double PeriapsisAltitude,
    double Eccentricity, double InclinationDeg, double SmaMeters, double PeriodSeconds);
public sealed record EngineSnapshot(int Index, bool Active, double VacThrustN, double IspS);
public sealed record TankSnapshot(string Resource, double Amount, double Capacity);
public sealed record SimEvent(double UtSeconds, string Type, string? VesselId, string Detail);
public readonly record struct double3Snap(double X, double Y, double Z);
public readonly record struct QuatSnap(double X, double Y, double Z, double W);
```
(Field availability is grounded in OS_ANALYSIS §10's verified KSA API sweep; the sampler task
T9.1 maps them. If a field proves unreachable, drop it from BOTH the record and the tree and
note it in CLAUDE.md.)

`SnapshotStore`:
```csharp
public sealed class SnapshotStore
{
    public SimSnapshot Current { get; }                  // volatile read
    public void Publish(SimSnapshot s);                  // volatile swap + wake waiters
    // Completes when Sequence > afterSequence (or ct). Used by stream/events files.
    public ValueTask<SimSnapshot> WaitForNextAsync(long afterSequence, CancellationToken ct);
}
```
Implementation: `volatile` field + a `TaskCompletionSource` swapped per publish
(`RunContinuationsAsynchronously`). No locks on the read path.

**Tests:** publish/wait ordering, many concurrent waiters, cancellation.

## T8.2 — Static tree builder

Files: `gatOS.SimFs/SimFsTree.cs`, `Formats.cs`.

`SimFsTree.Build(SnapshotStore store) → VfsDirectory` producing (MVP tree; matches analysis
§3.6 minus not-yet-reachable items):

```
/time/ut /time/warp
/vessels/active/...            # dynamic dir alias → the active vessel's dir (NOT a symlink)
/vessels/by-id/<sanitizedId>/
    id name situation parent
    position/{lat,lon}  velocity/{orbital,surface,inertial}
    attitude/{quat,rates}
    altitude/{barometric,radar}
    mass/{total,dry,propellant}
    orbit/{apoapsis,periapsis,ecc,inc,sma,period}        # only when Orbit != null
    engines/<n>/{active,vac_thrust,isp}
    tanks/<resource>/{amount,capacity}
    battery/charge
    stream                     # blocking NDJSON (T8.3)
/events                        # blocking NDJSON (T8.3)
```

Mechanics:
- Every scalar file is a `StaticTextFile(() => Format(store.Current ⟨field⟩))` — content
  resolved at **open**, one value + `\n`.
- Formatting (`Formats.cs`): doubles `ToString("G9", InvariantCulture)`; quat/rates =
  space-separated components; booleans `0`/`1`; strings verbatim. **Fixed and documented** —
  this is a user-facing API surface.
- `by-id` is a `DelegateDirectory` whose `List()/Lookup()` read `store.Current.Vessels`
  (dynamic — vessels appear/disappear between calls). Id sanitization: replace anything outside
  `[A-Za-z0-9._-]` with `_`; collision → suffix `~2`. Qid paths for dynamic nodes come from a
  `(kind, vesselId, relpath)` → ulong interning map so the same logical file keeps a stable qid
  across snapshots.
- `active`: directory whose Lookup/List delegate to the active vessel's dir; `ENOENT`-style
  null when no active vessel.

**Tests:** build tree from a fixture snapshot; walk every path via the M7 test client; check
formatted contents; vessel add/remove reflected in readdir; active-vessel switch.

## T8.3 — Streaming files (`stream`, `events`)

Files: `gatOS.SimFs/StreamFile.cs`.

Per-open-handle semantics (the subtle part — encode exactly):
- On `Open()`: capture `startSeq = store.Current.Sequence`; allocate an append-only byte buffer
  with absolute offsets `0..produced`.
- `ReadAsync(offset, count, ct)`:
  - If `offset < bufferStart` (reader fell behind a capped buffer): serve from `bufferStart`
    anyway (cat/tail don't rewind; document).
  - If data available at `offset`: return up to `count` bytes immediately.
  - Else: `await store.WaitForNextAsync(lastSeq, ct)`, append one formatted line, loop.
    Cancellation (Tflush/clunk) → propagate (server answers per T7.4).
- Buffer cap 256 KiB per handle; when trimming, drop whole lines and append a
  `{"notice":"dropped"}` line.
- `stream` line (one per published snapshot, **for the vessel owning the file**): single-line
  JSON via `System.Text.Json` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`:
  `{"seq":n,"ut":…,"sit":"Freefall","alt":{"baro":…,"radar":…},"vel":{"orb":…,"surf":…,"inr":…},"att":{"q":[x,y,z,w],"rates":[x,y,z]},"mass":{"t":…,"d":…,"p":…}}`
- `/events` line: `{"ut":…,"type":"situation-change","vessel":"id","detail":"Landed→Freefall"}`.
  Event types produced by the sampler (T9.2): `situation-change, vessel-appeared,
  vessel-removed, active-changed, warp-changed, soi-changed`.

**Tests:** headless publisher at 10 Hz + M7 client: sequential reads stream lines; parked read
wakes on publish; Tflush during park; backpressure trim path.

## T8.4 — End-to-end headless: real kernel mount of fake telemetry

Extend the T7.5 CI smoke: SampleHost v2 = `NinePServer(SimFsTree.Build(store))` + a fake
sampler publishing a scripted flight (altitude ramp). Assert from the mounted fs:
`cat /mnt/vessels/by-id/test-1/altitude/radar` twice → increasing values;
`timeout 3 tail -f stream | head -3` → 3 JSON lines that `jq` parses.
**This is the M8 exit: the entire host-side stack proven without the game.**

---

# M9 — Telemetry sampler + live `/sim` in-game

## T9.1 — TelemetrySampler (game thread)

Files: `gatOS.GameMod/Telemetry/TelemetrySampler.cs`.

- Called from `OnBeforeUi(dt)`; rate-limited by accumulated dt to `sample_rate_hz` (default
  10 Hz; skip entirely while `VmState != Running` **and** no 9p connection has attached since —
  cheap idle).
- Reads (per OS_ANALYSIS §10 / ksa skill — verify each accessor against the decompiled sources
  while implementing; drop gracefully what doesn't exist at runtime):
  `Universe.GetElapsedSimTime().Seconds()`, `SimulationSpeed`, `Program.ControlledVehicle?.Id`,
  `Universe.CurrentSystem?.Vehicles.GetList()`; per vehicle: `Id`, `DisplayName`-equivalent,
  `Situation.ToString()`, `GetPositionCci()`, lat/lon (from position + parent body — if no
  direct accessor exists, compute or omit in MVP), `GetSurfaceSpeed()`, `GetInertialSpeed()`,
  `GetBody2Cci()`, `BodyRates` (NaN-guard → zeros), `GetBarometricAltitude()`,
  `GetRadarAltitude()`, `TotalMass`/`InertMass`/`PropellantMass`, `Orbit`/`OrbitData`
  (apo/peri **radii→altitudes** via parent `MeanRadius`), engines via
  `vehicle.Parts.Modules.Get<EngineController>()`, tanks via `Get<Tank>()`, battery via
  `Get<Battery>()`, parent body name.
- Each vehicle wrapped in its own try/catch (one broken vehicle must not kill the snapshot);
  sanitize every double (`double.IsFinite` else 0 + a once-per-field debug log).
- Publishes via `SnapshotStore.Publish`.

**Tests:** the mapping is game-coupled — cover the pure parts (NaN sanitizer, radius→altitude
conversion, rate limiter) as unit tests; the rest is validated in-game (T9.3).

## T9.2 — Event diffing

Files: `gatOS.GameMod/Telemetry/EventDiffer.cs` (pure, unit-testable).

`Diff(SimSnapshot? previous, …current fields…) → IReadOnlyList<SimEvent>` producing the T8.3
event types from snapshot deltas (situation per vessel, vessel set add/remove, active id,
warp value, parent body change = soi-changed). Unit tests with fixture pairs.

## T9.3 — Wire-up + in-game validation pass #2 (M9 exit)

- `OnFullyLoaded` (T6.1) additionally: create `SnapshotStore`, `SimFsTree.Build`, start
  `NinePServer` (port 0 → actual), hand `Func<int?>` sim-port provider to `VmHostOptions`
  (so the kernel cmdline gets `gatos.simport=<p>` — the guest supervisor mounts it
  automatically, T2.2).
- In-game checklist (append to `docs/VALIDATION.md`):
  `ls /sim/vessels/by-id/` lists vessels; `watch -n1 cat /sim/vessels/active/altitude/radar`
  live during a flight; `tail -f /sim/vessels/active/stream | jq .alt.radar` streams;
  Ctrl-C both cleanly; `cat /sim/events` during a launch shows liftoff-ish situation changes;
  time-warp changes `/sim/time/warp`. Kill the 9p server (debug menu button "Restart SimFs" —
  add it to the diagnostics menu) → guest supervisor remounts within ~4 s.

---

# M10 — Persistence & savegame shape

## T10.1 — Disk profiles

Files: `gatOS.Vm/DiskProfileSelector.cs`, changes in `VmHost`.

- Abstraction: `IDiskProfileSelector { string CurrentProfile { get; } }`. MVP implementation:
  `"default"` always (one persistent computer per install — D2). The VM boots whatever
  `CurrentProfile` says at `EnsureStartedAsync` time.
- Per-save binding (stretch, same task if cheap): investigate at runtime whether a stable
  savegame identifier is reachable from mod code (decompiled sources sweep: save/load pathways;
  the ksa skill has no documented save hook — **this is a research sub-task**, timebox 1 day).
  If found: profile = `save-<sanitized-id>`, switching saves while the VM runs →
  StopAsync + state Stopped (next session boots the right overlay). If not found: stay on
  `"default"`, document, move on.

## T10.2 — Base-image version migration

In `DiskManager`: at `EnsureBaseInstalled`, if `<profile>.toml` records `guest_version <
current`: keep the overlay working (its old base remains installed); log an info line; the
diagnostics window shows "disk uses guest v1 (current v2) — Reset disk to upgrade". Automatic
`qemu-img rebase` migration = M12 (risky; manual reset suffices for MVP).
**Tests:** version-mismatch path with two fake base versions.

## T10.3 — Crash consistency check

Integration test (`GATOS_IT=1`): boot, `touch /root/marker; sync`, **kill -9 qemu**, boot the
same overlay again → marker exists, fs journal recovered (dmesg grep optional). Proves the
no-clean-shutdown path is survivable.

---

# M11 — Packaging, licensing, release CI

## T11.1 — QEMU win-x64 bundle tooling

Files: `tools/fetch-qemu.sh`, `tools/fetch-qemu.ps1`, `tools/qemu-win64-files.txt`.

- Pin a QEMU version ≥ 11.0 and source (Stefan Weil w64 build). Script: download installer,
  verify pinned sha256, extract (7z), copy **only** the load-bearing subset into
  `vendor/qemu/win-x64/`: `qemu-system-x86_64.exe`, `qemu-img.exe`, required DLLs, BIOS blobs
  (`bios-256k.bin`, `kvmvapic.bin`, `vgabios*.bin` not needed with `-display none` — determine
  the minimal set **empirically**: start from the full set, delete, boot-test on Windows, record
  the final list in `qemu-win64-files.txt`; expect ~90 MB per analysis §3.10).
- The list file is committed; binaries are not.

> (As built — **done early**, pulled ahead to unblock M6 in-game testing and `GATOS_IT=1` on
> Windows (same pattern as T6.5). Deviations, each verified on the Windows game machine:
> **Pin = `qemu-w64-setup-20260501.exe`** (QEMU 11.0.50 snapshot `v11.0.0-12631-g54e84cdc7a`;
> Weil publishes only date-stamped snapshot installers, ≥ 11.0 satisfied), verified by
> **sha512, not sha256** — that is what upstream publishes (`.sha512` next to the installer).
> **Pins live in `tools/qemu-win64-files.txt`** (`#pin:` header lines) so both scripts parse
> one source of truth and cannot drift. **The DLL set is not hand-picked**: it is the exact
> transitive PE import-table closure of the two exes over the DLLs shipped at the installer
> root (`tools/Get-QemuImportClosure.ps1`, 104 DLLs — the Weil build load-time links GTK/SDL/
> spice/gstreamer, so they cannot be dropped). **share/ set determined empirically** per the
> plan (delete + boot-test the real guest): `bios-256k.bin`, `efi-virtio.rom`, `kvmvapic.bin`,
> `linuxboot_dma.bin`, `vgabios-stdvga.bin`. The plan's guess that `vgabios*.bin` is unneeded
> with `-display none` was **wrong**: q35 instantiates the default VGA (and the vapic option
> ROM) regardless; `multiboot_dma.bin`/`pvh.bin`/`pxe-virtio.rom` are confirmed unneeded.
> Total 113 files ≈ 123 MB (vs the ~90 MB estimate); QEMU `COPYING`/`COPYING.LIB` ship in the
> bundle. **fetch-qemu.ps1 bootstraps a portable 7-Zip** when none is installed (NSIS needs
> full `7z.exe`): administrative MSI extract (`msiexec /a`, no install, no elevation), MSI
> sha256 pinned and cross-checked against the winget community manifest; `fetch-qemu.sh`
> requires `7z`/`7zz` on PATH. Both scripts are idempotent (stamp under `vendor/qemu/`) and a
> from-scratch run reproduces the boot-tested tree bit-for-bit. **Headless test resolution**:
> `VendoredQemuSetup` (`[SetUpFixture]` in `gatOS.Vm.Tests` + `gatOS.Ssh.Tests`) points
> `QemuLocator.OverridePath` at `vendor/qemu/win-x64` on Windows when present.)

## T11.2 — THIRD-PARTY-NOTICES + GPL source mirroring

- `THIRD-PARTY-NOTICES.md`: QEMU (GPLv2, separate-process aggregation note), the guest image
  components (kernel GPLv2, busybox GPLv2, musl MIT, alpine-baselayout, dropbear MIT,
  qemu-guest-agent), SSH.NET (MIT), Tomlyn (BSD-2), purrTTY contract DLLs (purrTTY's license).
- Full texts under `third-party-licenses/` (shipped in dist, T6.5).
- **Corresponding-source obligation**: a `gatos-third-party-src` repo (or release assets on the
  guest releases) mirroring (a) the exact QEMU source tarball for the pinned Windows build,
  (b) Alpine source packages for the GPL components at the pinned versions (`apk fetch --source`
  list scripted in `guest/mirror-sources.sh`). Wire into `guest-image.yml` so every `guest-v<N>`
  release carries/links its sources. (Analysis §3.10 — SFLC guidance.)

## T11.3 — Dist assembly + size budget

- Extend T6.5's target: fail loudly (`<Error>`) on `Release` config if `guest/out` or
  `vendor/qemu/win-x64` is missing (CI must never publish a hollow dist).
- Record the dist zip size in CI output; budget alarm (warning) at >300 MB.

## T11.4 — Release workflow

Extend `.github/workflows/build.yml` to the full purrtty `release.yml` shape:
- main → prerelease `tip-<stamp>` (+ prune old tips); `release/<v>` → `v<v>` release;
  `feature/*` → build+test only. mod.toml version stamping identical (sed on
  `gatOS.GameMod/mod.toml`).
- Steps added before dist build: `guest/fetch-guest.sh` (pinned guest release) and
  `tools/fetch-qemu.sh` (cache both with `actions/cache` keyed on pin values).
- `GATOS_IT=1` integration tests run in this job (KVM on the runner; TCG fallback tolerated).
- Asset: `gatOS-<version>.zip` containing the `gatOS/` folder.

## T11.5 — Clean-machine install test (M11 exit)

On a Windows machine with nothing preinstalled: download release zip + purrTTY release, unzip
both into the KSA mods dir, launch, run the T6.6 checklist. Repeat on Linux with distro qemu
installed. Document any friction in README install section.

---

# M12 — Polish backlog (à la carte; each item independent)

| Item | Sketch |
|---|---|
| **P1 Writable control files** | `Twrite` handler + `VfsFile.WriteAsync`; `/sim/.../engines/<n>/active` accepts `0/1`; marshaled to the game thread via a `ConcurrentQueue` drained in `OnBeforeUi` (and solver-prefix if state must be solver-visible — ksa skill pattern); **gameplay gating decision first**. |
| **P2 restrict mode** | `restrict_network=true` → `-netdev user,restrict=on` + `guestfwd` is not needed (guest dials 10.0.2.2 which restrict blocks — verify; if blocked, switch 9p to a `guestfwd=tcp:10.0.2.100:564-cmd:...`-free alternative: keep an allow rule via `guestfwd` TCP target to the host server). Spike before promising. |
| **P3 Multi-computer fiction** | per-vessel busybox `unshare`/chroot trees + per-computer prompt/hostname; `/sim` already namespaced per vessel; menu lists "computers". |
| **P4 Debian-minbase variant** | parameterize `build-image.sh` (mmdebstrap path); non-cloud kernel for 9p (analysis §3.8). |
| **P5 savevm/loadvm resume** | internal qcow2 snapshot on shutdown, `-loadvm` on boot; **must validate under WHPX explicitly**; feature-flag it. |
| **P6 Per-install SSH key rotation** | first boot: QGA `guest-exec` writes a freshly generated pubkey to authorized_keys, then host forgets the baked key. |
| **P7 WHP-missing first-run UX** | detect TCG-only on Windows + WHP feature absent → status-window callout with copyable DISM command (needs reboot; never auto-elevate). |
| **P8 qemu-img rebase migration** | auto-rebase overlays onto a new base version with backup + rollback. |
| **P9 Idle auto-suspend** | stop VM after N min with no sessions AND no 9p reads (config). |
| **P10 purrTTY 3D screens** | out of scope here — purrTTY's renderer-neutral seam work, tracked in the purrtty repo. |

---

# Appendix A — 9P2000.L quick wire reference (for M7)

- Framing: `size[4: total incl. itself] type[1] tag[2] body…`, all little-endian.
  `NOTAG=0xFFFF` (Tversion only). Strings: `len[2] utf8…`. Qid: `type[1] ver[4] path[8]`;
  `QTDIR=0x80, QTFILE=0x00`.
- `Tversion: msize[4] version[s]` → `Rversion: msize[4] version[s]`.
- `Tattach: fid[4] afid[4: 0xFFFFFFFF] uname[s] aname[s] n_uname[4]` → `Rattach: qid`.
- `Twalk: fid[4] newfid[4] nwname[2] name[s]×n` → `Rwalk: nwqid[2] qid×n` (≤16 names).
- `Tlopen: fid[4] flags[4 (Linux O_*)]` → `Rlopen: qid iounit[4]`.
- `Tread: fid[4] offset[8] count[4]` → `Rread: count[4] data`.
- `Treaddir: fid[4] offset[8] count[4]` → `Rreaddir: count[4] {qid[13] offset[8] type[1] name[s]}×`.
- `Tgetattr: fid[4] request_mask[8]` → `Rgetattr: valid[8] qid mode[4] uid[4] gid[4] nlink[8]
  rdev[8] size[8] blksize[8] blocks[8] atime_s[8] atime_ns[8] mtime_s[8] mtime_ns[8]
  ctime_s[8] ctime_ns[8] btime_s[8] btime_ns[8] gen[8] data_version[8]`.
- `Tclunk/Rclunk: fid[4]/-`; `Tflush: oldtag[2]` → `Rflush`; `Rlerror: ecode[4 (Linux errno)]`.
- `Tstatfs: fid[4]` → `Rstatfs: type[4] bsize[4] blocks[8] bfree[8] bavail[8] files[8]
  ffree[8] fsid[8] namelen[4]`.
- Mode bits: `S_IFREG=0x8000, S_IFDIR=0x4000`; dirent types `DT_REG=8, DT_DIR=4`.
- Cross-check every layout against diod `protocol.md` before coding; fix this appendix if it
  disagrees.

# Appendix B — Definition of Done (every task)

1. Code + tests written; `dotnet build gatos.slnx` and `dotnet test gatos.slnx` green.
2. New behavior covered by tests at the right level (unit for pure logic; `GATOS_IT=1`
   integration for anything touching qemu/ssh/9p-kernel).
3. CLAUDE.md / README / this plan updated if commands, structure, or contracts changed.
4. Committed with the task id; no unrelated changes in the commit.
5. No game-assembly references outside `gatOS.GameMod`; no blocking calls on the render
   thread; no unbounded queues; every spawned thread/process has an owner that stops it.
