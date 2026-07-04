# gatOS recipes — complete worked programs

Runnable end-to-end examples against the `/sim` surface. Path/format details:
[`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md). Frame math:
[`coordinate-frames.md`](coordinate-frames.md).

---

## 0. Connecting from a host program (Bun/TypeScript)

A host program talks to the mod over HTTP `/v1`. Base URL:

- **Host:** `http://127.0.0.1:4242/v1` (default `http_preferred_port`).
- **Guest:** the env var `$GATOS_HTTP` (≈ `http://10.0.2.2:4242/v1`), or read `/sim` directly with `fs`.

A tiny self-contained client (no dependencies, Bun has `fetch` built in):

```ts
// gatos.ts — minimal host client over the HTTP /v1 API
const BASE = process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1";

export async function getJson<T>(path: string): Promise<T> {
  const r = await fetch(`${BASE}/${path}`);
  if (!r.ok) throw new Error(`GET ${path} -> ${r.status} ${await r.text()}`);
  return r.json() as Promise<T>;
}

/** Issue a command (the generic write surface). Throws with the errno on failure. */
export async function command(cmd: {
  vessel_id: string; action: string;
  ordinal?: number; value?: number; values?: number[]; token?: string;
}): Promise<void> {
  const r = await fetch(`${BASE}/command`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(cmd),
  });
  if (!r.ok) {
    const e = (await r.json().catch(() => ({}))) as { errno?: string; message?: string };
    throw new Error(`${cmd.action} -> ${e.errno ?? r.status}: ${e.message ?? ""}`);
  }
}

export interface Body { id: string; class: string; parent_id?: string; mass: number; mean_radius: number; mu: number; soi_meters: number; }
export interface Telemetry { id: string; parent?: string; sit: string; pos_cci: [number,number,number]; vel_cci: [number,number,number]; mass: { t:number;d:number;p:number }; orbit?: { ap:number; pe:number; period:number }; }
```

> Prefer the repo's typed SDK (`examples/sdk-ts`) for larger programs — it wraps exactly these calls
> behind `new GatosClient()` and auto-selects HTTP vs `/sim`. The raw client above is shown so a recipe
> is copy-pasteable with zero setup.

---

## 1. ⭐ Teleport two vessels into a shared orbit

**Task:** *teleport vessel `Hunter` to a circular orbit 120,000 m above Earth, and teleport `Polaris`
to be just 50 m ahead of Hunter on that same orbit.*

The plan (see [SPEC §6](../../../SPEC_9P_FILESYSTEM.md) for teleport semantics):

1. `debug.teleport` sets a **CCI state vector** about the vessel's **current parent body**. So both
   vessels must already be in **Earth's** sphere of influence (parent = `Earth`).
2. Circular-orbit radius `r = Earth.radius + 120000`; circular speed `v = sqrt(μ/r)`.
3. Put Hunter at CCI `pos=(r,0,0)`, `vel=(0,v,0)` → an equatorial, prograde, circular orbit
   (X–Y is the equatorial plane; velocity ⟂ position).
4. "50 m ahead on the same orbit" = advance the **true anomaly** by `Δθ = 50/r` (rotate position and
   velocity together about the orbit normal +Z). Ahead = the direction of motion (prograde).

```ts
// teleport-rendezvous.ts  —  run with:  bun teleport-rendezvous.ts
import { getJson, command, type Body, type Telemetry } from "./gatos.ts";

const ALTITUDE = 120_000;   // meters above the surface
const LEAD = 50;            // meters Polaris leads Hunter, along-track

// 1. Find the parent body Hunter currently orbits (must be Earth).
const hunter = await getJson<Telemetry>("vessels/Hunter/telemetry");
if (hunter.parent !== "Earth") {
  throw new Error(`Hunter's parent is '${hunter.parent}', not Earth — teleport is about the current ` +
    `parent. Move it into Earth's SOI first (e.g. debug/control_vessel + a transfer).`);
}
const bodies = await getJson<Body[]>("bodies");
const earth = bodies.find((b) => b.id === "Earth");
if (!earth) throw new Error("Earth not in the body catalog (telemetry_bodies enabled?)");

// 2. Circular orbit geometry.
const r = earth.mean_radius + ALTITUDE;     // orbital radius from Earth's center, m
const v = Math.sqrt(earth.mu / r);          // circular speed, m/s
console.log(`r=${r.toFixed(1)} m  v=${v.toFixed(2)} m/s  (period ≈ ${(2*Math.PI*Math.sqrt(r**3/earth.mu)).toFixed(0)} s)`);

// 3. Hunter: equatorial prograde circular state in CCI.
await command({ vessel_id: "Hunter", action: "debug.teleport", values: [r, 0, 0, 0, v, 0] });

// 4. Polaris: same orbit, 50 m ahead → advance true anomaly by Δθ = LEAD / r.
const dth = LEAD / r;
const px = r * Math.cos(dth), py = r * Math.sin(dth);     // position rotated +Δθ about +Z
const vx = -v * Math.sin(dth), vy = v * Math.cos(dth);    // velocity rotated the same
await command({ vessel_id: "Polaris", action: "debug.teleport", values: [px, py, 0, vx, vy, 0] });

console.log("Hunter and Polaris placed; Polaris leads by", LEAD, "m.");
```

Notes / variations:

- **Why `vessels/Hunter/...` works:** in KSA a vessel's name *is* its id, so the literal ids are
  `Hunter` and `Polaris`. (Confirm with `getJson<string[]>("vessels")`.)
- **`debug_namespace` must be on** (default). If a teleport returns `EACCES`, enable
  `debug_namespace` in `gatos.toml`. `EINVAL` ⇒ a non-finite/short values array.
- **Inclination:** the recipe builds an equatorial (inc 0) orbit. For a different plane, rotate the
  `(pos, vel)` pair about CCI +X (ascending node) by the inclination, or about +Z by the LAN.
- **File-write equivalent** (in-guest, no HTTP): `echo "$r 0 0 0 $v 0" > /sim/debug/vessels/Hunter/teleport`.
- **SDK equivalent:** `await new GatosClient().vessel("Hunter").debug.teleport([r,0,0,0,v,0])`.
- **Tight formations: batch the teleports.** Each write/command executes in its *own* frame (writes
  block until the game thread drains them — one per tick), so the two teleports above land a frame
  apart: Hunter drifts ~`v × frame_dt` (≈100 m at 7.8 km/s / 60 fps) before Polaris is placed. Fine
  for a 50 m *lead* demo, wrong for exact spacing. For same-tick placement write ONE group to
  `/sim/ctl/batch` (SPEC §3.10) — in-guest, or `POST /v1/fs/ctl/batch` with the same text:

  ```sh
  cat > /sim/ctl/batch <<EOF
  debug/vessels/Hunter/teleport $r 0 0 0 $v 0
  debug/vessels/Polaris/teleport $px $py 0 $vx $vy 0
  commit
  EOF
  ```

---

## 2. Read live telemetry

```ts
import { getJson, type Telemetry } from "./gatos.ts";
const t = await getJson<Telemetry>("vessels/active/telemetry");
console.log(`${t.id} ${t.sit}  ap=${t.orbit?.ap?.toFixed(0)}m pe=${t.orbit?.pe?.toFixed(0)}m`);
console.log(`pos_cci=${t.pos_cci.map(n=>n.toFixed(0))}  mass=${t.mass.t.toFixed(0)}kg`);
```
In-guest shell twin: `cat /sim/vessels/active/telemetry | jq .orbit`.

---

## 3. Throttle up and ignite (active vessel)

```ts
import { command } from "./gatos.ts";
const id = "Hunter";
await command({ vessel_id: id, action: "vessel.throttle", value: 0.75 }); // 75 %
await command({ vessel_id: id, action: "vessel.ignite",   value: 1 });    // light it
// later: await command({ vessel_id: id, action: "vessel.shutdown", value: 1 });
```
Shell twin: `echo 0.75 > /sim/vessels/active/ctl/throttle && echo 1 > /sim/vessels/active/ctl/engine`.

---

## 4. Hold an orientation, then schedule a circularization burn

```ts
import { getJson, command, type Telemetry } from "./gatos.ts";
const id = "Hunter";

// Point prograde via the onboard autopilot (no quaternion math; warp-correct).
await command({ vessel_id: id, action: "vessel.attitude_mode", token: "Prograde" });

// Circularize at apoapsis: schedule a prograde Δv at the time-to-apoapsis.
const t = await getJson<Telemetry & { orbit?: { ap:number; period:number; ta:number; t_ap:number } }>(
  `vessels/${id}/telemetry`);
// (compute the Δv you need from vis-viva using the orbit elements; here we just show the call shape)
const ut = (await getJson<{ ut:number }>("time")).ut + (t as any).orbit.t_ap;
const dv: [number,number,number] = [/* dvx */ 0, /* dvy */ 0, /* dvz */ 0]; // CCI Δv vector
await command({ vessel_id: id, action: "vessel.burn", values: [ut, ...dv] });
```
`ctl/attitude_*` and `ctl/burn` are **solver-phase** (effect on the next solver step). For hand-flown
closed loops, run near 1× warp.

---

## 5. Wait for a sim time (warp-correct), then act

```ts
const { ut } = await (await fetch(`${process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1"}/time`)).json();
const wake = ut + 300; // 300 sim-seconds from now
await fetch(`${process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1"}/time/wait?until=${wake}`); // blocks
// ...do the thing at T+300s...
```
In-guest twin: `echo $((ut+300)) > /sim/time/alarm` (the read blocks until reached).

---

## 6. Stream events

```ts
const base = process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1";
const res = await fetch(`${base}/events`);
const reader = res.body!.getReader();
const dec = new TextDecoder(); let buf = "";
for (;;) {
  const { value, done } = await reader.read(); if (done) break;
  buf += dec.decode(value, { stream: true });
  let nl: number;
  while ((nl = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, nl).trim(); buf = buf.slice(nl + 1);
    if (line.startsWith("data:")) console.log("event", JSON.parse(line.slice(5).trim()));
  }
}
```
In-guest twin: `tail -f /sim/events` (one JSON line per event).

---

## 7. Switch which vessel you control

```ts
import { command } from "./gatos.ts";
// Focus + take control (cheat-tier; needs debug_namespace). After this, vessels/active/ is Polaris.
await command({ vessel_id: "Polaris", action: "debug.control_vessel", token: "Polaris" });
```

---

## 8. Watch the game in the terminal (Kitty graphics)

The mod can render the KSA viewport as a live video stream over the Kitty terminal graphics
protocol, exposed at `/sim/display` (off by default). Any Kitty-capable terminal — an in-game
purrTTY tab or an external emulator SSH'd into the guest — renders it. Consuming it is basically
`cat`, because the host bakes complete, in-place Kitty frames:

```sh
echo 1 > /sim/display/enabled     # turn it on (idles for free while off)
echo 24 > /sim/display/fps         # optional: retune live (1..60, clamped)
echo 480 > /sim/display/width      # optional: bigger image (16..1920 px)
cat /sim/display/stream            # render; Ctrl-C to stop
echo 0 > /sim/display/enabled      # stop the capture
```

Controls (`enabled`/`fps`/`width`/`height`/`encoding`) are plain files, so they also work over HTTP
(`POST /v1/fs/display/enabled`) and MQTT (`gatos/sim/display/*`). The image is the scene without the
UI (pre-tonemap). Full example with alt-screen + clean teardown: `examples/simscreen/`; design in
`STREAM_PLAN.md`; the `/sim/display` catalog is in `SPEC_9P_FILESYSTEM.md` §3.8.

---

See also the in-repo Rust references: `examples/gogogo-rs` (minimal control panel) and
`examples/land-o-matic` (full G-FOLD/UPFG autopilot).

## 8. Fan out one write to many files in a single tick

A `/sim` write doesn't return until the next game tick (the 9p thread enqueues the command, the game
thread drains it), so writing one value to many files *sequentially* pays one tick **per file**. (And
you can't redirect to a glob: `echo 1 > /sim/.../*/on` is an `ambiguous redirect` — `>` is one fd. The
composable shape is `tee`, which takes the files as args and the value on stdin:
`echo 1 | tee /sim/.../*/on >/dev/null` — but `tee` writes them sequentially.) Two fixes:

- **Guaranteed same tick — `/sim/ctl/batch`** (SPEC §3.10): one `<path> <value>` line per write, then
  `commit`; the group executes atomically in one drain. Shell glob fan-out is one loop:

  ```sh
  { for f in /sim/vessels/by-id/*/lights/*/on; do echo "$f 1"; done; echo commit; } > /sim/ctl/batch
  ```

- **Concurrent dispatch (probabilistic)**: issue the writes concurrently and they *usually* land in
  the same tick's drain. `examples/kecho` is a tiny *concurrent `tee`* —
  `echo 1 | kecho /sim/vessels/by-id/*/lights/*/on`. Still useful past the batch's 64-command cap or
  when a stray extra frame doesn't matter.

---

## 9. Weld one vessel rigidly to another (the `weld_here` cheat)

**Task:** *attach `Polaris` to a part of `Hunter` at their current relative pose, so Polaris rides along
through maneuvers and time-warp.* Cheat-tier — needs `debug_namespace` + `telemetry_vessel_parts` (both
default on). The two vessels must orbit the **same body**. (Ported from the `unscience` mod; see
[SPEC §3.7](../../../SPEC_9P_FILESYSTEM.md).)

```ts
// weld-here.ts  —  run with:  bun weld-here.ts
import { command } from "./gatos.ts";
const BASE = process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1";
const source = "Polaris", target = "Hunter";

// 1. Pick a STABLE anchor part id from the target's top-level parts (0 ⇒ the target's body/CoM frame).
//    The field-level fs endpoint returns one leaf's raw text value.
const r = await fetch(`${BASE}/fs/vessels/${target}/parts/0/instance_id`);
const piid = r.ok ? Number((await r.text()).trim()) : 0;

// 2. Capture the CURRENT relative pose and weld. token = target id; values = [part_iid, lock(1=lock attitude)].
await command({ vessel_id: source, action: "debug.weld_here", token: target, values: [piid, 1] });

// later: suspend (keep the entry) or remove
// await command({ vessel_id: source, action: "debug.weld_enable", value: 0 }); // suspend
// await command({ vessel_id: source, action: "debug.weld_remove", value: 1 }); // unweld
```

Shell twins (in-guest, against the `/sim` mount):
```sh
piid=$(cat /sim/vessels/Hunter/parts/0/instance_id)
echo "Hunter $piid" > /sim/debug/vessels/Polaris/weld_here   # weld at the current pose (lock defaults to 1)
cat  /sim/debug/welds/count                                  # -> 1
echo 0 > /sim/debug/welds/Polaris/enabled                    # suspend tracking (entry kept)
echo 1 > /sim/debug/vessels/Polaris/unweld                   # remove this weld
echo 1 > /sim/debug/welds/clear                              # remove ALL welds
```

Notes:
- **`weld` vs `weld_here`:** `weld` takes an **explicit** pose
  `<target> <part_iid> <x y z> <pitch yaw roll> <lock>`; `weld_here` captures the current pose for you (the
  practical path — computing offsets by hand is hard).
- Errnos: `EBUSY` (source==target, or the two orbit different bodies), `ENOENT` (target/part gone),
  `EINVAL` (bad arity/values).
- Welds are **runtime-only** (never persisted) and cleared on mod unload. A source may anchor at most one
  weld; many sources may anchor to one target.

---

## 10. Stick "thug life" sunglasses on a part (the `thug_life` cheat)

**Task:** *anchor a flat, world-space sunglasses quad to a part of `Hunter` and tune it live.* Cheat-tier
— needs `debug_namespace` + `telemetry_vessel_parts` (both default on). Tip: `cat /sim/debug/thug_life/help`
prints a console-friendly readme (all the commands + worked examples on `Hunter`/`Polaris`/`Banjo`). This is
gatOS's **first custom GPU rendering**: the quad is drawn into KSA's scene and tracks the part each frame (it's depth-tested, so it's
occluded by geometry in front of it). The render hook + GPU resources install **lazily on the first
entry** and are freed when the last one is removed. (Ported from the `unscience` mod; see
[SPEC §3.7](../../../SPEC_9P_FILESYSTEM.md), and the **ksa skill's `quad.md`** for the render internals.)

```ts
// thug-life.ts  —  run with:  bun thug-life.ts
import { command } from "./gatos.ts";
const BASE = process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1";
const vessel = "Hunter";

// 1. Discover a STABLE anchor part id from the vessel's top-level parts (0 ⇒ the vehicle body frame).
const r = await fetch(`${BASE}/fs/vessels/${vessel}/parts/0/instance_id`);
const piid = r.ok ? Number((await r.text()).trim()) : 0;

// 2. Add the quad. token = anchor vessel; values = [part_iid] (pose/size default) or the full
//    [part_iid, x, y, z, pitch, yaw, roll, w, h].
await command({ vessel_id: vessel, action: "debug.thug_life_add", token: vessel, values: [piid] });
// The new entry takes the lowest free id (reused after remove/clear); the first add is id 0.

// 3. Tune it live (id travels in `ordinal`).
await command({ vessel_id: vessel, action: "debug.thug_life_position", ordinal: 0, values: [0, 0.5, 0] });
await command({ vessel_id: vessel, action: "debug.thug_life_rotation", ordinal: 0, values: [0, 0, 0] });
await command({ vessel_id: vessel, action: "debug.thug_life_size",     ordinal: 0, values: [1.2, 0.4] });
await command({ vessel_id: vessel, action: "debug.thug_life_visible",  ordinal: 0, value: 1 });

// later: remove one, or clear all
// await command({ vessel_id: vessel, action: "debug.thug_life_remove", ordinal: 0, value: 1 });
// await command({ vessel_id: vessel, action: "debug.thug_life_clear",  value: 1 });
```

Shell twins (in-guest, against the `/sim` mount):
```sh
piid=$(cat /sim/vessels/Hunter/parts/0/instance_id)
echo "Hunter $piid" > /sim/debug/thug_life/add          # anchor at the default pose/size (id 0)
cat  /sim/debug/thug_life/count                          # -> 1
echo "0 0.5 0"  > /sim/debug/thug_life/0/position        # nudge it up
echo "1.2 0.4"  > /sim/debug/thug_life/0/size            # width height
echo 0 > /sim/debug/thug_life/0/visible                  # hide; 1 to show
cat  /sim/debug/thug_life/0/spec                          # the 10-token spec (echo back to add)
echo 1 > /sim/debug/thug_life/0/remove                   # remove this entry
echo 1 > /sim/debug/thug_life/clear                       # remove ALL quads (frees the GPU + unpatches)
```

Notes:
- Anchor a **top-level part by `instance_id`** (from `parts/<n>/instance_id`) or pass `0` for the vehicle
  **body frame**. No subparts in v1.
- Errnos: `ENOENT` (vessel/part/id gone), `EINVAL` (bad arity/values), `EIO` (renderer unavailable).
- Entries are **runtime-only** (never persisted); cleared on mod unload. With no entries the render hook is
  removed entirely (zero per-frame cost).

---

## 11. Kick a vessel with a one-shot impulse (the `impulse` cheat)

**Task:** *change a vessel's velocity instantly — no propellant, no pointing, no waiting.* Cheat-tier
(`debug_namespace`, default on). Write `x y z [cci|body] [ns|dv]` to `debug/vessels/<id>/impulse`:
the vector defaults to an **impulse in newton-seconds** in the **parent-CCI frame**; Δv = J ÷ the
vessel's live total mass (the same math as KSA's own docking-separation impulse). Two optional
keywords, any order after the numbers: `body` reads the vector in the vessel frame (+X = nose, same
convention as `attitude/quat`); `dv` skips the mass division and applies the vector **directly as Δv
in m/s** — `ctl/burn` semantics minus the autopilot. Full semantics:
[SPEC §6](../../../SPEC_9P_FILESYSTEM.md).

```sh
# In-guest shell twins (against the /sim mount):
echo "50000 0 0 body"  > /sim/debug/vessels/Hunter/impulse   # 50 kN·s shove off the nose
echo "10 0 0 body dv"  > /sim/debug/vessels/Hunter/impulse   # exactly +10 m/s forward
echo "0 0 -25 dv"      > /sim/debug/vessels/Hunter/impulse   # -25 m/s along CCI -Z (southward)

# Prograde +100 m/s without the flight computer: kick along the velocity unit vector (CCI).
v=$(cat /sim/vessels/Hunter/velocity/cci)                     # "vx vy vz"
set -- $v; mag=$(echo "sqrt($1*$1+$2*$2+$3*$3)" | bc -l)
echo "$(echo "100*$1/$mag" | bc -l) $(echo "100*$2/$mag" | bc -l) $(echo "100*$3/$mag" | bc -l) dv" \
  > /sim/debug/vessels/Hunter/impulse
```

```ts
// impulse.ts  —  run with:  bun impulse.ts
import { command } from "./gatos.ts";
// 5 kN·s pushoff along the nose (like an undock separation, aimed by attitude):
await command({ vessel_id: "Hunter", action: "debug.impulse", values: [5000, 0, 0], token: "body" });
// Precise +10 m/s prograde-ish forward kick (Δv mode):
await command({ vessel_id: "Hunter", action: "debug.impulse", values: [10, 0, 0], token: "body", aux: "dv" });
```

Notes:
- **JSON shape:** `values` = the 3-vector, `token` = frame (`cci`/`body`, omit ⇒ `cci`), `aux` = unit
  (`ns`/`dv`, omit ⇒ `ns`).
- The kick is **instantaneous and non-physical** — the orbit is rebuilt at the current CCI position
  with the bumped velocity (the teleport machinery), so it works on-rails, in the physics bubble, and
  at any magnitude. A landed vessel gets launched exactly as the math says.
- To *predict* the Δv of an `ns` kick, read `mass/total` first: Δv = J/m. Or just use `dv`.
- Frame phase (one command per tick): to kick a formation simultaneously, batch the writes through
  `/sim/ctl/batch` exactly like the formation teleport (§8-batch).
- Errnos: `EINVAL` (bad arity/keyword/non-finite), `EBUSY` (no parent body / mass unavailable),
  `ENOENT` (vessel gone), `EACCES` (debug namespace off).
