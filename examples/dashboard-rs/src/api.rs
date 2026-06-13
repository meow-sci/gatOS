//! The HTTP client + the JSON models for the gatOS `/v1` API.
//!
//! Reads come from one atomic `GET /v1/snapshot` (the whole fleet, each vessel's full record);
//! writes go through the single `POST /v1/command`. Every model field is `#[serde(default)]` and
//! unknown fields are ignored, so a partial payload — or a field whose snake_case name we guessed
//! slightly wrong — degrades to a default (shown as 0 / empty) instead of failing the whole parse.

use std::time::Duration;

use anyhow::{anyhow, Result};
use serde::{Deserialize, Serialize};

/// A blocking client over the `/v1` base URL (e.g. `http://sim:4242/v1`, i.e. `$GATOS_HTTP`).
pub struct Client {
    base: String,
    agent: ureq::Agent,
}

impl Client {
    pub fn new(base: impl Into<String>) -> Self {
        let agent = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(2))
            .timeout_read(Duration::from_secs(4))
            .build();
        Self {
            base: base.into().trim_end_matches('/').to_string(),
            agent,
        }
    }

    pub fn snapshot(&self) -> Result<Snapshot> {
        self.get("snapshot")
    }

    pub fn status(&self) -> Result<Status> {
        self.get("status")
    }

    fn get<T: for<'de> Deserialize<'de>>(&self, path: &str) -> Result<T> {
        self.agent
            .get(&format!("{}/{path}", self.base))
            .call()
            .map_err(|e| anyhow!("{e}"))?
            .into_json::<T>()
            .map_err(|e| anyhow!("parse {path}: {e}"))
    }

    /// Submits one control command. Maps a non-2xx `{errno,message}` body to a [`CommandError`].
    pub fn command(&self, cmd: &Command) -> std::result::Result<(), CommandError> {
        match self
            .agent
            .post(&format!("{}/command", self.base))
            .send_json(cmd)
        {
            Ok(_) => Ok(()),
            Err(ureq::Error::Status(_, resp)) => {
                let body: ErrorBody = resp.into_json().unwrap_or_default();
                Err(CommandError {
                    errno: if body.errno.is_empty() {
                        "EIO".into()
                    } else {
                        body.errno
                    },
                    message: if body.message.is_empty() {
                        "command failed".into()
                    } else {
                        body.message
                    },
                })
            }
            Err(e) => Err(CommandError {
                errno: "ECONN".into(),
                message: e.to_string(),
            }),
        }
    }
}

// ---- write: the transport-agnostic command shape (mirrors C# SimCommand) -----------------------

#[derive(Serialize)]
pub struct Command {
    pub vessel_id: String,
    pub action: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ordinal: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub value: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
}

impl Command {
    fn new(vessel: &str, action: &str) -> Self {
        Self {
            vessel_id: vessel.into(),
            action: action.into(),
            ordinal: None,
            value: None,
            token: None,
        }
    }

    /// A vessel-level numeric/flag/trigger action (`value` carries the flag/fraction/trigger token).
    pub fn vessel(vessel: &str, action: &str, value: f64) -> Self {
        Self {
            value: Some(value),
            ..Self::new(vessel, action)
        }
    }

    /// A per-module action addressed by ordinal (engine/light index).
    pub fn module(vessel: &str, action: &str, ordinal: i32, value: f64) -> Self {
        Self {
            ordinal: Some(ordinal),
            value: Some(value),
            ..Self::new(vessel, action)
        }
    }

    /// A symbolic-token action (attitude mode).
    pub fn token(vessel: &str, action: &str, token: &str) -> Self {
        Self {
            token: Some(token.into()),
            ..Self::new(vessel, action)
        }
    }
}

/// The errno + message a failed command reports (the frozen control-file errno vocabulary).
#[derive(Debug, Clone)]
pub struct CommandError {
    pub errno: String,
    pub message: String,
}

#[derive(Deserialize, Default)]
struct ErrorBody {
    #[serde(default)]
    errno: String,
    #[serde(default)]
    message: String,
}

// ---- read models (subset of the /v1/snapshot JSON we render) -----------------------------------

#[derive(Deserialize, Default, Clone)]
pub struct Snapshot {
    #[serde(default)]
    pub ut_seconds: f64,
    #[serde(default)]
    pub warp_factor: f64,
    #[serde(default)]
    pub game_version: String,
    #[serde(default)]
    pub vessels: Vec<Vessel>,
}

#[derive(Deserialize, Default, Clone)]
pub struct Vessel {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub situation: String,
    #[serde(default)]
    pub controlled: bool,
    #[serde(default)]
    pub parent_body_name: Option<String>,
    #[serde(default)]
    pub orbital_speed: f64,
    #[serde(default)]
    pub surface_speed: f64,
    #[serde(default)]
    pub barometric_altitude: f64,
    #[serde(default)]
    pub radar_altitude: f64,
    #[serde(default)]
    pub mass_total: f64,
    #[serde(default)]
    pub mass_dry: f64,
    #[serde(default)]
    pub mass_propellant: f64,
    #[serde(default)]
    pub throttle_cmd: f64,
    #[serde(default)]
    pub lights_master_on: bool,
    #[serde(default)]
    pub rcs_on: bool,
    #[serde(default)]
    pub attitude_mode: String,
    #[serde(default)]
    pub battery_charge_fraction: Option<f64>,
    #[serde(default)]
    pub power_produced_w: f64,
    #[serde(default)]
    pub power_consumed_w: f64,
    #[serde(default)]
    pub orbit: Option<Orbit>,
    #[serde(default)]
    pub engines: Vec<Engine>,
    #[serde(default)]
    pub tanks: Vec<Tank>,
    #[serde(default)]
    pub lights: Vec<Light>,
}

impl Vessel {
    /// Total propellant fill fraction 0..1 across tanks (mass-weighted by capacity), or 0 when dry.
    pub fn fuel_fraction(&self) -> f64 {
        let cap: f64 = self.tanks.iter().map(|t| t.capacity).sum();
        if cap <= 0.0 {
            0.0
        } else {
            self.tanks.iter().map(|t| t.amount).sum::<f64>() / cap
        }
    }
}

#[derive(Deserialize, Default, Clone)]
pub struct Orbit {
    #[serde(default)]
    pub apoapsis_altitude: f64,
    #[serde(default)]
    pub periapsis_altitude: f64,
    #[serde(default)]
    pub eccentricity: f64,
    #[serde(default)]
    pub inclination_deg: f64,
    #[serde(default)]
    pub sma_meters: f64,
    #[serde(default)]
    pub period_seconds: f64,
    #[serde(default)]
    pub true_anomaly_deg: f64,
    #[serde(default)]
    pub time_to_apoapsis: f64,
    #[serde(default)]
    pub time_to_periapsis: f64,
}

#[derive(Deserialize, Default, Clone)]
pub struct Engine {
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub vac_thrust_n: f64,
    #[serde(default)]
    pub isp_s: f64,
    #[serde(default)]
    pub propellant_available: bool,
}

#[derive(Deserialize, Default, Clone)]
pub struct Tank {
    #[serde(default)]
    pub resource: String,
    #[serde(default)]
    pub amount: f64,
    #[serde(default)]
    pub capacity: f64,
    #[serde(default)]
    pub fraction: f64,
}

#[derive(Deserialize, Default, Clone)]
pub struct Light {
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub on: bool,
}

#[derive(Deserialize, Default, Clone)]
pub struct Status {
    #[serde(default)]
    pub control: bool,
    #[serde(default)]
    pub debug: bool,
}
