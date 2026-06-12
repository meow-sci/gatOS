# Widgets/Basic/Button

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/Button")
- Source: .github/skills/imgui/demo.cpp:111
- Summary: Demonstrates Button behavior within Widgets / Basic.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Basic/Button");
        static int clicked = 0;
        if (ImGui::Button("Button"))
            clicked++;
        if (clicked & 1)
        {
            ImGui::SameLine();
            ImGui::Text("Thanks for clicking me!");
        }
```

