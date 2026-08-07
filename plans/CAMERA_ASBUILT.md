# CAMERA_ASBUILT — the game-free half of tasks C1/C2 of `plans/CAMERA_CONTROLS_PLAN.md`, as built

> **Scope:** the `/sim/camera` filesystem surface, its line grammars, its validation rules, its
> compositing model and its store. 100 % game-free, entirely in `gatOS.SimFs/Camera/` +
> `gatOS.SimFs.Tests/Camera/` (plus the tree wiring in `SimFsTree.cs`). **No `gatOS.GameMod` change is
> part of this work item** — the director, frame resolution and actuators
> (`gatOS.GameMod/Game/Ksa/Camera/**`, plan §6.2) are a separate agent's, as is the `KsaCatalog`
> routing of the `camera.*` family.
>
> This note is the input for the docs lockstep pass (AGENTS.md §9): SPEC, `scope/`, `CLAUDE.md`,
> `docs/MILESTONES.md`, `docs/VALIDATION.md`, and the `gatos` skill. Nothing in
> `SPEC_9P_FILESYSTEM.md`, `scope/` or `docs/` was touched here.
>
> Companion: [`SCHEDULER_ASBUILT.md`](SCHEDULER_ASBUILT.md) (tasks S.0–S.4, the shared
> `PlaybackClock`/registry the camera player will join).

---

## 1. Files added / changed

### Added — `gatOS.SimFs/Camera/`

| File | Contents |
|---|---|
| `CameraTypes.cs` | `FrameKind`, `AimUpKind`, `CameraModeKind`, `TargetKind`, `TargetRef` — the addressing vocabulary |
| `CameraRules.cs` | `CameraRules` — the game-free validation + token tables (AGENTS.md §3 `<Feature>Rules`) |
| `CameraState.cs` | `CameraChannel`, `CameraChannelMask`, `CameraChannels`, `CameraPose`, `CameraState` — the §4.3 compositor |
| `CameraStore.cs` | `CameraLimits`, `CameraTrackLookup`, `CameraTrack`, `CameraTrackInfo`, `CameraStatus`, `CameraStore` (+ nested `CameraUpload`) |
| `CameraDirectory.cs` | `CameraDirectory`, `CameraTrackFile`, `CameraTrackWriteHandle` — the writable `track/` dir |
| `CameraCommands.cs` | `CameraCommands` — the 27 action keys + the six line grammars |
| `CameraFormat.cs` | `CameraFormat` — the `/sim` text projection of `CameraStatus`/`CameraPose` (**additive; see §8.1**) |

Already landed in commit `d9f4468` and **used, not modified**, by this work item: `CameraMath.cs`
(`Vec3`/`Quat`), `Easing.cs`, `Splines.cs`, `PoseSmoother.cs`.

### Changed

| File | Change |
|---|---|
| `gatOS.SimFs/SimFsTree.cs` | new optional `CameraStore? camera = null` param on `Build`; `_camera` field; `CameraDir()` + `CameraPoseDir()`; three new generic control-file helpers (`RangedControl`, `TokenControl`, `LineControl`) |

### Added — tests (`gatOS.SimFs.Tests/Camera/`)

`CameraRulesTests.cs` (93), `CameraCommandsTests.cs` (68), `CameraTreeTests.cs` (65),
`CameraStoreTests.cs` (28), `CameraStateTests.cs` (16) — **270 tests**. `SimFsTreeTests`'
control-enabled tree-crawl guard extended with every new path (it now builds with a `CameraStore`
holding one track, `crawl.json`).

Full solution: **build 0 warnings / 0 errors**, **test 1 199 passed, 0 failed** (`gatOS.SimFs.Tests`
922 passed / 6 skipped).

---

## 2. New public API surface

### `gatOS.SimFs.Camera.FrameKind` (enum)
`Ecl` | `Cce` | `BodyFixed` | `Enu` | `Lvlh` | `Chase`. Tokens `ecl cce bodyfixed enu lvlh chase`.

### `gatOS.SimFs.Camera.AimUpKind` (enum)
`World` | `Target` | `Velocity` | `Free`. Tokens `world target velocity free`.

### `gatOS.SimFs.Camera.CameraModeKind` (enum)
`Orbit` | `Free` | `Map` | `Iva` | `Fixed`. Tokens `orbit free map iva fixed`.

### `gatOS.SimFs.Camera.TargetKind` (enum)
`None` | `Vessel` | `Body` | `Part`.

### `gatOS.SimFs.Camera.TargetRef` (readonly record struct)
`(TargetKind Kind, string Id, string PartInstanceId)`.

| Member | Notes |
|---|---|
| `static TargetRef None` / `Vessel(id)` / `Body(id)` / `Part(vesselId, instanceId)` | factories |
| `bool HasTarget` | `Kind != None` |
| `static bool TryParse(string?, out TargetRef)` | `vessel:<id>` \| `body:<id>` \| `part:<vessel-id>/<instance-id>` \| `none`; kind prefix case-insensitive, ids are not |
| `override string ToString()` | the canonical spelling; **round-trips exactly** (asserted) |

Ids are validated with `CameraRules.IsValidId` — the same `[A-Za-z0-9._-]{1,64}` charset as SPEC §2.2
sanitization, `AudioStore.IsValidName` and `ScheduleStore.IsValidId`. A failed parse yields `None`,
never a half-value. Existence is **not** checked (that is the game seam's ENOENT).

### `gatOS.SimFs.Camera.CameraRules` (static class)

| Member | Notes |
|---|---|
| `static readonly string[] FrameTokens / AimUpTokens / ModeTokens` | indexed by enum ordinal |
| `const double MaxSmoothingSeconds = 10.0` | |
| `static bool IsValidId(string)` | the shared `/sim` charset |
| `TryParseFrame/TryParseAimUp/TryParseMode(string?, out T)` | case-insensitive |
| `NameOf(FrameKind/AimUpKind/CameraModeKind)` → `string?` | null when out of range |
| `IsValidFov(double deg, double min, double max)` | bounds are **parameters** — this class never reads config |
| `IsValidLatitude(double)` | `[-90, 90]` |
| `IsValidLongitude(double)` | `[-180, 360]` accepted (both conventions) |
| `NormalizeLongitude(double)` | folds into canonical `[-180, 180)` |
| `IsValidAltitude(double)` | finite, `≥ 0` |
| `IsValidRoll(double)` | any finite |
| `IsValidOrthoHeight(double)` | finite, **`> 0`** |
| `IsValidSmoothing(double)` | `[0, 10]` |
| `IsValidOrbitRadius(double)` | finite, `≥ 0` |
| `IsValidOrbitAzimuth(double)` | any finite |
| `IsValidOrbitElevation(double)` | `[-90, 90]` |
| `IsValidTimeScale(double)` | finite, `≥ 0` (`0` = pause) |
| `IsFiniteVector(IReadOnlyList<double>?)` / `(…, int arity)` | |
| `IsUnitQuaternionish(IReadOnlyList<double>?)` | 4 finite components, norm in `[0.5, 2]`; **zero rejected** |

> **For the GameMod agent:** re-run these game-side in the actuator. `POST /v1/command` and
> `gatos/command` author a `SimCommand` directly and never touch `CameraCommands`.

### `gatOS.SimFs.Camera.CameraChannel` (enum, 17 members)
`Position, Frame, Anchor, Rotation, AimTarget, AimOffset, AimFrame, AimUp, Roll, Fov, Ortho,
OrthoHeight, Smoothing, OrbitRadius, OrbitAzimuth, OrbitElevation, TimeScale`.
**The ordinal is the mask bit index — append, never reorder.**

### `gatOS.SimFs.Camera.CameraChannelMask` (`[Flags] uint`)
One bit per channel + `None` + `All` (`(1u << 17) - 1`).

### `gatOS.SimFs.Camera.CameraChannels` (static class)
`const int Count = 17`; `Mask(CameraChannel)`; `Has(this CameraChannelMask, CameraChannel)`.

### `gatOS.SimFs.Camera.CameraPose` (readonly record struct, init-only properties)
`Position` (`Vec3`), `PositionIsGeo` (`bool`), `Latitude`, `Longitude`, `Altitude`, `Frame`
(`FrameKind`), `Anchor` (`TargetRef`), `Rotation` (`Quat`), `AimTarget` (`TargetRef`), `AimOffset`
(`Vec3`), `AimFrame` (`FrameKind`), `AimUp` (`AimUpKind`), `Roll`, `Fov`, `Ortho` (`bool`),
`OrthoHeight`, `Smoothing`, `OrbitRadius`, `OrbitAzimuth`, `OrbitElevation`, `TimeScale`;
`static CameraPose Default` (origin / `ecl` / no anchor / identity / no aim / `aim_frame=bodyfixed` /
`up=world` / fov 60 / perspective / ortho_height 1000 / smoothing 0 / time_scale 1).

Init-only properties rather than 21 positional parameters, so construction sites read as
`CameraPose.Default with { Fov = 24 }`.

### `gatOS.SimFs.Camera.CameraState` (sealed class)

| Member | Notes |
|---|---|
| `CameraPose Baseline { get; }` | |
| `CameraChannelMask Overrides { get; }` | which channels carry a live override |
| `void SetBaseline(in CameraPose)` | ownership take |
| `bool HasOverride(CameraChannel)` | |
| `void SetOverride(CameraChannel, double)` | Roll, Fov, OrthoHeight, Smoothing, Orbit{Radius,Azimuth,Elevation}, TimeScale |
| `void SetOverride(CameraChannel, bool)` | Ortho |
| `void SetOverride(CameraChannel, Vec3)` | Position (**also clears `PositionIsGeo`**), AimOffset |
| `void SetOverride(CameraChannel, Quat)` | Rotation |
| `void SetOverride(CameraChannel, FrameKind)` | Frame, AimFrame |
| `void SetOverride(CameraChannel, AimUpKind)` | AimUp |
| `void SetOverride(CameraChannel, TargetRef)` | Anchor, AimTarget |
| `void SetGeoOverride(double lat, double lon, double alt, TargetRef anchor)` | claims `Position`; claims `Anchor` too **only when `anchor.HasTarget`**; normalizes longitude |
| `void ClearOverrides()` | `pose/reset` |
| `void ClearAll()` | `camera/release` — also drops the baseline |
| `CameraPose Compose(in CameraPose? trackSample, CameraChannelMask trackChannels)` | **allocates nothing** (asserted: < 64 B over 10 000 calls) |

A channel/payload mismatch throws `ArgumentOutOfRangeException` (a programming error, not a user one —
the transports can never produce it because the tree wires each leaf to one typed setter).

### `gatOS.SimFs.Camera.CameraLimits` (sealed record)
`(int MaxTracks = 32, int MaxTrackBytes = 1048576, long MaxTotalBytes = 8388608, int MaxKeys = 4096,
double FovMin = 1, double FovMax = 179)`.

### `gatOS.SimFs.Camera.CameraTrackLookup` (enum)
`Ready` | `Uploading` (⇒ EBUSY) | `Missing` (⇒ ENOENT).

### `gatOS.SimFs.Camera.CameraTrack` / `CameraTrackInfo`
`CameraTrack(string Name, byte[] Bytes, int Version)`;
`CameraTrackInfo(string Name, long Bytes, int Version, bool Ready)`.

### `gatOS.SimFs.Camera.CameraStatus` (sealed record)
`(bool Owned, CameraModeKind Mode, TargetRef Follow, bool Tidal, CameraPose Pose, string TrackName,
double TrackTMs, double TrackDurationMs, string ShotName, int ShotIndex, PlaybackState Playback,
double Rate, bool Loop)` + `static CameraStatus Idle`.

`PlaybackState` is `gatOS.SimFs.Commands.PlaybackState` — deliberately the **same** vocabulary as
`ctl/schedules/<id>/state`.

### `gatOS.SimFs.Camera.CameraStore` (sealed class)

| Member | Thread | Notes |
|---|---|---|
| `CameraStore(CameraLimits)` / `CameraStore()` | | |
| `CameraLimits Limits { get; }` | | |
| `CameraState State { get; }` | **game** | the §4.3 compositor; no locks by design |
| `CameraStatus Status { get; }` | any | volatile; `Idle` before the first publish |
| `void PublishStatus(CameraStatus)` | **game** | one volatile swap |
| `Action<CameraTrack>? OnTrackCommitted { get; set; }` | | **the C3 seam** — fired outside the store lock on every commit |
| `static bool IsValidName(string)` | | `= CameraRules.IsValidId` |
| `IReadOnlyList<CameraTrackInfo> List()` | any | name-sorted |
| `bool Exists(string)` / `long SizeOf(string)` / `byte[] SnapshotBytes(string)` | any | |
| `CameraTrackLookup TryGet(string, out CameraTrack?)` | any | committed only |
| `int? CurrentVersion(string)` | any | |
| `CameraUpload OpenUpload(string, bool mustCreate)` | any | EINVAL / EEXIST / ENOSPC |
| `void Delete(string)` | any | ENOENT |
| `void Clear()` | any | mod unload |
| `(int Tracks, long Bytes) Usage()` | any | |
| `void HttpUpload(string, long offset, ReadOnlySpan<byte>, bool complete)` | any | the HTTP chunk mirror |
| `void EmitEvent(SimEvent)` / `IReadOnlyList<SimEvent> DrainEvents()` | | `AudioStore` shape, bounded at 64, oldest dropped |

`CameraStore.CameraUpload` (public nested): `Name`, `Length`, `Write(ulong, ReadOnlySpan<byte>)`,
`SetLength(long)`, `Commit()`, `Abort()`.

### `gatOS.SimFs.Camera.CameraDirectory` / `CameraTrackFile`
`VfsDirectory` / `VfsFile` clones of the audio pair. **`CameraTrackFile` deliberately does NOT override
`IsStreaming`** — see §8.2.

### `gatOS.SimFs.Camera.CameraFormat` (static class)
`const string Absent = "-"`; `FollowId(in CameraStatus)`, `Position(in CameraPose)`,
`Geo(in CameraPose)`, `Aim(in CameraPose)`, `Play(in CameraStatus)`, `Set(in CameraStatus)`,
`Playback(in CameraStatus)`, `Status(in CameraStatus)`, `Info(CameraStore)`, `Frame(FrameKind)`,
`Up(AimUpKind)`, `Mode(CameraModeKind)`.

### `gatOS.SimFs.SimFsTree.Build` (signature change, source-compatible)
```csharp
Build(SnapshotStore store, ICommandSink? commands, Func<string>? transports,
      DisplaySurface? display = null, AudioStore? audio = null, ScheduleStore? schedules = null,
      CameraStore? camera = null)
```

---

## 3. Action keys

All **global** addressing (AGENTS.md §4 mode 1): `VesselId = ""`, `Ordinal = SimCommand.NoOrdinal`.
All **Frame** phase — **none was added to `SimCommand.SolverActions`** (nothing about the camera is
visible to the vehicle solver). Every key is a `const string` on `CameraCommands`.

| Action key | Argument shape | Written from | Errnos the file itself produces |
|---|---|---|---|
| `camera.enabled` | `Value` `0`\|`1` | `camera/enabled` | EINVAL |
| `camera.release` | `Value` `1` | `camera/release` | EINVAL (token ≠ `1`) |
| `camera.mode` | `Token` ∈ `orbit\|free\|map\|iva\|fixed` | `camera/mode` | EINVAL |
| `camera.follow` | `Token` = target ref | `camera/follow` | EINVAL (empty) |
| `camera.tidal` | `Value` `0`\|`1` | `camera/tidal` | EINVAL |
| `camera.position` | `Values` `[x,y,z]`, `Token` = frame token or `""` | `camera/pose/position` | EINVAL |
| `camera.frame` | `Token` = frame token | `camera/pose/frame` | EINVAL |
| `camera.anchor` | `Token` = target ref | `camera/pose/anchor` | EINVAL (empty) |
| `camera.geo` | `Values` `[lat,lon,alt]` (lon normalized), `Token` = `body:<id>` or `""` | `camera/pose/geo` | EINVAL |
| `camera.orbit_radius` | `Value` ≥ 0 finite | `camera/pose/orbit/radius` | EINVAL |
| `camera.orbit_azimuth` | `Value` finite | `camera/pose/orbit/azimuth` | EINVAL |
| `camera.orbit_elevation` | `Value` ∈ `[-90, 90]` | `camera/pose/orbit/elevation` | EINVAL |
| `camera.rotation` | `Values` `[x,y,z,w]`, norm ∈ `[0.5, 2]` | `camera/pose/rotation` | EINVAL |
| `camera.aim` | `Token` = target ref, `Values` = 7-slot array (below) | `camera/pose/aim` | EINVAL |
| `camera.aim_target` | `Token` = target ref | `camera/pose/aim_target` | EINVAL (empty) |
| `camera.aim_offset` | `Values` `[x,y,z]` | `camera/pose/aim_offset` | EINVAL |
| `camera.aim_frame` | `Token` = frame token | `camera/pose/aim_frame` | EINVAL |
| `camera.aim_up` | `Token` ∈ `world\|target\|velocity\|free` | `camera/pose/aim_up` | EINVAL |
| `camera.roll` | `Value` finite (degrees) | `camera/pose/roll` | EINVAL |
| `camera.fov` | `Value` ∈ `[fov_min, fov_max]` (degrees) | `camera/pose/fov` | EINVAL |
| `camera.ortho` | `Value` `0`\|`1` | `camera/pose/ortho` | EINVAL |
| `camera.ortho_height` | `Value` > 0 finite (metres) | `camera/pose/ortho_height` | EINVAL |
| `camera.smoothing` | `Value` ∈ `[0, 10]` (seconds) | `camera/pose/smoothing` | EINVAL |
| `camera.pose_reset` | `Value` `1` | `camera/pose/reset` | EINVAL |
| `camera.play` | `Token` = track name, `Aux` = group or **null**, `Values` = 6-slot array | `camera/play` | EINVAL |
| `camera.set` | `Values` = flat `[key, value, …]` pairs, no `Token` | `camera/set` | EINVAL |
| `camera.stop` | `Value` `1` | `camera/stop` | EINVAL |

### `camera.aim` `Values` slots (`AimSlots = 7`)

| Index | Const | Meaning | Default when the keyword is absent |
|---|---|---|---|
| 0–2 | `AimOffX/Y/Z` | offset in the aim frame, metres | `0 0 0` |
| 3 | `AimFrameOrdinal` | `(int)FrameKind` | `(int)FrameKind.BodyFixed` |
| 4 | `AimUpOrdinal` | `(int)AimUpKind` | `(int)AimUpKind.World` |
| 5 | `AimRoll` | roll, degrees | `0` |
| 6 | `AimRollPresent` | `1` when the line said `roll` | `0` ⇒ **leave the roll channel alone** |

### `camera.play` `Values` slots (`PlaySlots = 6`)

| Index | Const | Meaning | Default |
|---|---|---|---|
| 0 | `PlayAtSeconds` | start offset, seconds ≥ 0 | `0` |
| 1 | `PlayRate` | rate ∈ `[0, 100]` | `1` |
| 2 | `PlayLoop` | `0`\|`1` | `0` |
| 3–5 | `PlayAtPresent` / `PlayRatePresent` / `PlayLoopPresent` | keyword-present flags | `0` |

### `camera.geo` / `camera.position` slots
`GeoLat = 0`, `GeoLon = 1`, `GeoAlt = 2` (`GeoSlots = 3`); `PositionSlots = 3` (x, y, z).

### `camera.set` pair keys
`SetT = 0` (seconds ≥ 0), `SetRate = 1` (`[0, 100]`), `SetLoop = 2` (`0`\|`1`),
`SetPaused = 3` (`0`\|`1`). `const double MaxRate = 100.0`.

> **For the GameMod agent:** route the whole `camera.*` family in `KsaCatalog` before vehicle
> resolution (global addressing), re-validate with `CameraRules`, and apply through
> `CameraStore.State.SetOverride…` so the §4.3 precedence holds for **every** transport. `camera.focus`
> (the existing action) is unchanged and stays where it is.

---

## 4. `/sim` grammars

```
/sim/camera/pose/position   "<x> <y> <z> [<frame>]"
/sim/camera/pose/geo        "<lat> <lon> <alt> [body:<id>]"
/sim/camera/pose/rotation   "<x> <y> <z> <w>"
/sim/camera/pose/aim        "<target> [off <x> <y> <z>] [frame <frame>] [up <up>] [roll <deg>]"
/sim/camera/play            "<track> [at <sec>] [rate <x>] [loop 0|1] [group <token>]"
/sim/camera/set             "[t <sec>] [rate <x>] [loop 0|1] [paused 0|1]"
```

- Keyword groups (`off`/`frame`/`up`/`roll`/`at`/`rate`/`loop`/`group`/`t`/`paused`) are
  **order-independent** and each may appear **at most once** (duplicate ⇒ EINVAL). An unknown keyword
  ⇒ EINVAL. A keyword with a missing/short argument list ⇒ EINVAL.
- Whitespace between fields is collapsed, so column-aligned `timed_batch` scripts parse.
- `camera/set` requires **at least one** adjustment.
- `camera/pose/geo`'s longitude accepts both conventions (`-80.649` and `279.351`) and is **normalized
  to `[-180, 180)` at parse time**, so the read-back is canonical.
- `camera/pose/position` and `camera/pose/geo` leave `Token` **empty** when their optional tail is
  omitted, meaning "use the current `pose/frame` / `pose/anchor`".

---

## 5. New `/sim` paths

Archetypes per SPEC §3: **S** = status read, **St** = state control, **T** = trigger, **D** = directory.
Everything below exists **only when a `CameraStore` is wired** (`[camera] camera_enabled`).
Qid strings are the `/sim`-relative paths verbatim (`camera/pose/orbit/radius`, …).

| Path | A | Format / payload |
|---|---|---|
| `/sim/camera/` | D | |
| `camera/status` | S | multi-line, one `key value…` per line (§6) |
| `camera/info` | S | one line: usage + caps + token vocabularies |
| `camera/target` | S | the follow target's bare id, or `-` |
| `camera/playback` | S | `<state> <t_ms:F1> <duration_ms:F1> <shot> <index> <rate> <loop>` |
| `camera/enabled` | St | Flag → `camera.enabled`; read = owned |
| `camera/release` | T | write `1` → `camera.release` |
| `camera/mode` | St | Enum `orbit\|free\|map\|iva\|fixed` → `camera.mode` |
| `camera/follow` | St | Token (target ref) → `camera.follow` |
| `camera/tidal` | St | Flag → `camera.tidal` |
| `camera/track/` | D | writable upload dir (`CameraDirectory`) |
| `camera/track/<name>` | St | the raw JSON track bytes; create/write/clunk-commit, `rm` evicts |
| `camera/play` | St | Line → `camera.play`; read = the loaded track name or `-` |
| `camera/set` | St | Line → `camera.set`; read = `t <sec> rate <x> loop 0\|1 paused 0\|1` |
| `camera/stop` | T | write `1` → `camera.stop` |
| `camera/pose/` | D | |
| `camera/pose/position` | St | Line → `camera.position`; read = `x y z <frame>` |
| `camera/pose/frame` | St | Enum (frames) → `camera.frame` |
| `camera/pose/anchor` | St | Token (target ref) → `camera.anchor` |
| `camera/pose/geo` | St | Line → `camera.geo`; read = `lat lon alt [body:<id>]` |
| `camera/pose/orbit/radius` | St | Number `[0, ∞)` → `camera.orbit_radius` |
| `camera/pose/orbit/azimuth` | St | Number (finite) → `camera.orbit_azimuth` |
| `camera/pose/orbit/elevation` | St | Number `[-90, 90]` → `camera.orbit_elevation` |
| `camera/pose/rotation` | St | vector(4) `x y z w` → `camera.rotation` (norm-checked; §8.3) |
| `camera/pose/aim` | St | Line → `camera.aim` |
| `camera/pose/aim_target` | St | Token (target ref) → `camera.aim_target` |
| `camera/pose/aim_offset` | St | vector(3) → `camera.aim_offset` |
| `camera/pose/aim_frame` | St | Enum (frames) → `camera.aim_frame` |
| `camera/pose/aim_up` | St | Enum `world\|target\|velocity\|free` → `camera.aim_up` |
| `camera/pose/roll` | St | Number (degrees) → `camera.roll` |
| `camera/pose/fov` | St | Number `[fov_min, fov_max]` → `camera.fov` |
| `camera/pose/ortho` | St | Flag → `camera.ortho` |
| `camera/pose/ortho_height` | St | Number `(0, ∞)` → `camera.ortho_height` |
| `camera/pose/smoothing` | St | Number `[0, 10]` → `camera.smoothing` |
| `camera/pose/reset` | T | write `1` → `camera.pose_reset` |

**Every read leaf is live** (`StaticTextFile`/`LiveLine`, formatted per access, never
snapshot-memoized): the camera moves every rendered frame, far faster than the telemetry publish
cadence.

**Every read-back is the composed effective value** off the director-published `CameraStatus`, not the
last thing written — AGENTS.md §7's resync-after-restart property. The composite read-backs
(`position`, `geo`, `aim`, `rotation`, `play`, `set`) **re-parse through their own grammar**
(asserted), so "read a leaf, write it straight back" is a no-op.

**Transport parity is structural:** these are ordinary VFS leaves, so `VfsScan` mirrors them to
`GET/PUT /v1/fs/sim/camera/pose/fov` and `gatos/sim/camera/pose/fov` with no new code, and
`POST /v1/command` / `gatos/command` reach every `camera.*` action by construction. `ctl/batch` and
`ctl/timed_batch` reach every camera leaf too (asserted end to end).

### `camera/status` layout

```
owned 1
mode fixed
follow vessel:apollo11
tidal 1
anchor body:earth
frame bodyfixed
position -40 8 12
geo 28.5 -80.5 45 0            # lat lon alt <1 when the geodetic spelling is the live one>
rotation 0 0 0 1
aim part:apollo11/77 off 0 1.2 0 frame lvlh up velocity roll -6
fov 42
ortho 1 250                     # <enabled> <half-height m>
smoothing 0.35
orbit 120 30 -15                # radius azimuth elevation
time_scale 0.25
```

### `camera/info` layout

```
enabled=1 owned=0 tracks=1 tracks_max=3 bytes=10 bytes_max=128 track_bytes_max=64 keys_max=8
fov_min=10 fov_max=120 frames=ecl,cce,bodyfixed,enu,lvlh,chase modes=orbit,free,map,iva,fixed
up=world,target,velocity,free
```
(one line; wrapped here for readability)

### Gating (asserted)

- `Build(…, camera: null)` — **no `camera/` node at all**, so the SPEC stays truthful.
- `Build(…)` with a store but **no command sink** — every read stays, every state control degrades to
  a read-only `StaticTextFile` twin, and the three triggers (`release`, `stop`, `pose/reset`)
  **vanish entirely** (a trigger has no value to read).

### Events

`CameraStore.EmitEvent`/`DrainEvents` carry `camera.shot` / `camera.finished` in the `AudioStore` shape
(bounded at 64, oldest dropped). **The event payload strings are C3/C4's to define** — this work item
built the queue, not the emitters.

> **For the GameMod agent:** the telemetry sampler must drain this store exactly like
> `AudioStore`/`ScheduleStore` (`gatOS.GameMod/Game/TelemetrySampler.cs`), gated by `_settings.Events`.

---

## 6. Semantics worth documenting in the SPEC

- **The compositor is three layers: `Track ?? Override ?? Baseline`, per channel.** *Baseline* is
  captured from the live game camera at ownership take and never changes while owned. *Override* is
  every L1/L2 leaf write. *Track* is only the channels the active shot declares. An undeclared channel
  falls through, which is what lets a `timed_batch` pull focus while a track interpolates position.
- **Writing a channel a shot is driving is accepted and superseded on the next frame** — no error, no
  lock. The override is recorded and reappears the moment the shot stops declaring that channel.
- **`pose/reset` clears overrides only.** The active track keeps driving; a reset is about *your*
  writes, not about stopping playback. `camera/release` clears everything including the baseline.
- **`pose/position` and `pose/geo` are two spellings of ONE channel.** Writing either replaces the
  other (a Cartesian write clears the geodetic flag). The director resolves whichever is live and
  publishes **both** back, so a client can read the geodetic form of a Cartesian placement.
- **`pose/geo`'s body tail also sets the anchor**; omitting it keeps the current anchor. The read-back
  emits the tail **only when the anchor is a body**, so a `vessel:`/`none` anchor still re-parses.
- **`pose/aim` sets four channels at once** (aim target, offset, frame, up) with documented defaults
  for the omitted ones — zero offset, `bodyfixed`, `world`. **Roll is the exception:** an aim line
  without `roll` leaves the roll channel alone (slot 6 says so), because roll is animatable on its own.
- **A track is invisible to `play` until its upload commits** (9p clunk / HTTP `complete=1`) —
  `Uploading` ⇒ EBUSY, `Missing` ⇒ ENOENT. A truncate makes the previously-committed bytes unreachable
  *immediately*, exactly like a real file.
- **Committed track bytes are immutable.** A re-upload installs a fresh array under a bumped version,
  so a shot that started on v1 keeps playing v1.
- **Caps are enforced per-write** so the failing `write(2)` carries the real errno: EFBIG (per-track),
  ENOSPC (store total and track count). A clunk cannot carry one.
- **Unlike audio clips, camera tracks ARE in the scalar field mirror** — see §8.2.
- The authoring loop is `cp /mnt/shots/flyby.json /sim/camera/track/flyby` — host-side editing already
  works through the existing `/mnt` passthrough; no watcher, no persistence layer.

---

## 7. Config keys the caps map to (`[camera]`, already in `GatOsConfig.cs` from task C0.2)

| Key | Default | Maps to |
|---|---|---|
| `camera_enabled` | `true` | pass `camera: null` to `SimFsTree.Build` when false |
| `camera_max_tracks` | `32` | `CameraLimits.MaxTracks` |
| `camera_max_track_bytes` | `1048576` | `CameraLimits.MaxTrackBytes` |
| `camera_max_total_bytes` | `8388608` | `CameraLimits.MaxTotalBytes` |
| `camera_max_keys` | `4096` | `CameraLimits.MaxKeys` (**enforced by the C3 track parser, not here**) |
| `camera_fov_min` / `camera_fov_max` | `1` / `179` | `CameraLimits.FovMin` / `FovMax` → the `pose/fov` range |
| `camera_release_blend_s` | `0.6` | **not consumed here** — the director's hand-back blend |
| `camera_allow_time_channel` | `true` | **not consumed here** — the director gates the `TimeScale` channel (also needs `[control] debug_namespace`) |

---

## 8. Deviations from the brief / plan (with justification)

1. **A seventh file, `CameraFormat.cs`, was added** (the brief listed six). *Why:* the camera surface
   has an unusual amount of text projection — a composite line per channel family — and every one of
   those renderings carries a hard obligation to **re-parse through its own grammar**. Putting them in
   one named place makes that obligation testable in isolation (`CameraCommandsTests`
   `.CompositeReadBacks_ReParseThroughTheirOwnGrammar`) and keeps `SimFsTree.cs` from absorbing ~80
   lines of string building. It is additive: no other file's responsibilities moved.

2. **`CameraTrackFile` does not override `IsStreaming`** (deliberate, per the brief) — so camera tracks
   **do** appear in `VfsScan.Leaves` and therefore at `GET/PUT /v1/fs/sim/camera/track/<name>` and
   `gatos/sim/camera/track/<name>`. `AudioClipFile` opts out because a clip is multi-MiB binary; a
   track is small JSON text and is genuinely useful over every transport. `camera_max_track_bytes`
   (1 MiB default) is what keeps that honest. `CameraTreeTests` asserts the **inverse** of
   `AudioTreeTests.ClipFiles_AreExcludedFromTheScalarFieldMirror`.

3. **`camera/pose/rotation` is a `LineControlFile`, not a `VectorControlFile`,** even though the brief
   and the plan both say "Vector4". *Why:* `VectorControlFile` can only range-check components, and a
   zero quaternion passes every component check while naming no rotation at all — normalising it would
   silently substitute identity for whatever the author meant. `CameraCommands.ParseRotation` is
   **wire-identical** (four space-separated reals in, `Values = [x,y,z,w]` out, no `Token`) and adds
   exactly one rule: `CameraRules.IsUnitQuaternionish`. Document it in the SPEC as a vector(4) control
   with an extra norm constraint.

4. **`CameraStatus` carries a whole `CameraPose` instead of separate `Position`/`Rotation`/`Fov`/
   `Ortho`/`AnchorFrame` fields.** The brief listed those five individually, but read-back is required
   for **every** channel (`aim_offset`, `smoothing`, `orbit/*`, …), and duplicating five of them
   alongside the pose would create two definitions that could disagree. `Follow`, `Tidal`, `Mode`,
   `Owned` and the six playback fields stay as named members exactly as briefed; `ShotIndex`, `Rate`
   and `Loop` were added because `camera/playback` is specified to render them.

5. **`CameraLimits` also carries `FovMin`/`FovMax`** (the brief scoped it to the four size caps).
   `pose/fov` is a `ControlFile.Ranged` and the tree needs the bounds at construction time;
   threading a second parameter through `SimFsTree.Build` for them would be worse. `CameraRules`
   itself still takes the bounds as parameters and reads no config, as briefed.

6. **`SetGeoOverride(lat, lon, alt, anchor)` rather than a `SetOverride(CameraChannel, …)` overload.**
   The geodetic spelling takes four arguments of two shapes and conditionally claims a *second*
   channel; an overload set cannot express that, and the explicit name says what it does.

7. **The no-sink tree keeps read-only twins of the state controls and drops only the triggers**, where
   the audio precedent drops the controls entirely. *Why:* an audio `play`/`stop` with no sink is a
   file that can do nothing and says nothing; a camera `pose/fov` with no sink still reports the live
   FOV, which is exactly the read-only-observer case the transports want. Nothing is writable either
   way. Asserted in `CameraTreeTests.NoSink_KeepsTheReads_AndNothingIsWritable`.

8. **Three generic helpers were added to `SimFsTree.Builder`** — `RangedControl`, `TokenControl`,
   `LineControl` — alongside the existing `FlagControl`/`FractionControl`/`NumberControl`/
   `VectorControl`/`EnumControl`. They follow the identical degrade-to-`StaticTextFile` shape and are
   not camera-specific; the camera surface was simply the first consumer to need all three.

9. **`CameraStore.OnTrackCommitted` is the C3 seam**, invoked on the committing thread **outside** the
   store lock. The brief asked for "a clearly-named seam"; a notification hook rather than a validating
   one, because the brief also fixes that "commit does not parse" until C3 lands. C3 can promote it to
   a rejecting validator by throwing `VfsErrorException(EINVAL)` from the handler — the 9p clunk path
   will surface it, and the HTTP path already voids the upload on any throw.

10. **`CameraChannel.TimeScale` exists in the compositor but has no `/sim` leaf.** The plan puts the
    `time` channel in task **C4** and additionally gates it on `[control] debug_namespace`; the channel
    is present here so the compositor's shape is final, but no leaf writes it. `debug/time/warp`
    already covers the discrete case and is already schedulable via `ctl/timed_batch`.

---

## 9. Threading (for the `CLAUDE.md` threading-rules paragraph)

- `CameraState` is **game-thread only**, both for mutation (via the command drain) and for reads (the
  director). **There are no locks in it on purpose** — adding one would only hide a rule violation.
  Transport threads read the volatile `CameraStore.Status` snapshot instead.
- `CameraStore`'s track table is guarded by **one lock** (uploads arrive as ≤512 KiB chunks, so the
  hold times are short memcpys). `PublishStatus` is a single volatile reference swap by the game
  thread; `Status` is read lock-free by every leaf.
- `OnTrackCommitted` is invoked **outside** the store lock (arbitrary caller code must never run while
  the table is held).
- The event queue is a bounded `Queue<SimEvent>` under its own lock, drained by the telemetry sampler.
- `CameraState.Compose` **allocates nothing** (asserted: < 64 B over 10 000 calls) — it runs every
  rendered frame.
- No Harmony patch, no KSA type, no game reference anywhere in this work item.

---

## 10. Still open (not this work item)

- **C1.1/C1.4, C2.1–C2.3** — `gatOS.GameMod/Game/Ksa/Camera/**`: `CameraDirector` (ownership take /
  release / restore capture, `Mod.DriveCamera` in `OnAfterFrame`, teardown in `TeardownGameCheats`),
  `CameraFrames` (the six frames + the `geo` arithmetic), `CameraTargets` (`vessel:`/`body:`/`part:`
  resolution reusing the weld anchor resolver), `CameraReader`; `KsaCatalog` routing of the whole
  `camera.*` family with game-side `CameraRules` re-validation; the `SetFollow`-on-both-cameras fix
  (plan §11.1) applied to `camera.focus` too.
- **C3** — `CameraTrack`/`TrackParser`/`TrackEvaluator`/`Playback`: the JSON schema, `camera_max_keys`
  enforcement, the `CameraChannelMask` a shot declares, registration in `/sim/ctl/schedules` as
  `kind = camera-track`, and the `camera.shot`/`camera.finished` event payloads.
- **C4** — the `time` channel actuator (`Universe.SetSimulationSpeed`), gated on `debug_namespace`.
- Wiring `CameraStore` into `Mod` (construction from `[camera]` config, `SimFsTree.Build(…, camera:)`,
  the sampler's `DrainEvents`, `Clear()` on teardown) and the HTTP `PUT /v1/camera/track/<name>` route
  (`CameraStore.HttpUpload` is built and tested; the route is not).
- Docs lockstep (AGENTS.md §9) from this note.
- `docs/VALIDATION.md` — a `## camera — validation pass — **NOT YET RUN**` section per plan §9.
