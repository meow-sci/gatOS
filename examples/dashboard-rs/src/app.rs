//! Application state + input handling. The render pass (see `ui.rs`) reads this state and writes
//! back the interactive hit-test rects (dashboard rows, detail control ring) used by the mouse
//! handler on the next event — so keyboard and mouse drive exactly the same [`Action`]s.

use std::sync::mpsc::Sender;
use std::time::Duration;

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
/// to one of these; `activate` maps it to a `POST /v1/command` (or, for the attitude control, opens
/// the modal picker).
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
    OpenAttitudePicker,
    EngineToggle(usize),
    LightToggle(usize),
    RefillFuel,
    RefillBattery,
    WarpDown,
    WarpUp,
}

/// The attitude modes the picker offers — `manual` (unset / drop the flight computer to manual)
/// plus every `FlightComputerAttitudeTrackTarget`. Kept in step with the canonical token list the
/// 9p `ctl/attitude_mode` control file accepts (`SimFsTree.AttitudeModeTokens`).
pub const ATTITUDE_MODES: &[&str] = &[
    "manual",
    "Prograde",
    "Retrograde",
    "Normal",
    "AntiNormal",
    "RadialOut",
    "RadialIn",
    "Toward",
    "Away",
    "Antivel",
    "Align",
    "Forward",
    "Backward",
    "Up",
    "Down",
    "Ahead",
    "Behind",
    "Outward",
    "Inward",
    "PositiveDv",
    "NegativeDv",
    "Custom",
    "None",
];

const THROTTLE_STEP: f64 = 0.1;

/// A modal list-picker overlay (currently the attitude-mode chooser). While `App.picker` is
/// `Some`, all keyboard/mouse input routes to it; the render pass draws it over the detail screen
/// and writes back the hit-test rects (`area`, `item_rects`) the mouse handler reads next event.
pub struct Picker {
    pub title: &'static str,
    /// The command action the chosen token submits (e.g. `vessel.attitude_mode`).
    pub action: String,
    pub options: Vec<&'static str>,
    pub selected: usize,
    /// First visible option (the render pass scrolls `selected` into view).
    pub offset: usize,
    /// Popup outer rect — a click outside it cancels.
    pub area: Rect,
    /// Visible option rects → option index, for click hit-testing.
    pub item_rects: Vec<(Rect, usize)>,
}

pub struct App {
    pub snapshot: Snapshot,
    pub status: Status,
    pub screen: Screen,
    pub table: TableState,
    /// Detail control ring (rect + action), rebuilt every render; mouse hit-tests against it.
    pub controls: Vec<(Rect, Action)>,
    /// Index into `controls` of the keyboard-focused control.
    pub focus: usize,
    /// The throttle bar's clickable rect on the detail telemetry pane (set each render); a click
    /// inside it sets the throttle to the clicked fraction.
    pub throttle_bar: Rect,
    /// The active modal picker overlay, or `None` when no popup is open.
    pub picker: Option<Picker>,
    /// Data-rows area of the dashboard table (excl. header), for click → row mapping.
    pub dashboard_area: Rect,
    pub status_line: String,
    pub status_is_error: bool,
    pub connected: bool,
    /// The worker's snapshot poll interval (shown in the header so the read-back cadence is visible).
    pub poll_interval: Duration,
    pub should_quit: bool,
    cmd_tx: Sender<Command>,
}

impl App {
    pub fn new(cmd_tx: Sender<Command>, poll_interval: Duration) -> Self {
        let mut table = TableState::default();
        table.select(Some(0));
        Self {
            snapshot: Snapshot::default(),
            status: Status::default(),
            screen: Screen::Dashboard,
            table,
            controls: Vec::new(),
            focus: 0,
            throttle_bar: Rect::default(),
            picker: None,
            dashboard_area: Rect::default(),
            status_line: "connecting…".into(),
            status_is_error: false,
            connected: false,
            poll_interval,
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
        // A modal picker swallows all input until it is confirmed or cancelled.
        if self.picker.is_some() {
            self.on_key_picker(key);
            return;
        }
        if key.code == KeyCode::Char('q') {
            self.should_quit = true;
            return;
        }
        match self.screen {
            Screen::Dashboard => self.on_key_dashboard(key),
            Screen::Detail(_) => self.on_key_detail(key),
        }
    }

    fn on_key_picker(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc | KeyCode::Backspace | KeyCode::Char('q') => self.picker = None,
            KeyCode::Up | KeyCode::Char('k') | KeyCode::BackTab => self.picker_move(-1),
            KeyCode::Down | KeyCode::Char('j') | KeyCode::Tab => self.picker_move(1),
            KeyCode::Enter | KeyCode::Char(' ') => self.picker_confirm(),
            _ => {}
        }
    }

    fn picker_move(&mut self, delta: i32) {
        if let Some(p) = self.picker.as_mut() {
            let n = p.options.len();
            if n == 0 {
                return;
            }
            p.selected = (p.selected as i32 + delta).rem_euclid(n as i32) as usize;
        }
    }

    /// Submits the highlighted option as a token command and closes the picker.
    fn picker_confirm(&mut self) {
        let Some(p) = self.picker.take() else {
            return;
        };
        let Screen::Detail(ref id) = self.screen else {
            return;
        };
        let id = id.clone();
        let token = p.options[p.selected];
        let _ = self.cmd_tx.send(Command::token(&id, &p.action, token));
        self.status_line = format!("attitude → {token}…");
        self.status_is_error = false;
    }

    /// Opens the attitude-mode picker, pre-selecting the vessel's current mode.
    fn open_attitude_picker(&mut self) {
        let Screen::Detail(ref id) = self.screen else {
            return;
        };
        let current = self
            .snapshot
            .vessels
            .iter()
            .find(|v| &v.id == id)
            .map(|v| v.attitude_mode.clone())
            .unwrap_or_default();
        let selected = ATTITUDE_MODES
            .iter()
            .position(|m| m.eq_ignore_ascii_case(&current))
            .unwrap_or(0);
        self.picker = Some(Picker {
            title: "Attitude mode",
            action: "vessel.attitude_mode".into(),
            options: ATTITUDE_MODES.to_vec(),
            selected,
            offset: 0,
            area: Rect::default(),
            item_rects: Vec::new(),
        });
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
        if self.picker.is_some() {
            match m.kind {
                MouseEventKind::ScrollUp => self.picker_move(-1),
                MouseEventKind::ScrollDown => self.picker_move(1),
                MouseEventKind::Down(MouseButton::Left) => self.picker_click(m.column, m.row),
                _ => {}
            }
            return;
        }
        match m.kind {
            MouseEventKind::ScrollUp => self.scroll(-1),
            MouseEventKind::ScrollDown => self.scroll(1),
            MouseEventKind::Down(MouseButton::Left) => self.on_click(m.column, m.row),
            _ => {}
        }
    }

    /// A click on an option selects + confirms it; a click outside the popup cancels.
    fn picker_click(&mut self, x: u16, y: u16) {
        let pos = Position { x, y };
        let (hit, inside) = match self.picker.as_ref() {
            Some(p) => (
                p.item_rects
                    .iter()
                    .find(|(r, _)| r.contains(pos))
                    .map(|&(_, idx)| idx),
                p.area.contains(pos),
            ),
            None => return,
        };
        if let Some(idx) = hit {
            if let Some(p) = self.picker.as_mut() {
                p.selected = idx;
            }
            self.picker_confirm();
        } else if !inside {
            self.picker = None;
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
                // A click anywhere along the throttle bar sets the throttle to that fraction.
                if self.throttle_bar.width > 0 && self.throttle_bar.contains(pos) {
                    let rel = (x - self.throttle_bar.x) as f64 + 0.5;
                    self.set_throttle(rel / self.throttle_bar.width as f64);
                    return;
                }
                if let Some(i) = self.controls.iter().position(|(r, _)| r.contains(pos)) {
                    self.focus = i;
                    let action = self.controls[i].1;
                    self.activate(action);
                }
            }
        }
    }

    // ---- command dispatch --------------------------------------------------------------------

    /// Sends a throttle setpoint (used by the clickable throttle bar; clamped 0..1).
    fn set_throttle(&mut self, fraction: f64) {
        let Screen::Detail(ref id) = self.screen else {
            return;
        };
        let frac = clamp01(fraction);
        let id = id.clone();
        let _ = self.cmd_tx.send(Command::vessel(&id, "vessel.throttle", frac));
        self.status_line = format!("throttle → {}%", (frac * 100.0).round() as i64);
        self.status_is_error = false;
    }

    fn activate(&mut self, action: Action) {
        match action {
            Action::Back => {
                self.screen = Screen::Dashboard;
                return;
            }
            Action::OpenAttitudePicker => {
                self.open_attitude_picker();
                return;
            }
            _ => {}
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
            // Handled before the vessel lookup above.
            Action::Back | Action::OpenAttitudePicker => return,
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
