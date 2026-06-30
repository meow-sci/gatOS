//! Downsamples a [`Canvas`] window into braille terminal cells.
//!
//! Each cell covers a 2 (horizontal) x 4 (vertical) block of canvas dots — the standard Unicode
//! braille dot matrix — giving 4x the vertical resolution of a plain character cell. That's what
//! lets the crawl scroll one *dot* at a time instead of jumping a whole row per step.

use ratatui::style::{Color, Style};
use ratatui::text::Span;

use crate::canvas::Canvas;

// Braille dot bit layout, the standard "drawille" convention (2 cols x 4 rows):
//   (row,col) -> bit:  (0,0)=0 (0,1)=3
//                      (1,0)=1 (1,1)=4
//                      (2,0)=2 (2,1)=5
//                      (3,0)=6 (3,1)=7
const BIT: [[u8; 2]; 4] = [[0, 3], [1, 4], [2, 5], [6, 7]];
const BRAILLE_BASE: u32 = 0x2800;

/// Render one terminal row: `top_dot` is the canvas dot-row aligned with this cell row's top edge
/// (it may be negative or past `canvas.height` — those just read as empty, see [`Canvas::sample`]).
pub fn render_row(
    canvas: &Canvas,
    top_dot: i64,
    cols: usize,
    threshold: u8,
    color: Color,
) -> Vec<Span<'static>> {
    (0..cols)
        .map(|col| render_cell(canvas, top_dot, col, threshold, color))
        .collect()
}

fn render_cell(
    canvas: &Canvas,
    top_dot: i64,
    col: usize,
    threshold: u8,
    color: Color,
) -> Span<'static> {
    let base_x = (col * 2) as i64;
    let mut pattern: u8 = 0;
    let mut coverage_sum: u32 = 0;
    let mut lit: u32 = 0;
    for (dy, row_bits) in BIT.iter().enumerate() {
        for (dx, bit) in row_bits.iter().enumerate() {
            let v = canvas.sample(base_x + dx as i64, top_dot + dy as i64);
            if v >= threshold {
                pattern |= 1 << bit;
                coverage_sum += v as u32;
                lit += 1;
            }
        }
    }
    if lit == 0 {
        return Span::raw(" ");
    }
    let avg = coverage_sum as f32 / lit as f32 / 255.0;
    let ch = char::from_u32(BRAILLE_BASE + pattern as u32).unwrap_or(' ');
    Span::styled(ch.to_string(), Style::default().fg(shade(color, avg)))
}

/// Blend toward the base color by coverage, so a glyph's edge dots (lower average coverage) look
/// dimmer than its solid interior — a cheap stand-in for anti-aliasing at braille resolution.
fn shade(color: Color, t: f32) -> Color {
    let floor = 0.45 + 0.55 * t.clamp(0.0, 1.0);
    match color {
        Color::Rgb(r, g, b) => Color::Rgb(
            (r as f32 * floor) as u8,
            (g as f32 * floor) as u8,
            (b as f32 * floor) as u8,
        ),
        other => other,
    }
}
