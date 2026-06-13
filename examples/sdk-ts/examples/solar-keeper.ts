// solar-keeper — the canonical sensor→actuator loop: deploy panels when sunlit, retract on the
// dark side. Demonstrates a closed loop using only reads + a STATE control. Run in the guest:
//   bun run examples/solar-keeper.ts <vesselId>
import { GatosClient } from "../src/index.ts";

const id = process.argv[2];
if (!id) {
  console.error("usage: solar-keeper.ts <vesselId>");
  process.exit(1);
}

const sdk = new GatosClient();
const vessel = sdk.vessel(id);

// solar/<n>/occluded and solar/<n>/goal aren't in the telemetry doc, so this loop reads/writes
// them through the transport's command + a direct occluded read (file or HTTP).
let deployed = false;
for (;;) {
  // Use the FsTransport-friendly read via the telemetry document's power as a proxy: panels
  // produce > 0 when sunlit. (For per-panel occlusion, read solar/<n>/occluded directly.)
  const producing = (await vessel.telemetry()).power.prod > 0;
  if (producing && !deployed) {
    await vessel.animation(0).setGoal(1); // deploy
    deployed = true;
    console.log("sunlit → panels deployed");
  } else if (!producing && deployed) {
    await vessel.animation(0).setGoal(0); // retract
    deployed = false;
    console.log("dark → panels retracted");
  }
  await sdk.sleepSim(5);
}
