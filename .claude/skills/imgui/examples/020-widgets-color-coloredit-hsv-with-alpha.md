# Widgets/Color/ColorEdit (HSV, with Alpha)

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorEdit (HSV, with Alpha)")
- Source: .github/skills/imgui/demo.cpp:398
- Summary: Demonstrates ColorEdit (HSV, with Alpha) behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorEdit (HSV, with Alpha)");
        ImGui::Text("Color widget HSV with Alpha:");
        ImGui::ColorEdit4("MyColor##2", (float*)&color, ImGuiColorEditFlags_DisplayHSV | base_flags);
```

