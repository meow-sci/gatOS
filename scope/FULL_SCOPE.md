# gatOS — Full Scope & Game-Integration Map

> **What this folder is.** `scope/` is the durable, structured catalog of **every gatOS feature and
> exactly how it touches the Kitten Space Agency (KSA) game** — the document you read *first* when a
> KSA update lands to answer one question: **"will this game update break gatOS, and where?"**
>
> It is deliberately separate from the other docs because it has a different job:
> - [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md) is the **API catalog** (every `/sim` path, format, unit) — the user-facing contract.
> - [`docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md) is the **at-a-glance anchor mirror** (one row per `[KsaAnchor]`).
> - **`scope/` is the break-impact view**: for each feature, the precise gatOS code site, the KSA
>   member it binds to, the **decompiled-source file and asset/XML path** that backs it, the failure
>   mode if KSA moves it, and how that failure surfaces. The `[KsaAnchor]` attributes in
>   `gatOS.GameMod/Game/Ksa/**` remain the **source of truth**; this folder is the human, cross-referenced,
>   "what depends on what" companion.

**Maintenance is mandatory.** Any change to a gatOS feature or its KSA binding MUST update the relevant
`scope/` page in the same work item — see the *Instruction Maintenance Mandate* in
[`AGENTS.md`](../AGENTS.md). Keeping `scope/` stale defeats its entire purpose.

---

## 0. How to use this folder when a game update lands (the break-check playbook)

This is the operational heart of `scope/`. gatOS only takes a KSA dependency in **one project**
(`gatOS.GameMod`) and that dependency is funneled through `[KsaAnchor]`-marked accessors, so a game
update's blast radius is small and discoverable. The procedure:

1. **Read the changelog first.** Each game-assemblies checkout ships a `current/version.json` with the
   full per-revision commit log of that build. Diff is trivial:
   - New / current build: `…/ksa-game-assemblies/current/version.json`
   - Previous build: `…/ksa-game-assemblies_<old-version>/current/version.json`
   Scan the commit messages for anything touching a subsystem listed in this folder's inventory
   (electrical, docking, flight computer, staging/sequences, parts/modules, numerics/Brutal, Situation,
   Vehicle control, **and render internals** — `SuperMeshRenderSystem`, `Program.OffScreenPass`/the render
   pass, the Vulkan/`Planet.Render.Core` surface, or the `UnlitMesh` shaders, which back the `thug_life`
   quad and are gatOS's **highest-churn** coupling; render internals churn faster than the gameplay APIs
   and are not as reliably changelog-covered, so re-verify the `thug_life` quad in a live flight on any
   update — see [`ksa-assets-and-versions.md#render-refs`](ksa-assets-and-versions.md#render-refs)). See
   [`ksa-assets-and-versions.md`](ksa-assets-and-versions.md) for how versions and the decomp/dll/Content
   layout are organized.

2. **Build against the new assemblies — this is the alarm system.** gatOS resolves the KSA reference
   DLLs through `KSAFolder` (default: the sibling `../ksa-game-assemblies/current/dll/`), so a plain
   build compiles `Game/Ksa/**` against whatever is checked out:
   ```bash
   dotnet build gatOS.GameMod      # redirect deploy if KSA is running: GATOS_DIST_DIR=<tmp> dotnet build gatOS.GameMod
   ```
   Any **renamed/removed/retyped member gatOS binds to non-reflectively becomes a compile error at the
   exact `[KsaAnchor]` site.** That error list *is* the work list. (Reflection-based accessors —
   throttle field, light-template clone — can't fail at compile time; check them at runtime via
   `/sim/status/accessors`. They are flagged `High` risk and enumerated in
   [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md).)

   > **⚠️ The first error list is NOT the work list — iterate to green.** Roslyn reports
   > **declaration-phase** errors (types in method signatures, field/property types) *before* it binds
   > any method body, and **skips body binding entirely while any declaration error is outstanding**.
   > So a single bad parameter type can mask every other break in the project. The 5261 pass hit this
   > exactly: the first build reported **one** error (a `SimTime` parameter on `VesselReader.TimeUntil`)
   > and hid **nine** more that only appeared after it was fixed. **Fix, rebuild, repeat until the build
   > is green** — only then is the error list complete.

3. **Diff the decompiled source for silent semantic drift.** A member can keep its name and signature
   but change *meaning* (units, frame, what a value represents) — these compile clean and are the
   dangerous ones. For every changelog hit, open the matching decomp file in **both** trees and compare:
   ```
   <new>/current/decomp/KSA/<File>.cs   vs   <old>/current/decomp/KSA/<File>.cs
   ```
   The `[KsaAnchor].SourceFile` of each accessor (e.g. `KSA/DockingPort.cs`) names the file to open.

4. **Fix, re-anchor, re-document.** For each break/drift: relocate the API, fix the accessor, update its
   `[KsaAnchor]` (`Member`, `Verified`, `GameVersion`, `Notes`), then update **all four** human views in
   the same commit: the matching `scope/` page, [`docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md),
   [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md) (if the `/sim` surface/units/semantics moved), and
   [`docs/VALIDATION.md`](../docs/VALIDATION.md) (re-run the affected checklist in a live flight).

5. **Runtime drift without a compile break is caught by the health latches.** Every accessor runs under
   a try/catch in `KsaCatalog`/`KsaHealth`; a throwing read/write latches that accessor *degraded*
   (`EOPNOTSUPP`), logs once, and shows up in `/sim/status/accessors`. The guest sees a failed sensor,
   not a crashed mod. This is the safety net for the things steps 2–3 miss.

> **Current applied result of this playbook:** the **2026.8.3.5117 → 2026.8.5.5168** update was run
> through it on 2026-08-05 — **four compile breaks, all fixed; three silent semantic breaks found and
> closed.** PREVIOUS was itself an audited baseline and CURRENT's `fromRevision` is 5117, so the two
> side-by-side trees chained with no gap (revs 5118–5168, 49 commits) and no git-history fallback was
> needed.
>
> **All four compile breaks trace to one revision — rev 5154**, which moved offscreen rendering off
> `VkRenderPass`/framebuffers onto **Vulkan dynamic rendering** and deleted `Program.OffScreenPass`,
> `KSA.OffscreenTarget`, `KSA.RenderTarget`, `KSA.Framebuffer`, `Core.RenderPassState` and
> `Core.DynamicRenderState`. `FxReflect`'s cloud worley-noise handle was retyped
> (`RenderTarget` → `RenderImage`), `FrameCapture` had to null-guard the now-nullable
> `RenderTarget.ColorImage`, and `ThugLifeQuadRenderer.BuildPipeline` was migrated to
> `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` — the same call KSA's own renderers make,
> which also means the quad now **tracks** the engine's sample count (including the new CMAA2 option,
> rev 5156) rather than hard-binding one. ⚠️ **Name-collision trap:** the *new*
> `KSA.Rendering.RenderTarget` is an unrelated class reusing the deleted one's name — never re-bind by
> name alone. The render **preconditions** were re-verified rather than assumed: the main viewport's
> offscreen target is still literally `Program._offscreenTarget`, the offscreen colour still ends the
> frame in `SampledReadVfc` before the final composite and `End()`, and CMAA2 renders into its own
> target — so the display transpiler and capture both still hold. **The sibling purrTTY mod took the
> same break** (an unpatched purrTTY hard-crashes KSA at the first frame with
> `TypeLoadException: KSA.OffscreenTarget`) and was migrated in the same work item.
>
> **Three silent semantic breaks — each one "a gatOS write now does nothing, with no read to explain
> it":** (1) **rev 5143** made `FlightComputer.RCSMode` a hard cut-off for *manual* RCS, so
> `ctl/translate`/`ctl/rotate` do nothing while RCS is off — **this falsified an explicit claim in
> `ksa-write-surface.md`** and inverted a `VALIDATION.md` checklist item; closed by exposing
> `ctl/rcs_mode` (read + Solver-phase write). (2) **rev 5128** added `Vehicle.ClearHeldPlayerInput()`,
> which clears the latched thruster flags on vessel switch, window focus loss, camera-mode switch and —
> *every update* — while ImGui holds keyboard focus or warp exceeds 30×, so the "latches until
> rewritten" contract is now conditional (documented; throttle/ignite unaffected). (3) **rev 5132** let
> players disable a decoupler module and gated `SetIsActive` on it, making `decoupler.fire` a silent
> no-op **reported as success**; closed with `EOPNOTSUPP` + a new `decouplers/<n>/enabled` read.
>
> **Inherited value drift, no API change:** encounter population widened again (rev 5141 — near-coplanar
> SOI encounters like Hohmann-to-Luna are now predicted), RCS thrust reduced (rev 5119), size-D/E SRB
> grain sizes corrected (rev 5124), part mass/moment-of-inertia computation fixed plus a tangent-ogive
> mass type (rev 5166). **Verified clean:** all 13 reflection accessors and all 7 Harmony targets,
> including the `KittenEva` scale chain despite heavy kitten-locomotion churn. Build + full test suite
> green against 5168 (0 warnings; 772 passed / 11 skipped).
> ([read 5168 findings](ksa-read-surface.md#5168-findings) /
> [write 5168 findings](ksa-write-surface.md#5168-findings) /
> [pass record](ksa-assets-and-versions.md#5168-pass)). Live re-check items are queued in
> `../docs/VALIDATION.md`. **5168 is now the verified baseline.**
>
> The prior **2026.7.9.5018 → 2026.8.3.5117** update was run
> through it on 2026-08-01 — **two compile breaks, both fixed; two silent semantic drifts documented;
> nothing else needed.** The pass deliberately spanned the intermediate `2026.7.10.5056` drop (revs
> 5019–5116, 97 commits), because 5056 had only ever been build-checked — its changelog/decomp/Content
> diff was never run, so 5018 was the last fully-audited baseline and a 5056→5117 diff would have
> silently skipped 37 revisions. **Method note:** the `_prev` sibling checkout was *not* the right
> baseline here; the main checkout is a git repo holding every prior drop, so
> `git diff 3106557 HEAD` (5018 → HEAD) gave the true window. Prefer that whenever `_prev` is not
> itself an audited baseline.
>
> **Break 1 — `NavBallData.DeltaVInVacuum` → `DeltaV` (rev 5114)**, `VesselReader.SampleNavball` the
> single call site. The rename was only the visible half: the same revision changed **both** navball
> performance values' *meaning*. `navball/deltav` is now the **active staging sequence's**
> propellant-aware Δv (`Parts.PerformanceSequences.FindActiveSequenceDeltaV()`) instead of the
> whole-stack vacuum rocket equation; `navball/twr`'s numerator moved from `TotalEngineVacuumThrust` to
> `ComputeActiveThrust(AtmosphericPressure)`, making it **atmosphere-corrected** and excluding engines
> that cannot produce thrust. TWR **compiles clean** — a textbook example of why step 3 exists.
> Per maintainer decision the fix was **binding-only**: no `SimSnapshot` field rename and no SPEC
> rewording, so `NavballSnapshot.DeltaVVacuumMs` and SPEC §3.4.3's "vacuum Δv" now overstate provenance.
>
> **Break 2 — `VolumetricTrailRenderer.ExpansionTimeSeconds` removed (revs 5059/5097)**, when the
> volumetric-trail subsystem was split into `PlumeSegmentStore`/`PlumeSegmentMaintenance`/
> `PlumeTimingProfile`/`PlumeTrailSettings`/`PlumeTrailUploadBuilder`/`PlumeTrailEmitterTracker`. The
> field moved onto the new `PlumeTrailSettings` with the **same default (`5f`) and meaning**. Because the
> game's own Plume Trails debug window still exposes it — `VolumetricTrailRenderer.OnDrawUi` now delegates
> its "Profile" section to `PlumeTrailSegmentsManager.OnDrawProfileUi()`, which draws
> `_settings.ExpansionTimeSeconds` — the `/sim` node was **re-bound rather than dropped**, via a new
> two-hop `FxReflect.TrailSettings` accessor carrying its own `fx.trail_settings` health latch (so a
> future move degrades `render/expansion_time` alone). No SPEC change. *Standing rule this applied:
> gatOS exposes what the built-in debug windows expose, reached the way they reach it.*
>
> **Semantic drift, no code change:** substance phase **names** (rev 5095 — a new `DefaultPhase` XML
> attribute makes the default phase render bare, so `tanks/<n>/substance` now reports `"Kerosene"` not
> `"Liquid Kerosene"` and `srb/<n>/substance` `"APCP"` not `"Solid APCP"`, while gas-default substances
> keep `"Liquid O2"`/`"Liquid H2"`; audited — no example, tutorial or SDK code string-matches these);
> **encounter population** (revs 5106/5110 — final-trajectory-only plus an excessive-entry fix; the
> `Encounter` struct is unchanged and gained `TaMainOrbit`); **docking identity** (rev 5076 — larger
> vehicles now absorb smaller ones, so which `/sim/vessels/<id>` survives a dock can differ).
>
> **Verified clean:** all eight Harmony hook targets and every reflection accessor (re-checked
> member-by-member, since neither can fail at compile time); the whole `thug_life` pipeline —
> `SuperMeshRenderSystem.cs` is **byte-identical** across the window, and the alpha-to-coverage rework of
> revs 5057/5058 does not reach it because A2C is a transient attachment that is *not* a member of the
> offscreen render pass; the terrain UBO writes (`PlanetRenderer`'s UBO plumbing unchanged, and gatOS
> derives offsets from the public strides); the audio actuator (`GameAudio.System`/`GetChannelGroup`
> intact through 249 lines of churn); and the new `Part` matrix cache (rev 5112 — safe, because the
> `PositionParentAsmb`/`Asmb2ParentAsmb`/`Scale` setters gatOS writes through all call
> `ResetCachedPosMatrixValues()`). Build + full test suite green against 5117 (0 warnings; 769 passed /
> 11 skipped). ([read 5117 findings](ksa-read-surface.md#5117-findings) /
> [write 5117 findings](ksa-write-surface.md#5117-findings)). Live re-check items are queued in
> `../docs/VALIDATION.md`. ~~5117 is now the verified baseline.~~ *(superseded by 5168 above)*
>
> The prior **2026.7.8.4980 → 2026.7.9.5018** update was run
> through it on 2026-07-24 — **one compile break, fixed; one coverage gap opened; nothing else needed.**
> Rev 4992 (solid rocket motors) generalized propellant storage from liquid-only to a new
> `ISubstanceStore` (`Liquid | Solid`) abstraction, renaming `Mole.GetLiquidMass` → `GetStoredMass`
> (also `GetLiquidVolume` → `GetStoredVolume`, `Consume`/`ProduceLiquid` → `…Stored`, `ContainsLiquid`
> deleted). `VesselReader.SampleTanks` was the one call site — **values unchanged**, since `Tank` moles
> are liquids. Build + full test suite green against 5018 (0 warnings; 681 passed / 11 skipped); every
> other bound member, reflection accessor, and Harmony hook target verified unchanged by a full decomp +
> Content diff of the two side-by-side checkouts (changelog gapless — revs 4981–5018). **The coverage
> gap it opened — closed in the same work item:** SRB solid propellant lives on the new
> **`SolidGrainSegment`** module, an `ISubstanceStore` but **not a `Tank`**, so it is absent from
> `/sim`'s `tanks/` — while `Vehicle.PropellantMass` (now computed from `Parts.SubstanceStores`) *does*
> include it, making `mass/propellant` > Σ `tanks/<r>/amount` on booster vessels. gatOS gained a
> dedicated **`srb/<n>/`** read surface (SPEC §3.4.8, `VesselReader.SampleSrbs`): grain mass, usable
> mass, fraction, burn time, mass flow, chamber/exit conditions, burning area, stack validity and a
> per-segment `segments/<m>/` breakdown — **read-only**, since KSA forces a solid's throttle to 0 or 1,
> so ignition stays on the engine surface (`srb/<n>/engine` cross-links to `engines/<n>`). **Semantic drift inherited, no API change**: encounter candidacy widened (rev 4991 — the flat
> SOI cutoff became an orbital-geometry/MOID test, so small moons like Phobos and Deimos now yield
> `encounters/<n>/` rows that 4980 skipped); `Module.List` segmentation (rev 4990) is API-compatible for
> every call gatOS makes. Behavior notes: `/sim/debug/refuel` now refills SRB grains for free; SRBs are
> ordinary `EngineController`s (`SolidMotor : RocketCore`) so `engines/<n>` covers them, though throttle
> is physically inert on a solid; `Program.RenderGame` and `SuperMeshRenderSystem.cs` are **byte-identical**,
> so the display transpiler and the thug_life postfix are untouched; no `Brutal` numerics drift; no
> celestial body-parameter edits in `Astronomicals.xml`. ([read 5018 findings](ksa-read-surface.md#5018-findings) /
> [write 5018 findings](ksa-write-surface.md#5018-findings)). Live re-check items are queued in
> `docs/VALIDATION.md`.
>
> The prior **2026.7.6.4939 → 2026.7.8.4980** update was run
> through it on 2026-07-22 — **one compile break, fixed same-day; no other change needed.** Rev 4943
> removed `InputEvents.VehicleDockingInputData.OldMeanRadius` (docking camera zoom-jump fix) —
> `DockingActuator.Undock` dropped the field from its enqueue (now `{Vehicle, DockingPort, Undock}`,
> still exactly the stock UnDock menu-item enqueue; `DockingPort.Undock` → `Vehicle.Split(Connector,
> PushoffImpulse)` byte-identical). Build + full test suite green against 4980 (0 warnings); every other
> bound member, reflection accessor, and Harmony hook target verified unchanged by a full decomp +
> Content diff of the two side-by-side checkouts (changelog gapless again — revs 4940–4980). Two
> **semantic drifts gatOS inherits, no API change**: the new `FlightComputer.RCSMode` (revs 4946/4949,
> R keybind) — with RCS toggled off, an **auto attitude hold on an RCS-only vessel silently stops
> actuating** (a new silent-ignore path beside the `IsControllable` gate; `ctl/rotate`/`ctl/translate`
> manual flags are not gated); and the `RollMode` default flip `Up` → `Decoupled` (rev 4978) — a fresh
> FC **no longer holds `attitude_target`'s roll component** (pointing still converges). Behavior notes:
> undocked/decoupled vessels now keep their control-module-stamped name (rev 4950 — friendlier
> `vessels/by-id` keys after a split); massless vehicles get a density-based fallback mass (rev 4955);
> the fuel-flow default flipped to furthest-to-nearest-by-stage with a persisted per-engine rule
> (4957/4958/4965 — drain order only, all reads stay truthful); the verlet fix (4977) changes high-warp
> physics values, not read semantics; rev 4942's screenshot feature is additive to `RenderGame` (the
> display transpiler's final-`End()` anchor holds) but its hi-res path force-rebuilds the renderer at 1
> sample — a niche transient hazard for an active thug_life quad pipeline (self-disabling latch covers
> it). ([read 4980 findings](ksa-read-surface.md#4980-findings) /
> [write 4980 findings](ksa-write-surface.md#4980-findings)). Live re-check items are queued in
> `docs/VALIDATION.md`.
>
> The prior 2026.7.5.4892 → 2026.7.6.4939 pass (2026-07-16) was clean — no code changes; behavior notes
> only (the additive fuel-line / tank-transfer / propellant-use system — reads report the new rules
> truthfully, an active transfer draws 20 W/tank; the rev 4914 control-module lockout is **UI-only** so
> `/sim` writes still actuate control-less vessels; animating parts update colliders and force off-rails
> (rev 4930); rev 4915 removes the old service-module parts, **save-breaking upstream** —
> [read 4939 findings](ksa-read-surface.md#4939-findings) /
> [write 4939 findings](ksa-write-surface.md#4939-findings)).
> The 2026.7.3.4826 → 2026.7.5.4892 pass (2026-07-14) was also clean — behavior notes only (the
> rev 4884 combustion→Reactions / tank-affinity refactor, additive to every read; FC `CommandThrottle`
> zeroing; the `Staging`→`ResourceGroups` window rename; the 4866 on-rails shifts —
> [read 4892 findings](ksa-read-surface.md#4892-findings)).
> The 2026.6.9.4750 → 2026.7.3.4826 pass (2026-07-03) was also clean — behavior notes only
> (post-decouple control-state inheritance, a near-SoI gravitation nuance, solar-cell 50→100 W —
> [read 4826 findings](ksa-read-surface.md#4826-findings)). The 2026.6.8.4680 → 2026.6.9.4750 pass
> found four gaps, all fixed 2026-06-27 (G1 docking `PushoffImpulse` N·s, G2 power `Joules`→`Watts`,
> G3 the `controllable` read, G4 the sampler reads anchored) — see
> [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md).

---

## 1. What gatOS is (the seam in one picture)

gatOS runs a real Alpine Linux in a QEMU microVM and exposes live KSA telemetry to the guest as a 9P
filesystem at `/sim` (mirrored over HTTP `/v1`, MQTT `gatos/`, and a serial bus). Its first-class MCP
transport presents that same snapshot/command seam as logical JSON resources and agent tools. Players open SSH
terminals into the guest through purrTTY. **Only the left-hand "gatOS mod" box below ever touches KSA;**
everything to the right of the `SimSnapshot`/`SimCommand` seam is game-free and headlessly testable.

```
        KSA game process (the ONLY place KSA types appear: gatOS.GameMod)
   ┌──────────────────────────────────────────────────────────────────────┐
   │  StarMap lifecycle hooks  ─┐                                            │
   │  Harmony patches          ─┤                                            │       QEMU subprocess
   │   • solver-drain prefix    │   reads        ┌── SimSnapshot ──┐         │     ┌──────────────────┐
   │   • menu postfix           ├─ Game/Ksa ────►│  (immutable)    │──┐      │     │ Alpine guest     │
   │  TelemetrySampler          │   Readers/     └─────────────────┘  │      │     │  sshd, ash, apk  │
   │   (game thread, OnBeforeUi)│                                      ▼      │slirp│  /sim ◄ 9p tcp   │
   │  CommandQueue drain        │   writes       ┌── SimCommand ───┐  9P/HTTP │◄───►│  /mnt ◄ 9p tcp   │
   │   (Frame + Solver phase)  ─┘─ Game/Ksa ────►│  (immutable)    │ MQTT/MCP │     │  SSH ◄ hostfwd   │
   │                                Actuators/   └─────────────────┘  serial │     └──────────────────┘
   │  SshShellSession (ICustomShell ← purrTTY contract)  VmHost → QEMU        │
   └──────────────────────────────────────────────────────────────────────┘
```

The two immutable record types `SimSnapshot` (reads) and `SimCommand` (writes) are the firewall: KSA
types never cross them. That is why a game update can only break things *inside* `Game/Ksa/**` (plus the
two Harmony hook targets), and why the rest of the system is unaffected by KSA churn by construction.

---

## 2. Feature inventory (entry table)

Every gatOS feature, whether it is KSA-coupled, and where the detail lives. "KSA-coupled?" = does a
KSA game update have any chance of breaking it.

| # | Feature area | KSA-coupled? | Detail page |
|---|---|---|---|
| **Reads (sensors)** | | | |
| R | Vessel telemetry (flight/orbit/mass/engines/tanks/power/RCS/solar/lights/docking/decouplers/navball/environment/encounters) | **Yes** (heavy) | [`ksa-read-surface.md`](ksa-read-surface.md) |
| R | Celestial bodies + system catalog (orbits, atmosphere, ocean, frames) | **Yes** | [`ksa-read-surface.md`](ksa-read-surface.md) |
| R | Time / warp / auto-warp / sim-step | **Yes** (sampler-direct) | [`ksa-read-surface.md`](ksa-read-surface.md#sampler-direct-reads) |
| R | Events (snapshot-diff: engine/flameout/dock/undock/decouple/animation/battery) | Indirect (diff over reads) | [`ksa-read-surface.md`](ksa-read-surface.md#events) |
| R | Vessel parts list (top-level parts + nested `subparts/<m>/`, plus the `parts/json` whole-tree JSON doc — a game-free projection; the welds anchor picker; gated by `telemetry_vessel_parts`) | **Yes** | [`ksa-read-surface.md`](ksa-read-surface.md#parts) |
| R | Camera state read-back (`camera/status`, `target`, `mode`, `tidal`, `map/scope`, every composed `pose/**` value — published per rendered frame by the director, **not** through `SimSnapshot`) | **Yes** (Medium: `Viewport.{Mode,MapController}`, `MapController.Scope`, `Camera.{Following,TidalLocking,GetFieldOfView,Orthographic}`, the `CameraMode` enum, and the `Celestial` geodetic back-projection) | [`ksa-read-surface.md#camera`](ksa-read-surface.md#camera) |
| **Writes (controls)** | | | |
| W | Engine ignite/shutdown, per-engine active/min-throttle, manual throttle | **Yes** | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | Staging, RCS (master + manual translation `ctl/translate` + manual rotation `ctl/rotate` — W1, AGC_PLAN §7.4), flight-computer attitude/frame/target/burn | **Yes** (Solver phase for FC setpoints; translation/rotation = reflection on `_manualControlInputs.ThrusterCommandFlags`) | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | Lights (master/on/brightness/colour/cone angles), animations/solar/light deploy | **Yes** (High: template) | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | Decouplers, docking undock + pushoff | **Yes** (4750: `PushoffImpulse`, N·s — G1 fixed) | [`ksa-write-surface.md`](ksa-write-surface.md#docking) |
| W | Camera focus (vessel + body) | **Yes** (Medium: `Program.MainViewport.{BaseCamera,MapCamera}` + `Camera.SetFollow(…, changeControl:false, alert:false)` — **both** cameras since C1.4) | [`ksa-write-surface.md#camera-focus`](ksa-write-surface.md#camera-focus) |
| W | Programmable camera `/sim/camera/**` (ownership take/release, `mode`/`follow`/`tidal`, six reference frames, aim-with-offset, geodetic placement, orbit placement, FOV/ortho/roll/smoothing, JSON tracks, the interpolated `time` channel, `map/scope`) — vessel-agnostic, authority-exempt, gated by `[camera] camera_enabled` | **Yes** (Medium: the prior anchors plus `Viewport.OnFrame(double)` Harmony prefix/postfix and `Program.MainViewport` identity guard. Same-frame apply occurs before `Camera.OnFrame`; postfix reads final `Camera.{PositionEcl,LocalRotation}`. No private-field injection.) | [`ksa-write-surface.md#camera-director`](ksa-write-surface.md#camera-director) |
| W | `/sim/debug` cheats: teleport, one-shot impulse (N·s or Δv kick, CCI or body frame), refill fuel/battery, warp set, control-vessel, pushoff | **Yes** | [`ksa-write-surface.md`](ksa-write-surface.md#debug) |
| W | `/sim/debug` welds (weld/weld_here/unweld/enable/clear) + `always_render_iva` render cheat (ported from `unscience`) | **Yes** (High: per-frame `Teleport`; dynamic `gatos.iva` Harmony) | [`ksa-write-surface.md`](ksa-write-surface.md#welds) |
| W | `/sim/debug/thug_life` world-space quad cheat (add/clear/per-entry position/rotation/size/visible/remove; ported from `unscience`) — gatOS's **first custom GPU rendering** | **Yes** (⚠️ **highest-churn**: render-pipeline internals + Vulkan; dynamic `gatos.thug_life` Harmony postfix on `SuperMeshRenderSystem.RenderMainPass`) | [`ksa-write-surface.md`](ksa-write-surface.md#thug-life) |
| W | First-class per-vessel nodes `vessels/by-id/<id>/{scale,always_render}` (model scaling + render-distance override; ported from `unscience` garrys-torch/i-feel-seen) — authority-exempt, outside `/sim/debug` | **Yes** (High: `Part.Scale` + KittenEva reflection; Medium: dynamic `gatos.always_render` Harmony prefixes on `Vehicle.GetWorldMatrix`/`UpdateRenderData`) | [`ksa-write-surface.md`](ksa-write-surface.md#per-vessel-nodes) |
| W | `/sim/debug/iva` free-floating cabin objects with real inertial physics (master `enabled` switch + `adopt`/`adopt_all`/`release`/`clear`/`nudge`; a **gatOS-owned BepuPhysics 2.5 `Simulation`** in the vessel assembly frame, driving shipped IVA prop **SubPart** transforms; plans/IVA_MOVEMENTS.md) — **off by default; off means no simulation exists at all** | **Yes** (Medium: `MeshReference.PositionCompare` triangle soup + `PartModelModule.Template.Internal` classifier for interior geometry; `Part.{PositionParentAsmb,Asmb2ParentAsmb}` per-frame driver; `Vehicle.{AccelerationBody,AngularAccelerationBody,BodyRates,CenterOfMassAsmb}` forcing terms. **No Harmony patch**, no game-solver mutation) + a new **`BepuPhysics`/`BepuUtilities` DLL reference** | [`ksa-write-surface.md`](ksa-write-surface.md#iva-physics) |
| W | `/sim/audio` userland audio playback (`file/` clip uploads + `play`/`set`/`stop` through the game's FMOD mixer; `audio.*` actions, vessel-agnostic, gated by `audio_enabled`) | **Yes** (Low: `GameAudio.System`/`GetChannelGroup` public statics + the FMOD Core `Brutal.FmodApi` P/Invoke surface — new `Brutal.Fmod.dll` reference) | [`ksa-write-surface.md`](ksa-write-surface.md#audio) |
| W | `/sim/debug/{engineplume,plumetrail,clouds,terrain}` FX editors (the game's four built-in imgui render editors as filesystems: per-**template** engine plumes, the **global** trail renderer, per-body/layer/cloud-type clouds, per-body terrain + a global `wireframe`; one writable leaf per knob + `json`/`reset` per entity; live read-back sampled by `FxEditorReader`; gated by `[control] debug_namespace`) | **Yes** (**High**: reflected renderer handles — `Program._volumetricTrailRenderer`, `_planetTransparenciesRenderer`, `VolumetricExhaustTemplate.References`, the cloud-apply privates and the terrain `MappedMemory` UBO rings — plus the plume propagation loop, the cloud re-upload and the terrain paired UBO write; per-capability health latches) | [`ksa-write-surface.md`](ksa-write-surface.md#fx-editors) |
| **Runtime coupling** | | | |
| C | StarMap lifecycle, Harmony patches (solver-drain, menu fallback), ModMenu entry, status UI | **Yes** (hook targets) | [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md) |
| C | Threading phases (Frame vs Solver), command-drain timing, churn machinery (`[KsaAnchor]`/`KsaHealth`) | **Yes** | [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md) |
| C | Coordinate frames & numerics (CCI/CCE/CCF/ECL, `double3`/`doubleQuat`, Brutal) | **Yes** (Brutal) | [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#frames-and-numerics) |
| C | Mod-ecosystem ABIs: purrTTY contract, StarMap loader, ModMenu | No (not KSA game) | [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#mod-ecosystem-abis) |
| A | KSA assets used: decomp / dll / Content XML; version pins & diffing | **Yes** | [`ksa-assets-and-versions.md`](ksa-assets-and-versions.md) |
| **Game-free surface** | | | |
| G | VM/QEMU lifecycle, disks, ports, guest image, SSH | No | [`non-ksa-surface.md`](non-ksa-surface.md) |
| G | 9P server + VFS, host folder mounts | No | [`non-ksa-surface.md`](non-ksa-surface.md) |
| G | SimFs tree, snapshot/command model, telemetry gating | No | [`non-ksa-surface.md`](non-ksa-surface.md) |
| G | `/sim/ctl/batch` atomic same-tick command groups (SPEC §3.10; reuses the existing drain + per-file parsers, no new KSA binding) | No | [`non-ksa-surface.md`](non-ksa-surface.md) |
| G | Timed command scheduler — `/sim/ctl/timed_batch` + `/sim/ctl/schedules/**`, the 7 `schedule.*` actions, three clock bases (`render`/`wall`/`ut`), shared-clock groups, coalescing catch-up, cap-pressure eviction (gated by `[schedule] schedule_enabled`). Adds **zero** KSA bindings: `KsaCatalog` routes the family straight to the game-free `ScheduleStore.Execute`, and `Mod.TickSchedules` is a driver, not a binding | No | [`non-ksa-surface.md#scheduler`](non-ksa-surface.md#scheduler) |
| G | The game-free half of the programmable camera — `gatOS.SimFs/Camera/**`: the math primitives, `CameraRules`, the three-layer `Track ?? Override ?? Baseline` compositor, `CameraStore` + the writable `track/` dir, the six line grammars, and the JSON track parser/evaluator/player (`kind = camera-track` in the schedules registry) | No | [`non-ksa-surface.md#camera-game-free`](non-ksa-surface.md#camera-game-free) |
| G | HTTP `/v1`, MQTT, MCP Streamable HTTP, serial/bus transports, TypeScript SDK | No | [`non-ksa-surface.md`](non-ksa-surface.md) |

---

## 3. Where KSA actually appears in gatOS (the complete coupling census)

The binding rule (AGENTS.md "THE dependency rule" + its G2 stronger form) is that KSA/Brutal/StarMap
type names live **only** in `gatOS.GameMod`. Within that project, the KSA *game-state* surface is
confined to `Game/Ksa/**`. The full census — the only files a KSA update can touch:

| Location | KSA touch | Guarded by |
|---|---|---|
| `Game/Ksa/Readers/VesselReader.cs` | 22 `[KsaAnchor]` reads (the bulk of telemetry; +`ReadControllable`, G3; +`SampleSrbs`, 5018) | per-accessor try/catch → `KsaHealth`; `BuildFull` whole-pass guard |
| `Game/Ksa/Readers/AnimationLinks.cs` | 1 `[KsaAnchor]` read (structural animation↔module links, cached per vehicle — GP3) | rebuilt on module-count change / 10 s; consumed inside the `BuildFull`/`BuildCore` guards |
| `Game/Ksa/Readers/BodyReader.cs` | 3 `[KsaAnchor]` reads (celestial catalog; statics cached per body — GP3) | sampler-level guard |
| `Game/Ksa/Readers/PartsReader.cs` | 1 `[KsaAnchor]` read (parts + nested subparts; welds anchor picker) | per-call try/catch; `VesselParts` sampler gate |
| `Game/Ksa/Actuators/*.cs` (15 anchored files; `IvaActuator.cs` delegates to `Render/IvaForceRender.cs`, no anchor) | 40 `[KsaAnchor]`s (all controls + debug; incl. `ScaleActuator`'s recursive `Part.Scale` write + best-effort read, `AudioActuator`'s 3 FMOD anchors — `GameAudio.System` create/play + the per-frame channel tick — and `CameraActuator.Focus`, **rebound** at C1.4 to set follow on **both** the base and map cameras) | `KsaCatalog` try/catch per command; `AudioActuator.Tick` under the `_audioDead` session latch |
| `Game/Ksa/Render/IvaForceRender.cs` | 1 `[KsaAnchor]` (`always_render_iva` cheat; own dynamic `gatos.iva` Harmony) | per-postfix try/catch; restored + unpatched on disable/unload |
| `Game/Ksa/Render/VesselForceRender.cs` | 3 `[KsaAnchor]` (per-vessel `always_render` override; own dynamic `gatos.always_render` Harmony prefixes on `Vehicle.GetWorldMatrix`/`UpdateRenderData`, installed only while ≥ 1 vessel is marked) | per-prefix try/catch → stock cull; install throw → `KsaCatalog` degrade latch; unpatched on last unmark/prune/unload |
| `Game/Ksa/Welds/{WeldEngine,WeldManager}.cs` | 4 `[KsaAnchor]` (per-frame `Teleport` driver + registry/liveness) | per-weld try/catch in the driver; `_weldsDead` session latch |
| `Game/Ksa/Iva/*.cs` (`InteriorGeometry`×1, `FloatingObject`×3, `IvaPhysicsManager`×6; `CabinSim`/`CabinCallbacks`/`CabinTuning` touch **Bepu only**, no KSA type) | 10 `[KsaAnchor]` (IVA cabin physics: the interior-mesh walk, the per-frame SubPart transform driver, adopt-time measurement/lookup, the forcing-term reads + park gates) | master switch off by default (nothing constructed); per-vessel try/catch in the driver → that cabin dropped; `_ivaDead` session latch releases everything and disables the feature |
| `Game/Ksa/ThugLife/*.cs` (`ThugLifeTextureFactory`×1, `ThugLifeQuadRenderer`×2, `ThugLifeRenderPatches`×1, `ThugLifeManager`×3; `ThugLifeEntry`/`ThugLifeTexturePattern` have none) | 7 `[KsaAnchor]` render-internals (`thug_life` cheat: Vulkan GPU build, per-frame anchor math, dynamic `gatos.thug_life` Harmony postfix on `SuperMeshRenderSystem.RenderMainPass`) — **deepest / highest-churn coupling** | per-frame try/catch; self-disables (`Active=false`) on any GPU fault; unpatched + GPU freed on disable/unload |
| `Game/Ksa/Fx/*.cs` (`FxReflect`×8, `PlumeActuator`×4, `TrailActuator`×2, `CloudActuator`×4, `TerrainActuator`×3, `FxEditorReader`×4; `FxPristine` has none — no KSA types) | 25 `[KsaAnchor]` (the four FX editors: the reflective handles in `FxReflect` incl. the terrain UBO rings, the per-family read/write/apply pairs, the plume propagation loop, the sampler's rosters) | per-command try/catch in `KsaCatalog`; per-capability `KsaHealth` latches (`fx.*` keys) → `EOPNOTSUPP`; sampler-level try/catch (logged once); pristine restore at unload |
| `Game/Ksa/Camera/*.cs` | Programmable-camera anchors plus `CameraViewportPatch`'s `Viewport.OnFrame(double)` prefix/postfix. Main viewport is selected by identity; the controller's public `Camera`/viewport camera is used, never unscience's obsolete `___Transform` injector | idle director is one branch; hook faults latch once; driver fault restores camera; unresolvable frames hold last good pose |
| `Game/Ksa/{DisplayRenderPatch,FrameCapture}.cs` | 2 `[KsaAnchor]` (the `/sim/display` capture: the `Program.RenderGame` transpiler + the in-band Vulkan blit/readback) | transpiler degrades to **no injection**; a capture-time fault latches the feature off for the session (`_faulted`) |
| `Game/Ksa/KsaCatalog.cs` | 2 `[KsaAnchor]` (vehicle/astronomical resolution) | self |
| `Game/Ksa/{KsaAnchor,KsaHealth}.cs` | churn machinery (no KSA types in KsaHealth) | — |
| `Game/TelemetrySampler.cs` | 5 `[KsaAnchor]` reads (G4: `Universe.*` time/warp/system + `VersionInfo.Current`) | per-vehicle + per-call try/catch |
| `Game/Mod.Game.cs` | Harmony targets `Universe.ExecuteNextVehicleSolvers`, `Program.DrawProgramMenusHook`; `Program.MainViewport`, `ModLibrary.Find` | `AccessTools` null-check + try/catch → feature disabled, not crash |
| `Game/BrutalModLogger.cs` | `Brutal.Logging` sink | try/catch at install |
| `Mod.cs`, `ModAssets.cs` | StarMap.API attributes, purrTTY contract — **no KSA game types** | n/a (mod-ecosystem ABI, not KSA) |

Detail and per-member break-impact: the four `ksa-*.md` pages. **The census totals 147 `[KsaAnchor]`s**
(the one further occurrence in `Game/Ksa/KsaAnchor.cs` is the attribute's own doc comment, not a
binding). It grew with each ported cheat: the sampler's `Universe`/`VersionInfo` reads were anchored in
the 4750 fix-pass (G4); the `unscience`-ported welds/IVA/parts feature added 6 (PartsReader,
IvaForceRender, WeldEngine×2, WeldManager×2); the `thug_life` render cheat added the 7
`Game/Ksa/ThugLife/` render-internals anchors (`ThugLifeTextureFactory.UploadPixels`,
`ThugLifeQuadRenderer.{BuildPipeline,TryComputeModelEgo}`, `ThugLifeRenderPatches.Apply`,
`ThugLifeManager.{Update,IsLive,EnsureGpu}`); the first-class per-vessel nodes added `ScaleActuator`×2
and `VesselForceRender`×3 (the `gatos.always_render` patch targets + the two reproduced method bodies);
the IVA cabin-physics feature added the 10 `Game/Ksa/Iva/` anchors (`InteriorGeometry.Build`,
`FloatingObject.{ApplyPose,ReadBodyPose,RestoreRestPose}`, `IvaPhysicsManager.{Adopt,Update,TryMeasure,
IsInteriorProp,FindSubPart,IsLive}`); the **FX editors** (2026-08-01) added the `Game/Ksa/Fx/` set (25,
the largest single addition — the reflective handles are catalogued in
[`ksa-runtime-coupling.md#fx-accessors`](ksa-runtime-coupling.md#fx-accessors)); and the **programmable
camera** (2026-08-06) added the `Game/Ksa/Camera/` set (**21**, the second-largest —
`CameraDirector.{Take,Restore,Apply,ApplyTimeScale,RestorePositionEcl,SetMode,SetFollow,SetTidal,
SetMapScope}`, `CameraTargets.{TryResolve,PositionEcl,VelocityEcl,BodyFixed2Ecl,UpEcl,Describe,IsLive}`,
`CameraFrames.{TryFrame2Ecl,GeoToEcl,TryEclToGeo}`, `CameraReader.{Sample,ModeOf}`) while **rebinding**
one existing anchor (`CameraActuator.Focus`, C1.4) rather than adding it. The **generic timed
scheduler** landed in the same window and added **no anchor at all** — it is game-free end to end
([`non-ksa-surface.md#scheduler`](non-ksa-surface.md#scheduler)).

So the only remaining un-anchored KSA touch-points are the two `Mod.Game.cs` Harmony hook targets (the
`gatos.iva`/`gatos.thug_life`/`gatos.always_render` patch targets and the weld/IVA drivers'
`VehicleSolver.Wait()` are themselves anchored; the camera and schedule drivers install **no** patch
and their KSA members are anchored inside `Game/Ksa/Camera/`).

---

## 4. Risk classes (how `scope/` rates churn)

Mirrors `ChurnRisk` in `Game/Ksa/KsaAnchor.cs`:

| Risk | Meaning | Examples |
|---|---|---|
| **Low** | Core vehicle/orbit/time/body state + the struct-of-arrays (`ModuleStateful`) pattern. | `Vehicle.Id`, `Orbit` elements, `Celestial.Mass`, `Universe.GetElapsedTime`. |
| **Medium** | FlightComputer, InputEvents-mediated ops, NavBall, per-module controllers, docking. | `FlightComputer.*`, `EngineController.SetIsActive`, `DockingPort.*`, `SequenceList`. |
| **High** | Template internals + anything reached by **reflection** (no compile-time guard). | `LightModule.Template.*` (clone), `Vehicle._manualControlInputs.EngineThrottle`. |

High-risk items deserve a runtime check after every update even when the build is green, because the
compiler can't see them. They are listed in [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#reflection-accessors).

---

## 5. Document map

- [`ksa-read-surface.md`](ksa-read-surface.md) — every sensor/read: `/sim` path → gatOS site → KSA member → decomp file → units → risk → break notes.
- [`ksa-write-surface.md`](ksa-write-surface.md) — every control/debug write: action key → actuator → KSA member → phase → errno → break notes.
- [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md) — lifecycle, Harmony hooks, threading phases, frames/numerics, reflection accessors, mod-ecosystem ABIs, the churn machinery.
- [`ksa-assets-and-versions.md`](ksa-assets-and-versions.md) — the KSA decomp/dll/Content assets gatOS depends on, the exact XML templates that seed runtime values, version pins, and the version-diff method.
- [`non-ksa-surface.md`](non-ksa-surface.md) — the complete game-free feature inventory (VM, SSH, 9P, SimFs, transports, guest, SDK) — included so this catalog is *complete*, each entry marked "KSA coupling: none".

**Cross-references (kept in lockstep with `scope/`):** [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md)
(API), [`docs/KSA_INTEGRATION_MATRIX.md`](../docs/KSA_INTEGRATION_MATRIX.md) (anchor mirror),
[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) (runtime shape),
[`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](../docs/KSA_CELESTIAL_COORDINATE_FRAMES.md) (frames),
[`docs/VALIDATION.md`](../docs/VALIDATION.md) (live checklists).
# Paint surface

Paint adds a game-free rule/transform domain (`gatOS.Paint`) and one high-churn KSA adapter
(`Game/Ksa/Paint`). It covers opt-in vehicle shader transformation and reversible shared/individual
EVA coloration across 9P, HTTP, MQTT, and MCP. Its authoritative maintenance map is
[`plans/PAINT_ASBUILT.md`](../plans/PAINT_ASBUILT.md); every KSA upgrade must run that document's
shader/state-bit/material-clone audit and the paint checklist in `docs/VALIDATION.md`.

# Clutter texture surface

Custom ground-clutter textures add a second, game-free store (`gatOS.SimFs/Paint/TextureStore.cs`,
`TextureDirectory.cs`, `TextureCommands.cs`) and a second KSA adapter file
(`Game/Ksa/Paint/ClutterTextureBridge.cs`, **2** `[KsaAnchor]`s — one High bind/upload site, one
Medium catalog walk that reuses `FxReflect.Terrain` and so adds no reflection site). The store owns
the in-memory upload set (`TextureFile`/`TextureFileInfo`, magic-byte container sniffing for
png/jpeg/bmp/hdr/dds/ktx/ktx2), the desired-binding set (`TextureBinding`), the published
`ClutterTextureInfo` catalog, `TextureBindStatus` applied rows, `TextureRuntime`, the file/byte/
binding/dimension caps, and the `Revision` counter that gates the bridge's per-frame reconcile — with
nothing bound the feature costs one integer comparison per frame, which is why it has no runtime
master switch. `/sim/paint/textures/{file/,status,info,help,bindings,applied,clutter,bind,unbind,
clear}` is the whole tree; HTTP adds dedicated binary routes at `/v1/paint/texture/…` (the second
transport-parity exception after audio), MQTT mirrors the scalars, and MCP projects the same store
through `gatos.paint_texture` plus three `gatos.paint_control` operations. Its authoritative
maintenance map is [`plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md`](../plans/GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN.md).
