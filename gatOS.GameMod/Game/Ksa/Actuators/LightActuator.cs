using System.Reflection;
using System.Runtime.CompilerServices;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Light controls (KSA_GAME_INTEGRATION_PLAN §5.1/§5.2). The vessel master toggle mirrors
///     <c>Vehicle.ToggleLights()</c> as a set (the proven unscience <c>zippo</c>/<c>red-alert</c>
///     path); per-light on/off drives the part's <c>PowerConsumer.LightIsActive</c>; per-light
///     brightness/colour/spread mutate the light's <see cref="LightModule.TemplateData"/>. Because
///     that template is <b>shared</b> across all lights spawned from the same part template, a
///     per-instance write first clones the template (tracked in a <see cref="ConditionalWeakTable{TKey,TValue}"/>)
///     so one light's change doesn't recolour (or re-spread) every identical light — the red-alert
///     pattern. Game-thread only; may throw — <c>KsaCatalog</c> wraps every call.
/// </summary>
internal static class LightActuator
{
    // KSA's render-side clamp on the spotlight outer-cone half-angle (Light.CreateSpotLight): the
    // valid radian band the template value must land in (~0..89.94 deg). MAX is just under π/2.
    private const double MinOuterAngleRad = 1E-05;
    private const double MaxOuterAngleRad = 1.5697963;

    // Lights whose Template has been unshared (cloned) for per-instance writes.
    private static readonly ConditionalWeakTable<LightModule, object> Unshared = new();
    private static readonly object Marker = new();

    [KsaAnchor("Vehicle.LightsOn; PowerConsumer.LightSwitch/.LightIsActive", SourceFile = "KSA/Vehicle.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Replicates ToggleLights() as a set. PowerConsumer.UpdateModules reads LightIsActive "
                + "into state.Active each tick when LightSwitch is set.")]
    internal static CommandResult SetMaster(Vehicle vehicle, bool on)
    {
        vehicle.LightsOn = on;
        foreach (var consumer in vehicle.Parts.Modules.Get<PowerConsumer>())
            if (consumer.LightSwitch)
                consumer.LightIsActive = on;
        return CommandResult.Ok;
    }

    [KsaAnchor("LightModule.Parent.FullPart.LightSwitch.LightIsActive", SourceFile = "KSA/LightModule.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium, Notes = "Per-light on/off via the part's power switch.")]
    internal static CommandResult SetOn(Vehicle vehicle, int ordinal, bool on)
    {
        if (GetLight(vehicle, ordinal) is not { } light)
            return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
        if (light.Parent.FullPart.LightSwitch is not { } powerSwitch)
            return new CommandResult(CommandOutcome.Unsupported, $"light {ordinal} has no power switch");
        powerSwitch.LightIsActive = on;
        return CommandResult.Ok;
    }

    [KsaAnchor("LightModule.Template.Intensity.Value (per-instance, after template clone)",
        SourceFile = "KSA/LightModule.cs / KSA/FloatReference.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
        Notes = "Template is shared; EnsureUnshared clones it (red-alert ConditionalWeakTable pattern) first.")]
    internal static CommandResult SetBrightness(Vehicle vehicle, int ordinal, double value)
    {
        if (GetLight(vehicle, ordinal) is not { } light)
            return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
        EnsureUnshared(light);
        light.Template.Intensity.Value = (float)Math.Max(0, value);
        return CommandResult.Ok;
    }

    [KsaAnchor("LightModule.Template.ColorRgb.{R,G,B} + OnDataLoad (per-instance, after template clone)",
        SourceFile = "KSA/LightModule.cs / KSA/ColorRgbReference.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
        Notes = "OnDataLoad recomputes the rendered Value from R/G/B (zippo pattern).")]
    internal static CommandResult SetColor(Vehicle vehicle, int ordinal, IReadOnlyList<double> rgb)
    {
        if (rgb.Count != 3)
            return new CommandResult(CommandOutcome.Invalid, "color expects 'r g b'");
        if (GetLight(vehicle, ordinal) is not { } light)
            return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
        EnsureUnshared(light);
        var color = light.Template.ColorRgb;
        color.R = (float)Math.Clamp(rgb[0], 0, 1);
        color.G = (float)Math.Clamp(rgb[1], 0, 1);
        color.B = (float)Math.Clamp(rgb[2], 0, 1);
        color.OnDataLoad(null!); // recompute the rendered Value from R/G/B (mod arg is unused)
        return CommandResult.Ok;
    }

    [KsaAnchor("LightModule.Template.OuterAngle.Value (per-instance, after template clone)",
        SourceFile = "KSA/LightModule.cs / KSA/FloatReference.cs", Verified = "2026-06-21", Risk = ChurnRisk.High,
        Notes = "Spotlight outer-cone half-angle. Argument is degrees; stored as radians, clamped to KSA's "
                + "render range [1E-05, 1.5697963] rad (~0..89.94 deg, see Light.CreateSpotLight). Template is "
                + "shared; EnsureUnshared clones OuterAngle (red-alert pattern) so one light's spread doesn't "
                + "widen every identical light.")]
    internal static CommandResult SetSpread(Vehicle vehicle, int ordinal, double degrees)
    {
        if (GetLight(vehicle, ordinal) is not { } light)
            return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
        EnsureUnshared(light);
        var radians = Math.Clamp(degrees * (Math.PI / 180.0), MinOuterAngleRad, MaxOuterAngleRad);
        light.Template.OuterAngle.Value = (float)radians;
        return CommandResult.Ok;
    }

    private static LightModule? GetLight(Vehicle vehicle, int ordinal)
    {
        var lights = vehicle.Parts.Modules.Get<LightModule>();
        return ordinal >= 0 && ordinal < lights.Length ? lights[ordinal] : null;
    }

    private static void EnsureUnshared(LightModule light)
    {
        if (Unshared.TryGetValue(light, out _))
            return;
        var clone = ShallowClone(light.Template);
        clone.Intensity = ShallowClone(light.Template.Intensity);
        clone.ColorRgb = ShallowClone(light.Template.ColorRgb);
        clone.OuterAngle = ShallowClone(light.Template.OuterAngle);
        light.Template = clone;
        Unshared.Add(light, Marker);
    }

    /// <summary>Field-by-field clone across the whole type hierarchy (ctor-agnostic — handles internal refs).</summary>
    private static T ShallowClone<T>(T source) where T : class
    {
        var type = source.GetType();
        var clone = (T)RuntimeHelpers.GetUninitializedObject(type);
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            foreach (var field in current.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                field.SetValue(clone, field.GetValue(source));
        return clone;
    }
}
