// shotbuilder — one shot description, two runnable outputs.
//
// A gatOS camera move is reachable two ways, and they are the SAME surface:
//
//   L1/L2  ordinary /sim leaves (camera/pose/fov, camera/pose/orbit/azimuth, ...)
//          driven over time by /sim/ctl/timed_batch — a list of "<offsetMs> <path> <payload>"
//   L3     a JSON track uploaded to /sim/camera/track/<name> and played with /sim/camera/play
//
// This file compiles one description into both, so you can diff them for the same shot.
// The track interpolates on the host at render rate; the timed batch is that same curve
// *pre-sampled* into leaf writes. Everything here is plain text/JSON — no SDK, no RPC.

// ---------------------------------------------------------------------------
// The description (what a human writes)
// ---------------------------------------------------------------------------

/** A scalar channel: one number holds it constant, a `[from, to]` pair animates it. */
export type Scalar = number | [number, number];
export type Vec3 = [number, number, number];
/** A position channel: one point holds it, a `[from, to]` pair dollies between them. */
export type Points = Vec3 | [Vec3, Vec3];

/** Named eases, spelled exactly as the track format spells them. */
export type Ease = "linear" | "in" | "out" | "in-out";

export interface Aim {
  /** `vessel:<id>` | `body:<id>` | `part:<vessel-id>/<instance-id>` */
  target: string;
  /** Metres in `frame`, measured on the subject. Default `[0,0,0]`. */
  offset?: Vec3;
  frame?: string;
  /** `world` | `target` | `velocity` | `free`. Default `world`. */
  up?: string;
}

export interface Move {
  name?: string;
  /** Seconds. Required, > 0. */
  duration: number;
  ease?: Ease;
  /** Exponent for the `in`/`out`/`in-out` curves. The track format's default is 3. */
  easePower?: number;

  /** Place the camera on a sphere about the anchor. Wins over `position` when radius > 0. */
  orbit?: { radius?: Scalar; azimuth?: Scalar; elevation?: Scalar };
  /** Place the camera at a point in `frame`, relative to the anchor. */
  position?: Points;

  fov?: Scalar;
  /** Degrees. Only meaningful on the aim path — an explicit rotation already names one. */
  roll?: Scalar;
}

export interface Shot {
  name: string;
  loop?: boolean;
  /** `vessel:<id>` | `body:<id>` | `part:<vessel>/<instance>` — what the pose is measured about. */
  anchor?: string;
  /** `ecl` | `cce` | `bodyfixed` | `enu` | `lvlh` | `chase`. */
  frame?: string;
  aim?: Aim;
  /** Sampling rate for the timed-batch emission ONLY. The track needs no sampling. */
  sampleHz?: number;
  moves: Move[];
}

// ---------------------------------------------------------------------------
// Easing — the same four curves gatOS.SimFs/Camera/Easing.cs applies, so the
// pre-sampled batch and the host-interpolated track trace the same path.
// ---------------------------------------------------------------------------

const clampPower = (p: number) => Math.min(16, Math.max(0.01, p));

export function ease(t: number, kind: Ease, power = 3): number {
  if (t <= 0) return 0; // endpoint snap, before anything that could round
  if (t >= 1) return 1;
  const p = clampPower(power);
  switch (kind) {
    case "in":
      return Math.pow(t, p);
    case "out":
      return 1 - Math.pow(1 - t, p);
    case "in-out":
      return t < 0.5 ? Math.pow(2 * t, p) / 2 : 1 - Math.pow(2 * (1 - t), p) / 2;
    default:
      return t;
  }
}

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

const scalarIsPair = (v: Scalar): v is [number, number] => Array.isArray(v);
const pointsArePair = (v: Points): v is [Vec3, Vec3] => Array.isArray(v[0]);

/** Fixed-ish formatting: enough precision for metres, no 0.30000000000000004. */
export function num(v: number): string {
  const s = v.toFixed(6);
  return s.includes(".") ? s.replace(/0+$/, "").replace(/\.$/, "") : s;
}

const lerp = (a: number, b: number, u: number) => a + (b - a) * u;

function moveName(m: Move, i: number): string {
  return m.name ?? `shot-${i}`;
}

/** Every move must drive at least one channel — an empty shot is EINVAL on upload. */
function assertDrivesSomething(m: Move, i: number): void {
  if (!m.orbit && !m.position && m.fov === undefined && m.roll === undefined) {
    throw new Error(`move ${i} ("${moveName(m, i)}") declares no channels`);
  }
  if (!(m.duration > 0)) {
    throw new Error(`move ${i} ("${moveName(m, i)}") must last a positive time`);
  }
}

// ---------------------------------------------------------------------------
// L3 — the JSON track
// ---------------------------------------------------------------------------

/** A track channel: one key for a constant, two keys (with the ease on the first) for a ramp. */
function channel(v: Scalar, duration: number, m: Move): unknown {
  if (!scalarIsPair(v)) return { keys: [{ t: 0, v }] };
  const first: Record<string, unknown> = { t: 0, v: v[0], ease: m.ease ?? "in-out" };
  if (m.easePower !== undefined) first.ease_power = m.easePower;
  return { keys: [first, { t: duration, v: v[1] }] };
}

function positionBlock(m: Move, frame: string | undefined): unknown {
  if (m.orbit) {
    const block: Record<string, unknown> = { mode: "orbit" };
    if (frame) block.frame = frame;
    // Only authored sub-channels are emitted: an unauthored one is left to the live
    // pose/orbit/* overrides rather than being clobbered with a default.
    if (m.orbit.radius !== undefined) block.radius = channel(m.orbit.radius, m.duration, m);
    if (m.orbit.azimuth !== undefined) block.azimuth = channel(m.orbit.azimuth, m.duration, m);
    if (m.orbit.elevation !== undefined) block.elevation = channel(m.orbit.elevation, m.duration, m);
    return block;
  }

  const p = m.position!;
  const block: Record<string, unknown> = { mode: "cartesian", curve: "linear" };
  if (frame) block.frame = frame;
  if (!pointsArePair(p)) {
    block.keys = [{ t: 0, v: p }];
  } else {
    const first: Record<string, unknown> = { t: 0, v: p[0], ease: m.ease ?? "in-out" };
    if (m.easePower !== undefined) first.ease_power = m.easePower;
    block.keys = [first, { t: m.duration, v: p[1] }];
  }
  return block;
}

/** Compile the description into the track JSON `/sim/camera/track/<name>` accepts. */
export function toTrack(shot: Shot): string {
  const defaults: Record<string, unknown> = {};
  if (shot.frame) defaults.frame = shot.frame;
  if (shot.anchor) defaults.anchor = shot.anchor;

  let t = 0;
  const shots = shot.moves.map((m, i) => {
    assertDrivesSomething(m, i);
    const out: Record<string, unknown> = { name: moveName(m, i), t, duration: m.duration };
    if (m.orbit || m.position) out.position = positionBlock(m, shot.frame);
    if (shot.aim) {
      // aim.* is constant for a shot on purpose: the host re-resolves it against the LIVE
      // target every frame, which is what makes an offset stay glued to a moving subject.
      const aim: Record<string, unknown> = { target: shot.aim.target };
      if (shot.aim.offset) aim.offset = shot.aim.offset;
      if (shot.aim.frame) aim.frame = shot.aim.frame;
      if (shot.aim.up) aim.up = shot.aim.up;
      if (m.roll !== undefined) aim.roll = channel(m.roll, m.duration, m);
      out.aim = aim;
    } else if (m.roll !== undefined) {
      out.roll = channel(m.roll, m.duration, m);
    }
    if (m.fov !== undefined) out.fov = channel(m.fov, m.duration, m);
    t += m.duration;
    return out;
  });

  const track: Record<string, unknown> = { loop: shot.loop ?? false };
  if (Object.keys(defaults).length > 0) track.defaults = defaults;
  track.shots = shots;
  // Collapse arrays-of-numbers onto one line: "v": [0, 3, -40] reads as a point, not a column.
  return JSON.stringify(track, null, 2).replace(/\[\s*(-?[\d.eE+]+(?:,\s*-?[\d.eE+]+)*)\s*\]/g, (_, body: string) =>
    `[${body.split(/,\s*/).join(", ")}]`,
  ) + "\n";
}

// ---------------------------------------------------------------------------
// L1/L2 — the timed batch
// ---------------------------------------------------------------------------

interface Entry {
  ms: number;
  path: string;
  payload: string;
}

/** Sample a scalar channel across a move into one entry per tick. */
function sampleScalar(v: Scalar, path: string, m: Move, t0: number, dtMs: number, out: Entry[]): void {
  if (!scalarIsPair(v)) {
    out.push({ ms: t0, path, payload: num(v) });
    return;
  }
  const steps = Math.max(1, Math.ceil((m.duration * 1000) / dtMs));
  for (let i = 0; i <= steps; i++) {
    const u = i / steps;
    out.push({
      ms: t0 + u * m.duration * 1000,
      path,
      payload: num(lerp(v[0], v[1], ease(u, m.ease ?? "in-out", m.easePower))),
    });
  }
}

function samplePosition(m: Move, frame: string | undefined, t0: number, dtMs: number, out: Entry[]): void {
  const tail = frame ? ` ${frame}` : "";
  const p = m.position!;
  if (!pointsArePair(p)) {
    out.push({ ms: t0, path: "camera/pose/position", payload: p.map(num).join(" ") + tail });
    return;
  }
  const steps = Math.max(1, Math.ceil((m.duration * 1000) / dtMs));
  for (let i = 0; i <= steps; i++) {
    const u = i / steps;
    const k = ease(u, m.ease ?? "in-out", m.easePower);
    const at: Vec3 = [lerp(p[0][0], p[1][0], k), lerp(p[0][1], p[1][1], k), lerp(p[0][2], p[1][2], k)];
    out.push({
      ms: t0 + u * m.duration * 1000,
      path: "camera/pose/position",
      payload: at.map(num).join(" ") + tail,
    });
  }
}

/**
 * Compile the description into a `/sim/ctl/timed_batch` script: take the camera, set up the
 * static channels, then replay every animated channel as pre-sampled leaf writes, then hand
 * the camera back.
 */
export function toTimedBatch(shot: Shot): string {
  const dtMs = 1000 / (shot.sampleHz ?? 20);
  const lines: string[] = [
    `# ${shot.name} — generated by examples/shotbuilder. Feed to /sim/ctl/timed_batch.`,
    `@id ${shot.name}`,
    "@clock render", // the same clock a camera track plays on
    `@loop ${shot.loop ? 1 : 0}`,
    "",
  ];

  const entries: Entry[] = [];

  // Setup, all at offset 0. Distinct paths in one tick keep their authored order, so the
  // ownership take lands before anything it has to be owned for.
  entries.push({ ms: 0, path: "camera/enabled", payload: "1" });
  if (shot.frame) entries.push({ ms: 0, path: "camera/pose/frame", payload: shot.frame });
  if (shot.anchor) entries.push({ ms: 0, path: "camera/pose/anchor", payload: shot.anchor });
  if (shot.aim) {
    const a = shot.aim;
    let line = a.target;
    if (a.offset) line += ` off ${a.offset.map(num).join(" ")}`;
    if (a.frame) line += ` frame ${a.frame}`;
    if (a.up) line += ` up ${a.up}`;
    entries.push({ ms: 0, path: "camera/pose/aim", payload: line });
  }

  let t0 = 0;
  let orbiting = false;
  for (const [i, m] of shot.moves.entries()) {
    assertDrivesSomething(m, i);
    if (m.orbit) {
      if (m.orbit.radius !== undefined) sampleScalar(m.orbit.radius, "camera/pose/orbit/radius", m, t0, dtMs, entries);
      if (m.orbit.azimuth !== undefined) sampleScalar(m.orbit.azimuth, "camera/pose/orbit/azimuth", m, t0, dtMs, entries);
      if (m.orbit.elevation !== undefined) sampleScalar(m.orbit.elevation, "camera/pose/orbit/elevation", m, t0, dtMs, entries);
      orbiting = true;
    } else if (m.position) {
      // Placement precedence is orbit -> geodetic -> cartesian, so a non-zero orbit radius
      // would keep winning. Writing 0 hands placement back to pose/position.
      if (orbiting) entries.push({ ms: t0, path: "camera/pose/orbit/radius", payload: "0" });
      samplePosition(m, shot.frame, t0, dtMs, entries);
      orbiting = false;
    }
    if (m.fov !== undefined) sampleScalar(m.fov, "camera/pose/fov", m, t0, dtMs, entries);
    if (m.roll !== undefined) sampleScalar(m.roll, "camera/pose/roll", m, t0, dtMs, entries);
    t0 += m.duration * 1000;
  }

  // Eased hand-back over [camera] camera_release_blend_s. Use camera/release for a hard cut.
  entries.push({ ms: t0, path: "camera/enabled", payload: "0" });

  // Offsets are absolute from the schedule's start and must be sorted-stable so that, within
  // one tick, the authored order across distinct paths survives.
  entries.sort((a, b) => a.ms - b.ms);
  for (const e of entries) lines.push(`${num(e.ms)} ${e.path} ${e.payload}`);
  lines.push("commit", "");
  return lines.join("\n");
}
