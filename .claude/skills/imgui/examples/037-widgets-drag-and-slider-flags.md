# Widgets/Drag and Slider Flags

- Marker: IMGUI_DEMO_MARKER("Widgets/Drag and Slider Flags")
- Source: .github/skills/imgui/demo.cpp:982
- Summary: Demonstrates Drag and Slider Flags behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Drag and Slider Flags");
        // Demonstrate using advanced flags for DragXXX and SliderXXX functions. Note that the flags are the same!
        static ImGuiSliderFlags flags = ImGuiSliderFlags_None;
        ImGui::CheckboxFlags("ImGuiSliderFlags_AlwaysClamp", &flags, ImGuiSliderFlags_AlwaysClamp);
        ImGui::CheckboxFlags("ImGuiSliderFlags_ClampOnInput", &flags, ImGuiSliderFlags_ClampOnInput);
        ImGui::SameLine(); HelpMarker("Clamp value to min/max bounds when input manually with Ctrl+Click. By default Ctrl+Click allows going out of bounds.");
        ImGui::CheckboxFlags("ImGuiSliderFlags_ClampZeroRange", &flags, ImGuiSliderFlags_ClampZeroRange);
        ImGui::SameLine(); HelpMarker("Clamp even if min==max==0.0f. Otherwise DragXXX functions don't clamp.");
        ImGui::CheckboxFlags("ImGuiSliderFlags_Logarithmic", &flags, ImGuiSliderFlags_Logarithmic);
        ImGui::SameLine(); HelpMarker("Enable logarithmic editing (more precision for small values).");
        ImGui::CheckboxFlags("ImGuiSliderFlags_NoRoundToFormat", &flags, ImGuiSliderFlags_NoRoundToFormat);
        ImGui::SameLine(); HelpMarker("Disable rounding underlying value to match precision of the format string (e.g. %.3f values are rounded to those 3 digits).");
        ImGui::CheckboxFlags("ImGuiSliderFlags_NoInput", &flags, ImGuiSliderFlags_NoInput);
        ImGui::SameLine(); HelpMarker("Disable Ctrl+Click or Enter key allowing to input text directly into the widget.");
        ImGui::CheckboxFlags("ImGuiSliderFlags_NoSpeedTweaks", &flags, ImGuiSliderFlags_NoSpeedTweaks);
        ImGui::SameLine(); HelpMarker("Disable keyboard modifiers altering tweak speed. Useful if you want to alter tweak speed yourself based on your own logic.");
        ImGui::CheckboxFlags("ImGuiSliderFlags_WrapAround", &flags, ImGuiSliderFlags_WrapAround);
        ImGui::SameLine(); HelpMarker("Enable wrapping around from max to min and from min to max (only supported by DragXXX() functions)");
        ImGui::CheckboxFlags("ImGuiSliderFlags_ColorMarkers", &flags, ImGuiSliderFlags_ColorMarkers);

        // Drags
        static float drag_f = 0.5f;
        static float drag_f4[4];
        static int drag_i = 50;
        ImGui::Text("Underlying float value: %f", drag_f);
        ImGui::DragFloat("DragFloat (0 -> 1)", &drag_f, 0.005f, 0.0f, 1.0f, "%.3f", flags);
        ImGui::DragFloat("DragFloat (0 -> +inf)", &drag_f, 0.005f, 0.0f, FLT_MAX, "%.3f", flags);
        ImGui::DragFloat("DragFloat (-inf -> 1)", &drag_f, 0.005f, -FLT_MAX, 1.0f, "%.3f", flags);
        ImGui::DragFloat("DragFloat (-inf -> +inf)", &drag_f, 0.005f, -FLT_MAX, +FLT_MAX, "%.3f", flags);
        //ImGui::DragFloat("DragFloat (0 -> 0)", &drag_f, 0.005f, 0.0f, 0.0f, "%.3f", flags);           // To test ClampZeroRange
        //ImGui::DragFloat("DragFloat (100 -> 100)", &drag_f, 0.005f, 100.0f, 100.0f, "%.3f", flags);
        ImGui::DragInt("DragInt (0 -> 100)", &drag_i, 0.5f, 0, 100, "%d", flags);
        ImGui::DragFloat4("DragFloat4 (0 -> 1)", drag_f4, 0.005f, 0.0f, 1.0f, "%.3f", flags); // Multi-component item, mostly here to document the effect of ImGuiSliderFlags_ColorMarkers.

        // Sliders
        static float slider_f = 0.5f;
        static float slider_f4[4];
        static int slider_i = 50;
        const ImGuiSliderFlags flags_for_sliders = (flags & ~ImGuiSliderFlags_WrapAround);
        ImGui::Text("Underlying float value: %f", slider_f);
        ImGui::SliderFloat("SliderFloat (0 -> 1)", &slider_f, 0.0f, 1.0f, "%.3f", flags_for_sliders);
        ImGui::SliderInt("SliderInt (0 -> 100)", &slider_i, 0, 100, "%d", flags_for_sliders);
        ImGui::SliderFloat4("SliderFloat4 (0 -> 1)", slider_f4, 0.0f, 1.0f, "%.3f", flags); // Multi-component item, mostly here to document the effect of ImGuiSliderFlags_ColorMarkers.

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsFonts()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsFonts()
{
    if (ImGui::TreeNode("Fonts"))
    {
```

