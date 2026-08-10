# AGENTS.md — the `/sim` schema-change constitution

This file is the **binding, step-by-step playbook for changing the `/sim` API surface** — adding,
removing, renaming, or altering any node, value format, action key, config gate, or transport
mirror. It codifies the patterns already latent in the codebase so that every schema change is
made the same way, end to end, with nothing forgotten. `AGENTS.md` states *that* the SPEC/scope
lockstep is mandatory; this file states **exactly how** to execute it.

Audience: any agent or human touching `gatOS.SimFs/SimFsTree.cs`, `gatOS.SimFs/Commands/`,
`gatOS.GameMod/Game/Ksa/`, `SPEC_9P_FILESYSTEM.md`, or the scope/matrix docs. When this file and
the code disagree, the code wins and this file is stale — fix it in the same change.

---

## 0. What counts as a schema change

Any of the following is a schema change and MUST follow this playbook in full:

- a `/sim` node or directory added/removed/renamed (`SimFsTree.cs`), including per-module and
  per-registry-entry files;
- a value **format**, **unit**, or **range** change (`Formats.cs`, `SimSnapshot` field semantics);
- a command **action key** added/removed/renamed, or its argument shape/phase changed
  (`KsaCatalog.cs`, `SimCommand.SolverActions`, the actuators);
- an HTTP `/v1` route, MQTT topic, or config gate that changes availability;
- an errno mapping change (`CommandResult.cs`) or a file archetype change.

Pure implementation changes behind an unchanged surface (perf, refactors) are not schema changes,
but still obey the G2 dependency rule and threading rules (`AGENTS.md`).

---

## 1. The one write path and the one read path (invariants)

These are structural invariants. Never add a parallel path.

- **Writes:** `/sim` control file → immutable `SimCommand` → `ICommandSink`/`CommandQueue`
  (transport threads only *enqueue*) → `CommandQueue.Drain` on the **game thread** →
  `KsaCatalog.Execute` → actuator. `POST /v1/fs/<path>`, `POST /v1/command`, MQTT
  `gatos/sim/<path>/set` and `gatos/command` all reach exactly this pipeline **by construction** —
  adding a control file to the tree lights up every transport with zero new code.
- **Reads:** game state is sampled on the game thread into an immutable `SimSnapshot` (or a
  dedicated store for host-side/bulk state — see §6), published by one volatile swap. 9p files,
  HTTP and MQTT all project the same snapshot (`Formats` for 9p text, `SimJson` for the JSON
  aggregates, `VfsScan` for the leaf-by-leaf field mirror).
- **Phase is derived, never passed.** `SimCommand.Phase` comes from the action key via the
  `SimCommand.SolverActions` set (`gatOS.SimFs/Commands/SimCommand.cs`). A new action that must be
  visible to the vehicle solver (flight-computer setpoints, refills) goes in that set; everything
  else (render/registry/cosmetic/debug) is `Frame`. Never set a phase at a construction site.
- **G2 dependency rule:** a KSA type name may appear **only** under `gatOS.GameMod/Game/Ksa/`,
  annotated `[KsaAnchor]`. `SimFs`, transports, formats and the command pipeline stay game-free.

---

## 2. Naming conventions (binding)

- **Paths** are lower snake_case, plural directories for collections (`vessels/`, `lights/`),
  integer-keyed subdirs for registries (`debug/thug_life/<id>/`). A node's **qid string is its
  path relative to `/sim`** and must be unique and stable (`Qid("debug/thug_life/0/position")`).
- **Action keys:** `<family>.<name>` in snake_case after the dot. Families: `vessel.*`,
  `engine.*`, `light.*`, `rcs.*`, `animation.*`, `decoupler.*`, `docking.*`, `camera.*`,
  `audio.*`, and the cheat namespace `debug.*`. Registry families use
  `debug.<feature>_<verb|field>` (`debug.thug_life_add`, `debug.weld_clear`).
- **One knob = one leaf.** Prefer many small writable leaves (`color`, `size`, `visible`) over a
  single "settings blob" file — the leaves are what make the surface scriptable (`echo`-able),
  animatable at frame rates, and automatically mirrored per-field over HTTP/MQTT.
- **Config keys** are snake_case (Tomlyn `SnakeCaseLower` off the C# property), grouped via the
  `Sections` table in `GatOsConfig.cs`.

---

## 3. Choosing the control-file archetype

All in `gatOS.SimFs/Commands/`; all derive from `CommandFile` (line-buffered, actuate on first
`\n`, EINVAL on parse failure **before** the sink). Use the tree-local helper wrappers in
`SimFsTree.cs` (`FlagControl`/`FractionControl`/`NumberControl`/`VectorControl`/`EnumControl`) —
they degrade to read-only `StaticTextFile` when no sink is wired.

| Payload | Archetype | Notes |
|---|---|---|
| `0`/`1` | `ControlFile.Flag` | booleans, toggles |
| real in `[0,1]` | `ControlFile.Fraction` | throttles, goals, fractions |
| any finite real | `ControlFile.Number` | scalars with units |
| exactly N reals | `VectorControlFile` | colors (`r g b`), positions (`x y z`), ranges |
| token from a fixed set | `EnumControlFile` | modes; canonical casing echoed back |
| any token | `TokenControlFile` | ids, names |
| mixed string+numbers | `LineControlFile` | escape hatch; hand-written parser, short + long forms |
| fire-once | `TriggerFile` | `reset`, `remove`, `clear`, `refill_*` (write `1`) |
| multi-write atomic group | `BatchFile` | already global at `/sim/ctl/batch`; don't add per-feature batches |

Parse failures throw `VfsErrorException(EINVAL)` and never reach the sink. **Re-validate the same
rules game-side** in the catalog/actuator (arity, ranges, finiteness): HTTP/MQTT `POST /v1/command`
bypasses the 9p parse. Put shared validation in a game-free `<Feature>Rules` class in
`gatOS.SimFs/Commands/` (precedents: `ScaleRules`, `ImpulseRules`, `TranslateRules`) so it is
unit-testable without game DLLs and callable from both sides.

---

## 4. Addressing modes

Pick exactly one per action family; it decides how `KsaCatalog.Execute` routes:

1. **Global** (`debug.warp`, `debug.always_render_iva`): `VesselId = ""`, handled before vehicle
   resolution.
2. **Registry-keyed** (`debug.thug_life_*`): `VesselId = ""`, entry id in `Ordinal`, auxiliary
   symbols in `Token`/`Aux`; a private router in `KsaCatalog` arity-checks and dispatches. The
   `/sim` tree exposes `add` (parser), `clear` (trigger), `count`, `help`, and one `<id>/` subdir
   per live entry with per-field writable leaves + a `remove` trigger. **This is the template for
   any "editor" feature.**
3. **Per-vessel** (`debug.teleport`, per-module controls): vessel id path-implied under
   `debug/vessels/<id>/` or `vessels/by-id/<id>/`, carried in `SimCommand.VesselId`, resolved via
   `ResolveVehicle`. Module index rides `Ordinal`.

Authority: everything `debug.*` is exempt from the active-vessel authority gate (the debug
namespace is its own opt-in, `[control] debug_namespace`). A **non-debug** per-vessel action that
must work on any addressed vessel goes in `KsaCatalog.AnyVesselActions` — a deliberate,
documented decision, not a default.

---

## 5. Actuator rules (`gatOS.GameMod/Game/Ksa/`)

- `internal static`, game-thread-only, returns `CommandResult`; lives under `Actuators/`,
  `Render/`, or a feature folder. Every member touching a KSA type carries `[KsaAnchor]` with
  `Member`, `SourceFile`, `Verified` (ISO date), `GameVersion`, `Risk` (`ChurnRisk`), `Notes`.
- **Lazy dynamic Harmony:** if the feature needs a patch, install on first use, remove on last
  use/despawn-prune/unload (`VesselForceRender`, `IvaForceRender`, `ThugLife` are the models).
  Registries are mutated only on the game thread and read by patches through one volatile
  immutable snapshot. All cheats tear down in `Mod.TeardownGameCheats`.
- Prefer **remembering the pristine value** before first mutation so a `reset` trigger can restore
  it; state that does not survive scene changes/despawns must be pruned (ride the sampler's
  vehicle enumeration like `VesselForceRender.Prune`).
- Errnos: `Invalid` for bad args, `NotFound` for a gone vessel/entry, `Unsupported` for unknown
  action or a degraded/absent capability, `Fault` (+ health latch) for thrown exceptions.

---

## 6. Read-back — pick one mechanism

| Mechanism | When | Precedent |
|---|---|---|
| `SimSnapshot` init-only property | small global state, sampled per tick | `AlwaysRenderIva` |
| `IReadOnlyList<XSnapshot>` on `SimSnapshot` | registry features | `Welds`, `ThugLife` |
| `VesselSnapshot` property (filled in `VesselReader`) | per-vessel state | `Scale`, `AlwaysRender` |
| dedicated store (volatile publish) | host-side, off-cadence, or bulk state | `AudioStore` |
| no read-back (literal default) | pure impulse inputs | `teleport` `"0 0 0 0 0 0"` |

Snapshot-backed leaves use `Line(...)` (memoized per publish); values that change without a
snapshot publish use `LiveLine(...)`. A plain snapshot property reaches `/v1/snapshot` and MQTT
automatically via `SimJson.Serialize<T>` — touch `SimJson` only for a new *category* endpoint, and
`Formats` only for a non-trivial composite line (`Formats.WeldSpec` precedent; the
`Formats.VesselTelemetry` doc is **frozen** for the SDK).

---

## 7. Animation-rate ergonomics (the "light show" bar)

Any writable leaf that could plausibly be animated (colors, intensities, scales, positions) must
hold up to the `examples/dancy-party-rs` usage pattern — clients writing individual leaves at
10–60 Hz, fire-and-forget, deduped client-side:

- writes must be cheap to validate and enqueue (no allocation-heavy parsing on the hot path);
- one leaf per scalar/vector knob so clients write only what changed;
- read-back must reflect the **live** value so a client can resync after restart;
- a family-level `reset` trigger restores game defaults (so an aborted animation never strands
  the game in a weird state);
- multi-knob atomic updates ride the existing `/sim/ctl/batch` — never a bespoke batch file;
- document the effective apply cadence in the SPEC entry (one command per frame drain; batches
  land as one unit).

---

## 8. Config gating

The `/sim/debug` namespace is gated by `[control] debug_namespace` — a new debug feature needs
**no new key** unless it carries its own cost/caps (the `[audio] audio_enabled` precedent). When
adding a key: property + XML doc in `GatOsConfig.cs`, an entry in its `Sections` table, clamping
in the load path (clamp + warn, never reject), and a **hand-synced** matching block in
`Configuration/gatos.default.toml` (checked-in template; no test asserts the sync — do it).

---

## 9. Docs lockstep matrix (MUST — same change, no exceptions)

| Artifact | When | What |
|---|---|---|
| `SPEC_9P_FILESYSTEM.md` | always | §3.x surface table rows (+ prose block per family: discovery, id/lifecycle, patch lifecycle, persistence, errnos); §5.1 one row per action key; §2.5 for new config gates; §2.4/§8 only for new errnos/units |
| `docs/KSA_INTEGRATION_MATRIX.md` | KSA binding added/moved | row(s) `Path \| A \| Write \| KSA anchor \| Risk \| Phase`; own `##` block for large features |
| `scope/FULL_SCOPE.md` | always | §2 feature-inventory row; §3 coupling census if files added under `Game/Ksa/**` |
| `scope/ksa-write-surface.md` | any new write binding | rows (or a new `{#anchor}` section for a family) incl. the game-version tick column |
| `scope/ksa-read-surface.md` | new sampler/reader KSA read | row/section for the read binding |
| `scope/ksa-runtime-coupling.md` | any Harmony patch, per-frame driver, or render hook | a `###` entry describing install/teardown lifecycle |
| `AGENTS.md` | feature lands | status-table row; threading-rules paragraph if a new game-thread work site or patch is added |
| `docs/VALIDATION.md` | game-coupled feature lands | a `## <feature> — validation pass — **NOT YET RUN**` checklist section |
| `docs/MILESTONES.md` | feature lands | as-built detail |
| `.agents/skills/gatos/` + `docs/TUTORIAL_DATA_REFERENCE.md` | change affects how programs are written | refresh recipes/reference |

The code wins; the docs mirror it; they must never disagree. `[KsaAnchor]` attributes remain the
source of truth for bindings; `scope/` is their human mirror.

---

## 10. Test requirements

For every new surface, in `gatOS.SimFs.Tests` (fixture naming: `<Feature>TreeTests` for a debug
subtree, `Vessel<Feature>Tests` for a per-vessel node; standard 9p-client fixture with
`FakeCommandSink { DebugEnabled = true }` for debug trees):

1. **Read-back** — publish a snapshot/store state, read the leaf, assert the projected text.
2. **Write → exact command** — write the leaf, assert the full `SimCommand` (action, ordinal,
   values/token, **phase**).
3. **EINVAL boundary** — unparseable/wrong-arity/non-finite input throws EINVAL and
   `_sink.Submits` stays zero.
4. **Rules class** — the game-free `<Feature>Rules` validation, table-driven.

Also extend the tree-crawl guard
(`SimFsTreeTests.ControlEnabledTree_ExposesEveryModuleControlStatusAndDebugPath`) with every new
path — it is what catches a subtree silently vanishing.

Every change ends with `dotnet build gatos.slnx` and `dotnet test gatos.slnx --nologo -v quiet`
green, zero warnings (warnings are errors). GameMod has no test project (game-coupled); its logic
that *can* be game-free must live game-free (rules classes, stores) so it is tested.

---

## 11. Definition of done (checklist)

- [ ] Tree nodes declared in `SimFsTree.cs` with stable unique qids; helper wrappers used.
- [ ] Action keys named per §2; added to `SolverActions` **only** if solver-visible.
- [ ] Catalog routing + game-side re-validation; actuator(s) under `Game/Ksa/` with `[KsaAnchor]`.
- [ ] Read-back mechanism chosen per §6 and wired (sampler or store).
- [ ] Reset/teardown semantics: pristine-value restore, despawn pruning, `TeardownGameCheats`.
- [ ] Config gate only if warranted; default TOML template synced.
- [ ] SPEC + matrix + scope pages + AGENTS.md + VALIDATION + MILESTONES updated (§9).
- [ ] Tests per §10, tree-crawl guard extended, full build + suite green, zero warnings.
- [ ] Commit message starts with the task/issue id.
