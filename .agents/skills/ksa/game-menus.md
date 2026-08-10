# Adding Top-Level Menus to the KSA Game Title Bar

## How the Game Menu Bar Works

The game draws its menu bar in `Program.DrawMenuBar(Viewport viewport, int windowWidth)`. It creates a borderless ImGui window with `ImGuiWindowFlags.MenuBar` and calls `ImGui.BeginMenuBar()` / `ImGui.EndMenuBar()` inside it. The built-in menus rendered inside that block are, in order:

1. **File** — save/load, exit, settings
2. **Editor** (when in editor mode) *or* **Universe** (in flight) — simulation controls
3. **View** — cameras, overlays, orbit lines, debug tools

To add a custom top-level menu entry, code must be injected **inside** the `BeginMenuBar()` / `EndMenuBar()` block, after the last built-in menu.

## Harmony Transpiler Pattern

The only reliable injection point is a **Harmony Transpiler** on `Program.DrawMenuBar`. A prefix/postfix does not work because you need to execute `ImGui.BeginMenu` / `ImGui.EndMenu` inside the existing `BeginMenuBar()` scope.

### Strategy

Walk the IL backwards and find the first (i.e. last-in-source) `ImGui.EndMenu()` call — this is the View menu's closing call. Inject your menu calls immediately after it, before the version string display code and `ImGui.EndMenuBar()`.

### Complete Implementation

```csharp
using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;
using System.Reflection;
using System.Reflection.Emit;

[HarmonyPatch(typeof(Program), "DrawMenuBar")]
public static class MyMenuPatcher
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        MethodInfo endMenuMethod  = AccessTools.Method(typeof(ImGui), nameof(ImGui.EndMenu));
        MethodInfo injectMethod   = AccessTools.Method(typeof(MyMenuPatcher), nameof(DrawMyMenu));

        int endMenuCount = 0;
        for (int i = codes.Count - 1; i >= 0; i--)
        {
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt)
                && codes[i].operand is MethodInfo m && m == endMenuMethod)
            {
                endMenuCount++;
                if (endMenuCount == 1)   // last EndMenu in source = View menu's EndMenu
                {
                    // Ldarg_1 pushes the Viewport parameter; call our injected method
                    codes.Insert(i + 4, new CodeInstruction(OpCodes.Ldarg_1));
                    codes.Insert(i + 5, new CodeInstruction(OpCodes.Call, injectMethod));
                    Console.WriteLine("MyMod: menu injected successfully");
                    break;
                }
            }
        }

        return codes;
    }

    private static void DrawMyMenu(Viewport viewport)
    {
        if (ImGui.BeginMenu("My Mod"))
        {
            viewport.MenuBarInUse = true;   // REQUIRED: blocks game hotkeys/camera while menu is open

            if (ImGui.MenuItem("Do Something", default, _enabled))
                _enabled = !_enabled;

            if (ImGui.BeginMenu("Submenu"))
            {
                ImGui.Text("Nested content");
                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }
    }

    private static bool _enabled = false;
}
```

Apply this in your `Patcher.cs` using `_harmony.PatchAll()` or `_harmony.Patch(...)` in the usual way.

## Critical Details

### `viewport.MenuBarInUse = true`

Set this **inside** the `ImGui.BeginMenu(...)` block (i.e. only when the menu is open). It tells the game the menu bar is actively being used, which:
- Prevents the menu bar from auto-hiding (when the auto-hide setting is on)
- Suppresses camera movement and game hotkey processing while the menu is open

Omitting this causes the game to steal keyboard/mouse input from your menu.

### Injection offset (`i + 4`, `i + 5`)

After the View menu's `ImGui.EndMenu()` call there are **3 IL instructions** for the version string display (`SetCursorPosY`, `SetCursorPosX`, `TextColored` on the main viewport). These must not be displaced, so insertion starts at `i + 4` to append after them and before `ImGui.EndMenuBar()`.

### `DrawMenuBar` is called per-viewport

In split-screen mode, `DrawMenuBar` is called once per visible viewport. Your `DrawMyMenu(Viewport viewport)` will be called multiple times per frame. If you have per-frame state, account for this. The `viewport` argument lets you scope behavior to a specific viewport if needed.

### Harmony patch timing

Apply the patch in `[StarMapAllModsLoaded]`, not in the `[StarMapMod]` constructor:

```csharp
[StarMapMod]
public class Mod
{
    [StarMapAllModsLoaded]
    public void OnFullyLoaded() => Patcher.Patch();

    [StarMapUnload]
    public void Unload() => Patcher.Unload();
}
```

## ImGui Calls Available Inside the Menu

Once inside the `BeginMenu("...")` block you can use all standard ImGui menu calls:

```csharp
ImGui.MenuItem("Label")                        // simple clickable item, returns bool
ImGui.MenuItem("Label", default, isSelected)   // with checkmark toggle
ImGui.MenuItem("Label", shortcutStr, selected, enabled)  // full overload
ImGui.BeginMenu("Submenu") / ImGui.EndMenu()   // nested submenu
ImGui.Separator()                              // horizontal divider
ImGui.Text("Info text")                        // non-interactive text
ImGui.TextColored(color, "Colored text")
```
