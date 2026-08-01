//! The data layer: read/write `/sim` fields by path, discover the volumetric-exhaust templates, run
//! the show, poll the game's read-back, and reset templates to pristine. Two backends mirror the
//! sibling examples:
//!
//! - [`FsSource`] reads the **real `/sim` mount** with `std::fs` (the in-guest default): discovery is
//!   a directory walk of `debug/engineplume/templates/`; a write is one `echo value > file`; a read is
//!   a `cat`.
//! - [`HttpSource`] uses the mod's HTTP `/v1/fs/<path>` mirror (the `--url`/`$GATOS_HTTP` dev mode).
//!   `/v1/fs` has **no directory listing** (a path that resolves to a directory is a 404), so the
//!   roster comes from `GET /v1/snapshot` → `fx_editors.plume_templates[].id`.
//!
//! **Sanitization, the one gotcha.** A template's `/sim` directory is named `Sanitize(rawId)`
//! (`[A-Za-z0-9._-]` survive, everything else becomes `_`), while the command the leaf builds carries
//! the **raw** id. Addressing **by path** — all this program ever does — therefore always uses the
//! *sanitized* name and the server maps it back itself. The ids in `/v1/snapshot` are **raw**, so the
//! HTTP discovery path runs each through [`sanitize_segment`] before building a path. (Corollary: this
//! program never uses `POST /v1/command`, which would need the raw id in its token.)
//!
//! Discovery runs on demand (`r` rescans); the resulting per-template path lists are cached on the
//! worker. The worker runs on a tiny **current-thread tokio runtime** (see [`spawn_worker`]). It drives
//! the animation frame timer and a read-back timer, and dispatches every write **fire-and-forget** via
//! `spawn_blocking` — it never awaits the write's result. The gatOS backend batches writes per game
//! tick, so a write "response" is up to a whole frame away; this program doesn't care whether a given
//! colour write has landed yet, only that the animation timing stays crisp. There is no bespoke writer
//! pool: tokio's blocking pool absorbs the concurrent in-flight writes. An `inflight` gauge exists only
//! so a clean **stop** can briefly drain pending writes before resetting (so a stale colour can't land
//! *after* the reset).
//!
//! A frame that changes **two or more** leaves goes out as ONE `/sim/ctl/batch` group (SPEC §3.10), so
//! all four gradient stops move in the *same* game tick and the plume never renders a torn gradient.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::sync::mpsc::UnboundedReceiver;

use crate::color::Rgb;
use crate::party::{fmt_num, Plan, SLOTS};

/// A reply channel to the UI thread (std mpsc — the UI polls it with `try_recv`).
type ToUi = std::sync::mpsc::Sender<FromWorker>;

// ---- paths — one place --------------------------------------------------------------------------

/// The FX family root. Present only while `[control] debug_namespace = true` — when it is off the
/// whole `/sim/debug` subtree is **absent**, so writes fail `ENOENT`, not `EACCES`.
const PLUME_ROOT: &str = "debug/engineplume";
/// The atomic same-tick command group (SPEC §3.10).
const BATCH_PATH: &str = "ctl/batch";
/// `BatchFile.MaxCommands` — a group may carry at most this many commands.
const BATCH_MAX: usize = 64;

fn tpl_dir(id: &str) -> String {
    format!("{PLUME_ROOT}/templates/{id}")
}
fn color_path(id: &str, slot: usize) -> String {
    format!("{}/emission/color{slot}", tpl_dir(id))
}
fn brightness_path(id: &str) -> String {
    format!("{}/emission/brightness", tpl_dir(id))
}
fn reset_path(id: &str) -> String {
    format!("{}/reset", tpl_dir(id))
}
fn json_path(id: &str) -> String {
    format!("{}/json", tpl_dir(id))
}

/// The outcome of a failed write — an errno-ish tag + message (the frozen control-file errno
/// vocabulary: `EINVAL`, `EACCES`, `ENOENT`, `ETIMEDOUT`, …), surfaced on the status line.
#[derive(Debug, Clone)]
pub struct CmdError {
    pub errno: String,
    pub message: String,
}

/// Integration health read from the source — drives the header badges and explains write failures.
#[derive(Clone, Copy, Default, Debug)]
pub struct Health {
    pub connected: bool,
    /// A command sink is wired. Whether control is *enabled* only shows up as `EACCES` on the first
    /// write (the fs source can only see that the `ctl` directory exists).
    pub control: bool,
    /// `[control] debug_namespace` is on — i.e. `/sim/debug` exists at all. Off is the single most
    /// likely reason nothing works, which is why it gets its own badge.
    pub debug: bool,
}

/// One discovered volumetric-exhaust template. `id` is the SANITIZED `/sim` path segment (and the
/// display id) — see the module docs on sanitization. `colors` holds the four `emission/color<n>`
/// paths in slot order; `brightness` is `None` when that leaf is absent (never observed in practice,
/// but the tree is snapshot-driven and we do not assume).
#[derive(Clone, Debug)]
pub struct PlumeTemplate {
    pub id: String,
    pub colors: [String; SLOTS],
    pub brightness: Option<String>,
    pub reset: String,
    /// A live read-back sampled at discovery: the current `emission/color0..3` and `brightness`.
    /// Feeds the Templates-screen row and the `c` (capture) action.
    pub current: Option<TemplateValues>,
}

impl PlumeTemplate {
    /// How many of the four gradient stops actually resolved (4 = healthy).
    pub fn stops(&self) -> usize {
        self.colors.iter().filter(|p| !p.is_empty()).count()
    }
}

/// One template's read-back: the four gradient stops and the emissive brightness, as the *game*
/// currently reports them. Values are 32-bit floats game-side and the FX surface is re-sampled on a
/// write or every 2 s otherwise, so an idle read-back can be slightly stale and `0.6` reads back as
/// `0.60000002` — never compare one to a written string.
#[derive(Clone, Copy, Debug, Default)]
pub struct TemplateValues {
    pub colors: [Option<Rgb>; SLOTS],
    pub brightness: Option<f64>,
}

/// A read/write/discover interface over the `/sim` plume surface. Discovery/health/reads/writes are
/// all blocking; the worker offloads each onto tokio's blocking pool, so the trait stays `Send + Sync`.
pub trait Source: Send + Sync {
    /// Writes `value` to a field as one newline-terminated write (the `echo value > file` shape), so a
    /// control file actuates and a failure carries the real errno.
    fn write(&self, path: &str, value: &str) -> Result<(), CmdError>;

    /// Reads a field's text, or `None` if it's missing/unreadable.
    fn read_field(&self, path: &str) -> Option<String>;

    /// Reads integration health (connection + control + debug-namespace gating).
    fn health(&self) -> Health;

    /// Walks/queries the `/sim` plume-template roster and returns every template's leaf paths.
    fn discover(&self) -> Vec<PlumeTemplate>;

    /// A short label for the header (e.g. `fs:/sim` or the HTTP base URL).
    fn label(&self) -> String;

    /// The whole-template `json` document, parsed into slot colours + brightness. One small read —
    /// deliberately **not** `/v1/snapshot`, which is the whole world (vessels, parts, bodies).
    fn read_values(&self, id: &str) -> Option<TemplateValues> {
        parse_values(&self.read_field(&json_path(id))?)
    }
}

/// Sanitizes a template id into a `/sim` path segment (non-`[A-Za-z0-9._-]` → `_`), matching the
/// server's own `SimFsTree.Sanitize` so `/v1/snapshot`-discovered (raw) ids address the right paths.
/// The `~2` duplicate suffix `SanitizeNames` can add cannot occur here — the template registry is
/// keyed by id, so ids are unique.
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

/// Parses a template's `json` document into its gradient stops + brightness.
///
/// Two shapes must both parse, because this one parser serves both reads: the per-template `json`
/// leaf (`Formats.FxFields`) writes **bare numbers** for arity-1 fields, while `/v1/snapshot` writes
/// **every** value as an array (`double[]`, even scalars). The `fields` map keys are verbatim — only
/// `SimJson`'s *property* naming policy is snake_case, not its dictionary-key policy — so they keep
/// their slashes (`"emission/color0"`).
fn parse_values(text: &str) -> Option<TemplateValues> {
    let doc: serde_json::Value = serde_json::from_str(text).ok()?;
    let mut out = TemplateValues::default();
    for (slot, cell) in out.colors.iter_mut().enumerate() {
        *cell = doc.get(format!("emission/color{slot}")).and_then(field_rgb);
    }
    out.brightness = doc.get("emission/brightness").and_then(field_scalar);
    Some(out)
}

fn field_scalar(v: &serde_json::Value) -> Option<f64> {
    v.as_f64()
        .or_else(|| v.as_array().and_then(|a| a.first()).and_then(|n| n.as_f64()))
}

fn field_rgb(v: &serde_json::Value) -> Option<Rgb> {
    let a = v.as_array()?;
    if a.len() != 3 {
        return None;
    }
    Some(Rgb::new(a[0].as_f64()?, a[1].as_f64()?, a[2].as_f64()?))
}

/// A one-line, actionable hint for the errnos this program can actually provoke. Appended to the
/// raw `"{errno}: {message}"` so the status line says what to DO about it.
pub fn hint(errno: &str) -> Option<&'static str> {
    match errno {
        // The whole /sim/debug subtree is ABSENT when the namespace is gated off, so the failure
        // is ENOENT — not EACCES. Different config key, different fix.
        "ENOENT" => Some("is [control] debug_namespace = true? (/sim/debug is absent when it is off)"),
        "EACCES" | "EPERM" => Some("[control] enabled = false in gatos.toml"),
        "EOPNOTSUPP" => Some("the plume accessor is degraded — see /sim/status/accessors"),
        "ETIMEDOUT" => Some("the game thread is not draining (paused or loading?)"),
        "EINVAL" => Some("a value was out of range or the wrong arity"),
        _ => None,
    }
}

/// `"{errno}: {message}"`, plus [`hint`]'s advice when there is any.
fn describe(e: &CmdError) -> String {
    match hint(&e.errno) {
        Some(h) => format!("{}: {} \u{2014} {h}", e.errno, e.message),
        None => format!("{}: {}", e.errno, e.message),
    }
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
            connected: self.root.join("time").is_dir() || self.root.join("vessels").is_dir(),
            control: self.root.join("ctl").is_dir(),
            // `SimFsTree` only adds DebugDir() when the debug namespace is enabled, so the directory
            // existing *is* the gate.
            debug: self.root.join(PLUME_ROOT).is_dir(),
        }
    }

    fn discover(&self) -> Vec<PlumeTemplate> {
        // The documented discovery path (SPEC §3.7: "Discovery: `ls templates/`"). The directory
        // names are already sanitized by the server.
        let mut out = Vec::new();
        for (id, is_dir) in self.entries(&format!("{PLUME_ROOT}/templates")) {
            if !is_dir {
                continue;
            }
            let files: Vec<String> = self
                .entries(&format!("{}/emission", tpl_dir(&id)))
                .into_iter()
                .filter(|(_, d)| !*d)
                .map(|(n, _)| n)
                .collect();
            // A stop whose leaf is missing keeps an empty path, so the row can render as incomplete
            // (still armable — a write would report ENOENT, which is the honest answer).
            let colors = std::array::from_fn(|slot| {
                if files.iter().any(|f| f == &format!("color{slot}")) {
                    color_path(&id, slot)
                } else {
                    String::new()
                }
            });
            let brightness = files
                .iter()
                .any(|f| f == "brightness")
                .then(|| brightness_path(&id));
            let current = self.read_values(&id);
            out.push(PlumeTemplate {
                reset: reset_path(&id),
                id,
                colors,
                brightness,
                current,
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

/// Uses the mod's HTTP `/v1/fs/<path>` field mirror (and `/v1/snapshot` for discovery). `base` is the
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
                debug: v.get("debug").and_then(|c| c.as_bool()).unwrap_or(false),
            },
            None => Health::default(),
        }
    }

    fn discover(&self) -> Vec<PlumeTemplate> {
        // /v1/fs has NO directory listing (VfsScan::Resolve returns None for a directory, which the
        // server turns into 404/ENOENT), so the roster comes from the one JSON route that carries it:
        // GET /v1/snapshot -> fx_editors.plume_templates[].id. Those ids are RAW
        // (FxEntitySnapshot.Id), so each is sanitized into its /sim path segment exactly the way the
        // server names the directory. `fx_editors` is ABSENT (not empty) when the debug namespace is
        // off — the empty roster is the signal, and Health::debug explains it. This runs once per
        // rescan, never in the animation loop.
        let ids: Vec<String> = self
            .get_json("snapshot")
            .and_then(|v| {
                v.get("fx_editors")?
                    .get("plume_templates")?
                    .as_array()
                    .map(|a| {
                        a.iter()
                            .filter_map(|e| e.get("id")?.as_str().map(sanitize_segment))
                            .collect()
                    })
            })
            .unwrap_or_default();

        // …then one small GET /v1/fs/<tpl>/json per template for the current values.
        let mut out: Vec<PlumeTemplate> = ids
            .into_iter()
            .map(|id| {
                let current = self.read_values(&id);
                PlumeTemplate {
                    colors: std::array::from_fn(|slot| color_path(&id, slot)),
                    brightness: Some(brightness_path(&id)),
                    reset: reset_path(&id),
                    current,
                    id,
                }
            })
            .collect();
        out.sort_by(|a, b| a.id.cmp(&b.id));
        out
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

/// Maps an HTTP status back to the errno the field endpoints report (`SimHttpServer.StatusForErrno`).
fn errno_from_body(resp: &ureq::Response) -> String {
    // The field endpoints return `{errno,message}`; fall back to the status text.
    let status = resp.status();
    match status {
        400 => "EINVAL".into(),
        403 => "EACCES".into(),
        404 => "ENOENT".into(),
        409 => "EBUSY".into(),
        500 => "EIO".into(),        // a KSA call threw
        501 => "EOPNOTSUPP".into(), // the plume family's accessor is latched degraded
        504 => "ETIMEDOUT".into(),  // the game thread did not drain in command_timeout_ms
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
    /// (Re)scan the plume-template roster and report the catalog + health.
    Discover,
    /// Set the templates the worker reads back (sent on entering the show screen) so the read-back row
    /// populates before a show starts. The reset action targets the same set.
    Watch { templates: Vec<String> },
    /// Begin (or, if already running, re-target/re-plan) the show over the given armed template ids.
    /// An empty palette or no matching templates is a no-op error.
    Start {
        templates: Vec<String>,
        plan: Plan,
        hz: f64,
        batch: bool,
    },
    /// Live-edit the running show's plan / frame rate / dispatch mode without resetting its clock.
    Update { plan: Plan, hz: f64, batch: bool },
    /// Restore every watched template to its pristine values **without** stopping the show.
    ResetTemplates,
    /// Read one template's current `emission/color0..3` (the `c` capture action).
    Capture { id: String },
    /// Stop the show and restore every armed template to pristine.
    Stop,
}

/// A reply from the worker to the UI thread.
pub enum FromWorker {
    /// The discovered catalog + health (answer to [`ToWorker::Discover`]).
    Catalog {
        templates: Vec<PlumeTemplate>,
        health: Health,
    },
    /// What the game reports back for the first watched template (`None` when nothing is armed).
    Readback {
        id: Option<String>,
        values: TemplateValues,
        armed: usize,
    },
    /// A throttled live frame of the running show (drives the preview band + write counter).
    Tick {
        /// The four gradient-slot colours of the LEAD template this frame (the live band).
        slots: [Rgb; SLOTS],
        segment: u64,
        /// The brightness written this frame, or `None` when the effect is off.
        brightness: Option<f64>,
        /// Armed templates.
        targets: usize,
        /// Writes dispatched since this show started, and how many are still in flight right now.
        writes: u64,
        inflight: usize,
    },
    /// The show stopped (templates reset); carries any error from the reset writes.
    Stopped { error: Option<String> },
    /// A standalone reset completed; carries the first write error, if any.
    ResetDone { error: Option<String> },
    /// One template's captured gradient stops (answer to [`ToWorker::Capture`]).
    Captured { id: String, colors: Vec<Rgb> },
    /// A start request that couldn't run (no templates / empty palette / debug namespace off).
    Refused(String),
}

/// The live state of a running show on the worker side: the clock, the plan, the resolved templates,
/// and the last colour/brightness actually written (for per-leaf frame-level dedupe).
struct RunningShow {
    start: Instant,
    plan: Plan,
    /// Dispatch the frame as one `/sim/ctl/batch` group when it changes ≥ 2 leaves.
    batch: bool,
    /// Resolved from the catalog by armed id.
    templates: Vec<PlumeTemplate>,
    /// Last colour wire-form written per (template, slot). With no stagger every template holds the
    /// same four values; with stagger they diverge as the scroll ripples.
    color_seen: Vec<[Option<String>; SLOTS]>,
    /// Last brightness wire-form written per template. Stays `None` forever while the effect is off,
    /// because nothing is ever written.
    bright_seen: Vec<Option<String>>,
    /// Writes dispatched since this show started (colours + brightness, across all templates).
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
/// armed while a show is running), and the read-back poll timer.
async fn worker_loop(
    source: Arc<dyn Source>,
    mut rx: UnboundedReceiver<ToWorker>,
    tx: ToUi,
    mut hz: f64,
) {
    let mut state = Worker {
        catalog: Vec::new(),
        watch: Vec::new(),
        show: None,
        health: Health::default(),
        inflight: Arc::new(AtomicUsize::new(0)),
    };

    let mut frame = tokio::time::interval(hz_period(hz));
    frame.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut poll = tokio::time::interval(Duration::from_millis(1000));

    // The UI doesn't need 60 ticks/sec; throttle preview updates to ~15 Hz.
    let ui_min_gap = Duration::from_millis(66);
    let mut last_ui = Instant::now() - ui_min_gap;

    loop {
        tokio::select! {
            cmd = rx.recv() => {
                let Some(cmd) = cmd else { break }; // UI dropped the sender → exit
                let prev_hz = hz;
                handle(cmd, &source, &tx, &mut state, &mut hz).await;
                if (hz - prev_hz).abs() > f64::EPSILON {
                    frame = tokio::time::interval(hz_period(hz));
                    frame.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
                }
            }
            _ = frame.tick(), if state.show.is_some() => {
                let inflight = state.inflight.clone();
                let rs = state.show.as_mut().expect("guard ensures Some");
                let elapsed = rs.start.elapsed().as_secs_f64() * 1000.0;
                // The preview band mirrors the LEAD template's four gradient stops, so it is
                // literally a picture of the plume.
                let mut slots = [Rgb::WHITE; SLOTS];
                let mut segment = 0u64;
                for (slot, cell) in slots.iter_mut().enumerate() {
                    let (c, seg) = rs.plan.slot_color_at(0, slot, elapsed);
                    *cell = c;
                    if slot == 0 {
                        segment = seg;
                    }
                }
                let brightness = rs.plan.brightness_at(0, elapsed);

                // Compute the writes that actually changed this frame, then fire them and forget —
                // we never await the result.
                let writes = frame_writes(rs, elapsed);
                rs.writes += writes.len() as u64;
                dispatch(&source, &inflight, writes, rs.batch);

                if last_ui.elapsed() >= ui_min_gap {
                    last_ui = Instant::now();
                    let _ = tx.send(FromWorker::Tick {
                        slots,
                        segment,
                        brightness,
                        targets: rs.templates.len(),
                        writes: rs.writes,
                        inflight: inflight.load(Ordering::Relaxed),
                    });
                }
            }
            _ = poll.tick() => {
                let (id, values, armed) = read_readback(&source, &state.watch).await;
                let _ = tx.send(FromWorker::Readback { id, values, armed });
            }
        }
    }
}

/// Everything the worker owns across commands (kept in one struct so `handle` stays readable — dancy
/// passed these as seven separate `&mut`s and needed `#[allow(clippy::too_many_arguments)]`).
struct Worker {
    catalog: Vec<PlumeTemplate>,
    watch: Vec<String>,
    show: Option<RunningShow>,
    health: Health,
    inflight: Arc<AtomicUsize>,
}

/// Handles one UI command. Heavy/blocking work (discover, reads, resets) is offloaded to the blocking
/// pool and awaited; the hot animation path never goes through here.
async fn handle(
    cmd: ToWorker,
    source: &Arc<dyn Source>,
    tx: &ToUi,
    state: &mut Worker,
    hz: &mut f64,
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
            state.catalog = cat.clone();
            state.health = health;
            let _ = tx.send(FromWorker::Catalog {
                templates: cat,
                health,
            });
        }
        ToWorker::Watch { templates } => {
            state.watch = templates;
            // Read once immediately so the row populates without waiting a full poll interval.
            let (id, values, armed) = read_readback(source, &state.watch).await;
            let _ = tx.send(FromWorker::Readback { id, values, armed });
        }
        ToWorker::Start {
            templates,
            plan,
            hz: new_hz,
            batch,
        } => {
            *hz = new_hz.clamp(1.0, 240.0);
            state.watch = templates.clone();
            let targets = resolve_targets(&state.catalog, &templates);
            if plan.colors.is_empty() {
                let _ = tx.send(FromWorker::Refused("add at least one colour first".into()));
            } else if targets.is_empty() {
                let _ = tx.send(FromWorker::Refused(if state.health.debug {
                    "no templates armed".into()
                } else {
                    "the /sim/debug namespace is off \u{2014} set [control] debug_namespace = true in gatos.toml"
                        .into()
                }));
            } else {
                state.show = Some(RunningShow {
                    start: Instant::now(),
                    plan,
                    batch,
                    color_seen: vec![[const { None }; SLOTS]; targets.len()],
                    bright_seen: vec![None; targets.len()],
                    templates: targets,
                    writes: 0,
                });
            }
        }
        ToWorker::Update {
            plan,
            hz: new_hz,
            batch,
        } => {
            *hz = new_hz.clamp(1.0, 240.0);
            if let Some(rs) = state.show.as_mut() {
                rs.plan = plan; // keep the clock running so the scroll doesn't jump
                rs.batch = batch;
            }
        }
        ToWorker::ResetTemplates => {
            let paths = reset_paths(&state.catalog, &state.watch);
            if paths.is_empty() {
                let _ = tx.send(FromWorker::ResetDone {
                    error: Some("no templates armed".into()),
                });
            } else {
                let s = source.clone();
                let batch = state.show.as_ref().is_none_or(|rs| rs.batch);
                let error = tokio::task::spawn_blocking(move || reset_templates(&*s, &paths, batch))
                    .await
                    .unwrap_or_else(|_| Some("reset dispatch failed".into()));
                let _ = tx.send(FromWorker::ResetDone { error });
            }
        }
        ToWorker::Capture { id } => {
            let s = source.clone();
            let key = id.clone();
            let values = tokio::task::spawn_blocking(move || s.read_values(&key))
                .await
                .ok()
                .flatten();
            let colors: Vec<Rgb> = values
                .map(|v| v.colors.iter().flatten().copied().collect())
                .unwrap_or_default();
            let _ = tx.send(FromWorker::Captured { id, colors });
        }
        ToWorker::Stop => {
            if let Some(rs) = state.show.take() {
                // Briefly let in-flight writes drain so a stale queued colour can't land *after* the
                // reset and leave a plume stuck mid-show. Then reset synchronously.
                drain_inflight(&state.inflight).await;
                let s = source.clone();
                let paths: Vec<String> = rs.templates.iter().map(|t| t.reset.clone()).collect();
                let error = tokio::task::spawn_blocking(move || reset_templates(&*s, &paths, rs.batch))
                    .await
                    .unwrap_or_else(|_| Some("reset dispatch failed".into()));
                let _ = tx.send(FromWorker::Stopped { error });
            }
        }
    }
}

/// Reads the **first** watched template's current values (one small `json` read, off the blocking
/// pool) for the read-back row. Returns `(None, default, 0)` when nothing is armed.
///
/// Caveat worth remembering: gatOS re-samples the FX surface when a `/sim` write lands (immediately)
/// or every **2 s** otherwise, and the values are 32-bit floats game-side — so an idle read-back can
/// be up to 2 s stale and `0.6` reads back as `0.60000002`.
async fn read_readback(
    source: &Arc<dyn Source>,
    watch: &[String],
) -> (Option<String>, TemplateValues, usize) {
    let Some(first) = watch.first().cloned() else {
        return (None, TemplateValues::default(), 0);
    };
    let armed = watch.len();
    let s = source.clone();
    let id = first.clone();
    let values = tokio::task::spawn_blocking(move || s.read_values(&id))
        .await
        .ok()
        .flatten()
        .unwrap_or_default();
    (Some(first), values, armed)
}

/// Waits (bounded) for fire-and-forget writes to drain, so a clean stop resets from a quiet state.
async fn drain_inflight(inflight: &Arc<AtomicUsize>) {
    let deadline = Instant::now() + Duration::from_secs(2);
    while inflight.load(Ordering::Relaxed) > 0 && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(2)).await;
    }
}

/// The catalog entries whose id is in `selected`, in catalog order.
fn resolve_targets(catalog: &[PlumeTemplate], selected: &[String]) -> Vec<PlumeTemplate> {
    catalog
        .iter()
        .filter(|t| selected.iter().any(|s| s == &t.id))
        .cloned()
        .collect()
}

/// The `reset` trigger paths of every catalog template whose id is in `selected`.
fn reset_paths(catalog: &[PlumeTemplate], selected: &[String]) -> Vec<String> {
    resolve_targets(catalog, selected)
        .into_iter()
        .map(|t| t.reset)
        .collect()
}

/// The per-frame writes that actually CHANGED, as `(path, value)` pairs, recorded as seen (per-leaf
/// dedupe — a static palette re-broadcasts nothing). Ordered slot 0..3 then brightness, per template.
/// Pure: the caller dispatches the returned pairs.
fn frame_writes(rs: &mut RunningShow, elapsed_ms: f64) -> Vec<(String, String)> {
    let mut out = Vec::new();
    for t in 0..rs.templates.len() {
        for slot in 0..SLOTS {
            let path = &rs.templates[t].colors[slot];
            if path.is_empty() {
                continue; // this stop's leaf was missing at discovery
            }
            let wire = rs.plan.slot_color_at(t, slot, elapsed_ms).0.to_sim();
            if rs.color_seen[t][slot].as_deref() != Some(wire.as_str()) {
                out.push((path.clone(), wire.clone()));
                rs.color_seen[t][slot] = Some(wire);
            }
        }
        // `None` means the brightness effect is off: skip the leaf entirely so the template keeps its
        // authored emissive brightness (this is NOT "write 0").
        let (Some(v), Some(path)) = (
            rs.plan.brightness_at(t, elapsed_ms),
            rs.templates[t].brightness.clone(),
        ) else {
            continue;
        };
        let wire = fmt_num(v);
        if rs.bright_seen[t].as_deref() != Some(wire.as_str()) {
            out.push((path, wire.clone()));
            rs.bright_seen[t] = Some(wire);
        }
    }
    out
}

/// Dispatches one frame's writes fire-and-forget.
///
/// A frame that changes ≥ 2 leaves goes out as ONE `/sim/ctl/batch` group (SPEC §3.10): one write →
/// one command group → ONE game-tick drain, so all four gradient stops move together and the plume
/// never renders a torn gradient. `BatchFile` caps a group at 64 commands, so we chunk. A single-leaf
/// frame skips the batch overhead, and `--no-batch` reverts to dancy-party-rs's per-leaf dispatch.
fn dispatch(
    source: &Arc<dyn Source>,
    inflight: &Arc<AtomicUsize>,
    writes: Vec<(String, String)>,
    batch: bool,
) {
    match writes.len() {
        0 => {}
        1 => dispatch_one(source, inflight, writes.into_iter().next().expect("len 1")),
        _ if batch => {
            for chunk in writes.chunks(BATCH_MAX) {
                dispatch_one(
                    source,
                    inflight,
                    (BATCH_PATH.to_string(), batch_payload(chunk)),
                );
            }
        }
        _ => {
            for w in writes {
                dispatch_one(source, inflight, w);
            }
        }
    }
}

/// One fire-and-forget write on tokio's blocking pool. The `inflight` gauge exists only so a clean
/// stop can drain before resetting; the result is deliberately dropped.
fn dispatch_one(
    source: &Arc<dyn Source>,
    inflight: &Arc<AtomicUsize>,
    (path, value): (String, String),
) {
    inflight.fetch_add(1, Ordering::Relaxed);
    let s = source.clone();
    let infl = inflight.clone();
    tokio::task::spawn_blocking(move || {
        let _ = s.write(&path, &value);
        infl.fetch_sub(1, Ordering::Relaxed);
    });
}

/// The `/sim/ctl/batch` payload: one `<path> <value>` line per write, terminated by `commit`.
/// Paths are `/sim`-relative and contain no whitespace (id sanitization guarantees it), so the
/// server's "the path ends at the first space" rule is safe. Every FX write is Frame phase, so the
/// group never mixes phases.
fn batch_payload(writes: &[(String, String)]) -> String {
    let mut s = String::with_capacity(writes.len() * 64 + 8);
    for (p, v) in writes {
        s.push_str(p);
        s.push(' ');
        s.push_str(v);
        s.push('\n');
    }
    s.push_str("commit\n");
    s
}

/// Restores every armed template to its pristine (pre-gatOS) values — the "CUT THE FIRE" cleanup.
/// Written synchronously through the source AFTER the fire-and-forget pool has drained, so the reset
/// is guaranteed to land last and is durable before the `Stopped` ack.
///
/// This never hand-restores colours: `reset` (`debug.engineplume_reset`) replays the values gatOS
/// captured on its *first* write to each field and drops the record — the mod is the only thing that
/// knows the originals. A reset with nothing recorded is a successful no-op.
///
/// Uses `/sim/ctl/batch` when > 1 template is armed, so every plume in the show snaps back in the SAME
/// game tick. `reset` is a trigger: the value is `1`.
fn reset_templates(source: &dyn Source, paths: &[String], batch: bool) -> Option<String> {
    if paths.len() > 1 && batch {
        let mut first_err = None;
        for chunk in paths.chunks(BATCH_MAX) {
            let group: Vec<(String, String)> =
                chunk.iter().map(|p| (p.clone(), "1".to_string())).collect();
            if let Err(e) = source.write(BATCH_PATH, &batch_payload(&group)) {
                first_err.get_or_insert(describe(&e));
            }
        }
        return first_err;
    }
    let mut first_err: Option<String> = None;
    for p in paths {
        if let Err(e) = source.write(p, "1") {
            first_err.get_or_insert(describe(&e));
        }
    }
    first_err
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A `/sim`-shaped fixture: `Kerolox` is healthy (all four stops + brightness + json + reset),
    /// `Hydrolox` is the same, `SolidBooster` has only two stops and no brightness.
    fn fixture(tag: &str) -> (PathBuf, FsSource) {
        let root = std::env::temp_dir().join(format!("nyan_{tag}_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        for (id, stops, bright) in [
            ("Kerolox", 4, true),
            ("Hydrolox", 4, true),
            ("SolidBooster", 2, false),
        ] {
            let dir = root.join(format!("debug/engineplume/templates/{id}"));
            fs::create_dir_all(dir.join("emission")).unwrap();
            for slot in 0..stops {
                fs::write(dir.join(format!("emission/color{slot}")), "0 0 0\n").unwrap();
            }
            if bright {
                fs::write(dir.join("emission/brightness"), "12.5\n").unwrap();
            }
            fs::write(dir.join("reset"), "0\n").unwrap();
            fs::write(
                dir.join("json"),
                // The `Formats.FxFields` shape: arity-1 fields are bare numbers, vectors are arrays.
                r#"{"emission/brightness":12.5,"emission/color0":[1,0.6,0],"emission/color1":[1,1,0]}"#,
            )
            .unwrap();
        }
        fs::create_dir_all(root.join("time")).unwrap();
        fs::create_dir_all(root.join("ctl")).unwrap();
        (root.clone(), FsSource::new(root))
    }

    #[test]
    fn fs_discovery_finds_templates_and_leaves() {
        let (root, s) = fixture("disc");
        let cat = s.discover();
        assert_eq!(cat.len(), 3);
        let k = cat.iter().find(|t| t.id == "Kerolox").unwrap();
        assert_eq!(k.stops(), 4);
        assert_eq!(k.colors[0], "debug/engineplume/templates/Kerolox/emission/color0");
        assert_eq!(k.colors[3], "debug/engineplume/templates/Kerolox/emission/color3");
        assert_eq!(
            k.brightness.as_deref(),
            Some("debug/engineplume/templates/Kerolox/emission/brightness")
        );
        assert_eq!(k.reset, "debug/engineplume/templates/Kerolox/reset");
        // The fixture is /sim-shaped enough to read as connected, controlled and debug-enabled.
        let h = s.health();
        assert!(h.connected && h.control && h.debug);
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn fs_discovery_marks_a_template_with_missing_stops() {
        let (root, s) = fixture("partial");
        let cat = s.discover();
        let b = cat.iter().find(|t| t.id == "SolidBooster").unwrap();
        assert_eq!(b.stops(), 2); // only color0/color1 exist
        assert!(b.colors[2].is_empty() && b.colors[3].is_empty());
        assert!(b.brightness.is_none());
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn fs_read_values_parses_the_json_document() {
        let (root, s) = fixture("vals");
        let v = s.read_values("Kerolox").unwrap();
        assert_eq!(v.colors[0], Some(Rgb::new(1.0, 0.6, 0.0)));
        assert_eq!(v.colors[1], Some(Rgb::new(1.0, 1.0, 0.0)));
        assert_eq!(v.colors[2], None); // absent from the document
        assert_eq!(v.brightness, Some(12.5));
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn read_values_accepts_the_snapshot_array_shape() {
        // /v1/snapshot writes EVERY field value as an array (double[]), even scalars — one parser,
        // both shapes.
        let v = parse_values(r#"{"emission/brightness":[12.5],"emission/color0":[1,0,0]}"#).unwrap();
        assert_eq!(v.brightness, Some(12.5));
        assert_eq!(v.colors[0], Some(Rgb::new(1.0, 0.0, 0.0)));
    }

    #[test]
    fn sanitize_segment_matches_the_server() {
        assert_eq!(sanitize_segment("Kerolox"), "Kerolox");
        assert_eq!(sanitize_segment("My Template!"), "My_Template_");
        assert_eq!(sanitize_segment("a.b-c_d"), "a.b-c_d");
    }

    fn show(plan: Plan, templates: usize, batch: bool) -> RunningShow {
        let tpls: Vec<PlumeTemplate> = (0..templates)
            .map(|n| {
                let id = format!("T{n}");
                PlumeTemplate {
                    colors: std::array::from_fn(|slot| color_path(&id, slot)),
                    brightness: Some(brightness_path(&id)),
                    reset: reset_path(&id),
                    current: None,
                    id,
                }
            })
            .collect();
        RunningShow {
            start: Instant::now(),
            plan,
            batch,
            color_seen: vec![[const { None }; SLOTS]; tpls.len()],
            bright_seen: vec![None; tpls.len()],
            templates: tpls,
            writes: 0,
        }
    }

    #[test]
    fn frame_writes_dedupes_and_skips_brightness_when_off() {
        // A single-colour palette: every stop resolves to the same colour forever, and the default
        // plan leaves emission/brightness alone entirely.
        let mut rs = show(Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000), 1, true);
        let first = frame_writes(&mut rs, 0.0);
        assert_eq!(first.len(), SLOTS); // four stops, no brightness
        assert!(first.iter().all(|(p, _)| p.contains("emission/color")));
        // Nothing changed -> nothing written (per-leaf dedupe).
        assert!(frame_writes(&mut rs, 100.0).is_empty());
        assert!(frame_writes(&mut rs, 5000.0).is_empty());
    }

    #[test]
    fn frame_writes_emits_four_stops_on_a_segment_boundary() {
        // Six hard-cut stripes: at each stripe boundary the whole window scrolls, so all four stops
        // change at once — exactly the frame that must go out as ONE batch.
        let palette: Vec<Rgb> = (0..6).map(|i| Rgb::new(i as f64 / 6.0, 0.25, 0.5)).collect();
        let mut rs = show(Plan::new(palette, 200).with_steps(1), 1, true);
        assert_eq!(frame_writes(&mut rs, 0.0).len(), SLOTS);
        assert!(frame_writes(&mut rs, 100.0).is_empty()); // mid-segment: hard cut holds
        let w = frame_writes(&mut rs, 200.0);
        assert_eq!(w.len(), SLOTS);
        for (slot, (path, _)) in w.iter().enumerate() {
            assert!(path.ends_with(&format!("emission/color{slot}")));
        }
        // Brightness on: one extra write per template once it is enabled.
        rs.plan = rs.plan.clone().with_brightness(10.0, 10.0, 500, 0);
        let w = frame_writes(&mut rs, 400.0);
        assert_eq!(w.len(), SLOTS + 1);
        assert!(w.last().unwrap().0.ends_with("emission/brightness"));
        assert_eq!(w.last().unwrap().1, "10");
    }

    #[test]
    fn batch_payload_is_path_value_lines_then_commit() {
        let group = vec![
            ("debug/engineplume/templates/K/emission/color0".to_string(), "1 0 0".to_string()),
            ("debug/engineplume/templates/K/emission/color1".to_string(), "1 0.6 0".to_string()),
        ];
        assert_eq!(
            batch_payload(&group),
            "debug/engineplume/templates/K/emission/color0 1 0 0\n\
             debug/engineplume/templates/K/emission/color1 1 0.6 0\n\
             commit\n"
        );
    }

    #[test]
    fn errno_hints_distinguish_the_two_gates() {
        // debug_namespace off is ENOENT (the node is ABSENT); control disabled is EACCES.
        assert!(hint("ENOENT").unwrap().contains("debug_namespace"));
        assert!(hint("EACCES").unwrap().contains("enabled = false"));
        assert!(hint("EBUSY").is_none());
    }

    #[test]
    fn worker_discovers_runs_and_resets_end_to_end() {
        let (root, _) = fixture("e2e");
        let color0 = root.join("debug/engineplume/templates/Kerolox/emission/color0");
        let reset = root.join("debug/engineplume/templates/Kerolox/reset");

        let (cmd_tx, cmd_rx) = tokio::sync::mpsc::unbounded_channel::<ToWorker>();
        let (up_tx, up_rx) = std::sync::mpsc::channel::<FromWorker>();
        spawn_worker(Arc::new(FsSource::new(&root)), 60.0, cmd_rx, up_tx);

        // Discover -> Catalog with the three fixture templates.
        cmd_tx.send(ToWorker::Discover).unwrap();
        let cat = recv_catalog(&up_rx);
        assert_eq!(cat.len(), 3);

        // Start a solid-red show over one template; wait for a live tick, then (the write is
        // fire-and-forget, and goes out as one ctl/batch group) poll until color0 holds red.
        cmd_tx
            .send(ToWorker::Start {
                templates: vec!["Kerolox".into()],
                plan: Plan::new(vec![Rgb::new(1.0, 0.0, 0.0)], 1000),
                hz: 60.0,
                // Per-leaf dispatch: the fixture is a plain directory, so there is no ctl/batch
                // handler to interpret a group — the real mount has one.
                batch: false,
            })
            .unwrap();
        wait_for_tick(&up_rx);
        assert!(
            wait_for_file(&color0, "1 0 0"),
            "fire-and-forget colour write should land"
        );

        // Stop -> every armed template's `reset` trigger is written.
        cmd_tx.send(ToWorker::Stop).unwrap();
        loop {
            match up_rx.recv_timeout(Duration::from_secs(3)).unwrap() {
                FromWorker::Stopped { error } => {
                    assert!(error.is_none(), "reset failed: {error:?}");
                    break;
                }
                _ => continue,
            }
        }
        assert_eq!(fs::read_to_string(&reset).unwrap().trim(), "1");

        drop(cmd_tx); // worker exits
        let _ = fs::remove_dir_all(&root);
    }

    fn recv_catalog(rx: &std::sync::mpsc::Receiver<FromWorker>) -> Vec<PlumeTemplate> {
        loop {
            match rx.recv_timeout(Duration::from_secs(3)).unwrap() {
                FromWorker::Catalog { templates, .. } => return templates,
                _ => continue,
            }
        }
    }

    fn wait_for_tick(rx: &std::sync::mpsc::Receiver<FromWorker>) {
        loop {
            if let FromWorker::Tick { .. } = rx.recv_timeout(Duration::from_secs(3)).unwrap() {
                return;
            }
        }
    }

    /// Polls a file until it trims to `want`, or times out (fire-and-forget writes land async).
    fn wait_for_file(path: &std::path::Path, want: &str) -> bool {
        let deadline = Instant::now() + Duration::from_secs(3);
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
