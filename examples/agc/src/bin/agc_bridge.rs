//! `agc-bridge` — the spacecraft-side of the AGC (AGC_PLAN §4.3): converts live `/sim`
//! telemetry into the electrical world Luminary099 expects (CDU counts, PIPA pulses, radar
//! words, discretes) and the AGC's outputs (jets, engine on/off, THRUST pulse trains) back
//! into `/sim` control writes. One sim-poll worker thread; the main thread owns the AGC port.

use std::sync::mpsc;
use std::time::{Duration, Instant};

use agc::clockpolicy::{ClockPolicy, Hold};
use agc::discretes::Discretes;
use agc::downlink::Downlink;
use agc::engines::Engines;
use agc::imu::Imu;
use agc::ksa_quat::{self, Quat};
use agc::pipa::Pipa;
use agc::proto::{chan, AgcEvent, AgcPort, SocketPort};
use agc::radar::Radar;
use agc::rcs::{batch_lines, Rcs};
use agc::sim::{self, FsSource, HttpSource, Source, Tick};
use agc::uplink::Uplink;
use agc::vec3::Vec3;

struct Config {
    port: u16,
    root: String,
    url: Option<String>,
    switches: String,
    cmd_dir: String,
    downlink: Option<String>,
    interval: Duration,
    vehicle: String,
    embedded: bool,
    rope: Option<String>,
    padload: Option<String>,
}

fn parse_args() -> Config {
    let mut c = Config {
        port: 19797,
        root: "/sim".into(),
        url: None,
        switches: "/run/agc/switches".into(),
        cmd_dir: "/run/agc/lm/cmd".into(),
        downlink: None,
        interval: Duration::from_millis(25),
        vehicle: "lm".into(),
        embedded: false,
        rope: None,
        padload: None,
    };
    for a in std::env::args().skip(1) {
        if let Some(v) = a.strip_prefix("--port=") {
            c.port = v.parse().unwrap_or(19797);
        } else if let Some(v) = a.strip_prefix("--root=") {
            c.root = v.into();
        } else if let Some(v) = a.strip_prefix("--url=") {
            c.url = Some(v.into());
        } else if let Some(v) = a.strip_prefix("--switches=") {
            c.switches = v.into();
        } else if let Some(v) = a.strip_prefix("--cmd-dir=") {
            c.cmd_dir = v.into();
        } else if let Some(v) = a.strip_prefix("--downlink=") {
            c.downlink = Some(v.into());
        } else if let Some(v) = a.strip_prefix("--interval=") {
            c.interval = Duration::from_millis(v.parse().unwrap_or(25));
        } else if let Some(v) = a.strip_prefix("--vehicle=") {
            c.vehicle = v.into();
        } else if let Some(v) = a.strip_prefix("--agc=") {
            c.embedded = v == "embedded";
        } else if let Some(v) = a.strip_prefix("--rope=") {
            c.rope = Some(v.into());
        } else if let Some(v) = a.strip_prefix("--padload=") {
            c.padload = Some(v.into());
        } else if a == "--help" || a == "-h" {
            println!(
                "agc-bridge [--vehicle=lm] [--port=19797] [--root=/sim | --url=http://sim:4242/v1]\n\
                 [--agc=extern|embedded] [--rope=FILE.bin] [--padload=FILE.core]\n\
                 [--switches=DIR] [--cmd-dir=DIR] [--downlink=FILE.ndjson] [--interval=25]"
            );
            std::process::exit(0);
        }
    }
    c
}

fn main() {
    let cfg = parse_args();
    let source: Box<dyn Source> = match &cfg.url {
        Some(u) => Box::new(HttpSource::new(u.clone())),
        None => Box::new(FsSource::new(&cfg.root)),
    };
    eprintln!(
        "agc-bridge: vehicle={} agc=127.0.0.1:{} sim={}",
        cfg.vehicle,
        cfg.port,
        source.label()
    );

    // Sim worker: one thread owns the source, polls each interval, sends ticks over a channel.
    let (tick_tx, tick_rx) = mpsc::channel::<Tick>();
    let (ctl_tx, ctl_rx) = mpsc::channel::<Vec<(String, String)>>();
    let interval = cfg.interval;
    let worker = std::thread::spawn(move || {
        loop {
            let tick = sim::poll(&*source);
            if tick_tx.send(tick).is_err() {
                return;
            }
            // Apply any control writes the main loop queued (throttle/ignite/batch/…).
            let deadline = Instant::now() + interval;
            while let Ok(writes) = ctl_rx.recv_timeout(deadline.saturating_duration_since(Instant::now())) {
                for (path, value) in writes {
                    if let Err(e) = source.write(&path, &value) {
                        eprintln!("write {path} <- {value}: {} {}", e.errno, e.message);
                    }
                }
                if Instant::now() >= deadline {
                    break;
                }
            }
        }
    });

    let mut port: Box<dyn AgcPort> = if cfg.embedded {
        #[cfg(feature = "embedded")]
        {
            let rope = cfg.rope.clone().expect("--agc=embedded needs --rope=FILE.bin");
            match agc::proto::EmbeddedPort::new(&rope, cfg.padload.as_deref(), cfg.port) {
                Ok(p) => Box::new(p),
                Err(e) => {
                    eprintln!("embedded init failed: {e}");
                    std::process::exit(1);
                }
            }
        }
        #[cfg(not(feature = "embedded"))]
        {
            eprintln!("this binary was built without --features embedded; falling back to extern");
            Box::new(SocketPort::new(cfg.port))
        }
    } else {
        Box::new(SocketPort::new(cfg.port))
    };
    let mut imu = Imu::new();
    let mut pipa = Pipa::new();
    let mut radar = Radar::new();
    let mut engines = Engines::new();
    let mut rcs = Rcs::new();
    let mut discretes = Discretes::new(&cfg.switches);
    let mut uplink = Uplink::new();
    let mut clock = ClockPolicy::new();
    let mut downlink = Downlink::new(cfg.downlink.as_deref().map(std::path::Path::new));

    let t0 = Instant::now();
    let mut last: Option<Tick> = None;
    let mut ch12 = 0u16;
    let mut last_status = Instant::now();
    let mut last_loop = Instant::now();

    std::fs::create_dir_all(&cfg.cmd_dir).ok();

    loop {
        let now = t0.elapsed().as_secs_f64();
        let loop_dt = last_loop.elapsed().as_secs_f64();
        last_loop = Instant::now();
        // Embedded mode: WE are the AGC's clock. Pause/warp holds freeze the engine exactly
        // (§3.4 — the mission clock stops with the game). Extern mode: no-op.
        if !matches!(clock.holding(), Some(Hold::Paused) | Some(Hold::Warp)) {
            port.step(loop_dt);
        }
        let mut writes: Vec<(String, String)> = Vec::new();

        // ---- drain the AGC ----
        while let Some(ev) = port.recv() {
            match ev {
                AgcEvent::Channel { channel, value } => {
                    match channel {
                        chan::JETS_V | chan::JETS_H => rcs.on_jets(channel, value, now),
                        chan::DSALMOUT => {
                            let cmds = engines.on_dsalmout(value, discretes.engine_armed());
                            if cmds.ignite {
                                writes.push(("vessels/active/ctl/ignite".into(), "1".into()));
                            }
                            if cmds.shutdown {
                                writes.push(("vessels/active/ctl/shutdown".into(), "1".into()));
                            }
                        }
                        chan::CHAN12 => {
                            let was = ch12;
                            ch12 = value;
                            imu.coarse_enable = value & (1 << 3) != 0; // b4
                            let zero = value & (1 << 4) != 0; // b5
                            if zero != ((was & (1 << 4)) != 0) {
                                imu.on_zero_cdu(zero);
                            }
                            radar.on_pos2_command(value & (1 << 12) != 0); // b13
                        }
                        chan::CHAN13 => {
                            // Radar activity (b4) + select code (b1-3): answer the race now.
                            if value & 0o10 != 0 {
                                if let Some(t) = last.as_ref().and_then(|t| t.telemetry.as_ref()) {
                                    let body = last.as_ref().and_then(|t| t.body).unwrap_or_default();
                                    let v_surf = surface_velocity_body(t, body.rotation_rate);
                                    radar.deliver(&mut *port, (value & 7) as u8, t.alt.radar, v_surf);
                                }
                            }
                        }
                        chan::CHAN14 => engines.on_chan14(value),
                        chan::CDUX_DRIVE | chan::CDUY_DRIVE | chan::CDUZ_DRIVE => {
                            if let Some(t) = last.as_ref().and_then(|t| t.telemetry.as_ref()) {
                                let axis = (channel - chan::CDUX_DRIVE) as usize;
                                imu.on_cdu_drive(axis, value, Quat::from_array(t.att_q));
                            }
                        }
                        chan::GYRO => imu.on_gyro_burst(value),
                        0o34 | 0o35 => {
                            let ut = last
                                .as_ref()
                                .and_then(|t| t.telemetry.as_ref())
                                .map(|t| t.ut)
                                .unwrap_or(0.0);
                            downlink.on_channel(channel, value, ut);
                        }
                        _ => {}
                    }
                }
                AgcEvent::CounterEcho { register, pulse } => {
                    if register == agc::proto::reg::THRUST {
                        engines.on_thrust_echo(pulse);
                    }
                }
                AgcEvent::KeepAlive => {}
            }
        }

        // ---- launcher command spool (`agc align`, `agc uplink FILE`) ----
        service_cmd_dir(&cfg.cmd_dir, &mut uplink, &imu);

        // ---- sim ticks ----
        while let Ok(tick) = tick_rx.try_recv() {
            let dt = cfg.interval.as_secs_f64();
            if let Some(t) = tick.telemetry.clone() {
                let verdict = clock.gate(t.seq, t.ut, t.warp, tick.sim_dt);
                if let Some(hold) = verdict.hold {
                    pipa.reset();
                    if matches!(hold, Hold::Paused | Hold::Warp) {
                        // Hold feeds + actuation entirely (writes would ETIMEDOUT on pause).
                    }
                } else {
                    if let Some(drift) = verdict.resync {
                        // Extern-mode pause drift: trim the mission clock via V55 (best effort;
                        // A6 embedded mode freezes the engine instead — plan §3.4).
                        queue_clock_trim(&mut uplink, drift);
                    }
                    let q = Quat::from_array(t.att_q);
                    let body = tick.body.unwrap_or_default();
                    let rate = tick
                        .rates
                        .map(|r| Vec3::from_array(r).norm())
                        .unwrap_or(0.0);
                    imu.operating = discretes.iss_operating;
                    imu.tick(&mut *port, q, rate, dt);
                    pipa.tick(
                        &mut *port,
                        Vec3::from_array(t.vel_cci),
                        Vec3::from_array(t.pos_cci),
                        t.ut,
                        body.mu,
                        imu.q_sm,
                        discretes.iss_operating,
                    );
                    let v_surf = surface_velocity_body(&t, body.rotation_rate);
                    radar.powered = discretes.get("lr_power");
                    radar.tick(&mut *port, dt, t.alt.radar, v_surf);
                    // Embedded RequestRadarData hook: keep the word table fresh.
                    let words = radar.words(t.alt.radar, v_surf);
                    port.set_radar_words(words, radar.range_good || radar.vel_good);

                    // Actuation out.
                    if let Some(signs) = rcs.tick(now, dt) {
                        let lines = batch_lines(signs);
                        writes.push(("vessels/active/ctl/batch".into(), lines.join("\n")));
                    }
                    let e = engines.tick(&mut *port);
                    if let Some(thr) = e.throttle {
                        writes.push(("vessels/active/ctl/throttle".into(), format!("{thr:.4}")));
                    }
                }
            }
            last = Some(tick);
        }

        discretes.tick(&mut *port, now);
        uplink.tick(&mut *port);

        if !writes.is_empty() && clock.holding().is_none() && ctl_tx.send(writes).is_err() {
            break;
        }

        if last_status.elapsed() > Duration::from_secs(10) {
            last_status = Instant::now();
            let (sent, received) = port.stats();
            eprintln!(
                "status: agc={} sent={sent} recv={received} hold={:?} pulses={:?} thrust_pulses={} dl_pairs={}",
                port.connected(),
                clock.holding(),
                pipa.last_pulses,
                engines.pulses,
                downlink.pairs,
            );
        }

        std::thread::sleep(Duration::from_millis(2));
        if worker.is_finished() {
            eprintln!("sim worker exited; shutting down");
            break;
        }
    }
}

/// Surface-relative velocity in the (KSA≡LM under identity body_map) body frame:
/// v_surf = v_cci − ω×r, then CCI→body via the attitude quaternion.
fn surface_velocity_body(t: &agc::sim::Telemetry, rotation_rate: f64) -> Vec3 {
    let r = Vec3::from_array(t.pos_cci);
    let v = Vec3::from_array(t.vel_cci);
    let omega = Vec3::new(0.0, 0.0, rotation_rate);
    let v_surf_cci = v - omega.cross(r);
    ksa_quat::transform(v_surf_cci, Quat::from_array(t.att_q).conj())
}

/// `agc align`: cage the virtual platform to the current body attitude (a perfect P57), then
/// uplink the matching REFSMMAT so Luminary's idea of the platform agrees, then V41N20 coarse
/// align to zero the CDUs.
fn service_cmd_dir(dir: &str, uplink: &mut Uplink, imu: &Imu) {
    let Ok(entries) = std::fs::read_dir(dir) else { return };
    for e in entries.flatten() {
        let path = e.path();
        if path.extension().and_then(|s| s.to_str()) != Some("cmd") {
            continue;
        }
        let Ok(text) = std::fs::read_to_string(&path) else { continue };
        std::fs::remove_file(&path).ok();
        let line = text.trim();
        if line == "align" {
            let rows = [
                ksa_quat::transform(Vec3::x(), imu.q_sm).to_array(),
                ksa_quat::transform(Vec3::y(), imu.q_sm).to_array(),
                ksa_quat::transform(Vec3::z(), imu.q_sm).to_array(),
            ];
            let script = agc::uplink::refsmmat_uplink(rows);
            if let Err(err) = uplink.push_keys(&script) {
                eprintln!("align: {err}");
            }
            let _ = uplink.push_keys("V41N20E");
            eprintln!("align: REFSMMAT uplink queued ({} words) + V41N20E", uplink.pending());
        } else if let Some(file) = line.strip_prefix("uplink ") {
            match std::fs::read_to_string(file.trim()) {
                Ok(keys) => {
                    let compact: String = keys.split_whitespace().collect();
                    match uplink.push_keys(&compact) {
                        Ok(()) => eprintln!("uplink: {} queued ({} words)", file.trim(), uplink.pending()),
                        Err(err) => eprintln!("uplink: {err}"),
                    }
                }
                Err(err) => eprintln!("uplink: {file}: {err}"),
            }
        }
    }
}

/// V55 clock trim after a pause: increment the mission clock by the hold duration
/// (R1 hours, R2 minutes, R3 0.01 s — best effort to the nearest centisecond).
fn queue_clock_trim(uplink: &mut Uplink, drift_s: f64) {
    if drift_s < 0.5 {
        return;
    }
    let total_cs = (drift_s * 100.0).round() as u64;
    let h = total_cs / 360_000;
    let m = (total_cs % 360_000) / 6_000;
    let cs = total_cs % 6_000;
    let keys = format!("V55E+{h:05}E+{m:05}E+{cs:05}E");
    if uplink.push_keys(&keys).is_ok() {
        eprintln!("resync: V55 clock trim {drift_s:.2}s queued");
    }
}
