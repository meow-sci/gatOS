---
name: imgui
description: ImGui is an immediate mode UI library.  KSA uses ImGui for UI
---

# Overview

KSA (Kitten Space Agency) uses the **ImGui** immediate-mode UI library for its user interface.

ImGui itself is a C/C++ library, but KSA uses a custom C# wrapper exposed through the game's internal **BRUTAL** framework. Everything lives behind a single static `ImGui` class:

```csharp
using Brutal.ImGuiApi;
```

```csharp
ImGui.Begin("My Window"u8);
if (ImGui.Button("Click me!"u8))
{
    Console.WriteLine("clicked!");
}
ImGui.End();
```

Immediate mode means: **there is no retained widget tree**. You re-issue the entire UI every frame from inside a render callback (in KSA, a `[StarMapAfterGui]` method — see the `ksa` and `mod-impl` skills). Widget functions both draw *and* return their interaction result (`true` when clicked/changed) in the same call.

The BRUTAL wrapper is **1:1 with upstream Dear ImGui** (currently v1.92.x, docking branch) but uses C# conventions: `PascalCase` methods, `ref`/`out` parameters instead of pointers, nullable structs (`float2?`) for optional sizes, and the BRUTAL string/numeric/color types described below. If you know stock ImGui, you know this API — the rest of this skill covers what is *different* and the traps an agent will otherwise fall into.

> **For exact signatures, enum members, and the full widget index, see the self-contained
> [`./api-reference.md`](./api-reference.md)** in this skill directory — it transcribes the
> entire public `ImGui` surface, the extension helpers, and every enum's members. Consult it
> rather than guessing from upstream C++.

---

# Strings: `ImString` (display) and `ImInputString` (editable)

This is the single most important thing to get right with BRUTAL ImGui.

## `ImString` — every label / text argument

**Every** display-string parameter (labels, text, IDs, formats, tooltips, hints, previews) is typed `ImString`, never `string`. `ImString` is a `ref struct` that is *also* an `[InterpolatedStringHandler]`. It wraps a UTF-8 byte span and converts implicitly from many sources:

| You write | What happens | Cost |
|---|---|---|
| `"literal"u8` | UTF-8 `ReadOnlySpan<byte>` → points directly at static data | **zero alloc, best** |
| `$"val: {x}"` passed directly to an `ImString` param | compiler uses the interpolation handler → writes UTF-8 straight into a shared frame buffer | **zero string alloc, best for dynamic** |
| `"literal"` (plain C# string) | implicit `string`→`ImString`, copied into the frame buffer | allocs the C# string |
| a `string` variable | same implicit copy | avoid in hot paths |

**Rules of thumb:**

- Static text → use a **UTF-8 literal**: `"Speed"u8`.
- Dynamic text → pass an **interpolated string directly** to the call, or store it in an **`ImString`-typed** local first (never a `string`-typed local):

```csharp
float val = 123.45f;

ImGui.Text($"val: {val:F2}");          // preferred — handler writes UTF-8 in place

ImString line = $"val: {val:F2}";      // also fine — explicitly ImString-typed
ImGui.Text(line);

string s = $"val: {val:F2}";           // AVOID — allocs a C# string, then copies
ImGui.Text(s);
```

**Critical caveat — `ImString` is frame-scoped.** Interpolated/converted `ImString` values are written into a single shared ring buffer (`ImString.SharedStorage`) that is reset every frame. **Never cache an `ImString` (or the result of an interpolation) in a field across frames.** Build them fresh each frame inside your render method. UTF-8 literals (`"x"u8`) are the exception — they point at static data and are always safe.

Useful members: `ImString.Empty`, `ImString.Null`, `.IsEmpty`, `.Length`, `.ToString()` (allocates / pooled). `default(ImString)` is the idiomatic "no value" for optional `ImString` parameters.

## `ImInputString` — editable text buffers

`InputText` widgets need a *mutable, persistent* buffer, which `ImString` is not. Use `ImInputString` — a class holding a fixed `byte[]` you keep alive across frames as a **field**:

```csharp
// Field on the UI/mod class — survives across frames. Arg is byte capacity (incl. null terminator).
private readonly ImInputString _nameInput = new(128);

// In the render method:
ImGui.InputText("##name"u8, _nameInput);            // edits the buffer in place
if (!_nameInput.IsEmpty)
    DoSomething(_nameInput.ToString());             // read back as needed
```

Key members: `.Length`, `.Capacity`, `.IsEmpty`, `.Clear()`, `.ToString()`, `.Value`/`.ValueSpan`, and setters `.Value8` (UTF-8) / `.Value16` (UTF-16) to programmatically set contents. It converts implicitly to `ImString` so it can also be used anywhere a read-only display string is wanted.

> The old `InputText(label, ref string, maxLength, ...)` overload still exists but is
> `[Obsolete]` — always prefer the `ImInputString` overload.

---

# Numeric and color types (`Brutal.Numerics`)

BRUTAL ImGui does **not** use `System.Numerics.Vector2/4` and does **not** use C-style float arrays. Vectors are BRUTAL value types:

```csharp
using Brutal.Numerics;
```

- `float2`, `float3`, `float4` — float vectors (`.X .Y .Z .W`, also `.R .G .B .A` on float4).
- `int2`, `int3`, `int4` — int vectors.
- `byte4` — byte vector (used by colors).

Sizes/positions are `float2`. Optional sizes are nullable: `in float2? size = null` (pass `null` to mean "auto").

```csharp
ImGui.SetNextWindowSize(new float2(400, 300), ImGuiCond.FirstUseEver);
ImGui.Button("Go"u8, new float2(120, 0));   // width 120, auto height
```

## Colors

Two color representations:

- **`float4`** — RGBA in 0..1. Used by `TextColored`, `ColorEdit4`, `ColorButton`, and the `float4` overloads of `PushStyleColor`/`GetColorU32`.
- **`ImColor8`** — packed 32-bit RGBA (the upstream `ImU32`). Used by `PushStyleColor(ImGuiCol, ImColor8)`, `TableSetBgColor`, and **all `ImDrawList` drawing**.

`ImColor8` conversions: implicit from `uint`, `byte4`, `ImGuiCol` (resolves the *current style color*), and `Color.Preset`; **explicit** from `float4` (`(ImColor8)myFloat4`). Presets: `ImColor8.White/Black/Red/Green/Blue`. Helpers: `.AsUint()`, `.AsFloat4()`, `.AsByte4()`.

```csharp
float4 red = new float4(1f, 0.2f, 0.2f, 1f);
ImGui.TextColored(red, "Warning"u8);

ImGui.PushStyleColor(ImGuiCol.Text, red);                    // float4 overload
ImGui.PushStyleColor(ImGuiCol.Button, ImColor8.Red);         // ImColor8 overload
ImGui.Text("styled"u8);
ImGui.PopStyleColor(2);

ImColor8 c = ImGui.GetColorU32(ImGuiCol.TextDisabled);       // current theme color as U32
```

---

# Signature traps (where agents get it wrong)

The wrapper mirrors upstream ImGui, but a few signatures differ from what you may assume. **These are the common mistakes:**

### `DragFloat` / `DragInt` — speed is the 3rd argument

```csharp
// (label, ref value, vSpeed, vMin, vMax, format?, flags?)
ImGui.DragFloat("Speed (m/s)"u8, ref _speed, 1f, 1f, 250f);  // speed=1, min=1, max=250
```
It is **not** `(label, ref, min, max)`. Forgetting `vSpeed` silently turns your min into the drag speed. Prefer `DragFloat` over `SliderFloat` — it supports both dragging and double-click-to-type. `SliderFloat` is `(label, ref v, vMin, vMax, ...)` (no speed — sliders map the full range).

### Multi-component widgets take vector `ref`, not arrays

```csharp
float3 pos = new float3(1, 2, 3);
ImGui.DragFloat3("Position"u8, ref pos, 0.1f);   // ref float3 — NOT float[3]
ImGui.InputInt2("Cell"u8, ref cell);             // ref int2
ImGui.SliderFloat4("Color"u8, ref rgba, 0f, 1f); // ref float4
```
There are `2/3/4` variants for `DragFloat/DragInt/SliderFloat/SliderInt/InputFloat/InputInt`, each taking `ref floatN`/`ref intN`.

### `Combo` / `ListBox` — the trailing int is `popupMaxHeightInItems`, not a count

A `string[]` converts implicitly (to `RefString8Array`), so the array is auto-counted. The trailing int is the popup height cap, **not** the number of items:

```csharp
string[] names = { "Linear", "Ease In", "Ease Out" };
int sel = 0;
// label, ref selectedIndex, items[], popupMaxHeightInItems (-1 = default)
if (ImGui.Combo("Easing"u8, ref sel, names))            { /* changed */ }
if (ImGui.Combo("Easing"u8, ref sel, names, names.Length)) { /* also valid: show all rows, no scroll */ }
```
Other `Combo` overloads: `ImString itemsSeparatedByZeros` (a single string with `\0` separators) and a `ComboGetter` delegate. For **enums**, use `ImGuiEx.Combo<TEnum>` (see Extensions). For a filterable/custom list, use `BeginCombo`/`EndCombo` (cookbook below).

### `ColorEdit3` / `ColorPicker3` take `ref float3`

The built-ins take `ref float3`. If your color is a `float4` and you only want RGB, use the `ImGuiEx.ColorEdit3(ref float4)` / `ImGuiEx.ColorPicker3(ref float4)` helpers. `ColorEdit4`/`ColorPicker4` take `ref float4`.

### Other notable shapes

- Optional sizes are `in float2?` (pass `null` for auto): `Button`, `BeginChild`, `Selectable`, `ColorButton`, `ProgressBar`, `BeginListBox`, `BeginTable`(outerSize)…
- `format` arguments are **printf-style** `ImString`: `"%.2f"`, `"%d"`, `"%.0f deg"`. Leave as `default(ImString)` for the default.
- `PushFont` now **requires** a size: `ImGui.PushFont(font, size)`. The no-size overload is obsolete.
- `Begin` has both `Begin(name, ref bool open, flags)` (window with close button) and `Begin(name, flags)`.

---

# Window / layout skeleton

```csharp
ImGui.SetNextWindowSize(new float2(420, 300), ImGuiCond.FirstUseEver);
if (ImGui.Begin("My Mod"u8, ref _windowVisible))
{
    ImGui.Text("Hello"u8);
    ImGui.SameLine();
    ImGui.TextDisabled("(dimmed)"u8);

    ImGui.Separator();

    if (ImGui.CollapsingHeader("Section"u8, ImGuiTreeNodeFlags.DefaultOpen))
    {
        ImGui.Indent();
        ImGui.Text("nested"u8);
        ImGui.Unindent();
    }
}
ImGui.End();   // ALWAYS call End() even when Begin() returned false
```

**Begin/End pairing rule:** `Begin` must always be matched by `End()` (unconditionally). The `Begin*`/`End*` pairs that are conditional (only call `End*` when the `Begin*` returned `true`) are: `BeginChild` family is unconditional like `Begin`; but **`BeginCombo`/`BeginListBox`/`BeginMenu`/`BeginPopup*`/`BeginTabBar`/`BeginTabItem`/`BeginTable`/`BeginTooltip`/`BeginDragDrop*` only get their matching `End*` when they return `true`.** When unsure, follow upstream ImGui's rule for that specific Begin.

Common layout helpers: `SameLine(offsetX=0, spacing=-1)`, `Spacing()`, `NewLine()`, `Indent/Unindent`, `BeginGroup/EndGroup`, `AlignTextToFramePadding()`, `SetNextItemWidth(w)` (negative = "fill to right, leaving |w| px"), `GetContentRegionAvail()`, `GetCursorScreenPos()`.

---

# Tables (preferred for aligned multi-column layout)

KSA mods lean heavily on tables for label/value grids and lists:

```csharp
ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new float2(6f, 3f));
var flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.RowBg;
if (ImGui.BeginTable("##stats"u8, 2, flags))
{
    ImGui.TableSetupColumn("##label"u8, ImGuiTableColumnFlags.WidthStretch, 1f);
    ImGui.TableSetupColumn("##value"u8, ImGuiTableColumnFlags.WidthStretch, 2f);

    ImGui.TableNextRow();
    ImGui.TableNextColumn(); ImGui.TextColored(grey, "Current"u8);
    ImGui.TableNextColumn(); ImGui.Text($"{value:F2} g");

    ImGui.EndTable();   // only because BeginTable returned true
}
ImGui.PopStyleVar();
```

Use `TableHeadersRow()` after `TableSetupColumn` calls for a header; `TableSetupScrollFreeze`, `TableSetBgColor`, and `TableGetSortSpecs()` for fancier tables. Prefer tables over the legacy `Columns()` API (which is obsolete-ish).

---

# Custom drawing (`ImDrawList`)

For graphs, gauges, and custom widgets, draw primitives directly:

```csharp
ImDrawListPtr dl = ImGui.GetWindowDrawList();   // clipped to current window
float2 p = ImGui.GetCursorScreenPos();
dl.AddRectFilled(p, p + new float2(200, 100), ImColor8.Black, 4f);
dl.AddLine(p, p + new float2(200, 100), ImColor8.Green, 2f);
dl.AddText(p + new float2(4, 4), ImColor8.White, "label"u8);
```

`GetForegroundDrawList()` / `GetBackgroundDrawList()` draw over/under everything (not clipped to a window). Draw colors are **`ImColor8`**. Available `ImDrawListPtr` extension methods (in `Brutal.ImGuiApi`): `AddLine`, `AddRect`, `AddRectFilled`, `AddRectFilledMultiColor`, `AddQuad(Filled)`, `AddTriangle(Filled)`, `AddCircle(Filled)`, `AddNgon(Filled)`, `AddEllipse(Filled)` (takes `float2 radius`), `AddText`, `AddBezierCubic/Quadratic`, `AddPolyline`, `AddConvex/ConcavePolyFilled`, `AddImage(Rounded/Quad)`.

---

# KSA / BRUTAL extension helpers

These are **separate namespaces** — add the `using` to access them.

### `Brutal.ImGuiApi.Extensions` → `ImGuiEx`

Generic helpers the core API lacks, especially for **enums** (the enum must be `int`-sized):

```csharp
using Brutal.ImGuiApi.Extensions;

MyEnum mode = MyEnum.A;
ImGuiEx.Combo<MyEnum>("Mode"u8, ref mode, enumLabels);   // enum-typed combo
ImGuiEx.RadioButton("A"u8, ref mode, MyEnum.A);
ImGuiEx.CheckboxFlags("Flag"u8, ref myFlags, MyFlags.X);

ImGuiEx.ColorEdit3("Tint"u8, ref tintFloat4);            // float4 RGB edit
ImGuiEx.ColorPicker3("Tint"u8, ref tintFloat4);
float4 rgb = ImGuiEx.ColorConvertHSVtoRGB(hsv);
```
Also generic `DragScalar<T>/SliderScalar<T>/InputScalar<T>` (and `…N` span variants) for arbitrary unmanaged numeric types.

### `Brutal.ImGuiApi.Abstractions` → `ImGuiUtils`

```csharp
using Brutal.ImGuiApi.Abstractions;

ImGuiUtils.TextShadow("Title"u8, ImColor8.White);   // text with a 1px shadow
ImGuiUtils.SetLastFocusOnAppearing();               // focus the last item when window appears
```

### Advanced / internal API

`ImGui.Internal.*` exposes the upstream `imgui_internal.h` surface (e.g. `GetCurrentWindow()`, `FocusWindow()`, `BeginColumns()`); `ImGui.PInvoke.*` is the raw unmanaged binding layer. Reach for these only when the public API genuinely lacks something. `ImGuiCompatibilityEx` / `ImGuiKsaEx` hold `[Obsolete]` shims for old call shapes — don't write new code against them, but you'll see them flagged when migrating.

---

# Blocking game hotkeys during text input (REQUIRED)

When a mod has text inputs, typing in them would also fire game hotkeys (e.g. `\` opens the console, `Enter` submits commands). KSA mods solve this with a shared **`HotkeyGuard`** helper that **every top-level mod must apply** (this is a repo-wide rule).

`HotkeyGuard` Harmony-patches `GameSettings.OnKeyAll` and, whenever `ImGui.GetIO().WantTextInput` is true *and the in-game console is not open*, consumes the key so it never reaches game hotkeys. Apply it from your `Patcher`:

```csharp
using MeowSci.KsaAbstractions;

// In Patcher.Patch(), after creating the Harmony instance:
HotkeyGuard.Patch(_harmony);
// In Patcher.Unload(), before nulling the Harmony instance:
HotkeyGuard.Unpatch(_harmony);
```

Because the guard checks `Program.ConsoleWindow.IsOpen`, the global `WantTextInput` flag is safe here — the console keeps working. **Individual mods do not need their own per-window focus traps**; applying `HotkeyGuard` once covers every `InputText`/combo-filter in the mod automatically. (If you ever need per-window scoping for some other reason, check focus *inside* the `Begin`/`End` block with `ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)` — `IsWindowFocused` returns nothing meaningful after `End()`.)

The full implementation (inlined here so the mechanism is self-contained — the canonical copy lives in the `ksa-abstractions.lib` project as `MeowSci.KsaAbstractions.HotkeyGuard`):

```csharp
using System;
using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Abstractions;
using HarmonyLib;
using KSA;

namespace MeowSci.KsaAbstractions;

/// <summary>
/// Blocks game hotkeys (GameSettings.OnKeyAll) whenever an ImGui text input has keyboard focus.
/// Uses the global ImGui WantTextInput flag so every InputText / combo filter is covered automatically.
/// </summary>
public static class HotkeyGuard
{
    private static MethodInfo? _original;
    private static MethodInfo? _prefix;

    public static void Patch(Harmony harmony)
    {
        _original = AccessTools.Method(typeof(GameSettings), nameof(GameSettings.OnKeyAll));
        _prefix = typeof(HotkeyGuard).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
        Console.WriteLine("ksa-abstractions: HotkeyGuard patch applied");
    }

    public static void Unpatch(Harmony harmony)
    {
        if (_original != null && _prefix != null)
            harmony.Unpatch(_original, _prefix);
        _original = null;
        _prefix = null;
        Console.WriteLine("ksa-abstractions: HotkeyGuard patch removed");
    }

    private static bool Prefix(ref bool __result)
    {
        if (!Program.ConsoleWindow.IsOpen && ImGui.GetIO().WantTextInput)
        {
            __result = true;
            return false;
        }
        return true;
    }
}
```

---

# Cookbook

### Colored / styled text
```csharp
ImGui.TextColored(new float4(1f, 0.2f, 0.2f, 1f), $"Peak: {peak:F2} g");
ImGui.SameLine(0, 20);
ImGui.Text($"Avg: {avg:F2} g");
ImGui.TextDisabled("(hint)"u8);
ImGui.TextWrapped("a long paragraph that wraps to the content width"u8);
```

### Separators
```csharp
ImGui.Separator();
ImGui.SeparatorText("Section title"u8);
```

### Buttons / checkbox / radio
```csharp
if (ImGui.Button("Apply"u8)) Apply();
if (ImGui.SmallButton("x"u8)) Remove();
ImGui.Checkbox("Enabled"u8, ref _enabled);
ImGui.RadioButton("On"u8, ref _mode, 1); ImGui.SameLine();
ImGui.RadioButton("Off"u8, ref _mode, 0);
```

### Numeric input / drag / slider
```csharp
ImGui.DragFloat("Interval (s)"u8, ref _interval, 0.01f, 0.01f, 10f, "%.2f");
ImGui.SliderInt("FOV"u8, ref _fov, 10, 200);
ImGui.InputDouble("Mass"u8, ref _mass, step: 0.1, flags: ImGuiInputTextFlags.None);
ImGui.SliderAngle("Pitch"u8, ref _pitchRadians);   // shows degrees, stores radians
```

### Combo (simple, from string[])
```csharp
if (ImGui.Combo("Easing"u8, ref _easingIdx, _easingNames))
{ /* selection changed */ }
```

### Combo with a live filter (BeginCombo + ImInputString)
```csharp
private readonly ImInputString _itemFilter = new(128);   // field

string preview = _selected >= 0 ? _items[_selected] : "Select...";
if (ImGui.BeginCombo("##combo"u8, preview))
{
    if (ImGui.IsWindowAppearing())
    {
        ImGui.SetKeyboardFocusHere();
        _itemFilter.Clear();
    }
    ImGui.SetNextItemWidth(-1f);
    ImGui.InputTextWithHint("##filter"u8, "filter..."u8, _itemFilter);
    string f = _itemFilter.ToString().Trim();

    for (int i = 0; i < _items.Length; i++)
    {
        if (f.Length > 0 && !_items[i].Contains(f, StringComparison.OrdinalIgnoreCase))
            continue;
        bool sel = _selected == i;
        if (ImGui.Selectable(_items[i], sel)) _selected = i;
        if (sel) ImGui.SetItemDefaultFocus();
    }
    ImGui.EndCombo();   // only because BeginCombo returned true
}
```

### Color editing
```csharp
float4 col = new float4(1f, 0.5f, 0.2f, 1f);
ImGui.ColorEdit4("Color##rgba"u8, ref col, ImGuiColorEditFlags.Float);
// swatch + picker popup, no inline sliders:
ImGui.ColorEdit4("Color##sw"u8, ref col,
    ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
```

### Collapsing header / tree
```csharp
if (ImGui.CollapsingHeader("Advanced"u8, ImGuiTreeNodeFlags.DefaultOpen))
    ImGui.Text("content"u8);
```

### Progress bar
```csharp
ImGui.ProgressBar(Math.Clamp(progress, 0f, 1f), new float2(-1, 0));
```

### Tooltip
```csharp
if (ImGui.IsItemHovered()) ImGui.SetTooltip("explanation"u8);
// or: ImGui.SetItemTooltip("explanation"u8);
```

### Child region (scrollable sub-area)
```csharp
if (ImGui.BeginChild("##log"u8, new float2(0, 200), ImGuiChildFlags.Borders))
{
    foreach (var line in _lines) ImGui.Text(line);
}
ImGui.EndChild();   // unconditional, like End()
```

### Keypress / toggle
```csharp
if (ImGui.IsKeyPressed(ImGuiKey.F11)) _windowVisible = !_windowVisible;
```

---

# Reference: the C++ demo examples

The upstream Dear ImGui demo (`imgui_demo.cpp`) is mined into per-feature snippets under
[`./examples/`](./examples/), indexed by [`./examples-toc.md`](./examples-toc.md) (179 entries:
widgets, layout, tables, drag-drop, plotting, styling, popups, docking, etc.). Use them as a
**behavioral** reference for what a widget can do and which flags exist.

**They are raw C++ and must be translated to BRUTAL C#** when you adopt them. Apply these rules:

1. `ImGui::Foo(...)` → `ImGui.Foo(...)`.
2. C string literals → UTF-8 literals: `"x"` → `"x"u8`; dynamic text → interpolated `$"..."`.
3. `ImVec2`/`ImVec4` → `float2`/`float4`; raw float/int arrays → `float2/3/4`, `int2/3/4` passed by `ref`.
4. Pointer out-params (`&value`) → `ref value`; `bool*` open flags → `ref bool`.
5. `ImU32` colors → `ImColor8`; `ImColor`/normalized colors → `float4`.
6. `static` locals in C++ demos (persisting across frames) → **fields** on your class (`ImInputString`, etc.).
7. Re-check argument order against `ImGui.cs` — the demo's positional args may not match the C# overload's defaults (see *Signature traps*).
