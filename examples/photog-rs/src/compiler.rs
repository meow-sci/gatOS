//! Pure project validation and compilation into gatOS's native camera track plus a discrete-cue
//! `timed_batch`. Motion stays in the render-rate track; only channels absent from the track schema
//! (smoothing/projection/release) are emitted as sidecar cues.

use std::fmt;

use serde_json::{json, Map, Value};

use crate::model::{
    valid_id, CameraFrame, Ease, EaseKind, Project, Projection, Range, Shot, ShotKind, SubjectRef,
    Vec3, PROJECT_VERSION,
};

#[derive(Debug, Clone, PartialEq)]
pub struct CompiledTake {
    pub track_name: String,
    pub track_json: String,
    pub cue_id: String,
    pub group_id: String,
    pub timed_batch: String,
    pub duration_s: f64,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CompileRequest {
    pub track_name: String,
    pub cue_id: String,
    pub group_id: String,
    pub start_shot: usize,
}

impl CompileRequest {
    pub fn new(base: &str) -> Self {
        let base = sanitize_name(base);
        Self {
            track_name: format!("photog-{base}"),
            cue_id: format!("photog-{base}-cues"),
            group_id: format!("photog-{base}-take"),
            start_shot: 0,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ValidationError {
    pub field: String,
    pub message: String,
}

impl ValidationError {
    fn new(field: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            message: message.into(),
        }
    }
}

impl fmt::Display for ValidationError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.field, self.message)
    }
}

impl std::error::Error for ValidationError {}

pub fn validate_project(project: &Project) -> Vec<ValidationError> {
    let mut errors = Vec::new();
    if project.version != PROJECT_VERSION {
        errors.push(ValidationError::new(
            "version",
            format!("expected schema version {PROJECT_VERSION}"),
        ));
    }
    if project.name.trim().is_empty() {
        errors.push(ValidationError::new("name", "must not be empty"));
    }
    if project.shots.is_empty() {
        errors.push(ValidationError::new(
            "shots",
            "must contain at least one shot",
        ));
    }
    if project.shots.len() > 256 {
        errors.push(ValidationError::new(
            "shots",
            "cannot exceed the native 256-shot limit",
        ));
    }
    for (index, shot) in project.shots.iter().enumerate() {
        validate_shot(index, shot, &mut errors);
    }
    errors
}

fn validate_shot(index: usize, shot: &Shot, errors: &mut Vec<ValidationError>) {
    let root = format!("shots[{index}]");
    if shot.name.trim().is_empty() {
        errors.push(ValidationError::new(
            format!("{root}.name"),
            "must not be empty",
        ));
    }
    finite_range(
        &format!("{root}.duration_s"),
        shot.duration_s,
        0.0,
        f64::INFINITY,
        false,
        errors,
    );
    finite_range(
        &format!("{root}.blend_in_s"),
        shot.blend_in_s,
        0.0,
        f64::INFINITY,
        true,
        errors,
    );
    finite_range(
        &format!("{root}.smoothing_s"),
        shot.smoothing_s,
        0.0,
        10.0,
        true,
        errors,
    );
    finite_range(
        &format!("{root}.ease.power"),
        shot.ease.power,
        0.01,
        16.0,
        true,
        errors,
    );
    validate_subject(&format!("{root}.anchor"), &shot.anchor, errors);
    validate_subject(&format!("{root}.aim.target"), &shot.aim.target, errors);
    validate_vec(&format!("{root}.aim.offset_m"), shot.aim.offset_m, errors);

    let placement_needs_anchor =
        !matches!(shot.kind, ShotKind::LensOnly) && shot.frame != CameraFrame::Ecl;
    if placement_needs_anchor && shot.anchor == SubjectRef::None {
        errors.push(ValidationError::new(
            format!("{root}.anchor"),
            "this placement frame requires a vessel or body anchor",
        ));
    }
    if shot.frame == CameraFrame::Chase && matches!(shot.anchor, SubjectRef::Body(_)) {
        errors.push(ValidationError::new(
            format!("{root}.frame"),
            "chase is only resolvable about a vessel",
        ));
    }

    match &shot.kind {
        ShotKind::Orbit {
            radius_m,
            azimuth_deg,
            elevation_deg,
        } => {
            validate_scalar_range(
                &format!("{root}.kind.radius_m"),
                radius_m,
                errors,
                |value| value > 0.0,
                "must be finite and > 0 so orbit placement wins",
            );
            validate_scalar_range(
                &format!("{root}.kind.azimuth_deg"),
                azimuth_deg,
                errors,
                |_| true,
                "must be finite",
            );
            validate_scalar_range(
                &format!("{root}.kind.elevation_deg"),
                elevation_deg,
                errors,
                |value| (-90.0..=90.0).contains(&value),
                "must be in [-90, 90] degrees",
            );
        }
        ShotKind::Dolly { from_m, to_m } => {
            validate_vec(&format!("{root}.kind.from_m"), *from_m, errors);
            validate_vec(&format!("{root}.kind.to_m"), *to_m, errors);
        }
        ShotKind::Chase { offset_m } => {
            validate_vec(&format!("{root}.kind.offset_m"), *offset_m, errors);
            if !matches!(shot.anchor, SubjectRef::Vessel(_)) {
                errors.push(ValidationError::new(
                    format!("{root}.anchor"),
                    "a chase/attach shot requires a vessel anchor",
                ));
            }
        }
        ShotKind::Static { position_m } => {
            validate_vec(&format!("{root}.kind.position_m"), *position_m, errors);
        }
        ShotKind::LensOnly => {
            if shot.lens.fov_deg.is_none() && shot.lens.roll_deg.is_none() {
                errors.push(ValidationError::new(
                    format!("{root}.lens"),
                    "a lens-only shot must animate or hold FOV or roll",
                ));
            }
        }
    }

    if let Some(fov) = &shot.lens.fov_deg {
        validate_scalar_range(
            &format!("{root}.lens.fov_deg"),
            fov,
            errors,
            |value| (1.0..=179.0).contains(&value),
            "must be in the default camera range [1, 179] degrees",
        );
    }
    if let Some(roll) = &shot.lens.roll_deg {
        validate_scalar_range(
            &format!("{root}.lens.roll_deg"),
            roll,
            errors,
            |_| true,
            "must be finite",
        );
    }
    if let Projection::Orthographic { half_height_m } = shot.lens.projection {
        finite_range(
            &format!("{root}.lens.projection.half_height_m"),
            half_height_m,
            0.0,
            f64::INFINITY,
            false,
            errors,
        );
    }
}

fn validate_subject(field: &str, subject: &SubjectRef, errors: &mut Vec<ValidationError>) {
    if let Some(id) = subject.id() {
        if !valid_id(id) {
            errors.push(ValidationError::new(
                field,
                "target id must be 1..64 characters of [A-Za-z0-9._-]",
            ));
        }
    }
}

fn validate_vec(field: &str, value: Vec3, errors: &mut Vec<ValidationError>) {
    if value
        .values()
        .iter()
        .any(|component| !component.is_finite())
    {
        errors.push(ValidationError::new(field, "all components must be finite"));
    }
}

fn validate_scalar_range(
    field: &str,
    range: &Range<f64>,
    errors: &mut Vec<ValidationError>,
    predicate: impl Fn(f64) -> bool,
    expectation: &str,
) {
    for (suffix, value) in [("start", *range.start()), ("end", *range.end())] {
        if !value.is_finite() || !predicate(value) {
            errors.push(ValidationError::new(
                format!("{field}.{suffix}"),
                expectation,
            ));
        }
    }
}

fn finite_range(
    field: &str,
    value: f64,
    min: f64,
    max: f64,
    inclusive_min: bool,
    errors: &mut Vec<ValidationError>,
) {
    let valid_min = if inclusive_min {
        value >= min
    } else {
        value > min
    };
    if !value.is_finite() || !valid_min || value > max {
        let bracket = if inclusive_min { "[" } else { "(" };
        errors.push(ValidationError::new(
            field,
            format!("must be finite and in {bracket}{min}, {max}]"),
        ));
    }
}

pub fn compile_project(
    project: &Project,
    request: &CompileRequest,
) -> Result<CompiledTake, Vec<ValidationError>> {
    let mut errors = validate_project(project);
    for (field, value) in [
        ("track_name", request.track_name.as_str()),
        ("cue_id", request.cue_id.as_str()),
        ("group_id", request.group_id.as_str()),
    ] {
        if !valid_id(value) {
            errors.push(ValidationError::new(
                field,
                "must be 1..64 characters of [A-Za-z0-9._-]",
            ));
        }
    }
    if request.track_name == "camera" || request.cue_id == "camera" {
        errors.push(ValidationError::new(
            "identifiers",
            "'camera' is reserved for the native track player",
        ));
    }
    if request.start_shot >= project.shots.len() {
        errors.push(ValidationError::new(
            "start_shot",
            "must name an existing shot",
        ));
    }
    if !errors.is_empty() {
        return Err(errors);
    }

    let shots = &project.shots[request.start_shot..];
    let mut absolute_s = 0.0;
    let mut compiled_shots = Vec::with_capacity(shots.len());
    let mut cue_lines = vec![
        format!("# {} — generated by photog-rs", project.name),
        format!("@id {}", request.cue_id),
        "@clock render".into(),
        "@rate 0".into(),
        format!("@loop {}", u8::from(project.r#loop)),
        format!("@group {}", request.group_id),
        String::new(),
    ];
    let mut warnings = Vec::new();

    for shot in shots {
        compiled_shots.push(compile_shot(shot, absolute_s));
        let at_ms = absolute_s * 1000.0;
        cue_lines.push(format!(
            "{} camera/pose/smoothing {}",
            num(at_ms),
            num(shot.smoothing_s)
        ));
        match shot.lens.projection {
            Projection::Perspective => {
                cue_lines.push(format!("{} camera/pose/ortho 0", num(at_ms)));
            }
            Projection::Orthographic { half_height_m } => {
                cue_lines.push(format!(
                    "{} camera/pose/ortho_height {}",
                    num(at_ms),
                    num(half_height_m)
                ));
                cue_lines.push(format!("{} camera/pose/ortho 1", num(at_ms)));
                warnings.push(format!(
                    "shot '{}': orthographic half-height cannot be restored by KSA on release",
                    shot.name
                ));
            }
        }
        absolute_s += shot.duration_s;
    }

    if !project.r#loop {
        let end_ms = num(absolute_s * 1000.0);
        cue_lines.push(format!("{end_ms} camera/stop 1"));
        cue_lines.push(format!("{end_ms} camera/enabled 0"));
    }
    cue_lines.push("commit".into());

    let track = json!({
        "loop": project.r#loop,
        "shots": compiled_shots,
    });
    let mut track_json =
        serde_json::to_string_pretty(&track).expect("JSON values always serialize");
    track_json.push('\n');
    let mut timed_batch = cue_lines.join("\n");
    timed_batch.push('\n');

    Ok(CompiledTake {
        track_name: request.track_name.clone(),
        track_json,
        cue_id: request.cue_id.clone(),
        group_id: request.group_id.clone(),
        timed_batch,
        duration_s: absolute_s,
        warnings,
    })
}

fn compile_shot(shot: &Shot, absolute_s: f64) -> Value {
    let mut out = Map::new();
    out.insert("name".into(), json!(shot.name));
    out.insert("t".into(), json!(absolute_s));
    out.insert("duration".into(), json!(shot.duration_s));
    out.insert("blend_in".into(), json!(shot.blend_in_s));
    if shot.anchor != SubjectRef::None {
        out.insert("anchor".into(), json!(shot.anchor.wire()));
    }

    match &shot.kind {
        ShotKind::Orbit {
            radius_m,
            azimuth_deg,
            elevation_deg,
        } => {
            out.insert(
                "position".into(),
                json!({
                    "mode": "orbit",
                    "frame": shot.frame.token(),
                    "radius": scalar_channel(radius_m, shot.duration_s, shot.ease),
                    "azimuth": scalar_channel(azimuth_deg, shot.duration_s, shot.ease),
                    "elevation": scalar_channel(elevation_deg, shot.duration_s, shot.ease),
                }),
            );
        }
        ShotKind::Dolly { from_m, to_m } => {
            out.insert(
                "position".into(),
                json!({
                    "mode": "cartesian",
                    "curve": "linear",
                    "frame": shot.frame.token(),
                    "keys": vector_keys(*from_m, *to_m, shot.duration_s, shot.ease),
                }),
            );
        }
        ShotKind::Chase { offset_m } => {
            out.insert(
                "position".into(),
                json!({
                    "mode": "attach",
                    "frame": shot.frame.token(),
                    "offset": offset_m.values(),
                }),
            );
        }
        ShotKind::Static { position_m } => {
            out.insert(
                "position".into(),
                json!({
                    "mode": "cartesian",
                    "frame": shot.frame.token(),
                    "keys": [{"t": 0.0, "v": position_m.values()}],
                }),
            );
        }
        ShotKind::LensOnly => {}
    }

    if shot.aim.target != SubjectRef::None {
        let mut aim = Map::new();
        aim.insert("target".into(), json!(shot.aim.target.wire()));
        aim.insert("offset".into(), json!(shot.aim.offset_m.values()));
        aim.insert("frame".into(), json!(shot.aim.frame.token()));
        aim.insert("up".into(), json!(shot.aim.up.token()));
        if let Some(roll) = &shot.lens.roll_deg {
            aim.insert(
                "roll".into(),
                scalar_channel(roll, shot.duration_s, shot.ease),
            );
        }
        out.insert("aim".into(), Value::Object(aim));
    } else if let Some(roll) = &shot.lens.roll_deg {
        out.insert(
            "roll".into(),
            scalar_channel(roll, shot.duration_s, shot.ease),
        );
    }
    if let Some(fov) = &shot.lens.fov_deg {
        out.insert(
            "fov".into(),
            scalar_channel(fov, shot.duration_s, shot.ease),
        );
    }
    Value::Object(out)
}

fn scalar_channel(range: &Range<f64>, duration_s: f64, ease: Ease) -> Value {
    match range {
        Range::Constant(value) => json!({"keys": [{"t": 0.0, "v": value}]}),
        Range::Animated { start, end } => json!({
            "keys": [start_key(0.0, json!(start), ease), {"t": duration_s, "v": end}],
        }),
    }
}

fn vector_keys(from: Vec3, to: Vec3, duration_s: f64, ease: Ease) -> Vec<Value> {
    vec![
        start_key(0.0, json!(from.values()), ease),
        json!({"t": duration_s, "v": to.values()}),
    ]
}

fn start_key(t: f64, value: Value, ease: Ease) -> Value {
    let mut key = Map::new();
    key.insert("t".into(), json!(t));
    key.insert("v".into(), value);
    key.insert("ease".into(), json!(ease.kind.token()));
    if ease.kind != EaseKind::Linear {
        key.insert("ease_power".into(), json!(ease.power));
    }
    Value::Object(key)
}

pub fn sanitize_name(value: &str) -> String {
    let mut out = String::with_capacity(value.len().min(40));
    for c in value.chars() {
        if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') {
            out.push(c);
        } else if !out.ends_with('-') {
            out.push('-');
        }
        if out.len() == 40 {
            break;
        }
    }
    let trimmed = out.trim_matches(['.', '-']).to_string();
    if trimmed.is_empty() {
        "take".into()
    } else {
        trimmed
    }
}

fn num(value: f64) -> String {
    let mut out = format!("{value:.6}");
    while out.ends_with('0') {
        out.pop();
    }
    if out.ends_with('.') {
        out.pop();
    }
    if out == "-0" {
        "0".into()
    } else {
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Aim, AimUp, Lens};

    fn shot(kind: ShotKind) -> Shot {
        Shot {
            name: "one".into(),
            duration_s: 4.0,
            blend_in_s: 0.25,
            anchor: SubjectRef::Vessel("Hunter".into()),
            aim: Aim {
                target: SubjectRef::Vessel("Hunter".into()),
                offset_m: Vec3(0.0, 0.0, -1.0),
                frame: CameraFrame::Bodyfixed,
                up: AimUp::World,
            },
            frame: CameraFrame::Bodyfixed,
            kind,
            lens: Lens::default(),
            smoothing_s: 0.2,
            ease: Ease::default(),
        }
    }

    fn compile(shots: Vec<Shot>, looping: bool, start_shot: usize) -> CompiledTake {
        let project = Project {
            version: PROJECT_VERSION,
            name: "test".into(),
            r#loop: looping,
            shots,
        };
        let mut request = CompileRequest::new("test");
        request.start_shot = start_shot;
        compile_project(&project, &request).unwrap()
    }

    #[test]
    fn compiles_all_five_shot_types_and_absolute_times() {
        let kinds = vec![
            ShotKind::Orbit {
                radius_m: Range::Constant(20.0),
                azimuth_deg: Range::Animated {
                    start: 0.0,
                    end: 90.0,
                },
                elevation_deg: Range::Constant(-10.0),
            },
            ShotKind::Dolly {
                from_m: Vec3(-8.0, 0.0, -2.0),
                to_m: Vec3(-3.0, 0.0, -1.0),
            },
            ShotKind::Chase {
                offset_m: Vec3(-10.0, 0.0, -2.0),
            },
            ShotKind::Static {
                position_m: Vec3(1.0, 2.0, 3.0),
            },
            ShotKind::LensOnly,
        ];
        let shots = kinds
            .into_iter()
            .enumerate()
            .map(|(index, kind)| {
                let mut value = shot(kind);
                value.name = format!("shot-{index}");
                value.duration_s = index as f64 + 1.0;
                value
            })
            .collect();
        let take = compile(shots, false, 0);
        let json: Value = serde_json::from_str(&take.track_json).unwrap();
        assert_eq!(json["shots"][0]["t"], 0.0);
        assert_eq!(json["shots"][1]["t"], 1.0);
        assert_eq!(json["shots"][4]["t"], 10.0);
        assert_eq!(json["shots"][0]["position"]["mode"], "orbit");
        assert_eq!(json["shots"][1]["position"]["mode"], "cartesian");
        assert_eq!(json["shots"][2]["position"]["mode"], "attach");
        assert_eq!(
            json["shots"][3]["position"]["keys"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
        assert!(json["shots"][4].get("position").is_none());
        assert!(take.timed_batch.contains("15000 camera/stop 1"));
        assert!(take.timed_batch.contains("15000 camera/enabled 0"));
    }

    #[test]
    fn play_from_selection_rebases_and_omits_prior_shots() {
        let mut first = shot(ShotKind::Static {
            position_m: Vec3(1.0, 0.0, 0.0),
        });
        first.name = "first".into();
        let mut second = shot(ShotKind::Static {
            position_m: Vec3(2.0, 0.0, 0.0),
        });
        second.name = "second".into();
        let take = compile(vec![first, second], false, 1);
        let json: Value = serde_json::from_str(&take.track_json).unwrap();
        assert_eq!(json["shots"].as_array().unwrap().len(), 1);
        assert_eq!(json["shots"][0]["name"], "second");
        assert_eq!(json["shots"][0]["t"], 0.0);
        assert_eq!(take.duration_s, 4.0);
    }

    #[test]
    fn looping_sidecar_has_no_automatic_release() {
        let take = compile(vec![shot(ShotKind::LensOnly)], true, 0);
        assert!(take.timed_batch.contains("@loop 1"));
        assert!(!take.timed_batch.contains("camera/stop"));
        assert!(!take.timed_batch.contains("camera/enabled 0"));
    }

    #[test]
    fn projection_and_smoothing_are_boundary_cues_not_track_channels() {
        let mut value = shot(ShotKind::Static {
            position_m: Vec3(1.0, 2.0, 3.0),
        });
        value.lens.projection = Projection::Orthographic {
            half_height_m: 12.0,
        };
        let take = compile(vec![value], false, 0);
        assert!(take.timed_batch.contains("0 camera/pose/smoothing 0.2"));
        assert!(take.timed_batch.contains("0 camera/pose/ortho_height 12"));
        assert!(take.timed_batch.contains("0 camera/pose/ortho 1"));
        assert!(!take.track_json.contains("ortho"));
        assert!(!take.track_json.contains("smoothing"));
        assert_eq!(take.warnings.len(), 1);
    }

    #[test]
    fn validation_catches_ranges_targets_and_placement_requirements() {
        let mut value = shot(ShotKind::Chase {
            offset_m: Vec3(f64::NAN, 0.0, 0.0),
        });
        value.anchor = SubjectRef::Body("bad id".into());
        value.duration_s = 0.0;
        value.smoothing_s = 11.0;
        value.ease.power = 20.0;
        value.lens.fov_deg = Some(Range::Constant(200.0));
        let project = Project {
            version: 1,
            name: "x".into(),
            r#loop: false,
            shots: vec![value],
        };
        let errors = validate_project(&project);
        assert!(errors.len() >= 7, "{errors:#?}");
    }
}
