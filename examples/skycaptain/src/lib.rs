//! skycaptain — a skywriting autopilot for gatOS `/sim`.
//!
//! Library layout (the pure core is host-testable with no game attached):
//! - [`vec3`], [`ksa_quat`], [`frames`] — math: vectors, KSA's exact quaternion arithmetic, and the
//!   CCI↔CCF/canvas frame work.
//! - [`font`] — the Skybrush Caps letterforms (physics-shaped: descending entries, ascending
//!   launch-ramp exits, load-bearing italic).
//! - [`plan`] — text → timed strokes + solved ballistic hops.
//! - [`sim`] — the `/sim` transport (fs mount, HTTP mirror).
//! - [`simulate`] — a built-in physics backend for `--simulate` and the headless integration test.
//! - [`flight`] — the phase machine + tracking controller that flies a plan.
//! - [`app`], [`ui`] — the ratatui TUI.

pub mod app;
pub mod flight;
pub mod font;
pub mod frames;
pub mod ksa_quat;
pub mod plan;
pub mod sim;
pub mod simulate;
pub mod ui;
pub mod vec3;
