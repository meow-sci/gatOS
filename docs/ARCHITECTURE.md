# Runtime Architecture & Telemetry Tuning

High-level architecture decisions and tradeoffs are in `OS_ANALYSIS.md`. This file covers the
runtime shape of the running system and how to tune the telemetry pipeline.

---

## Runtime architecture

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
virtio-9p / virtiofs / vsock (none exist on Windows QEMU hosts). One transport, identical on
every host.

### Port allocation

`VmHost` allocates loopback ports via `PortAllocator` and injects them into the QEMU kernel
cmdline via `QemuCommandBuilder`:

| Port purpose | Cmdline param | Guest side |
|---|---|---|
| SSH (hostfwd to dropbear :22) | built into `-netdev` hostfwd | dropbear |
| 9P `/sim` server | `gatos.simport=<port>` | `sim-mount` supervisor |
| 9P `/mnt` server | `gatos.mntport=<port>` (0 = idle) | `mnt-mount` supervisor |
| HTTP `/v1` server | `gatos.httpport=<port>` | guest env `$GATOS_HTTP` |
| MQTT broker | `gatos.mqttport=<port>` | guest env `$GATOS_MQTT` |
| virtio-serial bridge | `gatos.serial` chardev (4th port) | `/dev/virtio-ports/gatos.serial` |

### Slirp networking

The guest network is 10.0.2.0/24 (QEMU's default slirp range). The guest reaches the host at
`10.0.2.2`; the host reaches the guest via hostfwd rules on 127.0.0.1. All TCP, no UDP. The
guest's `/etc/hosts` aliases `10.0.2.2` as `sim`, so scripts can use `$GATOS_HTTP` (e.g.
`http://sim:<port>`) without hardcoding IPs.

### Disk layout

`DiskManager` maintains versioned artifacts under the runtime data dir:

```
<GatOsPaths.DataDir>/disks/guest-v<N>/
    base-v<N>.qcow2          partitionless ext4, zstd-compressed base image
    vmlinuz-virt             kernel
    initramfs-virt           trimmed initramfs
    manifest.toml            host boot contract (cmdline template, ssh user/key, host-key pin)
    id_ed25519               SSH client key (committed static key, loopback-only)
    <profile>.qcow2          per-save overlay (backing ref = base-v<N>.qcow2, bare relative)
    <profile>.pid            PID lock file (stale reclaim on process-absent check)
```

The host-key pin is read from the *installed* `manifest.toml` (not the dist copy) so a
re-keyed rebuild of the same version can't cause a pin mismatch on an existing installation.

---

## Telemetry pipeline

```
KSA game thread (OnBeforeGui)
  └─ TelemetrySampler.SampleTelemetry()
       ├─ SampleClock: drift-free phase, drops long-frame backlog
       ├─ TelemetrySettings: reads volatile fields (rate, gates)
       ├─ VesselReader (core reads always)
       ├─ VesselReader.Enrich (guarded, only if vessel_detail gate is on)
       ├─ BodyReader (only if bodies gate is on)
       └─ SnapshotStore.Publish(snapshot)  ← single volatile reference swap

Consumers (any thread, lazy except MQTT):
  ├─ NinePServer sessions: read Current snapshot on demand
  ├─ SimHttpServer: read Current snapshot on demand, SSE on WaitForNextAsync
  ├─ SimMqttBroker: wakes on Publish, serializes, pushes retained topics
  └─ SerialBridge: wakes on Publish interval, serializes frame
```

### Cadence

`sample_rate_hz` (config, default 10; clamped 1–120) seeds `TelemetrySettings` (game-free,
`gatOS.SimFs`) whose volatile fields the game-thread sampler reads **every tick**. The gatOS
menu's *Telemetry* submenu (rate presets) and the status window (a 1–120 slider) write it live
and persist to `gatos.toml` (background `PersistConfig`, never on the render thread). The rate
drives `SampleClock.SetRate`. Everything downstream is a consumer of the published snapshot, so
this one knob sets the whole system's refresh rate.

### Per-stream gates

All default on; `telemetry_enabled` is the master gate:

| Config key | What it gates | Cost when off |
|---|---|---|
| `telemetry_vessel_detail` | G3 enrich pass (navball, environment, every per-module `StateList` read, `with`-clone alloc) | Drops all module-level events (flameout/dock/decouple) the differ can no longer see |
| `telemetry_bodies` | `BodyReader` celestial catalog reads | Drops `/sim/bodies/` + `system` |
| `telemetry_events` | `EventDiffer` + `EventsFile` | Drops `/sim/events` blocking reads |

Gating **at the sampler** is deliberate: a disabled stream skips its (often expensive) KSA reads
*and* shrinks the published snapshot, so every transport (9p/HTTP/MQTT/serial) serves less **by
construction** — no per-transport gate to keep in sync (transport-parity stays structural).

`telemetry_vessel_detail` is the big lever: off drops the whole G3 enrich pass, leaving only
core flight telemetry.

### Consumer cost

- **9p and HTTP are lazy**: a snapshot costs nothing until a guest reads.
- **MQTT is the one eager pusher**: gated on `ConnectedClients` + changed-only (byte-compare vs
  last payload — so static topics like `system`/`bodies` and a paused sim go quiet after one
  publish), serializes straight to UTF-8 (no intermediate string).
- **Serial**: driven by `serial_interval_ms` (default 500), not the main sample rate.
- **MQTT field mirror**: separate `field_feed_hz` cadence (default 4 Hz), also changed-only.

### Measuring live

The status window's **Telemetry block** shows `PerfStat` readouts for the game-thread costs:
- **Sample time** (avg/max/last) — the full `SampleTelemetry` call
- **Command drain** (avg/max) — per-frame + solver-phase `CommandQueue.Drain`
- **MQTT publish** (avg/max) — background serialization time while a client is connected

All recorded allocation-free (two `Stopwatch.GetTimestamp()` reads per interval). Use them to
see the cost of the current rate/stream config and confirm a toggle's effect. The **Reset**
button clears all accumulators.

---

## Config sections reference

| Section | Key knobs |
|---|---|
| `[common]` | `sample_rate_hz`, `disk_size_gb`, `cpu_model` |
| `[telemetry]` | `telemetry_enabled`, `telemetry_vessel_detail`, `telemetry_bodies`, `telemetry_events` |
| `[control]` | `control_enabled`, `control_all_vessels`, `debug_namespace`, `command_timeout_ms`, `max_commands_per_frame` |
| `[http]` | `enabled`, `preferred_port` (4242), `http_field_endpoints` |
| `[mqtt]` | `enabled`, `preferred_port` (1883), `mqtt_field_topics`, `field_feed_hz` |
| `[serial]` | `serial_telemetry_port`, `serial_command_port`, `serial_mode`, `serial_interval_ms` |
| `[[mounts]]` | `name`, `path`, `read_only` (array, off by default) |

Config is read from `<GatOsPaths.DataDir>/gatos.toml` (seeded on first run from
`<modDir>/gatos.default.toml`). Bad files fall back to in-memory defaults (never overwritten).
Written atomically (temp+rename). On Windows the install dir and data dir are the same folder —
see [ModDir == DataDir memory note](../memory/moddir-equals-datadir-windows.md) and the M6
description in [`docs/MILESTONES.md`](MILESTONES.md#m6).
