using Brutal.Numerics;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>
///     The <c>/sim/debug/plumetrail</c> actuator (plans/FX_EDITORS_PLAN.md §3): the game's "Plume
///     Trails" editor as files. Scope is <b>global</b> — there is one
///     <see cref="VolumetricTrailRenderer"/> for the whole game — and every exposed setting is a
///     public instance field the renderer re-reads each frame, so a write needs no apply call.
///     Game-thread only (the Frame command drain).
/// </summary>
internal static class TrailActuator
{
    /// <summary>The singleton entity's id in the snapshot/command surface.</summary>
    private const string Entity = "";

    /// <summary>
    ///     The one field that does not live on the renderer itself: since 2026.8.3.5117 (revs
    ///     5059/5097) it hangs off the renderer's private <c>PlumeTrailSettings</c>, which is what
    ///     the game's own Plume Trails debug window edits in its "Profile" section.
    /// </summary>
    private const string ExpansionTimeKey = "render/expansion_time";

    /// <summary>
    ///     Sets one trail field, capturing its pristine value on the first write.
    ///     <c>Unsupported</c> (health-latched) while the renderer cannot be resolved.
    /// </summary>
    internal static CommandResult Set(KsaHealth health, FxFieldSpec spec, string field,
        IReadOnlyList<double> values)
    {
        if (FxReflect.Trail(out var error) is not { } trail)
            return FxReflect.Degrade(health, FxReflect.TrailAccessor, error);
        FxReflect.Healthy(health, FxReflect.TrailAccessor);

        // The settings-backed field latches its own capability, so a future move of the private
        // settings hop degrades render/expansion_time alone and leaves the other ten fields healthy.
        if (spec.Key == ExpansionTimeKey)
        {
            if (FxReflect.TrailSettings(trail, out var settingsError) is null)
                return FxReflect.Degrade(health, FxReflect.TrailSettingsAccessor, settingsError);
            FxReflect.Healthy(health, FxReflect.TrailSettingsAccessor);
        }

        var pristine = new double[spec.Arity];
        if (TryRead(trail, spec, pristine))
            FxPristine.Capture(FxFamily.PlumeTrail, Entity, field, pristine);
        if (!TryWrite(trail, spec, values))
            return new CommandResult(CommandOutcome.Invalid, $"'{field}' is not bound in this game build");

        FxEditorReader.Invalidate();
        return CommandResult.Ok;
    }

    /// <summary>Restores the trail renderer's pristine values (a no-op when nothing was captured).</summary>
    internal static CommandResult Reset(KsaHealth health)
    {
        if (FxReflect.Trail(out var error) is null)
            return FxReflect.Degrade(health, FxReflect.TrailAccessor, error);
        FxReflect.Healthy(health, FxReflect.TrailAccessor);
        FxPristine.Restore(FxFamily.PlumeTrail, Entity);
        FxEditorReader.Invalidate();
        return CommandResult.Ok;
    }

    /// <summary>Drops every live trail — a one-shot, not a settings change.</summary>
    [KsaAnchor("Program.Instance.ClearPlumeTrails() → VolumetricTrailRenderer.ClearPlumeTrails()",
        SourceFile = "KSA/Program.cs / KSA/VolumetricTrailRenderer.cs:251",
        Verified = "2026-08-01", GameVersion = "2026.8.3.5117", Risk = ChurnRisk.Medium,
        Notes = "DISCREPANCY vs plans/FX_EDITORS_PLAN.md §3: ClearPlumeTrails is a public INSTANCE method "
            + "on Program (not static), so it is reached through the public Program.Instance — no reflection.")]
    internal static CommandResult Clear(KsaHealth health)
    {
        if (Program.Instance is not { } program)
            return FxReflect.Degrade(health, FxReflect.TrailAccessor, "the game program is not running yet");
        program.ClearPlumeTrails();
        FxReflect.Healthy(health, FxReflect.TrailAccessor);
        return CommandResult.Ok;
    }

    /// <summary>Replays one captured field (the <see cref="FxPristine"/> restore path).</summary>
    internal static bool Restore(string field, double[] values)
    {
        if (FxReflect.Trail(out _) is not { } trail
            || FxCatalog.Match(FxCatalog.PlumeTrail, field) is not { } match)
            return false;
        var applied = TryWrite(trail, match.Spec, values);
        if (applied)
            FxEditorReader.Invalidate();
        return applied;
    }

    /// <summary>
    ///     Reads one trail field into <paramref name="dst"/> (length = the spec's arity). Shared by
    ///     the sampler and the pristine capture so read-back and write agree by construction.
    /// </summary>
    [KsaAnchor("VolumetricTrailRenderer.{MaxDistance,VoxelDepthFirstSliceThickness,MinStepSize,"
            + "StepSizeDistanceScale,ErosionMaxDepth,ErosionEdgeSharpness,SelfShadowStepCount,"
            + "LightBrightness,SkyAmbientBrightness} (public fields); "
            + "PlumeTrailSettings.ExpansionTimeSeconds via FxReflect.TrailSettings",
        SourceFile = "KSA/VolumetricTrailRenderer.cs:172-192 / KSA/PlumeTrailSettings.cs:9",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Plain public instance fields, read fresh by the renderer every frame — read-back is "
            + "always the live value and a write needs no apply call. Floats, so a value round-trips at "
            + "single precision. ExpansionTimeSeconds is the exception: revs 5059/5097 moved it off the "
            + "renderer onto the private PlumeTrailSettings (High risk, separately latched), matching "
            + "where the game's own Plume Trails debug window now edits it. 5402: the global float4 "
            + "DebugTrailColor was REMOVED (with its 'trailColor' debug-window row and the "
            + "VolumetricTrailParams.TrailColor UBO slot); colour, density and lifetime are now per "
            + "PlumeTrailTemplate asset (Color/DensityMultiplier/Lifetime, PlumeTrailAssets.xml) and "
            + "ride each SubmitEmitter call — so render/trail_color was retired from /sim rather than "
            + "re-bound (nothing global remains to bind). PlumeTrailSettings.SegmentLifetimeSeconds "
            + "also left (never bound). The nine remaining fields are byte-identical.")]
    internal static bool TryRead(VolumetricTrailRenderer r, FxFieldSpec spec, double[] dst)
    {
        switch (spec.Key)
        {
            case "render/max_distance": dst[0] = r.MaxDistance; return true;
            case "render/voxel_first_slice": dst[0] = r.VoxelDepthFirstSliceThickness; return true;
            case "render/min_step_size": dst[0] = r.MinStepSize; return true;
            case "render/step_size_distance_scale": dst[0] = r.StepSizeDistanceScale; return true;
            case ExpansionTimeKey:
                if (FxReflect.TrailSettings(r, out _) is not { } readSettings)
                    return false;
                dst[0] = readSettings.ExpansionTimeSeconds;
                return true;
            case "render/erosion_max_depth": dst[0] = r.ErosionMaxDepth; return true;
            case "render/erosion_edge_sharpness": dst[0] = r.ErosionEdgeSharpness; return true;
            case "render/self_shadow_steps": dst[0] = r.SelfShadowStepCount; return true;
            case "render/light_brightness": dst[0] = r.LightBrightness; return true;
            case "render/sky_ambient_brightness": dst[0] = r.SkyAmbientBrightness; return true;
            default: return false;
        }
    }

    /// <summary>Applies one validated payload. False ⇒ the field is not bound in this build.</summary>
    private static bool TryWrite(VolumetricTrailRenderer r, FxFieldSpec spec, IReadOnlyList<double> v)
    {
        switch (spec.Key)
        {
            case "render/max_distance": r.MaxDistance = (float)v[0]; return true;
            case "render/voxel_first_slice": r.VoxelDepthFirstSliceThickness = (float)v[0]; return true;
            case "render/min_step_size": r.MinStepSize = (float)v[0]; return true;
            case "render/step_size_distance_scale": r.StepSizeDistanceScale = (float)v[0]; return true;
            case ExpansionTimeKey:
                if (FxReflect.TrailSettings(r, out _) is not { } writeSettings)
                    return false;
                writeSettings.ExpansionTimeSeconds = (float)v[0];
                return true;
            case "render/erosion_max_depth": r.ErosionMaxDepth = (float)v[0]; return true;
            case "render/erosion_edge_sharpness": r.ErosionEdgeSharpness = (float)v[0]; return true;
            case "render/self_shadow_steps": r.SelfShadowStepCount = (int)Math.Round(v[0]); return true;
            case "render/light_brightness": r.LightBrightness = (float)v[0]; return true;
            case "render/sky_ambient_brightness": r.SkyAmbientBrightness = (float)v[0]; return true;
            default: return false;
        }
    }
}
