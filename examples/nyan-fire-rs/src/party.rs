//! The plume animation model. (The filename is inherited from `examples/dancy-party-rs`, whose
//! `party.rs` this is a fork of, so the two files diff 1:1. It holds the *show's* animation plan.)
//!
//! Everything here is a pure function of *elapsed time*, so it's trivially testable and the worker
//! thread (which owns the wall clock) just feeds it `start.elapsed()`.
//!
//! There are **two independent clocks**, because the two things we drive want different rates:
//!
//! - The **stripe clock** walks the palette in segments of `color_ms`. The plume's emission gradient
//!   has **four spatial stops** (`color0` at the nozzle exit … `color3` at the plume tip) which the
//!   game blends between along the exhaust, so we treat the palette as a *cyclic stripe sequence* and
//!   show a **moving window** over it: at segment `k`, slot `i` shows `palette[(k + i*slot_step) % n]`.
//!   The whole rainbow therefore sits along the plume at once and *scrolls* nozzle-ward as `k` advances.
//! - The **brightness clock** drives `emission/brightness` — a wholly separate leaf on a real 0..200
//!   scale (**not** a colour multiplier), so it has its own segment length `bright_ms` and its own
//!   quantization. It is **off by default**, in which case the leaf is never written at all and the
//!   template keeps its authored brightness.
//!
//! Each clock also has optional **stagger** (`slot_stagger_ms` per gradient slot, `tpl_stagger_ms` per
//! template): the worker animates slot `i` of template `t` as if its clock were
//! `i*slot_stagger + t*tpl_stagger` behind the lead, so a non-zero value ripples the scroll across the
//! stops / templates instead of moving them in lockstep. Note this composes with — and is independent
//! of — the *window* offset, which is what produces the rainbow in the first place.

use crate::color::Rgb;

/// An immutable show plan: the ordered stripe palette plus the two independent timings (and their
/// staggers) and both quantizations. The worker recomputes colours/brightness from this every tick;
/// editing it mid-show swaps the plan in place without resetting the clock (see `source::RunningShow`).
#[derive(Clone, Debug)]
pub struct Plan {
    /// The ordered stripe palette. Empty ⇒ the show refuses to start.
    pub colors: Vec<Rgb>,
    /// Stripe-clock segment duration, ms — how long one palette entry occupies a gradient slot.
    pub color_ms: f64,
    /// Cross-fade quantization per segment (`0` = continuous; `1` = hard cut / crisp stripes; higher
    /// = smoother but more distinct writes). Fewer steps ⇒ fewer writes.
    pub steps: u32,
    /// Per-slot clock offset, ms (`0` = the four slots share one clock).
    pub slot_stagger_ms: f64,
    /// How many palette entries apart adjacent gradient slots sit.
    /// `1` (default) = a 4-wide moving window; `0` = all four slots show the same colour
    /// (a solid plume that cycles); `2+` = a coarser rainbow across the plume.
    pub slot_step: u32,
    /// Per-template clock offset, ms (`0` = every armed template in lockstep).
    pub tpl_stagger_ms: f64,
    /// Brightness pulse floor, on the leaf's real 0..200 scale.
    pub bright_min: f64,
    /// Brightness pulse ceiling, 0..200. `bright_max <= 0` ⇒ the effect is OFF and
    /// `emission/brightness` is NEVER written (the template keeps its authored value).
    pub bright_max: f64,
    /// Brightness-clock segment length, ms.
    pub bright_ms: f64,
    /// Brightness quantization per segment (`0` = continuous).
    pub bright_steps: u32,
}

impl Plan {
    pub fn new(colors: Vec<Rgb>, color_ms: u64) -> Self {
        Self {
            colors,
            color_ms: (color_ms as f64).max(1.0),
            steps: 0,
            slot_stagger_ms: 0.0,
            slot_step: 1,
            tpl_stagger_ms: 0.0,
            // Default: the brightness effect is off — the leaf is never written.
            bright_min: 0.0,
            bright_max: 0.0,
            bright_ms: 600.0,
            bright_steps: 0,
        }
    }

    /// Builder: quantize the stripe cross-fade to `steps` discrete values per segment (`0` =
    /// continuous, `1` = hard cut — the crisp-band nyan look).
    pub fn with_steps(mut self, steps: u32) -> Self {
        self.steps = steps;
        self
    }

    /// Builder: set the per-slot clock offset and the window width between adjacent gradient slots.
    pub fn with_slots(mut self, slot_stagger_ms: f64, slot_step: u32) -> Self {
        self.slot_stagger_ms = slot_stagger_ms.max(0.0);
        self.slot_step = slot_step;
        self
    }

    /// Builder: set the per-template clock offset (`0` = lockstep).
    pub fn with_tpl_stagger(mut self, tpl_stagger_ms: f64) -> Self {
        self.tpl_stagger_ms = tpl_stagger_ms.max(0.0);
        self
    }

    /// Builder: configure the `emission/brightness` pulse (range on the real 0..200 leaf scale, the
    /// change interval, and the quantization). `max <= 0` leaves it **off** — the leaf is untouched.
    pub fn with_brightness(mut self, min: f64, max: f64, bright_ms: u64, bright_steps: u32) -> Self {
        self.bright_min = min.clamp(0.0, BRIGHT_CEILING);
        self.bright_max = max.clamp(0.0, BRIGHT_CEILING);
        self.bright_ms = (bright_ms as f64).max(1.0);
        self.bright_steps = bright_steps;
        self
    }

    /// The colour for gradient slot `slot` (0 = nozzle exit … 3 = plume tip) of template `tpl`
    /// at `elapsed_ms`, plus that slot's stripe-segment index.
    ///
    /// Two independent offsets compose here:
    ///   * the WINDOW offset (`slot * slot_step` palette entries) — this is the rainbow itself,
    ///     and it is what makes all four stops show different colours at the same instant;
    ///   * the CLOCK offset (`slot * slot_stagger_ms` + `tpl * tpl_stagger_ms`) — optional, it
    ///     ripples the scroll across slots / templates instead of moving them in lockstep.
    ///
    /// Pure and total: an empty palette falls back to white (the worker never starts with one).
    pub fn slot_color_at(&self, tpl: usize, slot: usize, elapsed_ms: f64) -> (Rgb, u64) {
        let n = self.colors.len();
        if n == 0 {
            return (Rgb::WHITE, 0);
        }
        let local =
            elapsed_ms - slot as f64 * self.slot_stagger_ms - tpl as f64 * self.tpl_stagger_ms;
        let progress = local.max(0.0) / self.color_ms;
        let segment = progress.floor() as u64;
        let t_raw = progress - segment as f64; // 0..1 within the current segment
        let t = quantize(t_raw, self.steps);
        let base = segment as usize + slot * self.slot_step as usize;
        let from = self.colors[base % n];
        let to = self.colors[(base + 1) % n];
        (from.lerp(to, t), segment)
    }

    /// The `emission/brightness` value for template `tpl` at `elapsed_ms`, or `None` when the effect
    /// is OFF — in which case the leaf is NEVER written and the template keeps its authored brightness.
    ///
    /// OFF is `bright_max <= 0` (a zero-brightness plume is invisible, so 0 is the natural sentinel and
    /// leaves "pin a constant" available as `min == max > 0`). When on, each template drifts between
    /// independent random targets drawn from `[min, max]` — one per `bright_ms` segment, interpolated
    /// (optionally quantized to `bright_steps`) — so the exhaust throbs. Deterministic: the targets come
    /// from `rand01(tpl, segment)`, so the same plan always produces the same throb.
    pub fn brightness_at(&self, tpl: usize, elapsed_ms: f64) -> Option<f64> {
        if self.bright_max <= 0.0 {
            return None;
        }
        let lo = self.bright_min.min(self.bright_max).clamp(0.0, BRIGHT_CEILING);
        let hi = self.bright_min.max(self.bright_max).clamp(0.0, BRIGHT_CEILING);
        if hi - lo < 1e-9 {
            return Some(hi); // pinned constant (one deduped write)
        }
        let progress = elapsed_ms.max(0.0) / self.bright_ms;
        let segment = progress.floor() as u64;
        let t = quantize(progress - segment as f64, self.bright_steps);
        let from = lo + rand01(tpl as u64, segment) * (hi - lo);
        let to = lo + rand01(tpl as u64, segment + 1) * (hi - lo);
        Some(from + (to - from) * t)
    }
}

/// The inclusive ceiling of the `emission/brightness` leaf (`FxCatalog.EnginePlume`, SPEC §3.7).
pub const BRIGHT_CEILING: f64 = 200.0;

/// The number of gradient stops the plume's emission ramp has (`emission/color0..color3`).
pub const SLOTS: usize = 4;

/// Snaps `t` (0..1 within a segment) to the lower of `steps` evenly-spaced values, capping the
/// number of DISTINCT values written per segment. `0` = continuous; `1` = hard cut (t is always 0).
fn quantize(t: f64, steps: u32) -> f64 {
    if steps == 0 {
        t
    } else {
        let s = steps as f64;
        ((t * s).floor() / s).min((s - 1.0) / s)
    }
}

/// The `/sim` wire form of a brightness value: trimmed to 3 decimals with no trailing zeros
/// (`12`, `0.5`, `137.25`). The leaf's range is 0..200, so 3 decimals is ample and 5 would be noise.
pub fn fmt_num(v: f64) -> String {
    let s = format!("{:.3}", v.clamp(0.0, BRIGHT_CEILING));
    let trimmed = s.trim_end_matches('0').trim_end_matches('.');
    if trimmed.is_empty() {
        "0".to_string()
    } else {
        trimmed.to_string()
    }
}

/// A deterministic pseudo-random value in `[0, 1)` from two integer inputs (a SplitMix64-style mix).
/// Keying on `(template, segment)` gives each template its own reproducible brightness sequence
/// without a PRNG dependency or any per-frame state.
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

    /// A 6-entry palette whose entries are all distinguishable.
    fn six() -> Vec<Rgb> {
        (0..6)
            .map(|i| rgb(i as f64 / 10.0, 1.0 - i as f64 / 10.0, 0.5))
            .collect()
    }

    #[test]
    fn window_shows_four_different_palette_entries() {
        let p = six();
        let plan = Plan::new(p.clone(), 1000).with_steps(1);
        // At t = 0 the window sits on P[0..4], one entry per gradient slot.
        for slot in 0..SLOTS {
            assert_eq!(plan.slot_color_at(0, slot, 0.0).0, p[slot]);
        }
        // One stripe segment later the whole window has scrolled one entry nozzle-ward.
        for slot in 0..SLOTS {
            assert_eq!(plan.slot_color_at(0, slot, 1000.0).0, p[slot + 1]);
        }
    }

    #[test]
    fn hard_cut_holds_a_slot_for_a_whole_segment() {
        let p = six();
        let plan = Plan::new(p.clone(), 1000).with_steps(1);
        assert_eq!(plan.slot_color_at(0, 0, 0.0).0, p[0]);
        assert_eq!(plan.slot_color_at(0, 0, 990.0).0, p[0]); // no fade at all within the segment
        assert_eq!(plan.slot_color_at(0, 0, 1000.0).0, p[1]); // snaps at the boundary
    }

    #[test]
    fn continuous_fade_interpolates() {
        // steps = 0 (the default): slot 0 fades smoothly from P[0] to P[1] across the segment.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 0.0, 1.0)], 1000);
        let (mid, seg) = plan.slot_color_at(0, 0, 500.0);
        assert_eq!(seg, 0);
        assert!((mid.r - 0.5).abs() < 1e-9 && (mid.b - 0.5).abs() < 1e-9);
    }

    #[test]
    fn steps_quantize_the_fade() {
        let red = rgb(1.0, 0.0, 0.0);
        let blue = rgb(0.0, 0.0, 1.0);
        // steps = 4: t snaps to {0, .25, .5, .75}. At 60% through, that floors to .5.
        let q = Plan::new(vec![red, blue], 1000).with_steps(4);
        let (f, _) = q.slot_color_at(0, 0, 600.0);
        assert!((f.r - 0.5).abs() < 1e-9 && (f.b - 0.5).abs() < 1e-9);
        let (late, _) = q.slot_color_at(0, 0, 999.0);
        assert!((late.b - 0.75).abs() < 1e-9);
    }

    #[test]
    fn slot_step_zero_makes_every_slot_equal() {
        // slot_step 0 collapses the window: all four stops share one colour, so the plume is solid
        // and simply cycles the palette.
        let plan = Plan::new(six(), 1000).with_steps(1).with_slots(0.0, 0);
        let lead = plan.slot_color_at(0, 0, 2500.0).0;
        for slot in 1..SLOTS {
            assert_eq!(plan.slot_color_at(0, slot, 2500.0).0, lead);
        }
    }

    #[test]
    fn slot_stagger_equal_to_color_ms_cancels_the_window() {
        // A per-slot clock offset of exactly one segment moves each slot one segment BACK, which
        // exactly cancels the one-entry window offset — every stop lands on the same colour again.
        let plan = Plan::new(six(), 1000).with_steps(1).with_slots(1000.0, 1);
        let lead = plan.slot_color_at(0, 0, 5000.0).0;
        for slot in 1..SLOTS {
            assert_eq!(plan.slot_color_at(0, slot, 5000.0).0, lead);
        }
    }

    #[test]
    fn palette_scrolls_back_to_start_after_n_segments() {
        let p = six();
        let plan = Plan::new(p.clone(), 100).with_steps(1);
        // n entries x color_ms = one full scroll; slot 0 is back on P[0].
        let (c, seg) = plan.slot_color_at(0, 0, 600.0);
        assert_eq!(seg, 6);
        assert_eq!(c, p[0]);
    }

    #[test]
    fn empty_palette_is_total() {
        let plan = Plan::new(vec![], 500);
        assert_eq!(plan.slot_color_at(0, 3, 1234.0).0, Rgb::WHITE);
    }

    #[test]
    fn tpl_stagger_desyncs_templates() {
        // 500 ms behind on a 1 s stripe clock: template 1's slot 0 is half a segment behind
        // template 0's, so with a continuous fade they resolve to different colours.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0), rgb(0.0, 0.0, 1.0)], 1000)
            .with_tpl_stagger(500.0);
        let a = plan.slot_color_at(0, 0, 500.0).0;
        let b = plan.slot_color_at(1, 0, 500.0).0;
        assert_ne!(a, b);
    }

    #[test]
    fn brightness_is_none_when_max_is_zero() {
        // The default plan leaves emission/brightness alone entirely.
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000);
        assert_eq!(plan.brightness_at(0, 0.0), None);
        assert_eq!(plan.brightness_at(3, 9999.0), None);
        // An explicit zero ceiling is also off (not "write 0").
        let off = plan.with_brightness(0.0, 0.0, 500, 0);
        assert_eq!(off.brightness_at(0, 1234.0), None);
    }

    #[test]
    fn brightness_is_pinned_when_min_equals_max() {
        // A collapsed but non-zero range pins the leaf to a constant (one write, then deduped).
        let plan = Plan::new(vec![rgb(1.0, 0.0, 0.0)], 1000).with_brightness(42.5, 42.5, 500, 0);
        assert_eq!(plan.brightness_at(0, 0.0), Some(42.5));
        assert_eq!(plan.brightness_at(7, 9999.0), Some(42.5));
    }

    #[test]
    fn brightness_stays_in_range_and_is_deterministic() {
        let plan = Plan::new(vec![rgb(1.0, 1.0, 1.0)], 1000).with_brightness(20.0, 80.0, 500, 0);
        for tpl in 0..5 {
            for step in 0..50 {
                let b = plan.brightness_at(tpl, step as f64 * 37.0).unwrap();
                assert!((20.0..=80.0).contains(&b), "brightness {b} out of range");
            }
        }
        // Deterministic: same (template, time) -> same value.
        assert_eq!(plan.brightness_at(2, 321.0), plan.brightness_at(2, 321.0));
    }

    #[test]
    fn brightness_varies_across_templates_and_segments() {
        let plan = Plan::new(vec![rgb(1.0, 1.0, 1.0)], 1000).with_brightness(0.0, 200.0, 500, 0);
        // Different templates land on different targets at the same instant.
        let t0 = plan.brightness_at(0, 0.0).unwrap();
        let t1 = plan.brightness_at(1, 0.0).unwrap();
        assert!((t0 - t1).abs() > 1e-9, "templates should differ");
        // The same template changes its target across brightness segments.
        let seg0 = plan.brightness_at(0, 0.0).unwrap();
        let seg2 = plan.brightness_at(0, 1000.0).unwrap(); // two 500 ms segments later
        assert!((seg0 - seg2).abs() > 1e-9, "segments should differ");
    }

    #[test]
    fn brightness_wire_form_is_compact() {
        assert_eq!(fmt_num(12.0), "12");
        assert_eq!(fmt_num(0.0), "0");
        assert_eq!(fmt_num(137.25), "137.25");
        assert_eq!(fmt_num(1e9), "200"); // clamped to the leaf ceiling
    }
}
