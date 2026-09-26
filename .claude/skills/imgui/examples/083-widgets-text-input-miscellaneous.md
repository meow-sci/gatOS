# Widgets/Text Input/Miscellaneous

- Marker: IMGUI_DEMO_MARKER("Widgets/Text Input/Miscellaneous")
- Source: .github/skills/imgui/demo.cpp:3228
- Summary: Demonstrates Miscellaneous behavior within Widgets / Text Input.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Text Input/Miscellaneous");
            static char buf1[16];
            static ImGuiInputTextFlags flags = ImGuiInputTextFlags_EscapeClearsAll;
            ImGui::CheckboxFlags("ImGuiInputTextFlags_EscapeClearsAll", &flags, ImGuiInputTextFlags_EscapeClearsAll);
            ImGui::CheckboxFlags("ImGuiInputTextFlags_ReadOnly", &flags, ImGuiInputTextFlags_ReadOnly);
            ImGui::CheckboxFlags("ImGuiInputTextFlags_NoUndoRedo", &flags, ImGuiInputTextFlags_NoUndoRedo);
            ImGui::InputText("Hello", buf1, IM_COUNTOF(buf1), flags);
            ImGui::TreePop();
        }

        ImGui::TreePop();
    }

}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsTooltips()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsTooltips()
{
    if (ImGui::TreeNode("Tooltips"))
    {
```

