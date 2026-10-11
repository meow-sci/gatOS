# KSA 5554 follow-ups

The 2026-10-11 upgrade applies the confirmed compatibility fixes against `2026.10.10.5554`.
[Evidence](../scope/ksa-assets-and-versions.md#5554-pass) and the
[live checklist](../docs/VALIDATION.md#ksa-5554-upgrade) distinguish static checks from runtime proof.

1. **Required live validation:** run the 5554 checklist on supported game hardware, recording graphics
   settings and `/sim/status/accessors`. Test the changed descriptor bindings, texture restoration,
   terrain round-trip, multi-star catalog/follow targets, throttle cap, burns and high warp. The arm64
   audit host ran isolated IL seams using a scratch COFF-header adaptation; this does not launch or
   validate the x64 game, its native dependencies or GPU objects.
2. **Optional telemetry expansion:** decide whether to expose MagneticScoop collection state and
   core-specific ThermalCore/AntimatterRamCore properties and tank magnetic-containment capability. Ordinary engine/tank telemetry already
   works. Adding module-specific paths requires the normal snapshot, transport, SPEC, site and MCP
   contract work; it is not silently included in this upgrade.
3. **Interstellar cinematic precision:** measure sub-metre camera offsets at light-year origins.
   Generic body targets now resolve all stars/barycenters, but existing camera composition still
   passes through absolute double-precision ECL positions. Do not claim the stock map/portrait
   precision fixes automatically remove that limitation from gatOS's own director. A future fix
   should keep origin and offset separate through composition and final camera application, with
   numerical regression tests and a live moving-anchor pass.
