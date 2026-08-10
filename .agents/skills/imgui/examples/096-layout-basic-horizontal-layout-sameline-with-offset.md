# Layout/Basic Horizontal Layout/SameLine (with offset)

- Marker: IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/SameLine (with offset)")
- Source: .github/skills/imgui/demo.cpp:3903
- Summary: Demonstrates SameLine (with offset) behavior within Layout / Basic Horizontal Layout.

```cpp
        IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/SameLine (with offset)");
        ImGui::Text("Aligned");
        ImGui::SameLine(150); ImGui::Text("x=150");
        ImGui::SameLine(300); ImGui::Text("x=300");
        ImGui::Text("Aligned");
        ImGui::SameLine(150); ImGui::SmallButton("x=150");
        ImGui::SameLine(300); ImGui::SmallButton("x=300");

        // Checkbox
```

