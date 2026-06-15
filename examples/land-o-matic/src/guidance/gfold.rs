//! G-FOLD powered-descent guidance: the fuel-optimal convex trajectory solver (`LANDING_PROGRAM_PLAN.md`
//! §5). A faithful port of `thirdparty/G-FOLD` (Blackmore/Açıkmeşe lossless convexification), re-axised
//! to an **ENU** target-centred frame (up = +z; gravity = (0,0,−g)), with two deliberate changes from
//! the reference (plan §5.3): the **G-limit** acceleration cap is enforced (the reference declares
//! `G_max` but never uses it), and the problem is assembled as a single SOCP for the native-Rust
//! [`clarabel`] conic solver — the lossless-convexification thrust **lower** bound becomes a rotated
//! second-order cone, so no exponential cone is needed.
//!
//! Frame & units: target at the origin, ENU axes, SI throughout. The state is `[r(3), v(3)]`; the
//! control is `u = T/m` (thrust acceleration); `z = ln m`; `σ ≥ ‖u‖` is the lossless slack.

use clarabel::algebra::CscMatrix;
use clarabel::solver::*;

use super::types::Vec3;
use super::vehicle::{VehicleModel, G0};

/// The powered-descent problem in the ENU target-centred frame.
#[derive(Debug, Clone)]
pub struct Problem {
    pub vehicle: VehicleModel,
    /// Initial position relative to the target (ENU, m).
    pub r0: Vec3,
    /// Initial surface-relative velocity (ENU, m/s).
    pub v0: Vec3,
    /// Gravity magnitude g (> 0); the gravity vector is `(0, 0, −g)`.
    pub gravity: f64,
    /// Body spin vector ω expressed in the **ENU (G) frame**, rad/s. When non-zero the trapezoidal
    /// dynamics gain the exact (linear, convex-preserving) Coriolis `−2ω×v` and centrifugal
    /// `−ω×(ω×r)` terms (plan §5.7); `Vec3::zeros()` recovers the flat-planet dynamics (the MVP — the
    /// closed-loop re-solve absorbs the omitted rotation for slow bodies).
    pub omega_g: Vec3,
    /// Max thrust acceleration as a multiple of g₀ (the pilot's lever): `‖u‖ ≤ g_limit·g₀`.
    pub g_limit: f64,
    /// Glide-slope: `cot(γ_gs)` — the vehicle stays within `‖horiz‖ ≤ cot(γ_gs)·height` of the pad.
    pub glide_slope_cot: f64,
    /// Thrust-pointing: `cos(θ_pt)` — keeps thrust within θ_pt of vertical (`u_z ≥ cos(θ_pt)·σ`).
    pub pointing_cos: f64,
    /// Speed cap, m/s.
    pub v_max: f64,
    /// Number of discretization nodes (≥ 4).
    pub n: usize,
    /// Force the first thrust vector vertical (the reference's modeling choice; off for MPC — plan §5.3).
    pub lock_initial_thrust_up: bool,
}

/// What to optimize.
#[derive(Debug, Clone, Copy)]
pub enum Objective {
    /// Problem 3: minimize ‖r(t_f) − target‖ with the vehicle on the ground — find the closest
    /// reachable aim point.
    MinLandingError { target: Vec3 },
    /// Problem 4: minimize fuel (maximize final mass) with r(t_f) pinned to `target`.
    MinFuel { target: Vec3 },
}

/// One discretization node of a solved trajectory.
#[derive(Debug, Clone, Copy)]
pub struct Node {
    /// Position relative to the target (ENU, m).
    pub r: Vec3,
    /// Velocity (ENU, m/s).
    pub v: Vec3,
    /// Mass, kg.
    pub mass: f64,
    /// Commanded thrust acceleration `u = T/m` (ENU, m/s²).
    pub thrust_accel: Vec3,
    /// Slack σ ≈ ‖u‖ (m/s²).
    pub sigma: f64,
}

impl Node {
    /// Commanded thrust force, N (`‖u‖·m`).
    pub fn thrust(&self) -> f64 {
        self.thrust_accel.norm() * self.mass
    }
}

/// A solved trajectory.
#[derive(Debug, Clone)]
pub struct Trajectory {
    pub tf: f64,
    pub dt: f64,
    pub nodes: Vec<Node>,
}

impl Trajectory {
    pub fn touchdown(&self) -> &Node {
        self.nodes.last().expect("non-empty trajectory")
    }
    /// Final (touchdown) mass, kg.
    pub fn final_mass(&self) -> f64 {
        self.touchdown().mass
    }
}

// ---- conic problem assembly ---------------------------------------------------------------------

/// A sparse linear form `Σ coeff·x[col]`.
type LinExpr = Vec<(usize, f64)>;
/// An affine row `(lin, const)` = `lin·x + const`.
type Row = (LinExpr, f64);
/// A second-order cone block: row 0 is the cone bound, rows 1.. the vector it bounds.
type SocBlock = Vec<Row>;

/// Accumulates rows for Clarabel's `A x + s = b, s ∈ K` form, keeping cone blocks contiguous: all
/// equalities (ZeroCone), then all inequalities (NonnegativeCone), then each second-order cone.
struct ConicBuilder {
    nvars: usize,
    eq: Vec<Row>,
    le: Vec<Row>,
    soc: Vec<SocBlock>,
}

impl ConicBuilder {
    fn new(nvars: usize) -> Self {
        Self {
            nvars,
            eq: Vec::new(),
            le: Vec::new(),
            soc: Vec::new(),
        }
    }

    /// `terms · x = rhs`.
    fn eq(&mut self, terms: LinExpr, rhs: f64) {
        self.eq.push((terms, rhs));
    }

    /// `terms · x ≤ rhs`.
    fn le(&mut self, terms: LinExpr, rhs: f64) {
        self.le.push((terms, rhs));
    }

    /// A second-order cone block: `rows[0] ≥ ‖rows[1..]‖`, where each row is an affine expression
    /// `s_i = terms · x + const` (cone slack `s = b − A x`, so the row contributes `A = −terms, b = const`).
    fn soc(&mut self, rows: SocBlock) {
        self.soc.push(rows);
    }

    fn build(self) -> (CscMatrix<f64>, Vec<f64>, Vec<SupportedConeT<f64>>) {
        let mut ai = Vec::new();
        let mut aj = Vec::new();
        let mut av = Vec::new();
        let mut b = Vec::new();
        let mut cones = Vec::new();
        let mut row = 0usize;

        // Equalities (ZeroCone): A x = b.
        for (terms, rhs) in &self.eq {
            for &(c, v) in terms {
                ai.push(row);
                aj.push(c);
                av.push(v);
            }
            b.push(*rhs);
            row += 1;
        }
        if !self.eq.is_empty() {
            cones.push(SupportedConeT::ZeroConeT(self.eq.len()));
        }

        // Inequalities (NonnegativeCone): A x + s = b, s ≥ 0  ⇔  A x ≤ b.
        for (terms, rhs) in &self.le {
            for &(c, v) in terms {
                ai.push(row);
                aj.push(c);
                av.push(v);
            }
            b.push(*rhs);
            row += 1;
        }
        if !self.le.is_empty() {
            cones.push(SupportedConeT::NonnegativeConeT(self.le.len()));
        }

        // Second-order cones: s = b − A x with s₀ ≥ ‖s₁..‖.
        for block in &self.soc {
            for (terms, konst) in block {
                for &(c, v) in terms {
                    ai.push(row);
                    aj.push(c);
                    av.push(-v); // A = −terms
                }
                b.push(*konst);
                row += 1;
            }
            cones.push(SupportedConeT::SecondOrderConeT(block.len()));
        }

        let a = CscMatrix::new_from_triplets(row, self.nvars, ai, aj, av);
        (a, b, cones)
    }
}

// ---- the solver ---------------------------------------------------------------------------------

const PER_NODE: usize = 11; // r(3) v(3) z(1) u(3) sigma(1)

/// Clarabel settings: quiet, with an iteration cap (the cap is treated as "infeasible" by the caller,
/// never a hang).
#[allow(clippy::field_reassign_with_default)]
fn solver_settings() -> DefaultSettings<f64> {
    let mut s = DefaultSettings::default();
    s.verbose = false;
    s.max_iter = 200;
    s
}

/// Solve the SOCP for a **fixed** time-of-flight `tf`. Returns `None` if infeasible / not solved, or if
/// the reference mass trajectory would go non-physical at this `tf` (which the time search treats as
/// infeasible).
pub fn solve_fixed_tf(prob: &Problem, obj: &Objective, tf: f64) -> Option<Trajectory> {
    let n = prob.n;
    if n < 4 || tf <= 0.0 {
        return None;
    }
    let dt = tf / n as f64;
    let veh = &prob.vehicle;
    let g = prob.gravity;

    // Non-dimensionalization (plan §5.4): scale length by L = ‖r0‖, time by √(L/g), acceleration by g.
    // Positions, velocities, and thrust accelerations become O(1) so the conic solver is well
    // conditioned (raw SI mixes ~2000 m with ~20 m/s² and a ~1e-4 mass-flow coefficient and makes
    // Clarabel report false infeasibility / numerical errors). Mass stays a log, shifted by ln(m_wet).
    let ls = prob.r0.norm().max(10.0); // length scale
    let ts = (ls / g).sqrt(); // time scale (gravity → 1)
    let vs = ls / ts; // velocity scale (= √(L·g))
    let dt_nd = dt / ts;
    let mwlog = veh.m_wet_log();
    let r0n = prob.r0 / ls;
    let v0n = prob.v0 / vs;
    let mass_coef = veh.alpha() * g * dt; // Δz = −(mass_coef/2)(σ'_n + σ'_{n+1}), σ' the g-scaled slack
    let glimit_cap = prob.g_limit * G0 / g; // σ' ≤ g_limit·g₀/g
    let vmax_nd = prob.v_max / vs;
    let scale_target = |t: Vec3| t / ls;

    let is_p3 = matches!(obj, Objective::MinLandingError { .. });
    let terr = PER_NODE * n; // epigraph variable (P3 only)
    let nvars = PER_NODE * n + if is_p3 { 1 } else { 0 };

    // Index helpers into the flat variable vector.
    let ri = |i: usize| PER_NODE * i; // r[i] = ri..ri+3
    let vi = |i: usize| PER_NODE * i + 3;
    let zi = |i: usize| PER_NODE * i + 6;
    let ui = |i: usize| PER_NODE * i + 7;
    let si = |i: usize| PER_NODE * i + 10;

    // Reference (max-thrust) mass trajectory and Taylor coefficients (plan §5.4), in g-scaled slack
    // units; the log-mass anchor is shifted by ln(m_wet) so z is O(0.1) about 0. Δz = z − z0 is scale-free.
    let mut z0s = vec![0.0; n]; // shifted reference log-mass
    let mut mu1 = vec![0.0; n]; // ρ1/(z0_term·g)
    let mut mu2 = vec![0.0; n]; // ρ2/(z0_term·g)
    for (k, ((zl, m1), m2)) in z0s.iter_mut().zip(mu1.iter_mut()).zip(mu2.iter_mut()).enumerate() {
        let t = k as f64 * dt;
        let z0_term = veh.m_wet() - veh.alpha() * veh.rho2() * t;
        if z0_term <= 1.0 {
            return None; // non-physical linearization anchor at this tf → treat as infeasible
        }
        *zl = z0_term.ln() - mwlog;
        *m1 = veh.rho1() / z0_term / g;
        *m2 = veh.rho2() / z0_term / g;
    }

    let mut bld = ConicBuilder::new(nvars);

    // --- boundary conditions (equalities), all in scaled units ---
    for k in 0..3 {
        bld.eq(vec![(ri(0) + k, 1.0)], r0n[k]); // initial position
        bld.eq(vec![(vi(0) + k, 1.0)], v0n[k]); // initial velocity
        bld.eq(vec![(vi(n - 1) + k, 1.0)], 0.0); // touchdown velocity zero
    }
    bld.eq(vec![(si(n - 1), 1.0)], 0.0); // engine off at touchdown
    bld.eq(vec![(zi(0), 1.0)], 0.0); // initial log-mass (shifted: ln m_wet − ln m_wet = 0)

    // Terminal thrust vertical (with σ_{N-1}=0 ⇒ u_{N-1}=0).
    bld.eq(vec![(ui(n - 1), 1.0)], 0.0);
    bld.eq(vec![(ui(n - 1) + 1, 1.0)], 0.0);
    bld.eq(vec![(ui(n - 1) + 2, 1.0), (si(n - 1), -1.0)], 0.0);

    if prob.lock_initial_thrust_up {
        bld.eq(vec![(ui(0), 1.0)], 0.0);
        bld.eq(vec![(ui(0) + 1, 1.0)], 0.0);
        bld.eq(vec![(ui(0) + 2, 1.0), (si(0), -1.0)], 0.0);
    }

    // Terminal position.
    match obj {
        Objective::MinLandingError { .. } => bld.eq(vec![(ri(n - 1) + 2, 1.0)], 0.0), // on the ground
        Objective::MinFuel { target } => {
            let t = scale_target(*target);
            for k in 0..3 {
                bld.eq(vec![(ri(n - 1) + k, 1.0)], t[k]);
            }
        }
    }

    // Rotating-frame coupling (plan §5.7): the scaled spin ω' = ω_G·ts, its skew [ω']× (Coriolis) and
    // [ω']×² (centrifugal). Both are linear in the decision variables, so they fold into the equality
    // rows with no new cones. Zero ω_G ⇒ zero matrices ⇒ the flat-planet dynamics below.
    let omega_nd = prob.omega_g * ts;
    let sk = skew(omega_nd);
    let sk2 = matmul3(&sk, &sk);

    // --- dynamics (equalities), trapezoidal collocation, all scaled (g' = 1) ---
    for nn in 0..n - 1 {
        // velocity: v'_{n+1} − v'_n − (dt_nd/2)(u'_n + u'_{n+1})
        //           + dt_nd·[ω']×(v'_n+v'_{n+1}) + (dt_nd/2)·[ω']×²(r'_n+r'_{n+1}) = dt_nd·g'
        for k in 0..3 {
            let rhs = if k == 2 { -dt_nd } else { 0.0 };
            let mut terms = vec![
                (vi(nn + 1) + k, 1.0),
                (vi(nn) + k, -1.0),
                (ui(nn) + k, -0.5 * dt_nd),
                (ui(nn + 1) + k, -0.5 * dt_nd),
            ];
            for j in 0..3 {
                let cor = dt_nd * sk[k][j]; // Coriolis (skew diagonal is 0 → no self-coupling)
                if cor != 0.0 {
                    terms.push((vi(nn) + j, cor));
                    terms.push((vi(nn + 1) + j, cor));
                }
                let cen = 0.5 * dt_nd * sk2[k][j]; // centrifugal
                if cen != 0.0 {
                    terms.push((ri(nn) + j, cen));
                    terms.push((ri(nn + 1) + j, cen));
                }
            }
            bld.eq(terms, rhs);
        }
        // position: r'_{n+1} − r'_n − (dt_nd/2)(v'_{n+1} + v'_n) = 0
        for k in 0..3 {
            bld.eq(
                vec![
                    (ri(nn + 1) + k, 1.0),
                    (ri(nn) + k, -1.0),
                    (vi(nn + 1) + k, -0.5 * dt_nd),
                    (vi(nn) + k, -0.5 * dt_nd),
                ],
                0.0,
            );
        }
        // mass: z_{n+1} − z_n + (mass_coef/2)(σ'_n + σ'_{n+1}) = 0
        bld.eq(
            vec![
                (zi(nn + 1), 1.0),
                (zi(nn), -1.0),
                (si(nn), 0.5 * mass_coef),
                (si(nn + 1), 0.5 * mass_coef),
            ],
            0.0,
        );
    }

    // --- per-node cones & linear bounds (scaled) ---
    for nn in 0..n {
        // G-limit (the pilot lever): σ'_n ≤ g_limit·g₀/g, every node.
        bld.le(vec![(si(nn), 1.0)], glimit_cap);
    }
    for nn in 0..n - 1 {
        // thrust pointing: u'_z ≥ cos(θ_pt)·σ'  ⇔  −u'_z + cos(θ_pt)·σ' ≤ 0
        bld.le(vec![(ui(nn) + 2, -1.0), (si(nn), prob.pointing_cos)], 0.0);

        // velocity cap: ‖v'_n‖ ≤ V_max/Vs
        bld.soc(vec![
            (vec![], vmax_nd),
            (vec![(vi(nn), 1.0)], 0.0),
            (vec![(vi(nn) + 1, 1.0)], 0.0),
            (vec![(vi(nn) + 2, 1.0)], 0.0),
        ]);

        // glide-slope: ‖(r_n − r_term)_horiz‖ ≤ cot(γ)·(r_n − r_term)_up
        let gc = prob.glide_slope_cot;
        bld.soc(vec![
            (vec![(ri(nn) + 2, gc), (ri(n - 1) + 2, -gc)], 0.0),
            (vec![(ri(nn), 1.0), (ri(n - 1), -1.0)], 0.0),
            (vec![(ri(nn) + 1, 1.0), (ri(n - 1) + 1, -1.0)], 0.0),
        ]);

        // thrust slack (lossless): ‖u_n‖ ≤ σ_n
        bld.soc(vec![
            (vec![(si(nn), 1.0)], 0.0),
            (vec![(ui(nn), 1.0)], 0.0),
            (vec![(ui(nn) + 1, 1.0)], 0.0),
            (vec![(ui(nn) + 2, 1.0)], 0.0),
        ]);
    }

    // thrust magnitude bounds (Taylor), nodes 0..N-2 (scaled). Unlike the reference we include node 0:
    // its anchor Δz=0 is well-defined, and excluding it lets the solver inject a nonphysical unbounded
    // first-node impulse (plan §5.3). Node N-1 stays out — the engine is off there (σ=0).
    for nn in 0..n - 1 {
        let (m1, m2, zl) = (mu1[nn], mu2[nn], z0s[nn]);
        // upper (linear): σ_n ≤ μ2(1 − (z_n − z0))  ⇔  σ_n + μ2·z_n ≤ μ2(1 + z0)
        bld.le(vec![(si(nn), 1.0), (zi(nn), m2)], m2 * (1.0 + zl));
        // lower (rotated SOC): σ_n ≥ μ1(1 − Δz + ½Δz²), Δz = z_n − z0.
        // Let a = σ_n + μ1·z_n − μ1(1 + z0). Then a ≥ ½μ1·Δz²  ⇔  ‖(√(2μ1)·Δz, a−1)‖ ≤ a+1.
        let k = (2.0 * m1).sqrt();
        let a_terms = vec![(si(nn), 1.0), (zi(nn), m1)];
        let a_const = -m1 * (1.0 + zl);
        bld.soc(vec![
            (a_terms.clone(), a_const + 1.0),                 // s0 = a + 1
            (vec![(zi(nn), k)], -k * zl),                     // s1 = √(2μ1)·(z_n − z0)
            (a_terms, a_const - 1.0),                         // s2 = a − 1
        ]);
    }

    // P3 epigraph: ‖r'_{N-1} − target'‖ ≤ terr
    if let Objective::MinLandingError { target } = obj {
        let t = scale_target(*target);
        bld.soc(vec![
            (vec![(terr, 1.0)], 0.0),
            (vec![(ri(n - 1), 1.0)], -t.x),
            (vec![(ri(n - 1) + 1, 1.0)], -t.y),
            (vec![(ri(n - 1) + 2, 1.0)], -t.z),
        ]);
    }

    // --- objective ---
    let mut q = vec![0.0; nvars];
    match obj {
        Objective::MinFuel { .. } => q[zi(n - 1)] = -1.0, // maximize z_{N-1} = ln m
        Objective::MinLandingError { .. } => q[terr] = 1.0,
    }

    let (a, b, cones) = bld.build();
    let p = CscMatrix::new(nvars, nvars, vec![0usize; nvars + 1], vec![], vec![]); // P = 0

    let mut solver = DefaultSolver::new(&p, &q, &a, &b, &cones, solver_settings()).ok()?;
    solver.solve();
    match solver.solution.status {
        SolverStatus::Solved | SolverStatus::AlmostSolved => {}
        _ => return None,
    }

    // --- unscale back to SI ---
    let x = &solver.solution.x;
    let nodes = (0..n)
        .map(|i| Node {
            r: Vec3::new(x[ri(i)], x[ri(i) + 1], x[ri(i) + 2]) * ls,
            v: Vec3::new(x[vi(i)], x[vi(i) + 1], x[vi(i) + 2]) * vs,
            mass: (x[zi(i)] + mwlog).exp(),
            thrust_accel: Vec3::new(x[ui(i)], x[ui(i) + 1], x[ui(i) + 2]) * g,
            sigma: x[si(i)] * g,
        })
        .collect();
    Some(Trajectory { tf, dt, nodes })
}

/// The cost a time-of-flight `tf` yields for the golden-section search: landing error (P3) or fuel
/// (P4, as −ln m_f). Infeasible `tf` → a large sentinel so the search avoids it.
fn cost(prob: &Problem, obj: &Objective, tf: f64) -> f64 {
    match solve_fixed_tf(prob, obj, tf) {
        Some(traj) => match obj {
            Objective::MinLandingError { target } => (traj.touchdown().r - *target).norm(),
            Objective::MinFuel { .. } => -traj.final_mass().ln(),
        },
        None => 1e10,
    }
}

/// Estimate the time-of-flight with the lowest cost. A coarse scan first **brackets the feasible
/// window** (the bounded-thrust feasible region can be a narrow band that a pure golden section misses
/// when most of the range is infeasible), then a golden-section refines around the best sample. Returns
/// `None` if no `tf` is feasible.
pub fn estimate_tof(prob: &Problem, obj: &Objective) -> Option<f64> {
    let veh = &prob.vehicle;
    // Upper bound: the smaller of fuel-burn time and the Taylor-anchor validity limit (z0_term > 0).
    let t_hi = (veh.m_fuel / (veh.alpha() * veh.rho1()))
        .min(veh.m_wet() / (veh.alpha() * veh.rho2()) * 0.97);
    // Lower bound: well under the time to null the speed at the G-limit, so the feasible band is inside.
    let t_lo = (prob.v0.norm() / (prob.g_limit * G0)).max(1.0) * 0.5;
    if t_hi <= t_lo {
        return None;
    }

    const SAMPLES: usize = 32;
    let step = (t_hi - t_lo) / SAMPLES as f64;
    let mut best: Option<(f64, f64)> = None; // (cost, tf)
    for i in 0..=SAMPLES {
        let tf = t_lo + step * i as f64;
        let c = cost(prob, obj, tf);
        if c < 1e9 && best.is_none_or(|(bc, _)| c < bc) {
            best = Some((c, tf));
        }
    }
    let (_, tf_best) = best?;

    // Golden-section refine within ±1 scan step of the best feasible sample.
    let (mut a, mut b) = ((tf_best - step).max(t_lo), (tf_best + step).min(t_hi));
    let gr = (5f64.sqrt() - 1.0) * 0.5;
    for _ in 0..40 {
        if b - a <= 0.2 {
            break;
        }
        let d = (b - a) * gr;
        let (t1, t2) = (a + d, b - d);
        let (c1, c2) = (cost(prob, obj, t1), cost(prob, obj, t2));
        if c1 > c2 {
            b = t1;
        } else {
            a = t2;
        }
    }
    Some((a + b) * 0.5)
}

/// The cross-product matrix `[w]×` such that `[w]×·a = w × a`.
fn skew(w: Vec3) -> [[f64; 3]; 3] {
    [
        [0.0, -w.z, w.y],
        [w.z, 0.0, -w.x],
        [-w.y, w.x, 0.0],
    ]
}

/// 3×3 matrix product.
fn matmul3(a: &[[f64; 3]; 3], b: &[[f64; 3]; 3]) -> [[f64; 3]; 3] {
    let mut m = [[0.0; 3]; 3];
    for (i, mi) in m.iter_mut().enumerate() {
        for (j, mij) in mi.iter_mut().enumerate() {
            for (k, aik) in a[i].iter().enumerate() {
                *mij += aik * b[k][j];
            }
        }
    }
    m
}

/// The full two-stage solve (plan §5.1): Problem 3 finds the closest reachable aim point, then Problem 4
/// minimizes fuel to it. Each stage searches its own time-of-flight.
pub fn solve(prob: &Problem, desired_target: Vec3) -> Option<Trajectory> {
    let p3 = Objective::MinLandingError {
        target: desired_target,
    };
    let tf3 = estimate_tof(prob, &p3)?;
    let aim = solve_fixed_tf(prob, &p3, tf3)?.touchdown().r;

    let p4 = Objective::MinFuel { target: aim };
    let tf4 = estimate_tof(prob, &p4)?;
    solve_fixed_tf(prob, &p4, tf4)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A generic high-TWR lander on an Earth-like body — comfortably feasible (max thrust ≈ 4 g at wet
    /// mass), so the G-limit can actually bind. The reference "earth" case is *physically* infeasible
    /// (its ~2 g max thrust can't arrest 250 m/s of descent within 2000 m), so it's a poor fixture; the
    /// reference's own demo is the gentle Mars case ([`mars_problem`]).
    fn lander_problem(g_limit: f64) -> Problem {
        Problem {
            vehicle: VehicleModel {
                m_dry: 1000.0,
                m_fuel: 600.0,
                isp: 300.0,
                thrust_max: 80000.0, // ~4 g at wet mass (full throttle), ~6.5 g near dry
                throttle_min: 0.1,
                throttle_max: 0.8,
            },
            r0: Vec3::new(300.0, 100.0, 1500.0), // 1500 m up, 300 m downrange
            v0: Vec3::new(20.0, -10.0, -60.0),   // descending 60 m/s
            gravity: 9.81,
            omega_g: Vec3::zeros(),
            g_limit,
            glide_slope_cot: 1.0 / 30f64.to_radians().tan(),
            pointing_cos: 45f64.to_radians().cos(),
            v_max: 200.0,
            n: 25,
            lock_initial_thrust_up: false,
        }
    }

    /// The reference "mars" vessel (`thirdparty/G-FOLD/vessel_parameters_mars.json`), re-axised to ENU
    /// (the reference stores `[up, downrange, cross]`; we use `[east, north, up]`). A gentle, feasible
    /// descent — the same case the reference repo demos.
    fn mars_problem(g_limit: f64) -> Problem {
        Problem {
            vehicle: VehicleModel {
                m_dry: 2000.0,
                m_fuel: 800.0,
                isp: 203.94,
                thrust_max: 24000.0,
                throttle_min: 0.2,
                throttle_max: 0.8,
            },
            // reference initial_state [up=1000, dr=360, cr=-560, v_up=-10, v_dr=-5, v_cr=-5]
            r0: Vec3::new(360.0, -560.0, 1000.0),
            v0: Vec3::new(-5.0, -5.0, -10.0),
            gravity: 3.71,
            omega_g: Vec3::zeros(),
            g_limit,
            glide_slope_cot: 1.0 / 30f64.to_radians().tan(),
            pointing_cos: 45f64.to_radians().cos(),
            v_max: 100.0,
            n: 25,
            lock_initial_thrust_up: false,
        }
    }

    /// Re-integrate the dynamics from the thrust profile and confirm the solver's state trajectory
    /// satisfies them — proves the discretization is encoded correctly.
    fn check_dynamics(prob: &Problem, traj: &Trajectory) {
        let dt = traj.dt;
        let g = Vec3::new(0.0, 0.0, -prob.gravity);
        for w in traj.nodes.windows(2) {
            let (a, b) = (&w[0], &w[1]);
            let v_pred = a.v + (dt * 0.5) * ((a.thrust_accel + g) + (b.thrust_accel + g));
            let r_pred = a.r + (dt * 0.5) * (b.v + a.v);
            assert!((v_pred - b.v).norm() < 1e-3, "velocity defect {}", (v_pred - b.v).norm());
            assert!((r_pred - b.r).norm() < 1e-2, "position defect {}", (r_pred - b.r).norm());
        }
    }

    #[test]
    fn two_stage_solve_lands_softly() {
        let prob = lander_problem(5.0); // 5 g cap: above what throttle_max allows, so it doesn't bind
        let traj = solve(&prob, Vec3::new(0.0, 0.0, 0.0)).expect("solved");
        let td = traj.touchdown();
        // Boundary conditions.
        assert!((traj.nodes[0].r - prob.r0).norm() < 1e-3);
        assert!(td.v.norm() < 1e-2, "touchdown speed {}", td.v.norm());
        assert!(td.r.z.abs() < 1.0, "touchdown altitude {}", td.r.z);
        // Physical mass: burned some fuel, but not more than exists.
        assert!(td.mass > prob.vehicle.m_dry, "mass {} <= dry", td.mass);
        assert!(td.mass < prob.vehicle.m_wet());
        check_dynamics(&prob, &traj);
    }

    #[test]
    fn constraints_are_respected() {
        let prob = lander_problem(5.0);
        let traj = solve(&prob, Vec3::new(0.0, 0.0, 0.0)).expect("solved");
        let cap = prob.g_limit * G0;
        for (i, node) in traj.nodes.iter().enumerate() {
            // G-limit / slack.
            assert!(node.sigma <= cap + 1e-6, "node {i} sigma {} > cap {cap}", node.sigma);
            // glide-slope: stay within the cone above the pad.
            let horiz = (node.r.x.powi(2) + node.r.y.powi(2)).sqrt();
            assert!(
                horiz <= prob.glide_slope_cot * node.r.z.max(0.0) + 1.0,
                "node {i} glide-slope violated: horiz {horiz} z {}",
                node.r.z
            );
            // speed cap.
            assert!(node.v.norm() <= prob.v_max + 1e-3);
        }
        // interior thrust within [rho1, rho2] (Taylor approx ⇒ small tolerance).
        let (r1, r2) = (prob.vehicle.rho1(), prob.vehicle.rho2());
        for node in &traj.nodes[1..traj.nodes.len() - 1] {
            let thrust = node.thrust();
            assert!(thrust >= r1 * 0.8 && thrust <= r2 * 1.05, "thrust {thrust} out of [{r1},{r2}]");
        }
    }

    #[test]
    fn tighter_g_limit_costs_more_fuel() {
        // A tighter deceleration cap forces a gentler, longer burn ⇒ more gravity loss ⇒ more fuel.
        // (Both within the single-shot-Taylor feasible band; the fuel-optimal here is ~4.3 g.)
        let target = Vec3::new(0.0, 0.0, 0.0);
        let loose = solve(&lander_problem(4.0), target).expect("loose solved");
        let tight = solve(&lander_problem(2.5), target).expect("tight solved");
        assert!(
            tight.final_mass() < loose.final_mass(),
            "tight final mass {} should be < loose {}",
            tight.final_mass(),
            loose.final_mass()
        );
    }

    #[test]
    fn mars_reference_scenario_lands() {
        // The gentle descent the reference repo demos — a feasibility/sanity check on real params.
        let traj = solve(&mars_problem(5.0), Vec3::new(0.0, 0.0, 0.0)).expect("mars solved");
        let td = traj.touchdown();
        assert!(td.v.norm() < 1e-2, "mars touchdown speed {}", td.v.norm());
        assert!(td.r.z.abs() < 1.0);
        assert!(td.mass > mars_problem(5.0).vehicle.m_dry);
    }

    /// The rotating-frame terms (§5.7) stay convex and feasible, still land at the target with zero
    /// velocity, and measurably change the trajectory versus the flat-planet dynamics. A fast spin
    /// exaggerates the effect so it's unambiguous.
    #[test]
    fn rotating_frame_terms_are_feasible_and_change_the_path() {
        let base = lander_problem(5.0);
        let flat = solve(&base, Vec3::zeros()).expect("flat solved");

        // A deliberately fast spin (well above any real body) so the Coriolis/centrifugal effect is large.
        let spinning = Problem {
            omega_g: Vec3::new(0.0, 0.02, 0.03),
            ..base
        };
        let rot = solve(&spinning, Vec3::zeros()).expect("rotating solved");

        // Still a valid landing: at the target with ~zero velocity.
        let td = rot.touchdown();
        assert!(td.v.norm() < 1e-2, "rotating touchdown speed {}", td.v.norm());
        assert!(td.r.norm() < 1.0, "rotating touchdown miss {}", td.r.norm());

        // The dynamics actually changed the trajectory (compare the mid-trajectory positions).
        let mid = flat.nodes.len() / 2;
        let drift = (flat.nodes[mid].r - rot.nodes[mid].r).norm();
        assert!(drift > 1.0, "rotating terms had no effect (drift {drift} m)");
    }

    #[test]
    fn estimate_tof_is_within_bounds() {
        let prob = lander_problem(5.0);
        let obj = Objective::MinFuel {
            target: Vec3::new(0.0, 0.0, 0.0),
        };
        let tf = estimate_tof(&prob, &obj).expect("a feasible tof");
        assert!(tf > 0.0 && tf < 200.0, "tof {tf}");
    }
}
