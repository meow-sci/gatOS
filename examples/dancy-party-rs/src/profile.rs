//! Save/load **profiles** — a reusable snapshot of everything that shapes the show *except* which
//! vessels it runs on. A profile is the ordered color palette plus every [`crate::app::Settings`]
//! knob (frame rate, fade steps, the two clock durations, the two staggers), serialized to a small,
//! hand-editable YAML file. Vessels are deliberately **not** saved: a profile is the look-and-feel of
//! a party, and you re-pick the craft it plays on each run (`--profile <name>` loads the file, then
//! you arm vessels as usual).
//!
//! Storage is a flat directory of `<name>.yaml` files. The directory is `$DANCY_PROFILE_DIR` if set,
//! else `~/.dancy-party/profiles` (`$HOME`, or `%USERPROFILE%` on Windows), else `./dancy-profiles`.
//! A `--profile` argument that looks like a path (has a separator or a `.yaml`/`.yml` suffix) is used
//! verbatim; a bare name is resolved inside that directory.
//!
//! The format is intentionally tiny — a line-based `key: value` map plus a `colors:` list of hex
//! strings — so there's no YAML dependency and a player can edit one by hand:
//!
//! ```yaml
//! # dancy-party-rs profile
//! hz: 30
//! steps: 0
//! color_ms: 1200
//! anim_ms: 2500
//! color_stagger_ms: 0
//! anim_stagger_ms: 0
//! colors:
//!   - "#ff0000"
//!   - "#00ff00"
//! ```

use std::fs;
use std::path::PathBuf;

use crate::app::Settings;
use crate::color::{self, Rgb};

/// A reusable show: the ordered palette + every display knob. The selected vessels are intentionally
/// excluded (see the module docs) — a profile is re-applied to whatever craft you arm this run.
#[derive(Clone, Debug, PartialEq)]
pub struct Profile {
    pub settings: Settings,
    pub colors: Vec<Rgb>,
}

// Knob ranges — kept in step with the CLI flag validation / `Settings::adjust` clamps in `app.rs`.
const MIN_MS: i64 = 50;
const MAX_MS: i64 = 60_000;
const MAX_STAGGER: f64 = 60_000.0;

impl Profile {
    /// Serializes to the flat YAML form documented above.
    pub fn to_yaml(&self) -> String {
        let s = &self.settings;
        let mut out = String::from("# dancy-party-rs profile\n");
        out.push_str(&format!("hz: {}\n", fmt_f(s.hz)));
        out.push_str(&format!("steps: {}\n", s.steps));
        out.push_str(&format!("color_ms: {}\n", s.color_ms));
        out.push_str(&format!("anim_ms: {}\n", s.anim_ms));
        out.push_str(&format!("color_stagger_ms: {}\n", fmt_f(s.color_stagger_ms)));
        out.push_str(&format!("anim_stagger_ms: {}\n", fmt_f(s.anim_stagger_ms)));
        out.push_str(&format!("bright_min: {}\n", fmt_f(s.bright_min)));
        out.push_str(&format!("bright_max: {}\n", fmt_f(s.bright_max)));
        out.push_str(&format!("bright_ms: {}\n", s.bright_ms));
        out.push_str(&format!("bright_steps: {}\n", s.bright_steps));
        out.push_str("colors:\n");
        for c in &self.colors {
            out.push_str(&format!("  - \"{}\"\n", c.to_hex()));
        }
        out
    }

    /// Parses the flat YAML form. Unknown keys are ignored, missing settings keep their defaults, and
    /// every value is clamped into its knob range — so a hand-edited or partial file still loads. An
    /// unparseable color line is an error (it's almost always a typo worth surfacing).
    pub fn from_yaml(text: &str) -> Result<Profile, String> {
        let mut settings = Settings::default();
        let mut colors = Vec::new();
        let mut in_colors = false;

        for raw in text.lines() {
            let line = strip_comment(raw);
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }
            // A `- "#rrggbb"` entry under the (indented) `colors:` block.
            if in_colors && trimmed.starts_with('-') {
                let val = unquote(trimmed[1..].trim());
                match color::parse(&val) {
                    Some(rgb) => colors.push(rgb),
                    None => return Err(format!("bad color in profile: '{val}'")),
                }
                continue;
            }
            let Some((key, value)) = trimmed.split_once(':') else {
                continue;
            };
            let (key, value) = (key.trim(), unquote(value.trim()));
            in_colors = false;
            match key {
                "hz" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.hz = v.clamp(1.0, 240.0);
                    }
                }
                "steps" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.steps = (v.round() as i64).clamp(0, 1000) as u32;
                    }
                }
                "color_ms" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.color_ms = (v.round() as i64).clamp(MIN_MS, MAX_MS) as u64;
                    }
                }
                "anim_ms" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.anim_ms = (v.round() as i64).clamp(MIN_MS, MAX_MS) as u64;
                    }
                }
                "color_stagger_ms" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.color_stagger_ms = v.clamp(0.0, MAX_STAGGER);
                    }
                }
                "anim_stagger_ms" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.anim_stagger_ms = v.clamp(0.0, MAX_STAGGER);
                    }
                }
                "bright_min" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.bright_min = v.clamp(0.0, 1.0);
                    }
                }
                "bright_max" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.bright_max = v.clamp(0.0, 1.0);
                    }
                }
                "bright_ms" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.bright_ms = (v.round() as i64).clamp(MIN_MS, MAX_MS) as u64;
                    }
                }
                "bright_steps" => {
                    if let Ok(v) = value.parse::<f64>() {
                        settings.bright_steps = (v.round() as i64).clamp(0, 1000) as u32;
                    }
                }
                "colors" => in_colors = value.is_empty(),
                _ => {}
            }
        }
        Ok(Profile { settings, colors })
    }
}

/// The directory profiles live in (see module docs). Honors `$DANCY_PROFILE_DIR`, else a per-user
/// `~/.dancy-party/profiles`, else a relative `dancy-profiles`.
pub fn profiles_dir() -> PathBuf {
    if let Ok(d) = std::env::var("DANCY_PROFILE_DIR") {
        if !d.is_empty() {
            return PathBuf::from(d);
        }
    }
    if let Ok(home) = std::env::var("HOME") {
        if !home.is_empty() {
            return PathBuf::from(home).join(".dancy-party").join("profiles");
        }
    }
    if let Ok(up) = std::env::var("USERPROFILE") {
        if !up.is_empty() {
            return PathBuf::from(up).join(".dancy-party").join("profiles");
        }
    }
    PathBuf::from("dancy-profiles")
}

/// Resolves a `--profile`/save argument to a file. A value that looks like a path (contains a
/// separator or ends in `.yaml`/`.yml`) is used as-is; a bare name becomes `<profiles_dir>/<name>.yaml`
/// (the name sanitized to a filename-safe segment).
pub fn resolve_path(name: &str) -> PathBuf {
    let trimmed = name.trim();
    let looks_path = trimmed.contains('/')
        || trimmed.contains('\\')
        || trimmed.ends_with(".yaml")
        || trimmed.ends_with(".yml");
    if looks_path {
        PathBuf::from(trimmed)
    } else {
        profiles_dir().join(format!("{}.yaml", sanitize_name(trimmed)))
    }
}

/// Writes `profile` to the file `name` resolves to (creating parent dirs), returning the path written.
pub fn save(name: &str, profile: &Profile) -> Result<PathBuf, String> {
    let path = resolve_path(name);
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("{}: {e}", parent.display()))?;
    }
    fs::write(&path, profile.to_yaml()).map_err(|e| format!("{}: {e}", path.display()))?;
    Ok(path)
}

/// Loads and parses the profile `name` resolves to.
pub fn load(name: &str) -> Result<Profile, String> {
    let path = resolve_path(name);
    let text = fs::read_to_string(&path).map_err(|e| format!("{}: {e}", path.display()))?;
    Profile::from_yaml(&text)
}

/// Filename-safe rendering of a profile name (non-`[A-Za-z0-9._-]` → `_`), so a name with spaces or
/// punctuation still maps to a tidy file.
fn sanitize_name(name: &str) -> String {
    let s: String = name
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') {
                c
            } else {
                '_'
            }
        })
        .collect();
    if s.is_empty() {
        "profile".to_string()
    } else {
        s
    }
}

/// Shortest rendering of a float (drops a trailing `.0`): `30`, `0`, `12.5`.
fn fmt_f(v: f64) -> String {
    if (v - v.round()).abs() < 1e-9 {
        format!("{}", v.round() as i64)
    } else {
        format!("{v}")
    }
}

/// Drops a `# comment`. A `#` only starts a comment at the line start or after whitespace — so the
/// `#` inside a `"#rrggbb"` hex color (preceded by a quote) is left intact.
fn strip_comment(line: &str) -> &str {
    let bytes = line.as_bytes();
    for (i, &b) in bytes.iter().enumerate() {
        if b == b'#' && (i == 0 || bytes[i - 1].is_ascii_whitespace()) {
            return &line[..i];
        }
    }
    line
}

/// Strips a surrounding pair of single or double quotes, if present.
fn unquote(s: &str) -> String {
    let b = s.as_bytes();
    if b.len() >= 2 && (b[0] == b'"' || b[0] == b'\'') && b[b.len() - 1] == b[0] {
        s[1..s.len() - 1].to_string()
    } else {
        s.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profile() -> Profile {
        Profile {
            settings: Settings {
                hz: 45.0,
                steps: 8,
                color_ms: 800,
                anim_ms: 3000,
                color_stagger_ms: 80.0,
                anim_stagger_ms: 200.0,
                bright_min: 0.25,
                bright_max: 0.9,
                bright_ms: 400,
                bright_steps: 6,
            },
            colors: vec![Rgb::from_u8(255, 0, 0), Rgb::from_u8(0, 128, 255)],
        }
    }

    #[test]
    fn round_trips_settings_and_colors() {
        let p = profile();
        let yaml = p.to_yaml();
        let back = Profile::from_yaml(&yaml).unwrap();
        assert_eq!(back.settings, p.settings);
        assert_eq!(back.colors, p.colors);
    }

    #[test]
    fn missing_keys_keep_defaults_and_partial_loads() {
        let p = Profile::from_yaml("color_ms: 500\ncolors:\n  - \"#ffffff\"\n").unwrap();
        assert_eq!(p.settings.color_ms, 500);
        assert_eq!(p.settings, Settings { color_ms: 500, ..Settings::default() });
        assert_eq!(p.colors, vec![Rgb::WHITE]);
    }

    #[test]
    fn out_of_range_values_are_clamped() {
        let p = Profile::from_yaml("hz: 9999\nsteps: -5\ncolor_ms: 1\n").unwrap();
        assert_eq!(p.settings.hz, 240.0);
        assert_eq!(p.settings.steps, 0);
        assert_eq!(p.settings.color_ms, MIN_MS as u64);
    }

    #[test]
    fn comments_and_blank_lines_are_ignored() {
        let p = Profile::from_yaml("# header\n\nhz: 30  # the rate\ncolors:\n").unwrap();
        assert_eq!(p.settings.hz, 30.0);
        assert!(p.colors.is_empty());
    }

    #[test]
    fn a_bad_color_is_an_error() {
        let err = Profile::from_yaml("colors:\n  - \"nope\"\n").unwrap_err();
        assert!(err.contains("nope"));
    }

    #[test]
    fn resolve_path_distinguishes_names_from_paths() {
        std::env::set_var("DANCY_PROFILE_DIR", "/tmp/dp");
        assert_eq!(resolve_path("showtime"), PathBuf::from("/tmp/dp/showtime.yaml"));
        assert_eq!(resolve_path("my show!"), PathBuf::from("/tmp/dp/my_show_.yaml"));
        // A path-like argument is used verbatim.
        assert_eq!(resolve_path("./x.yaml"), PathBuf::from("./x.yaml"));
        std::env::remove_var("DANCY_PROFILE_DIR");
    }

    #[test]
    fn save_then_load_on_disk() {
        // Use a path-like name (handled verbatim by `resolve_path`) so this doesn't depend on the
        // process-global `$DANCY_PROFILE_DIR` (which parallel tests would race on).
        let dir = std::env::temp_dir().join(format!("dancy_prof_{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        let target = dir.join("disk_test.yaml");
        let name = target.to_string_lossy().into_owned();
        let p = profile();
        let path = save(&name, &p).unwrap();
        assert!(path.exists());
        let back = load(&name).unwrap();
        assert_eq!(back, p);
        let _ = fs::remove_dir_all(&dir);
    }
}
