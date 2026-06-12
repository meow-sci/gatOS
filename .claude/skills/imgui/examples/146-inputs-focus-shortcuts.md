# Inputs & Focus/Shortcuts

- Marker: IMGUI_DEMO_MARKER("Inputs & Focus/Shortcuts")
- Source: .github/skills/imgui/demo.cpp:7361
- Summary: Demonstrates Shortcuts behavior within Inputs & Focus.

```cpp
            IMGUI_DEMO_MARKER("Inputs & Focus/Shortcuts");
            static ImGuiInputFlags route_options = ImGuiInputFlags_Repeat;
            static ImGuiInputFlags route_type = ImGuiInputFlags_RouteFocused;
            ImGui::CheckboxFlags("ImGuiInputFlags_Repeat", &route_options, ImGuiInputFlags_Repeat);
            ImGui::RadioButton("ImGuiInputFlags_RouteActive", &route_type, ImGuiInputFlags_RouteActive);
            ImGui::RadioButton("ImGuiInputFlags_RouteFocused (default)", &route_type, ImGuiInputFlags_RouteFocused);
            ImGui::Indent();
            ImGui::BeginDisabled(route_type != ImGuiInputFlags_RouteFocused);
            ImGui::CheckboxFlags("ImGuiInputFlags_RouteOverActive##0", &route_options, ImGuiInputFlags_RouteOverActive);
            ImGui::EndDisabled();
            ImGui::Unindent();
            ImGui::RadioButton("ImGuiInputFlags_RouteGlobal", &route_type, ImGuiInputFlags_RouteGlobal);
            ImGui::Indent();
            ImGui::BeginDisabled(route_type != ImGuiInputFlags_RouteGlobal);
            ImGui::CheckboxFlags("ImGuiInputFlags_RouteOverFocused", &route_options, ImGuiInputFlags_RouteOverFocused);
            ImGui::CheckboxFlags("ImGuiInputFlags_RouteOverActive", &route_options, ImGuiInputFlags_RouteOverActive);
            ImGui::CheckboxFlags("ImGuiInputFlags_RouteUnlessBgFocused", &route_options, ImGuiInputFlags_RouteUnlessBgFocused);
            ImGui::EndDisabled();
            ImGui::Unindent();
            ImGui::RadioButton("ImGuiInputFlags_RouteAlways", &route_type, ImGuiInputFlags_RouteAlways);
            ImGuiInputFlags flags = route_type | route_options; // Merged flags
            if (route_type != ImGuiInputFlags_RouteGlobal)
                flags &= ~(ImGuiInputFlags_RouteOverFocused | ImGuiInputFlags_RouteOverActive | ImGuiInputFlags_RouteUnlessBgFocused);

            ImGui::SeparatorText("Using SetNextItemShortcut()");
            ImGui::Text("Ctrl+S");
            ImGui::SetNextItemShortcut(ImGuiMod_Ctrl | ImGuiKey_S, flags | ImGuiInputFlags_Tooltip);
            ImGui::Button("Save");
            ImGui::Text("Alt+F");
            ImGui::SetNextItemShortcut(ImGuiMod_Alt | ImGuiKey_F, flags | ImGuiInputFlags_Tooltip);
            static float f = 0.5f;
            ImGui::SliderFloat("Factor", &f, 0.0f, 1.0f);

            ImGui::SeparatorText("Using Shortcut()");
            const float line_height = ImGui::GetTextLineHeightWithSpacing();
            const ImGuiKeyChord key_chord = ImGuiMod_Ctrl | ImGuiKey_A;

            ImGui::Text("Ctrl+A");
            ImGui::Text("IsWindowFocused: %d, Shortcut: %s", ImGui::IsWindowFocused(), ImGui::Shortcut(key_chord, flags) ? "PRESSED" : "...");

            ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(1.0f, 0.0f, 1.0f, 0.1f));

            ImGui::BeginChild("WindowA", ImVec2(-FLT_MIN, line_height * 14), true);
            ImGui::Text("Press Ctrl+A and see who receives it!");
            ImGui::Separator();

            // 1: Window polling for Ctrl+A
            ImGui::Text("(in WindowA)");
            ImGui::Text("IsWindowFocused: %d, Shortcut: %s", ImGui::IsWindowFocused(), ImGui::Shortcut(key_chord, flags) ? "PRESSED" : "...");

            // 2: InputText also polling for Ctrl+A: it always uses _RouteFocused internally (gets priority when active)
            // (Commented because the owner-aware version of Shortcut() is still in imgui_internal.h)
            //char str[16] = "Press Ctrl+A";
            //ImGui::Spacing();
            //ImGui::InputText("InputTextB", str, IM_COUNTOF(str), ImGuiInputTextFlags_ReadOnly);
            //ImGuiID item_id = ImGui::GetItemID();
            //ImGui::SameLine(); HelpMarker("Internal widgets always use _RouteFocused");
            //ImGui::Text("IsWindowFocused: %d, Shortcut: %s", ImGui::IsWindowFocused(), ImGui::Shortcut(key_chord, flags, item_id) ? "PRESSED" : "...");

            // 3: Dummy child is not claiming the route: focusing them shouldn't steal route away from WindowA
            ImGui::BeginChild("ChildD", ImVec2(-FLT_MIN, line_height * 4), true);
            ImGui::Text("(in ChildD: not using same Shortcut)");
            ImGui::Text("IsWindowFocused: %d", ImGui::IsWindowFocused());
            ImGui::EndChild();

            // 4: Child window polling for Ctrl+A. It is deeper than WindowA and gets priority when focused.
            ImGui::BeginChild("ChildE", ImVec2(-FLT_MIN, line_height * 4), true);
            ImGui::Text("(in ChildE: using same Shortcut)");
            ImGui::Text("IsWindowFocused: %d, Shortcut: %s", ImGui::IsWindowFocused(), ImGui::Shortcut(key_chord, flags) ? "PRESSED" : "...");
            ImGui::EndChild();

            // 5: In a popup
            if (ImGui::Button("Open Popup"))
                ImGui::OpenPopup("PopupF");
            if (ImGui::BeginPopup("PopupF"))
            {
                ImGui::Text("(in PopupF)");
                ImGui::Text("IsWindowFocused: %d, Shortcut: %s", ImGui::IsWindowFocused(), ImGui::Shortcut(key_chord, flags) ? "PRESSED" : "...");
                // (Commented because the owner-aware version of Shortcut() is still in imgui_internal.h)
                //ImGui::InputText("InputTextG", str, IM_COUNTOF(str), ImGuiInputTextFlags_ReadOnly);
                //ImGui::Text("IsWindowFocused: %d, Shortcut: %s", ImGui::IsWindowFocused(), ImGui::Shortcut(key_chord, flags, ImGui::GetItemID()) ? "PRESSED" : "...");
                ImGui::EndPopup();
            }
            ImGui::EndChild();
            ImGui::PopStyleColor();

            ImGui::TreePop();
        }

        // Display mouse cursors
        if (ImGui::TreeNode("Mouse Cursors"))
        {
```

