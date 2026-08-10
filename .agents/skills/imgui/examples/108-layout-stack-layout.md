# Layout/Stack Layout

- Marker: IMGUI_DEMO_MARKER("Layout/Stack Layout")
- Source: .github/skills/imgui/demo.cpp:4532
- Summary: Demonstrates Stack Layout behavior within Layout.

```cpp
        IMGUI_DEMO_MARKER("Layout/Stack Layout");
        static bool widget_a = true, widget_b = true, widget_c = true;
        static bool spring_a = true, spring_ab = true, spring_bc = true, spring_c = true;
        static bool minimize_width = false, minimize_height = true;
        static bool horizontal = true, draw_springs = true;
        static ImVec2 item_spacing = ImGui::GetStyle().ItemSpacing;
        static float a_c_spring_weight = 0.0f;
        static float ab_spring_weight = 0.5f;
        static float alignment = 0.5f;

        struct funcs
        {
            static void VisibleSpring(float spring_weight)
            {
                ImGui::Spring(spring_weight);
                if (!draw_springs)
                    return;

                ImVec2 rect_min = ImGui::GetItemRectMin();
                ImVec2 rect_max = ImGui::GetItemRectMax();

                ImVec2 rect_size = ImGui::GetItemRectSize();
                if (rect_size.x <= 0.0f && rect_size.y <= 0.0f)
                    return;

                // Draw zig-zag
                float width = 0.0f, spacing = 0.0f;
                ImVec2 direction, origin;
                ImVec2 spacing_min, spring_max;

                if (horizontal)
                {
                    spacing     = floorf(item_spacing.x);
                    width       = rect_size.x - spacing;
                    origin      = ImVec2(floorf(rect_min.x), floorf(rect_min.y + (rect_max.y - rect_min.y) / 2));
                    direction   = ImVec2(1.0f, 0.0f);
                    spring_max  = ImVec2(rect_min.x + width, rect_max.y);
                    spacing_min = ImVec2(rect_min.x + width, rect_min.y);
                }
                else
                {
                    spacing     = floorf(item_spacing.y);
                    width       = rect_size.y - spacing;
                    origin      = ImVec2(floorf(rect_min.x + (rect_max.x - rect_min.x) / 2), floorf(rect_min.y));
                    direction   = ImVec2(0.0f, 1.0f);
                    spring_max  = ImVec2(rect_max.x, rect_min.y + width);
                    spacing_min = ImVec2(rect_min.x, rect_min.y + width);
                }

                if (spring_weight <= 0.0f && spacing <= 0.0f)
                    return;

                ImDrawList* draw_list = ImGui::GetWindowDrawList();

                draw_list->PushClipRect(rect_min, rect_max, true);

                draw_list->AddRectFilled(rect_min, spring_max, ImColor(80, 20, 80));
                draw_list->AddRectFilled(spacing_min, rect_max, ImColor(80, 20, 20));

                const float zig_zag_size = 3;
                ImVec2 normal = ImVec2(-direction.y, direction.x);

                draw_list->PathClear();
                origin.x += 0.5f;
                origin.y += 0.5f;
                draw_list->PathLineTo(origin);
                for (float x = zig_zag_size * 0.5f; x <= width; x += zig_zag_size)
                {
                    ImVec2 p;
                    p.x = origin.x + direction.x * x + normal.x * zig_zag_size;
                    p.y = origin.y + direction.y * x + normal.y * zig_zag_size;
                    draw_list->PathLineTo(p);
                    normal = ImVec2(-normal.x, -normal.y);
                }
                draw_list->PathStroke(ImColor(255, 255, 255, 190), false, 1.0f);

                draw_list->PopClipRect();
            }
        };

        ImGui::Checkbox("Widget A",  &widget_a);  ImGui::SameLine();
        ImGui::Checkbox("Widget B",  &widget_b);  ImGui::SameLine();
        ImGui::Checkbox("Widget C",  &widget_c);
        ImGui::Checkbox("Spring A",  &spring_a);  ImGui::SameLine();
        ImGui::Checkbox("Spring AB", &spring_ab); ImGui::SameLine();
        ImGui::Checkbox("Spring BC", &spring_bc); ImGui::SameLine();
        ImGui::Checkbox("Spring C",  &spring_c);
        ImGui::Checkbox("Horizontal", &horizontal);            ImGui::SameLine();
        ImGui::Checkbox("Minimize Width", &minimize_width);     ImGui::SameLine();
        ImGui::Checkbox("Minimize Height",  &minimize_height);
        ImGui::Checkbox("Draw Springs", &draw_springs); ImGui::SameLine();
        ImGui::TextUnformatted(" "); ImGui::SameLine();
        ImGui::ColorButton("- Spring", ImColor(80, 20, 80), ImGuiColorEditFlags_NoTooltip | ImGuiColorEditFlags_NoPicker); ImGui::SameLine();
        ImGui::TextUnformatted("Spring"); ImGui::SameLine();
        ImGui::TextUnformatted(" "); ImGui::SameLine();
        ImGui::ColorButton("- Spacing", ImColor(80, 20, 20), ImGuiColorEditFlags_NoTooltip | ImGuiColorEditFlags_NoPicker); ImGui::SameLine();
        ImGui::TextUnformatted("Item Spacing");
        ImGui::DragFloat("Item Spacing", horizontal ? &item_spacing.x : &item_spacing.y, 0.1f, 0.0f, 50.0f);
        ImGui::DragFloat("A & C Spring Weight", &a_c_spring_weight, 0.002f, 0.0f, 1.0f);
        ImGui::DragFloat("AB Spring Weight", &ab_spring_weight, 0.002f, 0.0f, 1.0f);
        if (ImGui::IsItemHovered()) ImGui::SetTooltip("BC Spring Weight = 1 - AB Spring Weight");
        ImGui::DragFloat("Minor Axis Alignment", &alignment, 0.002f, 0.0f, 1.0f);
        if (ImGui::IsItemHovered()) ImGui::SetTooltip("This is vertical alignment for horizontal layouts and horizontal alignment for vertical layouts.");
        ImGui::Text("Layout widgets:");
        ImGui::Text("| Spring A | Widget A | Spring AB | Widget B | Spring BC | Widget C | Spring C |");

        ImGui::Spacing();

        ImVec2 widget_size;
        widget_size.x = floorf(ImGui::GetContentRegionAvail().x / 4);
        widget_size.y = horizontal ? floorf(widget_size.x / 3) : widget_size.x;

        ImVec2 small_widget_size = widget_size;
        if (horizontal)
            small_widget_size.y = floorf(small_widget_size.y / 2);
        else
            small_widget_size.x = floorf(small_widget_size.x / 2);

        ImVec2 layout_size = ImVec2(widget_size.x * 4, widget_size.y * 4);
        if (minimize_width)  layout_size.x = 0.0f;
        if (minimize_height) layout_size.y = 0.0f;

        // Minor axis alignment can be set by style or directly in BeginHorizontal/BeginVertical
        // Example:
        //    ImGui::PushStyleVar(ImGuiStyleVar_LayoutAlign, alignment);

        ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(floorf(item_spacing.x), floorf(item_spacing.y)));

        if (horizontal) { ImGui::BeginHorizontal("h1", layout_size, alignment); } else { ImGui::BeginVertical("v1", layout_size, alignment); }
        if (spring_a)   { funcs::VisibleSpring(a_c_spring_weight); }
        if (widget_a)   { ImGui::Button("Widget A", widget_size); }
        if (spring_ab)  { funcs::VisibleSpring(ab_spring_weight); }
        if (widget_b)   { ImGui::Button("Widget B", small_widget_size); }
        if (spring_bc)  { funcs::VisibleSpring(1.0f - ab_spring_weight); }
        if (widget_c)   { ImGui::Button("Widget C", widget_size); }
        if (spring_c)   { funcs::VisibleSpring(a_c_spring_weight); }
        if (horizontal) { ImGui::EndHorizontal(); } else { ImGui::EndVertical(); }

        ImGui::PopStyleVar();

        ImDrawList* draw_list = ImGui::GetWindowDrawList();
        draw_list->AddRect(ImGui::GetItemRectMin(), ImGui::GetItemRectMax(), ImGui::GetColorU32(ImGuiCol_Border));

        ImGui::TreePop();
    }
#endif // IMGUI_HAS_STACK_LAYOUT
}

//-----------------------------------------------------------------------------
// [SECTION] DemoWindowPopups()
//-----------------------------------------------------------------------------

static void DemoWindowPopups()
{
    if (!ImGui::CollapsingHeader("Popups & Modal windows"))
        return;

    // The properties of popups windows are:
    // - They block normal mouse hovering detection outside them. (*)
    // - Unless modal, they can be closed by clicking anywhere outside them, or by pressing ESCAPE.
    // - Their visibility state (~bool) is held internally by Dear ImGui instead of being held by the programmer as
    //   we are used to with regular Begin() calls. User can manipulate the visibility state by calling OpenPopup().
    // (*) One can use IsItemHovered(ImGuiHoveredFlags_AllowWhenBlockedByPopup) to bypass it and detect hovering even
    //     when normally blocked by a popup.
    // Those three properties are connected. The library needs to hold their visibility state BECAUSE it can close
    // popups at any time.

    // Typical use for regular windows:
    //   bool my_tool_is_active = false; if (ImGui::Button("Open")) my_tool_is_active = true; [...] if (my_tool_is_active) Begin("My Tool", &my_tool_is_active) { [...] } End();
    // Typical use for popups:
    //   if (ImGui::Button("Open")) ImGui::OpenPopup("MyPopup"); if (ImGui::BeginPopup("MyPopup")) { [...] EndPopup(); }

    // With popups we have to go through a library call (here OpenPopup) to manipulate the visibility state.
    // This may be a bit confusing at first but it should quickly make sense. Follow on the examples below.

    if (ImGui::TreeNode("Popups"))
    {
```

