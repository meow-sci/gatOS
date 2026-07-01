# kecho (Rust CLI example)

A **concurrent `tee` for `/sim`** — it reads stdin and writes it to **every path argument at once**, so
a fan-out like

```sh
echo 1 | kecho /sim/vessels/by-id/*/lights/*/on
```

turns on every light of every vessel in **a single game tick** instead of one write per tick.

Where [`gogogo-rs`](../gogogo-rs) and [`dancy-party-rs`](../dancy-party-rs) are interactive TUIs, this
one is a plain command-line tool that composes like the core shell utilities — drop it on the right of
a pipe, exactly where you'd otherwise reach for `tee`.

## Why you can't just redirect

`echo 1 > /sim/vessels/by-id/*/lights/*/on` **doesn't do what it looks like.** `>` is shell redirection
to a *single* file descriptor; when the glob matches more than one file, bash refuses with
`ambiguous redirect`, and the program on the left never even sees the targets. Writing one value to
many files is exactly the job the core tool **`tee`** exists for — it takes the files as *arguments* and
the data on *stdin*:

```sh
echo 1 | tee /sim/vessels/by-id/*/lights/*/on >/dev/null
```

That works and is fully composable — but `tee` writes the files **sequentially**, and a `/sim` write
doesn't return until the next game tick: the 9p server enqueues the command and the game thread drains
the queue once per frame (the gatOS threading rules — writes are *enqueued* on the 9p thread and
*executed* on the game thread). So sequential `tee` over 40 lights ≈ 40 ticks. Visibly laggy.

**kecho is a drop-in concurrent `tee`** for that case: same stdin-and-arguments shape, but it issues
every write **simultaneously** (one `spawn_blocking` per file on tokio's blocking pool, collected with a
`JoinSet`), so all the commands are sitting in the 9p server's queue together and drain in the **same
tick's** `CommandQueue.Drain`. 40 lights ≈ 1 tick.

It stays composable like `cat`/`tee`: stdin can come from a pipe, a here-string (`<<<`), command
substitution, or a file (`kecho … < value.txt`), and — like `tee` — stdin is echoed to stdout by
default so kecho can sit mid-pipeline (silence it with `-q` or `>/dev/null`).

> This is the same fire-and-forget dispatch [`dancy-party-rs`](../dancy-party-rs) uses for its light
> writes. The one difference: kecho is a short-lived CLI, so it waits for the **whole batch** (one
> barrier) before exiting. A *true* detach — exit before the writes land — would race process
> termination against threads that hadn't issued their `write()` yet and silently drop writes, so kecho
> doesn't do that; waiting one tick for the batch is already "nearly instant."

## Usage

```text
echo VALUE | kecho [OPTIONS] PATH...
```

Whatever is on stdin is written **verbatim** (binary-safe, like `cat`) to every `PATH`. **Pipe `echo`
for control files** — its trailing newline rides along, and the `/sim` control files actuate on that
newline. (`printf 1 | kecho …` would write `1` with no newline and may not actuate, exactly as
`printf 1 > file` wouldn't.)

| option | meaning |
| --- | --- |
| `-q, --quiet` | don't echo stdin back to stdout (the `tee` passthrough is on by default) |
| `-j, --jobs <n>` | cap simultaneously in-flight writes (default `0` = unbounded, i.e. all in one tick). A positive value throttles the fan-out. |
| `--url <base>` | write via the HTTP `/v1/fs` mirror at `<base>` instead of the filesystem (host dev; e.g. `$GATOS_HTTP`, `http://127.0.0.1:4242/v1`). No shell globbing on the host — pass explicit paths. |
| `-v, --verbose` | print a one-line summary (count + elapsed) to stderr |
| `--` | end options; treat the rest as `PATH`s (for paths starting with `-`) |
| `-h, --help` | show help |

**Exit code:** `0` = all writes ok · `1` = one or more failed (each printed to stderr as
`kecho: <path>: <ERRNO> <message>`, using the same control-file errno vocabulary as the rest of gatOS) ·
`2` = bad arguments / stdin error.

## Examples

```sh
echo 1 | kecho /sim/vessels/by-id/*/lights/*/on          # all lights, every vessel, one tick
echo 0 | kecho /sim/vessels/active/rcs/*/active           # cut every RCS thruster at once
echo '1 0 0' | kecho /sim/vessels/by-id/Hunter/lights/*/color   # paint Hunter's lights red
echo 0.5 | kecho -v /sim/vessels/by-id/*/ctl/throttle     # throttle every vessel; print a summary
echo 1 | kecho -j 8 /sim/vessels/by-id/*/lights/*/on      # same, but at most 8 writes in flight
kecho /sim/vessels/by-id/*/lights/*/on <<< 1              # here-string instead of a pipe
kecho /sim/vessels/active/ctl/attitude_mode < mode.txt    # value from a file
```

kecho doesn't interpret the value — it delivers the same stdin bytes to every target (a flag `0`/`1`, a
`G9` number, a space-separated vector, a token) and lets each file actuate and report its own errno on
failure.

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo build --release              # then: echo 1 | ./target/release/kecho /sim/.../on
cargo install --path .             # or install `kecho` onto $PATH so it drops into pipelines
```

## On the host (dev)

There's no `/sim` mount and no shell globbing on the host, so point kecho at the HTTP mirror and pass
explicit paths (each is reduced to its `/sim`-relative form automatically — a leading `/sim/` is
stripped, and the body's trailing newline is trimmed since the HTTP field endpoint actuates on receipt):

```sh
echo 1 | cargo run -- --url "$GATOS_HTTP" vessels/active/lights/0/on
echo '1 0 0' | cargo run -- --url http://127.0.0.1:4242/v1 /sim/vessels/by-id/Hunter/lights/0/color
```

(For the FS path you can also point it at a `/sim`-shaped fixture directory by passing absolute or
relative paths — kecho writes whatever paths you give it, exactly like `tee path` would.)

## How it works (the short version)

```
echo 1 ─┬─► stdin ─► kecho path1 path2 … pathN
        │                    │
        │                    └─ JoinSet: spawn_blocking(write(path_i, stdin)) for every i, all at once
        │                           │ (tokio blocking pool, ≤512 concurrent by default)
        │                           ▼
        │                    9p server enqueues N SimCommands ──► one game tick drains them together
        └─► stdout (tee passthrough, unless -q)
```

The whole program is ~two small files: `src/sink.rs` (the `Sink` trait + the `std::fs` and HTTP
backends + errno mapping) and `src/main.rs` (argument parsing, stdin read, and the concurrent
`dispatch`). Run the tests with `cargo test`.

## Just a faster tee

Functionally `echo 1 | tee … >/dev/null` does the same thing — kecho only adds the concurrent fan-out so
"do this to *everything*" lands in one tick instead of dozens. It composes everywhere `tee` does:
lights, RCS, throttles, and any other per-vessel/per-module control file you can name with a glob.
