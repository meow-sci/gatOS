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

### MCP transport — live mod pass pending

The MCP host defaults to loopback, has a configurable bind host, and needs no guest-image change. Run this during the next live KSA
flight with an MCP client that supports Streamable HTTP; its static/schema coverage belongs in
`gatOS.Mcp.Tests`, while this table verifies lifecycle wiring and the game-thread boundary.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | The status window's **MCP** row reports a bound port; a client can initialize and discover the namespaced `gatos.*` tools and `gatos://` resources at `http://127.0.0.1:<port>/mcp` | ☐ | default preferred port is 4243; a clash falls back to an ephemeral port |
| 2 | `gatos.get_world`, a resource read, and `gatos.get_vessel` return a common envelope with matching `snapshot_sequence` / `ut`; a vessel id containing path-hostile characters remains its raw KSA id | ☐ | proves the logical JSON projection, not the `/sim` name sanitizer |
| 3 | List pagination defaults to 50 and accepts 1,000; `gatos.get_world(detail:"full")` and `gatos.get_vessel(include:["all"])` return complete response documents rather than a measured/truncated result | ☐ | request framing remains 24 MiB; use chunked clip/track upload for large inputs |
| 4 | `gatos.ignite_engines`/`gatos.vessel_control` or `gatos.command` visibly execute through the normal game-thread command queue, then read back on a later snapshot; `gatos.execute_batch` rejects mixed phases and `gatos.schedule_batch` advances on its selected clock | ☐ | run only on a safe test vessel; existing control/debug gates still apply |
| 5 | On the default specific-address bind, an invalid `Host` or `Origin`, a session id, or a non-JSON/non-POST request is rejected; normal loopback clients work without a bearer token | ☐ | a wildcard bind intentionally accepts the client-used authority and exposes the unauthenticated endpoint |
| 6 | `/sim/display` is absent from MCP discovery/resources/tools; `gatos.get_capabilities` reports that deliberate exclusion | ☐ | terminal-video stream must not be reintroduced as an MCP JSON interface |
| 7 | Set `mcp_enabled = false`, restart the mod, and confirm the MCP status row says `disabled` with no bound port while `/sim`, HTTP, MQTT, serial, and the VM remain usable | ☐ | optional transport failure/disable must not affect the game |

Full `GATOS_IT=1` suite re-run on v3: **green, 278/278, 0 skipped** (see AGENTS.md). The only
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
pending — see AGENTS.md M5 note).

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

## KSA `2026.8.19.5261` upgrade re-verification — **NOT YET RUN**

Prereq: the mod built + deployed against the 5261 DLLs. Everything below is what the **static** pass of
the 2026-08-11 upgrade could not settle — reflection, Harmony install, render correctness, and the
per-frame lifecycle underneath the new object-pooled `PhysicsBubble`. The static findings are in
[`../scope/ksa-assets-and-versions.md#5261-pass`](../scope/ksa-assets-and-versions.md#5261-pass);
build + full suite were green (0 warnings, 1317 passed).

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `cat /sim/status/accessors` is clean (no degraded latches) after a flight with throttle, translate/rotate, staging, and an FC setpoint | ☐ | reflection + Harmony can't be build-checked; `ManualControlInputs` gained `GrabHeld` (rev 5203) but `EngineThrottle`/`ThrusterCommandFlags` are unmoved |
| 2 | On a **hyperbolic / escape** trajectory, `cat …/orbit/{time_to_ap,time_to_pe}` read `0` — **not** ~1.7e29 | ☐ | **the 5261 semantic break.** `UniverseTime.EndOfTime.Seconds()` is finite; `IsSaturated()` guards restore the `0` contract |
| 3 | On a vessel with no upcoming SOI change, `cat …/orbit/next_patch` reads `0`; on one with a patch ahead it reads a plausible UT | ☐ | same sentinel; `_nextPatchEventTime` now defaults to `EndOfTime` |
| 4 | `cat /sim/time/ut` advances smoothly and matches in-game elapsed time under warp | ☐ | `GetElapsedSimTime()` → `GetElapsedSeconds()`; `UniverseTime` is Int128 ns, so precision should *improve* |
| 5 | `echo "<ut> <dvx> <dvy> <dvz>" > …/ctl/burn` still plans a burn; writing a **non-finite** `ut` returns `Invalid` and leaves `/sim/status/accessors` **clean** | ☐ | `new UniverseTime(NaN)` **throws** where `SimTime` did not — the guard must reject before constructing |
| 6 | Welds still track rigidly through translation, rotation and **time-warp**, and hold for several minutes without a physics blowup, log spam, or a "SnapToLeader body/origin time" mismatch | ☐ | **highest live risk.** `VehicleSolver.Wait()` (renamed) + `Teleport` now detaching via the **object-pooled** `PhysicsBubble` (`RemoveFromBubble`, revs 5215/5220/5237) on every weld tick |
| 7 | Weld two vessels, then **separate them far enough to split the physics bubble** and rejoin — no crash, no stale Bepu handle | ☐ | rev 5220 pooling + rev 5237 stale-handle fix; per-frame teleport exercises merge/split hard |
| 8 | `thug_life` quad still draws in the right place, right size, correctly depth-tested and occluded — including in a **supersampled screenshot** | ☐ | rev 5241 added `Program.SetViewport` inside `RenderMainPass`; gatOS already set it itself, so verify no double-set artifact |
| 9 | `/sim/display` screen stream still captures correct, non-corrupt frames | ☐ | transpiler anchor (final `commandBuffer2.End()`) + `ShaderReadOnlyOptimal` assumption re-derived statically; see also the purrTTY-side notes |
| 10 | IVA cabin physics still tracks (accelerometer/rates sane, no jitter) | ☐ | shares the renamed `VehicleSolver.Wait()` drain |
| 11 | `cat …/battery/{charge,capacity}` reflect the **×10** capacities (rev 5227) and `fraction` still spans 0…1 | ☐ | value-only change, unit `J` unchanged — flag any guest flight program with an absolute charge threshold |
| 12 | On an uncontrollable vessel (no control module), `/sim` writes still actuate it while the stock UI now refuses **all** keyboard input | ☐ | revs 5252/5253 widened `ControlsLockout` in `Vehicle.OnKey`; gatOS bypasses that path — confirm the documented divergence is still the intended Option-A behavior |

## Welds / `always_render_iva` / parts — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass (purrTTY tip release). `[control] debug_namespace = true` and
`telemetry_vessel_parts = true` (both default). Run during a real flight with **two vessels close
together** (weld one onto the other) and a crewed capsule (an IVA). These surfaces are gatOS-only (no
ImGui) — drive them over `/sim` (or HTTP/MQTT). See `SPEC_9P_FILESYSTEM.md` §3.4.17 (parts) + §3.7
(`debug/welds/**`, `always_render_iva`) and `docs/KSA_INTEGRATION_MATRIX.md`.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `echo 1 > /sim/debug/always_render_iva` makes interior (IVA) meshes visible from the external camera; `echo 0 …` hides them again | ☐ | global render cheat |
| 2 | With the cheat **off**, no `gatos.iva` Harmony patches exist (reads `0` at start; first enable logs "patches installed", disable logs "patches removed") | ☐ | dynamic patch lifecycle |
| 3 | Toggle repeatedly → no residue (interiors hidden after the final `0`); quitting with it **on** restores templates + unpatches cleanly at unload | ☐ | `TeardownGameCheats` |
| 4 | `ls /sim/vessels/active/parts/` lists the top-level parts; `cat parts/0/{instance_id,template,is_root,position}` are sane | ☐ | `telemetry_vessel_parts` |
| 4b | `ls /sim/vessels/active/parts/<n>/subparts/` lists each part's subparts (count matches `subpart_count`; empty dir when 0); `cat subparts/0/{instance_id,id,display_name,template,position}` are sane, and every subpart `instance_id` is distinct from all part ids | ☐ | subpart discovery (2026-07-16) |
| 4c | `cat /sim/vessels/active/parts/json \| jq` parses; the document matches the `parts/<n>/` leaves (same ids/names/positions, nested `subparts` arrays); `jq '.[] \| select(.display_name=="…") \| .instance_id'` finds a weld anchor in one pipe | ☐ | `parts/json` whole-tree doc (2026-07-16) |
| 5 | Stage/decouple or edit the active vessel → `parts/` updates within a sample (count-change invalidation); a count-preserving edit updates within 10 s | ☐ | per-vehicle cache invalidation |
| 6 | `telemetry_vessel_parts=false` (the "Vessel parts" telemetry menu toggle or config) → `/sim/vessels/<id>/parts/` is gone | ☐ | gate |
| 7 | Pick an anchor `<piid>` from the target's `parts/<n>/instance_id`; `echo "<target> <piid>" > /sim/debug/vessels/<source>/weld_here` welds the source at its current pose (it stays put relative to the target) | ☐ | `weld_here` capture |
| 8 | The welded source tracks the target **rigidly** through translation, rotation, and **time-warp** (offset/orientation preserved); `cat /sim/debug/welds/count` ≥1 and `/sim/debug/welds/<source>/{target,part,offset,rotation,lock_rotation}` reflect it | ☐ | per-frame driver after `VehicleSolver.Wait()` |
| 9 | `echo 0 > /sim/debug/welds/<source>/enabled` suspends tracking (entry kept; source free); `echo 1 …` resumes it | ☐ | suspend/resume |
| 10 | Staging an **unrelated** part on the target (anchor part survives) does **not** drop the weld; removing the anchor part itself falls back to body-frame anchoring (still not dropped) | ☐ | anchor re-resolution each tick |
| 10b | Weld to a **subpart** `<piid>` (from `parts/<n>/subparts/<m>/instance_id`) — `weld_here` captures and tracks exactly like a part anchor; anchored to an **animated** subpart (e.g. a landing-leg / robotics segment), the welded source follows the animation as the subpart moves | ☐ | subpart anchor (2026-07-16); `PositionVehicleAsmb`/`Asmb2VehicleAsmb` compose through `PartParent` |
| 11 | `echo 1 > /sim/debug/vessels/<source>/unweld` removes that weld; `echo 1 > /sim/debug/welds/clear` removes all (count → 0) | ☐ | remove / clear |
| 12 | Weld a vessel to itself, or to one orbiting a different body → `EBUSY`; bad `<piid>`/target → `ENOENT`; bad arity/values → `EINVAL` | ☐ | errnos |
| 13 | With **no** welds active, the `OnAfterUi` driver is a no-op — no measurable per-frame cost, no `VehicleSolver.Wait()` | ☐ | `WeldManager.IsEmpty` early-out |
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
exercise the reflection + flags path (`TranslateActuator`). See `SPEC_9P_FILESYSTEM.md` §3.4.18 and
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

## `ctl/rotate` (manual RCS rotation — W1, AGC_PLAN §7.4) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[control] control_enabled = true` (default). Best exercised on a vessel
with rotation-mapped RCS (any stock rocket with an RCS ring; the EVA kitten backpack also carries
rotation jets). **Set `ctl/attitude_mode` to `manual` first** — an active auto-attitude hold
strips the manual rotation bits (`WithNoRotation()`), the inverse of translate's compose behavior.
The game-free half (parse, EINVAL, command shape/phase, read-back rendering) is covered by
`gatOS.SimFs.Tests/Commands/VesselRotateTests.cs`; these items exercise the reflection + flags
path (`RotateActuator`). See `SPEC_9P_FILESYSTEM.md` §3.4.18 and `docs/KSA_INTEGRATION_MATRIX.md`
(control surface). **All items pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | In `attitude_mode=manual`, floating: `echo "1 0 0" > ctl/rotate` → the vessel **rolls right** (about the nose axis); `echo "0 0 0"` stops the jets | ☐ | +x = `RollRight` (KSA torque decode) |
| 2 | `echo "0 1 0"` → **pitches up**; `echo "0 0 1"` → **yaws right** (torque axes on the X-nose/Y-right/Z-down frame) | ☐ | +y = `PitchUp`, +z = `YawRight` |
| 3 | The command **latches**: torque keeps applying across many seconds without re-writing; `cat ctl/rotate` reads back the latched signs; program exit without `0 0 0` leaves it firing (documented) | ☐ | held-key semantics |
| 4 | Compose with translate: `ctl/batch` carrying both `rotate` and `translate` fires both; each file's write preserves the other's bits | ☐ | bit masking (`~AllRotation` / `~AllTranslation`) |
| 5 | Under an active attitude hold (auto mode), the file write is accepted but the hold keeps steering (rotation bits stripped; at most a rate bias on the held axis) — then `attitude_mode=manual` restores full authority | ☐ | `WithNoRotation()` — documented behavior |
| 6 | `echo 0 > ctl/rcs` (master off) silences RCS rotation; gimbaled engines still respond through TVC while the main engine burns | ☐ | `SelectJetsToFire` vs `ComputeTvcControl` |
| 7 | Magnitudes are ignored: `echo "0.2 0 0"` behaves exactly like `1 0 0` (bang-bang) | ☐ | signs only |
| 8 | With `[control] all_vessels = false`, a write to a **non-controlled** vessel's `ctl/rotate` fails `EACCES` | ☐ | authority gate (not in `AnyVesselActions`) |

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

## IVA cabin physics (`/sim/debug/iva`) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass, `[control] debug_namespace = true`, and a vessel with an interior — the stock
**Gemini 7** is the reference (its cabin ships sardine tins, bolts, screws, photos, notes, tape and a
toothbrush). The physics model itself is covered game-free by `gatOS.SimFs.Tests/Iva/CabinPhysicsTests`
and the surface by `gatOS.SimFs.Tests/Commands/IvaPhysicsTreeTests`; these items exercise what only a live
flight can show. See `SPEC_9P_FILESYSTEM.md` §3.7 (**iva**), `docs/KSA_INTEGRATION_MATRIX.md` (IVA cabin
physics) and `plans/IVA_MOVEMENTS.md`. **All items pending a live flight.**

Items 1–3 are the plan's open questions (§7 Q1–Q3) and should be run **first** — everything else builds
on them. Item 2 needs **no new code and no adopted object**; run it before anything else.

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | **(Q2)** On the pad, `cat /sim/vessels/by-id/<id>/environment/g_force` reads **≈ 1.0**, and `environment/accel` is ≈ 9.81 m/s² along the seat "up" axis | ☐ | the accelerometer assumption the whole model rests on; zero new code |
| 2 | `cat /sim/debug/iva/enabled` reads `0` on a fresh launch; the gatOS status window shows **no** "IVA physics" perf row; `cat /sim/debug/iva/interior` and `…/count` are empty/`0` | ☐ | **off by default means nothing exists** |
| 3 | `echo 1 > /sim/debug/iva/enabled`, then `cat /sim/debug/iva/interior` → one row for the vessel with a **plausible triangle count** (thousands) and an AABB roughly matching the cabin, `fallback` = `0` | ☐ | the interior mesh actually built from the IVA art |
| 4 | **(Q1)** On the pad, adopt one sardine tin (`echo "Gemini7 <iid>" > adopt`): it **settles on the cabin floor** and stays — it does not fall through the hull or sink | ☐ | interior winding / one-sidedness; `iva_double_sided_interior` should make this moot |
| 5 | In orbit (coasting, IVA camera): the same object **drifts in a straight line** and does not accelerate; `nudge` sends it across the cabin and it **bounces off a wall** | ☐ | the case the feature exists for; KSA's own sim would never step here |
| 6 | Light the engines: floating objects **slam aft** and pile against the rear bulkhead; cut thrust and they drift free again | ☐ | linear reaction term |
| 7 | RCS-rotate the vessel: objects **sling outward and lag the rotation** (they do not rigidly follow the cabin) | ☐ | Euler + Coriolis + centrifugal terms |
| 8 | `echo "Gemini7 8" > adopt_all` cuts loose **8 small props** (bolts/screws/notes/tins first) and never a hull panel, seat or console; `echo "Gemini7 4 Sardine" > adopt_all` picks only sardine tins | ☐ | smallest-first heuristic + template filter |
| 9 | Each object's rendered model **sits on** its collision proxy (no visible offset or float-away), and `…/<id>/{position,velocity,mass,shape,size,asleep}` track what you see | ☐ | the shape-offset transform math |
| 10 | Leave the objects still for a few seconds on the pad → `asleep` reads `1` and the status window's "IVA physics" avg drops toward zero | ☐ | sleeping is what makes 16 settled props free |
| 11 | Time-warp above 1× → `cat /sim/debug/iva/stats` shows `parked=1 reason=warp` and objects freeze in place; return to 1× and they resume from rest (no lurch) | ☐ | park/un-park |
| 12 | Leave the IVA camera → `parked=1 reason=not-iva`; `echo 1 > run_outside_iva` un-parks it; enter the **VAB** → `parked=1 reason=editor` and nothing moves | ☐ | **(Q5)** the editor gate |
| 13 | `echo 1 > /sim/debug/iva/<id>/release` puts that prop back at **exactly** its original pose (compare a screenshot before adopting); `echo 1 > clear` does it for all | ☐ | rest-pose exactness |
| 14 | **(Q3)** Adopt several props, displace them, **save and reload**: the reloaded vehicle has every prop back at its template rest pose, and the save file contains no prop transform | ☐ | the "cannot contaminate a save" claim, against the shipping binary |
| 15 | `echo 0 > /sim/debug/iva/enabled` while objects float → everything restores, `count` → `0`, `interior` empties, the perf row stops advancing | ☐ | **the master switch ends the code** |
| 16 | Stage/decouple the part carrying an adopted prop mid-flight → that object auto-releases and `tail -f /sim/events` shows `iva.release`; nothing crashes | ☐ | part-tree churn (R5) |
| 17 | `tail -f /sim/events \| grep iva.impact` fires on wall hits with a plausible speed; wire it to `/sim/audio` (the `help` recipe) and hear the clunk | ☐ | impact events end-to-end |
| 18 | Fly hard (hard burn, spin, touchdown): no object escapes the cabin. If one does, `iva.escape` fires and it reappears at the cabin centre rather than vanishing | ☐ | speed clamp + leash (R3) |
| 19 | Two objects collide with each other and separate plausibly | ☐ | object↔object (free once the sim runs) |
| 20 | With ~16 objects awake, the status window's "IVA physics" avg stays well under a millisecond and the frame rate is unaffected; **the vessel's own trajectory is untouched** (`orbit/apoapsis`/`periapsis` do not drift while objects bounce) | ☐ | perf + the one-way-coupling guarantee |
| 21 | Mod unload (quit) with objects floating → clean unload, no errors in the log; `[iva] iva_physics_enabled = true` in `gatos.toml` makes the feature start enabled on the next launch | ☐ | teardown + config seed |

## FX editors (`/sim/debug/{engineplume,plumetrail,clouds,terrain}`) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[control] debug_namespace = true` and `control_enabled = true` (both default) —
no other gate. Be in a **flight scene** with at least one vessel that has a rocket engine, near a body
with clouds and terrain (so all four families have live entities). The game-free half (field tables,
tree, ranges, EINVAL boundaries, command shapes) is covered by
`gatOS.SimFs.Tests/{FxCatalogTests,Commands/FxEditorsTreeTests}`; these items exercise the game half —
the reflected renderer handles, the propagation/apply paths and the terrain UBO write — that needs a live
game. See `SPEC_9P_FILESYSTEM.md` §3.7 (the four family blocks) + §5.1,
`docs/KSA_INTEGRATION_MATRIX.md` (FX editors) and `scope/ksa-write-surface.md#fx-editors`. **All items
pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `ls /sim/debug/{engineplume/templates,clouds/bodies,terrain/bodies}` each list entities; `cat /sim/debug/plumetrail/json` and one `<entity>/json` per family return a populated object; `cat /sim/status/accessors` shows **no** `fx.*` entry | ☐ | rosters + all six handles resolve |
| 2 | **engineplume, live effect:** with an engine **burning**, `echo 120 > /sim/debug/engineplume/templates/<id>/emission/brightness` and `echo "1 0.35 0.05" > …/emission/color0` visibly change that plume **within a frame** | ☐ | the propagation pass (`OnSettingsChanged`/`UpdateModifiers`/`RecomputeGasVisibilityDensity`) |
| 3 | **engineplume, shared scope:** two vessels using the **same** template both change from one write; a vessel on a different template does not | ☐ | per-TEMPLATE scope is the documented surprise |
| 4 | **engineplume, round-trip + reset:** each written leaf reads back the written value (single-precision, `quality/samples` rounded); `echo 1 > …/<id>/reset` restores the original look exactly | ☐ | pristine capture/replay |
| 5 | **plumetrail, live effect:** `echo 200000 > /sim/debug/plumetrail/render/max_distance` and `echo "0.9 0.6 0.4 1" > …/render/trail_color` change the trails with no apply call; values read back | ☐ | public fields re-read each frame |
| 6 | **plumetrail:** `echo 1 > /sim/debug/plumetrail/clear` removes the trails currently in the world (settings unchanged); `echo 1 > …/reset` restores the settings but does **not** clear trails | ☐ | `clear` vs `reset` are different things |
| 7 | **clouds, live effect:** `echo "0.9 0.9 1" > /sim/debug/clouds/bodies/<body>/layers/0/color` (and a `types/<m>/density`) changes the clouds **immediately, with no hitch/stall and no visible pipeline recreate** | ☐ | the layer re-upload + shadow repopulate; `NoiseScale` deliberately unexposed |
| 8 | **clouds:** a shared field (`shared/transition_start_km`) re-uploads **every** layer; `echo 1 > …/<body>/reset` restores the body's original clouds; an out-of-range layer/type index answers `ENOENT` | ☐ | `Apply(layer:-1)` + errnos |
| 9 | **terrain, global:** `echo 1 > /sim/debug/terrain/wireframe` puts all planet terrain in wireframe; `echo 0` restores it; the leaf reads back the live value | ☐ | public instance field, no reflection |
| 10 | **terrain, per body:** `echo 0.35 > /sim/debug/terrain/bodies/<body>/tessellation/factor`, `echo 9000 > …/max_height`, `echo 45 > …/slope_roughness_deg` each visibly change the surface **with no flicker or strobing over several seconds** | ☐ | the frames-in-flight UBO mirror copy — flicker here means the mirror loop is wrong |
| 11 | **terrain:** `slope_roughness_deg` reads back the degrees written (not radians); `biomes/detail_fade_*_km` read back in km; a body with no render slot is absent from `bodies/` | ☐ | the `TanMeanSlopeRoughnessRadians` unit trap |
| 12 | **terrain reset:** `echo 1 > /sim/debug/terrain/bodies/<body>/reset` restores every touched value | ☐ | pristine replay through the paired write |
| 13 | **Animation rate:** a loop writing `emission/color0` + `emission/brightness` (and a cloud `color`) at ~20 Hz for a minute stays smooth — no frame-time spike, no command timeouts; a `/sim/ctl/batch` group of several FX leaves lands in one tick | ☐ | the "light show" bar (AGENTS.md §7) |
| 14 | **In-game editor round-trip:** change a value in the game's own imgui editor ("Volumetric Exhausts" / "Clouds" / "Terrain Editor") — the matching `/sim` leaf reflects it within ~2 s | ☐ | the 2 s resample beat |
| 15 | **Errnos:** an out-of-range or wrong-arity write fails with `EINVAL` and does **not** change the game; an unknown template/body id ⇒ `ENOENT`; the same via `POST /v1/command` with a bad `values` array is rejected game-side | ☐ | parse-time **and** game-side re-validation |
| 16 | **Transports:** `curl http://127.0.0.1:4242/v1/fs/debug/terrain/wireframe` reads it and `POST` sets it; the MQTT topic `gatos/sim/debug/clouds/bodies/<body>/layers/0/color` mirrors and `…/set` actuates; `GET /v1/snapshot` carries `fxEditors` | ☐ | field-level parity |
| 17 | **Teardown:** with several FX values changed across all four families, unload the mod (quit) — **every** value is restored and the log shows the restore count; reloading starts from pristine values | ☐ | `FxPristine.RestoreAll` in `TeardownGameCheats` |
| 18 | `[control] debug_namespace = false` ⇒ the four family dirs are absent and the `debug.*` FX actions answer `EACCES`; with it on but the game in a menu/no flight scene, the rosters are simply empty (no errors, no log spam) | ☐ | config gate + empty-roster behavior |

## Timed scheduler (`/sim/ctl/timed_batch` + `ctl/schedules/`) — validation pass — **NOT YET RUN**

Prereq: the T6.6 pass. `[schedule] schedule_enabled = true` and `[control] control_enabled = true` (both
default); the caps this pass leans on are `[schedule] schedule_max_live = 16`,
`schedule_max_entries = 8192`, `schedule_max_bytes = 1048576` and `schedule_default_clock = "render"`,
and item 4 needs `[control] max_commands_per_frame` at its default. Be in a **flight scene** with a
controllable vessel (the schedule needs real control leaves to drive). The scheduler is **100 % game-free
and adds no KSA binding at all** — the clock, the grammar, the caps, the coalescing policy, the eviction
rule and the whole tree are covered by
`gatOS.SimFs.Tests/Commands/{PlaybackClockTests,SchedulerTests,TimedBatchFileTests,ScheduleTreeTests,
ScheduleEvictionTests}` and the extended `CommandQueueTests`, so the items below are only what a live
game can show: real frame pacing, real hitches, real warp, and the interaction with the command drain.
See `SPEC_9P_FILESYSTEM.md` (the `ctl/timed_batch` + `ctl/schedules` family),
`docs/KSA_INTEGRATION_MATRIX.md` (which records that this feature contributes **no** anchor) and
`scope/ksa-runtime-coupling.md#schedule-tick`. **All items pending a live flight.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | With a schedule running, press **F2** (hide UI) for ≥ 10 s: entries keep firing on time, `cat /sim/ctl/schedules/<id>/t` keeps advancing, `/sim/stream` keeps growing, and a command written from the guest still executes. Press F2 again — no double-tick, no time jump | ☐ | **the C0.1 regression**: both GUI hooks sit inside `Program.OnFrame`'s `if (DrawUI)` block; `OnAfterFrame` stands in |
| 2 | A `@clock render` schedule and a `@clock wall` schedule with identical offsets, run through a deliberate hitch (alt-tab, or a big scene load): `render` **lags and never catches up**, `wall` demands a catch-up burst and lands back on wall time, and both report the same `dropped` accounting they earned | ☐ | the two clock bases are only distinguishable under a real hitch |
| 3 | A `@clock ut` schedule under 100× warp advances ~100× faster than the same script on `render`; a scene reload does **not** rewind it (the backwards UT step is clamped to 0) | ☐ | the `ut` discontinuity guard |
| 4 | A schedule authoring ~3 600 entries across a handful of leaves, run through a hitch: only a few commands actually execute that tick, `<id>/dropped` counts the rest, `tail -f /sim/events` shows `schedule.dropped` **at most once per second**, and no command times out | ☐ | coalescing bounds the burst by *distinct leaves*; the throttle keeps the event log usable |
| 5 | A launch sequence (`0 ctl/throttle 1`, `2000 ctl/ignite 1`, `9000 ctl/stage 1`, …) fires **on time** while the frame rate is under load; the vessel does what the script says | ☐ | the end-to-end point of the feature |
| 6 | One schedule mixes a **Frame**-phase write (`ctl/throttle`) and a **Solver**-phase one (`ctl/attitude_target`) at the same offset: both land, each in its own phase | ☐ | phase mixing is the deliberate relaxation of `ctl/batch`'s rule |
| 7 | `echo 4000 > /sim/ctl/schedules/<id>/scrub` fires **nothing**; scrubbing *backwards* makes the passed entries replay on the next ticks | ☐ | a seek is navigation, not playback |
| 8 | A looping schedule: on each wrap the finished cycle's tail fires **before** the new cycle's head, and after ten loops the timeline has **not drifted** (the remainder is kept) | ☐ | loop-boundary ordering + no drift |
| 9 | Two `@group take-3` schedules: `pause`/`scrub`/`rate` on **either** moves both; a third joiner starts at the group's *current* position and its already-past entries fire on its first tick; removing the last member drops the group clock | ☐ | shared-clock groups |
| 10 | Fill the registry to `schedule_max_live` with **completed** one-shots, then commit one more: the **oldest finished** player is reclaimed (not the take just started), `tail -f /sim/events` shows `schedule.evicted … reason=max_live`, and the commit succeeds **first try** (no EINVAL-then-retry) | ☐ | cap-pressure eviction is eager and on the game thread, precisely so the retry race does not exist |
| 11 | A schedule whose **first** entry fails (bad ordinal) reports `state = failed` and `last_error = entry <n>: <ERRNO> …` **while its remaining entries still run to the end**, and is **never** evicted while still running | ☐ | `failed` is not terminal; `IsFinished` requires exhausted + not looping + past duration |
| 12 | Completed schedules **persist** with their final `state`/`dropped`/`last_error` until `remove`/`clear` (below the cap nothing is ever reclaimed), so a script can start a take and come back to read the outcome | ☐ | the persist-for-reading property |
| 13 | Errnos are shell-visible: `commit` with `[control] control_enabled=false` ⇒ **EACCES**; a duplicate `@id` ⇒ EINVAL naming the id; past `schedule_max_entries`/`schedule_max_bytes` ⇒ EINVAL; an unresolvable path ⇒ ENOENT; a non-`ctl` target ⇒ EINVAL "not a control file"; a rejected commit **does not burn the id** | ☐ | up-front all-or-nothing validation, id reserved last |
| 14 | Kill the SSH session / disconnect the guest while a schedule runs — it **keeps running** to completion (commit is fire-and-forget, no waiter) | ☐ | the outcome path is the observer → `last_error`, not a blocked write |
| 15 | From the host: `curl http://127.0.0.1:4242/v1/fs/sim/ctl/schedules/<id>/rate` reads it and `POST` sets it; the MQTT topic `gatos/sim/ctl/schedules/<id>/t` mirrors; `POST /v1/command` with `schedule.pause` works | ☐ | field-level parity is structural (ordinary VFS leaves) |
| 16 | Mod unload (quit) with schedules running → clean unload, no errors in the log; `[schedule] schedule_enabled = false` ⇒ `/sim/ctl/timed_batch` and `/sim/ctl/schedules` are **absent** while `/sim/ctl/batch` still works, and `schedule.*` via `/v1/command` answers `EOPNOTSUPP` | ☐ | `TeardownGameCheats` + the config gate |

## Programmable camera (`/sim/camera`) — validation pass — **FIRST PASS FOUND DRIVER BUG; FIX LANDED, RECHECK PENDING**

Prereq: the T6.6 pass. `[camera] camera_enabled = true` and `[control] control_enabled = true` (both
default); tracks additionally need `[schedule] schedule_enabled = true`, and the interpolated `time`
channel needs **both** `[control] debug_namespace = true` **and** `[camera]
camera_allow_time_channel = true`. Items 8 and 20 lean on `[camera] camera_fov_min = 1.0` /
`camera_fov_max = 179.0` and `camera_release_blend_s = 0.6`. Be in a **flight scene** near a rotating
body with terrain, clouds and an ocean, with a vessel that can go EVA (item 7). The game-free half —
the compositor, the rules, the grammars, the tree and gating, the whole JSON track parser/evaluator and
the player's registry behaviour — is covered by `gatOS.SimFs.Tests/Camera/**` (12 fixtures), so the items
below are only what a live game can show. See `SPEC_9P_FILESYSTEM.md` (the `/sim/camera` family),
`docs/KSA_INTEGRATION_MATRIX.md` (programmable camera),
`scope/ksa-write-surface.md#camera-director` and `plans/CAMERA_ASBUILT.md`. The first Hunter/Gemini7
orbit attempt failed and directly motivated the same-frame driver correction below; all rows remain
pending a clean recheck.

> **Out of scope, deliberately:** IVA and Map as *ownership contexts* are **not implemented and are not
> implementable without a Harmony patch** (`plans/CAMERA_ASBUILT.md` §W6,
> `scope/ksa-runtime-coupling.md#camera-mode-contexts`). Do not write checks for them.
> `/sim/camera/map/scope` **does** ship and is item 15.

| # | Check | Result | Notes |
|---|---|---|---|
| 0 | Regression: orbit 5 m and 20 m around `vessel:Hunter`, aim at Hunter, then step azimuth 0/90/180/270. Hunter remains centred and background geometry is stable; repeat on Gemini7 at 20–50 m. `camera/status` reports changing `applied_position_ecl` and a unit `applied_rotation` | ☐ | **2026-08-09 first live pass failed:** target absent and distant rocket jumped. Root cause was an after-render frame-N pose rendered against frame-N+1 target. Fixed with main `Viewport.OnFrame` prefix/postfix, anchor-relative smoothing and exact aim; this is the required recheck |
| 1 | Own the camera, start a move, then press **F2** (hide UI): the shot **keeps moving** smoothly, a `timed_batch` sequence keeps firing, `/sim/stream` keeps advancing and guest writes still actuate. F2 again — no jump, no double-step | ☐ | **the C0.1 regression**; the director is the one driver that runs on *every* frame, F2 or not |
| 2 | `echo 1 > /sim/camera/enabled` then `echo 1 > /sim/camera/release` from **orbit** mode: the camera comes back to **exactly** where and how it was — position, rotation, follow target, tidal flag, FOV, ortho, and mode. Repeat entering **from** map mode and restoring **into** map mode | ☐ | the restore order (`SetFollow` first — it teleports — then `NoRotation`→pose→projection→mode last) |
| 3 | Record ~30 s of footage across a take + release: **no `TimedAlert` text appears at all** except the one documented alert when taking the camera *from* map mode ("Fixed Camera") | ☐ | direct `Viewport.Mode` assignment + `alert:false` on every follow; the Map exception is `MapController.OnSwitchOn`'s control state, not cosmetics |
| 4 | While owned, the player's camera keys/scroll do **nothing** (and a mode hotkey is undone within a frame); after release they work normally | ☐ | the per-frame re-assert; ownership means one writer |
| 5 | A scripted flyby (a `timed_batch` walking `pose/position` + `pose/aim` at 60 Hz, or a JSON track) is **judder-free** at 60 fps — no stutter, no per-frame snap, no horizon flick at waypoints | ☐ | the smoother + catmull-rom/squad; a flick at waypoints means slerp, not squad |
| 6 | `pose/frame bodyfixed` with a **body** anchor: the camera **rides the rotating planet** (a fixed lat/lon stays over the same ground) rather than staying inertial | ☐ | `Celestial.GetCcf2Cce()` is live, not captured |
| 7 | `pose/aim_target part:<vessel>/<kittenaut-subpart>` with `off 0 0.9 0`: the framing **stays on the kittenaut's head while it walks**. Then check EVA locomotion itself — with gatOS holding the camera, "forward" for the kittenaut is wherever the **shot** faces | ☐ | aim is re-resolved every frame; the second half is the **`KittenEva.PrepareWorker`** side effect (documented, not worked around) |
| 8 | `echo 5 > /sim/camera/pose/fov` and `echo 170 > …/fov` both take effect (the game's own UI only offers 15–120); `echo 1 > /sim/camera/pose/ortho` gives a real orthographic view; `ortho_height` changes the framing | ☐ | `SetFieldOfView` does not clamp — that is what puts fisheye/telephoto in reach |
| 9 | After changing `ortho_height`, release the camera: the half-height is **not** restored (there is no public getter in 5168 to capture it from). Confirm this is acceptable in practice — i.e. it is only visible if the player was already in ortho | ☐ | the one camera change gatOS **cannot** undo; if unacceptable, the fix is a gatOS-side latch, not a restore |
| 10 | `pose/geo` over an ocean, walking the altitude down: the view descends to the surface and then **stops at ~0.5 m** no matter how much lower you ask for | ☐ | `Camera.ClampCamera()` runs at the top of every `Camera.OnFrame` and is deliberately not worked around |
| 11 | A `pose/geo` climb straight through a **cloud deck**: the clouds render correctly from inside and above, with no popping or z-fighting | ☐ | same-frame viewport apply, tested against volumetric passes |
| 12 | `pose/roll 30` — confirm **subjectively** that the sign feels right (positive roll rolls the camera clockwise, so the horizon tilts counter-clockwise) | ☐ | the sign was **defined, not derived**; if it reads backwards, flip it once, in `Apply`, and say so in the SPEC |
| 13 | A track with a `"time"` channel eases into slow-mo (`0.15`) and back: the sim visibly slows; on **release**, the player's original warp setting is **restored**. Then run a shot with **no** `time` channel and confirm the warp setting is **untouched** | ☐ | the capture is **lazy** — first frame the channel is driven — and the restore is conditional on it |
| 14 | Start an **auto-warp** (a manoeuvre-node warp), then run a `time`-channel shot over it: note who wins frame by frame. Then run the same shot with the auto-warp stopped first | ☐ | neither public `SetSimulationSpeed` overload checks `IsAutoWarpActive`; gatOS deliberately adds no guard — **stop the auto-warp before rolling the shot** |
| 15 | `echo 5000 > /sim/camera/map/scope` in **map** mode changes the map zoom and reads back; a value **below** the focus's mean radius reads back **clamped up**; changing the map focus and re-entering map **recomputes** it (a scope written before the focus change does not survive); outside map mode the write is accepted but has no visible effect | ☐ | the three inherited `MapController` behaviours (`OnFrame` clamp, `OnSwitchOn`→`SetDefaults`, mode-scoped) |
| 16 | Upload a track (`cp flyby.json /sim/camera/track/flyby`), `echo flyby > /sim/camera/play`: shots run in order, `tail -f /sim/events` shows `camera.shot` per boundary and one `camera.finished reason=complete`; `/sim/ctl/schedules/camera/` appears with `kind camera-track`; `schedules/camera/{pause,scrub,rate}` drive the take | ☐ | the track player is a schedules-registry entry, so the whole schedule transport drives it |
| 17 | `cp` a **deliberately malformed** track: the write/`cp` reports the error where it can, and `cat /sim/camera/last_error` names the track and the specific parse problem (shot/channel/key). A later `camera/play` of the same name answers **EINVAL with the same message**; a `play` that actually starts **clears** it | ☐ | a 9p clunk cannot carry an errno — this leaf is the diagnosis a guest can actually read |
| 18 | A track's `"mode":"orbit"` circle and a hand-written `echo "90" > /sim/camera/pose/orbit/azimuth` (same radius/elevation/frame/anchor) put the camera in the **same place**; a full `0 → 360` azimuth sweep closes with **no seam** | ☐ | both resolve through the one `CameraPlacement.Spherical`; the 360° fold is bit-exact |
| 19 | Frames degrade **honestly**: `pose/frame lvlh` about a landed vessel ⇒ **EOPNOTSUPP** with a message naming why (not a silent fallback); `enu` about a vessel at its parent's centre likewise; despawn the anchor mid-shot ⇒ the camera **holds the last good pose** and the log gets the reason **once**, not 60×/s | ☐ | nothing ever silently falls back to a different frame; no NaN reaches the view matrix |
| 20 | Refusals are correct and explain themselves: `camera/mode`, `camera/follow`, `camera/tidal` ⇒ EOPNOTSUPP **while owned** (message points at `pose/anchor`+`pose/aim_target` or `camera/enabled 0`); `camera.follow part:…` ⇒ EOPNOTSUPP; an anchor/aim target that is not live ⇒ **ENOENT at write time**; `pose/geo` with a non-body anchor ⇒ EOPNOTSUPP naming both fixes | ☐ | ownership *is* a mode park with `Following == null`; a follow would give the transform a second writer |
| 21 | `camera/enabled 0` **blends** back over `camera_release_blend_s` (0.6 s) onto a **moving** follow target and lands on the target, not on where it used to be; `camera/release` is an instant **hard cut**; taking the camera again mid-blend keeps the shot, baseline and overrides | ☐ | the restore point is recomputed every blend frame; two verbs, one restore path |
| 22 | **(§5.2 / C5.3 — the open question this pass must answer)** With the camera far from the controlled vessel (a few km, and again a few hundred km), do distant objects render in the right place, or does the game's bubble-relative ego frame produce visible drift/precision artefacts? Note the distance at which anything becomes objectionable | ☐ | unfollowed, every object takes the plain `GetPositionEcl() − PositionEcl` path — self-consistent, so it *should* be fine, but it is an assumption until seen |
| 23 | From the host: `curl http://127.0.0.1:4242/v1/fs/sim/camera/pose/fov` reads and `POST` sets it; `gatos/sim/camera/pose/fov` mirrors over MQTT; a `POST /v1/command` with a **bad** `camera.geo` payload is rejected **game-side** with the same errno the 9p write would give | ☐ | the HTTP/MQTT paths bypass the 9p parse — `CameraRules` re-runs in the actuator for exactly this |
| 24 | With `[schedule] schedule_enabled = false`: every `pose/**` channel still works, and `camera/play`/`set`/`stop` answer **EOPNOTSUPP** with a message naming the flag | ☐ | a camera track *is* a schedules-registry entry — a real, reachable configuration |
| 25 | With `[control] debug_namespace = false` **or** `[camera] camera_allow_time_channel = false`, a track whose shot drives `time` still runs at 1× and logs **one** warning naming which gate is off (not one per frame); the warning re-arms on the next ownership take | ☐ | ignore-with-a-warning, per plan §4.4 — failing a whole shot over a config flag would be worse |
| 26 | Mod unload (quit) **with a track playing**: the camera is handed back, the take is stopped (`camera.finished reason=stopped`), no errors in the log. `[camera] camera_enabled = false` ⇒ the whole `/sim/camera` tree is **absent** and `camera.*` via `/v1/command` answers `EOPNOTSUPP`, while `ctl/focus` still works | ☐ | `TeardownGameCheats` → `Shutdown()` + the config gate; `camera.focus` is a separate, always-on action |

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

## KSA 2026.7.5.4892 upgrade — live re-check items — **NOT YET RUN**

The 2026.7.3.4826 → 2026.7.5.4892 playbook pass (2026-07-14) was **clean** — build + tests green, full
decomp/Content diff found no bound-member change (see `scope/FULL_SCOPE.md` §0 and the
`scope/ksa-read-surface.md` 4892 findings). Note the game marks rev 4884 as **save-breaking** upstream
(saved games and saved vehicles), so start from fresh vehicles. These are the residual items static
review cannot settle; they can ride any of the pending passes above (the 4826 items remain valid and
can run on 4892). **All items pending a live flight on 4892.**

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `/sim/status/accessors` clean after normal flying + a `ctl/throttle` write + a `lights/<n>/brightness` write + a `vessels/<id>/scale` write | ☐ | reflection accessors are compile-blind; decomp can lag the binary |
| 2 | `tanks/` listing on a **new** stock vehicle (post-4884 Gemini7/Rocket) shows the new-catalog substances (e.g. methalox reactants) with sane `amount/capacity/fraction`; `debug/vessels/<id>/refill_fuel` still fills the affinity-assigned mix | ☐ | rev 4884 Reactions/affinity refactor — read path unchanged, catalog + auto-assignment new |
| 3 | After a `ctl/burn` completes (or engines shut down), `engines/<n>/throttle` reads **0**, not the last commanded value | ☐ | `FlightComputer.CommandEngineThrottles` now zeroes `CommandThrottle`/`CommandBurnTime` when no burn is commanded |
| 4 | `echo 1 > ctl/stage` still activates the next sequence and `ctl/stage`-driven decouples behave (staging window in-game is now "Resource Groups") | ☐ | `SequenceList.ActivateNextSequence` intact; sequences double-buffered (4880) + batched spent-sequence removal (4873) |
| 5 | At high warp with crossing orbits / discarded stages: `situation` transitions show more vessels going on-rails (ignite + no propellant no longer blocks it; distant ocean vessels float on-rails) — truthful new behavior, not a gatOS bug | ☐ | rev 4866 on-rails perf changes |
| 6 | thug_life quad still draws correctly (add an entry, check pose/depth/MSAA vs the scene) | ☐ | `SuperMeshRenderSystem.cs` untouched, but the ground-clutter render overhaul (4861–4889) reworked pipeline-adjacent state; only a live draw proves render-pass compatibility |
| 7 | `/sim/display` still streams (enable + open a reader; frames advance) | ☐ | `RenderGame` interior gained an underwater pass; transpiler targets the final `End()` — should absorb it |
| 8 | Weld a vessel pair across a CCI↔CCF frame transition (e.g. near/into atmosphere): no attitude/rate corruption on the welded source | ☐ | rev 4867 fixed angular-velocity corruption in CCI↔CCF transitions — welds ride `Teleport` through those frames |
| 9 | EVA kitten spawn: kitten appears just outside the door (no collision kick-spin); `eva`-taxi tutorial flow still works | ☐ | rev 4869 spawn-position change + backpack collider |

## KSA 2026.7.6.4939 upgrade — live re-check items — **NOT YET RUN**

The 2026.7.5.4892 → 2026.7.6.4939 playbook pass (2026-07-16) was **clean** — build + tests green, full
decomp/Content diff (`git diff 7cf5c0a..2423a02`, gapless changelog) found no bound-member change (see
`scope/FULL_SCOPE.md` §0 and the `scope/ksa-read-surface.md` / `scope/ksa-write-surface.md` 4939
findings). Note rev 4915 removes the old service-module parts — **save-breaking upstream** (the second
save-breaker after 4884) — so start from fresh vehicles. The 4892 items above remain valid and can run
on 4939. These are the residual items static review cannot settle:

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `/sim/status/accessors` clean after normal flying + a `ctl/throttle` write + a `ctl/translate` write + a `lights/<n>/brightness` write + a `vessels/<id>/scale` write | ☐ | reflection accessors are compile-blind; decomp can lag the binary |
| 2 | On a **control-less** vessel (e.g. a decoupled stage with no control module): `ctl/stage`, `engines/<n>/active`, and `decouplers/<n>/fire` via `/sim` still succeed while the stock UI shows the new lockout ("No vehicle control module.") — confirm this divergence is intended gatOS behavior | ☐ | rev 4914 `ControlsLockout` is UI/input-layer only; `SequenceList.ActivateNextSequence`/`EngineController.SetIsActive`/`Decoupler.SetIsActive` carry no gate |
| 3 | Fuel: build a vehicle with a fuel line + a propellant-use-disabled tank; `engines/<n>/propellant` flips per the new rules (line-fed stacks drainable; crossfeed no longer crosses a decoupler out-of-stage; disabled tank walls off); an armed tank-to-tank transfer shows ~20 W per draining tank in `power/consumed` | ☐ | revs 4903/4907/4917/4938 — reads report game truth; formats unchanged |
| 4 | `animation.goal` on landing legs: colliders follow the deployed legs (vehicle stands on them) and `situation` stays physics-simulated (off-rails) while the animation runs | ☐ | rev 4930 + `VehicleUpdateTask` off-rails-while-animating |
| 5 | `echo 1 > ctl/stage` on a controllable vessel still activates the next sequence (the in-flight Sequences window is redesigned + re-orderable in flight) | ☐ | `SequenceList.cs` +1137-line UI rework; `ActivateNextSequence` byte-compatible |
| 6 | thug_life quad still draws correctly (add an entry; check pose/depth/MSAA vs the scene, with and without the new Plume Trails / screenspace-particles graphics toggles ON) | ☐ | `SuperMeshRenderSystem.cs` untouched, but revs 4894–4932 add mid-frame compute/composite passes; only a live draw proves render-pass compatibility |
| 7 | `/sim/display` still streams (enable + open a reader; frames advance; try with Plume Trails ON) | ☐ | `RenderGame` interior gained volumetric-trail + gizmos calls; the tail (final `End()`) is byte-identical — transpiler should absorb it |
| 8 | `tanks/` listing on a new vehicle after adding a fuel line / toggling propellant-use: `amount/capacity/fraction` stay sane through `RecreateResourceManagers` rebuilds | ☐ | rev 4938 toggling rebuilds resource managers; `Tank.Moles` path untouched |

## KSA 2026.7.8.4980 upgrade — live re-check items — **NOT YET RUN**

The 2026.7.6.4939 → 2026.7.8.4980 playbook pass (2026-07-22) found **one compile break, fixed**
(`DockingActuator.Undock` — rev 4943 removed `VehicleDockingInputData.OldMeanRadius`) and **two
inherited semantic drifts** (`FlightComputer.RCSMode` gating auto attitude holds; the `RollMode`
default flip). See `scope/FULL_SCOPE.md` §0 and the `scope/ksa-read-surface.md` /
`scope/ksa-write-surface.md` 4980 findings. The 4939 items above remain valid and can run on 4980.
Residual items static review cannot settle:

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `echo 1 > …/docking/<n>/undock` on a docked port still separates cleanly with the pushoff impulse, and the camera no longer jumps (the rev 4943 fix this break rode in on) | ☐ | the enqueue lost `OldMeanRadius` — confirm the new camera-follow path tolerates a `/sim`-initiated undock |
| 2 | **RCSMode gate**: hold `ctl/attitude_mode=Prograde` on an RCS-only vessel, press the new **R** keybind (RCS off) → the hold silently stops actuating (gauge shows RCS off); press R again → hold resumes. ~~Confirm `ctl/rotate`/`ctl/translate` manual flags still fire jets regardless of the toggle~~ | ☐ | revs 4946/4949/4975. ⚠️ **The struck-through half is now WRONG** — rev 5143 (5168) made `RCSMode` gate the manual flags too. Superseded by the 5168 items below; run this against 5168 expectations, not these. `FlightComputer.RCSMode` **is** now surfaced, as `ctl/rcs_mode`. |
| 3 | **Roll decoupled default**: on a **fresh** (never-saved) vessel, write a full-quaternion `ctl/attitude_target` → +X pointing converges but the vessel rolls free; set Roll mode in the stock UI → roll holds. Decide whether `SetAttitudeTarget` should set `RollMode` explicitly | ☐ | rev 4978 `RollMode` default `Up`→`Decoupled`; loaded saves keep their serialized mode |
| 4 | After `docking.undock` / `decoupler.fire` on a vessel whose control modules carry names: the separated vessel's `name` / `vessels/by-id/<id>` key is the persisted control-module name (not `<parent>-<n>`) and telemetry follows it | ☐ | rev 4950 `Control.VehicleName` stamps; gatOS keys vessels by `Vehicle.Id` |
| 5 | thug_life quad still draws correctly under the reworked cascaded shadows; then take a **hi-res (scale>1) screenshot** with a quad active — expect at worst a transient self-disable (`Active=false` + one log), never a crash | ☐ | shadow rework is cascade-path only, but rev 4942's `SampleCountOverride` renderer rebuild can mismatch the never-rebuilt quad pipeline's MSAA state |
| 6 | `/sim/display` still streams with **texture streaming** ON (new default) and across a hi-res screenshot capture | ☐ | rev 4942 inserts `ScreenshotCapture` calls immediately before the transpiler's final-`End()` anchor; rev 4974 texture streaming is terrain-side |
| 7 | High-warp physics sanity: `acceleration`/`dynamic_pressure` no longer gain spurious orbital energy at high physics warp (the values gatOS reports track the fixed integrator) | ☐ | rev 4977 verlet + CCI-frame drag fix — value drift, members unchanged |

## KSA 2026.7.9.5018 upgrade — live re-check items — **NOT YET RUN**

The 2026.7.8.4980 → 2026.7.9.5018 playbook pass (2026-07-24) found **one compile break, fixed**
(`VesselReader.SampleTanks` — rev 4992 renamed `Mole.GetLiquidMass` → `GetStoredMass`), **one coverage
gap** (SRB solid propellant absent from `tanks/` while counted in `mass/propellant`), and **two inherited
semantic drifts** (widened encounter candidacy; `Module.List` concrete-type segmentation). See
`scope/FULL_SCOPE.md` §0 and the `scope/ksa-read-surface.md` / `scope/ksa-write-surface.md` 5018
findings. All 4980 items above remain valid and can run on 5018. Residual items static review cannot
settle:

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | Build a vessel **with SRBs**: `ls /sim/vessels/active/srb/` lists one dir per booster; `tanks/` lists only the liquid tanks; and **`mass/propellant` − Σ `tanks/<r>/amount` = Σ `srb/<n>/mass`** (the identity the new surface exists to make checkable). Confirm it holds as the boosters burn | ☐ | the headline check for the new `srb/<n>/` read surface (SPEC §3.4.8) |
| 1a | `srb/<n>/`: `substance`/`grain`/`grain_shape` name the propellant + geometry; `valid`=1 and `error` empty; `segment_count` matches the stacked segments; `segments/<m>/{mass,mass_initial,radius,length,volume}` are plausible and Σ `segments/<m>/mass` = `srb/<n>/mass` | ☐ | static/config reads — checkable on the pad before ignition |
| 1b | Pre-ignition `srb/<n>/fraction` = 1 on a fresh booster and `burn_time` = 0 (it only populates while burning); after ignition `active`=1, `mass_flow` > 0, `burn_time` counts down, `chamber_pressure`/`chamber_temp`/`exit_*` go non-zero, and `burning_area` traces the grain's thrust curve (rises then falls for a star grain) | ☐ | the live burn-state reads; `burn_time` is 0-when-idle by KSA design, so `fraction` is the pre-ignition gauge |
| 1c | At burnout `mass` settles at ≈ `mass_unburnable` (the sliver), `mass_burnable`/`fraction` → 0, `propellant` → 0, `active` → 0 | ☐ | confirms the unburnable-sliver accounting from `SolidMotor.RefreshUnburnableGrain` |
| 1d | `srb/<n>/engine` indexes a real `engines/<n>` entry, and igniting via `ctl/ignite` (or that engine's `active`, or `ctl/stage`) lights **that** booster. Writing `engines/<n>/throttle`-style throttle changes nothing about its thrust | ☐ | the cross-link + the read-only rationale; confirms nothing is missing on the control side |
| 2 | On the same vessel, `engines/<n>/` includes the SRBs with sane `vac_thrust`/`isp`, `engines/<n>/active` and staging ignite them, and `ctl/throttle` visibly does **nothing** to their thrust | ☐ | `SolidMotor : RocketCore` under an ordinary `EngineController`; throttle inert by physics, not by API — confirm no error/exception path |
| 3 | `echo 1 > /sim/debug/vessels/<id>/refuel` on a spent SRB restores its grain (mass and thrust return) as well as the liquid tanks | ☐ | rev 4992 rerouted `RefillAllTanks` through `ISubstanceStore`; gatOS's anchor (`Vehicle.RefillConsumables()`) is unchanged, so this is a free win to confirm |
| 4 | In Mars orbit, `encounters` now emits lines for **Phobos/Deimos** approaches that 4980 omitted; the `{body,ut,distance}` shape is unchanged and the values are plausible | ☐ | rev 4991 replaced the flat 10 000 km SOI cutoff with a radius-band + approximate-MOID test |
| 5 | Positional `/sim` indices are stable after the module-storage rework: `engines/<n>`, `rcs/<n>`, `lights/<n>`, `tanks/<r>`, `docking/<n>`, `decouplers/<n>` map to the same physical parts across a save/load and a staging event | ☐ | rev 4990 segments same-concrete-type modules contiguously; `Get<T>()` is per-concrete-type so ordering should be unaffected — verify rather than assume |
| 6 | `/sim/status/accessors` reports **no** degraded accessor after a fresh flight load (the reflection set: manual throttle, thruster flags, light-template clone) | ☐ | standing post-update check — decomp can lag the shipping binary |
| 7 | thug_life quad still draws correctly, and `/sim/display` still streams | ☐ | statically clean (`SuperMeshRenderSystem.cs` and `Program.RenderGame` byte-identical), but rev 4988's MilkyWay renderer split and the plume-trail/ground-clutter pass churn are only provable live |

## KSA 2026.8.3.5117 upgrade — live re-check items — **NOT YET RUN**

The 2026.7.9.5018 → 2026.8.3.5117 playbook pass (2026-08-01, spanning the un-audited 5056 drop — revs
5019–5116) found **two compile breaks, both fixed** (`NavBallData.DeltaVInVacuum` → `DeltaV`;
`VolumetricTrailRenderer.ExpansionTimeSeconds` moved onto `PlumeTrailSettings`) and **three drifts with
no code change** (substance phase names, encounter population, docking identity). See
`scope/ksa-assets-and-versions.md#5117-pass` and the 5117
[read](../scope/ksa-read-surface.md#5117-findings) / [write](../scope/ksa-write-surface.md#5117-findings)
findings. All prior items above remain valid and can run on 5117. Residual items static review cannot
settle:

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | `cat /sim/vessels/active/navball/deltav` on a **multi-stage** vehicle now reports the **active sequence's** Δv, not the whole stack's: it should step *up* as a spent stage is dropped (the new stage's own Δv), where the old value fell monotonically | ☐ | rev 5114 rewired it onto `Parts.PerformanceSequences.FindActiveSequenceDeltaV()` — the headline semantic change |
| 1a | `navball/twr` is now **atmosphere-corrected**: on the pad it should read visibly **lower** than the same vessel's vacuum TWR, rise with altitude, and ignore engines that are out of propellant | ☐ | numerator moved to `ComputeActiveThrust(AtmosphericPressure)`; this drift compiles clean, so only a live read proves it |
| 1b | Confirm the value is still finite/sane in edge cases the old formula handled: no engines, all engines dry, zero throttle, on-rails/warp | ☐ | `Sanitize.Finite` guards NaN, but a new divide-by-zero path would surface as 0 rather than a crash |
| 2 | `cat /sim/vessels/active/tanks/*/substance` reports the **new bare names** — `Kerosene`, not `Liquid Kerosene` — while cryogenic (gas-default) substances keep `Liquid O2` / `Liquid H2` / `Liquid CH4`; `srb/<n>/substance` reports `APCP`, not `Solid APCP` | ☐ | rev 5095 `DefaultPhase`; **check `examples/`, `site/guides/` and the SDK for any string match on substance names before relying on this** |
| 3 | `echo 3.5 > /sim/debug/plumetrail/render/expansion_time` still visibly changes plume-trail expansion, `cat` reads the value back, and the game's own Plume Trails debug window shows the same number in its "Profile" section | ☐ | the re-bound two-hop accessor — the round-trip through `PlumeTrailSettings` is the whole point of the fix |
| 3a | The other ten `debug/plumetrail/render/*` fields still read/write, and `debug/plumetrail/reset` restores every captured pristine value **including** `expansion_time` | ☐ | confirms the pristine capture/restore path still covers the relocated field |
| 4 | `/sim/status/accessors` reports **no** degraded accessor after a fresh flight load — specifically the new **`fx.trail_settings`** latch alongside the existing set (manual throttle, thruster flags, light-template clone, the five other FX handles) | ☐ | standing post-update check; the new latch is the one most likely to fire, since it walks two private fields |
| 5 | In an orbit whose period greatly exceeds the target's, `encounters/` no longer floods with entries, and the listed approaches reflect the **final** trajectory including planned burns | ☐ | revs 5106/5110 — row count/content change only, struct unchanged |
| 6 | Dock a **small** vessel to a **larger** one and confirm which `/sim/vessels/<id>` survives; re-check that any docking program keying off the surviving id still works | ☐ | rev 5076 "larger vehicles absorb smaller vehicles"; also re-check contact docking after the rev 5061 origin-snap fix |
| 7 | thug_life quad still draws correctly, and `/sim/display` still streams — **test with MSAA both on and off** | ☐ | statically clean (`SuperMeshRenderSystem.cs` byte-identical; A2C is not a member of the offscreen pass), but revs 5057/5058 made the alpha-to-coverage attachment MSAA-conditional, so both paths deserve a look |
| 8 | IVA cabin physics (`/sim/debug/iva`) still tracks: adopted objects follow the cabin without drift or stale poses | ☐ | rev 5112 added a `Part.MatrixAsmb2VehicleAsmb` cache; statically safe (the pose setters invalidate), but this is the one place a stale cache would show as visible lag |
| 9 | Vehicles can now be **destroyed** by structural g-limit / dynamic pressure (rev 5115). Confirm a destroyed vessel disappears cleanly from `/sim/vessels/`, the sampler's despawn pruning fires, and no accessor latches degraded | ☐ | a new way for a vessel to vanish mid-flight — not modelled in `/sim` yet, but the pruning paths must tolerate it |

## KSA 2026.8.5.5168 upgrade — live re-check items — **NOT YET RUN**

The 2026.8.3.5117 → 2026.8.5.5168 playbook pass (2026-08-05, revs 5118–5168) found **four compile
breaks, all fixed** (every one from rev 5154's move of offscreen rendering onto Vulkan **dynamic
rendering**, which deleted `Program.OffScreenPass`, `KSA.OffscreenTarget`, `KSA.RenderTarget`,
`KSA.Framebuffer` and `Core.RenderPassState`) and **three silent semantic breaks, all closed**
(`RCSMode` now gating *manual* RCS; the game clearing latched thruster flags; disabled decouplers).
See `scope/ksa-assets-and-versions.md#5168-pass` and the 5168
[read](../scope/ksa-read-surface.md#5168-findings) / [write](../scope/ksa-write-surface.md#5168-findings)
findings. **The sibling purrTTY mod needed the same rev-5154 migration** — an unpatched purrTTY
hard-crashes KSA on the first frame with `TypeLoadException: KSA.OffscreenTarget`, so make sure both
mods are rebuilt before running any of this. All prior items above remain valid and can run on 5168.
Residual items static review cannot settle:

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | **The new `ctl/rcs_mode` round-trip**: `cat …/ctl/rcs_mode` reads `Enabled` on a fresh vessel; press **R** in-game → it reads `Disabled`; `echo Enabled > …/ctl/rcs_mode` turns it back on and the in-game gauge agrees | ☐ | new Solver-phase control (rev 5143). Confirm it **sticks** — a Frame-phase write would flash on then revert, which is exactly why it is Solver-phase |
| 1a | **The gate it exists for**: with `rcs_mode=Disabled`, `echo "1 0 0" > …/ctl/translate` and a `ctl/rotate` write both fire **no** jets (while the read-backs still report the commanded signs); re-enable → the same writes fire jets normally | ☐ | rev 5143 `FlightComputer.cs:471`. **This inverts the 4980 item 2 expectation** — manual RCS *is* gated now |
| 2 | **Latch clearing (rev 5128)**: hold a `ctl/translate` and then (a) alt-tab away from the game window, (b) time-warp past **30×**, (c) click into any in-game text field, (d) switch camera mode, (e) switch controlled vessel. In every case jets should **stop** and `…/ctl/translate` should read `0 0 0` without gatOS having written it | ☐ | `Vehicle.ClearHeldPlayerInput()`; (b) and (c) re-clear *every update*, so they are sticky-off, not one-shot. **The (c) case is the one to watch for gatOS specifically** — confirm whether typing in a **purrTTY terminal window** counts as ImGui keyboard capture, since that would cancel RCS while a player types |
| 2a | Confirm the same events do **not** disturb `ctl/throttle` or `ctl/ignite` | ☐ | statically they touch different fields (`_engineFlags` is the keyboard throttle-ramp, not `EngineOn`/`EngineThrottle`) — cheap to confirm and valuable if wrong |
| 3 | **Disabled decoupler**: disable a part's decoupler module in the editor (rev 5132), launch, then `cat …/decouplers/<n>/enabled` → `0`, and `echo 1 > …/decouplers/<n>/fire` → **EOPNOTSUPP** (not a false success, and nothing separates). Re-enable → `1` and fire works | ☐ | the false-success fix; also confirm an ordinary decoupler still reads `enabled` = `1` |
| 4 | **thug_life quad after the dynamic-rendering migration** — the single highest-risk item in this pass. Add an entry and check pose/depth/blending against the scene with **MSAA off**, **MSAA on**, and the new **CMAA2** anti-aliasing option (rev 5156) selected | ☐ | `BuildPipeline` no longer names a `VkRenderPass`; it is stamped by `Program.OffscreenTarget.SetupGraphicsPipeline`. Statically the postfix still runs inside the offscreen `BeginRendering` scope and the sample count now follows the target, but only a live draw proves render-target compatibility. A GPU fault self-disables (`Active=false` + one log) rather than crashing |
| 5 | **`/sim/display` still streams** across all three AA modes, and with **fullscreen** toggled (rev 5123 reworked Windows fullscreen onto `VK_EXT_full_screen_exclusive`) | ☐ | statically verified: the transpiler's final-`End()` anchor survives, the offscreen colour still ends in `SampledReadVfc` (`Program.cs:4312`), and CMAA2 renders into its own target rather than disturbing that layout — but the capture is a GPU readback and deserves a live look |
| 6 | **purrTTY in-world terminal quad** still renders correctly (it now owns its render pass/framebuffer outright instead of borrowing KSA's deleted `RenderTarget`, and its scene barrier moved to the tracked-state `PipelineBarrier2`) | ☐ | purrTTY-side change made in the same work item; check both the in-world quad and the normal terminal window, with MSAA on and off |
| 7 | **Encounters (rev 5141)**: plan a **near-coplanar** transfer (e.g. Hohmann LEO → Luna) and confirm `encounters/` now predicts the SOI encounter it previously missed, with plausible `{body,ut,distance}` | ☐ | the fix explicitly calls out Hohmann-to-Luna; compounds the 5117 items 5 |
| 8 | **Value drift sanity**: RCS authority feels weaker overall and small thrusters much weaker than large (rev 5119); size-D/E SRB `srb/<n>/` masses + burn times shifted (rev 5124); part `mass`/`center_of_mass` and inertia-derived navball figures moved (rev 5166, incl. new fairing masses + tangent-ogive type). Confirm nothing reads as NaN/zero/absurd | ☐ | pure value drift — no API change — but these feed Δv/TWR budgeting in flight programs and the tutorials |
| 9 | `/sim/status/accessors` reports **no** degraded accessor after a fresh flight load — the full reflection set, and specifically the `fx.*` FX handles and the `KittenEva` scale chain (kitten locomotion churned heavily in revs 5128–5144) | ☐ | standing post-update check |

## AGC (examples/agc — Luminary099 in-guest) — mission cards M-A…M-E — **NOT YET RUN IN-GAME**

Prereq: the T6.6 pass; `examples/agc` built + installed in the guest (`tools/build-agc.sh` —
rope assembly is checksum-gated, so a successful install already proves the byte-exact
Luminary099/Comanche055 ropes). Host-tier and in-guest-tier validation (AGC_PLAN §10 tiers 1-2)
**already ran green on 2026-07-22**: 51 unit tests (codec golden packets, IMU/PIPA conservation
properties, padload octal pins, radar quantization) + live-wire tests against a real yaAGC
(V16N36 clock, V35 lamp test, padload-core resume) + the embedded-mode freeze/thaw test.
These cards are the remaining in-game tier. Flagged `[impl-verify]` calibration items are
listed with their card.

| Card | Check | Result | Notes |
|---|---|---|---|
| M-A "First light" | Fresh save → `agc start lm` (audit GREEN on the stock moon) → `dsky` in a purrTTY tab: V35 animates every segment/lamp; V16N36 ticks; V37E00E reaches P00; same on an in-world quad | ☐ | emulator + DSKY + clock, in-game packaging |
| M-B "Alignment" | ISS 90 s turn-on (NO ATT clears); `agc align`; V41N20 coarse align zeroes; V16N20 tracks the tumbling vessel within quantization on ALL axes (falsifies gimbal sign errors); V40 zero; pause → resume resyncs clean (V55 trim in `agc log`) | ☐ | the whole IMU seam; `body_map` signs |
| M-C "Burns" | V48 DAP load; V76/V77 rate-damp/hold visibly (DAP jets ride `ctl/batch` rotate/translate); P30+P40: ignition at TIG under V99/PRO, Average-G N40 counts down, auto-cutoff within tolerance vs `/sim` truth | ☐ | actuation seam; **calibrate THRUST lbf/pulse** (config) vs commanded thrust; **verify jet-table signs** (DAP fighting itself = wrong sign) |
| M-D "Landing" | Pre-PDI `agc-padload --statevec` uplink; V37E63E → PRO at TIG → DPS 10% + throttle-up; LR locks (ALT/VEL lamps out), V57 accept, N63 ΔH converges; P64 pitchover + N64 LPD; P66 ROD to touchdown near the `Apollo11` landmark; P70 abort-to-orbit demo | ☐ | the whole point; **verify LR select-code table + slant-range beam** (N63-vs-`/sim/altitude/radar` telemetry in `agc log`); 099-new padload cells (GAINBRAK/TCG*/DELTTFAP/V2FG/TAUVERT/LRVF zeroed — V21-load if P63/P65 misbehave) |
| M-E "Ascent & CM" | P12: ABORT STAGE + `ctl/stage` at TIG, APS monitored ascent to target orbit; `agc start cm` → Comanche055 P00/V16N36 on :19697, `dsky --cm` lamp set | ☐ | staging path; CM mode |
| embedded | `agc start lm --agc=embedded` (built `--features embedded`): M-B and M-C repeat identically; pausing the game freezes V16N36 exactly; kill + restart resumes from the auto core dump | ☐ | A6 exit criteria |
| system | `apollo11-system` generated + selected: Moon ~389,000 km at t≈+273,000 s (LOI), crescent moon over the pad at t=0, `Apollo11` landmark at the padload site | ☐ | epoch placement sanity |
# Paint live KSA checklist (2026.8.19.5261)

- [ ] Disabled boot: no gatOS paint Harmony owner, stock shaders/material indices, zero clones/work.
- [ ] Enable parts through each of 9P, HTTP field POST, MQTT `/set`, and MCP; status becomes active
  after deferred rebuild and raster compile count advances.
- [ ] Static/dynamic top-level parts and subparts: global/template/vessel/instance precedence; all
  multiply/tint/replace modes; exact black sentinel; staging and vessel split/join semantics.
- [ ] Raytraced IVA shader when available; glass remains stock; frost/wetness/emissive/temperature
  variants still compile.
- [ ] Disable parts restores stock; re-enable restores retained rules; explicit clear removes them.
- [ ] With standalone humble-arteest active, enabling refuses with conflict and does not partially patch.
- [ ] Force an anchor/compile failure: stock fallback, degraded status, no render-thread exception.
- [ ] EVA shared and individual precedence for body/fur/helmet/visor/MMU; sclera/cosmetics stay stock.
- [ ] EVA disable, despawn, scene change, and avatar replacement restore exact captured handles.
- [ ] Repeated colours share clones; changed/cleared rules free unused handles; cap returns a clean
  degraded state without exhausting KSA's 512-entry material pool.
- [ ] Interop: if another mod changes a bound slot, gatOS conditional restore does not overwrite it.
- [ ] Soak enable/disable/rebuild and EVA churn while watching Vulkan validation and clone counters.

# Custom clutter textures live KSA checklist (2026.8.19.5261)

- [ ] **Risk #1, unvalidated by construction — the out-of-band submit.** A bind decodes and uploads
  through a discrete `stagingPool.Submit().Wait()` (the same shape `ThugLifeTextureFactory` already
  ships) while frames are in flight, and `FrameCapture`'s header states that a private command buffer
  submitted alongside in-flight frames "corrupts the device and crashes the game". The reconciliation
  — that the rule governs per-frame work touching in-flight frame resources, while this is a one-shot
  upload to a fresh image nothing has bound yet — is **reasoning, not evidence**. Bind repeatedly
  during heavy scene load, during time warp, and while `/sim/display` is streaming; watch for device
  loss. If it proves unsafe, the fallback is to record the upload in-band at the `DisplayRenderPatch`
  injection point and complete the bind a frame later.
- [ ] Inert boot: with `paint_textures_enabled = true` but nothing ever bound, the feature costs one
  `Revision` comparison per frame — no KSA API is touched, `status` reads `bound=0 applied=0`, and
  frame time is indistinguishable from a build with `paint_textures_enabled = false` (which removes
  the subtree entirely). There is no runtime master switch to check, by design.
- [ ] `cat /sim/paint/textures/clutter` on a loaded flight lists real stock textures with plausible
  `slot`/`WxH`/mip/`used_by`/ecotype columns; on a scene with no ground clutter it is empty rather
  than an error, and `/sim/status/accessors` shows no new degraded latch
  (`paint.clutter_catalog`, `paint.texture_upload`).
- [ ] **Transport parity — bind and unbind the same texture through each of the four transports** and
  confirm the visual result and the `bindings`/`applied` read-back are identical every time:
  9P (`echo '<id> rock.png' > /sim/paint/textures/bind`), HTTP field POST to
  `/v1/fs/paint/textures/bind`, MQTT publish to `gatos/sim/paint/textures/bind/set`, and MCP
  `gatos.paint_control(operation:"texture_bind", target, file, value)`. Repeat with the explicit
  `raw` mode (`value:1` over MCP, the third token everywhere else) and confirm the mode survives
  every transport identically. Uploads themselves are the documented
  exception: 9P `cat > file/<name>`, HTTP `PUT /v1/paint/texture/file/<name>`, MCP
  `gatos.paint_texture(operation:"upload")` — **MQTT carries no binary upload**.
- [ ] The HTTP upload route rejects an oversized body with **413 / EFBIG** when `Content-Length`
  exceeds the server's 1 MiB request cap, rather than silently committing an empty file; chunked
  `?offset=N&complete=0|1` uploads of a real multi-MiB PNG commit correctly and `?complete=1` flips
  `ready`. The `/v1/paint/textures/...` alias behaves identically to `/v1/paint/texture/...`.
- [ ] Multiple simultaneous bindings: bind three different uploads over three different stock
  textures (ideally diffuse + normal + PBR of the same material) at once. All three render, `applied`
  shows three `applied` rows, `vram_bytes` is the sum, and unbinding one leaves the other two intact.
- [ ] Re-upload of a bound file: overwrite `file/rock.png` with different bytes while it is bound.
  The version bumps, the new image reaches the GPU without an explicit re-bind, and the previous
  image goes through the retire queue rather than being destroyed inline.
- [ ] `rm /sim/paint/textures/file/rock.png` while bound: the binding is torn down first, the stock
  texture reappears, `bindings` loses the row, and no orphaned VRAM remains in `status`.
- [ ] Global teardown through **both** spellings: `echo all > unbind` and `echo 1 > clear` each
  restore every stock texture, leave the uploads in place (so a re-bind needs no re-upload), and
  produce identical `status`/`bindings`/`applied` state — they normalize to the same
  `paint.texture_clear` action, and any divergence is a bug.
- [ ] Mod unload / scene teardown with bindings live: every stock `ImageView` is written back, the
  device idles, the retire queue drains, and the game keeps rendering stock clutter with no leak and
  no validation error. Repeat with a bind still `pending` (bound before a renderer existed).
- [ ] **Deferred destruction under the Vulkan validation layers.** Run the whole bind/unbind/re-bind
  cycle with validation enabled and confirm zero `VkImage` destroyed-while-in-use errors: images are
  queued by `Restore` and disposed only after `MaxFramesInFlight + 1` ticks, never inline. Also
  confirm `retiring` in `status` returns to `0` and does not grow monotonically under churn.
- [ ] Shared-asset behaviour: pick a catalog row with `used_by` > 1, bind it, and confirm **every**
  material listed in its `ecotypes` column changes together — the documented granularity, not a bug —
  and that unbinding restores all of them.
- [ ] **`faithful` renders the authored colour, in any biome.** Bind a known-colour PNG (flat
  `#808080`, `#FF0000`, and pure white patches) with the default mode — `echo '<id> swatch.png' >
  bind`, no third token — and screenshot the clutter it draws in **two different biomes** (for
  example Earth grassland and a desert/scree ecotype, or two bodies). The rendered swatches must
  match the authored colours and must match **each other**: identical pixels across the two biomes is
  what proves the terrain tint really was cancelled by clearing alpha, not merely reduced. The image
  must not read ~2× too bright. `bindings` shows the row's third column as `faithful`.
- [ ] **`raw` reproduces the stock convention.** Re-bind the same file with `raw`
  (`echo '<id> swatch.png raw' > bind`): the revision bumps and the image re-uploads even though the
  pair is unchanged, the mid-grey `#808080` patch now reads as *unmodulated* rather than as the grey
  it was authored as, the white patch reads far too bright, and an `A=255`
  swatch visibly takes the per-instance terrain tint — different in each of the two biomes above.
  Confirm a genuine stock-texture replacement (a re-exported dump of a stock clutter texture) is
  visually indistinguishable from stock in `raw` and *not* in `faithful`. `bindings` reads `raw`, and
  the row is still echo-symmetric with `bind`.
- [ ] **A decode that cannot be corrected fails loudly and names the fix.** Bind an image whose
  decode is not RGBA8 (a BC-compressed `dds`, an untranscodable `ktx`, or an `hdr`) in the default
  `faithful` mode: the reconcile leaves the stock texture drawn and `applied` carries a `failed` row
  whose error names mode `raw` as the fix. Re-binding the same file with `raw` then succeeds. (The
  check runs at reconcile, so it surfaces as a `failed` row and a `paint.texture_upload` health
  fault, **not** as an errno on the `bind` write.)
- [ ] Remaining shader behaviour, visually: `A=255` in `raw` visibly pulls the per-instance terrain
  tint in while `A=0` keeps the exact texture colour, and real cutout opacity still comes only from
  the separate `opacity` slot. Confirm the generated mip chain kills aliasing at distance in both
  modes.
- [ ] Cap errnos, each from a real write: `EINVAL` (bad name, unparseable `bind` line — including a
  third token that is neither `faithful` nor `raw` — non-image bytes), `ENOENT` (unknown stock texture id, or a bind naming no upload), `EBUSY` (bind of an
  uncommitted upload), `ENOSPC` (file-count, total-byte, and binding caps), `EFBIG` (per-file cap,
  carried by the failing `write(2)` mid-write), `EEXIST` (9p `Tlcreate` of a taken name), `EPERM`
  (`mkdir`/`rename` inside `file/`). An upload larger than `paint_texture_max_dimension` is
  **downscaled**, not rejected.
- [ ] Soak: bind/unbind/re-upload churn across scene changes, vessel switches, time warp, and
  `/sim/display` streaming for an extended session while watching Vulkan validation, `vram_bytes`,
  `retiring`, the two health latches, and frame time for drift.

# Stickers live KSA checklist (2026.8.19.5261)

`/sim/paint/stickers` is code complete and **entirely unvalidated in a live session**. It is gatOS's
second render-thread draw injection and its first pass that samples the scene depth buffer, so
almost every item here is about the renderer rather than the surface.

**Run `echo 1 > /sim/paint/stickers/debug` first on any failure below.** It draws every sticker as a
magenta 8×8 checker of its projection box instead of its image: if the checker lands in the right
place at the right size, the box, the reverse-Z depth reconstruction, the NDC convention and the ego
matrices are all correct, and whatever is wrong is in sampling or lighting instead.

- [ ] **Zero-sticker steady state.** With `paint_stickers_enabled = true` and nothing ever placed,
  `cat /sim/paint/stickers/info` reads `stickers=0 live=0 images=0 vram_bytes=0 patch=0
  renderer=idle`: no Harmony patch on `RenderTarget.ResolveAttachments`, no pipeline, no descriptor
  pool, no image. The whole per-frame cost is `StickerManager.IsEmpty`. Confirm frame time is
  indistinguishable from a build with `paint_stickers_enabled = false` (which removes the subtree
  entirely), and that `/sim/status/accessors` shows no `paint.sticker_renderer` or
  `paint.sticker_texture` latch. **In the same pass, re-run the clutter-texture checklist's
  bind/unbind/re-upload items**: S0 moved the decode → `SimpleVkTexture` → retire-ring path out of
  `ClutterTextureBridge` into the shared `UserTextureGpu`, and `/sim/paint/textures` behaviour must
  be *unchanged* by that refactor. The out-of-band `stagingPool.Submit().Wait()` flagged as item #1
  of that checklist is now shared by both consumers — and stickers add a second instance of it in
  `StickerDecalRenderer.BuildGeometry` — so a device loss there indicts both features at once.
- [ ] Upload a 512² PNG **with a real alpha channel** through
  `cat meow.png > /sim/paint/textures/file/meow.png`. `cat /sim/paint/textures/files` shows it
  `ready`, and once a sticker uses it `cat /sim/paint/stickers/info` reads `images=1` with a
  plausible `vram_bytes` (RGBA8 + mips). Sticker images are capped at **2048** on the longest edge
  independent of `paint_texture_max_dimension`, and are uploaded **uncorrected** — the clutter
  `faithful` colour correction would be actively wrong here, because the decal shader decodes sRGB
  itself and alpha is real opacity.
- [ ] **Body anchor at the pad.** `echo 'meow.png body Earth <lat> <lon> w=5 h=5' >
  /sim/paint/stickers/place` puts a decal on the ground that hugs the terrain rather than floating
  or z-fighting. Then: it **rides the planet's rotation** (watch it stay put relative to the pad
  across a long time warp, not relative to the camera), it survives a vessel switch and a scene
  reload, and `cat /sim/paint/stickers/0/live` stays `1` throughout. The CPU terrain sample is
  `accurate: false` and the GPU adds tessellation displacement the CPU never sees, so the
  projection-box depth is what absorbs the difference — check the default `d=1` is enough on rough
  ground and that raising it fixes any decal that punches through.
- [ ] **Vessel anchor by spray.** Aim at a fuel tank and `echo 'meow.png w=2 h=2' >
  /sim/paint/stickers/spray`. The decal **conforms to the cylinder** (it is a projection, not a
  quad), `cat last` reports `<id> vessel <vessel-id> part <iid> hit <dist>m`, it stays welded to the
  hull through flight, rotation and staging of *other* parts, and a decal sprayed onto a **gimballing
  engine bell or a robotics segment follows that sub-part** — `spray` anchors to the sub-part
  `RayCastEgo` returns, which is exactly what makes that work. Confirm `aim=cursor` hits what the
  mouse is over and that a miss returns **ENOENT** with `last` reading `no hit within <range>m`.
- [ ] **Ground clutter is painted by the projection.** Place a wide sticker across a rock/scree
  field. The rocks and grass standing inside the projection box are painted too, even though clutter
  has no CPU-addressable transform and **cannot be aimed at** — a `spray` ray passes through a rock
  to the terrain behind it. This is the single best proof that the depth reconstruction is right.
- [ ] **Image lifecycle.** Re-upload the same name with different bytes → every sticker using it
  hot-swaps on the next tick (the binder is keyed by content version) and the old GPU image goes
  through the retire queue, not an inline destroy. `rm /sim/paint/textures/file/meow.png` → the
  stickers go **dormant, not deleted**: `live=0`, `texture=missing`, the entries and their `spec`
  lines survive, and `info` reports `patch=0` once the last live one goes. Re-upload → they come
  back on their own.
- [ ] **Teardown on the last live sticker.** `echo 1 > /sim/paint/stickers/0/remove` (or
  `echo 1 > clear`) with nothing else live: `patch=0 renderer=idle`, the Harmony postfix is gone,
  and the pipeline/mesh/descriptor pool are destroyed only after `GraphicsAndCompute.WaitIdle()`.
  Run the whole place/remove cycle **under the Vulkan validation layers** and confirm zero
  destroyed-while-in-use errors, then place again and confirm the GPU path comes back up cleanly.
- [ ] **Render-target churn.** With a sticker live, toggle MSAA on and off, change resolution, and
  switch CMAA2 on and off mid-session. `ResolveAttachments` does nothing when neither attachment is
  multisampled but the postfix fires either way, and the depth descriptor is a per-frame ring
  rewritten from the live `DepthImage.ImageView` — so the decal must stay aligned and nothing may
  crash. This is the item most likely to find a bug.
- [ ] **Lighting sanity.** The shader's lighting is an approximation: sun dot product plus
  `0.12 * planetColor + 0.02` ambient, times `brightness`. Check a sticker on the night side or in a
  planet's shadow is dim but not pure black, that one in direct sun is not blown out, and that
  `brightness` in `(0, 8]` is a usable correction either way. `planetColor` is zero for an airless
  body or a camera in shadow — the small constant is what keeps those readable.
- [ ] **F2 (UI hidden) still draws the sticker.** The pass injects into the scene's colour image
  before any UI compositing, so hiding the game UI must not hide decals.
- [ ] **Crew-cam portraits and secondary viewports are unaffected.** The postfix filters on both
  `__instance == Program.OffscreenTarget` and `Program.RenderedViewport == Program.MainViewport`;
  stickers are main-viewport-only in v1 and a portrait rendering a sticker (or crashing) means the
  identity check is wrong. Also confirm the map view and any extra window are clean.
- [ ] **Unload with stickers present.** Tear the mod down with several live stickers on both anchor
  kinds: `Active` clears, the postfix unpatches, the device idles, the pipeline and every sticker
  image are destroyed, the bindless slots are returned with `FreeTexture`, and KSA keeps rendering
  with no leak and no validation error. Repeat while `/sim/display` is streaming and during heavy
  scene load.
