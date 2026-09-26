# Widgets/Tooltips

- Marker: IMGUI_DEMO_MARKER("Widgets/Tooltips")
- Source: .github/skills/imgui/demo.cpp:3251
- Summary: Demonstrates Tooltips behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Tooltips");
        // Tooltips are windows following the mouse. They do not take focus away.
        ImGui::SeparatorText("General");

        // Typical use cases:
        // - Short-form (text only):      SetItemTooltip("Hello");
        // - Short-form (any contents):   if (BeginItemTooltip()) { Text("Hello"); EndTooltip(); }

        // - Full-form (text only):       if (IsItemHovered(...)) { SetTooltip("Hello"); }
        // - Full-form (any contents):    if (IsItemHovered(...) && BeginTooltip()) { Text("Hello"); EndTooltip(); }

        HelpMarker(
            "Tooltip are typically created by using a IsItemHovered() + SetTooltip() sequence.\n\n"
            "We provide a helper SetItemTooltip() function to perform the two with standards flags.");

        ImVec2 sz = ImVec2(-FLT_MIN, 0.0f);

        ImGui::Button("Basic", sz);
        ImGui::SetItemTooltip("I am a tooltip");

        ImGui::Button("Fancy", sz);
        if (ImGui::BeginItemTooltip())
        {
            ImGui::Text("I am a fancy tooltip");
            static float arr[] = { 0.6f, 0.1f, 1.0f, 0.5f, 0.92f, 0.1f, 0.2f };
            ImGui::PlotLines("Curve", arr, IM_COUNTOF(arr));
            ImGui::Text("Sin(time) = %f", sinf((float)ImGui::GetTime()));
            ImGui::EndTooltip();
        }

        ImGui::SeparatorText("Always On");

        // Showcase NOT relying on a IsItemHovered() to emit a tooltip.
        // Here the tooltip is always emitted when 'always_on == true'.
        static int always_on = 0;
        ImGui::RadioButton("Off", &always_on, 0);
        ImGui::SameLine();
        ImGui::RadioButton("Always On (Simple)", &always_on, 1);
        ImGui::SameLine();
        ImGui::RadioButton("Always On (Advanced)", &always_on, 2);
        if (always_on == 1)
            ImGui::SetTooltip("I am following you around.");
        else if (always_on == 2 && ImGui::BeginTooltip())
        {
            ImGui::ProgressBar(sinf((float)ImGui::GetTime()) * 0.5f + 0.5f, ImVec2(ImGui::GetFontSize() * 25, 0.0f));
            ImGui::EndTooltip();
        }

        ImGui::SeparatorText("Custom");

        HelpMarker(
            "Passing ImGuiHoveredFlags_ForTooltip to IsItemHovered() is the preferred way to standardize "
            "tooltip activation details across your application. You may however decide to use custom "
            "flags for a specific tooltip instance.");

        // The following examples are passed for documentation purpose but may not be useful to most users.
        // Passing ImGuiHoveredFlags_ForTooltip to IsItemHovered() will pull ImGuiHoveredFlags flags values from
        // 'style.HoverFlagsForTooltipMouse' or 'style.HoverFlagsForTooltipNav' depending on whether mouse or keyboard/gamepad is being used.
        // With default settings, ImGuiHoveredFlags_ForTooltip is equivalent to ImGuiHoveredFlags_DelayShort + ImGuiHoveredFlags_Stationary.
        ImGui::Button("Manual", sz);
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_ForTooltip))
            ImGui::SetTooltip("I am a manually emitted tooltip.");

        ImGui::Button("DelayNone", sz);
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_DelayNone))
            ImGui::SetTooltip("I am a tooltip with no delay.");

        ImGui::Button("DelayShort", sz);
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_DelayShort | ImGuiHoveredFlags_NoSharedDelay))
            ImGui::SetTooltip("I am a tooltip with a short delay (%0.2f sec).", ImGui::GetStyle().HoverDelayShort);

        ImGui::Button("DelayLong", sz);
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_DelayNormal | ImGuiHoveredFlags_NoSharedDelay))
            ImGui::SetTooltip("I am a tooltip with a long delay (%0.2f sec).", ImGui::GetStyle().HoverDelayNormal);

        ImGui::Button("Stationary", sz);
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_Stationary))
            ImGui::SetTooltip("I am a tooltip requiring mouse to be stationary before activating.");

        // Using ImGuiHoveredFlags_ForTooltip will pull flags from 'style.HoverFlagsForTooltipMouse' or 'style.HoverFlagsForTooltipNav',
        // which default value include the ImGuiHoveredFlags_AllowWhenDisabled flag.
        ImGui::BeginDisabled();
        ImGui::Button("Disabled item", sz);
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_ForTooltip))
            ImGui::SetTooltip("I am a a tooltip for a disabled item.");
        ImGui::EndDisabled();

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsTreeNodes()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsTreeNodes()
{
    if (ImGui::TreeNode("Tree Nodes"))
    {
```

