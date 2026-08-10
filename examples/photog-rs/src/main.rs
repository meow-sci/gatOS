//! `photog-rs` — cinematic camera project editor for gatOS.

mod app;
mod ui;
mod worker;

use std::io::{self, Stdout};
use std::path::{Path, PathBuf};
use std::sync::mpsc;
use std::time::Duration;

use photog::{load_project, FsTransport, HttpTransport, Project, Transport};
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

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let config = match Config::from_args() {
        Ok(config) => config,
        Err(error) => {
            eprintln!("photog-rs: {error}");
            std::process::exit(2);
        }
    };
    if config.help {
        print_help();
        return Ok(());
    }
    let (project, project_path) = match &config.project_path {
        Some(path) => (load_project(path)?, Some(path.clone())),
        None => (Project::new("untitled-take"), None),
    };
    let transport = build_transport(config.source);
    let (request_tx, request_rx) = mpsc::channel();
    let (update_tx, update_rx) = mpsc::channel();
    let worker = worker::spawn_worker(transport, config.interval, request_rx, update_tx);

    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(
        &mut terminal,
        App::new(project, project_path, request_tx),
        update_rx,
    );
    restore_terminal(&mut terminal)?;
    worker.join().map_err(|_| "photog I/O worker panicked")?;
    result?;
    Ok(())
}

enum SourceKind {
    Fs(PathBuf),
    Http(String),
}

struct Config {
    project_path: Option<PathBuf>,
    source: SourceKind,
    interval: Duration,
    help: bool,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut project_path = None;
        let mut root = None;
        let mut url = None;
        let mut interval = Duration::from_millis(150);
        let mut help = false;
        let mut args = std::env::args().skip(1);
        while let Some(argument) = args.next() {
            match argument.as_str() {
                "--root" => {
                    root = Some(PathBuf::from(
                        args.next().ok_or("--root needs a directory")?,
                    ));
                }
                "--url" => url = Some(args.next().ok_or("--url needs a /v1 base URL")?),
                "--interval" => {
                    let ms = args
                        .next()
                        .ok_or("--interval needs milliseconds")?
                        .parse::<u64>()
                        .map_err(|_| "--interval must be an integer")?;
                    interval = Duration::from_millis(ms.max(10));
                }
                "-h" | "--help" => help = true,
                value if value.starts_with('-') => {
                    return Err(format!("unknown option '{value}' (try --help)"));
                }
                value if project_path.is_none() => project_path = Some(PathBuf::from(value)),
                value => return Err(format!("unexpected project path '{value}'")),
            }
        }
        if root.is_some() && url.is_some() {
            return Err("--root and --url are mutually exclusive".into());
        }
        let source = if let Some(root) = root {
            SourceKind::Fs(root)
        } else if let Some(url) = url {
            SourceKind::Http(url)
        } else if Path::new("/sim").is_dir() {
            SourceKind::Fs(PathBuf::from("/sim"))
        } else if let Ok(url) = std::env::var("GATOS_HTTP") {
            SourceKind::Http(url)
        } else {
            SourceKind::Fs(PathBuf::from("/sim"))
        };
        Ok(Self {
            project_path,
            source,
            interval,
            help,
        })
    }
}

fn build_transport(source: SourceKind) -> Box<dyn Transport> {
    match source {
        SourceKind::Fs(root) => Box::new(FsTransport::new(root)),
        SourceKind::Http(url) => Box::new(HttpTransport::new(url)),
    }
}

fn print_help() {
    println!("photog-rs — cinematic camera project editor for gatOS");
    println!();
    println!("USAGE: photog-rs [project.json] [--root <dir> | --url <v1-base>] [--interval <ms>]");
    println!();
    println!("  project.json    open a saved v1 photog project (otherwise starts unsaved)");
    println!("  --root <dir>    direct /sim or filesystem fixture root");
    println!("  --url <base>    HTTP base ending in /v1 (e.g. $GATOS_HTTP)");
    println!("  --interval <ms> live status polling interval (default 150)");
    println!();
    println!(
        "The default is /sim when mounted, then $GATOS_HTTP, then /sim for a clear offline state."
    );
    println!(
        "Press ? in the editor for all controls. Emergency recovery: echo 1 > /sim/camera/release"
    );
}

fn run(
    terminal: &mut Tui,
    mut app: App,
    updates: mpsc::Receiver<worker::Update>,
) -> io::Result<()> {
    let tick = Duration::from_millis(80);
    while !app.should_quit {
        while let Ok(update) = updates.try_recv() {
            app.apply_update(update);
        }
        terminal.draw(|frame| ui::render(frame, &mut app))?;
        if event::poll(tick)? {
            match event::read()? {
                Event::Key(key) if key.kind == KeyEventKind::Press => app.on_key(key),
                Event::Mouse(mouse) => app.on_mouse(mouse),
                _ => {}
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

fn install_panic_hook() {
    let original = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let _ = disable_raw_mode();
        let _ = execute!(io::stdout(), LeaveAlternateScreen, DisableMouseCapture);
        original(info);
    }));
}
