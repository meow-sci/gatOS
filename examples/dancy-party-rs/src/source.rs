//! The data layer: read/write `/sim` fields by path, discover the lights once, run the party, poll
//! battery, and refill it. Two backends mirror the sibling examples:
//!
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): discovery is
//!   a directory walk; a write is one `echo value > file`; a read is a `cat`.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url`/`$GATOS_HTTP` dev mode).
//!   It has no directory listing, so discovery *probes* `lights/<n>/…` per vessel until the ordinals
//!   run out.
//!
//! Discovery runs **once** (the `/sim` light tree is assumed stable for the program's life — re-reading
//! the 9p tree has a real cost), and the resulting per-vessel path lists are cached on the worker.
//!
//! The worker runs on a tiny **current-thread tokio runtime** (see [`spawn_worker`]). It drives the
//! animation frame timer and a battery-poll timer, and dispatches every light write **fire-and-forget**
//! via `spawn_blocking` — it never awaits the write's result. The gatOS backend batches writes per game
//! tick, so a write "response" is up to a whole frame (~16 ms) away; this program doesn't care whether
//! a given light write has landed yet, only that the animation timing stays crisp. There is no bespoke
//! writer pool: tokio's blocking pool absorbs the concurrent in-flight writes, and they all land in the
//! same game-tick batch. An `inflight` gauge exists only so a clean **stop** can briefly drain pending
//! writes before resetting the lights (so a stale color can't land *after* the reset).

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::sync::mpsc::UnboundedReceiver;

use crate::color::Rgb;
use crate::party::Plan;

/// A reply channel to the UI thread (std mpsc — the UI polls it with `try_recv`).
type ToUi = std::sync::mpsc::Sender<FromWorker>;

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
/// vessels' lists. `id` is the sanitized `/sim` path segment (and the display id).
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

/// A read/write/discover interface over the `/sim` surface. Discovery/health/reads/writes are all
/// blocking; the worker offloads each onto tokio's blocking pool, so the trait stays `Send + Sync`.
pub trait Source: Send + Sync {
    /// Writes `value` to a field as one newline-terminated write (the `echo value > file` shape), so a
    /// control file actuates and a failure carries the real errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// Reads a field's text, or `None` if it's missing/unreadable (used for the battery meter).
    fn read_field(&self, path: &str) -> Option<String>;

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

    fn read_field(&self, path: &str) -> Option<String> {
        fs::read_to_string(self.root.join(path)).ok()
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

    fn read_field(&self, path: &str) -> Option<String> {
        self.read(path).ok()
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

// ---- worker channel protocol --------------------------------------------------------------------

/// A request from the UI thread to the worker (sent over an unbounded tokio channel; sending is sync,
/// so the UI thread never blocks).
pub enum ToWorker {
    /// (Re)scan the vessel light tree and report the catalog + health.
    Discover,
    /// Set the vessels whose battery the worker polls (sent on entering the party screen) so the meter
    /// populates before a party starts. The refill action targets the same set.
    Watch { vessels: Vec<String> },
    /// Begin (or, if already running, re-target/re-plan) the party over the given selected vessel ids
    /// with this plan and frame rate. An empty palette or no matching lights is a no-op error.
    Start {
        vessels: Vec<String>,
        plan: Plan,
        hz: f64,
    },
    /// Live-edit the running party's plan / frame rate without resetting its clock (ignored when idle).
    Update { plan: Plan, hz: f64 },
    /// Refill every watched vessel's battery (`debug.refill_battery`); reports the outcome.
    RefillBattery,
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
    /// The aggregate battery charge across the watched vessels (`fraction` = average over the `count`
    /// vessels that have a battery; `None`/`0` when none do).
    Battery {
        fraction: Option<f64>,
        count: usize,
    },
    /// A throttled live frame of the running party (drives the preview swatch + write counter).
    Tick {
        color: Rgb,
        color_segment: u64,
        anim_segment: u64,
        /// The goal actuation setpoint (0..1) for this frame, or `None` when the animation is off
        /// (`anim_min == anim_max`) and the goal is left untouched.
        goal: Option<f64>,
        targets: usize,
        /// Writes dispatched since this party started, and how many are still in flight right now.
        writes: u64,
        inflight: usize,
    },
    /// The party stopped (lights reset); carries any error from the reset writes.
    Stopped { error: Option<String> },
    /// A battery-refill request completed; carries the first write error, if any.
    RefillDone { error: Option<String> },
    /// A start request that couldn't run (no lights / empty palette / not connected).
    Refused(String),
}

/// The live state of a running party on the worker side: the clock, the plan, the resolved target
/// paths, and the last color/goal actually written (for per-light frame-level dedupe).
struct RunningParty {
    start: Instant,
    plan: Plan,
    color_paths: Vec<String>,
    goal_paths: Vec<String>,
    /// Last color wire-form written **per light** (parallel to `color_paths`). With no color stagger
    /// every entry holds the same value; with stagger they diverge as the wave ripples.
    color_seen: Vec<Option<String>>,
    /// Last goal wire-form written per light (parallel to `goal_paths`). `None` until first written;
    /// once the animation is a noop nothing is ever written, so it stays `None`.
    goal_seen: Vec<Option<String>>,
    /// Writes dispatched since this party started (color + goal, across all lights).
    writes: u64,
}

/// Spawns the worker thread. It hosts a small current-thread tokio runtime for its whole life; the
/// returned channels are the only way to talk to it. Dropping the [`ToWorker`] sender closes the
/// channel and the worker exits. `hz` is the initial frame rate (live-tunable via `Update`/`Start`).
pub fn spawn_worker(source: Arc<dyn Source>, hz: f64, rx: UnboundedReceiver<ToWorker>, tx: ToUi) {
    std::thread::spawn(move || {
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_time()
            .build()
            .expect("build worker runtime");
        rt.block_on(worker_loop(source, rx, tx, hz));
    });
}

/// Period between animation frames for a frame rate (clamped 1..240 Hz).
fn hz_period(hz: f64) -> Duration {
    Duration::from_secs_f64(1.0 / hz.clamp(1.0, 240.0))
}

/// The async control loop. Selects over three sources: UI commands, the animation frame timer (only
/// armed while a party is running), and the battery-poll timer.
async fn worker_loop(source: Arc<dyn Source>, mut rx: UnboundedReceiver<ToWorker>, tx: ToUi, mut hz: f64) {
    let mut catalog: Vec<VesselLights> = Vec::new();
    let mut watch: Vec<String> = Vec::new();
    let mut party: Option<RunningParty> = None;
    let inflight = Arc::new(AtomicUsize::new(0));

    let mut frame = tokio::time::interval(hz_period(hz));
    frame.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut batt = tokio::time::interval(Duration::from_millis(1000));

    // The UI doesn't need 60 ticks/sec; throttle preview updates to ~15 Hz.
    let ui_min_gap = Duration::from_millis(66);
    let mut last_ui = Instant::now() - ui_min_gap;

    loop {
        tokio::select! {
            cmd = rx.recv() => {
                let Some(cmd) = cmd else { break }; // UI dropped the sender → exit
                let prev_hz = hz;
                handle(cmd, &source, &tx, &mut catalog, &mut watch, &mut party, &mut hz, &inflight).await;
                if (hz - prev_hz).abs() > f64::EPSILON {
                    frame = tokio::time::interval(hz_period(hz));
                    frame.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
                }
            }
            _ = frame.tick(), if party.is_some() => {
                let rp = party.as_mut().expect("guard ensures Some");
                let elapsed = rp.start.elapsed().as_secs_f64() * 1000.0;
                // The preview band mirrors light 0's actual output — palette color dimmed by its
                // brightness — so the calmer copy reflects the brightness flicker too.
                let (lead_raw, color_seg) = rp.plan.color_at(elapsed);
                let lead_color = lead_raw.scaled(rp.plan.brightness_at(0, elapsed));
                let (goal, anim_seg) = rp.plan.goal_at(elapsed);

                // Compute the per-light writes that actually changed this frame, then fire each one
                // and forget it — we never await the result.
                for (path, value) in frame_writes(rp, elapsed) {
                    rp.writes += 1;
                    inflight.fetch_add(1, Ordering::Relaxed);
                    let s = source.clone();
                    let infl = inflight.clone();
                    tokio::task::spawn_blocking(move || {
                        let _ = s.write(&path, &value);
                        infl.fetch_sub(1, Ordering::Relaxed);
                    });
                }

                if last_ui.elapsed() >= ui_min_gap {
                    last_ui = Instant::now();
                    let _ = tx.send(FromWorker::Tick {
                        color: lead_color,
                        color_segment: color_seg,
                        anim_segment: anim_seg,
                        goal,
                        targets: rp.color_paths.len(),
                        writes: rp.writes,
                        inflight: inflight.load(Ordering::Relaxed),
                    });
                }
            }
            _ = batt.tick() => {
                let (fraction, count) = read_battery(&source, &watch).await;
                let _ = tx.send(FromWorker::Battery { fraction, count });
            }
        }
    }
}

/// Handles one UI command. Heavy/blocking work (discover, reads, resets, refills) is offloaded to the
/// blocking pool and awaited; the hot animation path never goes through here.
#[allow(clippy::too_many_arguments)]
async fn handle(
    cmd: ToWorker,
    source: &Arc<dyn Source>,
    tx: &ToUi,
    catalog: &mut Vec<VesselLights>,
    watch: &mut Vec<String>,
    party: &mut Option<RunningParty>,
    hz: &mut f64,
    inflight: &Arc<AtomicUsize>,
) {
    match cmd {
        ToWorker::Discover => {
            let s = source.clone();
            let cat = tokio::task::spawn_blocking(move || s.discover())
                .await
                .unwrap_or_default();
            let s = source.clone();
            let health = tokio::task::spawn_blocking(move || s.health())
                .await
                .unwrap_or_default();
            *catalog = cat.clone();
            let _ = tx.send(FromWorker::Catalog {
                vessels: cat,
                health,
            });
        }
        ToWorker::Watch { vessels } => {
            *watch = vessels;
            // Read once immediately so the meter populates without waiting a full poll interval.
            let (fraction, count) = read_battery(source, watch).await;
            let _ = tx.send(FromWorker::Battery { fraction, count });
        }
        ToWorker::Start {
            vessels,
            plan,
            hz: new_hz,
        } => {
            *hz = new_hz.clamp(1.0, 240.0);
            *watch = vessels.clone();
            let (color_paths, goal_paths) = resolve_targets(catalog, &vessels);
            if plan.colors.is_empty() {
                let _ = tx.send(FromWorker::Refused("add at least one color first".into()));
            } else if color_paths.is_empty() {
                let _ = tx.send(FromWorker::Refused(
                    "no lights on the selected vessel(s)".into(),
                ));
            } else {
                let color_seen = vec![None; color_paths.len()];
                let goal_seen = vec![None; goal_paths.len()];
                *party = Some(RunningParty {
                    start: Instant::now(),
                    plan,
                    color_paths,
                    goal_paths,
                    color_seen,
                    goal_seen,
                    writes: 0,
                });
            }
        }
        ToWorker::Update { plan, hz: new_hz } => {
            *hz = new_hz.clamp(1.0, 240.0);
            if let Some(rp) = party.as_mut() {
                rp.plan = plan; // keep the clock running so the animation doesn't jump
            }
        }
        ToWorker::RefillBattery => {
            if watch.is_empty() {
                let _ = tx.send(FromWorker::RefillDone {
                    error: Some("no vessels armed".into()),
                });
            } else {
                let s = source.clone();
                let w = watch.clone();
                let error = tokio::task::spawn_blocking(move || refill_batteries(&*s, &w))
                    .await
                    .unwrap_or_else(|_| Some("refill dispatch failed".into()));
                let _ = tx.send(FromWorker::RefillDone { error });
            }
        }
        ToWorker::Stop => {
            if let Some(rp) = party.take() {
                // Briefly let in-flight writes drain so a stale queued color can't land *after* the
                // reset and leave a light stuck mid-party. Then reset synchronously.
                drain_inflight(inflight).await;
                let s = source.clone();
                let error = tokio::task::spawn_blocking(move || reset_lights(&*s, &rp))
                    .await
                    .unwrap_or_else(|_| Some("reset dispatch failed".into()));
                let _ = tx.send(FromWorker::Stopped { error });
            }
        }
    }
}

/// Reads each watched vessel's `battery/charge` (off the blocking pool) and averages the ones that
/// have a battery. Returns `(None, 0)` when no watched vessel reports a battery.
async fn read_battery(source: &Arc<dyn Source>, watch: &[String]) -> (Option<f64>, usize) {
    if watch.is_empty() {
        return (None, 0);
    }
    let s = source.clone();
    let w = watch.to_vec();
    tokio::task::spawn_blocking(move || {
        let mut sum = 0.0;
        let mut n = 0usize;
        for id in &w {
            if let Some(text) = s.read_field(&format!("vessels/by-id/{id}/battery/charge")) {
                if let Ok(v) = text.trim().parse::<f64>() {
                    sum += v;
                    n += 1;
                }
            }
        }
        if n == 0 {
            (None, 0)
        } else {
            (Some(sum / n as f64), n)
        }
    })
    .await
    .unwrap_or((None, 0))
}

/// Writes `1` to every watched vessel's `debug/vessels/<id>/refill_battery` trigger. Returns the first
/// error encountered (e.g. `EACCES` when debug is gated off), or `None` on success.
fn refill_batteries(source: &dyn Source, watch: &[String]) -> Option<String> {
    let mut first_err = None;
    for id in watch {
        if let Err(e) = source.write(&format!("debug/vessels/{id}/refill_battery"), "1") {
            first_err.get_or_insert(format!("{}: {}", e.errno, e.message));
        }
    }
    first_err
}

/// Waits (bounded) for fire-and-forget writes to drain, so a clean stop resets from a quiet state.
async fn drain_inflight(inflight: &Arc<AtomicUsize>) {
    let deadline = Instant::now() + Duration::from_secs(2);
    while inflight.load(Ordering::Relaxed) > 0 && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(2)).await;
    }
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

/// Computes the per-light writes that **changed** this frame and records them as seen (per-light
/// dedupe — a static palette re-broadcasts nothing). Each light `i` runs on its own clock offset:
/// color at `elapsed - i*color_stagger`, goal at `elapsed - i*anim_stagger`, so the two staggers
/// ripple color and animation across the lights independently. Pure: the caller dispatches the
/// returned `(path, value)` pairs fire-and-forget.
fn frame_writes(rp: &mut RunningParty, elapsed_ms: f64) -> Vec<(String, String)> {
    let mut out = Vec::new();
    let color_stagger = rp.plan.color_stagger_ms;
    let anim_stagger = rp.plan.anim_stagger_ms;

    for i in 0..rp.color_paths.len() {
        let local = elapsed_ms - i as f64 * color_stagger;
        // The palette color (on this light's color clock) scaled by its own random brightness (on the
        // brightness clock). Both feed the per-light dedupe below, so a static color *and* steady
        // brightness re-broadcasts nothing, while a varying brightness only writes when its quantized
        // value actually moves.
        let color = rp.plan.color_at(local).0;
        let bright = rp.plan.brightness_at(i, elapsed_ms);
        let wire = color.scaled(bright).to_sim();
        if rp.color_seen[i].as_deref() != Some(wire.as_str()) {
            out.push((rp.color_paths[i].clone(), wire.clone()));
            rp.color_seen[i] = Some(wire);
        }
    }

    for j in 0..rp.goal_paths.len() {
        let local = elapsed_ms - j as f64 * anim_stagger;
        // `None` here means the animation is off (`anim_min == anim_max`): skip the goal entirely so a
        // collapsed range never writes a constant setpoint — the actuation is a true noop.
        let Some(goal) = rp.plan.goal_at(local).0 else {
            continue;
        };
        let wire = fmt_goal(goal);
        if rp.goal_seen[j].as_deref() != Some(wire.as_str()) {
            out.push((rp.goal_paths[j].clone(), wire.clone()));
            rp.goal_seen[j] = Some(wire);
        }
    }

    out
}

/// The `/sim` wire form of a goal actuation fraction: a 0..1 number trimmed to 4 decimals with no
/// trailing zeros (`1`, `0`, `0.75`), matching the compact style of the color writes.
fn fmt_goal(v: f64) -> String {
    let s = format!("{:.4}", v.clamp(0.0, 1.0));
    let trimmed = s.trim_end_matches('0').trim_end_matches('.');
    if trimmed.is_empty() {
        "0".to_string()
    } else {
        trimmed.to_string()
    }
}

/// Resets every targeted light to white with goal 0 — the "STOP, MY EYES" cleanup. Written
/// synchronously and directly through the source (after the pool is drained) so the reset is
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
        if let Some(stripped) = s.label().strip_prefix("fs:") {
            let _ = fs::remove_dir_all(stripped);
        }
    }

    #[test]
    fn fs_read_field_reads_a_value() {
        let root = std::env::temp_dir().join(format!("dancy_rf_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let d = root.join("vessels/by-id/Hunter/battery");
        fs::create_dir_all(&d).unwrap();
        fs::write(d.join("charge"), "0.42\n").unwrap();
        let s = FsSource::new(&root);
        assert_eq!(
            s.read_field("vessels/by-id/Hunter/battery/charge")
                .as_deref()
                .map(str::trim),
            Some("0.42")
        );
        assert!(s.read_field("vessels/by-id/Hunter/nope").is_none());
        let _ = fs::remove_dir_all(&root);
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

    fn party(plan: Plan, colors: usize, goals: usize) -> RunningParty {
        RunningParty {
            start: Instant::now(),
            plan,
            color_paths: (0..colors).map(|n| format!("v/{n}/color")).collect(),
            goal_paths: (0..goals).map(|n| format!("v/{n}/goal")).collect(),
            color_seen: vec![None; colors],
            goal_seen: vec![None; goals],
            writes: 0,
        }
    }

    #[test]
    fn frame_writes_dedupes_color_and_writes_goal_on_flip() {
        // Single-color plan: color is constant red; the goal flips on its own 1 s animation clock.
        let mut rp = party(Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000, 1000), 2, 1);

        // First frame: both colors + the one goal.
        assert_eq!(frame_writes(&mut rp, 0.0).len(), 3);
        // Same color, same goal -> nothing (per-light dedupe).
        assert_eq!(frame_writes(&mut rp, 100.0).len(), 0);
        // Goal flips at the animation boundary -> exactly one goal write, color still deduped.
        let w = frame_writes(&mut rp, 1000.0);
        assert_eq!(w.len(), 1);
        assert!(w[0].0.ends_with("/goal"));
    }

    #[test]
    fn color_stagger_desyncs_lights_without_touching_the_goal_clock() {
        // red/blue, 1 s color fade, with a 500 ms color stagger but no animation stagger. At t=500,
        // light 0 is halfway red->blue, light 1 (500 ms behind) is pure red — different wires.
        let plan = Plan::new(vec![Rgb::new(1.0, 0.0, 0.0), Rgb::new(0.0, 0.0, 1.0)], 1000, 1000)
            .with_staggers(500.0, 0.0);
        let mut rp = party(plan, 2, 0);
        let writes = frame_writes(&mut rp, 500.0);
        let v0 = &writes.iter().find(|(p, _)| p == "v/0/color").unwrap().1;
        let v1 = &writes.iter().find(|(p, _)| p == "v/1/color").unwrap().1;
        assert_ne!(v0, v1, "staggered lights should resolve to different colors");
    }

    #[test]
    fn sanitize_segment_replaces_specials() {
        assert_eq!(sanitize_segment("Hunter"), "Hunter");
        assert_eq!(sanitize_segment("My Ship!"), "My_Ship_");
    }

    #[test]
    fn worker_discovers_parties_and_resets_end_to_end() {
        use std::time::Duration;

        // Fixture: one vessel, one light with color + goal, both starting off-white.
        let root = std::env::temp_dir().join(format!("dancy_e2e_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let light = root.join("vessels/by-id/Hunter/lights/0");
        fs::create_dir_all(&light).unwrap();
        fs::write(light.join("color"), "0 0 0\n").unwrap();
        fs::write(light.join("goal"), "0\n").unwrap();

        let (cmd_tx, cmd_rx) = tokio::sync::mpsc::unbounded_channel::<ToWorker>();
        let (up_tx, up_rx) = std::sync::mpsc::channel::<FromWorker>();
        spawn_worker(Arc::new(FsSource::new(&root)), 60.0, cmd_rx, up_tx);

        // Discover -> Catalog with the one light.
        cmd_tx.send(ToWorker::Discover).unwrap();
        let cat = recv_catalog(&up_rx);
        assert_eq!(cat.len(), 1);
        assert_eq!(cat[0].color_paths.len(), 1);

        // Start a solid-red party; wait for a live tick, then (the write is fire-and-forget) poll the
        // file until it holds red.
        cmd_tx
            .send(ToWorker::Start {
                vessels: vec!["Hunter".into()],
                plan: Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000, 1000),
                hz: 60.0,
            })
            .unwrap();
        wait_for_tick(&up_rx);
        assert!(
            wait_for_file(&light.join("color"), "1 0 0"),
            "fire-and-forget color write should land"
        );

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
        assert_eq!(
            fs::read_to_string(light.join("color")).unwrap().trim(),
            "1 1 1"
        );
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

    /// Polls a file until it trims to `want`, or times out (fire-and-forget writes land async).
    fn wait_for_file(path: &std::path::Path, want: &str) -> bool {
        let deadline = Instant::now() + Duration::from_secs(2);
        while Instant::now() < deadline {
            if let Ok(s) = fs::read_to_string(path) {
                if s.trim() == want {
                    return true;
                }
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        false
    }
}
