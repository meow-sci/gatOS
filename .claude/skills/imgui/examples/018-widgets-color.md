# Widgets/Color

- Marker: IMGUI_DEMO_MARKER("Widgets/Color")
- Source: .github/skills/imgui/demo.cpp:376
- Summary: Demonstrates Color behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color");
        static ImVec4 color = ImVec4(114.0f / 255.0f, 144.0f / 255.0f, 154.0f / 255.0f, 200.0f / 255.0f);
        static ImGuiColorEditFlags base_flags = ImGuiColorEditFlags_None;

        ImGui::SeparatorText("Options");
        ImGui::CheckboxFlags("ImGuiColorEditFlags_NoAlpha", &base_flags, ImGuiColorEditFlags_NoAlpha);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_AlphaOpaque", &base_flags, ImGuiColorEditFlags_AlphaOpaque);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_AlphaNoBg", &base_flags, ImGuiColorEditFlags_AlphaNoBg);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_AlphaPreviewHalf", &base_flags, ImGuiColorEditFlags_AlphaPreviewHalf);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_NoOptions", &base_flags, ImGuiColorEditFlags_NoOptions); ImGui::SameLine(); HelpMarker("Right-click on the individual color widget to show options.");
        ImGui::CheckboxFlags("ImGuiColorEditFlags_NoDragDrop", &base_flags, ImGuiColorEditFlags_NoDragDrop);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_NoColorMarkers", &base_flags, ImGuiColorEditFlags_NoColorMarkers);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_HDR", &base_flags, ImGuiColorEditFlags_HDR); ImGui::SameLine(); HelpMarker("Currently all this does is to lift the 0..1 limits on dragging widgets.");
```

