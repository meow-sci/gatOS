//! `dsky` — the ratatui DSKY (AGC_PLAN §5): a stock yaAGC socket client. Run it in its own
//! purrTTY tab (or on an in-world quad). Keys per the real keyboard; `p` = timed PRO
//! press/release (600 ms), `P` = 6 s hold (standby needs a real hold). Tab cycles
//! dsky → panel → status.

use std::io::{self, Stdout};
use std::time::{Duration, Instant};

use agc::dsky::{key_wire, ui, Dsky, KeyWire};
use agc::proto::{chan, AgcEvent, AgcPort, SocketPort};
use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{self, Event, KeyCode, KeyEventKind};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::layout::Rect;
use ratatui::style::{Color, Style};
use ratatui::text::Line;
use ratatui::widgets::{Block, Borders, Paragraph};
use ratatui::Terminal;

type Tui = Terminal<CrosstermBackend<Stdout>>;

#[derive(Clone, Copy, PartialEq)]
enum Panel {
    Dsky,
    Switches,
    Status,
}

struct Config {
    port: u16,
    switches_dir: String,
    title: String,
}

fn parse_args() -> Config {
    let mut cfg = Config {
        port: 19797,
        switches_dir: "/run/agc/switches".into(),
        title: "DSKY — LM · Luminary099".into(),
    };
    for a in std::env::args().skip(1) {
        if let Some(v) = a.strip_prefix("--port=") {
            cfg.port = v.parse().unwrap_or(19797);
            if cfg.port == 19697 {
                cfg.title = "DSKY — CM · Comanche055".into();
            }
        } else if let Some(v) = a.strip_prefix("--switches=") {
            cfg.switches_dir = v.into();
        } else if a == "--cm" {
            cfg.port = 19697;
            cfg.title = "DSKY — CM · Comanche055".into();
        } else if a == "--help" || a == "-h" {
            println!("dsky [--port=19797] [--cm] [--switches=/run/agc/switches]");
            std::process::exit(0);
        }
    }
    cfg
}

fn main() -> io::Result<()> {
    let cfg = parse_args();
    install_panic_hook();
    let mut terminal = setup_terminal()?;
    let res = run(&mut terminal, cfg);
    restore_terminal(&mut terminal)?;
    res
}

fn setup_terminal() -> io::Result<Tui> {
    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen)?;
    Terminal::new(CrosstermBackend::new(stdout))
}

fn restore_terminal(terminal: &mut Tui) -> io::Result<()> {
    disable_raw_mode()?;
    execute!(terminal.backend_mut(), LeaveAlternateScreen)?;
    terminal.show_cursor()
}

fn install_panic_hook() {
    let original = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let _ = disable_raw_mode();
        let _ = execute!(io::stdout(), LeaveAlternateScreen);
        original(info);
    }));
}

fn run(terminal: &mut Tui, cfg: Config) -> io::Result<()> {
    let mut port = SocketPort::new(cfg.port);
    let mut dsky = Dsky::new();
    let mut panel = Panel::Dsky;
    let start = Instant::now();
    let mut pro_release: Option<Instant> = None;

    loop {
        // Drain everything the AGC broadcast.
        while let Some(ev) = port.recv() {
            if let AgcEvent::Channel { channel, value } = ev {
                dsky.on_channel(channel, value);
            }
        }
        // Timed PRO release.
        if let Some(t) = pro_release {
            if Instant::now() >= t {
                port.write_channel(chan::CHAN32, 0o20000, Some(0o20000));
                pro_release = None;
            }
        }

        // ch 0163 flash timing: 1.28 s period, digits dark for the last 25%.
        let phase = (start.elapsed().as_millis() % 1280) as f64 / 1280.0;
        let flash_off = phase > 0.75;

        terminal.draw(|f| {
            let area = f.area();
            match panel {
                Panel::Dsky => ui::render(f, area, &dsky, port.connected(), flash_off, &cfg.title),
                Panel::Switches => render_switches(f, area, &cfg.switches_dir),
                Panel::Status => render_status(f, area, &port),
            }
        })?;

        if event::poll(Duration::from_millis(33))? {
            if let Event::Key(k) = event::read()? {
                if k.kind != KeyEventKind::Press {
                    continue;
                }
                match k.code {
                    KeyCode::Char('q') | KeyCode::Esc => return Ok(()),
                    KeyCode::Tab => {
                        panel = match panel {
                            Panel::Dsky => Panel::Switches,
                            Panel::Switches => Panel::Status,
                            Panel::Status => Panel::Dsky,
                        };
                    }
                    KeyCode::Char('P') => {
                        port.write_channel(chan::CHAN32, 0, Some(0o20000));
                        pro_release = Some(Instant::now() + Duration::from_secs(6));
                    }
                    KeyCode::Enter => send_key(&mut port, '\n'),
                    KeyCode::Char(c) => {
                        if panel == Panel::Switches {
                            toggle_switch(&cfg.switches_dir, c);
                        } else {
                            send_key(&mut port, c);
                            if c == 'p' {
                                pro_release = Some(Instant::now() + Duration::from_millis(600));
                            }
                        }
                    }
                    _ => {}
                }
            }
        }
    }
}

fn send_key(port: &mut SocketPort, c: char) {
    match key_wire(c) {
        Some(KeyWire::Code(code)) => port.write_channel(chan::DSKY_KEYS, code, Some(0o37)),
        Some(KeyWire::Pro { .. }) => port.write_channel(chan::CHAN32, 0, Some(0o20000)),
        None => {}
    }
}

fn switch_list(dir: &str) -> Vec<(String, bool)> {
    agc::discretes::SWITCHES
        .iter()
        .map(|(name, default)| {
            let on = match std::fs::read_to_string(format!("{dir}/{name}")) {
                Ok(s) => s.trim() == "1",
                Err(_) => *default,
            };
            (name.to_string(), on)
        })
        .collect()
}

fn toggle_switch(dir: &str, c: char) {
    let idx = match c {
        '1'..='9' => c as usize - '1' as usize,
        '0' => 9,
        'a' => 10,
        _ => return,
    };
    let list = switch_list(dir);
    if let Some((name, on)) = list.get(idx) {
        let _ = std::fs::create_dir_all(dir);
        let _ = std::fs::write(format!("{dir}/{name}"), if *on { "0\n" } else { "1\n" });
    }
}

fn render_switches(f: &mut ratatui::Frame, area: Rect, dir: &str) {
    let block = Block::default().borders(Borders::ALL).title(" cockpit switches (1-9,0,a toggle · Tab next) ");
    let inner = block.inner(area);
    f.render_widget(block, area);
    let mut lines = Vec::new();
    for (i, (name, on)) in switch_list(dir).iter().enumerate() {
        let key = match i {
            0..=8 => (b'1' + i as u8) as char,
            9 => '0',
            _ => 'a',
        };
        let state = if *on { "ON " } else { "off" };
        let style = if *on { Style::default().fg(Color::Green) } else { Style::default().fg(Color::DarkGray) };
        lines.push(Line::styled(format!(" {key}  {state}  {name}"), style));
    }
    lines.push(Line::raw(""));
    lines.push(Line::styled(
        format!(" files: {dir}/<name> — echo 1 > … works from any shell"),
        Style::default().fg(Color::DarkGray),
    ));
    f.render_widget(Paragraph::new(lines), inner);
}

fn render_status(f: &mut ratatui::Frame, area: Rect, port: &SocketPort) {
    let block = Block::default().borders(Borders::ALL).title(" link status (Tab next) ");
    let inner = block.inner(area);
    f.render_widget(block, area);
    let lines = vec![
        Line::raw(format!(" connected: {}", port.connected())),
        Line::raw(format!(" packets sent: {}", port.sent)),
        Line::raw(format!(" events received: {}", port.received)),
        Line::raw(" bridge status: agc log — downlink: agc log lm downlink"),
    ];
    f.render_widget(Paragraph::new(lines), inner);
}
