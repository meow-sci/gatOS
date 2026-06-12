# Tools/Style Editor

- Marker: IMGUI_DEMO_MARKER("Tools/Style Editor")
- Source: .github/skills/imgui/demo.cpp:7834
- Summary: Demonstrates Style Editor behavior within Tools.

```cpp
    IMGUI_DEMO_MARKER("Tools/Style Editor");
    // You can pass in a reference ImGuiStyle structure to compare to, revert to and save to
    // (without a reference style pointer, we will use one compared locally as a reference)
    ImGuiStyle& style = GetStyle();
    static ImGuiStyle ref_saved_style;

    // Default to using internal storage as reference
    static bool init = true;
    if (init && ref == NULL)
        ref_saved_style = style;
    init = false;
    if (ref == NULL)
        ref = &ref_saved_style;

    // The logic behind dynamically changing 'max_border_size' is to not encourage people to increase border size too much: it'll likely reveal lots of subtle rendering artifacts and this isn't a priority right now.
    // Note that _MainScale is currently internal PLEASE DO NOT USE IN YOUR CODE.
    const float default_border_size = (float)(int)style._MainScale;
    const float max_border_size = IM_MAX(default_border_size, 2.0f);

    PushItemWidth(GetWindowWidth() * 0.50f);

    {
        // General
        SeparatorText("General");
        if ((GetIO().BackendFlags & ImGuiBackendFlags_RendererHasTextures) == 0)
        {
            BulletText("Warning: Font scaling will NOT be smooth, because\nImGuiBackendFlags_RendererHasTextures is not set!");
            BulletText("For instructions, see:");
            SameLine();
            TextLinkOpenURL("docs/BACKENDS.md", "https://github.com/ocornut/imgui/blob/master/docs/BACKENDS.md");
        }

        if (ShowStyleSelector("Colors##Selector"))
            ref_saved_style = style;
        ShowFontSelector("Fonts##Selector");
        if (DragFloat("FontSizeBase", &style.FontSizeBase, 0.20f, 5.0f, 100.0f, "%.0f"))
            style._NextFrameFontSizeBase = style.FontSizeBase; // FIXME: Temporary hack until we finish remaining work.
        SameLine(0.0f, 0.0f); Text(" (out %.2f)", GetFontSize());
        DragFloat("FontScaleMain", &style.FontScaleMain, 0.02f, 0.5f, 4.0f);
        BeginDisabled(GetIO().ConfigDpiScaleFonts);
        DragFloat("FontScaleDpi", &style.FontScaleDpi, 0.02f, 0.5f, 4.0f);
        SetItemTooltip("When io.ConfigDpiScaleFonts is set, this value is automatically overwritten.");
        EndDisabled();

        // Simplified Settings (expose floating-pointer border sizes as boolean representing 0.0f or 1.0f)
        if (SliderFloat("FrameRounding", &style.FrameRounding, 0.0f, 12.0f, "%.0f"))
            style.GrabRounding = style.FrameRounding; // Make GrabRounding always the same value as FrameRounding
        { bool border = (style.WindowBorderSize > 0.0f); if (Checkbox("WindowBorder", &border)) { style.WindowBorderSize = border ? default_border_size : 0.0f; } }
        SameLine();
        { bool border = (style.FrameBorderSize > 0.0f);  if (Checkbox("FrameBorder", &border)) { style.FrameBorderSize = border ? default_border_size : 0.0f; } }
        SameLine();
        { bool border = (style.PopupBorderSize > 0.0f);  if (Checkbox("PopupBorder", &border)) { style.PopupBorderSize = border ? default_border_size : 0.0f; } }
    }

    // Save/Revert button
    if (Button("Save Ref"))
        *ref = ref_saved_style = style;
    SameLine();
    if (Button("Revert Ref"))
        style = *ref;
    SameLine();
    HelpMarker(
        "Save/Revert in local non-persistent storage. Default Colors definition are not affected. "
        "Use \"Export\" below to save them somewhere.");

    SeparatorText("Details");
    if (BeginTabBar("##tabs", ImGuiTabBarFlags_None))
    {
        if (BeginTabItem("Sizes"))
        {
            SeparatorText("Main");
            SliderFloat2("WindowPadding", (float*)&style.WindowPadding, 0.0f, 20.0f, "%.0f");
            SliderFloat2("FramePadding", (float*)&style.FramePadding, 0.0f, 20.0f, "%.0f");
            SliderFloat2("ItemSpacing", (float*)&style.ItemSpacing, 0.0f, 20.0f, "%.0f");
            SliderFloat2("ItemInnerSpacing", (float*)&style.ItemInnerSpacing, 0.0f, 20.0f, "%.0f");
            SliderFloat2("TouchExtraPadding", (float*)&style.TouchExtraPadding, 0.0f, 10.0f, "%.0f");
            SliderFloat("IndentSpacing", &style.IndentSpacing, 0.0f, 30.0f, "%.0f");
            SliderFloat("GrabMinSize", &style.GrabMinSize, 1.0f, 20.0f, "%.0f");

            SeparatorText("Borders");
            SliderFloat("WindowBorderSize", &style.WindowBorderSize, 0.0f, max_border_size, "%.0f");
            SliderFloat("ChildBorderSize", &style.ChildBorderSize, 0.0f, max_border_size, "%.0f");
            SliderFloat("PopupBorderSize", &style.PopupBorderSize, 0.0f, max_border_size, "%.0f");
            SliderFloat("FrameBorderSize", &style.FrameBorderSize, 0.0f, max_border_size, "%.0f");

            SeparatorText("Rounding");
            SliderFloat("WindowRounding", &style.WindowRounding, 0.0f, 12.0f, "%.0f");
            SliderFloat("ChildRounding", &style.ChildRounding, 0.0f, 12.0f, "%.0f");
            SliderFloat("FrameRounding", &style.FrameRounding, 0.0f, 12.0f, "%.0f");
            SliderFloat("PopupRounding", &style.PopupRounding, 0.0f, 12.0f, "%.0f");
            SliderFloat("GrabRounding", &style.GrabRounding, 0.0f, 12.0f, "%.0f");

            SeparatorText("Scrollbar");
            SliderFloat("ScrollbarSize", &style.ScrollbarSize, 1.0f, 20.0f, "%.0f");
            SliderFloat("ScrollbarRounding", &style.ScrollbarRounding, 0.0f, 12.0f, "%.0f");
            SliderFloat("ScrollbarPadding", &style.ScrollbarPadding, 0.0f, 10.0f, "%.0f");

            SeparatorText("Tabs");
            SliderFloat("TabBorderSize", &style.TabBorderSize, 0.0f, max_border_size, "%.0f");
            SliderFloat("TabBarBorderSize", &style.TabBarBorderSize, 0.0f, max_border_size, "%.0f");
            SliderFloat("TabBarOverlineSize", &style.TabBarOverlineSize, 0.0f, IM_MAX(3.0f, max_border_size), "%.0f");
            SameLine(); HelpMarker("Overline is only drawn over the selected tab when ImGuiTabBarFlags_DrawSelectedOverline is set.");
            DragFloat("TabMinWidthBase", &style.TabMinWidthBase, 0.5f, 1.0f, 500.0f, "%.0f");
            DragFloat("TabMinWidthShrink", &style.TabMinWidthShrink, 0.5f, 1.0f, 500.0f, "%0.f");
            DragFloat("TabCloseButtonMinWidthSelected", &style.TabCloseButtonMinWidthSelected, 0.5f, -1.0f, 100.0f, (style.TabCloseButtonMinWidthSelected < 0.0f) ? "%.0f (Always)" : "%.0f");
            DragFloat("TabCloseButtonMinWidthUnselected", &style.TabCloseButtonMinWidthUnselected, 0.5f, -1.0f, 100.0f, (style.TabCloseButtonMinWidthUnselected < 0.0f) ? "%.0f (Always)" : "%.0f");
            SliderFloat("TabRounding", &style.TabRounding, 0.0f, 12.0f, "%.0f");

            SeparatorText("Tables");
            SliderFloat2("CellPadding", (float*)&style.CellPadding, 0.0f, 20.0f, "%.0f");
            SliderAngle("TableAngledHeadersAngle", &style.TableAngledHeadersAngle, -50.0f, +50.0f);
            SliderFloat2("TableAngledHeadersTextAlign", (float*)&style.TableAngledHeadersTextAlign, 0.0f, 1.0f, "%.2f");

            SeparatorText("Trees");
            bool combo_open = BeginCombo("TreeLinesFlags", GetTreeLinesFlagsName(style.TreeLinesFlags));
            SameLine();
            HelpMarker("[Experimental] Tree lines may not work in all situations (e.g. using a clipper) and may incurs slight traversal overhead.\n\nImGuiTreeNodeFlags_DrawLinesFull is faster than ImGuiTreeNodeFlags_DrawLinesToNode.");
            if (combo_open)
            {
                const ImGuiTreeNodeFlags options[] = { ImGuiTreeNodeFlags_DrawLinesNone, ImGuiTreeNodeFlags_DrawLinesFull, ImGuiTreeNodeFlags_DrawLinesToNodes };
                for (ImGuiTreeNodeFlags option : options)
                    if (Selectable(GetTreeLinesFlagsName(option), style.TreeLinesFlags == option))
                        style.TreeLinesFlags = option;
                EndCombo();
            }
            SliderFloat("TreeLinesSize", &style.TreeLinesSize, 0.0f, max_border_size, "%.0f");
            SliderFloat("TreeLinesRounding", &style.TreeLinesRounding, 0.0f, 12.0f, "%.0f");

            SeparatorText("Windows");
            SliderFloat2("WindowTitleAlign", (float*)&style.WindowTitleAlign, 0.0f, 1.0f, "%.2f");
            SliderFloat("WindowBorderHoverPadding", &style.WindowBorderHoverPadding, 1.0f, 20.0f, "%.0f");
            int window_menu_button_position = style.WindowMenuButtonPosition + 1;
            if (Combo("WindowMenuButtonPosition", (int*)&window_menu_button_position, "None\0Left\0Right\0"))
                style.WindowMenuButtonPosition = (ImGuiDir)(window_menu_button_position - 1);

            SeparatorText("Widgets");
            SliderFloat("ColorMarkerSize", &style.ColorMarkerSize, 0.0f, 8.0f, "%.0f");
            Combo("ColorButtonPosition", (int*)&style.ColorButtonPosition, "Left\0Right\0");
            SliderFloat2("ButtonTextAlign", (float*)&style.ButtonTextAlign, 0.0f, 1.0f, "%.2f");
            SameLine(); HelpMarker("Alignment applies when a button is larger than its text content.");
            SliderFloat2("SelectableTextAlign", (float*)&style.SelectableTextAlign, 0.0f, 1.0f, "%.2f");
            SameLine(); HelpMarker("Alignment applies when a selectable is larger than its text content.");
            SliderFloat("SeparatorSize", &style.SeparatorSize, 0.0f, 10.0f, "%.0f");
            SliderFloat("SeparatorTextBorderSize", &style.SeparatorTextBorderSize, 0.0f, 10.0f, "%.0f");
            SliderFloat2("SeparatorTextAlign", (float*)&style.SeparatorTextAlign, 0.0f, 1.0f, "%.2f");
            SliderFloat2("SeparatorTextPadding", (float*)&style.SeparatorTextPadding, 0.0f, 40.0f, "%.0f");
            SliderFloat("LogSliderDeadzone", &style.LogSliderDeadzone, 0.0f, 12.0f, "%.0f");
            SliderFloat("ImageRounding", &style.ImageRounding, 0.0f, 12.0f, "%.0f");
            SliderFloat("ImageBorderSize", &style.ImageBorderSize, 0.0f, max_border_size, "%.0f");

            SeparatorText("Docking");
            //SetCursorPosX(GetCursorPosX() + CalcItemWidth() - GetFrameHeight());
            Checkbox("DockingNodeHasCloseButton", &style.DockingNodeHasCloseButton);
            SliderFloat("DockingSeparatorSize", &style.DockingSeparatorSize, 0.0f, 12.0f, "%.0f");

            SeparatorText("Tooltips");
            for (int n = 0; n < 2; n++)
                if (TreeNodeEx(n == 0 ? "HoverFlagsForTooltipMouse" : "HoverFlagsForTooltipNav"))
                {
                    ImGuiHoveredFlags* p = (n == 0) ? &style.HoverFlagsForTooltipMouse : &style.HoverFlagsForTooltipNav;
                    CheckboxFlags("ImGuiHoveredFlags_DelayNone", p, ImGuiHoveredFlags_DelayNone);
                    CheckboxFlags("ImGuiHoveredFlags_DelayShort", p, ImGuiHoveredFlags_DelayShort);
                    CheckboxFlags("ImGuiHoveredFlags_DelayNormal", p, ImGuiHoveredFlags_DelayNormal);
                    CheckboxFlags("ImGuiHoveredFlags_Stationary", p, ImGuiHoveredFlags_Stationary);
                    CheckboxFlags("ImGuiHoveredFlags_NoSharedDelay", p, ImGuiHoveredFlags_NoSharedDelay);
                    TreePop();
                }

            SeparatorText("Misc");
            SliderFloat2("DisplayWindowPadding", (float*)&style.DisplayWindowPadding, 0.0f, 30.0f, "%.0f"); SameLine(); HelpMarker("Apply to regular windows: amount which we enforce to keep visible when moving near edges of your screen.");
            SliderFloat2("DisplaySafeAreaPadding", (float*)&style.DisplaySafeAreaPadding, 0.0f, 30.0f, "%.0f"); SameLine(); HelpMarker("Apply to every windows, menus, popups, tooltips: amount where we avoid displaying contents. Adjust if you cannot see the edges of your screen (e.g. on a TV where scaling has not been configured).");

            EndTabItem();
        }

        if (BeginTabItem("Colors"))
        {
            static int output_dest = 0;
            static bool output_only_modified = true;
            if (Button("Export"))
            {
                if (output_dest == 0)
                    LogToClipboard();
                else
                    LogToTTY();
                LogText("ImVec4* colors = GetStyle().Colors;" IM_NEWLINE);
                for (int i = 0; i < ImGuiCol_COUNT; i++)
                {
                    const ImVec4& col = style.Colors[i];
                    const char* name = GetStyleColorName(i);
                    if (!output_only_modified || memcmp(&col, &ref->Colors[i], sizeof(ImVec4)) != 0)
                        LogText("colors[ImGuiCol_%s]%*s= ImVec4(%.2ff, %.2ff, %.2ff, %.2ff);" IM_NEWLINE,
                            name, 23 - (int)strlen(name), "", col.x, col.y, col.z, col.w);
                }
                LogFinish();
            }
            SameLine(); SetNextItemWidth(GetFontSize() * 10); Combo("##output_type", &output_dest, "To Clipboard\0To TTY\0");
            SameLine(); Checkbox("Only Modified Colors", &output_only_modified);

            static ImGuiTextFilter filter;
            filter.Draw("Filter colors", GetFontSize() * 16);

            static ImGuiColorEditFlags alpha_flags = 0;
            if (RadioButton("Opaque", alpha_flags == ImGuiColorEditFlags_AlphaOpaque))       { alpha_flags = ImGuiColorEditFlags_AlphaOpaque; } SameLine();
            if (RadioButton("Alpha",  alpha_flags == ImGuiColorEditFlags_None))              { alpha_flags = ImGuiColorEditFlags_None; } SameLine();
            if (RadioButton("Both",   alpha_flags == ImGuiColorEditFlags_AlphaPreviewHalf))  { alpha_flags = ImGuiColorEditFlags_AlphaPreviewHalf; } SameLine();
            HelpMarker(
                "In the color list:\n"
                "Left-click on color square to open color picker,\n"
                "Right-click to open edit options menu.");

            SetNextWindowSizeConstraints(ImVec2(0.0f, GetTextLineHeightWithSpacing() * 10), ImVec2(FLT_MAX, FLT_MAX));
            BeginChild("##colors", ImVec2(0, 0), ImGuiChildFlags_Borders | ImGuiChildFlags_NavFlattened, ImGuiWindowFlags_AlwaysVerticalScrollbar | ImGuiWindowFlags_AlwaysHorizontalScrollbar);
            PushItemWidth(GetFontSize() * -12);
            for (int i = 0; i < ImGuiCol_COUNT; i++)
            {
                const char* name = GetStyleColorName(i);
                if (!filter.PassFilter(name))
                    continue;
                PushID(i);
#ifndef IMGUI_DISABLE_DEBUG_TOOLS
                if (Button("?"))
                    DebugFlashStyleColor((ImGuiCol)i);
                SetItemTooltip("Flash given color to identify places where it is used.");
                SameLine();
#endif
                ColorEdit4("##color", (float*)&style.Colors[i], ImGuiColorEditFlags_AlphaBar | alpha_flags);
                if (memcmp(&style.Colors[i], &ref->Colors[i], sizeof(ImVec4)) != 0)
                {
                    // Tips: in a real user application, you may want to merge and use an icon font into the main font,
                    // so instead of "Save"/"Revert" you'd use icons!
                    // Read the FAQ and docs/FONTS.md about using icon fonts. It's really easy and super convenient!
                    SameLine(0.0f, style.ItemInnerSpacing.x); if (Button("Save")) { ref->Colors[i] = style.Colors[i]; }
                    SameLine(0.0f, style.ItemInnerSpacing.x); if (Button("Revert")) { style.Colors[i] = ref->Colors[i]; }
                }
                SameLine(0.0f, style.ItemInnerSpacing.x);
                TextUnformatted(name);
                PopID();
            }
            PopItemWidth();
            EndChild();

            EndTabItem();
        }

        if (BeginTabItem("Fonts"))
        {
            ImGuiIO& io = GetIO();
            ImFontAtlas* atlas = io.Fonts;
            ShowFontAtlas(atlas);

            // Post-baking font scaling. Note that this is NOT the nice way of scaling fonts, read below.
            // (we enforce hard clamping manually as by default DragFloat/SliderFloat allows Ctrl+Click text to get out of bounds).
            /*
            SeparatorText("Legacy Scaling");
            const float MIN_SCALE = 0.3f;
            const float MAX_SCALE = 2.0f;
            HelpMarker(
                "Those are old settings provided for convenience.\n"
                "However, the _correct_ way of scaling your UI is currently to reload your font at the designed size, "
                "rebuild the font atlas, and call style.ScaleAllSizes() on a reference ImGuiStyle structure.\n"
                "Using those settings here will give you poor quality results.");
            PushItemWidth(GetFontSize() * 8);
            DragFloat("global scale", &io.FontGlobalScale, 0.005f, MIN_SCALE, MAX_SCALE, "%.2f", ImGuiSliderFlags_AlwaysClamp); // Scale everything
            //static float window_scale = 1.0f;
            //if (DragFloat("window scale", &window_scale, 0.005f, MIN_SCALE, MAX_SCALE, "%.2f", ImGuiSliderFlags_AlwaysClamp)) // Scale only this window
            //    SetWindowFontScale(window_scale);
            PopItemWidth();
            */

            EndTabItem();
        }

        if (BeginTabItem("Rendering"))
        {
            Checkbox("Anti-aliased lines", &style.AntiAliasedLines);
            SameLine();
            HelpMarker("When disabling anti-aliasing lines, you'll probably want to disable borders in your style as well.");

            Checkbox("Anti-aliased lines use texture", &style.AntiAliasedLinesUseTex);
            SameLine();
            HelpMarker("Faster lines using texture data. Require backend to render with bilinear filtering (not point/nearest filtering).");

            Checkbox("Anti-aliased fill", &style.AntiAliasedFill);
            PushItemWidth(GetFontSize() * 8);
            DragFloat("Curve Tessellation Tolerance", &style.CurveTessellationTol, 0.02f, 0.10f, 10.0f, "%.2f");
            if (style.CurveTessellationTol < 0.10f) style.CurveTessellationTol = 0.10f;

            // When editing the "Circle Segment Max Error" value, draw a preview of its effect on auto-tessellated circles.
            DragFloat("Circle Tessellation Max Error", &style.CircleTessellationMaxError , 0.005f, 0.10f, 5.0f, "%.2f", ImGuiSliderFlags_AlwaysClamp);
            const bool show_samples = IsItemActive();
            if (show_samples)
                SetNextWindowPos(GetCursorScreenPos());
            if (show_samples && BeginTooltip())
            {
                TextUnformatted("(R = radius, N = approx number of segments)");
                Spacing();
                ImDrawList* draw_list = GetWindowDrawList();
                const float min_widget_width = CalcTextSize("R: MMM\nN: MMM").x;
                for (int n = 0; n < 8; n++)
                {
                    const float RAD_MIN = 5.0f;
                    const float RAD_MAX = 70.0f;
                    const float rad = RAD_MIN + (RAD_MAX - RAD_MIN) * (float)n / (8.0f - 1.0f);

                    BeginGroup();

                    // N is not always exact here due to how PathArcTo() function work internally
                    Text("R: %.f\nN: %d", rad, draw_list->_CalcCircleAutoSegmentCount(rad));

                    const float canvas_width = IM_MAX(min_widget_width, rad * 2.0f);
                    const float offset_x     = floorf(canvas_width * 0.5f);
                    const float offset_y     = floorf(RAD_MAX);

                    const ImVec2 p1 = GetCursorScreenPos();
                    draw_list->AddCircle(ImVec2(p1.x + offset_x, p1.y + offset_y), rad, GetColorU32(ImGuiCol_Text));
                    Dummy(ImVec2(canvas_width, RAD_MAX * 2));

                    /*
                    const ImVec2 p2 = GetCursorScreenPos();
                    draw_list->AddCircleFilled(ImVec2(p2.x + offset_x, p2.y + offset_y), rad, GetColorU32(ImGuiCol_Text));
                    Dummy(ImVec2(canvas_width, RAD_MAX * 2));
                    */

                    EndGroup();
                    SameLine();
                }
                EndTooltip();
            }
            SameLine();
            HelpMarker("When drawing circle primitives with \"num_segments == 0\" tessellation will be calculated automatically.");

            DragFloat("Global Alpha", &style.Alpha, 0.005f, 0.20f, 1.0f, "%.2f"); // Not exposing zero here so user doesn't "lose" the UI (zero alpha clips all widgets). But application code could have a toggle to switch between zero and non-zero.
            DragFloat("Disabled Alpha", &style.DisabledAlpha, 0.005f, 0.0f, 1.0f, "%.2f"); SameLine(); HelpMarker("Additional alpha multiplier for disabled items (multiply over current value of Alpha).");
            PopItemWidth();

            EndTabItem();
        }

        EndTabBar();
    }
    PopItemWidth();
}

//-----------------------------------------------------------------------------
// [SECTION] User Guide / ShowUserGuide()
//-----------------------------------------------------------------------------

// We omit the ImGui:: prefix in this function, as we don't expect user to be copy and pasting this code.
void ImGui::ShowUserGuide()
{
    ImGuiIO& io = GetIO();
    BulletText("Double-click on title bar to collapse window.");
    BulletText(
        "Click and drag on lower corner or border to resize window.\n"
        "(double-click to auto fit window to its contents)");
    BulletText("Ctrl+Click on a slider or drag box to input value as text.");
    BulletText("Tab/Shift+Tab to cycle through keyboard editable fields.");
    BulletText("Ctrl+Tab/Ctrl+Shift+Tab to focus windows.");
    if (io.FontAllowUserScaling)
        BulletText("Ctrl+Mouse Wheel to zoom window contents.");
    BulletText("While inputting text:\n");
    Indent();
    BulletText("Ctrl+Left/Right to word jump.");
    BulletText("Ctrl+A or double-click to select all.");
    BulletText("Ctrl+X/C/V to use clipboard cut/copy/paste.");
    BulletText("Ctrl+Z to undo, Ctrl+Y/Ctrl+Shift+Z to redo.");
    BulletText("Escape to revert.");
    Unindent();
    BulletText("With Keyboard controls enabled:");
    Indent();
    BulletText("Arrow keys or Home/End/PageUp/PageDown to navigate.");
    BulletText("Space to activate a widget.");
    BulletText("Return to input text into a widget.");
    BulletText("Escape to deactivate a widget, close popup,\nexit a child window or the menu layer, clear focus.");
    BulletText("Alt to jump to the menu layer of a window.");
    BulletText("Menu or Shift+F10 to open a context menu.");
    Unindent();
    BulletText("With Gamepad controls enabled:");
    Indent();
    BulletText("D-Pad: Navigate / Tweak / Resize (in Windowing mode).");
    BulletText("%s Face button: Activate / Open / Toggle. Hold: activate with text input.", io.ConfigNavSwapGamepadButtons ? "East" : "South");
    BulletText("%s Face button: Cancel / Close / Exit.", io.ConfigNavSwapGamepadButtons ? "South" : "East");
    BulletText("West Face button: Toggle Menu. Hold for Windowing mode (Focus/Move/Resize windows).");
    BulletText("North Face button: Open Context Menu.");
    BulletText("L1/R1: Tweak Slower/Faster, Focus Previous/Next (in Windowing Mode).");
    Unindent();
}

//-----------------------------------------------------------------------------
// [SECTION] Example App: Main Menu Bar / ShowExampleAppMainMenuBar()
//-----------------------------------------------------------------------------
// - ShowExampleAppMainMenuBar()
// - ShowExampleMenuFile()
//-----------------------------------------------------------------------------

// Demonstrate creating a "main" fullscreen menu bar and populating it.
// Note the difference between BeginMainMenuBar() and BeginMenuBar():
// - BeginMenuBar() = menu-bar inside current window (which needs the ImGuiWindowFlags_MenuBar flag!)
// - BeginMainMenuBar() = helper to create menu-bar-sized window at the top of the main viewport + call BeginMenuBar() into it.
static void ShowExampleAppMainMenuBar()
{
    if (ImGui::BeginMainMenuBar())
    {
        if (ImGui::BeginMenu("File"))
        {
```

