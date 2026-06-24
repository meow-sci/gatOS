//! Application state + input handling. Two screens — a vessel **multi-select** and the **party**
//! console — plus four modals (manual color entry, the XKCD fuzzy picker, the per-color-time editor,
//! and the **settings** popup). The render pass ([`crate::ui`]) reads this state and writes back the
//! interactive hit-test rects (vessel rows, color-row buttons, the party/refill/settings buttons,
//! modal lists) that the mouse handler tests on the next event, so keyboard and mouse drive the exact
//! same actions.
//!
//! All `/sim` I/O lives on the worker thread ([`crate::source`]); this type only *sends* commands
//! (discover / watch / start / update / refill / stop) and folds the worker's replies into display
//! state. Every display-affecting knob lives in [`Settings`] and is editable live from the settings
//! popup; a running party adopts changes immediately (the plan is republished without resetting the
//! clock).

use ratatui::crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::layout::{Position, Rect};
use tokio::sync::mpsc::UnboundedSender;

use crate::color::{self, Rgb};
use crate::party::Plan;
use crate::profile::{self, Profile};
use crate::source::{FromWorker, ToWorker, VesselLights};
use crate::xkcd::XKCD;

/// Which of the two top-level screens is showing.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Screen {
    Vessels,
    Party,
}

/// Focus within the party screen — Tab cycles it; the active section interprets the arrow/letter keys.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Focus {
    Colors,
    Time,
    Button,
}

/// Every display-affecting knob, all live-editable from the settings popup. The animation timing is
/// **decoupled** from the color timing: `color_ms` paces the palette cross-fade while `anim_ms` paces
/// the deploy goal-pulse, and each has its own per-light stagger.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Settings {
    /// Animation frame rate (the worker's dispatch cadence), Hz, clamped 1..240.
    pub hz: f64,
    /// Fade quantization steps per color segment (0 = continuous).
    pub steps: u32,
    /// Per-color cross-fade duration, ms.
    pub color_ms: u64,
    /// Per goal-pulse (deploy animation) half-period, ms — keep ≥ the ~2 s in-game stroke so it finishes.
    pub anim_ms: u64,
    /// Per-light color-clock stagger, ms.
    pub color_stagger_ms: f64,
    /// Per-light animation-clock stagger, ms.
    pub anim_stagger_ms: f64,
    /// Random-brightness range floor, on the `0..`[`BRIGHT_SCALE`] scale (`bright_min == bright_max`
    /// disables the effect). Divided by [`BRIGHT_SCALE`] to get the 0..1 color multiplier.
    pub bright_min: f64,
    /// Random-brightness range ceiling, on the `0..`[`BRIGHT_SCALE`] scale.
    pub bright_max: f64,
    /// Time each random brightness target holds before drifting to the next, ms.
    pub bright_ms: u64,
    /// Brightness-drift quantization steps (0 = continuous).
    pub bright_steps: u32,
    /// Animation actuation floor, on the `0..`[`ANIM_SCALE`] scale — the goal the retract half drives to
    /// (`anim_min == anim_max` turns the animation off). Divided by [`ANIM_SCALE`] for the 0..1 setpoint.
    pub anim_min: f64,
    /// Animation actuation ceiling, on the `0..`[`ANIM_SCALE`] scale — the goal the extend half drives to.
    pub anim_max: f64,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            hz: 30.0,
            steps: 0,
            color_ms: 1200,
            anim_ms: 2500,
            color_stagger_ms: 0.0,
            anim_stagger_ms: 0.0,
            // Brightness variation off by default (min == max == full = the top of the scale).
            bright_min: BRIGHT_SCALE,
            bright_max: BRIGHT_SCALE,
            bright_ms: 600,
            bright_steps: 0,
            // Animation pulses across the full retract..extend range by default (0..1 = 0..ANIM_SCALE).
            anim_min: 0.0,
            anim_max: ANIM_SCALE,
        }
    }
}

/// The number of editable rows in the settings popup (see [`Settings::adjust`]).
pub const SETTING_ROWS: usize = 12;

const MIN_MS: u64 = 50;
const MAX_MS: u64 = 60_000;
const MAX_STAGGER: f64 = 60_000.0;
/// The top of the brightness min/max scale (full brightness, = a 1.0 color multiplier). The
/// configured 0..[`BRIGHT_SCALE`] value is divided by this to get the actual 0..1 multiplier.
pub const BRIGHT_SCALE: f64 = 10_000.0;
/// The top of the animation actuation min/max scale (= a fully-extended `1.0` goal). The configured
/// 0..[`ANIM_SCALE`] value is divided by this to get the actual 0..1 goal setpoint.
pub const ANIM_SCALE: f64 = 10_000.0;

impl Settings {
    /// Nudges row `row` by `dir` (-1/+1), a coarse step when `big` (Shift), within each knob's range.
    /// Live-applied by the caller (republished to a running party).
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
                self.color_ms = (self.color_ms as i64 + d * step).clamp(MIN_MS as i64, MAX_MS as i64) as u64;
            }
            3 => {
                let step = if big { 1000 } else { 100 };
                self.anim_ms = (self.anim_ms as i64 + d * step).clamp(MIN_MS as i64, MAX_MS as i64) as u64;
            }
            4 => {
                let step = if big { 100.0 } else { 10.0 };
                self.color_stagger_ms = (self.color_stagger_ms + dir as f64 * step).clamp(0.0, MAX_STAGGER);
            }
            5 => {
                let step = if big { 100.0 } else { 10.0 };
                self.anim_stagger_ms = (self.anim_stagger_ms + dir as f64 * step).clamp(0.0, MAX_STAGGER);
            }
            6 => {
                let step = if big { 20.0 } else { 1.0 };
                self.bright_min = (self.bright_min + dir as f64 * step).clamp(0.0, BRIGHT_SCALE);
            }
            7 => {
                let step = if big { 20.0 } else { 1.0 };
                self.bright_max = (self.bright_max + dir as f64 * step).clamp(0.0, BRIGHT_SCALE);
            }
            8 => {
                let step = if big { 1000 } else { 100 };
                self.bright_ms = (self.bright_ms as i64 + d * step).clamp(MIN_MS as i64, MAX_MS as i64) as u64;
            }
            9 => {
                let step = if big { 10 } else { 1 };
                self.bright_steps = (self.bright_steps as i64 + d * step).clamp(0, 1000) as u32;
            }
            10 => {
                let step = if big { 500.0 } else { 50.0 };
                self.anim_min = (self.anim_min + dir as f64 * step).clamp(0.0, ANIM_SCALE);
            }
            11 => {
                let step = if big { 500.0 } else { 50.0 };
                self.anim_max = (self.anim_max + dir as f64 * step).clamp(0.0, ANIM_SCALE);
            }
            _ => {}
        }
    }

    /// The label for a settings row.
    pub fn row_label(row: usize) -> &'static str {
        match row {
            0 => "frame rate",
            1 => "fade steps",
            2 => "color time",
            3 => "anim time",
            4 => "color stagger",
            5 => "anim stagger",
            6 => "bright min",
            7 => "bright max",
            8 => "bright time",
            9 => "bright steps",
            10 => "anim min",
            11 => "anim max",
            _ => "",
        }
    }

    /// The formatted current value for a settings row.
    pub fn row_value(&self, row: usize) -> String {
        match row {
            0 => format!("{} Hz", fmt_hz(self.hz)),
            1 => {
                if self.steps == 0 {
                    "continuous".into()
                } else {
                    format!("{} steps", self.steps)
                }
            }
            2 => format!("{} ms", self.color_ms),
            3 => {
                if self.anim_min == self.anim_max {
                    format!("{} ms (off)", self.anim_ms)
                } else {
                    format!("{} ms", self.anim_ms)
                }
            }
            4 => format!("{} ms", self.color_stagger_ms as u64),
            5 => format!("{} ms", self.anim_stagger_ms as u64),
            6 => format!("{}", self.bright_min as u64),
            7 => format!("{}", self.bright_max as u64),
            8 => {
                if self.bright_min >= self.bright_max {
                    format!("{} ms (off)", self.bright_ms)
                } else {
                    format!("{} ms", self.bright_ms)
                }
            }
            9 => {
                if self.bright_steps == 0 {
                    "continuous".into()
                } else {
                    format!("{} steps", self.bright_steps)
                }
            }
            10 => format!("{}", self.anim_min as u64),
            11 => format!("{}", self.anim_max as u64),
            _ => String::new(),
        }
    }

    /// The current value of a row as a bare integer string — what the manual-input popup prefills (so
    /// the user edits from the live value rather than an empty field).
    pub fn row_input_value(&self, row: usize) -> String {
        match row {
            0 => format!("{}", self.hz.round() as u64),
            1 => self.steps.to_string(),
            2 => self.color_ms.to_string(),
            3 => self.anim_ms.to_string(),
            4 => format!("{}", self.color_stagger_ms as u64),
            5 => format!("{}", self.anim_stagger_ms as u64),
            6 => format!("{}", self.bright_min as u64),
            7 => format!("{}", self.bright_max as u64),
            8 => self.bright_ms.to_string(),
            9 => self.bright_steps.to_string(),
            10 => format!("{}", self.anim_min as u64),
            11 => format!("{}", self.anim_max as u64),
            _ => String::new(),
        }
    }

    /// Applies a manually-typed whole number `v` to row `row`, clamped to that row's valid range. The
    /// input is unconstrained (0 or higher); the clamp keeps the setting usable (e.g. frame rate can't
    /// be 0, durations have a floor).
    pub fn set_from_input(&mut self, row: usize, v: u64) {
        let vf = v as f64;
        match row {
            0 => self.hz = vf.clamp(1.0, 240.0),
            1 => self.steps = v.min(1000) as u32,
            2 => self.color_ms = v.clamp(MIN_MS, MAX_MS),
            3 => self.anim_ms = v.clamp(MIN_MS, MAX_MS),
            4 => self.color_stagger_ms = vf.clamp(0.0, MAX_STAGGER),
            5 => self.anim_stagger_ms = vf.clamp(0.0, MAX_STAGGER),
            6 => self.bright_min = vf.clamp(0.0, BRIGHT_SCALE),
            7 => self.bright_max = vf.clamp(0.0, BRIGHT_SCALE),
            8 => self.bright_ms = v.clamp(MIN_MS, MAX_MS),
            9 => self.bright_steps = v.min(1000) as u32,
            10 => self.anim_min = vf.clamp(0.0, ANIM_SCALE),
            11 => self.anim_max = vf.clamp(0.0, ANIM_SCALE),
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

/// Friendly rendering of a goal actuation setpoint for the status line / live band: `off` when the
/// animation is disabled (`None`), else the 0..1 fraction trimmed to 2 decimals (`1`, `0`, `0.75`).
pub fn fmt_goal_display(goal: Option<f64>) -> String {
    match goal {
        None => "off".to_string(),
        Some(v) => {
            let s = format!("{:.2}", v.clamp(0.0, 1.0));
            let trimmed = s.trim_end_matches('0').trim_end_matches('.');
            if trimmed.is_empty() {
                "0".to_string()
            } else {
                trimmed.to_string()
            }
        }
    }
}

/// One vessel row on the select screen: its `/sim` id, how many lights it has, and whether it's
/// armed for the party.
pub struct VesselRow {
    pub id: String,
    pub lights: usize,
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

/// Per-color-time editor — type a millisecond value (a quick path to the `color_ms` setting).
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
/// whole number (0 or higher), `Enter` applies it (clamped to the row's range), `Esc` returns to the
/// settings list. Lets you set a precise value the ←/→ stepping is too coarse or too slow to reach.
pub struct SettingInputModal {
    pub row: usize,
    pub text: String,
    pub area: Rect,
}

/// The save-profile prompt — type a name, `Enter` writes `<name>.yaml` (palette + all settings, but
/// not the armed vessels) to the profiles directory; `Esc` cancels.
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

/// The aggregate battery view across the armed vessels (average charge over the `count` that have a
/// battery; `fraction` is `None` when none do).
#[derive(Clone, Copy, Default)]
pub struct BatteryView {
    pub fraction: Option<f64>,
    pub count: usize,
}

pub struct App {
    pub screen: Screen,
    pub modal: Modal,
    pub should_quit: bool,
    /// Set when we asked the worker to reset lights on the way out, so `main` waits for the ack.
    pub pending_stop: bool,
    /// **Hide mode** — collapse the whole UI to a single status bar (party toggle + battery + refill)
    /// so a running party doesn't block the game. Toggled with `h`; `h`/`Esc` restores the full UI.
    pub hidden: bool,

    // ---- vessel screen ----
    pub vessels: Vec<VesselRow>,
    pub vsel: usize,
    pub discovering: bool,

    // ---- party screen ----
    pub colors: Vec<Rgb>,
    pub csel: usize,
    pub focus: Focus,
    pub partying: bool,
    /// The latest live frame from the worker (preview swatch + heartbeat), or `None` when stopped.
    pub live: Option<LiveFrame>,
    /// Aggregate battery across the armed vessels (updated whether or not a party is running).
    pub battery: BatteryView,
    /// Writes dispatched since the current party started, and how many are still in flight.
    pub writes: u64,
    pub inflight: usize,

    // ---- shared ----
    pub connected: bool,
    pub control: bool,
    pub label: String,
    /// Every display-affecting knob; edited live from the settings popup, adopted by a running party.
    pub settings: Settings,
    pub status: String,
    pub status_err: bool,

    // ---- hit-test rects recorded each render ----
    pub vessel_rects: Vec<(Rect, usize)>,
    pub color_rects: Vec<(Rect, usize)>,
    pub up_btns: Vec<(Rect, usize)>,
    pub down_btns: Vec<(Rect, usize)>,
    pub del_btns: Vec<(Rect, usize)>,
    pub add_btn: Rect,
    pub xkcd_btn: Rect,
    pub time_minus: Rect,
    pub time_plus: Rect,
    pub refill_btn: Rect,
    pub settings_btn: Rect,
    pub party_btn: Rect,
    pub back_btn: Rect,
    /// The "show" button on the hide-mode bar (restores the full UI).
    pub hide_show_btn: Rect,

    tx: UnboundedSender<ToWorker>,
}

/// A throttled live animation frame mirrored from the worker for the UI preview.
#[derive(Clone, Copy)]
pub struct LiveFrame {
    pub color: Rgb,
    pub color_segment: u64,
    pub anim_segment: u64,
    /// The goal actuation setpoint (0..1) this frame, or `None` when the animation is off.
    pub goal: Option<f64>,
}

/// The per-color time floor (ms) — fast enough to strobe, slow enough that the worker keeps up.
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
            screen: Screen::Vessels,
            modal: Modal::None,
            should_quit: false,
            pending_stop: false,
            hidden: false,
            vessels: Vec::new(),
            vsel: 0,
            discovering: true,
            colors,
            csel: 0,
            focus: Focus::Colors,
            partying: false,
            live: None,
            battery: BatteryView::default(),
            writes: 0,
            inflight: 0,
            connected: false,
            control: false,
            label,
            settings,
            status: "scanning for vessels\u{2026}".into(),
            status_err: false,
            vessel_rects: Vec::new(),
            color_rects: Vec::new(),
            up_btns: Vec::new(),
            down_btns: Vec::new(),
            del_btns: Vec::new(),
            add_btn: Rect::default(),
            xkcd_btn: Rect::default(),
            time_minus: Rect::default(),
            time_plus: Rect::default(),
            refill_btn: Rect::default(),
            settings_btn: Rect::default(),
            party_btn: Rect::default(),
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
            FromWorker::Catalog { vessels, health } => {
                self.discovering = false;
                self.connected = health.connected;
                self.control = health.control;
                self.merge_vessels(vessels);
                self.status = if self.vessels.is_empty() {
                    "no vessels found \u{2014} start a flight, then press r to rescan".into()
                } else {
                    format!(
                        "{} vessel(s) found \u{2014} space to arm, Enter to party",
                        self.vessels.len()
                    )
                };
                self.status_err = false;
            }
            FromWorker::Battery { fraction, count } => {
                self.battery = BatteryView { fraction, count };
            }
            FromWorker::Tick {
                color,
                color_segment,
                anim_segment,
                goal,
                targets,
                writes,
                inflight,
            } => {
                self.partying = true;
                self.live = Some(LiveFrame {
                    color,
                    color_segment,
                    anim_segment,
                    goal,
                });
                self.writes = writes;
                self.inflight = inflight;
                let goal_txt = fmt_goal_display(goal);
                self.status = format!(
                    "\u{1f389} PARTY \u{b7} {targets} light(s) \u{b7} color {color_segment} \u{b7} anim {anim_segment} \u{b7} goal {goal_txt}"
                );
                self.status_err = false;
            }
            FromWorker::Stopped { error } => {
                self.partying = false;
                self.live = None;
                self.writes = 0;
                self.inflight = 0;
                self.pending_stop = false;
                match error {
                    Some(e) => {
                        self.status = format!("stopped, but reset failed \u{2014} {e}");
                        self.status_err = true;
                    }
                    None => {
                        self.status = "stopped \u{2014} lights reset to white".into();
                        self.status_err = false;
                    }
                }
            }
            FromWorker::RefillDone { error } => match error {
                Some(e) => {
                    self.status = format!("battery refill failed \u{2014} {e}");
                    self.status_err = true;
                }
                None => {
                    self.status = "battery refilled \u{26a1}".into();
                    self.status_err = false;
                }
            },
            FromWorker::Refused(why) => {
                self.partying = false;
                self.live = None;
                self.status = format!("can't party \u{2014} {why}");
                self.status_err = true;
            }
        }
    }

    /// Folds a fresh catalog into the vessel rows, preserving each vessel's armed state by id.
    fn merge_vessels(&mut self, cat: Vec<VesselLights>) {
        let armed: Vec<String> = self
            .vessels
            .iter()
            .filter(|v| v.selected)
            .map(|v| v.id.clone())
            .collect();
        self.vessels = cat
            .into_iter()
            .map(|v| VesselRow {
                selected: armed.contains(&v.id),
                lights: v.light_count(),
                id: v.id,
            })
            .collect();
        if self.vsel >= self.vessels.len() {
            self.vsel = self.vessels.len().saturating_sub(1);
        }
    }

    pub fn selected_vessel_ids(&self) -> Vec<String> {
        self.vessels
            .iter()
            .filter(|v| v.selected)
            .map(|v| v.id.clone())
            .collect()
    }

    fn plan(&self) -> Plan {
        Plan::new(self.colors.clone(), self.settings.color_ms, self.settings.anim_ms)
            .with_steps(self.settings.steps)
            .with_staggers(self.settings.color_stagger_ms, self.settings.anim_stagger_ms)
            .with_brightness(
                self.settings.bright_min / BRIGHT_SCALE,
                self.settings.bright_max / BRIGHT_SCALE,
                self.settings.bright_ms,
                self.settings.bright_steps,
            )
            .with_anim_range(
                self.settings.anim_min / ANIM_SCALE,
                self.settings.anim_max / ANIM_SCALE,
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
                Screen::Vessels => self.on_key_vessels(key),
                Screen::Party => self.on_key_party(key),
            },
        }
    }

    /// Hide-mode keys: the screen is one status bar, so only the party toggle / refill / restore (and
    /// quit) are live.
    fn on_key_hidden(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Esc | KeyCode::Char('h') => self.hidden = false,
            KeyCode::Enter | KeyCode::Char('p') | KeyCode::Char('P') => self.toggle_party(),
            KeyCode::Char('g') => self.refill_battery(),
            KeyCode::Char('q') => self.request_quit(),
            _ => {}
        }
    }

    /// `q` (and Esc on the vessel screen) asks before leaving — see [`Self::on_key_confirm_quit`].
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

    fn on_key_vessels(&mut self, key: KeyEvent) {
        match key.code {
            KeyCode::Char('q') | KeyCode::Esc => self.request_quit(),
            KeyCode::Up | KeyCode::Char('k') => self.move_vsel(-1),
            KeyCode::Down | KeyCode::Char('j') => self.move_vsel(1),
            KeyCode::Char(' ') => self.toggle_vessel(self.vsel),
            KeyCode::Char('a') => self.toggle_all_vessels(),
            KeyCode::Char('r') => self.rescan(),
            KeyCode::Enter | KeyCode::Char('p') => self.go_party(),
            _ => {}
        }
    }

    fn on_key_party(&mut self, key: KeyEvent) {
        // Screen-wide actions from anywhere on the party screen.
        match key.code {
            KeyCode::Enter | KeyCode::Char('P') => {
                self.toggle_party();
                return;
            }
            KeyCode::Char('q') => {
                self.request_quit();
                return;
            }
            KeyCode::Esc | KeyCode::Char('b') => {
                self.back_to_vessels();
                return;
            }
            KeyCode::Char('s') => {
                self.open_settings();
                return;
            }
            KeyCode::Char('g') => {
                self.refill_battery();
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

    /// Handles the manual numeric-entry popup for a settings row: digits only, `Enter` applies (clamped
    /// to the row's range) and returns to the settings list, `Esc` returns without changing anything.
    fn on_key_setting_input(&mut self, key: KeyEvent) {
        let row = match &self.modal {
            Modal::SettingInput(m) => m.row,
            _ => return,
        };
        match key.code {
            KeyCode::Esc => self.reopen_settings(row),
            KeyCode::Enter => {
                let text = match &self.modal {
                    Modal::SettingInput(m) => m.text.trim().to_string(),
                    _ => return,
                };
                match text.parse::<u64>() {
                    Ok(v) => {
                        self.settings.set_from_input(row, v);
                        self.republish_plan();
                        self.reopen_settings(row);
                    }
                    Err(_) => {
                        self.status = "enter a whole number (0 or higher)".into();
                        self.status_err = true;
                    }
                }
            }
            KeyCode::Backspace => {
                if let Modal::SettingInput(m) = &mut self.modal {
                    m.text.pop();
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

    // ---- vessel actions ----------------------------------------------------------------------

    fn move_vsel(&mut self, d: i32) {
        let n = self.vessels.len();
        if n == 0 {
            return;
        }
        self.vsel = (self.vsel as i32 + d).rem_euclid(n as i32) as usize;
    }

    fn toggle_vessel(&mut self, i: usize) {
        if let Some(v) = self.vessels.get_mut(i) {
            v.selected = !v.selected;
        }
    }

    fn toggle_all_vessels(&mut self) {
        let all_on = self.vessels.iter().all(|v| v.selected);
        for v in &mut self.vessels {
            v.selected = !all_on;
        }
    }

    fn rescan(&mut self) {
        self.discovering = true;
        self.status = "rescanning\u{2026}".into();
        self.status_err = false;
        let _ = self.tx.send(ToWorker::Discover);
    }

    fn go_party(&mut self) {
        let armed = self.selected_vessel_ids();
        if armed.is_empty() {
            self.status = "arm at least one vessel first (space)".into();
            self.status_err = true;
            return;
        }
        self.screen = Screen::Party;
        self.focus = Focus::Colors;
        // Tell the worker which vessels to watch so the battery meter populates before a party starts.
        let _ = self.tx.send(ToWorker::Watch { vessels: armed });
        if self.colors.is_empty() {
            self.status = "build a palette: a = RGB/hex \u{b7} f = XKCD picker".into();
            self.status_err = false;
        }
    }

    fn back_to_vessels(&mut self) {
        if self.partying {
            self.stop_party();
        }
        self.screen = Screen::Vessels;
    }

    // ---- party actions -----------------------------------------------------------------------

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

    fn refill_battery(&mut self) {
        let _ = self.tx.send(ToWorker::RefillBattery);
        self.status = "refilling battery\u{2026}".into();
        self.status_err = false;
    }

    fn toggle_party(&mut self) {
        if self.partying {
            self.stop_party();
        } else {
            self.start_party();
        }
    }

    fn start_party(&mut self) {
        let vessels = self.selected_vessel_ids();
        if vessels.is_empty() {
            self.status = "no vessels armed \u{2014} go back (b) and arm one".into();
            self.status_err = true;
            return;
        }
        if self.colors.is_empty() {
            self.status = "add at least one color (a or f)".into();
            self.status_err = true;
            return;
        }
        self.partying = true; // optimistic; the first Tick confirms
        self.status = "LET'S PARTY! \u{1f389}".into();
        self.status_err = false;
        let _ = self.tx.send(ToWorker::Start {
            vessels,
            plan: self.plan(),
            hz: self.settings.hz,
        });
    }

    fn stop_party(&mut self) {
        self.partying = false;
        self.live = None;
        let _ = self.tx.send(ToWorker::Stop);
        self.status = "stopping \u{2014} resetting lights to white\u{2026}".into();
        self.status_err = false;
    }

    /// Pushes the current palette/timing to a running party so edits take effect without a restart.
    fn republish_plan(&self) {
        if self.partying {
            let _ = self.tx.send(ToWorker::Update {
                plan: self.plan(),
                hz: self.settings.hz,
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

    /// Serializes the current palette + settings (not the armed vessels) to `<name>.yaml` and reports
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
        if self.partying {
            // Reset the lights on the way out; `main` waits briefly for the Stopped ack.
            let _ = self.tx.send(ToWorker::Stop);
            self.pending_stop = true;
            self.partying = false;
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
                Screen::Vessels => self.on_mouse_vessels(m),
                Screen::Party => self.on_mouse_party(m),
            },
        }
    }

    fn on_mouse_vessels(&mut self, m: MouseEvent) {
        match m.kind {
            MouseEventKind::ScrollUp => self.move_vsel(-1),
            MouseEventKind::ScrollDown => self.move_vsel(1),
            MouseEventKind::Down(MouseButton::Left) => {
                let pos = Position {
                    x: m.column,
                    y: m.row,
                };
                if let Some(&(_, i)) = self.vessel_rects.iter().find(|(r, _)| r.contains(pos)) {
                    self.vsel = i;
                    self.toggle_vessel(i);
                }
            }
            _ => {}
        }
    }

    fn on_mouse_party(&mut self, m: MouseEvent) {
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
                } else if self.refill_btn.contains(pos) {
                    self.refill_battery();
                } else if self.settings_btn.contains(pos) {
                    self.open_settings();
                } else if self.party_btn.contains(pos) {
                    self.toggle_party();
                } else if self.back_btn.contains(pos) {
                    self.back_to_vessels();
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

    /// Hide-mode clicks: only the three bar buttons (party toggle / refill / show) are live.
    fn on_mouse_hidden(&mut self, m: MouseEvent) {
        if let MouseEventKind::Down(MouseButton::Left) = m.kind {
            let pos = Position {
                x: m.column,
                y: m.row,
            };
            if self.party_btn.contains(pos) {
                self.toggle_party();
            } else if self.refill_btn.contains(pos) {
                self.refill_battery();
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
    use crate::source::Health;

    fn app() -> App {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        App::new(tx, "mock".into(), Settings::default(), Vec::new())
    }

    fn catalog() -> Vec<VesselLights> {
        vec![
            VesselLights {
                id: "Hunter".into(),
                color_paths: vec!["a".into(), "b".into()],
                goal_paths: vec!["g".into()],
            },
            VesselLights {
                id: "Polaris".into(),
                color_paths: vec!["c".into()],
                goal_paths: vec![],
            },
        ]
    }

    fn catalog_msg() -> FromWorker {
        FromWorker::Catalog {
            vessels: catalog(),
            health: Health::default(),
        }
    }

    #[test]
    fn selection_survives_a_rescan() {
        let mut a = app();
        a.apply(catalog_msg());
        a.toggle_vessel(0);
        assert_eq!(a.selected_vessel_ids(), vec!["Hunter".to_string()]);
        a.apply(catalog_msg());
        assert_eq!(a.selected_vessel_ids(), vec!["Hunter".to_string()]);
    }

    #[test]
    fn cannot_party_without_arming_a_vessel() {
        let mut a = app();
        a.apply(catalog_msg());
        a.go_party();
        assert_eq!(a.screen, Screen::Vessels); // refused, with a hint
        assert!(a.status_err);
        a.toggle_vessel(0);
        a.go_party();
        assert_eq!(a.screen, Screen::Party);
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
    fn color_time_has_a_floor() {
        let mut a = app();
        a.settings.color_ms = 100;
        a.nudge_time(-1); // -100 -> would be 0, clamped to the floor
        assert_eq!(a.settings.color_ms, MIN_PER_MS);
    }

    #[test]
    fn settings_adjust_is_independent_for_color_and_anim() {
        let mut s = Settings::default();
        // Color and animation timing move independently.
        let (c0, a0) = (s.color_ms, s.anim_ms);
        s.adjust(2, 1, false); // color time +100
        assert_eq!(s.color_ms, c0 + 100);
        assert_eq!(s.anim_ms, a0); // animation untouched
        s.adjust(3, -1, true); // anim time -1000 (coarse)
        assert_eq!(s.anim_ms, a0 - 1000);
        // Independent staggers, too.
        s.adjust(4, 1, false); // color stagger +10
        assert_eq!(s.color_stagger_ms, 10.0);
        assert_eq!(s.anim_stagger_ms, 0.0);
        // Frame rate clamps at 240.
        for _ in 0..100 {
            s.adjust(0, 1, true);
        }
        assert_eq!(s.hz, 240.0);
    }

    #[test]
    fn enter_on_a_settings_row_opens_manual_input_and_applies_it() {
        let mut a = app();
        a.screen = Screen::Party;
        a.open_settings();
        // Move to "color time" (row 2) and open the manual-entry popup.
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
        s.set_from_input(0, 0); // frame rate can't be 0
        assert_eq!(s.hz, 1.0);
        s.set_from_input(0, 99_999); // ...nor absurdly high
        assert_eq!(s.hz, 240.0);
        s.set_from_input(2, 0); // duration floor
        assert_eq!(s.color_ms, 50);
        s.set_from_input(6, 999_999); // brightness scale ceiling
        assert_eq!(s.bright_min, BRIGHT_SCALE);
        s.set_from_input(1, 7); // fade steps takes the value verbatim
        assert_eq!(s.steps, 7);
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
    fn settings_adjust_covers_the_brightness_rows() {
        let mut s = Settings::default();
        assert_eq!(s.bright_min, BRIGHT_SCALE); // 10000 = off/full
        assert_eq!(s.bright_max, BRIGHT_SCALE);
        s.adjust(6, -1, false); // bright min -1 (regular step)
        assert_eq!(s.bright_min, 9999.0);
        s.adjust(7, -1, true); // bright max -20 (coarse step)
        assert_eq!(s.bright_max, 9980.0);
        s.adjust(8, 1, false); // bright time +100
        assert_eq!(s.bright_ms, 700);
        s.adjust(9, 1, false); // bright steps +1
        assert_eq!(s.bright_steps, 1);
        // Floor holds at 0 (coarse step of 20, plenty of iterations to drive past it).
        for _ in 0..1000 {
            s.adjust(6, -1, true);
        }
        assert_eq!(s.bright_min, 0.0);
        // Ceiling holds at the top of the scale.
        for _ in 0..1000 {
            s.adjust(7, 1, true);
        }
        assert_eq!(s.bright_max, BRIGHT_SCALE);
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
    fn battery_reply_updates_the_meter() {
        let mut a = app();
        a.apply(FromWorker::Battery {
            fraction: Some(0.5),
            count: 2,
        });
        assert_eq!(a.battery.count, 2);
        assert_eq!(a.battery.fraction, Some(0.5));
    }

    fn press(a: &mut App, c: char) {
        a.on_key(KeyEvent::new(KeyCode::Char(c), KeyModifiers::NONE));
    }

    #[test]
    fn h_toggles_hide_mode_on_the_party_screen() {
        let mut a = app();
        a.screen = Screen::Party;
        assert!(!a.hidden);
        press(&mut a, 'h'); // hide
        assert!(a.hidden);
        // While hidden, normal party keys don't fire (e.g. 'a' must not open the add-color modal).
        press(&mut a, 'a');
        assert!(matches!(a.modal, Modal::None));
        press(&mut a, 'h'); // show again
        assert!(!a.hidden);
    }

    #[test]
    fn hide_mode_still_toggles_the_party() {
        let mut a = app();
        a.apply(catalog_msg());
        a.toggle_vessel(0);
        a.go_party();
        a.add_color(Rgb::from_u8(255, 0, 0));
        a.hidden = true;
        assert!(!a.partying);
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
        assert!(a.partying); // optimistic start fired even from the hidden bar
    }

    #[test]
    fn w_saves_a_profile_round_tripping_palette_and_settings() {
        // A path-like name is written verbatim (no dependence on the global `$DANCY_PROFILE_DIR`).
        let dir = std::env::temp_dir().join(format!("dancy_app_prof_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let target = dir.join("myshow.yaml");
        let name = target.to_string_lossy().into_owned();

        let mut a = app();
        a.screen = Screen::Party;
        a.add_color(Rgb::from_u8(10, 20, 30));
        a.settings.color_ms = 777;
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
    fn q_confirmation_works_from_the_party_screen_too() {
        let mut a = app();
        a.screen = Screen::Party;
        press(&mut a, 'q');
        assert!(matches!(a.modal, Modal::ConfirmQuit(_)));
        assert!(!a.should_quit);
        press(&mut a, 'y');
        assert!(a.should_quit);
    }

    #[test]
    fn empty_profile_name_is_rejected() {
        let mut a = app();
        a.screen = Screen::Party;
        press(&mut a, 'w');
        a.on_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE)); // no name typed
        assert!(matches!(a.modal, Modal::SaveProfile(_))); // stays open
        assert!(a.status_err);
    }
}
