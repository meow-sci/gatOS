//! Transparent ratatui rendering: foreground color and markers carry hierarchy/selection; no pane,
//! modal, or selection paints an opaque background over the game.

use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, Clear, Paragraph, Wrap};
use ratatui::Frame;

use crate::app::{App, Modal, UiAction};

const TITLE: Color = Color::Cyan;
const LABEL: Color = Color::DarkGray;
const VALUE: Color = Color::White;
const ACCENT: Color = Color::LightCyan;
const GOOD: Color = Color::Green;
const WARN: Color = Color::Yellow;
const BAD: Color = Color::Red;

pub fn render(frame: &mut Frame, app: &mut App) {
    app.shot_rows.clear();
    app.inspector_rows.clear();
    app.actions.clear();
    let area = frame.area();
    if area.width == 0 || area.height == 0 {
        return;
    }
    let rows = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(if area.height >= 4 { 2 } else { 1 }),
    ])
    .split(area);
    render_header(frame, app, rows[0]);
    if area.width >= 72 {
        let panes = Layout::horizontal([
            Constraint::Percentage(30),
            Constraint::Percentage(42),
            Constraint::Percentage(28),
        ])
        .split(rows[1]);
        render_shots(frame, app, panes[0]);
        render_inspector(frame, app, panes[1]);
        render_live(frame, app, panes[2]);
    } else {
        let panes = Layout::vertical([
            Constraint::Percentage(38),
            Constraint::Percentage(38),
            Constraint::Percentage(24),
        ])
        .split(rows[1]);
        render_shots(frame, app, panes[0]);
        render_inspector(frame, app, panes[1]);
        render_live(frame, app, panes[2]);
    }
    render_footer(frame, app, rows[2]);
    render_modal(frame, app);
}

fn render_header(frame: &mut Frame, app: &App, area: Rect) {
    let dirty = if app.dirty { "*" } else { "" };
    let path = app
        .project_path
        .as_ref()
        .map(|path| path.display().to_string())
        .unwrap_or_else(|| "unsaved".into());
    let line = Line::from(vec![
        Span::styled(
            " photog ",
            Style::new().fg(TITLE).add_modifier(Modifier::BOLD),
        ),
        Span::styled(
            format!("{}{}", app.project.name, dirty),
            Style::new().fg(VALUE).add_modifier(Modifier::BOLD),
        ),
        Span::styled(
            format!(
                "  {} shots · {}  ",
                app.project.shots.len(),
                if app.project.r#loop { "loop" } else { "once" }
            ),
            Style::new().fg(LABEL),
        ),
        Span::styled(path, Style::new().fg(LABEL)),
    ]);
    frame.render_widget(Paragraph::new(line), area);
}

fn render_shots(frame: &mut Frame, app: &mut App, area: Rect) {
    let block = Block::default()
        .borders(Borders::ALL)
        .border_style(Style::new().fg(LABEL))
        .title(Span::styled(" shots / timeline ", Style::new().fg(TITLE)));
    let inner = block.inner(area);
    frame.render_widget(block, area);
    if inner.width == 0 || inner.height == 0 {
        return;
    }
    let offset = app
        .selected
        .saturating_sub(inner.height.saturating_sub(1) as usize);
    for (row, index) in (offset..app.project.shots.len())
        .take(inner.height as usize)
        .enumerate()
    {
        let shot = &app.project.shots[index];
        let selected = index == app.selected;
        let duration_cells =
            ((shot.duration_s.ceil() as usize).clamp(1, 10)).min(inner.width as usize);
        let timeline = "━".repeat(duration_cells);
        let text = format!(
            "{} {:>2}. {:<14} {:>5.1}s {}",
            if selected { "▶" } else { " " },
            index + 1,
            shot.name,
            shot.duration_s,
            timeline
        );
        let rect = Rect::new(inner.x, inner.y + row as u16, inner.width, 1);
        app.shot_rows.push((rect, index));
        frame.render_widget(
            Paragraph::new(text).style(if selected {
                Style::new().fg(ACCENT).add_modifier(Modifier::BOLD)
            } else {
                Style::new().fg(VALUE)
            }),
            rect,
        );
    }
}

fn render_inspector(frame: &mut Frame, app: &mut App, area: Rect) {
    let title = format!(" inspector · {} ", app.shot().kind.label());
    let block = Block::default()
        .borders(Borders::ALL)
        .border_style(Style::new().fg(LABEL))
        .title(Span::styled(title, Style::new().fg(TITLE)));
    let inner = block.inner(area);
    frame.render_widget(block, area);
    if inner.width == 0 || inner.height == 0 {
        return;
    }
    let fields = app.fields();
    let offset = app
        .field_cursor
        .saturating_sub(inner.height.saturating_sub(1) as usize);
    for (row, index) in (offset..fields.len())
        .take(inner.height as usize)
        .enumerate()
    {
        let field = fields[index];
        let selected = index == app.field_cursor;
        let line = Line::from(vec![
            Span::styled(if selected { "▶ " } else { "  " }, Style::new().fg(ACCENT)),
            Span::styled(
                format!("{:<13}", field.label(app.shot())),
                Style::new().fg(LABEL),
            ),
            Span::styled(
                app.field_value(field),
                Style::new()
                    .fg(if selected { ACCENT } else { VALUE })
                    .add_modifier(if selected {
                        Modifier::BOLD
                    } else {
                        Modifier::empty()
                    }),
            ),
        ]);
        let rect = Rect::new(inner.x, inner.y + row as u16, inner.width, 1);
        app.inspector_rows.push((rect, index));
        frame.render_widget(Paragraph::new(line), rect);
    }
}

fn render_live(frame: &mut Frame, app: &App, area: Rect) {
    let connection = if app.live.connected {
        "online"
    } else {
        "offline"
    };
    let block = Block::default()
        .borders(Borders::ALL)
        .border_style(Style::new().fg(LABEL))
        .title(Span::styled(
            format!(" live · {connection} "),
            Style::new().fg(if app.live.connected { GOOD } else { BAD }),
        ));
    let inner = block.inner(area);
    frame.render_widget(block, area);
    let mut lines = vec![
        kv("transport", &app.live.label),
        kv("camera", if app.live.owned { "owned" } else { "idle" }),
        kv(
            "playback",
            if app.live.playback_state.is_empty() {
                "—"
            } else {
                &app.live.playback_state
            },
        ),
        kv(
            "shot",
            if app.live.shot.is_empty() {
                "—"
            } else {
                &app.live.shot
            },
        ),
        kv(
            "time",
            &format!(
                "{:.1}/{:.1}s",
                app.live.position_ms / 1000.0,
                app.live.duration_ms / 1000.0
            ),
        ),
        kv("rate", &format!("{:.2}x", app.live.rate)),
        kv(
            "cue",
            if app.live.cue_state.is_empty() {
                "—"
            } else {
                &app.live.cue_state
            },
        ),
        kv("dropped", &app.live.cue_dropped.to_string()),
    ];
    if let Some(gate) = &app.live.gate_failure {
        lines.push(Line::from(Span::styled(gate.clone(), Style::new().fg(BAD))));
    }
    if app.live.camera_last_error != "-" && !app.live.camera_last_error.is_empty() {
        lines.push(Line::from(Span::styled(
            format!("camera: {}", app.live.camera_last_error),
            Style::new().fg(BAD),
        )));
    }
    if app.live.cue_last_error != "-" && !app.live.cue_last_error.is_empty() {
        lines.push(Line::from(Span::styled(
            format!("cues: {}", app.live.cue_last_error),
            Style::new().fg(BAD),
        )));
    }
    frame.render_widget(Paragraph::new(lines).wrap(Wrap { trim: true }), inner);
}

fn kv(key: &str, value: &str) -> Line<'static> {
    Line::from(vec![
        Span::styled(format!("{key:<10}"), Style::new().fg(LABEL)),
        Span::styled(value.to_string(), Style::new().fg(VALUE)),
    ])
}

fn render_footer(frame: &mut Frame, app: &mut App, area: Rect) {
    if area.height == 0 {
        return;
    }
    let buttons = [
        ("[a add]", UiAction::Add),
        ("[p play]", UiAction::Play),
        ("[v preview]", UiAction::Preview),
        ("[z stop]", UiAction::Stop),
        ("[s save]", UiAction::Save),
    ];
    let mut x = area.x;
    for (label, action) in buttons {
        let width = (label.chars().count() as u16 + 1).min(area.x + area.width - x);
        if width == 0 {
            break;
        }
        let rect = Rect::new(x, area.y, width, 1);
        app.actions.push((rect, action));
        frame.render_widget(
            Paragraph::new(label).style(Style::new().fg(ACCENT).add_modifier(Modifier::BOLD)),
            rect,
        );
        x += width;
    }
    let status_x = x.min(area.x + area.width);
    frame.render_widget(
        Paragraph::new(app.status.clone()).style(Style::new().fg(if app.status_error {
            BAD
        } else {
            WARN
        })),
        Rect::new(status_x, area.y, area.x + area.width - status_x, 1),
    );
    if area.height > 1 {
        frame.render_widget(
            Paragraph::new("↑↓ shots · Tab fields · Enter edit · P from shot · space pause · [ ] rate · , . scrub · l loop · ! release · ? help")
                .style(Style::new().fg(LABEL)),
            Rect::new(area.x, area.y + 1, area.width, 1),
        );
    }
}

fn render_modal(frame: &mut Frame, app: &mut App) {
    let Some(modal) = app.modal.as_mut() else {
        return;
    };
    match modal {
        Modal::Picker(picker) => {
            let height = (picker.options.len() as u16 + 2).min(frame.area().height.max(1));
            let area = centered(frame.area(), 44.min(frame.area().width), height);
            picker.area = area;
            picker.rows.clear();
            frame.render_widget(Clear, area);
            let block = Block::default()
                .borders(Borders::ALL)
                .border_style(Style::new().fg(ACCENT))
                .title(Span::styled(
                    format!(" {} ", picker.title),
                    Style::new().fg(TITLE),
                ));
            let inner = block.inner(area);
            frame.render_widget(block, area);
            let offset = picker
                .selected
                .saturating_sub(inner.height.saturating_sub(1) as usize);
            for (row, index) in (offset..picker.options.len())
                .take(inner.height as usize)
                .enumerate()
            {
                let rect = Rect::new(inner.x, inner.y + row as u16, inner.width, 1);
                picker.rows.push((rect, index));
                let selected = index == picker.selected;
                frame.render_widget(
                    Paragraph::new(format!(
                        "{} {}",
                        if selected { "▶" } else { " " },
                        picker.options[index]
                    ))
                    .style(Style::new().fg(if selected {
                        ACCENT
                    } else {
                        VALUE
                    })),
                    rect,
                );
            }
        }
        Modal::Editor(editor) => {
            let area = centered(
                frame.area(),
                58.min(frame.area().width),
                3.min(frame.area().height),
            );
            editor.area = area;
            frame.render_widget(Clear, area);
            let block = Block::default()
                .borders(Borders::ALL)
                .border_style(Style::new().fg(ACCENT))
                .title(Span::styled(
                    format!(" {} ", editor.title),
                    Style::new().fg(TITLE),
                ));
            frame.render_widget(
                Paragraph::new(format!("{}▏", editor.input))
                    .style(Style::new().fg(VALUE))
                    .block(block),
                area,
            );
        }
        Modal::Help(help_area) => {
            let area = centered(
                frame.area(),
                70.min(frame.area().width),
                12.min(frame.area().height),
            );
            *help_area = area;
            frame.render_widget(Clear, area);
            let text = "Shot editor\n  ↑↓ select shot · Tab select field · Enter/click edit\n  a add · y duplicate · x delete · J/K reorder · r rename\n  s save · S save-as · R reload\n\nPlayback\n  p whole take · P from selected · v preview selected\n  space pause/resume · [ ] rate · , . scrub · l loop · z stop\n  ! emergency hard release · g refresh targets · q quit/release\n\nRanges use start..end; vectors use x y z. Esc closes a modal.";
            frame.render_widget(
                Paragraph::new(text)
                    .style(Style::new().fg(VALUE))
                    .wrap(Wrap { trim: false })
                    .block(
                        Block::default()
                            .borders(Borders::ALL)
                            .border_style(Style::new().fg(ACCENT))
                            .title(Span::styled(" help ", Style::new().fg(TITLE))),
                    ),
                area,
            );
        }
    }
}

fn centered(area: Rect, width: u16, height: u16) -> Rect {
    Rect::new(
        area.x + area.width.saturating_sub(width) / 2,
        area.y + area.height.saturating_sub(height) / 2,
        width.min(area.width),
        height.min(area.height),
    )
}

#[cfg(test)]
mod tests {
    use std::sync::mpsc;

    use photog::Project;
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;

    use super::*;

    fn screen(width: u16, height: u16) -> String {
        let (tx, _) = mpsc::channel();
        let mut app = App::new(Project::new("trailer"), None, tx);
        let mut terminal = Terminal::new(TestBackend::new(width, height)).unwrap();
        terminal.draw(|frame| render(frame, &mut app)).unwrap();
        let buffer = terminal.backend().buffer();
        let mut output = String::new();
        for y in 0..height {
            for x in 0..width {
                output.push_str(buffer[(x, y)].symbol());
            }
            output.push('\n');
        }
        output
    }

    #[test]
    fn normal_layout_has_all_three_panes() {
        let output = screen(110, 30);
        assert!(output.contains("shots / timeline"));
        assert!(output.contains("inspector"));
        assert!(output.contains("live"));
    }

    #[test]
    fn narrow_layout_stacks_without_panicking() {
        let output = screen(45, 18);
        assert!(output.contains("photog"));
        assert!(output.contains("shots / timeline"));
    }

    #[test]
    fn very_short_layout_still_renders_controls() {
        let output = screen(24, 5);
        assert!(output.contains("photog"));
        assert!(output.contains("[a add]"));
    }
}
