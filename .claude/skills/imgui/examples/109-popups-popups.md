# Popups/Popups

- Marker: IMGUI_DEMO_MARKER("Popups/Popups")
- Source: .github/skills/imgui/demo.cpp:4709
- Summary: Overview examples for Popups.

```cpp
        IMGUI_DEMO_MARKER("Popups/Popups");
        ImGui::TextWrapped(
            "When a popup is active, it inhibits interacting with windows that are behind the popup. "
            "Clicking outside the popup closes it.");

        static int selected_fish = -1;
        const char* names[] = { "Bream", "Haddock", "Mackerel", "Pollock", "Tilefish" };
        static bool toggles[] = { true, false, false, false, false };

        // Simple selection popup (if you want to show the current selection inside the Button itself,
        // you may want to build a string using the "###" operator to preserve a constant ID with a variable label)
        if (ImGui::Button("Select.."))
            ImGui::OpenPopup("my_select_popup");
        ImGui::SameLine();
        ImGui::TextUnformatted(selected_fish == -1 ? "<None>" : names[selected_fish]);
        if (ImGui::BeginPopup("my_select_popup"))
        {
            ImGui::SeparatorText("Aquarium");
            for (int i = 0; i < IM_COUNTOF(names); i++)
                if (ImGui::Selectable(names[i]))
                    selected_fish = i;
            ImGui::EndPopup();
        }

        // Showing a menu with toggles
        if (ImGui::Button("Toggle.."))
            ImGui::OpenPopup("my_toggle_popup");
        if (ImGui::BeginPopup("my_toggle_popup"))
        {
            for (int i = 0; i < IM_COUNTOF(names); i++)
                ImGui::MenuItem(names[i], "", &toggles[i]);
            if (ImGui::BeginMenu("Sub-menu"))
            {
                ImGui::MenuItem("Click me");
                ImGui::EndMenu();
            }

            ImGui::Separator();
            ImGui::Text("Tooltip here");
            ImGui::SetItemTooltip("I am a tooltip over a popup");

            if (ImGui::Button("Stacked Popup"))
                ImGui::OpenPopup("another popup");
            if (ImGui::BeginPopup("another popup"))
            {
                for (int i = 0; i < IM_COUNTOF(names); i++)
                    ImGui::MenuItem(names[i], "", &toggles[i]);
                if (ImGui::BeginMenu("Sub-menu"))
                {
                    ImGui::MenuItem("Click me");
                    if (ImGui::Button("Stacked Popup"))
                        ImGui::OpenPopup("another popup");
                    if (ImGui::BeginPopup("another popup"))
                    {
                        ImGui::Text("I am the last one here.");
                        ImGui::EndPopup();
                    }
                    ImGui::EndMenu();
                }
                ImGui::EndPopup();
            }
            ImGui::EndPopup();
        }

        // Call the more complete ShowExampleMenuFile which we use in various places of this demo
        if (ImGui::Button("With a menu.."))
            ImGui::OpenPopup("my_file_popup");
        if (ImGui::BeginPopup("my_file_popup", ImGuiWindowFlags_MenuBar))
        {
            if (ImGui::BeginMenuBar())
            {
                if (ImGui::BeginMenu("File"))
                {
                    ShowExampleMenuFile();
                    ImGui::EndMenu();
                }
                if (ImGui::BeginMenu("Edit"))
                {
                    ImGui::MenuItem("Dummy");
                    ImGui::EndMenu();
                }
                ImGui::EndMenuBar();
            }
            ImGui::Text("Hello from popup!");
            ImGui::Button("This is a dummy button..");
            ImGui::EndPopup();
        }

        ImGui::TreePop();
    }

    if (ImGui::TreeNode("Context menus"))
    {
```

