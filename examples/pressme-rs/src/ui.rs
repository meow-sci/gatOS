//! Rendering: the whole terminal is one column of full-width buttons, its height split evenly across
//! them (ratatui's `Fill(1)` constraints distribute the remainder rows). Each button shows its label
//! centered, a shortcut number in the corner, and — once pressed — a result / spinner subtitle. The
//! per-frame rects are recorded onto the [`App`] so `app.rs` can hit-test clicks.

use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::Frame;

use crate::app::{App, RunState};
use crate::button::{BtnState, Button};

/// Braille spinner frames for a running command.
const SPINNER: [char; 8] = ['\u{2807}', '\u{280b}', '\u{2819}', '\u{2838}', '\u{28b0}', '\u{28e0}', '\u{28c4}', '\u{2846}'];

pub fn render(f: &mut Frame, app: &mut App) {
    let area = f.area();
    if area.width == 0 || area.height == 0 || app.buttons.is_empty() {
        return;
    }

    let rects = split_evenly(area, app.buttons.len());
    app.rects = rects.clone();

    for (i, b) in app.buttons.iter().enumerate() {
        let state = match b.state {
            RunState::Running(_) => BtnState::Active,
            _ if i == app.selected => BtnState::Selected,
            _ => BtnState::Normal,
        };
        let hint = if i < 9 { (i + 1) as u8 } else { 0 };
        let widget = Button::new(&b.label, b.color)
            .state(state)
            .hint(hint)
            .subtitle(subtitle(&b.state));
        f.render_widget(widget, rects[i]);
    }
}

/// Splits `area` into `n` stacked full-width rows of near-equal height.
fn split_evenly(area: Rect, n: usize) -> Vec<Rect> {
    Layout::vertical(vec![Constraint::Fill(1); n])
        .split(area)
        .to_vec()
}

/// The subtitle line for a button's state: nothing when idle, a spinner while running, a ✓/✗ result
/// once done.
fn subtitle(state: &RunState) -> Option<String> {
    match state {
        RunState::Idle => None,
        RunState::Running(started) => {
            let frame = (started.elapsed().as_millis() / 90) as usize % SPINNER.len();
            Some(format!("running {}", SPINNER[frame]))
        }
        RunState::Done(o) if o.ok => Some(format!("\u{2713} {}", o.summary)),
        RunState::Done(o) => Some(format!("\u{2717} {}", o.summary)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config;
    use crate::runner::{Outcome, RunMsg};
    use ratatui::backend::TestBackend;
    use ratatui::style::Color;
    use ratatui::Terminal;
    use std::sync::mpsc;

    fn app_with(labels: &[&str]) -> App {
        let (tx, _rx) = mpsc::channel();
        let buttons = labels
            .iter()
            .map(|l| config::Button {
                label: l.to_string(),
                color: Color::Rgb(0x2e, 0xa0, 0x43),
                command: "true".into(),
            })
            .collect();
        App::new(buttons, tx)
    }

    fn render_to_text(app: &mut App, w: u16, h: u16) -> String {
        let mut terminal = Terminal::new(TestBackend::new(w, h)).unwrap();
        terminal.draw(|f| render(f, app)).unwrap();
        let buf = terminal.backend().buffer().clone();
        let mut out = String::new();
        for y in 0..buf.area.height {
            for x in 0..buf.area.width {
                out.push_str(buf[(x, y)].symbol());
            }
            out.push('\n');
        }
        out
    }

    #[test]
    fn split_is_even_and_covers_the_whole_height() {
        let rects = split_evenly(Rect::new(0, 0, 20, 10), 3);
        assert_eq!(rects.len(), 3);
        // Rows tile top-to-bottom with no gap and cover all 10 rows.
        assert_eq!(rects[0].y, 0);
        assert_eq!(rects[1].y, rects[0].y + rects[0].height);
        assert_eq!(rects[2].y, rects[1].y + rects[1].height);
        assert_eq!(rects[2].bottom(), 10);
    }

    #[test]
    fn labels_render_and_result_shows_after_finish() {
        let mut a = app_with(&["Deploy", "Rollback"]);
        let text = render_to_text(&mut a, 30, 8);
        assert!(text.contains("Deploy"));
        assert!(text.contains("Rollback"));

        a.apply(RunMsg::Finished {
            idx: 0,
            outcome: Outcome {
                ok: true,
                code: Some(0),
                summary: "shipped".into(),
            },
        });
        let text = render_to_text(&mut a, 30, 8);
        assert!(text.contains("shipped"));
        assert!(text.contains('\u{2713}')); // ✓
    }

    #[test]
    fn tiny_and_odd_sizes_never_panic() {
        for &(w, h) in &[(1, 1), (2, 2), (4, 3), (3, 7), (12, 1), (40, 5)] {
            let mut a = app_with(&["A", "B", "C"]);
            let _ = render_to_text(&mut a, w, h);
        }
    }
}
