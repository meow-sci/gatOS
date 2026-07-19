//! ratatui rendering. House rules from the sibling examples: backgrounds stay unset so purrTTY
//! shows the game through the terminal (foreground-only color; only status banners may set a bg),
//! one bordered block, key/value HUD lines on the left, a Braille canvas on the right when there's
//! room.

use ratatui::layout::{Constraint, Direction, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::symbols::Marker;
use ratatui::text::{Line, Span};
use ratatui::widgets::canvas::{Canvas, Line as CLine, Points};
use ratatui::widgets::{Block, Borders, Paragraph};
use ratatui::Frame;

use crate::app::{mmss, App, Screen};
use crate::font;

const KEY: Color = Color::DarkGray;
const VAL: Color = Color::Gray;
const ACCENT: Color = Color::LightCyan;
const OK: Color = Color::LightGreen;
const BAD: Color = Color::LightRed;
const WARN: Color = Color::Yellow;

pub fn render(f: &mut Frame, app: &App) {
    let area = f.area();
    if area.width == 0 || area.height == 0 {
        return;
    }
    let (phase, colr) = match app.screen {
        Screen::Input => ("COMPOSE".to_string(), VAL),
        Screen::Flying => (app.view.phase_label.clone(), ACCENT),
        Screen::Done => match &app.end_state {
            Some(Ok(())) => ("DONE".to_string(), OK),
            Some(Err(_)) => ("ABORTED".to_string(), BAD),
            None => ("…".to_string(), VAL),
        },
    };
    let title = Line::from(vec![
        Span::styled(
            "skycaptain",
            Style::default().fg(ACCENT).add_modifier(Modifier::BOLD),
        ),
        Span::raw(" \u{b7} "),
        Span::styled(
            phase,
            Style::default().fg(colr).add_modifier(Modifier::BOLD),
        ),
    ]);
    let block = Block::default().borders(Borders::ALL).title(title);
    let inner = block.inner(area);
    f.render_widget(block, area);

    match app.screen {
        Screen::Input => render_input(f, inner, app),
        Screen::Flying | Screen::Done => render_flight(f, inner, app),
    }
}

fn kv(k: &str, v: String) -> Line<'static> {
    Line::from(vec![
        Span::styled(format!(" {k:<11}"), Style::default().fg(KEY)),
        Span::styled(v, Style::default().fg(VAL)),
    ])
}

fn render_input(f: &mut Frame, area: Rect, app: &App) {
    let mut lines: Vec<Line> = Vec::new();
    lines.push(Line::from(""));
    lines.push(Line::from(vec![
        Span::styled(" message   ", Style::default().fg(KEY)),
        Span::raw(" "),
    ]));

    // The text being composed, with unsupported characters flagged in red.
    let mut spans = vec![Span::raw("   \u{201c}")];
    for c in app.text.chars() {
        let ok = c == ' ' || font::glyph(c).is_some();
        spans.push(Span::styled(
            c.to_string(),
            if ok {
                Style::default().fg(ACCENT).add_modifier(Modifier::BOLD)
            } else {
                Style::default().fg(BAD).add_modifier(Modifier::CROSSED_OUT)
            },
        ));
    }
    spans.push(Span::styled("\u{258f}", Style::default().fg(VAL)));
    spans.push(Span::raw("\u{201d}"));
    lines.push(Line::from(spans));
    lines.push(Line::from(Span::styled(
        format!(
            "   letters {}  \u{b7}  supported: A-Z 0-9 .,-!?':",
            font::SUPPORTED.len()
        ),
        Style::default().fg(KEY),
    )));
    lines.push(Line::from(""));
    lines.push(kv(
        "canvas",
        format!(
            "letters {:.0} m tall \u{b7} draw {:.0} m/s \u{b7} warp {}\u{d7} paint / {}\u{d7} hop",
            app.info_height, app.info_speed, app.info_warps.0, app.info_warps.1
        ),
    ));
    lines.push(kv(
        "source",
        format!(
            "{}{}",
            app.source_label,
            if app.connected { "" } else { " (unreachable)" }
        ),
    ));
    lines.push(Line::from(""));

    lines.push(Line::from(Span::styled(
        " pre-flight",
        Style::default().fg(KEY),
    )));
    if app.checks.is_empty() {
        lines.push(Line::from(Span::styled(
            "   waiting for telemetry\u{2026}",
            Style::default().fg(VAL),
        )));
    }
    for (label, ok) in &app.checks {
        let (mark, colr) = if *ok {
            ("\u{2713}", OK)
        } else {
            ("\u{2717}", BAD)
        };
        lines.push(Line::from(vec![
            Span::styled(format!("   {mark} "), Style::default().fg(colr)),
            Span::styled(label.clone(), Style::default().fg(VAL)),
        ]));
    }
    if let Some(err) = &app.start_error {
        lines.push(Line::from(""));
        lines.push(Line::from(Span::styled(
            format!(" \u{29c8} {err} "),
            Style::default()
                .fg(Color::Black)
                .bg(WARN)
                .add_modifier(Modifier::BOLD),
        )));
    }
    for l in app.log.iter().rev().take(3) {
        lines.push(Line::from(Span::styled(
            format!("   {l}"),
            Style::default().fg(KEY),
        )));
    }
    lines.push(Line::from(""));
    lines.push(Line::from(Span::styled(
        " type your message \u{b7} Enter write it \u{b7} Esc quit",
        Style::default().fg(KEY),
    )));
    f.render_widget(Paragraph::new(lines), area);
}

fn render_flight(f: &mut Frame, area: Rect, app: &App) {
    let mut lines: Vec<Line> = Vec::new();
    let v = &app.view;

    // The message with per-letter status: done, current (bold + progress), pending.
    let mut spans = vec![Span::raw(" ")];
    for (i, ch) in app.letters.iter().enumerate() {
        let style = match v.cur_letter {
            Some(cur) if i < cur => Style::default().fg(OK),
            Some(cur) if i == cur => Style::default()
                .fg(WARN)
                .add_modifier(Modifier::BOLD | Modifier::UNDERLINED),
            None if matches!(app.end_state, Some(Ok(()))) => Style::default().fg(OK),
            _ => Style::default().fg(KEY),
        };
        spans.push(Span::styled(ch.to_string(), style));
        spans.push(Span::raw(" "));
    }
    if let Some(cur) = v.cur_letter {
        spans.push(Span::styled(
            format!(
                "  {} {:.0}%",
                app.letters.get(cur).copied().unwrap_or(' '),
                v.letter_progress * 100.0
            ),
            Style::default().fg(WARN),
        ));
    }
    lines.push(Line::from(""));
    lines.push(Line::from(spans));
    lines.push(Line::from(""));

    let wall = |game: f64| if v.warp > 0.1 { game / v.warp } else { game };
    lines.push(kv(
        "this letter",
        format!(
            "{} game \u{b7} ~{} wall",
            mmss(v.eta_letter),
            mmss(wall(v.eta_letter))
        ),
    ));
    lines.push(kv(
        "whole text",
        format!(
            "{} game \u{b7} ~{} wall",
            mmss(v.eta_total),
            mmss(wall(v.eta_total))
        ),
    ));
    lines.push(kv(
        "warp",
        if (v.warp - v.warp_wanted).abs() < 0.01 {
            format!("{:.0}\u{d7}", v.warp)
        } else {
            format!("{:.0}\u{d7} \u{2192} {:.0}\u{d7}", v.warp, v.warp_wanted)
        },
    ));
    lines.push(kv("alt radar", format!("{:.0} m", v.alt_radar)));
    lines.push(kv("speed", format!("{:.0} m/s", v.speed)));
    let bars = (v.throttle * 10.0).round() as usize;
    lines.push(Line::from(vec![
        Span::styled(" throttle   ", Style::default().fg(KEY)),
        Span::styled("\u{2588}".repeat(bars), Style::default().fg(ACCENT)),
        Span::styled(
            "\u{2591}".repeat(10 - bars.min(10)),
            Style::default().fg(KEY),
        ),
        Span::styled(
            format!(" {:.0}%", v.throttle * 100.0),
            Style::default().fg(VAL),
        ),
    ]));
    lines.push(kv("propellant", format!("{:.0}%", v.prop_frac * 100.0)));
    lines.push(Line::from(""));

    if let Some(Err(reason)) = &app.end_state {
        lines.push(Line::from(Span::styled(
            format!(" \u{29c8} ABORT: {reason} "),
            Style::default()
                .fg(Color::Black)
                .bg(BAD)
                .add_modifier(Modifier::BOLD),
        )));
    } else if matches!(app.end_state, Some(Ok(()))) {
        lines.push(Line::from(Span::styled(
            " \u{2713} written across the sky \u{b7} vehicle parked on the FC's hover hold \u{2014} it's yours ",
            Style::default()
                .fg(Color::Black)
                .bg(OK)
                .add_modifier(Modifier::BOLD),
        )));
    }
    for l in app.log.iter().rev().take(2) {
        lines.push(Line::from(Span::styled(
            format!(" {l}"),
            Style::default().fg(KEY),
        )));
    }
    lines.push(Line::from(""));
    lines.push(Line::from(Span::styled(
        match app.screen {
            Screen::Done => " n new message \u{b7} q quit",
            _ => " a abort \u{b7} q abort+quit",
        },
        Style::default().fg(KEY),
    )));

    // HUD left, sky canvas right when there's room.
    if !app.outline.is_empty() && area.width >= 70 && area.height >= 10 {
        let cols = Layout::default()
            .direction(Direction::Horizontal)
            .constraints([Constraint::Length(44), Constraint::Min(20)])
            .split(area);
        f.render_widget(Paragraph::new(lines), cols[0]);
        render_sky(f, cols[1], app);
    } else {
        f.render_widget(Paragraph::new(lines), area);
    }
}

/// The sky canvas: planned strokes dim, hop arcs dimmer, painted trail bright, pen marker.
fn render_sky(f: &mut Frame, area: Rect, app: &App) {
    let (mut min_a, mut max_a, mut min_b, mut max_b) = (f64::MAX, f64::MIN, f64::MAX, f64::MIN);
    for path in &app.outline {
        for &(a, b) in path {
            min_a = min_a.min(a);
            max_a = max_a.max(a);
            min_b = min_b.min(b);
            max_b = max_b.max(b);
        }
    }
    if min_a >= max_a || min_b >= max_b {
        return;
    }
    // Equal-scale bounds for the braille grid (2 dots/col, 4 dots/row).
    let px_w = (area.width.max(1) as f64) * 2.0;
    let px_h = (area.height.max(1) as f64) * 4.0;
    let (mut a0, mut a1, mut b0, mut b1) = (min_a, max_a, min_b, max_b);
    let pad_a = (a1 - a0) * 0.06;
    let pad_b = (b1 - b0) * 0.06;
    a0 -= pad_a;
    a1 += pad_a;
    b0 -= pad_b;
    b1 += pad_b;
    let m_per_px = ((a1 - a0) / px_w).max((b1 - b0) / px_h);
    let (ca, cb) = ((a0 + a1) / 2.0, (b0 + b1) / 2.0);
    let (ha, hb) = (m_per_px * px_w / 2.0, m_per_px * px_h / 2.0);

    let canvas = Canvas::default()
        .marker(Marker::Braille)
        .x_bounds([ca - ha, ca + ha])
        .y_bounds([cb - hb, cb + hb])
        .paint(|ctx| {
            for path in &app.outline {
                for w in path.windows(2) {
                    ctx.draw(&CLine {
                        x1: w[0].0,
                        y1: w[0].1,
                        x2: w[1].0,
                        y2: w[1].1,
                        color: KEY,
                    });
                }
            }
            let pts: Vec<(f64, f64)> = app.painted.clone();
            ctx.draw(&Points {
                coords: &pts,
                color: ACCENT,
            });
            let pen = app.view.pen;
            ctx.draw(&Points {
                coords: &[pen],
                color: WARN,
            });
        });
    f.render_widget(canvas, area);
}
