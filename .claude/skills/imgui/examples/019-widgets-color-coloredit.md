# Widgets/Color/ColorEdit

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorEdit")
- Source: .github/skills/imgui/demo.cpp:390
- Summary: Demonstrates ColorEdit behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorEdit");
        ImGui::SeparatorText("Inline color editor");
        ImGui::Text("Color widget:");
        ImGui::SameLine(); HelpMarker(
            "Click on the color square to open a color picker.\n"
            "Ctrl+Click on individual component to input value.\n");
        ImGui::ColorEdit3("MyColor##1", (float*)&color, base_flags);
```

