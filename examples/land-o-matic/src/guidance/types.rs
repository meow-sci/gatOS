//! Shared math types for the guidance core. f64 throughout — this is aerospace physics, not graphics.

/// A 3-vector in whatever frame the caller documents (CCI or the target-centred ENU frame). All
/// guidance math is double-precision.
pub type Vec3 = nalgebra::Vector3<f64>;
