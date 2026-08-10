# Widgets/Basic/Slider (enum)

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/Slider (enum)")
- Source: .github/skills/imgui/demo.cpp:256
- Summary: Demonstrates Slider (enum) behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/Slider (enum)");
            enum Element { Element_Fire, Element_Earth, Element_Air, Element_Water, Element_COUNT };
            static int elem = Element_Fire;
            const char* elems_names[Element_COUNT] = { "Fire", "Earth", "Air", "Water" };
            const char* elem_name = (elem >= 0 && elem < Element_COUNT) ? elems_names[elem] : "Unknown";
            ImGui::SliderInt("slider enum", &elem, 0, Element_COUNT - 1, elem_name); // Use ImGuiSliderFlags_NoInput flag to disable Ctrl+Click here.
            ImGui::SameLine(); HelpMarker("Using the format string parameter to display a name instead of the underlying integer.");
        }

        ImGui::SeparatorText("Selectors/Pickers");

        {
```

