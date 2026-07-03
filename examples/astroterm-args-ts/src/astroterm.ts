// Assembling and launching the astroterm command line from a solved observer.

import { spawn, type ChildProcess } from "node:child_process";

export interface Observer {
  latitudeDeg: number; // [-90, 90]
  longitudeDeg: number; // [-180, 180]
}

export interface AstrotermOptions {
  observer: Observer;
  datetime: string; // yyyy-mm-ddThh:mm:ss (UTC)
  speed: number; // 0 freezes the sky to the captured game instant
  aspectRatio?: number;
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

/** Build the astroterm argv from a solved observer + cosmetic options. */
export function buildArgs(o: AstrotermOptions): string[] {
  // Round to 6 decimals (~0.1 m) — clean values, no float-noise tails like -80.64999999999998.
  const deg = (x: number) => String(Number(x.toFixed(6)));
  const args: string[] = [
    "-a", deg(o.observer.latitudeDeg),
    "-o", deg(o.observer.longitudeDeg),
    "-d", o.datetime,
    "-s", String(o.speed),
  ];
  if (o.aspectRatio !== undefined) args.push("-r", String(o.aspectRatio));
  if (o.threshold !== undefined) args.push("-t", String(o.threshold));
  if (o.label !== undefined) args.push("-l", String(o.label));
  if (o.fps !== undefined) args.push("-f", String(o.fps));
  if (o.color) args.push("-c");
  if (o.unicode) args.push("-u");
  if (o.constellations) args.push("-C");
  if (o.grid) args.push("-g");
  if (o.braille) args.push("-b");
  if (o.metadata) args.push("-m");
  return args;
}

/** Render an argv as a copy-pasteable shell command line. */
export function commandLine(args: string[]): string {
  return ["astroterm", ...args].map(quote).join(" ");
}

function quote(a: string): string {
  return /^[\w.:+\/-]+$/.test(a) ? a : `'${a.replace(/'/g, "'\\''")}'`;
}

/** Spawn astroterm inheriting the terminal (it takes over the purrTTY tab). */
export function spawnAstroterm(args: string[]): ChildProcess {
  return spawn("astroterm", args, { stdio: "inherit" });
}

/** Spawn astroterm and resolve with its exit code; rejects with the spawn error (e.g. ENOENT). */
export function run(args: string[]): Promise<number> {
  return new Promise((resolve, reject) => {
    const child = spawnAstroterm(args);
    child.on("error", reject);
    child.on("exit", (code) => resolve(code ?? 0));
  });
}
