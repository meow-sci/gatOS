//! Application state + input handling. The render pass (see `ui.rs`) reads this state and writes
//! back the interactive hit-test rects (dashboard rows, detail control ring) used by the mouse
//! handler on the next event — so keyboard and mouse drive exactly the same [`Action`]s.

use std::sync::mpsc::Sender;

use ratatui::crossterm::event::{KeyCode, KeyEvent, MouseButton, MouseEvent, MouseEventKind};
use ratatui::layout::{Position, Rect};
use ratatui::widgets::TableState;

use crate::api::{Command, CommandError, Snapshot, Status};

/// A message from the worker thread to the UI.
pub enum Update {
    Snapshot(Snapshot),
    Status(Status),
    CommandDone(Result<(), CommandError>),
    Error(String),
}

pub enum Screen {
    Dashboard,
    Detail(String),
}

/// Every control the detail screen exposes. Keyboard (focus + Enter) and mouse (click) both resolve
/// to one of these; `activate` maps it to a `POST /v1/command`.
#[derive(Clone, Copy, PartialEq)]
pub enum Action {
    Back,
    Ignite,
    Shutdown,
    Stage,
    ThrottleDown,
    ThrottleUp,
    ThrottleZero,
    ThrottleFull,
    ToggleLights,
    ToggleRcs,
    CycleAttitude,
    EngineToggle(usize),
    LightToggle(usize),
    RefillFuel,
    RefillBattery,
    WarpDown,
    WarpUp,
}

/// The flight-computer attitude modes the cycle control steps through.
const ATTITUDE_MODES: &[&str] = &[
    "manual",
    "Prograde",
    "Retrograde",
    "Normal",
    "AntiNormal",
    "RadialOut",
    "RadialIn",
];

const THROTTLE_STEP: f64 = 0.1;

pub struct App {
    pub snapshot: Snapshot,
    pub status: Status,
    pub screen: Screen,
    pub table: TableState,
    /// Detail control ring (rect + action), rebuilt every render; mouse hit-tests against it.
    pub controls: Vec<(Rect, Action)>,
    /// Index into `controls` of the keyboard-focused control.
    pub focus: usize,
    /// Data-rows area of the dashboard table (excl. header), for click → row mapping.
    pub dashboard_area: Rect,
    pub status_line: String,
    pub status_is_error: bool,
    pub connected: bool,
    pub should_quit: bool,
    cmd_tx: Sender<Command>,
}

impl App {
    pub fn new(cmd_tx: Sender<Command>) -> Self {
        let mut table = TableState::default();
        table.select(Some(0));
        Self {
            snapshot: Snapshot::default(),
            status: Status::default(),
            screen: Screen::Dashboard,
            table,
            controls: Vec::new(),
            focus: 0,
            dashboard_area: Rect::default(),
            status_line: "connecting…".into(),
            status_is_error: false,
            connected: false,
            should_quit: false,
            cmd_tx,
        }
    }

    // ---- worker updates ----------------------------------------------------------------------

    pub fn apply(&mut self, update: Update) {
        match update {
            Update::Snapshot(s) => {
                self.snapshot = s;
                self.connected = true;
                self.clamp_selection();
            }
            Update::Status(s) => self.status = s,
            Update::CommandDone(Ok(())) => {
                self.status_line = "ok".into();
                self.status_is_error = false;
            }
            Update::CommandDone(Err(e)) => {
                self.status_line = format!("{}: {}", e.errno, e.message);
                self.status_is_error = true;
            }
            Update::Error(msg) => {
                self.connected = false;
                self.status_line = format!("offline: {msg}");
                self.status_is_error = true;
            }
        }
    }

    fn clamp_selection(&mut self) {
        let n = self.snapshot.vessels.len();
        if n == 0 {
            self.table.select(None);
        } else {
            let i = self.table.selected().unwrap_or(0).min(n - 1);
            self.table.select(Some(i));
        }
    }

    // ---- keyboard ----------------------------------------------------------------------------

    pub fn on_key(&mut self, key: KeyEvent) {
        if key.code == KeyCode::Char('q') {
            self.should_quit = true;
            return;
        }
        match self.screen {
            Screen::Dashboard => self.on_key_dashboard(key),
            Screen::Detail(_) => self.on_key_detail(key),
        }
    }

    fn on_key_dashboard(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc => self.should_quit = true,
            KeyCode::Up | KeyCode::Char('k') => self.move_selection(-1),
            KeyCode::Down | KeyCode::Char('j') => self.move_selection(1),
            KeyCode::Enter | KeyCode::Char('l') => self.open_selected(),
            _ => {}
        }
    }

    fn on_key_detail(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc | KeyCode::Backspace => self.screen = Screen::Dashboard,
            KeyCode::Tab | KeyCode::Down | KeyCode::Char('j') => self.move_focus(1),
            KeyCode::BackTab | KeyCode::Up | KeyCode::Char('k') => self.move_focus(-1),
            KeyCode::Left | KeyCode::Char('-') => self.activate(Action::ThrottleDown),
            KeyCode::Right | KeyCode::Char('=') | KeyCode::Char('+') => {
                self.activate(Action::ThrottleUp)
            }
            KeyCode::Enter | KeyCode::Char(' ') => {
                if let Some(&(_, action)) = self.controls.get(self.focus) {
                    self.activate(action);
                }
            }
            _ => {}
        }
    }

    fn move_selection(&mut self, delta: i32) {
        let n = self.snapshot.vessels.len();
        if n == 0 {
            return;
        }
        let cur = self.table.selected().unwrap_or(0) as i32;
        let next = (cur + delta).rem_euclid(n as i32) as usize;
        self.table.select(Some(next));
    }

    fn move_focus(&mut self, delta: i32) {
        let n = self.controls.len();
        if n == 0 {
            return;
        }
        let cur = self.focus.min(n - 1) as i32;
        self.focus = (cur + delta).rem_euclid(n as i32) as usize;
    }

    fn open_selected(&mut self) {
        if let Some(i) = self.table.selected() {
            if let Some(v) = self.snapshot.vessels.get(i) {
                self.screen = Screen::Detail(v.id.clone());
                self.focus = 0;
                self.status_line = String::new();
                self.status_is_error = false;
            }
        }
    }

    // ---- mouse -------------------------------------------------------------------------------

    pub fn on_mouse(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => self.scroll(-1),
            MouseEventKind::ScrollDown => self.scroll(1),
            MouseEventKind::Down(MouseButton::Left) => self.on_click(m.column, m.row),
            _ => {}
        }
    }

    fn scroll(&mut self, delta: i32) {
        match self.screen {
            Screen::Dashboard => self.move_selection(delta),
            Screen::Detail(_) => self.move_focus(delta),
        }
    }

    fn on_click(&mut self, x: u16, y: u16) {
        match self.screen {
            Screen::Dashboard => {
                let area = self.dashboard_area;
                if area.height == 0 || y < area.y || y >= area.y + area.height {
                    return;
                }
                let row = (y - area.y) as usize;
                let idx = self.table.offset() + row;
                if idx < self.snapshot.vessels.len() {
                    if self.table.selected() == Some(idx) {
                        self.open_selected(); // second click on the selected row opens it
                    } else {
                        self.table.select(Some(idx));
                    }
                }
            }
            Screen::Detail(_) => {
                let pos = Position { x, y };
                if let Some(i) = self.controls.iter().position(|(r, _)| r.contains(pos)) {
                    self.focus = i;
                    let action = self.controls[i].1;
                    self.activate(action);
                }
            }
        }
    }

    // ---- command dispatch --------------------------------------------------------------------

    fn activate(&mut self, action: Action) {
        if action == Action::Back {
            self.screen = Screen::Dashboard;
            return;
        }
        let Screen::Detail(ref id) = self.screen else {
            return;
        };
        let id = id.clone();
        let Some(v) = self.snapshot.vessels.iter().find(|v| v.id == id).cloned() else {
            self.status_line = "vessel is gone".into();
            self.status_is_error = true;
            return;
        };

        let cmd = match action {
            Action::Back => return,
            Action::Ignite => Command::vessel(&id, "vessel.ignite", 1.0),
            Action::Shutdown => Command::vessel(&id, "vessel.shutdown", 1.0),
            Action::Stage => Command::vessel(&id, "vessel.stage", 1.0),
            Action::ThrottleDown => Command::vessel(
                &id,
                "vessel.throttle",
                clamp01(v.throttle_cmd - THROTTLE_STEP),
            ),
            Action::ThrottleUp => Command::vessel(
                &id,
                "vessel.throttle",
                clamp01(v.throttle_cmd + THROTTLE_STEP),
            ),
            Action::ThrottleZero => Command::vessel(&id, "vessel.throttle", 0.0),
            Action::ThrottleFull => Command::vessel(&id, "vessel.throttle", 1.0),
            Action::ToggleLights => Command::vessel(&id, "vessel.lights", flip(v.lights_master_on)),
            Action::ToggleRcs => Command::vessel(&id, "vessel.rcs", flip(v.rcs_on)),
            Action::CycleAttitude => {
                Command::token(&id, "vessel.attitude_mode", next_attitude(&v.attitude_mode))
            }
            Action::EngineToggle(i) => match v.engines.get(i) {
                Some(e) => Command::module(&id, "engine.active", e.index, flip(e.active)),
                None => return,
            },
            Action::LightToggle(i) => match v.lights.get(i) {
                Some(l) => Command::module(&id, "light.on", l.index, flip(l.on)),
                None => return,
            },
            Action::RefillFuel => Command::vessel(&id, "debug.refill_fuel", 1.0),
            Action::RefillBattery => Command::vessel(&id, "debug.refill_battery", 1.0),
            Action::WarpDown => Command::vessel(
                &id,
                "debug.warp",
                (self.snapshot.warp_factor / 2.0).max(1.0),
            ),
            Action::WarpUp => Command::vessel(
                &id,
                "debug.warp",
                (self.snapshot.warp_factor * 2.0).max(1.0),
            ),
        };

        let _ = self.cmd_tx.send(cmd);
        self.status_line = "sending…".into();
        self.status_is_error = false;
    }
}

fn clamp01(v: f64) -> f64 {
    v.clamp(0.0, 1.0)
}

fn flip(on: bool) -> f64 {
    if on {
        0.0
    } else {
        1.0
    }
}

fn next_attitude(current: &str) -> &'static str {
    let i = ATTITUDE_MODES
        .iter()
        .position(|m| m.eq_ignore_ascii_case(current))
        .unwrap_or(0);
    ATTITUDE_MODES[(i + 1) % ATTITUDE_MODES.len()]
}
