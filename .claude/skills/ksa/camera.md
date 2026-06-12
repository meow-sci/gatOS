# Camera Controller Patching

## Overview

KSA has two camera controller types that can be patched via Harmony to intercept or override camera behavior:

- `OrbitController` — orbit/follow camera mode
- `FlyController` — free-fly camera mode

Both expose an `OnFrame(double inDeltaTime)` method that drives the camera each frame.

## Harmony Patch Pattern

```csharp
[HarmonyPatch(typeof(OrbitController), "OnFrame")]
[HarmonyPrefix]
private static bool OrbitController_OnFrame_Prefix(OrbitController __instance, double inDeltaTime, Transform3D ___Transform)
    => HandleOnFramePrefix(__instance, inDeltaTime, ___Transform);

[HarmonyPatch(typeof(FlyController), "OnFrame")]
[HarmonyPrefix]
private static bool FlyController_OnFrame_Prefix(FlyController __instance, double inDeltaTime, Transform3D ___Transform)
    => HandleOnFramePrefix(__instance, inDeltaTime, ___Transform);

// Return false to suppress default camera logic; return true to run it normally.
private static bool HandleOnFramePrefix(Controller controller, double deltaTime, Transform3D transform)
{
    if (shouldOverride)
    {
        // ... manipulate transform ...
        return false; // skip original
    }
    return true; // pass through
}
```

- Both types derive from `Controller` — use `Controller` as the parameter type in shared handlers
- `___Transform` (triple-underscore) accesses the private `Transform3D` field by name via Harmony injection

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

// Viewport camera reference (e.g., in UpdateRenderData patches):
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

```csharp
double3 startUp    = double3.UnitY.Transform(startRotation);
double3 right      = double3.Cross(startUp, startOffset).Normalized();
double3 orbitAxis  = double3.Cross(startOffset.Normalized(), right).Normalized();
```
