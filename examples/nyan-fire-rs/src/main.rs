//! nyan-fire-rs — a ratatui **engine-plume colour console** over the gatOS `/sim` filesystem.
//!
//! Pick volumetric-exhaust templates (multi-select), build an ordered stripe palette — typed as
//! RGB/hex, fuzzy-picked from the bundled XKCD survey, or **captured** from a template's own authored
//! gradient — tune the timing, and hit **LIGHT THE RAINBOW!**. A worker thread then treats the palette
//! as a cyclic stripe sequence and shows a moving window over it across the plume's four emission
//! stops (`color0` at the nozzle exit … `color3` at the tip), so the whole rainbow sits along the
//! exhaust and scrolls nozzle-ward. Hit **CUT THE FIRE** (or quit) and every armed template's `reset`
//! restores its pristine, pre-gatOS values.
//!
//! Architecture: one worker thread hosts a tiny tokio runtime ([`source::spawn_worker`]) that owns the
//! [`source::Source`], drives the animation + read-back timers, and dispatches every plume write
//! **fire-and-forget** (it never waits on the write's result — the gatOS backend batches writes per
//! game tick, so a response is a whole frame away and this console doesn't care). A frame that changes
//! two or more leaves goes out as ONE `/sim/ctl/batch` group, so all four stops move in the same game
//! tick and the gradient never renders torn. The main thread runs the render + input loop and never
//! touches I/O. Every display knob lives in [`app::Settings`] and is editable live from the in-app
//! settings popup on the show screen.

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
            eprintln!("nyan-fire-rs: {e}");
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

/// Which backend serves the `/sim` plume fields.
enum SourceKind {
    Fs(String),
    Http(String),
}

struct Config {
    source: SourceKind,
    /// Seed values for the in-app [`Settings`] (all live-tunable from the show-screen settings popup).
    settings: Settings,
    /// The palette to start with — empty by default, or seeded from `--nyan` / a `--profile` file.
    colors: Vec<Rgb>,
    help: bool,
}

/// The classic nyan-cat rainbow trail: six flat stripes, hard-cut, scrolling nozzle-ward fast enough
/// to read as motion. Seeds BOTH the palette and the timing, at this argument's position (so a later
/// flag still wins) — the same composition rule as `--profile`.
///
/// Hard cut (`steps 1`) is deliberate: the game already blends between the four stops *spatially*
/// along the plume, so a temporal fade on top blends red→orange→yellow through each other into muddy
/// browns instead of six distinct bands (and it caps the writes at four per stripe segment).
/// `emission/brightness` is left alone — the authored per-template value is whatever the game ships,
/// and 200 (the ceiling) is a bloom blowout, not "full" — so the stripes burn at the template's own
/// brightness. Add a throb with `--bright-min 20 --bright-max 60 --bright-ms 150`.
fn nyan_preset(settings: &mut Settings, colors: &mut Vec<Rgb>) {
    *colors = [
        (0xff, 0x00, 0x00), // red      -> "1 0 0"
        (0xff, 0x99, 0x00), // orange   -> "1 0.6 0"
        (0xff, 0xff, 0x00), // yellow   -> "1 1 0"
        (0x33, 0xff, 0x00), // green    -> "0.2 1 0"
        (0x00, 0x99, 0xff), // blue     -> "0 0.6 1"
        (0x66, 0x33, 0xff), // violet   -> "0.4 0.2 1"
    ]
    .iter()
    .map(|&(r, g, b)| Rgb::from_u8(r, g, b))
    .collect();
    settings.color_ms = 220; // 6 stripes x 220 ms = 1.32 s per full scroll
    settings.steps = 1; // HARD CUT — crisp bands, no muddy in-between hues
    settings.slot_step = 1; // the 4-wide moving window
    settings.slot_stagger_ms = 0.0;
    settings.tpl_stagger_ms = 0.0;
    settings.bright_min = 0.0; // brightness untouched: the stripes ride the template's own
    settings.bright_max = 0.0; // authored emissive brightness
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
                // Applied AT ITS ARGUMENT POSITION, exactly like --profile: any timing flag after it
                // overrides that one knob, and a --profile after it replaces it wholesale.
                "--nyan" => nyan_preset(&mut settings, &mut colors),
                "--profile" => match args.next() {
                    Some(name) => {
                        // A profile restores every display knob + the palette (but not the templates —
                        // those are picked fresh each run). Applied over the seeds, so an explicit
                        // timing flag after --profile still wins.
                        let p = profile::load(&name).map_err(|e| format!("--profile: {e}"))?;
                        settings = p.settings;
                        colors = p.colors;
                    }
                    None => return Err("--profile wants a name (or path to a .yaml)".into()),
                },
                "--no-batch" => settings.batch = false,
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
                "--slot-stagger-ms" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=60_000.0).contains(&v) => settings.slot_stagger_ms = v,
                    _ => return Err("--slot-stagger-ms wants a number in 0..60000".into()),
                },
                "--slot-step" => match args.next().map(|s| s.parse::<u32>()) {
                    Some(Ok(v)) if v <= 8 => settings.slot_step = v,
                    _ => return Err("--slot-step wants a number in 0..8 (0 = all stops alike)".into()),
                },
                "--tpl-stagger-ms" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=60_000.0).contains(&v) => settings.tpl_stagger_ms = v,
                    _ => return Err("--tpl-stagger-ms wants a number in 0..60000".into()),
                },
                "--bright-min" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=200.0).contains(&v) => settings.bright_min = v,
                    _ => return Err("--bright-min wants a number in 0..200".into()),
                },
                "--bright-max" => match args.next().map(|s| s.parse::<f64>()) {
                    Some(Ok(v)) if (0.0..=200.0).contains(&v) => settings.bright_max = v,
                    _ => return Err("--bright-max wants a number in 0..200 (0 = off)".into()),
                },
                "--bright-ms" => match args.next().map(|s| s.parse::<u64>()) {
                    Some(Ok(v)) if (50..=60_000).contains(&v) => settings.bright_ms = v,
                    _ => return Err("--bright-ms wants a number in 50..60000".into()),
                },
                "--bright-steps" => match args.next().map(|s| s.parse::<u32>()) {
                    Some(Ok(v)) if v <= 1000 => settings.bright_steps = v,
                    _ => {
                        return Err("--bright-steps wants a number in 0..1000 (0 = continuous)".into())
                    }
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
    println!("nyan-fire-rs \u{2014} an engine-plume colour console over gatOS /sim");
    println!();
    println!("USAGE: nyan-fire-rs [--nyan] [--root <dir> | --url <base>] [--profile <name>] [timing seeds]");
    println!();
    println!("  --nyan         seed the classic nyan-cat rainbow preset (palette + snappy hard-cut");
    println!("                 timing). Applied AT ITS ARGUMENT POSITION, exactly like --profile: any");
    println!("                 timing flag AFTER it overrides that one knob; a --profile after it");
    println!("                 replaces it wholesale. It does NOT touch emission/brightness.");
    println!("  --root <dir>   read/write the /sim mount at <dir> (default: /sim when present)");
    println!("  --url <base>   use HTTP /v1 instead (e.g. $GATOS_HTTP, http://127.0.0.1:4242/v1)");
    println!("  --profile <name>  load a saved profile (palette + all settings, NOT the armed templates).");
    println!("                    A bare name resolves to <dir>/<name>.yaml; a path is used as-is.");
    println!("                    Save one from the show screen with  w . Profiles dir:");
    println!("                    $NYAN_PROFILE_DIR, else ~/.nyan-fire/profiles.");
    println!("  --no-batch     write each leaf separately instead of one atomic /sim/ctl/batch group");
    println!("                 per frame (the default batches, so all four stops move in one game tick).");
    println!();
    println!("Timing seeds (all live-tunable in the show-screen settings popup; press  s):");
    println!("  --hz <n>            animation frame rate, 1..240 (default 30)");
    println!("  --steps <n>         quantize each stripe fade to <n> values, 0..1000 (default 0 =");
    println!("                      continuous; 1 = hard cut, the crisp-stripe nyan look)");
    println!("  --color-ms <n>      time one palette entry occupies a gradient slot, ms, 50..60000");
    println!("                      (default 1200)");
    println!("  --slot-stagger-ms <n>  per-slot clock offset, 0..60000 (default 0 = lockstep)");
    println!("  --slot-step <n>        palette entries between adjacent gradient slots, 0..8 (default 1;");
    println!("                         0 = all four stops share one colour)");
    println!("  --tpl-stagger-ms <n>   per-template clock offset, 0..60000 (default 0 = lockstep)");
    println!("  --bright-min <f>       emission/brightness pulse floor, 0..200 (default 0)");
    println!("  --bright-max <f>       emission/brightness pulse ceiling, 0..200 (default 0 = OFF: the");
    println!("                         leaf is never written and each template keeps its authored value)");
    println!("  --bright-ms <n>        time between brightness targets, ms, 50..60000 (default 600)");
    println!("  --bright-steps <n>     quantize the brightness drift to <n> values, 0..1000 (0 = cont.)");
    println!();
    println!("Needs  [control] debug_namespace = true  in gatos.toml \u{2014} when it is off the whole");
    println!("/sim/debug subtree is ABSENT, so writes fail ENOENT (404), not EACCES.");
    println!("A template is SHARED: editing it repaints every nozzle in the universe that uses it.");
    println!("Templates screen: \u{2191}\u{2193} move \u{b7} space arm \u{b7} a all \u{b7} c capture \u{b7} r rescan \u{b7} Enter \u{2192} show.");
    println!("Show screen:      a RGB/hex \u{b7} f XKCD \u{b7} [ ] reorder \u{b7} d remove \u{b7} s settings \u{b7} g reset");
    println!("                  Enter/P burn \u{b7} w save profile \u{b7} h hide (status-bar-only overlay).");
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

    // A short tick keeps the live gradient band animating smoothly between input events (the worker
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

    // If we asked the worker to reset the templates on the way out, give it a moment to confirm so we
    // don't leave someone's plumes purple.
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nyan_preset_is_six_hard_cut_stripes_that_leave_brightness_alone() {
        let mut s = Settings::default();
        let mut colors = Vec::new();
        nyan_preset(&mut s, &mut colors);
        assert_eq!(colors.len(), 6);
        // The six stripes land on pleasingly round /sim wire forms.
        assert_eq!(colors[0].to_sim(), "1 0 0");
        assert_eq!(colors[1].to_sim(), "1 0.6 0");
        assert_eq!(colors[3].to_sim(), "0.2 1 0");
        assert_eq!(s.color_ms, 220);
        assert_eq!(s.steps, 1); // hard cut
        assert_eq!(s.slot_step, 1); // the 4-wide moving window
        // emission/brightness stays untouched (max <= 0 = the leaf is never written).
        assert!(s.bright_is_off());
    }
}
