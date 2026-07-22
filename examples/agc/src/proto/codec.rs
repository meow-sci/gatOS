//! Packet pack/parse — a faithful port of `yaAGC/agc_utilities.c` `FormIoPacket` /
//! `ParseIoPacket` (the byte-layout comments there are the spec).

/// One parsed 4-byte unit.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Packet {
    /// The 8-bit channel number (the 9-bit wire field without the u-bit).
    pub channel: u16,
    /// The 15-bit value.
    pub value: u16,
    /// The u-bit (0x100 of the 9-bit channel field): this is a mask packet.
    pub u: bool,
}

/// Packs `channel` (9 bits: `0x100` = u-bit) + `value` (15 bits) into 4 bytes.
/// Mirrors `FormIoPacket` byte-for-byte.
pub fn pack(channel: u16, value: u16, u: bool) -> [u8; 4] {
    let ch = (channel & 0xFF) | if u { 0x100 } else { 0 };
    let v = value & 0x7FFF;
    [
        (ch >> 3) as u8,
        0x40 | (((ch << 3) & 0x38) as u8) | (((v >> 12) & 0x07) as u8),
        0x80 | (((v >> 6) & 0x3F) as u8),
        0xC0 | ((v & 0x3F) as u8),
    ]
}

/// Parses 4 bytes into a [`Packet`]. Returns `None` unless all four signature fields match
/// (`00/01/10/11` in the top two bits) — the caller uses that to resync on a byte stream.
pub fn parse(bytes: [u8; 4]) -> Option<Packet> {
    if bytes[0] & 0xC0 != 0x00
        || bytes[1] & 0xC0 != 0x40
        || bytes[2] & 0xC0 != 0x80
        || bytes[3] & 0xC0 != 0xC0
    {
        return None;
    }
    let channel = (((bytes[0] & 0x1F) as u16) << 3) | (((bytes[1] >> 3) & 0x07) as u16);
    let value = (((bytes[1] as u16) << 12) & 0x7000)
        | (((bytes[2] as u16) << 6) & 0x0FC0)
        | ((bytes[3] as u16) & 0x003F);
    Some(Packet {
        channel,
        value,
        u: bytes[0] & 0x20 != 0,
    })
}

/// An incremental parser over a TCP byte stream: buffers up to 4 bytes, resyncs on signature
/// mismatch by shifting one byte (exactly the strategy the stock clients use), and detects the
/// `FF FF FF FF` keepalive.
#[derive(Default)]
pub struct StreamParser {
    buf: [u8; 4],
    len: usize,
}

/// One unit out of the byte stream.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Unit {
    Packet(Packet),
    KeepAlive,
}

impl StreamParser {
    /// Feeds one byte; returns a completed unit when four coherent bytes are assembled.
    pub fn push(&mut self, byte: u8) -> Option<Unit> {
        self.buf[self.len] = byte;
        self.len += 1;
        if self.len < 4 {
            return None;
        }
        if self.buf == [0xFF; 4] {
            self.len = 0;
            return Some(Unit::KeepAlive);
        }
        if let Some(p) = parse(self.buf) {
            self.len = 0;
            return Some(Unit::Packet(p));
        }
        // Resync: drop the first byte, keep looking for a signature-coherent window.
        self.buf.copy_within(1..4, 0);
        self.len = 3;
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Golden packets hand-computed from the FormIoPacket layout (AGC_PLAN Appendix A).
    #[test]
    fn golden_pack() {
        // Channel 010, value 0 (blank DSKY row): bytes 00|0000001 ...
        assert_eq!(pack(0o10, 0, false), [0x01, 0x40, 0x80, 0xC0]);
        // Channel 015, keycode 021 (VERB): ch=0x0D -> b0=0x01, b1=0x40|0x28|0 = 0x68 ...
        assert_eq!(
            pack(0o15, 0o21, false),
            [0x01, 0x40 | 0x28, 0x80, 0xC0 | 0o21 as u8]
        );
        // u-packet mask 037 on ch 015 (the keypress mask every DSKY sends first).
        let m = pack(0o15, 0o37, true);
        assert_eq!(m[0] & 0x20, 0x20, "u-bit lives at byte0 0x20");
        assert_eq!(parse(m).unwrap(), Packet { channel: 0o15, value: 0o37, u: true });
        // Counter packet: PIPAX +1 = channel 0200|037 = 0237, value 0 (PINC).
        let c = pack(0o237, 0, false);
        assert_eq!(c[0] & 0x10, 0x10, "counter flag = channel bit 0200 -> byte0 0x10");
        assert_eq!(parse(c).unwrap().channel, 0o237);
        // PRO press: mask (ch 0432 = u|032, 020000), then data (032, 0) — active-low.
        let pro_mask = pack(0o32, 0o20000, true);
        let parsed = parse(pro_mask).unwrap();
        assert!(parsed.u);
        assert_eq!(parsed.channel, 0o32);
        assert_eq!(parsed.value, 0o20000);
    }

    #[test]
    fn round_trip_all_fields() {
        for &ch in &[0u16, 0o5, 0o15, 0o163, 0o177, 0o237, 0xFF] {
            for &v in &[0u16, 1, 0o37, 0o20000, 0x7FFF] {
                for &u in &[false, true] {
                    let p = parse(pack(ch, v, u)).expect("round trip");
                    assert_eq!((p.channel, p.value, p.u), (ch, v, u));
                }
            }
        }
    }

    #[test]
    fn parse_rejects_bad_signatures() {
        assert!(parse([0x40, 0x40, 0x80, 0xC0]).is_none());
        assert!(parse([0x00, 0x00, 0x80, 0xC0]).is_none());
        assert!(parse([0x00, 0x40, 0xC0, 0xC0]).is_none());
        assert!(parse([0x00, 0x40, 0x80, 0x80]).is_none());
    }

    #[test]
    fn stream_resyncs_after_garbage() {
        let mut sp = StreamParser::default();
        let good = pack(0o10, 0o1234, false);
        let mut units = Vec::new();
        // Two garbage bytes, then a clean packet, then a keepalive.
        for b in [0xC3u8, 0x99].iter().chain(good.iter()).chain([0xFF; 4].iter()) {
            if let Some(u) = sp.push(*b) {
                units.push(u);
            }
        }
        assert_eq!(
            units,
            vec![
                Unit::Packet(Packet { channel: 0o10, value: 0o1234, u: false }),
                Unit::KeepAlive
            ]
        );
    }

    #[test]
    fn counter_codes_match_engine_switch() {
        use crate::proto::CounterKind::*;
        assert_eq!(Pinc.code(), 0);
        assert_eq!(Pcdu.code(), 1);
        assert_eq!(Minc.code(), 2);
        assert_eq!(Mcdu.code(), 3);
        assert_eq!(Dinc.code(), 4);
        assert_eq!(Shinc.code(), 5);
        assert_eq!(Shanc.code(), 6);
        assert_eq!(PcduFast.code(), 0o21);
        assert_eq!(McduFast.code(), 0o23);
    }
}
