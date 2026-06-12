# Widgets/Multi-component Widgets

- Marker: IMGUI_DEMO_MARKER("Widgets/Multi-component Widgets")
- Source: .github/skills/imgui/demo.cpp:1212
- Summary: Demonstrates Multi-component Widgets behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Multi-component Widgets");
        static float vec4f[4] = { 0.10f, 0.20f, 0.30f, 0.44f };
        static int vec4i[4] = { 1, 5, 100, 255 };

        static ImGuiSliderFlags flags = 0;
        ImGui::CheckboxFlags("ImGuiSliderFlags_ColorMarkers", &flags, ImGuiSliderFlags_ColorMarkers); // Only passing this to Drag/Sliders

        ImGui::SeparatorText("2-wide");
        ImGui::InputFloat2("input float2", vec4f);
        ImGui::InputInt2("input int2", vec4i);
        ImGui::DragFloat2("drag float2", vec4f, 0.01f, 0.0f, 1.0f, NULL, flags);
        ImGui::DragInt2("drag int2", vec4i, 1, 0, 255, NULL, flags);
        ImGui::SliderFloat2("slider float2", vec4f, 0.0f, 1.0f, NULL, flags);
        ImGui::SliderInt2("slider int2", vec4i, 0, 255, NULL, flags);

        ImGui::SeparatorText("3-wide");
        ImGui::InputFloat3("input float3", vec4f);
        ImGui::InputInt3("input int3", vec4i);
        ImGui::DragFloat3("drag float3", vec4f, 0.01f, 0.0f, 1.0f, NULL, flags);
        ImGui::DragInt3("drag int3", vec4i, 1, 0, 255, NULL, flags);
        ImGui::SliderFloat3("slider float3", vec4f, 0.0f, 1.0f, NULL, flags);
        ImGui::SliderInt3("slider int3", vec4i, 0, 255, NULL, flags);

        ImGui::SeparatorText("4-wide");
        ImGui::InputFloat4("input float4", vec4f);
        ImGui::InputInt4("input int4", vec4i);
        ImGui::DragFloat4("drag float4", vec4f, 0.01f, 0.0f, 1.0f, NULL, flags);
        ImGui::DragInt4("drag int4", vec4i, 1, 0, 255, NULL, flags);
        ImGui::SliderFloat4("slider float4", vec4f, 0.0f, 1.0f, NULL, flags);
        ImGui::SliderInt4("slider int4", vec4i, 0, 255, NULL, flags);

        ImGui::SeparatorText("Ranges");
        static float begin = 10, end = 90;
        static int begin_i = 100, end_i = 1000;
        ImGui::DragFloatRange2("range float", &begin, &end, 0.25f, 0.0f, 100.0f, "Min: %.1f %%", "Max: %.1f %%", ImGuiSliderFlags_AlwaysClamp);
        ImGui::DragIntRange2("range int", &begin_i, &end_i, 5, 0, 1000, "Min: %d units", "Max: %d units");
        ImGui::DragIntRange2("range int (no bounds)", &begin_i, &end_i, 5, 0, 0, "Min: %d units", "Max: %d units");

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsPlotting()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsPlotting()
{
    // Plot/Graph widgets are not very good.
// Consider using a third-party library such as ImPlot: https://github.com/epezent/implot
// (see others https://github.com/ocornut/imgui/wiki/Useful-Extensions)
    if (ImGui::TreeNode("Plotting"))
    {
```

