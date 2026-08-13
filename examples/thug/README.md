# thug — thug-life glasses, the trivial way

A small Rust CLI over the gatOS `/sim/debug/thug_life/` surface: it finds (or creates) the
part-anchored sunglasses quad for a vessel and slides it down onto the face with an eased
animation. Successor to `examples/thug-life/thug.ts`, with sensible defaults — a kitten gets
shades with zero configuration.

```bash
thug Hunter                 # shades on Hunter, all defaults
thug                        # shades on EVERY kitten currently in the world
thug Hunter Polaris Banjo   # the whole squad, animated in lockstep
thug --time 3 --easing linear --scale 1.5 Polaris
thug --end-pos 0.25,0.01,-0.30 Hunter          # where they come to rest
thug --start-pos -2,0,0 Hunter                 # where the slide begins
thug --cameras crew Hunter  # visible only in the crew-portrait face cams
thug --off Hunter           # slide them off and remove the entry
```

## What it does

1. Resolves the vessel's root part (`/sim/vessels/by-id/<id>/parts/0/instance_id`), or uses
   `--part <iid>`. When that leaf is missing because the parts list is not being sampled
   (`telemetry_vessel_parts = false`), it anchors to the vehicle frame (part iid `0`) and says so.
2. Creates the entry via `/sim/debug/thug_life/add`, seeded at the start pose (reuses an existing
   entry for the vessel if one is live).
3. Writes `<entry>/position` at `--hz` for `--time` seconds, easing from `--start-pos` to
   `--end-pos` on all three axes — rotation `(90, 180, 90)`, size `0.9 × 0.22 m` unless
   overridden. Multiple vessels animate in the same loop, so the squad moves together.
4. `--off` runs the same animation in reverse, then removes the entry.

With no vessel arguments it targets every vessel whose `/sim/vessels/by-id/<id>/is_kitten`
leaf reads `1`.

## Poses

`--end-pos x,y,z` is where the glasses come to rest; `--start-pos x,y,z` is where the animation
begins. Both are metres in the **anchor part's local frame** — the same frame and text format as
the entry's own `position` leaf, so you can nudge a live entry by hand:

```bash
echo "0.25 0.01 -0.30" > /sim/debug/thug_life/0/position   # tune it live
thug --end-pos 0.25,0.01,-0.30 Hunter                      # then keep it
```

Commas are the documented separator; a quoted `"0.25 0.01 -0.30"` works too, so a value read back
out of `position` pastes straight in. With `--start-pos` omitted the start is 1.5 m up the part's
`+Z` from `--end-pos` — the drop-onto-the-face move. Give it explicitly for anything else (a slide
in from the side, a rise from below).

The defaults (`0.23,0,-0.33`, rotation `(90, 180, 90)`, `0.9 × 0.22 m`) come from `thug.ts` and are
tuned for one EVA kitten model — treat them as a starting point, not the right answer for every
model.

## Options

| Flag | Default | Meaning |
|---|---|---|
| `--end-pos <x,y,z>` | `0.23,0,-0.33` | resting pose, metres, anchor part's local frame |
| `--start-pos <x,y,z>` | 1.5 m up `+Z` from `--end-pos` | pose the animation starts from |
| `-t, --time <s>` | `1.2` | animation duration (`0` = instant) |
| `--hz <hz>` | `60` | position write rate |
| `-e, --easing <fn>` | `ease-out` | `linear` \| `ease-in` \| `ease-out` \| `ease-in-out` |
| `-s, --scale <x>` | — | uniform size multiplier (excludes `--width/--height`) |
| `--width <m>` / `--height <m>` | `0.9` / `0.22` | explicit quad size |
| `--part <iid>` | root part | anchor part instance id |
| `-c, --cameras <m>` | `all` | which render passes draw the quad: `all`, or tokens of `main`/`crew`/`other` (`crew` = kitten face cams only; `main crew` = everywhere but extra camera windows) |
| `--off` | — | reverse the animation and remove the entry |
| `--sim <path>` | `/sim` (`$GATOS_SIM`) | the mounted 9p filesystem root |
| `--url <base>` | (`$GATOS_HTTP`) | use the HTTP `/v1/fs` mirror instead of the mount |

## Where it reads and writes

The `/sim` mount is the default and the preferred source; in the guest no flags are needed. The
order is: `--url` → `--sim` → `$GATOS_SIM` → the `/sim` mount if it is actually serving →
`$GATOS_HTTP`.

The mount deliberately outranks `$GATOS_HTTP`, because the guest login shell *presets* that
variable whenever the host serves the HTTP API — treating it as the default would route every
in-guest run through slirp, and through a transport that may be switched off by the time the
program runs.

`$GATOS_HTTP` is the `/v1` API base (`http://sim:4242/v1`), so `--url` takes a base with or
without the `/v1` suffix; both address `/v1/fs/<path>`.

`/v1/fs` serves leaves, not directories, so over HTTP the entry lookup probes `<id>/vessel` by
number and the vessel roster comes from `GET /v1/vessels`; on the mount both are directory reads.

## Requirements

- gatOS running with `debug_namespace = true` (the `thug_life` surface lives under `/sim/debug/`)
  and `control_enabled = true` (every entry write is a control-file write).
- Either a mounted `/sim` (inside the guest, or any host mount of the 9p export) or the HTTP
  API (`--url http://localhost:<port>`, needs `http_enabled` + `http_field_endpoints`).

If neither is reachable the program says which source it tried and why it gave up, rather than
blaming the vessel.

## Build

```bash
cargo build --release   # target/release/thug
```
