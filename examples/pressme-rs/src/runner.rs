//! Running a button's shell command off the UI thread.
//!
//! Each press spawns a short-lived thread (commands are independent — press several and they run
//! concurrently) that runs the command through the platform shell, captures its output + exit
//! status, boils it down to one status line, and sends the [`Outcome`] back over the channel the
//! main loop drains. Nothing here blocks rendering.

use std::process::Command;
use std::sync::mpsc::Sender;
use std::thread;

/// A message from a run thread back to the UI.
pub enum RunMsg {
    /// The command for button `idx` finished.
    Finished { idx: usize, outcome: Outcome },
}

/// The result of one command run, distilled for the one-line button status.
#[derive(Clone, Debug, PartialEq)]
pub struct Outcome {
    /// The process exited 0.
    pub ok: bool,
    /// The exit code, if the process ran (None when the shell couldn't be spawned).
    pub code: Option<i32>,
    /// A short human summary — the first line of output, or the failure reason.
    pub summary: String,
}

/// Spawns a thread that runs `command` and sends its [`Outcome`] back tagged with `idx`.
pub fn spawn(idx: usize, command: String, tx: Sender<RunMsg>) {
    thread::spawn(move || {
        let outcome = run(&command);
        let _ = tx.send(RunMsg::Finished { idx, outcome });
    });
}

/// Runs `command` through the platform shell and captures the outcome.
fn run(command: &str) -> Outcome {
    let (shell, flag) = shell();
    match Command::new(shell).arg(flag).arg(command).output() {
        Ok(out) => {
            let ok = out.status.success();
            let stream = if ok { &out.stdout } else { &out.stderr };
            Outcome {
                ok,
                code: out.status.code(),
                summary: summarize(stream, ok, out.status.code()),
            }
        }
        Err(e) => Outcome {
            ok: false,
            code: None,
            summary: format!("could not run {shell}: {e}"),
        },
    }
}

/// The shell used to run a command: `cmd /C` on Windows, `sh -c` everywhere else (the gatOS guest).
fn shell() -> (&'static str, &'static str) {
    if cfg!(windows) {
        ("cmd", "/C")
    } else {
        ("sh", "-c")
    }
}

/// Distills captured bytes into one status line: the first non-empty output line (trimmed and
/// length-capped), else a bare "ok" / "exit N".
fn summarize(bytes: &[u8], ok: bool, code: Option<i32>) -> String {
    let text = String::from_utf8_lossy(bytes);
    let first = text.lines().map(str::trim).find(|l| !l.is_empty());
    match first {
        Some(line) => truncate(line, 60),
        None if ok => "ok".to_string(),
        None => match code {
            Some(c) => format!("exit {c}"),
            None => "failed".to_string(),
        },
    }
}

/// Truncates `s` to at most `max` chars, appending an ellipsis when it had to cut.
fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        return s.to_string();
    }
    let head: String = s.chars().take(max.saturating_sub(1)).collect();
    format!("{head}\u{2026}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn summarize_prefers_first_nonempty_line() {
        assert_eq!(summarize(b"\n  hello \nworld\n", true, Some(0)), "hello");
        assert_eq!(summarize(b"", true, Some(0)), "ok");
        assert_eq!(summarize(b"", false, Some(3)), "exit 3");
        assert_eq!(summarize(b"", false, None), "failed");
    }

    #[test]
    fn summarize_caps_length() {
        let long = "x".repeat(200);
        let out = summarize(long.as_bytes(), true, Some(0));
        assert_eq!(out.chars().count(), 60);
        assert!(out.ends_with('\u{2026}'));
    }

    #[test]
    fn a_real_command_runs() {
        // Portable across sh/cmd: `echo hi` prints "hi" on both.
        let outcome = run("echo hi");
        assert!(outcome.ok);
        assert_eq!(outcome.summary, "hi");
    }
}
