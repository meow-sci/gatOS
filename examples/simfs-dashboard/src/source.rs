//! The data source abstraction and its two backends. A [`Source`] reads and writes `/sim` *fields*
//! by path — the whole program is built on the field-level filesystem surface, not the atomic JSON
//! snapshot the sibling `dashboard-rs` uses.
//!
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): a field
//!   read is one `read()`, a control write is one `echo value > file`, and the search popup walks
//!   the directory tree. This is the truest "DIY filesystem dashboard" and the intended mode.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url` mode, so it runs on the
//!   host for dev like `dashboard-rs`). The same paths, served over slirp.
//!
//! Both are driven by one background worker thread (see `main.rs`) that re-reads only the fields
//! currently on the dashboard, once per `--interval` — so polling cost is O(placed widgets), and
//! the header advertises the cadence exactly like `dashboard-rs`.

use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::time::Duration;

use serde::Deserialize;

use crate::catalog;

/// The outcome of a failed field write — an errno name + message (the frozen control-file errno
/// vocabulary: `EINVAL`, `EACCES`, `EBUSY`, …).
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// One directory entry from [`Source::list_dir`].
#[derive(Clone, Debug)]
pub struct DirEntry {
    pub name: String,
    pub is_dir: bool,
}

/// Integration health flags read from the source (drives the header + which catalog templates the
/// search popup offers).
#[derive(Clone, Copy, Default, Debug)]
pub struct Health {
    pub connected: bool,
    pub control: bool,
    pub debug: bool,
}

/// A read/write/enumerate interface over the `/sim` field surface. Implementations run on the
/// worker thread only.
pub trait Source: Send {
    /// Reads a scalar field's current value (trailing newline trimmed). `Err` carries a short tag
    /// (e.g. `"ENOENT"`) suitable for display on the widget card.
    fn read(&self, path: &str) -> Result<String, String>;

    /// Writes `value` to a field as one newline-terminated write (the `echo value > file` shape),
    /// so a control file actuates and a failure carries the real errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// Lists a directory (filesystem source only; the HTTP mirror has no listing endpoint).
    fn list_dir(&self, path: &str) -> Result<Vec<DirEntry>, String>;

    /// Every scalar leaf under the root, `/`-joined relative paths (filesystem source only — HTTP
    /// returns empty and relies on catalog template expansion).
    fn walk_leaves(&self) -> Vec<String>;

    /// The live vessel ids, as `/sim` path segments (already sanitized).
    fn vessel_ids(&self) -> Vec<String>;

    /// The live celestial body ids, as `/sim` path segments (already sanitized).
    fn body_ids(&self) -> Vec<String>;

    /// Integration health (connection + control/debug gating).
    fn health(&self) -> Health;

    /// A short label for the header (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;
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
        fs::write(self.root.join(path), payload).map_err(|e| {
            let errno = errno_name(e.raw_os_error());
            CmdError {
                errno,
                message: e.to_string(),
            }
        })
    }

    fn list_dir(&self, path: &str) -> Result<Vec<DirEntry>, String> {
        let mut out = Vec::new();
        for entry in fs::read_dir(self.root.join(path)).map_err(|e| errno_name(e.raw_os_error()))? {
            let entry = entry.map_err(|e| errno_name(e.raw_os_error()))?;
            let name = entry.file_name().to_string_lossy().into_owned();
            let is_dir = entry.file_type().map(|t| t.is_dir()).unwrap_or(false);
            out.push(DirEntry { name, is_dir });
        }
        out.sort_by(|a, b| a.name.cmp(&b.name));
        Ok(out)
    }

    fn walk_leaves(&self) -> Vec<String> {
        let mut out = Vec::new();
        walk(&self.root, "", 0, &mut out);
        out.sort();
        out
    }

    fn vessel_ids(&self) -> Vec<String> {
        self.list_dir("vessels/by-id")
            .map(|es| {
                es.into_iter()
                    .filter(|e| e.is_dir)
                    .map(|e| e.name)
                    .collect()
            })
            .unwrap_or_default()
    }

    fn body_ids(&self) -> Vec<String> {
        self.list_dir("bodies")
            .map(|es| {
                es.into_iter()
                    .filter(|e| e.is_dir)
                    .map(|e| e.name)
                    .collect()
            })
            .unwrap_or_default()
    }

    fn health(&self) -> Health {
        Health {
            connected: self.root.is_dir(),
            control: self.root.join("status").is_dir(),
            debug: self.root.join("debug").is_dir(),
        }
    }

    fn label(&self) -> String {
        format!("fs:{}", self.root.display())
    }
}

/// Depth-first leaf walk. Prunes the `vessels/active` alias (it duplicates the active vessel's
/// subtree) and skips streaming files (a `read()` on `stream`/`events`/`alarm` would park).
fn walk(dir: &std::path::Path, prefix: &str, depth: usize, out: &mut Vec<String>) {
    if depth > 12 {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().into_owned();
        let rel = if prefix.is_empty() {
            name.clone()
        } else {
            format!("{prefix}/{name}")
        };
        let Ok(ft) = entry.file_type() else {
            continue;
        };
        if ft.is_dir() {
            if rel == "vessels/active" {
                continue;
            }
            walk(&entry.path(), &rel, depth + 1, out);
        } else if !catalog::path_is_streaming(&rel) {
            out.push(rel);
        }
    }
}

// ---- HTTP source (the /v1/fs mirror) ------------------------------------------------------------

/// Uses the mod's HTTP `/v1/fs/<path>` field mirror (and `/v1/vessels`, `/v1/bodies`, `/v1/status`
/// for discovery). `base` is the `/v1` root, e.g. `http://sim:4242/v1` (`$GATOS_HTTP`).
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
            Err(ureq::Error::Status(_, resp)) => Err(errno_body(resp).errno),
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
            Err(ureq::Error::Status(_, resp)) => Err(into_cmd_error(errno_body(resp))),
            Err(e) => Err(CmdError {
                errno: "ECONN".into(),
                message: e.to_string(),
            }),
        }
    }

    fn list_dir(&self, _path: &str) -> Result<Vec<DirEntry>, String> {
        Err("the HTTP source has no directory listing — search uses the catalog".to_string())
    }

    fn walk_leaves(&self) -> Vec<String> {
        Vec::new() // no enumeration endpoint; candidates() expands templates against the live ids
    }

    fn vessel_ids(&self) -> Vec<String> {
        self.get_json("vessels")
            .and_then(|v| {
                v.as_array().map(|a| {
                    a.iter()
                        .filter_map(|e| e.as_str())
                        .map(catalog::sanitize_segment)
                        .collect()
                })
            })
            .unwrap_or_default()
    }

    fn body_ids(&self) -> Vec<String> {
        self.get_json("bodies")
            .and_then(|v| {
                v.as_array().map(|a| {
                    a.iter()
                        .filter_map(|e| e.get("id").and_then(|i| i.as_str()))
                        .map(catalog::sanitize_segment)
                        .collect()
                })
            })
            .unwrap_or_default()
    }

    fn health(&self) -> Health {
        match self.get_json("status") {
            Some(v) => Health {
                connected: true,
                control: v.get("control").and_then(|c| c.as_bool()).unwrap_or(false),
                debug: v.get("debug").and_then(|d| d.as_bool()).unwrap_or(false),
            },
            None => Health::default(),
        }
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

impl HttpSource {
    fn get_json(&self, path: &str) -> Option<serde_json::Value> {
        self.agent
            .get(&format!("{}/{path}", self.base))
            .call()
            .ok()?
            .into_json()
            .ok()
    }
}

#[derive(Deserialize, Default)]
struct ErrorBody {
    #[serde(default)]
    errno: String,
    #[serde(default)]
    message: String,
}

/// A `{errno,message}` body with sane fallbacks (mirrors the field endpoints' error shape).
struct ErrnoBody {
    errno: String,
    message: String,
}

fn errno_body(resp: ureq::Response) -> ErrnoBody {
    let body: ErrorBody = resp.into_json().unwrap_or_default();
    ErrnoBody {
        errno: if body.errno.is_empty() {
            "EIO".into()
        } else {
            body.errno
        },
        message: if body.message.is_empty() {
            "request failed".into()
        } else {
            body.message
        },
    }
}

fn into_cmd_error(b: ErrnoBody) -> CmdError {
    CmdError {
        errno: b.errno,
        message: b.message,
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

// ---- worker channel protocol --------------------------------------------------------------------

/// A request from the UI thread to the worker.
pub enum ToWorker {
    /// Replace the set of fields polled each interval (the dashboard's widget paths).
    Subscribe(Vec<String>),
    /// Write one field (a control actuation). `note` is echoed back for the status line.
    Write {
        path: String,
        value: String,
        note: String,
    },
    /// Rebuild the search candidate list (re-list ids, walk leaves, read health).
    Refresh,
}

/// A reply from the worker to the UI thread.
pub enum FromWorker {
    /// The latest values of the subscribed fields, plus a transport-connected hint.
    Values {
        values: HashMap<String, Result<String, String>>,
        connected: bool,
    },
    /// A write completed.
    WriteDone {
        note: String,
        result: Result<(), CmdError>,
    },
    /// A refreshed search candidate set + health flags.
    Catalog {
        candidates: Vec<catalog::Candidate>,
        health: Health,
    },
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn fs_source_walks_reads_and_prunes() {
        let root = std::env::temp_dir().join(format!("simfs_src_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("vessels/by-id/A/altitude")).unwrap();
        fs::create_dir_all(root.join("vessels/active/altitude")).unwrap();
        fs::create_dir_all(root.join("status")).unwrap();
        fs::write(root.join("vessels/by-id/A/altitude/radar"), "123\n").unwrap();
        fs::write(root.join("vessels/by-id/A/stream"), "line\n").unwrap();
        fs::write(root.join("vessels/active/altitude/radar"), "9\n").unwrap();

        let s = FsSource::new(&root);

        // Reads trim the trailing newline (the /sim file convention).
        assert_eq!(s.read("vessels/by-id/A/altitude/radar").unwrap(), "123");
        // A missing field surfaces ENOENT.
        assert_eq!(s.read("vessels/by-id/A/nope").unwrap_err(), "ENOENT");

        let leaves = s.walk_leaves();
        assert!(leaves.contains(&"vessels/by-id/A/altitude/radar".to_string()));
        assert!(!leaves.iter().any(|l| l.starts_with("vessels/active"))); // alias pruned
        assert!(!leaves.iter().any(|l| l.ends_with("/stream"))); // streaming skipped

        assert_eq!(s.vessel_ids(), vec!["A".to_string()]);
        let health = s.health();
        assert!(health.connected && health.control && !health.debug);

        fs::remove_dir_all(&root).ok();
    }
}
