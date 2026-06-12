# Robotics — Rotating Parts at Runtime

How `flexo` articulates parts (hinges/rotors) by rotating a Part about a local axis each frame. This is the general pattern for moving any Part within a live vehicle without the editor.

## Core transform

A part's pose relative to its parent assembly is `Part.Asmb2ParentAsmb` (`doubleQuat`) + `Part.PositionParentAsmb` (`double3`). Rotate about a local axis:

```csharp
double angleRad = degrees * Math.PI / 180.0;
var axis = new double3(axisX, axisY, axisZ);                 // in the part's LOCAL assembly space
var hingeRotation = doubleQuat.CreateFromAxisAngle(axis, angleRad);

// moving part: hinge applied FIRST (local), then the original orientation
movingPart.Asmb2ParentAsmb = doubleQuat.Concatenate(hingeRotation, _originalRotation);
```

Snapshot `_originalRotation = movingPart.Asmb2ParentAsmb` and `_pivotPosition = movingPart.PositionParentAsmb` at construction; restore them on dispose.

## Gotcha: TreeChildren do NOT follow; SubParts DO

The central non-obvious fact:

- **`Part.SubParts`** (the part's own sub-parts) follow the parent automatically through the assembly hierarchy.
- **`Part.TreeChildren`** (downstream parts in the vehicle tree) have **independent** vehicle-assembly-space transforms — you must manually orbit each around the pivot:

```csharp
var rotMatrix = double4x4.CreateFromQuaternion(hingeRotation);
foreach (var snap in _descendants)            // recursively collected from TreeChildren
{
    double3 relative = snap.OriginalPosition - _pivotPosition;
    double3 rotated  = double3.Transform(relative, rotMatrix);
    snap.Part.PositionParentAsmb = _pivotPosition + rotated;
    // NOTE the opposite Concatenate order vs the moving part: orig FIRST, then hinge (both vehicle-asmb space)
    snap.Part.Asmb2ParentAsmb = doubleQuat.Concatenate(snap.OriginalRotation, hingeRotation);
    InvalidateSubPartCaches(snap.Part);
    snap.Part.BoundingBoxVehicleAsmb = snap.Part.ComputeBoundingBoxVehicleAsmb();
}
```

Concatenate order differs by case: moving part `(hinge, orig)` (hinge in local space); descendants `(orig, hinge)` (hinge in vehicle space).

## Gotcha: SubPart caches must be force-invalidated

SubParts cache `_positionVehicleAsmb` / `_asmb2VehicleAsmb` derived from the parent rotation, invalidated only by the SubPart's **own** setter. After rotating a parent, force a recompute by self-assigning (otherwise thrust vectors, connector positions, and lights stay stale):

```csharp
static void InvalidateSubPartCaches(Part part) {
    foreach (var sub in part.SubParts) {
        sub.PositionParentAsmb = sub.PositionParentAsmb;     // trip the setter
        sub.Asmb2ParentAsmb    = sub.Asmb2ParentAsmb;
        sub.BoundingBoxVehicleAsmb = sub.ComputeBoundingBoxVehicleAsmb();
        InvalidateSubPartCaches(sub);                        // recurse
    }
}
```

Also update the moving part's own bbox: `movingPart.BoundingBoxVehicleAsmb = movingPart.ComputeBoundingBoxVehicleAsmb();`

## Required physics recompute

After moving parts, recompute mass and derived vehicle data:

```csharp
Traverse.Create(vehicle.Parts).Method("RecomputeStaticMass").GetValue();  // private — via Harmony Traverse
vehicle.UpdateAfterPartTreeModification();   // bbox, mass/propellant, aero, flight-computer config
```

`vehicle.Parts` is a `PartTree`.

## Drive it before the solvers (ordering)

Update transforms in a Harmony **prefix on `Universe.ExecuteNextVehicleSolvers`** with `priority = Priority.First`, so they're applied before the physics solvers read them that step (see lifecycle.md for the hook):

```csharp
var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
harmony.Patch(original, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
```

## Detecting robotic parts

No special module — pure template-id matching against a vehicle scan:

- `VehicleProvider.GetControlledVehicle()` → `PartHelpers.GetAllParts(vehicle)` (recurses `vehicle.Parts.Parts` + `Part.SubParts`).
- Match `part.Template.Id == FixedPartTemplateId` / `MovingPartTemplateId`. Any fixed+moving pair on the same vehicle qualifies.
- (Connectivity check available via `partA.Connections` + `connection.OtherPart(partA)`, but flexo doesn't require it.)

## Summary ordering

1. In an `ExecuteNextVehicleSolvers` prefix, compute `hingeRotation`.
2. Set the moving part's `Asmb2ParentAsmb` (`Concatenate(hinge, orig)`).
3. Orbit each TreeChildren descendant around the pivot (`Concatenate(orig, hinge)`), invalidate its SubPart caches, recompute its bbox.
4. Recompute the moving part's bbox.
5. `RecomputeStaticMass` (private, via Traverse) + `UpdateAfterPartTreeModification()`.
