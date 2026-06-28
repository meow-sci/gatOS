# Milestone & Feature Build Status

Full per-milestone implementation notes, class names, and as-built deviations from the plan.
Summaries and current status table live in [CLAUDE.md](../CLAUDE.md); this file has the detail.

---

## M0 — Repository scaffold: DONE

The solution, all 11 projects, shared build config, the logging shim, `GatOsPaths`, the vendored
purrTTY contract, and CI are in place and green.

---

## M1 — De-risking spike: DONE

All three gates passed against a real Alpine 3.24 guest (kernel 6.18): 9p synthetic files
`cat`/`tail -f`/Ctrl-C-Tflush from the kernel's v9fs client against a hand-rolled C# 9P2000.L
server; SSH.NET 2025.1.0 shell with **live resize** against dropbear; a known-good QEMU invocation.
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
- New `/sim/events`: engine-state, flameout, docked/undocked, decoupled, animation-complete,
  battery-depleted/charged

**Integration layer additions (`gatOS.GameMod/Game/Ksa/`):**
- `Readers/{VesselReader (now core + a guarded enrich pass),BodyReader}`

### G4 — Full control surface

**Writes:** `ctl/{throttle,stage,rcs,attitude_mode,attitude_frame,attitude_target,burn}`,
`engines/<n>/min_throttle`, `rcs/<n>/active`, `lights/<n>/{on,brightness,color,inner_angle,outer_angle}`,
`decouplers/<n>/fire`, `docking/<n>/undock`, `ctl/focus` (+ `bodies/<id>/focus`), and the
**`/sim/debug/`** cheat namespace (gated by
`[control] debug_namespace`: `vessels/<id>/{teleport,refill_fuel,refill_battery}`,
`vessels/<id>/docking/<n>/pushoff_impulse`, `time/warp`, `focus` (camera-by-id, vehicle/body),
`control_vessel` (focus + control)).

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

## Welds + `always_render_iva` + parts listing (ex-`unscience`): Code DONE; in-game pass pending

Three additions ported from the sibling `unscience` mod, exposed **only** on the gatOS surfaces (9p
`/sim` debug + HTTP `/v1` + MQTT — **no ImGui**). KSA-coupled code is confined to
`gatOS.GameMod/Game/Ksa/` per the G2 rule; the snapshot/command plumbing is game-free. KSA bindings
verified against decomp `2026.6.9.4750` (anchors `2026-06-28`).

**Parts listing** — `vessels/by-id/<id>/parts/<n>/` (**top-level parts only**, no subparts), gated by a
new `telemetry_vessel_parts` config key (default true). Leaves: `instance_id` (uint — the **stable** weld
anchor handle), `id`, `display_name`, `template`, `is_root`, `subpart_count`, `position`.
`Game/Ksa/Readers/PartsReader.cs` builds `PartSnapshot[]` from `Vehicle.Parts.Parts`, cached per vehicle
in a `ConditionalWeakTable<Vehicle,…>` and rebuilt on a `Vehicle.Parts.Count` change or every 10 s (sim
seconds). The sampler projects it per vessel when the gate is on.

**`always_render_iva`** — global render cheat at `/sim/debug/always_render_iva` (`debug.always_render_iva`,
Frame, vessel-agnostic) that forces interior (IVA) part meshes to render outside the IVA camera by flipping
`PartModelModule.Template.Internal=false`. `Game/Ksa/Render/IvaForceRender.cs` installs two Harmony patches
on its **own** `Harmony("gatos.iva")` instance **only while enabled** (a `PartModel(PartModelModule.Template)`
ctor postfix + an editor-only `PartModel.AddInstance` postfix) and bulk-flips/tracks the internal templates
over `PartModel.Instances`; disable restores the tracked templates and unpatches. `Actuators/IvaActuator.cs`
is the thin actuator.

**Welds** — rigidly attach a source vessel to a target vessel's part (a game hack).
`Game/Ksa/Welds/{WeldEntry,WeldEngine,WeldManager}.cs`: `WeldManager` is the game-thread registry +
per-frame driver, `WeldEngine` the stateless teleport math ported verbatim from `unscience` (orientation
stored as an authoritative `doubleQuat`, Euler display-only; `weld_here` capture is the inverse transform;
the orbit is stamped with `Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime`). Per-source
controls under `/sim/debug/vessels/<id>/`: `weld` (explicit pose), `weld_here` (capture the current
relative pose), `unweld`; registry view + ops under `/sim/debug/welds/`: `clear`, `count`, and
`<source>/{target,part,offset,rotation,lock_rotation,enabled}`. Action keys `debug.weld_{create,here,
remove,enable,clear}` (all Frame). The driver runs in `OnAfterUi` (`Mod.DriveWelds`) after
`JobSystems.VehicleSolvers.Wait()` — the **third game-thread mutation site**, beside the Frame and Solver
drains; self-gated to a no-op when empty, so **no** Harmony patch and zero cost when unused.

**Wiring:** game-free `gatOS.SimFs/Commands/LineControlFile.cs` (a new whole-line-parsed control archetype,
backs `weld`/`weld_here`); `SimSnapshot` gains `PartSnapshot`/`WeldSnapshot` records (`VesselSnapshot.Parts`,
`SimSnapshot.{Welds,AlwaysRenderIva}`); `Formats` gains `UInt`/`WeldSpec`; `TelemetrySettings` gains the
`VesselParts` gate. `KsaCatalog` (now an instance dispatcher) gains a `WeldManager` ctor param + the 6 new
actions (IVA + `weld_clear` handled vessel-agnostically before vehicle resolution; weld create/here resolve
the target from the command `Token`). `Mod.Game.cs` lazily creates `_weldManager` (game thread), drives it
via the `DriveWelds(dt)` partial from `OnAfterUi`, tears both cheats down via `TeardownGameCheats`, and adds
a "Vessel parts" telemetry menu toggle. `gatOS.GameMod.csproj` gained a `Brutal.Concurrency` reference (for
`JobSystems.VehicleSolvers.Wait()`).

Full catalog: **`SPEC_9P_FILESYSTEM.md`** §3.4.16 (parts) + §3.7 (`debug/welds/**`, `always_render_iva`);
anchors mirrored in `docs/KSA_INTEGRATION_MATRIX.md` and `scope/`. **Pending: the in-game pass**
(checklist in `docs/VALIDATION.md`).

---

## Suite totals and pending work

**Full non-IT suite**: green, zero warnings.

**`GATOS_IT=1` suite (last verified 2026-06-13 on Windows/TCG against guest v3)**: 321/321
(including the 43 additional tests from the 2026-06-13 hardening review). The
`HostMountIntegrationTests` fixture requires guest v10 to be published.

**Still pending: the in-game passes** — T6.6/T9.3/G1–G4 and the welds/IVA/parts checklists in
`docs/VALIDATION.md` are runnable now that the purrTTY tip release is cut, but need a live KSA flight
to complete.

**Next**: M10 (persistence & savegame shape). Everything past M9 is not yet implemented, with
the single exception of T11.1 (QEMU win-x64 bundle) which was pulled forward and is done.
