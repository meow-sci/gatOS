//! gatos-dashboard — an interactive ratatui TUI over the gatOS `/v1` HTTP API.
//!
//! Layout: a single worker thread owns the HTTP client and interleaves snapshot polling with command
//! submission (`recv_timeout`); the main thread runs the render + input loop. Keyboard and mouse both
//! drive the same actions (see `app.rs`); the UI stays transparent over the game (see `ui.rs`).

mod api;
mod app;
mod ui;

use std::io::{self, Stdout};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::Duration;

use anyhow::Result;
use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{
    self, DisableMouseCapture, EnableMouseCapture, Event, KeyEventKind,
};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::Terminal;

use api::{Client, Command};
use app::{App, Update};

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> Result<()> {
    let config = Config::from_args();
    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(&mut terminal, config);
    restore_terminal(&mut terminal)?;
    result
}

/// Default pane-border weight (0–100) when `--border-weight` is not given. It controls how much
/// border is drawn over the game — full box ≥67, a single top rule ≥34, nothing below — so lowering
/// it lets the game show through (real transparency; a terminal can't alpha-blend an opaque line).
/// Overridable per-run via the flag and live via the in-app settings overlay.
const DEFAULT_BORDER_WEIGHT: u8 = 100;

struct Config {
    url: String,
    interval: Duration,
    border_weight: u8,
}

impl Config {
    fn from_args() -> Self {
        // $GATOS_HTTP is the guest-side base (e.g. http://sim:4242/v1); fall back to the host loopback.
        let mut url =
            std::env::var("GATOS_HTTP").unwrap_or_else(|_| "http://127.0.0.1:4242/v1".to_string());
        // Snapshot poll cadence (read-back refresh). Default 50 ms (~20 Hz) for snappy feedback;
        // overridable via --interval, floored at 10 ms to avoid hammering the HTTP server.
        let mut interval = Duration::from_millis(50);
        // Pane-border weight (0–100); tunable live in the settings overlay (see DEFAULT_BORDER_WEIGHT).
        let mut border_weight = DEFAULT_BORDER_WEIGHT;
        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--url" => {
                    if let Some(u) = args.next() {
                        url = u;
                    }
                }
                "--interval" => {
                    if let Some(ms) = args.next().and_then(|s| s.parse::<u64>().ok()) {
                        interval = Duration::from_millis(ms.max(10));
                    }
                }
                "--border-weight" => {
                    if let Some(v) = args.next().and_then(|s| s.parse::<u8>().ok()) {
                        border_weight = v.min(100);
                    }
                }
                "-h" | "--help" => {
                    print_help();
                    std::process::exit(0);
                }
                _ => {}
            }
        }
        Self {
            url,
            interval,
            border_weight,
        }
    }
}

fn print_help() {
    println!("gatos-dashboard — interactive TUI for the gatOS /sim HTTP API");
    println!();
    println!("USAGE: gatos-dashboard [--url <base>] [--interval <ms>] [--border-weight <0-100>]");
    println!("  --url <base>     API base (default: $GATOS_HTTP, else http://127.0.0.1:4242/v1)");
    println!("  --interval <ms>  snapshot poll interval, min 10 (default 50)");
    println!("  --border-weight <0-100>  pane borders over the game: full box >=67, top rule >=34,");
    println!("                           off below (default 100)");
}

fn run(terminal: &mut Tui, config: Config) -> Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<Command>();
    let (update_tx, update_rx) = mpsc::channel::<Update>();
    spawn_worker(Client::new(config.url), config.interval, cmd_rx, update_tx);

    let mut app = App::new(cmd_tx, config.interval, config.border_weight);
    let tick = Duration::from_millis(100);
    while !app.should_quit {
        while let Ok(update) = update_rx.try_recv() {
            app.apply(update);
        }
        terminal.draw(|f| ui::render(f, &mut app))?;
        if event::poll(tick)? {
            match event::read()? {
                Event::Key(k) if k.kind == KeyEventKind::Press => app.on_key(k),
                Event::Mouse(m) => app.on_mouse(m),
                _ => {}
            }
        }
    }
    Ok(())
    // Dropping `app` drops `cmd_tx`; the worker's recv_timeout then returns Disconnected and it exits.
}

/// One thread owns the client: poll the snapshot, then wait one interval for a command (executing it
/// promptly and re-polling on the next loop so state reflects the command). All outcomes — including
/// connection errors — flow back as `Update`s; `/v1/status` is fetched once per (re)connect.
fn spawn_worker(client: Client, interval: Duration, cmd_rx: Receiver<Command>, tx: Sender<Update>) {
    thread::spawn(move || {
        let mut need_status = true;
        loop {
            if need_status {
                if let Ok(status) = client.status() {
                    let _ = tx.send(Update::Status(status));
                    need_status = false;
                }
            }
            match client.snapshot() {
                Ok(snap) => {
                    let _ = tx.send(Update::Snapshot(snap));
                }
                Err(e) => {
                    let _ = tx.send(Update::Error(e.to_string()));
                    need_status = true; // refetch status once we reconnect
                }
            }
            match cmd_rx.recv_timeout(interval) {
                Ok(cmd) => {
                    let _ = tx.send(Update::CommandDone(client.command(&cmd)));
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => return,
            }
        }
    });
}

fn setup_terminal() -> Result<Tui> {
    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen, EnableMouseCapture)?;
    Ok(Terminal::new(CrosstermBackend::new(stdout))?)
}

fn restore_terminal(terminal: &mut Tui) -> Result<()> {
    disable_raw_mode()?;
    execute!(
        terminal.backend_mut(),
        LeaveAlternateScreen,
        DisableMouseCapture
    )?;
    terminal.show_cursor()?;
    Ok(())
}

/// Restore the terminal on panic so a crash doesn't leave the user in raw-mode/alt-screen.
fn install_panic_hook() {
    let original = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let _ = disable_raw_mode();
        let _ = execute!(io::stdout(), LeaveAlternateScreen, DisableMouseCapture);
        original(info);
    }));
}
