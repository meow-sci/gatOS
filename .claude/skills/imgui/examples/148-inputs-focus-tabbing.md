# Inputs & Focus/Tabbing

- Marker: IMGUI_DEMO_MARKER("Inputs & Focus/Tabbing")
- Source: .github/skills/imgui/demo.cpp:7482
- Summary: Demonstrates Tabbing behavior within Inputs & Focus.

```cpp
            IMGUI_DEMO_MARKER("Inputs & Focus/Tabbing");
            ImGui::Text("Use Tab/Shift+Tab to cycle through keyboard editable fields.");
            static char buf[32] = "hello";
            ImGui::InputText("1", buf, IM_COUNTOF(buf));
            ImGui::InputText("2", buf, IM_COUNTOF(buf));
            ImGui::InputText("3", buf, IM_COUNTOF(buf));
            ImGui::PushItemFlag(ImGuiItemFlags_NoTabStop, true);
            ImGui::InputText("4 (tab skip)", buf, IM_COUNTOF(buf));
            ImGui::SameLine(); HelpMarker("Item won't be cycled through when using TAB or Shift+Tab.");
            ImGui::PopItemFlag();
            ImGui::InputText("5", buf, IM_COUNTOF(buf));
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Focus from code"))
        {
```

