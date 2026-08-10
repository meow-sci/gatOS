# Widgets/Disable Blocks

- Marker: IMGUI_DEMO_MARKER("Widgets/Disable Blocks")
- Source: .github/skills/imgui/demo.cpp:809
- Summary: Demonstrates Disable Blocks behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Disable Blocks");
        ImGui::Checkbox("Disable entire section above", &demo_data->DisableSections);
        ImGui::SameLine(); HelpMarker("Demonstrate using BeginDisabled()/EndDisabled() across other sections.");
        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsDragAndDrop()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsDragAndDrop()
{
    if (ImGui::TreeNode("Drag and Drop"))
    {
```

