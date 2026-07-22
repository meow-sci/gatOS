//! RCS demodulation (AGC_PLAN §4.6, D-A7): the DAP fires 16 individual jets on ch 005/006 with
//! millisecond timing; KSA's `/sim` offers signs-only bang-bang (`ctl/rotate` + `ctl/translate`,
//! Frame phase, ~tick resolution). The bridge integrates each jet's ON time between ticks,
//! composes per-axis duty cycles, and **sigma-delta modulates** them into sign writes so the
//! average torque/force matches the DAP's intent even below tick resolution.
//!
//! Jet tables (LM_Simulator decode + LM geometry; AGC_PLAN §1.6.1):
//! ch 005 b1-8 = Q4U,Q4D,Q3U,Q3D,Q2U,Q2D,Q1U,Q1D (the 8 vertical jets)
//! ch 006 b1-8 = Q3A,Q4F,Q1F,Q2A,Q2L,Q3R,Q4R,Q1L (the 8 horizontal jets)
//! Axis composition: nv=(Q2D+Q4U)−(Q2U+Q4D), nu=(Q1D+Q3U)−(Q1U+Q3D);
//! pitch ∝ (nu−nv), roll ∝ (nu+nv), yaw ∝ (Q1F+Q2L+Q3A+Q4R)−(Q1L+Q2A+Q3R+Q4F);
//! ±X translation = net vertical, ±Y = L−R jets, ±Z = A−F jets.
//! [impl-verify signs at the A4 in-game pass — the DAP visibly fighting itself is the tell.]

/// Per-axis duty in [-1, 1]: rotation (roll, pitch, yaw) + translation (x, y, z), LM body.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct AxisDuty {
    pub rot: [f64; 3],
    pub tra: [f64; 3],
}

/// The signs the sigma-delta emitted this tick (what goes into `ctl/batch`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Signs {
    pub rot: [i8; 3],
    pub tra: [i8; 3],
}

pub struct Rcs {
    /// Jet bit states as last seen: (ch5, ch6).
    bits: (u16, u16),
    /// Monotonic time of the last bit change / tick, s.
    last_t: f64,
    /// Per-jet ON-time integrals since the last tick, s (ch5 jets 0-7, ch6 jets 8-15).
    on_time: [f64; 16],
    /// Sigma-delta accumulators per axis.
    acc: AxisDuty,
    /// The last emitted signs (skip identical batch writes).
    pub last_signs: Signs,
}

impl Default for Rcs {
    fn default() -> Self {
        Self::new()
    }
}

impl Rcs {
    pub fn new() -> Self {
        Self {
            bits: (0, 0),
            last_t: 0.0,
            on_time: [0.0; 16],
            acc: AxisDuty::default(),
            last_signs: Signs::default(),
        }
    }

    fn integrate_to(&mut self, t: f64) {
        let dt = (t - self.last_t).max(0.0);
        for j in 0..8 {
            if self.bits.0 & (1 << j) != 0 {
                self.on_time[j] += dt;
            }
            if self.bits.1 & (1 << j) != 0 {
                self.on_time[8 + j] += dt;
            }
        }
        self.last_t = t;
    }

    /// A ch 005 or ch 006 write arrived at monotonic time `t`.
    pub fn on_jets(&mut self, channel: u16, value: u16, t: f64) {
        self.integrate_to(t);
        if channel == 0o5 {
            self.bits.0 = value;
        } else {
            self.bits.1 = value;
        }
    }

    /// All-jets-off (the hardware-restart channel clear).
    pub fn all_off(&mut self, t: f64) {
        self.integrate_to(t);
        self.bits = (0, 0);
    }

    /// One bridge tick at monotonic time `t` over interval `dt`: fold jet on-times into axis
    /// duties, run the sigma-delta, and return the signs to write (None = unchanged).
    pub fn tick(&mut self, t: f64, dt: f64) -> Option<Signs> {
        self.integrate_to(t);
        let d = |i: usize| self.on_time[i] / dt.max(1e-6);
        // ch 005 jet indices: b1..b8 = Q4U(0),Q4D(1),Q3U(2),Q3D(3),Q2U(4),Q2D(5),Q1U(6),Q1D(7)
        let (q4u, q4d, q3u, q3d, q2u, q2d, q1u, q1d) =
            (d(0), d(1), d(2), d(3), d(4), d(5), d(6), d(7));
        // ch 006: b1..b8 = Q3A(8),Q4F(9),Q1F(10),Q2A(11),Q2L(12),Q3R(13),Q4R(14),Q1L(15)
        let (q3a, q4f, q1f, q2a, q2l, q3r, q4r, q1l) =
            (d(8), d(9), d(10), d(11), d(12), d(13), d(14), d(15));
        self.on_time = [0.0; 16];

        let nv = (q2d + q4u) - (q2u + q4d);
        let nu = (q1d + q3u) - (q1u + q3d);
        let duty = AxisDuty {
            rot: [
                (nu + nv) * 0.5,                                     // roll
                (nu - nv) * 0.5,                                     // pitch
                ((q1f + q2l + q3a + q4r) - (q1l + q2a + q3r + q4f)) * 0.25, // yaw
            ],
            tra: [
                ((q1u + q2u + q3u + q4u) - (q1d + q2d + q3d + q4d)) * 0.25, // +X = up jets
                ((q1l + q2l) - (q3r + q4r)) * 0.5,                   // +Y = left-firing jets
                ((q3a + q2a) - (q4f + q1f)) * 0.5,                   // +Z = aft-firing jets
            ],
        };

        // Sigma-delta: accumulate duty each tick; emit a sign when the accumulator crosses
        // ±0.5 of a tick's worth, subtracting what the emitted bang-bang delivers.
        let mut signs = Signs::default();
        for a in 0..3 {
            signs.rot[a] = Self::sd(&mut self.acc.rot[a], duty.rot[a]);
            signs.tra[a] = Self::sd(&mut self.acc.tra[a], duty.tra[a]);
        }
        if signs == self.last_signs {
            return None;
        }
        self.last_signs = signs;
        Some(signs)
    }

    fn sd(acc: &mut f64, duty: f64) -> i8 {
        *acc += duty;
        // Strict thresholds: a +0.5 duty stream must alternate {+1, 0}, never ring −1.
        if *acc > 0.5 {
            *acc -= 1.0;
            1
        } else if *acc < -0.5 {
            *acc += 1.0;
            -1
        } else {
            0
        }
    }
}

/// Renders the batch lines for a signs write (KSA body = LM body under the identity body_map:
/// +x thrust/nose, +y right, +z down ≡ LM forward).
pub fn batch_lines(s: Signs) -> Vec<String> {
    vec![
        format!("rotate {} {} {}", s.rot[0], s.rot[1], s.rot[2]),
        format!("translate {} {} {}", s.tra[0], s.tra[1], s.tra[2]),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A jet held ON the whole interval yields full duty and a persistent sign.
    #[test]
    fn full_on_yaw_gives_full_duty() {
        let mut r = Rcs::new();
        // All four +yaw jets on (Q1F b3, Q2L b5, Q3A b1, Q4R b7 of ch 006).
        let v = (1 << 2) | (1 << 4) | (1 << 0) | (1 << 6);
        r.on_jets(0o6, v, 0.0);
        let s = r.tick(0.025, 0.025).unwrap();
        assert_eq!(s.rot[2], 1, "+yaw sign");
        // Next tick still on: the sigma-delta keeps emitting +1 (unchanged → None).
        assert!(r.tick(0.050, 0.025).is_none());
        assert_eq!(r.last_signs.rot[2], 1);
    }

    /// A 50% duty cycle averages to alternating signs ≈ half the ticks firing.
    #[test]
    fn half_duty_alternates() {
        let mut r = Rcs::new();
        let mut fired = 0;
        let mut t = 0.0;
        let dt = 0.025;
        // All four U jets (pure +X translation, no net torque) on for half of each tick.
        let all_u = (1 << 0) | (1 << 2) | (1 << 4) | (1 << 6);
        for i in 0..100 {
            r.on_jets(0o5, all_u, t);
            r.on_jets(0o5, 0, t + dt / 2.0);
            t = (i + 1) as f64 * dt;
            if let Some(s) = r.tick(t, dt) {
                if s.tra[0] > 0 {
                    fired += 1;
                }
            } else if r.last_signs.tra[0] > 0 {
                fired += 1;
            }
        }
        assert!(
            (30..=70).contains(&fired),
            "≈50% of 100 ticks should fire +x, got {fired}"
        );
    }

    /// Opposed pairs cancel rotation and produce pure translation.
    #[test]
    fn opposed_pairs_translate_without_torque() {
        let mut r = Rcs::new();
        // All four U jets: pure +X translation, no roll/pitch (nu = nv = 0 net... actually
        // nu = Q3U - Q1U = 0 with all four on; nv = Q4U - Q2U = 0).
        let v = (1 << 0) | (1 << 2) | (1 << 4) | (1 << 6); // Q4U,Q3U,Q2U,Q1U
        r.on_jets(0o5, v, 0.0);
        let s = r.tick(0.025, 0.025).unwrap();
        assert_eq!(s.rot, [0, 0, 0]);
        assert_eq!(s.tra[0], 1);
    }

    #[test]
    fn batch_lines_shape() {
        let s = Signs { rot: [1, 0, -1], tra: [0, 0, 0] };
        assert_eq!(batch_lines(s), vec!["rotate 1 0 -1".to_string(), "translate 0 0 0".to_string()]);
    }
}
