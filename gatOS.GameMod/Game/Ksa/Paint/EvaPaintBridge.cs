using System.Collections;
using System.Reflection;
using Brutal.Numerics;
using gatOS.Paint;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>
/// Rebinds supported EVA render slots to gatOS-owned material clones. Stock materials are never
/// mutated; every changed array slot is conditionally restored and every owned GPU handle freed.
/// </summary>
internal static class EvaPaintBridge
{
    private static readonly FieldInfo EvaRenderable = typeof(KittenEva).GetField("_renderable",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AvatarField = typeof(KittenRenderable).GetField("_characterAvatar",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Dictionary<string, Binding> Bindings = new(StringComparer.Ordinal);
    private static readonly Dictionary<CloneKey, Clone> Clones = [];
    private static FieldInfo? _assetMapField;
    private static int _peak;

    internal static void Tick(PaintStore store)
    {
        try
        {
            foreach (var clone in Clones.Values) clone.Uses = 0;
            var live = new HashSet<string>(StringComparer.Ordinal);
            var materialNames = new HashSet<string>(StringComparer.Ordinal);
            if (Universe.CurrentSystem is { } system)
                foreach (var astronomical in system.All.UnsafeAsList())
                    if (astronomical is KittenEva eva)
                    {
                        live.Add(eva.Id);
                        Apply(eva, store, materialNames);
                    }

            foreach (var id in Bindings.Keys.Where(id => !live.Contains(id)).ToArray())
            {
                Restore(Bindings[id]);
                Bindings.Remove(id);
            }
            foreach (var key in Clones.Where(x => x.Value.Uses == 0).Select(x => x.Key).ToArray())
                RemoveClone(key);

            _peak = Math.Max(_peak, Clones.Count);
            store.PublishRuntime(s => s with
            {
                KittensStatus = KittenPaintStatus.Active,
                MaterialNames = materialNames.Order(StringComparer.Ordinal).ToArray(),
                ActiveKittenBindings = Bindings.Values.Sum(b => b.Slots.Count(s => s.CloneHandle >= 0)),
                LiveMaterialClones = Clones.Count,
                PeakMaterialClones = _peak,
                KittenError = "",
            });
        }
        catch (Exception ex)
        {
            Disable(store);
            store.SetKittensMaster(false);
            store.PublishRuntime(s => s with { KittensStatus = KittenPaintStatus.Degraded, KittenError = ex.Message });
        }
    }

    internal static void Disable(PaintStore store)
    {
        foreach (var binding in Bindings.Values) Restore(binding);
        Bindings.Clear();
        foreach (var key in Clones.Keys.ToArray()) RemoveClone(key);
        store.PublishRuntime(s => s with
        {
            KittensStatus = KittenPaintStatus.Disabled,
            MaterialNames = [],
            ActiveKittenBindings = 0,
            LiveMaterialClones = 0,
        });
    }

    private static void Apply(KittenEva eva, PaintStore store, ISet<string> names)
    {
        var renderable = EvaRenderable.GetValue(eva) ?? throw new InvalidOperationException("KittenEva._renderable is null");
        var avatar = (CharacterAvatar)(AvatarField.GetValue(renderable)
            ?? throw new InvalidOperationException("KittenRenderable._characterAvatar is null"));
        if (!Bindings.TryGetValue(eva.Id, out var binding) || !ReferenceEquals(binding.Avatar, avatar))
        {
            if (binding is not null) Restore(binding);
            binding = new Binding(avatar, BuildSlots(avatar));
            Bindings[eva.Id] = binding;
        }

        foreach (var slot in binding.Slots)
        {
            names.Add(slot.Name);
            var rule = store.ResolveKitten(eva.Id, slot.Name);
            if (rule is null)
            {
                RestoreSlot(slot);
                continue;
            }
            var key = new CloneKey(slot.OriginalHandle, PaintBits.Encode(rule.Color));
            if (!Clones.TryGetValue(key, out var clone))
            {
                if (Clones.Count >= store.Current.MaterialCloneCap)
                    throw new InvalidOperationException($"EVA material clone cap {store.Current.MaterialCloneCap} reached");
                clone = CreateClone(key, rule.Color);
                Clones.Add(key, clone);
            }
            clone.Uses++;
            if (slot.CloneHandle >= 0 && slot.CloneHandle != clone.Asset.Handle
                && slot.Array[slot.Index] == slot.CloneHandle)
                slot.Array[slot.Index] = slot.OriginalHandle;
            slot.Array[slot.Index] = clone.Asset.Handle;
            slot.CloneHandle = clone.Asset.Handle;
        }
    }

    [KsaAnchor("KittenEva._renderable -> KittenRenderable._characterAvatar; CharacterAvatar Core/Fur/Attachments; protected MaterialIndices",
        SourceFile = "KSA/KittenEva.cs / KittenRenderable.cs / CharacterAvatar.cs / *Renderable.cs",
        Verified = "2026-08-23", GameVersion = "2026.8.22.5348", Risk = ChurnRisk.High,
        Notes = "5348: the MMU changed asset and shape. Content/Core/CharacterAssets.xml swapped "
            + "Characters/KittenMMU/KSA_Cat_MMU.gltf for the skinned SK_KSA_MMU.glb, so "
            + "Attachments.Mmu.MmuMesh is retyped StaticMeshRenderable -> AnimatedRenderable (the "
            + "reflected MaterialIndices walk still resolves, since FindField searches the base chain), "
            + "and the two <Materials> blocks were REORDERED — KSA_MMU_Color is now index 0 and "
            + "KSA_MMU_Texts index 1, the reverse of before. Because slots are named by array ordinal, "
            + "a saved rule targeting 'mmu' now repaints the MMU body instead of the label decals; the "
            + "array LENGTH is a live check, since the .glb is not in the repo.")]
    private static List<Slot> BuildSlots(CharacterAvatar avatar)
    {
        var slots = new List<Slot>();
        AddRenderable(slots, avatar.Core.CharacterModel, "body", avatar.Core.ScleraMeshIndices);
        AddRenderable(slots, avatar.Fur.CatFurRenderable, "fur", null);
        AddRenderable(slots, avatar.Attachments.Helmet.HelmetMesh, "helmet", null);
        AddRenderable(slots, avatar.Attachments.Helmet.VisorMesh, "visor", null);
        AddRenderable(slots, avatar.Attachments.Mmu.MmuMesh, "mmu", null);
        return slots;
    }

    private static void AddRenderable(List<Slot> slots, object? renderable, string category,
        ICollection<int>? excluded)
    {
        if (renderable is null) return;
        var field = FindField(renderable.GetType(), "MaterialIndices")
            ?? throw new MissingFieldException(renderable.GetType().FullName, "MaterialIndices");
        var array = (int[])field.GetValue(renderable)!;
        var ordinal = 0;
        for (var i = 0; i < array.Length; i++)
        {
            if (excluded?.Contains(i) == true) continue;
            var name = ordinal++ == 0 ? category : $"{category}.{ordinal - 1}";
            slots.Add(new Slot(array, i, name, array[i]));
        }
    }

    [KsaAnchor("GpuMaterialSystem.CreateObject; AssetManager.AssetMap; GpuObjectAssetRef.Dispose -> Free; MaterialData layout",
        SourceFile = "KSA/GpuMaterialSystem.cs / GpuObjectSystem.cs / AssetManager.cs / MaterialData.cs",
        Verified = "2026-08-15", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "Pool capacity is fixed at 512; stock buffer is not TransferSrc and must not be read back.")]
    private static Clone CreateClone(CloneKey key, PaintColor color)
    {
        var system = Program.Instance.MaterialSystem;
        var source = FindAsset(key.SourceHandle)
            ?? throw new InvalidOperationException($"source material handle {key.SourceHandle} is not registered");
        var data = BuildMaterialData(source);
        data.AlbedoColor = new float4((float)color.R, (float)color.G, (float)color.B, 1f);
        var name = $"gatOS.Paint/{key.SourceHandle}/{unchecked((uint)key.ColorBits):X8}";
        if (!system.CreateObject(name, data))
            throw new InvalidOperationException($"could not allocate EVA material clone '{name}'");
        return new Clone(name, system.GetOrLoad(name));
    }

    private static MaterialData BuildMaterialData(GpuObjectAssetRef source)
    {
        var id = source.Id.ToString();
        var texture = Program.Instance.TextureSystem;
        if (id.EndsWith("_FurMaterial", StringComparison.Ordinal))
        {
            var parts = id[..^"_FurMaterial".Length].Split(',');
            if (parts.Length != 3) throw new InvalidOperationException($"unrecognized fur material '{id}'");
            var resources = Program.Instance.CharacterRenderResources;
            return new MaterialData
            {
                ExtraData = new float4(resources.FurTexture.BindlessHandle, resources.FurSampler.BindlessIndex,
                    texture.GetOrLoad(parts[2]).BindlessHandle, 0),
                NormalTexture = texture.GetOrLoad(parts[1]).BindlessHandle,
                RoughnessMetalScale = float4.One,
                AlbedoTexture = texture.GetOrLoad(parts[0]).BindlessHandle,
                RoughMetallicAOTexture = Program.Instance.SuperMeshRenderSystem.GltfSystem.BlankMaterialTexture.BindlessHandle,
                AlbedoColor = float4.One,
                Sampler = texture.SamplerRepeatHandle,
                EmissiveTexture = texture.DefaultBlackTexture.BindlessHandle,
            };
        }

        var material = ModLibrary.Get<PbrMaterialReference>(id).Get();
        return new MaterialData
        {
            AlbedoTexture = material.DiffuseReference?.Get().BindlessHandle ?? texture.DefaultWhiteTexture.BindlessHandle,
            Sampler = texture.SamplerRepeatHandle,
            AlbedoColor = float4.One,
            NormalTexture = material.NormalReference?.Get().BindlessHandle ?? texture.DefaultWhiteTexture.BindlessHandle,
            RoughMetallicAOTexture = material.PBRMap?.Get().BindlessHandle ?? texture.DefaultWhiteTexture.BindlessHandle,
            RoughnessMetalScale = float4.One,
            EmissiveTexture = material.EmissiveMap?.Get().BindlessHandle ?? texture.DefaultBlackTexture.BindlessHandle,
        };
    }

    private static GpuObjectAssetRef? FindAsset(int handle)
    {
        var system = Program.Instance.MaterialSystem;
        _assetMapField ??= FindField(system.GetType(), "AssetMap")
            ?? throw new MissingFieldException(system.GetType().FullName, "AssetMap");
        var map = _assetMapField.GetValue(system)!;
        var values = (IEnumerable)(map.GetType().GetProperty("Values")?.GetValue(map)
            ?? throw new MissingMemberException("material AssetMap.Values"));
        foreach (var value in values)
            if (value is GpuObjectAssetRef asset && asset.Handle == handle) return asset;
        return null;
    }

    private static void RemoveClone(CloneKey key)
    {
        if (!Clones.Remove(key, out var clone)) return;
        var system = Program.Instance.MaterialSystem;
        _assetMapField ??= FindField(system.GetType(), "AssetMap")!;
        var map = _assetMapField.GetValue(system)!;
        var method = map.GetType().GetMethods().First(m => m.Name == "TryRemove" && m.GetParameters().Length == 2);
        var args = new object?[] { clone.Asset.Id, null };
        if ((bool)method.Invoke(map, args)! && args[1] is GpuObjectAssetRef removed) removed.Dispose();
    }

    private static void Restore(Binding binding)
    {
        foreach (var slot in binding.Slots) RestoreSlot(slot);
    }

    private static void RestoreSlot(Slot slot)
    {
        if (slot.CloneHandle >= 0 && slot.Array[slot.Index] == slot.CloneHandle)
            slot.Array[slot.Index] = slot.OriginalHandle;
        slot.CloneHandle = -1;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } field)
                return field;
        return null;
    }

    private readonly record struct CloneKey(int SourceHandle, int ColorBits);
    private sealed class Clone(string name, GpuObjectAssetRef asset)
    {
        internal string Name { get; } = name;
        internal GpuObjectAssetRef Asset { get; } = asset;
        internal int Uses { get; set; }
    }
    private sealed record Binding(CharacterAvatar Avatar, List<Slot> Slots);
    private sealed class Slot(int[] array, int index, string name, int originalHandle)
    {
        internal int[] Array { get; } = array;
        internal int Index { get; } = index;
        internal string Name { get; } = name;
        internal int OriginalHandle { get; } = originalHandle;
        internal int CloneHandle { get; set; } = -1;
    }
}
