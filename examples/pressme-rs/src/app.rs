//! Application state + input handling. The panel is one column of full-width buttons; the height
//! splits evenly across them. A keyboard cursor selects a button; pressing it (or clicking it)
//! spawns its command off-thread. Each button's live run state is kept here; the rects the renderer
//! lays out are recorded back onto [`App`] so mouse hits test against the last-drawn layout.

use std::sync::mpsc::Sender;
use std::time::Instant;

use ratatui::crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::layout::{Position, Rect};

use crate::config;
use crate::runner::{self, Outcome, RunMsg};

/// One button's run lifecycle.
#[derive(Clone, Debug, PartialEq)]
pub enum RunState {
    /// Never pressed (or reset).
    Idle,
    /// Command in flight since the instant (drives the spinner).
    Running(Instant),
    /// Last run finished with this outcome.
    Done(Outcome),
}

/// A button: its immutable config plus its live run state.
pub struct ButtonModel {
    pub label: String,
    pub color: ratatui::style::Color,
    pub command: String,
    pub state: RunState,
}

pub struct App {
    pub buttons: Vec<ButtonModel>,
    pub selected: usize,
    pub should_quit: bool,
    tx: Sender<RunMsg>,
    /// The button rects from the last render, for mouse hit-testing.
    pub rects: Vec<Rect>,
}

impl App {
    pub fn new(buttons: Vec<config::Button>, tx: Sender<RunMsg>) -> Self {
        let buttons = buttons
            .into_iter()
            .map(|b| ButtonModel {
                label: b.label,
                color: b.color,
                command: b.command,
                state: RunState::Idle,
            })
            .collect();
        Self {
            buttons,
            selected: 0,
            should_quit: false,
            tx,
            rects: Vec::new(),
        }
    }

    // ---- run-thread updates ------------------------------------------------------------------

    pub fn apply(&mut self, msg: RunMsg) {
        match msg {
            RunMsg::Finished { idx, outcome } => {
                if let Some(b) = self.buttons.get_mut(idx) {
                    b.state = RunState::Done(outcome);
                }
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
            KeyCode::Up | KeyCode::Char('k') => self.move_selection(-1),
            KeyCode::Down | KeyCode::Char('j') => self.move_selection(1),
            KeyCode::Home => self.selected = 0,
            KeyCode::End => self.selected = self.buttons.len().saturating_sub(1),
            KeyCode::Enter | KeyCode::Char(' ') => self.press(self.selected),
            // Number keys 1..9 press that button directly.
            KeyCode::Char(c @ '1'..='9') => {
                let idx = c as usize - '1' as usize;
                if idx < self.buttons.len() {
                    self.selected = idx;
                    self.press(idx);
                }
            }
            _ => {}
        }
    }

    fn move_selection(&mut self, delta: isize) {
        let n = self.buttons.len() as isize;
        if n == 0 {
            return;
        }
        self.selected = (self.selected as isize + delta).rem_euclid(n) as usize;
    }

    // ---- mouse -------------------------------------------------------------------------------

    pub fn on_mouse(&mut self, m: MouseEvent) {
        let pos = Position {
            x: m.column,
            y: m.row,
        };
        match m.kind {
            // Hovering moves the cursor; clicking presses the button under the pointer.
            MouseEventKind::Moved => {
                if let Some(i) = self.hit(pos) {
                    self.selected = i;
                }
            }
            MouseEventKind::Down(MouseButton::Left) => {
                if let Some(i) = self.hit(pos) {
                    self.selected = i;
                    self.press(i);
                }
            }
            _ => {}
        }
    }

    /// The button index whose last-rendered rect contains `pos`, if any.
    fn hit(&self, pos: Position) -> Option<usize> {
        self.rects.iter().position(|r| r.contains(pos))
    }

    // ---- pressing ----------------------------------------------------------------------------

    /// Presses button `idx`: if it isn't already running, mark it running and spawn its command.
    fn press(&mut self, idx: usize) {
        let Some(b) = self.buttons.get_mut(idx) else {
            return;
        };
        if matches!(b.state, RunState::Running(_)) {
            return; // one run at a time per button
        }
        b.state = RunState::Running(Instant::now());
        runner::spawn(idx, b.command.clone(), self.tx.clone());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ratatui::style::Color;
    use std::sync::mpsc;

    fn app() -> (App, mpsc::Receiver<RunMsg>) {
        let (tx, rx) = mpsc::channel();
        let buttons = vec![
            config::Button {
                label: "A".into(),
                color: Color::Rgb(1, 2, 3),
                command: "true".into(),
            },
            config::Button {
                label: "B".into(),
                color: Color::Rgb(4, 5, 6),
                command: "true".into(),
            },
        ];
        (App::new(buttons, tx), rx)
    }

    #[test]
    fn selection_wraps_both_ways() {
        let (mut a, _rx) = app();
        assert_eq!(a.selected, 0);
        a.move_selection(-1);
        assert_eq!(a.selected, 1); // wrapped up from the top
        a.move_selection(1);
        assert_eq!(a.selected, 0); // wrapped back down
    }

    #[test]
    fn press_marks_running_and_ignores_re_press() {
        let (mut a, _rx) = app();
        a.press(0);
        assert!(matches!(a.buttons[0].state, RunState::Running(_)));
        let before = a.buttons[0].state.clone();
        a.press(0); // already running → no change, no second spawn
        assert_eq!(a.buttons[0].state, before);
    }

    #[test]
    fn finished_message_records_the_outcome() {
        let (mut a, _rx) = app();
        a.apply(RunMsg::Finished {
            idx: 1,
            outcome: Outcome {
                ok: true,
                code: Some(0),
                summary: "done".into(),
            },
        });
        assert!(matches!(&a.buttons[1].state, RunState::Done(o) if o.summary == "done"));
    }

    #[test]
    fn click_hit_tests_against_recorded_rects() {
        let (mut a, _rx) = app();
        a.rects = vec![Rect::new(0, 0, 10, 3), Rect::new(0, 3, 10, 3)];
        assert_eq!(a.hit(Position { x: 5, y: 4 }), Some(1));
        assert_eq!(a.hit(Position { x: 5, y: 99 }), None);
    }
}
