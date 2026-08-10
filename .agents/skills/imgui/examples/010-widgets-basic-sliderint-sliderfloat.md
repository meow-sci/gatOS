# Widgets/Basic/SliderInt, SliderFloat

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/SliderInt, SliderFloat")
- Source: .github/skills/imgui/demo.cpp:240
- Summary: Demonstrates SliderInt, SliderFloat behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/SliderInt, SliderFloat");
            static int i1 = 0;
            ImGui::SliderInt("slider int", &i1, -1, 3);
            ImGui::SameLine(); HelpMarker("Ctrl+Click to input value.");

            static float f1 = 0.123f, f2 = 0.0f;
            ImGui::SliderFloat("slider float", &f1, 0.0f, 1.0f, "ratio = %.3f");
            ImGui::SliderFloat("slider float (log)", &f2, -10.0f, 10.0f, "%.4f", ImGuiSliderFlags_Logarithmic);
```

