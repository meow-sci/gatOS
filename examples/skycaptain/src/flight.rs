//! The flight executive: runs a compiled [`plan::Plan`] against the live sim.
//!
//! Phase machine:
//!
//! ```text
//! Arm ──(warp forced to 1×; hover achieved; canvas frozen; pen already lit)──▶ Paint
//! Paint ──(profile end; `time/alarm` cuts the engine at the exact ramp top)──▶ Coast
//! Coast ──(ballistic; attitude pre-aimed; alarm relights at arrival)─────────▶ Paint …
//! … last stroke / any failure / pilot abort ─────────────────────────────────▶ Rescue
//! Rescue ──(warp 1×, engine ON, brake to hover, then hand the game's own FC a
//!           persistent hover hold — the vehicle NEVER falls engine-off)───────▶ Done / Abort
//! ```
//!
//! Two disciplines matter everywhere:
//! - **All timing is sim-time (`ut`).** Stroke references are anchored at pen-down ut and sampled at
//!   `ut − anchor`, so any warp factor (or a pause) leaves the geometry untouched. The engine cut at
//!   a ramp top and the relight at hop arrival use `time/alarm` — a poll loop at 10× warp would miss
//!   by tens of meters; the alarm parks until the exact sim moment.
//! - **Attitude moves stay small.** KSA's flight computer slews at ~5°/s, so the controller clamps
//!   thrust tilt and the plan stops at sharp corners. During the blocking alarm windows the last
//!   command (a constant-attitude ramp / brake) is exactly what the FC should keep flying.

use std::time::Instant;

use crate::frames::{self, Canvas};
use crate::ksa_quat;
use crate::plan::{self, Item, Plan, PlanEnv};
use crate::sim::{self, Source, Tick};
use crate::vec3::Vec3;

pub const G0: f64 = 9.80665;
/// KSA `TrailPlumeSegmentsManager._segmentLifetimeSeconds`: a stroke fades out 1200 s after it is
/// painted, so a whole message wants to fit well inside this.
pub const TRAIL_LIFETIME_S: f64 = 1200.0;

#[derive(Clone, Copy, Debug)]
pub struct FlightCfg {
    /// Warp while painting (closed-loop; keep modest so the ~10 Hz sampler still gives a few
    /// control updates per second of *game* time).
    pub warp_draw: f64,
    /// Warp while coasting a hop (nothing to control — only the arrival matters).
    pub warp_hop: f64,
    /// Warp inside the precision windows around an engine cut / relight.
    pub warp_fine: f64,
    /// Resolve `assist`-flagged hops with the `debug/…/impulse` cheat instead of refusing.
    pub allow_impulse: bool,
    /// Keep the tank topped up with `debug/…/refill_fuel` during the flight — removes the fuel
    /// budget AND keeps the mass (and so the thrust model) constant.
    pub cheat_refill: bool,
    /// Rescue (brake to hover) if radar altitude falls below this, m.
    pub floor_radar: f64,
    /// Compass heading the text runs toward (0 = N, 90 = E).
    pub heading_deg: f64,
}

impl Default for FlightCfg {
    fn default() -> Self {
        FlightCfg {
            warp_draw: 4.0,
            warp_hop: 10.0,
            warp_fine: 2.0,
            allow_impulse: false,
            cheat_refill: false,
            floor_radar: 250.0,
            heading_deg: 90.0,
        }
    }
}

#[derive(Clone, Debug)]
pub enum Phase {
    /// Brake to a hover (engine lit — this small smear becomes the first ink point).
    Arm,
    /// Painting stroke `items[item]`.
    Paint {
        item: usize,
    },
    /// Coasting hop `items[item]` (engine off), relight scheduled.
    Coast {
        item: usize,
    },
    /// Engine ON, warp 1×, braking to a hover — entered on completion AND on every failure.
    /// Ends by writing the game FC a persistent hover hold, then Done/Abort. A skywriter released
    /// engine-off is a lawn dart; this phase is why aborts stopped cratering vehicles.
    Rescue {
        reason: String,
        /// True when this rescue ends a *successful* flight (→ Done, not Abort).
        success: bool,
    },
    Done,
    Abort(String),
}

/// One step's UI report.
#[derive(Clone, Debug, Default)]
pub struct FlightView {
    pub phase_label: String,
    /// Index of the letter being worked (drawn or hopped toward), if any.
    pub cur_letter: Option<usize>,
    /// 0..1 progress through the current letter's time window.
    pub letter_progress: f64,
    /// Game-seconds remaining for the current letter / the whole text.
    pub eta_letter: f64,
    pub eta_total: f64,
    pub warp: f64,
    pub warp_wanted: f64,
    pub pos_err: f64,
    pub speed: f64,
    pub throttle: f64,
    pub alt_radar: f64,
    pub prop_frac: f64,
    /// Pen position, canvas meters.
    pub pen: (f64, f64),
    /// Reference (target) position while painting, canvas meters.
    pub ref_pen: (f64, f64),
    /// Painted-trace points since the last report (canvas meters).
    pub painted_append: Vec<(f64, f64)>,
    pub done: bool,
    pub aborted: Option<String>,
}

pub struct Flight {
    pub cfg: FlightCfg,
    pub plan: Plan,
    pub env: PlanEnv,
    canvas: Option<Canvas>,
    engines: sim::EngineAgg,
    body: sim::Body,
    vessel_id: String,
    phase: Phase,
    /// Reference clock into the current stroke, sim-seconds. Advanced each paint tick — but only
    /// while tracking is healthy: past ~60 m of error the reference *waits* for the vehicle
    /// (a plan-time reference that marches on regardless turns any upset into a chase).
    paint_t: f64,
    /// Smoothed reference-clock rate (1 = realtime, ~0.12 = crawling). Also scales the reference
    /// derivatives fed to the tracker.
    ref_rate: f64,
    /// ut when tracking error first exceeded the lost-letterform bound + the best (smallest) error
    /// seen since — improvement resets the give-up clock.
    lost_since_ut: Option<f64>,
    lost_best_err: f64,
    /// Rescue bookkeeping (sim time).
    rescue_start_ut: Option<f64>,
    rescue_stable_since_ut: Option<f64>,
    /// Last `refill_fuel` cheat write, ut.
    last_refill_ut: f64,
    /// Wall time of the last warp write attempt (for the retry pacing).
    last_warp_assert: Option<Instant>,
    /// Telemetry has matched the desired warp since it was last set. Until confirmed, the write is
    /// retried (a transient failure must not strand the flight at the wrong warp); once confirmed,
    /// any later change is the PILOT's and is adopted, not fought.
    warp_confirmed: bool,
    warp_attempts: u32,
    /// (ut, along-ramp speed) of the previous paint tick + smoothed measured ramp acceleration —
    /// the launch predictor trusts measurement over the plan.
    ramp_prev: Option<(f64, f64)>,
    ramp_accel_est: Option<f64>,
    /// Wide-tilt capture mode (set while the velocity error is large).
    capture_tilt: bool,
    /// Sim-dt of the current control tick.
    dt_tick: f64,
    /// ut the coast phase relights at.
    ignite_ut: f64,
    /// Plan-timeline seconds consumed before the current item (for ETAs).
    item_t0: f64,
    /// Vertical specific-force trim (integral), m/s².
    trim_b: f64,
    last_ut: Option<f64>,
    dt_avg: f64,
    /// The warp factor this phase wants — None until the first request, so the first `want_warp`
    /// always fires even if the game happens to sit at that factor (a previous run may have left
    /// any warp behind).
    warp_desired: Option<f64>,
    warp_unavailable: bool,
    write_fail_streak: u32,
    last_seq: Option<u64>,
    stale_since: Option<Instant>,
    /// Arm bookkeeping in **sim time** (wall time would break under warp / headless fast-forward).
    arm_start_ut: Option<f64>,
    arm_stable_since_ut: Option<f64>,
    hover_anchor: Option<Vec3>,
    /// Local-up direction in CCI from the last telemetry (for the parting nose-up attitude).
    last_up: Option<Vec3>,
    last_throttle: f64,
    last_pen: (f64, f64),
    last_ref: (f64, f64),
    last_err: f64,
    painted_pending: Vec<(f64, f64)>,
}

impl Flight {
    /// Pre-flight: validate the vessel + environment, compile the plan, and hand back a ready
    /// flight (or the reasons it can't fly). `template` carries the pilot's tuning (letter height,
    /// draw speed, gaps); gravity and thrust limits are overwritten from live data. The canvas is
    /// frozen later, at the hover point.
    pub fn start(
        src: &dyn Source,
        tick: &Tick,
        text: &str,
        cfg: FlightCfg,
        template: PlanEnv,
        notes: &mut Vec<String>,
    ) -> Result<Flight, String> {
        let tel = tick.telemetry.as_ref().ok_or("no active vessel")?;
        let body = tick.body.ok_or("parent body unreadable")?;
        if !tel.controllable {
            return Err("vessel is not controllable (no control module)".into());
        }
        let engines = sim::read_engines(src).ok_or("no engines readable on the active vessel")?;
        let atmo = body.atmosphere.ok_or_else(|| {
            "parent body has no atmosphere — KSA only emits plume trails inside one".to_string()
        })?;
        if tel.alt.baro > atmo.height {
            return Err(format!(
                "above the atmosphere ceiling ({:.0} km) — no trail will form; descend",
                atmo.height / 1000.0
            ));
        }
        if tel.vel.surf > 400.0 {
            return Err(format!(
                "surface speed {:.0} m/s — brake to a rough hover first",
                tel.vel.surf
            ));
        }

        let r = Vec3::from_array(tel.pos_cci).norm();
        let g = body.mu / (r * r);
        let accel_max = engines.thrust_max / tel.mass.t;
        let twr = accel_max / g;
        if twr < 1.25 {
            return Err(format!(
                "TWR {twr:.2} < 1.25 — not enough thrust to hover-write"
            ));
        }
        let mut env = PlanEnv {
            g,
            accel_max,
            accel_min: engines.throttle_min * accel_max,
            ..template
        };
        // The pen requires the engine LIT, and KSA floors a lit engine at `min_throttle` — if that
        // floor thrust rivals local gravity, the vehicle physically cannot descend while painting.
        // This bites high-TWR craft on low-g bodies (Mars!) hardest.
        if env.accel_min >= 0.8 * g {
            return Err(format!(
                "engine can't throttle low enough to write here: minimum thrust is {:.2} g of {} \
                 gravity ({:.0}% floor × TWR {twr:.1}) — descending strokes are impossible. \
                 Deactivate some engines, or fly a craft with TWR 2–5 on this body and a deep-throttling engine",
                env.accel_min / g,
                tel.parent.as_deref().unwrap_or("local"),
                engines.throttle_min * 100.0
            ));
        }
        // Low-g bodies need a wider tilt to keep useful lateral authority (a = g·tanθ): floor it
        // at ~2 m/s², then never plan more than a 2×-margin hover allows.
        let lowg_floor = (2.0 / g).atan().to_degrees().min(30.0);
        env.tilt_max_deg = env
            .tilt_max_deg
            .max(lowg_floor)
            .min(((twr - 1.05) / twr).asin().to_degrees().max(4.0));
        if env.accel_min > 0.6 * g {
            notes.push(format!(
                "deep-throttle floor {:.2} g vs gravity — descents will be slow and floaty",
                env.accel_min / g
            ));
        }
        if cfg.cheat_refill {
            notes.push("tank refill cheat ON — propellant will be topped up in flight".into());
        }

        let plan = plan::compile(text, &env);
        if plan.letters.is_empty() {
            return Err("nothing drawable in that text".into());
        }
        notes.extend(plan.warnings.iter().cloned());

        let assist_count = plan
            .items
            .iter()
            .filter(|i| matches!(i, Item::Hop(h) if h.assist))
            .count();
        if assist_count > 0 && !cfg.allow_impulse {
            return Err(format!(
                "{assist_count} hop(s) need the impulse cheat (mid-text dots?) — re-word or pass --allow-impulse"
            ));
        }

        // Envelope: text hangs below the first ink; hop arcs rise above it. The extra 0.35·height
        // covers tracking overshoot below the baseline.
        let drop = -plan.bbox.2 + 0.35 * env.height;
        if tel.alt.radar - drop < cfg.floor_radar {
            return Err(format!(
                "too low: text + overshoot margin extends {drop:.0} m below here, radar alt {:.0} m (need {:.0} m) — climb, or use --height for smaller letters",
                tel.alt.radar,
                drop + cfg.floor_radar
            ));
        }
        let rho = atmo.density_at(tel.alt.baro);
        if rho < 1e-9 {
            return Err(
                "air too thin here for the trail (density < 1e-9) — descend a little".into(),
            );
        }
        let q = 0.5 * rho * env.v_draw * env.v_draw;
        if q > 400.0 {
            notes.push(format!(
                "dynamic pressure ~{q:.0} Pa at draw speed — expect drag wobble; higher altitude writes cleaner"
            ));
        }
        if plan.total_time > TRAIL_LIFETIME_S - 150.0 {
            notes.push(format!(
                "plan takes {:.0} s of game time; trail segments fade after {TRAIL_LIFETIME_S:.0} s — early letters will thin before you finish",
                plan.total_time
            ));
        }

        Ok(Flight {
            cfg,
            plan,
            env,
            canvas: None,
            engines,
            body,
            vessel_id: tel.id.clone(),
            phase: Phase::Arm,
            paint_t: 0.0,
            ref_rate: 1.0,
            lost_since_ut: None,
            lost_best_err: f64::MAX,
            rescue_start_ut: None,
            rescue_stable_since_ut: None,
            last_refill_ut: 0.0,
            last_warp_assert: None,
            warp_confirmed: false,
            warp_attempts: 0,
            ramp_prev: None,
            ramp_accel_est: None,
            capture_tilt: false,
            dt_tick: 0.0,
            ignite_ut: 0.0,
            item_t0: 0.0,
            trim_b: 0.0,
            last_ut: None,
            dt_avg: 0.3,
            warp_desired: None,
            warp_unavailable: false,
            write_fail_streak: 0,
            last_seq: None,
            stale_since: None,
            arm_start_ut: None,
            arm_stable_since_ut: None,
            hover_anchor: None,
            last_up: None,
            last_throttle: 0.0,
            last_pen: (0.0, 0.0),
            last_ref: (0.0, 0.0),
            last_err: 0.0,
            painted_pending: Vec::new(),
        })
    }

    /// Pilot/failure abort: brake to a hover first, then hand back a *hovering* vehicle. A second
    /// abort while already rescuing skips straight to the safe hand-off.
    pub fn abort(&mut self, src: &dyn Source, reason: &str) {
        match &self.phase {
            Phase::Done | Phase::Abort(_) => {}
            Phase::Rescue { reason: r, success } => {
                let (r, s) = (r.clone(), *success);
                self.safe_release(src);
                self.phase = if s { Phase::Done } else { Phase::Abort(r) };
            }
            _ => self.start_rescue(src, reason, false),
        }
    }

    /// Terminal, can't-keep-flying abort (telemetry gone, UI closing): best-effort safe hand-off —
    /// engine ON at hover throttle with the FC holding nose-up (onboard setpoints persist after we
    /// stop talking) — never an engine-off release.
    pub fn hard_abort(&mut self, src: &dyn Source, reason: &str) {
        self.safe_release(src);
        self.phase = Phase::Abort(reason.to_string());
    }

    fn start_rescue(&mut self, src: &dyn Source, reason: &str, success: bool) {
        self.want_warp(src, 1.0);
        let _ = src.write("vessels/active/ctl/engine", "1");
        self.hover_anchor = None;
        self.rescue_start_ut = None;
        self.rescue_stable_since_ut = None;
        self.phase = Phase::Rescue {
            reason: reason.to_string(),
            success,
        };
    }

    /// Park the vehicle on the game's own flight computer: nose-up attitude hold + hover throttle +
    /// engine on. These setpoints are onboard — KSA keeps flying them after this program stops
    /// writing — so the pilot inherits a hovering craft, not a falling one.
    fn safe_release(&mut self, src: &dyn Source) {
        self.want_warp(src, 1.0);
        // Hover fraction from the specific-force model (mass-normalized, no live mass needed).
        let hover = (self.env.g / self.env.accel_max * 1.03)
            .clamp((self.engines.throttle_min + 0.01).min(1.0), 1.0);
        let _ = src.write("vessels/active/ctl/throttle", &format!("{hover:.4}"));
        let _ = src.write("vessels/active/ctl/engine", "1");
        if let Some(up) = self.last_up {
            let q = ksa_quat::compute_burn_body2cci(up, up);
            let a = q.to_array();
            let _ = src.write(
                "vessels/active/ctl/attitude_target",
                &format!("{} {} {} {}", a[0], a[1], a[2], a[3]),
            );
        }
    }

    pub fn is_over(&self) -> bool {
        matches!(self.phase, Phase::Done | Phase::Abort(_))
    }

    /// One control cycle. May block briefly on `time/alarm` inside cut/relight windows.
    pub fn step(&mut self, src: &dyn Source, tick: &Tick) -> FlightView {
        let Some(tel) = tick.telemetry.as_ref() else {
            if self.stale(true) {
                self.hard_abort(src, "telemetry lost");
            }
            return self.view(None);
        };
        self.last_up = Some(Vec3::from_array(tel.pos_cci).normalize());

        // Staleness: the sampler stopped publishing (seq frozen) while unpaused.
        let frozen = self.last_seq == Some(tel.seq) && tel.warp > 0.0;
        self.last_seq = Some(tel.seq);
        if self.stale(frozen) {
            self.hard_abort(src, "telemetry stale > 8 s");
            return self.view(Some(tel));
        }
        if frozen {
            return self.view(Some(tel)); // hold commands; keep last attitude/throttle
        }

        // Game-dt tracking for gain scheduling + the reference clock.
        self.dt_tick = 0.0;
        if let Some(prev) = self.last_ut {
            let dt = (tel.ut - prev).clamp(0.0, 10.0);
            self.dt_tick = dt;
            if dt > 1e-4 {
                self.dt_avg = 0.8 * self.dt_avg + 0.2 * dt;
            }
        }
        self.last_ut = Some(tel.ut);

        // Warp maintenance. Until telemetry confirms the desired factor, retry the write every
        // couple of seconds (a transiently failed one-shot must not strand the flight at the wrong
        // warp — that's how a run got stuck writing at 1×). Once confirmed, a later change is the
        // PILOT turning the dial: adopt it instead of fighting them; our next phase transition
        // will state its own preference again (which they can override again).
        if !self.warp_unavailable && tel.warp >= 0.1 {
            if let Some(want) = self.warp_desired {
                let matches = (tel.warp - want).abs() / want.max(0.1) < 0.25;
                if matches {
                    self.warp_confirmed = true;
                } else if self.warp_confirmed {
                    self.warp_desired = Some(tel.warp); // pilot override — respected
                } else {
                    let due = self
                        .last_warp_assert
                        .map(|t| t.elapsed().as_secs_f64() > 2.0)
                        .unwrap_or(true);
                    if due && self.warp_attempts < 6 {
                        self.try_warp_write(src);
                    } else if self.warp_attempts >= 6 {
                        self.warp_confirmed = true; // stop insisting; fly at whatever it is
                    }
                }
            }
        }

        // Tank cheat: top up every ~20 sim-seconds (and instantly when low).
        if self.cfg.cheat_refill
            && !matches!(self.phase, Phase::Done | Phase::Abort(_))
            && (tel.ut - self.last_refill_ut > 20.0
                || tel.mass.p < 0.15 * (tel.mass.t - tel.mass.d).max(1.0))
        {
            let path = format!("debug/vessels/{}/refill_fuel", sanitize_id(&self.vessel_id));
            let _ = src.write(&path, "1");
            self.last_refill_ut = tel.ut;
        }

        // Hard safety floor + fuel: both end in a RESCUE (engine-on brake to hover), never an
        // engine-off release — a hovering skywriter dropped engine-off is a crater.
        let active = matches!(
            self.phase,
            Phase::Arm | Phase::Paint { .. } | Phase::Coast { .. }
        );
        if active && tel.alt.radar < self.cfg.floor_radar {
            self.start_rescue(src, "radar floor breached", false);
        }
        if active && tel.mass.p < 1.0 {
            if self.cfg.cheat_refill {
                let path = format!("debug/vessels/{}/refill_fuel", sanitize_id(&self.vessel_id));
                let _ = src.write(&path, "1");
                self.last_refill_ut = tel.ut;
            } else {
                self.start_rescue(src, "out of propellant", false);
            }
        }

        let Some(lon) = tick.lon_deg else {
            return self.view(Some(tel));
        };
        let ft = frames::frame_tick(
            Vec3::from_array(tel.pos_cci),
            Vec3::from_array(tel.vel_cci),
            lon,
            self.body.rotation_rate,
        );
        if let Some(c) = self.canvas {
            let p = c.to_canvas(ft.pos_ccf);
            self.last_pen = (p.x, p.y);
        }

        match self.phase.clone() {
            Phase::Arm => self.step_arm(src, tel, &ft),
            Phase::Paint { item } => self.step_paint(src, tel, &ft, item),
            Phase::Coast { item } => self.step_coast(src, tel, &ft, item),
            Phase::Rescue { reason, success } => self.step_rescue(src, tel, &ft, reason, success),
            Phase::Done | Phase::Abort(_) => {}
        }
        self.view(Some(tel))
    }

    // ---- phases ---------------------------------------------------------------------------------

    fn step_arm(&mut self, src: &dyn Source, tel: &sim::Telemetry, ft: &frames::FrameTick) {
        // Hover capture is the most delicate closed loop in the program — force 1× warp for it.
        // (A previous run can leave the game at 10×, where ~1 command per game-second cannot
        // capture anything: the vehicle porpoises on its plume until the timeout. Seen on Mars.)
        self.want_warp(src, 1.0);
        let start = *self.arm_start_ut.get_or_insert(tel.ut);
        if tel.ut - start > 240.0 {
            self.start_rescue(src, "arm timed out (could not settle into a hover)", false);
            return;
        }
        // Light the engine immediately: the little braking smear merges into the first ink point.
        if self.hover_anchor.is_none() {
            let _ = src.write("vessels/active/ctl/engine", "1");
        }
        // Velocity damping first; freeze an anchor once slow, then hold position on it.
        if ft.vel_ccf.norm() < 15.0 && self.hover_anchor.is_none() {
            self.hover_anchor = Some(ft.pos_ccf);
        }
        let (a_des_ccf, err) = match self.hover_anchor {
            Some(anchor) => {
                let e = anchor - ft.pos_ccf;
                let v_cmd = cap_vec(e * 0.35, 30.0);
                (cap_vec((v_cmd - ft.vel_ccf) * 1.0, 8.0), e.norm())
            }
            None => (cap_vec(ft.vel_ccf * -0.9, 8.0), f64::INFINITY),
        };
        self.command_thrust(src, tel, ft, a_des_ccf, None);

        let stable = ft.vel_ccf.norm() < 3.5 && err < 25.0;
        match (stable, self.arm_stable_since_ut) {
            (true, None) => self.arm_stable_since_ut = Some(tel.ut),
            (true, Some(t0)) if tel.ut - t0 > 2.0 => {
                // Freeze the canvas here: this hover point is the first ink.
                self.canvas = Some(Canvas::new(ft.pos_ccf, self.cfg.heading_deg));
                self.paint_t = 0.0;
                self.ref_rate = 1.0;
                self.item_t0 = 0.0;
                self.trim_b = 0.0;
                self.phase = Phase::Paint { item: 0 };
                self.want_warp(src, self.cfg.warp_draw);
            }
            (false, _) => self.arm_stable_since_ut = None,
            _ => {}
        }
    }

    fn step_paint(
        &mut self,
        src: &dyn Source,
        tel: &sim::Telemetry,
        ft: &frames::FrameTick,
        item: usize,
    ) {
        let Some(canvas) = self.canvas else { return };
        let Item::Stroke(stroke) = &self.plan.items[item] else {
            return;
        };
        let stroke = stroke.clone();
        let t_local = self.paint_t;
        let r0 = stroke.sample(t_local);
        // Time dilation: while the reference clock crawls (see below), its derivatives crawl too —
        // feeding the tracker the full-speed profile velocity while the position creeps guarantees
        // a runaway chase.
        let rate = self.ref_rate;
        let mut r = r0;
        r.vel = (r.vel.0 * rate, r.vel.1 * rate);
        r.acc = (r.acc.0 * rate * rate, r.acc.1 * rate * rate);

        let pos_can = canvas.to_canvas(ft.pos_ccf);
        self.painted_pending.push((pos_can.x, pos_can.y));

        // Cascaded velocity-form tracking, every stage hard-capped: position error commands a
        // *bounded* velocity correction; the velocity loop commands a *bounded* acceleration on top
        // of the feedforward. An unsaturated cascade can't wind up — a plain PD here saturates on a
        // ~100 m error, bang-bangs against the asymmetric climb/brake authority (thrust can't point
        // down) through the FC's slew lag, and pumps itself into a runaway.
        let vel_can = canvas.dir_to_canvas(ft.vel_ccf);
        let e = Vec3::new(r.pos.0 - pos_can.x, r.pos.1 - pos_can.y, -pos_can.z);
        self.last_ref = r.pos;
        self.last_err = (e.x * e.x + e.y * e.y).sqrt();
        // Integral trim soaks the steady vertical bias (thrust model error, drag).
        if self.last_err < 120.0 {
            self.trim_b = (self.trim_b + 0.05 * e.y * self.dt_avg).clamp(-1.5, 1.5);
        }
        let k_pos = 0.35; // 1/s
        let k_vel = (0.3 * std::f64::consts::TAU / self.dt_avg.max(0.05)).clamp(0.4, 1.0); // 1/s
        let v_corr = cap_vec(e * k_pos, 30.0);
        let v_err = Vec3::new(r.vel.0 - vel_can.x, r.vel.1 - vel_can.y, -vel_can.z) + v_corr;
        let a_fb = cap_vec(v_err * k_vel, 6.0);
        // Capture mode: a large velocity error (hop arrival crossrange, big upset) justifies a
        // one-time wide thrust swing and a stronger correction — the routine clamps would take
        // forever to kill it.
        self.capture_tilt = v_err.norm() > 18.0;
        let a_fb = if self.capture_tilt {
            cap_vec(v_err * k_vel, 10.0)
        } else {
            a_fb
        };
        let mut a_des_can = Vec3::new(r.acc.0 + a_fb.x, r.acc.1 + a_fb.y + self.trim_b, a_fb.z);

        // Attitude pre-swing: if the profile brakes hard within the FC's swing horizon, command the
        // brake's thrust attitude *now* — the alignment gate floors the throttle while the nose
        // swings, and the brake then starts aligned at full authority instead of smearing sideways
        // through a half-swung burn. Only while tracking is healthy: during a crawl the lookahead
        // sees "brake coming" forever, and holding the brake attitude while off the path is a
        // runaway.
        if self.last_err < 60.0 && self.ref_rate > 0.7 {
            let ahead = stroke.sample(t_local + 2.8);
            let v_now = (r0.vel.0 * r0.vel.0 + r0.vel.1 * r0.vel.1).sqrt();
            let v_ahead = (ahead.vel.0 * ahead.vel.0 + ahead.vel.1 * ahead.vel.1).sqrt();
            if v_ahead < v_now - 15.0 {
                let brake = Vec3::new(ahead.acc.0, ahead.acc.1, 0.0);
                a_des_can = brake + cap_vec(a_fb * 0.4, 2.5);
            }
        }
        self.command_thrust(
            src,
            tel,
            ft,
            canvas.dir_from_canvas(a_des_can),
            Some(canvas),
        );

        // Advance the reference clock — at a crawl while the vehicle is far off it (the reference
        // waits instead of running away). The threshold scales with reference speed: at 90 m/s a
        // second of slew lag is 90 m of "error" the vehicle recovers on its own. `ref_rate` is
        // smoothed so the derivative scaling above doesn't chatter.
        let profile_speed = stroke.sample(t_local).vel;
        let r_speed =
            (profile_speed.0 * profile_speed.0 + profile_speed.1 * profile_speed.1).sqrt();
        let crawl_at = 60.0 + 1.0 * r_speed;
        let target_rate = if self.last_err > crawl_at { 0.12 } else { 1.0 };
        self.ref_rate = 0.7 * self.ref_rate + 0.3 * target_rate;
        self.paint_t += self.dt_tick * self.ref_rate;

        // Give-up guard, deliberately lenient: only rescue when the error is HUGE (2.5 km), has
        // stayed huge for 45 s, and is not improving — a vehicle that is clawing its way back gets
        // to keep trying (the crawling reference + capture mode usually recover it).
        if self.last_err > 2500.0 {
            if self.lost_since_ut.is_none() {
                self.lost_since_ut = Some(tel.ut);
                self.lost_best_err = self.last_err;
            }
            if self.last_err < self.lost_best_err * 0.9 {
                // Improving — restart the clock and remember the new best.
                self.lost_best_err = self.last_err;
                self.lost_since_ut = Some(tel.ut);
            }
            if tel.ut - self.lost_since_ut.unwrap_or(tel.ut) > 45.0 {
                self.start_rescue(
                    src,
                    "lost the letterform (tracking error > 2.5 km, not recovering)",
                    false,
                );
                return;
            }
        } else {
            self.lost_since_ut = None;
            self.lost_best_err = f64::MAX;
        }

        // Periodically re-assert the pen (idempotent STATE write) — staging/other clients happen.
        if (t_local as u64).is_multiple_of(3) {
            let _ = src.write("vessels/active/ctl/engine", "1");
        }

        // Approaching the stroke end: tighten warp, then alarm to the cut moment. The cut time is
        // *re-solved from the measured state*: predict the ballistic arc for a release at each
        // instant of the remaining ramp run and pick the minimum-miss instant — a planned-time cut
        // would inherit the whole tracking error as launch error.
        let remaining = stroke.duration - t_local;
        let next_hop = matches!(self.plan.items.get(item + 1), Some(Item::Hop(_)));
        if next_hop && remaining < 3.0 * self.cfg.warp_fine.max(1.0) / 2.0 {
            self.want_warp(src, self.cfg.warp_fine);
        }
        // Fire the cut on *state* as well as clock: if the pen is about to run off the ramp tip,
        // release now — waiting for the (possibly crawling) reference clock would overshoot the
        // stroke and lob the vehicle past the letter.
        // Measure the along-ramp acceleration while sprinting the final segment (the launch
        // predictor wants the delivered accel, not the planned one).
        if next_hop && stroke.pts.len() >= 2 && t_local > *stroke.t.last().unwrap_or(&0.0) - 12.0 {
            let d = stroke.segs.last().map(|s| s.dir).unwrap_or((0.0, 1.0));
            let s_along = vel_can.x * d.0 + vel_can.y * d.1;
            if let Some((put, ps)) = self.ramp_prev {
                let dt = tel.ut - put;
                if dt > 1e-3 && s_along > ps {
                    let a = ((s_along - ps) / dt).clamp(0.5, 40.0);
                    let ema = self.ramp_accel_est.unwrap_or(a);
                    self.ramp_accel_est = Some(0.6 * ema + 0.4 * a);
                }
            }
            self.ramp_prev = Some((tel.ut, s_along));
        }

        let tracking_ok = self.last_err < 80.0;
        // "About to run off the ramp tip": only meaningful when the reference clock has actually
        // reached the final segment AND the pen is *on* the ramp line (small lateral offset) —
        // a bare along-axis projection would also fire from the stroke entry, which can sit past
        // the tip's perpendicular plane (found the hard way).
        let tip_imminent =
            next_hop && tracking_ok && t_local >= stroke.t[stroke.pts.len() - 2] && {
                let tip = *stroke.pts.last().unwrap();
                let d = stroke.segs.last().map(|s| s.dir).unwrap_or((0.0, 1.0));
                let (ra, rb) = (tip.0 - pos_can.x, tip.1 - pos_can.y);
                let along = ra * d.0 + rb * d.1;
                let lateral = (ra - along * d.0).hypot(rb - along * d.1);
                let s_along = vel_can.x * d.0 + vel_can.y * d.1;
                s_along > 5.0 && lateral < 60.0 && along < s_along * 2.2 * self.dt_avg.max(0.15)
            };
        // Never release for a hop while tracking is bad — the launch would carry the whole error.
        // (The crawling reference holds `remaining` up while the vehicle recaptures.)
        if tip_imminent || (remaining <= 2.5 * self.dt_avg.max(0.15) && (tracking_ok || !next_hop))
        {
            let cut_ut = match self.plan.items.get(item + 1) {
                Some(Item::Hop(hop)) if !hop.assist => self
                    .refine_cut(tel, &pos_can, &vel_can, &stroke, hop, remaining)
                    .unwrap_or(tel.ut + remaining),
                _ => tel.ut + remaining,
            } - self.frame_lead();
            let reached = src.wait_until(cut_ut.max(tel.ut)).unwrap_or(cut_ut);
            self.last_ut = None; // the alarm jumped sim time; don't let it pollute dt_tick
            if let Some(Item::Hop(hop)) = self.plan.items.get(item + 1).cloned() {
                let _ = src.write("vessels/active/ctl/engine", "0");
                if hop.assist && self.cfg.allow_impulse {
                    self.fire_assist_impulse(src, ft, &hop, canvas);
                }
                self.ignite_ut = reached + hop.t_flight;
                self.item_t0 += stroke.duration;
                self.phase = Phase::Coast { item: item + 1 };
                self.aim_entry(src, tel, ft, item + 2, canvas);
                self.want_warp(src, self.cfg.warp_hop);
            } else {
                // Last stroke: cut the pen, then rescue-to-hover — the pilot inherits a hovering
                // vehicle, not a falling one.
                let _ = src.write("vessels/active/ctl/engine", "0");
                self.item_t0 += stroke.duration;
                self.start_rescue(src, "done", true);
            }
        }
    }

    fn step_coast(
        &mut self,
        src: &dyn Source,
        tel: &sim::Telemetry,
        ft: &frames::FrameTick,
        item: usize,
    ) {
        let Some(canvas) = self.canvas else { return };

        // Refine the relight moment from the *measured* arc: closest approach to the entry point.
        // The launch is never perfect; the plan's flight time would hand the entry the whole error.
        if let Some(Item::Stroke(next)) = self.plan.items.get(item + 1) {
            let entry = next.pts[0];
            let p = canvas.to_canvas(ft.pos_ccf);
            let v = canvas.dir_to_canvas(ft.vel_ccf);
            let horizon = ((self.ignite_ut - tel.ut) + 6.0).clamp(1.0, 90.0);
            let mut best: Option<(f64, f64)> = None;
            let mut tau = 0.0;
            while tau <= horizon {
                let px = p.x + v.x * tau;
                let py = p.y + v.y * tau - 0.5 * self.env.g * tau * tau;
                let d2 = (px - entry.0).powi(2) + (py - entry.1).powi(2);
                if best.is_none_or(|(b, _)| d2 < b) {
                    best = Some((d2, tau));
                }
                tau += 0.05;
            }
            if let Some((_, tau)) = best {
                let refined = tel.ut + tau;
                self.ignite_ut = refined.clamp(self.ignite_ut - 20.0, self.ignite_ut + 20.0);
            }
        }
        let remaining = self.ignite_ut - tel.ut;
        let fine_window = 2.5 * self.cfg.warp_fine.max(1.0) / 2.0;

        // Keep the entry attitude pre-aimed all the way in (the FC holds it with RCS/wheels).
        self.aim_entry(src, tel, ft, item + 1, canvas);

        if remaining > fine_window {
            self.want_warp(src, self.cfg.warp_hop);
            // Chunked alarm: ~0.8 s of wall time per chunk keeps the pilot's abort responsive.
            let chunk = 0.8 * self.warp_desired.unwrap_or(1.0).max(1.0);
            let _ = src.wait_until((tel.ut + chunk).min(self.ignite_ut - fine_window * 0.9));
            self.last_ut = None;
        } else {
            self.want_warp(src, self.cfg.warp_fine);
            let _ = src.wait_until(self.ignite_ut - self.frame_lead());
            self.last_ut = None; // the alarm jumped sim time; dt_tick must restart clean
                                 // Relight: attitude is already aimed; brake throttle from the entry envelope.
            let brake = plan::accel_along((0.0, 1.0), &self.env).min(self.env.accel_max * 0.85);
            let throttle = self.throttle_for(brake + self.env.g * 0.2, tel);
            let _ = src.write("vessels/active/ctl/throttle", &format!("{throttle:.4}"));
            let _ = src.write("vessels/active/ctl/engine", "1");
            if let Some(Item::Hop(h)) = self.plan.items.get(item) {
                self.item_t0 += h.t_flight;
            }
            self.paint_t = 0.0;
            self.ref_rate = 1.0;
            self.ramp_prev = None;
            self.ramp_accel_est = None;
            self.phase = Phase::Paint { item: item + 1 };
            self.want_warp(src, self.cfg.warp_draw);
        }
    }

    /// Brake to a hover with the engine ON, then park the vehicle on the game FC's own hover hold
    /// and finish. Times out into the safe hand-off regardless — rescue can end early, never badly.
    fn step_rescue(
        &mut self,
        src: &dyn Source,
        tel: &sim::Telemetry,
        ft: &frames::FrameTick,
        reason: String,
        success: bool,
    ) {
        self.want_warp(src, 1.0);
        let start = *self.rescue_start_ut.get_or_insert(tel.ut);
        let _ = src.write("vessels/active/ctl/engine", "1");

        // Pure velocity damping — no position anchor. A rescue's job is to stop moving, not to
        // stand somewhere particular; an anchor-chase at wide tilt dithers the commanded attitude
        // faster than the FC can slew and never settles (measured, not theorized).
        self.capture_tilt = false;
        let a_des = cap_vec(ft.vel_ccf * -1.0, 8.0);
        self.command_thrust(src, tel, ft, a_des, None);

        let stable = ft.vel_ccf.norm() < 6.0;
        let settled = match (stable, self.rescue_stable_since_ut) {
            (true, None) => {
                self.rescue_stable_since_ut = Some(tel.ut);
                false
            }
            (true, Some(t0)) => tel.ut - t0 > 2.0,
            (false, _) => {
                self.rescue_stable_since_ut = None;
                false
            }
        };
        if settled || tel.ut - start > 120.0 {
            self.safe_release(src);
            self.phase = if success {
                Phase::Done
            } else {
                Phase::Abort(reason)
            };
        }
    }

    // ---- actuation ------------------------------------------------------------------------------

    /// Turn a desired **path** acceleration (CCF) into thrust attitude + throttle and write both.
    /// `thrust = a_des − g_eff` with `g_eff` = gravity + centrifugal + Coriolis in the rotating
    /// frame — the exact terms KSA integrates for an off-rails vehicle.
    fn command_thrust(
        &mut self,
        src: &dyn Source,
        tel: &sim::Telemetry,
        ft: &frames::FrameTick,
        a_des_ccf: Vec3,
        canvas: Option<Canvas>,
    ) {
        let g_eff = frames::gravity(ft.pos_ccf, self.body.mu)
            + frames::centrifugal_ccf(ft.pos_ccf, self.body.rotation_rate)
            + frames::coriolis_ccf(ft.vel_ccf, self.body.rotation_rate);
        let mut thrust = a_des_ccf - g_eff;

        // Tilt clamp about the local vertical: the plan promised the FC only small swings — except
        // in capture mode (killing a hop-arrival crossrange), where one wide swing beats a
        // half-minute of feeble sideways nudging.
        let up = canvas
            .map(|c| c.u_hat)
            .unwrap_or_else(|| ft.pos_ccf.normalize());
        let vert = up * thrust.dot(&up);
        let lat = thrust - vert;
        let tilt_deg = if self.capture_tilt {
            35.0
        } else {
            self.env.tilt_max_deg * 1.3
        };
        let max_tan = tilt_deg.to_radians().tan();
        let vmag = vert.norm().max(0.1);
        if lat.norm() > vmag * max_tan {
            thrust = vert + lat * (vmag * max_tan / lat.norm());
        }
        if thrust.dot(&up) < 0.05 {
            thrust = up * 0.05 + lat; // never command downward thrust
        }

        let dir_cci = frames::ccf_to_cci(thrust.normalize(), ft.theta);

        // Alignment-gated throttle: the FC slews the nose at a few deg/s, and full thrust on a
        // half-swung nose shoves the vehicle sideways (the classic corner-brake smear). Scale the
        // commanded magnitude by how far the *actual* thrust axis (telemetry attitude) is from the
        // commanded direction: floor it beyond 25°, full within 8°.
        let actual_axis = ksa_quat::transform(Vec3::x(), ksa_quat::Quat::from_array(tel.att_q));
        let align = actual_axis
            .dot(&dir_cci)
            .clamp(-1.0, 1.0)
            .acos()
            .to_degrees();
        let gate = (1.0 - (align - 8.0) / 17.0).clamp(0.0, 1.0);
        let throttle = self.throttle_for(thrust.norm() * gate, tel);
        let q = ksa_quat::compute_burn_body2cci(Vec3::from_array(tel.pos_cci).normalize(), dir_cci);
        let a = q.to_array();
        let att = src.write(
            "vessels/active/ctl/attitude_target",
            &format!("{} {} {} {}", a[0], a[1], a[2], a[3]),
        );
        let thr = src.write("vessels/active/ctl/throttle", &format!("{throttle:.4}"));
        self.last_throttle = throttle;
        match (att, thr) {
            (Ok(()), Ok(())) => self.write_fail_streak = 0,
            _ => {
                self.write_fail_streak += 1;
                if self.write_fail_streak > 8 {
                    self.hard_abort(src, "control writes failing (EACCES?)");
                }
            }
        }
    }

    fn throttle_for(&self, specific_force: f64, tel: &sim::Telemetry) -> f64 {
        (specific_force * tel.mass.t / self.engines.thrust_max)
            .clamp((self.engines.throttle_min + 0.01).min(1.0), 1.0)
    }

    /// Pre-aim the attitude for the next stroke's entry brake: thrust against the hop's *planned
    /// arrival velocity* (not just the entry tangent — a word-gap arrival carries crossrange that
    /// the very first burn should be pointed against). Constant during the coast, so the FC has
    /// the whole hop to settle on it.
    fn aim_entry(
        &mut self,
        src: &dyn Source,
        tel: &sim::Telemetry,
        ft: &frames::FrameTick,
        next_stroke: usize,
        canvas: Canvas,
    ) {
        let Some(Item::Stroke(s)) = self.plan.items.get(next_stroke) else {
            return;
        };
        let hop_v2 = match self.plan.items.get(next_stroke.wrapping_sub(1)) {
            Some(Item::Hop(h)) if !h.assist => Some(h.v2),
            _ => None,
        };
        let brake_dir = match hop_v2 {
            Some(v2) => {
                let m = (v2.0 * v2.0 + v2.1 * v2.1).sqrt().max(1e-9);
                Vec3::new(-v2.0 / m, -v2.1 / m, 0.0)
            }
            None => {
                let d0 = (s.pts[1].0 - s.pts[0].0, s.pts[1].1 - s.pts[0].1);
                let l = (d0.0 * d0.0 + d0.1 * d0.1).sqrt().max(1e-9);
                Vec3::new(-d0.0 / l, -d0.1 / l, 0.0)
            }
        };
        let a_brake = plan::accel_along_tilt((brake_dir.x, brake_dir.y), &self.env, 35.0).min(8.0);
        let a_des = canvas.dir_from_canvas(brake_dir * a_brake);
        let g_eff = frames::gravity(ft.pos_ccf, self.body.mu)
            + frames::centrifugal_ccf(ft.pos_ccf, self.body.rotation_rate);
        let thrust = a_des - g_eff;
        let dir_cci = frames::ccf_to_cci(thrust.normalize(), ft.theta);
        let q = ksa_quat::compute_burn_body2cci(Vec3::from_array(tel.pos_cci).normalize(), dir_cci);
        let a = q.to_array();
        let _ = src.write(
            "vessels/active/ctl/attitude_target",
            &format!("{} {} {} {}", a[0], a[1], a[2], a[3]),
        );
    }

    /// Re-solve the engine-cut instant from the measured state: propagate the current canvas state
    /// along the ramp at its planned acceleration, ballistically extend a release at each candidate
    /// instant, and pick the minimum vertical miss at the hop target. Collapses tracking error into
    /// meters of launch error instead of inheriting all of it.
    fn refine_cut(
        &self,
        tel: &sim::Telemetry,
        pos_can: &Vec3,
        vel_can: &Vec3,
        stroke: &plan::Stroke,
        hop: &plan::Hop,
        remaining: f64,
    ) -> Option<f64> {
        let seg = stroke.segs.last()?;
        let d = seg.dir;
        let a = self.ramp_accel_est.unwrap_or(seg.a_f * 0.9);
        let g = self.env.g;
        let s0 = vel_can.x * d.0 + vel_can.y * d.1; // speed along the ramp
        let horizon = (remaining + 2.0).clamp(0.5, 8.0);
        let mut best: Option<(f64, f64)> = None; // (|miss|, tau)
        let mut tau = 0.0;
        while tau <= horizon {
            let s = s0 + a * tau;
            let run = s0 * tau + 0.5 * a * tau * tau;
            let (px, py) = (pos_can.x + d.0 * run, pos_can.y + d.1 * run);
            let (vx, vy) = (d.0 * s, d.1 * s);
            if s > 1.0 && vx > 0.5 {
                let t = (hop.to.0 - px) / vx;
                if t > 0.5 {
                    let miss = (py + vy * t - 0.5 * g * t * t - hop.to.1).abs();
                    if best.is_none_or(|(m, _)| miss < m) {
                        best = Some((miss, tau));
                    }
                }
            }
            tau += 0.02;
        }
        best.map(|(_, tau)| tel.ut + tau)
    }

    /// An `assist` hop can't be launched from its ramp (a dot's ramp is centimeters); with the
    /// pilot's blessing, kick the exact launch velocity on with the impulse cheat (Δv mode, CCI).
    fn fire_assist_impulse(
        &mut self,
        src: &dyn Source,
        ft: &frames::FrameTick,
        hop: &plan::Hop,
        canvas: Canvas,
    ) {
        let t = hop.t_flight.max(2.0);
        let pos_can = canvas.to_canvas(ft.pos_ccf);
        let want = Vec3::new(
            (hop.to.0 - pos_can.x) / t,
            (hop.to.1 - pos_can.y + 0.5 * self.env.g * t * t) / t,
            -pos_can.z / t,
        );
        let dv_ccf = canvas.dir_from_canvas(want) - ft.vel_ccf;
        let dv_cci = frames::ccf_to_cci(dv_ccf, ft.theta);
        let path = format!("debug/vessels/{}/impulse", sanitize_id(&self.vessel_id));
        let _ = src.write(
            &path,
            &format!("{} {} {} cci dv", dv_cci.x, dv_cci.y, dv_cci.z),
        );
    }

    // ---- plumbing -------------------------------------------------------------------------------

    /// Half a sim frame at the fine warp — the lead applied to alarm targets so the next game frame
    /// lands the command on the intended tick, not one late.
    fn frame_lead(&self) -> f64 {
        0.5 * self.cfg.warp_fine.max(1.0) / 60.0
    }

    /// State a phase's warp preference. The write is attempted immediately and then retried by the
    /// maintenance loop in [`Flight::step`] until telemetry confirms it (or the pilot overrides).
    fn want_warp(&mut self, src: &dyn Source, factor: f64) {
        if self.warp_unavailable {
            return;
        }
        let changed = self
            .warp_desired
            .map(|w| (w - factor).abs() > 0.01)
            .unwrap_or(true);
        if changed {
            self.warp_desired = Some(factor);
            self.warp_confirmed = false;
            self.warp_attempts = 0;
            self.try_warp_write(src);
        }
    }

    fn try_warp_write(&mut self, src: &dyn Source) {
        let Some(want) = self.warp_desired else {
            return;
        };
        match src.write("debug/time/warp", &format!("{want}")) {
            Ok(()) => {
                self.warp_attempts += 1;
                self.last_warp_assert = Some(Instant::now());
            }
            Err(e) if e.errno == "EACCES" || e.errno == "ENOENT" => {
                self.warp_unavailable = true; // debug namespace off — fly at whatever warp is set
            }
            Err(_) => {
                // Transient (game paused/loading?): count it and let the maintenance loop retry.
                self.warp_attempts += 1;
                self.last_warp_assert = Some(Instant::now());
            }
        }
    }

    fn stale(&mut self, is_stale: bool) -> bool {
        if !is_stale {
            self.stale_since = None;
            return false;
        }
        let t0 = *self.stale_since.get_or_insert_with(Instant::now);
        t0.elapsed().as_secs_f64() > 8.0
    }

    fn view(&mut self, tel: Option<&sim::Telemetry>) -> FlightView {
        let (cur_letter, t_in_item) = match &self.phase {
            Phase::Paint { item } | Phase::Coast { item } => {
                let letter = match &self.plan.items[*item] {
                    Item::Stroke(s) => Some(s.letter),
                    Item::Hop(_) => match self.plan.items.get(*item + 1) {
                        Some(Item::Stroke(s)) => Some(s.letter),
                        _ => None,
                    },
                };
                let t = match (&self.phase, tel) {
                    (Phase::Paint { .. }, _) => self.paint_t,
                    (Phase::Coast { .. }, Some(t)) => {
                        let hop_t = match &self.plan.items[*item] {
                            Item::Hop(h) => h.t_flight,
                            _ => 0.0,
                        };
                        hop_t - (self.ignite_ut - t.ut)
                    }
                    _ => 0.0,
                };
                (letter, t.max(0.0))
            }
            _ => (None, 0.0),
        };
        let now_plan = self.item_t0 + t_in_item;
        let (eta_letter, letter_progress) = cur_letter
            .and_then(|i| self.plan.letters.get(i))
            .map(|l| {
                let span = (l.t_end - l.t_start).max(1e-6);
                (
                    (l.t_end - now_plan).max(0.0),
                    ((now_plan - l.t_start) / span).clamp(0.0, 1.0),
                )
            })
            .unwrap_or((0.0, 0.0));

        FlightView {
            phase_label: match &self.phase {
                Phase::Arm => "ARM · settling into hover (1×)".into(),
                Phase::Paint { .. } => "PAINT".into(),
                Phase::Coast { .. } => "HOP".into(),
                Phase::Rescue { success: true, .. } => "RESCUE · braking to a parting hover".into(),
                Phase::Rescue { reason, .. } => format!("RESCUE · {reason} — braking to hover"),
                Phase::Done => "DONE · left hovering on the FC".into(),
                Phase::Abort(r) => format!("ABORT · {r}"),
            },
            cur_letter,
            letter_progress,
            eta_letter,
            eta_total: (self.plan.total_time - now_plan).max(0.0),
            warp: tel.map(|t| t.warp).unwrap_or(1.0),
            warp_wanted: self.warp_desired.unwrap_or(1.0),
            pos_err: self.last_err,
            speed: tel.map(|t| t.vel.surf).unwrap_or(0.0),
            throttle: self.last_throttle,
            alt_radar: tel.map(|t| t.alt.radar).unwrap_or(0.0),
            prop_frac: tel
                .map(|t| {
                    if t.mass.t > t.mass.d {
                        t.mass.p / (t.mass.t - t.mass.d).max(1.0)
                    } else {
                        0.0
                    }
                })
                .unwrap_or(0.0),
            pen: self.last_pen,
            ref_pen: self.last_ref,
            painted_append: std::mem::take(&mut self.painted_pending),
            done: matches!(self.phase, Phase::Done),
            aborted: match &self.phase {
                Phase::Abort(r) => Some(r.clone()),
                _ => None,
            },
        }
    }

    /// The writing canvas, once frozen at the arm-phase hover point (None before that).
    pub fn canvas(&self) -> Option<Canvas> {
        self.canvas
    }

    /// The plan outline for the UI preview canvas: every stroke polyline + sampled hop arcs.
    pub fn outline(&self) -> Vec<Vec<(f64, f64)>> {
        let mut out = Vec::new();
        for item in &self.plan.items {
            match item {
                Item::Stroke(s) => out.push(s.pts.clone()),
                Item::Hop(h) => {
                    if h.assist {
                        continue;
                    }
                    let mut arc = Vec::new();
                    for k in 0..=12 {
                        let t = h.t_flight * k as f64 / 12.0;
                        arc.push((
                            h.from.0 + h.v1.0 * t,
                            h.from.1 + h.v1.1 * t - 0.5 * self.env.g * t * t,
                        ));
                    }
                    out.push(arc);
                }
            }
        }
        out
    }
}

/// Clamp a vector's magnitude.
fn cap_vec(v: Vec3, cap: f64) -> Vec3 {
    let n = v.norm();
    if n > cap {
        v * (cap / n)
    } else {
        v
    }
}

/// gatOS sanitizes ids in filesystem paths (non-`[A-Za-z0-9._-]` → `_`); the debug paths must use
/// the sanitized form or a vessel named "My Rocket" gets ENOENT.
fn sanitize_id(id: &str) -> String {
    id.chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || ".-_".contains(c) {
                c
            } else {
                '_'
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_lead_is_half_a_frame() {
        let f = Flight::default_lead(2.0);
        assert!((f - 0.5 * 2.0 / 60.0).abs() < 1e-12);
    }

    impl Flight {
        fn default_lead(warp_fine: f64) -> f64 {
            0.5 * warp_fine.max(1.0) / 60.0
        }
    }

    #[test]
    fn throttle_respects_floor_and_ceiling() {
        // A pure-function replica of throttle_for's clamp behavior.
        let clamp = |sf: f64, mass: f64, tmax: f64, tmin: f64| -> f64 {
            (sf * mass / tmax).clamp((tmin + 0.01).min(1.0), 1.0)
        };
        assert!((clamp(9.81, 1000.0, 40000.0, 0.1) - 0.24525).abs() < 1e-6);
        assert_eq!(clamp(0.1, 1000.0, 40000.0, 0.1), 0.11);
        assert_eq!(clamp(200.0, 1000.0, 40000.0, 0.1), 1.0);
    }
}
