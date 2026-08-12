//! Shared plumbing for the `fx` / `celebrate` binaries: the `/sim` (or HTTP `/v1/fs`) source,
//! kitten discovery, and the spawn-line writer for `/sim/debug/fx/`.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

/// Read/write one `/sim`-relative leaf (path never starts with a slash).
pub trait Source {
    fn read(&self, path: &str) -> Option<String>;
    fn write(&self, path: &str, value: &str) -> Result<(), String>;
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
    fn read(&self, path: &str) -> Option<String> {
        fs::read_to_string(self.resolve(path)).ok()
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        fs::write(self.resolve(path), value).map_err(|e| e.to_string())
    }
}

pub struct HttpSource {
    base: String,
    agent: ureq::Agent,
}

impl HttpSource {
    pub fn new(mut base: String) -> Self {
        while base.ends_with('/') {
            base.pop();
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
    fn read(&self, path: &str) -> Option<String> {
        self.agent
            .get(&self.url(path))
            .call()
            .ok()
            .and_then(|r| r.into_string().ok())
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        self.agent
            .post(&self.url(path))
            .set("Content-Type", "text/plain")
            .send_string(value)
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
}

/// Builds the source from the shared `--sim`/`--url` conventions (env: `GATOS_SIM` / `GATOS_HTTP`).
pub fn source_from(sim: Option<String>, url: Option<String>) -> Box<dyn Source> {
    let url = url.or_else(|| std::env::var("GATOS_HTTP").ok().filter(|s| !s.is_empty()));
    match url {
        Some(base) => Box::new(HttpSource::new(base)),
        None => {
            let root = sim
                .map(PathBuf::from)
                .or_else(|| std::env::var_os("GATOS_SIM").map(PathBuf::from))
                .unwrap_or_else(|| PathBuf::from("/sim"));
            Box::new(FsSource::new(root))
        }
    }
}

/// Every vessel whose `is_kitten` leaf reads 1, from the `vessels/list` roster (works over HTTP
/// too — `/v1/fs` has no directory listing).
pub fn discover_kittens(source: &dyn Source) -> Vec<String> {
    let roster = source.read("vessels/list").unwrap_or_default();
    roster
        .lines()
        .map(str::trim)
        .filter(|id| !id.is_empty())
        .filter(|id| {
            source
                .read(&format!("vessels/by-id/{id}/is_kitten"))
                .is_some_and(|v| v.trim() == "1")
        })
        .map(String::from)
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
