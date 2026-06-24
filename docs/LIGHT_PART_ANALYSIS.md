# Light Part Analysis — how KSA renders part spotlights, and how to get a laser-tight beam

> Scope: how a vessel light part is defined (XML → mesh/material/texture), how that becomes an
> in-memory runtime light, how the light reaches the GPU, the exact cone-falloff math in the
> shader, **why turning the gatOS `spread` "way down" does not produce a pinpoint today**, and a
> concrete plan to give gatOS complete control over the beam. All file/line references are against
> the decompiled sources under `thirdparty/ksa/` and the gatOS sources, verified 2026-06-23.

---

## 0. TL;DR (read this first)

There are **three** separate things standing between you and a "laser", and only one of them is
what you suspected:

1. **The real blocker — the inner/outer-angle *swap*, not a clamp.** The original gatOS `spread`
   control wrote **only** the spotlight's **outer** half-angle (`OuterAngle`). The **inner**
   half-angle stays at the part's template default (22.5° for the standard spotlight, 13° for the
   floodlight). KSA's `Light.CreateSpotLight` contains `if (inner > outer) swap(inner, outer)`. So the
   moment you set the outer angle *below* the untouched inner angle, KSA **swaps them** — the
   effective cone width snaps back up to the old inner angle. Net effect: the beam refuses to get
   narrower than ~22.5° half-angle no matter how small you set it. **This is the "clamped minimum" you
   saw — it's actually the inner angle acting as a floor.** Fix: drive the inner angle down together
   with (or below) the outer angle. *Pure gatOS-side change, fully practical — **now implemented** as
   the `inner_angle`/`outer_angle` controls, see §7.*

2. **A hard render clamp exists but is not your problem.** `CreateSpotLight` clamps the outer angle
   to `[1e-5, 1.5697963]` rad ≈ `[0.00057°, 89.94°]`. The **minimum is essentially zero**, so the
   clamp does *not* stop a narrow beam. gatOS already mirrors this same range. (One agent pass
   misread the clamp as forcing the angle *up* to 90° — that is wrong; `Math.Clamp(x, lo, hi)`
   bounds `x` into the range, it does not push small values up to `hi`.)

3. **A shader floor on *brightness* of sub-degree cones (~0.8° half-angle).** The spot-falloff
   shader divides by `max(cos(inner) − cos(outer), SPOT_DENOM_EPSILON)` with
   `SPOT_DENOM_EPSILON = 1e-4`. Because `cos()` is almost flat near 0°, once the cone is narrower
   than ≈ **0.81° half-angle** the cone's entire angular span shrinks below that epsilon and the
   beam **dims toward black** rather than getting tighter. You can push *through* this with very high
   `brightness` (intensity is unbounded in gatOS and multiplies the result), but you cannot make a
   bright, razor-thin cone purely from angles without patching the (precompiled) shader. A
   **~0.8°–2° half-angle bright pinpoint is achievable**; an arc-minute laser is not, short of a
   shader patch.

Plus one conceptual caveat: **KSA spotlights only illuminate surfaces inside the cone — there is no
volumetric "beam in the air".** A "laser" here means a tight bright *dot* where the cone lands on
geometry, not a glowing line through vacuum. A visible beam would need a wholly separate line/volume
renderer (out of scope for the cone-angle controls).

**Implemented retrofit:** gatOS exposes both real KSA angles by their own names —
`lights/<n>/outer_angle` and `lights/<n>/inner_angle` (degrees) — and writing the outer angle also
pulls the inner down to stay `≤ outer` so the cone actually narrows (beats the swap). Details in §7.
A pure-XML laser example part (narrow from spawn, no runtime needed) lives in
`docs/examples/CoreLaserA{Assets,GameData}.xml`.

---

## 1. The two-file asset model (Assets vs GameData)

Every part is described by **two** XML documents that are merged by Id:

| File | Declares | Light-relevant content |
|---|---|---|
| `Content/Core/CoreElectricalAAssets.xml` | *Renderable* assets: the mesh atlas, the PBR material (with its 4 textures), `SubPart`s (mesh + material + view-mesh), and `Part` prefabs (subpart instances + connectors). | `SubPart` `…_SpotlightA`, `…_FloodlightA` bind a mesh to the shared `CoreElectricalA_Material`. `Part` prefabs `…_LightSmallA/B/C` place those subparts. |
| `Content/Core/CoreElectricalAGameData.xml` | *Gameplay/sim* data per Id: colliders, power, batteries, **and the `<Light>` definition**. | `SubPartGameData` `…_SpotlightA` / `…_FloodlightA` carry the `<Light>` block (the actual emitter). `PartGameData` `…_LightSmallA/B/C` carry `<PowerConsumer LightSwitch="true">` and the deploy animation. |

The same `Id` appears in both files; the loader fuses the renderable half (Assets) with the
gameplay half (GameData) into one part/subpart definition.

### 1.1 The emitter — `<Light>` in GameData

`CoreElectricalAGameData.xml:104-130`:

```xml
<SubPartGameData Id="CoreElectricalA_Subpart_SpotlightA">
    <Light>
        <Type>Spot</Type>
        <Transform><Position X="0.38" Y="0.21" Z="0"/></Transform>
        <Range Value="5"/>
        <Intensity Value="10"/>
        <Color R="1" G="1" B="1"/>
        <InnerAngle Value="0.392599"/>   <!-- 22.5° half-angle, the fully-lit core -->
        <OuterAngle Value="0.785398"/>   <!-- 45.0° half-angle, the cone edge      -->
    </Light>
</SubPartGameData>

<SubPartGameData Id="CoreElectricalA_Subpart_FloodlightA">
    <Light>
        <Type>Spot</Type>
        <Transform><Position X="0.338" Y="0" Z="0"/></Transform>
        <Range Value="3"/>
        <Intensity Value="10"/>
        <Color R="1" G="1" B="1"/>
        <InnerAngle Value="0.23"/>       <!-- 13.18° -->
        <OuterAngle Value="1.57"/>       <!-- 89.95° — basically a hemisphere flood -->
    </Light>
</SubPartGameData>
```

**Angles are radians. Both inner and outer are cone *half*-angles** (measured from the cone axis).
The prefabs that use these subparts:

- `CoreElectricalA_Prefab_LightSmallA` → uses `SpotlightA` (×1) — `Assets.xml:564`
- `CoreElectricalA_Prefab_LightSmallB` → uses `SpotlightA` (×2) — `Assets.xml:586,607`
- `CoreElectricalA_Prefab_LightSmallC` → uses `FloodlightA` (×1) — `Assets.xml:620`

So the two stock spotlight kinds and their **inner-angle floors** are:

| Part | Subpart | Inner (floor) | Outer (default spread) |
|---|---|---|---|
| LightSmallA, LightSmallB | `SpotlightA` | **22.5°** | 45° |
| LightSmallC | `FloodlightA` | **13.18°** | 89.95° |

That inner-angle column is exactly the half-angle below which `spread` currently appears to "stop
working" (see §5/§6).

### 1.2 The power gate — `<PowerConsumer LightSwitch>`

`CoreElectricalAGameData.xml:35-56` (LightSmallA):

```xml
<PartGameData Id="CoreElectricalA_Prefab_LightSmallA">
    <EditorTag Value="Lights"/>
    <PowerConsumer LightSwitch="true"><Consumed Watts="60" /></PowerConsumer>
    <KeyframeAnimationModule …>…</KeyframeAnimationModule>   <!-- the hinge deploy anim -->
    <Collider …/>
</PartGameData>
```

`LightSwitch="true"` is what makes the part a toggleable light and links the on/off state to power
draw. The `KeyframeAnimationModule` is the physical hinge/cover deploy animation — independent of
the beam (and already surfaced by gatOS as the co-located `goal`/`current`/`state` control).

### 1.3 The mesh / material / texture half (Assets)

`CoreElectricalAAssets.xml:3-10, 152-160`:

```xml
<MeshAtlas Path="Meshes/CoreElectricalA_MeshAtlas.glb" />

<PbrMaterial Id="CoreElectricalA_Material">
    <Diffuse      Path="Textures/CoreElectricalA_TextureAtlas_Diffuse.ktx2"  Category="Vessel" />
    <Normal       Path="Textures/CoreElectricalA_TextureAtlas_Normal.ktx2"   Category="Vessel" />
    <AoRoughMetal Path="Textures/CoreElectricalA_TextureAtlas_PBR.ktx2"      Category="Vessel" />
    <Emissive     Path="Textures/CoreElectricalA_TextureAtlas_Emissive.ktx2" Category="Vessel" />
</PbrMaterial>

<SubPart Id="CoreElectricalA_Subpart_SpotlightA">
    <PartModel Id="CoreElectricalA_Subpart_SpotlightA_Model">
        <Mesh Id="CoreElectricalA_Subpart_SpotlightA" />
        <Material Id="CoreElectricalA_Material" />
    </PartModel>
    <MeshView><Mesh Id="CoreElectricalA_Subpart_SpotlightA_VM" /></MeshView>
</SubPart>
```

Two things to keep distinct, because they are completely different rendering systems:

- **The glowing lens/bulb you see on the lamp body** is the **Emissive texture**
  (`…_TextureAtlas_Emissive.ktx2`) baked into the PBR material on the mesh. It is a self-lit texel
  on the mesh surface; it does **not** illuminate anything and has **no angle**. It glows the same
  whether or not the cone is hitting a wall. Its **color is recolorable** — there's a per-instance
  emissive RGB tint the engine already uses for battery status lights — but it is *not* wired to
  light parts today and is a separate knob from the cast-light color. See **§10** for whether/how
  gatOS could drive it.
- **The cone of light cast onto other surfaces** is the dynamic `<Light>` emitter (§1.1), a separate
  punctual light fed to the deferred lighting pass. **This is the only thing `spread` affects.**
  Meshes/textures/the GLB are irrelevant to the beam width.

`MeshAtlas`/`PartModel`/`MeshView` are the geometry binding (one GLB atlas, a render model = mesh +
material, and a low-poly "view mesh" for the editor/icons). None of it participates in cone width,
so it is not pursued further here.

---

## 2. XML → in-memory: `LightModule.TemplateData`

`thirdparty/ksa/KSA/LightModule.cs:11-53` — the `<Light>` block deserializes into a nested
`TemplateData` (XML type name `"Light"`):

```csharp
[XmlType(TypeName = "Light")]
public class TemplateData : TemplateDataBase
{
    public enum LightType { Spot, Point }
    [XmlElement("Type")]       public LightType Type;
    [XmlElement("Transform")]  public TransformReference Transform = new();
    [XmlElement("Range")]      public FloatReference Range     = new(1f);
    [XmlElement("Intensity")]  public FloatReference Intensity = new(1f);
    [XmlElement("Color")]      public ColorRgbReference ColorRgb = new(Color.Gray);
    [XmlElement("InnerAngle")] public FloatReference InnerAngle = new((float)Math.PI / 8f);  // 22.5°
    [XmlElement("OuterAngle")] public FloatReference OuterAngle = new((float)Math.PI / 4f);  // 45°
    [XmlElement("RayTracing")] public bool RayTracing = false;
    …
}
```

- Angles are `FloatReference` (a boxed mutable `float`, `.Value`) in **radians**.
- **Defaults matter:** even a `<Light>` with no `<InnerAngle>` gets `π/8 = 22.5°`. So the inner-angle
  floor is *always present*, whether the XML states it or not.

`CreateComponents` (`LightModule.cs:67-84`) instantiates one `LightModule` per `<Light>` and — key
detail — **passes the *shared* `TemplateData` reference** straight from the part template into every
instance. Multiple identical lights on a vessel point at the *same* `TemplateData` object. (This is
why gatOS must clone before per-instance edits — §6.)

---

## 3. Per-frame: `LightModule.UpdateRenderData` → `Light.CreateSpotLight`

`thirdparty/ksa/KSA/LightModule.cs:86-129`. Each frame, for each enabled light, the module:

1. Early-outs if the part's `LightSwitch` is off or unpowered (`:88-96`).
2. Computes the world transform; for spots, derives the beam direction from the rotation
   (`double3.UnitX` rotated, `:114-116`).
3. Builds the runtime `Light` (`:117`):

```csharp
Light light = Light.CreateSpotLight(
    Template.Transform.PositionValue.Transform(matrix),
    double5,                 // beam direction
    Template.Range,
    Template.OuterAngle,     // ← radians, from template
    Template.InnerAngle,     // ← radians, from template
    Template.ColorRgb,
    Template.Intensity,
    ELightFlags.CastsShadows | ELightFlags.SoftShadows);
…
Program.LightSystem.CreateLightInstance(light, viewport);
```

The runtime light is **rebuilt from the template every frame**, so editing `Template.OuterAngle`/
`Template.InnerAngle` takes effect immediately — and **the swap/clamp in `CreateSpotLight` runs
every frame** on whatever the template currently holds (§4). There is no cached "validated" copy to
bypass it.

(`RayTracing="false"` for these parts, so they go through the raster `LightSystem`, not the RT
lists at `:55-57,102-104,118-120`.)

---

## 4. The runtime `Light` struct — the swap and the clamp

`thirdparty/ksa/KSA.Rendering.Lighting/Light.cs`:

```csharp
public struct Light
{
    private const float MAX_OUTER_ANGLE = 1.5697963f;   // ≈ 89.9433°, just under π/2
    private const float MIN_OUTER_ANGLE = 1E-05f;       // ≈ 0.000573°
    …
    public float InnerAngle;     // radians (pre-GPU)
    public float OuterAngle;     // radians (pre-GPU)

    public static Light CreateSpotLight(double3 inPosition, double3 inDirection, float inRange,
        float inOuterAngle, float inInnerAngle, float3 inColor,
        float inIntensity = 1f, ELightFlags inFlags = ELightFlags.None)
    {
        if (inInnerAngle > inOuterAngle)                 // ← (1) THE SWAP
        {
            float num = inOuterAngle;
            inOuterAngle = inInnerAngle;
            inInnerAngle = num;
        }
        inOuterAngle = float.Clamp(inOuterAngle, 1E-05f, 1.5697963f);  // ← (2) outer clamp
        inInnerAngle = float.Clamp(inInnerAngle, 0f, inOuterAngle);    // ← (3) inner ≤ outer
        return new Light { Type = ELightType.Spot, …,
            OuterAngle = inOuterAngle, InnerAngle = inInnerAngle };
    }
}
```

Three behaviours, in order:

1. **Swap (lines 56-61).** If the caller's inner > outer, they are exchanged. This is the trap the
   original `spread`-only path fell into: it lowered `OuterAngle` but left `InnerAngle` at 22.5°/13°.
   As soon as the outer angle dropped below the inner angle, `inner > outer` became true → they swap →
   the **effective outer angle becomes the old inner angle**. The cone stops narrowing. *This is the
   apparent "minimum".* (gatOS now lowers inner alongside outer — §7 — so the swap never fires.)
2. **Outer clamp (line 62).** Bounds the outer half-angle to `[1e-5, 1.5697963]` rad. The lower
   bound is ~0.0006° — effectively zero — so the clamp itself is **not** what blocks a narrow beam.
   The upper bound (≈89.94°) is why a "spotlight" can never be a full 180° area light.
3. **Inner ≤ outer (line 63).** Inner is finally clamped into `[0, outer]`. So if you *do* drive
   inner down alongside outer, they stay consistent.

### 4.1 Angles → cosines for the GPU

`Light.cs:81-106` packs the struct for the GPU and **converts the half-angles to cosines** (points
send `0` as a sentinel):

```csharp
return new LightShaderData {
    …
    OuterAngle = (Type == ELightType.Spot) ? float.Cos(OuterAngle) : 0f,   // cos(outer)
    InnerAngle = (Type == ELightType.Spot) ? float.Cos(InnerAngle) : 0f,   // cos(inner)
    …
};
```

GPU struct (`KSA.Rendering.Lighting.ShaderStructs/LightShaderData.cs`, `Pack=1`, mirrored on the
GLSL side in `Content/Core/Shaders/Lighting/LightData.glsl:19-30`):

```glsl
struct LightData {
    vec3 position;  float range;
    vec3 direction; float outerAngle;  // = cos(outerAngle)
    vec3 color;     float intensity;
    float innerAngle;                  // = cos(innerAngle)
    uint type;      uint flags;  uint shadowIndex;
};
```

So in shader-land: `outerAngle` and `innerAngle` are **cosines**, and a narrower cone means a
**larger** cosine (closer to 1).

---

## 5. The GPU cone falloff — and the brightness floor on tiny cones

The lights are evaluated in the clustered deferred pass
`Content/Core/Shaders/Lighting/LightPrePass.comp`. Constants (`:35-38`):

```glsl
const float RANGE_EPSILON      = 1e-6;
const float DIST_EPSILON       = 1e-12;
const float ATT_EPSILON        = 1e-7;
const float SPOT_DENOM_EPSILON = 1e-4;   // ← the one that bites narrow cones
```

Spot attenuation (`:289-296`):

```glsl
float isSpot  = light.type == LIGHT_TYPE_SPOT ? 1.0 : 0.0;
float cosAng  = dot(light.direction, -lightDir);                         // cos(angle off axis)
float denom   = max(light.innerAngle - light.outerAngle, SPOT_DENOM_EPSILON);
float spotAtt = saturate((cosAng - light.outerAngle) / denom);          // 1 inside inner, 0 outside outer
spotAtt      *= spotAtt;                                                 // squared (smoother edge)
float att     = rangeAtt * mix(1.0, spotAtt, isSpot) * light.intensity; // final multiply
```

This is the standard glTF/Frostbite punctual-spot model, but with the `1/(cosInner − cosOuter)`
term computed live and floored by `SPOT_DENOM_EPSILON` instead of precomputed. There is **no
`spotScale`/`spotOffset` precompute**, so the only narrow-cone guard is that `max(..., 1e-4)`.

**Why sub-degree cones go dark.** Take the laser case — both angles tiny and roughly equal, say
both `= t` rad. Then `cosInner − cosOuter ≈ 0`, so `denom = 1e-4`. On the cone axis `cosAng = 1`
and `cosOuter = cos(t) ≈ 1 − t²/2`, so:

```
spotAtt(center) ≈ saturate( (t²/2) / 1e-4 )² = saturate( t² / 2e-4 )²
```

- Full brightness at center requires `t²/2 ≥ 1e-4` → **t ≥ √(2·1e-4) ≈ 0.0141 rad ≈ 0.81°.**
- At `t = 0.4°` → `spotAtt ≈ 0.06` (≈6% of brightness).
- At `t = 0.06°` → `spotAtt ≈ 2.5e-5` (effectively black).

So **≈0.8° half-angle (≈1.6° full cone) is the tightest cone that is still fully bright** from
angles alone. Below that the beam fades because its entire angular span is smaller than the
`1e-4` cosine epsilon. Because `att` multiplies linearly by `light.intensity`, you can **compensate
by cranking `brightness`** (gatOS exposes intensity unbounded): e.g. `intensity ≈ 1/spotAtt` keeps a
0.4° cone visible. Push far enough and you get an arbitrarily small dot, at the cost of huge
intensity numbers and a sub-pixel target. A truly clean narrow beam without the intensity hack would
require lowering `SPOT_DENOM_EPSILON` — i.e. **patching the shader**, which is precompiled SPIR-V
shipped in `Content/Core/Shaders/` and not something gatOS edits.

**No volumetric beam.** `LightPrePass.comp` accumulates *surface irradiance* only
(`diffuseIrradiance`/`specularIrradiance`, `:263-264`). Nothing in this path scatters light in the
empty space between the lamp and the surface, so a narrow spot reads as a bright *dot on geometry*,
never a visible shaft in vacuum. Volumetric scattering in KSA is atmosphere/sun-scale, not wired to
these per-part punctual lights. A laser *line* would be a separate renderer entirely.

---

## 6. The original gatOS `spread` design — and why it failed

The first cut exposed a single writable `lights/<n>/spread` node that mapped only to `OuterAngle`
(degrees). In code terms it touched `OuterAngle` and nothing else — the template's `InnerAngle`
stayed at its default (22.5° for SpotlightA, 13° for FloodlightA). So:

- Setting `spread` between the inner angle and 89.94° narrowed the cone as expected (45° → ~22.5°).
- Setting `spread` **below** the inner angle did nothing visible: `CreateSpotLight` swapped inner and
  outer (§4) and the rendered cone was pinned at the inner angle. **← the "laser won't work /
  clamped minimum" report.** The clamp `MinOuterAngleRad = 1e-5` was *not* the limiter (it faithfully
  mirrors KSA and is ~0°); the **untouched inner angle** was.

That diagnosis drove the design below: a made-up "spread" name hiding the outer angle, with no way to
move the inner angle, can never produce a pinpoint. The fix is to expose **both** real angles and
keep them consistent.

---

## 7. Implemented design — expose the real `inner_angle` / `outer_angle`

Rather than invent names (`spread`/`softness`/`focus`), gatOS now mirrors KSA's own fields. Two
writable nodes per light, **in degrees** (rad↔deg only at the gatOS boundary; KSA still stores
radians):

| `/sim` node | Action | Maps to | Semantics |
|---|---|---|---|
| `lights/<n>/outer_angle` | `light.outer_angle` | `LightModule.Template.OuterAngle.Value` | outer cone half-angle (hard edge); clamped to `[1e-5, 1.5697963]` rad; **also lowers `InnerAngle` to ≤ outer** so narrowing it actually narrows the cone (beats the swap) |
| `lights/<n>/inner_angle` | `light.inner_angle` | `LightModule.Template.InnerAngle.Value` | inner cone half-angle (full-brightness core); clamped to `[0, current outer]` |

End-to-end (each piece a parity-clean echo of the existing light controls):

- **Snapshot** — `LightSnapshot.OuterAngleDeg` + `InnerAngleDeg` (`SimSnapshot.cs`).
- **Reader** — `VesselReader.SampleLights` reads both `Template.{Outer,Inner}Angle.Value * RadToDeg`.
- **Actuator** — `LightActuator.SetOuterAngle` (clamps outer, then `if (inner > outer) inner = outer`)
  and `SetInnerAngle` (clamps to `[0, outer]`). `EnsureUnshared` now clones **both** `OuterAngle`
  *and* `InnerAngle` (red-alert pattern), so a per-light edit never reshapes every identical light.
- **Routing** — `KsaCatalog`: `"light.outer_angle"`/`"light.inner_angle"` (Frame phase; not solver).
- **Tree** — `SimFsTree.LightDir` emits the two `NumberControl`s.
- **Transports** — HTTP `/v1` and MQTT pick both up by construction (reads via `SimJson`/`Formats`,
  writes via `SimCommand`/`CommandQueue`); the TS SDK gains `setOuterAngle`/`setInnerAngle`.
- **SPEC / matrix** — `SPEC_9P_FILESYSTEM.md` + `docs/KSA_INTEGRATION_MATRIX.md` updated in lockstep.

**Making a laser at runtime:** drop the outer angle low and the inner with it, e.g.

```sh
echo 1.2 > /sim/vessels/by-id/<id>/lights/<n>/outer_angle   # ~1.2° hard edge
echo 0.6 > /sim/vessels/by-id/<id>/lights/<n>/inner_angle   # tight bright core
# optionally crank brightness to punch a sub-degree cone through the shader floor (§5)
echo 5000 > /sim/vessels/by-id/<id>/lights/<n>/brightness
```

Above ~0.8° outer half-angle the cone stays fully bright; below that, raise `brightness` to
compensate (§5). The pure-XML laser part in `docs/examples/CoreLaserA*.xml` ships these tight values
baked in, so it is narrow from spawn with no runtime calls at all. It also **retains the stock
small-spotlight deploy animation** by reusing that GLB: KSA binds a keyframe animation to subparts by
matching each subpart *instance Id* against the node names baked into the `.glb`
(`KeyframeAnimationData.PartLookup` → `ApplyAnimationTransforms`), so the example gives its subpart
instances the exact Ids the stock `CoreElectricalA_Prefab_LightSmallA_Anim.glb` drives
(`…_SpotlightAHinge1/2`, `…_SpotlightA1`, `…_LightMountA1`) — safe because instance Ids are
**per-Part scoped** (`PartTemplate.cs`). Only the head's `InstanceOf` is re-pointed at the laser
emitter. Because the part then has a `KeyframeAnimationModule`, gatOS also surfaces its
`lights/<n>/{goal,current,state}` deploy control.

### Not done (deliberately) — sub-degree bright beams & volumetric shafts

The `~0.8°` brightness floor is `SPOT_DENOM_EPSILON = 1e-4` inside the precompiled
`LightPrePass.comp` (§5). Beating it *brightly* without the intensity hack would mean shipping a
patched core shader — high risk (render correctness + KSA churn) and out of scope here. A *visible
beam in vacuum* (volumetric shaft / line) is a separate renderer entirely; KSA spotlights only light
surfaces. Both remain available as future work if the intensity-compensation path proves insufficient.

---

## 9. File reference index

| Concern | File | Lines |
|---|---|---|
| `<Light>` emitter XML | `thirdparty/ksa/Content/Core/CoreElectricalAGameData.xml` | 104-130 |
| `PowerConsumer`/`LightSwitch` XML | same | 35-102 |
| Mesh/material/texture binding | `thirdparty/ksa/Content/Core/CoreElectricalAAssets.xml` | 3-10, 152-160, 551-637 |
| `TemplateData` (XML→memory), defaults | `thirdparty/ksa/KSA/LightModule.cs` | 11-53 |
| Shared-template instancing | same | 67-84 |
| Per-frame rebuild → `CreateSpotLight` | same | 86-129 |
| **Swap + clamp** | `thirdparty/ksa/KSA.Rendering.Lighting/Light.cs` | 54-79 |
| Angles → cosines (GPU pack) | same | 81-106 |
| GPU struct (GLSL) | `thirdparty/ksa/Content/Core/Shaders/Lighting/LightData.glsl` | 19-30 |
| **Cone falloff + epsilon** | `thirdparty/ksa/Content/Core/Shaders/Lighting/LightPrePass.comp` | 35-38, 289-296 |
| gatOS read (inner/outer angle) | `gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs` | `SampleLights` |
| gatOS write (inner/outer angle) | `gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs` | `SetOuterAngle`/`SetInnerAngle` |
| gatOS `/sim` nodes | `gatOS.SimFs/SimFsTree.cs` | `LightDir` |
| gatOS snapshot fields | `gatOS.SimFs/Snapshots/SimSnapshot.cs` | `LightSnapshot.{Outer,Inner}AngleDeg` |
| gatOS routing | `gatOS.GameMod/Game/Ksa/KsaCatalog.cs` | `light.{outer,inner}_angle` |
| SPEC entry | `SPEC_9P_FILESYSTEM.md` | lights / actions tables |
| Pure-XML laser example | `docs/examples/CoreLaserA{Assets,GameData}.xml` | — |
| Emissive tint (per-instance) | `thirdparty/ksa/KSA/PartModelModule.cs` | 79-144 |
| Emissive tint GPU field | `thirdparty/ksa/KSA/PartModel.cs` | `PerInstanceData.EmissiveColor` |
| Emissive shader (replace path) | `thirdparty/ksa/Content/Core/Shaders/Mesh/MeshIndirect.frag` | ~164-181 |

---

## 10. Recoloring the emissive lens (the glowing bulb) — separate from the cast-light color

**Short answer: yes, it is technically possible, but only through a render-path Harmony patch — it is
*not* an existing data field we can set like `light.color`.** The cast-light color (`light.color` →
`LightModule.Template.ColorRgb`) and the lens *emissive* color are two unrelated systems; recoloring
the emissive lens needs a different mechanism than the one gatOS uses today.

### 10.1 How emissive is rendered

The PBR material's GPU struct carries **only an emissive texture index** — no per-material emissive
color or strength (`MaterialData.cs` / `MaterialSet.glsl`: `albedoColor` tints diffuse, but there is
no `emissiveColor`). Emissive contribution is computed per-pixel in the mesh fragment shader
(`Content/Core/Shaders/Mesh/MeshIndirect.frag` ~164-181, GLSL compiled to SPIR-V at runtime by
`Brutal.ShaderCompiler`; `EMISSIVE_MULTIPLIER = 1.25` in `Common/Lighting.glsl`):

```glsl
if (emissive && drawData.emissiveTextureIndex >= 0) {
    float sampledEmissive = texture(... emissiveTextureIndex ...).x;   // grayscale mask from the atlas
    if (sampledEmissive != 0.0) {
        if (addEmissiveColor) {                                        // ← state-flag bit 7
            vec3 unpacked = unpackRGB(emissiveColor);                  // per-INSTANCE packed RGB
            lightColor += gammaToLinear(unpacked * EMISSIVE_MULTIPLIER);
        } else {
            lightColor += gammaToLinear(vec3(sampledEmissive) * EMISSIVE_MULTIPLIER);  // raw white-ish
        }
    }
}
```

So there **is** a per-instance emissive RGB tint. It lives in the per-draw instance data, not the
material:

- `PartModel.PerInstanceData { float4x4 ModelMatrix; int StateBitFlag; uint EmissiveColor; … }`
  (`PartModel.cs`) — `EmissiveColor` is RGB packed `0xRRGGBB`.
- The shader uses it **only when `StateBitFlag` bit 7 (`0x80`) is set** (`addEmissiveColor`). When the
  bit is clear, emissive falls back to the texture's grayscale (the default white-ish glow you see).

### 10.2 Why the lens is always white today

`PartModelModule.UpdateRenderData` (`PartModelModule.cs:79-144`) builds `PerInstanceData` **every
frame** and sets bit 7 + a real `EmissiveColor` in exactly **one** case — a part that has a `Battery`
module with `HasStatusLight` (the red→yellow→green charge indicator):

```csharp
Color.Preset preset = Color.Black;
if (Parent.FullPart.Modules.TryGetTypeList(out Module<Battery>.List batt) && batt[0].HasStatusLight) {
    num |= 0x80;                       // turn on the tint
    preset = /* gradient from charge fraction */;
}
…
EmissiveColor = PackByte3(preset.AsByte3…)   // 0 (black) for everything else
```

A light part has no battery, so bit 7 is never set and `EmissiveColor` stays 0 → the shader takes the
raw-texture branch → the lens glows its baked white-ish color regardless of `light.color`. There is
**no game-state field** on a light part that feeds emissive, which is why we cannot reach it the way
we reach `OuterAngle`/`ColorRgb` (mutate a shared template that the engine re-reads each frame).

### 10.3 What it would take in gatOS (plan)

Because the only knob is computed inside the per-frame render-data build, driving it means a **Harmony
patch on `PartModelModule.UpdateRenderData`** (a render-path patch — a heavier, higher-churn
integration than the existing template-field actuators, but Harmony is already used in `GameMod`).
Sketch:

1. **Command + override store.** New action `light.emissive_color` (values `[r,g,b]`, Frame phase),
   routed through the one `SimCommand`/`CommandQueue` like every other control. Its actuator records
   the desired RGB (and an on/off) in a `ConditionalWeakTable<Part, …>` (or keyed by the light part),
   on the game thread — no render mutation here.
2. **Render hook.** Patch `UpdateRenderData` so that, for a part present in that override table, it
   sets `StateBitFlag |= 0x80` and `EmissiveColor = packed RGB` before `PartModel.AddInstance`.
   Cleanest forms: a **transpiler** injecting one `GatosEmissive.Apply(Parent, ref num, ref preset)`
   call right before the `byte3 asByte = preset.AsByte3;` line, or a **prefix that reimplements** the
   ~15-line flag/instance build and returns `false` (more code duplicated, but no IL surgery). Confine
   it to `gatOS.GameMod/Game/Ksa/` per the G2 rule.
3. **Read-back + parity.** Add `LightSnapshot.EmissiveColor`, a reader that reports the active override
   (or "default" when none — KSA exposes no current emissive value to read), a `lights/<n>/emissive_color`
   `VectorControl`, the `KsaCatalog` route, and the `SPEC_9P_FILESYSTEM.md` + `KSA_INTEGRATION_MATRIX.md`
   rows. HTTP/MQTT/SDK light up by construction (transport-parity rule).

Which subpart(s) to tint: apply to the light part's `PartModelModule`(s) — the shader gates on
`sampledEmissive != 0`, so only the texels that are actually emissive (the lens) recolor; the rest of
the mesh is untouched. Gate the override on `LightSwitch` being active if you want the tint to follow
the on/off state (matches the cast light).

### 10.4 Limitations (call these out before building it)

- **Flat color, not tinted-grayscale.** The `addEmissiveColor` branch *replaces* the texture value
  with a flat `unpackRGB(emissiveColor) * 1.25`; it does **not** multiply by the sampled magnitude. So
  the lens becomes a uniform color patch, losing any brightness gradient the emissive texture had.
- **8-bit, LDR.** `EmissiveColor` is `0xRRGGBB` (each channel 0..1), times the fixed
  `EMISSIVE_MULTIPLIER = 1.25`. No HDR/strength channel; you can't make it arbitrarily bright, only
  recolor at roughly the stock intensity.
- **Render-thread Harmony patch.** Unlike the angle/color actuators (game-state template writes), this
  hooks a hot per-frame render method — higher churn risk (bit layout, method shape) and must read the
  override table lock-free. It is the only available injection point; there is no per-material or
  per-part emissive-color data to set instead.
- **Per-instance only.** Tints a specific part instance, exactly like the battery status light — fine
  for our use (per-light control), and it won't bleed onto other lights sharing the material.

### 10.5 Dead-end: the "fake battery" XML shortcut

A tempting shortcut is to give the light part a `<Battery HasStatusLight="true">` so the stock code
flips on the emissive-color path. It **does** set bit `0x80` — but it does **not** give a controllable
color, so it is not usable:

- The color in that branch is **derived from `Battery.FilledFraction`** (empty→red, mid→yellow,
  full→green), computed in code. There is **no XML attribute for the color**, and you can't pin the
  fraction — a battery charges to full on a powered vessel, so you'd get a **green lens that drifts**
  with charge, not a chosen color, and it would contradict `light.color`.
- It has **side effects**: the part becomes a real battery (adds capacity / electrical behaviour).
- It **doesn't reduce the patch work** (§10.3): `EmissiveColor` is still a local computed in
  `UpdateRenderData`, so a chosen color still needs the Harmony override at the same point — the
  battery only pre-sets the enable bit. The override would in fact have to *replace* the battery's
  charge color.

So the fake-battery route can only produce a charge-driven green/red glow, never an author-chosen or
beam-matched emissive color. Stick with the §10.3 per-instance override.
