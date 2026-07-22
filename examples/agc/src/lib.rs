//! The gatOS AGC example: the real Apollo 11 flight software (Luminary099 on yaAGC) flying KSA
//! vessels through the `/sim` filesystem. See `../../plans/AGC_PLAN.md` for the full design.
//!
//! Layout (one crate, three `[[bin]]`s — the land-o-matic pattern):
//! - [`proto`] — the yaAGC socket wire protocol (4-byte packets, counters, u-bit masks).
//! - [`sim`] — the `/sim` data source (fs / HTTP backends, the telemetry doc, control writes).
//! - [`vec3`] / [`ksa_quat`] — KSA-exact vector/quaternion math (ported from land-o-matic).
//! - [`imu`] — the virtual stable member: `q_sm`, CDU emission, coarse/fine align, ZERO CDU.
//! - [`pipa`] — the accelerometer integrator (velocity differencing, pulse accumulators).
//! - [`radar`] — the landing-radar model (antenna positions, beams, scales, data-good).
//! - [`engines`] — DPS on/off edges + THRUST DINC clocking → `ctl/throttle`.
//! - [`rcs`] — ch 5/6 jet decode → axis duties → sigma-delta bang-bang `ctl/batch` writes.
//! - [`discretes`] — ch 030–033 state + the `/run/agc/switches` cockpit files.
//! - [`uplink`] — the digital uplink (V71/V72 P27 word streams over ch 0173) + key macros.
//! - [`padload`] — the Luminary099 erasable padload catalog + resume-core writer + KSA audit.
//! - [`downlink`] — ch 034/035 recorder → NDJSON.
//! - [`clockpolicy`] — pause/warp/stale gating + resync orchestration.
//! - [`dsky`] — the ratatui DSKY face (also used by the `dsky` bin's panels).

pub mod clockpolicy;
pub mod discretes;
pub mod downlink;
pub mod dsky;
pub mod engines;
pub mod imu;
pub mod ksa_quat;
pub mod padload;
pub mod pipa;
pub mod proto;
pub mod radar;
pub mod rcs;
pub mod sim;
pub mod uplink;
pub mod vec3;
