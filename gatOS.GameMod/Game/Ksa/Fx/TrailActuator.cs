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
    ///     Sets one trail field, capturing its pristine value on the first write.
    ///     <c>Unsupported</c> (health-latched) while the renderer cannot be resolved.
    /// </summary>
    internal static CommandResult Set(KsaHealth health, FxFieldSpec spec, string field,
        IReadOnlyList<double> values)
    {
        if (FxReflect.Trail(out var error) is not { } trail)
            return FxReflect.Degrade(health, FxReflect.TrailAccessor, error);
        FxReflect.Healthy(health, FxReflect.TrailAccessor);

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
        SourceFile = "KSA/Program.cs:4610 / KSA/VolumetricTrailRenderer.cs:259",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Medium,
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
            + "StepSizeDistanceScale,ExpansionTimeSeconds,ErosionMaxDepth,ErosionEdgeSharpness,"
            + "SelfShadowStepCount,LightBrightness,SkyAmbientBrightness,DebugTrailColor} (public fields)",
        SourceFile = "KSA/VolumetricTrailRenderer.cs:173-196", Verified = "2026-08-01",
        GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Medium,
        Notes = "Plain public instance fields, read fresh by the renderer every frame — read-back is "
            + "always the live value and a write needs no apply call. Floats, so a value round-trips at "
            + "single precision.")]
    internal static bool TryRead(VolumetricTrailRenderer r, FxFieldSpec spec, double[] dst)
    {
        switch (spec.Key)
        {
            case "render/max_distance": dst[0] = r.MaxDistance; return true;
            case "render/voxel_first_slice": dst[0] = r.VoxelDepthFirstSliceThickness; return true;
            case "render/min_step_size": dst[0] = r.MinStepSize; return true;
            case "render/step_size_distance_scale": dst[0] = r.StepSizeDistanceScale; return true;
            case "render/expansion_time": dst[0] = r.ExpansionTimeSeconds; return true;
            case "render/erosion_max_depth": dst[0] = r.ErosionMaxDepth; return true;
            case "render/erosion_edge_sharpness": dst[0] = r.ErosionEdgeSharpness; return true;
            case "render/self_shadow_steps": dst[0] = r.SelfShadowStepCount; return true;
            case "render/light_brightness": dst[0] = r.LightBrightness; return true;
            case "render/sky_ambient_brightness": dst[0] = r.SkyAmbientBrightness; return true;
            case "render/trail_color":
                dst[0] = r.DebugTrailColor.X;
                dst[1] = r.DebugTrailColor.Y;
                dst[2] = r.DebugTrailColor.Z;
                dst[3] = r.DebugTrailColor.W;
                return true;
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
            case "render/expansion_time": r.ExpansionTimeSeconds = (float)v[0]; return true;
            case "render/erosion_max_depth": r.ErosionMaxDepth = (float)v[0]; return true;
            case "render/erosion_edge_sharpness": r.ErosionEdgeSharpness = (float)v[0]; return true;
            case "render/self_shadow_steps": r.SelfShadowStepCount = (int)Math.Round(v[0]); return true;
            case "render/light_brightness": r.LightBrightness = (float)v[0]; return true;
            case "render/sky_ambient_brightness": r.SkyAmbientBrightness = (float)v[0]; return true;
            case "render/trail_color":
                r.DebugTrailColor = new float4((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
                return true;
            default: return false;
        }
    }
}
