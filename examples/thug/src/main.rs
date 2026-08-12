//! thug — slide thug-life glasses onto a kitten vessel, the trivial way.
//!
//! The gatOS mod renders the glasses as a part-anchored textured quad (`/sim/debug/thug_life/`,
//! needs `[control] debug_namespace`). This CLI wraps the whole ceremony — find the vessel's root
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
//! **Sources.** By default this talks to the `/sim` mount with plain file I/O (run it inside the
//! gatOS guest, or anywhere the 9p filesystem is mounted; `--sim <path>` / `$GATOS_SIM` move the
//! root). `--url <base>` / `$GATOS_HTTP` switches to the mod's HTTP `/v1/fs/<path>` mirror instead —
//! handy from the host during development. `/v1/fs` has no directory listing, so entry discovery
//! probes `<id>/vessel` leaves by number rather than reading the directory (works on both sources).
//!
//! Kitten auto-discovery (`thug` with no vessel args) reads each vessel's `is_kitten` leaf.

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

/// Highest entry id probed when discovering existing entries (ids reuse the lowest free slot, so
/// live ids are dense near zero; 64 is far beyond anything sane).
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

    let source: Box<dyn Source> = match &config.url {
        Some(base) => Box::new(HttpSource::new(base.clone())),
        None => Box::new(FsSource::new(config.sim.clone())),
    };

    let vessels = if config.vessels.is_empty() {
        let kittens = discover_kittens(source.as_ref());
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
        match ensure_entry(source.as_ref(), vessel, &config) {
            Ok(id) => {
                entries.insert(vessel.clone(), id);
            }
            Err(e) => {
                eprintln!("thug: {vessel}: {e}");
                std::process::exit(1);
            }
        }
    }

    animate(source.as_ref(), &entries, &config);

    if config.off {
        for (vessel, id) in &entries {
            match source.write(&format!("debug/thug_life/{id}/remove"), "1") {
                Ok(()) => eprintln!("thug: {vessel}: glasses removed."),
                Err(e) => eprintln!("thug: {vessel}: remove failed: {e}"),
            }
        }
    } else {
        eprintln!("thug: deal with it.");
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
    /// Reverse: slide the glasses off and remove the entry.
    off: bool,
    sim: PathBuf,
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
            off: false,
            sim: std::env::var_os("GATOS_SIM")
                .map(PathBuf::from)
                .unwrap_or_else(|| PathBuf::from("/sim")),
            url: std::env::var("GATOS_HTTP").ok().filter(|s| !s.is_empty()),
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
                "--off" => config.off = true,
                "--sim" => config.sim = PathBuf::from(take("--sim")?),
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
        --off           slide the glasses off (reverse) and remove the entry
        --sim <path>    the /sim mount root            [default: /sim, env: GATOS_SIM]
        --url <base>    use HTTP /v1/fs at <base> instead of the mount  [env: GATOS_HTTP]
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
        let _ = source.write(
            &format!("debug/thug_life/{id}/size"),
            &format!("{} {}", config.width, config.height),
        );
        return Ok(id);
    }
    if config.off {
        return Err("no glasses to take off".into());
    }

    let part = match config.part {
        Some(iid) => iid.to_string(),
        None => source
            .read(&format!("vessels/by-id/{vessel}/parts/0/instance_id"))
            .ok_or_else(|| format!("vessel '{vessel}' not found (parts/0/instance_id unreadable)"))?
            .trim()
            .to_string(),
    };

    let start_z = FINAL_Z + config.meters;
    let (rx, ry, rz) = DEFAULT_ROT;
    let line = format!(
        "{vessel} {part} {FINAL_X} {FINAL_Y} {start_z:.6} {rx} {ry} {rz} {} {}",
        config.width, config.height
    );
    source
        .write("debug/thug_life/add", &line)
        .map_err(|e| format!("add failed: {e} (is [control] debug_namespace enabled?)"))?;

    // The add lands on the next game tick; poll briefly for the entry to materialize.
    let deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < deadline {
        if let Some(id) = find_entry(source, vessel) {
            eprintln!("thug: {vessel}: created entry {id}");
            return Ok(id);
        }
        sleep(Duration::from_millis(20));
    }
    Err("timed out waiting for the entry to appear after add".into())
}

/// Probes entry ids for one whose `vessel` leaf matches. Works on both sources (no readdir).
fn find_entry(source: &dyn Source, vessel: &str) -> Option<u32> {
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

/// Auto-discovery: every vessel whose `is_kitten` leaf reads 1. The vessel roster comes from the
/// `list` leaf (one id per line), so this works over HTTP too (no directory listing there).
fn discover_kittens(source: &dyn Source) -> Vec<String> {
    let roster = source.read("vessels/list").unwrap_or_default();
    roster
        .lines()
        .map(str::trim)
        .filter(|id| !id.is_empty())
        .filter(|id| {
            source
                .read(&format!("vessels/by-id/{id}/is_kitten"))
                .is_some_and(|v| v.trim() == "1")
        })
        .map(String::from)
        .collect()
}

// ================================================================================================
//  Animation
// ================================================================================================

/// Slides every entry's Z from `FINAL_Z + meters` to `FINAL_Z` (or the reverse for `--off`),
/// writing positions for the whole squad each frame so they move together.
fn animate(source: &dyn Source, entries: &BTreeMap<String, u32>, config: &Config) {
    let (from_z, to_z) = if config.off {
        (FINAL_Z, FINAL_Z + config.meters)
    } else {
        (FINAL_Z + config.meters, FINAL_Z)
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
                let _ = source.write(&format!("debug/thug_life/{id}/position"), &position);
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
        let _ = source.write(&format!("debug/thug_life/{id}/position"), &position);
    }
}

// ================================================================================================
//  Sources — the /sim mount, or the HTTP /v1/fs mirror
// ================================================================================================

/// Read/write one `/sim`-relative leaf (path never starts with a slash).
trait Source {
    fn read(&self, path: &str) -> Option<String>;
    fn write(&self, path: &str, value: &str) -> Result<(), String>;
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
    fn read(&self, path: &str) -> Option<String> {
        fs::read_to_string(self.resolve(path)).ok()
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        fs::write(self.resolve(path), value).map_err(|e| e.to_string())
    }
}

struct HttpSource {
    base: String,
    agent: ureq::Agent,
}

impl HttpSource {
    fn new(mut base: String) -> Self {
        while base.ends_with('/') {
            base.pop();
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
    fn read(&self, path: &str) -> Option<String> {
        self.agent
            .get(&self.url(path))
            .call()
            .ok()
            .and_then(|r| r.into_string().ok())
    }

    fn write(&self, path: &str, value: &str) -> Result<(), String> {
        self.agent
            .post(&self.url(path))
            .set("Content-Type", "text/plain")
            .send_string(value)
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
}
