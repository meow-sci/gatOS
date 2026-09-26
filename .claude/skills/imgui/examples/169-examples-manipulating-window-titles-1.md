# Examples/Manipulating window titles##1

- Marker: IMGUI_DEMO_MARKER("Examples/Manipulating window titles##1")
- Source: .github/skills/imgui/demo.cpp:9484
- Summary: Demonstrates Manipulating window titles##1 behavior within Examples.

```cpp
    IMGUI_DEMO_MARKER("Examples/Manipulating window titles##1");
    ImGui::Text("This is window 1.\nMy title is the same as window 2, but my identifier is unique.");
    ImGui::End();

    ImGui::SetNextWindowPos(ImVec2(base_pos.x + 100, base_pos.y + 200), ImGuiCond_FirstUseEver);
    ImGui::Begin("Same title as another window##2");
```

