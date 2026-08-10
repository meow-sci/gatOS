# Tables/Context menus

- Marker: IMGUI_DEMO_MARKER("Tables/Context menus")
- Source: .github/skills/imgui/demo.cpp:6493
- Summary: Demonstrates Context menus behavior within Tables.

```cpp
        IMGUI_DEMO_MARKER("Tables/Context menus");
        HelpMarker(
            "By default, right-clicking over a TableHeadersRow()/TableHeader() line will open the default context-menu.\n"
            "Using ImGuiTableFlags_ContextMenuInBody we also allow right-clicking over columns body.");
        static ImGuiTableFlags flags1 = ImGuiTableFlags_Resizable | ImGuiTableFlags_Reorderable | ImGuiTableFlags_Hideable | ImGuiTableFlags_Borders | ImGuiTableFlags_ContextMenuInBody;

        PushStyleCompact();
        ImGui::CheckboxFlags("ImGuiTableFlags_ContextMenuInBody", &flags1, ImGuiTableFlags_ContextMenuInBody);
        PopStyleCompact();

        // Context Menus: first example
        // [1.1] Right-click on the TableHeadersRow() line to open the default table context menu.
        // [1.2] Right-click in columns also open the default table context menu (if ImGuiTableFlags_ContextMenuInBody is set)
        const int COLUMNS_COUNT = 3;
        if (ImGui::BeginTable("table_context_menu", COLUMNS_COUNT, flags1))
        {
            ImGui::TableSetupColumn("One");
            ImGui::TableSetupColumn("Two");
            ImGui::TableSetupColumn("Three");

            // [1.1]] Right-click on the TableHeadersRow() line to open the default table context menu.
            ImGui::TableHeadersRow();

            // Submit dummy contents
            for (int row = 0; row < 4; row++)
            {
                ImGui::TableNextRow();
                for (int column = 0; column < COLUMNS_COUNT; column++)
                {
                    ImGui::TableSetColumnIndex(column);
                    ImGui::Text("Cell %d,%d", column, row);
                }
            }
            ImGui::EndTable();
        }

        // Context Menus: second example
        // [2.1] Right-click on the TableHeadersRow() line to open the default table context menu.
        // [2.2] Right-click on the ".." to open a custom popup
        // [2.3] Right-click in columns to open another custom popup
        HelpMarker(
            "Demonstrate mixing table context menu (over header), item context button (over button) "
            "and custom per-column context menu (over column body).");
        ImGuiTableFlags flags2 = ImGuiTableFlags_Resizable | ImGuiTableFlags_SizingFixedFit | ImGuiTableFlags_Reorderable | ImGuiTableFlags_Hideable | ImGuiTableFlags_Borders;
        if (ImGui::BeginTable("table_context_menu_2", COLUMNS_COUNT, flags2))
        {
            ImGui::TableSetupColumn("One");
            ImGui::TableSetupColumn("Two");
            ImGui::TableSetupColumn("Three");

            // [2.1] Right-click on the TableHeadersRow() line to open the default table context menu.
            ImGui::TableHeadersRow();
            for (int row = 0; row < 4; row++)
            {
                ImGui::TableNextRow();
                for (int column = 0; column < COLUMNS_COUNT; column++)
                {
                    // Submit dummy contents
                    ImGui::TableSetColumnIndex(column);
                    ImGui::Text("Cell %d,%d", column, row);
                    ImGui::SameLine();

                    // [2.2] Right-click on the ".." to open a custom popup
                    ImGui::PushID(row * COLUMNS_COUNT + column);
                    ImGui::SmallButton("..");
                    if (ImGui::BeginPopupContextItem())
                    {
                        ImGui::Text("This is the popup for Button(\"..\") in Cell %d,%d", column, row);
                        if (ImGui::Button("Close"))
                            ImGui::CloseCurrentPopup();
                        ImGui::EndPopup();
                    }
                    ImGui::PopID();
                }
            }

            // [2.3] Right-click anywhere in columns to open another custom popup
            // (instead of testing for !IsAnyItemHovered() we could also call OpenPopup() with ImGuiPopupFlags_NoOpenOverExistingPopup
            // to manage popup priority as the popups triggers, here "are we hovering a column" are overlapping)
            int hovered_column = -1;
            for (int column = 0; column < COLUMNS_COUNT + 1; column++)
            {
                ImGui::PushID(column);
                if (ImGui::TableGetColumnFlags(column) & ImGuiTableColumnFlags_IsHovered)
                    hovered_column = column;
                if (hovered_column == column && !ImGui::IsAnyItemHovered() && ImGui::IsMouseReleased(1))
                    ImGui::OpenPopup("MyPopup");
                if (ImGui::BeginPopup("MyPopup"))
                {
                    if (column == COLUMNS_COUNT)
                        ImGui::Text("This is a custom popup for unused space after the last column.");
                    else
                        ImGui::Text("This is a custom popup for Column %d", column);
                    if (ImGui::Button("Close"))
                        ImGui::CloseCurrentPopup();
                    ImGui::EndPopup();
                }
                ImGui::PopID();
            }

            ImGui::EndTable();
            ImGui::Text("Hovered column: %d", hovered_column);
        }
        ImGui::TreePop();
    }

    // Demonstrate creating multiple tables with the same ID
    if (open_action != -1)
        ImGui::SetNextItemOpen(open_action != 0);
    if (ImGui::TreeNode("Synced instances"))
    {
```

