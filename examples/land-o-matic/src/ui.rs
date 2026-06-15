//! Rendering. For M0 a read-only state HUD: a bordered card of the active vessel's telemetry. Per the
//! project's transparent-TUI rule the panel leaves backgrounds unset (purrTTY shows the game through)
//! and colors foregrounds only. The richer guidance HUD — trajectory canvas, throttle/G gauges, input
//! fields — arrives in M3 (`LANDING_PROGRAM_PLAN.md` §9).

use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, Paragraph};
use ratatui::Frame;

use crate::app::App;
use crate::sim::Telemetry;

const KEY: Color = Color::DarkGray;
const VAL: Color = Color::Gray;
const ACCENT: Color = Color::LightCyan;

pub fn render(f: &mut Frame, app: &App) {
    let area = f.area();
    if area.width == 0 || area.height == 0 {
        return;
    }

    let title = Line::from(vec![
        Span::styled("land-o-matic", Style::default().fg(ACCENT).add_modifier(Modifier::BOLD)),
        Span::styled(" \u{b7} MONITOR", Style::default().fg(VAL)),
    ]);
    let block = Block::default().borders(Borders::ALL).title(title);
    let inner = block.inner(area);
    f.render_widget(block, area);

    let lines = match &app.telemetry {
        Some(t) => state_lines(app, t),
        None => vec![Line::from(Span::styled(
            app.status.clone(),
            Style::default().fg(if app.status_err { Color::Red } else { VAL }),
        ))],
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
        format!(
            "ut {:.1} \u{b7} seq {} \u{b7} warp {:.0}\u{d7} \u{b7} {}",
            t.ut, t.seq, t.warp, app.label
        ),
        Style::default().fg(KEY),
    )));
    lines
}

// ---- formatting helpers -------------------------------------------------------------------------

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
    use crate::sim::{FromWorker, Tick};
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;

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
                "parent":"Kerbin","pos_cci":[600000,0,1000],"pos_ecl":[0,0,0],"vel_cci":[10,0,-82],
                "vel":{"orb":2210.5,"surf":88.0,"inr":2210.5},"alt":{"baro":1255,"radar":1240},
                "mass":{"t":5980,"d":4800,"p":1180},"att_q":[0.1,0.2,0.3,0.927],
                "power":{"prod":0,"cons":0}}"#,
        )
        .unwrap()
    }

    #[test]
    fn renders_state_readout() {
        let mut a = App::new("fs:/sim".into());
        a.apply(FromWorker::Tick(Tick {
            connected: true,
            telemetry: Some(telemetry()),
            body: None,
        }));
        let text = render_to_text(&a, 70, 16);
        assert!(text.contains("land-o-matic"));
        assert!(text.contains("Kerbal-1"));
        assert!(text.contains("radar"));
        assert!(text.contains("1.24 km"));
        assert!(text.contains("prop"));
    }

    #[test]
    fn renders_message_when_no_vessel() {
        let mut a = App::new("fs:/sim".into());
        a.apply(FromWorker::Tick(Tick {
            connected: true,
            ..Tick::default()
        }));
        let text = render_to_text(&a, 40, 8);
        assert!(text.contains("no active vessel"));
    }

    #[test]
    fn tiny_sizes_never_panic() {
        let mut a = App::new("x".into());
        a.apply(FromWorker::Tick(Tick {
            connected: true,
            telemetry: Some(telemetry()),
            body: None,
        }));
        for &(w, h) in &[(1, 1), (2, 2), (5, 3), (10, 4), (20, 2)] {
            let _ = render_to_text(&a, w, h);
        }
    }
}
