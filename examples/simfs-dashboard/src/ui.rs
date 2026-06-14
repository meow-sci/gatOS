//! Rendering. **Transparency is the rule** (same as `dashboard-rs`): cell backgrounds are left
//! unset so purrTTY shows the live game through the text; only the toolbar/status bars and modal
//! popups get a subtle background. Each widget is a bordered card flowed into a grid; control cards
//! render an interactive zone (bar / button / picker handle) whose absolute rect is recorded into
//! [`App`] for the next mouse event.

use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Clear, Paragraph};
use ratatui::Frame;

use crate::app::{App, InputPurpose, Modal, Zone, ZoneAction, CUSTOM_ROW};
use crate::widget::{parse_flag, parse_scalar, Kind};

// Foreground palette (the dashboard is otherwise transparent).
const TITLE: Color = Color::Cyan;
const LABEL: Color = Color::DarkGray;
const VALUE: Color = Color::White;
const GOOD: Color = Color::Green;
const WARN: Color = Color::Yellow;
const BAD: Color = Color::Red;
const ACCENT: Color = Color::Magenta;
const FOCUS: Color = Color::LightCyan;
const EMPTY: Color = Color::DarkGray;
const BAR_BG: Color = Color::Rgb(24, 28, 44);
const BORDER_BASE: (u8, u8, u8) = (0, 200, 220);

const CARD_H: u16 = 5;

pub fn render(f: &mut Frame, app: &mut App) {
    let chunks = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(1),
    ])
    .split(f.area());

    render_toolbar(f, app, chunks[0]);
    render_grid(f, app, chunks[1]);
    render_status(f, app, chunks[2]);

    match &app.modal {
        Modal::Search(_) => render_search(f, app),
        Modal::Input(_) => render_input(f, app),
        Modal::Picker(_) => render_picker(f, app),
        Modal::Settings(_) => render_settings(f, app),
        Modal::None => {}
    }
}

// ---- toolbar ------------------------------------------------------------------------------------

fn render_toolbar(f: &mut Frame, app: &mut App, area: Rect) {
    // Right-aligned buttons: [+ add] [save] [settings]; record their rects for click hit-testing.
    let buttons = [" [+ add] ", " [save] ", " [settings] "];
    let mut x = area.x + area.width;
    let mut rects = [Rect::default(); 3];
    for (i, label) in buttons.iter().enumerate().rev() {
        let w = (label.chars().count() as u16).min(x.saturating_sub(area.x));
        x = x.saturating_sub(w);
        rects[i] = Rect {
            x,
            y: area.y,
            width: w,
            height: 1,
        };
    }
    app.tb_add = rects[0];
    app.tb_save = rects[1];
    app.tb_settings = rects[2];

    let used: u16 = buttons.iter().map(|b| b.chars().count() as u16).sum();
    let left = Rect {
        x: area.x,
        y: area.y,
        width: area.width.saturating_sub(used),
        height: 1,
    };

    let dirty = if app.dirty { "*" } else { "" };
    let file = app.current_file.as_deref().unwrap_or("(unsaved)");
    let conn = if app.connected {
        Span::styled("\u{25cf} online", Style::new().fg(GOOD))
    } else {
        Span::styled("\u{25cf} offline", Style::new().fg(BAD))
    };
    let header = Line::from(vec![
        Span::styled(
            " simfs ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ),
        kv("src", &app.source_label),
        kv("file", &format!("{file}{dirty}")),
        kv("widgets", &app.widgets.len().to_string()),
        kv("cols", &app.columns.to_string()),
        kv("poll", &fmt_poll(app.interval)),
        ctl_badge("ctl", app.control_enabled),
        Span::raw(" "),
        ctl_badge("dbg", app.debug_enabled),
        Span::raw(" "),
        conn,
    ]);
    f.render_widget(Paragraph::new(header).style(Style::new().bg(BAR_BG)), left);

    for (i, label) in buttons.iter().enumerate() {
        f.render_widget(
            Paragraph::new(Span::styled(
                *label,
                Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
            ))
            .style(Style::new().bg(BAR_BG)),
            rects[i],
        );
    }
}

fn ctl_badge(name: &str, on: bool) -> Span<'static> {
    if on {
        Span::styled(format!("{name}:on"), Style::new().fg(GOOD))
    } else {
        Span::styled(format!("{name}:off"), Style::new().fg(LABEL))
    }
}

// ---- the widget grid ----------------------------------------------------------------------------

/// A flattened, render-ready view of one widget (so the render loop never borrows `app` while it
/// also writes back the hit-test rects).
struct CardData {
    title: String,
    path: String,
    kind: Kind,
    value: Option<String>,
    error: Option<String>,
}

fn render_grid(f: &mut Frame, app: &mut App, area: Rect) {
    if app.widgets.is_empty() {
        let hint =
            "No widgets yet \u{2014} press  a  (or click  [+ add] ) to search the /sim filesystem";
        let y = area.y + area.height / 2;
        f.render_widget(
            Paragraph::new(Span::styled(hint, Style::new().fg(LABEL))).centered(),
            Rect {
                x: area.x,
                y,
                width: area.width,
                height: 1,
            },
        );
        app.card_rects.clear();
        app.zones.clear();
        return;
    }

    let cards: Vec<CardData> = app
        .widgets
        .iter()
        .map(|w| CardData {
            title: w.title.clone(),
            path: w.path.clone(),
            kind: w.kind.clone(),
            value: app.value_of(&w.path).map(str::to_string),
            error: app.error_of(&w.path).map(str::to_string),
        })
        .collect();

    let cols = app.columns.max(1);
    let card_w = (area.width / cols).max(1);
    let rows_visible = (area.height / CARD_H).max(1) as usize;
    let sel = app.selected.min(cards.len() - 1);
    let sel_row = sel / cols as usize;
    let start_row = if sel_row >= rows_visible {
        sel_row + 1 - rows_visible
    } else {
        0
    };
    let start = start_row * cols as usize;

    let mut card_rects: Vec<(Rect, usize)> = Vec::new();
    let mut zones: Vec<Zone> = Vec::new();

    for (vis, idx) in (start..cards.len()).enumerate() {
        let vis_row = vis / cols as usize;
        if vis_row >= rows_visible {
            break;
        }
        let col = (vis % cols as usize) as u16;
        let rect = Rect {
            x: area.x + col * card_w,
            y: area.y + vis_row as u16 * CARD_H,
            width: card_w,
            height: CARD_H,
        };
        card_rects.push((rect, idx));
        render_card(
            f,
            &cards[idx],
            idx,
            idx == sel,
            app.border_opacity,
            rect,
            &mut zones,
        );
    }

    app.card_rects = card_rects;
    app.zones = zones;
}

fn render_card(
    f: &mut Frame,
    card: &CardData,
    idx: usize,
    selected: bool,
    opacity: u8,
    rect: Rect,
    zones: &mut Vec<Zone>,
) {
    let border = if selected {
        FOCUS
    } else {
        border_color(opacity)
    };
    let title_style = if selected {
        Style::new().fg(FOCUS).add_modifier(Modifier::BOLD)
    } else {
        Style::new().fg(TITLE).add_modifier(Modifier::BOLD)
    };
    let marker = if selected { "\u{25b6} " } else { " " };
    let block = Block::bordered()
        .border_style(Style::new().fg(border))
        .title(Span::styled(
            format!(
                "{marker}{}",
                trunc(&card.title, rect.width.saturating_sub(4) as usize)
            ),
            title_style,
        ))
        .title_bottom(Span::styled(
            format!(" {} ", card.kind.tag()),
            Style::new().fg(LABEL),
        ));
    let inner = block.inner(rect);
    f.render_widget(block, rect);
    if inner.width == 0 || inner.height == 0 {
        return;
    }

    let row0 = Rect {
        x: inner.x,
        y: inner.y,
        width: inner.width,
        height: 1,
    };
    let path_row = Rect {
        x: inner.x,
        y: inner.y + inner.height - 1,
        width: inner.width,
        height: 1,
    };

    // The dim path line at the bottom of every card (the field this widget is bound to).
    if inner.height >= 2 {
        f.render_widget(
            Paragraph::new(Span::styled(
                trunc(&card.path, inner.width as usize),
                Style::new().fg(LABEL),
            )),
            path_row,
        );
    }

    // If the field can't be read, show the errno in place of the value (unless it's a pure button).
    if card.value.is_none() {
        if let Some(err) = &card.error {
            if !matches!(card.kind, Kind::Trigger { .. }) {
                f.render_widget(
                    Paragraph::new(Span::styled(err.clone(), Style::new().fg(BAD))),
                    row0,
                );
                return;
            }
        }
    }

    match &card.kind {
        Kind::Throttle | Kind::Fraction => {
            let frac = card
                .value
                .as_deref()
                .and_then(parse_scalar)
                .unwrap_or(0.0)
                .clamp(0.0, 1.0);
            let bar_x = row0.x + 6;
            let bar_w = row0.width.saturating_sub(6);
            let mut spans = vec![Span::styled(
                format!("{:>4} ", pct(frac)),
                Style::new().fg(WARN).add_modifier(Modifier::BOLD),
            )];
            spans.extend(bar_spans(frac, bar_w as usize, WARN));
            f.render_widget(Paragraph::new(Line::from(spans)), row0);
            zones.push(Zone {
                rect: Rect {
                    x: bar_x,
                    y: row0.y,
                    width: bar_w,
                    height: 1,
                },
                action: ZoneAction::SetFraction(idx),
            });
        }
        Kind::Gauge { unit, max } => {
            let v = card.value.as_deref().and_then(parse_scalar).unwrap_or(0.0);
            match max {
                Some(m) if *m > 0.0 => {
                    let frac = (v / m).clamp(0.0, 1.0);
                    let mut spans = vec![Span::styled(
                        format!("{:>4} ", pct(frac)),
                        Style::new()
                            .fg(level_color(frac))
                            .add_modifier(Modifier::BOLD),
                    )];
                    spans.extend(bar_spans(
                        frac,
                        row0.width.saturating_sub(6) as usize,
                        level_color(frac),
                    ));
                    f.render_widget(Paragraph::new(Line::from(spans)), row0);
                }
                _ => render_value(f, row0, &display_scalar(unit, v), VALUE),
            }
        }
        Kind::Number { unit } => {
            let v = card.value.as_deref().and_then(parse_scalar).unwrap_or(0.0);
            render_value(f, row0, &display_scalar(unit, v), VALUE);
        }
        Kind::Flag => {
            let on = card.value.as_deref().map(parse_flag).unwrap_or(false);
            render_value(
                f,
                row0,
                if on { "ON" } else { "off" },
                if on { GOOD } else { LABEL },
            );
        }
        Kind::Toggle => {
            let on = card.value.as_deref().map(parse_flag).unwrap_or(false);
            let (text, color) = if on {
                ("[ ON ]", GOOD)
            } else {
                ("[ off ]", LABEL)
            };
            f.render_widget(
                Paragraph::new(Span::styled(
                    text,
                    Style::new().fg(color).add_modifier(Modifier::BOLD),
                )),
                row0,
            );
            zones.push(Zone {
                rect: Rect {
                    x: row0.x,
                    y: row0.y,
                    width: text.chars().count() as u16,
                    height: 1,
                },
                action: ZoneAction::Toggle(idx),
            });
        }
        Kind::Trigger { verb, .. } => {
            let label = format!("[ {verb} ]");
            f.render_widget(
                Paragraph::new(Span::styled(
                    label.clone(),
                    Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
                )),
                row0,
            );
            zones.push(Zone {
                rect: Rect {
                    x: row0.x,
                    y: row0.y,
                    width: label.chars().count() as u16,
                    height: 1,
                },
                action: ZoneAction::Trigger(idx),
            });
        }
        Kind::NumberCtl { unit, .. } => {
            let v = card.value.as_deref().and_then(parse_scalar).unwrap_or(0.0);
            let val = display_scalar(unit, v);
            let minus = Span::styled("[-]", Style::new().fg(ACCENT).add_modifier(Modifier::BOLD));
            let plus = Span::styled("[+]", Style::new().fg(ACCENT).add_modifier(Modifier::BOLD));
            let mid = format!(" {val} ");
            f.render_widget(
                Paragraph::new(Line::from(vec![
                    minus,
                    Span::styled(mid.clone(), Style::new().fg(VALUE)),
                    plus,
                ])),
                row0,
            );
            zones.push(Zone {
                rect: Rect {
                    x: row0.x,
                    y: row0.y,
                    width: 3,
                    height: 1,
                },
                action: ZoneAction::Step(idx, -1),
            });
            let plus_x = row0.x + 3 + mid.chars().count() as u16;
            zones.push(Zone {
                rect: Rect {
                    x: plus_x,
                    y: row0.y,
                    width: 3,
                    height: 1,
                },
                action: ZoneAction::Step(idx, 1),
            });
        }
        Kind::Enum { .. } => {
            let token = card.value.as_deref().unwrap_or("\u{2014}").trim();
            let token = if token.is_empty() { "manual" } else { token };
            f.render_widget(
                Paragraph::new(Span::styled(
                    format!("{token} \u{25be}"),
                    Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
                )),
                row0,
            );
            zones.push(Zone {
                rect: row0,
                action: ZoneAction::OpenPicker(idx),
            });
        }
        Kind::Vector | Kind::Json | Kind::Text => {
            let v = card.value.as_deref().unwrap_or("\u{2026}");
            render_value(f, row0, &trunc(v, row0.width as usize), VALUE);
        }
    }
}

fn render_value(f: &mut Frame, rect: Rect, text: &str, color: Color) {
    f.render_widget(
        Paragraph::new(Span::styled(text.to_string(), Style::new().fg(color))),
        rect,
    );
}

// ---- status bar ---------------------------------------------------------------------------------

fn render_status(f: &mut Frame, app: &App, area: Rect) {
    let (msg, color) = if app.status_line.is_empty() {
        (
            "a add \u{b7} Enter/click act \u{b7} -/= nudge \u{b7} [ ] move \u{b7} R rename \u{b7} x remove \u{b7} w save \u{b7} s settings \u{b7} q quit"
                .to_string(),
            LABEL,
        )
    } else if app.status_is_error {
        (app.status_line.clone(), BAD)
    } else {
        (app.status_line.clone(), GOOD)
    };
    f.render_widget(
        Paragraph::new(Span::styled(msg, Style::new().fg(color))).style(Style::new().bg(BAR_BG)),
        area,
    );
}

// ---- search modal -------------------------------------------------------------------------------

fn render_search(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = (screen.width * 4 / 5).clamp(30, screen.width.saturating_sub(2));
    let height = (screen.height * 4 / 5).clamp(6, screen.height.saturating_sub(2));
    let popup = centered(screen, width, height);

    f.render_widget(Clear, popup);
    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " add /sim field ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " type to filter \u{b7} \u{2191}\u{2193} select \u{b7} Enter add \u{b7} Esc close ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::Search(s) = &mut app.modal else {
        return;
    };
    s.area = popup;
    if inner.height == 0 {
        return;
    }

    let input_rect = Rect {
        x: inner.x,
        y: inner.y,
        width: inner.width,
        height: 1,
    };
    s.input_rect = input_rect;
    f.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled("\u{1f50d} ", Style::new().fg(TITLE)),
            Span::styled(
                s.query.clone(),
                Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
            ),
            Span::styled("\u{2588}", Style::new().fg(LABEL)),
        ])),
        input_rect,
    );

    let list_area = Rect {
        x: inner.x,
        y: inner.y + 1,
        width: inner.width,
        height: inner.height.saturating_sub(1),
    };
    if list_area.height == 0 {
        return;
    }

    if s.loading {
        f.render_widget(
            Paragraph::new(Span::styled(
                "loading catalog\u{2026}",
                Style::new().fg(LABEL),
            )),
            list_area,
        );
        return;
    }

    let rows = s.row_count();
    let visible = list_area.height as usize;
    if s.selected < s.offset {
        s.offset = s.selected;
    } else if s.selected >= s.offset + visible {
        s.offset = s.selected + 1 - visible;
    }

    s.item_rects.clear();
    let allow_custom = rows > s.filtered.len();
    for row in 0..list_area.height {
        let idx = s.offset + row as usize;
        if idx >= rows {
            break;
        }
        let rect = Rect {
            x: list_area.x,
            y: list_area.y + row,
            width: list_area.width,
            height: 1,
        };
        let focused = idx == s.selected;
        let (marker, base) = if focused {
            ("\u{25b6} ", FOCUS)
        } else {
            ("  ", VALUE)
        };

        let line = if allow_custom && idx == s.filtered.len() {
            s.item_rects.push((rect, CUSTOM_ROW));
            Line::from(vec![
                Span::styled(marker, Style::new().fg(ACCENT)),
                Span::styled("add custom path: ", Style::new().fg(LABEL)),
                Span::styled(
                    s.query.trim().to_string(),
                    Style::new().fg(base).add_modifier(Modifier::BOLD),
                ),
            ])
        } else {
            let cand = &s.all[s.filtered[idx]];
            s.item_rects.push((rect, idx));
            Line::from(vec![
                Span::styled(marker, Style::new().fg(ACCENT)),
                Span::styled(
                    format!("{:<5}", cand.kind.tag()),
                    Style::new().fg(tag_color(&cand.kind)),
                ),
                Span::styled(
                    trunc(&cand.path, list_area.width.saturating_sub(8) as usize),
                    Style::new().fg(base),
                ),
            ])
        };
        f.render_widget(Paragraph::new(line), rect);
    }
}

fn tag_color(kind: &Kind) -> Color {
    if kind.is_writable() {
        ACCENT
    } else {
        LABEL
    }
}

// ---- input modal (save / rename) ----------------------------------------------------------------

fn render_input(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 50u16.min(screen.width.saturating_sub(2)).max(20);
    let popup = centered(screen, width, 5);
    f.render_widget(Clear, popup);

    let Modal::Input(m) = &mut app.modal else {
        return;
    };
    m.area = popup;
    let hint = match m.purpose {
        InputPurpose::Save => " Enter save \u{b7} Esc cancel ",
        InputPurpose::Rename => " Enter rename \u{b7} Esc cancel ",
    };
    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            format!(" {} ", m.title),
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(hint, Style::new().fg(LABEL)))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);
    if inner.height == 0 {
        return;
    }
    f.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled(
                m.text.clone(),
                Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
            ),
            Span::styled("\u{2588}", Style::new().fg(LABEL)),
        ])),
        Rect {
            x: inner.x,
            y: inner.y,
            width: inner.width,
            height: 1,
        },
    );
}

// ---- enum picker --------------------------------------------------------------------------------

fn render_picker(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let Modal::Picker(p) = &mut app.modal else {
        return;
    };
    let n = p.options.len() as u16;
    let width = 28u16.min(screen.width.saturating_sub(2)).max(12);
    let max_body = screen.height.saturating_sub(2).max(1);
    let body = n.min(max_body);
    let popup = centered(screen, width, body + 2);
    p.area = popup;

    f.render_widget(Clear, popup);
    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            format!(" {} ", trunc(&p.title, width.saturating_sub(4) as usize)),
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let visible = inner.height as usize;
    if visible > 0 {
        if p.selected < p.offset {
            p.offset = p.selected;
        } else if p.selected >= p.offset + visible {
            p.offset = p.selected + 1 - visible;
        }
    }

    p.item_rects.clear();
    for row in 0..inner.height {
        let idx = p.offset + row as usize;
        if idx >= p.options.len() {
            break;
        }
        let rect = Rect {
            x: inner.x,
            y: inner.y + row,
            width: inner.width,
            height: 1,
        };
        p.item_rects.push((rect, idx));
        let focused = idx == p.selected;
        let (marker, style) = if focused {
            (
                "\u{25b6} ",
                Style::new().fg(FOCUS).add_modifier(Modifier::BOLD),
            )
        } else {
            ("  ", Style::new().fg(VALUE))
        };
        let text = if p.options[idx].eq_ignore_ascii_case("manual") {
            "manual (off)".to_string()
        } else {
            p.options[idx].clone()
        };
        f.render_widget(
            Paragraph::new(Line::from(vec![
                Span::styled(marker, Style::new().fg(ACCENT)),
                Span::styled(text, style),
            ])),
            rect,
        );
    }
}

// ---- settings modal -----------------------------------------------------------------------------

fn render_settings(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let width = 48u16.min(screen.width.saturating_sub(2)).max(24);
    let popup = centered(screen, width, 7);
    f.render_widget(Clear, popup);

    let cols = app.columns;
    let opacity = app.border_opacity;

    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            " settings ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .title_bottom(Span::styled(
            " \u{2190}/\u{2192} opacity \u{b7} \u{2191}/\u{2193} columns \u{b7} Esc close ",
            Style::new().fg(LABEL),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    let Modal::Settings(st) = &mut app.modal else {
        return;
    };
    st.area = popup;
    if inner.width == 0 || inner.height < 3 {
        return;
    }

    // Row 0: columns stepper.
    let row0 = Rect {
        x: inner.x,
        y: inner.y,
        width: inner.width,
        height: 1,
    };
    let minus = Rect {
        x: inner.x + 9,
        y: row0.y,
        width: 3,
        height: 1,
    };
    let plus = Rect {
        x: inner.x + 9 + 4 + 1,
        y: row0.y,
        width: 3,
        height: 1,
    };
    st.cols_minus = minus;
    st.cols_plus = plus;
    f.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled("Columns  ", Style::new().fg(VALUE)),
            Span::styled("[-]", Style::new().fg(ACCENT)),
            Span::styled(
                format!(" {cols} "),
                Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
            ),
            Span::styled("[+]", Style::new().fg(ACCENT)),
        ])),
        row0,
    );

    // Row 2: opacity slider.
    let label_row = Rect {
        x: inner.x,
        y: inner.y + 2,
        width: inner.width,
        height: 1,
    };
    let pct_text = format!(" {opacity:>3}%");
    let pct_w = pct_text.chars().count() as u16;
    let track_w = inner.width.saturating_sub(pct_w).max(1);
    let track = Rect {
        x: inner.x,
        y: inner.y + 3.min(inner.height - 1),
        width: track_w,
        height: 1,
    };
    st.opacity_track = track;
    f.render_widget(
        Paragraph::new(Span::styled("Border opacity", Style::new().fg(VALUE))),
        label_row,
    );
    let mut spans = bar_spans(opacity as f64 / 100.0, track_w as usize, ACCENT);
    spans.push(Span::styled(
        pct_text,
        Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
    ));
    f.render_widget(Paragraph::new(Line::from(spans)), track);
}

// ---- shared helpers -----------------------------------------------------------------------------

fn centered(screen: Rect, width: u16, height: u16) -> Rect {
    Rect {
        x: screen.x + screen.width.saturating_sub(width) / 2,
        y: screen.y + screen.height.saturating_sub(height) / 2,
        width,
        height,
    }
}

fn border_color(opacity: u8) -> Color {
    let f = opacity.min(100) as f64 / 100.0;
    let scale = |c: u8| (c as f64 * f).round() as u8;
    Color::Rgb(
        scale(BORDER_BASE.0),
        scale(BORDER_BASE.1),
        scale(BORDER_BASE.2),
    )
}

fn bar_spans(frac: f64, width: usize, color: Color) -> Vec<Span<'static>> {
    let frac = frac.clamp(0.0, 1.0);
    let filled = ((frac * width as f64).round() as usize).min(width);
    vec![
        Span::styled("\u{2588}".repeat(filled), Style::new().fg(color)),
        Span::styled("\u{2591}".repeat(width - filled), Style::new().fg(EMPTY)),
    ]
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

/// Formats a read-only scalar by its unit hint (m, m/s, kg, N, W, s, °, …).
fn display_scalar(unit: &str, v: f64) -> String {
    match unit {
        "m" => fmt_len(v),
        "m/s" => fmt_speed(v),
        "kg" => fmt_mass(v),
        "N" => fmt_force(v),
        "W" => format!("{} W", trim(v)),
        "J" => format!("{} J", trim(v)),
        "Pa" => format!("{} Pa", trim(v)),
        "g" => format!("{} g", trim(v)),
        "x" => format!("{}x", trim(v)),
        "\u{b0}" => format!("{}\u{b0}", trim(v)),
        "ut" => fmt_dur(v),
        "s" => {
            if v.abs() >= 120.0 {
                fmt_dur(v)
            } else {
                format!("{} s", trim(v))
            }
        }
        "" => trim(v),
        other => format!("{} {other}", trim(v)),
    }
}

fn kv<'a>(k: &'a str, v: &str) -> Span<'a> {
    Span::styled(format!("{k} {v}  "), Style::new().fg(VALUE))
}

fn trunc(s: &str, max: usize) -> String {
    if max == 0 {
        return String::new();
    }
    if s.chars().count() <= max {
        s.to_string()
    } else {
        s.chars()
            .take(max.saturating_sub(1))
            .chain(['\u{2026}'])
            .collect()
    }
}

fn trim(v: f64) -> String {
    if !v.is_finite() {
        return "—".to_string();
    }
    let s = format!("{v:.3}");
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s.is_empty() || s == "-0" {
        "0".to_string()
    } else {
        s.to_string()
    }
}

fn pct(frac: f64) -> String {
    format!("{}%", (frac.clamp(0.0, 1.0) * 100.0).round() as i64)
}

fn fmt_len(m: f64) -> String {
    let a = m.abs();
    if a >= 1.0e6 {
        format!("{:.2}Mm", m / 1.0e6)
    } else if a >= 1.0e3 {
        format!("{:.2}km", m / 1.0e3)
    } else {
        format!("{:.0}m", m)
    }
}

fn fmt_speed(ms: f64) -> String {
    if ms.abs() >= 1.0e3 {
        format!("{:.2}km/s", ms / 1.0e3)
    } else {
        format!("{:.0}m/s", ms)
    }
}

fn fmt_mass(kg: f64) -> String {
    if kg.abs() >= 1.0e3 {
        format!("{:.1}t", kg / 1.0e3)
    } else {
        format!("{:.0}kg", kg)
    }
}

fn fmt_force(n: f64) -> String {
    if n.abs() >= 1.0e3 {
        format!("{:.0}kN", n / 1.0e3)
    } else {
        format!("{:.0}N", n)
    }
}

fn fmt_dur(seconds: f64) -> String {
    if !seconds.is_finite() || seconds <= 0.0 {
        return "—".to_string();
    }
    let s = seconds as i64;
    let (h, m, sec) = (s / 3600, (s % 3600) / 60, s % 60);
    if h > 0 {
        format!("{h}h{m:02}m")
    } else if m > 0 {
        format!("{m}m{sec:02}s")
    } else {
        format!("{sec}s")
    }
}

fn fmt_poll(interval: std::time::Duration) -> String {
    let ms = interval.as_millis();
    if ms == 0 {
        return "—".to_string();
    }
    format!("{ms}ms ({} Hz)", trim(1000.0 / ms as f64))
}
