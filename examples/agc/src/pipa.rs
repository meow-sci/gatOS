//! The PIPA integrator (AGC_PLAN §3.3) — the one equation that must be right:
//!
//! ```text
//! Δv_sensed(CCI) = (v₂ − v₁) − g(r_mid)·dt        g(r) = −μ·r/|r|³
//! Δv_sensed(SM)  = R_sm←cci · Δv_sensed(CCI)
//! pulses_axis    = round_accum(Δv_sensed(SM) / 0.0585 m/s)   (per-axis remainder carried)
//! ```
//!
//! Velocity differencing (not `environment/accel`·dt) is the primary source: it captures
//! thrust, drag and pad contact forces exactly like a physical accelerometer (a landed LM
//! integrates +1 g upward), and cannot drift relative to the game's own integrator.

use crate::ksa_quat::{self, Quat};
use crate::proto::{reg, AgcPort, CounterKind};
use crate::vec3::Vec3;

/// One PIPA pulse = 5.85 cm/s.
pub const PULSE_MS: f64 = 0.0585;

#[derive(Default)]
pub struct Pipa {
    prev: Option<(Vec3, Vec3, f64)>, // (v_cci, r_cci, ut)
    /// Per-axis un-emitted remainder, m/s (quantization never loses ΔV).
    remainder: [f64; 3],
    /// Pulses emitted this tick per axis (status display / rate accounting).
    pub last_pulses: [i32; 3],
}

impl Pipa {
    pub fn new() -> Self {
        Self::default()
    }

    /// Drops integration history (pause/warp/resync boundaries — never integrate across them).
    pub fn reset(&mut self) {
        self.prev = None;
        self.last_pulses = [0; 3];
    }

    /// One telemetry interval: computes sensed ΔV and emits PINC/MINC trains. `q_sm` is the
    /// platform quaternion the counts are expressed on. No-op on the first sample after reset.
    pub fn tick(
        &mut self,
        port: &mut dyn AgcPort,
        v_cci: Vec3,
        r_cci: Vec3,
        ut: f64,
        mu: f64,
        q_sm: Quat,
        operating: bool,
    ) {
        self.last_pulses = [0; 3];
        let Some((v1, r1, t1)) = self.prev.replace((v_cci, r_cci, ut)) else {
            return;
        };
        let dt = ut - t1;
        if !(dt > 0.0) || dt > 5.0 {
            return; // stale/backwards/huge gap — resync path handles it
        }
        if !operating {
            return;
        }
        let r_mid = (r1 + r_cci) * 0.5;
        let rn = r_mid.norm();
        if rn < 1.0 {
            return;
        }
        let g = r_mid * (-mu / (rn * rn * rn));
        let dv_cci = (v_cci - v1) - g * dt;
        let dv_sm = ksa_quat::transform(dv_cci, q_sm.conj());
        for (axis, (&dv, register)) in [dv_sm.x, dv_sm.y, dv_sm.z]
            .iter()
            .zip([reg::PIPAX, reg::PIPAY, reg::PIPAZ])
            .enumerate()
        {
            self.remainder[axis] += dv;
            let pulses = (self.remainder[axis] / PULSE_MS).trunc() as i32;
            if pulses != 0 {
                self.remainder[axis] -= pulses as f64 * PULSE_MS;
                let kind = if pulses > 0 { CounterKind::Pinc } else { CounterKind::Minc };
                for _ in 0..pulses.abs() {
                    port.counter(register, kind);
                }
                self.last_pulses[axis] = pulses;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct CountPort {
        pulses: [i64; 3],
    }
    impl AgcPort for CountPort {
        fn recv(&mut self) -> Option<crate::proto::AgcEvent> {
            None
        }
        fn write_channel(&mut self, _c: u16, _v: u16, _m: Option<u16>) {}
        fn counter(&mut self, register: u8, kind: CounterKind) {
            let axis = (register - reg::PIPAX) as usize;
            self.pulses[axis] += match kind {
                CounterKind::Pinc => 1,
                CounterKind::Minc => -1,
                _ => panic!("PIPA feed must use PINC/MINC"),
            };
        }
        fn connected(&self) -> bool {
            true
        }
    }

    /// Free fall around a body: sensed ΔV ≈ 0, no pulses (gravity is not "felt").
    #[test]
    fn free_fall_emits_nothing() {
        let mut pipa = Pipa::new();
        let mut port = CountPort { pulses: [0; 3] };
        let mu = 4.9048695e12; // Moon
        let r0 = 1_800_000.0_f64;
        let vc = (mu / r0).sqrt(); // circular
        let om = vc / r0;
        let mut t = 0.0;
        for i in 0..200 {
            t = i as f64 * 0.1;
            let a = om * t;
            let r = Vec3::new(r0 * a.cos(), r0 * a.sin(), 0.0);
            let v = Vec3::new(-vc * a.sin(), vc * a.cos(), 0.0);
            pipa.tick(&mut port, v, r, t, mu, Quat::IDENTITY, true);
        }
        let _ = t;
        let total: i64 = port.pulses.iter().map(|p| p.abs()).sum();
        assert!(total <= 2, "free fall should sense ~zero ΔV, got {:?}", port.pulses);
    }

    /// Constant thrust: pulse total equals ΔV/0.0585 regardless of tick size (conservation).
    #[test]
    fn quantization_conserves_delta_v() {
        let mut pipa = Pipa::new();
        let mut port = CountPort { pulses: [0; 3] };
        // Far from the body so gravity ≈ 0; accelerate along +X at 3 m/s² for 20 s.
        let r = Vec3::new(1e12, 0.0, 0.0);
        let accel = 3.0;
        let steps = 800; // 25 ms ticks
        for i in 0..=steps {
            let t = 20.0 * i as f64 / steps as f64;
            let v = Vec3::new(accel * t, 0.0, 0.0);
            pipa.tick(&mut port, v, r, t, 0.0, Quat::IDENTITY, true);
        }
        let want = (accel * 20.0 / PULSE_MS) as i64; // 60/0.0585 ≈ 1025
        assert!(
            (port.pulses[0] - want).abs() <= 1,
            "X pulses {} want {}",
            port.pulses[0],
            want
        );
        assert_eq!(port.pulses[1], 0);
        assert_eq!(port.pulses[2], 0);
    }

    /// A platform rotation moves the sensed axis: thrust along CCI X with the platform rotated
    /// 90° about Z reads on a different PIPA axis, not X.
    #[test]
    fn platform_orientation_selects_axis() {
        let mut pipa = Pipa::new();
        let mut port = CountPort { pulses: [0; 3] };
        let q_sm = crate::ksa_quat::axis_angle(Vec3::z(), std::f64::consts::FRAC_PI_2);
        let r = Vec3::new(1e12, 0.0, 0.0);
        for i in 0..=100 {
            let t = i as f64 * 0.1;
            let v = Vec3::new(2.0 * t, 0.0, 0.0);
            pipa.tick(&mut port, v, r, t, 0.0, q_sm, true);
        }
        assert_eq!(port.pulses[0], 0, "X PIPA must not see the rotated thrust");
        assert!(port.pulses[1].abs() > 300, "rotated thrust lands on Y: {:?}", port.pulses);
    }
}
