// orbit-watch — a tiny live TUI of the active vessel's orbit. Run inside the guest:
//   bun run examples/orbit-watch.ts        (uses $GATOS_HTTP if set, else /sim)
import { GatosClient } from "../src/index.ts";

const sdk = new GatosClient();

// The controlled vessel is the one whose telemetry has controlled=true.
async function activeId(): Promise<string | undefined> {
  for (const id of await sdk.vesselIds()) {
    if ((await sdk.vessel(id).telemetry()).controlled) return id;
  }
  return undefined;
}

const id = (await activeId()) ?? (await sdk.vesselIds())[0];
if (!id) {
  console.error("no vessels");
  process.exit(1);
}

for (;;) {
  const t = await sdk.vessel(id).telemetry();
  const o = t.orbit;
  const km = (m: number) => (m / 1000).toFixed(1);
  process.stdout.write(
    `\r${t.id}  ${t.sit.padEnd(10)}  ut=${t.ut.toFixed(0)}  ` +
      (o
        ? `ap=${km(o.ap)}km pe=${km(o.pe)}km ecc=${o.ecc.toFixed(4)} t→pe=${o.t_pe.toFixed(0)}s   `
        : "suborbital   "),
  );
  await sdk.sleepSim(1); // one sim-second, warp-correct
}
