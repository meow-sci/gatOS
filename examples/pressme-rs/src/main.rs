//! pressme-rs — a ratatui panel of full-width, hex-colored buttons, each of which runs a shell
//! command when pressed.
//!
//! Give it a set of (label, color, command) triplets — from a TOML file (`pressme.toml`) or from
//! repeatable `--button LABEL:COLOR:COMMAND` flags — and it stacks one full-width button per triplet,
//! splitting the terminal height evenly across them. Select with the arrow keys (or hover the mouse),
//! press with Enter / space / click / the button's number, and the command runs off the UI thread;
//! its result lands as a one-line subtitle on the button.
//!
//! It talks to no gatOS API itself — it just runs shells — but inside the guest those commands are
//! ordinary Alpine userland, so pointing them at the `/sim` mount turns it into a one-key control
//! board (see the shipped `pressme.toml`).
//!
//! Architecture (lighter than the sibling examples — no polling worker): the main thread runs the
//! render + input loop, and each press spawns a short-lived thread that runs the command and sends
//! its outcome back over a channel the loop drains.

mod app;
mod button;
mod config;
mod runner;
mod ui;

use std::io::{self, Stdout};
use std::path::{Path, PathBuf};
use std::sync::mpsc;
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

use app::App;
use runner::RunMsg;

type Tui = Terminal<CrosstermBackend<Stdout>>;

const DEFAULT_CONFIG: &str = "pressme.toml";

fn main() -> io::Result<()> {
    let args = match Args::from_env() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("pressme-rs: {e}");
            std::process::exit(2);
        }
    };
    if args.help {
        print_help();
        return Ok(());
    }
    let buttons = match resolve_buttons(&args) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("pressme-rs: {e}");
            eprintln!("(pass --button LABEL:COLOR:COMMAND, or a --config TOML — see pressme.toml)");
            std::process::exit(2);
        }
    };

    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(&mut terminal, buttons);
    restore_terminal(&mut terminal)?;
    result
}

struct Args {
    config: Option<PathBuf>,
    cli_buttons: Vec<config::Button>,
    help: bool,
}

impl Args {
    fn from_env() -> Result<Self, String> {
        let mut config: Option<PathBuf> = None;
        let mut cli_buttons: Vec<config::Button> = Vec::new();
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--config" | "-c" => {
                    config = Some(PathBuf::from(
                        args.next().ok_or("--config needs a file path")?,
                    ))
                }
                "--button" | "-b" => {
                    let spec = args.next().ok_or("--button needs LABEL:COLOR:COMMAND")?;
                    cli_buttons.push(config::parse_cli_button(&spec)?);
                }
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        Ok(Self {
            config,
            cli_buttons,
            help,
        })
    }
}

/// Gathers the button set: the TOML file (an explicit `--config`, or the default `pressme.toml` when
/// it exists) first, then any `--button` flags appended in order. Empty is an error.
fn resolve_buttons(args: &Args) -> Result<Vec<config::Button>, String> {
    let mut buttons = Vec::new();
    let explicit = args.config.is_some();
    let path = args.config.clone().unwrap_or_else(|| PathBuf::from(DEFAULT_CONFIG));
    if explicit || Path::new(&path).exists() {
        buttons.extend(config::load_file(&path)?);
    }
    buttons.extend(args.cli_buttons.iter().cloned());
    if buttons.is_empty() {
        return Err(format!(
            "no buttons (no {DEFAULT_CONFIG} found and no --button given)"
        ));
    }
    Ok(buttons)
}

fn print_help() {
    println!("pressme-rs \u{2014} a ratatui panel of buttons that run shell commands");
    println!();
    println!("USAGE: pressme-rs [--config <file>] [--button LABEL:COLOR:COMMAND]...");
    println!();
    println!("  -c, --config <file>   TOML button file (default: ./pressme.toml when present)");
    println!("  -b, --button <spec>   add one button; repeatable. LABEL:COLOR:COMMAND — only the");
    println!("                        first two ':' split, so COMMAND may contain colons. COLOR is");
    println!("                        hex (#rrggbb, #rgb, or bare). e.g. 'Deploy:#2ea043:make ship'");
    println!("  -h, --help            show this help");
    println!();
    println!("Buttons stack full-width; the terminal height splits evenly across them.");
    println!("Keys: \u{2191}\u{2193}/jk select \u{b7} \u{23ce}/space/click/1-9 press \u{b7} q quit.");
    println!("Commands run off the UI thread; the first output line (or exit code) shows on the button.");
}

fn run(terminal: &mut Tui, buttons: Vec<config::Button>) -> io::Result<()> {
    let (tx, rx) = mpsc::channel::<RunMsg>();
    let mut app = App::new(buttons, tx);

    // A steady tick so a running button's spinner animates even with no input.
    let tick = Duration::from_millis(90);
    while !app.should_quit {
        while let Ok(msg) = rx.try_recv() {
            app.apply(msg);
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
