# Examples/Constrained Resizing window

- Marker: IMGUI_DEMO_MARKER("Examples/Constrained Resizing window")
- Source: .github/skills/imgui/demo.cpp:9339
- Summary: Demonstrates Constrained Resizing window behavior within Examples.

```cpp
    IMGUI_DEMO_MARKER("Examples/Constrained Resizing window");
    if (window_open)
    {
        if (ImGui::GetIO().KeyShift)
        {
            // Display a dummy viewport (in your real app you would likely use ImageButton() to display a texture)
            ImVec2 avail_size = ImGui::GetContentRegionAvail();
            ImVec2 pos = ImGui::GetCursorScreenPos();
            ImGui::ColorButton("viewport", ImVec4(0.5f, 0.2f, 0.5f, 1.0f), ImGuiColorEditFlags_NoTooltip | ImGuiColorEditFlags_NoDragDrop, avail_size);
            ImGui::SetCursorScreenPos(ImVec2(pos.x + 10, pos.y + 10));
            ImGui::Text("%.2f x %.2f", avail_size.x, avail_size.y);
        }
        else
        {
            ImGui::Text("(Hold Shift to display a dummy viewport)");
            if (ImGui::IsWindowDocked())
                ImGui::Text("Warning: Sizing Constraints won't work if the window is docked!");
            if (ImGui::Button("Set 200x200")) { ImGui::SetWindowSize(ImVec2(200, 200)); } ImGui::SameLine();
            if (ImGui::Button("Set 500x500")) { ImGui::SetWindowSize(ImVec2(500, 500)); } ImGui::SameLine();
            if (ImGui::Button("Set 800x200")) { ImGui::SetWindowSize(ImVec2(800, 200)); }
            ImGui::SetNextItemWidth(ImGui::GetFontSize() * 20);
            ImGui::Combo("Constraint", &type, test_desc, IM_COUNTOF(test_desc));
            ImGui::SetNextItemWidth(ImGui::GetFontSize() * 20);
            ImGui::DragInt("Lines", &display_lines, 0.2f, 1, 100);
            ImGui::Checkbox("Auto-resize", &auto_resize);
            ImGui::Checkbox("Window padding", &window_padding);
            for (int i = 0; i < display_lines; i++)
                ImGui::Text("%*sHello, sailor! Making this line long enough for the example.", i * 4, "");
        }
    }
    ImGui::End();
}

//-----------------------------------------------------------------------------
// [SECTION] Example App: Simple overlay / ShowExampleAppSimpleOverlay()
//-----------------------------------------------------------------------------

// Demonstrate creating a simple static window with no decoration
// + a context-menu to choose which corner of the screen to use.
static void ShowExampleAppSimpleOverlay(bool* p_open)
{
    static int location = 0;
    ImGuiIO& io = ImGui::GetIO();
    ImGuiWindowFlags window_flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoDocking | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoNav;
    if (location >= 0)
    {
        const float PAD = 10.0f;
        const ImGuiViewport* viewport = ImGui::GetMainViewport();
        ImVec2 work_pos = viewport->WorkPos; // Use work area to avoid menu-bar/task-bar, if any!
        ImVec2 work_size = viewport->WorkSize;
        ImVec2 window_pos, window_pos_pivot;
        window_pos.x = (location & 1) ? (work_pos.x + work_size.x - PAD) : (work_pos.x + PAD);
        window_pos.y = (location & 2) ? (work_pos.y + work_size.y - PAD) : (work_pos.y + PAD);
        window_pos_pivot.x = (location & 1) ? 1.0f : 0.0f;
        window_pos_pivot.y = (location & 2) ? 1.0f : 0.0f;
        ImGui::SetNextWindowPos(window_pos, ImGuiCond_Always, window_pos_pivot);
        ImGui::SetNextWindowViewport(viewport->ID);
        window_flags |= ImGuiWindowFlags_NoMove;
    }
    else if (location == -2)
    {
        // Center window
        ImGui::SetNextWindowPos(ImGui::GetMainViewport()->GetCenter(), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
        window_flags |= ImGuiWindowFlags_NoMove;
    }
    ImGui::SetNextWindowBgAlpha(0.35f); // Transparent background
    if (ImGui::Begin("Example: Simple overlay", p_open, window_flags))
    {
```

