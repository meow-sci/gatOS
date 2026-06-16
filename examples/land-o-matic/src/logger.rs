//! Optional file logger for debugging the guidance loop (enable with `--log <path>`).
//!
//! This lives in the **app/binary layer on purpose**: all I/O stays out of `guidance/` (the
//! purity rule, `LANDING_PROGRAM_PLAN.md` §8.2). The solver hands back pure-data diagnostics
//! ([`SolveTrace`]); this module turns a tick — the vehicle state, the derived physics, the pilot
//! inputs, and that trace — into a human-readable record appended to the log file.
//!
//! Tail it live from another purrTTY tab while the TUI runs: `tail -f land-o-matic.log`.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::Path;

use land_o_matic::guidance::autopilot::{Command, Inputs, Phase, State, VehicleSpec};
use land_o_matic::guidance::frames;
use land_o_matic::guidance::gfold::{SolveTrace, StageTrace};
use land_o_matic::guidance::vehicle::G0;

/// Appends formatted guidance records to a file. I/O errors are swallowed — logging must never take
/// down the guidance loop.
pub struct Logger {
    file: File,
}

impl Logger {
    /// Open (create/append) the log file and write a session header.
    pub fn open(path: &str) -> std::io::Result<Self> {
        let file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(Path::new(path))?;
        let mut logger = Logger { file };
        let _ = writeln!(
            logger.file,
            "\n==== land-o-matic guidance log opened (units SI: m, m/s, m/s², kg, N) ===="
        );
        let _ = logger.file.flush();
        Ok(logger)
    }

    /// Record one held tick (paused / time-warp / stale telemetry — no guidance ran).
    pub fn record_hold(&mut self, ut: f64, seq: u64, reason: &str) {
        let _ = writeln!(self.file, "ut={ut:.1} seq={seq}  HELD: {reason}");
        let _ = self.file.flush();
    }

    /// Record one guidance tick: state, derived physics, inputs, the solver trace, and a heuristic
    /// feasibility diagnosis. `trace` is the autopilot's latest [`SolveTrace`] (`None` when no G-FOLD
    /// solve ran this tick — e.g. the UPFG terminal leg).
    pub fn record(
        &mut self,
        seq: u64,
        state: &State,
        spec: &VehicleSpec,
        inputs: &Inputs,
        cmd: &Command,
        trace: Option<&SolveTrace>,
    ) {
        let s = format_record(seq, state, spec, inputs, cmd, trace);
        let _ = self.file.write_all(s.as_bytes());
        let _ = self.file.flush();
    }
}

fn format_record(
    seq: u64,
    state: &State,
    spec: &VehicleSpec,
    inputs: &Inputs,
    cmd: &Command,
    trace: Option<&SolveTrace>,
) -> String {
    // ---- derived physics (the same transforms the autopilot uses) ----
    let ut = state.ut;
    let r = state.pos_cci.norm();
    let up = if r > 0.0 {
        state.pos_cci / r
    } else {
        state.pos_cci
    };
    let v_surf = frames::surface_velocity(state.vel_cci, state.pos_cci, state.omega);
    let v_vert = v_surf.dot(&up); // + up / − down
    let v_horiz = (v_surf - up * v_vert).norm();
    let g_local = if r > 0.0 { state.mu / (r * r) } else { 0.0 };

    let a_cap = inputs.g_limit * G0; // commanded deceleration ceiling
    let a_thrust = if state.mass > 0.0 {
        spec.thrust_max / state.mass
    } else {
        0.0
    };
    let a_eff = a_cap.min(a_thrust); // can exceed neither the G-limit nor available thrust
    let twr_wet = if g_local > 0.0 {
        a_thrust / g_local
    } else {
        0.0
    };

    let mut out = String::new();
    let p = format!("{:?}", cmd.phase);
    out.push_str(&format!(
        "\n==== ut={ut:.1} seq={seq} phase={p} throttle={:.0}% tgo={:.0}s ====\n",
        cmd.throttle * 100.0,
        cmd.tgo
    ));
    out.push_str(&format!(
        "state  pos_cci=[{:.0}, {:.0}, {:.0}] |r|={r:.0}m  alt_radar={:.0}m\n",
        state.pos_cci.x, state.pos_cci.y, state.pos_cci.z, state.radar_alt
    ));
    out.push_str(&format!(
            "       vel_cci=[{:.1}, {:.1}, {:.1}]  v_surf={:.1} (descent={:.1} horiz={:.1})  mass={:.0}kg\n",
            state.vel_cci.x, state.vel_cci.y, state.vel_cci.z,
            v_surf.norm(), -v_vert, v_horiz, state.mass
        ));
    out.push_str(&format!(
        "body   mu={:.4e}  g_local={g_local:.3}  omega={:.4e}  density={:.4}\n",
        state.mu, state.omega, state.density
    ));
    out.push_str(&format!(
            "spec   F_max={:.0}N  isp={:.0}s  thr_min={:.2}  a_max(wet→now)={a_thrust:.2}m/s² ({:.2}g)  TWR(now)={twr_wet:.2}\n",
            spec.thrust_max, spec.isp, spec.throttle_min, a_thrust / G0
        ));
    out.push_str(&format!(
            "limits g_limit={:.2}g (a_cap={a_cap:.2}m/s²)  glide={:.0}°  point={:.0}°  v_max={:.0}  n={}\n",
            inputs.g_limit, inputs.glide_slope_deg, inputs.pointing_deg, inputs.v_max, inputs.n
        ));

    // ---- solver trace (only meaningful when a G-FOLD solve ran this tick) ----
    if matches!(cmd.phase, Phase::Burn | Phase::Infeasible) {
        if let Some(tr) = trace {
            out.push_str(&format!("solve  outcome=\"{}\"\n", tr.outcome));
            let sm = &tr.summary;
            out.push_str(&format!(
                "       ENU r0=[{:.0}, {:.0}, {:.0}]  v0=[{:.1}, {:.1}, {:.1}]  (east,north,up)\n",
                sm.r0.x, sm.r0.y, sm.r0.z, sm.v0.x, sm.v0.y, sm.v0.z
            ));
            for st in &tr.stages {
                out.push_str(&format_stage(st));
            }
            if let Some(aim) = tr.aim {
                out.push_str(&format!(
                    "       P3 aim point (ENU) = [{:.0}, {:.0}, {:.0}]  ({:.0}m downrange)\n",
                    aim.x,
                    aim.y,
                    aim.z,
                    aim.x.hypot(aim.y)
                ));
            }
        }
    }

    // ---- heuristic diagnosis: is this state even reachable at this G-limit? ----
    if a_eff > 0.0 && v_horiz > 1.0 {
        let brake_dist = v_horiz * v_horiz / (2.0 * a_eff);
        let brake_time = v_horiz / a_eff;
        let natural_angle = state.radar_alt.atan2(brake_dist).to_degrees();
        out.push_str(&format!(
                "diag   horiz Δv {v_horiz:.0} m/s needs ≥{brake_time:.0}s of braking and ≈{:.1}km downrange\n",
                brake_dist / 1000.0
            ));
        out.push_str(&format!(
                "       altitude budget {:.1}km → natural approach ≈{natural_angle:.1}° vs configured glide {:.0}°{}\n",
                state.radar_alt / 1000.0,
                inputs.glide_slope_deg,
                if brake_dist > state.radar_alt * 3.0 {
                    "  ⚠ too fast/low for this G-limit"
                } else {
                    ""
                }
            ));
        // Near-zero descent at high altitude with ~orbital horizontal speed = still in orbit, not on a
        // powered-descent trajectory. G-FOLD stays infeasible (you'd orbit away / exceed v_max long
        // before reaching the pad) until periapsis is dropped below the surface.
        if (-v_vert).abs() < 0.05 * v_horiz && state.radar_alt > brake_dist.max(1.0) * 5.0 {
            out.push_str(
                "       ⚠ near-circular orbit (≈0 descent at high altitude) — not a descent state; \
                 deorbit (lower periapsis below the surface) before engaging\n",
            );
        }
    }

    out
}

/// Format one P3/P4 stage's time-of-flight search.
fn format_stage(st: &StageTrace) -> String {
    let (feas, total) = st.feasible_count();
    let hist = st
        .status_histogram()
        .iter()
        .map(|(k, n)| format!("{k}×{n}"))
        .collect::<Vec<_>>()
        .join(", ");
    let chosen = st
        .chosen_tf
        .map(|t| format!("  chosen_tf={t:.1}s"))
        .unwrap_or_default();
    format!(
        "       {}: tf∈[{:.1},{:.1}] bounds_ok={}  feasible {feas}/{total}  {{{hist}}}{chosen}\n",
        st.objective, st.t_lo, st.t_hi, st.bounds_ok
    )
}
