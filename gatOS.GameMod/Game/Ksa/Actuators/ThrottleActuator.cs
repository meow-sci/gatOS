using System.Reflection;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Manual throttle (KSA_GAME_INTEGRATION_PLAN §5.1 <c>ctl/throttle</c>). KSA has a public
///     <c>GetManualThrottle()</c> reader but no public setter — the value lives in the private
///     <c>Vehicle._manualControlInputs.EngineThrottle</c> field (the same one
///     <see cref="Vehicle.SetEnum"/> toggles <c>EngineOn</c> on). We set it by reflection, confined
///     to this actuator and annotated High-churn; a missing field degrades the accessor (EOPNOTSUPP)
///     rather than crashing. Game-thread only.
/// </summary>
internal static class ThrottleActuator
{
    private static readonly FieldInfo? ManualInputsField =
        typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

    [KsaAnchor("Vehicle._manualControlInputs.EngineThrottle (private; set by reflection)",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
        Notes = "No public throttle setter; GetManualThrottle() reads this same field.")]
    internal static CommandResult Set(Vehicle vehicle, double fraction)
    {
        if (ManualInputsField is null)
            return new CommandResult(CommandOutcome.Unsupported, "manual throttle field not found in this build");

        // _manualControlInputs is a struct: box it, mutate the field, write the box back.
        var inputs = ManualInputsField.GetValue(vehicle);
        if (inputs is null)
            return new CommandResult(CommandOutcome.Fault, "manual control inputs unavailable");

        var throttleField = inputs.GetType().GetField("EngineThrottle",
            BindingFlags.Instance | BindingFlags.Public);
        if (throttleField is null)
            return new CommandResult(CommandOutcome.Unsupported, "EngineThrottle field not found in this build");

        throttleField.SetValue(inputs, (float)Math.Clamp(fraction, 0, 1));
        ManualInputsField.SetValue(vehicle, inputs);
        return CommandResult.Ok;
    }
}
