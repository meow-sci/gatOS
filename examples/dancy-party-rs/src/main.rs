//! dancy-party-rs — a ratatui **party-lights console** over the gatOS `/sim` filesystem.
//!
//! Pick vessels (multi-select), build an ordered color palette — typed as RGB/hex or fuzzy-picked
//! from the bundled XKCD survey — set a per-color time, and hit **LETS PARTY!**. A worker thread then
//! cross-fades the palette at `--hz` and pulses each light's deploy `goal`, broadcasting the frame to
//! every `lights/<n>/color` + `lights/<n>/goal` file on the selected vessels. Hit **STOP, MY EYES**
//! (or quit) and every light snaps back to white.
//!
//! Architecture (mirrors the sibling examples): one worker thread owns the [`source::Source`] and runs
//! the animation loop; the main thread runs the render + input loop and never touches I/O. The light
//! tree is discovered **once** (re-walking a 9p tree is costly) and cached on the worker.

mod app;
mod color;
mod party;
mod source;
mod ui;
mod xkcd;

use std::io::{self, Stdout};
use std::path::Path;
use std::sync::mpsc;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{
    self, DisableMouseCapture, EnableMouseCapture, Event, KeyEventKind,
};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::Terminal;

use app::App;
use source::{spawn_worker, FromWorker, FsSource, HttpSource, Source, ToWorker, WriteConfig};

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> io::Result<()> {
    let config = match Config::from_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("dancy-party-rs: {e}");
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

/// Which backend serves the `/sim` light fields.
enum SourceKind {
    Fs(String),
    Http(String),
}

struct Config {
    source: SourceKind,
    hz: f64,
    steps: u32,
    stagger_ms: f64,
    write_cfg: WriteConfig,
    help: bool,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut url: Option<String> = None;
        let mut root: Option<String> = None;
        let mut hz: Option<f64> = None;
        let mut steps: Option<u32> = None;
        let mut stagger_ms: Option<f64> = None;
        let mut async_writes = false;
        let mut writers: Option<usize> = None;
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--url" => url = args.next(),
                "--root" => root = args.next(),
                "--hz" => {
                    hz = match args.next().map(|s| s.parse::<f64>()) {
                        Some(Ok(v)) if (1.0..=240.0).contains(&v) => Some(v),
                        _ => return Err("--hz wants a number in 1..240".into()),
                    }
                }
                "--steps" => {
                    steps = match args.next().map(|s| s.parse::<u32>()) {
                        Some(Ok(v)) if v <= 1000 => Some(v),
                        _ => return Err("--steps wants a number in 0..1000 (0 = continuous)".into()),
                    }
                }
                "--stagger-ms" => {
                    stagger_ms = match args.next().map(|s| s.parse::<f64>()) {
                        Some(Ok(v)) if (0.0..=60_000.0).contains(&v) => Some(v),
                        _ => return Err("--stagger-ms wants a number in 0..60000 (0 = lockstep)".into()),
                    }
                }
                "--async" => async_writes = true,
                "--writers" => {
                    writers = match args.next().map(|s| s.parse::<usize>()) {
                        Some(Ok(v)) if (1..=64).contains(&v) => Some(v),
                        _ => return Err("--writers wants a number in 1..64".into()),
                    }
                }
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        Ok(Self {
            source: resolve_source(url, root),
            hz: hz.unwrap_or(60.0),
            steps: steps.unwrap_or(0),
            stagger_ms: stagger_ms.unwrap_or(0.0),
            write_cfg: WriteConfig {
                async_writes,
                writers: writers.unwrap_or(8),
            },
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

fn build_source(kind: SourceKind) -> Arc<dyn Source> {
    match kind {
        SourceKind::Fs(root) => Arc::new(FsSource::new(root)),
        SourceKind::Http(base) => Arc::new(HttpSource::new(base)),
    }
}

fn print_help() {
    println!("dancy-party-rs \u{2014} a party-lights console over gatOS /sim");
    println!();
    println!("USAGE: dancy-party-rs [--root <dir> | --url <base>] [--hz <n>] [--steps <n>] [--stagger-ms <n>] [--async [--writers <n>]]");
    println!();
    println!("  --root <dir>   read/write the /sim mount at <dir> (default: /sim when present)");
    println!("  --url <base>   use HTTP /v1/fs instead (e.g. $GATOS_HTTP, http://127.0.0.1:4242/v1)");
    println!("  --hz <n>       party color-update rate, 1..240 (default 60)");
    println!();
    println!("Performance experiment knobs (the fade can lag with many lights — use these to find why):");
    println!("  --steps <n>    quantize each color fade to <n> discrete values, 0..1000 (default 0 =");
    println!("                 continuous). Fewer steps = fewer distinct 9p writes; 1 = hard cut, no fade.");
    println!("  --stagger-ms <n>  offset each light by <n> ms so the palette ripples across the lights");
    println!("                 instead of all changing at once, 0..60000 (default 0 = lockstep).");
    println!("  --async        hand light writes to a background thread pool instead of blocking the");
    println!("                 animation loop on each write.");
    println!("  --writers <n>  pool size for --async, 1..64 (default 8).");
    println!("  The party screen shows live write latency / throughput / backlog so you can read off the");
    println!("  per-write cost and whether the loop is keeping up.");
    println!();
    println!("In the guest, no flags are needed: it reads /sim and drives the selected vessels' lights.");
    println!("Vessels screen: \u{2191}\u{2193} move \u{b7} space arm \u{b7} a all \u{b7} r rescan \u{b7} Enter \u{2192} party.");
    println!("Party screen:   a RGB/hex \u{b7} f XKCD picker \u{b7} [ ] reorder \u{b7} d remove \u{b7} Enter/P toggle party.");
}

fn run(terminal: &mut Tui, config: Config) -> io::Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    spawn_worker(source, config.hz, config.write_cfg, cmd_rx, update_tx);

    let mut app = App::new(
        cmd_tx,
        label,
        config.hz,
        config.steps,
        config.stagger_ms,
        config.write_cfg,
    );

    // A short tick keeps the live color band animating smoothly between input events (the worker
    // pushes throttled frames; we just need to redraw to show them).
    let tick = Duration::from_millis(50);
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

    // If we asked the worker to reset the lights on the way out, give it a moment to confirm so we
    // don't leave the rig mid-strobe.
    if app.pending_stop {
        let deadline = Instant::now() + Duration::from_millis(1000);
        while app.pending_stop {
            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else {
                break;
            };
            match update_rx.recv_timeout(remaining) {
                Ok(update) => app.apply(update),
                Err(_) => break,
            }
        }
    }
    Ok(())
}

fn setup_terminal() -> io::Result<Tui> {
    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen, EnableMouseCapture)?;
    Terminal::new(CrosstermBackend::new(stdout))
}

fn restore_terminal(terminal: &mut Tui) -> io::Result<()> {
    disable_raw_mode()?;
    execute!(
        terminal.backend_mut(),
        LeaveAlternateScreen,
        DisableMouseCapture
    )?;
    terminal.show_cursor()
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
