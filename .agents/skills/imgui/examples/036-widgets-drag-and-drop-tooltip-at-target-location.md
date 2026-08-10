# Widgets/Drag and Drop/Tooltip at target location

- Marker: IMGUI_DEMO_MARKER("Widgets/Drag and Drop/Tooltip at target location")
- Source: .github/skills/imgui/demo.cpp:944
- Summary: Demonstrates Tooltip at target location behavior within Widgets / Drag and Drop.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Drag and Drop/Tooltip at target location");
            for (int n = 0; n < 2; n++)
            {
                // Drop targets
                ImGui::Button(n ? "drop here##1" : "drop here##0");
                if (ImGui::BeginDragDropTarget())
                {
                    ImGuiDragDropFlags drop_target_flags = ImGuiDragDropFlags_AcceptBeforeDelivery | ImGuiDragDropFlags_AcceptNoPreviewTooltip;
                    if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload(IMGUI_PAYLOAD_TYPE_COLOR_4F, drop_target_flags))
                    {
                        IM_UNUSED(payload);
                        ImGui::SetMouseCursor(ImGuiMouseCursor_NotAllowed);
                        ImGui::SetTooltip("Cannot drop here!");
                    }
                    ImGui::EndDragDropTarget();
                }

                // Drop source
                static ImVec4 col4 = { 1.0f, 0.0f, 0.2f, 1.0f };
                if (n == 0)
                    ImGui::ColorButton("drag me", col4);

            }
            ImGui::TreePop();
        }

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsDragsAndSliders()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsDragsAndSliders()
{
    if (ImGui::TreeNode("Drag/Slider Flags"))
    {
```

