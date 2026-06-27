# Plan — Fix Current Gaps from the KSA 2026.6.9.4750 Update

**Status:** gatOS was built/verified against the **2026.6.8.4680** baseline. The **2026.6.9.4750**
update (2026-06-27, KSA revs 4680→4750) changed three subsystems gatOS binds to. **`gatOS.GameMod` does
not currently compile against 4750** — one confirmed compile break (docking pushoff). This plan closes
every gap the update introduced.

**Method:** the [`scope/` break-check playbook](../scope/FULL_SCOPE.md#0-how-to-use-this-folder-when-a-game-update-lands-the-break-check-playbook)
was run end to end — changelog scan (`…/ksa-game-assemblies/current/version.json`), build against the new
DLLs, decomp diff (old vs new `current/decomp/KSA/*.cs`), and asset/XML diff. Findings are cross-linked to
the scope pages.

## Evidence summary

| Gap | KSA rev | Severity | Detected by | Compiles? | Scope ref |
|---|---|---|---|---|---|
| **G1** Docking pushoff renamed `PushoffForce`→`PushoffImpulse` (N→N·s) | 4683 | **P0 — build red** | build error + decomp + XML | ❌ no | [reads](../scope/ksa-read-surface.md#docking-pushoff--dockingnpushoff_force-compile-break) · [writes](../scope/ksa-write-surface.md#docking) |
| **G2** Power retyped `Joules`→`Watts` (energy/sample → instantaneous W) | 4681 | **P1 — silent value change** | decomp + XML | ✅ yes | [reads](../scope/ksa-read-surface.md#power-productionconsumption--power-solarnproduced-generatornproduced-silent-unit-change) |
| **G3** New `Vehicle.IsControllable` gates control + flight computer | 4699 | **P1/P2 — silent no-op** | decomp + XML | ✅ yes | [writes](../scope/ksa-write-surface.md#cross-cutting-4750-concern-vehicleiscontrollable-rev-4699) |
| **G4** Hygiene: unanchored sampler reads, `Situation` flags, live re-verify of reflection/Harmony, battery type | various | **P3** | review | ✅ yes | [runtime](../scope/ksa-runtime-coupling.md) |

Confirmed **not** affected (no action): staging `SequenceList.ActivateNextSequence` (the rev 4732 rename
is "Resource Groups", not Sequences), Brutal numerics (rev 4729 bump compiled clean), lights / animations
/ decouplers / RCS / engines / flight computer / teleport / refills / bodies / time. See
[`scope/ksa-write-surface.md`](../scope/ksa-write-surface.md#items-confirmed-not-affected-by-4750).

---

## G1 — Docking pushoff (P0, restores the build)

**What changed.** `KSA/DockingPort.cs`: `PushoffForce`→**`PushoffImpulse`** (`float`), `LatchingImpulse`→
**`LatchingKineticEnergy`** (`float`); `Undock → oldVehicle.Split(Connector, PushoffImpulse)`;
`Vehicle.Split(Part.Connector, double splitImpulse, string?)`. The value is now an **impulse (N·s)**, not
a force (N). Asset `Content/Core/CoreCouplingAGameData.xml`: `<PushoffImpulse Ns="7000"/>`,
`<LatchingKineticEnergy J="50"/>` (still numerically 7000, now N·s).

**Build errors:** `DockingActuator.cs:58`, `VesselReader.cs:539` (CS1061 `PushoffForce`).

**Decision — rename the `/sim` leaf, don't just retype it.** The datum's *meaning* changed (force→impulse),
so keeping `pushoff_force` would be actively misleading to guests. Rename `pushoff_force`→`pushoff_impulse`
and `PushoffForceN`→`PushoffImpulseNs` (unit N·s). This is a breaking `/sim` API change, justified by the
game's own breaking rename and by the fact that consumers must re-interpret the value regardless.
*(Minimal-churn alternative if back-compat is required: keep the `pushoff_force` path/field name, just bind
to `PushoffImpulse` and change the documented unit to N·s. Not recommended — name would lie.)*

**Tasks**
1. `gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs:539` — `port.PushoffForce` → `port.PushoffImpulse`;
   rename the snapshot field to `PushoffImpulseNs`. Update the `SampleDocking` `[KsaAnchor]` (line 524):
   `Member` → `.PushoffImpulse`, `Verified="2026-06-27"`, `GameVersion="2026.6.9.4750"`, `Notes` (impulse
   N·s, seeded from `DockingPortTemplate.PushoffImpulse`, stock 7000 N·s).
2. `gatOS.GameMod/Game/Ksa/Actuators/DockingActuator.cs` — `SetPushoffForce` → bind `PushoffImpulse`
   (line 58), rename param `newtons`→`impulse`, validation message "must be >= 0 N·s"; consider renaming
   the method `SetPushoffImpulse`. Update both anchors (lines 24, 47): the `Undock` anchor's
   `Split(Connector, PushoffForce)` → `PushoffImpulse`, the setter anchor `Member`/`Notes`/`Verified`/
   `GameVersion`. (Optionally also expose `LatchingKineticEnergy` as a read — enhancement, not required.)
3. `gatOS.SimFs/Snapshots/VesselSnapshot.cs` — `DockingSnapshot.PushoffForceN` → `PushoffImpulseNs`.
4. `gatOS.SimFs/SimFsTree.cs` — rename the `docking/<n>/pushoff_force` read leaf and the
   `debug/vessels/<id>/docking/<n>/pushoff_force` control leaf → `pushoff_impulse`.
5. `gatOS.SimFs/{Formats.cs,SimJson.cs}` — rename the projected field everywhere it appears (telemetry doc
   + field-level mirror), so all transports stay in parity.
6. `gatOS.GameMod/Game/Ksa/KsaCatalog.cs` — keep `debug.docking_pushoff` (action key can stay); if the
   actuator method was renamed, update the dispatch line. (Action key rename is optional; if changed,
   update SPEC + matrix + serial/SCPI aliases too.)
7. **Docs in lockstep:** `SPEC_9P_FILESYSTEM.md` lines ~336 / ~415 / ~501 (path + unit N→N·s + the action
   row); `docs/KSA_INTEGRATION_MATRIX.md` docking read row + `debug.docking_pushoff` row;
   `scope/ksa-read-surface.md` + `scope/ksa-write-surface.md` docking rows (flip ❌→✅);
   `examples/sdk-ts/` + the `gatos` skill if they name the field.
8. Re-verify the SDK/`sim_openapi.yml` regenerates (HTTP `OpenApi.cs` derives from the tree).

**Done when:** `dotnet build gatos.slnx` is green against 4750; `dotnet test gatos.slnx` green; the
docking read/debug-write round-trips in a live flight (undock applies the impulse; setting it changes
separation energy) — add to `docs/VALIDATION.md`.

---

## G2 — Power/energy now Watts (P1, silent value change)

**What changed.** rev 4681 retyped `SolarPanelState.Produced/Stored`, `GeneratorState.Produced`,
`PowerConsumerState.Consumed` `Joules`→**`Watts`** (and `Battery.MaximumCapacity`/`BatteryState.Charge`
to the strongly-typed `Joules`). gatOS calls `.Value()` on each; both `Watts.Value()` and `Joules.Value()`
return the backing `float`, so **it compiles** — but `power/produced`, `power/consumed`,
`solar/<n>/produced`, `generators/<n>/produced` now emit **instantaneous watts** instead of
energy-accumulated-per-sample. This is the *correct* meaning (the `/sim` fields are named/specced in W;
the XML authors `<Produced W="200"/>`), so 4750 fixes a long-standing naming/semantics mismatch — but the
**values guests see change magnitude**, so it must be documented as a behavior change, and the stale
"Joules per sample" notes corrected.

**Tasks** (mostly docs; no functional code change required — verify, then re-label)
1. Verify no code path scales these by `dt` or accumulates (it doesn't — `VesselReader.SamplePowerProduced`
   `:351` / `SamplePowerConsumed` `:365` / `SampleSolar` `:410` / `SampleGenerators` `:467` sum `.Value()`
   straight through). Confirm `battery/fraction` (ratio) and `battery/capacity` (Joules) unchanged.
2. Re-label the `[KsaAnchor]` `Notes` on those four readers + `SampleBattery` (`:330`): "Joules per
   sample"/"per-sample energy proxy" → "instantaneous watts (W)"; bump `Verified`/`GameVersion`.
3. `SPEC_9P_FILESYSTEM.md` lines ~254–259 / ~295 / ~309: drop "this sample" — these are now genuinely
   instantaneous W. Note the behavior change in the SPEC changelog/notes.
4. `docs/KSA_INTEGRATION_MATRIX.md` power/solar/generator rows: "per-sample energy proxy" → watts.
5. `scope/ksa-read-surface.md`: flip the ⚠️ power/solar/generator/battery rows to ✅ with a "values now W"
   note; update the detail subsection.
6. **Optional enhancement (separate task, not a gap):** surface the new `SolarPanelState.Stored` (Watts)
   and `DistanceToSun`, and per-source `Active`, as new reads — additive, no break.

**Done when:** anchors/SPEC/matrix/scope agree the values are watts; a live flight confirms `power/*`
reads a stable rate that matches the XML-authored panel/generator wattages under load.

---

## G3 — `Vehicle.IsControllable` reporting (P1) + optional gating (P2)

**What changed.** rev 4699 added `Vehicle.IsControllable => _overrideIsControllable ||
Parts.Controls.NumModules > 0` (`KSA/Vehicle.cs:526`); control + flight-computer paths now gate through
`ControlsLockout`. A vessel without a `<Control />` module can't be controlled by the player or the FC.
Capsules now carry `<Control />` (`Content/Core/CoreCommandAGameData.xml`); kittens inherently do. No
compile break, but gatOS commands to an uncontrollable vessel can **silently no-op** (most visibly the
Solver-phase FC setpoints).

**Tasks**
1. **P1 — report it.** Add a `controllable` read: `VesselReader.SampleCore` → new `VesselSnapshot.Controllable`
   field ← `vehicle.IsControllable` (new `[KsaAnchor]`, Low/Medium risk, `KSA/Vehicle.cs`). Project it via
   `SimJson`/`Formats`; add the `vessels/<id>/controllable` leaf in `SimFsTree`; document in SPEC + matrix
   + `scope/ksa-read-surface.md`. Cheap, high value — guests/autopilots can pre-check before commanding.
2. **P2 — decide on gating (evaluate in a live flight first).** Option A (recommended default): **don't**
   broadly gate — let KSA's own lockout drop the command and rely on the new `controllable` read so guests
   know. Option B: in `KsaCatalog.Execute`, for the *flight-control* subset only (throttle, ignite/engine,
   stage, rcs, attitude_*, burn — **not** lights/camera/animations/debug), return `EACCES` when
   `!vehicle.IsControllable`, for a clearer signal than a silent `Ok`. Do **not** gate lights/camera (they
   aren't subject to the control-module lockout). If Option B is taken, document the new `EACCES` trigger
   in SPEC errno notes + `scope/ksa-write-surface.md`.
3. Note in scope/SPEC that `debug.control_vessel` may itself refuse an uncontrollable target in 4750 —
   confirm live and document the resulting outcome (`EBUSY`/`Denied`).

**Done when:** `controllable` reads correctly for a controllable vs. a debris vessel; the chosen gating
behavior is documented and validated.

---

## G4 — Hygiene & live re-verification (P3)

1. **Anchor the sampler's direct reads.** `TelemetrySampler.cs` reads `Universe.GetElapsedSimTime`,
   `SimulationSpeed`, `GetLastSimStep`, `GetSimulationSpeeds`, `IsAutoWarpActive`, `AutoWarpTime`,
   `CurrentSystem`, `Program.ControlledVehicle` with **no `[KsaAnchor]`** (a rename would error in the
   sampler, not at an anchor). Add anchors so the census in `scope/` and the matrix is complete.
2. **`Situation` is a `[Flags]` bitfield** (since rev 4645, pre-baseline) and rev 4704 adds aerostat
   `Landed`. `Vehicle.Situation.ToString()` can be composite. Confirm guest/SDK parsers tolerate
   comma-separated values; note explicitly in SPEC + `scope/ksa-read-surface.md` (already noted).
3. **Live-verify the reflection accessors and Harmony hooks** (build can't catch these — see
   [`scope/ksa-runtime-coupling.md`](../scope/ksa-runtime-coupling.md#reflection-accessors)): `ctl/throttle`
   via `Vehicle._manualControlInputs.EngineThrottle` (confirmed present at `Vehicle.cs:232`); the
   solver-drain prefix on `Universe.ExecuteNextVehicleSolvers`; the menu postfix on
   `Program.DrawProgramMenusHook`. Check `/sim/status/accessors` is clean in a live flight.
4. **Battery type note** — `Battery.MaximumCapacity`/`Charge` are now the `Joules` struct; values
   unchanged. Update the `SampleBattery` anchor `Notes`/`Verified` only.
5. **Scan the Brutal changelog** alongside KSA on future bumps (numerics live in `Brutal.Core.Numerics`,
   used throughout `Game/Ksa/**`).

---

## Execution order & checklist

1. [ ] **G1** docking (unblocks the build) → `dotnet build gatos.slnx` green.
2. [ ] **G2** power re-label (docs + anchors; verify no `dt` scaling).
3. [ ] **G3.1** add `controllable` read; **G3.2** decide gating after a live check.
4. [ ] **G4** anchors + notes; live-verify reflection/Harmony/`status/accessors`.
5. [ ] Bump every touched `[KsaAnchor]` to `Verified="2026-06-27"`, `GameVersion="2026.6.9.4750"`.
6. [ ] Update **all four mirrors** per change: `[KsaAnchor]` · `docs/KSA_INTEGRATION_MATRIX.md` ·
   `SPEC_9P_FILESYSTEM.md` · the matching `scope/` page (flip ❌/⚠️ → ✅).
7. [ ] `dotnet test gatos.slnx` green; run the affected `docs/VALIDATION.md` checklists in a live flight.
8. [ ] Update the matrix header `Verified` date and `scope/ksa-assets-and-versions.md` version table to
   reflect 4750 as the verified baseline.

## Definition of done
- gatOS builds and tests green against `2026.6.9.4750`.
- Docking reads/writes use `PushoffImpulse` (N·s); power reads documented as instantaneous W;
  `controllable` is reported.
- Every `[KsaAnchor]` touched is re-verified to 4750; `scope/`, the matrix, and the SPEC agree with the
  code; `docs/VALIDATION.md` records the live re-check.
