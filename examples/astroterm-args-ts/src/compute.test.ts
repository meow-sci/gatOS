// End-to-end test of the read → frame-math → argv flow against a fake /sim (no game needed).

import { expect, test } from "bun:test";
import { compute, parseCli } from "./index.ts";
import type { Sim } from "./sim.ts";
import {
  ccfDirToLatLon, normalize, parseDatetime, quatConj, quatRotate, realDatetimeString,
  UNIT_X, type Quat, type Vec3,
} from "./sky.ts";

const RAD = Math.PI / 180;

class FakeSim implements Sim {
  readonly kind = "fake";
  constructor(private readonly leaves: Record<string, string>) {}
  async read(path: string): Promise<string> {
    const v = this.leaves[path];
    if (v === undefined) throw new Error(`fake /sim has no '${path}'`);
    return v.trim();
  }
  async readJson<T>(path: string): Promise<T> {
    return JSON.parse(await this.read(path)) as T;
  }
}

/** Pull a flag's value out of an astroterm argv. */
function arg(args: string[], flag: string): string {
  return args[args.indexOf(flag) + 1]!;
}

test("Earth-parented zenith uses the vessel's own geographic ground point + epoch datetime", async () => {
  const sim = new FakeSim({
    "system/home": "Earth",
    "vessels/active/id": "Hunter",
    "time/ut": "3600",
    "vessels/by-id/Hunter/telemetry": JSON.stringify({
      parent: "Earth", pos_cci: [0, 6_700_000, 0] as Vec3, att_q: [0, 0, 0, 1] as Quat,
    }),
    "vessels/by-id/Hunter/position/lat": "45.5",
    "vessels/by-id/Hunter/position/lon": "-122.25",
  });
  const { args } = await compute(sim, parseCli([]));
  expect(Number(arg(args, "-a"))).toBeCloseTo(45.5, 9);
  expect(Number(arg(args, "-o"))).toBeCloseTo(-122.25, 9);
  // datetime = default epoch (2025-11-30T00:00:00) + 3600 s.
  expect(arg(args, "-d")).toBe(realDatetimeString(parseDatetime("2025-11-30T00:00:00"), 3600));
  expect(args).toContain("-C");
});

test("--game-epoch flows into the datetime", async () => {
  const sim = new FakeSim({
    "system/home": "Earth",
    "vessels/active/id": "Hunter",
    "time/ut": "120",
    "vessels/by-id/Hunter/telemetry": JSON.stringify({
      parent: "Earth", pos_cci: [1, 0, 0] as Vec3, att_q: [0, 0, 0, 1] as Quat,
    }),
    "vessels/by-id/Hunter/position/lat": "0",
    "vessels/by-id/Hunter/position/lon": "0",
  });
  const { args } = await compute(sim, parseCli(["--game-epoch", "2000-01-01T11:58:55.816"]));
  // 11:58:55.816 + 120 s = 12:00:55.816 → rounds to 12:00:56.
  expect(arg(args, "-d")).toBe("2000-01-01T12:00:56");
});

test("Earth-parented nose: maps the nose direction to a virtual Earth ground point", async () => {
  // Earth CCI is the identity wrt ECL; Earth's body-fixed frame is rotated about Z by 60° this tick.
  const qCciEcl: Quat = [0, 0, 0, 1];
  const qCcfEcl: Quat = axisAngle([0, 0, 1], 60 * RAD);
  // att_q rotates body +X to a recognizable Earth-CCI direction (about Z by 90° → +Y).
  const attQ: Quat = axisAngle([0, 0, 1], 90 * RAD);
  const sim = new FakeSim({
    "system/home": "Earth",
    "vessels/active/id": "Hunter",
    "time/ut": "0",
    "vessels/by-id/Hunter/telemetry": JSON.stringify({
      parent: "Earth", pos_cci: [6_700_000, 0, 0] as Vec3, att_q: attQ,
    }),
    "bodies/Earth/orientation/cci_to_ecl": qCciEcl.join(" "),
    "bodies/Earth/orientation/ccf_to_ecl": qCcfEcl.join(" "),
  });
  const { args } = await compute(sim, parseCli(["--view", "nose"]));

  // Independent expectation: nose(+X)→CCI = +Y; →ECL (identity); →CCF (un-rotate 60° about Z).
  const noseCci = quatRotate(attQ, UNIT_X);
  const expected = ccfDirToLatLon(quatRotate(quatConj(qCcfEcl), quatRotate(qCciEcl, noseCci)));
  expect(Number(arg(args, "-a"))).toBeCloseTo(expected.latDeg, 5);
  expect(Number(arg(args, "-o"))).toBeCloseTo(expected.lonDeg, 5);
});

test("Luna-parented zenith: virtual Earth ground point via Luna→ECL→Earth-CCF", async () => {
  const qLunaCciEcl: Quat = axisAngle(normalize3([1, 2, 0.5]), 30 * RAD);
  const qEarthCcfEcl: Quat = axisAngle([0, 0, 1], 200 * RAD);
  const posCci: Vec3 = [1_700_000, 200_000, -50_000]; // Luna CCI
  const sim = new FakeSim({
    "system/home": "Earth",
    "vessels/active/id": "Probe",
    "time/ut": "5000.5",
    "vessels/by-id/Probe/telemetry": JSON.stringify({
      parent: "Luna", pos_cci: posCci, att_q: [0, 0, 0, 1] as Quat,
    }),
    "bodies/Luna/orientation/cci_to_ecl": qLunaCciEcl.join(" "),
    "bodies/Earth/orientation/ccf_to_ecl": qEarthCcfEcl.join(" "),
  });
  const { args } = await compute(sim, parseCli([]));

  const dirEcl = quatRotate(qLunaCciEcl, normalize(posCci));
  const expected = ccfDirToLatLon(quatRotate(quatConj(qEarthCcfEcl), dirEcl));
  expect(Number(arg(args, "-a"))).toBeCloseTo(expected.latDeg, 5);
  expect(Number(arg(args, "-o"))).toBeCloseTo(expected.lonDeg, 5);
  expect(arg(args, "-d")).toBe(realDatetimeString(parseDatetime("2025-11-30T00:00:00"), 5000.5));
});

function axisAngle(axis: Vec3, angleRad: number): Quat {
  const h = angleRad / 2;
  const s = Math.sin(h);
  return [axis[0] * s, axis[1] * s, axis[2] * s, Math.cos(h)];
}
function normalize3(v: Vec3): Vec3 {
  const m = Math.hypot(v[0], v[1], v[2]);
  return [v[0] / m, v[1] / m, v[2] / m];
}
