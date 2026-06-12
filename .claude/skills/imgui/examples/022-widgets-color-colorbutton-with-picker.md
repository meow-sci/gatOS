# Widgets/Color/ColorButton (with Picker)

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorButton (with Picker)")
- Source: .github/skills/imgui/demo.cpp:406
- Summary: Demonstrates ColorButton (with Picker) behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorButton (with Picker)");
        ImGui::Text("Color button with Picker:");
        ImGui::SameLine(); HelpMarker(
            "With the ImGuiColorEditFlags_NoInputs flag you can hide all the slider/text inputs.\n"
            "With the ImGuiColorEditFlags_NoLabel flag you can pass a non-empty label which will only "
            "be used for the tooltip and picker popup.");
        ImGui::ColorEdit4("MyColor##3", (float*)&color, ImGuiColorEditFlags_NoInputs | ImGuiColorEditFlags_NoLabel | base_flags);
```

