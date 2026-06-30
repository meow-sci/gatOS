//! starwars — reads text from stdin and scrolls it bottom-to-top like the *Star Wars* opening
//! crawl, rendered as braille dots so the motion is smooth instead of jumping a whole terminal row
//! at a time. No 3D perspective is simulated here: pair this with a purrTTY terminal window already
//! rotated/tilted in the game's 3D space and this program just needs to be the flat scrolling text.
//!
//! Architecture: [`canvas`] rasterizes the (vendored Star Jedi TTF) text into a tall dot-coverage
//! bitmap once per terminal size; [`braille`] downsamples a 2x4-dot window of it into terminal
//! cells each frame; [`app`] owns the scroll position and rebuild-on-resize logic; [`ui`] wires that
//! into a ratatui `Paragraph` every tick.

mod app;
mod braille;
mod canvas;
mod ui;

use std::io::{self, Read, Stdout};
use std::time::{Duration, Instant};

use ratatui::backend::CrosstermBackend;
use ratatui::crossterm::event::{self, Event, KeyEventKind};
use ratatui::crossterm::execute;
use ratatui::crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use ratatui::style::Color;
use ratatui::Terminal;

use app::App;

type Tui = Terminal<CrosstermBackend<Stdout>>;

fn main() -> io::Result<()> {
    let config = match Config::from_args() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("starwars: {e}");
            std::process::exit(2);
        }
    };
    if config.help {
        print_help();
        return Ok(());
    }

    let mut text = String::new();
    io::stdin().read_to_string(&mut text)?;

    let app = App::new(
        text,
        config.uppercase,
        config.color,
        config.threshold,
        config.speed,
        config.font_px,
        config.margin_pct,
        config.loop_mode,
    );

    let mut terminal = setup_terminal()?;
    install_panic_hook();
    let result = run(&mut terminal, app, config.fps);
    restore_terminal(&mut terminal)?;
    result
}

struct Config {
    color: Color,
    threshold: u8,
    speed: f64,
    font_px: f32,
    margin_pct: f64,
    fps: u32,
    uppercase: bool,
    loop_mode: bool,
    help: bool,
}

impl Config {
    fn from_args() -> Result<Self, String> {
        let mut color = Color::Rgb(255, 217, 67); // classic crawl yellow
        let mut threshold: u8 = 90;
        let mut speed: f64 = 6.0;
        let mut font_px: f32 = 24.0;
        let mut margin_pct: f64 = 12.0;
        let mut fps: u32 = 30;
        let mut uppercase = false;
        let mut loop_mode = false;
        let mut help = false;

        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--color" => {
                    let v = args.next().ok_or("--color needs a value")?;
                    color = parse_color(&v)?;
                }
                "--threshold" => {
                    let v = args.next().ok_or("--threshold needs a value")?;
                    threshold = v
                        .parse()
                        .map_err(|_| format!("bad --threshold '{v}' (0-255)"))?;
                }
                "--speed" => {
                    let v = args.next().ok_or("--speed needs a value")?;
                    speed = v
                        .parse()
                        .map_err(|_| format!("bad --speed '{v}' (dots/sec)"))?;
                }
                "--font-size" => {
                    let v = args.next().ok_or("--font-size needs a value")?;
                    font_px = v
                        .parse()
                        .map_err(|_| format!("bad --font-size '{v}' (dots)"))?;
                }
                "--margin" => {
                    let v = args.next().ok_or("--margin needs a value")?;
                    margin_pct = v
                        .parse()
                        .map_err(|_| format!("bad --margin '{v}' (0-45)"))?;
                }
                "--fps" => {
                    let v = args.next().ok_or("--fps needs a value")?;
                    fps = v.parse().map_err(|_| format!("bad --fps '{v}'"))?;
                }
                "--uppercase" => uppercase = true,
                "--loop" => loop_mode = true,
                "-h" | "--help" => help = true,
                other => return Err(format!("unknown argument '{other}' (try --help)")),
            }
        }

        Ok(Self {
            color,
            threshold,
            speed,
            font_px,
            margin_pct,
            fps: fps.clamp(1, 120),
            uppercase,
            loop_mode,
            help,
        })
    }
}

fn parse_color(s: &str) -> Result<Color, String> {
    if let Some(hex) = s.strip_prefix('#') {
        if hex.len() == 6 {
            let r = u8::from_str_radix(&hex[0..2], 16);
            let g = u8::from_str_radix(&hex[2..4], 16);
            let b = u8::from_str_radix(&hex[4..6], 16);
            if let (Ok(r), Ok(g), Ok(b)) = (r, g, b) {
                return Ok(Color::Rgb(r, g, b));
            }
        }
        return Err(format!("bad --color hex '{s}' (want #rrggbb)"));
    }
    match s.to_ascii_lowercase().as_str() {
        "yellow" => Ok(Color::Rgb(255, 217, 67)),
        "white" => Ok(Color::Rgb(255, 255, 255)),
        "red" => Ok(Color::Rgb(255, 64, 64)),
        "green" => Ok(Color::Rgb(64, 255, 96)),
        "blue" => Ok(Color::Rgb(96, 160, 255)),
        "cyan" => Ok(Color::Rgb(64, 230, 255)),
        "magenta" => Ok(Color::Rgb(255, 96, 220)),
        "orange" => Ok(Color::Rgb(255, 160, 64)),
        other => Err(format!("unknown --color '{other}' (name, or #rrggbb)")),
    }
}

fn print_help() {
    println!("starwars \u{2014} scrolls stdin bottom-to-top as a smooth braille-rendered crawl");
    println!();
    println!("USAGE: <some text source> | starwars [options]");
    println!();
    println!("  --color <name|#rrggbb>  text color (default: yellow)");
    println!("  --speed <dots/sec>      scroll speed (default: 6)");
    println!("  --font-size <dots>      glyph size (default: 24)");
    println!("  --margin <0-45>         left/right margin, percent of width (default: 12)");
    println!(
        "  --threshold <0-255>     coverage cutoff for a \u{201c}lit\u{201d} dot (default: 90)"
    );
    println!("  --fps <n>               redraw rate (default: 30)");
    println!(
        "  --uppercase             force input to upper case (the bundled font is caps-first)"
    );
    println!("  --loop                  restart from the bottom when the crawl finishes");
    println!();
    println!(
        "Reads all of stdin to EOF before starting, then scrolls until it exits past the top."
    );
    println!("Keys: q / Esc / Ctrl+C to quit early.");
}

fn run(terminal: &mut Tui, mut app: App, fps: u32) -> io::Result<()> {
    let frame_dt = Duration::from_secs_f64(1.0 / fps as f64);
    let mut last = Instant::now();
    loop {
        terminal.draw(|f| ui::render(f, &mut app))?;

        if event::poll(frame_dt)? {
            if let Event::Key(k) = event::read()? {
                if k.kind == KeyEventKind::Press && app.on_key(k) {
                    return Ok(());
                }
            }
        }

        let now = Instant::now();
        app.tick(now - last);
        last = now;

        if app.finished() {
            return Ok(());
        }
    }
}

fn setup_terminal() -> io::Result<Tui> {
    enable_raw_mode()?;
    let mut stdout = io::stdout();
    execute!(stdout, EnterAlternateScreen)?;
    Terminal::new(CrosstermBackend::new(stdout))
}

fn restore_terminal(terminal: &mut Tui) -> io::Result<()> {
    disable_raw_mode()?;
    execute!(terminal.backend_mut(), LeaveAlternateScreen)?;
    terminal.show_cursor()
}

/// Restore the terminal on panic so a crash doesn't leave the user in raw-mode/alt-screen.
fn install_panic_hook() {
    let original = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let _ = disable_raw_mode();
        let _ = execute!(io::stdout(), LeaveAlternateScreen);
        original(info);
    }));
}
