# agc — the Apollo Guidance Computer, running for real, inside gatOS

The **flown Apollo 11 flight software** — Luminary099 for the LM, Comanche055 for the CM —
executing on the mature Virtual AGC emulator (`yaAGC`) as an ordinary Linux process **inside
the gatOS guest**, flying your KSA vessel through `/sim`. A Rust **bridge** converts live
telemetry into the electrical world the AGC expects (IMU gimbal counts, PIPA ΔV pulses,
landing-radar words, cockpit discretes) and converts the AGC's outputs (RCS jet channels,
engine on/off, throttle pulse trains) back into `/sim` control writes. The **DSKY** is a
ratatui TUI speaking the standard yaAGC socket protocol — run it in a purrTTY tab, or park
that tab on an in-world cockpit quad. You fly P63→P66 to a lunar touchdown on the code that
landed on 1969-07-20.

> Design + verified research base: [`../../plans/AGC_PLAN.md`](../../plans/AGC_PLAN.md).
> **Mission manual: [`APOLLO_11_FLIGHT_GUIDE.md`](APOLLO_11_FLIGHT_GUIDE.md)** — the full
> Apollo 11 profile, launch to splashdown. The July-1969 Earth/Moon system:
> [`apollo11-system/`](apollo11-system/).

## Status

- ✅ **A0 toolchain** — `tools/build-agc.sh` builds yaAGC/yaYUL from a Virtual AGC checkout and
  assembles both ropes, hard-failing unless **byte-identical** to the archival `.binsource`
  (verified: Luminary099 + Comanche055 both reproduce).
- ✅ **A1 protocol + DSKY** — wire codec (golden-packet + resync tests), `SocketPort`
  (10-slot scan, keepalive liveness, reconnect), the ratatui DSKY face with panels.
  Live-tested against a real yaAGC running Luminary099: V35 lamp test, V16N36 clock.
- ✅ **A3 bridge core** — virtual IMU (`q_sm`, CDU emission with real FIFO pacing, coarse/fine
  align, ZERO CDU), PIPA velocity-differencing integrator, ch 030-033 discretes +
  `/run/agc/switches/*` cockpit files with the authentic 90 s ISS turn-on, padload generator
  (cell addresses verified in the yaYUL listing; values from the Luminary069 template; core
  format **proven by resuming a live yaAGC from it**), KSA-universe fit audit, P27/V71
  digital uplink incl. the REFSMMAT block.
- ✅ **A4 actuation** — ch 5/6 jet-duty demodulation → sigma-delta bang-bang `ctl/batch`
  {`rotate` (the W1 gatOS control), `translate`}; ENGINE ON/OFF edges behind the arm switch;
  client-clocked THRUST DINC loop → `ctl/throttle`.
- ✅ **A5 landing radar** — antenna position state machine, data-good/low-scale discretes,
  15-bit MSB-first SHINC/SHANC delivery, the rope's own scale factors
  (`CONTROLLED_CONSTANTS.agc`: 1.079 ft/bit range, −0.644/1.212/0.8668 ft/s/bit beams,
  12288-count velocity bias).
- ✅ **A6 embedded mode** — `--features embedded` links `agc_engine.c` in-process (compiled
  from your Virtual AGC checkout, never vendored): pausing the game **freezes the mission
  clock exactly**, `RequestRadarData` becomes a synchronous hook, and a built-in server keeps
  DSKYs mode-blind. Live-tested: boots Luminary099, freeze/thaw bit-exact.
- ✅ **A7 polish** — downlink NDJSON recorder with Luminary099 list names, `agc verbs` card,
  this README + the flight guide + `docs/VALIDATION.md` mission cards.
- ⚠️ The **in-game** mission cards (M-A … M-E, `docs/VALIDATION.md`) are pending a live KSA
  flight, as is calibration of the flagged `[impl-verify]` items (THRUST pulse weight, LR
  select-code table, jet-table signs, the 099-new padload cells).

## Data interface

Reads: the atomic `vessels/active/telemetry` doc (+ `attitude/rates`, `bodies/<parent>/*`,
`time/sim_dt`). Writes: `ctl/{ignite,shutdown,throttle,batch}` with the batch carrying
`rotate` + `translate` signs (Frame phase, one tick, atomically). The AGC is the autopilot —
KSA's own flight computer stays in `manual`. Pause/warp/stale gating follows the land-o-matic
M6 precedent; extern mode resyncs the mission clock on resume (V55), embedded mode never needs
to.

## Build & run (in-guest)

```sh
apk add --no-cache build-base cmake git cargo rust
git clone --depth 1 https://github.com/alex-sherwin/virtualagc /opt/src/virtualagc
cd examples/agc            # via /mnt host mount, git clone, or cp
./tools/build-agc.sh       # toolchain + ropes (checksum-verified) + cargo build --release
./tools/install-agc.sh     # → /opt/agc, `agc` + `dsky` on PATH

agc start lm               # padload from live /sim (audit printed) → yaAGC :19797 → bridge
dsky                       # in its own purrTTY tab, or on a cockpit quad
agc align                  # REFSMMAT uplink + V41N20 coarse align
agc status | agc log [lm] [agc|bridge|downlink] | agc uplink FILE | agc verbs | agc stop
```

Raise `memory_mb` (512+, 1024 while compiling) in `gatos.toml` for the one-time build;
runtime is featherweight — yaAGC is an 85 kHz machine. `agc start lm --agc=embedded` selects
embedded mode when built with `cargo build --release --features embedded`.

## Host-side dev

Everything builds and tests off-game: `cargo test` (51 unit tests — codec golden packets,
IMU/PIPA conservation properties, padload encodings pinned to known octals, radar word
quantization). With a Virtual AGC checkout:

```sh
(cd /path/to/virtualagc && cmake -S . -B build && cmake --build build --target yaAGC yaYUL oct2bin)
(cd /path/to/virtualagc/Luminary099 && ../build/yaYUL/yaYUL --unpound-page MAIN.agc > MAIN.agc.lst)
AGC_IT=1 YAAGC=…/build/yaAGC/yaAGC ROPE=…/Luminary099/MAIN.agc.bin cargo test --test live_yaagc
VIRTUALAGC=/path/to/virtualagc ROPE=… cargo test --features embedded
```

The bridge/dsky also run against a fixture (`--root ./fixture`) or the HTTP mirror
(`--url $GATOS_HTTP`).

## Keys (dsky)

`0-9 v n + -` · `⏎` ENTR · `c` CLR · `k` KEY REL · `r` RSET · `p` PRO (600 ms press/release) ·
`P` PRO 6 s hold (standby) · `Tab` panels (dsky → switches → status) · `q` quit.

## No TUI required

The whole stack is ordinary Unix. Raw-shell mission control:

```sh
echo 1 > /run/agc/switches/eng_arm_desc        # cockpit switches are files
agc uplink sv.keys                             # any V71 key script over the digital uplink
tail -f /var/log/agc/lm.downlink.ndjson | jq   # the 50-pair/s telemetry Houston saw
agc log lm bridge                              # hold states, pulse rates, errnos
```

Even the DSKY protocol is 4-byte packets you can hand-craft with `nc` (AGC_PLAN Appendix A).

## Licensing

MIT, matching the mod — for the example code in this folder. yaAGC/yaYUL are GPLv2, built
from the Virtual AGC checkout at install time and never redistributed here; the AGC flight
software itself is public domain. Source-only example; not part of `gatos.slnx` or CI.
