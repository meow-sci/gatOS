# Widgets/Images

- Marker: IMGUI_DEMO_MARKER("Widgets/Images")
- Source: .github/skills/imgui/demo.cpp:1054
- Summary: Demonstrates Images behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Images");
        ImGuiIO& io = ImGui::GetIO();
        ImGui::TextWrapped(
            "Below we are displaying the font texture (which is the only texture we have access to in this demo). "
            "Use the 'ImTextureID' type as storage to pass pointers or identifier to your own texture data. "
            "Hover the texture for a zoomed view!");

        // Below we are displaying the font texture because it is the only texture we have access to inside the demo!
        // Read description about ImTextureID/ImTextureRef and FAQ for details about texture identifiers.
        // If you use one of the default imgui_impl_XXXX.cpp rendering backend, they all have comments at the top
        // of their respective source file to specify what they are using as texture identifier, for example:
        // - The imgui_impl_dx11.cpp renderer expect a 'ID3D11ShaderResourceView*' pointer.
        // - The imgui_impl_opengl3.cpp renderer expect a GLuint OpenGL texture identifier, etc.
        // So with the DirectX11 backend, you call ImGui::Image() with a 'ID3D11ShaderResourceView*' cast to ImTextureID.
        // - If you decided that ImTextureID = MyEngineTexture*, then you can pass your MyEngineTexture* pointers
        //   to ImGui::Image(), and gather width/height through your own functions, etc.
        // - You can use ShowMetricsWindow() to inspect the draw data that are being passed to your renderer,
        //   it will help you debug issues if you are confused about it.
        // - Consider using the lower-level ImDrawList::AddImage() API, via ImGui::GetWindowDrawList()->AddImage().
        // - Read https://github.com/ocornut/imgui/blob/master/docs/FAQ.md
        // - Read https://github.com/ocornut/imgui/wiki/Image-Loading-and-Displaying-Examples

        // Grab the current texture identifier used by the font atlas.
        ImTextureRef my_tex_id = io.Fonts->TexRef;

        // Regular user code should never have to care about TexData-> fields, but since we want to display the entire texture here, we pull Width/Height from it.
        float my_tex_w = (float)io.Fonts->TexData->Width;
        float my_tex_h = (float)io.Fonts->TexData->Height;

        {
            ImGui::Text("%.0fx%.0f", my_tex_w, my_tex_h);
            ImVec2 pos = ImGui::GetCursorScreenPos();
            ImVec2 uv_min = ImVec2(0.0f, 0.0f); // Top-left
            ImVec2 uv_max = ImVec2(1.0f, 1.0f); // Lower-right
            ImGui::PushStyleVar(ImGuiStyleVar_ImageBorderSize, IM_MAX(1.0f, ImGui::GetStyle().ImageBorderSize));
            ImGui::ImageWithBg(my_tex_id, ImVec2(my_tex_w, my_tex_h), uv_min, uv_max, ImVec4(0.0f, 0.0f, 0.0f, 1.0f));
            if (ImGui::BeginItemTooltip())
            {
                float region_sz = 32.0f;
                float region_x = io.MousePos.x - pos.x - region_sz * 0.5f;
                float region_y = io.MousePos.y - pos.y - region_sz * 0.5f;
                float zoom = 4.0f;
                if (region_x < 0.0f) { region_x = 0.0f; }
                else if (region_x > my_tex_w - region_sz) { region_x = my_tex_w - region_sz; }
                if (region_y < 0.0f) { region_y = 0.0f; }
                else if (region_y > my_tex_h - region_sz) { region_y = my_tex_h - region_sz; }
                ImGui::Text("Min: (%.2f, %.2f)", region_x, region_y);
                ImGui::Text("Max: (%.2f, %.2f)", region_x + region_sz, region_y + region_sz);
                ImVec2 uv0 = ImVec2((region_x) / my_tex_w, (region_y) / my_tex_h);
                ImVec2 uv1 = ImVec2((region_x + region_sz) / my_tex_w, (region_y + region_sz) / my_tex_h);
                ImGui::ImageWithBg(my_tex_id, ImVec2(region_sz * zoom, region_sz * zoom), uv0, uv1, ImVec4(0.0f, 0.0f, 0.0f, 1.0f));
                ImGui::EndTooltip();
            }
            ImGui::PopStyleVar();
        }
```

