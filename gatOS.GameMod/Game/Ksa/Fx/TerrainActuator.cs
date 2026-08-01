using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>
///     The <c>/sim/debug/terrain</c> actuator (plans/FX_EDITORS_PLAN.md §5): the v1 subset of the
///     game's "Terrain Editor". Two very different tiers live here:
///     <list type="bullet">
///         <item><c>wireframe</c> — a family-global toggle on the public
///         <c>PlanetRenderer.Wireframe</c> field. Zero reflection, always available.</item>
///         <item>the per-body rows — a <b>paired write</b>: the reference object on
///         <c>Celestial.BodyTemplate</c> where one exists, <b>and</b> the GPU-mapped UBO struct at the
///         body's render slot, mirrored into every frame-in-flight copy. This is what the in-game
///         editor does, and there is no public repopulate that would re-derive the UBO for us (see the
///         anchor on <see cref="Write"/>).</item>
///     </list>
///     Game-thread only (the Frame command drain).
/// </summary>
internal static class TerrainActuator
{
    /// <summary>The family-global entity's id in the snapshot/command surface.</summary>
    internal const string GlobalEntity = "";

    /// <summary>The one field addressed with the empty entity token.</summary>
    private const string WireframeKey = "wireframe";

    /// <summary>
    ///     Sets one terrain field, capturing its pristine value on the first write.
    ///     <c>NotFound</c> for an unknown body or a body with no live render slot; <c>Invalid</c> when
    ///     a global field is addressed per body (or the reverse).
    /// </summary>
    internal static CommandResult Set(KsaHealth health, string id, FxFieldMatch match, string field,
        IReadOnlyList<double> values)
    {
        var global = match.Spec.Key == WireframeKey;
        if (global != (id.Length == 0))
            return new CommandResult(CommandOutcome.Invalid,
                global ? $"'{field}' is a global field (empty entity)" : $"'{field}' is addressed per body");

        if (FxReflect.Terrain(out var rendererError) is not { } renderer)
            return FxReflect.Degrade(health, FxReflect.TerrainRendererAccessor, rendererError);
        FxReflect.Healthy(health, FxReflect.TerrainRendererAccessor);

        if (global)
        {
            var pristineFlag = new double[1] { renderer.Wireframe ? 1 : 0 };
            FxPristine.Capture(FxFamily.Terrain, GlobalEntity, field, pristineFlag);
            renderer.Wireframe = values[0] > 0.5;
            FxEditorReader.Invalidate();
            return CommandResult.Ok;
        }

        if (Resolve(renderer, id) is not { } body)
            return new CommandResult(CommandOutcome.NotFound, $"body '{id}' has no terrain render slot");
        if (FxReflect.TerrainUbo(renderer, out var uboError) is not { } maps)
            return FxReflect.Degrade(health, FxReflect.TerrainUboAccessor, uboError);
        FxReflect.Healthy(health, FxReflect.TerrainUboAccessor);

        var pristine = new double[match.Spec.Arity];
        if (Read(renderer, maps, body, match.Spec, pristine))
            FxPristine.Capture(FxFamily.Terrain, id, field, pristine);
        if (!Write(renderer, maps, body, match.Spec, values))
            return new CommandResult(CommandOutcome.Invalid, $"'{field}' is not bound in this game build");

        FxEditorReader.Invalidate();
        return CommandResult.Ok;
    }

    /// <summary>Restores one entity's pristine terrain values (a no-op when nothing was captured).</summary>
    internal static CommandResult Reset(KsaHealth health, string id)
    {
        if (FxReflect.Terrain(out var rendererError) is null)
            return FxReflect.Degrade(health, FxReflect.TerrainRendererAccessor, rendererError);
        FxReflect.Healthy(health, FxReflect.TerrainRendererAccessor);
        FxPristine.Restore(FxFamily.Terrain, id);
        FxEditorReader.Invalidate();
        return CommandResult.Ok;
    }

    /// <summary>Replays one captured field (the <see cref="FxPristine"/> restore path).</summary>
    internal static bool Restore(string id, string field, double[] values)
    {
        if (FxReflect.Terrain(out _) is not { } renderer
            || FxCatalog.Match(FxCatalog.Terrain, field) is not { } match)
            return false;

        if (match.Spec.Key == WireframeKey)
        {
            renderer.Wireframe = values[0] > 0.5;
            FxEditorReader.Invalidate();
            return true;
        }

        if (Resolve(renderer, id) is not { } body || FxReflect.TerrainUbo(renderer, out _) is not { } maps)
            return false;
        var applied = Write(renderer, maps, body, match.Spec, values);
        if (applied)
            FxEditorReader.Invalidate();
        return applied;
    }

    /// <summary>
    ///     The celestial named by <paramref name="id"/>, but only while it holds both UBO render
    ///     slots — a body without a slot has no addressable terrain and is absent from the tree.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); PlanetRenderer.RenderUboSlot(Celestial); "
            + "PlanetRenderer.MeshUboSlot(Celestial) (both public, -1 when unslotted)",
        SourceFile = "KSA/Universe.cs / KSA/PlanetRenderer.cs:374,379", Verified = "2026-08-01",
        GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Medium,
        Notes = "The slot helpers are the public, allocation-free way to ask 'is this body rendered?'; "
            + "they are also the indices every UBO write needs.")]
    internal static Celestial? Resolve(PlanetRenderer renderer, string id)
    {
        if (id.Length == 0 || Universe.CurrentSystem is not { } system)
            return null;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (astronomical is Celestial celestial && celestial.Id == id && HasSlot(renderer, celestial))
                return celestial;
        return null;
    }

    /// <summary>Whether a body currently holds both terrain UBO slots.</summary>
    internal static bool HasSlot(PlanetRenderer renderer, Celestial celestial)
        => renderer.RenderUboSlot(celestial) >= 0 && renderer.MeshUboSlot(celestial) >= 0;

    /// <summary>
    ///     Reads one per-body terrain field into <paramref name="dst"/>. Values are read out of the
    ///     UBO — the struct the GPU actually samples — so read-back is the live truth and a written
    ///     value round-trips exactly.
    /// </summary>
    [KsaAnchor("PlanetRenderer.PlanetUbo.{TanMeanSlopeRoughnessRadians,HapkeMeanAlbedo,BiomeBlendStrength,"
            + "DetailFadeStartMeters,DetailFadeEndMeters,TessellationEdgeLengthPixels,TessellationFactor,"
            + "TessellationRangeMeters}; PlanetRenderer.MeshUbo.{MinHeight,MaxHeight} (public struct fields)",
        SourceFile = "KSA/PlanetRenderer.cs:37-125", Verified = "2026-08-01",
        GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "Read through the reflected MappedMemory rings at the body's slot, frame 0. "
            + "TanMeanSlopeRoughnessRadians stores RADIANS despite the 'Tan' prefix (the editor writes "
            + "deg * PI/180 into it), so the leaf converts to/from degrees.")]
    internal static bool Read(PlanetRenderer renderer, FxReflect.TerrainUboMaps maps, Celestial body,
        FxFieldSpec spec, double[] dst)
    {
        ref var planet = ref PlanetUbo(renderer, maps, renderer.RenderUboSlot(body));
        ref var mesh = ref MeshUbo(renderer, maps, renderer.MeshUboSlot(body));
        switch (spec.Key)
        {
            case "min_height": dst[0] = mesh.MinHeight; return true;
            case "max_height": dst[0] = mesh.MaxHeight; return true;
            case "slope_roughness_deg": dst[0] = planet.TanMeanSlopeRoughnessRadians * (180.0 / Math.PI); return true;
            case "hapke_albedo": dst[0] = planet.HapkeMeanAlbedo; return true;
            case "biomes/blend_strength": dst[0] = planet.BiomeBlendStrength; return true;
            case "biomes/detail_fade_start_km": dst[0] = planet.DetailFadeStartMeters / 1000.0; return true;
            case "biomes/detail_fade_end_km": dst[0] = planet.DetailFadeEndMeters / 1000.0; return true;
            case "tessellation/edge_length_px": dst[0] = planet.TessellationEdgeLengthPixels; return true;
            case "tessellation/factor": dst[0] = planet.TessellationFactor; return true;
            case "tessellation/range_m": dst[0] = planet.TessellationRangeMeters; return true;
            default: return false;
        }
    }

    /// <summary>
    ///     Applies one validated payload: the reference object (where the field has one) plus the UBO
    ///     struct at the body's slot, then the frame-in-flight mirror copy. False ⇒ not bound here.
    /// </summary>
    [KsaAnchor("Celestial.BodyTemplate.HeightReference.{Minimum,Maximum}; "
            + "BodyTemplate.TerrainReference.BiomeMaterials.{BlendStrength.Value,DetailFadeInStart,"
            + "DetailFadeInEnd}; PlanetUbo/MeshUbo writes at (NumCelestials*frame + slot)*Stride "
            + "over the reflected _renderUboMap/_meshUboMap",
        SourceFile = "KSA/PlanetRenderer.cs:2107-2398 (the in-game Terrain Editor's write + mirror loop) / "
            + "KSA/AstronomicalTemplate.cs:27,51 / KSA/BiomeMaterialsReference.cs",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "INVESTIGATION (plan §5 directive): PlanetRenderer has NO public repopulate/invalidate that "
            + "would re-derive a body's UBO from its reference objects — the two population loops the plan "
            + "pointed at (:684-720 for PlanetUbo, :1086-1114 for MeshUbo) are inline CONSTRUCTOR code, not "
            + "callable methods, and they also reallocate descriptor sets. So the paired write is "
            + "implemented faithfully instead: write frame slot 0, then copy that struct into the remaining "
            + "MaxFramesInFlight mirrors (:2388-2398) or the value flickers. The memory is host-visible + "
            + "host-coherent, written on the same (main) thread the game's own editor writes it from.")]
    private static bool Write(PlanetRenderer renderer, FxReflect.TerrainUboMaps maps, Celestial body,
        FxFieldSpec spec, IReadOnlyList<double> v)
    {
        var renderSlot = renderer.RenderUboSlot(body);
        var meshSlot = renderer.MeshUboSlot(body);
        ref var planet = ref PlanetUbo(renderer, maps, renderSlot);
        ref var mesh = ref MeshUbo(renderer, maps, meshSlot);
        var template = body.BodyTemplate;
        var biomes = template.TerrainReference?.BiomeMaterials;

        switch (spec.Key)
        {
            case "min_height":
                if (template.HeightReference is { } minRef)
                    minRef.Minimum = new DistanceReference(v[0], DistanceUnit.Meters);
                mesh.MinHeight = (float)v[0];
                break;
            case "max_height":
                if (template.HeightReference is { } maxRef)
                    maxRef.Maximum = new DistanceReference(v[0], DistanceUnit.Meters);
                mesh.MaxHeight = (float)v[0];
                break;
            case "slope_roughness_deg":
                planet.TanMeanSlopeRoughnessRadians = (float)(v[0] * (Math.PI / 180.0));
                break;
            case "hapke_albedo":
                planet.HapkeMeanAlbedo = (float)v[0];
                break;
            case "biomes/blend_strength":
                if (biomes?.BlendStrength is { } blend)
                    blend.Value = (float)v[0];
                planet.BiomeBlendStrength = (float)v[0];
                mesh.BiomeBlendStrength = (float)v[0];
                break;
            case "biomes/detail_fade_start_km":
                if (biomes is not null)
                    biomes.DetailFadeInStart = new DistanceReference(v[0] * 1000.0, DistanceUnit.Meters);
                planet.DetailFadeStartMeters = (float)(v[0] * 1000.0);
                break;
            case "biomes/detail_fade_end_km":
                if (biomes is not null)
                    biomes.DetailFadeInEnd = new DistanceReference(v[0] * 1000.0, DistanceUnit.Meters);
                planet.DetailFadeEndMeters = (float)(v[0] * 1000.0);
                break;
            case "tessellation/edge_length_px":
                planet.TessellationEdgeLengthPixels = (float)v[0];
                break;
            case "tessellation/factor":
                planet.TessellationFactor = (float)v[0];
                break;
            case "tessellation/range_m":
                planet.TessellationRangeMeters = (float)v[0];
                break;
            default:
                return false;
        }

        Mirror(renderer, maps, renderSlot, meshSlot);
        return true;
    }

    /// <summary>
    ///     Copies the just-written frame-0 UBO structs into every other frame-in-flight mirror — the
    ///     tail of the in-game editor's write path. Without it the change flickers, appearing only on
    ///     the frames that happen to sample slot 0.
    /// </summary>
    private static void Mirror(PlanetRenderer renderer, FxReflect.TerrainUboMaps maps, int renderSlot, int meshSlot)
    {
        var frames = Program.GetRenderer()?.MaxFramesInFlight ?? 1;
        var planetValue = PlanetUbo(renderer, maps, renderSlot);
        var meshValue = MeshUbo(renderer, maps, meshSlot);
        for (var frame = 1; frame < frames; frame++)
        {
            var planetOffset = (renderer.NumCelestials * frame + renderSlot) * renderer.PlanetUboStride;
            var meshOffset = (renderer.NumCelestials * frame + meshSlot) * renderer.MeshUboStride;
            maps.RenderUbo.Offset(planetOffset).As<PlanetRenderer.PlanetUbo>() = planetValue;
            maps.MeshUbo.Offset(meshOffset).As<PlanetRenderer.MeshUbo>() = meshValue;
        }
    }

    private static ref PlanetRenderer.PlanetUbo PlanetUbo(PlanetRenderer renderer,
        FxReflect.TerrainUboMaps maps, int slot)
        => ref maps.RenderUbo.Offset(slot * renderer.PlanetUboStride).As<PlanetRenderer.PlanetUbo>();

    private static ref PlanetRenderer.MeshUbo MeshUbo(PlanetRenderer renderer,
        FxReflect.TerrainUboMaps maps, int slot)
        => ref maps.MeshUbo.Offset(slot * renderer.MeshUboStride).As<PlanetRenderer.MeshUbo>();
}
