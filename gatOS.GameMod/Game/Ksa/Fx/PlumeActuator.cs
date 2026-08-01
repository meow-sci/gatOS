using Brutal.Numerics;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>
///     The <c>/sim/debug/engineplume</c> actuator (plans/FX_EDITORS_PLAN.md §2): edits one shared
///     <see cref="VolumetricExhaustTemplate"/> and propagates the change to every live nozzle that
///     references it. <b>Scope is per template, not per engine</b> — a write repaints every plume in
///     the universe using that template, exactly like the game's own "Volumetric Exhausts" editor.
///     Game-thread only (the Frame command drain).
/// </summary>
internal static class PlumeActuator
{
    /// <summary>
    ///     Sets one template field. Captures the pristine value on the first write to that field,
    ///     applies it, then runs the propagation pass. <c>NotFound</c> when the id is unknown,
    ///     <c>Invalid</c> when the field has no binding in this build.
    /// </summary>
    internal static CommandResult Set(string id, FxFieldSpec spec, string field,
        IReadOnlyList<double> values)
    {
        if (Resolve(id) is not { } template)
            return new CommandResult(CommandOutcome.NotFound, $"exhaust template '{id}' is gone");

        var pristine = new double[spec.Arity];
        if (TryRead(template, spec, pristine))
            FxPristine.Capture(FxFamily.EnginePlume, id, field, pristine);
        if (!TryWrite(template, spec, values))
            return new CommandResult(CommandOutcome.Invalid, $"'{field}' is not bound in this game build");

        Propagate();
        FxEditorReader.Invalidate();
        return CommandResult.Ok;
    }

    /// <summary>Restores one template's pristine values (a no-op when nothing was captured).</summary>
    internal static CommandResult Reset(string id)
    {
        if (Resolve(id) is null)
            return new CommandResult(CommandOutcome.NotFound, $"exhaust template '{id}' is gone");
        FxPristine.Restore(FxFamily.EnginePlume, id);
        Propagate();
        FxEditorReader.Invalidate();
        return CommandResult.Ok;
    }

    /// <summary>Replays one captured field (the <see cref="FxPristine"/> restore path).</summary>
    internal static bool Restore(string id, string field, double[] values)
    {
        if (Resolve(id) is not { } template
            || FxCatalog.Match(FxCatalog.EnginePlume, field) is not { } match)
            return false;
        var applied = TryWrite(template, match.Spec, values);
        if (applied)
            FxEditorReader.Invalidate();
        return applied;
    }

    [KsaAnchor("VolumetricExhaustTemplate.Get(string) (public static)",
        SourceFile = "KSA/VolumetricExhaustTemplate.cs:48", Verified = "2026-08-01",
        GameVersion = "2026.7.10.5056", Risk = ChurnRisk.Medium,
        Notes = "Hash lookup into the shared template registry; null for an unknown id (⇒ ENOENT).")]
    internal static VolumetricExhaustTemplate? Resolve(string id)
        => id.Length == 0 ? null : VolumetricExhaustTemplate.Get(id);

    /// <summary>
    ///     Reads one field's live value into <paramref name="dst"/> (length = the spec's arity).
    ///     Shared by the sampler (building the snapshot) and the pristine capture, so a written value
    ///     always reads back through exactly the same accessor it was written through.
    /// </summary>
    [KsaAnchor("VolumetricExhaustTemplate.{LengthWeights,Absorption,Emission,Noise,Quality} → "
            + "DoubleReference.Value / BoolReference.Value / ColorRgbReference.{R,G,B} / "
            + "Quality.VolumetricVesselShadows (all public fields)",
        SourceFile = "KSA/VolumetricExhaustTemplate.cs / LengthWeights.cs / Absorption.cs / Emission.cs / "
            + "ColorGradient.cs / MachDiamonds.cs / Noise.cs / Quality.cs",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "Read half of the /sim/debug/engineplume leaf set. Colours are read off the serialized "
            + "R/G/B fields (not the derived Color.Preset Value), which is what a construct-new write sets.")]
    internal static bool TryRead(VolumetricExhaustTemplate t, FxFieldSpec spec, double[] dst)
    {
        switch (spec.Key)
        {
            case "core/radius_weight": return Scalar(t.LengthWeights?.RadiusWeight, dst);
            case "core/nozzle_pressure_weight": return Scalar(t.LengthWeights?.NozzlePressureWeight, dst);
            case "core/jet_expansion_weight": return Scalar(t.LengthWeights?.JetExpansionWeight, dst);
            case "core/exit_mach_weight": return Scalar(t.LengthWeights?.ExitMachNumberWeight, dst);
            case "absorption/density": return Scalar(t.Absorption?.Density, dst);
            case "absorption/fake_clean_burn": return Flag(t.Absorption?.FakeCleanBurnInAtmosphere, dst);
            case "absorption/scattering_brightness": return Scalar(t.Absorption?.ScatteringBrightness, dst);
            case "absorption/phase_eccentricity": return Scalar(t.Absorption?.ScatteringPhaseEccentricity, dst);
            case "absorption/refraction_intensity": return Scalar(t.Absorption?.RefractionIntensity, dst);
            case "emission/brightness": return Scalar(t.Emission?.Brightness, dst);
            case "emission/color0": return Color(t.Emission?.ColorGradient?.Color0, dst);
            case "emission/color1": return Color(t.Emission?.ColorGradient?.Color1, dst);
            case "emission/color2": return Color(t.Emission?.ColorGradient?.Color2, dst);
            case "emission/color3": return Color(t.Emission?.ColorGradient?.Color3, dst);
            case "mach_diamonds/lead_in": return Scalar(t.Emission?.Flow?.MachDiamonds?.LeadIn, dst);
            case "mach_diamonds/lead_out": return Scalar(t.Emission?.Flow?.MachDiamonds?.LeadOut, dst);
            case "mach_diamonds/middle_radius": return Scalar(t.Emission?.Flow?.MachDiamonds?.MiddleRadius, dst);
            case "noise/density_strength": return Scalar(t.Noise?.DensityNoise?.Intensity, dst);
            case "noise/density_size": return Scalar(t.Noise?.DensityNoise?.Size, dst);
            case "noise/radial_strength": return Scalar(t.Noise?.RadialShapeNoise?.Intensity, dst);
            case "noise/radial_barrel_shock": return Scalar(t.Noise?.RadialShapeNoise?.BarrelShockIntensity, dst);
            case "noise/radial_speed": return Scalar(t.Noise?.RadialShapeNoise?.Speed, dst);
            case "noise/radial_size": return Scalar(t.Noise?.RadialShapeNoise?.Size, dst);
            case "noise/shape_strength": return Scalar(t.Noise?.ShapeNoise?.Intensity, dst);
            case "noise/shape_size": return Scalar(t.Noise?.ShapeNoise?.Size, dst);
            case "quality/samples": return Scalar(t.Quality?.SampleCount, dst);
            case "quality/self_shadow_samples": return Scalar(t.Quality?.SelfShadowSampleCount, dst);
            case "quality/vessel_shadows":
                if (t.Quality is null)
                    return false;
                dst[0] = t.Quality.VolumetricVesselShadows ? 1 : 0;
                return true;
            default: return false;
        }
    }

    /// <summary>Applies one validated payload to the template. False ⇒ the field is not bound here.</summary>
    [KsaAnchor("VolumetricExhaustTemplate.{LengthWeights,Absorption,Emission,Noise,Quality} writes: "
            + "DoubleReference.Value / BoolReference.Value (in place) and "
            + "ColorGradient.Color0..3 = new ColorRgbReference(float3) + OnDataLoad(Mod.Empty)",
        SourceFile = "KSA/VolumetricExhaustRenderer.cs:2052-2290 (the in-game editor's write sites)",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "ColorRgbReference.Value has a protected setter, so an in-place colour write silently does "
            + "nothing: colours MUST be construct-new + OnDataLoad, exactly as the editor does. "
            + "DoubleReference/BoolReference expose a plain public Value field with no derived cache, so "
            + "those are written in place. Integer-valued counts are rounded (SPEC documents this).")]
    private static bool TryWrite(VolumetricExhaustTemplate t, FxFieldSpec spec, IReadOnlyList<double> v)
    {
        switch (spec.Key)
        {
            case "core/radius_weight": return Set(t.LengthWeights?.RadiusWeight, v[0]);
            case "core/nozzle_pressure_weight": return Set(t.LengthWeights?.NozzlePressureWeight, v[0]);
            case "core/jet_expansion_weight": return Set(t.LengthWeights?.JetExpansionWeight, v[0]);
            case "core/exit_mach_weight": return Set(t.LengthWeights?.ExitMachNumberWeight, v[0]);
            case "absorption/density": return Set(t.Absorption?.Density, v[0]);
            case "absorption/fake_clean_burn": return Set(t.Absorption?.FakeCleanBurnInAtmosphere, v[0]);
            case "absorption/scattering_brightness": return Set(t.Absorption?.ScatteringBrightness, v[0]);
            case "absorption/phase_eccentricity": return Set(t.Absorption?.ScatteringPhaseEccentricity, v[0]);
            case "absorption/refraction_intensity": return Set(t.Absorption?.RefractionIntensity, v[0]);
            case "emission/brightness": return Set(t.Emission?.Brightness, v[0]);
            case "emission/color0":
                if (t.Emission?.ColorGradient is not { } g0)
                    return false;
                g0.Color0 = NewColor(v);
                return true;
            case "emission/color1":
                if (t.Emission?.ColorGradient is not { } g1)
                    return false;
                g1.Color1 = NewColor(v);
                return true;
            case "emission/color2":
                if (t.Emission?.ColorGradient is not { } g2)
                    return false;
                g2.Color2 = NewColor(v);
                return true;
            case "emission/color3":
                if (t.Emission?.ColorGradient is not { } g3)
                    return false;
                g3.Color3 = NewColor(v);
                return true;
            case "mach_diamonds/lead_in": return Set(t.Emission?.Flow?.MachDiamonds?.LeadIn, v[0]);
            case "mach_diamonds/lead_out": return Set(t.Emission?.Flow?.MachDiamonds?.LeadOut, v[0]);
            case "mach_diamonds/middle_radius": return Set(t.Emission?.Flow?.MachDiamonds?.MiddleRadius, v[0]);
            case "noise/density_strength": return Set(t.Noise?.DensityNoise?.Intensity, v[0]);
            case "noise/density_size": return Set(t.Noise?.DensityNoise?.Size, v[0]);
            case "noise/radial_strength": return Set(t.Noise?.RadialShapeNoise?.Intensity, v[0]);
            case "noise/radial_barrel_shock": return Set(t.Noise?.RadialShapeNoise?.BarrelShockIntensity, v[0]);
            case "noise/radial_speed": return Set(t.Noise?.RadialShapeNoise?.Speed, v[0]);
            case "noise/radial_size": return Set(t.Noise?.RadialShapeNoise?.Size, v[0]);
            case "noise/shape_strength": return Set(t.Noise?.ShapeNoise?.Intensity, v[0]);
            case "noise/shape_size": return Set(t.Noise?.ShapeNoise?.Size, v[0]);
            case "quality/samples": return Set(t.Quality?.SampleCount, Math.Round(v[0]));
            case "quality/self_shadow_samples": return Set(t.Quality?.SelfShadowSampleCount, Math.Round(v[0]));
            case "quality/vessel_shadows":
                if (t.Quality is null)
                    return false;
                t.Quality.VolumetricVesselShadows = v[0] > 0.5;
                return true;
            default: return false;
        }
    }

    /// <summary>
    ///     The propagation pass the game's own editor runs after any template edit: every live
    ///     nozzle instance rebuilds its shader data from the (now edited) template. Skips the
    ///     transient-animation LUT re-bake — gatOS does not expose the transient curves.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.RocketNozzles.ModulesAndAllStates; "
            + "RocketNozzleFxState.VolumetricExhaust; VolumetricExhaustInstance.{OnSettingsChanged,UpdateModifiers}; "
            + "RocketNozzle.RecomputeGasVisibilityDensity(in VolumetricExhaustInstance)",
        SourceFile = "KSA/VolumetricExhaustRenderer.cs:2316-2337 / KSA/VolumetricExhaustInstance.cs:179,231 / "
            + "KSA/RocketNozzle.cs:156",
        Verified = "2026-08-01", GameVersion = "2026.7.10.5056", Risk = ChurnRisk.High,
        Notes = "Mirrors the editor's post-edit loop. UpdateModifiers takes the renderer's own "
            + "pressure/throttle (FxReflect.PlumeModifierArgs, best-effort) — the per-frame AddInstance "
            + "path recomputes them for every drawn nozzle, so this cannot disturb a live plume. Each "
            + "vehicle is isolated: one mid-teardown vessel must not abort the propagation.")]
    private static void Propagate()
    {
        if (Universe.CurrentSystem is not { } system)
            return;

        var (pressure, throttle) = FxReflect.PlumeModifierArgs();
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
                {
                    if (module.FxState.VolumetricExhaust is not { } instance)
                        continue;
                    instance.OnSettingsChanged();
                    instance.UpdateModifiers(pressure, throttle);
                    module.Module.RecomputeGasVisibilityDensity(in instance);
                }
            }
            catch (Exception)
            {
                // A vehicle mid-teardown must not abort the propagation for the rest.
            }
        }
    }

    private static ColorRgbReference NewColor(IReadOnlyList<double> v)
    {
        var color = new ColorRgbReference(new float3((float)v[0], (float)v[1], (float)v[2]));
        color.OnDataLoad(KSA.Mod.Empty);
        return color;
    }

    private static bool Scalar(DoubleReference? reference, double[] dst)
    {
        if (reference is null)
            return false;
        dst[0] = reference.Value;
        return true;
    }

    private static bool Flag(BoolReference? reference, double[] dst)
    {
        if (reference is null)
            return false;
        dst[0] = reference.Value ? 1 : 0;
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

    private static bool Set(BoolReference? reference, double value)
    {
        if (reference is null)
            return false;
        reference.Value = value > 0.5;
        return true;
    }
}
