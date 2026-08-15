namespace gatOS.Paint;

/// <summary>One optional paint rule with a retained colour while disabled.</summary>
public sealed record PaintRule(bool Enabled, PaintColor Color)
{
    /// <summary>A disabled rule carrying the normal default brush.</summary>
    public static PaintRule Default => new(false, PaintColor.Default);
}

/// <summary>Stable individual-part key.</summary>
public readonly record struct PartPaintKey(string VesselId, uint InstanceId);

/// <summary>Stable per-EVA material key.</summary>
public readonly record struct KittenMaterialKey(string VesselId, string MaterialName);

/// <summary>Runtime condition of the part shader subsystem.</summary>
public enum PartPaintStatus { Disabled, Arming, Active, Degraded, Conflict }

/// <summary>Runtime condition of the EVA material bridge.</summary>
public enum KittenPaintStatus { Disabled, Active, Degraded }

/// <summary>Published transport-safe paint state.</summary>
public sealed record PaintSnapshot
{
    /// <summary>Initial empty state.</summary>
    public static PaintSnapshot Empty { get; } = new();
    public bool PartsEnabled { get; init; }
    public PartPaintStatus PartsStatus { get; init; }
    public PaintBlendMode Blend { get; init; }
    public PaintRule GlobalPart { get; init; } = PaintRule.Default;
    public IReadOnlyDictionary<string, PaintRule> Templates { get; init; }
        = new Dictionary<string, PaintRule>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, PaintRule> Vessels { get; init; }
        = new Dictionary<string, PaintRule>(StringComparer.Ordinal);
    public IReadOnlyDictionary<PartPaintKey, PaintRule> Parts { get; init; }
        = new Dictionary<PartPaintKey, PaintRule>();
    public bool KittensEnabled { get; init; }
    public KittenPaintStatus KittensStatus { get; init; }
    public PaintRule SharedKitten { get; init; } = PaintRule.Default;
    public IReadOnlyDictionary<string, PaintRule> SharedMaterials { get; init; }
        = new Dictionary<string, PaintRule>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, PaintRule> Kittens { get; init; }
        = new Dictionary<string, PaintRule>(StringComparer.Ordinal);
    public IReadOnlyDictionary<KittenMaterialKey, PaintRule> KittenMaterials { get; init; }
        = new Dictionary<KittenMaterialKey, PaintRule>();
    public IReadOnlyList<string> MaterialNames { get; init; } = [];
    public int ActiveKittenBindings { get; init; }
    public int LiveMaterialClones { get; init; }
    public int PeakMaterialClones { get; init; }
    public int MaterialCloneCap { get; init; } = 64;
    public bool RaytracedShaderAvailable { get; init; }
    public int RasterCompileCount { get; init; }
    public int RaytracedCompileCount { get; init; }
    public string PartError { get; init; } = "";
    public string KittenError { get; init; } = "";
}
