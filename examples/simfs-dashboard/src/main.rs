//! simfs-dashboard — a DIY ratatui dashboard *builder* over the gatOS `/sim` filesystem.
//!
//! Unlike the sibling `dashboard-rs` (a fixed fleet view over the atomic HTTP snapshot), this one
//! is empty at start: you search the `/sim` field surface in a popup and place fields as widgets.
//! First-party knowledge of what each path means (see `catalog.rs`) turns a control file into a
//! live control — `…/ctl/throttle` becomes a clickable throttle bar, `…/lights/0/on` a toggle.
//!
//! Architecture: one worker thread owns the [`source::Source`] and re-reads only the placed fields
//! once per `--interval` (so polling cost is O(widgets)); the main thread runs the render + input
//! loop. Layouts serialize to TOML and load with `--file`.

mod app;
mod catalog;
mod source;
mod ui;
mod widget;

use std::collections::HashMap;
use std::io::{self, Stdout};
use std::path::Path;
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

use app::{App, Layout};
use source::{FromWorker, FsSource, HttpSource, Source, ToWorker};
use widget::Widget;

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> Result<()> {
    let config = match Config::from_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("simfs-dashboard: {e}");
            std::process::exit(2);
        }
    };
    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(&mut terminal, config);
    restore_terminal(&mut terminal)?;
    result
}

/// Which backend serves the `/sim` fields.
enum SourceKind {
    Fs(String),
    Http(String),
}

struct Config {
    source: SourceKind,
    interval: Duration,
    columns: u16,
    border_opacity: u8,
    widgets: Vec<Widget>,
    file: Option<String>,
}

impl Config {
    fn from_args() -> Result<Self> {
        let mut url: Option<String> = None;
        let mut root: Option<String> = None;
        let mut interval_ms: Option<u64> = None;
        let mut columns: Option<u16> = None;
        let mut opacity: Option<u8> = None;
        let mut file: Option<String> = None;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--url" => url = args.next(),
                "--root" => root = args.next(),
                "--file" => file = args.next(),
                "--interval" => interval_ms = args.next().and_then(|s| s.parse().ok()),
                "--columns" => columns = args.next().and_then(|s| s.parse().ok()),
                "--border-opacity" => opacity = args.next().and_then(|s| s.parse().ok()),
                "-h" | "--help" => {
                    print_help();
                    std::process::exit(0);
                }
                other => return Err(anyhow::anyhow!("unknown argument '{other}' (try --help)")),
            }
        }

        // A loaded layout supplies defaults for everything the CLI did not explicitly override.
        let (mut widgets, def_cols, def_opacity, def_interval) = match &file {
            Some(path) => {
                let layout = Layout::load(path)
                    .map_err(|e| anyhow::anyhow!("failed to load layout '{path}': {e}"))?;
                let cols = layout.columns;
                let op = layout.border_opacity;
                let iv = layout.interval_ms;
                (layout.to_widgets(), cols, op, iv)
            }
            None => (Vec::new(), 3, 100, None),
        };
        widgets.truncate(512); // a sane upper bound on a hand-edited file

        let interval = Duration::from_millis(interval_ms.or(def_interval).unwrap_or(250).max(1));
        let columns = columns.unwrap_or(def_cols).clamp(1, 8);
        let border_opacity = opacity.unwrap_or(def_opacity).min(100);
        let source = resolve_source(url, root);

        Ok(Self {
            source,
            interval,
            columns,
            border_opacity,
            widgets,
            file,
        })
    }
}

/// Picks the backend: explicit `--url`/`--root` win; otherwise default to the real `/sim` mount
/// when present (the in-guest case), else `$GATOS_HTTP` (the host-dev case), else `/sim` anyway.
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
    println!("simfs-dashboard \u{2014} DIY dashboard builder over the gatOS /sim filesystem");
    println!();
    println!("USAGE: simfs-dashboard [--file <layout.toml>] [--root <dir> | --url <base>]");
    println!("                       [--interval <ms>] [--columns <n>] [--border-opacity <0-100>]");
    println!();
    println!("  --file <layout.toml>      load a saved dashboard at startup");
    println!(
        "  --root <dir>              read the /sim mount at <dir> (default: /sim when present)"
    );
    println!("  --url <base>              read via HTTP /v1/fs instead (e.g. $GATOS_HTTP)");
    println!("  --interval <ms>           field re-read cadence, min 1 (default 250)");
    println!("  --columns <n>             dashboard grid columns, 1-8 (default 3)");
    println!("  --border-opacity <0-100>  card-border brightness over the game (default 100)");
    println!();
    println!(
        "In the guest, no flags are needed: it reads /sim directly. Press 'a' to add a field."
    );
}

fn run(terminal: &mut Tui, config: Config) -> Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    spawn_worker(source, config.interval, cmd_rx, update_tx);

    let mut app = App::new(
        cmd_tx,
        label,
        config.widgets,
        config.columns,
        config.border_opacity,
        config.interval,
        config.file,
    );

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
    // Dropping `app` drops `cmd_tx`; the worker's recv_timeout then returns Disconnected and exits.
}

/// One thread owns the source: each loop re-reads the subscribed fields (the placed widgets), pushes
/// the values, then waits up to one interval for a command — a write (control actuation) or a
/// catalog refresh (re-list ids + walk leaves for the search popup). Mirrors `dashboard-rs`'s worker.
fn spawn_worker(
    source: Box<dyn Source>,
    interval: Duration,
    rx: Receiver<ToWorker>,
    tx: Sender<FromWorker>,
) {
    thread::spawn(move || {
        let mut subs: Vec<String> = Vec::new();
        loop {
            let mut values = HashMap::new();
            let mut any_ok = false;
            for path in &subs {
                let result = source.read(path);
                any_ok |= result.is_ok();
                values.insert(path.clone(), result);
            }
            let connected = if subs.is_empty() {
                source.health().connected
            } else {
                any_ok
            };
            if tx.send(FromWorker::Values { values, connected }).is_err() {
                return;
            }

            match rx.recv_timeout(interval) {
                Ok(ToWorker::Subscribe(s)) => subs = s,
                Ok(ToWorker::Write { path, value, note }) => {
                    let result = source.write(&path, &value);
                    let _ = tx.send(FromWorker::WriteDone { note, result });
                }
                Ok(ToWorker::Refresh) => {
                    let vessels = source.vessel_ids();
                    let bodies = source.body_ids();
                    let leaves = source.walk_leaves();
                    let health = source.health();
                    let candidates = catalog::candidates(
                        &vessels,
                        &bodies,
                        &leaves,
                        health.control,
                        health.debug,
                    );
                    let _ = tx.send(FromWorker::Catalog { candidates, health });
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
