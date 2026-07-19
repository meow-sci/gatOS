# skycaptain

A ratatui **skywriting autopilot** over the gatOS `/sim` filesystem: type a message, and the active
vessel takes control of itself, hovers on its plume, and writes the text across the sky — turning
the engine on and off so the plume trail draws letters. Time compression is driven automatically
(default 4× while painting, 10× while coasting between letters) so you don't grow old watching.

```sh
# in the Alpine guest
apk add --no-cache cargo rust
cargo run --release            # reads /sim, TUI opens on the compose screen
```

Type your message, check the pre-flight list, press Enter. The vehicle settles into a hover,
freezes a canvas at that spot, and starts writing — the hover point becomes the first ink of the
first letter. `a` aborts (engine cut, warp restored, controls released).

No game handy? `cargo run --release -- --simulate --text "HI"` flies the whole thing in a built-in
physics sandbox on any host.

## How it writes (the physics)

KSA's plume trails give the autopilot a very particular pen:

- **The trail is binary.** KSA emits trail segments whenever the engine produced thrust that step
  (`DutyCycle > 0`), at a fixed visual width — throttle doesn't matter. Pen-down = engine lit,
  pen-up = engine cut. There is no half-ink.
- **Painting means hovering.** While the pen is down the same engine is also the only thing holding
  the vehicle up, so every stroke is flown on thrust: vertical strokes are throttle moves,
  horizontal strokes tilt the thrust vector a few degrees. KSA's flight computer slews attitude at
  only ~5°/s, so the plan stops dead at sharp corners and waits out the nose swing (the dwell paints
  a dot *on* the corner — invisible).
- **Pen-up means ballistic.** With the engine off nothing can change the velocity — every gap
  between strokes is a solved projectile arc under the parent body's real `μ/r²` gravity. To hop to
  the next letter, the autopilot rides an ascending stroke of the letter it just finished
  (repainting it — also invisible), releases the engine at a solved speed and instant
  (`time/alarm`-precise), lobs over, and relights exactly on the next letter's descending entry
  stroke.
- **The font is shaped by all of this.** "Skybrush Caps" letterforms each draw as one continuous
  pen path with retraces; entries descend (to absorb the steep hop arrivals), exits ride straight
  ~58° diagonals (the ballistic launch ramps — native on A K M V W X Y Z 4 7 9, an authored brush
  swash on the rest), and the whole font is sheared ~10° italic because a rightward hop needs a
  right-leaning launch. The flourishes are load-bearing. Letter spacing is adjusted per pair by the
  hop solver — the typography is kerned by ballistics.
- **Trails live in the surface frame and fade.** KSA stores trail points in the body-fixed (CCF)
  frame, so the writing stays glued over the ground; the canvas is a surface-fixed plane and all
  guidance runs in the rotating frame (gravity + centrifugal + Coriolis, the same terms KSA
  integrates). Segments fade out 1200 game-seconds after they're painted — the TUI warns when a
  message is too long to finish before its head starts thinning.

**Atmosphere required:** KSA only emits plume trails *inside* an atmosphere (below the ceiling,
density > 1e-9). No air, no ink — the pre-flight list checks this, and the sweet spot is high thin
air (trail forms, drag ≈ 0). Strokes bloom to ~160 m wide within seconds, which is why letters
default to ~900 m tall.

## The TUI

- **Compose screen** — type the message (A–Z 0-9 `.,-!?':` — unsupported characters are struck
  through), with a live pre-flight checklist: vessel controllable, TWR ≥ 1.25, atmosphere band,
  altitude clearance (the text hangs *below* the start point), surface speed, warp authority,
  propellant.
- **Flight screen** — per-letter progress (done/current/pending with %), ETA for the current letter
  and the whole text in game seconds and wall seconds at the current warp, throttle bar, altitude,
  propellant, and a Braille sky-canvas: planned strokes and hop arcs in gray, painted trail in cyan,
  the pen in yellow.
- **Keys**: type/Backspace/Enter on compose; `a` abort, `q` abort+quit while flying; `n` new
  message when done.

**Every ending is a rescue.** Completion, any failure, and the pilot's `a` all funnel into the same
sequence: warp to 1×, engine ON, brake to a hover, then park the vehicle on the game FC's *own*
persistent hover hold (nose-up attitude target + hover throttle — onboard setpoints KSA keeps
flying after the program stops talking). You always get a hovering craft back, never a falling one.
The arm phase likewise forces 1× warp before trying to capture the initial hover (a leftover 10×
from an earlier run once porpoised a rocket into the Martian regolith).

## Picking a craft (matters more than any flag)

The pen is the engine, so **the engine must be lit to draw — and KSA floors a lit engine's
throttle at its `min_throttle` (often 10–20%)**. That floor sets a hard rule:

> **min-throttle thrust must stay well under local gravity**, or the vehicle cannot *descend*
> while painting and every down-stroke is physically impossible. The pre-flight refuses with the
> numbers when this bites.

Since hover throttle is `1/TWR`, the sweet spot is **TWR ≈ 2–5 on the body you're writing over**
with a deep-throttling engine. Very high TWR is a trap, and low gravity multiplies TWR: an
Earth-TWR-2 craft is already TWR ~5.4 on Mars. Small and light is good (agile attitude); pair it
with a small tank and `--cheat-refill` — the debug refill keeps the tank topped up in flight, which
also freezes the mass so the thrust model stays exact. On low-g bodies the autopilot automatically
widens its thrust-tilt allowance to keep useful lateral authority.

## Options

```
--text MSG        pre-fill the message          --height M     letter cap height [900]
--url BASE        HTTP /v1 mirror               --speed M/S    draw speed        [70]
--root DIR        fixture /sim dir              --heading DEG  text compass dir  [90 = east]
--simulate        built-in physics sandbox      --floor M      rescue radar floor [250]
--warp-draw X     warp while painting [4]       --interval MS  poll cadence      [120]
--warp-hop X      warp while coasting [10]      --slew DPS     FC slew assumption [5]
--warp-fine X     warp at cut/relight [2]       --tilt DEG     max paint tilt     [12]
--allow-impulse   let unsolvable hops (mid-text dots) use the debug impulse cheat
--cheat-refill    keep the tank topped up with debug refill_fuel (recommended)
```

Warp control uses `debug/time/warp` (the gatOS debug namespace) and is **cooperative**: each phase
states its preference and retries until telemetry confirms it took — but once confirmed, turning
the warp dial yourself is respected (the program adopts your factor instead of fighting you; its
next phase transition states its preference again). With `debug_namespace=false` the program still
flies at whatever warp you set by hand — all its timing is in sim time, so any warp is correct,
just not automatic. KSA allows engine starts up to 30× compression; the defaults stay well under.

A mid-text `.` `,` `!` `?` `:` has a dot the ballistics genuinely cannot relaunch from (a dot's
"ramp" is meters long) — the planner refuses and says so. End the message with it, or pass
`--allow-impulse` to let those single hops cheat with `debug/vessels/<id>/impulse`.

## What to expect (honestly)

The median ink lands well inside the plume bloom, but corners and hop entries carry visible blobs
and hooks — brake overshoot at a slewing nose, crossrange kill after a long arc. It reads as
hand-lettered brushwork, which is half the charm. Writing is slow at 1×: roughly 1–2 minutes of
game time per letter (that's what the warp automation is for). Fuel: hovering costs ~`m·g/(Isp·g₀)`
per second — the compose screen shows the tank, low propellant triggers a rescue (or a refill, with
the cheat on). The give-up logic is deliberately patient: it only rescues out of a letter after the
tracking error has exceeded 2.5 km for 45 s *without improving* — a vehicle clawing its way back
gets to keep trying.

## Host-side development

```sh
cargo test                                  # planner/font/frames/sim unit tests + the sandbox e2e flight
cargo run -- --simulate --text "KSA"        # full TUI against the built-in physics sandbox
cargo run -- --url $GATOS_HTTP              # against a live mod from the host
SKYC_TRACE=trace.csv cargo test --test integration   # dump a per-tick flight trace (csv)
```

The core (`font`, `plan`, `frames`, `ksa_quat`, `flight`) is game-free; the end-to-end test flies
"HI" in the sandbox and grades the painted trace against the planned letterforms.

## No TUI required

The pen itself is two files — this program's whole actuation vocabulary:

```sh
echo 1 > /sim/vessels/active/ctl/engine                  # pen down (and hover thrust…)
echo 0.55 > /sim/vessels/active/ctl/throttle             # …at this throttle
echo "x y z w" > /sim/vessels/active/ctl/attitude_target # point the plume (Body→CCI quaternion)
echo 0 > /sim/vessels/active/ctl/engine                  # pen up — now you're ballistic
echo 4 > /sim/debug/time/warp                            # hurry the sky along
```

Everything else is knowing *when* — which is the autopilot's job.

---

MIT, matching the mod. Source-only example; not part of the .NET solution or CI.
