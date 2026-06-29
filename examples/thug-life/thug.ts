#!/usr/bin/env bun

import { parseArgs } from "util";
import { readdir, readFile } from "fs/promises";
import { join } from "path";

interface Options {
  time?: number;
  id?: string;
  meters?: number;
  hz?: number;
  easing?: string;
}

const { values, positionals } = parseArgs({
  args: Bun.argv.slice(2),
  options: {
    time: {
      type: "string",
      short: "t",
      description: "Animation duration in seconds",
    },
    id: {
      type: "string",
      description: "Vehicle ID (will find or create thug_life instance)",
    },
    meters: {
      type: "string",
      short: "m",
      description: "Starting offset in meters (e.g. 1.0 or -0.5)",
    },
    hz: {
      type: "string",
      description: "Animation frame rate in Hz (default: 120)",
    },
    easing: {
      type: "string",
      description: "Easing function: ease-out or linear (default: ease-out)",
    },
  },
  strict: true,
});

const opts: Options = {};
if (values.time) opts.time = parseFloat(values.time as string);
if (values.id) opts.id = values.id as string;
if (values.meters) opts.meters = parseFloat(values.meters as string);
if (values.hz) opts.hz = parseFloat(values.hz as string);
if (values.easing) opts.easing = values.easing as string;

// Validate required args
if (!opts.time || !opts.id || opts.meters === undefined) {
  console.error("Usage: thug.ts --id <vessel-id> --meters <offset> --time <seconds> [--hz <hz>] [--easing ease-out|linear]");
  console.error("  --id:     Vehicle ID (will find or create thug_life instance)");
  console.error("  --meters: Starting offset in meters (e.g. 1.0, -0.5)");
  console.error("  --time:   Animation duration in seconds (e.g. 2.5)");
  console.error("  --hz:     Frame rate in Hz (default: 120)");
  console.error("  --easing: ease-out or linear (default: ease-out)");
  console.error("");
  console.error("Example: thug.ts --id Hunter --meters 1.5 --time 2.0");
  console.error("Example: thug.ts --id Hunter --meters 1.5 --time 2.0 --hz 60 --easing linear");
  process.exit(1);
}

// Set defaults
const hz = opts.hz ?? 120;
const easing = opts.easing ?? "ease-out";

if (!["ease-out", "linear"].includes(easing)) {
  console.error(`Error: Unknown easing function "${easing}". Must be "ease-out" or "linear".`);
  process.exit(1);
}

// Easing functions
function easeOut(t: number): number {
  // Quadratic ease-out: t = 1 - (1-p)^2
  return 1 - Math.pow(1 - t, 2);
}

function linearEase(t: number): number {
  return t;
}

const easingFn = easing === "ease-out" ? easeOut : linearEase;

// Find or create thug_life instance for vessel
async function findOrCreateThugLifeInstance(
  vesselId: string,
  startZ: number
): Promise<string> {
  const thugLifeDir = "/sim/debug/thug_life";
  const FINAL_X = 0.23;
  const FINAL_Y = 0;

  // Try to find existing instance for this vessel
  try {
    const entries = await readdir(thugLifeDir);
    for (const entry of entries) {
      if (entry === "add") continue; // Skip the add control file

      const vesselFile = join(thugLifeDir, entry, "vessel");
      try {
        const content = await readFile(vesselFile, "utf-8");
        if (content.trim() === vesselId) {
          console.error(`Found existing thug_life instance: ${entry}`);
          return entry;
        }
      } catch {
        // File doesn't exist or can't read, continue
      }
    }
  } catch {
    // Directory might not exist yet
  }

  // No existing instance found, create a new one
  console.error(`Creating new thug_life instance for vessel: ${vesselId}`);

  // Get the instance_id of the vessel's first part
  const instanceIdFile = `/sim/vessels/by-id/${vesselId}/parts/0/instance_id`;
  try {
    const instanceId = await readFile(instanceIdFile, "utf-8");

    // Format: vesselId instanceId x y z rotX rotY rotZ sizeX sizeY
    const addCommand = `${vesselId} ${instanceId.trim()} ${FINAL_X} ${FINAL_Y} ${startZ.toFixed(6)} 90 180 90 0.9 0.22`;

    // Write to /sim/debug/thug_life/add with all initialization params
    await Bun.write("/sim/debug/thug_life/add", addCommand);

    // Busywait for the newly created instance to appear on the filesystem
    const maxWaitMs = 5000; // 5 second timeout
    const startTime = Date.now();

    while (Date.now() - startTime < maxWaitMs) {
      try {
        const entries = await readdir(thugLifeDir);
        for (const entry of entries) {
          if (entry === "add") continue;

          const vesselFile = join(thugLifeDir, entry, "vessel");
          try {
            const content = await readFile(vesselFile, "utf-8");
            if (content.trim() === vesselId) {
              console.error(`Created thug_life instance: ${entry}`);
              return entry;
            }
          } catch {
            // File doesn't exist or can't read, continue
          }
        }
      } catch {
        // Directory read failed, retry
      }

      // Sleep 1ms before retrying
      await new Promise((resolve) => setTimeout(resolve, 1));
    }

    throw new Error(
      `Timeout waiting for thug_life instance to appear after write to /sim/debug/thug_life/add`
    );
  } catch (e) {
    console.error(`Error creating thug_life instance: ${e}`);
    process.exit(1);
  }
}

// Animation parameters
const FINAL_X = 0.23;
const FINAL_Y = 0;
const FINAL_Z = -0.33;
const FRAME_TIME_MS = 1000 / hz;

async function main() {
  const startZ = FINAL_Z + opts.meters!;
  const distance = -opts.meters!; // Distance to travel (negative of offset)
  const totalFrames = Math.round(opts.time! * hz);

  // Find or create thug_life instance for the vessel
  const thugLifeInstanceId = await findOrCreateThugLifeInstance(
    opts.id!,
    startZ
  );

  console.error(`Animating ${totalFrames} frames over ${opts.time}s at ${hz}Hz (${easing})`);
  console.error(`  Start Z: ${startZ.toFixed(6)}`);
  console.error(`  End Z:   ${FINAL_Z.toFixed(6)}`);
  console.error("");

  for (let frame = 0; frame < totalFrames; frame++) {
    // Calculate progress (0 to 1)
    const progress = frame / totalFrames;
    // Apply easing function
    const easedProgress = easingFn(progress);
    // Calculate Z position based on eased progress
    const currentZ = startZ + distance * easedProgress;

    const posPath = `/sim/debug/thug_life/${thugLifeInstanceId}/position`;
    const posValue = `${FINAL_X} ${FINAL_Y} ${currentZ.toFixed(6)}`;

    try {
      await Bun.write(posPath, posValue);
    } catch (e) {
      console.error(`Failed to write to ${posPath}: ${e}`);
      process.exit(1);
    }

    await new Promise((resolve) => setTimeout(resolve, FRAME_TIME_MS));
  }

  // Final write to ensure exact final position
  try {
    const posPath = `/sim/debug/thug_life/${thugLifeInstanceId}/position`;
    const posValue = `${FINAL_X} ${FINAL_Y} ${FINAL_Z}`;
    await Bun.write(posPath, posValue);
  } catch (e) {
    console.error(`Failed to write final position: ${e}`);
    process.exit(1);
  }

  console.error("Animation complete.");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
