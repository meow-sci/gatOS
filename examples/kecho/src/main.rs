//! kecho — `echo`, but it writes one value to **many** gatOS `/sim` files **concurrently**.
//!
//! ## Why this exists
//!
//! `/sim` control files actuate the game, but a write doesn't return until the next game tick: the 9p
//! server enqueues the command and the game thread drains the queue once per frame (see the gatOS
//! threading rules). So a *sequential* fan-out —
//!
//! ```sh
//! for f in /sim/vessels/by-id/*/lights/*/on; do echo 1 > "$f"; done
//! ```
//!
//! — pays one whole game tick **per file**: 40 lights ≈ 40 ticks. It isn't that `echo` is slow; it's
//! that the writes are *serialized*, each blocking for its own tick before the next one starts.
//!
//! kecho fixes that by issuing every write **at once**. The shell still does the globbing —
//!
//! ```sh
//! kecho 1 /sim/vessels/by-id/*/lights/*/on
//! ```
//!
//! — but kecho hands every expanded path to the tokio blocking pool simultaneously (`spawn_blocking`
//! per file, collected with a `JoinSet`), so all the commands are sitting in the 9p server's queue
//! together and drain in the **same tick's** `CommandQueue.Drain`. 40 lights ≈ 1 tick. This is the same
//! fire-and-forget dispatch dancy-party-rs uses; the only difference is that kecho is a short-lived CLI,
//! so it waits for the whole batch (one barrier) before exiting — a true detach would race process exit
//! against threads that hadn't issued their `write()` yet and silently drop writes.
//!
//! ## Usage
//!
//! ```text
//! kecho [OPTIONS] VALUE PATH...
//! ```
//!
//! Writes `VALUE` (plus a trailing newline, so control files actuate) to every `PATH`. Quote a value
//! with spaces — vectors and state-vectors are single arguments: `kecho "1 0 0" …/color`. See `--help`.

mod sink;

use std::process::ExitCode;
use std::sync::Arc;
use std::time::Instant;

use tokio::sync::Semaphore;
use tokio::task::JoinSet;

use sink::{FsSink, HttpSink, Sink, WriteError};

fn main() -> ExitCode {
    let config = match Config::parse(std::env::args().skip(1)) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("kecho: {e}");
            eprintln!("Try 'kecho --help'.");
            return ExitCode::from(2);
        }
    };
    if config.help {
        print_help();
        return ExitCode::SUCCESS;
    }
    run(config)
}

/// A parsed command line. `value` is written to every `paths` entry.
struct Config {
    value: String,
    paths: Vec<String>,
    /// Append a trailing `\n` to the value (default true; `-n` turns it off). Control files actuate on
    /// the newline, so you almost never want it off.
    append_newline: bool,
    /// Print a one-line summary to stderr when done.
    verbose: bool,
    /// Cap on simultaneously in-flight writes (`0` = unbounded — the default, and the point: all in one
    /// tick). A positive value throttles via a semaphore, trading single-tick latency for fewer
    /// concurrent fds.
    jobs: usize,
    /// HTTP `/v1` base for the HTTP sink; `None` writes the local `/sim` mount with `std::fs`.
    url: Option<String>,
    help: bool,
}

impl Config {
    /// Parses argv (already skipping the program name). Options precede positionals; the first
    /// positional is the value and the rest are target paths. `--` forces everything after it to be a
    /// positional, so a value or path may start with `-` (`kecho -- -1 …/x`).
    fn parse(args: impl Iterator<Item = String>) -> Result<Self, String> {
        let mut value: Option<String> = None;
        let mut paths: Vec<String> = Vec::new();
        let mut append_newline = true;
        let mut verbose = false;
        let mut jobs = 0usize;
        let mut url: Option<String> = None;
        let mut help = false;
        let mut positional_only = false;

        let push = |v: String, value: &mut Option<String>, paths: &mut Vec<String>| {
            if value.is_none() {
                *value = Some(v);
            } else {
                paths.push(v);
            }
        };

        let mut args = args;
        while let Some(arg) = args.next() {
            if positional_only {
                push(arg, &mut value, &mut paths);
                continue;
            }
            match arg.as_str() {
                "--" => positional_only = true,
                "-n" => append_newline = false,
                "-v" | "--verbose" => verbose = true,
                "-h" | "--help" => help = true,
                "--url" => {
                    url = Some(args.next().ok_or("--url wants a base URL (e.g. $GATOS_HTTP)")?)
                }
                "-j" | "--jobs" => {
                    let n = args.next().ok_or("--jobs wants a number (0 = unbounded)")?;
                    jobs = n
                        .parse::<usize>()
                        .map_err(|_| format!("--jobs wants a number, got '{n}'"))?;
                }
                // An unknown `-x` flag is an error; a bare `-` (stdin convention) or a negative-looking
                // token is treated as a positional so values like `-1` work without `--`.
                other if other.starts_with('-') && other.len() > 1 && !looks_numeric(other) => {
                    return Err(format!("unknown option '{other}'"));
                }
                _ => push(arg, &mut value, &mut paths),
            }
        }

        if help {
            return Ok(Self {
                value: String::new(),
                paths,
                append_newline,
                verbose,
                jobs,
                url,
                help: true,
            });
        }

        let value = value.ok_or("missing VALUE (usage: kecho [OPTIONS] VALUE PATH...)")?;
        if paths.is_empty() {
            return Err("need at least one target PATH to write to".into());
        }
        Ok(Self {
            value,
            paths,
            append_newline,
            verbose,
            jobs,
            url,
            help: false,
        })
    }
}

/// Whether a leading-`-` token is actually a number (`-1`, `-1.5`) rather than a flag, so it falls
/// through to being a positional value.
fn looks_numeric(s: &str) -> bool {
    s.parse::<f64>().is_ok()
}

fn run(config: Config) -> ExitCode {
    let sink: Arc<dyn Sink> = match &config.url {
        Some(u) => Arc::new(HttpSink::new(u.clone())),
        None => Arc::new(FsSink),
    };
    let label = sink.label();

    // A current-thread runtime is enough: every write runs on the *blocking* pool (multi-threaded, up
    // to 512 by default), so the writes are concurrent regardless of the scheduler flavor.
    let rt = tokio::runtime::Builder::new_current_thread()
        .build()
        .expect("build kecho runtime");

    let total = config.paths.len();
    let start = Instant::now();
    let failures = rt.block_on(dispatch(
        sink,
        &config.value,
        config.paths,
        config.append_newline,
        config.jobs,
    ));
    let elapsed = start.elapsed();

    for (path, e) in &failures {
        eprintln!("kecho: {path}: {} {}", e.errno, e.message);
    }
    if config.verbose {
        eprintln!(
            "kecho: wrote {}/{} target(s) via {label} in {:.1?}",
            total - failures.len(),
            total,
            elapsed
        );
    }

    if failures.is_empty() {
        ExitCode::SUCCESS
    } else {
        ExitCode::from(1)
    }
}

/// Fires one write per path, all concurrently, and returns the failures (path + error). This is the
/// whole trick: each `spawn_blocking` issues its blocking `write()` on the tokio blocking pool, so the
/// per-tick-blocking writes overlap and their commands batch into one game tick instead of serializing
/// one-per-tick. With `jobs == 0` everything is dispatched at once; a positive `jobs` bounds the number
/// in flight via a semaphore (the permit is held until that write completes).
async fn dispatch(
    sink: Arc<dyn Sink>,
    value: &str,
    paths: Vec<String>,
    append_newline: bool,
    jobs: usize,
) -> Vec<(String, WriteError)> {
    let value = Arc::new(value.to_string());
    let sem = (jobs > 0).then(|| Arc::new(Semaphore::new(jobs)));
    let mut set = JoinSet::new();

    for path in paths {
        let sink = sink.clone();
        let value = value.clone();
        // Acquiring before spawn naturally throttles the loop to `jobs` in-flight writes; with no
        // semaphore every path is spawned immediately (the single-tick fan-out).
        let permit = match &sem {
            Some(s) => Some(s.clone().acquire_owned().await.expect("semaphore open")),
            None => None,
        };
        set.spawn_blocking(move || {
            let _permit = permit; // released when this write finishes
            let result = sink.write(&path, &value, append_newline);
            (path, result)
        });
    }

    let mut failures = Vec::new();
    while let Some(joined) = set.join_next().await {
        match joined {
            Ok((path, Err(e))) => failures.push((path, e)),
            Ok((_, Ok(()))) => {}
            // A panic inside a write task: surface it as a synthetic failure rather than aborting the
            // rest (every other write still completes).
            Err(e) => failures.push((
                "<task>".to_string(),
                WriteError {
                    errno: "EIO".into(),
                    message: format!("write task failed: {e}"),
                },
            )),
        }
    }
    failures
}

fn print_help() {
    println!("kecho \u{2014} echo one value to many gatOS /sim files, concurrently");
    println!();
    println!("USAGE: kecho [OPTIONS] VALUE PATH...");
    println!();
    println!("Writes VALUE to every PATH at once. Where a sequential shell loop pays one game tick");
    println!("per file (each /sim write blocks until the next tick), kecho fires every write");
    println!("simultaneously so they land in a single tick. Quote a value with spaces:");
    println!("  kecho \"1 0 0\" /sim/vessels/by-id/Hunter/lights/*/color");
    println!();
    println!("OPTIONS:");
    println!("  -n             do not append a trailing newline (control files actuate on the");
    println!("                 newline, so you rarely want this)");
    println!("  -j, --jobs <n> cap simultaneously in-flight writes (default 0 = unbounded, i.e. all");
    println!("                 in one tick). A positive value throttles the fan-out.");
    println!("  --url <base>   write via the HTTP /v1/fs mirror at <base> instead of the filesystem");
    println!("                 (host dev; e.g. $GATOS_HTTP, http://127.0.0.1:4242/v1). No shell");
    println!("                 globbing on the host \u{2014} pass explicit paths.");
    println!("  -v, --verbose  print a one-line summary (count + elapsed) to stderr");
    println!("  --             end options; treat the rest as VALUE then PATHs (for values/paths");
    println!("                 that start with '-')");
    println!("  -h, --help     show this help");
    println!();
    println!("EXAMPLES:");
    println!("  kecho 1 /sim/vessels/by-id/*/lights/*/on       # all lights, every vessel, one tick");
    println!("  kecho 0 /sim/vessels/active/rcs/*/active        # cut every RCS thruster at once");
    println!("  kecho \"1 0 0\" /sim/vessels/by-id/Hunter/lights/*/color   # paint them red");
    println!();
    println!("EXIT: 0 = all writes ok, 1 = one or more failed (each printed as");
    println!("      'kecho: <path>: <ERRNO> <message>'), 2 = bad arguments.");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn parse(args: &[&str]) -> Result<Config, String> {
        Config::parse(args.iter().map(|s| s.to_string()))
    }

    #[test]
    fn splits_value_from_paths() {
        let c = parse(&["1", "/a/on", "/b/on"]).unwrap();
        assert_eq!(c.value, "1");
        assert_eq!(c.paths, vec!["/a/on", "/b/on"]);
        assert!(c.append_newline && !c.verbose && c.jobs == 0 && c.url.is_none());
    }

    #[test]
    fn n_flag_suppresses_newline() {
        let c = parse(&["-n", "1", "/a/on"]).unwrap();
        assert!(!c.append_newline);
        assert_eq!(c.value, "1");
    }

    #[test]
    fn captures_url_jobs_and_verbose() {
        let c = parse(&["--url", "http://h/v1", "-j", "4", "-v", "1", "/a/on"]).unwrap();
        assert_eq!(c.url.as_deref(), Some("http://h/v1"));
        assert_eq!(c.jobs, 4);
        assert!(c.verbose);
    }

    #[test]
    fn double_dash_lets_value_start_with_dash() {
        let c = parse(&["--", "-1", "/a/x"]).unwrap();
        assert_eq!(c.value, "-1");
        assert_eq!(c.paths, vec!["/a/x"]);
    }

    #[test]
    fn negative_number_value_needs_no_double_dash() {
        // `-1` looks numeric, so it's a positional, not an unknown flag.
        let c = parse(&["-1.5", "/a/x"]).unwrap();
        assert_eq!(c.value, "-1.5");
    }

    #[test]
    fn errors_on_unknown_flag_missing_value_and_no_paths() {
        assert!(parse(&["--nope", "1", "/a"]).is_err());
        assert!(parse(&[]).is_err()); // no value
        assert!(parse(&["1"]).is_err()); // value but no paths
        assert!(parse(&["--url"]).is_err()); // dangling option arg
        assert!(parse(&["-j", "x", "1", "/a"]).is_err()); // non-numeric jobs
    }

    #[test]
    fn help_short_circuits_without_requiring_value() {
        let c = parse(&["--help"]).unwrap();
        assert!(c.help);
    }

    /// The behavior that matters: a fan-out writes the value to *every* path, and all of them land.
    #[test]
    fn dispatch_writes_every_target() {
        let d = std::env::temp_dir().join(format!("kecho_disp_{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        let paths: Vec<String> = (0..50)
            .map(|i| d.join(format!("f{i}")).to_str().unwrap().to_string())
            .collect();

        let rt = tokio::runtime::Builder::new_current_thread()
            .build()
            .unwrap();
        let failures = rt.block_on(dispatch(
            Arc::new(FsSink),
            "1",
            paths.clone(),
            true,
            0,
        ));
        assert!(failures.is_empty(), "no write should fail: {failures:?}");
        for p in &paths {
            assert_eq!(fs::read_to_string(p).unwrap(), "1\n");
        }
        let _ = fs::remove_dir_all(&d);
    }

    /// A bounded `--jobs` still writes every target (just fewer at a time).
    #[test]
    fn dispatch_is_complete_under_a_jobs_cap() {
        let d = std::env::temp_dir().join(format!("kecho_jobs_{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        let paths: Vec<String> = (0..20)
            .map(|i| d.join(format!("f{i}")).to_str().unwrap().to_string())
            .collect();

        let rt = tokio::runtime::Builder::new_current_thread()
            .build()
            .unwrap();
        let failures = rt.block_on(dispatch(Arc::new(FsSink), "x", paths.clone(), true, 3));
        assert!(failures.is_empty());
        assert_eq!(fs::read_to_string(&paths[19]).unwrap(), "x\n");
        let _ = fs::remove_dir_all(&d);
    }

    /// Failures are reported per-path, and the good writes in the same batch still land.
    #[test]
    fn dispatch_reports_failures_without_aborting_the_batch() {
        let d = std::env::temp_dir().join(format!("kecho_fail_{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        let good = d.join("ok").to_str().unwrap().to_string();
        let bad = "/no/such/kecho/dir/x".to_string();

        let rt = tokio::runtime::Builder::new_current_thread()
            .build()
            .unwrap();
        let failures = rt.block_on(dispatch(
            Arc::new(FsSink),
            "1",
            vec![good.clone(), bad.clone()],
            true,
            0,
        ));
        assert_eq!(failures.len(), 1);
        assert_eq!(failures[0].0, bad);
        assert_eq!(fs::read_to_string(&good).unwrap(), "1\n"); // the good one still wrote
        let _ = fs::remove_dir_all(&d);
    }

    #[test]
    fn looks_numeric_distinguishes_values_from_flags() {
        assert!(looks_numeric("-1"));
        assert!(looks_numeric("-1.5"));
        assert!(looks_numeric("42"));
        assert!(!looks_numeric("-n"));
        assert!(!looks_numeric("--url"));
    }
}
