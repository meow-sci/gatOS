//! Versioned, human-readable project model. This is deliberately smaller than the native track
//! schema: it describes ordered preset shots, not arbitrary keyframes.

use std::fmt;

use serde::{de, Deserialize, Deserializer, Serialize, Serializer};

pub const PROJECT_VERSION: u32 = 1;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Project {
    pub version: u32,
    pub name: String,
    #[serde(default)]
    pub r#loop: bool,
    pub shots: Vec<Shot>,
}

impl Project {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            version: PROJECT_VERSION,
            name: name.into(),
            r#loop: false,
            shots: vec![Shot::default()],
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Shot {
    pub name: String,
    pub duration_s: f64,
    #[serde(default)]
    pub blend_in_s: f64,
    pub anchor: SubjectRef,
    pub aim: Aim,
    pub frame: CameraFrame,
    pub kind: ShotKind,
    #[serde(default)]
    pub lens: Lens,
    #[serde(default)]
    pub smoothing_s: f64,
    #[serde(default)]
    pub ease: Ease,
}

impl Default for Shot {
    fn default() -> Self {
        Self {
            name: "shot-1".into(),
            duration_s: 5.0,
            blend_in_s: 0.35,
            anchor: SubjectRef::None,
            aim: Aim::default(),
            frame: CameraFrame::Ecl,
            // A fresh project safely retains the player's live placement and only claims FOV.
            // Picking a live target and an orbit/dolly preset turns it into a spatial shot.
            kind: ShotKind::LensOnly,
            lens: Lens::default(),
            smoothing_s: 0.25,
            ease: Ease::default(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum ShotKind {
    Orbit {
        radius_m: Range<f64>,
        azimuth_deg: Range<f64>,
        elevation_deg: Range<f64>,
    },
    Dolly {
        from_m: Vec3,
        to_m: Vec3,
    },
    Chase {
        offset_m: Vec3,
    },
    Static {
        position_m: Vec3,
    },
    LensOnly,
}

impl ShotKind {
    pub fn label(&self) -> &'static str {
        match self {
            Self::Orbit { .. } => "orbit",
            Self::Dolly { .. } => "dolly",
            Self::Chase { .. } => "chase/attach",
            Self::Static { .. } => "static",
            Self::LensOnly => "lens-only",
        }
    }

    pub fn preset(name: &str) -> Self {
        match name {
            "dolly" => Self::Dolly {
                from_m: Vec3(-40.0, 0.0, -6.0),
                to_m: Vec3(-12.0, 0.0, -3.0),
            },
            "chase/attach" | "chase" => Self::Chase {
                offset_m: Vec3(-18.0, 0.0, -4.0),
            },
            "static" => Self::Static {
                position_m: Vec3(-30.0, 12.0, -8.0),
            },
            "lens-only" => Self::LensOnly,
            _ => Self::Orbit {
                radius_m: Range::Constant(40.0),
                azimuth_deg: Range::Animated {
                    start: -35.0,
                    end: 35.0,
                },
                elevation_deg: Range::Constant(-12.0),
            },
        }
    }
}

/// A channel that either holds one value or animates between two endpoints.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Range<T> {
    Constant(T),
    Animated { start: T, end: T },
}

impl<T> Range<T> {
    pub fn start(&self) -> &T {
        match self {
            Self::Constant(value) => value,
            Self::Animated { start, .. } => start,
        }
    }

    pub fn end(&self) -> &T {
        match self {
            Self::Constant(value) => value,
            Self::Animated { end, .. } => end,
        }
    }

    pub fn is_animated(&self) -> bool {
        matches!(self, Self::Animated { .. })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Vec3(pub f64, pub f64, pub f64);

impl Vec3 {
    pub const ZERO: Self = Self(0.0, 0.0, 0.0);

    pub fn values(self) -> [f64; 3] {
        [self.0, self.1, self.2]
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SubjectRef {
    Vessel(String),
    Body(String),
    None,
}

impl SubjectRef {
    pub fn wire(&self) -> String {
        match self {
            Self::Vessel(id) => format!("vessel:{id}"),
            Self::Body(id) => format!("body:{id}"),
            Self::None => "none".into(),
        }
    }

    pub fn id(&self) -> Option<&str> {
        match self {
            Self::Vessel(id) | Self::Body(id) => Some(id),
            Self::None => None,
        }
    }

    pub fn parse(value: &str) -> Result<Self, String> {
        if value.eq_ignore_ascii_case("none") {
            return Ok(Self::None);
        }
        let (kind, id) = value
            .split_once(':')
            .ok_or_else(|| "expected vessel:<id>, body:<id>, or none".to_string())?;
        if !valid_id(id) {
            return Err("target id must be 1..64 characters of [A-Za-z0-9._-]".into());
        }
        if kind.eq_ignore_ascii_case("vessel") {
            Ok(Self::Vessel(id.into()))
        } else if kind.eq_ignore_ascii_case("body") {
            Ok(Self::Body(id.into()))
        } else {
            Err("v1 accepts vessel:<id>, body:<id>, or none (not part:)".into())
        }
    }
}

impl fmt::Display for SubjectRef {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.wire())
    }
}

impl Serialize for SubjectRef {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_str(&self.wire())
    }
}

impl<'de> Deserialize<'de> for SubjectRef {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let value = String::deserialize(deserializer)?;
        Self::parse(&value).map_err(de::Error::custom)
    }
}

pub fn valid_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 64
        && id != "."
        && id != ".."
        && id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'_' | b'-'))
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Aim {
    pub target: SubjectRef,
    #[serde(default)]
    pub offset_m: Vec3,
    #[serde(default)]
    pub frame: CameraFrame,
    #[serde(default)]
    pub up: AimUp,
}

impl Default for Aim {
    fn default() -> Self {
        Self {
            target: SubjectRef::None,
            offset_m: Vec3::ZERO,
            frame: CameraFrame::Bodyfixed,
            up: AimUp::World,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Lens {
    #[serde(default = "default_fov")]
    pub fov_deg: Option<Range<f64>>,
    #[serde(default)]
    pub roll_deg: Option<Range<f64>>,
    #[serde(default)]
    pub projection: Projection,
}

fn default_fov() -> Option<Range<f64>> {
    Some(Range::Constant(45.0))
}

impl Default for Lens {
    fn default() -> Self {
        Self {
            fov_deg: default_fov(),
            roll_deg: None,
            projection: Projection::Perspective,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum Projection {
    #[default]
    Perspective,
    Orthographic {
        half_height_m: f64,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum CameraFrame {
    Ecl,
    Cce,
    #[default]
    Bodyfixed,
    Enu,
    Lvlh,
    Chase,
}

impl CameraFrame {
    pub const ALL: [Self; 6] = [
        Self::Ecl,
        Self::Cce,
        Self::Bodyfixed,
        Self::Enu,
        Self::Lvlh,
        Self::Chase,
    ];

    pub fn token(self) -> &'static str {
        match self {
            Self::Ecl => "ecl",
            Self::Cce => "cce",
            Self::Bodyfixed => "bodyfixed",
            Self::Enu => "enu",
            Self::Lvlh => "lvlh",
            Self::Chase => "chase",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "kebab-case")]
pub enum AimUp {
    #[default]
    World,
    Target,
    Velocity,
    Free,
}

impl AimUp {
    pub const ALL: [Self; 4] = [Self::World, Self::Target, Self::Velocity, Self::Free];

    pub fn token(self) -> &'static str {
        match self {
            Self::World => "world",
            Self::Target => "target",
            Self::Velocity => "velocity",
            Self::Free => "free",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Ease {
    pub kind: EaseKind,
    pub power: f64,
}

impl Default for Ease {
    fn default() -> Self {
        Self {
            kind: EaseKind::InOut,
            power: 3.0,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "kebab-case")]
pub enum EaseKind {
    Linear,
    In,
    Out,
    #[default]
    InOut,
}

impl EaseKind {
    pub const ALL: [Self; 4] = [Self::Linear, Self::In, Self::Out, Self::InOut];

    pub fn token(self) -> &'static str {
        match self {
            Self::Linear => "linear",
            Self::In => "in",
            Self::Out => "out",
            Self::InOut => "in-out",
        }
    }
}

impl Default for Vec3 {
    fn default() -> Self {
        Self::ZERO
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn subject_refs_round_trip_as_wire_strings() {
        for subject in [
            SubjectRef::Vessel("Hunter".into()),
            SubjectRef::Body("Kerth".into()),
            SubjectRef::None,
        ] {
            let json = serde_json::to_string(&subject).unwrap();
            assert_eq!(serde_json::from_str::<SubjectRef>(&json).unwrap(), subject);
        }
    }

    #[test]
    fn part_targets_are_outside_v1() {
        assert!(SubjectRef::parse("part:Hunter/12").is_err());
        assert!(SubjectRef::parse("vessel:bad id").is_err());
    }
}
