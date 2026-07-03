#!/usr/bin/env bun
// astroterm-args-ts — point astroterm at the same sky the KSA player is looking at.
//
// Reads the live look direction from /sim (the controlled vessel's radial "up" by default, or its
// nose), turns it into the geographic latitude/longitude on the home body whose zenith points that
// way, and feeds astroterm that observer plus datetime = game_epoch + ut. astroterm then draws the
// real sky overhead at that ground point at the game's (real-world) time.
//
// --game-epoch is the alignment knob: it is the UTC instant that game time 0 maps to, which differs
// per star-data set (the in-game star field sits at a fixed rotation relative to astroterm's J2000
// catalog; choosing the epoch rotates astroterm's sky to match). See ASTROTERM_PLAN.md.

import { defaultSim, type Sim } from "./sim.ts";
import { buildArgs, commandLine, run, spawnAstroterm, type AstrotermOptions } from "./astroterm.ts";
import {
  ccfDirToLatLon, clamp, normalize, parseDatetime, parseQuat, quatConj, quatRotate,
  realDatetimeString, UNIT_X, wrap180, type DateTimeUtc, type Quat, type Vec3,
} from "./sky.ts";

// KSA's documented default: game time 0 == JD 2461009.5 == 2025-11-30T00:00:00 UTC. Override per
// star-data set with --game-epoch (e.g. J2000-aligned data → 2000-01-01T11:58:55.816).
const DEFAULT_GAME_EPOCH = "2025-11-30T00:00:00";

interface Telemetry {
  parent?: string;
  pos_cci: Vec3;
  att_q: Quat;
}

export interface Cli {
  view: "zenith" | "nose";
  vessel?: string;
  epoch: DateTimeUtc;
  print: boolean;
  watchSec?: number;
  speed: number;
  aspectRatio: number;
  threshold?: number;
  label?: number;
  fps?: number;
  color: boolean;
  unicode: boolean;
  constellations: boolean;
  grid: boolean;
  braille: boolean;
  metadata: boolean;
}

const USAGE = `astroterm-args-ts — aim astroterm at the in-game sky (reads /sim)

Usage: astroterm-args-ts [options]

  --view zenith|nose   What to center: the vessel's local "up" (default) or its nose (+X).
  --vessel <id>        Use this vessel instead of the active one.
  --game-epoch <iso>   UTC instant of game time 0 (default ${DEFAULT_GAME_EPOCH}).
                       The per-star-data alignment knob; accepts fractional seconds.
  --print              Print the command instead of running astroterm (default: run it).
  --watch <sec>        Recompute and relaunch every <sec> seconds to track game time / motion.

  --speed <f>          astroterm animation speed (default 0 = frozen to the captured instant).
  --aspect-ratio <f>   Cell aspect ratio (default 2.0; makes the projection round).
  --threshold <f>      Star magnitude threshold (-t).
  --label <f>          Label magnitude threshold (-l).
  --fps <int>          Frames per second (-f).
  --no-color           Disable -c (default on).      --no-unicode         Disable -u (default on).
  --no-constellations  Disable -C (default on).      --no-grid            Disable -g (default on).
  --braille            Enable -b (braille constellation lines).
  --metadata           Enable -m (astroterm metadata overlay).
  -h, --help           This help.

Transport: reads /sim in-guest, or the HTTP /v1 API when $GATOS_HTTP is set (host).`;

export function parseCli(argv: string[]): Cli {
  const c: Cli = {
    view: "zenith", epoch: parseDatetime(DEFAULT_GAME_EPOCH), print: false, speed: 0, aspectRatio: 2.0,
    color: true, unicode: true, constellations: true, grid: true, braille: false, metadata: false,
  };
  const next = (i: number, flag: string): string => {
    const v = argv[i + 1];
    if (v === undefined) throw new Error(`${flag} needs a value`);
    return v;
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    switch (a) {
      case "--view": {
        const v = next(i++, a);
        if (v !== "zenith" && v !== "nose") throw new Error(`--view must be zenith|nose, got '${v}'`);
        c.view = v;
        break;
      }
      case "--vessel": c.vessel = next(i++, a); break;
      case "--game-epoch": c.epoch = parseDatetime(next(i++, a)); break;
      case "--print": c.print = true; break;
      case "--exec": c.print = false; break;
      case "--watch": c.watchSec = Number(next(i++, a)); break;
      case "--speed": c.speed = Number(next(i++, a)); break;
      case "--aspect-ratio": c.aspectRatio = Number(next(i++, a)); break;
      case "--threshold": c.threshold = Number(next(i++, a)); break;
      case "--label": c.label = Number(next(i++, a)); break;
      case "--fps": c.fps = Number(next(i++, a)); break;
      case "--no-color": c.color = false; break;
      case "--no-unicode": c.unicode = false; break;
      case "--no-constellations": c.constellations = false; break;
      case "--no-grid": c.grid = false; break;
      case "--braille": c.braille = true; break;
      case "--metadata": c.metadata = true; break;
      case "-h": case "--help": console.log(USAGE); process.exit(0);
      default: throw new Error(`unknown argument '${a}' (try --help)`);
    }
  }
  return c;
}

/** Read /sim, derive the observer lat/lon + datetime, and build the astroterm argv. */
export async function compute(sim: Sim, cli: Cli): Promise<{ args: string[]; summary: string }> {
  const home = await sim.read("system/home");
  if (!home) throw new Error("no home body in /sim (telemetry_bodies disabled, or not in a flight?)");

  const vesselId = cli.vessel ?? (await sim.read("vessels/active/id"));
  if (!vesselId) throw new Error("no controlled vessel; pass --vessel <id>");

  const ut = Number(await sim.read("time/ut"));
  const datetime = realDatetimeString(cli.epoch, ut);

  const tel = await sim.readJson<Telemetry>(`vessels/by-id/${vesselId}/telemetry`);
  const parent = tel.parent ?? home;

  let latDeg: number;
  let lonDeg: number;
  let how: string;
  if (parent === home && cli.view === "zenith") {
    // The vessel is over the home body: its own geographic ground point is the observer.
    latDeg = Number(await sim.read(`vessels/by-id/${vesselId}/position/lat`));
    lonDeg = Number(await sim.read(`vessels/by-id/${vesselId}/position/lon`));
    how = "ground-point";
  } else {
    // Convert the look direction into the home body's geographic frame (the "virtual ground point"
    // whose zenith points that way): parentCCI → ECL → homeCCF, then read lat/lon.
    const dirParentCci: Vec3 = cli.view === "nose" ? quatRotate(tel.att_q, UNIT_X) : normalize(tel.pos_cci);
    const qParentCciToEcl = parseQuat(await sim.read(`bodies/${parent}/orientation/cci_to_ecl`));
    const qHomeCcfToEcl = parseQuat(await sim.read(`bodies/${home}/orientation/ccf_to_ecl`));
    const dirEcl = quatRotate(qParentCciToEcl, dirParentCci);
    const dirHomeCcf = quatRotate(quatConj(qHomeCcfToEcl), dirEcl);
    const ll = ccfDirToLatLon(dirHomeCcf);
    latDeg = ll.latDeg;
    lonDeg = ll.lonDeg;
    how = "virtual-ground-point";
  }

  const observer = { latitudeDeg: clamp(latDeg, -90, 90), longitudeDeg: wrap180(lonDeg) };
  const opts: AstrotermOptions = {
    observer, datetime, speed: cli.speed, aspectRatio: cli.aspectRatio,
    threshold: cli.threshold, label: cli.label, fps: cli.fps,
    color: cli.color, unicode: cli.unicode, constellations: cli.constellations,
    grid: cli.grid, braille: cli.braille, metadata: cli.metadata,
  };
  const summary =
    `vessel=${vesselId} parent=${parent} home=${home} view=${cli.view} (${how}) ut=${ut.toFixed(1)} | ` +
    `lat=${observer.latitudeDeg.toFixed(3)} lon=${observer.longitudeDeg.toFixed(3)} @ ${datetime}`;
  return { args: buildArgs(opts), summary };
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function main(): Promise<void> {
  const cli = parseCli(process.argv.slice(2));
  const sim = defaultSim();
  console.error(`[astroterm-args] transport=${sim.kind}`);

  if (cli.watchSec !== undefined) {
    if (!(cli.watchSec > 0)) throw new Error("--watch needs a positive number of seconds");
    let child: ReturnType<typeof spawnAstroterm> | undefined;
    const stop = () => { child?.kill(); process.exit(0); };
    process.on("SIGINT", stop);
    process.on("SIGTERM", stop);
    for (;;) {
      const { args, summary } = await compute(sim, cli);
      console.error(`[astroterm-args] ${summary}`);
      if (cli.print) {
        console.log(commandLine(args));
      } else {
        child?.kill();
        child = spawnAstroterm(args);
        child.on("error", (e) => console.error(`[astroterm-args] launch failed: ${e.message}`));
      }
      await sleep(cli.watchSec * 1000);
    }
  }

  const { args, summary } = await compute(sim, cli);
  console.error(`[astroterm-args] ${summary}`);
  if (cli.print) {
    console.log(commandLine(args));
    return;
  }
  try {
    process.exit(await run(args));
  } catch (err) {
    if ((err as NodeJS.ErrnoException).code === "ENOENT") {
      console.error("[astroterm-args] astroterm not found on PATH — printing the command instead:");
      console.log(commandLine(args));
      return;
    }
    throw err;
  }
}

if (import.meta.main) {
  main().catch((err) => {
    console.error(`[astroterm-args] error: ${err instanceof Error ? err.message : err}`);
    process.exit(1);
  });
}
