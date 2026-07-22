//! Pause / warp / stale gating + resync orchestration (AGC_PLAN §3.4) — the land-o-matic M6
//! precedent: never feed counters or write controls during pause (`time/sim_dt == 0`),
//! time-warp (> 1×), or stale telemetry (same `seq` several ticks running). On release, the
//! bridge performs a state resync (clock trim + state-vector uplink) because the extern AGC's
//! wall clock kept running.

/// Why feeds are held (shown on the status panel).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Hold {
    Paused,
    Warp,
    Stale,
}

#[derive(Default)]
pub struct ClockPolicy {
    prev_seq: Option<u64>,
    stale_count: u32,
    holding: Option<Hold>,
    /// Sim time when the hold began (the resync knows how far the AGC clock drifted).
    hold_started_ut: f64,
}

/// The gate verdict for one tick.
pub struct Verdict {
    /// None = feed normally.
    pub hold: Option<Hold>,
    /// A hold just ended: run the resync (uplink state vector, trim the clock by ~`drift` s).
    pub resync: Option<f64>,
}

impl ClockPolicy {
    pub fn new() -> Self {
        Self::default()
    }

    /// Evaluates one telemetry tick. `sim_dt == Some(0.0)` ⇒ paused.
    pub fn gate(&mut self, seq: u64, ut: f64, warp: f64, sim_dt: Option<f64>) -> Verdict {
        let paused = sim_dt == Some(0.0);
        let stalled = self.prev_seq == Some(seq) && !paused;
        self.stale_count = if stalled { self.stale_count + 1 } else { 0 };
        self.prev_seq = Some(seq);

        let hold = if paused {
            Some(Hold::Paused)
        } else if warp > 1.0 {
            Some(Hold::Warp)
        } else if self.stale_count >= 3 {
            Some(Hold::Stale)
        } else {
            None
        };

        let resync = match (self.holding, hold) {
            (None, Some(_)) => {
                self.hold_started_ut = ut;
                None
            }
            (Some(_), None) => Some(ut - self.hold_started_ut),
            _ => None,
        };
        self.holding = hold;
        Verdict { hold, resync }
    }

    pub fn holding(&self) -> Option<Hold> {
        self.holding
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pause_holds_and_resyncs_on_resume() {
        let mut cp = ClockPolicy::new();
        assert!(cp.gate(1, 100.0, 1.0, Some(0.02)).hold.is_none());
        let v = cp.gate(2, 100.0, 1.0, Some(0.0));
        assert_eq!(v.hold, Some(Hold::Paused));
        assert!(v.resync.is_none());
        // Paused ticks keep the same seq but that must NOT count as stale.
        assert_eq!(cp.gate(2, 100.0, 1.0, Some(0.0)).hold, Some(Hold::Paused));
        let v = cp.gate(3, 130.0, 1.0, Some(0.02));
        assert!(v.hold.is_none());
        assert_eq!(v.resync, Some(30.0), "resync carries the hold duration");
    }

    #[test]
    fn warp_holds() {
        let mut cp = ClockPolicy::new();
        assert_eq!(cp.gate(1, 0.0, 10.0, Some(0.02)).hold, Some(Hold::Warp));
    }

    #[test]
    fn stale_latches_after_three() {
        let mut cp = ClockPolicy::new();
        assert!(cp.gate(7, 0.0, 1.0, Some(0.02)).hold.is_none());
        assert!(cp.gate(7, 0.0, 1.0, Some(0.02)).hold.is_none());
        assert!(cp.gate(7, 0.0, 1.0, Some(0.02)).hold.is_none());
        assert_eq!(cp.gate(7, 0.0, 1.0, Some(0.02)).hold, Some(Hold::Stale));
    }
}
