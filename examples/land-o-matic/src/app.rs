//! Application state + input handling. For M0 this is a read-only monitor: it holds the latest poll of
//! the active vessel and a status line. (Guidance inputs — G-limit, target, ENGAGE/ABORT — arrive in
//! M3, see `LANDING_PROGRAM_PLAN.md` §9.2.)

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

use crate::sim::{Body, FromWorker, Telemetry};

pub struct App {
    pub should_quit: bool,
    pub label: String,

    // ---- live state from the worker ----
    pub connected: bool,
    pub telemetry: Option<Telemetry>,
    pub body: Option<Body>,

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
}
