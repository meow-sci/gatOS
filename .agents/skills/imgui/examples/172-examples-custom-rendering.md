# Examples/Custom rendering

- Marker: IMGUI_DEMO_MARKER("Examples/Custom rendering")
- Source: .github/skills/imgui/demo.cpp:9524
- Summary: Demonstrates Custom rendering behavior within Examples.

```cpp
    IMGUI_DEMO_MARKER("Examples/Custom rendering");

    // Tip: If you do a lot of custom rendering, you probably want to use your own geometrical types and benefit of
    // overloaded operators, etc. Define IM_VEC2_CLASS_EXTRA in imconfig.h to create implicit conversions between your
    // types and ImVec2/ImVec4. Dear ImGui defines overloaded operators but they are internal to imgui.cpp and not
    // exposed outside (to avoid messing with your types) In this example we are not using the maths operators!

    if (ImGui::BeginTabBar("##TabBar"))
    {
        if (ImGui::BeginTabItem("Primitives"))
        {
```

