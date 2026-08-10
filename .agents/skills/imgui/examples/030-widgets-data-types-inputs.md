# Widgets/Data Types/Inputs

- Marker: IMGUI_DEMO_MARKER("Widgets/Data Types/Inputs")
- Source: .github/skills/imgui/demo.cpp:776
- Summary: Demonstrates Inputs behavior within Widgets / Data Types.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Data Types/Inputs");
        static bool inputs_step = true;
        static ImGuiInputTextFlags flags = ImGuiInputTextFlags_None;
        ImGui::SeparatorText("Inputs");
        ImGui::Checkbox("Show step buttons", &inputs_step);
        ImGui::CheckboxFlags("ImGuiInputTextFlags_ReadOnly", &flags, ImGuiInputTextFlags_ReadOnly);
        ImGui::CheckboxFlags("ImGuiInputTextFlags_ParseEmptyRefVal", &flags, ImGuiInputTextFlags_ParseEmptyRefVal);
        ImGui::CheckboxFlags("ImGuiInputTextFlags_DisplayEmptyRefVal", &flags, ImGuiInputTextFlags_DisplayEmptyRefVal);
        ImGui::InputScalar("input s8",      ImGuiDataType_S8,     &s8_v,  inputs_step ? &s8_one  : NULL, NULL, "%d", flags);
        ImGui::InputScalar("input u8",      ImGuiDataType_U8,     &u8_v,  inputs_step ? &u8_one  : NULL, NULL, "%u", flags);
        ImGui::InputScalar("input s16",     ImGuiDataType_S16,    &s16_v, inputs_step ? &s16_one : NULL, NULL, "%d", flags);
        ImGui::InputScalar("input u16",     ImGuiDataType_U16,    &u16_v, inputs_step ? &u16_one : NULL, NULL, "%u", flags);
        ImGui::InputScalar("input s32",     ImGuiDataType_S32,    &s32_v, inputs_step ? &s32_one : NULL, NULL, "%d", flags);
        ImGui::InputScalar("input s32 hex", ImGuiDataType_S32,    &s32_v, inputs_step ? &s32_one : NULL, NULL, "%04X", flags);
        ImGui::InputScalar("input u32",     ImGuiDataType_U32,    &u32_v, inputs_step ? &u32_one : NULL, NULL, "%u", flags);
        ImGui::InputScalar("input u32 hex", ImGuiDataType_U32,    &u32_v, inputs_step ? &u32_one : NULL, NULL, "%08X", flags);
        ImGui::InputScalar("input s64",     ImGuiDataType_S64,    &s64_v, inputs_step ? &s64_one : NULL, NULL, NULL, flags);
        ImGui::InputScalar("input u64",     ImGuiDataType_U64,    &u64_v, inputs_step ? &u64_one : NULL, NULL, NULL, flags);
        ImGui::InputScalar("input float",   ImGuiDataType_Float,  &f32_v, inputs_step ? &f32_one : NULL, NULL, NULL, flags);
        ImGui::InputScalar("input double",  ImGuiDataType_Double, &f64_v, inputs_step ? &f64_one : NULL, NULL, NULL, flags);

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsDisableBlocks()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsDisableBlocks(ImGuiDemoWindowData* demo_data)
{
    if (ImGui::TreeNode("Disable Blocks"))
    {
```

