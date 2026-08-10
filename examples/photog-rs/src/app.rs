//! Editor state and the keyboard/mouse interaction model. Modal pickers and text editors both call
//! the same apply functions, which keeps mouse and keyboard behavior equivalent.

use std::path::PathBuf;
use std::sync::mpsc::Sender;
use std::time::{SystemTime, UNIX_EPOCH};

use photog::{
    compile_project, load_project, save_project_atomic, AimUp, CameraFrame, CompileRequest,
    EaseKind, LiveStatus, Project, Projection, Range, Shot, ShotKind, SubjectRef, Vec3,
};
use ratatui::crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::layout::{Position, Rect};

use crate::worker::{Request, Update};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Field {
    Name,
    Kind,
    Duration,
    Blend,
    Anchor,
    AimTarget,
    AimOffset,
    Frame,
    AimFrame,
    AimUp,
    Smoothing,
    Ease,
    EasePower,
    Fov,
    Roll,
    Projection,
    PlacementA,
    PlacementB,
    PlacementC,
}

impl Field {
    pub fn label(self, shot: &Shot) -> &'static str {
        match self {
            Self::Name => "name",
            Self::Kind => "type",
            Self::Duration => "duration",
            Self::Blend => "blend in",
            Self::Anchor => "anchor",
            Self::AimTarget => "aim target",
            Self::AimOffset => "aim offset",
            Self::Frame => "pose frame",
            Self::AimFrame => "aim frame",
            Self::AimUp => "aim up",
            Self::Smoothing => "smoothing",
            Self::Ease => "easing",
            Self::EasePower => "ease power",
            Self::Fov => "FOV",
            Self::Roll => "roll",
            Self::Projection => "projection",
            Self::PlacementA => match shot.kind {
                ShotKind::Orbit { .. } => "radius",
                ShotKind::Dolly { .. } => "from",
                ShotKind::Chase { .. } => "offset",
                ShotKind::Static { .. } => "position",
                ShotKind::LensOnly => "placement",
            },
            Self::PlacementB => match shot.kind {
                ShotKind::Orbit { .. } => "azimuth",
                ShotKind::Dolly { .. } => "to",
                _ => "",
            },
            Self::PlacementC => match shot.kind {
                ShotKind::Orbit { .. } => "elevation",
                _ => "",
            },
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PickerAction {
    Edit(Field),
    AddShot,
}

#[derive(Debug, Clone)]
pub struct Picker {
    pub title: String,
    pub action: PickerAction,
    pub options: Vec<String>,
    pub selected: usize,
    pub area: Rect,
    pub rows: Vec<(Rect, usize)>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EditorAction {
    Edit(Field),
    SaveAs,
}

#[derive(Debug, Clone)]
pub struct Editor {
    pub title: String,
    pub action: EditorAction,
    pub input: String,
    pub area: Rect,
}

#[derive(Debug, Clone)]
pub enum Modal {
    Picker(Picker),
    Editor(Editor),
    Help(Rect),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UiAction {
    Add,
    Play,
    Preview,
    Stop,
    Save,
}

pub struct App {
    pub project: Project,
    pub project_path: Option<PathBuf>,
    pub dirty: bool,
    pub selected: usize,
    pub field_cursor: usize,
    pub vessels: Vec<String>,
    pub bodies: Vec<String>,
    pub live: LiveStatus,
    pub rate: f64,
    pub paused: bool,
    pub status: String,
    pub status_error: bool,
    pub modal: Option<Modal>,
    pub shot_rows: Vec<(Rect, usize)>,
    pub inspector_rows: Vec<(Rect, usize)>,
    pub actions: Vec<(Rect, UiAction)>,
    pub should_quit: bool,
    tx: Sender<Request>,
}

impl App {
    pub fn new(project: Project, project_path: Option<PathBuf>, tx: Sender<Request>) -> Self {
        Self {
            project,
            project_path,
            dirty: false,
            selected: 0,
            field_cursor: 0,
            vessels: Vec::new(),
            bodies: Vec::new(),
            live: LiveStatus::default(),
            rate: 1.0,
            paused: false,
            status: "connecting…".into(),
            status_error: false,
            modal: None,
            shot_rows: Vec::new(),
            inspector_rows: Vec::new(),
            actions: Vec::new(),
            should_quit: false,
            tx,
        }
    }

    pub fn fields(&self) -> Vec<Field> {
        let mut fields = vec![
            Field::Name,
            Field::Kind,
            Field::Duration,
            Field::Blend,
            Field::Anchor,
            Field::AimTarget,
            Field::AimOffset,
            Field::Frame,
            Field::AimFrame,
            Field::AimUp,
            Field::Smoothing,
            Field::Ease,
            Field::EasePower,
            Field::Fov,
            Field::Roll,
            Field::Projection,
        ];
        match self.shot().kind {
            ShotKind::Orbit { .. } => {
                fields.extend([Field::PlacementA, Field::PlacementB, Field::PlacementC])
            }
            ShotKind::Dolly { .. } => fields.extend([Field::PlacementA, Field::PlacementB]),
            ShotKind::Chase { .. } | ShotKind::Static { .. } => fields.push(Field::PlacementA),
            ShotKind::LensOnly => {}
        }
        fields
    }

    pub fn shot(&self) -> &Shot {
        &self.project.shots[self.selected.min(self.project.shots.len() - 1)]
    }

    fn shot_mut(&mut self) -> &mut Shot {
        let index = self.selected.min(self.project.shots.len() - 1);
        &mut self.project.shots[index]
    }

    pub fn field_value(&self, field: Field) -> String {
        let shot = self.shot();
        match field {
            Field::Name => shot.name.clone(),
            Field::Kind => shot.kind.label().into(),
            Field::Duration => format!("{} s", number(shot.duration_s)),
            Field::Blend => format!("{} s", number(shot.blend_in_s)),
            Field::Anchor => shot.anchor.wire(),
            Field::AimTarget => shot.aim.target.wire(),
            Field::AimOffset => vec_text(shot.aim.offset_m),
            Field::Frame => shot.frame.token().into(),
            Field::AimFrame => shot.aim.frame.token().into(),
            Field::AimUp => shot.aim.up.token().into(),
            Field::Smoothing => format!("{} s", number(shot.smoothing_s)),
            Field::Ease => shot.ease.kind.token().into(),
            Field::EasePower => number(shot.ease.power),
            Field::Fov => shot
                .lens
                .fov_deg
                .as_ref()
                .map(range_text)
                .unwrap_or_else(|| "—".into()),
            Field::Roll => shot
                .lens
                .roll_deg
                .as_ref()
                .map(range_text)
                .unwrap_or_else(|| "—".into()),
            Field::Projection => match shot.lens.projection {
                Projection::Perspective => "perspective".into(),
                Projection::Orthographic { half_height_m } => {
                    format!("orthographic · {} m", number(half_height_m))
                }
            },
            Field::PlacementA => match &shot.kind {
                ShotKind::Orbit { radius_m, .. } => range_text(radius_m),
                ShotKind::Dolly { from_m, .. } => vec_text(*from_m),
                ShotKind::Chase { offset_m } => vec_text(*offset_m),
                ShotKind::Static { position_m } => vec_text(*position_m),
                ShotKind::LensOnly => "—".into(),
            },
            Field::PlacementB => match &shot.kind {
                ShotKind::Orbit { azimuth_deg, .. } => range_text(azimuth_deg),
                ShotKind::Dolly { to_m, .. } => vec_text(*to_m),
                _ => "—".into(),
            },
            Field::PlacementC => match &shot.kind {
                ShotKind::Orbit { elevation_deg, .. } => range_text(elevation_deg),
                _ => "—".into(),
            },
        }
    }

    pub fn apply_update(&mut self, update: Update) {
        match update {
            Update::Live(live) => {
                self.live = live;
                if self.live.connected && self.status.starts_with("connecting") {
                    self.set_status("ready", false);
                }
            }
            Update::Targets { vessels, bodies } => {
                self.vessels = vessels;
                self.bodies = bodies;
                self.set_status("live targets refreshed", false);
            }
            Update::Done(result) => match result {
                Ok(message) => self.set_status(&message, false),
                Err(message) => self.set_status(&message, true),
            },
        }
    }

    pub fn on_key(&mut self, key: KeyEvent) {
        if self.modal.is_some() {
            self.on_modal_key(key);
            return;
        }
        if key.modifiers.contains(KeyModifiers::CONTROL) && key.code == KeyCode::Char('c') {
            self.quit();
            return;
        }
        match key.code {
            KeyCode::Char('q') => self.quit(),
            KeyCode::Up | KeyCode::Char('k') => self.select_delta(-1),
            KeyCode::Down | KeyCode::Char('j') => self.select_delta(1),
            KeyCode::BackTab => self.field_delta(-1),
            KeyCode::Tab => self.field_delta(1),
            KeyCode::Enter => self.open_current_field(),
            KeyCode::Char('a') => self.open_add_picker(),
            KeyCode::Char('y') => self.duplicate(),
            KeyCode::Char('x') => self.delete(),
            KeyCode::Char('J') => self.reorder(1),
            KeyCode::Char('K') => self.reorder(-1),
            KeyCode::Char('r') => self.open_editor(Field::Name),
            KeyCode::Char('s') => self.save(false),
            KeyCode::Char('S') => self.save(true),
            KeyCode::Char('R') => self.reload(),
            KeyCode::Char('p') => self.play(0, false),
            KeyCode::Char('P') => self.play(self.selected, false),
            KeyCode::Char('v') => self.play(self.selected, true),
            KeyCode::Char(' ') => self.toggle_pause(),
            KeyCode::Char('[') => self.change_rate(-0.25),
            KeyCode::Char(']') => self.change_rate(0.25),
            KeyCode::Char(',') => self.scrub(-1.0),
            KeyCode::Char('.') => self.scrub(1.0),
            KeyCode::Char('l') => self.toggle_loop(),
            KeyCode::Char('g') => {
                let _ = self.tx.send(Request::RefreshTargets);
            }
            KeyCode::Char('z') => {
                let _ = self.tx.send(Request::Stop);
            }
            KeyCode::Char('!') => {
                let _ = self.tx.send(Request::EmergencyRelease);
            }
            KeyCode::Char('?') => self.modal = Some(Modal::Help(Rect::ZERO)),
            _ => {}
        }
    }

    pub fn on_mouse(&mut self, mouse: MouseEvent) {
        if self.modal.is_some() {
            self.on_modal_mouse(mouse);
            return;
        }
        let position = Position::new(mouse.column, mouse.row);
        match mouse.kind {
            MouseEventKind::Down(MouseButton::Left) => {
                if let Some((_, index)) = self
                    .shot_rows
                    .iter()
                    .find(|(rect, _)| rect.contains(position))
                {
                    self.selected = *index;
                    self.field_cursor = 0;
                    return;
                }
                if let Some((_, index)) = self
                    .inspector_rows
                    .iter()
                    .find(|(rect, _)| rect.contains(position))
                {
                    self.field_cursor = *index;
                    self.open_current_field();
                    return;
                }
                if let Some((_, action)) = self
                    .actions
                    .iter()
                    .find(|(rect, _)| rect.contains(position))
                {
                    self.activate(*action);
                }
            }
            MouseEventKind::ScrollUp => self.select_delta(-1),
            MouseEventKind::ScrollDown => self.select_delta(1),
            _ => {}
        }
    }

    fn activate(&mut self, action: UiAction) {
        match action {
            UiAction::Add => self.open_add_picker(),
            UiAction::Play => self.play(0, false),
            UiAction::Preview => self.play(self.selected, true),
            UiAction::Stop => {
                let _ = self.tx.send(Request::Stop);
            }
            UiAction::Save => self.save(false),
        }
    }

    fn quit(&mut self) {
        let _ = self.tx.send(Request::Stop);
        self.should_quit = true;
    }

    fn select_delta(&mut self, delta: isize) {
        self.selected = move_index(self.selected, delta, self.project.shots.len());
        self.field_cursor = 0;
    }

    fn field_delta(&mut self, delta: isize) {
        self.field_cursor = move_index(self.field_cursor, delta, self.fields().len());
    }

    fn open_current_field(&mut self) {
        let fields = self.fields();
        if let Some(field) = fields.get(self.field_cursor).copied() {
            match field {
                Field::Kind => self.open_picker(
                    "shot type",
                    PickerAction::Edit(field),
                    ["orbit", "dolly", "chase/attach", "static", "lens-only"]
                        .into_iter()
                        .map(str::to_string)
                        .collect(),
                    self.shot().kind.label(),
                ),
                Field::Anchor | Field::AimTarget => {
                    let mut options = vec!["none".into()];
                    options.extend(self.vessels.iter().map(|id| format!("vessel:{id}")));
                    options.extend(self.bodies.iter().map(|id| format!("body:{id}")));
                    let current = if field == Field::Anchor {
                        self.shot().anchor.wire()
                    } else {
                        self.shot().aim.target.wire()
                    };
                    if !options.contains(&current) {
                        options.insert(1, current.clone());
                    }
                    self.open_picker("live target", PickerAction::Edit(field), options, &current);
                }
                Field::Frame | Field::AimFrame => self.open_picker(
                    "camera frame",
                    PickerAction::Edit(field),
                    CameraFrame::ALL
                        .iter()
                        .map(|value| value.token().into())
                        .collect(),
                    if field == Field::Frame {
                        self.shot().frame.token()
                    } else {
                        self.shot().aim.frame.token()
                    },
                ),
                Field::AimUp => self.open_picker(
                    "aim up",
                    PickerAction::Edit(field),
                    AimUp::ALL
                        .iter()
                        .map(|value| value.token().into())
                        .collect(),
                    self.shot().aim.up.token(),
                ),
                Field::Ease => self.open_picker(
                    "easing",
                    PickerAction::Edit(field),
                    EaseKind::ALL
                        .iter()
                        .map(|value| value.token().into())
                        .collect(),
                    self.shot().ease.kind.token(),
                ),
                Field::Projection => self.open_picker(
                    "projection",
                    PickerAction::Edit(field),
                    vec!["perspective".into(), "orthographic".into()],
                    match self.shot().lens.projection {
                        Projection::Perspective => "perspective",
                        Projection::Orthographic { .. } => "orthographic",
                    },
                ),
                _ => self.open_editor(field),
            }
        }
    }

    fn open_add_picker(&mut self) {
        self.open_picker(
            "add shot",
            PickerAction::AddShot,
            ["orbit", "dolly", "chase/attach", "static", "lens-only"]
                .into_iter()
                .map(str::to_string)
                .collect(),
            "orbit",
        );
    }

    fn open_picker(
        &mut self,
        title: &str,
        action: PickerAction,
        options: Vec<String>,
        current: &str,
    ) {
        let selected = options
            .iter()
            .position(|value| value == current)
            .unwrap_or(0);
        self.modal = Some(Modal::Picker(Picker {
            title: title.into(),
            action,
            options,
            selected,
            area: Rect::ZERO,
            rows: Vec::new(),
        }));
    }

    fn open_editor(&mut self, field: Field) {
        let value = self.editor_value(field);
        self.modal = Some(Modal::Editor(Editor {
            title: format!("edit {}", field.label(self.shot())),
            action: EditorAction::Edit(field),
            input: value,
            area: Rect::ZERO,
        }));
    }

    fn editor_value(&self, field: Field) -> String {
        let shot = self.shot();
        match field {
            Field::Duration => number(shot.duration_s),
            Field::Blend => number(shot.blend_in_s),
            Field::Smoothing => number(shot.smoothing_s),
            Field::EasePower => number(shot.ease.power),
            Field::Fov => shot
                .lens
                .fov_deg
                .as_ref()
                .map(range_text)
                .unwrap_or_default(),
            Field::Roll => shot
                .lens
                .roll_deg
                .as_ref()
                .map(range_text)
                .unwrap_or_default(),
            _ => self.field_value(field),
        }
    }

    fn on_modal_key(&mut self, key: KeyEvent) {
        if matches!(key.code, KeyCode::Esc) {
            self.modal = None;
            return;
        }
        match self.modal.as_mut() {
            Some(Modal::Picker(picker)) => match key.code {
                KeyCode::Up | KeyCode::Char('k') | KeyCode::BackTab => {
                    picker.selected = move_index(picker.selected, -1, picker.options.len());
                }
                KeyCode::Down | KeyCode::Char('j') | KeyCode::Tab => {
                    picker.selected = move_index(picker.selected, 1, picker.options.len());
                }
                KeyCode::Enter | KeyCode::Char(' ') => self.confirm_picker(),
                _ => {}
            },
            Some(Modal::Editor(editor)) => match key.code {
                KeyCode::Enter => self.confirm_editor(),
                KeyCode::Backspace => {
                    editor.input.pop();
                }
                KeyCode::Char(character) if !key.modifiers.contains(KeyModifiers::CONTROL) => {
                    editor.input.push(character);
                }
                _ => {}
            },
            Some(Modal::Help(_)) => self.modal = None,
            None => {}
        }
    }

    fn on_modal_mouse(&mut self, mouse: MouseEvent) {
        if mouse.kind != MouseEventKind::Down(MouseButton::Left) {
            return;
        }
        let position = Position::new(mouse.column, mouse.row);
        match self.modal.as_mut() {
            Some(Modal::Picker(picker)) => {
                if let Some((_, index)) =
                    picker.rows.iter().find(|(rect, _)| rect.contains(position))
                {
                    picker.selected = *index;
                    self.confirm_picker();
                } else if !picker.area.contains(position) {
                    self.modal = None;
                }
            }
            Some(Modal::Editor(editor)) => {
                if !editor.area.contains(position) {
                    self.modal = None;
                }
            }
            Some(Modal::Help(area)) if !area.contains(position) => self.modal = None,
            Some(Modal::Help(_)) => {}
            None => {}
        }
    }

    fn confirm_picker(&mut self) {
        let Some(Modal::Picker(picker)) = self.modal.take() else {
            return;
        };
        let value = picker.options[picker.selected].clone();
        match picker.action {
            PickerAction::AddShot => {
                let mut shot = self.shot().clone();
                shot.name = unique_shot_name(&self.project, &value);
                shot.kind = ShotKind::preset(&value);
                if matches!(shot.kind, ShotKind::Chase { .. }) {
                    shot.frame = CameraFrame::Chase;
                } else if !matches!(shot.kind, ShotKind::LensOnly)
                    && shot.anchor == SubjectRef::None
                {
                    if let Some(vessel) = self.vessels.first() {
                        shot.anchor = SubjectRef::Vessel(vessel.clone());
                        shot.aim.target = SubjectRef::Vessel(vessel.clone());
                        shot.frame = CameraFrame::Bodyfixed;
                    }
                }
                let at = self.selected + 1;
                self.project.shots.insert(at, shot);
                self.selected = at;
                self.changed("shot added");
            }
            PickerAction::Edit(field) => {
                if let Err(error) = self.apply_picker(field, &value) {
                    self.set_status(&error, true);
                } else {
                    self.changed("field updated");
                }
            }
        }
    }

    fn apply_picker(&mut self, field: Field, value: &str) -> Result<(), String> {
        let shot = self.shot_mut();
        match field {
            Field::Kind => {
                shot.kind = ShotKind::preset(value);
                if matches!(shot.kind, ShotKind::Chase { .. }) {
                    shot.frame = CameraFrame::Chase;
                }
            }
            Field::Anchor => shot.anchor = SubjectRef::parse(value)?,
            Field::AimTarget => shot.aim.target = SubjectRef::parse(value)?,
            Field::Frame => shot.frame = parse_frame(value)?,
            Field::AimFrame => shot.aim.frame = parse_frame(value)?,
            Field::AimUp => shot.aim.up = parse_up(value)?,
            Field::Ease => shot.ease.kind = parse_ease(value)?,
            Field::Projection => {
                shot.lens.projection = if value == "orthographic" {
                    Projection::Orthographic {
                        half_height_m: 20.0,
                    }
                } else {
                    Projection::Perspective
                };
            }
            _ => return Err("this field is not a picker".into()),
        }
        Ok(())
    }

    fn confirm_editor(&mut self) {
        let Some(Modal::Editor(editor)) = self.modal.take() else {
            return;
        };
        match editor.action {
            EditorAction::SaveAs => self.save_to(PathBuf::from(editor.input)),
            EditorAction::Edit(field) => match self.apply_text(field, editor.input.trim()) {
                Ok(()) => self.changed("field updated"),
                Err(error) => self.set_status(&error, true),
            },
        }
    }

    fn apply_text(&mut self, field: Field, value: &str) -> Result<(), String> {
        let shot = self.shot_mut();
        match field {
            Field::Name => {
                if value.is_empty() {
                    return Err("name cannot be empty".into());
                }
                shot.name = value.into();
            }
            Field::Duration => shot.duration_s = parse_number(value)?,
            Field::Blend => shot.blend_in_s = parse_number(value)?,
            Field::AimOffset => shot.aim.offset_m = parse_vec(value)?,
            Field::Smoothing => shot.smoothing_s = parse_number(value)?,
            Field::EasePower => shot.ease.power = parse_number(value)?,
            Field::Fov => {
                shot.lens.fov_deg = if value.is_empty() {
                    None
                } else {
                    Some(parse_range(value)?)
                }
            }
            Field::Roll => {
                shot.lens.roll_deg = if value.is_empty() {
                    None
                } else {
                    Some(parse_range(value)?)
                }
            }
            Field::PlacementA => match &mut shot.kind {
                ShotKind::Orbit { radius_m, .. } => *radius_m = parse_range(value)?,
                ShotKind::Dolly { from_m, .. } => *from_m = parse_vec(value)?,
                ShotKind::Chase { offset_m } => *offset_m = parse_vec(value)?,
                ShotKind::Static { position_m } => *position_m = parse_vec(value)?,
                ShotKind::LensOnly => return Err("lens-only has no placement".into()),
            },
            Field::PlacementB => match &mut shot.kind {
                ShotKind::Orbit { azimuth_deg, .. } => *azimuth_deg = parse_range(value)?,
                ShotKind::Dolly { to_m, .. } => *to_m = parse_vec(value)?,
                _ => return Err("this shot has no second placement field".into()),
            },
            Field::PlacementC => match &mut shot.kind {
                ShotKind::Orbit { elevation_deg, .. } => *elevation_deg = parse_range(value)?,
                _ => return Err("this shot has no third placement field".into()),
            },
            _ => return Err("this field uses a picker".into()),
        }
        Ok(())
    }

    fn duplicate(&mut self) {
        let mut shot = self.shot().clone();
        shot.name = unique_shot_name(&self.project, &format!("{}-copy", shot.name));
        self.selected += 1;
        self.project.shots.insert(self.selected, shot);
        self.changed("shot duplicated");
    }

    fn delete(&mut self) {
        if self.project.shots.len() == 1 {
            self.project.shots[0] = Shot::default();
        } else {
            self.project.shots.remove(self.selected);
            self.selected = self.selected.min(self.project.shots.len() - 1);
        }
        self.changed("shot deleted");
    }

    fn reorder(&mut self, delta: isize) {
        let target = move_index_clamped(self.selected, delta, self.project.shots.len());
        if target != self.selected {
            self.project.shots.swap(self.selected, target);
            self.selected = target;
            self.changed("shot reordered");
        }
    }

    fn save(&mut self, save_as: bool) {
        if save_as || self.project_path.is_none() {
            self.modal = Some(Modal::Editor(Editor {
                title: "save project as".into(),
                action: EditorAction::SaveAs,
                input: self
                    .project_path
                    .as_ref()
                    .map(|path| path.display().to_string())
                    .unwrap_or_else(|| "take.photog.json".into()),
                area: Rect::ZERO,
            }));
        } else if let Some(path) = self.project_path.clone() {
            self.save_to(path);
        }
    }

    fn save_to(&mut self, path: PathBuf) {
        match save_project_atomic(&path, &self.project) {
            Ok(()) => {
                self.project_path = Some(path.clone());
                self.dirty = false;
                self.set_status(&format!("saved {}", path.display()), false);
            }
            Err(error) => self.set_status(&format!("save: {error}"), true),
        }
    }

    fn reload(&mut self) {
        let Some(path) = self.project_path.clone() else {
            self.set_status("reload needs a saved project", true);
            return;
        };
        match load_project(&path) {
            Ok(project) => {
                self.project = project;
                self.selected = 0;
                self.field_cursor = 0;
                self.dirty = false;
                self.set_status("project reloaded", false);
            }
            Err(error) => self.set_status(&format!("reload: {error}"), true),
        }
    }

    fn play(&mut self, start: usize, preview: bool) {
        let mut project = self.project.clone();
        let actual_start = if preview {
            project.shots = vec![project.shots[start].clone()];
            project.r#loop = false;
            0
        } else {
            start
        };
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let mut request = CompileRequest::new(&format!("{}-{nonce}", project.name));
        request.start_shot = actual_start;
        match compile_project(&project, &request) {
            Ok(take) => {
                let warning = take.warnings.first().cloned();
                let _ = self.tx.send(Request::Play {
                    take,
                    rate: self.rate,
                });
                if let Some(warning) = warning {
                    self.set_status(&warning, false);
                } else {
                    self.set_status(
                        if preview {
                            "starting preview…"
                        } else {
                            "starting take…"
                        },
                        false,
                    );
                }
            }
            Err(errors) => {
                let message = errors
                    .first()
                    .map(ToString::to_string)
                    .unwrap_or_else(|| "project validation failed".into());
                self.set_status(&message, true);
            }
        }
    }

    fn toggle_pause(&mut self) {
        self.paused = !self.paused;
        let _ = self.tx.send(Request::Pause(self.paused));
    }

    fn change_rate(&mut self, delta: f64) {
        self.rate = (self.rate + delta).clamp(0.0, 100.0);
        let _ = self.tx.send(Request::Rate(self.rate));
    }

    fn scrub(&mut self, delta: f64) {
        let seconds = (self.live.position_ms / 1000.0 + delta).max(0.0);
        let _ = self.tx.send(Request::Scrub(seconds));
    }

    fn toggle_loop(&mut self) {
        self.project.r#loop = !self.project.r#loop;
        self.dirty = true;
        let _ = self.tx.send(Request::Loop(self.project.r#loop));
        self.set_status(
            if self.project.r#loop {
                "loop on"
            } else {
                "loop off"
            },
            false,
        );
    }

    fn changed(&mut self, message: &str) {
        self.dirty = true;
        self.set_status(message, false);
    }

    fn set_status(&mut self, message: &str, error: bool) {
        self.status = message.into();
        self.status_error = error;
    }
}

fn move_index(index: usize, delta: isize, length: usize) -> usize {
    if length == 0 {
        return 0;
    }
    (index as isize + delta).rem_euclid(length as isize) as usize
}

fn move_index_clamped(index: usize, delta: isize, length: usize) -> usize {
    (index as isize + delta).clamp(0, length.saturating_sub(1) as isize) as usize
}

fn unique_shot_name(project: &Project, base: &str) -> String {
    let mut candidate = base.to_string();
    let mut suffix = 2;
    while project.shots.iter().any(|shot| shot.name == candidate) {
        candidate = format!("{base}-{suffix}");
        suffix += 1;
    }
    candidate
}

fn parse_number(value: &str) -> Result<f64, String> {
    value.parse::<f64>().map_err(|_| "expected a number".into())
}

fn parse_range(value: &str) -> Result<Range<f64>, String> {
    if let Some((start, end)) = value.split_once("..") {
        Ok(Range::Animated {
            start: parse_number(start.trim())?,
            end: parse_number(end.trim())?,
        })
    } else {
        Ok(Range::Constant(parse_number(value)?))
    }
}

fn parse_vec(value: &str) -> Result<Vec3, String> {
    let values = value
        .split_whitespace()
        .map(parse_number)
        .collect::<Result<Vec<_>, _>>()?;
    if values.len() != 3 {
        return Err("expected three numbers: x y z".into());
    }
    Ok(Vec3(values[0], values[1], values[2]))
}

fn parse_frame(value: &str) -> Result<CameraFrame, String> {
    CameraFrame::ALL
        .into_iter()
        .find(|frame| frame.token() == value)
        .ok_or_else(|| "unknown camera frame".into())
}

fn parse_up(value: &str) -> Result<AimUp, String> {
    AimUp::ALL
        .into_iter()
        .find(|up| up.token() == value)
        .ok_or_else(|| "unknown aim-up mode".into())
}

fn parse_ease(value: &str) -> Result<EaseKind, String> {
    EaseKind::ALL
        .into_iter()
        .find(|ease| ease.token() == value)
        .ok_or_else(|| "unknown easing mode".into())
}

fn range_text(value: &Range<f64>) -> String {
    match value {
        Range::Constant(value) => number(*value),
        Range::Animated { start, end } => format!("{}..{}", number(*start), number(*end)),
    }
}

fn vec_text(value: Vec3) -> String {
    format!(
        "{} {} {}",
        number(value.0),
        number(value.1),
        number(value.2)
    )
}

fn number(value: f64) -> String {
    let mut out = format!("{value:.4}");
    while out.ends_with('0') {
        out.pop();
    }
    if out.ends_with('.') {
        out.pop();
    }
    out
}

#[cfg(test)]
mod tests {
    use std::sync::mpsc;

    use super::*;

    fn app() -> (App, mpsc::Receiver<Request>) {
        let (tx, rx) = mpsc::channel();
        (App::new(Project::new("test"), None, tx), rx)
    }

    #[test]
    fn keyboard_add_duplicate_delete_and_reorder_mark_dirty() {
        let (mut app, _) = app();
        app.on_key(KeyEvent::new(KeyCode::Char('a'), KeyModifiers::NONE));
        assert!(matches!(app.modal, Some(Modal::Picker(_))));
        app.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert_eq!(app.project.shots.len(), 2);
        app.on_key(KeyEvent::new(KeyCode::Char('y'), KeyModifiers::NONE));
        assert_eq!(app.project.shots.len(), 3);
        app.on_key(KeyEvent::new(KeyCode::Char('K'), KeyModifiers::SHIFT));
        app.on_key(KeyEvent::new(KeyCode::Char('x'), KeyModifiers::NONE));
        assert_eq!(app.project.shots.len(), 2);
        assert!(app.dirty);
    }

    #[test]
    fn keyboard_numeric_and_vector_editing_paths() {
        let (mut app, _) = app();
        app.open_editor(Field::Duration);
        if let Some(Modal::Editor(editor)) = app.modal.as_mut() {
            editor.input = "7.5".into();
        }
        app.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert_eq!(app.shot().duration_s, 7.5);

        app.open_editor(Field::AimOffset);
        if let Some(Modal::Editor(editor)) = app.modal.as_mut() {
            editor.input = "1 2 3".into();
        }
        app.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert_eq!(app.shot().aim.offset_m, Vec3(1.0, 2.0, 3.0));
    }

    #[test]
    fn mouse_selects_a_shot_and_opens_an_inspector_editor() {
        let (mut app, _) = app();
        app.project.shots.push(Shot::default());
        app.shot_rows = vec![(Rect::new(0, 1, 10, 1), 0), (Rect::new(0, 2, 10, 1), 1)];
        app.on_mouse(MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 2,
            row: 2,
            modifiers: KeyModifiers::NONE,
        });
        assert_eq!(app.selected, 1);
        app.inspector_rows = vec![(Rect::new(12, 1, 20, 1), 0)];
        app.on_mouse(MouseEvent {
            kind: MouseEventKind::Down(MouseButton::Left),
            column: 13,
            row: 1,
            modifiers: KeyModifiers::NONE,
        });
        assert!(matches!(app.modal, Some(Modal::Editor(_))));
    }

    #[test]
    fn playback_keys_send_worker_commands() {
        let (mut app, rx) = app();
        app.on_key(KeyEvent::new(KeyCode::Char(' '), KeyModifiers::NONE));
        assert!(matches!(rx.recv().unwrap(), Request::Pause(true)));
        app.on_key(KeyEvent::new(KeyCode::Char(']'), KeyModifiers::NONE));
        assert!(matches!(rx.recv().unwrap(), Request::Rate(value) if value == 1.25));
    }
}
