//! The ch 030–033 discrete state machine + the `/run/agc/switches/<name>` cockpit files
//! (AGC_PLAN §4.5). All bits in channels 30–33 are **inverted as sensed by the program** —
//! 0 = signal present (`INPUT_OUTPUT_CHANNEL_BIT_DESCRIPTIONS.agc:148`). Writes are masked so
//! bits owned elsewhere (PRO = ch 032 b14, owned by the DSKY; LR bits of ch 033, owned by the
//! radar model) are never stomped.
//!
//! Switch files are `0`/`1` text, polled ~10 Hz — busybox-scriptable:
//! `echo 1 > /run/agc/switches/eng_arm_desc`. Defaults land a flyable ship while keeping the
//! switches real for procedure nerds.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use crate::proto::{chan, AgcPort};

/// The ISS turn-on delay: request → 90 s → operate (the authentic wait; Luminary answers the
/// request with ch 012 b15 "ISS turn-on delay complete").
pub const ISS_TURNON_SECS: f64 = 90.0;

const fn bit(n: u16) -> u16 {
    1 << (n - 1)
}

/// The cockpit switches (file name, default). Missing files read as their default.
pub const SWITCHES: &[(&str, bool)] = &[
    ("eng_arm_desc", true),   // ch 030 b3 (descent half)
    ("eng_arm_asc", false),   // ch 030 b3 (ascent half — either arms)
    ("abort", false),         // ch 030 b1
    ("abort_stage", false),   // ch 030 b4
    ("auto_throttle", true),  // ch 030 b5
    ("imu_operate", true),    // ch 030 b9 (behind the turn-on sequence)
    ("agc_has_control", true),// ch 030 b10
    ("mode_auto", true),      // ch 031 b14 (PGNS AUTO)
    ("mode_hold", false),     // ch 031 b13 (ATT HOLD)
    ("lr_power", true),       // radar model gate
    ("uplink_block", false),  // ch 033 b10
];

pub struct Discretes {
    dir: PathBuf,
    /// Current switch view.
    pub state: HashMap<&'static str, bool>,
    /// ISS sequencing: time the turn-on request started; None = not requested.
    turnon_started: Option<f64>,
    /// True once the 90 s delay has elapsed (drives IMU operate + ch 030 b9/b14).
    pub iss_operating: bool,
    /// Last written channel values (write-on-change).
    last: HashMap<u16, u16>,
}

impl Discretes {
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        let mut state = HashMap::new();
        for (name, default) in SWITCHES {
            state.insert(*name, *default);
        }
        Self {
            dir: dir.into(),
            state,
            turnon_started: None,
            iss_operating: false,
            last: HashMap::new(),
        }
    }

    pub fn get(&self, name: &str) -> bool {
        self.state.get(name).copied().unwrap_or(false)
    }

    /// True when either engine-arm switch is up (the ignite gate `engines.rs` consumes).
    pub fn engine_armed(&self) -> bool {
        self.get("eng_arm_desc") || self.get("eng_arm_asc")
    }

    fn read_switch(dir: &Path, name: &str, default: bool) -> bool {
        match std::fs::read_to_string(dir.join(name)) {
            Ok(s) => s.trim() == "1",
            Err(_) => default,
        }
    }

    /// One tick at monotonic time `t`: re-read the switch files, run the ISS turn-on sequence,
    /// and push changed ch 030/031/032/033 values (masked to the bits this module owns).
    pub fn tick(&mut self, port: &mut dyn AgcPort, t: f64) {
        for (name, default) in SWITCHES {
            let v = Self::read_switch(&self.dir, name, *default);
            self.state.insert(*name, v);
        }

        // ISS turn-on: the operate switch starts the 90 s delay once; flipping it off resets.
        if self.get("imu_operate") {
            match self.turnon_started {
                None => {
                    self.turnon_started = Some(t);
                    self.iss_operating = false;
                }
                Some(t0) => {
                    if !self.iss_operating && t - t0 >= ISS_TURNON_SECS {
                        self.iss_operating = true;
                    }
                }
            }
        } else {
            self.turnon_started = None;
            self.iss_operating = false;
        }

        // ---- ch 030 (active-low: 0 = present) ----
        let mut v30 = 0u16;
        let mut m30 = 0u16;
        for (b, present) in [
            (1, self.get("abort")),
            (3, self.engine_armed()),
            (4, self.get("abort_stage")),
            (5, self.get("auto_throttle")),
            (9, self.iss_operating),
            (10, self.get("agc_has_control")),
            // b11 IMU cage: never (we don't model caging).
            (11, false),
            // b12/b13 IMU CDU fail / IMU fail: healthy = absent = 1.
            (12, false),
            (13, false),
            // b14 ISS turn-on requested: present while the delay is running.
            (14, self.get("imu_operate") && !self.iss_operating),
            // b15 SM temp in limits: always present (perfect IMU policy).
            (15, true),
        ] {
            m30 |= bit(b);
            if !present {
                v30 |= bit(b);
            }
        }
        self.write_if_changed(port, chan::CHAN30, v30, m30);

        // ---- ch 031: mode switches (b13 att hold, b14 auto stab; both 1 = DAP off).
        // ACA/THC pulses (b1-12) belong to the panel's nudge path; we own the mode bits + b15.
        let mut v31 = 0u16;
        let m31 = bit(13) | bit(14) | bit(15);
        if !self.get("mode_hold") {
            v31 |= bit(13);
        }
        if !self.get("mode_auto") {
            v31 |= bit(14);
        }
        v31 |= bit(15); // ACA in detent (no manual stick) = signal absent = 1
        self.write_if_changed(port, chan::CHAN31, v31, m31);

        // ---- ch 032: thruster-pair disables (b1-8) + descent-engine crew disable (b9) —
        // none disabled ⇒ all absent ⇒ 1s. b14 (PRO) is the DSKY's; excluded from the mask.
        let m32 = 0o777; // b1-9
        self.write_if_changed(port, chan::CHAN32, m32, m32);

        // ---- ch 033: b10 uplink block; the LR bits are the radar model's.
        let m33 = bit(10);
        let v33 = if self.get("uplink_block") { 0 } else { bit(10) };
        self.write_if_changed(port, chan::CHAN33, v33, m33);
    }

    fn write_if_changed(&mut self, port: &mut dyn AgcPort, ch: u16, value: u16, mask: u16) {
        if self.last.get(&ch) != Some(&value) {
            self.last.insert(ch, value);
            port.write_channel(ch, value, Some(mask));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Rec {
        writes: Vec<(u16, u16, Option<u16>)>,
    }
    impl AgcPort for Rec {
        fn recv(&mut self) -> Option<crate::proto::AgcEvent> {
            None
        }
        fn write_channel(&mut self, c: u16, v: u16, m: Option<u16>) {
            self.writes.push((c, v, m));
        }
        fn counter(&mut self, _r: u8, _k: crate::proto::CounterKind) {}
        fn connected(&self) -> bool {
            true
        }
    }

    fn tmpdir() -> PathBuf {
        let d = std::env::temp_dir().join(format!("agc-sw-{}-{}", std::process::id(), rand_tag()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }
    fn rand_tag() -> u64 {
        use std::time::{SystemTime, UNIX_EPOCH};
        SystemTime::now().duration_since(UNIX_EPOCH).unwrap().subsec_nanos() as u64
    }

    #[test]
    fn iss_turnon_takes_90_seconds() {
        let dir = tmpdir();
        let mut d = Discretes::new(&dir);
        let mut p = Rec { writes: vec![] };
        d.tick(&mut p, 0.0);
        assert!(!d.iss_operating);
        d.tick(&mut p, 89.0);
        assert!(!d.iss_operating, "still in the turn-on delay");
        d.tick(&mut p, 90.5);
        assert!(d.iss_operating, "delay elapsed");
        // ch 030: b9 present (0) once operating; b14 request absent (1).
        let (_, v, m) = *p.writes.iter().filter(|w| w.0 == chan::CHAN30).last().unwrap();
        assert_eq!(v & bit(9), 0, "IMU operate present = 0 (active-low)");
        assert_ne!(v & bit(14), 0, "turn-on request cleared");
        assert_ne!(m.unwrap() & bit(15), 0);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn switch_file_overrides_default() {
        let dir = tmpdir();
        std::fs::write(dir.join("auto_throttle"), "0\n").unwrap();
        let mut d = Discretes::new(&dir);
        let mut p = Rec { writes: vec![] };
        d.tick(&mut p, 0.0);
        let (_, v, _) = *p.writes.iter().find(|w| w.0 == chan::CHAN30).unwrap();
        assert_ne!(v & bit(5), 0, "auto throttle off ⇒ signal absent ⇒ 1");
        // Flip it back on; write-on-change emits a new value.
        std::fs::write(dir.join("auto_throttle"), "1\n").unwrap();
        d.tick(&mut p, 0.2);
        let (_, v2, _) = *p.writes.iter().filter(|w| w.0 == chan::CHAN30).last().unwrap();
        assert_eq!(v2 & bit(5), 0);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn pro_bit_never_in_ch32_mask() {
        let dir = tmpdir();
        let mut d = Discretes::new(&dir);
        let mut p = Rec { writes: vec![] };
        d.tick(&mut p, 0.0);
        let (_, _, m) = *p.writes.iter().find(|w| w.0 == chan::CHAN32).unwrap();
        assert_eq!(m.unwrap() & bit(14), 0, "PRO belongs to the DSKY client");
        std::fs::remove_dir_all(&dir).ok();
    }
}
