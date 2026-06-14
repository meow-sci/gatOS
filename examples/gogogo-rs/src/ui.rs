//! Rendering. Two widgets, two orientations. The layout (and the bar's direction) is chosen from the
//! terminal size every frame, so it flips live on resize. Per the project's transparent-TUI rule the
//! panel leaves backgrounds unset — purrTTY shows the game through — and colors foregrounds; only the
//! throttle bar paints a background, which is how it shows its fill level. The renderer also records
//! the bar/button rects back onto the [`App`] so `app.rs` can hit-test mouse events.

use ratatui::buffer::Buffer;
use ratatui::layout::Rect;
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::Span;
use ratatui::widgets::{Block, BorderType, Borders};
use ratatui::Frame;

use crate::app::App;

/// Eighth-block characters for a smooth bar leading edge, filling bottom-up (`▁`..`▇`) and, reused for
/// the horizontal bar, left-to-right (`▏`..`▉`). Index 0 is empty; full cells use a background fill.
const VBLOCKS: [char; 8] = [' ', '\u{2581}', '\u{2582}', '\u{2583}', '\u{2584}', '\u{2585}', '\u{2586}', '\u{2587}'];
const HBLOCKS: [char; 8] = [' ', '\u{258f}', '\u{258e}', '\u{258d}', '\u{258c}', '\u{258b}', '\u{258a}', '\u{2589}'];

const ACCENT: Color = Color::LightCyan;
const TRACK: Color = Color::DarkGray;
const TRACK_CHAR: char = '\u{2591}'; // ░ — the faint slider channel (and the disabled/greyed fill)

pub fn render(f: &mut Frame, app: &mut App) {
    let area = f.area();
    if area.width == 0 || area.height == 0 {
        return;
    }

    // The border defines the floating window — but only when there's room; tiny windows use it all.
    let inner = if area.width >= 6 && area.height >= 4 {
        let color = if app.active { Color::Gray } else { TRACK };
        let title = format!(" gogogo \u{b7} {} ", truncate(&app.label, 18));
        let block = Block::default()
            .borders(Borders::ALL)
            .border_type(BorderType::Rounded)
            .border_style(Style::default().fg(color))
            .title(Span::styled(title, Style::default().fg(color)));
        let inner = block.inner(area);
        f.render_widget(block, area);
        inner
    } else {
        area
    };
    if inner.width == 0 || inner.height == 0 {
        return;
    }

    if app.orientation.is_vertical(inner) {
        layout_vertical(f, app, inner);
    } else {
        layout_horizontal(f, app, inner);
    }
}

fn layout_vertical(f: &mut Frame, app: &mut App, inner: Rect) {
    let enabled = app.active;
    let throttle = app.throttle;
    let ignited = app.ignited();
    let status = app.status.clone();
    let status_err = app.status_err;
    let pct = (throttle.clamp(0.0, 1.0) * 100.0).round() as i32;

    let header_h: u16 = u16::from(inner.height >= 3);
    let status_h: u16 = u16::from(inner.height >= 5);
    let button_h: u16 = u16::from(inner.height >= 2);

    let bar_top = inner.y + header_h;
    let bar_bottom = inner.y + inner.height - button_h - status_h;
    let bar_rect = Rect::new(inner.x, bar_top, inner.width, bar_bottom.saturating_sub(bar_top));
    app.bar_rect = bar_rect;
    app.bar_vertical = true;
    app.button_rect = if button_h > 0 {
        Rect::new(inner.x, inner.y + inner.height - 1, inner.width, 1)
    } else {
        Rect::ZERO
    };

    let buf = f.buffer_mut();
    if header_h > 0 {
        let style = label_style(enabled);
        put_centered(buf, inner.x, inner.y, inner.width, &format!("THR {pct:>3}%"), style);
    }
    draw_vbar(buf, bar_rect, throttle, enabled);
    if status_h > 0 {
        put_centered(buf, inner.x, bar_bottom, inner.width, &status, status_style(status_err));
    }
    if button_h > 0 {
        draw_button(buf, app.button_rect, ignited, enabled);
    }
}

fn layout_horizontal(f: &mut Frame, app: &mut App, inner: Rect) {
    let enabled = app.active;
    let throttle = app.throttle;
    let ignited = app.ignited();
    let status = app.status.clone();
    let status_err = app.status_err;
    let pct = (throttle.clamp(0.0, 1.0) * 100.0).round() as i32;

    // Reserve a right-hand column for the toggle button; the throttle takes the rest. Widths shrink
    // gracefully (and stay <= inner.width, so the split never underflows) as the window gets narrow.
    let btn_w = match inner.width {
        w if w >= 20 => 14,         // full "[ SHUTDOWN ]" + padding, bar gets 6+
        w if w >= 16 => 12,         // full "[ SHUTDOWN ]"
        w => w / 2,                 // degenerate narrow window: split it, label may clip
    };
    let left_w = inner.width - btn_w;
    let left = Rect::new(inner.x, inner.y, left_w, inner.height);
    let right = Rect::new(inner.x + left_w, inner.y, btn_w, inner.height);

    let header_h: u16 = u16::from(left.height >= 2);
    let status_h: u16 = u16::from(left.height >= 3);
    let bar_top = left.y + header_h;
    let bar_bottom = left.y + left.height - status_h;
    let bar_rect = Rect::new(left.x, bar_top, left.width, bar_bottom.saturating_sub(bar_top).max(1));
    app.bar_rect = bar_rect;
    app.bar_vertical = false;
    app.button_rect = right;

    let buf = f.buffer_mut();
    if header_h > 0 {
        let head = if left.width >= 14 {
            format!("THROTTLE  {pct:>3}%")
        } else {
            format!("THR {pct:>3}%")
        };
        put_centered(buf, left.x, left.y, left.width, &head, label_style(enabled));
    }
    // A horizontal bar is one row of fill; repeat it down the band for a fatter drag target.
    for y in bar_rect.y..bar_rect.y + bar_rect.height {
        draw_hbar_row(buf, bar_rect.x, y, bar_rect.width, throttle, enabled);
    }
    if status_h > 0 {
        put_centered(buf, left.x, bar_bottom, left.width, &status, status_style(status_err));
    }
    draw_button(buf, right, ignited, enabled);
}

// ---- bar + button drawing -----------------------------------------------------------------------

/// A vertical fill bar filling bottom-up across `rect`. Full cells use a background fill (per the
/// transparent-TUI rule, the bar is the one element allowed a background); the leading edge uses an
/// eighth-block glyph for sub-cell smoothness; the rest is a faint channel. Disabled = all greyed.
fn draw_vbar(buf: &mut Buffer, rect: Rect, frac: f64, enabled: bool) {
    if rect.width == 0 || rect.height == 0 {
        return;
    }
    let level = (frac.clamp(0.0, 1.0) * (rect.height as f64) * 8.0).round() as i32;
    for r in 0..rect.height {
        let floor = (rect.height - 1 - r) as i32 * 8; // eighths below this cell, 0 at the bottom row
        let y = rect.y + r;
        for cx in rect.x..rect.x + rect.width {
            let cell = &mut buf[(cx, y)];
            if enabled && level >= floor + 8 {
                cell.set_char(' ').set_bg(ACCENT);
            } else if enabled && level > floor {
                cell.set_char(VBLOCKS[(level - floor) as usize]).set_fg(ACCENT).set_bg(Color::Reset);
            } else {
                cell.set_char(TRACK_CHAR).set_fg(TRACK).set_bg(Color::Reset);
            }
        }
    }
}

/// One row of a horizontal fill bar filling left-to-right. Same fill discipline as [`draw_vbar`].
fn draw_hbar_row(buf: &mut Buffer, x: u16, y: u16, w: u16, frac: f64, enabled: bool) {
    if w == 0 {
        return;
    }
    let level = (frac.clamp(0.0, 1.0) * (w as f64) * 8.0).round() as i32;
    for i in 0..w {
        let floor = i as i32 * 8;
        let cell = &mut buf[(x + i, y)];
        if enabled && level >= floor + 8 {
            cell.set_char(' ').set_bg(ACCENT);
        } else if enabled && level > floor {
            cell.set_char(HBLOCKS[(level - floor) as usize]).set_fg(ACCENT).set_bg(Color::Reset);
        } else {
            cell.set_char(TRACK_CHAR).set_fg(TRACK).set_bg(Color::Reset);
        }
    }
}

/// The ignite/shutdown toggle: a bracketed label centered in `rect`, colored by state (green IGNITE
/// when cold, red SHUTDOWN when lit), greyed when disabled. No background — it floats over the game.
fn draw_button(buf: &mut Buffer, rect: Rect, ignited: bool, enabled: bool) {
    if rect.width == 0 || rect.height == 0 {
        return;
    }
    let (text, color) = if !enabled {
        ("[  \u{2014}\u{2014}  ]", TRACK)
    } else if ignited {
        ("[ SHUTDOWN ]", Color::Red)
    } else {
        ("[ IGNITE ]", Color::Green)
    };
    let style = Style::default().fg(color).add_modifier(Modifier::BOLD);
    put_centered(buf, rect.x, rect.y + rect.height / 2, rect.width, text, style);
}

// ---- helpers ------------------------------------------------------------------------------------

fn label_style(enabled: bool) -> Style {
    Style::default()
        .fg(if enabled { Color::White } else { TRACK })
        .add_modifier(Modifier::BOLD)
}

fn status_style(is_error: bool) -> Style {
    Style::default().fg(if is_error { Color::Red } else { TRACK })
}

/// Writes `s` horizontally centered within `[x, x+w)` at row `y`, clipped to the width.
fn put_centered(buf: &mut Buffer, x: u16, y: u16, w: u16, s: &str, style: Style) {
    if w == 0 {
        return;
    }
    let len = s.chars().count() as u16;
    let start = if len >= w { x } else { x + (w - len) / 2 };
    let avail = (x + w).saturating_sub(start) as usize;
    let text: String = s.chars().take(avail).collect();
    buf.set_string(start, y, text, style);
}

/// Truncates `s` to `max` chars, appending `…` when it was cut.
fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        return s.to_string();
    }
    let head: String = s.chars().take(max.saturating_sub(1)).collect();
    format!("{head}\u{2026}")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::app::{App, Orient};
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;
    use std::sync::mpsc;

    /// Renders an app at `(w, h)` and returns the screen text (one string, rows newline-joined).
    fn render_to_text(app: &mut App, w: u16, h: u16) -> String {
        let mut terminal = Terminal::new(TestBackend::new(w, h)).unwrap();
        terminal.draw(|f| render(f, app)).unwrap();
        let buf = terminal.backend().buffer().clone();
        let mut out = String::new();
        for y in 0..buf.area.height {
            for x in 0..buf.area.width {
                out.push_str(buf[(x, y)].symbol());
            }
            out.push('\n');
        }
        out
    }

    fn app(orientation: Orient) -> App {
        let (tx, _rx) = mpsc::channel();
        App::new(tx, "fs:/sim".to_string(), orientation)
    }

    #[test]
    fn inactive_panel_disables_and_labels_itself() {
        let mut a = app(Orient::Auto);
        a.active = false;
        a.connected = true;
        a.apply(crate::source::FromWorker::Poll(crate::source::Poll {
            connected: true,
            active: false,
            throttle: None,
            ignited: None,
        }));
        let text = render_to_text(&mut a, 40, 12);
        assert!(text.contains("no active vessel"));
        assert!(!text.contains("IGNITE")); // the button is greyed, not labelled
    }

    #[test]
    fn active_vertical_shows_throttle_and_ignite() {
        let mut a = app(Orient::Vertical);
        a.active = true;
        a.throttle = 0.5;
        let text = render_to_text(&mut a, 20, 30);
        assert!(text.contains("THR"));
        assert!(text.contains("IGNITE"));
    }

    #[test]
    fn active_horizontal_shows_throttle_and_button() {
        let mut a = app(Orient::Horizontal);
        a.active = true;
        a.throttle = 0.5;
        let text = render_to_text(&mut a, 60, 8);
        assert!(text.contains("THROTTLE"));
        assert!(text.contains("IGNITE"));
    }

    #[test]
    fn lit_engines_show_shutdown() {
        let mut a = app(Orient::Horizontal);
        a.active = true;
        a.apply(crate::source::FromWorker::Poll(crate::source::Poll {
            connected: true,
            active: true,
            throttle: Some(0.2),
            ignited: Some(true),
        }));
        let text = render_to_text(&mut a, 60, 8);
        assert!(text.contains("SHUTDOWN"));
    }

    /// The layout must survive any size — including sizes too small for the border or any widget.
    #[test]
    fn tiny_and_odd_sizes_never_panic() {
        for orient in [Orient::Auto, Orient::Vertical, Orient::Horizontal] {
            for &(w, h) in &[(1, 1), (2, 2), (4, 3), (6, 4), (8, 2), (3, 8), (12, 5)] {
                let mut a = app(orient);
                a.active = true;
                a.throttle = 0.73;
                let _ = render_to_text(&mut a, w, h);
            }
        }
    }
}
