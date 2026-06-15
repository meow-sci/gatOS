# LANDING_PROGRAM_PLAN.md — `land-o-matic`

A Rust **ratatui** powered-descent guidance program for KSA, run **inside the Alpine guest** over the
gatOS `/sim` filesystem. It fuses two real flight-guidance algorithms:

- **UPFG** (Unified Powered Flight Guidance — the Space Shuttle's explicit guidance, as implemented in
  **Noiredd's PEGAS**) as the **closed-loop steering law** (thrust direction + time-to-go, every tick).
- **G-FOLD** (Guidance for Fuel-Optimal Large Diverts — Açıkmeşe/Blackmore lossless-convexification
  powered descent) as the **trajectory planner/replanner** (the fuel-optimal divert, the glide-slope
  and **G-limit** constraints, and the suicide-burn ignition time), re-solved live.

The G-limit is a live input: changing it re-solves G-FOLD and re-throttles UPFG — the descent analogue
of PEGAS's "watch as I change the G-Limit and the rocket recalculates."

> **This document is the build plan and the reference-frame contract.** It is written from a deep read
> of `thirdparty/PEGAS`, `thirdparty/PEGAS-MATLAB`, `thirdparty/G-FOLD`, the KSA decompiled sources
> under `thirdparty/ksa/`, and the gatOS `/sim` surface. Every load-bearing claim carries a `file:line`
> citation (see §14). **Frames and units are the crux and are treated first (§3).**

---

## 0. How to read this / status

- **All milestones M0–M7 built** (`examples/land-o-matic/`, lib + bin, host-tested green): read-only
  HUD, the reference-frame + KSA-quaternion core, the G-FOLD SOCP (Clarabel), the closed-loop MPC that
  flies the vessel, the UPFG terminal-guidance port (CSE conic propagator + steering, run directly in
  CCI), the **hybrid handoff** (G-FOLD braking + UPFG terminal, G-limit capping both legs), **M6 polish**
  (trajectory canvas, pause/warp/stale guidance holds, optional exact rotating-frame dynamics), and **M7
  atmospheric drag**. The **only remaining work is the in-KSA flight validation pass** (deferred — no
  live KSA flight here, as with the rest of the repo's in-game checks). As-built deviations worth noting:
  - **G-FOLD:** the SOCP is non-dimensionalized for solver conditioning; node 0 is bounded (the
    reference's unbounded first node injects a nonphysical impulse); the single-shot Taylor bound is
    valid for G-limits near the fuel-optimal (≈[2.5 g, ∞) on the test lander) — successive
    convexification would widen that.
  - **UPFG:** single-stage, constant-thrust path only (a lander is one stage; the reference's
    multi-stage recursion / coast / constant-acceleration virtual stage are not ported — the G-limit is
    a throttle law, §6.4, not a virtual stage). The steering uses the **velocity-to-go direction**
    `λ = unit(vgo)` with conic-propagated gravity; the orbital `λ̇` corrector (`iF = unit(λ − λ̇·J/L)`) is
    **omitted** for descent because its `lambdade = Q − S·J/L` goes negative in the near-vertical regime,
    swinging `iF` off the braking direction and destabilizing the iteration (and G-FOLD already owns
    position/divert). The closed loop **re-converges from a fresh seed each tick** (carrying the working
    set across ticks induces a predictor-corrector 2-cycle). The terminal throttle is a required-decel
    suicide law capped by the G-limit (`terminal_throttle`), aiming below the cut speed so the touchdown
    gate trips. One latent reference bug was fixed in the CSE port: `A,D,E` are captured from the
    pre-`KIL` `KTTI(xguess)` (the MATLAB warm-starts them from the previous solve, which leaves them
    stale and returns `r ≈ r0` when the first guess is exact, e.g. a circular orbit at half-period).
  - **Hybrid (M5):** the phase handoff is **altitude-latched** — below `handoff_alt` (default 300 m) the
    autopilot switches from G-FOLD braking to UPFG terminal and never reverts (descent is one-way; this
    avoids phase chatter at the boundary). The G-limit caps deceleration on both legs (the SOCP `σ`
    constraint for braking, `terminal_throttle`'s cap for terminal). The braking `Phase` is rendered
    `BRAKING` (still `Phase::Burn` internally).
  - **Atmosphere (M7):** drag is a velocity-quadratic, so it can't be a true G-FOLD decision-variable
    term without breaking convexity. It is instead a **constant acceleration bias** (`Problem.drag_accel`,
    ENU) evaluated once per solve at the current state and added to the gravity vector — crude over the
    horizon (it over-counts drag for the slower late trajectory) but convex, and the ~1 Hz re-solve plus
    the terminal phase refresh it. In the terminal leg the vertical drag deceleration is subtracted from
    the gravity the throttle must overcome. `Cd·A` is a `--drag-area` input (KSA's drag coefficient isn't
    read); density comes from `/sim` `environment/density` (detail-gated → 0/off when absent). Off by
    default (`drag_area = 0`). Aero-entry (bank-angle/lift) guidance remains out of scope (plan §1, §13).
- The program is a **standalone Cargo binary** at `examples/land-o-matic/`. It is **not** part of
  `gatos.slnx`, not in CI, not in `Directory.Build.props`, and needs **no** `THIRD-PARTY-NOTICES.md`
  entry — examples are source-only and user-compiled, exactly like the sibling `gogogo-rs` /
  `simfs-dashboard` / `dashboard-rs` Rust examples it is modeled on.
- Build it incrementally per the **milestones (§12)**. The guidance core is pure, host-testable Rust;
  validate it against the reference repos on the host **before** flying it in-guest.

---

## 1. Goal & scope

Land a KSA vehicle softly and fuel-efficiently on a chosen surface point, under a pilot-set maximum
deceleration (**G-limit**) and minimum **glide-slope**, with a live TUI that shows the state, the
planned trajectory, the solver status, and the time-to-ignition, and accepts live input changes.

In scope:
- Powered descent from a sub-orbital / descending state to touchdown on a (possibly rotating) body.
- Vacuum and thin-atmosphere bodies (atmosphere/drag handled as a disturbance + optional term — §5.7).
- A single active vessel, driven via `/sim/vessels/active/ctl/*`.

Out of scope (initially; see §13): aerodynamic entry guidance (bank-angle/lift), multi-body transfers,
rendezvous. We assume the vehicle is already on a descent trajectory toward the target body.

---

## 2. The guidance concept (UPFG + G-FOLD), one paragraph

**G-FOLD answers "what is the fuel-optimal way down, and when must I light the engine?"** It is a convex
program (a second-order cone program) re-solved a few times per second; it owns the **glide-slope**,
the **thrust/throttle bounds**, the **G-limit**, the **divert** to the aim point, and the **ignition
time**. **UPFG answers "given where I am right now, which way do I point and how hard do I burn this
instant to hit the terminal state?"** It is a cheap predictor-corrector (2–3 iterations) run every
telemetry tick that produces a thrust unit-vector and a time-to-go, with the **G-limit enforced as a
throttle law** (PEGAS's `throttle = m·gLim·g₀ / F`). G-FOLD plans; UPFG flies. Both consume the same
G-limit; both emit the same primitive — a desired CCI thrust direction + a throttle — written to the
same two `/sim` control files.

```
              pilot inputs: target site, G-limit, glide-slope, touchdown speed
                                   │
   /sim telemetry ──► [state in CCI] ─┬─────────────► G-FOLD planner (SOCP, ~1 Hz)
   (pos,vel,mass,μ,ω) │              │                 ├─ feasible? optimal tf? ignition time?
                      │              │                 ├─ fuel-optimal divert trajectory
                      │              │                 └─ aim point + reference thrust profile
                      │              │                                     │ (reference + ignition time)
                      │              └─────────────► UPFG steering (every tick, ~10 Hz)
                      │                                ├─ thrust unit-vector iF, time-to-go
                      │                                └─ throttle = min(G-limit law, reference)
                      ▼                                                    │
              [thrust dir + throttle] ◄───────────────────────────────────┘
                      │
   /sim controls ◄────┴─ ctl/attitude_target (Body→CCI quat aiming +X along iF) + ctl/throttle + ctl/ignite
```

---

## 3. Reference frames — the master analysis (READ THIS FIRST)

Every bug in a guidance port is a frame/units bug. This section is the contract the rest of the plan
obeys.

### 3.1 Frame inventory

**KSA internal frames** (`thirdparty/ksa/KSA/Vehicle.cs`, `VehicleReferenceFrameEx.cs`, `Celestial.cs`):

| Frame | Meaning | Rotating? | Right-handed? | Used for |
|---|---|---|---|---|
| **CCI** | Celestial-Centric **Inertial**: parent-centered, non-rotating, **+Z = spin axis** | No | **Yes** | **canonical position & velocity** (`StateVectors.PositionCci/VelocityCci`), attitude target |
| **CCF** | Celestial-Centric **Fixed**: parent-centered, **spins with the body** | Yes (ω) | Yes | **lat/lon/altitude**, the landing site is *stationary* here |
| **CCE** | Celestial-Centric **Ecliptic**: parent-centered, inertially-oriented | No | Yes | stored attitude `Body2Cce` |
| **Ecl** | System ecliptic (absolute) | No | Yes | cross-body; not needed for single-body landing |
| **ENU** | local East-North-Up at a point: `up=r̂`, `east=(Ẑ×r)/‖·‖`, `north=up×east` | — | Yes | the natural **surface/guidance** frame |

Key KSA facts (all verified, §14):
- **Position & velocity are CCI, double precision, meters / m/s.** `Vehicle.GetPositionCci()`
  (`Vehicle.cs:1696`), `GetVelocityCci()` (`Vehicle.cs:1644`).
- **Velocity is INERTIAL by default.** Surface-relative velocity = `v_cci − ω×r_cci`, with
  `ω = (0,0,GetAngularVelocity())` about CCI **+Z** (`Vehicle.GetSurfaceSpeed()`, `Vehicle.cs:1911-1920`;
  `IParentBody.GetAngularVelocityCci`, `IParentBody.cs:76-80`).
- **Gravity is point-mass inverse-square**, single dominant parent: `a = −(μ/r²)·r̂`,
  `μ = Mass·6.6743e-11` (`BubbleOrigin.GetGravitationPhys`, `BubbleOrigin.cs:146-167`; `IParentBody.cs:15`).
- **Attitude `Body2Cci` is body→world** (active rotation, `v_world = v_body.Transform(Body2Cci)`),
  `Vehicle.GetBody2Cci()` (`Vehicle.cs:1989`). **Thrust acts along body +X** (`FlightComputer` measures
  pointing error on `double3.UnitX`, `FlightComputer.cs:972`; `BurnTarget.ComputeBurnBody2Cci` puts the
  Δv direction in the +X row, `BurnTarget.cs:60-68`).
- **All KSA frames are right-handed** (built from `double3.Cross` triads). g₀ = 9.80665.

**UPFG's frame** (PEGAS): a **planet-centered inertial** (ECI-like) Cartesian frame; position, velocity,
gravity (via conic state extrapolation), and the steering unit-vector `iF` are all expressed in it
(`upfg.md:48-50`, `simulation.md:62`). PEGAS converts game↔UPFG with a **Y↔Z swap (`vecYZ`)** *only
because kOS is left-handed Y-up* (`pegas_util.ks:30-34`). **KSA is right-handed, so we do NOT swap —
UPFG runs directly in KSA's CCI.** This is a critical correction; carrying PEGAS's swap into our port
would be a bug.

**G-FOLD's frame** (`thirdparty/G-FOLD`): a **surface-fixed, target-at-origin, flat-planet** frame with
**constant gravity** (`g = [-g,0,0]`, **axis 0 = up**), curvature and planet rotation ignored over the
short divert; SI units, unscaled (`lcvx_optimizer.py:42`, `gfold_util.py:25,81`,
`vessel_parameters_*.json`). We adopt the same idea but **re-axis to ENU** (axis layout = East, North,
**Up**) to match KSA's `GetEnu2Cci`, and optionally restore the rotating-frame terms (§3.6, §5.7).

### 3.2 Units (SI everywhere; the `/sim` boundary already converts)

meters, m/s, m/s², **kilograms** (KSA mass is kg; **note PEGAS works in kg after a ×1000 from tonnes —
we are already kg, no conversion**), Newtons, **radians internally** (lat/lon and navball are degrees at
the `/sim` boundary), seconds. g₀ = 9.80665. G = 6.6743e-11.

### 3.3 The frames we actually compute in

Three frames, chosen to keep each algorithm in its *native* assumptions:

1. **CCI — the I/O frame.** All `/sim` reads land here and all `/sim` writes originate here
   (`position/cci`, `velocity/cci`, `attitude_target` quaternion). UPFG runs **directly in CCI**
   (its native inertial frame, central gravity), so it needs no frame change at all.
2. **G — target-centered ENU surface frame**, used **only** by the G-FOLD SOCP (which needs a local
   "up" for glide-slope/pointing and a constant gravity). Origin at the landing site; basis
   `(Ê, N̂, Û)` with `Û = r̂_site`, `Ê = normalize(Ẑ_cci × Û)`, `N̂ = Û × Ê` (KSA's `GetEnu2Cci`,
   `VehicleReferenceFrameEx.cs:81-99`). Up is axis 2 (we re-axis G-FOLD's `[up,downrange,crossrange]`
   to `[E,N,U]`; gravity becomes `g_G = (0,0,−g)`).
3. **CCF — the bookkeeping frame** where the site is stationary; used to define the target (lat/lon →
   site direction) and to derive surface-relative velocity. We never integrate in CCF directly; we lift
   to CCI/G.

### 3.4 Input transforms (CCI → guidance inputs)

Per tick, read `r_cci, v_cci, m`, and once at startup read parent `μ, R, ω` (and `rotation` epoch):

```
ω_cci      = (0, 0, ω)                                  # spin about CCI +Z
v_surf_cci = v_cci − ω_cci × r_cci                      # SURFACE-relative velocity (KSA's GetSurfaceSpeed math)
g_mag      = μ / |r_cci|²                               # local gravity magnitude (point-mass)
```

**Target site in CCI (it co-rotates):** from pilot lat/lon and terrain radius `R_t`,
`r_site_ccf = R_t · dir_ccf(lat,lon)` with `dir_ccf = (cos lat cos lon, cos lat sin lon, sin lat)`
(`Celestial.GetDirCcfFromLatLon`, `Celestial.cs:515-523`). Lift to CCI at the current time:
`r_site_cci = Rz(θ(t)) · r_site_ccf`, `θ(t) = ω·t + θ₀` (`Celestial.GetCcf2Cci`, `Celestial.cs:402-407`).
The site's **inertial** velocity is `v_site_cci = ω_cci × r_site_cci` (it moves with the ground).

**Build the ENU basis at the site, expressed in CCI** (this *is* the G→CCI rotation):
```
Û = r_site_cci / |r_site_cci|
Ê = normalize( Ẑ_cci × Û )        # Ẑ_cci = (0,0,1); degenerate only exactly at the poles → fall back to body-frame east
N̂ = Û × Ê
```

**State handed to G-FOLD (in G):**
```
Δr     = r_cci − r_site_cci
r_G    = ( Δr·Ê , Δr·N̂ , Δr·Û )            # downrange/crossrange/altitude, target at origin
v_G    = ( v_surf·Ê , v_surf·N̂ , v_surf·Û ) # surface-relative velocity in ENU  (vertical speed = v_G.z)
g_G    = ( 0, 0, −g_mag )                    # constant over the solve horizon
```
Use **radar altitude** (`altitude/radar`) as the authoritative ground clearance and reconcile with
`r_G.z` (they should agree to terrain roughness; trust radar near touchdown).

**State handed to UPFG (in CCI):** `r_cci`, `v_cci` directly; terminal target = the **site's CCI state**
`(r_site_cci(t+tgo), v_site_cci)` so that "arrive at the site with the ground's velocity" ≡ zero
surface-relative touchdown velocity. (UPFG iterates, so the mild `tgo`-dependence of `r_site_cci` is
absorbed by re-running; update the predicted touchdown site each cycle.)

### 3.5 Output transforms (guidance → `/sim` writes)

Guidance yields a desired **thrust acceleration direction** `d̂` (in G for G-FOLD, in CCI for UPFG) and a
commanded thrust magnitude. Convert and write:

```
d̂_cci = (G-FOLD)  u.x·Ê + u.y·N̂ + u.z·Û     # ENU components → CCI
        (UPFG)     iF                          # already CCI
throttle = clamp( |T_cmd| / F_max ,  min_throttle, 1 )      # F_max = current max thrust (sum engines, vac)
        # …or the G-limit law when accel-limited:  throttle = m·G_limit·g₀ / F_max   (PEGAS, pegas_util.ks:1040)
q = compute_burn_body2cci( r̂_cci , d̂_cci )    # PORT of BurnTarget.ComputeBurnBody2Cci (ksa BurnTarget.cs:60-68)
```

`compute_burn_body2cci(pos_dir, thrust_dir)` (the **exact** KSA recipe — port it verbatim):
```
x = normalize(thrust_dir)                       # body +X → thrust direction
y = cross(thrust_dir, pos_dir); if |y|≈0 { y = any_orthogonal(x) } else { y = normalize(y) }   # roll reference
z = normalize( cross(thrust_dir, y) )
q = quat_from_rows(x, y, z)                      # KSA double4x4 rows = [x;y;z], doubleQuat.CreateFromRotationMatrix
```
Then write (newline-terminated → synchronous errno):
```
echo "<qx> <qy> <qz> <qw>"  > /sim/vessels/active/ctl/attitude_target    # Target2Cci; FlightComputer slews +X→d̂
echo "<throttle>"           > /sim/vessels/active/ctl/throttle
echo 1                      > /sim/vessels/active/ctl/ignite             # at ignition; ctl/shutdown after touchdown
```

> **Correctness guarantee, not convention-guessing.** Rather than reason about Hamilton sign
> conventions, **port KSA's own quaternion math** (`doubleQuat.CreateFromRotationMatrix`,
> `doubleQuat.Concatenate`, `double3.Transform`) from `thirdparty/ksa/Brutal.Numerics/` and unit-test
> the invariant `transform(UnitX, compute_burn_body2cci(p̂, d̂)) ≈ d̂`. Because the FlightComputer
> measures attitude error on `UnitX.Transform(rotation)` (`FlightComputer.cs:966-1015`), satisfying that
> invariant **is** the proof that the vehicle's thrust axis aims at `d̂`. The roll DOF (FlightComputer
> resolves it via `UnitZ`, `FlightComputer.cs:1000`) is free for a lander; the position-vector roll
> reference keeps it continuous so the autopilot doesn't thrash.

**Pre-ignition** (before G-FOLD's ignition time): hold a surface-retrograde attitude (write a
`compute_burn_body2cci(r̂, −v̂_surf_cci)` quaternion, or simply `attitude_mode=Retrograde`), throttle 0,
and display the countdown. **Do not** use `ctl/burn` — that is an *impulsive* Δv maneuver primitive
(`FlightComputerActuator.SetBurn`, executed by the autopilot via the rocket equation); continuous
powered descent needs the `attitude_target` + `throttle` pair.

### 3.6 The rotating-planet question (Coriolis / centrifugal) — and why frequent re-solve saves us

G-FOLD's flat-planet assumption ignores planet rotation. For a landing on a *rotating* body the site
moves in inertial space, so we must be deliberate:

- **UPFG is exact here.** Run in CCI with central gravity; target the site's CCI state. No flat-planet
  approximation — rotation enters only through the (re-computed each cycle) target position. **This is
  the rigorous leg of the hybrid** and the reason UPFG owns the terminal phase.
- **G-FOLD works in the co-rotating G frame**, where the site is fixed. The fictitious accelerations
  there are **centrifugal** `−ω×(ω×r)` (linear in `r`) and **Coriolis** `−2ω×v` (linear in `v`). Both
  are *linear*, so they can be added to the discretized dynamics **without breaking convexity** (§5.7).
  - **MVP:** omit them (vanilla G-FOLD), fold centrifugal into a constant `g_eff` at the site, and let
    the **closed-loop re-solve reject the rest** — this is exactly how MPC tolerates model error, and we
    never fly G-FOLD open-loop.
  - **Rigorous:** include the exact linear Coriolis/centrifugal terms (cheap, convex). Recommended once
    the MVP flies, especially for fast-rotating bodies or long high divert.
- **Error budget** (decide per body): centrifugal ≈ `ω²·R·cos(lat)`; Coriolis ≈ `2·ω·v`. For slow
  bodies these are ≪ thrust accel and the re-solve absorbs them; for fast bodies, enable §5.7. The TUI
  surfaces the modeled-vs-unmodeled residual (commanded vs measured `environment/accel`) so the pilot
  sees if the model is fighting reality.

### 3.7 Frame correctness checklist (gate every milestone against this)

- [ ] Velocity used for guidance is **surface-relative** (`v_cci − ω×r`), never raw inertial, except
      where UPFG intentionally targets the site's inertial velocity.
- [ ] `Ẑ_cci = (0,0,1)` is the **spin axis** (north); ENU east uses `Ẑ×r`.
- [ ] **No Y↔Z swap** anywhere (that was a kOS artifact; KSA is right-handed).
- [ ] Gravity is `μ/r²` toward center, `μ` read from `bodies/<parent>/mu` (or `Mass·6.6743e-11`).
- [ ] `attitude_target` is a **`Target2Cci`** quaternion whose **+X** maps to the thrust direction,
      verified by the ported `transform(UnitX, q) ≈ d̂` test.
- [ ] Mass is **kg** already (no tonnes ×1000 — that's a PEGAS-from-KSP artifact).
- [ ] Altitudes: **radar** = terrain-relative (use for touchdown), **barometric** = mean-sphere
      (don't use near terrain).
- [ ] Lat/lon are **CCF** (body-fixed), geocentric `asin(z)`/`atan2(y,x)` — fine for point targeting.

---

## 4. Data interface (`/sim`) — exact reads & writes

Mount point inside the guest is **`/sim`** (`mount -t 9p … 10.0.2.2 /sim`, `guest/rootfs-overlay/sbin/sim-mount`).
Reads are one-shot `read_to_string` (the `cache=none` mount re-snapshots each open); writes are one
newline-terminated `fs::write` carrying the real errno. All vessel paths use the
`vessels/active/…` alias (no vessel id needed).

### 4.1 Reads (the guidance input set)

Prefer the **atomic** doc to avoid stitching scalars across ticks:
```
/sim/vessels/active/telemetry      # one JSON object: seq, ut, pos_cci[xyz], vel_cci[xyz], vel{orb,surf,inr},
                                   #   alt{baro,radar}, mass{t,d,p}, att_q[xyzw], orbit{…}, …  (Formats.VesselTelemetry)
```
Individual scalars (all CCI/SI unless noted; `G9` doubles):

| Path | Meaning | Units / frame |
|---|---|---|
| `time/ut` | sim universal time | s |
| `time/sim_dt` | sim seconds advanced last tick (**0 ⇒ paused**) | s |
| `time/warp` | time-warp factor | — |
| `vessels/active/position/cci` | position | m, **CCI** |
| `vessels/active/velocity/cci` | velocity | m/s, **CCI inertial** |
| `vessels/active/velocity/surface` | speed magnitude | m/s, surface-relative (scalar only) |
| `vessels/active/altitude/radar` | height above terrain/ocean | m |
| `vessels/active/altitude/barometric` | height above mean radius | m |
| `vessels/active/attitude/quat` | orientation | quat `x y z w`, **Body→CCI** |
| `vessels/active/attitude/rates` | body rates | rad/s, body axes |
| `vessels/active/mass/{total,dry,propellant}` | masses | kg |
| `vessels/active/position/{lat,lon}` | site of vehicle | deg, **CCF** |
| `vessels/active/engines/<n>/{vac_thrust,isp,min_throttle,throttle,propellant,active}` | per-engine | N, s, frac, … |
| `vessels/active/environment/{accel,g_force,density,dynamic_pressure}` | sensed accel / drag env | m/s² (body), g, kg/m³, Pa — **detail-gated** |
| `bodies/<parent>/{mu,radius,rotation_rate}` | gravity model | m³/s², m, rad/s — **bodies-gated** |

**Derived (compute, not provided):**
- **Vertical speed** = `v_surf_cci · r̂_cci`; **horizontal speed** = `|v_surf_cci − (v_surf_cci·r̂)r̂|`.
  `/sim` exposes only the surface *speed scalar*, not the vector — reconstruct via `v_cci − ω×r`.
- **Max thrust `F_max`** = Σ `engines/<n>/vac_thrust` over active engines (or read once; recompute on
  staging). **Effective exhaust velocity** `ve = Isp·g₀`; **α** (mass-flow per N) `= 1/ve`.
- **Surface gravity** `g = μ/r²` (compute from `bodies/<parent>/mu` and `|position/cci|`).

### 4.2 Writes (the control set)

| Path | Grammar | Effect | Phase / latency |
|---|---|---|---|
| `vessels/active/ctl/attitude_target` | `x y z w` (quat) | sets `AttitudeTarget.Target2Cci`, mode Auto+Custom | **solver-phase** (next solver step) |
| `vessels/active/ctl/attitude_mode` | enum (`manual`,`Retrograde`,`Custom`,…) | named track-target / manual | solver-phase |
| `vessels/active/ctl/attitude_frame` | enum (`EnuBody`,`Lvlh`,`VlfBody`,…) | reference frame for named modes | solver-phase |
| `vessels/active/ctl/throttle` | `0..1` | manual throttle | frame-phase (next frame) |
| `vessels/active/ctl/ignite` / `ctl/shutdown` | `1` | ignite / shut down active stage | frame-phase |
| `vessels/active/ctl/engine` | `0`/`1` | master ignite toggle (also readable = `EngineOn`) | frame-phase |
| `vessels/active/ctl/stage` | `1` | activate next stage | frame-phase |
| `debug/vessels/<id>/teleport` | `px py pz vx vy vz` (CCI) | cheat: set state — **for test setups** | frame-phase |

> **Why attitude is solver-phase (latency model, important):** KSA's async vehicle solver deep-copies
> the whole `FlightComputer` at *prepare* and restores it at *apply* (`FlightComputer.CopyFrom`,
> `Vehicle.cs:1606-1618`; `VehicleUpdateData.cs:69`). gatOS routes `attitude_*`/`burn` writes into a
> `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers` so they land **before** the snapshot
> and stick (`SimCommand.SolverActions`). The TUI just writes the file; effect appears on the next
> solver step — so the closed loop must tolerate ~one-step attitude latency (it does; the FC also slews
> at a finite rate). Throttle is frame-phase and effectively immediate.

### 4.3 Pacing (never `sleep` on the wall clock)

The guest wall clock is decoupled from sim time under warp. Pace the control loop against **sim time**:
read `time/ut` deltas, or block on **`/sim/time/alarm`** (write a target UT, read parks until reached).
Run the guidance loop at the telemetry cadence (default **`sample_rate_hz = 10`**, clamped 1–120). Do
not assume a fixed Δt; integrate against actual `ut` deltas. If `time/sim_dt == 0` (paused) or warp is
high, hold the last command and surface a "paused/warp" indicator.

### 4.4 Resilience to telemetry gates

- `environment/{accel,g_force,density,dynamic_pressure}`, `navball/*`, and the `ctl/*` read-backs
  require **`telemetry_vessel_detail=true`**; `bodies/*` (incl. `mu`) require **`telemetry_bodies=true`**.
- **Be self-sufficient:** read `bodies/<parent>/{mu,radius,rotation_rate}` **once at startup** and
  compute gravity ourselves; derive vertical/horizontal speed from `velocity/cci`. Then the loop needs
  only **core** fields (position/velocity/attitude/altitude/mass/engines), which survive detail-off.
- The TUI should detect missing detail fields and either prompt the pilot to enable the gate or fall
  back to computed values, never silently use zeros (note: non-finite `/sim` scalars sanitize to 0).

---

## 5. Algorithm 1 — G-FOLD planner (the convex program)

Faithful to `thirdparty/G-FOLD` (Blackmore two-stage over Açıkmeşe–Ploen lossless convexification),
re-axised to ENU, extended with the **G-limit** the reference declares but never enforces
(`G_max` is dead in `vessel_parameters_*.json`), and adapted for receding-horizon (MPC) re-solve.

### 5.1 Problem — two stages

- **P3 (min landing error)**: minimize `‖(r_N)_horiz − target_horiz‖` with `r_N.z = 0` (on the ground),
  to find the **closest reachable aim point**. Run **once at ignition** (and on big disturbances).
- **P4 (min fuel)**: maximize `z_N = ln m_N` (≡ min fuel) with `r_N` **pinned** to the P3 aim point.
  Run **every replan** thereafter.

(`gfold_util.py:90-116` chains P3→P4; `lcvx_optimizer.py:60-67` are the objectives.)

### 5.2 Variables & discretization

Over `N` nodes (start N≈30; pilot/profile-tunable 20–60), `dt = tf/N`:
`r_n∈ℝ³` (G frame), `v_n∈ℝ³`, `z_n∈ℝ (=ln m)`, `u_n∈ℝ³ (=T/m, thrust accel)`, `σ_n∈ℝ` (slack).
**Trapezoidal** collocation (matches the repo; ZOH is an option):
```
v_{n+1} = v_n + (dt/2)[(u_n + g_G) + (u_{n+1} + g_G)]            (+ Coriolis/centrifugal, §5.7)
r_{n+1} = r_n + (dt/2)(v_{n+1} + v_n)
z_{n+1} = z_n − (α·dt/2)(σ_n + σ_{n+1})                          α = 1/(g₀·Isp)
```

### 5.3 Constraints → Clarabel cones

Assemble `Ax + s = b`, `s ∈ K`. Cone types per constraint (Clarabel `SupportedConeT`):

| Constraint | Form | Cone |
|---|---|---|
| Dynamics (above), boundary conds | equalities | `ZeroConeT` |
| Initial state | `r_0=r_G`, `v_0=v_G`, `z_0=ln m_wet` | `ZeroConeT` |
| Terminal | `v_N=0`; (P4) `r_N=aim`, (P3) `r_N.z=0`; `u_N=σ_N·Û`, `σ_N=0` | `ZeroConeT` |
| Thrust slack (lossless) | `‖u_n‖ ≤ σ_n` | `SecondOrderConeT(4)` |
| Thrust **upper** (Taylor of `ρ₂e^{−z}`) | `σ_n ≤ μ₂ₙ(1 − (z_n − z0_n))` | `NonnegativeConeT` |
| Thrust **lower** (Taylor of `ρ₁e^{−z}`) | `σ_n ≥ μ₁ₙ(1 − Δz + ½Δz²)`, Δz=z_n−z0_n | rotated→`SecondOrderConeT` (§5.4) |
| **G-limit (new)** | `σ_n ≤ G_limit·g₀` | `NonnegativeConeT` |
| Glide-slope | `‖(r_n−r_N)_horiz‖ ≤ cot(γ_gs)·(r_n−r_N).z` | `SecondOrderConeT(3)` |
| Thrust pointing | `u_n.z ≥ cos(θ_pt)·σ_n` | `NonnegativeConeT` |
| Velocity cap | `‖v_n‖ ≤ V_max` | `SecondOrderConeT(4)` |

where `z0_n = ln(m_wet − α·ρ₂·t_n)`, `μ₁ₙ = ρ₁/(m_wet − α·ρ₂·t_n)`, `μ₂ₙ = ρ₂/(…)`,
`ρ₁ = throttle_min·F_max`, `ρ₂ = F_max` (`gfold_util.py:15-18,76-78`; `lcvx_optimizer.py:50-56`).

**MVP deviations from the repo (deliberate, for receding-horizon use):**
- **Drop** the "initial thrust vertical" constraint (`u_0 = σ_0·Û`, `lcvx_optimizer.py:32`): in MPC the
  current thrust direction is arbitrary, not vertical. Keep the *terminal* vertical/`σ_N=0` for a soft
  upright touchdown.
- **Relax `θ_pt` (pointing) during braking:** a fast horizontal arrival needs near-retrograde
  (≈horizontal) thrust; a tight cone-to-vertical makes the high divert infeasible. Make `θ_pt` a
  parameter, generous early, tightened near terminal (or hand terminal to UPFG, §7).
- **Glide-slope sign:** `γ_gs` is the minimum elevation of the vehicle as seen from the pad
  (`height/horiz ≥ tan γ_gs`); small γ = permissive, large γ = steep mandatory approach. Pilot input.

### 5.4 Convexification details

- **Lossless relaxation:** the real constraint `‖u‖ = σ` is relaxed to `‖u‖ ≤ σ` (SOC); provably tight
  at the optimum. The thrust *magnitude* bounds become bounds on `σ` against `e^{−z}`, **Taylor-expanded
  about the reference mass trajectory `z0_n`** so no exponential appears — a **pure SOCP**
  (ECOS solves the repo's P3 with no exp-cone, proving it). 
- **Lower-bound as a rotated SOC:** `σ_n ≥ μ₁(1 − Δz + ½Δz²)` ⇔ `(σ_n − μ₁ + μ₁Δz) ≥ ½μ₁Δz²`. With
  `a = σ_n − μ₁ + μ₁Δz` (affine) and `w = √μ₁·Δz`, this is the rotated cone `2·a·(½) ≥ w²`, i.e.
  `a ≥ ½ w²` → standard `SecondOrderConeT(3)` via `‖(w, (a−½))‖ ≤ (a+½)`. Provide this transform in code
  and unit-test it.
- **Alternative (Clarabel-only):** Clarabel supports `ExponentialConeT`, so the mass-thrust coupling can
  be modeled *exactly* without the Taylor step. **Recommend the Taylor/SOCP path first** (matches the
  validated reference, better conditioned); keep exp-cone as a documented option.
- **Conditioning:** the SOCP is in raw SI (meters, kg). For real-time robustness, **non-dimensionalize**
  (scale length by `R` or initial altitude, time by `√(R/g)`, mass by `m_wet`) before handing to
  Clarabel, and unscale outputs. Note the repo does *not* scale and can be ill-conditioned; we should.

### 5.5 Time-of-flight search & ignition timing (the suicide burn)

`tf` is not fixed — the repo searches it by **golden-section**, re-solving per trial
(`gfold_util.py:28-51`), bounded by `[m_dry·|v0|/ρ₂ , m_fuel/(α·ρ₁)]`. We keep golden-section for the
**initial** plan; in steady state we **shrink the horizon** as `tf` counts down and warm-start. The
**ignition time** falls out: scan ignition delay (coast then burn) for the **latest** ignition whose
optimal solve is still feasible within the G-limit — that is the fuel-optimal suicide-burn. Pre-ignition
the TUI shows this countdown; UPFG's `tgo` cross-checks it.

### 5.6 Closed-loop use (MPC)

Each replan: set `r_0,v_0,z_0 =` current measured state; solve (P4 to the fixed aim point); **apply only
the first node** — `T_cmd = u_0·m_now`, direction `û_0` → `attitude_target`, magnitude → `throttle`.
Re-solve at ~1 Hz (or immediately when the pilot changes G-limit / glide-slope / target). Between
re-solves, UPFG (faster) tracks the latest reference (§7). If a solve returns infeasible, hold the last
feasible command, raise an **ABORT/DIVERT** alert, and fall back to P3 to re-find a reachable aim point.

### 5.7 Optional exact rotating-frame dynamics (the rigorous upgrade)

In the co-rotating G frame, add to the velocity dynamics the **linear** terms
`−2 ω_G × v_n` (Coriolis) and `−ω_G × (ω_G × r_n)` (centrifugal), where `ω_G` is the body spin in G
coordinates. Both are linear in the decision variables, so they fold into the equality `A` matrix with
**no loss of convexity** and no new cones. Gate behind a `--rotating-dynamics` flag; validate it changes
the trajectory in the expected direction (eastward Coriolis deflection). For slow bodies, leave off.

---

## 6. Algorithm 2 — UPFG terminal guidance (the port)

A faithful Rust port of `unifiedPoweredFlightGuidance` (canonical math:
`PEGAS-MATLAB/MATLAB/unifiedPoweredFlightGuidance.m`; game integration: `PEGAS/kOS/pegas_upfg.ks`),
adapted for descent.

### 6.1 The pure-function port

UPFG is a **pure function** `upfg(vehicle, target, state, previous) → (current, guidance)` whose entire
memory is the `previous`/`current` struct — trivially re-entrant, an ideal Rust port. Implement the
blocks verbatim:
- Persistent struct: `cser` (conic-state-extrapolation state), `rbias, rd, rgrav, tb, time, tgo, v, vgo`
  (`unifiedPoweredFlightGuidance.m:238-247`).
- Block 1 vehicle params (`ve=Isp·g₀`, `aT=F/m`, `tu=ve/aT`), Block 2 `vgo -= Δv_sensed`, Block 3
  time-to-go via the per-stage thrust integral `L` (Tsiolkovsky), Block 4 the **L,J,S,Q,P,H** integrals
  (`uPFG.m:141-185`), Block 5 the steering law `iF = unit(λ − λ̇·J/L)` (`uPFG.m:187-208`), Block 7 gravity
  via **CSE** (port `CSEroutine.m`, a Shepperd/Kepler conic propagator), Block 8 the terminal correction.
- Port the **CSE magic coefficients** (`rc1 = r − 0.1·rthrust − (tgo/30)·vthrust`, etc.,
  `uPFG.m:219-220`) verbatim — they minimize coast-vs-powered error and must not be "cleaned up."
- Convergence is the **host's** job: re-call until `Δtgo` between iterations < 0.5 s and the steering
  vector moves < ~15° (PEGAS's `upfgConvergenceCriterion`/`GoodSolutionCriterion`,
  `pegas_settings.ks:24,27`); 2–3 iterations typical.

### 6.2 Descent adaptation

From the PEGAS analysis (the author's note that UPFG "also works for descent"; deorbit is an explicit
UPFG mode, `upfg.md:86`):
- **Terminal velocity points down.** The `vd = vdval·[ix;iy;iz]·[sinγ;0;cosγ]` construction
  (`uPFG.m:226-235`) already yields a downward vector at `γ = −90°` (`sin(−90°)=−1` along `ix=r̂`,
  i.e. −radial). Use a small `vdval` (touchdown speed, e.g. 1–3 m/s) and `γ≈−90°`, **or** build `vd`
  directly as the site's inertial velocity (so surface-relative touchdown speed ≈ 0).
- **Target a point, not a plane.** Skip the orbit-plane projection (`uPFG.m:228`); set `rd` = the site's
  CCI position. To null cross-range, set `iy = normalize(cross(Û, v̂_horiz))` so the in-plane machinery
  drives lateral error to zero. **Preserve UPFG's negative-normal sign convention** wherever the
  `iy`/cross-product machinery is reused (`upfg.md:140`).
- **Bootstrap seed:** PEGAS seeds `rd` by rotating the position 20° prograde (`pegas_util.ks:339`) —
  wrong for descent. Seed `rd` ≈ the site, `vgo ≈ vd − v` directly.
- **Termination:** replace the orbital "reached target velocity"/angular-momentum cutoff with
  "radar altitude ≤ touchdown height **and** surface speed ≤ touchdown speed" → `ctl/shutdown`.

### 6.3 Frame choice (run UPFG in CCI)

Run UPFG **directly in CCI** (right-handed, central gravity — its native assumptions; **no `vecYZ`
swap**). The terminal target is the **site's CCI state** at predicted touchdown,
`(r_site_cci(t+tgo), v_site_cci = ω×r_site_cci)`. CSE's central inverse-square gravity is exactly KSA's
gravity model, so this leg is rigorous (no flat-planet error). Update the predicted touchdown site each
cycle (mild `tgo` coupling, absorbed by iteration).

### 6.4 G-limit throttle law

When the burn is acceleration-limited (the usual case in terminal descent), set
`throttle = m·G_limit·g₀ / F_max` (PEGAS `throttleControl`, `pegas_util.ks:1040`) recomputed from
**measured mass** every tick — a continuous throttle-down holding deceleration at exactly the G-limit.
Otherwise follow G-FOLD's reference throttle. Clamp to `[min_throttle, 1]`; the deep-throttle floor
(`min_throttle`) caps how low the limiter can hold (PEGAS's `constAccBurnTime` models the same floor).

### 6.5 Output

UPFG yields `iF` (CCI thrust unit-vector) and `tgo`. Convert exactly as §3.5
(`q = compute_burn_body2cci(r̂_cci, iF)` → `attitude_target`; G-limit law → `throttle`). `tgo` drives the
TUI countdown and the terminal handoff.

---

## 7. Combining the two (flight phases, handoff, the G-limit lever)

| Phase | Active guidance | Attitude | Throttle | Exit |
|---|---|---|---|---|
| **Coast / pre-ignition** | G-FOLD planning only | surface-retrograde hold | 0 | reach G-FOLD ignition time / pilot ENGAGE |
| **Braking (high divert)** | **G-FOLD MPC** (apply first node) | `attitude_target` = `û_0` | first-node \|T\|, capped by G-limit | low altitude / low `tgo` (handoff gate) |
| **Terminal descent** | **UPFG** (CCI, site target) | `attitude_target` = `iF` | G-limit law → near-zero at touchdown | radar alt ≤ h_td & surface speed ≤ v_td |
| **Touchdown / safe** | none | upright hold | `ctl/shutdown` | landed |

- **Why this split:** G-FOLD's coarse discretization is strongest for the *strategic* divert and the
  glide-slope/G-limit envelope; it is weak at the very end (few nodes, pinned terminal). UPFG's
  continuous predictor-corrector with central gravity is strongest for the *precise* terminal null. The
  handoff gate (e.g. altitude < ~500 m or `tgo` < ~15 s, tuned per vehicle) trades planning for tracking.
- **Simplest viable variant (MVP):** **G-FOLD-only MPC** all the way down (apply first node each
  re-solve), no UPFG. Lands, just less precisely at the end. Add UPFG in M5. The milestones build it in
  this order so there is always a flyable program.
- **The G-limit lever (the headline feature):** the pilot's G-limit feeds **both** legs — the G-FOLD
  `σ_n ≤ G_limit·g₀` constraint (re-solve → new optimal trajectory, new ignition time) **and** the UPFG
  throttle law. Changing it live visibly re-plans the trajectory and re-throttles the burn — the descent
  analogue of PEGAS's live G-limit demo. Same for glide-slope, target site, and touchdown speed.

---

## 8. Rust architecture

### 8.1 Location & `Cargo.toml`

`examples/land-o-matic/` (standalone binary, MIT, `publish = false`), cloned from `gogogo-rs`. Build
**in-guest** with Alpine's musl Rust (`apk add cargo rust`), so everything is musl-static by default.

```toml
[package]
name = "land-o-matic"
version = "0.1.0"
edition = "2021"
description = "ratatui powered-descent guidance (UPFG + G-FOLD) over the gatOS /sim filesystem"
license = "MIT"
publish = false

[[bin]]
name = "land-o-matic"
path = "src/main.rs"

[dependencies]
ratatui = "0.29"                                   # re-exports crossterm (use ratatui::crossterm)
clarabel = "0.11"                                  # native-Rust conic solver; DEFAULT FEATURES ONLY (pure Rust, musl-clean)
nalgebra = "0.34"                                  # f64 Vector3/UnitQuaternion/Matrix3 for the guidance math
ureq = { version = "2", default-features = false } # optional host HTTP /v1/fs dev source; no TLS (slirp is http://)
serde = { version = "1", features = ["derive"] }
serde_json = "1"                                   # parse /sim/.../telemetry
toml = "0.8"                                       # optional vessel/profile presets

[profile.release]
opt-level = "z"
lto = true
strip = true
```
**musl rule:** never enable Clarabel's `sdp`/`blas`/`lapack`/`mkl`/`openblas` features (they drag in
C/Fortran and break the static build) and never add OpenSSL TLS. Default Clarabel ships its own Rust
QDLDL — SOCP (+exp-cone) needs nothing else.

### 8.2 Module layout (mirror gatOS's "game-free core" discipline)

```
examples/land-o-matic/src/
  main.rs        # CLI args, terminal setup/teardown + panic hook, spawn worker, run UI loop  (≈ gogogo-rs/main.rs)
  app.rs         # App state: pilot inputs (G-limit, target, glide-slope), phase, latest snapshot+solution, keymap
  ui.rs          # ratatui render: HUD panels, trajectory canvas, gauges, input fields, status
  sim/
    mod.rs       # Source trait + FsSource + HttpSource (extend gogogo-rs/source.rs read set) + Telemetry struct
    pacing.rs    # sim-time pacing via time/ut & time/alarm
  guidance/      # ★ PURE, host-testable. No I/O, no ratatui. The whole point of the discipline.
    mod.rs       # GuidanceCore: ingest Snapshot → emit Command {thrust_dir_cci, throttle, phase, diagnostics}
    frames.rs    # CCI↔ENU(G), surface velocity, ENU basis, site lat/lon→CCI(t)
    ksa_quat.rs  # PORT of doubleQuat.{CreateFromRotationMatrix,Concatenate,Inverse}, double3.Transform, ComputeBurnBody2Cci
    gfold.rs     # build & solve the SOCP via clarabel (cones per §5.3); golden-section tf; MPC first-node
    upfg.rs      # the UPFG port (blocks 1–8) + CSE (CSEroutine port); descent target builder
    vehicle.rs   # VehicleModel: F_max, Isp/ve, α, min_throttle, mass; rebuilt on staging
    types.rs     # Vec3=nalgebra, Snapshot, Command, Limits {g_limit, glide_slope, v_touchdown, ...}
```

**Binding internal rule (mirrors gatOS's dependency rule):** `guidance/` must compile and unit-test on a
bare host with **no `/sim`, no game, no terminal** — only `nalgebra` + `clarabel`. `sim/` and `ui/` never
appear in `guidance/`. This is what lets us validate UPFG against MATLAB and G-FOLD against Python on the
host (x86_64) before ever booting the guest.

### 8.3 Threading & loop (from `gogogo-rs`, extended)

- **UI thread:** `terminal.draw` + `crossterm` event poll (keys/mouse/resize); drains `FromWorker`
  snapshots/solutions; never blocks on I/O or the solver.
- **Worker/guidance thread:** owns the `Source`; each cycle: pace to sim time → read telemetry → run
  `GuidanceCore::step` (transforms + UPFG every tick; G-FOLD re-solve when due) → write `ctl/*` →
  send the snapshot+solution+diagnostics to the UI. Coalesce pilot input changes (last value wins) like
  the gogogo-rs throttle batch.
- **Stack size:** spawn the worker with an explicit large stack —
  `thread::Builder::new().stack_size(8<<20)` — because musl's default thread stack (~80–128 KiB) is too
  small for interior-point + nalgebra frames. (Host-glibc dev won't show this; the guest will.)
- Channels: `ToWorker { SetGLimit(f64), SetTarget(LatLon|Here|Downrange(f64)), SetGlideSlope(f64),
  Engage, Abort, … }`, `FromWorker { Snapshot, Solution, WriteResult, Diagnostics }`.

### 8.4 The `Source` trait

Reuse `gogogo-rs`'s `Source`/`FsSource`/`HttpSource` verbatim (read trims trailing `\n`; write is one
newline-terminated payload mapping `raw_os_error` to the frozen errno vocabulary). Add a
`read_telemetry()` that parses `vessels/active/telemetry` JSON into a `Snapshot`, and a one-shot
`read_body(parent)` for `mu/radius/rotation_rate`. Default backend = `/sim`; `--url`/`$GATOS_HTTP`
selects HTTP `/v1/fs` for host-side dev against a running mod.

---

## 9. TUI design (ratatui)

A guidance HUD that stays out of the way (the repo's "transparent over the game" rule: leave widget
backgrounds unset so purrTTY shows the game through; color the foreground; fills only on header/status).

### 9.1 Layout & panels

```
┌ land-o-matic ───────────────────────────── phase: BRAKING · fs:/sim · ut 12345.6 ┐
│ STATE                          │ TRAJECTORY (downrange × altitude, Canvas/Braille) │
│  alt(radar)   1 240 m          │   ╲  planned G-FOLD path ······                   │
│  v_vert        -82.4 m/s       │    ╲___ current ●                                 │
│  v_horz         31.0 m/s       │        ╲      glide-slope cone ────               │
│  surface spd    88.0 m/s       │          ╲___________________ pad △              │
│  mass         5 980 kg         ├───────────────────────────────────────────────── │
│  TWR (now)      1.9            │ THRUST / THROTTLE                                  │
│  g-load (meas)  1.4 g          │  throttle ▓▓▓▓▓▓▓░░░  68%   cmd |T| 41.2 kN        │
│  fuel          1 180 kg        │  g-limit  [ 3.0 g ]  ◄ ↑/↓     accel 1.4/3.0 g    │
│                                │  glide    [ 30°  ]            point err 2.1°       │
│ PLAN (G-FOLD)                  ├───────────────────────────────────────────────── │
│  solve  OK  18 ms  P4 N=30     │ INPUTS / ACTIONS                                   │
│  tgo (gfold)    24.8 s         │  target: HERE↓  (lat -0.10 lon 12.4) [t] set      │
│  ignition       T-00:03.1      │  [e] ENGAGE   [a] ABORT   [r] re-solve  [q] quit  │
│  fuel @ td      940 kg         │                                                   │
│ UPFG  tgo 23.9s  conv ✓ 3it    │ STATUS  attitude_target ✓  throttle ✓             │
└──────────────────────────────────────────────────────────────────────────────────┘
```

Widgets (all musl-clean ratatui): **Canvas + Braille** for the trajectory/glide-slope plot (planned path,
current marker, cone, pad); **Gauge/LineGauge** for throttle, fuel, g-load-vs-limit, time-to-ignition;
**Chart/Sparkline** for altitude & vertical-speed history; styled **Paragraph** blocks for the numeric
HUD; an input row for editable fields.

### 9.2 Inputs / keybindings

`↑/↓` adjust the focused field (G-limit, glide-slope, touchdown speed, horizon N); `Tab` cycles fields;
`t` set target (cycle HERE / pad-by-latlon / downrange-offset; numeric entry); `e` ENGAGE (arm the burn —
the program will ignite at the computed time); `a` ABORT (cut throttle, hold retrograde, stop writing
guidance); `r` force re-solve; `space` hold/resume; `q` quit (cuts throttle, restores terminal). Every
input change that affects the plan triggers a G-FOLD re-solve (the live "watch it re-plan" behavior).

### 9.3 Safety affordances

A prominent **ABORT** that cuts throttle and reverts attitude to manual; an **infeasible** banner when
G-FOLD can't find a solution within the G-limit (suggests raising the G-limit or it's too late to land);
a **paused/warp** indicator (`time/sim_dt==0` / high `warp`) since guidance shouldn't command during
warp; a **stale telemetry** indicator if `seq`/`ut` stops advancing.

---

## 10. Build, deploy, run

In-guest (the intended path, from the sibling examples' READMEs):
```sh
apk add --no-cache cargo rust            # one-time; Alpine musl toolchain → static binary by default
cd /mnt/<host-mount>/land-o-matic        # source via a host-folder mount (gatos.toml [[mounts]], guest v10+)
#   …or: git clone the repo in-guest (git is in the image); …or cp into /root
cargo run --release                      # reads /sim, drives the active vessel
```
- **Resource headroom:** Clarabel+nalgebra is a much heavier build than the existing examples. Bump the
  guest `disk_size_gb` and `memory_mb` in `gatos.toml` before the first `cargo build --release` (rustc is
  memory-hungry); `opt-level="z" + lto + strip` keeps the binary small for the small guest disk.
- **Host dev loop:** the `guidance/` crate and its tests run on the host (`cargo test` on x86_64 with
  glibc) with **no guest needed** — this is where algorithm validation happens. The full TUI can run on
  the host against a fixture `--root ./fixture` or a live mod via `--url $GATOS_HTTP`.
- Not wired into `gatos.slnx`, CI, or `Directory.Build.props`; no `THIRD-PARTY-NOTICES.md` change. Add a
  `README.md` in the crate (build/run, the frame contract summary, safety notes) per example convention.

---

## 11. Validation & testing

### 11.1 Unit (host, `guidance/`)
- **Frames:** round-trip CCI↔ENU; `v_surf = v_cci − ω×r` against a hand-computed equatorial case;
  ENU basis orthonormal & right-handed; site lat/lon→CCF→CCI→lat/lon identity.
- **Quaternion (the critical one):** `transform(UnitX, compute_burn_body2cci(p̂, d̂)) ≈ d̂` for many
  random `(p̂,d̂)`; compare the ported `CreateFromRotationMatrix` to a few values computed from the KSA
  source by hand.
- **G-FOLD pieces:** the rotated-SOC encoding of the quadratic lower bound equals the original
  inequality at sampled points; trapezoidal integrator reproduces a constant-thrust analytic arc.
- **UPFG pieces:** CSE conic propagation matches a Keplerian two-body propagation to tolerance; thrust
  integrals L,J,S match closed forms for a single constant-thrust stage.

### 11.2 Parity vs the reference implementations (host)
- **G-FOLD:** feed `vessel_parameters_earth.json` / `_mars.json` initial states into our solver and
  compare the trajectory, thrust profile, and final mass to the Python repo's output
  (`demo_data.html`/`demo_traj.html`). Same SOCP ⇒ same optimum (allow solver tolerance). This is the
  acceptance test for `gfold.rs`.
- **UPFG:** drive our port with a known PEGAS-MATLAB **ascent** case and match `iF`/`tgo` per cycle
  against `unifiedPoweredFlightGuidance.m`; then a constructed **descent** case (a suborbital target)
  for sanity. This is the acceptance test for `upfg.rs`.

### 11.3 Closed-loop simulation (in-game KSA — the deferred in-game pass)
- Use `debug/vessels/<id>/teleport` (CCI state) to set up a repeatable descent; fly it; measure
  **touchdown position error**, **touchdown surface speed**, **peak g-load vs G-limit**, **fuel used vs
  G-FOLD's predicted optimum**, and **glide-slope adherence**. Sweep the G-limit live and confirm
  re-planning + the predicted fuel/ignition changes. Record results in `examples/land-o-matic/README.md`
  (and, if it graduates beyond an example, a `docs/VALIDATION.md` entry).
- Bodies to cover: a vacuum body (no drag, clean test of the core), then an atmospheric body (exercise
  the drag disturbance / §5.7).

### 11.4 Numerical conditioning
- Confirm the non-dimensionalized SOCP (§5.4) solves in **single-digit milliseconds** at N=30 in-guest
  (musl) so ~1 Hz re-solve is comfortable; profile `clarabel` iterations; cap `max_iter` and handle the
  cap as "infeasible → hold/ABORT," never a hang.

---

## 12. Milestones

Each milestone ends with the program **buildable and runnable**, and (M1+) **host tests green**.

- **M0 — Scaffold + read-only HUD.** Crate from the `gogogo-rs` skeleton; `Source` + telemetry parse;
  TUI shows live state (alt, velocities, mass, attitude) for the active vessel. No control writes.
  *Exit:* runs in-guest, mirrors `/sim` numbers.
- **M1 — Frames & quaternion core (pure).** `frames.rs` + `ksa_quat.rs` with the §11.1 unit tests green
  on the host. *Exit:* the frame correctness checklist (§3.7) is all green in tests.
- **M2 — G-FOLD solve (offline parity).** `vehicle.rs` + `gfold.rs` building the SOCP for Clarabel;
  golden-section `tf`; **parity vs the Python repo** (§11.2). No flying yet. *Exit:* matches reference
  trajectories within tolerance.
- **M3 — G-FOLD closed-loop MPC (flyable).** Wire first-node apply → `attitude_target`+`throttle`+
  `ignite`; ignition timing; ABORT. Add the **G-limit** constraint + live re-solve. *Exit:* lands a
  vehicle in-game (coarse terminal), G-limit re-plans live.
- **M4 — UPFG port (offline parity).** `upfg.rs` + CSE; **parity vs PEGAS-MATLAB** ascent case (§11.2);
  descent target builder. *Exit:* UPFG matches the reference; descent case sane.
- **M5 — Hybrid + terminal handoff.** G-FOLD braking → UPFG terminal; G-limit throttle law; precise
  touchdown. *Exit:* touchdown error & speed within targets; peak-g ≤ G-limit.
- **M6 — Polish & robustness.** Trajectory canvas + gauges; infeasible/abort/warp/stale UX;
  non-dimensionalization; optional rotating-frame terms (§5.7); profiles/presets; README + validation
  record. *Exit:* the §11.3 in-game pass documented across a vacuum and an atmospheric body.
- **M7 (stretch) — Atmosphere & entry.** Drag term from `environment/density` + a `Cd·A` estimate in the
  planner; optional aero-entry hooks. *Exit:* accurate landings in non-trivial atmosphere.

---

## 13. Risks & open questions

- **Real-time SOCP in-guest (musl).** Mitigate: non-dimensionalize, modest N, warm-start, ~1 Hz re-solve
  (UPFG carries the fast loop), large worker stack, `max_iter` → ABORT not hang. *Open:* measured
  in-guest solve time at N=30 — to be profiled in M2/M3.
- **Coriolis/curvature error on fast-rotating bodies.** Mitigate: UPFG terminal is exact; add the linear
  rotating terms (§5.7); re-solve rejects residual. *Open:* the per-body error budget / handoff altitude.
- **Atmospheric drag** is unmodeled in M0–M6 (disturbance only). *Open:* whether the home body's
  atmosphere is significant at landing speeds → M7. Pull `dynamic_pressure`/`density` to quantify.
- **Attitude latency & slew.** `attitude_target` is solver-phase and the FC slews finitely; the loop
  must tolerate ~1-step lag (it does — closed-loop). *Open:* tune handoff/gains so the FC keeps up during
  fast terminal pitch-overs; consider feeding a non-zero `RatesCci` if lag bites (currently 0).
- **Time-warp & pause.** Guidance must idle during warp/pause (`time/sim_dt==0`, `warp>1`) and pace via
  sim time. *Open:* desired behavior if the pilot warps mid-burn (recommend: auto-ABORT to manual).
- **Infeasibility / too-late-to-land.** Define the policy: raise G-limit suggestion, divert to nearest
  reachable point (P3), or controlled crash-minimization. *Open:* product decision.
- **Staging during descent.** `F_max`/`α`/`min_throttle` change on stage events; `vehicle.rs` must rebuild
  and G-FOLD re-solve. *Open:* multi-stage landers (probably rare; single-stage first).
- **Roll DOF.** Free for a lander; the position-vector roll reference keeps it continuous. *Open:* if a
  vehicle needs a specific roll (legs/sensors), expose a roll input.

---

## 14. References (citations behind every load-bearing claim)

**UPFG / PEGAS** (`thirdparty/PEGAS`, `thirdparty/PEGAS-MATLAB`):
- Canonical algorithm: `PEGAS-MATLAB/MATLAB/unifiedPoweredFlightGuidance.m` (Blocks 0–8); thrust
  integrals `:141-185`; steering `iF` `:187-208`; terminal/`vd` `:226-235`; pitch/yaw `:210-216`.
- Game integration (kOS): `PEGAS/kOS/pegas_upfg.ks`; frame swap `pegas_util.ks:30-34` (kOS-only, **do not
  port**); state ingest `:356-365`; steering egress `:996`; **G-limit virtual stage** `:780-786`,`:596-624`;
  **live throttle = m·gLim·g₀/F** `:1008-1049` (`:1040`); convergence `:863-1005`,`pegas_settings.ks:24,27`.
- CSE: `PEGAS-MATLAB/MATLAB/CSEroutine.m`. Frames/units prose: `docs/upfg.md` (`:48-50` inertial frame,
  `:86` deorbit, `:140` negative-normal), `docs/simulation.md:150-247`. Constants `initSimulation.m:6-10`.

**G-FOLD** (`thirdparty/G-FOLD`):
- SOCP model: `src/lcvx_optimizer.py` — variables `:20-24`; constraints `:26-56` (slack `:47`,
  glide-slope `:44`, pointing `:48`, mass `:46`, Taylor thrust bounds `:50-56`, terminal `:30-39`);
  objectives `:60-67`; solvers ECOS/SCS `:63,:69`; returns `:71-76`.
- Driver: `src/gfold_util.py` — two-stage P3→P4 `:90-116`; golden-section tf `:28-51`; derived params &
  `z0` reference `:12-21,:72-78`; **axis 0 = up**, constant `g` `:19,:25,:81`. Params
  `vessel_parameters_{earth,mars}.json` (note `G_max` is **declared but unused** — we add it, §5.3).

**KSA ground truth** (`thirdparty/ksa/`):
- State (CCI): `KSA/Vehicle.cs:1696` (pos), `:1644` (vel), `:1911-1920` (surface vel = `v−ω×r`),
  `:1922-1952` (baro/radar alt), `:459-465` (mass/accel), `:1989` (`GetBody2Cci`).
- Spin axis / rotation: `KSA/IParentBody.cs:76-80`, `KSA/Celestial.cs:402-407,:167-170`; lat/lon
  `Celestial.cs:515-523,:553-591`; gravity `KSA/BubbleOrigin.cs:146-167`, `IParentBody.cs:15`.
- Local frames: `KSA/VehicleReferenceFrameEx.cs:81-99` (ENU), `:495-526` (CCI↔CCF states).
- **Attitude control:** `KSA/FlightComputer.cs:966-1015` (track-error on `UnitX` `:972`, roll on `UnitZ`
  `:1000`); **`KSA/BurnTarget.cs:60-68` (`ComputeBurnBody2Cci` — the quaternion recipe to port)**;
  `KSA/AttitudeTarget.cs`; quaternion math to port: `Brutal.Numerics/doubleQuat.cs`
  (`CreateFromRotationMatrix`,`Concatenate`,`Inverse`), `Brutal.Numerics/double3.cs` (`Transform`).
- Solver snapshot/restore (write-phase reason): `KSA/Vehicle.cs:1606-1618`, `KSA/VehicleUpdateData.cs:69`.

**gatOS `/sim`** (this repo):
- Tree & formats: `gatOS.SimFs/SimFsTree.cs`, `Formats.cs` (telemetry doc `:155-227`); snapshot model
  `gatOS.SimFs/Snapshots/SimSnapshot.cs`.
- Readers (source + frame of each datum): `gatOS.GameMod/Game/Ksa/Readers/{VesselReader,BodyReader}.cs`.
- **Actuators (write semantics):** `gatOS.GameMod/Game/Ksa/Actuators/FlightComputerActuator.cs`
  (`attitude_target` → `Target2Cci`; `burn` is impulsive — not for descent), `ThrottleActuator.cs`,
  `EngineActuator.cs`; phase routing `gatOS.SimFs/Commands/SimCommand.cs` (`SolverActions`).
- Mount: `guest/rootfs-overlay/sbin/sim-mount`. Matrix: `docs/KSA_INTEGRATION_MATRIX.md`. Pipeline:
  `docs/ARCHITECTURE.md`.

**Rust template & ecosystem:** `examples/gogogo-rs/` (worker+mpsc+`Source`, `src/{main,source}.rs`,
`Cargo.toml`), `examples/simfs-dashboard/`, `examples/dashboard-rs/`. Crates: `clarabel` 0.11
(Apache-2.0, native-Rust SOCP+exp-cone — default features only for musl), `ratatui` 0.29 (MIT),
`nalgebra` 0.34 (f64 vectors/quaternions), `ureq` 2 (no-TLS). Guest: Alpine musl
(`guest/build-image.sh`), `apk add cargo rust`.
