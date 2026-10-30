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

    /// <summary>The per-body field that is also mirrored into the renderer-wide tessellation range.</summary>
    private const string TessellationRangeKey = "tessellation/range_m";

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

        // The body's own capture is always its UBO value. The renderer-wide tessellation range is
        // shared by every body, so it gets one capture of its own under the global entity, taken
        // before the first range write to any body.
        var pristine = new double[match.Spec.Arity];
        if (Read(renderer, maps, body, match.Spec, pristine, perBodyUbo: true))
            FxPristine.Capture(FxFamily.Terrain, id, field, pristine);
        if (match.Spec.Key == TessellationRangeKey)
            FxPristine.Capture(FxFamily.Terrain, GlobalEntity, TessellationRangeKey, [renderer.TessellationRange]);
        if (!Write(renderer, maps, body, match.Spec, values, rendererWideRange: true, out var rangeError))
            return new CommandResult(CommandOutcome.Invalid, $"'{field}' is not bound in this game build");

        FxEditorReader.Invalidate();
        if (rangeError is not null)
            return FxReflect.Degrade(health, FxReflect.TerrainTessRangeAccessor, rangeError);
        if (match.Spec.Key == TessellationRangeKey)
            FxReflect.Healthy(health, FxReflect.TerrainTessRangeAccessor);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Restores one entity's pristine terrain values (a no-op when nothing was captured). A body
    ///     whose <c>tessellation/range_m</c> gatOS changed also puts the shared renderer-wide range
    ///     back, since that range is what compute tessellation draws every body with.
    /// </summary>
    internal static CommandResult Reset(KsaHealth health, string id)
    {
        if (FxReflect.Terrain(out var rendererError) is null)
            return FxReflect.Degrade(health, FxReflect.TerrainRendererAccessor, rendererError);
        FxReflect.Healthy(health, FxReflect.TerrainRendererAccessor);
        var touchedRange = id.Length != 0 && FxPristine.Has(FxFamily.Terrain, id, TessellationRangeKey);
        FxPristine.Restore(FxFamily.Terrain, id);
        if (touchedRange)
            FxPristine.RestoreField(FxFamily.Terrain, GlobalEntity, TessellationRangeKey);
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

        if (id.Length == 0)
        {
            // The only other global capture: the renderer-wide tessellation range.
            if (match.Spec.Key != TessellationRangeKey
                || !FxReflect.SetTessellationRange(renderer, (float)values[0], out _))
                return false;
            FxEditorReader.Invalidate();
            return true;
        }

        if (Resolve(renderer, id) is not { } body || FxReflect.TerrainUbo(renderer, out _) is not { } maps)
            return false;
        var applied = Write(renderer, maps, body, match.Spec, values, rendererWideRange: false, out _);
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
    ///     value round-trips exactly. The one exception is <c>tessellation/range_m</c> under compute
    ///     tessellation, which reads the renderer-wide range that path actually uses.
    /// </summary>
    [KsaAnchor("PlanetRenderer.PlanetUbo.{TanMeanSlopeRoughness,HapkeMeanAlbedo,BiomeBlendStrength,"
            + "DetailFadeStartMeters,DetailFadeEndMeters,TessellationEdgeLengthPixels,TessellationFactor,"
            + "TessellationRangeMeters}; PlanetRenderer.MeshUbo.{MinHeight,MaxHeight} (public struct fields); "
            + "PlanetRenderer.{ComputeTessellationEnabled,TessellationRange} (public getters)",
        SourceFile = "KSA/PlanetRenderer.cs:29-116,376-392,2638-2646", Verified = "2026-10-29",
        GameVersion = "2026.10.25.5707", Risk = ChurnRisk.High,
        Notes = "Read through the reflected MappedMemory rings at the body's slot, frame 0. "
            + "5554: TanMeanSlopeRoughness stores TAN(angle), matching the corrected stock editor. "
            + "The degree-based leaf uses atan on read and tan on write; a field rename alone changes its meaning. "
            + "5707: while ComputeTessellationEnabled (the default: tessellation setting on, "
            + "PlanetTessellation.UseCompute = true) the drawn range is the renderer-wide TessellationRange, "
            + "not the UBO field, so range_m reads that — the same value for every body. With hardware "
            + "tessellation (or tessellation off) it reads the per-body UBO field as before.")]
    internal static bool Read(PlanetRenderer renderer, FxReflect.TerrainUboMaps maps, Celestial body,
        FxFieldSpec spec, double[] dst, bool perBodyUbo = false)
    {
        ref var planet = ref PlanetUbo(renderer, maps, renderer.RenderUboSlot(body));
        ref var mesh = ref MeshUbo(renderer, maps, renderer.MeshUboSlot(body));
        switch (spec.Key)
        {
            case "min_height": dst[0] = mesh.MinHeight; return true;
            case "max_height": dst[0] = mesh.MaxHeight; return true;
            case "slope_roughness_deg": dst[0] = Math.Atan(planet.TanMeanSlopeRoughness) * (180.0 / Math.PI); return true;
            case "hapke_albedo": dst[0] = planet.HapkeMeanAlbedo; return true;
            case "biomes/blend_strength": dst[0] = planet.BiomeBlendStrength; return true;
            case "biomes/detail_fade_start_km": dst[0] = planet.DetailFadeStartMeters / 1000.0; return true;
            case "biomes/detail_fade_end_km": dst[0] = planet.DetailFadeEndMeters / 1000.0; return true;
            case "tessellation/edge_length_px": dst[0] = planet.TessellationEdgeLengthPixels; return true;
            case "tessellation/factor": dst[0] = planet.TessellationFactor; return true;
            case TessellationRangeKey:
                dst[0] = !perBodyUbo && renderer.ComputeTessellationEnabled
                    ? renderer.TessellationRange
                    : planet.TessellationRangeMeters;
                return true;
            default: return false;
        }
    }

    /// <summary>
    ///     Applies one validated payload: the reference object (where the field has one) plus the UBO
    ///     struct at the body's slot, then the frame-in-flight mirror copy. False ⇒ not bound here.
    ///     A <c>tessellation/range_m</c> write also sets the renderer-wide range when
    ///     <paramref name="rendererWideRange"/> is true (a player write; restores replay the two
    ///     captures separately). <paramref name="rangeError"/> is non-null when the value landed in
    ///     the UBO but the renderer-wide range could not be written.
    /// </summary>
    [KsaAnchor("Celestial.BodyTemplate.HeightReference.{Minimum,Maximum}; "
            + "BodyTemplate.TerrainReference.BiomeMaterials.{BlendStrength.Value,DetailFadeInStart,"
            + "DetailFadeInEnd}; PlanetUbo/MeshUbo writes at (NumCelestials*frame + slot)*Stride "
            + "over the reflected _renderUboMap/_meshUboMap",
        SourceFile = "KSA/PlanetRenderer.cs:2155-2530 (the in-game Terrain Editor's write + mirror loop) / "
            + "KSA/AstronomicalTemplate.cs:27,51 / KSA/BiomeMaterialsReference.cs / KSA/DistantSphereRenderer.cs:99-105",
        Verified = "2026-10-29", GameVersion = "2026.10.25.5707", Risk = ChurnRisk.High,
        Notes = "INVESTIGATION (plan §5 directive): PlanetRenderer has NO public repopulate/invalidate that "
            + "would re-derive a body's UBO from its reference objects — the two population loops the plan "
            + "pointed at (:684-720 for PlanetUbo, :1086-1114 for MeshUbo) are inline CONSTRUCTOR code, not "
            + "callable methods, and they also reallocate descriptor sets. So the paired write is "
            + "implemented faithfully instead: write frame slot 0, then copy that struct into the remaining "
            + "MaxFramesInFlight mirrors (:2388-2398) or the value flickers. The memory is host-visible + "
            + "host-coherent, written on the same (main) thread the game's own editor writes it from. "
            + "5348: the mirror is now FIELD-WISE, not a struct copy — MeshUbo gained per-frame "
            + "DirAnchorHi/Lo + DirAnchorUvHi/Lo (revs 5319-5325) that GenerateMeshData rewrites every "
            + "frame from the live camera; see the Mirror remarks. "
            + "5482 (rev 5457): PlanetRenderer.cs is byte-identical, but the far-away sphere now does heightmap "
            + "displacement and copies HeightReference.Minimum/Maximum into its OWN material once, at "
            + "construction (DistantSphereRenderer.cs:99-105, DistantSphereMaterialData). Live min_height/"
            + "max_height writes therefore reach near terrain immediately but not the distant-sphere view "
            + "until KSA rebuilds that renderer. 5554: roughness is tan(degrees * PI/180). Rev 5534 "
            + "moved viewport-dependent mesh anchors into PlanetViewportData.MeshViewportUbo; the two "
            + "reflected per-body rings, slot arithmetic and field-wise mirror remain valid. "
            + "5707 (compute tessellation, revs 5654-5656/5680): the stock editor's Range slider now edits the "
            + "renderer-wide PlanetRenderer._tessellationRange and mirrors it into the nearby body's UBO "
            + "(PlanetRenderer.cs:2644-2646); compute tessellation and frustum culling read only the "
            + "renderer-wide value. range_m therefore writes both, like the editor — so under compute "
            + "tessellation the last write applies to every body. Edge length and factor are still read "
            + "per body from the UBO by both paths.")]
    private static bool Write(PlanetRenderer renderer, FxReflect.TerrainUboMaps maps, Celestial body,
        FxFieldSpec spec, IReadOnlyList<double> v, bool rendererWideRange, out string? rangeError)
    {
        rangeError = null;
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
                planet.TanMeanSlopeRoughness = (float)Math.Tan(v[0] * (Math.PI / 180.0));
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
            case TessellationRangeKey:
                planet.TessellationRangeMeters = (float)v[0];
                if (rendererWideRange && !FxReflect.SetTessellationRange(renderer, (float)v[0], out var error))
                    rangeError = error;
                break;
            default:
                return false;
        }

        Mirror(renderer, maps, renderSlot, meshSlot);
        return true;
    }

    /// <summary>
    ///     Propagates the just-written frame-0 values into every other frame-in-flight mirror — the
    ///     tail of the in-game editor's write path. Without it the change flickers, appearing only on
    ///     the frames that happen to sample slot 0.
    /// </summary>
    /// <remarks>
    ///     Copies <b>only the individual fields gatOS writes</b>, never the whole struct. KSA
    ///     2026.8.22.5348 (revs 5319–5325, the terrain-precision rework) gave <c>MeshUbo</c> four
    ///     split-double anchor fields — <c>DirAnchorHi/Lo</c> and <c>DirAnchorUvHi/Lo</c> — which
    ///     <c>PlanetRenderer.GenerateMeshData</c> rewrites <i>per frame index, every frame</i> from the
    ///     live camera. A whole-struct copy stamps frame 0's anchor over each other frame's live value,
    ///     corrupting one frame of terrain precision per mirrored frame on every terrain write. Before
    ///     5348 the only per-frame <c>MeshUbo</c> fields were frame-invariant, so the struct copy was
    ///     harmless; field-wise copying is also immune to the next such addition, on either struct.
    /// </remarks>
    private static void Mirror(PlanetRenderer renderer, FxReflect.TerrainUboMaps maps, int renderSlot, int meshSlot)
    {
        var frames = Program.GetRenderer()?.MaxFramesInFlight ?? 1;
        var planetSrc = PlanetUbo(renderer, maps, renderSlot);
        var meshSrc = MeshUbo(renderer, maps, meshSlot);
        for (var frame = 1; frame < frames; frame++)
        {
            var planetOffset = (renderer.NumCelestials * frame + renderSlot) * renderer.PlanetUboStride;
            var meshOffset = (renderer.NumCelestials * frame + meshSlot) * renderer.MeshUboStride;

            ref var planetDst = ref maps.RenderUbo.Offset(planetOffset).As<PlanetRenderer.PlanetUbo>();
            planetDst.TanMeanSlopeRoughness = planetSrc.TanMeanSlopeRoughness;
            planetDst.HapkeMeanAlbedo = planetSrc.HapkeMeanAlbedo;
            planetDst.BiomeBlendStrength = planetSrc.BiomeBlendStrength;
            planetDst.DetailFadeStartMeters = planetSrc.DetailFadeStartMeters;
            planetDst.DetailFadeEndMeters = planetSrc.DetailFadeEndMeters;
            planetDst.TessellationEdgeLengthPixels = planetSrc.TessellationEdgeLengthPixels;
            planetDst.TessellationFactor = planetSrc.TessellationFactor;
            planetDst.TessellationRangeMeters = planetSrc.TessellationRangeMeters;

            ref var meshDst = ref maps.MeshUbo.Offset(meshOffset).As<PlanetRenderer.MeshUbo>();
            meshDst.MinHeight = meshSrc.MinHeight;
            meshDst.MaxHeight = meshSrc.MaxHeight;
            meshDst.BiomeBlendStrength = meshSrc.BiomeBlendStrength;
        }
    }

    private static ref PlanetRenderer.PlanetUbo PlanetUbo(PlanetRenderer renderer,
        FxReflect.TerrainUboMaps maps, int slot)
        => ref maps.RenderUbo.Offset(slot * renderer.PlanetUboStride).As<PlanetRenderer.PlanetUbo>();

    private static ref PlanetRenderer.MeshUbo MeshUbo(PlanetRenderer renderer,
        FxReflect.TerrainUboMaps maps, int slot)
        => ref maps.MeshUbo.Offset(slot * renderer.MeshUboStride).As<PlanetRenderer.MeshUbo>();
}
