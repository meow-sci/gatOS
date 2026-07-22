//! The ratatui DSKY face (AGC_PLAN §5.1): lamp grid, PROG/VERB/NOUN windows, three big
//! seven-segment registers built from block glyphs, all foreground-only (the purrTTY
//! transparent-TUI rule — only the NO AGC banner gets a background fill).

use ratatui::layout::{Constraint, Direction, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, Paragraph};
use ratatui::Frame;

use super::Dsky;

/// One seven-seg glyph, 3×5 cells.
fn seg(c: char) -> [&'static str; 5] {
    match c {
        '0' => ["███", "█ █", "█ █", "█ █", "███"],
        '1' => ["  █", "  █", "  █", "  █", "  █"],
        '2' => ["███", "  █", "███", "█  ", "███"],
        '3' => ["███", "  █", "███", "  █", "███"],
        '4' => ["█ █", "█ █", "███", "  █", "  █"],
        '5' => ["███", "█  ", "███", "  █", "███"],
        '6' => ["███", "█  ", "███", "█ █", "███"],
        '7' => ["███", "  █", "  █", "  █", "  █"],
        '8' => ["███", "█ █", "███", "█ █", "███"],
        '9' => ["███", "█ █", "███", "  █", "███"],
        '+' => ["   ", " █ ", "███", " █ ", "   "],
        '-' => ["   ", "   ", "███", "   ", "   "],
        ' ' => ["   ", "   ", "   ", "   ", "   "],
        _ => ["███", "  █", " █ ", "   ", " █ "],
    }
}

fn lamp(label: &str, on: bool, on_color: Color) -> Span<'static> {
    let s = format!(" {label:^9} ");
    if on {
        Span::styled(s, Style::default().fg(Color::Black).bg(on_color).add_modifier(Modifier::BOLD))
    } else {
        Span::styled(s, Style::default().fg(Color::DarkGray))
    }
}

/// Renders the DSKY into `area`. `flash_off` = the VN-flash duty phase says digits are dark
/// right now (ch 0163 b6 semantics: "verb/noun digits OFF now" while flashing, 1.28 s / 75%).
pub fn render(f: &mut Frame, area: Rect, d: &Dsky, connected: bool, flash_off: bool, title: &str) {
    let block = Block::default().borders(Borders::ALL).title(format!(" {title} "));
    let inner = block.inner(area);
    f.render_widget(block, area);

    if !connected {
        let banner = Paragraph::new(Line::from(Span::styled(
            "  NO AGC — waiting for yaAGC on the socket  ",
            Style::default().fg(Color::White).bg(Color::Red).add_modifier(Modifier::BOLD),
        )));
        f.render_widget(banner, Rect { height: 1.min(inner.height), ..inner });
        return;
    }

    let cols = Layout::default()
        .direction(Direction::Horizontal)
        .constraints([Constraint::Length(38), Constraint::Min(28)])
        .split(inner);

    // ---- left: the lamp grid (LM set) ----
    let amber = Color::Yellow;
    let white = Color::White;
    let lamps = vec![
        Line::from(vec![lamp("UPLINK", d.uplink_acty, white), Span::raw(" "), lamp("TEMP", d.temp, amber)]),
        Line::from(vec![lamp("NO ATT", d.no_att, white), Span::raw(" "), lamp("GIMBAL LOCK", d.gimbal_lock, amber)]),
        Line::from(vec![lamp("STBY", d.stby, white), Span::raw(" "), lamp("PROG", d.prog_lamp, amber)]),
        Line::from(vec![lamp("KEY REL", d.key_rel, white), Span::raw(" "), lamp("RESTART", d.restart, amber)]),
        Line::from(vec![lamp("OPR ERR", d.opr_err, white), Span::raw(" "), lamp("TRACKER", d.tracker, amber)]),
        Line::from(vec![lamp("PRIO DSP", d.prio_disp, white), Span::raw(" "), lamp("ALT", d.alt, amber)]),
        Line::from(vec![lamp("NO DAP", d.no_dap, white), Span::raw(" "), lamp("VEL", d.vel, amber)]),
        Line::from(vec![lamp("AGC WARN", d.agc_warn, Color::Red), Span::raw(" "), lamp(
            if d.engine_on { "ENG ON" } else if d.engine_off { "ENG OFF" } else { "ENGINE" },
            d.engine_on || d.engine_off,
            if d.engine_on { Color::Green } else { amber },
        )]),
    ];
    f.render_widget(Paragraph::new(lamps), cols[0]);

    // ---- right: COMP ACTY + PROG/VERB/NOUN + R1-R3 ----
    let green = Style::default().fg(Color::Green).add_modifier(Modifier::BOLD);
    let mut lines: Vec<Line> = Vec::new();
    let vn_dark = flash_off && d.vn_flash;
    let two = |a: char, b: char| format!("{a}{b}");
    lines.push(Line::from(vec![
        lamp("COMP ACTY", d.comp_acty, Color::Green),
        Span::raw("   PROG "),
        Span::styled(two(d.prog[0], d.prog[1]), green),
    ]));
    lines.push(Line::from(vec![
        Span::raw(" VERB "),
        Span::styled(if vn_dark { "  ".into() } else { two(d.verb[0], d.verb[1]) }, green),
        Span::raw("        NOUN "),
        Span::styled(if vn_dark { "  ".into() } else { two(d.noun[0], d.noun[1]) }, green),
    ]));
    lines.push(Line::default());
    // Big registers: sign + 5 digits, 5 rows each.
    for reg in 0..3 {
        let sign = d.sign[reg].ch();
        for row in 0..5 {
            let mut spans = vec![Span::raw(if row == 2 {
                format!("R{} ", reg + 1)
            } else {
                "   ".into()
            })];
            spans.push(Span::styled(seg(sign)[row].to_string(), green));
            spans.push(Span::raw(" "));
            for dig in 0..5 {
                spans.push(Span::styled(seg(d.r[reg][dig])[row].to_string(), green));
                spans.push(Span::raw(" "));
            }
            lines.push(Line::from(spans));
        }
        lines.push(Line::default());
    }
    lines.push(Line::from(Span::styled(
        "keys: 0-9 v n + - ⏎(ENTR) c(CLR) p(PRO) k(KEY REL) r(RSET) · Tab panels · q quit",
        Style::default().fg(Color::DarkGray),
    )));
    f.render_widget(Paragraph::new(lines), cols[1]);
}
