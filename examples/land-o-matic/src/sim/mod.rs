//! The data source for land-o-matic: read the **active vessel's** `/sim` telemetry and (later) write
//! its control fields by path. Everything lives under the `vessels/active/…` alias, so it never needs
//! to know the vessel id.
//!
//! Two backends, mirroring the sibling examples:
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): a read is one
//!   `read()`, a control write is one `echo value > file`.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url`/`$GATOS_HTTP` dev mode).
//!
//! One background worker thread (see `main.rs`) owns the source and polls once per interval, so the
//! render/input loop never blocks on I/O.

use std::fs;
use std::path::PathBuf;
use std::time::Duration;

use serde::Deserialize;

/// The outcome of a failed control write — an errno-ish tag + message (the frozen control-file errno
/// vocabulary: `EINVAL`, `EACCES`, `EBUSY`, …), surfaced on the status line.
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// A read/write interface over the active vessel's `/sim` fields. Implementations run on the worker
/// thread only.
pub trait Source: Send {
    /// Reads a field's current value (trailing newline trimmed). `Err` carries a short tag
    /// (e.g. `"ENOENT"`).
    fn read(&self, path: &str) -> Result<String, String>;

    /// Writes `value` as one newline-terminated write (the `echo value > file` shape), so a control
    /// file actuates and a failure carries the real errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// Lists the child names of a directory (for enumerating `engines/*`). Empty if unsupported
    /// (the HTTP backend) or the directory is absent.
    fn list(&self, path: &str) -> Vec<String>;

    /// A short label for the status line (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;
}

// ---- telemetry model ----------------------------------------------------------------------------

/// The whole-vessel `telemetry` document (`gatOS.SimFs/Formats.cs` `VesselTelemetry`). One `read()`
/// yields one self-consistent snapshot, so the guidance loop never stitches scalar files across ticks.
///
/// **Frames/units (see `LANDING_PROGRAM_PLAN.md` §3):** `pos_cci`/`vel_cci` are CCI (parent-centred
/// inertial), meters / m/s; `att_q` is the Body→CCI quaternion `x y z w`; `alt.radar` is terrain-relative
/// meters; masses are kg; `vel.surf` is the surface-relative speed *scalar* (the vector is derived).
#[derive(Debug, Clone, Deserialize)]
pub struct Telemetry {
    pub seq: u64,
    pub ut: f64,
    pub warp: f64,
    pub id: String,
    pub sit: String,
    pub controlled: bool,
    #[serde(default)]
    pub parent: Option<String>,
    pub pos_cci: [f64; 3],
    pub vel_cci: [f64; 3],
    pub vel: Speeds,
    pub alt: Altitudes,
    pub mass: Masses,
    pub att_q: [f64; 4],
    #[serde(default)]
    pub orbit: Option<OrbitDoc>,
    #[serde(default)]
    pub power: Option<PowerDoc>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Speeds {
    /// Orbital (== inertial) speed magnitude, m/s.
    pub orb: f64,
    /// Surface-relative speed magnitude, m/s.
    pub surf: f64,
    /// Inertial speed magnitude, m/s.
    pub inr: f64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Altitudes {
    /// Above the mean-radius datum, m.
    pub baro: f64,
    /// Above local terrain/ocean, m — use this for ground clearance.
    pub radar: f64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Masses {
    /// Total mass, kg.
    pub t: f64,
    /// Dry mass, kg.
    pub d: f64,
    /// Propellant mass, kg.
    pub p: f64,
}

/// A subset of the telemetry `orbit` block (the fields the monitor shows). Other keys (`sma`, `ta`,
/// `t_ap`, `t_pe`) are present in the document and ignored here.
#[derive(Debug, Clone, Deserialize)]
pub struct OrbitDoc {
    pub ap: f64,
    pub pe: f64,
    pub ecc: f64,
    pub inc: f64,
    pub period: f64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct PowerDoc {
    pub prod: f64,
    pub cons: f64,
    #[serde(default)]
    pub battery: Option<f64>,
}

/// Parent-body constants read once from `bodies/<parent>/…` (these can be telemetry-gated, so we read
/// them ourselves and compute gravity rather than depending on detail streams — plan §4.4).
#[derive(Debug, Clone, Copy)]
pub struct Body {
    /// Standard gravitational parameter μ = GM, m³/s².
    pub mu: f64,
    /// Mean radius, m.
    pub radius: f64,
    /// Sidereal rotation rate ω, rad/s (about the CCI +Z spin axis).
    pub rotation_rate: f64,
}

/// One poll of the active vessel.
#[derive(Debug, Clone, Default)]
pub struct Tick {
    /// The source is reachable (the `/sim` mount exists / the HTTP server answers).
    pub connected: bool,
    /// The whole-vessel telemetry, if a controlled vessel exists and parsed.
    pub telemetry: Option<Telemetry>,
    /// Parent-body constants, if resolvable.
    pub body: Option<Body>,
}

/// Reads the active vessel's telemetry (and its parent-body constants). Distinguishes "not connected"
/// from "connected but no active vessel" via a `time/ut` probe.
pub fn poll(src: &dyn Source) -> Tick {
    match read_telemetry(src) {
        Ok(t) => {
            let body = t.parent.as_deref().and_then(|p| read_body(src, p));
            Tick {
                connected: true,
                telemetry: Some(t),
                body,
            }
        }
        Err(_) => Tick {
            connected: src.read("time/ut").is_ok(),
            telemetry: None,
            body: None,
        },
    }
}

/// Reads + parses `vessels/active/telemetry` into a [`Telemetry`].
pub fn read_telemetry(src: &dyn Source) -> Result<Telemetry, String> {
    let raw = src.read("vessels/active/telemetry")?;
    serde_json::from_str(&raw).map_err(|e| format!("parse: {e}"))
}

/// Reads the parent body's `{mu,radius,rotation_rate}` scalars. `rotation_rate` defaults to 0 (a
/// non-rotating body) if absent.
pub fn read_body(src: &dyn Source, parent: &str) -> Option<Body> {
    let mu = read_scalar(src, &format!("bodies/{parent}/mu"))?;
    let radius = read_scalar(src, &format!("bodies/{parent}/radius"))?;
    let rotation_rate = read_scalar(src, &format!("bodies/{parent}/rotation_rate")).unwrap_or(0.0);
    Some(Body {
        mu,
        radius,
        rotation_rate,
    })
}

fn read_scalar(src: &dyn Source, path: &str) -> Option<f64> {
    src.read(path).ok().and_then(|s| parse_scalar(&s))
}

/// Aggregated propulsion spec from the active vessel's `engines/*` (plan §4.1): summed thrust,
/// thrust-weighted Isp, and the most-restrictive throttle floor. `None` if no engines are readable.
#[derive(Debug, Clone, Copy)]
pub struct EngineAgg {
    pub thrust_max: f64,
    pub isp: f64,
    pub throttle_min: f64,
}

pub fn read_engines(src: &dyn Source) -> Option<EngineAgg> {
    let mut total_thrust = 0.0;
    let mut total_mdot = 0.0; // Σ thrust_i/Isp_i  (the g₀ in mdot cancels in the combined Isp)
    let mut throttle_min = 0.0f64;
    let mut count = 0;
    for name in src.list("vessels/active/engines") {
        let base = format!("vessels/active/engines/{name}");
        if let (Some(t), Some(isp)) = (
            read_scalar(src, &format!("{base}/vac_thrust")),
            read_scalar(src, &format!("{base}/isp")),
        ) {
            if t > 0.0 && isp > 0.0 {
                total_thrust += t;
                total_mdot += t / isp;
                throttle_min =
                    throttle_min.max(read_scalar(src, &format!("{base}/min_throttle")).unwrap_or(0.0));
                count += 1;
            }
        }
    }
    if count == 0 || total_mdot <= 0.0 {
        return None;
    }
    Some(EngineAgg {
        thrust_max: total_thrust,
        isp: total_thrust / total_mdot,
        throttle_min,
    })
}

/// The active vessel's body-fixed longitude (deg), read separately (it isn't in the telemetry doc); used
/// to recover the body's rotation angle so a ground-fixed target stays put.
pub fn read_longitude(src: &dyn Source) -> Option<f64> {
    read_scalar(src, "vessels/active/position/lon")
}

/// Parses the leading float of a `/sim` scalar (`G9` doubles). `"1"`, `"0.42"`, `"-0"` all parse.
pub fn parse_scalar(value: &str) -> Option<f64> {
    value.split_whitespace().next()?.parse::<f64>().ok()
}

// ---- filesystem source (the real /sim mount) ----------------------------------------------------

/// Reads the `/sim` mount directly via `std::fs`. `root` defaults to `/sim` in the guest, but can point
/// at any directory (e.g. a hand-made fixture for host-side dev).
pub struct FsSource {
    root: PathBuf,
}

impl FsSource {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }
}

impl Source for FsSource {
    fn read(&self, path: &str) -> Result<String, String> {
        fs::read_to_string(self.root.join(path))
            .map(|s| s.trim_end_matches(['\n', '\r']).to_string())
            .map_err(|e| errno_name(e.raw_os_error()))
    }

    fn write(&self, path: &str, value: &str) -> Result<(), CmdError> {
        // One newline-terminated write — the control file actuates on the newline and a failed write(2)
        // surfaces the real errno via the io::Error.
        let payload = format!("{}\n", value.trim_end_matches(['\n', '\r']));
        fs::write(self.root.join(path), payload).map_err(|e| CmdError {
            errno: errno_name(e.raw_os_error()),
            message: e.to_string(),
        })
    }

    fn list(&self, path: &str) -> Vec<String> {
        fs::read_dir(self.root.join(path))
            .map(|rd| {
                rd.filter_map(|e| e.ok().map(|e| e.file_name().to_string_lossy().into_owned()))
                    .collect()
            })
            .unwrap_or_default()
    }

    fn label(&self) -> String {
        format!("fs:{}", self.root.display())
    }
}

/// Maps a raw OS errno (Linux) to its name for compact display.
fn errno_name(raw: Option<i32>) -> String {
    match raw {
        Some(1) => "EPERM".into(),
        Some(2) => "ENOENT".into(),
        Some(13) => "EACCES".into(),
        Some(16) => "EBUSY".into(),
        Some(21) => "EISDIR".into(),
        Some(22) => "EINVAL".into(),
        Some(30) => "EROFS".into(),
        Some(95) => "EOPNOTSUPP".into(),
        Some(110) => "ETIMEDOUT".into(),
        Some(n) => format!("E{n}"),
        None => "EIO".into(),
    }
}

// ---- HTTP source (the /v1/fs mirror) ------------------------------------------------------------

/// Uses the mod's HTTP `/v1/fs/<path>` field mirror. `base` is the `/v1` root, e.g.
/// `http://sim:4242/v1` (`$GATOS_HTTP`).
pub struct HttpSource {
    base: String,
    agent: ureq::Agent,
}

impl HttpSource {
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
}

impl Source for HttpSource {
    fn read(&self, path: &str) -> Result<String, String> {
        match self.agent.get(&format!("{}/fs/{path}", self.base)).call() {
            Ok(resp) => resp
                .into_string()
                .map(|s| s.trim_end_matches(['\n', '\r']).to_string())
                .map_err(|_| "EIO".to_string()),
            Err(ureq::Error::Status(404, _)) => Err("ENOENT".to_string()),
            Err(ureq::Error::Status(code, _)) => Err(format!("HTTP{code}")),
            Err(_) => Err("ECONN".to_string()),
        }
    }

    fn write(&self, path: &str, value: &str) -> Result<(), CmdError> {
        match self
            .agent
            .post(&format!("{}/fs/{path}", self.base))
            .send_string(value)
        {
            Ok(_) => Ok(()),
            Err(ureq::Error::Status(code, resp)) => Err(CmdError {
                errno: format!("HTTP{code}"),
                message: resp.into_string().unwrap_or_else(|_| "write failed".into()),
            }),
            Err(e) => Err(CmdError {
                errno: "ECONN".into(),
                message: e.to_string(),
            }),
        }
    }

    fn list(&self, _path: &str) -> Vec<String> {
        Vec::new() // the HTTP /v1/fs mirror does not enumerate directories
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"{
        "seq": 42, "ut": 12345.6, "warp": 1, "id": "Kerbal-1", "sit": "Freefall",
        "controlled": true, "parent": "Kerbin",
        "pos_cci": [600000.0, 0.0, 1000.0],
        "pos_ecl": [1.0, 2.0, 3.0],
        "vel_cci": [10.0, 0.0, -82.0],
        "vel": {"orb": 2210.5, "surf": 88.0, "inr": 2210.5},
        "alt": {"baro": 1255.0, "radar": 1240.0},
        "mass": {"t": 5980.0, "d": 4800.0, "p": 1180.0},
        "att_q": [0.1, 0.2, 0.3, 0.927],
        "orbit": {"ap": 80000, "pe": -50000, "ecc": 0.9, "inc": 12.0, "sma": 700000,
                  "period": 1234.0, "ta": 170.0, "t_ap": 0.0, "t_pe": 60.0},
        "power": {"prod": 5.0, "cons": 2.0, "battery": 0.8}
    }"#;

    #[test]
    fn telemetry_parses_full_document() {
        let t: Telemetry = serde_json::from_str(SAMPLE).unwrap();
        assert_eq!(t.id, "Kerbal-1");
        assert_eq!(t.parent.as_deref(), Some("Kerbin"));
        assert_eq!(t.pos_cci, [600000.0, 0.0, 1000.0]);
        assert_eq!(t.att_q, [0.1, 0.2, 0.3, 0.927]);
        assert_eq!(t.alt.radar, 1240.0);
        assert_eq!(t.mass.t, 5980.0);
        assert_eq!(t.power.unwrap().battery, Some(0.8));
    }

    #[test]
    fn telemetry_parses_without_optional_fields() {
        let minimal = r#"{
            "seq": 1, "ut": 0, "warp": 1, "id": "x", "sit": "Landed", "controlled": false,
            "pos_cci": [0,0,0], "pos_ecl": [0,0,0], "vel_cci": [0,0,0],
            "vel": {"orb": 0, "surf": 0, "inr": 0}, "alt": {"baro": 0, "radar": 0},
            "mass": {"t": 1, "d": 1, "p": 0}, "att_q": [0,0,0,1],
            "power": {"prod": 0, "cons": 0}
        }"#;
        let t: Telemetry = serde_json::from_str(minimal).unwrap();
        assert!(t.parent.is_none());
        assert!(t.orbit.is_none());
        assert!(t.power.unwrap().battery.is_none());
    }

    #[test]
    fn parse_scalar_takes_leading_float() {
        assert_eq!(parse_scalar("0.42"), Some(0.42));
        assert_eq!(parse_scalar("1 2 3"), Some(1.0));
        assert_eq!(parse_scalar("nope"), None);
    }

    #[test]
    fn fs_source_reads_body_and_telemetry() {
        let root = std::env::temp_dir().join(format!("lom_src_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("vessels/active")).unwrap();
        fs::create_dir_all(root.join("bodies/Kerbin")).unwrap();
        fs::write(root.join("vessels/active/telemetry"), SAMPLE).unwrap();
        fs::write(root.join("bodies/Kerbin/mu"), "3.5316e12\n").unwrap();
        fs::write(root.join("bodies/Kerbin/radius"), "600000\n").unwrap();
        fs::write(root.join("bodies/Kerbin/rotation_rate"), "2.9089e-4\n").unwrap();

        let s = FsSource::new(&root);
        let tick = poll(&s);
        assert!(tick.connected);
        let t = tick.telemetry.expect("telemetry present");
        assert_eq!(t.id, "Kerbal-1");
        let b = tick.body.expect("body present");
        assert_eq!(b.radius, 600000.0);
        assert!((b.mu - 3.5316e12).abs() < 1e6);

        fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn poll_reports_no_active_vessel() {
        let root = std::env::temp_dir().join(format!("lom_empty_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("time")).unwrap();
        fs::write(root.join("time/ut"), "123\n").unwrap();

        let tick = poll(&FsSource::new(&root));
        assert!(tick.connected); // /sim is mounted…
        assert!(tick.telemetry.is_none()); // …but there is no active vessel
        fs::remove_dir_all(&root).ok();
    }
}
