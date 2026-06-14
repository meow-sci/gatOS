//! Rendering. **Transparency is the rule:** cell backgrounds are left unset everywhere so purrTTY
//! shows the live game through the text; only the header and status/footer bars get a (subtle) bg.
//! Selection/focus is a bright-bold foreground color + a `▶ ` marker — never reverse-video. Bars
//! (throttle/battery/tank) are drawn as colored foreground block runs, not ratatui `Gauge`s (which
//! need a bg to be visible).

use std::time::Duration;

use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Cell, Clear, Paragraph, Row, Table};
use ratatui::Frame;

use crate::api::Vessel;
use crate::app::{Action, App, Screen};

/// Columns the throttle label + percent occupy before the bar starts (`"Throttle "` = 9, a
/// 4-wide right-aligned percent, a space). The bar's clickable rect is anchored at this offset.
const THROTTLE_PREFIX: u16 = 14;

// Foreground palette (the TUI is otherwise transparent).
const TITLE: Color = Color::Cyan;
const LABEL: Color = Color::DarkGray;
const VALUE: Color = Color::White;
const GOOD: Color = Color::Green;
const WARN: Color = Color::Yellow;
const BAD: Color = Color::Red;
const ACCENT: Color = Color::Magenta;
const FOCUS: Color = Color::LightCyan;
const EMPTY: Color = Color::DarkGray; // the unfilled run of a fg bar
                                      // The only background fills — header and status/footer bars.
const BAR_BG: Color = Color::Rgb(24, 28, 44);

pub fn render(f: &mut Frame, app: &mut App) {
    // Resolve the screen id first so the `&app.screen` borrow is released before the mutable
    // `render_*(f, app, …)` call.
    let detail_id = match &app.screen {
        Screen::Dashboard => None,
        Screen::Detail(id) => Some(id.clone()),
    };
    match detail_id {
        None => render_dashboard(f, app),
        Some(id) => render_detail(f, app, &id),
    }
    if app.picker.is_some() {
        render_picker(f, app);
    }
}

// ---- dashboard --------------------------------------------------------------------------------

fn render_dashboard(f: &mut Frame, app: &mut App) {
    let chunks = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(1),
    ])
    .split(f.area());

    // Header bar (bg-filled).
    let conn = if app.connected {
        Span::styled("● online", Style::new().fg(GOOD))
    } else {
        Span::styled("● offline", Style::new().fg(BAD))
    };
    let control = if app.status.control {
        Span::styled("control:on", Style::new().fg(GOOD))
    } else {
        Span::styled("control:off", Style::new().fg(LABEL))
    };
    let debug = if app.status.debug {
        Span::styled("debug:on", Style::new().fg(WARN))
    } else {
        Span::styled("debug:off", Style::new().fg(LABEL))
    };
    let header = Line::from(vec![
        Span::styled(
            " gatOS ",
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ),
        Span::styled(
            "fleet  ",
            Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
        ),
        kv("UT", &fmt_dur(app.snapshot.ut_seconds)),
        kv("warp", &format!("{}x", trim(app.snapshot.warp_factor))),
        kv("ver", val_or(&app.snapshot.game_version, "?")),
        kv("vessels", &app.snapshot.vessels.len().to_string()),
        kv("poll", &fmt_poll(app.poll_interval)),
        control,
        Span::raw("  "),
        debug,
        Span::raw("  "),
        conn,
    ]);
    f.render_widget(
        Paragraph::new(header).style(Style::new().bg(BAR_BG)),
        chunks[0],
    );

    // Vessel table (transparent; header row + data rows, no border so the click→row math is exact).
    let head = Row::new([
        "",
        "VESSEL",
        "SITUATION",
        "ALT",
        "SPEED",
        "MASS",
        "FUEL",
        "BATT",
    ])
    .style(Style::new().fg(TITLE).add_modifier(Modifier::BOLD));
    let rows: Vec<Row> = app.snapshot.vessels.iter().map(vessel_row).collect();
    let widths = [
        Constraint::Length(1),
        Constraint::Min(12),
        Constraint::Length(12),
        Constraint::Length(10),
        Constraint::Length(10),
        Constraint::Length(9),
        Constraint::Length(6),
        Constraint::Length(6),
    ];
    let table = Table::new(rows, widths)
        .header(head)
        .row_highlight_style(Style::new().fg(FOCUS).add_modifier(Modifier::BOLD))
        .highlight_symbol("▶ ");
    f.render_stateful_widget(table, chunks[1], &mut app.table);

    // The data-rows area (below the 1-row header) — used to map a click row to a vessel index.
    app.dashboard_area = Rect {
        x: chunks[1].x,
        y: chunks[1].y.saturating_add(1),
        width: chunks[1].width,
        height: chunks[1].height.saturating_sub(1),
    };

    let hints = "↑↓/scroll select · Enter/click open · q quit";
    f.render_widget(
        Paragraph::new(Span::styled(hints, Style::new().fg(LABEL))).style(Style::new().bg(BAR_BG)),
        chunks[2],
    );
}

fn vessel_row(v: &Vessel) -> Row<'static> {
    let name = if v.name.is_empty() {
        v.id.clone()
    } else {
        v.name.clone()
    };
    let batt = match v.battery_charge_fraction {
        Some(f) => Cell::from(pct(f)).style(Style::new().fg(level_color(f))),
        None => Cell::from("—").style(Style::new().fg(LABEL)),
    };
    Row::new(vec![
        Cell::from(if v.controlled { "●" } else { " " }).style(Style::new().fg(if v.controlled {
            GOOD
        } else {
            LABEL
        })),
        Cell::from(name).style(Style::new().fg(VALUE)),
        Cell::from(v.situation.clone()).style(Style::new().fg(situation_color(&v.situation))),
        Cell::from(fmt_len(best_alt(v))).style(Style::new().fg(VALUE)),
        Cell::from(fmt_speed(v.orbital_speed.max(v.surface_speed))).style(Style::new().fg(VALUE)),
        Cell::from(fmt_mass(v.mass_total)).style(Style::new().fg(VALUE)),
        Cell::from(pct(v.fuel_fraction())).style(Style::new().fg(level_color(v.fuel_fraction()))),
        batt,
    ])
}

// ---- vessel detail ----------------------------------------------------------------------------

fn render_detail(f: &mut Frame, app: &mut App, id: &str) {
    let chunks = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(1),
    ])
    .split(f.area());

    let Some(v) = app.snapshot.vessels.iter().find(|v| v.id == id).cloned() else {
        f.render_widget(
            Paragraph::new(Line::from(vec![
                Span::styled(
                    format!(" {id} "),
                    Style::new().fg(BAD).add_modifier(Modifier::BOLD),
                ),
                Span::styled("is gone — Esc to go back", Style::new().fg(LABEL)),
            ]))
            .style(Style::new().bg(BAR_BG)),
            chunks[0],
        );
        app.controls.clear();
        return;
    };

    // Header bar.
    let header = Line::from(vec![
        Span::styled(" ◂ ", Style::new().fg(ACCENT)),
        Span::styled(
            if v.name.is_empty() {
                v.id.clone()
            } else {
                v.name.clone()
            },
            Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
        ),
        Span::raw("  "),
        Span::styled(
            v.situation.clone(),
            Style::new().fg(situation_color(&v.situation)),
        ),
        Span::raw("  "),
        Span::styled(
            format!("◷ {}", v.parent_body_name.as_deref().unwrap_or("—")),
            Style::new().fg(ACCENT),
        ),
        Span::raw("  "),
        if v.controlled {
            Span::styled("[controlled]", Style::new().fg(GOOD))
        } else {
            Span::styled("[uncontrolled]", Style::new().fg(LABEL))
        },
        Span::raw("  "),
        Span::styled("att ", Style::new().fg(LABEL)),
        Span::styled(
            val_or(&v.attitude_mode, "manual").to_string(),
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ),
        Span::raw("  "),
        kv("poll", &fmt_poll(app.poll_interval)),
    ]);
    f.render_widget(
        Paragraph::new(header).style(Style::new().bg(BAR_BG)),
        chunks[0],
    );

    // Body: telemetry (left) + controls (right).
    let body = Layout::horizontal([Constraint::Min(34), Constraint::Length(30)]).split(chunks[1]);
    let telem = Block::bordered()
        .border_style(Style::new().fg(TITLE))
        .title(Span::styled(
            " telemetry ",
            Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
        ));
    let telem_inner = telem.inner(body[0]);
    f.render_widget(telem, body[0]);
    let bar_w = (telem_inner.width.saturating_sub(20)).clamp(6, 24);
    // The throttle bar is the first telemetry line; record its rect so a click maps to a fraction.
    app.throttle_bar = Rect {
        x: telem_inner.x.saturating_add(THROTTLE_PREFIX),
        y: telem_inner.y,
        width: bar_w.min(telem_inner.width.saturating_sub(THROTTLE_PREFIX)),
        height: 1,
    };
    f.render_widget(
        Paragraph::new(telemetry_lines(&v, bar_w as usize)),
        telem_inner,
    );

    render_controls(f, app, &v, body[1]);

    // Status bar — last command outcome / connection.
    let (msg, color) = if app.status_line.is_empty() {
        (
            "Tab/↑↓ focus · Enter/click activate · −/= or click bar: throttle · Esc back · q quit"
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
        chunks[2],
    );
}

fn telemetry_lines(v: &Vessel, bar_w: usize) -> Vec<Line<'static>> {
    let mut lines = Vec::new();

    // Throttle bar (clickable — see App::throttle_bar). The label + 4-wide percent are a fixed
    // THROTTLE_PREFIX columns so the bar starts where the click hit-test rect is anchored.
    let mut throttle = vec![
        label("Throttle "),
        Span::styled(
            format!("{:>4}", pct(v.throttle_cmd)),
            Style::new().fg(WARN).add_modifier(Modifier::BOLD),
        ),
        Span::raw(" "),
    ];
    throttle.extend(bar_spans(v.throttle_cmd, bar_w, WARN));
    lines.push(Line::from(throttle));
    lines.push(Line::raw(""));

    if let Some(o) = &v.orbit {
        lines.push(section("Orbit"));
        lines.push(kvline(&[
            ("ap", fmt_len(o.apoapsis_altitude)),
            ("pe", fmt_len(o.periapsis_altitude)),
            ("ecc", trim(o.eccentricity)),
            ("inc", format!("{}°", trim(o.inclination_deg))),
            ("sma", fmt_len(o.sma_meters)),
        ]));
        lines.push(kvline(&[
            ("ta", format!("{}°", trim(o.true_anomaly_deg))),
            ("period", fmt_dur(o.period_seconds)),
            ("t-ap", fmt_dur(o.time_to_apoapsis)),
            ("t-pe", fmt_dur(o.time_to_periapsis)),
        ]));
    }

    lines.push(section("Velocity"));
    lines.push(kvline(&[
        ("orbital", fmt_speed(v.orbital_speed)),
        ("surface", fmt_speed(v.surface_speed)),
    ]));
    lines.push(section("Altitude"));
    lines.push(kvline(&[
        ("baro", fmt_len(v.barometric_altitude)),
        ("radar", fmt_len(v.radar_altitude)),
    ]));
    lines.push(section("Mass"));
    lines.push(kvline(&[
        ("total", fmt_mass(v.mass_total)),
        ("dry", fmt_mass(v.mass_dry)),
        ("prop", fmt_mass(v.mass_propellant)),
    ]));

    lines.push(section("Power"));
    lines.push(Line::from(vec![
        label("  produced "),
        Span::styled(
            format!("{} W", trim(v.power_produced_w)),
            Style::new().fg(GOOD),
        ),
        label("  consumed "),
        Span::styled(
            format!("{} W", trim(v.power_consumed_w)),
            Style::new().fg(WARN),
        ),
    ]));
    if let Some(b) = v.battery_charge_fraction {
        let mut batt = vec![label("  battery  "), pct_span(b), Span::raw(" ")];
        batt.extend(bar_spans(b, bar_w, level_color(b)));
        lines.push(Line::from(batt));
    }

    if !v.engines.is_empty() {
        lines.push(section("Engines"));
        for e in &v.engines {
            let mut row = vec![
                label(&format!("  {} ", e.index)),
                on_off(e.active),
                label("  thrust "),
                Span::styled(fmt_force(e.vac_thrust_n), Style::new().fg(VALUE)),
                label("  Isp "),
                Span::styled(format!("{}s", trim(e.isp_s)), Style::new().fg(VALUE)),
            ];
            if !e.propellant_available {
                row.push(Span::styled(
                    "  DRY",
                    Style::new().fg(BAD).add_modifier(Modifier::BOLD),
                ));
            }
            lines.push(Line::from(row));
        }
    }

    if !v.tanks.is_empty() {
        lines.push(section("Tanks"));
        for t in &v.tanks {
            let frac = if t.capacity > 0.0 {
                t.amount / t.capacity
            } else {
                t.fraction
            };
            let mut row = vec![
                label(&format!("  {:<10}", trunc(&t.resource, 10))),
                pct_span(frac),
                Span::raw(" "),
            ];
            row.extend(bar_spans(frac, bar_w, level_color(frac)));
            lines.push(Line::from(row));
        }
    }

    lines
}

/// Builds the control ring (and records each control's `Rect` into `app.controls` for mouse
/// hit-testing), then renders one button per row — the focused one bright-bold with a `▶ ` marker.
fn render_controls(f: &mut Frame, app: &mut App, v: &Vessel, area: Rect) {
    let block = Block::bordered()
        .border_style(Style::new().fg(TITLE))
        .title(Span::styled(
            " controls ",
            Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
        ));
    let inner = block.inner(area);
    f.render_widget(block, area);

    let items = control_items(v, app.status.debug);
    if app.focus >= items.len() {
        app.focus = items.len().saturating_sub(1);
    }

    app.controls.clear();
    for (i, (action, label, base)) in items.iter().enumerate() {
        if i as u16 >= inner.height {
            // Past the visible area: keep it keyboard-reachable but skip the (off-screen) rect.
            app.controls.push((Rect::default(), *action));
            continue;
        }
        let rect = Rect {
            x: inner.x,
            y: inner.y + i as u16,
            width: inner.width,
            height: 1,
        };
        app.controls.push((rect, *action));

        let focused = i == app.focus;
        let (marker, style) = if focused {
            ("▶ ", Style::new().fg(FOCUS).add_modifier(Modifier::BOLD))
        } else {
            ("  ", Style::new().fg(*base))
        };
        let line = Line::from(vec![
            Span::styled(marker, Style::new().fg(ACCENT)),
            Span::styled(label.clone(), style),
        ]);
        f.render_widget(Paragraph::new(line), rect);
    }
}

/// The ordered control list: (action, label, resting color). Debug controls are dim when the
/// server reports debug disabled (they still submit and surface EACCES if so).
fn control_items(v: &Vessel, debug_enabled: bool) -> Vec<(Action, String, Color)> {
    let on_off_color = |on: bool| if on { GOOD } else { LABEL };
    let debug_color = if debug_enabled { ACCENT } else { LABEL };
    let mut items = vec![
        (Action::Back, "← Back".to_string(), LABEL),
        (Action::Ignite, "Ignite".to_string(), GOOD),
        (Action::Shutdown, "Shutdown".to_string(), BAD),
        (Action::Stage, "Stage ▲".to_string(), WARN),
        (Action::ThrottleDown, "Throttle −10%".to_string(), VALUE),
        (Action::ThrottleUp, "Throttle +10%".to_string(), VALUE),
        (Action::ThrottleZero, "Throttle 0%".to_string(), VALUE),
        (Action::ThrottleFull, "Throttle 100%".to_string(), VALUE),
        (
            Action::ToggleLights,
            format!("Lights: {}", onoff(v.lights_master_on)),
            on_off_color(v.lights_master_on),
        ),
        (
            Action::ToggleRcs,
            format!("RCS: {}", onoff(v.rcs_on)),
            on_off_color(v.rcs_on),
        ),
        (
            Action::OpenAttitudePicker,
            format!("Attitude: {} ▾", val_or(&v.attitude_mode, "manual")),
            ACCENT,
        ),
    ];
    for (i, e) in v.engines.iter().enumerate() {
        items.push((
            Action::EngineToggle(i),
            format!("Engine {}: {}", e.index, onoff(e.active)),
            on_off_color(e.active),
        ));
    }
    for (i, l) in v.lights.iter().enumerate() {
        items.push((
            Action::LightToggle(i),
            format!("Light {}: {}", l.index, onoff(l.on)),
            on_off_color(l.on),
        ));
    }
    items.push((Action::RefillFuel, "Refill fuel ◆".to_string(), debug_color));
    items.push((
        Action::RefillBattery,
        "Refill battery ◆".to_string(),
        debug_color,
    ));
    items.push((Action::WarpDown, "Warp ÷2 ◆".to_string(), debug_color));
    items.push((Action::WarpUp, "Warp ×2 ◆".to_string(), debug_color));
    items
}

// ---- modal picker -----------------------------------------------------------------------------

/// Draws the modal list-picker centered over the screen and records its hit-test rects back into
/// `app.picker` (outer `area` + per-option `item_rects`) for the next mouse event. A picker is a
/// menu, so it gets a (subtle) background fill + `Clear` underneath to stay legible over the game —
/// the one place transparency is traded for readability.
fn render_picker(f: &mut Frame, app: &mut App) {
    let screen = f.area();
    let Some(p) = app.picker.as_mut() else {
        return;
    };
    let n = p.options.len() as u16;
    let width = 30u16.min(screen.width.saturating_sub(2)).max(10);
    // Cap the body to what fits (leave room for the border); scroll the rest.
    let max_body = screen.height.saturating_sub(2).max(1);
    let body = n.min(max_body);
    let height = body + 2;
    let x = screen.x + screen.width.saturating_sub(width) / 2;
    let y = screen.y + screen.height.saturating_sub(height) / 2;
    let popup = Rect {
        x,
        y,
        width,
        height,
    };
    p.area = popup;

    f.render_widget(Clear, popup);
    let block = Block::bordered()
        .border_style(Style::new().fg(ACCENT))
        .title(Span::styled(
            format!(" {} ", p.title),
            Style::new().fg(ACCENT).add_modifier(Modifier::BOLD),
        ))
        .style(Style::new().bg(BAR_BG));
    let inner = block.inner(popup);
    f.render_widget(block, popup);

    // Scroll the selection into the visible window.
    let visible = inner.height as usize;
    if p.selected < p.offset {
        p.offset = p.selected;
    } else if visible > 0 && p.selected >= p.offset + visible {
        p.offset = p.selected + 1 - visible;
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
            ("▶ ", Style::new().fg(FOCUS).add_modifier(Modifier::BOLD))
        } else {
            ("  ", Style::new().fg(VALUE))
        };
        // "manual" is the unset option — label it so that's obvious.
        let text = if p.options[idx].eq_ignore_ascii_case("manual") {
            "manual (off)".to_string()
        } else {
            p.options[idx].to_string()
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

// ---- span / formatting helpers ----------------------------------------------------------------

fn kv<'a>(k: &'a str, v: &str) -> Span<'a> {
    Span::styled(format!("{k} {v}  "), Style::new().fg(VALUE))
}

fn label(s: &str) -> Span<'static> {
    Span::styled(s.to_string(), Style::new().fg(LABEL))
}

fn section(name: &str) -> Line<'static> {
    Line::from(Span::styled(
        name.to_string(),
        Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
    ))
}

fn kvline(pairs: &[(&str, String)]) -> Line<'static> {
    let mut spans = vec![Span::raw("  ")];
    for (k, v) in pairs {
        spans.push(Span::styled(format!("{k} "), Style::new().fg(LABEL)));
        spans.push(Span::styled(format!("{v}  "), Style::new().fg(VALUE)));
    }
    Line::from(spans)
}

fn pct_span(frac: f64) -> Span<'static> {
    Span::styled(
        pct(frac),
        Style::new()
            .fg(level_color(frac))
            .add_modifier(Modifier::BOLD),
    )
}

fn on_off(on: bool) -> Span<'static> {
    if on {
        Span::styled("[ON]", Style::new().fg(GOOD).add_modifier(Modifier::BOLD))
    } else {
        Span::styled("[off]", Style::new().fg(LABEL))
    }
}

/// A foreground ratio bar: a colored filled run + a dim empty run (no background — see-through).
fn bar_spans(frac: f64, width: usize, color: Color) -> Vec<Span<'static>> {
    let frac = frac.clamp(0.0, 1.0);
    let filled = (frac * width as f64).round() as usize;
    let filled = filled.min(width);
    vec![
        Span::styled("█".repeat(filled), Style::new().fg(color)),
        Span::styled("░".repeat(width - filled), Style::new().fg(EMPTY)),
    ]
}

fn onoff(on: bool) -> &'static str {
    if on {
        "ON"
    } else {
        "off"
    }
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

fn situation_color(s: &str) -> Color {
    match s.to_ascii_lowercase().as_str() {
        "landed" | "splashed" | "prelaunch" => Color::Blue,
        "freefall" | "orbiting" | "escaping" | "suborbital" => GOOD,
        "flying" | "ascending" | "descending" => WARN,
        _ => VALUE,
    }
}

fn best_alt(v: &Vessel) -> f64 {
    if v.radar_altitude > 0.0 {
        v.radar_altitude
    } else {
        v.barometric_altitude
    }
}

fn val_or<'a>(s: &'a str, fallback: &'a str) -> &'a str {
    if s.is_empty() {
        fallback
    } else {
        s
    }
}

fn trunc(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        s.to_string()
    } else {
        s.chars().take(max.saturating_sub(1)).chain(['…']).collect()
    }
}

fn trim(v: f64) -> String {
    // Up to 3 decimals, trailing zeros stripped.
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

/// The worker's snapshot poll cadence as `<interval>ms (<rate> Hz)` — the read-back refresh rate the
/// header advertises. The rate is the reciprocal of the interval; lower `--interval` for snappier
/// updates (the sampler's in-game `sample_rate_hz` is the other, independent freshness gate).
fn fmt_poll(interval: Duration) -> String {
    let ms = interval.as_millis();
    if ms == 0 {
        return "—".to_string();
    }
    format!("{ms}ms ({} Hz)", trim(1000.0 / ms as f64))
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
