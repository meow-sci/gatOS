#!/usr/bin/env bun
// shotbuilder — read a shot description, write the two runnable forms of the same move.
//
//   bun index.ts shots/flyby.json --out out
//     out/flyby.tb    -> cat into /sim/ctl/timed_batch      (L1/L2: plain leaf writes)
//     out/flyby.json  -> cp  into /sim/camera/track/flyby   (L3: a typed track)

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { type Shot, toTimedBatch, toTrack } from "./shot.ts";

const args = process.argv.slice(2);
const input = args.find((a) => !a.startsWith("--"));
const outDir = args.includes("--out") ? args[args.indexOf("--out") + 1]! : "out";

if (!input) {
  console.error("usage: bun index.ts <shot.json> [--out <dir>]");
  process.exit(2);
}

// The description is ordinary JSON; JSON.parse rejects comments, so keep shots comment-free.
const shot = JSON.parse(readFileSync(input, "utf8")) as Shot;
if (!shot.name || !Array.isArray(shot.moves) || shot.moves.length === 0) {
  console.error(`${input}: needs a "name" and a non-empty "moves" array`);
  process.exit(2);
}

mkdirSync(outDir, { recursive: true });

const tb = join(outDir, `${shot.name}.tb`);
const track = join(outDir, `${shot.name}.json`);
writeFileSync(tb, toTimedBatch(shot));
writeFileSync(track, toTrack(shot));

const total = shot.moves.reduce((s, m) => s + m.duration, 0);
console.log(`${shot.name}: ${shot.moves.length} move(s), ${total}s`);
console.log(`  ${tb}`);
console.log(`  ${track}`);
