//! The button set — where the panel gets its (label, color, command) triplets from.
//!
//! Two sources, and you can mix them:
//!
//! 1. A **TOML file** (the primary, unambiguous form — see the example `pressme.toml`): an array of
//!    `[[button]]` tables, each with `label`, `color`, and `command`. File order is button order.
//! 2. Repeatable **`--button LABEL:COLOR:COMMAND`** flags on the command line — handy for a
//!    throwaway one-liner panel. Only the first two `:` split fields off, so the `command` (the
//!    last field) may itself contain colons freely; `label` and `color` may not.
//!
//! `color` is a hex string — `#rrggbb`, `#rgb`, or the bare digits — parsed once here into a
//! [`Color::Rgb`], so the render path never re-parses.

use std::fs;
use std::path::Path;

use ratatui::style::Color;
use serde::Deserialize;

/// One configured button: what to show, how to paint it, what to run.
#[derive(Clone, Debug, PartialEq)]
pub struct Button {
    pub label: String,
    pub color: Color,
    pub command: String,
}

/// The whole TOML document: `[[button]]` tables plus ignored unknown keys (so the format can grow).
#[derive(Deserialize)]
struct RawFile {
    #[serde(default)]
    button: Vec<RawButton>,
}

#[derive(Deserialize)]
struct RawButton {
    label: String,
    color: String,
    command: String,
}

/// Loads and parses the TOML button file at `path`.
pub fn load_file(path: &Path) -> Result<Vec<Button>, String> {
    let text = fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    parse_toml(&text).map_err(|e| format!("{}: {e}", path.display()))
}

/// Parses the TOML button document (see the module docs / `pressme.toml` for the shape).
pub fn parse_toml(text: &str) -> Result<Vec<Button>, String> {
    let raw: RawFile = toml::from_str(text).map_err(|e| e.message().to_string())?;
    if raw.button.is_empty() {
        return Err("no [[button]] entries".to_string());
    }
    raw.button
        .into_iter()
        .map(|b| {
            let color = parse_color(&b.color)
                .ok_or_else(|| format!("button '{}': bad color '{}'", b.label, b.color))?;
            Ok(Button {
                label: b.label,
                color,
                command: b.command,
            })
        })
        .collect()
}

/// Parses one `--button LABEL:COLOR:COMMAND` value. Splits on the first two colons only, so the
/// command may contain colons; empty label/color/command are all errors.
pub fn parse_cli_button(spec: &str) -> Result<Button, String> {
    let mut parts = spec.splitn(3, ':');
    let label = parts.next().unwrap_or("").trim();
    let color = parts.next().unwrap_or("").trim();
    let command = parts.next().unwrap_or("").trim();
    if label.is_empty() || color.is_empty() || command.is_empty() {
        return Err(format!(
            "bad --button '{spec}' (want LABEL:COLOR:COMMAND, e.g. 'Deploy:#2ea043:make deploy')"
        ));
    }
    let color = parse_color(color).ok_or_else(|| format!("bad color '{color}' in --button"))?;
    Ok(Button {
        label: label.to_string(),
        color,
        command: command.to_string(),
    })
}

/// Parses a hex color: `#rrggbb`, `rrggbb`, `#rgb`, or `rgb` (the short form doubles each nibble,
/// so `#f80` == `#ff8800`). Returns `None` on anything else.
pub fn parse_color(s: &str) -> Option<Color> {
    let h = s.trim().strip_prefix('#').unwrap_or(s.trim());
    let (r, g, b) = match h.len() {
        3 => {
            let n = u16::from_str_radix(h, 16).ok()?;
            let (r, g, b) = ((n >> 8) & 0xf, (n >> 4) & 0xf, n & 0xf);
            // 0xf → 0xff: duplicate each nibble.
            ((r * 17) as u8, (g * 17) as u8, (b * 17) as u8)
        }
        6 => {
            let n = u32::from_str_radix(h, 16).ok()?;
            (((n >> 16) & 0xff) as u8, ((n >> 8) & 0xff) as u8, (n & 0xff) as u8)
        }
        _ => return None,
    };
    Some(Color::Rgb(r, g, b))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_toml_in_file_order() {
        let text = r##"
            [[button]]
            label = "A"
            color = "#ff0000"
            command = "echo a"

            [[button]]
            label = "B"
            color = "0f0"
            command = "echo b:with:colons"
        "##;
        let btns = parse_toml(text).unwrap();
        assert_eq!(btns.len(), 2);
        assert_eq!(btns[0].label, "A");
        assert_eq!(btns[0].color, Color::Rgb(255, 0, 0));
        assert_eq!(btns[1].color, Color::Rgb(0, 255, 0));
        assert_eq!(btns[1].command, "echo b:with:colons");
    }

    #[test]
    fn empty_or_bad_toml_is_an_error() {
        assert!(parse_toml("").unwrap_err().contains("no [[button]]"));
        let bad = "[[button]]\nlabel=\"X\"\ncolor=\"nope\"\ncommand=\"echo\"\n";
        assert!(parse_toml(bad).unwrap_err().contains("bad color"));
    }

    #[test]
    fn cli_button_keeps_colons_in_the_command() {
        let b = parse_cli_button("Curl:#1f6feb:curl http://host:8080/v1").unwrap();
        assert_eq!(b.label, "Curl");
        assert_eq!(b.color, Color::Rgb(0x1f, 0x6f, 0xeb));
        assert_eq!(b.command, "curl http://host:8080/v1");
    }

    #[test]
    fn cli_button_rejects_missing_fields() {
        assert!(parse_cli_button("OnlyLabel").is_err());
        assert!(parse_cli_button("L:#fff").is_err());
        assert!(parse_cli_button("L:badcolor:cmd").is_err());
    }

    #[test]
    fn color_forms() {
        assert_eq!(parse_color("#2ea043"), Some(Color::Rgb(0x2e, 0xa0, 0x43)));
        assert_eq!(parse_color("2ea043"), Some(Color::Rgb(0x2e, 0xa0, 0x43)));
        assert_eq!(parse_color("#f80"), Some(Color::Rgb(0xff, 0x88, 0x00)));
        assert_eq!(parse_color("fff"), Some(Color::Rgb(255, 255, 255)));
        assert_eq!(parse_color("#12345"), None);
        assert_eq!(parse_color("#gg0000"), None);
    }
}
