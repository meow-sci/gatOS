using Brutal.Numerics;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
using KSA;
using KSA.Atmosphere.Rendering;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>
///     The <c>/sim/debug/clouds</c> actuator (plans/FX_EDITORS_PLAN.md §4): the game's "Clouds"
///     editor as files, scoped <b>per body → per layer → per cloud type</b>. The data itself hangs
///     off the public <c>AtmosphericBody.BodyTemplate.CloudsReference</c> (zero reflection); only the
///     render-side apply needs the cloud renderer's private handles, and a failure there degrades the
///     apply alone — the data write still lands and the next natural repopulate picks it up.
///     Game-thread only (the Frame command drain).
/// </summary>
/// <remarks>
///     <c>NoiseScale</c> is deliberately <b>not</b> exposed: changing it forces
///     <c>CloudRenderer.RecreateLayerPipelines()</c>, which destroys and rebuilds Vulkan pipelines.
///     Excluding it means gatOS's apply path can never recreate a pipeline.
/// </remarks>
internal static class CloudActuator
{
    /// <summary>
    ///     Sets one cloud field, capturing its pristine value on the first write, then re-uploads the
    ///     affected layer(s). <c>NotFound</c> for an unknown body or an out-of-range layer/type index.
    /// </summary>
    internal static CommandResult Set(KsaHealth health, string id, FxFieldMatch match, string field,
        IReadOnlyList<double> values)
    {
        if (Resolve(id) is not { } target)
            return new CommandResult(CommandOutcome.NotFound, $"body '{id}' has no clouds");
        var (body, clouds) = target;
        if (!Addresses(clouds, match.Indices))
            return new CommandResult(CommandOutcome.NotFound, $"'{field}' is out of range on '{id}'");

        var pristine = new double[match.Spec.Arity];
        if (TryRead(clouds, match.Spec, match.Indices, pristine))
            FxPristine.Capture(FxFamily.Clouds, id, field, pristine);
        if (!TryWrite(clouds, match.Spec, match.Indices, values))
            return new CommandResult(CommandOutcome.Invalid, $"'{field}' is not bound in this game build");

        FxEditorReader.Invalidate();
        return Apply(health, body, clouds, LayerOf(match));
    }

    /// <summary>Restores one body's pristine cloud values (a no-op when nothing was captured).</summary>
    internal static CommandResult Reset(KsaHealth health, string id)
    {
        if (Resolve(id) is not { } target)
            return new CommandResult(CommandOutcome.NotFound, $"body '{id}' has no clouds");
        var (body, clouds) = target;
        FxPristine.Restore(FxFamily.Clouds, id);
        FxEditorReader.Invalidate();
        return Apply(health, body, clouds, layer: -1);
    }

    /// <summary>Replays one captured field (the <see cref="FxPristine"/> restore path).</summary>
    internal static bool Restore(string id, string field, double[] values)
    {
        if (Resolve(id) is not { } target
            || FxCatalog.Match(FxCatalog.Clouds, field) is not { } match
            || !Addresses(target.Clouds, match.Indices))
            return false;
        var clouds = target.Clouds;
        var applied = TryWrite(clouds, match.Spec, match.Indices, values);
        if (applied)
            FxEditorReader.Invalidate();
        return applied;
    }

    /// <summary>
    ///     The body named by <paramref name="id"/> together with its cloud definition, or null when
    ///     the body is gone or carries no clouds.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); AtmosphericBody.BodyTemplate.CloudsReference",
        SourceFile = "KSA/Universe.cs / KSA/AstronomicalTemplate.cs:60",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Low,
        Notes = "Same enumeration the telemetry sampler uses. CloudsReference lives on AstronomicalTemplate "
            + "(the base of every CelestialTemplate); the apply path needs an AtmosphericBody specifically, "
            + "so only atmospheric bodies are addressable.")]
    internal static (AtmosphericBody Body, CloudsReference Clouds)? Resolve(string id)
    {
        if (id.Length == 0 || Universe.CurrentSystem is not { } system)
            return null;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (astronomical is AtmosphericBody body && body.Id == id
                && body.BodyTemplate.CloudsReference is { } clouds)
                return (body, clouds);
        return null;
    }

    /// <summary>Whether a field's captured indices address a layer / cloud type that exists.</summary>
    internal static bool Addresses(CloudsReference clouds, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
            return true;
        if (indices[0] >= clouds.Layers.Count)
            return false;
        if (indices.Count == 1)
            return true;
        var types = clouds.Layers[indices[0]].VolumetricCloud?.CloudTypes;
        return types is not null && indices[1] < types.Count;
    }

    /// <summary>
    ///     Reads one cloud field into <paramref name="dst"/> (length = the spec's arity). Shared by
    ///     the sampler and the pristine capture, so a written value reads back through exactly the
    ///     accessor it was written through.
    /// </summary>
    [KsaAnchor("CloudsReference.{OrbitTransitionStartAltitude,OrbitTransitionEndAltitude,MaxShadowsAltitude,"
            + "Layers}; CloudLayerReference.{RotationSpeed,VolumetricCloud,TwoDimensionalCloud}; "
            + "VolumetricCloudReference.{Detail.Size,ColorRgb,Noise.ScrollSpeed,Raymarching,CloudTypes}; "
            + "RaymarchingReference.{Step.Size,Step.Scale,Step.Maximum,LightDistance,LightSamples}; "
            + "CloudTypeReference.{StartAltitude,Height,Density,EdgeSharpness,MultipleScatteringBrightness,"
            + "CloudShape.InterpolateShapes} (all public)",
        SourceFile = "KSA/CloudsReference.cs / CloudLayerReference.cs / VolumetricCloudReference.cs / "
            + "TwoDimensionalCloudReference.cs / RaymarchingReference.cs / RaymarchingStepReference.cs / "
            + "CloudTypeReference.cs / CloudShapeReference.cs",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "Read half of the /sim/debug/clouds leaf set. Distances are read in the unit the leaf "
            + "publishes (km for the shared/detail rows, m for the raymarch/cloud-type rows) so a write "
            + "round-trips; colours read the serialized R/G/B fields.")]
    internal static bool TryRead(CloudsReference c, FxFieldSpec spec, IReadOnlyList<int> idx, double[] dst)
    {
        switch (spec.Key)
        {
            case "shared/transition_start_km": return Km(c.OrbitTransitionStartAltitude, dst);
            case "shared/transition_end_km": return Km(c.OrbitTransitionEndAltitude, dst);
            case "shared/max_shadows_altitude_km": return Km(c.MaxShadowsAltitude, dst);
        }

        if (idx.Count == 0 || idx[0] >= c.Layers.Count)
            return false;
        var layer = c.Layers[idx[0]];
        switch (spec.Key)
        {
            case "layers/*/rotation_speed":
                if (layer.RotationSpeed is not { } rotation)
                    return false;
                dst[0] = rotation.X;
                dst[1] = rotation.Y;
                dst[2] = rotation.Z;
                return true;
            case "layers/*/detail_tile_km": return Km(layer.VolumetricCloud?.Detail?.Size, dst);
            case "layers/*/color": return Color(layer.VolumetricCloud?.ColorRgb, dst);
            case "layers/*/scroll_speed": return Scalar(layer.VolumetricCloud?.Noise?.ScrollSpeed, dst);
            case "layers/*/two_d/lambertian": return Scalar(layer.TwoDimensionalCloud?.Lambertian, dst);
            case "layers/*/two_d/color": return Color(layer.TwoDimensionalCloud?.ColorRgb, dst);
            case "layers/*/raymarch/step_size": return Meters(layer.VolumetricCloud?.Raymarching?.Step?.Size, dst);
            case "layers/*/raymarch/step_scale":
                if (layer.VolumetricCloud?.Raymarching?.Step is not { } step)
                    return false;
                dst[0] = step.Scale;
                return true;
            case "layers/*/raymarch/max_step": return Meters(layer.VolumetricCloud?.Raymarching?.Step?.Maximum, dst);
            case "layers/*/raymarch/light_distance": return Meters(layer.VolumetricCloud?.Raymarching?.LightDistance, dst);
            case "layers/*/raymarch/light_samples": return Scalar(layer.VolumetricCloud?.Raymarching?.LightSamples, dst);
        }

        var types = layer.VolumetricCloud?.CloudTypes;
        if (types is null || idx.Count < 2 || idx[1] >= types.Count)
            return false;
        var type = types[idx[1]];
        switch (spec.Key)
        {
            case "layers/*/types/*/start_altitude": return Meters(type.StartAltitude, dst);
            case "layers/*/types/*/height": return Meters(type.Height, dst);
            case "layers/*/types/*/density": return Scalar(type.Density, dst);
            case "layers/*/types/*/edge_sharpness": return Scalar(type.EdgeSharpness, dst);
            case "layers/*/types/*/multi_scatter": return Scalar(type.MultipleScatteringBrightness, dst);
            case "layers/*/types/*/interpolate":
                if (type.CloudShape is not { } shape)
                    return false;
                dst[0] = shape.InterpolateShapes ? 1 : 0;
                return true;
            default: return false;
        }
    }

    /// <summary>Applies one validated payload. False ⇒ the field is not bound in this build.</summary>
    [KsaAnchor("Cloud reference writes: DistanceReference/Vector3Reference/DoubleReference/ColorRgbReference "
            + "construct-new (+ ColorRgbReference.OnDataLoad), RaymarchingStepReference.Scale and "
            + "CloudShapeReference.InterpolateShapes in place",
        SourceFile = "KSA.Atmosphere.Rendering/CloudRenderer.cs:1370-1560 (the in-game editor's write sites)",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "DistanceReference and Vector3Reference cache a derived value behind a private field and "
            + "ColorRgbReference.Value has a protected setter, so all three MUST be replaced wholesale — "
            + "mutating them in place silently does nothing. DoubleReference exposes a plain public Value "
            + "field with no cache, so those are written in place (both idioms appear in the game's own "
            + "editor). NoiseScale is deliberately absent (it would force a pipeline rebuild).")]
    private static bool TryWrite(CloudsReference c, FxFieldSpec spec, IReadOnlyList<int> idx,
        IReadOnlyList<double> v)
    {
        switch (spec.Key)
        {
            case "shared/transition_start_km":
                c.OrbitTransitionStartAltitude = NewKm(v[0]);
                return true;
            case "shared/transition_end_km":
                c.OrbitTransitionEndAltitude = NewKm(v[0]);
                return true;
            case "shared/max_shadows_altitude_km":
                c.MaxShadowsAltitude = NewKm(v[0]);
                return true;
        }

        if (idx.Count == 0 || idx[0] >= c.Layers.Count)
            return false;
        var layer = c.Layers[idx[0]];
        switch (spec.Key)
        {
            case "layers/*/rotation_speed":
                layer.RotationSpeed = new Vector3Reference(new double3(v[0], v[1], v[2]));
                return true;
            case "layers/*/detail_tile_km":
                if (layer.VolumetricCloud?.Detail is not { } detail)
                    return false;
                detail.Size = NewKm(v[0]);
                return true;
            case "layers/*/color":
                if (layer.VolumetricCloud is not { } volumetric)
                    return false;
                volumetric.ColorRgb = NewColor(v);
                return true;
            case "layers/*/scroll_speed": return Set(layer.VolumetricCloud?.Noise?.ScrollSpeed, v[0]);
            case "layers/*/two_d/lambertian": return Set(layer.TwoDimensionalCloud?.Lambertian, v[0]);
            case "layers/*/two_d/color":
                if (layer.TwoDimensionalCloud is not { } flat)
                    return false;
                flat.ColorRgb = NewColor(v);
                return true;
            case "layers/*/raymarch/step_size":
                if (layer.VolumetricCloud?.Raymarching?.Step is not { } stepSize)
                    return false;
                stepSize.Size = NewMeters(v[0]);
                return true;
            case "layers/*/raymarch/step_scale":
                if (layer.VolumetricCloud?.Raymarching?.Step is not { } stepScale)
                    return false;
                stepScale.Scale = (float)v[0];
                return true;
            case "layers/*/raymarch/max_step":
                if (layer.VolumetricCloud?.Raymarching?.Step is not { } stepMax)
                    return false;
                stepMax.Maximum = NewMeters(v[0]);
                return true;
            case "layers/*/raymarch/light_distance":
                if (layer.VolumetricCloud?.Raymarching is not { } raymarching)
                    return false;
                raymarching.LightDistance = NewMeters(v[0]);
                return true;
            case "layers/*/raymarch/light_samples":
                return Set(layer.VolumetricCloud?.Raymarching?.LightSamples, Math.Round(v[0]));
        }

        var types = layer.VolumetricCloud?.CloudTypes;
        if (types is null || idx.Count < 2 || idx[1] >= types.Count)
            return false;
        var type = types[idx[1]];
        switch (spec.Key)
        {
            case "layers/*/types/*/start_altitude":
                type.StartAltitude = NewMeters(v[0]);
                return true;
            case "layers/*/types/*/height":
                type.Height = NewMeters(v[0]);
                return true;
            case "layers/*/types/*/density": return Set(type.Density, v[0]);
            case "layers/*/types/*/edge_sharpness": return Set(type.EdgeSharpness, v[0]);
            case "layers/*/types/*/multi_scatter": return Set(type.MultipleScatteringBrightness, v[0]);
            case "layers/*/types/*/interpolate":
                if (type.CloudShape is not { } shape)
                    return false;
                shape.InterpolateShapes = v[0] > 0.5;
                return true;
            default: return false;
        }
    }

    /// <summary>
    ///     Re-derives the GPU-side render data for the affected layer (or every layer, when
    ///     <paramref name="layer"/> is negative — a shared-settings write or a reset), exactly as the
    ///     in-game editor does. A missing private handle degrades <b>only</b> the apply: the data
    ///     write already landed, so the change appears on the next natural repopulate.
    /// </summary>
    [KsaAnchor("CloudLayerReference.OnDataLoad(Mod.Empty); CloudRenderer._planetToCloudRenderData (public); "
            + "CloudLayerRenderData.UpdateStaticData(Renderer, AtmosphericBody, CloudLayerReference, "
            + "float, float, float); CloudShadowsRenderer.PopulatePlanets(Dictionary<KeyHash, "
            + "CloudLayerRenderData[]>, RenderTarget)",
        SourceFile = "KSA.Atmosphere.Rendering/CloudRenderer.cs:1570-1595 / CloudLayerRenderData.cs:347 / "
            + "CloudShadowsRenderer.cs:76",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "Keyed on the public Astronomical.Hash, per-layer index into the render-data array, then "
            + "one shadow-atlas repopulate — the editor's exact sequence. NoiseScale (the only field that "
            + "would additionally need RecreateLayerPipelines) is not exposed, so this never rebuilds a "
            + "pipeline.")]
    private static CommandResult Apply(KsaHealth health, AtmosphericBody body, CloudsReference clouds, int layer)
    {
        if (FxReflect.Clouds(out var rendererError) is not { } renderer)
            return Degraded(health, FxReflect.CloudRendererAccessor, rendererError);
        FxReflect.Healthy(health, FxReflect.CloudRendererAccessor);

        if (FxReflect.CloudApply(renderer, out var applyError) is not { } handles)
            return Degraded(health, FxReflect.CloudApplyAccessor, applyError);
        FxReflect.Healthy(health, FxReflect.CloudApplyAccessor);

        if (!renderer._planetToCloudRenderData.TryGetValue(body.Hash, out var renderData))
            return CommandResult.Ok; // body not currently rendered; the data write stands

        var start = (float)(double)clouds.OrbitTransitionStartAltitude;
        var end = (float)(double)clouds.OrbitTransitionEndAltitude;
        var maxShadow = (float)(double)clouds.MaxShadowsAltitude;
        for (var i = 0; i < clouds.Layers.Count && i < renderData.Length; i++)
        {
            if (layer >= 0 && i != layer)
                continue;
            clouds.Layers[i].OnDataLoad(KSA.Mod.Empty);
            renderData[i].UpdateStaticData(handles.Renderer, body, clouds.Layers[i], start, end, maxShadow);
        }

        handles.Shadows.PopulatePlanets(renderer._planetToCloudRenderData, handles.WorleyNoise);
        return CommandResult.Ok;
    }

    /// <summary>The layer a match addresses, or -1 for a shared (all-layer) field.</summary>
    private static int LayerOf(FxFieldMatch match) => match.Indices.Count == 0 ? -1 : match.Indices[0];

    /// <summary>
    ///     A degraded apply is still a successful write: the reference data changed and the renderer
    ///     picks it up on its next repopulate, so this reports Ok while latching the capability.
    /// </summary>
    private static CommandResult Degraded(KsaHealth health, string accessor, string error)
    {
        FxReflect.Degrade(health, accessor, error);
        return CommandResult.Ok;
    }

    private static DistanceReference NewKm(double km) => new(km, DistanceUnit.Kilometers);

    private static DistanceReference NewMeters(double meters) => new(meters, DistanceUnit.Meters);

    private static ColorRgbReference NewColor(IReadOnlyList<double> v)
    {
        var color = new ColorRgbReference(new float3((float)v[0], (float)v[1], (float)v[2]));
        color.OnDataLoad(KSA.Mod.Empty);
        return color;
    }

    private static bool Km(DistanceReference? reference, double[] dst)
    {
        if (reference is null)
            return false;
        dst[0] = reference.InKilometers();
        return true;
    }

    private static bool Meters(DistanceReference? reference, double[] dst)
    {
        if (reference is null)
            return false;
        dst[0] = reference.InMeters();
        return true;
    }

    private static bool Scalar(DoubleReference? reference, double[] dst)
    {
        if (reference is null)
            return false;
        dst[0] = reference.Value;
        return true;
    }

    private static bool Color(ColorRgbReference? reference, double[] dst)
    {
        if (reference is null)
            return false;
        dst[0] = reference.R;
        dst[1] = reference.G;
        dst[2] = reference.B;
        return true;
    }

    private static bool Set(DoubleReference? reference, double value)
    {
        if (reference is null)
            return false;
        reference.Value = value;
        return true;
    }
}
