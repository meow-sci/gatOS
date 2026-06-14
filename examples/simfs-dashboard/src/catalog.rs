//! First-party knowledge of the `/sim` filesystem. [`classify`] maps a concrete `/sim` field path
//! to a widget [`Kind`] + a default title — this is what lets the dashboard render a *throttle
//! slider* for `…/ctl/throttle` instead of a raw number, and a clickable toggle for `…/lights/0/on`.
//! The mapping mirrors the tree built in `gatOS.SimFs/SimFsTree.cs` (the single source of truth for
//! the path layout and the control surface). [`candidates`] enumerates addable fields for the
//! search popup, expanding per-vessel/body templates against the live ids.

use std::collections::BTreeMap;

use crate::widget::Kind;

/// Accepted `ctl/attitude_mode` tokens (mirrors `SimFsTree.AttitudeModeTokens`): `manual` drops
/// the flight computer; any other names a `FlightComputerAttitudeTrackTarget`.
pub const ATTITUDE_MODES: &[&str] = &[
    "manual",
    "Prograde",
    "Retrograde",
    "Normal",
    "AntiNormal",
    "RadialOut",
    "RadialIn",
    "Toward",
    "Away",
    "Antivel",
    "Align",
    "Forward",
    "Backward",
    "Up",
    "Down",
    "Ahead",
    "Behind",
    "Outward",
    "Inward",
    "PositiveDv",
    "NegativeDv",
    "Custom",
    "None",
];

/// Accepted `ctl/attitude_frame` tokens (the `VehicleReferenceFrame` names).
pub const ATTITUDE_FRAMES: &[&str] = &["EclBody", "EnuBody", "Lvlh", "VlfBody", "BurnBody", "Dock"];

/// Streaming/blocking files — never poll-readable (a `read()` parks). Excluded from the catalog.
pub const STREAMING_LEAVES: &[&str] = &["stream", "events", "alarm"];

/// Module directories that group an indexed/named set of children (`engines/0/…`, `tanks/lf/…`).
const INDEXED_MODULES: &[&str] = &[
    "engines",
    "tanks",
    "rcs",
    "solar",
    "generators",
    "lights",
    "docking",
    "decouplers",
    "animations",
];

/// Every directory that contextualizes a leaf (indexed modules + the fixed grouping dirs).
const MODULE_DIRS: &[&str] = &[
    "engines",
    "tanks",
    "rcs",
    "solar",
    "generators",
    "lights",
    "docking",
    "decouplers",
    "animations",
    "orbit",
    "altitude",
    "velocity",
    "position",
    "attitude",
    "mass",
    "navball",
    "environment",
    "battery",
    "power",
    "atmosphere",
    "ocean",
];

/// The classification result: a default title + the rendering/interaction archetype.
pub struct Spec {
    pub title: String,
    pub kind: Kind,
}

fn spec(title: impl Into<String>, kind: Kind) -> Spec {
    Spec {
        title: title.into(),
        kind,
    }
}

/// Derives the widget archetype + a default title for a concrete `/sim` field path. Deterministic
/// from the path alone (no value needed), so it is recomputed on every layout load.
pub fn classify(path: &str) -> Spec {
    let segs: Vec<&str> = path.split('/').filter(|s| !s.is_empty()).collect();
    let last = segs.last().copied().unwrap_or("");
    let prev = if segs.len() >= 2 {
        segs[segs.len() - 2]
    } else {
        ""
    };
    let has = |k: &str| segs.contains(&k);

    // ---- globals (absolute paths) ----
    match segs.as_slice() {
        ["time", "ut"] => return spec("UT", Kind::Number { unit: "ut" }),
        ["time", "warp"] => return spec("Warp", Kind::Number { unit: "x" }),
        ["time", "sim_dt"] => return spec("Sim dt", Kind::Number { unit: "s" }),
        ["time", "warp_speeds"] => return spec("Warp speeds", Kind::Text),
        ["time", "auto_warp"] => return spec("Auto-warp", Kind::Text),
        ["system", "name"] => return spec("System", Kind::Text),
        ["system", "home"] => return spec("Home body", Kind::Text),
        ["system", "sun"] => return spec("Sun", Kind::Text),
        ["status", "game_version"] => return spec("Game version", Kind::Text),
        ["status", "sampler"] => return spec("Sampler", Kind::Text),
        ["status", "transports"] => return spec("Transports", Kind::Text),
        ["status", "accessors"] => return spec("Accessors", Kind::Text),
        ["debug", "time", "warp"] => {
            return spec(
                "Warp (debug)",
                Kind::NumberCtl {
                    step: 2.0,
                    unit: "x",
                },
            )
        }
        ["debug", "switch_vessel"] => return spec("Switch vessel", Kind::Text),
        _ => {}
    }

    // ---- control surface (ctl/*) ----
    if prev == "ctl" {
        return match last {
            "throttle" => spec("Throttle", Kind::Throttle),
            "ignite" => spec(
                "Ignite",
                Kind::Trigger {
                    verb: "Ignite",
                    value: "1",
                },
            ),
            "shutdown" => spec(
                "Shutdown",
                Kind::Trigger {
                    verb: "Shutdown",
                    value: "1",
                },
            ),
            "stage" => spec(
                "Stage",
                Kind::Trigger {
                    verb: "Stage \u{25b2}",
                    value: "1",
                },
            ),
            "lights" => spec("Lights", Kind::Toggle),
            "rcs" => spec("RCS", Kind::Toggle),
            "attitude_mode" => spec(
                "Attitude mode",
                Kind::Enum {
                    options: ATTITUDE_MODES,
                },
            ),
            "attitude_frame" => spec(
                "Attitude frame",
                Kind::Enum {
                    options: ATTITUDE_FRAMES,
                },
            ),
            "attitude_target" => spec("Attitude target", Kind::Vector),
            "burn" => spec("Burn", Kind::Vector),
            _ => spec(pretty(last), Kind::Text),
        };
    }

    // ---- /sim/debug per-vessel cheats ----
    if has("debug") && has("vessels") {
        match last {
            "refill_fuel" => {
                return spec(
                    "Refill fuel",
                    Kind::Trigger {
                        verb: "Refill fuel",
                        value: "1",
                    },
                )
            }
            "refill_battery" => {
                return spec(
                    "Refill battery",
                    Kind::Trigger {
                        verb: "Refill batt",
                        value: "1",
                    },
                )
            }
            "teleport" => return spec("Teleport", Kind::Vector),
            _ => {}
        }
    }

    let module = module_of(&segs);
    let kind = kind_for(module, last, &segs, &has);
    let title = title_for(module, last, &segs, &has);
    spec(title, kind)
}

/// The most specific module directory contextualizing this leaf, or `""` (top-level vessel/body
/// leaf, or a global already handled above).
fn module_of(segs: &[&str]) -> &'static str {
    let mut found = "";
    for s in segs {
        if let Some(m) = MODULE_DIRS.iter().find(|m| **m == *s) {
            found = m;
        }
    }
    found
}

fn kind_for(module: &str, last: &str, _segs: &[&str], has: &impl Fn(&str) -> bool) -> Kind {
    match (module, last) {
        // engines
        ("engines", "active") => Kind::Toggle,
        ("engines", "vac_thrust") => Kind::Number { unit: "N" },
        ("engines", "isp") => Kind::Number { unit: "s" },
        ("engines", "throttle") => Kind::Gauge {
            unit: "",
            max: Some(1.0),
        },
        ("engines", "propellant") => Kind::Flag,
        ("engines", "min_throttle") => Kind::Fraction,
        // tanks
        ("tanks", "amount") | ("tanks", "capacity") => Kind::Number { unit: "" },
        ("tanks", "fraction") => Kind::Gauge {
            unit: "",
            max: Some(1.0),
        },
        // rcs
        ("rcs", "active") => Kind::Toggle,
        ("rcs", "propellant") => Kind::Flag,
        ("rcs", "map") => Kind::Text,
        // solar
        ("solar", "produced") => Kind::Number { unit: "W" },
        ("solar", "occluded") => Kind::Flag,
        ("solar", "sun_aoa") | ("solar", "tracker_angle") => Kind::Number { unit: "\u{b0}" },
        ("solar", "efficiency") | ("solar", "current") => Kind::Gauge {
            unit: "",
            max: Some(1.0),
        },
        ("solar", "goal") => Kind::Fraction,
        ("solar", "state") => Kind::Text,
        // generators
        ("generators", "active") => Kind::Flag,
        ("generators", "produced") => Kind::Number { unit: "W" },
        // lights
        ("lights", "on") => Kind::Toggle,
        ("lights", "brightness") => Kind::NumberCtl {
            step: 0.1,
            unit: "",
        },
        ("lights", "color") => Kind::Vector,
        // docking
        ("docking", "docked") => Kind::Flag,
        ("docking", "docked_to") => Kind::Text,
        // decouplers
        ("decouplers", "fired") => Kind::Flag,
        ("decouplers", "fire") => Kind::Trigger {
            verb: "Fire",
            value: "1",
        },
        // animations
        ("animations", "goal") => Kind::Fraction,
        ("animations", "current") => Kind::Gauge {
            unit: "",
            max: Some(1.0),
        },
        ("animations", "state") => Kind::Text,
        // orbit (vessels + bodies)
        ("orbit", "apoapsis") | ("orbit", "periapsis") | ("orbit", "sma") => {
            Kind::Number { unit: "m" }
        }
        ("orbit", "ecc") => Kind::Number { unit: "" },
        ("orbit", "inc") | ("orbit", "lan") | ("orbit", "argpe") | ("orbit", "true_anomaly") => {
            Kind::Number { unit: "\u{b0}" }
        }
        ("orbit", "period") | ("orbit", "time_to_ap") | ("orbit", "time_to_pe") => {
            Kind::Number { unit: "s" }
        }
        ("orbit", "next_patch") => Kind::Number { unit: "ut" },
        // altitude
        ("altitude", _) => Kind::Number { unit: "m" },
        // velocity
        ("velocity", "orbital") | ("velocity", "surface") | ("velocity", "inertial") => {
            Kind::Number { unit: "m/s" }
        }
        ("velocity", _) => Kind::Vector,
        // position
        ("position", "lat") | ("position", "lon") => Kind::Number { unit: "\u{b0}" },
        ("position", _) => Kind::Vector,
        // attitude
        ("attitude", _) => Kind::Vector,
        // mass
        ("mass", _) => Kind::Number { unit: "kg" },
        // navball
        ("navball", "pitch") | ("navball", "yaw") | ("navball", "roll") => {
            Kind::Number { unit: "\u{b0}" }
        }
        ("navball", "twr") => Kind::Number { unit: "" },
        ("navball", "deltav") | ("navball", "speed") => Kind::Number { unit: "m/s" },
        ("navball", "frame") => Kind::Text,
        // environment
        ("environment", "pressure") | ("environment", "dynamic_pressure") => {
            Kind::Number { unit: "Pa" }
        }
        ("environment", "density") | ("environment", "ocean_density") => {
            Kind::Number { unit: "kg/m\u{b3}" }
        }
        ("environment", "terrain_radius") => Kind::Number { unit: "m" },
        ("environment", "accel") | ("environment", "angular_accel") => Kind::Vector,
        ("environment", "g_force") => Kind::Number { unit: "g" },
        // battery
        ("battery", "charge") | ("battery", "fraction") => Kind::Gauge {
            unit: "",
            max: Some(1.0),
        },
        ("battery", "capacity") => Kind::Number { unit: "J" },
        // power
        ("power", _) => Kind::Number { unit: "W" },
        // atmosphere / ocean (bodies)
        ("atmosphere", "present") | ("ocean", "present") => Kind::Flag,
        ("atmosphere", "height") | ("atmosphere", "scale_height") => Kind::Number { unit: "m" },
        ("atmosphere", "sea_level_pressure") => Kind::Number { unit: "Pa" },
        ("atmosphere", "sea_level_density") | ("ocean", "density") => {
            Kind::Number { unit: "kg/m\u{b3}" }
        }
        // top-level body / vessel leaves (module == "")
        _ => top_level_kind(last, has),
    }
}

/// Kind for a leaf directly under a vessel or body directory (no grouping module).
fn top_level_kind(last: &str, has: &impl Fn(&str) -> bool) -> Kind {
    if has("bodies") {
        return match last {
            "mass" => Kind::Number { unit: "kg" },
            "radius" | "soi" => Kind::Number { unit: "m" },
            "mu" => Kind::Number { unit: "" },
            "rotation_rate" => Kind::Number { unit: "rad/s" },
            _ => Kind::Text, // id, class, parent, children
        };
    }
    if has("vessels") {
        return match last {
            "controlled" => Kind::Flag,
            "com" => Kind::Vector,
            "telemetry" => Kind::Json,
            _ => Kind::Text, // id, name, situation, parent
        };
    }
    Kind::Text
}

fn title_for(module: &str, last: &str, segs: &[&str], has: &impl Fn(&str) -> bool) -> String {
    // Indexed module: "Engine 0 active", "Tank lf fraction", "Light 2 on".
    if INDEXED_MODULES.contains(&module) {
        let idx = after(segs, module).unwrap_or("");
        return format!("{} {} {}", singular(module), idx, pretty(last));
    }
    // Grouping module: "Orbit apoapsis", "Mass total", "Battery charge".
    if !module.is_empty() {
        return format!("{} {}", cap(module), pretty(last));
    }
    // Top-level vessel/body leaf.
    if has("vessels") {
        return match last {
            "com" => "Center of mass".to_string(),
            _ => pretty(last),
        };
    }
    pretty(last)
}

/// The segment immediately after `module` in the path (the index / resource name).
fn after<'a>(segs: &[&'a str], module: &str) -> Option<&'a str> {
    let i = segs.iter().position(|s| *s == module)?;
    segs.get(i + 1).copied()
}

fn singular(module: &str) -> &'static str {
    match module {
        "engines" => "Engine",
        "tanks" => "Tank",
        "rcs" => "RCS",
        "solar" => "Solar",
        "generators" => "Gen",
        "lights" => "Light",
        "docking" => "Dock",
        "decouplers" => "Decoupler",
        "animations" => "Anim",
        _ => "Item",
    }
}

/// Replaces `_` with spaces and capitalizes the first letter (`time_to_ap` -> `Time to ap`).
fn pretty(s: &str) -> String {
    let spaced = s.replace('_', " ");
    cap(&spaced)
}

fn cap(s: &str) -> String {
    let mut c = s.chars();
    match c.next() {
        Some(first) => first.to_uppercase().collect::<String>() + c.as_str(),
        None => String::new(),
    }
}

// ---- search candidates ---------------------------------------------------------------------

/// An addable field surfaced by the search popup.
pub struct Candidate {
    pub path: String,
    pub title: String,
    pub kind: Kind,
}

/// Per-vessel relative leaf paths offered by template expansion (the non-indexed, always-present
/// fields). Indexed modules — `engines/0/active`, `lights/2/on`, … — are discovered by the live
/// filesystem walk (in-guest); over HTTP, type the exact path into the search box to add one.
const VESSEL_LEAVES: &[&str] = &[
    "ctl/throttle",
    "ctl/ignite",
    "ctl/shutdown",
    "ctl/stage",
    "ctl/lights",
    "ctl/rcs",
    "ctl/attitude_mode",
    "ctl/attitude_frame",
    "id",
    "name",
    "situation",
    "parent",
    "controlled",
    "com",
    "telemetry",
    "position/lat",
    "position/lon",
    "velocity/orbital",
    "velocity/surface",
    "velocity/inertial",
    "altitude/barometric",
    "altitude/radar",
    "mass/total",
    "mass/dry",
    "mass/propellant",
    "orbit/apoapsis",
    "orbit/periapsis",
    "orbit/ecc",
    "orbit/inc",
    "orbit/sma",
    "orbit/period",
    "orbit/true_anomaly",
    "orbit/time_to_ap",
    "orbit/time_to_pe",
    "navball/pitch",
    "navball/yaw",
    "navball/roll",
    "navball/twr",
    "navball/deltav",
    "navball/speed",
    "environment/pressure",
    "environment/density",
    "environment/dynamic_pressure",
    "environment/g_force",
    "battery/charge",
    "battery/fraction",
    "battery/capacity",
    "power/produced",
    "power/consumed",
];

/// Per-vessel debug cheats (only when the server's debug namespace is on).
const DEBUG_VESSEL_LEAVES: &[&str] = &["refill_fuel", "refill_battery"];

/// Per-body relative leaf paths offered by template expansion.
const BODY_LEAVES: &[&str] = &[
    "id",
    "class",
    "parent",
    "mass",
    "radius",
    "mu",
    "soi",
    "rotation_rate",
    "position/ecl",
    "velocity/ecl",
    "orbit/apoapsis",
    "orbit/periapsis",
    "orbit/ecc",
    "orbit/inc",
    "orbit/sma",
    "orbit/period",
];

const GLOBAL_LEAVES: &[&str] = &[
    "time/ut",
    "time/warp",
    "time/sim_dt",
    "time/warp_speeds",
    "time/auto_warp",
    "system/name",
    "system/home",
    "system/sun",
];

const STATUS_LEAVES: &[&str] = &["status/game_version", "status/sampler", "status/transports"];

/// Builds the search candidate set: every live filesystem leaf (when available) plus per-vessel,
/// per-body and global templates expanded against the current ids — deduped and sorted by path.
pub fn candidates(
    vessels: &[String],
    bodies: &[String],
    fs_leaves: &[String],
    control: bool,
    debug: bool,
) -> Vec<Candidate> {
    let mut map: BTreeMap<String, Candidate> = BTreeMap::new();

    let mut add = |path: String| {
        if path_is_streaming(&path) {
            return;
        }
        if !map.contains_key(&path) {
            let spec = classify(&path);
            map.insert(
                path.clone(),
                Candidate {
                    path,
                    title: spec.title,
                    kind: spec.kind,
                },
            );
        }
    };

    // Concrete leaves discovered on the real /sim mount (the complete, index-aware set).
    for leaf in fs_leaves {
        add(leaf.clone());
    }

    // Template expansion — gives HTTP a useful list and FS the curated conditional fields.
    for v in vessels {
        for rel in VESSEL_LEAVES {
            add(format!("vessels/by-id/{v}/{rel}"));
        }
        if debug {
            for rel in DEBUG_VESSEL_LEAVES {
                add(format!("debug/vessels/{v}/{rel}"));
            }
        }
    }
    for b in bodies {
        for rel in BODY_LEAVES {
            add(format!("bodies/{b}/{rel}"));
        }
    }
    for g in GLOBAL_LEAVES {
        add(g.to_string());
    }
    if control {
        for g in STATUS_LEAVES {
            add(g.to_string());
        }
    }
    if debug {
        add("debug/time/warp".to_string());
    }

    map.into_values().collect()
}

/// Whether a path's final segment is a streaming/blocking file that must never be polled.
pub fn path_is_streaming(path: &str) -> bool {
    let last = path.rsplit('/').next().unwrap_or("");
    STREAMING_LEAVES.contains(&last)
}

/// Sanitizes one id into a `/sim` path segment, mirroring `SimFsTree.Sanitize`: chars outside
/// `[A-Za-z0-9._-]` become `_`. (The `~N` duplicate suffixing is not replicated — id collisions
/// are rare and only matter when two vessels share a sanitized id.)
pub fn sanitize_segment(id: &str) -> String {
    if id.is_empty() {
        return "_".to_string();
    }
    let s: String = id
        .chars()
        .map(|c| match c {
            'A'..='Z' | 'a'..='z' | '0'..='9' | '.' | '_' | '-' => c,
            _ => '_',
        })
        .collect();
    match s.as_str() {
        "." | ".." => format!("_{s}"),
        _ => s,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The control surface must classify to interactive (writable) kinds — this is the whole point.
    #[test]
    fn control_paths_are_writable_widgets() {
        let v = "vessels/by-id/Kerbal-1";
        assert!(matches!(
            classify(&format!("{v}/ctl/throttle")).kind,
            Kind::Throttle
        ));
        assert!(matches!(
            classify(&format!("{v}/ctl/lights")).kind,
            Kind::Toggle
        ));
        assert!(matches!(
            classify(&format!("{v}/ctl/rcs")).kind,
            Kind::Toggle
        ));
        assert!(matches!(
            classify(&format!("{v}/ctl/ignite")).kind,
            Kind::Trigger { .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/ctl/stage")).kind,
            Kind::Trigger { .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/ctl/attitude_mode")).kind,
            Kind::Enum { .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/engines/0/active")).kind,
            Kind::Toggle
        ));
        assert!(matches!(
            classify(&format!("{v}/engines/2/min_throttle")).kind,
            Kind::Fraction
        ));
        assert!(matches!(
            classify(&format!("{v}/lights/1/on")).kind,
            Kind::Toggle
        ));
        assert!(matches!(
            classify(&format!("{v}/lights/1/brightness")).kind,
            Kind::NumberCtl { .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/decouplers/0/fire")).kind,
            Kind::Trigger { .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/animations/0/goal")).kind,
            Kind::Fraction
        ));
        assert!(matches!(
            classify("debug/vessels/Kerbal-1/refill_fuel").kind,
            Kind::Trigger { .. }
        ));
        assert!(matches!(
            classify("debug/time/warp").kind,
            Kind::NumberCtl { .. }
        ));
    }

    /// Read-only sensors must classify to non-writable display kinds (with the right unit).
    #[test]
    fn sensor_paths_are_read_only() {
        let v = "vessels/by-id/x";
        assert!(matches!(
            classify(&format!("{v}/altitude/radar")).kind,
            Kind::Number { unit: "m" }
        ));
        assert!(matches!(
            classify(&format!("{v}/velocity/orbital")).kind,
            Kind::Number { unit: "m/s" }
        ));
        assert!(matches!(
            classify(&format!("{v}/mass/total")).kind,
            Kind::Number { unit: "kg" }
        ));
        assert!(matches!(
            classify(&format!("{v}/battery/fraction")).kind,
            Kind::Gauge { max: Some(_), .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/tanks/lf/fraction")).kind,
            Kind::Gauge { .. }
        ));
        assert!(matches!(
            classify(&format!("{v}/position/cci")).kind,
            Kind::Vector
        ));
        assert!(matches!(
            classify(&format!("{v}/telemetry")).kind,
            Kind::Json
        ));
        assert!(matches!(
            classify(&format!("{v}/situation")).kind,
            Kind::Text
        ));
        assert!(matches!(
            classify(&format!("{v}/controlled")).kind,
            Kind::Flag
        ));
        assert!(matches!(
            classify("time/warp").kind,
            Kind::Number { unit: "x" }
        ));
        assert!(!classify(&format!("{v}/altitude/radar")).kind.is_writable());
        assert!(!classify(&format!("{v}/telemetry")).kind.is_writable());
    }

    #[test]
    fn unknown_paths_fall_back_to_text() {
        assert!(matches!(
            classify("vessels/by-id/x/some_future_field").kind,
            Kind::Text
        ));
    }

    #[test]
    fn sanitize_matches_csharp_rule() {
        assert_eq!(sanitize_segment("Kerbal 1"), "Kerbal_1");
        assert_eq!(sanitize_segment("a/b:c"), "a_b_c");
        assert_eq!(sanitize_segment(".."), "_..");
        assert_eq!(sanitize_segment(""), "_");
        assert_eq!(sanitize_segment("ok.name-2"), "ok.name-2");
    }

    #[test]
    fn candidates_expand_per_vessel_and_dedup() {
        let vessels = vec!["A".to_string()];
        let leaves = vec!["vessels/by-id/A/ctl/throttle".to_string()];
        let cands = candidates(&vessels, &[], &leaves, true, false);
        // The fs leaf and the template both produce ctl/throttle — exactly one survives.
        let n = cands
            .iter()
            .filter(|c| c.path == "vessels/by-id/A/ctl/throttle")
            .count();
        assert_eq!(n, 1);
        // Streaming files are never offered.
        assert!(!cands.iter().any(|c| path_is_streaming(&c.path)));
    }
}
