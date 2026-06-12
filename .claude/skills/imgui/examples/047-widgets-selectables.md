# Widgets/Selectables

- Marker: IMGUI_DEMO_MARKER("Widgets/Selectables")
- Source: .github/skills/imgui/demo.cpp:1592
- Summary: Demonstrates Selectables behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Selectables");
        // Selectable() has 2 overloads:
        // - The one taking "bool selected" as a read-only selection information.
        //   When Selectable() has been clicked it returns true and you can alter selection state accordingly.
        // - The one taking "bool* p_selected" as a read-write selection information (convenient in some cases)
        // The earlier is more flexible, as in real application your selection may be stored in many different ways
        // and not necessarily inside a bool value (e.g. in flags within objects, as an external list, etc).
```

