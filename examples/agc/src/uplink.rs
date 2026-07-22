//! The digital uplink (AGC_PLAN §4.7): P27 (V70-V73) word streams over fictitious channel 0173.
//! Each uplinked "keycode" K is triplicated as `K | ((K ^ 037) << 5) | (K << 10)` (verified in
//! `yaDSKY2.cpp OutputKeycode` + Luminary's `KEYRUPT,_UPRUPT.agc` UPRUPT redundancy check);
//! yaAGC stores the word in INLINK and raises UPRUPT immediately. A bad triple locks out the
//! uplink until an error-reset — so the queue paces itself and never floods.

use crate::proto::{chan, AgcPort};

/// DSKY keycodes (octal — `piDSKY.py parseDskyKey`).
pub fn keycode(c: char) -> Option<u16> {
    Some(match c {
        '0' => 0o20,
        '1'..='9' => c as u16 - '0' as u16,
        'V' => 0o21, // VERB
        'N' => 0o37, // NOUN
        'E' => 0o34, // ENTR
        'R' => 0o22, // RSET (error reset — ELRCODE)
        'C' => 0o36, // CLR
        'K' => 0o31, // KEY REL
        '+' => 0o32,
        '-' => 0o33,
        _ => return None,
    })
}

/// The triplicated uplink word for one keycode.
pub fn uplink_word(k: u16) -> u16 {
    let k = k & 0o37;
    k | ((k ^ 0o37) << 5) | (k << 10)
}

/// A paced uplink queue: push key strings, tick() sends a few words per bridge tick (UPRUPT
/// service takes AGC time; ~40 words/s is comfortably conservative).
#[derive(Default)]
pub struct Uplink {
    queue: std::collections::VecDeque<u16>,
    /// Words sent since start (UPLINK ACTY flickers on the DSKY while this grows).
    pub sent: u64,
}

impl Uplink {
    pub fn new() -> Self {
        Self::default()
    }

    /// Queues a DSKY key string, e.g. `"V71E24E1733E..."` — every char must be a keycode.
    pub fn push_keys(&mut self, keys: &str) -> Result<(), String> {
        let mut words = Vec::with_capacity(keys.len());
        for c in keys.chars() {
            if c.is_whitespace() {
                continue;
            }
            let k = keycode(c).ok_or_else(|| format!("no keycode for {c:?}"))?;
            words.push(uplink_word(k));
        }
        self.queue.extend(words);
        Ok(())
    }

    pub fn pending(&self) -> usize {
        self.queue.len()
    }

    /// Sends up to one word per tick (25 ms tick ⇒ 40 words/s).
    pub fn tick(&mut self, port: &mut dyn AgcPort) {
        if let Some(w) = self.queue.pop_front() {
            port.write_channel(chan::UPLINK, w, None);
            self.sent += 1;
        }
    }
}

/// Renders an octal word as the five DSKY digits + E of a P27 data entry.
fn oct5(w: u16) -> String {
    format!("{:05o}E", w & 0o77777)
}

/// Builds the V71 contiguous-block update key string: `V71E <II>E <ECADR>E <data...>E V33E`,
/// where II = number of components including II and ECADR (i.e. data.len() + 2), max 20 (024).
pub fn v71_block(ecadr: u16, data: &[u16]) -> String {
    let ii = data.len() + 2;
    assert!((3..=20).contains(&ii), "V71 allows 1..=18 data words");
    let mut s = format!("V71E{:02o}E{}", ii, oct5(ecadr));
    for &w in data {
        s.push_str(&oct5(w));
    }
    s.push_str("V33E");
    s
}

/// REFSMMAT ECADR in Luminary099 (E3,1733 — verified in the yaYUL listing).
pub const REFSMMAT_ECADR: u16 = 0o1733;

/// Builds the REFSMMAT uplink from the stable-member basis expressed in the reference frame:
/// `rows[i]` = SM axis i in CCI coordinates. Elements are DP scaled B-1 (V71 REFSMMAT format,
/// `UPDATE_PROGRAM.agc:141-160` — 20 components: II, ECADR, 9 DP pairs).
pub fn refsmmat_uplink(rows: [[f64; 3]; 3]) -> String {
    let mut data = Vec::with_capacity(18);
    for row in rows {
        for elem in row {
            let words = crate::padload::dp(elem, 1);
            data.extend_from_slice(&words);
        }
    }
    v71_block(REFSMMAT_ECADR, &data)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The triplication rule from KEYRUPT,_UPRUPT.agc: low 5 = K, mid 5 = ~K, high 5 = K.
    #[test]
    fn word_is_triplicated() {
        for k in [0o1u16, 0o20, 0o21, 0o34, 0o37] {
            let w = uplink_word(k);
            assert_eq!(w & 0o37, k);
            assert_eq!((w >> 5) & 0o37, k ^ 0o37);
            assert_eq!((w >> 10) & 0o37, k);
        }
    }

    #[test]
    fn v71_block_shape() {
        let s = v71_block(0o1733, &[0o12345, 0o67]);
        assert_eq!(s, "V71E04E01733E12345E00067EV33E");
    }

    #[test]
    fn refsmmat_has_20_components() {
        let s = refsmmat_uplink([[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]);
        // II = 18 data + 2 = 20 = 024 octal.
        assert!(s.starts_with("V71E24E01733E"), "{s}");
        // 1.0 scaled B-1 = 0.5 fraction → DP hi word 0.5·2^14 = 8192 = 0o20000.
        assert!(s.contains("20000E00000E"), "{s}");
        assert!(s.ends_with("V33E"));
    }

    #[test]
    fn queue_paces_one_word_per_tick() {
        struct Rec {
            words: Vec<u16>,
        }
        impl AgcPort for Rec {
            fn recv(&mut self) -> Option<crate::proto::AgcEvent> {
                None
            }
            fn write_channel(&mut self, c: u16, v: u16, _m: Option<u16>) {
                assert_eq!(c, chan::UPLINK);
                self.words.push(v);
            }
            fn counter(&mut self, _r: u8, _k: crate::proto::CounterKind) {}
            fn connected(&self) -> bool {
                true
            }
        }
        let mut u = Uplink::new();
        let mut p = Rec { words: vec![] };
        u.push_keys("V71E").unwrap();
        assert_eq!(u.pending(), 4);
        u.tick(&mut p);
        u.tick(&mut p);
        assert_eq!(p.words.len(), 2);
        assert_eq!(p.words[0], uplink_word(0o21));
    }
}
