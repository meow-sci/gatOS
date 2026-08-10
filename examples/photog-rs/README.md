# photog-rs

`photog-rs` is a standalone Rust/ratatui editor for cinematic gatOS camera takes. It edits a small,
versioned project JSON rather than exposing the native track format as a general keyframe graph. Five
presets cover the useful grammar: orbit, dolly, chase/attach, static, and lens-only.

The editor compiles each take into two live artifacts:

- `camera/track/<unique-name>` — the authoritative native JSON track. Position, orbit, FOV, and roll
  interpolate on every rendered frame.
- `ctl/timed_batch` — a shared-clock cue player for shot-boundary smoothing/projection changes and,
  for a non-looping take, the final track stop plus eased camera release.

Both players start at rate `0` in one unique group. photog waits until `camera` and the cue player are
visible under `ctl/schedules/`, then changes the shared rate to `1` (or the selected editor rate). This
keeps the two independently-uploaded artifacts on exactly one clock with no startup skew.

## Run

Inside the gatOS guest, `/sim` is the default:

```sh
cd /mnt/source/examples/photog-rs
cargo run --release -- projects/example-trailer.photog.json
```

Host-side HTTP and fixture modes use the same UI:

```sh
cargo run --release -- projects/example-trailer.photog.json \
  --url http://127.0.0.1:4242/v1
cargo run -- --root test-fixture projects/example-trailer.photog.json
```

Resolution order without a transport flag is a mounted `/sim`, then `$GATOS_HTTP`, then `/sim` so an
unconfigured run has an explicit offline state. Project saves are local and atomic (temporary sibling
plus rename); uploaded camera tracks are deployment state, not project persistence.

## Controls

| Task | Keyboard | Mouse |
|---|---|---|
| Select shot / field | `↑`/`↓`, `Tab`/`Shift-Tab` | wheel or click |
| Edit field / choose modal item | `Enter`; type; `Enter` | click field, then option |
| Add / duplicate / delete | `a` / `y` / `x` | add button; edit list by keyboard |
| Reorder / rename | `J` / `K`; `r` | select then use the same commands |
| Save / save-as / reload | `s` / `S` / `R` | save button |
| Play all / from selected / preview selected | `p` / `P` / `v` | play / preview buttons |
| Pause, rate, scrub, live loop | `Space`, `[` `]`, `,` `.`, `l` | playback remains keyboard-precise |
| Stop with eased release | `z` | stop button |
| Hard emergency release | `!` | — |
| Refresh targets / help / quit | `g` / `?` / `q` | target fields use live picker |

Scalar animation is entered as `start..end`; one number is a constant. Vectors are `x y z`. A failed
validation leaves the live camera untouched and names the first invalid project field.

## Frames and targeting

- `ecl`: absolute ecliptic coordinates; the only placement frame that needs no anchor.
- `cce`: ecliptic axes centered on the anchor.
- `bodyfixed`: vessel body axes (`+X` nose, `+Y` right, **`-Z` up**) or a body's rotating frame.
- `enu`: local east/north/up.
- `lvlh`: local orbital frame.
- `chase`: vessel-relative chase/body frame; it cannot use a body anchor.

Aim offsets use their own frame and are re-resolved on the target every frame. v1 intentionally offers
only `vessel:`, `body:`, and `none`; `part:` aim targets and raw quaternion authoring are outside this
editor's first version. Vessel/body pickers come from the live simulation.

## Ownership and limitations

photog uses cinematic anchor cuts while gatOS retains one fixed-camera ownership session. It never
tries to own Map or IVA: KSA's controllers overwrite those poses and gatOS deliberately has no Harmony
patch for them. While owned, stock camera keys and `camera/mode|follow|tidal` are inert; an EVA kitten's
walking direction follows the authored camera basis. The world camera still observes KSA's
surface-plus-0.5 m floor.

Orthographic mode itself is restored, but KSA exposes no getter for the hidden orthographic half-height.
The compiler and status line warn whenever a project writes it. Looping takes stay owned until Stop;
non-looping takes stop and perform the configured eased release at their final cue.

If the terminal or editor is interrupted, the worker makes a best-effort stop/release. The unconditional
manual recovery remains:

```sh
echo 1 > /sim/camera/release
# or over HTTP:
curl -X POST --data 1 "$GATOS_HTTP/fs/camera/release"
```

## Verification

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
```

Golden tests compile [`projects/example-trailer.photog.json`](projects/example-trailer.photog.json)
into the exact native track and timed-batch sidecar. This example consumes the existing published API;
it adds no `/sim`, HTTP, or MQTT surface.
