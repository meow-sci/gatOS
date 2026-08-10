# Widgets/Selectables/In Tables

- Marker: IMGUI_DEMO_MARKER("Widgets/Selectables/In Tables")
- Source: .github/skills/imgui/demo.cpp:1656
- Summary: Demonstrates In Tables behavior within Widgets / Selectables.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selectables/In Tables");
            static bool selected[10] = {};

            if (ImGui::BeginTable("split1", 3, ImGuiTableFlags_Resizable | ImGuiTableFlags_NoSavedSettings | ImGuiTableFlags_Borders))
            {
                for (int i = 0; i < 10; i++)
                {
                    char label[32];
                    sprintf(label, "Item %d", i);
                    ImGui::TableNextColumn();
                    ImGui::Selectable(label, &selected[i]); // FIXME-TABLE: Selection overlap
                }
                ImGui::EndTable();
            }
            ImGui::Spacing();
            if (ImGui::BeginTable("split2", 3, ImGuiTableFlags_Resizable | ImGuiTableFlags_NoSavedSettings | ImGuiTableFlags_Borders))
            {
                for (int i = 0; i < 10; i++)
                {
                    char label[32];
                    sprintf(label, "Item %d", i);
                    ImGui::TableNextRow();
                    ImGui::TableNextColumn();
                    ImGui::Selectable(label, &selected[i], ImGuiSelectableFlags_SpanAllColumns);
                    ImGui::TableNextColumn();
                    ImGui::Text("Some other contents");
                    ImGui::TableNextColumn();
                    ImGui::Text("123456");
                }
                ImGui::EndTable();
            }
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Grid"))
        {
```

