# Using Dockspace (Read note below)

- Marker: IMGUI_DEMO_MARKER("Using Dockspace (Read note below)")
- Source: .github/skills/imgui/demo.cpp:9854
- Summary: Demonstrates Using Dockspace (Read note below) functionality.

```cpp
        IMGUI_DEMO_MARKER("Using Dockspace (Read note below)");
        //
        // **This example is specific to Dear ImGui Explorer**
        // (we are here creating a dockspace in a window)
        //
        // Most apps will simply want to allow docking windows on the edge of the screen (viewport)
        // For this, choose between:
        //    // Create a dockspace in main viewport
        //    ImGui::DockSpaceOverViewport(); // call this just after ImGui::NewFrame()
        // And:
        //    // Create a dockspace in main viewport, where central node is transparent.
        //    ImGui::DockSpaceOverViewport(0, nullptr, ImGuiDockNodeFlags_PassthruCentralNode); // call this just after ImGui::NewFrame()
        //
        // (See ShowExampleAppDockSpaceBasic())

    }

    // Submit the DockSpace widget inside our window
    // - Note that the id here is different from the one used by DockSpaceOverViewport(), so docking state won't get transfered between "Basic" and "Advanced" demos.
    // - If we made the ShowExampleAppDockSpaceBasic() calculate its own ID and pass it to DockSpaceOverViewport() the ID could easily match.
    ImGuiID dockspace_id = ImGui::GetID("MyDockSpace");
    ImGui::DockSpace(dockspace_id, ImVec2(0.0f, 0.0f), dockspace_flags);

    ImGui::End();

    // Create 5 windows for the user to play with docking
    for (int i = 0; i < 3; ++i)
    {
        char window_name[100];
        snprintf(window_name, 100, "Dockable window #%i", i + 1);
        ImVec2 pos(100, 100 + i * 50);
        ImGui::SetNextWindowPos(pos, ImGuiCond_Once);
        ImGui::Begin(window_name);
        ImGui::Text("Hello from %s", window_name);
        ImGui::End();
    }
}

// THIS IS A DEMO FOR ADVANCED USAGE OF DockSpace().
// MOST REGULAR APPLICATIONS WANTING TO ALLOW DOCKING WINDOWS ON THE EDGE OF YOUR SCREEN CAN SIMPLY USE:
//    ImGui::NewFrame(); + ImGui::DockSpaceOverViewport();                                                   // Create a dockspace in main viewport
// OR:
//    ImGui::NewFrame(); + ImGui::DockSpaceOverViewport(0, nullptr, ImGuiDockNodeFlags_PassthruCentralNode); // Create a dockspace in main viewport, where central node is transparent.
// Demonstrate using DockSpace() to create an explicit docking node within an existing window, with various options.
// Read https://github.com/ocornut/imgui/wiki/Docking for details.
// The reasons we do not use DockSpaceOverViewport() in this demo is because:
// - (1) we allow the host window to be floating/moveable instead of filling the viewport (when args->IsFullscreen == false)
//       which is mostly to showcase the idea that DockSpace() may be submitted anywhere.
//       Also see 'Demo->Examples->Documents' for a less abstract version of this.
// - (2) we allow the host window to have padding (when args->UsePadding == true)
// - (3) we expose variety of other flags.
static void ShowExampleAppDockSpaceAdvanced(ImGuiDemoDockspaceArgs* args, bool* p_open)
{
    ImGuiDockNodeFlags dockspace_flags = args->DockSpaceFlags;

    // We are using the ImGuiWindowFlags_NoDocking flag to make the parent window not dockable into,
    // because it would be confusing to have two docking targets within each others.
    ImGuiWindowFlags window_flags = ImGuiWindowFlags_NoDocking;
    if (args->IsFullscreen)
    {
        // Fullscreen dockspace: practically the same as calling DockSpaceOverViewport();
        const ImGuiViewport* viewport = ImGui::GetMainViewport();
        ImGui::SetNextWindowPos(viewport->WorkPos);
        ImGui::SetNextWindowSize(viewport->WorkSize);
        ImGui::SetNextWindowViewport(viewport->ID);
        ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0.0f);
        ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
        window_flags |= ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove;
        window_flags |= ImGuiWindowFlags_NoBringToFrontOnFocus | ImGuiWindowFlags_NoNavFocus;
        window_flags |= ImGuiWindowFlags_NoBackground;
    }
    else
    {
        // Floating dockspace
        dockspace_flags &= ~ImGuiDockNodeFlags_PassthruCentralNode;
    }

    // Important: note that we proceed even if Begin() returns false (aka window is collapsed).
    // This is because we want to keep our DockSpace() active. If a DockSpace() is inactive,
    // all active windows docked into it will lose their parent and become undocked.
    // We cannot preserve the docking relationship between an active window and an inactive docking, otherwise
    // any change of dockspace/settings would lead to windows being stuck in limbo and never being visible.
    if (!args->KeepWindowPadding)
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0.0f, 0.0f));
    ImGui::Begin("Window with a DockSpace", p_open, window_flags);
    if (!args->KeepWindowPadding)
        ImGui::PopStyleVar();

    if (args->IsFullscreen)
        ImGui::PopStyleVar(2);

    // Submit the DockSpace widget inside our window
    // - Note that the id here is different from the one used by DockSpaceOverViewport(), so docking state won't get transfered between "Basic" and "Advanced" demos.
    // - If we made the ShowExampleAppDockSpaceBasic() calculate its own ID and pass it to DockSpaceOverViewport() the ID could easily match.
    ImGuiID dockspace_id = ImGui::GetID("MyDockSpace");
    ImGui::DockSpace(dockspace_id, ImVec2(0.0f, 0.0f), dockspace_flags);

    ImGui::End();
}

static void ShowExampleAppDockSpaceBasic(ImGuiDockNodeFlags flags)
{
    // Basic version which you can use in many apps:
    // e.g:
    //   ImGui::DockSpaceOverViewport();
    // or:
    //   ImGui::DockSpaceOverViewport(0, nullptr, ImGuiDockNodeFlags_PassthruCentralNode); // Central node will be transparent
    // or:
    //   ImGuiViewport* viewport = ImGui::GetMainViewport();
    //   ImGui::DockSpaceOverViewport(0, viewport, ImGuiDockNodeFlags_None);

    ImGui::DockSpaceOverViewport(0, nullptr, flags);
}

void ShowExampleAppDockSpace(bool* p_open)
{
    static int opt_demo_mode = 0;
    static bool opt_demo_mode_changed = false;
    static ImGuiDemoDockspaceArgs args;

    if (sUseExampleAppDockSpaceImguiExplorer)
        ShowExampleAppDockSpaceImguiExplorer(&args, p_open);
    else
    {
        if (opt_demo_mode == 0)
            ShowExampleAppDockSpaceBasic(args.DockSpaceFlags);
        else
            ShowExampleAppDockSpaceAdvanced(&args, p_open);
    }

    // Refocus our window to minimize perceived loss of focus when changing mode (caused by the fact that each use a different window, which would not happen in a real app)
    if (opt_demo_mode_changed)
        ImGui::SetNextWindowFocus();

    ImGui::Begin("Examples: Dockspace", p_open, ImGuiWindowFlags_MenuBar);
    if (!sUseExampleAppDockSpaceImguiExplorer)
    {
        opt_demo_mode_changed = false;
        opt_demo_mode_changed |= ImGui::RadioButton("Basic demo mode", &opt_demo_mode, 0);
        opt_demo_mode_changed |= ImGui::RadioButton("Advanced demo mode", &opt_demo_mode, 1);
        ImGui::SeparatorText("Options");
    }

    if (sUseExampleAppDockSpaceImguiExplorer)
        opt_demo_mode = 1;

    if ((ImGui::GetIO().ConfigFlags & ImGuiConfigFlags_DockingEnable) == 0)
    {
        ShowDockingDisabledMessage();
    }
    else if (opt_demo_mode == 0)
    {
        args.DockSpaceFlags &= ImGuiDockNodeFlags_PassthruCentralNode; // Allowed flags
        ImGui::CheckboxFlags("Flag: PassthruCentralNode", &args.DockSpaceFlags, ImGuiDockNodeFlags_PassthruCentralNode);
    }
    else if (opt_demo_mode == 1)
    {
        if (!sUseExampleAppDockSpaceImguiExplorer)
            ImGui::Checkbox("Fullscreen", &args.IsFullscreen);
        ImGui::Checkbox("Keep Window Padding", &args.KeepWindowPadding);
        ImGui::SameLine();
        HelpMarker("This is mostly exposed to facilitate understanding that a DockSpace() is _inside_ a window.");
        ImGui::BeginDisabled(args.IsFullscreen == false);
        ImGui::CheckboxFlags("Flag: PassthruCentralNode",      &args.DockSpaceFlags, ImGuiDockNodeFlags_PassthruCentralNode);
        ImGui::EndDisabled();
        ImGui::CheckboxFlags("Flag: NoDockingOverCentralNode", &args.DockSpaceFlags, ImGuiDockNodeFlags_NoDockingOverCentralNode);
        ImGui::CheckboxFlags("Flag: NoDockingSplit",           &args.DockSpaceFlags, ImGuiDockNodeFlags_NoDockingSplit);
        ImGui::CheckboxFlags("Flag: NoUndocking",              &args.DockSpaceFlags, ImGuiDockNodeFlags_NoUndocking);
        ImGui::CheckboxFlags("Flag: NoResize",                 &args.DockSpaceFlags, ImGuiDockNodeFlags_NoResize);
        ImGui::CheckboxFlags("Flag: AutoHideTabBar",           &args.DockSpaceFlags, ImGuiDockNodeFlags_AutoHideTabBar);
    }

    // Show demo options and help
    if (ImGui::BeginMenuBar())
    {
        if (ImGui::BeginMenu("Help"))
        {
            ImGui::TextUnformatted(
                "This demonstrates the use of ImGui::DockSpace() which allows you to manually\ncreate a docking node _within_ another window." "\n"
                "The \"Basic\" version uses the ImGui::DockSpaceOverViewport() helper. Most applications can probably use this.");
            ImGui::Separator();
            ImGui::TextUnformatted("When docking is enabled, you can ALWAYS dock MOST window into another! Try it now!" "\n"
                "- Drag from window title bar or their tab to dock/undock." "\n"
                "- Drag from window menu button (upper-left button) to undock an entire node (all windows)." "\n"
                "- Hold SHIFT to disable docking (if io.ConfigDockingWithShift == false, default)" "\n"
                "- Hold SHIFT to enable docking (if io.ConfigDockingWithShift == true)");
            ImGui::Separator();
            ImGui::TextUnformatted("More details:"); ImGui::Bullet(); ImGui::SameLine(); ImGui::TextLinkOpenURL("Docking Wiki page", "https://github.com/ocornut/imgui/wiki/Docking");
            ImGui::BulletText("Read comments in ShowExampleAppDockSpace()");
            ImGui::EndMenu();
        }
        ImGui::EndMenuBar();
    }

    ImGui::End();
}

//-----------------------------------------------------------------------------
// [SECTION] Example App: Documents Handling / ShowExampleAppDocuments()
//-----------------------------------------------------------------------------

// Simplified structure to mimic a Document model
struct MyDocument
{
    char        Name[32];   // Document title
    int         UID;        // Unique ID (necessary as we can change title)
    bool        Open;       // Set when open (we keep an array of all available documents to simplify demo code!)
    bool        OpenPrev;   // Copy of Open from last update.
    bool        Dirty;      // Set when the document has been modified
    ImVec4      Color;      // An arbitrary variable associated to the document

    MyDocument(int uid, const char* name, bool open = true, const ImVec4& color = ImVec4(1.0f, 1.0f, 1.0f, 1.0f))
    {
        UID = uid;
        snprintf(Name, sizeof(Name), "%s", name);
        Open = OpenPrev = open;
        Dirty = false;
        Color = color;
    }
    void DoOpen()       { Open = true; }
    void DoForceClose() { Open = false; Dirty = false; }
    void DoSave()       { Dirty = false; }
};

struct ExampleAppDocuments
{
    ImVector<MyDocument>    Documents;
    ImVector<MyDocument*>   CloseQueue;
    MyDocument*             RenamingDoc = NULL;
    bool                    RenamingStarted = false;

    ExampleAppDocuments()
    {
        Documents.push_back(MyDocument(0, "Lettuce",             true,  ImVec4(0.4f, 0.8f, 0.4f, 1.0f)));
        Documents.push_back(MyDocument(1, "Eggplant",            true,  ImVec4(0.8f, 0.5f, 1.0f, 1.0f)));
        Documents.push_back(MyDocument(2, "Carrot",              true,  ImVec4(1.0f, 0.8f, 0.5f, 1.0f)));
        Documents.push_back(MyDocument(3, "Tomato",              false, ImVec4(1.0f, 0.3f, 0.4f, 1.0f)));
        Documents.push_back(MyDocument(4, "A Rather Long Title", false, ImVec4(0.4f, 0.8f, 0.8f, 1.0f)));
        Documents.push_back(MyDocument(5, "Some Document",       false, ImVec4(0.8f, 0.8f, 1.0f, 1.0f)));
    }

    // As we allow to change document name, we append a never-changing document ID so tabs are stable
    void GetTabName(MyDocument* doc, char* out_buf, size_t out_buf_size)
    {
        snprintf(out_buf, out_buf_size, "%s###doc%d", doc->Name, doc->UID);
    }

    // Display placeholder contents for the Document
    void DisplayDocContents(MyDocument* doc)
    {
        ImGui::PushID(doc);
        ImGui::Text("Document \"%s\"", doc->Name);
        ImGui::PushStyleColor(ImGuiCol_Text, doc->Color);
        ImGui::TextWrapped("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.");
        ImGui::PopStyleColor();

        ImGui::SetNextItemShortcut(ImGuiMod_Ctrl | ImGuiKey_R, ImGuiInputFlags_Tooltip);
        if (ImGui::Button("Rename.."))
        {
            RenamingDoc = doc;
            RenamingStarted = true;
        }
        ImGui::SameLine();

        ImGui::SetNextItemShortcut(ImGuiMod_Ctrl | ImGuiKey_M, ImGuiInputFlags_Tooltip);
        if (ImGui::Button("Modify"))
            doc->Dirty = true;

        ImGui::SameLine();
        ImGui::SetNextItemShortcut(ImGuiMod_Ctrl | ImGuiKey_S, ImGuiInputFlags_Tooltip);
        if (ImGui::Button("Save"))
            doc->DoSave();

        ImGui::SameLine();
        ImGui::SetNextItemShortcut(ImGuiMod_Ctrl | ImGuiKey_W, ImGuiInputFlags_Tooltip);
        if (ImGui::Button("Close"))
            CloseQueue.push_back(doc);
        ImGui::ColorEdit3("color", &doc->Color.x);  // Useful to test drag and drop and hold-dragged-to-open-tab behavior.
        ImGui::PopID();
    }

    // Display context menu for the Document
    void DisplayDocContextMenu(MyDocument* doc)
    {
        if (!ImGui::BeginPopupContextItem())
            return;

        char buf[256];
        sprintf(buf, "Save %s", doc->Name);
        if (ImGui::MenuItem(buf, "Ctrl+S", false, doc->Open))
            doc->DoSave();
        if (ImGui::MenuItem("Rename...", "Ctrl+R", false, doc->Open))
            RenamingDoc = doc;
        if (ImGui::MenuItem("Close", "Ctrl+W", false, doc->Open))
            CloseQueue.push_back(doc);
        ImGui::EndPopup();
    }

    // [Optional] Notify the system of Tabs/Windows closure that happened outside the regular tab interface.
    // If a tab has been closed programmatically (aka closed from another source such as the Checkbox() in the demo,
    // as opposed to clicking on the regular tab closing button) and stops being submitted, it will take a frame for
    // the tab bar to notice its absence. During this frame there will be a gap in the tab bar, and if the tab that has
    // disappeared was the selected one, the tab bar will report no selected tab during the frame. This will effectively
    // give the impression of a flicker for one frame.
    // We call SetTabItemClosed() to manually notify the Tab Bar or Docking system of removed tabs to avoid this glitch.
    // Note that this completely optional, and only affect tab bars with the ImGuiTabBarFlags_Reorderable flag.
    void NotifyOfDocumentsClosedElsewhere()
    {
        for (MyDocument& doc : Documents)
        {
            if (!doc.Open && doc.OpenPrev)
                ImGui::SetTabItemClosed(doc.Name);
            doc.OpenPrev = doc.Open;
        }
    }
};

void ShowExampleAppDocuments(bool* p_open)
{
    static ExampleAppDocuments app;

    // Options
    enum Target
    {
        Target_None,
        Target_Tab,                 // Create documents as local tab into a local tab bar
        Target_DockSpaceAndWindow   // Create documents as regular windows, and create an embedded dockspace
    };
    static Target opt_target = Target_Tab;
    static bool opt_reorderable = true;
    static ImGuiTabBarFlags opt_fitting_flags = ImGuiTabBarFlags_FittingPolicyDefault_;

    // When (opt_target == Target_DockSpaceAndWindow) there is the possibily that one of our child Document window (e.g. "Eggplant")
    // that we emit gets docked into the same spot as the parent window ("Example: Documents").
    // This would create a problematic feedback loop because selecting the "Eggplant" tab would make the "Example: Documents" tab
    // not visible, which in turn would stop submitting the "Eggplant" window.
    // We avoid this problem by submitting our documents window even if our parent window is not currently visible.
    // Another solution may be to make the "Example: Documents" window use the ImGuiWindowFlags_NoDocking.

    bool window_contents_visible = ImGui::Begin("Example: Documents", p_open, ImGuiWindowFlags_MenuBar);
    if (!window_contents_visible && opt_target != Target_DockSpaceAndWindow)
    {
        ImGui::End();
        return;
    }
```

