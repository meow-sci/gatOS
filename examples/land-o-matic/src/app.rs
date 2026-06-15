//! Application state + input handling. For M0 this is a read-only monitor: it holds the latest poll of
//! the active vessel and a status line. (Guidance inputs — G-limit, target, ENGAGE/ABORT — arrive in
//! M3, see `LANDING_PROGRAM_PLAN.md` §9.2.)

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

use crate::guidance::frames;
use crate::guidance::ksa_quat::{self, Quat};
use crate::guidance::Vec3;
use crate::sim::{Body, FromWorker, Telemetry};

pub struct App {
    pub should_quit: bool,
    pub label: String,

    // ---- live state from the worker ----
    pub connected: bool,
    pub telemetry: Option<Telemetry>,
    pub body: Option<Body>,
    /// Frame-derived quantities, recomputed each tick (None until the first telemetry).
    pub derived: Option<Derived>,

    pub status: String,
    pub status_err: bool,
}

impl App {
    pub fn new(label: String) -> Self {
        Self {
            should_quit: false,
            label,
            connected: false,
            telemetry: None,
            body: None,
            derived: None,
            status: "connecting\u{2026}".to_string(),
            status_err: false,
        }
    }

    pub fn apply(&mut self, msg: FromWorker) {
        match msg {
            FromWorker::Tick(t) => {
                self.connected = t.connected;
                self.telemetry = t.telemetry;
                self.body = t.body;
                self.derived = self.telemetry.as_ref().map(|tel| derive(tel, self.body));
                self.status = if !self.connected {
                    format!("not connected \u{b7} {}", self.label)
                } else if self.telemetry.is_none() {
                    "no active vessel".to_string()
                } else {
                    "monitoring".to_string()
                };
                self.status_err = false;
            }
        }
    }

    pub fn on_key(&mut self, k: KeyEvent) {
        match k.code {
            KeyCode::Char('q') | KeyCode::Esc => self.should_quit = true,
            KeyCode::Char('c') if k.modifiers.contains(KeyModifiers::CONTROL) => {
                self.should_quit = true
            }
            _ => {}
        }
    }
}

/// Frame-derived flight quantities for the HUD/guidance (plan §3.4). Computed by the glue layer from
/// the CCI telemetry + parent-body constants; the guidance core itself stays `Vec3`-only.
#[derive(Debug, Clone, Copy)]
pub struct Derived {
    /// Surface-relative vertical speed, m/s (+ up, − descending).
    pub vertical_speed: f64,
    /// Surface-relative horizontal (ground-track) speed, m/s.
    pub horizontal_speed: f64,
    /// Local gravity μ/r², m/s² (0 if the parent body is unknown).
    pub gravity: f64,
    /// Angle of the thrust axis (+X body) from local up, degrees.
    pub pitch_from_up_deg: f64,
    /// Angle from the surface-retrograde hold we would command — the M3 attitude path, previewed live.
    pub retro_error_deg: f64,
}

/// Build [`Derived`] from telemetry + parent-body constants, exercising the frame + quaternion core.
pub fn derive(t: &Telemetry, body: Option<Body>) -> Derived {
    let pos = Vec3::new(t.pos_cci[0], t.pos_cci[1], t.pos_cci[2]);
    let vel = Vec3::new(t.vel_cci[0], t.vel_cci[1], t.vel_cci[2]);
    let omega = body.map_or(0.0, |b| b.rotation_rate);

    let basis = frames::enu_basis(pos);
    let v_surf = frames::surface_velocity(vel, pos, omega);
    let v_enu = frames::to_enu(v_surf, &basis);
    let vertical_speed = v_enu.z;
    let horizontal_speed = v_enu.x.hypot(v_enu.y);
    let gravity = body.map_or(0.0, |b| b.mu / pos.norm_squared());

    let nose = ksa_quat::transform(Vec3::new(1.0, 0.0, 0.0), Quat::from_array(t.att_q));
    let pitch_from_up_deg = angle_between(nose, basis.up).to_degrees();

    // The surface-retrograde hold we would command — the exact M3 attitude path, previewed live.
    let retro_error_deg = if v_surf.norm() > 1e-2 {
        let target = ksa_quat::compute_burn_body2cci(basis.up, -v_surf.normalize());
        let aim = ksa_quat::transform(Vec3::new(1.0, 0.0, 0.0), target);
        angle_between(nose, aim).to_degrees()
    } else {
        0.0
    };

    Derived {
        vertical_speed,
        horizontal_speed,
        gravity,
        pitch_from_up_deg,
        retro_error_deg,
    }
}

fn angle_between(a: Vec3, b: Vec3) -> f64 {
    let (na, nb) = (a.norm(), b.norm());
    if na < 1e-12 || nb < 1e-12 {
        0.0
    } else {
        (a.dot(&b) / (na * nb)).clamp(-1.0, 1.0).acos()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sim::Tick;

    fn sample_telemetry(controlled: bool) -> Telemetry {
        serde_json::from_str(&format!(
            r#"{{"seq":1,"ut":0,"warp":1,"id":"x","sit":"Freefall","controlled":{controlled},
                "pos_cci":[0,0,0],"pos_ecl":[0,0,0],"vel_cci":[0,0,0],
                "vel":{{"orb":0,"surf":0,"inr":0}},"alt":{{"baro":0,"radar":0}},
                "mass":{{"t":1,"d":1,"p":0}},"att_q":[0,0,0,1],"power":{{"prod":0,"cons":0}}}}"#
        ))
        .unwrap()
    }

    #[test]
    fn apply_tracks_connection_and_vessel() {
        let mut a = App::new("fs:/sim".into());
        // Not connected.
        a.apply(FromWorker::Tick(Tick::default()));
        assert!(!a.connected && a.telemetry.is_none());
        assert!(a.status.starts_with("not connected"));

        // Connected, no vessel.
        a.apply(FromWorker::Tick(Tick {
            connected: true,
            ..Tick::default()
        }));
        assert!(a.connected && a.telemetry.is_none());
        assert_eq!(a.status, "no active vessel");

        // Connected with a controlled vessel.
        a.apply(FromWorker::Tick(Tick {
            connected: true,
            telemetry: Some(sample_telemetry(true)),
            body: None,
        }));
        assert!(a.telemetry.is_some());
        assert_eq!(a.status, "monitoring");
    }

    #[test]
    fn quit_keys() {
        let mut a = App::new("x".into());
        a.on_key(KeyEvent::new(KeyCode::Char('q'), KeyModifiers::NONE));
        assert!(a.should_quit);
    }

    #[test]
    fn derive_computes_surface_relative_descent() {
        // Equatorial vessel at radius 601 km, descending 50 m/s, otherwise co-rotating with the ground.
        let omega = 2.9089e-4_f64;
        let r = 601_000.0_f64;
        let vy = omega * r; // co-rotation speed at this point
        let json = format!(
            r#"{{"seq":1,"ut":0,"warp":1,"id":"x","sit":"Freefall","controlled":true,
                "pos_cci":[{r},0,0],"vel_cci":[-50,{vy},0],
                "vel":{{"orb":0,"surf":0,"inr":0}},"alt":{{"baro":1000,"radar":1000}},
                "mass":{{"t":5000,"d":4000,"p":1000}},"att_q":[0,0,0,1],"power":{{"prod":0,"cons":0}}}}"#
        );
        let t: Telemetry = serde_json::from_str(&json).unwrap();
        let body = Body {
            mu: 3.5316e12,
            radius: 600_000.0,
            rotation_rate: omega,
        };
        let d = derive(&t, Some(body));
        assert!((d.vertical_speed + 50.0).abs() < 1e-3, "vspeed {}", d.vertical_speed);
        assert!(d.horizontal_speed < 1e-3, "hspeed {}", d.horizontal_speed);
        assert!((d.gravity - 9.78).abs() < 0.1, "gravity {}", d.gravity);
    }
}
