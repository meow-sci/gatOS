# Examples/Custom rendering/Draw Channels

- Marker: IMGUI_DEMO_MARKER("Examples/Custom rendering/Draw Channels")
- Source: .github/skills/imgui/demo.cpp:9783
- Summary: Demonstrates Draw Channels behavior within Examples / Custom rendering.

```cpp
            IMGUI_DEMO_MARKER("Examples/Custom rendering/Draw Channels");
            ImDrawList* draw_list = ImGui::GetWindowDrawList();
            {
                ImGui::Text("Blue shape is drawn first: appears in back");
                ImGui::Text("Red shape is drawn after: appears in front");
                ImVec2 p0 = ImGui::GetCursorScreenPos();
                draw_list->AddRectFilled(ImVec2(p0.x, p0.y), ImVec2(p0.x + 50, p0.y + 50), IM_COL32(0, 0, 255, 255)); // Blue
                draw_list->AddRectFilled(ImVec2(p0.x + 25, p0.y + 25), ImVec2(p0.x + 75, p0.y + 75), IM_COL32(255, 0, 0, 255)); // Red
                ImGui::Dummy(ImVec2(75, 75));
            }
            ImGui::Separator();
            {
                ImGui::Text("Blue shape is drawn first, into channel 1: appears in front");
                ImGui::Text("Red shape is drawn after, into channel 0: appears in back");
                ImVec2 p1 = ImGui::GetCursorScreenPos();

                // Create 2 channels and draw a Blue shape THEN a Red shape.
                // You can create any number of channels. Tables API use 1 channel per column in order to better batch draw calls.
                draw_list->ChannelsSplit(2);
                draw_list->ChannelsSetCurrent(1);
                draw_list->AddRectFilled(ImVec2(p1.x, p1.y), ImVec2(p1.x + 50, p1.y + 50), IM_COL32(0, 0, 255, 255)); // Blue
                draw_list->ChannelsSetCurrent(0);
                draw_list->AddRectFilled(ImVec2(p1.x + 25, p1.y + 25), ImVec2(p1.x + 75, p1.y + 75), IM_COL32(255, 0, 0, 255)); // Red

                // Flatten/reorder channels. Red shape is in channel 0 and it appears below the Blue shape in channel 1.
                // This works by copying draw indices only (vertices are not copied).
                draw_list->ChannelsMerge();
                ImGui::Dummy(ImVec2(75, 75));
                ImGui::Text("After reordering, contents of channel 0 appears below channel 1.");
            }
            ImGui::EndTabItem();
        }

        ImGui::EndTabBar();
    }

    ImGui::End();
}

//-----------------------------------------------------------------------------
// [SECTION] Example App: Docking, DockSpace / ShowExampleAppDockSpace()
//-----------------------------------------------------------------------------

struct ImGuiDemoDockspaceArgs
{
    bool                IsFullscreen = true;
    bool                KeepWindowPadding = false; // Keep WindowPadding to help understand that DockSpace() is a widget inside the window.
    ImGuiDockNodeFlags  DockSpaceFlags  = ImGuiDockNodeFlags_None;
};


static void ShowExampleAppDockSpaceImguiExplorer(ImGuiDemoDockspaceArgs* args, bool* p_open)
{
    ImGuiDockNodeFlags dockspace_flags = args->DockSpaceFlags;

    // We are using the ImGuiWindowFlags_NoDocking flag to make the parent window not dockable into,
    // because it would be confusing to have two docking targets within each others.
    ImGuiWindowFlags window_flags = ImGuiWindowFlags_NoDocking;
    {
        // Floating dockspace
        dockspace_flags &= ~ImGuiDockNodeFlags_PassthruCentralNode;
    }

    // Important: note that we proceed even if Begin() returns false (aka window is collapsed).
    // This is because we want to keep our DockSpace() active. If a DockSpace() is inactive,
    // all active windows docked into it will lose their parent and become undocked.
    // We cannot preserve the docking relationship between an active window and an inactive docking, otherwise
    // any change of dockspace/settings would lead to windows being stuck in limbo and never being visible.
    ImGui::SetNextWindowSize(ImVec2(400, 600), ImGuiCond_Once);
    ImGui::Begin("Window with a DockSpace", p_open, window_flags);
    {
```

