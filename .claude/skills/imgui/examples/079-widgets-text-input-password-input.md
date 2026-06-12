# Widgets/Text Input/Password input

- Marker: IMGUI_DEMO_MARKER("Widgets/Text Input/Password input")
- Source: .github/skills/imgui/demo.cpp:3099
- Summary: Demonstrates Password input behavior within Widgets / Text Input.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Text Input/Password input");
            static char password[64] = "password123";
            ImGui::InputText("password", password, IM_COUNTOF(password), ImGuiInputTextFlags_Password);
            ImGui::SameLine(); HelpMarker("Display all characters as '*'.\nDisable clipboard cut and copy.\nDisable logging.\n");
            ImGui::InputTextWithHint("password (w/ hint)", "<password>", password, IM_COUNTOF(password), ImGuiInputTextFlags_Password);
            ImGui::InputText("password (clear)", password, IM_COUNTOF(password));
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Completion, History, Edit Callbacks"))
        {
```

