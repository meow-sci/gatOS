# Widgets/Progress Bars

- Marker: IMGUI_DEMO_MARKER("Widgets/Progress Bars")
- Source: .github/skills/imgui/demo.cpp:1338
- Summary: Demonstrates Progress Bars behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Progress Bars");
        // Animate a simple progress bar
        static float progress_accum = 0.0f, progress_dir = 1.0f;
        progress_accum += progress_dir * 0.4f * ImGui::GetIO().DeltaTime;
        if (progress_accum >= +1.1f) { progress_accum = +1.1f; progress_dir *= -1.0f; }
        if (progress_accum <= -0.1f) { progress_accum = -0.1f; progress_dir *= -1.0f; }

        const float progress = IM_CLAMP(progress_accum, 0.0f, 1.0f);

        // Typically we would use ImVec2(-1.0f,0.0f) or ImVec2(-FLT_MIN,0.0f) to use all available width,
        // or ImVec2(width,0.0f) for a specified width. ImVec2(0.0f,0.0f) uses ItemWidth.
        ImGui::ProgressBar(progress, ImVec2(0.0f, 0.0f));
        ImGui::SameLine(0.0f, ImGui::GetStyle().ItemInnerSpacing.x);
        ImGui::Text("Progress Bar");

        char buf[32];
        sprintf(buf, "%d/%d", (int)(progress * 1753), 1753);
        ImGui::ProgressBar(progress, ImVec2(0.f, 0.f), buf);

        // Pass an animated negative value, e.g. -1.0f * (float)ImGui::GetTime() is the recommended value.
        // Adjust the factor if you want to adjust the animation speed.
        ImGui::ProgressBar(-1.0f * (float)ImGui::GetTime(), ImVec2(0.0f, 0.0f), "Searching..");
        ImGui::SameLine(0.0f, ImGui::GetStyle().ItemInnerSpacing.x);
        ImGui::Text("Indeterminate");

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsQueryingStatuses()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsQueryingStatuses()
{
    if (ImGui::TreeNode("Querying Item Status (Edited/Active/Hovered etc.)"))
    {
```

