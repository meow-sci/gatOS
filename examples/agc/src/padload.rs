//! The Luminary099 erasable padload (AGC_PLAN §4.7, Appendix D): cell catalog with addresses
//! verified against the yaYUL listing of `Luminary099/ERASABLE_ASSIGNMENTS.agc`, values from
//! `Luminary069/PADLOADS.agc` (the best in-tree template — no flown Apollo 11 erasable image
//! exists anywhere in the Virtual AGC tree), the resume-core writer, and the KSA-universe
//! audit (D-A6).
//!
//! Cells that are new in Luminary099 and undocumented in the 069 template (`GAINBRAK/GAINAPPR`,
//! `TCG*`, `DELTTFAP`, `V2FG`, `TAUVERT`, `LRVF`) are zeroed with comments — zero disables the
//! associated heuristics; they are V21-loadable in flight and flagged for the M-D validation
//! pass.

use crate::sim::Body;

/// AGC single-precision word: `value · 2^(14−b)`, one's-complement negative, 15 bits.
pub fn sp(value: f64, b: i32) -> u16 {
    let scaled = value * 2f64.powi(14 - b);
    let m = scaled.abs().round() as u32 & 0x3FFF;
    if scaled < 0.0 && m != 0 {
        (0o77777 ^ m) as u16
    } else {
        m as u16
    }
}

/// AGC double-precision pair: `value · 2^(28−b)` split 14/14, both words one's-complement for
/// negatives (the canonical same-sign DP form).
pub fn dp(value: f64, b: i32) -> [u16; 2] {
    let n = (value.abs() * 2f64.powi(28 - b)).round() as u64;
    let hi = ((n >> 14) & 0x3FFF) as u16;
    let lo = (n & 0x3FFF) as u16;
    if value < 0.0 && n != 0 {
        [0o77777 ^ hi, 0o77777 ^ lo]
    } else {
        [hi, lo]
    }
}

/// Decode a DP pair back to engineering units (test/round-trip aid).
pub fn dp_decode(words: [u16; 2], b: i32) -> f64 {
    let sgn = if words[0] & 0o40000 != 0 { -1.0 } else { 1.0 };
    let (hi, lo) = if sgn < 0.0 {
        ((0o77777 ^ words[0]) & 0x3FFF, (0o77777 ^ words[1]) & 0x3FFF)
    } else {
        (words[0] & 0x3FFF, words[1] & 0x3FFF)
    };
    sgn * ((hi as u64 * 16384 + lo as u64) as f64) * 2f64.powi(b - 28)
}

/// One padload cell: flat erasable address (bank·0400 + in-bank offset) + words.
pub struct Cell {
    pub name: &'static str,
    pub addr: u16,
    pub words: Vec<u16>,
}

/// Mission parameters resolved from `/sim` + config.
pub struct Mission {
    /// LM mass, kg (`mass/total` at padload time).
    pub lem_mass_kg: f64,
    /// CSM mass, kg (0 = undocked).
    pub csm_mass_kg: f64,
    /// Landing-site latitude/longitude, degrees (moon-fixed).
    pub site_lat_deg: f64,
    pub site_lon_deg: f64,
    /// Site radius, m (moon radius + site altitude).
    pub site_radius_m: f64,
    /// Nominal landing time, AGC-clock seconds (TLAND).
    pub tland_s: f64,
    /// Moon prime-meridian hour angle at AGC clock zero, revolutions [0,1) — becomes AZO.
    pub moon_phase_rev: f64,
}

/// Builds the full LM padload cell set.
pub fn lm_cells(m: &Mission) -> Vec<Cell> {
    let mut c: Vec<Cell> = Vec::new();
    let mut push = |name: &'static str, addr: u16, words: Vec<u16>| {
        c.push(Cell { name, addr, words });
    };

    // ---- masses + DAP (E2 unswitched) ----
    push("LEMMASS", 0o1331, vec![sp(m.lem_mass_kg, 16)]);
    push("CSMMASS", 0o1332, vec![sp(m.csm_mass_kg, 16)]);
    // DAPDATR1 left 0: the M-C card loads DAP config via V48/R03 (crew procedure, authentic).

    // ---- IMU compensation (E3,1452-1477): all zero = perfect IMU (plan §3.2 policy). ----
    push("PIPA/GYRO COMP", 0o1452, vec![0; 22]);

    // ---- time & planetary orientation (E3) ----
    // TEPHEM: AGC clock zero IS the padload epoch (t=0), triple precision csec.
    push("TEPHEM", 0o1706, vec![0, 0, 0]);
    // AZO: lunar prime-meridian phase at the epoch, revolutions in DP B0 (PADLOADS: ".7753
    // revolutions"). AXO/-AYO = 0: KSA's moon spins about the CCI pole exactly (§3.4).
    push("AZO", 0o1711, dp(m.moon_phase_rev, 0).to_vec());
    push("-AYO", 0o1713, vec![0, 0]);
    push("AXO", 0o1715, vec![0, 0]);

    // ---- W-matrix + lunar misc (E4): rendezvous/surface nav out of scope → zeros. ----
    push("WRENDPOS..WSURFVEL", 0o2000, vec![0; 8]);
    push("504LM", 0o2012, vec![0; 6]); // no libration in KSA
    push("AGSK", 0o2020, vec![0, 0]);

    // RLS: moon-fixed landing-site vector, meters, DP B-27 per component.
    let (lat, lon) = (m.site_lat_deg.to_radians(), m.site_lon_deg.to_radians());
    let r = m.site_radius_m;
    let rls = [
        r * lat.cos() * lon.cos(),
        r * lat.cos() * lon.sin(),
        r * lat.sin(),
    ];
    let mut rls_words = Vec::new();
    for comp in rls {
        rls_words.extend_from_slice(&dp(comp, 27));
    }
    push("RLS", 0o2022, rls_words);

    // ---- descent targets (E5; Luminary069 PADLOADS values — real Apollo 10/11-era targets).
    push("TLAND", 0o2400, dp(m.tland_s * 100.0, 28).to_vec()); // csec, DP integer scale
    let vec6 = |x: (f64, i32), y: (f64, i32), z: (f64, i32)| {
        let mut w = Vec::with_capacity(6);
        w.extend_from_slice(&dp(x.0, x.1));
        w.extend_from_slice(&dp(y.0, y.1));
        w.extend_from_slice(&dp(z.0, z.1));
        w
    };
    push("RBRFG", 0o2402, vec6((2.92362643e3, 24), (0.0, 24), (-1.00839629e4, 24)));
    push("VBRFG", 0o2410, vec6((-4.83907728e-1, 10), (0.0, 10), (1.71785605, 10)));
    push("ABRFG", 0o2416, vec6((-5.22722473e-5, -4), (0.0, -4), (-2.86621213e-4, -4)));
    push("VBRFG*", 0o2424, dp(3.86517612, 10).to_vec());
    push("ABRFG*", 0o2426, dp(-1.71972727e-3, -4).to_vec());
    push("JBRFG*", 0o2430, dp(-2.90216724e-8, -18).to_vec());
    // GAINBRAK/TCGFBRAK/TCGIBRAK: 099-new, undocumented in the 069 template — zeroed
    // [impl-verify at M-D; V21-loadable in flight].
    push("GAINBRAK/TCG*", 0o2432, vec![0; 4]);
    push("RAPFG", 0o2436, vec6((2.35092239e1, 24), (0.0, 24), (-5.28319999e-1, 24)));
    push("VAPFG", 0o2444, vec6((-9.44879999e-3, 10), (0.0, 10), (3.96239999e-3, 10)));
    push("AAPFG", 0o2452, vec6((1.52399999e-6, -4), (0.0, -4), (-1.98119999e-5, -4)));
    push("VAPFG*", 0o2460, dp(8.91539999e-3, 10).to_vec());
    push("AAPFG*", 0o2462, dp(-1.18871999e-4, -4).to_vec());
    push("JAPFG*", 0o2464, dp(8.37249023e-8, -18).to_vec());
    push("GAINAPPR/TCG*", 0o2466, vec![0; 4]);
    push("VIGN", 0o2472, dp(1.69952182e1, 10).to_vec());
    push("RIGNX", 0o2474, dp(-4.09432231e4, 24).to_vec());
    push("RIGNZ", 0o2476, dp(-4.40014934e5, 24).to_vec());
    push("KIGNX/B4", 0o2500, dp(-0.022499999, 4).to_vec());
    push("KIGNY/B8", 0o2502, dp(-0.174716605, 8).to_vec());
    push("KIGNV/B4", 0o2504, dp(-0.165939331, 4).to_vec());
    push("LOWCRIT", 0o2506, vec![0o4251]);
    push("HIGHCRIT", 0o2507, vec![0o4622]);
    // V2FG (P65 target velocity, m/cs, guidance frame: X up) = 4 ft/s descent; TAUVERT 0.
    push("V2FG", 0o2510, vec6((-0.0122, 10), (0.0, 10), (0.0, 10)));
    push("TAUVERT", 0o2516, vec![0, 0]);
    push("DELQFIX", 0o2520, dp(15.24, 24).to_vec()); // 50 ft altitude-update clamp
    // LR geometry/weights — MUST agree with radar.rs mount angles (D-A8).
    push("LRALPHA", 0o2522, vec![0o1042]); // 6°
    push("LRBETA1", 0o2523, vec![0o4210]); // 24°
    push("LRALPHA2", 0o2524, vec![0o1042]);
    push("LRBETA2", 0o2525, vec![0o0]);
    push("LRVMAX", 0o2526, vec![sp(0.047625, 0)]); // 2000 ft/s in m/cs B0-ish per PADLOADS
    push("LRVF", 0o2527, vec![sp(0.047625, 0)]); // 099-new; = LRVMAX [impl-verify]
    push("LRWVZ", 0o2530, vec![sp(0.7, 0)]);
    push("LRWVY", 0o2531, vec![sp(0.7, 0)]);
    push("LRWVX", 0o2532, vec![sp(0.4, 0)]);

    // ---- ascent + R03 trim times (E6) ----
    push("HIASCENT", 0o3000, vec![sp(5050.0, 16)]);
    push("ROLLTIME", 0o3001, vec![sp(3000.0, 14)]); // 30 s in csec (integer)
    push("PITTIME", 0o3002, vec![sp(3000.0, 14)]);

    // ---- landing-phase criteria (E7) ----
    push("LRHMAX", 0o3420, vec![sp(15240.0, 14)]); // 50,000 ft in m (integer SP)
    push("LRWH", 0o3421, vec![sp(0.35, 0)]);
    push("TENDBRAK", 0o3423, vec![sp(2000.0, 17)]); // 20 s (two-phase) csec B-17
    push("TENDAPPR", 0o3424, vec![sp(1000.0, 17)]); // 10 s csec B-17
    push("DELTTFAP/LEADTIME", 0o3425, vec![0, 0]); // 099-new — zeroed [impl-verify]
    push("TNEWA", 0o3431, vec![0, 0]);

    c
}

/// Writes a **full-format** resume-core file — the layout `yaAGC` reads for an explicit
/// core-resume argument (`agc_engine_init` with `AllOrErasable=1`, verified in
/// `agc_engine_init.c:330-428`): 512 channel words, 8 banks × 0400 erasable words, then the
/// CPU-state scalars — everything `%o` text, one value per line. Channels and CPU state carry
/// the engine's own cold-start values (ch 030 = 037777, 031-033 = 077777 — active-low idle;
/// Z = 04000 so the rope boots through the normal fresh-start vector; `AllowInterrupt` = 1).
pub fn write_core(cells: &[Cell], path: &std::path::Path) -> std::io::Result<()> {
    let mut erasable = vec![0u16; 8 * 0o400];
    for cell in cells {
        for (i, w) in cell.words.iter().enumerate() {
            let a = cell.addr as usize + i;
            if a < erasable.len() {
                erasable[a] = *w & 0o77777;
            }
        }
    }
    erasable[0o5] = 0o4000; // RegZ — the fresh-start program counter

    let mut out = String::with_capacity((512 + erasable.len() + 40) * 7);
    // 512 i/o channel words (cold-start idle values).
    for ch in 0..512usize {
        let v = match ch {
            0o30 => 0o37777,
            0o31 | 0o32 | 0o33 => 0o77777,
            _ => 0,
        };
        out.push_str(&format!("{v:06o}\n"));
    }
    for w in &erasable {
        out.push_str(&format!("{w:06o}\n"));
    }
    // CPU state, in MakeCoreDump order: CycleCounter, ExtraCode, AllowInterrupt, PendFlag,
    // PendDelay, ExtraDelay, OutputChannel7, OutputChannel10[16], IndexValue,
    // InterruptRequests[0..=10], InIsr, SubstituteInstruction, DownruptTimeValid,
    // DownruptTime, Downlink.
    out.push_str("0\n0\n1\n0\n0\n0\n0\n"); // CycleCounter..OutputChannel7
    for _ in 0..16 {
        out.push_str("0\n"); // OutputChannel10
    }
    out.push_str("0\n"); // IndexValue
    for _ in 0..11 {
        out.push_str("0\n"); // InterruptRequests (loader re-arms DOWNRUPT itself)
    }
    out.push_str("0\n0\n1\n0\n0\n"); // InIsr, SubstituteInstruction, DownruptTimeValid=1, DownruptTime, Downlink
    std::fs::write(path, out)
}

/// The KSA-universe fit report (D-A6): audits the live moon against Luminary's fixed-in-rope
/// constants. Green within 1%, yellow within 5%, red beyond (reassembly / conformal map
/// territory — AGC_PLAN §6.2 / Appendix F).
pub struct Audit {
    pub lines: Vec<String>,
    pub worst: f64,
}

/// Luminary099 `CONTROLLED_CONSTANTS.agc`: MUM = 4.9027780e12 m³/s², 504RM = 1,738,090 m.
pub const MUM: f64 = 4.9027780e12;
pub const RM_504: f64 = 1_738_090.0;
/// The real lunar sidereal rotation rate the rope's orientation series assumes, rad/s.
pub const LUNAR_OMEGA: f64 = 2.6617e-6;

pub fn audit_moon(body: &Body) -> Audit {
    let mut lines = Vec::new();
    let mut worst: f64 = 0.0;
    let mut row = |name: &str, ksa: f64, rope: f64| {
        let err = if rope != 0.0 { ((ksa - rope) / rope).abs() } else { 0.0 };
        worst = worst.max(err);
        let flag = if err < 0.01 {
            "OK"
        } else if err < 0.05 {
            "WARN"
        } else {
            "BAD"
        };
        lines.push(format!(
            "{name:<16} KSA {ksa:.6e}  rope {rope:.6e}  err {:.2}%  [{flag}]",
            err * 100.0
        ));
    };
    row("mu (m^3/s^2)", body.mu, MUM);
    row("radius (m)", body.radius, RM_504);
    row("rot rate (rad/s)", body.rotation_rate, LUNAR_OMEGA);
    Audit { lines, worst }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// SP encodings pinned against hand-computed PADLOADS.agc examples.
    #[test]
    fn sp_matches_known_octals() {
        assert_eq!(sp(2000.0, 17), 0o372, "TENDBRAK: 20 s → 250");
        assert_eq!(sp(0.35, 0), 0o13146, "LRWH .35 → 5734");
        assert_eq!(sp(3000.0, 14), 0o5670, "ROLLTIME integer 3000");
        // One's complement negative: −250 = 77777 ^ 372.
        assert_eq!(sp(-2000.0, 17), 0o77777 ^ 0o372);
    }

    #[test]
    fn dp_round_trips() {
        for (x, b) in [
            (1.69952182e1, 10),
            (-4.09432231e4, 24),
            (-0.022499999, 4),
            (15.24, 24),
            (-2.90216724e-8, -18),
            (0.7753, 0),
        ] {
            let got = dp_decode(dp(x, b), b);
            let tol = 2f64.powi(b - 27); // one LSB
            assert!((got - x).abs() <= tol, "{x} B{b} → {got}");
        }
    }

    #[test]
    fn core_file_shape_and_cells_land() {
        let m = Mission {
            lem_mass_kg: 15_000.0,
            csm_mass_kg: 0.0,
            site_lat_deg: 0.674,
            site_lon_deg: 23.473,
            site_radius_m: 1_737_400.0,
            tland_s: 3600.0,
            moon_phase_rev: 0.25,
        };
        let cells = lm_cells(&m);
        let dir = std::env::temp_dir().join(format!("agc-pad-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("padload.core");
        write_core(&cells, &p).unwrap();
        let text = std::fs::read_to_string(&p).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 512 + 8 * 0o400 + 40, "channels + erasable + CPU state");
        // Channels: ch 030 idle = 037777 (active-low, temp-in-limits bit 15 present).
        assert_eq!(lines[0o30], "037777");
        assert_eq!(lines[0o33], "077777");
        let e = |addr: usize| 512 + addr; // erasable section offset
        // Z = fresh-start vector.
        assert_eq!(lines[e(0o5)], "004000");
        // LOWCRIT at flat 0o2506 must hold 04251.
        assert_eq!(lines[e(0o2506)], "004251");
        assert_eq!(lines[e(0o2507)], "004622");
        // LEMMASS: 15000 kg B16 → 15000/4 = 3750.
        assert_eq!(u16::from_str_radix(lines[e(0o1331)], 8).unwrap(), 3750);
        // RLS x-component decodes back to ~site vector x.
        let w0 = u16::from_str_radix(lines[e(0o2022)], 8).unwrap();
        let w1 = u16::from_str_radix(lines[e(0o2023)], 8).unwrap();
        let x = dp_decode([w0, w1], 27);
        let want = 1_737_400.0 * 0.674f64.to_radians().cos() * 23.473f64.to_radians().cos();
        assert!((x - want).abs() < 10.0, "RLS.x {x} vs {want}");
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn audit_flags_mismatches() {
        let good = Body { mu: MUM, radius: RM_504, rotation_rate: LUNAR_OMEGA };
        assert!(audit_moon(&good).worst < 0.01);
        let bad = Body { mu: MUM * 1.2, ..good };
        assert!(audit_moon(&bad).worst > 0.05);
    }
}
