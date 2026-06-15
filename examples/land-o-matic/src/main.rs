//! land-o-matic — a powered-descent landing-guidance TUI over the gatOS `/sim` filesystem, built with
//! ratatui. It fuses two real flight-guidance algorithms: **G-FOLD** (convex fuel-optimal powered
//! descent — the planner/replanner) and **UPFG** (the Space Shuttle's explicit guidance, via PEGAS —
//! the closed-loop steering law). See `LANDING_PROGRAM_PLAN.md` for the full design and the
//! reference-frame contract.
//!
//! Run it **in the guest** (`apk add --no-cache cargo rust && cargo run --release`): it reads the
//! `/sim` mount and drives the active vessel. For host-side dev, point it at a fixture directory with
//! `--root <dir>` or the mod's HTTP mirror with `--url <base>`.
//!
//! Architecture (mirrors the sibling examples): one worker thread owns the [`sim::Source`] and polls
//! the active vessel once per `--interval`, while the main thread runs the render + input loop, so the
//! UI never blocks on I/O. (M0 is read-only; control writes + guidance arrive in M3.)

use std::io::{self, Stdout};
use std::path::Path;
use std::sync::mpsc::{self, Sender};
use std::thread;
use std::time::Duration;

use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{self, Event, KeyEventKind};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::Terminal;

use land_o_matic::app::App;
use land_o_matic::sim::{self, FromWorker, FsSource, HttpSource, Source};
use land_o_matic::ui;

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> io::Result<()> {
    let config = match Config::from_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("land-o-matic: {e}");
            std::process::exit(2);
        }
    };
    if config.help {
        print_help();
        return Ok(());
    }
    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(&mut terminal, config);
    restore_terminal(&mut terminal)?;
    result
}

/// Which backend serves the active vessel's `/sim` fields.
enum SourceKind {
    Fs(String),
    Http(String),
}

struct Config {
    source: SourceKind,
    interval: Duration,
    help: bool,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut url: Option<String> = None;
        let mut root: Option<String> = None;
        let mut interval_ms: Option<u64> = None;
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--url" => url = args.next(),
                "--root" => root = args.next(),
                "--interval" => interval_ms = args.next().and_then(|s| s.parse().ok()),
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        // Default 100 ms ≈ the 10 Hz telemetry cadence; floor at 10 ms.
        let interval = Duration::from_millis(interval_ms.unwrap_or(100).max(10));
        Ok(Self {
            source: resolve_source(url, root),
            interval,
            help,
        })
    }
}

/// Picks the backend: explicit `--url`/`--root` win; otherwise default to the real `/sim` mount when
/// present (the in-guest case), else `$GATOS_HTTP` (the host-dev case), else `/sim` anyway.
fn resolve_source(url: Option<String>, root: Option<String>) -> SourceKind {
    if let Some(u) = url {
        return SourceKind::Http(u);
    }
    if let Some(r) = root {
        return SourceKind::Fs(r);
    }
    if Path::new("/sim").is_dir() {
        return SourceKind::Fs("/sim".to_string());
    }
    if let Ok(http) = std::env::var("GATOS_HTTP") {
        return SourceKind::Http(http);
    }
    SourceKind::Fs("/sim".to_string())
}

fn build_source(kind: SourceKind) -> Box<dyn Source> {
    match kind {
        SourceKind::Fs(root) => Box::new(FsSource::new(root)),
        SourceKind::Http(base) => Box::new(HttpSource::new(base)),
    }
}

fn print_help() {
    println!("land-o-matic \u{2014} powered-descent landing guidance over gatOS /sim");
    println!();
    println!("USAGE: land-o-matic [--root <dir> | --url <base>] [--interval <ms>]");
    println!();
    println!("  --root <dir>     read the /sim mount at <dir> (default: /sim when present)");
    println!("  --url <base>     read via HTTP /v1/fs instead (e.g. $GATOS_HTTP)");
    println!("  --interval <ms>  poll cadence, min 10 (default 100 \u{2248} 10 Hz telemetry)");
    println!();
    println!("In the guest, no flags are needed: it reads /sim and monitors the active vessel.");
    println!("Keys: q / Esc quit.");
}

fn run(terminal: &mut Tui, config: Config) -> io::Result<()> {
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    spawn_worker(source, config.interval, update_tx);

    let mut app = App::new(label);

    let tick = Duration::from_millis(80);
    while !app.should_quit {
        while let Ok(update) = update_rx.try_recv() {
            app.apply(update);
        }
        terminal.draw(|f| ui::render(f, &app))?;
        if event::poll(tick)? {
            if let Event::Key(k) = event::read()? {
                if k.kind == KeyEventKind::Press {
                    app.on_key(k);
                }
            }
        }
    }
    Ok(())
    // Dropping `update_rx` (on quit) makes the worker's next send fail, so it exits.
}

/// One thread owns the source: poll the active vessel, push it to the UI, sleep one interval, repeat.
fn spawn_worker(source: Box<dyn Source>, interval: Duration, tx: Sender<FromWorker>) {
    thread::spawn(move || loop {
        let tick = sim::poll(&*source);
        if tx.send(FromWorker::Tick(tick)).is_err() {
            return;
        }
        thread::sleep(interval);
    });
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

/// Restore the terminal on panic so a crash doesn't leave the user in raw-mode/alt-screen.
fn install_panic_hook() {
    let original = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let _ = disable_raw_mode();
        let _ = execute!(io::stdout(), LeaveAlternateScreen);
        original(info);
    }));
}
