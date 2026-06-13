// launch-monitor — watch the event stream and abort on a flameout. Demonstrates reactive events
// and a trigger command. Run in the guest:
//   bun run examples/launch-monitor.ts <vesselId>
import { GatosClient } from "../src/index.ts";

const id = process.argv[2];
if (!id) {
  console.error("usage: launch-monitor.ts <vesselId>");
  process.exit(1);
}

const sdk = new GatosClient();

console.log(`watching events for ${id}…`);
for await (const event of sdk.events()) {
  if (event.vessel && event.vessel !== id) continue;
  console.log(`[${event.ut.toFixed(0)}] ${event.type}: ${event.detail}`);
  if (event.type === "flameout") {
    console.error("flameout → shutting down engines");
    await sdk.vessel(id).shutdown();
    break;
  }
}
