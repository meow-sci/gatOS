# Tables/Row height

- Marker: IMGUI_DEMO_MARKER("Tables/Row height")
- Source: .github/skills/imgui/demo.cpp:6045
- Summary: Demonstrates Row height behavior within Tables.

```cpp
        IMGUI_DEMO_MARKER("Tables/Row height");
        HelpMarker(
            "You can pass a 'min_row_height' to TableNextRow().\n\nRows are padded with 'style.CellPadding.y' on top and bottom, "
            "so effectively the minimum row height will always be >= 'style.CellPadding.y * 2.0f'.\n\n"
            "We cannot honor a _maximum_ row height as that would require a unique clipping rectangle per row.");
        if (ImGui::BeginTable("table_row_height", 1, ImGuiTableFlags_Borders))
        {
            for (int row = 0; row < 8; row++)
            {
                float min_row_height = (float)(int)(TEXT_BASE_HEIGHT * 0.30f * row + ImGui::GetStyle().CellPadding.y * 2.0f);
                ImGui::TableNextRow(ImGuiTableRowFlags_None, min_row_height);
                ImGui::TableNextColumn();
                ImGui::Text("min_row_height = %.2f", min_row_height);
            }
            ImGui::EndTable();
        }

        HelpMarker(
            "Showcase using SameLine(0,0) to share Current Line Height between cells.\n\n"
            "Please note that Tables Row Height is not the same thing as Current Line Height, "
            "as a table cell may contains multiple lines.");
        if (ImGui::BeginTable("table_share_lineheight", 2, ImGuiTableFlags_Borders))
        {
            ImGui::TableNextRow();
            ImGui::TableNextColumn();
            ImGui::ColorButton("##1", ImVec4(0.13f, 0.26f, 0.40f, 1.0f), ImGuiColorEditFlags_None, ImVec2(40, 40));
            ImGui::TableNextColumn();
            ImGui::Text("Line 1");
            ImGui::Text("Line 2");

            ImGui::TableNextRow();
            ImGui::TableNextColumn();
            ImGui::ColorButton("##2", ImVec4(0.13f, 0.26f, 0.40f, 1.0f), ImGuiColorEditFlags_None, ImVec2(40, 40));
            ImGui::TableNextColumn();
            ImGui::SameLine(0.0f, 0.0f); // Reuse line height from previous column
            ImGui::Text("Line 1, with SameLine(0,0)");
            ImGui::Text("Line 2");

            ImGui::EndTable();
        }

        HelpMarker("Showcase altering CellPadding.y between rows. Note that CellPadding.x is locked for the entire table.");
        if (ImGui::BeginTable("table_changing_cellpadding_y", 1, ImGuiTableFlags_Borders))
        {
            ImGuiStyle& style = ImGui::GetStyle();
            for (int row = 0; row < 8; row++)
            {
                if ((row % 3) == 2)
                    ImGui::PushStyleVarY(ImGuiStyleVar_CellPadding, 20.0f);
                ImGui::TableNextRow(ImGuiTableRowFlags_None);
                ImGui::TableNextColumn();
                ImGui::Text("CellPadding.y = %.2f", style.CellPadding.y);
                if ((row % 3) == 2)
                    ImGui::PopStyleVar();
            }
            ImGui::EndTable();
        }

        ImGui::TreePop();
    }

    if (open_action != -1)
        ImGui::SetNextItemOpen(open_action != 0);
    if (ImGui::TreeNode("Outer size"))
    {
```

