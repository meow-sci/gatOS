# Widgets/Tree Nodes/Hierarchy lines

- Marker: IMGUI_DEMO_MARKER("Widgets/Tree Nodes/Hierarchy lines")
- Source: .github/skills/imgui/demo.cpp:3380
- Summary: Demonstrates Hierarchy lines behavior within Widgets / Tree Nodes.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Tree Nodes/Hierarchy lines");
            static ImGuiTreeNodeFlags base_flags = ImGuiTreeNodeFlags_DrawLinesFull | ImGuiTreeNodeFlags_DefaultOpen;
            HelpMarker("Default option for DrawLinesXXX is stored in style.TreeLinesFlags");
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_DrawLinesNone", &base_flags, ImGuiTreeNodeFlags_DrawLinesNone);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_DrawLinesFull", &base_flags, ImGuiTreeNodeFlags_DrawLinesFull);
            ImGui::CheckboxFlags("ImGuiTreeNodeFlags_DrawLinesToNodes", &base_flags, ImGuiTreeNodeFlags_DrawLinesToNodes);

            if (ImGui::TreeNodeEx("Parent", base_flags))
            {
                if (ImGui::TreeNodeEx("Child 1", base_flags))
                {
                    ImGui::Button("Button for Child 1");
                    ImGui::TreePop();
                }
                if (ImGui::TreeNodeEx("Child 2", base_flags))
                {
                    ImGui::Button("Button for Child 2");
                    ImGui::TreePop();
                }
                ImGui::Text("Remaining contents");
                ImGui::Text("Remaining contents");
                ImGui::TreePop();
            }

            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Clipping Large Trees"))
        {
```

