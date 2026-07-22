//! A faithful Rust port of the KSA (`Brutal.Numerics`) quaternion arithmetic (verbatim from
//! `examples/land-o-matic/src/guidance/ksa_quat.rs`, plus the composition/inverse/axis-angle
//! helpers the virtual IMU needs).
//!
//! Why port the exact arithmetic instead of a generic quaternion crate? KSA's `Transform` uses
//! the conjugate-left convention `q*·[v,1]·q` (`Brutal.Numerics/double3.cs:761`) with a specific
//! Hamilton product (`Quaternions.cs:7`). Reproducing it bit-for-bit makes
//! `transform(v, mul(a, b)) == transform(transform(v, a), b)` hold by construction — the
//! composition rule the IMU's frame chains (body → CCI → stable member) are built on, pinned by
//! the [`tests`].

use crate::vec3::Vec3;

/// A quaternion in KSA's `(x, y, z, w)` storage — the `/sim` `att_q` convention (Body→CCI).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Quat {
    pub x: f64,
    pub y: f64,
    pub z: f64,
    pub w: f64,
}

impl Quat {
    pub const IDENTITY: Quat = Quat { x: 0.0, y: 0.0, z: 0.0, w: 1.0 };

    pub fn new(x: f64, y: f64, z: f64, w: f64) -> Self {
        Self { x, y, z, w }
    }

    /// From the `/sim` `att_q` array order `[x, y, z, w]`.
    pub fn from_array(a: [f64; 4]) -> Self {
        Self { x: a[0], y: a[1], z: a[2], w: a[3] }
    }

    pub fn to_array(self) -> [f64; 4] {
        [self.x, self.y, self.z, self.w]
    }

    /// The conjugate — for a unit quaternion, the inverse rotation.
    pub fn conj(self) -> Quat {
        Quat::new(-self.x, -self.y, -self.z, self.w)
    }

    pub fn normalize(self) -> Quat {
        let n = (self.x * self.x + self.y * self.y + self.z * self.z + self.w * self.w).sqrt();
        if n < 1e-300 {
            Quat::IDENTITY
        } else {
            Quat::new(self.x / n, self.y / n, self.z / n, self.w / n)
        }
    }
}

/// `Brutal.Numerics/Quaternions.hamilton_product_WZYX` (Quaternions.cs:7), on `[x, y, z, w]`.
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

/// Composition under KSA's transform: `transform(v, mul(a, b)) == transform(transform(v, a), b)`
/// (rotate by `a` first, then `b`). Derivation: `transform(v, q) = q*·v·q`, so chaining gives
/// `b*·(a*·v·a)·b = (a·b)*·v·(a·b)`.
pub fn mul(a: Quat, b: Quat) -> Quat {
    let r = hamilton(a.to_array(), b.to_array());
    Quat::new(r[0], r[1], r[2], r[3])
}

/// KSA `double3.Transform(value, rotation)` (double3.cs:761): rotate `v` by the quaternion `q`,
/// using the conjugate-left convention `q*·[v,1]·q`. For a Body→CCI quaternion this maps a
/// body-frame vector into CCI.
pub fn transform(v: Vec3, q: Quat) -> Vec3 {
    let qv = [v.x, v.y, v.z, 1.0];
    let p = [-q.x, -q.y, -q.z, q.w]; // conjugate (W kept)
    let q2 = [q.x, q.y, q.z, q.w];
    let p2 = hamilton(p, qv);
    let r = hamilton(p2, q2);
    Vec3::new(r[0], r[1], r[2])
}

/// KSA `doubleQuat.CreateFromRotationMatrix` (doubleQuat.cs:256, Shepperd's method). The matrix
/// is row-major; pass its three rows. Under KSA's transform, row k of the matrix is
/// `transform(e_k, q)` — the image of source-frame axis k in the destination frame.
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

/// A rotation of `angle` radians about the unit axis `n`, in the SOURCE frame of whatever chain
/// it is [`mul`]-composed into, with the sign convention that
/// `transform(v, axis_angle(n, θ))` rotates `v` by **+θ** about `n` (right-hand rule) — pinned
/// by [`tests::axis_angle_matches_matrix`] against [`from_rows`] of the textbook matrix.
pub fn axis_angle(n: Vec3, angle: f64) -> Quat {
    let h = angle * 0.5;
    let s = h.sin();
    // Under KSA's q*·v·q transform, the standard [n·sin(θ/2), cos(θ/2)] storage rotates by +θ
    // (right-hand rule) — pinned by tests::axis_angle_matches_matrix against from_rows.
    Quat::new(n.x * s, n.y * s, n.z * s, h.cos()).normalize()
}

#[cfg(test)]
mod tests {
    use super::*;

    const EPS: f64 = 1e-9;

    fn close(a: Vec3, b: Vec3) -> bool {
        (a - b).norm() < EPS
    }

    #[test]
    fn identity_transform_is_noop() {
        let v = Vec3::new(3.0, -4.0, 5.0);
        assert!(close(transform(v, Quat::IDENTITY), v));
    }

    #[test]
    fn from_rows_identity_is_identity_quat() {
        let q = from_rows(Vec3::x(), Vec3::y(), Vec3::z());
        assert!(q.x.abs() < 1e-12 && q.y.abs() < 1e-12 && q.z.abs() < 1e-12);
        assert!((q.w - 1.0).abs() < 1e-12);
    }

    /// The composition law the IMU frame chains rely on.
    #[test]
    fn mul_composes_transforms() {
        let a = axis_angle(Vec3::new(0.3, -0.5, 0.81).normalize(), 0.7);
        let b = axis_angle(Vec3::new(-0.1, 0.9, 0.4).normalize(), -1.3);
        let v = Vec3::new(1.0, 2.0, -0.5);
        assert!(close(transform(v, mul(a, b)), transform(transform(v, a), b)));
    }

    #[test]
    fn conj_inverts() {
        let q = axis_angle(Vec3::new(0.2, 0.3, 0.93).normalize(), 1.1);
        let v = Vec3::new(-2.0, 0.4, 1.5);
        assert!(close(transform(transform(v, q), q.conj()), v));
    }

    /// Pin the axis_angle sign convention: +90° about +Z maps +X → +Y (right-hand rule), and
    /// agrees with from_rows of the textbook active-rotation matrix R_z(90°) whose rows are the
    /// images of the source axes: e_x → (0,1,0), e_y → (−1,0,0), e_z → (0,0,1).
    #[test]
    fn axis_angle_matches_matrix() {
        let th = std::f64::consts::FRAC_PI_2;
        let q = axis_angle(Vec3::z(), th);
        assert!(close(transform(Vec3::x(), q), Vec3::y()));
        let m = from_rows(Vec3::new(0.0, 1.0, 0.0), Vec3::new(-1.0, 0.0, 0.0), Vec3::z());
        let v = Vec3::new(0.3, -0.7, 0.2);
        assert!(close(transform(v, q), transform(v, m)));
    }

    #[test]
    fn array_round_trip() {
        let a = [0.1, 0.2, 0.3, 0.927];
        assert_eq!(Quat::from_array(a).to_array(), a);
    }
}
