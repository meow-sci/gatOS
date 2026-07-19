//! End-to-end: fly a whole message in the built-in physics sandbox and check the *ink*.
//!
//! This is the test that catches sign errors anywhere in the stack (frames, quaternion, controller,
//! hop solver, cut timing): the simulated vehicle must arm, paint every stroke, coast every hop,
//! and finish — and the trace of positions where the engine was burning must lie on the planned
//! letterforms, while the engine must be *off* between glyphs.

use skycaptain::flight::{Flight, FlightCfg};
use skycaptain::plan::{self, Item, PlanEnv};
use skycaptain::sim::{self, Source};
use skycaptain::simulate::{SimClock, SimWorld};

/// Distance from point `p` to segment `a`–`b`.
fn dist_seg(p: (f64, f64), a: (f64, f64), b: (f64, f64)) -> f64 {
    let (vx, vy) = (b.0 - a.0, b.1 - a.1);
    let (wx, wy) = (p.0 - a.0, p.1 - a.1);
    let len2 = vx * vx + vy * vy;
    let t = if len2 > 1e-12 {
        ((wx * vx + wy * vy) / len2).clamp(0.0, 1.0)
    } else {
        0.0
    };
    let (cx, cy) = (a.0 + vx * t - p.0, a.1 + vy * t - p.1);
    (cx * cx + cy * cy).sqrt()
}

fn dist_to_plan(p: (f64, f64), strokes: &[Vec<(f64, f64)>]) -> f64 {
    let mut best = f64::MAX;
    for s in strokes {
        for w in s.windows(2) {
            best = best.min(dist_seg(p, w[0], w[1]));
        }
    }
    best
}

#[test]
fn writes_hi_in_the_sandbox() {
    let world = SimWorld::new(SimClock::PerRead(0.12));
    let inspect = world.handle();
    let src: &dyn Source = &world;

    let cfg = FlightCfg {
        warp_draw: 4.0,
        warp_hop: 10.0,
        warp_fine: 2.0,
        ..FlightCfg::default()
    };
    let env = PlanEnv {
        height: 700.0,
        v_draw: 55.0,
        ..PlanEnv::default()
    };

    let tick = sim::poll(src);
    let mut notes = Vec::new();
    let mut flight = Flight::start(src, &tick, "HI", cfg, env, &mut notes)
        .unwrap_or_else(|e| panic!("pre-flight refused: {e} (notes: {notes:?})"));

    let mut iterations = 0u64;
    let mut phase_log: Vec<(f64, String)> = Vec::new();
    while !flight.is_over() {
        let tick = sim::poll(src);
        let view = flight.step(src, &tick);
        if phase_log
            .last()
            .map(|(_, p)| p != &view.phase_label)
            .unwrap_or(true)
        {
            phase_log.push((inspect.ut(), view.phase_label.clone()));
        }
        if let Ok(path) = std::env::var("SKYC_TRACE") {
            use std::io::Write;
            let mut f = std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(path)
                .unwrap();
            writeln!(
                f,
                "{:.2},{},{:.1},{:.3},{:.1},{:.1},{:.1},{:.1},{:.1}",
                inspect.ut(),
                view.phase_label.split(' ').next().unwrap_or(""),
                view.pos_err,
                view.throttle,
                view.pen.0,
                view.pen.1,
                view.ref_pen.0,
                view.ref_pen.1,
                view.speed
            )
            .unwrap();
        }
        iterations += 1;
        assert!(
            iterations < 40_000,
            "flight never finished (phase {:?}, ut {:.0}); log {phase_log:#?}",
            view.phase_label,
            inspect.ut()
        );
    }

    let tick = sim::poll(src);
    let view = flight.step(src, &tick);
    assert!(
        view.done,
        "flight ended in abort: {:?}; phase log {phase_log:#?}",
        view.aborted
    );

    // Reconstruct the planned strokes in canvas coordinates and grade the ink.
    let canvas = flight.canvas().expect("canvas frozen during arm");
    let strokes: Vec<Vec<(f64, f64)>> = flight
        .plan
        .items
        .iter()
        .filter_map(|i| match i {
            Item::Stroke(s) => Some(s.pts.clone()),
            _ => None,
        })
        .collect();
    assert!(strokes.len() >= 2, "HI should have at least 2 strokes");

    let trace = inspect.trace();
    let painting: Vec<(f64, f64)> = trace
        .iter()
        .filter(|(_, burning)| *burning)
        .map(|(p, _)| {
            let c = canvas.to_canvas(*p);
            (c.x, c.y)
        })
        .collect();
    let coasting = trace.iter().filter(|(_, b)| !b).count();
    assert!(
        painting.len() > 500,
        "almost no ink ({} samples)",
        painting.len()
    );
    // At least one ballistic hop must have flown (H→I is a short lob; ~40+ engine-off substeps).
    assert!(
        coasting > 40,
        "no engine-off coasting recorded — hops never happened"
    );

    // Ink fidelity: distance from the ink to the planned letterforms. Context for the bounds: the
    // KSA plume blooms to an ~80 m radius within seconds, so ink within ~½ bloom reads as "on the
    // stroke"; the excursions live at corner brakes and hop entries. (The arm phase burns a little
    // before the canvas freezes; that smear sits at the first ink point by construction.)
    let mut d: Vec<f64> = painting
        .iter()
        .map(|&p| dist_to_plan(p, &strokes))
        .collect();
    d.sort_by(|a, b| a.total_cmp(b));
    let med = d[d.len() / 2];
    let p95 = d[d.len() * 95 / 100];
    let max = *d.last().unwrap();
    assert!(med < 25.0, "median ink error {med:.1} m");
    assert!(p95 < 180.0, "p95 ink error {p95:.1} m");
    assert!(max < 400.0, "max ink error {max:.1} m");

    // Plane discipline: the ink must stay essentially on the writing plane.
    let off_plane: f64 = trace
        .iter()
        .filter(|(_, b)| *b)
        .map(|(p, _)| canvas.to_canvas(*p).z.abs())
        .fold(0.0, f64::max);
    assert!(
        off_plane < 40.0,
        "ink wandered {off_plane:.1} m off the canvas plane"
    );
}

#[test]
fn assist_hops_need_the_flag() {
    let world = SimWorld::new(SimClock::PerRead(0.12));
    let src: &dyn Source = &world;
    let tick = sim::poll(src);
    let mut notes = Vec::new();
    let err = Flight::start(
        src,
        &tick,
        "A.B",
        FlightCfg::default(),
        PlanEnv::default(),
        &mut notes,
    )
    .err()
    .expect("mid-text dot must be refused without --allow-impulse");
    assert!(err.contains("--allow-impulse"), "unexpected refusal: {err}");
}

#[test]
fn plan_env_from_live_sim_is_consistent() {
    // The planner's promise (every A–Z pair hops cleanly) must hold for the env Flight::start
    // actually derives from the sandbox vehicle, not just PlanEnv::default().
    let world = SimWorld::new(SimClock::PerRead(0.12));
    let src: &dyn Source = &world;
    let tick = sim::poll(src);
    let tel = tick.telemetry.as_ref().unwrap();
    let eng = sim::read_engines(src).unwrap();
    let r = skycaptain::vec3::Vec3::from_array(tel.pos_cci).norm();
    let g = tick.body.unwrap().mu / (r * r);
    let accel_max = eng.thrust_max / tel.mass.t;
    let env = PlanEnv {
        g,
        accel_max,
        accel_min: eng.throttle_min * accel_max,
        ..PlanEnv::default()
    };
    let plan = plan::compile(
        "THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG 0123456789",
        &env,
    );
    let assists = plan
        .items
        .iter()
        .filter(|i| matches!(i, Item::Hop(h) if h.assist))
        .count();
    assert_eq!(assists, 0, "warnings: {:?}", plan.warnings);
}
