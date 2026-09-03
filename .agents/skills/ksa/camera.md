# Camera Controller Patching

## Overview

KSA has two camera controller types that can be patched via Harmony to intercept or override camera behavior:

- `OrbitController` — orbit/follow camera mode
- `FlyController` — free-fly camera mode

Both override `Controller.OnFrame(IViewport inViewport, double inDeltaTime)` — **two** parameters — which drives the camera each frame. (Through build 2026.8.22.5348 the parameter type was the concrete `Viewport` class; build 2026.9.7.5402 replaced that class with the `IViewport`/`IGameViewport` interfaces, `ViewportBase`, `GameViewport` and a static `ViewportRegistry` — see the callout below.)

## Harmony Patch Pattern

```csharp
[HarmonyPatch(typeof(OrbitController), "OnFrame")]
[HarmonyPrefix]
private static bool OrbitController_OnFrame_Prefix(OrbitController __instance, double inDeltaTime)
    => HandleOnFramePrefix(__instance, inDeltaTime);

[HarmonyPatch(typeof(FlyController), "OnFrame")]
[HarmonyPrefix]
private static bool FlyController_OnFrame_Prefix(FlyController __instance, double inDeltaTime)
    => HandleOnFramePrefix(__instance, inDeltaTime);

// Return false to suppress default camera logic; return true to run it normally.
private static bool HandleOnFramePrefix(Controller controller, double deltaTime)
{
    // The camera transform IS the controller's public `Camera` field (KSA.Camera : Transform3D),
    // so writing to it mutates the live view by reference.
    Camera transform = controller.Camera;

    if (shouldOverride)
    {
        // ... manipulate transform ...
        return false; // skip original
    }
    return true; // pass through
}
```

- Both types derive from `Controller` — use `Controller` as the parameter type in shared handlers
- The camera transform is the **public mutable field** `public Camera Camera;` on `KSA.Controller`
  (`decomp/KSA/Controller.cs:12`), where `KSA.Camera : Transform3D`. Reach it as `__instance.Camera`
- Declaring only `(Controller __instance, double inDeltaTime)` and omitting `inViewport` is legal —
  Harmony binds original arguments **by name**, so a subset is fine

> **⚠️ A prefix on `Controller.OnFrame` fires up to 7× per frame — bind the main viewport explicitly.**
> Since 2026.9.7.5402 viewports live in the static `ViewportRegistry` (`MAX_VIEWPORTS = 8`): **1**
> `ViewportType.Main`, **1** `PartThumbnail` (a `PartThumbnailViewport : ViewportBase` — *not* an
> `IGameViewport`, has no controllers), **4** `Secondary` camera windows and **2** `CharacterPortrait`
> (crew face-cams). `Program.OnFrameViewports` calls `viewport.OnFrame(dt)` for every registered
> viewport, and `GameViewport.OnFrame` is literally `GetActiveController().OnFrame(this, dt);
> GetCamera().OnFrame(dt);` (+ audio for the main one) — so every hidden secondary window and both
> portrait viewports run their controllers too, each with its **own** `Controller` and `Camera`
> instances. A patch that assumes "one call per frame" will animate the wrong camera, or the same
> animation several times. **Resolve the target viewport by identity — `Program.MainViewport`
> (an `IGameViewport`; `ReferenceEquals`) — never by index or `ShaderSlot`**, and never assume
> `Program.GetCamera()`/`GetCameraMode()` mean the main one (they read the *frame* viewport).
> `ViewportRegistry.IsMainCamera(Camera)` answers "is this the main viewport's base or map camera".
>
> **Viewport API cheat-sheet (5402):** `IViewport` = `{Id, ShaderSlot, Name, Type, State, OptionFlags,
> LightMode, Visible, Mode, OffscreenTarget, MainTarget, Width/Height/Size, PendingSize, Position,
> GetCamera(), SetVisible(), RequestResize(), OnFrame(dt)}`; `IGameViewport : IViewport` adds
> `{BaseCamera, MapCamera, FlyController, OrbitController, MapController, IvaController,
> FixedController, PartPicker, Hovered, MenuBarInUse, ImGuiId, IvaAudio, GetActiveController(),
> SetCameraMode(), NextCameraMode(), SetName(), DrawImGui()}`. Everything that used to be a public
> **field** (`Mode`, `Visible`, `Hovered`, `MenuBarInUse`, `FixedController`, `Size`, `Position`,
> `LightMode`, `ShouldRenderGizmos`/`ShouldRenderStars`/`IsOffscreen`) is now a **get-only or
> protected-set property**: write `Mode` through `SetCameraMode`, `Visible` through `SetVisible`,
> `Hovered`/`MenuBarInUse` through the explicit `IGameViewportLifecycle` interface
> (`((IGameViewportLifecycle)vp).SetMenuBarInUse(true)`), and the render flags are the
> `ViewportOptionFlags` bit-set (`vp.HasAll(ViewportOptionFlags.RenderPartModels)`). Replacing a
> controller (`FixedController` etc.) now needs reflection on the protected setter of the
> `GameViewport` auto-property — gatOS does this in `Game/Ksa/Camera/ViewportSeam.cs`.
> `Viewport.Index` became `IViewport.ShaderSlot` (the per-viewport UBO / `GlobalShaderBindings.DynamicOffset` slot);
> `Program.GetCrewPortraitViewport(int)` still exists, but classify by `vp.Type ==
> ViewportType.CharacterPortrait` (`vp.Is(...)` in `ViewportEx`) rather than by identity or index.
>
> Do **not** port unscience's "first controller wins" guard
> (`camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs:437-444`): it locks onto the
> first `Controller` instance to call `Update` and then, for every other instance, returns `true` —
> which the prefix inverts into `return false`, **skipping the original `OnFrame`**. The result is that
> the other three viewports' controllers are suppressed but never driven, i.e. **frozen** for the whole
> playback. It is a bug, not a pattern.

> **⚠️ There is no `___Transform` — do not reintroduce it.** `Controller`, `OrbitController` and
> `FlyController` have **no private `Transform3D` field**; a `Transform3D ___Transform` field injector
> therefore binds to nothing. Harmony validates injected field names at **patch time**, so `Patch()`
> *throws* — and in a shared-Harmony host that also silently aborts every feature patched after it in
> the chain. An earlier revision of this document documented exactly that injector and it misled a real
> implementation; the working fix lives in
> `unscience/camera-controller-override.lib/CameraControllerOverridePatches.cs:42-64`, whose in-source
> comment records the history.

## Coordinate Frame: ECL (Ecliptic)

The camera uses **Ecliptic (ECL)** coordinates — the solar-system-scale inertial frame. This is separate from CCI/CCE which are per-body frames used for vehicle physics.

```csharp
double3 cameraPos = transform.PositionEcl;  // camera position in ecliptic space
```

## Transform3D

```csharp
transform.PositionEcl    // double3 — camera world position (ecliptic)
transform.LocalRotation  // doubleQuat — camera orientation
```

Write to these to move/orient the camera.

## Controller & Camera API

```csharp
Controller controller = __instance; // OrbitController or FlyController

// Target the camera is following:
double3 targetPos = controller.Camera.Following.GetPositionEcl();

// Look-at rotation (built-in helper):
double3 up = double3.UnitY.Transform(transform.LocalRotation);
transform.LocalRotation = Camera.LookAtRotation(lookDirection, up);

// Viewport camera reference (e.g., in UpdateRenderData patches — the parameter is IViewport since 5402):
Camera camera = viewport.GetCamera();
double3 egoPos = camera.GetPositionEgo(vehicle); // vehicle position in camera ego space
```

## Orbit / Rodrigues Rotation Pattern

To orbit the camera by a total angle from a fixed start offset (avoids cumulative drift):

```csharp
double3 k     = orbitAxis;          // normalized rotation axis
double  cos   = Math.Cos(angleRad);
double  sin   = Math.Sin(angleRad);
double3 rotated = startOffset * cos
    + double3.Cross(k, startOffset) * sin
    + k * double3.Dot(k, startOffset) * (1.0 - cos);

transform.PositionEcl = currentTargetPos + rotated;
```

- Always rotate `startOffset` (captured at animation start), not the live offset — prevents cumulative floating-point drift
- `currentTargetPos` should be fetched fresh each frame so the orbit follows a moving target

## Orbit Axis Calculation

The axis is the camera's own up vector projected perpendicular to the offset — with a fallback at each
step, because both cross products degenerate when the camera looks straight down its own orbit axis.
Verbatim from `unscience/camera-controller-override.lib/Animation/AnimationHelpers.cs:75-88`:

```csharp
public static double3 CalculateOrbitAxis(double3 startOffset, doubleQuat startRotation)
{
    double3 startUp = double3.UnitY.Transform(startRotation);
    if (startUp.LengthSquared() < 0.00000001) startUp = double3.UnitY;   // degenerate rotation

    double3 right = double3.Cross(startUp, startOffset);
    if (right.LengthSquared() < 0.0001)                                   // up ∥ offset
    {
        double3 offsetDir = double3.Normalize(startOffset);
        double3 fallback = Math.Abs(double3.Dot(offsetDir, double3.UnitY)) < 0.99
            ? double3.UnitY
            : double3.UnitX;
        right = double3.Cross(fallback, startOffset);
    }
    return double3.Normalize(double3.Cross(startOffset, right));
}
```

- Skipping the fallbacks yields a zero-length axis (then NaN out of `Normalize`) for exactly the shots
  people write first — a top-down orbit, or a camera whose up already lies along the offset
