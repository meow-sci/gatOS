//! The landing-radar model (AGC_PLAN §4.4, A5): antenna position state machine, beam geometry,
//! the real Luminary099 scale factors, data-good discretes, and the SHINC/SHANC delivery race.
//!
//! Scale factors come from `Luminary099/CONTROLLED_CONSTANTS.agc` (not guessed):
//! - `HSCAL`  — range **1.079 ft/bit** (low scale; ch 033 b9 set); high scale = ×5 (5.395).
//! - `VXSCAL/VYSCAL/VZSCAL` — velocity **−0.644 / +1.212 / +0.8668 ft/s/bit**, with
//!   `LVELBIAS = −12288` (the AGC computes v = scale·(count − 12288), so we write
//!   count = 12288 + v/|scale| with the sign folded into the axis).
//!
//! Select code on ch 013 b1-3 (`RADAR A/B/C`, code = A·4 + B·2 + C): LR VX=4, VY=5, VZ=6,
//! range=7 (RR range-rate=1, range=2 — out of scope). [impl-verify at the A5 in-game pass —
//! the N63-vs-`/sim`-truth telemetry on the status panel catches a wrong table immediately.]

use crate::ksa_quat::{self, Quat};
use crate::proto::{chan, reg, AgcPort, CounterKind};
use crate::vec3::Vec3;

const FT: f64 = 0.3048;
/// Range low scale, ft/bit (`HSCAL`).
pub const RANGE_LOW_FT_PER_BIT: f64 = 1.079;
/// High-scale multiplier (low-scale bit 033 b9 clear above the threshold).
pub const RANGE_HIGH_MULT: f64 = 5.0;
/// The low/high scale switch altitude, m (~2,500 ft — the real LR threshold).
pub const LOW_SCALE_BELOW_M: f64 = 2500.0 * FT;
/// Velocity scales, ft/s per bit (sign = beam sense; `VXSCAL/VYSCAL/VZSCAL`).
pub const VEL_SCALE_FT: [f64; 3] = [-0.644, 1.212, 0.8668];
/// `LVELBIAS` — velocity words are biased by 12288 counts.
pub const VEL_BIAS: i32 = 12288;
/// Max altitude for data-good (`LRHMAX` padload = 50,000 ft).
pub const MAX_ALT_M: f64 = 50_000.0 * FT;
/// Antenna reposition time, s.
pub const SLEW_SECS: f64 = 2.0;

/// LR antenna tilt angles, radians (must agree with the padload's LRALPHA/LRBETA cells:
/// position 1 = 6°/24°, position 2 = 6°/0° — `Luminary069/PADLOADS.agc` values).
pub const POS1_ALPHA: f64 = 6.0 * std::f64::consts::PI / 180.0;
pub const POS1_BETA: f64 = 24.0 * std::f64::consts::PI / 180.0;
pub const POS2_ALPHA: f64 = 6.0 * std::f64::consts::PI / 180.0;
pub const POS2_BETA: f64 = 0.0;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Antenna {
    Pos1,
    Slewing(u8 /* remaining ticks estimate */),
    Pos2,
}

pub struct Radar {
    pub antenna: Antenna,
    slew_left: f64,
    /// Radar power switch (`/run/agc/switches/lr_power`).
    pub powered: bool,
    /// Last delivered word + select code (status display).
    pub last: Option<(u8, u16)>,
    /// Data-good state mirrored to ch 033 (b5 range good, b8 vel good, b9 low scale).
    pub range_good: bool,
    pub vel_good: bool,
    pub low_scale: bool,
}

impl Default for Radar {
    fn default() -> Self {
        Self::new()
    }
}

impl Radar {
    pub fn new() -> Self {
        Self {
            antenna: Antenna::Pos1,
            slew_left: 0.0,
            powered: true,
            last: None,
            range_good: false,
            vel_good: false,
            low_scale: false,
        }
    }

    /// ch 012 b13 — LR position 2 command (edge-triggered slew).
    pub fn on_pos2_command(&mut self, commanded: bool) {
        if commanded && self.antenna == Antenna::Pos1 {
            self.antenna = Antenna::Slewing(0);
            self.slew_left = SLEW_SECS;
        }
    }

    /// The antenna → LM-body rotation for the current position: tilt `alpha` about body X,
    /// then `beta` about body Y [impl-verify axis order vs the GSOP antenna definition — the
    /// padload LRALPHA/LRBETA cells use the same convention either way, so N63 agreement is
    /// the arbiter].
    fn antenna_to_body(&self) -> Quat {
        let (alpha, beta) = match self.antenna {
            Antenna::Pos2 => (POS2_ALPHA, POS2_BETA),
            _ => (POS1_ALPHA, POS1_BETA),
        };
        ksa_quat::mul(
            ksa_quat::axis_angle(Vec3::x(), alpha),
            ksa_quat::axis_angle(Vec3::y(), beta),
        )
    }

    /// One bridge tick: advance the slew, refresh data-good + the ch 033 mirror.
    /// `alt_radar` = `/sim` terrain altitude (m); `v_surf_body` = surface-relative velocity in
    /// the KSA body frame (m/s).
    pub fn tick(&mut self, port: &mut dyn AgcPort, dt: f64, alt_radar: f64, v_surf_body: Vec3) {
        if let Antenna::Slewing(_) = self.antenna {
            self.slew_left -= dt;
            if self.slew_left <= 0.0 {
                self.antenna = Antenna::Pos2;
            }
        }
        let in_limits = self.powered && alt_radar > 3.0 && alt_radar < MAX_ALT_M;
        self.range_good = in_limits;
        self.vel_good = in_limits && v_surf_body.norm() < 2000.0 * FT;
        self.low_scale = alt_radar < LOW_SCALE_BELOW_M;

        // ch 033 is active-low ("0 = signal present") and b11-15 are latched internally —
        // we mask-write only the LR bits: b5 range good, b6 pos1, b7 pos2, b8 vel good,
        // b9 range low scale.
        let mut value = 0u16;
        let bit = |n: u16| 1u16 << (n - 1);
        if !self.range_good {
            value |= bit(5);
        }
        if self.antenna != Antenna::Pos1 {
            value |= bit(6);
        }
        if self.antenna != Antenna::Pos2 {
            value |= bit(7);
        }
        if !self.vel_good {
            value |= bit(8);
        }
        if !self.low_scale {
            value |= bit(9);
        }
        let mask = bit(5) | bit(6) | bit(7) | bit(8) | bit(9);
        port.write_channel(chan::CHAN33, value, Some(mask));
    }

    /// Quantizes all four LR words (select codes 4..7 → indices 0..3) for the current antenna
    /// position — shared by the extern SHINC race and the embedded `RequestRadarData` hook.
    pub fn words(&self, alt_radar: f64, v_surf_body: Vec3) -> [u16; 4] {
        let q_ab = self.antenna_to_body();
        let v_ant = ksa_quat::transform(v_surf_body, q_ab.conj());
        let mut out = [0u16; 4];
        for (i, (comp, scale)) in [v_ant.x, v_ant.y, v_ant.z].iter().zip(VEL_SCALE_FT).enumerate() {
            let counts = VEL_BIAS + (comp / FT / scale) as i32;
            out[i] = counts.clamp(0, 0x7FFF) as u16;
        }
        // Slant range along the range beam: altitude / cos(beam vs local up). We use the
        // antenna boresight tilted by the mount — slant = alt / max(cos(tilt), 0.2)
        // [impl-verify beam vector (HBEAMANT) at the A5 in-game pass].
        let tilt = match self.antenna {
            Antenna::Pos2 => POS2_BETA,
            _ => POS1_BETA,
        };
        let slant_ft = alt_radar / FT / tilt.cos().max(0.2);
        let scale = if self.low_scale {
            RANGE_LOW_FT_PER_BIT
        } else {
            RANGE_LOW_FT_PER_BIT * RANGE_HIGH_MULT
        };
        out[3] = ((slant_ft / scale) as i32).clamp(0, 0x7FFF) as u16;
        out
    }

    /// Answers a radar gate: ch 013 showed activity (b4) with select code `code` — deliver the
    /// word into RNRAD as 15 SHINC/SHANC pulses, MSB-first, inside the 9-gate window.
    /// Returns the delivered word.
    pub fn deliver(
        &mut self,
        port: &mut dyn AgcPort,
        code: u8,
        alt_radar: f64,
        v_surf_body: Vec3,
    ) -> Option<u16> {
        if !self.powered || !(4..=7).contains(&code) {
            return None; // RR codes — out of scope (plan non-goal)
        }
        let word = self.words(alt_radar, v_surf_body)[(code - 4) as usize];
        // MSB-first, 15 bits: SHANC shifts in a 1, SHINC a 0.
        for i in (0..15).rev() {
            let kind = if word & (1 << i) != 0 { CounterKind::Shanc } else { CounterKind::Shinc };
            port.counter(reg::RNRAD, kind);
        }
        self.last = Some((code, word));
        Some(word)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Rec {
        bits: Vec<u8>,
        ch33: Option<(u16, Option<u16>)>,
    }
    impl AgcPort for Rec {
        fn recv(&mut self) -> Option<crate::proto::AgcEvent> {
            None
        }
        fn write_channel(&mut self, c: u16, v: u16, m: Option<u16>) {
            if c == chan::CHAN33 {
                self.ch33 = Some((v, m));
            }
        }
        fn counter(&mut self, register: u8, kind: CounterKind) {
            assert_eq!(register, reg::RNRAD);
            self.bits.push(match kind {
                CounterKind::Shanc => 1,
                CounterKind::Shinc => 0,
                _ => panic!("radar word must shift"),
            });
        }
        fn connected(&self) -> bool {
            true
        }
    }

    /// The shift train reassembles to the word, MSB-first.
    #[test]
    fn word_shifts_msb_first() {
        let mut r = Radar::new();
        let mut p = Rec { bits: vec![], ch33: None };
        // 10,000 ft altitude → high scale (5.395 ft/bit).
        let w = r.deliver(&mut p, 7, 10_000.0 * FT, Vec3::ZERO).unwrap();
        assert_eq!(p.bits.len(), 15);
        let rebuilt = p.bits.iter().fold(0u16, |acc, &b| (acc << 1) | b as u16);
        assert_eq!(rebuilt, w);
        // Sanity: slant ≈ 10000/cos(24°) ft over 5.395 ft/bit ≈ 2029 counts.
        let expect = (10_000.0 / POS1_BETA.cos() / (RANGE_LOW_FT_PER_BIT * RANGE_HIGH_MULT)) as i32;
        assert!((w as i32 - expect).abs() <= 1, "range word {w} vs {expect}");
    }

    /// Velocity words carry the 12288 bias and the per-beam scale sign.
    #[test]
    fn velocity_word_biased_and_scaled() {
        let mut r = Radar::new();
        r.antenna = Antenna::Pos2;
        let mut p = Rec { bits: vec![], ch33: None };
        // 100 ft/s along body X (with pos2 alpha=6° tilt, X_ant ≈ X_body).
        let w = r.deliver(&mut p, 4, 1000.0, Vec3::new(100.0 * FT, 0.0, 0.0)).unwrap() as i32;
        let expect = VEL_BIAS + (100.0 * POS2_ALPHA.cos() / VEL_SCALE_FT[0]) as i32;
        assert!((w - expect).abs() <= 2, "vx word {w} vs {expect}");
    }

    /// Antenna slews to POS2 on the ch 012 b13 command, reporting through ch 033 b6/b7.
    #[test]
    fn antenna_slew_state_machine() {
        let mut r = Radar::new();
        let mut p = Rec { bits: vec![], ch33: None };
        r.on_pos2_command(true);
        assert!(matches!(r.antenna, Antenna::Slewing(_)));
        for _ in 0..100 {
            r.tick(&mut p, 0.025, 1000.0, Vec3::ZERO);
        }
        assert_eq!(r.antenna, Antenna::Pos2);
        let (v, m) = p.ch33.unwrap();
        let bit = |n: u16| 1u16 << (n - 1);
        assert_eq!(m.unwrap() & (bit(6) | bit(7)), bit(6) | bit(7));
        assert_eq!(v & bit(7), 0, "pos2 present = active-low 0");
        assert_ne!(v & bit(6), 0, "pos1 absent = 1");
    }

    /// Data-good + low-scale thresholds.
    #[test]
    fn data_good_thresholds() {
        let mut r = Radar::new();
        let mut p = Rec { bits: vec![], ch33: None };
        r.tick(&mut p, 0.025, 100.0, Vec3::ZERO); // 100 m — low scale, good
        assert!(r.range_good && r.low_scale);
        r.tick(&mut p, 0.025, 10_000.0, Vec3::ZERO); // 10 km — high scale, good (< 50k ft)
        assert!(r.range_good && !r.low_scale);
        r.tick(&mut p, 0.025, 20_000.0, Vec3::ZERO); // above LRHMAX (15,240 m)
        assert!(!r.range_good);
    }
}
