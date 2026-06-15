//! Reference-frame transforms between CCI (parent-centred inertial — the `/sim` I/O frame), CCF
//! (body-fixed, rotating — where the landing site is stationary) and a target-centred ENU surface
//! frame (the G-FOLD working frame). See `LANDING_PROGRAM_PLAN.md` §3.
//!
//! Conventions, taken from the KSA source (cited in the plan): CCI/CCF are right-handed with **+Z the
//! spin axis**; ENU is `up = r̂`, `east = normalize(Ẑ × r)`, `north = up × east`
//! (`VehicleReferenceFrameEx.GetEnu2Cci`); CCF lat/lon are `lat = asin(z)`, `lon = atan2(y, x)`
//! (`Celestial.GetLlaFromCcf`); the body spins about +Z at rate ω (`IParentBody.GetAngularVelocityCci`).

use super::types::Vec3;

/// A local East-North-Up basis at a CCI position, expressed **in CCI**. This is also the G→CCI rotation
/// for the target-centred guidance frame (columns east/north/up).
#[derive(Debug, Clone, Copy)]
pub struct EnuBasis {
    pub east: Vec3,
    pub north: Vec3,
    pub up: Vec3,
}

/// ENU basis at a CCI position (`GetEnu2Cci`). At the poles `Ẑ × r` degenerates; we fall back to CCI +X
/// for east (the guidance never lands at a geographic pole, but stay defined).
pub fn enu_basis(pos_cci: Vec3) -> EnuBasis {
    let up = pos_cci.normalize();
    let z = Vec3::new(0.0, 0.0, 1.0);
    let east_raw = z.cross(&up);
    let east = if east_raw.norm() < 1e-9 {
        Vec3::new(1.0, 0.0, 0.0)
    } else {
        east_raw.normalize()
    };
    let north = up.cross(&east);
    EnuBasis { east, north, up }
}

/// Surface-relative velocity: `v_cci − ω × r_cci`, ω about CCI +Z (KSA `Vehicle.GetSurfaceSpeed` math).
/// This is the velocity to guide on — relative to the ground the vehicle is landing on.
pub fn surface_velocity(vel_cci: Vec3, pos_cci: Vec3, omega: f64) -> Vec3 {
    let w = Vec3::new(0.0, 0.0, omega);
    vel_cci - w.cross(&pos_cci)
}

/// Project a CCI vector onto an ENU basis → `(east, north, up)` components.
pub fn to_enu(v_cci: Vec3, b: &EnuBasis) -> Vec3 {
    Vec3::new(v_cci.dot(&b.east), v_cci.dot(&b.north), v_cci.dot(&b.up))
}

/// Reconstruct a CCI vector from ENU components (the inverse of [`to_enu`]). This is the output
/// transform for a G-frame thrust vector → CCI.
pub fn from_enu(v_enu: Vec3, b: &EnuBasis) -> Vec3 {
    b.east * v_enu.x + b.north * v_enu.y + b.up * v_enu.z
}

/// CCF unit direction from geodetic lat/lon in degrees (`Celestial.GetDirCcfFromLatLon`):
/// `x = cos lat cos lon, y = cos lat sin lon, z = sin lat`.
pub fn dir_ccf_from_latlon(lat_deg: f64, lon_deg: f64) -> Vec3 {
    let (lat, lon) = (lat_deg.to_radians(), lon_deg.to_radians());
    Vec3::new(lat.cos() * lon.cos(), lat.cos() * lon.sin(), lat.sin())
}

/// Geodetic lat/lon in degrees from a CCF direction (`Celestial.GetLlaFromCcf`):
/// `lat = asin(ẑ), lon = atan2(ŷ, x̂)`.
pub fn latlon_from_ccf(dir: Vec3) -> (f64, f64) {
    let d = dir.normalize();
    (d.z.asin().to_degrees(), d.y.atan2(d.x).to_degrees())
}

/// Rotate a vector by `angle` (rad) about +Z.
pub fn rotate_z(v: Vec3, angle: f64) -> Vec3 {
    let (s, c) = angle.sin_cos();
    Vec3::new(c * v.x - s * v.y, s * v.x + c * v.y, v.z)
}

/// CCF→CCI for a vector, given the body's current rotation angle θ about +Z (`GetCcf2Cci` is a +θ
/// rotation about the spin axis).
pub fn ccf_to_cci(v_ccf: Vec3, theta: f64) -> Vec3 {
    rotate_z(v_ccf, theta)
}

/// CCI→CCF for a vector (inverse of [`ccf_to_cci`]).
pub fn cci_to_ccf(v_cci: Vec3, theta: f64) -> Vec3 {
    rotate_z(v_cci, -theta)
}

/// The body's current rotation angle θ about +Z, recovered from the vehicle's CCI position and its
/// **reported CCF longitude** (`/sim` `position/lon`): `θ = atan2(y_cci, x_cci) − lon_ccf`. This lets us
/// place an absolute lat/lon target in CCI without knowing the body's rotation epoch (plan §3.4).
pub fn rotation_angle(pos_cci: Vec3, reported_lon_deg: f64) -> f64 {
    pos_cci.y.atan2(pos_cci.x) - reported_lon_deg.to_radians()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(a: Vec3, b: Vec3) -> bool {
        (a - b).norm() < 1e-7
    }

    #[test]
    fn enu_basis_is_orthonormal_and_right_handed() {
        let pos = Vec3::new(600_000.0, 200_000.0, 150_000.0);
        let b = enu_basis(pos);
        for v in [b.east, b.north, b.up] {
            assert!((v.norm() - 1.0).abs() < 1e-9);
        }
        assert!(b.east.dot(&b.north).abs() < 1e-9);
        assert!(b.east.dot(&b.up).abs() < 1e-9);
        assert!(b.north.dot(&b.up).abs() < 1e-9);
        // up == r̂, and east×north == up (right-handed).
        assert!(close(b.up, pos.normalize()));
        assert!(close(b.east.cross(&b.north), b.up));
    }

    #[test]
    fn to_from_enu_round_trip() {
        let b = enu_basis(Vec3::new(600_000.0, 0.0, 100_000.0));
        let v = Vec3::new(-12.0, 34.0, -56.0);
        assert!(close(from_enu(to_enu(v, &b), &b), v));
    }

    #[test]
    fn surface_velocity_cancels_corotation_at_equator() {
        // On the equator at radius R, a vessel moving exactly with the surface has zero surface speed.
        let r = 600_000.0;
        let omega = 2.9e-4;
        let pos = Vec3::new(r, 0.0, 0.0);
        let vel_corot = Vec3::new(0.0, omega * r, 0.0); // ω×r at this point
        assert!(surface_velocity(vel_corot, pos, omega).norm() < 1e-6);
        // A purely radial (descending) inertial velocity is unchanged by de-rotation in the vertical.
        let descend = Vec3::new(-50.0, omega * r, 0.0); // 50 m/s down + co-rotation
        let vs = surface_velocity(descend, pos, omega);
        assert!(close(vs, Vec3::new(-50.0, 0.0, 0.0)));
    }

    #[test]
    fn latlon_round_trip() {
        for (lat, lon) in [(0.0, 0.0), (45.0, -120.0), (-33.5, 174.7), (10.0, 179.0)] {
            let dir = dir_ccf_from_latlon(lat, lon);
            let (la, lo) = latlon_from_ccf(dir);
            assert!((la - lat).abs() < 1e-7, "lat {la} != {lat}");
            assert!((lo - lon).abs() < 1e-7, "lon {lo} != {lon}");
        }
    }

    #[test]
    fn ccf_cci_are_inverse() {
        let v = Vec3::new(123.0, -456.0, 789.0);
        let theta = 1.234;
        assert!(close(cci_to_ccf(ccf_to_cci(v, theta), theta), v));
    }

    #[test]
    fn rotation_angle_recovers_theta() {
        // Place a known CCF point, rotate it into CCI by θ, then recover θ from the CCI position and the
        // (unchanged) CCF longitude.
        let theta = 0.9;
        let ccf = dir_ccf_from_latlon(20.0, 35.0) * 600_000.0;
        let cci = ccf_to_cci(ccf, theta);
        let (_, lon_ccf) = latlon_from_ccf(ccf);
        let recovered = rotation_angle(cci, lon_ccf);
        // Compare as angles (mod 2π).
        let diff = (recovered - theta).rem_euclid(std::f64::consts::TAU);
        assert!(diff < 1e-9 || (std::f64::consts::TAU - diff) < 1e-9);
    }
}
