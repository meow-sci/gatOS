//! land-o-matic: powered-descent landing guidance over the gatOS `/sim` filesystem.
//!
//! Structured as a library (this crate) plus a thin binary (`main.rs`) so the pure guidance core is a
//! first-class, independently testable unit — mirroring gatOS's "game-free library" discipline
//! (`LANDING_PROGRAM_PLAN.md` §8.2). The [`guidance`] module never touches [`sim`] or the terminal; it
//! speaks only nalgebra/clarabel.

pub mod app;
pub mod guidance;
pub mod sim;
pub mod ui;
