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

**M3 — gatOS.Vm (QEMU lifecycle): DONE.** `VmHost` (`gatOS.Vm/VmHost.cs`) is the coalesced async
state machine (Stopped→Starting→Running→Stopping/Faulted): `EnsureStartedAsync` runs one shared
boot for concurrent callers (base install → overlay + `DiskLock` → 3 loopback ports → spawn →
SSH-banner readiness raced against process death; one retry on a hostfwd port clash), `StopAsync`
walks the shutdown ladder **QGA `guest-shutdown` → QMP `quit` → kill** and always releases the
disk lock; an unexpected exit while Running flips to Faulted (retryable) and frees the lock.
Supporting cast (all `gatOS.Vm/`, all game-free): `GatOsPaths`; `PortAllocator`; `QemuLocator`
(bundled `win-x64/` on Windows per D5, PATH + Homebrew prefixes on unix); `GuestManifest`
(Tomlyn-parsed `manifest.toml` — the host↔guest boot contract); `DiskManager`+`DiskLock`
(versioned `base-v<N>.qcow2` install, kernel/initrd/manifest/ssh-key under `disks/guest-v<N>/`,
overlays with **bare relative backing refs**, PID lock files with stale reclaim, never
`qemu-img commit`); `QemuCommandBuilder` (per-OS accel ladders `whpx|kvm|hvf→tcg`, **non-x64
hosts collapse to tcg**, `-cpu host` vs `max`, injectable `OperatingSystemFacts`); `QemuProcess`
(3 s survival window, `AccelFailureClassifier` + one forced-tcg retry, `logs/qemu-*.log`
retention ×5, 100-line stderr ring, minimal QMP quit); `ReadinessProbe` (reads the `SSH-` banner —
a bare TCP connect is meaningless, slirp accepts from t=0); `QgaClient` (0xFF-sentinel
`guest-sync-delimited` preamble, all failures soft). `IQemuProcess`/`IDiskManager`/`IQgaClient`
seams + an internal `VmHost` ctor (`InternalsVisibleTo`) make the state machine fake-testable;
`gatOS.Vm.Tests/Integration/` boots the real fetched guest. Measured on the dev Mac (TCG, worst
case): boot→Running→clean QGA stop ≈ 10 s end-to-end. CI installs QEMU, opens `/dev/kvm`, fetches
the pinned guest and runs the whole suite with `GATOS_IT=1`.

**M4 — gatOS.Ssh (the ICustomShell implementation): DONE.** `VmConnectionBroker`
(`gatOS.Ssh/VmConnectionBroker.cs`) owns the shared `VmHost` (disposing the broker stops the VM)
and hands out one **new connected `SshClient` per session**, pinning the guest host key against
the manifest sha256 (mismatch → `HostKeyMismatchException`; one retry on connection-refused).
`SshShellSession` (`gatOS.Ssh/SshShellSession.cs`) implements the vendored
`purrTTY.Core.Terminal.ICustomShell`: trivial ctor (purrTTY's registry probe-instantiates and
disposes, T0.5); `StartAsync` boots the VM lazily and opens an `xterm-256color` PTY at the launch
size (a pre-start resize wins; failures map to `CustomShellStartException` carrying
`VmStartException.UserMessage`); input flows through `ShellInputQueue` (bounded 1 MiB, dedicated
writer thread, overflow drops + logs once per episode — purrTTY's `PtyInputQueue` discipline;
the first write failure terminates the session); `NotifyTerminalResize` →
`ShellStream.ChangeWindowSize` (live SIGWINCH, verified in-guest); one `Terminate` path raises
`Terminated` exactly once (clean close 0; connection error / VM fault / write failure 1 —
sessions watch `VmHost.StatusChanged` for Faulted). **Stopping a session never stops the VM.**
Internal `IShellBroker`/`IShellChannel` seams (+ the `SshShellChannel` adapter owning the
client+stream pair) keep the session unit-testable without SSH.NET: `gatOS.Ssh.Tests` = 19
fake-driven unit tests + 2 `GATOS_IT=1` fixtures against the real guest (broker echo-ok +
tampered-pin rejection; full session: prompt, `stty size` 24 80, live resize → 30 120, `$TERM`,
two concurrent sessions on one VM, session stops leave the VM Running).

**M5 — upstream purrTTY changes: DONE** (in the **purrtty** repo, commits `9fb5e13`/`a56966a`).
T5.1: `purrTTY.GameMod/mod.toml` exports `purrTTY.CustomShellContract` + `purrTTY.Logging` over
the StarMap ALC (`[StarMap] ExportedAssemblies`) so gatOS's `ImportedAssemblies` (M6) resolves
purrTTY's loaded copies — one type identity, one shared `CustomShellRegistry.Instance`. T5.2:
the New Tab / New Window menus append custom shells registered by other mods, enumerating
`CustomShellRegistry.GetAvailableShells()` live per draw (probe-free; solves cross-mod
registration timing without a refresh hook), launching via
`ProcessLaunchOptions.CreateCustomGame(id)`. **Still pending: the purrTTY tip release cut**
(next push to purrtty `main`) — M6 in-game testing needs a purrTTY install carrying both changes.

**M6 — gatOS.GameMod (in-game integration): CODE DONE; T6.6/T6.7 in-game passes pending.**
`Mod` (`gatOS.GameMod/Mod.cs`) is the `[StarMapMod]` entry, a **partial class split on the
game-assembly boundary**: `Mod.cs` itself uses no KSA/Brutal types, so the project builds on CI
without the private game DLLs; the game-coupled half (`Game/Mod.Game.cs` + `Game/BrutalModLogger.cs`)
compiles only when `KSAFolder/KSA.dll` exists (csproj `KsaAssembliesPresent` gate) and is reached
through `partial void` seams (`InstallGameLogging`, `DrawGameUi`) whose calls drop out otherwise.
`OnFullyLoaded` (never throws): swap `ModLog` to a Brutal `LogCategory("gatOS")` sink — isolated in
`TryInstallGameLogging` so a load failure can't abort init, and `BrutalModLogger`'s ctor refuses
while `LogSystem.IsEnabled` is false (calls would silently no-op) — then resolve
`GatOsPaths.ModDir` from the entry assembly, `ModAssets.Validate()` (T6.2: manifest schema +
artifact files + `QemuLocator.Find()`, all problems folded into one `AssetStatus.Error` string),
`GatOsConfig.LoadOrCreate` (T6.3: Tomlyn 2.6 serializer, snake_case, clamp+log normalize, atomic
temp+rename save, first-run file with comment header; bad files → in-memory defaults, never
overwritten), build `VmHost`+`VmConnectionBroker` (**no boot**, D2), register shell `"gatos"`
(purrTTY absence detected after the fact: the contract assembly resolving from gatOS's own folder
means the vendored fallback loaded). `Unload` = `broker.DisposeAsync().AsTask().Wait(15 s)` (the
dispose is the 10 s-grace QGA→QMP→kill ladder). T6.4 diagnostics: `[ModMenuEntry("gatOS")]` menu
(Status/Start VM/Shut Down VM/Restart SimFs (M9)/Open Data Folder/Reset Disk…+confirm-modal) and
an ImGui status window (state, accel + WHPX DISM hint when tcg-on-Windows, ports, uptime, guest version, config,
newest qemu log — cached per `VmStatus` transition — fault reason, asset status, action note); all
actions `Task.Run`, draw code reads volatile state only (rule 5). Two load-order subtleties worth
keeping: game-typed *statics* live in a nested `Palette` class (field types resolve at type load;
`Mod` must load without game DLLs) and the partial impls are `NoInlining` so missing-assembly
faults hit the guarded call sites. **Verified 2026-06-12 by a headless smoke driving the deployed
dist** (LoadFrom + reflection): init, registration, registry-created session booting the real VM
(WHPX fail → auto TCG retry), echo + launch-size + live resize, session stop leaves VM Running,
2.2 s clean unload — see `docs/VALIDATION.md`. **Pending: T6.6 in-game pass** (needs the purrTTY
tip release with M5) **and T6.7** (WHPX-enabled run; `HypervisorPlatform` is off on the game
machine).

**M7 — gatOS.NineP (the 9P2000.L server): DONE.** Three layers, all game-free. `Vfs/`: the
seam SimFs implements — `VfsNode`/`VfsDirectory`/`VfsFile` (ctor takes `(name, qidPath)`; the
tree assigns qids) + per-open `IVfsFileHandle`; **sizes are truthful** — `VfsFile.Size` is
abstract (no fake-4096; spike rule 1 makes it ENODATA-fatal) and an opened fid stats its
handle's own `Size`; `StaticTextFile` snapshots its provider per open; `DelegateDirectory`
covers fixed and dynamic dirs; `VfsErrorException(errno)` surfaces a chosen `Rlerror` (anything
else → EIO). `Protocol/`: `MessageType` (diod numbers), `NinePReader`/`NinePWriter`
(`BinaryPrimitives`, string = `len[2]`+UTF-8, `PatchUInt32` for count back-patching), `Qid`,
`LinuxErrno`, `ProtocolException` (malformed frame ⇒ close connection). `Server/`:
`NinePServer` (listens on **loopback** — slirp delivers guest→10.0.2.2 to 127.0.0.1, no
firewall prompt; `StartAsync(port 0)` → `Port`) runs one `Session` per connection: every
message dispatched as its own task with a per-tag CTS (a parked blocking read never stalls the
loop), responses serialized by a write lock, fids hold walk *paths* (so `..` needs no parent
pointers), readdir **includes `.`/`..`** with next-ordinal cookies and a per-fid listing
snapshot for stable paging, reads clamp to msize−11, Twrite → EACCES, unknown types →
EOPNOTSUPP; **Tflush**: cancel + suppress the old reply, await the handler, then Rflush — a
flushed tag is never answered. `gatOS.NineP.Tests` = 40 tests: codec round-trips, hand-built
golden Rversion/Rgetattr/Rreaddir frames (`NinePServerOptions.AttrTime` injectable), and the
conformance suite driven by a **public managed test client** (`TestClient/NinePTestClient`,
tag-correlated, reused by SimFs.Tests via project reference).

**M8 — gatOS.SimFs (the `/sim` tree): DONE.** `Snapshots/`: the immutable game-free records
(`SimSnapshot`/`VesselSnapshot`/…, plan shapes verbatim) and `SnapshotStore` — volatile
`Current` + a TCS swapped per `Publish` (lock-free reads, capture-and-recheck in
`WaitForNextAsync`, intermediate snapshots are *skipped, never replayed*). `SimFsTree.Build(store)`
→ `/time/{ut,warp}`, `/vessels/active` (alias listing the active vessel's children directly —
`active/…` and `by-id/…` walk to **identical qids**), `/vessels/by-id/<sanitized>/…` (id/name/
situation/parent, position/{cci,lat,lon}, velocity, attitude, altitude, mass, orbit + battery
only-when-present, engines/<n>, tanks/<resource>, stream), `/events`; dynamic nodes are
transient but qids are interned by relpath; ids sanitized to `[A-Za-z0-9._-]` with `~N`
collision suffixes; vanished vessels → ENOENT. `Formats` is the **frozen user-facing surface**:
G9 invariant doubles, `0`/`1` flags, space-separated vector/quat, one value + LF per scalar
file, relaxed-escaping NDJSON lines. The two spike-mandated file models: `StreamFile` =
growing-log (per-open buffer seeded with the current line so size is never 0, pump task appends
per observed publish, 0 bytes at the frontier — `tail -f` follows, `cat` samples; 256 KiB cap
drops whole lines + `{"notice":"dropped"}`), `EventsFile` = blocking-event (read parks for the
next event, delivers, then owes two 0-byte reads; size claims 1 — the only always-truthful
value for variable-length lines). `gatOS.SimFs.Tests` = 38 tests incl. the **M7+M8 exit**:
`Integration/SimMountIntegrationTests` (GATOS_IT) boots the real guest with
`SimPortProvider = () => server.Port`, the guest's `sim-mount` supervisor mounts `/sim` on its
own, and the real v9fs client proves live scalars, the alias, `tail -f stream`, the blocking
events read, and Tflush-survival — this **supersedes the planned ubuntu mount-smoke.sh**
(T7.5/T8.4 as-built notes). Verified on this machine: full `GATOS_IT=1` suite 172/172.

**M9 — live `/sim` telemetry: CODE DONE; T9.3 in-game pass pending.** The pure pieces live in
game-free `gatOS.SimFs/Telemetry/` (as-built deviation — GameMod has no test project):
`EventDiffer` (previous/current snapshot pair → the six fixed event types; a null previous is
the baseline, no events), `SampleClock` (dt accumulator, drift-free phase, long-frame backlog
dropped), `Sanitize` (NaN/Inf→0, radius→altitude for KSA's from-center apsides). The
game-coupled accessor half is `gatOS.GameMod/Game/TelemetrySampler.cs` (compile-gated like all
`Game/**`): every KSA read verified against the decompiled sources — vessels via
`Universe.CurrentSystem.All.UnsafeAsList()`, `Name = Id` (KSA has no separate display name),
lat/lon via the ready-made `IParentBody.GetLlaFromCcf`, `Orbit.Inclination` radians→deg,
engines from `EngineController.VacuumData` (Isp computed: thrust/(massflow·g₀)), tanks from
per-`Mole` SoA state, battery from `Parts.Batteries` (`Joules.Value()`); one try/catch per
vehicle, publish via one volatile swap (threading rules 1–2). Beware: the instance
`double3.Transform` extension drags BepuUtilities into overload resolution — use the static
overload. Wire-up (T9.3) in `Mod`: `OnFullyLoaded` builds `SnapshotStore` + `SimFsTree` and
binds the `NinePServer` (ephemeral loopback port) **before** the `VmHost`, whose
`SimPortProvider` hands the port to the kernel cmdline (null when the bind failed → guest
idles); `OnBeforeUi` → `SampleTelemetry` partial seam (NoInlining, one-error disable latch);
idle gate = VM Starting/Running or `NinePServer.ActiveSessions > 0`; `Unload` disposes the
server after the VM. Diagnostics: **Restart SimFs** menu item (rebinds the **same port** —
it is baked into the running guest's cmdline; the supervisor re-establishes the mount unaided)
and a SimFs status row. **Verified 2026-06-12 by the headless dist smoke** (supervisor mounts
`/sim` by itself during boot, warp readable, restart-remount unaided, 3.3 s clean unload —
`docs/VALIDATION.md`); full `GATOS_IT=1` suite 187/187. **Pending: the T9.3 in-game pass**
(same purrTTY-tip-release blocker as T6.6).

**KSA game-integration G1–G4: CODE DONE; in-game pass pending.** The full read + write surface and
its churn firewall, per `KSA_GAME_INTEGRATION_PLAN.md` (Parts 1–5). G1/G2 (below) are the command
pipeline + integration-layer foundation; G3/G4 (further below) are the read-surface expansion and
the full control surface. The 9p server gained a **write path**
(`gatOS.NineP`): `IVfsWritableFileHandle`, `VfsFile.IsWritable`/`OpenWrite`, writable files stat
`0644` (kernel pre-checks write permission from getattr), `Tlopen` accepts `O_WRONLY`/`O_RDWR` on
writable nodes, `Twrite` dispatches to the handle, `Tsetattr` accepts the `O_TRUNC` size-truncate
on writable files (no-op) and `Tfsync` trivially succeeds; errnos `EBUSY`/`ETIMEDOUT` added. The
**command pipeline** is game-free in `gatOS.SimFs/Commands/`: `SimCommand` (vessel id + action key
+ ordinal + value + `CommandPhase`), `CommandResult`/`CommandOutcome`→errno, `CommandQueue`
(`ICommandSink`: transport threads `SubmitAsync` + await with timeout → ETIMEDOUT, game thread
`Drain(phase, ICommandExecutor, max)`; abandoned-on-timeout commands are skipped; one TCS per
command with `RunContinuationsAsynchronously` so the awaiter never resumes inline on the game
thread), and the two writable archetypes `ControlFile` (STATE: `Flag`/`Fraction`) + `TriggerFile`
(TRIGGER) over a shared line-buffered `CommandFile` (actuates on the first newline → real errno on
the failed `write(2)`; unterminated writes actuate best-effort on clunk). `SimFsTree.Build` gained
an overload taking an `ICommandSink` + transports provider: with a sink it adds the writable
surface — `engines/<n>/active` (STATE), `ctl/{ignite,shutdown,lights}`, `animations/<n>/goal`,
`solar/<n>/goal` (solar-filtered animation view, same ordinal) — and the `/sim/status/`
integration-health tree (`game_version`, `sampler`, `accessors` NDJSON, `transports`); **with no
sink the tree is byte-for-byte the old read-only tree** (existing tests unchanged). The
**integration layer** — the only KSA-touching code, all under `gatOS.GameMod/Game/Ksa/` (compile-
gated like all `Game/**`): `KsaAnchorAttribute`+`ChurnRisk` (per-member provenance; the churn
playbook is a grep over `[KsaAnchor]` build breaks), `Readers/VesselReader` (the M9 sampler reads
refactored out + new lights-master/animations reads), `Actuators/{Engine,Light,Animation}Actuator`
(ignite/shutdown via `Vehicle.SetEnum(VehicleEngine.*)`, engine `active` via
`EngineController.SetIsActive`, lights via `PowerConsumer.LightIsActive`, animation/solar via
`KeyframeAnimationModule.TimeGoal`), and `KsaCatalog` (`ICommandExecutor`: resolves the target
vessel, authority-gates per G-D1, dispatches to the actuator, and owns the **health latches**
`KsaHealth` — a thrown KSA call latches that accessor degraded → EOPNOTSUPP, logs once, surfaces in
`/sim/status/accessors`). `Mod` builds a `CommandQueue` from new `[control]` config
(`control_enabled`/`control_all_vessels`/`debug_namespace`/`command_timeout_ms`/`max_commands_per_frame`),
passes it to `SimFsTree.Build`, and **drains the frame queue each `OnBeforeUi` after the sampler**
(both touch game state only on the game thread). `VesselSnapshot` gained `LightsMasterOn` +
`Animations`; `SimSnapshot` gained `GameVersion`/`SampleRateHz`/`Accessors`. The co-located
per-point reference is **`docs/KSA_INTEGRATION_MATRIX.md`**. Tests: NineP write-path golden +
conformance (`Tlopen`/`Twrite`/`Tsetattr`/`Tfsync`/mode bits), SimFs `ControlFile`/`TriggerFile`/
`CommandQueue` unit tests + a control-surface fixture over the 9p client, and a `GATOS_IT`
control-surface guest fixture (`echo 1 > …/engines/0/active` actuates; `echo bogus` → nonzero
EINVAL). **Pending: the G1 in-game pass** (same purrTTY-tip-release blocker as T6.6).

**G3 (read-surface expansion) & G4 (full control surface): CODE DONE; in-game pass pending.** Parts
4–5 of the plan. **Reads (game-free `SimSnapshot` extensions + `SimFsTree`):** `/sim/system` and
`/sim/bodies/<id>/…` (celestial catalog: mass/radius/mu/soi/rotation, position/velocity ecl, orbit,
atmosphere, ocean — planets/moons are `Celestial`, the star is a separate `StellarBody`);
`time/{sim_dt,warp_speeds,auto_warp}` + the `time/alarm` blocking sim-time wake device (`AlarmFile`,
writable + blocking read); per-vessel `telemetry` (atomic JSON doc), `controlled`, `com`,
`position/ecl`, `velocity/cci`, `navball/…`, `environment/…` (pressure/density/dynamic-pressure/
accel/g-force), orbit extras (lan/argpe/true-anomaly/time-to-ap-pe/next-patch), `encounters`,
engine throttle/propellant/min-throttle, tank `fraction`, battery `fraction`/`capacity`,
`power/{produced,consumed}`, and module dirs `rcs/ solar/ generators/ lights/ docking/ decouplers/`.
New `/sim/events`: engine-state, flameout, docked/undocked, decoupled, animation-complete,
battery-depleted/charged. **Writes (G4 control surface):** `ctl/{throttle,stage,rcs,attitude_mode,
attitude_frame,attitude_target,burn}`, `engines/<n>/min_throttle`, `rcs/<n>/active`,
`lights/<n>/{on,brightness,color}`, `decouplers/<n>/fire`, and the **`/sim/debug/`** cheat namespace
(gated by `[control] debug_namespace`: `vessels/<id>/{teleport,refill_fuel,refill_battery}`,
`time/warp`, `switch_vessel`). New game-free command archetypes: `ControlFile.Number`,
`VectorControlFile`, `EnumControlFile`, `TokenControlFile`; `SimCommand` gained `Values` (vectors)
+ `Token` (enum/free tokens). **The integration layer grew (all `gatOS.GameMod/Game/Ksa/`):**
`Readers/{VesselReader (now core + a guarded enrich pass),BodyReader}`,
`Actuators/{Engine,Light(+per-instance clone),Animation,Staging,Throttle(reflection),Rcs,Decoupler,
FlightComputer,Debug}Actuator`, `KsaCatalog` dispatches all actions (debug-namespace exempt from the
authority gate). **Solver phase:** a Harmony `Priority.First` prefix on
`Universe.ExecuteNextVehicleSolvers` (`Mod.DrainSolverCommands` via `InstallSolverHook`/`RemoveSolverHook`
partial seams) drains `CommandPhase.Solver` commands inside the physics step — the debug refills **and
the flight-computer setpoints** (`attitude_mode`/`attitude_frame`/`attitude_target`/`burn`), which KSA's
async solver snapshot-restores via `FlightComputer.CopyFrom` so a frame-phase write would be clobbered.
Phase is derived from the action by `SimCommand.Phase` (`SolverActions` is the one source of truth).
Co-located reference: **`docs/KSA_INTEGRATION_MATRIX.md`** (now covers G1–G4 + the documented
deferrals: aero `cda` [private], `parts/<instanceId>` tree, per-nozzle engine internals, gimbal
command, RCS pulse). **Pending: the G3/G4 in-game pass** (same purrTTY-tip-release blocker as T6.6).

**Additional transports G5 (HTTP) & G7 (serial/bus framing + live virtio-serial bridge): BUILT; G6 (TypeScript SDK): BUILT.**
Plan Parts 6–8. All game-free and built on the **same** `SnapshotStore` + `CommandQueue` the 9p
tree uses — no second copy of the action table, no new KSA coupling. **`gatOS.Http`** (G5): a raw
loopback-`TcpListener` HTTP/1.1 server (not `HttpListener` — that needs http.sys URL-ACL/admin on
Windows; not GenHTTP — avoids a heavy dependency tree in the mod ALC) serving `/v1`: JSON snapshot
projections (`snapshot`/`time`/`status`/`system`/`bodies[/{id}]`/`vessels[/{id}[/telemetry]]`), SSE
`GET /v1/events` and per-vessel `GET /v1/vessels/{id}/stream` (the HTTP twin of the 9p `stream`
file), long-poll `GET /v1/time/wait`, `GET /v1/openapi.json`, and one generic `POST /v1/command`
carrying the `SimCommand` shape with `CommandOutcome`→HTTP-status+`{errno,message}` (debug.* gated).
The read JSON is produced by the shared, game-free `SimJson` projection layer (`gatOS.SimFs`) — the
single source of truth both HTTP and MQTT serve, so the two stay byte-compatible (transport-parity
invariant below). It also serves the **field-level filesystem mirror** `GET /v1/fs/<path>` (one
endpoint per `/sim` leaf, raw text value), `?stream=1` for an SSE feed of one value on change, and
`POST /v1/fs/<path>` to write/actuate one field (the `echo > file` shape) — all resolved by walking
the same `/sim` VFS tree via `VfsScan` (config `[http] http_field_endpoints`). `Mod`
hosts it (config `[http] enabled`/`preferred_port`=4242 ephemeral-fallback); `VmHost`/`QemuCommandBuilder`
inject `gatos.httpport` on the cmdline (guest dials `10.0.2.2:<port>` outbound via slirp, like 9p).
**`gatOS.Bus`** (G7): the framing codecs — `Ccsds` (TM space packets), `Nmea` (sentences +
XOR checksum), `ScpiCommandPort` (`CTL:ENG0:ACT 1`→`SimCommand`→sink, `OK`/`ERR <errno>`),
`SerialTelemetry` (NDJSON/NMEA/CCSDS frames) — **plus the live serial bridge**: `SerialBridge`
(duplex over one `Stream` — telemetry pump out + SCPI command lines in, both targeting the active
vessel) and `SerialBridgeConnector` (connect-with-retry to the QEMU `gatos.serial` chardev,
mirroring `QgaClient`). `VmHost` allocates a 4th loopback port + `QemuCommandBuilder` wires a
`virtserialport,name=gatos.serial` (the guest's init symlinks it to `/dev/virtio-ports/gatos.serial`,
no rebuild needed); `Mod` starts/stops the connector on the VM `Running`/stop transitions per
`[serial] serial_telemetry_port`/`serial_command_port`/`serial_mode`/`serial_interval_ms`.
**`examples/sdk-ts`** (G6): a TypeScript/Bun SDK with
`FsTransport`+`HttpTransport` behind one typed `GatosClient` (the per-vessel `telemetry` doc is the
same `Formats.VesselTelemetry` JSON over both — parse-identical; the 9p file appends a trailing LF
per the file convention, the HTTP/MQTT bodies do not), reactive events, warp-aware time helpers,
`GatosError` errno mapping, and
example scripts + a pure-shell README. Config grew `[http]` + `[serial]` flags. Tests:
gatOS.Http 37 (HttpClient over the live socket, incl. `/v1/system`, `/v1/vessels/{id}` + `/v1/bodies/{id}`,
the per-vessel stream SSE, and the `/v1/fs/<path>` field read sweep / per-value SSE / per-archetype
writes / error + disabled paths),
gatOS.Bus 32 (codec/SCPI + `SerialBridge`/connector
over a loopback socket pair). Full non-IT suite green, zero warnings. **Guest image v3 is BUILT
(`GUEST_VERSION=3`, released + fetched).** All three extra transports are **validated in-guest**
(2026-06-13, Windows/TCG) by the `GATOS_IT` fixture `SimFs.Tests/Integration/TransportEnvIntegrationTests`
against the real v3 guest: HTTP/MQTT — the `sim` `/etc/hosts` alias, `$GATOS_HTTP`/`$GATOS_MQTT`
(`/etc/profile.d/gatos.sh`) + `/run/gatos/{http,mqtt}-port`, a **live telemetry read over slirp**
(`wget $GATOS_HTTP/time`), MQTT-broker TCP reachability; serial — the guest reads an NDJSON frame off
`/dev/virtio-ports/gatos.serial` and an `echo CTL:… >` SCPI command actuates (`OK`) with a bad line
rejected (`ERR EINVAL`). See `docs/VALIDATION.md`.

**MQTT transport (`gatOS.Mqtt`, MQTTnet): BUILT.** A user-requested additional bridge alongside
9p/HTTP/serial. An **embedded MQTTnet broker** (`SimMqttBroker`) in the host process on a loopback
port (guest reaches it at `10.0.2.2:<port>`, like the others — no external broker) over the same
`SnapshotStore` + `CommandQueue` (and the same `SimJson` projection layer HTTP uses): a publish pump
emits retained topics `gatos/time`, `gatos/status`, `gatos/system`, `gatos/bodies`, `gatos/snapshot`
(whole world), `gatos/vessels/<id>/telemetry` (the compact SDK-stable doc) and
`gatos/vessels/<id>/snapshot` (the full granular vessel record), plus the non-retained
`gatos/events`; clients publish a JSON `SimCommand`
to `gatos/command` and the outcome is published to `gatos/command/result` (debug.* gated). A second
pump (config `[mqtt] mqtt_field_topics`, cadence `field_feed_hz`=4) mirrors the **whole `/sim`
filesystem leaf-by-leaf** under retained `gatos/sim/<path>` (one topic per scalar/`ctl`/`debug` field,
changed-only, via the shared `VfsScan` walk over the same `/sim` tree — so an MQTT explorer renders
the device tree, not just JSON blobs): canonical `vessels/by-id` only (the duplicate `active` alias
is replaced by a `gatos/sim/vessels/active_id` pointer), and a client writes one field by publishing
its value to `gatos/sim/<path>/set` (same actuation as a 9p `echo`; outcome on
`gatos/command/result`). `Mod` hosts
it (config `[mqtt] enabled`/`preferred_port`=1883 ephemeral-fallback; the broker also gets the
`SimTransportsStatus` provider so `gatos/status` carries the bound-ports line); `VmHost`/`QemuCommandBuilder`
inject `gatos.mqttport`; the guest exports `$GATOS_MQTT=sim:<port>` (active on guest v3, like
`$GATOS_HTTP` — validated in-guest, see `docs/VALIDATION.md`). `gatOS.Mqtt.Tests` (19) connect a real
MQTTnet client to the broker (the full topic set incl. the per-vessel `snapshot`, `gatos/events`, the
field-level `gatos/sim/<path>` read sweep + `/set` actuation across every write archetype + set
error paths, enriched time/status, retained delivery to a late subscriber, command routing + errno,
debug gating, and that consumed commands are not rebroadcast). Full `GATOS_IT=1` suite green on guest
v3, zero warnings.
**Still pending: the in-game pass** (purrTTY tip release is now cut — the T6.6/T9.3/G1–G4 checklists
in `docs/VALIDATION.md` are runnable but need a live KSA flight). The headless host↔guest stack
(VM, shells, `/sim`, HTTP/MQTT/serial transports, control surface) is otherwise fully built and
validated against the real guest.

Everything past M9 is **not yet implemented** — next is M10 (persistence & savegame shape) —
with one exception pulled forward: **T11.1 QEMU win-x64 bundle tooling is DONE**
(`tools/fetch-qemu.{ps1,sh}` populate
`vendor/qemu/win-x64/` from the pinned Weil installer; pin + trimmed file list live in
`tools/qemu-win64-files.txt`, derivation helper `tools/Get-QemuImportClosure.ps1`; see the
T11.1 as-built note in `OS_PLAN.md`). On Windows, headless tests resolve that vendored bundle
via `QemuLocator.OverridePath` (`VendoredQemuSetup` in `gatOS.Vm.Tests`/`gatOS.Ssh.Tests`/
`gatOS.SimFs.Tests`), and `QemuLocator.Find()` throws the typed `QemuNotFoundException` (not
`InvalidOperationException`) when `GatOsPaths.ModDir` is unset, so the test skip-gate works.
The full `GATOS_IT=1` suite was verified green on the Windows 11 game machine against **guest v3**
(278/278, 0 skipped, 2026-06-13 — TCG fallback: WHPX needs the off-by-default `HypervisorPlatform`
Windows feature; guest boot ≈ 7 s under TCG). A follow-up review (2026-06-13) hardened the 9p
server against a tag-reuse-while-in-flight teardown (`Session` frees a tag *before* writing its
reply — this was the real cause of the `find /sim` flake) and added 43 game-free tests across
NineP/SimFs/Bus/MQTT/HTTP for control-file errno paths, codec edge cases, transport error/retained
paths, and a full control-enabled tree crawl; the non-IT suite is green with zero warnings and the
`/sim` mount IT fixture re-verified against guest v3 (suite now 321 with `GATOS_IT`). Track real
progress against the milestone table in `OS_PLAN.md` Part 3; do not document planned code here as
if it exists.

## Build and Test Commands

```bash
dotnet build gatos.slnx                          # build the whole solution
dotnet test  gatos.slnx --nologo -v quiet        # full suite (5 test projects)
dotnet build gatOS.Vm                            # one project
dotnet build gatOS.GameMod                       # also deploys the mod folder (see below)
```

Every `gatOS.GameMod` build deploys the complete mod folder via its `CopyCustomContent`
target (T6.5): managed payload (all output DLLs except loader-supplied 0Harmony/StarMap.API),
mod.toml + deps.json, licenses, `guest/out/**` → `<dist>/gatOS/guest/` (High-importance
message when missing — fetch or build the guest first for an in-game-usable dist), and
`vendor/qemu/win-x64/**` when present. Destination: `GATOS_DIST_DIR` (CI) else the per-OS KSA
mods dir (`SelectedDistModDir` in `Directory.Build.props`). The managed payload is
wipe-cleaned each deploy; `guest/`+`qemu/` copy incrementally (`SkipUnchangedFiles`).

Every task ends with **both** the build and the test suite green. Keep test output minimal (no
Console spew from passing tests). Integration tests that need a real VM are gated by the `GATOS_IT=1`
env var and self-skip (`Assert.Ignore`) otherwise, so plain `dotnet test` never needs QEMU.
To run them locally: `guest/fetch-guest.sh` once (or build the image), have QEMU available —
on Linux/macOS a system install, on Windows run `tools/fetch-qemu.ps1` once (tests pick up
`vendor/qemu/win-x64/` automatically) — then `GATOS_IT=1 dotnet test gatos.slnx`
(PowerShell: `$env:GATOS_IT='1'; dotnet test gatos.slnx`). With `GATOS_IT=1`, missing prerequisites are hard failures,
not skips (see `gatOS.Vm.Tests/TestEnv.cs`); tests needing only `qemu-img` (DiskManager) also run
un-gated whenever QEMU is present. CI runs the full suite with `GATOS_IT=1` under KVM.

## Repository layout & project map

```
gatos.slnx                      XML solution (17 projects: 9 libs/mod + 8 test projects)
Directory.Build.props           shared build config + KSA/dist path resolution
CLAUDE.md / README.md           this file; user-facing readme
OS_IDEA.md / OS_ANALYSIS.md / OS_PLAN.md   goals / research / execution plan
KSA_GAME_INTEGRATION_PLAN.md    proposed plan: /sim read/write expansion, control files, HTTP +
                                bus transports, KSA-churn integration layer (G-series phases)
docs/VALIDATION.md              in-game validation record (T6.6/T6.7 checklists + results)
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
```

### Projects and the dependency rule

```
gatOS.Logging                    (no deps)            game-free logging shim
gatOS.NineP    → Logging                              9P2000.L codec + server + VFS (M7, built);
                                                      VfsScan (walk/read/write) + VfsFile.IsStreaming
                                                      back the field-level transport mirrors
gatOS.SimFs    → NineP, Logging                       /sim tree, snapshots, stream/events, AlarmFile,
                                                      EventDiffer/SampleClock/Sanitize (M8+M9+G3, built);
                                                      Formats + SimJson (the shared JSON projection
                                                      HTTP/MQTT both serve — transport parity);
                                                      Commands/ (SimCommand, CommandQueue, Control/Trigger/
                                                      Vector/Enum/Number/Token control files — G1+G4, built)
gatOS.Http     → SimFs, Logging                       magic HTTP /v1 server (raw TcpListener; G5, built)
gatOS.Bus      → SimFs, Logging                       serial/bus framing CCSDS/NMEA/SCPI + the gatos.serial
                                                      SerialBridge/Connector over QEMU virtio-serial (G7, built)
gatOS.Mqtt     → SimFs, Logging, MQTTnet              embedded MQTT broker over the same store+sink (built)
gatOS.Vm       → Logging, Tomlyn                      QEMU lifecycle, disks, ports, GatOsPaths (M3, built)
gatOS.Ssh      → Vm, Logging, vendor/purrTTY, SSH.NET SshShellSession : ICustomShell (M4, built)
gatOS.GameMod  → Ssh, SimFs, Http, Mqtt, Bus, Vm, Logging, vendor/purrTTY,
                  KSA DLLs, StarMap.API, Lib.Harmony, ModMenu.Attributes, Tomlyn   the KSA mod (M6, built)
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
> `gatOS.GameMod/Game/Ksa/`** (`Readers/`, `Actuators/`, `KsaCatalog`, annotated with
> `[KsaAnchor]`). Transports (9p/HTTP/serial), the `/sim` tree, formats and the command pipeline
> never see one — they speak `SimSnapshot` (reads) and `SimCommand`/`ICommandExecutor` (writes).
> When a decomp drop breaks the build, the diff is confined to that folder + `docs/KSA_INTEGRATION_MATRIX.md`.
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

### Runtime architecture (recap)

```
KSA game process                                          QEMU subprocess
┌──────────────────────────────────────────────┐         ┌──────────────────────────────┐
│ purrTTY mod (stock — M5 landed upstream)     │         │ Alpine guest (hostname gatos)│
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
   construction); never pass a phase at a construction site.
2. **9p server threads never touch game state** — they read the latest published snapshot, and for
   writes they only *enqueue* an immutable `SimCommand` and await its result (never executing it).
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
  loaded copies over the StarMap ALC (D6) via mod.toml `ImportedAssemblies` — the loader
  consults dependency-mod ALCs before the mod-local resolver. `GameMod` references them with
  `<Private>true</Private>` anyway: that puts the vendored copies in the dist **and** in
  deps.json (StarMap resolves a mod's own files through `AssemblyDependencyResolver`, which
  only sees deps.json entries), so gatOS still loads — registering into a registry nobody
  consumes — when purrTTY is absent.
- Commits: small, per-task, message starts with the task id (e.g. `T3.4: qemu readiness probe`).

## Instruction Maintenance Mandate (MUST)

Whenever you make meaningful repository changes, you MUST evaluate and update this file in the same
work item if it affects: project structure/dependencies, the host↔guest seam, build/test/deploy
commands, the threading rules, or **milestone/feature status**. As each milestone lands, move it
from "not yet implemented" to documented reality and add the concrete navigation pointers (class
names, files) — prefer verified code paths over the plan when documenting behavior. Remove defunct
guidance immediately. Do not document planned-but-unbuilt code as if it exists.
