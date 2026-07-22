//! The `/sim` data source for the AGC bridge — the land-o-matic `Source` pattern (fs + HTTP
//! backends, one worker thread owns it), plus the extra reads the bridge needs beyond the
//! atomic telemetry doc: `attitude/rates` (body rad/s — not in the compact doc), body
//! constants, and the `ctl/batch` write the RCS demodulator uses.

use std::fs;
use std::path::PathBuf;
use std::time::Duration;

use serde::Deserialize;

/// The outcome of a failed control write — an errno-ish tag + message (the frozen control-file
/// errno vocabulary: `EINVAL`, `EACCES`, `EBUSY`, `ETIMEDOUT`, …), surfaced on the status panel.
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// A read/write interface over the active vessel's `/sim` fields. Implementations run on the
/// bridge's sim thread only.
pub trait Source: Send {
    /// Reads a field's current value (trailing newline trimmed).
    fn read(&self, path: &str) -> Result<String, String>;

    /// Writes `value` as one newline-terminated write (the `echo value > file` shape).
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// Lists the child names of a directory (for enumerating `engines/*`).
    fn list(&self, path: &str) -> Vec<String>;

    /// A short label for the status panel.
    fn label(&self) -> String;
}

// ---- telemetry model ----------------------------------------------------------------------------

/// The whole-vessel `telemetry` document (`gatOS.SimFs/Formats.cs` `VesselTelemetry`) — one
/// `read()` yields one self-consistent snapshot (`seq`/`ut` included), so the IMU/PIPA feeds
/// never stitch scalar files across ticks. Frames/units: `pos_cci`/`vel_cci` are CCI meters /
/// m/s; `att_q` is the Body→CCI quaternion `x y z w`; `alt.radar` is terrain-relative meters;
/// masses kg.
#[derive(Debug, Clone, Deserialize)]
pub struct Telemetry {
    pub seq: u64,
    pub ut: f64,
    pub warp: f64,
    pub id: String,
    pub sit: String,
    pub controlled: bool,
    #[serde(default)]
    pub controllable: Option<bool>,
    #[serde(default)]
    pub parent: Option<String>,
    pub pos_cci: [f64; 3],
    pub vel_cci: [f64; 3],
    pub alt: Altitudes,
    pub mass: Masses,
    pub att_q: [f64; 4],
}

#[derive(Debug, Clone, Deserialize)]
pub struct Altitudes {
    /// Above the mean-radius datum, m.
    pub baro: f64,
    /// Above local terrain/ocean, m — the landing-radar truth source.
    pub radar: f64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Masses {
    pub t: f64,
    pub d: f64,
    pub p: f64,
}

/// Parent-body constants read once from `bodies/<parent>/…`.
#[derive(Debug, Clone, Copy, Default)]
pub struct Body {
    /// μ = GM, m³/s².
    pub mu: f64,
    /// Mean radius, m.
    pub radius: f64,
    /// Sidereal rotation rate ω about CCI +Z, rad/s.
    pub rotation_rate: f64,
}

/// One poll of the active vessel (telemetry doc + the side reads the bridge needs).
#[derive(Debug, Clone, Default)]
pub struct Tick {
    pub connected: bool,
    pub telemetry: Option<Telemetry>,
    pub body: Option<Body>,
    /// Body rotation rates, rad/s (`attitude/rates` — a separate scalar-vector file).
    pub rates: Option<[f64; 3]>,
    /// `time/sim_dt` == 0 ⇒ the game is paused.
    pub sim_dt: Option<f64>,
}

/// Reads the active vessel's telemetry + the bridge's side reads.
pub fn poll(src: &dyn Source) -> Tick {
    match read_telemetry(src) {
        Ok(t) => {
            let body = t.parent.as_deref().and_then(|p| read_body(src, p));
            let rates = read_vector3(src, "vessels/active/attitude/rates");
            let sim_dt = read_scalar(src, "time/sim_dt");
            Tick { connected: true, telemetry: Some(t), body, rates, sim_dt }
        }
        Err(_) => Tick {
            connected: src.read("time/ut").is_ok(),
            telemetry: None,
            body: None,
            rates: None,
            sim_dt: read_scalar(src, "time/sim_dt"),
        },
    }
}

pub fn read_telemetry(src: &dyn Source) -> Result<Telemetry, String> {
    let raw = src.read("vessels/active/telemetry")?;
    serde_json::from_str(&raw).map_err(|e| format!("parse: {e}"))
}

pub fn read_body(src: &dyn Source, parent: &str) -> Option<Body> {
    let mu = read_scalar(src, &format!("bodies/{parent}/mu"))?;
    let radius = read_scalar(src, &format!("bodies/{parent}/radius"))?;
    let rotation_rate = read_scalar(src, &format!("bodies/{parent}/rotation_rate")).unwrap_or(0.0);
    Some(Body { mu, radius, rotation_rate })
}

/// Reads a single `/sim` scalar field (leading float), or `None` if absent/unparsable.
pub fn read_scalar(src: &dyn Source, path: &str) -> Option<f64> {
    src.read(path).ok().and_then(|s| parse_scalar(&s))
}

/// Reads a space-separated 3-vector file (`x y z`).
pub fn read_vector3(src: &dyn Source, path: &str) -> Option<[f64; 3]> {
    let s = src.read(path).ok()?;
    let mut it = s.split_whitespace().map(|t| t.parse::<f64>().ok());
    Some([it.next()??, it.next()??, it.next()??])
}

/// Parses the leading float of a `/sim` scalar (`G9` doubles).
pub fn parse_scalar(value: &str) -> Option<f64> {
    value.split_whitespace().next()?.parse::<f64>().ok()
}

/// The active vessel's geodetic latitude/longitude, degrees (separate scalar files).
pub fn read_lat_lon(src: &dyn Source) -> Option<(f64, f64)> {
    Some((
        read_scalar(src, "vessels/active/position/lat")?,
        read_scalar(src, "vessels/active/position/lon")?,
    ))
}

/// Aggregated propulsion spec from `engines/*`: summed vac thrust + the throttle floor.
#[derive(Debug, Clone, Copy)]
pub struct EngineAgg {
    pub thrust_max: f64,
    pub throttle_min: f64,
    pub count: usize,
}

pub fn read_engines(src: &dyn Source) -> Option<EngineAgg> {
    let mut thrust = 0.0;
    let mut floor = 0.0f64;
    let mut count = 0;
    for name in src.list("vessels/active/engines") {
        let base = format!("vessels/active/engines/{name}");
        if let Some(t) = read_scalar(src, &format!("{base}/vac_thrust")) {
            if t > 0.0 {
                thrust += t;
                floor = floor.max(read_scalar(src, &format!("{base}/min_throttle")).unwrap_or(0.0));
                count += 1;
            }
        }
    }
    (count > 0).then_some(EngineAgg { thrust_max: thrust, throttle_min: floor, count })
}

// ---- control writes -------------------------------------------------------------------------

/// One newline-terminated write to a control file under `vessels/active/`.
pub fn write_ctl(src: &dyn Source, name: &str, value: &str) -> Result<(), CmdError> {
    src.write(&format!("vessels/active/ctl/{name}"), value)
}

/// The one-batch rotate+translate write the RCS demodulator issues each tick: both commands
/// execute atomically in the same game tick (SPEC §3.10). Lines are `<action> <args>`.
pub fn write_batch(src: &dyn Source, lines: &[String]) -> Result<(), CmdError> {
    if lines.is_empty() {
        return Ok(());
    }
    src.write("vessels/active/ctl/batch", &lines.join("\n"))
}

// ---- filesystem source (the real /sim mount) ----------------------------------------------------

/// Reads the `/sim` mount directly via `std::fs`. `root` defaults to `/sim` in the guest, but
/// can point at any directory (a hand-made fixture for host-side dev).
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
        "seq": 42, "ut": 12345.6, "warp": 1, "id": "Eagle", "sit": "Freefall",
        "controlled": true, "controllable": true, "parent": "Luna",
        "pos_cci": [1800000.0, 0.0, 1000.0],
        "vel_cci": [10.0, 1600.0, -2.0],
        "vel": {"orb": 1600.0, "surf": 1595.0, "inr": 1600.0},
        "alt": {"baro": 15000.0, "radar": 14930.0},
        "mass": {"t": 15000.0, "d": 6800.0, "p": 8200.0},
        "att_q": [0.1, 0.2, 0.3, 0.927]
    }"#;

    #[test]
    fn telemetry_parses() {
        let t: Telemetry = serde_json::from_str(SAMPLE).unwrap();
        assert_eq!(t.seq, 42);
        assert_eq!(t.parent.as_deref(), Some("Luna"));
        assert_eq!(t.alt.radar, 14930.0);
        assert_eq!(t.att_q[3], 0.927);
    }

    #[test]
    fn fixture_round_trip() {
        let dir = std::env::temp_dir().join(format!("agc-sim-test-{}", std::process::id()));
        std::fs::create_dir_all(dir.join("vessels/active/attitude")).unwrap();
        std::fs::create_dir_all(dir.join("bodies/Luna")).unwrap();
        std::fs::create_dir_all(dir.join("time")).unwrap();
        std::fs::write(dir.join("vessels/active/telemetry"), SAMPLE).unwrap();
        std::fs::write(dir.join("vessels/active/attitude/rates"), "0.01 -0.02 0.003\n").unwrap();
        std::fs::write(dir.join("bodies/Luna/mu"), "4.9048695e12\n").unwrap();
        std::fs::write(dir.join("bodies/Luna/radius"), "1737100\n").unwrap();
        std::fs::write(dir.join("bodies/Luna/rotation_rate"), "2.6617e-6\n").unwrap();
        std::fs::write(dir.join("time/ut"), "12345.6\n").unwrap();
        std::fs::write(dir.join("time/sim_dt"), "0.02\n").unwrap();

        let src = FsSource::new(&dir);
        let tick = poll(&src);
        assert!(tick.connected);
        let t = tick.telemetry.unwrap();
        assert_eq!(t.id, "Eagle");
        assert_eq!(tick.rates.unwrap(), [0.01, -0.02, 0.003]);
        let b = tick.body.unwrap();
        assert!((b.radius - 1_737_100.0).abs() < 1.0);
        assert_eq!(tick.sim_dt, Some(0.02));
        std::fs::remove_dir_all(&dir).ok();
    }
}
