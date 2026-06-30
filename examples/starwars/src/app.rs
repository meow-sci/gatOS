//! Scroll state: owns the rasterized [`Canvas`] and the dot-precision scroll position, and rebuilds
//! the canvas whenever the terminal size changes (word-wrap depends on the dot width).

use std::time::Duration;

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::style::Color;

use crate::canvas::{Canvas, Layout};

pub struct App {
    layout: Layout,
    text: String,
    pub color: Color,
    pub threshold: u8,
    speed_dots_per_sec: f64,
    font_px: f32,
    margin_pct: f64,
    loop_mode: bool,

    cols: u16,
    rows: u16,
    pub canvas: Option<Canvas>,
    pub scroll_dot: f64,
}

#[allow(clippy::too_many_arguments)]
impl App {
    pub fn new(
        text: String,
        uppercase: bool,
        color: Color,
        threshold: u8,
        speed_dots_per_sec: f64,
        font_px: f32,
        margin_pct: f64,
        loop_mode: bool,
    ) -> Self {
        App {
            layout: Layout::load(),
            text: if uppercase { text.to_uppercase() } else { text },
            color,
            threshold,
            speed_dots_per_sec,
            font_px,
            margin_pct,
            loop_mode,
            cols: 0,
            rows: 0,
            canvas: None,
            scroll_dot: 0.0,
        }
    }

    /// Rebuilds the canvas if the terminal size changed since the last frame (or hasn't built yet).
    pub fn ensure_canvas(&mut self, cols: u16, rows: u16) {
        if cols == 0 || rows == 0 {
            return;
        }
        if self.canvas.is_some() && cols == self.cols && rows == self.rows {
            return;
        }
        let dot_width = cols as usize * 2;
        let margin_dots =
            ((dot_width as f64 * self.margin_pct / 100.0) as usize).min(dot_width / 2);
        self.canvas = Some(
            self.layout
                .render(&self.text, dot_width, margin_dots, self.font_px),
        );
        self.cols = cols;
        self.rows = rows;
        self.restart();
    }

    /// Resets the scroll position so the whole canvas re-enters from below the viewport.
    pub fn restart(&mut self) {
        self.scroll_dot = -(self.viewport_height_dots() as f64);
    }

    pub fn tick(&mut self, dt: Duration) {
        self.scroll_dot += self.speed_dots_per_sec * dt.as_secs_f64();
        if self.loop_mode && self.finished() {
            self.restart();
        }
    }

    pub fn finished(&self) -> bool {
        match &self.canvas {
            Some(canvas) => self.scroll_dot > canvas.height as f64,
            None => false,
        }
    }

    fn viewport_height_dots(&self) -> i64 {
        self.rows as i64 * 4
    }

    /// Returns `true` if the key requests quit.
    pub fn on_key(&self, key: KeyEvent) -> bool {
        matches!(
            (key.code, key.modifiers),
            (KeyCode::Char('q'), _)
                | (KeyCode::Esc, _)
                | (KeyCode::Char('c'), KeyModifiers::CONTROL)
        )
    }
}
