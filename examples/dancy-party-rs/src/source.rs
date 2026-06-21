//! The data layer: read/write `/sim` light fields by path, discover the lights once, and run the
//! party. Two backends mirror the sibling examples:
//!
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): discovery is
//!   a directory walk; a color/goal write is one `echo value > file`.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url`/`$GATOS_HTTP` dev mode).
//!   It has no directory listing, so discovery *probes* `lights/<n>/…` per vessel until the ordinals
//!   run out.
//!
//! Discovery runs **once** (the `/sim` light tree is assumed stable for the program's life — re-reading
//! the 9p tree has a real cost), and the resulting per-vessel path lists are cached on the worker. The
//! worker thread owns the source: when idle it blocks for a command; while partying it runs the
//! [`crate::party`] frame loop at `--hz`, broadcasting each frame's color/goal to every selected
//! light, so the UI thread never touches I/O.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::color::Rgb;
use crate::party::Plan;

/// The outcome of a failed write — an errno-ish tag + message (the frozen control-file errno
/// vocabulary: `EINVAL`, `EACCES`, `EBUSY`, …), surfaced on the status line.
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// Integration health read from the source — drives the header badges and explains write failures.
#[derive(Clone, Copy, Default, Debug)]
pub struct Health {
    pub connected: bool,
    pub control: bool,
}

/// One vessel's discovered light wiring: every `lights/<n>/color` path and every `lights/<n>/goal`
/// path it has. The worker keeps these; a party over a selection just concatenates the chosen
/// vessels' lists. `name` is the display id; `id` is the sanitized `/sim` path segment.
#[derive(Clone, Debug)]
pub struct VesselLights {
    pub id: String,
    pub color_paths: Vec<String>,
    pub goal_paths: Vec<String>,
}

impl VesselLights {
    pub fn light_count(&self) -> usize {
        self.color_paths.len()
    }
}

/// A read/write/discover interface over the `/sim` light surface. Discovery/health run on the worker
/// thread; `write` may also be called from the async writer-pool threads (`--async`), so the trait is
/// `Send + Sync`.
pub trait Source: Send + Sync {
    /// Writes `value` to a field as one newline-terminated write (the `echo value > file` shape), so a
    /// control file actuates and a failure carries the real errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// Reads integration health (connection + control gating).
    fn health(&self) -> Health;

    /// Walks/probes the `/sim` vessel tree once and returns every vessel's light color/goal paths.
    fn discover(&self) -> Vec<VesselLights>;

    /// A short label for the header (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;
}

/// Sanitizes a vessel id into a `/sim` path segment (non-`[A-Za-z0-9._-]` → `_`), matching the
/// server's own sanitization so HTTP-discovered ids address the right paths.
pub fn sanitize_segment(id: &str) -> String {
    id.chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') {
                c
            } else {
                '_'
            }
        })
        .collect()
}

// ---- filesystem source (the real /sim mount) ----------------------------------------------------

/// Reads the `/sim` mount directly via `std::fs`. `root` defaults to `/sim` in the guest, but can
/// point at any directory (a `/sim`-shaped fixture for host-side dev).
pub struct FsSource {
    root: PathBuf,
}

impl FsSource {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    fn entries(&self, rel: &str) -> Vec<(String, bool)> {
        let mut out = Vec::new();
        if let Ok(rd) = fs::read_dir(self.root.join(rel)) {
            for e in rd.flatten() {
                let name = e.file_name().to_string_lossy().into_owned();
                let is_dir = e.file_type().map(|t| t.is_dir()).unwrap_or(false);
                out.push((name, is_dir));
            }
        }
        out.sort();
        out
    }
}

impl Source for FsSource {
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError> {
        let payload = format!("{}\n", value.trim_end_matches(['\n', '\r']));
        fs::write(self.root.join(path), payload).map_err(|e| CmdError {
            errno: errno_name(e.raw_os_error()),
            message: e.to_string(),
        })
    }

    fn health(&self) -> Health {
        Health {
            connected: self.root.join("vessels").is_dir() || self.root.join("time").is_dir(),
            control: self.root.join("status").is_dir(),
        }
    }

    fn discover(&self) -> Vec<VesselLights> {
        let mut out = Vec::new();
        for (vid, is_dir) in self.entries("vessels/by-id") {
            if !is_dir {
                continue;
            }
            let lights_dir = format!("vessels/by-id/{vid}/lights");
            let mut color_paths = Vec::new();
            let mut goal_paths = Vec::new();
            for (ord, ord_is_dir) in self.entries(&lights_dir) {
                if !ord_is_dir {
                    continue;
                }
                let files: Vec<String> = self
                    .entries(&format!("{lights_dir}/{ord}"))
                    .into_iter()
                    .filter(|(_, d)| !*d)
                    .map(|(n, _)| n)
                    .collect();
                if files.iter().any(|f| f == "color") {
                    color_paths.push(format!("{lights_dir}/{ord}/color"));
                }
                if files.iter().any(|f| f == "goal") {
                    goal_paths.push(format!("{lights_dir}/{ord}/goal"));
                }
            }
            out.push(VesselLights {
                id: vid,
                color_paths,
                goal_paths,
            });
        }
        out.sort_by(|a, b| a.id.cmp(&b.id));
        out
    }

    fn label(&self) -> String {
        format!("fs:{}", self.root.display())
    }
}

// ---- HTTP source (the /v1/fs mirror) ------------------------------------------------------------

/// Uses the mod's HTTP `/v1/fs/<path>` field mirror (and `/v1/vessels` for discovery). `base` is the
/// `/v1` root, e.g. `http://127.0.0.1:4242/v1` (`$GATOS_HTTP`).
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

    fn read(&self, path: &str) -> Result<String, ()> {
        match self.agent.get(&format!("{}/fs/{path}", self.base)).call() {
            Ok(resp) => resp.into_string().map_err(|_| ()),
            Err(_) => Err(()),
        }
    }

    fn get_json(&self, path: &str) -> Option<serde_json::Value> {
        self.agent
            .get(&format!("{}/{path}", self.base))
            .call()
            .ok()?
            .into_json()
            .ok()
    }
}

/// How many consecutive missing light ordinals end the per-vessel HTTP probe, and the hard cap on
/// ordinals tried — lights are densely numbered from 0, so a short gap means we're past the end.
const HTTP_PROBE_MISS_LIMIT: u32 = 6;
const HTTP_PROBE_MAX: u32 = 256;

impl Source for HttpSource {
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError> {
        match self
            .agent
            .post(&format!("{}/fs/{path}", self.base))
            .send_string(value)
        {
            Ok(_) => Ok(()),
            Err(ureq::Error::Status(code, resp)) => Err(CmdError {
                errno: errno_from_body(&resp),
                message: resp
                    .into_string()
                    .unwrap_or_else(|_| format!("HTTP {code}")),
            }),
            Err(e) => Err(CmdError {
                errno: "ECONN".into(),
                message: e.to_string(),
            }),
        }
    }

    fn health(&self) -> Health {
        match self.get_json("status") {
            Some(v) => Health {
                connected: true,
                control: v.get("control").and_then(|c| c.as_bool()).unwrap_or(false),
            },
            None => Health::default(),
        }
    }

    fn discover(&self) -> Vec<VesselLights> {
        let ids: Vec<String> = self
            .get_json("vessels")
            .and_then(|v| {
                v.as_array().map(|a| {
                    a.iter()
                        .filter_map(|e| e.as_str())
                        .map(sanitize_segment)
                        .collect()
                })
            })
            .unwrap_or_default();

        let mut out = Vec::new();
        for vid in ids {
            let mut color_paths = Vec::new();
            let mut goal_paths = Vec::new();
            let mut misses = 0;
            for n in 0..HTTP_PROBE_MAX {
                let color = format!("vessels/by-id/{vid}/lights/{n}/color");
                if self.read(&color).is_ok() {
                    color_paths.push(color);
                    let goal = format!("vessels/by-id/{vid}/lights/{n}/goal");
                    if self.read(&goal).is_ok() {
                        goal_paths.push(goal);
                    }
                    misses = 0;
                } else {
                    misses += 1;
                    if misses >= HTTP_PROBE_MISS_LIMIT {
                        break;
                    }
                }
            }
            out.push(VesselLights {
                id: vid,
                color_paths,
                goal_paths,
            });
        }
        out.sort_by(|a, b| a.id.cmp(&b.id));
        out
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

fn errno_from_body(resp: &ureq::Response) -> String {
    // The field endpoints return `{errno,message}`; fall back to the status text.
    let status = resp.status();
    match status {
        403 => "EACCES".into(),
        404 => "ENOENT".into(),
        409 => "EBUSY".into(),
        400 => "EINVAL".into(),
        _ => format!("HTTP{status}"),
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

// ---- write tuning + stats (the perf-experiment knobs) -------------------------------------------

/// How the party broadcasts each frame's writes — the `--async`/`--writers` perf experiment.
#[derive(Clone, Copy, Debug)]
pub struct WriteConfig {
    /// `false` (default): write every light synchronously on the animation thread, blocking on each
    /// (so a slow 9p write directly stalls the fade). `true`: hand writes to a pool of `writers`
    /// background threads and never block the animation loop.
    pub async_writes: bool,
    /// Number of background writer threads when `async_writes` is set (ignored otherwise).
    pub writers: usize,
}

impl Default for WriteConfig {
    fn default() -> Self {
        Self {
            async_writes: false,
            writers: 8,
        }
    }
}

/// A point-in-time view of the write pipeline's behaviour, pushed to the UI each tick so the operator
/// can read off the actual cost of a `/sim` light write and whether the loop is keeping up.
#[derive(Clone, Copy, Default, Debug)]
pub struct WriteSnapshot {
    /// Writes issued since the party started (color + goal, across all lights).
    pub writes: u64,
    /// Mean / max / most-recent single-write latency, in microseconds.
    pub avg_us: u64,
    pub max_us: u64,
    pub last_us: u64,
    /// Writes still queued/in-flight on the async pool (always 0 in sync mode).
    pub inflight: usize,
    /// Color broadcasts skipped because the async pool was still draining the previous one — the
    /// "can't keep up" signal. Always 0 in sync mode.
    pub dropped: u64,
    /// Whether the async pool is in use, and its width.
    pub async_writes: bool,
    pub writers: usize,
}

/// Shared, lock-light accumulator the sync path and every pool thread update as writes complete.
#[derive(Default)]
struct WriteStats {
    inflight: AtomicUsize,
    count: AtomicU64,
    total_us: AtomicU64,
    max_us: AtomicU64,
    last_us: AtomicU64,
    err: Mutex<Option<String>>,
}

impl WriteStats {
    fn record(&self, us: u64, err: Option<String>) {
        self.count.fetch_add(1, Ordering::Relaxed);
        self.total_us.fetch_add(us, Ordering::Relaxed);
        self.max_us.fetch_max(us, Ordering::Relaxed);
        self.last_us.store(us, Ordering::Relaxed);
        if let Some(e) = err {
            let mut slot = self.err.lock().unwrap();
            if slot.is_none() {
                *slot = Some(e);
            }
        }
    }

    /// Resets the counters at the start of a run so each party measures fresh (in-flight is left
    /// alone — the previous run's pool is drained before a new one starts).
    fn reset(&self) {
        self.count.store(0, Ordering::Relaxed);
        self.total_us.store(0, Ordering::Relaxed);
        self.max_us.store(0, Ordering::Relaxed);
        self.last_us.store(0, Ordering::Relaxed);
        *self.err.lock().unwrap() = None;
    }

    fn take_error(&self) -> Option<String> {
        self.err.lock().unwrap().take()
    }

    fn snapshot(&self, cfg: WriteConfig, dropped: u64) -> WriteSnapshot {
        let count = self.count.load(Ordering::Relaxed);
        let total = self.total_us.load(Ordering::Relaxed);
        WriteSnapshot {
            writes: count,
            avg_us: total.checked_div(count).unwrap_or(0),
            max_us: self.max_us.load(Ordering::Relaxed),
            last_us: self.last_us.load(Ordering::Relaxed),
            inflight: self.inflight.load(Ordering::Relaxed),
            dropped,
            async_writes: cfg.async_writes,
            writers: cfg.writers,
        }
    }
}

/// The write fan-out for a frame. `Sync` writes inline on the animation thread; `Pool` round-robins
/// jobs across background threads. Both feed the same [`WriteStats`], so latency is measured either
/// way. Built once per worker and reused across parties.
enum Broadcaster {
    Sync {
        source: Arc<dyn Source>,
        stats: Arc<WriteStats>,
    },
    Pool {
        txs: Vec<Sender<(String, String)>>,
        next: usize,
        stats: Arc<WriteStats>,
    },
}

impl Broadcaster {
    fn build(source: Arc<dyn Source>, cfg: WriteConfig) -> Self {
        let stats = Arc::new(WriteStats::default());
        if !cfg.async_writes {
            return Broadcaster::Sync { source, stats };
        }
        let mut txs = Vec::new();
        for _ in 0..cfg.writers.max(1) {
            let (tx, rx) = mpsc::channel::<(String, String)>();
            txs.push(tx);
            let src = source.clone();
            let st = stats.clone();
            std::thread::spawn(move || {
                // Ends when its sender drops (worker exit). Each job writes one field and records its
                // own latency; the in-flight count drops as the queue drains.
                for (path, value) in rx {
                    let t0 = Instant::now();
                    let err = src.write(&path, &value).err().map(|e| format!("{}: {}", e.errno, e.message));
                    st.record(t0.elapsed().as_micros() as u64, err);
                    st.inflight.fetch_sub(1, Ordering::Relaxed);
                }
            });
        }
        Broadcaster::Pool {
            txs,
            next: 0,
            stats,
        }
    }

    fn stats(&self) -> &Arc<WriteStats> {
        match self {
            Broadcaster::Sync { stats, .. } | Broadcaster::Pool { stats, .. } => stats,
        }
    }

    fn inflight(&self) -> usize {
        self.stats().inflight.load(Ordering::Relaxed)
    }

    /// Issues one field write. Sync mode blocks (and times) the write here; pool mode enqueues it and
    /// returns immediately, the chosen thread timing it as it drains.
    fn send(&mut self, path: &str, value: &str) {
        match self {
            Broadcaster::Sync { source, stats } => {
                let t0 = Instant::now();
                let err = source.write(path, value).err().map(|e| format!("{}: {}", e.errno, e.message));
                stats.record(t0.elapsed().as_micros() as u64, err);
            }
            Broadcaster::Pool { txs, next, stats } => {
                stats.inflight.fetch_add(1, Ordering::Relaxed);
                let i = *next;
                *next = (*next + 1) % txs.len();
                if txs[i].send((path.to_string(), value.to_string())).is_err() {
                    stats.inflight.fetch_sub(1, Ordering::Relaxed);
                }
            }
        }
    }

    /// Blocks until the pool has drained every queued write (no-op in sync mode). Bounded so a wedged
    /// write can't hang shutdown.
    fn drain(&self) {
        if let Broadcaster::Sync { .. } = self {
            return;
        }
        let deadline = Instant::now() + Duration::from_secs(2);
        while self.inflight() > 0 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(2));
        }
    }
}

// ---- worker channel protocol --------------------------------------------------------------------

/// A request from the UI thread to the worker.
pub enum ToWorker {
    /// (Re)scan the vessel light tree and report the catalog + health.
    Discover,
    /// Begin (or, if already running, re-target/re-plan) the party over the given selected vessel ids
    /// with this palette + per-color duration. An empty palette or no matching lights is a no-op error.
    Start {
        vessels: Vec<String>,
        plan: Plan,
    },
    /// Live-edit the running party's palette/duration without resetting its clock (ignored when idle).
    Update { plan: Plan },
    /// Stop the party and reset every targeted light to white + goal 0.
    Stop,
}

/// A reply from the worker to the UI thread.
pub enum FromWorker {
    /// The discovered catalog + health (answer to [`ToWorker::Discover`]).
    Catalog {
        vessels: Vec<VesselLights>,
        health: Health,
    },
    /// A throttled live frame of the running party (drives the preview swatch + write status).
    Tick {
        color: Rgb,
        segment: u64,
        goal: u8,
        targets: usize,
        error: Option<String>,
        perf: WriteSnapshot,
    },
    /// The party stopped (lights reset); carries any error from the reset writes.
    Stopped { error: Option<String> },
    /// A start request that couldn't run (no lights / empty palette / not connected).
    Refused(String),
}

/// The live state of a running party on the worker side: the clock, the plan, the resolved target
/// paths, and the last color/goal actually written (for frame-level dedupe).
struct RunningParty {
    start: Instant,
    plan: Plan,
    color_paths: Vec<String>,
    goal_paths: Vec<String>,
    /// Last color wire-form written **per light** (parallel to `color_paths`) for dedupe. With no
    /// stagger every entry holds the same value; with stagger they diverge as the wave ripples.
    color_seen: Vec<Option<String>>,
    /// Last goal written per light (parallel to `goal_paths`).
    goal_seen: Vec<Option<u8>>,
    /// Color passes skipped because the async pool hadn't drained the previous one yet.
    dropped: u64,
}

/// Spawns the worker thread. It owns the source for its whole life; the returned channels are the
/// only way to talk to it. Dropping the `ToWorker` sender makes the worker (and its writer pool) exit.
pub fn spawn_worker(
    source: Arc<dyn Source>,
    hz: f64,
    write_cfg: WriteConfig,
    rx: Receiver<ToWorker>,
    tx: Sender<FromWorker>,
) {
    let frame_dt = Duration::from_secs_f64(1.0 / hz.clamp(1.0, 240.0));
    // The UI doesn't need 60 ticks/sec; throttle preview updates to ~15 Hz.
    let ui_min_gap = Duration::from_millis(66);

    std::thread::spawn(move || {
        let mut catalog: Vec<VesselLights> = Vec::new();
        let mut party: Option<RunningParty> = None;
        let mut last_ui = Instant::now() - ui_min_gap;
        let mut bc = Broadcaster::build(source.clone(), write_cfg);

        loop {
            if let Some(rp) = party.as_mut() {
                // --- partying: render one frame, then wait up to one frame for a command ---
                let elapsed = rp.start.elapsed().as_secs_f64() * 1000.0;
                // The "lead" frame (offset 0) drives the UI preview; per-light writes (which may be
                // staggered behind it) happen in write_frame.
                let lead = rp.plan.frame(elapsed);
                write_frame(&mut bc, rp, elapsed);

                if last_ui.elapsed() >= ui_min_gap {
                    last_ui = Instant::now();
                    let _ = tx.send(FromWorker::Tick {
                        color: lead.color,
                        segment: lead.segment,
                        goal: lead.goal,
                        targets: rp.color_paths.len(),
                        error: bc.stats().take_error(),
                        perf: bc.stats().snapshot(write_cfg, rp.dropped),
                    });
                }

                match rx.recv_timeout(frame_dt) {
                    Ok(cmd) => {
                        if handle(cmd, &source, &mut bc, &mut catalog, &mut party, &tx) {
                            return;
                        }
                    }
                    Err(RecvTimeoutError::Timeout) => {}
                    Err(RecvTimeoutError::Disconnected) => return,
                }
            } else {
                // --- idle: block until there's something to do ---
                match rx.recv() {
                    Ok(cmd) => {
                        if handle(cmd, &source, &mut bc, &mut catalog, &mut party, &tx) {
                            return;
                        }
                    }
                    Err(_) => return,
                }
            }
        }
    });
}

/// Handles one command. Returns `true` if the worker should exit (channel hung up while resetting).
fn handle(
    cmd: ToWorker,
    source: &Arc<dyn Source>,
    bc: &mut Broadcaster,
    catalog: &mut Vec<VesselLights>,
    party: &mut Option<RunningParty>,
    tx: &Sender<FromWorker>,
) -> bool {
    match cmd {
        ToWorker::Discover => {
            *catalog = source.discover();
            let _ = tx.send(FromWorker::Catalog {
                vessels: catalog.clone(),
                health: source.health(),
            });
        }
        ToWorker::Start { vessels, plan } => {
            let (color_paths, goal_paths) = resolve_targets(catalog, &vessels);
            if plan.colors.is_empty() {
                let _ = tx.send(FromWorker::Refused("add at least one color first".into()));
            } else if color_paths.is_empty() {
                let _ = tx.send(FromWorker::Refused(
                    "no lights on the selected vessel(s)".into(),
                ));
            } else {
                bc.stats().reset(); // measure each run fresh
                let color_seen = vec![None; color_paths.len()];
                let goal_seen = vec![None; goal_paths.len()];
                *party = Some(RunningParty {
                    start: Instant::now(),
                    plan,
                    color_paths,
                    goal_paths,
                    color_seen,
                    goal_seen,
                    dropped: 0,
                });
            }
        }
        ToWorker::Update { plan } => {
            if let Some(rp) = party.as_mut() {
                rp.plan = plan; // keep the clock running so the animation doesn't jump
            }
        }
        ToWorker::Stop => {
            if let Some(rp) = party.take() {
                // Drain any in-flight async writes first, so a stale queued color can't land *after*
                // the reset and leave a light stuck mid-party. Then reset synchronously.
                bc.drain();
                let err = reset_lights(&**source, &rp);
                let _ = tx.send(FromWorker::Stopped { error: err });
            }
        }
    }
    false
}

/// Concatenates the color/goal paths of every catalog vessel whose id is in `selected`.
fn resolve_targets(catalog: &[VesselLights], selected: &[String]) -> (Vec<String>, Vec<String>) {
    let mut colors = Vec::new();
    let mut goals = Vec::new();
    for v in catalog {
        if selected.iter().any(|s| s == &v.id) {
            colors.extend(v.color_paths.iter().cloned());
            goals.extend(v.goal_paths.iter().cloned());
        }
    }
    (colors, goals)
}

/// Writes the current frame to every target, **per light**. Each light `i` is animated at
/// `elapsed - i * stagger_ms` so a non-zero stagger ripples the palette across the lights; with
/// `stagger_ms == 0` every light resolves to the same lead frame, i.e. the original lockstep
/// broadcast. Each light only writes when its own quantized color / goal actually changes (per-light
/// dedupe — still no full re-broadcast on a static palette). Errors accumulate in the shared
/// [`WriteStats`].
///
/// In async mode the whole color pass is **skipped** (and counted in `dropped`) while the pool still
/// has the previous pass in flight — this bounds the queue and surfaces a "writes can't keep up"
/// signal instead of growing memory without limit. Goal writes (at most two per segment) always go.
fn write_frame(bc: &mut Broadcaster, rp: &mut RunningParty, elapsed_ms: f64) {
    let stagger = rp.plan.stagger_ms;

    if bc.inflight() <= rp.color_paths.len() {
        for i in 0..rp.color_paths.len() {
            let local = elapsed_ms - i as f64 * stagger;
            let wire = rp.plan.frame(local).color.to_sim();
            if rp.color_seen[i].as_deref() != Some(wire.as_str()) {
                bc.send(&rp.color_paths[i], &wire);
                rp.color_seen[i] = Some(wire);
            }
        }
    } else {
        // Leave color_seen untouched so this pass retries once the pool catches up.
        rp.dropped += 1;
    }

    for j in 0..rp.goal_paths.len() {
        let local = elapsed_ms - j as f64 * stagger;
        let goal = rp.plan.frame(local).goal;
        if rp.goal_seen[j] != Some(goal) {
            bc.send(&rp.goal_paths[j], &goal.to_string());
            rp.goal_seen[j] = Some(goal);
        }
    }
}

/// Resets every targeted light to white with goal 0 — the "STOP, MY EYES" cleanup. Always written
/// synchronously and directly through the source (the pool is drained first) so the reset is
/// guaranteed durable before the `Stopped` ack.
fn reset_lights(source: &dyn Source, rp: &RunningParty) -> Option<String> {
    let mut first_err: Option<String> = None;
    let white = Rgb::WHITE.to_sim();
    for p in &rp.color_paths {
        if let Err(e) = source.write(p, &white) {
            first_err.get_or_insert(format!("{}: {}", e.errno, e.message));
        }
    }
    for p in &rp.goal_paths {
        if let Err(e) = source.write(p, "0") {
            first_err.get_or_insert(format!("{}: {}", e.errno, e.message));
        }
    }
    first_err
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    /// An in-memory source that records every write — lets us drive the worker with no real /sim.
    struct MockSource {
        catalog: Vec<VesselLights>,
        writes: Mutex<Vec<(String, String)>>,
    }

    impl Source for MockSource {
        fn write(&self, path: &str, value: &str) -> Result<(), CmdError> {
            self.writes
                .lock()
                .unwrap()
                .push((path.to_string(), value.to_string()));
            Ok(())
        }
        fn health(&self) -> Health {
            Health {
                connected: true,
                control: true,
            }
        }
        fn discover(&self) -> Vec<VesselLights> {
            self.catalog.clone()
        }
        fn label(&self) -> String {
            "mock".into()
        }
    }

    fn fixture() -> FsSource {
        let root = std::env::temp_dir().join(format!("dancy_disc_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        // Hunter: light 0 has color+goal, light 1 has only color. Polaris: one light, color only.
        for (v, n, files) in [
            ("Hunter", 0, &["color", "goal", "on"][..]),
            ("Hunter", 1, &["color", "on"][..]),
            ("Polaris", 0, &["color"][..]),
        ] {
            let d = root.join(format!("vessels/by-id/{v}/lights/{n}"));
            fs::create_dir_all(&d).unwrap();
            for f in files {
                fs::write(d.join(f), "0\n").unwrap();
            }
        }
        fs::create_dir_all(root.join("status")).unwrap();
        FsSource::new(root)
    }

    #[test]
    fn fs_discovery_finds_color_and_goal_paths() {
        let s = fixture();
        let cat = s.discover();
        assert_eq!(cat.len(), 2);
        let hunter = cat.iter().find(|v| v.id == "Hunter").unwrap();
        assert_eq!(hunter.color_paths.len(), 2); // lights 0 and 1
        assert_eq!(hunter.goal_paths.len(), 1); // only light 0 has a goal
        let polaris = cat.iter().find(|v| v.id == "Polaris").unwrap();
        assert_eq!(polaris.light_count(), 1);
        assert!(polaris.goal_paths.is_empty());
        assert!(s.health().connected && s.health().control);
        // Clean up the fixture dir.
        if let Some(stripped) = s.label().strip_prefix("fs:") {
            let _ = fs::remove_dir_all(stripped);
        }
    }

    #[test]
    fn resolve_targets_concats_selected_vessels() {
        let cat = vec![
            VesselLights {
                id: "A".into(),
                color_paths: vec!["a/0/color".into()],
                goal_paths: vec!["a/0/goal".into()],
            },
            VesselLights {
                id: "B".into(),
                color_paths: vec!["b/0/color".into(), "b/1/color".into()],
                goal_paths: vec![],
            },
        ];
        let (c, g) = resolve_targets(&cat, &["A".into(), "B".into()]);
        assert_eq!(c.len(), 3);
        assert_eq!(g.len(), 1);
        let (c2, _) = resolve_targets(&cat, &["B".into()]);
        assert_eq!(c2.len(), 2);
    }

    fn sync_bc(source: Arc<dyn Source>) -> Broadcaster {
        Broadcaster::Sync {
            source,
            stats: Arc::new(WriteStats::default()),
        }
    }

    fn party(plan: Plan, colors: usize, goals: usize) -> RunningParty {
        RunningParty {
            start: Instant::now(),
            plan,
            color_paths: (0..colors).map(|n| format!("v/{n}/color")).collect(),
            goal_paths: (0..goals).map(|n| format!("v/{n}/goal")).collect(),
            color_seen: vec![None; colors],
            goal_seen: vec![None; goals],
            dropped: 0,
        }
    }

    #[test]
    fn write_frame_dedupes_color_and_writes_goal_on_flip() {
        let src = Arc::new(MockSource {
            catalog: vec![],
            writes: Mutex::new(Vec::new()),
        });
        let mut bc = sync_bc(src.clone());
        // Single-color plan, no stagger: color is constant red, the goal flips at 1000 ms.
        let mut rp = party(Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000), 2, 1);

        write_frame(&mut bc, &mut rp, 0.0);
        // First frame: both colors + the one goal.
        assert_eq!(src.writes.lock().unwrap().len(), 3);

        // Same color, same goal -> no further writes (per-light dedupe).
        write_frame(&mut bc, &mut rp, 100.0);
        assert_eq!(src.writes.lock().unwrap().len(), 3);

        // Goal flips at the segment boundary -> one goal write, color unchanged so still deduped.
        write_frame(&mut bc, &mut rp, 1000.0);
        assert_eq!(src.writes.lock().unwrap().len(), 4);
    }

    #[test]
    fn stagger_desyncs_lights_so_they_dont_all_write_the_same_color() {
        // Two-color red/blue plan, 1000 ms/color. With a 500 ms stagger, light 1 is half a segment
        // behind light 0, so at t=0 they hold different colors and must each get their own write.
        let src = Arc::new(MockSource {
            catalog: vec![],
            writes: Mutex::new(Vec::new()),
        });
        let mut bc = sync_bc(src.clone());
        let plan = Plan::new(vec![Rgb::new(1.0, 0.0, 0.0), Rgb::new(0.0, 0.0, 1.0)], 1000)
            .with_stagger(500.0);
        let mut rp = party(plan, 2, 0);

        write_frame(&mut bc, &mut rp, 500.0);
        let writes = src.writes.lock().unwrap();
        // Light 0 at local 500 (halfway red->blue); light 1 at local 0 (pure red). Different wires.
        let v0 = &writes.iter().find(|(p, _)| p == "v/0/color").unwrap().1;
        let v1 = &writes.iter().find(|(p, _)| p == "v/1/color").unwrap().1;
        assert_ne!(v0, v1, "staggered lights should resolve to different colors");
    }

    #[test]
    fn async_pool_writes_every_light_and_drains() {
        // An async broadcaster fans one frame out across the pool; after draining, every targeted
        // light path has been written exactly once, and the latency stats are populated.
        let src = Arc::new(MockSource {
            catalog: vec![],
            writes: Mutex::new(Vec::new()),
        });
        let mut bc = Broadcaster::build(
            src.clone(),
            WriteConfig {
                async_writes: true,
                writers: 4,
            },
        );
        let mut rp = party(Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000), 10, 10);
        write_frame(&mut bc, &mut rp, 0.0);
        bc.drain();
        let writes = src.writes.lock().unwrap();
        assert_eq!(writes.len(), 20); // 10 colors + 10 goals
        assert!(writes.iter().any(|(p, _)| p == "v/3/color"));
        assert!(bc.stats().count.load(Ordering::Relaxed) == 20);
    }

    #[test]
    fn sanitize_segment_replaces_specials() {
        assert_eq!(sanitize_segment("Hunter"), "Hunter");
        assert_eq!(sanitize_segment("My Ship!"), "My_Ship_");
    }

    #[test]
    fn worker_discovers_parties_and_resets_end_to_end() {
        use std::sync::mpsc;
        use std::time::Duration;

        // Fixture: one vessel, one light with color + goal, both starting off-white.
        let root = std::env::temp_dir().join(format!("dancy_e2e_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let light = root.join("vessels/by-id/Hunter/lights/0");
        fs::create_dir_all(&light).unwrap();
        fs::write(light.join("color"), "0 0 0\n").unwrap();
        fs::write(light.join("goal"), "0\n").unwrap();

        let (cmd_tx, cmd_rx) = mpsc::channel::<ToWorker>();
        let (up_tx, up_rx) = mpsc::channel::<FromWorker>();
        spawn_worker(
            Arc::new(FsSource::new(&root)),
            60.0,
            WriteConfig::default(),
            cmd_rx,
            up_tx,
        );

        // Discover -> Catalog with the one light.
        cmd_tx.send(ToWorker::Discover).unwrap();
        let cat = recv_catalog(&up_rx);
        assert_eq!(cat.len(), 1);
        assert_eq!(cat[0].color_paths.len(), 1);

        // Start a solid-red party; wait for a live tick, then the file must hold red.
        cmd_tx
            .send(ToWorker::Start {
                vessels: vec!["Hunter".into()],
                plan: Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000),
            })
            .unwrap();
        wait_for_tick(&up_rx);
        let colored = fs::read_to_string(light.join("color")).unwrap();
        assert_eq!(colored.trim(), "1 0 0");

        // Stop -> the light resets to white with goal 0.
        cmd_tx.send(ToWorker::Stop).unwrap();
        loop {
            match up_rx.recv_timeout(Duration::from_secs(2)).unwrap() {
                FromWorker::Stopped { error } => {
                    assert!(error.is_none());
                    break;
                }
                _ => continue,
            }
        }
        assert_eq!(fs::read_to_string(light.join("color")).unwrap().trim(), "1 1 1");
        assert_eq!(fs::read_to_string(light.join("goal")).unwrap().trim(), "0");

        drop(cmd_tx); // worker exits
        let _ = fs::remove_dir_all(&root);
    }

    fn recv_catalog(rx: &std::sync::mpsc::Receiver<FromWorker>) -> Vec<VesselLights> {
        loop {
            match rx.recv_timeout(Duration::from_secs(2)).unwrap() {
                FromWorker::Catalog { vessels, .. } => return vessels,
                _ => continue,
            }
        }
    }

    fn wait_for_tick(rx: &std::sync::mpsc::Receiver<FromWorker>) {
        loop {
            if let FromWorker::Tick { .. } = rx.recv_timeout(Duration::from_secs(2)).unwrap() {
                return;
            }
        }
    }
}
