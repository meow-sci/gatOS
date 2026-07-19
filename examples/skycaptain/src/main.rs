//! Binary: CLI parsing, terminal lifecycle, and the worker thread. The worker owns the
//! [`sim::Source`] and (while flying) the [`flight::Flight`]; the main thread renders and forwards
//! keys — the land-o-matic threading pattern.

use std::io;
use std::sync::mpsc::{self, RecvTimeoutError};
use std::thread;
use std::time::Duration;

use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{self, Event, KeyEventKind};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::Terminal;

use skycaptain::app::{App, FromWorker, ToWorker};
use skycaptain::flight::{Flight, FlightCfg};
use skycaptain::plan::PlanEnv;
use skycaptain::sim::{self, FsSource, HttpSource, Source};
use skycaptain::simulate::{SimClock, SimWorld};
use skycaptain::ui;

struct Config {
    source: SourceChoice,
    interval: Duration,
    text: String,
    env: PlanEnv,
    cfg: FlightCfg,
}

enum SourceChoice {
    Fs(String),
    Http(String),
    Simulate,
}

fn parse_args() -> Result<Config, String> {
    let mut cfg = Config {
        source: SourceChoice::Fs("/sim".into()),
        interval: Duration::from_millis(120),
        text: String::new(),
        env: PlanEnv::default(),
        cfg: FlightCfg::default(),
    };
    let mut chose_source = false;
    let mut args = std::env::args().skip(1);
    while let Some(a) = args.next() {
        let mut val = |name: &str| args.next().ok_or(format!("{name} needs a value"));
        match a.as_str() {
            "--url" => {
                cfg.source = SourceChoice::Http(val("--url")?);
                chose_source = true;
            }
            "--root" => {
                cfg.source = SourceChoice::Fs(val("--root")?);
                chose_source = true;
            }
            "--simulate" => {
                cfg.source = SourceChoice::Simulate;
                chose_source = true;
            }
            "--interval" => {
                let ms: u64 = val("--interval")?
                    .parse()
                    .map_err(|_| "--interval: bad ms")?;
                cfg.interval = Duration::from_millis(ms.max(20));
            }
            "--text" => cfg.text = val("--text")?,
            "--height" => cfg.env.height = parse_f(&val("--height")?, 200.0, 5000.0)?,
            "--speed" => cfg.env.v_draw = parse_f(&val("--speed")?, 20.0, 300.0)?,
            "--heading" => cfg.cfg.heading_deg = parse_f(&val("--heading")?, 0.0, 360.0)?,
            "--warp-draw" => cfg.cfg.warp_draw = parse_f(&val("--warp-draw")?, 1.0, 30.0)?,
            "--warp-hop" => cfg.cfg.warp_hop = parse_f(&val("--warp-hop")?, 1.0, 30.0)?,
            "--warp-fine" => cfg.cfg.warp_fine = parse_f(&val("--warp-fine")?, 1.0, 10.0)?,
            "--floor" => cfg.cfg.floor_radar = parse_f(&val("--floor")?, 0.0, 100_000.0)?,
            "--slew" => cfg.env.slew_dps = parse_f(&val("--slew")?, 1.0, 45.0)?,
            "--tilt" => cfg.env.tilt_max_deg = parse_f(&val("--tilt")?, 5.0, 35.0)?,
            "--allow-impulse" => cfg.cfg.allow_impulse = true,
            "--cheat-refill" => cfg.cfg.cheat_refill = true,
            "-h" | "--help" => {
                println!("{HELP}");
                std::process::exit(0);
            }
            other => return Err(format!("unknown flag {other} (see --help)")),
        }
    }
    if !chose_source {
        if std::path::Path::new("/sim").is_dir() {
            cfg.source = SourceChoice::Fs("/sim".into());
        } else if let Ok(url) = std::env::var("GATOS_HTTP") {
            cfg.source = SourceChoice::Http(url);
        }
    }
    Ok(cfg)
}

fn parse_f(s: &str, lo: f64, hi: f64) -> Result<f64, String> {
    s.parse::<f64>()
        .map_err(|_| format!("bad number {s}"))
        .map(|v| v.clamp(lo, hi))
}

const HELP: &str = "\
skycaptain — write text in the sky with your engine plume (gatOS /sim)

USAGE: skycaptain [--text MSG] [options]

source (default: /sim mount, else $GATOS_HTTP):
  --url BASE        HTTP /v1 mirror, e.g. http://127.0.0.1:4242/v1
  --root DIR        a /sim-shaped directory (fixtures)
  --simulate        built-in physics sandbox — no game needed

writing:
  --text MSG        pre-fill the message (you can still edit in the TUI)
  --height M        letter cap height, m               [900]
  --speed M/S       draw speed along strokes           [100]
  --heading DEG     compass heading the text runs      [90 = east]

time compression (needs the gatOS debug namespace):
  --warp-draw X     warp while painting                [4]
  --warp-hop X      warp while coasting between glyphs [10]
  --warp-fine X     warp inside cut/relight windows    [2]

tuning (match your craft's flight computer):
  --slew DPS        FC attitude slew rate assumption   [5]
  --tilt DEG        max thrust tilt while painting     [12; auto-raised on low-g bodies]

safety / cheats:
  --floor M         rescue under this radar altitude   [250]
  --allow-impulse   let unsolvable hops (mid-text dots) use debug impulse
  --cheat-refill    keep the tank topped up with debug refill_fuel (also
                    steadies the thrust model — recommended for long text)

  --interval MS     worker poll cadence                [120]

Aborts and completion both end in a RESCUE: warp 1x, engine on, brake to a
hover, then the vehicle is parked on the game FC's own nose-up hover hold.
";

type Tui = Terminal<CrosstermBackend<io::Stdout>>;

fn setup_terminal() -> io::Result<Tui> {
    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen)?;
    Terminal::new(CrosstermBackend::new(stdout))
}

fn restore_terminal() {
    let _ = disable_raw_mode();
    let _ = execute!(io::stdout(), LeaveAlternateScreen);
}

fn install_panic_hook() {
    let original = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        restore_terminal();
        original(info);
    }));
}

fn main() -> io::Result<()> {
    let config = match parse_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("skycaptain: {e}");
            std::process::exit(2);
        }
    };
    install_panic_hook();
    let mut terminal = setup_terminal()?;
    let res = run(&mut terminal, config);
    restore_terminal();
    res
}

fn build_source(choice: SourceChoice) -> Box<dyn Source> {
    match choice {
        SourceChoice::Fs(root) => Box::new(FsSource::new(root)),
        SourceChoice::Http(base) => Box::new(HttpSource::new(base)),
        SourceChoice::Simulate => Box::new(SimWorld::new(SimClock::Wall)),
    }
}

fn run(terminal: &mut Tui, config: Config) -> io::Result<()> {
    let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
    let (update_tx, update_rx) = mpsc::channel::<FromWorker>();

    let source = build_source(config.source);
    let label = source.label();
    let env = config.env;
    let fcfg = config.cfg;
    let interval = config.interval;
    thread::spawn(move || worker(source, interval, env, fcfg, cmd_rx, update_tx));

    let mut app = App::new(
        cmd_tx,
        label,
        config.text,
        env.height,
        env.v_draw,
        (fcfg.warp_draw, fcfg.warp_hop),
    );

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
    // Dropping cmd_tx ends the worker (its recv sees Disconnected).
}

fn worker(
    source: Box<dyn Source>,
    interval: Duration,
    env: PlanEnv,
    fcfg: FlightCfg,
    rx: mpsc::Receiver<ToWorker>,
    tx: mpsc::Sender<FromWorker>,
) {
    let src = &*source;
    let mut flying: Option<Flight> = None;

    loop {
        let tick = sim::poll(src);

        if let Some(f) = flying.as_mut() {
            let view = f.step(src, &tick);
            let over = f.is_over();
            if tx.send(FromWorker::Flight(view)).is_err() {
                return;
            }
            if over {
                flying = None;
            }
        } else {
            let checks = readiness(src, &tick, &env, &fcfg);
            if tx
                .send(FromWorker::Idle {
                    connected: tick.connected,
                    label: src.label(),
                    checks,
                })
                .is_err()
            {
                return;
            }
        }

        // Drain pilot commands; the timeout is the poll pacing.
        match rx.recv_timeout(interval) {
            Ok(first) => {
                let mut queue = vec![first];
                while let Ok(more) = rx.try_recv() {
                    queue.push(more);
                }
                for cmd in queue {
                    match cmd {
                        ToWorker::Start(text) => {
                            if flying.is_none() {
                                let fresh = sim::poll(src);
                                let mut notes = Vec::new();
                                match Flight::start(src, &fresh, &text, fcfg, env, &mut notes) {
                                    Ok(f) => {
                                        let _ = tx.send(FromWorker::Started {
                                            letters: f.plan.letters.iter().map(|l| l.ch).collect(),
                                            outline: f.outline(),
                                            total_time: f.plan.total_time,
                                            notes,
                                        });
                                        let _ = tx.send(FromWorker::Log(format!(
                                            "plan: {:.0} s of game time, {:.0} s of it painting",
                                            f.plan.total_time, f.plan.paint_time
                                        )));
                                        flying = Some(f);
                                    }
                                    Err(reason) => {
                                        let _ = tx.send(FromWorker::StartFailed { reason, notes });
                                    }
                                }
                            }
                        }
                        ToWorker::Abort => {
                            if let Some(f) = flying.as_mut() {
                                f.abort(src, "pilot abort");
                            }
                        }
                    }
                }
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => {
                if let Some(f) = flying.as_mut() {
                    // Parting act: park the vehicle on the game FC's hover hold (engine ON).
                    f.hard_abort(src, "ui closed");
                }
                return;
            }
        }
    }
}

/// The compose screen's pre-flight checklist (informational — `Flight::start` re-validates).
fn readiness(
    src: &dyn Source,
    tick: &sim::Tick,
    env: &PlanEnv,
    fcfg: &FlightCfg,
) -> Vec<(String, bool)> {
    let mut out = Vec::new();
    let Some(tel) = tick.telemetry.as_ref() else {
        out.push(("active vessel with telemetry".into(), false));
        return out;
    };
    out.push((
        format!("vessel {} · controllable", tel.id),
        tel.controllable,
    ));

    match sim::read_engines(src) {
        Some(eng) => {
            let r = skycaptain::vec3::Vec3::from_array(tel.pos_cci).norm();
            let g = tick.body.map(|b| b.mu / (r * r)).unwrap_or(9.81);
            let twr = eng.thrust_max / (tel.mass.t * g);
            out.push((format!("engines · TWR {twr:.2} (need ≥ 1.25)"), twr >= 1.25));
        }
        None => out.push(("engines readable".into(), false)),
    }

    match tick.body.and_then(|b| b.atmosphere) {
        Some(atmo) => {
            let rho = atmo.density_at(tel.alt.baro);
            let in_band = tel.alt.baro < atmo.height && rho >= 1e-9;
            out.push((
                format!(
                    "atmosphere · alt {:.1} km of {:.0} km ceiling (trail {})",
                    tel.alt.baro / 1000.0,
                    atmo.height / 1000.0,
                    if in_band {
                        "forms here"
                    } else {
                        "will NOT form here"
                    }
                ),
                in_band,
            ));
        }
        None => out.push(("atmosphere (plume trails need one!)".into(), false)),
    }

    let drop = 1.2 * env.height;
    out.push((
        format!(
            "clearance · radar {:.0} m (text hangs {:.0} m below)",
            tel.alt.radar, drop
        ),
        tel.alt.radar > drop + fcfg.floor_radar,
    ));
    out.push((
        format!("hover-ish · surface speed {:.0} m/s (< 400)", tel.vel.surf),
        tel.vel.surf < 400.0,
    ));

    let warp_ok = src.read("time/warp_speeds").is_ok() && src.read("debug/time/warp").is_ok();
    out.push((
        if warp_ok {
            "time-warp control (debug namespace)".into()
        } else {
            "time-warp control off — will write at current warp".into()
        },
        warp_ok,
    ));
    out.push((
        format!("propellant · {:.0} kg", tel.mass.p),
        tel.mass.p > 50.0,
    ));
    out
}
