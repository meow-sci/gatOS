# Widgets/Basic/SliderAngle

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/SliderAngle")
- Source: .github/skills/imgui/demo.cpp:249
- Summary: Demonstrates SliderAngle behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/SliderAngle");
            static float angle = 0.0f;
            ImGui::SliderAngle("slider angle", &angle);

            // Using the format string to display a name instead of an integer.
            // Here we completely omit '%d' from the format string, so it'll only display a name.
            // This technique can also be used with DragInt().
```

