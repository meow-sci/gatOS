# Widgets/Color/ColorButton (simple)

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorButton (simple)")
- Source: .github/skills/imgui/demo.cpp:482
- Summary: Demonstrates ColorButton (simple) behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorButton (simple)");
        ImGui::Text("Color button only:");
        static bool no_border = false;
        ImGui::Checkbox("ImGuiColorEditFlags_NoBorder", &no_border);
        ImGui::ColorButton("MyColor##3c", *(ImVec4*)&color, base_flags | (no_border ? ImGuiColorEditFlags_NoBorder : 0), ImVec2(80, 80));
```

