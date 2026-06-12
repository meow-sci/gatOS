# Widgets/Selection State/Multi-Select (multiple scopes)

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (multiple scopes)")
- Source: .github/skills/imgui/demo.cpp:2287
- Summary: Demonstrates Multi-Select (multiple scopes) behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (multiple scopes)");
            // Use default select: Pass index to SetNextItemSelectionUserData(), store index in Selection
            const int SCOPES_COUNT = 3;
            const int ITEMS_COUNT = 8; // Per scope
            static ImGuiSelectionBasicStorage selections_data[SCOPES_COUNT];

            // Use ImGuiMultiSelectFlags_ScopeRect to not affect other selections in same window.
            static ImGuiMultiSelectFlags flags = ImGuiMultiSelectFlags_ScopeRect | ImGuiMultiSelectFlags_ClearOnEscape;// | ImGuiMultiSelectFlags_ClearOnClickVoid;
            if (ImGui::CheckboxFlags("ImGuiMultiSelectFlags_ScopeWindow", &flags, ImGuiMultiSelectFlags_ScopeWindow) && (flags & ImGuiMultiSelectFlags_ScopeWindow))
                flags &= ~ImGuiMultiSelectFlags_ScopeRect;
            if (ImGui::CheckboxFlags("ImGuiMultiSelectFlags_ScopeRect", &flags, ImGuiMultiSelectFlags_ScopeRect) && (flags & ImGuiMultiSelectFlags_ScopeRect))
                flags &= ~ImGuiMultiSelectFlags_ScopeWindow;
            ImGui::CheckboxFlags("ImGuiMultiSelectFlags_ClearOnClickVoid", &flags, ImGuiMultiSelectFlags_ClearOnClickVoid);
            ImGui::CheckboxFlags("ImGuiMultiSelectFlags_BoxSelect1d", &flags, ImGuiMultiSelectFlags_BoxSelect1d);

            for (int selection_scope_n = 0; selection_scope_n < SCOPES_COUNT; selection_scope_n++)
            {
                ImGui::PushID(selection_scope_n);
                ImGuiSelectionBasicStorage* selection = &selections_data[selection_scope_n];
                ImGuiMultiSelectIO* ms_io = ImGui::BeginMultiSelect(flags, selection->Size, ITEMS_COUNT);
                selection->ApplyRequests(ms_io);

                ImGui::SeparatorText("Selection scope");
                ImGui::Text("Selection size: %d/%d", selection->Size, ITEMS_COUNT);

                for (int n = 0; n < ITEMS_COUNT; n++)
                {
                    char label[64];
                    sprintf(label, "Object %05d: %s", n, ExampleNames[n % IM_COUNTOF(ExampleNames)]);
                    bool item_is_selected = selection->Contains((ImGuiID)n);
                    ImGui::SetNextItemSelectionUserData(n);
                    ImGui::Selectable(label, item_is_selected);
                }

                // Apply multi-select requests
                ms_io = ImGui::EndMultiSelect();
                selection->ApplyRequests(ms_io);
                ImGui::PopID();
            }
            ImGui::TreePop();
        }

        // See ShowExampleAppAssetsBrowser()
        if (ImGui::TreeNode("Multi-Select (tiled assets browser)"))
        {
            ImGui::Checkbox("Assets Browser", &demo_data->ShowAppAssetsBrowser);
            ImGui::Text("(also access from 'Examples->Assets Browser' in menu)");
            ImGui::TreePop();
        }

        // Demonstrate supporting multiple-selection in a tree.
        // - We don't use linear indices for selection user data, but our ExampleTreeNode* pointer directly!
        //   This showcase how SetNextItemSelectionUserData() never assume indices!
        // - The difficulty here is to "interpolate" from RangeSrcItem to RangeDstItem in the SetAll/SetRange request.
        //   We want this interpolation to match what the user sees: in visible order, skipping closed nodes.
        //   This is implemented by our TreeGetNextNodeInVisibleOrder() user-space helper.
        // - Important: In a real codebase aiming to implement full-featured selectable tree with custom filtering, you
        //   are more likely to build an array mapping sequential indices to visible tree nodes, since your
        //   filtering/search + clipping process will benefit from it. Having this will make this interpolation much easier.
        // - Consider this a prototype: we are working toward simplifying some of it.
        if (ImGui::TreeNode("Multi-Select (trees)"))
        {
```

