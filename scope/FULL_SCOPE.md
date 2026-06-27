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
[`CLAUDE.md`](../CLAUDE.md). Keeping `scope/` stale defeats its entire purpose.

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
   Vehicle control). See [`ksa-assets-and-versions.md`](ksa-assets-and-versions.md) for how versions and
   the decomp/dll/Content layout are organized.

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

> **Current applied result of this playbook:** the 2026.6.8.4680 → 2026.6.9.4750 update has been run
> through it. Findings and the remediation plan are in
> [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md). **All four gaps are fixed
> (2026-06-27) — gatOS builds + tests green against 4750:** G1 docking (`PushoffImpulse`, N·s), G2 power
> (`Joules`→`Watts`, instantaneous W), G3 a `controllable` read (gatOS doesn't gate — relies on KSA's own
> lockout), G4 the sampler's `Universe`/`VersionInfo` reads anchored. Only the in-flight re-validation
> checklist (`docs/VALIDATION.md`) remains.

---

## 1. What gatOS is (the seam in one picture)

gatOS runs a real Alpine Linux in a QEMU microVM and exposes live KSA telemetry to the guest as a 9P
filesystem at `/sim` (mirrored over HTTP `/v1`, MQTT `gatos/`, and a serial bus). Players open SSH
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
   │   (Frame + Solver phase)  ─┘─ Game/Ksa ────►│  (immutable)    │  MQTT    │     │  SSH ◄ hostfwd   │
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
| **Writes (controls)** | | | |
| W | Engine ignite/shutdown, per-engine active/min-throttle, manual throttle | **Yes** | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | Staging, RCS, flight-computer attitude/frame/target/burn | **Yes** (Solver phase) | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | Lights (master/on/brightness/colour/cone angles), animations/solar/light deploy | **Yes** (High: template) | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | Decouplers, docking undock + pushoff | **Yes** (4750: `PushoffImpulse`, N·s — G1 fixed) | [`ksa-write-surface.md`](ksa-write-surface.md#docking) |
| W | Camera focus (vessel + body) | **Yes** | [`ksa-write-surface.md`](ksa-write-surface.md) |
| W | `/sim/debug` cheats: teleport, refill fuel/battery, warp set, control-vessel, pushoff | **Yes** | [`ksa-write-surface.md`](ksa-write-surface.md#debug) |
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
| G | HTTP `/v1`, MQTT, serial/bus transports, TypeScript SDK | No | [`non-ksa-surface.md`](non-ksa-surface.md) |

---

## 3. Where KSA actually appears in gatOS (the complete coupling census)

The binding rule (CLAUDE.md "THE dependency rule" + its G2 stronger form) is that KSA/Brutal/StarMap
type names live **only** in `gatOS.GameMod`. Within that project, the KSA *game-state* surface is
confined to `Game/Ksa/**`. The full census — the only files a KSA update can touch:

| Location | KSA touch | Guarded by |
|---|---|---|
| `Game/Ksa/Readers/VesselReader.cs` | 21 `[KsaAnchor]` reads (the bulk of telemetry; +`ReadControllable`, G3) | per-accessor try/catch → `KsaHealth`; `Enrich` whole-pass guard |
| `Game/Ksa/Readers/BodyReader.cs` | 3 `[KsaAnchor]` reads (celestial catalog) | sampler-level guard |
| `Game/Ksa/Actuators/*.cs` (11 files) | 26 `[KsaAnchor]` writes (all controls + debug) | `KsaCatalog` try/catch per command |
| `Game/Ksa/KsaCatalog.cs` | 2 `[KsaAnchor]` (vehicle/astronomical resolution) | self |
| `Game/Ksa/{KsaAnchor,KsaHealth}.cs` | churn machinery (no KSA types in KsaHealth) | — |
| `Game/TelemetrySampler.cs` | 5 `[KsaAnchor]` reads (G4: `Universe.*` time/warp/system + `VersionInfo.Current`) | per-vehicle + per-call try/catch |
| `Game/Mod.Game.cs` | Harmony targets `Universe.ExecuteNextVehicleSolvers`, `Program.DrawProgramMenusHook`; `Program.MainViewport`, `ModLibrary.Find` | `AccessTools` null-check + try/catch → feature disabled, not crash |
| `Game/BrutalModLogger.cs` | `Brutal.Logging` sink | try/catch at install |
| `Mod.cs`, `ModAssets.cs` | StarMap.API attributes, purrTTY contract — **no KSA game types** | n/a (mod-ecosystem ABI, not KSA) |

Detail and per-member break-impact: the four `ksa-*.md` pages. Total `[KsaAnchor]` sites: **59**
(across 15 files) — the sampler's `Universe`/`VersionInfo` reads were anchored in the 4750 fix-pass (G4),
so the only remaining un-anchored KSA touch-points are the two Harmony hook targets.

---

## 4. Risk classes (how `scope/` rates churn)

Mirrors `ChurnRisk` in `Game/Ksa/KsaAnchor.cs`:

| Risk | Meaning | Examples |
|---|---|---|
| **Low** | Core vehicle/orbit/time/body state + the struct-of-arrays (`ModuleStateful`) pattern. | `Vehicle.Id`, `Orbit` elements, `Celestial.Mass`, `Universe.GetElapsedSimTime`. |
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
