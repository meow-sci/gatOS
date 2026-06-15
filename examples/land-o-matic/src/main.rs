//! land-o-matic — a powered-descent landing-guidance TUI over the gatOS `/sim` filesystem, built with
//! ratatui. It fuses two real flight-guidance algorithms: **G-FOLD** (convex fuel-optimal powered
//! descent — the planner/replanner) and, from M4, **UPFG** (the Space Shuttle's explicit guidance, via
//! PEGAS — the closed-loop terminal steering). See `LANDING_PROGRAM_PLAN.md` for the full design and
//! the reference-frame contract.
//!
//! Run it **in the guest** (`apk add --no-cache cargo rust && cargo run --release`): it reads the
//! `/sim` mount and drives the active vessel. For host-side dev, point it at a fixture directory with
//! `--root <dir>` or the mod's HTTP mirror with `--url <base>`.
//!
//! Architecture (mirrors the sibling examples): one worker thread owns the [`sim::Source`] **and the
//! autopilot** — each tick it polls telemetry, runs the G-FOLD MPC, and writes the control files —
//! while the main thread runs the render + input loop, so the UI never blocks on I/O or the solver.

use std::io::{self, Stdout};
use std::path::Path;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::Duration;

use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{self, Event, KeyEventKind};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::Terminal;

use land_o_matic::app::{App, FromWorker, GuidanceView, ToWorker};
use land_o_matic::guidance::autopilot::{Autopilot, Command, Inputs, Phase, State, VehicleSpec};
use land_o_matic::guidance::Vec3;
use land_o_matic::sim::{self, Body, FsSource, HttpSource, Source, Telemetry};

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

        // 200 ms ≈ a 5 Hz guidance re-solve cadence; floor 20 ms.
        let interval = Duration::from_millis(interval_ms.unwrap_or(200).max(20));
        Ok(Self {
            source: resolve_source(url, root),
            interval,
            help,
        })
    }
}

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
    println!("  --interval <ms>  poll + guidance cadence, min 20 (default 200)");
    println!();
    println!("In the guest, no flags are needed: it reads /sim and guides the active vessel.");
    println!("Keys: e ENGAGE \u{b7} a ABORT \u{b7} \u{2191}/\u{2193} (or -/=) G-limit \u{b7} q quit.");
    println!();
    println!("SAFETY: this fires your engine and steers the vessel. Watch it; press a to abort.");
}

fn run(terminal: &mut Tui, config: Config) -> io::Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    spawn_worker(source, config.interval, cmd_rx, update_tx);

    let mut app = App::new(cmd_tx, label);

    let tick = Duration::from_millis(80);
    while !app.should_quit {
        while let Ok(update) = update_rx.try_recv() {
            app.apply(update);
        }
        terminal.draw(|f| land_o_matic::ui::render(f, &app))?;
        if event::poll(tick)? {
            if let Event::Key(k) = event::read()? {
                if k.kind == KeyEventKind::Press {
                    app.on_key(k);
                }
            }
        }
    }
    Ok(())
    // Dropping `cmd_tx` (on quit) makes the worker's recv_timeout return Disconnected, so it exits.
}

/// The worker owns the source and the autopilot. Each loop: poll telemetry → run the G-FOLD MPC → write
/// the control files → push state to the UI, then wait up to one interval for pilot commands.
fn spawn_worker(
    source: Box<dyn Source>,
    interval: Duration,
    rx: Receiver<ToWorker>,
    tx: Sender<FromWorker>,
) {
    thread::spawn(move || {
        let mut autopilot = Autopilot::new(Inputs::default());
        let mut spec: Option<VehicleSpec> = None;

        loop {
            let tick = sim::poll(&*source);
            let mut guidance = None;
            let mut status = None;

            if let (Some(tel), Some(body)) = (tick.telemetry.as_ref(), tick.body.as_ref()) {
                if spec.is_none() {
                    spec = read_spec(&*source, tel);
                }
                if let Some(sp) = spec {
                    let lon = sim::read_longitude(&*source).unwrap_or(0.0);
                    let state = build_state(tel, body, lon);
                    let cmd = autopilot.step(&state, &sp);
                    status = apply_command(&*source, &cmd);
                    guidance = Some(GuidanceView {
                        phase: cmd.phase,
                        throttle: cmd.throttle,
                        tgo: cmd.tgo,
                        predicted_mass: cmd.predicted_mass,
                        peak_g: cmd.peak_g,
                    });
                }
            } else {
                spec = None; // lost the vessel; re-read engines when it returns
            }

            if tx.send(FromWorker::Tick { tick, guidance, status }).is_err() {
                return;
            }

            match rx.recv_timeout(interval) {
                Ok(first) => {
                    apply_input(&mut autopilot, first);
                    while let Ok(more) = rx.try_recv() {
                        apply_input(&mut autopilot, more);
                    }
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => return,
            }
        }
    });
}

fn apply_input(ap: &mut Autopilot, cmd: ToWorker) {
    match cmd {
        ToWorker::SetGLimit(v) => ap.inputs.g_limit = v,
        ToWorker::Engage => ap.engage(),
        ToWorker::Abort => ap.abort(),
    }
}

fn read_spec(src: &dyn Source, tel: &Telemetry) -> Option<VehicleSpec> {
    let eng = sim::read_engines(src)?;
    Some(VehicleSpec {
        m_dry: tel.mass.d,
        isp: eng.isp,
        thrust_max: eng.thrust_max,
        throttle_min: eng.throttle_min,
        throttle_max: 1.0,
    })
}

fn build_state(tel: &Telemetry, body: &Body, lon_deg: f64) -> State {
    State {
        ut: tel.ut,
        pos_cci: Vec3::new(tel.pos_cci[0], tel.pos_cci[1], tel.pos_cci[2]),
        vel_cci: Vec3::new(tel.vel_cci[0], tel.vel_cci[1], tel.vel_cci[2]),
        mass: tel.mass.t,
        radar_alt: tel.alt.radar,
        lon_deg,
        mu: body.mu,
        omega: body.rotation_rate,
    }
}

/// Translate an autopilot [`Command`] into `/sim` control writes, per phase. Returns a status line when
/// notable. Idle writes nothing (the pilot keeps control).
fn apply_command(src: &dyn Source, cmd: &Command) -> Option<(String, bool)> {
    let write_attitude = |q: land_o_matic::guidance::ksa_quat::Quat| {
        let a = q.to_array();
        src.write(
            "vessels/active/ctl/attitude_target",
            &format!("{} {} {} {}", a[0], a[1], a[2], a[3]),
        )
    };
    match cmd.phase {
        Phase::Idle => None,
        Phase::Burn => {
            let mut err = None;
            if let Some(q) = cmd.attitude_target {
                if let Err(e) = write_attitude(q) {
                    err = Some((format!("attitude: {}: {}", e.errno, e.message), true));
                }
            }
            let _ = src.write("vessels/active/ctl/throttle", &format!("{:.4}", cmd.throttle));
            if cmd.ignite {
                let _ = src.write("vessels/active/ctl/ignite", "1");
            }
            err.or(Some((format!("BURN \u{b7} tgo {:.0}s", cmd.tgo), false)))
        }
        Phase::Infeasible => {
            if let Some(q) = cmd.attitude_target {
                let _ = write_attitude(q);
            }
            let _ = src.write("vessels/active/ctl/throttle", "0");
            Some(("no feasible landing solution".to_string(), true))
        }
        Phase::Touchdown => {
            let _ = src.write("vessels/active/ctl/throttle", "0");
            let _ = src.write("vessels/active/ctl/shutdown", "1");
            let _ = src.write("vessels/active/ctl/attitude_mode", "manual");
            Some(("touchdown \u{2014} engine cut".to_string(), false))
        }
        Phase::Abort => {
            let _ = src.write("vessels/active/ctl/throttle", "0");
            let _ = src.write("vessels/active/ctl/shutdown", "1");
            let _ = src.write("vessels/active/ctl/attitude_mode", "manual");
            Some(("ABORT \u{2014} released to manual".to_string(), true))
        }
    }
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

#[cfg(test)]
mod tests {
    use super::*;
    use land_o_matic::guidance::ksa_quat::Quat;
    use std::fs;

    fn fixture(tag: &str) -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!("lom_main_{tag}_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        root
    }

    #[test]
    fn read_spec_aggregates_engines() {
        let root = fixture("spec");
        for n in ["0", "1"] {
            fs::create_dir_all(root.join(format!("vessels/active/engines/{n}"))).unwrap();
            fs::write(root.join(format!("vessels/active/engines/{n}/vac_thrust")), "40000\n").unwrap();
            fs::write(root.join(format!("vessels/active/engines/{n}/isp")), "300\n").unwrap();
            fs::write(root.join(format!("vessels/active/engines/{n}/min_throttle")), "0.1\n").unwrap();
        }
        let src = FsSource::new(&root);
        let tel: Telemetry = serde_json::from_str(
            r#"{"seq":1,"ut":0,"warp":1,"id":"x","sit":"Freefall","controlled":true,
                "pos_cci":[0,0,0],"vel_cci":[0,0,0],"vel":{"orb":0,"surf":0,"inr":0},
                "alt":{"baro":0,"radar":0},"mass":{"t":1500,"d":1000,"p":500},
                "att_q":[0,0,0,1],"power":{"prod":0,"cons":0}}"#,
        )
        .unwrap();
        let spec = read_spec(&src, &tel).expect("spec");
        assert!((spec.thrust_max - 80000.0).abs() < 1.0);
        assert!((spec.isp - 300.0).abs() < 1.0);
        assert_eq!(spec.m_dry, 1000.0);
        fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn apply_burn_writes_controls() {
        let root = fixture("burn");
        fs::create_dir_all(root.join("vessels/active/ctl")).unwrap();
        for f in ["attitude_target", "throttle", "ignite"] {
            fs::write(root.join(format!("vessels/active/ctl/{f}")), "\n").unwrap();
        }
        let src = FsSource::new(&root);
        let cmd = Command {
            phase: Phase::Burn,
            attitude_target: Some(Quat::IDENTITY),
            throttle: 0.5,
            ignite: true,
            tgo: 10.0,
            predicted_mass: 1200.0,
            peak_g: 3.0,
        };
        apply_command(&src, &cmd);
        assert_eq!(src.read("vessels/active/ctl/throttle").unwrap(), "0.5000");
        assert_eq!(src.read("vessels/active/ctl/attitude_target").unwrap(), "0 0 0 1");
        assert_eq!(src.read("vessels/active/ctl/ignite").unwrap(), "1");
        fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn idle_writes_nothing() {
        let root = fixture("idle");
        fs::create_dir_all(root.join("vessels/active/ctl")).unwrap();
        fs::write(root.join("vessels/active/ctl/throttle"), "0.9\n").unwrap();
        let src = FsSource::new(&root);
        let cmd = Command {
            phase: Phase::Idle,
            attitude_target: None,
            throttle: 0.0,
            ignite: false,
            tgo: 0.0,
            predicted_mass: 0.0,
            peak_g: 0.0,
        };
        apply_command(&src, &cmd);
        // Idle must not touch controls — the pilot keeps manual control.
        assert_eq!(src.read("vessels/active/ctl/throttle").unwrap(), "0.9");
        fs::remove_dir_all(&root).ok();
    }
}
