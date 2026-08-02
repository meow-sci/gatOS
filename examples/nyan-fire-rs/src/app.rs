//! Application state + input handling. Two screens — a plume-template **multi-select** and the
//! **show** console — plus the same seven modals as `dancy-party-rs` (manual color entry, the XKCD
//! fuzzy picker, the stripe-time editor, the **settings** popup and its manual-entry box, save
//! profile, and the quit confirmation). The render pass ([`crate::ui`]) reads this state and writes
//! back the interactive hit-test rects (template rows, color-row buttons, the show/reset/settings
//! buttons, modal lists) that the mouse handler tests on the next event, so keyboard and mouse drive
//! the exact same actions.
//!
//! All `/sim` I/O lives on the worker thread ([`crate::source`]); this type only *sends* commands
//! (discover / watch / start / update / capture / reset / stop) and folds the worker's replies into
//! display state. Every display-affecting knob lives in [`Settings`] and is editable live from the
//! settings popup; a running show adopts changes immediately (the plan is republished without
//! resetting the stripe clock, so the rainbow doesn't jump).

use ratatui::crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::layout::{Position, Rect};
use tokio::sync::mpsc::UnboundedSender;

use crate::color::{self, Rgb};
use crate::party::{fmt_num, Plan, SLOTS};
use crate::profile::{self, Profile};
use crate::source::{FromWorker, PlumeTemplate, ToWorker};
use crate::xkcd::XKCD;

/// Which of the two top-level screens is showing.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Screen {
    Templates,
    Show,
}

/// Focus within the show screen — Tab cycles it; the active section interprets the arrow/letter keys.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Focus {
    Colors,
    Time,
    Button,
}

/// Every display-affecting knob, all live-editable from the settings popup. The stripe timing is
/// **decoupled** from the brightness timing: `color_ms` paces the palette's scroll along the four
/// gradient stops while `bright_ms` paces the `emission/brightness` drift, and each clock has its own
/// quantization (plus the per-slot / per-template staggers).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Settings {
    /// Animation frame rate (the worker's dispatch cadence), Hz, clamped 1..240.
    pub hz: f64,
    /// Stripe cross-fade quantization per segment (0 = continuous, 1 = hard cut / crisp bands).
    pub steps: u32,
    /// How long one palette entry occupies a gradient slot, ms.
    pub color_ms: u64,
    /// Per-slot stripe-clock stagger, ms (independent of the window offset that makes the rainbow).
    pub slot_stagger_ms: f64,
    /// How many palette entries apart adjacent gradient slots sit (0 = all four stops alike).
    pub slot_step: u32,
    /// Per-template stripe-clock stagger, ms.
    pub tpl_stagger_ms: f64,
    /// `emission/brightness` pulse floor, on the leaf's real `0..`[`BRIGHT_MAX`] scale. No division
    /// anywhere: the setting *is* the value written to the leaf.
    pub bright_min: f64,
    /// `emission/brightness` pulse ceiling. `<= 0` means the effect is **off** and the leaf is never
    /// written — the template keeps its authored brightness.
    pub bright_max: f64,
    /// Time each random brightness target holds before drifting to the next, ms.
    pub bright_ms: u64,
    /// Brightness-drift quantization steps (0 = continuous).
    pub bright_steps: u32,
    /// Dispatch a multi-leaf frame as ONE `/sim/ctl/batch` group (SPEC §3.10) so all four gradient
    /// stops move in the same game tick. Not a settings row — it is a transport choice, not a look —
    /// but it is saved in the profile and shown in the title bar. `--no-batch` turns it off.
    pub batch: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            hz: 30.0,
            steps: 0,
            color_ms: 1200,
            slot_stagger_ms: 0.0,
            slot_step: 1,
            tpl_stagger_ms: 0.0,
            // The brightness effect is off by default: max <= 0 means the leaf is never written.
            bright_min: 0.0,
            bright_max: 0.0,
            bright_ms: 600,
            bright_steps: 0,
            batch: true,
        }
    }
}

/// The number of editable rows in the settings popup (see [`Settings::adjust`]).
pub const SETTING_ROWS: usize = 10;

const MIN_MS: u64 = 50;
const MAX_MS: u64 = 60_000;
const MAX_STAGGER: f64 = 60_000.0;
/// The widest window the four gradient slots can span, in palette entries.
const MAX_SLOT_STEP: u32 = 8;
/// The inclusive ceiling of the `emission/brightness` leaf. Unlike dancy-party-rs's synthetic
/// `BRIGHT_SCALE`, this is the leaf's **real** range — the settings value goes to `/sim` as-is.
pub const BRIGHT_MAX: f64 = crate::party::BRIGHT_CEILING;

impl Settings {
    /// Nudges row `row` by `dir` (-1/+1), a coarse step when `big` (Shift), within each knob's range.
    /// Live-applied by the caller (republished to a running show).
    pub fn adjust(&mut self, row: usize, dir: i32, big: bool) {
        let d = dir as i64;
        match row {
            0 => {
                let step = if big { 25.0 } else { 5.0 };
                self.hz = (self.hz + dir as f64 * step).clamp(1.0, 240.0);
            }
            1 => {
                let step = if big { 10 } else { 1 };
                self.steps = (self.steps as i64 + d * step).clamp(0, 1000) as u32;
            }
            2 => {
                let step = if big { 1000 } else { 100 };
                self.color_ms =
                    (self.color_ms as i64 + d * step).clamp(MIN_MS as i64, MAX_MS as i64) as u64;
            }
            3 => {
                let step = if big { 100.0 } else { 10.0 };
                self.slot_stagger_ms = (self.slot_stagger_ms + dir as f64 * step).clamp(0.0, MAX_STAGGER);
            }
            4 => {
                self.slot_step =
                    (self.slot_step as i64 + d).clamp(0, MAX_SLOT_STEP as i64) as u32;
            }
            5 => {
                let step = if big { 100.0 } else { 10.0 };
                self.tpl_stagger_ms = (self.tpl_stagger_ms + dir as f64 * step).clamp(0.0, MAX_STAGGER);
            }
            6 => {
                let step = if big { 10.0 } else { 1.0 };
                self.bright_min = (self.bright_min + dir as f64 * step).clamp(0.0, BRIGHT_MAX);
            }
            7 => {
                let step = if big { 10.0 } else { 1.0 };
                self.bright_max = (self.bright_max + dir as f64 * step).clamp(0.0, BRIGHT_MAX);
            }
            8 => {
                let step = if big { 1000 } else { 100 };
                self.bright_ms =
                    (self.bright_ms as i64 + d * step).clamp(MIN_MS as i64, MAX_MS as i64) as u64;
            }
            9 => {
                let step = if big { 10 } else { 1 };
                self.bright_steps = (self.bright_steps as i64 + d * step).clamp(0, 1000) as u32;
            }
            _ => {}
        }
    }

    /// The label for a settings row.
    pub fn row_label(row: usize) -> &'static str {
        match row {
            0 => "frame rate",
            1 => "fade steps",
            2 => "stripe time",
            3 => "slot stagger",
            4 => "slot step",
            5 => "tpl stagger",
            6 => "bright min",
            7 => "bright max",
            8 => "bright time",
            9 => "bright steps",
            _ => "",
        }
    }

    /// The formatted current value for a settings row.
    pub fn row_value(&self, row: usize) -> String {
        match row {
            0 => format!("{} Hz", fmt_hz(self.hz)),
            1 => match self.steps {
                0 => "continuous".into(),
                1 => "1 step (hard cut)".into(),
                n => format!("{n} steps"),
            },
            2 => format!("{} ms", self.color_ms),
            3 => format!("{} ms", self.slot_stagger_ms as u64),
            4 => match self.slot_step {
                0 => "0 (all slots alike)".into(),
                1 => "1 (rainbow window)".into(),
                n => n.to_string(),
            },
            5 => format!("{} ms", self.tpl_stagger_ms as u64),
            6 => fmt_num(self.bright_min),
            // The single most important annotation in the popup: 0 doesn't mean "write 0", it means
            // the leaf is never touched at all.
            7 => {
                if self.bright_is_off() {
                    "off (leaf untouched)".into()
                } else {
                    fmt_num(self.bright_max)
                }
            }
            8 => {
                if self.bright_is_off() {
                    format!("{} ms (off)", self.bright_ms)
                } else if self.bright_is_pinned() {
                    format!("{} ms (pinned)", self.bright_ms)
                } else {
                    format!("{} ms", self.bright_ms)
                }
            }
            9 => {
                let base = if self.bright_steps == 0 {
                    "continuous".to_string()
                } else {
                    format!("{} steps", self.bright_steps)
                };
                if self.bright_is_off() {
                    format!("{base} (off)")
                } else {
                    base
                }
            }
            _ => String::new(),
        }
    }

    /// The current value of a row as a bare string — what the manual-input popup prefills (so the user
    /// edits from the live value rather than an empty field). Whole-number rows render whole; the two
    /// brightness rows render as a decimal so a typed value round-trips.
    pub fn row_input_value(&self, row: usize) -> String {
        match row {
            0 => format!("{}", self.hz.round() as u64),
            1 => self.steps.to_string(),
            2 => self.color_ms.to_string(),
            3 => format!("{}", self.slot_stagger_ms as u64),
            4 => self.slot_step.to_string(),
            5 => format!("{}", self.tpl_stagger_ms as u64),
            6 => fmt_num(self.bright_min),
            7 => fmt_num(self.bright_max),
            8 => self.bright_ms.to_string(),
            9 => self.bright_steps.to_string(),
            _ => String::new(),
        }
    }

    /// True when the `emission/brightness` effect is disabled — the ceiling is at (or below) zero, so
    /// the leaf is never written and each template keeps its authored brightness. Matches
    /// [`crate::party::Plan::brightness_at`]'s sentinel exactly.
    pub fn bright_is_off(&self) -> bool {
        self.bright_max <= 0.0
    }

    /// True when the brightness range has collapsed to a non-zero point — the leaf is pinned to that
    /// constant (one write, deduped forever after).
    pub fn bright_is_pinned(&self) -> bool {
        !self.bright_is_off() && (self.bright_min - self.bright_max).abs() < 1e-9
    }

    /// Whether a settings row holds a decimal rather than a whole number, so the manual-entry popup
    /// accepts a decimal point and parses it as a float. The two brightness rows are `0..200` on the
    /// leaf's own scale, and `42.5` is a perfectly good emissive brightness.
    pub fn row_is_fraction(row: usize) -> bool {
        matches!(row, 6 | 7)
    }

    /// Applies a manually-typed number `v` to row `row`, clamped to that row's valid range. The input
    /// is unconstrained (0 or higher); the clamp keeps the setting usable (e.g. frame rate can't be 0,
    /// durations have a floor). Whole-number rows round `v`; the brightness rows take the decimal
    /// verbatim, which is why a player can type `42.5`.
    pub fn set_from_input(&mut self, row: usize, v: f64) {
        let v = v.max(0.0);
        let int = v.round() as u64;
        match row {
            0 => self.hz = v.clamp(1.0, 240.0),
            1 => self.steps = int.min(1000) as u32,
            2 => self.color_ms = int.clamp(MIN_MS, MAX_MS),
            3 => self.slot_stagger_ms = v.clamp(0.0, MAX_STAGGER),
            4 => self.slot_step = int.min(MAX_SLOT_STEP as u64) as u32,
            5 => self.tpl_stagger_ms = v.clamp(0.0, MAX_STAGGER),
            6 => self.bright_min = v.clamp(0.0, BRIGHT_MAX),
            7 => self.bright_max = v.clamp(0.0, BRIGHT_MAX),
            8 => self.bright_ms = int.clamp(MIN_MS, MAX_MS),
            9 => self.bright_steps = int.min(1000) as u32,
            _ => {}
        }
    }
}

fn fmt_hz(hz: f64) -> String {
    if (hz - hz.round()).abs() < 1e-6 {
        format!("{}", hz.round() as i64)
    } else {
        format!("{hz:.1}")
    }
}

/// Friendly rendering of an `emission/brightness` value for the status line / live band: `off` when
/// the effect is disabled (`None` — the leaf is never written), else the trimmed 0..200 number.
pub fn fmt_bright_display(brightness: Option<f64>) -> String {
    match brightness {
        None => "off".to_string(),
        Some(v) => fmt_num(v),
    }
}

/// One template row on the select screen: its (sanitized) `/sim` id, how many of the four gradient
/// stops resolved, the brightness the game last reported, and whether it's armed for the show.
pub struct TemplateRow {
    pub id: String,
    pub stops: usize,
    pub brightness: Option<f64>,
    pub selected: bool,
}

pub enum Modal {
    None,
    AddColor(AddColorModal),
    Xkcd(XkcdModal),
    Time(TimeModal),
    Settings(SettingsModal),
    SettingInput(SettingInputModal),
    SaveProfile(SaveProfileModal),
    ConfirmQuit(ConfirmQuitModal),
}

/// Manual color entry — type an RGB triple (`255 128 0`) or hex (`#ff8000`); a live swatch previews
/// the parse. `Tab` jumps to the XKCD picker.
pub struct AddColorModal {
    pub text: String,
    pub area: Rect,
}

/// The XKCD fuzzy picker — a space-separated **AND** filter over the bundled survey palette with a
/// live preview swatch of the highlighted color.
pub struct XkcdModal {
    pub query: String,
    /// Lowercased humanized name per `XKCD` entry (parallel index), built once on open.
    pub hays: Vec<String>,
    pub filtered: Vec<usize>,
    pub selected: usize,
    pub offset: usize,
    pub area: Rect,
    pub item_rects: Vec<(Rect, usize)>,
}

/// Stripe-time editor — type a millisecond value (a quick path to the `color_ms` setting).
pub struct TimeModal {
    pub text: String,
    pub area: Rect,
}

/// The settings popup — a list of [`Settings`] rows; ←/→ adjust the selected one (Shift = coarse),
/// ↑/↓ move, Esc/`s` close. Records its hit-test rects for the mouse handler.
pub struct SettingsModal {
    pub sel: usize,
    pub area: Rect,
    pub rows: Vec<(Rect, usize)>,
}

/// Manual numeric entry for one settings row (opened with `Enter` from the settings popup) — type a
/// number, `Enter` applies it (clamped to the row's range), `Esc` returns to the settings list. Lets
/// you set a precise value the ←/→ stepping is too coarse or too slow to reach.
pub struct SettingInputModal {
    pub row: usize,
    pub text: String,
    pub area: Rect,
}

/// The save-profile prompt — type a name, `Enter` writes `<name>.yaml` (palette + all settings, but
/// not the armed templates) to the profiles directory; `Esc` cancels.
pub struct SaveProfileModal {
    pub text: String,
    pub area: Rect,
}

/// The quit confirmation — `y`/`Enter` quits, `n`/`Esc` cancels. Records the two button rects for the
/// mouse handler.
#[derive(Default)]
pub struct ConfirmQuitModal {
    pub area: Rect,
    pub quit_btn: Rect,
    pub cancel_btn: Rect,
}

/// The read-back row — what the **game** reports back for the first armed template. gatOS re-samples
/// the FX surface on a write or every 2 s otherwise, and the values are 32-bit floats game-side, so
/// an idle row can be up to 2 s behind and `0.6` reads back as `0.60000002`.
#[derive(Clone, Default)]
pub struct ReadbackView {
    pub id: Option<String>,
    pub colors: [Option<Rgb>; SLOTS],
    pub brightness: Option<f64>,
    pub armed: usize,
}

pub struct App {
    pub screen: Screen,
    pub modal: Modal,
    pub should_quit: bool,
    /// Set when we asked the worker to reset the templates on the way out, so `main` waits for the ack.
    pub pending_stop: bool,
    /// **Hide mode** — collapse the whole UI to a single status bar (show toggle + live stops + reset)
    /// so a running show doesn't block the game. Toggled with `h`; `h`/`Esc` restores the full UI.
    pub hidden: bool,

    // ---- templates screen ----
    pub templates: Vec<TemplateRow>,
    pub tsel: usize,
    pub discovering: bool,

    // ---- show screen ----
    pub colors: Vec<Rgb>,
    pub csel: usize,
    pub focus: Focus,
    pub burning: bool,
    /// The latest live frame from the worker (the four-quarter preview band), or `None` when stopped.
    pub live: Option<LiveFrame>,
    /// What the game reports back for the first armed template (updated whether or not a show runs).
    pub readback: ReadbackView,
    /// Writes dispatched since the current show started, and how many are still in flight.
    pub writes: u64,
    pub inflight: usize,

    // ---- shared ----
    pub connected: bool,
    pub control: bool,
    /// `[control] debug_namespace` — off means the whole `/sim/debug` subtree is **absent**, which is
    /// the single most likely reason nothing works, so it gets its own header badge.
    pub debug: bool,
    pub label: String,
    /// Every display-affecting knob; edited live from the settings popup, adopted by a running show.
    pub settings: Settings,
    pub status: String,
    pub status_err: bool,

    // ---- hit-test rects recorded each render ----
    pub template_rects: Vec<(Rect, usize)>,
    pub color_rects: Vec<(Rect, usize)>,
    pub up_btns: Vec<(Rect, usize)>,
    pub down_btns: Vec<(Rect, usize)>,
    pub del_btns: Vec<(Rect, usize)>,
    pub add_btn: Rect,
    pub xkcd_btn: Rect,
    pub time_minus: Rect,
    pub time_plus: Rect,
    pub reset_btn: Rect,
    pub settings_btn: Rect,
    pub burn_btn: Rect,
    pub back_btn: Rect,
    /// The "show" button on the hide-mode bar (restores the full UI).
    pub hide_show_btn: Rect,

    tx: UnboundedSender<ToWorker>,
}

/// A throttled live animation frame mirrored from the worker for the UI preview: the lead template's
/// four gradient stops (slot 0 = nozzle exit … slot 3 = plume tip) this instant.
#[derive(Clone, Copy)]
pub struct LiveFrame {
    pub slots: [Rgb; SLOTS],
    pub segment: u64,
    /// The brightness written this frame, or `None` when the effect is off.
    pub brightness: Option<f64>,
}

/// The stripe-time floor (ms) — fast enough to strobe, slow enough that the worker keeps up.
const MIN_PER_MS: u64 = MIN_MS;
const TIME_STEP: u64 = 100;

impl App {
    pub fn new(
        tx: UnboundedSender<ToWorker>,
        label: String,
        settings: Settings,
        colors: Vec<Rgb>,
    ) -> Self {
        let app = Self {
            screen: Screen::Templates,
            modal: Modal::None,
            should_quit: false,
            pending_stop: false,
            hidden: false,
            templates: Vec::new(),
            tsel: 0,
            discovering: true,
            colors,
            csel: 0,
            focus: Focus::Colors,
            burning: false,
            live: None,
            readback: ReadbackView::default(),
            writes: 0,
            inflight: 0,
            connected: false,
            control: false,
            debug: false,
            label,
            settings,
            status: "scanning for plume templates\u{2026}".into(),
            status_err: false,
            template_rects: Vec::new(),
            color_rects: Vec::new(),
            up_btns: Vec::new(),
            down_btns: Vec::new(),
            del_btns: Vec::new(),
            add_btn: Rect::default(),
            xkcd_btn: Rect::default(),
            time_minus: Rect::default(),
            time_plus: Rect::default(),
            reset_btn: Rect::default(),
            settings_btn: Rect::default(),
            burn_btn: Rect::default(),
            back_btn: Rect::default(),
            hide_show_btn: Rect::default(),
            tx,
        };
        let _ = app.tx.send(ToWorker::Discover);
        app
    }

    // ---- worker replies ----------------------------------------------------------------------

    pub fn apply(&mut self, msg: FromWorker) {
        match msg {
            FromWorker::Catalog { templates, health } => {
                self.discovering = false;
                self.connected = health.connected;
                self.control = health.control;
                self.debug = health.debug;
                self.merge_templates(templates);
                self.status = if self.templates.is_empty() {
                    if self.debug {
                        "no plume templates \u{2014} load a flight, then press r to rescan".into()
                    } else {
                        "no plume templates \u{2014} set [control] debug_namespace = true in gatos.toml"
                            .into()
                    }
                } else {
                    format!(
                        "{} template(s) found \u{2014} space to arm, c to capture, Enter for the show",
                        self.templates.len()
                    )
                };
                // An empty roster is only an *error* when the debug namespace explains it — otherwise
                // it just means no flight is loaded yet.
                self.status_err = self.templates.is_empty() && !self.debug;
            }
            FromWorker::Readback { id, values, armed } => {
                self.readback = ReadbackView {
                    id,
                    colors: values.colors,
                    brightness: values.brightness,
                    armed,
                };
            }
            FromWorker::Tick {
                slots,
                segment,
                brightness,
                targets,
                writes,
                inflight,
            } => {
                self.burning = true;
                self.live = Some(LiveFrame {
                    slots,
                    segment,
                    brightness,
                });
                self.writes = writes;
                self.inflight = inflight;
                let bri = fmt_bright_display(brightness);
                self.status = format!(
                    "\u{1f525} BURNING \u{b7} {targets} template(s) \u{b7} stripe {segment} \u{b7} bri {bri}"
                );
                self.status_err = false;
            }
            FromWorker::Stopped { error } => {
                self.burning = false;
                self.live = None;
                self.writes = 0;
                self.inflight = 0;
                self.pending_stop = false;
                match error {
                    Some(e) => {
                        self.status = format!("stopped, but the reset failed \u{2014} {e}");
                        self.status_err = true;
                    }
                    None => {
                        self.status = "stopped \u{2014} templates restored to pristine".into();
                        self.status_err = false;
                    }
                }
            }
            FromWorker::ResetDone { error } => match error {
                Some(e) => {
                    self.status = format!("reset failed \u{2014} {e}");
                    self.status_err = true;
                }
                None => {
                    self.status = "templates reset to pristine \u{27f2}".into();
                    self.status_err = false;
                }
            },
            FromWorker::Captured { id, colors } => {
                if colors.is_empty() {
                    self.status = format!("nothing to capture from {id} \u{2014} no readable stops");
                    self.status_err = true;
                } else {
                    let n = colors.len();
                    for c in colors {
                        // Skip an exact repeat of the tail so a flat gradient doesn't add four
                        // identical entries.
                        if self.colors.last() != Some(&c) {
                            self.colors.push(c);
                        }
                    }
                    self.csel = self.colors.len().saturating_sub(1);
                    self.republish_plan();
                    self.status = format!("captured {n} stop(s) from {id}");
                    self.status_err = false;
                }
            }
            FromWorker::Refused(why) => {
                self.burning = false;
                self.live = None;
                self.status = format!("can't burn \u{2014} {why}");
                self.status_err = true;
            }
        }
    }

    /// Folds a fresh catalog into the template rows, preserving each template's armed state by id.
    fn merge_templates(&mut self, cat: Vec<PlumeTemplate>) {
        let armed: Vec<String> = self
            .templates
            .iter()
            .filter(|t| t.selected)
            .map(|t| t.id.clone())
            .collect();
        self.templates = cat
            .into_iter()
            .map(|t| TemplateRow {
                selected: armed.contains(&t.id),
                stops: t.stops(),
                brightness: t.current.and_then(|v| v.brightness),
                id: t.id,
            })
            .collect();
        if self.tsel >= self.templates.len() {
            self.tsel = self.templates.len().saturating_sub(1);
        }
    }

    pub fn selected_template_ids(&self) -> Vec<String> {
        self.templates
            .iter()
            .filter(|t| t.selected)
            .map(|t| t.id.clone())
            .collect()
    }

    fn plan(&self) -> Plan {
        // Note: no division anywhere on the brightness values (unlike dancy-party-rs's BRIGHT_SCALE) —
        // the setting IS the leaf value, on the leaf's real 0..200 scale.
        Plan::new(self.colors.clone(), self.settings.color_ms)
            .with_steps(self.settings.steps)
            .with_slots(self.settings.slot_stagger_ms, self.settings.slot_step)
            .with_tpl_stagger(self.settings.tpl_stagger_ms)
            .with_brightness(
                self.settings.bright_min,
                self.settings.bright_max,
                self.settings.bright_ms,
                self.settings.bright_steps,
            )
    }

    // ---- keyboard ----------------------------------------------------------------------------

    pub fn on_key(&mut self, key: KeyEvent) {
        match &mut self.modal {
            Modal::AddColor(_) => self.on_key_add(key),
            Modal::Xkcd(_) => self.on_key_xkcd(key),
            Modal::Time(_) => self.on_key_time(key),
            Modal::Settings(_) => self.on_key_settings(key),
            Modal::SettingInput(_) => self.on_key_setting_input(key),
            Modal::SaveProfile(_) => self.on_key_save_profile(key),
            Modal::ConfirmQuit(_) => self.on_key_confirm_quit(key),
            Modal::None if self.hidden => self.on_key_hidden(key),
            Modal::None => match self.screen {
                Screen::Templates => self.on_key_templates(key),
                Screen::Show => self.on_key_show(key),
            },
        }
    }

    /// Hide-mode keys: the screen is one status bar, so only the show toggle / reset / restore (and
    /// quit) are live.
    fn on_key_hidden(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc | KeyCode::Char('h') => self.hidden = false,
            KeyCode::Enter | KeyCode::Char('p') | KeyCode::Char('P') => self.toggle_show(),
            KeyCode::Char('g') => self.reset_armed(),
            KeyCode::Char('q') => self.request_quit(),
            _ => {}
        }
    }

    /// `q` (and Esc on the templates screen) asks before leaving — see [`Self::on_key_confirm_quit`].
    fn on_key_confirm_quit(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Char('y') | KeyCode::Char('Y') | KeyCode::Enter => {
                self.modal = Modal::None;
                self.quit();
            }
            KeyCode::Esc | KeyCode::Char('n') | KeyCode::Char('N') => self.modal = Modal::None,
            _ => {}
        }
    }

    fn on_key_templates(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Char('q') | KeyCode::Esc => self.request_quit(),
            KeyCode::Up | KeyCode::Char('k') => self.move_tsel(-1),
            KeyCode::Down | KeyCode::Char('j') => self.move_tsel(1),
            KeyCode::Char(' ') => self.toggle_template(self.tsel),
            KeyCode::Char('a') => self.toggle_all_templates(),
            KeyCode::Char('r') => self.rescan(),
            KeyCode::Char('c') => self.capture_palette(),
            KeyCode::Enter | KeyCode::Char('p') => self.go_show(),
            _ => {}
        }
    }

    fn on_key_show(&mut self, key: KeyEvent) {
        // Screen-wide actions from anywhere on the show screen.
        match key.code {
            KeyCode::Enter | KeyCode::Char('P') => {
                self.toggle_show();
                return;
            }
            KeyCode::Char('q') => {
                self.request_quit();
                return;
            }
            KeyCode::Esc | KeyCode::Char('b') => {
                self.back_to_templates();
                return;
            }
            KeyCode::Char('s') => {
                self.open_settings();
                return;
            }
            KeyCode::Char('g') => {
                self.reset_armed();
                return;
            }
            KeyCode::Char('w') => {
                self.open_save_profile();
                return;
            }
            KeyCode::Char('h') => {
                self.hidden = true;
                return;
            }
            KeyCode::Tab => {
                self.cycle_focus(1);
                return;
            }
            KeyCode::BackTab => {
                self.cycle_focus(-1);
                return;
            }
            _ => {}
        }
        match self.focus {
            Focus::Colors => match key.code {
                KeyCode::Up | KeyCode::Char('k') => self.move_csel(-1),
                KeyCode::Down | KeyCode::Char('j') => self.move_csel(1),
                KeyCode::Char('[') => self.move_color(-1),
                KeyCode::Char(']') => self.move_color(1),
                KeyCode::Char('a') => self.open_add_color(),
                KeyCode::Char('f') | KeyCode::Char('x') => self.open_xkcd(),
                KeyCode::Char('d') | KeyCode::Delete | KeyCode::Backspace => self.remove_color(),
                _ => {}
            },
            Focus::Time => match key.code {
                KeyCode::Left | KeyCode::Char('-') | KeyCode::Char('_') => self.nudge_time(-1),
                KeyCode::Right | KeyCode::Char('=') | KeyCode::Char('+') => self.nudge_time(1),
                KeyCode::Char('e') => self.open_time(),
                _ => {}
            },
            Focus::Button => {}
        }
        // Shift+Up / Shift+Down reorder the focused color regardless of section, a common reflex.
        if key.modifiers.contains(KeyModifiers::SHIFT) {
            match key.code {
                KeyCode::Up => self.move_color(-1),
                KeyCode::Down => self.move_color(1),
                _ => {}
            }
        }
    }

    fn on_key_add(&mut self, key: KeyEvent) {
        let Modal::AddColor(m) = &mut self.modal else {
            return;
        };
        match key.code {
            KeyCode::Esc => self.modal = Modal::None,
            KeyCode::Tab => self.open_xkcd(),
            KeyCode::Enter => {
                if let Some(rgb) = color::parse(&m.text) {
                    self.add_color(rgb);
                    self.modal = Modal::None;
                } else {
                    self.status = "couldn't parse \u{2014} try `255 128 0` or `#ff8000`".into();
                    self.status_err = true;
                }
            }
            KeyCode::Backspace => {
                m.text.pop();
            }
            KeyCode::Char(c) => m.text.push(c),
            _ => {}
        }
    }

    fn on_key_xkcd(&mut self, key: KeyEvent) {
        let Modal::Xkcd(m) = &mut self.modal else {
            return;
        };
        match key.code {
            KeyCode::Esc => self.modal = Modal::None,
            KeyCode::Up => m.move_sel(-1),
            KeyCode::Down => m.move_sel(1),
            KeyCode::Enter => {
                if let Some(&idx) = m.filtered.get(m.selected) {
                    self.add_color(Rgb::from_f32(XKCD[idx].1));
                    self.modal = Modal::None;
                }
            }
            KeyCode::Backspace => {
                m.query.pop();
                m.refilter();
            }
            KeyCode::Char(c) => {
                m.query.push(c);
                m.refilter();
            }
            _ => {}
        }
    }

    fn on_key_time(&mut self, key: KeyEvent) {
        let Modal::Time(m) = &mut self.modal else {
            return;
        };
        match key.code {
            KeyCode::Esc => self.modal = Modal::None,
            KeyCode::Enter => {
                if let Ok(v) = m.text.trim().parse::<u64>() {
                    self.settings.color_ms = v.clamp(MIN_PER_MS, MAX_MS);
                    self.republish_plan();
                    self.modal = Modal::None;
                } else {
                    self.status = "enter a whole number of milliseconds".into();
                    self.status_err = true;
                }
            }
            KeyCode::Backspace => {
                m.text.pop();
            }
            KeyCode::Char(c) if c.is_ascii_digit() => m.text.push(c),
            _ => {}
        }
    }

    fn on_key_settings(&mut self, key: KeyEvent) {
        let big = key.modifiers.contains(KeyModifiers::SHIFT);
        let sel = match &self.modal {
            Modal::Settings(m) => m.sel,
            _ => return,
        };
        match key.code {
            KeyCode::Esc | KeyCode::Char('s') | KeyCode::Char('q') => self.modal = Modal::None,
            KeyCode::Enter => self.open_setting_input(sel),
            KeyCode::Up | KeyCode::Char('k') => {
                if let Modal::Settings(m) = &mut self.modal {
                    m.sel = (sel + SETTING_ROWS - 1) % SETTING_ROWS;
                }
            }
            KeyCode::Down | KeyCode::Char('j') => {
                if let Modal::Settings(m) = &mut self.modal {
                    m.sel = (sel + 1) % SETTING_ROWS;
                }
            }
            KeyCode::Left | KeyCode::Char('-') | KeyCode::Char('_') | KeyCode::Char('h') => {
                self.settings.adjust(sel, -1, big);
                self.republish_plan();
            }
            KeyCode::Right | KeyCode::Char('=') | KeyCode::Char('+') | KeyCode::Char('l') => {
                self.settings.adjust(sel, 1, big);
                self.republish_plan();
            }
            _ => {}
        }
    }

    /// Handles the manual numeric-entry popup for a settings row: `Enter` applies (clamped to the row's
    /// range) and returns to the settings list, `Esc` returns without changing anything. Whole-number
    /// rows accept digits only; the two brightness rows also accept a single `.` so a decimal like
    /// `42.5` can be typed.
    fn on_key_setting_input(&mut self, key: KeyEvent) {
        let row = match &self.modal {
            Modal::SettingInput(m) => m.row,
            _ => return,
        };
        let fraction = Settings::row_is_fraction(row);
        match key.code {
            KeyCode::Esc => self.reopen_settings(row),
            KeyCode::Enter => {
                let text = match &self.modal {
                    Modal::SettingInput(m) => m.text.trim().to_string(),
                    _ => return,
                };
                match text.parse::<f64>() {
                    Ok(v) => {
                        self.settings.set_from_input(row, v);
                        self.republish_plan();
                        self.reopen_settings(row);
                    }
                    Err(_) => {
                        self.status = if fraction {
                            "enter a number 0..200 (e.g. 42.5)".into()
                        } else {
                            "enter a whole number (0 or higher)".into()
                        };
                        self.status_err = true;
                    }
                }
            }
            KeyCode::Backspace => {
                if let Modal::SettingInput(m) = &mut self.modal {
                    m.text.pop();
                }
            }
            // A decimal point is allowed once, and only on the brightness rows.
            KeyCode::Char('.') if fraction => {
                if let Modal::SettingInput(m) = &mut self.modal {
                    if !m.text.contains('.') {
                        m.text.push('.');
                    }
                }
            }
            KeyCode::Char(c) if c.is_ascii_digit() => {
                if let Modal::SettingInput(m) = &mut self.modal {
                    m.text.push(c);
                }
            }
            _ => {}
        }
    }

    fn on_key_save_profile(&mut self, key: KeyEvent) {
        let Modal::SaveProfile(m) = &mut self.modal else {
            return;
        };
        match key.code {
            KeyCode::Esc => self.modal = Modal::None,
            KeyCode::Enter => {
                let name = m.text.trim().to_string();
                if name.is_empty() {
                    self.status = "enter a profile name".into();
                    self.status_err = true;
                } else {
                    self.save_profile(&name);
                    self.modal = Modal::None;
                }
            }
            KeyCode::Backspace => {
                m.text.pop();
            }
            KeyCode::Char(c) => m.text.push(c),
            _ => {}
        }
    }

    // ---- template actions --------------------------------------------------------------------

    fn move_tsel(&mut self, d: i32) {
        let n = self.templates.len();
        if n == 0 {
            return;
        }
        self.tsel = (self.tsel as i32 + d).rem_euclid(n as i32) as usize;
    }

    fn toggle_template(&mut self, i: usize) {
        if let Some(t) = self.templates.get_mut(i) {
            t.selected = !t.selected;
        }
    }

    fn toggle_all_templates(&mut self) {
        let all_on = self.templates.iter().all(|t| t.selected);
        for t in &mut self.templates {
            t.selected = !all_on;
        }
    }

    fn rescan(&mut self) {
        self.discovering = true;
        self.status = "rescanning\u{2026}".into();
        self.status_err = false;
        let _ = self.tx.send(ToWorker::Discover);
    }

    /// Reads the highlighted template's four current gradient stops into the palette — the plume
    /// analogue of the XKCD picker, and the fastest way to build a palette that *belongs* on that
    /// engine (it is also the only way to get the authored colours into a saved profile).
    fn capture_palette(&mut self) {
        let Some(row) = self.templates.get(self.tsel) else {
            self.status = "nothing to capture \u{2014} no templates".into();
            self.status_err = true;
            return;
        };
        let id = row.id.clone();
        self.status = format!("capturing {id}\u{2026}");
        self.status_err = false;
        let _ = self.tx.send(ToWorker::Capture { id });
    }

    fn go_show(&mut self) {
        let armed = self.selected_template_ids();
        if armed.is_empty() {
            self.status = "arm at least one template first (space)".into();
            self.status_err = true;
            return;
        }
        self.screen = Screen::Show;
        self.focus = Focus::Colors;
        // Tell the worker which templates to watch so the read-back row populates before a show starts.
        let _ = self.tx.send(ToWorker::Watch { templates: armed });
        if self.colors.is_empty() {
            self.status = "build a palette: a = RGB/hex \u{b7} f = XKCD picker".into();
            self.status_err = false;
        }
    }

    fn back_to_templates(&mut self) {
        if self.burning {
            self.stop_show();
        }
        self.screen = Screen::Templates;
    }

    // ---- show actions ------------------------------------------------------------------------

    fn cycle_focus(&mut self, d: i32) {
        let order = [Focus::Colors, Focus::Time, Focus::Button];
        let cur = order.iter().position(|f| *f == self.focus).unwrap_or(0);
        self.focus = order[(cur as i32 + d).rem_euclid(3) as usize];
    }

    fn move_csel(&mut self, d: i32) {
        let n = self.colors.len();
        if n == 0 {
            return;
        }
        self.csel = (self.csel as i32 + d).rem_euclid(n as i32) as usize;
    }

    /// Moves the selected color one slot earlier/later (`dir` -1/+1), following it with the cursor.
    fn move_color(&mut self, dir: i32) {
        let n = self.colors.len();
        if n < 2 {
            return;
        }
        let j = self.csel as i32 + dir;
        if j < 0 || j >= n as i32 {
            return;
        }
        self.colors.swap(self.csel, j as usize);
        self.csel = j as usize;
        self.republish_plan();
    }

    fn add_color(&mut self, rgb: Rgb) {
        self.colors.push(rgb);
        self.csel = self.colors.len() - 1;
        self.republish_plan();
        self.status = format!("added {}", rgb.to_hex());
        self.status_err = false;
    }

    fn remove_color(&mut self) {
        if self.colors.is_empty() {
            return;
        }
        let removed = self.colors.remove(self.csel.min(self.colors.len() - 1));
        if self.csel >= self.colors.len() {
            self.csel = self.colors.len().saturating_sub(1);
        }
        self.republish_plan();
        self.status = format!("removed {}", removed.to_hex());
        self.status_err = false;
    }

    fn nudge_time(&mut self, dir: i32) {
        let next = self.settings.color_ms as i64 + dir as i64 * TIME_STEP as i64;
        self.settings.color_ms = next.clamp(MIN_PER_MS as i64, MAX_MS as i64) as u64;
        self.republish_plan();
    }

    /// Restores every armed template to its pristine (pre-gatOS) values **without** stopping the show.
    /// While a show runs the very next frame re-writes the colours, so a mid-show reset is a one-frame
    /// flash back to pristine — that is the intended, honest behaviour.
    fn reset_armed(&mut self) {
        let _ = self.tx.send(ToWorker::ResetTemplates);
        self.status = "resetting templates to pristine\u{2026}".into();
        self.status_err = false;
    }

    fn toggle_show(&mut self) {
        if self.burning {
            self.stop_show();
        } else {
            self.start_show();
        }
    }

    fn start_show(&mut self) {
        let templates = self.selected_template_ids();
        if templates.is_empty() {
            self.status = "no templates armed \u{2014} go back (b) and arm one".into();
            self.status_err = true;
            return;
        }
        if self.colors.is_empty() {
            self.status = "add at least one colour (a or f)".into();
            self.status_err = true;
            return;
        }
        self.burning = true; // optimistic; the first Tick confirms
        self.status = "LIGHT THE RAINBOW! \u{1f308}".into();
        self.status_err = false;
        let _ = self.tx.send(ToWorker::Start {
            templates,
            plan: self.plan(),
            hz: self.settings.hz,
            batch: self.settings.batch,
        });
    }

    fn stop_show(&mut self) {
        self.burning = false;
        self.live = None;
        let _ = self.tx.send(ToWorker::Stop);
        self.status = "cutting the fire \u{2014} restoring pristine values\u{2026}".into();
        self.status_err = false;
    }

    /// Pushes the current palette/timing to a running show so edits take effect without a restart.
    fn republish_plan(&self) {
        if self.burning {
            let _ = self.tx.send(ToWorker::Update {
                plan: self.plan(),
                hz: self.settings.hz,
                batch: self.settings.batch,
            });
        }
    }

    // ---- modals: open ------------------------------------------------------------------------

    fn open_add_color(&mut self) {
        self.modal = Modal::AddColor(AddColorModal {
            text: String::new(),
            area: Rect::default(),
        });
    }

    pub fn open_xkcd(&mut self) {
        let hays: Vec<String> = XKCD.iter().map(|(n, _)| color::humanize(n)).collect();
        let mut m = XkcdModal {
            query: String::new(),
            hays,
            filtered: (0..XKCD.len()).collect(),
            selected: 0,
            offset: 0,
            area: Rect::default(),
            item_rects: Vec::new(),
        };
        m.refilter();
        self.modal = Modal::Xkcd(m);
    }

    fn open_time(&mut self) {
        self.modal = Modal::Time(TimeModal {
            text: self.settings.color_ms.to_string(),
            area: Rect::default(),
        });
    }

    fn open_settings(&mut self) {
        self.reopen_settings(0);
    }

    /// Opens (or returns to) the settings popup with row `sel` highlighted.
    fn reopen_settings(&mut self, sel: usize) {
        self.modal = Modal::Settings(SettingsModal {
            sel: sel.min(SETTING_ROWS - 1),
            area: Rect::default(),
            rows: Vec::new(),
        });
    }

    /// Opens the manual numeric-entry popup for a settings row, prefilled with its current value.
    fn open_setting_input(&mut self, row: usize) {
        self.modal = Modal::SettingInput(SettingInputModal {
            row,
            text: self.settings.row_input_value(row),
            area: Rect::default(),
        });
    }

    fn open_save_profile(&mut self) {
        self.modal = Modal::SaveProfile(SaveProfileModal {
            text: String::new(),
            area: Rect::default(),
        });
    }

    /// Serializes the current palette + settings (not the armed templates) to `<name>.yaml` and reports
    /// the written path (or the error) on the status line.
    fn save_profile(&mut self, name: &str) {
        let prof = Profile {
            settings: self.settings,
            colors: self.colors.clone(),
        };
        match profile::save(name, &prof) {
            Ok(path) => {
                self.status = format!("saved profile \u{2192} {}", path.display());
                self.status_err = false;
            }
            Err(e) => {
                self.status = format!("save failed \u{2014} {e}");
                self.status_err = true;
            }
        }
    }

    /// Opens the quit confirmation (the `q`/Esc-to-leave gate). Confirming calls [`Self::quit`].
    fn request_quit(&mut self) {
        self.modal = Modal::ConfirmQuit(ConfirmQuitModal::default());
    }

    fn quit(&mut self) {
        if self.burning {
            // Reset the templates on the way out; `main` waits briefly for the Stopped ack.
            let _ = self.tx.send(ToWorker::Stop);
            self.pending_stop = true;
            self.burning = false;
        }
        self.should_quit = true;
    }

    // ---- mouse -------------------------------------------------------------------------------

    pub fn on_mouse(&mut self, m: MouseEvent) {
        match &self.modal {
            Modal::Xkcd(_) => self.on_mouse_xkcd(m),
            Modal::Settings(_) => self.on_mouse_settings(m),
            Modal::SettingInput(_) => self.on_mouse_setting_input(m),
            Modal::ConfirmQuit(_) => self.on_mouse_confirm_quit(m),
            Modal::AddColor(_) | Modal::Time(_) | Modal::SaveProfile(_) => {
                // Click outside the box dismisses; otherwise ignore (typing drives these).
                if let MouseEventKind::Down(MouseButton::Left) = m.kind {
                    let area = match &self.modal {
                        Modal::AddColor(a) => a.area,
                        Modal::Time(t) => t.area,
                        Modal::SaveProfile(s) => s.area,
                        _ => Rect::default(),
                    };
                    if !area.contains(Position {
                        x: m.column,
                        y: m.row,
                    }) {
                        self.modal = Modal::None;
                    }
                }
            }
            Modal::None if self.hidden => self.on_mouse_hidden(m),
            Modal::None => match self.screen {
                Screen::Templates => self.on_mouse_templates(m),
                Screen::Show => self.on_mouse_show(m),
            },
        }
    }

    fn on_mouse_templates(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => self.move_tsel(-1),
            MouseEventKind::ScrollDown => self.move_tsel(1),
            MouseEventKind::Down(MouseButton::Left) => {
                let pos = Position {
                    x: m.column,
                    y: m.row,
                };
                if let Some(&(_, i)) = self.template_rects.iter().find(|(r, _)| r.contains(pos)) {
                    self.tsel = i;
                    self.toggle_template(i);
                }
            }
            _ => {}
        }
    }

    fn on_mouse_show(&mut self, m: MouseEvent) {
        let pos = Position {
            x: m.column,
            y: m.row,
        };
        match m.kind {
            MouseEventKind::ScrollUp => {
                self.focus = Focus::Colors;
                self.move_csel(-1);
            }
            MouseEventKind::ScrollDown => {
                self.focus = Focus::Colors;
                self.move_csel(1);
            }
            MouseEventKind::Down(MouseButton::Left) => {
                if let Some(&(_, i)) = self.up_btns.iter().find(|(r, _)| r.contains(pos)) {
                    self.focus = Focus::Colors;
                    self.csel = i;
                    self.move_color(-1);
                } else if let Some(&(_, i)) = self.down_btns.iter().find(|(r, _)| r.contains(pos)) {
                    self.focus = Focus::Colors;
                    self.csel = i;
                    self.move_color(1);
                } else if let Some(&(_, i)) = self.del_btns.iter().find(|(r, _)| r.contains(pos)) {
                    self.focus = Focus::Colors;
                    self.csel = i;
                    self.remove_color();
                } else if let Some(&(_, i)) = self.color_rects.iter().find(|(r, _)| r.contains(pos)) {
                    self.focus = Focus::Colors;
                    self.csel = i;
                } else if self.add_btn.contains(pos) {
                    self.open_add_color();
                } else if self.xkcd_btn.contains(pos) {
                    self.open_xkcd();
                } else if self.time_minus.contains(pos) {
                    self.focus = Focus::Time;
                    self.nudge_time(-1);
                } else if self.time_plus.contains(pos) {
                    self.focus = Focus::Time;
                    self.nudge_time(1);
                } else if self.reset_btn.contains(pos) {
                    self.reset_armed();
                } else if self.settings_btn.contains(pos) {
                    self.open_settings();
                } else if self.burn_btn.contains(pos) {
                    self.toggle_show();
                } else if self.back_btn.contains(pos) {
                    self.back_to_templates();
                }
            }
            _ => {}
        }
    }

    /// Setting-input clicks: a click outside the box returns to the settings list (typing drives it).
    fn on_mouse_setting_input(&mut self, m: MouseEvent) {
        if let MouseEventKind::Down(MouseButton::Left) = m.kind {
            let (row, inside) = match &self.modal {
                Modal::SettingInput(s) => (
                    s.row,
                    s.area.contains(Position {
                        x: m.column,
                        y: m.row,
                    }),
                ),
                _ => return,
            };
            if !inside {
                self.reopen_settings(row);
            }
        }
    }

    /// Quit-confirmation clicks: the two buttons, or click outside to cancel.
    fn on_mouse_confirm_quit(&mut self, m: MouseEvent) {
        if let MouseEventKind::Down(MouseButton::Left) = m.kind {
            let pos = Position {
                x: m.column,
                y: m.row,
            };
            let (quit_hit, inside) = match &self.modal {
                Modal::ConfirmQuit(c) => (c.quit_btn.contains(pos), c.area.contains(pos)),
                _ => (false, false),
            };
            if quit_hit {
                self.modal = Modal::None;
                self.quit();
            } else if !inside
                || matches!(&self.modal, Modal::ConfirmQuit(c) if c.cancel_btn.contains(pos))
            {
                self.modal = Modal::None;
            }
        }
    }

    /// Hide-mode clicks: only the three bar buttons (show toggle / reset / show-the-UI) are live.
    fn on_mouse_hidden(&mut self, m: MouseEvent) {
        if let MouseEventKind::Down(MouseButton::Left) = m.kind {
            let pos = Position {
                x: m.column,
                y: m.row,
            };
            if self.burn_btn.contains(pos) {
                self.toggle_show();
            } else if self.reset_btn.contains(pos) {
                self.reset_armed();
            } else if self.hide_show_btn.contains(pos) {
                self.hidden = false;
            }
        }
    }

    fn on_mouse_settings(&mut self, m: MouseEvent) {
        let sel = match &self.modal {
            Modal::Settings(s) => s.sel,
            _ => return,
        };
        match m.kind {
            MouseEventKind::ScrollUp => {
                self.settings.adjust(sel, 1, false);
                self.republish_plan();
            }
            MouseEventKind::ScrollDown => {
                self.settings.adjust(sel, -1, false);
                self.republish_plan();
            }
            MouseEventKind::Down(MouseButton::Left) => {
                let pos = Position {
                    x: m.column,
                    y: m.row,
                };
                let (hit, inside) = match &self.modal {
                    Modal::Settings(s) => (
                        s.rows
                            .iter()
                            .find(|(r, _)| r.contains(pos))
                            .map(|&(_, i)| i),
                        s.area.contains(pos),
                    ),
                    _ => (None, false),
                };
                if let Some(i) = hit {
                    if let Modal::Settings(s) = &mut self.modal {
                        s.sel = i;
                    }
                } else if !inside {
                    self.modal = Modal::None;
                }
            }
            _ => {}
        }
    }

    fn on_mouse_xkcd(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => {
                if let Modal::Xkcd(x) = &mut self.modal {
                    x.move_sel(-1)
                }
            }
            MouseEventKind::ScrollDown => {
                if let Modal::Xkcd(x) = &mut self.modal {
                    x.move_sel(1)
                }
            }
            MouseEventKind::Down(MouseButton::Left) => {
                let pos = Position {
                    x: m.column,
                    y: m.row,
                };
                let pick = if let Modal::Xkcd(x) = &mut self.modal {
                    if let Some(&(_, row)) = x.item_rects.iter().find(|(r, _)| r.contains(pos)) {
                        x.selected = row;
                        x.filtered.get(row).copied()
                    } else if !x.area.contains(pos) {
                        self.modal = Modal::None;
                        return;
                    } else {
                        None
                    }
                } else {
                    None
                };
                if let Some(idx) = pick {
                    self.add_color(Rgb::from_f32(XKCD[idx].1));
                    self.modal = Modal::None;
                }
            }
            _ => {}
        }
    }
}

impl XkcdModal {
    pub fn move_sel(&mut self, d: i32) {
        let n = self.filtered.len();
        if n == 0 {
            return;
        }
        self.selected = (self.selected as i32 + d).rem_euclid(n as i32) as usize;
    }

    /// Fuzzy, space-separated **AND** filter over the humanized color names (same discipline as the
    /// `simfs-dashboard` search): every term must match, ranked best-first.
    pub fn refilter(&mut self) {
        let terms: Vec<String> = self.query.split_whitespace().map(str::to_lowercase).collect();
        let mut scored: Vec<(i64, usize)> = self
            .hays
            .iter()
            .enumerate()
            .filter_map(|(i, hay)| {
                let mut total = 0i64;
                for t in &terms {
                    total += fuzzy_score(t, hay)?;
                }
                Some((total, i))
            })
            .collect();
        scored.sort_by_key(|&(s, _)| std::cmp::Reverse(s));
        self.filtered = scored.into_iter().map(|(_, i)| i).collect();
        if self.selected >= self.filtered.len() {
            self.selected = self.filtered.len().saturating_sub(1);
        }
        self.offset = 0;
    }
}

/// Scores one (already-lowercased) `term` against an (already-lowercased) `hay`, or `None` when it
/// doesn't match. Contiguous substrings score high (with a word-boundary bonus); a scattered
/// subsequence scores low but still counts.
fn fuzzy_score(term: &str, hay: &str) -> Option<i64> {
    if term.is_empty() {
        return Some(0);
    }
    if let Some(pos) = hay.find(term) {
        let boundary = pos == 0 || matches!(hay.as_bytes()[pos - 1], b' ' | b'-' | b'_');
        let mut score = 1000 - pos.min(500) as i64;
        if boundary {
            score += 300;
        }
        return Some(score);
    }
    subsequence_score(term, hay)
}

fn subsequence_score(term: &str, hay: &str) -> Option<i64> {
    let mut chars = term.chars();
    let mut need = chars.next();
    let mut gaps = 0i64;
    let mut started = false;
    for hc in hay.chars() {
        match need {
            Some(tc) if tc == hc => {
                started = true;
                need = chars.next();
            }
            _ if started => gaps += 1,
            _ => {}
        }
    }
    need.is_none().then(|| 100 - gaps.min(90))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::source::{Health, TemplateValues};

    fn app() -> App {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        App::new(tx, "mock".into(), Settings::default(), Vec::new())
    }

    fn tpl(id: &str, stops: usize) -> PlumeTemplate {
        PlumeTemplate {
            id: id.into(),
            colors: std::array::from_fn(|slot| {
                if slot < stops {
                    format!("debug/engineplume/templates/{id}/emission/color{slot}")
                } else {
                    String::new()
                }
            }),
            brightness: Some(format!(
                "debug/engineplume/templates/{id}/emission/brightness"
            )),
            reset: format!("debug/engineplume/templates/{id}/reset"),
            current: Some(TemplateValues {
                colors: [None; SLOTS],
                brightness: Some(12.5),
            }),
        }
    }

    fn catalog_msg() -> FromWorker {
        FromWorker::Catalog {
            templates: vec![tpl("Kerolox", 4), tpl("Hydrolox", 4)],
            health: Health {
                connected: true,
                control: true,
                debug: true,
            },
        }
    }

    fn press(a: &mut App, c: char) {
        a.on_key(KeyEvent::new(KeyCode::Char(c), KeyModifiers::NONE));
    }

    #[test]
    fn selection_survives_a_rescan() {
        let mut a = app();
        a.apply(catalog_msg());
        a.toggle_template(0);
        assert_eq!(a.selected_template_ids(), vec!["Kerolox".to_string()]);
        a.apply(catalog_msg());
        assert_eq!(a.selected_template_ids(), vec!["Kerolox".to_string()]);
    }

    #[test]
    fn cannot_start_without_arming_a_template() {
        let mut a = app();
        a.apply(catalog_msg());
        a.go_show();
        assert_eq!(a.screen, Screen::Templates); // refused, with a hint
        assert!(a.status_err);
        a.toggle_template(0);
        a.go_show();
        assert_eq!(a.screen, Screen::Show);
    }

    #[test]
    fn color_palette_add_remove_reorder() {
        let mut a = app();
        a.add_color(Rgb::from_u8(255, 0, 0));
        a.add_color(Rgb::from_u8(0, 255, 0));
        a.add_color(Rgb::from_u8(0, 0, 255));
        assert_eq!(a.colors.len(), 3);
        a.csel = 2;
        a.move_color(-1);
        assert_eq!(a.csel, 1);
        assert_eq!(a.colors[1], Rgb::from_u8(0, 0, 255));
        a.remove_color();
        assert_eq!(a.colors.len(), 2);
    }

    #[test]
    fn stripe_time_has_a_floor() {
        let mut a = app();
        a.settings.color_ms = 100;
        a.nudge_time(-1); // -100 -> would be 0, clamped to the floor
        assert_eq!(a.settings.color_ms, MIN_PER_MS);
    }

    #[test]
    fn settings_adjust_covers_every_row() {
        let mut s = Settings::default();
        // Frame rate clamps at 240 (row 0).
        for _ in 0..100 {
            s.adjust(0, 1, true);
        }
        assert_eq!(s.hz, 240.0);
        s.adjust(1, 1, false); // fade steps +1
        assert_eq!(s.steps, 1);
        let c0 = s.color_ms;
        s.adjust(2, 1, false); // stripe time +100
        assert_eq!(s.color_ms, c0 + 100);
        s.adjust(3, 1, false); // slot stagger +10
        assert_eq!(s.slot_stagger_ms, 10.0);
        s.adjust(4, 1, false); // slot step 1 -> 2 (always a unit step)
        assert_eq!(s.slot_step, 2);
        s.adjust(5, 1, true); // tpl stagger +100 (coarse)
        assert_eq!(s.tpl_stagger_ms, 100.0);
        s.adjust(6, 1, true); // bright min +10 (coarse)
        assert_eq!(s.bright_min, 10.0);
        s.adjust(7, 1, false); // bright max +1 -> the effect switches on
        assert_eq!(s.bright_max, 1.0);
        assert!(!s.bright_is_off());
        s.adjust(8, 1, false); // bright time +100
        assert_eq!(s.bright_ms, 700);
        s.adjust(9, 1, false); // bright steps +1
        assert_eq!(s.bright_steps, 1);
        // The knob ranges hold at both ends.
        for _ in 0..1000 {
            s.adjust(4, 1, true);
            s.adjust(7, 1, true);
            s.adjust(6, -1, true);
        }
        assert_eq!(s.slot_step, MAX_SLOT_STEP);
        assert_eq!(s.bright_max, BRIGHT_MAX);
        assert_eq!(s.bright_min, 0.0);
    }

    #[test]
    fn enter_on_a_settings_row_opens_manual_input_and_applies_it() {
        let mut a = app();
        a.screen = Screen::Show;
        a.open_settings();
        // Move to "stripe time" (row 2) and open the manual-entry popup.
        a.on_key(KeyEvent::new(KeyCode::Down, KeyModifiers::NONE));
        a.on_key(KeyEvent::new(KeyCode::Down, KeyModifiers::NONE));
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        match &a.modal {
            Modal::SettingInput(m) => assert_eq!(m.row, 2),
            _ => panic!("expected SettingInput modal"),
        }
        // Clear the prefill and type an arbitrary value.
        for _ in 0..8 {
            a.on_key(KeyEvent::new(KeyCode::Backspace, KeyModifiers::NONE));
        }
        for c in "4200".chars() {
            press(&mut a, c);
        }
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert_eq!(a.settings.color_ms, 4200);
        // It returns to the settings list on the same row.
        match &a.modal {
            Modal::Settings(m) => assert_eq!(m.sel, 2),
            _ => panic!("expected to return to Settings"),
        }
    }

    #[test]
    fn manual_input_clamps_to_the_row_range() {
        let mut s = Settings::default();
        s.set_from_input(0, 0.0); // frame rate can't be 0
        assert_eq!(s.hz, 1.0);
        s.set_from_input(0, 99_999.0); // ...nor absurdly high
        assert_eq!(s.hz, 240.0);
        s.set_from_input(2, 0.0); // duration floor
        assert_eq!(s.color_ms, 50);
        s.set_from_input(4, 99.0); // slot step ceiling
        assert_eq!(s.slot_step, MAX_SLOT_STEP);
        s.set_from_input(7, 999.0); // the leaf's real 0..200 ceiling
        assert_eq!(s.bright_max, 200.0);
        s.set_from_input(1, 7.0); // fade steps takes the value verbatim
        assert_eq!(s.steps, 7);
    }

    #[test]
    fn typing_a_decimal_sets_a_brightness_value() {
        let mut a = app();
        a.screen = Screen::Show;
        a.open_settings();
        // Walk to "bright min" (row 6) and open the manual-entry popup.
        for _ in 0..6 {
            a.on_key(KeyEvent::new(KeyCode::Down, KeyModifiers::NONE));
        }
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        match &a.modal {
            Modal::SettingInput(m) => assert_eq!(m.row, 6),
            _ => panic!("expected SettingInput modal"),
        }
        // Clear the prefill and type a decimal — the '.' must be accepted on this brightness row.
        for _ in 0..8 {
            a.on_key(KeyEvent::new(KeyCode::Backspace, KeyModifiers::NONE));
        }
        for c in "42.5".chars() {
            press(&mut a, c);
        }
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert!((a.settings.bright_min - 42.5).abs() < 1e-9);
    }

    #[test]
    fn brightness_off_when_max_is_zero() {
        let mut s = Settings::default();
        assert!(s.bright_is_off()); // the default leaves emission/brightness alone
        assert!(!s.bright_is_pinned());
        assert_eq!(s.row_value(7), "off (leaf untouched)");
        s.bright_min = 42.5;
        s.bright_max = 42.5;
        assert!(!s.bright_is_off());
        assert!(s.bright_is_pinned()); // a collapsed non-zero range pins the leaf
    }

    #[test]
    fn manual_input_esc_returns_without_change() {
        let mut a = app();
        a.open_settings();
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE)); // open input on row 0
        let before = a.settings.hz;
        a.on_key(KeyEvent::new(KeyCode::Esc, KeyModifiers::NONE));
        assert!(matches!(a.modal, Modal::Settings(_)));
        assert_eq!(a.settings.hz, before);
    }

    #[test]
    fn xkcd_search_is_fuzzy_and_anded() {
        let mut a = app();
        a.open_xkcd();
        let Modal::Xkcd(m) = &mut a.modal else {
            panic!("expected xkcd modal")
        };
        m.query = "cloudy blue".into();
        m.refilter();
        let top = m.filtered[0];
        assert_eq!(XKCD[top].0, "CloudyBlue");
        m.query = "blue zzzzz".into();
        m.refilter();
        assert!(m.filtered.is_empty());
    }

    #[test]
    fn readback_reply_updates_the_row() {
        let mut a = app();
        a.apply(FromWorker::Readback {
            id: Some("Kerolox".into()),
            values: TemplateValues {
                colors: [
                    Some(Rgb::new(1.0, 0.0, 0.0)),
                    None,
                    None,
                    Some(Rgb::new(0.0, 0.0, 1.0)),
                ],
                brightness: Some(12.5),
            },
            armed: 3,
        });
        assert_eq!(a.readback.id.as_deref(), Some("Kerolox"));
        assert_eq!(a.readback.armed, 3);
        assert_eq!(a.readback.brightness, Some(12.5));
        assert_eq!(a.readback.colors[0], Some(Rgb::new(1.0, 0.0, 0.0)));
        assert_eq!(a.readback.colors[1], None);
    }

    #[test]
    fn capture_appends_four_stops() {
        let mut a = app();
        a.apply(FromWorker::Captured {
            id: "Kerolox".into(),
            colors: vec![
                Rgb::new(1.0, 0.0, 0.0),
                Rgb::new(1.0, 0.6, 0.0),
                Rgb::new(1.0, 1.0, 0.0),
                Rgb::new(0.2, 1.0, 0.0),
            ],
        });
        assert_eq!(a.colors.len(), 4);
        assert_eq!(a.colors[0], Rgb::new(1.0, 0.0, 0.0));
        assert!(!a.status_err);
        // A flat gradient collapses to one entry (consecutive duplicates are skipped).
        let mut b = app();
        b.apply(FromWorker::Captured {
            id: "Solid".into(),
            colors: vec![Rgb::WHITE; 4],
        });
        assert_eq!(b.colors.len(), 1);
    }

    #[test]
    fn h_toggles_hide_mode_on_the_show_screen() {
        let mut a = app();
        a.screen = Screen::Show;
        assert!(!a.hidden);
        press(&mut a, 'h'); // hide
        assert!(a.hidden);
        // While hidden, normal show keys don't fire (e.g. 'a' must not open the add-color modal).
        press(&mut a, 'a');
        assert!(matches!(a.modal, Modal::None));
        press(&mut a, 'h'); // show again
        assert!(!a.hidden);
    }

    #[test]
    fn hide_mode_still_toggles_the_show() {
        let mut a = app();
        a.apply(catalog_msg());
        a.toggle_template(0);
        a.go_show();
        a.add_color(Rgb::from_u8(255, 0, 0));
        a.hidden = true;
        assert!(!a.burning);
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert!(a.burning); // optimistic start fired even from the hidden bar
    }

    #[test]
    fn w_saves_a_profile_round_tripping_palette_and_settings() {
        // A path-like name is written verbatim (no dependence on the global `$NYAN_PROFILE_DIR`).
        let dir = std::env::temp_dir().join(format!("nyan_app_prof_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let target = dir.join("myshow.yaml");
        let name = target.to_string_lossy().into_owned();

        let mut a = app();
        a.screen = Screen::Show;
        a.add_color(Rgb::from_u8(10, 20, 30));
        a.settings.color_ms = 777;
        a.settings.batch = false;
        // Open the save modal, type the (path-like) name, Enter.
        press(&mut a, 'w');
        assert!(matches!(a.modal, Modal::SaveProfile(_)));
        for c in name.chars() {
            press(&mut a, c);
        }
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert!(matches!(a.modal, Modal::None));
        assert!(!a.status_err, "save should report success: {}", a.status);

        let loaded = profile::load(&name).unwrap();
        assert_eq!(loaded.colors, vec![Rgb::from_u8(10, 20, 30)]);
        assert_eq!(loaded.settings.color_ms, 777);
        assert!(!loaded.settings.batch);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn q_asks_before_quitting_and_cancel_keeps_running() {
        let mut a = app();
        press(&mut a, 'q');
        assert!(matches!(a.modal, Modal::ConfirmQuit(_)));
        assert!(!a.should_quit, "q alone must not quit");
        // n cancels.
        press(&mut a, 'n');
        assert!(matches!(a.modal, Modal::None));
        assert!(!a.should_quit);
    }

    #[test]
    fn confirming_the_quit_dialog_quits() {
        let mut a = app();
        press(&mut a, 'q');
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert!(a.should_quit);
    }

    #[test]
    fn q_confirmation_works_from_the_show_screen_too() {
        let mut a = app();
        a.screen = Screen::Show;
        press(&mut a, 'q');
        assert!(matches!(a.modal, Modal::ConfirmQuit(_)));
        assert!(!a.should_quit);
        press(&mut a, 'y');
        assert!(a.should_quit);
    }

    #[test]
    fn empty_profile_name_is_rejected() {
        let mut a = app();
        a.screen = Screen::Show;
        press(&mut a, 'w');
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE)); // no name typed
        assert!(matches!(a.modal, Modal::SaveProfile(_))); // stays open
        assert!(a.status_err);
    }
}
