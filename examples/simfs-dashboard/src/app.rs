//! Application state + input handling. The render pass (`ui.rs`) reads this state and writes back
//! the interactive hit-test rects (dashboard cards, control zones, toolbar buttons, modal lists)
//! the mouse handler reads on the next event — so keyboard and mouse drive the same actions, the
//! `dashboard-rs` discipline. The model is a flat ordered list of [`Widget`]s flowed into a grid;
//! the user searches `/sim` fields into it, rearranges, renames, and saves the layout to TOML.

use std::collections::HashMap;
use std::sync::mpsc::Sender;
use std::time::Duration;

use ratatui::crossterm::event::{KeyCode, KeyEvent, MouseButton, MouseEvent, MouseEventKind};
use ratatui::layout::{Position, Rect};
use serde::{Deserialize, Serialize};

use crate::catalog::{self, Candidate};
use crate::source::{CmdError, FromWorker, ToWorker};
use crate::widget::{parse_flag, parse_scalar, Kind, Widget};

/// The last-read state of one field (its value, or the errno of a failed read).
#[derive(Clone, Default)]
pub struct FieldState {
    pub value: Option<String>,
    pub error: Option<String>,
}

/// A recorded interactive sub-region of a control widget (set each render, hit-tested next event).
#[derive(Clone, Copy)]
pub struct Zone {
    pub rect: Rect,
    pub action: ZoneAction,
}

/// What clicking a [`Zone`] does. The `usize` is the widget index.
#[derive(Clone, Copy)]
pub enum ZoneAction {
    /// Set a throttle/fraction from the click column within the bar rect.
    SetFraction(usize),
    Toggle(usize),
    Trigger(usize),
    OpenPicker(usize),
    /// Step a writable number; the `i32` is the direction (+1 / -1).
    Step(usize, i32),
}

pub enum Modal {
    None,
    Search(SearchModal),
    Input(InputModal),
    Picker(PickerModal),
    Settings(SettingsModal),
}

/// The field-search popup: a query box over the catalog candidate list (+ an "add custom path" row
/// when the query looks like a path the catalog didn't surface — the escape hatch for HTTP-mode
/// indexed modules or exotic leaves).
pub struct SearchModal {
    pub query: String,
    pub all: Vec<Candidate>,
    pub filtered: Vec<usize>,
    pub selected: usize,
    pub offset: usize,
    pub loading: bool,
    pub area: Rect,
    pub input_rect: Rect,
    /// (rect, filtered index) — `usize::MAX` marks the "add custom path" row.
    pub item_rects: Vec<(Rect, usize)>,
}

pub const CUSTOM_ROW: usize = usize::MAX;

impl SearchModal {
    fn refilter(&mut self) {
        let q = self.query.to_lowercase();
        self.filtered = self
            .all
            .iter()
            .enumerate()
            .filter(|(_, c)| {
                q.is_empty()
                    || c.path.to_lowercase().contains(&q)
                    || c.title.to_lowercase().contains(&q)
            })
            .map(|(i, _)| i)
            .collect();
        if self.selected >= self.filtered.len() {
            self.selected = self.filtered.len().saturating_sub(1);
        }
        self.offset = 0;
    }

    /// Whether to offer adding the raw query as a path (it contains a `/` and isn't already listed).
    fn allow_custom(&self) -> bool {
        let q = self.query.trim();
        q.contains('/') && !self.all.iter().any(|c| c.path == q)
    }

    /// Total selectable rows (filtered candidates + the optional custom row).
    pub fn row_count(&self) -> usize {
        self.filtered.len() + usize::from(self.allow_custom())
    }
}

pub struct InputModal {
    pub purpose: InputPurpose,
    pub title: &'static str,
    pub text: String,
    pub area: Rect,
}

#[derive(Clone, Copy, PartialEq)]
pub enum InputPurpose {
    Save,
    Rename,
}

/// The enum-token picker (attitude mode/frame), bound to a writable widget.
pub struct PickerModal {
    pub widget: usize,
    pub title: String,
    pub options: Vec<String>,
    pub selected: usize,
    pub offset: usize,
    pub area: Rect,
    pub item_rects: Vec<(Rect, usize)>,
}

pub struct SettingsModal {
    pub area: Rect,
    pub cols_minus: Rect,
    pub cols_plus: Rect,
    pub opacity_track: Rect,
}

pub struct App {
    pub source_label: String,
    pub widgets: Vec<Widget>,
    pub values: HashMap<String, FieldState>,
    pub selected: usize,
    pub columns: u16,
    pub border_opacity: u8,
    pub interval: Duration,
    pub connected: bool,
    pub control_enabled: bool,
    pub debug_enabled: bool,
    pub status_line: String,
    pub status_is_error: bool,
    pub dirty: bool,
    pub current_file: Option<String>,
    pub modal: Modal,
    pub should_quit: bool,

    // Hit-test rects recorded each render.
    pub card_rects: Vec<(Rect, usize)>,
    pub zones: Vec<Zone>,
    pub tb_add: Rect,
    pub tb_save: Rect,
    pub tb_settings: Rect,

    cmd_tx: Sender<ToWorker>,
}

impl App {
    pub fn new(
        cmd_tx: Sender<ToWorker>,
        source_label: String,
        widgets: Vec<Widget>,
        columns: u16,
        border_opacity: u8,
        interval: Duration,
        current_file: Option<String>,
    ) -> Self {
        let app = Self {
            source_label,
            widgets,
            values: HashMap::new(),
            selected: 0,
            columns: columns.clamp(1, 8),
            border_opacity: border_opacity.min(100),
            interval,
            connected: false,
            control_enabled: false,
            debug_enabled: false,
            status_line: "connecting\u{2026}".into(),
            status_is_error: false,
            dirty: false,
            current_file,
            modal: Modal::None,
            should_quit: false,
            card_rects: Vec::new(),
            zones: Vec::new(),
            tb_add: Rect::default(),
            tb_save: Rect::default(),
            tb_settings: Rect::default(),
            cmd_tx,
        };
        app.resubscribe();
        app.request_refresh();
        app
    }

    // ---- worker plumbing ---------------------------------------------------------------------

    /// Tells the worker which fields to poll (the deduped, non-streaming widget paths).
    fn resubscribe(&self) {
        let mut paths: Vec<String> = Vec::new();
        for w in &self.widgets {
            if !catalog::path_is_streaming(&w.path) && !paths.contains(&w.path) {
                paths.push(w.path.clone());
            }
        }
        let _ = self.cmd_tx.send(ToWorker::Subscribe(paths));
    }

    fn request_refresh(&self) {
        let _ = self.cmd_tx.send(ToWorker::Refresh);
    }

    fn write_field(&mut self, path: String, value: String, note: String) {
        self.status_line = note.clone();
        self.status_is_error = false;
        let _ = self.cmd_tx.send(ToWorker::Write { path, value, note });
    }

    pub fn apply(&mut self, update: FromWorker) {
        match update {
            FromWorker::Values { values, connected } => {
                self.connected = connected;
                for (path, result) in values {
                    let entry = self.values.entry(path).or_default();
                    match result {
                        Ok(v) => {
                            entry.value = Some(v);
                            entry.error = None;
                        }
                        Err(e) => entry.error = Some(e),
                    }
                }
            }
            FromWorker::WriteDone { note, result } => match result {
                Ok(()) => {
                    self.status_line = format!("{note}: ok");
                    self.status_is_error = false;
                }
                Err(CmdError { errno, message }) => {
                    self.status_line = format!("{note}: {errno} ({message})");
                    self.status_is_error = true;
                }
            },
            FromWorker::Catalog { candidates, health } => {
                self.connected = health.connected;
                self.control_enabled = health.control;
                self.debug_enabled = health.debug;
                if let Modal::Search(s) = &mut self.modal {
                    s.all = candidates;
                    s.loading = false;
                    s.refilter();
                }
            }
        }
    }

    // ---- value access ------------------------------------------------------------------------

    pub fn value_of(&self, path: &str) -> Option<&str> {
        self.values.get(path).and_then(|s| s.value.as_deref())
    }

    pub fn error_of(&self, path: &str) -> Option<&str> {
        self.values.get(path).and_then(|s| s.error.as_deref())
    }

    fn scalar_of(&self, path: &str) -> f64 {
        self.value_of(path).and_then(parse_scalar).unwrap_or(0.0)
    }

    fn flag_of(&self, path: &str) -> bool {
        self.value_of(path).map(parse_flag).unwrap_or(false)
    }

    // ---- keyboard ----------------------------------------------------------------------------

    pub fn on_key(&mut self, key: KeyEvent) {
        match &mut self.modal {
            Modal::Search(_) => self.on_key_search(key),
            Modal::Input(_) => self.on_key_input(key),
            Modal::Picker(_) => self.on_key_picker(key),
            Modal::Settings(_) => self.on_key_settings(key),
            Modal::None => self.on_key_dashboard(key),
        }
    }

    fn on_key_dashboard(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Char('q') => self.should_quit = true,
            KeyCode::Up | KeyCode::Char('k') => self.move_selection(0, -1),
            KeyCode::Down | KeyCode::Char('j') => self.move_selection(0, 1),
            KeyCode::Left | KeyCode::Char('h') => self.move_selection(-1, 0),
            KeyCode::Right | KeyCode::Char('l') => self.move_selection(1, 0),
            KeyCode::Char('a') => self.open_search(),
            KeyCode::Char('w') => self.open_save(),
            KeyCode::Char('s') => self.open_settings(),
            KeyCode::Char('R') => self.open_rename(),
            KeyCode::Char('x') | KeyCode::Delete => self.remove_selected(),
            KeyCode::Char('[') => self.reorder(-1),
            KeyCode::Char(']') => self.reorder(1),
            KeyCode::Char('-') | KeyCode::Char('_') => self.nudge_selected(-1),
            KeyCode::Char('=') | KeyCode::Char('+') => self.nudge_selected(1),
            KeyCode::Enter | KeyCode::Char(' ') => self.activate_selected(),
            _ => {}
        }
    }

    fn on_key_search(&mut self, key: KeyEvent) {
        let Modal::Search(s) = &mut self.modal else {
            return;
        };
        match key.code {
            KeyCode::Esc => self.modal = Modal::None,
            KeyCode::Enter => self.search_confirm(),
            KeyCode::Up => self.search_move(-1),
            KeyCode::Down => self.search_move(1),
            KeyCode::Backspace => {
                s.query.pop();
                s.refilter();
            }
            KeyCode::Char(c) => {
                s.query.push(c);
                s.refilter();
            }
            _ => {}
        }
    }

    fn on_key_input(&mut self, key: KeyEvent) {
        let Modal::Input(m) = &mut self.modal else {
            return;
        };
        match key.code {
            KeyCode::Esc => self.modal = Modal::None,
            KeyCode::Enter => self.input_confirm(),
            KeyCode::Backspace => {
                m.text.pop();
            }
            KeyCode::Char(c) => m.text.push(c),
            _ => {}
        }
    }

    fn on_key_picker(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc | KeyCode::Char('q') => self.modal = Modal::None,
            KeyCode::Up | KeyCode::Char('k') => self.picker_move(-1),
            KeyCode::Down | KeyCode::Char('j') => self.picker_move(1),
            KeyCode::Enter | KeyCode::Char(' ') => self.picker_confirm(),
            _ => {}
        }
    }

    fn on_key_settings(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc | KeyCode::Enter | KeyCode::Char('q') | KeyCode::Char('s') => {
                self.modal = Modal::None
            }
            KeyCode::Left | KeyCode::Char('h') => self.adjust_opacity(-5),
            KeyCode::Right | KeyCode::Char('l') => self.adjust_opacity(5),
            KeyCode::Up | KeyCode::Char('k') => self.adjust_columns(1),
            KeyCode::Down | KeyCode::Char('j') => self.adjust_columns(-1),
            _ => {}
        }
    }

    // ---- mouse -------------------------------------------------------------------------------

    pub fn on_mouse(&mut self, m: MouseEvent) {
        match &mut self.modal {
            Modal::Search(_) => self.on_mouse_search(m),
            Modal::Picker(_) => self.on_mouse_picker(m),
            Modal::Settings(_) => self.on_mouse_settings(m),
            Modal::Input(_) => {
                // A click outside the input box dismisses it; otherwise ignore.
                if let MouseEventKind::Down(MouseButton::Left) = m.kind {
                    if let Modal::Input(im) = &self.modal {
                        if !im.area.contains(Position {
                            x: m.column,
                            y: m.row,
                        }) {
                            self.modal = Modal::None;
                        }
                    }
                }
            }
            Modal::None => self.on_mouse_dashboard(m),
        }
    }

    fn on_mouse_dashboard(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => self.move_selection(0, -1),
            MouseEventKind::ScrollDown => self.move_selection(0, 1),
            MouseEventKind::Down(MouseButton::Left) => self.on_click(m.column, m.row),
            _ => {}
        }
    }

    fn on_click(&mut self, x: u16, y: u16) {
        let pos = Position { x, y };
        if self.tb_add.contains(pos) {
            self.open_search();
            return;
        }
        if self.tb_save.contains(pos) {
            self.open_save();
            return;
        }
        if self.tb_settings.contains(pos) {
            self.open_settings();
            return;
        }
        if let Some(z) = self.zones.iter().copied().find(|z| z.rect.contains(pos)) {
            self.activate_zone(z, x);
            return;
        }
        if let Some(&(_, i)) = self.card_rects.iter().find(|(r, _)| r.contains(pos)) {
            self.selected = i;
        }
    }

    fn on_mouse_search(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => self.search_move(-1),
            MouseEventKind::ScrollDown => self.search_move(1),
            MouseEventKind::Down(MouseButton::Left) => {
                let pos = Position {
                    x: m.column,
                    y: m.row,
                };
                let Modal::Search(s) = &mut self.modal else {
                    return;
                };
                if let Some(&(_, row)) = s.item_rects.iter().find(|(r, _)| r.contains(pos)) {
                    if row == CUSTOM_ROW {
                        self.search_add_custom();
                    } else {
                        s.selected = row;
                        self.search_confirm();
                    }
                } else if !s.area.contains(pos) {
                    self.modal = Modal::None;
                }
            }
            _ => {}
        }
    }

    fn on_mouse_picker(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => self.picker_move(-1),
            MouseEventKind::ScrollDown => self.picker_move(1),
            MouseEventKind::Down(MouseButton::Left) => {
                let pos = Position {
                    x: m.column,
                    y: m.row,
                };
                let Modal::Picker(p) = &mut self.modal else {
                    return;
                };
                if let Some(&(_, idx)) = p.item_rects.iter().find(|(r, _)| r.contains(pos)) {
                    p.selected = idx;
                    self.picker_confirm();
                } else if !p.area.contains(pos) {
                    self.modal = Modal::None;
                }
            }
            _ => {}
        }
    }

    fn on_mouse_settings(&mut self, m: MouseEvent) {
        let pos = Position {
            x: m.column,
            y: m.row,
        };
        let Modal::Settings(st) = &self.modal else {
            return;
        };
        match m.kind {
            MouseEventKind::Down(MouseButton::Left) | MouseEventKind::Drag(MouseButton::Left) => {
                if st.opacity_track.width > 0 && st.opacity_track.contains(pos) {
                    let rel = x_rel(m.column, st.opacity_track);
                    self.border_opacity = (rel * 100.0).round() as u8;
                } else if st.cols_minus.contains(pos) {
                    self.adjust_columns(-1);
                } else if st.cols_plus.contains(pos) {
                    self.adjust_columns(1);
                } else if !st.area.contains(pos)
                    && matches!(m.kind, MouseEventKind::Down(MouseButton::Left))
                {
                    self.modal = Modal::None;
                }
            }
            MouseEventKind::ScrollUp => self.adjust_opacity(5),
            MouseEventKind::ScrollDown => self.adjust_opacity(-5),
            _ => {}
        }
    }

    // ---- selection / layout edits ------------------------------------------------------------

    fn move_selection(&mut self, dx: i32, dy: i32) {
        let n = self.widgets.len();
        if n == 0 {
            return;
        }
        let cols = self.columns.max(1) as i32;
        let cur = self.selected.min(n - 1) as i32;
        let mut next = cur;
        if dx != 0 {
            next = (cur + dx).rem_euclid(n as i32);
        }
        if dy != 0 {
            let candidate = cur + dy * cols;
            if candidate >= 0 && candidate < n as i32 {
                next = candidate;
            }
        }
        self.selected = next as usize;
    }

    fn reorder(&mut self, dir: i32) {
        let n = self.widgets.len();
        if n < 2 {
            return;
        }
        let i = self.selected.min(n - 1);
        let j = i as i32 + dir;
        if j < 0 || j >= n as i32 {
            return;
        }
        self.widgets.swap(i, j as usize);
        self.selected = j as usize;
        self.dirty = true;
    }

    fn remove_selected(&mut self) {
        if self.widgets.is_empty() {
            return;
        }
        let i = self.selected.min(self.widgets.len() - 1);
        let removed = self.widgets.remove(i);
        if self.selected >= self.widgets.len() {
            self.selected = self.widgets.len().saturating_sub(1);
        }
        self.dirty = true;
        self.resubscribe();
        self.status_line = format!("removed {}", removed.title);
        self.status_is_error = false;
    }

    fn selected_widget(&self) -> Option<&Widget> {
        self.widgets.get(self.selected)
    }

    // ---- activation (keyboard) ---------------------------------------------------------------

    fn activate_selected(&mut self) {
        let Some(w) = self.selected_widget() else {
            return;
        };
        let (path, kind) = (w.path.clone(), w.kind.clone());
        match kind {
            Kind::Toggle => self.toggle(&path),
            Kind::Trigger { verb, value } => self.trigger(&path, verb, value),
            Kind::Enum { options } => self.open_picker(self.selected, options),
            Kind::NumberCtl { step, unit } => self.step(&path, 1, step, unit),
            // Throttle/Fraction use -/= to nudge; read-only kinds do nothing.
            _ => {}
        }
    }

    fn nudge_selected(&mut self, dir: i32) {
        let Some(w) = self.selected_widget() else {
            return;
        };
        let (path, kind) = (w.path.clone(), w.kind.clone());
        match kind {
            Kind::Throttle | Kind::Fraction => {
                let cur = self.scalar_of(&path);
                let next = (cur + dir as f64 * 0.05).clamp(0.0, 1.0);
                self.write_field(path, fmt_num(next), format!("set \u{2192} {}", pct(next)));
            }
            Kind::NumberCtl { step, unit } => self.step(&path, dir, step, unit),
            _ => {}
        }
    }

    // ---- activation (zones) ------------------------------------------------------------------

    fn activate_zone(&mut self, z: Zone, click_x: u16) {
        match z.action {
            ZoneAction::SetFraction(i) => {
                let Some(w) = self.widgets.get(i) else {
                    return;
                };
                let path = w.path.clone();
                let frac = x_rel(click_x, z.rect).clamp(0.0, 1.0);
                self.write_field(path, fmt_num(frac), format!("set \u{2192} {}", pct(frac)));
            }
            ZoneAction::Toggle(i) => {
                if let Some(w) = self.widgets.get(i) {
                    let path = w.path.clone();
                    self.toggle(&path);
                }
            }
            ZoneAction::Trigger(i) => {
                if let Some(w) = self.widgets.get(i).cloned() {
                    if let Kind::Trigger { verb, value } = w.kind {
                        self.trigger(&w.path, verb, value);
                    }
                }
            }
            ZoneAction::OpenPicker(i) => {
                if let Some(Kind::Enum { options }) = self.widgets.get(i).map(|w| w.kind.clone()) {
                    self.open_picker(i, options);
                }
            }
            ZoneAction::Step(i, dir) => {
                if let Some(w) = self.widgets.get(i).cloned() {
                    if let Kind::NumberCtl { step, unit } = w.kind {
                        self.step(&w.path, dir, step, unit);
                    }
                }
            }
        }
    }

    fn toggle(&mut self, path: &str) {
        let next = if self.flag_of(path) { "0" } else { "1" };
        self.write_field(
            path.to_string(),
            next.to_string(),
            format!("{path} \u{2192} {next}"),
        );
    }

    fn trigger(&mut self, path: &str, verb: &str, value: &str) {
        self.write_field(path.to_string(), value.to_string(), verb.to_string());
    }

    fn step(&mut self, path: &str, dir: i32, step: f64, unit: &str) {
        let cur = self.scalar_of(path);
        let next = if unit == "x" {
            // Multiplicative for warp-like factors; never below 1.
            if dir > 0 { cur * step } else { cur / step }.max(1.0)
        } else {
            (cur + dir as f64 * step).max(0.0)
        };
        self.write_field(
            path.to_string(),
            fmt_num(next),
            format!("set \u{2192} {}", fmt_num(next)),
        );
    }

    // ---- modals: open ------------------------------------------------------------------------

    fn open_search(&mut self) {
        self.modal = Modal::Search(SearchModal {
            query: String::new(),
            all: Vec::new(),
            filtered: Vec::new(),
            selected: 0,
            offset: 0,
            loading: true,
            area: Rect::default(),
            input_rect: Rect::default(),
            item_rects: Vec::new(),
        });
        self.request_refresh();
    }

    fn open_save(&mut self) {
        let text = self
            .current_file
            .clone()
            .unwrap_or_else(|| "dashboard.toml".to_string());
        self.modal = Modal::Input(InputModal {
            purpose: InputPurpose::Save,
            title: "Save layout as",
            text,
            area: Rect::default(),
        });
    }

    fn open_rename(&mut self) {
        let Some(w) = self.selected_widget() else {
            return;
        };
        self.modal = Modal::Input(InputModal {
            purpose: InputPurpose::Rename,
            title: "Rename widget",
            text: w.title.clone(),
            area: Rect::default(),
        });
    }

    fn open_settings(&mut self) {
        self.modal = Modal::Settings(SettingsModal {
            area: Rect::default(),
            cols_minus: Rect::default(),
            cols_plus: Rect::default(),
            opacity_track: Rect::default(),
        });
    }

    fn open_picker(&mut self, widget: usize, options: &'static [&'static str]) {
        let current = self
            .widgets
            .get(widget)
            .and_then(|w| self.value_of(&w.path))
            .unwrap_or("")
            .to_string();
        let selected = options
            .iter()
            .position(|o| o.eq_ignore_ascii_case(current.trim()))
            .unwrap_or(0);
        let title = self
            .widgets
            .get(widget)
            .map(|w| w.title.clone())
            .unwrap_or_default();
        self.modal = Modal::Picker(PickerModal {
            widget,
            title,
            options: options.iter().map(|s| s.to_string()).collect(),
            selected,
            offset: 0,
            area: Rect::default(),
            item_rects: Vec::new(),
        });
    }

    // ---- modals: actions ---------------------------------------------------------------------

    fn search_move(&mut self, delta: i32) {
        if let Modal::Search(s) = &mut self.modal {
            let n = s.row_count();
            if n == 0 {
                return;
            }
            s.selected = (s.selected as i32 + delta).rem_euclid(n as i32) as usize;
        }
    }

    fn search_confirm(&mut self) {
        let Modal::Search(s) = &mut self.modal else {
            return;
        };
        // The custom row sits just past the filtered candidates.
        if s.allow_custom() && s.selected == s.filtered.len() {
            self.search_add_custom();
            return;
        }
        let Some(&cand_idx) = s.filtered.get(s.selected) else {
            return;
        };
        let path = s.all[cand_idx].path.clone();
        self.add_widget(Widget::from_path(path));
    }

    fn search_add_custom(&mut self) {
        let Modal::Search(s) = &self.modal else {
            return;
        };
        let path = s.query.trim().to_string();
        if path.is_empty() {
            return;
        }
        self.add_widget(Widget::from_path(path));
    }

    fn add_widget(&mut self, w: Widget) {
        self.status_line = format!("added {}", w.path);
        self.status_is_error = false;
        self.widgets.push(w);
        self.selected = self.widgets.len() - 1;
        self.dirty = true;
        self.modal = Modal::None;
        self.resubscribe();
    }

    fn input_confirm(&mut self) {
        let Modal::Input(m) = &self.modal else {
            return;
        };
        match m.purpose {
            InputPurpose::Save => {
                let name = m.text.trim().to_string();
                self.modal = Modal::None;
                self.save_to(&name);
            }
            InputPurpose::Rename => {
                let title = m.text.clone();
                self.modal = Modal::None;
                if let Some(w) = self.widgets.get_mut(self.selected) {
                    w.title = title;
                    self.dirty = true;
                }
            }
        }
    }

    fn picker_move(&mut self, delta: i32) {
        if let Modal::Picker(p) = &mut self.modal {
            let n = p.options.len();
            if n == 0 {
                return;
            }
            p.selected = (p.selected as i32 + delta).rem_euclid(n as i32) as usize;
        }
    }

    fn picker_confirm(&mut self) {
        let Modal::Picker(p) = &self.modal else {
            return;
        };
        let Some(w) = self.widgets.get(p.widget) else {
            self.modal = Modal::None;
            return;
        };
        let path = w.path.clone();
        let token = p.options[p.selected].clone();
        self.modal = Modal::None;
        self.write_field(path, token.clone(), format!("\u{2192} {token}"));
    }

    fn adjust_opacity(&mut self, delta: i32) {
        self.border_opacity = (self.border_opacity as i32 + delta).clamp(0, 100) as u8;
    }

    fn adjust_columns(&mut self, delta: i32) {
        self.columns = (self.columns as i32 + delta).clamp(1, 8) as u16;
        self.dirty = true;
    }

    // ---- persistence -------------------------------------------------------------------------

    fn save_to(&mut self, name: &str) {
        if name.is_empty() {
            self.status_line = "save cancelled (no filename)".into();
            self.status_is_error = true;
            return;
        }
        let name = if name.contains('.') {
            name.to_string()
        } else {
            format!("{name}.toml")
        };
        let layout = Layout {
            columns: self.columns,
            border_opacity: self.border_opacity,
            interval_ms: Some(self.interval.as_millis() as u64),
            widgets: self
                .widgets
                .iter()
                .map(|w| WidgetCfg {
                    title: w.title.clone(),
                    path: w.path.clone(),
                })
                .collect(),
        };
        match toml::to_string_pretty(&layout) {
            Ok(text) => match std::fs::write(&name, text) {
                Ok(()) => {
                    self.current_file = Some(name.clone());
                    self.dirty = false;
                    self.status_line = format!("saved {name}");
                    self.status_is_error = false;
                }
                Err(e) => {
                    self.status_line = format!("save failed: {e}");
                    self.status_is_error = true;
                }
            },
            Err(e) => {
                self.status_line = format!("serialize failed: {e}");
                self.status_is_error = true;
            }
        }
    }
}

// ---- layout file (TOML) -------------------------------------------------------------------------

/// The persisted dashboard. Widgets are stored as `{title, path}` only — the widget [`Kind`] is
/// re-derived from the path on load (see [`Widget::from_path`]), so the file stays small and a
/// hand-edited path always gets a sensible widget.
#[derive(Serialize, Deserialize)]
pub struct Layout {
    #[serde(default = "default_columns")]
    pub columns: u16,
    #[serde(default = "default_opacity")]
    pub border_opacity: u8,
    #[serde(default)]
    pub interval_ms: Option<u64>,
    #[serde(default, rename = "widget")]
    pub widgets: Vec<WidgetCfg>,
}

#[derive(Serialize, Deserialize)]
pub struct WidgetCfg {
    pub title: String,
    pub path: String,
}

fn default_columns() -> u16 {
    3
}

fn default_opacity() -> u8 {
    100
}

impl Layout {
    /// Loads a layout from a TOML file.
    pub fn load(path: &str) -> anyhow::Result<Self> {
        let text = std::fs::read_to_string(path)?;
        Ok(toml::from_str(&text)?)
    }

    /// Materializes the persisted widgets (re-deriving each kind from its path).
    pub fn to_widgets(&self) -> Vec<Widget> {
        self.widgets
            .iter()
            .map(|w| Widget::titled(&w.path, &w.title))
            .collect()
    }
}

// ---- small helpers ------------------------------------------------------------------------------

/// The 0..1 position of column `x` within `rect` (centered on the cell).
fn x_rel(x: u16, rect: Rect) -> f64 {
    if rect.width == 0 {
        return 0.0;
    }
    let rel = x.saturating_sub(rect.x) as f64 + 0.5;
    (rel / rect.width as f64).clamp(0.0, 1.0)
}

/// Formats a number for writing to a field: shortest round-tripping decimal, no trailing `.0`.
fn fmt_num(v: f64) -> String {
    let s = format!("{v}");
    s
}

fn pct(frac: f64) -> String {
    format!("{}%", (frac.clamp(0.0, 1.0) * 100.0).round() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::widget::Kind;

    #[test]
    fn layout_round_trips_through_toml() {
        let layout = Layout {
            columns: 4,
            border_opacity: 60,
            interval_ms: Some(120),
            widgets: vec![
                WidgetCfg {
                    title: "My throttle".into(),
                    path: "vessels/by-id/A/ctl/throttle".into(),
                },
                WidgetCfg {
                    title: "Radar".into(),
                    path: "vessels/by-id/A/altitude/radar".into(),
                },
            ],
        };
        let text = toml::to_string_pretty(&layout).unwrap();
        let back: Layout = toml::from_str(&text).unwrap();
        assert_eq!(back.columns, 4);
        assert_eq!(back.border_opacity, 60);
        assert_eq!(back.interval_ms, Some(120));

        // Widgets persist only {title, path}; the kind is re-derived from the path on load.
        let widgets = back.to_widgets();
        assert_eq!(widgets.len(), 2);
        assert_eq!(widgets[0].title, "My throttle");
        assert!(matches!(widgets[0].kind, Kind::Throttle));
        assert!(matches!(widgets[1].kind, Kind::Number { unit: "m" }));
    }

    #[test]
    fn minimal_layout_uses_defaults() {
        // A file with only widgets still loads (serde defaults fill the rest).
        let back: Layout = toml::from_str("[[widget]]\ntitle='T'\npath='time/warp'\n").unwrap();
        assert_eq!(back.columns, 3);
        assert_eq!(back.border_opacity, 100);
        assert_eq!(back.widgets.len(), 1);
    }
}
