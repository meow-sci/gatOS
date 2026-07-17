//! The custom **button widget** — a beveled, hex-colored, full-width button, after the ratatui
//! *custom widget* example (<https://ratatui.rs/examples/widgets/custom_widget/>): a solid fill,
//! a one-eighth highlight along the top edge and a shadow along the bottom for a raised, 3D look,
//! with the label centered on it.
//!
//! Unlike the rest of the gatOS TUI examples, this widget **does** paint a background — a colored
//! fill is the whole point of a button — so it deliberately opts out of the project's
//! transparent-over-the-game rule. The text color is picked for contrast against the fill (dark ink
//! on light buttons, light ink on dark ones) so any hex the player throws at it stays readable.

use ratatui::buffer::Buffer;
use ratatui::layout::Rect;
use ratatui::style::{Color, Modifier, Style};
use ratatui::widgets::Widget;

/// How the button is currently drawn.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum BtnState {
    /// Not selected.
    Normal,
    /// The keyboard cursor is on it.
    Selected,
    /// Its command is running right now.
    Active,
}

/// A single beveled button. Cheap to build per frame.
pub struct Button<'a> {
    label: &'a str,
    /// A second, dimmer centered line under the label (the run result or "running …").
    subtitle: Option<String>,
    /// 1-based shortcut hint painted in the top-left corner (`0` = none).
    hint: u8,
    color: Color,
    state: BtnState,
}

impl<'a> Button<'a> {
    pub fn new(label: &'a str, color: Color) -> Self {
        Self {
            label,
            subtitle: None,
            hint: 0,
            color,
            state: BtnState::Normal,
        }
    }

    pub fn state(mut self, state: BtnState) -> Self {
        self.state = state;
        self
    }

    pub fn subtitle(mut self, subtitle: Option<String>) -> Self {
        self.subtitle = subtitle;
        self
    }

    pub fn hint(mut self, hint: u8) -> Self {
        self.hint = hint;
        self
    }

    /// The fill color for the current state: Selected brightens, Active brightens more (a pressed
    /// glow), Normal is the configured color verbatim.
    fn fill(&self) -> Color {
        match self.state {
            BtnState::Normal => self.color,
            BtnState::Selected => lighten(self.color, 0.25),
            BtnState::Active => lighten(self.color, 0.5),
        }
    }
}

impl Widget for Button<'_> {
    fn render(self, area: Rect, buf: &mut Buffer) {
        if area.width == 0 || area.height == 0 {
            return;
        }
        let fill = self.fill();
        let ink = contrast(fill);

        // Solid fill (spaces on a colored background).
        buf.set_style(area, Style::new().bg(fill).fg(ink));

        // Bevel: a bright top edge and a dark bottom edge — only when there's a row to spare.
        if area.height >= 2 {
            let top = lighten(fill, 0.4);
            let bottom = darken(fill, 0.4);
            let last = area.bottom() - 1;
            for x in area.x..area.right() {
                buf[(x, area.y)].set_char('\u{2594}').set_fg(top).set_bg(fill); // ▔
                buf[(x, last)].set_char('\u{2581}').set_fg(bottom).set_bg(fill); // ▁
            }
        }

        // Shortcut hint in the top-left corner.
        if self.hint > 0 && area.width > 4 {
            buf.set_string(
                area.x + 1,
                area.y,
                self.hint.to_string(),
                Style::new().fg(ink).bg(fill).add_modifier(Modifier::DIM),
            );
        }

        // Label, centered. Selected/Active frame it with » … « (Latin-1 guillemets — near-universal
        // font coverage) and bold.
        let selected = self.state != BtnState::Normal;
        let label = if selected && area.width as usize >= self.label.chars().count() + 4 {
            format!("\u{00bb} {} \u{00ab}", self.label)
        } else {
            self.label.to_string()
        };
        let mut label_style = Style::new().fg(ink).bg(fill);
        if selected {
            label_style = label_style.add_modifier(Modifier::BOLD);
        }
        let label_y = area.y + (area.height - 1) / 2;
        put_centered(buf, area.x, label_y, area.width, &label, label_style);

        // Subtitle one row below the label, if it fits without landing on the shadow edge.
        if let Some(sub) = &self.subtitle {
            let sub_y = label_y + 1;
            let floor = area.bottom().saturating_sub(if area.height >= 2 { 1 } else { 0 });
            if area.height >= 3 && sub_y < floor {
                let style = Style::new().fg(ink).bg(fill).add_modifier(Modifier::DIM);
                put_centered(buf, area.x, sub_y, area.width, sub, style);
            }
        }
    }
}

// ---- color helpers ------------------------------------------------------------------------------

/// Chooses black or white ink for readable contrast against `bg`, by perceived luminance (Rec. 601).
/// Non-RGB colors (never produced by our hex parser) fall back to white.
pub fn contrast(bg: Color) -> Color {
    match bg {
        Color::Rgb(r, g, b) => {
            let lum = 0.299 * r as f32 + 0.587 * g as f32 + 0.114 * b as f32;
            if lum > 140.0 {
                Color::Black
            } else {
                Color::White
            }
        }
        _ => Color::White,
    }
}

/// Blends `color` toward white by `amt` (0..1). Non-RGB colors pass through unchanged.
pub fn lighten(color: Color, amt: f32) -> Color {
    map_rgb(color, |c| c as f32 + (255.0 - c as f32) * amt)
}

/// Blends `color` toward black by `amt` (0..1). Non-RGB colors pass through unchanged.
pub fn darken(color: Color, amt: f32) -> Color {
    map_rgb(color, |c| c as f32 * (1.0 - amt))
}

fn map_rgb(color: Color, f: impl Fn(u8) -> f32) -> Color {
    match color {
        Color::Rgb(r, g, b) => Color::Rgb(
            f(r).round().clamp(0.0, 255.0) as u8,
            f(g).round().clamp(0.0, 255.0) as u8,
            f(b).round().clamp(0.0, 255.0) as u8,
        ),
        other => other,
    }
}

/// Writes `s` horizontally centered within `[x, x+w)` at row `y`, clipped to the width.
fn put_centered(buf: &mut Buffer, x: u16, y: u16, w: u16, s: &str, style: Style) {
    if w == 0 {
        return;
    }
    let len = s.chars().count() as u16;
    let start = if len >= w { x } else { x + (w - len) / 2 };
    let avail = (x + w).saturating_sub(start) as usize;
    let text: String = s.chars().take(avail).collect();
    buf.set_string(start, y, text, style);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn contrast_picks_readable_ink() {
        assert_eq!(contrast(Color::Rgb(255, 255, 255)), Color::Black);
        assert_eq!(contrast(Color::Rgb(0, 0, 0)), Color::White);
        assert_eq!(contrast(Color::Rgb(0x2e, 0xa0, 0x43)), Color::White); // mid green → white ink
    }

    #[test]
    fn lighten_and_darken_move_toward_the_extremes() {
        assert_eq!(lighten(Color::Rgb(100, 100, 100), 0.5), Color::Rgb(178, 178, 178));
        assert_eq!(darken(Color::Rgb(100, 100, 100), 0.5), Color::Rgb(50, 50, 50));
        // Fully lightened/darkened clamp at the ends.
        assert_eq!(lighten(Color::Rgb(10, 20, 30), 1.0), Color::Rgb(255, 255, 255));
        assert_eq!(darken(Color::Rgb(10, 20, 30), 1.0), Color::Rgb(0, 0, 0));
    }
}
