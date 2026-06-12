# Widgets/Selection State/Multi-Select (checkboxes)

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (checkboxes)")
- Source: .github/skills/imgui/demo.cpp:2249
- Summary: Demonstrates Multi-Select (checkboxes) behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (checkboxes)");
            ImGui::Text("In a list of checkboxes (not selectable):");
            ImGui::BulletText("Using _NoAutoSelect + _NoAutoClear flags.");
            ImGui::BulletText("Shift+Click to check multiple boxes.");
            ImGui::BulletText("Shift+Keyboard to copy current value to other boxes.");

            // If you have an array of checkboxes, you may want to use NoAutoSelect + NoAutoClear and the ImGuiSelectionExternalStorage helper.
            static bool items[20] = {};
            static ImGuiMultiSelectFlags flags = ImGuiMultiSelectFlags_NoAutoSelect | ImGuiMultiSelectFlags_NoAutoClear | ImGuiMultiSelectFlags_ClearOnEscape;
            ImGui::CheckboxFlags("ImGuiMultiSelectFlags_NoAutoSelect", &flags, ImGuiMultiSelectFlags_NoAutoSelect);
            ImGui::CheckboxFlags("ImGuiMultiSelectFlags_NoAutoClear", &flags, ImGuiMultiSelectFlags_NoAutoClear);
            ImGui::CheckboxFlags("ImGuiMultiSelectFlags_BoxSelect2d", &flags, ImGuiMultiSelectFlags_BoxSelect2d); // Cannot use ImGuiMultiSelectFlags_BoxSelect1d as checkboxes are varying width.

            if (ImGui::BeginChild("##Basket", ImVec2(-FLT_MIN, ImGui::GetFontSize() * 20), ImGuiChildFlags_Borders | ImGuiChildFlags_ResizeY))
            {
                ImGuiMultiSelectIO* ms_io = ImGui::BeginMultiSelect(flags, -1, IM_COUNTOF(items));
                ImGuiSelectionExternalStorage storage_wrapper;
                storage_wrapper.UserData = (void*)items;
                storage_wrapper.AdapterSetItemSelected = [](ImGuiSelectionExternalStorage* self, int n, bool selected) { bool* array = (bool*)self->UserData; array[n] = selected; };
                storage_wrapper.ApplyRequests(ms_io);
                for (int n = 0; n < 20; n++)
                {
                    char label[32];
                    sprintf(label, "Item %d", n);
                    ImGui::SetNextItemSelectionUserData(n);
                    ImGui::Checkbox(label, &items[n]);
                }
                ms_io = ImGui::EndMultiSelect();
                storage_wrapper.ApplyRequests(ms_io);
            }
            ImGui::EndChild();

            ImGui::TreePop();
        }

        // Demonstrate individual selection scopes in same window
        if (ImGui::TreeNode("Multi-Select (multiple scopes)"))
        {
```

