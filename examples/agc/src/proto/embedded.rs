//! [`EmbeddedPort`] (A6): `agc_engine.c` linked in-process via the `vagc` shim. The bridge
//! owns time completely — pausing the game simply stops [`EmbeddedPort::step`], freezing the
//! mission clock exactly (the extern-mode pause-drift problem disappears). A built-in TCP
//! server speaks the standard wire protocol on the same base port, so DSKYs are mode-blind.

use std::collections::VecDeque;
use std::io::{ErrorKind, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::os::raw::{c_char, c_int, c_void};
use std::sync::Mutex;

use super::codec::{pack, StreamParser, Unit};
use super::{chan, AgcEvent, AgcPort, CounterKind};

#[repr(C)]
struct AgcT {
    _opaque: [u8; 0],
}

extern "C" {
    fn vagc_new() -> *mut AgcT;
    fn vagc_free(state: *mut AgcT);
    fn vagc_init(state: *mut AgcT, rope: *const c_char, core: *const c_char, all: c_int) -> c_int;
    fn vagc_step(state: *mut AgcT, cycles: c_int);
    fn vagc_write_io(state: *mut AgcT, channel: c_int, value: c_int, mask: c_int);
    fn vagc_counter(state: *mut AgcT, counter: c_int, inc_type: c_int);
    fn vagc_set_output_cb(
        cb: extern "C" fn(ctx: *mut c_void, channel: c_int, value: c_int),
        ctx: *mut c_void,
    );
    fn vagc_set_radar_words(words: *const u16, valid: c_int);
    fn vagc_make_core_dump(state: *mut AgcT, path: *const c_char) -> c_int;
}

/// The output-callback sink. A process hosts ONE AGC (the engine's CDU FIFOs are file-scope
/// statics — same limit as stock yaAGC), so a global sink is faithful to the machine.
static OUTBOX: Mutex<VecDeque<(u16, u16)>> = Mutex::new(VecDeque::new());

extern "C" fn on_output(_ctx: *mut c_void, channel: c_int, value: c_int) {
    if let Ok(mut q) = OUTBOX.lock() {
        if q.len() < 65536 {
            q.push_back((channel as u16 & 0x1FF, value as u16 & 0x7FFF));
        }
    }
}

/// AGC machine cycles per second (85,333 MCT/s — 11.7 µs each).
pub const CYCLES_PER_SEC: f64 = 1_024_000.0 / 12.0;

struct Client {
    stream: TcpStream,
    parser: StreamParser,
    masks: [u16; 256],
}

pub struct EmbeddedPort {
    state: *mut AgcT,
    inbox: VecDeque<AgcEvent>,
    listener: Option<TcpListener>,
    clients: Vec<Client>,
    /// Fractional-cycle carry between steps.
    cycle_debt: f64,
    pub sent: u64,
    pub received: u64,
}

// The raw pointer is owned exclusively by this struct; the engine has no thread affinity.
unsafe impl Send for EmbeddedPort {}

impl EmbeddedPort {
    /// Boots the rope (+ optional resume core, full format) and opens the DSKY server.
    pub fn new(rope: &str, resume: Option<&str>, base_port: u16) -> Result<Self, String> {
        let rope_c = std::ffi::CString::new(rope).map_err(|e| e.to_string())?;
        let core_c = resume.map(|r| std::ffi::CString::new(r).unwrap());
        let state = unsafe { vagc_new() };
        if state.is_null() {
            return Err("vagc_new: out of memory".into());
        }
        let rc = unsafe {
            vagc_init(
                state,
                rope_c.as_ptr(),
                core_c.as_ref().map_or(std::ptr::null(), |c| c.as_ptr()),
                1,
            )
        };
        if rc != 0 {
            unsafe { vagc_free(state) };
            return Err(format!("agc_engine_init failed: rc={rc}"));
        }
        unsafe { vagc_set_output_cb(on_output, std::ptr::null_mut()) };
        OUTBOX.lock().unwrap().clear();
        let listener = TcpListener::bind(("127.0.0.1", base_port))
            .map_err(|e| format!("bind {base_port}: {e}"))
            .ok();
        if let Some(l) = &listener {
            l.set_nonblocking(true).ok();
        }
        Ok(Self {
            state,
            inbox: VecDeque::new(),
            listener,
            clients: Vec::new(),
            cycle_debt: 0.0,
            sent: 0,
            received: 0,
        })
    }

    /// Advances the AGC by `dt` wall seconds. NOT calling this is a perfect freeze (pause).
    pub fn step(&mut self, dt: f64) {
        self.cycle_debt += dt.clamp(0.0, 0.25) * CYCLES_PER_SEC;
        let cycles = self.cycle_debt as i32;
        if cycles > 0 {
            self.cycle_debt -= cycles as f64;
            unsafe { vagc_step(self.state, cycles) };
        }
        self.pump_outputs();
        self.serve_clients();
    }

    /// Keeps the embedded radar hook's word table fresh (select codes 4..7).
    pub fn set_radar_words(&mut self, words: [u16; 4], valid: bool) {
        unsafe { vagc_set_radar_words(words.as_ptr(), valid as c_int) };
    }

    /// The raw engine pointer (debug probes only).
    pub fn raw_state(&mut self) -> *mut std::ffi::c_void {
        self.state as *mut std::ffi::c_void
    }

    /// Reads an I/O channel (debug probes).
    pub fn read_io(&mut self, channel: u16) -> u16 {
        extern "C" {
            fn vagc_read_io(state: *mut AgcT, channel: c_int) -> c_int;
        }
        unsafe { vagc_read_io(self.state, channel as c_int) as u16 }
    }

    /// Dumps the machine state (the `kill/restart mid-flight` resume path).
    pub fn core_dump(&mut self, path: &str) {
        if let Ok(c) = std::ffi::CString::new(path) {
            unsafe {
                vagc_make_core_dump(self.state, c.as_ptr());
            }
        }
    }

    fn pump_outputs(&mut self) {
        let drained: Vec<(u16, u16)> = {
            let mut q = OUTBOX.lock().unwrap();
            q.drain(..).collect()
        };
        for (channel, value) in drained {
            self.received += 1;
            let ev = if channel & 0x80 != 0 && channel >= chan::COUNTER_BASE {
                AgcEvent::CounterEcho { register: (channel & 0x7F) as u8, pulse: value }
            } else {
                AgcEvent::Channel { channel, value }
            };
            self.inbox.push_back(ev);
            // Fan out to DSKY clients exactly like yaAGC's broadcast.
            let bytes = pack(channel, value, false);
            self.clients.retain_mut(|c| c.stream.write_all(&bytes).is_ok());
        }
    }

    fn serve_clients(&mut self) {
        if let Some(l) = &self.listener {
            while let Ok((s, _)) = l.accept() {
                s.set_nonblocking(true).ok();
                s.set_nodelay(true).ok();
                self.clients.push(Client {
                    stream: s,
                    parser: StreamParser::default(),
                    masks: [0o77777; 256],
                });
            }
        }
        let state = self.state;
        let mut buf = [0u8; 512];
        self.clients.retain_mut(|c| loop {
            match c.stream.read(&mut buf) {
                Ok(0) => return false,
                Ok(n) => {
                    for &b in &buf[..n] {
                        if let Some(Unit::Packet(p)) = c.parser.push(b) {
                            if p.u {
                                c.masks[(p.channel & 0xFF) as usize] = p.value;
                            } else if p.channel & 0x80 != 0 && p.channel >= chan::COUNTER_BASE {
                                unsafe {
                                    vagc_counter(
                                        state,
                                        (p.channel & 0x7F) as c_int,
                                        p.value as c_int,
                                    );
                                }
                            } else {
                                let mask = c.masks[(p.channel & 0xFF) as usize];
                                unsafe {
                                    vagc_write_io(
                                        state,
                                        p.channel as c_int,
                                        p.value as c_int,
                                        mask as c_int,
                                    );
                                }
                            }
                        }
                    }
                }
                Err(e) if e.kind() == ErrorKind::WouldBlock => return true,
                Err(_) => return false,
            }
        });
    }
}

impl Drop for EmbeddedPort {
    fn drop(&mut self) {
        unsafe { vagc_free(self.state) };
    }
}

impl AgcPort for EmbeddedPort {
    fn recv(&mut self) -> Option<AgcEvent> {
        if self.inbox.is_empty() {
            self.pump_outputs();
            self.serve_clients();
        }
        self.inbox.pop_front()
    }

    fn write_channel(&mut self, channel: u16, value: u16, mask: Option<u16>) {
        self.sent += 1;
        unsafe {
            vagc_write_io(
                self.state,
                channel as c_int,
                value as c_int,
                mask.unwrap_or(0o77777) as c_int,
            );
        }
    }

    fn counter(&mut self, register: u8, kind: CounterKind) {
        self.sent += 1;
        unsafe { vagc_counter(self.state, register as c_int, kind.code() as c_int) };
    }

    fn connected(&self) -> bool {
        true
    }

    fn step(&mut self, dt: f64) {
        EmbeddedPort::step(self, dt);
    }

    fn set_radar_words(&mut self, words: [u16; 4], valid: bool) {
        EmbeddedPort::set_radar_words(self, words, valid);
    }

    fn stats(&self) -> (u64, u64) {
        (self.sent, self.received)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dsky::Dsky;

    fn rope() -> Option<String> {
        let r = std::env::var("ROPE").ok()?;
        std::path::Path::new(&r).exists().then_some(r)
    }

    /// The embedded engine boots Luminary099, answers V16N36E, and freezing means frozen.
    /// Needs ROPE=…/Luminary099/MAIN.agc.bin (skips otherwise, like AGC_IT).
    #[test]
    fn embedded_luminary_runs_and_freezes() {
        let Some(rope) = rope() else {
            eprintln!("ROPE not set — skipping embedded live test");
            return;
        };
        let mut port = EmbeddedPort::new(&rope, None, 19961).expect("boot");
        let mut dsky = Dsky::new();
        // Fresh start settles (~3 s of AGC time in fast-forward).
        for _ in 0..30 {
            port.step(0.1);
            while let Some(ev) = port.recv() {
                if let AgcEvent::Channel { channel, value } = ev {
                    dsky.on_channel(channel, value);
                }
            }
        }
        // Type V16N36E with realistic key pacing (the keyboard monitor needs cycles between
        // presses).
        for c in ['v', '1', '6', 'n', '3', '6', '\n'] {
            if let Some(crate::dsky::KeyWire::Code(k)) = crate::dsky::key_wire(c) {
                port.write_channel(chan::DSKY_KEYS, k, Some(0o37));
            }
            for _ in 0..3 {
                port.step(0.1);
                while let Some(ev) = port.recv() {
                    if let AgcEvent::Channel { channel, value } = ev {
                        dsky.on_channel(channel, value);
                    }
                }
            }
        }
        assert_eq!(dsky.verb, ['1', '6']);
        assert_eq!(dsky.noun, ['3', '6']);
        let r3_a = dsky.r[2];
        // Freeze: no step() calls ⇒ no channel traffic ⇒ the clock cannot move.
        for _ in 0..10 {
            assert!(port.recv().is_none(), "frozen AGC must be silent");
        }
        assert_eq!(dsky.r[2], r3_a, "mission clock frozen while paused");
        // Thaw and confirm it ticks again.
        for _ in 0..20 {
            port.step(0.1);
            while let Some(ev) = port.recv() {
                if let AgcEvent::Channel { channel, value } = ev {
                    dsky.on_channel(channel, value);
                }
            }
        }
        assert_ne!(dsky.r[2], r3_a, "clock resumes after thaw");
    }
}
