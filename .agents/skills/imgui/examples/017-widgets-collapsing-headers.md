# Widgets/Collapsing Headers

- Marker: IMGUI_DEMO_MARKER("Widgets/Collapsing Headers")
- Source: .github/skills/imgui/demo.cpp:345
- Summary: Demonstrates Collapsing Headers behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Collapsing Headers");
        static bool closable_group = true;
        ImGui::Checkbox("Show 2nd header", &closable_group);
        if (ImGui::CollapsingHeader("Header", ImGuiTreeNodeFlags_None))
        {
            ImGui::Text("IsItemHovered: %d", ImGui::IsItemHovered());
            for (int i = 0; i < 5; i++)
                ImGui::Text("Some content %d", i);
        }
        if (ImGui::CollapsingHeader("Header with a close button", &closable_group))
        {
            ImGui::Text("IsItemHovered: %d", ImGui::IsItemHovered());
            for (int i = 0; i < 5; i++)
                ImGui::Text("More content %d", i);
        }
        /*
        if (ImGui::CollapsingHeader("Header with a bullet", ImGuiTreeNodeFlags_Bullet))
            ImGui::Text("IsItemHovered: %d", ImGui::IsItemHovered());
        */
        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsColorAndPickers()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsColorAndPickers()
{
    if (ImGui::TreeNode("Color/Picker Widgets"))
    {
```

