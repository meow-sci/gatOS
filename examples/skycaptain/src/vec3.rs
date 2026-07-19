//! A minimal f64 3-vector. land-o-matic uses nalgebra (its SOCP solver needs it); here the math is
//! plain vector algebra, so a 60-line Vec3 keeps the in-guest build light. Method names mirror the
//! nalgebra subset the shared `ksa_quat` port calls (`cross`, `dot`, `norm`, `normalize`), so that
//! module is a verbatim copy from land-o-matic apart from the import.

use std::ops::{Add, AddAssign, Div, Mul, Neg, Sub};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Vec3 {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

impl Vec3 {
    pub const ZERO: Vec3 = Vec3 {
        x: 0.0,
        y: 0.0,
        z: 0.0,
    };

    pub const fn new(x: f64, y: f64, z: f64) -> Self {
        Self { x, y, z }
    }

    pub const fn x() -> Self {
        Self::new(1.0, 0.0, 0.0)
    }

    pub const fn y() -> Self {
        Self::new(0.0, 1.0, 0.0)
    }

    pub const fn z() -> Self {
        Self::new(0.0, 0.0, 1.0)
    }

    pub fn from_array(a: [f64; 3]) -> Self {
        Self::new(a[0], a[1], a[2])
    }

    pub fn dot(&self, o: &Vec3) -> f64 {
        self.x * o.x + self.y * o.y + self.z * o.z
    }

    pub fn cross(&self, o: &Vec3) -> Vec3 {
        Vec3::new(
            self.y * o.z - self.z * o.y,
            self.z * o.x - self.x * o.z,
            self.x * o.y - self.y * o.x,
        )
    }

    pub fn norm(&self) -> f64 {
        self.dot(self).sqrt()
    }

    pub fn normalize(&self) -> Vec3 {
        *self / self.norm()
    }

    /// `normalize`, or `fallback` when the vector is (near) zero.
    pub fn normalize_or(&self, fallback: Vec3) -> Vec3 {
        let n = self.norm();
        if n < 1e-12 {
            fallback
        } else {
            *self / n
        }
    }

    pub fn is_finite(&self) -> bool {
        self.x.is_finite() && self.y.is_finite() && self.z.is_finite()
    }
}

impl Add for Vec3 {
    type Output = Vec3;
    fn add(self, o: Vec3) -> Vec3 {
        Vec3::new(self.x + o.x, self.y + o.y, self.z + o.z)
    }
}

impl AddAssign for Vec3 {
    fn add_assign(&mut self, o: Vec3) {
        *self = *self + o;
    }
}

impl Sub for Vec3 {
    type Output = Vec3;
    fn sub(self, o: Vec3) -> Vec3 {
        Vec3::new(self.x - o.x, self.y - o.y, self.z - o.z)
    }
}

impl Neg for Vec3 {
    type Output = Vec3;
    fn neg(self) -> Vec3 {
        Vec3::new(-self.x, -self.y, -self.z)
    }
}

impl Mul<f64> for Vec3 {
    type Output = Vec3;
    fn mul(self, s: f64) -> Vec3 {
        Vec3::new(self.x * s, self.y * s, self.z * s)
    }
}

impl Mul<Vec3> for f64 {
    type Output = Vec3;
    fn mul(self, v: Vec3) -> Vec3 {
        v * self
    }
}

impl Div<f64> for Vec3 {
    type Output = Vec3;
    fn div(self, s: f64) -> Vec3 {
        Vec3::new(self.x / s, self.y / s, self.z / s)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cross_is_right_handed() {
        assert_eq!(Vec3::x().cross(&Vec3::y()), Vec3::z());
        assert_eq!(Vec3::y().cross(&Vec3::z()), Vec3::x());
    }

    #[test]
    fn normalize_or_handles_zero() {
        assert_eq!(Vec3::ZERO.normalize_or(Vec3::y()), Vec3::y());
        let v = Vec3::new(3.0, 0.0, 4.0);
        assert!((v.normalize().norm() - 1.0).abs() < 1e-12);
    }
}
