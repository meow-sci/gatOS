---
name: ksa
description: detailed information about kitten space agency (KSA) game code, decompiled sources, modding patterns, architecture and best practices for working with mods and the game codebase
---

# KSA Mod Structure

**StarMap is a mod loader only.** It is used to run the game and link mods in at runtime. The only interaction with StarMap is through the C# lifecycle attribute annotations on the mod class — there is no other StarMap API to use.

Mods are C# 10 classes decorated with StarMap attributes:

```csharp
using StarMap.API;
using KSA;

[StarMapMod]
public class Mod
{
  public bool ImmediateUnload => false;

  [StarMapImmediateLoad]  public void OnImmediateLoad() { }
  [StarMapAllModsLoaded]  public void OnFullyLoaded() { Patcher.Patch(); }
  [StarMapBeforeGui]      public void OnBeforeUi(double dt) { }
  [StarMapAfterGui]       public void OnAfterUi(double dt) { }
  [StarMapUnload]         public void Unload() { Patcher.Unload(); }
}
```

These attributes are the **complete** StarMap interface. Do not attempt to call other StarMap APIs or use StarMap for anything beyond these lifecycle hooks.

- HarmonyLib patching is done in `Patcher.cs`; call `Patcher.Patch()` in `OnFullyLoaded` and `Patcher.Unload()` in `Unload`
- Use `Console.WriteLine` for logging
- Guard all lifecycle methods with try/catch and log errors

### Lifecycle ordering (where to do what)

| Hook | Use for |
|---|---|
| `[StarMapImmediateLoad]` | Almost nothing — the renderer is NOT live (don't call `Program.GetRenderer()`). |
| `[StarMapAllModsLoaded]` | Apply Harmony patches, build GPU resources, set static `Instance`. Renderer is live. |
| `[StarMapBeforeGui]` (dt) | Per-frame compute / sampling / state mutation; drain the game-thread queue. |
| `[StarMapAfterGui]` (dt) | ImGui rendering, hotkey toggles (`ImGui.IsKeyPressed(ImGuiKey.F11)`). |
| `[StarMapUnload]` | Unpatch Harmony, dispose GPU resources (reverse order), null static `Instance`. |

**For the full scaffolding playbook** — the two-project `<name>` + `<name>.lib` split, `Patcher.cs`, `mod.toml`, `.csproj`/`Directory.Build.props`, the `ISubmod` interface, the dual standalone+submod static-`Instance` pattern, the `GameThread` off-thread scheduler, and solver-timing hooks (`Universe.ExecuteNextVehicleSolvers`) — see [lifecycle.md](lifecycle.md).

### Solver-timing hooks (cross-cutting)

State that must be visible to the **physics solvers each sim step** (battery charge, robotics transforms) cannot be set in the render loop — the render frame and sim step run at different cadences. Patch a `priority = Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers` instead. See [lifecycle.md](lifecycle.md), [telemetry.md](telemetry.md), [robotics.md](robotics.md).

## Topic map

| Topic | File |
|---|---|
| Mod scaffolding, lifecycle, ISubmod, GameThread scheduler, solver hooks | [lifecycle.md](lifecycle.md) |
| Vehicle telemetry, resources (fuel/battery refill), Situation, flight events | [telemetry.md](telemetry.md) |
| Vehicle physics data, render override, engine control | [vehicle-api.md](vehicle-api.md) |
| Parts/SubParts rendering, raycasting, mouse picking | [parts.md](parts.md) |
| GPU materials, kitten tinting/spawning, vehicle paint shader swap, engine emissive | [materials.md](materials.md) |
| Lights, solar panels, KeyframeAnimationModule, runtime LightPart creation | [lights.md](lights.md) |
| Robotics — rotating parts at runtime (hinges/rotors) | [robotics.md](robotics.md) |
| HUD GaugeCanvas, View-menu injection, celestial/orbit reparenting, ImGui theming | [gauges-orbits.md](gauges-orbits.md) |
| Camera controller patching | [camera.md](camera.md) |
| In-world textured quad (render-to-texture) | [quad.md](quad.md) |
| Top-level game menu injection | [game-menus.md](game-menus.md) |
| KittenEva avatar (animations, expressions, scaling) | [kitten-eva.md](kitten-eva.md) |
| Runtime reflection debugging | [debug.md](debug.md) |

## Researching KSA Game APIs

When you need to understand game types, APIs, or behavior:
- **Prefer the decompiled sources** in `decomp/ksa/` — they contain all available information and are much easier to read
- Do **not** attempt to inspect DLL files directly using shell commands or reflection tooling — use the decompiled sources instead

> **Important:** The decompiled sources may be outdated. The running binary can have a completely different internal structure — field names that appear in decompiled code may not exist at runtime. When in doubt, use the runtime reflection dump strategy to discover the real structure. See [debug.md](debug.md).

## Runtime Debugging

When decompiled source field names don't match the actual binary (reflection returns `null`, counts show `-1`, etc.):

- Use an ImGui **Dbg button** to trigger a reflection dump at runtime
- Walk the object graph, printing `GetType().FullName` and all fields via `BindingFlags.Public | NonPublic | Instance | DeclaredOnly`
- Pay special attention to `List<T>` / `IList` fields — the game may store typed components in a generic `Components` list rather than named fields
- Save the console output to a file (e.g. `<mod>/DEBUG`) for offline analysis

See [debug.md](debug.md) for complete helper code, the `DumpPartsWithComponents` pattern, and a worked example of how `LightModule+TemplateData` was discovered inside `PartTemplate.Components`.

## Cross-Mod Assembly Sharing

StarMap loads each mod into its own `AssemblyLoadContext` (ALC). By default, two mods that both compile against the same `.lib` project will each get **independent copies** of that assembly with **separate static state**.

To share an assembly (and its static state) across mods, declare a dependency in `mod.toml`:

```toml
[[StarMap.ModDependencies]]
ModId = "blinky"
Optional = true
ImportedAssemblies = [
    "MeowSci.BlinkyLib"
]
```

When the dependent mod's ALC tries to load an assembly whose name appears in `ImportedAssemblies`, it **delegates to the dependency mod's ALC** — returning the exact same `Assembly` object. Same Assembly → same `Type` objects → same static fields → shared singleton state.

The dependency mod can optionally declare which assemblies it exposes:

```toml
# in the dependency mod's mod.toml
[StarMap]
ExportedAssemblies = ["MeowSci.BlinkyLib"]
```

### Resolution Matrix

| Dependent sets `ImportedAssemblies` | Dependency sets `ExportedAssemblies` | Shared assemblies |
|---|---|---|
| No | No | Entry assembly only |
| **Yes** | No | Exactly what `ImportedAssemblies` lists |
| No | Yes | Everything in `ExportedAssemblies` |
| Yes | Yes | Intersection of both lists |

### Architecture Rules

1. **Shared state goes in `.lib` assemblies only** — the mod entry assembly (e.g. `MeowSci.Blinky`) is private and never imported by other mods.
2. **`ImportedAssemblies` lists `.lib` assembly names** — e.g. `"MeowSci.BlinkyLib"`, not `"MeowSci.Blinky"`.
3. **Use `Optional = true`** so each mod remains independently installable. Guard code paths that depend on the other mod being present.
4. **Transitive `.lib` deps may need importing too** — if `blinky.lib` → `ksa-abstractions.lib` and both mods need the same `GameThread` static state, import `MeowSci.KsaAbstractions` as well.
5. **Build-time references still needed** — the `.csproj` `<ProjectReference>` to the `.lib` project provides compile-time types. At runtime, `ImportedAssemblies` redirects the load to the dependency's ALC instead of loading the local copy.

# Universe & Vehicles

```csharp
var vehicles = Universe.CurrentSystem?.Vehicles.GetList(); // List<Vehicle>
Vehicle? controlled = Program.ControlledVehicle;           // currently player-controlled vehicle
double simTime = Universe.GetElapsedSimTime();
```

- `vehicle.Id` — string identifier
- `vehicle.Parent` — celestial body the vehicle orbits; must match between vehicles for teleport operations to be valid
- `vehicle.BodyRates` — `double3` angular velocity (rad/s); guard against NaN before use
- `vehicle.Body2Cce` — direct `doubleQuat` property (body frame → body-fixed frame)
- `vehicle.Orbit` — current orbital state; use `vehicle.Orbit.OrbitLineColor` when creating new orbits
- `vehicle.IsEditedVehicle` — `bool`, true when in VAB/editor

For physics data (AccelerationBody, NavBallData, FlightComputer, TotalMass, render override patching) see [vehicle-api.md](vehicle-api.md).

## Time

```csharp
var elapsed = Universe.GetElapsedSimTime(); // returns a time value
double seconds = elapsed.Seconds();          // convert to double seconds
```

## Celestial Body Properties

```csharp
vehicle.Parent.Mass        // double — body mass (kg)
vehicle.Parent.MeanRadius  // double — body mean radius (m)
vehicle.Parent.GetCci2Cce() // doubleQuat — CCI-to-CCE frame rotation
```

# Parts

## Regular Vehicles

Top-level parts are accessed via `vehicle.Parts.Parts`. Each `Part` has a `SubParts` collection forming a tree. Recurse to reach all parts:

```csharp
void SetPartScaleRecursive(Part part, float factor)
{
    part.Scale = new double3(factor, factor, factor);
    foreach (var sub in part.SubParts)
        SetPartScaleRecursive(sub, factor);
}

// Apply to all parts on a vehicle:
foreach (var part in vehicle.Parts.Parts)
    SetPartScaleRecursive(part, factor);
```

`part.Scale` is a `double3` — set all three axes to the same value for uniform scaling.

### Part Properties

- `part.Id` — string identifier (e.g. `"pixel_3_7_a"`)
- `part.DisplayName` — human-readable name
- `part.IsSubPart` — whether this is a child subpart
- `part.PartParent` — parent `Part` in the tree (nullable)
- `part.TreeChildren` — `IList<Part>` direct children

### Modules & Components

Parts contain typed modules accessed via generic `Get<T>()` calls. These return a **`Span<T>`** — check `.Length`, index `[0]`:

```csharp
// Modules of type T on THIS part only:
Span<LightModule> lights = part.Modules.Get<LightModule>();

// Modules of type T on this part AND its sub-parts (aggregated):
Span<EngineController> engines = part.SubtreeModules.Get<EngineController>();

// Modules of type T across the ENTIRE vehicle:
Span<EngineController> all = vehicle.Parts.Modules.Get<EngineController>();
```

- `Modules` = this part only; `SubtreeModules` = part + sub-parts. Use `SubtreeModules` to ask "does this top-level part carry a light/engine/anim anywhere".
- `part.FullPart` resolves an inner sub-part to its owning top-level part — fetch subtree modules off `part.FullPart ?? part`.
- **Addressing parts: prefer `Part.InstanceId` (uint, runtime-unique) over `Part.Id` (string, can collide** across instances of the same template). To resolve an InstanceId, iterate `vehicle.Parts.Parts` matching it.

Module **mutable state** may live in a struct-of-arrays separate from the module object (e.g. `Battery`/`BatteryState`). You write it through a `ref`-returning accessor, not a property — see the `ModuleStateful` pattern in [telemetry.md](telemetry.md).

After modifying module state (e.g. activating/deactivating engines), call:

```csharp
vehicle.Parts.RecomputeAllDerivedData();
```

For engine control see [vehicle-api.md](vehicle-api.md); for lights/solar/keyframe animation see [lights.md](lights.md); for rotating parts at runtime see [robotics.md](robotics.md).

## Dynamically Adding Parts at Runtime

Parts can be created and merged into a live vehicle's part tree at runtime using `PartTree.Merge()`. This is the same mechanism the vehicle editor uses, so the game handles it correctly.

### Basic Part Creation and Merge

```csharp
// 1. Look up the PartTemplate from ModLibrary. 
//    IMPORTANT: Use Get<PartTemplate>(), NOT TryGet<PartTemplate>()
//    TryGet does NOT support PartTemplate and always returns false.
//    Get throws NullReferenceException if the id is unknown.
PartTemplate template;
try { template = ModLibrary.Get<PartTemplate>("CorePropulsionA_Prefab_EngineA2"); }
catch (Exception ex) { Console.WriteLine($"template not found: {ex.Message}"); return; }

// 2. Create the Part with a unique string id and the template
var part = new Part("my_part_id", template);

// 3. Set position relative to the parent part (in parent's assembly frame)
part.PositionParentAsmb = new double3(x, y, z);
part.Asmb2ParentAsmb = new doubleQuat(0, 0, 0, 1); // identity = no rotation

// 4. Merge into the vehicle tree under a parent part
//    Merge() internally calls RecomputeAllDerivedData() once.
//    Returns false if the merge failed.
Part parentPart = vehicle.Parts.Root;  // or any other valid Part
bool ok = vehicle.Parts.Merge(parentPart, part);
```

The part template ID is the exact string key used in the game's XML — e.g. `CorePropulsionA_Prefab_EngineA2`. List available IDs with the debug reflection approach shown in debug.md.

### Adding Engine Parts (Resource Consumers)

Engines require **two extra steps** beyond a basic Merge, or they will never fire:

**The problem:** `PartTree.Merge()` wires the tree hierarchy but does NOT create `Part.Connection` objects. The game's `ResourceManager` builds its propellant-flow graph by walking `Part.Connections` (not the tree). Without a connection from the engine to a fuel-carrying part, `ResourceManager.ResourceAvailable()` always returns `false` and the engine is starved every tick — `IsActive` is irrelevant.

**Step 1 — Establish a fuel connection:**

`Part` implements `IConnector`, so you can create a direct resource connection between two parts:

```csharp
// Find a part on the vehicle that has Tank modules (fuel / oxidizer)
Part? FindFuelPart(Vehicle vehicle)
{
    foreach (var p in vehicle.Parts.Parts)
        if (p.SubtreeModules.Get<Tank>().Length > 0 && !p.IsSubPart)
            return p;
    return null;
}

// After Merge(), connect the new engine part to the fuel part
Part? fuelPart = FindFuelPart(vehicle);
if (fuelPart != null)
{
    bool connected = Part.Connection.Connect(enginePart, fuelPart);
    // Part.Connection.Connect() is static and takes two IConnector arguments.
    // Part itself implements IConnector so you can pass Parts directly.
    // Returns false if either side is already connected or blocked.
}
```

`Part.Connection.Connect()` adds the connection to both parts' `Connections` lists. After this,  `ResourceManager.PopulateGraph()` (called inside `RecomputeAllDerivedData`) walks those connections and discovers the fuel tanks.

**Step 2 — Recompute after all connections are established:**

Each `Merge()` call already triggers `RecomputeAllDerivedData()`, but that runs *before* you call `Part.Connection.Connect()`. Call it explicitly once more after all connections are wired:

```csharp
vehicle.Parts.RecomputeAllDerivedData();
```

This rebuilds every `ResourceManager` graph, now including the new connections. Without this the engines will still be starved.

**Step 3 — Set MinimumThrottle after recompute:**

The `EngineController.MinimumThrottle` field prevents firing below a threshold. The default is `1.0` (full throttle only for SRBs) or `0.1` for liquid engines. You can lower it, but only after `Merge()` + `RecomputeAllDerivedData()` because `SubtreeModules.Get<EngineController>()` returns empty for a part that hasn't been through recompute yet:

```csharp
var controllers = enginePart.SubtreeModules.Get<EngineController>();
foreach (var c in controllers)
    c.MinimumThrottle = 0.0001f; // fire at any throttle above zero
```

**Step 4 — Activate with SetIsActive:**

```csharp
var controllers = vehicle.Parts.Modules.Get<EngineController>();
foreach (var c in controllers)
    if (c.Parent.Id.StartsWith("my_prefix_"))
        c.SetIsActive(null, true);
```

`SetIsActive` first argument is a nullable `Vehicle?` (not a "caller" object), pass `null`.

### Complete Dynamic Engine Add Pattern

```csharp
// 1. Template lookup
PartTemplate template;
try { template = ModLibrary.Get<PartTemplate>(enginePartId); }
catch { return; }

// 2. Create + position
var enginePart = new Part(uniqueId, template);
enginePart.PositionParentAsmb = new double3(x, y, z);
enginePart.Asmb2ParentAsmb = new doubleQuat(0, 0, 0, 1);

// 3. Merge into vehicle tree (triggers RecomputeAllDerivedData internally)
bool merged = vehicle.Parts.Merge(vehicle.Parts.Root, enginePart);
if (!merged) return;

// 4. Wire the resource connection so the engine can find fuel
Part? fuelPart = FindFuelPart(vehicle);  // finds first part with Tank modules
if (fuelPart != null)
    Part.Connection.Connect(enginePart, fuelPart);

// 5. Recompute AGAIN after connections are wired — this rebuilds ResourceManager graphs
vehicle.Parts.RecomputeAllDerivedData();

// 6. Now SubtreeModules is fully populated — set throttle limits
foreach (var c in enginePart.SubtreeModules.Get<EngineController>())
    c.MinimumThrottle = 0.0001f;

// 7. Activate
foreach (var c in enginePart.SubtreeModules.Get<EngineController>())
    c.SetIsActive(null, true);
```

### Removing Dynamically Added Parts

Disconnect resource connections first, then split the part from the tree:

```csharp
// Disconnect resource connections (prevents dangling graph references)
foreach (var conn in enginePart.Connections.ToList())
{
    try { conn.Disconnect(); } catch { }
}
// Split removes the part from the PartTree and triggers RecomputeAllDerivedData
vehicle.Parts.Split(enginePart);
```

### Resource Flow Architecture (Reference)

Understanding this prevents future mistakes:

- `Part.Connections` — list of `Part.Connection` objects; each connection links two `IConnector`s
- `Part.Connection.Connect(IConnector a, IConnector b)` — static factory; `Part` itself implements `IConnector`
- `ResourceManager` — created fresh per `RocketCore` during `RecomputeAllDerivedData()`
- `ResourceManager.PopulateGraph()` — starts at the engine's `FullPart` (= `PartParent ?? self`) and does a BFS over `Part.Connections` to discover `Tank` modules
- `FlowRule.NearestToFurtherestSameStage` — used for `EngineController`; only considers tanks in the same stage
- `ResourceAvailable()` — returns `false` if the graph is empty (no connections → no tanks found → engine always starved)
- The tree hierarchy (`TreeParent` / `TreeChildren`) is **irrelevant** to fuel flow — only `Connections` matter

## KittenEva (EVA Kitten/Kerbal)

`KittenEva` is a special vehicle subtype. Detect it via:

```csharp
vehicle.GetType().Name == "KittenEva"
```

KittenEva renders through `CharacterAvatar.Core.Scale` (a `float` where `0.01f` = 1:1 game scale, i.e. multiply your desired factor by `0.01f`). Access it via reflection since it is not part of the public `Vehicle` API:

```csharp
var allFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var renderable = vehicle.GetType().GetField("_renderable", allFlags)?.GetValue(vehicle);
var avatar = renderable?.GetType().GetField("_characterAvatar", allFlags)?.GetValue(renderable);
var coreField = avatar?.GetType().GetField("Core", allFlags);
var core = coreField?.GetValue(avatar);

// Try field first, then property
var scaleField = core?.GetType().GetField("Scale", allFlags);
var scaleProp  = core?.GetType().GetProperty("Scale", allFlags);
if (scaleField != null && scaleField.FieldType == typeof(float))
{
    scaleField.SetValue(core, factor * 0.01f);
    coreField!.SetValue(avatar, core); // write struct back
}
else if (scaleProp != null && scaleProp.PropertyType == typeof(float))
{
    scaleProp.SetValue(core, factor * 0.01f);
    coreField!.SetValue(avatar, core);
}
```

`vehicle.Parts.Parts` still iterates KittenEva parts but scaling them has no visual effect — the `Core.Scale` path above is what drives rendering. Apply both when doing a generic "scale any vehicle" implementation.

For the full KittenEva API including animations, expressions, and casting patterns see [kitten-eva.md](kitten-eva.md).

# 3D Positioning — Physics-Bypass Teleport

KSA uses double-precision coordinate frames:
- **CCI** (Celestial-Centered Inertial) — inertial absolute frame; used for positions and velocities
- **CCE** (Celestial-Centered Earth-fixed, i.e. body-fixed) — rotates with the parent body; used for orientation stored on vehicles
- **Body frame** — the vehicle's own local frame; `body2Cci` quaternion converts from it to CCI

To move a vehicle to an absolute position, bypassing all physics simulation, call `Teleport`. The pattern (e.g. "weld" source to target):

```csharp
double3 tgtPosCci     = target.GetPositionCci();
double3 tgtVelCci     = target.GetVelocityCci();
doubleQuat tgtBody2Cci = target.GetBody2Cci();

// Offset expressed in target's body frame (metres):
double3 offsetCci = new double3(offsetX, offsetY, offsetZ).Transform(tgtBody2Cci);
double3 newPosCci = tgtPosCci + offsetCci;

// Orientation: compose delta rotation with target orientation, then convert to CCE
doubleQuat deltaRot   = EulerDegreesToQuat(pitchDeg, yawDeg, rollDeg);
doubleQuat newBody2Cci = doubleQuat.Concatenate(deltaRot, tgtBody2Cci);
doubleQuat cci2Cce     = source.Parent.GetCci2Cce();
doubleQuat newBody2Cce = doubleQuat.Concatenate(newBody2Cci, cci2Cce).NormalizedOrZero();

Orbit newOrbit = Orbit.CreateFromStateCci(
    source.Parent,
    Universe.GetElapsedSimTime(),
    newPosCci,
    tgtVelCci,                 // match target velocity to stay co-moving
    source.Orbit.OrbitLineColor
);

source.Teleport(newOrbit, newBody2Cce, target.BodyRates);
```

Key points:
- `Teleport` takes `(Orbit, doubleQuat body2Cce, double3 bodyRates)` — it overwrites physics state completely each frame
- Always call `.NormalizedOrZero()` on computed quaternions before passing to `Teleport`
- `doubleQuat.Concatenate(q1, q2)` composes rotations (q2 applied first, then q1 — same convention as `Quaternion.Concatenate` in .NET)
- `source.Parent` must equal `target.Parent`; validate before teleporting or the coordinate math is invalid
- To maintain a locked relative position, call `Teleport` every frame (e.g. in `OnAfterUi`)
- Guard `BodyRates` for NaN, especially when rotation is unlocked: `if (double.IsNaN(rates.X) || ...) rates = double3.zero;`

## Euler to Quaternion (ZYX intrinsic)

```csharp
doubleQuat EulerDegreesToQuat(float pitchDeg, float yawDeg, float rollDeg)
{
    double cp = Math.Cos(pitchDeg * Math.PI / 360), sp = Math.Sin(pitchDeg * Math.PI / 360);
    double cy = Math.Cos(yawDeg   * Math.PI / 360), sy = Math.Sin(yawDeg   * Math.PI / 360);
    double cr = Math.Cos(rollDeg  * Math.PI / 360), sr = Math.Sin(rollDeg  * Math.PI / 360);
    var qPitch = new doubleQuat(sp, 0,  0,  cp);
    var qYaw   = new doubleQuat(0,  sy, 0,  cy);
    var qRoll  = new doubleQuat(0,  0,  sr, cr);
    return doubleQuat.Concatenate(doubleQuat.Concatenate(qYaw, qPitch), qRoll);
}
// doubleQuat constructor: (x, y, z, w)
```

# Colors

`KSAColor.Xkcd` provides named colors. Cast to `(float4)` for ImGui:

```csharp
ImGui.TextColored((float4)KSAColor.Xkcd.Custard, "label");
ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32((float4)KSAColor.Xkcd.HotPink));
```

Notable names: `Custard`, `RadioactiveGreen`, `Orangeish`, `GreenApple`, `OrangishRed`, `BrightMagenta`, `HotPink`, `CanaryYellow`, `BrightLightBlue`.

# Game Menu Bar — Adding Top-Level Menus

Custom top-level menus can be injected into the game's title bar menu (alongside File / Universe / View) using a Harmony **Transpiler** on `Program.DrawMenuBar`. This is the only viable approach because injection must happen inside the game's existing `ImGui.BeginMenuBar()` / `ImGui.EndMenuBar()` block. Setting `viewport.MenuBarInUse = true` inside the open menu is required to suppress game hotkeys and prevent the bar from auto-hiding.

See [game-menus.md](game-menus.md) for the complete pattern including the transpiler code, injection offset rationale, and all available ImGui menu calls.

To add items into the **existing View menu** (rather than a new top-level menu) the transpiler is overkill — a plain Harmony prefix on `GaugeCanvas.OnDrawMenuBar` (which the game calls inside the open View menu) works with no IL rewriting. See [gauges-orbits.md](gauges-orbits.md).

# Camera Controller Patching

KSA cameras (`OrbitController`, `FlyController`) can be intercepted via Harmony prefix on `OnFrame`. Return `false` to suppress default camera behavior. Camera uses **ECL (Ecliptic)** coordinates (distinct from vehicle CCI/CCE frames).

See [camera.md](camera.md) for full details including `Transform3D`, `Controller.Camera.Following`, orbit math, and look-at helpers.

# In-World Textured Quad (Render-to-Texture Surface)

Draw a flat textured quad in the scene, anchored to a `Part` / `SubPart` on a `Vehicle`, sampling any `VkImageView` (e.g. an offscreen render target). Reuses KSA's `UnlitMesh.{vert,frag}` (`vec3 pos + vec2 uv`, `mat4` MVP push constant, one combined-image-sampler). Injects the draw via a Harmony **postfix on `SuperMeshRenderSystem.RenderMainPass`** — that runs inside the already-begun offscreen pass on the active command buffer.

Key gotchas:
- Bind the pipeline to `Program.OffScreenPass` (NOT `Program.MainPass`) and set `RasterizationSamples = Program.OffScreenPass.SampleCount` — otherwise depth silently misbehaves under MSAA.
- Use `RenderingPresets.ReverseZDepthStencil.DepthTestWrite` (KSA's offscreen pass is reverse-Z) and `Presets.Rasterization.Fill.CullNone` (double-sided).
- Compose the model matrix in **ego space**: combine `part.PositionEgo(...)` + `part.Asmb2Ego(...)` with your own scale / rotation / offset; multiply by `camera.MVP.viewProjection` for the MVP push constant.
- Don't bake `part.MatrixAsmb2Ego` directly — it includes the part's scale, which you usually want to exclude.
- If the source texture is gamma-encoded (as ImGui output is), use `R8G8B8A8UNorm` for the source; SRGB formats double-decode and darken the result.

See [quad.md](quad.md) for the complete pipeline setup, vertex/index layout, per-frame model-matrix composition, ray-vs-quad picking via `Cursor.InputRay`, and lifecycle / disposal rules.

# Numerics

## Types

| Precision | Scalar | Vector | Matrix | Quaternion |
|-----------|--------|--------|--------|------------|
| 32-bit | `float` | `float2`, `float3`, `float4` | `float4x4` | `floatQuat` |
| 64-bit | `double` | `double3`, `double4` | `double4x4` | `doubleQuat` |

All from `Brutal.Numerics`.

## Common Operations

```csharp
double3.Normalize(v)     // normalize vector
v.Length()               // vector magnitude
double3.Dot(a, b)        // dot product
double3.Cross(a, b)      // cross product
double3.Lerp(a, b, t)    // linear interpolation (t ∈ [0,1])
doubleQuat.Slerp(a, b, t) // spherical linear interpolation
v.Transform(quat)        // rotate vector by quaternion
float3.Pack(in double3)  // double3 → float3
floatQuat.Pack(doubleQuat) // doubleQuat → floatQuat
float4x4.CreateTranslation(float3)
float4x4.CreateFromQuaternion(floatQuat)
```

# Audio

```csharp
var music = ModLibrary.Get<MusicPlayList>("AssetName");
music.PlayMusic(out ChannelWrapper? channel);

var sound = ModLibrary.Get<MultiSound>("AssetName");
sound.Play();
```

Assets are defined in an `Assets.xml` file in the mod directory.

# Persistence for mod state / data

Use the mods folder and the mod name that matches the mod folder name (the mods kebab case name)

```csharp
// common root for userland files
var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var userlandModsDir = Path.Combine(myDocuments, "My Games", "Kitten Space Agency", "mods");

// mod specific
var configDirectory = Path.Combine(userlandModsDir, "fixme-mod-name");
return Path.Combine(configDirectory, "FIXME_FILENAME_HERE");
```

# Input Chain & Focus Traps

The game processes keyboard input through a short-circuit chain in `Program.cs`:

```
GameSettings.OnKeyAll → Popup.OnKeyAll → ConsoleWindow.OnKey → ConsoleWindow.IsOpen → Editor?.OnKey → ...
```

If any handler returns `true`, all downstream handlers are skipped. This means a Harmony prefix on `GameSettings.OnKeyAll` that returns `true` will **block the in-game console** (`\` toggle, `Enter` submit) and all other handlers.

## Blocking Game Hotkeys for Mod Text Inputs

When a mod has `InputText` widgets, typing triggers game hotkeys. To block them **only** for your mod:

1. **Patch `GameSettings.OnKeyAll`** with a prefix that checks a mod-scoped flag
2. **Set the flag per-frame** inside your `Begin`/`End` blocks using `ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().WantTextInput`
3. **Never use `WantTextInput` alone** — it's a global flag that's `true` for any active text input, including the game's in-game console

See the ImGui skill for the full implementation pattern.

# TOML

How to use tomlyn with KSA

First inlclude the tomlyn nuget in the csharp project

```xml
<ItemGroup>
    <PackageReference Include="Tomlyn" Version="0.19.0"/>
</ItemGroup>
```

And in the mods `CopyCustomContent` csproj file section, add the Tomlyn dll as a reference so it gets copied to the mod output directory, for example

```xml
<Target Name="CopyCustomContent" AfterTargets="AfterBuild">
    <!-- other stuff -->
    <Copy SourceFiles="$(TargetDir)Tomlyn.dll" DestinationFolder="$(DistDir)"/>
</Target>
```

How to import and use

```csharp
using Tomlyn;
using Tomlyn.Model;

// example loading

// Use TryToModel for graceful error handling with Tomlyn
if (!Toml.TryToModel<TomlTable>(tomlContent, out var tomlTable, out var diagnostics))
{
    foreach (var diagnostic in diagnostics)
    {
        // Console.WriteLine($"TOML parsing error in {filePath}: {diagnostic}");
    }
    return null;
}

```
