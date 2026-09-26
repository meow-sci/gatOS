# Layout/Basic Horizontal Layout/SameLine

- Marker: IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/SameLine")
- Source: .github/skills/imgui/demo.cpp:3882
- Summary: Demonstrates SameLine behavior within Layout / Basic Horizontal Layout.

```cpp
        IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/SameLine");
        ImGui::Text("Two items: Hello"); ImGui::SameLine();
        ImGui::TextColored(ImVec4(1, 1, 0, 1), "Sailor");

        // Adjust spacing
        ImGui::Text("More spacing: Hello"); ImGui::SameLine(0, 20);
        ImGui::TextColored(ImVec4(1, 1, 0, 1), "Sailor");

        // Button
        ImGui::AlignTextToFramePadding();
        ImGui::Text("Normal buttons"); ImGui::SameLine();
        ImGui::Button("Banana"); ImGui::SameLine();
        ImGui::Button("Apple"); ImGui::SameLine();
        ImGui::Button("Corniflower");

        // Button
        ImGui::Text("Small buttons"); ImGui::SameLine();
        ImGui::SmallButton("Like this one"); ImGui::SameLine();
        ImGui::Text("can fit within a text block.");

        // Aligned to arbitrary position. Easy/cheap column.
```

