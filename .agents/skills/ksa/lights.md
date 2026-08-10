# Lights, Solar Panels & Keyframe Animation

Controlling stock KSA lights (on/off, color, intensity), deploying solar panels, driving keyframe animations, and creating light parts at runtime. All types in namespace `KSA`.

## Module access basics

- `part.Modules.Get<T>()` → `Span<T>` of modules on **this part only**. Check `.Length`, index `[0]`.
- `part.SubtreeModules.Get<T>()` → `Span<T>` aggregated over part + all sub-parts. Use to ask "does this top-level part carry a light/anim anywhere".
- `part.FullPart` resolves an inner sub-part to its owning top-level part.

## Light on/off — `Part.LightSwitch` (a `PowerConsumer`)

```csharp
public PowerConsumer? LightSwitch;   // on Part — null if the part isn't a light host
public bool LightIsActive;           // on PowerConsumer
```

```csharp
var ls = topPart.LightSwitch;
if (ls != null) ls.LightIsActive = enabled;   // on/off lives on the TOP-LEVEL part's switch
```

Inner light parts resolve theirs via `LightPart.FullPart.LightSwitch`. **An unpowered light won't render even when switched on** — the render path early-outs when the power-consumer state isn't `Active`.

## Light color / intensity — reflection on `LightModule.TemplateData`

Light appearance lives in `LightModule.TemplateData` (decomp-authoritative):

```csharp
public class LightModule : Module<LightModule> {
  public class TemplateData : TemplateDataBase {
    public FloatReference    Intensity = new FloatReference(1f);
    [XmlElement("Color")] public ColorRgbReference ColorRgb = new ColorRgbReference(Color.Gray);
  }
  public TemplateData Template;   // SHARED across all instances of the same PartTemplate
}
```

`ColorRgbReference` has public float fields `R`, `G`, `B` and an `OnDataLoad(Mod)` that recomputes `Value = new float3(R,G,B)`. `FloatReference` has `.Value`.

**Preferred path — the live module instance (matches decomp):**

```csharp
foreach (var lm in part.Modules.Get<LightModule>())   // Span<LightModule>
{
    EnsurePerInstanceTemplate(lm);          // clone the TemplateData first — see sharing gotcha
    var c = lm.Template.ColorRgb;           // ColorRgbReference
    c.R = r; c.G = g; c.B = b;
    c.OnDataLoad(null);                     // REQUIRED after R/G/B writes, or Value stays stale
    lm.Template.Intensity.Value = intensity;
}
```

> **Field-name discrepancy to know:** the decomp field is `ColorRgb` (with `[XmlElement("Color")]`). `red-alert` addresses the live module's `ColorRgb`; `zippo` instead walks `PartTemplate.Components` filtering type `"KSA.LightModule+TemplateData"` and reads a field named `"Color"`. **Prefer the live-module `Template.ColorRgb` path** — it matches the decomp. Always call `OnDataLoad(null)` after editing channels.

### Sharing gotcha — TemplateData is per-template, not per-instance

By default every `LightModule` instance shares one `TemplateData` per `PartTemplate`, so writing color/intensity changes **every part using that template**.

- To color **one** part independently: reflection-clone the `TemplateData` (and its `ColorRgb`) on first write and track it (e.g. `ConditionalWeakTable<LightModule, object>`). `RuntimeHelpers.GetUninitializedObject` + field copy works for the shallow clone.
- To color a **whole grid cheaply**: deliberately write the shared template once, deduped by `ReferenceEquals` on the template (what its-so-shiny does).

### Detecting light parts

- Live: `part.Modules.Get<LightModule>().Length > 0` (recurse `part.SubParts`), or `part.SubtreeModules.Get<LightModule>()`.
- Template-only (no instance): scan `PartTemplate.Components` for type name `"KSA.LightModule+TemplateData"`.

## Solar panels & keyframe animation — `KeyframeAnimationModule.TimeGoal`

Deploy/retract and any keyframe actuation is driven by setting `KeyframeAnimationModule.TimeGoal` (a `float` in **seconds**, range `0 .. Shared.Duration` — NOT normalized). The engine animates and recomputes transforms.

```csharp
KeyframeAnimationModule? FindAnimModule(Part part) {
    var owner = part.FullPart ?? part;
    var span = owner.SubtreeModules.Get<KeyframeAnimationModule>();
    return span.Length > 0 ? span[0] : null;
}

// actuate to normalized t in [0,1]:
anim.TimeGoal = t * anim.Shared.Duration;        // deploy = Duration, retract = 0
// toggle:
bool deployed = anim.TimeGoal >= anim.Shared.Duration * 0.5f;
anim.TimeGoal = deployed ? 0f : anim.Shared.Duration;
```

`KeyframeAnimationModule` has `required KeyframeAnimationData Shared` (with `float Duration`) and `float TimeGoal`. The solver drives `State.TimeCurrent` toward `TimeGoal` and derives `DeploymentState`.

### Capability detection

- `part.SubtreeModules.Get<KeyframeAnimationModule>().Length > 0` ⇒ animatable.
- `animSpan[0].ShowDeployRetract` (public bool) ⇒ a deploy/retract panel (vs a generic actuator).
- `part.SubtreeModules.Get<SolarPanel>().Length > 0` ⇒ solar panel.

### Part addressing gotcha

Use `Part.InstanceId` (uint, runtime-unique) as the lookup key, **not** `Part.Id` (string, can collide across instances of the same template). To resolve: iterate `VehicleProvider.GetAllVehicles()` → `v.Parts.Parts` matching `InstanceId`.

## Creating LightPart instances at runtime (its-so-shiny)

Uses the stock `"LightPart"` template — real scene lights consuming `PowerConsumer` power (distinct from engine-part LCD pixels in blinky). The key pattern: create all parts, **wire `TreeParent`/`TreeChildren` manually**, then rebuild the tree **once**.

```csharp
var template = ModLibrary.Get<PartTemplate>("LightPart");   // throws if missing
var part = new Part(partId, template);
part.PositionParentAsmb = new double3(px, py, pz);
part.Asmb2ParentAsmb = doubleQuat.Concatenate(qY, qXNeg);
part.Scale = new double3(s, s, s);

var root = vehicle.Parts.Root;
foreach (var p in createdParts) {
    p.TreeParent = root;
    root.TreeChildren.Add(p);                  // BOTH directions wired manually
}
// power + stage alignment (below), then a SINGLE rebuild:
vehicle.Parts = PartTree.CreateFromNewPartTree(root);
vehicle.UpdateVehicleConfiguration();
```

Destruction mirrors it: `connection.Disconnect()` each `part.Connections.ToArray()`, remove from `parent.TreeChildren` + null `TreeParent`, then `PartTree.CreateFromNewPartTree(root)` + `UpdateVehicleConfiguration()`.

### Powering the created lights

```csharp
var batteries = vehicle.Parts.Modules.Get<Battery>();    // dedup by battery.Parent.FullPart
// round-robin distribute consumers across battery anchors:
var batteryPart = batteryParts[i % batteryParts.Count];
createdParts[i].SetStage(batteryPart.Stage);             // stage alignment matters
Part.Connection.Connect(lightPart, batteryPart);         // bool; see SKILL.md resource-flow section
```

Distributing across K battery anchors cuts `PowerManager.PopulateGraph`/`CreateOrders` cost from ~O(N³) to ~O(N³/K²) (each consumer DFS only sees ~N/K consumers). Without a battery connection the light switch has no power and won't light.

## Cross-cutting

- After any R/G/B channel write, call `OnDataLoad(null)` on the color reference.
- `LightModule.TemplateData` is shared per template — clone per-instance or accept whole-template effect.
- Tree mutation: wire `TreeParent`+`TreeChildren`, set stages, connect power, **then** `PartTree.CreateFromNewPartTree(root)` once, then `UpdateVehicleConfiguration()`.
- See SKILL.md for the engine-part resource/connection flow and `PartTree.Merge`; this file covers lights/solar/animation specifically.
