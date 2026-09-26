# Debugging KSA Mod Internals at Runtime

## Context

The KSA decompiled sources in `decomp/ksa/` may be **outdated** — the actual running binary can have a very different internal structure. Field names like `PointLights`/`SpotLights` that appear in decompiled `PartTemplate.cs` may not exist in the binary at all.

When you cannot trust the decompiled source field names, use the runtime reflection dump strategy below.

---

## The Debug Strategy

1. Add a **Dbg button** to your ImGui window
2. Walk the object graph with reflection, printing type names and field values to the console
3. Run the game, trigger the dump, read the console log to discover the actual structure
4. Update your reflection helpers with the real field/type names
5. Remove or keep the Dbg button once confirmed working

---

## Discovering Fields on an Unknown Type

Use this helper to walk the full type hierarchy (including base classes) and print every field:

```csharp
private static readonly BindingFlags All =
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

private static void DumpAllFields(object obj, string indent = "")
{
    var type = obj.GetType();
    while (type != null && type != typeof(object))
    {
        foreach (var f in type.GetFields(All | BindingFlags.DeclaredOnly))
        {
            object? val = null;
            try { val = f.GetValue(obj); } catch { val = "<error>"; }
            string valStr = val is System.Collections.ICollection col
                ? $"[Count={col.Count}]" : val?.ToString() ?? "null";
            Console.WriteLine($"zippo: {indent}[{type.Name}] {f.Name} ({f.FieldType.Name}) = {valStr}");
        }
        type = type.BaseType;
    }
}
```

Key: use `BindingFlags.DeclaredOnly` with the loop so you don't see inherited fields twice.

---

## Discovering Components Inside a List Field

The actual binary may store typed components in a generic `List<IComponent>` or similar — not as separate named fields. This helper walks all parts, finds those with a non-empty `Components` list, and dumps each item's full type name and all its own fields:

```csharp
private static void DumpPartsWithComponents(Part part, string indent = "")
{
    var tmpl = part.Template;
    if (tmpl != null)
    {
        var compField = tmpl.GetType().GetField("Components", All);
        if (compField?.GetValue(tmpl) is System.Collections.IList comps && comps.Count > 0)
        {
            Console.WriteLine($"zippo: Part {part.Id} has Components[{comps.Count}]:");
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                Console.WriteLine($"zippo:   [{i}] {c.GetType().FullName}");
                var ctype = c.GetType();
                while (ctype != null && ctype != typeof(object))
                {
                    foreach (var f in ctype.GetFields(All | BindingFlags.DeclaredOnly))
                    {
                        object? fv = null;
                        try { fv = f.GetValue(c); } catch { fv = "<err>"; }
                        string fvs = fv is System.Collections.ICollection col
                            ? $"[Count={col.Count}]" : fv?.ToString() ?? "null";
                        Console.WriteLine($"zippo:     .{f.Name} ({f.FieldType.Name}) = {fvs}");
                    }
                    ctype = ctype.BaseType;
                }
            }
        }
    }
    var subs = part.SubParts;
    for (int i = 0; i < subs.Length; i++)
        DumpPartsWithComponents(subs[i], indent + "  ");
}
```

Hook it to a Dbg button in the ImGui window:

```csharp
if (ImGui.Button("Dbg##mymod"))
{
    var v = SelectedVehicle;
    if (v != null)
    {
        Console.WriteLine("mymod: === component dump ===");
        var parts = v.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
            DumpPartsWithComponents(parts[i]);
    }
}
```

---

## Case Study: Finding `LightModule+TemplateData`

### Problem

`PartTemplate` in the decompiled sources had `PointLights` and `SpotLights` list fields. The mod was written to use those via reflection. At runtime, all reflection calls returned `null` — the fields do not exist in the current binary.

### Dump Output (truncated)

```
zippo: Part CoreInternalA_Subpart_Assets1 has Components[4]:
zippo:   [0] KSA.PartModelModule+Template
zippo:   [1] KSA.LightModule+TemplateData
zippo:     .Type (LightType) = Point
zippo:     .Transform (TransformReference) = KSA.TransformReference
zippo:     .Range (FloatReference) = KSA.FloatReference
zippo:     .Intensity (FloatReference) = KSA.FloatReference
zippo:     .Color (ColorReference) = KSA.ColorReference
zippo:     .InnerAngle (FloatReference) = KSA.FloatReference
zippo:     .OuterAngle (FloatReference) = KSA.FloatReference
zippo:     .Id (String) =
zippo:   [2] KSA.IVASeat+IVASeatTemplate
zippo:   [3] KSA.IVASeat+IVASeatTemplate
```

### Conclusion

- Light data lives in `KSA.LightModule+TemplateData` items inside the `Components` list, not in named `PointLights`/`SpotLights` fields
- `Intensity` is a `FloatReference` — access the actual value via reflection on the `.Value` field
- `Color` is a `ColorReference` — set `.R`, `.G`, `.B` float fields, then call `.OnDataLoad(null)` to propagate the computed `float3` `Value`
- Filter components by exact type name:

```csharp
private static List<object> GetLightComponents(PartTemplate t)
{
    var result = new List<object>();
    var comps = GF(t, "Components") as System.Collections.IList;
    if (comps == null) return result;
    for (int i = 0; i < comps.Count; i++)
    {
        var c = comps[i];
        if (c?.GetType().FullName == "KSA.LightModule+TemplateData")
            result.Add(c);
    }
    return result;
}
```

---

## General Notes

- **`BindingFlags.DeclaredOnly` in a loop** is the correct way to walk a type hierarchy. Omitting it causes duplicates from inherited fields.
- **Nested types appear as `OuterClass+InnerClass`** in `GetType().FullName` — use that exact string when filtering by type name.
- **Struct fields need write-back** — if a field is a value type (struct), setting a field on the struct via reflection requires reading the struct, modifying it, and writing it back to its parent. Reference types do not need this.
- **`FloatReference` and `ColorReference`** appear to be mutable reference types with a `Value` field and an `OnDataLoad()` method that re-derives `Value` from component fields (e.g. `R`, `G`, `B`). Always call `OnDataLoad(null)` after editing individual channels.
- Save the output of a debug dump run to `zippo/DEBUG` (or similar) so it can be analyzed later without re-running the game.
