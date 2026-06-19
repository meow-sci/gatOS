# In-game validation record (OS_PLAN.md T6.6 / T6.7 / T9.3)

Manual pass results live here. Record machine, date, purrTTY/gatOS versions and the outcome of
each item; failures get a short note plus the relevant `logs/qemu-*.log` excerpt.

## Guest-v3 transport-env validation (run 2026-06-13, Windows 11 game machine, TCG)

Guest image **v3** built + fetched (`GUEST_VERSION=3`, host-key pin verified). The v3-only
guest activation for the extra transports (G5 HTTP + MQTT) is proven in-guest by the
`GATOS_IT=1` fixture `SimFs.Tests/Integration/TransportEnvIntegrationTests`, which boots the
real guest with the host HTTP server **and** MQTT broker wired
(`HttpPortProvider`/`MqttPortProvider`):

| Check | Result |
|---|---|
| `init-gatos` writes `/etc/profile.d/gatos.sh` exporting `$GATOS_HTTP = http://sim:<port>/v1` | ✅ |
| …and `$GATOS_MQTT = sim:<port>` | ✅ |
| `/run/gatos/{http,mqtt}-port` carry the bare ports for non-shell consumers | ✅ |
| The `sim` host alias resolves to the slirp gateway `10.0.2.2` (`/etc/hosts`) | ✅ |
| **Guest reads live telemetry over slirp**: `wget -qO- $GATOS_HTTP/time` → JSON with advancing `ut` | ✅ |
| The MQTT broker accepts the guest's TCP connection over the same slirp path (`nc sim <port>`) | ✅ |
| `/sim` 9p mount + control-surface writes still work on v3 (existing fixtures, re-run on v3) | ✅ |

### G7 serial bridge (added 2026-06-13, fixture `GuestSerialPort_StreamsTelemetry_AndAcceptsCommands`)

Boots the real v3 guest with `VmHostOptions.SerialEnabled = true` and a host `SerialBridge`
connected to the QEMU `gatos.serial` chardev:

| Check | Result |
|---|---|
| The VM exposes a `gatos.serial` virtio-serial chardev port (`VmStatus.SerialPort`) | ✅ |
| The guest device `/dev/virtio-ports/gatos.serial` appears unaided (init symlinks it) | ✅ |
| Guest reads an NDJSON telemetry frame off the device (`head -n1`) over the chardev | ✅ |
| `echo CTL:IGNITE >` the device actuates → `OK`; the command reaches the executor | ✅ |
| A bad command (`CTL:BOGUS`) → `ERR EINVAL` (no executor hit) | ✅ |

Full `GATOS_IT=1` suite re-run on v3: **green, 278/278, 0 skipped** (see CLAUDE.md). The only
remaining item is the **in-game pass** (the purrTTY tip release is now cut, so the
T6.6/T9.3/G1–G4 checklists below are runnable, but they need a live KSA flight).

## M9 headless dist smoke (pre-validation; run 2026-06-12, Windows 11 game machine)

The M6 reflection harness, extended for M9, against the deployed dist (real VM boot, TCG):

| Check | Result |
|---|---|
| `OnFullyLoaded` starts the 9p server (ephemeral loopback port) before any VM exists | ✅ |
| First session boot carries `gatos.simport=<port>`; the guest supervisor mounts `/sim` **unaided** (its connection appears during boot, before the prompt) | ✅ |
| `cat /sim/time/warp` through the real mount returns the published value | ✅ |
| `OnBeforeUi` ×5 headless: no throw escapes the hook (sampler runs or latches once) | ✅ |
| **Restart SimFs**: server bounced on the same port → guest re-establishes `/sim` by itself within seconds (`cat` works again, no manual umount) | ✅ |
| Unload: VM stop + 9p server stop, returned in 3.3 s | ✅ |

## Headless dist smoke (pre-validation; run 2026-06-12, Windows 11 game machine)

Before the first in-game pass, the deployed dist (`<MyDocuments>\My Games\Kitten Space
Agency\mods\gatOS`) was driven headlessly exactly as the game would — `Assembly.LoadFrom` of the
dist assemblies, lifecycle hooks invoked by reflection, the session opened through
`CustomShellRegistry` — proving everything below except the purrTTY/ModMenu UI layers:

| Check | Result |
|---|---|
| `OnFullyLoaded`: asset validation OK (guest v1, bundled QEMU), first-run `gatos.toml` created | ✅ |
| Game logging absent → caught, stays on console (init not aborted) | ✅ |
| Shell `gatos` registered; purrTTY absence detected and logged once | ✅ |
| `CreateShell` → `StartAsync` boots the VM from dist assets (base install + overlay on first boot) | ✅ |
| WHPX attempt failed (HypervisorPlatform disabled) → classifier retried with TCG automatically | ✅ |
| Echo round-trip; `stty size` = launch size (30 100); live resize → 40 120 | ✅ |
| Session stop leaves the VM **Running** | ✅ |
| `Unload()` → QGA guest-shutdown, returned in 2.2 s | ✅ |

## T6.6 — In-game validation pass #1 (the M6 exit) — **NOT YET RUN**

Prereq: a purrTTY install carrying the T5.1/T5.2 changes (the purrTTY tip release cut is still
pending — see CLAUDE.md M5 note).

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | KSA launches with purrTTY (≥ T5.x release) + gatOS installed | ☐ | |
| 2 | purrTTY New Tab → **gatOS** → tab opens, boot, motd + root prompt | ☐ | TCG on this machine: expect ~7–10 s first boot |
| 3 | `stty size` matches the window; resizing the window updates it | ☐ | |
| 4 | `apk add htop` (real slirp network) → `htop` draws; Ctrl-C works | ☐ | |
| 5 | Second concurrent gatOS tab; closing tabs leaves the VM up (status window) | ☐ | |
| 6 | Quit game → qemu process gone, disk lock released | ☐ | |
| 7 | gatOS **without** purrTTY → game loads clean, one log line, no crash | ☐ | |

## T6.7 — Windows validation pass — **WHPX VERIFIED HEADLESSLY (2026-06-13)**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | WHP feature enabled: boot under WHPX, record boot time + accel | ✅ | `HypervisorPlatform` enabled on the game machine; `VmHostIntegrationTests` (the real `VmHost` path) boots **`accel whpx`** end-to-end. Boot ≈ 1–2 s vs ≈ 7 s under TCG |
| 2 | WHP disabled: fallback lands on TCG, session usable, status window shows accel=tcg + DISM hint | ◐ | Fallback + usable session verified headlessly 2026-06-12 (WHPX "Unexpected VP exit code 4" → forced-tcg retry); the status-window hint itself needs the in-game pass |
| 3 | Full T6.6 checklist on Windows | ☐ | |

**Bug found & fixed during this pass (2026-06-13, i9-13900K / Raptor Lake).** Enabling WHPX exposed
that QEMU's WHPX backend triple-faults the guest under `-cpu host` **and** `-cpu max` — "WHPX:
Unexpected VP exit code 4" — so gatOS's classifier silently fell back to TCG even with WHPX
available. Empirically confirmed against the real guest: `host`, `host,-vmx`, `host,-apxf,-mpx`,
`max` all fault; every *named* model (`qemu64`, `Haswell`, `Skylake-Client`) boots fully to sshd.
Fix: `QemuCommandBuilder.ResolveCpuModel` now selects `-cpu` per accelerator — `host` on KVM/HVF, a
named model (default `Haswell`, AES-NI for fast in-guest SSH) on WHPX, `max` on TCG — overridable
via the `cpu_model` config. The APX/MPX CPUID-conflict warnings in stderr are a red herring (the
13900K has no APX); masking those bits does not fix the fault, only a named model does.

## T9.3 — In-game validation pass #2 (the M9 exit) — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). Run during a real flight with at least one vessel.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `ls /sim/vessels/by-id/` lists the loaded vessels | ☐ | |
| 2 | `watch -n1 cat /sim/vessels/active/altitude/radar` live during a flight; Ctrl-C clean | ☐ | |
| 3 | `tail -f /sim/vessels/active/stream \| jq .alt.radar` streams; Ctrl-C clean | ☐ | needs `apk add jq` |
| 4 | `cat /sim/events` during a launch shows situation changes; warp change → `warp-changed` | ☐ | |
| 5 | Time-warp changes `/sim/time/warp`; `/sim/time/ut` advances faster under warp | ☐ | |
| 6 | Status window: SimFs row shows port + 1 connection while the VM is up | ☐ | |
| 7 | Menu → Restart SimFs → guest re-establishes `/sim` within ~4 s (verified headlessly ✅) | ◐ | headless 2026-06-12: same-port rebind + unaided remount |
| 8 | Orbit dir appears for an orbiting vessel; apoapsis is an altitude (not a radius) | ☐ | |
| 9 | Battery/tanks/engines dirs match the vessel; values move during a burn | ☐ | |

## G1 — Control-surface validation pass — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). Run during a real flight with a vessel that has at
least one engine and one deployable solar panel. `[control] enabled = true` (default). See
`docs/KSA_INTEGRATION_MATRIX.md` for the full path/anchor list.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `echo 1 > /sim/vessels/active/ctl/ignite` ignites the active stage (exit 0) | ☐ | |
| 2 | `echo 1 > /sim/vessels/active/ctl/shutdown` shuts engines down | ☐ | |
| 3 | `echo 0 > /sim/vessels/active/engines/0/active` toggles one engine; read-back reflects it | ☐ | |
| 4 | `echo 1 > /sim/vessels/active/ctl/lights` / `echo 0 …` toggles vessel lights | ☐ | |
| 5 | `echo 1 > /sim/vessels/active/solar/0/goal` deploys a panel; `0` retracts it | ☐ | |
| 6 | `echo 0.5 > /sim/vessels/active/animations/0/goal` drives an animation to mid-travel | ☐ | |
| 7 | `echo bogus > …/engines/0/active` fails with EINVAL (nonzero exit, "Invalid argument") | ☐ | verified via `GATOS_IT` fixture ✅ |
| 8 | `[control] enabled=false` → every write fails EACCES | ☐ | |
| 9 | `cat /sim/status/transports` shows the 9p port + `control on`; `/sim/status/game_version` non-empty | ☐ | |
| 10 | A deliberately broken accessor surfaces in `/sim/status/accessors` and the rest keeps working | ☐ | health-latch path |

## G3 / G4 — read-surface & full-control validation pass — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). Read surface verified over the managed 9p client
(`SimFsTreeTests`, `EventDifferTests`, `FormatsTests`) and the control surface over the client
(`ControlSurfaceTests`); these are the in-guest spot-checks. `[control] debug_namespace = true`
(default) for the debug rows. See `docs/KSA_INTEGRATION_MATRIX.md` for the full path/anchor list.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `cat /sim/system/sun` names the star; `ls /sim/bodies` lists planets/moons + the star | ☐ | |
| 2 | `cat /sim/bodies/<planet>/{radius,mu,soi}` and `…/atmosphere/sea_level_pressure` are sane | ☐ | |
| 3 | `cat /sim/vessels/active/telemetry \| jq .orbit.ap` returns the apoapsis (one atomic read) | ☐ | |
| 4 | `cat /sim/vessels/active/navball/{pitch,twr,deltav}` track the in-game NavBall | ☐ | |
| 5 | `cat /sim/vessels/active/environment/{pressure,density,g_force}` move during ascent/reentry | ☐ | |
| 6 | `echo TARGET > /sim/time/alarm; cat /sim/time/alarm` blocks until sim time reaches TARGET | ☐ | verified over 9p client ✅ |
| 7 | `echo 0.5 > …/ctl/throttle` sets throttle; NavBall/engines reflect it near 1× warp | ☐ | reflection path (High churn) |
| 8 | `echo 1 > …/ctl/stage` activates the next stage | ☐ | |
| 9 | `echo prograde > …/ctl/attitude_mode` then the autopilot holds prograde; `manual` releases | ☐ | |
| 10 | `echo "0 0 0 1" > …/ctl/attitude_target` / `echo "<ut> <dvx> <dvy> <dvz>" > …/ctl/burn` set FC targets | ☐ | |
| 11 | `echo 1 > …/ctl/rcs` and `…/rcs/<n>/active` toggle RCS | ☐ | |
| 12 | `echo "1 0 0" > …/lights/0/color` recolours only that light (per-instance clone) | ☐ | High churn |
| 13 | `echo 1 > …/decouplers/0/fire` fires once; a second write returns EBUSY | ☐ | |
| 14 | `echo 50 > /sim/debug/time/warp` sets warp; `echo <id> > /sim/debug/focus` moves the camera (vehicle/body); `echo <vid> > /sim/debug/control_vessel` focuses+controls | ☐ | |
| 15 | `echo 1 > /sim/debug/vessels/<id>/refill_battery` tops the battery (solver-phase drain) | ☐ | |
| 16 | `echo "<px py pz vx vy vz>" > /sim/debug/vessels/<id>/teleport` moves the vessel; no NaN glitch | ☐ | |
| 17 | `[control] debug_namespace=false` → `/sim/debug` is absent | ☐ | verified over 9p client ✅ |

## Screen stream (`/sim/display`) — in-game validation pass — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). Run during a flight (or any in-3D-view moment). The
capture is render-thread Vulkan code that cannot be exercised headlessly — the game-free half
(encoder, stream, controls) is covered by `gatOS.SimFs.Tests/Display/**`. See
[`STREAM_PLAN.md`](../STREAM_PLAN.md) and `docs/KSA_INTEGRATION_MATRIX.md` (the `FrameCapture` anchor).

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `echo 1 > /sim/display/enabled` then `cat /sim/display/stream` in an in-game purrTTY tab shows the live game view | ☐ | |
| 2 | The same from an external kitty-capable terminal SSH'd into the guest renders identically | ☐ | reachability: hostfwd SSH port on 127.0.0.1 |
| 3 | `echo 5 > /sim/display/fps` / `echo 30 …` visibly changes the refresh rate; game fps is unaffected at low rates | ☐ | |
| 4 | `echo 640 > /sim/display/width; echo 360 > /sim/display/height` resizes the image live | ☐ | |
| 5 | Colour fidelity vs the on-screen frame (pre-tonemap, no UI): acceptable, or switch source later if not | ☐ | the offscreen target is pre-tonemap; bright areas may clamp |
| 6 | `echo 0 > /sim/display/enabled` stops the stream; the status window Display line shows "off" and capture cost returns to 0 | ☐ | |
| 7 | Status window Display line shows capture/encode ms while streaming; no game-fps hitch at 10–15 fps | ☐ | synchronous readback (S7 deferred readback is the no-stall follow-up) |
| 8 | Two readers at once (purrTTY tab + external terminal) both render; closing one leaves the other streaming | ☐ | multi-reader fan-out |
| 9 | `cat /sim/display/format` reports the live `WxH@fps enc`; `POST /v1/fs/display/enabled` (HTTP) toggles it too | ☐ | transport parity for the controls |
