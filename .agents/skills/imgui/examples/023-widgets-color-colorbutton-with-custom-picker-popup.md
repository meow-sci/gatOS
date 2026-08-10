# Widgets/Color/ColorButton (with custom Picker popup)

- Marker: IMGUI_DEMO_MARKER("Widgets/Color/ColorButton (with custom Picker popup)")
- Source: .github/skills/imgui/demo.cpp:414
- Summary: Demonstrates ColorButton (with custom Picker popup) behavior within Widgets / Color.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Color/ColorButton (with custom Picker popup)");
        ImGui::Text("Color button with Custom Picker Popup:");

        // Generate a default palette. The palette will persist and can be edited.
        static bool saved_palette_init = true;
        static ImVec4 saved_palette[32] = {};
        if (saved_palette_init)
        {
            for (int n = 0; n < IM_COUNTOF(saved_palette); n++)
            {
                ImGui::ColorConvertHSVtoRGB(n / 31.0f, 0.8f, 0.8f,
                    saved_palette[n].x, saved_palette[n].y, saved_palette[n].z);
                saved_palette[n].w = 1.0f; // Alpha
            }
            saved_palette_init = false;
        }

        static ImVec4 backup_color;
        bool open_popup = ImGui::ColorButton("MyColor##3b", color, base_flags);
        ImGui::SameLine(0, ImGui::GetStyle().ItemInnerSpacing.x);
        open_popup |= ImGui::Button("Palette");
        if (open_popup)
        {
            ImGui::OpenPopup("mypicker");
            backup_color = color;
        }
        if (ImGui::BeginPopup("mypicker"))
        {
            ImGui::Text("MY CUSTOM COLOR PICKER WITH AN AMAZING PALETTE!");
            ImGui::Separator();
            ImGui::ColorPicker4("##picker", (float*)&color, base_flags | ImGuiColorEditFlags_NoSidePreview | ImGuiColorEditFlags_NoSmallPreview);
            ImGui::SameLine();

            ImGui::BeginGroup(); // Lock X position
            ImGui::Text("Current");
            ImGui::ColorButton("##current", color, ImGuiColorEditFlags_NoPicker | ImGuiColorEditFlags_AlphaPreviewHalf, ImVec2(60, 40));
            ImGui::Text("Previous");
            if (ImGui::ColorButton("##previous", backup_color, ImGuiColorEditFlags_NoPicker | ImGuiColorEditFlags_AlphaPreviewHalf, ImVec2(60, 40)))
                color = backup_color;
            ImGui::Separator();
            ImGui::Text("Palette");
            for (int n = 0; n < IM_COUNTOF(saved_palette); n++)
            {
                ImGui::PushID(n);
                if ((n % 8) != 0)
                    ImGui::SameLine(0.0f, ImGui::GetStyle().ItemSpacing.y);

                ImGuiColorEditFlags palette_button_flags = ImGuiColorEditFlags_NoAlpha | ImGuiColorEditFlags_NoPicker | ImGuiColorEditFlags_NoTooltip;
                if (ImGui::ColorButton("##palette", saved_palette[n], palette_button_flags, ImVec2(20, 20)))
                    color = ImVec4(saved_palette[n].x, saved_palette[n].y, saved_palette[n].z, color.w); // Preserve alpha!

                // Allow user to drop colors into each palette entry. Note that ColorButton() is already a
                // drag source by default, unless specifying the ImGuiColorEditFlags_NoDragDrop flag.
                if (ImGui::BeginDragDropTarget())
                {
                    if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload(IMGUI_PAYLOAD_TYPE_COLOR_3F))
                        memcpy((float*)&saved_palette[n], payload->Data, sizeof(float) * 3);
                    if (const ImGuiPayload* payload = ImGui::AcceptDragDropPayload(IMGUI_PAYLOAD_TYPE_COLOR_4F))
                        memcpy((float*)&saved_palette[n], payload->Data, sizeof(float) * 4);
                    ImGui::EndDragDropTarget();
                }

                ImGui::PopID();
            }
            ImGui::EndGroup();
            ImGui::EndPopup();
        }
```

