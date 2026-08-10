# CAMERA_ASBUILT — the game-free half of tasks C1/C2 of `plans/CAMERA_CONTROLS_PLAN.md`, as built

> **Scope:** the `/sim/camera` filesystem surface, its line grammars, its validation rules, its
> compositing model and its store. 100 % game-free, entirely in `gatOS.SimFs/Camera/` +
> `gatOS.SimFs.Tests/Camera/` (plus the tree wiring in `SimFsTree.cs`). **No `gatOS.GameMod` change is
> part of this work item** — the director, frame resolution and actuators
> (`gatOS.GameMod/Game/Ksa/Camera/**`, plan §6.2) are a separate agent's, as is the `KsaCatalog`
> routing of the `camera.*` family.
>
> This note is the input for the docs lockstep pass (AGENTS.md §9): SPEC, `scope/`, `AGENTS.md`,
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
`GET/POST /v1/fs/camera/pose/fov` and `gatos/sim/camera/pose/fov` with no new code, and
`POST /v1/command` / `gatos/command` reach every `camera.*` action by construction — passing
`"vessel_id": ""`, which the JSON command parser **requires as a present string** even for a globally
addressed action (`SimHttpServer.ParseCommand`; the field may be empty but never omitted or null).
`ctl/batch` and `ctl/timed_batch` reach every camera leaf too (asserted end to end).

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
   **do** appear in `VfsScan.Leaves` and therefore at `GET/POST /v1/fs/camera/track/<name>` and
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

## 9. Threading (for the `AGENTS.md` threading-rules paragraph)

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

---
---

# Game-side (director) — tasks C1/C2 of `plans/CAMERA_CONTROLS_PLAN.md`, as built

> **Scope:** `gatOS.GameMod` only. The game-free half above was **used, not modified** — no file under
> `gatOS.SimFs/**` or `gatOS.SimFs.Tests/**` was touched by this work item, and neither were
> `SPEC_9P_FILESYSTEM.md`, `scope/**` or `docs/**`.
>
> Baseline: KSA `2026.8.5.5168`, every binding verified against
> `ksa-game-assemblies/current/decomp/` on **2026-08-06**.
>
> **Zero Harmony patches.** The whole feature hangs off the existing `[StarMapAfterOnFrame]` hook.

---

## G1. Files added / changed

### Added — `gatOS.GameMod/Game/Ksa/Camera/`

| File | Contents |
|---|---|
| `CameraTargets.cs` | `CameraTarget` (a resolution) + `CameraTargets`: `vessel:` / `body:` / `part:` → live object, position, velocity, body-fixed frame, up axis; the reverse `Describe(IFollowable?)` for read-back; `IsLive` |
| `CameraFrames.cs` | the six frames × anchor → ECL rotation; the placement precedence (orbit → geodetic → Cartesian); `GeoToEcl` / `TryEclToGeo` / `SphericalDirection` |
| `CameraDirector.cs` | ownership take / eased release / hard restore, the per-frame apply, the live-camera controls (`mode`/`follow`/`tidal`), despawn prune, event drain |
| `CameraReader.cs` | the live camera → `CameraStatus` sample, including filling in whichever position spelling the author did not write; the `CameraMode` ⇄ `CameraModeKind` mapping (both directions) |

### Changed

| File | Change |
|---|---|
| `Game/Ksa/Actuators/CameraActuator.cs` | **extended**: `Focus` keeps its behaviour but now sets follow on **both** viewport cameras with `alert: false` (**C1.4**); new `Execute(SimCommand, CameraDirector)` validating + routing the 26 new actions |
| `Game/Ksa/KsaCatalog.cs` | `camera.focus`'s special case folded into one `Camera(SimCommand)` sub-dispatcher routing the whole `camera.*` family before vehicle resolution and before the authority gate; new `CameraDirector? camera = null` ctor param |
| `Game/Ksa/Welds/WeldManager.cs` | `FindPart` `private` → `internal` (+ XML doc): the camera's `part:` anchors reuse the weld anchor resolver rather than duplicating it |
| `Game/Ksa/Render/VesselForceRender.cs` | `using KsaCamera = KSA.Camera;` + two use sites — **namespace-shadowing collateral, see G9.1** |
| `Game/Mod.Game.cs` | `_cameraDirector`/`_cameraDead`; construction in `EnsureControlObjects`; `partial void DriveCamera(double)`; `Shutdown()` in `TeardownGameCheats`; director passed to `KsaCatalog` + `TelemetrySampler` |
| `Game/TelemetrySampler.cs` | new `CameraDirector? camera` ctor param; `_camera?.Prune(vessels)` beside `VesselForceRender.Prune`; `camera.*` events folded into `NewEvents` |
| `Mod.cs` | `_cameraStore` built under `[camera] camera_enabled` and passed to `SimFsTree.Build(..., camera:)` **by named argument**; `partial void DriveCamera(double)`; the `OnAfterFrame` call site |

Build **0 warnings / 0 errors**; test suite **1257 passed, 0 failed** (`gatOS.GameMod` has no test
project — it is game-coupled, and every piece of logic that could be game-free already is, in
`gatOS.SimFs/Camera/`).

> **Note on the count:** the brief quoted 1199. The repo now reports 1257 because another work item is
> concurrently adding C3 files (`gatOS.SimFs/Camera/{CameraTrack,CameraSample,TrackParser,
> TrackEvaluator,Playback}.cs` + `gatOS.SimFs.Tests/Camera/TrackParserTests.cs`) and a
> `ScheduleStore.cs` change. **None of those are mine** (`git status` confirms my diff is
> `gatOS.GameMod` only) and no test regressed.

---

## G2. The `[KsaAnchor]` list

All: `Verified = "2026-08-06"`, `GameVersion = "2026.8.5.5168"`.

| # | Member | File | Risk | What it binds |
|---|---|---|---|---|
| 1 | `CameraTargets.TryResolve` | `Camera/CameraTargets.cs` | Low | `Universe.CurrentSystem.Get(id)` → `Astronomical`; `Vehicle`; `Celestial` |
| 2 | `CameraTargets.PositionEcl` | `Camera/CameraTargets.cs` | Low | `Astronomical.GetPositionEcl()`; `Vehicle.{CenterOfMassAsmb,GetBodyFixed2Ecl}`; `Part.PositionVehicleAsmb` |
| 3 | `CameraTargets.VelocityEcl` | `Camera/CameraTargets.cs` | Low | `Astronomical.GetVelocityEcl()` |
| 4 | `CameraTargets.BodyFixed2Ecl` | `Camera/CameraTargets.cs` | Low | `IOrientation.GetBodyFixed2Ecl()`; `Part.Asmb2VehicleAsmb`; `doubleQuat.{Concatenate,NormalizeOrZero}` |
| 5 | `CameraTargets.UpEcl` | `Camera/CameraTargets.cs` | **Medium** | `Celestial.GetRotationAxisCce()`; the `Vehicle.ComputeBody2Cce` axis convention (+X fwd, +Y right, **−Z up**) |
| 6 | `CameraTargets.Describe` | `Camera/CameraTargets.cs` | Low | `Camera.Following` → `IFollowable`; `Astronomical.Id` |
| 7 | `CameraTargets.IsLive` | `Camera/CameraTargets.cs` | Low | `Universe.CurrentSystem.All.UnsafeAsList()` |
| 8 | `CameraFrames.TryFrame2Ecl` | `Camera/CameraFrames.cs` | Medium | `Vehicle.{GetEnu2Cce,GetLvlh2Cce,Body2Cce,ComputeEnu2Cce,ComputeLvlh2Cce,GetPositionCce,GetVelocityCce}`; `Celestial.{GetCci2Cce,GetCcf2Cce,GetDirCcfFromLatLon,MeanRadius,GetPositionCce,GetVelocityCce}` |
| 9 | `CameraFrames.GeoToEcl` | `Camera/CameraFrames.cs` | Medium | `Celestial.{GetDirCcfFromLatLon,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius,GetPositionEclFromCce}` |
| 10 | `CameraFrames.TryEclToGeo` | `Camera/CameraFrames.cs` | Medium | `Celestial.{GetPositionCceFromEcl,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius}` |
| 11 | `CameraDirector.Take` | `Camera/CameraDirector.cs` | Medium | `Program.MainViewport`; `Viewport.{Mode,GetCamera,SetCameraMode,BaseCamera}`; `Camera.{PositionEcl,LocalPosition,LocalRotation,NoRotation,Following,TidalLocking,GetFieldOfView,Orthographic,Unfollow}` |
| 12 | `CameraDirector.Restore` | `Camera/CameraDirector.cs` | Medium | `Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,SetOrthographic}`; `Viewport.{Mode,SetCameraMode}` |
| 13 | `CameraDirector.Apply` | `Camera/CameraDirector.cs` | Medium | `Camera.{PositionEcl,LocalRotation,LookAtRotation,SetFieldOfView,SetOrthographic,SetOrthoHalfHeight}` |
| 14 | `CameraDirector.RestorePositionEcl` | `Camera/CameraDirector.cs` | Medium | the `Camera.PositionCce` composition (`LocalPosition` ⇄ `GetBodyFixed2Ecl` unless `NoRotation`); `IPosition.GetPositionEcl()` |
| 15 | `CameraDirector.SetMode` | `Camera/CameraDirector.cs` | Medium | `Program.MainViewport`; `Viewport.SetCameraMode(CameraMode)` |
| 16 | `CameraDirector.SetFollow` | `Camera/CameraDirector.cs` | Medium | `Program.MainViewport.{BaseCamera,MapCamera}`; `Camera.{SetFollow,Unfollow,TidalLocking}` |
| 17 | `CameraDirector.SetTidal` | `Camera/CameraDirector.cs` | Medium | `Camera.{Following,TidalLocking,SetFollow,PositionEcl}` |
| 18 | `CameraReader.Sample` | `Camera/CameraReader.cs` | Medium | `Viewport.Mode` (public field); `Camera.{Following,TidalLocking,GetFieldOfView,Orthographic}` |
| 19 | `CameraReader.ModeOf(CameraMode)` | `Camera/CameraReader.cs` | Low | the `CameraMode` enum |
| 20 | `CameraActuator.Focus` (**rebound**) | `Actuators/CameraActuator.cs` | Medium | `Program.MainViewport.{BaseCamera,MapCamera}`; `Camera.SetFollow(..., changeControl:false, alert:false)` |

**Unchanged and still correct:** `KsaCatalog.ResolveAstronomical` / `ResolveVehicle`,
`WeldManager.FindPart` (now also a camera binding — its existing anchor covers it),
`VesselForceRender`'s two prefixes (only the type *name* was aliased).

---

## G3. The ownership sequence (`camera/enabled 1` → `CameraDirector.Take`)

1. **Capture the restore state from the _active_ camera** — `Program.MainViewport.GetCamera()`, which
   returns `MapCamera` in Map mode and `BaseCamera` otherwise, so the capture must not assume the base
   camera: `Viewport.Mode`, `Following`, `TidalLocking`, `NoRotation`, `LocalPosition`,
   `LocalRotation`, `Orthographic`, `PositionEcl` (as an absolute fallback), and
   `GetFieldOfView() × 180/π` — **radians → degrees converted once, here, at the boundary**, because
   `GetFieldOfView()` returns radians while `SetFieldOfView(float)` takes degrees.
2. **Park the mode.** `Viewport.Mode = CameraMode.Fixed` by **direct field assignment**, so
   `FixedController.OnSwitchOn`'s `TimedAlert("Fixed Camera")` never draws. **Exception: leaving Map
   goes through `SetCameraMode`** — see G8.2.
3. **`BaseCamera.Unfollow(changeControl: false)`** (the default `true` would null
   `Program.ControlledVehicle` and drop the player's vessel). With `Following == null`,
   `FixedController.OnFrame`'s entire body is skipped, so the game's camera solver writes nothing and
   gatOS is the sole writer.
4. `BaseCamera.NoRotation = false` (insurance against a stuck map flag re-interpreting every later
   write), then seed `BaseCamera`'s `PositionEcl`/`LocalRotation` from the capture — so taking the
   camera **from map mode** is still visually continuous.
5. **Seed the compositor baseline** (`CameraState.SetBaseline`) from the captured values: position =
   the captured absolute ECL point, `frame = ecl`, `anchor = none`, `rotation` = the captured
   `LocalRotation`, `fov` = the captured degrees, `ortho` = the captured flag, everything else
   `CameraPose.Default`. **An owned-but-unwritten camera therefore sits exactly where the game left
   it.** (It no longer *follows*, which is inherent to unfollowing and documented.)
6. `PoseSmoother.Reset()`, `_appliedFovDeg = NaN` (forces one FOV write), phase → `Owned`.

`Take()` is idempotent, and **cancels an in-flight release blend** (a director who changes their mind
mid-handback keeps their shot, baseline and overrides intact).

### The per-frame re-assert (owned)

Every frame the driver checks `Viewport.Mode != Fixed` and `Camera.Following != null` and undoes both.
A camera hotkey or another mod could otherwise wake `FixedController.OnFrame` and leave two writers
fighting over one transform. **Consequence to document: while gatOS owns the camera, the player's
camera keys do nothing.** Release to get them back.

---

## G4. The release sequences — two verbs, one restore path

All three entry points funnel into `CameraDirector.Restore()`; only the *approach* differs.

| Entry point | Behaviour |
|---|---|
| `camera/enabled 0` → `Release()` | **eased blend-back** over `[camera] camera_release_blend_s` (default 0.6 s), then `Restore()`. A configured `0` collapses to an immediate cut. |
| `camera/release` (trigger) → `Restore()` | **hard cut**, no blend — this is the "give it back *now*" verb (plan §4.1 calls it "hard restore to game control"). |
| `Mod.TeardownGameCheats` → `Shutdown()` | `Restore()` + `CameraStore.Clear()` (drops every uploaded track). |

### The blend (`StepRelease`)

Eased `InOut` over the configured duration: `Vec3.Lerp` on position, `Splines.Slerp` on rotation,
linear on FOV, from the pose gatOS was holding at blend start toward the restore pose. **The restore
point is recomputed every frame of the blend**, not baked at its start, so blending back onto a moving
follow target lands on the target rather than on where it used to be — `RestorePositionEcl()`
reproduces `Camera.PositionCce`'s own composition (`LocalPosition` transformed through
`GetBodyFixed2Ecl()` unless `NoRotation`) because the camera being blended is unfollowed and its own
`PositionCce` would not use the captured target.

### The restore itself (`Restore`), in order and why

1. `SetFollow(saved, savedTidal, changeControl: false, alert: false)` **first** — it *teleports* the
   camera to `target + 2.5 × MeanRadius × forward`, so the transform must be written over it, not
   before it. If the saved target despawned (`CameraTargets.IsLive` false, or `Prune` nulled it), this
   degrades to `Unfollow(changeControl: false)` rather than throwing.
2. `NoRotation` (it changes how `LocalPosition` is interpreted), then `LocalPosition`, `LocalRotation`.
3. `SetFieldOfView(savedDegrees)`, `SetOrthographic(savedFlag)`.
4. `Viewport.Mode` **last** — direct assignment, except restoring **into** Map, which goes through
   `SetCameraMode` so `MapController.OnSwitchOn` re-establishes `NoRotation`, `GridFlag` and the map's
   own control state.
5. `finally`: `CameraState.ClearAll()`, publish `CameraStatus.Idle`, reset the smoother, drop the
   captured references. The phase flips to `Idle` **before** the try, so even a throwing restore leaves
   the director idle rather than half-owned.

Idempotent and safe to call at any time, including before the first `Take()`.

---

## G5. Frame resolution table — and how each frame degrades

Placement precedence in `TryResolvePosition` is **orbit → geodetic → Cartesian**: a non-zero
`pose/orbit/radius` is an explicit "place me on a sphere about the anchor" and wins; with radius `0`
the orbit channels name no placement, so the position channel (whichever spelling is live) applies.
Writing `0` to the radius hands placement back to `pose/position`.

| Token | Resolution (vessel / part anchor) | Resolution (body anchor) | Degrades to |
|---|---|---|---|
| `ecl` | identity, **origin (0,0,0)** — the offset *is* the point; no anchor needed | same | never fails |
| `cce` | identity rotation, origin = anchor `GetPositionEcl()` (CCE shares the ecliptic's axes) | same | `Unsupported` when no anchor resolves |
| `bodyfixed` | `Vehicle.GetBodyFixed2Ecl()` = `Body2Cce`; for a `part:` anchor, `Concatenate(Part.Asmb2VehicleAsmb, Body2Cce)` | `Celestial.GetBodyFixed2Ecl()` = `GetCcf2Cce()` | `Unsupported` (no anchor) |
| `enu` | `Vehicle.GetEnu2Cce()` — **nullable**, and it dereferences `Orbit.Parent` unguarded, so both are checked | built at the pose's **own geodetic lat/lon** via `Vehicle.ComputeEnu2Cce(surfaceCce, body.GetCci2Cce())` (CCI +Z *is* the spin axis) | `Unsupported`: "needs the anchor vessel to be orbiting a body" / "degenerate ... at its parent's centre" / "degenerate at this latitude (on the rotation axis)" |
| `lvlh` | `Vehicle.GetLvlh2Cce()` — **nullable** (null on near-zero or collinear position+velocity) | `Vehicle.ComputeLvlh2Cce(body.GetPositionCce(), body.GetVelocityCce())` | `Unsupported`: "degenerate for this anchor (no orbital motion to derive it from)" |
| `chase` | `Body2Cce` (part-aware; identical to `bodyfixed` — KSA's vehicle body frame *is* the chase convention) | — | `Unsupported`: "frame 'chase' needs a vessel or part anchor" |

**Nothing ever silently falls back to a different frame.** At *write* time an unresolvable frame is an
`Unsupported`/EOPNOTSUPP the guest's `write(2)` carries; **per frame**, the director **holds the last
good pose** and logs the reason once (`ModLog.Debug`, de-duplicated by message so a despawned anchor
cannot write 60 lines a second). A non-finite result is treated as unresolvable, so no NaN can reach
the view matrix.

**Aim** resolves the same way: `aimPointEcl = target position + offset.Transform(aimFrame2Ecl)`,
re-resolved every frame — which is what makes `+0.9 m` on a kittenaut's own Y axis *stay* its head as
it walks. A zero/near-zero forward vector (camera sitting on its subject) or a NaN `LookAtRotation`
holds the previous rotation.

**`pose/aim_up`:** `world` = ecliptic +Z; `target` = the aim target's own up (**celestial** → rotation
axis `GetRotationAxisCce()`; **vehicle/part** → body **−Z**, per `ComputeBody2Cce`'s +X fwd / +Y right
/ −Z up convention); `velocity` = the **anchor's** `GetVelocityEcl()`, falling back to the aim
target's when no anchor is set; `free` = the camera's *current* up carried forward (parallel
transport, so tracking never snaps the horizon back to level). Any degenerate/collinear up falls back
to world +Z, then to ecliptic +X for the look-straight-along-the-pole case — a wrong roll is
recoverable, a NaN view matrix is not.

**Roll** is applied **only on the aim path** (the surface documents it as "applied after aim"): an
explicit `pose/rotation` already names a complete orientation. Sign: `CreateFromAxisAngle(UnitZ,
−roll)` composed **before** the view→ECL map, i.e. positive roll rolls the camera clockwise and the
horizon tilts counter-clockwise. *(One for the live pass to confirm subjectively.)*

---

## G6. The `geo` axis convention — how it was resolved

`Camera.SetLatLon` (`KSA/Camera.cs:925`) does **not** contain the trigonometry; it delegates to
`Celestial.GetPositionEclFromLatLon`, which is
`GetDirCcfFromLatLon(lat, lon).Transform(GetCcf2Cce()) × MeanRadius + GetPositionEcl()`. And
`Celestial.GetDirCcfFromLatLon` (`KSA/Celestial.cs:674`) is:

```csharp
z = sin(lat);  x = cos(lat)*cos(lon);  y = cos(lat)*sin(lon);   // then .Normalized(), in CCF
```

i.e. **CCF +Z is the north pole and +X is the prime meridian on the equator**. gatOS **calls
`GetDirCcfFromLatLon` rather than restating that trigonometry**, so a future convention change lands
as a behaviour change we inherit instead of a silent divergence.

The final point is composed in one expression, so no frame mixing can occur (gatOS cannot call
`SetLatLon`/`SetAltitude` themselves — both bail out unless `_following is Celestial`, and the
director deliberately unfollows):

```csharp
dirCce = GetDirCcfFromLatLon(lat, lon).Transform(GetCcf2Cce()).NormalizeOrZero();
posEcl = body.GetPositionEclFromCce(dirCce * (MeanRadius + GetTerrainHeightFromDirCce(dirCce) + alt));
```

`GetSurfacePositionEclFromDirCce` is *literally* the same expression minus `alt`, and it expects a
**unit** direction and does **not** normalize (its `FromCce` sibling does) — hence the explicit
`NormalizeOrZero`. `GetTerrainHeightFromDirCce` returns **metres** and `0` for a body with no
heightmap, so **altitude is above terrain**, degrading to above-mean-sphere.

`TryEclToGeo` is the exact inverse (`lat = asin(ccf.Z)`, `lon = atan2(ccf.Y, ccf.X)`,
`alt = |posCce| − (MeanRadius + terrain)`) — the same pair KSA's own `GetLatitudeFromCce` /
`GetLongitudeFromCcf` use. It is what lets `CameraReader` **publish both position spellings**: a
Cartesian placement about a body reads back a real latitude, and a geodetic placement back-projects
into `pose/position`'s own frame. `NormalizeLongitude` keeps the read-back canonical (`[-180, 180)`).

**`Camera.ClampCamera()` still applies** and is deliberately not worked around: it runs at the top of
every `Camera.OnFrame` and pushes the camera to *surface + 0.5 m* whenever the frame viewport's
altitude is at or below that. It is the ocean-skimming floor — but an altitude request below **0.5 m**
is silently corrected, and the altitude it reads is the **frame** viewport's, not the camera being
updated.

---

## G7. The C3 seam (exactly where the track evaluator plugs in)

`CameraDirector.Update` computes, once per frame:

```csharp
const CameraChannelMask trackClaims = CameraChannelMask.None;   // <- C3 replaces both of these
var pose = store.State.Compose(trackSample: null, trackClaims);  //    with the evaluated sample + mask
```

marked in-source with a `// ---- THE C3 SEAM ----` banner. `trackClaims` is threaded through
`Apply` → `ApplyProjection` (not just used at the `Compose` call) precisely so a shot that animates
`ortho_height` drives it even though no leaf override exists — see G8.3.

Everything else C3 needs is already present and unused by this work item:
`CameraStore.OnTrackCommitted` (the parse hook), `CameraStore.{TryGet,CurrentVersion}`,
`CameraStore.EmitEvent` (drained by the sampler, G1), and the `CameraStatus` player fields
(`TrackName`/`TrackTMs`/`TrackDurationMs`/`ShotName`/`ShotIndex`/`Playback`/`Rate`/`Loop`), which
`CameraReader.Sample` currently fills with the idle values. **`camera.play` / `camera.set` /
`camera.stop` return `Unsupported` (EOPNOTSUPP)** with the message *"camera track playback is not
implemented yet (task C3); every camera channel is reachable from /sim/camera/pose and schedulable
through /sim/ctl/timed_batch"* — the leaves exist on every transport, they just answer honestly.

The **C4 time-channel seam** is likewise untouched: `CameraChannel.TimeScale` composes but has no
leaf, no actuator and no `Universe.SetSimulationSpeed` call anywhere in `Game/Ksa/Camera/`.
`[camera] camera_allow_time_channel` is still **not consumed by any code**.

---

## G8. Deviations from the brief, with justification

1. **`camera/release` is a hard cut; only `camera/enabled 0` blends.** The brief asked for one restore
   path *and* an optional eased blend; plan §4.1 calls `release` "hard restore to game control". Both
   are satisfied by making the *approach* the difference and the *restore* shared: `release` is the
   panic button (and the teardown path), `enabled 0` is the cinematic hand-back. Document both
   spellings in the SPEC.

2. **Leaving `CameraMode.Map` goes through `SetCameraMode` and accepts the "Fixed Camera" alert.** The
   brief offered "or explicitly clear `NoRotation`". That alternative is **wrong here**:
   `MapController.OnSwitchOn` also sets `Program.IsControlledVehicleActive = false` and stashes
   `PreviouslyControlledVehicle`, and only `OnSwitchOff` restores them — bypassing it would strand the
   player with an **uncontrollable vessel**. So taking the camera *from map view* prints one
   three-second `TimedAlert`; every other entry mode is a silent direct assignment. (`SetCameraMode`
   also calls `Program.ControlledVehicle?.ClearHeldPlayerInput()`, dropping any latched
   `ctl/translate` / `ctl/rotate` flags — SPEC §3.4.19.)

3. **`pose/ortho_height` is written only when its channel is explicitly claimed** (a live override or a
   C3 track claim). `Camera` exposes `SetOrthoHalfHeight` but has **no public getter** for
   `_orthoHalfHeight` in 5168, so there is nothing to capture at ownership take and **nothing to
   restore at release**. Writing the composed default every frame would silently clobber a value that
   could never be put back. Consequence for the SPEC: *changing `ortho_height` is the one camera
   change gatOS cannot undo.*

4. **`camera/mode`, `camera/follow` and `camera/tidal` are refused while gatOS owns the camera**
   (`Unsupported`, with a message naming the alternative). Ownership *is* a mode park with
   `Following == null`; honouring a follow would wake `FixedController.OnFrame` and give the transform
   a second writer, and honouring a mode change would be undone by the next frame's re-assert. The
   messages point at `pose/anchor` + `pose/aim_target` (which do everything a follow does, better) or
   at writing `0` to `camera/enabled`.

5. **`camera.follow` rejects a `part:` reference** (`Unsupported`): the game's camera can only follow a
   whole `IFollowable`. Aiming at a part is fully supported — that is `pose/aim_target`.

6. **`camera.follow` preserves the current tidal-locking flag** rather than defaulting it, so one leaf
   never quietly resets another; `camera/tidal` is the leaf that changes it. `SetTidal` re-issues
   `SetFollow` (the only writer of `TidalLocking`, which is get-only) **and re-asserts `PositionEcl`
   afterwards** — a flag change must not fling the view the way `SetFollow`'s unconditional teleport
   would. `camera.follow` itself **keeps** the teleport, matching `camera.focus` and the game's own
   follow action.

7. **`DriveCamera` is called at the *end* of `OnAfterFrame`, inside an `if (!ranInGui)`-restructured
   body rather than literally before an early return.** The brief's phrasing assumed the early-return
   shape; the requirement it encodes — *runs on every frame, not just UI-hidden ones* — is met exactly,
   and putting it after `DrivePerFrame`/`DrivePostSolver` means the director sees **this** frame's
   drained commands on every frame instead of only on GUI-visible ones. A comment at the call site says
   why.

8. **Existence is validated at write time** for `camera.anchor`, `camera.aim_target`, `camera.aim`,
   `camera.follow` and `camera.geo`'s `body:` tail — a reference naming nothing live is `NotFound`
   (ENOENT), per the brief's errno table. Note the consequence: you cannot pre-arm an anchor for a
   vessel that has not spawned yet.

9. **`camera.geo` without a `body:` tail requires the current `pose/anchor` to already be a body**,
   else `Unsupported` with a message naming both fixes. A latitude about a vessel means nothing.

---

## G9. Things the brief did not settle (decisions taken, flagged for review)

1. **Namespace shadowing — the one piece of collateral.** The mandated folder
   `Game/Ksa/Camera/` implies namespace `gatOS.GameMod.Game.Ksa.Camera`, and a child namespace of
   `Game.Ksa` **shadows the simple name `Camera` for every file under `Game/Ksa/`**, beating
   `using KSA;`. New files use `using KsaCamera = KSA.Camera;`. The only pre-existing casualties were
   `VesselForceRender.cs`'s `typeof(Camera)` and its `Camera camera` prefix parameter, fixed with the
   same alias plus a comment. **Anyone adding a file under `Game/Ksa/` that names the game's `Camera`
   type must now alias it.** (The alternative — namespace ≠ folder — was rejected as worse.)

2. **`camera/target`, `camera/mode`, `camera/tidal` and `camera/status` report idle values while gatOS
   does *not* own the camera.** The brief mandated the `AudioActuator._publishedEmpty` edge latch
   ("an idle director publishes once and then does nothing"), which is also what keeps the feature
   genuinely free when off — but it means those four leaves do not mirror the *player's* camera. If
   observing an unowned camera turns out to be wanted, the fix is a cheap always-on sample in the
   driver (three field reads) at the cost of the zero-work property. **Flagged for the SPEC wording.**

3. **`enu` as an aim frame about a *celestial* aim target uses the camera pose's own lat/lon**, since
   that is the only geodetic point in scope. For a *vessel* aim target `GetEnu2Cce()` is used and
   lat/lon is ignored, so this only bites the (unusual) "aim offset in ENU about a planet" case.

4. **The bubble-relative ego question (plan §5.2 / C5.3) is untouched and still open**: unfollowed,
   every object takes the plain `GetPositionEcl() − PositionEcl` path. Self-consistent, so it should be
   visually fine, but it is a VALIDATION item, not an assumption.

5. **`pose/roll`'s sign** is a free creative choice and was defined, not derived (G5). Confirm it
   subjectively in the live pass.

6. **The C3 agent is working in `gatOS.SimFs/Camera/` concurrently.** If `CameraState.Compose`'s
   signature or `CameraStatus`'s shape moves, the two call sites in `CameraDirector.Update` and the one
   in `CameraReader.Sample` are the whole surface to re-check.

---

## G10. Threading (for the `AGENTS.md` threading-rules paragraph)

**A seventh game-thread work site: `Mod.DriveCamera` → `CameraDirector.Update`, run at the end of
`[StarMapAfterOnFrame] Mod.OnAfterFrame`** — after the render, on **every** rendered frame (unlike
every other driver, which stands in only on the frames the F2-gated GUI hooks were skipped). It writes
`Camera.PositionEcl` / `LocalRotation` / the projection, so the *next* frame's
`Program.OnFrameViewports` rebuilds every matrix from it; gatOS never touches a matrix and needs **no
Harmony patch**. It self-gates to a single branch (`CameraDirector.IsIdle`) while gatOS does not own
the camera — the default — and in that state no camera is read, no pose composed and nothing
published. Camera teardown rides `Mod.TeardownGameCheats`.

`CameraState` and the director's own fields are game-thread-only, with no locks by design; transport
threads only enqueue `SimCommand`s (drained in the ordinary Frame phase — **no camera action is in
`SimCommand.SolverActions`**) and read the volatile `CameraStore.Status` the director publishes with
one swap. Despawn pruning rides the telemetry sampler's vehicle enumeration
(`CameraDirector.Prune`, beside `VesselForceRender.Prune`), and `camera.*` events drain into each
snapshot alongside audio's, IVA's and the scheduler's.

---

## G11. Still open after this work item

> **Superseded — read §W9 instead.** C3 landed (the `# Track evaluator (L3)` section below), and its
> wiring plus C4 and the shippable half of C5 landed in the `# Integration + C4 + C5` section at the end
> of this file. The list below is kept as the record of what was open *at the time*; every item except
> the HTTP track route, the docs lockstep and the live pass is now done or has a verdict.

- **C3** — the track parser/evaluator/player (G7 is where it plugs in).
- **C4** — the `time` channel (`Universe.SetSimulationSpeed`), gated on `debug_namespace`;
  `[camera] camera_allow_time_channel` is still unconsumed.
- **C5** — IVA and Map ownership contexts; the bubble-relative ego re-check.
- The HTTP `PUT /v1/camera/track/<name>` route (`CameraStore.HttpUpload` is built and tested; the route
  is not).
- **Docs lockstep (AGENTS.md §9)** from this note: `SPEC_9P_FILESYSTEM.md` (the `/sim/camera` family +
  §5.1 action rows + the §2.5 `[camera]` gate), `docs/KSA_INTEGRATION_MATRIX.md` (G2's table),
  `scope/FULL_SCOPE.md` + `scope/ksa-{read,write}-surface.md` + `scope/ksa-runtime-coupling.md` (the
  `OnAfterFrame` camera driver), `AGENTS.md` (status row + G10's threading paragraph),
  `docs/MILESTONES.md`, and `docs/VALIDATION.md` (`## camera — **NOT YET RUN**`).
- **In-game validation** — nothing here has been run against a live flight. Beyond plan §9's list, this
  work item specifically wants: ownership/release round-trip leaves the camera exactly as found
  (including *from* and *into* map mode); no `TimedAlert` text in footage except the documented
  Map-exit one; F2 does not stall the director; the `pose/geo` ocean skim down to the 0.5 m
  `ClampCamera` floor; aim-with-offset holding a kittenaut's head **plus the EVA-locomotion re-check**
  (the `KittenEva.PrepareWorker` caveat documented on `CameraDirector`); FOV beyond the game's 15–120;
  and `ortho_height`'s non-restorability (G8.3).

---

# Track evaluator (L3) — task C3, as built

> **The `TryEvaluate` seam shipped EXACTLY as briefed — no deviation.** The director can wire to:
> ```csharp
> bool TryEvaluate(double tSeconds, out CameraPose sample, out CameraChannelMask channels);
> ```
> declared on the new `gatOS.SimFs.Camera.ITrackSampler` and implemented by **both**
> `CameraPlayback` (one playing track) and `CameraPlaybackController` (the "is anything playing"
> wrapper the director should hold). One addition, purely additive: `TryEvaluateNow(out sample, out
> channels)` on both, which samples at the player's *own* `PlaybackClock` — **prefer it**, it is what
> keeps the director from inventing a second notion of "now".
>
> **Scope:** 100 % game-free, entirely in `gatOS.SimFs/Camera/` + `gatOS.SimFs.Tests/Camera/`, plus
> one minimal additive change to `gatOS.SimFs/Commands/ScheduleStore.cs` (§T6). No `gatOS.GameMod`,
> `SPEC_9P_FILESYSTEM.md`, `scope/` or `docs/` file was touched. **Not committed.**

## T1. Files added / changed

### Added — `gatOS.SimFs/Camera/`

| File | Contents |
|---|---|
| `CameraTrack.cs` | `CurveKind`, `PositionMode`, `TrackKey<TValue>`, `TrackChannel<TValue>`, `PositionSpec`, `AimSpec`, `TrackDefaults`, `Shot` (incl. the computed claim mask), `Track` |
| `TrackParser.cs` | `TrackParser` — JSON to `Track`, full validation, `const int MaxShots = 256` |
| `CameraSample.cs` | `CameraSample` (pose + mask + shot index/name), `CameraPlacement` (spherical to cartesian — the orbit resolution the director must share) |
| `TrackEvaluator.cs` | `TrackEvaluator.Sample(Track, tSeconds)` — shot selection, blend-in, every curve kind |
| `Playback.cs` | `ITrackSampler`, `CameraPlayback` (`IPlaybackPlayer`, `kind = camera-track`), `CameraPlaybackController` (the `camera.play`/`set`/`stop` executor + the commit-time validator + parsed-track cache) |

### Changed

| File | Change |
|---|---|
| `gatOS.SimFs/Commands/ScheduleStore.cs` | see §T6 — one new interface member, `_runners` retyped, three new public methods, `UtSeconds` promoted to public |

### Added — tests (`gatOS.SimFs.Tests/Camera/`)

`TrackParserTests.cs` (58), `TrackEvaluatorTests.cs` (24), `CameraPlaybackTests.cs` (27) — **109 tests**.

Full solution: **build 0 warnings / 0 errors**; **1 308 passed, 0 failed** (`gatOS.SimFs.Tests`
1 031 passed / 6 skipped, up from 922 — every prior test still green).

---

## T2. The JSON schema, as implemented

```jsonc
{
  "loop": false,                        // bool,   default false — the authored loop default
  "defaults": {                         // object, optional
    "frame":  "cce",                    // frame token — the default for a shot's position block
    "anchor": "vessel:apollo11",        // target ref — the default shot anchor
    "ease":   "in-out",                 // ease token OR [x1,y1,x2,y2]; also the blend_in shape
    "ease_power": 3                     // number [0.01,16]; requires "ease"; applies to both halves
  },
  "shots": [                            // REQUIRED, non-empty, <= 256, ordered by t, non-overlapping
    {
      "name":     "pad-rise",           // string,  default "shot-<index>"
      "t":        0.0,                  // seconds >= 0, default 0 — absolute on the track timeline
      "duration": 8.0,                  // seconds > 0, REQUIRED
      "anchor":   "body:earth",         // target ref, default defaults.anchor, else none
      "blend_in": 0.5,                  // seconds >= 0, default 0

      "position": { ... },              // see below
      "aim":      { ... },              // see below   (mutually exclusive with "rotation")
      "rotation": { "curve": ..., "keys": [ {"t":..., "v":[x,y,z,w]} ] },
      "roll":     { "keys": [ ... ] },  // degrees    (mutually exclusive with aim.roll)
      "fov":      { "keys": [ ... ] },  // degrees, each key in [fov_min, fov_max]
      "time":     { "keys": [ ... ] }   // sim-speed factor >= 0 (0 = paused) — see T3
    }
  ]
}
```

### `position`

```jsonc
// mode "cartesian" (the default when "keys" is present)
{ "mode": "cartesian", "curve": "catmull-rom", "frame": "bodyfixed",
  "keys": [ { "t": 0, "v": [x,y,z], "ease": "out", "ease_power": 3,
              "handle_out": [x,y,z], "handle_in": [x,y,z] } ] }

// mode "orbit" (the default when any of radius/azimuth/elevation is present)
{ "mode": "orbit", "frame": "bodyfixed",
  "radius":    { "keys": [ {"t":0,"v":120} ] },                 // metres >= 0
  "azimuth":   { "keys": [ {"t":0,"v":0}, {"t":8,"v":360} ] },  // degrees, any finite
  "elevation": { "keys": [ {"t":0,"v":15} ] } }                 // degrees, [-90, 90]

// mode "attach" (the default when "offset" is present)
{ "mode": "attach", "frame": "chase", "offset": [0, 3, -12] }
```

`frame` default: the block's own, then `defaults.frame`, then `ecl`. An explicit `mode` **must** agree
with what was authored (`"mode":"orbit"` + cartesian `keys` is EINVAL, per the brief).

### `aim`

```jsonc
{ "target": "vessel:apollo11",     // REQUIRED, must not be "none"
  "offset": [0, 1.2, 0],           // default [0,0,0]  — CONSTANT for the shot (see T4)
  "frame":  "bodyfixed",           // default bodyfixed (NOT defaults.frame — see T4)
  "up":     "world",               // default world
  "roll":   { "keys": [ ... ] } }  // optional animated roll, degrees
```

### Channel shape

Every channel is `{ "curve": ..., "keys": [ ... ] }`, or the **bare-array shorthand**
`"fov": [ ... ]`. The `position` block is the one exception: its `curve` sits beside `keys` on the
block, which is how plan §4.4 spells it.

- `curve` is one of `step | linear | catmull-rom | bezier`. Default **`linear`**, except a
  **rotation** channel whose default is **`catmull-rom`** (see T4).
- `keys`: non-empty, at most `camera_max_keys` (`CameraLimits.MaxKeys`), times **strictly increasing**
  and inside `[0, duration]`. One key is legal (a constant).
- Key: `t` (REQUIRED), `v` (REQUIRED — number, `[x,y,z]`, or `[x,y,z,w]`), `ease`, `ease_power`,
  `handle_in`, `handle_out`.

### The ease-resolution rule (decided here — plan §4.4 is ambiguous)

A segment's ease comes from its **start** key; if the start key names none, the **end** key's ease is
used; failing both, `defaults.ease`; failing that, linear. *Why:* plan §4.4's own example uses **both**
spellings — the position channel puts `"ease":"out","ease_power":3` on the *departing* key, while the
`fov` and `aim.roll` channels put theirs on the *arriving* key. Either single rule would silently make
half the plan's example inert. The rule is folded into every key **at parse time**, so `TrackKey.Ease`
is already resolved and `TrackEvaluator` never looks sideways. The last key's ease is stored but inert.

### Validation matrix (all EINVAL, all naming shot / channel / key)

| Rejected | Message shape |
|---|---|
| empty upload; over `MaxTrackBytes` | `camera track: the track is empty` / `... past the N-byte per-track cap` |
| unparseable JSON | `camera track: not valid JSON (...)` |
| top level not an object; `shots` missing/empty/not an array; over 256 shots | `camera track: shots is empty ...` |
| **any unknown key** at any level | `camera track: shots[0].durration is not a known key here (expected one of ...)` |
| shot with no `duration`, `duration <= 0`, `t < 0`, `blend_in < 0` | `camera track: shots[0].duration is 0; a shot must last a positive time` |
| shots out of time order, or overlapping | `camera track: shots[1] starts at t=4 but shots[0] runs to t=5; shots must not overlap` |
| a shot that declares **no** channels | `camera track: shots[0] declares no channels ...` |
| empty `keys`; non-monotonic/duplicate key times; a key outside `[0,duration]` | `camera track: shots[0].fov[2] is at t=2, not after [1] at t=4; key times must strictly increase` |
| any non-finite number, anywhere | `... must be a finite number` |
| out-of-range value (`fov` outside `[fov_min,fov_max]`, `elevation` outside `[-90,90]`, `radius<0`, `time<0`) | `camera track: shots[0].fov[0].v is 400; expected in [1, 179] degrees` |
| unknown enum token (`frame`, `up`, `mode`, `curve`, `ease`) | `... must be one of ecl\|cce\|bodyfixed\|enu\|lvlh\|chase` |
| `ease_power` without `ease`; `ease_power` with bezier handles; `ease_power` outside `[0.01,16]`; a 2- or 5-element ease array | `camera track: shots[0].fov[0].ease_power has no 'ease' to apply to` |
| `curve: bezier` with a missing handle on any segment | `camera track: shots[0].position.keys[0] has no 'handle_out'; a bezier curve needs both handles on every segment` |
| a handle on a **non**-bezier curve | `... carries a bezier handle but the curve is 'linear'` |
| `curve: bezier` on a **rotation** channel | `... cannot be 'bezier' here — a rotation channel interpolates with slerp/squad ...` |
| a quaternion key whose norm is outside `[0.5, 2]` (incl. all-zero) | `... is not a usable rotation ...` |
| a malformed target ref; `aim.target` = `none` | `camera track: shots[0].aim.target must be "vessel:<id>" ...` |
| `roll` declared **both** at shot level and inside `aim` | `... roll is declared both at the shot level and inside 'aim'; keep one of them` |

**Warned, not rejected:** a shot declaring **both** `aim` and `rotation`. Per plan §4.4 `aim` wins;
the rotation channel is dropped and a `ModLog.Warn` says so (a warning path, per the brief).

**Accepted deliberately:** `//` and `/* */` comments and trailing commas (`JsonCommentHandling.Skip`,
`AllowTrailingCommas`) — plan §4.4 documents the format *with* comments, and a shot list is exactly
the kind of file people annotate. `MaxDepth = 32`.

---

## T3. Channel to `pose/` leaf correspondence — **one documented mismatch, as pre-authorised**

| Track channel | `/sim/camera/pose/` leaf | `CameraChannelMask` bits claimed |
|---|---|---|
| `position` (cartesian / attach) | `position` (+ `frame`, `anchor`) | `Position \| Frame` (+ `Anchor`) |
| `position` (orbit) | `orbit/radius`, `orbit/azimuth`, `orbit/elevation` (+ `frame`, `anchor`) | `Frame` (+ `Anchor`) + one bit **per authored** sub-channel |
| `aim` | `aim` (which is `aim_target`, `aim_offset`, `aim_frame`, `aim_up`) | `AimTarget \| AimOffset \| AimFrame \| AimUp` |
| `aim.roll` / `roll` | `roll` | `Roll` |
| `rotation` | `rotation` | `Rotation` |
| `fov` | `fov` | `Fov` |
| **`time`** | **none** | `TimeScale` |

**`time` is the mismatch, and it is the one CAMERA_ASBUILT §8.10 already records:**
`CameraChannel.TimeScale` exists in the compositor with **no `/sim` leaf**, because the plan puts the
`time` channel in task **C4** and gates it on `[control] debug_namespace`. C3 therefore **parses and
evaluates** `time` (validated as a finite factor >= 0) and claims `TimeScale` in the mask — but
**applying** it is C4's, and the director must ignore the `TimeScale` bit until then (and gate it on
`camera_allow_time_channel` + `debug_namespace` after). No other channel was invented; every other one
maps to a leaf that already exists.

Leaves with **no** track channel (deliberate, not an oversight): `geo`, `ortho`, `ortho_height`,
`smoothing`, and `frame`/`anchor`/`aim_*` as standalone channels. They stay L1/L2-only; adding one
later is non-breaking *because unknown channel names are rejected today*, so nothing can silently
depend on their absence.

### The mask discipline (plan §4.3), as enforced

- The mask is computed in `Shot.Channels` from **what was authored**, never widened.
- `Frame` is claimed **only** when the shot drives a position (any mode); `Anchor` **only** when it
  drives a position *and* an anchor resolved. A `fov`-only shot in a track with `defaults.anchor` set
  claims **`Fov` alone** — asserted (`AFovOnlyShot_ClaimsNeitherAnchorNorFrame`).
- Orbit mode claims one bit per authored sub-channel, so `{"azimuth": ...}` alone leaves radius and
  elevation to the live overrides.
- The unclaimed fields of the emitted `CameraPose` carry `CameraPose.Default`'s values (60 degree FOV,
  ...) and are **meaningless** — `AnUndeclaredChannel_IsNotClobberedByThePosesDefault` is the
  regression guard.

---

## T4. Evaluation semantics

- **Absolute-from-start, never incremental.** Every channel is evaluated from its keys at the absolute
  `t`. A full turn is the key pair `0 -> 360`; there is no `azimuth += omega*dt` anywhere. At
  `t == duration` the eased progress snaps to exactly `1.0` (`Easing.Apply`), so the azimuth is
  **exactly** `360.0`, and `CameraPlacement.Spherical` folds that to **exactly** `0` before any
  trigonometry (`360 - 360*floor(360/360)`) — so the resolved placement is **bit-identical** to the
  one at `t == 0`. Asserted twice (`AFullOrbit_ClosesBitIdentically`,
  `AnEasedFullOrbit_AlsoClosesExactly`). The emitted **scalar** stays un-normalised (monotonic
  `0..360`) so a smoother does not see a 359-to-1 discontinuity; the fold happens only in the
  resolution.
- **`CameraPlacement.Spherical(radius, azDeg, elDeg)` is the ONE orbit resolution.** Azimuth in the
  frame's XY plane from +X toward +Y, elevation above it toward +Z; elevation clamped to `[-90,90]`
  (a Bezier ease may deliberately overshoot); non-finite gives `Vec3.Zero`. **The director must resolve
  `pose/orbit/*` through it** rather than re-deriving the trigonometry, or a track's circle and a
  hand-written `echo 90 > pose/orbit/azimuth` land in two different places.
- **Curves.** `step` holds; `linear` lerps (slerp for rotation); `catmull-rom` is
  `Splines.CatmullRom` with the terminal keys repeated as their own missing neighbours (centripetal,
  alpha = 0.5); `bezier` is `Splines.Bezier` with the per-key handles. Scalars are lifted to `(v,0,0)`
  and read back from `.X`, so they reuse the landed, tested primitives (a 1-D centripetal Catmull-Rom
  *is* the 3-D one on that lift).
- **Rotation** defaults to `catmull-rom` (squad) — `a = SquadIntermediate(k-1,k0,k1)`,
  `b = SquadIntermediate(k0,k1,k2)`, terminal keys repeated — when the channel has **at least 3 keys**,
  and slerp otherwise; explicit `"curve":"linear"` forces slerp. `bezier` is rejected. Asserted C1
  across a key boundary, with the slerp discontinuity as the control.
- **Bezier-ease overshoot survives.** `Easing.Apply` may legitimately return outside `[0,1]`;
  `linear` segments use a private `LerpUnclamped` (endpoints still snapped exactly) so anticipation and
  overshoot reach the pose. Spline segments keep the landed **clamped** behaviour — extrapolating a
  spline past its hull really would fling the camera. Documented asymmetry.
- **Shot selection** is a binary search for the last shot started by `t`. **Outside a shot the
  evaluator holds, it does not release:** before the first shot, in a gap, and past the last shot it
  returns the nearest shot's terminal sample. A gap is a hold; ending playback is the *player's*
  decision.
- **`blend_in`** cross-fades from the **previous shot evaluated at its own end** over `blend_in`
  seconds, eased by `defaults.ease` (falling back to `in-out` — a linear blend edge reads as two cuts).
  It fades **only the channels both shots declare** (`Position`, `Rotation` (slerp), `AimOffset`,
  `Roll`, `Fov`, `Orbit*`, `TimeScale`); a channel only the new shot drives is taken at full value, a
  channel only the old shot drove is released, and discrete channels (frame, anchor, aim target, aim
  frame, up) cut. The **first** shot has nothing to blend from and starts at full value — softening
  that edge is the director's `PoseSmoother`, which can see the live composed pose the evaluator
  deliberately cannot.
- **`aim.offset`/`target`/`frame`/`up` are constant for a shot** on purpose: the point of an aim
  channel is that the host re-resolves it against the *live* target every frame. `aim.frame` defaults
  to **`bodyfixed`**, *not* `defaults.frame` — an aim offset is measured on the subject, which is what
  makes "+0.9 m on the kittenaut's own Y axis" stay its head as it walks.
- **Determinism** is asserted directly: the same `t` sampled twice, and sampled out of order, is
  bit-identical (`TheSameT_AlwaysYieldsABitIdenticalSample`). A NaN `t` degrades to the track start.
- **Allocation:** `TheSamplePath_AllocatesNothing` — under 64 B over 10 000 samples of a Catmull-Rom +
  scalar track.

---

## T5. The player, the controller, and the registry

### `CameraPlayback : IPlaybackPlayer, ITrackSampler`

| Member | Value |
|---|---|
| `const string CameraTrackKind` | **`"camera-track"`** — the `schedules/<id>/kind` leaf value |
| `Kind` | `camera-track` |
| `Clock` | a `PlaybackClock` on **`ClockBase.Render`** (plan §4.4: the playback clock is `dtPlayer`) |
| `DurationMs` | the track's own length (end of its last shot; a leading gap counts) |
| `PendingCount` / `Dropped` / `LastError` | **0 / 0 / `-`** — a track fires no entries |
| `State` | **computed, never latched**: stopped is `done`; clock not started is `pending`; `!loop && position >= duration` is `done`; paused is `paused`; else `running`. **Never `failed`** |
| `OwnsClock` | `group == ""` |
| `TrackName`, `Track`, `ShotIndex` | for the director's `CameraStatus` publish |

**Completing is a hold; stopping is a release.** A non-looping track that runs past its end reports
`done` and **keeps returning its final sample**, so the shot does not snap away the instant it lands.
`camera.stop` is what makes `TryEvaluate` return false and hands the channels back to the overrides and
the baseline. (The blend-back seam is the director's — `camera_release_blend_s` + `PoseSmoother`.)

**Eviction safety:** because the player never reports `failed`, `ScheduleStore.IsFinished` reduces for
this kind to `State == Done`, which is reached only by an explicit stop or by running past the end. A
take in progress is therefore **structurally un-evictable** under cap pressure — asserted
(`APlayingTrack_IsNeverEvictedUnderCapPressure`, `MaxLive: 1`, five `Activate` passes).

### `CameraPlaybackController`

Holds the one live player and executes the three verbs — the game-free executor `KsaCatalog` should
route `camera.play` / `camera.set` / `camera.stop` to, exactly as it routes `schedule.*` to
`ScheduleStore.Execute`.

| Action | Behaviour | Errnos |
|---|---|---|
| `camera.play` | resolve track, parse (cached by version), **retire the current take (`reason=replaced`)**, reserve id, resolve clock, apply `at`/`rate`/`loop`, `Register`, `Start` | ENOENT (no such track), EBUSY (still uploading), EINVAL (bad name, parse failure — **with the parse message** — or the `MaxLive` cap) |
| `camera.set` | `t` scrubs (`sec*1000`), `rate` sets `Clock.Rate`, plus `loop` and `paused` — the *same* clock the `schedules/<id>/` leaves drive | ENOENT (nothing playing), EINVAL (bad pair/range) |
| `camera.stop` | retire (`reason=stopped`), unregister | — (**idempotent**: Ok with nothing playing) |

- **Registry id:** the fixed, predictable **`"camera"`** when free, else an auto `#N`
  (`ReserveId(null)`). So the surface is normally `/sim/ctl/schedules/camera/`.
- **`loop`** comes from the `play` line when present, else from the track's own `"loop"`.
- **`rate`** defaults to 1.
- **Grouped players ignore `at`/`rate`/`loop`** (a Debug note says so) — the ScheduleStore rule that
  the group's timeline belongs to whoever created it. `camera.set`, being a *live* control, always
  drives the clock, group or not — exactly like `schedules/<id>/scrub`.
- **Replacing removes the old player from the registry** (unlike a finished schedule, which persists):
  there is only ever one camera player and leaving a corpse named `camera` would block id reuse.
- `Clear()` — teardown: retire, drop the parsed-track cache, reset `LastTrackError`.

### The commit-time validator (the `OnTrackCommitted` seam, promoted)

`CameraPlaybackController`'s constructor installs itself as `CameraStore.OnTrackCommitted`
(suppressible with `hookCommits: false`). On commit it parses, caches the result by
`(name, version)`, and — when the bytes are **non-empty** and invalid — **throws
`VfsErrorException(EINVAL)`**, as CAMERA_ASBUILT §8.9 pre-authorised.

- **An empty commit does not throw.** A zero-byte commit is the ordinary shape of `truncate -s 0` and
  of a not-yet-started upload, not an authoring error; it is recorded only.
- The 9p clunk swallows the throw (`Session.FidEntry.Dispose` logs it at Debug) and **cannot carry an
  errno** — by design. The diagnosis therefore reaches the author **three** ways:
  1. `ModLog.Warn("camera: rejected track '<name>': <message>")`;
  2. `CameraPlaybackController.LastTrackError` (`"<name>: <message>"`, `-` when clean);
  3. the **EINVAL with the same parse message** that `camera/play <name>` returns.
- **The committed bytes are deliberately left in place**, so `cat /sim/camera/track/<name>` still
  shows what was written. The HTTP `complete=1` path *does* surface the EINVAL directly.
- **Deviation from the brief:** the brief asked for the parse error in "the store's status/info text".
  No such field exists on `CameraStore`, and adding a leaf would require a `SPEC_9P_FILESYSTEM.md`
  change this work item is forbidden from making. The three surfaces above carry it instead; a
  `camera/last_error` leaf is a clean C6 follow-up.

### Events (through `CameraStore.EmitEvent`, the bounded `audio.finished` queue)

| Type | `Detail` |
|---|---|
| `camera.shot` | `<id> track=<track> shot=<index> name=<shot-name>` |
| `camera.finished` | `<id> track=<track> kind=camera-track reason=complete\|stopped\|replaced` |

`VesselId` is always `null`; the UT stamp is `ScheduleStore.UtSeconds` (one notion of "when" for the
whole registry). `camera.shot` fires on each shot boundary and **re-arms on a loop wrap** (diffed off
`PlaybackClock.LoopCount`), so a looping take announces shot 0 each cycle. `camera.finished
reason=complete` fires **once**. Emission happens in `CameraPlayback.Poll`, called from
`TryEvaluate` — the director samples every frame while it drives the camera, which is exactly when a
track is playing. `Poll(in CameraSample)` is public so a driver may pump the edges without sampling.

> **For the GameMod agent:** the telemetry sampler must drain `CameraStore.DrainEvents()` exactly like
> `AudioStore`/`ScheduleStore`, gated by `_settings.Events`.

---

## T6. The `ScheduleStore` change (minimal + additive)

| Change | Why |
|---|---|
| **`IPlaybackPlayer` gains `bool OwnsClock { get; }`** | the registry advances each distinct clock once per tick and needs to know which players own theirs. It was already `ScheduleRunner.OwnsClock` (`internal`), just not on the interface. No external implementor exists (verified across the whole solution + tests), so this breaks nothing. |
| **`_runners` retyped `List<ScheduleRunner>` to `List<IPlaybackPlayer>`** | one registry for both kinds. `Tick` now pattern-tests `is ScheduleRunner` (only entry-firing kinds have work there). |
| `FindRunner` becomes **`FindPlayer`**; `Remove`/`ReleaseSlot`/`GroupInUse` take `IPlaybackPlayer` | so `schedule.pause/scrub/rate/loop/stop/remove` reach a camera track too — asserted (`TheScheduleTransport_DrivesACameraTrackPlayerToo`). |
| **new `public void Register(IPlaybackPlayer)`** | joins a foreign player. Caller owns `ReserveId` and `Clock.Start()`, exactly as the schedule path does. |
| **new `public bool Unregister(string id)`** | stop + drop + release id/group — the `schedule.remove` path by another name. |
| **new `public PlaybackClock ResolveGroupClock(group, base, rate, loop, who = null)`** | extracted from the private `ResolveClock(Schedule)`, which now delegates to it. **Behaviour is unchanged** for schedules; the camera player needs the *same* shared instance, not one that merely agrees. |
| **`internal double UtSeconds` promoted to `public`** | foreign players stamp their events with the registry's UT. |

`Activate`, `AdvanceAll`, `Publish`, `Clear` and the cap-pressure eviction all work over the unified
list unchanged. **No schedule path, grammar, leaf or errno moved**; all ~120 existing scheduler tests
(`ScheduleEvictionTests` included) pass untouched.

---

## T7. Deviations from the brief / plan (with justification)

1. **`Channel<T>`/`Key<T>` are named `TrackChannel<TValue>`/`TrackKey<TValue>`.** *Why:*
   `CameraChannel` already exists in this namespace and means something different and load-bearing (a
   compositor channel, i.e. a mask bit). A second unrelated `Channel<T>` beside it would make the mask
   discipline — the single most important invariant here — read ambiguously at every call site.
   `TrackChannel` also avoids shadowing `System.Threading.Channels.Channel<T>`.
2. **`TrackChannel<TValue>` is a sealed class, not a record.** A record's positional array property is
   handed out mutable; a committed track must be immutable so a shot that started against version 3
   keeps playing version 3. The indexer also keeps the evaluator's inner loop an array access.
3. **`TrackKey.Ease` is already resolved** (non-nullable) rather than "the ease as authored". The
   start-key-else-end-key-else-default rule (T2) is folded in at parse time so the evaluator never has
   to look at a key's neighbours to know how to leave it.
4. **`const int TrackParser.MaxShots = 256`, rather than a `CameraLimits` field.** The brief lists
   "shot count" among the caps, but `CameraLimits` has none and adding one would mean a new
   `[camera] camera_max_shots` key in `GatOsConfig.cs` + the default TOML — a config-surface change
   outside this work item. 256 is far past anything hand-authored and exists only to bound the object
   graph inside the byte cap.
5. **A seventh public type, `CameraPlacement`.** The orbit channels need spherical-to-cartesian
   resolution, that maths is game-free, and the director needs *the same* definition for the
   `pose/orbit/*` leaves. Putting it anywhere else would guarantee two implementations that disagree
   at the third decimal — and it is where the 360-degree-closure fold lives.
6. **`CameraPlaybackController` is a new type the brief did not name.** `Playback.cs` was briefed as
   "the track player"; but `camera.play`/`set`/`stop` need a game-free executor with a stable home (the
   `ScheduleStore.Execute` precedent), the `OnTrackCommitted` seam needs an owner, and the director
   needs one object to ask "is anything playing". One type covers all three; `CameraPlayback` stays
   purely "one playing track".
7. **`TryEvaluateNow` added beside the briefed `TryEvaluate(tSeconds, ...)`.** Purely additive. The
   briefed signature ships verbatim (see the banner); `TryEvaluateNow` is the form that keeps the
   director from deriving a second "now" from something other than the player's own clock.
8. **`aim.frame` defaults to `bodyfixed`, not `defaults.frame`.** Matching the landed
   `CameraCommands.ParseAim` default, and for the same reason: an aim offset is measured on the
   *subject*.
9. **A rotation channel's default curve is `catmull-rom` (squad), while every other channel defaults
   to `linear`.** The brief requires "squad when >= 3 keys"; slerp's C0 flick at each waypoint is the
   documented defect a rotation track exists to avoid. `"curve":"linear"` forces slerp.
10. **`linear` segments extrapolate (endpoints still snapped); spline segments clamp.** The brief notes
    a Bezier ease may legitimately leave `[0,1]` and that consumers must clamp themselves. Clamping
    *everything* would delete anticipation/overshoot — the only shapes a power curve cannot express —
    so linear segments honour it and splines (whose extrapolation would fling the camera outside the
    key hull) do not.
11. **A gap, a pre-roll and a run-off-the-end all HOLD.** The brief left this open. Releasing in a gap
    would snap the camera back to the overrides and then snap again when the next shot began.
12. **The parse error is not in `CameraStore`'s status/info text** — see T5's deviation note.

---

## T8. Still open (not this work item)

> **Superseded — read §W9 instead.** Every GameMod wiring item below is done (§W2), the `TimeScale` bit
> now has its applier (§W3), and the director resolves `pose/orbit/*` through `CameraPlacement.Spherical`.
> `camera/last_error` shipped (§W5). The list is kept as the record of what was open at the time.

- **Wiring in `gatOS.GameMod`:** construct `CameraPlaybackController(camera, schedules)`; route
  `camera.play`/`camera.set`/`camera.stop` in `KsaCatalog` to `controller.Execute`; call
  `controller.TryEvaluateNow(out pose, out channels)` in the director and feed
  `CameraState.Compose(pose, channels)`; publish `CameraStatus` from `controller.Current`
  (`TrackName`, `Clock.PositionMs`, `DurationMs`, `ShotIndex` + `Track.Shots[i].Name`, `State`,
  `Clock.Rate`, `Clock.Loop`); drain `CameraStore.DrainEvents()` in the telemetry sampler; call
  `controller.Clear()` from `Mod.TeardownGameCheats`.
- **The `TimeScale` bit must be ignored by the director until C4**, then gated on
  `camera_allow_time_channel` + `[control] debug_namespace`.
- **The director must resolve `pose/orbit/*` through `CameraPlacement.Spherical`.**
- Docs lockstep (AGENTS.md §9) from this note — notably the SPEC's `/sim/camera` track-schema section
  (T2 is the source text), the `schedules/<id>/kind` value `camera-track`, and the two event rows.
- `docs/VALIDATION.md` — the camera checklist per plan §9.
- Optional C6 follow-ups: a `camera/last_error` leaf for T5's upload-rejection text; a
  `camera_max_shots` config key for T7.4.

---
---

# Integration + C4 + C5 — the track seam wired, the time channel, and the map/IVA verdict, as built

> **Scope:** the C3 evaluator is now actually driving the camera; task **C4** (the interpolated `time`
> channel) is complete; task **C5** is **partially** complete — `C5.2` ships `/sim/camera/map/scope`,
> and **`C5.1` (IVA) and the "park in Map" half of `C5.2` are NOT implementable without a Harmony
> patch** (§W6 — evidence, not opinion). Two new `/sim` leaves. **Zero Harmony patches**, still.
>
> Baseline: KSA `2026.8.5.5168`, every new binding verified against
> `ksa-game-assemblies/current/decomp/` on **2026-08-06**.
>
> Build **0 warnings / 0 errors**; **1 313 passed, 0 failed** (from 1 308 — +5 new, none regressed).
> `SPEC_9P_FILESYSTEM.md`, `scope/**` and `docs/**` were **not** touched: this section is their input.
> **Not committed.**

---

## W1. Files changed

| File | Change |
|---|---|
| `gatOS.SimFs/Camera/CameraStore.cs` | `CameraStatus` gains **`double MapScope = 0`** (trailing, defaulted — every existing construction site compiles unchanged); `CameraStore` gains the volatile **`LastError`** property; the `OnTrackCommitted` doc no longer says "C3 has not landed" |
| `gatOS.SimFs/Camera/CameraCommands.cs` | new action key **`MapScopeAction = "camera.map_scope"`** |
| `gatOS.SimFs/Camera/CameraFormat.cs` | `Status()` gains a **`map_scope <m>`** line (after `tidal`) |
| `gatOS.SimFs/Camera/Playback.cs` | `CameraPlaybackController.LastTrackError` now **delegates to `CameraStore.LastError`** (one definition); every `camera.play` failure funnels through a new private `Fail(...)` that records it; a successful `play` clears it |
| `gatOS.SimFs/SimFsTree.cs` | `camera/last_error` (`LiveLine`) and the `camera/map/` dir with `scope` (`RangedControl`) |
| `gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs` | the **C3 seam wired**; the **C4 time channel** (`ApplyTimeScale`); **`SetMapScope`**; a `Playback` property; playback stop + sim-speed restore in `Restore`; `playback.Clear()` in `Shutdown`; the §W6 verdict written into the type remarks |
| `gatOS.GameMod/Game/Ksa/Camera/CameraReader.cs` | `Sample` takes the live `CameraPlayback?` and fills the eight player fields + `MapScope` from it |
| `gatOS.GameMod/Game/Ksa/Camera/CameraFrames.cs` | `TryResolvePosition` resolves `pose/orbit/*` through **`CameraPlacement.Spherical`**; the local `SphericalDirection` (and the now-unused `DegToRad`) deleted |
| `gatOS.GameMod/Game/Ksa/Actuators/CameraActuator.cs` | `camera.play`/`set`/`stop` route to `director.Playback.Execute`; new `camera.map_scope` case |
| `gatOS.GameMod/Game/Mod.Game.cs` | `CameraPlaybackController` constructed in `EnsureControlObjects` and passed to the director, with the two time-channel config gates |

Tests: `CameraTreeTests` (+2 tests, +3 `TestCase`s, three expectations updated),
`SimFsTreeTests`' control-enabled crawl guard extended with both new paths.

---

## W2. The C3 seam, as wired

`CameraDirector.Update` (the `// ---- THE C3 SEAM ----` banner is gone; the code is the comment now):

```csharp
CameraPose? trackSample = null;
var trackClaims = CameraChannelMask.None;
if (playback is { } player && player.TryEvaluateNow(out var sampled, out var claims))
{
    trackSample = sampled;
    trackClaims = claims;
}

var pose = store.State.Compose(trackSample, trackClaims);
```

- **`TryEvaluateNow`, never `TryEvaluate(t, …)`.** It samples at the player's own `PlaybackClock` — the
  same instance `/sim/ctl/schedules/<id>/{t,rate,pause,scrub,loop}` drives and the same one a shared-clock
  group shares. Deriving a second "now" here (from `dt`, or from `Program.FrameNumber`) is exactly the
  drift plan §3.4 exists to prevent.
- **`trackClaims` is passed unmasked**, including `TimeScale` — C4 lands in the same work item, so the
  bit now has an applier (§W3). It continues to be threaded through `Apply` to `ApplyProjection` for the
  `ortho_height` case, and now also to `ApplyTimeScale`.
- **The player fields are published, not latched.** `CameraReader.Sample` takes `playback?.Current` and
  reads `TrackName`, `Clock.PositionMs`, `DurationMs`, `ShotIndex` (plus the bounds-checked
  `Track.Shots[i].Name`), `State`, `Clock.Rate`, `Clock.Loop` straight off it, so `camera/playback` and
  `ctl/schedules/camera/t` can never disagree about where the take is.
- **`camera.play`/`set`/`stop` route to `CameraPlaybackController.Execute`** from `CameraActuator`,
  exactly as `KsaCatalog` routes `schedule.*` to `ScheduleStore.Execute`. The former EOPNOTSUPP stub is
  gone; a *new* EOPNOTSUPP takes its place for the one configuration that genuinely cannot play a track
  — see §W8.4.
- **`pose/orbit/*` now resolves through `CameraPlacement.Spherical`** (game-free, shared with the
  track's `"mode":"orbit"`), so a track's circle and `echo 90 > pose/orbit/azimuth` land in the same
  place, and the leaf path inherits the 360-degree closure fold. `CameraFrames.SphericalDirection` is
  deleted: a second implementation is the failure mode, so it does not get to survive as dead code.
- **Events** already reached `/sim/events`: `TelemetrySampler` was drilling `CameraDirector.DrainEvents`
  into `CameraStore.DrainEvents` from the previous work item; `CameraPlayback.Poll` (called from
  `TryEvaluate`) is now what fills that queue.
- **Construction** (`Mod.Game.cs`): `new CameraPlaybackController(cameraStore, scheduleStore)` with
  `hookCommits` left at its default `true` — nothing else wants `CameraStore.OnTrackCommitted`, so the
  controller is the commit-time validator, and a malformed upload fails the write.

### Two lifecycle decisions the brief left open

1. **Releasing the camera stops the take.** `CameraDirector.Restore()` ends with
   `playback?.Execute(camera.stop)`. *Why:* once the director is idle nothing samples the player, so a
   surviving take would drive nothing, emit no further `camera.shot` edges, and still occupy
   `/sim/ctl/schedules/camera` as a live entry. It is literally the `camera/stop` verb, so it emits the
   documented `camera.finished reason=stopped`. It is the **last** statement in the restore's `try`, so
   giving the player their camera back can never be skipped by a failure in it.
2. **`Shutdown()` additionally calls `playback.Clear()`** — drops the parsed-track cache and resets
   `LastError` alongside `CameraStore.Clear()`'s track eviction. `Restore()` deliberately does *not*
   clear `LastError`: a release must not erase the diagnosis a guest is about to read.

---

## W3. C4 — the interpolated `time` channel

`CameraDirector.ApplyTimeScale(pose, trackClaims)`, called from `Apply` right after `ApplyProjection`.
**No new `/sim` leaf**: plan §7 C4.1 is explicit that `debug/time/warp` (action `debug.warp`,
`SimFsTree.cs:1272`, the same `Universe.SetSimulationSpeed` primitive) already covers the discrete case
and is already schedulable through `ctl/timed_batch`. C4 adds only the interpolated channel, which is
authored as a track's `"time": { "keys": [...] }` and claims `CameraChannel.TimeScale`.

| Aspect | Behaviour |
|---|---|
| Binding | `Universe.SetSimulationSpeed(value, alert: false)` (`KSA/Universe.cs:1998`). `alert: false` is load-bearing — the default draws a speed `TimedAlert` on screen, i.e. in the footage. |
| Gates | **`[control] debug_namespace` AND `[camera] camera_allow_time_channel`**, both passed to the director's constructor. |
| Gate closed | **Ignored with a one-shot `ModLog.Warn`, not an error.** Plan §4.4 says "ignored with a warning otherwise", and failing a whole shot over a config flag would be worse than running it at 1x. The message names *which* gate is off. Re-armed on each `Take()`, so one warning per ownership session rather than one per process (a 60 Hz driver must not fill the log). |
| Capture | `Universe.GetSimulationSpeed()` (`:2021`), read **lazily — the first frame the channel is actually driven**, never at ownership take. |
| Restore | In `Restore()`, **only when that capture happened**. A director that never touches time leaves the player's warp setting exactly as found. `_timeCaptured`/`_appliedTimeScale` reset in the `finally`, so even a throwing restore leaves no latch. |
| Idempotence | The write is skipped while the composed value equals the last applied one, so a settled curve does not re-write every frame. |
| Values | `0` pauses, `0.15` is slow-mo, `> 1` warps. Non-finite or negative is ignored (the parser already rejects those; this is the belt). |
| Read-back | Already existed: `camera/status`' `time_scale` line now reports the composed effective value, because the `TimeScale` bit reaches `Compose`. |

### The `Universe.IsAutoWarpActive` interaction (documented, deliberately **not** guarded)

Neither public `SetSimulationSpeed` overload checks `Universe.IsAutoWarpActive` (`:96`) — only the
private `simspeed` terminal command (`SetSimulationSpeedDirect`, `:622`) does, and it refuses outright.
Meanwhile the auto-warp update itself calls `SetSimulationSpeed(num10, alert: false)` **every step**
(`:1929`). So a track driving the time channel during an auto-warp *fights* it, and whoever wrote last
in the frame wins — in practice gatOS, because the auto-warp update runs inside the sim step and
`ApplyTimeScale` runs after the render. No guard was added because the game itself does not pick a
winner and both alternatives are worse: refusing would make a shot silently ignore its own authored
curve, and stopping the auto-warp would cancel a manoeuvre the player scheduled. **Stop the auto-warp
before rolling the shot.** Stated in the `ApplyTimeScale` XML docs and worth a SPEC sentence.

---

## W4. C5.2 — `/sim/camera/map/scope` (the part of C5 that ships)

| | |
|---|---|
| Path | `/sim/camera/map/scope` — its own `map/` directory, so a second map knob needs no new top-level name |
| Archetype | **St** — `RangedControl` `[0, inf)`; read-only `StaticTextFile` twin with no command sink |
| Action | `camera.map_scope`, **global** addressing (`VesselId = ""`, `Ordinal = NoOrdinal`), **Frame** phase |
| Binding | `Program.MainViewport.MapController.Scope` (`KSA/MapController.cs:33`, a plain `public double`) |
| Errnos | EINVAL (non-finite, or `< 0`) |
| Read-back | `CameraStatus.MapScope`, plus a new `map_scope <m>` line in `camera/status` |
| Ownership | **Not gated.** Like `mode`/`follow`/`tidal` it configures the *game's* camera, not the composed pose |

Three inherited behaviours the SPEC should state, all of them the game's own:

1. `MapController.OnFrame` clamps `Scope` **up** to `Camera.Following.MeanRadius` on every map frame, so
   a smaller written value reads back clamped.
2. `OnSwitchOn` calls `SetDefaults()` — which recomputes `Scope` wholesale from the focus's mean radius
   and sphere of influence — whenever the follow target changed since the map was last left. A scope
   written before a focus change does not survive it.
3. It has **no visible effect outside `map` mode**; until then it is a stored field.

**The read-back publish.** `SetMapScope` writes the field and then publishes
`store.Status with { MapScope = controller.Scope }` (one volatile swap). This is necessary because the
director only samples the live viewport while it *owns* the camera — and map is precisely the mode in
which it does not — so without it the leaf would report the idle `0` for a value the guest had just
written. It does **not** change the §G9.2 property that `mode`/`follow`/`tidal`/`target` report idle
values for an unowned camera; it just means a write to this one leaf is observable.

---

## W5. `/sim/camera/last_error` (the second new leaf)

| | |
|---|---|
| Path | `/sim/camera/last_error` |
| Archetype | **S** — `LiveLine`, formatted per access |
| Value | `"<track>: <message>"`, or `-` when clean |
| Source | **`CameraStore.LastError`** (a volatile string on the store, not on `CameraStatus`) |

*Why it exists:* a 9p **clunk** — which is what commits an upload — cannot carry an errno, so a guest
that `cp`s a malformed track had no way to *read* why it was rejected. Task C3 left the diagnosis in
three places (`ModLog.Warn`, `CameraPlaybackController.LastTrackError`, and the later EINVAL from
`camera/play`), **none of which is visible from inside the guest at the moment it went wrong**. This is
that diagnosis, on the filesystem, on every transport.

*Why on the store and not on `CameraStatus`:* the status is published only by the director, and only
while gatOS owns the camera — which is not when tracks get uploaded. `CameraPlaybackController.LastTrackError`
now simply **delegates** to `CameraStore.LastError`, so there is one definition and the property and the
leaf cannot disagree.

**What sets it:**

- a commit that failed to parse (`OnTrackCommitted`) — including the empty-commit case, which records
  but does not throw;
- **every** `camera.play` rejection — bad name, ENOENT, EBUSY, a parse failure, or the `MaxLive` cap —
  funnelled through a new private `Fail(name, outcome, message)`. This matters most on the
  `ctl/timed_batch` path, where the errno has no caller left to read it;
- **cleared by a `camera.play` that actually started** (a take that started is the proof the track is
  good) and by `CameraPlaybackController.Clear()` (teardown). Nothing else clears it: an error that
  stays until something works is more useful than one that quietly ages out.

Empty/whitespace assignments render as `-`, so the leaf is never blank.

---

## W6. C5.1 (IVA) and "park in Map" — **NOT implemented; here is the evidence**

The plan's §7 C5.1/C5.2 assume "gatOS writes last (end of `OnAfterFrame`, after `OnFrameViewports`), so
the seat-pin and the cone-clamp are bypassed". **That assumption is false**, and the decomp says so
plainly. The real per-frame order is:

```
frame N   : OnFrameViewports -> controller.OnFrame -> Camera.OnFrame (builds _vp) -> Render
            ... -> [StarMapAfterOnFrame] -> gatOS writes PositionEcl / LocalRotation
frame N+1 : OnFrameViewports -> controller.OnFrame   <- overwrites, BEFORE any matrix is rebuilt
                             -> Camera.OnFrame       <- builds _vp from the CONTROLLER's values
```

(`Viewport.OnFrame`, `KSA/Viewport.cs:139-144`, is literally
`GetActiveController().OnFrame(this, dt); GetCamera().OnFrame(dt);`.)

gatOS writing "last" in frame *N* therefore only reaches the screen because the active controller writes
**nothing** at the top of frame *N+1*. `FixedController.OnFrame` (`KSA/FixedController.cs:18-35`) wraps
its **entire body** in `if (following != null)`, and ownership unfollows — that is the whole trick, and
it is specific to `FixedController`.

**`IVAController.OnFrame` (`KSA/IVAController.cs:27-118`) — what it actually does:**

- lines 29-38: `if (!(Camera.Following is Vehicle vehicle))` then `Program.HoveredViewport.NextCameraMode(); return;`
  and the same for `vehicle != LastFollowing`. gatOS's ownership requires `Following == null`, so this
  fires **immediately** and cycles the mode (IVA to Orbit) — on the *hovered* viewport, which need not
  even be the one gatOS bound.
- line 41: `Camera.PositionEcl = vehicle.GetPositionEcl() + ...` — **unconditional**; the seat pin is not
  something a later writer can win, it is the first thing written each frame.
- line 112: `Camera.LocalRotation = localRotation` — written on every frame except the switch frame,
  after the cone clamp (lines 82-108).

So C5.1 as designed does not "bypass the seat pin"; it produces a camera that either bounces straight
out of IVA mode or is driven entirely by the IVA controller with gatOS's writes never rendered — the
"silently does nothing" outcome the brief said to report rather than ship.

**`MapController.OnFrame` (`KSA/MapController.cs:124-289`) fails the same test:** lines 126-130,
`if (Camera.Following == null) { Program.SetCameraMode(CameraMode.Free); return; }`; and lines 281-282
assign **both** `Camera.PositionEcl` and `Camera.LocalRotation` from its own scope/orbit-view solution,
unconditionally, at the end of every frame.

**Conclusion.** Making either an ownership context requires a Harmony patch to suppress a controller's
`OnFrame` — the exact thing this feature's design exists to avoid (plan §0.2, §5.2, and the `unscience`
`UnpatchAll` cautionary tale of §1.3). Neither is offered, and the reasoning is written into
`CameraDirector`'s type remarks so the next reader does not re-derive it. The rest of C5.2 —
`MapController.Scope` as a first-class leaf — ships (§W4), and it is the part with standalone value.

**Hazard 10 (`Camera.NoRotation` changing the meaning of `PositionCce`/`LocalPosition`) is
consequently moot for the owned camera** and needs no frame-resolution change: `Take` sets
`NoRotation = false` on the base camera and unfollows, and with `_following == null` *and* `Parent == null`
both `Camera.PositionEcl` (`KSA/Camera.cs:106-127`) and `WorldRotation` (`:130-146`) bypass the flag
entirely. It still matters on the **restore** path, where it is already handled (§G4 step 2, and
`RestorePositionEcl`'s reproduction of the `PositionCce` composition).

**C5.3 remains a VALIDATION item**, untouched and correctly so — the bubble-relative ego question is
explicitly conditional on what a live pass shows.

---

## W7. New / updated `[KsaAnchor]`s

All `Verified = "2026-08-06"`, `GameVersion = "2026.8.5.5168"`.

| Member | File | Risk | Binds |
|---|---|---|---|
| `CameraDirector.ApplyTimeScale` (**new**) | `Camera/CameraDirector.cs` | Medium | `Universe.SetSimulationSpeed(double, alert:false)` (`:1998`), `Universe.GetSimulationSpeed()` (`:2021`), `Universe.IsAutoWarpActive` (`:96`) |
| `CameraDirector.SetMapScope` (**new**) | `Camera/CameraDirector.cs` | Medium | `Program.MainViewport.MapController`; `MapController.Scope` (`:33`) |
| `CameraDirector.Restore` (**extended**) | `Camera/CameraDirector.cs` | Medium | plus `Universe.SetSimulationSpeed` (conditional restore) |
| `CameraReader.Sample` (**extended**) | `Camera/CameraReader.cs` | Medium | plus `Viewport.MapController`, `MapController.Scope` |

`CameraFrames.TryResolvePosition` lost no binding — the deleted `SphericalDirection` was pure
trigonometry with no KSA type in it.

---

## W8. Deviations / decisions flagged for review

1. **`camera/status` gained a `map_scope` line.** The status block is documented as "the whole camera
   state, one `key value...` per line", and `mode`/`follow`/`tidal` — the other game-camera controls —
   are already there. `last_error` deliberately is **not** added: it is a diagnostic, not state, and
   status is meant to stay trivially machine-parseable.
2. **`CameraStatus.MapScope` is a trailing defaulted positional parameter.** A record's positional
   parameters accept defaults, so every existing construction site (one in tests, one in
   `CameraReader`) compiled unchanged and `CameraStatus.Idle` did not have to be respelled.
3. **`camera.map_scope` is range-validated in the director, not in a new `CameraRules` member.** The
   rule is "finite and >= 0" — identical to `IsValidOrbitRadius`, and a second name for it would only
   invite the two to drift. The 9p path is bounded by the leaf's own `RangedControl`; the HTTP/MQTT
   `POST /v1/command` path hits the director's check, which is where the game's own clamping and the
   read-back publish also live.
4. **`camera.play`/`set`/`stop` answer EOPNOTSUPP when `[schedule] schedule_enabled = false`,** with a
   message naming the flag. A camera track *is* a `/sim/ctl/schedules` entry (`kind = camera-track`), so
   there is genuinely nowhere to register a player without the registry. Every L1/L2 channel still works
   in that configuration, and the message says so. This is a real, reachable configuration and wants a
   SPEC sentence.
5. **The blend-back samples the track.** During a `Phase.Releasing` blend the evaluator is still called
   (so `camera.shot` edges keep firing and the published status stays truthful about the player's
   timeline) even though `StepRelease` ignores the pose. The take is stopped at the end of the blend, by
   `Restore`.
6. **`_timeWarned` is per ownership session,** not per process — see §W3.

---

## W9. Still open after this work item

- **C5.1 (IVA) and Map-as-an-ownership-context** — blocked on the §W6 finding; they need a Harmony patch
  or a redesign, and neither was improvised.
- **C5.3** — the bubble-relative ego re-check (a VALIDATION item by construction).
- The HTTP `PUT /v1/camera/track/<name>` route (`CameraStore.HttpUpload` is built and tested; the route
  is not).
- **Docs lockstep (AGENTS.md §9)** from this note: `SPEC_9P_FILESYSTEM.md` (the two new leaves, the
  `camera.map_scope` action row, the `map_scope` status line, the §W3 time-channel semantics and its two
  config gates, and the §W8.4 EOPNOTSUPP), `docs/KSA_INTEGRATION_MATRIX.md` (§W7),
  `scope/FULL_SCOPE.md` + `scope/ksa-{read,write}-surface.md`, `AGENTS.md`, `docs/MILESTONES.md`.
- **In-game validation** — nothing here has run against a live flight. Beyond plan §9's list this work
  item specifically wants: a track actually driving the camera end to end (upload, `play`, shot edges,
  `finished`); `camera/last_error` after a deliberately malformed `cp`; the time channel easing into
  slow-mo and **restoring the player's warp on release** (and *not* touching it when no shot used it);
  the one-shot warning with either gate off; `camera/map/scope` in map mode, including the two clamping
  behaviours of §W4; and confirmation that `pose/orbit/*` and a track's `"mode":"orbit"` now put the
  camera in the same place.
