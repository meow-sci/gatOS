# Widgets/Selectables/Multiple items on the same line

- Marker: IMGUI_DEMO_MARKER("Widgets/Selectables/Multiple items on the same line")
- Source: .github/skills/imgui/demo.cpp:1615
- Summary: Demonstrates Multiple items on the same line behavior within Widgets / Selectables.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Selectables/Multiple items on the same line");
            // - Using SetNextItemAllowOverlap()
            // - Using the Selectable() override that takes "bool* p_selected" parameter, the bool value is toggled automatically.
            {
                static bool selected[3] = {};
                ImGui::SetNextItemAllowOverlap(); ImGui::Selectable("main.c", &selected[0]); ImGui::SameLine(); ImGui::SmallButton("Link 1");
                ImGui::SetNextItemAllowOverlap(); ImGui::Selectable("hello.cpp", &selected[1]); ImGui::SameLine(); ImGui::SmallButton("Link 2");
                ImGui::SetNextItemAllowOverlap(); ImGui::Selectable("hello.h", &selected[2]); ImGui::SameLine(); ImGui::SmallButton("Link 3");
            }

            // (2)
            // - Using ImGuiSelectableFlags_AllowOverlap is a shortcut for calling SetNextItemAllowOverlap()
            // - No visible label, display contents inside the selectable bounds.
            // - We don't maintain actual selection in this example to keep things simple.
            ImGui::Spacing();
            {
                static bool checked[5] = {};
                static int selected_n = 0;
                const float color_marker_w = ImGui::CalcTextSize("x").x;
                for (int n = 0; n < 5; n++)
                {
                    ImGui::PushID(n);
                    ImGui::AlignTextToFramePadding();
                    if (ImGui::Selectable("##selectable", selected_n == n, ImGuiSelectableFlags_AllowOverlap))
                        selected_n = n;
                    ImGui::SameLine(0, 0);
                    ImGui::Checkbox("##check", &checked[n]);
                    ImGui::SameLine();
                    ImVec4 color((n & 1) ? 1.0f : 0.2f, (n & 2) ? 1.0f : 0.2f, 0.2f, 1.0f);
                    ImGui::ColorButton("##color", color, ImGuiColorEditFlags_NoTooltip, ImVec2(color_marker_w, 0));
                    ImGui::SameLine();
                    ImGui::Text("Some label");
                    ImGui::PopID();
                }
            }

            ImGui::TreePop();
        }

        if (ImGui::TreeNode("In Tables"))
        {
```

