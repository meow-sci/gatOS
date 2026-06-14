//! gogogo-rs — a tiny floating throttle + ignite/shutdown control panel over the gatOS `/sim`
//! filesystem, built with ratatui.
//!
//! It is *just* two widgets — a drag-slide throttle slider and an ignite/shutdown toggle — sized to
//! live in a small floating terminal window. The layout auto-picks a vertical or horizontal
//! orientation from the terminal dimensions and flips live on resize. It drives the **active vessel**
//! via the `vessels/active/…` control files; when there is no valid active vessel the controls grey
//! out and go inert.
//!
//! Architecture (mirrors the sibling examples): one worker thread owns the [`source::Source`] —
//! polling the active vessel's control state once per `--interval` and applying control writes
//! between polls — while the main thread runs the render + input loop, so the UI never blocks on I/O.

mod app;
mod source;
mod ui;

use std::io::{self, Stdout};
use std::path::Path;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::Duration;

use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{
    self, DisableMouseCapture, EnableMouseCapture, Event, KeyEventKind,
};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::Terminal;

use app::{App, Orient};
use source::{FromWorker, FsSource, HttpSource, Source, ToWorker};

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> io::Result<()> {
    let config = match Config::from_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("gogogo-rs: {e}");
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
    orientation: Orient,
    help: bool,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut url: Option<String> = None;
        let mut root: Option<String> = None;
        let mut interval_ms: Option<u64> = None;
        let mut orientation = Orient::Auto;
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--url" => url = args.next(),
                "--root" => root = args.next(),
                "--interval" => interval_ms = args.next().and_then(|s| s.parse().ok()),
                "--orientation" | "-o" => {
                    orientation = match args.next().as_deref() {
                        Some("v") | Some("vertical") => Orient::Vertical,
                        Some("h") | Some("horizontal") => Orient::Horizontal,
                        Some("auto") | None => Orient::Auto,
                        Some(other) => {
                            return Err(format!(
                                "bad --orientation '{other}' (auto|vertical|horizontal)"
                            ))
                        }
                    }
                }
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        let interval = Duration::from_millis(interval_ms.unwrap_or(120).max(1));
        Ok(Self {
            source: resolve_source(url, root),
            interval,
            orientation,
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
    println!("gogogo-rs \u{2014} a tiny throttle + ignite/shutdown control panel over gatOS /sim");
    println!();
    println!("USAGE: gogogo-rs [--root <dir> | --url <base>] [--interval <ms>]");
    println!("                 [--orientation auto|vertical|horizontal]");
    println!();
    println!("  --root <dir>          read the /sim mount at <dir> (default: /sim when present)");
    println!("  --url <base>          read via HTTP /v1/fs instead (e.g. $GATOS_HTTP)");
    println!("  --interval <ms>       control poll/write cadence, min 1 (default 120)");
    println!("  -o, --orientation     force the layout; default auto-picks from the terminal size");
    println!();
    println!("In the guest, no flags are needed: it reads /sim and drives the active vessel.");
    println!("Drag the slider to set throttle; click the button to ignite/shut down.");
    println!("Keys: space/enter toggle \u{b7} \u{2191}/\u{2193} or -/= nudge \u{b7} 0 cut \u{b7} g full \u{b7} q quit.");
}

fn run(terminal: &mut Tui, config: Config) -> io::Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    spawn_worker(source, config.interval, cmd_rx, update_tx);

    let mut app = App::new(cmd_tx, label, config.orientation);

    let tick = Duration::from_millis(80);
    while !app.should_quit {
        while let Ok(update) = update_rx.try_recv() {
            app.apply(update);
        }
        terminal.draw(|f| ui::render(f, &mut app))?;
        if event::poll(tick)? {
            match event::read()? {
                Event::Key(k) if k.kind == KeyEventKind::Press => app.on_key(k),
                Event::Mouse(m) => app.on_mouse(m),
                _ => {} // Resize and the rest just fall through to the next redraw.
            }
        }
    }
    Ok(())
    // Dropping `app` drops `cmd_tx`; the worker's recv_timeout then returns Disconnected and exits.
}

/// One thread owns the source: each loop polls the active vessel's control state, pushes it, then
/// waits up to one interval for a control command. A burst of drag writes that piled up is coalesced
/// to the last throttle value (ignite/shutdown still execute in order), so a fast sweep is one write.
fn spawn_worker(
    source: Box<dyn Source>,
    interval: Duration,
    rx: Receiver<ToWorker>,
    tx: Sender<FromWorker>,
) {
    thread::spawn(move || loop {
        let poll = source::poll(&*source);
        if tx.send(FromWorker::Poll(poll)).is_err() {
            return;
        }

        match rx.recv_timeout(interval) {
            Ok(first) => {
                let mut batch = vec![first];
                while let Ok(more) = rx.try_recv() {
                    batch.push(more);
                }
                // Only the final throttle in the batch matters; triggers all fire.
                let last_throttle = batch
                    .iter()
                    .rposition(|c| matches!(c, ToWorker::Throttle(_)));
                for (i, cmd) in batch.into_iter().enumerate() {
                    match cmd {
                        ToWorker::Throttle(v) => {
                            if Some(i) == last_throttle {
                                let r = source
                                    .write("vessels/active/ctl/throttle", &format!("{v:.4}"));
                                send_write(&tx, r, format!("throttle \u{2192} {}%", pct(v)));
                            }
                        }
                        ToWorker::Engine(on) => {
                            // One toggle file: 1 = ignite, 0 = shutdown (the /sim ctl/engine flag).
                            let r = source.write("vessels/active/ctl/engine", if on { "1" } else { "0" });
                            send_write(&tx, r, if on { "ignite" } else { "shutdown" }.to_string());
                        }
                    }
                }
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return,
        }
    });
}

fn pct(v: f64) -> i64 {
    (v.clamp(0.0, 1.0) * 100.0).round() as i64
}

fn send_write(tx: &Sender<FromWorker>, result: Result<(), source::CmdError>, ok_msg: String) {
    let msg = match result {
        Ok(()) => FromWorker::Write {
            message: ok_msg,
            is_error: false,
        },
        Err(e) => FromWorker::Write {
            message: format!("{}: {}", e.errno, e.message),
            is_error: true,
        },
    };
    let _ = tx.send(msg);
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
