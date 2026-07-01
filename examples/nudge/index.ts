#!/usr/bin/env bun
// nudge — shift a vessel's CCI position by a fixed delta, keeping its velocity.
//
// Reads the vessel's current CCI position + velocity from the /sim mount, adds the
// (px, py, pz) delta to the position, and teleports it to that new state vector.
// Velocity is passed through unchanged, so the vessel keeps moving exactly as before
// — it just gets "nudged" by the delta. Handy for sliding an EVA kitten along the
// surface a few meters at a time.
//
// CCI = Celestial-Centered Inertial about the vessel's *current* parent body (meters,
// m/s). teleport sets the state about whatever body the vessel is already orbiting; it
// does not change the parent. See SPEC_9P_FILESYSTEM.md §6 and the gatos skill.
//
//   bun index.ts --vehicle Kitten_1 --px 1.5 --py 0 --pz -2
//
// Orbital speeds are large (km/s), so the read→compute→write happens back-to-back with
// the two reads issued concurrently to keep the window between sampling the position
// and committing the teleport as small as possible.

interface Args {
  vehicle: string;
  px: number;
  py: number;
  pz: number;
  simRoot: string;
}

function parseArgs(argv: string[]): Args {
  const opts: Record<string, string> = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith("--")) {
        throw new Error(`missing value for --${key}`);
      }
      opts[key] = next;
      i++;
    }
  }

  const vehicle = opts.vehicle;
  if (!vehicle) throw new Error("--vehicle <id> is required");

  const num = (name: string) => {
    const raw = opts[name] ?? "0";
    const n = Number(raw);
    if (!Number.isFinite(n)) throw new Error(`--${name} must be a finite number (got '${raw}')`);
    return n;
  };

  return {
    vehicle,
    px: num("px"),
    py: num("py"),
    pz: num("pz"),
    simRoot: opts["sim-root"] ?? process.env.GATOS_SIM ?? "/sim",
  };
}

/** Parse a "x y z" CCI vector file into a 3-tuple, validating it has exactly 3 finite components. */
function parseVec3(text: string, label: string): [number, number, number] {
  const parts = text.trim().split(/\s+/).map(Number);
  if (parts.length !== 3 || parts.some((n) => !Number.isFinite(n))) {
    throw new Error(`expected 3 finite numbers in ${label}, got '${text.trim()}'`);
  }
  return [parts[0], parts[1], parts[2]];
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const base = `${args.simRoot}/vessels/by-id/${args.vehicle}`;
  const teleportPath = `${args.simRoot}/debug/vessels/${args.vehicle}/teleport`;

  // Read position + velocity concurrently — minimise the gap before we commit the teleport.
  const [posText, velText] = await Promise.all([
    Bun.file(`${base}/position/cci`).text(),
    Bun.file(`${base}/velocity/cci`).text(),
  ]);

  const [x, y, z] = parseVec3(posText, "position/cci");
  const [vx, vy, vz] = parseVec3(velText, "velocity/cci");

  // Nudge the position by the delta; velocity unchanged.
  const nx = x + args.px;
  const ny = y + args.py;
  const nz = z + args.pz;

  // teleport expects "px py pz vx vy vz" (CCI, m and m/s) terminated by a newline.
  const stateVector = `${nx} ${ny} ${nz} ${vx} ${vy} ${vz}\n`;
  await Bun.write(teleportPath, stateVector);

  console.error(
    `nudged ${args.vehicle} by [${args.px} ${args.py} ${args.pz}] m  ` +
      `(${x.toFixed(2)} ${y.toFixed(2)} ${z.toFixed(2)}) -> (${nx.toFixed(2)} ${ny.toFixed(2)} ${nz.toFixed(2)})`,
  );
}

main().catch((err) => {
  console.error(`nudge: ${err instanceof Error ? err.message : err}`);
  process.exit(1);
});
