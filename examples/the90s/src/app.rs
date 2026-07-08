//! The application state + input handling. Two screens over the same sound list — a keyboard-nav
//! **list** and a full-screen **soundboard** grid — plus the two controls visible on both: the
//! left-edge volume slider and the red **OMG STOP** button. Geometry (the slider/button/row/cell
//! rects and the grid's column count) is recorded by [`crate::ui`] each frame so mouse hits can be
//! tested against the last-rendered layout.

use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};

use ratatui::crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::layout::{Position, Rect};

use crate::source::{FromWorker, RegOutcome, ToWorker};

/// How long a just-pressed button stays visually "lit" — a couple of render ticks, enough for the
/// 90s-flash-app *thunk* without any timer machinery.
pub const FLASH: Duration = Duration::from_millis(200);

/// Which screen is showing. `Tab` toggles; `1`/`2` jump.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Screen {
    List,
    Board,
}

/// One sound's registration state. Sounds are inert until `Ready` (the clip is in the store).
#[derive(Clone, PartialEq, Debug)]
pub enum SoundState {
    Registering,
    Ready,
    Error(String),
}

/// One sound on the board: config identity + live state.
pub struct SoundUi {
    /// The display name (the YAML key).
    pub name: String,
    /// The `/sim/audio/file/` store name it registered under.
    pub clip: String,
    pub state: SoundState,
    /// Live channels of this clip right now (from the last `audio/status` poll).
    pub count: u32,
    /// When the sound was last pressed — drives the short button flash.
    pub flash: Option<Instant>,
}

impl SoundUi {
    pub fn is_flashing(&self) -> bool {
        self.flash.is_some_and(|t| t.elapsed() < FLASH)
    }
}

pub struct App {
    tx: Sender<ToWorker>,
    pub should_quit: bool,
    pub screen: Screen,

    pub title: String,
    pub label: String,
    pub sounds: Vec<SoundUi>,
    pub selected: usize,

    /// The soundboard volume 0..1 — applied to new plays and live-adjusted onto playing clips.
    pub volume: f64,
    dragging: bool,
    last_sent_pct: Option<i32>,

    // ---- live state from the worker ----
    pub connected: bool,
    pub audio_ok: bool,
    /// Total live channels (any client's) — shown on the OMG STOP button.
    pub total_active: u32,
    reg_pending: usize,
    reg_failed: usize,

    pub status: String,
    pub status_err: bool,

    /// First visible list row (the renderer scrolls it to keep the selection visible).
    pub list_offset: usize,

    // ---- geometry recorded by the renderer (for mouse hit-testing) ----
    pub slider_rect: Rect,
    pub stop_all_rect: Rect,
    /// `(sound index, rect)` for each visible row (list) or cell (board).
    pub hit_rects: Vec<(usize, Rect)>,
    /// `(sound index, rect)` for each visible per-row `[■]` stop button (list screen only).
    pub stop_rects: Vec<(usize, Rect)>,
    /// The board grid's column count — keyboard up/down move by this stride.
    pub grid_cols: usize,
}

impl App {
    pub fn new(
        tx: Sender<ToWorker>,
        title: String,
        label: String,
        sounds: Vec<(String, String)>, // (display name, clip)
        screen: Screen,
        volume: f64,
    ) -> Self {
        let reg_pending = sounds.len();
        Self {
            tx,
            should_quit: false,
            screen,
            title,
            label,
            sounds: sounds
                .into_iter()
                .map(|(name, clip)| SoundUi {
                    name,
                    clip,
                    state: SoundState::Registering,
                    count: 0,
                    flash: None,
                })
                .collect(),
            selected: 0,
            volume: volume.clamp(0.0, 1.0),
            dragging: false,
            last_sent_pct: None,
            connected: false,
            audio_ok: false,
            total_active: 0,
            reg_pending,
            reg_failed: 0,
            status: "connecting\u{2026}".to_string(),
            status_err: false,
            list_offset: 0,
            slider_rect: Rect::ZERO,
            stop_all_rect: Rect::ZERO,
            hit_rects: Vec::new(),
            stop_rects: Vec::new(),
            grid_cols: 1,
        }
    }

    // ---- worker updates ----------------------------------------------------------------------

    pub fn apply(&mut self, msg: FromWorker) {
        match msg {
            FromWorker::Poll(p) => {
                self.connected = p.connected;
                self.audio_ok = p.audio_ok;
                self.total_active = p.total;
                for (sound, count) in self.sounds.iter_mut().zip(p.counts) {
                    sound.count = count;
                }
                if !self.connected {
                    self.set_status(&format!("not connected \u{b7} {}", self.label), false);
                } else if !self.audio_ok {
                    self.set_status("audio surface missing \u{2014} is [audio] audio_enabled on?", true);
                } else if self.status_is_transitional() {
                    self.set_status(&self.ready_status(), self.reg_failed > 0);
                }
            }
            FromWorker::Registered { idx, result } => {
                self.reg_pending = self.reg_pending.saturating_sub(1);
                let Some(sound) = self.sounds.get_mut(idx) else {
                    return;
                };
                match result {
                    Ok(RegOutcome::Uploaded) | Ok(RegOutcome::Cached) => {
                        sound.state = SoundState::Ready;
                    }
                    Err(e) => {
                        let msg = format!("{}: {e}", sound.name);
                        sound.state = SoundState::Error(e);
                        self.reg_failed += 1;
                        self.set_status(&msg, true);
                        return;
                    }
                }
                if self.status_is_transitional() {
                    self.set_status(&self.ready_status(), self.reg_failed > 0);
                }
            }
            FromWorker::Write { message, is_error } => {
                self.set_status(&message, is_error);
            }
        }
    }

    /// The status line is showing lifecycle chatter (safe to overwrite with fresher lifecycle
    /// state) rather than a recent play/stop/error message.
    fn status_is_transitional(&self) -> bool {
        self.status.starts_with("connecting")
            || self.status.starts_with("not connected")
            || self.status.starts_with("audio surface")
            || self.status.starts_with("registering")
            || self.status.starts_with("ready")
    }

    fn ready_status(&self) -> String {
        if self.reg_pending > 0 {
            format!(
                "registering sounds\u{2026} {}/{}",
                self.sounds.len() - self.reg_pending,
                self.sounds.len()
            )
        } else if self.reg_failed > 0 {
            format!("ready \u{b7} {} sound(s) failed to register", self.reg_failed)
        } else {
            "ready".to_string()
        }
    }

    // ---- keyboard ------------------------------------------------------------------------------

    pub fn on_key(&mut self, k: KeyEvent) {
        match k.code {
            KeyCode::Char('q') => self.should_quit = true,
            KeyCode::Char('c') if k.modifiers.contains(KeyModifiers::CONTROL) => {
                self.should_quit = true
            }
            // Esc backs out of the board; from the list it quits.
            KeyCode::Esc => match self.screen {
                Screen::Board => self.screen = Screen::List,
                Screen::List => self.should_quit = true,
            },
            KeyCode::Tab | KeyCode::Char('f') => self.toggle_screen(),
            KeyCode::Char('1') => self.screen = Screen::List,
            KeyCode::Char('2') => self.screen = Screen::Board,
            KeyCode::Enter | KeyCode::Char(' ') | KeyCode::Char('p') => self.play(self.selected),
            KeyCode::Char('s') | KeyCode::Char('x') | KeyCode::Delete | KeyCode::Backspace => {
                self.stop(self.selected)
            }
            KeyCode::Char('o') | KeyCode::Char('S') => self.stop_all(),
            KeyCode::Char('=') | KeyCode::Char('+') => self.nudge_volume(0.05),
            KeyCode::Char('-') | KeyCode::Char('_') => self.nudge_volume(-0.05),
            KeyCode::Up | KeyCode::Char('k') => self.move_selection(-(self.nav_stride() as isize)),
            KeyCode::Down | KeyCode::Char('j') => self.move_selection(self.nav_stride() as isize),
            KeyCode::Left | KeyCode::Char('h') if self.screen == Screen::Board => {
                self.move_selection(-1)
            }
            KeyCode::Right | KeyCode::Char('l') if self.screen == Screen::Board => {
                self.move_selection(1)
            }
            _ => {}
        }
    }

    fn toggle_screen(&mut self) {
        self.screen = match self.screen {
            Screen::List => Screen::Board,
            Screen::Board => Screen::List,
        };
    }

    /// Up/down move by one row on the list, one grid **row** (the column count) on the board.
    fn nav_stride(&self) -> usize {
        match self.screen {
            Screen::List => 1,
            Screen::Board => self.grid_cols.max(1),
        }
    }

    fn move_selection(&mut self, delta: isize) {
        if self.sounds.is_empty() {
            return;
        }
        let n = self.sounds.len() as isize;
        let next = self.selected as isize + delta;
        if (0..n).contains(&next) {
            self.selected = next as usize;
        }
    }

    // ---- mouse ---------------------------------------------------------------------------------

    pub fn on_mouse(&mut self, m: MouseEvent) {
        let pos = Position {
            x: m.column,
            y: m.row,
        };
        match m.kind {
            MouseEventKind::Down(MouseButton::Left) => {
                if self.slider_rect.contains(pos) {
                    // Press on the slider arms click-and-drag, so holding and sweeping scrubs it.
                    self.dragging = true;
                    self.last_sent_pct = None;
                    self.commit_volume(self.volume_at(m.row), true);
                } else if self.stop_all_rect.contains(pos) {
                    self.stop_all();
                } else if let Some(i) = hit(&self.stop_rects, pos) {
                    self.selected = i;
                    self.stop(i);
                } else if let Some(i) = hit(&self.hit_rects, pos) {
                    self.selected = i;
                    self.play(i);
                }
            }
            MouseEventKind::Down(MouseButton::Right) => {
                // Right-click a sound = stop all its clips (both screens).
                if let Some(i) = hit(&self.hit_rects, pos) {
                    self.selected = i;
                    self.stop(i);
                }
            }
            MouseEventKind::Drag(MouseButton::Left) => {
                if self.dragging {
                    self.commit_volume(self.volume_at(m.row), true);
                }
            }
            MouseEventKind::Up(MouseButton::Left) => {
                self.dragging = false;
                self.last_sent_pct = None;
            }
            // Wheel over the slider (or anywhere left of it) tweaks the volume; elsewhere it moves
            // the selection through the sounds.
            MouseEventKind::ScrollUp => {
                if m.column <= self.slider_rect.right() {
                    self.nudge_volume(0.05);
                } else {
                    self.move_selection(-1);
                }
            }
            MouseEventKind::ScrollDown => {
                if m.column <= self.slider_rect.right() {
                    self.nudge_volume(-0.05);
                } else {
                    self.move_selection(1);
                }
            }
            _ => {}
        }
    }

    /// The volume (0..1) a click/drag at row `y` maps to along the vertical slider (top = 100 %),
    /// each cell centered on its half-step.
    pub fn volume_at(&self, y: u16) -> f64 {
        let r = self.slider_rect;
        if r.height == 0 {
            return self.volume;
        }
        let rel = (y as f64 + 0.5 - r.y as f64) / r.height as f64;
        (1.0 - rel).clamp(0.0, 1.0)
    }

    // ---- commands ------------------------------------------------------------------------------

    /// Plays one more layer of sound `i`. Every press starts a **new** channel (repeat-press
    /// layering is the whole point); presses that pile up within one worker interval are dispatched
    /// as a single same-tick `ctl/batch` group.
    pub fn play(&mut self, i: usize) {
        let Some(sound) = self.sounds.get_mut(i) else {
            return;
        };
        match &sound.state {
            SoundState::Ready => {}
            SoundState::Registering => {
                let msg = format!("{}: still registering\u{2026}", sound.name);
                self.set_status(&msg, false);
                return;
            }
            SoundState::Error(e) => {
                let msg = format!("{}: {e}", sound.name);
                self.set_status(&msg, true);
                return;
            }
        }
        if !self.connected || !self.audio_ok {
            return; // the status line already explains why
        }
        sound.flash = Some(Instant::now());
        let _ = self.tx.send(ToWorker::Play {
            clip: sound.clip.clone(),
            label: sound.name.clone(),
        });
    }

    /// Stops **all** live clips of sound `i` (the `audio/stop <name>` fan-out).
    pub fn stop(&mut self, i: usize) {
        let Some(sound) = self.sounds.get(i) else {
            return;
        };
        if sound.state != SoundState::Ready || !self.connected || !self.audio_ok {
            return;
        }
        let _ = self.tx.send(ToWorker::Stop {
            clip: sound.clip.clone(),
            label: sound.name.clone(),
        });
    }

    /// OMG STOP — stops every live channel (idempotent, always armed).
    pub fn stop_all(&mut self) {
        let _ = self.tx.send(ToWorker::StopAll);
        self.set_status("OMG STOP", false);
    }

    fn nudge_volume(&mut self, delta: f64) {
        self.commit_volume(self.volume + delta, false);
    }

    /// Sets the displayed volume and sends it to the worker. `dedupe` (the drag path) skips the
    /// send when the rounded percent hasn't changed, so a held sweep sends one command per 1 %
    /// crossed, not one per mouse event.
    fn commit_volume(&mut self, value: f64, dedupe: bool) {
        let value = value.clamp(0.0, 1.0);
        self.volume = value;
        let pct = (value * 100.0).round() as i32;
        if dedupe && self.last_sent_pct == Some(pct) {
            return;
        }
        self.last_sent_pct = Some(pct);
        let _ = self.tx.send(ToWorker::SetVolume(value));
        self.set_status(&format!("vol \u{2192} {pct}%"), false);
    }

    fn set_status(&mut self, message: &str, is_error: bool) {
        self.status = message.to_string();
        self.status_err = is_error;
    }
}

/// Finds the sound index whose recorded rect contains `pos`.
fn hit(rects: &[(usize, Rect)], pos: Position) -> Option<usize> {
    rects.iter().find(|(_, r)| r.contains(pos)).map(|(i, _)| *i)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::source::Poll;
    use std::sync::mpsc::{self, Receiver};

    fn app() -> (App, Receiver<ToWorker>) {
        let (tx, rx) = mpsc::channel();
        let sounds = vec![
            ("Airhorn".to_string(), "Airhorn.mp3".to_string()),
            ("Bonk".to_string(), "Bonk.wav".to_string()),
            ("Rimshot".to_string(), "Rimshot.ogg".to_string()),
        ];
        (
            App::new(tx, "T".into(), "fs:/sim".into(), sounds, Screen::List, 1.0),
            rx,
        )
    }

    fn ready(a: &mut App) {
        for i in 0..a.sounds.len() {
            a.apply(FromWorker::Registered {
                idx: i,
                result: Ok(crate::source::RegOutcome::Cached),
            });
        }
        a.apply(FromWorker::Poll(Poll {
            connected: true,
            audio_ok: true,
            counts: vec![0; a.sounds.len()],
            total: 0,
        }));
    }

    #[test]
    fn play_needs_registration_and_a_live_audio_surface() {
        let (mut a, rx) = app();
        a.play(0); // still Registering — no command
        assert!(rx.try_recv().is_err());
        ready(&mut a);
        a.play(0);
        assert!(matches!(rx.try_recv(), Ok(ToWorker::Play { clip, .. }) if clip == "Airhorn.mp3"));
        assert!(a.sounds[0].is_flashing());
    }

    #[test]
    fn repeated_presses_each_send_a_play() {
        let (mut a, rx) = app();
        ready(&mut a);
        a.play(1);
        a.play(1);
        a.play(1);
        let plays: Vec<_> = std::iter::from_fn(|| rx.try_recv().ok()).collect();
        assert_eq!(plays.len(), 3); // layering: every press is a new channel
    }

    #[test]
    fn a_failed_registration_makes_the_sound_inert() {
        let (mut a, rx) = app();
        a.apply(FromWorker::Registered {
            idx: 0,
            result: Err("ENOENT: no such file".into()),
        });
        assert!(matches!(a.sounds[0].state, SoundState::Error(_)));
        assert!(a.status_err);
        a.apply(FromWorker::Poll(Poll {
            connected: true,
            audio_ok: true,
            counts: vec![0, 0, 0],
            total: 0,
        }));
        a.play(0);
        assert!(rx.try_recv().is_err());
    }

    #[test]
    fn stop_and_stop_all_send_their_commands() {
        let (mut a, rx) = app();
        ready(&mut a);
        a.stop(2);
        assert!(matches!(rx.try_recv(), Ok(ToWorker::Stop { clip, .. }) if clip == "Rimshot.ogg"));
        a.stop_all();
        assert!(matches!(rx.try_recv(), Ok(ToWorker::StopAll)));
    }

    #[test]
    fn board_navigation_moves_by_grid_stride() {
        let (mut a, _rx) = app();
        ready(&mut a);
        a.screen = Screen::Board;
        a.grid_cols = 2;
        a.on_key(KeyEvent::from(KeyCode::Down)); // 0 → 2 (one grid row down)
        assert_eq!(a.selected, 2);
        a.on_key(KeyEvent::from(KeyCode::Down)); // would be 4 — out of range, stays
        assert_eq!(a.selected, 2);
        a.on_key(KeyEvent::from(KeyCode::Left));
        assert_eq!(a.selected, 1);
        a.on_key(KeyEvent::from(KeyCode::Up));
        assert!(a.selected < 2); // back up a row
    }

    #[test]
    fn volume_slider_maps_rows_and_dedupes_drags() {
        let (mut a, rx) = app();
        ready(&mut a);
        a.slider_rect = Rect::new(0, 0, 2, 10);
        assert!(a.volume_at(0) > 0.9);
        assert!(a.volume_at(9) < 0.1);
        // A drag press + a same-percent drag = one SetVolume.
        a.on_mouse(MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 0,
            row: 0,
            modifiers: KeyModifiers::NONE,
        });
        a.on_mouse(MouseEvent {
            kind: MouseEventKind::Drag(MouseButton::Left),
            column: 0,
            row: 0,
            modifiers: KeyModifiers::NONE,
        });
        let sets: Vec<_> = std::iter::from_fn(|| rx.try_recv().ok())
            .filter(|c| matches!(c, ToWorker::SetVolume(_)))
            .collect();
        assert_eq!(sets.len(), 1);
        assert!(a.volume > 0.9);
    }

    #[test]
    fn clicks_route_to_slider_button_rows() {
        let (mut a, rx) = app();
        ready(&mut a);
        a.stop_all_rect = Rect::new(0, 20, 12, 1);
        a.hit_rects = vec![(0, Rect::new(6, 2, 20, 1)), (1, Rect::new(6, 3, 20, 1))];
        a.stop_rects = vec![(1, Rect::new(22, 3, 3, 1))];
        // Click a row → select + play.
        a.on_mouse(MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 8,
            row: 3,
            modifiers: KeyModifiers::NONE,
        });
        assert_eq!(a.selected, 1);
        assert!(matches!(rx.try_recv(), Ok(ToWorker::Play { .. })));
        // Click the row's [■] → stop (the stop rect wins over the row rect).
        a.on_mouse(MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 23,
            row: 3,
            modifiers: KeyModifiers::NONE,
        });
        assert!(matches!(rx.try_recv(), Ok(ToWorker::Stop { .. })));
        // Click OMG STOP.
        a.on_mouse(MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 3,
            row: 20,
            modifiers: KeyModifiers::NONE,
        });
        assert!(matches!(rx.try_recv(), Ok(ToWorker::StopAll)));
    }

    #[test]
    fn poll_updates_counts_and_disabled_audio_is_surfaced() {
        let (mut a, _rx) = app();
        ready(&mut a);
        a.apply(FromWorker::Poll(Poll {
            connected: true,
            audio_ok: true,
            counts: vec![2, 0, 1],
            total: 4,
        }));
        assert_eq!(a.sounds[0].count, 2);
        assert_eq!(a.total_active, 4);
        a.apply(FromWorker::Poll(Poll {
            connected: true,
            audio_ok: false,
            counts: vec![0, 0, 0],
            total: 0,
        }));
        assert!(a.status.contains("audio_enabled"));
    }

    #[test]
    fn tab_and_esc_navigate_screens() {
        let (mut a, _rx) = app();
        a.on_key(KeyEvent::from(KeyCode::Tab));
        assert_eq!(a.screen, Screen::Board);
        a.on_key(KeyEvent::from(KeyCode::Esc));
        assert_eq!(a.screen, Screen::List);
        a.on_key(KeyEvent::from(KeyCode::Esc));
        assert!(a.should_quit);
    }
}
