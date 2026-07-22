# KSA / gatOS coordinate frames — a working reference for flight programs

This is the reference frame contract you need to do orbital math, set attitude, and place vessels
through `/sim`. It distills KSA's frame model (`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`) into the
concrete facts a program acts on, and adds the attitude-quaternion recipe proven by the working
landing program (`examples/land-o-matic`).

**All KSA frames are right-handed and orthonormal. There is no other convention.**

---

## 1. The frame zoo

| Frame | Center | Orientation | Inertial? | Where it shows up in `/sim` |
|---|---|---|---|---|
| **ECL** (Ecliptic) | the star | Z = ecliptic normal; X = zero-longitude (vernal); Y = Z×X | yes | `bodies/<id>/position/ecl`, `velocity/ecl`; vessel `position/ecl` |
| **ORB** (perifocal) | focus | in the orbit plane (periapsis on X) | yes | implied by orbital elements |
| **CCE / STAR** (Celestial-Centered Ecliptic) | a body | **same axes as ECL**, re-centered on the body | yes | vehicle attitude is internally kept here; "STAR" in the KSA UI |
| **CCF** (Celestial-Centered Fixed) | a body | Z = north pole (spin axis); X = prime-meridian∩equator; rotates with the body | **no** | latitude/longitude live here (`position/lat`, `position/lon`) |
| **CCI** (Celestial-Centered Inertial) | a body | Z = north pole (spin axis); **X = vernal point (fixed)**; Y = Z×X | yes | **the main I/O frame**: `position/cci`, `velocity/cci`, `attitude/quat` (Body→CCI), `debug/teleport`, `debug/…/impulse` (default frame), `ctl/burn` Δv |

The relationship that matters most in practice:

- **CCI vs CCF**: same Z (north pole). CCF's X spins with the planet; CCI's X is frozen at the vernal
  point. They differ by the **hour angle** = `rotation_rate · (elapsed since prime meridian crossed
  the vernal point)`. Surface points (lat/lon) are fixed in CCF; orbits are described in CCI.
- **CCI is inertial** → Newtonian orbital mechanics work directly in it. Do your orbit math in CCI.
- **CCE/STAR is inertial too** and shares ECL's axes; it's the frame attitude is *internally* kept
  in, but the `/sim` attitude quaternion you read and write is **Body→CCI** (already converted for
  you — see §4).

### Axes of CCI (the frame you'll use constantly)

```
       +Z  (north pole / spin axis)
        |
        |
        +-------- +X  (vernal point: equator ∩ orbit plane, fixed in inertial space)
       /
     +Y   (= Z × X, completes the right-handed set)
```

- The **X–Y plane is the equatorial plane.** An orbit lying in it has **inclination 0** (equatorial).
- Position `(r,0,0)` is on the equator at the vernal-point longitude; velocity `(0,v,0)` there is
  **prograde** (eastward, counterclockwise seen from the north). That pair is a zero-inclination
  circular orbit.

---

## 2. Surface-relative vs inertial velocity

`velocity/cci` (and `vel_cci`) is the **inertial** velocity. The body's surface is moving under the
vessel at `ω × r` (where `ω = rotation_rate · ẑ`, `ẑ = CCI +Z`). For anything referenced to the
ground (landing, surface speed, atmospheric drag), use the **surface-relative** velocity:

```
ω        = [0, 0, rotation_rate]          // CCI, rad/s  (bodies/<parent>/rotation_rate)
v_surface = v_cci − ω × r_cci
```

`/sim` also gives you the scalar `velocity/surface` directly. For vector work, compute it yourself
as above. On a non-rotating body (`rotation_rate = 0`) the two are equal.

---

## 3. Local ENU (East-North-Up) frame at a point

For guidance near a surface (the landing program works here), build a local ENU basis from a CCI
position. ENU columns expressed in CCI:

```
Û  = r_cci / |r_cci|                    // Up   = local vertical (radially out)
ẑ  = [0, 0, 1]                          // CCI north
Ê  = normalize(ẑ × Û)                   // East (degenerates at the poles → fall back to CCI +X)
N̂  = Û × Ê                              // North

R_enu→cci = [ Ê | N̂ | Û ]               // columns are the ENU axes in CCI
```

Transform a CCI vector `w` into ENU by dotting with each axis: `w_enu = [w·Ê, w·N̂, w·Û]`. Transform
back with the linear combination `w_cci = w_e·Ê + w_n·N̂ + w_u·Û`. Local gravity in ENU is
`[0, 0, −μ/|r|²]`. At the poles `ẑ × Û → 0`; guard `|ẑ × Û| < 1e-9` and substitute CCI +X as East.

KSA's own frame names overlap this: `EnuBody` (an East-North-Up body frame) and `Lvlh`
(Local-Vertical-Local-Horizontal) are selectable as `ctl/attitude_frame` reference frames for the
named auto modes — see §5.

---

## 4. Attitude: the Body→CCI quaternion (read AND write)

`attitude/quat` (`att_q`) and the `ctl/attitude_target` write are the **Body→CCI** rotation, stored
`[x, y, z, w]`. Semantics (the invariant the landing program proves):

> Applying the quaternion to a body-frame axis yields that axis expressed in CCI.
> In particular **the vessel's reference/thrust axis is body +X**, so the quaternion `q` you write
> to `ctl/attitude_target` makes the autopilot point body +X along `transform(+X, q)` in CCI.
> `transform(UnitX, q) == desired_direction_cci` holds by construction.

The full body triad is **aircraft-style: +X = nose, +Y = right, +Z = down** (verified against the
RCS translation-thruster geometry — `ctl/translate`'s sign convention follows it: `+x` thrusts along
the nose, `+y` right, `+z` down). Rotation follows the same triad as torque axes — `ctl/rotate`'s
signs are KSA's own torque-command decode: `+x` = roll right (about the nose), `+y` = pitch up
(about the right axis), `+z` = yaw right (about the down axis); full authority needs
`attitude_mode=manual` (an auto hold strips manual rotation).

### To aim the thrust/nose at a desired CCI direction `d̂`

You need a full Body→CCI rotation, i.e. a complete orthonormal body triad expressed in CCI. Body +X
must be `d̂`; the other two axes set the roll (usually irrelevant for thrust, but the quaternion needs
them). Build a triad from `d̂` and a roll reference `p̂` (commonly the local vertical `r_cci`-hat):

```
x̂ = normalize(d̂)                        // body +X → desired thrust direction
s  = x̂ × p̂                              // a vector ⟂ to x̂; if |s| < 1e-9 (d̂ ∥ p̂),
ŷ = normalize(s)                        //   pick any vector ⟂ x̂ instead
ẑ_b = normalize(x̂ × ŷ)                  // body +Z completes the triad
// rows (x̂, ŷ, ẑ_b) form the Body→CCI rotation matrix → convert to quaternion (Shepperd's method)
q = matrix_to_quat(rows = [x̂, ŷ, ẑ_b])  // returns [x, y, z, w]
write "ctl/attitude_target" = "{q.x} {q.y} {q.z} {q.w}"
```

**Use KSA's exact quaternion arithmetic, not a generic library.** The landing program ports KSA's
Hamilton product and matrix→quat conversion verbatim (`Brutal.Numerics`) so the
`transform(UnitX, q) == d̂` round-trip is exact. A different convention (e.g. nalgebra's, or a Y↔Z
swap from kOS-style left-handed math) will *look* close and steer wrong. Reference implementation:
`examples/land-o-matic/src/` (the frames/quaternion module). Verify with the unit test
`transform(UnitX, compute_body2cci(p, d)).approx_eq(d)`.

### Simpler path: let the autopilot do it

If you only need a standard orientation (prograde, retrograde, radial, etc.), **don't build a
quaternion** — write a named mode to `ctl/attitude_mode` (§5). The flight computer steers there
itself, warp-correct, and you avoid the quaternion math entirely. Custom quaternions are for
guidance laws (G-FOLD/UPFG, a custom hold) that compute a direction every tick.

---

## 5. The onboard flight computer (named attitude modes & burns)

The setpoints under `ctl/` are *onboard* — the sim integrates them itself, so they behave correctly
at any time-warp. The guest is mission control; the autopilot flies.

- `ctl/attitude_mode` = `manual` | a track target (`Prograde`, `Retrograde`, `Normal`, `RadialOut`,
  `Toward`, `Antivel`, `Custom`, …; full list in [SPEC §3.4.17](../../../SPEC_9P_FILESYSTEM.md)).
- `ctl/attitude_frame` = the reference frame the named modes resolve against (`EclBody`, `EnuBody`,
  `Lvlh`, `VlfBody`, `BurnBody`, `Dock`).
- `ctl/attitude_target` = a custom Body→CCI quaternion (sets mode → `Custom` internally).
- `ctl/burn` = `ut dvx dvy dvz` — schedule an impulsive maneuver at sim time `ut` with a **CCI** Δv
  vector; the autopilot orients and executes it.

**These four are solver-phase**: they take effect on the next solver step (~10 Hz), not the next
frame. Expect a tick of latency; for closed-loop control run near 1× warp (the SDK's `atWarp(1, …)`
brackets a maneuver).

---

## 6. Orbital math cheat-sheet (all in CCI, SI units)

```
μ      = bodies/<parent>/mu               // m³/s²   (G·M; do not assume 9.8)
R      = bodies/<parent>/radius           // m       (mean radius)
r      = R + altitude                      // orbital radius from center, m
g(r)   = μ / r²                            // local gravity magnitude, m/s²

v_circular(r) = sqrt(μ / r)                // speed for a circular orbit
v_escape(r)   = sqrt(2μ / r)
vis-viva:  v² = μ (2/r − 1/a)              // speed at radius r on an orbit of SMA a
period:    T  = 2π sqrt(a³ / μ)

g₀ = 9.80665 m/s²                          // standard gravity, only for Isp→exhaust velocity
v_exhaust = Isp · g₀                       // m/s
Tsiolkovsky: Δv = v_exhaust · ln(m_wet / m_dry)
TWR = thrust / (mass · g(r))               // dimensionless
```

Altitudes in `/sim` orbit elements are **above the surface** (`apoapsis`/`periapsis` are altitudes,
not radii); add `radius` to get the geocentric radius. Inclination/LAN/argpe/true-anomaly are in
**degrees**; convert to radians for trig.

---

## 7. Pitfalls (the ones that bite)

- **Mass is kg**, not tonnes. (PEGAS/KSP heritage code uses tonnes — don't.)
- **Gravity is `μ/r²`**, never a hardcoded 9.8.
- **Velocity for ground-referenced work is surface-relative** (`v_cci − ω×r`), not raw `vel_cci`.
- **Use the right altitude**: `radar` for terrain clearance, `barometric` for above-mean-radius.
- **Don't substitute a foreign quaternion library** for the Body→CCI math; use KSA's exact arithmetic.
- **Attitude/burn writes are solver-phase** (a tick of latency) and want ~1× warp for closed-loop.
- **Teleport is CCI about the *current* parent** — make sure the vessel is in the intended body's SOI
  first (see [SPEC §6](../../../SPEC_9P_FILESYSTEM.md) and the recipe). The `debug/…/impulse` kick
  shares the frame (and can instead take the vector in the vessel **body** frame, +X = nose); its
  default unit is **N·s** (Δv = J ÷ mass) — append `dv` for straight m/s.
- **Guard non-finite reads**: a closed gate or absent module can yield `0`; sanitize before using.

---

Next: [`flight-programs.md`](flight-programs.md) for the control-loop structure, and
[`recipes.md`](recipes.md) for complete worked programs (including teleport). Full path/format
catalog: [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md).
