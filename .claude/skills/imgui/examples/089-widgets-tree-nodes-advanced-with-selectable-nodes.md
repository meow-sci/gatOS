# Widgets/Tree Nodes/Advanced, with Selectable nodes

- Marker: IMGUI_DEMO_MARKER("Widgets/Tree Nodes/Advanced, with Selectable nodes")
- Source: .github/skills/imgui/demo.cpp:3419
- Summary: Demonstrates Advanced, with Selectable nodes behavior within Widgets / Tree Nodes.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Tree Nodes/Advanced, with Selectable nodes");
            HelpMarker(
                "This is a more typical looking tree with selectable nodes.\n"
                "Click to select, Ctrl+Click to toggle, click on arrows or double-click to open.");
            static ImGuiTreeNodeFlags base_flags = ImGuiTreeNodeFlags_OpenOnArrow | ImGuiTreeNodeFlags_OpenOnDoubleClick | ImGuiTreeNodeFlags_SpanAvailWidth;
            static bool align_label_with_current_x_position = false;
            static bool test_drag_and_drop = false;
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_OpenOnArrow", &base_flags, ImGuiTreeNodeFlags_OpenOnArrow);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_OpenOnDoubleClick", &base_flags, ImGuiTreeNodeFlags_OpenOnDoubleClick);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_SpanAvailWidth", &base_flags, ImGuiTreeNodeFlags_SpanAvailWidth); ImGui::SameLine(); HelpMarker("Extend hit area to all available width instead of allowing more items to be laid out after the node.");
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_SpanFullWidth", &base_flags, ImGuiTreeNodeFlags_SpanFullWidth);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_SpanLabelWidth", &base_flags, ImGuiTreeNodeFlags_SpanLabelWidth); ImGui::SameLine(); HelpMarker("Reduce hit area to the text label and a bit of margin.");
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_SpanAllColumns", &base_flags, ImGuiTreeNodeFlags_SpanAllColumns); ImGui::SameLine(); HelpMarker("For use in Tables only.");
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_AllowOverlap", &base_flags, ImGuiTreeNodeFlags_AllowOverlap);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_Framed", &base_flags, ImGuiTreeNodeFlags_Framed); ImGui::SameLine(); HelpMarker("Draw frame with background (e.g. for CollapsingHeader)");
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_FramePadding", &base_flags, ImGuiTreeNodeFlags_FramePadding);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_NavLeftJumpsToParent", &base_flags, ImGuiTreeNodeFlags_NavLeftJumpsToParent);

            HelpMarker("Default option for DrawLinesXXX is stored in style.TreeLinesFlags");
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_DrawLinesNone", &base_flags, ImGuiTreeNodeFlags_DrawLinesNone);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_DrawLinesFull", &base_flags, ImGuiTreeNodeFlags_DrawLinesFull);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_DrawLinesToNodes", &base_flags, ImGuiTreeNodeFlags_DrawLinesToNodes);

            ImGui::Checkbox("Align label with current X position", &align_label_with_current_x_position);
            ImGui::Checkbox("Test tree node as drag source", &test_drag_and_drop);
            ImGui::Text("Hello!");
            if (align_label_with_current_x_position)
                ImGui::Unindent(ImGui::GetTreeNodeToLabelSpacing());

            // 'selection_mask' is dumb representation of what may be user-side selection state.
            //  You may retain selection state inside or outside your objects in whatever format you see fit.
            // 'node_clicked' is temporary storage of what node we have clicked to process selection at the end
            /// of the loop. May be a pointer to your own node type, etc.
            static int selection_mask = (1 << 2);
            int node_clicked = -1;
            for (int i = 0; i < 6; i++)
            {
                // Disable the default "open on single-click behavior" + set Selected flag according to our selection.
                // To alter selection we use IsItemClicked() && !IsItemToggledOpen(), so clicking on an arrow doesn't alter selection.
                ImGuiTreeNodeFlags node_flags = base_flags;
                const bool is_selected = (selection_mask & (1 << i)) != 0;
                if (is_selected)
                    node_flags |= ImGuiTreeNodeFlags_Selected;
                if (i < 3)
                {
                    // Items 0..2 are Tree Node
                    bool node_open = ImGui::TreeNodeEx((void*)(intptr_t)i, node_flags, "Selectable Node %d", i);
                    if (ImGui::IsItemClicked() && !ImGui::IsItemToggledOpen())
                        node_clicked = i;
                    if (test_drag_and_drop && ImGui::BeginDragDropSource())
                    {
                        ImGui::SetDragDropPayload("_TREENODE", NULL, 0);
                        ImGui::Text("This is a drag and drop source");
                        ImGui::EndDragDropSource();
                    }
                    if (i == 2 && (base_flags & ImGuiTreeNodeFlags_SpanLabelWidth))
                    {
                        // Item 2 has an additional inline button to help demonstrate SpanLabelWidth.
                        ImGui::SameLine();
                        if (ImGui::SmallButton("button")) {}
                    }
                    if (node_open)
                    {
                        ImGui::BulletText("Blah blah\nBlah Blah");
                        ImGui::SameLine();
                        ImGui::SmallButton("Button");
                        ImGui::TreePop();
                    }
                }
                else
                {
                    // Items 3..5 are Tree Leaves
                    // The only reason we use TreeNode at all is to allow selection of the leaf. Otherwise we can
                    // use BulletText() or advance the cursor by GetTreeNodeToLabelSpacing() and call Text().
                    node_flags |= ImGuiTreeNodeFlags_Leaf | ImGuiTreeNodeFlags_NoTreePushOnOpen; // ImGuiTreeNodeFlags_Bullet
                    ImGui::TreeNodeEx((void*)(intptr_t)i, node_flags, "Selectable Leaf %d", i);
                    if (ImGui::IsItemClicked() && !ImGui::IsItemToggledOpen())
                        node_clicked = i;
                    if (test_drag_and_drop && ImGui::BeginDragDropSource())
                    {
                        ImGui::SetDragDropPayload("_TREENODE", NULL, 0);
                        ImGui::Text("This is a drag and drop source");
                        ImGui::EndDragDropSource();
                    }
                }
            }
            if (node_clicked != -1)
            {
                // Update selection state
                // (process outside of tree loop to avoid visual inconsistencies during the clicking frame)
                if (ImGui::GetIO().KeyCtrl)
                    selection_mask ^= (1 << node_clicked);          // Ctrl+Click to toggle
                else //if (!(selection_mask & (1 << node_clicked))) // Depending on selection behavior you want, may want to preserve selection when clicking on item that is part of the selection
                    selection_mask = (1 << node_clicked);           // Click to single-select
            }
            if (align_label_with_current_x_position)
                ImGui::Indent(ImGui::GetTreeNodeToLabelSpacing());
            ImGui::TreePop();
        }
        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsVerticalSliders()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsVerticalSliders()
{
    if (ImGui::TreeNode("Vertical Sliders"))
    {
```

