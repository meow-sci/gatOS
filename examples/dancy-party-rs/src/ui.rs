//! Rendering. **Transparency is the rule** (the project-wide TUI discipline): cell backgrounds are
//! left unset so purrTTY shows the live game through the text; only the top/bottom bars and the modal
//! popups get a subtle background. Color swatches are drawn as foreground block glyphs (`████`) in the
//! true color, and the battery bar as a colored foreground block run, so they read against the game
//! without painting a box. The render pass also records the interactive rects (vessel rows, per-color
//! buttons, the time stepper, the refill / settings / party buttons, modal lists) back onto [`App`]
//! for the next mouse event.

use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Clear, Paragraph};
use ratatui::Frame;

use crate::app::{App, Focus, Modal, Screen, Settings, SETTING_ROWS};
use crate::color::{self, Rgb};
use crate::xkcd::XKCD;

/// One contiguous segment of the hide-mode bar, tagged so the layout pass can record the clickable
/// button rects (everything else is `None`).
enum HideTag {
    None,
    Party,
    Refill,
    Show,
}

const TITLE: Color = Color::Magenta;
const LABEL: Color = Color::DarkGray;
const VALUE: Color = Color::White;
const GOOD: Color = Color::Green;
const WARN: Color = Color::Yellow;
const BAD: Color = Color::Red;
const ACCENT: Color = Color::LightMagenta;
const FOCUS: Color = Color::LightCyan;
const EMPTY: Color = Color::DarkGray; // the unfilled run of a fg bar
const BAR_BG: Color = Color::Rgb(20, 16, 32);

const SWATCH: &str = "\u{2588}\u{2588}\u{2588}\u{2588}"; // ████

pub fn render(f: &mut Frame, app: &mut App) {
    // Hide mode collapses everything to a single status bar so the party doesn't block the game. The
    // quit confirmation still overlays it (it can be triggered by `q` from the hidden bar).
    if app.hidden {
        render_hidden(f, app);
        if matches!(app.modal, Modal::ConfirmQuit(_)) {
            render_confirm_quit(f, app);
        }
        return;
    }

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
        Modal::Settings(_) => render_settings(f, app),
        Modal::SaveProfile(_) => render_save_profile(f, app),
        Modal::ConfirmQuit(_) => render_confirm_quit(f, app),
        Modal::None => {}
    }
}

// ---- hide mode (status-bar-only overlay) --------------------------------------------------------

/// The whole UI collapsed to one bar on the top row: title, party state, the battery meter, and the
/// three live buttons (refill / party toggle / show). Everything else is left blank — and blanks are
/// transparent — so the game shows through while a party runs. Records the button rects.
fn render_hidden(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    app.party_btn = Rect::default();
    app.refill_btn = Rect::default();
    app.hide_show_btn = Rect::default();
    if screen.height == 0 || screen.width == 0 {
        return;
    }
    let bar = Rect::new(screen.x, screen.y, screen.width, 1);

    // Build the bar as contiguous tagged segments, then assign each button its rect from the running
    // x offset (the segments abut, so widths sum exactly).
    let mut items: Vec<(Span, HideTag)> = vec![
        (
            Span::styled(
                " \u{1f483} DANCY ",
                Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
            ),
            HideTag::None,
        ),
        if app.partying {
            (
                Span::styled(
                    "\u{1f389} PARTY ",
                    Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
                ),
                HideTag::None,
            )
        } else {
            (Span::styled("idle ", Style::new().fg(LABEL)), HideTag::None)
        },
        (label("\u{b7} battery "), HideTag::None),
    ];

    match app.battery.fraction {
        Some(frac) if app.battery.count > 0 => {
            items.push((pct_span(frac), HideTag::None));
            items.push((Span::raw(" "), HideTag::None));
            for s in bar_spans(frac, 8, level_color(frac)) {
                items.push((s, HideTag::None));
            }
        }
        _ => items.push((label("\u{2014} none"), HideTag::None)),
    }

    items.push((Span::raw("  "), HideTag::None));
    items.push((
        Span::styled(
            " [ refill \u{26a1} ] ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ),
        HideTag::Refill,
    ));
    items.push((Span::raw(" "), HideTag::None));
    let (party_txt, party_col) = if app.partying {
        (" [ STOP, MY EYES ] ", BAD)
    } else {
        (" [ LETS PARTY! ] ", GOOD)
    };
    items.push((
        Span::styled(party_txt, Style::new().fg(party_col).add_modifier(Modifier::BOLD)),
        HideTag::Party,
    ));
    items.push((Span::raw(" "), HideTag::None));
    items.push((Span::styled(" [ h show ] ", Style::new().fg(LABEL)), HideTag::Show));

    let right = bar.x + bar.width;
    let mut x = bar.x;
    for (span, tag) in &items {
        let w = span.content.chars().count() as u16;
        let sx = x.min(right);
        let rect = Rect::new(sx, bar.y, w.min(right.saturating_sub(sx)), 1);
        match tag {
            HideTag::Party => app.party_btn = rect,
            HideTag::Refill => app.refill_btn = rect,
            HideTag::Show => app.hide_show_btn = rect,
            HideTag::None => {}
        }
        x = x.saturating_add(w);
    }

    let line = Line::from(items.into_iter().map(|(s, _)| s).collect::<Vec<_>>());
    f.render_widget(Paragraph::new(line).style(Style::new().bg(BAR_BG)), bar);
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
            let s = &app.settings;
            spans.push(kv("colors", &app.colors.len().to_string()));
            spans.push(kv("color", &format!("{}ms", s.color_ms)));
            spans.push(kv("anim", &format!("{}ms", s.anim_ms)));
            spans.push(kv("hz", &fmt_hz(s.hz)));
            spans.push(kv("steps", &fmt_steps(s.steps)));
            if s.color_stagger_ms > 0.0 || s.anim_stagger_ms > 0.0 {
                spans.push(kv(
                    "stag",
                    &format!("{}/{}ms", s.color_stagger_ms as u64, s.anim_stagger_ms as u64),
                ));
            }
            if app.partying {
                spans.push(kv("wr", &format!("{} ({})", app.writes, app.inflight)));
                spans.push(Span::styled(
                    "\u{1f389} PARTY ",
                    Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
                ));
            } else {
                spans.push(Span::styled("idle ", Style::new().fg(LABEL)));
            }
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
        Constraint::Length(1), // time row
        Constraint::Length(1), // battery + refill
        Constraint::Length(1), // live preview band
        Constraint::Length(1), // buttons
    ])
    .split(area);

    render_palette(f, app, chunks[0]);
    render_time_row(f, app, chunks[1]);
    render_battery_row(f, app, chunks[2]);
    render_live_band(f, app, chunks[3]);
    render_buttons(f, app, chunks[4]);
}

/// The battery meter — an aggregate charge bar across the armed vessels, with a `[ refill ⚡ ]`
/// button anchored at the right edge of the row (records `app.refill_btn`).
fn render_battery_row(f: &mut Frame, app: &mut App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    let btn_txt = " [ refill \u{26a1} ] ";
    let bw = (btn_txt.chars().count() as u16).min(area.width);
    let btn_rect = Rect::new(area.x + area.width.saturating_sub(bw), area.y, bw, 1);
    app.refill_btn = btn_rect;
    f.render_widget(
        Paragraph::new(Span::styled(
            btn_txt,
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        )),
        btn_rect,
    );

    let left = Rect::new(area.x, area.y, area.width.saturating_sub(bw), 1);
    if left.width == 0 {
        return;
    }
    let bar_w = (left.width.saturating_sub(18)).min(24) as usize;
    let mut spans = vec![label("battery ")];
    match app.battery.fraction {
        Some(frac) if app.battery.count > 0 => {
            spans.push(pct_span(frac));
            spans.push(Span::raw(" "));
            if bar_w > 0 {
                spans.extend(bar_spans(frac, bar_w, level_color(frac)));
            }
            if app.battery.count > 1 {
                spans.push(Span::styled(
                    format!(" avg/{}", app.battery.count),
                    Style::new().fg(LABEL),
                ));
            }
        }
        _ => spans.push(Span::styled(
            "\u{2014} no battery on armed vessel(s)",
            Style::new().fg(LABEL),
        )),
    }
    f.render_widget(Paragraph::new(Line::from(spans)), left);
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
            Span::styled(format!("{:>3}", idx + 1), Style::new().fg(LABEL)),
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
            format!(" {:>5} ms ", app.settings.color_ms),
            Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
        ),
        Span::styled("[+]", Style::new().fg(ACCENT)),
        Span::styled("   (e type \u{b7} s settings)", Style::new().fg(LABEL)),
    ]);
    f.render_widget(Paragraph::new(line), area);
}

/// A full-width band of the live interpolated color while partying, with the two independent clock
/// segments + goal. Empty when idle.
fn render_live_band(f: &mut Frame, app: &App, area: Rect) {
    if area.height == 0 || area.width == 0 {
        return;
    }
    if let Some(live) = app.live {
        let labeltxt = format!(
            " LIVE  {}  color {}  anim {}  goal {} ",
            live.color.to_hex(),
            live.color_segment,
            live.anim_segment,
            live.goal
        );
        let bar = "\u{2588}".repeat(area.width as usize);
        f.render_widget(
            Paragraph::new(Span::styled(bar, Style::new().fg(live.color.to_term()))),
            area,
        );
        let w = (labeltxt.chars().count() as u16).min(area.width);
        f.render_widget(
            Paragraph::new(Span::styled(
                labeltxt,
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
    let right = area.x + area.width;
    let mut x = area.x;

    // Back (left).
    let back = "[ \u{2039} back ]";
    let back_w = (back.chars().count() as u16).min(right.saturating_sub(x));
    let back_rect = Rect::new(x, area.y, back_w, 1);
    app.back_btn = back_rect;
    f.render_widget(
        Paragraph::new(Span::styled(back, Style::new().fg(LABEL))),
        back_rect,
    );
    x = back_rect.x + back_rect.width + 1;

    // Settings (next to back).
    let setg = "[ settings ]";
    let setg_w = (setg.chars().count() as u16).min(right.saturating_sub(x.min(right)));
    let setg_rect = Rect::new(x.min(right), area.y, setg_w, 1);
    app.settings_btn = setg_rect;
    if setg_w > 0 {
        f.render_widget(
            Paragraph::new(Span::styled(
                setg,
                Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
            )),
            setg_rect,
        );
    }

    // Party toggle (centered, but kept clear of the settings button and the right edge).
    let (label_txt, col) = if app.partying {
        ("[ STOP, MY EYES ]", BAD)
    } else {
        ("[ LETS PARTY! ]", GOOD)
    };
    let focused = app.focus == Focus::Button;
    let style = if focused {
        Style::new()
            .fg(col)
            .bg(Color::Rgb(40, 40, 40))
            .add_modifier(Modifier::BOLD)
    } else {
        Style::new().fg(col).add_modifier(Modifier::BOLD)
    };
    let w = label_txt.chars().count() as u16;
    let guard = setg_rect.x + setg_rect.width + 1;
    let px = (area.x + area.width.saturating_sub(w) / 2)
        .max(guard)
        .min(right.saturating_sub(1));
    let pw = w.min(right.saturating_sub(px));
    if pw == 0 {
        app.party_btn = Rect::default();
        return;
    }
    let rect = Rect::new(px, area.y, pw, 1);
    app.party_btn = rect;
    f.render_widget(Paragraph::new(Span::styled(label_txt, style)), rect);
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
            "  \u{b7}  Tab focus \u{b7} Enter/P party \u{b7} g refill \u{b7} s settings \u{b7} w save \u{b7} h hide \u{b7} b back \u{b7} q quit"
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
        None => Line::from(Span::styled("\u{2026} type a color", Style::new().fg(LABEL))),
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

fn render_save_profile(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 52u16.min(screen.width.saturating_sub(2));
    let height = 5u16.min(screen.height.saturating_sub(2));
    let popup = centered(screen, width, height);
    f.render_widget(Clear, popup);

    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " save profile ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " Enter save (palette + settings, not vessels) \u{b7} Esc cancel ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::SaveProfile(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    if inner.height == 0 {
        return;
    }
    f.render_widget(
        Paragraph::new(Span::styled(
            "profile name",
            Style::new().fg(LABEL),
        )),
        Rect::new(inner.x, inner.y, inner.width, 1),
    );
    if inner.height >= 2 {
        f.render_widget(
            Paragraph::new(Line::from(vec![
                Span::styled("> ", Style::new().fg(TITLE)),
                Span::styled(m.text.clone(), Style::new().fg(VALUE).add_modifier(Modifier::BOLD)),
                Span::styled("\u{2588}", Style::new().fg(LABEL)),
            ])),
            Rect::new(inner.x, inner.y + 1, inner.width, 1),
        );
    }
}

/// The quit confirmation — a small centered popup with `[ quit ]` / `[ cancel ]` buttons (records
/// their rects for the mouse handler). Reachable from every screen, so it overlays whatever's behind.
fn render_confirm_quit(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 40u16.min(screen.width.saturating_sub(2)).max(18);
    let height = 5u16.min(screen.height.saturating_sub(2)).max(4);
    let popup = centered(screen, width, height);
    f.render_widget(Clear, popup);

    let partying = app.partying;
    let block = Block::bordered()
        .border_style(Style::new().fg(BAD))
        .title(Span::styled(
            " quit? ",
            Style::new().fg(BAD).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " y/Enter quit \u{b7} n/Esc cancel ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::ConfirmQuit(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    m.quit_btn = Rect::default();
    m.cancel_btn = Rect::default();
    if inner.width == 0 || inner.height == 0 {
        return;
    }

    let msg = if partying {
        "Quit? The party stops and lights reset to white."
    } else {
        "Quit dancy-party-rs?"
    };
    f.render_widget(
        Paragraph::new(Span::styled(msg, Style::new().fg(VALUE))),
        Rect::new(inner.x, inner.y, inner.width, 1),
    );

    if inner.height >= 3 {
        let y = inner.y + 2;
        let quit = "[ quit ]";
        let cancel = "[ cancel ]";
        let qw = (quit.chars().count() as u16).min(inner.width);
        let quit_rect = Rect::new(inner.x, y, qw, 1);
        m.quit_btn = quit_rect;
        f.render_widget(
            Paragraph::new(Span::styled(
                quit,
                Style::new().fg(BAD).add_modifier(Modifier::BOLD),
            )),
            quit_rect,
        );
        let cx = inner.x + qw + 2;
        if cx < inner.x + inner.width {
            let cw = (cancel.chars().count() as u16).min(inner.x + inner.width - cx);
            let cancel_rect = Rect::new(cx, y, cw, 1);
            m.cancel_btn = cancel_rect;
            f.render_widget(
                Paragraph::new(Span::styled(cancel, Style::new().fg(LABEL))),
                cancel_rect,
            );
        }
    }
}

/// The settings popup — every display-affecting knob, edited live. Like the other menus it trades
/// transparency for readability (bg fill + `Clear`) and records its hit-test rects (popup area + one
/// rect per row) back into the modal for the next mouse event. A running party adopts changes at once.
fn render_settings(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 54u16.min(screen.width.saturating_sub(2)).max(28);
    let height = (SETTING_ROWS as u16 + 3).min(screen.height).max(5);
    let popup = centered(screen, width, height);
    f.render_widget(Clear, popup);

    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " settings ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " \u{2191}\u{2193} row \u{b7} \u{2190}\u{2192} adjust (Shift coarse) \u{b7} Esc close ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::Settings(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    m.rows.clear();
    if inner.width == 0 || inner.height == 0 {
        return;
    }

    for row in 0..SETTING_ROWS {
        if row as u16 >= inner.height {
            break;
        }
        let y = inner.y + row as u16;
        let rect = Rect::new(inner.x, y, inner.width, 1);
        m.rows.push((rect, row));

        let focused = row == m.sel;
        let (marker, name_style) = if focused {
            ("\u{25b6} ", Style::new().fg(FOCUS).add_modifier(Modifier::BOLD))
        } else {
            ("  ", Style::new().fg(VALUE))
        };
        let line = Line::from(vec![
            Span::styled(marker, Style::new().fg(ACCENT)),
            Span::styled(format!("{:<16}", Settings::row_label(row)), name_style),
            Span::styled(
                app.settings.row_value(row),
                Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
            ),
        ]);
        f.render_widget(Paragraph::new(line), rect);
    }
}

// ---- span / formatting helpers ------------------------------------------------------------------

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

fn label(s: &str) -> Span<'static> {
    Span::styled(s.to_string(), Style::new().fg(LABEL))
}

fn ctl_badge(name: &str, on: bool) -> Span<'static> {
    if on {
        Span::styled(format!("{name}:on"), Style::new().fg(GOOD))
    } else {
        Span::styled(format!("{name}:off"), Style::new().fg(LABEL))
    }
}

/// A foreground ratio bar: a colored filled run + a dim empty run (no background — see-through).
fn bar_spans(frac: f64, width: usize, color: Color) -> Vec<Span<'static>> {
    let frac = frac.clamp(0.0, 1.0);
    let filled = ((frac * width as f64).round() as usize).min(width);
    vec![
        Span::styled("\u{2588}".repeat(filled), Style::new().fg(color)),
        Span::styled("\u{2591}".repeat(width - filled), Style::new().fg(EMPTY)),
    ]
}

fn pct_span(frac: f64) -> Span<'static> {
    Span::styled(
        format!("{:>3}%", (frac.clamp(0.0, 1.0) * 100.0).round() as i64),
        Style::new().fg(level_color(frac)).add_modifier(Modifier::BOLD),
    )
}

fn level_color(frac: f64) -> Color {
    if frac >= 0.5 {
        GOOD
    } else if frac >= 0.2 {
        WARN
    } else {
        BAD
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
    use crate::app::Settings;
    use crate::source::{FromWorker, Health, VesselLights};
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;

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
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        App::new(tx, "fs:/sim".into(), Settings::default(), Vec::new())
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
    fn party_screen_shows_toggle_and_battery_and_settings() {
        let mut a = app();
        a.screen = Screen::Party;
        let off = render_to_text(&mut a, 80, 16);
        assert!(off.contains("LETS PARTY!"));
        assert!(off.contains("battery"));
        assert!(off.contains("refill"));
        assert!(off.contains("settings"));
        a.partying = true;
        let on = render_to_text(&mut a, 80, 16);
        assert!(on.contains("STOP, MY EYES"));
    }

    #[test]
    fn settings_popup_lists_decoupled_timing_rows() {
        let mut a = app();
        a.screen = Screen::Party;
        // Open settings the way a key would.
        a.on_key(ratatui::crossterm::event::KeyEvent::new(
            ratatui::crossterm::event::KeyCode::Char('s'),
            ratatui::crossterm::event::KeyModifiers::NONE,
        ));
        let text = render_to_text(&mut a, 80, 18);
        assert!(text.contains("color time"));
        assert!(text.contains("anim time"));
        assert!(text.contains("color stagger"));
        assert!(text.contains("anim stagger"));
        assert!(text.contains("frame rate"));
    }

    #[test]
    fn tiny_sizes_never_panic() {
        for &(w, h) in &[(1, 1), (4, 3), (10, 5), (20, 8), (40, 12)] {
            let mut a = app();
            a.screen = Screen::Party;
            let _ = render_to_text(&mut a, w, h);
            a.open_xkcd();
            let _ = render_to_text(&mut a, w, h);
            a.modal = Modal::None;
            a.hidden = true;
            let _ = render_to_text(&mut a, w, h);
        }
    }

    #[test]
    fn hide_mode_shows_only_a_status_bar_with_the_buttons() {
        let mut a = app();
        a.screen = Screen::Party;
        a.hidden = true;
        let text = render_to_text(&mut a, 80, 16);
        assert!(text.contains("DANCY"));
        assert!(text.contains("LETS PARTY!"));
        assert!(text.contains("refill"));
        assert!(text.contains("show"));
        // The palette/title chrome of the full UI must be gone (only the one bar renders).
        assert!(!text.contains("palette"));
        // Button rects were recorded for the mouse handler.
        assert!(a.party_btn.width > 0 && a.refill_btn.width > 0 && a.hide_show_btn.width > 0);
    }

    #[test]
    fn save_profile_modal_renders() {
        let mut a = app();
        a.screen = Screen::Party;
        a.on_key(ratatui::crossterm::event::KeyEvent::new(
            ratatui::crossterm::event::KeyCode::Char('w'),
            ratatui::crossterm::event::KeyModifiers::NONE,
        ));
        let text = render_to_text(&mut a, 80, 18);
        assert!(text.contains("save profile"));
        assert!(text.contains("profile name"));
    }
}
