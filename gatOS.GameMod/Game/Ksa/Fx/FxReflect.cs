using System.Reflection;
using Brutal.VulkanApi.Abstractions;
using Core;
using gatOS.SimFs.Commands;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>
///     The shared, lazily-resolved reflection accessors behind the four FX editors
///     (plans/FX_EDITORS_PLAN.md §§2–5). Everything KSA exposes publicly is reached directly — only
///     the handful of members the game keeps private are resolved here, once, and cached. Every
///     accessor is <b>null-tolerant</b>: a member that moved or vanished yields <c>null</c> plus a
///     human-readable reason, which the calling actuator turns into a health latch
///     (EOPNOTSUPP for that capability only) instead of an exception.
/// </summary>
/// <remarks>
///     Game-thread only by contract (the command drain and the sampler tick). The caches are plain
///     statics with no locking; <see cref="FieldInfo"/> resolution is idempotent, so a torn read
///     could at worst re-resolve.
/// </remarks>
internal static class FxReflect
{
    /// <summary>Health-latch key: the global volumetric-trail renderer instance.</summary>
    internal const string TrailAccessor = "fx.trail_renderer";

    /// <summary>Health-latch key: the trail renderer's private plume-trail settings object.</summary>
    internal const string TrailSettingsAccessor = "fx.trail_settings";

    /// <summary>Health-latch key: the volumetric-exhaust template registry (enumeration only).</summary>
    internal const string PlumeTemplatesAccessor = "fx.plume_templates";

    /// <summary>Health-latch key: the cloud renderer instance.</summary>
    internal const string CloudRendererAccessor = "fx.cloud_renderer";

    /// <summary>Health-latch key: the cloud renderer's private apply handles.</summary>
    internal const string CloudApplyAccessor = "fx.cloud_apply";

    /// <summary>Health-latch key: the planet (terrain) renderer instance.</summary>
    internal const string TerrainRendererAccessor = "fx.terrain_renderer";

    /// <summary>Health-latch key: the terrain UBO mapped-memory handles (the live-apply half).</summary>
    internal const string TerrainUboAccessor = "fx.terrain_ubo";

    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private const BindingFlags AnyStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static FieldInfo? _trailField;
    private static FieldInfo? _trailManagerField;
    private static FieldInfo? _trailSettingsField;
    private static FieldInfo? _transparenciesField;
    private static FieldInfo? _templateReferencesField;
    private static FieldInfo? _exhaustPressureField;
    private static FieldInfo? _exhaustThrottleField;
    private static FieldInfo? _cloudRendererField;
    private static FieldInfo? _cloudShadowsField;
    private static FieldInfo? _worleyField;
    private static FieldInfo? _renderUboMapField;
    private static FieldInfo? _meshUboMapField;
    private static bool _trailResolved;
    private static bool _trailSettingsResolved;
    private static bool _transparenciesResolved;
    private static bool _templateReferencesResolved;
    private static bool _exhaustModifiersResolved;
    private static bool _cloudApplyResolved;
    private static bool _terrainUboResolved;

    /// <summary>The cloud renderer's private handles the layer-apply path needs.</summary>
    /// <param name="Renderer">The engine renderer <c>UpdateStaticData</c> takes.</param>
    /// <param name="Shadows">The cloud-shadow renderer that re-populates the shadow atlas.</param>
    /// <param name="WorleyNoise">The shared 3-D worley-noise target <c>PopulatePlanets</c> takes.</param>
    internal readonly record struct CloudApplyHandles(
        Renderer Renderer, CloudShadowsRenderer Shadows, RenderImage WorleyNoise);

    /// <summary>The terrain UBO mapped-memory handles (both host-visible, coherent).</summary>
    /// <param name="RenderUbo">The per-celestial <c>PlanetUbo</c> ring.</param>
    /// <param name="MeshUbo">The per-celestial <c>MeshUbo</c> ring.</param>
    internal readonly record struct TerrainUboMaps(MappedMemory RenderUbo, MappedMemory MeshUbo);

    /// <summary>
    ///     The global volumetric-trail renderer, or null with a reason. Its editable settings are
    ///     public instance fields the renderer re-reads every frame, so no apply call exists.
    /// </summary>
    [KsaAnchor("Program.Instance._volumetricTrailRenderer (private instance field)",
        SourceFile = "KSA/Program.cs:184", Verified = "2026-09-02", GameVersion = "2026.9.7.5402",
        Risk = ChurnRisk.High,
        Notes = "Reflection: the only handle on the one VolumetricTrailRenderer. The renderer type and "
            + "every field gatOS writes are public; only the Program field is private. Null while the "
            + "renderer has not been constructed (pre-Program.Instance), which is a transient degrade.")]
    internal static VolumetricTrailRenderer? Trail(out string error)
    {
        error = "";
        if (Program.Instance is not { } program)
        {
            error = "the game program is not running yet";
            return null;
        }

        if (!_trailResolved)
        {
            _trailResolved = true;
            _trailField = typeof(Program).GetField("_volumetricTrailRenderer", AnyInstance);
        }

        if (_trailField?.GetValue(program) is VolumetricTrailRenderer trail)
            return trail;
        error = "Program._volumetricTrailRenderer is missing in this build";
        return null;
    }

    /// <summary>
    ///     The trail renderer's plume-trail settings object, or null with a reason. This is the
    ///     <b>same object the game's own "Plume Trails" debug window edits</b> — its "Profile"
    ///     section delegates to <c>PlumeTrailSegmentsManager.OnDrawProfileUi()</c>, which draws
    ///     <c>_settings.ExpansionTimeSeconds</c> — so gatOS exposes exactly what that window exposes.
    /// </summary>
    /// <remarks>
    ///     Two private hops, hence <see cref="ChurnRisk.High"/>: the 2026.8.3.5117 plume refactor
    ///     (revs 5059/5097) split <c>PlumeTrailSegmentsManager</c> apart and moved
    ///     <c>ExpansionTimeSeconds</c> off <see cref="VolumetricTrailRenderer"/> onto this object.
    ///     A future move degrades <c>render/expansion_time</c> alone (EOPNOTSUPP) — the other ten
    ///     trail fields are public on the renderer and unaffected.
    /// </remarks>
    [KsaAnchor("VolumetricTrailRenderer._plumeTrailSegmentsManager (private) → "
            + "PlumeTrailSegmentsManager._settings (private) → PlumeTrailSettings.ExpansionTimeSeconds (public)",
        SourceFile = "KSA/VolumetricTrailRenderer.cs:166 / KSA/PlumeTrailSegmentsManager.cs:19 / "
            + "KSA/PlumeTrailSettings.cs:11", Verified = "2026-09-02", GameVersion = "2026.9.7.5402",
        Risk = ChurnRisk.High,
        Notes = "Was VolumetricTrailRenderer.ExpansionTimeSeconds (a public field) up to 2026.7.10.5056; "
            + "revs 5059/5097 moved it onto the new PlumeTrailSettings. Same default (5f) and meaning. "
            + "Mirrors PlumeTrailSegmentsManager.OnDrawProfileUi, which edits this exact field.")]
    internal static PlumeTrailSettings? TrailSettings(VolumetricTrailRenderer trail, out string error)
    {
        error = "";
        if (!_trailSettingsResolved)
        {
            _trailSettingsResolved = true;
            _trailManagerField = typeof(VolumetricTrailRenderer)
                .GetField("_plumeTrailSegmentsManager", AnyInstance);
            _trailSettingsField = typeof(PlumeTrailSegmentsManager).GetField("_settings", AnyInstance);
        }

        if (_trailManagerField?.GetValue(trail) is PlumeTrailSegmentsManager manager
            && _trailSettingsField?.GetValue(manager) is PlumeTrailSettings settings)
            return settings;

        error = "VolumetricTrailRenderer._plumeTrailSegmentsManager/PlumeTrailSegmentsManager._settings "
                + "are missing in this build";
        return null;
    }

    /// <summary>
    ///     Every loaded volumetric-exhaust template, or null with a reason. The registry field is
    ///     internal; the collection type and its <c>GetList()</c> are public, so one field read is
    ///     the whole binding.
    /// </summary>
    [KsaAnchor("VolumetricExhaustTemplate.References (internal static field) .GetList()",
        SourceFile = "KSA/VolumetricExhaustTemplate.cs:38 / KSA/SerializedCollection.cs",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "Enumeration only — resolution by id uses the public VolumetricExhaustTemplate.Get(id). "
            + "The reader falls back to harvesting ids off live nozzles when this fails.")]
    internal static List<VolumetricExhaustTemplate>? PlumeTemplates(out string error)
    {
        error = "";
        if (!_templateReferencesResolved)
        {
            _templateReferencesResolved = true;
            _templateReferencesField = typeof(VolumetricExhaustTemplate).GetField("References", AnyStatic);
        }

        if (_templateReferencesField?.GetValue(null) is SerializedCollection<VolumetricExhaustTemplate> collection)
            return collection.GetList();
        error = "VolumetricExhaustTemplate.References is missing in this build";
        return null;
    }

    /// <summary>
    ///     The pressure/throttle pair the exhaust renderer feeds <c>UpdateModifiers</c> with, so the
    ///     propagation pass uses exactly the game's own inputs. Best-effort: falls back to
    ///     <c>(0, 1)</c> (vacuum, full throttle) when the private fields moved — harmless, because the
    ///     per-frame draw path recomputes the modifiers for every live nozzle anyway.
    /// </summary>
    [KsaAnchor("VolumetricExhaustRenderer._currentAtmosphericPressure/_debugThrottle (private fields); "
            + "Program.VolumetricExhaustRenderer (public static)",
        SourceFile = "KSA/VolumetricExhaustRenderer.cs:290,310 / KSA/Program.cs:467",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "Mirrors the in-game editor's propagation arguments. AddInstance() re-runs UpdateModifiers "
            + "with the live pressure + per-nozzle throttle every frame, so these values cannot disturb a "
            + "live plume beyond the current frame — hence best-effort rather than health-latched. "
            + "5402: VolumetricExhaustRenderer was heavily reworked (+1248 lines) and the two fields "
            + "moved in the reshuffle (:278 -> :290, :306 -> :310) with names and types (double, float) "
            + "intact; every other reflected FX field name in this file also still resolves.")]
    internal static (float Pressure, float Throttle) PlumeModifierArgs()
    {
        var renderer = Program.VolumetricExhaustRenderer;
        if (renderer is null)
            return (0f, 1f);

        if (!_exhaustModifiersResolved)
        {
            _exhaustModifiersResolved = true;
            var type = typeof(VolumetricExhaustRenderer);
            _exhaustPressureField = type.GetField("_currentAtmosphericPressure", AnyInstance);
            _exhaustThrottleField = type.GetField("_debugThrottle", AnyInstance);
        }

        var pressure = _exhaustPressureField?.GetValue(renderer) is double p ? (float)p : 0f;
        var throttle = _exhaustThrottleField?.GetValue(renderer) is float t ? t : 1f;
        return (pressure, throttle);
    }

    /// <summary>The cloud renderer, or null with a reason.</summary>
    [KsaAnchor("Program.Instance._planetTransparenciesRenderer (private) .GetCloudRenderer() (public)",
        SourceFile = "KSA/Program.cs:176 / KSA/PlanetTransparenciesRenderer.cs:75",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "One private field hop; the renderer accessor itself is public. Needed only for the apply "
            + "path — the cloud DATA hangs off the public AtmosphericBody.BodyTemplate.CloudsReference.")]
    internal static CloudRenderer? Clouds(out string error)
    {
        error = "";
        if (Program.Instance is not { } program)
        {
            error = "the game program is not running yet";
            return null;
        }

        if (!_transparenciesResolved)
        {
            _transparenciesResolved = true;
            _transparenciesField = typeof(Program).GetField("_planetTransparenciesRenderer", AnyInstance);
        }

        if (_transparenciesField?.GetValue(program) is not PlanetTransparenciesRenderer transparencies)
        {
            error = "Program._planetTransparenciesRenderer is missing in this build";
            return null;
        }

        if (transparencies.GetCloudRenderer() is { } clouds)
            return clouds;
        error = "the cloud renderer is not constructed";
        return null;
    }

    /// <summary>
    ///     The cloud renderer's private apply handles, or null with a reason. A failure degrades the
    ///     <b>apply</b> only: the caller still performs the data write, which the next natural
    ///     repopulate picks up.
    /// </summary>
    [KsaAnchor("CloudRenderer._renderer/_cloudShadowsRenderer/_worleyNoise3dTarget (private fields)",
        SourceFile = "KSA.Atmosphere.Rendering/CloudRenderer.cs:105,161,233",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "The three arguments CloudLayerRenderData.UpdateStaticData + CloudShadowsRenderer."
            + "PopulatePlanets need; the render-data map itself (_planetToCloudRenderData) is public. "
            + "_worleyNoise3dTarget was KSA.RenderTarget up to 2026.8.3.5117; rev 5154's dynamic-rendering "
            + "migration retyped it to KSA.Rendering.RenderImage (and PopulatePlanets' parameter with it). "
            + "The old KSA.RenderTarget/KSA.OffscreenTarget classes are gone; the NEW KSA.Rendering."
            + "RenderTarget is an unrelated type, so do not re-bind this to the name alone.")]
    internal static CloudApplyHandles? CloudApply(CloudRenderer renderer, out string error)
    {
        error = "";
        if (!_cloudApplyResolved)
        {
            _cloudApplyResolved = true;
            var type = typeof(CloudRenderer);
            _cloudRendererField = type.GetField("_renderer", AnyInstance);
            _cloudShadowsField = type.GetField("_cloudShadowsRenderer", AnyInstance);
            _worleyField = type.GetField("_worleyNoise3dTarget", AnyInstance);
        }

        if (_cloudRendererField?.GetValue(renderer) is Renderer engine
            && _cloudShadowsField?.GetValue(renderer) is CloudShadowsRenderer shadows
            && _worleyField?.GetValue(renderer) is RenderImage worley)
            return new CloudApplyHandles(engine, shadows, worley);

        error = "CloudRenderer._renderer/_cloudShadowsRenderer/_worleyNoise3dTarget are missing in this build";
        return null;
    }

    /// <summary>The planet (terrain) renderer, or null with a reason.</summary>
    [KsaAnchor("Program.GetPlanetRenderer() (public static)", SourceFile = "KSA/Program.cs:563",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Public accessor; null before the renderer exists. Backs the zero-reflection "
            + "PlanetRenderer.Wireframe toggle and the per-body slot lookups.")]
    internal static PlanetRenderer? Terrain(out string error)
    {
        error = "";
        if (Program.Instance is null)
        {
            error = "the game program is not running yet";
            return null;
        }

        if (Program.GetPlanetRenderer() is { } renderer)
            return renderer;
        error = "the planet renderer is not constructed";
        return null;
    }

    /// <summary>
    ///     The terrain UBO mapped-memory rings, or null with a reason. A failure degrades the
    ///     terrain <b>live apply</b> only — the reference-object writes still land and take effect on
    ///     the next renderer rebuild.
    /// </summary>
    [KsaAnchor("PlanetRenderer._renderUboMap/_meshUboMap (private readonly MappedMemory fields)",
        SourceFile = "KSA/PlanetRenderer.cs:263,265", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.High,
        Notes = "Host-visible coherent UBO rings, indexed (NumCelestials*frame + slot)*Stride with the "
            + "public PlanetUboStride/MeshUboStride/NumCelestials and the public RenderUboSlot/MeshUboSlot "
            + "helpers. gatOS writes frame slot 0 and mirrors into the other MaxFramesInFlight copies, "
            + "exactly as the in-game Terrain Editor does (PlanetRenderer.cs:2388-2398).")]
    internal static TerrainUboMaps? TerrainUbo(PlanetRenderer renderer, out string error)
    {
        error = "";
        if (!_terrainUboResolved)
        {
            _terrainUboResolved = true;
            var type = typeof(PlanetRenderer);
            _renderUboMapField = type.GetField("_renderUboMap", AnyInstance);
            _meshUboMapField = type.GetField("_meshUboMap", AnyInstance);
        }

        if (_renderUboMapField?.GetValue(renderer) is MappedMemory render
            && _meshUboMapField?.GetValue(renderer) is MappedMemory mesh)
            return new TerrainUboMaps(render, mesh);

        error = "PlanetRenderer._renderUboMap/_meshUboMap are missing in this build";
        return null;
    }

    /// <summary>Latches one FX capability degraded (logged once) and returns the EOPNOTSUPP result.</summary>
    internal static CommandResult Degrade(KsaHealth health, string accessor, string error)
    {
        health.Fault(accessor, SafeUt(), error);
        return new CommandResult(CommandOutcome.Unsupported, $"{accessor}: {error}");
    }

    /// <summary>Clears an FX capability's latch after a successful resolve.</summary>
    internal static void Healthy(KsaHealth health, string accessor) => health.Clear(accessor);

    private static double SafeUt()
    {
        try
        {
            return Universe.GetElapsedSeconds();
        }
        catch
        {
            return 0;
        }
    }
}
