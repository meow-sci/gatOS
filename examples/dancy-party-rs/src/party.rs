//! The party animation — a pure function of *elapsed time*, so it's trivially testable and the
//! worker thread (which owns the wall clock) just feeds it `start.elapsed()`.
//!
//! Given an ordered palette `c[0..n]` and a per-color duration, the loop walks **segments**: segment
//! `k` cross-fades from `c[k % n]` to `c[(k+1) % n]` over one duration, so the palette cycles forever
//! as a smooth gradient. In parallel the animation **goal** — the `lights/<n>/goal` deploy setpoint —
//! flips between `1` and `0` on every segment boundary, which is what makes the light hardware
//! physically animate (extend/retract) each color step, exactly the `echo 1 > goal … echo 0 > goal`
//! pulse the task describes. A single-color palette still pulses the goal; the color just holds.

use crate::color::Rgb;

/// An immutable party plan: the ordered palette + how long each color lasts. The worker recomputes a
/// [`Frame`] from this every tick; editing the palette/duration mid-party swaps the plan in place
/// without resetting the clock (see `source::RunningParty`).
#[derive(Clone, Debug)]
pub struct Plan {
    pub colors: Vec<Rgb>,
    pub per_ms: f64,
    /// How many discrete color values the cross-fade is quantized to **per segment** (the `--steps`
    /// perf knob). `0` means continuous (the fade is limited only by `--hz` and the 5-decimal wire
    /// quantization in [`Rgb::to_sim`]); `1` snaps to each palette color with no fade at all; higher
    /// values trade smoothness for fewer distinct color writes. Fewer steps ⇒ fewer 9p writes.
    pub steps: u32,
    /// Per-light time offset in milliseconds (the `--stagger-ms` knob): light `i` is animated as if
    /// the clock were `i * stagger_ms` behind the lead, so the palette ripples across the lights
    /// instead of every light changing at once. `0` (default) means no stagger — every light shares
    /// the lead frame, the original lockstep broadcast.
    pub stagger_ms: f64,
}

/// One animation frame: the interpolated tint to push to every light's `color`, plus the current
/// segment index and the `goal` setpoint (0/1) for this segment.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Frame {
    pub color: Rgb,
    pub segment: u64,
    pub goal: u8,
}

impl Plan {
    pub fn new(colors: Vec<Rgb>, per_ms: u64) -> Self {
        Self {
            colors,
            per_ms: (per_ms as f64).max(1.0),
            steps: 0,
            stagger_ms: 0.0,
        }
    }

    /// Builder: quantize the cross-fade to `steps` discrete values per segment (`0` = continuous).
    pub fn with_steps(mut self, steps: u32) -> Self {
        self.steps = steps;
        self
    }

    /// Builder: offset each light by `stagger_ms` so the palette ripples across them (`0` = lockstep).
    pub fn with_stagger(mut self, stagger_ms: f64) -> Self {
        self.stagger_ms = stagger_ms.max(0.0);
        self
    }

    /// The frame at `elapsed_ms` since the party began. With an empty palette the color falls back to
    /// white (the worker never starts a party with no colors, but the function stays total).
    pub fn frame(&self, elapsed_ms: f64) -> Frame {
        let n = self.colors.len();
        if n == 0 {
            return Frame {
                color: Rgb::WHITE,
                segment: 0,
                goal: 0,
            };
        }
        let progress = (elapsed_ms.max(0.0)) / self.per_ms;
        let segment = progress.floor() as u64;
        let t_raw = progress - segment as f64; // 0..1 within the current segment
        // `--steps` quantizes the fade: snap `t` to the nearest lower of `steps` evenly-spaced values
        // (0, 1/s, … (s-1)/s). This caps the number of *distinct* colors written per segment to
        // `steps`, the main lever for cutting 9p write volume. `steps == 0` leaves the fade continuous.
        let t = if self.steps == 0 {
            t_raw
        } else {
            let s = self.steps as f64;
            ((t_raw * s).floor() / s).min((s - 1.0) / s)
        };
        let from = self.colors[(segment as usize) % n];
        let to = self.colors[((segment as usize) + 1) % n];
        Frame {
            color: from.lerp(to, t),
            segment,
            // Start deployed (1) and flip each segment: even -> 1, odd -> 0 (the `echo 1 > goal …
            // echo 0 > goal` pulse, beginning with the extend).
            goal: ((segment + 1) % 2) as u8,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rgb(r: f64, g: f64, b: f64) -> Rgb {
        Rgb::new(r, g, b)
    }

    #[test]
    fn segment_and_goal_advance_with_time() {
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 0.0, 1.0)], 1000);
        // Start of segment 0: exactly the first color, goal high.
        let f0 = plan.frame(0.0);
        assert_eq!(f0.segment, 0);
        assert_eq!(f0.goal, 1);
        assert_eq!(f0.color, rgb(1.0, 0.0, 0.0));
        // Halfway through segment 0: halfway between red and blue.
        let mid = plan.frame(500.0);
        assert!((mid.color.r - 0.5).abs() < 1e-9 && (mid.color.b - 0.5).abs() < 1e-9);
        // Segment 1 flips the goal and now fades blue -> red.
        let f1 = plan.frame(1000.0);
        assert_eq!(f1.segment, 1);
        assert_eq!(f1.goal, 0);
        assert_eq!(f1.color, rgb(0.0, 0.0, 1.0));
    }

    #[test]
    fn palette_cycles_back_to_start() {
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 1.0, 0.0), rgb(0.0, 0.0, 1.0)], 100);
        // After 3 segments (300 ms) we're back to the first color (segment 3, an odd segment so the
        // goal has flipped low).
        let f = plan.frame(300.0);
        assert_eq!(f.segment, 3);
        assert_eq!(f.goal, 0);
        assert_eq!(f.color, rgb(1.0, 0.0, 0.0));
    }

    #[test]
    fn single_color_holds_but_goal_still_pulses() {
        let plan = Plan::new(vec![rgb(0.2, 0.4, 0.6)], 500);
        // Color never changes (from == to), but the goal alternates each 500 ms segment.
        assert_eq!(plan.frame(0.0).goal, 1);
        assert_eq!(plan.frame(0.0).color, rgb(0.2, 0.4, 0.6));
        assert_eq!(plan.frame(600.0).goal, 0);
        assert_eq!(plan.frame(600.0).color, rgb(0.2, 0.4, 0.6));
        assert_eq!(plan.frame(1100.0).goal, 1);
    }

    #[test]
    fn empty_palette_is_total() {
        let plan = Plan::new(vec![], 500);
        assert_eq!(plan.frame(1234.0).color, Rgb::WHITE);
    }

    #[test]
    fn steps_quantize_the_fade() {
        let red = rgb(1.0, 0.0, 0.0);
        let blue = rgb(0.0, 0.0, 1.0);

        // steps = 1: snap to the segment's start color for the whole segment (no fade).
        let snap = Plan::new(vec![red, blue], 1000).with_steps(1);
        assert_eq!(snap.frame(0.0).color, red);
        assert_eq!(snap.frame(900.0).color, red); // still red until the segment flips
        assert_eq!(snap.frame(1000.0).color, blue);

        // steps = 4: t snaps to {0, .25, .5, .75}. At 60% through, that floors to .5.
        let q = Plan::new(vec![red, blue], 1000).with_steps(4);
        let f = q.frame(600.0);
        assert!((f.color.r - 0.5).abs() < 1e-9 && (f.color.b - 0.5).abs() < 1e-9);
        // Just shy of the next segment still caps at .75, never reaching pure blue mid-segment.
        let late = q.frame(999.0);
        assert!((late.color.b - 0.75).abs() < 1e-9);
    }
}
