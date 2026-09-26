# Widgets/Tree Nodes/Clipping Large Trees

- Marker: IMGUI_DEMO_MARKER("Widgets/Tree Nodes/Clipping Large Trees")
- Source: .github/skills/imgui/demo.cpp:3409
- Summary: Demonstrates Clipping Large Trees behavior within Widgets / Tree Nodes.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Tree Nodes/Clipping Large Trees");
            ImGui::TextWrapped(
                "- Using ImGuiListClipper with trees is a less easy than on arrays or grids.\n"
                "- Refer to 'Demo->Examples->Property Editor' for an example of how to do that.\n"
                "- Discuss in #3823");
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Advanced, with Selectable nodes"))
        {
```

