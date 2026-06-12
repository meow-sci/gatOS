# Layout/Basic Horizontal Layout/SameLine (more)

- Marker: IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/SameLine (more)")
- Source: .github/skills/imgui/demo.cpp:3912
- Summary: Demonstrates SameLine (more) behavior within Layout / Basic Horizontal Layout.

```cpp
        IMGUI_DEMO_MARKER("Layout/Basic Horizontal Layout/SameLine (more)");
        static bool c1 = false, c2 = false, c3 = false, c4 = false;
        ImGui::Checkbox("My", &c1); ImGui::SameLine();
        ImGui::Checkbox("Tailor", &c2); ImGui::SameLine();
        ImGui::Checkbox("Is", &c3); ImGui::SameLine();
        ImGui::Checkbox("Rich", &c4);

        // Various
        static float f0 = 1.0f, f1 = 2.0f, f2 = 3.0f;
        ImGui::PushItemWidth(ImGui::CalcTextSize("AAAAAAA").x);
        const char* items[] = { "AAAA", "BBBB", "CCCC", "DDDD" };
        static int item = -1;
        ImGui::Combo("Combo", &item, items, IM_COUNTOF(items)); ImGui::SameLine();
        ImGui::SliderFloat("X", &f0, 0.0f, 5.0f); ImGui::SameLine();
        ImGui::SliderFloat("Y", &f1, 0.0f, 5.0f); ImGui::SameLine();
        ImGui::SliderFloat("Z", &f2, 0.0f, 5.0f);

        ImGui::Text("Lists:");
        static int selection[4] = { 0, 1, 2, 3 };
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) ImGui::SameLine();
            ImGui::PushID(i);
            ImGui::ListBox("", &selection[i], items, IM_COUNTOF(items));
            ImGui::PopID();
            //ImGui::SetItemTooltip("ListBox %d hovered", i);
        }
        ImGui::PopItemWidth();

        // Dummy
```

