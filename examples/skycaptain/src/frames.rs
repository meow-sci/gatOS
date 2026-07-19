//! Reference-frame math for a surface-anchored skywriter.
//!
//! **Why CCF (Celestial-Centered-Fixed, the body's rotating frame) and not CCI:** KSA stores plume
//! trail segments in CCF (`PlumeSegment.StartPositionCcf`, re-projected through the body's current
//! spin each frame — `TrailPlumeSegmentsManager`), so a drawn stroke is glued to the ground and
//! co-rotates with the planet. Writing that stays readable therefore happens on a **surface-fixed
//! canvas**, and all guidance runs in CCF: "hovering" is zero *surface-relative* velocity, exactly
//! what a rocket balancing on its plume does naturally.
//!
//! `/sim` speaks CCI (`pos_cci`, `vel_cci`, `attitude_target`), so each tick we recover the body's
//! hour angle from the reported longitude (the land-o-matic trick: the same physical point has CCI
//! longitude `atan2(y,x)` and CCF longitude `position/lon`; the difference is the rotation angle) and
//! rotate between the two frames. Both share +Z = the spin axis.

use crate::vec3::Vec3;

/// Local East-North-Up unit vectors at a position (valid in whichever frame `r` is expressed in —
/// CCF and CCI share the +Z spin axis, so the construction is identical).
#[derive(Clone, Copy, Debug)]
pub struct EnuBasis {
    pub east: Vec3,
    pub north: Vec3,
    pub up: Vec3,
}

/// ENU basis at `r`: Up = radial out, East = ẑ×Up (degenerates at the poles → falls back to +X),
/// North = Up×East.
pub fn enu_basis(r: Vec3) -> EnuBasis {
    let up = r.normalize_or(Vec3::z());
    let east = Vec3::z().cross(&up).normalize_or(Vec3::x());
    let north = up.cross(&east);
    EnuBasis { east, north, up }
}

/// Rotation about +Z by `angle` (radians): the CCF↔CCI hour-angle rotation.
pub fn rotate_z(v: Vec3, angle: f64) -> Vec3 {
    let (s, c) = angle.sin_cos();
    Vec3::new(c * v.x - s * v.y, s * v.x + c * v.y, v.z)
}

/// The body's current hour angle θ, recovered from one CCI position and its reported CCF longitude
/// (degrees): a point at CCF longitude λ sits at CCI longitude λ+θ. `ccf_to_cci` is then `Rz(θ)`.
pub fn rotation_angle(pos_cci: Vec3, lon_deg: f64) -> f64 {
    pos_cci.y.atan2(pos_cci.x) - lon_deg.to_radians()
}

pub fn cci_to_ccf(v: Vec3, theta: f64) -> Vec3 {
    rotate_z(v, -theta)
}

pub fn ccf_to_cci(v: Vec3, theta: f64) -> Vec3 {
    rotate_z(v, theta)
}

/// Surface-relative (rotating-frame) velocity expressed in CCI axes: `v_cci − ω×r`.
pub fn surface_velocity_cci(r_cci: Vec3, v_cci: Vec3, rotation_rate: f64) -> Vec3 {
    let omega = Vec3::new(0.0, 0.0, rotation_rate);
    v_cci - omega.cross(&r_cci)
}

/// Gravity μ/r² toward the body center, at `r` (any body-centered frame).
pub fn gravity(r: Vec3, mu: f64) -> Vec3 {
    let d2 = r.dot(&r);
    -mu / d2 * r.normalize()
}

/// Centrifugal acceleration `−ω×(ω×r)` felt in the rotating CCF frame.
pub fn centrifugal_ccf(r_ccf: Vec3, rotation_rate: f64) -> Vec3 {
    let omega = Vec3::new(0.0, 0.0, rotation_rate);
    -omega.cross(&omega.cross(&r_ccf))
}

/// Coriolis acceleration `−2ω×v` felt in the rotating CCF frame at CCF velocity `v_ccf`.
pub fn coriolis_ccf(v_ccf: Vec3, rotation_rate: f64) -> Vec3 {
    let omega = Vec3::new(0.0, 0.0, rotation_rate);
    -2.0 * omega.cross(&v_ccf)
}

/// One tick's worth of frame state: the hour angle, plus the vehicle's CCF position and CCF
/// (rotating-frame) velocity, derived from the CCI telemetry.
#[derive(Clone, Copy, Debug)]
pub struct FrameTick {
    pub theta: f64,
    pub pos_ccf: Vec3,
    pub vel_ccf: Vec3,
}

pub fn frame_tick(pos_cci: Vec3, vel_cci: Vec3, lon_deg: f64, rotation_rate: f64) -> FrameTick {
    let theta = rotation_angle(pos_cci, lon_deg);
    let pos_ccf = cci_to_ccf(pos_cci, theta);
    let vel_ccf = cci_to_ccf(surface_velocity_cci(pos_cci, vel_cci, rotation_rate), theta);
    FrameTick {
        theta,
        pos_ccf,
        vel_ccf,
    }
}

// ---- the canvas ---------------------------------------------------------------------------------

/// The surface-fixed writing plane. Anchored in CCF at the position the pen starts; `e_hat` is the
/// reading direction (horizontal, at the requested compass heading), `u_hat` the local vertical,
/// `n_hat = e_hat × u_hat` the plane normal. Canvas coordinates are meters: `a` along the text,
/// `b` up, `n` off-plane (guidance servos `n → 0`).
#[derive(Clone, Copy, Debug)]
pub struct Canvas {
    pub anchor_ccf: Vec3,
    pub e_hat: Vec3,
    pub u_hat: Vec3,
    pub n_hat: Vec3,
}

impl Canvas {
    /// Build the canvas at a CCF anchor. `heading_deg` is the compass heading the text runs toward
    /// (0 = North, 90 = East — the default reads left→right for a viewer looking north).
    pub fn new(anchor_ccf: Vec3, heading_deg: f64) -> Canvas {
        let enu = enu_basis(anchor_ccf);
        let h = heading_deg.to_radians();
        let e_hat = (enu.east * h.sin() + enu.north * h.cos()).normalize();
        let u_hat = enu.up;
        let n_hat = e_hat.cross(&u_hat);
        Canvas {
            anchor_ccf,
            e_hat,
            u_hat,
            n_hat,
        }
    }

    /// CCF point → canvas `(a, b, n)`.
    pub fn to_canvas(&self, p_ccf: Vec3) -> Vec3 {
        let d = p_ccf - self.anchor_ccf;
        Vec3::new(d.dot(&self.e_hat), d.dot(&self.u_hat), d.dot(&self.n_hat))
    }

    /// Canvas `(a, b, n)` → CCF point.
    pub fn from_canvas(&self, c: Vec3) -> Vec3 {
        self.anchor_ccf + self.e_hat * c.x + self.u_hat * c.y + self.n_hat * c.z
    }

    /// Direction-only versions (no anchor translation).
    pub fn dir_to_canvas(&self, d_ccf: Vec3) -> Vec3 {
        Vec3::new(
            d_ccf.dot(&self.e_hat),
            d_ccf.dot(&self.u_hat),
            d_ccf.dot(&self.n_hat),
        )
    }

    pub fn dir_from_canvas(&self, c: Vec3) -> Vec3 {
        self.e_hat * c.x + self.u_hat * c.y + self.n_hat * c.z
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(a: Vec3, b: Vec3) -> bool {
        (a - b).norm() < 1e-9
    }

    #[test]
    fn enu_is_orthonormal() {
        let e = enu_basis(Vec3::new(6.0e6, 2.0e6, 3.0e6));
        assert!((e.east.norm() - 1.0).abs() < 1e-12);
        assert!(e.east.dot(&e.north).abs() < 1e-12);
        assert!(e.east.dot(&e.up).abs() < 1e-12);
        assert!(close(e.east.cross(&e.north), e.up));
    }

    #[test]
    fn ccf_cci_round_trip() {
        let v = Vec3::new(1.0, 2.0, 3.0);
        let t = 0.7;
        assert!(close(cci_to_ccf(ccf_to_cci(v, t), t), v));
    }

    #[test]
    fn corotating_point_has_zero_ccf_velocity() {
        // A point riding the surface: v_cci = ω×r. Its rotating-frame velocity must be ~0.
        let r = Vec3::new(6.4e6, 0.0, 1.0e5);
        let w = 7.29e-5;
        let v = Vec3::new(0.0, 0.0, w).cross(&r);
        let ft = frame_tick(r, v, 0.0, w);
        assert!(ft.vel_ccf.norm() < 1e-9);
    }

    #[test]
    fn canvas_round_trip_and_axes() {
        let anchor = Vec3::new(6.4e6, 1.0e5, 2.0e6);
        let cv = Canvas::new(anchor, 90.0); // text runs east
        let p = cv.from_canvas(Vec3::new(500.0, -200.0, 30.0));
        assert!(close(cv.to_canvas(p), Vec3::new(500.0, -200.0, 30.0)));
        // heading 90 = East: e_hat ⋅ east ≈ 1
        let enu = enu_basis(anchor);
        assert!((cv.e_hat.dot(&enu.east) - 1.0).abs() < 1e-9);
        // b is up
        assert!((cv.u_hat.dot(&enu.up) - 1.0).abs() < 1e-9);
    }

    #[test]
    fn gravity_points_inward() {
        let g = gravity(Vec3::new(7.0e6, 0.0, 0.0), 3.986e14);
        assert!(g.x < 0.0 && g.y.abs() < 1e-12);
        assert!((g.norm() - 3.986e14 / (7.0e6f64 * 7.0e6)).abs() < 1e-6);
    }
}
