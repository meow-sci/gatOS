//! Rendering: a bordered HUD — vessel state + frame-derived flight numbers + the guidance/autopilot
//! block — plus a keys/status footer. Per the project's transparent-TUI rule the panel leaves
//! backgrounds unset (purrTTY shows the game through) and colors foregrounds only. (Plan §9; the
//! trajectory canvas is a later polish item.)

use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, Paragraph};
use ratatui::Frame;

use crate::app::App;
use crate::guidance::autopilot::Phase;
use crate::sim::Telemetry;

const KEY: Color = Color::DarkGray;
const VAL: Color = Color::Gray;
const ACCENT: Color = Color::LightCyan;

/// A short label and color for a flight phase.
fn phase_label(p: Phase) -> (&'static str, Color) {
    match p {
        Phase::Idle => ("IDLE", VAL),
        Phase::Burn => ("BURN", Color::LightGreen),
        Phase::Infeasible => ("NO SOLUTION", Color::LightRed),
        Phase::Touchdown => ("TOUCHDOWN", Color::LightCyan),
        Phase::Abort => ("ABORT", Color::LightRed),
    }
}

pub fn render(f: &mut Frame, app: &App) {
    let area = f.area();
    if area.width == 0 || area.height == 0 {
        return;
    }

    let (phase_txt, phase_col) = app
        .guidance
        .map(|g| phase_label(g.phase))
        .unwrap_or(("MONITOR", VAL));
    let title = Line::from(vec![
        Span::styled("land-o-matic", Style::default().fg(ACCENT).add_modifier(Modifier::BOLD)),
        Span::raw(" \u{b7} "),
        Span::styled(phase_txt, Style::default().fg(phase_col).add_modifier(Modifier::BOLD)),
    ]);
    let block = Block::default().borders(Borders::ALL).title(title);
    let inner = block.inner(area);
    f.render_widget(block, area);

    let lines = match &app.telemetry {
        Some(t) => state_lines(app, t),
        None => vec![
            Line::from(Span::styled(
                app.status.clone(),
                Style::default().fg(if app.status_err { Color::Red } else { VAL }),
            )),
            Line::from(""),
            keys_line(),
        ],
    };
    f.render_widget(Paragraph::new(lines), inner);
}

fn state_lines(app: &App, t: &Telemetry) -> Vec<Line<'static>> {
    let mut lines = vec![
        kv2(
            "vessel",
            &t.id,
            "state",
            &format!("{}{}", t.sit, if t.controlled { "" } else { " (uncontrolled)" }),
        ),
        kv(
            "parent",
            &t.parent.clone().unwrap_or_else(|| "\u{2014}".into()),
        ),
        kv2(
            "altitude",
            &format!("radar {}", meters(t.alt.radar)),
            "",
            &format!("baro {}", meters(t.alt.baro)),
        ),
        kv2(
            "speed",
            &format!("surf {:.1}", t.vel.surf),
            "",
            &format!("orb {:.1}   inr {:.1}  m/s", t.vel.orb, t.vel.inr),
        ),
        kv2(
            "mass",
            &format!("total {:.0}", t.mass.t),
            "",
            &format!("dry {:.0}   prop {:.0}  kg", t.mass.d, t.mass.p),
        ),
        kv(
            "att q",
            &format!(
                "{:.3} {:.3} {:.3} {:.3}  (Body\u{2192}CCI)",
                t.att_q[0], t.att_q[1], t.att_q[2], t.att_q[3]
            ),
        ),
        kv("pos cci", &vec3(t.pos_cci)),
        kv("vel cci", &vec3(t.vel_cci)),
    ];

    if let Some(d) = &app.derived {
        lines.push(kv2(
            "v/h spd",
            &format!("v {:+.1} m/s", d.vertical_speed),
            "",
            &format!("h {:.1} m/s   g {:.2} m/s\u{b2}", d.horizontal_speed, d.gravity),
        ));
        lines.push(kv(
            "pointing",
            &format!(
                "pitch {:.0}\u{b0} from up   retro err {:.1}\u{b0}",
                d.pitch_from_up_deg, d.retro_error_deg
            ),
        ));
    }

    // ---- guidance / autopilot ----
    lines.push(Line::from(""));
    if let Some(g) = &app.guidance {
        let (ptxt, pcol) = phase_label(g.phase);
        lines.push(Line::from(vec![
            Span::styled(format!("{:<9}", "guidance"), Style::default().fg(KEY)),
            Span::styled(ptxt, Style::default().fg(pcol).add_modifier(Modifier::BOLD)),
            Span::styled(
                format!("   G-limit {:.1} g", app.g_limit),
                Style::default().fg(VAL),
            ),
        ]));
        lines.push(kv2(
            "throttle",
            &format!("{:.0}%", g.throttle * 100.0),
            "",
            &format!("peak {:.2} g", g.peak_g),
        ));
        if g.phase == Phase::Burn {
            lines.push(kv2(
                "tgo",
                &format!("{:.0} s", g.tgo),
                "",
                &format!("fuel@td {:.0} kg", g.predicted_mass),
            ));
        }
    } else {
        lines.push(kv(
            "guidance",
            &format!("disarmed   G-limit {:.1} g   press [e] to engage", app.g_limit),
        ));
    }

    if let Some(b) = &app.body {
        lines.push(kv(
            "body",
            &format!(
                "\u{3bc} {:.4e}   R {}   \u{3c9} {:.4e} rad/s",
                b.mu,
                meters(b.radius),
                b.rotation_rate
            ),
        ));
    }

    if let Some(o) = &t.orbit {
        lines.push(kv(
            "orbit",
            &format!(
                "ap {}  pe {}  ecc {:.3}  inc {:.1}\u{b0}  T {:.0}s",
                meters(o.ap),
                meters(o.pe),
                o.ecc,
                o.inc,
                o.period
            ),
        ));
    }

    if let Some(p) = &t.power {
        let batt = p
            .battery
            .map(|b| format!("  batt {:.0}%", b * 100.0))
            .unwrap_or_default();
        lines.push(kv(
            "power",
            &format!("prod {:.1} W  cons {:.1} W{}", p.prod, p.cons, batt),
        ));
    }

    lines.push(Line::from(""));
    lines.push(Line::from(Span::styled(
        app.status.clone(),
        Style::default().fg(if app.status_err { Color::LightRed } else { ACCENT }),
    )));
    lines.push(keys_line());
    lines.push(Line::from(Span::styled(
        format!(
            "ut {:.1} \u{b7} seq {} \u{b7} warp {:.0}\u{d7} \u{b7} {}",
            t.ut, t.seq, t.warp, app.label
        ),
        Style::default().fg(KEY),
    )));
    lines
}

// ---- formatting helpers -------------------------------------------------------------------------

fn keys_line() -> Line<'static> {
    Line::from(Span::styled(
        "[e] engage  [a] abort  \u{2191}/\u{2193} G-limit  [q] quit",
        Style::default().fg(KEY),
    ))
}

fn kv(key: &str, val: &str) -> Line<'static> {
    Line::from(vec![
        Span::styled(format!("{key:<9}"), Style::default().fg(KEY)),
        Span::styled(val.to_string(), Style::default().fg(VAL)),
    ])
}

/// A key plus two value columns (the second continues the line, e.g. `radar … baro …`).
fn kv2(key: &str, a: &str, _spacer: &str, b: &str) -> Line<'static> {
    Line::from(vec![
        Span::styled(format!("{key:<9}"), Style::default().fg(KEY)),
        Span::styled(format!("{a:<18}"), Style::default().fg(VAL)),
        Span::styled(b.to_string(), Style::default().fg(VAL)),
    ])
}

fn meters(x: f64) -> String {
    if x.abs() >= 1000.0 {
        format!("{:.2} km", x / 1000.0)
    } else {
        format!("{x:.0} m")
    }
}

fn vec3(v: [f64; 3]) -> String {
    format!("{:.0} {:.0} {:.0}", v[0], v[1], v[2])
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::app::FromWorker;
    use crate::sim::Tick;
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;
    use std::sync::mpsc;

    fn app() -> App {
        let (tx, _rx) = mpsc::channel();
        App::new(tx, "fs:/sim".into())
    }

    fn render_to_text(app: &App, w: u16, h: u16) -> String {
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

    fn telemetry() -> Telemetry {
        serde_json::from_str(
            r#"{"seq":42,"ut":12345.6,"warp":1,"id":"Kerbal-1","sit":"Freefall","controlled":true,
                "parent":"Kerbin","pos_cci":[600000,0,1000],"vel_cci":[10,0,-82],
                "vel":{"orb":2210.5,"surf":88.0,"inr":2210.5},"alt":{"baro":1255,"radar":1240},
                "mass":{"t":5980,"d":4800,"p":1180},"att_q":[0.1,0.2,0.3,0.927],
                "power":{"prod":0,"cons":0}}"#,
        )
        .unwrap()
    }

    fn tick(telemetry: Option<Telemetry>) -> FromWorker {
        FromWorker::Tick {
            tick: Tick {
                connected: true,
                telemetry,
                body: None,
            },
            guidance: None,
            status: None,
        }
    }

    #[test]
    fn renders_state_readout() {
        let mut a = app();
        a.apply(tick(Some(telemetry())));
        let text = render_to_text(&a, 76, 22);
        assert!(text.contains("land-o-matic"));
        assert!(text.contains("Kerbal-1"));
        assert!(text.contains("radar"));
        assert!(text.contains("1.24 km"));
        assert!(text.contains("guidance"));
        assert!(text.contains("engage"));
    }

    #[test]
    fn renders_message_when_no_vessel() {
        let mut a = app();
        a.apply(tick(None));
        let text = render_to_text(&a, 40, 8);
        assert!(text.contains("no active vessel"));
    }

    #[test]
    fn tiny_sizes_never_panic() {
        let mut a = app();
        a.apply(tick(Some(telemetry())));
        for &(w, h) in &[(1, 1), (2, 2), (5, 3), (10, 4), (20, 2)] {
            let _ = render_to_text(&a, w, h);
        }
    }
}
