//! dancy-party-rs — a ratatui **party-lights console** over the gatOS `/sim` filesystem.
//!
//! Pick vessels (multi-select), build an ordered color palette — typed as RGB/hex or fuzzy-picked
//! from the bundled XKCD survey — tune the timing, and hit **LETS PARTY!**. A worker thread then
//! cross-fades the palette and pulses each light's deploy `goal` on **two independent clocks**,
//! broadcasting each frame to every `lights/<n>/color` + `lights/<n>/goal` file on the selected
//! vessels. Hit **STOP, MY EYES** (or quit) and every light snaps back to white.
//!
//! Architecture: one worker thread hosts a tiny tokio runtime ([`source::spawn_worker`]) that owns the
//! [`source::Source`], drives the animation + battery timers, and dispatches every light write
//! **fire-and-forget** (it never waits on the write's result — the gatOS backend batches writes per
//! game tick, so a response is a whole frame away and this console doesn't care). The main thread runs
//! the render + input loop and never touches I/O. The light tree is discovered **once** (re-walking a
//! 9p tree is costly) and cached on the worker. Every display knob lives in [`app::Settings`] and is
//! editable live from the in-app settings popup on the party screen.

mod app;
mod color;
mod party;
mod profile;
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

use app::{App, Settings};
use color::Rgb;
use source::{spawn_worker, FromWorker, FsSource, HttpSource, Source, ToWorker};

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
    /// Seed values for the in-app [`Settings`] (all live-tunable from the party-screen settings popup).
    settings: Settings,
    /// The palette to start with — empty by default, or seeded from a `--profile` file.
    colors: Vec<Rgb>,
    help: bool,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut url: Option<String> = None;
        let mut root: Option<String> = None;
        let mut settings = Settings::default();
        let mut colors: Vec<Rgb> = Vec::new();
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--url" => url = args.next(),
                "--root" => root = args.next(),
                "--profile" => match args.next() {
                    Some(name) => {
                        // A profile restores every display knob + the palette (but not the vessels —
                        // those are picked fresh each run). Applied over the seeds, so an explicit
                        // timing flag after --profile still wins.
                        let p = profile::load(&name).map_err(|e| format!("--profile: {e}"))?;
                        settings = p.settings;
                        colors = p.colors;
                    }
                    None => return Err("--profile wants a name (or path to a .yaml)".into()),
                },
                "--hz" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (1.0..=240.0).contains(&v) => settings.hz = v,
                    _ => return Err("--hz wants a number in 1..240".into()),
                },
                "--steps" => match args.next().map(|s| s.parse::<u32>()) {
                    Some(Ok(v)) if v <= 1000 => settings.steps = v,
                    _ => return Err("--steps wants a number in 0..1000 (0 = continuous)".into()),
                },
                "--color-ms" => match args.next().map(|s| s.parse::<u64>()) {
                    Some(Ok(v)) if (50..=60_000).contains(&v) => settings.color_ms = v,
                    _ => return Err("--color-ms wants a number in 50..60000".into()),
                },
                "--anim-ms" => match args.next().map(|s| s.parse::<u64>()) {
                    Some(Ok(v)) if (50..=60_000).contains(&v) => settings.anim_ms = v,
                    _ => return Err("--anim-ms wants a number in 50..60000".into()),
                },
                "--color-stagger-ms" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=60_000.0).contains(&v) => settings.color_stagger_ms = v,
                    _ => return Err("--color-stagger-ms wants a number in 0..60000".into()),
                },
                "--anim-stagger-ms" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=60_000.0).contains(&v) => settings.anim_stagger_ms = v,
                    _ => return Err("--anim-stagger-ms wants a number in 0..60000".into()),
                },
                "--bright-min" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=10_000.0).contains(&v) => settings.bright_min = v,
                    _ => return Err("--bright-min wants a number in 0..10000".into()),
                },
                "--bright-max" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=10_000.0).contains(&v) => settings.bright_max = v,
                    _ => return Err("--bright-max wants a number in 0..10000".into()),
                },
                "--bright-ms" => match args.next().map(|s| s.parse::<u64>()) {
                    Some(Ok(v)) if (50..=60_000).contains(&v) => settings.bright_ms = v,
                    _ => return Err("--bright-ms wants a number in 50..60000".into()),
                },
                "--bright-steps" => match args.next().map(|s| s.parse::<u32>()) {
                    Some(Ok(v)) if v <= 1000 => settings.bright_steps = v,
                    _ => return Err("--bright-steps wants a number in 0..1000 (0 = continuous)".into()),
                },
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        Ok(Self {
            source: resolve_source(url, root),
            settings,
            colors,
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
    println!("USAGE: dancy-party-rs [--root <dir> | --url <base>] [--profile <name>] [timing seeds]");
    println!();
    println!("  --root <dir>   read/write the /sim mount at <dir> (default: /sim when present)");
    println!("  --url <base>   use HTTP /v1/fs instead (e.g. $GATOS_HTTP, http://127.0.0.1:4242/v1)");
    println!("  --profile <name>  load a saved profile (palette + all timing/settings, NOT vessels).");
    println!("                    A bare name resolves to <dir>/<name>.yaml; a path is used as-is.");
    println!("                    Save one from the party screen with  w . Profiles dir:");
    println!("                    $DANCY_PROFILE_DIR, else ~/.dancy-party/profiles.");
    println!();
    println!("Timing seeds (all live-tunable in the party-screen settings popup; press  s):");
    println!("  --hz <n>            animation frame rate, 1..240 (default 30)");
    println!("  --steps <n>         quantize each color fade to <n> discrete values, 0..1000 (default 0 =");
    println!("                      continuous). Fewer steps = fewer distinct 9p writes; 1 = hard cut.");
    println!("  --color-ms <n>      per-color cross-fade duration, ms (default 1200)");
    println!("  --anim-ms <n>       deploy goal-pulse half-period, ms (default 2500) \u{2014} the color and the");
    println!("                      deploy animation run on INDEPENDENT clocks, so keep this \u{2265} the ~2 s");
    println!("                      in-game stroke and each extend/retract actually completes.");
    println!("  --color-stagger-ms <n>  per-light color offset, 0..60000 (default 0 = lockstep)");
    println!("  --anim-stagger-ms <n>   per-light deploy offset, 0..60000 (default 0 = lockstep)");
    println!("  --bright-min <n>        random-brightness floor, 0..10000 (default 10000 = off)");
    println!("  --bright-max <n>        random-brightness ceiling, 0..10000 (default 10000). min<max enables it.");
    println!("  --bright-ms <n>         time between random brightness targets, ms, 50..60000 (default 600)");
    println!("  --bright-steps <n>      quantize the brightness drift to <n> values, 0..1000 (0 = continuous)");
    println!();
    println!("In the guest, no flags are needed: it reads /sim and drives the selected vessels' lights.");
    println!("Vessels screen: \u{2191}\u{2193} move \u{b7} space arm \u{b7} a all \u{b7} r rescan \u{b7} Enter \u{2192} party.");
    println!("Party screen:   a RGB/hex \u{b7} f XKCD \u{b7} [ ] reorder \u{b7} d remove \u{b7} s settings \u{b7} g refill \u{b7} Enter/P party.");
    println!("                w save profile \u{b7} h hide (status-bar-only overlay for in-game use).");
}

fn run(terminal: &mut Tui, config: Config) -> io::Result<()> {
    // Commands to the worker go over an unbounded tokio channel (sending is sync, so the render thread
    // never blocks); the worker's replies come back over a std channel the UI polls.
    let (cmd_tx, cmd_rx) = tokio::sync::mpsc::unbounded_channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    spawn_worker(source, config.settings.hz, cmd_rx, update_tx);

    let mut app = App::new(cmd_tx, label, config.settings, config.colors);

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
