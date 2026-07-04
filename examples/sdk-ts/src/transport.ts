import { readFile, writeFile, readdir } from "node:fs/promises";
import { GatosError, errnoForStatus } from "./errors.ts";
import type { Body, Command, SimEvent, TimeInfo, VesselTelemetry } from "./models.ts";

/**
 * One typed surface over either transport. Reads are uniform because the per-vessel telemetry
 * document is byte-identical over 9p and HTTP; writes are uniform because both carry the same
 * {@link Command} shape (the file transport maps it to a `/sim` path, HTTP to `POST /v1/command`).
 */
export interface Transport {
  vesselIds(): Promise<string[]>;
  vesselTelemetry(id: string): Promise<VesselTelemetry>;
  time(): Promise<TimeInfo>;
  bodies(): Promise<Body[]>;
  command(c: Command): Promise<void>;
  events(): AsyncIterable<SimEvent>;
  /** Blocks until sim time reaches `ut`; returns the reached ut. */
  waitUntilUt(ut: number): Promise<number>;
}

/** The default transport: HTTP when `$GATOS_HTTP` is set, else the `/sim` filesystem. */
export function defaultTransport(): Transport {
  const http = process.env.GATOS_HTTP;
  return http ? new HttpTransport(http) : new FsTransport();
}

// --------------------------------------------------------------------------------------------
// Filesystem transport (the /sim 9p tree)
// --------------------------------------------------------------------------------------------

export class FsTransport implements Transport {
  constructor(private readonly root = "/sim") {}

  async vesselIds(): Promise<string[]> {
    return readdir(`${this.root}/vessels/by-id`);
  }

  async vesselTelemetry(id: string): Promise<VesselTelemetry> {
    return JSON.parse(await this.read(`vessels/by-id/${id}/telemetry`)) as VesselTelemetry;
  }

  async time(): Promise<TimeInfo> {
    const auto = (await this.read("time/auto_warp")).split(/\s+/);
    return {
      ut: Number(await this.read("time/ut")),
      warp: Number(await this.read("time/warp")),
      sim_dt: Number(await this.read("time/sim_dt")),
      warp_speeds: (await this.read("time/warp_speeds")).split(/\s+/).filter(Boolean).map(Number),
      auto_warp_active: auto[0] === "1",
      auto_warp_target_ut: auto.length > 1 ? Number(auto[1]) : 0,
    };
  }

  async bodies(): Promise<Body[]> {
    const ids = await readdir(`${this.root}/bodies`);
    return Promise.all(
      ids.map(async (id) => ({
        id,
        class: await this.read(`bodies/${id}/class`),
        parent_id: (await this.read(`bodies/${id}/parent`)) || undefined,
        child_ids: (await this.read(`bodies/${id}/children`)).split("\n").filter(Boolean),
        mass: Number(await this.read(`bodies/${id}/mass`)),
        mean_radius: Number(await this.read(`bodies/${id}/radius`)),
        mu: Number(await this.read(`bodies/${id}/mu`)),
        soi_meters: Number(await this.read(`bodies/${id}/soi`)),
      })),
    );
  }

  async command(c: Command): Promise<void> {
    const { path, payload } = fileCommand(c);
    try {
      await writeFile(`${this.root}/${path}`, `${payload}\n`);
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code ?? "EIO";
      throw new GatosError(code, `write ${path} failed`);
    }
  }

  async *events(): AsyncIterable<SimEvent> {
    // The blocking /sim/events file delivers one JSON line per event; reopen on EOF to keep
    // following (the kernel completes each read() with the line + trailing 0-byte reads).
    for (;;) {
      const text = await this.read("events");
      for (const line of text.split("\n").filter(Boolean)) {
        yield JSON.parse(line) as SimEvent;
      }
    }
  }

  async waitUntilUt(ut: number): Promise<number> {
    await writeFile(`${this.root}/time/alarm`, `${ut}\n`);
    return Number(await this.read("time/alarm"));
  }

  private async read(path: string): Promise<string> {
    return (await readFile(`${this.root}/${path}`, "utf8")).trim();
  }
}

// --------------------------------------------------------------------------------------------
// HTTP transport (the magic /v1 API at $GATOS_HTTP)
// --------------------------------------------------------------------------------------------

export class HttpTransport implements Transport {
  constructor(private readonly base: string) {}

  async vesselIds(): Promise<string[]> {
    return this.getJson<string[]>("vessels");
  }

  async vesselTelemetry(id: string): Promise<VesselTelemetry> {
    return this.getJson<VesselTelemetry>(`vessels/${id}/telemetry`);
  }

  time(): Promise<TimeInfo> {
    return this.getJson<TimeInfo>("time");
  }

  bodies(): Promise<Body[]> {
    return this.getJson<Body[]>("bodies");
  }

  async command(c: Command): Promise<void> {
    const response = await fetch(`${this.base}/command`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(c),
    });
    if (!response.ok) {
      const body = (await response.json().catch(() => ({}))) as { errno?: string; message?: string };
      throw new GatosError(errnoForStatus(response.status, body.errno), body.message ?? "command failed");
    }
  }

  async *events(): AsyncIterable<SimEvent> {
    const response = await fetch(`${this.base}/events`);
    const reader = response.body!.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return;
      buffer += decoder.decode(value, { stream: true });
      let nl: number;
      while ((nl = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (line.startsWith("data:")) {
          yield JSON.parse(line.slice("data:".length).trim()) as SimEvent;
        }
      }
    }
  }

  async waitUntilUt(ut: number): Promise<number> {
    const { reached_ut } = await this.getJson<{ reached_ut: number }>(`time/wait?until=${ut}`);
    return reached_ut;
  }

  private async getJson<T>(path: string): Promise<T> {
    const response = await fetch(`${this.base}/${path}`);
    if (!response.ok) {
      throw new GatosError(errnoForStatus(response.status), `GET ${path} -> ${response.status}`);
    }
    return (await response.json()) as T;
  }
}

/** Maps a {@link Command} to its `/sim` file path + the text payload to write there. */
function fileCommand(c: Command): { path: string; payload: string } {
  const v = `vessels/by-id/${c.vessel_id}`;
  const ord = c.ordinal ?? -1;
  const num = (c.value ?? 0).toString();
  const vec = (c.values ?? []).join(" ");
  switch (c.action) {
    case "vessel.ignite":
      return { path: `${v}/ctl/ignite`, payload: "1" };
    case "vessel.shutdown":
      return { path: `${v}/ctl/shutdown`, payload: "1" };
    case "vessel.stage":
      return { path: `${v}/ctl/stage`, payload: "1" };
    case "vessel.throttle":
      return { path: `${v}/ctl/throttle`, payload: num };
    case "vessel.lights":
      return { path: `${v}/ctl/lights`, payload: num };
    case "vessel.rcs":
      return { path: `${v}/ctl/rcs`, payload: num };
    case "vessel.attitude_mode":
      return { path: `${v}/ctl/attitude_mode`, payload: c.token ?? "" };
    case "vessel.attitude_frame":
      return { path: `${v}/ctl/attitude_frame`, payload: c.token ?? "" };
    case "vessel.attitude_target":
      return { path: `${v}/ctl/attitude_target`, payload: vec };
    case "vessel.burn":
      return { path: `${v}/ctl/burn`, payload: vec };
    case "engine.active":
      return { path: `${v}/engines/${ord}/active`, payload: num };
    case "engine.min_throttle":
      return { path: `${v}/engines/${ord}/min_throttle`, payload: num };
    case "rcs.active":
      return { path: `${v}/rcs/${ord}/active`, payload: num };
    case "light.on":
      return { path: `${v}/lights/${ord}/on`, payload: num };
    case "light.brightness":
      return { path: `${v}/lights/${ord}/brightness`, payload: num };
    case "light.color":
      return { path: `${v}/lights/${ord}/color`, payload: vec };
    case "light.outer_angle":
      return { path: `${v}/lights/${ord}/outer_angle`, payload: num };
    case "light.inner_angle":
      return { path: `${v}/lights/${ord}/inner_angle`, payload: num };
    case "animation.goal":
      return { path: `${v}/animations/${ord}/goal`, payload: num };
    case "decoupler.fire":
      return { path: `${v}/decouplers/${ord}/fire`, payload: "1" };
    case "debug.warp":
      return { path: `debug/time/warp`, payload: num };
    case "camera.focus":
      // View-only camera move to any vehicle/body id (the per-node ctl/focus + bodies/<id>/focus
      // triggers map to the same action; debug/focus is the by-id form).
      return { path: `debug/focus`, payload: c.token ?? c.vessel_id };
    case "debug.control_vessel":
      return { path: `debug/control_vessel`, payload: c.token ?? c.vessel_id };
    case "debug.teleport":
      return { path: `debug/vessels/${c.vessel_id}/teleport`, payload: vec };
    case "debug.impulse": {
      // "x y z [cci|body] [ns|dv]" — frame keyword rides in token, unit keyword in aux.
      const keywords = [c.token, c.aux].filter(Boolean).join(" ");
      return {
        path: `debug/vessels/${c.vessel_id}/impulse`,
        payload: keywords ? `${vec} ${keywords}` : vec,
      };
    }
    case "debug.refill_fuel":
      return { path: `debug/vessels/${c.vessel_id}/refill_fuel`, payload: "1" };
    case "debug.refill_battery":
      return { path: `debug/vessels/${c.vessel_id}/refill_battery`, payload: "1" };
    default:
      throw new GatosError("EINVAL", `no /sim file mapping for action '${c.action}'`);
  }
}
