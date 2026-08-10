# Widgets/Basic/ListBox

- Marker: IMGUI_DEMO_MARKER("Widgets/Basic/ListBox")
- Source: .github/skills/imgui/demo.cpp:296
- Summary: Demonstrates ListBox behavior within Widgets / Basic.

```cpp
            IMGUI_DEMO_MARKER("Widgets/Basic/ListBox");
            const char* items[] = { "Apple", "Banana", "Cherry", "Kiwi", "Mango", "Orange", "Pineapple", "Strawberry", "Watermelon" };
            static int item_current = 1;
            ImGui::ListBox("listbox", &item_current, items, IM_COUNTOF(items), 4);
            ImGui::SameLine(); HelpMarker(
                "Using the simplified one-liner ListBox API here.\n"
                "Refer to the \"List boxes\" section below for an explanation of how to use the more flexible and general BeginListBox/EndListBox API.");
        }

        // Testing ImGuiOnceUponAFrame helper.
        //static ImGuiOnceUponAFrame once;
        //for (int i = 0; i < 5; i++)
        //    if (once)
        //        ImGui::Text("This will be displayed only once.");

        ImGui::TreePop();
    }
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowWidgetsBullets()
//-----------------------------------------------------------------------------

static void DemoWindowWidgetsBullets()
{
    if (ImGui::TreeNode("Bullets"))
    {
```

