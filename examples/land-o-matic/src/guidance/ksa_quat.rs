//! A faithful Rust port of the KSA (`Brutal.Numerics`) quaternion arithmetic the flight computer uses,
//! plus `BurnTarget.ComputeBurnBody2Cci` — the recipe for "aim the thrust axis (+X body) along a CCI
//! direction". This is the **attitude-output** path: the quaternion we write to `ctl/attitude_target`.
//!
//! Why port the exact arithmetic instead of using nalgebra's quaternion? KSA's `Transform` uses the
//! conjugate-left convention `q*·[v,1]·q` (`Brutal.Numerics/double3.cs:761`) with a specific Hamilton
//! product (`Quaternions.cs:7`), paired with `CreateFromRotationMatrix` (`doubleQuat.cs:256`). By
//! reproducing that arithmetic bit-for-bit, the invariant
//! `transform(UnitX, compute_burn_body2cci(p, d)) == d` holds by construction — the same identity KSA
//! relies on when it points a burn — and the [`tests`] verify it. The flight computer measures attitude
//! error on `double3.UnitX.Transform(rotation)` (`KSA/FlightComputer.cs:972`), so satisfying that
//! invariant *is* the proof the vehicle's thrust axis ends up along `d`.

use super::types::Vec3;

/// A quaternion in KSA's `(x, y, z, w)` storage, interpreted as a Body→CCI rotation (the
/// `attitude_target` / `attitude/quat` convention).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Quat {
    pub x: f64,
    pub y: f64,
    pub z: f64,
    pub w: f64,
}

impl Quat {
    pub const IDENTITY: Quat = Quat {
        x: 0.0,
        y: 0.0,
        z: 0.0,
        w: 1.0,
    };

    pub fn new(x: f64, y: f64, z: f64, w: f64) -> Self {
        Self { x, y, z, w }
    }

    /// From the `/sim` `att_q` array order `[x, y, z, w]`.
    pub fn from_array(a: [f64; 4]) -> Self {
        Self {
            x: a[0],
            y: a[1],
            z: a[2],
            w: a[3],
        }
    }

    /// To the `ctl/attitude_target` write order `x y z w`.
    pub fn to_array(self) -> [f64; 4] {
        [self.x, self.y, self.z, self.w]
    }
}

/// `Brutal.Numerics/Quaternions.hamilton_product_WZYX` (Quaternions.cs:7), on `[x, y, z, w]` arrays.
fn hamilton(p: [f64; 4], q: [f64; 4]) -> [f64; 4] {
    let (px, py, pz, pw) = (p[0], p[1], p[2], p[3]);
    let (qx, qy, qz, qw) = (q[0], q[1], q[2], q[3]);
    [
        pw * qx + pz * qy - py * qz + px * qw, // X
        pw * qy - pz * qx + py * qw + px * qz, // Y
        pw * qz + pz * qw + py * qx - px * qy, // Z
        pw * qw - pz * qz - py * qy - px * qx, // W
    ]
}

/// KSA `double3.Transform(value, rotation)` (double3.cs:761): rotate `v` by the quaternion `q`, using
/// the conjugate-left convention `q*·[v,1]·q`. For a Body→CCI quaternion this maps a body-frame vector
/// into CCI (so `transform(UnitX, body2cci)` is the thrust axis expressed in CCI).
pub fn transform(v: Vec3, q: Quat) -> Vec3 {
    let qv = [v.x, v.y, v.z, 1.0];
    let p = [-q.x, -q.y, -q.z, q.w]; // conjugate (W kept)
    let q2 = [q.x, q.y, q.z, q.w];
    let p2 = hamilton(p, qv);
    let r = hamilton(p2, q2);
    Vec3::new(r[0], r[1], r[2])
}

/// KSA `doubleQuat.CreateFromRotationMatrix` (doubleQuat.cs:256, Shepperd's method). The matrix is
/// row-major (`double4x4` rows are `X,Y,Z`, so `M11=row0.x` …); pass its three rows.
pub fn from_rows(r0: Vec3, r1: Vec3, r2: Vec3) -> Quat {
    let (m11, m12, m13) = (r0.x, r0.y, r0.z);
    let (m21, m22, m23) = (r1.x, r1.y, r1.z);
    let (m31, m32, m33) = (r2.x, r2.y, r2.z);
    let trace = m11 + m22 + m33;
    if trace > 0.0 {
        let mut s = (trace + 1.0).sqrt();
        let w = s * 0.5;
        s = 0.5 / s;
        Quat::new((m23 - m32) * s, (m31 - m13) * s, (m12 - m21) * s, w)
    } else if m11 >= m22 && m11 >= m33 {
        let s = (1.0 + m11 - m22 - m33).sqrt();
        let inv = 0.5 / s;
        Quat::new(0.5 * s, (m12 + m21) * inv, (m13 + m31) * inv, (m23 - m32) * inv)
    } else if m22 > m33 {
        let s = (1.0 + m22 - m11 - m33).sqrt();
        let inv = 0.5 / s;
        Quat::new((m21 + m12) * inv, 0.5 * s, (m32 + m23) * inv, (m31 - m13) * inv)
    } else {
        let s = (1.0 + m33 - m11 - m22).sqrt();
        let inv = 0.5 / s;
        Quat::new((m31 + m13) * inv, (m32 + m23) * inv, 0.5 * s, (m12 - m21) * inv)
    }
}

/// KSA `BurnTarget.ComputeBurnBody2Cci(positionDirCci, thrustDirCci)` (BurnTarget.cs:60): a Body→CCI
/// quaternion that points the thrust axis (+X body) along `thrust_dir`, using `position_dir` to resolve
/// the (free, for a lander) roll. When thrust is parallel to position (vertical), any orthogonal roll
/// reference is used — the thrust direction is unaffected.
pub fn compute_burn_body2cci(position_dir: Vec3, thrust_dir: Vec3) -> Quat {
    let d = normalize_or(thrust_dir, Vec3::new(0.0, 0.0, 1.0));
    let cross = d.cross(&position_dir);
    let s = if cross.norm() < 1e-9 {
        any_orthogonal(d)
    } else {
        cross.normalize()
    };
    let u = d.cross(&s).normalize();
    from_rows(d, s, u)
}

fn any_orthogonal(v: Vec3) -> Vec3 {
    let a = if v.x.abs() < 0.9 {
        Vec3::new(1.0, 0.0, 0.0)
    } else {
        Vec3::new(0.0, 1.0, 0.0)
    };
    v.cross(&a).normalize()
}

fn normalize_or(v: Vec3, fallback: Vec3) -> Vec3 {
    let n = v.norm();
    if n < 1e-12 {
        fallback
    } else {
        v / n
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const X: Vec3 = Vec3::new(1.0, 0.0, 0.0);

    fn close(a: Vec3, b: Vec3) -> bool {
        (a - b).norm() < 1e-9
    }

    #[test]
    fn identity_transform_is_noop() {
        let v = Vec3::new(3.0, -4.0, 5.0);
        assert!(close(transform(v, Quat::IDENTITY), v));
    }

    #[test]
    fn from_rows_identity_is_identity_quat() {
        let q = from_rows(Vec3::x(), Vec3::y(), Vec3::z());
        assert!((q.x).abs() < 1e-12 && (q.y).abs() < 1e-12 && (q.z).abs() < 1e-12);
        assert!((q.w - 1.0).abs() < 1e-12);
    }

    /// THE invariant (plan §3.5): the burn quaternion aims body +X along the thrust direction, for any
    /// position/thrust pair — including the vertical (degenerate-roll) case.
    #[test]
    fn burn_quat_aims_thrust_axis_along_direction() {
        let cases = [
            (Vec3::new(1.0, 0.0, 0.0), Vec3::new(0.0, 0.0, 1.0)),
            (Vec3::new(0.0, 0.0, 1.0), Vec3::new(0.3, -0.7, 0.4)),
            (Vec3::new(0.2, 0.9, -0.1), Vec3::new(-0.6, 0.2, 0.77)),
            // vertical: thrust parallel to position (straight up) — the hard, common landing case.
            (Vec3::new(0.0, 0.0, 1.0), Vec3::new(0.0, 0.0, 1.0)),
            (Vec3::new(0.0, 0.0, 1.0), Vec3::new(0.0, 0.0, -1.0)),
        ];
        for (p, d) in cases {
            let dn = d.normalize();
            let q = compute_burn_body2cci(p, dn);
            let aim = transform(X, q);
            assert!(close(aim, dn), "aim {aim:?} != d {dn:?}");
        }
    }

    /// The burn quaternion's three body axes map to an orthonormal CCI triad (a valid rotation).
    #[test]
    fn burn_quat_is_orthonormal_rotation() {
        let q = compute_burn_body2cci(Vec3::new(0.0, 0.0, 1.0), Vec3::new(0.3, -0.7, 0.4).normalize());
        let bx = transform(Vec3::x(), q);
        let by = transform(Vec3::y(), q);
        let bz = transform(Vec3::z(), q);
        assert!((bx.norm() - 1.0).abs() < 1e-9);
        assert!(bx.dot(&by).abs() < 1e-9 && bx.dot(&bz).abs() < 1e-9 && by.dot(&bz).abs() < 1e-9);
        // right-handed: bx × by == bz
        assert!(close(bx.cross(&by), bz));
    }

    /// Round-trip an `att_q` array through [`Quat`].
    #[test]
    fn array_round_trip() {
        let a = [0.1, 0.2, 0.3, 0.927];
        assert_eq!(Quat::from_array(a).to_array(), a);
    }

    /// A 90° rotation about +Z maps +X→+Y under KSA's Transform (sanity vs a hand-built rotation).
    #[test]
    fn ninety_about_z() {
        // Rows of a +90° about-Z rotation matrix (M11..M33), row-major.
        let q = from_rows(
            Vec3::new(0.0, 1.0, 0.0),
            Vec3::new(-1.0, 0.0, 0.0),
            Vec3::new(0.0, 0.0, 1.0),
        );
        let r = transform(X, q);
        // We don't assert the sign convention here (that's what burn_quat_aims… pins down); we assert
        // it's a unit rotation into the XY plane orthogonal to X.
        assert!((r.norm() - 1.0).abs() < 1e-9);
        assert!(r.z.abs() < 1e-9);
        assert!(r.dot(&X).abs() < 1e-9);
    }
}
