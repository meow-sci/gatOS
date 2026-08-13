//! thug — slide thug-life glasses onto a kitten vessel, the trivial way.
//!
//! The gatOS mod renders the glasses as a part-anchored textured quad (`/sim/debug/thug_life/`,
//! needs `debug_namespace = true`). This CLI wraps the whole ceremony — find the vessel's root
//! part, create (or reuse) the entry, then animate the glasses sliding down onto the face:
//!
//! ```text
//! thug Hunter                 # shades on Hunter, all defaults
//! thug                        # shades on EVERY kitten currently in the world
//! thug Hunter Polaris Banjo   # the whole squad, animated together
//! thug --time 3 --easing linear --scale 1.5 Hunter
//! thug --off Hunter           # slide them off and remove the entry
//! ```
//!
//! Defaults are tuned for the EVA kittens: the glasses land at (0.23, 0, -0.33) in the root part's
//! frame, rotated (90, 180, 90) degrees, sized 0.9 × 0.22 m — the same numbers the original
//! `examples/thug-life/thug.ts` used. Everything is overridable.
//!
//! **Sources.** The `/sim` mount is the default and the preferred one: run inside the gatOS guest
//! (or anywhere the 9p export is mounted) and no flags are needed. `--sim <path>` / `$GATOS_SIM`
//! move the root; `--url <base>` / `$GATOS_HTTP` switch to the mod's HTTP `/v1/fs/<path>` mirror
//! instead — the host-side dev path. The guest login shell *presets* `$GATOS_HTTP` whenever the
//! host serves HTTP, so it is only consulted when no mount is there to use: in the guest the 9p
//! mount is both faster and always in sync with what the mod is serving.
//!
//! `$GATOS_HTTP` is the `/v1` base (`http://sim:4242/v1`), so `--url` accepts a base with or
//! without the `/v1` suffix; both address `/v1/fs/<path>`.
//!
//! Directory listings exist only on the mount (`/v1/fs` serves leaves, not directories), so over
//! HTTP entry discovery probes `<id>/vessel` leaves by number and the vessel roster comes from
//! `GET /v1/vessels` instead of the `vessels/by-id` listing.

use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};
use std::thread::sleep;
use std::time::{Duration, Instant};

// ---- resting pose (the thug.ts numbers, now defaults rather than hard-code) --------------------

const FINAL_X: f64 = 0.23;
const FINAL_Y: f64 = 0.0;
const FINAL_Z: f64 = -0.33;
const DEFAULT_ROT: (f64, f64, f64) = (90.0, 180.0, 90.0);
const DEFAULT_W: f64 = 0.9;
const DEFAULT_H: f64 = 0.22;

/// Where the 9p export is mounted in the guest.
const DEFAULT_SIM: &str = "/sim";

/// Highest entry id probed when discovering existing entries over a source with no directory
/// listing (ids reuse the lowest free slot, so live ids are dense near zero; 64 is far beyond
/// anything sane).
const MAX_PROBE_ID: u32 = 64;

fn main() {
    let config = match Config::from_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("thug: {e}");
            eprintln!("try: thug --help");
            std::process::exit(2);
        }
    };
    if config.help {
        print_help();
        return;
    }

    let source = pick_source(&config);
    let source = source.as_ref();

    // Fail on the *transport* before blaming a vessel: an unmounted /sim and a missing kitten look
    // identical one leaf at a time, and the first one is the one that keeps happening.
    if source.read("time/ut").is_none() {
        eprintln!("thug: cannot reach the sim through {} (time/ut unreadable).", source.label());
        eprintln!("      in the guest: is /sim mounted, and is the mod loaded? (mount | grep /sim)");
        eprintln!("      over HTTP: gatos.toml needs http_enabled + http_field_endpoints = true");
        std::process::exit(1);
    }
    if source.read("debug/thug_life/count").is_none() {
        eprintln!("thug: {} has no debug/thug_life surface.", source.label());
        eprintln!("      set debug_namespace = true in gatos.toml and reload the mod.");
        std::process::exit(1);
    }

    let vessels = if config.vessels.is_empty() {
        let kittens = discover_kittens(source);
        if kittens.is_empty() {
            eprintln!("thug: no vessel ids given and no kittens found (is_kitten reads empty).");
            eprintln!("      name a vessel: thug Hunter");
            std::process::exit(1);
        }
        eprintln!("thug: found kittens: {}", kittens.join(", "));
        kittens
    } else {
        config.vessels.clone()
    };

    // Resolve every vessel to a live entry id first, so the squad animates in lockstep.
    let mut entries: BTreeMap<String, u32> = BTreeMap::new();
    for vessel in &vessels {
        match ensure_entry(source, vessel, &config) {
            Ok(id) => {
                entries.insert(vessel.clone(), id);
            }
            Err(e) => {
                eprintln!("thug: {vessel}: {e}");
                std::process::exit(1);
            }
        }
    }

    let wrote = animate(source, &entries, &config);

    if config.off {
        for (vessel, id) in &entries {
            match source.write(&format!("debug/thug_life/{id}/remove"), "1") {
                Ok(()) => eprintln!("thug: {vessel}: glasses removed."),
                Err(e) => eprintln!("thug: {vessel}: remove failed: {e}"),
            }
        }
    } else if wrote {
        eprintln!("thug: deal with it.");
    }

    if !wrote {
        std::process::exit(1);
    }
}

// ================================================================================================
//  Config / args
// ================================================================================================

struct Config {
    help: bool,
    vessels: Vec<String>,
    /// Drop-in distance above the face, metres (start offset along the part's +Z).
    meters: f64,
    /// Animation duration, seconds.
    time: f64,
    /// Write rate, Hz.
    hz: f64,
    easing: Easing,
    /// Quad size, metres.
    width: f64,
    height: f64,
    /// Anchor part instance id; None = the vessel's root part (`parts/0/instance_id`).
    part: Option<u32>,
    /// Which render passes draw the quad: `all`, or tokens of main/crew/other. None = leave default.
    cameras: Option<String>,
    /// Reverse: slide the glasses off and remove the entry.
    off: bool,
    /// Explicit `--sim` root; None = the env/mount defaults (see [`pick_source`]).
    sim: Option<PathBuf>,
    /// Explicit `--url` base; None = the env/mount defaults (see [`pick_source`]).
    url: Option<String>,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut config = Config {
            help: false,
            vessels: Vec::new(),
            meters: 1.5,
            time: 1.2,
            hz: 60.0,
            easing: Easing::EaseOut,
            width: DEFAULT_W,
            height: DEFAULT_H,
            part: None,
            cameras: None,
            off: false,
            sim: None,
            url: None,
        };
        let mut scale: Option<f64> = None;
        let mut explicit_size = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            let mut take = |name: &str| -> Result<String, String> {
                args.next().ok_or_else(|| format!("{name} needs a value"))
            };
            match arg.as_str() {
                "-h" | "--help" => config.help = true,
                "--meters" | "-m" => config.meters = parse_f64(&take("--meters")?, "--meters")?,
                "--time" | "-t" => config.time = parse_f64(&take("--time")?, "--time")?,
                "--hz" => config.hz = parse_f64(&take("--hz")?, "--hz")?,
                "--easing" | "-e" => config.easing = Easing::parse(&take("--easing")?)?,
                "--scale" | "-s" => scale = Some(parse_f64(&take("--scale")?, "--scale")?),
                "--width" => {
                    config.width = parse_f64(&take("--width")?, "--width")?;
                    explicit_size = true;
                }
                "--height" => {
                    config.height = parse_f64(&take("--height")?, "--height")?;
                    explicit_size = true;
                }
                "--part" => config.part = Some(
                    take("--part")?.parse::<u32>().map_err(|_| "--part needs a part instance id")?,
                ),
                "--cameras" | "-c" => {
                    let value = take("--cameras")?.replace(',', " ");
                    let ok = !value.trim().is_empty()
                        && value.split_whitespace().all(|t| {
                            matches!(t.to_lowercase().as_str(), "all" | "main" | "crew" | "other")
                        });
                    if !ok {
                        return Err(format!(
                            "--cameras needs 'all' or tokens of main/crew/other, got '{value}'"
                        ));
                    }
                    config.cameras = Some(value.to_lowercase());
                }
                "--off" => config.off = true,
                "--sim" => config.sim = Some(PathBuf::from(take("--sim")?)),
                "--url" => config.url = Some(take("--url")?),
                other if other.starts_with('-') => return Err(format!("unknown option {other}")),
                vessel => config.vessels.push(vessel.to_string()),
            }
        }

        if let Some(s) = scale {
            if explicit_size {
                return Err("--scale and --width/--height are mutually exclusive".into());
            }
            if !(s.is_finite() && s > 0.0) {
                return Err("--scale must be a positive number".into());
            }
            config.width = DEFAULT_W * s;
            config.height = DEFAULT_H * s;
        }
        if !(config.time.is_finite() && config.time >= 0.0) {
            return Err("--time must be >= 0".into());
        }
        if !(config.hz.is_finite() && config.hz > 0.0 && config.hz <= 1000.0) {
            return Err("--hz must be in (0, 1000]".into());
        }
        if !(config.meters.is_finite()) {
            return Err("--meters must be finite".into());
        }
        Ok(config)
    }
}

fn parse_f64(s: &str, name: &str) -> Result<f64, String> {
    s.parse::<f64>().map_err(|_| format!("{name} needs a number, got '{s}'"))
}

fn print_help() {
    println!(
        "thug — slide thug-life glasses onto a kitten vessel (gatOS /sim/debug/thug_life)

USAGE:
    thug [OPTIONS] [VESSEL_ID]...

With no vessel ids, every kitten in the world gets the treatment (via the is_kitten leaf).

OPTIONS:
    -m, --meters <m>    drop-in start height above the face, metres     [default: 1.5]
    -t, --time <s>      animation duration, seconds (0 = instant)       [default: 1.2]
        --hz <hz>       position write rate, Hz                         [default: 60]
    -e, --easing <fn>   ease-out | ease-in | ease-in-out | linear       [default: ease-out]
    -s, --scale <x>     uniform size multiplier over the 0.9x0.22 quad
        --width <m>     explicit quad width, metres                     [default: 0.9]
        --height <m>    explicit quad height, metres                    [default: 0.22]
        --part <iid>    anchor part instance id                         [default: root part]
    -c, --cameras <m>   which render passes draw the quad: all, or tokens
                        of main/crew/other (e.g. 'crew' = face cams only,
                        'main crew' = everywhere but extra windows)      [default: all]
        --off           slide the glasses off (reverse) and remove the entry
        --sim <path>    the /sim mount root            [default: /sim, env: GATOS_SIM]
        --url <base>    use the HTTP /v1/fs mirror instead of the mount  [env: GATOS_HTTP]
                        (with or without the /v1 suffix; the mount wins when it is up)
    -h, --help          this text

EXAMPLES:
    thug Hunter
    thug                                   # the whole litter
    thug --time 2.5 --easing linear --scale 1.4 Polaris
    thug --off Hunter                      # take them off
"
    );
}

// ================================================================================================
//  Easing
// ================================================================================================

#[derive(Clone, Copy)]
enum Easing {
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

impl Easing {
    fn parse(s: &str) -> Result<Self, String> {
        match s {
            "linear" => Ok(Self::Linear),
            "ease-in" => Ok(Self::EaseIn),
            "ease-out" => Ok(Self::EaseOut),
            "ease-in-out" => Ok(Self::EaseInOut),
            other => Err(format!(
                "unknown easing '{other}' (linear | ease-in | ease-out | ease-in-out)"
            )),
        }
    }

    /// Maps linear progress `t` in [0,1] to eased progress (quadratic family, like thug.ts).
    fn apply(self, t: f64) -> f64 {
        let t = t.clamp(0.0, 1.0);
        match self {
            Self::Linear => t,
            Self::EaseIn => t * t,
            Self::EaseOut => 1.0 - (1.0 - t) * (1.0 - t),
            Self::EaseInOut => {
                if t < 0.5 {
                    2.0 * t * t
                } else {
                    1.0 - 2.0 * (1.0 - t) * (1.0 - t)
                }
            }
        }
    }
}

// ================================================================================================
//  Entry management
// ================================================================================================

/// Finds the existing thug-life entry for a vessel, or creates one seeded at the animation start.
fn ensure_entry(source: &dyn Source, vessel: &str, config: &Config) -> Result<u32, String> {
    if let Some(id) = find_entry(source, vessel) {
        eprintln!("thug: {vessel}: reusing entry {id}");
        // A reused entry may carry an old size; assert the requested one.
        if let Err(e) = source.write(
            &format!("debug/thug_life/{id}/size"),
            &format!("{} {}", config.width, config.height),
        ) {
            eprintln!("thug: entry {id}: size write failed: {e}");
        }
        apply_cameras(source, id, config);
        return Ok(id);
    }
    if config.off {
        return Err("no glasses to take off".into());
    }

    let part = resolve_part(source, vessel, config)?;

    let start_z = FINAL_Z + config.meters;
    let (rx, ry, rz) = DEFAULT_ROT;
    let line = format!(
        "{vessel} {part} {FINAL_X} {FINAL_Y} {start_z:.6} {rx} {ry} {rz} {} {}",
        config.width, config.height
    );
    source
        .write("debug/thug_life/add", &line)
        .map_err(|e| format!("add failed: {e} (is control_enabled = true in gatos.toml?)"))?;

    // The add lands on the next game tick; poll briefly for the entry to materialize.
    let deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < deadline {
        if let Some(id) = find_entry(source, vessel) {
            eprintln!("thug: {vessel}: created entry {id}");
            apply_cameras(source, id, config);
            return Ok(id);
        }
        sleep(Duration::from_millis(20));
    }
    Err("timed out waiting for the entry to appear after add".into())
}

/// Picks the anchor part instance id: `--part` wins, else the vessel's root part.
///
/// A missing `parts/0/instance_id` has two very different causes, so it is diagnosed rather than
/// reported as one error: the vessel really is gone (name it), or the parts list simply is not
/// being sampled (`telemetry_vessel_parts = false`), in which case part iid `0` — the vehicle
/// frame — is the documented stand-in and lands the quad in the same place on a kitten.
fn resolve_part(source: &dyn Source, vessel: &str, config: &Config) -> Result<String, String> {
    if let Some(iid) = config.part {
        return Ok(iid.to_string());
    }
    if let Some(iid) = source.read(&format!("vessels/by-id/{vessel}/parts/0/instance_id")) {
        return Ok(iid.trim().to_string());
    }
    if source.read(&format!("vessels/by-id/{vessel}/id")).is_none() {
        let roster = source.vessels();
        let known = if roster.is_empty() {
            "the world has no vessels right now".to_string()
        } else {
            format!("known vessels: {}", roster.join(", "))
        };
        return Err(format!("vessel '{vessel}' is not in the world ({known})"));
    }
    eprintln!(
        "thug: {vessel}: parts are not published (telemetry_vessel_parts off) — \
         anchoring to the vehicle frame"
    );
    Ok("0".to_string())
}

/// Writes the entry's camera mask when `--cameras` was given (default = the mod's `all`).
fn apply_cameras(source: &dyn Source, id: u32, config: &Config) {
    if let Some(cameras) = &config.cameras {
        if let Err(e) = source.write(&format!("debug/thug_life/{id}/cameras"), cameras) {
            eprintln!("thug: entry {id}: cameras write failed: {e}");
        }
    }
}

/// Finds the entry id whose `vessel` leaf matches. Uses the directory listing where the source has
/// one (the mount); over HTTP it probes ids, stopping once `count` live entries have been seen.
fn find_entry(source: &dyn Source, vessel: &str) -> Option<u32> {
    let matches = |id: u32| {
        source
            .read(&format!("debug/thug_life/{id}/vessel"))
            .filter(|v| v.trim() == vessel)
            .map(|_| id)
    };

    if let Some(names) = source.list("debug/thug_life") {
        // The listing carries `add`/`clear`/`count`/`help` alongside the numbered entry dirs.
        let mut ids: Vec<u32> = names.iter().filter_map(|n| n.parse::<u32>().ok()).collect();
        ids.sort_unstable();
        return ids.into_iter().find_map(matches);
    }

    let count: u32 = source
        .read("debug/thug_life/count")
        .and_then(|s| s.trim().parse().ok())
        .unwrap_or(0);
    if count == 0 {
        return None;
    }
    let mut seen = 0;
    for id in 0..=MAX_PROBE_ID {
        if let Some(v) = source.read(&format!("debug/thug_life/{id}/vessel")) {
            if v.trim() == vessel {
                return Some(id);
            }
            seen += 1;
            if seen >= count {
                break; // every live entry inspected
            }
        }
    }
    None
}

/// Auto-discovery: every vessel whose `is_kitten` leaf reads 1, over the source's vessel roster.
fn discover_kittens(source: &dyn Source) -> Vec<String> {
    source
        .vessels()
        .into_iter()
        .filter(|id| {
            source
                .read(&format!("vessels/by-id/{id}/is_kitten"))
                .is_some_and(|v| v.trim() == "1")
        })
        .collect()
}

// ================================================================================================
//  Animation
// ================================================================================================

/// Slides every entry's Z from `FINAL_Z + meters` to `FINAL_Z` (or the reverse for `--off`),
/// writing positions for the whole squad each frame so they move together. Returns false when the
/// position writes are failing (control disabled, entry gone) — the first error is reported once
/// rather than per frame.
fn animate(source: &dyn Source, entries: &BTreeMap<String, u32>, config: &Config) -> bool {
    let (from_z, to_z) = if config.off {
        (FINAL_Z, FINAL_Z + config.meters)
    } else {
        (FINAL_Z + config.meters, FINAL_Z)
    };

    let mut failure: Option<String> = None;
    let mut report = |source: &dyn Source, path: &str, value: &str| {
        if let Err(e) = source.write(path, value) {
            if failure.is_none() {
                eprintln!("thug: position write failed: {path}: {e}");
                failure = Some(e);
            }
        }
    };

    let frames = (config.time * config.hz).round().max(0.0) as u64;
    if frames > 0 {
        eprintln!(
            "thug: animating {frames} frames over {}s at {}Hz",
            config.time, config.hz
        );
        let frame_time = Duration::from_secs_f64(1.0 / config.hz);
        let start = Instant::now();
        for frame in 0..frames {
            let progress = frame as f64 / frames as f64;
            let z = from_z + (to_z - from_z) * config.easing.apply(progress);
            let mut position = String::new();
            let _ = write!(position, "{FINAL_X} {FINAL_Y} {z:.6}");
            for id in entries.values() {
                report(source, &format!("debug/thug_life/{id}/position"), &position);
            }
            // Pace against the wall clock, not per-frame sleeps, so write latency doesn't stretch
            // the animation.
            let next = start + frame_time * (frame as u32 + 1);
            if let Some(pause) = next.checked_duration_since(Instant::now()) {
                sleep(pause);
            }
        }
    }

    // Exact final pose, always.
    let position = format!("{FINAL_X} {FINAL_Y} {to_z}");
    for id in entries.values() {
        report(source, &format!("debug/thug_life/{id}/position"), &position);
    }

    failure.is_none()
}

// ================================================================================================
//  Sources — the /sim mount, or the HTTP /v1/fs mirror
// ================================================================================================

/// Read/write one `/sim`-relative leaf (path never starts with a slash).
trait Source {
    /// A short description of where this source points, for error messages.
    fn label(&self) -> String;

    fn read(&self, path: &str) -> Option<String>;

    fn write(&self, path: &str, value: &str) -> Result<(), String>;

    /// The child names of a `/sim` directory, or None when the transport has no listing (HTTP).
    fn list(&self, path: &str) -> Option<Vec<String>>;

    /// The vessel roster (ids as they appear under `vessels/by-id`).
    fn vessels(&self) -> Vec<String>;
}

/// Picks the source: explicit flags win, then `$GATOS_SIM`, then the mount if it is actually
/// serving, then `$GATOS_HTTP`.
///
/// The mount deliberately outranks `$GATOS_HTTP`: the guest login shell presets that variable
/// whenever the host serves the HTTP API, so preferring it would silently route every in-guest run
/// through slirp — and through a transport that may well be disabled by the time the program runs.
fn pick_source(config: &Config) -> Box<dyn Source> {
    if let Some(base) = &config.url {
        return Box::new(HttpSource::new(base.clone()));
    }
    if let Some(root) = &config.sim {
        return Box::new(FsSource::new(root.clone()));
    }
    if let Some(root) = std::env::var_os("GATOS_SIM") {
        return Box::new(FsSource::new(PathBuf::from(root)));
    }
    // `/sim` exists as an empty directory even when the 9p mount is down (init creates it), so the
    // test is "is the tree there", not "does the path exist".
    if Path::new(DEFAULT_SIM).join("time").is_dir() {
        return Box::new(FsSource::new(PathBuf::from(DEFAULT_SIM)));
    }
    if let Some(base) = std::env::var("GATOS_HTTP").ok().filter(|s| !s.is_empty()) {
        return Box::new(HttpSource::new(base));
    }
    Box::new(FsSource::new(PathBuf::from(DEFAULT_SIM)))
}

struct FsSource {
    root: PathBuf,
}

impl FsSource {
    fn new(root: PathBuf) -> Self {
        Self { root }
    }

    fn resolve(&self, path: &str) -> PathBuf {
        let mut p = self.root.clone();
        p.extend(Path::new(path));
        p
    }
}

impl Source for FsSource {
    fn label(&self) -> String {
        format!("the mount at {}", self.root.display())
    }

    fn read(&self, path: &str) -> Option<String> {
        fs::read_to_string(self.resolve(path)).ok()
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        fs::write(self.resolve(path), value).map_err(|e| e.to_string())
    }

    fn list(&self, path: &str) -> Option<Vec<String>> {
        let mut names: Vec<String> = fs::read_dir(self.resolve(path))
            .ok()?
            .filter_map(|e| Some(e.ok()?.file_name().to_string_lossy().into_owned()))
            .collect();
        names.sort();
        Some(names)
    }

    fn vessels(&self) -> Vec<String> {
        self.list("vessels/by-id").unwrap_or_default()
    }
}

struct HttpSource {
    /// The server root *without* the `/v1` suffix — `url()` adds it back.
    base: String,
    agent: ureq::Agent,
}

impl HttpSource {
    fn new(base: String) -> Self {
        // `$GATOS_HTTP` is the /v1 API base (`http://sim:4242/v1`), but a bare host base is the
        // natural thing to type on --url. Normalize both to the server root.
        let mut base = base.trim_end_matches('/').to_string();
        if let Some(root) = base.strip_suffix("/v1") {
            base = root.to_string();
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
    fn label(&self) -> String {
        format!("the HTTP API at {}/v1", self.base)
    }

    fn read(&self, path: &str) -> Option<String> {
        self.agent
            .get(&self.url(path))
            .call()
            .ok()
            .and_then(|r| r.into_string().ok())
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        match self
            .agent
            .post(&self.url(path))
            .set("Content-Type", "text/plain")
            .send_string(value)
        {
            Ok(_) => Ok(()),
            // The mod answers a rejected field write with a JSON error body ({"error":"EACCES",…});
            // it says far more than "400 Bad Request" does.
            Err(ureq::Error::Status(code, response)) => Err(match response.into_string() {
                Ok(body) if !body.trim().is_empty() => format!("HTTP {code}: {}", body.trim()),
                _ => format!("HTTP {code}"),
            }),
            Err(e) => Err(e.to_string()),
        }
    }

    fn list(&self, _path: &str) -> Option<Vec<String>> {
        None // /v1/fs serves leaves only — callers fall back to probing.
    }

    fn vessels(&self) -> Vec<String> {
        // GET /v1/vessels is the roster endpoint: a JSON array of ids, e.g. ["Hunter","Polaris"].
        let body = self
            .agent
            .get(&format!("{}/v1/vessels", self.base))
            .call()
            .ok()
            .and_then(|r| r.into_string().ok())
            .unwrap_or_default();
        json_string_array(&body)
    }
}

/// Pulls the elements out of a flat JSON array of strings (`["a","b"]`) — the one JSON shape this
/// program consumes, so it does not carry a parser to read it.
fn json_string_array(body: &str) -> Vec<String> {
    body.split('"')
        .skip(1)
        .step_by(2)
        .map(|s| s.replace("\\\"", "\"").replace("\\\\", "\\"))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn http_base_normalizes_with_and_without_v1() {
        // $GATOS_HTTP carries the /v1 suffix; a hand-typed --url usually does not. Both must
        // address /v1/fs/<path> exactly once.
        for base in ["http://sim:4242", "http://sim:4242/", "http://sim:4242/v1", "http://sim:4242/v1/"] {
            let source = HttpSource::new(base.to_string());
            assert_eq!(source.url("time/ut"), "http://sim:4242/v1/fs/time/ut", "base {base}");
        }
    }

    #[test]
    fn json_array_parses_the_vessel_roster() {
        assert_eq!(json_string_array("[\"Hunter\",\"Polaris\"]"), ["Hunter", "Polaris"]);
        assert!(json_string_array("[]").is_empty());
    }

    #[test]
    fn easing_hits_both_ends() {
        for easing in [Easing::Linear, Easing::EaseIn, Easing::EaseOut, Easing::EaseInOut] {
            assert_eq!(easing.apply(0.0), 0.0);
            assert_eq!(easing.apply(1.0), 1.0);
        }
    }
}
