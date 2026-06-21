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
use std::sync::mpsc::{Receiver, RecvTimeoutError, Sender};
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

/// A read/write/discover interface over the `/sim` light surface. Implementations run on the worker
/// thread only.
pub trait Source: Send {
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
    last_color_wire: Option<String>,
    last_goal: Option<u8>,
}

/// Spawns the worker thread. It owns the source for its whole life; the returned channels are the
/// only way to talk to it. Dropping the `ToWorker` sender makes the worker exit.
pub fn spawn_worker(
    source: Box<dyn Source>,
    hz: f64,
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

        loop {
            if let Some(rp) = party.as_mut() {
                // --- partying: render one frame, then wait up to one frame for a command ---
                let elapsed = rp.start.elapsed().as_secs_f64() * 1000.0;
                let frame = rp.plan.frame(elapsed);
                let err = write_frame(&*source, rp, &frame);

                if last_ui.elapsed() >= ui_min_gap {
                    last_ui = Instant::now();
                    let _ = tx.send(FromWorker::Tick {
                        color: frame.color,
                        segment: frame.segment,
                        goal: frame.goal,
                        targets: rp.color_paths.len(),
                        error: err,
                    });
                }

                match rx.recv_timeout(frame_dt) {
                    Ok(cmd) => {
                        if handle(cmd, &*source, &mut catalog, &mut party, &tx) {
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
                        if handle(cmd, &*source, &mut catalog, &mut party, &tx) {
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
    source: &dyn Source,
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
                *party = Some(RunningParty {
                    start: Instant::now(),
                    plan,
                    color_paths,
                    goal_paths,
                    last_color_wire: None,
                    last_goal: None,
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
                let err = reset_lights(source, &rp);
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

/// Writes one frame to every target: the color to all `color` paths (only when the quantized wire
/// form changed since last frame — a cheap global dedupe, not per-light tracking) and, on a segment
/// boundary, the new goal to all `goal` paths. Returns the first write error seen, if any.
fn write_frame(source: &dyn Source, rp: &mut RunningParty, frame: &crate::party::Frame) -> Option<String> {
    let mut first_err: Option<String> = None;

    let wire = frame.color.to_sim();
    if rp.last_color_wire.as_deref() != Some(wire.as_str()) {
        for p in &rp.color_paths {
            if let Err(e) = source.write(p, &wire) {
                first_err.get_or_insert(format!("{}: {}", e.errno, e.message));
            }
        }
        rp.last_color_wire = Some(wire);
    }

    if rp.last_goal != Some(frame.goal) {
        let g = frame.goal.to_string();
        for p in &rp.goal_paths {
            if let Err(e) = source.write(p, &g) {
                first_err.get_or_insert(format!("{}: {}", e.errno, e.message));
            }
        }
        rp.last_goal = Some(frame.goal);
    }

    first_err
}

/// Resets every targeted light to white with goal 0 — the "STOP, MY EYES" cleanup.
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

    #[test]
    fn write_frame_dedupes_color_and_writes_goal_on_flip() {
        let src = MockSource {
            catalog: vec![],
            writes: Mutex::new(Vec::new()),
        };
        let mut rp = RunningParty {
            start: Instant::now(),
            plan: Plan::new(vec![Rgb::WHITE], 1000),
            color_paths: vec!["v/0/color".into(), "v/1/color".into()],
            goal_paths: vec!["v/0/goal".into()],
            last_color_wire: None,
            last_goal: None,
        };
        let f0 = crate::party::Frame {
            color: Rgb::new(1.0, 0.0, 0.0),
            segment: 0,
            goal: 1,
        };
        write_frame(&src, &mut rp, &f0);
        // First frame: both colors + the one goal.
        assert_eq!(src.writes.lock().unwrap().len(), 3);

        // Same color, same goal -> no further writes (global dedupe).
        write_frame(&src, &mut rp, &f0);
        assert_eq!(src.writes.lock().unwrap().len(), 3);

        // Goal flips -> one goal write, color unchanged so still deduped.
        let f1 = crate::party::Frame {
            color: Rgb::new(1.0, 0.0, 0.0),
            segment: 1,
            goal: 0,
        };
        write_frame(&src, &mut rp, &f1);
        assert_eq!(src.writes.lock().unwrap().len(), 4);
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
        spawn_worker(Box::new(FsSource::new(&root)), 60.0, cmd_rx, up_tx);

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
