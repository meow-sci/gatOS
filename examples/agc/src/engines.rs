//! Engine actuation (AGC_PLAN §4.6, D-A7): ch 011 b13/b14 → `ctl/ignite`/`ctl/shutdown` (gated
//! by the ENGINE ARM switch), and the client-clocked THRUST loop — when Luminary loads the
//! THRUST counter and raises ch 014 b4 (drive activity), *we* clock it with DINC pulses; every
//! POUT/MOUT echo is one throttle up/down pulse, ZOUT means drained.

use crate::proto::{reg, AgcPort, CounterKind};

/// DPS full thrust, lbf (`FDPS`, CONTROLLED_CONSTANTS.agc).
pub const F_FULL_LBF_DEFAULT: f64 = 9817.5;
/// Commanded-thrust weight of one THRUST pulse, lbf (AGC_PLAN §1.6.4; config-calibrated at the
/// A4 in-game pass — the N40-vs-`/sim` cross-check makes miscalibration obvious).
pub const LBF_PER_PULSE_DEFAULT: f64 = 2.7;
/// The DPS soft floor the AGC assumes; `install` sets the engine's `min_throttle` to match.
pub const MIN_THROTTLE: f64 = 0.10;
/// DINCs sent per bridge tick while draining (paced well under the interlace ceiling).
const DINC_PER_TICK: u32 = 40;

/// What the bridge should do to `/sim` this tick.
#[derive(Debug, Default, PartialEq)]
pub struct EngineCommands {
    pub ignite: bool,
    pub shutdown: bool,
    /// New `ctl/throttle` value when it moved more than ε.
    pub throttle: Option<f64>,
}

pub struct Engines {
    /// ch 011 b13/b14 as last seen.
    engine_on_bit: bool,
    engine_off_bit: bool,
    /// ch 014 b4 — THRUST drive activity.
    pub drive_active: bool,
    /// Net accumulated throttle pulses (POUT − MOUT since start).
    pub pulses: i64,
    draining: bool,
    last_throttle: f64,
    pub f_full_lbf: f64,
    pub lbf_per_pulse: f64,
}

impl Default for Engines {
    fn default() -> Self {
        Self::new()
    }
}

impl Engines {
    pub fn new() -> Self {
        Self {
            engine_on_bit: false,
            engine_off_bit: false,
            drive_active: false,
            pulses: 0,
            draining: false,
            last_throttle: MIN_THROTTLE,
            f_full_lbf: F_FULL_LBF_DEFAULT,
            lbf_per_pulse: LBF_PER_PULSE_DEFAULT,
        }
    }

    /// ch 011 update. Returns ignite/shutdown edges (arm gating applied by the caller's
    /// discretes state — an unarmed engine ignores the ON command, exactly like the real
    /// ENG ARM breaker).
    pub fn on_dsalmout(&mut self, value: u16, armed: bool) -> EngineCommands {
        let on = value & (1 << 12) != 0; // b13
        let off = value & (1 << 13) != 0; // b14
        let mut out = EngineCommands::default();
        if on && !self.engine_on_bit && armed {
            out.ignite = true;
        }
        if off && !self.engine_off_bit {
            out.shutdown = true;
        }
        self.engine_on_bit = on;
        self.engine_off_bit = off;
        out
    }

    /// ch 014 update: bit 4 = thrust drive activity — start/stop the DINC clocking loop.
    pub fn on_chan14(&mut self, value: u16) {
        let active = value & (1 << 3) != 0;
        if active && !self.drive_active {
            self.draining = true;
        }
        self.drive_active = active;
    }

    /// A counter echo on 0255: POUT (+1 throttle pulse), MOUT (−1), ZOUT (drained — stop).
    pub fn on_thrust_echo(&mut self, pulse: u16) {
        match pulse {
            0o15 => self.pulses += 1,
            0o16 => self.pulses -= 1,
            0o17 => self.draining = false,
            _ => {}
        }
    }

    /// One bridge tick: clock the THRUST counter while a drain is pending, then derive the
    /// commanded throttle fraction = clamp(0.10 + pulses·k/F_full, 0.10, 1.0).
    pub fn tick(&mut self, port: &mut dyn AgcPort) -> EngineCommands {
        if self.draining && self.drive_active {
            for _ in 0..DINC_PER_TICK {
                port.counter(reg::THRUST, CounterKind::Dinc);
            }
        }
        let frac = (MIN_THROTTLE + self.pulses as f64 * self.lbf_per_pulse / self.f_full_lbf)
            .clamp(MIN_THROTTLE, 1.0);
        let mut out = EngineCommands::default();
        if (frac - self.last_throttle).abs() > 0.002 {
            self.last_throttle = frac;
            out.throttle = Some(frac);
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Rec {
        dincs: u32,
    }
    impl AgcPort for Rec {
        fn recv(&mut self) -> Option<crate::proto::AgcEvent> {
            None
        }
        fn write_channel(&mut self, _c: u16, _v: u16, _m: Option<u16>) {}
        fn counter(&mut self, register: u8, kind: CounterKind) {
            assert_eq!((register, kind), (reg::THRUST, CounterKind::Dinc));
            self.dincs += 1;
        }
        fn connected(&self) -> bool {
            true
        }
    }

    #[test]
    fn ignite_needs_arm_and_edge() {
        let mut e = Engines::new();
        // ENGINE ON with the arm switch off: no ignite.
        assert!(!e.on_dsalmout(1 << 12, false).ignite);
        // Bit still set, now armed — but no new edge (bit was already on).
        assert!(!e.on_dsalmout(1 << 12, true).ignite);
        // Drop and re-raise: edge fires.
        e.on_dsalmout(0, true);
        assert!(e.on_dsalmout(1 << 12, true).ignite);
        // ENGINE OFF edge always honored.
        assert!(e.on_dsalmout(1 << 13, true).shutdown);
    }

    #[test]
    fn thrust_clocking_tracks_pulses() {
        let mut e = Engines::new();
        let mut p = Rec { dincs: 0 };
        e.on_chan14(1 << 3); // drive activity up
        e.tick(&mut p);
        assert!(p.dincs > 0, "drain must clock DINCs");
        // 728 POUT echoes ≈ +20% of DPS thrust (728·2.7/9817.5 ≈ 0.20).
        for _ in 0..728 {
            e.on_thrust_echo(0o15);
        }
        e.on_thrust_echo(0o17); // ZOUT — drained
        let out = e.tick(&mut p);
        let thr = out.throttle.expect("throttle moved");
        assert!((thr - 0.30).abs() < 0.01, "0.10 + 0.20 ≈ {thr}");
        // MOUTs walk it back down.
        for _ in 0..364 {
            e.on_thrust_echo(0o16);
        }
        let thr2 = e.tick(&mut p).throttle.unwrap();
        assert!((thr2 - 0.20).abs() < 0.01, "{thr2}");
    }

    #[test]
    fn throttle_clamps_to_floor_and_full() {
        let mut e = Engines::new();
        let mut p = Rec { dincs: 0 };
        for _ in 0..100_000 {
            e.on_thrust_echo(0o15);
        }
        assert_eq!(e.tick(&mut p).throttle, Some(1.0));
        for _ in 0..300_000 {
            e.on_thrust_echo(0o16);
        }
        assert_eq!(e.tick(&mut p).throttle, Some(MIN_THROTTLE));
    }
}
