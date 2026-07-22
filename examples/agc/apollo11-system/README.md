# Apollo 11 (July 1969) — custom KSA system

Starts the game with **Earth and the Moon where they really were at Apollo 11 launch**:
sim time zero = **1969-07-16 13:32:00 UTC**, the moment Columbia and Eagle left LC-39A.
Companion to the AGC example (`../`) and the flight guide (`../APOLLO_11_FLIGHT_GUIDE.md`).

## Build & install

The system file embeds the full stock Earth/Luna terrain + texture definitions, which are the
game's own content — so it is **generated from your install**, never shipped:

```sh
bun make-apollo11-system.ts \
    --content "C:/Program Files (x86)/Steam/steamapps/common/Kitten Space Agency/Content/Core" \
    --out     "C:/Program Files (x86)/Steam/steamapps/common/Kitten Space Agency/Content/Apollo11"
```

(any Bun ≥ 1.0; on Linux/macOS point `--content`/`--out` at the equivalent paths — `--out` must
be a new folder next to `Core`.)

Start KSA → the system/modpack selection now offers **"Apollo 11 (July 1969)"**. Pick it; the
choice persists as the last-used system. Re-run the generator after game updates so terrain
data stays current.

## What's placed where (and why you can trust it)

| Thing | Value | Source |
|---|---|---|
| Epoch (sim t = 0) | 1969-07-16 13:32:00 UTC (launch) | mission record |
| Moon orbit | a 385,475 km · e 0.0505 · i 5.145° · Ω 354.21° · ω 289.7° · perigee at t +11.14 d | Meeus mean elements at epoch; a/e from the real July 1969 apsides (apogee Jul 14 404,950 km, perigee Jul 28 366,000 km) |
| Moon at LOI (t≈+3 d) | ≈389,000 km from Earth | reproduced by construction |
| Moon spin | tidally locked, Cassini tilt 1.5424°, node-following azimuth −5.8° | Cassini's laws at the 1969 node |
| Earth orbit | aphelion Jul 4-5 behind it (M=191.8°), perihelion at t +170.6 d | 1969 apsides |
| Earth clock | subsolar longitude 23.4°W at t=0 | 13:32 UT |
| Landing site | stock `Apollo11` landmark, 0.67408°N 23.47297°E | matches `agc-padload`'s default site |

Historical touchdown = sim **t +369,940 s** (Jul 20 20:17:40 UT) — hand that to the padload as
`agc-padload --tland=369940` if you fly the historical timeline.

KSA propagates two-body Kepler orbits, so these osculating elements stay exact forever in-game —
no perturbation drift to chase.

MIT, matching the mod. Source-only example; not part of `gatos.slnx` or CI. The generated
output contains KSA content and belongs in your game folder, not in git.
