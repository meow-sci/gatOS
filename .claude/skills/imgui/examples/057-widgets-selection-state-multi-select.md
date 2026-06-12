# Widgets/Selection State/Multi-Select

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select")
- Source: .github/skills/imgui/demo.cpp:2037
- Summary: Demonstrates Multi-Select behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select");
            ImGui::Text("Supported features:");
            ImGui::BulletText("Keyboard navigation (arrows, page up/down, home/end, space).");
            ImGui::BulletText("Ctrl modifier to preserve and toggle selection.");
            ImGui::BulletText("Shift modifier for range selection.");
            ImGui::BulletText("Ctrl+A to select all.");
            ImGui::BulletText("Escape to clear selection.");
            ImGui::BulletText("Click and drag to box-select.");
            ImGui::Text("Tip: Use 'Demo->Tools->Debug Log->Selection' to see selection requests as they happen.");

            // Use default selection.Adapter: Pass index to SetNextItemSelectionUserData(), store index in Selection
            const int ITEMS_COUNT = 50;
            static ImGuiSelectionBasicStorage selection;
            ImGui::Text("Selection: %d/%d", selection.Size, ITEMS_COUNT);

            // The BeginChild() has no purpose for selection logic, other that offering a scrolling region.
            if (ImGui::BeginChild("##Basket", ImVec2(-FLT_MIN, ImGui::GetFontSize() * 20), ImGuiChildFlags_FrameStyle | ImGuiChildFlags_ResizeY))
            {
                ImGuiMultiSelectFlags flags = ImGuiMultiSelectFlags_ClearOnEscape | ImGuiMultiSelectFlags_BoxSelect1d;
                ImGuiMultiSelectIO* ms_io = ImGui::BeginMultiSelect(flags, selection.Size, ITEMS_COUNT);
                selection.ApplyRequests(ms_io);

                for (int n = 0; n < ITEMS_COUNT; n++)
                {
                    char label[64];
                    sprintf(label, "Object %05d: %s", n, ExampleNames[n % IM_COUNTOF(ExampleNames)]);
                    bool item_is_selected = selection.Contains((ImGuiID)n);
                    ImGui::SetNextItemSelectionUserData(n);
                    ImGui::Selectable(label, item_is_selected);
                }

                ms_io = ImGui::EndMultiSelect();
                selection.ApplyRequests(ms_io);
            }
            ImGui::EndChild();
            ImGui::TreePop();
        }

        // Demonstrate using the clipper with BeginMultiSelect()/EndMultiSelect()
        if (ImGui::TreeNode("Multi-Select (with clipper)"))
        {
```

