//! celebrate — the one-word party button: a confetti-and-glitter volley on every kitten.
//!
//! ```text
//! celebrate                  # 6 alternating party/sparkle bursts on every kitten
//! celebrate Hunter           # just Hunter
//! celebrate --volleys 10 --interval 0.25 --scale 1.5
//! ```
//!
//! Sugar over the same `/sim/debug/fx/` surface the `fx` binary drives.

use std::thread::sleep;
use std::time::Duration;

use fx::{check_source, discover_kittens, source_from, spawn};

fn main() {
    let mut args = std::env::args().skip(1);

    let mut vessels: Vec<String> = Vec::new();
    let mut volleys = 6u32;
    let mut interval = 0.35f64;
    let mut scale = 1.0f64;
    let mut sim: Option<String> = None;
    let mut url: Option<String> = None;

    while let Some(arg) = args.next() {
        let mut take = |name: &str| -> String {
            args.next().unwrap_or_else(|| {
                eprintln!("celebrate: {name} needs a value");
                std::process::exit(2);
            })
        };
        match arg.as_str() {
            "-h" | "--help" => {
                println!(
                    "celebrate — a confetti-and-glitter volley on every kitten (gatOS /sim/debug/fx)

USAGE:
    celebrate [VESSEL_ID]... [--volleys <n>] [--interval <s>] [--scale <x>]
              [--sim <path>] [--url <base>]

No vessel ids = every kitten in the world (via the is_kitten leaf). Alternates the
party and sparkle profiles, one volley per beat. Defaults: 6 volleys, 0.35 s apart."
                );
                return;
            }
            "--volleys" | "-v" => {
                volleys = take("--volleys").parse().unwrap_or_else(|_| {
                    eprintln!("celebrate: --volleys needs a positive integer");
                    std::process::exit(2);
                })
            }
            "--interval" | "-i" => {
                interval = take("--interval").parse().unwrap_or_else(|_| {
                    eprintln!("celebrate: --interval needs a number");
                    std::process::exit(2);
                })
            }
            "--scale" | "-s" => {
                scale = take("--scale").parse().unwrap_or_else(|_| {
                    eprintln!("celebrate: --scale needs a number");
                    std::process::exit(2);
                })
            }
            "--sim" => sim = Some(take("--sim")),
            "--url" => url = Some(take("--url")),
            other if other.starts_with('-') => {
                eprintln!("celebrate: unknown option {other}");
                std::process::exit(2);
            }
            vessel => vessels.push(vessel.to_string()),
        }
    }

    let source = source_from(sim, url);
    if let Err(lines) = check_source(source.as_ref()) {
        for line in lines {
            eprintln!("celebrate: {line}");
        }
        std::process::exit(1);
    }

    if vessels.is_empty() {
        vessels = discover_kittens(source.as_ref());
        if vessels.is_empty() {
            eprintln!("celebrate: no kittens found and no vessel named; try: celebrate Hunter");
            std::process::exit(1);
        }
    }
    eprintln!("celebrate: {} 🎉", vessels.join(", "));

    for volley in 0..volleys {
        if volley > 0 {
            sleep(Duration::from_secs_f64(interval.max(0.05)));
        }
        let profile = if volley % 2 == 0 { "party" } else { "sparkle" };
        for vessel in &vessels {
            if let Err(e) = spawn(source.as_ref(), vessel, profile, scale, None) {
                eprintln!("celebrate: {vessel}: {e}");
            }
        }
    }
    eprintln!("celebrate: done. deal with it.");
}
