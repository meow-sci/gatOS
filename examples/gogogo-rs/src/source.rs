//! The data source for gogogo-rs: read/write the **active vessel's** `/sim` control fields by path.
//!
//! Everything this panel needs lives under the `vessels/active/…` alias, so it never has to know the
//! vessel id — `vessels/active/ctl/throttle`, `vessels/active/ctl/ignite`, `…/ctl/shutdown`, and the
//! per-engine `vessels/active/engines/<n>/active` flags it aggregates to know whether the engines are
//! lit. This is the same field-level filesystem surface `simfs-dashboard` is built on.
//!
//! Two backends, mirroring the sibling examples:
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): a read is one
//!   `read()`, a control write is one `echo value > file`, and engine discovery is one `readdir`.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url`/`$GATOS_HTTP` dev mode).
//!   It has no directory listing, so engine state is unknown over HTTP (the toggle falls back to the
//!   last command's intent — see `app.rs`).
//!
//! One background worker thread (see `main.rs`) owns the source: it polls the few fields once per
//! interval and applies control writes between polls, so the render/input loop never blocks on I/O.

use std::fs;
use std::path::PathBuf;
use std::time::Duration;

/// The outcome of a failed control write — an errno-ish tag + message (the frozen control-file errno
/// vocabulary: `EINVAL`, `EACCES`, `EBUSY`, …), surfaced on the status line.
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// A read/write interface over the active vessel's `/sim` control fields. Implementations run on the
/// worker thread only.
pub trait Source: Send {
    /// Reads a scalar field's current value (trailing newline trimmed). `Err` carries a short tag
    /// (e.g. `"ENOENT"`).
    fn read(&self, path: &str) -> Result<String, String>;

    /// Writes `value` to a field as one newline-terminated write (the `echo value > file` shape), so
    /// a control file actuates and a failure carries the real errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// The active vessel's engine indices (the `vessels/active/engines/<n>` directory names), or
    /// `None` when listing is unsupported (HTTP) or there is no active vessel.
    fn engine_indices(&self) -> Option<Vec<String>>;

    /// A short label for the title bar (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;
}

/// A single poll of the active vessel's control state.
pub struct Poll {
    /// The source is reachable (the `/sim` mount exists / the HTTP server answers).
    pub connected: bool,
    /// There is a valid active vessel (its `id` reads back). When false, the panel disables itself.
    pub active: bool,
    /// The current throttle 0..1, if readable.
    pub throttle: Option<f64>,
    /// Whether any engine is lit (`Some`), or unknown (`None` — HTTP, or no active vessel).
    pub ignited: Option<bool>,
}

/// Reads the active vessel's control state in as few field reads as possible: the happy path (a live
/// vessel) is `id` + `throttle` + the engine flags; with no active vessel it is `id` + a `time/ut`
/// probe to tell "no vessel" from "not connected".
pub fn poll(src: &dyn Source) -> Poll {
    if src.read("vessels/active/id").is_err() {
        return Poll {
            connected: src.read("time/ut").is_ok(),
            active: false,
            throttle: None,
            ignited: None,
        };
    }
    let throttle = src
        .read("vessels/active/ctl/throttle")
        .ok()
        .and_then(|s| parse_scalar(&s));
    Poll {
        connected: true,
        active: true,
        throttle,
        ignited: engines_ignited(src),
    }
}

/// Aggregates the active vessel's engine `active` flags: `Some(true)` if any engine is lit, `None`
/// when the indices can't be listed (HTTP).
fn engines_ignited(src: &dyn Source) -> Option<bool> {
    let indices = src.engine_indices()?;
    let mut any = false;
    for i in &indices {
        if let Ok(v) = src.read(&format!("vessels/active/engines/{i}/active")) {
            any |= v.trim() == "1";
        }
    }
    Some(any)
}

/// Parses the leading float of a `/sim` scalar (G9 doubles). `"1"`, `"0.42"`, `"-0"` all parse.
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

    fn engine_indices(&self) -> Option<Vec<String>> {
        let dir = self.root.join("vessels/active/engines");
        let mut out = Vec::new();
        for entry in fs::read_dir(dir).ok()?.flatten() {
            if entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                out.push(entry.file_name().to_string_lossy().into_owned());
            }
        }
        Some(out)
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
/// `http://sim:4242/v1` (`$GATOS_HTTP`). No directory listing, so [`Source::engine_indices`] is
/// `None` and the ignite/shutdown toggle reflects local command intent over this transport.
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

    fn engine_indices(&self) -> Option<Vec<String>> {
        None // no listing endpoint; the toggle falls back to command intent
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

// ---- worker channel protocol --------------------------------------------------------------------

/// A control request from the UI thread to the worker.
pub enum ToWorker {
    /// Set the throttle (0..1) — written to `vessels/active/ctl/throttle`.
    Throttle(f64),
    /// Fire `vessels/active/ctl/ignite`.
    Ignite,
    /// Fire `vessels/active/ctl/shutdown`.
    Shutdown,
}

/// A reply from the worker to the UI thread.
pub enum FromWorker {
    /// The latest poll of the active vessel's control state.
    Poll(Poll),
    /// A control write completed (carries the status-line message + whether it failed).
    Write { message: String, is_error: bool },
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_scalar_takes_leading_float() {
        assert_eq!(parse_scalar("0.42"), Some(0.42));
        assert_eq!(parse_scalar("1 2 3"), Some(1.0));
        assert_eq!(parse_scalar("nope"), None);
    }

    #[test]
    fn fs_source_reads_writes_and_lists_engines() {
        let root = std::env::temp_dir().join(format!("gogogo_src_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("vessels/active/ctl")).unwrap();
        fs::create_dir_all(root.join("vessels/active/engines/0")).unwrap();
        fs::create_dir_all(root.join("vessels/active/engines/1")).unwrap();
        fs::write(root.join("vessels/active/id"), "Kerbal-1\n").unwrap();
        fs::write(root.join("vessels/active/ctl/throttle"), "0.5\n").unwrap();
        fs::write(root.join("vessels/active/engines/0/active"), "1\n").unwrap();
        fs::write(root.join("vessels/active/engines/1/active"), "0\n").unwrap();

        let s = FsSource::new(&root);

        // Reads trim the trailing newline (the /sim file convention).
        assert_eq!(s.read("vessels/active/ctl/throttle").unwrap(), "0.5");
        assert_eq!(s.read("vessels/active/nope").unwrap_err(), "ENOENT");

        // A write is one newline-terminated payload.
        s.write("vessels/active/ctl/throttle", "0.75").unwrap();
        assert_eq!(s.read("vessels/active/ctl/throttle").unwrap(), "0.75");

        let mut idx = s.engine_indices().unwrap();
        idx.sort();
        assert_eq!(idx, vec!["0".to_string(), "1".to_string()]);

        // A full poll sees a live vessel, the throttle, and "ignited" (engine 0 is lit).
        let p = poll(&s);
        assert!(p.connected && p.active);
        assert_eq!(p.throttle, Some(0.75));
        assert_eq!(p.ignited, Some(true));

        fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn poll_reports_no_active_vessel() {
        let root = std::env::temp_dir().join(format!("gogogo_empty_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("time")).unwrap();
        fs::write(root.join("time/ut"), "123\n").unwrap();

        let p = poll(&FsSource::new(&root));
        assert!(p.connected); // /sim is mounted…
        assert!(!p.active); // …but there is no active vessel
        assert_eq!(p.throttle, None);
        assert_eq!(p.ignited, None);

        fs::remove_dir_all(&root).ok();
    }
}
