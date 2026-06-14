//! The application state + input handling. The panel has exactly two interactive widgets — a throttle
//! slider and an ignite/shutdown toggle — and one job: drive the active vessel. Geometry (the bar and
//! button rects, and which way the bar runs) is recorded by [`crate::ui`] each frame so mouse hits can
//! be tested against the last-rendered layout; the orientation is chosen there from the terminal size.

use std::sync::mpsc::Sender;

use ratatui::crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::layout::{Position, Rect};

use crate::source::{FromWorker, ToWorker};

/// The panel orientation. `Auto` picks vertical vs horizontal from the terminal aspect every frame,
/// so the layout flips live on resize.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Orient {
    Auto,
    Vertical,
    Horizontal,
}

impl Orient {
    /// Resolves to "is the bar vertical?" for the given inner area. Auto goes vertical when the
    /// window is portrait-ish: terminal cells are about twice as tall as wide, so `width < height*2`
    /// is the visually-square cutoff (a normal 80×24 lands wide → horizontal; a tall 30×40 → vertical).
    pub fn is_vertical(self, area: Rect) -> bool {
        match self {
            Orient::Vertical => true,
            Orient::Horizontal => false,
            Orient::Auto => area.width < area.height.saturating_mul(2),
        }
    }
}

pub struct App {
    tx: Sender<ToWorker>,
    pub should_quit: bool,

    // ---- live state from the worker ----
    /// Source reachable (drives the title color + the "not connected" status).
    pub connected: bool,
    /// A valid active vessel exists. The single gate that enables/disables the controls.
    pub active: bool,
    /// The displayed throttle 0..1. Synced from the sim each poll, except while dragging (the user's
    /// value wins so a stale read can't snap the slider back mid-drag).
    pub throttle: f64,
    /// The ignite/shutdown toggle state — the **live game state** read from `ctl/engine` every poll
    /// (KSA `EngineOn`). Never internal: a click writes the flip, and the button only changes once
    /// the next poll reads the new state back.
    engine_on: bool,

    // ---- interaction ----
    dragging: bool,
    last_sent_pct: Option<i32>,
    pub status: String,
    pub status_err: bool,
    pub label: String,
    pub orientation: Orient,

    // ---- geometry recorded by the renderer (for mouse hit-testing) ----
    pub bar_rect: Rect,
    pub bar_vertical: bool,
    pub button_rect: Rect,
}

impl App {
    pub fn new(tx: Sender<ToWorker>, label: String, orientation: Orient) -> Self {
        Self {
            tx,
            should_quit: false,
            connected: false,
            active: false,
            throttle: 0.0,
            engine_on: false,
            dragging: false,
            last_sent_pct: None,
            status: "connecting\u{2026}".to_string(),
            status_err: false,
            label,
            orientation,
            bar_rect: Rect::ZERO,
            bar_vertical: true,
            button_rect: Rect::ZERO,
        }
    }

    /// The toggle's displayed state — the live `ctl/engine` read (off when unreadable).
    pub fn ignited(&self) -> bool {
        self.engine_on
    }

    // ---- worker updates ----------------------------------------------------------------------

    pub fn apply(&mut self, msg: FromWorker) {
        match msg {
            FromWorker::Poll(p) => {
                self.connected = p.connected;
                self.active = p.active;
                self.engine_on = p.engine_on.unwrap_or(false);
                if !self.dragging {
                    if let Some(t) = p.throttle {
                        self.throttle = t;
                    }
                }
                if !self.active {
                    self.status = if self.connected {
                        "no active vessel".to_string()
                    } else {
                        format!("not connected \u{b7} {}", self.label)
                    };
                    self.status_err = false;
                } else if self.status.starts_with("no active")
                    || self.status.starts_with("not connected")
                    || self.status.starts_with("connecting")
                {
                    self.status = "ready".to_string();
                    self.status_err = false;
                }
            }
            FromWorker::Write { message, is_error } => {
                self.status = message;
                self.status_err = is_error;
            }
        }
    }

    // ---- keyboard ----------------------------------------------------------------------------

    pub fn on_key(&mut self, k: KeyEvent) {
        match k.code {
            KeyCode::Char('q') | KeyCode::Esc => self.should_quit = true,
            KeyCode::Char('c') if k.modifiers.contains(KeyModifiers::CONTROL) => {
                self.should_quit = true
            }
            KeyCode::Char(' ') | KeyCode::Enter => self.toggle(),
            KeyCode::Up | KeyCode::Char('=') | KeyCode::Char('+') => self.nudge(0.05),
            KeyCode::Down | KeyCode::Char('-') | KeyCode::Char('_') => self.nudge(-0.05),
            KeyCode::Char('0') => self.commit_throttle(0.0, false),
            KeyCode::Char('g') => self.commit_throttle(1.0, false), // gogogo!
            _ => {}
        }
    }

    // ---- mouse -------------------------------------------------------------------------------

    pub fn on_mouse(&mut self, m: MouseEvent) {
        let pos = Position {
            x: m.column,
            y: m.row,
        };
        match m.kind {
            MouseEventKind::Down(MouseButton::Left) => {
                if self.active && self.bar_rect.contains(pos) {
                    // Press on the bar arms click-and-drag, so holding and sweeping scrubs it live.
                    self.dragging = true;
                    self.last_sent_pct = None;
                    let v = self.throttle_at(m.column, m.row);
                    self.commit_throttle(v, true);
                } else if self.active && self.button_rect.contains(pos) {
                    self.toggle();
                }
            }
            MouseEventKind::Drag(MouseButton::Left) => {
                if self.dragging && self.active {
                    let v = self.throttle_at(m.column, m.row);
                    self.commit_throttle(v, true);
                }
            }
            MouseEventKind::Up(MouseButton::Left) => {
                self.dragging = false;
                self.last_sent_pct = None;
            }
            MouseEventKind::ScrollUp => self.nudge(0.05),
            MouseEventKind::ScrollDown => self.nudge(-0.05),
            _ => {}
        }
    }

    /// The throttle (0..1) a click/drag at column `x`, row `y` maps to along the bar — horizontal bars
    /// scrub by column, vertical bars by row (top = 100 %), each cell centered on its half-step.
    pub fn throttle_at(&self, x: u16, y: u16) -> f64 {
        let r = self.bar_rect;
        if self.bar_vertical {
            if r.height == 0 {
                return self.throttle;
            }
            let rel = (y as f64 + 0.5 - r.y as f64) / r.height as f64;
            (1.0 - rel).clamp(0.0, 1.0)
        } else {
            if r.width == 0 {
                return self.throttle;
            }
            let rel = (x as f64 + 0.5 - r.x as f64) / r.width as f64;
            rel.clamp(0.0, 1.0)
        }
    }

    // ---- commands ----------------------------------------------------------------------------

    fn toggle(&mut self) {
        if !self.active {
            return;
        }
        // Command the opposite of the live game state; the button flips when the next poll reads it
        // back (never optimistically from internal state).
        let ignite = !self.engine_on;
        let _ = self.tx.send(ToWorker::Engine(ignite));
        self.set_status(if ignite { "ignite" } else { "shutdown" }, false);
    }

    fn nudge(&mut self, delta: f64) {
        self.commit_throttle(self.throttle + delta, false);
    }

    /// Sets the displayed throttle and sends the setpoint. `dedupe` (the drag path) skips the write
    /// when the rounded percent hasn't changed, so a held sweep sends one command per 1 % crossed,
    /// not one per mouse event.
    fn commit_throttle(&mut self, value: f64, dedupe: bool) {
        if !self.active {
            return;
        }
        let value = value.clamp(0.0, 1.0);
        self.throttle = value;
        let pct = (value * 100.0).round() as i32;
        if dedupe && self.last_sent_pct == Some(pct) {
            return;
        }
        self.last_sent_pct = Some(pct);
        let _ = self.tx.send(ToWorker::Throttle(value));
        self.set_status(&format!("throttle \u{2192} {pct}%"), false);
    }

    fn set_status(&mut self, message: &str, is_error: bool) {
        self.status = message.to_string();
        self.status_err = is_error;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;

    fn app() -> App {
        let (tx, _rx) = mpsc::channel();
        App::new(tx, "fs:/sim".to_string(), Orient::Auto)
    }

    #[test]
    fn auto_orientation_follows_aspect() {
        // A normal landscape terminal is horizontal; a portrait one is vertical.
        assert!(!Orient::Auto.is_vertical(Rect::new(0, 0, 80, 24)));
        assert!(Orient::Auto.is_vertical(Rect::new(0, 0, 30, 40)));
        // Overrides win regardless of size.
        assert!(Orient::Vertical.is_vertical(Rect::new(0, 0, 80, 24)));
        assert!(!Orient::Horizontal.is_vertical(Rect::new(0, 0, 30, 40)));
    }

    #[test]
    fn throttle_at_maps_both_orientations() {
        let mut a = app();
        // Horizontal bar at x=0..10: left edge ~0, right edge ~1, middle ~0.5.
        a.bar_vertical = false;
        a.bar_rect = Rect::new(0, 0, 10, 1);
        assert!(a.throttle_at(0, 0) < 0.1);
        assert!(a.throttle_at(9, 0) > 0.9);
        assert!((a.throttle_at(5, 0) - 0.55).abs() < 0.1);
        // Vertical bar at y=0..10: top ~1, bottom ~0.
        a.bar_vertical = true;
        a.bar_rect = Rect::new(0, 0, 1, 10);
        assert!(a.throttle_at(0, 0) > 0.9);
        assert!(a.throttle_at(0, 9) < 0.1);
    }

    #[test]
    fn controls_are_inert_without_an_active_vessel() {
        let mut a = app();
        a.active = false;
        a.bar_vertical = false;
        a.bar_rect = Rect::new(0, 0, 10, 1);
        a.commit_throttle(0.8, false);
        assert_eq!(a.throttle, 0.0); // no write, no display change
        a.toggle();
        assert!(!a.ignited()); // no command issued, state unchanged
    }

    #[test]
    fn toggle_reflects_game_state_not_internal() {
        let mut a = app();
        a.active = true;
        // The button mirrors the live ctl/engine read — a click does NOT flip it locally.
        a.toggle();
        assert!(!a.ignited(), "stays off until the game confirms ignition");
        // The game reports the engines lit → the button shows lit.
        a.apply(FromWorker::Poll(crate::source::Poll {
            connected: true,
            active: true,
            throttle: Some(0.3),
            engine_on: Some(true),
        }));
        assert!(a.ignited());
        assert_eq!(a.throttle, 0.3); // throttle also synced from the sim
        // The game reports it shut down again → the button follows.
        a.apply(FromWorker::Poll(crate::source::Poll {
            connected: true,
            active: true,
            throttle: Some(0.3),
            engine_on: Some(false),
        }));
        assert!(!a.ignited());
    }
}
