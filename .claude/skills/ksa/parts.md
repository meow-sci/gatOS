# KSA Parts & SubParts — Rendering, Raycasting, and Mouse Detection

Deep reference for working with KSA Part/SubPart rendering and mouse interaction outside the game's built-in vehicle editor.

## Part vs SubPart Architecture

In KSA, the part hierarchy is:

- **Part** — a top-level entry in a vehicle's `PartTree`. Has `SubParts[]` children.
- **SubPart** — a leaf-level child under a Part. Each SubPart is itself a `Part` instance with `IsSubPart = true`.

### PartTemplate and SubPart Templates

Every Part is created from a `PartTemplate` loaded via `ModLibrary.Get<PartTemplate>(templateId)`. Templates define the Part's modules via a `Components` list containing typed `TemplateDataBase` entries:

```
PartTemplate
  └─ Components: List<ModuleBase.TemplateDataBase>
       ├─ PartModelModule.Template  (rendering mesh + material)
       ├─ MeshViewModule.Template   (raycast mesh — NOT always present)
       ├─ LightModule.TemplateData  (lights)
       └─ ... other module types
```

**Key insight:** The `Components` list uses `[XmlElement]` polymorphic deserialization. Each component type has an `[XmlType(TypeName = "...")]` attribute mapping its XML element name to the C# type.

### The Two Mesh Types

| Module | Purpose | Mesh ID convention | Always present? |
|---|---|---|---|
| `PartModelModule` | GPU rendering (visual mesh) | e.g. `CoreFuelTankA_Subpart_Skin3W3HA_Model` | Yes (if part renders) |
| `MeshViewModule` | CPU raycasting (click/hover detection) | Typically `_VM` suffix or same as render mesh | **No** — many SubPart templates omit it |

Both reference a `MeshReference` object, but the MeshView mesh is typically a lower-polygon simplified version optimized for raycast performance.

## Creating Parts Programmatically

```csharp
PartTemplate template = ModLibrary.Get<PartTemplate>(subPartTemplateId);
var part = new Part(instanceId, template);
part.PositionParentAsmb = position;    // double3, assembly-space
part.Asmb2ParentAsmb = rotation;       // doubleQuat
part.Scale = scale;                    // double3

// CRITICAL: Populate PartTree.Modules so rendering and raycasting modules are created
PartTree.CreateFromNewPartTree(part);
```

`PartTree.CreateFromNewPartTree()` calls `TransferPartSubtree()` which:
1. Adds the Part to the tree's `_parts` list
2. Sets `part.Tree = this`
3. Calls `part.Modules.AddStatesInto(States, prevStates)` + `Modules.AddFrom(part.Modules)`
4. Does the same for all `SubParts` children
5. Calls `RecomputeAllDerivedData()`

After this call, `part.Modules.Get<PartModelModule>()` and `part.Modules.Get<MeshViewModule>()` return their respective module spans (if the template defined those components).

## Rendering SubParts

Rendering uses `PartModelModule`, which is created by `PartModelModule.CreateComponents()` during `PartTree.CreateFromNewPartTree()`:

```csharp
// Inside PartModelModule.CreateComponents (called by engine):
foreach (TemplateDataBase component in template.Components)
{
    if (component is PartModelModule.Template template2)
    {
        var module = new PartModelModule(template2.Id)
        {
            Parent = part,
            PartModel = PartModel.Get(template2)  // resolves mesh + material
        };
        part.Modules.Add(module);
    }
}
```

The `PartModel.Get()` call resolves the template's `Mesh` (a `MeshReference`) and `Material` (a `PbrMaterialReference`), creating GPU resources as needed. Rendering then happens automatically when `UpdateRenderData()` is called with the appropriate ego-space matrix.

### VehicleEditingSpace for Isolated Rendering

To render parts outside any vehicle (e.g. a custom editor), create a `VehicleEditingSpace`:

```csharp
double sunRadius = Universe.CurrentSystem.GetWorldSun()?.MeanRadius ?? 696_000_000.0;
double3 positionEcl = new double3(0, 0, 10.0 * sunRadius);  // far from everything
var editingSpace = new VehicleEditingSpace(positionEcl, doubleQuat.Identity, 10.0, null);
```

Then obtain the transform matrix via:
```csharp
Camera camera = viewport.GetCamera();
double4x4 matrixAsmb2Ego = editingSpace.GetMatrixAsmb2Ego(camera);
```

This is the same approach the game's built-in vehicle editor uses.

## Mouse Detection (Raycasting)

### How the Game Does It

The game's `VehicleEditor.cs` raycast pipeline:

1. Build ray: `camera.ScreenToEgoRay(cursorScreenPos)` — returns a `Ray` in ego-space
2. Get matrix: `editingSpace.GetMatrixAsmb2Ego(camera)` — assembly-to-ego transform
3. For each Part, call `part.RayCastEgo(in matrix, ray, ...)` — iterates `part.SubParts[]` testing each child
4. Each SubPart tested via `part.RayCastEgoSubPart(in matrix, ray, ...)` — tests the Part's own mesh

### RayCastEgoSubPart Implementation (from decompiled Part.cs)

```csharp
public bool RayCastEgoSubPart(ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray,
    out double minDistance, out double maxDistance,
    out double3 nearIntersectionPositionAsmb, out double3 nearIntersectionNormalAsmb,
    out double3 farIntersectionPositionAsmb, out double3 farIntersectionNormalAsmb)
{
    // 1. Get MeshViewModule — REQUIRED for raycasting
    Span<MeshViewModule> span = Modules.Get<MeshViewModule>();
    if (span.IsEmpty) return false;  // ← No MeshViewModule = no raycast = invisible to clicks

    MeshReference meshView = span[0].MeshView;

    // 2. Bounding sphere quick-rejection test
    double scale = Double3Ex.GetAbsoluteLargestElement(ScaleTotal);
    double3 posEgo = PositionEgo(in matrixVehicleAsmb2Ego);
    BoundingSphere3D sphere = new BoundingSphere3D(posEgo, meshView.BoundingSphereRadius * scale);
    if (!ray.Raycast(sphere, out _, out _)) return false;

    // 3. Watertight triangle raycast using PositionCompare vertex data
    double3[] positionCompare = meshView.PositionCompare;
    double4x4 vertexOffset = MatrixAsmb2Ego(in matrixVehicleAsmb2Ego);
    if (!ray.RaycastWatertight(positionCompare, in vertexOffset, out nearT, out nearIdx, out farT, out farIdx))
        return false;

    // ... compute intersection positions and normals ...
    return true;
}
```

### MeshReference Data Requirements

For raycasting to work, the `MeshReference` must have:

| Field | Type | Required state |
|---|---|---|
| `BoundingSphereRadius` | `double` | `> 0` — calculated from mesh vertex extremes |
| `PositionCompare` | `double3[]` | Non-empty — indexed vertex positions used by `RaycastWatertight()` |
| `HostMesh` | `MeshAsset` | Non-null — needed for normal lookup on hit |

These fields are populated when the mesh is loaded from GLTF atlas files. They are `[XmlIgnore]` runtime fields, not persisted.

### The Missing MeshViewModule Problem

**Problem:** Many SubPart templates (particularly IVA props like chairs, instruments, decorative items) only define a `<PartModel>` component in their template XML — they have no `<MeshView>` component. This means:

- `PartModelModule` is created → rendering works fine
- `MeshViewModule` is **never** created → `Modules.Get<MeshViewModule>()` returns empty span
- `RayCastEgoSubPart()` returns `false` at the very first check
- The part is completely invisible to mouse hover and click detection

**Why the game doesn't care:** The built-in vehicle editor always works with top-level Parts that contain SubParts as children. `RayCastEgo()` iterates `SubParts[]` and those children typically DO have MeshView components. The game never needs to raycast against a bare SubPart template in isolation.

**When it matters:** Custom editors (like space-tape) that create standalone Parts from individual SubPart template IDs hit this issue because the created Part has no SubParts children, and the Part itself may lack a MeshViewModule.

### Fix: Create MeshViewModule from Rendering Mesh

The `PartModelModule` always has a `MeshReference` via `partModel.PartModel.Template.Mesh`. This rendering mesh has the same `BoundingSphereRadius` and `PositionCompare` data needed for raycasting (it was populated during GLTF loading). We can reuse it:

```csharp
private static void EnsureMeshViewModule(Part part, string subPartTemplateId)
{
    if (!part.Modules.Get<MeshViewModule>().IsEmpty)
        return; // already has one from template

    Span<PartModelModule> partModels = part.Modules.Get<PartModelModule>();
    if (partModels.IsEmpty)
        return;

    MeshReference? renderMesh = partModels[0].PartModel?.Template?.Mesh;
    if (renderMesh == null || renderMesh.PositionCompare is not { Length: > 0 } || renderMesh.BoundingSphereRadius <= 0)
        return;

    var module = new MeshViewModule(subPartTemplateId, renderMesh) { Parent = part };
    part.Modules.Add(module);
}
```

**Call this after `PartTree.CreateFromNewPartTree(part)`** but before any raycasting occurs. This makes ALL SubPart types clickable, including IVA props.

**Trade-off:** The rendering mesh typically has more triangles than a dedicated `_VM` (view mesh), so raycasting is slightly more expensive per-part. In practice this is negligible for editor-scale part counts.

### Complete Part Creation with Raycast Support

```csharp
private static Part CreatePartFromPlacement(SubPartPlacement placement)
{
    PartTemplate template = ModLibrary.Get<PartTemplate>(placement.SubPartTemplateId);
    var part = new Part(placement.InstanceId, template);
    part.PositionParentAsmb = placement.Position;
    part.Asmb2ParentAsmb = placement.Rotation;
    part.Scale = placement.Scale;

    PartTree.CreateFromNewPartTree(part);
    EnsureMeshViewModule(part, placement.SubPartTemplateId); // ← fix for missing MeshView

    return part;
}
```

### Custom Editor Raycast Loop

```csharp
// Build ray from camera through ImGui cursor position
Ray ray = camera.ScreenToEgoRay(new double2(ImGui.GetMousePos().X, ImGui.GetMousePos().Y));
ray.Direction = ray.Direction.NormalizeOrZero();

double4x4 matrixAsmb2Ego = editingSpace.GetMatrixAsmb2Ego(camera);

Part? highlighted = null;
double closest = double.MaxValue;

foreach (Part part in editorParts)
{
    // Test the Part's own mesh (works for standalone SubPart-template Parts)
    if (part.RayCastEgoSubPart(in matrixAsmb2Ego, ray,
        out double nearT, out double _, out double3 _, out double3 _,
        out double3 _, out double3 _)
        && nearT < closest)
    {
        closest = nearT;
        highlighted = part;
    }

    // Also test SubParts children (for imported multi-SubPart Parts)
    if (part.RayCastEgo(in matrixAsmb2Ego, ray,
        out double nearT2, out double _, out double3 _, out double3 _,
        out double3 _, out double3 _, out Part? closestSub, out Part? _)
        && nearT2 < closest)
    {
        closest = nearT2;
        highlighted = closestSub?.PartParent ?? closestSub ?? part;
    }
}

// Apply visual feedback
if (highlighted != previousHighlight)
{
    if (previousHighlight != null) previousHighlight.Highlighted = false;
    if (highlighted != null) highlighted.Highlighted = true;
}
```

### Hover/Selection Visual Feedback

The game's GPU shader automatically renders highlight and selection effects when:
- `part.Highlighted = true` — hover highlight effect
- `part.Selected = true` — selection outline effect

These are set per-Part and the shader picks them up automatically during rendering.

## ModLibrary Lookup Notes

- `ModLibrary.Get<PartTemplate>(id)` — works; throws `NullReferenceException` if not found
- `ModLibrary.Get<MeshReference>(id)` — works; throws if not found
- `ModLibrary.TryGet<MeshReference>(id, out var mesh)` — **does NOT work** for `MeshReference` (the `TryGet` method has no branch for `MeshReference` type and always returns false). Use `Get<MeshReference>` with try/catch instead.
- `ModLibrary.TryGet<T>` supported types: `FileReference`, `SoundBehavior`, `PbrMaterialReference`, `GaugeCanvas`, `GaugeComponent`, `AstronomicalTemplate`, `SpatialSoundData`, `SoundGroupData`, `CharacterReference` and variants, `SituationTemplate`

## Coordinate Spaces for Part Positioning

| Space | Description | Conversion |
|---|---|---|
| **ParentAsmb** | Position/rotation relative to parent Part | `part.PositionParentAsmb`, `part.Asmb2ParentAsmb` |
| **VehicleAsmb** | Position in the vehicle's assembly frame | `part.PositionVehicleAsmb` (computed) |
| **Ego** | Camera-relative eye space | `part.PositionEgo(in matrixAsmb2Ego)` |

The `matrixAsmb2Ego` transform is obtained from the `VehicleEditingSpace`:
```csharp
double4x4 matrixAsmb2Ego = editingSpace.GetMatrixAsmb2Ego(camera);
```

`Part.MatrixAsmb2Ego(in matrixAsmb2Ego)` gives the full vertex transform including the Part's own position/rotation/scale. This is used both for rendering vertex positions and for raycast geometry transformation.

### Matrix Cache Invalidation

`Part` caches its `_matrixAsmb` (assembly-space matrix). The property setters for `PositionParentAsmb`, `Asmb2ParentAsmb`, and `Scale` reset this cache to `Identity`. However, if you need belt-and-suspenders invalidation (e.g. after reflection-based mutations):

```csharp
private static readonly FieldInfo? _matrixAsmbField =
    typeof(Part).GetField("_matrixAsmb", BindingFlags.NonPublic | BindingFlags.Instance);

private static void InvalidatePartMatrixCache(Part part)
{
    _matrixAsmbField?.SetValue(part, double4x4.Identity);
}
```
