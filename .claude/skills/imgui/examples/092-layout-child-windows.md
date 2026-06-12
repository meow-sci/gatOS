# Layout/Child windows

- Marker: IMGUI_DEMO_MARKER("Layout/Child windows")
- Source: .github/skills/imgui/demo.cpp:3656
- Summary: Demonstrates Child windows behavior within Layout.

```cpp
        IMGUI_DEMO_MARKER("Layout/Child windows");
        ImGui::SeparatorText("Child windows");

        HelpMarker("Use child windows to begin into a self-contained independent scrolling/clipping regions within a host window.");
        static bool disable_mouse_wheel = false;
        static bool disable_menu = false;
        ImGui::Checkbox("Disable Mouse Wheel", &disable_mouse_wheel);
        ImGui::Checkbox("Disable Menu", &disable_menu);

        // Child 1: no border, enable horizontal scrollbar
        {
            ImGuiWindowFlags window_flags = ImGuiWindowFlags_HorizontalScrollbar;
            if (disable_mouse_wheel)
                window_flags |= ImGuiWindowFlags_NoScrollWithMouse;
            ImGui::BeginChild("ChildL", ImVec2(ImGui::GetContentRegionAvail().x * 0.5f, 260), ImGuiChildFlags_None, window_flags);
            for (int i = 0; i < 100; i++)
                ImGui::Text("%04d: scrollable region", i);
            ImGui::EndChild();
        }

        ImGui::SameLine();

        // Child 2: rounded border
        {
            ImGuiWindowFlags window_flags = ImGuiWindowFlags_None;
            if (disable_mouse_wheel)
                window_flags |= ImGuiWindowFlags_NoScrollWithMouse;
            if (!disable_menu)
                window_flags |= ImGuiWindowFlags_MenuBar;
            ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 5.0f);
            ImGui::BeginChild("ChildR", ImVec2(0, 260), ImGuiChildFlags_Borders, window_flags);
            if (!disable_menu && ImGui::BeginMenuBar())
            {
                if (ImGui::BeginMenu("Menu"))
                {
                    ShowExampleMenuFile();
                    ImGui::EndMenu();
                }
                ImGui::EndMenuBar();
            }
            if (ImGui::BeginTable("split", 2, ImGuiTableFlags_Resizable | ImGuiTableFlags_NoSavedSettings))
            {
                for (int i = 0; i < 100; i++)
                {
                    char buf[32];
                    sprintf(buf, "%03d", i);
                    ImGui::TableNextColumn();
                    ImGui::Button(buf, ImVec2(-FLT_MIN, 0.0f));
                }
                ImGui::EndTable();
            }
            ImGui::EndChild();
            ImGui::PopStyleVar();
        }

        // Child 3: manual-resize
        ImGui::SeparatorText("Manual-resize");
        {
            HelpMarker("Drag bottom border to resize. Double-click bottom border to auto-fit to vertical contents.");
            //if (ImGui::Button("Set Height to 200"))
            //    ImGui::SetNextWindowSize(ImVec2(-FLT_MIN, 200.0f));

            ImGui::PushStyleColor(ImGuiCol_ChildBg, ImGui::GetStyleColorVec4(ImGuiCol_FrameBg));
            if (ImGui::BeginChild("ResizableChild", ImVec2(-FLT_MIN, ImGui::GetTextLineHeightWithSpacing() * 8), ImGuiChildFlags_Borders | ImGuiChildFlags_ResizeY))
                for (int n = 0; n < 10; n++)
                    ImGui::Text("Line %04d", n);
            ImGui::PopStyleColor();
            ImGui::EndChild();
        }

        // Child 4: auto-resizing height with a limit
        ImGui::SeparatorText("Auto-resize with constraints");
        {
            static int draw_lines = 3;
            static int max_height_in_lines = 10;
            ImGui::SetNextItemWidth(ImGui::GetFontSize() * 8);
            ImGui::DragInt("Lines Count", &draw_lines, 0.2f);
            ImGui::SetNextItemWidth(ImGui::GetFontSize() * 8);
            ImGui::DragInt("Max Height (in Lines)", &max_height_in_lines, 0.2f);

            ImGui::SetNextWindowSizeConstraints(ImVec2(0.0f, ImGui::GetTextLineHeightWithSpacing() * 1), ImVec2(FLT_MAX, ImGui::GetTextLineHeightWithSpacing() * max_height_in_lines));
            if (ImGui::BeginChild("ConstrainedChild", ImVec2(-FLT_MIN, 0.0f), ImGuiChildFlags_Borders | ImGuiChildFlags_AutoResizeY))
                for (int n = 0; n < draw_lines; n++)
                    ImGui::Text("Line %04d", n);
            ImGui::EndChild();
        }

        ImGui::SeparatorText("Misc/Advanced");

        // Demonstrate a few extra things
        // - Changing ImGuiCol_ChildBg (which is transparent black in default styles)
        // - Using SetCursorPos() to position child window (the child window is an item from the POV of parent window)
        //   You can also call SetNextWindowPos() to position the child window. The parent window will effectively
        //   layout from this position.
        // - Using ImGui::GetItemRectMin/Max() to query the "item" state (because the child window is an item from
        //   the POV of the parent window). See 'Demo->Querying Status (Edited/Active/Hovered etc.)' for details.
        {
            static int offset_x = 0;
            static bool override_bg_color = true;
            static ImGuiChildFlags child_flags = ImGuiChildFlags_Borders | ImGuiChildFlags_ResizeX | ImGuiChildFlags_ResizeY;
            ImGui::SetNextItemWidth(ImGui::GetFontSize() * 8);
            ImGui::DragInt("Offset X", &offset_x, 1.0f, -1000, 1000);
            ImGui::Checkbox("Override ChildBg color", &override_bg_color);
            ImGui::CheckboxFlags("ImGuiChildFlags_Borders", &child_flags, ImGuiChildFlags_Borders);
            ImGui::CheckboxFlags("ImGuiChildFlags_AlwaysUseWindowPadding", &child_flags, ImGuiChildFlags_AlwaysUseWindowPadding);
            ImGui::CheckboxFlags("ImGuiChildFlags_ResizeX", &child_flags, ImGuiChildFlags_ResizeX);
            ImGui::CheckboxFlags("ImGuiChildFlags_ResizeY", &child_flags, ImGuiChildFlags_ResizeY);
            ImGui::CheckboxFlags("ImGuiChildFlags_FrameStyle", &child_flags, ImGuiChildFlags_FrameStyle);
            ImGui::SameLine(); HelpMarker("Style the child window like a framed item: use FrameBg, FrameRounding, FrameBorderSize, FramePadding instead of ChildBg, ChildRounding, ChildBorderSize, WindowPadding.");
            if (child_flags & ImGuiChildFlags_FrameStyle)
                override_bg_color = false;

            ImGui::SetCursorPosX(ImGui::GetCursorPosX() + (float)offset_x);
            if (override_bg_color)
                ImGui::PushStyleColor(ImGuiCol_ChildBg, IM_COL32(255, 0, 0, 100));
            ImGui::BeginChild("Red", ImVec2(200, 100), child_flags, ImGuiWindowFlags_None);
            if (override_bg_color)
                ImGui::PopStyleColor();

            for (int n = 0; n < 50; n++)
                ImGui::Text("Some test %d", n);
            ImGui::EndChild();
            bool child_is_hovered = ImGui::IsItemHovered();
            ImVec2 child_rect_min = ImGui::GetItemRectMin();
            ImVec2 child_rect_max = ImGui::GetItemRectMax();
            ImGui::Text("Hovered: %d", child_is_hovered);
            ImGui::Text("Rect of child window is: (%.0f,%.0f) (%.0f,%.0f)", child_rect_min.x, child_rect_min.y, child_rect_max.x, child_rect_max.y);
        }

        ImGui::TreePop();
    }

    if (ImGui::TreeNode("Widgets Width"))
    {
```

