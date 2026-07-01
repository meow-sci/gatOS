//! Rasterizes stdin text into one tall dot-coverage bitmap using the vendored Star Jedi font.
//!
//! This is the "simulated font" half of the program: [`fontdue`] rasterizes real glyphs from the
//! embedded TTF into an 8-bit coverage bitmap at a chosen pixel size, and [`Layout::render`] blits
//! word-wrapped, centered lines of it into one big [`Canvas`] sized in *dots* — the sub-cell units
//! [`crate::braille`] later downsamples 2x4 dots per terminal cell. Scrolling that canvas one dot at
//! a time (instead of one whole terminal row at a time) is what makes the crawl smooth.

use fontdue::{Font, FontSettings};

/// The Star Jedi TTF, vendored so the program needs no external font file at runtime.
const FONT_BYTES: &[u8] = include_bytes!("../assets/Starjedi.ttf");

/// A tall bitmap of glyph coverage (0..255 per dot), `width` dots wide.
pub struct Canvas {
    pub width: usize,
    pub height: usize,
    coverage: Vec<u8>,
}

impl Canvas {
    /// Coverage at dot `(x, y)`; out-of-range reads as 0 (empty), which lets callers scroll a
    /// window past either edge of the canvas with no special-casing.
    pub fn sample(&self, x: i64, y: i64) -> u8 {
        if x < 0 || y < 0 {
            return 0;
        }
        let (x, y) = (x as usize, y as usize);
        if x >= self.width || y >= self.height {
            return 0;
        }
        self.coverage[y * self.width + x]
    }
}

enum Line {
    Text(String),
    Gap,
}

pub struct Layout {
    font: Font,
}

impl Layout {
    pub fn load() -> Layout {
        let font = Font::from_bytes(FONT_BYTES, FontSettings::default())
            .expect("bundled assets/Starjedi.ttf failed to parse");
        Layout { font }
    }

    /// Word-wraps `text` to `dot_width` dots (minus `margin_dots` on each side), centers each line,
    /// and rasterizes the whole thing into one [`Canvas`] at font size `px` dots.
    ///
    /// Input lines are kept as authored (a crawl's line breaks are usually hand-placed for pacing)
    /// and only soft-wrapped if a single line is too wide; a blank input line becomes a paragraph
    /// gap. Consecutive blank lines collapse to one gap.
    pub fn render(&self, text: &str, dot_width: usize, margin_dots: usize, px: f32) -> Canvas {
        let content_width = (dot_width.saturating_sub(margin_dots * 2)).max(1) as f32;
        let lines = self.build_lines(text, content_width, px);

        let lm = self
            .font
            .horizontal_line_metrics(px)
            .expect("Starjedi.ttf has no horizontal line metrics");
        let line_height = lm.new_line_size.max(1.0);
        let gap_height = line_height * 0.6;

        let mut height = 0.0f32;
        for line in &lines {
            height += match line {
                Line::Text(_) => line_height,
                Line::Gap => gap_height,
            };
        }
        let height = (height.ceil() as usize).max(1);

        let mut coverage = vec![0u8; dot_width * height];
        let mut baseline = lm.ascent;
        for line in &lines {
            match line {
                Line::Text(text) => {
                    self.blit_line(
                        text,
                        dot_width,
                        margin_dots,
                        content_width,
                        px,
                        baseline,
                        height,
                        &mut coverage,
                    );
                    baseline += line_height;
                }
                Line::Gap => baseline += gap_height,
            }
        }

        Canvas {
            width: dot_width,
            height,
            coverage,
        }
    }

    fn build_lines(&self, text: &str, content_width: f32, px: f32) -> Vec<Line> {
        let mut out = Vec::new();
        let mut last_was_gap = true; // swallow leading blank lines
        for raw in text.lines() {
            let raw = raw.trim_end_matches('\r');
            if raw.trim().is_empty() {
                if !last_was_gap {
                    out.push(Line::Gap);
                    last_was_gap = true;
                }
                continue;
            }
            for wrapped in self.wrap(raw, content_width, px) {
                out.push(Line::Text(wrapped));
            }
            last_was_gap = false;
        }
        while matches!(out.last(), Some(Line::Gap)) {
            out.pop();
        }
        out
    }

    /// Greedy word-wrap of one chunk of text to `max_width` dots.
    fn wrap(&self, text: &str, max_width: f32, px: f32) -> Vec<String> {
        let space_w = self.font.metrics(' ', px).advance_width;
        let mut lines = Vec::new();
        let mut cur = String::new();
        let mut cur_w = 0.0f32;
        for word in text.split_whitespace() {
            let w = self.text_width(word, px);
            if cur.is_empty() {
                cur.push_str(word);
                cur_w = w;
            } else if cur_w + space_w + w <= max_width {
                cur.push(' ');
                cur.push_str(word);
                cur_w += space_w + w;
            } else {
                lines.push(std::mem::take(&mut cur));
                cur.push_str(word);
                cur_w = w;
            }
        }
        if !cur.is_empty() {
            lines.push(cur);
        }
        if lines.is_empty() {
            lines.push(String::new());
        }
        lines
    }

    fn text_width(&self, s: &str, px: f32) -> f32 {
        s.chars()
            .map(|c| self.font.metrics(c, px).advance_width)
            .sum()
    }

    #[allow(clippy::too_many_arguments)]
    fn blit_line(
        &self,
        text: &str,
        canvas_width: usize,
        margin_dots: usize,
        content_width: f32,
        px: f32,
        baseline: f32,
        canvas_height: usize,
        coverage: &mut [u8],
    ) {
        let line_w = self.text_width(text, px);
        let mut pen_x = margin_dots as f32 + ((content_width - line_w) / 2.0).max(0.0);
        for ch in text.chars() {
            let (metrics, bitmap) = self.font.rasterize(ch, px);
            if metrics.width > 0 && metrics.height > 0 {
                let x0 = (pen_x + metrics.xmin as f32).round() as i64;
                let y0 = (baseline - metrics.ymin as f32 - metrics.height as f32).round() as i64;
                for gy in 0..metrics.height {
                    let cy = y0 + gy as i64;
                    if cy < 0 || cy as usize >= canvas_height {
                        continue;
                    }
                    let row = cy as usize * canvas_width;
                    for gx in 0..metrics.width {
                        let cx = x0 + gx as i64;
                        if cx < 0 || cx as usize >= canvas_width {
                            continue;
                        }
                        let v = bitmap[gy * metrics.width + gx];
                        let cell = &mut coverage[row + cx as usize];
                        *cell = (*cell).max(v);
                    }
                }
            }
            pen_x += metrics.advance_width;
        }
    }
}
