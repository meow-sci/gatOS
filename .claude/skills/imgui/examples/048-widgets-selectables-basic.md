# Widgets/Selectables/Basic

- Marker: IMGUI_DEMO_MARKER("Widgets/Selectables/Basic")
- Source: .github/skills/imgui/demo.cpp:1599
- Summary: Demonstrates Basic behavior within Widgets / Selectables.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Selectables/Basic");
        if (ImGui::TreeNode("Basic"))
        {
            static bool selection[5] = { false, true, false, false };
            ImGui::Selectable("1. I am selectable", &selection[0]);
            ImGui::Selectable("2. I am selectable", &selection[1]);
            ImGui::Selectable("3. I am selectable", &selection[2]);
            if (ImGui::Selectable("4. I am double clickable", selection[3], ImGuiSelectableFlags_AllowDoubleClick))
                if (ImGui::IsMouseDoubleClicked(0))
                    selection[3] = !selection[3];
            ImGui::TreePop();
        }
```

