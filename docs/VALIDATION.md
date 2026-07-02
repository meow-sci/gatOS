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
