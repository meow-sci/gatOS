//! The "Skybrush Caps" vector font — letterforms designed around the physics of plume writing.
//!
//! A skywriting vehicle can only paint while thrusting (KSA emits trail segments whenever the engine
//! produced thrust that step) and can only *steer* while painting — with the engine off it flies a
//! ballistic arc whose lateral velocity cannot change. Those constraints shape every glyph here:
//!
//! - **One pen-down path per glyph** (a few need two). Anything already painted can be repainted
//!   invisibly, so a glyph is a connected walk over its strokes with retraces — an Euler tour of the
//!   doubled letterform graph, hand-authored.
//! - **Entries descend.** A ballistic hop between letters arrives moving *down* (past the arc's
//!   apex), so each path begins with a descending stroke that absorbs the arrival braking. Round
//!   glyphs get a small overshoot flick-serif above the curve to take the hit.
//! - **Exits are straight ~58° launch ramps.** The path ends riding a straight diagonal upward; the
//!   autopilot accelerates along it (repainting — invisible) and cuts the engine at the tip, so the
//!   exit tangent is the ballistic launch direction. The ramp must be *straight*: the flight
//!   computer slews attitude at only a few deg/s, so a launch run must hold one attitude. And it
//!   must be *diagonal*: a hop's required launch speed scales as `1/√(d̂ₓ·d̂ᵧ)`, so ~45–60° launches
//!   need far less ramp than near-vertical ones. Letters with a native up-right diagonal (A K M V W
//!   X Y Z 4 7 9) launch off it; everything else ends in an authored **swash** — a brush-flick off
//!   the letter's tail that doubles as the ramp. The flourish is load-bearing.
//! - **The italic is load-bearing too.** Layout shears every glyph ~10° right, which keeps even
//!   steep strokes launch-viable and gives the font its skywriter's-hurry look.
//!
//! Coordinates are em units, authored upright: baseline `y = 0`, cap height `y = 1`, `x ∈ [0, width]`.
//! Swashes and flick-serifs may poke outside. Layout applies the shear and scales by letter height.

/// One glyph: advance width (em) and its pen-down subpaths (em polylines, retraces inline).
/// Subpaths after the first are reached by an in-glyph ballistic hop (authored to be reachable:
/// e.g. `!` paints the bar, then hops down-right to an italically-offset dot).
#[derive(Clone, Debug)]
pub struct GlyphData {
    pub width: f64,
    pub subpaths: Vec<Vec<(f64, f64)>>,
    /// How far (em) the planner may lengthen the final launch segment when a hop (typically across
    /// a word gap) needs more ramp than authored. Swash exits take a long flourish (0.6 em); native
    /// diagonal exits allow only a small brush-overshoot past the stroke tip (0.25 em — think a
    /// hand-lettered A whose leg flicks a touch past the apex); dots allow none.
    pub max_ext_em: f64,
}

/// Width of the space "glyph" (no strokes), em.
pub const SPACE_WIDTH: f64 = 0.55;

/// The italic shear applied at layout: `x' = x + SLANT · y`. tan(10°) ≈ 0.176.
pub const SLANT: f64 = 0.176;

/// The swash launch direction, upright coords (58° above horizontal).
const SWASH_COS: f64 = 0.529_919_264_233_204_9;
const SWASH_SIN: f64 = 0.848_048_096_156_425_9;

/// Append the launch swash to a path: a straight 58° flick from the path's end, long enough to be a
/// usable ramp (≥ 0.55 em) and to lift the launch point clear of the baseline (tip y ≥ 0.7 where
/// the letter allows), capped so it doesn't tower over the cap line.
fn swash(path: &mut Vec<(f64, f64)>) {
    let &(x, y) = path.last().expect("swash needs a path end");
    let len = (0.55f64)
        .max((0.7 - y) / SWASH_SIN)
        .min(((1.05 - y) / SWASH_SIN).max(0.3));
    path.push((x + SWASH_COS * len, y + SWASH_SIN * len));
}

enum Exit {
    /// The authored path already ends on a straight up-right diagonal.
    Native,
    /// Append the standard swash flick to the final subpath.
    Swash,
}

fn build(width: f64, subpaths: &[&[(f64, f64)]], exit: Exit) -> GlyphData {
    let mut subs: Vec<Vec<(f64, f64)>> = subpaths.iter().map(|s| s.to_vec()).collect();
    let mut max_ext_em = match exit {
        Exit::Swash => {
            swash(subs.last_mut().expect("glyph has strokes"));
            0.6
        }
        Exit::Native => 0.25,
    };
    // A stubby final subpath (a dot) is no launch ramp — never stretch it.
    let last = subs.last().expect("glyph has strokes");
    let total: f64 = last
        .windows(2)
        .map(|w| ((w[1].0 - w[0].0).powi(2) + (w[1].1 - w[0].1).powi(2)).sqrt())
        .sum();
    if total < 0.5 {
        max_ext_em = 0.0;
    }
    GlyphData {
        width,
        subpaths: subs,
        max_ext_em,
    }
}

/// Look up a glyph (case-folded). `None` for unsupported characters (the planner skips them with a
/// warning) — and for space, which is pure advance (see [`SPACE_WIDTH`]).
pub fn glyph(c: char) -> Option<GlyphData> {
    use Exit::{Native, Swash};
    Some(match c.to_ascii_uppercase() {
        // A: down the left leg, crossbar out and back, full right leg, then repaint the left leg —
        // its final ascent is the native launch diagonal, cut at the apex.
        'A' => build(
            0.62,
            &[&[
                (0.31, 1.0),
                (0.0, 0.0),
                (0.109, 0.35),
                (0.512, 0.35),
                (0.62, 0.0),
                (0.31, 1.0),
                (0.0, 0.0),
                (0.31, 1.0),
            ]],
            Native,
        ),
        'B' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.5, 1.0),
                (0.62, 0.84),
                (0.62, 0.62),
                (0.48, 0.5),
                (0.0, 0.5),
                (0.48, 0.5),
                (0.62, 0.38),
                (0.62, 0.14),
                (0.48, 0.0),
                (0.0, 0.0),
                (0.48, 0.0),
                (0.62, 0.14),
                (0.62, 0.38),
            ]],
            Swash,
        ),
        // C: flick-serif entry, top hook, sweep down around, ending at the lower-right terminal —
        // the swash flicks up out of the C's mouth.
        'C' => build(
            0.6,
            &[&[
                (0.10, 1.06),
                (0.16, 0.93),
                (0.38, 1.0),
                (0.57, 0.9),
                (0.38, 1.0),
                (0.16, 0.93),
                (0.03, 0.72),
                (0.0, 0.5),
                (0.03, 0.28),
                (0.16, 0.07),
                (0.38, 0.0),
                (0.57, 0.1),
            ]],
            Swash,
        ),
        'D' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.42, 1.0),
                (0.6, 0.82),
                (0.62, 0.5),
                (0.6, 0.18),
                (0.42, 0.0),
                (0.0, 0.0),
                (0.42, 0.0),
                (0.6, 0.18),
                (0.62, 0.5),
            ]],
            Swash,
        ),
        'E' => build(
            0.6,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.58, 1.0),
                (0.0, 1.0),
                (0.0, 0.5),
                (0.5, 0.5),
                (0.0, 0.5),
                (0.0, 0.0),
                (0.58, 0.0),
            ]],
            Swash,
        ),
        'F' => build(
            0.58,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.55, 1.0),
                (0.0, 1.0),
                (0.0, 0.5),
                (0.48, 0.5),
            ]],
            Swash,
        ),
        'G' => build(
            0.62,
            &[&[
                (0.10, 1.06),
                (0.16, 0.93),
                (0.38, 1.0),
                (0.57, 0.9),
                (0.38, 1.0),
                (0.16, 0.93),
                (0.03, 0.72),
                (0.0, 0.5),
                (0.03, 0.28),
                (0.16, 0.07),
                (0.38, 0.0),
                (0.55, 0.1),
                (0.58, 0.3),
                (0.58, 0.45),
                (0.34, 0.45),
                (0.58, 0.45),
            ]],
            Swash,
        ),
        'H' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 0.5),
                (0.62, 0.5),
                (0.62, 1.0),
                (0.62, 0.0),
                (0.62, 0.5),
            ]],
            Swash,
        ),
        'I' => build(0.14, &[&[(0.07, 1.0), (0.07, 0.0)]], Swash),
        // J: serif-bar entry at the top LEFT (a bare top-right entry would make every hop into J
        // overfly the whole glyph), stem, hook out and back, swash off the stem base.
        'J' => build(
            0.55,
            &[&[
                (-0.03, 1.12),
                (0.0, 1.0),
                (0.5, 1.0),
                (0.5, 0.18),
                (0.38, 0.03),
                (0.2, 0.0),
                (0.06, 0.08),
                (0.0, 0.25),
                (0.06, 0.08),
                (0.2, 0.0),
                (0.38, 0.03),
                (0.5, 0.18),
            ]],
            Swash,
        ),
        'K' => build(
            0.6,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 0.5),
                (0.55, 0.0),
                (0.0, 0.5),
                (0.55, 1.0),
            ]],
            Native,
        ),
        'L' => build(0.55, &[&[(0.0, 1.0), (0.0, 0.0), (0.55, 0.0)]], Swash),
        // M: strokes in order, then repaint the second diagonal — the native ramp.
        'M' => build(
            0.82,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.41, 0.25),
                (0.82, 1.0),
                (0.82, 0.0),
                (0.82, 1.0),
                (0.41, 0.25),
                (0.82, 1.0),
            ]],
            Native,
        ),
        'N' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.62, 0.0),
                (0.62, 1.0),
                (0.62, 0.45),
            ]],
            Swash,
        ),
        'O' => build(
            0.62,
            &[&[
                (0.13, 1.06),
                (0.19, 0.93),
                (0.42, 1.0),
                (0.57, 0.88),
                (0.62, 0.65),
                (0.62, 0.35),
                (0.57, 0.12),
                (0.42, 0.0),
                (0.19, 0.07),
                (0.05, 0.28),
                (0.0, 0.5),
                (0.05, 0.72),
                (0.19, 0.93),
                (0.05, 0.72),
                (0.0, 0.5),
                (0.05, 0.28),
                (0.19, 0.07),
                (0.42, 0.0),
                (0.57, 0.12),
                (0.62, 0.35),
            ]],
            Swash,
        ),
        'P' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.5, 1.0),
                (0.62, 0.84),
                (0.62, 0.62),
                (0.48, 0.46),
                (0.0, 0.46),
                (0.48, 0.46),
                (0.62, 0.62),
            ]],
            Swash,
        ),
        'Q' => build(
            0.62,
            &[&[
                (0.13, 1.06),
                (0.19, 0.93),
                (0.42, 1.0),
                (0.57, 0.88),
                (0.62, 0.65),
                (0.62, 0.35),
                (0.57, 0.12),
                (0.42, 0.0),
                (0.19, 0.07),
                (0.05, 0.28),
                (0.0, 0.5),
                (0.05, 0.72),
                (0.19, 0.93),
                (0.05, 0.72),
                (0.0, 0.5),
                (0.05, 0.28),
                (0.19, 0.07),
                (0.42, 0.0),
                (0.66, -0.14),
                (0.42, 0.0),
                (0.57, 0.12),
                (0.62, 0.35),
            ]],
            Swash,
        ),
        'R' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.0),
                (0.0, 1.0),
                (0.5, 1.0),
                (0.62, 0.84),
                (0.62, 0.62),
                (0.48, 0.46),
                (0.0, 0.46),
                (0.3, 0.46),
                (0.62, 0.0),
            ]],
            Swash,
        ),
        'S' => build(
            0.6,
            &[&[
                (0.57, 1.04),
                (0.55, 0.9),
                (0.38, 1.0),
                (0.16, 0.93),
                (0.05, 0.78),
                (0.09, 0.62),
                (0.25, 0.53),
                (0.42, 0.47),
                (0.55, 0.38),
                (0.58, 0.22),
                (0.48, 0.06),
                (0.28, 0.0),
                (0.1, 0.06),
                (0.03, 0.2),
                (0.1, 0.06),
                (0.28, 0.0),
                (0.48, 0.06),
                (0.58, 0.22),
            ]],
            Swash,
        ),
        // T: stem (clean steep entry), crossbar both ways, then back down the stem — the swash is a
        // cursive-style tail off the stem base.
        'T' => build(
            0.6,
            &[&[
                (0.3, 1.0),
                (0.3, 0.0),
                (0.3, 1.0),
                (0.0, 1.0),
                (0.6, 1.0),
                (0.3, 1.0),
                (0.3, 0.0),
            ]],
            Swash,
        ),
        'U' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.0, 0.2),
                (0.08, 0.04),
                (0.31, 0.0),
                (0.54, 0.04),
                (0.62, 0.2),
                (0.62, 1.0),
                (0.62, 0.45),
            ]],
            Swash,
        ),
        'V' => build(0.62, &[&[(0.0, 1.0), (0.31, 0.0), (0.62, 1.0)]], Native),
        'W' => build(
            0.9,
            &[&[
                (0.0, 1.0),
                (0.21, 0.0),
                (0.45, 0.62),
                (0.69, 0.0),
                (0.9, 1.0),
            ]],
            Native,
        ),
        'X' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.62, 0.0),
                (0.31, 0.5),
                (0.0, 0.0),
                (0.31, 0.5),
                (0.62, 1.0),
            ]],
            Native,
        ),
        'Y' => build(
            0.62,
            &[&[
                (0.0, 1.0),
                (0.31, 0.5),
                (0.31, 0.0),
                (0.31, 0.5),
                (0.62, 1.0),
            ]],
            Native,
        ),
        'Z' => build(
            0.6,
            &[&[
                (-0.05, 1.14),
                (0.0, 1.0),
                (0.6, 1.0),
                (0.0, 0.0),
                (0.6, 0.0),
                (0.0, 0.0),
                (0.6, 1.0),
            ]],
            Native,
        ),

        '0' => build(
            0.58,
            &[&[
                (0.12, 1.06),
                (0.18, 0.93),
                (0.39, 1.0),
                (0.53, 0.88),
                (0.58, 0.65),
                (0.58, 0.35),
                (0.53, 0.12),
                (0.39, 0.0),
                (0.18, 0.07),
                (0.05, 0.28),
                (0.0, 0.5),
                (0.05, 0.72),
                (0.18, 0.93),
                (0.05, 0.72),
                (0.0, 0.5),
                (0.05, 0.28),
                (0.18, 0.07),
                (0.39, 0.0),
                (0.53, 0.12),
                (0.58, 0.35),
            ]],
            Swash,
        ),
        // 1: flag first (a descending entry), back to the top, stem down, swash off the base.
        '1' => build(
            0.44,
            &[&[(0.32, 1.0), (0.08, 0.78), (0.32, 1.0), (0.32, 0.0)]],
            Swash,
        ),
        '2' => build(
            0.6,
            &[&[
                (0.02, 0.96),
                (0.06, 0.84),
                (0.18, 0.97),
                (0.42, 1.0),
                (0.56, 0.88),
                (0.58, 0.72),
                (0.48, 0.5),
                (0.0, 0.0),
                (0.58, 0.0),
            ]],
            Swash,
        ),
        '3' => build(
            0.6,
            &[&[
                (0.03, 0.97),
                (0.07, 0.85),
                (0.2, 0.96),
                (0.42, 1.0),
                (0.56, 0.86),
                (0.56, 0.68),
                (0.44, 0.55),
                (0.22, 0.51),
                (0.44, 0.55),
                (0.58, 0.42),
                (0.6, 0.22),
                (0.5, 0.06),
                (0.3, 0.0),
                (0.12, 0.04),
                (0.03, 0.16),
                (0.12, 0.04),
                (0.3, 0.0),
                (0.5, 0.06),
                (0.6, 0.22),
            ]],
            Swash,
        ),
        // 4: diagonal, bar out and back, stem down and back, then ride the diagonal up — native.
        '4' => build(
            0.62,
            &[&[
                (0.44, 1.0),
                (0.06, 0.3),
                (0.62, 0.3),
                (0.44, 0.3),
                (0.44, 0.0),
                (0.44, 0.3),
                (0.06, 0.3),
                (0.44, 1.0),
            ]],
            Native,
        ),
        '5' => build(
            0.6,
            &[&[
                (0.06, 1.0),
                (0.06, 0.55),
                (0.06, 1.0),
                (0.58, 1.0),
                (0.06, 1.0),
                (0.06, 0.55),
                (0.35, 0.58),
                (0.55, 0.45),
                (0.58, 0.25),
                (0.45, 0.05),
                (0.22, 0.0),
                (0.06, 0.1),
                (0.22, 0.0),
                (0.45, 0.05),
                (0.58, 0.25),
            ]],
            Swash,
        ),
        '6' => build(
            0.6,
            &[&[
                (0.5, 1.06),
                (0.44, 0.94),
                (0.24, 0.75),
                (0.1, 0.54),
                (0.03, 0.3),
                (0.1, 0.1),
                (0.3, 0.0),
                (0.5, 0.06),
                (0.58, 0.24),
                (0.58, 0.42),
                (0.44, 0.52),
                (0.22, 0.52),
                (0.08, 0.42),
                (0.03, 0.3),
                (0.1, 0.1),
                (0.3, 0.0),
                (0.5, 0.06),
                (0.58, 0.24),
                (0.58, 0.42),
            ]],
            Swash,
        ),
        '7' => build(
            0.6,
            &[&[
                (-0.05, 1.14),
                (0.0, 1.0),
                (0.6, 1.0),
                (0.24, 0.0),
                (0.6, 1.0),
            ]],
            Native,
        ),
        '8' => build(
            0.62,
            &[&[
                (0.27, 1.08),
                (0.31, 1.0),
                (0.12, 0.93),
                (0.04, 0.78),
                (0.12, 0.62),
                (0.31, 0.54),
                (0.1, 0.46),
                (0.02, 0.28),
                (0.12, 0.08),
                (0.31, 0.0),
                (0.5, 0.08),
                (0.6, 0.28),
                (0.5, 0.46),
                (0.31, 0.54),
                (0.5, 0.62),
                (0.58, 0.78),
                (0.5, 0.93),
                (0.31, 1.0),
                (0.5, 0.93),
                (0.58, 0.78),
                (0.5, 0.62),
                (0.31, 0.54),
                (0.5, 0.46),
                (0.6, 0.28),
            ]],
            Swash,
        ),
        // 9: loop, then the tail down and back up, continuing onto the loop's right side (repaint)
        // — the retraced tail + loop edge make a long native ramp.
        '9' => build(
            0.6,
            &[&[
                (0.24, 1.10),
                (0.28, 0.98),
                (0.1, 0.9),
                (0.02, 0.72),
                (0.06, 0.56),
                (0.22, 0.48),
                (0.42, 0.5),
                (0.55, 0.62),
                (0.58, 0.78),
                (0.5, 0.93),
                (0.28, 0.98),
                (0.5, 0.93),
                (0.58, 0.78),
                (0.55, 0.62),
                (0.44, 0.2),
                (0.4, 0.0),
                (0.44, 0.2),
                (0.55, 0.62),
                (0.58, 0.78),
            ]],
            Native,
        ),

        // Dots are drawn as open diamonds (the plume blooms to ~80 m radius — a missing edge
        // vanishes). Entries descend from the right vertex; exits ascend to the top vertex.
        // In `!` `?` `:` the dot sits right of where the shear puts the exit above it, so the
        // downhill in-glyph hop solves — italic typography, enforced by ballistics.
        '.' => build(
            0.2,
            &[&[(0.16, 0.09), (0.1, 0.02), (0.04, 0.09), (0.1, 0.16)]],
            Native,
        ),
        ',' => build(
            0.2,
            &[&[(0.12, 0.14), (0.12, 0.0), (0.02, -0.18), (0.12, 0.0)]],
            Native,
        ),
        '-' => build(0.5, &[&[(0.03, 0.62), (0.03, 0.5), (0.47, 0.5)]], Swash),
        '!' => build(
            0.36,
            &[
                &[(0.1, 1.0), (0.1, 0.3), (0.1, 1.0)],
                &[(0.36, 0.09), (0.3, 0.02), (0.24, 0.09), (0.3, 0.16)],
            ],
            Native,
        ),
        '?' => build(
            0.55,
            &[
                &[
                    (0.02, 0.94),
                    (0.06, 0.82),
                    (0.16, 0.94),
                    (0.34, 1.0),
                    (0.5, 0.9),
                    (0.54, 0.74),
                    (0.46, 0.56),
                    (0.28, 0.44),
                    (0.28, 0.3),
                    (0.28, 0.44),
                    (0.46, 0.56),
                    (0.54, 0.74),
                ],
                &[(0.48, 0.09), (0.42, 0.02), (0.36, 0.09), (0.42, 0.16)],
            ],
            Native,
        ),
        '\'' => build(0.16, &[&[(0.08, 1.06), (0.02, 0.84), (0.08, 1.06)]], Native),
        ':' => build(
            0.3,
            &[
                &[(0.16, 0.66), (0.1, 0.59), (0.04, 0.66), (0.1, 0.73)],
                &[(0.25, 0.09), (0.19, 0.02), (0.13, 0.09), (0.19, 0.16)],
            ],
            Native,
        ),
        _ => return None,
    })
}

/// Every supported non-space character (for validation tests and the UI's "supported" hint).
pub const SUPPORTED: &str = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,-!?':";

#[cfg(test)]
mod tests {
    use super::*;

    fn sheared_dir(a: (f64, f64), b: (f64, f64)) -> (f64, f64) {
        let dx = (b.0 + SLANT * b.1) - (a.0 + SLANT * a.1);
        let dy = b.1 - a.1;
        let l = (dx * dx + dy * dy).sqrt();
        (dx / l, dy / l)
    }

    /// Entry rule: the first segment of every subpath descends (it absorbs the steep ballistic
    /// arrival), and is non-degenerate.
    #[test]
    fn every_subpath_entry_descends() {
        for c in SUPPORTED.chars() {
            let g = glyph(c).unwrap();
            for (i, sub) in g.subpaths.iter().enumerate() {
                assert!(sub.len() >= 2, "{c:?} subpath {i} too short");
                let (_, dy) = sheared_dir(sub[0], sub[1]);
                assert!(dy < -0.05, "{c:?} subpath {i} entry must descend (dy={dy})");
            }
        }
    }

    /// Exit rule: the final segment of every subpath ascends and — after the italic shear — leans
    /// right, so it can serve as a ballistic launch ramp for a rightward hop.
    #[test]
    fn every_subpath_exit_ascends_rightward() {
        for c in SUPPORTED.chars() {
            let g = glyph(c).unwrap();
            for (i, sub) in g.subpaths.iter().enumerate() {
                let n = sub.len();
                let (dx, dy) = sheared_dir(sub[n - 2], sub[n - 1]);
                assert!(dy > 0.02, "{c:?} subpath {i} exit must ascend (dy={dy})");
                assert!(
                    dx > 0.005,
                    "{c:?} subpath {i} exit must lean right after shear (dx={dx})"
                );
            }
        }
    }

    /// The launch ramp of every glyph's *last* subpath must be a decent straight run: ≥ 0.35 em
    /// within 15° of the exit direction. (Dots are exempt — they're flagged for assist/terminal
    /// use by the planner.)
    #[test]
    fn last_subpath_has_a_straight_ramp() {
        for c in SUPPORTED.chars() {
            if ".,'?!:".contains(c) {
                continue;
            }
            let g = glyph(c).unwrap();
            let sub = g.subpaths.last().unwrap();
            let n = sub.len();
            let exit = sheared_dir(sub[n - 2], sub[n - 1]);
            let mut run = 0.0;
            for i in (1..n).rev() {
                let d = sheared_dir(sub[i - 1], sub[i]);
                let cosang = d.0 * exit.0 + d.1 * exit.1;
                if cosang < 15f64.to_radians().cos() {
                    break;
                }
                let (dx, dy) = (
                    (sub[i].0 + SLANT * sub[i].1) - (sub[i - 1].0 + SLANT * sub[i - 1].1),
                    sub[i].1 - sub[i - 1].1,
                );
                run += (dx * dx + dy * dy).sqrt();
            }
            assert!(run >= 0.35, "{c:?} straight launch run only {run:.2} em");
        }
    }

    /// Segments must all be non-degenerate (zero-length segments break direction math).
    #[test]
    fn no_zero_length_segments() {
        for c in SUPPORTED.chars() {
            let g = glyph(c).unwrap();
            for sub in &g.subpaths {
                for w in sub.windows(2) {
                    let (dx, dy) = (w[1].0 - w[0].0, w[1].1 - w[0].1);
                    let len = (dx * dx + dy * dy).sqrt();
                    assert!(len > 0.01, "{c:?} has a near-zero segment {w:?}");
                }
            }
        }
    }

    /// Glyph geometry stays inside a sane box (swashes/flicks may poke out).
    #[test]
    fn glyphs_stay_in_box() {
        for c in SUPPORTED.chars() {
            let g = glyph(c).unwrap();
            for sub in &g.subpaths {
                for &(x, y) in sub {
                    assert!((-0.1..=1.1).contains(&x), "{c:?} x={x} out of box");
                    assert!((-0.25..=1.2).contains(&y), "{c:?} y={y} out of box");
                }
            }
        }
    }

    /// Render a few glyphs as ASCII art — an eyeball check that the paths draw plausible
    /// letterforms. `cargo test font::tests::ascii_preview -- --nocapture` to look.
    #[test]
    fn ascii_preview() {
        let cols = 18usize;
        let rows = 12usize;
        for c in "BAR9!".chars() {
            let g = glyph(c).unwrap();
            let mut grid = vec![vec![' '; cols]; rows];
            for sub in &g.subpaths {
                for w in sub.windows(2) {
                    for k in 0..=24 {
                        let t = k as f64 / 24.0;
                        let x = w[0].0 + (w[1].0 - w[0].0) * t;
                        let y = w[0].1 + (w[1].1 - w[0].1) * t;
                        let cx = (x / 1.3 * (cols as f64 - 1.0) + 0.8).round() as i64;
                        let cy = ((1.15 - y) / 1.45 * (rows as f64 - 1.0)).round() as i64;
                        if (0..cols as i64).contains(&cx) && (0..rows as i64).contains(&cy) {
                            grid[cy as usize][cx as usize] = '#';
                        }
                    }
                }
            }
            println!("--- {c}");
            for row in grid {
                println!("{}", row.iter().collect::<String>());
            }
        }
    }
}
