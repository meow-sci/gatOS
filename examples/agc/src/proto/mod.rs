//! The yaAGC socket wire protocol (verified against `yaAGC/agc_utilities.c` +
//! `yaAGC/SocketAPI.c`, see AGC_PLAN §1.3 / Appendix A).
//!
//! Every unit is a 4-byte packet with 2-bit resync signatures in the top bits:
//! `00 u ccccc · 01 ccc vvv · 10 vvvvvv · 11 vvvvvv` — a 9-bit "channel" (u-bit 0x100 +
//! 8-bit channel number) and a 15-bit value. Three packet flavors:
//! - **data**: plain channel write (either direction).
//! - **mask** (`u` set): stores a per-client bitmask; the *next* data write on that channel
//!   replaces only masked bits (`SocketAPI.c:219-238`).
//! - **counter** (channel `0200 | reg`): unprogrammed increment of erasable register `reg`;
//!   the *value* carries the increment type ([`CounterKind`]).

pub mod client;
pub mod codec;

pub use client::SocketPort;
pub use codec::{pack, parse, Packet};

/// Unprogrammed-sequence increment types (`agc_engine.c` `UnprogrammedIncrement`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CounterKind {
    /// +1, one's-complement (PIPAs).
    Pinc,
    /// −1, one's-complement.
    Minc,
    /// +1, two's-complement (CDUs; FIFO-paced at 400 counts/s on CDUX/Y/Z).
    Pcdu,
    /// −1, two's-complement.
    Mcdu,
    /// +1 two's-complement, fast lane (6,400 counts/s on the CDU FIFOs).
    PcduFast,
    /// −1 two's-complement, fast lane.
    McduFast,
    /// Diminish-toward-zero (THRUST/ALTM clocking; echoes POUT/MOUT/ZOUT).
    Dinc,
    /// Shift left, insert 0 (serial radar/uplink bit).
    Shinc,
    /// Shift left, insert 1.
    Shanc,
}

impl CounterKind {
    /// The wire value carried in a counter packet (octal per the engine's switch).
    pub fn code(self) -> u16 {
        match self {
            CounterKind::Pinc => 0,
            CounterKind::Pcdu => 1,
            CounterKind::Minc => 2,
            CounterKind::Mcdu => 3,
            CounterKind::Dinc => 4,
            CounterKind::Shinc => 5,
            CounterKind::Shanc => 6,
            CounterKind::PcduFast => 0o21,
            CounterKind::McduFast => 0o23,
        }
    }
}

/// Erasable counter registers (octal addresses; `agc_engine.h`).
pub mod reg {
    pub const CDUX: u8 = 0o32;
    pub const CDUY: u8 = 0o33;
    pub const CDUZ: u8 = 0o34;
    pub const PIPAX: u8 = 0o37;
    pub const PIPAY: u8 = 0o40;
    pub const PIPAZ: u8 = 0o41;
    pub const INLINK: u8 = 0o45;
    pub const RNRAD: u8 = 0o46;
    pub const THRUST: u8 = 0o55;
    pub const ALTM: u8 = 0o60;
}

/// AGC I/O channels the bridge and DSKY care about (octal).
pub mod chan {
    /// Vertical RCS jets (LM "PYJETS").
    pub const JETS_V: u16 = 0o5;
    /// Horizontal RCS jets (LM "ROLLJETS").
    pub const JETS_H: u16 = 0o6;
    /// DSKY digits (relay rows).
    pub const DSKY_DIGITS: u16 = 0o10;
    /// DSALMOUT: lamps + LM ENGINE ON (b13) / ENGINE OFF (b14).
    pub const DSALMOUT: u16 = 0o11;
    /// ISS/GN&C moding: b4 coarse align, b5 zero IMU CDUs, b9-12 DPS trim, b13 LR pos 2 cmd.
    pub const CHAN12: u16 = 0o12;
    /// Radar select (b1-3) + activity (b4), DSKY light test (b10).
    pub const CHAN13: u16 = 0o13;
    /// Drive enables: b4 THRUST activity, b6-10 gyro, b13-15 CDU Z/Y/X drive.
    pub const CHAN14: u16 = 0o14;
    /// DSKY keycodes (writes auto-raise KEYRUPT1).
    pub const DSKY_KEYS: u16 = 0o15;
    /// Marks / ROD switch (KEYRUPT2 never raised over sockets — AGC_PLAN §1.6.7).
    pub const CHAN16: u16 = 0o16;
    /// Input discretes (active-low except b15).
    pub const CHAN30: u16 = 0o30;
    /// ACA/THC + mode switches (active-low).
    pub const CHAN31: u16 = 0o31;
    /// Thruster-pair disables + PRO key (b14, active-low).
    pub const CHAN32: u16 = 0o32;
    /// Radar discretes + uplink/downlink flags (b11-15 latched internally).
    pub const CHAN33: u16 = 0o33;
    /// Downlink word 1 (paired with 035, 50 word-pairs/s).
    pub const DOWNLINK1: u16 = 0o34;
    /// Downlink word 2.
    pub const DOWNLINK2: u16 = 0o35;
    /// Fictitious: composite DSKY lamps + VN flash (sent on change only).
    pub const DSKY_LAMPS: u16 = 0o163;
    /// Fictitious: digital uplink word → INLINK + UPRUPT.
    pub const UPLINK: u16 = 0o173;
    /// Fictitious: IMU CDU X coarse-align drive burst (value = 040000·minus | count).
    pub const CDUX_DRIVE: u16 = 0o174;
    /// Fictitious: IMU CDU Y drive burst.
    pub const CDUY_DRIVE: u16 = 0o175;
    /// Fictitious: IMU CDU Z drive burst.
    pub const CDUZ_DRIVE: u16 = 0o176;
    /// Fictitious: gyro fine-align burst (value = ((ch014 & 0740) << 6) | count).
    pub const GYRO: u16 = 0o177;
    /// Counter-echo base: DINC POUT/MOUT/ZOUT arrive as channel 0200|reg.
    pub const COUNTER_BASE: u16 = 0o200;
}

/// What a connected AGC said to us (one parsed inbound packet).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AgcEvent {
    /// A CPU channel write broadcast (or the connect-time replay).
    Channel { channel: u16, value: u16 },
    /// A counter echo: DINC drains emit POUT (015) / MOUT (016) / ZOUT (017) on 0200|reg.
    CounterEcho { register: u8, pulse: u16 },
    /// The ~1.5 s `FF FF FF FF` keepalive (doubles as a liveness signal).
    KeepAlive,
}

/// The AGC seam the bridge drives — [`SocketPort`] (extern yaAGC process) today; an embedded
/// `agc_engine.c` port (A6) implements the same trait so everything above it is mode-blind.
pub trait AgcPort {
    /// Drains one pending event, non-blocking. `None` = nothing waiting.
    fn recv(&mut self) -> Option<AgcEvent>;

    /// Writes a channel value; `mask` sends a u-packet first so only masked bits change
    /// (the shared-discrete convention every yaAGC peripheral uses).
    fn write_channel(&mut self, channel: u16, value: u16, mask: Option<u16>);

    /// Sends one unprogrammed counter increment to erasable register `register`.
    fn counter(&mut self, register: u8, kind: CounterKind);

    /// True while the underlying transport is up (used for the NO AGC banner / resync).
    fn connected(&self) -> bool;
}
