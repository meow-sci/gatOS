# Widgets/Basic/RadioButton

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/RadioButton")
- Source: .github/skills/imgui/demo.cpp:125
- Summary: Demonstrates RadioButton behavior within Widgets / Basic.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Basic/RadioButton");
        static int e = 0;
        ImGui::RadioButton("radio a", &e, 0); ImGui::SameLine();
        ImGui::RadioButton("radio b", &e, 1); ImGui::SameLine();
        ImGui::RadioButton("radio c", &e, 2);

        ImGui::AlignTextToFramePadding();
        ImGui::TextLinkOpenURL("Hyperlink", "https://github.com/ocornut/imgui/wiki/Error-Handling");

        // Color buttons, demonstrate using PushID() to add unique identifier in the ID stack, and changing style.
```

