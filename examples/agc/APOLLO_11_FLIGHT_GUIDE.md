# Flying Apollo 11 in KSA — the gatOS flight guide

*You are going to fly the actual Apollo 11 mission profile, in Kitten Space Agency, with the
actual Apollo 11 lunar-module flight software — Luminary099, the code that landed on
1969-07-20 — executing on a real AGC emulator inside your gatOS guest, reading your ship
through `/sim` and firing your ship's engines back through it. This guide chains every gatOS
capability into one mission: the Apollo-epoch solar system, the guest Linux toolbox, the AGC
stack, the DSKY in a purrTTY tab (or welded to your cockpit), the landing radar, the digital
uplink, and the descent to Tranquility.*

> Prerequisites: gatOS installed and working (a purrTTY tab gets you a `gatos:~#` prompt and
> `ls /sim` shows live data). Skim `README.md` in this folder for the AGC stack itself and
> `apollo11-system/README.md` for the 1969 system. Everything below happens **in the game**
> or **inside the guest**.

---

## 0. Mission at a glance

| Phase | Historical time (sim t, s) | You fly it with | AGC program |
|---|---|---|---|
| Launch to LEO | 0 → +720 | KSA vessel + your hands (or `ctl/` scripts) | — (P11 monitor was CM-side) |
| TLI (trans-lunar injection) | +9,930 | `ctl/burn` or hand-flown | — |
| Coast + MCC | +9,930 → +273,000 | `ctl/attitude_mode`, time-warp | P00 on the LM AGC (powered up en route) |
| LOI-1/LOI-2 (lunar orbit) | +273,000 | `ctl/burn` | — |
| Undock + DOI | +361,000 | RCS translate + P30/P40 | P00 → P30 → P40 |
| **PDI → touchdown** | +367,000 → **+369,940** | **Luminary099** flies, you monitor & ROD | **P63 → P64 → P66** |
| Surface ops | — | your kitten + the downlink | P68, P12 prep |
| Ascent + rendezvous | +412,000 | P12, then `ctl/burn` | P12 |
| TEI + entry | +506,000 | `ctl/burn` | (Comanche055 optional) |

The historical timestamps assume the Apollo-11-epoch system (sim t=0 = 1969-07-16 13:32 UTC).
You are not graded on them — but if you keep them, the Moon is exactly where NASA needed it.

---

## 1. Set the stage

### 1.1 The July 1969 sky

Generate and select the **Apollo 11 (July 1969)** system (see
[`apollo11-system/README.md`](apollo11-system/README.md)):

```sh
bun apollo11-system/make-apollo11-system.ts --content <KSA>/Content/Core --out <KSA>/Content/Apollo11
```

Start KSA, pick *Apollo 11 (July 1969)* in the system selection. Sim t=0 is launch morning:
the Moon is 2 days past new (a thin crescent over the pad at dawn), 11 days from perigee, and
will be ~389,000 km away when you arrive — just as it was.

### 1.2 The ships

Build (or grab from your hangar) a three-piece stack in the editor:

- **S-IVB stand-in**: any upper stage with ~3,000 m/s of ΔV for TLI.
- **Columbia (CSM)**: engine ≈ SPS class, docking port, RCS with rotation + translation maps.
- **Eagle (LM)**: this one matters. Two stages:
  - *Descent stage*: one gimbaled engine, **max thrust ≈ 45 kN** (DPS class), deep-throttle
    to ~10% (`echo 0.10 > /sim/vessels/active/engines/<n>/min_throttle` — writable at runtime),
    landing legs, ~8,200 kg propellant on ~6,800 kg dry (the honest LM envelope — but ANY
    lander flies: the AGC only believes what the padload tells it, and V48/N47 trim in flight).
  - *Ascent stage*: fixed engine ≈ 16 kN, its own RCS **with both rotation and translation
    control maps on all axes** — the AGC's DAP speaks through `ctl/rotate`/`ctl/translate`.
- Decoupler between stages (ABORT STAGE = `ctl/stage`).

Name the LM `Eagle`. You'll thank yourself when six vessels are in flight.

### 1.3 The computer

One-time, inside the guest (raise `memory_mb` to 1024+ in `gatos.toml` for the build):

```sh
gatos:~# apk add --no-cache build-base cmake git cargo rust bun
gatos:~# git clone --depth 1 https://github.com/alex-sherwin/virtualagc /opt/src/virtualagc
gatos:~# git clone --depth 1 https://github.com/meow-sci/gatOS /opt/src/gatos   # or use a /mnt mount
gatos:~# cd /opt/src/gatos/examples/agc
gatos:agc# ./tools/build-agc.sh          # yaAGC + yaYUL + BOTH ropes, checksum-verified
gatos:agc# ./tools/install-agc.sh        # → /opt/agc, `agc` + `dsky` on PATH
```

`build-agc.sh` hard-fails unless the assembled Luminary099 is **byte-identical** to the
archival `.binsource`. When it passes, the code in `/opt/agc/rom/Luminary099.bin` is, bit for
bit, the program that landed Eagle.

### 1.4 The cockpit

- Minimum: two purrTTY tabs — one shell, one `dsky`.
- Proper: weld a display quad by the pilot seat and park the DSKY tab on it (the welds +
  `/sim/display` recipe — see `examples/welds/` and the docs site). Add a third tab with
  `dsky` on the CM port later for Columbia.
- Sound: `dsky --audio` uploads key clicks + MASTER ALARM through `/sim/audio`, so alarms come
  out of the game's speakers.

---

## 2. Launch and translunar coast (your hands, gatOS's files)

Luminary is a lunar-operations program — boost was Saturn/CM business. Fly to orbit however
you like; the gatOS way from a second tab:

```sh
# a 60-second gravity-turn sketch — the real thing is yours to write (see the site tutorials)
echo manual > /sim/vessels/active/ctl/attitude_mode
echo 1   > /sim/vessels/active/ctl/ignite
echo 1.0 > /sim/vessels/active/ctl/throttle
watch -n1 'cat /sim/vessels/active/altitude/baro /sim/vessels/active/velocity/orbital'
```

Target the historical parking orbit: **~185 km, 32.5° inclination**. Then TLI at t≈+9,930 s —
compute your transfer (the `gogogo`/`land-o-matic`/site tutorials cover `ctl/burn`):

```sh
echo "<ut> <dvx> <dvy> <dvz>" > /sim/vessels/active/ctl/burn    # KSA's FC executes at ut
```

Coast tips: `echo 2000 > /sim/time/warp` eats the three days; the AGC stack holds its feeds
during warp automatically and resyncs at 1× (you'll see `hold=Warp` in `agc log`). Transpose
and dock Columbia to Eagle the way you'd dock anything (RCS + `ctl/translate`, watching
`docking/`).

## 3. Power up Eagle's brain (en route, day 2)

Switch control to the LM (`echo Eagle > /sim/ctl/control_vessel` or the game UI), then:

```sh
gatos:~# agc start lm
== KSA-universe audit (moon vs Luminary099 rope constants) ==
  mu (m^3/s^2)     KSA 4.904870e12  rope 4.902778e12  err 0.04%  [OK]
  radius (m)       KSA 1.737100e6   rope 1.738090e6   err 0.06%  [OK]
  rot rate (rad/s) KSA 2.661700e-6  rope 2.661700e-6  err 0.00%  [OK]
  VERDICT: GREEN
padload: 47 cells → /var/lib/agc/lm/padload.core ...
== yaAGC (lm) on :19797
== agc-bridge (lm, extern mode)
```

That padload was computed **from your live universe**: your LM's real mass into N47, the KSA
moon's real radius under `RLS`, the moon's real hour angle into the orientation cells. The
audit is your guarantee Luminary's fixed constants agree with the world you're flying in.

In the DSKY tab (`dsky`):

| You type | You see |
|---|---|
| `v35⏎` | every segment and lamp blazes — lamp test |
| `v16n36⏎` | the mission clock, ticking on the AGC's own TIME counters |
| `v36⏎` | fresh start; `v37⏎00⏎` → P00 idle |
| `r` | RSET — clears the boot RESTART lamp |

The ISS (platform) needs its authentic **90-second turn-on**: the bridge sequences it from the
`imu_operate` switch (on by default; watch NO ATT clear). Then align:

```sh
gatos:~# agc align      # REFSMMAT uplink (watch UPLINK ACTY flicker) + V41N20 coarse align
```

`v16n20⏎` now shows the gimbal angles. **Tumble the ship with the rotation keys — the
needles track.** That's the whole virtual-IMU seam (CDU counts at 39.55″ each, PIPA ΔV at
5.85 cm/s a pulse) working end to end. This is mission card **M-B**; do it before you trust
the machine with your descent.

## 4. LOI and DOI (t ≈ +273,000 … +366,000)

Brake into lunar orbit with `ctl/burn` (LOI-1 ≈ 890 m/s retrograde at ~110 km pericynthion;
LOI-2 circularizes at ~110 km). Undock Eagle. In the LM, drop pericynthion to **~15.2 km**
above the site (DOI, ≈ 22 m/s retrograde on the far side — P30 will happily display it if you
key the target in; or write `ctl/burn` and let KSA's FC do the housekeeping burn).

Before PDI, refresh the AGC's world model:

```sh
gatos:~# agc-padload --statevec=/tmp/sv.keys     # V71 state-vector block from live /sim
gatos:~# agc uplink /tmp/sv.keys                 # UPLINK ACTY flickers; V33E accepts inside
```

And load the DAP: `v48⏎` (R03) — accept/trim the N46/N47 values (the padload seeded your real
masses). `v76⏎`/`v77⏎` pick min-impulse vs rate-command if you hand-fly.

## 5. THE DESCENT (t ≈ +367,000): P63 → P64 → P66

Cockpit state: descent engine **armed** (`echo 1 > /run/agc/switches/eng_arm_desc` — or flip
it on the `dsky` Tab-panel), **auto throttle** on, **LR power** on, attitude mode `manual`
(the AGC is the autopilot now — KSA's own FC must not fight it; the bridge's RCS/gimbal
commands ride `ctl/rotate` + `ctl/translate` in one atomic `ctl/batch` per tick, W1's whole
reason to exist).

```
V37E63E          P63 BRAKING. N33 flashes TIG — PRO if you like it.
                 At TIG−35 s: ullage; TIG: DPS lights at 10% (the bridge is clocking the
                 THRUST counter), throttle-up at +26 s — watch /sim/vessels/active/ctl/throttle
                 follow the AGC's pulse trains.
V16N63E          ΔH / H-dot / H while braking.
                 ~13 km alt: LR locks — ALT/VEL lamps out, N63's ΔH shrinks as radar
                 updates converge the state vector.
V57E             ACCEPT LR updates (the real crews did exactly this).
                 1202-style PROG alarms? RSET and keep flying — it's the same executive.
P64 (auto)       pitch-over at ~2.2 km "high gate"; N64 shows LPD angle + time.
V16N68E          slant range / TG / velocities on the way in.
V37E66E (or ROD) P66 rate-of-descent at ~150 m: the DSKY panel's ACA nudge keys (or the
                 real +/− on N92) command ±0.3 m/s steps; kill horizontal drift, watch
                 the shadow, and set her down.
CONTACT          engine stop (b14 edge → ctl/shutdown). P68 confirms. Tranquility Base.
```

Everything in that block is Luminary099 logic — the bridge only translated electrons:
CDU/PIPA counts in, jet/throttle pulses out, LR words raced into RNRAD inside the 9-gate
window with the rope's own scale factors (1.079 ft/bit low range; −0.644/1.212/0.8668
ft/s/bit beams).

**If it goes wrong:** ABORT (`/run/agc/switches/abort`) → P70 flies you back up on the DPS;
ABORT STAGE (`abort_stage`, then `ctl/stage` when the panel calls for it) → P71 on the APS.
Both are padloaded and live.

## 6. On the surface

- `v16n43⏎` — lat/lon/alt; compare with `cat /sim/vessels/active/position/{lat,lon}`. The
  landing dispersion from Tranquility (0.674°N 23.473°E) is your score.
- The landed PIPAs integrate +1 g up — `v16n40⏎`'s ΔV accumulates exactly like a real ISS
  sitting on regolith (velocity differencing makes pad contact honest).
- Watch mission telemetry the ground saw: `agc log lm downlink | jq .` — 50 word-pairs/s of
  the descent-ascent downlink list, decoded names included.
- Plant something. There's an `Apollo11` landmark under you.

## 7. Ascent and home

- `v37e12e` — P12: N74 targets (the padload's `HIASCENT` already knows your staged mass),
  ENTER, PRO, **ABORT STAGE + `ctl/stage`** at TIG, and the APS+DAP fly the monitored ascent
  to a ~17 × 84 km orbit.
- Rendezvous with Columbia your favorite way (`ctl/burn` + RCS; P20's rendezvous radar is the
  one seam deliberately out of scope). Dock, transfer the kittens, cut Eagle loose.
- TEI at t≈+506,000 (~1,000 m/s prograde on the far side), coast home. If you want the CM
  experience for entry monitoring: `agc start cm` boots **Comanche055** on :19697 —
  `dsky --cm` gives you Columbia's panel (P00/V16N36/P52 work today; the CM bridge profile is
  the documented follow-on).
- Splash down (KSA oceans are real — aim for water).

## 8. If something misbehaves

| Symptom | Look at |
|---|---|
| DSKY dark / NO AGC banner | `agc status`, `agc log lm agc` (yaAGC log) |
| lamps look wrong after reconnect | ch 0163 is change-only — press any key once |
| N20 doesn't track the ship | `agc log` for `hold=`, IMU `saturated` (rate > 19.5°/s outruns real CDUs too), redo `agc align` |
| burns land off-target | audit verdict? N47 masses (V48)? `thrust_pulses` in `agc log` vs `ctl/throttle` |
| writes fail EACCES/ETIMEDOUT | authority gating / paused game — the errno vocabulary is SPEC §5 |
| paused/warped game confused the clock | extern mode resyncs (V55 trim) on resume; embedded mode (`agc start lm --agc=embedded`, built with `--features embedded`) freezes the mission clock perfectly instead |

*The checklists behind this guide (M-A … M-E) live in `docs/VALIDATION.md`; the design and
every verified interface fact live in `plans/AGC_PLAN.md`. Godspeed.*
