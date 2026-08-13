//! fx — burst face-anchored particle effects on kitten vessels via `/sim/debug/fx/`.
//!
//! ```text
//! fx party                       # confetti on every kitten
//! fx danger Hunter               # fire flash on Hunter
//! fx death Hunter --scale 2      # a big grey puff
//! fx sparkle --bursts 5          # a 5-volley glitter salvo
//! fx list                        # profiles + live emitter count
//! fx clear                       # stop everything now
//! ```

use std::thread::sleep;
use std::time::Duration;

use fx::{check_source, discover_kittens, profiles, source_from, spawn};

fn main() {
    let mut args = std::env::args().skip(1).peekable();

    let mut command: Option<String> = None;
    let mut vessels: Vec<String> = Vec::new();
    let mut scale = 1.0f64;
    let mut offset: Option<[f64; 3]> = None;
    let mut bursts = 1u32;
    let mut interval = 0.35f64;
    let mut sim: Option<String> = None;
    let mut url: Option<String> = None;

    while let Some(arg) = args.next() {
        let mut take = |name: &str| -> String {
            args.next().unwrap_or_else(|| {
                eprintln!("fx: {name} needs a value");
                std::process::exit(2);
            })
        };
        match arg.as_str() {
            "-h" | "--help" => {
                print_help();
                return;
            }
            "--scale" | "-s" => scale = parse_f64(&take("--scale"), "--scale"),
            "--offset" => {
                let raw = take("--offset");
                let parts: Vec<f64> = raw
                    .split(',')
                    .map(|p| parse_f64(p.trim(), "--offset"))
                    .collect();
                if parts.len() != 3 {
                    eprintln!("fx: --offset needs 'x,y,z' (metres, assembly frame)");
                    std::process::exit(2);
                }
                offset = Some([parts[0], parts[1], parts[2]]);
            }
            "--bursts" | "-b" => {
                bursts = take("--bursts").parse().unwrap_or_else(|_| {
                    eprintln!("fx: --bursts needs a positive integer");
                    std::process::exit(2);
                })
            }
            "--interval" | "-i" => interval = parse_f64(&take("--interval"), "--interval"),
            "--sim" => sim = Some(take("--sim")),
            "--url" => url = Some(take("--url")),
            other if other.starts_with('-') => {
                eprintln!("fx: unknown option {other}");
                std::process::exit(2);
            }
            word if command.is_none() => command = Some(word.to_string()),
            vessel => vessels.push(vessel.to_string()),
        }
    }

    let Some(command) = command else {
        print_help();
        std::process::exit(2);
    };

    let source = source_from(sim, url);
    if let Err(lines) = check_source(source.as_ref()) {
        for line in lines {
            eprintln!("fx: {line}");
        }
        std::process::exit(1);
    }

    match command.as_str() {
        "list" => {
            println!("profiles: {}", profiles(source.as_ref()).join(", "));
            let count = source
                .read("debug/fx/count")
                .map(|s| s.trim().to_string())
                .unwrap_or_else(|| "?".into());
            println!("live: {count}");
        }
        "clear" => match source.write("debug/fx/clear", "1") {
            Ok(()) => eprintln!("fx: cleared."),
            Err(e) => {
                eprintln!("fx: clear failed: {e}");
                std::process::exit(1);
            }
        },
        profile => {
            let known = profiles(source.as_ref());
            if !known.iter().any(|p| p == profile) {
                eprintln!("fx: unknown profile '{profile}' (known: {})", known.join(", "));
                std::process::exit(2);
            }
            if vessels.is_empty() {
                vessels = discover_kittens(source.as_ref());
                if vessels.is_empty() {
                    eprintln!("fx: no vessels given and no kittens found; name one: fx {profile} Hunter");
                    std::process::exit(1);
                }
                eprintln!("fx: targets: {}", vessels.join(", "));
            }
            for burst in 0..bursts {
                if burst > 0 {
                    sleep(Duration::from_secs_f64(interval.max(0.05)));
                }
                for vessel in &vessels {
                    if let Err(e) = spawn(source.as_ref(), vessel, profile, scale, offset) {
                        eprintln!("fx: {vessel}: spawn failed: {e}");
                    }
                }
            }
            eprintln!(
                "fx: {profile} x{bursts} on {} vessel(s).",
                vessels.len()
            );
        }
    }
}

fn parse_f64(s: &str, name: &str) -> f64 {
    s.parse().unwrap_or_else(|_| {
        eprintln!("fx: {name} needs a number, got '{s}'");
        std::process::exit(2);
    })
}

fn print_help() {
    println!(
        "fx — face-anchored particle effects on kitten vessels (gatOS /sim/debug/fx)

USAGE:
    fx <profile> [VESSEL_ID]... [OPTIONS]     burst an effect (no vessels = every kitten)
    fx list                                   profiles + live emitter count
    fx clear                                  stop every gatOS effect now

PROFILES:
    party      confetti burst (celebrations)
    sparkle    gold glitter (small wins)
    danger     fire flash (trouble)
    death      slow grey puff (the end)

OPTIONS:
    -s, --scale <x>      size/velocity multiplier                     [default: 1]
        --offset <x,y,z> anchor override, metres, vessel assembly frame
                         (kittens default to their face)
    -b, --bursts <n>     repeat the burst n times                     [default: 1]
    -i, --interval <s>   seconds between repeats                      [default: 0.35]
        --sim <path>     the /sim mount root        [default: /sim, env: GATOS_SIM]
        --url <base>     use HTTP /v1/fs at <base>              [env: GATOS_HTTP]
                         (with or without the /v1 suffix; the mount wins when it is up)
    -h, --help           this text

EXAMPLES:
    fx party
    fx danger Hunter --bursts 3
    fx death Hunter --scale 2

Requires gatOS with debug_namespace = true, and the game's graphics Particles
setting on.
"
    );
}
