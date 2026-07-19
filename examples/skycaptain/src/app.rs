//! UI-side state + the worker protocol. The main thread owns [`App`] (render + keys); the worker
//! thread owns the [`crate::sim::Source`] and the [`crate::flight::Flight`], and the two talk over
//! two mpsc channels — the land-o-matic pattern, so the render loop never blocks on I/O (the worker
//! deliberately *does* block, briefly, on `time/alarm` precision windows).

use std::collections::VecDeque;
use std::sync::mpsc::Sender;

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

use crate::flight::FlightView;

/// UI → worker.
pub enum ToWorker {
    /// Compile + fly this text.
    Start(String),
    /// Cut the engine, restore warp 1, release the vehicle.
    Abort,
}

/// Worker → UI.
pub enum FromWorker {
    /// Idle-mode readiness report (one per poll).
    Idle {
        connected: bool,
        label: String,
        checks: Vec<(String, bool)>,
    },
    /// The flight was refused before takeoff.
    StartFailed {
        reason: String,
        notes: Vec<String>,
    },
    /// The flight is on: static plan data for the preview canvas.
    Started {
        letters: Vec<char>,
        outline: Vec<Vec<(f64, f64)>>,
        total_time: f64,
        notes: Vec<String>,
    },
    /// One control-cycle report while flying.
    Flight(FlightView),
    Log(String),
}

#[derive(Clone, Copy, PartialEq)]
pub enum Screen {
    Input,
    Flying,
    Done,
}

pub struct App {
    pub tx: Sender<ToWorker>,
    pub should_quit: bool,
    pub screen: Screen,
    // input screen
    pub text: String,
    pub connected: bool,
    pub source_label: String,
    pub checks: Vec<(String, bool)>,
    pub start_error: Option<String>,
    // flight screen
    pub letters: Vec<char>,
    pub outline: Vec<Vec<(f64, f64)>>,
    pub painted: Vec<(f64, f64)>,
    pub total_time: f64,
    pub view: FlightView,
    pub end_state: Option<Result<(), String>>,
    pub log: VecDeque<String>,
    // tuning shown on the input screen (informational; set by CLI flags)
    pub info_height: f64,
    pub info_speed: f64,
    pub info_warps: (f64, f64),
}

impl App {
    pub fn new(
        tx: Sender<ToWorker>,
        label: String,
        text: String,
        height: f64,
        speed: f64,
        warps: (f64, f64),
    ) -> App {
        App {
            tx,
            should_quit: false,
            screen: Screen::Input,
            text,
            connected: false,
            source_label: label,
            checks: Vec::new(),
            start_error: None,
            letters: Vec::new(),
            outline: Vec::new(),
            painted: Vec::new(),
            total_time: 0.0,
            view: FlightView::default(),
            end_state: None,
            log: VecDeque::new(),
            info_height: height,
            info_speed: speed,
            info_warps: warps,
        }
    }

    pub fn apply(&mut self, msg: FromWorker) {
        match msg {
            FromWorker::Idle {
                connected,
                label,
                checks,
            } => {
                self.connected = connected;
                self.source_label = label;
                self.checks = checks;
            }
            FromWorker::StartFailed { reason, notes } => {
                for n in notes {
                    self.push_log(n);
                }
                self.start_error = Some(reason);
                self.screen = Screen::Input;
            }
            FromWorker::Started {
                letters,
                outline,
                total_time,
                notes,
            } => {
                self.letters = letters;
                self.outline = outline;
                self.total_time = total_time;
                self.painted.clear();
                self.end_state = None;
                self.start_error = None;
                for n in notes {
                    self.push_log(n);
                }
                self.screen = Screen::Flying;
            }
            FromWorker::Flight(v) => {
                self.painted.extend(v.painted_append.iter().copied());
                if self.painted.len() > 6000 {
                    let drop = self.painted.len() - 6000;
                    self.painted.drain(..drop);
                }
                if v.done {
                    self.end_state = Some(Ok(()));
                    self.screen = Screen::Done;
                } else if let Some(r) = &v.aborted {
                    self.end_state = Some(Err(r.clone()));
                    self.screen = Screen::Done;
                }
                self.view = v;
            }
            FromWorker::Log(l) => self.push_log(l),
        }
    }

    fn push_log(&mut self, line: String) {
        self.log.push_back(line);
        while self.log.len() > 120 {
            self.log.pop_front();
        }
    }

    pub fn on_key(&mut self, k: KeyEvent) {
        if k.code == KeyCode::Char('c') && k.modifiers.contains(KeyModifiers::CONTROL) {
            let _ = self.tx.send(ToWorker::Abort);
            self.should_quit = true;
            return;
        }
        match self.screen {
            Screen::Input => match k.code {
                KeyCode::Esc => self.should_quit = true,
                KeyCode::Enter => {
                    if !self.text.trim().is_empty() {
                        self.start_error = None;
                        let _ = self.tx.send(ToWorker::Start(self.text.clone()));
                    }
                }
                KeyCode::Backspace => {
                    self.text.pop();
                }
                KeyCode::Char(c) if self.text.len() < 60 => self.text.push(c),
                _ => {}
            },
            Screen::Flying => match k.code {
                KeyCode::Char('a') | KeyCode::Char('A') | KeyCode::Esc => {
                    let _ = self.tx.send(ToWorker::Abort);
                }
                KeyCode::Char('q') | KeyCode::Char('Q') => {
                    let _ = self.tx.send(ToWorker::Abort);
                    self.should_quit = true;
                }
                _ => {}
            },
            Screen::Done => match k.code {
                KeyCode::Char('n') | KeyCode::Char('N') => {
                    self.screen = Screen::Input;
                    self.end_state = None;
                }
                KeyCode::Char('q') | KeyCode::Char('Q') | KeyCode::Esc => self.should_quit = true,
                _ => {}
            },
        }
    }
}

/// `m:ss` for game/wall second displays.
pub fn mmss(secs: f64) -> String {
    let s = secs.max(0.0).round() as u64;
    format!("{}:{:02}", s / 60, s % 60)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mmss_formats() {
        assert_eq!(mmss(0.0), "0:00");
        assert_eq!(mmss(61.4), "1:01");
        assert_eq!(mmss(3599.6), "60:00");
    }
}
