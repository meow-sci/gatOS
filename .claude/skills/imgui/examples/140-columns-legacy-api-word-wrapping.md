# Columns (legacy API)/Word-wrapping

- Marker: IMGUI_DEMO_MARKER("Columns (legacy API)/Word-wrapping")
- Source: .github/skills/imgui/demo.cpp:7178
- Summary: Demonstrates Word-wrapping behavior within Columns (legacy API).

```cpp
        IMGUI_DEMO_MARKER("Columns (legacy API)/Word-wrapping");
        ImGui::Columns(2, "word-wrapping");
        ImGui::Separator();
        ImGui::TextWrapped("The quick brown fox jumps over the lazy dog.");
        ImGui::TextWrapped("Hello Left");
        ImGui::NextColumn();
        ImGui::TextWrapped("The quick brown fox jumps over the lazy dog.");
        ImGui::TextWrapped("Hello Right");
        ImGui::Columns(1);
        ImGui::Separator();
        ImGui::TreePop();
    }

    if (ImGui::TreeNode("Horizontal Scrolling"))
    {
```

