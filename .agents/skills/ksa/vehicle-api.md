# Vehicle API Reference

## Controlled Vehicle

```csharp
Vehicle? vehicle = Program.ControlledVehicle; // currently player-controlled vehicle; may be null
```

## Physics / Flight Data

```csharp
double3 accelBody = vehicle.AccelerationBody;   // acceleration in vehicle body frame (m/s²)
double  twr       = vehicle.NavBallData.ThrustWeightRatio; // live TWR
float   vacThrust = vehicle.FlightComputer.VehicleConfig.TotalEngineVacuumThrust; // Newtons
float   totalMass = vehicle.TotalMass;          // kg

// Surface gravity of parent body (m/s²):
double g = 6.6743e-11 * vehicle.Parent.Mass / (vehicle.Parent.MeanRadius * vehicle.Parent.MeanRadius);
double maxAccel = totalMass > 0 ? vacThrust / totalMass : 0;
```

- `AccelerationBody.X` = longitudinal (thrust axis), `.Y` = lateral, `.Z` = normal
- Convert to G-force: `accelBody.Length() / 9.80665`
- `BodyRates` — `double3` angular velocity (rad/s); guard NaN before use

## Orientation Helpers

```csharp
doubleQuat body2Cci = vehicle.GetBody2Cci();   // body frame → inertial frame
doubleQuat body2Cce = vehicle.Body2Cce;        // direct property (body frame → body-fixed frame)
double3    posCci   = vehicle.GetPositionCci();
double3    velCci   = vehicle.GetVelocityCci();
```

## Render Override (Visibility / LOD Bypass)

KSA normally culls vehicle rendering at range. To force a vehicle to render regardless of distance, Harmony-patch these two methods on `Vehicle`:

```csharp
[HarmonyPatch(typeof(Vehicle), "GetWorldMatrix")]
[HarmonyPrefix]
private static bool GetWorldMatrix_Prefix(Vehicle __instance, Camera camera, ref float4x4? __result)
{
    double3    pos   = camera.GetPositionEgo(__instance);          // ego-space position
    float4x4   trans = float4x4.CreateTranslation(float3.Pack(in pos));
    float4x4   rot   = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce));
    __result = rot * trans;
    return false; // suppress original
}

[HarmonyPatch(typeof(Vehicle), "UpdateRenderData")]
[HarmonyPrefix]
private static bool UpdateRenderData_Prefix(Vehicle __instance, Viewport viewport, int inFrameIndex)
{
    double4x4 m = __instance.GetMatrixAsmb2Ego(viewport.GetCamera());
    __instance.Parts.UpdateRenderData(in m, __instance.IsEditedVehicle, inFrameIndex);
    return false;
}
```

- `camera.GetPositionEgo(vehicle)` returns the vehicle position relative to the camera's ego frame (`double3`)
- `vehicle.GetMatrixAsmb2Ego(Camera)` builds the assembly-to-ego transform matrix
- `vehicle.IsEditedVehicle` — bool flag for the VAB/editor vehicle state
- Return `false` from prefix to skip the original LOD check

## Engine Control

```csharp
// Get all engines on a vehicle:
EngineController[] engines = vehicle.Parts.Modules.Get<EngineController>();

// Get engines on a specific part subtree:
EngineController[] engines = part.SubtreeModules.Get<EngineController>();

// Activate/deactivate:
engine.SetIsActive(null, false); // first arg is unused (pass null)

// Set minimum throttle:
engine.MinimumThrottle = 0.0001f; // float, range 0–1

// After modifying engine state, recompute vehicle data:
vehicle.Parts.RecomputeAllDerivedData();
```
