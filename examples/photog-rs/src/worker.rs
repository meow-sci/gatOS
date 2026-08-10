//! One background worker owns the live transport. The terminal thread only edits local state and
//! sends small commands, so a slow 9p or HTTP operation cannot stall rendering or input.

use std::sync::mpsc::{Receiver, RecvTimeoutError, Sender};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use photog::{CompiledTake, LiveStatus, PlaybackController, PlaybackOptions, Transport};

#[derive(Debug)]
pub enum Request {
    RefreshTargets,
    Play { take: CompiledTake, rate: f64 },
    Pause(bool),
    Rate(f64),
    Scrub(f64),
    Loop(bool),
    Stop,
    EmergencyRelease,
}

#[derive(Debug)]
pub enum Update {
    Live(LiveStatus),
    Targets {
        vessels: Vec<String>,
        bodies: Vec<String>,
    },
    Done(Result<String, String>),
}

pub fn spawn_worker(
    mut transport: Box<dyn Transport>,
    interval: Duration,
    rx: Receiver<Request>,
    tx: Sender<Update>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        let mut player = PlaybackController::new(PlaybackOptions::default());
        send_targets(&mut *transport, &tx);
        loop {
            if tx.send(Update::Live(player.poll(&mut *transport))).is_err() {
                let _ = player.stop_and_release(&mut *transport);
                return;
            }
            match rx.recv_timeout(interval) {
                Ok(request) => {
                    let done = handle(request, &mut player, &mut *transport, &tx);
                    if tx.send(Update::Done(done)).is_err() {
                        let _ = player.stop_and_release(&mut *transport);
                        return;
                    }
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => {
                    let _ = player.stop_and_release(&mut *transport);
                    return;
                }
            }
        }
    })
}

fn handle(
    request: Request,
    player: &mut PlaybackController,
    transport: &mut dyn Transport,
    tx: &Sender<Update>,
) -> Result<String, String> {
    match request {
        Request::RefreshTargets => {
            send_targets(transport, tx);
            Ok("targets refreshed".into())
        }
        Request::Play { take, rate } => {
            let warning = take.warnings.join("; ");
            player
                .play(transport, &take, rate)
                .map(|()| {
                    let head = format!("rolling '{}' ({:.2}s)", take.track_name, take.duration_s);
                    if warning.is_empty() {
                        head
                    } else {
                        format!("{head} · warning: {warning}")
                    }
                })
                .map_err(|error| error.to_string())
        }
        Request::Pause(paused) => player
            .pause(transport, paused)
            .map(|()| {
                if paused {
                    "paused".into()
                } else {
                    "resumed".into()
                }
            })
            .map_err(|error| error.to_string()),
        Request::Rate(rate) => player
            .set_rate(transport, rate)
            .map(|()| format!("rate {rate:.2}x"))
            .map_err(|error| error.to_string()),
        Request::Scrub(seconds) => player
            .scrub_seconds(transport, seconds)
            .map(|()| format!("scrubbed to {seconds:.2}s"))
            .map_err(|error| error.to_string()),
        Request::Loop(looping) => player
            .set_loop(transport, looping)
            .map(|()| format!("loop {}", if looping { "on" } else { "off" }))
            .map_err(|error| error.to_string()),
        Request::Stop => player
            .stop_and_release(transport)
            .map(|()| "stopped and released".into())
            .map_err(|error| error.to_string()),
        Request::EmergencyRelease => {
            player.emergency_release(transport);
            Ok("hard camera release requested".into())
        }
    }
}

fn send_targets(transport: &mut dyn Transport, tx: &Sender<Update>) {
    let vessels = transport.discover_vessels();
    let bodies = transport.discover_bodies();
    match (vessels, bodies) {
        (Ok(vessels), Ok(bodies)) => {
            let _ = tx.send(Update::Targets { vessels, bodies });
        }
        (vessels, bodies) => {
            let errors = [vessels.err(), bodies.err()]
                .into_iter()
                .flatten()
                .map(|error| error.to_string())
                .collect::<Vec<_>>()
                .join("; ");
            let _ = tx.send(Update::Done(Err(format!("target discovery: {errors}"))));
        }
    }
}
