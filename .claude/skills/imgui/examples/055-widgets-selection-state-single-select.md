# Widgets/Selection State/Single-Select

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Single-Select")
- Source: .github/skills/imgui/demo.cpp:1999
- Summary: Demonstrates Single-Select behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Single-Select");
            static int selected = -1;
            for (int n = 0; n < 5; n++)
            {
                char buf[32];
                sprintf(buf, "Object %d", n);
                if (ImGui::Selectable(buf, selected == n))
                    selected = n;
            }
            ImGui::TreePop();
        }

        // Demonstrate implementation a most-basic form of multi-selection manually
        // This doesn't support the Shift modifier which requires BeginMultiSelect()!
        if (ImGui::TreeNode("Multi-Select (manual/simplified, without BeginMultiSelect)"))
        {
```

