# Widgets/Text Input/Filtered Text Input

- Marker: IMGUI_DEMO_MARKER("Widgets/Text Input/Filtered Text Input")
- Source: .github/skills/imgui/demo.cpp:3067
- Summary: Demonstrates Filtered Text Input behavior within Widgets / Text Input.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Text Input/Filtered Text Input");
            struct TextFilters
            {
                // Modify character input by altering 'data->Eventchar' (ImGuiInputTextFlags_CallbackCharFilter callback)
                static int FilterCasingSwap(ImGuiInputTextCallbackData* data)
                {
                    if (data->EventChar >= 'a' && data->EventChar <= 'z') { data->EventChar -= 'a' - 'A'; } // Lowercase becomes uppercase
                    else if (data->EventChar >= 'A' && data->EventChar <= 'Z') { data->EventChar += 'a' - 'A'; } // Uppercase becomes lowercase
                    return 0;
                }

                // Return 0 (pass) if the character is 'i' or 'm' or 'g' or 'u' or 'i', otherwise return 1 (filter out)
                static int FilterImGuiLetters(ImGuiInputTextCallbackData* data)
                {
                    if (data->EventChar < 256 && strchr("imgui", (char)data->EventChar))
                        return 0;
                    return 1;
                }
            };

            static char buf1[32] = ""; ImGui::InputText("default", buf1, IM_COUNTOF(buf1));
            static char buf2[32] = ""; ImGui::InputText("decimal", buf2, IM_COUNTOF(buf2), ImGuiInputTextFlags_CharsDecimal);
            static char buf3[32] = ""; ImGui::InputText("hexadecimal", buf3, IM_COUNTOF(buf3), ImGuiInputTextFlags_CharsHexadecimal | ImGuiInputTextFlags_CharsUppercase);
            static char buf4[32] = ""; ImGui::InputText("uppercase", buf4, IM_COUNTOF(buf4), ImGuiInputTextFlags_CharsUppercase);
            static char buf5[32] = ""; ImGui::InputText("no blank", buf5, IM_COUNTOF(buf5), ImGuiInputTextFlags_CharsNoBlank);
            static char buf6[32] = ""; ImGui::InputText("casing swap", buf6, IM_COUNTOF(buf6), ImGuiInputTextFlags_CallbackCharFilter, TextFilters::FilterCasingSwap); // Use CharFilter callback to replace characters.
            static char buf7[32] = ""; ImGui::InputText("\"imgui\"", buf7, IM_COUNTOF(buf7), ImGuiInputTextFlags_CallbackCharFilter, TextFilters::FilterImGuiLetters); // Use CharFilter callback to disable some characters.
            ImGui::TreePop();
        }

        if (ImGui::TreeNode("Password Input"))
        {
```

