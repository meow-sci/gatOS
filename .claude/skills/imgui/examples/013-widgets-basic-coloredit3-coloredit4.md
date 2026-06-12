# Widgets/Basic/ColorEdit3, ColorEdit4

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/ColorEdit3, ColorEdit4")
- Source: .github/skills/imgui/demo.cpp:268
- Summary: Demonstrates ColorEdit3, ColorEdit4 behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/ColorEdit3, ColorEdit4");
            static float col1[3] = { 1.0f, 0.0f, 0.2f };
            static float col2[4] = { 0.4f, 0.7f, 0.0f, 0.5f };
            ImGui::ColorEdit3("color 1", col1);
            ImGui::SameLine(); HelpMarker(
                "Click on the color square to open a color picker.\n"
                "Click and hold to use drag and drop.\n"
                "Right-Click on the color square to show options.\n"
                "Ctrl+Click on individual component to input value.\n");

            ImGui::ColorEdit4("color 2", col2);
        }

        {
            // Using the _simplified_ one-liner Combo() api here
            // See "Combo" section for examples of how to use the more flexible BeginCombo()/EndCombo() api.
```

