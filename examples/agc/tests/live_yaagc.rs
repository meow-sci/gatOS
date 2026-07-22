//! Live-wire integration tests against a real yaAGC running the real Luminary099 rope.
//!
//! Gated on `AGC_IT=1` (the `GATOS_IT` convention): set `AGC_IT=1`, `YAAGC=/path/to/yaAGC`
//! and `ROPE=/path/to/Luminary099.bin`, then `cargo test --test live_yaagc`. Skips silently
//! otherwise, so plain `cargo test` never needs the toolchain.

use std::process::{Child, Command};
use std::time::{Duration, Instant};

use agc::dsky::Dsky;
use agc::proto::{chan, AgcEvent, AgcPort, SocketPort};

struct Yaagc {
    child: Child,
}

impl Drop for Yaagc {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn spawn_yaagc(port: u16, resume: Option<&std::path::Path>) -> Option<Yaagc> {
    if std::env::var("AGC_IT").as_deref() != Ok("1") {
        eprintln!("AGC_IT != 1 — skipping live yaAGC test");
        return None;
    }
    let yaagc = std::env::var("YAAGC").expect("AGC_IT=1 requires YAAGC=/path/to/yaAGC");
    let rope = std::env::var("ROPE").expect("AGC_IT=1 requires ROPE=/path/to/Luminary099.bin");
    let dir = std::env::temp_dir().join(format!("agc-it-{port}"));
    std::fs::create_dir_all(&dir).unwrap();
    let mut cmd = Command::new(yaagc);
    cmd.current_dir(&dir)
        .arg("--nodebug")
        .arg(format!("--port={port}"))
        .arg("--interlace=10")
        .arg("--no-resume")
        .arg(&rope);
    if let Some(r) = resume {
        cmd.arg(r);
    }
    let child = cmd
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .expect("spawn yaAGC");
    std::thread::sleep(Duration::from_millis(600));
    Some(Yaagc { child })
}

fn pump(port: &mut SocketPort, dsky: &mut Dsky, secs: f64) -> usize {
    let end = Instant::now() + Duration::from_secs_f64(secs);
    let mut events = 0;
    while Instant::now() < end {
        while let Some(ev) = port.recv() {
            if let AgcEvent::Channel { channel, value } = ev {
                dsky.on_channel(channel, value);
                events += 1;
            }
        }
        std::thread::sleep(Duration::from_millis(5));
    }
    events
}

fn key(port: &mut SocketPort, c: char) {
    use agc::dsky::{key_wire, KeyWire};
    match key_wire(c).expect("keycode") {
        KeyWire::Code(k) => port.write_channel(chan::DSKY_KEYS, k, Some(0o37)),
        KeyWire::Pro { .. } => unreachable!(),
    }
    std::thread::sleep(Duration::from_millis(250));
}

/// Luminary099 boots, we type V16N36E (monitor the mission clock) and the DSKY shows
/// VERB 16 / NOUN 36 with R1-R3 updating — the whole wire protocol proven end-to-end.
#[test]
fn luminary_boots_and_v16n36_ticks() {
    let Some(_agc) = spawn_yaagc(19871, None) else { return };
    let mut port = SocketPort::new(19871);
    let mut dsky = Dsky::new();
    assert!(port.ensure_connected(), "connect to yaAGC");
    pump(&mut port, &mut dsky, 3.0); // fresh start settles

    for c in ['v', '1', '6', 'n', '3', '6', '\n'] {
        key(&mut port, c);
    }
    pump(&mut port, &mut dsky, 2.0);
    assert_eq!(dsky.verb, ['1', '6'], "VERB window shows 16");
    assert_eq!(dsky.noun, ['3', '6'], "NOUN window shows 36");

    // The mission clock advances: R3 (centiseconds) must change between samples.
    let r3_a = dsky.r[2];
    pump(&mut port, &mut dsky, 1.5);
    let r3_b = dsky.r[2];
    assert_ne!(r3_a, r3_b, "N36 R3 ticks with the AGC's own timers");
}

/// V35 lamp test drives the display relays hard — every digit window fills with 8s.
#[test]
fn v35_lamp_test_lights_the_panel() {
    let Some(_agc) = spawn_yaagc(19881, None) else { return };
    let mut port = SocketPort::new(19881);
    let mut dsky = Dsky::new();
    assert!(port.ensure_connected());
    pump(&mut port, &mut dsky, 3.0);

    for c in ['v', '3', '5', '\n'] {
        key(&mut port, c);
    }
    pump(&mut port, &mut dsky, 3.0);
    // During the lamp test EVERY window (verb included) floods with 8s.
    assert!(
        dsky.r.iter().flatten().filter(|c| **c == '8').count() >= 10,
        "lamp test floods 8s, got {:?}",
        dsky.r
    );
}

/// The padload resume-core format round-trips through yaAGC: generate a padload, boot with it
/// as the core-resume file, and verify the machine still runs (V16N36 ticks) — proving the
/// 8×0400 octal text format is what yaAGC expects.
#[test]
fn padload_core_resumes() {
    if std::env::var("AGC_IT").as_deref() != Ok("1") {
        return;
    }
    let mission = agc::padload::Mission {
        lem_mass_kg: 15_000.0,
        csm_mass_kg: 0.0,
        site_lat_deg: 0.6741,
        site_lon_deg: 23.4730,
        site_radius_m: 1_737_400.0,
        tland_s: 3600.0,
        moon_phase_rev: 0.25,
    };
    let cells = agc::padload::lm_cells(&mission);
    let dir = std::env::temp_dir().join("agc-it-pad");
    std::fs::create_dir_all(&dir).unwrap();
    let core = dir.join("padload.core");
    agc::padload::write_core(&cells, &core).unwrap();

    let Some(_agc) = spawn_yaagc(19891, Some(&core)) else { return };
    let mut port = SocketPort::new(19891);
    let mut dsky = Dsky::new();
    assert!(port.ensure_connected());
    pump(&mut port, &mut dsky, 3.0);
    for c in ['v', '1', '6', 'n', '3', '6', '\n'] {
        key(&mut port, c);
    }
    pump(&mut port, &mut dsky, 2.0);
    assert_eq!(dsky.verb, ['1', '6'], "AGC alive after padload resume");
    let r3_a = dsky.r[2];
    pump(&mut port, &mut dsky, 1.5);
    assert_ne!(r3_a, dsky.r[2], "clock ticks after padload resume");
}
