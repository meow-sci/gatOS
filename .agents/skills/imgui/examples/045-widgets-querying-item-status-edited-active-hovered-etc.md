# Widgets/Querying Item Status (Edited,Active,Hovered etc.)

- Marker: IMGUI_DEMO_MARKER("Widgets/Querying Item Status (Edited,Active,Hovered etc.)")
- Source: .github/skills/imgui/demo.cpp:1375
- Summary: Demonstrates Querying Item Status (Edited,Active,Hovered etc.) behavior within Widgets.

```cpp
        IMGUI_DEMO_MARKER("Widgets/Querying Item Status (Edited,Active,Hovered etc.)");
        // Select an item type
        const char* item_names[] =
        {
            "Text", "Button", "Button (w/ repeat)", "Checkbox", "SliderFloat", "InputText", "InputTextMultiline", "InputFloat",
            "InputFloat3", "ColorEdit4", "Selectable", "MenuItem", "TreeNode", "TreeNode (w/ double-click)", "Combo", "ListBox"
        };
        static int item_type = 4;
        static bool item_disabled = false;
        ImGui::Combo("Item Type", &item_type, item_names, IM_COUNTOF(item_names), IM_COUNTOF(item_names));
        ImGui::SameLine();
        HelpMarker("Testing how various types of items are interacting with the IsItemXXX functions. Note that the bool return value of most ImGui function is generally equivalent to calling ImGui::IsItemHovered().");
        ImGui::Checkbox("Item Disabled", &item_disabled);

        // Submit selected items so we can query their status in the code following it.
        bool ret = false;
        static bool b = false;
        static float col4f[4] = { 1.0f, 0.5, 0.0f, 1.0f };
        static char str[16] = {};
        if (item_disabled)
            ImGui::BeginDisabled(true);
        if (item_type == 0) { ImGui::Text("ITEM: Text"); }                                              // Testing text items with no identifier/interaction
        if (item_type == 1) { ret = ImGui::Button("ITEM: Button"); }                                    // Testing button
        if (item_type == 2) { ImGui::PushItemFlag(ImGuiItemFlags_ButtonRepeat, true); ret = ImGui::Button("ITEM: Button"); ImGui::PopItemFlag(); } // Testing button (with repeater)
        if (item_type == 3) { ret = ImGui::Checkbox("ITEM: Checkbox", &b); }                            // Testing checkbox
        if (item_type == 4) { ret = ImGui::SliderFloat("ITEM: SliderFloat", &col4f[0], 0.0f, 1.0f); }   // Testing basic item
        if (item_type == 5) { ret = ImGui::InputText("ITEM: InputText", &str[0], IM_COUNTOF(str)); }  // Testing input text (which handles tabbing)
        if (item_type == 6) { ret = ImGui::InputTextMultiline("ITEM: InputTextMultiline", &str[0], IM_COUNTOF(str)); } // Testing input text (which uses a child window)
        if (item_type == 7) { ret = ImGui::InputFloat("ITEM: InputFloat", col4f, 1.0f); }               // Testing +/- buttons on scalar input
        if (item_type == 8) { ret = ImGui::InputFloat3("ITEM: InputFloat3", col4f); }                   // Testing multi-component items (IsItemXXX flags are reported merged)
        if (item_type == 9) { ret = ImGui::ColorEdit4("ITEM: ColorEdit4", col4f); }                     // Testing multi-component items (IsItemXXX flags are reported merged)
        if (item_type == 10) { ret = ImGui::Selectable("ITEM: Selectable"); }                            // Testing selectable item
        if (item_type == 11) { ret = ImGui::MenuItem("ITEM: MenuItem"); }                                // Testing menu item (they use ImGuiButtonFlags_PressedOnRelease button policy)
        if (item_type == 12) { ret = ImGui::TreeNode("ITEM: TreeNode"); if (ret) ImGui::TreePop(); }     // Testing tree node
        if (item_type == 13) { ret = ImGui::TreeNodeEx("ITEM: TreeNode w/ ImGuiTreeNodeFlags_OpenOnDoubleClick", ImGuiTreeNodeFlags_OpenOnDoubleClick | ImGuiTreeNodeFlags_NoTreePushOnOpen); } // Testing tree node with ImGuiButtonFlags_PressedOnDoubleClick button policy.
        if (item_type == 14) { const char* items[] = { "Apple", "Banana", "Cherry", "Kiwi" }; static int current = 1; ret = ImGui::Combo("ITEM: Combo", &current, items, IM_COUNTOF(items)); }
        if (item_type == 15) { const char* items[] = { "Apple", "Banana", "Cherry", "Kiwi" }; static int current = 1; ret = ImGui::ListBox("ITEM: ListBox", &current, items, IM_COUNTOF(items), IM_COUNTOF(items)); }

        bool hovered_delay_none = ImGui::IsItemHovered();
        bool hovered_delay_stationary = ImGui::IsItemHovered(ImGuiHoveredFlags_Stationary);
        bool hovered_delay_short = ImGui::IsItemHovered(ImGuiHoveredFlags_DelayShort);
        bool hovered_delay_normal = ImGui::IsItemHovered(ImGuiHoveredFlags_DelayNormal);
        bool hovered_delay_tooltip = ImGui::IsItemHovered(ImGuiHoveredFlags_ForTooltip); // = Normal + Stationary

        // Display the values of IsItemHovered() and other common item state functions.
        // Note that the ImGuiHoveredFlags_XXX flags can be combined.
        // Because BulletText is an item itself and that would affect the output of IsItemXXX functions,
        // we query every state in a single call to avoid storing them and to simplify the code.
        ImGui::BulletText(
            "Return value = %d\n"
            "IsItemFocused() = %d\n"
            "IsItemHovered() = %d\n"
            "IsItemHovered(_AllowWhenBlockedByPopup) = %d\n"
            "IsItemHovered(_AllowWhenBlockedByActiveItem) = %d\n"
            "IsItemHovered(_AllowWhenOverlappedByItem) = %d\n"
            "IsItemHovered(_AllowWhenOverlappedByWindow) = %d\n"
            "IsItemHovered(_AllowWhenDisabled) = %d\n"
            "IsItemHovered(_RectOnly) = %d\n"
            "IsItemActive() = %d\n"
            "IsItemEdited() = %d\n"
            "IsItemActivated() = %d\n"
            "IsItemDeactivated() = %d\n"
            "IsItemDeactivatedAfterEdit() = %d\n"
            "IsItemVisible() = %d\n"
            "IsItemClicked() = %d\n"
            "IsItemToggledOpen() = %d\n"
            "GetItemRectMin() = (%.1f, %.1f)\n"
            "GetItemRectMax() = (%.1f, %.1f)\n"
            "GetItemRectSize() = (%.1f, %.1f)",
            ret,
            ImGui::IsItemFocused(),
            ImGui::IsItemHovered(),
            ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenBlockedByPopup),
            ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenBlockedByActiveItem),
            ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenOverlappedByItem),
            ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenOverlappedByWindow),
            ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled),
            ImGui::IsItemHovered(ImGuiHoveredFlags_RectOnly),
            ImGui::IsItemActive(),
            ImGui::IsItemEdited(),
            ImGui::IsItemActivated(),
            ImGui::IsItemDeactivated(),
            ImGui::IsItemDeactivatedAfterEdit(),
            ImGui::IsItemVisible(),
            ImGui::IsItemClicked(),
            ImGui::IsItemToggledOpen(),
            ImGui::GetItemRectMin().x, ImGui::GetItemRectMin().y,
            ImGui::GetItemRectMax().x, ImGui::GetItemRectMax().y,
            ImGui::GetItemRectSize().x, ImGui::GetItemRectSize().y
        );
        ImGui::BulletText(
            "with Hovering Delay or Stationary test:\n"
            "IsItemHovered() = %d\n"
            "IsItemHovered(_Stationary) = %d\n"
            "IsItemHovered(_DelayShort) = %d\n"
            "IsItemHovered(_DelayNormal) = %d\n"
            "IsItemHovered(_Tooltip) = %d",
            hovered_delay_none, hovered_delay_stationary, hovered_delay_short, hovered_delay_normal, hovered_delay_tooltip);

        if (item_disabled)
            ImGui::EndDisabled();

        char buf[1] = "";
        ImGui::InputText("unused", buf, IM_COUNTOF(buf), ImGuiInputTextFlags_ReadOnly);
        ImGui::SameLine();
        HelpMarker("This widget is only here to be able to tab-out of the widgets above and see e.g. Deactivated() status.");

        ImGui::TreePop();
    }

    if (ImGui::TreeNode("Querying Window Status (Focused/Hovered etc.)"))
    {
```

