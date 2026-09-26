# Widgets/Color/ColorEdit (float display)

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorEdit (float display)")
- Source: .github/skills/imgui/demo.cpp:402
- Summary: Demonstrates ColorEdit (float display) behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorEdit (float display)");
        ImGui::Text("Color widget with Float Display:");
        ImGui::ColorEdit4("MyColor##2f", (float*)&color, ImGuiColorEditFlags_Float | base_flags);
```

