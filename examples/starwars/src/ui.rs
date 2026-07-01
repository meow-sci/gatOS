//! Draws one frame: rebuild the canvas if the terminal resized, then render the current scroll
//! window row-by-row through [`crate::braille`].
//!
//! Backgrounds are deliberately never set — only lit dots get a foreground color — so an unmodified
//! purrTTY terminal with a transparent/default background shows whatever is behind it (the game)
//! through every cell this program doesn't draw a glyph into.

use ratatui::text::Line;
use ratatui::widgets::Paragraph;
use ratatui::Frame;

use crate::app::App;
use crate::braille;

pub fn render(frame: &mut Frame, app: &mut App) {
    let area = frame.area();
    app.ensure_canvas(area.width, area.height);
    let Some(canvas) = app.canvas.as_ref() else {
        return;
    };

    let top = app.scroll_dot.floor() as i64;
    let lines: Vec<Line> = (0..area.height as i64)
        .map(|r| {
            Line::from(braille::render_row(
                canvas,
                top + r * 4,
                area.width as usize,
                app.threshold,
                app.color,
            ))
        })
        .collect();

    frame.render_widget(Paragraph::new(lines), area);
}
