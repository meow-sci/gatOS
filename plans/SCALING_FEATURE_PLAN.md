# SCALING_FEATURE_PLAN — per-vessel model scaling on `/sim`

Status: **implemented** (2026-07-02; in-game validation — checklist item 10 in §10 — still pending a
live KSA flight, checklist now in `docs/VALIDATION.md`). The D6 "minimal documentation" choice was
superseded the same day when the sibling `always_render` feature landed with full doc lockstep: `scale`
is now also catalogued in `docs/KSA_INTEGRATION_MATRIX.md`, `scope/ksa-write-surface.md`,
`docs/MILESTONES.md`, the CLAUDE.md status table, and the `gatos` skill (the authority-gate exemption
was promoted to `KsaCatalog.AnyVesselActions` as §3.2b anticipated). This plan adds an independent
**vessel model scaling**
feature to gatOS, ported from the scaling behavior in the sibling `unscience/garrys-torch` mod but
**decoupled from welds** and exposed as a first-class per-vessel `/sim` node.

## 0. Goal (what we're building)

A new read/write node:

```
/sim/vessels/by-id/<id>/scale
```

- **Read** → the vessel's current uniform model scale factor (best-effort; `1` = unscaled/1:1).
- **Write** → an arbitrary **positive** `double` (e.g. `echo 50000 > scale`) uniformly rescales the
  whole vessel model, exactly like `unscience`'s `ApplyVehicleScale`.
- **Default** `1.0`. **No clamp** other than rejecting `0` and negatives (and non-finite / unparseable
  input) → `EINVAL`. We deliberately allow huge factors (100,000+ for "planet-sized kitten" resizing).
- Mirrored on **HTTP `/v1/fs/...`** and **MQTT `gatos/sim/...`** automatically (transport parity is
  structural — see §6).

### Locked design decisions (confirmed with the requester)

| # | Decision | Rationale |
|---|---|---|
| **D1** | **One-shot apply on write. No per-frame re-application.** | Re-applying every game tick is wasteful. Scaling writes `Part.Scale` once (like `unscience`), which the game persists until it rebuilds the vessel. |
| **D2** | **Read-back is best-effort** from the live part scale. A successful write does **not** depend on a readable value — if a vessel can't yield a representative scale we report `1.0` (never error the read). | The requester accepted "one-way write is fine if a guaranteed readback isn't viable." Reading `Part.Scale.X` is cheap and reliable in the normal case, so we provide the readback; it stays truthful (shows `1.0` if KSA later resets the vessel). No jank. |
| **D3** | **Any vessel by id** — scaling is exempt from the active-vessel authority gate. | The use case is resizing *arbitrary* vessels by path. This is the first deliberate step of moving per-vessel controls **out of `/sim/debug`**. |
| **D4** | **Placed under the regular vessel area** (`/sim/vessels/by-id/<id>/`), **not** `/sim/debug/`. | Requester wants to break this class of control out of the cumbersome debug namespace. |
| **D5** | **No dedicated config gate.** Reuses the existing `control_enabled` master write switch (like every other control). | Welds/IVA/thug_life have no per-cheat toggle either; a new gate is unnecessary. |
| **D6** | **Minimal documentation.** The *only* doc note is that `scale` lives under the regular vessel area intentionally. No `scope/`, `KSA_INTEGRATION_MATRIX`, `ARCHITECTURE`, `MILESTONES`, `gatos` skill, or `CLAUDE.md` status/threading churn. | Per requester direction. The one-shot actuator adds **no** new game-thread mutation site / driver / Harmony patch, so the threading-rules and architecture docs genuinely need no change anyway (see §7). The `[KsaAnchor]` attribute remains the in-code source of truth for the KSA binding. |

### Non-goals

- No coupling to welds (the two share **zero** code/state in `unscience` beyond packaging).
- No animation/preset/interpolation machinery (those are `unscience` weld-UI extras).
- No per-frame reconciler / `ScaleManager` / driver / Harmony patch.
- No clamping of the factor (other than `> 0`).
- No config gate, no `/sim/debug` placement.

---

## 1. What we're porting — the `unscience` scaling behavior

Source: `C:\Users\Alex\repos\meow-sci\unscience\garrys-torch.lib\WeldEngine.cs:154-203`. Scaling in
`unscience` is **much simpler than welding** and is applied **on-change only** (never in the per-frame
weld driver). It writes exactly one KSA field per part and has one reflection special-case.

```csharp
// WeldEngine.cs:154-195  (unscience — the exact behavior to reproduce)
public static void ApplyVehicleScale(Vehicle vehicle, float factor)
{
    foreach (var part in vehicle.Parts.Parts)
        SetPartScaleRecursive(part, factor);

    // KittenEva renders via CharacterAvatar.Core.Scale (Core.Scale 0.01 == 1:1)
    if (vehicle.GetType().Name == "KittenEva")
    {
        // reflect _renderable -> _characterAvatar -> Core -> Scale (float); set = factor * 0.01f
        // (defensive: null-checks each hop, tries field then property, struct write-back, swallow+log)
    }
}

// WeldEngine.cs:197-203
public static void SetPartScaleRecursive(Part part, float factor)
{
    part.Scale = new double3(factor, factor, factor);   // Part.Scale is a Brutal.Numerics.double3
    foreach (var sub in part.SubParts)
        SetPartScaleRecursive(sub, factor);
}
```

### KSA members touched (all confined to one gatOS file, `[KsaAnchor]`-annotated)

| Member | Type | Kind | Notes |
|---|---|---|---|
| `Vehicle.Parts` | `KSA.Vehicle` | field → `KSA.PartTree` | root of the part tree |
| `PartTree.Parts` | `KSA.PartTree` | `ReadOnlySpan<Part>` | top-level parts |
| `Part.SubParts` | `KSA.Part` | `ReadOnlySpan<Part>` | recurse |
| `Part.Scale` | `KSA.Part` | `Brutal.Numerics.double3` (**settable**) | **the one write**; default `(1,1,1)`; setter invalidates cached transform matrices; drives rendered size + bounding/raycast geometry. Does **not** touch mass/inertia/colliders/joints directly. |
| `KittenEva._renderable → _characterAvatar → Core → Core.Scale` | reflected | `float` | avatar scaled at `factor * 0.01f` (`0.01` = 1:1). Gate is a `GetType().Name == "KittenEva"` string check (brittle — flag as High churn). |

Behavioral facts we preserve (D1): apply once on write; the game keeps `Part.Scale` until it rebuilds
the vessel (scene reload / staging / undock), at which point it reverts to `1.0` — **the same
limitation as `unscience`**, which the requester accepted. Removal/reset semantics: writing `1` returns
the vessel to 1:1.

### One deliberate deviation from `unscience`: `double` factor, not `float`

`unscience` uses `float factor`. The requester wants "any positive **double**" up to 100,000s.
`Part.Scale` is already a `double3`, so we take a **`double`** factor end-to-end for full precision on
normal vessels. The KittenEva avatar `Core.Scale` is a `float`, so that one special-case is cast
(`(float)(factor * 0.01)`) — float easily represents 100,000s (just fewer mantissa bits), acceptable
for the avatar-only path.

---

## 2. Architecture & data flow

Everything reuses existing gatOS machinery — **no new command archetype, no new snapshot list, no new
game-thread driver.**

```
WRITE  echo 50000 > /sim/vessels/by-id/<id>/scale
  → NumberControl node (SimFsTree)                       [ControlFile.Number, parses invariant double]
  → SimCommand("<id>", "vessel.scale", NoOrdinal, 50000) [Frame phase — not in SolverActions]
  → CommandQueue.SubmitAsync (transport thread) → Drain(Frame) on the game thread
  → KsaCatalog.Execute → authority gate (scale-exempt, D3) → Dispatch
  → ScaleActuator.Set(vehicle, 50000)  [validate > 0 → EINVAL; else apply recursively + KittenEva]

READ   cat /sim/vessels/by-id/<id>/scale
  → game thread: VesselReader.Sample → VesselSnapshot.Scale = ScaleActuator.Read(vehicle)  [best-effort]
  → SnapshotStore (volatile swap) → NumberControl read lambda → Formats.Scalar (G9)   [9p]
                                  → VfsScan leaf walk → HTTP /v1/fs + MQTT gatos/sim/*  [auto parity]
                                  → SimJson full-snapshot → "scale": 50000              [auto, record prop]
```

---

## 3. Implementation — file by file

### 3.1 NEW: `gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs`

The one KSA-touching file (KSA type names stay under `Game/Ksa/…` per the dependency rule). It is a
static actuator like `ThrottleActuator`, plus a best-effort `Read` helper (co-located so the KittenEva
reflection lives in exactly one place). **No manager, no state.**

```csharp
using System.Reflection;
using Brutal.Numerics;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Uniform vessel model scaling (/sim/vessels/by-id/&lt;id&gt;/scale). Ported from the sibling
///     unscience mod's WeldEngine.ApplyVehicleScale but decoupled from welds and taking a double
///     factor (Part.Scale is a double3). One-shot: applied once per write, never re-driven per frame.
///     Game-thread only (drained in the Frame command phase). All KSA access confined here.
/// </summary>
internal static class ScaleActuator
{
    /// <summary>Write path: validate positivity, then apply uniformly. Called from KsaCatalog.Dispatch.</summary>
    internal static CommandResult Set(Vehicle vehicle, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
            return new CommandResult(CommandOutcome.Invalid, "scale must be a finite value > 0");

        Apply(vehicle, factor);
        return CommandResult.Ok;
    }

    [KsaAnchor("Vehicle.Parts.Parts; Part.{Scale(set),SubParts}; "
            + "KittenEva._renderable._characterAvatar.Core.Scale (reflected)",
        SourceFile = "KSA/Vehicle.cs / KSA/PartTree.cs / KSA/Part.cs", Verified = "2026-07-02",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.High,
        Notes = "Uniform recursive Part.Scale write (double3). KittenEva avatar scaled via reflected "
            + "Core.Scale = factor*0.01f (0.01 == 1:1). Ported from unscience WeldEngine.ApplyVehicleScale.")]
    private static void Apply(Vehicle vehicle, double factor)
    {
        foreach (var part in vehicle.Parts.Parts)
            SetPartScaleRecursive(part, factor);

        if (vehicle.GetType().Name == "KittenEva")
            TryScaleAvatar(vehicle, factor);   // reflection, defensive; swallow+log on failure
    }

    private static void SetPartScaleRecursive(Part part, double factor)
    {
        part.Scale = new double3(factor, factor, factor);
        foreach (var sub in part.SubParts)
            SetPartScaleRecursive(sub, factor);
    }

    /// <summary>Best-effort readback (D2): a representative part's uniform scale, else the KittenEva
    /// avatar scale, else 1.0. Never throws — a read must never fail the file.</summary>
    [KsaAnchor("Part.Scale (get); KittenEva avatar Core.Scale (reflected)",
        SourceFile = "KSA/Part.cs", Verified = "2026-07-02", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Medium, Notes = "Representative-part readback; falls back to 1.0.")]
    internal static double Read(Vehicle vehicle)
    {
        try
        {
            foreach (var part in vehicle.Parts.Parts)
                return part.Scale.X;                     // uniform; X is representative
            if (vehicle.GetType().Name == "KittenEva" && TryReadAvatar(vehicle) is { } s)
                return s;                                 // avatar Core.Scale / 0.01
        }
        catch { /* best-effort */ }
        return 1.0;
    }

    // TryScaleAvatar / TryReadAvatar: the unscience KittenEva reflection, inline (no shared helper in
    // gatOS). BindingFlags Instance|Public|NonPublic; try field then property; struct write-back; log.
}
```

Notes / defensive posture:
- If `Part.Scale` turns out **not** to be public-settable in gatOS's referenced KSA assemblies (it is
  public in the `unscience` decomp), fall back to a cached `FieldInfo`/`PropertyInfo` set-by-reflection
  exactly like `ThrottleActuator` (`gatOS.GameMod/Game/Ksa/Actuators/ThrottleActuator.cs:17-40`), and
  degrade to `CommandOutcome.Unsupported` (EOPNOTSUPP) when absent. **Verify at implementation time.**
- `KsaCatalog.Execute` already wraps `Dispatch` in try/catch → a thrown KSA call becomes
  `CommandOutcome.Fault` (EIO) + a health latch, so `Set` doesn't need its own catch.
- Placed in the existing `Actuators/` folder (already enumerated in the CLAUDE.md G2 rule) — **avoids a
  new `Game/Ksa/` subfolder and therefore avoids a CLAUDE.md project-map edit** (supports D6).

### 3.2 `gatOS.GameMod/Game/Ksa/KsaCatalog.cs`

Two edits, both tiny. No ctor change (the actuator is static — nothing to inject).

**(a) Dispatch row** — add next to the other `vessel.*` controls (after `KsaCatalog.cs:101`):

```csharp
"vessel.scale" => ScaleActuator.Set(vehicle, c.Value),
```

**(b) Authority-gate exemption (D3)** — at `KsaCatalog.cs:68-71`, let `vessel.scale` reach any
addressed vessel regardless of `control_all_vessels`, mirroring how the `debug.*` namespace is exempt:

```csharp
// Authority gate (G-D1): with all_vessels=false only the controlled vehicle is commandable.
// The cheat namespace is exempt; vessel.scale is likewise exempt — it is a deliberate per-vessel
// operation that works on any addressed vessel (first control moved out of the /sim/debug namespace).
var anyVessel = isDebug || command.Action == "vessel.scale";
if (!anyVessel && !allVessels && Program.ControlledVehicle?.Id != vehicle.Id)
    return new CommandResult(CommandOutcome.Denied, "control is restricted to the active vessel");
```

(If more per-vessel controls migrate out of debug later, promote this to a small `HashSet` — kept local
to `KsaCatalog` since it's a GameMod authority policy, keeping the `SimFs` layer clean.)

### 3.3 `gatOS.SimFs/Snapshots/SimSnapshot.cs`

Add one init-only property to `VesselSnapshot` (record at `SimSnapshot.cs:106`), beside `ThrottleCmd`
(`:158-159`). Init-only + default keeps every existing construction site and test valid.

```csharp
/// <summary>Uniform vessel model scale factor (<c>scale</c> read; best-effort). 1.0 = unscaled.</summary>
public double Scale { get; init; } = 1.0;
```

### 3.4 `gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs`

Populate the read-back in `Sample`, beside the other setpoint read-backs (`VesselReader.cs:169`,
`ThrottleCmd = …`):

```csharp
Scale = ScaleActuator.Read(vehicle),
```

Cheap (`Part.Scale.X` read; `ScaleActuator.Read` is exception-safe and defaults to `1.0`). It rides the
always-sampled base record, so it needs **no** telemetry gate. (If we ever want it gated it would sit
naturally behind `telemetry_vessel_detail`, but keep it ungated for now — one field.)

### 3.5 `gatOS.SimFs/SimFsTree.cs`

Add the node to the `VesselDir` children list (`SimFsTree.cs:349-382`), as a sibling of `com`
(after `:357`), using the existing **`NumberControl`** helper (`:551-557`). `NumberControl` already
degrades to a read-only `StaticTextFile` when no command sink is wired, so `scale` is **always
readable** and becomes **writable when control is enabled** — exactly the desired "regular vessel-area
read/write" shape, placed *outside* `CtlDir`.

```csharp
Line($"{p}/com", "com", () => Formats.Vector(Vessel(vesselId).CenterOfMass)),
// Model scale factor. Intentionally a first-class vessel node (NOT under /sim/debug): the first
// per-vessel control deliberately moved out of the debug namespace. Read = current (best-effort);
// write any value > 0 to rescale the whole model (action vessel.scale, one-shot).
NumberControl($"{p}/scale", "scale", vesselId, "vessel.scale", SimCommand.NoOrdinal,
    () => Formats.Scalar(Vessel(vesselId).Scale)),
new StaticTextFile("telemetry", Qid($"{p}/telemetry"), …),
```

### 3.6 No changes needed to

- **`SimCommand`** — reuse `(VesselId, Action, Ordinal, Value)`; `vessel.scale` is **not** added to
  `SolverActions`, so `Phase` derives to `Frame` (correct: `Part.Scale` is geometry, not solver state).
- **`ControlFile` / `CommandFile` / `CommandQueue`** — `ControlFile.Number` already parses a
  culture-invariant finite `double` (any sign); positivity is enforced in the actuator (see §5).
- **`Formats`** — `Formats.Scalar` (`Formats.cs:29`, `G9` invariant) is the read format.
- **`SimJson` / HTTP / MQTT code** — parity is automatic (§6).

---

## 4. Positive-only validation & errno

| Input | Where rejected | Result on `write(2)` |
|---|---|---|
| `abc`, empty, non-finite (`inf`/`nan`) | `ControlFile.Number.Parse` returns `null` | `EINVAL` |
| `0`, negative (`-5`) | parses fine → `ScaleActuator.Set` returns `CommandOutcome.Invalid` | `EINVAL` |
| any `> 0` finite double (incl. `100000`, `0.001`) | applied | success (`0`) |
| missing KSA member (defensive fallback) | `CommandOutcome.Unsupported` | `EOPNOTSUPP` |

`CommandFile.WriteHandle` actuates on the first `\n`, so `echo 50000 > scale` carries the real errno on
the failing `write`. This matches every other gatOS control file.

---

## 5. Threading & safety (why this needs no threading-rule changes)

- **Write applies on the game thread**, in the existing `Frame` command drain (`Mod.DrainCommands` →
  `CommandQueue.Drain(CommandPhase.Frame, …)`), satisfying threading rule 1 (game state mutated only on
  the game thread). It is a normal `CommandQueue` action — **not** a new mutation site.
- **Read is sampled on the game thread** in `VesselReader.Sample` and published via the single volatile
  snapshot swap; 9p/HTTP/MQTT threads only read the published snapshot (rule 2).
- **No per-frame driver, no Harmony patch, no render-thread work.** Because there is no new
  game-thread mutation site or driver, CLAUDE.md's threading rules, `docs/ARCHITECTURE.md`
  "game-thread cheats", and `scope/ksa-runtime-coupling.md` genuinely require **no** edit (consistent
  with D6). This is the concrete payoff of D1 (one-shot).
- Teardown: nothing to tear down (no registry). A vessel left scaled simply stays scaled until the game
  rebuilds it or the user writes `1`. (Optional nicety, out of scope: reset scaled vessels on `Unload`
  — skipped to honor "no new driver/state.")

---

## 6. Transports & parity (all automatic)

Confirmed against the code — adding the node + the snapshot property lights up every transport with
**no per-transport edit** (the binding transport-parity rule stays structural):

- **9p**: read/write directly.
- **HTTP `/v1/fs/vessels/by-id/<id>/scale`**: `GET` (+ SSE `stream`) and `PUT` via
  `VfsScan.Resolve/ReadTextAsync/WriteTextAsync` (the field-level VFS walk). Write propagates the
  control-file errno.
- **MQTT `gatos/sim/vessels/by-id/<id>/scale`** feed + `…/set` — same VFS walk.
- **Full-snapshot JSON** (`GET /v1/vessels/<id>` via `SimJson.Serialize`): the new `Scale` record
  property appears automatically as `"scale"`.
- **Compact `telemetry` doc** (`Formats.VesselTelemetry`, frozen for the TS SDK): **intentionally NOT
  touched** — leaving the SDK ABI frozen. (Add a `scale` line there only as a deliberate future SDK
  change.)

---

## 7. Documentation & maintenance (deliberately minimal — per requester)

Per the requester's explicit direction, we do **not** perform the usual lockstep doc churn. The single
doc note records the intentional placement.

**The one note** — add a single row to `SPEC_9P_FILESYSTEM.md` §3.4.1 (core vessel scalars,
`SPEC_9P_FILESYSTEM.md:189-216`), which is where `/sim` placement is catalogued, with an inline note
that it is intentionally in the regular vessel area (not `/sim/debug/`):

```
| scale | **St** | scalar | Uniform model scale; read = current (best-effort), **write > 0** to rescale
                            (action vessel.scale, Frame, one-shot). Default 1.0. First per-vessel
                            control intentionally placed here rather than under /sim/debug. |
```

(Optionally also add the action to the §5.1 action-key catalog — `| vessel.scale | — | value > 0 |
Frame | scale | positive-only; EINVAL if ≤0 |` — if you want the action list complete. Skip if you
want the truly minimal touch; the SPEC row above already names the action.)

**Explicitly NOT updated** (per D6): `scope/*`, `docs/KSA_INTEGRATION_MATRIX.md`,
`docs/ARCHITECTURE.md`, `docs/MILESTONES.md`, the `gatos` skill (`.claude/skills/gatos/`),
`CLAUDE.md` status table / threading rules / project map. The one-shot actuator adds no new
mutation-site/driver, so the threading & architecture docs need no change on the merits either; the
`[KsaAnchor]` attribute on `ScaleActuator` is the in-code source of truth for the KSA binding and can
be surfaced into those docs later if full lockstep is ever desired.

**Code comments** (not "docs") carry the intent inline: the `SimFsTree` node comment (§3.5) and the
`KsaCatalog` authority-exemption comment (§3.2b) both state the deliberate non-debug placement.

---

## 8. Tests

Game-coupled code (`ScaleActuator`) has no unit project (`gatOS.GameMod` is game-coupled — validated
in-game). The `/sim` surface is unit-tested in `gatOS.SimFs.Tests`.

**Add** `gatOS.SimFs.Tests/Commands/VesselScaleTests.cs` (model on `ControlSurfaceTests.cs` /
`IvaWeldsPartsTreeTests.cs`, using a `NinePServer` + `FakeCommandSink`):

- **Write builds the command**: `echo 50000 > vessels/by-id/<id>/scale` submits
  `SimCommand(<id>, "vessel.scale", NoOrdinal, 50000)`, `Phase == Frame`.
- **Positive-only → EINVAL, no submit**: `0`, `-5`, `nan`, `abc` each fail the write with `EINVAL`
  (the `null`-parse cases never reach the sink; `0`/`-5` are rejected — since rejection here lives in
  the actuator which the SimFs tests don't run, assert the parse-level cases via the control file and
  cover the `> 0` actuator rejection as an in-game validation item, OR add a thin unit around a
  `ScaleActuator`-independent validator if you factor positivity into a game-free helper).
- **Read-back**: a `VesselSnapshot { Scale = 2.5 }` fixture renders `cat scale` → `2.5` (G9), and the
  field appears in the full-snapshot JSON as `"scale"`.

**Supporting**: extend `gatOS.SimFs.Tests/TestData.cs` `Vessel()` with a `Scale` value so read-back
tests have data; `FormatsTests` already covers `Scalar`; add a `SimJsonTests` assertion that `scale`
serializes.

> Positivity note: `0`/negative rejection lives in `ScaleActuator` (game-coupled). To keep it
> unit-testable, optionally extract the check into a tiny game-free static (e.g. in `SimCommand` or a
> `ScaleRules` helper) and have `ScaleActuator.Set` call it — then a `SimFs.Tests` case can assert the
> EINVAL directly. Recommended, low-cost.

---

## 9. Edge cases & risks

- **KittenEva**: parts are scaled by the normal loop *and* the avatar via reflected `Core.Scale`
  (`factor * 0.01f`). Read falls back to the avatar scale only if the vessel has no parts. The
  `GetType().Name == "KittenEva"` gate is a brittle string check → `[KsaAnchor]` Risk **High**.
- **Vessels with no representative part**: `Read` returns `1.0` (never throws) — a truthful best-effort.
- **KSA rebuild reverts scale** (scene reload / staging / undock): expected and accepted (D1) — same as
  `unscience`. The read-back honestly reflects the reverted value.
- **Extreme factors** (100,000+): allowed (no clamp, per requirement). KSA physics/rendering/floating
  origin may misbehave at extremes — **out of gatOS's control**; we do not clamp or guard. Worth a
  one-line caveat in an in-game test note.
- **`Part.Scale` accessibility**: public-settable in the `unscience` decomp; **verify** against gatOS's
  KSA reference DLLs. If not public, use the `ThrottleActuator` reflection-with-degradation pattern.
- **Precision**: normal vessels get full `double` (`Part.Scale` is `double3`); the KittenEva avatar path
  is `float` only (acceptable).

---

## 10. Task checklist (each task ends with `dotnet build` + `dotnet test` green)

1. **`ScaleActuator.cs`** (new, §3.1): `Set` (validate `> 0`, apply), `Apply`/`SetPartScaleRecursive`
   (double), `Read` (best-effort), KittenEva reflection (inline). `[KsaAnchor]` on the KSA-touching
   methods. Verify `Part.Scale` is public-settable in the gatOS KSA DLLs; add reflection fallback if not.
2. **`KsaCatalog.cs`** (§3.2): add the `"vessel.scale"` dispatch row + the authority-gate exemption.
3. **`SimSnapshot.cs`** (§3.3): add `VesselSnapshot.Scale` (init-only, default `1.0`).
4. **`VesselReader.cs`** (§3.4): populate `Scale = ScaleActuator.Read(vehicle)`.
5. **`SimFsTree.cs`** (§3.5): add the `NumberControl` `scale` node in `VesselDir` (with the
   intentional-placement comment).
6. **(Optional) game-free positivity helper** (§8) so the EINVAL path is unit-testable.
7. **`gatOS.SimFs.Tests`** (§8): `VesselScaleTests` + `TestData`/`SimJsonTests` extensions.
8. **`SPEC_9P_FILESYSTEM.md`** (§7): the single `scale` row + intentional-placement note (the only doc
   change).
9. Build the solution (`dotnet build gatos.slnx`) and full suite (`dotnet test gatos.slnx --nologo -v
   quiet`) green; confirm zero warnings (zero-warning policy).
10. **In-game validation** (manual, GameMod is game-coupled): `echo 2 > /sim/vessels/by-id/<id>/scale`
    doubles the model; `echo 50000 >` gives planet-size; `echo 1 >` restores; `echo 0`/`-1` → EINVAL;
    scaling a non-active vessel by id works (D3); `cat scale` reflects the current value; KittenEva
    scales via the avatar path.

---

## 11. Summary of the change surface

| Layer | File | Change |
|---|---|---|
| Actuator (KSA) | `gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs` | **new** — apply + best-effort read + KittenEva |
| Dispatch/authority | `gatOS.GameMod/Game/Ksa/KsaCatalog.cs` | `vessel.scale` row + any-vessel exemption |
| Snapshot | `gatOS.SimFs/Snapshots/SimSnapshot.cs` | `VesselSnapshot.Scale` |
| Reader | `gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs` | populate `Scale` |
| Tree | `gatOS.SimFs/SimFsTree.cs` | `NumberControl` `scale` node |
| Tests | `gatOS.SimFs.Tests/**` | `VesselScaleTests` + fixtures |
| Docs | `SPEC_9P_FILESYSTEM.md` | one `scale` row (the only doc note) |

Untouched by construction: `SimCommand`, `ControlFile`, `CommandQueue`, `Formats`, `SimJson`, all HTTP
/ MQTT code, and (per D6) every other doc/scope/skill file.
