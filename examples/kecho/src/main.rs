//! kecho — `tee`, but it fans its writes out to many gatOS `/sim` files **in a single game tick**.
//!
//! ## Why this exists
//!
//! You can't redirect to many files at once. `echo 1 > /sim/vessels/by-id/*/lights/*/on` doesn't do
//! what it looks like: `>` is shell redirection to **one** file descriptor, and when the glob matches
//! more than one file the shell errors with `ambiguous redirect` — the program never even sees the
//! targets. The composable Unix answer is `tee`, which takes the files as *arguments* and the data on
//! *stdin*:
//!
//! ```sh
//! echo 1 | tee /sim/vessels/by-id/*/lights/*/on >/dev/null
//! ```
//!
//! That works, but `tee` writes the files **sequentially**, and a `/sim` write doesn't return until the
//! next game tick (the 9p server enqueues the command and the game thread drains the queue once per
//! frame — the gatOS threading rules). So sequential `tee` over 40 lights ≈ 40 ticks, visibly laggy.
//!
//! kecho is a drop-in concurrent `tee` for that: it reads stdin and writes those bytes to **every** path
//! argument **at once** (one `spawn_blocking` per file on tokio's blocking pool, collected with a
//! `JoinSet`), so all the commands sit in the 9p server's queue together and drain in the **same tick**.
//! 40 lights ≈ 1 tick. It stays composable like `cat`/`tee` — pipes, here-strings (`<<<`), and command
//! substitution all work, and (like `tee`) stdin is echoed to stdout so it can sit mid-pipeline.
//!
//! This is the same fire-and-forget dispatch dancy-party-rs uses; the one difference is that kecho is a
//! short-lived CLI, so it waits for the whole batch (one barrier) before exiting — a true detach would
//! race process exit against threads that hadn't issued their `write()` yet and silently drop writes.
//!
//! ## Usage
//!
//! ```text
//! echo VALUE | kecho [OPTIONS] PATH...
//! ```
//!
//! Whatever is on stdin is written verbatim to every `PATH`. Pipe `echo` for control files — its
//! trailing newline rides along and actuates the file. See `--help`.

mod sink;

use std::io::{Read, Write};
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

/// A parsed command line. stdin is written to every `paths` entry.
struct Config {
    paths: Vec<String>,
    /// Print a one-line summary to stderr when done.
    verbose: bool,
    /// Suppress the `tee`-style passthrough of stdin to stdout (handy when kecho is the last stage and
    /// you don't want the value echoed back).
    quiet: bool,
    /// Cap on simultaneously in-flight writes (`0` = unbounded — the default, and the point: all in one
    /// tick). A positive value throttles via a semaphore, trading single-tick latency for fewer
    /// concurrent fds.
    jobs: usize,
    /// HTTP `/v1` base for the HTTP sink; `None` writes the local `/sim` mount with `std::fs`.
    url: Option<String>,
    help: bool,
}

impl Config {
    /// Parses argv (already skipping the program name). All positionals are target paths; `--` forces
    /// everything after it to be a path, so a path may start with `-`.
    fn parse(args: impl Iterator<Item = String>) -> Result<Self, String> {
        let mut paths: Vec<String> = Vec::new();
        let mut verbose = false;
        let mut quiet = false;
        let mut jobs = 0usize;
        let mut url: Option<String> = None;
        let mut help = false;
        let mut positional_only = false;

        let mut args = args;
        while let Some(arg) = args.next() {
            if positional_only {
                paths.push(arg);
                continue;
            }
            match arg.as_str() {
                "--" => positional_only = true,
                "-v" | "--verbose" => verbose = true,
                "-q" | "--quiet" => quiet = true,
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
                other if other.starts_with('-') && other.len() > 1 => {
                    return Err(format!("unknown option '{other}'"));
                }
                _ => paths.push(arg),
            }
        }

        if help {
            return Ok(Self {
                paths,
                verbose,
                quiet,
                jobs,
                url,
                help: true,
            });
        }
        if paths.is_empty() {
            return Err("need at least one target PATH (usage: echo VALUE | kecho PATH...)".into());
        }
        Ok(Self {
            paths,
            verbose,
            quiet,
            jobs,
            url,
            help: false,
        })
    }
}

fn run(config: Config) -> ExitCode {
    // Read the whole payload from stdin once (control values are tiny; binary-safe like cat/tee).
    let mut data = Vec::new();
    if let Err(e) = std::io::stdin().read_to_end(&mut data) {
        eprintln!("kecho: reading stdin: {e}");
        return ExitCode::from(2);
    }
    // tee-style passthrough so kecho composes mid-pipeline (silence it with -q or `>/dev/null`).
    if !config.quiet {
        let _ = std::io::stdout().write_all(&data);
    }

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
        Arc::new(data),
        config.paths,
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
    data: Arc<Vec<u8>>,
    paths: Vec<String>,
    jobs: usize,
) -> Vec<(String, WriteError)> {
    let sem = (jobs > 0).then(|| Arc::new(Semaphore::new(jobs)));
    let mut set = JoinSet::new();

    for path in paths {
        let sink = sink.clone();
        let data = data.clone();
        // Acquiring before spawn naturally throttles the loop to `jobs` in-flight writes; with no
        // semaphore every path is spawned immediately (the single-tick fan-out).
        let permit = match &sem {
            Some(s) => Some(s.clone().acquire_owned().await.expect("semaphore open")),
            None => None,
        };
        set.spawn_blocking(move || {
            let _permit = permit; // released when this write finishes
            let result = sink.write(&path, &data);
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
    println!("kecho \u{2014} tee one stdin value to many gatOS /sim files, concurrently");
    println!();
    println!("USAGE: echo VALUE | kecho [OPTIONS] PATH...");
    println!();
    println!("Reads stdin and writes it to every PATH at once. You can't redirect to many files");
    println!("(`echo 1 > a b` is an 'ambiguous redirect'); like `tee`, kecho takes the files as");
    println!("arguments and the value on stdin \u{2014} but it fans the writes out concurrently, so they");
    println!("land in ONE game tick instead of one per file the way a sequential `tee` would.");
    println!();
    println!("Pipe `echo` for control files \u{2014} its trailing newline rides along and actuates the file:");
    println!("  echo 1 | kecho /sim/vessels/by-id/*/lights/*/on");
    println!();
    println!("OPTIONS:");
    println!("  -q, --quiet    don't echo stdin back to stdout (tee passthrough is on by default)");
    println!("  -j, --jobs <n> cap simultaneously in-flight writes (default 0 = unbounded, i.e. all");
    println!("                 in one tick). A positive value throttles the fan-out.");
    println!("  --url <base>   write via the HTTP /v1/fs mirror at <base> instead of the filesystem");
    println!("                 (host dev; e.g. $GATOS_HTTP, http://127.0.0.1:4242/v1). No shell");
    println!("                 globbing on the host \u{2014} pass explicit paths.");
    println!("  -v, --verbose  print a one-line summary (count + elapsed) to stderr");
    println!("  --             end options; treat the rest as PATHs (for paths starting with '-')");
    println!("  -h, --help     show this help");
    println!();
    println!("EXAMPLES:");
    println!("  echo 1 | kecho /sim/vessels/by-id/*/lights/*/on        # all lights, every vessel, one tick");
    println!("  echo 0 | kecho /sim/vessels/active/rcs/*/active         # cut every RCS thruster at once");
    println!("  echo '1 0 0' | kecho /sim/vessels/by-id/Hunter/lights/*/color   # paint them red");
    println!("  kecho /sim/.../*/on <<< 1                               # here-string instead of a pipe");
    println!();
    println!("EXIT: 0 = all writes ok, 1 = one or more failed (each printed as");
    println!("      'kecho: <path>: <ERRNO> <message>'), 2 = bad arguments / stdin error.");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn parse(args: &[&str]) -> Result<Config, String> {
        Config::parse(args.iter().map(|s| s.to_string()))
    }

    #[test]
    fn collects_paths() {
        let c = parse(&["/a/on", "/b/on"]).unwrap();
        assert_eq!(c.paths, vec!["/a/on", "/b/on"]);
        assert!(!c.verbose && !c.quiet && c.jobs == 0 && c.url.is_none());
    }

    #[test]
    fn captures_url_jobs_quiet_and_verbose() {
        let c = parse(&["--url", "http://h/v1", "-j", "4", "-q", "-v", "/a/on"]).unwrap();
        assert_eq!(c.url.as_deref(), Some("http://h/v1"));
        assert_eq!(c.jobs, 4);
        assert!(c.quiet && c.verbose);
        assert_eq!(c.paths, vec!["/a/on"]);
    }

    #[test]
    fn double_dash_lets_a_path_start_with_dash() {
        let c = parse(&["--", "-weird-path", "/a/x"]).unwrap();
        assert_eq!(c.paths, vec!["-weird-path", "/a/x"]);
    }

    #[test]
    fn errors_on_unknown_flag_no_paths_and_dangling_option() {
        assert!(parse(&["--nope", "/a"]).is_err());
        assert!(parse(&[]).is_err()); // no paths
        assert!(parse(&["-q"]).is_err()); // option but no paths
        assert!(parse(&["--url"]).is_err()); // dangling option arg
        assert!(parse(&["-j", "x", "/a"]).is_err()); // non-numeric jobs
    }

    #[test]
    fn help_short_circuits_without_requiring_paths() {
        let c = parse(&["--help"]).unwrap();
        assert!(c.help);
    }

    /// The behavior that matters: the stdin payload is written to *every* path, and all of them land.
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
            Arc::new(b"1\n".to_vec()),
            paths.clone(),
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
        let failures = rt.block_on(dispatch(
            Arc::new(FsSink),
            Arc::new(b"x\n".to_vec()),
            paths.clone(),
            3,
        ));
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
            Arc::new(b"1\n".to_vec()),
            vec![good.clone(), bad.clone()],
            0,
        ));
        assert_eq!(failures.len(), 1);
        assert_eq!(failures[0].0, bad);
        assert_eq!(fs::read_to_string(&good).unwrap(), "1\n"); // the good one still wrote
        let _ = fs::remove_dir_all(&d);
    }
}
