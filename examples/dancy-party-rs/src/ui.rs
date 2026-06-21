//! Rendering. **Transparency is the rule** (the project-wide TUI discipline): cell backgrounds are
//! left unset so purrTTY shows the live game through the text; only the top/bottom bars and the modal
//! popups get a subtle background. Color swatches are drawn as foreground block glyphs (`████`) in the
//! true color, so they read against the game without painting a box. The render pass also records the
//! interactive rects (vessel rows, per-color buttons, the time stepper, the party toggle, modal lists)
//! back onto [`App`] for the next mouse event.

use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Clear, Paragraph};
use ratatui::Frame;

use crate::app::{App, Focus, Modal, Screen};
use crate::color::{self, Rgb};
use crate::xkcd::XKCD;

const TITLE: Color = Color::Magenta;
const LABEL: Color = Color::DarkGray;
const VALUE: Color = Color::White;
const GOOD: Color = Color::Green;
const BAD: Color = Color::Red;
const ACCENT: Color = Color::LightMagenta;
const FOCUS: Color = Color::LightCyan;
const BAR_BG: Color = Color::Rgb(20, 16, 32);

const SWATCH: &str = "\u{2588}\u{2588}\u{2588}\u{2588}"; // ████

pub fn render(f: &mut Frame, app: &mut App) {
    let chunks = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(1),
    ])
    .split(f.area());

    render_title(f, app, chunks[0]);
    match app.screen {
        Screen::Vessels => render_vessels(f, app, chunks[1]),
        Screen::Party => render_party(f, app, chunks[1]),
    }
    render_status(f, app, chunks[2]);

    match &app.modal {
        Modal::AddColor(_) => render_add_color(f, app),
        Modal::Xkcd(_) => render_xkcd(f, app),
        Modal::Time(_) => render_time(f, app),
        Modal::None => {}
    }
}

// ---- title bar ----------------------------------------------------------------------------------

fn render_title(f: &mut Frame, app: &App, area: Rect) {
    let conn = if app.connected {
        Span::styled("\u{25cf} online", Style::new().fg(GOOD))
    } else {
        Span::styled("\u{25cf} offline", Style::new().fg(BAD))
    };
    let mut spans = vec![
        Span::styled(
            " \u{1f483} DANCY PARTY ",
            Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
        ),
        kv(
            "screen",
            match app.screen {
                Screen::Vessels => "vessels",
                Screen::Party => "party",
            },
        ),
    ];
    match app.screen {
        Screen::Vessels => {
            let armed = app.vessels.iter().filter(|v| v.selected).count();
            spans.push(kv("armed", &format!("{armed}/{}", app.vessels.len())));
        }
        Screen::Party => {
            spans.push(kv("colors", &app.colors.len().to_string()));
            spans.push(kv("per", &format!("{}ms", app.per_ms)));
            spans.push(kv("hz", &fmt_hz(app.hz)));
            spans.push(kv("steps", &fmt_steps(app.steps)));
            if app.stagger_ms > 0.0 {
                spans.push(kv("stag", &format!("{}ms", app.stagger_ms as u64)));
            }
            spans.push(kv(
                "wr",
                &if app.write_cfg.async_writes {
                    format!("async\u{00d7}{}", app.write_cfg.writers)
                } else {
                    "sync".into()
                },
            ));
            spans.push(if app.partying {
                Span::styled(
                    "\u{1f389} PARTY ",
                    Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
                )
            } else {
                Span::styled("idle ", Style::new().fg(LABEL))
            });
        }
    }
    spans.push(ctl_badge("ctl", app.control));
    spans.push(Span::raw(" "));
    spans.push(conn);
    spans.push(kv("  src", &app.label));
    f.render_widget(
        Paragraph::new(Line::from(spans)).style(Style::new().bg(BAR_BG)),
        area,
    );
}

// ---- vessel select screen -----------------------------------------------------------------------

fn render_vessels(f: &mut Frame, app: &mut App, area: Rect) {
    app.vessel_rects.clear();
    if app.vessels.is_empty() {
        let msg = if app.discovering {
            "scanning the /sim vessel tree\u{2026}"
        } else {
            "no vessels \u{2014} start a flight in KSA, then press  r  to rescan"
        };
        center_line(f, area, msg, LABEL);
        return;
    }

    let sel = app.vsel.min(app.vessels.len() - 1);
    let rows_visible = area.height as usize;
    let start = sel.saturating_sub(rows_visible.saturating_sub(1));
    for (row, idx) in (start..app.vessels.len()).take(rows_visible).enumerate() {
        let v = &app.vessels[idx];
        let rect = Rect::new(area.x, area.y + row as u16, area.width, 1);
        app.vessel_rects.push((rect, idx));

        let focused = idx == sel;
        let marker = if focused { "\u{25b6} " } else { "  " };
        let (box_txt, box_col) = if v.selected {
            ("[x]", GOOD)
        } else {
            ("[ ]", LABEL)
        };
        let id_style = if v.lights == 0 {
            Style::new().fg(LABEL)
        } else if focused {
            Style::new().fg(FOCUS).add_modifier(Modifier::BOLD)
        } else {
            Style::new().fg(VALUE)
        };
        let lights = if v.lights == 0 {
            Span::styled("no lights".to_string(), Style::new().fg(LABEL))
        } else {
            Span::styled(
                format!("{} light{}", v.lights, if v.lights == 1 { "" } else { "s" }),
                Style::new().fg(ACCENT),
            )
        };
        let line = Line::from(vec![
            Span::styled(marker, Style::new().fg(ACCENT)),
            Span::styled(box_txt, Style::new().fg(box_col).add_modifier(Modifier::BOLD)),
            Span::raw(" "),
            Span::styled(format!("{:<20}", trunc(&v.id, 20)), id_style),
            Span::raw(" "),
            lights,
        ]);
        f.render_widget(Paragraph::new(line), rect);
    }
}

// ---- party screen -------------------------------------------------------------------------------

fn render_party(f: &mut Frame, app: &mut App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    let chunks = Layout::vertical([
        Constraint::Min(3),    // palette
        Constraint::Length(1), // time
        Constraint::Length(1), // write-pipeline perf readout
        Constraint::Length(1), // live preview band
        Constraint::Length(1), // buttons
    ])
    .split(area);

    render_palette(f, app, chunks[0]);
    render_time_row(f, app, chunks[1]);
    render_perf_row(f, app, chunks[2]);
    render_live_band(f, app, chunks[3]);
    render_buttons(f, app, chunks[4]);
}

/// The write-pipeline readout — the whole point of the perf knobs. While partying it shows the actual
/// measured per-write latency, the write throughput, and (in async mode) the in-flight backlog and
/// dropped-broadcast count. Idle, it just restates the active tuning.
fn render_perf_row(f: &mut Frame, app: &App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    let mut spans = vec![Span::styled("writes ", Style::new().fg(LABEL))];
    match app.perf {
        Some(p) if app.partying => {
            spans.push(Span::styled(
                format!("{} ", p.writes),
                Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
            ));
            spans.push(Span::styled(
                format!("\u{b7} lat avg {} max {} last {} ", us(p.avg_us), us(p.max_us), us(p.last_us)),
                Style::new().fg(VALUE),
            ));
            if p.async_writes {
                let backlog_col = if p.inflight > 0 { ACCENT } else { LABEL };
                spans.push(Span::styled(
                    format!("\u{b7} {}-pool backlog {} ", p.writers, p.inflight),
                    Style::new().fg(backlog_col),
                ));
                let drop_col = if p.dropped > 0 { BAD } else { LABEL };
                spans.push(Span::styled(
                    format!("\u{b7} dropped {} ", p.dropped),
                    Style::new().fg(drop_col),
                ));
            }
        }
        _ => {
            spans.push(Span::styled(
                format!(
                    "idle \u{b7} steps {} \u{b7} stagger {}ms \u{b7} {} \u{b7} {} hz",
                    fmt_steps(app.steps),
                    app.stagger_ms as u64,
                    if app.write_cfg.async_writes {
                        format!("async\u{00d7}{}", app.write_cfg.writers)
                    } else {
                        "sync".into()
                    },
                    fmt_hz(app.hz),
                ),
                Style::new().fg(LABEL),
            ));
        }
    }
    f.render_widget(Paragraph::new(Line::from(spans)), area);
}

fn render_palette(f: &mut Frame, app: &mut App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    app.color_rects.clear();
    app.up_btns.clear();
    app.down_btns.clear();
    app.del_btns.clear();

    let border = if app.focus == Focus::Colors {
        FOCUS
    } else {
        LABEL
    };
    let block = Block::bordered()
        .border_style(Style::new().fg(border))
        .title(Span::styled(
            " palette ",
            Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " a RGB/hex \u{b7} f XKCD \u{b7} [ ] reorder \u{b7} d remove ",
            Style::new().fg(LABEL),
        ));
    let inner = block.inner(area);
    f.render_widget(block, area);
    if inner.width == 0 || inner.height == 0 {
        return;
    }

    if app.colors.is_empty() {
        center_line(
            f,
            inner,
            "empty palette \u{2014} press  a  (RGB/hex) or  f  (XKCD picker)",
            LABEL,
        );
        return;
    }

    let sel = app.csel.min(app.colors.len() - 1);
    let visible = inner.height as usize;
    let start = sel.saturating_sub(visible.saturating_sub(1));
    for (row, idx) in (start..app.colors.len()).take(visible).enumerate() {
        let c = app.colors[idx];
        let y = inner.y + row as u16;
        let focused = idx == sel && app.focus == Focus::Colors;

        let marker_w = 2u16;
        let up = Rect::new(inner.x + marker_w, y, 3, 1);
        let down = Rect::new(inner.x + marker_w + 4, y, 3, 1);
        let del = Rect::new(inner.x + marker_w + 8, y, 3, 1);
        app.up_btns.push((up, idx));
        app.down_btns.push((down, idx));
        app.del_btns.push((del, idx));
        app.color_rects.push((Rect::new(inner.x, y, inner.width, 1), idx));

        let marker = if focused { "\u{25b6} " } else { "  " };
        let line = Line::from(vec![
            Span::styled(marker, Style::new().fg(ACCENT)),
            Span::styled("[\u{2191}]", Style::new().fg(ACCENT)),
            Span::raw(" "),
            Span::styled("[\u{2193}]", Style::new().fg(ACCENT)),
            Span::raw(" "),
            Span::styled("[\u{2715}]", Style::new().fg(BAD)),
            Span::raw("  "),
            Span::styled(SWATCH, Style::new().fg(c.to_term())),
            Span::raw("  "),
            Span::styled(
                format!("{:>3}", idx + 1),
                Style::new().fg(LABEL),
            ),
            Span::raw("  "),
            Span::styled(
                c.to_hex(),
                if focused {
                    Style::new().fg(FOCUS).add_modifier(Modifier::BOLD)
                } else {
                    Style::new().fg(VALUE)
                },
            ),
            Span::raw("  "),
            Span::styled(format!("rgb {}", rgb255(c)), Style::new().fg(LABEL)),
        ]);
        f.render_widget(Paragraph::new(line), Rect::new(inner.x, y, inner.width, 1));
    }
}

fn render_time_row(f: &mut Frame, app: &mut App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    let focused = app.focus == Focus::Time;
    let label_style = if focused {
        Style::new().fg(FOCUS).add_modifier(Modifier::BOLD)
    } else {
        Style::new().fg(VALUE)
    };
    let minus = Rect::new(area.x + 16, area.y, 3, 1);
    let plus = Rect::new(area.x + 16 + 4 + 9, area.y, 3, 1);
    app.time_minus = minus;
    app.time_plus = plus;

    let line = Line::from(vec![
        Span::styled("time per color ", label_style),
        Span::styled("[-]", Style::new().fg(ACCENT)),
        Span::styled(
            format!(" {:>5} ms ", app.per_ms),
            Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
        ),
        Span::styled("[+]", Style::new().fg(ACCENT)),
        Span::styled("   (e to type)", Style::new().fg(LABEL)),
    ]);
    f.render_widget(Paragraph::new(line), area);
}

/// A full-width band of the live interpolated color while partying — the thing your eyes are
/// (probably) being assaulted by, mirrored in the terminal. Empty when idle.
fn render_live_band(f: &mut Frame, app: &App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    if let Some(live) = app.live {
        let label = format!(" LIVE  {}  seg {} goal {} ", live.color.to_hex(), live.segment, live.goal);
        let bar = "\u{2588}".repeat(area.width as usize);
        f.render_widget(
            Paragraph::new(Span::styled(bar, Style::new().fg(live.color.to_term()))),
            area,
        );
        // Overlay the readout in a contrasting style on top of the band's left edge.
        let w = (label.chars().count() as u16).min(area.width);
        f.render_widget(
            Paragraph::new(Span::styled(
                label,
                Style::new().fg(Color::Black).bg(live.color.to_term()),
            )),
            Rect::new(area.x, area.y, w, 1),
        );
    }
}

fn render_buttons(f: &mut Frame, app: &mut App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    let back = "[ \u{2039} back ]";
    let back_rect = Rect::new(area.x, area.y, back.chars().count() as u16, 1);
    app.back_btn = back_rect;
    f.render_widget(
        Paragraph::new(Span::styled(back, Style::new().fg(LABEL))),
        back_rect,
    );

    let (label, col) = if app.partying {
        ("[ STOP, MY EYES ]", BAD)
    } else {
        ("[ LETS PARTY! ]", GOOD)
    };
    let focused = app.focus == Focus::Button;
    let style = if focused {
        Style::new().fg(col).bg(Color::Rgb(40, 40, 40)).add_modifier(Modifier::BOLD)
    } else {
        Style::new().fg(col).add_modifier(Modifier::BOLD)
    };
    let w = label.chars().count() as u16;
    let right = area.x + area.width;
    // Center the button, but keep it clear of the back link, and never let it spill past the edge.
    let x = (area.x + area.width.saturating_sub(w) / 2)
        .max(back_rect.x + back_rect.width)
        .min(right.saturating_sub(1));
    let width = w.min(right.saturating_sub(x));
    if width == 0 {
        return;
    }
    let rect = Rect::new(x, area.y, width, 1);
    app.party_btn = rect;
    f.render_widget(Paragraph::new(Span::styled(label, style)), rect);
}

// ---- status bar ---------------------------------------------------------------------------------

fn render_status(f: &mut Frame, app: &App, area: Rect) {
    let (msg, color) = if app.status_err {
        (app.status.clone(), BAD)
    } else {
        (app.status.clone(), GOOD)
    };
    let keys = match (app.screen, &app.modal) {
        (_, Modal::None) if app.screen == Screen::Vessels => {
            "  \u{b7}  \u{2191}\u{2193} move \u{b7} space arm \u{b7} a all \u{b7} r rescan \u{b7} Enter party \u{b7} q quit"
        }
        (_, Modal::None) => {
            "  \u{b7}  Tab focus \u{b7} Enter/P party \u{b7} b back \u{b7} q quit"
        }
        _ => "",
    };
    let line = Line::from(vec![
        Span::styled(format!(" {msg}"), Style::new().fg(color)),
        Span::styled(keys, Style::new().fg(LABEL)),
    ]);
    f.render_widget(Paragraph::new(line).style(Style::new().bg(BAR_BG)), area);
}

// ---- modals -------------------------------------------------------------------------------------

fn render_add_color(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 52u16.min(screen.width.saturating_sub(2));
    let height = 6u16.min(screen.height.saturating_sub(2));
    let popup = centered(screen, width, height);
    f.render_widget(Clear, popup);

    let preview = color::parse(match &app.modal {
        Modal::AddColor(m) => &m.text,
        _ => "",
    });

    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " add color ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " Enter add \u{b7} Tab \u{2192} XKCD \u{b7} Esc cancel ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::AddColor(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    if inner.height == 0 {
        return;
    }

    f.render_widget(
        Paragraph::new(Span::styled(
            "RGB 0-255 (255 128 0) or hex (#ff8000)",
            Style::new().fg(LABEL),
        )),
        Rect::new(inner.x, inner.y, inner.width, 1),
    );
    f.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled("> ", Style::new().fg(TITLE)),
            Span::styled(m.text.clone(), Style::new().fg(VALUE).add_modifier(Modifier::BOLD)),
            Span::styled("\u{2588}", Style::new().fg(LABEL)),
        ])),
        Rect::new(inner.x, inner.y + 1, inner.width, 1),
    );
    let preview_line = match preview {
        Some(c) => Line::from(vec![
            Span::styled(SWATCH, Style::new().fg(c.to_term())),
            Span::raw("  "),
            Span::styled(c.to_hex(), Style::new().fg(VALUE)),
            Span::raw("  "),
            Span::styled(format!("rgb {}", rgb255(c)), Style::new().fg(LABEL)),
        ]),
        None => Line::from(Span::styled(
            "\u{2026} type a color",
            Style::new().fg(LABEL),
        )),
    };
    if inner.height >= 3 {
        f.render_widget(Paragraph::new(preview_line), Rect::new(inner.x, inner.y + 2, inner.width, 1));
    }
}

fn render_xkcd(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = (screen.width * 3 / 4).max(28).min(screen.width.saturating_sub(2));
    let height = (screen.height * 4 / 5).max(6).min(screen.height.saturating_sub(2));
    let popup = centered(screen, width, height);
    f.render_widget(Clear, popup);

    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " XKCD colors ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " fuzzy (space = AND) \u{b7} \u{2191}\u{2193} \u{b7} Enter add \u{b7} Esc close ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::Xkcd(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    m.item_rects.clear();
    if inner.height < 2 || inner.width == 0 {
        return;
    }

    // Input row, with a swatch of the highlighted color on the right.
    let preview = m.filtered.get(m.selected).map(|&i| Rgb::from_f32(XKCD[i].1));
    let mut input_spans = vec![
        Span::styled("\u{1f50d} ", Style::new().fg(TITLE)),
        Span::styled(m.query.clone(), Style::new().fg(VALUE).add_modifier(Modifier::BOLD)),
        Span::styled("\u{2588}", Style::new().fg(LABEL)),
    ];
    if let Some(c) = preview {
        input_spans.push(Span::raw("   "));
        input_spans.push(Span::styled(SWATCH, Style::new().fg(c.to_term())));
        input_spans.push(Span::styled(format!(" {}", c.to_hex()), Style::new().fg(LABEL)));
    }
    f.render_widget(
        Paragraph::new(Line::from(input_spans)),
        Rect::new(inner.x, inner.y, inner.width, 1),
    );

    let list = Rect::new(inner.x, inner.y + 1, inner.width, inner.height - 1);
    let visible = list.height as usize;
    if m.selected < m.offset {
        m.offset = m.selected;
    } else if visible > 0 && m.selected >= m.offset + visible {
        m.offset = m.selected + 1 - visible;
    }
    for row in 0..list.height {
        let idx = m.offset + row as usize;
        if idx >= m.filtered.len() {
            break;
        }
        let entry = m.filtered[idx];
        let (name, rgb) = (XKCD[entry].0, Rgb::from_f32(XKCD[entry].1));
        let rect = Rect::new(list.x, list.y + row, list.width, 1);
        m.item_rects.push((rect, idx));
        let focused = idx == m.selected;
        let (marker, base) = if focused {
            ("\u{25b6} ", FOCUS)
        } else {
            ("  ", VALUE)
        };
        let line = Line::from(vec![
            Span::styled(marker, Style::new().fg(ACCENT)),
            Span::styled(SWATCH, Style::new().fg(rgb.to_term())),
            Span::raw("  "),
            Span::styled(
                format!("{:<26}", trunc(&color::humanize(name), 26)),
                Style::new().fg(base),
            ),
            Span::styled(rgb.to_hex(), Style::new().fg(LABEL)),
        ]);
        f.render_widget(Paragraph::new(line), rect);
    }
}

fn render_time(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 40u16.min(screen.width.saturating_sub(2));
    let height = 5u16.min(screen.height.saturating_sub(2));
    let popup = centered(screen, width, height);
    f.render_widget(Clear, popup);

    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " time per color (ms) ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " Enter set \u{b7} Esc cancel ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::Time(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    if inner.height == 0 {
        return;
    }
    f.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled("> ", Style::new().fg(TITLE)),
            Span::styled(m.text.clone(), Style::new().fg(VALUE).add_modifier(Modifier::BOLD)),
            Span::styled("\u{2588}", Style::new().fg(LABEL)),
        ])),
        Rect::new(inner.x, inner.y, inner.width, 1),
    );
}

// ---- helpers ------------------------------------------------------------------------------------

fn centered(screen: Rect, width: u16, height: u16) -> Rect {
    Rect {
        x: screen.x + screen.width.saturating_sub(width) / 2,
        y: screen.y + screen.height.saturating_sub(height) / 2,
        width,
        height,
    }
}

fn center_line(f: &mut Frame, area: Rect, text: &str, color: Color) {
    if area.height == 0 {
        return;
    }
    let y = area.y + area.height / 2;
    f.render_widget(
        Paragraph::new(Span::styled(text.to_string(), Style::new().fg(color))).centered(),
        Rect::new(area.x, y, area.width, 1),
    );
}

fn kv<'a>(k: &'a str, v: &str) -> Span<'a> {
    Span::styled(format!("{k} {v}  "), Style::new().fg(VALUE))
}

fn ctl_badge(name: &str, on: bool) -> Span<'static> {
    if on {
        Span::styled(format!("{name}:on"), Style::new().fg(GOOD))
    } else {
        Span::styled(format!("{name}:off"), Style::new().fg(LABEL))
    }
}

fn rgb255(c: Rgb) -> String {
    let q = |x: f64| (x.clamp(0.0, 1.0) * 255.0).round() as u8;
    format!("{} {} {}", q(c.r), q(c.g), q(c.b))
}

fn fmt_hz(hz: f64) -> String {
    if (hz - hz.round()).abs() < 1e-6 {
        format!("{}", hz.round() as i64)
    } else {
        format!("{hz:.1}")
    }
}

fn fmt_steps(steps: u32) -> String {
    if steps == 0 {
        "cont".into()
    } else {
        steps.to_string()
    }
}

/// Formats a microsecond latency compactly — `µs` under a millisecond, `ms` above.
fn us(micros: u64) -> String {
    if micros < 1000 {
        format!("{micros}\u{00b5}s")
    } else {
        format!("{:.1}ms", micros as f64 / 1000.0)
    }
}

fn trunc(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        s.to_string()
    } else {
        s.chars().take(max.saturating_sub(1)).chain(['\u{2026}']).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::source::{FromWorker, Health, VesselLights};
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;
    use std::sync::mpsc;

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

    fn app() -> App {
        let (tx, _rx) = mpsc::channel();
        App::new(tx, "fs:/sim".into(), 60.0, 0, 0.0, crate::source::WriteConfig::default())
    }

    #[test]
    fn vessel_screen_lists_vessels_and_lights() {
        let mut a = app();
        a.apply(FromWorker::Catalog {
            vessels: vec![VesselLights {
                id: "Hunter".into(),
                color_paths: vec!["a".into(), "b".into()],
                goal_paths: vec!["g".into()],
            }],
            health: Health {
                connected: true,
                control: true,
            },
        });
        let text = render_to_text(&mut a, 70, 12);
        assert!(text.contains("Hunter"));
        assert!(text.contains("2 lights"));
        assert!(text.contains("DANCY PARTY"));
    }

    #[test]
    fn party_screen_shows_toggle_label() {
        let mut a = app();
        a.screen = Screen::Party;
        let off = render_to_text(&mut a, 70, 14);
        assert!(off.contains("LETS PARTY!"));
        a.partying = true;
        let on = render_to_text(&mut a, 70, 14);
        assert!(on.contains("STOP, MY EYES"));
    }

    #[test]
    fn tiny_sizes_never_panic() {
        for &(w, h) in &[(1, 1), (4, 3), (10, 5), (20, 8), (40, 12)] {
            let mut a = app();
            a.screen = Screen::Party;
            let _ = render_to_text(&mut a, w, h);
            a.open_xkcd();
            let _ = render_to_text(&mut a, w, h);
        }
    }
}
