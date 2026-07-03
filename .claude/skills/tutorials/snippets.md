# The reusable snippet library

Canonical, copy-paste helpers so every tutorial's plumbing is **identical and correct**. Reuse these
verbatim — don't reinvent I/O or (especially) the quaternion math per tutorial. Two languages cover
the series: **Python** (already in the Alpine guest — the natural in-guest choice) and **Bun/TypeScript**
(the natural host choice), plus **shell** one-liners. When a tutorial needs a helper not here and it's
genuinely reusable, add it here after.

Data/format facts these encode: [`docs/TUTORIAL_DATA_REFERENCE.md`](../../../docs/TUTORIAL_DATA_REFERENCE.md).
The quaternion is a verbatim port of KSA's own arithmetic (see the caution at the bottom).

---

## Python — in-guest (`/sim` file I/O)

> **Published convention: this Python toolkit ships as two importable modules, not one paste.** The
> [`gatos-io` setup tutorial](../../../site/src/content/docs/guides/gatos-io.mdx) has the reader build
> **`gatos_io.py`** (the I/O half — `read`/`read_scalar`/`read_vec`/`write`/`write_vec`) and
> **`gatos_frames.py`** (the math half — `cross`/`dot`/`norm`/`unit`/`neg` + `from_rows`/`body_to_cci`)
> in `~/flight/`, and later rungs `from gatos_io import …` / `from gatos_frames import …` instead of
> re-pasting. The block below is the **canonical source of those helpers' bodies** (keep it in sync);
> the modules are just this text split I/O-vs-frames. In the *modules* the comments are trimmed to
> terse one-liners (no docstrings — they run in a terminal, not an IDE); the fuller explanation lives
> in the tutorial prose. `time`-based pacing lives with the sim-time rung, not the two base modules.

```python
# The in-guest toolkit, shown as one block. In the published tutorials this is split into
# gatos_io.py (reads/writes) + gatos_frames.py (vectors + quaternion) — see the note above.
import math, time, json

SIM = "/sim"

# --- talking to /sim --------------------------------------------------------
def read_text(path):
    with open(f"{SIM}/{path}") as f:
        return f.read().strip()

def read_scalar(path):          # a G9 double, one value + newline
    return float(read_text(path))

def read_vec(path):             # "x y z" (or "x y z w") -> list of floats
    return [float(t) for t in read_text(path).split()]

def read_json(path):            # the atomic telemetry doc, events, etc.
    return json.loads(read_text(path))

def write(path, text):
    # A write to a ctl/ or debug/ file actuates on the newline and returns a
    # real Linux errno on failure (EACCES control off, EINVAL bad value,
    # EBUSY one-shot fired, ...). A failed setpoint raises OSError here.
    with open(f"{SIM}/{path}", "w") as f:
        f.write(str(text) + "\n")

# --- tiny vector math (CCI is a normal right-handed 3D frame) ----------------
def cross(a, b): return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
def dot(a, b):   return sum(x*y for x, y in zip(a, b))
def norm(a):     return math.sqrt(dot(a, a))
def unit(a):     n = norm(a); return [c/n for c in a]
def scale(a, s): return [c*s for c in a]
def neg(a):      return [-c for c in a]

# --- gating (never fly blind) ------------------------------------------------
def is_paused():   return read_scalar("time/sim_dt") == 0.0
def is_warping():  return read_scalar("time/warp") > 1.0

# --- pace in sim time, not wall time -----------------------------------------
def sleep_sim(seconds):
    # Warp-correct + pause-safe: park until sim time advances `seconds`.
    target = read_scalar("time/ut") + seconds
    write("time/alarm", target)      # arm the alarm
    read_scalar("time/alarm")        # the read blocks until sim time reaches it

# --- the Body->CCI attitude quaternion (KSA's exact arithmetic) --------------
def from_rows(r0, r1, r2):
    """doubleQuat.CreateFromRotationMatrix (Shepperd's method), row-major.
    Rows are the body X, Y, Z axes in CCI; returns the Body->CCI quat [x,y,z,w]."""
    m00, m01, m02 = r0; m10, m11, m12 = r1; m20, m21, m22 = r2
    tr = m00 + m11 + m22
    if tr > 0.0:
        s = math.sqrt(tr + 1.0); w = 0.5 * s; s = 0.5 / s
        return [(m12 - m21)*s, (m20 - m02)*s, (m01 - m10)*s, w]
    elif m00 >= m11 and m00 >= m22:
        s = math.sqrt(1.0 + m00 - m11 - m22); inv = 0.5 / s
        return [0.5*s, (m01 + m10)*inv, (m02 + m20)*inv, (m12 - m21)*inv]
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22); inv = 0.5 / s
        return [(m10 + m01)*inv, 0.5*s, (m21 + m12)*inv, (m20 - m02)*inv]
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11); inv = 0.5 / s
        return [(m20 + m02)*inv, (m21 + m12)*inv, 0.5*s, (m01 - m10)*inv]

def body_to_cci(aim, roll_ref):
    """Body->CCI quat putting body +X (nose/thrust axis) along `aim`.
    `roll_ref` pins the otherwise-free spin about that axis."""
    x = unit(aim)
    c = cross(x, roll_ref)
    if norm(c) < 1e-9:                       # aim ∥ roll_ref: roll is free, pick any ⟂
        a = [1.0, 0.0, 0.0] if abs(x[0]) < 0.9 else [0.0, 1.0, 0.0]
        y = unit(cross(x, a))
    else:
        y = unit(c)
    z = unit(cross(x, y))
    return from_rows(x, y, z)                # [x, y, z, w] for ctl/attitude_target
```

Shell equivalents of the I/O (for tutorials that stay in the terminal):

```sh
val=$(cat /sim/vessels/active/velocity/orbital)          # read a scalar
read -r x y z < /sim/vessels/active/position/cci         # read a vector into x y z
echo 0.5 > /sim/vessels/active/ctl/throttle              # write a control (errno on failure)
cat /sim/vessels/active/telemetry | jq .orbit            # parse the atomic doc
ut=$(cat /sim/time/ut); echo $((${ut%.*}+300)) > /sim/time/alarm; cat /sim/time/alarm  # wait 300 sim-s
tail -f /sim/events                                       # stream events
```

---

## Bun / TypeScript — host (HTTP `/v1`)

```ts
// gatos.ts — the host toolkit. `bun add` nothing; Bun has fetch built in.
const BASE = process.env.GATOS_HTTP ?? "http://127.0.0.1:4242/v1";

// --- reads -----------------------------------------------------------------
export async function getText(path: string): Promise<string> {
  const r = await fetch(`${BASE}/fs/${path}`);
  if (!r.ok) throw new Error(`GET fs/${path} -> ${r.status}`);
  return (await r.text()).trim();
}
export const getScalar = async (p: string) => Number(await getText(p));
export const getVec = async (p: string) => (await getText(p)).split(/\s+/).map(Number);

export async function getJson<T>(path: string): Promise<T> {
  const r = await fetch(`${BASE}/${path}`);
  if (!r.ok) throw new Error(`GET ${path} -> ${r.status} ${await r.text()}`);
  return r.json() as Promise<T>;
}

// --- writes ----------------------------------------------------------------
/** Raw file-twin write (mirrors an in-guest `echo v > /sim/<path>`). */
export async function putFs(path: string, value: string): Promise<void> {
  const r = await fetch(`${BASE}/fs/${path}`, { method: "POST", body: value });
  if (!r.ok) throw new Error(`POST fs/${path} -> ${await r.text()}`);
}
/** Structured command (the canonical host write). Throws with the errno on failure. */
export async function command(cmd: {
  vessel_id: string; action: string;
  ordinal?: number; value?: number; values?: number[]; token?: string;
}): Promise<void> {
  const r = await fetch(`${BASE}/command`, {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify(cmd),
  });
  if (!r.ok) {
    const e = (await r.json().catch(() => ({}))) as { errno?: string; message?: string };
    throw new Error(`${cmd.action} -> ${e.errno ?? r.status}: ${e.message ?? ""}`);
  }
}

// --- gating + sim-time pacing ----------------------------------------------
export interface TimeInfo { ut: number; warp: number; sim_dt: number; }
export const time = () => getJson<TimeInfo>("time");
/** Warp-correct + pause-safe sleep: block until sim time advances `seconds`. */
export async function sleepSim(seconds: number): Promise<void> {
  const { ut } = await time();
  await fetch(`${BASE}/time/wait?until=${ut + seconds}`);   // long-poll, blocks
}

// --- vectors + the Body->CCI quaternion (port of KSA's arithmetic) ---------
type V = number[];
const cross = (a: V, b: V): V => [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]];
const dot = (a: V, b: V) => a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
const norm = (a: V) => Math.sqrt(dot(a, a));
const unit = (a: V): V => { const n = norm(a); return [a[0]/n, a[1]/n, a[2]/n]; };

/** Shepperd's method, row-major rows = body X,Y,Z in CCI -> [x,y,z,w]. */
export function fromRows(r0: V, r1: V, r2: V): number[] {
  const [m00, m01, m02] = r0, [m10, m11, m12] = r1, [m20, m21, m22] = r2;
  const tr = m00 + m11 + m22;
  if (tr > 0) { let s = Math.sqrt(tr + 1); const w = 0.5*s; s = 0.5/s;
    return [(m12-m21)*s, (m20-m02)*s, (m01-m10)*s, w]; }
  if (m00 >= m11 && m00 >= m22) { const s = Math.sqrt(1+m00-m11-m22), i = 0.5/s;
    return [0.5*s, (m01+m10)*i, (m02+m20)*i, (m12-m21)*i]; }
  if (m11 > m22) { const s = Math.sqrt(1+m11-m00-m22), i = 0.5/s;
    return [(m10+m01)*i, 0.5*s, (m21+m12)*i, (m20-m02)*i]; }
  const s = Math.sqrt(1+m22-m00-m11), i = 0.5/s;
  return [(m20+m02)*i, (m21+m12)*i, 0.5*s, (m01-m10)*i];
}
/** Body->CCI quat putting body +X (thrust axis) along `aim`; `rollRef` pins the roll. */
export function bodyToCci(aim: V, rollRef: V): number[] {
  const x = unit(aim); const c = cross(x, rollRef);
  const y = norm(c) < 1e-9
    ? unit(cross(x, Math.abs(x[0]) < 0.9 ? [1,0,0] : [0,1,0]))
    : unit(c);
  return fromRows(x, y, unit(cross(x, y)));
}

export interface Telemetry {
  seq: number; ut: number; warp: number; id: string; sit: string;
  parent?: string; controllable: boolean;
  pos_cci: [number,number,number]; vel_cci: [number,number,number];
  mass: { t: number; d: number; p: number };
  att_q: [number,number,number,number];
  orbit?: { ap: number; pe: number; period: number; ta: number; t_ap: number; t_pe: number };
}
```

> For larger host programs prefer the repo's typed SDK (`examples/sdk-ts`) — it wraps exactly these
> calls behind `new GatosClient()` and auto-selects HTTP vs `/sim`. The raw toolkit above keeps a
> tutorial copy-pasteable with zero install.

---

## The dual-transport pairing (drop into a `<Tabs syncKey="transport">`)

The same operation, both ways — the two tabs a tutorial shows:

| In-guest (Python / shell) | Host (TS / curl) |
|---|---|
| `write("vessels/active/ctl/throttle", 0.5)` | `putFs("vessels/active/ctl/throttle", "0.5")` |
| `write("vessels/active/ctl/attitude_mode", "Prograde")` | `command({vessel_id:"Hunter", action:"vessel.attitude_mode", token:"Prograde"})` |
| `write("vessels/active/ctl/attitude_target", " ".join(map(str,q)))` | `command({vessel_id:"Hunter", action:"vessel.attitude_target", values:q})` |
| `read_json("vessels/active/telemetry")` | `getJson<Telemetry>("vessels/active/telemetry")` |
| `sleep_sim(300)` | `await sleepSim(300)` |

---

> **⚠️ The quaternion is not interchangeable.** `from_rows`/`fromRows` is a **verbatim port of KSA's
> `doubleQuat.CreateFromRotationMatrix`** (Shepperd's method) so that
> `transform(UnitX, q) == desired_direction_cci` holds exactly. A generic quaternion library (nalgebra,
> gl-matrix, a kOS-style left-handed convention) *looks* right and steers **wrong**. Always use this
> port; the invariant to test is `transform(UnitX, bodyToCci(aim, roll)) ≈ unit(aim)`. See
> [`docs/TUTORIAL_DATA_REFERENCE.md §5`](../../../docs/TUTORIAL_DATA_REFERENCE.md) and
> [`gatos/coordinate-frames.md §4`](../gatos/coordinate-frames.md).
>
> (In a rendered tutorial `.mdx`, wrap this as a proper aside: `:::caution[The quaternion is not
> interchangeable]` … `:::`.)
