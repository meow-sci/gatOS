# Widgets/Color/ColorPicker

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorPicker")
- Source: .github/skills/imgui/demo.cpp:488
- Summary: Demonstrates ColorPicker behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorPicker");
        ImGui::SeparatorText("Color picker");

        static bool ref_color = false;
        static ImVec4 ref_color_v(1.0f, 0.0f, 1.0f, 0.5f);
        static int picker_mode = 0;
        static int display_mode = 0;
        static ImGuiColorEditFlags color_picker_flags = ImGuiColorEditFlags_AlphaBar;

        ImGui::PushID("Color picker");
        ImGui::CheckboxFlags("ImGuiColorEditFlags_NoAlpha", &color_picker_flags, ImGuiColorEditFlags_NoAlpha);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_AlphaBar", &color_picker_flags, ImGuiColorEditFlags_AlphaBar);
        ImGui::CheckboxFlags("ImGuiColorEditFlags_NoSidePreview", &color_picker_flags, ImGuiColorEditFlags_NoSidePreview);
        if (color_picker_flags & ImGuiColorEditFlags_NoSidePreview)
        {
            ImGui::SameLine();
            ImGui::Checkbox("With Ref Color", &ref_color);
            if (ref_color)
            {
                ImGui::SameLine();
                ImGui::ColorEdit4("##RefColor", &ref_color_v.x, ImGuiColorEditFlags_NoInputs | base_flags);
            }
        }

        ImGui::Combo("Picker Mode", &picker_mode, "Auto/Current\0ImGuiColorEditFlags_PickerHueBar\0ImGuiColorEditFlags_PickerHueWheel\0");
        ImGui::SameLine(); HelpMarker("When not specified explicitly, user can right-click the picker to change mode.");

        ImGui::Combo("Display Mode", &display_mode, "Auto/Current\0ImGuiColorEditFlags_NoInputs\0ImGuiColorEditFlags_DisplayRGB\0ImGuiColorEditFlags_DisplayHSV\0ImGuiColorEditFlags_DisplayHex\0");
        ImGui::SameLine(); HelpMarker(
            "ColorEdit defaults to displaying RGB inputs if you don't specify a display mode, "
            "but the user can change it with a right-click on those inputs.\n\nColorPicker defaults to displaying RGB+HSV+Hex "
            "if you don't specify a display mode.\n\nYou can change the defaults using SetColorEditOptions().");

        ImGuiColorEditFlags flags = base_flags | color_picker_flags;
        if (picker_mode == 1)  flags |= ImGuiColorEditFlags_PickerHueBar;
        if (picker_mode == 2)  flags |= ImGuiColorEditFlags_PickerHueWheel;
        if (display_mode == 1) flags |= ImGuiColorEditFlags_NoInputs;       // Disable all RGB/HSV/Hex displays
        if (display_mode == 2) flags |= ImGuiColorEditFlags_DisplayRGB;     // Override display mode
        if (display_mode == 3) flags |= ImGuiColorEditFlags_DisplayHSV;
        if (display_mode == 4) flags |= ImGuiColorEditFlags_DisplayHex;
        ImGui::ColorPicker4("MyColor##4", (float*)&color, flags, ref_color ? &ref_color_v.x : NULL);

        ImGui::Text("Set defaults in code:");
        ImGui::SameLine(); HelpMarker(
            "SetColorEditOptions() is designed to allow you to set boot-time default.\n"
            "We don't have Push/Pop functions because you can force options on a per-widget basis if needed, "
            "and the user can change non-forced ones with the options menu.\nWe don't have a getter to avoid "
            "encouraging you to persistently save values that aren't forward-compatible.");
        if (ImGui::Button("Default: Uint8 + HSV + Hue Bar"))
            ImGui::SetColorEditOptions(ImGuiColorEditFlags_Uint8 | ImGuiColorEditFlags_DisplayHSV | ImGuiColorEditFlags_PickerHueBar);
        if (ImGui::Button("Default: Float + HDR + Hue Wheel"))
            ImGui::SetColorEditOptions(ImGuiColorEditFlags_Float | ImGuiColorEditFlags_HDR | ImGuiColorEditFlags_PickerHueWheel);

        // Always display a small version of both types of pickers
        // (that's in order to make it more visible in the demo to people who are skimming quickly through it)
        ImGui::Text("Both types:");
        float w = (ImGui::GetContentRegionAvail().x - ImGui::GetStyle().ItemSpacing.y) * 0.40f;
        ImGui::SetNextItemWidth(w);
        ImGui::ColorPicker3("##MyColor##5", (float*)&color, ImGuiColorEditFlags_PickerHueBar | ImGuiColorEditFlags_NoSidePreview | ImGuiColorEditFlags_NoInputs | ImGuiColorEditFlags_NoAlpha);
        ImGui::SameLine();
        ImGui::SetNextItemWidth(w);
        ImGui::ColorPicker3("##MyColor##6", (float*)&color, ImGuiColorEditFlags_PickerHueWheel | ImGuiColorEditFlags_NoSidePreview | ImGuiColorEditFlags_NoInputs | ImGuiColorEditFlags_NoAlpha);
        ImGui::PopID();

        // HSV encoded support (to avoid RGB<>HSV round trips and singularities when S==0 or V==0)
        static ImVec4 color_hsv(0.23f, 1.0f, 1.0f, 1.0f); // Stored as HSV!
        ImGui::Spacing();
        ImGui::Text("HSV encoded colors");
        ImGui::SameLine(); HelpMarker(
            "By default, colors are given to ColorEdit and ColorPicker in RGB, but ImGuiColorEditFlags_InputHSV "
            "allows you to store colors as HSV and pass them to ColorEdit and ColorPicker as HSV. This comes with the "
            "added benefit that you can manipulate hue values with the picker even when saturation or value are zero.");
        ImGui::Text("Color widget with InputHSV:");
        ImGui::ColorEdit4("HSV shown as RGB##1", (float*)&color_hsv, ImGuiColorEditFlags_DisplayRGB | ImGuiColorEditFlags_InputHSV | ImGuiColorEditFlags_Float);
        ImGui::ColorEdit4("HSV shown as HSV##1", (float*)&color_hsv, ImGuiColorEditFlags_DisplayHSV | ImGuiColorEditFlags_InputHSV | ImGuiColorEditFlags_Float);
        ImGui::DragFloat4("Raw HSV values", (float*)&color_hsv, 0.01f, 0.0f, 1.0f);

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsComboBoxes()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsComboBoxes()
{
    if (ImGui::TreeNode("Combo"))
    {
```

