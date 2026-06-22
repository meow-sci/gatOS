import { defaultTransport, type Transport } from "./transport.ts";
import type { Body, Quat, SimEvent, TimeInfo, VesselTelemetry } from "./models.ts";

/**
 * The high-level gatOS client. Reads are typed projections; commands are fluent and throw a
 * {@link import("./errors.ts").GatosError} carrying the errno on failure — identical behaviour over
 * either transport (pick one via {@link defaultTransport}, or pass your own).
 */
export class GatosClient {
  constructor(readonly transport: Transport = defaultTransport()) {}

  time(): Promise<TimeInfo> {
    return this.transport.time();
  }

  bodies(): Promise<Body[]> {
    return this.transport.bodies();
  }

  vesselIds(): Promise<string[]> {
    return this.transport.vesselIds();
  }

  events(): AsyncIterable<SimEvent> {
    return this.transport.events();
  }

  vessel(id: string): VesselHandle {
    return new VesselHandle(this.transport, id);
  }

  // ---- warp-aware time (KSA_GAME_INTEGRATION_PLAN §2.1) --------------------------------------

  /** Blocks until sim time reaches `ut` (the alarm device / long-poll); returns the reached ut. */
  waitUntilUt(ut: number): Promise<number> {
    return this.transport.waitUntilUt(ut);
  }

  /** Sleeps `seconds` of <b>sim</b> time (warp-correct) — never `setTimeout` against wall time. */
  async sleepSim(seconds: number): Promise<number> {
    const now = (await this.transport.time()).ut;
    return this.transport.waitUntilUt(now + seconds);
  }

  /** Sets the time-warp factor (debug namespace; throws EACCES if debug is disabled). */
  setWarp(factor: number): Promise<void> {
    return this.transport.command({ vessel_id: "", action: "debug.warp", value: factor });
  }

  /**
   * Runs `fn` at 1× warp, restoring the previous factor afterwards — the safe wrapper for a
   * hand-flown closed-loop maneuver (control loops are only sound near realtime).
   */
  async atWarp<T>(factor: number, fn: () => Promise<T>): Promise<T> {
    const before = (await this.time()).warp;
    await this.setWarp(factor);
    try {
      return await fn();
    } finally {
      await this.setWarp(before);
    }
  }
}

/** Vessel-scoped commands + reads. */
export class VesselHandle {
  constructor(
    private readonly t: Transport,
    readonly id: string,
  ) {}

  telemetry(): Promise<VesselTelemetry> {
    return this.t.vesselTelemetry(this.id);
  }

  ignite() {
    return this.cmd("vessel.ignite", { value: 1 });
  }

  shutdown() {
    return this.cmd("vessel.shutdown", { value: 1 });
  }

  stage() {
    return this.cmd("vessel.stage", { value: 1 });
  }

  setThrottle(fraction: number) {
    return this.cmd("vessel.throttle", { value: fraction });
  }

  setLights(on: boolean) {
    return this.cmd("vessel.lights", { value: on ? 1 : 0 });
  }

  setRcs(on: boolean) {
    return this.cmd("vessel.rcs", { value: on ? 1 : 0 });
  }

  /** Auto-track an attitude (`prograde`, `retrograde`, …) or `manual`. */
  setAttitudeMode(mode: string) {
    return this.cmd("vessel.attitude_mode", { token: mode });
  }

  setAttitudeFrame(frame: string) {
    return this.cmd("vessel.attitude_frame", { token: frame });
  }

  setAttitudeTarget(quat: Quat) {
    return this.cmd("vessel.attitude_target", { values: quat });
  }

  /** Schedules an impulsive burn at `ut` with the CCI Δv vector. */
  burn(ut: number, dv: [number, number, number]) {
    return this.cmd("vessel.burn", { values: [ut, ...dv] });
  }

  engine(n: number) {
    return new EngineHandle(this.t, this.id, n);
  }

  light(n: number) {
    return new LightHandle(this.t, this.id, n);
  }

  rcsThruster(n: number) {
    return { setActive: (on: boolean) => this.ord("rcs.active", n, on ? 1 : 0) };
  }

  decoupler(n: number) {
    return { fire: () => this.ord("decoupler.fire", n, 1) };
  }

  animation(n: number) {
    return { setGoal: (fraction: number) => this.ord("animation.goal", n, fraction) };
  }

  get debug() {
    return new DebugHandle(this.t, this.id);
  }

  private cmd(action: string, extra: { value?: number; values?: number[]; token?: string }) {
    return this.t.command({ vessel_id: this.id, action, ...extra });
  }

  private ord(action: string, ordinal: number, value: number) {
    return this.t.command({ vessel_id: this.id, action, ordinal, value });
  }
}

export class EngineHandle {
  constructor(
    private readonly t: Transport,
    private readonly id: string,
    private readonly n: number,
  ) {}

  activate() {
    return this.setActive(true);
  }

  deactivate() {
    return this.setActive(false);
  }

  setActive(on: boolean) {
    return this.t.command({ vessel_id: this.id, action: "engine.active", ordinal: this.n, value: on ? 1 : 0 });
  }

  setMinThrottle(fraction: number) {
    return this.t.command({ vessel_id: this.id, action: "engine.min_throttle", ordinal: this.n, value: fraction });
  }
}

export class LightHandle {
  constructor(
    private readonly t: Transport,
    private readonly id: string,
    private readonly n: number,
  ) {}

  on() {
    return this.set(true);
  }

  off() {
    return this.set(false);
  }

  set(on: boolean) {
    return this.t.command({ vessel_id: this.id, action: "light.on", ordinal: this.n, value: on ? 1 : 0 });
  }

  setBrightness(value: number) {
    return this.t.command({ vessel_id: this.id, action: "light.brightness", ordinal: this.n, value });
  }

  setColor(r: number, g: number, b: number) {
    return this.t.command({ vessel_id: this.id, action: "light.color", ordinal: this.n, values: [r, g, b] });
  }

  /** Spotlight beam spread — outer-cone half-angle in degrees (stock 45°, clamped ~0..89.94°). */
  setSpread(degrees: number) {
    return this.t.command({ vessel_id: this.id, action: "light.spread", ordinal: this.n, value: degrees });
  }
}

/** The cheat namespace (only works when `debug_namespace` is enabled host-side). */
export class DebugHandle {
  constructor(
    private readonly t: Transport,
    private readonly id: string,
  ) {}

  refillFuel() {
    return this.t.command({ vessel_id: this.id, action: "debug.refill_fuel", value: 1 });
  }

  refillBattery() {
    return this.t.command({ vessel_id: this.id, action: "debug.refill_battery", value: 1 });
  }

  teleport(stateCci: [number, number, number, number, number, number]) {
    return this.t.command({ vessel_id: this.id, action: "debug.teleport", values: stateCci });
  }

  /** Move the camera to this vessel (view only — does not change control). */
  focus() {
    return this.t.command({ vessel_id: this.id, action: "camera.focus", token: this.id });
  }

  /** Focus the camera on this vessel AND take control of it (cheat-tier). */
  controlHere() {
    return this.t.command({ vessel_id: this.id, action: "debug.control_vessel", token: this.id });
  }
}
