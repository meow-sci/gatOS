# Widgets/Basic/InputText

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/InputText")
- Source: .github/skills/imgui/demo.cpp:179
- Summary: Demonstrates InputText behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/InputText");
            static char str0[128] = "Hello, world!";
            ImGui::InputText("input text", str0, IM_COUNTOF(str0));
            ImGui::SameLine(); HelpMarker(
                "USER:\n"
                "Hold Shift or use mouse to select text.\n"
                "Ctrl+Left/Right to word jump.\n"
                "Ctrl+A or Double-Click to select all.\n"
                "Ctrl+X,Ctrl+C,Ctrl+V for clipboard.\n"
                "Ctrl+Z to undo, Ctrl+Y/Ctrl+Shift+Z to redo.\n"
                "Escape to revert.\n\n"
                "PROGRAMMER:\n"
                "You can use the ImGuiInputTextFlags_CallbackResize facility if you need to wire InputText() "
                "to a dynamic string type. See misc/cpp/imgui_stdlib.h for an example (this is not demonstrated "
                "in imgui_demo.cpp).");

            static char str1[128] = "";
            ImGui::InputTextWithHint("input text (w/ hint)", "enter text here", str1, IM_COUNTOF(str1));
```

