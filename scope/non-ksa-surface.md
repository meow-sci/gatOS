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
Verified (csproj references + `using` graphs): **no `gatOS.{Logging,Vm,Ssh,NineP,SimFs,Http,Mqtt,Mcp,Bus}`
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
| Ports | `PortAllocator.cs` | ephemeral loopback ports for QEMU's SSH/QGA/QMP channels and optional serial bridge | — |
| Guest agent / readiness | `QgaClient.cs`, `ReadinessProbe.cs`, `QemuLocator.cs` | QGA comms, SSH readiness, QEMU discovery (incl. bundled `vendor/qemu/win-x64`) | QEMU |
| Paths | `GatOsPaths.cs` | the **single** source of all filesystem locations (mod dir, data dir, disks, logs, config) | — |
| Config | `Configuration/GatOsConfig*` (in GameMod) + Tomlyn | TOML load/seed/save; flat keys grouped for HTTP/MQTT/MCP with consistent `*_bind_host` + `*_preferred_port` listener settings (loopback defaults), plus telemetry/control/serial/display/audio/IVA/camera/schedule and `[[mounts]]` | Tomlyn |

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

### Timed command scheduler — `/sim/ctl/timed_batch` + `/sim/ctl/schedules/**` {#scheduler}

`gatOS.SimFs/Commands/` (plans/SCHEDULER_ASBUILT.md). A **complete write feature with zero KSA
bindings**: it schedules gatOS's own `SimCommand`s against a timeline, so it never learns what a
command *does*. `KsaCatalog` routes the whole `schedule.*` family straight to `ScheduleStore.Execute`
— there is nothing game-side to re-validate, and a second implementation would be a second definition.
Gated by `[schedule] schedule_enabled` (off ⇒ `SimFsTree.Build` gets `schedules: null` and the two
nodes never exist; `/sim/ctl/batch` is unaffected either way).

| Feature | Key files | Responsibility |
|---|---|---|
| Timeline primitive | `Commands/PlaybackClock.cs` | `ClockBase` (`Render`/`Wall`/`Ut`), `PlaybackState`, and the one clock: position/duration/rate `[0,100]` (`0` = frozen)/loop/pause/scrub, `LoopCount` + `ScrubGeneration` edge counters. Doubles published as bit-cast `long`s through `Volatile`, so transport threads read torn-free without a lock |
| Committed schedule | `Commands/Schedule.cs` | `ScheduleEntry(DeadlineMs, Path, Command, IsTrigger)` + the immutable `Schedule`; entries stably sorted, so authored order survives inside one deadline and a group of `0`-offset lines behaves exactly like a `ctl/batch` |
| Cursor + catch-up | `Commands/Scheduler.cs` | one live player's cursor and the **coalescing** policy: among the entries due in one tick every trigger fires in order, but only the **last per path** of the non-triggers does, cross-path order preserved — bounding a hitch's burst by *distinct leaves* instead of entries. Allocation-free on a 0-or-1-entry tick |
| Registry + executor | `Commands/ScheduleStore.cs` | `ScheduleLimits`, the `IPlaybackPlayer` interface (which the camera track player also implements), id reservation, the game-thread `Activate`/`AdvanceAll`/`Tick`/`Execute`/`Clear`, the cap-pressure-only oldest-first eviction of **finished** players (`schedule.evicted`), and the bounded event queue |
| Tree + grammar | `Commands/ScheduleTree.cs`, `TimedBatchFile.cs` | the `/sim/ctl/{timed_batch,schedules/}` nodes (every status leaf a live line, since `t` advances every frame) and the `@id`/`@clock`/`@rate`/`@loop`/`@group` + `<offsetMs> <path> <payload>` + `commit` grammar — up-front, all-or-nothing validation; phase mixing deliberately **allowed** (the one relaxation of `BatchFile`'s rule); `[control] enabled = false` ⇒ `EACCES` on commit |
| Fire-and-forget seam | `Commands/CommandQueue.cs` | the added `IPostObserver` + `CommandQueue.Post(command, observer, token)` — no TCS, honours `ControlEnabled`, routes by `command.Phase`, and reports each outcome inline on the game thread into `schedules/<id>/last_error` |

The game side is a **driver, not a binding**: `Mod.TickSchedules` (the seventh game-thread work site)
plus the sampler's `DrainEvents` — see
[`ksa-runtime-coupling.md#schedule-tick`](ksa-runtime-coupling.md#schedule-tick) for the clock
sourcing, self-gating and failure mode. Transport parity is structural (ordinary VFS leaves ⇒ the HTTP
`/v1/fs/...` and MQTT `gatos/sim/...` mirrors light up with no new code). Tests:
`gatOS.SimFs.Tests/Commands/{PlaybackClockTests,SchedulerTests,TimedBatchFileTests,ScheduleTreeTests,
ScheduleEvictionTests}` + the extended `CommandQueueTests`. In-game pass pending
([`../docs/VALIDATION.md`](../docs/VALIDATION.md)).

### Programmable camera — the game-free half {#camera-game-free}

`gatOS.SimFs/Camera/` (plans/CAMERA_ASBUILT.md). Everything about `/sim/camera` **except the ~22
lines' worth of KSA member access in the director** lives here and is unit-tested on a bare host; the
game side is [`ksa-write-surface.md#camera-director`](ksa-write-surface.md#camera-director) /
[`ksa-read-surface.md#camera`](ksa-read-surface.md#camera). Gated by `[camera] camera_enabled`.

| Feature | Key files | Responsibility |
|---|---|---|
| Math primitives | `Camera/CameraMath.cs`, `Easing.cs`, `Splines.cs`, `PoseSmoother.cs` | `Vec3`/`Quat`, the ease curves (incl. cubic-bezier), Catmull-Rom / squad / bezier interpolation, and the critically-damped pose smoother — plain arithmetic over plain structs |
| Vocabulary + rules | `Camera/CameraTypes.cs`, `CameraRules.cs` | `FrameKind`/`AimUpKind`/`CameraModeKind`/`TargetRef` (the `vessel:`/`body:`/`part:` addressing, round-tripping exactly) and every validation predicate. `CameraRules` reads **no config** — FOV bounds are parameters — and re-runs game-side in the actuator, because `POST /v1/command` bypasses the 9p parse |
| The compositor | `Camera/CameraState.cs` | the 17 `CameraChannel`s, the mask, and `Track ?? Override ?? Baseline` per channel. `Compose` **allocates nothing** (asserted < 64 B over 10 000 calls) — it runs every rendered frame. Game-thread only, deliberately lock-free |
| Store + track dir | `Camera/CameraStore.cs`, `CameraDirectory.cs` | caps/versioning, the writable `track/` upload dir (create/write/clunk-commit, `rm` evicts), the volatile `CameraStatus` the director publishes, `LastError`, and the bounded `camera.shot`/`camera.finished` queue. Unlike audio clips, camera tracks are **not** `IsStreaming`, so they *are* in the scalar field mirror |
| Grammars + formatting | `Camera/CameraCommands.cs`, `CameraFormat.cs` | the 28 action keys and the six order-independent line grammars, plus the `/sim` text projection — every composite read-back **re-parses through its own grammar** (asserted), so "read a leaf, write it straight back" is a no-op |
| Tracks | `Camera/CameraTrack.cs`, `TrackParser.cs`, `TrackEvaluator.cs`, `CameraSample.cs`, `Playback.cs` | the JSON shot schema and its whole validation matrix, absolute-from-start evaluation (a full orbit closes **bit-identically**), `blend_in` cross-fades over only the channels both shots declare, and `CameraPlayback`/`CameraPlaybackController` — which register into the **schedules** registry as `kind = camera-track` (so `schedules/<id>/{pause,scrub,rate,loop,stop}` drive a take too) and act as the commit-time track validator |

Two consequences of that registration are worth stating here, because they are the feature's only
cross-gate coupling: `camera.play`/`set`/`stop` answer **`EOPNOTSUPP`** when
`[schedule] schedule_enabled = false` (there is nowhere to register a player), and because a
`CameraPlayback` never reports `failed`, `ScheduleStore.IsFinished` reduces to `State == Done` for it —
**a take in progress is structurally un-evictable under cap pressure**. Tests: twelve fixtures under
`gatOS.SimFs.Tests/Camera/` — `{CameraMathTests,EasingTests,SplinesTests,PoseSmootherTests}` for the
primitives and `{CameraRulesTests,CameraCommandsTests,CameraTreeTests,CameraStoreTests,CameraStateTests,
TrackParserTests,TrackEvaluatorTests,CameraPlaybackTests}` for the surface and the tracks. In-game pass
pending ([`../docs/VALIDATION.md`](../docs/VALIDATION.md)).

## HTTP `/v1` — `gatOS.Http`
`SimHttpServer.cs` (raw `TcpListener`, no HTTP lib), `HttpRequestLine.cs`, `OpenApi.cs`. Serves
`/v1/{snapshot,system,bodies,vessels/<id>[/telemetry|/stream|/events],status,command}` + the field-level
`/v1/fs/<path>` mirror (SSE via `?stream=1`). Projects the same `SimSnapshot`/`SimCommand`. Binds
`http_bind_host` / `http_preferred_port` (`127.0.0.1:4242` by default).

## MQTT — `gatOS.Mqtt`
`SimMqttBroker.cs` — embedded MQTTnet broker, bound through `mqtt_bind_host` / `mqtt_preferred_port`
(`127.0.0.1:1883` by default). Retained `gatos/{snapshot,system,bodies,time,status,events}`
+ `gatos/vessels/<id>/{telemetry,snapshot,stream}`; `gatos/command` in, `gatos/command/result` out;
`gatos/sim/<path>` field mirror. Changed-only publisher (the one eager pusher). Dep: MQTTnet.

## MCP — `gatOS.Mcp`
`SimMcpServer.cs`, `McpRegistry.cs`, `McpPresenters.cs`, and `McpToolHandlers.cs` implement a
stateless Streamable HTTP server at `/mcp` using the official C# SDK, bound through `mcp_bind_host` /
`mcp_preferred_port` (`127.0.0.1:4243` by default). Reads use only
`SnapshotStore` and existing game-free stores; writes compile through `CommandCatalog` into
`SimCommand`/`ICommandSink`, with shared atomic-batch and schedule builders. Resources and tools group
state by world, celestial, vessel, kitten, and runtime feature rather than mirroring VFS leaves.
There is no KSA reference or alternate actuator. `/sim/display` is the standing TTY-only exception
and is absent from the constructor, registry, and coverage contract. Dep: exact-pinned
`ModelContextProtocol.Core` 2.2.0. Public contract: [`SPEC_MCP.md`](../SPEC_MCP.md).

## Serial / bus — `gatOS.Bus`
`SerialBridge.cs` over QEMU virtio-serial (`gatos.serial`). Wire formats `SerialTelemetry.cs` (NDJSON),
`Nmea.cs`, `Ccsds.cs`; `ScpiCommandPort.cs` (SCPI → `SimCommand`); `SerialBridgeConnector.cs` (chardev
lifecycle, tied to the VM run). Cadence `serial_interval_ms`.

## Logging — `gatOS.Logging`
`ModLog.cs` (console-backed; `GameMod` swaps a game sink via `ModLog.SetLogger`), `PerfStat.cs`
(alloc-free timing accumulator for the status window). No game dependency by rule.

## Guest image — `guest/`
Alpine build/fetch pipeline (`build-image.sh`, `fetch-guest.{sh,ps1}`, `GUEST_VERSION`=17). Overlay
supervisors in `rootfs-overlay/sbin/` (`init-gatos`, `sim-mount`, `mnt-mount`, `qga-gatos`) read
`gatos.*port=` off the kernel cmdline; `usr/local/bin/tail` is the busybox-`tail -f` poll-mode shim that
makes `tail -f /sim/...` work over v9fs (guest v14 fix); `init-gatos` mounts cgroup2 with controllers
delegated so container runtimes (podman) run (guest v17). `manifest.toml` is the host boot contract. No
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
# Paint game-free surface

`gatOS.Paint` owns normalized sRGB validation/formatting, blend tokens, 7:7:7 encoding, precedence,
immutable `PaintSnapshot`, mutable game-thread `PaintStore`, and the pure idempotent GLSL transform.
`gatOS.SimFs` owns paths/parsers and canonical `paint.*` actions; HTTP/MQTT mirror its VFS and MCP
projects the same store logically. None references KSA/Brutal/StarMap.
