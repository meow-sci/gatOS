import type { Errno } from "./models.ts";

/**
 * A failed control command. Both transports surface the same {@link Errno}: the file transport
 * maps the failed `write(2)` errno, the HTTP transport maps the `{errno}` body / status code.
 */
export class GatosError extends Error {
  constructor(
    readonly errno: Errno | string,
    message: string,
  ) {
    super(`${errno}: ${message}`);
    this.name = "GatosError";
  }
}

/** Maps an HTTP status (with an optional `{errno}` body) to the frozen vocabulary. */
export function errnoForStatus(status: number, bodyErrno?: string): Errno | string {
  if (bodyErrno) return bodyErrno;
  switch (status) {
    case 400:
      return "EINVAL";
    case 403:
      return "EACCES";
    case 404:
      return "ENOENT";
    case 409:
      return "EBUSY";
    case 501:
      return "EOPNOTSUPP";
    case 504:
      return "ETIMEDOUT";
    default:
      return "EIO";
  }
}
