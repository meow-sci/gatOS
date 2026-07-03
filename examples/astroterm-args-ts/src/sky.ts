// The celestial math that turns a /sim look direction into astroterm observer args.
//
// astroterm is a ground-observer planetarium: give it a latitude, a longitude, and a UTC datetime
// and it draws the hemisphere centered on that observer's local zenith at that instant. There is no
// "look direction" — the view is always straight up. So to show the same sky the player sees, we:
//
//   1. Take the in-game look direction (the vessel's radial "up", or its nose) as a unit vector in
//      the HOME body's CCI frame (Z = north pole, X = vernal point) — see ASTROTERM_PLAN.md.
//   2. Express it in the home body's *body-fixed* CCF frame and read off the geographic latitude /
//      longitude whose zenith points that way. (For an Earth-parented vessel looking "up" this is
//      just its own ground point; for Luna / the nose it's a "virtual Earth ground point".)
//   3. Feed astroterm that lat/lon plus datetime = game_epoch + ut. astroterm's own GMST then rotates
//      the sky as game time advances; the --game-epoch is the per-star-data alignment knob (it sets
//      where the in-game star field sits relative to astroterm's J2000 catalog).
//
// All of this is exact quaternion algebra — no orbit integration.

export type Vec3 = readonly [number, number, number];
export type Quat = readonly [number, number, number, number]; // x y z w

export const UNIT_X: Vec3 = [1, 0, 0];

const DEG = 180 / Math.PI;

// ---- vectors & quaternions ------------------------------------------------------------------

export function normalize(v: Vec3): Vec3 {
  const [x, y, z] = v;
  const m = Math.hypot(x, y, z);
  if (m === 0 || !Number.isFinite(m)) throw new Error("cannot normalize a zero/non-finite vector");
  return [x / m, y / m, z / m];
}

/**
 * Rotate a vector by a unit quaternion (active rotation `v' = q ⊗ v ⊗ q*`, the System.Numerics /
 * Brutal `double3.Transform(v, q)` convention KSA uses). Component order is `[x, y, z, w]`.
 */
export function quatRotate(q: Quat, v: Vec3): Vec3 {
  const [qx, qy, qz, qw] = q;
  const [vx, vy, vz] = v;
  const tx = 2 * (qy * vz - qz * vy);
  const ty = 2 * (qz * vx - qx * vz);
  const tz = 2 * (qx * vy - qy * vx);
  return [
    vx + qw * tx + (qy * tz - qz * ty),
    vy + qw * ty + (qz * tx - qx * tz),
    vz + qw * tz + (qx * ty - qy * tx),
  ];
}

/** The conjugate (inverse, for a unit quaternion): rotates the other way. */
export function quatConj(q: Quat): Quat {
  const [x, y, z, w] = q;
  return [-x, -y, -z, w];
}

// ---- a direction → geographic latitude / longitude ------------------------------------------

export interface LatLon {
  latDeg: number; // [-90, 90]
  lonDeg: number; // [-180, 180]
}

/**
 * A unit direction in a body's *body-fixed* (CCF) frame → the geographic lat/lon whose zenith points
 * that way. Z = north pole ⇒ lat = asin z; X = prime meridian ⇒ lon = atan2(y, x). Same formula KSA's
 * `IParentBody.GetLlaFromCcf` uses, so it matches `vessels/.../position/{lat,lon}` by construction.
 */
export function ccfDirToLatLon(v: Vec3): LatLon {
  const [x, y, z] = v;
  const m = Math.hypot(x, y, z);
  if (m === 0 || !Number.isFinite(m)) throw new Error("cannot take lat/lon of a zero/non-finite vector");
  return { latDeg: Math.asin(z / m) * DEG, lonDeg: Math.atan2(y, x) * DEG };
}

export function wrap180(deg: number): number {
  return ((((deg + 180) % 360) + 360) % 360) - 180;
}

export function clamp(x: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, x));
}

// ---- time: game epoch + ut → astroterm UTC datetime -----------------------------------------

export interface DateTimeUtc {
  year: number;
  month: number; // 1..12
  day: number;
  hour: number;
  minute: number;
  second: number; // may be fractional (e.g. J2000 = …T11:58:55.816)
}

/** Parse `yyyy-mm-ddThh:mm:ss` with optional fractional seconds (UTC, no timezone). */
export function parseDatetime(s: string): DateTimeUtc {
  const m = /^(\d{4})-(\d{1,2})-(\d{1,2})T(\d{1,2}):(\d{1,2}):(\d{1,2}(?:\.\d+)?)$/.exec(s.trim());
  if (!m) throw new Error(`datetime must be 'yyyy-mm-ddThh:mm:ss[.fff]' (UTC), got '${s}'`);
  return {
    year: Number(m[1]!),
    month: Number(m[2]!),
    day: Number(m[3]!),
    hour: Number(m[4]!),
    minute: Number(m[5]!),
    second: Number(m[6]!),
  };
}

/** Epoch (UTC of game time 0) → epoch milliseconds since the Unix epoch. */
export function epochMillis(epoch: DateTimeUtc): number {
  const secWhole = Math.floor(epoch.second);
  const ms = Math.round((epoch.second - secWhole) * 1000);
  return Date.UTC(epoch.year, epoch.month - 1, epoch.day, epoch.hour, epoch.minute, secWhole, ms);
}

/**
 * Real UTC datetime of the current game instant = `epoch + ut`, formatted for astroterm
 * (`yyyy-mm-ddThh:mm:ss`, rounded to the nearest whole second — astroterm takes integer seconds).
 */
export function realDatetimeString(epoch: DateTimeUtc, utSeconds: number): string {
  const totalMs = epochMillis(epoch) + utSeconds * 1000;
  const d = new Date(Math.round(totalMs / 1000) * 1000);
  const p = (n: number, w = 2) => String(n).padStart(w, "0");
  return (
    `${p(d.getUTCFullYear(), 4)}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())}` +
    `T${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`
  );
}

// ---- parsing /sim leaf values ---------------------------------------------------------------

export function parseVec3(s: string): Vec3 {
  const p = s.trim().split(/\s+/).map(Number);
  if (p.length < 3 || p.some((n) => !Number.isFinite(n))) throw new Error(`bad vector: '${s}'`);
  return [p[0]!, p[1]!, p[2]!];
}

export function parseQuat(s: string): Quat {
  const p = s.trim().split(/\s+/).map(Number);
  if (p.length < 4 || p.some((n) => !Number.isFinite(n))) throw new Error(`bad quaternion: '${s}'`);
  return [p[0]!, p[1]!, p[2]!, p[3]!];
}
