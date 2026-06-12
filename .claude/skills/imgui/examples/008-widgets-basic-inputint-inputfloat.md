# Widgets/Basic/InputInt, InputFloat

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/InputInt, InputFloat")
- Source: .github/skills/imgui/demo.cpp:198
- Summary: Demonstrates InputInt, InputFloat behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/InputInt, InputFloat");
            static int i0 = 123;
            ImGui::InputInt("input int", &i0);

            static float f0 = 0.001f;
            ImGui::InputFloat("input float", &f0, 0.01f, 1.0f, "%.3f");

            static double d0 = 999999.00000001;
            ImGui::InputDouble("input double", &d0, 0.01f, 1.0f, "%.8f");

            static float f1 = 1.e10f;
            ImGui::InputFloat("input scientific", &f1, 0.0f, 0.0f, "%e");
            ImGui::SameLine(); HelpMarker(
                "You can input value using the scientific notation,\n"
                "  e.g. \"1e+8\" becomes \"100000000\".");

            static float vec4a[4] = { 0.10f, 0.20f, 0.30f, 0.44f };
            ImGui::InputFloat3("input float3", vec4a);
        }

        ImGui::SeparatorText("Drags");

        {
```

