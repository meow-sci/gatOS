# Scope — Game-Free Surface (KSA coupling: none)

> The other ~90% of gatOS: everything that builds and tests **without** the KSA game assemblies. Included
> so this catalog is *complete* — but every entry here is, by the dependency rule, **immune to KSA
> updates**. A game update can never break these; they only ever change what *values* flow through (which
> is the read/write surface's concern, not theirs).
>
> Canonical depth for these lives in [`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) and
> [`docs/MILESTONES.md`](../docs/MILESTONES.md); this page is the inventory + the "no KSA coupling"
> attestation, not a re-documentation.

## Dependency-rule attestation
Verified (csproj references + `using` graphs): **no `gatOS.{Logging,Vm,Ssh,NineP,SimFs,Http,Mqtt,Bus}`
project references KSA, Brutal, or StarMap.** Only `gatOS.GameMod` does, condition-guarded on
`Exists('$(KSAFolder)/…')`. This is what keeps the 9P server, VM manager, transports and SSH session
headlessly testable, and what confines KSA-update breakage to `Game/Ksa/**` (see
[`FULL_SCOPE.md`](FULL_SCOPE.md#3-where-ksa-actually-appears-in-gatos-the-complete-coupling-census)).

---

## VM / QEMU lifecycle — `gatOS.Vm`
| Feature | Key files | Responsibility | External dep |
|---|---|---|---|
| VM state machine | `VmHost.cs` | boot/shutdown ladder (QGA→QMP→kill), one `SemaphoreSlim`, status events | QEMU |
| QEMU cmdline | `QemuCommandBuilder.cs` | builds `-netdev`/hostfwd, injects `gatos.*port=` kernel cmdline, serial chardev | QEMU |
| Disks | `DiskManager.cs` | qcow2 base + per-profile overlay, PID locks, overlay delete (reset) | `qemu-img` |
| Ports | `PortAllocator.cs` | ephemeral loopback ports for ssh/sim/mnt/http/mqtt/serial | — |
| Guest agent / readiness | `QgaClient.cs`, `ReadinessProbe.cs`, `QemuLocator.cs` | QGA comms, SSH readiness, QEMU discovery (incl. bundled `vendor/qemu/win-x64`) | QEMU |
| Paths | `GatOsPaths.cs` | the **single** source of all filesystem locations (mod dir, data dir, disks, logs, config) | — |
| Config | `Configuration/GatOsConfig*` (in GameMod) + Tomlyn | TOML load/seed/save; sections `[common]/[telemetry]/[control]/[http]/[mqtt]/[serial]/[[mounts]]` | Tomlyn |

Ports + disk layout table: [`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md#port-allocation).

## SSH transport — `gatOS.Ssh`
| Feature | Key files | Responsibility | External dep |
|---|---|---|---|
| Shell session | `SshShellSession.cs` | implements purrTTY `ICustomShell`; SSH tunnel → guest shell, I/O bridge | SSH.NET; purrTTY contract (vendored) |
| Connection broker | `VmConnectionBroker.cs` | ensures VM running before dial; session lifecycle | — |
| Channel mux | `SshShellChannel.cs`, `ShellInputQueue.cs`, `IShellChannel.cs` | stdin/stdout/stderr, PTY resize | SSH.NET |

> The purrTTY `ICustomShell` contract is a **mod-ecosystem ABI**, not KSA — tracked in
> [`ksa-runtime-coupling.md#mod-ecosystem-abis`](ksa-runtime-coupling.md#mod-ecosystem-abis).

## 9P server + VFS — `gatOS.NineP`
| Feature | Key files | Responsibility |
|---|---|---|
| 9P2000.L codec | `Protocol/NinePReader.cs`, `NinePWriter.cs`, `MessageType.cs`, `Qid.cs`, `LinuxErrno.cs` | wire framing + errno mapping |
| Server | `Server/NinePServer.cs`, `Session.cs` | loopback TCP, one session per v9fs mount |
| VFS | `Vfs/VfsNode.cs`, `VfsDirectory.cs`, `VfsFile.cs`, `StaticTextFile.cs`, `DelegateDirectory.cs`, `VfsScan.cs` | node/dir/file tree; `VfsScan` backs the field-level transport mirrors; `VfsFile.IsStreaming` marks blocking/growing files |
| Host folder mounts | `Vfs/HostDirectory.cs`, `HostFile.cs`, `HostMountTree.cs` | `/mnt/<name>` host-folder passthrough (mtime + rwx) |

## SimFs — `/sim` tree, snapshot/command model — `gatOS.SimFs`
| Feature | Key files | Responsibility |
|---|---|---|
| Snapshot model | `Snapshots/SimSnapshot.cs`, `VesselSnapshot.cs`, `BodySnapshot.cs`, `SystemSnapshot.cs` | the immutable read seam (no game types) |
| Snapshot store | `Snapshots/SnapshotStore.cs` | single-volatile-swap pub/sub |
| Command model | `Commands/SimCommand.cs` | the immutable write seam; `Phase`/`SolverActions` (single source of truth for Frame vs Solver) |
| Command queue | `Commands/CommandQueue.cs` | transport-enqueue / game-thread-drain; `control_enabled`/`debug_namespace` gates |
| Tree builder | `SimFsTree.cs` | constructs the whole `/sim` VFS |
| Stream / events / alarm | `StreamFile.cs`, `EventsFile.cs`, `EventDiffer.cs`, `AlarmFile.cs` | growing-log telemetry, snapshot-diff events, time-warp-aware alarm |
| Control files | `Commands/{ControlFile,TriggerFile,VectorControlFile,EnumControlFile,NumberControlFile,TokenControlFile}.cs` | the writable `/sim` leaves |
| Batch control | `Commands/BatchFile.cs` (+ `CommandQueue.SubmitBatchAsync`, `ICommandSink` default) | `/sim/ctl/batch`: `<path> <value>` lines + `commit` → ONE command group the game thread drains atomically (same tick, in order, never split); all-or-nothing parse, one phase per batch, ≤64 commands. Entirely game-free — it resolves paths against the same VFS tree and reuses each control file's parser (`CommandFile.ParseToken`), so no new KSA binding |
| JSON / formats | `SimJson.cs`, `Formats.cs`, `Sanitize.cs` | the one read projection HTTP+MQTT share; 9p file formats; NaN/Inf scrub |
| Telemetry gating | `TelemetrySettings.cs` | runtime-mutable cadence + per-stream gates (read by the sampler each tick) |
| Audio clip store | `Audio/AudioStore.cs`, `AudioDirectory.cs`, `AudioCommands.cs` | in-memory uploaded clips behind `/sim/audio` (caps, versioning, ready-on-clunk), the writable `file/` VFS dir + upload handles, the `play`/`set`/`stop` grammars, the channel-status snapshot and the bounded `audio.finished` event queue — the FMOD calls themselves are the write surface's concern ([`ksa-write-surface.md#audio`](ksa-write-surface.md#audio)) |

> `SimSnapshot` and `SimCommand` are the **firewall**: KSA types stop here. `EventDiffer` only diffs
> snapshots, so it inherits — but never widens — the KSA reads. See
> [`ksa-read-surface.md#events`](ksa-read-surface.md#events).

## HTTP `/v1` — `gatOS.Http`
`SimHttpServer.cs` (raw `TcpListener`, no HTTP lib), `HttpRequestLine.cs`, `OpenApi.cs`. Serves
`/v1/{snapshot,system,bodies,vessels/<id>[/telemetry|/stream|/events],status,command}` + the field-level
`/v1/fs/<path>` mirror (SSE via `?stream=1`). Projects the same `SimSnapshot`/`SimCommand`.

## MQTT — `gatOS.Mqtt`
`SimMqttBroker.cs` — embedded MQTTnet broker (loopback). Retained `gatos/{snapshot,system,bodies,time,status,events}`
+ `gatos/vessels/<id>/{telemetry,snapshot,stream}`; `gatos/command` in, `gatos/command/result` out;
`gatos/sim/<path>` field mirror. Changed-only publisher (the one eager pusher). Dep: MQTTnet.

## Serial / bus — `gatOS.Bus`
`SerialBridge.cs` over QEMU virtio-serial (`gatos.serial`). Wire formats `SerialTelemetry.cs` (NDJSON),
`Nmea.cs`, `Ccsds.cs`; `ScpiCommandPort.cs` (SCPI → `SimCommand`); `SerialBridgeConnector.cs` (chardev
lifecycle, tied to the VM run). Cadence `serial_interval_ms`.

## Logging — `gatOS.Logging`
`ModLog.cs` (console-backed; `GameMod` swaps a game sink via `ModLog.SetLogger`), `PerfStat.cs`
(alloc-free timing accumulator for the status window). No game dependency by rule.

## Guest image — `guest/`
Alpine build/fetch pipeline (`build-image.sh`, `fetch-guest.{sh,ps1}`, `GUEST_VERSION`=14). Overlay
supervisors in `rootfs-overlay/sbin/` (`init-gatos`, `sim-mount`, `mnt-mount`, `qga-gatos`) read
`gatos.*port=` off the kernel cmdline; `usr/local/bin/tail` is the busybox-`tail -f` poll-mode shim that
makes `tail -f /sim/...` work over v9fs (guest v14 fix). `manifest.toml` is the host boot contract. No
custom guest binaries touch KSA — the guest never knows KSA exists; it sees a filesystem.

## TypeScript SDK — `examples/sdk-ts/`
Standalone Bun/TS client (`src/{client,models,transport,errors}.ts`) over HTTP `/v1` or MQTT. Not part of
the .NET solution. Frozen against the compact `Formats.VesselTelemetry` doc shape.

---

## Why these are KSA-update-proof
Each consumes only `SimSnapshot` (reads) / `SimCommand` (writes) / `VfsNode` (tree) — never a KSA type. A
KSA update changes the *contents* of a snapshot (caught and documented on the read/write pages), but the
*shape* of these subsystems is fixed by gatOS, not the game. The only way a KSA update reaches them is if
gatOS deliberately changes a `SimSnapshot`/`SimCommand` field in response — which is a gatOS API change,
tracked in [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md), not KSA churn.
