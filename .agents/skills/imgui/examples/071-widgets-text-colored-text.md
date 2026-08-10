# Widgets/Text/Colored Text

- Marker: IMGUI_DEMO_MARKER("Widgets/Text/Colored Text")
- Source: .github/skills/imgui/demo.cpp:2888
- Summary: Demonstrates Colored Text behavior within Widgets / Text.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Text/Colored Text");
            // Using shortcut. You can use PushStyleColor()/PopStyleColor() for more flexibility.
            ImGui::TextColored(ImVec4(1.0f, 0.0f, 1.0f, 1.0f), "Pink");
            ImGui::TextColored(ImVec4(1.0f, 1.0f, 0.0f, 1.0f), "Yellow");
            ImGui::TextDisabled("Disabled");
            ImGui::SameLine(); HelpMarker("The TextDisabled color is stored in ImGuiStyle.");
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Font Size"))
        {
```

