// Game-free unit tests for the sky math. Run with `bun test`.

import { expect, test } from "bun:test";
import {
  ccfDirToLatLon, clamp, epochMillis, normalize, parseDatetime, parseQuat, parseVec3,
  quatConj, quatRotate, realDatetimeString, UNIT_X, wrap180, type Quat, type Vec3,
} from "./sky.ts";

test("ccfDirToLatLon reads a body-fixed direction as geographic lat/lon", () => {
  expect(ccfDirToLatLon([1, 0, 0])).toEqual({ latDeg: 0, lonDeg: 0 }); // prime meridian, equator
  const east = ccfDirToLatLon([0, 1, 0]);
  expect(east.latDeg).toBeCloseTo(0, 9);
  expect(east.lonDeg).toBeCloseTo(90, 9);
  const pole = ccfDirToLatLon([0, 0, 5]);
  expect(pole.latDeg).toBeCloseTo(90, 9);
  const west = ccfDirToLatLon([-1, 0, 0]);
  expect(Math.abs(west.lonDeg)).toBeCloseTo(180, 9);
});

test("wrap180 / clamp", () => {
  expect(wrap180(190)).toBeCloseTo(-170, 9);
  expect(wrap180(-190)).toBeCloseTo(170, 9);
  expect(wrap180(45)).toBeCloseTo(45, 9);
  expect(clamp(120, -90, 90)).toBe(90);
  expect(clamp(-120, -90, 90)).toBe(-90);
});

test("quatRotate uses the standard active convention (UnitX about Z by 90° → UnitY)", () => {
  const s = Math.SQRT1_2;
  const r = quatRotate([0, 0, s, s], UNIT_X);
  expect(r[0]).toBeCloseTo(0, 9);
  expect(r[1]).toBeCloseTo(1, 9);
  expect(r[2]).toBeCloseTo(0, 9);
});

test("quaternion rotate/conjugate round-trips", () => {
  const q: Quat = normalizeQuat([0, 0.4, 0, 0.917]);
  const v: Vec3 = normalize([0.3, -0.7, 0.65]);
  const back = quatRotate(quatConj(q), quatRotate(q, v));
  expect(back[0]).toBeCloseTo(v[0], 9);
  expect(back[1]).toBeCloseTo(v[1], 9);
  expect(back[2]).toBeCloseTo(v[2], 9);
});

test("realDatetimeString = epoch + ut, rounded to whole seconds", () => {
  const epoch = parseDatetime("2025-11-30T00:00:00");
  expect(realDatetimeString(epoch, 0)).toBe("2025-11-30T00:00:00");
  expect(realDatetimeString(epoch, 3600)).toBe("2025-11-30T01:00:00");
  expect(realDatetimeString(epoch, 86400)).toBe("2025-12-01T00:00:00");
  expect(realDatetimeString(epoch, 90)).toBe("2025-11-30T00:01:30");
  // Rolls a month/year boundary correctly.
  expect(realDatetimeString(epoch, 32 * 86400)).toBe("2026-01-01T00:00:00");
});

test("--game-epoch accepts fractional seconds (J2000 = …T11:58:55.816 UTC)", () => {
  const j2000 = parseDatetime("2000-01-01T11:58:55.816");
  expect(j2000.second).toBeCloseTo(55.816, 9);
  // ut = 0.184 s → 55.816 + 0.184 = 56.000 → rounds to :56.
  expect(realDatetimeString(j2000, 0.184)).toBe("2000-01-01T11:58:56");
  // The fractional epoch shifts which whole second we land on.
  expect(realDatetimeString(j2000, 0)).toBe("2000-01-01T11:58:56"); // 55.816 rounds to 56
});

test("epochMillis is a pure UTC conversion", () => {
  expect(epochMillis(parseDatetime("1970-01-01T00:00:00"))).toBe(0);
  expect(epochMillis(parseDatetime("2000-01-01T00:00:00"))).toBe(Date.UTC(2000, 0, 1));
});

test("leaf parsers", () => {
  expect(parseVec3("1 2 3")).toEqual([1, 2, 3]);
  expect(parseQuat("0 0.3 0 0.95")).toEqual([0, 0.3, 0, 0.95]);
  expect(() => parseQuat("1 2 3")).toThrow();
});

function normalizeQuat(q: Quat): Quat {
  const [x, y, z, w] = q;
  const m = Math.hypot(x, y, z, w);
  return [x / m, y / m, z / m, w / m];
}
