//! The party animation — pure functions of *elapsed time*, so they're trivially testable and the
//! worker thread (which owns the wall clock) just feeds them `start.elapsed()`.
//!
//! There are **two independent clocks**, because the two things we drive want different rates:
//!
//! - The **color cross-fade** walks the palette in *segments* of `color_ms`: segment `k` fades from
//!   `c[k % n]` to `c[(k+1) % n]`, so the palette cycles forever as a smooth gradient.
//! - The **animation goal** — the `lights/<n>/goal` deploy setpoint that physically extends/retracts
//!   the light hardware — flips between `1` and `0` every `anim_ms`. The in-game deploy animation is
//!   ~2 s, so if the goal flipped on the *color* cadence it would never finish a stroke; giving it its
//!   own (slower) clock lets each extend/retract complete. A single-color palette still pulses.
//!
//! Each clock also has its own per-light **stagger** (`color_stagger_ms` / `anim_stagger_ms`): the
//! worker animates light `i` as if its clock were `i * stagger` behind the lead, so a non-zero value
//! ripples the effect across the lights instead of changing every light at once. The two staggers are
//! independent — you can ripple the color while every light pulses in lockstep, or vice-versa.

use crate::color::Rgb;

/// An immutable party plan: the ordered palette plus the two independent timings (and their
/// staggers) and the fade quantization. The worker recomputes color/goal from this every tick;
/// editing it mid-party swaps the plan in place without resetting the clock (see `source::RunningParty`).
#[derive(Clone, Debug)]
pub struct Plan {
    pub colors: Vec<Rgb>,
    /// Cross-fade segment duration, ms — how long one palette color lasts before fading to the next.
    pub color_ms: f64,
    /// Animation goal-pulse half-period, ms — how long the deploy `goal` holds each of `1`/`0`. Make
    /// this ≥ the in-game deploy time (~2 s) so each extend/retract stroke actually completes.
    pub anim_ms: f64,
    /// How many discrete color values the cross-fade is quantized to **per segment** (`0` = continuous;
    /// `1` = hard cut, no fade; higher = smoother but more distinct writes). Fewer steps ⇒ fewer writes.
    pub steps: u32,
    /// Per-light color-clock offset, ms (`0` = every light shares the lead color).
    pub color_stagger_ms: f64,
    /// Per-light animation-clock offset, ms (`0` = every light pulses in lockstep).
    pub anim_stagger_ms: f64,
    /// Random-brightness range floor (0..1). When `bright_min == bright_max` the effect is off and the
    /// brightness multiplier is a constant (default `1.0` = full, untouched color).
    pub bright_min: f64,
    /// Random-brightness range ceiling (0..1).
    pub bright_max: f64,
    /// How long each random brightness target holds before drifting to the next, ms (the brightness
    /// clock's segment length).
    pub bright_ms: f64,
    /// How many discrete brightness values the drift between two targets is quantized to (`0` =
    /// continuous). Like the color `steps`, this bounds the **distinct** brightness writes per segment.
    pub bright_steps: u32,
    /// Animation actuation floor (0..1) — the `goal` setpoint the *retract* half of the pulse drives to
    /// (default `0` = fully retracted). When `anim_min == anim_max` the animation is **off**: the goal
    /// is never written at all (see [`Plan::goal_at`]).
    pub anim_min: f64,
    /// Animation actuation ceiling (0..1) — the `goal` setpoint the *extend* half of the pulse drives to
    /// (default `1` = fully deployed).
    pub anim_max: f64,
}

impl Plan {
    pub fn new(colors: Vec<Rgb>, color_ms: u64, anim_ms: u64) -> Self {
        Self {
            colors,
            color_ms: (color_ms as f64).max(1.0),
            anim_ms: (anim_ms as f64).max(1.0),
            steps: 0,
            color_stagger_ms: 0.0,
            anim_stagger_ms: 0.0,
            // Default: brightness effect off (constant full brightness).
            bright_min: 1.0,
            bright_max: 1.0,
            bright_ms: 600.0,
            bright_steps: 0,
            // Default: the goal pulses across the full retract..extend range (0..1).
            anim_min: 0.0,
            anim_max: 1.0,
        }
    }

    /// Builder: quantize the cross-fade to `steps` discrete values per segment (`0` = continuous).
    pub fn with_steps(mut self, steps: u32) -> Self {
        self.steps = steps;
        self
    }

    /// Builder: set the independent per-light color / animation staggers (`0` = lockstep).
    pub fn with_staggers(mut self, color_stagger_ms: f64, anim_stagger_ms: f64) -> Self {
        self.color_stagger_ms = color_stagger_ms.max(0.0);
        self.anim_stagger_ms = anim_stagger_ms.max(0.0);
        self
    }

    /// Builder: configure the random per-light brightness drift (range, change interval, quantization).
    /// `min == max` turns it off (constant brightness = that value; `1.0` = untouched).
    pub fn with_brightness(mut self, min: f64, max: f64, bright_ms: u64, bright_steps: u32) -> Self {
        self.bright_min = min.clamp(0.0, 1.0);
        self.bright_max = max.clamp(0.0, 1.0);
        self.bright_ms = (bright_ms as f64).max(1.0);
        self.bright_steps = bright_steps;
        self
    }

    /// Builder: set the animation actuation range (0..1) the goal pulse swings between. `min == max`
    /// turns the animation **off** — the goal is never written (see [`Plan::goal_at`]).
    pub fn with_anim_range(mut self, min: f64, max: f64) -> Self {
        self.anim_min = min.clamp(0.0, 1.0);
        self.anim_max = max.clamp(0.0, 1.0);
        self
    }

    /// The brightness multiplier (0..1) for light `light` at `elapsed_ms` on the **brightness clock**.
    /// Each light drifts between independent random targets — one per `bright_ms` segment, drawn from
    /// `[bright_min, bright_max]` — interpolating (optionally quantized to `bright_steps`) between them,
    /// so the rig twinkles. Pure and deterministic: the targets come from a hash of `(light, segment)`,
    /// so the same plan always produces the same flicker (and it's testable). Off when `min == max`.
    pub fn brightness_at(&self, light: usize, elapsed_ms: f64) -> f64 {
        let lo = self.bright_min.min(self.bright_max).clamp(0.0, 1.0);
        let hi = self.bright_min.max(self.bright_max).clamp(0.0, 1.0);
        if hi - lo < 1e-9 {
            return hi;
        }
        let progress = elapsed_ms.max(0.0) / self.bright_ms;
        let segment = progress.floor() as u64;
        let t_raw = progress - segment as f64;
        let t = if self.bright_steps == 0 {
            t_raw
        } else {
            let s = self.bright_steps as f64;
            ((t_raw * s).floor() / s).min((s - 1.0) / s)
        };
        let l = light as u64;
        let from = lo + rand01(l, segment) * (hi - lo);
        let to = lo + rand01(l, segment + 1) * (hi - lo);
        from + (to - from) * t
    }

    /// The interpolated palette color at `elapsed_ms` on the **color clock**, plus the current
    /// cross-fade segment index. An empty palette falls back to white (the worker never starts a party
    /// with no colors, but the function stays total).
    pub fn color_at(&self, elapsed_ms: f64) -> (Rgb, u64) {
        let n = self.colors.len();
        if n == 0 {
            return (Rgb::WHITE, 0);
        }
        let progress = elapsed_ms.max(0.0) / self.color_ms;
        let segment = progress.floor() as u64;
        let t_raw = progress - segment as f64; // 0..1 within the current segment
        // `steps` quantizes the fade: snap `t` to the nearest lower of `steps` evenly-spaced values
        // (0, 1/s, … (s-1)/s), capping the number of *distinct* colors written per segment.
        let t = if self.steps == 0 {
            t_raw
        } else {
            let s = self.steps as f64;
            ((t_raw * s).floor() / s).min((s - 1.0) / s)
        };
        let from = self.colors[(segment as usize) % n];
        let to = self.colors[((segment as usize) + 1) % n];
        (from.lerp(to, t), segment)
    }

    /// The deploy `goal` (0..1 actuation fraction) at `elapsed_ms` on the **animation clock**, plus the
    /// current animation segment index. The pulse swings between the [`Plan::anim_min`]/[`Plan::anim_max`]
    /// range: it starts extended (`anim_max`) and flips each `anim_ms` — even segment → `anim_max`, odd →
    /// `anim_min` (the `echo <hi> > goal … echo <lo> > goal` pulse, beginning with the extend). When
    /// `anim_min == anim_max` the animation is a **noop**: `None` (the goal is never written), so a range
    /// collapsed to a point leaves the hardware wherever it sits instead of writing a constant setpoint.
    pub fn goal_at(&self, elapsed_ms: f64) -> (Option<f64>, u64) {
        let lo = self.anim_min.min(self.anim_max).clamp(0.0, 1.0);
        let hi = self.anim_min.max(self.anim_max).clamp(0.0, 1.0);
        let segment = (elapsed_ms.max(0.0) / self.anim_ms).floor() as u64;
        if hi - lo < 1e-9 {
            return (None, segment);
        }
        let value = if segment.is_multiple_of(2) { hi } else { lo };
        (Some(value), segment)
    }
}

/// A deterministic pseudo-random value in `[0, 1)` from two integer inputs (a SplitMix64-style mix).
/// Keying on `(light, segment)` gives each light its own reproducible brightness sequence without a
/// PRNG dependency or any per-frame state.
fn rand01(a: u64, b: u64) -> f64 {
    let mut x = a
        .wrapping_mul(0x9E37_79B9_7F4A_7C15)
        .wrapping_add(b.wrapping_add(1).wrapping_mul(0xD1B5_4A32_D192_ED03));
    x ^= x >> 30;
    x = x.wrapping_mul(0xBF58_476D_1CE4_E5B9);
    x ^= x >> 27;
    x = x.wrapping_mul(0x94D0_49BB_1331_11EB);
    x ^= x >> 31;
    (x >> 11) as f64 / (1u64 << 53) as f64
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rgb(r: f64, g: f64, b: f64) -> Rgb {
        Rgb::new(r, g, b)
    }

    #[test]
    fn color_clock_fades_and_advances() {
        // 1 s/color cross-fade between red and blue.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 0.0, 1.0)], 1000, 1000);
        let (c0, seg0) = plan.color_at(0.0);
        assert_eq!(seg0, 0);
        assert_eq!(c0, rgb(1.0, 0.0, 0.0));
        let (mid, _) = plan.color_at(500.0);
        assert!((mid.r - 0.5).abs() < 1e-9 && (mid.b - 0.5).abs() < 1e-9);
        let (c1, seg1) = plan.color_at(1000.0);
        assert_eq!(seg1, 1);
        assert_eq!(c1, rgb(0.0, 0.0, 1.0));
    }

    #[test]
    fn animation_clock_is_independent_of_color_clock() {
        // Fast color (200 ms) but slow animation (2 s): the goal must follow the 2 s clock, not color.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 0.0, 1.0)], 200, 2000);
        // Several color segments have elapsed by 1 s, but the goal is still in its first (extend) stroke.
        assert_eq!(plan.color_at(1000.0).1, 5); // 1000/200
        assert_eq!(plan.goal_at(1000.0), (Some(1.0), 0)); // still deployed, animation segment 0
        // The goal only flips once the 2 s animation clock crosses a boundary.
        assert_eq!(plan.goal_at(2000.0), (Some(0.0), 1));
        assert_eq!(plan.goal_at(4000.0), (Some(1.0), 2));
    }

    #[test]
    fn palette_cycles_back_to_start() {
        let plan = Plan::new(
            vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 1.0, 0.0), rgb(0.0, 0.0, 1.0)],
            100,
            100,
        );
        // After 3 color segments (300 ms) we're back to the first color.
        let (c, seg) = plan.color_at(300.0);
        assert_eq!(seg, 3);
        assert_eq!(c, rgb(1.0, 0.0, 0.0));
    }

    #[test]
    fn single_color_holds_but_goal_still_pulses() {
        let plan = Plan::new(vec![rgb(0.2, 0.4, 0.6)], 500, 500);
        assert_eq!(plan.color_at(0.0).0, rgb(0.2, 0.4, 0.6));
        assert_eq!(plan.color_at(600.0).0, rgb(0.2, 0.4, 0.6)); // from == to, never changes
        assert_eq!(plan.goal_at(0.0).0, Some(1.0));
        assert_eq!(plan.goal_at(600.0).0, Some(0.0));
        assert_eq!(plan.goal_at(1100.0).0, Some(1.0));
    }

    #[test]
    fn empty_palette_is_total() {
        let plan = Plan::new(vec![], 500, 500);
        assert_eq!(plan.color_at(1234.0).0, Rgb::WHITE);
    }

    #[test]
    fn brightness_is_off_when_min_equals_max() {
        // Default plan: min == max == 1.0 -> always full brightness, regardless of time/light.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000, 1000);
        assert_eq!(plan.brightness_at(0, 0.0), 1.0);
        assert_eq!(plan.brightness_at(3, 1234.0), 1.0);
        // A constant non-full value is also "off" (no variation) but dims everything uniformly.
        let dim = plan.with_brightness(0.3, 0.3, 500, 0);
        assert_eq!(dim.brightness_at(0, 0.0), 0.3);
        assert_eq!(dim.brightness_at(7, 999.0), 0.3);
    }

    #[test]
    fn brightness_stays_in_range_and_is_deterministic() {
        let plan = Plan::new(vec![rgb(1.0, 1.0, 1.0)], 1000, 1000).with_brightness(0.2, 0.8, 500, 0);
        for light in 0..5 {
            for step in 0..50 {
                let b = plan.brightness_at(light, step as f64 * 37.0);
                assert!((0.2..=0.8).contains(&b), "brightness {b} out of range");
            }
        }
        // Deterministic: same (light, time) -> same value.
        assert_eq!(
            plan.brightness_at(2, 321.0),
            plan.brightness_at(2, 321.0)
        );
    }

    #[test]
    fn brightness_varies_across_lights_and_segments() {
        let plan = Plan::new(vec![rgb(1.0, 1.0, 1.0)], 1000, 1000).with_brightness(0.0, 1.0, 500, 0);
        // Different lights land on different targets at the same instant (random per-light).
        let l0 = plan.brightness_at(0, 0.0);
        let l1 = plan.brightness_at(1, 0.0);
        assert!((l0 - l1).abs() > 1e-9, "lights should differ");
        // The same light changes its target across brightness segments.
        let seg0 = plan.brightness_at(0, 0.0);
        let seg2 = plan.brightness_at(0, 1000.0); // two 500 ms segments later
        assert!((seg0 - seg2).abs() > 1e-9, "segments should differ");
    }

    #[test]
    fn anim_range_swings_the_goal_between_min_and_max() {
        // A partial actuation range: extend half drives to 0.75, retract half to 0.25.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000, 1000).with_anim_range(0.25, 0.75);
        assert_eq!(plan.goal_at(0.0).0, Some(0.75)); // even segment -> max (extend)
        assert_eq!(plan.goal_at(1000.0).0, Some(0.25)); // odd segment -> min (retract)
        assert_eq!(plan.goal_at(2000.0).0, Some(0.75));
        // A reversed range still animates (min/max are normalized).
        let rev = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000, 1000).with_anim_range(0.75, 0.25);
        assert_eq!(rev.goal_at(0.0).0, Some(0.75));
        assert_eq!(rev.goal_at(1000.0).0, Some(0.25));
    }

    #[test]
    fn equal_anim_range_is_a_noop() {
        // min == max -> no animation at all: the goal is never written (None).
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000, 1000).with_anim_range(0.5, 0.5);
        assert_eq!(plan.goal_at(0.0).0, None);
        assert_eq!(plan.goal_at(1000.0).0, None);
        assert_eq!(plan.goal_at(9999.0).0, None);
        // Even the default-collapsed endpoints (e.g. both 0) are off, not a constant 0 write.
        let zero = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000, 1000).with_anim_range(0.0, 0.0);
        assert_eq!(zero.goal_at(500.0).0, None);
    }

    #[test]
    fn steps_quantize_the_fade() {
        let red = rgb(1.0, 0.0, 0.0);
        let blue = rgb(0.0, 0.0, 1.0);

        // steps = 1: snap to the segment's start color for the whole segment (no fade).
        let snap = Plan::new(vec![red, blue], 1000, 1000).with_steps(1);
        assert_eq!(snap.color_at(0.0).0, red);
        assert_eq!(snap.color_at(900.0).0, red);
        assert_eq!(snap.color_at(1000.0).0, blue);

        // steps = 4: t snaps to {0, .25, .5, .75}. At 60% through, that floors to .5.
        let q = Plan::new(vec![red, blue], 1000, 1000).with_steps(4);
        let (f, _) = q.color_at(600.0);
        assert!((f.r - 0.5).abs() < 1e-9 && (f.b - 0.5).abs() < 1e-9);
        let (late, _) = q.color_at(999.0);
        assert!((late.b - 0.75).abs() < 1e-9);
    }
}
