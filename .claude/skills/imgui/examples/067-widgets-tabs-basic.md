# Widgets/Tabs/Basic

- Marker: IMGUI_DEMO_MARKER("Widgets/Tabs/Basic")
- Source: .github/skills/imgui/demo.cpp:2744
- Summary: Demonstrates Basic behavior within Widgets / Tabs.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Tabs/Basic");
            ImGuiTabBarFlags tab_bar_flags = ImGuiTabBarFlags_None;
            if (ImGui::BeginTabBar("MyTabBar", tab_bar_flags))
            {
                if (ImGui::BeginTabItem("Avocado"))
                {
                    ImGui::Text("This is the Avocado tab!\nblah blah blah blah blah");
                    ImGui::EndTabItem();
                }
                if (ImGui::BeginTabItem("Broccoli"))
                {
                    ImGui::Text("This is the Broccoli tab!\nblah blah blah blah blah");
                    ImGui::EndTabItem();
                }
                if (ImGui::BeginTabItem("Cucumber"))
                {
                    ImGui::Text("This is the Cucumber tab!\nblah blah blah blah blah");
                    ImGui::EndTabItem();
                }
                ImGui::EndTabBar();
            }
            ImGui::Separator();
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Advanced & Close Button"))
        {
```

