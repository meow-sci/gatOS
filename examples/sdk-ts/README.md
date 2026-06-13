# gatos-sdk (TypeScript example)

A small, typed example SDK for the gatOS `/sim` API — **runnable inside the guest** — and the
reference for the project's "build your own SDK in any stack" story (KSA_GAME_INTEGRATION_PLAN
Part 8). The same typed surface sits on top of **either transport**:

- **`FsTransport`** — reads/writes the `/sim` 9p tree (zero dependencies; the default when
  `$GATOS_HTTP` is unset).
- **`HttpTransport`** — talks to the magic HTTP API at `$GATOS_HTTP` (set by the guest at boot
  when the host server is up; e.g. `http://sim:4242/v1`).

`new GatosClient()` picks automatically: HTTP when `$GATOS_HTTP` is present, else the filesystem.
Reads are uniform because the per-vessel `telemetry` document is byte-identical over both
transports; writes are uniform because both carry the same `Command` shape.

## Install & run (inside the guest)

Alpine is musl, and Bun ships a `bun-linux-x64-musl` build:

```sh
# one-time, in the guest:
apk add --no-cache curl unzip
curl -fsSL https://bun.sh/install | bash      # or: apk add nodejs npm

bun run examples/orbit-watch.ts               # live orbit TUI
bun run examples/launch-monitor.ts <vesselId> # events + flameout abort
bun run examples/solar-keeper.ts  <vesselId>  # sensor→actuator loop
```

The SDK core avoids Bun-only APIs, so `node examples/orbit-watch.ts` works too.

## API sketch

```ts
import { GatosClient } from "gatos-sdk";

const sdk = new GatosClient();                       // FsTransport or HttpTransport, auto
const v = sdk.vessel("my-craft");

const t = await v.telemetry();                       // typed VesselTelemetry (atomic)
console.log(t.orbit?.ap, t.power.battery);

await v.ignite();                                    // trigger
await v.setThrottle(0.5);                            // STATE 0..1
await v.engine(0).activate();                        // per-module
await v.setAttitudeMode("prograde");                 // FlightComputer auto-track
await v.light(0).setColor(1, 0, 0);                  // per-instance light colour

// warp-aware time — never sleep against wall time:
await sdk.sleepSim(30);                              // 30 sim-seconds (alarm device / long-poll)
await sdk.atWarp(1, async () => {                    // drop to 1× for a hand-flown maneuver
  await v.setThrottle(1);
  await sdk.sleepSim(10);
  await v.setThrottle(0);
});

for await (const e of sdk.events()) console.log(e);  // reactive events (blocking file / SSE)
```

Commands throw a `GatosError` carrying the errno (`EINVAL`, `ENOENT`, `EBUSY`, …) on failure —
the same vocabulary the failed `write(2)` / HTTP status reports.

## No SDK required

The whole API is shell-native. The pure-shell equivalent of `orbit-watch`:

```sh
# read (file transport)
cat /sim/vessels/active/orbit/apoapsis
jq .orbit.ap < /sim/vessels/active/telemetry

# read (HTTP transport)
curl -s "$GATOS_HTTP/vessels/active/telemetry" | jq .orbit.ap

# write
echo 1   > /sim/vessels/active/ctl/ignite
echo 0.5 > /sim/vessels/active/ctl/throttle
curl -s -X POST "$GATOS_HTTP/command" \
  -d '{"vessel_id":"my-craft","action":"engine.active","ordinal":0,"value":1}'

# wait 30 sim-seconds regardless of warp
echo "$(( $(cat /sim/time/ut | cut -d. -f1) + 30 ))" > /sim/time/alarm
cat /sim/time/alarm    # blocks until reached
```

That is the point of the design: every value is a file or a JSON endpoint, so any language —
or no language — can fly the ship.
