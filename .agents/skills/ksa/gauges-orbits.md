# HUD Gauges, Orbits/Celestials & ImGui Theming

`GaugeCanvas` (HUD widgets), the View-menu injection point, celestial/orbit reparenting, and global ImGui style. All types in namespace `KSA`.

## GaugeCanvas — HUD gauge save/restore (con-man)

`GaugeCanvas : GaugeBase` is the HUD gauge widget. Useful members:

```csharp
public static IReadOnlyList<GaugeCanvas> AllCanvases { get; }   // registry of ALL canvases
public bool Enabled { get; }                                    // getter only — no public setter
public string Id { get; }                                       // stable per-gauge id (from SerializedId)
```

**Gotcha — visibility/offset/scale have NO public setters.** Drive them via reflection on private instance fields:

```csharp
const BindingFlags I = BindingFlags.NonPublic | BindingFlags.Instance;
_enabled       = typeof(GaugeCanvas).GetField("_enabled", I);        // bool — visibility
_customOffset  = typeof(GaugeCanvas).GetField("_customOffset", I);   // float2 — drag delta from base pos
_customScale   = typeof(GaugeCanvas).GetField("_customScale", I);    // float2 — resize scale (default One)
_windowPosition= typeof(GaugeCanvas).GetField("_windowPosition", I); // float2 — base pos
_windowSize    = typeof(GaugeCanvas).GetField("_windowSize", I);     // float2 — base size
_windowTitle   = typeof(GaugeCanvas).GetField("_windowTitle", I);    // "<name>###<DebugTitle>"
// enumerate all: GetField("_canvases", BindingFlags.NonPublic|BindingFlags.Static) as List<GaugeCanvas>
```

The game recomputes base values every frame from current ImGui window state:

```
_windowPosition = ImGui.GetWindowPos() - _customOffset;   // resolution-aware base
_windowSize     = ImGui.GetWindowSize() / _customScale;
// on user drag/resize: _customOffset = pos - _windowPosition;  _customScale = size / _windowSize;
```

So `_customOffset`/`_customScale` are **deltas** — store those to keep layouts resolution-independent, keyed on `canvas.Id`.

**Apply gotcha:** setting the reflection fields is not enough to move an already-visible window (`SetNextWindowPos/Size` with `Appearing` only fires on first appear). Force it by window title with `ImGuiCond.Always`:

```csharp
var basePos = GetWindowPosition(canvas); var baseSize = GetWindowSize(canvas);
SetEnabled(canvas, state.Enabled);
SetCustomOffset(canvas, new float2(state.OffsetX, state.OffsetY));
SetCustomScale(canvas, new float2(state.ScaleX, state.ScaleY));
ImGui.SetWindowPos(windowTitle, basePos + offset, ImGuiCond.Always);
ImGui.SetWindowSize(windowTitle, baseSize * scale, ImGuiCond.Always);
```

## View-menu injection — `GaugeCanvas.OnDrawMenuBar`

`GaugeCanvas.OnDrawMenuBar()` is a `public static void` the game calls **from inside the already-open "View" menu**. So a plain Harmony **prefix** appends your items to the View menu with zero IL rewriting:

```csharp
[HarmonyPatch(typeof(GaugeCanvas), nameof(GaugeCanvas.OnDrawMenuBar))]
[HarmonyPrefix]
public static void OnDrawMenuBarPrefix() => MyLib.DrawMyMenus();
// body: ImGui.BeginMenu("Mine") ... EndMenu(); then ImGui.Separator();
```

**When to use which menu approach:**
- **Prefix/postfix on `GaugeCanvas.OnDrawMenuBar`** → cleanest way to add items into the existing **View** menu. No transpiler.
- **`Program.DrawMenuBar` transpiler** (see game-menus.md) → only when adding a *new top-level* menu in the bar.

**Sticky-menu UX:** to keep a menu open after each click, wrap items in `ImGui.PushItemFlag(ImGuiItemFlags.AutoClosePopups, false)` … `PopItemFlag()`.

## Celestials, IOrbiter & Orbit

`IOrbiter` is the common interface for celestials AND vehicles:

```csharp
public interface IOrbiter : IFollowable, IObjectId, IPosition, IVelocity, IOrientation, IRadius, ... {
    Orbit Orbit { get; }
    IParentBody Parent => Orbit.Parent;     // default impl — parent derives from the orbit
    ref bool ShowOrbit { get; }             // orbit-line visibility (assign normally: o.ShowOrbit = true)
    byte4 OrbitColor { get; }
    double3 GetPositionCci(); double3 GetVelocityCci();   // + Ecl/Cce/Orb variants
}
```

`Celestial : Astronomical, IOrbiter, IParentBody`:

```csharp
public Orbit Orbit { get; set; }                          // has a public setter
public IParentBody Parent => Orbit.Parent;                // parent IS the orbit's parent
public override double MeanRadius { get; }
public override string Class => (Parent is StellarBody) ? "Planet" : "Moon";
public void SetOrbit(Orbit newOrbit) => Orbit = newOrbit; // JUST sets Orbit — reparents implicitly
public override bool IsMoon() => !(Parent is StellarBody);
```

**`SetOrbit` reparents for free** — because `Parent => Orbit.Parent`, assigning an `Orbit` built with a different parent re-parents the body. There is no separate `SetParent` call.

### Enumerating / traversing

```csharp
Universe.CurrentSystem?.All.UnsafeAsList().OfType<Celestial>().ToList();   // all celestials
Universe.CurrentSystem?.All.UnsafeAsList().OfType<IOrbiter>().ToList();    // celestials + vehicles
StellarBody sun = Universe.CurrentSystem?.GetWorldSun();                   // root star

// SOI tree (IParentBody.Children is IReadOnlyList<IOrbiter>):
static void CollectAllCelestials(IParentBody parent, List<Celestial> result) {
    foreach (var child in parent.Children)
        if (child is Celestial c) { result.Add(c); CollectAllCelestials(c, result); }
}
```

Concrete body types: `StellarBody`, `PlanetaryBody`, `Asteroid`, `Comet`, plus `Vehicle`/`KittenEva`. Always null-guard `Universe.CurrentSystem` (null on main menu / loading).

### Orbit-line visibility

`ShowOrbit` (a `ref bool` on every `IOrbiter`) is the entire orbit-line toggle: `orbiter.ShowOrbit = true;` works for vehicles and celestials alike (marque toggles both).

## Repositioning a celestial every tick (kiwis-marbles)

A `Celestial` is **not** moved like a `Vehicle` — there is no `Celestial.Teleport`. Replace its `Orbit` with one synthesized from desired state vectors, then refresh the cache:

```csharp
double3 tgtPosCci = target.GetPositionCci();        // IOrbiter inertial position
double3 tgtVelCci = target.GetVelocityCci();
IParentBody parent = target.Parent;                 // reparent under target's parent
double3 newPosCci = tgtPosCci + offsetCci;          // offset in CCI frame, METERS (double3 — distances are 1e6–1e9 m)

Orbit newOrbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedSimTime(), newPosCci, tgtVelCci, source.OrbitColor);
source.SetOrbit(newOrbit);          // reparents automatically
source.UpdatePerFrameData();        // MANDATORY — recomputes cached CCI/CCE/Ecl transforms, else renders stale a frame
```

- `Orbit.CreateFromStateCci(IParentBody parent, SimTime stateTime, double3 posCci, double3 velCci, byte4 orbitLineColor)` builds an osculating orbit from state vectors. (Same factory used for vehicle teleport — see SKILL.md.)
- Drive it from `OnBeforeUi` (`Update(dt)`) every frame to override physics.
- **Weld chains need topological ordering** (if A welds to B and B to C, reposition B before A reads it) — Kahn's algorithm; fall back to original order on cycle.
- **Restore on unweld:** capture `OriginalOrbit = source.Orbit` at weld creation, restore with `SetOrbit` + `UpdatePerFrameData`.
- Stars (`StellarBody`) can't be sources (no orbit). Moons of a moved planet follow automatically (parent-relative orbits).

## Global ImGui theming (skittles)

Theming mutates the **global** style from `ImGui.GetStyle()` — no Harmony needed.

```csharp
ImGuiStylePtr style = ImGui.GetStyle();
for (int i = 0; i < 60; i++) {                       // exactly 60 ImGuiCol slots, each a float4 RGBA
    style.Colors[i] = new float4(r, g, b, a);
}
// plus ~35 scalar vars (Alpha, *Rounding, ScrollbarSize, GrabMinSize, ...),
// ~15 float2 vars (WindowPadding, FramePadding, ItemSpacing, ButtonTextAlign, ...),
// 3 bools (AntiAliasedLines, AntiAliasedLinesUseTex, AntiAliasedFill).
```

**Capture the game default BEFORE theming** — there's no engine API to query "KSA's default style," so snapshot it once at init and store it as "Game Default":

```csharp
DefaultTheme = CaptureCurrentStyle();   // run before any ApplyTheme
```

**Restore-on-unload = re-apply that snapshot**, NOT `ImGui.StyleColorsDark()` (that's Dear ImGui's default, not KSA's). For built-in "Dark"/"Light"/"Classic": call `StyleColorsDark()/Light()/Classic()` (colors only), then copy the non-color style vars back from your captured default so geometry stays consistent. `ImGui.ShowStyleEditor`/`ShowStyleSelector` exist in the binding but aren't required — a custom per-color/per-var UI works fine.
