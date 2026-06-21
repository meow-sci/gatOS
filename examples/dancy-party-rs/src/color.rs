//! Colors — the one currency this program deals in. A [`Rgb`] is a linear-ish 0..1 triple, which is
//! exactly the format the `/sim` `lights/<n>/color` control file wants (`"r g b"`, each 0..1). The
//! user, though, enters colors the friendly way — 0..255 channels or an HTML hex string — or picks
//! from the bundled [`crate::xkcd`] survey palette, so this module is the bridge: parse those human
//! formats in, interpolate between them, and format back out to the `/sim` wire form.

use ratatui::style::Color;

/// An RGB color with each channel in 0..1 — the `/sim` color-file representation. Stored as `f64`
/// so the interpolation math stays smooth even across a 60 Hz frame sweep.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Rgb {
    pub r: f64,
    pub g: f64,
    pub b: f64,
}

impl Rgb {
    pub const WHITE: Rgb = Rgb {
        r: 1.0,
        g: 1.0,
        b: 1.0,
    };

    pub fn new(r: f64, g: f64, b: f64) -> Self {
        Self {
            r: r.clamp(0.0, 1.0),
            g: g.clamp(0.0, 1.0),
            b: b.clamp(0.0, 1.0),
        }
    }

    /// From 0..255 channels (what the user types in RGB mode).
    pub fn from_u8(r: u8, g: u8, b: u8) -> Self {
        Self::new(r as f64 / 255.0, g as f64 / 255.0, b as f64 / 255.0)
    }

    /// From an `[f32; 3]` 0..1 triple (the [`crate::xkcd`] table's storage).
    pub fn from_f32(c: [f32; 3]) -> Self {
        Self::new(c[0] as f64, c[1] as f64, c[2] as f64)
    }

    /// Linear interpolation toward `other` by `t` (0 = self, 1 = other), per channel.
    pub fn lerp(self, other: Rgb, t: f64) -> Rgb {
        let t = t.clamp(0.0, 1.0);
        Rgb {
            r: self.r + (other.r - self.r) * t,
            g: self.g + (other.g - self.g) * t,
            b: self.b + (other.b - self.b) * t,
        }
    }

    /// The `/sim` wire form: three space-separated 0..1 values. Trimmed to 5 decimals — plenty of
    /// precision for a light tint, and short enough to keep the 9p write tiny.
    pub fn to_sim(self) -> String {
        format!("{} {} {}", trim5(self.r), trim5(self.g), trim5(self.b))
    }

    /// `#rrggbb`, for display next to each palette entry.
    pub fn to_hex(self) -> String {
        let q = |c: f64| (c.clamp(0.0, 1.0) * 255.0).round() as u8;
        format!("#{:02x}{:02x}{:02x}", q(self.r), q(self.g), q(self.b))
    }

    /// The ratatui truecolor for rendering a swatch.
    pub fn to_term(self) -> Color {
        let q = |c: f64| (c.clamp(0.0, 1.0) * 255.0).round() as u8;
        Color::Rgb(q(self.r), q(self.g), q(self.b))
    }
}

/// Parses a user-typed color in **either** HTML hex (`#ff8000`, `ff8000`, `#f80`) **or** a 0..255
/// triple (`255 128 0`, `255,128,0`). Hex is chosen when the string starts with `#` or is a bare
/// 3/6-digit hex run; otherwise it's read as three 0..255 channels. Returns `None` on anything else.
pub fn parse(input: &str) -> Option<Rgb> {
    let s = input.trim();
    if s.is_empty() {
        return None;
    }
    let looks_hex = s.starts_with('#') || (is_all_hex(s) && (s.len() == 3 || s.len() == 6));
    if looks_hex {
        parse_hex(s)
    } else {
        parse_triple(s)
    }
}

fn is_all_hex(s: &str) -> bool {
    !s.is_empty() && s.bytes().all(|b| b.is_ascii_hexdigit())
}

fn parse_hex(s: &str) -> Option<Rgb> {
    let h = s.strip_prefix('#').unwrap_or(s);
    match h.len() {
        // #rgb shorthand expands each nibble (f80 -> ff8800).
        3 => {
            let v: Vec<u8> = h
                .chars()
                .map(|c| c.to_digit(16).map(|d| (d * 17) as u8))
                .collect::<Option<Vec<_>>>()?;
            Some(Rgb::from_u8(v[0], v[1], v[2]))
        }
        6 => {
            let r = u8::from_str_radix(&h[0..2], 16).ok()?;
            let g = u8::from_str_radix(&h[2..4], 16).ok()?;
            let b = u8::from_str_radix(&h[4..6], 16).ok()?;
            Some(Rgb::from_u8(r, g, b))
        }
        _ => None,
    }
}

fn parse_triple(s: &str) -> Option<Rgb> {
    let nums: Vec<u32> = s
        .split([' ', ',', '\t'])
        .filter(|t| !t.is_empty())
        .map(|t| t.parse::<u32>().ok())
        .collect::<Option<Vec<_>>>()?;
    if nums.len() != 3 || nums.iter().any(|&n| n > 255) {
        return None;
    }
    Some(Rgb::from_u8(nums[0] as u8, nums[1] as u8, nums[2] as u8))
}

/// Turns a PascalCase / underscore XKCD name (`"CloudyBlue"`, `"Green_Yellow"`) into a spaced
/// lowercase label (`"cloudy blue"`, `"green yellow"`) for display and fuzzy search.
pub fn humanize(name: &str) -> String {
    let mut out = String::with_capacity(name.len() + 4);
    for (i, ch) in name.chars().enumerate() {
        if ch == '_' {
            if !out.ends_with(' ') {
                out.push(' ');
            }
        } else if ch.is_ascii_uppercase() && i != 0 {
            if !out.ends_with(' ') {
                out.push(' ');
            }
            out.push(ch.to_ascii_lowercase());
        } else {
            out.push(ch.to_ascii_lowercase());
        }
    }
    out
}

/// Shortest 0..5-decimal rendering of a 0..1 channel, no trailing zeros (`1`, `0`, `0.5`, `0.67451`).
fn trim5(v: f64) -> String {
    let s = format!("{:.5}", v.clamp(0.0, 1.0));
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s.is_empty() || s == "-0" {
        "0".to_string()
    } else {
        s.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_hex_forms() {
        assert_eq!(parse("#ff8000"), Some(Rgb::from_u8(255, 128, 0)));
        assert_eq!(parse("ff8000"), Some(Rgb::from_u8(255, 128, 0)));
        assert_eq!(parse("#f80"), Some(Rgb::from_u8(255, 136, 0)));
        assert_eq!(parse("#FFFFFF"), Some(Rgb::WHITE));
    }

    #[test]
    fn parses_triple_forms() {
        assert_eq!(parse("255 128 0"), Some(Rgb::from_u8(255, 128, 0)));
        assert_eq!(parse("255,128,0"), Some(Rgb::from_u8(255, 128, 0)));
        assert_eq!(parse("0 0 0"), Some(Rgb::from_u8(0, 0, 0)));
    }

    #[test]
    fn rejects_junk_and_out_of_range() {
        assert_eq!(parse(""), None);
        assert_eq!(parse("hello"), None);
        assert_eq!(parse("256 0 0"), None); // channel out of range
        assert_eq!(parse("1 2"), None); // too few
        assert_eq!(parse("1 2 3 4"), None); // too many
        assert_eq!(parse("#gg0000"), None); // not hex
    }

    #[test]
    fn sim_form_is_compact_and_round_trips() {
        assert_eq!(Rgb::WHITE.to_sim(), "1 1 1");
        assert_eq!(Rgb::from_u8(0, 0, 0).to_sim(), "0 0 0");
        // 128/255 = 0.50196..., trimmed to 5 decimals.
        assert_eq!(Rgb::from_u8(128, 128, 128).to_sim(), "0.50196 0.50196 0.50196");
    }

    #[test]
    fn lerp_endpoints_and_midpoint() {
        let a = Rgb::new(0.0, 0.0, 0.0);
        let b = Rgb::new(1.0, 0.5, 0.0);
        assert_eq!(a.lerp(b, 0.0), a);
        assert_eq!(a.lerp(b, 1.0), b);
        let mid = a.lerp(b, 0.5);
        assert!((mid.r - 0.5).abs() < 1e-9 && (mid.g - 0.25).abs() < 1e-9);
    }

    #[test]
    fn humanize_splits_camel_and_underscore() {
        assert_eq!(humanize("CloudyBlue"), "cloudy blue");
        assert_eq!(humanize("Green_Yellow"), "green yellow");
        assert_eq!(humanize("White"), "white");
    }

    #[test]
    fn hex_output_round_trips() {
        assert_eq!(Rgb::from_u8(255, 128, 0).to_hex(), "#ff8000");
    }
}
