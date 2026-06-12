# Widgets/Selection State/Multi-Select (dual list box)

- Marker: IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (dual list box)")
- Source: .github/skills/imgui/demo.cpp:2186
- Summary: Demonstrates Multi-Select (dual list box) behavior within Widgets / Selection State.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selection State/Multi-Select (dual list box)");
            // Init default state
            static ExampleDualListBox dlb;
            if (dlb.Items[0].Size == 0 && dlb.Items[1].Size == 0)
                for (int item_id = 0; item_id < IM_COUNTOF(ExampleNames); item_id++)
                    dlb.Items[0].push_back((ImGuiID)item_id);

            // Show
            dlb.Show();

            ImGui::TreePop();
        }

        // Demonstrate using the clipper with BeginMultiSelect()/EndMultiSelect()
        if (ImGui::TreeNode("Multi-Select (in a table)"))
        {
```

