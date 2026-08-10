//! Hybrid playback orchestration. The track and cue schedule start frozen in one shared-clock
//! group; only after both registry players are visible does one rate write roll the take.

use std::fmt;
use std::thread;
use std::time::Duration;

use crate::compiler::CompiledTake;
use crate::transport::{Transport, TransportError};

#[derive(Debug, Clone, Copy)]
pub struct PlaybackOptions {
    pub activation_attempts: usize,
    pub activation_delay: Duration,
}

impl Default for PlaybackOptions {
    fn default() -> Self {
        Self {
            activation_attempts: 40,
            activation_delay: Duration::from_millis(50),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlaybackError {
    pub stage: String,
    pub source: TransportError,
}

impl PlaybackError {
    fn new(stage: impl Into<String>, source: TransportError) -> Self {
        Self {
            stage: stage.into(),
            source,
        }
    }
}

impl fmt::Display for PlaybackError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.stage, self.source)
    }
}

impl std::error::Error for PlaybackError {}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct LiveStatus {
    pub connected: bool,
    pub label: String,
    pub owned: bool,
    pub playback: String,
    pub playback_state: String,
    pub position_ms: f64,
    pub duration_ms: f64,
    pub shot: String,
    pub rate: f64,
    pub looping: bool,
    pub camera_last_error: String,
    pub cue_state: String,
    pub cue_last_error: String,
    pub cue_dropped: u64,
    pub gate_failure: Option<String>,
}

pub struct PlaybackController {
    options: PlaybackOptions,
    active_cue: Option<String>,
    active_take: Option<CompiledTake>,
}

impl PlaybackController {
    pub fn new(options: PlaybackOptions) -> Self {
        Self {
            options,
            active_cue: None,
            active_take: None,
        }
    }

    pub fn play(
        &mut self,
        transport: &mut dyn Transport,
        take: &CompiledTake,
        rate: f64,
    ) -> Result<(), PlaybackError> {
        if !(0.0..=100.0).contains(&rate) || !rate.is_finite() {
            return Err(PlaybackError::new(
                "rate",
                TransportError::new("EINVAL", "playback rate must be finite and in [0, 100]"),
            ));
        }
        self.preflight(transport)?;
        let result = self.start(transport, take, rate);
        if result.is_err() {
            self.emergency_release(transport);
        }
        result
    }

    fn preflight(&self, transport: &mut dyn Transport) -> Result<(), PlaybackError> {
        transport
            .read_text("camera/info")
            .map_err(|error| PlaybackError::new("camera gate unavailable", error))?;
        transport
            .read_text("ctl/schedules/help")
            .map_err(|error| PlaybackError::new("schedule gate unavailable", error))?;
        Ok(())
    }

    fn start(
        &mut self,
        transport: &mut dyn Transport,
        take: &CompiledTake,
        rate: f64,
    ) -> Result<(), PlaybackError> {
        transport
            .write_text("camera/enabled", "1")
            .map_err(|error| PlaybackError::new("take camera ownership", error))?;
        transport
            .upload_text(
                &format!("camera/track/{}", take.track_name),
                &take.track_json,
            )
            .map_err(|error| PlaybackError::new("upload native track", error))?;
        let looping = serde_json::from_str::<serde_json::Value>(&take.track_json)
            .ok()
            .and_then(|track| track.get("loop").and_then(serde_json::Value::as_bool))
            .unwrap_or(false);
        let play = format!(
            "{} at 0 rate 0 loop {} group {}",
            take.track_name,
            u8::from(looping),
            take.group_id
        );
        transport
            .write_text("camera/play", &play)
            .map_err(|error| PlaybackError::new("start frozen native track", error))?;
        transport
            .upload_text("ctl/timed_batch", &take.timed_batch)
            .map_err(|error| PlaybackError::new("commit frozen cue schedule", error))?;

        let mut visible = false;
        for _ in 0..self.options.activation_attempts.max(1) {
            let camera = transport.read_text("ctl/schedules/camera/state").is_ok();
            let cues = transport
                .read_text(&format!("ctl/schedules/{}/state", take.cue_id))
                .is_ok();
            if camera && cues {
                visible = true;
                break;
            }
            if !self.options.activation_delay.is_zero() {
                thread::sleep(self.options.activation_delay);
            }
        }
        if !visible {
            let detail = transport
                .read_text("camera/last_error")
                .unwrap_or_else(|_| "players did not activate".into());
            return Err(PlaybackError::new(
                "wait for shared-clock players",
                TransportError::new("ETIMEDOUT", detail),
            ));
        }
        transport
            .write_text("ctl/schedules/camera/rate", &number(rate))
            .map_err(|error| PlaybackError::new("roll shared clock", error))?;
        self.active_cue = Some(take.cue_id.clone());
        self.active_take = Some(take.clone());
        Ok(())
    }

    pub fn pause(
        &mut self,
        transport: &mut dyn Transport,
        paused: bool,
    ) -> Result<(), PlaybackError> {
        transport
            .write_text("ctl/schedules/camera/pause", if paused { "1" } else { "0" })
            .map_err(|error| PlaybackError::new(if paused { "pause" } else { "resume" }, error))
    }

    pub fn set_rate(
        &mut self,
        transport: &mut dyn Transport,
        rate: f64,
    ) -> Result<(), PlaybackError> {
        if !(0.0..=100.0).contains(&rate) || !rate.is_finite() {
            return Err(PlaybackError::new(
                "rate",
                TransportError::new("EINVAL", "rate must be finite and in [0, 100]"),
            ));
        }
        transport
            .write_text("ctl/schedules/camera/rate", &number(rate))
            .map_err(|error| PlaybackError::new("set rate", error))
    }

    pub fn set_loop(
        &mut self,
        transport: &mut dyn Transport,
        looping: bool,
    ) -> Result<(), PlaybackError> {
        let Some(mut take) = self.active_take.clone() else {
            return transport
                .write_text("ctl/schedules/camera/loop", if looping { "1" } else { "0" })
                .map_err(|error| PlaybackError::new("set loop", error));
        };

        // A non-looping sidecar contains the final camera.stop + eased release. Merely flipping the
        // group clock would still fire them at the first wrap, so replace that player while the one
        // shared clock is frozen. Removing the cue member does not remove the group: camera remains.
        take.timed_batch = sidecar_for_loop(&take, looping);
        let result = (|| {
            transport
                .write_text("ctl/schedules/camera/pause", "1")
                .map_err(|error| PlaybackError::new("freeze while changing loop", error))?;
            transport
                .write_text(&format!("ctl/schedules/{}/remove", take.cue_id), "1")
                .map_err(|error| PlaybackError::new("replace loop cue schedule", error))?;
            transport
                .upload_text("ctl/timed_batch", &take.timed_batch)
                .map_err(|error| PlaybackError::new("commit loop cue schedule", error))?;
            transport
                .write_text("ctl/schedules/camera/loop", if looping { "1" } else { "0" })
                .map_err(|error| PlaybackError::new("set loop", error))?;
            transport
                .write_text("ctl/schedules/camera/pause", "0")
                .map_err(|error| PlaybackError::new("resume after changing loop", error))?;
            Ok(())
        })();
        if result.is_err() {
            self.emergency_release(transport);
        } else {
            self.active_take = Some(take);
        }
        result
    }

    pub fn scrub_seconds(
        &mut self,
        transport: &mut dyn Transport,
        seconds: f64,
    ) -> Result<(), PlaybackError> {
        if !seconds.is_finite() || seconds < 0.0 {
            return Err(PlaybackError::new(
                "scrub",
                TransportError::new("EINVAL", "scrub time must be finite and >= 0"),
            ));
        }
        transport
            .write_text("ctl/schedules/camera/scrub", &number(seconds * 1000.0))
            .map_err(|error| PlaybackError::new("scrub", error))
    }

    pub fn stop_and_release(&mut self, transport: &mut dyn Transport) -> Result<(), PlaybackError> {
        let mut first = None;
        if let Err(error) = transport.write_text("camera/stop", "1") {
            first.get_or_insert_with(|| PlaybackError::new("stop track", error));
        }
        let cue_path = self
            .active_cue
            .as_deref()
            .map(|cue| format!("ctl/schedules/{cue}/stop"))
            .unwrap_or_else(|| "ctl/schedules/camera/stop".into());
        if let Err(error) = transport.write_text(&cue_path, "1") {
            first.get_or_insert_with(|| PlaybackError::new("stop cues", error));
        }
        if let Err(error) = transport.write_text("camera/enabled", "0") {
            first.get_or_insert_with(|| PlaybackError::new("eased release", error));
        }
        self.active_cue = None;
        self.active_take = None;
        if let Some(error) = first {
            self.emergency_release(transport);
            Err(error)
        } else {
            Ok(())
        }
    }

    pub fn emergency_release(&mut self, transport: &mut dyn Transport) {
        let _ = transport.write_text("camera/release", "1");
        self.active_cue = None;
        self.active_take = None;
    }

    pub fn poll(&mut self, transport: &mut dyn Transport) -> LiveStatus {
        let label = transport.label();
        if transport.read_text("time/ut").is_err() {
            return LiveStatus {
                label,
                gate_failure: Some("/sim or HTTP endpoint is unreachable".into()),
                ..LiveStatus::default()
            };
        }
        let camera_info = transport.read_text("camera/info");
        let schedule_count = transport.read_text("ctl/schedules/count");
        let gate_failure = match (&camera_info, &schedule_count) {
            (Err(error), _) => Some(format!("camera gate: {error}")),
            (_, Err(error)) => Some(format!("schedule gate: {error}")),
            _ => None,
        };
        let camera_status = transport.read_text("camera/status").unwrap_or_default();
        let playback = transport.read_text("camera/playback").unwrap_or_default();
        let camera_last_error = transport
            .read_text("camera/last_error")
            .unwrap_or_else(|error| error.to_string());
        let parsed = parse_playback(&playback);
        let (cue_state, cue_last_error, cue_dropped) = if let Some(cue) = &self.active_cue {
            (
                transport
                    .read_text(&format!("ctl/schedules/{cue}/state"))
                    .unwrap_or_default(),
                transport
                    .read_text(&format!("ctl/schedules/{cue}/last_error"))
                    .unwrap_or_default(),
                transport
                    .read_text(&format!("ctl/schedules/{cue}/dropped"))
                    .ok()
                    .and_then(|value| value.parse().ok())
                    .unwrap_or(0),
            )
        } else {
            (String::new(), String::new(), 0)
        };
        LiveStatus {
            connected: true,
            label,
            owned: camera_status.lines().any(|line| line == "owned 1"),
            playback,
            playback_state: parsed.state,
            position_ms: parsed.position_ms,
            duration_ms: parsed.duration_ms,
            shot: parsed.shot,
            rate: parsed.rate,
            looping: parsed.looping,
            camera_last_error,
            cue_state,
            cue_last_error,
            cue_dropped,
            gate_failure,
        }
    }
}

#[derive(Default)]
struct ParsedPlayback {
    state: String,
    position_ms: f64,
    duration_ms: f64,
    shot: String,
    rate: f64,
    looping: bool,
}

fn parse_playback(value: &str) -> ParsedPlayback {
    let tokens = value.split_whitespace().collect::<Vec<_>>();
    if tokens.len() < 7 {
        return ParsedPlayback::default();
    }
    ParsedPlayback {
        state: tokens[0].into(),
        position_ms: tokens[1].parse().unwrap_or_default(),
        duration_ms: tokens[2].parse().unwrap_or_default(),
        shot: tokens[3].into(),
        rate: tokens[5].parse().unwrap_or_default(),
        looping: tokens[6] == "1",
    }
}

fn number(value: f64) -> String {
    let mut out = format!("{value:.6}");
    while out.ends_with('0') {
        out.pop();
    }
    if out.ends_with('.') {
        out.pop();
    }
    out
}

fn sidecar_for_loop(take: &CompiledTake, looping: bool) -> String {
    let stop = format!("{} camera/stop 1", number(take.duration_s * 1000.0));
    let release = format!("{} camera/enabled 0", number(take.duration_s * 1000.0));
    let mut lines = Vec::new();
    let mut has_stop = false;
    let mut has_release = false;
    for line in take.timed_batch.lines() {
        if line.starts_with("@loop ") {
            lines.push(format!("@loop {}", u8::from(looping)));
        } else if line == stop {
            has_stop = true;
            if !looping {
                lines.push(line.into());
            }
        } else if line == release {
            has_release = true;
            if !looping {
                lines.push(line.into());
            }
        } else if line == "commit" {
            if !looping {
                if !has_stop {
                    lines.push(stop.clone());
                }
                if !has_release {
                    lines.push(release.clone());
                }
            }
            lines.push("commit".into());
        } else {
            lines.push(line.into());
        }
    }
    let mut output = lines.join("\n");
    output.push('\n');
    output
}

#[cfg(test)]
mod tests {
    use std::collections::{HashMap, VecDeque};

    use super::*;

    #[derive(Default)]
    struct FakeTransport {
        writes: Vec<(String, String)>,
        reads: HashMap<String, VecDeque<Result<String, TransportError>>>,
        fail_write: Option<String>,
    }

    impl FakeTransport {
        fn ready() -> Self {
            let mut value = Self::default();
            for (path, response) in [
                ("camera/info", "enabled=1"),
                ("ctl/schedules/help", "help"),
                ("ctl/schedules/camera/state", "running"),
                ("ctl/schedules/cues/state", "running"),
            ] {
                value
                    .reads
                    .insert(path.into(), VecDeque::from([Ok(response.into())]));
            }
            value
        }
    }

    impl Transport for FakeTransport {
        fn read_text(&mut self, path: &str) -> Result<String, TransportError> {
            self.reads
                .get_mut(path)
                .and_then(VecDeque::pop_front)
                .unwrap_or_else(|| Err(TransportError::new("ENOENT", path)))
        }

        fn write_text(&mut self, path: &str, value: &str) -> Result<(), TransportError> {
            self.writes.push((path.into(), value.into()));
            if self.fail_write.as_deref() == Some(path) {
                Err(TransportError::new("EIO", "injected"))
            } else {
                Ok(())
            }
        }

        fn upload_text(&mut self, path: &str, value: &str) -> Result<(), TransportError> {
            self.write_text(path, value)
        }

        fn discover_vessels(&mut self) -> Result<Vec<String>, TransportError> {
            Ok(Vec::new())
        }

        fn discover_bodies(&mut self) -> Result<Vec<String>, TransportError> {
            Ok(Vec::new())
        }

        fn label(&self) -> String {
            "fake".into()
        }
    }

    fn take() -> CompiledTake {
        CompiledTake {
            track_name: "track".into(),
            track_json: "{}\n".into(),
            cue_id: "cues".into(),
            group_id: "group".into(),
            timed_batch: "@id cues\n@loop 0\ncommit\n".into(),
            duration_s: 1.0,
            warnings: Vec::new(),
        }
    }

    fn controller() -> PlaybackController {
        PlaybackController::new(PlaybackOptions {
            activation_attempts: 2,
            activation_delay: Duration::ZERO,
        })
    }

    #[test]
    fn starts_both_players_frozen_then_rolls_shared_clock() {
        let mut transport = FakeTransport::ready();
        controller().play(&mut transport, &take(), 1.0).unwrap();
        let paths = transport
            .writes
            .iter()
            .map(|(path, _)| path.as_str())
            .collect::<Vec<_>>();
        assert_eq!(
            paths,
            [
                "camera/enabled",
                "camera/track/track",
                "camera/play",
                "ctl/timed_batch",
                "ctl/schedules/camera/rate"
            ]
        );
        assert!(transport.writes[2].1.contains("rate 0"));
        assert!(transport.writes[2].1.contains("group group"));
        assert_eq!(transport.writes[4].1, "1");
    }

    #[test]
    fn pause_scrub_rate_and_loop_target_the_shared_camera_clock() {
        let mut transport = FakeTransport::default();
        let mut controller = controller();
        controller.pause(&mut transport, true).unwrap();
        controller.pause(&mut transport, false).unwrap();
        controller.scrub_seconds(&mut transport, 2.5).unwrap();
        controller.set_rate(&mut transport, 0.25).unwrap();
        controller.set_loop(&mut transport, true).unwrap();
        assert_eq!(
            transport.writes,
            [
                ("ctl/schedules/camera/pause".into(), "1".into()),
                ("ctl/schedules/camera/pause".into(), "0".into()),
                ("ctl/schedules/camera/scrub".into(), "2500".into()),
                ("ctl/schedules/camera/rate".into(), "0.25".into()),
                ("ctl/schedules/camera/loop".into(), "1".into()),
            ]
        );
    }

    #[test]
    fn live_loop_replaces_final_release_cues_while_the_group_is_frozen() {
        let mut transport = FakeTransport::ready();
        let mut controller = controller();
        controller.play(&mut transport, &take(), 1.0).unwrap();
        transport.writes.clear();

        controller.set_loop(&mut transport, true).unwrap();
        assert_eq!(
            transport
                .writes
                .iter()
                .map(|(path, _)| path.as_str())
                .collect::<Vec<_>>(),
            [
                "ctl/schedules/camera/pause",
                "ctl/schedules/cues/remove",
                "ctl/timed_batch",
                "ctl/schedules/camera/loop",
                "ctl/schedules/camera/pause",
            ]
        );
        assert!(transport.writes[2].1.contains("@loop 1"));
        assert!(!transport.writes[2].1.contains("camera/enabled 0"));

        transport.writes.clear();
        controller.set_loop(&mut transport, false).unwrap();
        assert!(transport.writes[2].1.contains("@loop 0"));
        assert!(transport.writes[2].1.contains("1000 camera/stop 1"));
        assert!(transport.writes[2].1.contains("1000 camera/enabled 0"));
    }

    #[test]
    fn startup_failure_unconditionally_hard_releases() {
        let mut transport = FakeTransport::ready();
        transport.fail_write = Some("ctl/timed_batch".into());
        let error = controller().play(&mut transport, &take(), 1.0).unwrap_err();
        assert_eq!(error.stage, "commit frozen cue schedule");
        assert_eq!(transport.writes.last().unwrap().0, "camera/release");
    }

    #[test]
    fn activation_timeout_reports_camera_error_and_releases() {
        let mut transport = FakeTransport::ready();
        transport.reads.remove("ctl/schedules/camera/state");
        transport.reads.insert(
            "camera/last_error".into(),
            VecDeque::from([Ok("track: bad".into())]),
        );
        let error = controller().play(&mut transport, &take(), 1.0).unwrap_err();
        assert_eq!(error.source.code, "ETIMEDOUT");
        assert!(error.source.message.contains("bad"));
        assert_eq!(transport.writes.last().unwrap().0, "camera/release");
    }

    #[test]
    fn stop_uses_eased_release_and_failure_falls_back_to_hard_release() {
        let mut transport = FakeTransport::ready();
        let mut controller = controller();
        controller.play(&mut transport, &take(), 1.0).unwrap();
        controller.stop_and_release(&mut transport).unwrap();
        assert!(transport.writes.ends_with(&[
            ("camera/stop".into(), "1".into()),
            ("ctl/schedules/cues/stop".into(), "1".into()),
            ("camera/enabled".into(), "0".into()),
        ]));

        transport.fail_write = Some("camera/enabled".into());
        assert!(controller.stop_and_release(&mut transport).is_err());
        assert_eq!(transport.writes.last().unwrap().0, "camera/release");
    }
}
