//! The flight plan: text → a timed sequence of pen-down strokes and ballistic pen-up hops, all in
//! 2-D canvas coordinates (meters; `a` along the text, `b` up). Pure math, no I/O — unit-tested on
//! the host, executed by `flight` against the live sim.
//!
//! The physics that shapes a plan:
//! - **Painting = thrusting.** While pen-down the vehicle hovers on its plume; available path
//!   acceleration depends on direction (vertical strokes are throttle-only; horizontal acceleration
//!   tilts the thrust vector, capped by `tilt_max` so the flight computer's slew-rate-limited
//!   attitude hold can keep up).
//! - **Pen-up = ballistic.** With the engine off nothing can change the velocity, so a hop from one
//!   stroke to the next is fully determined by its launch: the plan rides the previous glyph's final
//!   ascending stroke (the *ramp*, repainting it — invisible), releases at a solved speed, and the
//!   arc delivers the vehicle onto the next glyph's descending entry stroke.
//! - **Corners stop.** Sharp corners need a thrust-vector swing; the attitude hold slews at a few
//!   deg/s, so the plan parks at the vertex (painting a dot *on* the letter) for the swing duration.

use crate::font;

/// Environment + tuning the plan is built against. All SI; accelerations are specific force (m/s²).
#[derive(Clone, Copy, Debug)]
pub struct PlanEnv {
    /// Local gravity at the writing altitude (μ/r² — never 9.8 by assumption).
    pub g: f64,
    /// Max thrust specific force `f_max = TWR·g` (vacuum thrust / current mass).
    pub accel_max: f64,
    /// Min thrust specific force while lit (`min_throttle · f_max`): KSA floors a firing engine's
    /// throttle, so a painting vehicle cannot push less than this.
    pub accel_min: f64,
    /// Max thrust-vector tilt from local vertical while painting, deg. Keeps every attitude move
    /// small enough for the FC's slew-rate-limited hold (~5°/s default profile).
    pub tilt_max_deg: f64,
    /// FC attitude slew rate, deg/s — sizes corner dwells.
    pub slew_dps: f64,
    /// Cruise draw speed along strokes, m/s.
    pub v_draw: f64,
    /// Letter height (cap height), m.
    pub height: f64,
    /// Gap between letters / words, em (of `height`).
    pub letter_gap_em: f64,
    pub word_gap_em: f64,
}

impl Default for PlanEnv {
    fn default() -> Self {
        PlanEnv {
            g: 9.81,
            accel_max: 2.2 * 9.81,
            accel_min: 0.1 * 2.2 * 9.81,
            tilt_max_deg: 12.0,
            slew_dps: 5.0,
            v_draw: 70.0,
            height: 900.0,
            letter_gap_em: 0.34,
            word_gap_em: 0.95,
        }
    }
}

/// One pen-down polyline with its speed/time profile.
#[derive(Clone, Debug)]
pub struct Stroke {
    /// Index into [`Plan::letters`].
    pub letter: usize,
    /// Canvas vertices, meters.
    pub pts: Vec<(f64, f64)>,
    /// Speed at each vertex (m/s). `v[0]` is the hop-arrival speed it must brake from.
    pub v: Vec<f64>,
    /// Cumulative time at each vertex from stroke start (s), including dwells.
    pub t: Vec<f64>,
    /// Dwell (hold-at-vertex) time *at* each vertex, s — nonzero at sharp corners.
    pub dwell: Vec<f64>,
    /// Per-segment kinematics (between vertices i and i+1) for reference interpolation.
    pub segs: Vec<Seg>,
    pub duration: f64,
    /// Path length, m.
    pub length: f64,
}

/// A segment's trapezoidal speed plan: accelerate `v0→peak` at `a_f`, cruise, brake `peak→v1` at
/// `a_b`, covering length `l` along unit direction `dir`.
#[derive(Clone, Copy, Debug)]
pub struct Seg {
    pub dir: (f64, f64),
    pub l: f64,
    pub v0: f64,
    pub v1: f64,
    pub peak: f64,
    pub a_f: f64,
    pub a_b: f64,
    pub t_acc: f64,
    pub t_cruise: f64,
    pub t_dec: f64,
}

impl Seg {
    pub fn t_total(&self) -> f64 {
        self.t_acc + self.t_cruise + self.t_dec
    }

    /// (distance along segment, speed, tangential acceleration) at `tau ∈ [0, t_total]`.
    pub fn sample(&self, tau: f64) -> (f64, f64, f64) {
        let tau = tau.clamp(0.0, self.t_total());
        if tau < self.t_acc {
            (
                self.v0 * tau + 0.5 * self.a_f * tau * tau,
                self.v0 + self.a_f * tau,
                self.a_f,
            )
        } else if tau < self.t_acc + self.t_cruise {
            let d0 = self.v0 * self.t_acc + 0.5 * self.a_f * self.t_acc * self.t_acc;
            (d0 + self.peak * (tau - self.t_acc), self.peak, 0.0)
        } else {
            let d0 = self.v0 * self.t_acc
                + 0.5 * self.a_f * self.t_acc * self.t_acc
                + self.peak * self.t_cruise;
            let td = tau - self.t_acc - self.t_cruise;
            (
                d0 + self.peak * td - 0.5 * self.a_b * td * td,
                self.peak - self.a_b * td,
                -self.a_b,
            )
        }
    }
}

/// The tracker's reference state at `t_local` seconds into a stroke.
#[derive(Clone, Copy, Debug)]
pub struct RefState {
    pub pos: (f64, f64),
    pub vel: (f64, f64),
    pub acc: (f64, f64),
    /// Distance painted so far, m (for progress display).
    pub dist: f64,
}

impl Stroke {
    /// Interpolate the reference state at `t_local` (clamped to the stroke's duration). Dwells hold
    /// the vertex with zero velocity; segments follow their trapezoid. Past the end, the reference
    /// is the final vertex at the exit speed (the launch condition a ramp cut wants).
    pub fn sample(&self, t_local: f64) -> RefState {
        let n = self.pts.len();
        let mut dist = 0.0;
        for i in 0..n - 1 {
            let seg_start = self.t[i] + self.dwell[i];
            if t_local < seg_start {
                return RefState {
                    pos: self.pts[i],
                    vel: (0.0, 0.0),
                    acc: (0.0, 0.0),
                    dist,
                };
            }
            let seg = &self.segs[i];
            if t_local <= seg_start + seg.t_total() || i == n - 2 {
                let (d, s, a) = seg.sample(t_local - seg_start);
                let d = d.clamp(0.0, seg.l);
                return RefState {
                    pos: (self.pts[i].0 + seg.dir.0 * d, self.pts[i].1 + seg.dir.1 * d),
                    vel: (seg.dir.0 * s, seg.dir.1 * s),
                    acc: (seg.dir.0 * a, seg.dir.1 * a),
                    dist: dist + d,
                };
            }
            dist += seg.l;
        }
        RefState {
            pos: self.pts[n - 1],
            vel: (0.0, 0.0),
            acc: (0.0, 0.0),
            dist: self.length,
        }
    }
}

/// A glyph placement candidate during layout: (advance em, placed subpaths, hop into it).
type PlacedCandidate = (f64, Vec<Vec<(f64, f64)>>, Option<Hop>);
/// A scored hop candidate: (score, previous stroke pts (possibly extended), advance em, placed
/// subpaths, the hop).
type HopCandidate = (f64, Vec<(f64, f64)>, f64, Vec<Vec<(f64, f64)>>, Hop);

/// One ballistic pen-up hop. Everything is decided at the cut: `v1` (launch velocity along the exit
/// ramp), then `to = from + v1·T − ½gT²·ĵ` with arrival velocity `v2`.
#[derive(Clone, Copy, Debug)]
pub struct Hop {
    pub from: (f64, f64),
    pub to: (f64, f64),
    pub v1: (f64, f64),
    pub v2: (f64, f64),
    pub t_flight: f64,
    /// Highest point of the arc, canvas b (for the altitude envelope).
    pub apex_b: f64,
    /// True when unsolvable within the exit ramp's capacity (e.g. hopping *up* out of a period's
    /// tiny diamond): the flight layer needs the impulse cheat, or the user re-words the text.
    pub assist: bool,
}

#[derive(Clone, Debug)]
pub enum Item {
    Stroke(Stroke),
    Hop(Hop),
}

#[derive(Clone, Copy, Debug)]
pub struct Letter {
    pub ch: char,
    /// Plan-time window (game seconds from writing start).
    pub t_start: f64,
    pub t_end: f64,
}

#[derive(Clone, Debug, Default)]
pub struct Plan {
    pub items: Vec<Item>,
    pub letters: Vec<Letter>,
    /// Total game-time to write everything (paint + hops + dwells), s.
    pub total_time: f64,
    /// Pen-down (engine burning) game-time, s — the propellant driver.
    pub paint_time: f64,
    pub warnings: Vec<String>,
    /// Canvas bounds (a_min, a_max, b_min, b_max) including hop arcs.
    pub bbox: (f64, f64, f64, f64),
}

// ---- thrust envelope ----------------------------------------------------------------------------

/// Launch ramps may tilt the thrust vector this far off vertical: a ramp is one *straight* run
/// flown at one constant attitude (the swing into it is absorbed by the corner dwell before it),
/// so the slew-rate argument that caps tilt during curved tracking doesn't apply there.
pub const RAMP_TILT_DEG: f64 = 30.0;

/// Max acceleration along unit direction `d` (canvas 2-D) with thrust constrained to a cone of
/// `tilt_max` about vertical and magnitude within `[accel_min, accel_max]`. The thrust needed for
/// path acceleration `q` along `d` under gravity is `t = q·d + g·ĵ`; this returns the largest
/// feasible `q ≥ 0`.
pub fn accel_along(d: (f64, f64), env: &PlanEnv) -> f64 {
    accel_along_tilt(d, env, env.tilt_max_deg)
}

pub fn accel_along_tilt(d: (f64, f64), env: &PlanEnv, tilt_deg: f64) -> f64 {
    let (da, db) = (d.0.abs(), d.1); // lateral magnitude, vertical (signed)
    let tan_m = tilt_deg.to_radians().tan();

    // Tilt cone: q·da ≤ (q·db + g)·tan_m, with the vertical component q·db + g staying positive.
    let coeff = da - db * tan_m;
    let q_tilt = if coeff > 1e-9 {
        env.g * tan_m / coeff
    } else {
        f64::INFINITY
    };
    // Vertical-component floor: while painting the engine can't push less than accel_min, and the
    // thrust must keep pointing up-ish — for downward directions this caps q near (g − f_min)/|db|.
    // The 1.25 margin keeps the plan off the exact throttle floor so the tracker has correction
    // authority left while descending.
    let q_vert = if db < -1e-9 {
        (env.g - 1.25 * env.accel_min - 0.2).max(0.0) / (-db)
    } else {
        f64::INFINITY
    };
    // Magnitude ceiling: |q·d + g·ĵ|² ≤ f_max² → q² + 2q·g·db + g² − f_max² ≤ 0.
    let disc = env.accel_max * env.accel_max - env.g * env.g * (1.0 - db * db);
    let q_mag = if disc > 0.0 {
        -env.g * db + disc.sqrt()
    } else {
        0.0
    };

    q_tilt.min(q_vert).min(q_mag).max(0.2)
}

/// Thrust vector (canvas) for path acceleration `q` along `d` under gravity — what the vehicle must
/// point along. Used for corner-dwell sizing.
fn thrust_for(q: f64, d: (f64, f64), g: f64) -> (f64, f64) {
    (q * d.0, q * d.1 + g)
}

fn angle_between(a: (f64, f64), b: (f64, f64)) -> f64 {
    let na = (a.0 * a.0 + a.1 * a.1).sqrt();
    let nb = (b.0 * b.0 + b.1 * b.1).sqrt();
    if na < 1e-9 || nb < 1e-9 {
        return 0.0;
    }
    ((a.0 * b.0 + a.1 * b.1) / (na * nb))
        .clamp(-1.0, 1.0)
        .acos()
}

// ---- hop solving --------------------------------------------------------------------------------

/// The straight launch run at a stroke's end: walk backward while the path stays within 15° of the
/// exit direction (a launch ramp is flown at one attitude — it must be straight; the FC can't slew
/// around a bend while accelerating hard).
fn ramp_run(pts: &[(f64, f64)]) -> (f64, usize) {
    let n = pts.len();
    let exit = seg_dir(pts[n - 2], pts[n - 1]);
    let mut length = 0.0;
    let mut segs = 0;
    for i in (1..n).rev() {
        let d = seg_dir(pts[i - 1], pts[i]);
        if angle_between(d, exit) > 15f64.to_radians() {
            break;
        }
        length += seg_len(pts[i - 1], pts[i]);
        segs += 1;
    }
    (length, segs)
}

/// Ballistic capacity of a stroke's exit ramp: the speed the vehicle can build along the straight
/// launch run at the relaxed ramp tilt, with an 8% planning margin.
fn ramp_speed_cap(pts: &[(f64, f64)], env: &PlanEnv) -> f64 {
    let n = pts.len();
    let exit = seg_dir(pts[n - 2], pts[n - 1]);
    let (length, _) = ramp_run(pts);
    let a = accel_along_tilt(exit, env, RAMP_TILT_DEG);
    0.92 * (2.0 * a * length).sqrt()
}

fn seg_dir(a: (f64, f64), b: (f64, f64)) -> (f64, f64) {
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    let l = (dx * dx + dy * dy).sqrt();
    (dx / l, dy / l)
}

fn seg_len(a: (f64, f64), b: (f64, f64)) -> f64 {
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    (dx * dx + dy * dy).sqrt()
}

/// Solve the hop from `p1` launching along `d1` (unit, the exit-ramp tangent) to `p2`. The launch
/// line must pass above the target ("aim high, gravity drops you on"); the solution is unique:
/// `α = Δa/d1a`, `T = √(2(α·d1b − Δb)/g)`, `s1 = α/T`.
fn solve_hop(p1: (f64, f64), d1: (f64, f64), p2: (f64, f64), s1_cap: f64, g: f64) -> Option<Hop> {
    let (dx, dy) = (p2.0 - p1.0, p2.1 - p1.1);
    if d1.0 < 0.005 {
        return None; // exit doesn't lean toward the target side
    }
    let alpha = dx / d1.0;
    let disc = alpha * d1.1 - dy;
    if alpha < 1.0 || disc < 0.5 {
        return None;
    }
    let t = (2.0 * disc / g).sqrt();
    if !(0.8..=180.0).contains(&t) {
        return None;
    }
    let s1 = alpha / t;
    if s1 > s1_cap {
        return None;
    }
    let v1 = (s1 * d1.0, s1 * d1.1);
    let v2 = (v1.0, v1.1 - g * t);
    if v2.1 > -0.5 {
        return None; // must arrive descending (past apex) so the entry stroke can brake it
    }
    let apex_b = if v1.1 > 0.0 {
        p1.1 + v1.1 * v1.1 / (2.0 * g)
    } else {
        p1.1
    };
    Some(Hop {
        from: p1,
        to: p2,
        v1,
        v2,
        t_flight: t,
        apex_b,
        assist: false,
    })
}

// ---- speed profile ------------------------------------------------------------------------------

/// Mid-band corner slowdown (15–25° direction change): round it within ~12 m of the vertex — well
/// under the 80 m plume bloom — using the tilt-limited lateral authority.
fn corner_cap(dth: f64, env: &PlanEnv) -> f64 {
    let a_lat = env.g * env.tilt_max_deg.to_radians().tan();
    let r = 12.0 / (1.0 - (dth / 2.0).cos()).max(1e-6);
    (a_lat * r).sqrt().min(env.v_draw)
}

/// Build the vertex speed/time profile for a stroke: cap speeds at corners, run the classic
/// forward/backward acceleration-limit passes, then integrate segment times (+ dwells).
///
/// The trailing straight launch run (the ramp) gets the relaxed [`RAMP_TILT_DEG`] forward
/// acceleration — one attitude, full thrust — which is what makes the exit speed reachable.
fn profile(
    pts: &[(f64, f64)],
    v_entry: f64,
    v_exit: f64,
    env: &PlanEnv,
    warn: &mut Vec<String>,
    tag: &str,
) -> Stroke {
    let n = pts.len();

    // Per-segment accelerations, with the ramp segments relaxed.
    let (_, ramp_segs) = ramp_run(pts);
    let mut a_fwd = Vec::with_capacity(n - 1);
    let mut a_bwd = Vec::with_capacity(n - 1);
    for i in 0..n - 1 {
        let d = seg_dir(pts[i], pts[i + 1]);
        let is_ramp = v_exit > 0.0 && i >= n - 1 - ramp_segs;
        let tilt = if is_ramp {
            RAMP_TILT_DEG
        } else {
            env.tilt_max_deg
        };
        // Ramp accel keeps 12% in reserve: the envelope value saturates the throttle exactly, and a
        // saturated sprint leaves the tracker nothing to correct with — the launch speed then under-
        // delivers and the hop lands short.
        let margin = if is_ramp { 0.88 } else { 1.0 };
        a_fwd.push(margin * accel_along_tilt(d, env, tilt));
        a_bwd.push(accel_along((-d.0, -d.1), env)); // braking = accelerating the other way
    }

    // Vertex caps + corner dwells sized from the actual thrust-vector swing at the FC's slew rate.
    let mut v = vec![env.v_draw; n];
    let mut dwell = vec![0.0; n];
    v[0] = v_entry;
    v[n - 1] = v_exit;
    for i in 1..n - 1 {
        let din = seg_dir(pts[i - 1], pts[i]);
        let dout = seg_dir(pts[i], pts[i + 1]);
        let dth = angle_between(din, dout);
        if dth < 3f64.to_radians() {
            continue;
        }
        // Slew-rate cap: the thrust vector must rotate ~with the path direction, and the FC turns
        // at only `slew_dps`. The turn must fit in the shorter adjacent segment's traverse time:
        // v ≤ slew·min(L_in, L_out)/Δθ. This is what makes curved bowls (S, 5, 8…) followable —
        // lateral-force limits alone would allow speeds the attitude hold can't deliver.
        let l_adj = seg_len(pts[i - 1], pts[i]).min(seg_len(pts[i], pts[i + 1]));
        let v_slew = env.slew_dps.to_radians() * l_adj / dth;
        v[i] = v[i].min(v_slew.max(6.0));
        if dth < 15f64.to_radians() {
            continue;
        }
        if dth < 25f64.to_radians() {
            v[i] = v[i].min(corner_cap(dth, env));
            continue;
        }
        v[i] = 0.0;
        let t_in = thrust_for(a_bwd[i - 1].min(4.0), (-din.0, -din.1), env.g);
        let t_out = thrust_for(a_fwd[i].min(RAMP_TILT_DEG_ACCEL_CAP), dout, env.g);
        let swing = angle_between(t_in, t_out).to_degrees();
        dwell[i] = (swing / env.slew_dps + 0.6).clamp(0.8, 12.0);
    }
    if v_entry < 0.5 {
        dwell[0] = 1.0; // settle at a hover start
    }

    // Forward pass (can we reach v[i+1] from v[i]?), then backward (can we brake for v[i]?).
    for i in 0..n - 1 {
        let l = seg_len(pts[i], pts[i + 1]);
        let reachable = (v[i] * v[i] + 2.0 * a_fwd[i] * l).sqrt();
        if v[i + 1] > reachable {
            if i + 1 == n - 1 && v_exit > 0.0 {
                warn.push(format!(
                    "{tag}: ramp reaches {reachable:.0} of {v_exit:.0} m/s"
                ));
            }
            v[i + 1] = reachable;
        }
    }
    for i in (0..n - 1).rev() {
        let l = seg_len(pts[i], pts[i + 1]);
        let allowed = (v[i + 1] * v[i + 1] + 2.0 * a_bwd[i] * l).sqrt();
        if v[i] > allowed {
            if i == 0 {
                warn.push(format!(
                    "{tag}: entry overspeed {v0:.0}→{allowed:.0} m/s (hook may show)",
                    v0 = v[0]
                ));
            }
            v[i] = allowed;
        }
    }

    // Times: trapezoid per segment (accelerate/decelerate between vertex speeds, cruise between).
    let mut t = vec![0.0; n];
    let mut segs = Vec::with_capacity(n - 1);
    let mut length = 0.0;
    for i in 0..n - 1 {
        let d = seg_dir(pts[i], pts[i + 1]);
        let l = seg_len(pts[i], pts[i + 1]);
        length += l;
        let vc = env.v_draw.max(v[i]).max(v[i + 1]);
        let seg = make_seg(d, l, v[i], v[i + 1], vc, a_fwd[i], a_bwd[i]);
        t[i + 1] = t[i] + dwell[i] + seg.t_total();
        segs.push(seg);
    }
    let duration = t[n - 1] + dwell[n - 1];
    Stroke {
        letter: 0,
        pts: pts.to_vec(),
        v,
        t,
        dwell,
        segs,
        duration,
        length,
    }
}

/// Cap used when sizing the corner dwell's outgoing thrust estimate (keeps the estimate sane even
/// on high-TWR ships).
const RAMP_TILT_DEG_ACCEL_CAP: f64 = 14.0;

/// Build a segment's trapezoid: accelerate `v0→peak` at `a_f`, cruise at `peak ≤ vc`, brake to `v1`
/// at `a_b`, covering `l`. Degenerate cases collapse to two-phase or single-phase profiles.
fn make_seg(dir: (f64, f64), l: f64, v0: f64, v1: f64, vc: f64, a_f: f64, a_b: f64) -> Seg {
    // Peak speed the segment allows (accelerate then brake, meeting in the middle).
    let peak2 = (2.0 * a_f * a_b * l + a_b * v0 * v0 + a_f * v1 * v1) / (a_f + a_b);
    let peak = peak2.max(0.0).sqrt().min(vc).max(v0.max(v1)).max(0.5);
    let d_acc = ((peak * peak - v0 * v0) / (2.0 * a_f)).max(0.0);
    let d_dec = ((peak * peak - v1 * v1) / (2.0 * a_b)).max(0.0);
    let d_cruise = (l - d_acc - d_dec).max(0.0);
    Seg {
        dir,
        l,
        v0,
        v1,
        peak,
        a_f,
        a_b,
        t_acc: (peak - v0).max(0.0) / a_f,
        t_cruise: d_cruise / peak,
        t_dec: (peak - v1).max(0.0) / a_b,
    }
}

// ---- layout + assembly --------------------------------------------------------------------------

fn mag(v: (f64, f64)) -> f64 {
    (v.0 * v.0 + v.1 * v.1).sqrt()
}

/// Lengthen a stroke's final (swash) segment by `extra` meters along its direction: the launch
/// ramp grows and its release point moves up-and-toward the next glyph.
fn extend_swash(pts: &[(f64, f64)], extra: f64) -> Vec<(f64, f64)> {
    let mut out = pts.to_vec();
    let n = out.len();
    let d = seg_dir(out[n - 2], out[n - 1]);
    out[n - 1] = (out[n - 1].0 + d.0 * extra, out[n - 1].1 + d.1 * extra);
    out
}

/// Place a glyph's subpaths at `cursor` (em), sheared and scaled to meters.
fn place(glyph: &font::GlyphData, cursor_em: f64, h: f64) -> Vec<Vec<(f64, f64)>> {
    glyph
        .subpaths
        .iter()
        .map(|sub| {
            sub.iter()
                .map(|&(x, y)| ((cursor_em + x + font::SLANT * y) * h, y * h))
                .collect()
        })
        .collect()
}

/// Compile `text` into a full flight plan. Never fails outright: unsupported characters are skipped
/// with a warning, unsolvable hops are marked `assist` (the UI decides what to do about them).
pub fn compile(text: &str, env: &PlanEnv) -> Plan {
    let mut plan = Plan::default();
    let h = env.height;

    // 1. Layout: glyph subpaths in canvas meters, with physics-checked letter gaps.
    struct Placed {
        letter: usize,
        pts: Vec<(f64, f64)>,
        /// How far (em) the final launch segment may be lengthened for a long hop.
        max_ext_em: f64,
    }
    let mut placed: Vec<Placed> = Vec::new();
    let mut hops: Vec<Option<Hop>> = Vec::new(); // hop *into* placed[i] (None for the first)
    let mut cursor_em = 0.0f64;
    let mut pending_gap = 0.0f64; // extra em from spaces / skipped chars

    for ch in text.chars() {
        if ch == ' ' {
            pending_gap += font::SPACE_WIDTH + (env.word_gap_em - env.letter_gap_em);
            continue;
        }
        let Some(glyph) = font::glyph(ch) else {
            plan.warnings.push(format!("'{ch}' unsupported — skipped"));
            continue;
        };
        let letter = plan.letters.len();
        plan.letters.push(Letter {
            ch: ch.to_ascii_uppercase(),
            t_start: 0.0,
            t_end: 0.0,
        });

        let base_em = if placed.is_empty() {
            0.0
        } else {
            cursor_em + env.letter_gap_em + pending_gap
        };
        pending_gap = 0.0;

        // Solve the hop into this glyph: widen the gap if ballistics wants more room than
        // typography (the arc must arrive *descending*), and if the previous exit is a swash, try
        // lengthening it (a word-end flourish = a longer, higher launch ramp) before giving up.
        let mut chosen: Option<PlacedCandidate> = None;
        if placed.is_empty() {
            chosen = Some((base_em, place(&glyph, base_em, h), None));
        } else {
            // Score every feasible (swash-extension, gap-widen) combination and keep the gentlest:
            // slow arrivals paint small entry hooks, but extra flourish and extra gap cost style
            // points too. All candidates are closed-form, so the sweep is cheap.
            let max_ext_em = placed.last().unwrap().max_ext_em;
            let mut best: Option<HopCandidate> = None;
            for ext in 0..=6 {
                let ext_em = ext as f64 * 0.1;
                if ext_em > max_ext_em + 1e-9 {
                    break;
                }
                let prev_pts: Vec<(f64, f64)> = {
                    let prev = &placed.last().unwrap().pts;
                    if ext == 0 {
                        prev.clone()
                    } else {
                        extend_swash(prev, ext_em * h)
                    }
                };
                let pn = prev_pts.len();
                let d1 = seg_dir(prev_pts[pn - 2], prev_pts[pn - 1]);
                let cap = ramp_speed_cap(&prev_pts, env);
                for widen in 0..=24 {
                    let at_em = base_em + widen as f64 * 0.05;
                    let subs = place(&glyph, at_em, h);
                    if let Some(hop) = solve_hop(prev_pts[pn - 1], d1, subs[0][0], cap, env.g) {
                        // What matters at arrival is the velocity component PERPENDICULAR to the
                        // entry stroke — that's the part the (tilt-limited) tracker must remove
                        // while painting. A slow arrival moving the wrong way is worse than a
                        // brisk one already sliding down the entry.
                        let d2 = seg_dir(subs[0][0], subs[0][1]);
                        let along = hop.v2.0 * d2.0 + hop.v2.1 * d2.1;
                        let perp = (hop.v2.0 - along * d2.0).hypot(hop.v2.1 - along * d2.1);
                        let backwards = (-along).max(0.0); // arriving against the stroke direction
                        let score = 3.0 * perp
                            + backwards
                            + 0.3 * mag(hop.v2)
                            + 8.0 * ext as f64
                            + 2.0 * widen as f64;
                        if best.as_ref().is_none_or(|b| score < b.0) {
                            best = Some((score, prev_pts.clone(), at_em, subs, hop));
                        }
                    }
                }
            }
            if let Some((_, prev_pts, at_em, subs, hop)) = best {
                placed.last_mut().unwrap().pts = prev_pts; // commit extension (no-op at ext 0)
                chosen = Some((at_em, subs, Some(hop)));
            }
            if chosen.is_none() {
                // Give up: mark an assist hop straight at the typographic gap.
                let subs = place(&glyph, base_em, h);
                let prev = placed.last().unwrap();
                let p1 = *prev.pts.last().unwrap();
                let p2 = subs[0][0];
                plan.warnings.push(format!(
                    "hop into '{ch}' unsolvable from previous exit — needs --allow-impulse"
                ));
                let hop = Hop {
                    from: p1,
                    to: p2,
                    v1: (0.0, 0.0),
                    v2: (0.0, -env.v_draw.min(60.0)),
                    t_flight: 6.0,
                    apex_b: p1.1.max(p2.1),
                    assist: true,
                };
                chosen = Some((base_em, subs, Some(hop)));
            }
        }
        let (at_em, subs, first_hop) = chosen.unwrap();

        // Intra-glyph hops between subpaths (authored to go downhill — always solvable, else assist).
        for (k, sub) in subs.iter().enumerate() {
            let hop_in = if k == 0 {
                first_hop
            } else {
                let prev = placed.last().unwrap();
                let pn = prev.pts.len();
                let d1 = seg_dir(prev.pts[pn - 2], prev.pts[pn - 1]);
                let cap = ramp_speed_cap(&prev.pts, env);
                Some(
                    solve_hop(prev.pts[pn - 1], d1, sub[0], cap, env.g).unwrap_or_else(|| {
                        plan.warnings.push(format!(
                            "in-glyph hop of '{ch}' unsolvable — needs --allow-impulse"
                        ));
                        Hop {
                            from: prev.pts[pn - 1],
                            to: sub[0],
                            v1: (0.0, 0.0),
                            v2: (0.0, -30.0),
                            t_flight: 4.0,
                            apex_b: prev.pts[pn - 1].1.max(sub[0].1),
                            assist: true,
                        }
                    }),
                )
            };
            if placed.is_empty() {
                hops.push(None);
            } else {
                hops.push(hop_in);
            }
            placed.push(Placed {
                letter,
                pts: sub.clone(),
                max_ext_em: if k == subs.len() - 1 {
                    glyph.max_ext_em
                } else {
                    0.0
                },
            });
        }
        cursor_em = at_em + glyph.width;
    }

    if placed.is_empty() {
        plan.warnings.push("no drawable characters".into());
        return plan;
    }

    // 2. Translate everything so the first pen-down point is the canvas origin — the vehicle's
    // hover position when writing starts *is* the first ink.
    let origin = placed[0].pts[0];
    for p in &mut placed {
        for pt in &mut p.pts {
            *pt = (pt.0 - origin.0, pt.1 - origin.1);
        }
    }
    for hop in hops.iter_mut().flatten() {
        hop.from = (hop.from.0 - origin.0, hop.from.1 - origin.1);
        hop.to = (hop.to.0 - origin.0, hop.to.1 - origin.1);
        hop.apex_b -= origin.1;
    }

    // 3. Profiles + assembly. The exit speed of each stroke is its outgoing hop's launch speed.
    let mut clock = 0.0f64;
    let mut bbox = (f64::MAX, f64::MIN, f64::MAX, f64::MIN);
    for (i, p) in placed.iter().enumerate() {
        let letter = p.letter;
        if let Some(Some(hop)) = hops.get(i).cloned() {
            clock += hop.t_flight;
            bbox.3 = bbox.3.max(hop.apex_b);
            plan.items.push(Item::Hop(hop));
        }
        let v_entry = match hops.get(i) {
            Some(Some(hop)) => mag(hop.v2),
            _ => 0.0,
        };
        let v_exit = match hops.get(i + 1) {
            Some(Some(next)) if !next.assist => mag(next.v1),
            _ => 0.0,
        };
        let tag = format!("'{}'", plan.letters[letter].ch);
        let mut stroke = profile(&p.pts, v_entry, v_exit, env, &mut plan.warnings, &tag);
        stroke.letter = letter;
        if plan.letters[letter].t_start == 0.0 && plan.letters[letter].t_end == 0.0 {
            plan.letters[letter].t_start = clock;
        }
        clock += stroke.duration;
        plan.paint_time += stroke.duration;
        plan.letters[letter].t_end = clock;
        for &(a, b) in &stroke.pts {
            bbox.0 = bbox.0.min(a);
            bbox.1 = bbox.1.max(a);
            bbox.2 = bbox.2.min(b);
            bbox.3 = bbox.3.max(b);
        }
        plan.items.push(Item::Stroke(stroke));
    }
    plan.total_time = clock;
    plan.bbox = bbox;
    plan
}

#[cfg(test)]
mod tests {
    use super::*;

    fn env() -> PlanEnv {
        PlanEnv::default()
    }

    /// Ballistic closure: every solved hop's arc actually lands on its target.
    #[test]
    fn hops_close_ballistically() {
        let plan = compile("HELLO WORLD 42", &env());
        let mut checked = 0;
        for item in &plan.items {
            if let Item::Hop(h) = item {
                if h.assist {
                    continue;
                }
                let t = h.t_flight;
                let ax = h.from.0 + h.v1.0 * t;
                let ab = h.from.1 + h.v1.1 * t - 0.5 * env().g * t * t;
                assert!((ax - h.to.0).abs() < 1e-6, "a misses: {ax} vs {}", h.to.0);
                assert!((ab - h.to.1).abs() < 1e-6, "b misses: {ab} vs {}", h.to.1);
                // v2 consistent
                assert!((h.v2.1 - (h.v1.1 - env().g * t)).abs() < 1e-9);
                assert!(h.v2.1 < 0.0, "must arrive descending");
                checked += 1;
            }
        }
        // 12 glyphs in "HELLO WORLD 42" → 11 inter-glyph hops, all solvable.
        assert!(
            checked >= 11,
            "expected many hops, got {checked}; warnings: {:?}",
            plan.warnings
        );
    }

    /// THE font-wide guarantee: every letter/digit can hop to every other at default tuning —
    /// no assist, no warnings about unreachable ramp speeds.
    #[test]
    fn all_letter_pairs_hop_cleanly() {
        let chars: Vec<char> = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".chars().collect();
        let e = env();
        for &c1 in &chars {
            for &c2 in &chars {
                let text: String = [c1, c2].iter().collect();
                let plan = compile(&text, &e);
                let assists = plan
                    .items
                    .iter()
                    .filter(|i| matches!(i, Item::Hop(h) if h.assist))
                    .count();
                assert_eq!(
                    assists, 0,
                    "{c1}→{c2} needed assist; warnings: {:?}",
                    plan.warnings
                );
            }
        }
    }

    /// Dots can end a message but a mid-text period genuinely can't relaunch — it must be flagged.
    #[test]
    fn trailing_dot_ok_mid_dot_flagged() {
        let e = env();
        let tail = compile("OK.", &e);
        let assists = |p: &Plan| {
            p.items
                .iter()
                .filter(|i| matches!(i, Item::Hop(h) if h.assist))
                .count()
        };
        assert_eq!(assists(&tail), 0, "trailing dot needs no hop out");
        let mid = compile("A.B", &e);
        assert!(
            assists(&mid) >= 1,
            "mid-text dot must be flagged for assist"
        );
    }

    /// Profile sanity: times strictly increase, speeds within caps, durations positive.
    #[test]
    fn profiles_are_sane() {
        let plan = compile("KSA", &env());
        for item in &plan.items {
            if let Item::Stroke(s) = item {
                assert!(s.duration > 0.0);
                for w in s.t.windows(2) {
                    assert!(w[1] >= w[0] - 1e-9, "time not monotone: {:?}", s.t);
                }
                for (i, &v) in s.v.iter().enumerate() {
                    // Interior vertices respect cruise; the entry (hop arrival) and the exit
                    // (ramp launch) legitimately run faster.
                    let boundary = i == 0 || i == s.v.len() - 1;
                    assert!(
                        boundary || v <= env().v_draw + 1e-6,
                        "overspeed {v} at vertex {i}"
                    );
                    assert!(v >= 0.0);
                }
            }
        }
        assert!(plan.total_time > 0.0);
        assert!(plan.paint_time > 0.0 && plan.paint_time <= plan.total_time);
    }

    /// Letters get sequential, non-overlapping time windows, and unsupported chars only warn.
    #[test]
    fn letters_and_warnings() {
        let plan = compile("A~B", &env());
        assert_eq!(plan.letters.len(), 2);
        assert!(plan.warnings.iter().any(|w| w.contains("unsupported")));
        assert!(plan.letters[0].t_end <= plan.letters[1].t_start + 1e-9);
    }

    /// The first ink is at the canvas origin: the vehicle writes where it hovers.
    #[test]
    fn first_point_is_origin() {
        let plan = compile("GO", &env());
        let first = plan.items.iter().find_map(|i| match i {
            Item::Stroke(s) => Some(s.pts[0]),
            _ => None,
        });
        let p = first.unwrap();
        assert!(p.0.abs() < 1e-9 && p.1.abs() < 1e-9);
    }

    /// Hop arcs stay within a sane envelope above the cap line (the altitude check depends on it).
    #[test]
    fn bbox_covers_arcs() {
        let plan = compile("MY", &env());
        assert!(plan.bbox.3 >= 0.0, "bbox must include arc tops");
        assert!(
            plan.bbox.2 < -0.5 * env().height,
            "bbox must reach below the entry"
        );
        // Arc apex shouldn't be absurd (sub-2H above the first-ink line for defaults).
        assert!(
            plan.bbox.3 < 2.0 * env().height,
            "apex {} too high",
            plan.bbox.3
        );
    }
}
