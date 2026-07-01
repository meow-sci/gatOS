# Plan: `always_render_iva` and `welds` debug features

Status: **proposed** (not yet implemented). Author pass: 2026-06-28.

Bring two game-hack features from the sibling **unscience** super-mod into gatOS, exposed **only**
through the gatOS surfaces (`/sim` 9p + HTTP `/v1` + MQTT) — **no ImGui UI**. Both live under
`/sim/debug` because both are pure cheats.

1. **`always_render_iva`** — force interior (IVA) part meshes to render outside IVA camera mode.
   A single global `echo 0|1 > /sim/debug/always_render_iva` toggle.
2. **`welds`** — rigidly attach ("weld") one vessel to another, anchored to a chosen **part** on
   the target. Players discover parts under `/sim/vessels/by-id/<id>/parts/` and create welds via
   `/sim/debug`. (This is the feature unscience calls "garrys-torch"; in gatOS it is **only** ever
   called *welds* — no other name appears anywhere in code, paths, or docs.)

> **Naming rule (binding for this work):** the string "garrys", "garrys-torch", or "torch" must not
> appear in any gatOS source, path, config key, action key, SPEC entry, or doc. The feature is
> **welds** everywhere.

---

## 0. Source-of-truth research (verified, with file:line)

### 0.1 unscience — "always render IVA" (`IvaForceRender`)

`unscience/ksa-abstractions.lib/IvaForceRender.cs`. Pure logic, zero UI. Mechanism:

- KSA's render gate (confirmed in current decomp, `ksa-game-assemblies` `PartModel.cs:387`):
  ```csharp
  if (Template.RayTracing != PartModelModule.RaytracingMode.ShadowProxy
      && (!Template.Internal || viewport.Mode == CameraMode.IVA))
  ```
  i.e. interior meshes (`Template.Internal == true`) are skipped unless the camera is in IVA mode.
- To force them visible, **flip `PartModelModule.Template.Internal = false`** on every internal part
  template. `Internal` is a `bool` field on the *template* (`PartModelModule.cs:35`), so the flip is
  **template-global** (affects all instances of that part type — exactly right for a global cheat).
- unscience uses two Harmony patches, both gated by a static `_enabled`:
  1. **`PartModel(PartModelModule.Template)` ctor postfix** — flips `Internal=false` on newly built
     internal templates and tracks them (so part types that first appear *after* enabling are caught).
  2. **`PartModel.AddInstance(PerInstanceData, Viewport, int)` postfix** — *editor-only* (returns
     unless `Program.Editor != null`); forces interior meshes into VAB preview viewports.
- The toggle setter does a **bulk pass over `PartModel.Instances`** (static list, `PartModel.cs:325`)
  on enable (flip + track) and **restores** tracked templates to `Internal=true` on disable.

Current-decomp anchors: `PartModel.Instances` `PartModel.cs:325`; ctor `PartModel.cs:351`;
`AddInstance(PerInstanceData, Viewport, int frameIndex)` `PartModel.cs:375`; gate `PartModel.cs:387`;
`Template.Internal` `PartModelModule.cs:35`; `RaytracingMode.ShadowProxy` `PartModelModule.cs:10`;
`CameraMode.IVA` `CameraMode.cs`; `Program.MainViewport` `Program.cs:403`; `Program.Editor`.

### 0.2 unscience — "welds" (`WeldEntry` + `WeldEngine`)

`unscience/garrys-torch.lib/{WeldEntry,WeldEngine}.cs` are the reusable core (UI lives in
`garrys-torch.lib/GarrysTorchSubmod.cs` and `garrys-torch/Mod.cs` — **excluded**).

- **Data model** (`WeldEntry.cs`): `Vehicle Source`, `Vehicle Target`, `Part? TargetPart` (anchor;
  `null` ⇒ target CoM/body frame), `float3 Position` (anchor-frame metres), `float3 Rotation`
  (Euler pitch/yaw/roll degrees), `bool LockRotation`, `bool WeldEnabled`. **The anchor is a
  top-level `Part`** (`vehicle.Parts.Parts`), **not a SubPart** → we only need one part level.
  (unscience also has `Scale` + an animation system; **both are intentionally NOT ported** — welds in
  gatOS are pose-locking only.)
- **Per-tick update** (`WeldEngine.UpdateWeld`): for each weld, teleport the source so it tracks the
  anchor. The exact math (ported verbatim — §6). Crucially it is driven from a **lifecycle hook**
  (`[StarMapAfterGui]` in `garrys-torch/Mod.cs`), **not** a Harmony patch, and it:
  - calls `KSA.JobSystems.VehicleSolvers.Wait()` before mutating (drains in-flight solver workers);
  - stamps the new orbit with `Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime`
    (**not** `GetElapsedSimTime()`) so the teleported body time aligns with the next solver tick.
- The hook is always present but **self-gates** (`if (_welds.Count == 0) return;`), so it costs
  nothing when no welds exist. A weld whose `Source.Parent != Target.Parent` is auto-removed.

> **Key correction to the brief:** the welds per-tick update does **not** require a Harmony patch.
> unscience drives it from an existing game-thread lifecycle hook, self-gated to zero cost when idle.
> We adopt the same approach — strictly *less* invasive than a patch (§5.2). The only Harmony in this
> whole plan is the IVA pair (§5.1), and we make those **dynamic** (installed only while toggled on).

### 0.3 Current KSA APIs (verified in `ksa-game-assemblies`)

| Need | API | file:line |
|---|---|---|
| Parts list (top-level only) | `Vehicle.Parts` → `PartTree.Parts` (`ReadOnlySpan<Part>`), `.Count` | `Vehicle.cs:264`, `PartTree.cs:67`, `:65` |
| Part identity | `Part.InstanceId` (`uint`, runtime-unique, stable) | `Part.cs:321` |
| Part display | `Part.Id` (string, **can collide**), `Part.DisplayName`, `Part.Template.Id` | `Part.cs:411`, `:413`, `:323` |
| Part tree role | `Part.PartParent` (null⇒root), `Part.IsSubPart`, `Part.SubParts` | `Part.cs:381`, `:657`, `:655` |
| Part pose | `Part.PositionVehicleAsmb` (`double3`, cached), `Part.Asmb2VehicleAsmb` (`doubleQuat`, cached) | `Part.cs:415`, `:431` |
| Vehicle state | `GetPositionCci()`, `GetVelocityCci()`, `GetBody2Cci()`, `BodyRates`, `CenterOfMassAsmb` | `Vehicle.cs:1949`,`:1897`,`:2242`,`:458`,`:510` |
| Teleport | `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)`, `UpdatePerFrameData()`, `Orbit`, `Parent` | `Vehicle.cs:1594`,`:1972`,`:330`,`:332` |
| Orbit build | `Orbit.CreateFromStateCci(IParentBody, SimTime, double3 pos, double3 vel, byte4 color)` | `Orbit.cs:1396` |
| Frame transforms | `IParentBody.GetCci2Cce()` / `GetCce2Cci()` | `IParentBody.cs:47`,`:49` |
| Solver timing | `Universe.GetJobSimStep(double)` → `SimStep{PreviousTime,NextTime,DeltaTime}`; `Program.GetPlayerDeltaTime()` | `Universe.cs:2188`, `Program.cs:4467` |
| Solver drain | `JobSystems.VehicleSolvers.Wait()` | `JobScheduler.cs:51` |
| Frame order | `PrepareFrame`: wait→`ApplyVehicleSolvers()` (advances `_lastSimStep`)→`GetJobSimStep`→`ExecuteNextVehicleSolvers(dt,step)` | `Program.cs:1934-1984`, `Universe.cs:1599`,`:1660` |

> **Two API deltas vs. the unscience-era decomp** (important): `Orbit.CreateFromStateCci`'s time arg
> is **`SimTime`** (not `double`), and `Vehicle.Teleport` parameters are **nullable**. gatOS's existing
> `DebugActuator.Teleport` already uses both correctly (`Game/Ksa/Actuators/DebugActuator.cs`).

**Part-tree change detection:** there is **no public version counter / dirty flag / change event** on
`PartTree`/`Part`. The only cheap signals are `PartTree.Count` (catches add/remove) and the set of
`Part.InstanceId`s (catches swaps). We use **count-change as the primary invalidation** + a 10 s
backstop (§4.3).

### 0.4 gatOS integration points (verified, with file:line)

- **Write pipeline:** `SimCommand` (`gatOS.SimFs/Commands/SimCommand.cs`) → `CommandQueue`
  (`SubmitAsync` on transport threads, `Drain(phase,…)` on the game thread) → `ICommandExecutor`
  = `KsaCatalog` (`gatOS.GameMod/Game/Ksa/KsaCatalog.cs`, `Execute`+`Dispatch`) → actuators
  (`Game/Ksa/Actuators/*`). Errno via `CommandResult`/`CommandOutcome`.
- **Control-file archetypes:** `CommandFile` base (`Commands/CommandFile.cs`, line-buffered, one
  `Parse(token)` hook) with `ControlFile.{Flag,Fraction,Number}`, `VectorControlFile`,
  `EnumControlFile`, `TokenControlFile`, `TriggerFile`. Tree helpers `FlagControl`/`NumberControl`/
  `VectorControl`/… at `SimFsTree.cs:431-468`. A **global** (vessel-agnostic) control passes
  `vesselId = ""` (e.g. `debug/time/warp`, `SimFsTree.cs:519`).
- **`/sim/debug`:** built by `DebugDir()` (`SimFsTree.cs:506`), per-vessel `DebugVesselDir()`
  (`:532`); gated by `_commands is { DebugEnabled: true }` (`:106`) = `[control] debug_namespace`.
- **Read tree:** `SimFsTree.VesselDir()` (`:270-348`); per-module collections use the
  `DelegateDirectory(list, lookup)` pattern (see `EnginesDir`/`EngineDir` `:404-426`).
- **Snapshot:** `gatOS.SimFs/Snapshots/SimSnapshot.cs` (`VesselSnapshot` + per-module record lists,
  all `init`-only with `[]` defaults → adding a list never breaks construction sites). Built/published
  by `TelemetrySampler.Sample()` (`gatOS.GameMod/Game/TelemetrySampler.cs:90-140`).
- **Readers:** `Game/Ksa/Readers/VesselReader.cs` (`SampleEngines`/`SampleLights` are the templates
  for a parts reader). KSA types live **only** under `Game/Ksa/` (the G2 rule).
- **Transport parity (free):** HTTP `/v1/fs/<path>` and MQTT `gatos/sim/<path>` mirror **every**
  non-streaming leaf by walking the one tree (`gatOS.NineP/Vfs/VfsScan.cs`). A new `/sim` node lights
  up on all transports with no transport code. Full-snapshot JSON via `SimJson`; the frozen compact
  `Formats.VesselTelemetry` stays unchanged (parts/welds are tree + full-JSON only).
- **Harmony lifecycle:** `Mod` (`gatOS.GameMod/Mod.cs`) hooks `[StarMapBeforeGui] OnBeforeUi`
  (`:232`, samples + drains Frame commands), `[StarMapAfterGui] OnAfterUi` (`:256`, draws UI),
  `[StarMapUnload] Unload` (`:279`). The solver-phase command drain is a `Priority.First` prefix on
  `Universe.ExecuteNextVehicleSolvers` installed by `InstallSolverHook` (`Mod.Game.cs`). Phase of an
  action is derived from `SimCommand.SolverActions` — **all of our new actions are Frame phase**
  (they mutate mod registries / render templates, not solver-visible vehicle state; the weld *teleport*
  is done by the driver, not by a command).

---

## 1. Scope

**In:**
- `/sim/debug/always_render_iva` global flag (IVA force-render).
- `/sim/vessels/by-id/<id>/parts/<n>/…` read-only top-level part listing (cached).
- `/sim/debug/vessels/<id>/{weld,weld_here,unweld}` write controls + `/sim/debug/welds/…` registry view.
- Weld driver (per-frame teleport) on the game thread, self-gated.
- All three transports (free via parity), SPEC + scope + matrix + skill updates, game-free tests,
  in-game validation checklist.

**Out (explicitly):**
- No ImGui / menu / hotkey surface of any kind.
- No SubPart level (anchor is a top-level `Part`).
- No weld **scale** or **animation** (unscience extras) — pose-lock only.
- No persistence of welds across save/load (welds are live runtime cheats; cleared on unload).
  *(Optional future: persist to the gatOS config dir — noted in §9, not in this scope.)*

---

## 2. The `/sim` surface (authoritative — mirror into SPEC in lockstep)

### 2.1 IVA toggle (global)

```
/sim/debug/always_render_iva     STATE flag   read 0|1, write 0|1   action debug.always_render_iva
```
- Read: `0`/`1` = current force-render state (snapshot-backed).
- Write `1`: install IVA patches (if absent) + flip all internal templates visible.
  Write `0`: restore templates + remove IVA patches. Idempotent.
- Errno: `EACCES` if control disabled; `EINVAL` for non-`0|1`; `EIO` on KSA fault.
- Vessel-agnostic (like `debug.warp`): handled in `KsaCatalog.Execute` before vehicle resolution.

### 2.2 Parts listing (read-only, per vessel)

```
/sim/vessels/by-id/<id>/parts/                 directory, one numbered entry per top-level part
/sim/vessels/by-id/<id>/parts/<n>/instance_id  uint  (STABLE key; pass this to weld)
/sim/vessels/by-id/<id>/parts/<n>/id           string (Part.Id; may collide across instances)
/sim/vessels/by-id/<id>/parts/<n>/display_name string
/sim/vessels/by-id/<id>/parts/<n>/template     string (Part.Template.Id)
/sim/vessels/by-id/<id>/parts/<n>/is_root      0|1   (PartParent == null)
/sim/vessels/by-id/<id>/parts/<n>/subpart_count int  (informational; subparts not exposed)
/sim/vessels/by-id/<id>/parts/<n>/position     "x y z" (PositionVehicleAsmb, m, vehicle asmb frame)
```
- `<n>` is a 0-based **index** (like `engines/<n>`), friendly to enumerate. **It is not stable**
  across part edits; the stable handle is the `instance_id` leaf — *that* is what welds reference.
- Directory present only when the vessel has ≥1 part **and** the parts stream is enabled (§4.4 gate).
- The list is cached per vehicle and rebuilt on part-count change or every 10 s (§4.3).

### 2.3 Welds (write controls + registry view)

Per source vessel, under the existing `/sim/debug/vessels/<id>/`:
```
weld       STATE line   write "<target_id> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <lock>"
                        read  current spec for this source, or "" if not welded
                        action debug.weld_create
weld_here  STATE line   write "<target_id> <part_iid> [<lock>]"   (captures CURRENT relative pose)
                        action debug.weld_here
unweld     TRIGGER      write 1 to remove this source's weld      action debug.weld_remove
```
- `<part_iid>` = a part `instance_id` from §2.2, or `0` to anchor to the target's body/CoM frame.
- `<lock>` = `1` (default; lock orientation to anchor) or `0` (lock position only, free rotation).
- A vessel may be the **source of at most one weld** (re-`weld` replaces it). Many sources may weld
  to the same target/part.

Global registry view + ops, under `/sim/debug/welds/`:
```
/sim/debug/welds/                 directory, one entry per active weld, keyed by source id
/sim/debug/welds/<source_id>/target         string
/sim/debug/welds/<source_id>/part           uint (anchor instance_id; 0 = body frame)
/sim/debug/welds/<source_id>/offset         "x y z"
/sim/debug/welds/<source_id>/rotation       "pitch yaw roll" (deg)
/sim/debug/welds/<source_id>/lock_rotation  0|1
/sim/debug/welds/<source_id>/enabled        STATE flag  read/write 0|1 (suspend/resume, keep entry)
                                            action debug.weld_enable
/sim/debug/welds/clear            TRIGGER  write 1 to remove ALL welds   action debug.weld_clear
/sim/debug/welds/count            read     number of active welds
```

**Action key catalog (all Frame phase):**

| action | VesselId | Token | Values | meaning |
|---|---|---|---|---|
| `debug.always_render_iva` | — | — | `Value`=0/1 | global IVA force-render |
| `debug.weld_create` | source | target | `[part_iid,x,y,z,pitch,yaw,roll,lock]` | create/replace weld (explicit pose) |
| `debug.weld_here` | source | target | `[part_iid,lock]` | create/replace weld (capture current pose) |
| `debug.weld_remove` | source | — | — | remove this source's weld |
| `debug.weld_enable` | source | — | `Value`=0/1 | suspend/resume |
| `debug.weld_clear` | — | — | — | remove all welds |

Errnos: `EACCES` control off, `ENOENT` source/target/part gone, `EINVAL` bad arity/values,
`EBUSY` source==target or parent-body mismatch, `EIO` KSA fault. (Maps via `CommandResult`.)

> All of the above must be added to **`SPEC_9P_FILESYSTEM.md`** in the same change (the SPEC
> constitution). HTTP/MQTT mirrors come for free; document the `POST /v1/command` action keys + the
> `/v1/fs` & `gatos/sim/` leaf mirrors as usual.

---

## 3. Code layout (respecting the dependency + G2 rules)

```
gatOS.SimFs (game-free)
  Snapshots/SimSnapshot.cs        + PartSnapshot record; VesselSnapshot.Parts; WeldSnapshot record;
                                    SimSnapshot.Welds; SimSnapshot.AlwaysRenderIva
  Commands/LineControlFile.cs      NEW: generic CommandFile that parses a whole line via a
                                    Func<string,SimCommand?> (backs weld/weld_here)
  SimFsTree.cs                     + PartsDir/PartDir; always_render_iva flag; weld/weld_here/unweld;
                                    welds/ registry view + clear + count
  Formats.cs                       (reuse Scalar/Flag/Vector; add a WeldSpec line formatter if handy)
  SimJson.cs                       + parts + welds + always_render_iva in the full-snapshot JSON
  TelemetrySettings.cs             + VesselParts gate (live-toggle, mirrors VesselDetail)

gatOS.GameMod (game-coupled; KSA types only under Game/Ksa/)
  Game/Ksa/Render/IvaForceRender.cs   NEW: port of unscience IvaForceRender (dynamic patch mgmt)
  Game/Ksa/Welds/WeldEntry.cs         NEW: port of WeldEntry (no Scale/anim)
  Game/Ksa/Welds/WeldEngine.cs        NEW: port of WeldEngine.UpdateWeld + EulerDegreesToQuat
                                       (+ CapturePose for weld_here, + Quat→Euler helper)
  Game/Ksa/Welds/WeldManager.cs       NEW: registry + Update(dt) driver + telemetry projection
  Game/Ksa/Actuators/IvaActuator.cs   NEW: SetEnabled(bool) → IvaForceRender (game thread)
  Game/Ksa/Actuators/WeldActuator.cs  NEW: Create/Here/Remove/Enable/Clear → WeldManager
  Game/Ksa/Readers/PartsReader.cs     NEW: top-level parts → List<PartSnapshot>, cached
  Game/Ksa/KsaCatalog.cs              + dispatch for the 6 new actions ([KsaAnchor] on KSA calls)
  Game/TelemetrySampler.cs            + populate Parts (gated), Welds, AlwaysRenderIva
  Mod.cs / Game/Mod.Game.cs           + WeldManager lifecycle; drive WeldManager.Update in OnAfterUi;
                                       IVA disable + weld clear in Unload
```

`[KsaAnchor]` every KSA member touched (PartModel/Template, Part listing, Teleport/Orbit, solver
timing). Update `docs/KSA_INTEGRATION_MATRIX.md` and the `scope/` pages alongside.

---

## 4. Reads: parts listing + caching

### 4.1 Snapshot records (`SimSnapshot.cs`)
```csharp
public sealed record PartSnapshot(
    int Index, uint InstanceId, string Id, string DisplayName, string Template,
    bool IsRoot, int SubpartCount, double3Snap PositionVehicleAsmb);

// On VesselSnapshot (init-only, default empty — no construction site changes):
public IReadOnlyList<PartSnapshot> Parts { get; init; } = [];

// Top-level weld registry projection + IVA flag on SimSnapshot:
public IReadOnlyList<WeldSnapshot> Welds { get; init; } = [];
public bool AlwaysRenderIva { get; init; }
```

### 4.2 PartsReader (`Game/Ksa/Readers/PartsReader.cs`)
Enumerate `vehicle.Parts.Parts` (top-level), build `PartSnapshot`s. `[KsaAnchor]` the part members.
```csharp
private static List<PartSnapshot> Build(Vehicle vehicle)
{
    var parts = vehicle.Parts.Parts;               // ReadOnlySpan<Part>, top-level only
    var list = new List<PartSnapshot>(parts.Length);
    for (var i = 0; i < parts.Length; i++)
    {
        var part = parts[i];
        var pos = part.PositionVehicleAsmb;
        list.Add(new PartSnapshot(i, part.InstanceId, part.Id, part.DisplayName,
            part.Template.Id, part.PartParent is null, part.SubParts.Length,
            new double3Snap(Sanitize.Finite(pos.X), Sanitize.Finite(pos.Y), Sanitize.Finite(pos.Z))));
    }
    return list;
}
```

### 4.3 Caching (count-change primary, 10 s backstop)
Per-vehicle cache via `ConditionalWeakTable<Vehicle, Entry>` (auto-evicts with the vehicle — no leak,
no manual pruning). The reader runs only on the game thread (sampler), so no locking.
```csharp
private sealed class Entry { public IReadOnlyList<PartSnapshot> List = []; public int Count = -1; public double BuiltUt; }
private static readonly ConditionalWeakTable<Vehicle, Entry> _cache = new();

public static IReadOnlyList<PartSnapshot> Sample(Vehicle vehicle, double ut)
{
    var e = _cache.GetOrCreateValue(vehicle);
    var liveCount = vehicle.Parts.Count;                 // O(1) — the cheap change signal
    if (e.Count != liveCount || ut - e.BuiltUt >= 10.0)  // rebuild on part add/remove, else every 10 s
    {
        e.List = Build(vehicle);
        e.Count = liveCount;
        e.BuiltUt = ut;
    }
    return e.List;                                        // immutable; safely reused across ticks
}
```
- **Count-change** is the "simple way to know to invalidate" the brief asks for: it instantly catches
  the dominant edit case (parts added/removed). The **10 s timer** is only a backstop for
  count-preserving edits (e.g. swapping one part for another, or in-place re-position) — rare in
  flight, and a ≤10 s staleness on the listing is harmless (welds resolve parts live, not from this
  cache — §5.3). `ut` is sim seconds (consistent with the sampler); under pause it doesn't advance,
  but parts don't change while paused and the count check still fires regardless.
- **Rejected:** a Harmony hook on `PartTree.RecomputeAllDerivedData` for exact invalidation — it adds
  a patch (against the "minimal patches" goal) and over-fires (recompute runs on resource changes too).

### 4.4 Gate + wiring
Add `TelemetrySettings.VesselParts` (volatile bool, default **true**; live-toggleable like
`VesselDetail`). In `Sample()` (`TelemetrySampler.cs:108`), build `VesselSnapshot` with
`Parts = _settings.VesselParts ? PartsReader.Sample(vehicle, ut) : []`. When off, the reader is skipped
and the subtree vanishes on every transport. Tree: in `VesselDir` (`SimFsTree.cs:~338`) add
`if (vessel.Parts.Count > 0) children.Add(PartsDir(p, vesselId));`, mirroring `EnginesDir`/`EngineDir`.

---

## 5. The careful bits: Harmony + threading

### 5.1 IVA — dynamic patches, only while toggled on
Port `IvaForceRender` into `Game/Ksa/Render/IvaForceRender.cs`, with one change vs. unscience: **the
Harmony patches are installed on enable and removed on disable** (unscience leaves them installed and
gates on `_enabled`). This satisfies "only active when toggled on" literally — **default = no patches**.

- Own a dedicated `Harmony` instance (`"gatos.iva"`), created lazily on first enable.
- `SetEnabled(true)` (on the **game thread**, from the command drain):
  1. if patches absent → patch the **ctor postfix** (catch new internal templates) and the
     **editor `AddInstance` postfix** (VAB parity); both reference `PartModel`/`Template`.
  2. bulk pass `PartModel.Instances`: for each `Template.Internal`, set `false` and track it.
- `SetEnabled(false)`: restore tracked templates to `Internal=true`, clear tracking, **unpatch**.
- Reads: expose a `volatile bool Enabled`; the sampler copies it into `SimSnapshot.AlwaysRenderIva`
  (so the `/sim` read is snapshot-backed and thread-clean).
- `Unload`: call `SetEnabled(false)` (restores templates + unpatches) before the mod tears down.

Why dynamic install is safe here: enabling IVA is a rare user action, runs on the game thread (where
`PartModel.Instances` and Harmony (un)patching are both legal), and `PartModel` ctor is **not** a
per-frame hot path (parts are built on vessel load/staging, not each frame). The editor `AddInstance`
postfix is the only per-render patch and it exists **only while enabled**.

> Threading note: `debug.always_render_iva` is **Frame phase** → drains in `OnBeforeUi` on the game
> thread (`IvaActuator.SetEnabled`). Correct thread for both the template flip and (un)patching.

### 5.2 Welds — driver placement (no new Harmony patch)
Drive `WeldManager.Update(dt)` from the **existing** `[StarMapAfterGui] OnAfterUi` game-thread hook
(`Mod.cs:256`), self-gated so it is a single `if (manager.IsEmpty) return;` when no welds exist. This
reproduces unscience's **validated** placement and timing exactly:
- `OnAfterUi` runs on the game thread, after the GUI and after the vehicle-solver workers (queued in
  `PrepareFrame`) have had the render to finish.
- `WeldManager.Update` first calls `JobSystems.VehicleSolvers.Wait()` (cheap once they've finished),
  then teleports each source with the orbit time stamped at
  `Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime` (§6).

This is **strictly less invasive than a Harmony patch**: zero patches, zero hot-path cost when idle,
and work happens *only when welds are active* — which is the brief's actual intent. We add `Update` to
`OnAfterUi` and `Clear` to `Unload`; nothing else.

**Addressing "I only want the harmony patch active when we have active welds":** the welds tick needs
no patch at all, so there is nothing to install/remove — the self-gate *is* the "only when active"
guarantee. If, after the in-game pass, we decide a Harmony hook is preferable to extending `OnAfterUi`
with state mutation, the drop-in alternative is a **dynamically installed** postfix on
`Universe.ApplyVehicleSolvers` (the point right after solver results are applied and `_lastSimStep`
advances, before the next solve is queued — `Program.cs:1944`/`Universe.cs:1640`), patched on first
weld and unpatched on last weld. That placement may even let us drop the `Wait()` (workers are already
applied at that point), but the body-time/`NextTime` alignment there is **unverified** and must be
confirmed in-game before adopting. **Recommendation: ship the self-gated `OnAfterUi` driver (proven);
treat the dynamic `ApplyVehicleSolvers` patch as a validated-later optimization.**

> This introduces a third game-thread mutation site (after the Frame drain and the solver-phase
> prefix). It obeys threading rule 1 (game thread only) and matches unscience's proven pattern.
> Document it in `docs/ARCHITECTURE.md` and the threading section of `CLAUDE.md`.

### 5.3 Weld lifecycle & robustness (in the driver, per tick)
For each weld (port unscience's guards + add liveness checks):
- Drop the weld if source or target is no longer in `Universe.CurrentSystem.All`, or
  `Source.Parent != Target.Parent` (returns `EBUSY`-class removal), or target state is NaN.
- **Re-resolve the anchor `Part` by `InstanceId`** from `Target.Parts.Parts` each tick (O(parts), tiny):
  if the stored part was removed, fall back to body-frame anchoring (or drop — configurable; default
  fall back to CoM so the weld survives staging of unrelated parts). This makes welds robust to the
  target being edited/staged. (Welds resolve parts **live**, never via the §4.3 listing cache.)
- On removal/clear/unload: just drop entries — no Teleport needed (we never scaled anything).

---

## 6. Weld math (ported verbatim from `WeldEngine.UpdateWeld`)

Per active, `WeldEnabled` weld (all KSA calls `[KsaAnchor]`-annotated):
```csharp
// anchor = a specific target part (preferred), else target CoM/body frame
double3   tgtPosCci    = target.GetPositionCci();
double3   tgtVelCci    = target.GetVelocityCci();
doubleQuat tgtBody2Cci = target.GetBody2Cci().NormalizedOrZero();   // guard: skip if zero/NaN

double3 anchorPosCci; doubleQuat anchorBody2Cci;
if (anchorPart is not null) {
    double3 partOffset = anchorPart.PositionVehicleAsmb - target.CenterOfMassAsmb;
    anchorPosCci   = tgtPosCci + partOffset.Transform(tgtBody2Cci);
    anchorBody2Cci = doubleQuat.Concatenate(anchorPart.Asmb2VehicleAsmb, tgtBody2Cci).NormalizedOrZero();
    if (anchorBody2Cci == default) anchorBody2Cci = tgtBody2Cci;
} else { anchorPosCci = tgtPosCci; anchorBody2Cci = tgtBody2Cci; }

double3 offsetCci   = new double3(pos.X, pos.Y, pos.Z).Transform(anchorBody2Cci);
double3 newSrcPosCci = anchorPosCci + offsetCci;
double3 newSrcVelCci = tgtVelCci;                                   // co-moving

doubleQuat cci2Cce = source.Parent.GetCci2Cce();
doubleQuat newSrcBody2Cce; double3 newBodyRates;
if (lockRotation) {
    doubleQuat deltaRot      = EulerDegreesToQuat(rot.X, rot.Y, rot.Z);   // ZYX intrinsic (port)
    doubleQuat newSrcBody2Cci = doubleQuat.Concatenate(deltaRot, anchorBody2Cci);
    newSrcBody2Cce = doubleQuat.Concatenate(newSrcBody2Cci, cci2Cce).NormalizedOrZero();
    newBodyRates   = target.BodyRates;
} else {
    doubleQuat srcBody2Cci = source.GetBody2Cci().NormalizedOrZero();
    newSrcBody2Cce = doubleQuat.Concatenate(srcBody2Cci, cci2Cce).NormalizedOrZero();
    newBodyRates   = source.BodyRates;                              // NaN-guard → zero
}

SimTime tickEnd = Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime;  // NOT GetElapsedSimTime
Orbit newOrbit  = Orbit.CreateFromStateCci(source.Parent, tickEnd, newSrcPosCci, newSrcVelCci,
                                           source.Orbit.OrbitLineColor);
source.Teleport(newOrbit, newSrcBody2Cce, newBodyRates);
source.UpdatePerFrameData();
```
`EulerDegreesToQuat` is copied verbatim (ZYX intrinsic, `doubleQuat(x,y,z,w)` ctor). NaN guards on
target pos/vel/quat and body rates are copied verbatim. The `gatos`-skill "weld" Teleport note and
gatOS's `DebugActuator.Teleport` confirm this is the house pattern.

**`weld_here` capture (for `debug.weld_here`)** — invert the update at create time so the source stays
put, then locks:
```csharp
// offset (anchor frame) = R(anchorBody2Cci)^-1 · (srcPosCci − anchorPosCci)
double3 captOffset = (source.GetPositionCci() - anchorPosCci).Transform(anchorBody2Cci.Conjugate());
// delta orientation = srcBody2Cci · anchorBody2Cci^-1, then extract ZYX Euler degrees
doubleQuat delta = doubleQuat.Concatenate(source.GetBody2Cci(), anchorBody2Cci.Conjugate()).NormalizedOrZero();
float3 captEuler = QuatToEulerDegrees(delta);   // small ZYX-extraction helper (inverse of EulerDegreesToQuat)
```
Store `captOffset`/`captEuler` as the weld's `Position`/`Rotation`; thereafter it is an ordinary weld.
(`QuatToEulerDegrees` is the only new math; ~15 lines, unit-tested game-free.)

---

## 7. Write path wiring

### 7.1 LineControlFile (`gatOS.SimFs/Commands/LineControlFile.cs`)
A generic `CommandFile` subclass parsing the whole trimmed line:
```csharp
public sealed class LineControlFile : CommandFile {
    private readonly Func<string, SimCommand?> _parse;
    public static LineControlFile Create(string name, ulong qid, ICommandSink sink,
        Func<string> read, Func<string, SimCommand?> parse) => new(name, qid, sink, read, parse);
    protected override SimCommand? Parse(string token) => _parse(token);   // null ⇒ EINVAL
}
```
Weld parser (built in `SimFsTree`): split on whitespace; require 9 tokens for `weld`
(`target part x y z pitch yaw roll lock`) / 2–3 for `weld_here`; `target` is a non-empty string,
the rest parse as finite doubles (`part`→Values[0], `lock`→0/1); reject otherwise. Build
`SimCommand(source, "debug.weld_create", NoOrdinal, 0){ Token=target, Values=[part,x,y,z,p,y,r,lock] }`.

### 7.2 SimFsTree additions
- **IVA flag** in `DebugDir()` (`:509`): `FlagControl("debug/always_render_iva", "always_render_iva",
  "", "debug.always_render_iva", SimCommand.NoOrdinal, () => Formats.Flag(_store.Current.AlwaysRenderIva))`.
- **Per-source weld controls** in `DebugVesselDir()` (`:536`): `weld`/`weld_here` via `LineControlFile`,
  `unweld` via `TriggerFile(… "debug.weld_remove" …)`. Read lambdas project this source's
  `_store.Current.Welds` entry (or "").
- **Welds registry** in `DebugDir()`: a `DelegateDirectory("welds", …)` enumerating
  `_store.Current.Welds` into `welds/<source_id>/{target,part,offset,rotation,lock_rotation,enabled}`
  (the `enabled` leaf is a `FlagControl` → `debug.weld_enable`), plus `clear` (TriggerFile →
  `debug.weld_clear`) and `count` (`Line`).

### 7.3 KsaCatalog (`Game/Ksa/KsaCatalog.cs`)
- In `Execute`, handle the **vessel-agnostic** actions before vehicle resolution (mirror `debug.warp`):
  `debug.always_render_iva` → `IvaActuator.SetEnabled(c.Value != 0)`; `debug.weld_clear` →
  `WeldActuator.Clear()`.
- `debug.weld_create`/`weld_here` need both source (`VesselId`) **and** target (`Token`): resolve both
  (reuse `ResolveVehicle`), resolve the anchor `Part` by `InstanceId` from the target, then call
  `WeldActuator`. Reject `source==target` (`EBUSY`), missing part (`ENOENT`).
- `debug.weld_remove`/`weld_enable` are source-scoped → handle in `Dispatch(vehicle, c)`.
- `WeldActuator`/`IvaActuator` receive the `WeldManager`/`IvaForceRender` instances (constructor-
  injected into `KsaCatalog`, owned by `Mod`).

### 7.4 Mod lifecycle (`Mod.cs` / `Mod.Game.cs`)
- `OnFullyLoaded`: create `WeldManager` + `IvaForceRender`; pass into the `KsaCatalog`/control objects
  (`EnsureControlObjects`).
- `OnAfterUi` (`:256`): after `DrawGameUi()`, `try { _weldManager?.Update(dt); } catch { … }` (the
  manager early-returns when empty). Guard with the same `_uiDead`/`ReferenceEquals(_instance,this)`
  discipline.
- `Unload` (`:279`): `_weldManager?.Clear()` and `_ivaForceRender?.SetEnabled(false)` (restores
  templates + unpatches) — before stopping servers.

---

## 8. Implementation order (each step ends green: `dotnet build gatos.slnx` + `dotnet test`)

1. **Snapshot + Formats** — `PartSnapshot`, `WeldSnapshot`, `VesselSnapshot.Parts`,
   `SimSnapshot.{Welds,AlwaysRenderIva}`; any formatter. (game-free; add tests.)
2. **LineControlFile** + tests (parse/▢reject; EINVAL path).
3. **SimFsTree** — `PartsDir`/`PartDir`, IVA flag, weld controls, welds registry; feed from snapshot.
   Tests via the shared managed 9p client (fake snapshot with parts + welds → assert tree + values +
   that writes submit the right `SimCommand`). Confirm HTTP/MQTT mirrors via the existing parity tests.
4. **SimJson** — add parts/welds/iva to the full snapshot JSON; parity test.
5. **TelemetrySettings.VesselParts** + config plumbing (default true) + tests.
6. **PartsReader** (+ `[KsaAnchor]`), wired into the sampler behind the gate. (game-coupled.)
7. **IvaForceRender** + `IvaActuator`; KsaCatalog dispatch; Mod lifecycle (disable on unload).
8. **WeldEntry/WeldEngine/WeldManager** (+ `QuatToEulerDegrees`, capture); `WeldActuator`; KsaCatalog
   dispatch; sampler projection of `Welds`; driver in `OnAfterUi`; clear on unload.
9. **Docs**: SPEC, KSA_INTEGRATION_MATRIX, scope pages, ARCHITECTURE, CLAUDE.md status/threading,
   VALIDATION checklist, gatos skill (+ optional recipe).

Game-free steps (1–5) are fully unit-tested. Game-coupled steps (6–8) compile-gate on `KSA.dll` and
are covered by the in-game checklist (§9) per gatOS convention.

---

## 9. Testing & validation

**Game-free (NUnit, in `*.Tests`):**
- `LineControlFile`: valid weld/weld_here parse → exact `SimCommand`; arity/format errors → null/EINVAL.
- Tree: a synthetic `SimSnapshot` with parts + welds → `parts/<n>/*`, `welds/<id>/*`,
  `always_render_iva` present with correct values; writes route to the sink with the right action/args.
- Transport parity: parts/welds leaves appear under `/v1/fs` + `gatos/sim/` (existing parity harness);
  `SimJson` includes them.
- `QuatToEulerDegrees` ∘ `EulerDegreesToQuat` round-trip within tolerance.

**In-game (add to `docs/VALIDATION.md`; needs a live flight, like T6.6/T9.3/G1–G4):**
- IVA: `echo 1 > …/always_render_iva` → interiors visible from external cameras; `echo 0` restores;
  confirm no patches installed when off (log/`status`); toggle repeatedly; verify Unload restores.
- Parts: `ls …/parts/`, read `instance_id`/`display_name`/`template`; edit the vessel (add/remove a
  part) → listing updates within a frame (count change) and ≤10 s for count-preserving edits.
- Welds: `weld_here` two vessels (part anchor + CoM anchor); verify rigid tracking through maneuvers,
  rotation, and time-warp; `enabled 0/1` suspend/resume; `unweld`/`clear`; stage an unrelated part on
  the target (weld survives); confirm zero per-frame cost with no welds (PerfStat) and clean Unload.

---

## 10. Documentation mandate (do in the same work item)

- **`SPEC_9P_FILESYSTEM.md`** — all §2 paths/formats/actions/errno/phase + HTTP/MQTT mirrors. (binding)
- **`docs/KSA_INTEGRATION_MATRIX.md`** — new bindings: PartModel/Template.Internal render gate, Part
  listing, Teleport/Orbit for welds, solver timing (`GetJobSimStep`/`VehicleSolvers.Wait`).
- **`scope/`** — `ksa-read-surface.md` (parts reader), `ksa-write-surface.md` (weld + IVA actuators),
  `ksa-runtime-coupling.md` (weld driver hook + dynamic IVA Harmony), and the `scope/FULL_SCOPE.md`
  inventory. Each with its game-version status. (binding)
- **`docs/ARCHITECTURE.md`** — the new game-thread mutation site (`OnAfterUi` weld driver) + the
  `gatos.iva` dynamic Harmony instance + the parts cache.
- **`CLAUDE.md`** — status table (new feature row) + threading section (third mutation site); status
  detail in `docs/MILESTONES.md`.
- **`.claude/skills/gatos/`** — mention welds/IVA/parts and point at the SPEC; optional `recipes.md`
  entry (`weld_here` two vessels).

---

## 11. Risks & open questions

1. **Weld driver frame-phase (medium).** The self-gated `OnAfterUi` + `Wait()` + `GetJobSimStep().NextTime`
   is a faithful port of unscience's validated path; still, gatOS hasn't mutated vehicle state in
   `OnAfterUi` before. Confirm in-game (no "SnapToLeader body time X / origin time Y" warnings, no
   jitter under warp). The `ApplyVehicleSolvers`-postfix alternative (§5.2) is a *later* optimization,
   not the initial ship.
2. **`Template.Internal` is template-global (low).** Flipping it affects every instance of that part
   type across all vessels — intended for a global cheat. The bulk-flip + restore (and dynamic
   unpatch) fully revert it; verify no residual after rapid toggles / unload.
3. **`InstanceId` stability (low).** Assigned from a running id at construction; stable for a part's
   lifetime, the right weld key. It changes if a part is destroyed+recreated (e.g. some edits) — the
   driver's live re-resolve + CoM fallback (§5.3) handles that gracefully.
4. **Parts cost on huge vessels (low).** Mitigated by the count-change/10 s cache + the `VesselParts`
   gate (off ⇒ skipped entirely). Watch PerfStat on 200+ part craft.
5. **Phase choice for `debug.weld_*` (low).** All Frame phase: they only mutate the `WeldManager`
   registry (the *teleport* is the driver's job, not the command's), so no `SolverActions` entry is
   needed. Confirm create-then-immediate-tick has no first-frame ordering glitch in-game.
