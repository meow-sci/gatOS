//! Application state, the worker protocol, and input handling. The pilot's levers — ENGAGE, ABORT, and
//! the live G-limit — are sent to the worker, which owns the autopilot; the worker streams back the sim
//! state plus a guidance view for the HUD. (Plan §9.2.)

use std::sync::mpsc::Sender;

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

use crate::guidance::autopilot::{Phase, UpfgStatus};
use crate::guidance::frames;
use crate::guidance::ksa_quat::{self, Quat};
use crate::guidance::Vec3;
use crate::sim::{Body, Telemetry, Tick};

/// UI → worker.
pub enum ToWorker {
    /// Set the max-deceleration lever (g₀), re-planning live.
    SetGLimit(f64),
    /// Arm the autopilot (begin powered-descent guidance to the point below).
    Engage,
    /// Abort: cut throttle, release attitude to manual.
    Abort,
}

/// A snapshot of the autopilot for the HUD.
#[derive(Debug, Clone, Copy)]
pub struct GuidanceView {
    pub phase: Phase,
    pub throttle: f64,
    pub tgo: f64,
    pub predicted_mass: f64,
    pub peak_g: f64,
    /// UPFG diagnostics (present only in the terminal leg).
    pub upfg: Option<UpfgStatus>,
}

/// worker → UI.
pub enum FromWorker {
    Tick {
        tick: Tick,
        guidance: Option<GuidanceView>,
        /// A control-write status line (message, is_error), if anything notable happened this tick.
        status: Option<(String, bool)>,
    },
}

pub struct App {
    tx: Sender<ToWorker>,
    pub should_quit: bool,
    pub label: String,

    // ---- live state from the worker ----
    pub connected: bool,
    pub telemetry: Option<Telemetry>,
    pub body: Option<Body>,
    pub derived: Option<Derived>,
    pub guidance: Option<GuidanceView>,

    // ---- pilot levers (mirrors of the worker's autopilot inputs, for display + adjustment) ----
    pub g_limit: f64,

    pub status: String,
    pub status_err: bool,
}

impl App {
    pub fn new(tx: Sender<ToWorker>, label: String) -> Self {
        Self {
            tx,
            should_quit: false,
            label,
            connected: false,
            telemetry: None,
            body: None,
            derived: None,
            guidance: None,
            g_limit: crate::guidance::autopilot::Inputs::default().g_limit,
            status: "connecting\u{2026}".to_string(),
            status_err: false,
        }
    }

    pub fn apply(&mut self, msg: FromWorker) {
        match msg {
            FromWorker::Tick {
                tick,
                guidance,
                status,
            } => {
                self.connected = tick.connected;
                self.telemetry = tick.telemetry;
                self.body = tick.body;
                self.derived = self.telemetry.as_ref().map(|t| derive(t, self.body));
                self.guidance = guidance;
                if let Some((msg, err)) = status {
                    self.status = msg;
                    self.status_err = err;
                } else if !self.connected {
                    self.status = format!("not connected \u{b7} {}", self.label);
                    self.status_err = false;
                } else if self.telemetry.is_none() {
                    self.status = "no active vessel".to_string();
                    self.status_err = false;
                }
            }
        }
    }

    pub fn on_key(&mut self, k: KeyEvent) {
        match k.code {
            KeyCode::Char('q') | KeyCode::Esc => self.should_quit = true,
            KeyCode::Char('c') if k.modifiers.contains(KeyModifiers::CONTROL) => {
                self.should_quit = true
            }
            KeyCode::Char('e') => {
                let _ = self.tx.send(ToWorker::Engage);
                self.set_status("ENGAGE", false);
            }
            KeyCode::Char('a') => {
                let _ = self.tx.send(ToWorker::Abort);
                self.set_status("ABORT", true);
            }
            KeyCode::Up | KeyCode::Char('=') | KeyCode::Char('+') => self.nudge_g_limit(0.5),
            KeyCode::Down | KeyCode::Char('-') | KeyCode::Char('_') => self.nudge_g_limit(-0.5),
            _ => {}
        }
    }

    fn nudge_g_limit(&mut self, delta: f64) {
        self.g_limit = (self.g_limit + delta).clamp(1.0, 8.0);
        let _ = self.tx.send(ToWorker::SetGLimit(self.g_limit));
        self.set_status(&format!("G-limit \u{2192} {:.1} g", self.g_limit), false);
    }

    fn set_status(&mut self, message: &str, is_error: bool) {
        self.status = message.to_string();
        self.status_err = is_error;
    }
}

/// Frame-derived flight quantities for the HUD (plan §3.4). Computed by the glue layer from the CCI
/// telemetry + parent-body constants; the guidance core itself stays `Vec3`-only.
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
    /// Angle from the surface-retrograde hold we would command — the attitude path, previewed live.
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
    use std::sync::mpsc;

    fn app() -> (App, mpsc::Receiver<ToWorker>) {
        let (tx, rx) = mpsc::channel();
        (App::new(tx, "fs:/sim".into()), rx)
    }

    fn tick(connected: bool, telemetry: Option<Telemetry>) -> FromWorker {
        FromWorker::Tick {
            tick: Tick {
                connected,
                telemetry,
                body: None,
            },
            guidance: None,
            status: None,
        }
    }

    fn sample_telemetry() -> Telemetry {
        serde_json::from_str(
            r#"{"seq":1,"ut":0,"warp":1,"id":"x","sit":"Freefall","controlled":true,
                "pos_cci":[600000,0,1000],"vel_cci":[10,0,-82],
                "vel":{"orb":0,"surf":0,"inr":0},"alt":{"baro":1000,"radar":1000},
                "mass":{"t":1500,"d":1000,"p":500},"att_q":[0,0,0,1],"power":{"prod":0,"cons":0}}"#,
        )
        .unwrap()
    }

    #[test]
    fn apply_tracks_connection_and_vessel() {
        let (mut a, _rx) = app();
        a.apply(tick(false, None));
        assert!(!a.connected && a.telemetry.is_none());
        assert!(a.status.starts_with("not connected"));

        a.apply(tick(true, None));
        assert!(a.connected && a.telemetry.is_none());
        assert_eq!(a.status, "no active vessel");

        a.apply(tick(true, Some(sample_telemetry())));
        assert!(a.telemetry.is_some() && a.derived.is_some());
    }

    #[test]
    fn keys_send_worker_commands() {
        let (mut a, rx) = app();
        a.on_key(KeyEvent::new(KeyCode::Char('e'), KeyModifiers::NONE));
        assert!(matches!(rx.try_recv(), Ok(ToWorker::Engage)));

        a.on_key(KeyEvent::new(KeyCode::Char('a'), KeyModifiers::NONE));
        assert!(matches!(rx.try_recv(), Ok(ToWorker::Abort)));

        let g0 = a.g_limit;
        a.on_key(KeyEvent::new(KeyCode::Up, KeyModifiers::NONE));
        assert!(a.g_limit > g0);
        assert!(matches!(rx.try_recv(), Ok(ToWorker::SetGLimit(_))));
    }

    #[test]
    fn quit_keys() {
        let (mut a, _rx) = app();
        a.on_key(KeyEvent::new(KeyCode::Char('q'), KeyModifiers::NONE));
        assert!(a.should_quit);
    }
}
