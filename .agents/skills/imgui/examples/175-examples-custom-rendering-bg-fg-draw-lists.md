# Examples/Custom rendering/BG & FG draw lists

- Marker: IMGUI_DEMO_MARKER("Examples/Custom rendering/BG & FG draw lists")
- Source: .github/skills/imgui/demo.cpp:9761
- Summary: Demonstrates BG & FG draw lists behavior within Examples / Custom rendering.

```cpp
            IMGUI_DEMO_MARKER("Examples/Custom rendering/BG & FG draw lists");
            static bool draw_bg = true;
            static bool draw_fg = true;
            ImGui::Checkbox("Draw in Background draw list", &draw_bg);
            ImGui::SameLine(); HelpMarker("The Background draw list will be rendered below every Dear ImGui windows.");
            ImGui::Checkbox("Draw in Foreground draw list", &draw_fg);
            ImGui::SameLine(); HelpMarker("The Foreground draw list will be rendered over every Dear ImGui windows.");
            ImVec2 window_pos = ImGui::GetWindowPos();
            ImVec2 window_size = ImGui::GetWindowSize();
            ImVec2 window_center = ImVec2(window_pos.x + window_size.x * 0.5f, window_pos.y + window_size.y * 0.5f);
            if (draw_bg)
                ImGui::GetBackgroundDrawList()->AddCircle(window_center, window_size.x * 0.6f, IM_COL32(255, 0, 0, 200), 0, 10 + 4);
            if (draw_fg)
                ImGui::GetForegroundDrawList()->AddCircle(window_center, window_size.y * 0.6f, IM_COL32(0, 255, 0, 200), 0, 10);
            ImGui::EndTabItem();
        }

        // Demonstrate out-of-order rendering via channels splitting
        // We use functions in ImDrawList as each draw list contains a convenience splitter,
        // but you can also instantiate your own ImDrawListSplitter if you need to nest them.
        if (ImGui::BeginTabItem("Draw Channels"))
        {
```

