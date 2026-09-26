# Widgets/Text/Font Size

- Marker: IMGUI_DEMO_MARKER("Widgets/Text/Font Size")
- Source: .github/skills/imgui/demo.cpp:2899
- Summary: Demonstrates Font Size behavior within Widgets / Text.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Text/Font Size");
            ImGuiStyle& style = ImGui::GetStyle();
            const float global_scale = style.FontScaleMain * style.FontScaleDpi;
            ImGui::Text("style.FontScaleMain = %0.2f", style.FontScaleMain);
            ImGui::Text("style.FontScaleDpi = %0.2f", style.FontScaleDpi);
            ImGui::Text("global_scale = ~%0.2f", global_scale); // This is not technically accurate as internal scales may apply, but conceptually let's pretend it is.
            ImGui::Text("FontSize = %0.2f", ImGui::GetFontSize());

            ImGui::SeparatorText("");
            static float custom_size = 16.0f;
            ImGui::SliderFloat("custom_size", &custom_size, 10.0f, 100.0f, "%.0f");
            ImGui::Text("ImGui::PushFont(nullptr, custom_size);");
            ImGui::PushFont(NULL, custom_size);
            ImGui::Text("FontSize = %.2f (== %.2f * global_scale)", ImGui::GetFontSize(), custom_size);
            ImGui::PopFont();

            ImGui::SeparatorText("");
            static float custom_scale = 1.0f;
            ImGui::SliderFloat("custom_scale", &custom_scale, 0.5f, 4.0f, "%.2f");
            ImGui::Text("ImGui::PushFont(nullptr, style.FontSizeBase * custom_scale);");
            ImGui::PushFont(NULL, style.FontSizeBase * custom_scale);
            ImGui::Text("FontSize = %.2f (== style.FontSizeBase * %.2f * global_scale)", ImGui::GetFontSize(), custom_scale);
            ImGui::PopFont();

            ImGui::SeparatorText("");
            for (float scaling = 0.5f; scaling <= 4.0f; scaling += 0.5f)
            {
                ImGui::PushFont(NULL, style.FontSizeBase * scaling);
                ImGui::Text("FontSize = %.2f (== style.FontSizeBase * %.2f * global_scale)", ImGui::GetFontSize(), scaling);
                ImGui::PopFont();
            }

            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Word Wrapping"))
        {
```

