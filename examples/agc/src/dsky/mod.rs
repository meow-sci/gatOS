//! DSKY display-state decode (AGC_PLAN §1.5 / Appendix B, verified against `piDSKY.py` +
//! `yaDSKY2.cpp`): ch 010 relay rows → digits/signs + row-12 lamps, ch 011 → COMP/UPLINK ACTY
//! + engine bits, fictitious ch 0163 → composite lamps with the AGC-timed VN flash.

pub mod ui;

/// 5-bit relay digit code → glyph (decimal codes per piDSKY / the 7Seg-<code> assets).
pub fn glyph(code: u8) -> char {
    match code {
        0 => ' ',
        21 => '0',
        3 => '1',
        25 => '2',
        27 => '3',
        15 => '4',
        30 => '5',
        28 => '6',
        19 => '7',
        29 => '8',
        31 => '9',
        _ => '?',
    }
}

/// Register sign state: two relay bits, `+` wins when both set.
#[derive(Clone, Copy, Default, PartialEq, Eq, Debug)]
pub struct Sign {
    pub plus: bool,
    pub minus: bool,
}

impl Sign {
    pub fn ch(self) -> char {
        if self.plus {
            '+'
        } else if self.minus {
            '-'
        } else {
            ' '
        }
    }
}

/// The full DSKY display state a TUI renders.
#[derive(Clone, Default)]
pub struct Dsky {
    /// PROG/VERB/NOUN two-digit windows.
    pub prog: [char; 2],
    pub verb: [char; 2],
    pub noun: [char; 2],
    /// R1-R3 five-digit registers + signs.
    pub r: [[char; 5]; 3],
    pub sign: [Sign; 3],
    // ch 011 lamps
    pub comp_acty: bool,
    pub uplink_acty: bool,
    // ch 010 row-12 lamps (LM set)
    pub prio_disp: bool,
    pub no_dap: bool,
    pub vel: bool,
    pub no_att: bool,
    pub alt: bool,
    pub gimbal_lock: bool,
    pub tracker: bool,
    pub prog_lamp: bool,
    // ch 0163 composite lamps
    pub agc_warn: bool,
    pub temp: bool,
    pub key_rel: bool,
    pub vn_flash: bool,
    pub opr_err: bool,
    pub restart: bool,
    pub stby: bool,
    pub el_off: bool,
    // ch 011 engine command bits (status display)
    pub engine_on: bool,
    pub engine_off: bool,
}

impl Dsky {
    pub fn new() -> Self {
        let mut d = Self::default();
        d.prog = [' '; 2];
        d.verb = [' '; 2];
        d.noun = [' '; 2];
        d.r = [[' '; 5]; 3];
        d
    }

    /// Feed one AGC channel write.
    pub fn on_channel(&mut self, channel: u16, value: u16) {
        match channel {
            0o10 => self.on_digits(value),
            0o11 => {
                self.comp_acty = value & 0o2 != 0;
                self.uplink_acty = value & 0o4 != 0;
                self.engine_on = value & (1 << 12) != 0;
                self.engine_off = value & (1 << 13) != 0;
            }
            0o163 => {
                self.agc_warn = value & 0o1 != 0;
                self.temp = value & 0o10 != 0;
                self.key_rel = value & 0o20 != 0;
                self.vn_flash = value & 0o40 != 0;
                self.opr_err = value & 0o100 != 0;
                self.restart = value & 0o200 != 0;
                self.stby = value & 0o400 != 0;
                self.el_off = value & 0o1000 != 0;
            }
            _ => {}
        }
    }

    fn on_digits(&mut self, value: u16) {
        let row = (value >> 11) & 0o17;
        let b = value & 0o2000 != 0;
        let c = glyph(((value >> 5) & 0o37) as u8);
        let d = glyph((value & 0o37) as u8);
        match row {
            11 => self.prog = [c, d],
            10 => self.verb = [c, d],
            9 => self.noun = [c, d],
            8 => self.r[0][0] = d,
            7 => {
                self.r[0][1] = c;
                self.r[0][2] = d;
                self.sign[0].plus = b;
            }
            6 => {
                self.r[0][3] = c;
                self.r[0][4] = d;
                self.sign[0].minus = b;
            }
            5 => {
                self.r[1][0] = c;
                self.r[1][1] = d;
                self.sign[1].plus = b;
            }
            4 => {
                self.r[1][2] = c;
                self.r[1][3] = d;
                self.sign[1].minus = b;
            }
            3 => {
                self.r[1][4] = c;
                self.r[2][0] = d;
            }
            2 => {
                self.r[2][1] = c;
                self.r[2][2] = d;
                self.sign[2].plus = b;
            }
            1 => {
                self.r[2][3] = c;
                self.r[2][4] = d;
                self.sign[2].minus = b;
            }
            12 => {
                self.prio_disp = value & 0o1 != 0;
                self.no_dap = value & 0o2 != 0;
                self.vel = value & 0o4 != 0;
                self.no_att = value & 0o10 != 0;
                self.alt = value & 0o20 != 0;
                self.gimbal_lock = value & 0o40 != 0;
                self.tracker = value & 0o200 != 0;
                self.prog_lamp = value & 0o400 != 0;
            }
            _ => {}
        }
    }
}

/// The keypress → wire mapping (mask packet then data — `piDSKY.py packetize`). PRO is not a
/// keycode: ch 032 b14, active-low, real press AND release writes.
pub enum KeyWire {
    /// (channel 015, keycode, mask 037) — KEYRUPT1 auto.
    Code(u16),
    /// PRO pressed (ch 032, value 0, mask 020000) / released (value 020000).
    Pro { pressed: bool },
}

pub fn key_wire(c: char) -> Option<KeyWire> {
    if c == 'p' || c == 'P' {
        return Some(KeyWire::Pro { pressed: true });
    }
    crate::uplink::keycode(match c {
        '0'..='9' | '+' | '-' => c,
        'v' => 'V',
        'n' => 'N',
        '\n' | '\r' => 'E',
        'c' => 'C',
        'r' => 'R',
        'k' => 'K',
        _ => return None,
    })
    .map(KeyWire::Code)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Assemble "V16 N36" + a ticking R1 from raw relay writes.
    #[test]
    fn digits_decode() {
        let mut d = Dsky::new();
        // Row 10 (VERB): c='1'(3), d='6'(28).
        d.on_channel(0o10, (10 << 11) | ((3 as u16) << 5) | 28);
        // Row 9 (NOUN): '3'(27),'6'(28).
        d.on_channel(0o10, (9 << 11) | (27 << 5) | 28);
        assert_eq!(d.verb, ['1', '6']);
        assert_eq!(d.noun, ['3', '6']);
        // Row 7: R1D2='4'(15), R1D3='2'(25), sign +.
        d.on_channel(0o10, (7 << 11) | 0o2000 | (15 << 5) | 25);
        assert_eq!(d.r[0][1], '4');
        assert_eq!(d.r[0][2], '2');
        assert_eq!(d.sign[0].ch(), '+');
        // Row 6 with the minus bit too: '+' wins.
        d.on_channel(0o10, (6 << 11) | 0o2000 | (21 << 5) | 21);
        assert_eq!(d.sign[0].ch(), '+');
    }

    #[test]
    fn lamps_decode() {
        let mut d = Dsky::new();
        d.on_channel(0o10, (12 << 11) | 0o40 | 0o10); // GIMBAL LOCK + NO ATT
        assert!(d.gimbal_lock && d.no_att && !d.alt);
        d.on_channel(0o163, 0o40 | 0o200); // VN flash + RESTART
        assert!(d.vn_flash && d.restart);
        d.on_channel(0o11, 0o2 | (1 << 12)); // COMP ACTY + ENGINE ON
        assert!(d.comp_acty && d.engine_on);
    }

    #[test]
    fn key_wires() {
        assert!(matches!(key_wire('v'), Some(KeyWire::Code(0o21))));
        assert!(matches!(key_wire('\n'), Some(KeyWire::Code(0o34))));
        assert!(matches!(key_wire('5'), Some(KeyWire::Code(5))));
        assert!(matches!(key_wire('p'), Some(KeyWire::Pro { pressed: true })));
        assert!(key_wire('x').is_none());
    }
}
