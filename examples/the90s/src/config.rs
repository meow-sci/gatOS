//! The soundboard config file — a tiny YAML document mapping display names to local sound files:
//!
//! ```yaml
//! # the90s soundboard
//! title: Totally Rad Soundboard
//! sounds:
//!   Airhorn: /root/sounds/airhorn.mp3
//!   "You've Got Mail": /mnt/sounds/youve-got-mail.wav
//!   Dial-Up: sounds/dialup.ogg          # relative to the config file
//! ```
//!
//! Like the sibling `dancy-party-rs` profiles, the format is intentionally a **line-based YAML
//! subset** — `title: value` plus one indented `name: path` entry per sound under `sounds:` — so
//! there is no YAML dependency and a player can edit the file by hand. Sound order in the file is
//! the order on screen. Relative paths resolve against the config file's directory.
//!
//! Each sound registers in the gatOS clip store as `/sim/audio/file/<clip>`, where `<clip>` is
//! derived from the display name (sanitized to the store's `[A-Za-z0-9._-]`, ≤ 64 chars, name
//! grammar — SPEC §3.9) plus the file's extension, e.g. `You've Got Mail` + `.wav` →
//! `You_ve_Got_Mail.wav`. Two entries that collide on the derived clip name are a load error.

use std::fs;
use std::path::{Path, PathBuf};

/// One soundboard entry: the display name (the YAML key), the local audio file it plays, and the
/// derived `/sim/audio/file/<clip>` store name it registers under.
#[derive(Clone, Debug, PartialEq)]
pub struct Sound {
    pub name: String,
    pub path: PathBuf,
    pub clip: String,
}

/// The parsed soundboard: a title plus the ordered sound list.
#[derive(Clone, Debug, PartialEq)]
pub struct Config {
    pub title: String,
    pub sounds: Vec<Sound>,
}

/// Loads and parses the config at `path`; relative sound paths resolve against its directory.
pub fn load(path: &Path) -> Result<Config, String> {
    let text = fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    parse(&text, path.parent().unwrap_or(Path::new(".")))
}

/// Parses the config text (see the module docs for the accepted subset). `base` anchors relative
/// sound paths. Unknown top-level keys are ignored; a `sounds:` entry with no path is an error.
pub fn parse(text: &str, base: &Path) -> Result<Config, String> {
    let mut title = String::new();
    let mut sounds: Vec<Sound> = Vec::new();
    let mut in_sounds = false;

    for (lineno, raw) in text.lines().enumerate() {
        let line = strip_comment(raw);
        if line.trim().is_empty() {
            continue;
        }
        let indented = line.starts_with(' ') || line.starts_with('\t');
        if in_sounds && indented {
            let (name, value) = split_key_value(line.trim())
                .ok_or_else(|| format!("line {}: expected 'name: path'", lineno + 1))?;
            if value.is_empty() {
                return Err(format!("line {}: sound '{name}' has no file path", lineno + 1));
            }
            if sounds.iter().any(|s| s.name == name) {
                return Err(format!("line {}: duplicate sound name '{name}'", lineno + 1));
            }
            let path = resolve_path(&value, base);
            let clip = derive_clip(&name, &path);
            if let Some(other) = sounds.iter().find(|s| s.clip == clip) {
                return Err(format!(
                    "sounds '{}' and '{name}' both map to clip '{clip}' — rename one",
                    other.name
                ));
            }
            sounds.push(Sound { name, path, clip });
            continue;
        }
        in_sounds = false;
        let Some((key, value)) = split_key_value(line.trim()) else {
            continue;
        };
        match key.as_str() {
            "title" => title = value,
            "sounds" => in_sounds = value.is_empty(),
            _ => {} // unknown keys are ignored, so the format can grow
        }
    }

    if sounds.is_empty() {
        return Err("no sounds defined (need a 'sounds:' map with at least one entry)".to_string());
    }
    if title.is_empty() {
        title = "the90s".to_string();
    }
    Ok(Config { title, sounds })
}

/// Splits one `key: value` line, honoring a quoted key (so a display name may contain `:`); the
/// value is unquoted. Returns `None` when there is no unquoted `:` separator.
fn split_key_value(line: &str) -> Option<(String, String)> {
    let bytes = line.as_bytes();
    if bytes.first().is_some_and(|&b| b == b'"' || b == b'\'') {
        let quote = bytes[0];
        let close = 1 + line[1..].find(quote as char)?;
        let rest = line[close + 1..].trim_start();
        let value = rest.strip_prefix(':')?;
        return Some((line[1..close].to_string(), unquote(value.trim())));
    }
    let (key, value) = line.split_once(':')?;
    Some((key.trim().to_string(), unquote(value.trim())))
}

/// A relative sound path resolves against the config file's directory; absolute paths are used
/// verbatim.
fn resolve_path(value: &str, base: &Path) -> PathBuf {
    let p = PathBuf::from(value);
    if p.is_absolute() {
        p
    } else {
        base.join(p)
    }
}

/// Derives the `/sim/audio/file/` clip name for a sound: the display name sanitized to the store's
/// `[A-Za-z0-9._-]` grammar, plus the file's (lowercased) extension so `cat /sim/audio/status`
/// stays human-readable — trimmed to the store's 64-char cap.
pub fn derive_clip(name: &str, path: &Path) -> String {
    let mut base: String = name
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') {
                c
            } else {
                '_'
            }
        })
        .collect();
    if base.is_empty() {
        base = "sound".to_string();
    }
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let suffix = if ext.is_empty() || base.to_ascii_lowercase().ends_with(&format!(".{ext}")) {
        String::new()
    } else {
        format!(".{ext}")
    };
    // Sanitization leaves pure ASCII, so byte-truncation is char-safe.
    base.truncate(64 - suffix.len());
    format!("{base}{suffix}")
}

/// Drops a `# comment`. A `#` only starts a comment at the line start or after whitespace, so a
/// `#` inside a value (e.g. a filename) survives.
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

    #[test]
    fn parses_title_and_ordered_sounds() {
        let text = "# board\ntitle: My Board\nsounds:\n  Airhorn: /snd/airhorn.mp3\n  Bonk: /snd/bonk.wav\n";
        let c = parse(text, Path::new("/cfg")).unwrap();
        assert_eq!(c.title, "My Board");
        assert_eq!(c.sounds.len(), 2);
        assert_eq!(c.sounds[0].name, "Airhorn");
        assert_eq!(c.sounds[0].path, PathBuf::from("/snd/airhorn.mp3"));
        assert_eq!(c.sounds[0].clip, "Airhorn.mp3");
        assert_eq!(c.sounds[1].name, "Bonk"); // file order preserved
    }

    #[test]
    fn quoted_names_relative_paths_and_comments() {
        let text = "title: T\nsounds:\n  \"You've Got: Mail\": mail.wav  # classic\n";
        let c = parse(text, Path::new("/cfg")).unwrap();
        assert_eq!(c.sounds[0].name, "You've Got: Mail");
        assert_eq!(c.sounds[0].path, PathBuf::from("/cfg").join("mail.wav"));
        assert_eq!(c.sounds[0].clip, "You_ve_Got__Mail.wav");
    }

    #[test]
    fn missing_sounds_or_paths_are_errors() {
        assert!(parse("title: T\n", Path::new(".")).unwrap_err().contains("no sounds"));
        assert!(parse("sounds:\n  A:\n", Path::new(".")).unwrap_err().contains("no file path"));
    }

    #[test]
    fn duplicate_names_and_clip_collisions_are_errors() {
        let dup = "sounds:\n  A: /a.mp3\n  A: /b.mp3\n";
        assert!(parse(dup, Path::new(".")).unwrap_err().contains("duplicate"));
        // 'A B' and 'A_B' both sanitize to the clip 'A_B' — distinct names, same store entry.
        let collide = "sounds:\n  A B: /a.mp3\n  A_B: /b.mp3\n";
        assert!(parse(collide, Path::new(".")).unwrap_err().contains("both map to clip"));
    }

    #[test]
    fn clip_derivation_sanitizes_and_caps() {
        assert_eq!(derive_clip("Sad Trombone!", Path::new("/x/sad.OGG")), "Sad_Trombone_.ogg");
        assert_eq!(derive_clip("noext", Path::new("/x/raw")), "noext");
        // A name already carrying the extension doesn't get it twice.
        assert_eq!(derive_clip("boom.mp3", Path::new("/x/boom.mp3")), "boom.mp3");
        // Unicode collapses to '_', empty collapses to a placeholder.
        assert_eq!(derive_clip("🎺", Path::new("/x/t.mp3")), "_.mp3");
        assert_eq!(derive_clip("", Path::new("/x/t.mp3")), "sound.mp3");
        // Long names respect the 64-char store cap.
        let long = "x".repeat(100);
        let clip = derive_clip(&long, Path::new("/x/l.flac"));
        assert!(clip.len() <= 64);
        assert!(clip.ends_with(".flac"));
    }

    #[test]
    fn default_title_when_absent() {
        let c = parse("sounds:\n  A: /a.mp3\n", Path::new(".")).unwrap();
        assert_eq!(c.title, "the90s");
    }
}
