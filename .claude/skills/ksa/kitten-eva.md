# KittenEva (EVA / Spacewalking Kitten)

`KittenEva` is a concrete subclass of `Vehicle`. It is the spacewalking character—the equivalent of a Kerbal on EVA.

## Detection and Casting

```csharp
// Via is-pattern (preferred when you have a typed reference):
if (vehicle is KittenEva kitten) { ... }

// Via type name (when working with untyped Vehicle):
if (vehicle.GetType().Name == "KittenEva") { ... }

// From controlled vehicle:
var kitten = Program.ControlledVehicle as KittenEva;
```

## Accessing CharacterAvatar

The renderable and avatar are private fields accessed via reflection:

```csharp
using System.Reflection;

var renderableField = typeof(KittenEva).GetField("_renderable",
    BindingFlags.NonPublic | BindingFlags.Instance);
var renderable = renderableField?.GetValue(kitten); // type: KSA.KittenRenderable

var avatarField = typeof(KSA.KittenRenderable).GetField("_characterAvatar",
    BindingFlags.NonPublic | BindingFlags.Instance);
var avatar = avatarField?.GetValue(renderable) as KSA.CharacterAvatar;
```

> When the concrete type `KSA.KittenRenderable` is accessible, use it directly as shown above (preferred over `GetType()` on the value).

## Scaling

KittenEva visual size is controlled via `CharacterAvatar.Core.Scale` (a `float`). Scale of `0.01f` = 1:1 in-game. Multiply your factor by `0.01f`:

```csharp
var allFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
// (access renderable and avatar as above)
var coreField = avatar.GetType().GetField("Core", allFlags);
var core = coreField?.GetValue(avatar); // Core is a VALUE TYPE (struct) — must write back

var scaleField = core?.GetType().GetField("Scale", allFlags);
var scaleProp  = core?.GetType().GetProperty("Scale", allFlags);

if (scaleField != null && scaleField.FieldType == typeof(float))
{
    scaleField.SetValue(core, factor * 0.01f);
    coreField!.SetValue(avatar, core); // REQUIRED: write struct back to field
}
else if (scaleProp != null && scaleProp.PropertyType == typeof(float))
{
    scaleProp.SetValue(core, factor * 0.01f);
    coreField!.SetValue(avatar, core);
}
```

`vehicle.Parts.Parts` exists on KittenEva but has no visual effect; `Core.Scale` is the only effective scaling path.

## Body Animations

Play a body (movement) animation directly on the character model:

```csharp
avatar.Core.CharacterModel.SetAnimation(animation); // IAnimation
```

Available named animations via `avatar.Animations`:

```
MmuAnimations:
  .MmuIdleDefaultAnim
  .MmuMoveLeftLoopAnim
  .MmuMoveRightLoopAnim
  .MmuMoveForwardLoopAnim
  .MmuMoveBackwardLoopAnim
  .MmuMoveUpLoopAnim
  .MmuMoveDownLoopAnim

WalkingAnimations:
  .WalkingAnim
  .RunningAnim
```

## Facial Expressions

Expressions are driven by a `CatExpressionAnim` processor on the character model animation pipeline.

```csharp
using System.Linq;

// Get the expression processor
var expressionProcessor = avatar.Core.CharacterModel.AnimProcessors
    .OfType<KSA.CatExpressionAnim>()
    .FirstOrDefault();

if (expressionProcessor != null)
{
    expressionProcessor.ExpressionAnim    = animationRef;  // KSA.AnimationAssetRef
    expressionProcessor.ExpressionWeight  = weight;        // float 0.0–1.0
}
```

Available expression animation lists via `avatar.Expressions` (each is `List<AnimationAssetRef>`):

```
.Angry, .Awe, .Happy, .Sad, .Scared
```

Each list may have multiple variants — pick randomly via `list[random.Next(list.Count)]`.

### Expression Lifecycle Pattern

1. Set `ExpressionAnim` and `ExpressionWeight = 0f`
2. Each frame in `OnBeforeUi`, increment a timer and ease `ExpressionWeight` toward 1.0 (quadratic: `t*t`)
3. After duration expires, reset `ExpressionWeight` to 0 and clear `ExpressionAnim`

```csharp
// Ease-in example (quadratic, 250ms):
float easeInProgress = Math.Min(timer / 0.25f, 1.0f);
expressionProcessor.ExpressionWeight = easeInProgress * easeInProgress;
```
