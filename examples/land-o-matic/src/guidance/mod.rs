//! The guidance core: pure, host-testable flight-guidance math with **no `/sim`, game, or terminal
//! dependency** — only nalgebra (and, from M2, clarabel). This mirrors gatOS's "game-free library"
//! discipline (`LANDING_PROGRAM_PLAN.md` §8.2): everything here speaks `Vec3`/`Quat`, never a
//! `sim::Telemetry`. The app glues telemetry → these primitives → control writes.
//!
//! - [`frames`] — CCI ↔ ENU ↔ CCF transforms, surface-relative velocity.
//! - [`ksa_quat`] — the KSA quaternion port + the attitude-output recipe.
//! - [`vehicle`] — the propulsion model (thrust bounds, α, ρ₁/ρ₂).
//! - [`gfold`] — the G-FOLD fuel-optimal powered-descent SOCP (via clarabel).
//! - [`cse`] — the conic state-extrapolation propagator (UPFG's gravity model).
//! - [`upfg`] — the UPFG closed-loop terminal steering law (CCI).
//! - [`types`] — shared math types.

pub mod autopilot;
pub mod cse;
pub mod frames;
pub mod gfold;
pub mod ksa_quat;
pub mod types;
pub mod upfg;
pub mod vehicle;

pub use types::Vec3;
