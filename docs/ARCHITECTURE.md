# Runtime Architecture & Telemetry Tuning

High-level architecture decisions and tradeoffs are in `OS_ANALYSIS.md`. This file covers the
runtime shape of the running system and how to tune the telemetry pipeline.

---

## Runtime architecture

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
virtio-9p / virtiofs / vsock (none exist on Windows QEMU hosts). One transport, identical on
every host.

### Port allocation

`VmHost` allocates loopback ports via `PortAllocator` and injects them into the QEMU kernel
cmdline via `QemuCommandBuilder`:

| Port purpose | Cmdline param | Guest side |
|---|---|---|
| SSH (hostfwd to sshd :22) | built into `-netdev` hostfwd | OpenSSH `sshd` |
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
<GatOsPaths.DataDir>/disks/
    base-v<N>.qcow2          partitionless ext4, zstd-compressed base images, one per installed version
    guest-v<N>/              per-version boot artifacts (never deleted by a newer install):
        vmlinuz-virt           kernel
        initramfs-virt         trimmed initramfs
        manifest.toml          host boot contract (cmdline template, ssh user/key, host-key pin)
        id_ed25519             SSH client key (committed static key, loopback-only)
    <profile>.qcow2          per-save overlay (backing ref = base-v<N>.qcow2, bare relative)
    <profile>.toml           sidecar recording the guest version the overlay was created on
    <profile>.lock           lock file (stale reclaim on process-absent check)
```

**Version-pinned boots.** A profile always boots the kernel/initrd/manifest of the guest version
its overlay was created on (`GuestBoot`): the sidecar's `guest_version` selects `guest-v<N>/`, so a
guest upgrade shipping with the mod never mixes a new kernel with an old rootfs — a mixed boot
leaves the kernel without its `/lib/modules` tree, `modprobe 9p` fails in the guest, and `/sim`
silently never mounts (while SSH, served by the initramfs's virtio, keeps working). An
older-version profile keeps working and logs a WARN suggesting **Reset Disk** (which recreates the
overlay on the current base = the guest upgrade, wiping in-guest data). If an overlay's guest
version is no longer installed at all, the overlay is set aside as
`<profile>.orphaned-v<N>.qcow2` and a fresh current-version disk is created.

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
       ├─ PartsReader (per vehicle, only if vessel_parts gate is on; cached per vehicle)
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
| `telemetry_vessel_detail` | G3 detail pass (navball, environment, every per-module `StateList` read — sampled in the same single `BuildFull` pass as the core since GP3) | Drops all module-level events (flameout/dock/decouple) the differ can no longer see |
| `telemetry_vessel_parts` | `PartsReader` per-vessel parts list — top-level parts + nested `subparts/<m>/` + the `parts/json` whole-tree doc (the welds anchor picker); cached per vehicle, rebuilt on `Vehicle.Parts.Count` change or every 10 s (`parts/json` re-serializes only on rebuild) | Drops `/sim/vessels/<id>/parts/` |
| `telemetry_bodies` | `BodyReader` celestial catalog reads (statics cached per body since GP3 — per tick only positions/velocities are read) | Drops `/sim/bodies/` + `system` |
| `telemetry_bodies_rate_hz` | Bodies resample cadence (0 = every tick). Below the master rate, in-between ticks re-publish the **same** bodies/system objects by reference — no KSA reads, no allocation, and consumers can reference-compare for "unchanged" | n/a (a cadence, not a gate) |
| `telemetry_events` | `EventDiffer` + `EventsFile` (dictionary-free positional diff since GP3 — allocation-free when nothing changed) | Drops `/sim/events` blocking reads |

Gating **at the sampler** is deliberate: a disabled stream skips its (often expensive) KSA reads
*and* shrinks the published snapshot, so every transport (9p/HTTP/MQTT/serial) serves less **by
construction** — no per-transport gate to keep in sync (transport-parity stays structural).

`telemetry_vessel_detail` is the big lever: off drops the whole G3 enrich pass, leaving only
core flight telemetry.

### Consumer cost

- **9p and HTTP are lazy**: a snapshot costs nothing until a guest reads (and since GP1, reads hit
  per-snapshot memoized formatting — N readers of one value = one format).
- **MQTT is the one eager pusher**, now triply gated (GP2): on `ConnectedClients`, on **live
  subscription filters** (a topic nobody subscribed to is neither serialized nor injected; a new
  subscription forces its retained baseline within one cycle), and changed-only (reference-identity
  for `system`/`bodies` — the sampler re-publishes unchanged ones by reference — byte-compare for
  the rest). `mqtt_publish_hz` (default 0 = every snapshot) additionally caps the world-topic
  cadence. Vanished vessels' retained topics are tombstoned.
- **Serial**: driven by `serial_interval_ms` (default 500), not the main sample rate.
- **MQTT field mirror**: separate `field_feed_hz` cadence (default 4 Hz), also changed-only and
  subscription-gated per topic.

### Measuring live

The status window's **Telemetry block** shows `PerfStat` readouts for the game-thread costs:
- **Sample time** (avg/max/last) — the full `SampleTelemetry` call
- **Sample alloc** (avg/max/last, KiB) — bytes allocated on the game thread by one sample
  (`GC.GetAllocatedBytesForCurrentThread` delta; the GP3 regression tripwire)
- **Command drain** (avg/max) — per-frame + solver-phase `CommandQueue.Drain`
- **MQTT publish** (avg/max) — background serialization time while a client is connected

All recorded allocation-free (two `Stopwatch.GetTimestamp()` reads per interval; one thread-local
counter read per sample for the alloc figure). Use them to see the cost of the current rate/stream
config and confirm a toggle's effect. The **Reset** button clears all accumulators.

---

## Screen stream (`/sim/display`)

A downscaled, frame-rate-limited render of the KSA viewport exposed as the Kitty terminal graphics
protocol, so any SSH client whose terminal supports Kitty (an in-game purrTTY tab or an external
emulator) can display it. Full design + milestones in [`STREAM_PLAN.md`](../STREAM_PLAN.md).

```
render thread (GameMod/Game/Ksa)                          background (gatOS.SimFs/Display)
  DisplayRenderPatch: Harmony transpiler on Program.RenderGame injects a call just before the
    frame's commandBuffer.End() (offscreen ColorImage is ShaderReadOnlyOptimal, outside a render pass)
  FrameCapture.MaybeRecord(program, cb, surface)  (throttled to fps; only if enabled & a reader open)
    Program.MainViewport.OffscreenTarget.ColorImage   (public, post-resolve, no UI)
    records into the engine's OWN command buffer (no out-of-band submit, no WaitIdle):
      barrier offscreen → TransferSrc (engine's own sync2 TransitionImages2 presets)
      CopyImageToBuffer → FULL-frame host-visible staging buffer in a ResourceFrameIndex ring slot
      barrier the offscreen back to ShaderReadOnlyOptimal (restore)
    deferred readback: next visit to a slot reads its persistent mapping (its copy is complete by the
      frames-in-flight contract — no fence wait), nearest-neighbour downscales + converts the HDR
      half-float pixels → BGRA8 on the CPU, SubmitFrame(BGRA)
                                                  │
                                  DisplaySurface encode worker: swizzle + zlib + Kitty APC framing
                                                  │ latest-frame feed (drop-old, mirrors SnapshotStore)
  /sim/display/stream  (DisplayStreamFile, IsStreaming, multi-reader fan-out)
    guest:  echo 1 > /sim/display/enabled ; cat /sim/display/stream   → SSH PTY → terminal renders
```

- **Capture** rides the engine's own per-frame command buffer (Vulkan; rule 1) — no private queue submit
  and no `Device.WaitIdle` (doing GPU work out-of-band alongside the in-flight frames crashed the game; this
  is the engine authors' prescribed approach, mirroring KSA's PlanetMapExporter/OceanFFT readbacks). It is
  throttled to `display_fps` and gated so it idles for free unless enabled **and** a reader has the stream
  open. The copy-to-host result is read back a few frames later, when the frames-in-flight slot is reused and
  its fence has already been waited by the engine — so there is no stall. A fault disables it for the session.
- **Encode** (BGRA→RGBA swizzle, zlib, base64, Kitty framing) runs on a worker, never the render thread.
- **Controls** are `/sim/display/{enabled,fps,width,height,encoding}` — ordinary writable scalar files,
  so they mirror to HTTP `/v1/fs/display/*` and MQTT `gatos/sim/display/*` by construction. The binary
  `stream` is `IsStreaming` and excluded from the field mirror (consume it over 9p).
- The status window's **Display** line shows the live state + capture/encode `PerfStat`.

---

## Game-thread cheats (welds + IVA render + thug_life + per-vessel scale/always_render)

The cheats ported from the sibling `unscience` mod are exposed **only** on gatOS surfaces (9p `/sim`
+ HTTP `/v1` + MQTT — no ImGui), all mutating game state on the game thread:

- **Welds** (`gatOS.GameMod/Game/Ksa/Welds/`): a registry (`WeldManager`) whose per-frame driver
  teleports each welded source vessel onto its target/part anchor. It runs in `OnAfterUi`
  (`Mod.DriveWelds`, `[StarMapAfterGui]`) **after** the vehicle-solver workers (it calls
  `JobSystems.VehicleSolvers.Wait()` first) — a **third game-thread mutation site** beside the
  Frame-phase command drain (`OnBeforeUi`) and the Solver-phase prefix on
  `Universe.ExecuteNextVehicleSolvers`. It self-gates to a no-op when no welds exist, so it adds zero
  per-frame cost when unused and needs **no** Harmony patch.
- **`always_render_iva`** (`Game/Ksa/Render/IvaForceRender.cs`): forces interior (IVA) part meshes to
  render outside the IVA camera. It installs **two Harmony patches on its own dynamic
  `Harmony("gatos.iva")` instance only while enabled** (a `PartModel` ctor postfix + an editor-only
  `AddInstance` postfix) and bulk-flips the internal-template flag over `PartModel.Instances`; disabling
  restores the templates and unpatches. Default-off ⇒ zero patches.
- **`thug_life`** (`Game/Ksa/ThugLife/`): gatOS's **first custom GPU rendering** — anchors a flat,
  world-space textured quad (the "thug life" sunglasses meme) to a part on a vehicle, tracked each frame.
  This is a **render-thread draw injection**: `ThugLifeRenderPatches` installs a dynamic
  `Harmony("gatos.thug_life")` **postfix on `SuperMeshRenderSystem.RenderMainPass(CommandBuffer)`** (the
  one injection point for a world-space draw). The Vulkan pipeline/texture/buffers (`ThugLifeQuadRenderer`
  + `ThugLifeTextureFactory`, via `Program.GetRenderer()`) and the patch install **lazily on the first
  entry** and tear down with the last entry / at unload, so default-off ⇒ **zero patches and zero GPU
  resources**. KSA runs `RenderMainPass` on the **main thread** (same as the GUI hooks + command drain),
  so the render postfix, the command drain, and entry edits are all one thread — no cross-thread
  game-state access. This adds a **fourth game-thread work site**: `UpdateThugLife()` in `OnBeforeUi`
  (`[StarMapBeforeGui]`) revalidates / re-resolves each entry's anchor part per frame (a staged anchor part
  falls back to the vehicle body frame rather than dropping). The manager publishes an immutable
  `ThugLifeEntry[]` (swapped on add/remove) that the postfix reads, and self-disables (`Active=false`) on
  any GPU fault. Teardown dispose order: clear `Active` → unpatch → dispose GPU (safe because same-thread).
- **Per-vessel `scale` + `always_render`** (`Game/Ksa/Actuators/ScaleActuator.cs` +
  `Game/Ksa/Render/VesselForceRender.cs`): **first-class vessel nodes** under
  `vessels/by-id/<id>/`, deliberately outside `/sim/debug` and exempt from the active-vessel authority
  gate (`KsaCatalog.AnyVesselActions`). `scale` is a **one-shot** recursive `Part.Scale` write (no
  driver, no patch — KSA keeps it until it rebuilds the vessel). `always_render` bypasses KSA's
  sub-pixel cull (a vehicle whose projected diameter is < 1 px is normally not drawn) via **two
  Harmony prefixes on its own dynamic `Harmony("gatos.always_render")` instance —
  `Vehicle.GetWorldMatrix` + `Vehicle.UpdateRenderData` — installed only while ≥ 1 vessel is marked**
  and removed on the last unmark/despawn/unload. The id-keyed registry is mutated only on the game
  thread; the prefixes read a volatile immutable set (two hash lookups per vehicle per frame while
  installed); despawn pruning (`VesselForceRender.Prune`) rides the sampler's vehicle enumeration.

All create/remove via Frame-phase commands and tear down on unload
(`Mod.TeardownGameCheats`). The anchor picker for welds **and `thug_life`** is the per-vessel `parts/` list
(`telemetry_vessel_parts`; `thug_life` also accepts `0` = the vehicle body frame).

---

## Audio playback (`/sim/audio`)

Userland audio through the game's FMOD mixer (GATOS_CUSTOM_AUDIO_PLAN): the game-free
`AudioStore` (`gatOS.SimFs/Audio/`) holds uploaded clip bytes in memory (per-clip / total / count
caps) and the channel-status snapshot; the game-side `AudioActuator`
(`Game/Ksa/Actuators/AudioActuator.cs`) executes `audio.play/set/stop` against the public
`GameAudio.System` in the Frame-phase drain and ticks per frame (**a fifth game-thread work
site**: `Mod.DriveAudio` in `OnBeforeUi`, right after the drain — the same thread that pumps
`System.Update()`), pruning finished channels, enforcing `end=`, releasing evicted FMOD sounds
deferred, publishing `/sim/audio/status`, and queueing `audio.finished` events that the sampler
folds into the next snapshot. Uploads run entirely on transport threads (9p writable dir /
`PUT /v1/audio/file/<name>`) — the store is the only shared object, and FMOD copies clip bytes at
create, so eviction/re-upload never disturbs playback. Self-gates to a no-op while no channel or
cached sound exists; torn down by `Mod.TeardownGameCheats`. `audio_enabled=false` removes the
whole surface.

---

## Config sections reference

| Section | Key knobs |
|---|---|
| `[common]` | `sample_rate_hz`, `disk_size_gb`, `cpu_model` |
| `[telemetry]` | `telemetry_enabled`, `telemetry_vessel_detail`, `telemetry_vessel_parts`, `telemetry_bodies`, `telemetry_bodies_rate_hz`, `telemetry_events` |
| `[control]` | `control_enabled`, `control_all_vessels`, `debug_namespace`, `command_timeout_ms`, `max_commands_per_frame` |
| `[http]` | `enabled`, `preferred_port` (4242), `http_field_endpoints` |
| `[mqtt]` | `enabled`, `preferred_port` (1883), `mqtt_field_topics`, `field_feed_hz`, `mqtt_publish_hz` |
| `[serial]` | `serial_telemetry_port`, `serial_command_port`, `serial_mode`, `serial_interval_ms` |
| `[display]` | `display_enabled` (off), `display_fps`, `display_width`, `display_height`, `display_encoding` (boot seeds for `/sim/display`) |
| `[audio]` | `audio_enabled` (on), `audio_max_clip_bytes` (16 MiB), `audio_max_total_bytes` (64 MiB), `audio_max_clips` (64), `audio_max_channels` (16) |
| `[[mounts]]` | `name`, `path`, `read_only` (array, off by default) |

Config is read from `<GatOsPaths.DataDir>/gatos.toml` (seeded on first run from
`<modDir>/gatos.default.toml`). Bad files fall back to in-memory defaults (never overwritten).
Written atomically (temp+rename). On Windows the install dir and data dir are the same folder —
see [ModDir == DataDir memory note](../memory/moddir-equals-datadir-windows.md) and the M6
description in [`docs/MILESTONES.md`](MILESTONES.md#m6).
