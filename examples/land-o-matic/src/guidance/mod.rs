//! The guidance core: pure, host-testable flight-guidance math with **no `/sim`, game, or terminal
//! dependency** — only nalgebra (and, from M2, clarabel). This mirrors gatOS's "game-free library"
//! discipline (`LANDING_PROGRAM_PLAN.md` §8.2): everything here speaks `Vec3`/`Quat`, never a
//! `sim::Telemetry`. The app glues telemetry → these primitives → control writes.
//!
//! - [`frames`] — CCI ↔ ENU ↔ CCF transforms, surface-relative velocity.
//! - [`ksa_quat`] — the KSA quaternion port + the attitude-output recipe.
//! - [`types`] — shared math types.
//!
//! (G-FOLD `gfold` and the vehicle model `vehicle` arrive in M2; UPFG in M4.)

pub mod frames;
pub mod ksa_quat;
pub mod types;

pub use types::Vec3;
