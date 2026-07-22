//! [`SocketPort`] — the extern-mode [`AgcPort`](super::AgcPort): a TCP client of a stock yaAGC
//! process. yaAGC listens on ten ports (base..base+9), **one client each** — connect scans
//! upward for a free slot. Non-blocking reads, signature resync, auto-reconnect with the
//! connect-time channel replay handled by the layer above (it just sees Channel events).

use std::io::{ErrorKind, Read, Write};
use std::net::TcpStream;
use std::time::{Duration, Instant};

use super::codec::{pack, StreamParser, Unit};
use super::{chan, AgcEvent, AgcPort, CounterKind};

pub struct SocketPort {
    base_port: u16,
    stream: Option<TcpStream>,
    parser: StreamParser,
    inbox: std::collections::VecDeque<AgcEvent>,
    last_rx: Instant,
    last_attempt: Instant,
    /// Packets sent since connect (rate accounting vs the interlace ceiling).
    pub sent: u64,
    /// Events received since connect.
    pub received: u64,
}

/// No traffic (not even keepalives ~1.5 s apart) for this long ⇒ the link is dead.
const DEAD_AFTER: Duration = Duration::from_secs(5);
const RETRY_EVERY: Duration = Duration::from_secs(1);

impl SocketPort {
    /// Creates a port that will keep trying to reach yaAGC at `127.0.0.1:base..base+9`.
    pub fn new(base_port: u16) -> Self {
        Self {
            base_port,
            stream: None,
            parser: StreamParser::default(),
            inbox: Default::default(),
            last_rx: Instant::now(),
            last_attempt: Instant::now() - RETRY_EVERY,
            sent: 0,
            received: 0,
        }
    }

    /// Attempts a connection now (also called lazily by `recv`/writes).
    pub fn ensure_connected(&mut self) -> bool {
        if let Some(s) = &self.stream {
            // Keepalive-based liveness: yaAGC pings every ~1.5 s even when idle.
            if self.last_rx.elapsed() > DEAD_AFTER {
                let _ = s.shutdown(std::net::Shutdown::Both);
                self.stream = None;
            } else {
                return true;
            }
        }
        if self.last_attempt.elapsed() < RETRY_EVERY {
            return false;
        }
        self.last_attempt = Instant::now();
        for slot in 0..10u16 {
            let addr = ("127.0.0.1", self.base_port + slot);
            if let Ok(s) = TcpStream::connect_timeout(
                &std::net::SocketAddr::from(([127, 0, 0, 1], self.base_port + slot)),
                Duration::from_millis(250),
            ) {
                let _ = addr;
                s.set_nonblocking(true).ok();
                s.set_nodelay(true).ok();
                self.stream = Some(s);
                self.parser = StreamParser::default();
                self.last_rx = Instant::now();
                self.sent = 0;
                self.received = 0;
                return true;
            }
        }
        false
    }

    fn pump(&mut self) {
        if !self.ensure_connected() {
            return;
        }
        let mut buf = [0u8; 1024];
        loop {
            let n = match self.stream.as_mut().unwrap().read(&mut buf) {
                Ok(0) => {
                    self.stream = None;
                    return;
                }
                Ok(n) => n,
                Err(e) if e.kind() == ErrorKind::WouldBlock => return,
                Err(_) => {
                    self.stream = None;
                    return;
                }
            };
            self.last_rx = Instant::now();
            for &b in &buf[..n] {
                match self.parser.push(b) {
                    Some(Unit::KeepAlive) => self.inbox.push_back(AgcEvent::KeepAlive),
                    Some(Unit::Packet(p)) => {
                        self.received += 1;
                        let ev = if p.channel & 0x80 != 0 && p.channel >= chan::COUNTER_BASE {
                            AgcEvent::CounterEcho {
                                register: (p.channel & 0x7F) as u8,
                                pulse: p.value,
                            }
                        } else {
                            AgcEvent::Channel { channel: p.channel, value: p.value }
                        };
                        self.inbox.push_back(ev);
                    }
                    None => {}
                }
            }
        }
    }

    fn send(&mut self, bytes: &[u8]) {
        if !self.ensure_connected() {
            return;
        }
        if let Some(s) = self.stream.as_mut() {
            if s.write_all(bytes).is_err() {
                self.stream = None;
            } else {
                self.sent += (bytes.len() / 4) as u64;
            }
        }
    }
}

impl AgcPort for SocketPort {
    fn recv(&mut self) -> Option<AgcEvent> {
        if self.inbox.is_empty() {
            self.pump();
        }
        self.inbox.pop_front()
    }

    fn write_channel(&mut self, channel: u16, value: u16, mask: Option<u16>) {
        let mut out = [0u8; 8];
        let n = if let Some(m) = mask {
            out[..4].copy_from_slice(&pack(channel, m, true));
            out[4..].copy_from_slice(&pack(channel, value, false));
            8
        } else {
            out[..4].copy_from_slice(&pack(channel, value, false));
            4
        };
        self.send(&out[..n]);
    }

    fn counter(&mut self, register: u8, kind: CounterKind) {
        let bytes = pack(chan::COUNTER_BASE | register as u16, kind.code(), false);
        self.send(&bytes);
    }

    fn connected(&self) -> bool {
        self.stream.is_some()
    }
}
