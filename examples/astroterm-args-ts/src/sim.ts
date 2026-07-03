// Minimal transport reader for the few /sim leaves this tool needs. Same auto-selection as the
// gatos SDK: HTTP (the magic /v1 API) when $GATOS_HTTP is set — the host path — else the in-guest
// /sim 9p mount. Both expose every leaf the same way (HTTP via the /v1/fs field mirror), so one
// `read(path)` covers both. Host (HTTP) mode needs `http_field_endpoints=true` (the default).

import { readFile } from "node:fs/promises";

export interface Sim {
  /** Read a /sim leaf (e.g. `"system/home"`), trimmed of the trailing newline. */
  read(path: string): Promise<string>;
  /** Read a leaf and JSON-parse it (e.g. a vessel `telemetry` document). */
  readJson<T>(path: string): Promise<T>;
  /** Human label for diagnostics. */
  readonly kind: string;
}

/** HTTP when `$GATOS_HTTP` is set (host), else the `/sim` filesystem (in-guest). */
export function defaultSim(): Sim {
  const http = process.env.GATOS_HTTP;
  return http ? new HttpSim(http) : new FsSim();
}

class FsSim implements Sim {
  readonly kind = "fs(/sim)";
  constructor(private readonly root = "/sim") {}

  async read(path: string): Promise<string> {
    return (await readFile(`${this.root}/${path}`, "utf8")).trim();
  }

  async readJson<T>(path: string): Promise<T> {
    return JSON.parse(await this.read(path)) as T;
  }
}

class HttpSim implements Sim {
  readonly kind: string;
  constructor(private readonly base: string) {
    this.kind = `http(${base})`;
  }

  async read(path: string): Promise<string> {
    const res = await fetch(`${this.base}/fs/${path}`);
    if (!res.ok) throw new Error(`GET /fs/${path} -> ${res.status} (host mode needs http_field_endpoints)`);
    return (await res.text()).trim();
  }

  async readJson<T>(path: string): Promise<T> {
    return JSON.parse(await this.read(path)) as T;
  }
}
