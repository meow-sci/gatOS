# AGC_PLAN — the Apollo Guidance Computer, running for real, inside gatOS

*Authored 2026-07-22. The plan for running the **actual Apollo 11 flight software** (Luminary099
for the LM, optionally Comanche055 for the CM) on the **yaAGC emulator inside the gatOS guest**,
fed live KSA telemetry from `/sim` through a Rust bridge that simulates the LM's sensor/effector
interfaces (IMU, PIPAs, landing radar, throttle, RCS), with a **Rust ratatui DSKY** running in a
purrTTY terminal tab — including on an in-world cockpit quad. The player flies P63→P66 to a
lunar touchdown on code that landed on the Moon on 1969-07-20.*

> **STATUS (2026-07-22):** plan authored; no code yet. Milestones A0–A7 pending. The research
> base below was verified against the three source trees on this date (`meow-sci/gatOS`,
> `alex-sherwin/virtualagc`, `alex-sherwin/apollo-11`), including an out-of-tree build of
> yaAGC/yaYUL and a byte-identical assembly of Luminary099 and Comanche055 against their
> `.binsource` references, plus live wire-protocol probes against a running yaAGC.

---

## 0. Goal

**One paragraph:** gatOS's whole thesis is "real userland instead of fake terminal apps" — so the
Apollo Guidance Computer should be a *real AGC*: the flown Luminary099 rope, executing on the
mature Virtual AGC emulator (`yaAGC`), as an ordinary Linux process **inside the Alpine guest**.
Because the guest is always Linux x86_64 on every host OS, "must work on Linux" is true by
construction — Windows/macOS players get the identical stack. The guest's `/sim` mount is the
spacecraft: a Rust **bridge** process converts live vessel telemetry into the electrical world the
AGC expects (CDU angle counts, PIPA ΔV pulses, radar words, discretes) and converts the AGC's
outputs (RCS jet channels, engine on/off, throttle pulse trains) back into `/sim` control writes.
The **DSKY** is a ratatui TUI speaking the standard yaAGC socket protocol — run it in a purrTTY
tab, or park that tab on an in-world quad next to the pilot's seat.

**The player experience this plan buys:**

```
purrTTY ▸ New Tab ▸ gatOS
gatos:~# apk add --no-cache build-base cmake git cargo rust
gatos:~# git clone --depth 1 https://github.com/alex-sherwin/virtualagc /opt/src/virtualagc
gatos:~# cd /mnt/gatos-examples/agc          # or a git clone of the gatOS repo
gatos:agc# ./tools/build-agc.sh              # yaAGC + yaYUL + Luminary099.bin (checksum-verified)
gatos:agc# ./tools/install-agc.sh            # → /opt/agc, `agc` + `dsky` on PATH

gatos:~# agc start lm                        # padload generated from live /sim; yaAGC on :19797;
                                             # bridge attaches to the controlled vessel
gatos:~# dsky                                # (in its own purrTTY tab, or on a cockpit quad)

  V35E             → every segment and lamp lights (lamp test)
  V16N36E          → mission clock ticking, driven by the AGC's own timers
  V37E00E          → P00 idle
  agc align        → REFSMMAT uplinked over the digital uplink; V41N20E coarse align
  V16N20E          → tumble the ship with RCS: the gimbal-angle display tracks it live
  V37E40E … V99E PRO → BURN, BABY, BURN: the DPS lights under AGC command, N40 counts ΔV
  V37E63E … PRO    → PDI. N63 during braking, LR locks (ALT lamp out), P64 pitchover,
                     P66 ROD to touchdown. Lunar contact, engine off — flown by Luminary099.
```

**Non-goals (this plan):** rendezvous radar / P20 CSM tracking (stretch, A7+); AOT star marks
(broken at the emulator seam — §1.6.7 — alignment goes through REFSMMAT uplink instead); the AGS
(yaAGS/DEDA — noted as a follow-on, the repo has everything needed); multi-vessel simultaneous
LM+CM (works in principle — two yaAGC processes on different base ports — but only the CM-solo
mode is planned); MSFN ground-station realism (the downlink page in A7 is the hook).

**Legend:** facts below marked **[V]** were verified in source/build/wire on 2026-07-22 (§1 cites
where); items marked **[impl-verify]** are believed-correct but MUST be re-derived from the named
source at implementation time, per OS_PLAN §0.1 convention.

---

## 1. Research base (verified 2026-07-22)

### 1.1 The three source trees and their roles

| Repo | Role |
|---|---|
| `alex-sherwin/virtualagc` (checkout `../virtualagc`) | **The toolchain + canonical flight-software source.** yaAGC (emulator), yaYUL (assembler), oct2bin (binsource cross-check), yaDSKY2/piDSKY (DSKY protocol references), `Contributed/LM_Simulator` (the prior-art spacecraft bridge), all mission sources incl. `Luminary099/` + `Comanche055/` **with Makefiles and `.binsource` checksum references**. GPLv2+ tools; the AGC flight software itself is public domain. |
| `alex-sherwin/apollo-11` (checkout `../apollo-11`) | The famous readable mirror (chrislgarry/Apollo-11). **Display/proofreading repo — no build files, and its transcription lags virtualagc's** [V: spot-diffs found virtualagc-side corrections (`STERN` label, `1406POO`, `W.IND1`) absent in apollo-11; headers stop at 2009]. We *cite and read* it (it's the repo people know); we *assemble* from virtualagc. |
| `meow-sci/gatOS` (this repo) | Home of the deliverables: `examples/agc/` (bridge + DSKY + tools) and one small mod-side augmentation (W1, §7.4). |

### 1.2 The AGC, for integrators (one page)

The Block II AGC is a 15-bit (+parity) machine: **~85,333 machine cycles/s** (11.7 µs MCT),
36 fixed banks × 1024 words of rope (73,728 bytes as emulated), 8 × 256 words of erasable, ~11
interrupt vectors, and — the part that matters here — an I/O model with three distinct seams:

1. **I/O channels** (9-bit addresses, 15-bit values): program-visible registers. Outputs like
   ch 005/006 (RCS jets), 010 (DSKY digits), 011 (DSKY lamps + LM engine on/off), 012 (ISS/GN&C
   moding), 013 (radar select + misc), 014 (IMU/gyro/throttle drive enables); inputs like 015
   (DSKY keys), 016 (marks/ROD), 030–033 (discretes, mostly **active-low**).
2. **Counters** (erasable 024–060): updated by *unprogrammed sequences* that steal cycles —
   PINC/MINC (±1, one's-complement: PIPAs), PCDU/MCDU (±1 two's-complement: CDU gimbal/optics
   angles), DINC (diminish-toward-zero, emitting POUT/MOUT/ZOUT pulses: THRUST, TIME6), SHINC/
   SHANC (shift-in-0/1: radar RNRAD, uplink INLINK). This is how the outside world *pushes*
   sensor data in, and how pulse-train outputs are clocked out.
3. **Interrupts**: KEYRUPT1 (DSKY key), UPRUPT (uplink word), RADARUPT (radar word ready),
   DOWNRUPT (50/s downlink pacing), T3–T6RUPT (timers).

The **IMU loop** is the heart of integration: a gimbaled stable member (SM) holds an inertial
orientation ("REFSMMAT" relates it to the reference frame); **CDUs** report gimbal angles to the
AGC as ±1 counts (1 count = 360°/2¹⁵ = **39.55 arcsec**); **PIPAs** on the SM report specific
force as ±1 velocity pulses (**5.85 cm/s each**); the AGC can slew the platform (coarse align via
CDU error counters, ch 012 bit 4 + ch 014 drive bits) and precision-torque it (fine align,
**0.617981 arcsec/pulse**). Luminary navigates *only* from those counters plus radar — feed them
honestly and every program from P00 to P66 works on physics alone.

### 1.3 yaAGC: what we get (all [V], agent-verified in `yaAGC/` source + live probes)

- **Wire protocol**: TCP, 4-byte packets `00pppppp 01pppddd 10dddddd 11dddddd` — 2-bit
  signatures for resync, 8-bit channel + **u** bit (byte0 `0x20`) + counter flag (byte0 `0x10` ⇒
  channel `0200|ctr`), 15-bit value (`agc_utilities.c:86-149`). The **u-packet stores a per-client
  bitmask**; subsequent data writes replace only masked bits (`SocketAPI.c:219-238`) — that's how
  many peripherals share one discrete channel. Counter packets carry the increment **type** as the
  value: `0`=PINC `1`=PCDU `2`=MINC `3`=MCDU `4`=DINC `5`=SHINC `6`=SHANC, `021`/`023` = fast
  PCDU/MCDU (`agc_engine.c:1569-1623`). Servers send `FF FF FF FF` keepalives ~1.5 s (discard;
  doubles as a liveness signal for clients).
- **Ports**: base **19697** (`--port=N`), **10 listening ports, one client each** (base+0…+9);
  VirtualAGC convention: CM base 19697, **LM base 19797**. No handshake; on connect the server
  replays DSKY ch 010 relay rows + current input channels (but **not** fictitious ch 0163 — a
  late-joining DSKY sees lamps only on next change).
- **Inbound rate ceiling**: sockets are polled every `interlace+1` cycles, **one packet per client
  per poll** — default `--interlace=50` ⇒ ≈1,670 packets/s/client. PIPA pulse streams must fit
  (§4.4) or the launcher lowers interlace.
- **Counter recipes** (client → AGC; live-verified): PIPAX ±1 = `(0237, 000/002)` (Y `0240`,
  Z `0241`); CDUX ±1 = `(0232, 001/003)` (fast `021/023`; Y `0233`, Z `0234`) — CDUX/Y/Z go
  through a pacing FIFO applied at **400 counts/s (slow) / 6,400 counts/s (fast)** per axis, ≤128
  queued sign-runs; RNRAD shift-in = 15 × `(0246, 005|006)` MSB-first; uplink word =
  `(0173, word)` → INLINK + **UPRUPT immediately**.
- **Fictitious channels (AGC → clients)**: `0163` composite DSKY lamps/flash (§1.5); `0174/5/6`
  IMU CDU X/Y/Z coarse-align drive bursts (value = `040000·minus | count`, ≤24 pulses/burst every
  ~75 ms while ch 014 bits 15/14/13 set); `0177` gyro fine-align bursts (value =
  `((ch014 & 0740) << 6) | count`, ≤1024 pulses, sign/select in bits: (A,B)=(0,1)→X, (1,0)→Y,
  (1,1)→Z); `0166/0167/0170` LM RHC counters (in); `0171/0172` CM optics dumps; `0200|ctr`
  POUT(015)/MOUT(016)/ZOUT(017) echoes from DINC.
- **THRUST/ALTM/OUTLINK are client-clocked** [V, live-probed]: yaAGC generates *no* autonomous
  DINC trains. When Luminary loads the THRUST counter and raises ch 014 bit 4 (drive activity),
  the *bridge* clocks it: send `(0255, 004)` DINCs; each echo POUT/MOUT is one throttle up/down
  pulse; ZOUT ⇒ counter drained, stop. Same pattern for ALTM (`0260`).
- **Radar handshake**: AGC sets ch 013 bits 1-3 (select) + bit 4 (activity); the engine counts 9
  TIME5-synced gates, then clears bit 4, calls the **`RequestRadarData()` stub** ("expected to
  populate RNRAD") and raises RADARUPT. A socket client watches the ch 013 broadcast and races
  SHINC/SHANC words into RNRAD inside the gate window; an embedded host simply implements the
  stub — it is the designed integration point (`SocketAPI.c:508-516`).
- **DSKY key writes** to ch 015 auto-raise KEYRUPT1. **Ch 016 writes never raise KEYRUPT2/
  MARKRUPT** (no `InterruptRequests[6]` setter exists in the tree) — AOT marks are dead over
  sockets; see §1.6.7.
- **Timing**: `agc_engine()` = one MCT per call; the CLI simulator paces to wall clock with 10 ms
  sleep granularity. **No pause API** in the CLI process; an embedding host controls time
  completely by when it calls `agc_engine()`.
- **Lifecycle**: `MakeCoreDump` writes a **plain-text octal file** (512 channel words, 8×0400
  erasable, CPU state); the simulator auto-dumps every `--dump-time=N` (default 10 s) and by
  default **auto-resumes erasable** from `./core` on start; `--resume=FILE` restores everything;
  `--no-resume` for cold start. The interactive debugger is **on by default** — daemon runs use
  `--nodebug` (or the legacy `--core=FILE` form). Config file `--cfg=` selects `LMSIM`/`CMSIM`
  (`CmOrLm`, needed for CM RHC/optics behavior and downlink decode).
- **Embedding**: `gcc EmbeddedDemo.c NullAPI.c agc_engine.c` is a working minimal build. Host
  provides `ChannelOutput/ChannelInput/ChannelRoutine/ShiftToDeda/RequestRadarData` (+
  `BacktraceAdd` stub), owns the `agc_t` (Erasable/Fixed/InputChannel/InterruptRequests all
  public), and calls `agc_engine()` at its own cadence. One AGC per process (SocketAPI + CDU-FIFO
  statics are file-scope).
- **Build** [V, performed]: `cmake -S virtualagc -B build && cmake --build build --target yaAGC
  yaYUL oct2bin` — GUI tools skip cleanly without wxWidgets/SDL2; binaries link **libc + libm
  only** (pthread for the debugger's stdin thread). Static **musl build feasible** (the documented
  glibc `gethostbyname` static-link landmine doesn't apply to musl). **Caveat:** `EstablishSocket`
  calls `gethostname()`+`gethostbyname()` and fails if the hostname doesn't resolve — the guest
  must have `gatos` in `/etc/hosts` (install script ensures it).
- **Assembly** [V, performed byte-identical for both ropes]:
  `cd Luminary099 && ../yaYUL/yaYUL --unpound-page MAIN.agc > MAIN.agc.lst` → `MAIN.agc.bin`
  (73,728 B, big-endian 2 B/word, banks 2,3,0,1,4…35) + `.symtab` + `.lst`; per-bank "bugger"
  checksums embedded; cross-check = `Tools/oct2bin < Luminary099.binsource` then `diff`. Same
  recipe for `Comanche055`.

### 1.4 The flight software (Luminary099 / Comanche055) [V]

- **Identity:** Luminary099 = Luminary 1A rev 001 of LMY99, the **flown Apollo 11 LM software**
  (assembled 1969-07-14); Comanche055 = Colossus 2A rev 055, the flown CM software. Alternatives
  in-tree: LUM99R2 (later constants rev), Luminary210 (Apollo 15-17, rewritten P66) — noted for
  later, not planned.
- **Programs we target** (sources: `THE_LUNAR_LANDING.agc`, `LUNAR_LANDING_GUIDANCE_EQUATIONS.agc`,
  `P40-P47.agc`, `P51-P53.agc`, `P12.agc`, `P70-P71.agc`, `FRESH_START_AND_RESTART.agc`):
  P00 idle · P06 standby · P12 lunar ascent · P30/P32-35 targeting · **P40 DPS burn** · **P41 RCS
  burn** · P42 APS burn · P47 thrust monitor · P51/**P52**/P57 IMU align · **P63 braking** ·
  **P64 approach/LPD** · P65 auto vertical · **P66 ROD** · P68 landing confirm · P70/P71 aborts ·
  P76 target ΔV. CM: P00, P40 (SPS), P52, P61-P67 entry chain.
- **Key verbs** (catalog: `ASSEMBLY_AND_OPERATION_INFORMATION.agc:161-530`): V16 monitor decimal ·
  V21/22/24/25 load · V32/33/34 recycle/proceed/terminate · **V35 lamp test** · **V36 fresh
  start** · **V37 change program** · V40 zero CDUs · **V41 coarse align / V42 fine align** · V48
  DAP data load (R03) · V50 please-perform · V55 adjust clock · **V57/V58 permit/inhibit LR
  updates** · V60 LR position 2 · V63 radar sample · **V71/72/73 P27 uplink** · V75 U/V jets ·
  **V76 min-impulse / V77 rate-command** DAP · V82 orbit params · V99 engine-enable request.
- **Key nouns**: N09 alarms · **N20 present ICDU angles** · N33 TIG · N36 clock · **N40
  TFI/VG/ΔV** · N42 apo/peri/ΔV · N43 lat/lon/alt · N46 DAP config · **N47 LM/CSM weights** ·
  N60/N61 descent monitors · **N62/N63/N64/N68** landing displays · N65 sampled time · N66 LR
  range+position · N74 ascent.
- **Fixed-in-rope constants** (`CONTROLLED_CONSTANTS.agc`; changing them = reassembly): lunar
  μ `MUM` = 4.9027780e8 m³/cs² (= 4.9027780e12 m³/s²), `MUEARTH` = 3.986032e10, moon radius
  `504RM` = 1,738,090 m, pad radius `ERAD`; DPS/APS guidance parameters (`FDPS` 9817.5 lbf,
  `MDOTDPS`, `FAPS` 3500 lbf…); planetary-orientation series coefficients.
- **Pad-loaded erasable** (`ERASABLE_ASSIGNMENTS.agc`, `PL`-tagged + corroborated): **`RLS`**
  (moon-fixed landing-site vector) · **`TLAND`** · the full descent-target set (`RBRFG/VBRFG/
  ABRFG…`, `RAPFG/…`, ignition `VIGN/RIGNX/RIGNZ/KIGNX/B4/KIGNY/B8/KIGNV/B4`, `LOWCRIT/HIGHCRIT`
  throttle, `V2FG/TAUVERT`, `DELQFIX`) · **LR geometry/weights** (`LRALPHA/LRBETA1/LRALPHA2/
  LRBETA2`, `LRVMAX/LRVF/LRWVZ/Y/X`, `LRHMAX/LRWH`) · DAP pads (`HIASCENT/ROLLTIME/PITTIME/
  DKTRAP…/DKDB`) · W-matrix (`WRENDPOS/WRENDVEL/WSURFPOS/WSURFVEL…`) · `AOTAZ/AOTEL` ·
  `ZOOMTIME/TENDBRAK/TENDAPPR/DELTTFAP/LEADTIME` · `RPCRTIME/RPCRTQSW` · `504LM` · `AGSK` ·
  `TNEWA`. Corroborated-but-untagged: `LEMMASS/CSMMASS` (N47), `DAPDATR1` (N46), **IMU
  compensation** `PBIASX/PIPASCF/NBD*/ADIA*/ADSRA*` (zero them = perfect IMU), and the
  **planetary-orientation epoch `TEPHEM`, `AXO`, `-AYO`, `AZO`** — our hook for pinning the AGC's
  inertial frame to KSA's CCI (§3.4). `REFSMMAT` is *not* a padload (computed or uplinked).
  **The per-cell value template with units/scaling is `Luminary069/PADLOADS.agc`** (Apollo 10) —
  the best in-tree base for building a Luminary099 padload. No ready-made Luminary099 padload
  exists anywhere in the tree.
- **Channel bit documentation**: `Luminary099/INPUT_OUTPUT_CHANNEL_BIT_DESCRIPTIONS.agc` — the
  authoritative per-bit tables for ch 5/6/11/12/13/14/16/30-33 [impl-verify every bit the bridge
  wires against this file].
- **Downlink**: `DOWNLINK_LISTS.agc` (six LM lists, 50 word-pairs/s via ch 034/035 + DOWNRUPT);
  yaAGC broadcasts those channel writes and ships **`DecodeDigitalDownlink.c` + per-rope field
  specs `yaAGC/ddd-77772-Luminary099.tsv`** — everything a downlink decoder page needs.

### 1.5 DSKY interface decode [V — from yaDSKY2/piDSKY sources; tables to embed in `dsky`]

- **Ch 010** (digits): value = `AAAA B CCCCC DDDDD` — `AAAA`=(v>>11)&017 relay row, `B`=bit 11
  sign, `CCCCC`=(v>>5)&037 left digit, `DDDDD`=v&037 right digit:

  | Row | Left / Right digit | Sign bit B |
  |---|---|---|
  | 11 | PROG1 / PROG2 | — |
  | 10 | VERB1 / VERB2 | — |
  | 9 | NOUN1 / NOUN2 | — |
  | 8 | — / R1D1 | — |
  | 7 | R1D2 / R1D3 | R1 **+** |
  | 6 | R1D4 / R1D5 | R1 **−** |
  | 5 | R2D1 / R2D2 | R2 + |
  | 4 | R2D3 / R2D4 | R2 − |
  | 3 | R2D5 / R3D1 | — |
  | 2 | R3D2 / R3D3 | R3 + |
  | 1 | R3D4 / R3D5 | R3 − |
  | 12 | *lamp row (LM):* b1 PRIO DISP, b2 NO DAP, b3 VEL, b4 NO ATT, b5 ALT, b6 GIMBAL LOCK, b8 TRACKER, b9 PROG | |

  Sign rule: two bits per register, **`+` wins** if both set. Digit codes (5-bit → glyph):
  `0`=blank, `21`=0, `3`=1, `25`=2, `27`=3, `15`=4, `30`=5, `28`=6, `19`=7, `29`=8, `31`=9.
- **Ch 011**: b2 COMP ACTY · b3 UPLINK ACTY · b4 TEMP · b5 KEY REL · b6 V/N FLASH request ·
  b7 OPR ERR · **b13 LM ENGINE ON · b14 LM ENGINE OFF** (the bridge's engine commands, §4.6).
- **Ch 0163** (composite lamps, AGC-timed flash 1.28 s / 75% duty): b1 AGC warn · b4 TEMP ·
  b5 KEY REL · **b6 VN_FLASH = "verb/noun digits OFF now"** · b7 OPR ERR · b8 RESTART · b9 STBY ·
  b10 EL panel off. Sent only on change (late joiner assumes all-off).
- **Keycodes (ch 015, one write per press, stays latched, KEYRUPT1 auto)**: 1-9 = 1…9, 0 = 16,
  VERB = 17, RSET = 18, KEY REL = 25, + = 26, − = 27, ENTR = 28, CLR = 30, NOUN = 31.
  **PRO** is *not* a keycode: ch 032 bit 14, active-low, sent as mask-then-data pair with real
  **press AND release** writes (hold matters — standby entry). RSET keycode also clears the
  RESTART flip-flop in hardware.
- **Lamp sets**: LM = UPLINK ACTY, NO ATT, STBY, KEY REL, OPR ERR, (PRIO DISP, NO DAP on late
  LMs), TEMP, GIMBAL LOCK, PROG, RESTART, TRACKER, **ALT, VEL**. CM drops PRIO/NO DAP/ALT/VEL.
- **Minimal client reference**: `piPeripheral/piDSKY.py` (599 lines) — connect, resync on
  signature bits, decode 010/011/0163, send mask+data key packets.

### 1.6 Spacecraft interface decode [V — LM_Simulator + yaAGC source; per-bit re-check against
`INPUT_OUTPUT_CHANNEL_BIT_DESCRIPTIONS.agc` at implementation]

1. **RCS jets**: ch **005** = the 8 vertical jets, b1-8 = Q4U,Q4D,Q3U,Q3D,Q2U,Q2D,Q1U,Q1D;
   ch **006** = the 8 horizontal jets, b1-8 = Q3A,Q4F,Q1F,Q2A,Q2L,Q3R,Q4R,Q1L. Axis composition
   (LM_Simulator's, matching the DAP's U/V 45° geometry): `nv=(Q2D+Q4U)−(Q2U+Q4D)`,
   `nu=(Q1D+Q3U)−(Q1U+Q3D)`; pitch ∝ (nu−nv), roll ∝ (nu+nv),
   yaw ∝ (Q1F+Q2L+Q3A+Q4R)−(Q1L+Q2A+Q3R+Q4F); opposed pairs = translation (±X via net
   vertical, ±Y/±Z via horizontal pairs). LM RCS: 445 N/jet.
2. **IMU/CDU**: 1 CDU count = 39.55″ (PCDU/MCDU to `0232-0234`; **use fast types `021/023` when
   body rate ≥ 4°/s** — the slow FIFO lane saturates at 400 counts/s ≈ 1.2°/s); ch 012 b5 =
   ZERO CDU (reset our angle bookkeeping; motion inhibited while set); ch 012 b4 = coarse-align
   enable — with it set, `0174/5/6` bursts *slew the platform* 0.043948°/pulse; with it clear
   they're FDAI error-needle data (ignorable). Gyro fine align on `0177`: 0.617981″/pulse applied
   to the stable member about the selected SM axis. Gimbal lock: middle-gimbal |Z| into
   85.1°–274.9°.
3. **PIPAs**: 0.0585 m/s per pulse, PINC/MINC to `0237/0240/0241`, no FIFO — but the socket
   interlace ceiling applies (§4.4).
4. **Throttle**: THRUST counter (055) client-clocked DINC (§1.3). Pulse weight ≈ 2.7 lbf of
   commanded DPS thrust per pulse [impl-verify against the THROTTLE routine comments in
   `Luminary099` servicer and the GSOP figure; the bridge carries it as a config constant].
   Manual-vs-auto throttle: ch 030 b5 (auto throttle discrete); Luminary's throttle logic
   assumes the crew's manual lever sits at minimum during auto descent.
5. **Discretes** (all active-low except 030 b15): **ch 030** — b1 abort (w/ descent), b3 engine
   armed, b4 abort stage, b5 auto throttle, b6 display inertial data, b9 IMU operate, b10 AGC has
   control, b11 IMU cage, b12/13 IMU CDU/IMU fail, b14 ISS turn-on request, b15 SM over-temp
   (active-high). **ch 031** — b1-6 ACA out-of-detent pulses (±pitch/yaw/roll), b7-12 THC ±X/Y/Z
   translation, b13 att-hold mode, b14 auto-stab mode (both 1 = DAP off), b15 ACA out-of-detent.
   **ch 032** — b1-8 thruster-pair disables, b9 descent-engine crew disable, b10 gimbal-fail,
   b14 PRO. **ch 033** — b2-4 RR discretes, **b5 LR range data good, b6 LR pos 1, b7 LR pos 2,
   b8 LR velocity data good, b9 LR range low scale**, b10-12 uplink/downlink flags, b13 PIPA
   fail, b15 oscillator alive. Ch 033 bits 11-15 are latched: external writes can't touch them.
6. **Radar**: select code on ch 013 b1-3 (LR: vx/vy/vz/altitude; RR: range/range-rate), activity
   b4, then the SHINC/SHANC race or the embedded `RequestRadarData` hook (§1.3). LR antenna has
   two positions (ch 012 b13 commands repositioning; 033 b6/b7 report). **Scale factors (ft/count
   per beam, low/high altitude scales) are NOT in the Virtual AGC tree** — derive from the
   Luminary radar-read routines + GSOP at implementation time [impl-verify]; the padload's
   `LRALPHA/LRBETA*` mount angles must agree with the bridge's beam geometry.
7. **What LM_Simulator proves and where we surpass it**: it demonstrates the full IMU/PIPA/CDU
   feed, DSKY, discretes, and RCS decode over this exact protocol (25 ms loop) — but it has **no
   THRUST intake (no auto-throttle!), no radar data, no padload mechanism, no real dynamics**
   (GUI sliders), and its AOT marks depend on an interrupt yaAGC never raises. Our bridge closes
   all five gaps with a real universe behind it.

### 1.7 gatOS `/sim` surface the bridge stands on [V — SPEC_9P_FILESYSTEM.md + skill + examples]

**Reads** (per-vessel; the atomic `vessels/active/telemetry` JSON doc is the control-loop read —
one self-consistent snapshot with `seq`/`ut`): `position/cci` (m), `velocity/cci` (m/s vector;
CCI = parent-centered inertial, Z = pole, X = vernal), `attitude/quat` (**Body→CCI**, `x y z w`,
KSA convention `transform(+X, q) = nose`; use the KSA-exact quaternion math ported in
`examples/land-o-matic/src/guidance/ksa_quat.rs` — foreign libraries steer wrong),
`attitude/rates` (body rad/s), `altitude/{barometric,radar}` (radar = above terrain — the LR
truth source), `mass/{total,dry,propellant}` (kg), `environment/accel` (body-frame m/s²
[impl-verify: gravity-inclusive or specific force — its use in `g_force = |accel|/g₀` implies
specific force; the PIPA feed (§4.4) uses velocity-differencing regardless]), `engines/<n>/*`
(incl. writable `min_throttle`), `bodies/<parent>/{mu,radius,rotation_rate}`, `time/{ut,warp,
sim_dt}` (`sim_dt==0` ⇒ paused; writes while paused → `ETIMEDOUT`), blocking `time/alarm`.
No surface-velocity *vector* file: compute `v_surf = v_cci − ω×r`, ω = [0,0,rotation_rate].

**Writes**: `ctl/throttle` (0..1), `ctl/ignite`/`ctl/shutdown`/`ctl/engine`, `ctl/stage`,
`ctl/rcs`, **`ctl/translate` = signs-only bang-bang body-axis RCS translation (+x nose, +y
right, +z down), latching until `0 0 0`, Frame phase** — and **no rotation sibling exists**:
that is gatOS work item **W1** (§7.4). `ctl/attitude_*`/`ctl/burn` are Solver-phase (~10 Hz)
KSA-autopilot setpoints — the bridge deliberately does *not* use them (the AGC is the autopilot).
`ctl/batch` = up to 64 same-phase commands executed atomically in one tick (the bridge's
rotate+translate pair rides one batch). Debug: `teleport`, `impulse`, `refill_fuel`,
`time/warp`, `control_vessel`. Errnos ride the failing `write(2)` (`EINVAL/EACCES/EBUSY/EIO/
ETIMEDOUT…`); `controllable=0` means KSA silently ignores control — pre-check it.

**Cadence**: `sample_rate_hz` default 10, clamp 1–120, live-tunable in-game. The AGC profile
recommends 40 Hz (§4.3). Solver ≈ 10 Hz. Examples convention: poll the telemetry doc from a
worker thread; hold control on pause / warp>1 / stale `seq` (land-o-matic M6 precedent).

**Guest**: Alpine, busybox init + 5 supervised services; **outbound internet on by default**,
`apk add` works and persists in the per-save qcow2 overlay; source arrives via `/mnt` host
mounts, git clone, or cp; **in-guest builds are the house pattern** (`apk add cargo rust`,
musl-native, no cross-compile). Defaults 256 MB RAM / 2 CPU — README must tell players to raise
`memory_mb` for the one-time builds. Host reachable as `sim` (10.0.2.2); `$GATOS_HTTP` exported.

### 1.8 Examples house conventions this plan follows [V]

Standalone example dir; **one crate, several `[[bin]]`s** (land-o-matic's module layout is the
model); ratatui **0.29** using the `ratatui::crossterm` re-export; `ureq 2` (no TLS) for the
optional HTTP source; serde/serde_json; **no tokio**; release profile `opt-level="z"`, `lto`,
`strip`. Dual data source (`FsSource` on `/sim`, `HttpSource` via `--url`, `--root` fixture for
host dev). Writes = one newline-terminated `fs::write`. README skeleton: what → data interface →
build & run in-guest → host-side dev → keys → "No TUI required" raw-shell section → "MIT,
matching the mod. Source-only example; not part of gatos.slnx or CI."

---

## 2. Architecture

### 2.1 Decisions (locked)

| # | Decision | Rationale |
|---|---|---|
| D-A1 | **Everything runs inside the guest** (yaAGC, bridge, DSKY). | Linux-by-construction on every host OS; `/sim` is a local mount; purrTTY sessions already land in the guest; zero gatOS host-side changes needed to ship (except W1). The gatOS thesis applied: the AGC is just another Unix process. |
| D-A2 | Emulator = **yaAGC**, unmodified, from the `virtualagc` checkout. | 20+ years mature, socket peripheral model, embedding API, assembler + checksum toolchain in the same tree, GPLv2 (gatOS already ships GPLv2 QEMU — notice precedent exists). |
| D-A3 | **Two integration modes, laddered**: A0–A5 drive a stock `yaAGC` process over its socket protocol ("extern" mode); A6 adds **embedded mode** — `agc_engine.c` linked into the bridge via FFI, bridge serves the same wire protocol to DSKY clients. Extern stays supported for debugging with stock tools (yaDSKY2). | Extern ships value fastest and every protocol detail is now verified. Embedded is the destination because four things demand it: exact **pause/warp freeze** (stop calling `agc_engine()`), `RequestRadarData` as a clean radar hook, direct-erasable padload/state sync, and MARKRUPT if marks are ever wanted. The bridge's `AgcPort` trait keeps the swap cheap. |
| D-A4 | Flight software = **Luminary099** (LM, primary), **Comanche055** (CM, secondary), assembled at install time by yaYUL **from the virtualagc tree**, `diff`-verified against `.binsource`. | The flown Apollo 11 code; virtualagc's transcription is the proofed, assemblable one [V]. The apollo-11 repo is cited in docs as the readable mirror. |
| D-A5 | **Padload is generated from the live universe** by `agc-padload`: reads `/sim` (masses, μ, radius, site, ut) + mission constants (Luminary069 PADLOADS template), emits a **synthetic resume-core file** (plain-text octal [V]) for extern mode; embedded mode writes erasable directly. REFSMMAT + state vector go over the **digital uplink (V71/P27 via fictitious ch 0173)**. | No padload mechanism exists in prior art; the text core format makes this clean and deterministic. |
| D-A6 | **KSA-universe fit**: fly stock Luminary099 against KSA's Moon; `agc-padload` audits `bodies/Moon/{mu,radius}` vs `MUM/504RM` at start and warns beyond thresholds. Landing-radar updates + pre-PDI state uplink absorb modest error. Documented escape hatches: conformal (λ,τ) scaling (Appendix F) and rope reassembly with KSA constants (we own yaYUL). | KSA models an Earth/Moon system; exact values are save-dependent — audit, don't assume. |
| D-A7 | **Actuation mapping**: ch 011 b13/b14 → `ctl/ignite`/`ctl/shutdown` (gated by the ENGINE ARM panel switch → ch 030 b3); THRUST DINC-clocking → `ctl/throttle` (= 0.10 + pulses·k, engine `min_throttle` set to 0.10); ch 005/006 jet bits → per-axis duty cycles → **sigma-delta modulated bang-bang** writes of `ctl/rotate` (W1) + `ctl/translate`, one `ctl/batch` per bridge tick; DPS trim (ch 012 b9-12) ignored (KSA gimbals itself). | Keeps the AGC's DAP as the real autopilot; KSA's own FC stays in `manual`. |
| D-A8 | **LR = truth + geometry**: `altitude/radar` and the derived surface-velocity vector, rotated into the antenna frame (mount angles = the same `LRALPHA/LRBETA*` the padload declares), quantized with the real per-beam scale factors [impl-verify], delivered via SHINC/SHANC race (extern) or the hook (embedded). RR: out of scope. | N63/N68 and V57 behave like the real thing without inventing sensors. |
| D-A9 | **DSKY = `dsky`**, ratatui 0.29 + crossterm, pure ANSI/Unicode (no graphics protocol), truecolor with 256-color fallback, speaks the standard wire protocol to base+client-slot. Panels: `dsky` (default), `panel` (cockpit switches), `status` (bridge/downlink). | Works in every purrTTY tab and on in-world quads; stock yaDSKY2 remains usable for desktop debugging. |
| D-A10 | **Distribution = in-guest source build** (house pattern): `tools/build-agc.sh` compiles yaAGC/yaYUL (cmake) + assembles ropes + `cargo build --release`; source arrives via `/mnt` mount or git clone (guest has internet). No CI, no prebuilt binaries (matches every other example). | Zero cross-compile friction (musl-native), persists in the save overlay. |
| D-A11 | Ports: **LM base 19797, CM base 19697** (VirtualAGC convention); bridge and DSKYs are just clients; embedded bridge listens on the same bases so clients never care about the mode. `agc` launcher runs everything under busybox `start-stop-daemon` with `--nodebug`, logs under `/var/log/agc/`. | |

### 2.2 Process topology (in-guest)

```
┌──────────────────────────── gatOS guest (Alpine) ─────────────────────────────┐
│                                                                               │
│  /sim  (9p mount — live KSA telemetry & control)                              │
│    ▲ reads: vessels/active/telemetry, altitude/radar, bodies/*, time/*        │
│    │ writes: ctl/{batch,throttle,ignite,shutdown,translate,rotate*} (W1)      │
│    ▼                                                                          │
│  agc-bridge ──────────────── TCP :19797+slot ─────────────► yaAGC (Luminary099│
│    IMU sim (SM quat, CDU feed, coarse/fine align intake)     rope, --nodebug, │
│    PIPA integrator · LR model · discretes · switch state     socket protocol) │
│    THRUST clocking · jet-duty demodulator · uplink (V71)         ▲    ▲       │
│    padload/core generator · downlink recorder                    │    │       │
│    [A6: --agc=embedded links agc_engine.c in-process and         │    │       │
│     serves the same wire protocol itself]                        │    │       │
│                                                                  │    │       │
│  dsky ── purrTTY tab #2 (or in-world quad) ──────────────────────┘    │       │
│  dsky --panel=panel / yaDSKY2 on the host (debug) ────────────────────┘       │
│                                                                               │
│  /run/agc/switches/*  (file-per-switch cockpit discretes: dsky panel ⇄ bridge)│
└───────────────────────────────────────────────────────────────────────────────┘
```

Everything is ordinary Unix: `agc start lm` boots the pair, `agc status` shows both, `dsky`
attaches from any session, `agc log` tails, and a player who prefers raw tools can watch the
downlink with `cat /var/log/agc/downlink.ndjson | jq`.

---

## 3. Frames & the virtual IMU (the master analysis — read first, like LPP §3)

### 3.1 Frame inventory

| Frame | Definition | Where it appears |
|---|---|---|
| **CCI** | Parent-body-centered inertial: Z = pole, X = vernal point (right-handed) | `/sim position/cci`, `velocity/cci`, `attitude/quat` (Body→CCI) |
| **CCF** | Body-fixed: Z = pole, X = prime meridian; CCF↔CCI by hour angle | landing-site vector `RLS` (moon-fixed), LR terrain truth |
| **KSA body** | +X nose/thrust, +Y right, +Z down | `attitude/quat`, `ctl/translate`/`ctl/rotate` signs, `environment/accel` |
| **LM body** | +X thrust (up through the roof), +Z forward (crew windows), +Y right | Luminary's DAP, jet geometry, PIPA/gimbal axes |
| **Nav base / SM** | stable member; SM→"AGC reference" = REFSMMAT | CDU gimbal angles, PIPA components, gyro torquing |

**KSA→LM body mapping (config constant `body_map`, default identity):** KSA +X(nose) = LM +X
(thrust) — for a vertically-launched lander both are "up the stack", so the natural mapping is
X→X, Y→Y, Z→Z with the convention that the KSA belly (+Z) is the LM crew-window direction
(+Z). The bridge applies `body_map` at exactly two seams (telemetry in: rates/accel/quat;
actuation out: jet-axis duty), so an off-axis cockpit orientation is one config edit
[impl-verify the sign conventions against `INPUT_OUTPUT_CHANNEL_BIT_DESCRIPTIONS.agc` +
DAP comments in A4].

### 3.2 The stable member, REFSMMAT, and gimbal angles

The bridge owns one quaternion `q_sm` = **SM→CCI** (the platform's inertial orientation). The
AGC's REFSMMAT (its "reference-to-SM" matrix) is *derived from the same `q_sm`* and delivered to
the AGC by uplink — the two are never allowed to disagree (single source of truth in the bridge).

- **Gimbal angles** = the outer/inner/middle (X/Y/Z per LM_Simulator's kinematics) Euler
  decomposition of `R = body_map ∘ rot(q⁻¹ ⊗ q_sm)` (vehicle attitude relative to the platform).
  Emit CDU counts as the quantized delta (39.55″/count) since last tick: PCDU/MCDU trains, fast
  variants (`021/023`) whenever |body rate| ≥ 4°/s, respecting the 400/6,400 counts/s FIFO lanes
  (at 6,400 counts/s a CDU tracks ≤ 19.5°/s — beyond that the bridge saturates and raises its
  own "IMU unreliable" status; the real ISS had similar limits).
- **ZERO CDU** (ch 012 b5): reset the bridge's emitted-count bookkeeping to zero *without*
  touching `q_sm`; suspend CDU emission while set.
- **Coarse align** (ch 012 b4 + bursts on 0174/5/6): each pulse = 0.043948° commanded gimbal
  motion — the bridge *rotates `q_sm`* so that the resulting gimbal angles track the commanded
  slew (platform slaved to command), then resumes normal CDU tracking.
- **Fine align** (0177 bursts): rotate `q_sm` by 0.617981″ per pulse about the selected SM axis
  with the burst's sign. This is how P52-style alignment physically lands.
- **Gimbal lock**: when the middle angle enters 85.1°–274.9°, set NO ATT behavior true to life
  (the AGC lights it from its own CDU view; the bridge just keeps feeding honest counts).
- **Perfect-IMU policy**: padload zeroes all PIPA/gyro compensation cells; no drift is simulated
  in v1 (a config hook reserves `imu.drift_dph` for masochists later).

### 3.3 PIPA feeding (the one equation that must be right)

Over each telemetry interval [t₁,t₂] (consecutive `seq`, dt = ut₂−ut₁):

```
Δv_sensed(CCI) = (v₂ − v₁) − g(r_mid)·dt        g(r) = −μ·r/|r|³   (parent μ from /sim)
Δv_sensed(SM)  = R_sm←cci · Δv_sensed(CCI)       (R from q_sm — PIPAs live on the platform)
pulses_axis    = round_accum(Δv_sensed(SM) / 0.0585 m/s)            (per-axis remainder carried)
```

Velocity differencing (not `environment/accel`·dt) is the primary source: it captures thrust,
drag, and **pad contact forces** exactly the way a physical accelerometer does (a landed LM
integrates +g upward — Luminary's P57 gravity-vector logic and P12 lift-off monitoring depend on
that being true), and it can't drift relative to the game's own integrator. `environment/accel`
serves as a cross-check/telemetry only [impl-verify its gravity semantics, §1.7]. Emission is
PINC/MINC per pulse with per-axis accumulators so quantization never loses ΔV.

### 3.4 Time, TEPHEM, and the pause/warp contract

- **AGC clock ↔ KSA ut**: `agc-padload` fixes an epoch `ut₀` (AGC clock zero) and writes
  `TEPHEM` + the orientation epoch cells (`AXO/-AYO/AZO`) so Luminary's planetary-orientation
  routine reproduces KSA's **CCF↔CCI hour angle** (from `bodies/<moon>/rotation_rate` and the
  prime-meridian phase at ut₀) [impl-verify against `PLANETARY_INERTIAL_ORIENTATION.agc`'s
  evaluation before trusting RLS/latitude conversions].
- **Pause** (`time/sim_dt == 0`): extern mode — the bridge stops feeding counters/keys, holds
  actuation writes (they'd `ETIMEDOUT` anyway), and on resume performs a **state resync** (V66-
  style state-vector uplink + clock adjust V55/V70 as needed). The AGC's wall clock will have
  advanced — that drift is bounded by the pause length and corrected on resume; the plan accepts
  this for A0–A5. Embedded mode (A6) — the bridge simply stops calling `agc_engine()`:
  **perfect freeze**, mission clock included.
- **Warp > 1**: the AGC is a hard-real-time machine; the contract is "AGC ops fly at 1×".
  The bridge holds feeds during warp exactly like pause, resyncs on return to 1×, and the
  `status` panel says so. (Embedded mode could step the AGC at warp×wall in principle;
  deliberately out of scope — Luminary's own T4/T5 service loops assume real hardware pacing.)
- **Restart/GOJAM**: yaAGC zeroes output channels on hardware restart and broadcasts the clears
  [V]; the bridge treats that as "all jets off, engine command unchanged, re-latch discretes",
  and the RESTART lamp tells the player what Margaret Hamilton's restart logic is doing about it.

---

## 4. `agc-bridge` design

### 4.1 Crate layout (one crate, several bins — land-o-matic pattern)

```
examples/agc/
  Cargo.toml                  # [[bin]] agc-bridge, dsky, agc-padload; shared lib
  src/lib.rs
  src/proto/                  # wire codec: packet {chan,val,u}, counter types, client
      mod.rs codec.rs client.rs server.rs   # server.rs lands in A6 (embedded mode)
  src/sim/                    # /sim access — ported land-o-matic Source pattern
      mod.rs fs.rs http.rs fixture.rs types.rs
  src/ksa_quat.rs             # verbatim port of land-o-matic's KSA-exact quaternion math
  src/imu.rs                  # q_sm, CDU emitter, coarse/fine intake, ZERO CDU, gimbal calc
  src/pipa.rs                 # §3.3 integrator + pulse accumulators
  src/radar.rs                # LR positions, beams, scales, SHINC delivery / hook
  src/engines.rs              # THRUST clocking→throttle, on/off edges, arm gating
  src/rcs.rs                  # ch5/6 → axis duties → sigma-delta bang-bang → ctl/batch
  src/discretes.rs            # ch 030-033 state machine + /run/agc/switches watcher
  src/uplink.rs               # V71/V72 P27 word streams (0173), DSKY key macros
  src/padload.rs              # cell catalog, scalings, core-file writer (agc-padload bin)
  src/downlink.rs             # ch 34/35 recorder → NDJSON (+ ddd-77772 TSV field decode)
  src/clockpolicy.rs          # pause/warp/stale gating + resync orchestration
  src/bin/{agc-bridge,dsky,agc-padload}.rs
  dsky/                       # (module tree for the TUI: ui, seg7, lamps, keymap, panels)
  tools/build-agc.sh install-agc.sh agc   # launcher (start|stop|status|log|align|uplink)
  rom/                        # build output: Luminary099.bin, Comanche055.bin, .symtab (untracked)
  README.md
```

### 4.2 The AGC port abstraction

```rust
trait AgcPort {
    fn recv(&mut self) -> Option<AgcEvent>;          // channel writes, counter echoes, keepalives
    fn write_channel(&mut self, ch: u16, val: u16, mask: Option<u16>);
    fn counter(&mut self, reg: u8, kind: CounterKind);  // PINC/MINC/PCDU/MCDU/DINC/SHINC/SHANC (+fast)
}
```

`SocketPort` (A1) implements it over TCP with resync + reconnect + connect-replay handling;
`EmbeddedPort` (A6) implements it over FFI (`vagc-sys` module: cc-built `agc_engine.c`,
`agc_engine_init.c`, `agc_utilities.c` + a Rust-side `ChannelInput/ChannelOutput/
RequestRadarData` shim) and additionally runs `server.rs` so DSKYs still connect on :19797.

### 4.3 The main loop (extern mode)

One thread owns `/sim` polling (worker pattern); the main thread owns the AGC socket and ticks:

```
every poll (default 25 ms, --interval; recommend sample_rate_hz=40 in-game):
  t = read vessels/active/telemetry (atomic doc; skip if seq unchanged)
  gate: paused / warp>1 / stale → hold feeds, run clockpolicy, continue
  imu:  gimbal angles from (t.att_q, q_sm) → PCDU/MCDU deltas (fast if rate≥4°/s)
  pipa: Δv from (t.vel_cci, prev, μ, r) → PINC/MINC trains
  radar: if armed & ch013 gate pending → deliver SHINC/SHANC word
  discretes: recompute ch030-033 from vessel + switch files → masked writes on change
  rcs:  demodulate jet-on integrals since last tick → duty per axis → sigma-delta →
        one ctl/batch { translate "sx sy sz", rotate "sx sy sz" }     (Frame phase ✓)
  engines: apply pending on/off edges → ctl/ignite|shutdown; drain THRUST if ch014 b4
           (DINC until ZOUT; accumulate POUT/MOUT) → ctl/throttle when changed > ε
drain AgcPort continuously (select/poll): ch 005/006 jet edges (timestamped for duty
  integration), ch 011 engine bits, ch 012 moding, ch 013 radar/RHC gates, ch 014 drive
  enables, 0174-0177 platform commands, 034/035 downlink → recorder
```

Packet-rate budget [V-derived]: worst-case powered flight ≈ |a|/0.0585 pulses/s per PIPA axis —
a 6.4 m/s² DPS burn ≈ 110/axis ≈ 330 total, plus CDU tracking ≤ ~400/axis slow lane. The
launcher runs yaAGC with `--interlace=10` (~7.7 k packets/s ceiling) to keep 5× headroom;
embedded mode has no ceiling.

### 4.4 IMU/PIPA/radar modules

Per §3.2/§3.3. The LR model (A5): antenna position 1/2 state machine (ch 012 b13 command,
2-second slew, then 033 b6/b7); per-beam range/velocity = geometry-projected
(`altitude/radar`, `v_surf` in body frame via `q`) through the mount angles; data-good discretes
(033 b5/b8) asserted when within beam limits and above dropout altitude; low/high scale bit
(033 b9) per the real altitude threshold; word quantization with the GSOP scale factors
[impl-verify]. The bridge answers the ch 013 select race within the 9-gate window (measured
window ≥ tens of ms — comfortable; embedded mode replaces the race with the hook).

### 4.5 Discretes & the switch panel

`/run/agc/switches/<name>` files (`0`/`1`, poll 10 Hz — busybox-scriptable: the "No TUI
required" story is `echo 1 > /run/agc/switches/eng_arm_desc`): `eng_arm_desc`, `eng_arm_asc`,
`abort`, `abort_stage`, `auto_throttle`, `mode_auto`/`mode_hold` (ch 031 b13/b14), `imu_operate`,
`agc_has_control`, `lr_power`, `uplink_block`, plus the ISS turn-on delay sequencing (request →
90 s → operate, NO ATT clears — the authentic wait). Defaults land a flyable ship (armed
descent, auto throttle, AGC control, IMU operating after turn-on) while keeping the switches
real for procedure nerds. `dsky --panel=panel` is a friendly face over the same files.

### 4.6 Actuation

- **Engine**: ch 011 b13 edge (with `eng_arm_*` true) → `ctl/ignite`; b14 edge → `ctl/shutdown`.
  Staging (P12/abort-stage) is player-commanded from the panel (`ctl/stage`) per checklist —
  Luminary expects the crew to punch ABORT STAGE anyway.
- **Throttle**: commanded-thrust fraction = clamp(0.10 + pulses·k / F_full, 0.10, 1.0) →
  `ctl/throttle`; install sets the engine's `min_throttle` to 0.10. `k`, `F_full` config with
  DPS defaults (§1.6.4) — an honest non-DPS lander just means the AGC's mass/thrust padloads
  should match the vessel (N47/V48 let the player trim in flight, exactly as designed).
- **RCS**: per-axis duty from jet-on integrals (§1.6.1 composition), sigma-delta accumulated
  into signs-only writes so average torque matches the DAP's intent even below tick resolution;
  both vectors in **one `ctl/batch`**. Quantization floor: one bridge tick (25 ms) vs the DAP
  minimum impulse (14 ms) — acceptable v1 fidelity, recorded in Risks.

### 4.7 Uplink, padload, resync

`agc-padload` computes the §1.4 cell set (masses from `mass/*` at pad, RLS from the chosen site
lat/lon on the KSA moon via CCF, TLAND, TEPHEM/AXO/AYO/AZO from §3.4, LR mounts consistent with
`radar.rs`, DAP words, W-matrix defaults, IMU compensation = 0, descent targets scaled per
`Luminary069/PADLOADS.agc` scalings) → emits (a) a resume-core text file consumed by
`agc start` and (b) an uplink script for the in-band path. `uplink.rs` implements P27 (V71
block updates) over ch 0173 (`KKKKK | K̄K̄K̄K̄K̄<<5 | KKKKK<<10` triplication [V]) — used for
REFSMMAT delivery, post-pause/warp state-vector resync, and clock trims; UPLINK ACTY on the
DSKY flickers for real while it runs.

---

## 5. `dsky` design

### 5.1 Face (default panel, min 64×20, scales up; truecolor + 256 fallback)

```
┌────────────────────────── DSKY — LM · Luminary099 ──────────────────────────┐
│  ┌ UPLINK ┐ ┌ NO ATT ┐ ┌ STBY  ┐ ┌ KEY REL┐ ┌ OPR ERR┐   ┌──────┐ ┌──────┐  │
│  │  ACTY  │ │        │ │       │ │  ▂▂▂▂  │ │        │   │ COMP │ │ PROG │  │
│  └────────┘ └────────┘ └───────┘ └────────┘ └────────┘   │ ACTY │ │  63  │  │
│  ┌ TEMP   ┐ ┌ GIMBAL ┐ ┌ PROG  ┐ ┌ RESTART┐ ┌ TRACKER┐   └──────┘ ├──────┤  │
│  │        │ │  LOCK  │ │       │ │        │ │        │   ┌──────┐ │ VERB │  │
│  └────────┘ └────────┘ └───────┘ └────────┘ └────────┘   │ NOUN │ │  16  │  │
│  ┌ ALT    ┐ ┌ VEL    ┐                                   │  63  │ └──────┘  │
│  └────────┘ └────────┘                                   └──────┘           │
│         R1   ▐█▌ ▐█▌ ▐█▌ ▐█▌ ▐█▌      (big 3×5 half-block seven-segments,   │
│         R2   ▐█▌ ▐█▌ ▐█▌ ▐█▌ ▐█▌       green; +/− rendered as lit signs;    │
│         R3   ▐█▌ ▐█▌ ▐█▌ ▐█▌ ▐█▌       VN flash obeys ch163 b6)             │
│  V N + - 0-9 ENTR(⏎) CLR(c) PRO(p) KEYREL(k) RSET(r)  │ :status :panel :quit │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Rendering**: 7-seg glyphs from `▀▄█▌▐─│` composites, one fixed 3×5 cell per digit (a 5×7
  "large" mode when the terminal is big enough); lamp cells = reverse-video blocks in the
  authentic colors (white/amber; yellow PROG); COMP ACTY a green block. Everything degrades to
  ASCII + 8 colors (`--ascii`).
- **Protocol**: `SocketPort` reuse — resync, `FF` keepalive as liveness (banner "NO AGC" on
  loss + auto-reconnect), connect-replay renders the current display instantly; ch 0163
  late-join caveat handled (lamps assumed off until first change [V]).
- **Keys**: digits/`v`/`n`/`+`/`-`/Enter/`c`/`r`/`k` per §1.5 keycode table (mask packet then
  data [V]); **PRO**: `p` = timed press/release (600 ms); `P` = 6 s hold (standby needs a real
  hold; terminals have no key-up events — documented). RSET also clears RESTART in hardware [V].
- **Panels**: `--panel=dsky|panel|status` (also `:panel` at runtime). `panel` = the §4.5
  switches + ACA/THC nudge keys (writes ch 031 pulses — lets a player hand-fly P66 ROD from the
  terminal); `status` = bridge gates (pause/warp/stale), packet rates, IMU state (q_sm, gimbal
  angles, torque history), LR state, last downlink words decoded, last errnos.
- **Sound (optional, off by default)**: `--audio` plays key clicks + MASTER-ALARM through
  `/sim/audio` (upload once, `play` on events) — the game's speakers do DSKY sounds.

---

## 6. ROMs, padloads, missions

### 6.1 Build pipeline (`tools/build-agc.sh`) [V — commands proven]

```
apk add --no-cache build-base cmake git                     # one-time
git clone --depth 1 <virtualagc> /opt/src/virtualagc        # or /mnt mount
cmake -S /opt/src/virtualagc -B /opt/src/virtualagc/build
cmake --build .../build --target yaAGC yaYUL oct2bin -j$(nproc)
(cd .../Luminary099  && ../yaYUL/yaYUL --unpound-page MAIN.agc > MAIN.agc.lst)
(cd .../Comanche055  && ../yaYUL/yaYUL --unpound-page MAIN.agc > MAIN.agc.lst)
oct2bin < Luminary099.binsource && cmp MAIN.agc.bin oct2bin.bin   # hard-fail on mismatch
install → /opt/agc/{bin,rom,share}; ensure "127.0.0.1 gatos" in /etc/hosts
```

`install-agc.sh` + `agc` launcher: `agc start lm|cm` (padload → resume-core → yaAGC
`--nodebug --port=19797 --interlace=10 --cfg=LM.cfg` → bridge), `stop`, `status`, `log`,
`align` (REFSMMAT uplink + V41 macro), `uplink <file>`. Raise `memory_mb` (512+) in gatos.toml
for the one-time compile; runtime is featherweight (yaAGC is an 85 kHz machine — measured idle
CPU is negligible even under TCG).

### 6.2 The KSA-universe audit

At `agc start`, `agc-padload` prints the fit report: KSA moon μ vs `MUM`, radius vs `504RM`,
rotation rate vs the orientation series, site RLS. Green within ~1%; yellow = fly it, expect
LR/uplink to carry more correction; red = pick the conformal map (Appendix F) or the
`--reassemble` path (patch `CONTROLLED_CONSTANTS.agc` values, yaYUL rebuild — the audit prints
the exact octal patches). Earth-orbit CM work gets the same audit against `MUEARTH`/`ERAD`.

### 6.3 An "AGC-ready LM" in KSA (vessel requirements, documented in README)

Main engine ≈ DPS class (max thrust configured as `F_full`, `min_throttle` 0.10, gimbal OK);
RCS with both rotation + translation authority mapped on all axes; mass in the LM envelope
(padload N47 defaults from live `mass/*` anyway); staging optional (P12/abort-stage need a
decoupler + ascent engine). Any vessel flies with honest padloads — the AGC only knows what we
tell it about mass and thrust, and V48/N47 exist precisely to trim that in flight.

### 6.4 Mission ladder (each = a README procedure card + VALIDATION entry)

| Card | Content | Proves |
|---|---|---|
| M-A "First light" | V35, V16N36, V36/V37E00E | emulator + DSKY + clock |
| M-B "Alignment" | ISS turn-on wait, `agc align` (REFSMMAT uplink), V41N20, V16N20 while tumbling, V40 | IMU seam end-to-end |
| M-C "Burns" | V48 DAP load, V76/V77, P30+P41 RCS tweak; P30+P40 DPS burn w/ V99/N40 cutoff | actuation seam, Average-G |
| M-D "Landing" | pre-PDI uplink, V37E63E, PRO at TIG, N63 → V57 LR accept → P64 (LPD on `panel`) → P66 ROD → contact | the whole point |
| M-E "Ascent & CM" | P12 to orbit; `agc start cm` P00/P52/P40 SPS | staging path, Comanche mode |

---

## 7. Packaging, repo layout, and the gatOS work item

### 7.1 In-repo deliverables

Everything under `examples/agc/` (§4.1) — source-only, MIT (the example code; yaAGC/yaYUL are
fetched and built from the virtualagc checkout under GPLv2, never vendored into gatOS), not in
`gatos.slnx`/CI, per house rules. README follows the house skeleton including the "No TUI
required" section (raw `nc`-level protocol pokes + switch files + downlink NDJSON).

### 7.2 THIRD-PARTY-NOTICES

No change required while we don't redistribute binaries (players build from the virtualagc
checkout). **If** a future convenience tarball ships yaAGC binaries or rope images, add GPLv2
(Virtual AGC) + the public-domain flight-software note then — flagged in the README.

### 7.3 Docs-lockstep obligations of this plan

The plan itself adds no `/sim` surface. **W1 does** (below) and must ride the constitution:
SPEC_9P_FILESYSTEM.md + `scope/` pages + `docs/KSA_INTEGRATION_MATRIX.md` + `docs/VALIDATION.md`
in the same change. The examples get a `site/` tutorial (via the `tutorials` skill) at A7, and
CLAUDE.md's status table gains a row when A2 first ships something usable.

### 7.4 W1 — `ctl/rotate` (the one gatOS augmentation; prerequisite for A4)

Symmetric sibling of `ctl/translate` [V that none exists today]: per-vessel `ctl/rotate`,
grammar `x y z` **signs-only** about the KSA body axes (+x/−x roll, +y/−y pitch, +z/−z yaw
[impl-verify axis/sign convention against KSA's manual-rotation input path — same verification
style as translate's `ControlMap`/`ManualThrustMode.Direct` binding]), bang-bang full RCS
torque, **latching until `0 0 0`**, Frame phase, action key `vessel.rotate`, readable like
`translate`. Implementation mirror-images the translate actuator (`KsaCatalog` entry + reader
of the live value); reachable on HTTP/MQTT by construction (transport-parity rule). Acceptance:
NUnit grammar/errno tests + in-game checklist (each axis, latch, compose with translate,
`EACCES` under authority gating). *Fallback if KSA lacks a direct rotation input path:* the
actuator drives per-thruster `rcs/<n>` forces — decided at W1 implementation, recorded in scope/.

---

## 8. Milestones & tasks

Task ids `A<m>.<n>`; commits `A3.2: pipa integrator + accumulators`. Every milestone ends with
`cargo build --release` + `cargo test` green in-guest and on a host Linux checkout, and its
**Exit** demo captured in the README. yaAGC/rope steps must stay byte-reproducible.

**A0 — Toolchain & ROMs.** `tools/build-agc.sh`, `install-agc.sh`, `/etc/hosts` fix, launcher
skeleton (`--nodebug`, ports, logs). Assemble + verify both ropes in-guest.
*Exit:* `agc start lm && agc status` shows yaAGC alive on :19797 with Luminary099 loaded, on a
fresh save, plus the same on a bare Linux host checkout.

**A1 — Protocol crate + DSKY first light.** `proto/` codec (golden-packet unit tests from §1.3
tables incl. u-bit masking, counter flag, resync, keepalive), `SocketPort`, `dsky` face/keys
against stock yaAGC (host + guest).
*Exit:* V35 lamp test animates every segment; V16N36 ticks; V37E00E reaches P00 — screenshot in
README, procedure card M-A.

**A2 — In-game packaging.** purrTTY workflow docs, `/mnt` + git delivery paths, `panel`/
`status` stubs, `agc log`, memory guidance; VALIDATION.md gains the M-A in-game checklist.
*Exit:* fresh save → M-A card completed entirely inside purrTTY, including a DSKY tab on an
in-world quad.

**A3 — Bridge core: clock, discretes, IMU, PIPA, padload.** `sim/` sources + gating; discretes
state machine + switch files; `q_sm`/CDU/coarse/fine/ZERO; PIPA integrator; `agc-padload`
(core-file path) + `agc align` (REFSMMAT uplink + V41 macro); KSA-audit report.
*Exit:* M-B card: after alignment, V16N20 tracks the tumbling vessel within quantization, NO
ATT/gimbal-lock behavior correct; pause/resume resyncs clean.

**A4 — Actuation (needs W1).** W1 lands in gatOS proper first. Then `rcs.rs` duty demodulation
→ batch writes, `engines.rs` (ignite/shutdown edges, THRUST clocking → throttle), DAP config
card (V48), V76/V77.
*Exit:* M-C card: P40 executes a targeted DPS burn — ignition at TIG under V99/PRO, Average-G
N40 counts down, auto-cutoff within tolerance vs `/sim` truth; DAP visibly rate-damps in min-
impulse and holds attitude in rate-command.

**A5 — Landing radar & the descent.** `radar.rs` (positions, beams, scales [impl-verify], data-
good, SHINC race), V57/V58/V60 behavior, N63/N68 agreement telemetry in `status`, abort
switches.
*Exit:* M-D card: full PDI→touchdown flown by P63/P64/P66 on the KSA moon, LR converging the
AGC state (N68 Δ shrinking), ALT/VEL lamps correct, and a P70 abort-to-orbit demo.

**A6 — Embedded core.** `vagc-sys` (cc build of `agc_engine.c` + init + utilities; Rust shims
for the five hooks), `EmbeddedPort`, wire `server.rs` so DSKYs are mode-blind; pause = engine
freeze; direct-erasable padload/state sync path; `--agc=extern|embedded` flag (extern kept).
*Exit:* A4+A5 demos pass in embedded mode; pausing the game freezes the mission clock exactly;
kill/restart mid-flight resumes from the auto core dump.

**A7 — Polish & CM.** Downlink NDJSON + decoded `status` page (ddd-77772 TSV), `/sim/audio`
alarms, Comanche mode (`agc start cm`, CMSIM cfg, CM DSKY lamp set, P00/P52/P40 card), in-world
cockpit recipe (welds + purrTTY quad + `dsky`), `site/` tutorial, VALIDATION.md sweep.
*Exit:* M-E card; docs complete; status row added to CLAUDE.md.

---

## 9. Risks & open questions

| Risk | Mitigation |
|---|---|
| Socket packet-rate ceiling vs pulse streams | Budgeted §4.3 (5× headroom at `--interlace=10`); embedded mode removes it. |
| LR scale factors not in-tree | [impl-verify] from Luminary radar routines + GSOP at A5; until then N63-vs-truth telemetry in `status` catches wrong scales immediately. |
| THRUST pulse weight uncertain | Config constant, calibrated in A4 against commanded-vs-actual thrust (the N40/`/sim` cross-check makes miscalibration obvious). |
| KSA moon ≠ Luminary constants | Runtime audit + LR/uplink absorption; conformal map (App. F) and reassembly documented escape hatches. |
| RCS duty quantization (25 ms tick vs 14 ms min impulse) | Sigma-delta averaging; honest limitation noted; embedded mode + higher `sample_rate_hz` narrow it. |
| Pause drift in extern mode | Documented; resync-on-resume; solved fully by A6. |
| `environment/accel` semantics unknown | PIPA path never depends on it (velocity differencing). |
| AOT marks impossible over sockets | Alignment via REFSMMAT uplink (authentic — Houston did it too); MARKRUPT possible in embedded mode if ever wanted. |
| Gimbal-angle sign conventions (KSA↔LM) | `body_map` config + M-B V16N20 tracking test falsifies wrong signs in seconds. |
| Luminary099 vs LM_Simulator's Luminary-131 procedures | Verbs/nouns above re-checked against 099's own tables [V]; procedure cards written against 099. |
| One AGC per process | Non-issue: LM and CM are separate processes on separate base ports. |
| Terminal has no key-release (PRO hold) | Timed press/release + explicit hold key; documented. |

**Open:** exact `TEPHEM/AXO/AYO/AZO` encoding math (A3 [impl-verify]); whether `--reassemble`
ever graduates from escape hatch to default for far-from-real saves; DEDA/AGS follow-on.

---

## 10. Validation

1. **Host-tier (no game):** proto golden-packet tests; codec fuzz vs resync; PIPA/CDU math
   property tests (quantization conserves ΔV/angle); padload cell scalings vs
   `Luminary069/PADLOADS.agc` samples; `--root` fixture replays (recorded telemetry → expected
   counter streams). Rope build reproducibility (`cmp` vs binsource) in `build-agc.sh` itself.
2. **In-guest tier:** A0–A1 exits against stock yaAGC; bridge soak at 40 Hz for an hour (packet
   budget, no FIFO overflows, no reconnect storms).
3. **In-game tier (docs/VALIDATION.md):** the five mission cards M-A…M-E as checklists with
   expected DSKY displays at each step (N20 tracking tolerance, N40 cutoff tolerance, N63/N68
   convergence, touchdown residuals), plus pause/warp/restart behavior spot-checks.

---

## Appendix A — wire recipes (bridge cheat-sheet, all [V])

```
packet:  b0=00|u|t|ch[8:3]  b1=01|ch[2:0]|v[14:12]  b2=10|v[11:6]  b3=11|v[5:0]
u=0x20 (mask packet: store per-client bitmask); t=0x10 (counter: ch=0200|reg, v=kind)
kinds: 0 PINC · 1 PCDU · 2 MINC · 3 MCDU · 4 DINC · 5 SHINC(0) · 6 SHANC(1) · 021/023 fast PCDU/MCDU
PIPAX+1=(0237,000) −1=(0237,002) · Y 0240 · Z 0241        (no echo)
CDUX±  =(0232,001/003) fast 021/023 · Y 0233 · Z 0234      (FIFO 400/6400 cps per axis)
RNRAD  = 15×(0246,005|006) MSB-first, inside the ch013-b4 9-gate window
INLINK = (0173, K | (K^0x1F)<<5 | K<<10) → UPRUPT           (uplink chars)
THRUST = on ch014-b4: loop (0255,004) → echo 0255: 015 POUT / 016 MOUT / 017 ZOUT(stop)
keys   = u-packet ch 015 mask 037, then (015, keycode) — KEYRUPT1 auto; PRO = ch 032 b14
         mask/data pair: press (0432,020000)+(032,0), release (032,020000)
AGC→us = all CPU channel writes broadcast; 0163 lamps; 0174/5/6 CDU-slew bursts (040000=minus|count);
         0177 gyro bursts (((ch014&0740)<<6)|count); FF FF FF FF keepalive ≈1.5 s
registers (octal): CDUX 032 CDUY 033 CDUZ 034 · OPTY 035 OPTX 036 · PIPAX 037 PIPAY 040 PIPAZ 041 ·
         RHC 042-044 · INLINK 045 · RNRAD 046 · GYROCTR 047 · CDUXCMD 050-052 · OPT CMD 053-054 ·
         THRUST 055 · OUTLINK 057 · ALTM 060
```

## Appendix B — DSKY decode (embed as `dsky` tables)

Ch 010 rows/digit-codes/sign rule and lamp row 12: §1.5. Ch 011: §1.5. Ch 0163: §1.5 (flash
1.28 s/75%; VN_FLASH bit = digits off *now*). Keycodes: §1.5. LM vs CM lamp sets: §1.5.

## Appendix C — `/sim` ↔ AGC signal map (consolidated)

| AGC side | Direction | `/sim` side | Module |
|---|---|---|---|
| CDUX/Y/Z counts | → AGC | `attitude/quat` (+ `q_sm`) | imu |
| PIPAX/Y/Z pulses | → AGC | `velocity/cci` deltas − μ/r² integral | pipa |
| RNRAD (LR beams) | → AGC | `altitude/radar`, `v_cci − ω×r` via `q` | radar |
| ch 030-033 discretes | → AGC | vessel state + `/run/agc/switches/*` | discretes |
| ch 015 keys / 032 b14 PRO | → AGC | `dsky` keyboard | dsky |
| INLINK (0173) | → AGC | `agc align` / resync / P27 files | uplink |
| ch 011 b13/b14 | AGC → | `ctl/ignite` / `ctl/shutdown` | engines |
| THRUST DINC echoes | AGC → | `ctl/throttle` | engines |
| ch 005/006 jets | AGC → | `ctl/batch` {`translate`, `rotate` (W1)} | rcs |
| 0174-0176 slew / 0177 gyro | AGC → | rotate `q_sm` (platform) | imu |
| ch 034/035 | AGC → | `/var/log/agc/downlink.ndjson` (+ status page) | downlink |
| ch 012/013/014 moding | AGC → | imu/radar/engines state machines | * |

## Appendix D — padload catalog

The cell set in §1.4, generated by `agc-padload` with per-cell scalings cross-checked against
`Luminary069/PADLOADS.agc` (values) + `Luminary099/ERASABLE_ASSIGNMENTS.agc` (addresses; they
shift between revisions — never reuse 069 addresses). Output formats: resume-core text file
(extern) / direct erasable (embedded) / V71 uplink script (in-band).

## Appendix E — verb/noun quick card

The §1.4 verb + noun tables, shipped as `agc verbs` and printed on the README card; source of
truth `ASSEMBLY_AND_OPERATION_INFORMATION.agc`.

## Appendix F — conformal scaling (escape hatch for exotic universes)

Newtonian mechanics is invariant under `r' = λr`, `t' = τt`, `μ' = λ³μ/τ²`. If a target body's
(μ, R) can't pass the §6.2 audit, choose `λ = 504RM/R_ksa` and `τ = √(λ³ μ_ksa / MUM)`; the
bridge maps every length/velocity/time crossing the seam (positions ×λ, velocities ×λ/τ, its
tick clock ×1/τ) and the *unmodified* rope flies a mathematically exact mission — with the AGC's
clock running at τ× game time (only embedded mode can honor τ ≠ 1 exactly, by stepping
`agc_engine()` at τ·85333 Hz). Display values on the DSKY are then in "Luminary units"; the
`status` page shows both. This is a documented curiosity until someone needs it — the default
answer to a bad audit is reassembly (§6.2).

## Appendix G — source index (the citations behind §1)

- Wire/engine: `virtualagc/yaAGC/{agc_utilities.c,SocketAPI.c,agc_engine.c,agc_engine.h,
  agc_engine_init.c,agc_simulator.c,agc_cli.c,NullAPI.c,EmbeddedDemo.c,DecodeDigitalDownlink.c,
  ddd-77772-Luminary099.tsv}`
- DSKY clients: `virtualagc/yaDSKY2/yaDSKY2.cpp` (+ `*.ini`), `virtualagc/piPeripheral/
  piDSKY{,2}.py`
- Bridge prior art: `virtualagc/Contributed/LM_Simulator/` (`lm_simulator.tcl`,
  `modules/AGC_{IMU,DSKY,Outputs,Crew_Inputs,System_Inputs,Simulation_Monitor_Control}.tcl`,
  `lm_simulator.ini`, `doc/tutorial.txt`)
- Flight software: `virtualagc/Luminary099/` (`MAIN.agc`, `ERASABLE_ASSIGNMENTS.agc`,
  `CONTROLLED_CONSTANTS.agc`, `INPUT_OUTPUT_CHANNEL_BIT_DESCRIPTIONS.agc`,
  `ASSEMBLY_AND_OPERATION_INFORMATION.agc`, `THE_LUNAR_LANDING.agc`,
  `LUNAR_LANDING_GUIDANCE_EQUATIONS.agc`, `P40-P47.agc`, `DOWNLINK_LISTS.agc`,
  `DOWN_TELEMETRY_PROGRAM.agc`, `Luminary099.binsource`), `virtualagc/Luminary069/PADLOADS.agc`,
  `virtualagc/Comanche055/`, and the readable mirror `apollo-11/{Luminary099,Comanche055}/`
- gatOS: `SPEC_9P_FILESYSTEM.md`, `docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`,
  `docs/TUTORIAL_DATA_REFERENCE.md`, `examples/land-o-matic/` (Source pattern, `ksa_quat.rs`,
  gating), `examples/{gogogo-rs,skycaptain,dashboard-rs}/`, `guest/README.md`,
  `LANDING_PROGRAM_PLAN.md` (this plan's structural template)
