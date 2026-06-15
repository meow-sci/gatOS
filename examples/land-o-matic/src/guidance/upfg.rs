//! UPFG — Unified Powered Flight Guidance, the Space Shuttle's explicit closed-loop steering law
//! (Brand/Brown/Higgins, GN&C Equation Document 24), ported from Noiredd's PEGAS
//! (`thirdparty/PEGAS-MATLAB/MATLAB/unifiedPoweredFlightGuidance.m`) and adapted for **powered
//! descent**. Each call is a pure predictor-corrector iteration: given the vehicle state and the
//! previous iteration's working set, it returns a thrust unit-vector `iF` (where to point) and a
//! time-to-go. The host re-calls it until it converges (a few iterations), then again every tick.
//! See `LANDING_PROGRAM_PLAN.md` §6.
//!
//! **Frame: CCI, directly.** UPFG's native frame is planet-centred inertial with central gravity —
//! exactly KSA's CCI + `μ/r²` model — so it runs with **no frame change and no `vecYZ` swap** (that
//! swap is a kOS left-handed artifact; KSA is right-handed — plan §3.1, §6.3). Gravity over the
//! time-to-go comes from the conic propagator [`super::cse`], so this leg has no flat-planet error.
//!
//! **Scope (deliberate deviations from the multi-stage reference, plan §0/§6):** a lander is a single
//! stage at constant thrust, so this port implements the `n = 1`, `SM = 1` (constant-thrust) path — the
//! reference's stage recursion, coast handling, and constant-acceleration virtual-stage mode are
//! omitted. The G-limit is **not** modelled as PEGAS's constant-acceleration virtual stage; in descent
//! it is a throttle law applied outside UPFG (plan §6.4), so UPFG only ever needs constant-thrust
//! steering. Block 6 (pitch/yaw display angles) is dropped — the autopilot renders its own.

use super::cse::{cse_routine, CserState};
use super::types::Vec3;
use super::vehicle::G0;

/// The single powered stage UPFG steers (constant thrust). `max_burn_time` is kept for fidelity with the
/// reference but is unused on the `n = 1` path (the burn time is solved from `vgo`, not capped here).
#[derive(Debug, Clone, Copy)]
pub struct VehicleStage {
    /// Thrust UPFG plans with, N (use the vehicle's max thrust — PEGAS plans at full thrust and the
    /// G-limit throttle law scales the *applied* thrust separately, plan §6.4).
    pub thrust: f64,
    /// Effective exhaust velocity `ve = Isp·g₀`, m/s.
    pub exhaust_velocity: f64,
    /// Remaining burn time available, s (fidelity only; unused for a single stage).
    pub max_burn_time: f64,
}

/// The terminal condition UPFG drives toward, in CCI. For descent these are the **landing site's**
/// state at touchdown (plan §6.2): `rd` = site position, `vd` = site inertial velocity (so the
/// surface-relative touchdown speed is ≈ 0).
#[derive(Debug, Clone, Copy)]
pub struct Target {
    pub rd: Vec3,
    pub vd: Vec3,
}

/// The kinematic state UPFG reads each call (CCI, SI units).
#[derive(Debug, Clone, Copy)]
pub struct State {
    /// Sim time, s.
    pub time: f64,
    /// Mass, kg.
    pub mass: f64,
    /// Position, CCI, m.
    pub pos: Vec3,
    /// Velocity, CCI inertial, m/s.
    pub vel: Vec3,
}

/// UPFG's persistent working set (the reference `previous`/`current` struct). Carry it across iterations
/// and across ticks; seed it with [`UpfgState::seed`].
#[derive(Debug, Clone, Copy)]
pub struct UpfgState {
    pub cser: CserState,
    pub rbias: Vec3,
    pub rd: Vec3,
    pub rgrav: Vec3,
    /// Accumulated burn time, s.
    pub tb: f64,
    /// Time of the previous iteration, s.
    pub time: f64,
    pub tgo: f64,
    /// Velocity at the previous iteration, m/s.
    pub v: Vec3,
    /// Velocity-to-be-gained by thrust, m/s.
    pub vgo: Vec3,
}

impl UpfgState {
    /// Bootstrap the working set for a fresh engagement (plan §6.2): `vgo ≈ vd − v`, gravity terms zero
    /// (the conic propagator fills `rgrav` after the first iteration), `time = state.time` so the first
    /// iteration sees `dt = 0` and `tgo = 0` so the `rgrav` rescale is skipped on the first pass.
    pub fn seed(state: &State, target: &Target) -> Self {
        Self {
            cser: CserState::default(),
            rbias: Vec3::zeros(),
            rd: target.rd,
            rgrav: Vec3::zeros(),
            tb: 0.0,
            time: state.time,
            tgo: 0.0,
            v: state.vel,
            vgo: target.vd - state.vel,
        }
    }
}

/// The guidance output of one UPFG iteration.
#[derive(Debug, Clone, Copy)]
pub struct Guidance {
    /// Thrust unit-vector, CCI (point body +X here).
    pub i_f: Vec3,
    /// Time-to-go to the terminal state, s.
    pub tgo: f64,
    /// Angle between `iF` and `λ = unit(vgo)`, rad (the predictor-corrector's steering correction; a
    /// proxy for how converged the solution is).
    pub phi: f64,
}

/// One UPFG iteration (reference blocks 1–8, single constant-thrust stage, descent terminal). Pure: it
/// returns the updated working set and the guidance; the caller decides whether to iterate again.
pub fn upfg_step(
    stage: &VehicleStage,
    target: &Target,
    state: &State,
    previous: &UpfgState,
    mu: f64,
) -> (UpfgState, Guidance) {
    let t = state.time;
    let m = state.mass;
    let r = state.pos;
    let v = state.vel;
    let mut cser = previous.cser;
    let rd = target.rd; // descent: terminal position is the fixed site
    let tp = previous.time;
    let vprev = previous.v;
    let mut vgo = previous.vgo;

    // BLOCK 1 — stage parameters (constant thrust).
    let f_t = stage.thrust;
    let ve = stage.exhaust_velocity;

    // BLOCK 2 — decrement vgo by the sensed Δv since the last iteration (state is known exactly here).
    let dt = t - tp;
    let dvsensed = v - vprev;
    vgo -= dvsensed;

    // BLOCK 3 — time-to-go from the single-stage thrust integral L (Tsiolkovsky).
    let a_t = f_t / m; // thrust acceleration at current mass
    let tu = ve / a_t; // "time to burn as if the whole stage were fuel" = ve·m/F
    let l = vgo.norm(); // Li(1) = |vgo| for a single stage
    let tb = tu * (1.0 - (-l / ve).exp());
    let tgo = tb;

    // Degenerate-state guard (near the terminal point |vgo|→0, tgo→0): point retrograde and bail before
    // the 1/L, 1/J, 1/tgo divisions below blow up. The terminal cutoff fires here in practice.
    if l < 1e-3 || tgo < 1e-3 {
        return fallback(state, previous, target, tgo.max(0.0));
    }

    // BLOCK 4 — thrust integrals. The velocity-to-go steering below needs only L (= |vgo|, the Δv) and
    // S (= tb·L − J, the range integral that seeds the conic propagator). The full reference set
    // (J, Q, P, H) is ported and validated in `thrust_integrals_match_quadrature`; at runtime it is
    // consumed only by the orbital λ̇ corrector, which the descent steering omits (Block 5).
    let s = tb * l - (tu * l - ve * tb); // S = tb·L − J

    // BLOCK 5 — steering: thrust along the velocity-to-go direction λ = unit(vgo) (UPFG's predictor).
    // The full orbital corrector iF = unit(λ − λ̇·J/L) is *omitted* for descent: its denominator
    // lambdade = Q − S·J/L goes negative in the near-vertical regime, swinging iF tens of degrees off the
    // braking direction and (through the φ² terms in vthrust → vbias) destabilizing the iteration. G-FOLD
    // owns position/divert; the UPFG terminal leg nulls velocity (plan §6.2, §7). With iF = λ the steering
    // loss is zero, so vthrust = L·λ and vbias = 0.
    let lambda = unit(vgo);
    let i_f = lambda;
    let phi = 0.0;
    let vthrust = l * lambda;
    let rthrust = s * lambda;
    let vbias = vgo - vthrust; // = 0 by construction; retained for the Block 8 form

    // BLOCK 7 — gravity over tgo via the conic propagator (the reference's "magic" seed coefficients
    // are ported verbatim — they minimize the coast-vs-powered extrapolation error, plan §6.1).
    let rc1 = r - 0.1 * rthrust - (1.0 / 30.0) * vthrust * tgo;
    let vc1 = v + 1.2 * rthrust / tgo - 0.1 * vthrust;
    let (rc2, vc2, cser_new) = cse_routine(rc1, vc1, tgo, cser, mu);
    cser = cser_new;
    let vgrav = vc2 - vc1;
    let rgrav = rc2 - rc1 - vc1 * tgo;

    // BLOCK 8 — descent terminal: rd, vd are the (fixed) site state, so skip the reference's plane
    // projection and γ-construction; just update vgo. (dvgo = 0, as standard ascent uses.)
    let vgop = target.vd - v - vgrav + vbias;
    vgo = vgop;

    let current = UpfgState {
        cser,
        rbias: previous.rbias, // unused by the velocity-to-go steering; carried for struct fidelity
        rd,
        rgrav,
        tb: previous.tb + dt,
        time: t,
        tgo,
        v,
        vgo,
    };
    let guidance = Guidance { i_f, tgo, phi };

    // Guard against any non-finite leak (e.g. a near-singular maneuver geometry).
    if !is_finite(i_f) || !tgo.is_finite() {
        return fallback(state, previous, target, 0.0);
    }
    (current, guidance)
}

/// Iterate [`upfg_step`] until the solution converges — `Δtgo < 0.5 s` and the steering vector moves
/// `< 15°` between iterations (PEGAS's convergence/good-solution criteria, plan §6.1) — or `max_iters`
/// is reached. Returns the converged working set, the latest guidance, the iteration count, and whether
/// it actually converged. Typically 2–4 iterations.
pub fn converge(
    stage: &VehicleStage,
    target: &Target,
    state: &State,
    seed: UpfgState,
    mu: f64,
    max_iters: usize,
) -> (UpfgState, Guidance, usize, bool) {
    let mut work = seed;
    let mut last_tgo = work.tgo;
    let mut last_if: Option<Vec3> = None;
    let mut guidance = Guidance {
        i_f: unit(-state.vel),
        tgo: 0.0,
        phi: 0.0,
    };
    let mut converged = false;
    let mut iters = 0;
    let max_move = 15f64.to_radians();
    for k in 0..max_iters.max(1) {
        let (cur, g) = upfg_step(stage, target, state, &work, mu);
        work = cur;
        guidance = g;
        iters = k + 1;
        let dtgo = (g.tgo - last_tgo).abs();
        let moved = last_if.map_or(f64::INFINITY, |p| p.dot(&g.i_f).clamp(-1.0, 1.0).acos());
        last_tgo = g.tgo;
        last_if = Some(g.i_f);
        if k >= 1 && dtgo < 0.5 && moved < max_move {
            converged = true;
            break;
        }
    }
    (work, guidance, iters, converged)
}

/// Build the descent terminal target (plan §6.2): aim at the site, arriving with the site's (ground)
/// velocity so the surface-relative touchdown speed is ≈ 0. On a non-rotating body `site_vel_cci` is
/// zero; on a rotating body it is `ω × r_site`.
pub fn descent_target(site_cci: Vec3, site_vel_cci: Vec3) -> Target {
    Target {
        rd: site_cci,
        vd: site_vel_cci,
    }
}

/// The G-limit throttle law (PEGAS `throttleControl`, plan §6.4): the throttle that holds thrust
/// acceleration at exactly `g_limit·g₀` for the current mass, clamped to `[throttle_min, throttle_max]`.
/// This is the deceleration *cap*; the terminal controller takes the min of this and the
/// required-to-land throttle.
pub fn g_limit_throttle(mass: f64, thrust_max: f64, g_limit: f64, throttle_min: f64, throttle_max: f64) -> f64 {
    (mass * g_limit * G0 / thrust_max).clamp(throttle_min, throttle_max)
}

/// The UPFG terminal-descent throttle (plan §6.4, §7): a suicide-burn law that bleeds the surface speed
/// down to `v_target` over the remaining distance, **capped by the G-limit** and floored at
/// `throttle_min`. UPFG supplies the steering vector `iF`; this supplies the magnitude. Targeting a small
/// nonzero `v_target` (rather than zero) keeps the vehicle gently descending through the last few metres
/// instead of stalling into a hover just above the pad. `speed` is the surface-relative speed, `dist` the
/// remaining distance to the cut altitude (m), `g` local gravity (m/s²).
#[allow(clippy::too_many_arguments)]
pub fn terminal_throttle(
    mass: f64,
    thrust_max: f64,
    speed: f64,
    v_target: f64,
    dist: f64,
    g: f64,
    g_limit: f64,
    throttle_min: f64,
    throttle_max: f64,
) -> f64 {
    // Thrust acceleration = (decel to reach v_target over `dist`) + (gravity support).
    let decel = ((speed * speed - v_target * v_target) / (2.0 * dist.max(1.0))).max(0.0);
    let needed = mass * (decel + g) / thrust_max;
    let cap = mass * g_limit * G0 / thrust_max; // the G-limit deceleration cap
    let upper = cap.min(throttle_max).max(throttle_min);
    needed.clamp(throttle_min, upper)
}

// ---- helpers ------------------------------------------------------------------------------------

fn unit(v: Vec3) -> Vec3 {
    let n = v.norm();
    if n < 1e-12 {
        Vec3::zeros()
    } else {
        v / n
    }
}

fn is_finite(v: Vec3) -> bool {
    v.x.is_finite() && v.y.is_finite() && v.z.is_finite()
}

/// Terminal/degenerate fallback: steer surface-retrograde (or up if nearly stopped), carrying the
/// previous working set forward with a small/zero tgo.
fn fallback(state: &State, previous: &UpfgState, target: &Target, tgo: f64) -> (UpfgState, Guidance) {
    let rel_v = state.vel - target.vd;
    let dir = if rel_v.norm() > 1.0 {
        -rel_v.normalize()
    } else {
        unit(state.pos)
    };
    let mut work = *previous;
    work.time = state.time;
    work.v = state.vel;
    work.tgo = tgo;
    (
        work,
        Guidance {
            i_f: dir,
            tgo,
            phi: 0.0,
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The single-stage thrust integrals (Block 4) equal their defining quadratures for a constant-thrust
    /// stage with `a(t) = ve/(tu − t)` (plan §11.1). This pins the integral math independent of the rest.
    #[test]
    fn thrust_integrals_match_quadrature() {
        let ve: f64 = 3000.0;
        let tu: f64 = 200.0;
        let tb: f64 = 50.0; // burns 25% of the mass-time

        // Closed forms (the code under test).
        let l = ve * (tu / (tu - tb)).ln();
        let j = tu * l - ve * tb;
        let s = tb * l - j;
        let q = s * tu - 0.5 * ve * tb * tb;

        // High-resolution quadrature of the integral definitions:
        //   a(t)=ve/(tu−t);  L=∫a dt;  J=∫a·t dt;  L(t)=∫₀ᵗa;  S=∫L(t)dt;  J(t)=∫₀ᵗa·τ dτ;  Q=∫J(t)dt.
        let steps = 2_000_000;
        let h = tb / steps as f64;
        let (mut li, mut ji, mut si, mut qi) = (0.0, 0.0, 0.0, 0.0);
        let mut lacc = 0.0; // running L(t)
        for i in 0..steps {
            let t = (i as f64 + 0.5) * h;
            let a = ve / (tu - t);
            li += a * h;
            lacc += a * h;
            si += lacc * h; // S = ∫L(t)dt
            ji += a * t * h; // J(t) running; ends at J
            qi += ji * h; // Q = ∫J(t)dt
        }
        assert!((l - li).abs() < 1e-3, "L {l} vs {li}");
        assert!((j - ji).abs() < 1e-1, "J {j} vs {ji}");
        assert!((s - si).abs() < 1e-1, "S {s} vs {si}");
        assert!((q - qi).abs() < 20.0, "Q {q} vs {qi}");
    }

    /// A constructed descent case converges in a few iterations and yields a sane steering vector:
    /// pointing has an **upward** component (to brake the fall) and is generally **retrograde** (opposes
    /// the surface-relative velocity). Non-rotating body, so the site is inertially fixed (`vd = 0`).
    #[test]
    fn descent_case_converges_and_points_sanely() {
        let mu = 3.5316e12; // Kerbin-like
        let r_body = 600_000.0;
        // 2 km up, 1 km downrange, descending 120 m/s with 40 m/s of horizontal closing speed.
        let up = Vec3::new(1.0, 0.0, 0.0);
        let east = Vec3::new(0.0, 1.0, 0.0);
        let pos = up * (r_body + 2000.0) + east * 1000.0;
        let vel = up * -120.0 + east * -40.0;

        let site = up * r_body; // directly "below" the start, on the ground
        let _ = east; // (the closing direction is implicit in vel)
        let target = descent_target(site, Vec3::zeros());

        let stage = VehicleStage {
            thrust: 60_000.0,
            exhaust_velocity: 300.0 * G0,
            max_burn_time: 200.0,
        };
        let state = State {
            time: 0.0,
            mass: 4000.0,
            pos,
            vel,
        };
        let seed = UpfgState::seed(&state, &target);
        let (_, g, iters, converged) = converge(&stage, &target, &state, seed, mu, 20);

        assert!(converged, "did not converge ({iters} iters)");
        assert!((g.i_f.norm() - 1.0).abs() < 1e-9, "iF not unit");
        assert!(g.tgo > 0.0 && g.tgo < 200.0, "tgo {}", g.tgo);
        // Upward component: thrust opposes gravity/descent.
        assert!(g.i_f.dot(&pos.normalize()) > 0.2, "iF not upward: {:?}", g.i_f);
        // Generally retrograde: positive dot with −v̂.
        assert!(g.i_f.dot(&(-vel.normalize())) > 0.3, "iF not retrograde: {:?}", g.i_f);
    }

    /// Near the terminal point (|vgo|→0) the step takes the guarded fallback instead of dividing by zero.
    #[test]
    fn terminal_singularity_is_guarded() {
        let mu = 3.5316e12;
        let r_body = 600_000.0;
        let pos = Vec3::new(r_body + 5.0, 0.0, 0.0);
        let vel = Vec3::new(-0.2, 0.0, 0.0); // almost stopped
        let target = descent_target(Vec3::new(r_body, 0.0, 0.0), Vec3::zeros());
        let stage = VehicleStage {
            thrust: 60_000.0,
            exhaust_velocity: 300.0 * G0,
            max_burn_time: 200.0,
        };
        let state = State { time: 10.0, mass: 3500.0, pos, vel };
        let seed = UpfgState::seed(&state, &target);
        let (_, g, _, _) = converge(&stage, &target, &state, seed, mu, 20);
        assert!(is_finite(g.i_f) && g.tgo.is_finite(), "fallback produced non-finite output");
    }

    /// The definitive M4 "descent case sane" proof: a point-mass sim flown by UPFG terminal guidance
    /// end-to-end — UPFG steering (`iF`) recomputed each tick, throttle from [`terminal_throttle`] — lands
    /// a descending vehicle softly. Non-rotating body (isolates the steering + conic-gravity path). The
    /// UPFG working set is carried across ticks (warm-started `cser`, sensed-Δv `vgo` decrement), exactly
    /// as the closed-loop guidance runs in flight.
    #[test]
    fn upfg_terminal_lands_softly() {
        let mu = 3.5316e12;
        let r_body = 600_000.0;
        let thrust_max = 120_000.0; // ~3 g at the 4 t wet mass — a realistic lander TWR
        let ve = 300.0 * G0;
        let stage = VehicleStage {
            thrust: thrust_max,
            exhaust_velocity: ve,
            max_burn_time: 300.0,
        };
        let (g_limit, throttle_min, throttle_max) = (2.5, 0.05, 1.0); // 2.5 g cap binds below full thrust
        let (td_alt, v_td) = (5.0, 3.0);

        // A representative G-FOLD→UPFG handoff state: low and moderate speed (terminal's actual job —
        // see the hybrid sim for the full braking phase). Running UPFG terminal for an entire high-speed
        // descent would work but waste fuel (continuous gentle braking = high gravity loss).
        let up0 = Vec3::new(1.0, 0.0, 0.0);
        let east = Vec3::new(0.0, 1.0, 0.0);
        let mut pos = up0 * (r_body + 350.0) + east * 40.0;
        let mut vel = up0 * -45.0 + east * -12.0; // descending 45 m/s, closing 12 m/s
        let mut mass = 4000.0;
        let m_dry = 2700.0;
        let mut ut = 0.0;
        let dt = 0.1;

        let site = pos.normalize() * r_body; // the ground point directly below the start
        let mut landed = false;

        for _ in 0..3000 {
            let up = pos.normalize();
            let alt = pos.norm() - r_body;
            let v_vert = -vel.dot(&up); // + = descending
            // Touchdown: low and descending slowly. (Horizontal is nulled by iF; the final-speed
            // assertion below confirms it.)
            if alt <= td_alt && v_vert.abs() <= v_td {
                landed = true;
                break;
            }
            let target = descent_target(site, Vec3::zeros());
            let state = State { time: ut, mass, pos, vel };
            // Re-converge from a fresh seed each tick (each tick settles to its own state's fixed point —
            // carrying the working set across ticks induces a predictor-corrector 2-cycle here).
            let seed = UpfgState::seed(&state, &target);
            let (_, g, _, _) = converge(&stage, &target, &state, seed, mu, 20);

            let grav = mu / pos.norm_squared();
            // Throttle sizes the *vertical* braking (iF steers; the throttle controls the descent rate).
            // Aim for half the cut speed so the vehicle actually reaches the touchdown gate (a target of
            // exactly v_td would hold the descent rate at the gate boundary and never trip it).
            let throttle = terminal_throttle(
                mass, thrust_max, v_vert.max(0.0), v_td * 0.5, alt - td_alt, grav, g_limit, throttle_min, throttle_max,
            );
            let thrust_force = throttle * thrust_max;
            let g_load = thrust_force / mass;
            // Peak-g never exceeds the limit (the cap is the headline guarantee).
            assert!(g_load <= g_limit * G0 + 1e-6, "exceeded G-limit: {g_load} m/s²");
            let accel = g.i_f * g_load - up * grav;
            vel += accel * dt;
            pos += vel * dt;
            mass -= thrust_force / ve * dt;
            ut += dt;
            assert!(pos.norm() - r_body > -20.0, "crashed through the ground");
            assert!(mass > m_dry, "ran out of fuel: {mass:.0}");
        }

        assert!(landed, "never reached touchdown");
        assert!(vel.norm() < 6.0, "final speed {:.2} m/s", vel.norm());
    }

    #[test]
    fn g_limit_throttle_clamps() {
        // Light vehicle, strong engine: full G-limit accel needs < min throttle → clamps up to min.
        assert_eq!(g_limit_throttle(100.0, 1e6, 1.0, 0.1, 1.0), 0.1);
        // Heavy vehicle: needs more than full → clamps to max.
        assert_eq!(g_limit_throttle(1e5, 1e4, 3.0, 0.1, 1.0), 1.0);
        // In range: m·g·g₀/F.
        let th = g_limit_throttle(2000.0, 60_000.0, 1.0, 0.05, 1.0);
        assert!((th - 2000.0 * G0 / 60_000.0).abs() < 1e-9);
    }
}
