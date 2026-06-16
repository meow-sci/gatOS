//! Conic State Extrapolation (CSE) — a faithful Rust port of Shepperd & Robertson's universal-variable
//! two-body propagator (`thirdparty/PEGAS-MATLAB/MATLAB/CSEroutine.m`, "Space Shuttle GN&C Equation
//! Document 25"). Given a state under pure central gravity it extrapolates the state `dt` into the
//! future. UPFG's Block 7 ([`super::upfg`]) uses it to estimate the gravity contribution over the
//! time-to-go — exactly KSA's point-mass `μ/r²` gravity model, so this leg of the guidance carries no
//! flat-planet error (plan §3.6, §6.3).
//!
//! The port is line-for-line with the reference (plates 5-2 … 5-16 and the `KTTI`/`USS`/`QCF`/`KIL`/`SI`
//! subroutines), with the body's gravitational parameter `mu` passed explicitly (the MATLAB uses a
//! global). The reference's "silly-dash" scaled variables keep their names (`…s` suffix). The upstream
//! routine is "simulation-proven correct"; the [`tests`] re-prove it against an analytic circular orbit
//! and an RK4-integrated ellipse.

use super::types::Vec3;

/// Persistent CSE state carried between calls (the reference `cser` struct): the previously converged
/// transfer-time interval `dtcp` and independent variable `xcp`, plus the `A, D, E` universal-function
/// values that warm-start the Kepler iteration. Zero-initialized (`default`) on the first call.
#[derive(Debug, Clone, Copy, Default)]
pub struct CserState {
    pub dtcp: f64,
    pub xcp: f64,
    pub a: f64,
    pub d: f64,
    pub e: f64,
}

/// Extrapolate `(r0, v0)` forward by `dt` under central gravity `mu`. Returns the future `(r, v)` and the
/// updated [`CserState`] (feed it back as `last` next call to warm-start). Port of `CSEroutine`.
pub fn cse_routine(
    r0: Vec3,
    v0: Vec3,
    dt: f64,
    last: CserState,
    mu: f64,
) -> (Vec3, Vec3, CserState) {
    let dtcp = if last.dtcp == 0.0 { dt } else { last.dtcp };
    let xcp = last.xcp;
    let x = xcp;
    let kmax = 10;
    let imax = 10;

    // 5.1 ROUTINE — PLATE 5-2
    let f0 = if dt >= 0.0 { 1.0 } else { -1.0 };

    let mut n = 0i32;
    let r0m = r0.norm();

    let f1 = f0 * (r0m / mu).sqrt();
    let f2 = 1.0 / f1;
    let f3 = f2 / r0m;
    let f4 = f1 * r0m;
    let f5 = f0 / r0m.sqrt();
    let f6 = f0 * r0m.sqrt();

    let ir0 = r0 / r0m;
    let v0s = f1 * v0; // v0 with the silly dash
    let sigma0s = ir0.dot(&v0s);
    let b0 = v0s.dot(&v0s) - 1.0;
    let alphas = 1.0 - b0;

    // PLATE 5-3
    let mut xguess = f5 * x;
    let mut xlast = f5 * xcp;
    let mut xmin = 0.0;
    let mut dts = f3 * dt;
    let mut dtlast = f3 * dtcp;
    let mut dtmin = 0.0;

    // assuming the orbit is not parabolic, sqrt(|alphas|) is well away from zero
    let mut xmax = 2.0 * std::f64::consts::PI / alphas.abs().sqrt();

    let mut xp = 0.0; // xP — only used (and set) on the alphas>0 path, where n>0 can occur
    let mut ps = 0.0; // Ps
    let mut dtmax;

    if alphas > 0.0 {
        dtmax = xmax / alphas;
        xp = xmax;
        ps = dtmax;
        while dts >= ps {
            n += 1;
            dts -= ps;
            dtlast -= ps;
            xguess -= xp;
            xlast -= xp;
        }
    } else {
        let (t, _, _, _) = ktti(xmax, sigma0s, alphas, kmax);
        dtmax = t;
        if dtmax < dts {
            while dtmax < dts {
                dtmin = dtmax;
                xmin = xmax;
                xmax *= 2.0;
                let (t2, _, _, _) = ktti(xmax, sigma0s, alphas, kmax);
                dtmax = t2;
            }
        }
    }

    // PLATE 5-4
    if xmin >= xguess || xguess >= xmax {
        xguess = 0.5 * (xmin + xmax);
    }

    // Capture A, D, E from this KTTI(xguess) — not from `last` as the reference does. If the Kepler
    // loop below finds `xguess` already converged it breaks before its own KTTI, so the universal-function
    // values must already correspond to *this* xguess; warm-starting them from a previous solve (as the
    // MATLAB does) leaves them stale and yields r ≈ r0 when the first guess is exact (e.g. a circular
    // orbit at exactly half-period). KIL overwrites them whenever it does iterate, so this is strictly
    // more correct.
    let (mut dtguess, mut un_a, mut un_d, mut un_e) = ktti(xguess, sigma0s, alphas, kmax);

    if dts < dtguess {
        if xguess < xlast && xlast < xmax && dtguess < dtlast && dtlast < dtmax {
            xmax = xlast;
            dtmax = dtlast;
        }
    } else if xmin < xlast && xlast < xguess && dtmin < dtlast && dtlast < dtguess {
        xmin = xlast;
        dtmin = dtlast;
    }

    let kil_out = kil(
        imax, dts, xguess, dtguess, xmin, dtmin, xmax, dtmax, sigma0s, alphas, kmax, un_a, un_d,
        un_e,
    );
    xguess = kil_out.0;
    dtguess = kil_out.1;
    un_a = kil_out.2;
    un_d = kil_out.3;
    un_e = kil_out.4;

    // PLATE 5-5
    let rs = 1.0 + 2.0 * (b0 * un_a + sigma0s * un_d * un_e); // r with the silly dash
    let b4 = 1.0 / rs;

    let (xc, dtc) = if n > 0 {
        (
            f6 * (xguess + n as f64 * xp),
            f4 * (dtguess + n as f64 * ps),
        )
    } else {
        (f6 * xguess, f4 * dtguess)
    };

    let new_last = CserState {
        dtcp: dtc,
        xcp: xc,
        a: un_a,
        d: un_d,
        e: un_e,
    };

    // Extrapolated State Vector (ROUTINE 5.3.6, PLATE 5-16) — inline
    let f = 1.0 - 2.0 * un_a;
    let gs = 2.0 * (un_d * un_e + sigma0s * un_a); // G with the silly dash
    let fts = -2.0 * b4 * un_d * un_e; // Ft with the silly dash
    let gt = 1.0 - 2.0 * b4 * un_a;

    let r = r0m * (f * ir0 + gs * v0s);
    let v = f2 * (fts * ir0 + gt * v0s);
    (r, v, new_last)
}

/// 5.3.1 — Kepler Transfer Time Interval (PLATE 5-9). Returns `(t, A, D, E)`.
fn ktti(xarg: f64, s0s: f64, a: f64, kmax: i32) -> (f64, f64, f64, f64) {
    let u1 = uss(xarg, a, kmax);
    let zs = 2.0 * u1; // z with the silly dash
    let e = 1.0 - 0.5 * a * zs * zs;
    let w = (0.5 + e / 2.0).max(0.0).sqrt(); // safety: never sqrt a negative
    let d = w * zs;
    let a_out = d * d;
    let b = 2.0 * (e + s0s * d);
    let q = qcf(w);
    let t = d * (b + a_out * q);
    (t, a_out, d, e)
}

/// 5.3.2 — U1 Series Summation (PLATE 5-10).
fn uss(xarg: f64, a: f64, kmax: i32) -> f64 {
    let mut du1 = xarg / 4.0;
    let mut u1 = du1;
    let f7 = -a * du1 * du1;
    let mut k = 3;
    while k < kmax {
        du1 = f7 * du1 / (k as f64 * (k as f64 - 1.0));
        let u1old = u1;
        u1 += du1;
        if u1 == u1old {
            break;
        }
        k += 2;
    }
    u1
}

/// 5.3.3 — Q Continued Fraction (PLATES 5-11 / 5-12).
fn qcf(w: f64) -> f64 {
    let xq = if w < 1.0 {
        21.04 - 13.04 * w
    } else if w < 4.625 {
        (5.0 / 3.0) * (2.0 * w + 5.0)
    } else if w < 13.846 {
        (10.0 / 7.0) * (w + 12.0)
    } else if w < 44.0 {
        0.5 * (w + 60.0)
    } else if w < 100.0 {
        0.25 * (w + 164.0)
    } else {
        70.0
    };

    let y = (w - 1.0) / (w + 1.0);
    let mut j = xq.floor();
    let mut b = y / (1.0 + (j - 1.0) / (j + 2.0)); // first pass with b₀ = 0 ⇒ (1−b)=1
    while j > 2.0 {
        j -= 1.0;
        b = y / (1.0 + (j - 1.0) / (j + 2.0) * (1.0 - b));
    }
    1.0 / (w * w) * (1.0 + (2.0 - b / 2.0) / (3.0 * w * (w + 1.0)))
}

/// 5.3.4 — Kepler Iteration Loop (PLATES 5-13 / 5-14). Returns `(xguess, dtguess, A, D, E)`.
#[allow(clippy::too_many_arguments)]
fn kil(
    imax: i32,
    dts: f64,
    mut xguess: f64,
    mut dtguess: f64,
    mut xmin: f64,
    mut dtmin: f64,
    mut xmax: f64,
    mut dtmax: f64,
    s0s: f64,
    a: f64,
    kmax: i32,
    mut un_a: f64,
    mut un_d: f64,
    mut un_e: f64,
) -> (f64, f64, f64, f64, f64) {
    let mut i = 1;
    while i < imax {
        let dterror = dts - dtguess;
        if dterror.abs() < 1e-6 {
            break;
        }
        let (dxs, nxmin, ndtmin, nxmax, ndtmax) =
            si(dterror, xguess, dtguess, xmin, dtmin, xmax, dtmax);
        xmin = nxmin;
        dtmin = ndtmin;
        xmax = nxmax;
        dtmax = ndtmax;
        let xold = xguess;
        xguess += dxs;
        if xguess == xold {
            break;
        }
        let dtold = dtguess;
        let (t, ka, kd, ke) = ktti(xguess, s0s, a, kmax);
        dtguess = t;
        un_a = ka;
        un_d = kd;
        un_e = ke;
        if dtguess == dtold {
            break;
        }
        i += 1;
    }
    (xguess, dtguess, un_a, un_d, un_e)
}

/// 5.3.5 — Secant Iterator (PLATE 5-15). Returns `(dxs, xmin, dtmin, xmax, dtmax)`.
#[allow(clippy::too_many_arguments)]
fn si(
    dterror: f64,
    xguess: f64,
    dtguess: f64,
    mut xmin: f64,
    mut dtmin: f64,
    mut xmax: f64,
    mut dtmax: f64,
) -> (f64, f64, f64, f64, f64) {
    let etp = 1e-6;
    let dtminp = dtguess - dtmin; // delta tmin prim
    let dtmaxp = dtguess - dtmax;
    let dxs;
    if dtminp.abs() < etp || dtmaxp.abs() < etp {
        dxs = 0.0;
    } else if dterror < 0.0 {
        let mut d = (xguess - xmax) * (dterror / dtmaxp);
        if (xguess + d) <= xmin {
            d = (xguess - xmin) * (dterror / dtminp);
        }
        dxs = d;
        xmax = xguess;
        dtmax = dtguess;
    } else {
        let mut d = (xguess - xmin) * (dterror / dtminp);
        if (xguess + d) >= xmax {
            d = (xguess - xmax) * (dterror / dtmaxp);
        }
        dxs = d;
        xmin = xguess;
        dtmin = dtguess;
    }
    (dxs, xmin, dtmin, xmax, dtmax)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// RK4 two-body reference propagator (`r'' = −μ r/|r|³`) for parity checks.
    fn rk4_two_body(mut r: Vec3, mut v: Vec3, dt: f64, steps: usize, mu: f64) -> (Vec3, Vec3) {
        let h = dt / steps as f64;
        let accel = |r: Vec3| -mu * r / r.norm().powi(3);
        for _ in 0..steps {
            let k1v = accel(r);
            let k1r = v;
            let k2v = accel(r + 0.5 * h * k1r);
            let k2r = v + 0.5 * h * k1v;
            let k3v = accel(r + 0.5 * h * k2r);
            let k3r = v + 0.5 * h * k2v;
            let k4v = accel(r + h * k3r);
            let k4r = v + h * k3v;
            r += (h / 6.0) * (k1r + 2.0 * k2r + 2.0 * k3r + k4r);
            v += (h / 6.0) * (k1v + 2.0 * k2v + 2.0 * k3v + k4v);
        }
        (r, v)
    }

    /// A circular orbit propagates to the analytically known rotated state.
    #[test]
    fn circular_orbit_matches_analytic() {
        let mu: f64 = 3.986_004_418e14; // Earth
        let radius: f64 = 7.0e6;
        let v_circ = (mu / radius).sqrt();
        let r0 = Vec3::new(radius, 0.0, 0.0);
        let v0 = Vec3::new(0.0, v_circ, 0.0); // CCW in the XY plane
        let period = std::f64::consts::TAU * (radius.powi(3) / mu).sqrt();

        // Quarter, half, three-quarter, full period → exact rotations.
        for (frac, exp_r, exp_v) in [
            (
                0.25,
                Vec3::new(0.0, radius, 0.0),
                Vec3::new(-v_circ, 0.0, 0.0),
            ),
            (
                0.5,
                Vec3::new(-radius, 0.0, 0.0),
                Vec3::new(0.0, -v_circ, 0.0),
            ),
            (
                0.75,
                Vec3::new(0.0, -radius, 0.0),
                Vec3::new(v_circ, 0.0, 0.0),
            ),
            (1.0, r0, v0),
        ] {
            let (r, v, _) = cse_routine(r0, v0, frac * period, CserState::default(), mu);
            // ~10 m / 0.05 m/s: the Kepler loop converges to 1e-6 in scaled time, ≈1 ms ≈ v·1 ms here.
            assert!(
                (r - exp_r).norm() < 10.0,
                "frac {frac}: pos {r:?} != {exp_r:?}"
            );
            assert!(
                (v - exp_v).norm() < 0.05,
                "frac {frac}: vel {v:?} != {exp_v:?}"
            );
        }
    }

    /// An elliptical orbit matches a high-resolution RK4 integration.
    #[test]
    fn elliptical_orbit_matches_rk4() {
        let mu = 3.986_004_418e14;
        let r0 = Vec3::new(7.0e6, 0.0, 0.0);
        let v0 = Vec3::new(500.0, 8200.0, 300.0); // eccentric, slightly out of plane
        for &dt in &[120.0, 600.0, 1500.0] {
            let (r, v, _) = cse_routine(r0, v0, dt, CserState::default(), mu);
            let (rr, vr) = rk4_two_body(r0, v0, dt, 20_000, mu);
            assert!(
                (r - rr).norm() < 5.0,
                "dt {dt}: pos err {}",
                (r - rr).norm()
            );
            assert!(
                (v - vr).norm() < 1e-2,
                "dt {dt}: vel err {}",
                (v - vr).norm()
            );
        }
    }

    /// Forward then backward propagation returns to the start (each with a fresh warm-start).
    #[test]
    fn forward_back_round_trip() {
        let mu = 3.986_004_418e14;
        let r0 = Vec3::new(6.8e6, 1.0e6, 0.0);
        let v0 = Vec3::new(-300.0, 7600.0, 1200.0);
        let dt = 800.0;
        let (r1, v1, _) = cse_routine(r0, v0, dt, CserState::default(), mu);
        let (r2, v2, _) = cse_routine(r1, v1, -dt, CserState::default(), mu);
        assert!(
            (r2 - r0).norm() < 12.0,
            "round-trip pos err {}",
            (r2 - r0).norm()
        );
        assert!(
            (v2 - v0).norm() < 0.05,
            "round-trip vel err {}",
            (v2 - v0).norm()
        );
    }

    /// Warm-starting from the previous `cser` gives the same answer as a cold start (the warm start is a
    /// convergence optimization, not a result change).
    #[test]
    fn warm_start_matches_cold() {
        let mu = 4.9048695e12; // Moon-ish
        let r0 = Vec3::new(1.8e6, 0.0, 0.0);
        let v0 = Vec3::new(0.0, 1600.0, 0.0);
        let (rc, vc, _) = cse_routine(r0, v0, 300.0, CserState::default(), mu);
        // Prime a cser with a nearby call, then re-propagate the same step.
        let (_, _, primed) = cse_routine(r0, v0, 290.0, CserState::default(), mu);
        let (rw, vw, _) = cse_routine(r0, v0, 300.0, primed, mu);
        assert!(
            (rc - rw).norm() < 1e-2,
            "warm vs cold pos {}",
            (rc - rw).norm()
        );
        assert!((vc - vw).norm() < 1e-4);
    }
}
