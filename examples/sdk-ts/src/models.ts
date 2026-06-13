// Typed views of the gatOS telemetry surface. The per-vessel `telemetry` document is identical
// over both transports (the file `/sim/vessels/<id>/telemetry` and `GET /v1/vessels/<id>/telemetry`
// return the same JSON), which is what lets one typed API sit on top of both.

/** The frozen control-file errno vocabulary (KSA_GAME_INTEGRATION_PLAN Part 2). */
export type Errno =
  | "EINVAL"
  | "ENOENT"
  | "EACCES"
  | "EBUSY"
  | "EIO"
  | "ETIMEDOUT"
  | "EOPNOTSUPP";

/** A 3-vector as serialized in the telemetry document. */
export type Vec3 = [number, number, number];

/** A quaternion `[x, y, z, w]`. */
export type Quat = [number, number, number, number];

/** The atomic per-vessel telemetry document. */
export interface VesselTelemetry {
  seq: number;
  ut: number;
  warp: number;
  id: string;
  sit: string;
  controlled: boolean;
  parent?: string;
  pos_cci: Vec3;
  pos_ecl: Vec3;
  vel_cci: Vec3;
  vel: { orb: number; surf: number; inr: number };
  alt: { baro: number; radar: number };
  mass: { t: number; d: number; p: number };
  att_q: Quat;
  orbit?: {
    ap: number;
    pe: number;
    ecc: number;
    inc: number;
    sma: number;
    period: number;
    ta: number;
    t_ap: number;
    t_pe: number;
  };
  power: { prod: number; cons: number; battery?: number };
}

/** A point-in-time clock reading. */
export interface TimeInfo {
  ut: number;
  warp: number;
  sim_dt: number;
  warp_speeds: number[];
  auto_warp_active: boolean;
  auto_warp_target_ut: number;
}

/** A celestial body. */
export interface Body {
  id: string;
  class: string;
  parent_id?: string;
  child_ids: string[];
  mass: number;
  mean_radius: number;
  mu: number;
  soi_meters: number;
}

/** A discrete sim event. */
export interface SimEvent {
  ut: number;
  type: string;
  vessel?: string | null;
  detail: string;
}

/** The transport-agnostic command shape (mirrors the C# SimCommand). */
export interface Command {
  vessel_id: string;
  action: string;
  ordinal?: number;
  value?: number;
  values?: number[];
  token?: string;
}
