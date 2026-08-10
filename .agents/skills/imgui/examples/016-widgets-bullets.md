# Widgets/Bullets

- Marker: IMGUI_DEMO_MARKER("Widgets/Bullets")
- Source: .github/skills/imgui/demo.cpp:323
- Summary: Demonstrates Bullets behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Bullets");
        ImGui::BulletText("Bullet point 1");
        ImGui::BulletText("Bullet point 2\nOn multiple lines");
        if (ImGui::TreeNode("Tree node"))
        {
            ImGui::BulletText("Another bullet point");
            ImGui::TreePop();
        }
        ImGui::Bullet(); ImGui::Text("Bullet point 3 (two calls)");
        ImGui::Bullet(); ImGui::SmallButton("Button");
        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsCollapsingHeaders()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsCollapsingHeaders()
{
    if (ImGui::TreeNode("Collapsing Headers"))
    {
```

