# Inputs & Focus/Outputs

- Marker: IMGUI_DEMO_MARKER("Inputs & Focus/Outputs")
- Source: .github/skills/imgui/demo.cpp:7315
- Summary: Demonstrates Outputs behavior within Inputs & Focus.

```cpp
            IMGUI_DEMO_MARKER("Inputs & Focus/Outputs");
            ImGui::Text("io.WantCaptureMouse: %d", io.WantCaptureMouse);
            ImGui::Text("io.WantCaptureMouseUnlessPopupClose: %d", io.WantCaptureMouseUnlessPopupClose);
            ImGui::Text("io.WantCaptureKeyboard: %d", io.WantCaptureKeyboard);
            ImGui::Text("io.WantTextInput: %d", io.WantTextInput);
            ImGui::Text("io.WantSetMousePos: %d", io.WantSetMousePos);
            ImGui::Text("io.NavActive: %d, io.NavVisible: %d", io.NavActive, io.NavVisible);
```

