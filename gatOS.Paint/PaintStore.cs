namespace gatOS.Paint;

/// <summary>
/// Game-free mutable owner of desired paint rules. Mutations occur on the game thread; readers see
/// an immutable copy published with one volatile reference swap.
/// </summary>
public sealed class PaintStore
{
    private readonly Dictionary<string, PaintRule> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaintRule> _vessels = new(StringComparer.Ordinal);
    private readonly Dictionary<PartPaintKey, PaintRule> _parts = [];
    private readonly Dictionary<string, PaintRule> _sharedMaterials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaintRule> _kittens = new(StringComparer.Ordinal);
    private readonly Dictionary<KittenMaterialKey, PaintRule> _kittenMaterials = [];
    private volatile PaintSnapshot _current;
    private PaintSnapshot _status;

    /// <summary>Creates a disabled store with the configured clone cap.</summary>
    public PaintStore(int materialCloneCap = 64)
    {
        _status = PaintSnapshot.Empty with { MaterialCloneCap = materialCloneCap };
        _current = _status;
    }

    /// <summary>Latest transport-safe state.</summary>
    public PaintSnapshot Current => _current;

    public void SetPartsMaster(bool enabled) { _status = _status with { PartsEnabled = enabled }; Publish(); }
    public void SetKittensMaster(bool enabled) { _status = _status with { KittensEnabled = enabled }; Publish(); }
    public void SetBlend(PaintBlendMode mode) { _status = _status with { Blend = mode }; Publish(); }
    public void SetGlobalPart(bool? enabled = null, PaintColor? color = null)
        { _status = _status with { GlobalPart = Merge(_status.GlobalPart, enabled, color) }; Publish(); }
    public void SetTemplate(string id, bool? enabled = null, PaintColor? color = null)
        { Set(_templates, id, enabled, color); Publish(); }
    public void SetVessel(string id, bool? enabled = null, PaintColor? color = null)
        { Set(_vessels, id, enabled, color); Publish(); }
    public void SetPart(string vesselId, uint instanceId, bool? enabled = null, PaintColor? color = null)
        { Set(_parts, new PartPaintKey(vesselId, instanceId), enabled, color); Publish(); }
    public void SetSharedKitten(bool? enabled = null, PaintColor? color = null)
        { _status = _status with { SharedKitten = Merge(_status.SharedKitten, enabled, color) }; Publish(); }
    public void SetSharedMaterial(string name, bool? enabled = null, PaintColor? color = null)
        { Set(_sharedMaterials, name, enabled, color); Publish(); }
    public void SetKitten(string id, bool? enabled = null, PaintColor? color = null)
        { Set(_kittens, id, enabled, color); Publish(); }
    public void SetKittenMaterial(string id, string name, bool? enabled = null, PaintColor? color = null)
        { Set(_kittenMaterials, new KittenMaterialKey(id, name), enabled, color); Publish(); }

    public void ClearTemplate(string id) { _templates.Remove(id); Publish(); }
    public void ClearVessel(string id) { _vessels.Remove(id); Publish(); }
    public void ClearPart(string id, uint iid) { _parts.Remove(new(id, iid)); Publish(); }
    public void ClearSharedMaterial(string name) { _sharedMaterials.Remove(name); Publish(); }
    public void ClearKitten(string id)
    {
        _kittens.Remove(id);
        foreach (var key in _kittenMaterials.Keys.Where(k => k.VesselId == id).ToArray()) _kittenMaterials.Remove(key);
        Publish();
    }
    public void ClearKittenMaterial(string id, string name) { _kittenMaterials.Remove(new(id, name)); Publish(); }
    public void ClearParts()
    {
        _templates.Clear(); _vessels.Clear(); _parts.Clear();
        _status = _status with { GlobalPart = PaintRule.Default };
        Publish();
    }
    public void ClearKittens()
    {
        _sharedMaterials.Clear(); _kittens.Clear(); _kittenMaterials.Clear();
        _status = _status with { SharedKitten = PaintRule.Default };
        Publish();
    }

    /// <summary>Resolves part precedence: instance, vessel, template, global.</summary>
    public PaintRule? ResolvePart(string vesselId, uint instanceId, string templateId)
    {
        var s = _current;
        if (s.Parts.TryGetValue(new(vesselId, instanceId), out var part) && part.Enabled) return part;
        if (s.Vessels.TryGetValue(vesselId, out var vessel) && vessel.Enabled) return vessel;
        if (s.Templates.TryGetValue(templateId, out var template) && template.Enabled) return template;
        return s.GlobalPart.Enabled ? s.GlobalPart : null;
    }

    /// <summary>Resolves kitten precedence: individual material/default, shared material/default.</summary>
    public PaintRule? ResolveKitten(string vesselId, string materialName)
    {
        var s = _current;
        if (s.KittenMaterials.TryGetValue(new(vesselId, materialName), out var material) && material.Enabled) return material;
        if (s.Kittens.TryGetValue(vesselId, out var kitten) && kitten.Enabled) return kitten;
        if (s.SharedMaterials.TryGetValue(materialName, out var sharedMaterial) && sharedMaterial.Enabled) return sharedMaterial;
        return s.SharedKitten.Enabled ? s.SharedKitten : null;
    }

    /// <summary>Publishes KSA runtime diagnostics without changing desired rules.</summary>
    public void PublishRuntime(Func<PaintSnapshot, PaintSnapshot> update)
        { _status = update(_status); Publish(); }

    /// <summary>Removes dead per-part rules while retaining vessel/template/session rules.</summary>
    public void PruneParts(IReadOnlySet<PartPaintKey> live)
    {
        var changed = false;
        foreach (var key in _parts.Keys.Where(k => !live.Contains(k)).ToArray()) changed |= _parts.Remove(key);
        if (changed) Publish();
    }

    private void Publish() => _current = _status with
    {
        Templates = new Dictionary<string, PaintRule>(_templates, StringComparer.Ordinal),
        Vessels = new Dictionary<string, PaintRule>(_vessels, StringComparer.Ordinal),
        Parts = new Dictionary<PartPaintKey, PaintRule>(_parts),
        SharedMaterials = new Dictionary<string, PaintRule>(_sharedMaterials, StringComparer.Ordinal),
        Kittens = new Dictionary<string, PaintRule>(_kittens, StringComparer.Ordinal),
        KittenMaterials = new Dictionary<KittenMaterialKey, PaintRule>(_kittenMaterials),
    };

    private static PaintRule Merge(PaintRule current, bool? enabled, PaintColor? color)
        => new(enabled ?? current.Enabled, color ?? current.Color);

    private static void Set<TKey>(Dictionary<TKey, PaintRule> map, TKey key, bool? enabled, PaintColor? color)
        where TKey : notnull
    {
        map.TryGetValue(key, out var current);
        map[key] = Merge(current ?? PaintRule.Default, enabled, color);
    }
}
