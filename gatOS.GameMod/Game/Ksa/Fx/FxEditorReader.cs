using System.Diagnostics;
using System.Globalization;
using System.Text;
using gatOS.SimFs.Fx;
using gatOS.SimFs.Snapshots;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>
///     The FX-editor sampler (plans/FX_EDITORS_PLAN.md §1): projects the four families into an
///     immutable <see cref="FxEditorsSnapshot"/> whose concrete field keys and values round-trip
///     exactly with the writes the actuators perform — every leaf is read back through the same
///     accessor it is written through (<c>PlumeActuator.TryRead</c> and friends).
/// </summary>
/// <remarks>
///     <para><b>Memoization.</b> The whole surface is rebuilt only when an FX write happened since
///     the last build (<see cref="Invalidate"/>, bumped by every successful actuator write) or when
///     the resample interval elapsed — the latter catches edits made through the game's own imgui
///     editors. Otherwise the previous instance is republished <b>by reference</b>, so the steady
///     state costs one comparison and allocates nothing (the <c>parts/json</c> precedent).</para>
///     <para>Game-thread only (the sampler tick).</para>
/// </remarks>
internal static class FxEditorReader
{
    /// <summary>How long a build stays fresh without an FX write (catches in-game imgui edits).</summary>
    private static readonly long ResampleTicks = Stopwatch.Frequency * 2;

    private static int _version;
    private static int _builtVersion = -1;
    private static long _builtTimestamp;
    private static FxEditorsSnapshot? _last;

    /// <summary>Marks the sampled surface stale — called by every successful FX write.</summary>
    internal static void Invalidate() => _version++;

    /// <summary>
    ///     The FX-editor surface for this tick: the memoized instance unless a write landed or the
    ///     resample interval elapsed. Never throws — a family that cannot be read is simply empty.
    /// </summary>
    internal static FxEditorsSnapshot Sample(KsaHealth health)
    {
        var now = Stopwatch.GetTimestamp();
        if (_last is { } cached && _builtVersion == _version && now - _builtTimestamp < ResampleTicks)
            return cached;

        var snapshot = new FxEditorsSnapshot
        {
            PlumeTemplates = SamplePlumeTemplates(health),
            Trail = SampleTrail(),
            CloudBodies = SampleCloudBodies(),
            TerrainBodies = SampleTerrainBodies(),
            TerrainGlobal = SampleTerrainGlobal(),
        };
        _last = snapshot;
        _builtVersion = _version;
        _builtTimestamp = now;
        return snapshot;
    }

    /// <summary>Teardown: drops the memo so a reload starts from a fresh read.</summary>
    internal static void Reset()
    {
        _last = null;
        _builtVersion = -1;
        _builtTimestamp = 0;
    }

    private static IReadOnlyList<FxEntitySnapshot> SamplePlumeTemplates(KsaHealth health)
    {
        var templates = FxReflect.PlumeTemplates(out var error);
        if (templates is null)
        {
            FxReflect.Degrade(health, FxReflect.PlumeTemplatesAccessor, error);
            templates = HarvestPlumeTemplates();
        }
        else
        {
            FxReflect.Healthy(health, FxReflect.PlumeTemplatesAccessor);
        }

        if (templates.Count == 0)
            return [];

        var entities = new List<FxEntitySnapshot>(templates.Count);
        foreach (var template in templates)
        {
            if (template.Id is not { Length: > 0 } id)
                continue;
            var fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var spec in FxCatalog.EnginePlume)
            {
                var values = new double[spec.Arity];
                if (PlumeActuator.TryRead(template, spec, values))
                    fields[spec.Key] = values;
            }

            if (fields.Count > 0)
                entities.Add(new FxEntitySnapshot(id, fields));
        }

        return entities;
    }

    /// <summary>
    ///     The reflection-free fallback roster: the templates actually referenced by live nozzles.
    ///     Narrower than the registry (only loaded vessels contribute), but it keeps the family
    ///     usable when <c>VolumetricExhaustTemplate.References</c> moves.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.RocketNozzles.ModulesAndAllStates; "
            + "RocketNozzle.ReactionPlumes[].VolumetricExhaust.Id; VolumetricExhaustTemplate.Get(string)",
        SourceFile = "KSA/RocketNozzle.cs:15,40 / KSA/VolumetricExhaustReference.cs / "
            + "KSA/VolumetricExhaustTemplate.cs:48",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Medium,
        Notes = "Fallback enumeration only; all members public. Each vehicle is isolated so a "
            + "mid-teardown vessel cannot abort the harvest.")]
    private static List<VolumetricExhaustTemplate> HarvestPlumeTemplates()
    {
        var found = new List<VolumetricExhaustTemplate>();
        if (Universe.CurrentSystem is not { } system)
            return found;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var astronomical in system.All.UnsafeAsList())
        {
            if (astronomical is not Vehicle vehicle)
                continue;
            try
            {
                var nozzles = vehicle.Parts?.RocketNozzles;
                if (nozzles is null)
                    continue;
                foreach (var module in nozzles.ModulesAndAllStates)
                foreach (var plume in module.Module.ReactionPlumes)
                {
                    if (plume.VolumetricExhaust?.Id is not { Length: > 0 } id || !seen.Add(id))
                        continue;
                    if (VolumetricExhaustTemplate.Get(id) is { } template)
                        found.Add(template);
                }
            }
            catch (Exception)
            {
                // A vehicle mid-teardown must not abort the harvest for the rest.
            }
        }

        return found;
    }

    private static FxEntitySnapshot? SampleTrail()
    {
        if (FxReflect.Trail(out _) is not { } trail)
            return null;

        var fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var spec in FxCatalog.PlumeTrail)
        {
            var values = new double[spec.Arity];
            if (TrailActuator.TryRead(trail, spec, values))
                fields[spec.Key] = values;
        }

        return fields.Count == 0 ? null : new FxEntitySnapshot("", fields);
    }

    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); AtmosphericBody.BodyTemplate.CloudsReference",
        SourceFile = "KSA/Universe.cs / KSA/AstronomicalTemplate.cs:60", Verified = "2026-08-01",
        GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Low,
        Notes = "Only atmospheric bodies with a cloud definition are addressable — the apply path takes an "
            + "AtmosphericBody. Layer/cloud-type counts define which indexed leaves exist.")]
    private static IReadOnlyList<FxEntitySnapshot> SampleCloudBodies()
    {
        if (Universe.CurrentSystem is not { } system)
            return [];

        List<FxEntitySnapshot>? entities = null;
        foreach (var astronomical in system.All.UnsafeAsList())
        {
            if (astronomical is not AtmosphericBody body || body.BodyTemplate.CloudsReference is not { } clouds)
                continue;

            var fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var spec in FxCatalog.Clouds)
                ExpandClouds(clouds, spec, fields);
            if (fields.Count > 0)
                (entities ??= []).Add(new FxEntitySnapshot(body.Id, fields));
        }

        return (IReadOnlyList<FxEntitySnapshot>?)entities ?? [];
    }

    /// <summary>Materializes every concrete key one cloud spec expands to on this body.</summary>
    private static void ExpandClouds(CloudsReference clouds, FxFieldSpec spec,
        Dictionary<string, double[]> fields)
    {
        var wildcards = Wildcards(spec.Key);
        if (wildcards == 0)
        {
            Add(clouds, spec, [], spec.Key, fields);
            return;
        }

        for (var layer = 0; layer < clouds.Layers.Count; layer++)
        {
            if (wildcards == 1)
            {
                Add(clouds, spec, [layer], Concrete(spec.Key, [layer]), fields);
                continue;
            }

            var types = clouds.Layers[layer].VolumetricCloud?.CloudTypes;
            if (types is null)
                continue;
            for (var type = 0; type < types.Count; type++)
                Add(clouds, spec, [layer, type], Concrete(spec.Key, [layer, type]), fields);
        }

        static void Add(CloudsReference clouds, FxFieldSpec spec, int[] indices, string key,
            Dictionary<string, double[]> fields)
        {
            var values = new double[spec.Arity];
            if (CloudActuator.TryRead(clouds, spec, indices, values))
                fields[key] = values;
        }
    }

    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); PlanetRenderer.RenderUboSlot/MeshUboSlot",
        SourceFile = "KSA/Universe.cs / KSA/PlanetRenderer.cs:374,379", Verified = "2026-08-01",
        GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "Only bodies holding a live render slot are sampled (and therefore addressable). Values "
            + "are read out of the reflected UBO rings, so a degraded fx.terrain_ubo empties the per-body "
            + "roster while the global wireframe leaf stays live.")]
    private static IReadOnlyList<FxEntitySnapshot> SampleTerrainBodies()
    {
        if (Universe.CurrentSystem is not { } system
            || FxReflect.Terrain(out _) is not { } renderer
            || FxReflect.TerrainUbo(renderer, out _) is not { } maps)
            return [];

        List<FxEntitySnapshot>? entities = null;
        foreach (var astronomical in system.All.UnsafeAsList())
        {
            if (astronomical is not Celestial celestial || !TerrainActuator.HasSlot(renderer, celestial))
                continue;

            var fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var spec in FxCatalog.Terrain)
            {
                if (spec.Key == "wireframe")
                    continue; // family-global, published on the singleton entity instead
                var values = new double[spec.Arity];
                if (TerrainActuator.Read(renderer, maps, celestial, spec, values))
                    fields[spec.Key] = values;
            }

            if (fields.Count > 0)
                (entities ??= []).Add(new FxEntitySnapshot(celestial.Id, fields));
        }

        return (IReadOnlyList<FxEntitySnapshot>?)entities ?? [];
    }

    [KsaAnchor("PlanetRenderer.Wireframe (public instance field)", SourceFile = "KSA/PlanetRenderer.cs:216",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Medium,
        Notes = "DISCREPANCY vs plans/FX_EDITORS_PLAN.md §5: Wireframe is a plain public INSTANCE field, "
            + "not static — it is reached through Program.GetPlanetRenderer(). Zero reflection either way.")]
    private static FxEntitySnapshot? SampleTerrainGlobal()
    {
        if (FxReflect.Terrain(out _) is not { } renderer)
            return null;
        return new FxEntitySnapshot(TerrainActuator.GlobalEntity, new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["wireframe"] = [renderer.Wireframe ? 1 : 0],
        });
    }

    private static int Wildcards(string key)
    {
        var count = 0;
        foreach (var c in key)
            if (c == '*')
                count++;
        return count;
    }

    /// <summary>Substitutes a key pattern's <c>*</c> segments with the given indices, left to right.</summary>
    private static string Concrete(string pattern, ReadOnlySpan<int> indices)
    {
        var builder = new StringBuilder(pattern.Length + 4);
        var next = 0;
        foreach (var c in pattern)
        {
            if (c == '*' && next < indices.Length)
                builder.Append(indices[next++].ToString(CultureInfo.InvariantCulture));
            else
                builder.Append(c);
        }

        return builder.ToString();
    }
}
