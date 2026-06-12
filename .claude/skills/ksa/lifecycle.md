# Mod Scaffolding, Lifecycle & Threading

The structural conventions every mod in this repo follows. Replicate these when creating a new mod.

## Two-project split: `<name>` + `<name>.lib`

Every mod is **two** projects:

- `<name>/` — assembly `MeowSci.<Name>`. A thin StarMap host. Contains `Mod.cs` (lifecycle attributes), `Patcher.cs` (Harmony), `mod.toml`.
- `<name>.lib/` — assembly `MeowSci.<Name>Lib`. **All logic** lives here as an `ISubmod` plus a shared static Harmony-patch class.

This split is what lets a mod be **both** standalone AND consumed by the `unscience` supermod. Shared state must live in `.lib` assemblies (see ALC sharing in SKILL.md).

## StarMap host (`Mod.cs`)

```csharp
[StarMapMod]
public class Mod
{
    public bool ImmediateUnload => false;          // required property
    private MyThingSubmod _submod = new();
    private bool _windowVisible = false;

    [StarMapImmediateLoad] public void OnImmediateLoad() { }
    [StarMapAllModsLoaded] public void OnFullyLoaded() { _submod.Initialize(); Patcher.Patch(); }
    [StarMapBeforeGui]     public void OnBeforeUi(double dt) { _submod.Update(dt); }
    [StarMapAfterGui]      public void OnAfterUi(double dt) {
        if (ImGui.IsKeyPressed(ImGuiKey.F11)) _windowVisible = !_windowVisible;
        if (_windowVisible) { /* ImGui.Begin(...); _submod.RenderContent(); ImGui.End(); */ }
    }
    [StarMapUnload]        public void Unload() { Patcher.Unload(); _submod.Dispose(); }
}
```

Guard every lifecycle method body with try/catch and `Console.WriteLine($"<mod-id>: ...")` on error.

### Lifecycle ordering — where to do what

| Hook | Use for |
|---|---|
| `[StarMapImmediateLoad]` | Almost nothing. The renderer is NOT live yet — do not call `Program.GetRenderer()`. |
| `[StarMapAllModsLoaded]` | Apply Harmony patches, build GPU resources, set static `Instance`. Renderer is live here. |
| `[StarMapBeforeGui]` (dt) | **Per-frame compute / sampling / state mutation.** Drain the game-thread queue here. |
| `[StarMapAfterGui]` (dt) | ImGui rendering, hotkey toggles (`ImGui.IsKeyPressed`). |
| `[StarMapUnload]` | Unpatch Harmony, dispose GPU resources (reverse construction order), null static `Instance`. |

## Patcher.cs

```csharp
[HarmonyPatch]
internal static class Patcher
{
    private static Harmony? _harmony;

    public static void Patch()
    {
        try
        {
            _harmony ??= new Harmony("my-mod-id");
            HotkeyGuard.Patch(_harmony);              // REQUIRED for every mod (see CLAUDE.md)
            MyThingPatches.Apply(_harmony);           // shared static patch class in the .lib
        }
        catch (Exception ex) { Console.WriteLine($"my-mod-id: Error applying patches: {ex.Message}"); }
    }

    public static void Unload()
    {
        try
        {
            if (_harmony != null)
            {
                MyThingPatches.Remove(_harmony);
                HotkeyGuard.Unpatch(_harmony);
                _harmony = null;
            }
        }
        catch (Exception ex) { Console.WriteLine($"my-mod-id: Error removing patches: {ex.Message}"); }
    }
}
```

- One `Harmony` instance per mod, named with the mod id string.
- `PatchAll(typeof(Patcher).Assembly)` + `UnpatchAll("id")` is the minimal alternative (see `fixme-mod-name`), but **real mods prefer a shared static `Apply(Harmony)`/`Remove(Harmony)` patch class in the `.lib`** so the supermod can apply it to its own instance.
- A no-patch mod still creates a Harmony just for `HotkeyGuard` (e.g. `skittles`).
- For manual patches with priority: `harmony.Patch(AccessTools.Method(typeof(X), "Y"), prefix: new HarmonyMethod(m) { priority = Priority.First });`

## ISubmod (from `ksa-abstractions.lib`)

```csharp
public interface ISubmod
{
    string Name { get; }                    // header / menu label
    string Tooltip { get; }                 // hover tooltip
    void Initialize();                      // once; usually assigns the static Instance
    void Update(double dt);                 // every frame, pre-UI compute (runs even when hidden)
    void RenderContent();                   // ImGui content WITHOUT Begin/End — host frames it
    void RenderFloatingWindows() { }        // default no-op; own Begin/End windows; always called
    void Dispose();                         // teardown; null the static Instance
}
```

`RenderContent()` must NOT call `ImGui.Begin/End` — the host wraps it (in a `CollapsingHeader` for the supermod, or a `Begin/End` window for the standalone host). Use `RenderFloatingWindows()` for windows you own entirely (e.g. a part-editor scene window).

## The dual standalone + submod pattern (KEY)

The same `ISubmod` class serves both the standalone host and the `unscience` supermod. The rule:

**Logic lives in the `.lib`, exposed via (a) a static `Instance` singleton and (b) a static `Apply/Remove` patch class — so either host can drive it with its own Harmony instance.**

```csharp
public class MyThingSubmod : ISubmod
{
    public static MyThingSubmod? Instance { get; private set; }
    public void Initialize() { Instance = this; /* ... */ }
    public void Dispose() { if (ReferenceEquals(Instance, this)) Instance = null; }
}

// In the .lib — applied by BOTH the standalone Patcher and unscience's Patcher:
internal static class MyThingPatches
{
    public static void Apply(Harmony h) { /* h.Patch(...) */ }
    public static void Remove(Harmony h) { /* h.Unpatch(...) */ }
}

// The patch body dispatches to the singleton (thread-safe: same game thread):
static void Postfix(/* ... */) => MyThingSubmod.Instance?.DoTheThing();
```

The `unscience` supermod owns a **single** `new Harmony("MeowSci.Unscience")`, calls `HotkeyGuard.Patch` once, then one `Apply()` per submod on that instance. Its `OnBeforeUi` loops `Update(dt)` over all submods (even hidden), `OnAfterUi` renders visible ones.

## GameThread scheduler — mutating game state from off-thread

KSA game state has **thread affinity** — only mutate it on the game thread. Background/HTTP threads (e.g. the `unladen-swallow` RPC server) must enqueue work and let the game thread drain it.

```csharp
// ksa-abstractions.lib/GameThread.cs
public static class GameThread
{
    public static IGameStateScheduler Scheduler { get; }   // background threads enqueue here
    public static void DrainOnGameThread();                 // call once per frame on the game thread
}

// IGameStateScheduler:
Task Schedule(Action action);        // completes after the action runs on the game thread
Task<T> Schedule<T>(Func<T> func);   // resolves to the return value
```

- **Off-thread (HTTP handler):** `var result = await GameThread.Scheduler.Schedule(() => { /* touch game state */ return x; });`
- **Game thread:** call `GameThread.DrainOnGameThread()` from a submod's `Update(dt)` (which the supermod calls in `OnBeforeUi`).

Backed by a `ConcurrentQueue<WorkItem>` + `TaskCompletionSource` (`RunContinuationsAsynchronously`); exceptions on the game thread fault the returned task.

## Solver-timing hooks — `Universe.ExecuteNextVehicleSolvers`

Some state must be set **before the physics solvers run each sim step**, not in the render loop (the render frame and the sim step run at different cadences). Examples: refilling battery charge so the sim sees it (`eternal-flame`), driving robotics transforms before the solver reads them (`flexo`).

```csharp
var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
harmony.Patch(original, prefix: new HarmonyMethod(myPrefix) { priority = Priority.First });
// prefix calls e.g. MySubmod.Instance?.UpdateBeforeVehicleSolvers();
```

- `Universe.ExecuteNextVehicleSolvers(double dtPlayer, ...)` is the static per-sim-step entry that runs all vehicle update tasks.
- Use `priority = Priority.First` so you win the race, and wrap the prefix body in try/catch so a throw can't break the sim step.
- Rate-limit solver-prefix work with wall-clock `Environment.TickCount64` (solver frequency ≠ render frequency), and render-loop work with accumulated `dt`.

## Build / project conventions

- Repo-root `Directory.Build.props` holds shared props: `net10.0`, `LangVersion 13.0`, `Nullable enable`, `TreatWarningsAsErrors` (except CS1591), and the `KSAFolder` / `KSAUserModDir` / `SelectedDistModDir` paths.
- Standalone csproj: `<OutputType>Library</OutputType>`, `<AssemblyName>MeowSci.<Name></AssemblyName>`, `<DistDir>$(SelectedDistModDir)<mod-name>\</DistDir>`.
- PackageReferences (both `PrivateAssets="all"`): `StarMap.API`, `Lib.Harmony`.
- ProjectReferences: `..\<name>.lib\...` and `..\ksa-abstractions.lib\...` (the latter pulls in `HotkeyGuard`, `GameThread`, `KsaPaths`, providers).
- KSA game DLLs are `<Reference HintPath="$(KSAFolder)...">` with `<Private>false</Private>` (not copied). Common: `KSA`, `Brutal.Core.Common`, `Brutal.Core.Numerics`, `Brutal.Core.Strings`, `Brutal.ImGui`, `Brutal.ImGui.Abstractions`. Render mods add `Brutal.Vulkan*`, `Brutal.Core.Memory`, `Planet.Render.Core`.
- The `CopyCustomContent` target (`AfterTargets="AfterBuild"`) copies `mod.toml`, the entry DLL/pdb/deps.json, **all** `MeowSci.*.dll/pdb` (the lib + abstractions), `LICENSE`, and `third-party-licenses/` into `$(DistDir)`.
- Build the whole solution with `dotnet build` and ensure it compiles before a task is complete.

## mod.toml

```toml
name = "my-mod"
description = "what it does"
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "MeowSci.MyMod"          # the standalone assembly, NEVER the .lib

# optional: expose assemblies to other mods
# [StarMap]
# ExportedAssemblies = ["MeowSci.MyModLib"]

# optional: depend on another mod's lib (see ALC sharing in SKILL.md)
[[StarMap.ModDependencies]]
ModId = "zippo"
Optional = true
ImportedAssemblies = ["MeowSci.ZippoLib"]
```

`mod.toml` is copied to output with `<None Update="mod.toml"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None>`.

## Conventions

- **Logging:** `Console.WriteLine($"<mod-id>: ...")` always. Wrap lifecycle/patch bodies in try/catch that logs.
- **Window-toggle hotkey:** F11 is the default (`ImGui.IsKeyPressed(ImGuiKey.F11)` in `OnAfterUi`). Deviations in use: F8 (doh), F9 (glass, kiwis-marbles), F12 (thug-life).
- **Persistence:** `KsaPaths.UserDataDir` = `MyDocuments\My Games\Kitten Space Agency`. This repo persists under a `.unscience` subfolder, as TOML via Tomlyn (see SKILL.md TOML section).
- **HotkeyGuard is mandatory** — see SKILL.md and CLAUDE.md. Patch in `Patch()`, unpatch in `Unload()`.
