# Tables/Columns flags

- Marker: IMGUI_DEMO_MARKER("Tables/Columns flags")
- Source: .github/skills/imgui/demo.cpp:5856
- Summary: Demonstrates Columns flags behavior within Tables.

```cpp
        IMGUI_DEMO_MARKER("Tables/Columns flags");
        // Create a first table just to show all the options/flags we want to make visible in our example!
        const int column_count = 3;
        const char* column_names[column_count] = { "One", "Two", "Three" };
        static ImGuiTableColumnFlags column_flags[column_count] = { ImGuiTableColumnFlags_DefaultSort, ImGuiTableColumnFlags_None, ImGuiTableColumnFlags_DefaultHide };
        static ImGuiTableColumnFlags column_flags_out[column_count] = { 0, 0, 0 }; // Output from TableGetColumnFlags()

        if (ImGui::BeginTable("table_columns_flags_checkboxes", column_count, ImGuiTableFlags_None))
        {
            PushStyleCompact();
            for (int column = 0; column < column_count; column++)
            {
                ImGui::TableNextColumn();
                ImGui::PushID(column);
                ImGui::AlignTextToFramePadding(); // FIXME-TABLE: Workaround for wrong text baseline propagation across columns
                ImGui::Text("'%s'", column_names[column]);
                ImGui::Spacing();
                ImGui::Text("Input flags:");
                EditTableColumnsFlags(&column_flags[column]);
                ImGui::Spacing();
                ImGui::Text("Output flags:");
                ImGui::BeginDisabled();
                ShowTableColumnsStatusFlags(column_flags_out[column]);
                ImGui::EndDisabled();
                ImGui::PopID();
            }
            PopStyleCompact();
            ImGui::EndTable();
        }

        // Create the real table we care about for the example!
        // We use a scrolling table to be able to showcase the difference between the _IsEnabled and _IsVisible flags above,
        // otherwise in a non-scrolling table columns are always visible (unless using ImGuiTableFlags_NoKeepColumnsVisible
        // + resizing the parent window down).
        const ImGuiTableFlags flags
            = ImGuiTableFlags_SizingFixedFit | ImGuiTableFlags_ScrollX | ImGuiTableFlags_ScrollY
            | ImGuiTableFlags_RowBg | ImGuiTableFlags_BordersOuter | ImGuiTableFlags_BordersV
            | ImGuiTableFlags_Resizable | ImGuiTableFlags_Reorderable | ImGuiTableFlags_Hideable | ImGuiTableFlags_Sortable;
        ImVec2 outer_size = ImVec2(0.0f, TEXT_BASE_HEIGHT * 9);
        if (ImGui::BeginTable("table_columns_flags", column_count, flags, outer_size))
        {
            bool has_angled_header = false;
            for (int column = 0; column < column_count; column++)
            {
                has_angled_header |= (column_flags[column] & ImGuiTableColumnFlags_AngledHeader) != 0;
                ImGui::TableSetupColumn(column_names[column], column_flags[column]);
            }
            if (has_angled_header)
                ImGui::TableAngledHeadersRow();
            ImGui::TableHeadersRow();
            for (int column = 0; column < column_count; column++)
                column_flags_out[column] = ImGui::TableGetColumnFlags(column);
            float indent_step = (float)((int)TEXT_BASE_WIDTH / 2);
            for (int row = 0; row < 8; row++)
            {
                // Add some indentation to demonstrate usage of per-column IndentEnable/IndentDisable flags.
                ImGui::Indent(indent_step);
                ImGui::TableNextRow();
                for (int column = 0; column < column_count; column++)
                {
                    ImGui::TableSetColumnIndex(column);
                    ImGui::Text("%s %s", (column == 0) ? "Indented" : "Hello", ImGui::TableGetColumnName(column));
                }
            }
            ImGui::Unindent(indent_step * 8.0f);

            ImGui::EndTable();
        }
        ImGui::TreePop();
    }

    if (open_action != -1)
        ImGui::SetNextItemOpen(open_action != 0);
    if (ImGui::TreeNode("Columns widths"))
    {
```

