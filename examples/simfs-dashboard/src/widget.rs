//! The dashboard widget model. A widget binds **one** `/sim` field path to a [`Kind`] — the
//! interaction/rendering archetype the [`crate::catalog`] derives from the path via first-party
//! knowledge. The kind is *not* persisted: it is recomputed from the path on load, so a saved
//! layout is just a list of `{title, path}` (plus global settings) and a hand-edited path always
//! gets a sensible widget. Live read-back values live separately in [`crate::app::App::values`].

/// The interaction + rendering archetype of a field. Read-only kinds just display the value;
/// writable kinds ([`Kind::is_writable`]) render an interactive control that actuates by writing
/// the field (the `/sim` control files actuate on the newline — see `CommandFile`).
#[derive(Clone, Debug, PartialEq)]
pub enum Kind {
    /// Writable 0..1 throttle: a clickable bar; `-`/`=` nudge.
    Throttle,
    /// Writable 0..1 fraction (min-throttle, animation/solar deploy goal): clickable bar.
    Fraction,
    /// Writable 0/1 flag (lights, RCS, engine/rcs/light active): a clickable ON/off toggle.
    Toggle,
    /// Writable one-shot trigger (ignite, stage, decoupler fire, debug refills): a clickable button
    /// that writes `value` (conventionally `"1"`).
    Trigger {
        verb: &'static str,
        value: &'static str,
    },
    /// Writable unbounded number (light brightness, debug warp): `[-]`/`[+]` step buttons.
    NumberCtl { step: f64, unit: &'static str },
    /// Writable symbolic token (attitude mode/frame): activating opens a picker.
    Enum { options: &'static [&'static str] },
    /// Read-only scalar shown as a value + bar when it has a `max` (battery/tank fraction, etc.).
    Gauge {
        unit: &'static str,
        max: Option<f64>,
    },
    /// Read-only scalar number, formatted by `unit` (m, m/s, kg, N, W, s, °, …).
    Number { unit: &'static str },
    /// Read-only 0/1 flag shown as ON/off (controlled, propellant, docked, …).
    Flag,
    /// Read-only space-separated vector / quaternion (position, attitude, color, …).
    Vector,
    /// Read-only one-line JSON document (the per-vessel `telemetry` doc).
    Json,
    /// Read-only free text (id, name, situation, state, …) — also the fallback for unknown paths.
    Text,
}

impl Kind {
    /// Whether the widget renders an interactive control that writes the field.
    pub fn is_writable(&self) -> bool {
        matches!(
            self,
            Kind::Throttle
                | Kind::Fraction
                | Kind::Toggle
                | Kind::Trigger { .. }
                | Kind::NumberCtl { .. }
                | Kind::Enum { .. }
        )
    }

    /// A short ASCII tag shown in the search list / card corner to hint the widget archetype.
    pub fn tag(&self) -> &'static str {
        match self {
            Kind::Throttle => "thr",
            Kind::Fraction => "frac",
            Kind::Toggle => "tgl",
            Kind::Trigger { .. } => "btn",
            Kind::NumberCtl { .. } => "num±",
            Kind::Enum { .. } => "enum",
            Kind::Gauge { .. } => "gauge",
            Kind::Number { .. } => "num",
            Kind::Flag => "flag",
            Kind::Vector => "vec",
            Kind::Json => "json",
            Kind::Text => "text",
        }
    }
}

/// One placed dashboard widget: a user-facing `title`, the concrete (id-expanded) `/sim` `path`,
/// and the derived `kind`. Cards flow left-to-right in the dashboard grid in this list order.
#[derive(Clone, Debug)]
pub struct Widget {
    pub title: String,
    pub path: String,
    pub kind: Kind,
}

impl Widget {
    /// Builds a widget for `path`, deriving its kind + a default title from first-party knowledge.
    pub fn from_path(path: impl Into<String>) -> Self {
        let path = path.into();
        let spec = crate::catalog::classify(&path);
        Self {
            title: spec.title,
            path,
            kind: spec.kind,
        }
    }

    /// Builds a widget for `path` with an explicit (user-chosen) title; kind is still derived.
    pub fn titled(path: impl Into<String>, title: impl Into<String>) -> Self {
        let mut w = Self::from_path(path);
        w.title = title.into();
        w
    }
}

/// Parses the leading float of a `/sim` scalar (G9 doubles). `"1"`, `"0.42"`, `"-0"` all parse;
/// vectors return their first component, which is enough for the bar/number renderers that call it.
pub fn parse_scalar(value: &str) -> Option<f64> {
    value.split_whitespace().next()?.parse::<f64>().ok()
}

/// Reads a `/sim` flag value (`"1"` true, anything else false).
pub fn parse_flag(value: &str) -> bool {
    value.trim() == "1"
}
