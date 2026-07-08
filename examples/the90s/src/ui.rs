//! Rendering. A shared chrome — title line, left-edge volume rail, bottom bar with the red
//! **OMG STOP** button — around the two screen bodies: the keyboard-nav **list** and the
//! 90s-flash-app **soundboard** grid (a masonry layout: every row splits the full width among its
//! cells, so a partial last row stretches its buttons wider instead of leaving a hole).
//!
//! Per the project's transparent-TUI rule the app leaves backgrounds unset — purrTTY shows the
//! game through — and colors foregrounds; only the volume bar's fill paints a background (that is
//! how it shows its level). The renderer records the slider/button/row/cell rects back onto the
//! [`App`] so `app.rs` can hit-test mouse events, and the grid's column count so keyboard
//! navigation moves by grid rows.

use ratatui::buffer::Buffer;
use ratatui::layout::Rect;
use ratatui::style::{Color, Modifier, Style};
use ratatui::widgets::{Block, BorderType};
use ratatui::Frame;

use crate::app::{App, Screen, SoundState};

/// Eighth-block characters for a smooth bar leading edge, filling bottom-up. Index 0 is empty;
/// full cells use a background fill.
const VBLOCKS: [char; 8] = [' ', '\u{2581}', '\u{2582}', '\u{2583}', '\u{2584}', '\u{2585}', '\u{2586}', '\u{2587}'];

const TRACK: Color = Color::DarkGray;
const TRACK_CHAR: char = '\u{2591}'; // ░ — the faint slider channel
const VOLUME: Color = Color::LightYellow;
const ACTIVE: Color = Color::LightGreen;

/// The gloriously-90s button palette, cycled across the sounds.
const PALETTE: [Color; 6] = [
    Color::LightMagenta,
    Color::LightCyan,
    Color::LightYellow,
    Color::LightGreen,
    Color::LightRed,
    Color::LightBlue,
];

pub fn render(f: &mut Frame, app: &mut App) {
    let area = f.area();
    app.hit_rects.clear();
    app.stop_rects.clear();
    app.slider_rect = Rect::ZERO;
    app.stop_all_rect = Rect::ZERO;
    if area.width == 0 || area.height == 0 {
        return;
    }

    let title_h = u16::from(area.height >= 5);
    let bottom_h = u16::from(area.height >= 3);
    let rail_w: u16 = if area.width >= 30 {
        6
    } else if area.width >= 14 {
        4
    } else {
        0
    };
    let body = Rect::new(
        area.x + rail_w,
        area.y + title_h,
        area.width - rail_w,
        area.height - title_h - bottom_h,
    );
    let rail = Rect::new(area.x, body.y, rail_w, body.height);

    if title_h > 0 {
        draw_title(f.buffer_mut(), app, Rect::new(area.x, area.y, area.width, 1));
    }
    draw_rail(f.buffer_mut(), app, rail);
    match app.screen {
        Screen::List => draw_list(f.buffer_mut(), app, body),
        Screen::Board => draw_board(f, app, body),
    }
    if bottom_h > 0 {
        let bar = Rect::new(area.x, area.y + area.height - 1, area.width, 1);
        draw_bottom(f.buffer_mut(), app, bar);
    }
}

/// Everything is styled "enabled" only when a play could actually land.
fn enabled(app: &App) -> bool {
    app.connected && app.audio_ok
}

// ---- chrome ---------------------------------------------------------------------------------------

fn draw_title(buf: &mut Buffer, app: &App, rect: Rect) {
    let text = if rect.width as usize >= app.title.chars().count() + 10 {
        format!("\u{2591}\u{2592}\u{2593} {} \u{2593}\u{2592}\u{2591}", app.title)
    } else {
        app.title.clone()
    };
    let style = Style::default()
        .fg(if enabled(app) { Color::LightMagenta } else { TRACK })
        .add_modifier(Modifier::BOLD);
    put_centered(buf, rect.x, rect.y, rect.width, &text, style);
}

/// The left-edge volume rail: `VOL` header, the clickable/draggable fill bar, and the percent
/// readout. The bar is a purely app-side knob (there is no global `/sim` volume) — it scales new
/// plays and live-adjusts playing clips.
fn draw_rail(buf: &mut Buffer, app: &mut App, rail: Rect) {
    if rail.width == 0 || rail.height == 0 {
        return;
    }
    let header_h = u16::from(rail.height >= 5);
    let footer_h = u16::from(rail.height >= 3);
    let bar = Rect::new(
        rail.x,
        rail.y + header_h,
        rail.width.saturating_sub(2).max(1),
        rail.height - header_h - footer_h,
    );
    app.slider_rect = bar;

    let label_style = Style::default().fg(Color::White).add_modifier(Modifier::BOLD);
    if header_h > 0 {
        put_centered(buf, rail.x, rail.y, bar.width, "VOL", label_style);
    }
    draw_vbar(buf, bar, app.volume, VOLUME);
    if footer_h > 0 {
        let pct = format!("{:>3}", (app.volume * 100.0).round() as i32);
        put_centered(buf, rail.x, rail.y + rail.height - 1, bar.width, &pct, label_style);
    }
}

/// The bottom bar: the red OMG STOP button (with the live channel count when anything is
/// playing), the status line, and the key hints when there's room.
fn draw_bottom(buf: &mut Buffer, app: &mut App, bar: Rect) {
    let stop_label = if app.total_active > 0 {
        format!("[ OMG STOP ({}) ]", app.total_active)
    } else {
        "[ OMG STOP ]".to_string()
    };
    let stop_w = (stop_label.chars().count() as u16).min(bar.width);
    app.stop_all_rect = Rect::new(bar.x, bar.y, stop_w, 1);
    buf.set_string(
        bar.x,
        bar.y,
        fit(&stop_label, stop_w as usize),
        Style::default().fg(Color::Red).add_modifier(Modifier::BOLD),
    );

    let hints = match app.screen {
        Screen::List => "tab board \u{b7} \u{23ce} play \u{b7} s stop \u{b7} o omg \u{b7} q quit",
        Screen::Board => "tab list \u{b7} \u{23ce} play \u{b7} s stop \u{b7} o omg \u{b7} q quit",
    };
    let hints_w = hints.chars().count() as u16;
    let mut status_right = bar.right();
    if bar.width >= stop_w + hints_w + 24 {
        buf.set_string(bar.right() - hints_w, bar.y, hints, Style::default().fg(TRACK));
        status_right = bar.right() - hints_w - 2;
    }
    let status_x = bar.x + stop_w + 2;
    if status_right > status_x {
        let style = Style::default().fg(if app.status_err { Color::Red } else { Color::Gray });
        let w = (status_right - status_x) as usize;
        buf.set_string(status_x, bar.y, fit(&app.status, w), style);
    }
}

// ---- the list screen -------------------------------------------------------------------------------

/// One sound per row: selection marker + name on the left; the live `▶N` count, state icon, and a
/// clickable `[■]` stop button on the right.
fn draw_list(buf: &mut Buffer, app: &mut App, body: Rect) {
    if body.width < 4 || body.height == 0 {
        return;
    }
    let n = app.sounds.len();
    if n == 0 {
        put_centered(buf, body.x, body.y + body.height / 2, body.width, "no sounds in config", Style::default().fg(TRACK));
        return;
    }
    // Scroll to keep the selection visible.
    let vis = body.height as usize;
    if app.selected < app.list_offset {
        app.list_offset = app.selected;
    }
    if app.selected >= app.list_offset + vis {
        app.list_offset = app.selected + 1 - vis;
    }

    let on = enabled(app);
    let show_stop = body.width >= 26;
    for (row, i) in (app.list_offset..n).take(vis).enumerate() {
        let y = body.y + row as u16;
        let sound = &app.sounds[i];
        let selected = i == app.selected;
        app.hit_rects.push((i, Rect::new(body.x, y, body.width, 1)));

        // Right-side cluster: state/count, then the stop button.
        let mut right = body.right();
        if show_stop {
            let stop_style = if on && sound.count > 0 {
                Style::default().fg(Color::Red).add_modifier(Modifier::BOLD)
            } else {
                Style::default().fg(TRACK)
            };
            right -= 4;
            buf.set_string(right + 1, y, "[\u{25a0}]", stop_style);
            app.stop_rects.push((i, Rect::new(right + 1, y, 3, 1)));
        }
        let (badge, badge_style) = match &sound.state {
            SoundState::Registering => ("\u{2026}".to_string(), Style::default().fg(TRACK)),
            SoundState::Error(_) => ("\u{2717}".to_string(), Style::default().fg(Color::Red)),
            SoundState::Ready if sound.count > 0 => (
                format!("\u{25b6}{}", sound.count),
                Style::default().fg(ACTIVE).add_modifier(Modifier::BOLD),
            ),
            SoundState::Ready => (String::new(), Style::default()),
        };
        if !badge.is_empty() {
            let w = badge.chars().count() as u16;
            if right >= body.x + w + 4 {
                right -= w + 1;
                buf.set_string(right + 1, y, &badge, badge_style);
            }
        }

        // Left side: marker + name in the sound's palette color (white/bold when selected).
        let marker = if selected { "\u{25b8} " } else { "  " };
        let color = if !on || sound.state != SoundState::Ready {
            TRACK
        } else if selected {
            Color::White
        } else {
            PALETTE[i % PALETTE.len()]
        };
        let mut style = Style::default().fg(color);
        if selected {
            style = style.add_modifier(Modifier::BOLD);
        }
        if sound.is_flashing() {
            style = Style::default().fg(VOLUME).add_modifier(Modifier::BOLD);
        }
        let name_w = (right.saturating_sub(body.x + 2)) as usize;
        buf.set_string(body.x, y, marker, Style::default().fg(VOLUME));
        buf.set_string(body.x + 2, y, fit(&sound.name, name_w), style);
    }
}

// ---- the board screen --------------------------------------------------------------------------------

/// The full-screen soundboard: one chunky bordered button per sound in a masonry grid, the sound's
/// name under each box.
fn draw_board(f: &mut Frame, app: &mut App, body: Rect) {
    let n = app.sounds.len();
    if n == 0 || body.width < 6 || body.height < 3 {
        if body.height > 0 {
            put_centered(
                f.buffer_mut(),
                body.x,
                body.y + body.height / 2,
                body.width,
                "no sounds in config",
                Style::default().fg(TRACK),
            );
        }
        return;
    }
    let (cols, rows) = grid_dims(n, body.width, body.height);
    app.grid_cols = cols;
    let cells = masonry_rects(n, body, cols, rows);
    let on = enabled(app);

    for (i, cell) in cells.into_iter().enumerate() {
        app.hit_rects.push((i, cell));
        let sound = &app.sounds[i];
        let selected = i == app.selected;

        // The button box fills the cell minus a 1-col gutter and the label line under it.
        let label_h = u16::from(cell.height >= 4);
        let inset = u16::from(cell.width >= 6);
        let boxr = Rect::new(
            cell.x + inset,
            cell.y,
            cell.width - 2 * inset,
            cell.height - label_h,
        );

        let color = if !on || sound.state != SoundState::Ready {
            TRACK
        } else {
            PALETTE[i % PALETTE.len()]
        };
        let (border_style, border_type) = if sound.is_flashing() {
            (
                Style::default().fg(VOLUME).add_modifier(Modifier::BOLD),
                BorderType::Thick,
            )
        } else if selected {
            (
                Style::default().fg(Color::White).add_modifier(Modifier::BOLD),
                BorderType::Double,
            )
        } else {
            (Style::default().fg(color), BorderType::Rounded)
        };

        if boxr.width >= 2 && boxr.height >= 2 {
            f.render_widget(
                Block::bordered().border_type(border_type).border_style(border_style),
                boxr,
            );
        }

        // Inside the button: the live layer count (or the state icon).
        let (inner, inner_style) = match &sound.state {
            SoundState::Registering => ("\u{2026}".to_string(), Style::default().fg(TRACK)),
            SoundState::Error(_) => ("\u{2717}".to_string(), Style::default().fg(Color::Red)),
            SoundState::Ready if sound.count > 0 => (
                format!("\u{25b6} {}", sound.count),
                Style::default().fg(ACTIVE).add_modifier(Modifier::BOLD),
            ),
            SoundState::Ready => ("\u{25aa}".to_string(), Style::default().fg(color)),
        };
        let buf = f.buffer_mut();
        if boxr.height >= 3 {
            put_centered(buf, boxr.x + 1, boxr.y + boxr.height / 2, boxr.width - 2, &inner, inner_style);
        }

        // The name, under the box.
        if label_h > 0 {
            let mut style = Style::default().fg(if selected { Color::White } else { Color::Gray });
            if selected {
                style = style.add_modifier(Modifier::BOLD);
            }
            put_centered(
                buf,
                cell.x,
                cell.y + cell.height - 1,
                cell.width,
                &fit(&sound.name, cell.width.saturating_sub(2) as usize),
                style,
            );
        }
    }
}

// ---- grid math -------------------------------------------------------------------------------------

/// Picks the board's `(cols, rows)` for `n` buttons in a `w`×`h`-cell area. Terminal cells are
/// roughly twice as tall as wide, so it aims for button tiles ~2.4× wider than tall (visually
/// squarish), then nudges the column count so cells keep a usable minimum width and height.
pub fn grid_dims(n: usize, w: u16, h: u16) -> (usize, usize) {
    if n == 0 {
        return (0, 0);
    }
    let (wf, hf) = (w.max(1) as f64, h.max(1) as f64);
    let mut cols = ((n as f64 * wf / (2.4 * hf)).sqrt().round() as usize).clamp(1, n);
    let max_cols = ((wf / 12.0).floor() as usize).max(1).min(n);
    cols = cols.min(max_cols);
    // If the rows come out too flat to draw a box + label, trade width for height.
    while cols < max_cols && n.div_ceil(cols) * 4 > h as usize {
        cols += 1;
    }
    (cols, n.div_ceil(cols))
}

/// Splits `area` into `n` cells, `cols` per row — masonry style: each row divides the **full**
/// width among its own cells, so a partial last row stretches. Remainder columns/rows go to the
/// leading cells, keeping every button within one cell of the same size.
pub fn masonry_rects(n: usize, area: Rect, cols: usize, rows: usize) -> Vec<Rect> {
    let mut out = Vec::with_capacity(n);
    if n == 0 || cols == 0 || rows == 0 {
        return out;
    }
    let base_h = area.height / rows as u16;
    let extra_h = area.height % rows as u16;
    let mut y = area.y;
    for r in 0..rows {
        let h = base_h + u16::from((r as u16) < extra_h);
        let start = r * cols;
        let k = cols.min(n - start);
        let base_w = area.width / k as u16;
        let extra_w = area.width % k as u16;
        let mut x = area.x;
        for c in 0..k {
            let w = base_w + u16::from((c as u16) < extra_w);
            out.push(Rect::new(x, y, w, h));
            x += w;
        }
        y += h;
    }
    out
}

// ---- shared drawing ---------------------------------------------------------------------------------

/// A vertical fill bar filling bottom-up across `rect`. Full cells use a background fill (per the
/// transparent-TUI rule, the bar is the one element allowed a background); the leading edge uses
/// an eighth-block glyph for sub-cell smoothness; the rest is a faint channel.
fn draw_vbar(buf: &mut Buffer, rect: Rect, frac: f64, color: Color) {
    if rect.width == 0 || rect.height == 0 {
        return;
    }
    let level = (frac.clamp(0.0, 1.0) * (rect.height as f64) * 8.0).round() as i32;
    for r in 0..rect.height {
        let floor = (rect.height - 1 - r) as i32 * 8; // eighths below this cell, 0 at the bottom row
        let y = rect.y + r;
        for cx in rect.x..rect.x + rect.width {
            let cell = &mut buf[(cx, y)];
            if level >= floor + 8 {
                cell.set_char(' ').set_bg(color);
            } else if level > floor {
                cell.set_char(VBLOCKS[(level - floor) as usize]).set_fg(color).set_bg(Color::Reset);
            } else {
                cell.set_char(TRACK_CHAR).set_fg(TRACK).set_bg(Color::Reset);
            }
        }
    }
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

/// Truncates `s` to `w` display cells with a trailing ellipsis.
fn fit(s: &str, w: usize) -> String {
    if s.chars().count() <= w {
        s.to_string()
    } else if w >= 2 {
        let mut t: String = s.chars().take(w - 1).collect();
        t.push('\u{2026}');
        t
    } else {
        s.chars().take(w).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::app::{Screen, SoundUi};
    use crate::source::{FromWorker, Poll, RegOutcome};
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;
    use std::sync::mpsc;

    fn app(n: usize, screen: Screen) -> App {
        let (tx, _rx) = mpsc::channel();
        let sounds: Vec<(String, String)> = (0..n)
            .map(|i| (format!("Sound {i}"), format!("sound{i}.mp3")))
            .collect();
        let mut a = App::new(tx, "Rad Board".into(), "fs:/sim".into(), sounds, screen, 1.0);
        for i in 0..n {
            a.apply(FromWorker::Registered {
                idx: i,
                result: Ok(RegOutcome::Cached),
            });
        }
        a.apply(FromWorker::Poll(Poll {
            connected: true,
            audio_ok: true,
            counts: vec![0; n],
            total: 0,
        }));
        a
    }

    /// Renders an app at `(w, h)` and returns the screen text (rows newline-joined).
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

    #[test]
    fn list_screen_shows_title_sounds_and_omg_stop() {
        let mut a = app(3, Screen::List);
        let text = render_to_text(&mut a, 60, 16);
        assert!(text.contains("Rad Board"));
        assert!(text.contains("Sound 0"));
        assert!(text.contains("Sound 2"));
        assert!(text.contains("OMG STOP"));
        assert!(text.contains("VOL"));
        // Geometry got recorded for hit-testing.
        assert_eq!(a.hit_rects.len(), 3);
        assert_eq!(a.stop_rects.len(), 3);
        assert_ne!(a.slider_rect, Rect::ZERO);
        assert_ne!(a.stop_all_rect, Rect::ZERO);
    }

    #[test]
    fn board_screen_draws_a_button_and_label_per_sound() {
        let mut a = app(5, Screen::Board);
        let text = render_to_text(&mut a, 80, 24);
        for i in 0..5 {
            assert!(text.contains(&format!("Sound {i}")), "missing label {i}");
        }
        assert!(text.contains("OMG STOP"));
        assert_eq!(a.hit_rects.len(), 5);
        assert!(a.grid_cols >= 1);
    }

    #[test]
    fn active_counts_and_stop_total_show_up() {
        let mut a = app(2, Screen::List);
        a.apply(FromWorker::Poll(Poll {
            connected: true,
            audio_ok: true,
            counts: vec![3, 0],
            total: 3,
        }));
        let text = render_to_text(&mut a, 60, 12);
        assert!(text.contains("\u{25b6}3"));
        assert!(text.contains("OMG STOP (3)"));
    }

    #[test]
    fn grid_dims_cover_all_sounds_within_bounds() {
        for n in 1..=40 {
            for &(w, h) in &[(40u16, 12u16), (80, 24), (120, 30), (200, 50), (20, 8)] {
                let (cols, rows) = grid_dims(n, w, h);
                assert!(cols >= 1 && cols <= n, "n={n} w={w} h={h} cols={cols}");
                assert!(cols * rows >= n, "n={n}: {cols}x{rows} doesn't cover");
                assert!(cols * (rows - 1) < n, "n={n}: an entirely empty row");
            }
        }
    }

    #[test]
    fn masonry_rows_tile_the_full_width_and_height() {
        let area = Rect::new(2, 3, 61, 22);
        let n = 7;
        let (cols, rows) = grid_dims(n, area.width, area.height);
        let rects = masonry_rects(n, area, cols, rows);
        assert_eq!(rects.len(), n);
        // Every row spans exactly the area width (masonry: partial rows stretch)...
        let mut by_row: std::collections::BTreeMap<u16, Vec<Rect>> = Default::default();
        for r in &rects {
            by_row.entry(r.y).or_default().push(*r);
        }
        for row in by_row.values() {
            let w: u16 = row.iter().map(|r| r.width).sum();
            assert_eq!(w, area.width);
            // ...and cells within a row differ by at most one column.
            let min = row.iter().map(|r| r.width).min().unwrap();
            let max = row.iter().map(|r| r.width).max().unwrap();
            assert!(max - min <= 1);
        }
        let h: u16 = by_row.values().map(|row| row[0].height).sum();
        assert_eq!(h, area.height);
    }

    #[test]
    fn long_lists_scroll_to_the_selection() {
        let mut a = app(30, Screen::List);
        a.selected = 29;
        let text = render_to_text(&mut a, 60, 12);
        assert!(text.contains("Sound 29"));
        assert!(!text.contains("Sound 0 ")); // scrolled past the top
    }

    #[test]
    fn tiny_and_odd_sizes_never_panic() {
        for screen in [Screen::List, Screen::Board] {
            for n in [0usize, 1, 3, 13] {
                for &(w, h) in &[(1u16, 1u16), (2, 2), (5, 3), (8, 4), (12, 5), (13, 2), (3, 20)] {
                    let mut a = app(n, screen);
                    let _ = render_to_text(&mut a, w, h);
                }
            }
        }
    }

    #[test]
    fn flashing_and_error_states_render() {
        let mut a = app(2, Screen::Board);
        a.sounds[0].flash = Some(std::time::Instant::now());
        a.sounds[1] = SoundUi {
            name: "Broken".into(),
            clip: "broken.mp3".into(),
            state: SoundState::Error("ENOENT".into()),
            count: 0,
            flash: None,
        };
        let text = render_to_text(&mut a, 80, 20);
        assert!(text.contains('\u{2717}')); // the error icon
        assert!(text.contains("Broken"));
    }
}
