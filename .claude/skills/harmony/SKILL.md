---
name: harmony
description: 'Expert in use of Harmony Lib for runtime patching of dotnet C# code'
license: MIT
---


# Overview

Use HarmonyLib effectively by following best practices for the library.

This skill has two parts:

1. **KSA Modding Patterns** — how Harmony is actually wielded in THIS repository's KSA game mods. Read this first; it reflects the conventions every mod here follows and the real game types/methods that get patched.
2. **Harmony Library Reference** — a general AI-optimized reference for the library's full feature set.

---

# KSA Modding Patterns (this repo)

> These are the conventions used across ~40 patch files in this project. Follow them when creating or modifying a KSA mod. The verbatim code below is quoted from real mods.

## Mod lifecycle: the `Patcher.cs` contract

Every top-level mod has a `Patcher.cs` with a static `Patcher` class exposing exactly two methods that the StarMap mod loader calls **by name** (not by attribute or interface):

```csharp
public static void Patch()    // called on mod load — apply all patches here
public static void Unload()   // called on mod unload — remove all patches here
```

There is **no** `[Patch]`/`[Unpatch]`/`ModInfo` attribute on these methods. The loader invokes `Patch()` and `Unload()` by convention.

### The Harmony instance

- Stored as a single nullable **static field**: `private static Harmony? _harmony;`
- The ID is a **string literal**, either the mod folder name (`"zippo"`, `"flexo"`, `"thug-life"`) or a fully-qualified namespace (`"MeowSci.Blinky"`, `"MeowSci.Unscience"`). Pick one and reuse the exact same string for `UnpatchAll`.
- Created either inline (`= new Harmony("zippo")`) or lazily in `Patch()` (`_harmony = new Harmony("MeowSci.Blinky")`). Lazy creation pairs naturally with `_harmony = null` in `Unload()`.

### HotkeyGuard is mandatory (see CLAUDE.md)

Every top-level mod MUST apply `HotkeyGuard` from `MeowSci.KsaAbstractions`. Patch it in `Patch()`, unpatch it **first** in `Unload()`:

```csharp
HotkeyGuard.Patch(_harmony);    // in Patch()
HotkeyGuard.Unpatch(_harmony);  // in Unload(), before other unpatches
```

### Two patching styles

**Style A — attribute discovery via `PatchAll`** (use when patches are declared with `[HarmonyPatch]` attributes on classes/methods in the assembly). Put `[HarmonyPatch]` on the `Patcher` class to make its own annotated methods discoverable:

```csharp
[HarmonyPatch]
internal static class Patcher
{
    private static Harmony? _harmony = new Harmony("zippo");

    public static void Patch()
    {
        try
        {
            _harmony?.PatchAll(typeof(Patcher).Assembly);
            if (_harmony != null) HotkeyGuard.Patch(_harmony);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"zippo: Error applying patches: {ex.Message}");
        }
    }

    public static void Unload()
    {
        try
        {
            if (_harmony != null) HotkeyGuard.Unpatch(_harmony);
            _harmony?.UnpatchAll("zippo");   // same literal as the Harmony id
            _harmony = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"zippo: Error removing patches: {ex.Message}");
        }
    }
}
```

**Style B — manual `Apply(harmony)` / `Remove(harmony)` helpers** (the dominant style for `.lib` projects; gives precise control and clean unpatching). Each feature lives in a `*Patches.cs` static class with paired `Apply`/`Remove`:

```csharp
internal static class Patcher
{
    private static Harmony? _harmony;

    public static void Patch()
    {
        _harmony = new Harmony("MeowSci.Blinky");
        BlinkyPatches.Apply(_harmony);
        HotkeyGuard.Patch(_harmony);
    }

    public static void Unload()
    {
        if (_harmony != null)
        {
            BlinkyPatches.Remove(_harmony);
            HotkeyGuard.Unpatch(_harmony);
        }
        _harmony = null;
    }
}
```

The two styles compose: `flexo` calls `PatchAll(...)` AND `FlexoPatches.Apply(...)` (the latter needed because one patch requires a custom priority that attributes don't set here). The `unscience` supermod consolidates many `.lib` patch sets under **one** Harmony instance by calling each lib's `Apply`/`Remove` in turn.

## The `*Patches.cs` manual-patch helper (Style B canonical form)

This is the most important pattern in the repo. A static helper resolves the target `MethodInfo`s, stores them (plus the prefix/postfix `MethodInfo`s) in static fields, patches them, and unpatches by the **same stored references**:

```csharp
public static class IFeelSeenPatches
{
    private static VehicleTracker? _tracker;
    private static MethodInfo? _vehicleGetWorldMatrix;
    private static MethodInfo? _getWorldMatrixPrefix;

    public static void Apply(Harmony harmony, VehicleTracker tracker)
    {
        _tracker = tracker;
        // resolve our own private patch method by name
        _getWorldMatrixPrefix = typeof(IFeelSeenPatches)
            .GetMethod(nameof(GetWorldMatrixPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        // resolve the private game method by string name (AccessTools ignores visibility)
        _vehicleGetWorldMatrix = AccessTools.Method(typeof(Vehicle), "GetWorldMatrix");
        harmony.Patch(_vehicleGetWorldMatrix, prefix: new HarmonyMethod(_getWorldMatrixPrefix));
        Console.WriteLine("i-feel-seen.lib: patches applied");
    }

    public static void Remove(Harmony harmony)
    {
        if (_vehicleGetWorldMatrix != null && _getWorldMatrixPrefix != null)
            harmony.Unpatch(_vehicleGetWorldMatrix, _getWorldMatrixPrefix);  // targeted unpatch
        _tracker = null;
        _vehicleGetWorldMatrix = null;
        _getWorldMatrixPrefix = null;
        Console.WriteLine("i-feel-seen.lib: patches removed");
    }

    private static bool GetWorldMatrixPrefix(Vehicle __instance, Camera camera, ref float4x4? __result)
    {
        if (_tracker == null || !_tracker.IsTracked(__instance))
            return true;                 // not ours — run the original
        // ... compute custom matrix ...
        __result = /* ... */;
        return false;                    // skip original, our __result is returned
    }
}
```

Key points:
- `harmony.Patch(original, prefix: new HarmonyMethod(methodInfo))` is the manual patch call. Use the `prefix:` or `postfix:` named arg.
- `Remove` uses `harmony.Unpatch(original, prefixMethodInfo)` for a **targeted** unpatch (preferred in `.lib` helpers so it never touches another lib's patches). Top-level `Patcher.cs` files instead use `harmony.UnpatchAll("<id>")`.
- `Apply` can take extra context args (e.g. a `VehicleTracker`) which the prefix reads from a static field.

## HotkeyGuard — the canonical shared prefix (study this)

`ksa-abstractions.lib/HotkeyGuard.cs` is the reference implementation of a clean, reusable, target-a-game-method patch. It blocks game hotkeys while ImGui has text focus by patching `GameSettings.OnKeyAll` and forcing its `bool` return value:

```csharp
public static void Patch(Harmony harmony)
{
    _original = AccessTools.Method(typeof(GameSettings), nameof(GameSettings.OnKeyAll));
    _prefix = typeof(HotkeyGuard).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
    harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
    Console.WriteLine("ksa-abstractions: HotkeyGuard patch applied");
}

private static bool Prefix(ref bool __result)
{
    if (!Program.ConsoleWindow.IsOpen && ImGui.GetIO().WantTextInput)
    {
        __result = true;   // tell the game "the key was handled"
        return false;      // skip the real OnKeyAll → hotkeys don't fire
    }
    return true;           // otherwise let hotkeys work normally
}
```

This is the textbook "conditionally skip original and substitute the return value" prefix: `ref bool __result` + `return false`.

## Injected arguments seen in this repo

| Injected | Real usage here |
|----------|-----------------|
| `__instance` | `Vehicle __instance`, `PartModel __instance`, `Camera __instance`, `PartModelModule __instance`, `Controller __instance` — the game object being patched |
| `ref __result` | `ref bool __result` (HotkeyGuard), `ref float4x4? __result` (i-feel-seen) — write it then `return false` to override |
| named original args | `Camera camera`, `Viewport viewport`, `int inFrameIndex`, `CommandBuffer commandBuffer` — match the game method's real parameter names |
| `ref <param>` | `ref PartModel.PerInstanceData instanceData` — mutate a struct argument in place (paint, deform, emissive) |
| `___PrivateField` | `Transform3D ___Transform` on `Controller.OnFrame` — triple-underscore reads a private game field directly into the patch |
| positional | `PartModel.PerInstanceData __0`, `Viewport __1` (IvaForceRender) — by-index when you don't want to name them |

## Holding patch state: static `*PatchState` / manager singletons

Patch methods are static, so mutable config lives in a dedicated static class the prefix checks at runtime:

```csharp
// prefix gates rendering on a static flag toggled by the mod's UI
private static bool PartModelModulePrefix(PartModelModule __instance)
{
    if (BlinkyPatchState.RenderPixelParts) return true;
    return !__instance.Parent.FullPart.Id.StartsWith("pixel_");
}
```

`BlinkyPatchState` / `ShinyPatchState` are plain static classes holding `bool` toggles. For render hooks that need GPU resources, the pattern is a **manager singleton** with static `Active`/`Instance` that the postfix dispatches into, e.g. `ThugLifeRenderManager`:

```csharp
private static void RenderMainPassPostfix(CommandBuffer commandBuffer)
{
    if (!ThugLifeRenderManager.Active) return;          // cheap guard first
    try { ThugLifeRenderManager.Instance?.RecordDraws(commandBuffer); }
    catch (Exception ex) { Console.WriteLine($"thug-life: render postfix error: {ex.Message}"); }
}
```

Note: `Dispose()` on the manager sets `Active = false` **before** freeing GPU resources, so an in-flight render frame can't use-after-free.

## KSA-specific techniques worth knowing

**Render-data injection via `Unsafe.As` struct reinterpretation.** Several mods (humble-arteest paint & emissive, mesh-deform) write into the unused padding bytes of the game's per-instance GPU struct. Define an overlay struct with `[StructLayout(LayoutKind.Sequential)]` matching the real layout, reinterpret the `ref` param, and assign the named fields:

```csharp
private static void AddInstancePrefix(PartModel __instance, ref PartModel.PerInstanceData instanceData)
{
    if (!VehiclePaint.TryGetEffectiveColor(__instance, out var color)) return;
    ref var paintable = ref Unsafe.As<PartModel.PerInstanceData, PaintablePerInstanceData>(ref instanceData);
    paintable.PaintR = color.X;   // PaintR/G/B overlay the struct's packing1/2/3 bytes
    paintable.PaintG = color.Y;
    paintable.PaintB = color.Z;
}
```

**Two-patch coordination via `ThreadLocal<T>`.** When the data you need to inject (in a deep patch) isn't available in that method's args, capture it in an earlier patch and stash it thread-locally. mesh-deform captures the current `Part` in a prefix on `PartModelModule.UpdateRenderData`, then reads it in a prefix on `PartModel.AddInstance`. Safe because KSA renders single-threaded:

```csharp
public static readonly ThreadLocal<Part?> CurrentPart = new();
// patch 1: CurrentPart.Value = __instance.Parent;
// patch 2: var part = CapturePartPatch.CurrentPart.Value;  // who is being rendered right now
```

**Writing a private game field via `AccessTools.Field` + `SetValue`.** glass overrides the camera FOV by resolving `Camera._fovRadians` once in `Apply` and writing it in a prefix on `UpdateProjection`:

```csharp
_fovRadiansField = AccessTools.Field(typeof(Camera), "_fovRadians");   // once, in Apply
// in prefix:
_fovRadiansField.SetValue(__instance, targetRadians);
```

**Patching a constructor.** IvaForceRender patches `PartModel`'s constructor to mutate newly-created part templates:

```csharp
_ctorOriginal = AccessTools.Constructor(typeof(PartModel), new[] { typeof(PartModelModule.Template) });
harmony.Patch(_ctorOriginal, postfix: new HarmonyMethod(_ctorPostfix));
```

**Setting patch priority for ordering.** When your prefix must run before everything else on a hot game method (flexo's solver on `Universe.ExecuteNextVehicleSolvers`), set priority on the `HarmonyMethod`:

```csharp
var prefix = new HarmonyMethod(typeof(FlexoSolverPatch), nameof(BeforeVehicleSolvers))
{
    priority = Priority.First
};
harmony.Patch(original, prefix: prefix);
```

**Calling a private game method via `Traverse`.** flexo invokes a private recompute after editing parts:

```csharp
Traverse.Create(vehicle.Parts).Method("RecomputeStaticMass").GetValue();
```

**Transpiler (rare — only jplrepo uses one).** Injects a call before a specific ImGui call inside a game method, with a fallback warning if the IL target isn't found:

```csharp
foreach (var instr in instructions)
{
    if (!injected && instr.Calls(setCursorPosY))
    {
        yield return new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(Patcher), nameof(SaveMenuCursorPos)));
        injected = true;
    }
    yield return instr;
}
```

**Attribute-based menu-bar postfix.** UI mods append to the game menu by postfixing `Program.DrawProgramMenusHook` (or `GaugeCanvas.OnDrawMenuBar`), often applied with `harmony.CreateClassProcessor(typeof(MyMenuPatch)).Patch()`:

```csharp
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
[HarmonyPostfix]
private static void Postfix() { /* ImGui.BeginMenu(...) ... */ }
```

## Logging & error-handling conventions

- Log with `Console.WriteLine` (per CLAUDE.md). Standard messages: `"<mod>: Harmony patches applied"` / `"<mod>: patches removed"`, and on failure `"<mod>: Error applying patches: {ex.Message}"`.
- Wrap `Patch()`/`Unload()` bodies in try/catch so a failed patch can't take down mod loading.
- Wrap the **body of render/hot-path prefixes & postfixes** in try/catch too — an exception thrown from inside a patch on a per-frame game method would otherwise spam or destabilize the game loop.

## Real game types & methods patched in this repo (targeting reference)

| Game type.method | Patch | What the mod does |
|---|---|---|
| `GameSettings.OnKeyAll` | prefix (skip) | HotkeyGuard — block hotkeys during ImGui text input |
| `Vehicle.GetWorldMatrix` / `Vehicle.UpdateRenderData` | prefix (skip) | i-feel-seen — override render transform/distance for tracked vehicles |
| `Camera.ChangeFieldOfView` | prefix (skip) | glass — block stock FOV input when override active |
| `Camera.UpdateProjection` | prefix + field write | glass — inject custom FOV via `Camera._fovRadians` |
| `PartModel.AddInstance` | prefix (`ref PerInstanceData`) | humble-arteest paint, mesh-deform — inject GPU per-instance data |
| `PartModelDynamic.AddInstance` | prefix (`ref PerInstanceData`) | humble-arteest engine emissive temperature/TFI |
| `PartModel` ctor / `PartModel.AddInstance` | postfix | IvaForceRender — force interior meshes visible |
| `PartModelModule.UpdateRenderData` (+ Dynamic/Glass variants) | prefix (skip) | blinky/shiny — conditionally skip rendering `pixel_`/`shiny_` parts; mesh-deform Part capture |
| `PartModelRenderer.UpdateRenderData(Viewport, int)` | prefix | flexo — render editor parts in main pass (note arg-type overload match) |
| `Universe.ExecuteNextVehicleSolvers` | prefix (`Priority.First`) | flexo — run hinge solver before vehicle solvers |
| `SuperMeshRenderSystem.RenderMainPass` | postfix | thug-life — record extra quad draws into the command buffer |
| `Controller.OnFrame` (Orbit/Fly) | prefix (`___Transform`) | camera-controller-override — keyframe camera playback |
| `Program.DrawProgramMenusHook` / `GaugeCanvas.OnDrawMenuBar` | postfix | menu-bar UI injection |

To find the real signatures of any of these, look in `decomp/ksa` (read strategically — those files are large).

---

# Harmony Library Reference (AI-Optimized)

> HarmonyLib runtime patching library for .NET/Mono. Patches methods without modifying DLLs on disk.

## Core Concepts

- **Patches coexist**: Multiple Harmony patches on the same method do not conflict
- **Patch methods MUST be static**: Instance state stored in static variables
- **Unique ID required**: Use reverse domain notation (e.g., `com.example.mymod`)

## Setup

```csharp
using HarmonyLib;

// Create instance
var harmony = new Harmony("com.example.mymod");

// Apply all annotated patches in assembly
harmony.PatchAll();

// Or manual patching
harmony.Patch(originalMethod, 
    prefix: new HarmonyMethod(typeof(MyPatch).GetMethod("Prefix")),
    postfix: new HarmonyMethod(typeof(MyPatch).GetMethod("Postfix")));
```

## Patch Types

### 1. Prefix
Runs **before** original. Can skip original and modify arguments.

```csharp
[HarmonyPatch(typeof(TargetClass), "TargetMethod")]
class MyPatch
{
    // Return false to skip original (and subsequent prefixes with side effects)
    // Return true or void to continue
    static bool Prefix(ref int someArg, ref int __result)
    {
        someArg = 10;        // Modify argument (needs ref)
        __result = 42;       // Set return value (needs ref)
        return false;        // Skip original
    }
}
```

### 2. Postfix
Runs **after** original. Always runs (unless exception thrown). Preferred for compatibility.

```csharp
static void Postfix(ref int __result, int someArg)
{
    __result *= 2;  // Modify return value (needs ref)
}

// Pass-through postfix (for IEnumerable or special cases)
static IEnumerable<T> Postfix(IEnumerable<T> __result)
{
    foreach (var item in __result)
        yield return ModifyItem(item);
}
```

### 3. Finalizer
Wraps original + all patches in try/catch. Handles/suppresses exceptions.

```csharp
// Suppress all exceptions
static Exception Finalizer() => null;

// Observe exception
static void Finalizer(Exception __exception)
{
    if (__exception != null) Log(__exception);
}

// Replace exception
static Exception Finalizer(Exception __exception)
{
    return __exception != null ? new CustomException(__exception) : null;
}
```

### 4. Transpiler
Modifies IL instructions at patch time (not runtime). Advanced use only.

```csharp
static IEnumerable<CodeInstruction> Transpiler(
    IEnumerable<CodeInstruction> instructions,
    ILGenerator generator,      // Optional: for labels/locals
    MethodBase original)        // Optional: original method info
{
    var codes = new List<CodeInstruction>(instructions);
    // Modify codes list
    return codes;
}
```

**CodeMatcher** utility for transpilers:
```csharp
static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    return new CodeMatcher(instructions)
        .MatchStartForward(new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(Foo), "Bar")))
        .SetOperandAndAdvance(AccessTools.Method(typeof(MyClass), "MyReplacement"))
        .InstructionEnumeration();
}
```

### 5. Reverse Patch
Copies original method into your stub. Call private/protected methods directly.

```csharp
[HarmonyPatch(typeof(TargetClass), "PrivateMethod")]
class MyPatch
{
    // Stub signature must match original (instance methods need instance as first param)
    [HarmonyReversePatch]
    static int CallPrivateMethod(TargetClass instance, int arg)
    {
        // Stub body replaced by original's IL
        throw new NotImplementedException("Stub");
    }
}
// Usage: int result = MyPatch.CallPrivateMethod(targetInstance, 5);
```

Types: `HarmonyReversePatchType.Original` (default, unpatched) or `Snapshot` (with existing transpilers)

## Injected Arguments

| Name | Type | Description |
|------|------|-------------|
| `__instance` | class type | `this` for instance methods |
| `__result` | return type | Return value (use `ref` to modify) |
| `__state` | any | Share state between Prefix/Postfix (same class only) |
| `___fieldName` | field type | Private field access (3 underscores, use `ref` to modify) |
| `__args` | `object[]` | All arguments array |
| `__originalMethod` | `MethodBase` | Original method info (cannot call it) |
| `__runOriginal` | `bool` | Whether original will/did run |
| `someArg` | matches original | By name (use `ref` to modify) |
| `__0`, `__1`, etc. | matches original | By index |

## Annotations

### Target Specification

```csharp
// Basic - type + method name
[HarmonyPatch(typeof(MyClass), "MyMethod")]

// With argument types (for overloads)
[HarmonyPatch(typeof(MyClass), "MyMethod", new Type[] { typeof(int), typeof(string) })]

// Property getter/setter
[HarmonyPatch(typeof(MyClass), "PropertyName", MethodType.Getter)]
[HarmonyPatch(typeof(MyClass), "PropertyName", MethodType.Setter)]

// Constructor
[HarmonyPatch(typeof(MyClass), MethodType.Constructor)]
[HarmonyPatch(typeof(MyClass), MethodType.Constructor, new Type[] { typeof(int) })]

// Generics - must patch specific closed type
[HarmonyPatch(typeof(MyClass<string>), "Method")]

// Split across multiple attributes
[HarmonyPatch(typeof(MyClass))]
[HarmonyPatch("MyMethod")]
[HarmonyPatch(new Type[] { typeof(int) })]
```

### Patch Method Attributes

```csharp
[HarmonyPrefix]      // or name method "Prefix"
[HarmonyPostfix]     // or name method "Postfix"  
[HarmonyTranspiler]  // or name method "Transpiler"
[HarmonyFinalizer]   // or name method "Finalizer"
```

### Priority & Ordering

```csharp
[HarmonyPriority(Priority.High)]     // Run earlier (higher = earlier)
[HarmonyPriority(Priority.Low)]      // Run later
[HarmonyBefore("other.mod.id")]      // Run before specific mod
[HarmonyAfter("other.mod.id")]       // Run after specific mod
```

Priority values: `First=0`, `VeryHigh=100`, `High=200`, `Higher=300`, `Normal=400`, `Lower=500`, `Low=600`, `VeryLow=700`, `Last=800`

## Auxiliary Methods

```csharp
[HarmonyPatch(typeof(MyClass), "MyMethod")]
class MyPatch
{
    // Called before patching; return false to skip this patch class
    static bool Prepare(MethodBase original, Harmony harmony)
    {
        return original != null; // Example: only patch if method exists
    }
    
    // Dynamic target selection (replaces [HarmonyPatch] target)
    static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(SomeClass), "SomeMethod");
    }
    
    // Multiple targets
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(A), "Method");
        yield return AccessTools.Method(typeof(B), "Method");
    }
    
    // Called after patching; can handle/suppress exceptions
    static Exception Cleanup(MethodBase original, Exception ex)
    {
        return null; // Suppress exception
    }
}
```

## Utilities

### AccessTools
Reflection helper. All methods ignore visibility (public/private/etc).

```csharp
AccessTools.Method(typeof(MyClass), "MethodName")
AccessTools.Method(typeof(MyClass), "MethodName", new[] { typeof(int) })
AccessTools.Field(typeof(MyClass), "fieldName")
AccessTools.Property(typeof(MyClass), "PropertyName")
AccessTools.Constructor(typeof(MyClass), new[] { typeof(int) })
AccessTools.Inner(typeof(MyClass), "NestedClassName")
AccessTools.TypeByName("Namespace.ClassName")
```

### Traverse
Fluent reflection with null-safety.

```csharp
// Read private field
var value = Traverse.Create(instance).Field("_privateField").GetValue<int>();

// Set private field
Traverse.Create(instance).Field("_privateField").SetValue(42);

// Call private method
Traverse.Create(instance).Method("PrivateMethod", arg1, arg2).GetValue();

// Chain access
var deep = Traverse.Create(obj).Field("a").Property("B").Field("c").GetValue();
```

## Execution Order

1. **Prefixes** (highest priority first)
   - Void/no-ref prefixes always run (side-effect free)
   - First `return false` skips remaining prefixes with side effects AND original
2. **Original** (possibly transpiled)
3. **Postfixes** (lowest priority first) - always run unless exception
4. **Finalizers** (lowest priority first) - always run, handle exceptions

## Edge Cases & Limitations

| Issue | Cause | Workaround |
|-------|-------|------------|
| Patch not called | Method inlined by JIT | Patch caller instead, or use `[MethodImpl(MethodImplOptions.NoInlining)]` if you control the code |
| Generics shared | Reference types share implementation | Check `__instance.GetType()` in patch; value types usually not shared |
| Can't patch static constructor | Runs before patching | Time patching carefully, or accept it runs at wrong time |
| Can't patch native/extern | No IL to modify | Transpiler-only patch returning new implementation (loses ability to call original) |
| MissingMethodException (Unity) | Patching before Unity initialization | Delay patching until after scene load |
| base.Method() not working | Resolved at compile time | Use Reverse Patch |
| InvalidProgramException | Method has no RET instruction | Use Transpiler to fix IL |

## Debugging

```csharp
// Enable debug logging (writes to Desktop/harmony.log.txt)
Harmony.DEBUG = true;

// Or per-patch
[HarmonyDebug]
[HarmonyPatch(...)]
class MyPatch { }

// Environment variables
// HARMONY_NO_LOG=1        - Disable logging
// HARMONY_LOG_FILE=path   - Custom log path
```

## Common Patterns

### Skip Original Conditionally
```csharp
static bool Prefix(SomeType __instance)
{
    if (__instance.ShouldSkip)
    {
        return false; // Skip original
    }
    return true; // Run original
}
```

### Wrap Original in Try/Catch
```csharp
static Exception Finalizer(Exception __exception)
{
    if (__exception != null)
        Logger.Error($"Caught: {__exception}");
    return null; // Suppress
}
```

### Replace Method Call in Original
```csharp
static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    return new CodeMatcher(instructions)
        .MatchStartForward(new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(Original), "OldMethod")))
        .SetOperandAndAdvance(AccessTools.Method(typeof(MyClass), "NewMethod"))
        .InstructionEnumeration();
}
```

### Access Private Nested Type
```csharp
static MethodBase TargetMethod()
{
    var nestedType = AccessTools.Inner(typeof(OuterClass), "PrivateNestedClass");
    return AccessTools.Method(nestedType, "TargetMethod");
}
```

### Patch All Overloads
```csharp
static IEnumerable<MethodBase> TargetMethods()
{
    return typeof(MyClass)
        .GetMethods(AccessTools.all)
        .Where(m => m.Name == "OverloadedMethod");
}
```
