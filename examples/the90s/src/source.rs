//! The data source for the90s: the gatOS `/sim/audio` surface (SPEC §3.9) by path.
//!
//! Everything the soundboard does is four files — upload bytes into `audio/file/<clip>`, write a
//! play line to `audio/play`, a stop target to `audio/stop`, a volume adjust to `audio/set` — plus
//! `audio/status` (one line per live channel) polled for the per-sound "N playing" badges. A burst
//! of plays goes through `ctl/batch` instead, so they all start **in the same game tick** (§3.10).
//!
//! Two backends, mirroring the sibling examples:
//! - [`FsSource`] reads/writes the **real `/sim` mount** with `std::fs` (the in-guest default): an
//!   upload is one `fs::write` (playable on close), a play is one `echo line > file`.
//! - [`HttpSource`] uses the mod's HTTP `/v1` mirror (the `--url`/`$GATOS_HTTP` dev mode):
//!   `/v1/fs/<path>` for reads/writes, the dedicated binary `/v1/audio/file/{name}` route
//!   (chunked — bodies are capped at 1 MiB) for uploads, `/v1/audio/files` for the clip list.
//!
//! One background worker thread (see `main.rs`) owns the source: it registers the config's sounds
//! at startup, polls `audio/status` once per interval, and applies play/stop/volume commands
//! between polls, so the render/input loop never blocks on I/O.

use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::time::Duration;

/// The outcome of a failed write — an errno-ish tag + message (the frozen control-file errno
/// vocabulary: `ENOENT`, `EBUSY`, `EFBIG`, `ENOSPC`, …), surfaced on the status line.
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// A read/write interface over the `/sim` audio surface. Implementations run on the worker thread
/// only.
pub trait Source: Send {
    /// Reads a file's current text (trailing newline trimmed). `Err` carries a short tag
    /// (e.g. `"ENOENT"`).
    fn read(&self, path: &str) -> Result<String, String>;

    /// Writes `value` to a control file as one newline-terminated write (the `echo line > file`
    /// shape) — `value` may be multi-line (the `ctl/batch` group). A failure carries the errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// The stored size of clip `clip` in the `/sim/audio/file/` store, or `None` when absent.
    /// (Size is the staleness check: same name + same byte count ⇒ already registered.)
    fn clip_size(&self, clip: &str) -> Option<u64>;

    /// Uploads `bytes` as clip `clip` (create or replace) — playable once this returns.
    fn upload(&self, clip: &str, bytes: &[u8]) -> Result<(), CmdError>;

    /// A short label for the status line (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;
}

// ---- play/set/stop line grammar (SPEC §3.9) ------------------------------------------------------

/// The `audio/play` line for one press: clip name + the soundboard's current volume. Every press is
/// a **new channel** (auto `#N` id), so repeated presses of the same sound layer.
pub fn play_value(clip: &str, vol: f64) -> String {
    format!("{clip} vol={vol:.2}")
}

/// The `audio/set` line that live-adjusts every channel playing `clip` to the slider's volume.
pub fn set_value(clip: &str, vol: f64) -> String {
    format!("{clip} vol={vol:.2}")
}

/// A `ctl/batch` group that starts every clip in `clips` **in the same game tick** (SPEC §3.10) —
/// one `audio/play` line per clip, then the `commit` line. Callers keep `clips.len() <= 64` (the
/// batch command cap).
pub fn batch_play_value(clips: &[String], vol: f64) -> String {
    let mut out = String::new();
    for clip in clips {
        out.push_str(&format!("audio/play {}\n", play_value(clip, vol)));
    }
    out.push_str("commit");
    out
}

/// Parses `audio/status` (one `id name state pos_ms len_ms vol loop group` line per live channel)
/// into per-clip channel counts plus the total live channel count (which may include channels
/// started by other programs — the OMG STOP button stops those too).
pub fn status_counts(text: &str) -> (HashMap<String, u32>, u32) {
    let mut counts: HashMap<String, u32> = HashMap::new();
    let mut total = 0u32;
    for line in text.lines() {
        let mut cols = line.split_whitespace();
        let (Some(_id), Some(name)) = (cols.next(), cols.next()) else {
            continue;
        };
        total += 1;
        *counts.entry(name.to_string()).or_insert(0) += 1;
    }
    (counts, total)
}

// ---- filesystem source (the real /sim mount) -----------------------------------------------------

/// Reads/writes the `/sim` mount directly via `std::fs`. `root` defaults to `/sim` in the guest,
/// but can point at any directory (e.g. a hand-made fixture for host-side dev).
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

    fn clip_size(&self, clip: &str) -> Option<u64> {
        let meta = fs::metadata(self.root.join("audio/file").join(clip)).ok()?;
        meta.is_file().then(|| meta.len())
    }

    fn upload(&self, clip: &str, bytes: &[u8]) -> Result<(), CmdError> {
        // Create-or-truncate + write + close: the clip becomes playable on the close (clunk); a
        // mid-write cap failure (EFBIG/ENOSPC) surfaces on the failing write(2).
        fs::write(self.root.join("audio/file").join(clip), bytes).map_err(|e| CmdError {
            errno: errno_name(e.raw_os_error()),
            message: e.to_string(),
        })
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
        Some(27) => "EFBIG".into(),
        Some(28) => "ENOSPC".into(),
        Some(30) => "EROFS".into(),
        Some(95) => "EOPNOTSUPP".into(),
        Some(110) => "ETIMEDOUT".into(),
        Some(n) => format!("E{n}"),
        None => "EIO".into(),
    }
}

// ---- HTTP source (the /v1 mirror) ----------------------------------------------------------------

/// The HTTP server caps request bodies at 1 MiB, so uploads chunk at a comfortable margin below it
/// (`PUT /v1/audio/file/{name}?offset=N&complete=0|1` per chunk — SPEC §7).
const UPLOAD_CHUNK: usize = 512 * 1024;

/// Uses the mod's HTTP `/v1` mirror. `base` is the `/v1` root, e.g. `http://sim:4242/v1`
/// (`$GATOS_HTTP`).
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
        // Newline-terminated like the fs twin, so a multi-line ctl/batch group always carries its
        // final-newline `commit`.
        let payload = format!("{}\n", value.trim_end_matches(['\n', '\r']));
        match self
            .agent
            .post(&format!("{}/fs/{path}", self.base))
            .send_string(&payload)
        {
            Ok(_) => Ok(()),
            Err(ureq::Error::Status(code, resp)) => Err(CmdError {
                errno: http_errno(code),
                message: resp.into_string().unwrap_or_else(|_| "write failed".into()),
            }),
            Err(e) => Err(CmdError {
                errno: "ECONN".into(),
                message: e.to_string(),
            }),
        }
    }

    fn clip_size(&self, clip: &str) -> Option<u64> {
        // GET /v1/audio/files → [{name,bytes,version,ready}] — only a ready clip counts.
        let resp = self.agent.get(&format!("{}/audio/files", self.base)).call().ok()?;
        let list: serde_json::Value = resp.into_json().ok()?;
        list.as_array()?.iter().find_map(|c| {
            (c["name"].as_str() == Some(clip) && c["ready"].as_bool() == Some(true))
                .then(|| c["bytes"].as_u64())
                .flatten()
        })
    }

    fn upload(&self, clip: &str, bytes: &[u8]) -> Result<(), CmdError> {
        // Chunked binary upload: each chunk's offset = bytes sent so far, complete=0 on every
        // chunk but the last (which commits the clip).
        let total = bytes.len();
        let mut offset = 0usize;
        loop {
            let end = (offset + UPLOAD_CHUNK).min(total);
            let complete = end == total;
            let result = self
                .agent
                .put(&format!("{}/audio/file/{clip}", self.base))
                .query("offset", &offset.to_string())
                .query("complete", if complete { "1" } else { "0" })
                .send_bytes(&bytes[offset..end]);
            match result {
                Ok(_) => {}
                Err(ureq::Error::Status(code, resp)) => {
                    return Err(CmdError {
                        errno: http_errno(code),
                        message: resp.into_string().unwrap_or_else(|_| "upload failed".into()),
                    })
                }
                Err(e) => {
                    return Err(CmdError {
                        errno: "ECONN".into(),
                        message: e.to_string(),
                    })
                }
            }
            if complete {
                return Ok(());
            }
            offset = end;
        }
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

/// Maps the HTTP mirror's status codes back to the /sim errno vocabulary (SPEC §4/§7), so the
/// status line reads the same over either transport.
fn http_errno(code: u16) -> String {
    match code {
        400 => "EINVAL".into(),
        403 => "EACCES".into(),
        404 => "ENOENT".into(),
        409 => "EBUSY".into(),
        413 => "EFBIG".into(),
        507 => "ENOSPC".into(),
        _ => format!("HTTP{code}"),
    }
}

// ---- worker channel protocol ---------------------------------------------------------------------

/// A request from the UI thread to the worker.
pub enum ToWorker {
    /// Start one new channel of `clip` (`label` is the display name for the status line). A run of
    /// consecutive `Play`s is dispatched as one `ctl/batch` group — same-tick starts.
    Play { clip: String, label: String },
    /// Stop **every** live channel of `clip` (the `audio/stop <name>` fan-out).
    Stop { clip: String, label: String },
    /// Stop everything (`audio/stop all` — idempotent).
    StopAll,
    /// The soundboard volume 0..1: applied to subsequent plays and live-adjusted onto every
    /// currently-playing clip via `audio/set`.
    SetVolume(f64),
}

/// The registration outcome for one sound.
pub enum RegOutcome {
    /// The clip's bytes were uploaded to the store.
    Uploaded,
    /// A clip of the same name and size was already in the store — nothing to send.
    Cached,
}

/// One poll of the audio surface.
pub struct Poll {
    /// The source is reachable (the `/sim` mount exists / the HTTP server answers).
    pub connected: bool,
    /// The `/sim/audio` surface exists (`audio_enabled=true`).
    pub audio_ok: bool,
    /// Live channel count per sound, aligned to the config's sound order.
    pub counts: Vec<u32>,
    /// Total live channels (any client's — what OMG STOP will stop).
    pub total: u32,
}

/// A reply from the worker to the UI thread.
pub enum FromWorker {
    /// The latest `audio/status` poll.
    Poll(Poll),
    /// Sound `idx` finished registering (uploaded, already cached, or failed).
    Registered {
        idx: usize,
        result: Result<RegOutcome, String>,
    },
    /// A play/stop/volume write completed (status-line message + whether it failed).
    Write { message: String, is_error: bool },
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn play_set_and_batch_lines_match_the_spec_grammar() {
        assert_eq!(play_value("airhorn.mp3", 0.8), "airhorn.mp3 vol=0.80");
        assert_eq!(set_value("airhorn.mp3", 1.0), "airhorn.mp3 vol=1.00");
        let clips = vec!["a.mp3".to_string(), "b.wav".to_string()];
        assert_eq!(
            batch_play_value(&clips, 0.5),
            "audio/play a.mp3 vol=0.50\naudio/play b.wav vol=0.50\ncommit"
        );
    }

    #[test]
    fn status_counts_groups_by_clip() {
        let text = "#1 airhorn.mp3 playing 1200 4000 1 0 sfx\n\
                    #2 airhorn.mp3 playing 300 4000 1 0 sfx\n\
                    bgm music.ogg paused 9000 60000 0.4 1 music\n";
        let (counts, total) = status_counts(text);
        assert_eq!(total, 3);
        assert_eq!(counts.get("airhorn.mp3"), Some(&2));
        assert_eq!(counts.get("music.ogg"), Some(&1)); // paused still counts as active
        // An empty status (no live channels) is zero everywhere.
        let (counts, total) = status_counts("");
        assert!(counts.is_empty());
        assert_eq!(total, 0);
    }

    #[test]
    fn fs_source_reads_writes_and_uploads() {
        let root = std::env::temp_dir().join(format!("the90s_src_test_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("audio/file")).unwrap();
        fs::create_dir_all(root.join("ctl")).unwrap();
        fs::write(root.join("audio/status"), "").unwrap();

        let s = FsSource::new(&root);
        assert_eq!(s.read("audio/status").unwrap(), "");
        assert_eq!(s.read("audio/nope").unwrap_err(), "ENOENT");

        // Upload lands the bytes in the store; clip_size reports them; re-upload replaces.
        assert!(s.clip_size("boom.mp3").is_none());
        s.upload("boom.mp3", b"abc").unwrap();
        assert_eq!(s.clip_size("boom.mp3"), Some(3));
        s.upload("boom.mp3", b"abcdef").unwrap();
        assert_eq!(s.clip_size("boom.mp3"), Some(6));

        // A control write is one newline-terminated payload (multi-line batch preserved).
        s.write("audio/play", "boom.mp3 vol=1.00").unwrap();
        assert_eq!(fs::read_to_string(root.join("audio/play")).unwrap(), "boom.mp3 vol=1.00\n");
        s.write("ctl/batch", "audio/play a vol=1.00\ncommit").unwrap();
        assert_eq!(
            fs::read_to_string(root.join("ctl/batch")).unwrap(),
            "audio/play a vol=1.00\ncommit\n"
        );

        fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn http_errnos_map_to_the_sim_vocabulary() {
        assert_eq!(http_errno(404), "ENOENT");
        assert_eq!(http_errno(413), "EFBIG");
        assert_eq!(http_errno(507), "ENOSPC");
        assert_eq!(http_errno(500), "HTTP500");
    }
}
