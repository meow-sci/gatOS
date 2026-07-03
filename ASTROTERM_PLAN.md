# ASTROTERM_PLAN.md — line up `astroterm` with the KSA sky via `/sim`

> **Status:** **IMPLEMENTED** (Phases A + B). The gatOS body‑orientation addition (§7b) and the
> `examples/astroterm-args-ts` generator (§8) are built, typecheck clean, and unit‑tested
> (`dotnet test` green, 0 warnings; `bun test` 12/12). What remains is **Phase C** — bundling
> `astroterm` in the guest image (§9) and the in‑game `--game-epoch` calibration pass (§10). See §12.
>
> **Verdict:** **Feasible.** The generator uses astroterm's native **ground‑observer** mode — the
> vessel's geographic lat/lon on the home body + `datetime = game_epoch + ut` — so game time drives
> the view and a single `--game-epoch` (UTC of game time 0, default `2025‑11‑30T00:00:00`) is the
> per‑star‑data alignment knob. A small gatOS addition (each body's CCI/CCF orientation — built)
> handles Luna, other bodies, and the nose view by converting the inertial look direction into a
> *virtual* Earth ground point (pure quaternion algebra). The deliverable is a Bun/TypeScript
> generator `examples/astroterm-args-ts` that reads `/sim` and emits/runs an `astroterm` command
> whose view shows the **same stars** the player is looking at in‑game.

---

## 1. Goal & scope

The player is flying in KSA, whose skybox is a **real star catalog** rendered as a fixed
celestial background. We want to run `astroterm` (a terminal planetarium) inside a purrTTY
session so that its rendered sky **matches the stars/constellations visible in‑game**.

`astroterm` is a *ground‑observer, look‑straight‑up* renderer: give it a latitude, longitude,
and UTC datetime and it draws the hemisphere centered on that observer's **local zenith**. It
has **no** "point the camera" parameter. So the whole problem reduces to:

> Pick the one celestial direction we want at the **center** of `astroterm`'s view, express it
> as **(RA, Dec)** in the real‑world J2000 equatorial frame, then choose a synthetic
> `(latitude, longitude, datetime)` that places that (RA, Dec) at the observer's zenith.

The natural "center" is the player's **local zenith** (the radially‑outward "up" direction at
the vessel) — which is exactly the physical analog of `astroterm`'s model ("stand here, look
up"). On Luna's airless surface this is literally "look up at the sky"; in orbit it is "look
away from the planet at the stars." (Matching an arbitrary free‑look *camera* heading is a
non‑goal — see §7d — because `astroterm` can only ever center the zenith and cannot match roll.)

Deliverables requested:
1. Decide whether gatOS needs more `/sim` data (and spec it if so). → **§7**
2. `examples/astroterm-args-ts`: a TS program that reads `/sim` and builds the `astroterm`
   launch command. → **§8**

---

## 2. How `astroterm` renders the sky (verified from source)

Source: `thirdparty/astroterm/`. Key facts, with file refs:

- **Zenith‑centered azimuthal (stereographic) projection.** The *center* of the terminal view
  is always the observer's **local zenith**; the horizon is the edge of the disc.
  `coord.c:70-82` (`project_stereographic_north`, center `Φ=0` = zenith),
  `core_render.c:13-21` (`horizontal_to_polar`).
- **Only the visible hemisphere is drawn.** Objects below the horizon (`radius_polar > 1`) are
  culled — `core_render.c:23-36`.
- **No view‑direction parameter exists.** The only geometry inputs are `--latitude`,
  `--longitude`, `--datetime` (and cosmetic `--aspect-ratio`). There is no azimuth/elevation/pan
  and no roll. North is always at the top, East at the left — `core_render.c` grid labels.
- **Star catalog is real J2000 RA/Dec** (Yale Bright Star Catalog / BSC5), compiled into the
  binary; proper‑motion is applied to the requested datetime (`astro.c:23-32`,
  `calc_star_position`). Note: **proper motion only — no full precession/nutation/aberration**,
  so the catalog frame is J2000 to ~arcsecond level for nearby dates. Good enough for ASCII.
- **The coordinate pipeline** (`coord.c:14-47`, `equatorial_to_horizontal`):
  ```c
  local_sidereal_time = fmod(gmst + longitude, 2π);   // longitude in RADIANS, east-positive
  hour_angle          = local_sidereal_time - right_ascension;
  altitude = asin( sin(lat)·sin(dec) + cos(lat)·cos(dec)·cos(hour_angle) );
  azimuth  = atan2( sin(H), cos(H)·sin(lat) − tan(dec)·cos(lat) );  // then −π so 0=North
  ```
  CLI degrees → radians at `main.c:473-475` (`longitude *= π/180; latitude *= π/180`). Longitude
  range `[-180,180]`, latitude `[-90,90]`, enforced at `main.c:361-378`.
- **GMST** comes from `astro.c` (`greenwich_mean_sidereal_time_rad` → IERS‑2000 Earth Rotation
  Angle + a precession polynomial). `datetime` is parsed as `%Y-%m-%dT%H:%M:%S` UTC
  (`core.c:501-504`); absent ⇒ system time (`main.c:478-497`).
- **Sun/Moon/planets use real ephemerides** keyed on the datetime (`astro.c` Keplerian solver).
  → **Consequence:** if we feed a *synthetic* datetime to control LST, planets/Moon will be
  placed at their real‑world position for that fake datetime and **will not match KSA**. We only
  claim a match for **stars and constellation figures**, which is what the user cares about.

### 2.1 The identity we exploit

For an observer at latitude `φ` and local sidereal time `LST`, the **zenith** points at
celestial coordinates:

```
Dec_zenith = φ              (declination of the zenith = observer latitude)
RA_zenith  = LST            (right ascension of the zenith = local sidereal time)
```

This is the conceptual basis. **As built** we do not solve `longitude` from GMST (that would make
the datetime — and therefore the game epoch — cancel out). Instead we use astroterm's native
**ground‑observer** mode: feed the vessel's geographic latitude/longitude on the home body and
`datetime = game_epoch + ut`, and let astroterm compute `LST = GMST(datetime) + longitude` itself.
Then the **datetime genuinely drives the view** (Earth's rotation advances the sky with game time),
and shifting `--game-epoch` rotates the whole sky in RA — the per‑star‑data calibration knob (see
§4 and §8.3). For look directions that aren't the vessel's own zenith (Luna, or `--view nose`) we
convert the inertial direction into the equivalent *virtual* geographic ground point — pure
quaternion algebra (§4).

---

## 3. How KSA represents the sky (verified from decompiled sources)

Source: `thirdparty/ksa/`. Key facts, with refs:

- **Stars are real‑world data placed in the ECL (ecliptic) root frame.** They load from binary
  files as `float3` direction vectors (+ scale + RGB) and are rendered **directly in ECL world
  space** with no per‑frame rotation — `KSA/Mod.cs:178-201` (`LoadStarBinaries`),
  `KSA/InstancedStarTechnique.cs:73-89`, `Content/Core/Shaders/Star.vert`.
- **Earth's obliquity is the real value:** `Tilt = 23.441522°`, with `Azimuth = -0.363633°`
  (≈ the precession of the equinox between J2000 and the game epoch ≈ 26 yr × 50.3″/yr) and
  sidereal day `23.9344695944 h` — `Content/Core/Astronomicals.xml:532-537`. Orientation is
  built by `Celestial.CreateOrb2Cci(azimuth, tilt)` = `Rot(Z,−azimuth)·Rot(−X,−tilt)`
  (`KSA/Celestial.cs:456-461`); the game exposes frame quaternions like `GetCci2Cce()`
  (`KSA/Celestial.cs:72-80`).
- **Game epoch:** `/sim time/ut` is seconds since game start, and game time 0 maps by default to
  **JD 2461009.5 = 2025‑11‑30T00:00:00 UTC** (`Astronomicals.xml`). This is the default
  `--game-epoch`. The *effective* epoch can differ per star‑data set (e.g. J2000‑aligned replacement
  data ⇒ `2000‑01‑01T11:58:55.816 UTC`, J2000's UTC instant), because a different star‑field
  rotation is equivalent to a different epoch — so `--game-epoch` is the single calibration input
  (§4, §8.3).

### 3.1 The bridge (why this works at all)

KSA's frames line up with the real sky the same way the real solar system does:

| KSA frame (Earth) | Real‑world analog |
|---|---|
| **ECL** (Z = ecliptic normal, X = zero‑longitude) | J2000 **ecliptic** frame |
| **CCE / STAR** (ECL re‑centered on the body) | ecliptic, body‑centered |
| **CCI** (Z = north pole, X = vernal point, X–Y = equatorial plane) | J2000 **equatorial** frame (RA/Dec) |

Because the star binary is real data in ECL **and** Earth's CCI is built with the real obliquity
about the shared vernal‑point X axis, **a direction expressed in Earth's CCI is, by construction,
real‑world (RA, Dec)** — the exact frame `astroterm`'s BSC5 catalog uses:

```
RA  = atan2( d_cci.y, d_cci.x )      // X = vernal point ⇒ RA 0
Dec = asin ( d_cci.z / |d_cci| )     // Z = north pole  ⇒ Dec +90
```

This is the key result: **for an Earth‑parented vessel, `vessels/.../position/cci` is already a
celestial direction in RA/Dec — no constants, no obliquity, no sign guessing.** It uses KSA's own
CCI, so it automatically carries KSA's exact obliquity *and* the `-0.363633°` precession azimuth.

> Residual unknowns we will pin empirically (§10): (a) whether the binary author aligned ECL's X
> with the real J2000 vernal equinox (a fixed rotation about ECL Z if not), and (b) the sub‑degree
> precession offset. Both collapse into a single constant we can read off by pointing at a known
> bright star and reading the residual. We prefer **KSA‑sourced frames** (`position/cci` or the
> §7b quaternion) over any hardcoded obliquity precisely so these don't bite.

---

## 4. Core algorithm (as built — geographic ground observer + epoch)

```
INPUT (from /sim):
  home   = system/home                       # the Earth-equivalent body id
  ut     = time/ut                           # game seconds since the epoch
  parent = vessels/active/parent             # body the vessel orbits
  epoch  = --game-epoch (default 2025-11-30T00:00:00 UTC)   # game time 0 → real UTC
  view   = --view zenith (default) | nose

STEP 1 — observer latitude/longitude on the HOME body:
  if parent == home AND view == zenith:
      lat = vessels/active/position/lat       # the vessel's own ground point (KSA geodetic)
      lon = vessels/active/position/lon
  else:
      # the "virtual ground point" whose zenith is the look direction
      d_parentCCI = view==nose ? transform(+X, attitude/quat) : unit(position/cci)
      d_ecl       = transform(d_parentCCI, bodies/<parent>/orientation/cci_to_ecl)   # → ECL
      d_homeCCF   = transform(d_ecl, inverse(bodies/<home>/orientation/ccf_to_ecl))  # ECL → home CCF
      lat = deg( asin(d_homeCCF.z) )
      lon = deg( atan2(d_homeCCF.y, d_homeCCF.x) )

STEP 2 — datetime:
  datetime = format_utc( epoch + ut seconds )   # rounded to whole seconds (astroterm -d)

STEP 3 — emit:
  astroterm -a <lat> -o <wrap180(lon)> -d <datetime> -C -c -u -g -r 2.0 -s 0 [cosmetic]
```

Why this makes the epoch matter: astroterm computes the zenith RA as `GMST(datetime) + lon` itself.
Because `lon` is the *physical* home‑body longitude and `datetime = epoch + ut`, the epoch sets where
the in‑game star field sits relative to astroterm's J2000 catalog. As `ut` advances, astroterm's
`GMST` advances at the (real ≈ KSA) sidereal rate, so the sky tracks game time. Different star data
⇒ a different constant rotation ⇒ a different `--game-epoch` that calibrates it out (15°/hour).

Notes:
- `-s 0` freezes the sky to the captured instant; re‑run (or `--watch`) to resync as time/motion
  advance.
- `-r 2.0` compensates for non‑square terminal cells so the disc looks round (README guidance).
- `latitude = Dec` and `LST = RA` puts the target **exactly at zenith** (alt = 90°), nowhere near
  the horizon‑cull edge — no nudge needed.

---

## 5. From where can we actually observe? (feasibility per location)

`astroterm` only needs the **geographic ground point** (on the home body) whose zenith is the look
direction, plus the datetime. So *any* vantage works mathematically. What differs is whether the
ground point is read directly or computed, and whether the **in‑game sky is actually visible** to
eyeball the comparison.

| Vantage | How the observer is found | In‑game sky visible? | Recommendation |
|---|---|---|---|
| **Near‑Earth orbit** (Earth SOI) | the vessel's own `position/lat`/`lon` (sub‑satellite point) | ✅ above atmosphere; best on night side / away from Sun | **Primary target.** Simplest path. |
| **Earth surface, night** | same (`position/lat`/`lon`) | ⚠️ only at night, clear sky; daylight scatter hides stars | Works, but visibility is fiddly. |
| **Luna surface / Luna SOI** | virtual Earth ground point via `cci_to_ecl` + home `ccf_to_ecl` | ✅ **best** — no atmosphere, stars always crisp | **Ideal vantage**; uses the §7b orientation fields. |
| **Deep space / Sun‑parented** | same as Luna | ✅ | Falls out of the same general path. |

**Bottom line:** Earth‑parented zenith reads `position/lat`/`lon` directly; Luna/nose/other bodies
convert the inertial direction with the §7b orientation quaternions. All feed `datetime = epoch+ut`.

---

## 6. What `/sim` gives us (inventory)

From `SPEC_9P_FILESYSTEM.md`:

- `system/home`, `system/sun`, `system/name` — identify the Earth/Sun bodies.
- `time/ut` — game seconds since the epoch (the `datetime = epoch + ut` input).
- `vessels/active/{parent,position/cci,attitude/quat}` (in the atomic `telemetry` doc) — the look
  direction (`position/cci` = local up; `attitude/quat` = Body→CCI for `--view nose`).
- `vessels/active/position/lat` / `lon` — geodetic (CCF) lat/lon = the observer for the Earth‑zenith
  path (this *is* what astroterm wants there).
- `bodies/<id>/orientation/{cci_to_ecl,ccf_to_ecl}` — **§7b, built** — the inertial + body‑fixed
  orientations that convert any look direction into the home body's geographic lat/lon.

---

## 7. gatOS changes

### 7a. Earth‑parented zenith — no new field

When the vessel is parented to `system/home` and the view is the local zenith, the observer is just
the vessel's own `position/lat`/`lon` (already in `/sim`) — no orientation read needed.

### 7b. Body orientation in `/sim` (built — enables Luna, nose & the geographic conversion)

The generator needs two body orientations: each body's **inertial** frame (to express any look
direction in ECL) and the **home** body's **body‑fixed** frame (to read geographic lat/lon, and so
that game‑time rotation tracks). Both come from KSA's own frame data — no magic constants.

**`/sim` nodes** (under each body; sourced from `KSA/Celestial.cs` frame methods — **built**):

| Path | A | Format | Meaning |
|---|---|---|---|
| `bodies/<id>/orientation/cci_to_ecl` | S | quat `x y z w` | **Inertial** CCI → ECL/CCE rotation (fixed). KSA: `IParentBody.GetCci2Cce()`. For the home body, CCI = real equatorial, so a CCI dir is `(RA, Dec)`. |
| `bodies/<id>/orientation/ccf_to_ecl` | S | quat `x y z w` | **Body‑fixed** CCF → ECL/CCE rotation — *rotates with the body each tick*. KSA: `GetCcf2Cce()`. `inverse(ccf_to_ecl)` maps an inertial dir into CCF ⇒ `lat=asin z, lon=atan2 y x`. |
| `bodies/<id>/orientation/pole_ecl` | S | vector | Body north pole (CCI +Z) unit vector in ECL *(convenience; = `cci_to_ecl·[0,0,1]`)*. |
| `bodies/<id>/orientation/vernal_ecl` | S | vector | Body vernal point (CCI +X) unit vector in ECL *(convenience; = `cci_to_ecl·[1,0,0]`)*. |

With these the script does, for any parent (§4 STEP 1 `else` branch):
```
d_ecl     = transform(d_parentCCI, bodies/<parent>/orientation/cci_to_ecl)   # → ECL
d_homeCCF = transform(d_ecl, inverse(bodies/<home>/orientation/ccf_to_ecl))  # ECL → home CCF
lat/lon   = asin(d_homeCCF.z), atan2(d_homeCCF.y, d_homeCCF.x)
```

**Why this shape:** it mirrors `attitude/quat` (Body→CCI) and keeps the **transport‑parity rule**
structural — adding the fields to `SimSnapshot`/`SimJson` lights up 9p + HTTP `/v1/fs` + MQTT by
construction. Read‑only SENSORs (`S`); no command/actuator work.

**Work done for 7b:**
1. `gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs` — `Orientation()` reads `GetCci2Cce()` + `GetCcf2Cce()`
   for every body (`[KsaAnchor]`, the only place a KSA type appears).
2. `gatOS.SimFs/Snapshots/SimSnapshot.cs` — `OrientationSnapshot(CciToEcl, CcfToEcl, PoleEcl, VernalEcl)`
   on `BodySnapshot.Orientation`.
3. `gatOS.SimFs/SimFsTree.cs` — the `bodies/<id>/orientation/*` leaves (`Formats.Quat`/`Vector`).
4. `SimJson` — automatic via the generic record projection (HTTP `/v1/bodies` + MQTT + `/v1/fs` walk).
5. Docs in lockstep: `SPEC_9P_FILESYSTEM.md` §3.3, `docs/KSA_INTEGRATION_MATRIX.md`, the `gatos` skill.
6. `gatOS.SimFs.Tests` — tree crawl, readable leaves, JSON parity. `dotnet test gatos.slnx` green, 0 warnings.

### 7c. (obsolete) hardcoded obliquity

An earlier design considered hardcoding Earth's obliquity (`ε = 23.441522°`) to rotate ECL→equatorial
in userland. Superseded by §7b — the KSA‑sourced quaternions are exact and carry the spin phase, so
no magic constant is used.

### 7d. Out of scope — camera orientation export

Literally matching the *free‑look camera* heading would require exposing the game camera's
orientation in `/sim` (it isn't sim state today). We deliberately don't: `astroterm` can only ever
center the **zenith** and cannot reproduce camera roll, so the extra fidelity buys little. The
generator centers a principled, `/sim`‑derivable direction (zenith by default). If real
camera‑matching is wanted later, it's a separate gatOS feature (a `camera/orientation` quaternion)
and a `--view camera` mode in the generator.

---

## 8. `examples/astroterm-args-ts` — design

A small Bun/TypeScript program that reads `/sim` (in‑guest) or HTTP `/v1` (host), computes the
observer args, and either prints or execs the `astroterm` command. Mirrors `examples/sdk-ts`'s
transport auto‑selection.

### 8.1 Layout
```
examples/astroterm-args-ts/
  package.json            # bun; depends on the sdk-ts package (or a thin transport copy)
  README.md               # usage, what it matches / doesn't, where to observe from
  src/
    index.ts              # CLI entry: parse flags, read /sim, build cmd, print|exec
    sky.ts                # the math: ecl/cci -> RA/Dec, GMST, longitude solve
    sky.test.ts           # unit tests (no game needed): identities + known-star fixtures
    astroterm.ts          # arg assembly + spawn wrapper
```

### 8.2 Transport & reads
- Own thin transport (`sim.ts`, same auto‑select as sdk‑ts: HTTP when `$GATOS_HTTP` set, else `/sim`)
  — the SDK's `bodies()` doesn't carry orientation, so we read leaves directly. Reads: `system/home`,
  `time/ut`, `vessels/active/id`, the atomic `vessels/<id>/telemetry` (`parent`/`pos_cci`/`att_q`),
  `position/lat`+`lon` (Earth‑zenith), and `bodies/<parent>/orientation/cci_to_ecl` +
  `bodies/<home>/orientation/ccf_to_ecl` (other cases).
- Fail with a clear message if no vessel is controlled or `system/home` is empty (gates off).

### 8.3 The math module (`sky.ts`) — self‑contained, unit‑tested
- `quatRotate`/`quatConj` — the System.Numerics/Brutal active‑rotation convention KSA uses (so a
  quaternion read from `/sim` rotates the same way it does in‑game).
- `ccfDirToLatLon(v)` — a body‑fixed direction → `{lat=asin z, lon=atan2 y x}` (matches KSA's
  `GetLlaFromCcf`, so it agrees with `position/lat`/`lon`).
- `realDatetimeString(epoch, ut)` — `epoch + ut`, via `Date.UTC` (handles month/year rollover),
  rounded to whole seconds and formatted `yyyy-mm-ddThh:mm:ss`. `parseDatetime` accepts **fractional
  seconds** (`--game-epoch 2000-01-01T11:58:55.816`).
- **No GMST port needed** — astroterm computes GMST itself from the datetime we hand it (the earlier
  longitude‑solve design that needed the port is gone).
- Tests with no game (`sky.test.ts` + `compute.test.ts`): frame round‑trips, `realDatetimeString`
  rollover + fractional epoch, and the Earth‑zenith / Earth‑nose / Luna‑zenith `compute()` paths
  against a fake `/sim`.

### 8.4 CLI surface (what the generator accepts / emits)
```
astroterm-args-ts [--view zenith|nose] [--vessel <id>] [--game-epoch <iso>] [--exec|--print]
                  [--watch <sec>] [--speed 0] [--aspect-ratio 2.0] [--threshold <f>] [--label <f>]
                  [--fps <int>] [--no-color] [--no-unicode] [--no-constellations] [--no-grid]
                  [--braille] [--metadata]
```
- `--game-epoch <iso>` — UTC of game time 0 (default `2025-11-30T00:00:00`); the alignment knob.
- `--print` (auto‑fallback if `astroterm` not on `$PATH`) prints the command; default `--exec`
  spawns `astroterm` inheriting the TTY (runs in the purrTTY tab).
- Curated default cosmetic flags for purrTTY: `-C -c -u -g -r 2.0 -s 0`.
- `--watch <sec>`: re‑read `/sim` and re‑launch every N seconds to track game time + motion
  (kill/relaunch the child; cheap).

### 8.5 Where it runs
- **In‑guest (preferred):** if Bun is in the Alpine guest, run there, read `/sim` directly, and
  `--exec` `astroterm` straight into the purrTTY tab. (If Bun isn't in the guest image, that's a
  guest‑provisioning follow‑up — see §9 — or run on the host with `--print` and paste the line.)
- **On host:** reads via HTTP `/v1`, `--print` the command; paste into the purrTTY session where
  `astroterm` runs.

---

## 9. Guest provisioning (running `astroterm` inside Alpine)

`astroterm` is a single self‑contained C binary (BSC5 catalog + constellation/Keplerian data are
compiled in via `data/` → C arrays; **no runtime data files**), needing only `ncurses` at runtime.
Options, in order of preference:
1. **Build it into the guest image** (`guest/build-image.sh` / `guest/rootfs-overlay/`): add a
   build step (meson + ncurses‑dev) or drop a prebuilt static `astroterm` into the overlay, and
   add a `THIRD-PARTY-NOTICES.md` entry (MIT). Bumps `GUEST_VERSION`.
2. **`apk add`** at runtime if/when packaged for Alpine (not currently in Alpine repos — verify).
3. **Manual**: user `wget`s a release binary into the guest.

This is a *follow‑up*, not part of the args generator; the generator works regardless (it can
`--print` for a manually‑installed `astroterm`). Decide guest‑bundling separately so we don't
couple the TS deliverable to a guest image bump.

---

## 10. Verification & calibration (in‑game)

The frame conversions are deterministic (unit‑tested). The one empirical step is **calibrating
`--game-epoch`** for the star data in use:

1. **Static frame check (no game):** §8.3 unit tests — frame round‑trips, `epoch+ut` datetime,
   Earth/Luna/nose `compute()` paths.
2. **Calibrate the epoch:** from Luna's surface (or Earth orbit, night side) with the skybox clearly
   visible, run the generator and compare `astroterm`'s constellations to the in‑game sky. If the
   sky is rotated in RA, nudge `--game-epoch` (15° of sky ≈ 1 hour) until a recognizable figure
   (Orion, the Big Dipper) lines up. Record the calibrated epoch per star‑data set.
3. **Sanity anchor:** with a vessel in a known equatorial Earth orbit and the calibrated epoch, the
   ground‑track point and the overhead RA should agree with the live `position/lat`/`lon`.
4. Record results in `docs/VALIDATION.md`, consistent with the repo's T6.6/T9.3 pattern.

---

## 11. Limitations / liberties taken (be honest in the README)

- **Stars & constellations match; Sun/Moon/planets do not** (astroterm uses real ephemerides at the
  datetime; KSA's planets are sim‑specific). Run with `-C` and treat planets as absent.
- **Hemisphere only.** `astroterm` shows the half‑sphere around the zenith and culls the rest;
  in‑game you can see the full sphere.
- **No roll / no pan.** Center = zenith, North = up, always. We can't match a tilted/rotated camera.
- **Alignment is only as good as `--game-epoch`** is calibrated for the loaded star data (§10).
- **Datetime rounds to whole seconds** (astroterm's `-d`): ~0.5 s ⇒ ~0.002° — far below ASCII res.
- **Resync is manual** (re‑run / `--watch`); the captured view is a snapshot of one game instant.
- **Magnitude/threshold** are aesthetic (`-t`/`-l`); they don't affect alignment.

---

## 12. Task checklist

**Phase A — generator (geographic ground observer + epoch):** ✅ done
- [x] A1. Scaffold `examples/astroterm-args-ts` (package.json, README, src/, tsconfig).
- [x] A2. `sky.ts`: `quatRotate`/`quatConj`, `ccfDirToLatLon`, `realDatetimeString` (epoch+ut, fractional
        seconds), `wrap180`/`clamp`, leaf parsers.
- [x] A3. `sky.test.ts` + `compute.test.ts`: frame round‑trips, datetime rollover, Earth/Luna/nose paths.
- [x] A4. `astroterm.ts` + `index.ts`: own `sim.ts` transport, `--view`, `--game-epoch`, `--print`/`--exec`,
        cosmetic flags, `--watch`.
- [x] A5. README: how it works, the epoch knob, where to observe from, how to run in purrTTY.

**Phase B — body‑orientation gatOS addition:** ✅ done
- [x] B1. §7b: `BodyReader.Orientation()` reads `GetCci2Cce()` + `GetCcf2Cce()` per body.
- [x] B2. `bodies/<id>/orientation/{cci_to_ecl,ccf_to_ecl,pole_ecl,vernal_ecl}` (`SimSnapshot`,
        `SimFsTree`); HTTP `/v1/bodies` + MQTT + `/v1/fs` parity automatic via `SimJson`.
- [x] B3. Updated `SPEC_9P_FILESYSTEM.md` §3.3, `docs/KSA_INTEGRATION_MATRIX.md`, the `gatos` skill
        (quick index + `coordinate-frames.md`) — same change.
- [x] B4. `gatOS.SimFs.Tests` (tree crawl, readable leaves, JSON parity); full suite green, zero warnings.
- [x] B5. `index.ts` geographic path: Earth‑zenith uses `position/lat`/`lon`; nose/Luna convert
        parentCCI → ECL → home CCF via the new orientation quaternions; `datetime = epoch + ut`.

**Phase C — guest & validation (follow‑ups, still pending):**
- [ ] C1. Bundle `astroterm` in the guest image (§9) or document manual install; bump `GUEST_VERSION`.
- [ ] C2. In‑game `--game-epoch` calibration (§10); record in `docs/VALIDATION.md`.

---

### Appendix — quick reference
- Default game epoch: `2025-11-30T00:00:00 UTC` (JD 2461009.5). J2000‑aligned data: `2000-01-01T11:58:55.816`.
- Look dir → ECL: `transform(d_parentCCI, bodies/<parent>/orientation/cci_to_ecl)`.
- ECL → home geographic: `d_ccf = transform(d_ecl, inverse(bodies/<home>/orientation/ccf_to_ecl))`,
  `lat = asin(d_ccf.z)`, `lon = atan2(d_ccf.y, d_ccf.x)`.
- Earth‑zenith fast path: `lat = position/lat`, `lon = position/lon` (no orientation read).
- Datetime: `epoch + ut`, rounded to whole seconds.
- `astroterm` good defaults for purrTTY: `-C -c -u -g -r 2.0 -s 0`.
```
