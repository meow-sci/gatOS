---
name: upgrade-ksa
description: >-
  Validate the gatOS mod against a new upstream Kitten Space Agency (KSA) game build — the
  break-check playbook run when the KSA game / its decompiled sources are bumped and you must decide
  whether gatOS needs changes. Covers exactly which gatOS surface couples to KSA and where, how to
  diff the CURRENT (new) vs PREVIOUS (old) decompiled sources, build-as-alarm, semantic-drift review,
  the reflection + render-internals coupling the compiler can't catch, and which docs to update in
  lockstep. Use when asked to "check gatOS against the new KSA build", "upgrade KSA", run the
  version-diff / break-check, or review a game update's impact. REQUIRES two KSA decompiled-source
  trees to be provided: CURRENT and PREVIOUS.
---

# upgrade-ksa — validate gatOS against a new KSA game build

When the upstream KSA game changes, **only a small, bounded part of gatOS can break.** This skill is
the operational procedure to (a) find every place a game update *could* have broken gatOS, (b)
determine whether it *did*, and (c) produce a review that says clearly **whether gatOS needs changes
and where**. It is the executable form of the break-check playbook in
[`scope/FULL_SCOPE.md` §0](../../../scope/FULL_SCOPE.md).

The deliverable is an **impact review**, not a blind edit pass. Only make code changes if this
procedure shows a real break/drift, and only after confirming with the requester.

---

## 0. Required inputs — provide these before starting

This skill is **path-agnostic**. Do not assume or hard-code any machine-specific location. You must
be given (ask for whatever is missing before doing anything else):

| Input | What it is | Referred to below as |
|---|---|---|
| **CURRENT** KSA sources | The **new** game build being validated (the one gatOS is being upgraded *to*). | `<CURRENT>` |
| **PREVIOUS** KSA sources | The **prior verified baseline** gatOS is known-good against (upgrading *from*). | `<PREVIOUS>` |

Each provided tree is a **game-assemblies checkout** with this internal layout (note the confusing
inner `current/` subfolder — it is part of every checkout regardless of version, see
[`scope/ksa-assets-and-versions.md`](../../../scope/ksa-assets-and-versions.md)):

```
<CURRENT>/current/
  version.json      build id + date + FULL per-revision commit log (the changelog)   → step 1
  dll/              reference assemblies gatOS compiles against                       → step 2
  decomp/           decompiled C# sources (KSA/*.cs, Brutal*/, Planet*/)              → step 3
  Content/          game-data XML (part templates that seed units/values)             → step 3
```

**Confirm both trees resolve** (each has a `current/version.json`, `current/dll/`, `current/decomp/`)
and read the two `version.json` build ids back to the requester before proceeding. If only one tree
is available you can still do steps 2 + 5 (build-as-alarm and runtime/render re-verification) but you
**cannot** do the semantic-drift diff (steps 1, 3) — say so explicitly rather than skipping silently.

> If the requester gives you paths that differ from this layout (e.g. a bare `decomp/` with no
> `current/` wrapper, or a `dll/` folder directly), adapt — the four artifacts (`version.json`,
> `dll/`, `decomp/`, `Content/`) are what matter, not the exact nesting. Ask if the shape is unclear.

---

## 1. The one fact that bounds the whole task

gatOS takes a KSA dependency in **exactly one project** (`gatOS.GameMod`), and within it every
game-state touch is confined to **`gatOS.GameMod/Game/Ksa/**`** plus **two Harmony hook targets** in
`Game/Mod.Game.cs`. Two immutable records — `SimSnapshot` (reads) and `SimCommand` (writes) — are the
firewall; KSA types never cross them. Everything downstream (the 9p/HTTP/MQTT/serial transports, the
`/sim` tree, SimFs, the VM/SSH/guest stack) is **game-free and cannot be broken by a KSA update.**

This is *the binding rule* — see "THE dependency rule" and its "Stronger form for KSA integration
(G2)" in [`CLAUDE.md`](../../../CLAUDE.md). It is why the blast radius is small and discoverable:

- Every KSA member gatOS binds to non-reflectively carries a **`[KsaAnchor]`** attribute
  (`Member`, `SourceFile`, `Verified`, `GameVersion`, `Risk`, `Notes`) — defined in
  [`gatOS.GameMod/Game/Ksa/KsaAnchor.cs`](../../../gatOS.GameMod/Game/Ksa/KsaAnchor.cs). The anchors
  are the **source of truth**; the human mirrors are `scope/` and `docs/KSA_INTEGRATION_MATRIX.md`.
- So the review is a **grep + a build + a diff**, targeted at a known file set — not an open-ended
  audit. Do **not** spend effort reviewing the game-free surface (see §7 "Out of scope").

**Read [`scope/FULL_SCOPE.md`](../../../scope/FULL_SCOPE.md) first** — its §3 is the complete coupling
census (every file a KSA update can touch) and §0 is the playbook this skill operationalizes.

---

## 2. Procedure

Run these in order. Steps 2–5 each surface a different class of breakage; none alone is sufficient
(a member can keep its signature and change meaning; reflection and render bindings never fail to
compile). Record findings as you go for the §6 report.

### Step 1 — Changelog scan (what to even look at)

Read the commit log in **both** trees and diff the intent:

- `<CURRENT>/current/version.json` — `commits[]`, each with `rev`, `date`, `author`, `lines[]`.
- `<PREVIOUS>/current/version.json` — the same, for the prior build.

The CURRENT log is a superset that continues past the PREVIOUS build's last revision; **the new
revisions are the delta.** Flag any commit line touching a subsystem gatOS couples to. The
authoritative subsystem→file map is the table in
[`scope/ksa-assets-and-versions.md` "Subsystem → decomp/asset quick map"](../../../scope/ksa-assets-and-versions.md).
High-signal keywords: `Vehicle`, `Orbit`/`PatchedConic`/`Encounter`, `Celestial`/`StellarBody`/`CelestialSystem`/atmosphere/ocean, `Universe`/warp/time/solver, `EngineController`, `Tank`/`Mole`, electrical (`Battery`/`SolarPanel`/`Generator`/`Joules`/`Watts`), `DockingPort`/`InputEvents`, `FlightComputer`/`BurnTarget`/`NavBall`, `SequenceList`/staging, `LightModule`/`Light`, `ThrusterController`/RCS, `Decoupler`, animations/`SolarTracker`, `Program`/`Camera`/menu, `GameAudio`/FMOD, and — **highest priority** — render internals (`SuperMeshRenderSystem`, `Program.OffScreenPass`/render pass, Vulkan/`Planet.Render.Core`, `UnlitMesh` shaders, `Part` ego transforms).

> Render internals churn faster than the gameplay APIs **and are not reliably changelog-covered** —
> a clean changelog does **not** clear the `thug_life` quad. See step 5.

### Step 2 — Build against CURRENT = the alarm system

Point the build at the **new** DLLs and compile `gatOS.GameMod`. This is path-agnostic via the
`KSA_DLL_DIR` env var (resolution order documented in
[`Directory.Build.props`](../../../Directory.Build.props); env var wins):

```bash
# PowerShell:  $env:KSA_DLL_DIR = '<CURRENT>/current/dll'
# bash:        export KSA_DLL_DIR='<CURRENT>/current/dll'
dotnet build gatOS.GameMod
```

If the KSA game may be running (it holds a deploy lock on the mod folder), redirect the deploy so the
build doesn't fail on the copy step rather than the compile — set `GATOS_DIST_DIR` to a throwaway dir:

```bash
# bash:  GATOS_DIST_DIR=$(mktemp -d) KSA_DLL_DIR='<CURRENT>/current/dll' dotnet build gatOS.GameMod
```

**Every compile error is a renamed/removed/retyped member gatOS binds to. That error list *is* the
work list.** Each error lands at a `[KsaAnchor]` site under `Game/Ksa/**` (or a Harmony target in
`Game/Mod.Game.cs`). Also build the full solution once to confirm the game-free projects are
unaffected (they must be, by the dependency rule — if one breaks, something leaked a KSA type across
the seam and that is itself a finding).

> A **green build does NOT mean gatOS is safe.** It only clears the non-reflective, compile-visible
> bindings. Steps 3–5 exist precisely for what the compiler cannot see.

### Step 3 — Decomp + asset diff for silent semantic drift

The dangerous changes compile clean: a member keeps its name/signature but changes **meaning** —
units, frame of reference, what a value represents, or a new gating precondition. For every subsystem
flagged in step 1, open the matching decomp file in **both** trees and compare:

```
<CURRENT>/current/decomp/KSA/<File>.cs      vs      <PREVIOUS>/current/decomp/KSA/<File>.cs
```

The `<File>` to open is named by each accessor's **`[KsaAnchor].SourceFile`** (e.g. `KSA/DockingPort.cs`)
and by the subsystem map in
[`scope/ksa-assets-and-versions.md`](../../../scope/ksa-assets-and-versions.md). Look specifically for:

- **Renamed members** the compiler *did* catch (cross-check step 2) vs ones reached by reflection (step 4).
- **Changed field types** — the classic being a unit/quantity swap (e.g. `Joules`→`Watts`, an energy
  reference becoming an instantaneous-power reference). These are the highest-value catch of this step.
- **Changed method signatures / added parameters** on actuator entry points.
- **New gating** — a new precondition (e.g. a control-module requirement for controllability) that
  changes when a read/write is valid.
- **Frame/numeric convention changes** in `Brutal.Core.Numerics` (double3/doubleQuat) or the coordinate
  frames — these silently corrupt every derived value. See
  [`scope/ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md#frames-and-numerics) and
  [`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../../../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md).

For **units/values**, diff the Content XML too — attribute names make unit changes obvious (e.g.
`Ns=`/`J=`/`W=`). The XML-backed integration points are tabulated in
[`scope/ksa-assets-and-versions.md` "Asset XML that backs gatOS integration points"](../../../scope/ksa-assets-and-versions.md).

> **Decomp can lag the shipping binary.** If a read returns null / a count is `-1` / reflection misses
> but the decomp looks fine, the DLL is authoritative — use KSA's runtime reflection-dump strategy
> (ksa skill [`debug.md`](../ksa/debug.md)) to discover the real structure.

### Step 4 — Reflection accessors (the compiler is blind here)

A handful of bindings use **reflection** and therefore **cannot fail to compile** — a rename/move in
KSA turns them into a silent runtime miss, not a build error. These are rated **High** risk and must
be re-verified even when steps 2–3 are clean. They are enumerated in
[`scope/ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md#reflection-accessors) (e.g.
the manual throttle field on `Vehicle`, the light-template clone path). For each: diff the decomp of
the type it reflects into, and note that the **runtime** safety net is the per-accessor try/catch in
`KsaCatalog`/[`KsaHealth`](../../../gatOS.GameMod/Game/Ksa/KsaHealth.cs) — a throwing accessor latches
*degraded* (`EOPNOTSUPP`) and surfaces at **`/sim/status/accessors`** rather than crashing the mod.
Part of the review is confirming those latches still cover the reflected members, and flagging any for
in-game re-check via `/sim/status/accessors`.

### Step 5 — Render internals (`thug_life`) — deepest, highest-churn coupling

The `thug_life` world-space quad (`Game/Ksa/ThugLife/**`) is gatOS's only custom GPU rendering and its
deepest reach into KSA's render pipeline (Vulkan / `Planet.Render.Core` / `SuperMeshRenderSystem` /
`Program.OffScreenPass` / `UnlitMesh` shaders / `Part` ego transforms). Render internals churn faster
than gameplay APIs and are **not** reliably changelog-covered, so **re-verify this set on every update
regardless of a clean changelog.** The baked-in pipeline assumptions (shader keys, texture format,
reverse-Z depth, the render-pass/MSAA sample count, the `SuperMeshRenderSystem.RenderMainPass` postfix
target) are listed in
[`scope/ksa-assets-and-versions.md` "Render-internals references"](../../../scope/ksa-assets-and-versions.md#render-refs).
The runtime safety net: any GPU fault self-disables the feature (`Active=false`) rather than crashing —
but a *silently mis-drawn* quad is exactly what only a **live in-game check** catches.

### Step 6 — Fix, re-anchor, re-document (only for real findings)

For each confirmed break/drift, and **only** after confirming the fix approach with the requester:

1. Relocate the moved API / fix the accessor in the specific `Game/Ksa/**` file.
2. Update its **`[KsaAnchor]`** — `Member`, `Verified` (today's date), `GameVersion` (the CURRENT
   build id), and `Notes` if semantics moved.
3. Update **all** human views in the **same** change (the lockstep mandate in
   [`CLAUDE.md`](../../../CLAUDE.md) — "Instruction Maintenance Mandate" + the `/sim` API constitution):
   - the matching [`scope/`](../../../scope/FULL_SCOPE.md) page(s) and the row's **game-version status**
     ([read](../../../scope/ksa-read-surface.md) / [write](../../../scope/ksa-write-surface.md) /
     [runtime](../../../scope/ksa-runtime-coupling.md) / [assets](../../../scope/ksa-assets-and-versions.md));
   - [`docs/KSA_INTEGRATION_MATRIX.md`](../../../docs/KSA_INTEGRATION_MATRIX.md) (the anchor mirror);
   - [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md) **iff** the `/sim` surface, a unit, a
     format, a phase, or a semantic actually moved (its own binding constitution — see `CLAUDE.md`);
   - the affected checklist in [`docs/VALIDATION.md`](../../../docs/VALIDATION.md) (mark it for a live re-run).
4. Rebuild + run the full test suite green (`dotnet test gatos.slnx --nologo -v quiet`). Note that
   in-flight behavior still needs a live KSA session — the automated suite cannot cover it.

Keep the `[KsaAnchor]`s and the `scope/`/matrix mirrors from ever disagreeing — they are the same
lockstep discipline the `/sim` SPEC imposes.

---

## 3. The surface area to analyze (map every KSA touch to its review target)

This is the exhaustive list of what a KSA update can touch — use it as the checklist so nothing is
missed and nothing off-list is over-reviewed. Full per-member detail lives in the linked `scope/`
pages; the anchors themselves are in the `Game/Ksa/**` files.

| gatOS site (`gatOS.GameMod/…`) | KSA coupling | Review page |
|---|---|---|
| [`Game/Ksa/Readers/VesselReader.cs`](../../../gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs) | bulk of telemetry (~21 anchored reads: flight/orbit/mass/engines/tanks/power/RCS/solar/lights/docking/decouplers/navball/environment/encounters + `controllable`) | [`ksa-read-surface.md`](../../../scope/ksa-read-surface.md) |
| [`Game/Ksa/Readers/BodyReader.cs`](../../../gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs) | celestial catalog (orbits/atmosphere/ocean/frames) | [`ksa-read-surface.md`](../../../scope/ksa-read-surface.md) |
| [`Game/Ksa/Readers/AnimationLinks.cs`](../../../gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs) | animation↔module structural links | [`ksa-read-surface.md`](../../../scope/ksa-read-surface.md) |
| [`Game/Ksa/Readers/PartsReader.cs`](../../../gatOS.GameMod/Game/Ksa/Readers/PartsReader.cs) | parts + nested subparts list (welds anchor picker) | [`ksa-read-surface.md`](../../../scope/ksa-read-surface.md#parts) |
| [`Game/TelemetrySampler.cs`](../../../gatOS.GameMod/Game/TelemetrySampler.cs) | sampler-direct reads: `Universe.*` (time/warp/system), `VersionInfo.Current` | [`ksa-read-surface.md`](../../../scope/ksa-read-surface.md#sampler-direct-reads) |
| [`Game/Ksa/Actuators/*.cs`](../../../gatOS.GameMod/Game/Ksa/Actuators) (13 anchored) | all controls + debug writes: throttle/engine/staging/RCS/attitude/burn/lights/decoupler/docking/camera/teleport/refill/warp/scale/**audio (FMOD)** | [`ksa-write-surface.md`](../../../scope/ksa-write-surface.md) |
| [`Game/Ksa/Render/IvaForceRender.cs`](../../../gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs) | `always_render_iva` cheat (dynamic `gatos.iva` Harmony) | [`ksa-write-surface.md`](../../../scope/ksa-write-surface.md#welds) |
| [`Game/Ksa/Render/VesselForceRender.cs`](../../../gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs) | per-vessel `always_render` (dynamic `gatos.always_render` prefixes on `Vehicle.GetWorldMatrix`/`UpdateRenderData`) | [`ksa-write-surface.md`](../../../scope/ksa-write-surface.md#per-vessel-nodes) |
| [`Game/Ksa/Welds/*.cs`](../../../gatOS.GameMod/Game/Ksa/Welds) | per-frame `Teleport` weld driver + registry | [`ksa-write-surface.md`](../../../scope/ksa-write-surface.md#welds) |
| [`Game/Ksa/ThugLife/*.cs`](../../../gatOS.GameMod/Game/Ksa/ThugLife) | **⚠ highest-churn**: Vulkan GPU build + `SuperMeshRenderSystem.RenderMainPass` postfix + `Part` ego math | [`ksa-write-surface.md`](../../../scope/ksa-write-surface.md#thug-life), [render refs](../../../scope/ksa-assets-and-versions.md#render-refs) |
| [`Game/Ksa/FrameCapture.cs`](../../../gatOS.GameMod/Game/Ksa/FrameCapture.cs) + [`DisplayRenderPatch.cs`](../../../gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs) | `/sim/display` screen-stream render-hook capture (GPU readback) | [`ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md) |
| [`Game/Ksa/KsaCatalog.cs`](../../../gatOS.GameMod/Game/Ksa/KsaCatalog.cs) | vehicle/astronomical resolution + the try/catch degrade latches | [`ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md) |
| [`Game/Mod.Game.cs`](../../../gatOS.GameMod/Game/Mod.Game.cs) | **Harmony hook targets** (un-anchored): `Universe.ExecuteNextVehicleSolvers` (solver-drain prefix), `Program.DrawProgramMenusHook` (menu postfix); `Program.MainViewport`, `ModLibrary.Find` | [`ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md) |
| [`Game/BrutalModLogger.cs`](../../../gatOS.GameMod/Game/BrutalModLogger.cs) | `Brutal.Logging` sink | [`ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md) |
| Frames & numerics (used throughout) | `Brutal.Core.Numerics` (double3/doubleQuat), CCI/CCE/CCF/ECL conventions | [`ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md#frames-and-numerics) |

The two Harmony hook targets in `Game/Mod.Game.cs` are the **only un-anchored KSA touch-points** —
they can't carry a `[KsaAnchor]` on a patched external method, so **check them by hand every time**: a
renamed hook target won't fail the `gatOS.GameMod` compile (Harmony resolves by string via
`AccessTools`), it fails at patch-install time and disables that feature. Confirm the target
signatures against the CURRENT decomp of `KSA/Universe.cs` and `KSA/Program.cs`.

---

## 4. Risk tiers (re-verification priority)

Mirrors `ChurnRisk` in [`KsaAnchor.cs`](../../../gatOS.GameMod/Game/Ksa/KsaAnchor.cs) and §4 of
[`FULL_SCOPE.md`](../../../scope/FULL_SCOPE.md):

- **Low** — core vehicle/orbit/time/body state + the struct-of-arrays module pattern. Compile catches most.
- **Medium** — FlightComputer, InputEvents-mediated ops, NavBall, per-module controllers, docking.
- **High** — template internals and **anything reached by reflection or via the render pipeline**.
  The compiler is blind here; re-verify at runtime/in-flight even on a green build (steps 4 + 5).

Prioritize the review by risk: a green build clears most Low/Medium; the High set (reflection +
`thug_life` render + the manual-throttle/light-template reflection paths) is where a silent regression
hides and where a **live in-game pass** is mandatory.

---

## 5. When you must go beyond the sources

Two things this static review **cannot** fully settle, and the honest answer is to flag them for a
live pass rather than assert they pass:

- **Semantic drift the decomp doesn't reveal** (decomp lags the binary): use KSA's runtime
  reflection-dump approach — ksa skill [`debug.md`](../ksa/debug.md).
- **Render correctness** (the `thug_life` quad, the `/sim/display` capture): only a live KSA flight
  confirms it draws correctly. The `thug_life` render internals are documented in the ksa skill
  [`quad.md`](../ksa/quad.md).

The in-game checklists live in [`docs/VALIDATION.md`](../../../docs/VALIDATION.md); the runtime health
of every anchored accessor is observable in-guest at `/sim/status/accessors`.

---

## 6. Deliverable — the impact review

Produce a concise report, structured so the requester can act on it:

1. **Builds validated** — CURRENT build id (from `version.json`), PREVIOUS build id, and the
   `dotnet build gatOS.GameMod` result against CURRENT (clean / the exact compile-error → `[KsaAnchor]`
   work list).
2. **Changelog delta** — the new revisions between PREVIOUS and CURRENT, filtered to gatOS-relevant
   subsystems (cite `rev`s).
3. **Findings**, each as: the gatOS site (`Game/Ksa/…` + anchor), the KSA member, the change class
   (renamed / retyped / signature / **semantic-drift** / new-gating / reflection-miss / render), the
   decomp/XML evidence (both trees), and the **impact** (does a `/sim` value/behavior change? which
   transports/paths? errno?).
4. **Verdict per finding** — `no change needed` / `code change required` / `needs live in-game
   re-verification`. Be explicit about what the static review could *not* determine (reflection +
   render → live pass).
5. **If changes are required** — the specific files to touch and the full lockstep doc-update set (§2
   step 6). Do not edit until the approach is confirmed.

If everything is clean: say so plainly, list what was checked (the full §3 surface + the High-risk
reflection/render set), and name the residual items that still require a **live** pass
(`docs/VALIDATION.md`) before declaring the upgrade fully validated — a green build alone is not that.

---

## 7. Out of scope (do not review these for KSA breakage)

By the dependency rule these are **game-free** and cannot be broken by a KSA update — inventoried in
[`scope/non-ksa-surface.md`](../../../scope/non-ksa-surface.md). Don't spend the review here:

- The 9p server + VFS, `/sim` tree, SimFs, snapshot/command model, telemetry gating.
- HTTP `/v1`, MQTT, serial/bus transports, the TypeScript SDK.
- VM/QEMU lifecycle, disks, ports, the guest image, SSH.
- The purrTTY contract / StarMap loader / ModMenu ABIs — these are **mod-ecosystem** dependencies, not
  the KSA *game*. A KSA game update does not move them (a StarMap or purrTTY release is a separate
  event); noted in [`ksa-runtime-coupling.md`](../../../scope/ksa-runtime-coupling.md#mod-ecosystem-abis).

The one caveat: if a game-free project **fails to build** against the CURRENT DLLs, a KSA type has
leaked across the `SimSnapshot`/`SimCommand` seam — that is a dependency-rule violation and a finding
in its own right, not an out-of-scope item.
