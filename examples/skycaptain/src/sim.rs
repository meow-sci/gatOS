//! The data source for skycaptain: read the **active vessel's** `/sim` telemetry and write its
//! control fields by path. Everything lives under the `vessels/active/…` alias, so it never needs to
//! know the vessel id. Ported from `examples/land-o-matic/src/sim/mod.rs`, plus what a skywriter
//! needs on top: the parent body's **atmosphere** block (the plume trail only exists inside an
//! atmosphere), and a **sim-time alarm** (`time/alarm` — the warp-correct "sleep until", used to cut
//! the engine at an exact stroke top).
//!
//! Two backends, mirroring the sibling examples:
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): a read is one
//!   `read()`, a control write is one `echo value > file`.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url`/`$GATOS_HTTP` dev mode).
//!
//! One background worker thread (see `main.rs`) owns the source, so the render/input loop never
//! blocks on I/O — including the (deliberately) blocking alarm reads.

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

    /// Blocks until sim time reaches `ut`, returning the reached ut. Warp-correct and pause-safe:
    /// the sim clock, not the wall clock, decides when this returns. Callers cap the wait span and
    /// loop, so an abort is never more than one chunk away.
    fn wait_until(&self, ut: f64) -> Result<f64, String>;

    /// A short label for the status line (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;
}

// ---- telemetry model ----------------------------------------------------------------------------

/// The whole-vessel `telemetry` document (`gatOS.SimFs/Formats.cs` `VesselTelemetry`). One `read()`
/// yields one self-consistent snapshot, so the control loop never stitches scalar files across ticks.
///
/// Frames/units: `pos_cci`/`vel_cci` are CCI (parent-centred inertial), meters / m/s; `att_q` is the
/// Body→CCI quaternion `x y z w`; `alt.radar` is terrain-relative meters; masses are kg.
#[derive(Debug, Clone, Deserialize)]
pub struct Telemetry {
    pub seq: u64,
    pub ut: f64,
    pub warp: f64,
    pub id: String,
    pub sit: String,
    pub controlled: bool,
    /// KSA accepts flight-control commands (the vessel has a Control Module). Absent in older docs.
    #[serde(default = "default_true")]
    pub controllable: bool,
    #[serde(default)]
    pub parent: Option<String>,
    pub pos_cci: [f64; 3],
    pub vel_cci: [f64; 3],
    pub vel: Speeds,
    pub alt: Altitudes,
    pub mass: Masses,
    pub att_q: [f64; 4],
}

fn default_true() -> bool {
    true
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

/// The parent body's exponential-atmosphere constants (`bodies/<id>/atmosphere/…`). The plume trail
/// is emitted **only while inside the atmosphere** (KSA `Vehicle.UpdatePlumeTrailEmitters`: altitude
/// below `height` and density > 1e-9), so this block decides whether skywriting is possible at all,
/// and where the sweet spot is (high enough for ~zero drag, low enough to still emit).
#[derive(Debug, Clone, Copy)]
pub struct Atmosphere {
    /// Atmosphere top above the surface, m — the hard ceiling for trail emission.
    pub height: f64,
    /// Exponential scale height, m.
    pub scale_height: f64,
    /// Sea-level density, kg/m³.
    pub sea_level_density: f64,
}

impl Atmosphere {
    /// Density at altitude `a` (m) — `ρ₀·exp(−a/H)`, KSA `PhysicalAtmosphereReference`.
    pub fn density_at(&self, a: f64) -> f64 {
        self.sea_level_density * (-a / self.scale_height).exp()
    }
}

/// Parent-body constants read once from `bodies/<parent>/…`.
#[derive(Debug, Clone, Copy)]
pub struct Body {
    /// Standard gravitational parameter μ = GM, m³/s².
    pub mu: f64,
    /// Mean radius, m.
    pub radius: f64,
    /// Sidereal rotation rate ω, rad/s (about the CCI +Z spin axis).
    pub rotation_rate: f64,
    /// Present only when the body has an atmosphere — i.e. when a plume trail can exist.
    pub atmosphere: Option<Atmosphere>,
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
    /// Body-fixed longitude, deg (not in the telemetry doc; needed to recover the CCF hour angle).
    pub lon_deg: Option<f64>,
}

/// Reads the active vessel's telemetry (and its parent-body constants). Distinguishes "not connected"
/// from "connected but no active vessel" via a `time/ut` probe.
pub fn poll(src: &dyn Source) -> Tick {
    match read_telemetry(src) {
        Ok(t) => {
            let body = t.parent.as_deref().and_then(|p| read_body(src, p));
            let lon_deg = read_scalar(src, "vessels/active/position/lon");
            Tick {
                connected: true,
                telemetry: Some(t),
                body,
                lon_deg,
            }
        }
        Err(_) => Tick {
            connected: src.read("time/ut").is_ok(),
            telemetry: None,
            body: None,
            lon_deg: None,
        },
    }
}

/// Reads + parses `vessels/active/telemetry` into a [`Telemetry`].
pub fn read_telemetry(src: &dyn Source) -> Result<Telemetry, String> {
    let raw = src.read("vessels/active/telemetry")?;
    serde_json::from_str(&raw).map_err(|e| format!("parse: {e}"))
}

/// Reads the parent body's constants. `rotation_rate` defaults to 0 (a non-rotating body) if absent;
/// the atmosphere block is `None` for airless bodies (its `present` flag file only exists with air).
pub fn read_body(src: &dyn Source, parent: &str) -> Option<Body> {
    let mu = read_scalar(src, &format!("bodies/{parent}/mu"))?;
    let radius = read_scalar(src, &format!("bodies/{parent}/radius"))?;
    let rotation_rate = read_scalar(src, &format!("bodies/{parent}/rotation_rate")).unwrap_or(0.0);
    let atmosphere =
        read_scalar(src, &format!("bodies/{parent}/atmosphere/height")).and_then(|height| {
            Some(Atmosphere {
                height,
                scale_height: read_scalar(
                    src,
                    &format!("bodies/{parent}/atmosphere/scale_height"),
                )?,
                sea_level_density: read_scalar(
                    src,
                    &format!("bodies/{parent}/atmosphere/sea_level_density"),
                )?,
            })
        });
    Some(Body {
        mu,
        radius,
        rotation_rate,
        atmosphere,
    })
}

/// Reads a single `/sim` scalar field (leading float), or `None` if absent/unparsable.
pub fn read_scalar(src: &dyn Source, path: &str) -> Option<f64> {
    src.read(path).ok().and_then(|s| parse_scalar(&s))
}

/// Aggregated propulsion spec from the active vessel's `engines/*`: summed vacuum thrust,
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
                throttle_min = throttle_min
                    .max(read_scalar(src, &format!("{base}/min_throttle")).unwrap_or(0.0));
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

/// Parses the leading float of a `/sim` scalar (`G9` doubles). `"1"`, `"0.42"`, `"-0"` all parse.
pub fn parse_scalar(value: &str) -> Option<f64> {
    value.split_whitespace().next()?.parse::<f64>().ok()
}

// ---- filesystem source (the real /sim mount) ----------------------------------------------------

/// Reads the `/sim` mount directly via `std::fs`. `root` defaults to `/sim` in the guest, but can
/// point at any directory (e.g. a hand-made fixture for host-side dev).
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
        // One newline-terminated write — the control file actuates on the newline and a failed
        // write(2) surfaces the real errno via the io::Error.
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

    fn wait_until(&self, ut: f64) -> Result<f64, String> {
        // `time/alarm` parks the read until sim time reaches the written target, then returns the
        // reached ut — the 9p server's warp-correct sleep. A fixture dir (host tests) has a plain
        // file here, which returns immediately; that degrades to a busy poll, which is fine for
        // fixtures.
        self.write("time/alarm", &format!("{ut}"))
            .map_err(|e| e.errno)?;
        let raw = self.read("time/alarm")?;
        parse_scalar(&raw).ok_or_else(|| "EIO".to_string())
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
    /// Long-timeout agent for the blocking `/v1/time/wait` endpoint.
    waiter: ureq::Agent,
}

impl HttpSource {
    pub fn new(base: impl Into<String>) -> Self {
        let agent = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(2))
            .timeout_read(Duration::from_secs(4))
            .build();
        let waiter = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(2))
            .timeout_read(Duration::from_secs(120))
            .build();
        Self {
            base: base.into().trim_end_matches('/').to_string(),
            agent,
            waiter,
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

    fn wait_until(&self, ut: f64) -> Result<f64, String> {
        match self
            .waiter
            .get(&format!("{}/time/wait?until={ut}", self.base))
            .call()
        {
            Ok(resp) => {
                let s = resp.into_string().map_err(|_| "EIO".to_string())?;
                parse_scalar(&s).ok_or_else(|| "EIO".to_string())
            }
            Err(ureq::Error::Status(code, _)) => Err(format!("HTTP{code}")),
            Err(_) => Err("ECONN".to_string()),
        }
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"{
        "seq": 42, "ut": 12345.6, "warp": 1, "id": "Hunter", "sit": "Flying",
        "controlled": true, "controllable": true, "parent": "Earth",
        "pos_cci": [6378100.0, 0.0, 1000.0],
        "pos_ecl": [1.0, 2.0, 3.0],
        "vel_cci": [10.0, 465.0, -2.0],
        "vel": {"orb": 465.1, "surf": 10.2, "inr": 465.1},
        "alt": {"baro": 12000.0, "radar": 11800.0},
        "mass": {"t": 5980.0, "d": 4800.0, "p": 1180.0},
        "att_q": [0.1, 0.2, 0.3, 0.927]
    }"#;

    #[test]
    fn telemetry_parses_and_ignores_unknown_fields() {
        let t: Telemetry = serde_json::from_str(SAMPLE).unwrap();
        assert_eq!(t.id, "Hunter");
        assert!(t.controllable);
        assert_eq!(t.parent.as_deref(), Some("Earth"));
        assert_eq!(t.alt.radar, 11800.0);
    }

    #[test]
    fn telemetry_defaults_controllable_when_absent() {
        let minimal = r#"{
            "seq": 1, "ut": 0, "warp": 1, "id": "x", "sit": "Landed", "controlled": false,
            "pos_cci": [0,0,0], "vel_cci": [0,0,0],
            "vel": {"orb": 0, "surf": 0, "inr": 0}, "alt": {"baro": 0, "radar": 0},
            "mass": {"t": 1, "d": 1, "p": 0}, "att_q": [0,0,0,1]
        }"#;
        let t: Telemetry = serde_json::from_str(minimal).unwrap();
        assert!(t.controllable);
        assert!(t.parent.is_none());
    }

    #[test]
    fn fs_source_reads_body_with_atmosphere() {
        let root = std::env::temp_dir().join(format!("skyc_src_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("vessels/active/position")).unwrap();
        fs::create_dir_all(root.join("bodies/Earth/atmosphere")).unwrap();
        fs::write(root.join("vessels/active/telemetry"), SAMPLE).unwrap();
        fs::write(root.join("vessels/active/position/lon"), "12.5\n").unwrap();
        fs::write(root.join("bodies/Earth/mu"), "3.986004418e14\n").unwrap();
        fs::write(root.join("bodies/Earth/radius"), "6378100\n").unwrap();
        fs::write(root.join("bodies/Earth/rotation_rate"), "7.2921159e-5\n").unwrap();
        fs::write(root.join("bodies/Earth/atmosphere/height"), "140000\n").unwrap();
        fs::write(root.join("bodies/Earth/atmosphere/scale_height"), "8500\n").unwrap();
        fs::write(
            root.join("bodies/Earth/atmosphere/sea_level_density"),
            "1.225\n",
        )
        .unwrap();

        let s = FsSource::new(&root);
        let tick = poll(&s);
        assert!(tick.connected);
        assert_eq!(tick.lon_deg, Some(12.5));
        let b = tick.body.expect("body present");
        let atmo = b.atmosphere.expect("atmosphere present");
        assert_eq!(atmo.height, 140000.0);
        // density falls exponentially
        assert!(atmo.density_at(8500.0) < atmo.sea_level_density);
        fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn body_without_atmosphere_reads_none() {
        let root = std::env::temp_dir().join(format!("skyc_moon_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("bodies/Moon")).unwrap();
        fs::write(root.join("bodies/Moon/mu"), "4.9e12\n").unwrap();
        fs::write(root.join("bodies/Moon/radius"), "1.73e6\n").unwrap();
        let s = FsSource::new(&root);
        let b = read_body(&s, "Moon").unwrap();
        assert!(b.atmosphere.is_none());
        fs::remove_dir_all(&root).ok();
    }
}
