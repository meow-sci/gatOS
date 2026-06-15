# land-o-matic

A powered-descent **landing-guidance TUI** for KSA, run **inside the Alpine guest** over the gatOS
`/sim` filesystem. It fuses two real flight-guidance algorithms:

- **G-FOLD** (Guidance for Fuel-Optimal Large Diverts — Açıkmeşe/Blackmore convex powered descent) as
  the **trajectory planner/replanner**: the fuel-optimal divert, the glide-slope and **G-limit**
  constraints, and the suicide-burn ignition time, re-solved live as a second-order cone program.
- **UPFG** (the Space Shuttle's Unified Powered Flight Guidance, via Noiredd's **PEGAS**) as the
  **closed-loop steering law**: the thrust direction + time-to-go each tick.

> The full design, the algorithm analysis, and — critically — the **reference-frame contract** live in
> [`../../LANDING_PROGRAM_PLAN.md`](../../LANDING_PROGRAM_PLAN.md). Read §3 (frames) before touching the
> guidance math.

## Status

Built incrementally by milestone (see the plan §12):

- **M0 — read-only HUD** ✅ — polls `/sim` and mirrors the active vessel's state.
- **M1 — frames & quaternion core** ✅ — pure, host-tested CCI↔ENU↔CCF transforms and a verbatim
  port of KSA's quaternion math (the attitude-output path, proven by the `transform(UnitX, q) == d`
  invariant).
- **M2 — G-FOLD solve** ✅ — the fuel-optimal powered-descent SOCP via Clarabel (non-dimensionalized,
  G-limit enforced), validated offline on a generic lander and the Mars reference case.
- **M3 — G-FOLD closed-loop MPC** ✅ — flies the vessel: builds the guidance state from telemetry,
  re-solves each tick, writes `attitude_target`/`throttle`/`ignite`, with live G-limit re-plan and
  ABORT. Validated by a **host closed-loop point-mass simulation** that lands softly with the guidance
  in the loop. ⚠️ The in-KSA flight pass is still pending (deferred, like the rest of the repo's
  in-game validation).
- **M4 — UPFG port** ✅ — a faithful port of the conic state-extrapolation propagator (`CSEroutine`,
  Shepperd/Robertson universal-variable two-body) and the UPFG steering law, run **directly in CCI**
  (no `vecYZ` swap), adapted for descent: velocity-to-go steering with conic-propagated gravity, the
  orbital `λ̇` corrector omitted for the near-vertical regime (it destabilizes there; G-FOLD owns
  position/divert). Validated by CSE-vs-Kepler parity (analytic circular orbit + RK4 ellipse), the
  thrust-integral closed forms, and a **UPFG-only closed-loop landing sim**.
- **M5 — hybrid braking → terminal handoff** ✅ — the G-FOLD MPC flies the high divert, then hands off
  (latched, below the handoff altitude) to UPFG terminal guidance for the precise touchdown; the
  G-limit caps the deceleration on **both** legs. Validated by a host closed-loop sim that exercises the
  full **braking → terminal → touchdown** sequence (soft landing, peak-g ≤ G-limit, fuel to spare).
  ⚠️ the in-KSA flight pass is still pending.
- M6+ — trajectory canvas, infeasible/abort/warp/stale UX, successive convexification, atmosphere.

## Build & run (in-guest)

The guest is Alpine (musl); Alpine's packaged Rust builds a static binary with zero cross-compile
friction:

```sh
apk add --no-cache cargo rust          # one-time
cargo run --release                    # reads /sim, monitors/guides the active vessel
```

Get the source into the guest via a host-folder mount (`gatos.toml [[mounts]]` → `/mnt/<name>`, guest
v10+), a `git clone`, or `cp`. A Clarabel + nalgebra build is heavier than the other examples — give the
guest some `disk_size_gb` / `memory_mb` headroom in `gatos.toml`.

## Host-side dev

The guidance core is pure Rust with no `/sim`/game/terminal dependency, so it builds and tests on the
host:

```sh
cargo test                             # unit + frame/solver tests
cargo run -- --root ./fixture          # run the TUI against a fixture /sim tree
cargo run -- --url $GATOS_HTTP         # …or a live mod via the HTTP /v1/fs mirror
```

## Keys

- `e` — **ENGAGE** (arm guidance; targets the point directly below and starts the powered descent)
- `a` — **ABORT** (cut throttle, release attitude to manual)
- `↑`/`↓` (or `-`/`=`) — adjust the **G-limit** live (watch it re-plan)
- `q` / `Esc` — quit

> ⚠️ ENGAGE fires your engine and steers the vessel. Watch it; `a` aborts to manual.

## License

MIT, matching the mod. Source-only example; not part of `gatos.slnx` or CI.
