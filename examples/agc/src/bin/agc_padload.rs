//! `agc-padload` — generates the Luminary099 erasable padload from the live universe
//! (AGC_PLAN §4.7, D-A5/D-A6): reads `/sim` (masses, moon constants, hour angle), prints the
//! KSA-fit audit, and writes the resume-core file `agc start` hands to yaAGC. Can also emit a
//! V71 state-vector uplink script (`--statevec=FILE`) for the pre-PDI resync.

use agc::padload::{self, Mission};
use agc::sim::{self, FsSource, HttpSource, Source};
use agc::uplink;

fn main() {
    let mut out = "padload.core".to_string();
    let mut root = "/sim".to_string();
    let mut url: Option<String> = None;
    let mut vehicle = "lm".to_string();
    // Tranquility Base (Apollo 11): 0.6741°N 23.4730°E.
    let mut site_lat = 0.6741_f64;
    let mut site_lon = 23.4730_f64;
    let mut tland_s = 0.0_f64;
    let mut statevec: Option<String> = None;

    for a in std::env::args().skip(1) {
        if let Some(v) = a.strip_prefix("--out=") {
            out = v.into();
        } else if let Some(v) = a.strip_prefix("--root=") {
            root = v.into();
        } else if let Some(v) = a.strip_prefix("--url=") {
            url = Some(v.into());
        } else if let Some(v) = a.strip_prefix("--vehicle=") {
            vehicle = v.into();
        } else if let Some(v) = a.strip_prefix("--site-lat=") {
            site_lat = v.parse().unwrap_or(site_lat);
        } else if let Some(v) = a.strip_prefix("--site-lon=") {
            site_lon = v.parse().unwrap_or(site_lon);
        } else if let Some(v) = a.strip_prefix("--tland=") {
            tland_s = v.parse().unwrap_or(0.0);
        } else if let Some(v) = a.strip_prefix("--statevec=") {
            statevec = Some(v.into());
        } else if a == "--help" || a == "-h" {
            println!(
                "agc-padload [--vehicle=lm] [--out=padload.core] [--root=/sim | --url=…]\n\
                 [--site-lat=0.6741] [--site-lon=23.4730] [--tland=SECONDS]\n\
                 [--statevec=FILE]  (also emit a V71 state-vector uplink key script)"
            );
            return;
        }
    }

    if vehicle != "lm" {
        eprintln!("note: only the LM (Luminary099) padload is generated; the CM cold-starts");
    }

    let source: Box<dyn Source> = match url {
        Some(u) => Box::new(HttpSource::new(u)),
        None => Box::new(FsSource::new(&root)),
    };
    let tick = sim::poll(&*source);
    let telemetry = tick.telemetry;
    let body = tick.body.unwrap_or(agc::sim::Body {
        mu: padload::MUM,
        radius: padload::RM_504,
        rotation_rate: padload::LUNAR_OMEGA,
    });

    // ---- the KSA-universe fit audit (D-A6) ----
    println!("== KSA-universe audit (moon vs Luminary099 rope constants) ==");
    let audit = padload::audit_moon(&body);
    for line in &audit.lines {
        println!("  {line}");
    }
    if audit.worst > 0.05 {
        println!("  VERDICT: RED — consider rope reassembly (AGC_PLAN §6.2) or App. F scaling");
    } else if audit.worst > 0.01 {
        println!("  VERDICT: YELLOW — flyable; LR updates + pre-PDI uplink carry the error");
    } else {
        println!("  VERDICT: GREEN");
    }

    // Moon prime-meridian hour angle at 'now' from the active vessel: θ = atan2(y,x) − lon.
    let moon_phase_rev = match (&telemetry, sim::read_lat_lon(&*source)) {
        (Some(t), Some((_lat, lon))) => {
            let theta = t.pos_cci[1].atan2(t.pos_cci[0]) - lon.to_radians();
            (theta / std::f64::consts::TAU).rem_euclid(1.0)
        }
        _ => 0.0,
    };

    let mission = Mission {
        lem_mass_kg: telemetry.as_ref().map(|t| t.mass.t).unwrap_or(15_000.0),
        csm_mass_kg: 0.0,
        site_lat_deg: site_lat,
        site_lon_deg: site_lon,
        site_radius_m: body.radius,
        tland_s: if tland_s > 0.0 {
            tland_s
        } else {
            telemetry.as_ref().map(|t| t.ut + 3600.0).unwrap_or(3600.0)
        },
        moon_phase_rev,
    };

    let cells = padload::lm_cells(&mission);
    match padload::write_core(&cells, std::path::Path::new(&out)) {
        Ok(()) => println!(
            "padload: {} cells → {out} (mass {:.0} kg, site {:.4}°/{:.4}°, TLAND {:.0}s, AZO {:.4} rev)",
            cells.len(),
            mission.lem_mass_kg,
            site_lat,
            site_lon,
            mission.tland_s,
            moon_phase_rev
        ),
        Err(e) => {
            eprintln!("padload: write {out}: {e}");
            std::process::exit(1);
        }
    }

    // ---- optional V71 state-vector uplink script (pre-PDI / post-pause resync) ----
    if let (Some(path), Some(t)) = (statevec, telemetry) {
        // UPSVFLAG block (UPDATE_PROGRAM.agc:122-138): V71E 21E <ECADR UPSVFLAG>E, id word
        // (77775 = LEM, lunar sphere), 7 DP pairs: pos XYZ (2^27 m), vel XYZ (2^5 m/cs),
        // time (2^28 csec) — the lunar-sphere state-vector scalings. [impl-verify at the M-D
        // pre-PDI pass.]
        const UPSVFLAG_ECADR: u16 = 0o1501; // E3,1501, verified in the yaYUL listing
        let mut data: Vec<u16> = vec![0o77775];
        for p in t.pos_cci {
            data.extend_from_slice(&padload::dp(p, 27));
        }
        for v in t.vel_cci {
            data.extend_from_slice(&padload::dp(v / 100.0, 5)); // m/s → m/cs, scale 2^5
        }
        data.extend_from_slice(&padload::dp(t.ut * 100.0, 28));
        let script = uplink::v71_block(UPSVFLAG_ECADR, &data);
        match std::fs::write(&path, format!("{script}\n")) {
            Ok(()) => println!("statevec: V71 script → {path} (send with: agc uplink {path})"),
            Err(e) => eprintln!("statevec: {path}: {e}"),
        }
    }
}
