# Widgets/Selection State & Multi-Select

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State & Multi-Select")
- Source: .github/skills/imgui/demo.cpp:1989
- Summary: Demonstrates Selection State & Multi-Select behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Selection State & Multi-Select");
        HelpMarker("Selections can be built using Selectable(), TreeNode() or other widgets. Selection state is owned by application code/data.");

        ImGui::BulletText("Wiki page:");
        ImGui::SameLine();
        ImGui::TextLinkOpenURL("imgui/wiki/Multi-Select", "https://github.com/ocornut/imgui/wiki/Multi-Select");

        // Without any fancy API: manage single-selection yourself.
        if (ImGui::TreeNode("Single-Select"))
        {
```

