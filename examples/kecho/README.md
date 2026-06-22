# kecho (Rust CLI example)

`echo`, but it writes **one value to many `/sim` files at once** — so a fan-out like

```sh
kecho 1 /sim/vessels/by-id/*/lights/*/on
```

turns on every light of every vessel in **a single game tick** instead of one write per tick.

Where [`gogogo-rs`](../gogogo-rs) and [`dancy-party-rs`](../dancy-party-rs) are interactive TUIs, this
one is a plain command-line tool — a sharper `echo`/`tee` for the `/sim` surface that you drop straight
into shell one-liners and scripts.

## The problem it solves

`/sim` control files actuate the game, but **a write doesn't return until the next game tick**: the 9p
server enqueues the command and the game thread drains the queue once per frame (the gatOS threading
rules — writes are *enqueued* on the 9p thread and *executed* on the game thread). That's fine for one
write, but it makes a **sequential** fan-out slow:

```sh
# One whole game tick PER FILE — 40 lights ≈ 40 ticks. Visibly laggy.
for f in /sim/vessels/by-id/*/lights/*/on; do echo 1 > "$f"; done
```

It isn't that `echo` is slow — it's that the writes are *serialized*: each one blocks for its own tick
before the next even starts.

kecho issues **every write simultaneously**. The shell still does the globbing; kecho just takes all
the expanded paths and fires one write per file concurrently (onto tokio's blocking pool, collected
with a `JoinSet`), so all the commands are sitting in the 9p server's queue together and drain in the
**same tick's** `CommandQueue.Drain`. 40 lights ≈ 1 tick.

This is the same fire-and-forget dispatch [`dancy-party-rs`](../dancy-party-rs) uses for its light
writes. The one difference: kecho is a short-lived CLI, so it waits for the **whole batch** (one
barrier) before exiting. A *true* detach — exit before the writes land — would race process termination
against threads that hadn't issued their `write()` yet and silently drop writes, so kecho doesn't do
that; waiting one tick for the batch is already "nearly instant."

## Usage

```text
kecho [OPTIONS] VALUE PATH...
```

Writes `VALUE` (with a trailing newline, so control files actuate) to every `PATH`, concurrently.
**Quote a value that has spaces** — vectors and state-vectors are single arguments.

| option | meaning |
| --- | --- |
| `-n` | do not append a trailing newline (control files actuate on the newline, so you rarely want this) |
| `-j, --jobs <n>` | cap simultaneously in-flight writes (default `0` = unbounded, i.e. all in one tick). A positive value throttles the fan-out. |
| `--url <base>` | write via the HTTP `/v1/fs` mirror at `<base>` instead of the filesystem (host dev; e.g. `$GATOS_HTTP`, `http://127.0.0.1:4242/v1`). No shell globbing on the host — pass explicit paths. |
| `-v, --verbose` | print a one-line summary (count + elapsed) to stderr |
| `--` | end options; treat the rest as `VALUE` then `PATH`s (for values/paths starting with `-`) |
| `-h, --help` | show help |

**Exit code:** `0` = all writes ok · `1` = one or more failed (each printed to stderr as
`kecho: <path>: <ERRNO> <message>`, using the same control-file errno vocabulary as the rest of gatOS) ·
`2` = bad arguments.

## Examples

```sh
kecho 1 /sim/vessels/by-id/*/lights/*/on                 # all lights, every vessel, one tick
kecho 0 /sim/vessels/active/rcs/*/active                  # cut every RCS thruster at once
kecho "1 0 0" /sim/vessels/by-id/Hunter/lights/*/color    # paint Hunter's lights red
kecho -v 0.5 /sim/vessels/by-id/*/ctl/throttle            # set throttle on every vessel; print a summary
kecho -j 8 1 /sim/vessels/by-id/*/lights/*/on             # same, but at most 8 writes in flight
```

The value is whatever the file accepts — a flag (`0`/`1`), a `G9` number, a space-separated vector
(quote it), a token. kecho doesn't interpret it; it just delivers the same bytes to every target and
lets each file actuate (and report its own errno on failure).

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo build --release              # then: ./target/release/kecho 1 /sim/.../on
cargo install --path .             # or install `kecho` onto $PATH for one-liners
```

## On the host (dev)

There's no `/sim` mount and no shell globbing on the host, so point kecho at the HTTP mirror and pass
explicit paths (each is reduced to its `/sim`-relative form automatically — a leading `/sim/` is
stripped):

```sh
cargo run -- --url "$GATOS_HTTP" 1 vessels/active/lights/0/on
cargo run -- --url http://127.0.0.1:4242/v1 "1 0 0" /sim/vessels/by-id/Hunter/lights/0/color
```

(For the FS path you can also point it at a `/sim`-shaped fixture directory — kecho writes whatever
absolute or relative paths you give it, exactly like `echo … > path`.)

## How it works (the short version)

```
shell glob ──► kecho VALUE path1 path2 … pathN
                       │
                       └─ JoinSet: spawn_blocking(write(path_i, VALUE)) for every i, all at once
                              │ (tokio blocking pool, ≤512 concurrent by default)
                              ▼
                       9p server enqueues N SimCommands ──► one game tick drains them together
```

The whole program is ~two small files: `src/sink.rs` (the `Sink` trait + the `std::fs` and HTTP
backends + errno mapping) and `src/main.rs` (argument parsing + the concurrent `dispatch`). Run the
tests with `cargo test`.

## No tool required

kecho is pure convenience — the slow loop above does the same thing. But because the writes batch into
one tick, `kecho` makes "do this to *everything*" feel instant, which is exactly what you want for
lights, RCS, throttles, and any other per-vessel/per-module control file you can name with a glob.
