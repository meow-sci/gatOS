# Widgets/Selection State/Multi-Select (manual/simplified, without BeginMultiSelect)

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (manual/simplified, without BeginMultiSelect)")
- Source: .github/skills/imgui/demo.cpp:2015
- Summary: Demonstrates manual simplified multi-select behavior without BeginMultiSelect within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (manual/simplified, without BeginMultiSelect)");
            HelpMarker("Hold Ctrl and Click to select multiple items.");
            static bool selection[5] = { false, false, false, false, false };
            for (int n = 0; n < 5; n++)
            {
                char buf[32];
                sprintf(buf, "Object %d", n);
                if (ImGui::Selectable(buf, selection[n]))
                {
                    if (!ImGui::GetIO().KeyCtrl) // Clear selection when Ctrl is not held
                        memset(selection, 0, sizeof(selection));
                    selection[n] ^= 1; // Toggle current item
                }
            }
            ImGui::TreePop();
        }

        // Demonstrate handling proper multi-selection using the BeginMultiSelect/EndMultiSelect API.
        // Shift+Click w/ Ctrl and other standard features are supported.
        // We use the ImGuiSelectionBasicStorage helper which you may freely reimplement.
        if (ImGui::TreeNode("Multi-Select"))
        {
```

