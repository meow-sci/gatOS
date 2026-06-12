# Widgets/Basic/DragInt, DragFloat

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/DragInt, DragFloat")
- Source: .github/skills/imgui/demo.cpp:221
- Summary: Demonstrates DragInt, DragFloat behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/DragInt, DragFloat");
            static int i1 = 50, i2 = 42, i3 = 128;
            ImGui::DragInt("drag int", &i1, 1);
            ImGui::SameLine(); HelpMarker(
                "Click and drag to edit value.\n"
                "Hold Shift/Alt for faster/slower edit.\n"
                "Double-Click or Ctrl+Click to input value.");
            ImGui::DragInt("drag int 0..100", &i2, 1, 0, 100, "%d%%", ImGuiSliderFlags_AlwaysClamp);
            ImGui::DragInt("drag int wrap 100..200", &i3, 1, 100, 200, "%d", ImGuiSliderFlags_WrapAround);

            static float f1 = 1.00f, f2 = 0.0067f;
            ImGui::DragFloat("drag float", &f1, 0.005f);
            ImGui::DragFloat("drag small float", &f2, 0.0001f, 0.0f, 0.0f, "%.06f ns");
            //ImGui::DragFloat("drag wrap -1..1", &f3, 0.005f, -1.0f, 1.0f, NULL, ImGuiSliderFlags_WrapAround);
        }

        ImGui::SeparatorText("Sliders");

        {
```

