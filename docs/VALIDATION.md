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
| 18 | `cat …/docking/<n>/pushoff_impulse` reads N·s (stock 7000); `echo 1 > …/docking/<n>/undock` separates a docked port; `echo <ns> > /sim/debug/vessels/<id>/docking/<n>/pushoff_impulse` changes the separation energy | ☐ | **4750 re-check:** rev 4683 renamed `PushoffForce`→`PushoffImpulse` (N→N·s) and the `/sim` leaf `pushoff_force`→`pushoff_impulse`; see `plans/FIX_CURRENT_GAPS_PLAN.md` G1 |
| 19 | `cat …/power/{produced,consumed}` and `…/{solar,generators}/<n>/produced` read a **stable instantaneous wattage** (not a tiny per-frame number) that tracks the XML-authored panel/generator W under load | ☐ | **4750 re-check (G2):** rev 4681 retyped `Joules`→`Watts`; values now instantaneous W, magnitudes differ from the 4680 era; see `plans/FIX_CURRENT_GAPS_PLAN.md` G2 |
| 20 | `cat …/vessels/active/controllable` reads `1`; on a debris/uncontrollable vessel (no Control Module) `…/vessels/by-id/<id>/controllable` reads `0`. Confirm flight-control writes to that uncontrollable vessel no-op (gatOS returns `ok`; KSA's lockout drops them) — the documented Option-A behavior | ☐ | **4750 re-check (G3):** rev 4699 `Vehicle.IsControllable`; decide live whether silent-`Ok` warrants Option B (`EACCES` gating); see `plans/FIX_CURRENT_GAPS_PLAN.md` G3 |
| 21 | After loading 4750: `cat /sim/status/accessors` is clean (no degraded latches) through a throttle write (`ctl/throttle`, reflection field `_manualControlInputs.EngineThrottle`), a solver-phase FC setpoint (`ctl/attitude_mode`, Harmony `Universe.ExecuteNextVehicleSolvers` prefix), and the gatOS menu drawing (Harmony `Program.DrawProgramMenusHook`) | ☐ | **4750 re-check (G4.3):** reflection + Harmony targets can't be build-checked; confirmed present in decomp (`Vehicle.cs:232/526`), live-verify the latches stay clear; see `scope/ksa-runtime-coupling.md` |

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

## Welds / `always_render_iva` / parts — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). `[control] debug_namespace = true` and
`telemetry_vessel_parts = true` (both default). Run during a real flight with **two vessels close
together** (weld one onto the other) and a crewed capsule (an IVA). These surfaces are gatOS-only (no
ImGui) — drive them over `/sim` (or HTTP/MQTT). See `SPEC_9P_FILESYSTEM.md` §3.4.16 (parts) + §3.7
(`debug/welds/**`, `always_render_iva`) and `docs/KSA_INTEGRATION_MATRIX.md`.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `echo 1 > /sim/debug/always_render_iva` makes interior (IVA) meshes visible from the external camera; `echo 0 …` hides them again | ☐ | global render cheat |
| 2 | With the cheat **off**, no `gatos.iva` Harmony patches exist (reads `0` at start; first enable logs "patches installed", disable logs "patches removed") | ☐ | dynamic patch lifecycle |
| 3 | Toggle repeatedly → no residue (interiors hidden after the final `0`); quitting with it **on** restores templates + unpatches cleanly at unload | ☐ | `TeardownGameCheats` |
| 4 | `ls /sim/vessels/active/parts/` lists the **top-level** parts (no subparts); `cat parts/0/{instance_id,template,is_root,position}` are sane | ☐ | `telemetry_vessel_parts` |
| 5 | Stage/decouple or edit the active vessel → `parts/` updates within a sample (count-change invalidation); a count-preserving edit updates within 10 s | ☐ | per-vehicle cache invalidation |
| 6 | `telemetry_vessel_parts=false` (the "Vessel parts" telemetry menu toggle or config) → `/sim/vessels/<id>/parts/` is gone | ☐ | gate |
| 7 | Pick an anchor `<piid>` from the target's `parts/<n>/instance_id`; `echo "<target> <piid>" > /sim/debug/vessels/<source>/weld_here` welds the source at its current pose (it stays put relative to the target) | ☐ | `weld_here` capture |
| 8 | The welded source tracks the target **rigidly** through translation, rotation, and **time-warp** (offset/orientation preserved); `cat /sim/debug/welds/count` ≥1 and `/sim/debug/welds/<source>/{target,part,offset,rotation,lock_rotation}` reflect it | ☐ | per-frame driver after `VehicleSolvers.Wait()` |
| 9 | `echo 0 > /sim/debug/welds/<source>/enabled` suspends tracking (entry kept; source free); `echo 1 …` resumes it | ☐ | suspend/resume |
| 10 | Staging an **unrelated** part on the target (anchor part survives) does **not** drop the weld; removing the anchor part itself falls back to body-frame anchoring (still not dropped) | ☐ | anchor re-resolution each tick |
| 11 | `echo 1 > /sim/debug/vessels/<source>/unweld` removes that weld; `echo 1 > /sim/debug/welds/clear` removes all (count → 0) | ☐ | remove / clear |
| 12 | Weld a vessel to itself, or to one orbiting a different body → `EBUSY`; bad `<piid>`/target → `ENOENT`; bad arity/values → `EINVAL` | ☐ | errnos |
| 13 | With **no** welds active, the `OnAfterUi` driver is a no-op — no measurable per-frame cost, no `VehicleSolvers.Wait()` | ☐ | `WeldManager.IsEmpty` early-out |
| 14 | Quit with welds active → clean unload (welds cleared, no exception); reload shows welds are **not** persisted | ☐ | runtime-only; `TeardownGameCheats` |

## thug_life (world-space quad render cheat) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). `[control] debug_namespace = true` and
`telemetry_vessel_parts = true` (both default). Run during a real flight with at least one vessel
(ideally **several**). gatOS's **first custom GPU rendering** — drive it over `/sim` (or HTTP/MQTT); no
ImGui. See `SPEC_9P_FILESYSTEM.md` §3.7 (`debug/thug_life/**`), `docs/KSA_INTEGRATION_MATRIX.md` (render
set), and the ksa skill `quad.md`. **All items pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | Pick a `<piid>` from `…/parts/<n>/instance_id`; `echo "<vessel> <piid>" > /sim/debug/thug_life/add` → the sunglasses quad appears on that part; `cat /sim/debug/thug_life/count` ≥1 | ☐ | first entry installs the patch + GPU lazily |
| 2 | The quad is **correctly oriented** and **depth-tested** — it is occluded by geometry in front of it (NOT painted on top of everything) | ☐ | verifies the `Program.OffScreenPass` pass + **reverse-Z** depth |
| 3 | Tune `position`/`rotation`/`size` live (`echo "x y z" > …/<id>/position`, etc.) → the quad moves/rotates/resizes immediately; `echo 0 > …/<id>/visible` hides it, `1` shows it | ☐ | per-entry STATE writes (id in `ordinal`) |
| 4 | Multiple entries on **several vessels** all track their anchors **rigidly** through translation, rotation, **time-warp**, and **camera changes** (zoom/focus switch) | ☐ | per-frame anchor math on the main thread |
| 5 | Stage/decouple an **unrelated** part on an anchor vessel → the entry **survives**; stage/remove the **anchor part itself** → the quad falls back to the **vehicle body frame** (no crash, no drop) | ☐ | `UpdateThugLife` re-resolution each frame |
| 6 | Force MSAA **4×** and **8×** → no depth/edge artifacts on the quad | ☐ | `Program.OffScreenPass.SampleCount` must match the scene |
| 7 | `echo 1 > …/<id>/remove` removes one; `echo 1 > /sim/debug/thug_life/clear` removes all → quads vanish, the render postfix is **removed** and GPU resources **freed** (no per-frame cost when empty) | ☐ | lazy teardown on the last entry |
| 8 | Repeated add → clear → add cycles → no leak, no double-patch, no Vulkan validation spew; the quad still renders correctly after several cycles | ☐ | dynamic `gatos.thug_life` patch lifecycle |
| 9 | Quit with entries active → **clean Unload** (no Vulkan validation errors / no exception); reload shows entries are **not** persisted | ☐ | runtime-only; `TeardownGameCheats` dispose order: clear `Active` → unpatch → dispose GPU |
| 10 | Induce a GPU fault (e.g. an unavailable renderer) → the feature **self-disables** (`Active=false`), logs once, and the rest of gatOS keeps working | ☐ | `EIO` on `add` when the renderer is unavailable |

## Per-vessel `scale` + `always_render` nodes — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[control] control_enabled = true` (default); works with
`control_all_vessels = false` too — both actions are authority-exempt (`KsaCatalog.AnyVesselActions`).
Run during a real flight with at least two vessels, one far away. See `SPEC_9P_FILESYSTEM.md` §3.4.1
and `docs/KSA_INTEGRATION_MATRIX.md` (per-vessel nodes). **All items pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `echo 2 > /sim/vessels/by-id/<id>/scale` doubles the model; `echo 50000 >` gives planet-size; `echo 1 >` restores 1:1 | ☐ | one-shot recursive `Part.Scale` |
| 2 | `echo 0`, `echo -1`, `echo abc` into `scale` each fail with `EINVAL`; `cat scale` reflects the current factor | ☐ | `ScaleRules` + parse-level rejection |
| 3 | Scaling a **non-active** vessel by id works even with `control_all_vessels = false` | ☐ | authority exemption |
| 4 | A KittenEva (EVA kitten) scales via the avatar path (`Core.Scale`) | ☐ | reflection special-case |
| 5 | Scene reload / staging / undock reverts `scale` to 1 and the read-back honestly shows it | ☐ | accepted D1 limitation |
| 6 | Fly (or warp) away from a vessel until it disappears (< 1 px); `echo 1 > /sim/vessels/by-id/<id>/always_render` makes it visible again and it **stays** rendered at any distance; `echo 0 >` restores the stock cull (it vanishes again) | ☐ | first mark installs the `gatos.always_render` prefixes |
| 7 | `cat always_render` reads back `1` while marked, `0` after; marking a **non-active** vessel works with `control_all_vessels = false` | ☐ | read-back + authority exemption |
| 8 | The mark **survives a scene rebuild** (staging/undock — same vessel id); despawning the vessel (recover/destroy) drops the mark automatically (`cat` of a re-spawned same-id vessel reads `0`… unless it truly is the same id, in which case still marked — verify the prune only fires on despawn) | ☐ | id-keyed registry + sampler prune |
| 9 | With **no** vessel marked, no `gatos.always_render` patches are installed (repeated mark/unmark cycles → no double-patch, no leak); quit with marks active → clean unload | ☐ | dynamic patch lifecycle; `TeardownGameCheats` |
| 10 | An EVA kitten marked `always_render` is **not** force-rendered (documented limitation — its `UpdateRenderData` override bypasses the patched base) | ☐ | virtual-method limitation |

## `ctl/translate` (manual RCS translation) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[control] control_enabled = true` (default). Best exercised on an **EVA
kitten** (its backpack carries the six translation-mapped jets); a normal rocket with attitude-only
RCS accepts the write but fires nothing. The game-free half (parse, EINVAL, command shape/phase,
read-back rendering) is covered by `gatOS.SimFs.Tests/Commands/VesselTranslateTests.cs`; these items
exercise the reflection + flags path (`TranslateActuator`). See `SPEC_9P_FILESYSTEM.md` §3.4.17 and
`docs/KSA_INTEGRATION_MATRIX.md` (control surface). **All items pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | On a floating EVA kitten: `echo "1 0 0" > ctl/translate` → it accelerates **along its nose**; `echo "0 0 0"` stops the jets | ☐ | sign mapping +x = `TranslateForward` (nozzle geometry) |
| 2 | `echo "0 1 0"` → moves to its **right**; `echo "0 0 1"` → moves **down** (body frame is X-nose/Y-right/Z-down) | ☐ | +y = `Right`, +z = `Down` |
| 3 | The command **latches**: jets keep firing across many seconds without re-writing; `cat ctl/translate` reads back the latched signs; program exit without `0 0 0` leaves them firing (documented) | ☐ | held-key semantics |
| 4 | With an active attitude hold (`ctl/attitude_target` or a named mode), translation fires **while** the hold keeps steering (no fight, no mode drop) | ☐ | Auto attitude strips only rotation bits |
| 5 | `echo 0 > ctl/rcs` (master off) silences translation too; `1` restores | ☐ | `ThrusterController.IsActive` gate |
| 6 | In-game keyboard translate keys and the file compose sanely (last writer wins on the translate bits; keyboard rotation unaffected) | ☐ | rotation bits preserved on file writes |
| 7 | Magnitudes are ignored: `echo "0.2 0 0"` behaves exactly like `1 0 0` (bang-bang) | ☐ | signs only |

## `debug/vessels/<id>/impulse` (one-shot impulsive kick) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[control] debug_namespace = true` (default). Run during a real flight —
one vessel in a stable orbit, plus (for item 6) a landed one. The game-free half (grammar parse,
EINVAL boundary, command shape/phase) is covered by `gatOS.SimFs.Tests/Commands/VesselImpulseTests.cs`;
these items exercise the game half (`DebugActuator.Impulse`). See `SPEC_9P_FILESYSTEM.md` §3.7/§6 and
`docs/KSA_INTEGRATION_MATRIX.md` (debug table). **All items pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | Note `velocity/orbital`, then `echo "10 0 0 body dv" > /sim/debug/vessels/<id>/impulse` — the vessel gains ~10 m/s along its nose (orbit visibly changes; speed delta matches when pointed prograde) | ☐ | `dv` + `body`: rotate by `GetBody2Cci`, no mass division |
| 2 | Read `mass/total` (m, kg), compute `J = 5·m`, then `echo "$J 0 0" > impulse` (no keywords) — `velocity/cci` X gains ~5 m/s | ☐ | default `ns` unit + default `cci` frame: Δv = J/`TotalMass` |
| 3 | `echo "0 0 0" > impulse` succeeds silently (no-op); `cat impulse` reads `0 0 0` | ☐ | zero kick short-circuits before the teleport |
| 4 | Kicking a **non-active** vessel by id works (debug namespace is authority-exempt); with `debug_namespace = false` the file is gone | ☐ | gate behavior |
| 5 | On-rails at warp > 1: the kick still applies cleanly (orbit updates, no NaN/explosion) | ☐ | orbit-rebuild path is rails-safe by construction — confirm |
| 6 | A **landed** vessel kicked hard vertically (`body`, +X up or `dv` along +r̂) actually launches; a gentle kick just re-settles | ☐ | documented "it's a cheat" semantics |
| 7 | A huge N·s kick on a tiny vessel (Δv ≫ escape) produces a hyperbolic orbit, not a crash/NaN | ☐ | `CreateFromStateCci` handles hyperbolic states (teleport precedent) |

## `/sim/audio` (userland audio playback) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[audio] audio_enabled = true` and `[control] control_enabled = true` (both
default). Bring a few real audio files (an mp3, an ogg, a wav; one of them > 1 MiB for the
compressed-sample path). The game-free half (store, caps, grammars, tree, HTTP routes) is covered by
`gatOS.SimFs.Tests/Audio/**`; these items exercise the FMOD half that needs a live game. See
`SPEC_9P_FILESYSTEM.md` §3.9 and `docs/KSA_INTEGRATION_MATRIX.md` (audio playback). **All items
pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `cat alarm.mp3 > /sim/audio/file/alarm.mp3` from the guest; `ls -l /sim/audio/file/` shows name+size; `md5sum` of the guest file and a read-back of `/sim/audio/file/alarm.mp3` match | ☐ | chunked 9p upload + read-back |
| 2 | `echo alarm.mp3 > /sim/audio/play` → the clip plays through the game's speakers (exit 0); repeat for an `.ogg` and a `.wav` | ☐ | container sniffing (extension irrelevant) |
| 3 | `echo 'alarm.mp3 start=0 end=1200 vol=0.5' > /sim/audio/play` plays ~1.2 s at half volume then stops on its own | ☐ | range + tick-based `end=` |
| 4 | `echo 'music.ogg id=bgm loop=1 vol=0.4 group=music' > /sim/audio/play` loops; the in-game **Music** slider changes its loudness while the **SFX** slider does not; a `group=sfx` play follows the SFX slider | ☐ | channel-group routing |
| 5 | `echo 'bgm vol=0.1' > /sim/audio/set`, `pause=1`, `resume=1`, `seek=30000` each act audibly/immediately; `cat /sim/audio/status` reflects state/pos/vol per channel | ☐ | live channel control + status snapshot |
| 6 | A clip **> 1 MiB** plays with no audible create-stall and no command timeout; two concurrent plays of that same big clip both sound | ☐ | `CreateCompressedSample` path |
| 7 | `echo bgm > /sim/audio/stop` stops one; `echo all > /sim/audio/stop` silences everything (exit 0 even when idle); re-playing an existing `id=` restarts it (old channel replaced) | ☐ | stop/replace semantics |
| 8 | While `music.ogg` plays: `rm /sim/audio/file/music.ogg` — playback **continues** to its natural end; re-uploading a clip mid-play never glitches the playing channel | ☐ | FMOD copy + deferred Sound release |
| 9 | Caps produce shell-visible errnos: a clip past `audio_max_clip_bytes` fails **mid-`cat`** with `EFBIG`; filling the store → `ENOSPC`; playing while still uploading → `EBUSY`; `echo 'nope.mp3' > play` → `ENOENT`; a corrupt/garbage file plays → `EIO` | ☐ | errno vocabulary end-to-end |
| 10 | `tail -f /sim/events` (or `grep -m1 audio.finished`) shows `audio.finished` with `<id> <clip> ended` when a clip plays out and `… stopped` on an explicit stop | ☐ | events ride the sampler |
| 11 | Audio keeps playing at **any time-warp** (incl. > 10×) and while paused-into-menus; `cat /sim/audio/info` matches the loaded clips/caps/channels | ☐ | deliberate warp-mute bypass |
| 12 | From the **host**: `curl -T alarm.mp3 http://127.0.0.1:4242/v1/audio/file/curl.mp3` then `curl -X POST --data 'curl.mp3' http://127.0.0.1:4242/v1/fs/audio/play` plays it; `curl http://127.0.0.1:4242/v1/audio/files` lists it; `curl -X DELETE …/v1/audio/file/curl.mp3` evicts it | ☐ | HTTP binary routes + field-mirror control |
| 13 | Mod unload (quit) with channels playing → **immediate silence**, clean unload, no FMOD errors in the log; `[audio] audio_enabled=false` → `/sim/audio` absent and `audio.*` via `/v1/command` answers `EOPNOTSUPP` 501 | ☐ | `TeardownGameCheats` + config gate |

## KSA 2026.7.3.4826 upgrade — live re-check items — **NOT YET RUN**

The 2026.6.9.4750 → 2026.7.3.4826 playbook pass (2026-07-03) was **clean** — build + tests green, full
decomp/Content diff found no bound-member change (see `scope/FULL_SCOPE.md` §0 and the
`scope/ksa-read-surface.md` 4826 findings). These are the residual items static review cannot settle;
they can ride any of the pending passes above (none blocks the others). **All items pending a live
flight on 4826.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `/sim/status/accessors` shows no degraded accessor after normal flying + a `ctl/throttle` write + a `lights/<n>/brightness` write | ☐ | reflection accessors (manual throttle, light-template clone) are compile-blind; decomp can lag the binary |
| 2 | thug_life quad still draws correctly (add an entry, check pose/depth/MSAA vs the scene) | ☐ | `RenderMainPass` byte-identical + shaders unchanged statically, but Vulkan render-pass compatibility is only provable by drawing |
| 3 | `/sim/display` still streams (enable + open a reader; frames advance) | ☐ | `RenderGame` two-`End()` structure unchanged, shifted ~12 lines — transpiler should absorb it |
| 4 | KittenEva `scale` write still visibly resizes the avatar | ☐ | reflected `_renderable._characterAvatar.Core.Scale` chain; `KittenEva.cs` unchanged but chain types live elsewhere |
| 5 | `environment/g_force` sanity near an SoI boundary (no jump vs 4750 expectations) | ☐ | gravitation refactor folded the multi-body correction into `ComputeGravitationBub` |
| 6 | Decouple/undock a stage with engines on + throttle up: the new stage's `ctl/engine`/`ctl/throttle`/`engines/<n>/active` read back the **inherited** parent state (expected new 4826 behavior, not a gatOS bug) | ☐ | `Vehicle.Split` control-input inheritance + `Decoupler.Decouple` cascade removal |
| 7 | `solar/<n>/produced` on a stock small cell reads ~100 W in sunlight (stock value doubled from 50 W) | ☐ | `CoreElectricalAGameData.xml` value change, same unit |
