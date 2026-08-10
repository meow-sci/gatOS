# Examples/Manipulating window titles##2

- Marker: IMGUI_DEMO_MARKER("Examples/Manipulating window titles##2")
- Source: .github/skills/imgui/demo.cpp:9490
- Summary: Demonstrates Manipulating window titles##2 behavior within Examples.

```cpp
    IMGUI_DEMO_MARKER("Examples/Manipulating window titles##2");
    ImGui::Text("This is window 2.\nMy title is the same as window 1, but my identifier is unique.");
    ImGui::End();

    // Using "###" to display a changing title but keep a static identifier "AnimatedTitle"
    char buf[128];
    sprintf(buf, "Animated title %c %d###AnimatedTitle", "|/-\\"[(int)(ImGui::GetTime() / 0.25f) & 3], ImGui::GetFrameCount());
    ImGui::SetNextWindowPos(ImVec2(base_pos.x + 100, base_pos.y + 300), ImGuiCond_FirstUseEver);
    ImGui::Begin(buf);
```

