# Layout/Basic Horizontal Layout/Dummy

- Marker: IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/Dummy")
- Source: .github/skills/imgui/demo.cpp:3942
- Summary: Demonstrates Dummy behavior within Layout / Basic Horizontal Layout.

```cpp
        IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/Dummy");
        ImVec2 button_sz(40, 40);
        ImGui::Button("A", button_sz); ImGui::SameLine();
        ImGui::Dummy(button_sz); ImGui::SameLine();
        ImGui::Button("B", button_sz);

        // Manually wrapping
        // (we should eventually provide this as an automatic layout feature, but for now you can do it manually)
```

