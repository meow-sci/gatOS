# Milestone & Feature Build Status

Full per-milestone implementation notes, class names, and as-built deviations from the plan.
Summaries and current status table live in [AGENTS.md](../AGENTS.md); this file has the detail.

---

## M0 — Repository scaffold: DONE

The solution, all 11 projects, shared build config, the logging shim, `GatOsPaths`, the vendored
purrTTY contract, and CI are in place and green.

---

## M1 — De-risking spike: DONE

All three gates passed against a real Alpine 3.24 guest (kernel 6.18): 9p synthetic files
`cat`/`tail -f`/Ctrl-C-Tflush from the kernel's v9fs client against a hand-rolled C# 9P2000.L
server; SSH.NET 2026.0.0 shell with **live resize** against dropbear; a known-good QEMU invocation.
The spike's throwaway code was deleted when M2 landed (per plan).

**`spike/NOTES.md` (committed) is REQUIRED READING before M3/M4/M7/M8 work** — notably:
- `i_size` must be truthful (the analysis §3.6 fake-size advice is wrong on ≥6.11 kernels)
- A `read()` completes only on buffer-full or two consecutive 0-byte Rreads
- "growing-log" (`tail -f`) vs "blocking-event" (`cat`) synthetic files are two distinct models
  the M7 VFS must support

---

## M2 — Guest image pipeline: DONE

`guest/build-image.sh` reproducibly builds the guest from pinned Alpine 3.24 mirrors — no
setup-alpine, no openrc; busybox init runs the hand-written `guest/rootfs-overlay/`:
- Static slirp net 10.0.2.15, OpenSSH `sshd` key-only root login, qemu-ga via wrapper
- `sim-mount`: 9p supervisor driven by `gatos.simport=<port>` kernel cmdline (0/absent = idle)
- `mnt-mount`: parallel supervisor for host folder mounts — `gatos.mntport=<port>` mounts `/mnt`
  once (**guest v10+**)
- `init-gatos`: best-effort `resize2fs /dev/vda` so root ext4 grows online to fill a host-resized
  overlay (`resize2fs` ships via `e2fsprogs-extra`, **guest v9+**)
- `init-gatos`: mounts the unified cgroup2 hierarchy at `/sys/fs/cgroup` (`nsdelegate`) and
  delegates every controller via `cgroup.subtree_control` — what OpenRC's cgroups service does on
  stock Alpine; without it container runtimes fail (`crun: invalid file system type on
  /sys/fs/cgroup`). Makes in-guest `apk add podman` work out of the box (**guest v17+**;
  `GuestCgroupIntegrationTests` asserts the contract)

The base stays small (`DISK_SIZE_MB`, 1.5 GiB); the host grows the per-save overlay to
`[disk_size_gb]` (default 8 GiB). Artifacts in `guest/out/` (never committed): partitionless-ext4
`base.qcow2` (zstd qcow2), `vmlinuz-virt`, trimmed `initramfs-virt` (`features="base virtio
ext4"`), `manifest.toml` (the host boot contract: kernel cmdline, ssh user/key, host-key pin =
sha256 hex of the raw key blob), the **static committed** ed25519 session keypair (the SSH keys
live in `guest/keys/` and are reused by every build/version — the committed OpenSSH ed25519 key is
baked directly as `sshd`'s host key, no conversion — so the host-key pin never drifts across
rebuilds; loopback-only access makes the committed keys safe), `sha256sums.txt`.

Build needs root on Linux (macOS dev: Docker; both documented in `guest/README.md`); a built-in
smoke test (also `--smoke-only`) boots the artifacts, checks `ssh 'echo ok'`, **verifies the
host-key pin**, and powers off — measured cold boot→sshd **5 s under TCG** on the dev Mac.
`.github/workflows/guest-image.yml` builds and publishes GitHub release `guest-v<N>` (N =
`guest/GUEST_VERSION`); consumers obtain artifacts via `guest/fetch-guest.{sh,ps1}`
(checksum-verified, no-op when current).

---

## M3 — gatOS.Vm (QEMU lifecycle): DONE

`VmHost` (`gatOS.Vm/VmHost.cs`) is the coalesced async state machine
(Stopped→Starting→Running→Stopping/Faulted): `EnsureStartedAsync` runs one shared boot for
concurrent callers (base install → overlay + `DiskLock` → 3 loopback ports → spawn →
SSH-banner readiness raced against process death; one retry on a hostfwd port clash),
`StopAsync` walks the shutdown ladder **QGA `guest-shutdown` → QMP `quit` → kill** and always
releases the disk lock; an unexpected exit while Running flips to Faulted (retryable) and frees
the lock.

Supporting cast (all `gatOS.Vm/`, all game-free):
- `GatOsPaths`: centralized filesystem paths — never hardcode elsewhere
- `PortAllocator`: loopback port management
- `QemuLocator`: bundled `win-x64/` on Windows per D5, PATH + Homebrew prefixes on unix
- `GuestManifest`: Tomlyn-parsed `manifest.toml` — the host↔guest boot contract
- `DiskManager`+`DiskLock`: versioned `base-v<N>.qcow2` install, kernel/initrd/manifest/ssh-key
  under `disks/guest-v<N>/`, all install-once via `CopyIfMissing`; **boots and pins the host key
  against the *installed* manifest read back from `disks/guest-v<N>/`, never the bundled dist copy**
  (so a re-keyed rebuild of the same version can't desync the pin from the already-installed base);
  overlays with **bare relative backing refs**, PID lock files with stale reclaim, never
  `qemu-img commit`; `EnsureOverlaySize(profile, minBytes)` grows an overlay's virtual size
  grow-only — `qemu-img info --output=json` reads the current size, `qemu-img resize` only when
  target exceeds it (never shrinks), so `disk_size_gb` can be raised but lowering it is a no-op;
  `VmHost.BootAsync` calls it after taking the disk lock and before spawn, best-effort, and the
  guest's `init-gatos` `resize2fs /dev/vda` grows ext4 to match
- `QemuCommandBuilder`: per-OS accel ladders `whpx|kvm|hvf→tcg`, **non-x64 hosts collapse to
  tcg**, `-cpu` per accel — `host` on KVM/HVF, a **named model (`Haswell`) on WHPX** (WHPX
  triple-faults the guest on `-cpu host`/`max` — "Unexpected VP exit code 4", confirmed on a
  Raptor Lake i9-13900K; any named model boots), `max` on TCG; `cpu_model` config overrides —
  injectable `OperatingSystemFacts`
- `QemuProcess`: 3 s survival window, `AccelFailureClassifier` + one forced-tcg retry,
  `logs/qemu-*.log` retention ×5, 100-line stderr ring, minimal QMP quit
- `ReadinessProbe`: reads the `SSH-` banner — a bare TCP connect is meaningless, slirp accepts
  from t=0
- `QgaClient`: 0xFF-sentinel `guest-sync-delimited` preamble, all failures soft

`IQemuProcess`/`IDiskManager`/`IQgaClient` seams + an internal `VmHost` ctor
(`InternalsVisibleTo`) make the state machine fake-testable; `gatOS.Vm.Tests/Integration/` boots
the real fetched guest. Measured on the dev Mac (TCG, worst case): boot→Running→clean QGA stop
≈ 10 s end-to-end.

---

## M4 — gatOS.Ssh (the ICustomShell implementation): DONE

`VmConnectionBroker` (`gatOS.Ssh/VmConnectionBroker.cs`) owns the shared `VmHost` (disposing
the broker stops the VM) and hands out one **new connected `SshClient` per session**, pinning
the guest host key against the manifest sha256 (mismatch → `HostKeyMismatchException`; one retry
on connection-refused).

`SshShellSession` (`gatOS.Ssh/SshShellSession.cs`) implements the vendored
`purrTTY.Core.Terminal.ICustomShell`:
- Trivial ctor (purrTTY's registry probe-instantiates and disposes, T0.5)
- `StartAsync` boots the VM lazily and opens an `xterm-256color` PTY at the launch size (a
  pre-start resize wins; failures map to `CustomShellStartException` carrying
  `VmStartException.UserMessage`)
- Input flows through `ShellInputQueue` (bounded 1 MiB, dedicated writer thread, overflow drops +
  logs once per episode — purrTTY's `PtyInputQueue` discipline; the first write failure terminates
  the session)
- `NotifyTerminalResize` → `ShellStream.ChangeWindowSize` (live SIGWINCH, verified in-guest)
- One `Terminate` path raises `Terminated` exactly once (clean close 0; connection error / VM
  fault / write failure 1 — sessions watch `VmHost.StatusChanged` for Faulted)
- **Stopping a session never stops the VM**

Internal `IShellBroker`/`IShellChannel` seams (+ the `SshShellChannel` adapter owning the
client+stream pair) keep the session unit-testable without SSH.NET: `gatOS.Ssh.Tests` = 19
fake-driven unit tests + 2 `GATOS_IT=1` fixtures against the real guest (broker echo-ok +
tampered-pin rejection; full session: prompt, `stty size` 24 80, live resize → 30 120, `$TERM`,
two concurrent sessions on one VM, session stops leave the VM Running).

---

## M5 — Upstream purrTTY changes: DONE

In the **purrtty** repo, commits `9fb5e13`/`a56966a`:
- **T5.1**: `purrTTY.GameMod/mod.toml` exports `purrTTY.CustomShellContract` + `purrTTY.Logging`
  over the StarMap ALC (`[StarMap] ExportedAssemblies`) so gatOS's `ImportedAssemblies` (M6)
  resolves purrTTY's loaded copies — one type identity, one shared `CustomShellRegistry.Instance`
- **T5.2**: the New Tab / New Window menus append custom shells registered by other mods,
  enumerating `CustomShellRegistry.GetAvailableShells()` live per draw (probe-free; solves
  cross-mod registration timing without a refresh hook), launching via
  `ProcessLaunchOptions.CreateCustomGame(id)`

**Still pending: the purrTTY tip release cut** (next push to purrtty `main`) — M6 in-game
testing needs a purrTTY install carrying both changes.

---

## M6 — gatOS.GameMod (in-game integration): Code DONE; T6.6 in-game pass pending

`Mod` (`gatOS.GameMod/Mod.cs`) is the `[StarMapMod]` entry, a **partial class split on the
game-assembly boundary**: `Mod.cs` itself uses no KSA/Brutal types, so the project builds on CI
without the private game DLLs; the game-coupled half (`Game/Mod.Game.cs` +
`Game/BrutalModLogger.cs`) compiles only when `KSAFolder/KSA.dll` exists (csproj
`KsaAssembliesPresent` gate) and is reached through `partial void` seams (`InstallGameLogging`,
`DrawGameUi`) whose calls drop out otherwise.

`OnFullyLoaded` (never throws):
1. Swap `ModLog` to a Brutal `LogCategory("gatOS")` sink — isolated in `TryInstallGameLogging`
   so a load failure can't abort init; `BrutalModLogger`'s ctor refuses while `LogSystem.IsEnabled`
   is false
2. Resolve `GatOsPaths.ModDir` from the entry assembly
3. `ModAssets.Validate()` (T6.2): manifest schema + artifact files + `QemuLocator.Find()`, all
   problems folded into one `AssetStatus.Error` string
4. `GatOsConfig.LoadOrCreate(ConfigFile, BundledConfigFile)` (T6.3): Tomlyn 2.6 serializer,
   snake_case, clamp+log normalize; **section-grouped save** — `GatOsConfig.Serialize()` lets
   Tomlyn render each `key = value`, then regroups lines under
   `# ===== COMMON/TELEMETRY/CONTROL/TRANSPORTS =====` headers with per-key inline comments,
   common knobs first, plus a catch-all so a newly added property is never dropped — written
   atomically temp+rename; bad files → in-memory defaults, never overwritten. **First run seeds the
   data-dir `gatos.toml` from the template shipped in the mod folder**
   (`GatOsPaths.BundledConfigFile` = `<modDir>/gatos.default.toml`, the committed
   `gatOS.GameMod/Configuration/gatos.default.toml`) so settings edited before launch take effect;
   if that template is absent it writes generated defaults instead. The template is a **distinct
   filename** on purpose: on Windows the install dir and the data dir are the **same folder**
   (`Documents/My Games/Kitten Space Agency/mods/gatOS/`), so shipping the live `gatos.toml` would
   let a mod-update overwrite a player's config — and the deploy now **excludes `gatos.toml` from
   its wipe** for the same reason. Existing flat `gatos.toml` files load unchanged and are rewritten
   in the sectioned layout on the next save
5. Build `VmHost`+`VmConnectionBroker` (**no boot**, D2)
6. Register shell `"gatos"` (purrTTY absence detected after the fact: the contract assembly
   resolving from gatOS's own folder means the vendored fallback loaded)

`Unload` = `broker.DisposeAsync().AsTask().Wait(15 s)` (the dispose is the 10 s-grace
QGA→QMP→kill ladder).

**T6.4 diagnostics** — the gatOS menu (Status/Start VM/Shut Down VM/**Telemetry submenu**/Restart
SimFs/Open Data Folder/Reset Disk…+confirm-modal), drawn two ways with **identical content**
(purrTTY's exact pattern): via `[ModMenuEntry("gatOS")]` when the ModMenu mod is present, else via
a Harmony postfix on `KSA.Program.DrawProgramMenusHook()` adding a top-level `gatOS` menu
(`MenuFallbackPostfix`/`InstallMenuFallback` in `Game/Mod.Game.cs`, gated on
`ModLibrary.Find("ModMenu")`) — both call the shared `DrawMenuContentSafe`; plus an ImGui status
window (state, accel + WHPX DISM hint when tcg-on-Windows, ports, uptime, guest version, config,
newest qemu log — cached per `VmStatus` transition — fault reason, asset status, action note,
**a Telemetry block: sample-rate slider + per-stream checkboxes + a live perf readout
(sample-time avg/max/last, command-drain avg/max, and MQTT publish avg/max via `PerfStat`, with
a Reset)**); all actions `Task.Run`, draw code reads volatile state only (rule 5).

Two load-order subtleties: game-typed *statics* live in a nested `Palette` class (field types
resolve at type load; `Mod` must load without game DLLs) and the partial impls are `NoInlining`
so missing-assembly faults hit the guarded call sites.

**Verified 2026-06-12 by a headless smoke driving the deployed dist** (LoadFrom + reflection):
init, registration, registry-created session booting the real VM (WHPX fail → auto TCG retry),
echo + launch-size + live resize, session stop leaves VM Running, 2.2 s clean unload —
see `docs/VALIDATION.md`.

**T6.7 (WHPX-enabled run): DONE** — with `HypervisorPlatform` enabled on the game machine, the
real `VmHost` path now boots **`accel whpx`** end-to-end (verified 2026-06-13 via
`VmHostIntegrationTests`). This surfaced and fixed a real bug: WHPX triple-faults the guest under
`-cpu host`/`max` ("Unexpected VP exit code 4"), silently falling back to TCG; `QemuCommandBuilder`
now emits a named CPU model (`Haswell`, AES-NI for fast in-guest SSH) under WHPX, overridable via
the `cpu_model` config.

**Pending: T6.6 in-game pass** (needs the purrTTY tip release with M5).

---

## M7 — gatOS.NineP (the 9P2000.L server): DONE

Three layers, all game-free.

**`Vfs/`**: the seam SimFs implements — `VfsNode`/`VfsDirectory`/`VfsFile` (ctor takes
`(name, qidPath)`; the tree assigns qids) + per-open `IVfsFileHandle`; **sizes are truthful** —
`VfsFile.Size` is abstract (no fake-4096; spike rule 1 makes it ENODATA-fatal) and an opened fid
stats its handle's own `Size`; `StaticTextFile` snapshots its provider per open; `DelegateDirectory`
covers fixed and dynamic dirs; `VfsErrorException(errno)` surfaces a chosen `Rlerror` (anything
else → EIO).

**`Protocol/`**: `MessageType` (diod numbers), `NinePReader`/`NinePWriter` (`BinaryPrimitives`,
string = `len[2]`+UTF-8, `PatchUInt32` for count back-patching), `Qid`, `LinuxErrno`,
`ProtocolException` (malformed frame ⇒ close connection).

**`Server/`**: `NinePServer` (listens on **loopback** — slirp delivers guest→10.0.2.2 to
127.0.0.1, no firewall prompt; `StartAsync(port 0)` → `Port`) runs one `Session` per connection:
every message dispatched as its own task with a per-tag CTS (a parked blocking read never stalls
the loop), responses serialized by a write lock, fids hold walk *paths* (so `..` needs no parent
pointers), readdir **includes `.`/`..`** with next-ordinal cookies and a per-fid listing snapshot
for stable paging, reads clamp to msize−11, Twrite → EACCES, unknown types → EOPNOTSUPP;
**Tflush**: cancel + suppress the old reply, await the handler, then Rflush — a flushed tag is
never answered.

**Write/create surface** (added for host folder mounts): `Tlcreate`/`Tmkdir`/`Tunlinkat`/
`Trenameat`, real `Tsetattr` size-truncate + `O_TRUNC`-on-open, and `Tgetattr` now reports a
node's real mtime (`VfsNode.ModifiedUnixSeconds`, default -1 = the old fixed `AttrTime` for
synthetic nodes). The mutation surface is virtual on `VfsDirectory` (`CreateFile`/
`CreateDirectory`/`Unlink`/`Rename` + `IsWritable`), defaulting to `EROFS` so the synthetic `/sim`
tree stays byte-for-byte read-only; only `HostDirectory` overrides them.

A follow-up review (2026-06-13) hardened the server against tag-reuse-while-in-flight teardown
(`Session` frees a tag *before* writing its reply — this was the real cause of the `find /sim`
flake).

`gatOS.NineP.Tests` = 40 tests (pre-host-mount) + 18 `HostMountTests` + 43 additional
game-free tests added in the 2026-06-13 review: codec round-trips, hand-built golden
Rversion/Rgetattr/Rreaddir frames (`NinePServerOptions.AttrTime` injectable), and the conformance
suite driven by a **public managed test client** (`TestClient/NinePTestClient`, tag-correlated,
reused by SimFs.Tests via project reference).

---

## M8 — gatOS.SimFs (the `/sim` tree): DONE

**`Snapshots/`**: the immutable game-free records (`SimSnapshot`/`VesselSnapshot`/…, plan shapes
verbatim) and `SnapshotStore` — volatile `Current` + a TCS swapped per `Publish` (lock-free
reads, capture-and-recheck in `WaitForNextAsync`, intermediate snapshots are *skipped, never
replayed*).

**`SimFsTree.Build(store)`** → `/time/{ut,warp}`, `/vessels/active` (alias listing the active
vessel's children directly — `active/…` and `by-id/…` walk to **identical qids**),
`/vessels/by-id/<sanitized>/…` (id/name/situation/parent, position/{cci,lat,lon}, velocity,
attitude, altitude, mass, orbit + battery only-when-present, engines/<n>, tanks/<resource>,
stream), `/events`; dynamic nodes are transient but qids are interned by relpath; ids sanitized to
`[A-Za-z0-9._-]` with `~N` collision suffixes; vanished vessels → ENOENT.

**`Formats`** is the **frozen user-facing surface**: G9 invariant doubles, `0`/`1` flags,
space-separated vector/quat, one value + LF per scalar file, relaxed-escaping NDJSON lines.

The two spike-mandated file models:
- `StreamFile` = growing-log (per-open buffer seeded with the current line so size is never 0,
  pump task appends per observed publish, 0 bytes at the frontier — `tail -f` follows, `cat`
  samples; 256 KiB cap drops whole lines + `{"notice":"dropped"}`)
- `EventsFile` = blocking-event (read parks for the next event, delivers, then owes two 0-byte
  reads; size claims 1 — the only always-truthful value for variable-length lines)

`gatOS.SimFs.Tests` = 38 tests incl. the **M7+M8 exit**:
`Integration/SimMountIntegrationTests` (GATOS_IT) boots the real guest with
`SimPortProvider = () => server.Port`, the guest's `sim-mount` supervisor mounts `/sim` on its
own, and the real v9fs client proves live scalars, the alias, `tail -f stream`, the blocking
events read, and Tflush-survival — this **supersedes the planned ubuntu mount-smoke.sh**
(T7.5/T8.4 as-built notes). Verified: full `GATOS_IT=1` suite 172/172.

---

## M9 — Live `/sim` telemetry: Code DONE; T9.3 in-game pass pending

**Game-free pieces** in `gatOS.SimFs/Telemetry/`:
- `EventDiffer`: previous/current snapshot pair → six fixed event types; null previous = baseline,
  no events
- `SampleClock`: dt accumulator, drift-free phase, long-frame backlog dropped; **rate mutable live
  via `SetRate`**
- `Sanitize`: NaN/Inf→0, radius→altitude for KSA's from-center apsides
- `TelemetrySettings`: runtime-mutable cadence + per-stream gates — volatile fields the sampler
  reads every tick

**Game-coupled accessor half** in `gatOS.GameMod/Game/TelemetrySampler.cs` (compile-gated like
all `Game/**`): every KSA read verified against the decompiled sources — vessels via
`Universe.CurrentSystem.All.UnsafeAsList()`, `Name = Id` (KSA has no separate display name),
lat/lon via the ready-made `IParentBody.GetLlaFromCcf`, `Orbit.Inclination` radians→deg, engines
from `EngineController.VacuumData` (Isp computed: thrust/(massflow·g₀)), tanks from per-`Mole`
SoA state, battery from `Parts.Batteries` (`Joules.Value()`); one try/catch per vehicle, publish
via one volatile swap (threading rules 1–2). Beware: the instance `double3.Transform` extension
drags BepuUtilities into overload resolution — use the static overload.

**Wire-up** (T9.3) in `Mod`: `OnFullyLoaded` builds `SnapshotStore` + `SimFsTree` and binds the
`NinePServer` (ephemeral loopback port) **before** the `VmHost`, whose `SimPortProvider` hands
the port to the kernel cmdline (null when the bind failed → guest idles); `OnBeforeUi` →
`SampleTelemetry` partial seam (NoInlining, one-error disable latch); idle gate = VM
Starting/Running or a connected transport client (`NinePServer`/`SimHttpServer`/`SimMqttBroker`
session count > 0) — the sampler does zero work otherwise; `Unload` disposes the server after
the VM.

**Diagnostics**: **Restart SimFs** menu item (rebinds the **same port** — it is baked into the
running guest's cmdline; the supervisor re-establishes the mount unaided) and a SimFs status row.

**Verified 2026-06-12 by the headless dist smoke** (supervisor mounts `/sim` by itself during
boot, warp readable, restart-remount unaided, 3.3 s clean unload — `docs/VALIDATION.md`); full
`GATOS_IT=1` suite 187/187.

**Pending: the T9.3 in-game pass** (same purrTTY-tip-release blocker as T6.6).

---

## G1–G4 — KSA Game Integration: Code DONE; in-game pass pending

Full read + write surface and its churn firewall, per `KSA_GAME_INTEGRATION_PLAN.md` (Parts 1–5).

### G1/G2 — Command pipeline + integration-layer foundation

**9p server write path** (`gatOS.NineP`): `IVfsWritableFileHandle`, `VfsFile.IsWritable`/
`OpenWrite`, writable files stat `0644` (kernel pre-checks write permission from getattr), `Tlopen`
accepts `O_WRONLY`/`O_RDWR` on writable nodes, `Twrite` dispatches to the handle, `Tsetattr`
accepts the `O_TRUNC` size-truncate on writable files (no-op) and `Tfsync` trivially succeeds;
errnos `EBUSY`/`ETIMEDOUT` added.

**Command pipeline** (game-free, `gatOS.SimFs/Commands/`):
- `SimCommand`: vessel id + action key + ordinal + value + `CommandPhase`
- `CommandResult`/`CommandOutcome`→errno
- `CommandQueue` (`ICommandSink`): transport threads `SubmitAsync` + await with timeout →
  ETIMEDOUT, game thread `Drain(phase, ICommandExecutor, max)`; abandoned-on-timeout commands are
  skipped; one TCS per command with `RunContinuationsAsynchronously` so the awaiter never resumes
  inline on the game thread
- `ControlFile` (STATE: `Flag`/`Fraction`) + `TriggerFile` (TRIGGER) over a shared line-buffered
  `CommandFile` (actuates on the first newline → real errno on the failed `write(2)`;
  unterminated writes actuate best-effort on clunk)

**Control surface** added to `SimFsTree.Build`:
- `engines/<n>/active` (STATE)
- `ctl/{ignite,shutdown,engine,lights}` (`ctl/engine` is the readable+writable ignition toggle:
  read = live `EngineOn`, write `1`/`0` = ignite/shutdown — distinct from the per-engine "allowed
  to fire" `engines/<n>/active`)
- `animations/<n>/goal`, `solar/<n>/goal` (solar-filtered animation view, same ordinal)
- `/sim/status/` integration-health tree (`game_version`, `sampler`, `accessors` NDJSON,
  `transports`)

**Integration layer** (only KSA-touching code, all under `gatOS.GameMod/Game/Ksa/`):
- `KsaAnchorAttribute`+`ChurnRisk`: per-member provenance; the churn playbook is a grep over
  `[KsaAnchor]` build breaks
- `Readers/VesselReader`: the M9 sampler reads refactored out + new lights-master/animations reads
- `Actuators/{Engine,Light,Animation}Actuator`
- `KsaCatalog` (`ICommandExecutor`): resolves the target vessel, authority-gates per G-D1,
  dispatches to the actuator, and owns the **health latches** `KsaHealth` — a thrown KSA call
  latches that accessor degraded → EOPNOTSUPP, logs once, surfaces in `/sim/status/accessors`

### G3 — Read-surface expansion

**Game-free `SimSnapshot` extensions + `SimFsTree`:**
- `/sim/system` and `/sim/bodies/<id>/…` (celestial catalog: mass/radius/mu/soi/rotation,
  position/velocity ecl, orbit, atmosphere, ocean — planets/moons are `Celestial`, the star is
  a separate `StellarBody`)
- `time/{sim_dt,warp_speeds,auto_warp}` + the `time/alarm` blocking sim-time wake device
  (`AlarmFile`, writable + blocking read)
- Per-vessel: `telemetry` (atomic JSON doc), `controlled`, `com`, `position/ecl`, `velocity/cci`,
  `navball/…`, `environment/…` (pressure/density/dynamic-pressure/accel/g-force), orbit extras
  (lan/argpe/true-anomaly/time-to-ap-pe/next-patch), `encounters`, engine
  throttle/propellant/min-throttle, tank `fraction`, battery `fraction`/`capacity`,
  `power/{produced,consumed}`, and module dirs `rcs/ solar/ generators/ lights/ docking/
  decouplers/`
- **`srb/<n>/` (added with the KSA `2026.7.9.5018` upgrade)** — solid rocket motors, the one part of
  the propellant surface `tanks/` structurally cannot show: KSA stores solid grain on a
  `SolidGrainSegment` module rather than a `Tank`, so a booster contributes nothing to `tanks/` while
  still counting in `mass/propellant`. Per motor: `engine` (cross-link to the `engines/<n>` that
  ignites it), `part`, `substance`, `grain`/`grain_shape`, `valid`/`error`, `active`, `propellant`,
  `mass`/`mass_initial`/`mass_unburnable`/`mass_burnable`/`fraction`, `mass_flow`, `burn_time`,
  `burning_area`, `chamber_pressure`/`chamber_temp`, `exit_pressure`/`exit_temp`, `area_ratio`, plus a
  per-segment `segments/<m>/` breakdown. **Read-only** — KSA forces a solid's throttle to 0 or 1, so
  ignition stays on the engine surface. `Readers/VesselReader.SampleSrbs` →
  `SrbSnapshot`/`SrbSegmentSnapshot`; catalog in `SPEC_9P_FILESYSTEM.md` §3.4.8.
- New `/sim/events`: engine-state, flameout, docked/undocked, decoupled, animation-complete,
  battery-depleted/charged

**Integration layer additions (`gatOS.GameMod/Game/Ksa/`):**
- `Readers/{VesselReader (now core + a guarded enrich pass),BodyReader}`

### G4 — Full control surface

**Writes:** `ctl/{throttle,stage,rcs,translate,attitude_mode,attitude_frame,attitude_target,burn}`
(`ctl/translate` added 2026-07-04: manual RCS translation by body-axis signs — reflection on
`_manualControlInputs.ThrusterCommandFlags`, the throttle pattern; bang-bang, latches until `0 0 0`;
`TranslateActuator` + `VesselSnapshot.TranslateCmd` read-back via `GetThrusterFlags`; enables EVA-kitten
point-to-point flying, tutorial `site/…/guides/eva-taxi-to-a-part.mdx`),
`engines/<n>/min_throttle`, `rcs/<n>/active`, `lights/<n>/{on,brightness,color,inner_angle,outer_angle}`,
`decouplers/<n>/fire`, `docking/<n>/undock`, `ctl/focus` (+ `bodies/<id>/focus`), and the
**`/sim/debug/`** cheat namespace (gated by
`[control] debug_namespace`: `vessels/<id>/{teleport,refill_fuel,refill_battery}`,
`vessels/<id>/docking/<n>/pushoff_impulse`, `time/warp`, `focus` (camera-by-id, vehicle/body),
`control_vessel` (focus + control); `vessels/<id>/impulse` joined the namespace 2026-07-04 —
see its own section below).

**New command archetypes:** `ControlFile.Number`, `VectorControlFile`, `EnumControlFile`,
`TokenControlFile`; `SimCommand` gained `Values` (vectors) + `Token` (enum/free tokens).

**Integration layer:**
`Actuators/{Engine,Light(+per-instance clone),Animation,Staging,Throttle(reflection),Rcs,
Decoupler,Docking,Camera,FlightComputer,Debug}Actuator`; `KsaCatalog` dispatches all actions
(debug-namespace exempt from the authority gate). `DockingActuator.Undock` enqueues the game's own
`InputEvents.VehicleDockingInputData{Undock=true}` (→ `Vehicle.Split` using the port's `PushoffImpulse`,
N·s — 4750/rev 4683 renamed it from `PushoffForce`, N); `SetPushoffImpulse` overwrites that live
separation impulse (the debug knob). `CameraActuator.Focus`
moves the main-viewport camera to any `Astronomical` (vessel **or** celestial — the `camera.focus`
action resolves the target via `CurrentSystem.Get(id)`, bypassing the vehicle-only path/authority gate
since it only moves the view).

**Solver phase:** a Harmony `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`
(`Mod.DrainSolverCommands` via `InstallSolverHook`/`RemoveSolverHook` partial seams) drains
`CommandPhase.Solver` commands inside the physics step — the debug refills **and the
flight-computer setpoints** (`attitude_mode`/`attitude_frame`/`attitude_target`/`burn`), which
KSA's async solver snapshot-restores via `FlightComputer.CopyFrom` so a frame-phase write would
be clobbered. Phase is derived from the action by `SimCommand.Phase`
(`SimCommand.SolverActions` is the one source of truth). See also: threading rule 1.

Co-located reference: **`docs/KSA_INTEGRATION_MATRIX.md`** (G1–G4 + documented deferrals: aero
`cda` [private], `parts/<instanceId>` tree, per-nozzle engine internals, gimbal command, RCS
pulse).

**Tests:** NineP write-path golden + conformance (`Tlopen`/`Twrite`/`Tsetattr`/`Tfsync`/mode
bits), SimFs `ControlFile`/`TriggerFile`/`CommandQueue` unit tests + a control-surface fixture
over the 9p client, and a `GATOS_IT` control-surface guest fixture (`echo 1 > …/engines/0/active`
actuates; `echo bogus` → nonzero EINVAL).

**Pending: the G1–G4 in-game pass** (same purrTTY-tip-release blocker as T6.6).

---

## G5 — HTTP transport: DONE

**`gatOS.Http`**: a raw loopback-`TcpListener` HTTP/1.1 server (not `HttpListener` — that needs
http.sys URL-ACL/admin on Windows; not GenHTTP — avoids a heavy dependency tree in the mod ALC)
serving `/v1`:
- JSON snapshot projections: `snapshot`/`time`/`status`/`system`/`bodies[/{id}]`/
  `vessels[/{id}[/telemetry]]`
- SSE `GET /v1/events` and per-vessel `GET /v1/vessels/{id}/stream`
- Long-poll `GET /v1/time/wait`, `GET /v1/openapi.json`
- `POST /v1/command` carrying the `SimCommand` shape with `CommandOutcome`→HTTP-status+
  `{errno,message}` (debug.* gated)
- **Field-level filesystem mirror** `GET /v1/fs/<path>` (one endpoint per `/sim` leaf, raw text
  value), `?stream=1` for an SSE feed, and `POST /v1/fs/<path>` to write/actuate one field — all
  resolved by walking the same `/sim` VFS tree via `VfsScan`

All reads use the shared `SimJson` projection layer (transport parity). Config:
`[http] enabled`/`preferred_port`=4242 ephemeral-fallback, `http_field_endpoints`. `VmHost`/
`QemuCommandBuilder` inject `gatos.httpport` on the cmdline.

Tests: 37 (HttpClient over the live socket, incl. `/v1/system`, `/v1/vessels/{id}` +
`/v1/bodies/{id}`, the per-vessel stream SSE, and the `/v1/fs/<path>` field read sweep /
per-value SSE / per-archetype writes / error + disabled paths).

---

## G6 — TypeScript SDK: DONE

**`examples/sdk-ts/`**: a TypeScript/Bun SDK with `FsTransport`+`HttpTransport` behind one typed
`GatosClient` (the per-vessel `telemetry` doc is the same `Formats.VesselTelemetry` JSON over
both — parse-identical; the 9p file appends a trailing LF per the file convention, the HTTP/MQTT
bodies do not), reactive events, warp-aware time helpers, `GatosError` errno mapping, and example
scripts + a pure-shell README.

---

## G7 — Serial/bus framing + virtio-serial bridge: DONE

**`gatOS.Bus`**: the framing codecs — `Ccsds` (TM space packets), `Nmea` (sentences + XOR
checksum), `ScpiCommandPort` (`CTL:ENG0:ACT 1`→`SimCommand`→sink, `OK`/`ERR <errno>`),
`SerialTelemetry` (NDJSON/NMEA/CCSDS frames) — **plus the live serial bridge**: `SerialBridge`
(duplex over one `Stream` — telemetry pump out + SCPI command lines in, both targeting the active
vessel) and `SerialBridgeConnector` (connect-with-retry to the QEMU `gatos.serial` chardev,
mirroring `QgaClient`).

`VmHost` allocates a 4th loopback port + `QemuCommandBuilder` wires a
`virtserialport,name=gatos.serial` (the guest's init symlinks it to
`/dev/virtio-ports/gatos.serial`, no rebuild needed); `Mod` starts/stops the connector on the
VM `Running`/stop transitions per `[serial] serial_telemetry_port`/`serial_command_port`/
`serial_mode`/`serial_interval_ms`.

Tests: 32 (codec/SCPI + `SerialBridge`/connector over a loopback socket pair).

**Validated in-guest** (2026-06-13): guest reads an NDJSON frame off
`/dev/virtio-ports/gatos.serial` and an `echo CTL:… >` SCPI command actuates (`OK`) with a bad
line rejected (`ERR EINVAL`). See `docs/VALIDATION.md`.

---

## MQTT transport: DONE

**`gatOS.Mqtt`** (MQTTnet): an **embedded MQTTnet broker** (`SimMqttBroker`) in the host process
on a loopback port (guest reaches it at `10.0.2.2:<port>`, like the others — no external broker)
over the same `SnapshotStore` + `CommandQueue` (and the same `SimJson` projection layer HTTP uses):

**Topics published:**
- Retained: `gatos/time`, `gatos/status`, `gatos/system`, `gatos/bodies`, `gatos/snapshot`
  (whole world), `gatos/vessels/<id>/telemetry` (compact SDK-stable doc),
  `gatos/vessels/<id>/snapshot` (full granular record)
- Non-retained: `gatos/events`
- Field-level (config `[mqtt] mqtt_field_topics`, cadence `field_feed_hz`=4): retained
  `gatos/sim/<path>` leaf-by-leaf, canonical `vessels/by-id` only (duplicate `active` alias →
  `gatos/sim/vessels/active_id` pointer); client writes one field by publishing to
  `gatos/sim/<path>/set`

**Efficiency**: both pumps do zero serialization while no MQTT client is connected
(`ConnectedClients` gate — the only eager transport, so this keeps idle cost near-zero; a connect
wakes the parked pump via a linked-CTS race and force-republishes the current retained baseline);
**publish changed-only** (byte-compare vs last payload); **serialize straight to UTF-8**
(`SimJson.*Bytes` / `Formats.VesselTelemetryUtf8`, no intermediate string).

**Commands**: clients publish a JSON `SimCommand` to `gatos/command`; outcome published to
`gatos/command/result` (debug.* gated).

Config: `[mqtt] enabled`/`preferred_port`=1883 ephemeral-fallback. `VmHost`/`QemuCommandBuilder`
inject `gatos.mqttport`; guest exports `$GATOS_MQTT=sim:<port>` (active on guest v3).

`gatOS.Mqtt.Tests` (19): connect a real MQTTnet client to the broker (full topic set incl.
per-vessel `snapshot`, `gatos/events`, field-level `gatos/sim/<path>` read sweep + `/set`
actuation across every write archetype + set error paths, enriched time/status, retained delivery
to a late subscriber, command routing + errno, debug gating, and that consumed commands are not
rebroadcast). Full `GATOS_IT=1` suite green on guest v3, zero warnings.

**Validated in-guest** (2026-06-13): TCP reachability, `$GATOS_MQTT` env, live telemetry read
via wget. See `docs/VALIDATION.md`.

---

## MCP transport: DONE

**`gatOS.Mcp`** is the first-class, AI-oriented transport. It uses the official
`ModelContextProtocol.Core` 2.2.0 SDK for discovery, protocol negotiation, resource/tool schemas,
and dispatch; `SimMcpServer` supplies the deliberately small loopback-only, stateless Streamable
HTTP host. It binds `http://127.0.0.1:<port>/mcp`, prefers port 4243, and falls back to an ephemeral
port when needed (or immediately when `mcp_preferred_port = 0`). The server has no bearer-token
scheme; its loopback bind and exact local `Host`/`Origin` checks are the v1 boundary. It rejects
sessionful MCP requests, has bounded request concurrency, and exposes its bound port/request/error
status in the gatOS status window.

`McpRegistry` projects the shared `SnapshotStore`, `ICommandSink`, and the existing audio, camera,
and schedule stores into logical JSON resources and namespaced `gatos.*` tools. It never calls KSA
from a request thread and adds no KSA reflection or actuator binding. `McpPresenters` owns raw-id
entity lookup, sectioned vessel documents, 50-default/1,000-maximum cursor lists, waits, capability
discovery, and the common snapshot envelope. `McpToolHandlers` maps concise controls, the canonical
command envelope, same-tick batches, and timed batches back to the existing command catalog.

Config: `mcp_enabled = true` and `mcp_preferred_port = 4243` in `gatos.toml`. Responses have no
JSON result-size cap or inspection; HTTP request framing is capped at 24 MiB, and audio/camera-track
uploads are chunked. `/sim/display` remains deliberately outside the MCP contract. The complete
public resource, tool, schema, error, and maintenance contract is [`SPEC_MCP.md`](../SPEC_MCP.md).

---

## Host folder mounts (`/mnt/<name>`): DONE (requires guest v10)

A user-requested feature distinct from the `/sim` telemetry surface: share real **HOST OS
folders** into the guest, mounted at `/mnt/<name>`, off by default. Reuses the existing
9p-over-slirp mechanism — a **second `NinePServer`** (separate from the `/sim` server) whose
root is a `HostMountTree` (`gatOS.NineP/Vfs/`): one directory listing each configured mount as
a host-backed `HostDirectory`, so the guest mounts the root **once** at `/mnt` and
`/mnt/<name>/…` is the live host folder.

**Not** a `/sim` transport — exempt from the transport-parity rule.

**Passthrough VFS** (`gatOS.NineP/Vfs/HostDirectory.cs`, `HostFile.cs`, `HostMount.cs`,
game-free):
- `HostFile`/`HostDirectory` stat the live file (truthful `Size`/real mtime)
- Positional I/O via `System.IO.RandomAccess` (thread-safe, no shared seek state)
- Every resolved path is confined to the mount subtree (single-component names, `GetFullPath` +
  within-root check — no `..`/absolute escape)
- **Per-mount read-only/read-write**: each mount is read-only by default; `read_only = false`
  grants full passthrough (create/edit/delete/rename real host files); a read-only mount rejects
  opens-for-write with `EACCES` and create/mkdir/etc. with `EROFS`

**Config**: a TOML `[[mounts]]` array (`GatOsConfig.MountSpec` — `name`/`path`/`read_only`);
names sanitized to a safe single path component and de-duped at load; `Serialize()` hand-renders
the `[[mounts]]` blocks (Tomlyn would inline the whole list onto one unreadable line — both forms
deserialize identically; Windows paths render as literal `'…'` strings so backslashes need no
escaping).

**Wire-up**: `Mod` starts `StartMountsServer` (after the MQTT broker) only when `[[mounts]]` is
non-empty, feeds `VmHostOptions.MntPortProvider`, disposes at unload; the status window gains a
**Mounts** row. `VmHost`/`QemuCommandBuilder` inject `gatos.mntport=<port>` (0/absent = nothing
under `/mnt`); the guest's **`mnt-mount`** supervisor (`guest/rootfs-overlay/sbin/mnt-mount`,
respawned by inittab, mirrors `sim-mount` with a raised `msize`) mounts `/mnt` once it sees a
non-zero port.

**Requires guest image v10** (`GUEST_VERSION` is bumped to 10); the guest must be
rebuilt/released before `GATOS_IT` can run `HostMountIntegrationTests`.

**Tests**: `gatOS.NineP.Tests/HostMountTests` (18 fixtures over the managed client against temp
dirs — read/stat/mode/mtime, write/create/mkdir/unlink/rename/truncate, read-only rejection,
name-traversal `EINVAL`) plus the `GATOS_IT` guest fixture
`gatOS.SimFs.Tests/Integration/HostMountIntegrationTests` (real guest mounts a ro + a rw host
folder and read-writes it end to end — runnable once guest v10 is published). Full non-IT suite
green, zero warnings.

---

## T11.1 — QEMU win-x64 bundle tooling: DONE

`tools/fetch-qemu.{ps1,sh}` populate `vendor/qemu/win-x64/` from the pinned Weil installer;
pin + trimmed file list live in `tools/qemu-win64-files.txt`, derivation helper
`tools/Get-QemuImportClosure.ps1`. On Windows, headless tests resolve that vendored bundle via
`QemuLocator.OverridePath` (`VendoredQemuSetup` in `gatOS.Vm.Tests`/`gatOS.Ssh.Tests`/
`gatOS.SimFs.Tests`), and `QemuLocator.Find()` throws the typed `QemuNotFoundException` (not
`InvalidOperationException`) when `GatOsPaths.ModDir` is unset, so the test skip-gate works.

The full `GATOS_IT=1` suite was verified green on the Windows 11 game machine against **guest v3**
(278/278, 0 skipped, 2026-06-13 — TCG fallback: WHPX needs the off-by-default
`HypervisorPlatform` Windows feature; guest boot ≈ 7 s under TCG).

---

## Welds + `always_render_iva` + parts listing + `thug_life` (ex-`unscience`): Code DONE; in-game pass pending

Four additions ported from the sibling `unscience` mod, exposed **only** on the gatOS surfaces (9p
`/sim` debug + HTTP `/v1` + MQTT — **no ImGui**). KSA-coupled code is confined to
`gatOS.GameMod/Game/Ksa/` per the G2 rule; the snapshot/command plumbing is game-free. KSA bindings
verified against decomp `2026.6.9.4750` (anchors `2026-06-28`).

**Parts listing** — `vessels/by-id/<id>/parts/<n>/`, gated by a new `telemetry_vessel_parts` config key
(default true). Leaves: `instance_id` (uint — the **stable** weld anchor handle), `id`, `display_name`,
`template`, `is_root`, `subpart_count`, `position`. 2026-07-16: each part additionally nests its subparts
at `parts/<n>/subparts/<m>/{instance_id,id,display_name,template,position}` (a subpart is a full `Part`
with its own `InstanceId`; either level's `instance_id` is a valid weld anchor).
`Game/Ksa/Readers/PartsReader.cs` builds `PartSnapshot[]` (+ nested `SubpartSnapshot[]`) from
`Vehicle.Parts.Parts`/`Part.SubParts`, cached per vehicle in a `ConditionalWeakTable<Vehicle,…>` and
rebuilt on a `Vehicle.Parts.Count` change or every 10 s (sim seconds; subpart counts are template-fixed,
so the top-level count stays the right invalidation signal). The sampler projects it per vessel when the
gate is on. `parts/json` (same date) serves the whole part/subpart tree as one JSON document (the
`SimJson` snake_case projection of the `PartSnapshot` list) — the one-`cat` discovery path; the
serialization is memoized on the list **reference** in `SimFsTree.PartsJsonFile` (the sampler passes the
reader's cached list through unchanged, so re-serialization happens only on an actual rebuild), and the
per-publish `SnapshotTextFile` memo handles concurrent readers on top.

**`always_render_iva`** — global render cheat at `/sim/debug/always_render_iva` (`debug.always_render_iva`,
Frame, vessel-agnostic) that forces interior (IVA) part meshes to render outside the IVA camera by flipping
`PartModelModule.Template.Internal=false`. `Game/Ksa/Render/IvaForceRender.cs` installs two Harmony patches
on its **own** `Harmony("gatos.iva")` instance **only while enabled** (a `PartModel(PartModelModule.Template)`
ctor postfix + an editor-only `PartModel.AddInstance` postfix) and bulk-flips/tracks the internal templates
over `PartModel.Instances`; disable restores the tracked templates and unpatches. `Actuators/IvaActuator.cs`
is the thin actuator.

**Welds** — rigidly attach a source vessel to a target vessel's part **or subpart** (a game hack;
2026-07-16: `WeldManager.FindPart` resolves `<part_iid>` over `Vehicle.Parts.Parts` and each part's
`Part.SubParts` — an animated subpart anchor tracks its live pose, since the weld math's
`PositionVehicleAsmb`/`Asmb2VehicleAsmb` compose through `PartParent`).
`Game/Ksa/Welds/{WeldEntry,WeldEngine,WeldManager}.cs`: `WeldManager` is the game-thread registry +
per-frame driver, `WeldEngine` the stateless teleport math ported verbatim from `unscience` (orientation
stored as an authoritative `doubleQuat`, Euler display-only; `weld_here` capture is the inverse transform;
the orbit is stamped with `Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime`). Per-source
controls under `/sim/debug/vessels/<id>/`: `weld` (explicit pose), `weld_here` (capture the current
relative pose), `unweld`; registry view + ops under `/sim/debug/welds/`: `clear`, `count`, and
`<source>/{target,part,offset,rotation,lock_rotation,enabled}`. Action keys `debug.weld_{create,here,
remove,enable,clear}` (all Frame). The driver runs in `OnAfterUi` (`Mod.DriveWelds`) after
`JobSystems.VehicleSolver.Wait()` — the **third game-thread mutation site**, beside the Frame and Solver
drains; self-gated to a no-op when empty, so **no** Harmony patch and zero cost when unused.

**`thug_life`** — gatOS's **first custom GPU rendering**: anchors a flat, world-space textured quad (the
"thug life" sunglasses meme) to a part on a vehicle, tracked each frame, exposed **only** via
`/sim/debug/thug_life/` (add/clear + per-entry `position`/`rotation`/`size`/`visible`/`remove`/`spec`/
`vessel`/`part`, plus `count`). `Game/Ksa/ThugLife/`: `ThugLifeTexturePattern` (a static 26×5 char grid for
the sunglasses, no KSA API) → `ThugLifeTextureFactory` (builds an **`R8G8B8A8UNorm`** texture + sampler via
`SimpleVkTexture`/`VkUtils.UploadBufferToImage`/`DeviceEx.CreateSampler`); `ThugLifeQuadRenderer` (`unsafe`)
holds the GPU pipeline/descriptor/buffers (`BuildPipeline` reuses KSA's `"UnlitMeshVert"`/`"UnlitMeshFrag"`
shaders, the `Program.OffScreenPass` render pass + **reverse-Z** depth) and does the per-frame anchor math
(`TryComputeModelEgo`: camera `MVP.viewProjection`, `Vehicle.GetMatrixAsmb2Ego`/`Asmb2Ego`,
`Part.PositionEgo`/`Asmb2Ego`); `ThugLifeRenderPatches` installs a dynamic `Harmony("gatos.thug_life")`
**postfix on `SuperMeshRenderSystem.RenderMainPass`** (the one injection point for a world-space draw);
`ThugLifeEntry` is the data model; `ThugLifeManager` is the registry + GPU lifecycle + dynamic-patch +
`RecordDraws`/`Snapshot`/`Update`. Key as-built decisions: the render postfix + Vulkan resources install
**lazily on the first entry** and tear down with the last / at unload (off by default = **zero patches and
zero GPU**, the welds/IVA discipline); the anchor is a **top-level part by `instance_id`** (reuses the welds
`parts/` listing) **or `0` = the vehicle body frame** (no subparts in v1); KSA runs `RenderMainPass` on the
**main thread**, so the draw, the command drain, and entry edits are all one thread (no cross-thread access);
the manager publishes an immutable `ThugLifeEntry[]` and **self-disables on any GPU fault**. Entries are
**runtime-only** (never persisted). A new game-thread work site `UpdateThugLife()` (in `OnBeforeUi`)
revalidates/re-resolves each entry per frame; `_thugLife?.Clear()` in `TeardownGameCheats` tears it down.

**Wiring:** game-free `gatOS.SimFs/Commands/LineControlFile.cs` (a new whole-line-parsed control archetype,
backs `weld`/`weld_here` and `thug_life/add`); `SimSnapshot` gains `PartSnapshot`/`WeldSnapshot`/
`ThugLifeSnapshot` records (`VesselSnapshot.Parts`, `SimSnapshot.{Welds,AlwaysRenderIva,ThugLife}`);
`Formats` gains `UInt`/`WeldSpec`/`ThugLifeSpec`; `SimFsTree` gains `ThugLifeDir`/`ThugLifeEntryDir`/
`ParseThugLifeAdd`/`ThugLife(id)` under `DebugDir`; `TelemetrySettings` gains the `VesselParts` gate.
`KsaCatalog` (now an instance dispatcher) gains `WeldManager`/`ThugLifeManager` ctor params + the new actions
(IVA + `weld_clear` + the 7 `thug_life` actions handled vessel-agnostically — `thug_life_add` resolves the
anchor vehicle via `ResolveVehicle` from the command `Token`, the entry id travels in `ordinal`).
`Mod.Game.cs` lazily creates `_weldManager`/`_thugLife` (game thread), drives welds via `DriveWelds(dt)` from
`OnAfterUi` and thug_life via `UpdateThugLife()` from `OnBeforeUi`, tears all cheats down via
`TeardownGameCheats`, and adds a "Vessel parts" telemetry menu toggle; `TelemetrySampler` projects
`ThugLife = _thugLife.Snapshot()`. `gatOS.GameMod.csproj` gained `Brutal.Concurrency` (for
`JobSystems.VehicleSolver.Wait()`) and the `thug_life` render refs `Brutal.Core.Memory`/`Brutal.Vulkan`/
`Brutal.Vulkan.Abstractions`/`Brutal.Vulkan.Vma`/`Planet.Render.Core` (all `<Private>false</Private>`,
KSA-guarded), and set `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.

Full catalog: **`SPEC_9P_FILESYSTEM.md`** §3.4.17 (parts) + §3.7 (`debug/welds/**`, `always_render_iva`,
`debug/thug_life/**`); anchors mirrored in `docs/KSA_INTEGRATION_MATRIX.md` and `scope/` (the `thug_life`
render set is flagged the **deepest / highest-churn** KSA coupling). **Pending: the in-game pass**
(checklist in `docs/VALIDATION.md`).

---

## First-class per-vessel nodes: `scale` + `always_render` (ex-`unscience`): Code DONE; in-game pass pending

Two more `unscience` ports (garrys-torch scaling / i-feel-seen render-distance override), landed
2026-07-02 as **first-class vessel nodes** under `vessels/by-id/<id>/` — deliberately **outside**
`/sim/debug` (the first per-vessel controls migrated out of the debug namespace) and **exempt from the
active-vessel authority gate** (`KsaCatalog.AnyVesselActions`, a `HashSet` — each is a deliberate by-id
operation on an arbitrary vessel). Gated only by the `control_enabled` master; both Frame-phase.

**`scale`** (`SCALING_FEATURE_PLAN.md`; `Game/Ksa/Actuators/ScaleActuator.cs`): write any finite
value > 0 (`EINVAL` otherwise, via the game-free `gatOS.SimFs/Commands/ScaleRules`) to **one-shot**
uniformly rescale the model — recursive `Part.Scale = (f,f,f)` (public `double3` setter) +
the KittenEva avatar via reflected `Core.Scale = f*0.01f`. No driver, no patch, no clamp; KSA reverts
it on vessel rebuild. Read-back = a representative `Part.Scale.X` (best-effort, `1.0` fallback).

**`always_render`** (`Game/Ksa/Render/VesselForceRender.cs`): a `0`/`1` flag that bypasses KSA's
sub-pixel cull (`Camera.GetObjectDiameterPixelsAsDouble < 1.0` normally hides the vehicle) so the
vessel renders at **any** distance. Two Harmony prefixes on a dynamic `Harmony("gatos.always_render")`
instance — `Vehicle.GetWorldMatrix(Camera)` + `Vehicle.UpdateRenderData(Viewport,int)`, each
reproducing the stock body minus the cull — installed **only while ≥ 1 vessel is marked**, removed on
the last unmark/despawn/unload (the welds/IVA/thug_life discipline; a marked-empty session carries
zero patches). The id-keyed registry is game-thread-mutated and published as a volatile immutable set
for the prefixes; `VesselForceRender.Prune` (riding the sampler's vehicle enumeration) drops despawned
ids. Marks survive scene rebuilds (id-keyed); KittenEva's `UpdateRenderData` override is **not**
affected (virtual-method limitation, as in unscience). Read-back is a pure registry lookup.

**Wiring:** `VesselSnapshot.{Scale,AlwaysRender}` (init-only, sampled in `VesselReader.SampleCore` —
always on, not detail-gated); `SimFsTree` `NumberControl`/`FlagControl` nodes beside `com`;
`Mod.TeardownGameCheats` calls `VesselForceRender.Teardown()`. Tests:
`gatOS.SimFs.Tests/Commands/{VesselScaleTests,VesselAlwaysRenderTests}.cs`. Catalog:
`SPEC_9P_FILESYSTEM.md` §3.4.1 + §5.1; anchors mirrored in `docs/KSA_INTEGRATION_MATRIX.md`
(per-vessel nodes section) and `scope/` (`ksa-write-surface.md#per-vessel-nodes`,
`ksa-runtime-coupling.md#always-render-patches`). **Pending: the in-game pass** (checklist in
`docs/VALIDATION.md`).

---

## One-shot vessel impulse: `debug/vessels/<id>/impulse`: Code DONE; in-game pass pending

An impulsive-kick cheat, landed 2026-07-04: write `x y z [cci|body] [ns|dv]` to instantly change a
vessel's velocity — no propellant, no pointing, no autopilot. Defaults: the vector is an **impulse in
newton-seconds** (Δv = J ÷ live `Vehicle.TotalMass`, the same math as KSA's own `Vehicle.Split`
separation impulse) in the **parent-CCI frame**; the `body` keyword reads it in the vessel body frame
(+X = nose, rotated through `GetBody2Cci()` at application), and `dv` applies it directly as Δv m/s.
Keywords may follow the numbers in any order; zero vectors succeed as a no-op.

**Game-free half:** `gatOS.SimFs/Commands/ImpulseRules.cs` (the keyword sets + the arity/finite/keyword
validation both the 9p parse and the actuator share — the `ScaleRules` pattern, so the EINVAL boundary
is unit-testable); `SimFsTree.ParseImpulse` (a `LineControlFile` beside `teleport` in the debug vessel
dir) building `SimCommand(id, "debug.impulse", NoOrdinal, 0) { Values=[x,y,z], Token=frame, Aux=unit }`
— Frame-phase (not in `SolverActions`, the teleport precedent). **Game half:**
`DebugActuator.Impulse` — the *velocity-bump variant of the teleport pattern*: rotate to CCI if
`body`, divide by mass unless `dv`, then `Orbit.CreateFromStateCci(parent, now, GetPositionCci(),
GetVelocityCci() + Δv, default)` → `Vehicle.Teleport` → `UpdatePerFrameData()`, so it works on-rails
and in the physics bubble alike (KSA has no persistent external-force accumulator — forces are rebuilt
every solver substep, so an instantaneous velocity change *is* the correct primitive). Errnos:
`EINVAL` (arity/keyword/non-finite/non-finite result), `EBUSY` (no parent body; mass unavailable for
an N·s kick), `ENOENT` (vessel gone). HTTP/MQTT get the action + the `/v1/fs`–`gatos/sim` field
mirror structurally (zero transport code). Tests:
`gatOS.SimFs.Tests/Commands/VesselImpulseTests.cs` + the `SimFsTreeTests` enumeration list. Catalog:
`SPEC_9P_FILESYSTEM.md` §3.7/§5.1/§6; anchors mirrored in `docs/KSA_INTEGRATION_MATRIX.md` (debug
table) and `scope/ksa-write-surface.md#debug`. **Pending: the in-game pass** (checklist in
`docs/VALIDATION.md`).

---

## Custom audio playback: `/sim/audio` (GATOS_CUSTOM_AUDIO_PLAN P1–P3): Code DONE; in-game pass pending

Userland audio through the game's speakers, landed 2026-07-02 — upload real mp3/ogg/wav/flac bytes
with plain file writes and play them through KSA's own FMOD mixer. `cat alarm.mp3 >
/sim/audio/file/alarm.mp3; echo 'alarm.mp3' > /sim/audio/play`. This supersedes (and drops) the
purrTTY terminal-escape audio plan — no terminal protocol, no bells.

**Game-free half** (`gatOS.SimFs/Audio/`): `AudioStore` — in-memory clip table (name → immutable
committed `byte[]` + version) under one lock, with per-clip (`EFBIG`) / total-bytes / clip-count
(`ENOSPC`) caps enforced **per write** so the failing `write(2)` carries the errno; ready-on-commit
semantics (a clip is invisible to `play` until its upload clunks / HTTP `complete=1`); the
volatile-swapped channel-status snapshot the game thread publishes; a bounded (64) `audio.finished`
event queue the sampler drains; and the session-less HTTP chunked-upload state. `AudioDirectory` /
`AudioClipFile` — the writable `file/` dir (9p `Tlcreate` + chunked `Twrite`s + clunk-commit,
`O_TRUNC` truncate-replace with version bump, `O_APPEND` via ready-bytes seeding, `Tunlinkat` evict,
binary read-back; `IsStreaming=true` opts clips out of the scalar field mirrors). `AudioCommands` —
the `play`/`set`/`stop` grammars parsed fully in SimFs (EINVAL at the write; unit-testable game-free)
into `audio.*` `SimCommand`s: play carries the fixed 7-slot values array + the clip name in `Token` +
the optional `id=` in the **new `SimCommand.Aux`** slot (the one optional aux string the plan called
for); set carries (key,value) pairs; stop a bare target token. New errnos `EPERM`/`EFBIG` added to
`LinuxErrno`.

**Game half** (`Game/Ksa/Actuators/AudioActuator.cs`, 3 `[KsaAnchor]`s; new condition-guarded
`Brutal.Fmod.dll` reference): drives `GameAudio.System` (public static) with the game's own idioms —
`TryCreateSound(bytes, OpenMemory|_2d|CreateSample≤1MiB / CreateCompressedSample>1MiB, exInfo{Length})`
(FMOD copies + sniffs the container; cached per clip-version), `TryPlaySound(paused:true)` → configure
(position/loop/loop-points/volume/pan/pitch) → unpause, channel-group routing so the in-game
Sfx/Music/UI sliders govern playback. Channel table keyed by caller `id=` or auto `#N` (id reuse
replaces; `audio_max_channels` → `EBUSY`). Per-frame tick (`Mod.DriveAudio`, `OnBeforeUi` after the
drain — the **fifth game-thread work site**, self-gating when empty, `_audioDead` session latch):
prune finished channels (a recycled FMOD handle answering non-Ok *is* the completion signal), enforce
`end=` by position (~16 ms, correct under `pitch=`), release evicted sounds only once unreferenced
(never mid-playback), publish status (pos quantized 100 ms), emit `audio.finished` (`ended`/`stopped`/
`replaced`) — folded into `SimSnapshot.NewEvents` by the sampler (honors the `telemetry_events` gate).
Deliberate: playback ignores the >10× warp SFX mute. `KsaCatalog` routes `audio.*` vessel-agnostically
before vehicle resolution (`EOPNOTSUPP` when disabled); teardown via `TeardownGameCheats` →
`Shutdown()` (stop all, release all, clear store).

**Transports:** play/set/stop/status/info ride the shared machinery (field mirror + `/v1/command` +
`gatos/command` — the `aux` field added to both JSON command parsers and both OpenAPI docs). The
**one deliberate parity exception**: dedicated binary upload routes `PUT/POST
/v1/audio/file/<name>[?offset&complete]` (chunked, append-by-position, commit mirrors clunk) +
`DELETE` + `GET /v1/audio/files` (the field-write path is UTF-8/1 MiB-capped); MQTT gets no upload.
Config `[audio]`: `audio_enabled` (off ⇒ the surface is absent everywhere) + the four caps, clamped
in `Normalize()`. Tests: `gatOS.SimFs.Tests/Audio/{AudioStoreTests,AudioCommandsTests,AudioTreeTests}`
(100 tests: caps/versioning/ready, chunked offsets, grammar → exact commands, errnos on the 9p wire,
status/info rendering, field-mirror exclusion, HTTP chunk semantics). Catalog: `SPEC_9P_FILESYSTEM.md`
§3.9 + §5.1 + §7; `scope/ksa-write-surface.md#audio`; `docs/KSA_INTEGRATION_MATRIX.md` (audio
playback). **Pending: the in-game pass** (checklist in `docs/VALIDATION.md`) — the FMOD half cannot
be exercised headlessly.

---

## IVA cabin physics: `/sim/debug/iva` (plans/IVA_MOVEMENTS.md): Code DONE; in-game pass pending

Free-floating objects inside a vessel's interior, landed 2026-07-24. In IVA mode, loose props stop
being glued to the hull: weightless and drifting while coasting, slammed aft when the engines light,
flung around by RCS rotation, and colliding with the **actual interior surfaces**. `echo 1 >
/sim/debug/iva/enabled; echo "Gemini7 4 Sardine" > /sim/debug/iva/adopt_all`.

**Off by default behind one master switch**, `/sim/debug/iva/enabled`, and off means nothing exists:
no physics `Simulation`, no `BufferPool`, no interior collision mesh, no per-frame work (the driver is
one branch on `IvaPhysicsManager.IsIdle`), and no Bepu type is even loaded (the Bepu fields live on
`CabinSim`, which is not constructed until the first adopt). Writing `0` releases every object at its
exact rest pose and disposes every simulation; so does mod unload.

**Why a gatOS-owned physics world** (the decisive research, plan §1.3/§3): KSA models each vehicle as
**one** dynamic Bepu body whose shape is a handful of coarse convex primitives approximating the
*outside* of the whole vehicle — the Gemini-class pod's entire collision representation is a 2 m
cylinder plus a 0.89 m sphere, and the IVA interior part declares no `<Collider>` at all — while
`NarrowPhaseCallbacks.AllowContactGeneration` permits dynamic-vs-dynamic pairs unconditionally.
Anything placed where a crew member sits is therefore deep inside the vehicle's own collider:
penetration recovery ejects it through the hull **and** the contact constraint shoves the real
spacecraft. Suppressing that pair would mean patching a `readonly struct` callback inlined into Bepu's
hot path. Worse, `VehicleUpdateTask` only steps that simulation when `ConstraintSim.IsAnyConstrained` —
never for a coasting vessel, the exact case this feature is for — and the solve runs on
`VehicleSolvers` workers, so mutating it would race the game (threading rule 1). **This is also why
floating props are not separate KSA vessels**: each would be one more dynamic body in that same
simulation, with the same ejection and shoving, plus orbits, map markers, solver tasks and save-file
entries. So gatOS creates its own `BepuPhysics.Simulation` (against KSA's own embedded Bepu 2.5,
already in-process) with its own `BufferPool`/`Shapes` — never `ConstraintSim.GlobalShapes` — in the
**vessel assembly frame**, where the interior is static geometry that never moves, poses come out in
exactly the coordinates `Part.PositionParentAsmb` wants, and coordinates stay within metres of the
origin so float32 has precision to spare. It cannot perturb the vessel, cannot corrupt a save, cannot
race the solver.

**Game-free half** (`gatOS.SimFs/Iva/CabinPhysics.cs` + the `debug/iva` tree): the entire physics model
is one formula — `a = -a_p - alpha x r_b - 2 omega x v - omega x (omega x r_b)` — over plain vectors,
so it lives outside the KSA compile gate and is unit-tested on a bare host (`CabinPhysicsTests`: pad =
1 g down, coast = weightless, burn = aft, spin = centrifugal + Coriolis signs, superposition). Gravity
never appears: it is already absent from **proper** acceleration by construction, which is what lets one
formula cover pad, coast, burn, spin and touchdown with no `Situation` switch. `IvaSnapshot` /
`IvaObjectSnapshot` / `IvaInteriorSnapshot` / `IvaStatsSnapshot` + `Formats.Iva*` back the registry view.

**Game half** (`Game/Ksa/Iva/`, 9 `[KsaAnchor]`s; new condition-guarded `BepuPhysics`/`BepuUtilities`
references): `CabinSim`/`CabinCallbacks` (the Bepu wrapper — fixed substeps, the forcing field applied
in `IntegrateVelocity`, our own contact material, speed clamping, and impact detection by per-substep
speed change rather than narrow-phase bookkeeping); `InteriorGeometry` (the collision mesh, built from
the vessel's own interior meshes — `MeshReference.PositionCompare`, a de-indexed `double3[]` triangle
soup KSA retains forever for its picking raycasts, classified by `PartModelModule.Template.Internal`,
which is *defined* as "renders only through the IVA camera" and so is an exact art-driven classifier for
touchable interior surfaces; both windings emitted by default, with a bounding-box room fallback);
`FloatingObject` (the driven SubPart + the parent-frame transform math + exact rest-pose restore);
`IvaPhysicsManager` (registry, lifecycle, `adopt`/`adopt_all`/`release`/`clear`/`nudge`, the leash, and
the per-frame driver). **No Harmony patch.** `Mod.DriveIvaPhysics` runs in `OnAfterUi` right after
`DriveWelds` — the **sixth game-thread work site** — and like it calls `JobSystems.VehicleSolver.Wait()`
first so the accelerometer/rates/CoM readings are settled; `Timestep` runs with no `IThreadDispatcher`,
so every Bepu callback is on that same thread.

**Rendering is free**: an object drives a real shipped IVA prop **SubPart**'s
`PositionParentAsmb`/`Asmb2ParentAsmb`, which `PartModelModule.UpdateRenderData` re-reads every frame —
KSA's own idiom for a runtime-animated part transform (`KeyframeAnimationModule`, `SolarTracker`) — so
lighting, PBR, ray tracing and IVA visibility gating all follow with no renderer code. **SubParts only,
and binding**: `Part.GetReferenceWithChildren` serializes a `Transform` for top-level parts but not for
SubParts, so a displaced object physically cannot leak into a save file; adopting a top-level part is
refused by design. The rest pose is captured into gatOS's own fields, not KSA's `*Safe` pair.

**Rails**: objects park (velocities zeroed, poses held) under time warp, in the vehicle editor
(`Program.Editor != null` disables `Part` transform caching) and outside the IVA camera unless
`run_outside_iva`; speed is clamped and an escapee is leashed back to the cabin centroid
(`iva.escape`); adopting anything larger than `iva_max_object_size` is refused so structure cannot be
cut loose; a staged-away part auto-releases (`iva.release`); a per-vessel fault drops that cabin and a
driver fault releases everything and latches the feature off for the session.

**Transports:** the whole surface rides the shared machinery (field mirror + `/v1/command` +
`gatos/command`) by construction. Config `[iva]`: the `iva_physics_enabled` boot seed plus 11 tuning
knobs, clamped in `Normalize()` (which gained a `double` overload). Status window: an "IVA physics"
`PerfStat` row, shown only once the feature has actually run. Build plumbing: `Bepu*.dll` added to the
sibling `ksa-game-assemblies` `copy-ksa.ts` glob, and a `VerifyBepuReference` MSBuild target that fails
a stale `KSAFolder` with the one-line fix instead of 200 type errors. Tests:
`gatOS.SimFs.Tests/Iva/CabinPhysicsTests` (9) + `gatOS.SimFs.Tests/Commands/IvaPhysicsTreeTests` (22).
Catalog: `SPEC_9P_FILESYSTEM.md` §3.7 (**iva**) + §5.1 + §2.5; `scope/ksa-{read,write,runtime,assets}-*`;
`docs/KSA_INTEGRATION_MATRIX.md` (IVA cabin physics). **Pending: the in-game pass** (21-item checklist
in `docs/VALIDATION.md`, including the plan's Q1-Q3/Q5 open questions) — none of it can be exercised
headlessly.

---

## FX editors: `/sim/debug/{engineplume,plumetrail,clouds,terrain}` (issue #2, plans/FX_EDITORS_PLAN.md): Code DONE; in-game pass pending

The game's four built-in imgui render editors — "Volumetric Exhausts", "Plume Trails", "Clouds",
"Terrain Editor" — exposed as filesystems, landed 2026-08-01 in two phases (A: `81f8a36` game-free,
B: `3904f9e` GameMod). One writable leaf per knob, so a shell loop or a TUI can animate render state at
10–60 Hz (the AGENTS.md §7 "light show" bar). Gated by the existing `[control] debug_namespace` — **no
new config key**. The first feature built end-to-end under the `AGENTS.md` schema-change constitution.

**Phase A — game-free** (`gatOS.SimFs`, `81f8a36`): the *declarative field-catalog* pattern.
`Fx/FxCatalog.cs` holds four tables (`EnginePlume` 28 rows, `PlumeTrail` 11, `Clouds` 20, `Terrain` 11)
of `FxFieldSpec(Key, Kind, Min, Max, Unit, Doc)` — `FxKind` fixes the arity and the control archetype
(`Number`→`ControlFile.Ranged` (new), `Flag`→`ControlFile.Flag`, `Color3`/`Color4`→ranged
`VectorControlFile` (new overload)) — plus `Match`/`Matches`/`IsValid` (a `*` key segment matches a
non-negative integer index) and the nine action-key consts. `Snapshots`: `FxEntitySnapshot(Id, Fields)`
keyed by **concrete** field paths + `FxEditorsSnapshot{PlumeTemplates, Trail, CloudBodies, TerrainBodies,
TerrainGlobal}` on `SimSnapshot.FxEditors` (null ⇒ not sampled). `SimFsTree` builds all four subtrees from
**one** generic builder (`FxEntityNodes`/`FxChildren`/`FxLeaf`): the entity's live field keys × the family
table define which leaves and indexed subdirs exist, nested per path segment, in catalog order with
numeric index ordering (`layers/2` before `layers/10`), cached by (path, field count). Each entity also
gets a `json` document (`Formats.FxFields`, memoized on the field-dictionary reference) and a `reset`
trigger; each family a `help` readme; `plumetrail` additionally a `clear` trigger and `terrain` the
family-global `wireframe` leaf. Tests: `FxCatalogTests` + `Commands/FxEditorsTreeTests` (read-back,
write→exact `SimCommand`, EINVAL boundaries, indexed cloud paths) and the tree-crawl guard extended with
29 new paths.

**Phase B — GameMod** (`Game/Ksa/Fx/`, `3904f9e`, 24 `[KsaAnchor]`s, verified against
**`2026.7.10.5056`**): `FxReflect` — the six lazily-resolved, cached, null-tolerant private handles
(`Program._volumetricTrailRenderer`, `_planetTransparenciesRenderer`→`GetCloudRenderer()`,
`VolumetricExhaustTemplate.References`, `CloudRenderer._renderer`/`_cloudShadowsRenderer`/
`_worleyNoise3dTarget`, `PlanetRenderer._renderUboMap`/`_meshUboMap`) each behind its own `KsaHealth`
latch key (`fx.trail_renderer`, `fx.plume_templates`, `fx.cloud_renderer`, `fx.cloud_apply`,
`fx.terrain_renderer`, `fx.terrain_ubo`). `PlumeActuator` writes the shared `VolumetricExhaustTemplate`
(colours **construct-new + `OnDataLoad`** — the `Value` setter is protected) then runs the editor's own
propagation loop over every live nozzle. `TrailActuator` writes public renderer fields (no apply needed)
and `Clear` calls the public **instance** `Program.ClearPlumeTrails()`. `CloudActuator` writes the public
`CloudsReference` graph then re-derives the affected layer's render data + repopulates the shadow atlas;
a missing apply handle degrades the **apply only and still returns `Ok`**. `TerrainActuator` does the
paired write — reference object **and** the `PlanetUbo`/`MeshUbo` structs at the body's slot, plus the
frames-in-flight mirror copy (no public repopulate exists; verified). `FxEditorReader` samples all four
families through the actuators' own read halves, memoized: rebuilt only on an FX write
(`Invalidate`) or after 2 s (catching in-game imgui edits), else republished by reference.
`TelemetrySampler` gates the whole sample on `[control] debug_namespace` (new ctor flag from `Mod`);
`KsaCatalog` routes the four families vessel-agnostically before vehicle resolution and re-validates
every payload against the catalog (HTTP/MQTT bypass the 9p parse). `FxPristine` records a field's
pre-gatOS value on first write and replays it through the same write path on `reset`;
`Mod.TeardownGameCheats` runs `FxPristine.RestoreAll()` first, then `FxEditorReader.Reset()`. **No
Harmony patch, no per-frame driver, no GPU resources.**

**Deferred (documented as such):** plume startup/shutdown transient curves + LUT re-bake, test grid and
wireframe debug; the trail simulation/LOD/wind tier and the two rebuild-forcing toggles; cloud
`NoiseScale` (would force `RecreateLayerPipelines`), shape/density splines, texture slots; terrain
per-biome materials, procedural modifiers, ground clutter/ecotypes, BVH debug and exporters.

**Plan-vs-code deviations** (the code won, and the docs record them for the next break-check):
`ClearPlumeTrails` and `PlanetRenderer.Wireframe` are **instance**, not static; `CloudTypes` hangs off
`layer.VolumetricCloud`; `PlanetUbo.TanMeanSlopeRoughnessRadians` stores plain radians despite the name.
Catalog: `SPEC_9P_FILESYSTEM.md` §3.7 + §5.1; `docs/KSA_INTEGRATION_MATRIX.md` (FX editors);
`scope/ksa-{write,read}-surface.md#fx-editors`, `scope/ksa-runtime-coupling.md#fx-accessors`.
**Pending: the in-game pass** (18-item checklist in `docs/VALIDATION.md`) — the reflected handles, the
apply paths and the terrain UBO write cannot be exercised headlessly.

## Timed command scheduler: `/sim/ctl/timed_batch` + `ctl/schedules/` (plans/SCHEDULER_ASBUILT.md): Code DONE; in-game pass pending

The generic host-side timed-command scheduler — "run this script of `/sim` writes against a timeline" —
landed 2026-08 in two commits (`e96edda` the game-free S.0–S.4, `3be1dd9` the S.5 game-thread wiring,
plus `fca17ce` the cap-pressure eviction fix). It is **100 % game-free and contributes zero
`[KsaAnchor]`s**: it schedules gatOS's own `SimCommand`s and never learns what one *does*, so
`KsaCatalog` routes the whole `schedule.*` family straight to `ScheduleStore.Execute` rather than
re-validating anything game-side. Gated by the new `[schedule]` config section (`schedule_enabled`,
`schedule_max_live` 16, `schedule_max_entries` 8192, `schedule_max_bytes` 1 MiB,
`schedule_default_clock` `"render"`).

**The one timeline primitive** is `PlaybackClock` (`ClockBase` = `Render`\|`Wall`\|`Ut`): position,
never-shrinking duration, rate clamped `[0,100]` (**`0` is a legal frozen state**), loop, pause,
`Scrub`, plus `LoopCount`/`ScrubGeneration` edge counters consumers diff. Its doubles are published as
bit-cast `long`s through `Volatile`, so transport threads read them torn-free without a lock — which is
what lets `/sim/ctl/schedules/<id>/t` be a live line that advances every rendered frame rather than at
the telemetry cadence. `Schedule`/`ScheduleEntry` are the immutable committed script (entries **stably**
sorted, so authored order survives inside one deadline — a group of `0`-offset lines therefore behaves
exactly like a `ctl/batch`), `Scheduler` is one live player's cursor, and `ScheduleStore` is the
registry + the game-free `schedule.*` executor + the bounded event queue (the `AudioStore` shape).
`CommandQueue` gained `IPostObserver` + `Post(command, observer, token)` — fire-and-forget, no TCS,
honouring `ControlEnabled` and routing by `command.Phase`, so **phase mixing across posts is free**
(the deliberate relaxation of `BatchFile`'s one-phase rule).

**Three semantics that are derived, not declared, and are the reason the feature is safe under load.**
*Coalescing catch-up:* among the entries due in one tick every trigger fires in order, but only the
**last per path** of the non-triggers does, cross-path order preserved — bounding a hitch's burst by
*distinct leaves* instead of entries (a test drives 3 600 authored entries down to 3 emitted commands),
with everything dropped counted at `<id>/dropped` and reported as a `schedule.dropped` event throttled
to ≤ 1/player/second. *Scrub fires nothing* — a seek re-seats the cursor by binary search; it is
navigation, and scrubbing backwards replays. *Loop drains the tail first* and keeps the remainder, so a
loop boundary is indistinguishable from any other busy tick and the timeline does not drift.
Shared-clock `@group`s hold the *same* `PlaybackClock` instance, so `pause`/`scrub`/`rate`/`loop` on any
member moves them all, and a late joiner starts at the group's current position.

**Completed players persist — until the cap is reached.** A `done`/`failed` player stays listed with its
final `state`/`dropped`/`last_error` so a script can start a take and come back to read the outcome; but
that would let a long session of one-shots wedge the registry on its own history, so `Activate` evicts
**finished** players **oldest-first, under cap pressure only**, emitting `schedule.evicted` per reclaimed
slot. `IsFinished` is the one "can never fire again" test — `done`, or `failed` **and** exhausted **and**
not looping **and** past duration — because `failed` is *not* terminal (the first failing entry records
the cause and the schedule deliberately keeps running). Eviction is eager and on the game thread on
purpose: `ReserveId` runs on a transport thread and the cap counts *reserved* ids while an eviction pass
can only see *activated* runners, so reclaiming lazily at commit time could not work — the first commit
into a full registry would still EINVAL and only a retry a frame later would succeed.

**S.5 — the game-thread wiring** (`Mod.TickSchedules`, `Game/Mod.Game.cs`): activate → advance every
distinct clock once → tick → `Post` each due command, run in `DrivePerFrame` **immediately before** the
command drain so a command that falls due on this frame executes on this frame. The three clock bases
are sourced here and only here: `render` = the frame's `dtPlayer` (which KSA clamps, so it lags a hitch
and never catches up), `wall` = a host `Stopwatch` **parked while the registry is empty** so an idle gap
is never banked, `ut` = the sim-time delta with a backwards step clamped to 0. Self-gates to two integer
compares while nothing is live; `Scheduler.Tick` allocates nothing on a 0-or-1-entry tick; a tick throw
sets `_schedulesDead` for the session. Events drain through the sampler beside audio's and IVA's;
teardown rides `Mod.TeardownGameCheats` (`ScheduleStore.Clear()`).

**Deviations from the brief** (the code won): `ICommandSink.SubmitScheduleAsync` was **not** added — a
schedule is host-side state, not a game mutation, and it would have forced ten `ICommandSink`
implementors to carry a member they cannot implement, so `TimedBatchFile` holds the store directly (the
`AudioStore` precedent) and re-asserts `[control] enabled` itself (EACCES) because it does not go
through `SubmitAsync`. `ScheduleStore.Prune()` was not implemented as public surface; what landed is the
private cap-pressure eviction above. `ReserveId` is a separate public step from `Submit`, so a rejected
commit never burns the name. Tests: **107** across `PlaybackClockTests`, `SchedulerTests`,
`TimedBatchFileTests`, `ScheduleTreeTests` and `ScheduleEvictionTests`, plus 5 `CommandQueue.Post` tests
and the tree-crawl guard extended with every new path. Catalog: `SPEC_9P_FILESYSTEM.md`;
`scope/non-ksa-surface.md#scheduler` + `scope/ksa-runtime-coupling.md#schedule-tick`.
**Pending: the in-game pass** (16-item checklist in `docs/VALIDATION.md`) — real frame pacing, real
hitches and real warp are the only things that can distinguish the three clock bases.

---

## Programmable camera: `/sim/camera` (plans/CAMERA_ASBUILT.md, CAMERA_CONTROLS_PLAN C0–C5): Code DONE; in-game pass pending

gatOS as the sole writer of the main viewport's camera — ownership take/release, six reference frames,
aim-with-offset, geodetic placement, JSON shot tracks, an interpolated `time` channel and the map's own
zoom — landed 2026-08-06 in five commits (`0acf836` the F2-proof hook + `[camera]`/`[schedule]` config,
`d9f4468` the math primitives, `5fe0ef0` the `/sim` surface, `c860f7d` the C1/C2 director, `80dcf77`
the C3 track evaluator, `1467864` the wiring + the `time` channel + `map/scope`). The first live pass
falsified the after-render driver; the 2026-08-09 correction uses a main-viewport-identity
`Viewport.OnFrame` prefix/postfix for same-frame apply/final read-back, plus anchor-relative smoothing
and exact aim. Gated by `[camera] camera_enabled` (+ `camera_max_tracks` 32, `camera_max_track_bytes` 1 MiB,
`camera_max_total_bytes` 8 MiB, `camera_max_keys` 4096, `camera_fov_min`/`max` 1/179,
`camera_release_blend_s` 0.6, `camera_allow_time_channel`).

**Game-free half** (`gatOS.SimFs/Camera/`, 16 files): the math primitives (`CameraMath`, `Easing`,
`Splines`, `PoseSmoother`, `AnchoredPositionSmoother`); `CameraRules` — every validation predicate, reading **no config** (FOV
bounds are parameters); the addressing vocabulary (`FrameKind`, `AimUpKind`, `CameraModeKind`,
`TargetRef` with exact round-tripping); the **three-layer compositor** `CameraState` — 17 channels,
`Track ?? Override ?? Baseline` per channel, `Compose` **allocating nothing** (asserted < 64 B over
10 000 calls) because it runs every rendered frame; `CameraStore` + the writable `track/` upload dir;
`CameraCommands`' 28 action keys and six order-independent line grammars; `CameraFormat` — where every
composite read-back is built and asserted to **re-parse through its own grammar**, so "read a leaf,
write it straight back" is a no-op; and the track stack (`TrackParser` with its whole named-error
matrix, `TrackEvaluator`, `CameraPlayback`/`CameraPlaybackController`). Tracks are **absolute-from-start,
never incremental**: a full turn is the key pair `0 → 360`, and `CameraPlacement.Spherical` folds `360`
to exactly `0`, so an orbit closes **bit-identically** (asserted twice). Rotation channels default to
**squad** (catmull-rom) rather than slerp, because slerp's C0 flick at each waypoint is the defect a
rotation track exists to avoid.

**Game-side director** (`gatOS.GameMod/Game/Ksa/Camera/`, **21 `[KsaAnchor]`s** + the rebound
`CameraActuator.Focus` = 22, all verified `2026-08-06` against `2026.8.5.5168`): `CameraTargets`
(`vessel:`/`body:`/`part:` → live object, position, velocity, body-fixed frame, up axis — `part:`
reusing the welds anchor resolver, `WeldManager.FindPart` promoted to `internal`), `CameraFrames` (the
six frames × anchor → ECL rotation, the orbit→geodetic→Cartesian placement precedence, and a `geo`
implementation that **calls** `Celestial.GetDirCcfFromLatLon` rather than restating its trigonometry so
a convention change is inherited, not diverged from), `CameraDirector` (take/eased release/hard restore,
the per-frame apply, `mode`/`follow`/`tidal`/`map_scope`, despawn prune, event drain), and
`CameraReader` (the live camera → `CameraStatus`, **publishing both position spellings** so a Cartesian
placement reads back a real latitude).

**Why the same-frame patch is narrow.** `Viewport.OnFrame` is
`GetActiveController().OnFrame(...)` *then* `GetCamera().OnFrame(...)`; the prefix writes immediately
before those calls and the postfix samples immediately after. It guards `Program.MainViewport` by
identity because KSA owns four viewports. The active controller must still write **nothing** — true of
exactly one controller: `FixedController.OnFrame` wraps its entire body in
`if (following != null)`. So `Take()` parks `Viewport.Mode = Fixed` **by direct field assignment** (so
`OnSwitchOn`'s `TimedAlert("Fixed Camera")` never draws in the footage) and calls
`Unfollow(changeControl:false)`, and the driver re-asserts both every frame. **`IVAController` and
`MapController` fail that test outright** — with `Following == null` the first cycles out of IVA and the
second switches to Free, and both assign `PositionEcl`/`LocalRotation` unconditionally — so **IVA and
Map ownership contexts are not implemented and are not implementable without a Harmony patch**. That is
a finding, recorded in `scope/ksa-runtime-coupling.md#camera-mode-contexts` and in `CameraDirector`'s own
type remarks. What C5.2 *does* ship is `/sim/camera/map/scope`.

**C0.1 — the F2 fix, which the camera forced and everything else benefits from.** Both StarMap GUI
hooks sit inside `Program.OnFrame`'s `if (DrawUI)` block, so F2 used to stop the sampler, the command
drain, the audio tick, the thug-life updater, the welds driver and the IVA physics driver dead. The two
hook bodies were split into `Mod.DrivePerFrame`/`DrivePostSolver`, and the new `[StarMapAfterOnFrame]
Mod.OnAfterFrame` (a postfix on `Program.OnFrame` itself) re-runs both **only on the frames the GUI
hooks were skipped**, decided by a boolean latch. `DriveCamera` then runs **unconditionally** at the
end — the pose must be written after the render, and a camera that froze when the player hid the UI
would be useless.

**C4 — the interpolated `time` channel:** `Universe.SetSimulationSpeed(value, alert:false)` (`alert`
matters — the default alert would be *in the footage*), with the speed captured **lazily** the first
frame the channel is driven and restored only if captured, gated on **both** `[control] debug_namespace`
and `[camera] camera_allow_time_channel` (a closed gate is ignored with **one** warning per ownership
session, not an error). No new leaf: `debug/time/warp` already covers the discrete case. Neither public
`SetSimulationSpeed` overload checks `IsAutoWarpActive`, so a driven `time` channel *fights* an active
auto-warp — documented, deliberately unguarded, because the game itself picks no winner.

**Two things worth knowing before writing shots.** `ortho_height` is the one camera change gatOS
**cannot undo** — `Camera` exposes `SetOrthoHalfHeight` but has no public getter in 5168, so it is
written only when explicitly claimed and never restored. And `KittenEva.PrepareWorker` feeds the main
camera's forward/right/up into EVA locomotion, so **while gatOS holds the camera, "forward" for a
kittenaut on EVA is wherever the shot is facing** — documented rather than worked around.

**Collateral:** the mandated folder `Game/Ksa/Camera/` implies a namespace that **shadows the simple
name `Camera` for every file under `Game/Ksa/`**, beating `using KSA;`. New files use
`using KsaCamera = KSA.Camera;`; the only pre-existing casualty was `VesselForceRender.cs`, fixed the
same way. `CameraActuator.Focus` was **rebound** (C1.4): it now sets follow on **both** the viewport's
`BaseCamera` and `MapCamera` with `alert:false`, as the game's own follow action does — the old
single-camera path left the map view on the previous target. Tests: **twelve** `gatOS.SimFs.Tests/Camera/`
fixtures — the four math ones (`CameraMathTests`, `EasingTests`, `SplinesTests`, `PoseSmootherTests`)
plus **379** tests across the surface fixtures (`CameraRulesTests`, `CameraCommandsTests`,
`CameraTreeTests`, `CameraStoreTests`, `CameraStateTests`) and the track fixtures (`TrackParserTests`,
`TrackEvaluatorTests`, `CameraPlaybackTests`). `gatOS.GameMod` has no test project — it is game-coupled,
and every piece of logic that *could* be game-free already is. Catalog: `SPEC_9P_FILESYSTEM.md`;
`docs/KSA_INTEGRATION_MATRIX.md` (programmable camera); `scope/ksa-write-surface.md#camera-director`,
`scope/ksa-read-surface.md#camera`, `scope/ksa-runtime-coupling.md#camera-driver`,
`scope/non-ksa-surface.md#camera-game-free`. **Pending: the in-game pass** (26-item checklist in
`docs/VALIDATION.md`) — including two questions this pass must *answer*, not just check: the
bubble-relative ego behaviour of plan §5.2/C5.3, and whether `pose/roll`'s **defined-not-derived** sign
reads correctly.

---

## Suite totals and pending work

**Full non-IT suite**: green, zero warnings.

**`GATOS_IT=1` suite (last verified 2026-06-13 on Windows/TCG against guest v3)**: 321/321
(including the 43 additional tests from the 2026-06-13 hardening review). The
`HostMountIntegrationTests` fixture requires guest v10 to be published.

**Still pending: the in-game passes** — T6.6/T9.3/G1–G4 and the welds/IVA/parts, thug_life,
per-vessel `scale`/`always_render`, `debug/vessels/<id>/impulse`, `ctl/translate`, `/sim/audio`,
IVA-cabin-physics, FX-editor, **timed-scheduler** and **programmable-camera** checklists in
`docs/VALIDATION.md` are runnable now that the purrTTY tip release is cut, but need a live KSA flight to
complete.

**Next**: M10 (persistence & savegame shape). Everything past M9 is not yet implemented, with
the single exception of T11.1 (QEMU win-x64 bundle) which was pulled forward and is done.
