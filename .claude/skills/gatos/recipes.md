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

See also the in-repo Rust references: `examples/gogogo-rs` (minimal control panel) and
`examples/land-o-matic` (full G-FOLD/UPFG autopilot).
