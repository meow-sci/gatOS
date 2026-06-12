# Widgets/Drag and Drop/Drag to reorder items (simple)

- Marker: IMGUI_DEMO_MARKER("Widgets/Drag and Drop/Drag to reorder items (simple)")
- Source: .github/skills/imgui/demo.cpp:910
- Summary: Demonstrates Drag to reorder items (simple) behavior within Widgets / Drag and Drop.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Drag and Drop/Drag to reorder items (simple)");
            // FIXME: there is temporary (usually single-frame) ID Conflict during reordering as a same item may be submitting twice.
            // This code was always slightly faulty but in a way which was not easily noticeable.
            // Until we fix this, enable ImGuiItemFlags_AllowDuplicateId to disable detecting the issue.
            ImGui::PushItemFlag(ImGuiItemFlags_AllowDuplicateId, true);

            // Simple reordering
            HelpMarker(
                "We don't use the drag and drop api at all here! "
                "Instead we query when the item is held but not hovered, and order items accordingly.");
            static const char* item_names[] = { "Item One", "Item Two", "Item Three", "Item Four", "Item Five" };
            for (int n = 0; n < IM_COUNTOF(item_names); n++)
            {
                const char* item = item_names[n];
                ImGui::Selectable(item);

                if (ImGui::IsItemActive() && !ImGui::IsItemHovered())
                {
                    int n_next = n + (ImGui::GetMouseDragDelta(0).y < 0.f ? -1 : 1);
                    if (n_next >= 0 && n_next < IM_COUNTOF(item_names))
                    {
                        item_names[n] = item_names[n_next];
                        item_names[n_next] = item;
                        ImGui::ResetMouseDragDelta();
                    }
                }
            }

            ImGui::PopItemFlag();
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Tooltip at target location"))
        {
```

