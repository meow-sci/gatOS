# Widgets/Selection State/Multi-Select (with deletion)

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (with deletion)")
- Source: .github/skills/imgui/demo.cpp:2125
- Summary: Demonstrates Multi-Select (with deletion) behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (with deletion)");
            // Storing items data separately from selection data.
            // (you may decide to store selection data inside your item (aka intrusive storage) if you don't need multiple views over same items)
            // Use a custom selection.Adapter: store item identifier in Selection (instead of index)
            static ImVector<ImGuiID> items;
            static ExampleSelectionWithDeletion selection;
            selection.UserData = (void*)&items;
            selection.AdapterIndexToStorageId = [](ImGuiSelectionBasicStorage* self, int idx) { ImVector<ImGuiID>* p_items = (ImVector<ImGuiID>*)self->UserData; return (*p_items)[idx]; }; // Index -> ID

            ImGui::Text("Added features:");
            ImGui::BulletText("Dynamic list with Delete key support.");
            ImGui::Text("Selection size: %d/%d", selection.Size, items.Size);

            // Initialize default list with 50 items + button to add/remove items.
            static ImGuiID items_next_id = 0;
            if (items_next_id == 0)
                for (ImGuiID n = 0; n < 50; n++)
                    items.push_back(items_next_id++);
            if (ImGui::SmallButton("Add 20 items"))     { for (int n = 0; n < 20; n++) { items.push_back(items_next_id++); } }
            ImGui::SameLine();
            if (ImGui::SmallButton("Remove 20 items"))  { for (int n = IM_MIN(20, items.Size); n > 0; n--) { selection.SetItemSelected(items.back(), false); items.pop_back(); } }

            // (1) Extra to support deletion: Submit scrolling range to avoid glitches on deletion
            const float items_height = ImGui::GetTextLineHeightWithSpacing();
            ImGui::SetNextWindowContentSize(ImVec2(0.0f, items.Size * items_height));

            if (ImGui::BeginChild("##Basket", ImVec2(-FLT_MIN, ImGui::GetFontSize() * 20), ImGuiChildFlags_FrameStyle | ImGuiChildFlags_ResizeY))
            {
                ImGuiMultiSelectFlags flags = ImGuiMultiSelectFlags_ClearOnEscape | ImGuiMultiSelectFlags_BoxSelect1d;
                ImGuiMultiSelectIO* ms_io = ImGui::BeginMultiSelect(flags, selection.Size, items.Size);
                selection.ApplyRequests(ms_io);

                const bool want_delete = ImGui::Shortcut(ImGuiKey_Delete, ImGuiInputFlags_Repeat) && (selection.Size > 0);
                const int item_curr_idx_to_focus = want_delete ? selection.ApplyDeletionPreLoop(ms_io, items.Size) : -1;

                for (int n = 0; n < items.Size; n++)
                {
                    const ImGuiID item_id = items[n];
                    char label[64];
                    sprintf(label, "Object %05u: %s", item_id, ExampleNames[item_id % IM_COUNTOF(ExampleNames)]);

                    bool item_is_selected = selection.Contains(item_id);
                    ImGui::SetNextItemSelectionUserData(n);
                    ImGui::Selectable(label, item_is_selected);
                    if (item_curr_idx_to_focus == n)
                        ImGui::SetKeyboardFocusHere(-1);
                }

                // Apply multi-select requests
                ms_io = ImGui::EndMultiSelect();
                selection.ApplyRequests(ms_io);
                if (want_delete)
                    selection.ApplyDeletionPostLoop(ms_io, items, item_curr_idx_to_focus);
            }
            ImGui::EndChild();
            ImGui::TreePop();
        }

        // Implement a Dual List Box (#6648)
        if (ImGui::TreeNode("Multi-Select (dual list box)"))
        {
```

