# Widgets/Selection State/Multi-Select (in a table)

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (in a table)")
- Source: .github/skills/imgui/demo.cpp:2202
- Summary: Demonstrates Multi-Select (in a table) behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (in a table)");
            static ImGuiSelectionBasicStorage selection;

            const int ITEMS_COUNT = 10000;
            ImGui::Text("Selection: %d/%d", selection.Size, ITEMS_COUNT);
            if (ImGui::BeginTable("##Basket", 2, ImGuiTableFlags_ScrollY | ImGuiTableFlags_RowBg | ImGuiTableFlags_BordersOuter, ImVec2(0.0f, ImGui::GetFontSize() * 20)))
            {
                ImGui::TableSetupColumn("Object");
                ImGui::TableSetupColumn("Action");
                ImGui::TableSetupScrollFreeze(0, 1);
                ImGui::TableHeadersRow();

                ImGuiMultiSelectFlags flags = ImGuiMultiSelectFlags_ClearOnEscape | ImGuiMultiSelectFlags_BoxSelect1d;
                ImGuiMultiSelectIO* ms_io = ImGui::BeginMultiSelect(flags, selection.Size, ITEMS_COUNT);
                selection.ApplyRequests(ms_io);

                ImGuiListClipper clipper;
                clipper.Begin(ITEMS_COUNT);
                if (ms_io->RangeSrcItem != -1)
                    clipper.IncludeItemByIndex((int)ms_io->RangeSrcItem); // Ensure RangeSrc item is not clipped.
                while (clipper.Step())
                {
                    for (int n = clipper.DisplayStart; n < clipper.DisplayEnd; n++)
                    {
                        ImGui::TableNextRow();
                        ImGui::TableNextColumn();
                        ImGui::PushID(n);
                        char label[64];
                        sprintf(label, "Object %05d: %s", n, ExampleNames[n % IM_COUNTOF(ExampleNames)]);
                        bool item_is_selected = selection.Contains((ImGuiID)n);
                        ImGui::SetNextItemSelectionUserData(n);
                        ImGui::Selectable(label, item_is_selected, ImGuiSelectableFlags_SpanAllColumns | ImGuiSelectableFlags_AllowOverlap);
                        ImGui::TableNextColumn();
                        ImGui::SmallButton("hello");
                        ImGui::PopID();
                    }
                }

                ms_io = ImGui::EndMultiSelect();
                selection.ApplyRequests(ms_io);
                ImGui::EndTable();
            }
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Multi-Select (checkboxes)"))
        {
```

