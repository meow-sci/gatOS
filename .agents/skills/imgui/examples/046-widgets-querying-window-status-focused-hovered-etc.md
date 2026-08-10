# Widgets/Querying Window Status (Focused,Hovered etc.)

- Marker: IMGUI_DEMO_MARKER("Widgets/Querying Window Status (Focused,Hovered etc.)")
- Source: .github/skills/imgui/demo.cpp:1487
- Summary: Demonstrates Querying Window Status (Focused,Hovered etc.) behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Querying Window Status (Focused,Hovered etc.)");
        static bool embed_all_inside_a_child_window = false;
        ImGui::Checkbox("Embed everything inside a child window for testing _RootWindow flag.", &embed_all_inside_a_child_window);
        if (embed_all_inside_a_child_window)
            ImGui::BeginChild("outer_child", ImVec2(0, ImGui::GetFontSize() * 20.0f), ImGuiChildFlags_Borders);

        // Testing IsWindowFocused() function with its various flags.
        ImGui::BulletText(
            "IsWindowFocused() = %d\n"
            "IsWindowFocused(_ChildWindows) = %d\n"
            "IsWindowFocused(_ChildWindows|_NoPopupHierarchy) = %d\n"
            "IsWindowFocused(_ChildWindows|_DockHierarchy) = %d\n"
            "IsWindowFocused(_ChildWindows|_RootWindow) = %d\n"
            "IsWindowFocused(_ChildWindows|_RootWindow|_NoPopupHierarchy) = %d\n"
            "IsWindowFocused(_ChildWindows|_RootWindow|_DockHierarchy) = %d\n"
            "IsWindowFocused(_RootWindow) = %d\n"
            "IsWindowFocused(_RootWindow|_NoPopupHierarchy) = %d\n"
            "IsWindowFocused(_RootWindow|_DockHierarchy) = %d\n"
            "IsWindowFocused(_AnyWindow) = %d\n",
            ImGui::IsWindowFocused(),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_ChildWindows),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_ChildWindows | ImGuiFocusedFlags_NoPopupHierarchy),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_ChildWindows | ImGuiFocusedFlags_DockHierarchy),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_ChildWindows | ImGuiFocusedFlags_RootWindow),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_ChildWindows | ImGuiFocusedFlags_RootWindow | ImGuiFocusedFlags_NoPopupHierarchy),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_ChildWindows | ImGuiFocusedFlags_RootWindow | ImGuiFocusedFlags_DockHierarchy),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_RootWindow),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_RootWindow | ImGuiFocusedFlags_NoPopupHierarchy),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_RootWindow | ImGuiFocusedFlags_DockHierarchy),
            ImGui::IsWindowFocused(ImGuiFocusedFlags_AnyWindow));

        // Testing IsWindowHovered() function with its various flags.
        ImGui::BulletText(
            "IsWindowHovered() = %d\n"
            "IsWindowHovered(_AllowWhenBlockedByPopup) = %d\n"
            "IsWindowHovered(_AllowWhenBlockedByActiveItem) = %d\n"
            "IsWindowHovered(_ChildWindows) = %d\n"
            "IsWindowHovered(_ChildWindows|_NoPopupHierarchy) = %d\n"
            "IsWindowHovered(_ChildWindows|_DockHierarchy) = %d\n"
            "IsWindowHovered(_ChildWindows|_RootWindow) = %d\n"
            "IsWindowHovered(_ChildWindows|_RootWindow|_NoPopupHierarchy) = %d\n"
            "IsWindowHovered(_ChildWindows|_RootWindow|_DockHierarchy) = %d\n"
            "IsWindowHovered(_RootWindow) = %d\n"
            "IsWindowHovered(_RootWindow|_NoPopupHierarchy) = %d\n"
            "IsWindowHovered(_RootWindow|_DockHierarchy) = %d\n"
            "IsWindowHovered(_ChildWindows|_AllowWhenBlockedByPopup) = %d\n"
            "IsWindowHovered(_AnyWindow) = %d\n"
            "IsWindowHovered(_Stationary) = %d\n",
            ImGui::IsWindowHovered(),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_AllowWhenBlockedByPopup),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_AllowWhenBlockedByActiveItem),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_NoPopupHierarchy),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_DockHierarchy),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_RootWindow),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_RootWindow | ImGuiHoveredFlags_NoPopupHierarchy),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_RootWindow | ImGuiHoveredFlags_DockHierarchy),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_RootWindow),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_RootWindow | ImGuiHoveredFlags_NoPopupHierarchy),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_RootWindow | ImGuiHoveredFlags_DockHierarchy),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_ChildWindows | ImGuiHoveredFlags_AllowWhenBlockedByPopup),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_AnyWindow),
            ImGui::IsWindowHovered(ImGuiHoveredFlags_Stationary));

        ImGui::BeginChild("child", ImVec2(0, 50), ImGuiChildFlags_Borders);
        ImGui::Text("This is another child window for testing the _ChildWindows flag.");
        ImGui::EndChild();
        if (embed_all_inside_a_child_window)
            ImGui::EndChild();

        // Calling IsItemHovered() after begin returns the hovered status of the title bar.
        // This is useful in particular if you want to create a context menu associated to the title bar of a window.
        // This will also work when docked into a Tab (the Tab replace the Title Bar and guarantee the same properties).
        static bool test_window = false;
        ImGui::Checkbox("Hovered/Active tests after Begin() for title bar testing", &test_window);
        if (test_window)
        {
            // FIXME-DOCK: This window cannot be docked within the ImGui Demo window, this will cause a feedback loop and get them stuck.
            // Could we fix this through an ImGuiWindowClass feature? Or an API call to tag our parent as "don't skip items"?
            ImGui::Begin("Title bar Hovered/Active tests", &test_window);
            if (ImGui::BeginPopupContextItem()) // <-- This is using IsItemHovered()
            {
                if (ImGui::MenuItem("Close")) { test_window = false; }
                ImGui::EndPopup();
            }
            ImGui::Text(
                "IsItemHovered() after begin = %d (== is title bar hovered)\n"
                "IsItemActive() after begin = %d (== is window being clicked/moved)\n",
                ImGui::IsItemHovered(), ImGui::IsItemActive());
            ImGui::End();
        }

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsSelectables()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsSelectables()
{
    //ImGui::SetNextItemOpen(true, ImGuiCond_Once);
    if (ImGui::TreeNode("Selectables"))
    {
```

