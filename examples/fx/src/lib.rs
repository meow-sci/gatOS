//! Shared plumbing for the `fx` / `celebrate` binaries: the `/sim` (or HTTP `/v1/fs`) source,
//! kitten discovery, and the spawn-line writer for `/sim/debug/fx/`.
//!
//! **Sources.** The `/sim` mount is the default and the preferred one: run inside the gatOS guest
//! (or anywhere the 9p export is mounted) and no flags are needed. `--sim <path>` / `$GATOS_SIM`
//! move the root; `--url <base>` / `$GATOS_HTTP` switch to the mod's HTTP `/v1/fs/<path>` mirror
//! instead — the host-side dev path. The guest login shell *presets* `$GATOS_HTTP` whenever the
//! host serves HTTP, so it is only consulted when no mount is there to use.
//!
//! `$GATOS_HTTP` is the `/v1` base (`http://sim:4242/v1`), so a `--url` base is accepted with or
//! without the `/v1` suffix; both address `/v1/fs/<path>`.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

/// Where the 9p export is mounted in the guest.
const DEFAULT_SIM: &str = "/sim";

/// Read/write one `/sim`-relative leaf (path never starts with a slash).
pub trait Source {
    /// A short description of where this source points, for error messages.
    fn label(&self) -> String;

    fn read(&self, path: &str) -> Option<String>;

    fn write(&self, path: &str, value: &str) -> Result<(), String>;

    /// The child names of a `/sim` directory, or None when the transport has no listing (HTTP).
    fn list(&self, path: &str) -> Option<Vec<String>>;

    /// The vessel roster (ids as they appear under `vessels/by-id`).
    fn vessels(&self) -> Vec<String>;
}

pub struct FsSource {
    root: PathBuf,
}

impl FsSource {
    pub fn new(root: PathBuf) -> Self {
        Self { root }
    }

    fn resolve(&self, path: &str) -> PathBuf {
        let mut p = self.root.clone();
        p.extend(Path::new(path));
        p
    }
}

impl Source for FsSource {
    fn label(&self) -> String {
        format!("the mount at {}", self.root.display())
    }

    fn read(&self, path: &str) -> Option<String> {
        fs::read_to_string(self.resolve(path)).ok()
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        fs::write(self.resolve(path), value).map_err(|e| e.to_string())
    }

    fn list(&self, path: &str) -> Option<Vec<String>> {
        let mut names: Vec<String> = fs::read_dir(self.resolve(path))
            .ok()?
            .filter_map(|e| Some(e.ok()?.file_name().to_string_lossy().into_owned()))
            .collect();
        names.sort();
        Some(names)
    }

    fn vessels(&self) -> Vec<String> {
        self.list("vessels/by-id").unwrap_or_default()
    }
}

pub struct HttpSource {
    /// The server root *without* the `/v1` suffix — `url()` adds it back.
    base: String,
    agent: ureq::Agent,
}

impl HttpSource {
    pub fn new(base: String) -> Self {
        // `$GATOS_HTTP` is the /v1 API base (`http://sim:4242/v1`), but a bare host base is the
        // natural thing to type on --url. Normalize both to the server root.
        let mut base = base.trim_end_matches('/').to_string();
        if let Some(root) = base.strip_suffix("/v1") {
            base = root.to_string();
        }
        Self {
            base,
            agent: ureq::AgentBuilder::new()
                .timeout(Duration::from_secs(5))
                .build(),
        }
    }

    fn url(&self, path: &str) -> String {
        format!("{}/v1/fs/{path}", self.base)
    }
}

impl Source for HttpSource {
    fn label(&self) -> String {
        format!("the HTTP API at {}/v1", self.base)
    }

    fn read(&self, path: &str) -> Option<String> {
        self.agent
            .get(&self.url(path))
            .call()
            .ok()
            .and_then(|r| r.into_string().ok())
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        match self
            .agent
            .post(&self.url(path))
            .set("Content-Type", "text/plain")
            .send_string(value)
        {
            Ok(_) => Ok(()),
            // The mod answers a rejected field write with a JSON error body ({"error":"EACCES",…});
            // it says far more than "400 Bad Request" does.
            Err(ureq::Error::Status(code, response)) => Err(match response.into_string() {
                Ok(body) if !body.trim().is_empty() => format!("HTTP {code}: {}", body.trim()),
                _ => format!("HTTP {code}"),
            }),
            Err(e) => Err(e.to_string()),
        }
    }

    fn list(&self, _path: &str) -> Option<Vec<String>> {
        None // /v1/fs serves leaves only.
    }

    fn vessels(&self) -> Vec<String> {
        // GET /v1/vessels is the roster endpoint: a JSON array of ids, e.g. ["Hunter","Polaris"].
        let body = self
            .agent
            .get(&format!("{}/v1/vessels", self.base))
            .call()
            .ok()
            .and_then(|r| r.into_string().ok())
            .unwrap_or_default();
        json_string_array(&body)
    }
}

/// Pulls the elements out of a flat JSON array of strings (`["a","b"]`) — the one JSON shape these
/// programs consume, so they do not carry a parser to read it.
fn json_string_array(body: &str) -> Vec<String> {
    body.split('"')
        .skip(1)
        .step_by(2)
        .map(|s| s.replace("\\\"", "\"").replace("\\\\", "\\"))
        .collect()
}

/// Builds the source from the shared `--sim`/`--url` conventions: explicit flags win, then
/// `$GATOS_SIM`, then the mount if it is actually serving, then `$GATOS_HTTP`.
///
/// The mount deliberately outranks `$GATOS_HTTP`, because the guest login shell presets that
/// variable whenever the host serves the HTTP API: preferring it would route every in-guest run
/// through slirp, and through a transport that may be switched off by the time the program runs.
pub fn source_from(sim: Option<String>, url: Option<String>) -> Box<dyn Source> {
    if let Some(base) = url {
        return Box::new(HttpSource::new(base));
    }
    if let Some(root) = sim {
        return Box::new(FsSource::new(PathBuf::from(root)));
    }
    if let Some(root) = std::env::var_os("GATOS_SIM") {
        return Box::new(FsSource::new(PathBuf::from(root)));
    }
    // `/sim` exists as an empty directory even when the 9p mount is down (init creates it), so the
    // test is "is the tree there", not "does the path exist".
    if Path::new(DEFAULT_SIM).join("time").is_dir() {
        return Box::new(FsSource::new(PathBuf::from(DEFAULT_SIM)));
    }
    if let Some(base) = std::env::var("GATOS_HTTP").ok().filter(|s| !s.is_empty()) {
        return Box::new(HttpSource::new(base));
    }
    Box::new(FsSource::new(PathBuf::from(DEFAULT_SIM)))
}

/// Checks the transport before anything blames a vessel: an unmounted `/sim` and a world with no
/// kittens in it look identical one leaf at a time, and the first one is the one that keeps
/// happening. Returns the lines to print on failure.
pub fn check_source(source: &dyn Source) -> Result<(), Vec<String>> {
    if source.read("time/ut").is_none() {
        return Err(vec![
            format!("cannot reach the sim through {} (time/ut unreadable).", source.label()),
            "in the guest: is /sim mounted, and is the mod loaded? (mount | grep /sim)".into(),
            "over HTTP: gatos.toml needs http_enabled + http_field_endpoints = true".into(),
        ]);
    }
    if source.read("debug/fx/count").is_none() {
        return Err(vec![
            format!("{} has no debug/fx surface.", source.label()),
            "set debug_namespace = true in gatos.toml and reload the mod.".into(),
        ]);
    }
    Ok(())
}

/// Every vessel whose `is_kitten` leaf reads 1, over the source's vessel roster.
pub fn discover_kittens(source: &dyn Source) -> Vec<String> {
    source
        .vessels()
        .into_iter()
        .filter(|id| {
            source
                .read(&format!("vessels/by-id/{id}/is_kitten"))
                .is_some_and(|v| v.trim() == "1")
        })
        .collect()
}

/// One spawn write: `"<vessel> <profile> [scale <s>] [offset <x> <y> <z>]"`.
pub fn spawn(
    source: &dyn Source,
    vessel: &str,
    profile: &str,
    scale: f64,
    offset: Option<[f64; 3]>,
) -> Result<(), String> {
    let mut line = format!("{vessel} {profile}");
    if (scale - 1.0).abs() > f64::EPSILON {
        line.push_str(&format!(" scale {scale}"));
    }
    if let Some([x, y, z]) = offset {
        line.push_str(&format!(" offset {x} {y} {z}"));
    }
    source.write("debug/fx/spawn", &line)
}

/// The profile vocabulary as the mod reports it (falls back to the built-in four).
pub fn profiles(source: &dyn Source) -> Vec<String> {
    source
        .read("debug/fx/profiles")
        .map(|s| {
            s.trim()
                .split(',')
                .filter(|p| !p.is_empty())
                .map(String::from)
                .collect()
        })
        .unwrap_or_else(|| {
            ["party", "sparkle", "danger", "death"]
                .map(String::from)
                .to_vec()
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn http_base_normalizes_with_and_without_v1() {
        // $GATOS_HTTP carries the /v1 suffix; a hand-typed --url usually does not. Both must
        // address /v1/fs/<path> exactly once.
        for base in [
            "http://sim:4242",
            "http://sim:4242/",
            "http://sim:4242/v1",
            "http://sim:4242/v1/",
        ] {
            let source = HttpSource::new(base.to_string());
            assert_eq!(source.url("debug/fx/spawn"), "http://sim:4242/v1/fs/debug/fx/spawn", "base {base}");
        }
    }

    #[test]
    fn json_array_parses_the_vessel_roster() {
        assert_eq!(json_string_array("[\"Hunter\",\"Polaris\"]"), ["Hunter", "Polaris"]);
        assert!(json_string_array("[]").is_empty());
    }
}
