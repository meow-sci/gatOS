# Widgets/Drag and drop/Standard widgets

- Marker: IMGUI_DEMO_MARKER("Widgets/Drag and drop/Standard widgets")
- Source: .github/skills/imgui/demo.cpp:827
- Summary: Demonstrates Standard widgets behavior within Widgets / Drag and drop.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Drag and drop/Standard widgets");
            // ColorEdit widgets automatically act as drag source and drag target.
            // They are using standardized payload strings IMGUI_PAYLOAD_TYPE_COLOR_3F and IMGUI_PAYLOAD_TYPE_COLOR_4F
            // to allow your own widgets to use colors in their drag and drop interaction.
            // Also see 'Demo->Widgets->Color/Picker Widgets->Palette' demo.
            HelpMarker("You can drag from the color squares.");
            static float col1[3] = { 1.0f, 0.0f, 0.2f };
            static float col2[4] = { 0.4f, 0.7f, 0.0f, 0.5f };
            ImGui::ColorEdit3("color 1", col1);
            ImGui::ColorEdit4("color 2", col2);
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Drag and drop to copy/swap items"))
        {
```

