# Widgets

- Marker: IMGUI_DEMO_MARKER("Widgets")
- Source: .github/skills/imgui/demo.cpp:3603
- Summary: Demonstrates Widgets functionality.

```cpp
    // IMGUI_DEMO_MARKER("Widgets");

    const bool disable_all = demo_data->DisableSections; // The Checkbox for that is inside the "Disabled" section at the bottom
    if (disable_all)
        ImGui::BeginDisabled();

    DemoWindowWidgetsBasic();
    DemoWindowWidgetsBullets();
    DemoWindowWidgetsCollapsingHeaders();
    DemoWindowWidgetsComboBoxes();
    DemoWindowWidgetsColorAndPickers();
    DemoWindowWidgetsDataTypes();

    if (disable_all)
        ImGui::EndDisabled();
    DemoWindowWidgetsDisableBlocks(demo_data);
    if (disable_all)
        ImGui::BeginDisabled();

    DemoWindowWidgetsDragAndDrop();
    DemoWindowWidgetsDragsAndSliders();
    DemoWindowWidgetsFonts();
    DemoWindowWidgetsImages();
    DemoWindowWidgetsListBoxes();
    DemoWindowWidgetsMultiComponents();
    DemoWindowWidgetsPlotting();
    DemoWindowWidgetsProgressBars();
    DemoWindowWidgetsQueryingStatuses();
    DemoWindowWidgetsSelectables();
    DemoWindowWidgetsSelectionAndMultiSelect(demo_data);
    DemoWindowWidgetsTabs();
    DemoWindowWidgetsText();
    DemoWindowWidgetsTextFilter();
    DemoWindowWidgetsTextInput();
    DemoWindowWidgetsTooltips();
    DemoWindowWidgetsTreeNodes();
    DemoWindowWidgetsVerticalSliders();

    if (disable_all)
        ImGui::EndDisabled();
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowLayout()
//-----------------------------------------------------------------------------

static void DemoWindowLayout()
{
    if (!ImGui::CollapsingHeader("Layout & Scrolling"))
        return;

    if (ImGui::TreeNode("Child windows"))
    {
```

