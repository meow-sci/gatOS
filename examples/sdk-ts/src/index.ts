// gatos-sdk — a small typed client for the gatOS /sim API, runnable inside the guest on Bun or
// Node. The same typed surface sits on either transport (9p files or the magic HTTP API); pick
// automatically with `new GatosClient()` (HTTP when $GATOS_HTTP is set, else /sim).

export { GatosClient, VesselHandle, EngineHandle, LightHandle, DebugHandle } from "./client.ts";
export { defaultTransport, FsTransport, HttpTransport, type Transport } from "./transport.ts";
export { GatosError } from "./errors.ts";
export type * from "./models.ts";
