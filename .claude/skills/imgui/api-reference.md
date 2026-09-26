# BRUTAL ImGui — API Reference (self-contained)

Transcribed from the BRUTAL `Brutal.ImGuiApi` sources so the skill needs no external
files. The API mirrors upstream Dear ImGui v1.92.x (docking) with C# conventions.
Namespaces: `Brutal.ImGuiApi` (core `ImGui` + types), `Brutal.ImGuiApi.Extensions`
(`ImGuiEx`, `ImDrawList` `Add*`), `Brutal.ImGuiApi.Abstractions` (`ImGuiUtils`).
Numeric types (`float2/3/4`, `int2/3/4`, `byte4`) live in `Brutal.Numerics`.

---

## Core types

### `ImString` (ref struct, `[InterpolatedStringHandler]`) — all display strings
Implicit conversions in: `ReadOnlySpan<byte>` / `Span<byte>` (e.g. `"x"u8`, no copy),
`ReadOnlySpan<char>`/`Span<char>`, `string` (copies into shared buffer), `String8`,
`RefString8`, `LString`, `Ptr<byte>`. Implicit out: `string` (allocates via `ToString`),
`ReadOnlySpan<byte>`.
Members: `static ImString Empty`, `static ImString Null`, `bool IsEmpty`, `bool IsUnset`,
`int Length`, `ReadOnlySpan<byte> AsSpan()`, `string ToString()`.
- Backed by a single 8 MB `SharedStorage` ring buffer reset every frame → **never cache a
  built `ImString`/interpolation across frames**. `"x"u8` literals are static and always safe.
- Passing an interpolated `$"..."` directly to an `ImString` parameter uses the handler →
  no intermediate `string` allocation. Storing in a `string` first defeats this.

### `ImInputString` (class) — editable text buffers (keep as a field)
- `ImInputString(int capacity, ReadOnlySpan<byte> initial = default)` — capacity includes the
  null terminator (so `new(128)` → 127 usable bytes).
- `ImInputString(int capacity, ImString initial)`
- Members: `byte[] Buffer`, `int Length`, `int Capacity`, `bool IsEmpty`,
  `ReadOnlySpan<byte> ValueSpan`, `ImString Value`, set-only `Value8`/`Value16`,
  `void SetValue(...)`, `void Clear()`, `void EvaluateLength()`, `string ToString()`.
- Implicit conversion to `ImString`.
- Extension: `bool IsNullOrEmpty(this ImInputString?)`.

### `ImColor8` (struct) — packed 32-bit RGBA (upstream `ImU32`)
- `ImColor8(byte r, byte g, byte b, byte a = 255)`
- Implicit in: `uint`, `byte4`, `ImGuiCol` (resolves the *current style color*), `Color.Preset`.
- **Explicit** in: `float4` → `(ImColor8)myFloat4`.
- Implicit out: `uint`.
- Members: `uint AsUint()`, `byte4 AsByte4()`, `float4 AsFloat4()`, props `RGBA`/`ARGB`.
- Statics: `ImColor8.Black / White / Red / Green / Blue`.

---

## Enums (member names; all are upstream-equivalent)

`[Flags]` enums all start with `None = 0`; combine with `|`. Mask/`COUNT` members omitted.

- `ImGuiWindowFlags`: NoTitleBar, NoResize, NoMove, NoScrollbar, NoScrollWithMouse, NoCollapse, AlwaysAutoResize, NoBackground, NoSavedSettings, NoMouseInputs, MenuBar, HorizontalScrollbar, NoFocusOnAppearing, NoBringToFrontOnFocus, AlwaysVerticalScrollbar, AlwaysHorizontalScrollbar, NoNavInputs, NoNavFocus, UnsavedDocument, NoDocking, NoNav, NoDecoration, NoInputs
- `ImGuiChildFlags`: Borders, AlwaysUseWindowPadding, ResizeX, ResizeY, AutoResizeX, AutoResizeY, AlwaysAutoResize, FrameStyle, NavFlattened
- `ImGuiCond` (not flags): Always, Once, FirstUseEver, Appearing
- `ImGuiDir` (not flags): Left, Right, Up, Down
- `ImGuiTreeNodeFlags`: Selected, Framed, AllowOverlap, NoTreePushOnOpen, NoAutoOpenOnLog, DefaultOpen, OpenOnDoubleClick, OpenOnArrow, Leaf, Bullet, FramePadding, SpanAvailWidth, SpanFullWidth, SpanLabelWidth, SpanAllColumns, LabelSpanAllColumns, NavLeftJumpsToParent, CollapsingHeader, DrawLinesNone, DrawLinesFull, DrawLinesToNodes
- `ImGuiTableFlags`: Resizable, Reorderable, Hideable, Sortable, NoSavedSettings, ContextMenuInBody, RowBg, BordersInnerH, BordersOuterH, BordersInnerV, BordersOuterV, BordersH, BordersV, BordersInner, BordersOuter, Borders, NoBordersInBody, NoBordersInBodyUntilResize, SizingFixedFit, SizingFixedSame, SizingStretchProp, SizingStretchSame, NoHostExtendX, NoHostExtendY, NoKeepColumnsVisible, PreciseWidths, NoClip, PadOuterX, NoPadOuterX, NoPadInnerX, ScrollX, ScrollY, SortMulti, SortTristate, HighlightHoveredColumn
- `ImGuiTableColumnFlags`: Disabled, DefaultHide, DefaultSort, WidthStretch, WidthFixed, NoResize, NoReorder, NoHide, NoClip, NoSort, NoSortAscending, NoSortDescending, NoHeaderLabel, NoHeaderWidth, PreferSortAscending, PreferSortDescending, IndentEnable, IndentDisable, AngledHeader, IsEnabled, IsVisible, IsSorted, IsHovered, NoDirectResize
- `ImGuiTableRowFlags`: Headers
- `ImGuiColorEditFlags`: NoAlpha, NoPicker, NoOptions, NoSmallPreview, NoInputs, NoTooltip, NoLabel, NoSidePreview, NoDragDrop, NoBorder, AlphaOpaque, AlphaNoBg, AlphaPreviewHalf, AlphaBar, HDR, DisplayRGB, DisplayHSV, DisplayHex, Uint8, Float, PickerHueBar, PickerHueWheel, InputRGB, InputHSV, DefaultOptions
- `ImGuiComboFlags`: PopupAlignLeft, HeightSmall, HeightRegular, HeightLarge, HeightLargest, NoArrowButton, NoPreview, WidthFitPreview
- `ImGuiSelectableFlags`: NoAutoClosePopups, SpanAllColumns, AllowDoubleClick, Disabled, AllowOverlap, Highlight
- `ImGuiInputTextFlags`: CharsDecimal, CharsHexadecimal, CharsScientific, CharsUppercase, CharsNoBlank, AllowTabInput, EnterReturnsTrue, EscapeClearsAll, CtrlEnterForNewLine, ReadOnly, Password, AlwaysOverwrite, AutoSelectAll, ParseEmptyRefVal, DisplayEmptyRefVal, NoHorizontalScroll, NoUndoRedo, ElideLeft, CallbackCompletion, CallbackHistory, CallbackAlways, CallbackCharFilter, CallbackResize, CallbackEdit
- `ImGuiSliderFlags`: Logarithmic, NoRoundToFormat, NoInput, WrapAround, ClampOnInput, ClampZeroRange, NoSpeedTweaks, AlwaysClamp
- `ImGuiTabBarFlags`: Reorderable, AutoSelectNewTabs, TabListPopupButton, NoCloseWithMiddleMouseButton, NoTabListScrollingButtons, NoTooltip, DrawSelectedOverline, FittingPolicyMixed, FittingPolicyShrink, FittingPolicyScroll, FittingPolicyDefault
- `ImGuiTabItemFlags`: UnsavedDocument, SetSelected, NoCloseWithMiddleMouseButton, NoPushId, NoTooltip, NoReorder, Leading, Trailing, NoAssumedClosure
- `ImGuiPopupFlags`: MouseButtonLeft, MouseButtonRight, MouseButtonMiddle, MouseButtonDefault, NoReopen, NoOpenOverExistingPopup, NoOpenOverItems, AnyPopupId, AnyPopupLevel, AnyPopup
- `ImGuiHoveredFlags`: ChildWindows, RootWindow, AnyWindow, NoPopupHierarchy, DockHierarchy, AllowWhenBlockedByPopup, AllowWhenBlockedByActiveItem, AllowWhenOverlappedByItem, AllowWhenOverlappedByWindow, AllowWhenDisabled, NoNavOverride, AllowWhenOverlapped, RectOnly, RootAndChildWindows, ForTooltip, Stationary, DelayNone, DelayShort, DelayNormal, NoSharedDelay
- `ImGuiFocusedFlags`: ChildWindows, RootWindow, AnyWindow, NoPopupHierarchy, DockHierarchy, RootAndChildWindows
- `ImGuiButtonFlags`: MouseButtonLeft, MouseButtonRight, MouseButtonMiddle, EnableNav
- `ImGuiDragDropFlags`: SourceNoPreviewTooltip, SourceNoDisableHover, SourceNoHoldToOpenOthers, SourceAllowNullID, SourceExtern, PayloadAutoExpire, PayloadNoCrossContext, PayloadNoCrossProcess, AcceptBeforeDelivery, AcceptNoDrawDefaultRect, AcceptNoPreviewTooltip, AcceptPeekOnly
- `ImGuiDataType` (not flags): S8, U8, S16, U16, S32, U32, S64, U64, Float, Double, Bool, String
- `ImGuiMouseButton` (not flags): Left, Right, Middle
- `ImGuiStyleVar` (not flags): Alpha, DisabledAlpha, WindowPadding, WindowRounding, WindowBorderSize, WindowMinSize, WindowTitleAlign, ChildRounding, ChildBorderSize, PopupRounding, PopupBorderSize, FramePadding, FrameRounding, FrameBorderSize, ItemSpacing, ItemInnerSpacing, IndentSpacing, CellPadding, ScrollbarSize, ScrollbarRounding, GrabMinSize, GrabRounding, ImageBorderSize, TabRounding, TabBorderSize, TabBarBorderSize, TabBarOverlineSize, ButtonTextAlign, SelectableTextAlign, SeparatorTextBorderSize, SeparatorTextAlign, SeparatorTextPadding, DockingSeparatorSize  *(`PushStyleVar` takes `float` for scalar vars or `float2` for the 2-component ones; also `PushStyleVarX`/`PushStyleVarY`)*
- `ImGuiCol` (not flags): Text, TextDisabled, WindowBg, ChildBg, PopupBg, Border, BorderShadow, FrameBg, FrameBgHovered, FrameBgActive, TitleBg, TitleBgActive, TitleBgCollapsed, MenuBarBg, ScrollbarBg, ScrollbarGrab, ScrollbarGrabHovered, ScrollbarGrabActive, CheckMark, SliderGrab, SliderGrabActive, Button, ButtonHovered, ButtonActive, Header, HeaderHovered, HeaderActive, Separator, SeparatorHovered, SeparatorActive, ResizeGrip, ResizeGripHovered, ResizeGripActive, InputTextCursor, TabHovered, Tab, TabSelected, TabSelectedOverline, TabDimmed, TabDimmedSelected, TabDimmedSelectedOverline, DockingPreview, DockingEmptyBg, PlotLines, PlotLinesHovered, PlotHistogram, PlotHistogramHovered, TableHeaderBg, TableBorderStrong, TableBorderLight, TableRowBg, TableRowBgAlt, TextLink, TextSelectedBg, TreeLines, DragDropTarget, NavCursor, NavWindowingHighlight, NavWindowingDimBg, ModalWindowDimBg
- `ImGuiKey`: full upstream key set — `A`..`Z`, `_0`..`_9` (digit keys are prefixed with `_`), `F1`..`F24`, `Keypad0`..`Keypad9`, arrows (`LeftArrow`/`RightArrow`/`UpArrow`/`DownArrow`), `Space`, `Enter`, `Escape`, `Backspace`, `Tab`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, modifiers (`LeftCtrl`/`RightCtrl`/`LeftShift`/`RightShift`/`LeftAlt`/`RightAlt`/`LeftSuper`/`RightSuper`), combined mods (`ModCtrl`/`ModShift`/`ModAlt`/`ModSuper`), mouse (`MouseLeft`/`MouseRight`/`MouseMiddle`/`MouseWheelX`/`MouseWheelY`), gamepad, etc. Use a `ImGuiKeyChord` (e.g. `ImGuiKey.ModCtrl | ImGuiKey.S`) for `Shortcut`/`IsKeyChordPressed`.

---

## Extension helpers

### `ImGuiEx` (`using Brutal.ImGuiApi.Extensions;`)
```csharp
// Enum-typed wrappers (TEnum must be int-sized):
bool CheckboxFlags<T>(ImString label, ref T flags, T flagsValue) where T : Enum
bool Combo<T>(ImString label, ref T currentItem, ReadOnlySpan<Ptr<byte>> items, int popupMaxHeightInItems = -1) where T : Enum
bool Combo<T>(ImString label, ref T currentItem, RefString8Array items, int popupMaxHeightInItems = -1) where T : Enum   // string[] converts implicitly
bool Combo<T>(ImString label, ref T currentItem, ImString itemsSeparatedByZeros, int popupMaxHeightInItems = -1) where T : Enum
bool RadioButton<T>(ImString label, ref T v, T vButton) where T : Enum
// float4 color helpers (built-ins only take ref float3):
bool ColorEdit3(ImString label, ref float4 col, ImGuiColorEditFlags flags = None)
bool ColorPicker3(ImString label, ref float4 col, ImGuiColorEditFlags flags = None)
float4 ColorConvertHSVtoRGB(float4 hsv)
// Generic scalar widgets for any unmanaged numeric T (+ Span<T> "N" variants):
bool DragScalar<T>(ImString label, ImGuiDataType dt, ref T v, float vSpeed = 1f, T? min = null, T? max = null, ImString format = default, ImGuiSliderFlags flags = None) where T : unmanaged
bool SliderScalar<T>(ImString label, ImGuiDataType dt, ref T v, T min, T max, ImString format = default, ImGuiSliderFlags flags = None) where T : unmanaged
bool InputScalar<T>(ImString label, ImGuiDataType dt, ref T v, T? step = null, T? stepFast = null, ImString format = default, ImGuiInputTextFlags flags = None) where T : unmanaged
```

### `ImGuiUtils` (`using Brutal.ImGuiApi.Abstractions;`)
```csharp
void SetLastFocusOnAppearing(bool force = false)
void TextShadow(ImString text, ImColor8 color)
```

### `ImDrawListPtr` extensions (`using Brutal.ImGuiApi.Extensions;`) — colors are `ImColor8`
Obtain a list via `ImGui.GetWindowDrawList()` / `GetForegroundDrawList()` / `GetBackgroundDrawList()`.
```csharp
void AddLine(in float2 p1, in float2 p2, ImColor8 col, float thickness = 1f)
void AddRect(in float2 pMin, in float2 pMax, ImColor8 col, float rounding = 0f, ImDrawFlags flags = None, float thickness = 1f)
void AddRectFilled(in float2 pMin, in float2 pMax, ImColor8 col, float rounding = 0f, ImDrawFlags flags = None)
void AddRectFilledMultiColor(in float2 pMin, in float2 pMax, uint colUL, uint colUR, uint colBR, uint colBL)
void AddQuad / AddQuadFilled(in float2 p1..p4, ImColor8 col, [float thickness = 1f])
void AddTriangle / AddTriangleFilled(in float2 p1..p3, ImColor8 col, [float thickness = 1f])
void AddCircle / AddCircleFilled(in float2 center, float radius, ImColor8 col, int numSegments = 0, [float thickness = 1f])
void AddNgon / AddNgonFilled(in float2 center, float radius, ImColor8 col, int numSegments, [float thickness = 1f])
void AddEllipse / AddEllipseFilled(in float2 center, in float2 radius, ImColor8 col, float rot = 0f, int numSegments = 0, [float thickness = 1f])
void AddText(in float2 pos, ImColor8 col, ImString text)
void AddText(ImFontPtr font, float fontSize, in float2 pos, ImColor8 col, ImString text, float wrapWidth = 0f, in float4? cpuFineClipRect = null)
void AddBezierCubic(in float2 p1..p4, ImColor8 col, float thickness, int numSegments = 0)
void AddBezierQuadratic(in float2 p1..p3, ImColor8 col, float thickness, int numSegments = 0)
void AddPolyline(ReadOnlySpan<float2> points, ImColor8 col, ImDrawFlags flags, float thickness)
void AddConvexPolyFilled / AddConcavePolyFilled(ReadOnlySpan<float2> points, ImColor8 col)
void AddImage(ImTextureRef tex, in float2 pMin, in float2 pMax, in float2? uvMin = null, in float2? uvMax = null, ImColor8? col = null)
void AddImageRounded(ImTextureRef tex, in float2 pMin, in float2 pMax, in float2 uvMin, in float2 uvMax, ImColor8 col, float rounding, ImDrawFlags flags = None)
void AddImageQuad(ImTextureRef tex, in float2 p1..p4, in float2? uv1..uv4 = null, ImColor8? col = null)
```

---

## Full `ImGui` widget signature index

Every public managed method below is a static member of `ImGui` (call as `ImGui.X(...)`).
`unsafe`/`[DefaultValue]` attributes stripped for readability. `Ptr` = `Brutal.Pointers.Ptr`.
Advanced internals are under `ImGui.Internal.*`; raw bindings under `ImGui.PInvoke.*`.

### Context & frame
```csharp
	public static ImGuiContextPtr CreateContext(ImFontAtlasPtr sharedFontAtlas = default(ImFontAtlasPtr))
	public static void DestroyContext(ImGuiContextPtr ctx = default(ImGuiContextPtr))
	public static ImGuiContextPtr GetCurrentContext()
	public static void SetCurrentContext(ImGuiContextPtr ctx)
	public static void NewFrame()
	public static void EndFrame()
	public static void Render()
	public static ImDrawDataPtr GetDrawData()
	public static void ShowUserGuide()
	public static void UpdatePlatformWindows()
	public static void RenderPlatformWindowsDefault(Ptr platformRenderArg = default(Ptr), Ptr rendererRenderArg = default(Ptr))
	public static void DestroyPlatformWindows()
```

### Windows
```csharp
	public static bool Begin(ImString name, ref bool pOpen, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
	public static bool Begin(ImString name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
	public static void End()
	public static bool BeginChild(ImString strId, in float2? size = null, ImGuiChildFlags childFlags = ImGuiChildFlags.None, ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None)
	public static bool BeginChild(ImGuiID id, in float2? size = null, ImGuiChildFlags childFlags = ImGuiChildFlags.None, ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None)
	public static void EndChild()
	public static bool IsWindowAppearing()
	public static bool IsWindowCollapsed()
	public static bool IsWindowFocused(ImGuiFocusedFlags flags = ImGuiFocusedFlags.None)
	public static bool IsWindowHovered(ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
	public static ImDrawListPtr GetWindowDrawList()
	public static float GetWindowDpiScale()
	public static float2 GetWindowPos()
	public static float2 GetWindowSize()
	public static float GetWindowWidth()
	public static float GetWindowHeight()
	public static void SetNextWindowPos(in float2 pos, ImGuiCond cond = ImGuiCond.None, in float2? pivot = null)
	public static void SetNextWindowSize(in float2 size, ImGuiCond cond = ImGuiCond.None)
	public static void SetNextWindowSizeConstraints(in float2 sizeMin, in float2 sizeMax, ImGuiSizeCallback? customCallback = null, Ptr customCallbackData = default(Ptr))
	public static void SetNextWindowContentSize(in float2 size)
	public static void SetNextWindowCollapsed(bool collapsed, ImGuiCond cond = ImGuiCond.None)
	public static void SetNextWindowFocus()
	public static void SetNextWindowScroll(in float2 scroll)
	public static void SetNextWindowBgAlpha(float alpha)
	public static void SetNextWindowViewport(ImGuiID viewportId)
	public static void SetWindowPos(in float2 pos, ImGuiCond cond = ImGuiCond.None)
	public static void SetWindowSize(in float2 size, ImGuiCond cond = ImGuiCond.None)
	public static void SetWindowCollapsed(bool collapsed, ImGuiCond cond = ImGuiCond.None)
	public static void SetWindowFocus()
	public static void SetWindowPos(ImString name, in float2 pos, ImGuiCond cond = ImGuiCond.None)
	public static void SetWindowSize(ImString name, in float2 size, ImGuiCond cond = ImGuiCond.None)
	public static void SetWindowCollapsed(ImString name, bool collapsed, ImGuiCond cond = ImGuiCond.None)
	public static void SetWindowFocus(ImString name)
	public static void SetNextWindowDockID(ImGuiID dockId, ImGuiCond cond = ImGuiCond.None)
	public static void SetNextWindowClass(ImGuiWindowClassPtr windowClass)
	public static ImGuiID GetWindowDockID()
	public static bool IsWindowDocked()
```

### Scrolling
```csharp
	public static float GetScrollX()
	public static float GetScrollY()
	public static void SetScrollX(float scrollX)
	public static void SetScrollY(float scrollY)
	public static float GetScrollMaxX()
	public static float GetScrollMaxY()
	public static void SetScrollHereX(float centerXRatio = 0.5f)
	public static void SetScrollHereY(float centerYRatio = 0.5f)
	public static void SetScrollFromPosX(float localX, float centerXRatio = 0.5f)
	public static void SetScrollFromPosY(float localY, float centerYRatio = 0.5f)
	public static void TableSetupScrollFreeze(int cols, int rows)
```

### Child / content region
```csharp
	public static float2 GetCursorScreenPos()
	public static void SetCursorScreenPos(in float2 pos)
	public static float2 GetContentRegionAvail()
	public static float2 GetCursorPos()
	public static float GetCursorPosX()
	public static float GetCursorPosY()
	public static void SetCursorPos(in float2 localPos)
	public static void SetCursorPosX(float localX)
	public static void SetCursorPosY(float localY)
	public static float2 GetCursorStartPos()
```

### Fonts & style
```csharp
	public static ImGuiStylePtr GetStyle()
	public static void StyleColorsDark(ImGuiStylePtr dst = default(ImGuiStylePtr))
	public static void StyleColorsLight(ImGuiStylePtr dst = default(ImGuiStylePtr))
	public static void StyleColorsClassic(ImGuiStylePtr dst = default(ImGuiStylePtr))
	public static void PushFont(ImFontPtr font, float fontSizeBaseUnscaled)
	public static void PopFont()
	public static ImFontPtr GetFont()
	public static float GetFontSize()
	public static ImFontBakedPtr GetFontBaked()
	public static void PushStyleColor(ImGuiCol idx, ImColor8 col)
	public static void PushStyleColor(ImGuiCol idx, in float4 col)
	public static void PopStyleColor(int count = 1)
	public static void PushStyleVar(ImGuiStyleVar idx, float val)
	public static void PushStyleVar(ImGuiStyleVar idx, in float2 val)
	public static void PushStyleVarX(ImGuiStyleVar idx, float valX)
	public static void PushStyleVarY(ImGuiStyleVar idx, float valY)
	public static void PopStyleVar(int count = 1)
	public static float2 GetFontTexUvWhitePixel()
	public static ImColor8 GetColorU32(ImGuiCol idx, float alphaMul = 1f)
	public static ImColor8 GetColorU32(in float4 col)
	public static ImColor8 GetColorU32(ImColor8 col, float alphaMul = 1f)
	public static float4 GetStyleColorVec4(ImGuiCol idx)
	public static ImString GetStyleColorName(ImGuiCol idx)
```

### Layout & spacing
```csharp
	public static void PushItemFlag(ImGuiItemFlags option, bool enabled)
	public static void PopItemFlag()
	public static void PushItemWidth(float itemWidth)
	public static void PopItemWidth()
	public static void SetNextItemWidth(float itemWidth)
	public static float CalcItemWidth()
	public static void PushTextWrapPos(float wrapLocalPosX = 0f)
	public static void PopTextWrapPos()
	public static void Separator()
	public static void SameLine(float offsetFromStartX = 0f, float spacing = -1f)
	public static void NewLine()
	public static void Spacing()
	public static void Dummy(in float2 size)
	public static void Indent(float indentW = 0f)
	public static void Unindent(float indentW = 0f)
	public static void BeginGroup()
	public static void EndGroup()
	public static void AlignTextToFramePadding()
	public static float GetTextLineHeight()
	public static float GetTextLineHeightWithSpacing()
	public static float GetFrameHeight()
	public static float GetFrameHeightWithSpacing()
	public static void SeparatorText(ImString label)
	public static float GetTreeNodeToLabelSpacing()
```

### ID stack
```csharp
	public static void PushID(ImString strId)
	public static void PushID(nint ptrId)
	public static void PushID(int intId)
	public static void PopID()
	public static ImGuiID GetID(ImString strId)
	public static ImGuiID GetID(nint ptrId)
	public static ImGuiID GetID(int intId)
```

### Text widgets
```csharp
	public static void TextColored(in float4 col, ImString text)
	public static void TextDisabled(ImString text)
	public static void TextWrapped(ImString text)
	public static void LabelText(ImString label, ImString text)
	public static void BulletText(ImString text)
	public static void Bullet()
	public static bool TextLink(ImString label)
	public static bool TextLinkOpenURL(ImString label, ImString url = default(ImString))
	public static float2 CalcTextSize(ImString text, bool hideTextAfterDoubleHash = false, float wrapWidth = -1f)
	public static void Text(ImString text)
```

### Buttons / checkbox / radio
```csharp
	public static bool Button(ImString label, in float2? size = null)
	public static bool SmallButton(ImString label)
	public static bool InvisibleButton(ImString strId, in float2 size, ImGuiButtonFlags flags = ImGuiButtonFlags.None)
	public static bool ArrowButton(ImString strId, ImGuiDir dir)
	public static bool Checkbox(ImString label, ref bool v)
	public static bool CheckboxFlags(ImString label, ref int flags, int flagsValue)
	public static bool CheckboxFlags(ImString label, ref uint flags, uint flagsValue)
	public static bool RadioButton(ImString label, bool active)
	public static bool RadioButton(ImString label, ref int v, int vButton)
	public static void ProgressBar(float fraction, in float2? sizeArg = null, ImString overlay = default(ImString))
	public static void Image(ImTextureRef texRef, in float2 imageSize, in float2? uv0 = null, in float2? uv1 = null)
	public static void ImageWithBg(ImTextureRef texRef, in float2 imageSize, in float2? uv0 = null, in float2? uv1 = null, in float4? bgCol = null, in float4? tintCol = null)
	public static bool ImageButton(ImString strId, ImTextureRef texRef, in float2 imageSize, in float2? uv0 = null, in float2? uv1 = null, in float4? bgCol = null, in float4? tintCol = null)
	public static bool ColorButton(ImString descId, in float4 col, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None, in float2? size = null)
	public static void OpenPopupOnItemClick(ImString strId = default(ImString), ImGuiPopupFlags popupFlags = ImGuiPopupFlags.MouseButtonRight)
	public static bool BeginPopupContextItem(ImString strId = default(ImString), ImGuiPopupFlags popupFlags = ImGuiPopupFlags.MouseButtonRight)
	public static bool BeginPopupContextWindow(ImString strId = default(ImString), ImGuiPopupFlags popupFlags = ImGuiPopupFlags.MouseButtonRight)
	public static bool BeginPopupContextVoid(ImString strId = default(ImString), ImGuiPopupFlags popupFlags = ImGuiPopupFlags.MouseButtonRight)
	public static bool TabItemButton(ImString label, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
	public static void LogButtons()
	public static bool IsItemClicked(ImGuiMouseButton mouseButton = ImGuiMouseButton.Left)
	public static bool IsMouseDown(ImGuiMouseButton button)
	public static bool IsMouseClicked(ImGuiMouseButton button, bool repeat = false)
	public static bool IsMouseReleased(ImGuiMouseButton button)
	public static bool IsMouseDoubleClicked(ImGuiMouseButton button)
	public static bool IsMouseReleasedWithDelay(ImGuiMouseButton button, float delay)
	public static int GetMouseClickedCount(ImGuiMouseButton button)
	public static bool IsMouseDragging(ImGuiMouseButton button, float lockThreshold = -1f)
	public static float2 GetMouseDragDelta(ImGuiMouseButton button = ImGuiMouseButton.Left, float lockThreshold = -1f)
	public static void ResetMouseDragDelta(ImGuiMouseButton button = ImGuiMouseButton.Left)
```

### Combo / listbox
```csharp
	public static bool BeginCombo(ImString label, ImString previewValue = default(ImString), ImGuiComboFlags flags = ImGuiComboFlags.None)
	public static void EndCombo()
	public static bool Combo(ImString label, ref int currentItem, ImString itemsSeparatedByZeros, int popupMaxHeightInItems = -1)
	public static bool BeginListBox(ImString label, in float2? size = null)
	public static void EndListBox()
	public static bool Combo(ImString label, ref int currentItem, ReadOnlySpan<Ptr<byte>> items, int popupMaxHeightInItems = -1)
	public static bool Combo(ImString label, ref int currentItem, scoped RefString8Array items, int popupMaxHeightInItems = -1)
	public static bool ListBox(ImString label, ref int currentItem, ReadOnlySpan<Ptr<byte>> items, int heightInItems = -1)
	public static bool ListBox(ImString label, ref int currentItem, scoped RefString8Array items, int heightInItems = -1)
	public static bool Combo(ImString label, ref int currentItem, ComboGetter getter, nint userData, int itemsCount, int popupMaxHeightInItems = -1)
	public static bool ListBox(ImString label, ref int currentItem, ListBoxGetter getter, nint userData, int itemsCount, int heightInItems = -1)
```

### Drag widgets
```csharp
	public static bool DragFloat(ImString label, ref float v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragFloat2(ImString label, ref float2 v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragFloat3(ImString label, ref float3 v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragFloat4(ImString label, ref float4 v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragFloatRange2(ImString label, ref float vCurrentMin, ref float vCurrentMax, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, ImString format = default(ImString), ImString formatMax = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragInt(ImString label, ref int v, float vSpeed = 1f, int vMin = 0, int vMax = 0, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragInt2(ImString label, ref int2 v, float vSpeed = 1f, int vMin = 0, int vMax = 0, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragInt3(ImString label, ref int3 v, float vSpeed = 1f, int vMin = 0, int vMax = 0, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragInt4(ImString label, ref int4 v, float vSpeed = 1f, int vMin = 0, int vMax = 0, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragIntRange2(ImString label, ref int vCurrentMin, ref int vCurrentMax, float vSpeed = 1f, int vMin = 0, int vMax = 0, ImString format = default(ImString), ImString formatMax = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragScalar(ImString label, ImGuiDataType dataType, Ptr pData, float vSpeed = 1f, Ptr pMin = default(Ptr), Ptr pMax = default(Ptr), ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool DragScalarN(ImString label, ImGuiDataType dataType, Ptr pData, int components, float vSpeed = 1f, Ptr pMin = default(Ptr), Ptr pMax = default(Ptr), ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool BeginDragDropSource(ImGuiDragDropFlags flags = ImGuiDragDropFlags.None)
	public static bool SetDragDropPayload(ImString type, Ptr data, nuint sz, ImGuiCond cond = ImGuiCond.None)
	public static void EndDragDropSource()
	public static bool BeginDragDropTarget()
	public static ImGuiPayloadPtr AcceptDragDropPayload(ImString type, ImGuiDragDropFlags flags = ImGuiDragDropFlags.None)
	public static void EndDragDropTarget()
	public static ImGuiPayloadPtr GetDragDropPayload()
```

### Slider widgets
```csharp
	public static bool SliderFloat(ImString label, ref float v, float vMin, float vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderFloat2(ImString label, ref float2 v, float vMin, float vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderFloat3(ImString label, ref float3 v, float vMin, float vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderFloat4(ImString label, ref float4 v, float vMin, float vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderAngle(ImString label, ref float vRad, float vDegreesMin = -360f, float vDegreesMax = 360f, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderInt(ImString label, ref int v, int vMin, int vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderInt2(ImString label, ref int2 v, int vMin, int vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderInt3(ImString label, ref int3 v, int vMin, int vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderInt4(ImString label, ref int4 v, int vMin, int vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderScalar(ImString label, ImGuiDataType dataType, Ptr pData, Ptr pMin, Ptr pMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool SliderScalarN(ImString label, ImGuiDataType dataType, Ptr pData, int components, Ptr pMin, Ptr pMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool VSliderFloat(ImString label, in float2 size, ref float v, float vMin, float vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool VSliderInt(ImString label, in float2 size, ref int v, int vMin, int vMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
	public static bool VSliderScalar(ImString label, in float2 size, ImGuiDataType dataType, Ptr pData, Ptr pMin, Ptr pMax, ImString format = default(ImString), ImGuiSliderFlags flags = ImGuiSliderFlags.None)
```

### Input widgets
```csharp
	public static bool InputText(ImString label, ReadOnlySpan<byte> buf, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, Ptr userData = default(Ptr))
	public static bool InputTextMultiline(ImString label, ReadOnlySpan<byte> buf, in float2? size = null, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, Ptr userData = default(Ptr))
	public static bool InputTextWithHint(ImString label, ImString hint, ReadOnlySpan<byte> buf, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, Ptr userData = default(Ptr))
	public static bool InputFloat(ImString label, ref float v, float step = 0f, float stepFast = 0f, ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputFloat2(ImString label, ref float2 v, ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputFloat3(ImString label, ref float3 v, ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputFloat4(ImString label, ref float4 v, ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputInt(ImString label, ref int v, int step = 1, int stepFast = 100, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputInt2(ImString label, ref int2 v, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputInt3(ImString label, ref int3 v, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputInt4(ImString label, ref int4 v, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputDouble(ImString label, ref double v, double step = 0.0, double stepFast = 0.0, ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputScalar(ImString label, ImGuiDataType dataType, Ptr pData, Ptr pStep = default(Ptr), Ptr pStepFast = default(Ptr), ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool InputScalarN(ImString label, ImGuiDataType dataType, Ptr pData, int components, Ptr pStep = default(Ptr), Ptr pStepFast = default(Ptr), ImString format = default(ImString), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
	public static bool Shortcut(ImGuiKeyChord keyChord, ImGuiInputFlags flags = ImGuiInputFlags.None)
	public static void SetNextItemShortcut(ImGuiKeyChord keyChord, ImGuiInputFlags flags = ImGuiInputFlags.None)
	public static bool InputText(ImString label, ImInputString buffer, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, nint userData = 0)
	public static bool InputTextMultiline(ImString label, ImInputString buffer, in float2 size = default(float2), ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, nint userData = 0)
	public static bool InputTextWithHint(ImString label, ImString hint, ImInputString buffer, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, nint userData = 0)
```

### Color widgets
```csharp
	public static bool ColorEdit3(ImString label, ref float3 col, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
	public static bool ColorEdit4(ImString label, ref float4 col, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
	public static bool ColorPicker3(ImString label, ref float3 col, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
	public static bool ColorPicker4(ImString label, ref float4 col, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None, in float4? refCol = null)
	public static void SetColorEditOptions(ImGuiColorEditFlags flags)
	public static void TableSetBgColor(ImGuiTableBgTarget target, ImColor8 color, int columnN = -1)
	public static float4 ColorConvertU32ToFloat4(uint @in)
	public static uint ColorConvertFloat4ToU32(in float4 @in)
	public static void ColorConvertRGBtoHSV(float r, float g, float b, out float outH, out float outS, out float outV)
	public static void ColorConvertHSVtoRGB(float h, float s, float v, out float outR, out float outG, out float outB)
	public static void DebugFlashStyleColor(ImGuiCol idx)
```

### Trees & headers
```csharp
	public static bool TreeNode(ImString label)
	public static bool TreeNode(ImString strId, ImString text)
	public static bool TreeNode(nint ptrId, ImString text)
	public static bool TreeNodeEx(ImString label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
	public static bool TreeNodeEx(ImString strId, ImGuiTreeNodeFlags flags, ImString text)
	public static bool TreeNodeEx(nint ptrId, ImGuiTreeNodeFlags flags, ImString text)
	public static void TreePush(ImString strId)
	public static void TreePush(nint ptrId)
	public static void TreePop()
	public static bool CollapsingHeader(ImString label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
	public static bool CollapsingHeader(ImString label, ref bool pVisible, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
	public static void SetNextItemOpen(bool isOpen, ImGuiCond cond = ImGuiCond.None)
```

### Selectables & multiselect
```csharp
	public static void SetNextItemStorageID(ImGuiID storageId)
	public static bool Selectable(ImString label, bool selected = false, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, in float2? size = null)
	public static bool Selectable(ImString label, ref bool pSelected, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, in float2? size = null)
	public static ImGuiMultiSelectIOPtr BeginMultiSelect(ImGuiMultiSelectFlags flags, int selectionSize = -1, int itemsCount = -1)
	public static ImGuiMultiSelectIOPtr EndMultiSelect()
	public static void SetNextItemSelectionUserData(ulong selectionUserData)
	public static bool IsItemToggledSelection()
```

### Menus
```csharp
	public static bool BeginMenuBar()
	public static void EndMenuBar()
	public static bool BeginMainMenuBar()
	public static void EndMainMenuBar()
	public static bool BeginMenu(ImString label, bool enabled = true)
	public static void EndMenu()
	public static bool MenuItem(ImString label, ImString shortcut = default(ImString), bool selected = false, bool enabled = true)
	public static bool MenuItem(ImString label, ImString shortcut, ref bool pSelected, bool enabled = true)
```

### Tooltips
```csharp
	public static bool BeginTooltip()
	public static void EndTooltip()
	public static void SetTooltip(ImString text)
	public static bool BeginItemTooltip()
	public static void SetItemTooltip(ImString text)
```

### Popups & modals
```csharp
	public static bool BeginPopup(ImString strId, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
	public static bool BeginPopupModal(ImString name, ref bool pOpen, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
	public static bool BeginPopupModal(ImString name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
	public static void EndPopup()
	public static void OpenPopup(ImString strId, ImGuiPopupFlags popupFlags = ImGuiPopupFlags.None)
	public static void OpenPopup(ImGuiID id, ImGuiPopupFlags popupFlags = ImGuiPopupFlags.None)
	public static void CloseCurrentPopup()
	public static bool IsPopupOpen(ImString strId, ImGuiPopupFlags flags = ImGuiPopupFlags.None)
	public static float2 GetMousePosOnOpeningCurrentPopup()
```

### Tables
```csharp
	public static bool BeginTable(ImString strId, int columns, ImGuiTableFlags flags = ImGuiTableFlags.None, in float2? outerSize = null, float innerWidth = 0f)
	public static void EndTable()
	public static void TableNextRow(ImGuiTableRowFlags rowFlags = ImGuiTableRowFlags.None, float minRowHeight = 0f)
	public static bool TableNextColumn()
	public static bool TableSetColumnIndex(int columnN)
	public static void TableSetupColumn(ImString label, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, float initWidthOrWeight = 0f, ImGuiID userId = default(ImGuiID))
	public static void TableHeader(ImString label)
	public static void TableHeadersRow()
	public static void TableAngledHeadersRow()
	public static ImGuiTableSortSpecsPtr TableGetSortSpecs()
	public static int TableGetColumnCount()
	public static int TableGetColumnIndex()
	public static int TableGetRowIndex()
	public static ImString TableGetColumnName(int columnN = -1)
	public static ImGuiTableColumnFlags TableGetColumnFlags(int columnN = -1)
	public static void TableSetColumnEnabled(int columnN, bool v)
	public static int TableGetHoveredColumn()
```

### Columns (legacy)
```csharp
	public static void Columns(int count = 1, ImString id = default(ImString), bool borders = true)
	public static void NextColumn()
	public static int GetColumnIndex()
	public static float GetColumnWidth(int columnIndex = -1)
	public static void SetColumnWidth(int columnIndex, float width)
	public static float GetColumnOffset(int columnIndex = -1)
	public static void SetColumnOffset(int columnIndex, float offsetX)
	public static int GetColumnsCount()
```

### Tab bars
```csharp
	public static bool BeginTabBar(ImString strId, ImGuiTabBarFlags flags = ImGuiTabBarFlags.None)
	public static void EndTabBar()
	public static bool BeginTabItem(ImString label, ref bool pOpen, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
	public static bool BeginTabItem(ImString label, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
	public static void EndTabItem()
	public static void SetTabItemClosed(ImString tabOrDockedWindowLabel)
```

### Plotting
```csharp
	public static void PlotLines(ImString label, ReadOnlySpan<float> values, int valuesOffset = 0, ImString overlayText = default(ImString), float scaleMin = float.MaxValue, float scaleMax = float.MaxValue, float2? graphSize = null, int stride = 4)
	public static void PlotHistogram(ImString label, ReadOnlySpan<float> values, int valuesOffset = 0, ImString overlayText = default(ImString), float scaleMin = float.MaxValue, float scaleMax = float.MaxValue, float2? graphSize = null, int stride = 4)
	public static void PlotLines(ImString label, PlotLinesValuesGetter valuesGetter, nint data, int valuesCount, int valuesOffset = 0, ImString overlayText = default(ImString), float scaleMin = float.MaxValue, float scaleMax = float.MaxValue, float2? graphSize = null)
	public static void PlotHistogram(ImString label, PlotHistogramValuesGetter valuesGetter, nint data, int valuesCount, int valuesOffset = 0, ImString overlayText = default(ImString), float scaleMin = float.MaxValue, float scaleMax = float.MaxValue, float2? graphSize = null)
```

### Disabling & clipping
```csharp
	public static void BeginDisabled(bool disabled = true)
	public static void EndDisabled()
	public static void PushClipRect(in float2 clipRectMin, in float2 clipRectMax, bool intersectWithCurrentClipRect)
	public static void PopClipRect()
```

### Focus & activation
```csharp
	public static void SetItemDefaultFocus()
	public static void SetKeyboardFocusHere(int offset = 0)
	public static void SetNavCursorVisible(bool visible)
	public static void SetNextItemAllowOverlap()
```

### Item queries
```csharp
	public static bool IsItemHovered(ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
	public static bool IsItemActive()
	public static bool IsItemFocused()
	public static bool IsItemVisible()
	public static bool IsItemEdited()
	public static bool IsItemActivated()
	public static bool IsItemDeactivated()
	public static bool IsItemDeactivatedAfterEdit()
	public static bool IsItemToggledOpen()
	public static bool IsAnyItemHovered()
	public static bool IsAnyItemActive()
	public static bool IsAnyItemFocused()
	public static ImGuiID GetItemID()
	public static float2 GetItemRectMin()
	public static float2 GetItemRectMax()
	public static float2 GetItemRectSize()
```

### Keyboard input
```csharp
	public static bool IsKeyDown(ImGuiKey key)
	public static bool IsKeyPressed(ImGuiKey key, bool repeat = true)
	public static bool IsKeyReleased(ImGuiKey key)
	public static bool IsKeyChordPressed(ImGuiKeyChord keyChord)
	public static int GetKeyPressedAmount(ImGuiKey key, float repeatDelay, float rate)
	public static ImString GetKeyName(ImGuiKey key)
	public static void SetNextFrameWantCaptureKeyboard(bool wantCaptureKeyboard)
	public static void SetItemKeyOwner(ImGuiKey key)
```

### Mouse input
```csharp
	public static bool IsMouseHoveringRect(in float2 rMin, in float2 rMax, bool clip = true)
	public static bool IsMousePosValid(in float2? mousePos = null)
	public static bool IsAnyMouseDown()
	public static float2 GetMousePos()
	public static ImGuiMouseCursor GetMouseCursor()
	public static void SetMouseCursor(ImGuiMouseCursor cursorType)
	public static void SetNextFrameWantCaptureMouse(bool wantCaptureMouse)
```

### Clipboard & misc
```csharp
	public static ImGuiIOPtr GetIO()
	public static ImGuiPlatformIOPtr GetPlatformIO()
	public static void ShowDebugLogWindow(ref bool pOpen)
	public static void ShowDebugLogWindow()
	public static void LogToTTY(int autoOpenDepth = -1)
	public static void LogToFile(int autoOpenDepth = -1, ImString filename = default(ImString))
	public static void LogToClipboard(int autoOpenDepth = -1)
	public static void LogFinish()
	public static void LogText(ImString text)
	public static ImGuiViewportPtr GetMainViewport()
	public static double GetTime()
	public static int GetFrameCount()
	public static ImString GetClipboardText()
	public static void SetClipboardText(ImString text)
	public static void DebugTextEncoding(ImString text)
	public static void DebugStartItemPicker()
	public static bool DebugCheckVersionAndDataLayout(ImString versionStr, nuint szIo, nuint szStyle, nuint szVec2, nuint szVec4, nuint szDrawvert, nuint szDrawidx)
	public static void DebugLog(ImString text)
	public static void Assert(bool condition, string? message = null)
```

### Other
```csharp
	public static void ShowDemoWindow(ref bool pOpen)
	public static void ShowDemoWindow()
	public static void ShowMetricsWindow(ref bool pOpen)
	public static void ShowMetricsWindow()
	public static void ShowIDStackToolWindow(ref bool pOpen)
	public static void ShowIDStackToolWindow()
	public static void ShowAboutWindow(ref bool pOpen)
	public static void ShowAboutWindow()
	public static void ShowStyleEditor(ImGuiStylePtr @ref = default(ImGuiStylePtr))
	public static bool ShowStyleSelector(ImString label)
	public static void ShowFontSelector(ImString label)
	public static ImString GetVersion()
	public static ImGuiViewportPtr GetWindowViewport()
	public static void Value(ImString prefix, bool b)
	public static void Value(ImString prefix, int v)
	public static void Value(ImString prefix, uint v)
	public static void Value(ImString prefix, float v, ImString floatFormat = default(ImString))
	public static ImGuiID DockSpace(ImGuiID dockspaceId, in float2? size = null, ImGuiDockNodeFlags flags = ImGuiDockNodeFlags.None, ImGuiWindowClassPtr windowClass = default(ImGuiWindowClassPtr))
	public static ImGuiID DockSpaceOverViewport(ImGuiID dockspaceId = default(ImGuiID), ImGuiViewportPtr viewport = default(ImGuiViewportPtr), ImGuiDockNodeFlags flags = ImGuiDockNodeFlags.None, ImGuiWindowClassPtr windowClass = default(ImGuiWindowClassPtr))
	public static ImDrawListPtr GetBackgroundDrawList(ImGuiViewportPtr viewport = default(ImGuiViewportPtr))
	public static ImDrawListPtr GetForegroundDrawList(ImGuiViewportPtr viewport = default(ImGuiViewportPtr))
	public static bool IsRectVisible(in float2 size)
	public static bool IsRectVisible(in float2 rectMin, in float2 rectMax)
	public static ImDrawListSharedDataPtr GetDrawListSharedData()
	public static void SetStateStorage(ImGuiStoragePtr storage)
	public static ImGuiStoragePtr GetStateStorage()
	public static void LoadIniSettingsFromDisk(ImString iniFilename)
	public static void SaveIniSettingsToDisk(ImString iniFilename)
	public static void SetAllocatorFunctions(ImGuiMemAllocFunc allocFunc, ImGuiMemFreeFunc freeFunc, Ptr userData = default(Ptr))
	public static Ptr MemAlloc(nuint size)
	public static void MemFree(Ptr ptr)
	public static ImGuiViewportPtr FindViewportByID(ImGuiID id)
	public static ImGuiViewportPtr FindViewportByPlatformHandle(Ptr platformHandle)
	public static void LoadIniSettingsFromMemory(ImString iniData)
	public static ImString SaveIniSettingsToMemory()
	public static void GetAllocatorFunctions(out ImGuiMemAllocFunc? pAllocFunc, out ImGuiMemFreeFunc? pFreeFunc, out nint pUserData)
```
