# Widgets/Text Input/Eliding, Alignment

- Marker: IMGUI_DEMO_MARKER("Widgets/Text Input/Eliding, Alignment")
- Source: .github/skills/imgui/demo.cpp:3218
- Summary: Demonstrates Eliding, Alignment behavior within Widgets / Text Input.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Text Input/Eliding, Alignment");
            static char buf1[128] = "/path/to/some/folder/with/long/filename.cpp";
            static ImGuiInputTextFlags flags = ImGuiInputTextFlags_ElideLeft;
            ImGui::CheckboxFlags("ImGuiInputTextFlags_ElideLeft", &flags, ImGuiInputTextFlags_ElideLeft);
            ImGui::InputText("Path", buf1, IM_COUNTOF(buf1), flags);
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Miscellaneous"))
        {
```

