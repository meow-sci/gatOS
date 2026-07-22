//! The virtual IMU: one quaternion `q_sm` (stable member → CCI) is the platform truth; the AGC
//! sees it only through CDU gimbal-angle counts, and steers it only through coarse-align bursts
//! (ch 0174-0176, with ch 012 b4 set) and gyro fine-align bursts (ch 0177). AGC_PLAN §3.2.
//!
//! Gimbal kinematics are LM_Simulator's (verified in `Contributed/LM_Simulator/modules/
//! AGC_IMU.tcl` `modify_pipaXYZ`): the stable-member←nav-base matrix is
//! `M_sm←nb = Ry(IGA) · Rz(MGA) · Rx(OGA)` with CDUX = OGA (outer, about NB X), CDUY = IGA
//! (inner, about NB Y), CDUZ = MGA (middle, about NB Z). Extraction below inverts that product;
//! wrong-sign hypotheses die in seconds against the M-B V16N20 tracking check.

use crate::ksa_quat::{self, Quat};
use crate::proto::{reg, AgcPort, CounterKind};
use crate::vec3::Vec3;

/// One CDU count = 360°/2¹⁵ = 39.55 arcsec.
pub const CDU_COUNT_RAD: f64 = std::f64::consts::TAU / 32768.0;
/// One coarse-align pulse = 360°/2¹³ = 0.043948°.
pub const COARSE_PULSE_RAD: f64 = std::f64::consts::TAU / 8192.0;
/// One gyro fine-align pulse = 0.617981 arcsec.
pub const FINE_PULSE_RAD: f64 = 0.617981 / 3600.0 * std::f64::consts::PI / 180.0;
/// Slow-lane CDU FIFO ceiling, counts/s per axis (`agc_engine.c` PushCduFifo).
pub const SLOW_CPS: f64 = 400.0;
/// Fast-lane ceiling (types 021/023).
pub const FAST_CPS: f64 = 6400.0;
/// Body-rate threshold for switching to the fast counter types (AGC_PLAN §1.6.2).
pub const FAST_RATE_RAD: f64 = 4.0_f64 * std::f64::consts::PI / 180.0;

/// Gimbal angles, radians: `(oga, iga, mga)` = CDU X/Y/Z.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Gimbals {
    pub oga: f64,
    pub iga: f64,
    pub mga: f64,
}

pub struct Imu {
    /// Stable member → CCI. The single source of truth (REFSMMAT derives from it).
    pub q_sm: Quat,
    /// CDU counts already emitted to the AGC, per axis (mod 2¹⁵, AGC view).
    emitted: [i32; 3],
    /// ch 012 b5 — while set, CDU emission is suspended and our bookkeeping reads zero.
    pub zero_cdu: bool,
    /// ch 012 b4 — coarse-align enable: 0174-0176 bursts slew the platform.
    pub coarse_enable: bool,
    /// ISS operating (discretes: IMU operate + turn-on delay complete).
    pub operating: bool,
    /// Saturation flag: body rate outran the fast FIFO lane this tick (status display).
    pub saturated: bool,
    /// Optional KSA-body → LM-nav-base remap (default identity; AGC_PLAN §3.1).
    pub body_map: Quat,
}

impl Default for Imu {
    fn default() -> Self {
        Self::new()
    }
}

impl Imu {
    pub fn new() -> Self {
        Self {
            q_sm: Quat::IDENTITY,
            emitted: [0; 3],
            zero_cdu: false,
            coarse_enable: false,
            operating: false,
            saturated: false,
            body_map: Quat::IDENTITY,
        }
    }

    /// The nav-base→SM rotation for the current vehicle attitude (`q` = Body→CCI from `/sim`).
    fn q_nb_to_sm(&self, q_body2cci: Quat) -> Quat {
        // v_sm = transform(v_nb, body_map ∘ q ∘ q_sm⁻¹) — mul() composes left-to-right.
        ksa_quat::mul(ksa_quat::mul(self.body_map, q_body2cci), self.q_sm.conj())
    }

    /// Extracts gimbal angles from the vehicle attitude relative to the platform.
    pub fn gimbals(&self, q_body2cci: Quat) -> Gimbals {
        let q = self.q_nb_to_sm(q_body2cci);
        // Columns of M_sm←nb are the images of the NB axes in SM coordinates.
        let c0 = ksa_quat::transform(Vec3::x(), q);
        let c1 = ksa_quat::transform(Vec3::y(), q);
        let c2 = ksa_quat::transform(Vec3::z(), q);
        // M_sm←nb = Ry(IGA)·Rz(MGA)·Rx(OGA):  M10 = sinMG · M11 = cosOG·cosMG ·
        // M12 = −sinOG·cosMG · M00 = cosMG·cosIG · M20 = −cosMG·sinIG.
        let mga = c0.y.clamp(-1.0, 1.0).asin();
        let oga = (-c2.y).atan2(c1.y);
        let iga = (-c0.z).atan2(c0.x);
        Gimbals { oga, iga, mga }
    }

    /// Rebuilds `q_sm` so the current vehicle attitude reads as gimbal angles `g` — the
    /// coarse-align "platform slaved to command" move (AGC_PLAN §3.2).
    fn set_gimbals(&mut self, q_body2cci: Quat, g: Gimbals) {
        // Build M_sm←nb rows-as-columns from the target angles, get q_rel, then
        // q_sm = (body_map ∘ q) ∘ q_rel⁻¹  (from q_rel = body_map ∘ q ∘ q_sm⁻¹).
        let (so, co) = (g.oga.sin(), g.oga.cos());
        let (si, ci) = (g.iga.sin(), g.iga.cos());
        let (sm, cm) = (g.mga.sin(), g.mga.cos());
        // Columns of M_sm←nb (per the LM_Simulator expansion):
        let c0 = Vec3::new(cm * ci, sm, -cm * si);
        let c1 = Vec3::new(-co * sm * ci + so * si, co * cm, co * sm * si + so * ci);
        let c2 = Vec3::new(so * sm * ci + co * si, -so * cm, -so * sm * si + co * ci);
        // from_rows wants rows r_k = transform(e_k, q_rel) = columns of M_sm←nb.
        let q_rel = ksa_quat::from_rows(c0, c1, c2);
        self.q_sm = ksa_quat::mul(q_rel.conj(), ksa_quat::mul(self.body_map, q_body2cci));
    }

    /// ZERO CDU (ch 012 b5) edge: reset the emitted-count bookkeeping without touching `q_sm`.
    pub fn on_zero_cdu(&mut self, set: bool) {
        self.zero_cdu = set;
        if set {
            self.emitted = [0; 3];
        }
    }

    /// A coarse-align drive burst on 0174/0175/0176 (`axis` 0/1/2), value = `040000·minus|count`.
    /// With coarse-align enabled the platform slews so the commanded gimbal angle change lands;
    /// without it the burst is FDAI error-needle data (ignored).
    pub fn on_cdu_drive(&mut self, axis: usize, burst: u16, q_body2cci: Quat) {
        if !self.coarse_enable {
            return;
        }
        let count = (burst & 0o37777) as f64;
        let signed = if burst & 0o40000 != 0 { -count } else { count };
        let delta = signed * COARSE_PULSE_RAD;
        let mut g = self.gimbals(q_body2cci);
        match axis {
            0 => g.oga += delta,
            1 => g.iga += delta,
            _ => g.mga += delta,
        }
        self.set_gimbals(q_body2cci, g);
    }

    /// A gyro fine-align burst on 0177: value = `((ch014 & 0740) << 6) | count`. Select bits
    /// (A,B) = (0,1)→X, (1,0)→Y, (1,1)→Z; ch014 b9 (→ 040000 here) = negative direction
    /// (LM_Simulator `gyro_fine_align`). Torques the stable member about its own axis.
    pub fn on_gyro_burst(&mut self, value: u16) {
        let count = (value & 0o3777) as f64;
        let minus = value & 0o40000 != 0;
        let sel_a = value & 0o20000 != 0;
        let sel_b = value & 0o10000 != 0;
        let axis = match (sel_a, sel_b) {
            (false, true) => Vec3::x(),
            (true, false) => Vec3::y(),
            (true, true) => Vec3::z(),
            _ => return, // no gyro selected
        };
        let angle = if minus { -count } else { count } * FINE_PULSE_RAD;
        // Rotate the platform about its own (SM-frame) axis: prepend in the SM chain.
        self.q_sm = ksa_quat::mul(ksa_quat::axis_angle(axis, angle), self.q_sm).normalize();
    }

    /// One bridge tick: emit the PCDU/MCDU trains that walk the AGC's CDU counters to the
    /// current gimbal angles. `dt` paces the FIFO lanes; `body_rate` picks slow vs fast types.
    pub fn tick(
        &mut self,
        port: &mut dyn AgcPort,
        q_body2cci: Quat,
        body_rate: f64,
        dt: f64,
    ) -> Gimbals {
        let g = self.gimbals(q_body2cci);
        if !self.operating || self.zero_cdu {
            return g;
        }
        let fast = body_rate >= FAST_RATE_RAD;
        let (plus, minus) = if fast {
            (CounterKind::PcduFast, CounterKind::McduFast)
        } else {
            (CounterKind::Pcdu, CounterKind::Mcdu)
        };
        let budget = ((if fast { FAST_CPS } else { SLOW_CPS }) * dt).max(1.0) as i32;
        self.saturated = false;
        for (axis, (&target_angle, register)) in [g.oga, g.iga, g.mga]
            .iter()
            .zip([reg::CDUX, reg::CDUY, reg::CDUZ])
            .enumerate()
        {
            let target = ((target_angle / CDU_COUNT_RAD).round() as i32).rem_euclid(32768);
            // Shortest path in mod-2¹⁵ counter space.
            let mut delta = (target - self.emitted[axis]).rem_euclid(32768);
            if delta > 16384 {
                delta -= 32768;
            }
            let clamped = delta.clamp(-budget, budget);
            if clamped != delta {
                self.saturated = true;
            }
            let kind = if clamped >= 0 { plus } else { minus };
            for _ in 0..clamped.abs() {
                port.counter(register, kind);
            }
            self.emitted[axis] = (self.emitted[axis] + clamped).rem_euclid(32768);
        }
        g
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ksa_quat::axis_angle;

    struct NullPort {
        counts: Vec<(u8, CounterKind)>,
    }
    impl AgcPort for NullPort {
        fn recv(&mut self) -> Option<crate::proto::AgcEvent> {
            None
        }
        fn write_channel(&mut self, _c: u16, _v: u16, _m: Option<u16>) {}
        fn counter(&mut self, register: u8, kind: CounterKind) {
            self.counts.push((register, kind));
        }
        fn connected(&self) -> bool {
            true
        }
    }

    #[test]
    fn aligned_platform_reads_zero_gimbals() {
        let mut imu = Imu::new();
        let q = axis_angle(Vec3::new(0.3, 0.5, 0.81).normalize(), 0.9);
        imu.q_sm = q; // platform aligned with the body
        let g = imu.gimbals(q);
        assert!(g.oga.abs() < 1e-9 && g.iga.abs() < 1e-9 && g.mga.abs() < 1e-9);
    }

    /// Rotating the BODY about nav-base X (with the platform inertial) must move OGA alone.
    #[test]
    fn body_roll_moves_outer_gimbal() {
        let imu = Imu::new(); // q_sm = identity
        let th = 0.2;
        let q = axis_angle(Vec3::x(), th);
        let g = imu.gimbals(q);
        assert!((g.oga.abs() - th).abs() < 1e-9, "oga {} vs {th}", g.oga);
        assert!(g.iga.abs() < 1e-9 && g.mga.abs() < 1e-9);
    }

    /// Round-trip: set_gimbals(g) then gimbals() reads g back (the coarse-align invariant).
    #[test]
    fn set_gimbals_round_trips() {
        let mut imu = Imu::new();
        let q = axis_angle(Vec3::new(0.1, -0.7, 0.7).normalize(), 1.4);
        let want = Gimbals { oga: 0.5, iga: -0.3, mga: 0.4 };
        imu.set_gimbals(q, want);
        let got = imu.gimbals(q);
        assert!((got.oga - want.oga).abs() < 1e-9);
        assert!((got.iga - want.iga).abs() < 1e-9);
        assert!((got.mga - want.mga).abs() < 1e-9);
    }

    /// Quantization conserves angle: many small ticks emit exactly the counts of the total.
    #[test]
    fn cdu_emission_conserves_counts() {
        let mut imu = Imu::new();
        imu.operating = true;
        let mut port = NullPort { counts: vec![] };
        let total = 0.05_f64; // rad about X
        let steps = 40;
        for i in 1..=steps {
            let q = axis_angle(Vec3::x(), total * i as f64 / steps as f64);
            imu.tick(&mut port, q, 0.01, 0.025);
        }
        let x_counts: i64 = port
            .counts
            .iter()
            .filter(|(r, _)| *r == reg::CDUX)
            .map(|(_, k)| match k {
                CounterKind::Pcdu | CounterKind::PcduFast => 1i64,
                _ => -1,
            })
            .sum();
        let expect = (total / CDU_COUNT_RAD).round() as i64;
        assert!(
            (x_counts - expect).abs() <= 1,
            "emitted {x_counts} vs expected {expect}"
        );
    }

    /// The gyro fine-align burst decode: select bits + sign, small-angle effect on q_sm.
    #[test]
    fn gyro_burst_torques_platform() {
        let mut imu = Imu::new();
        let pulses = 1000u16;
        // X gyro (A=0,B=1), positive.
        imu.on_gyro_burst(0o10000 | pulses);
        let g = imu.gimbals(Quat::IDENTITY);
        // Platform moved +θ about SM X ⇒ the (inertial) body reads −θ on the outer gimbal.
        let want = pulses as f64 * FINE_PULSE_RAD;
        assert!((g.oga + want).abs() < 1e-9, "oga {} want {}", g.oga, -want);
        assert!(g.iga.abs() < 1e-12 && g.mga.abs() < 1e-12);
    }
}
