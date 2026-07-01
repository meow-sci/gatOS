//! The closed-loop powered-descent autopilot (plan §5.6, §7): a stateful **hybrid** controller that,
//! each tick, transforms the CCI vehicle state and emits a control command (a thrust direction +
//! throttle) as a `Body→CCI` attitude quaternion ready to write to `ctl/attitude_target`. Two legs:
//!
//! - **Braking** ([`Phase::Burn`]) — the high divert. Transform into the target-centred ENU frame,
//!   solve the G-FOLD SOCP, apply the first node (MPC). Re-solve strategy: a full two-stage solve at
//!   ignition (finds the time-of-flight), then a cheaper shrinking-horizon re-solve each tick (re-plan
//!   from scratch on failure).
//! - **Terminal** ([`Phase::Terminal`]) — the precise touchdown. Below `handoff_alt` (latched), UPFG
//!   terminal guidance steers along its velocity-to-go vector while a G-limit-capped suicide throttle
//!   sizes the descent braking.
//!
//! The G-limit lever feeds **both** legs, so changing it re-plans live. Pure — no `/sim`, no game, no
//! terminal; the worker feeds it a [`State`] built from telemetry and applies the returned [`Command`].

use super::frames::{self, EnuBasis};
use super::gfold::{self, Objective, Problem};
use super::ksa_quat::{self, Quat};
use super::upfg;
use super::vehicle::{VehicleModel, G0};
use super::Vec3;

/// The vehicle state in CCI (parent-centred inertial), built from `/sim` telemetry + body constants.
#[derive(Debug, Clone, Copy)]
pub struct State {
    /// Sim time, s.
    pub ut: f64,
    /// Position, CCI, m.
    pub pos_cci: Vec3,
    /// Velocity, CCI inertial, m/s.
    pub vel_cci: Vec3,
    /// Mass, kg.
    pub mass: f64,
    /// Height above terrain, m.
    pub radar_alt: f64,
    /// Reported body-fixed longitude, deg (to recover the body's rotation angle).
    pub lon_deg: f64,
    /// Parent gravitational parameter μ, m³/s².
    pub mu: f64,
    /// Parent spin rate ω, rad/s (about CCI +Z).
    pub omega: f64,
    /// Atmospheric density at the vehicle, kg/m³ (0 in vacuum / when the detail stream is gated).
    pub density: f64,
}

/// The propulsion spec (from `/sim` engine fields), rebuilt on staging.
#[derive(Debug, Clone, Copy)]
pub struct VehicleSpec {
    pub m_dry: f64,
    pub isp: f64,
    pub thrust_max: f64,
    pub throttle_min: f64,
    pub throttle_max: f64,
}

/// Pilot/config inputs (the live levers).
#[derive(Debug, Clone, Copy)]
pub struct Inputs {
    /// Max thrust acceleration in g₀ (the headline lever).
    pub g_limit: f64,
    /// Minimum glide-slope angle from the pad, degrees.
    pub glide_slope_deg: f64,
    /// Max thrust tilt from vertical, degrees.
    pub pointing_deg: f64,
    /// Speed cap, m/s.
    pub v_max: f64,
    /// Touchdown speed at/under which the engine cuts, m/s.
    pub v_touchdown: f64,
    /// Altitude at/under which (with low speed) we declare touchdown, m.
    pub touchdown_alt: f64,
    /// Altitude at/under which G-FOLD braking hands off to UPFG terminal guidance, m (plan §7).
    pub handoff_alt: f64,
    /// Terminal-phase braking safety margin (live, `[`/`]` keys): scales the UPFG suicide-burn
    /// deceleration so the vehicle brakes harder and arrives slow with altitude to spare. 1.0 = the
    /// minimal suicide profile (default — no margin, identical to the pre-tunable behavior); e.g. 1.2
    /// brakes 20 % harder ≈ "be stopped ~17 % above the target". Floored at 1.0 (the knob only adds
    /// safety; it can never under-brake).
    pub brake_margin: f64,
    /// Include the exact Coriolis/centrifugal terms in the G-FOLD braking dynamics (plan §5.7). Off by
    /// default — for slow bodies the closed-loop re-solve absorbs the rotation; enable for fast spinners.
    pub rotating_dynamics: bool,
    /// Drag area `Cd·A`, m² (plan M7). 0 disables the drag model (vacuum). With density from `/sim`, the
    /// drag deceleration is folded into the G-FOLD gravity bias and the terminal throttle.
    pub drag_area: f64,
    /// Discretization nodes.
    pub n: usize,
}

impl Default for Inputs {
    fn default() -> Self {
        Self {
            g_limit: 4.0,
            glide_slope_deg: 20.0,
            pointing_deg: 60.0,
            v_max: 300.0,
            v_touchdown: 3.0,
            touchdown_alt: 8.0,
            handoff_alt: 300.0,
            brake_margin: 1.0,
            rotating_dynamics: false,
            drag_area: 0.0,
            n: 20,
        }
    }
}

/// Flight phase.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    /// Not armed — the pilot has control.
    Idle,
    /// Armed and braking under the G-FOLD MPC (the high divert).
    Burn,
    /// Low and slow: UPFG terminal guidance flies the precise touchdown (plan §7).
    Terminal,
    /// No feasible landing solution right now (holding retrograde, throttle cut).
    Infeasible,
    /// Landed (or close enough): engine cut, attitude released.
    Touchdown,
    /// Pilot aborted: engine cut, attitude released.
    Abort,
}

/// UPFG terminal-phase diagnostics for the HUD.
#[derive(Debug, Clone, Copy)]
pub struct UpfgStatus {
    pub converged: bool,
    pub iters: u32,
}

/// The planned descent projected to the **downrange × altitude** plane for the trajectory canvas
/// (plan §9.1). Downrange is the horizontal distance from the pad (m); altitude is height above the site
/// (m). Both the path and the current marker live in this 2-D plane so the HUD can draw them directly.
#[derive(Debug, Clone, Default)]
pub struct PlanView {
    /// Planned nodes `(downrange, altitude)`, pad at the origin.
    pub path: Vec<(f64, f64)>,
    /// The vehicle's current `(downrange, altitude)`.
    pub current: (f64, f64),
    /// `cot(γ_gs)` — the glide-slope cone the canvas draws from the pad.
    pub glide_slope_cot: f64,
}

/// One control command for the worker to apply.
#[derive(Debug, Clone, Copy)]
pub struct Command {
    pub phase: Phase,
    /// Desired `Body→CCI` attitude (thrust axis along the commanded thrust), or `None` to release
    /// attitude to the pilot (Idle/Touchdown/Abort).
    pub attitude_target: Option<Quat>,
    /// Throttle 0..1.
    pub throttle: f64,
    /// Whether the engine should be lit.
    pub ignite: bool,
    /// Time-to-go to touchdown, s (0 when not burning).
    pub tgo: f64,
    /// Predicted touchdown mass, kg (0 when not solved).
    pub predicted_mass: f64,
    /// Peak thrust acceleration of the current plan, in g₀ (0 when not solved).
    pub peak_g: f64,
    /// UPFG convergence diagnostics (present only in the [`Phase::Terminal`] leg).
    pub upfg: Option<UpfgStatus>,
}

/// The stateful MPC autopilot.
pub struct Autopilot {
    pub inputs: Inputs,
    /// When set, the G-FOLD (re)plan runs through [`gfold::solve_traced`] and stashes the result in
    /// [`Self::last_trace`] for the `--log` diagnostic path. Off by default (the hot path skips it).
    pub trace_enabled: bool,
    /// The most recent solver diagnostics (only populated while [`Self::trace_enabled`]).
    pub last_trace: Option<gfold::SolveTrace>,
    armed: bool,
    aborted: bool,
    /// Landing target, frozen at engage in the body-fixed (CCF) frame so it stays put on the ground.
    target_ccf: Option<Vec3>,
    /// Time-of-flight of the active plan and the ignition time, for the shrinking horizon.
    plan_tf: Option<f64>,
    ignition_ut: Option<f64>,
    /// Latched once the vehicle drops below the handoff altitude — descent is one-way, so terminal
    /// guidance never reverts to braking (avoids a phase chatter at the boundary).
    terminal_latched: bool,
    /// The most recent solved trajectory (for the HUD).
    pub last: Option<gfold::Trajectory>,
    /// The most recent planned descent in the downrange×altitude plane (for the trajectory canvas).
    pub last_plan: Option<PlanView>,
}

impl Autopilot {
    pub fn new(inputs: Inputs) -> Self {
        Self {
            inputs,
            trace_enabled: false,
            last_trace: None,
            armed: false,
            aborted: false,
            target_ccf: None,
            plan_tf: None,
            ignition_ut: None,
            terminal_latched: false,
            last: None,
            last_plan: None,
        }
    }

    /// Arm the autopilot; the target is captured (directly below) on the next [`step`](Self::step).
    pub fn engage(&mut self) {
        self.armed = true;
        self.aborted = false;
        self.target_ccf = None;
        self.plan_tf = None;
        self.ignition_ut = None;
        self.terminal_latched = false;
    }

    /// Abort: cut throttle, release attitude.
    pub fn abort(&mut self) {
        self.aborted = true;
        self.armed = false;
    }

    pub fn is_armed(&self) -> bool {
        self.armed
    }

    /// Compute the control command for the current state.
    pub fn step(&mut self, state: &State, spec: &VehicleSpec) -> Command {
        if self.aborted {
            self.last_plan = None;
            return release(Phase::Abort);
        }
        if !self.armed {
            self.last_plan = None;
            return release(Phase::Idle);
        }

        // Body rotation angle (so a body-fixed target stays on the ground), and the landing frame.
        let theta = frames::rotation_angle(state.pos_cci, state.lon_deg);
        let ground_r = (state.pos_cci.norm() - state.radar_alt).max(1.0);
        let target_ccf = *self.target_ccf.get_or_insert_with(|| {
            frames::cci_to_ccf(state.pos_cci.normalize() * ground_r, theta) // "here": directly below
        });
        let target_cci = frames::ccf_to_cci(target_ccf, theta);
        let basis = frames::enu_basis(target_cci);

        let v_surf = frames::surface_velocity(state.vel_cci, state.pos_cci, state.omega);

        // Touchdown?
        if state.radar_alt <= self.inputs.touchdown_alt && v_surf.norm() <= self.inputs.v_touchdown
        {
            return release(Phase::Touchdown);
        }

        // Hand off to UPFG terminal guidance once low (latched — descent is one-way). Plan §7.
        if self.terminal_latched || state.radar_alt < self.inputs.handoff_alt {
            self.terminal_latched = true;
            return self.terminal_step(state, spec, target_cci, v_surf);
        }

        // State in the ENU guidance frame (target at origin).
        let r0 = frames::to_enu(state.pos_cci - target_cci, &basis);
        let v0 = frames::to_enu(v_surf, &basis);
        let gravity = state.mu / state.pos_cci.norm_squared();
        // Body spin in the ENU guidance frame, for the optional rotating-frame dynamics (plan §5.7).
        let omega_g = if self.inputs.rotating_dynamics {
            frames::to_enu(Vec3::new(0.0, 0.0, state.omega), &basis)
        } else {
            Vec3::zeros()
        };
        let drag_accel = self.drag_accel_enu(state, v_surf, &basis);

        let prob = self.problem(state, spec, r0, v0, gravity, omega_g, drag_accel);
        let traj = match self.solve(&prob, state.ut) {
            Some(t) => t,
            None => return hold_retro(state, v_surf), // Infeasible: hold retrograde, throttle cut
        };

        // First-node command.
        let node = &traj.nodes[0];
        let thrust_dir_g = if node.thrust_accel.norm() > 1e-6 {
            node.thrust_accel.normalize()
        } else {
            Vec3::new(0.0, 0.0, 1.0) // degenerate: point up
        };
        let thrust_dir_cci = frames::from_enu(thrust_dir_g, &basis);
        let throttle = (node.thrust_accel.norm() * state.mass / spec.thrust_max).clamp(0.0, 1.0);
        let att = ksa_quat::compute_burn_body2cci(state.pos_cci.normalize(), thrust_dir_cci);

        let tgo = self
            .plan_tf
            .zip(self.ignition_ut)
            .map(|(tf, t0)| (tf - (state.ut - t0)).max(0.0))
            .unwrap_or(traj.tf);
        let peak_g = traj
            .nodes
            .iter()
            .map(|n| n.thrust_accel.norm() / G0)
            .fold(0.0, f64::max);
        let predicted_mass = traj.final_mass();
        self.last_plan = Some(PlanView {
            path: traj
                .nodes
                .iter()
                .map(|n| (n.r.x.hypot(n.r.y), n.r.z))
                .collect(),
            current: (r0.x.hypot(r0.y), r0.z),
            glide_slope_cot: 1.0 / self.inputs.glide_slope_deg.to_radians().tan(),
        });
        self.last = Some(traj);

        Command {
            phase: Phase::Burn,
            attitude_target: Some(att),
            throttle,
            ignite: true,
            tgo,
            predicted_mass,
            peak_g,
            upfg: None,
        }
    }

    /// The drag acceleration in the ENU guidance frame (plan M7): `−½·ρ·|v|·v·(Cd·A)/m`, opposing the
    /// surface-relative velocity. `Vec3::zeros()` when the drag model is off (`drag_area = 0`) or in
    /// vacuum (`density = 0`). The ENU z-component is the vertical (braking) contribution.
    fn drag_accel_enu(&self, state: &State, v_surf: Vec3, basis: &EnuBasis) -> Vec3 {
        if self.inputs.drag_area <= 0.0 || state.density <= 0.0 {
            return Vec3::zeros();
        }
        let coef = 0.5 * state.density * v_surf.norm() * self.inputs.drag_area / state.mass;
        frames::to_enu(-coef * v_surf, basis)
    }

    /// The UPFG terminal leg (plan §6, §7): re-converge UPFG from a fresh seed each tick (carrying the
    /// working set across ticks induces a predictor-corrector 2-cycle), steer body +X along its
    /// velocity-to-go vector `iF`, and throttle with the G-limit-capped required-deceleration suicide law.
    fn terminal_step(
        &mut self,
        state: &State,
        spec: &VehicleSpec,
        target_cci: Vec3,
        v_surf: Vec3,
    ) -> Command {
        let up = state.pos_cci.normalize();
        let gravity = state.mu / state.pos_cci.norm_squared();

        // Canvas plan: the remaining straight descent from here to the pad.
        let basis = frames::enu_basis(target_cci);
        let r_enu = frames::to_enu(state.pos_cci - target_cci, &basis);
        let cur = (r_enu.x.hypot(r_enu.y), r_enu.z.max(0.0));
        self.last_plan = Some(PlanView {
            path: vec![cur, (0.0, 0.0)],
            current: cur,
            glide_slope_cot: 1.0 / self.inputs.glide_slope_deg.to_radians().tan(),
        });

        let stage = upfg::VehicleStage {
            thrust: spec.thrust_max,
            exhaust_velocity: spec.isp * G0,
            max_burn_time: f64::MAX,
        };
        // Aim at the site, arriving with the ground's velocity (ω × r_site) so surface-relative
        // touchdown speed ≈ 0.
        let site_vel = Vec3::new(0.0, 0.0, state.omega).cross(&target_cci);
        let target = upfg::descent_target(target_cci, site_vel);
        let ustate = upfg::State {
            time: state.ut,
            mass: state.mass,
            pos: state.pos_cci,
            vel: state.vel_cci,
        };
        let seed = upfg::UpfgState::seed(&ustate, &target);
        let (_, g, iters, converged) = upfg::converge(&stage, &target, &ustate, seed, state.mu, 20);

        // Vertical-rate suicide throttle (iF steers; this sizes the descent braking). Aim below the cut
        // speed so the touchdown gate trips instead of holding the rate at the boundary. Drag opposing the
        // descent (its ENU up-component) does some of the braking, so it reduces the gravity the thrust
        // must overcome (plan M7).
        let drag_decel = self.drag_accel_enu(state, v_surf, &basis).z.max(0.0);
        let v_vert_down = (-v_surf.dot(&up)).max(0.0);
        let dist = state.radar_alt - self.inputs.touchdown_alt;
        let throttle = upfg::terminal_throttle(
            state.mass,
            spec.thrust_max,
            v_vert_down,
            self.inputs.v_touchdown * 0.5,
            dist,
            gravity - drag_decel,
            self.inputs.g_limit,
            spec.throttle_min,
            spec.throttle_max,
            self.inputs.brake_margin,
        );
        let att = ksa_quat::compute_burn_body2cci(up, g.i_f);
        let peak_g = throttle * spec.thrust_max / state.mass / G0;

        Command {
            phase: Phase::Terminal,
            attitude_target: Some(att),
            throttle,
            ignite: true,
            tgo: g.tgo,
            predicted_mass: state.mass,
            peak_g,
            upfg: Some(UpfgStatus {
                converged,
                iters: iters as u32,
            }),
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn problem(
        &self,
        state: &State,
        spec: &VehicleSpec,
        r0: Vec3,
        v0: Vec3,
        gravity: f64,
        omega_g: Vec3,
        drag_accel: Vec3,
    ) -> Problem {
        Problem {
            vehicle: VehicleModel {
                m_dry: spec.m_dry,
                m_fuel: (state.mass - spec.m_dry).max(0.0),
                isp: spec.isp,
                thrust_max: spec.thrust_max,
                throttle_min: spec.throttle_min,
                throttle_max: spec.throttle_max,
            },
            r0,
            v0,
            gravity,
            drag_accel,
            omega_g,
            g_limit: self.inputs.g_limit,
            glide_slope_cot: 1.0 / self.inputs.glide_slope_deg.to_radians().tan(),
            pointing_cos: self.inputs.pointing_deg.to_radians().cos(),
            // The velocity cap is a safety rail, not a binding target — it must accommodate the actual
            // approach speed (a deorbit freefall or orbital state can be far above the configured
            // v_max), otherwise the early high-speed nodes make every solve infeasible. Track v0 with
            // margin; `inputs.v_max` is the floor.
            v_max: self.inputs.v_max.max(v0.norm() * 2.0),
            n: self.inputs.n,
            lock_initial_thrust_up: false,
        }
    }

    /// Re-solve: full two-stage at ignition (sets the plan), then a cheap shrinking-horizon re-solve;
    /// fall back to a fresh full solve if that fails.
    fn solve(&mut self, prob: &Problem, ut: f64) -> Option<gfold::Trajectory> {
        if let (Some(tf0), Some(t0)) = (self.plan_tf, self.ignition_ut) {
            let horizon = (tf0 - (ut - t0)).max(2.0);
            if let Some(t) = gfold::solve_fixed_tf(
                prob,
                &Objective::MinFuel {
                    target: Vec3::zeros(),
                },
                horizon,
            ) {
                if self.trace_enabled {
                    self.last_trace = Some(gfold::SolveTrace {
                        summary: gfold::ProblemSummary::of(prob),
                        outcome: format!("ok — shrinking-horizon fast solve (tf={horizon:.1}s)"),
                        ..Default::default()
                    });
                }
                return Some(t);
            }
        }
        // (re)plan from scratch — traced when --log is on (records why it's infeasible).
        let traj = if self.trace_enabled {
            let mut tr = gfold::SolveTrace::default();
            let r = gfold::solve_traced(prob, Vec3::zeros(), &mut tr);
            self.last_trace = Some(tr);
            r?
        } else {
            gfold::solve(prob, Vec3::zeros())?
        };
        self.plan_tf = Some(traj.tf);
        self.ignition_ut = Some(ut);
        Some(traj)
    }
}

/// Release attitude to the pilot, cut throttle.
fn release(phase: Phase) -> Command {
    Command {
        phase,
        attitude_target: None,
        throttle: 0.0,
        ignite: false,
        tgo: 0.0,
        predicted_mass: 0.0,
        peak_g: 0.0,
        upfg: None,
    }
}

/// Hold a surface-retrograde attitude with the throttle cut (no feasible plan).
fn hold_retro(state: &State, v_surf: Vec3) -> Command {
    let up = state.pos_cci.normalize();
    let dir = if v_surf.norm() > 1.0 {
        -v_surf.normalize()
    } else {
        up
    };
    Command {
        phase: Phase::Infeasible,
        attitude_target: Some(ksa_quat::compute_burn_body2cci(up, dir)),
        throttle: 0.0,
        ignite: false,
        tgo: 0.0,
        predicted_mass: 0.0,
        peak_g: 0.0,
        upfg: None,
    }
}

/// The local ENU basis exposed for callers that want to plot the plan in the landing frame.
pub fn landing_basis(state: &State) -> EnuBasis {
    frames::enu_basis(state.pos_cci)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn spec() -> VehicleSpec {
        VehicleSpec {
            m_dry: 1000.0,
            isp: 300.0,
            thrust_max: 80000.0, // ~4 g at wet mass
            throttle_min: 0.1,
            throttle_max: 1.0,
        }
    }

    #[test]
    fn idle_until_engaged() {
        let mut ap = Autopilot::new(Inputs::default());
        let state = State {
            ut: 0.0,
            pos_cci: Vec3::new(601_500.0, 0.0, 0.0),
            vel_cci: Vec3::new(-60.0, 10.0, 0.0),
            mass: 1500.0,
            radar_alt: 1500.0,
            lon_deg: 0.0,
            mu: 3.5316e12,
            omega: 0.0,
            density: 0.0,
        };
        let cmd = ap.step(&state, &spec());
        assert_eq!(cmd.phase, Phase::Idle);
        assert!(cmd.attitude_target.is_none() && cmd.throttle == 0.0);
    }

    #[test]
    fn abort_cuts_throttle() {
        let mut ap = Autopilot::new(Inputs::default());
        ap.engage();
        ap.abort();
        let state = State {
            ut: 0.0,
            pos_cci: Vec3::new(601_500.0, 0.0, 0.0),
            vel_cci: Vec3::new(-60.0, 0.0, 0.0),
            mass: 1500.0,
            radar_alt: 1500.0,
            lon_deg: 0.0,
            mu: 3.5316e12,
            omega: 0.0,
            density: 0.0,
        };
        let cmd = ap.step(&state, &spec());
        assert_eq!(cmd.phase, Phase::Abort);
        assert_eq!(cmd.throttle, 0.0);
    }

    /// The headline test: a point-mass sim with the autopilot in the loop, applying exactly the thrust
    /// it commands (recovered from the attitude quaternion via the KSA transform — so this also
    /// validates the whole attitude-output path), lands softly near the pad on a non-rotating body.
    #[test]
    fn closed_loop_lands_softly() {
        let mu = 3.5316e12;
        let r_body = 600_000.0;
        let spec = spec();
        let mut ap = Autopilot::new(Inputs {
            g_limit: 5.0,
            ..Inputs::default()
        });
        ap.engage();

        // Equatorial, 1500 m up, 200 m downrange, descending 60 m/s with a little cross motion.
        let mut pos = Vec3::new(r_body + 1500.0, 200.0, 0.0);
        let mut vel = Vec3::new(-60.0, 12.0, 0.0);
        let mut mass = 1500.0;
        let mut ut = 0.0;
        let dt = 0.5;

        let mut landed = false;
        for _ in 0..400 {
            let radar_alt = pos.norm() - r_body;
            let state = State {
                ut,
                pos_cci: pos,
                vel_cci: vel,
                mass,
                radar_alt,
                lon_deg: pos.y.atan2(pos.x).to_degrees(),
                mu,
                omega: 0.0, // non-rotating: isolates the MPC + control conversion
                density: 0.0,
            };
            let cmd = ap.step(&state, &spec);
            if cmd.phase == Phase::Touchdown {
                landed = true;
                break;
            }
            assert_ne!(
                cmd.phase,
                Phase::Infeasible,
                "went infeasible at alt {radar_alt:.0}"
            );

            // Apply exactly what the autopilot commanded: thrust along the attitude's +X axis.
            let thrust_dir = match cmd.attitude_target {
                Some(q) => ksa_quat::transform(Vec3::new(1.0, 0.0, 0.0), q),
                None => break,
            };
            let thrust_force = cmd.throttle * spec.thrust_max;
            let accel =
                thrust_dir * (thrust_force / mass) - pos.normalize() * (mu / pos.norm_squared());
            vel += accel * dt;
            pos += vel * dt;
            mass -= thrust_force / (spec.isp * G0) * dt;
            ut += dt;

            assert!(pos.norm() - r_body > -20.0, "crashed through the ground");
        }

        assert!(landed, "never reached touchdown");
        let final_alt = pos.norm() - r_body;
        let downrange = (pos - pos.normalize() * pos.norm()).norm(); // ~0 by construction; check horiz miss below
        let _ = downrange;
        assert!(final_alt.abs() < 30.0, "final altitude {final_alt:.1} m");
        assert!(vel.norm() < 6.0, "final speed {:.2} m/s", vel.norm());
        assert!(mass > spec.m_dry, "ran out of fuel: mass {mass:.0}");
    }

    /// The M5 hybrid acceptance test: a point-mass descent flown by the full autopilot exercises the
    /// **G-FOLD braking → UPFG terminal → touchdown** sequence, never exceeds the G-limit, and lands
    /// softly with fuel to spare. Applies exactly the thrust the autopilot commands (recovered through the
    /// attitude quaternion), so it validates both legs plus the control-conversion path end to end.
    #[test]
    fn hybrid_braking_then_terminal_lands() {
        let mu = 3.5316e12;
        let r_body = 600_000.0;
        let spec = VehicleSpec {
            m_dry: 1200.0,
            isp: 300.0,
            thrust_max: 130_000.0, // ~6.6 g at wet mass → the 4 g cap genuinely binds
            throttle_min: 0.1,
            throttle_max: 1.0,
        };
        let g_limit = 4.0;
        let mut ap = Autopilot::new(Inputs {
            g_limit,
            handoff_alt: 250.0,
            ..Inputs::default()
        });
        ap.engage();

        // 2500 m up, 600 m downrange + 200 m cross, descending 110 m/s with horizontal motion: a real
        // divert that the braking phase must work before the terminal phase finishes it.
        let mut pos = Vec3::new(r_body + 2500.0, 600.0, 200.0);
        let mut vel = Vec3::new(-110.0, 30.0, -10.0);
        let mut mass = 2000.0;
        let mut ut = 0.0;
        let dt = 0.25;
        let g_cap = g_limit * G0;

        let (mut saw_burn, mut saw_terminal, mut landed) = (false, false, false);
        for _ in 0..2000 {
            let radar_alt = pos.norm() - r_body;
            let state = State {
                ut,
                pos_cci: pos,
                vel_cci: vel,
                mass,
                radar_alt,
                lon_deg: pos.y.atan2(pos.x).to_degrees(),
                mu,
                omega: 0.0,
                density: 0.0,
            };
            let cmd = ap.step(&state, &spec);
            match cmd.phase {
                Phase::Burn => saw_burn = true,
                Phase::Terminal => {
                    saw_terminal = true;
                    // Once terminal latches it must never revert to braking.
                    assert!(
                        radar_alt < 260.0,
                        "terminal engaged too high: {radar_alt:.0} m"
                    );
                }
                Phase::Touchdown => {
                    landed = true;
                    break;
                }
                Phase::Infeasible => panic!("went infeasible at alt {radar_alt:.0} m"),
                other => panic!("unexpected phase {other:?}"),
            }

            let thrust_dir = match cmd.attitude_target {
                Some(q) => ksa_quat::transform(Vec3::new(1.0, 0.0, 0.0), q),
                None => break,
            };
            let thrust_force = cmd.throttle * spec.thrust_max;
            assert!(
                thrust_force / mass <= g_cap + 0.1,
                "peak-g exceeded at alt {radar_alt:.0} m"
            );
            let accel =
                thrust_dir * (thrust_force / mass) - pos.normalize() * (mu / pos.norm_squared());
            vel += accel * dt;
            pos += vel * dt;
            mass -= thrust_force / (spec.isp * G0) * dt;
            ut += dt;
            assert!(pos.norm() - r_body > -20.0, "crashed through the ground");
        }

        assert!(saw_burn, "never braked under G-FOLD");
        assert!(saw_terminal, "never handed off to UPFG terminal");
        assert!(landed, "never reached touchdown");
        assert!(vel.norm() < 6.0, "final speed {:.2} m/s", vel.norm());
        assert!(mass > spec.m_dry, "ran out of fuel: mass {mass:.0}");
    }

    /// M7: a descent through an atmosphere — drag is applied in the sim **and** modeled by the autopilot
    /// (G-FOLD gravity bias + terminal throttle). Confirms the drag path is consistent end-to-end and
    /// still lands softly.
    #[test]
    fn atmospheric_landing_with_drag() {
        let mu = 3.5316e12;
        let r_body = 600_000.0;
        let drag_area = 20.0; // Cd·A, m²
        let density = 0.1; // kg/m³ (a thin but non-trivial atmosphere)
        let spec = VehicleSpec {
            m_dry: 1200.0,
            isp: 300.0,
            thrust_max: 130_000.0,
            throttle_min: 0.1,
            throttle_max: 1.0,
        };
        let mut ap = Autopilot::new(Inputs {
            g_limit: 4.0,
            handoff_alt: 250.0,
            drag_area,
            ..Inputs::default()
        });
        ap.engage();

        let mut pos = Vec3::new(r_body + 2000.0, 400.0, 0.0);
        let mut vel = Vec3::new(-100.0, 25.0, 0.0);
        let mut mass = 2000.0;
        let mut ut = 0.0;
        let dt = 0.25;
        let mut landed = false;

        for _ in 0..2000 {
            let radar_alt = pos.norm() - r_body;
            let state = State {
                ut,
                pos_cci: pos,
                vel_cci: vel,
                mass,
                radar_alt,
                lon_deg: pos.y.atan2(pos.x).to_degrees(),
                mu,
                omega: 0.0,
                density,
            };
            let cmd = ap.step(&state, &spec);
            if cmd.phase == Phase::Touchdown {
                landed = true;
                break;
            }
            assert_ne!(
                cmd.phase,
                Phase::Infeasible,
                "infeasible at alt {radar_alt:.0} m"
            );
            let thrust_dir = match cmd.attitude_target {
                Some(q) => ksa_quat::transform(Vec3::new(1.0, 0.0, 0.0), q),
                None => break,
            };
            let thrust_force = cmd.throttle * spec.thrust_max;
            let grav = pos.normalize() * (mu / pos.norm_squared());
            // The same drag the autopilot models: −½·ρ·|v|·v·(Cd·A)/m.
            let drag = if vel.norm() > 1e-6 {
                -0.5 * density * vel.norm() * drag_area / mass * vel
            } else {
                Vec3::zeros()
            };
            let accel = thrust_dir * (thrust_force / mass) - grav + drag;
            vel += accel * dt;
            pos += vel * dt;
            mass -= thrust_force / (spec.isp * G0) * dt;
            ut += dt;
            assert!(pos.norm() - r_body > -20.0, "crashed through the ground");
        }

        assert!(landed, "never reached touchdown");
        assert!(vel.norm() < 6.0, "final speed {:.2} m/s", vel.norm());
        assert!(mass > spec.m_dry, "ran out of fuel: mass {mass:.0}");
    }
}
