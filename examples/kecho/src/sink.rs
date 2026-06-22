//! Where a write actually goes. Two backends mirror the sibling Rust examples:
//!
//! - [`FsSink`] writes the **real `/sim` mount** with `std::fs`: a write is one `open`+`write`+`close`
//!   of the exact bytes from stdin (so `echo 1 | kecho …` delivers `1\n`, and the trailing newline the
//!   9p control files actuate on rides along for free — `kecho` itself never touches the payload).
//! - [`HttpSink`] uses the mod's HTTP `/v1/fs/<path>` field mirror (the `--url`/`$GATOS_HTTP` host-dev
//!   mode). The field endpoint actuates on receipt and a stray trailing newline can confuse a strict
//!   parser, so the body is sent with trailing `\n`/`\r` trimmed.
//!
//! Neither sink batches or holds state — a sink is a stateless "write these bytes to that path"
//! function. The *concurrency* (and therefore the single-tick fan-out that is the whole point of
//! kecho) lives one layer up, in `main::dispatch`, which fires one `spawn_blocking(sink.write(..))`
//! per target file. The trait is `Send + Sync` precisely so it can be shared across those tasks.

use std::fs;
use std::time::Duration;

/// The outcome of a failed write — an errno-ish tag + message. The tag is the frozen gatOS
/// control-file errno vocabulary (`EINVAL`, `EACCES`, `EBUSY`, `ENOENT`, …) so failures read the same
/// whether they came back from the `/sim` mount or the HTTP mirror.
#[derive(Debug, Clone)]
pub struct WriteError {
    pub errno: String,
    pub message: String,
}

/// A "write these bytes to one path" backend. Stateless and `Send + Sync` so `main::dispatch` can
/// clone an `Arc<dyn Sink>` into every concurrent write task.
pub trait Sink: Send + Sync {
    /// Writes `data` to `path` as one actuating write (the bytes are the verbatim stdin payload —
    /// binary-safe, like `cat`/`tee`).
    fn write(&self, path: &str, data: &[u8]) -> Result<(), WriteError>;

    /// A short label for the `--verbose` summary (e.g. `fs` or the HTTP base URL).
    fn label(&self) -> String;
}

// ---- filesystem sink (the real /sim mount) ------------------------------------------------------

/// Writes paths **verbatim** with `std::fs`. The expected use is in-guest with the shell having already
/// glob-expanded absolute `/sim/...` paths (`echo 1 | kecho /sim/vessels/by-id/*/lights/*/on`);
/// relative paths resolve against the process cwd, exactly like `tee path` would.
pub struct FsSink;

impl Sink for FsSink {
    fn write(&self, path: &str, data: &[u8]) -> Result<(), WriteError> {
        // One write replaces the file's contents — the gatOS control files are single-value, so this is
        // the `echo value > file` shape, not an append. `data` is passed through untouched.
        fs::write(path, data).map_err(|e| WriteError {
            errno: errno_name(e.raw_os_error()),
            message: e.to_string(),
        })
    }

    fn label(&self) -> String {
        "fs".into()
    }
}

// ---- HTTP sink (the /v1/fs mirror) --------------------------------------------------------------

/// Uses the mod's HTTP `/v1/fs/<path>` field mirror. `base` is the `/v1` root, e.g.
/// `http://127.0.0.1:4242/v1` (`$GATOS_HTTP`). No shell globbing exists on the host, so paths must be
/// passed explicitly; each is reduced to its `/sim`-relative form by [`http_rel`] before the POST.
pub struct HttpSink {
    base: String,
    agent: ureq::Agent,
}

impl HttpSink {
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

impl Sink for HttpSink {
    fn write(&self, path: &str, data: &[u8]) -> Result<(), WriteError> {
        // The field endpoint actuates on receipt; trim a trailing newline so a piped `echo`'s `\n`
        // doesn't reach a strict value parser (the FS mount, by contrast, wants that newline).
        let rel = http_rel(path);
        match self
            .agent
            .post(&format!("{}/fs/{rel}", self.base))
            .send_bytes(trim_trailing_newline(data))
        {
            Ok(_) => Ok(()),
            Err(ureq::Error::Status(code, resp)) => Err(WriteError {
                errno: errno_from_status(code),
                message: resp
                    .into_string()
                    .unwrap_or_else(|_| format!("HTTP {code}")),
            }),
            Err(e) => Err(WriteError {
                errno: "ECONN".into(),
                message: e.to_string(),
            }),
        }
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

/// Reduces a path to the `/sim`-relative form the `/v1/fs/<path>` endpoint expects: drop leading
/// slashes, then a leading `sim/` segment. So `/sim/vessels/active/ctl/throttle`, `sim/…` and the
/// already-relative `vessels/…` all map to the same `vessels/…`.
pub fn http_rel(path: &str) -> String {
    let p = path.trim_start_matches('/');
    p.strip_prefix("sim/").unwrap_or(p).to_string()
}

/// Returns `data` with any trailing `\n`/`\r` bytes removed (for the HTTP body).
fn trim_trailing_newline(data: &[u8]) -> &[u8] {
    let mut end = data.len();
    while end > 0 && (data[end - 1] == b'\n' || data[end - 1] == b'\r') {
        end -= 1;
    }
    &data[..end]
}

/// Maps the HTTP field-endpoint status code to the matching control-file errno name, so an HTTP
/// failure reads the same as the FS one.
fn errno_from_status(code: u16) -> String {
    match code {
        400 => "EINVAL".into(),
        403 => "EACCES".into(),
        404 => "ENOENT".into(),
        409 => "EBUSY".into(),
        _ => format!("HTTP{code}"),
    }
}

/// Maps a raw OS errno (Linux) to its name for compact display — the same small table the other
/// examples use.
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

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp_dir(tag: &str) -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("kecho_{tag}_{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn fs_write_is_verbatim() {
        // kecho never edits the payload: whatever stdin held (e.g. `echo`'s `1\n`) lands byte-for-byte.
        let d = tmp_dir("verbatim");
        let f = d.join("on");
        FsSink.write(f.to_str().unwrap(), b"1\n").unwrap();
        assert_eq!(fs::read_to_string(&f).unwrap(), "1\n");
        let _ = fs::remove_dir_all(&d);
    }

    #[test]
    fn fs_write_overwrites_rather_than_appends() {
        let d = tmp_dir("overwrite");
        let f = d.join("color");
        FsSink.write(f.to_str().unwrap(), b"1 0 0\n").unwrap();
        FsSink.write(f.to_str().unwrap(), b"0 0 1\n").unwrap();
        assert_eq!(fs::read_to_string(&f).unwrap(), "0 0 1\n");
        let _ = fs::remove_dir_all(&d);
    }

    #[test]
    fn fs_write_missing_dir_is_an_error() {
        // A missing parent directory fails. The *name* of the errno is OS-specific (ENOENT in the
        // Linux guest, a different raw code on a Windows dev host), so we only assert it failed with a
        // non-empty tag; the exact Linux mapping is covered by `errno_table_names_the_common_codes`.
        let err = FsSink.write("/no/such/kecho/dir/on", b"1\n").unwrap_err();
        assert!(!err.errno.is_empty());
    }

    #[test]
    fn http_rel_strips_leading_slash_and_sim() {
        assert_eq!(http_rel("/sim/vessels/active/ctl/throttle"), "vessels/active/ctl/throttle");
        assert_eq!(http_rel("sim/vessels/active/id"), "vessels/active/id");
        assert_eq!(http_rel("/vessels/active/id"), "vessels/active/id");
        assert_eq!(http_rel("vessels/active/id"), "vessels/active/id");
        // A path that merely *starts* with "sim" but isn't the segment is left intact.
        assert_eq!(http_rel("/simulator/x"), "simulator/x");
    }

    #[test]
    fn trim_trailing_newline_drops_cr_and_lf() {
        assert_eq!(trim_trailing_newline(b"1\n"), b"1");
        assert_eq!(trim_trailing_newline(b"1 0 0\r\n"), b"1 0 0");
        assert_eq!(trim_trailing_newline(b"1"), b"1");
        assert_eq!(trim_trailing_newline(b""), b"");
    }

    #[test]
    fn errno_table_names_the_common_codes() {
        assert_eq!(errno_name(Some(13)), "EACCES");
        assert_eq!(errno_name(Some(2)), "ENOENT");
        assert_eq!(errno_name(Some(22)), "EINVAL");
        assert_eq!(errno_name(Some(999)), "E999");
        assert_eq!(errno_name(None), "EIO");
        assert_eq!(errno_from_status(403), "EACCES");
        assert_eq!(errno_from_status(409), "EBUSY");
    }
}
