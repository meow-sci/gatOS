# Widgets/Fonts

- Marker: IMGUI_DEMO_MARKER("Widgets/Fonts")
- Source: .github/skills/imgui/demo.cpp:1038
- Summary: Demonstrates Fonts behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Fonts");
        ImFontAtlas* atlas = ImGui::GetIO().Fonts;
        ImGui::ShowFontAtlas(atlas);
        // FIXME-NEWATLAS: Provide a demo to add/create a procedural font?
        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsImages()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsImages()
{
    if (ImGui::TreeNode("Images"))
    {
```

