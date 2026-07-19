//! `--simulate`: a self-contained physics backend implementing [`sim::Source`], so the whole
//! autopilot (plan → arm → paint → hop → done) runs end-to-end on the host with no game attached.
//! It integrates the same dynamics KSA applies to an off-rails vehicle — two-body gravity, thrust
//! along the (slew-rate-limited) attitude, propellant depletion — and honors the same `/sim` control
//! files the real mod serves. The headless integration test drives the entire flight against this
//! and asserts the painted trace actually lands on the letters.
//!
//! Deliberate omissions: no drag (the skywriting band is near-vacuum), no terrain (radar = baro),
//! instant warp changes, no solver-phase latency beyond the attitude slew itself.

use std::sync::{Arc, Mutex};
use std::time::Instant;

use crate::frames;
use crate::ksa_quat::{self, Quat};
use crate::sim::{CmdError, Source};
use crate::vec3::Vec3;

const DT: f64 = 0.02;

/// How the sim clock advances relative to the caller.
#[derive(Clone, Copy, Debug)]
pub enum SimClock {
    /// Game time = wall time × warp (the interactive `--simulate` TUI demo).
    Wall,
    /// A fixed game-time quantum × warp per telemetry read (headless tests: fast + deterministic).
    PerRead(f64),
}

pub struct SimWorld {
    state: Arc<Mutex<SimState>>,
}

struct SimState {
    clock: SimClock,
    last_wall: Instant,
    ut: f64,
    warp: f64,
    seq: u64,
    pos_cci: Vec3,
    vel_cci: Vec3,
    /// Current thrust-axis direction (body +X) in CCI; slews toward `target_dir`.
    thrust_dir: Vec3,
    target_dir: Vec3,
    slew_dps: f64,
    engine_on: bool,
    throttle: f64,
    mass_dry: f64,
    mass_prop: f64,
    vac_thrust: f64,
    isp: f64,
    min_throttle: f64,
    // Earth-ish body
    mu: f64,
    radius: f64,
    rot: f64,
    atmo_height: f64,
    atmo_scale_h: f64,
    atmo_rho0: f64,
    /// (pos_ccf, painting?) per substep — the "trail" for fidelity checks.
    trace: Vec<(Vec3, bool)>,
}

impl SimWorld {
    /// A vehicle hovering-ready ~30 km over the equator of an Earth-like body, co-rotating
    /// (zero surface velocity), engine off, nose up.
    pub fn new(clock: SimClock) -> SimWorld {
        let radius = 6_378_100.0;
        let alt = 30_000.0;
        let rot = 7.2921159e-5;
        let pos = Vec3::new(radius + alt, 0.0, 0.0);
        let vel = Vec3::new(0.0, 0.0, rot).cross(&pos); // co-rotating: surface-relative rest
        SimWorld {
            state: Arc::new(Mutex::new(SimState {
                clock,
                last_wall: Instant::now(),
                ut: 1000.0,
                warp: 1.0,
                seq: 0,
                pos_cci: pos,
                vel_cci: vel,
                thrust_dir: pos.normalize(),
                target_dir: pos.normalize(),
                slew_dps: 5.0,
                engine_on: false,
                throttle: 0.0,
                mass_dry: 5_000.0,
                mass_prop: 20_000.0,
                vac_thrust: 500_000.0, // TWR ≈ 2.06 wet
                isp: 450.0,            // ~12 min of hover in the tank
                min_throttle: 0.1,
                mu: 3.986004418e14,
                radius,
                rot,
                atmo_height: 140_000.0,
                atmo_scale_h: 8_500.0,
                atmo_rho0: 1.225,
                trace: Vec::new(),
            })),
        }
    }

    /// A second handle onto the same world (tests keep one to inspect the trace after the flight).
    pub fn handle(&self) -> SimWorld {
        SimWorld {
            state: Arc::clone(&self.state),
        }
    }

    /// The painted/coasted trace: (CCF position, engine-burning) per substep.
    pub fn trace(&self) -> Vec<(Vec3, bool)> {
        self.state.lock().unwrap().trace.clone()
    }

    pub fn ut(&self) -> f64 {
        self.state.lock().unwrap().ut
    }
}

impl SimState {
    fn mass(&self) -> f64 {
        self.mass_dry + self.mass_prop
    }

    fn theta(&self) -> f64 {
        self.rot * self.ut
    }

    fn pos_ccf(&self) -> Vec3 {
        frames::cci_to_ccf(self.pos_cci, self.theta())
    }

    fn advance_to(&mut self, target_ut: f64) {
        let mut guard = 0;
        while self.ut < target_ut - 1e-9 && guard < 2_000_000 {
            let dt = DT.min(target_ut - self.ut);
            self.substep(dt);
            guard += 1;
        }
    }

    fn substep(&mut self, dt: f64) {
        // Attitude: slew the thrust axis toward the target at the FC's rate limit — a true
        // rotation (Rodrigues about the mutual normal), so the angular rate is exact.
        let ang = self
            .thrust_dir
            .dot(&self.target_dir)
            .clamp(-1.0, 1.0)
            .acos();
        if ang > 1e-6 {
            let delta = ang.min(self.slew_dps.to_radians() * dt);
            let axis = self.thrust_dir.cross(&self.target_dir);
            let axis = if axis.norm() < 1e-9 {
                // (anti)parallel: rotate about any orthogonal
                self.thrust_dir.cross(&Vec3::z()).normalize_or(Vec3::x())
            } else {
                axis.normalize()
            };
            self.thrust_dir = (self.thrust_dir * delta.cos()
                + axis.cross(&self.thrust_dir) * delta.sin())
            .normalize_or(self.target_dir);
        }

        let mut acc = frames::gravity(self.pos_cci, self.mu);
        let burning = self.engine_on && self.mass_prop > 0.0;
        if burning {
            let thr = self.throttle.clamp(self.min_throttle, 1.0);
            let force = thr * self.vac_thrust;
            acc += self.thrust_dir * (force / self.mass());
            self.mass_prop = (self.mass_prop - force / (self.isp * 9.80665) * dt).max(0.0);
        }
        self.vel_cci += acc * dt;
        self.pos_cci += self.vel_cci * dt;
        self.ut += dt;
        self.trace.push((self.pos_ccf(), burning));
    }

    fn advance_for_read(&mut self) {
        let target = match self.clock {
            SimClock::Wall => {
                let wall = self.last_wall.elapsed().as_secs_f64();
                self.last_wall = Instant::now();
                self.ut + (wall * self.warp).min(3.0 * self.warp.max(1.0))
            }
            SimClock::PerRead(quantum) => self.ut + quantum * self.warp,
        };
        self.advance_to(target);
    }

    fn telemetry_json(&mut self) -> String {
        self.seq += 1;
        let r = self.pos_cci.norm();
        let alt = r - self.radius;
        let vsurf = frames::surface_velocity_cci(self.pos_cci, self.vel_cci, self.rot).norm();
        let q = ksa_quat::compute_burn_body2cci(self.pos_cci.normalize(), self.thrust_dir);
        let m = self.mass();
        format!(
            concat!(
                "{{\"seq\":{},\"ut\":{:.3},\"warp\":{},\"id\":\"SimBird\",\"sit\":\"Flying\",",
                "\"controlled\":true,\"controllable\":true,\"parent\":\"Earth\",",
                "\"pos_cci\":[{:.3},{:.3},{:.3}],\"vel_cci\":[{:.4},{:.4},{:.4}],",
                "\"vel\":{{\"orb\":{:.2},\"surf\":{:.2},\"inr\":{:.2}}},",
                "\"alt\":{{\"baro\":{:.1},\"radar\":{:.1}}},",
                "\"mass\":{{\"t\":{:.1},\"d\":{:.1},\"p\":{:.1}}},",
                "\"att_q\":[{},{},{},{}]}}"
            ),
            self.seq,
            self.ut,
            self.warp,
            self.pos_cci.x,
            self.pos_cci.y,
            self.pos_cci.z,
            self.vel_cci.x,
            self.vel_cci.y,
            self.vel_cci.z,
            self.vel_cci.norm(),
            vsurf,
            self.vel_cci.norm(),
            alt,
            alt,
            m,
            self.mass_dry,
            self.mass_prop,
            q.x,
            q.y,
            q.z,
            q.w,
        )
    }
}

impl Source for SimWorld {
    fn read(&self, path: &str) -> Result<String, String> {
        let mut s = self.state.lock().unwrap();
        match path {
            "vessels/active/telemetry" => {
                s.advance_for_read();
                Ok(s.telemetry_json())
            }
            "time/ut" => Ok(format!("{}", s.ut)),
            "time/warp" => Ok(format!("{}", s.warp)),
            "time/warp_speeds" => Ok("0.1 1 2 4 10 30 120".into()),
            "debug/time/warp" => Ok(format!("{}", s.warp)),
            "time/sim_dt" => Ok(format!("{}", DT * s.warp)),
            "vessels/active/position/lon" => {
                let p = s.pos_ccf();
                Ok(format!("{}", p.y.atan2(p.x).to_degrees()))
            }
            "vessels/active/ctl/engine" => Ok(if s.engine_on { "1" } else { "0" }.into()),
            "bodies/Earth/mu" => Ok(format!("{}", s.mu)),
            "bodies/Earth/radius" => Ok(format!("{}", s.radius)),
            "bodies/Earth/rotation_rate" => Ok(format!("{}", s.rot)),
            "bodies/Earth/atmosphere/height" => Ok(format!("{}", s.atmo_height)),
            "bodies/Earth/atmosphere/scale_height" => Ok(format!("{}", s.atmo_scale_h)),
            "bodies/Earth/atmosphere/sea_level_density" => Ok(format!("{}", s.atmo_rho0)),
            "vessels/active/engines/0/vac_thrust" => Ok(format!("{}", s.vac_thrust)),
            "vessels/active/engines/0/isp" => Ok(format!("{}", s.isp)),
            "vessels/active/engines/0/min_throttle" => Ok(format!("{}", s.min_throttle)),
            _ => Err("ENOENT".into()),
        }
    }

    fn write(&self, path: &str, value: &str) -> Result<(), CmdError> {
        let mut s = self.state.lock().unwrap();
        let bad = |m: &str| CmdError {
            errno: "EINVAL".into(),
            message: m.into(),
        };
        let nums: Vec<f64> = value
            .split_whitespace()
            .filter_map(|t| t.parse().ok())
            .collect();
        match path {
            "vessels/active/ctl/engine" => {
                s.engine_on = nums.first().copied().unwrap_or(0.0) > 0.5;
                Ok(())
            }
            "vessels/active/ctl/throttle" => {
                s.throttle = nums
                    .first()
                    .copied()
                    .ok_or_else(|| bad("throttle"))?
                    .clamp(0.0, 1.0);
                Ok(())
            }
            "vessels/active/ctl/attitude_target" => {
                if nums.len() != 4 {
                    return Err(bad("quat"));
                }
                let q = Quat::new(nums[0], nums[1], nums[2], nums[3]);
                s.target_dir = ksa_quat::transform(Vec3::x(), q).normalize_or(s.target_dir);
                Ok(())
            }
            "vessels/active/ctl/attitude_mode" => Ok(()),
            "debug/time/warp" => {
                s.warp = nums
                    .first()
                    .copied()
                    .ok_or_else(|| bad("warp"))?
                    .clamp(0.1, 1000.0);
                Ok(())
            }
            p if p.starts_with("debug/vessels/") && p.ends_with("/impulse") => {
                if nums.len() != 3 || !value.contains("dv") {
                    return Err(bad("impulse wants `x y z cci dv`"));
                }
                let dv = Vec3::new(nums[0], nums[1], nums[2]);
                s.vel_cci += dv;
                Ok(())
            }
            "time/alarm" => Ok(()), // handled by wait_until
            _ => Err(CmdError {
                errno: "ENOENT".into(),
                message: path.into(),
            }),
        }
    }

    fn list(&self, path: &str) -> Vec<String> {
        match path {
            "vessels/active/engines" => vec!["0".into()],
            _ => Vec::new(),
        }
    }

    fn wait_until(&self, ut: f64) -> Result<f64, String> {
        let mut s = self.state.lock().unwrap();
        s.advance_to(ut);
        if matches!(s.clock, SimClock::Wall) {
            s.last_wall = Instant::now(); // the jump isn't owed again by the wall clock
        }
        Ok(s.ut)
    }

    fn label(&self) -> String {
        "simulate".into()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hover_thrust_holds_altitude() {
        // Command exactly 1 g of thrust upward: over 10 s altitude should hold to tens of meters
        // (the only drift is thrust/weight growing as propellant burns off).
        let w = SimWorld::new(SimClock::PerRead(0.5));
        let r0 = {
            let s = w.state.lock().unwrap();
            s.pos_cci.norm()
        };
        {
            let mut s = w.state.lock().unwrap();
            s.engine_on = true;
            let g = s.mu / (s.pos_cci.norm() * s.pos_cci.norm());
            s.throttle = g * s.mass() / s.vac_thrust;
            s.target_dir = s.pos_cci.normalize();
            s.thrust_dir = s.target_dir;
            let t = s.ut + 10.0;
            s.advance_to(t);
        }
        let s = w.state.lock().unwrap();
        let drift = (s.pos_cci.norm() - r0).abs();
        assert!(drift < 30.0, "altitude drifted {drift} m");
    }

    #[test]
    fn ballistic_arc_comes_back_down() {
        let w = SimWorld::new(SimClock::PerRead(0.5));
        {
            let mut s = w.state.lock().unwrap();
            // Fling it upward at 50 m/s (surface-relative up), engine off.
            let up = s.pos_cci.normalize();
            s.vel_cci += up * 50.0;
            let t = s.ut + 20.0;
            s.advance_to(t);
        }
        let s = w.state.lock().unwrap();
        // v = 50 − g·t ≈ 50 − 9.6·20 < 0: descending again.
        let up = s.pos_cci.normalize();
        let vr = frames::surface_velocity_cci(s.pos_cci, s.vel_cci, s.rot).dot(&up);
        assert!(vr < -50.0, "should be falling, vr={vr}");
    }

    #[test]
    fn attitude_slew_is_rate_limited() {
        let w = SimWorld::new(SimClock::PerRead(0.5));
        {
            let mut s = w.state.lock().unwrap();
            let east = frames::enu_basis(s.pos_cci).east;
            s.target_dir = east; // 90° away from the initial up
            let t = s.ut + 6.0;
            s.advance_to(t);
        }
        let s = w.state.lock().unwrap();
        let ang = s
            .thrust_dir
            .dot(&s.target_dir)
            .clamp(-1.0, 1.0)
            .acos()
            .to_degrees();
        // 6 s at 5°/s = 30° of the 90° covered → ~60° remains.
        assert!(
            (ang - 60.0).abs() < 5.0,
            "slew not rate-limited: {ang}° left"
        );
    }
}
