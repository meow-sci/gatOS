# FX editors — `/sim/debug/{engineplume,plumetrail,clouds,terrain}` (issue #2)

> **Status:** design locked; implementation phases below. Tracks
> [gatOS#2](https://github.com/meow-sci/gatOS/issues/2): expose the game's built-in imgui debug
> editors — engine plume ("Volumetric Exhausts"), engine trail ("Plume Trails"), terrain
> ("Terrain Editor"), clouds ("Clouds") — as first-class `/sim/debug/` features. Follow
> **`AGENTS.md`** (the schema-change constitution) for every step; this plan supplies the
> feature-specific facts. Decompiled-source references are to
> `/home/user/ksa-game-assemblies/current/decomp/` (KSA build **2026.7.10.5056**).

## 0. Goals and the UX bar

The surface must support **"light show" animation programs** in the mold of
`examples/dancy-party-rs`: a TUI (or shell loop) writing individual attribute leaves at
10–60 Hz, fire-and-forget, deduped client-side — e.g. cross-fading the plume emission colors of
every engine template on a palette clock while pulsing `emission/brightness` on a second clock.
Consequences (AGENTS.md §7):

- **One leaf per knob** (`emission/color0` = `r g b`, `emission/brightness` = scalar), never a
  settings blob. Whole-entity `json` docs exist for *discovery/save*, not for writing.
- **Read-back is live**: leaves render the current sampled value, so a client can walk the tree
  once, snapshot a "profile", animate, and `reset` restores game defaults.
- **`reset` triggers** at entity level restore the pristine (pre-gatOS-write) values captured on
  first mutation.
- Writes are Frame-phase commands; multi-leaf atomic updates ride the existing `/sim/ctl/batch`.
- Everything gated by the existing `[control] debug_namespace` — **no new config key**.

## 1. The declarative field-catalog pattern (the schema pattern this feature codifies)

All four families share one game-free mechanism in `gatOS.SimFs/Fx/`:

> The pattern now has a first-party FX consumer: **`examples/nyan-fire-rs`** — a ratatui console that
> drives `debug/engineplume/templates/<id>/emission/color0..3` as a scrolling stripe window (batched
> through `ctl/batch`, deduped per leaf, `reset` on stop), i.e. exactly the §0 "light show" bar.


```csharp
public enum FxKind { Number, Flag, Color3, Color4 }

/// One editable attribute. Key is the slash path under the entity dir; segments "*" match a
/// non-negative integer index (cloud layers/types, terrain biomes).
public sealed record FxFieldSpec(string Key, FxKind Kind, double Min, double Max,
    string Unit, string Doc)
{
    public int Arity => Kind switch { FxKind.Color3 => 3, FxKind.Color4 => 4, _ => 1 };
}

public static class FxCatalog
{
    public static readonly IReadOnlyList<FxFieldSpec> EnginePlume;   // §2 table
    public static readonly IReadOnlyList<FxFieldSpec> PlumeTrail;    // §3 table
    public static readonly IReadOnlyList<FxFieldSpec> Clouds;        // §4 table
    public static readonly IReadOnlyList<FxFieldSpec> Terrain;       // §5 table
    /// Match a concrete field path ("layers/0/color") against a family's specs; returns the
    /// spec + extracted indices, or null. Used by the tree AND by KsaCatalog re-validation.
    public static FxFieldMatch? Match(IReadOnlyList<FxFieldSpec> family, string path);
    /// Validate a payload against a spec: arity, finiteness, [Min,Max] clamp check.
    public static bool IsValid(FxFieldSpec spec, IReadOnlyList<double> values);
}
```

**Snapshot shape** (`gatOS.SimFs/Snapshots/`, init-only additions — AGENTS.md §6):

```csharp
public sealed record FxEntitySnapshot(string Id, IReadOnlyDictionary<string, double[]> Fields);
// Fields is keyed by CONCRETE field paths ("emission/color0", "layers/0/color"); values are
// length-Arity arrays. Flags are 0/1.

public sealed record FxEditorsSnapshot
{
    public IReadOnlyList<FxEntitySnapshot> PlumeTemplates { get; init; } = [];
    public FxEntitySnapshot? Trail { get; init; }                  // single entity, Id = ""
    public IReadOnlyList<FxEntitySnapshot> CloudBodies { get; init; } = [];
    public IReadOnlyList<FxEntitySnapshot> TerrainBodies { get; init; } = [];
}

// SimSnapshot gains:  public FxEditorsSnapshot? FxEditors { get; init; }   (null ⇒ not sampled)
```

The tree walks a family's specs against an entity's `Fields` keys: concrete keys present in the
snapshot define which leaves (and which indexed subdirs) exist. One generic builder constructs
every leaf: `Number`→`NumberControl`-style, `Flag`→`FlagControl`-style, `Color3/4`→
`VectorControl`-style, all emitting the family's single `_set` action (§6). This keeps the tree,
validation, actuator dispatch, and SPEC documentation all driven by **one table per family**.

**Allocation discipline:** the sampler rebuilds `FxEditorsSnapshot` only when (a) an FX write
occurred since the last build (a version counter the actuators bump), or (b) 2 s elapsed
(catches in-game imgui edits), else it reuses the previous instance by reference (the
`parts/json` memoization precedent).

## 2. `/sim/debug/engineplume` — volumetric exhaust templates

**Scope: per-TEMPLATE (shared), not per-part.** `RocketNozzle.ReactionPlumes[].VolumetricExhaustReference.Id`
resolves to the one shared `VolumetricExhaustTemplate` (`KSA/VolumetricExhaustTemplate.cs:48`,
`Get(string id)` public static) — editing a template affects **every nozzle using it**. Document
this prominently in the SPEC and `help`.

**Enumeration:** `VolumetricExhaustTemplate.References` is `internal static readonly
SerializedCollection<VolumetricExhaustTemplate>` (`:37`) → one cached reflection read
(`GetList()`); fall back to harvesting ids from live nozzles if reflection fails (health-latch,
degrade gracefully).

### Tree

```
debug/engineplume/
  help                       StaticTextFile (family readme: template scope, propagation, reset)
  templates/                 DelegateDirectory over snapshot PlumeTemplates
    <id>/
      <field leaves per the table below, in nested dirs>
      json                   whole-entity doc (Fields dict as JSON, one line per field)
      reset                  TriggerFile → debug.engineplume_reset (Token=<id>)
```

### Field table (all rooted at `VolumetricExhaustTemplate`; editor: `KSA/VolumetricExhaustRenderer.cs:1991-2337`)

| Key | Kind | Range | Target member (all public) |
|---|---|---|---|
| `core/radius_weight` | Number | 0.0001–100 | `.LengthWeights.RadiusWeight.Value` |
| `core/nozzle_pressure_weight` | Number | 0.0001–100 | `.LengthWeights.NozzlePressureWeight.Value` |
| `core/jet_expansion_weight` | Number | 0.0001–100 | `.LengthWeights.JetExpansionWeight.Value` |
| `core/exit_mach_weight` | Number | 0.0001–100 | `.LengthWeights.ExitMachNumberWeight.Value` |
| `absorption/density` | Number | 0.0001–100 | `.Absorption.Density.Value` |
| `absorption/fake_clean_burn` | Flag | 0/1 | `.Absorption.FakeCleanBurnInAtmosphere.Value` |
| `absorption/scattering_brightness` | Number | 0–100 | `.Absorption.ScatteringBrightness.Value` |
| `absorption/phase_eccentricity` | Number | −1–1 | `.Absorption.ScatteringPhaseEccentricity.Value` |
| `absorption/refraction_intensity` | Number | 0–10 | `.Absorption.RefractionIntensity.Value` |
| `emission/brightness` | Number | 0–200 | `.Emission.Brightness.Value` |
| `emission/color0`..`color3` | Color3 | 0–1/ch | `.Emission.ColorGradient.Color{0..3}` (`ColorRgbReference` — **construct new + `OnDataLoad`**, §7) |
| `mach_diamonds/lead_in` | Number | 0–1 | `.Emission.Flow.MachDiamonds.LeadIn.Value` |
| `mach_diamonds/lead_out` | Number | 0–1 | `.Emission.Flow.MachDiamonds.LeadOut.Value` |
| `mach_diamonds/middle_radius` | Number | 0–1 | `.Emission.Flow.MachDiamonds.MiddleRadius.Value` |
| `noise/density_strength` | Number | 0–2 | `.Noise.DensityNoise.Intensity.Value` |
| `noise/density_size` | Number | 0–100 | `.Noise.DensityNoise.Size.Value` |
| `noise/radial_strength` | Number | 0–2 | `.Noise.RadialShapeNoise.Intensity.Value` |
| `noise/radial_barrel_shock` | Number | 0–4 | `.Noise.RadialShapeNoise.BarrelShockIntensity.Value` |
| `noise/radial_speed` | Number | 0–100 | `.Noise.RadialShapeNoise.Speed.Value` |
| `noise/radial_size` | Number | 0–100 | `.Noise.RadialShapeNoise.Size.Value` |
| `noise/shape_strength` | Number | 0–2 | `.Noise.ShapeNoise.Intensity.Value` |
| `noise/shape_size` | Number | 0–100 | `.Noise.ShapeNoise.Size.Value` |
| `quality/samples` | Number | 1–100 (int) | `.Quality.SampleCount.Value` |
| `quality/self_shadow_samples` | Number | 0–10 (int) | `.Quality.SelfShadowSampleCount.Value` |
| `quality/vessel_shadows` | Flag | 0/1 | `.Quality.VolumetricVesselShadows` (plain bool) |

**Deferred (documented in SPEC as such):** startup/shutdown transient durations + curves
(`CubicHermiteSpline` + a private-field LUT re-bake), test grid, wireframe debug.

### Apply — the propagation loop (MANDATORY after each write; `VolumetricExhaustRenderer.cs:2316-2337`)

```csharp
foreach (Vehicle v in Universe.CurrentSystem.All.OfType<Vehicle>())
    foreach (var m in v.Parts.RocketNozzles.ModulesAndAllStates)
    {
        VolumetricExhaustInstance inst = m.FxState.VolumetricExhaust;
        if (inst != null)
        {
            inst.OnSettingsChanged();                        // public
            inst.UpdateModifiers(pressure, throttle);        // public — see note
            m.Module.RecomputeGasVisibilityDensity(in inst); // public
        }
    }
```

The editor passes its private `_currentAtmosphericPressure`/`_debugThrottle` to
`UpdateModifiers`; those drive test-grid rendering — for live nozzles the per-frame update path
re-derives them, so the actuator may pass the instance's current values or re-read them
reflectively; verify against `VolumetricExhaustInstance.UpdateModifiers` (`:179`) semantics and
prefer the choice that doesn't disturb live plumes. Skip the `TransientAnimationLut` re-bake —
only needed for the deferred transient curves.

## 3. `/sim/debug/plumetrail` — the volumetric trail renderer (global)

**Scope: global singleton.** Instance: `Program.Instance` (public static) →
`_volumetricTrailRenderer` (**private field**, `KSA/Program.cs:160`) — one cached reflection
read. All v1 fields below are **public instance fields** on `VolumetricTrailRenderer`
(`KSA/VolumetricTrailRenderer.cs:173-196`, editor `:1006-1065`). Values are read fresh each
frame by the renderer — **no apply call**.

### Tree

```
debug/plumetrail/
  help
  render/<leaves per table>
  clear        TriggerFile → debug.plumetrail_clear   (Program.ClearPlumeTrails(), public static, KSA/Program.cs:4610)
  reset        TriggerFile → debug.plumetrail_reset   (restore pristine captured values)
```

### Field table

| Key | Kind | Range | Target (public field) | Default |
|---|---|---|---|---|
| `render/max_distance` | Number | 0.01–1e7 m | `MaxDistance` | 800000 |
| `render/voxel_first_slice` | Number | 0.001–100000 m | `VoxelDepthFirstSliceThickness` | 10 |
| `render/min_step_size` | Number | 0.001–100000 m | `MinStepSize` | 1 |
| `render/step_size_distance_scale` | Number | 0–10 | `StepSizeDistanceScale` | 0.002 |
| `render/expansion_time` | Number | 0.001–10000 s | `ExpansionTimeSeconds` | 5 |
| `render/erosion_max_depth` | Number | 0–1 | `ErosionMaxDepth` | 0.8 |
| `render/erosion_edge_sharpness` | Number | 0–0.999 | `ErosionEdgeSharpness` | 0.95 |
| `render/self_shadow_steps` | Number | 0–64 (int) | `SelfShadowStepCount` | 4 |
| `render/light_brightness` | Number | 0–1000 | `LightBrightness` | 7 |
| `render/sky_ambient_brightness` | Number | 0–1000 | `SkyAmbientBrightness` | 3 |
| `render/trail_color` | Color4 | 0–1/ch | `DebugTrailColor` (float4 RGBA) |  |

**Deferred:** `UseNoiseAntiTiling`/`DebugMode` (trigger `RebuildFrameResources()`), all
`PlumeTrailSegmentsManager` private sim/LOD/wind fields (19 reflected fields — tier 2).

## 4. `/sim/debug/clouds` — per-body cloud layers

**Scope: per-body → per-layer → per-cloud-type.** Data root (zero reflection):
`AtmosphericBody.BodyTemplate.CloudsReference` (`KSA/AstronomicalTemplate.cs:60`, public).
Editor: `KSA.Atmosphere.Rendering/CloudRenderer.cs:1311-1595`.

Renderer access for the apply path: `Program.Instance` → `_planetTransparenciesRenderer`
(private, `KSA/Program.cs:152`) → `GetCloudRenderer()` (public,
`KSA/PlanetTransparenciesRenderer.cs:87`) — one cached reflection read.

### Tree

```
debug/clouds/
  help
  bodies/                    DelegateDirectory over snapshot CloudBodies (bodies with a CloudsReference)
    <bodyId>/
      shared/{transition_start_km,transition_end_km,max_shadows_altitude_km}
      layers/<n>/{rotation_speed,detail_tile_km,color,scroll_speed}
      layers/<n>/two_d/{lambertian,color}
      layers/<n>/raymarch/{step_size,step_scale,max_step,light_distance,light_samples}
      layers/<n>/types/<m>/{start_altitude,height,density,edge_sharpness,multi_scatter,interpolate}
      json
      reset                  TriggerFile → debug.clouds_reset (Token=<bodyId>)
```

### Field table (rooted at `CloudsReference` / `Layers[n]` / `CloudTypes[m]`; all public)

| Key | Kind | Range | Target |
|---|---|---|---|
| `shared/transition_start_km` | Number | ≥0 km | `OrbitTransitionStartAltitude` = new `DistanceReference(v, Kilometers)` |
| `shared/transition_end_km` | Number | ≥0 km | `OrbitTransitionEndAltitude` (same idiom) |
| `shared/max_shadows_altitude_km` | Number | ≥0 km | `MaxShadowsAltitude` (same idiom) |
| `layers/*/rotation_speed` | Color3→**Vector3** (use Color3 kind arity-3; unit "vec3") | drag | `RotationSpeed` = new `Vector3Reference(v)` |
| `layers/*/detail_tile_km` | Number | ≥0 km | `VolumetricCloud.Detail.Size` = new `DistanceReference(v, Kilometers)` |
| `layers/*/color` | Color3 | 0–1/ch | `VolumetricCloud.ColorRgb` = new `ColorRgbReference(c)` + `OnDataLoad` |
| `layers/*/scroll_speed` | Number | 0–1e6 m/s | `VolumetricCloud.Noise.ScrollSpeed` = new `DoubleReference(v)` |
| `layers/*/two_d/lambertian` | Number | 0–1 | `TwoDimensionalCloud.Lambertian` = new `DoubleReference(v)` |
| `layers/*/two_d/color` | Color3 | 0–1/ch | `TwoDimensionalCloud.ColorRgb` (ColorRgb idiom) |
| `layers/*/raymarch/step_size` | Number | 0–1e6 m | `VolumetricCloud.Raymarching.Step.Size` (Distance idiom, Meters) |
| `layers/*/raymarch/step_scale` | Number | 0–1 | `…Raymarching.Step.Scale` (plain float) |
| `layers/*/raymarch/max_step` | Number | 0–1e6 m | `…Raymarching.Step.Maximum` (Distance idiom) |
| `layers/*/raymarch/light_distance` | Number | 0–1e6 m | `…Raymarching.LightDistance` (Distance idiom) |
| `layers/*/raymarch/light_samples` | Number | 0–20 (int) | `…Raymarching.LightSamples.Value` |
| `layers/*/types/*/start_altitude` | Number | −1e6–1e6 m | `CloudTypes[m].StartAltitude` (Distance idiom) |
| `layers/*/types/*/height` | Number | 0–1e6 m | `CloudTypes[m].Height` (Distance idiom) |
| `layers/*/types/*/density` | Number | 0–100 | `CloudTypes[m].Density` |
| `layers/*/types/*/edge_sharpness` | Number | 0–1 | `CloudTypes[m].EdgeSharpness` |
| `layers/*/types/*/multi_scatter` | Number | 0–1 | `CloudTypes[m].MultipleScatteringBrightness` |
| `layers/*/types/*/interpolate` | Flag | 0/1 | `CloudTypes[m].CloudShape.InterpolateShapes` (plain bool) |

**Deliberately EXCLUDED: `NoiseScale`** — changing it forces `RecreateLayerPipelines()`
(private, destroys/rebuilds Vulkan pipelines). Excluding it means the apply path never recreates
pipelines. Also deferred: shape/density splines, texture slots.

### Apply (after each write; `CloudRenderer.cs:1570-1595`)

```csharp
layer.OnDataLoad(Mod.Empty);                                  // public — recompute altitudes/colors
renderData[i].UpdateStaticData(_renderer, body, layers[i], start, end, maxShadowAlt);
_cloudShadowsRenderer.PopulatePlanets(_planetToCloudRenderData, _worleyNoise3dTarget);
```

`_planetToCloudRenderData` is a **public** field on `CloudRenderer`; `_renderer`,
`_cloudShadowsRenderer`, `_worleyNoise3dTarget` are private → cached reflection (health-latched;
on failure, degrade the *apply* but still perform the data write — the next natural repopulate
picks it up). Match the editor exactly: per-layer index `i` into
`_planetToCloudRenderData[keyHash]`.

**Idioms (binding):** `ColorRgbReference.Value` has a `protected set` — in-place mutation
silently does nothing; **construct new + `OnDataLoad(Mod.Empty)`** exactly as the editor does.
Same for `DistanceReference(v, unit)`, `DoubleReference(v)`, `Vector3Reference(v)`.

## 5. `/sim/debug/terrain` — per-body terrain (v1 subset)

**HARD family — scope tightly** (editor: `KSA/PlanetRenderer.cs:2062-2398`). Most fields need a
**paired write**: the reference object on `Celestial.BodyTemplate` **and** the GPU-mapped UBO
struct behind private `MappedMemory` fields (`_renderUboMap`/`_meshUboMap`/`_biomeDataMap`/
`_biomeMaterialsMap`, `PlanetRenderer.cs:250-258`), at the slot index from the public slot
helpers (`:374`, `:379`), **plus** the end-of-frame mirror copy into all `MaxFramesInFlight` UBO
mirrors (`:2388-2398`) or the change flickers. Instance: `Program.GetPlanetRenderer()` (public
static, `KSA/Program.cs:491`).

**v1 fields:**

| Key | Kind | Range | Reference target | UBO target |
|---|---|---|---|---|
| `wireframe` (global, not per body) | Flag | 0/1 | `PlanetRenderer.Wireframe` (public static-ish bool `:216`) | — |
| `bodies/<id>/min_height` | Number | −20000–0 m | `BodyTemplate.HeightReference.Minimum` = new `DistanceReference(v, Meters)` | `MeshUbo.MinHeight` |
| `bodies/<id>/max_height` | Number | 0–20000 m | `…HeightReference.Maximum` | `MeshUbo.MaxHeight` |
| `bodies/<id>/slope_roughness_deg` | Number | 0–90 deg | — | `PlanetUbo.TanMeanSlopeRoughnessRadians` (store radians) |
| `bodies/<id>/hapke_albedo` | Number | 0.0001–0.99999 | — | `PlanetUbo.HapkeMeanAlbedo` |
| `bodies/<id>/biomes/blend_strength` | Number | 1–10 | `TerrainReference.BiomeMaterials.BlendStrength.Value` | `PlanetUbo.BiomeBlendStrength` **and** `MeshUbo.BiomeBlendStrength` |
| `bodies/<id>/biomes/detail_fade_start_km` | Number | see editor | `BiomeMaterials.DetailFadeInStart` = new `DistanceReference(v*1000, Meters)` | `PlanetUbo.DetailFadeStartMeters` |
| `bodies/<id>/biomes/detail_fade_end_km` | Number | see editor | `BiomeMaterials.DetailFadeInEnd` | `PlanetUbo.DetailFadeEndMeters` |
| `bodies/<id>/tessellation/edge_length_px` | Number | 0.1–20 px | — | `PlanetUbo.TessellationEdgeLengthPixels` |
| `bodies/<id>/tessellation/factor` | Number | 0–1 | — | `PlanetUbo.TessellationFactor` |
| `bodies/<id>/tessellation/range_m` | Number | 1–20000 m | — | `PlanetUbo.TessellationRangeMeters` |

**Explicitly deferred:** per-biome material params, procedural modifiers, ground clutter /
ecotypes, BVH debug, exporters, debug mode.

**Implementation directives:** first check whether a public repopulate/invalidate path exists
that re-derives UBOs from the reference objects (`PlanetRenderer.cs:684-720`, `:1086-1114`) —
if a body's slot can be cheaply repopulated on demand, prefer that over raw UBO writes. Failing
that, implement the paired UBO write path faithfully (unsafe span writes over the reflected
`MappedMemory`, then the mirror-copy loop). ALL of terrain is wrapped in its own health-latch
accessor keys so a decomp drift degrades terrain only. Only bodies with a live render slot are
sampled/addressable; others are absent from `bodies/`.

## 6. Action keys, addressing, phase (game-free contract)

All **Frame** phase — none join `SimCommand.SolverActions`.

| Action | VesselId | Ordinal | Token | Aux | Values | Meaning |
|---|---|---|---|---|---|---|
| `debug.engineplume_set` | `""` | — | template id | field key | per spec arity | set one plume field |
| `debug.engineplume_reset` | `""` | — | template id | — | — | restore pristine template |
| `debug.plumetrail_set` | `""` | — | — | field key | per spec | set one trail field |
| `debug.plumetrail_reset` | `""` | — | — | — | — | restore pristine trail params |
| `debug.plumetrail_clear` | `""` | — | — | — | — | `Program.ClearPlumeTrails()` |
| `debug.clouds_set` | `""` | — | body id | field path (may contain indices) | per spec | set one cloud field |
| `debug.clouds_reset` | `""` | — | body id | — | — | restore pristine body clouds |
| `debug.terrain_set` | `""` | — | body id (`""` for `wireframe`) | field path | per spec | set one terrain field |
| `debug.terrain_reset` | `""` | — | body id | — | — | restore pristine body terrain |

All handled **vessel-agnostically** in `KsaCatalog.Execute` before vehicle resolution
(the thug_life precedent): route by prefix to a private router per family that
**re-validates via `FxCatalog.Match` + `IsValid`** (transport `POST /v1/command` bypasses the 9p
parse), then dispatches to the actuator.

**Errnos:** unknown field path → `Invalid`; unknown template/body id → `NotFound`; out of
range / wrong arity / non-finite → `Invalid`; reflection-degraded family → `Unsupported`
(health-latched); thrown → `Fault`.

**Reset semantics:** each actuator captures the pristine value of a field the first time gatOS
writes it (per entity, per field). `*_reset` writes all captured values back and clears the
capture. Reset with no captures = `Ok` no-op. Captures are runtime-only (session-scoped cheats,
like all of `/sim/debug`).

## 7. Implementation phases (each ends build + full test suite green, zero warnings)

**Phase A — game-free SimFs layer** (`gatOS.SimFs` + tests; no game types anywhere):
1. `Fx/FxCatalog.cs` (+`FxFieldSpec`, `FxKind`, `FxFieldMatch`) with the four tables above —
   include `Unit` + one-line `Doc` per field (feeds `help` and the SPEC).
2. `Snapshots/`: `FxEntitySnapshot`, `FxEditorsSnapshot`, `SimSnapshot.FxEditors` (init-only).
3. `SimFsTree`: `EnginePlumeDir()`, `PlumeTrailDir()`, `CloudsDir()`, `TerrainDir()` added to
   `DebugDir()`, all built generically from the catalog + snapshot (one shared private builder);
   `help` files; `json` docs; `reset`/`clear` triggers. Qid strings = relative paths.
4. Tests (`gatOS.SimFs.Tests/Commands/FxEditorsTreeTests.cs` + `FxCatalogTests.cs`): the four
   shapes of AGENTS.md §10 for representative leaves of each family (incl. an indexed cloud
   path), catalog match/validation tables, plus the tree-crawl guard extension
   (`SimFsTreeTests`) — seed `FxEditors` via `TestData`.

**Phase B — GameMod layer** (compiles only with KSA DLLs; all KSA names under `Game/Ksa/`):
1. `Game/Ksa/Fx/` : `PlumeActuator`, `TrailActuator`, `CloudActuator`, `TerrainActuator`,
   shared `FxReflect` (cached `FieldInfo`/accessor helpers + health-latch integration) and
   pristine-capture registry. Every KSA-touching member `[KsaAnchor]`-annotated
   (GameVersion 2026.7.10.5056, Verified 2026-08-01, honest ChurnRisk — terrain/clouds High).
2. `Game/Ksa/Readers/FxEditorReader.cs`: samples `FxEditorsSnapshot` per §1's memoization rule;
   wired into `TelemetrySampler.BuildSnapshot` gated on the debug namespace (pass the flag into
   the sampler from `Mod`).
3. `KsaCatalog`: prefix routes for the four families before vehicle resolution.
4. Teardown: reset-all on `Mod.TeardownGameCheats` (restore every pristine capture).

**Phase C — docs lockstep** (AGENTS.md §9, all in this change):
SPEC §3.7 subtree tables + prose blocks + §5.1 rows; `docs/KSA_INTEGRATION_MATRIX.md` new
section; `scope/FULL_SCOPE.md` inventory + census; `scope/ksa-write-surface.md` new
`{#fx-editors}` sections; `scope/ksa-read-surface.md` (FxEditorReader); `scope/ksa-runtime-coupling.md`
(the reflection accessors: `_volumetricTrailRenderer`, `_planetTransparenciesRenderer`,
cloud apply privates, terrain UBO maps, `VolumetricExhaustTemplate.References`);
`CLAUDE.md` status row; `docs/VALIDATION.md` checklist section; `docs/MILESTONES.md` as-built.
