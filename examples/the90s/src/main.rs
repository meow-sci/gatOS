//! the90s — a 90's-flash-app-style soundboard over the gatOS `/sim/audio` userland playback
//! surface (SPEC §3.9), built with ratatui.
//!
//! It loads a small YAML config (a title + `name: path` sound map), registers each sound file into
//! the `/sim/audio/file/` clip store at startup (skipping clips already there with the same size),
//! and then plays them through the game's speakers. Two screens — a keyboard-nav **list** and a
//! full-screen masonry **soundboard** — share a left-edge volume slider and the red **OMG STOP**
//! button. Every press layers a new channel (play a clip as many times as you like, all at once);
//! presses that pile up within one worker interval are dispatched as a single `ctl/batch` group so
//! they start in the **same game tick** (SPEC §3.10). Stopping a sound stops **all** of its live
//! channels (`audio/stop <clip>` fans out by name).
//!
//! Architecture (mirrors the sibling examples): one worker thread owns the [`source::Source`] —
//! registering the sounds, polling `audio/status` once per `--interval`, and applying
//! play/stop/volume commands between polls — while the main thread runs the render + input loop,
//! so the UI never blocks on I/O.

mod app;
mod config;
mod source;
mod ui;

use std::io::{self, Stdout};
use std::path::{Path, PathBuf};
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

use app::{App, Screen};
use source::{
    batch_play_value, play_value, set_value, status_counts, CmdError, FromWorker, FsSource,
    HttpSource, Poll, RegOutcome, Source, ToWorker,
};

type Tui = Terminal<CrosstermBackend<Stdout>>;

/// The ctl/batch command cap (SPEC §3.10) — a run of plays longer than this splits into groups.
const BATCH_CAP: usize = 64;

fn main() -> io::Result<()> {
    let cfg = match Args::from_env() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("the90s: {e}");
            std::process::exit(2);
        }
    };
    if cfg.help {
        print_help();
        return Ok(());
    }
    let board = match config::load(&cfg.config) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("the90s: config {e}");
            eprintln!("(see the example the90s.yaml next to this program's source)");
            std::process::exit(2);
        }
    };

    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(&mut terminal, cfg, board);
    restore_terminal(&mut terminal)?;
    result
}

/// Which backend serves the `/sim` audio surface.
enum SourceKind {
    Fs(String),
    Http(String),
}

struct Args {
    config: PathBuf,
    source: SourceKind,
    interval: Duration,
    screen: Screen,
    help: bool,
}

impl Args {
    fn from_env() -> Result<Self, String> {
        let mut config: Option<String> = None;
        let mut url: Option<String> = None;
        let mut root: Option<String> = None;
        let mut interval_ms: Option<u64> = None;
        let mut screen = Screen::List;
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--config" | "-c" => config = args.next(),
                "--url" => url = args.next(),
                "--root" => root = args.next(),
                "--interval" => interval_ms = args.next().and_then(|s| s.parse().ok()),
                "--screen" => {
                    screen = match args.next().as_deref() {
                        Some("list") | None => Screen::List,
                        Some("board") => Screen::Board,
                        Some(other) => return Err(format!("bad --screen '{other}' (list|board)")),
                    }
                }
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        Ok(Self {
            config: PathBuf::from(config.unwrap_or_else(|| "the90s.yaml".to_string())),
            source: resolve_source(url, root),
            interval: Duration::from_millis(interval_ms.unwrap_or(150).max(1)),
            screen,
            help,
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
    println!("the90s \u{2014} a 90's-flash-app soundboard over the gatOS /sim/audio surface");
    println!();
    println!("USAGE: the90s [--config <file>] [--root <dir> | --url <base>]");
    println!("              [--interval <ms>] [--screen list|board]");
    println!();
    println!("  -c, --config <file>   the soundboard YAML (default: ./the90s.yaml)");
    println!("  --root <dir>          use the /sim mount at <dir> (default: /sim when present)");
    println!("  --url <base>          use the HTTP /v1 mirror instead (e.g. $GATOS_HTTP)");
    println!("  --interval <ms>       audio/status poll cadence, min 1 (default 150)");
    println!("  --screen list|board   which screen to open on (default list)");
    println!();
    println!("In the guest, no flags are needed: it reads ./the90s.yaml and drives /sim/audio.");
    println!("Keys: \u{2191}\u{2193} pick \u{b7} \u{23ce}/space play (again = layer) \u{b7} s stop that sound \u{b7} o OMG STOP");
    println!("      tab flip list/board \u{b7} -/= volume \u{b7} q quit. Mouse: click to play, right-click");
    println!("      to stop, drag the left slider for volume.");
}

fn run(terminal: &mut Tui, args: Args, board: config::Config) -> io::Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(args.source);
    let label = source.label();
    let initial_volume = 1.0;
    let worker_sounds: Vec<WorkerSound> = board
        .sounds
        .iter()
        .map(|s| WorkerSound {
            clip: s.clip.clone(),
            path: s.path.clone(),
        })
        .collect();
    spawn_worker(source, worker_sounds, initial_volume, args.interval, cmd_rx, update_tx);

    let ui_sounds: Vec<(String, String)> = board
        .sounds
        .into_iter()
        .map(|s| (s.name, s.clip))
        .collect();
    let mut app = App::new(cmd_tx, board.title, label, ui_sounds, args.screen, initial_volume);

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

/// What the worker needs to register one sound: its store clip name + the local file to read.
struct WorkerSound {
    clip: String,
    path: PathBuf,
}

/// One thread owns the source. It first registers every sound into the clip store (skipping clips
/// already there with the same byte count), then loops: poll `audio/status`, push it, wait up to
/// one interval for commands. A burst of queued commands is drained together — consecutive plays
/// become one same-tick `ctl/batch` group, and a run of volume drags is coalesced to the last
/// value.
fn spawn_worker(
    source: Box<dyn Source>,
    sounds: Vec<WorkerSound>,
    initial_volume: f64,
    interval: Duration,
    rx: Receiver<ToWorker>,
    tx: Sender<FromWorker>,
) {
    thread::spawn(move || {
        // An immediate connectivity poll so the UI leaves "connecting…" fast, then registration.
        let _ = poll_and_send(&*source, &sounds, &tx);
        for (idx, s) in sounds.iter().enumerate() {
            let result = register(&*source, s);
            if tx.send(FromWorker::Registered { idx, result }).is_err() {
                return;
            }
        }

        let mut volume = initial_volume;
        loop {
            let counts = poll_and_send(&*source, &sounds, &tx);

            let first = match rx.recv_timeout(interval) {
                Ok(cmd) => cmd,
                Err(RecvTimeoutError::Timeout) => continue,
                Err(RecvTimeoutError::Disconnected) => return,
            };
            let mut batch = vec![first];
            while let Ok(more) = rx.try_recv() {
                batch.push(more);
            }

            // Only the final volume in the burst matters; plays/stops all fire, in order.
            let last_vol = batch
                .iter()
                .rposition(|c| matches!(c, ToWorker::SetVolume(_)));

            let mut i = 0;
            while i < batch.len() {
                match &batch[i] {
                    ToWorker::Play { .. } => {
                        // Gather the consecutive run of plays into one same-tick group.
                        let mut clips = Vec::new();
                        let mut labels = Vec::new();
                        while let Some(ToWorker::Play { clip, label }) = batch.get(i) {
                            clips.push(clip.clone());
                            labels.push(label.clone());
                            i += 1;
                        }
                        dispatch_plays(&*source, &clips, &labels, volume, &tx);
                    }
                    ToWorker::Stop { clip, label } => {
                        dispatch_stop(&*source, clip, label, &tx);
                        i += 1;
                    }
                    ToWorker::StopAll => {
                        let r = source.write("audio/stop", "all");
                        send_write(&tx, r, "\u{25a0} stopped everything".to_string());
                        i += 1;
                    }
                    ToWorker::SetVolume(v) => {
                        if Some(i) == last_vol {
                            volume = *v;
                            dispatch_volume(&*source, &sounds, counts.as_deref(), volume, &tx);
                        }
                        i += 1;
                    }
                }
            }
        }
    });
}

/// Registers one sound: if the store already holds a clip of the same name **and size**, it's
/// reused; otherwise the local file's bytes are uploaded (playable once the upload returns).
fn register(source: &dyn Source, sound: &WorkerSound) -> Result<RegOutcome, String> {
    let meta = std::fs::metadata(&sound.path)
        .map_err(|e| format!("{}: {e}", sound.path.display()))?;
    if !meta.is_file() {
        return Err(format!("{}: not a file", sound.path.display()));
    }
    if source.clip_size(&sound.clip) == Some(meta.len()) {
        return Ok(RegOutcome::Cached);
    }
    let bytes =
        std::fs::read(&sound.path).map_err(|e| format!("{}: {e}", sound.path.display()))?;
    source
        .upload(&sound.clip, &bytes)
        .map_err(|e| format!("upload failed \u{2014} {}: {}", e.errno, e.message))?;
    Ok(RegOutcome::Uploaded)
}

/// Polls `audio/status`, sends the [`Poll`] to the UI, and returns the per-sound counts (None when
/// the audio surface is unreachable) for the worker's volume fan-out.
fn poll_and_send(
    source: &dyn Source,
    sounds: &[WorkerSound],
    tx: &Sender<FromWorker>,
) -> Option<Vec<u32>> {
    let (poll, counts) = match source.read("audio/status") {
        Ok(text) => {
            let (by_clip, total) = status_counts(&text);
            let counts: Vec<u32> = sounds
                .iter()
                .map(|s| by_clip.get(&s.clip).copied().unwrap_or(0))
                .collect();
            (
                Poll {
                    connected: true,
                    audio_ok: true,
                    counts: counts.clone(),
                    total,
                },
                Some(counts),
            )
        }
        Err(_) => (
            Poll {
                // Distinguish "audio disabled" from "gatOS is gone" with a cheap time probe.
                connected: source.read("time/ut").is_ok(),
                audio_ok: false,
                counts: vec![0; sounds.len()],
                total: 0,
            },
            None,
        ),
    };
    let _ = tx.send(FromWorker::Poll(poll));
    counts
}

/// Starts the queued plays: one clip is a plain `audio/play` write; two or more become `ctl/batch`
/// groups (≤ 64 commands each) so every layer starts in the same game tick.
fn dispatch_plays(
    source: &dyn Source,
    clips: &[String],
    labels: &[String],
    volume: f64,
    tx: &Sender<FromWorker>,
) {
    if clips.len() == 1 {
        let r = source.write("audio/play", &play_value(&clips[0], volume));
        send_write(tx, r, format!("\u{25b6} {}", labels[0]));
        return;
    }
    for group in clips.chunks(BATCH_CAP) {
        let r = source.write("ctl/batch", &batch_play_value(group, volume));
        send_write(
            tx,
            r,
            format!("\u{25b6} {} sounds \u{b7} same tick", group.len()),
        );
    }
}

/// Stops every live channel of one clip. A no-match `ENOENT` is normal (the clip already finished)
/// — reported as a calm note, not an error.
fn dispatch_stop(source: &dyn Source, clip: &str, label: &str, tx: &Sender<FromWorker>) {
    match source.write("audio/stop", clip) {
        Ok(()) => send_write(tx, Ok(()), format!("\u{25a0} {label} stopped")),
        Err(e) if e.errno == "ENOENT" => {
            let _ = tx.send(FromWorker::Write {
                message: format!("{label}: nothing playing"),
                is_error: false,
            });
        }
        Err(e) => send_write(tx, Err(e), String::new()),
    }
}

/// Fans the slider volume out to every clip with live channels (`audio/set <clip> vol=`). A clip
/// that finished between the poll and the write returns `ENOENT` — ignored by design.
fn dispatch_volume(
    source: &dyn Source,
    sounds: &[WorkerSound],
    counts: Option<&[u32]>,
    volume: f64,
    tx: &Sender<FromWorker>,
) {
    let mut failed: Option<CmdError> = None;
    if let Some(counts) = counts {
        for (sound, &count) in sounds.iter().zip(counts) {
            if count == 0 {
                continue;
            }
            if let Err(e) = source.write("audio/set", &set_value(&sound.clip, volume)) {
                if e.errno != "ENOENT" {
                    failed = Some(e);
                }
            }
        }
    }
    let pct = (volume * 100.0).round() as i32;
    match failed {
        None => send_write(tx, Ok(()), format!("vol \u{2192} {pct}%")),
        Some(e) => send_write(tx, Err(e), String::new()),
    }
}

fn send_write(tx: &Sender<FromWorker>, result: Result<(), CmdError>, ok_msg: String) {
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
