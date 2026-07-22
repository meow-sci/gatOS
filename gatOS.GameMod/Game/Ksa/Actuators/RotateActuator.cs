using System.Reflection;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Manual RCS rotation (<c>ctl/rotate</c>) — the file twin of the player's rotation keys and
///     the symmetric sibling of <see cref="TranslateActuator"/>. Writes the rotation bits of the
///     private <c>Vehicle._manualControlInputs.ThrusterCommandFlags</c> (the same struct throttle
///     and translate write) by reflection. KSA's flight computer consumes those flags every solver
///     step: in the default <c>ManualThrustMode.Direct</c> they pass straight to
///     <c>SelectJetsToFire</c>, firing every thruster whose <c>ControlMap</c> matches at full
///     thrust — bang-bang, like a held key — and the TVC path decodes the same bits into gimbal
///     torque. <b>Unlike translate, an active auto-attitude hold strips the rotation bits</b>
///     (<c>WithNoRotation()</c> in <c>FlightComputer.ComputeRcsControl</c>), so this control has
///     full authority only with <c>ctl/attitude_mode = manual</c> — exactly like the player's
///     rotation keys. The command <b>latches</b> until overwritten — write <c>0 0 0</c> to stop.
///     This actuator preserves the translation bits it finds, so rotate and translate compose.
///     Game-thread only.
/// </summary>
internal static class RotateActuator
{
    private static readonly FieldInfo? ManualInputsField =
        typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

    private const ThrusterMapFlags AllRotation =
        ThrusterMapFlags.RollRight | ThrusterMapFlags.RollLeft
        | ThrusterMapFlags.PitchUp | ThrusterMapFlags.PitchDown
        | ThrusterMapFlags.YawRight | ThrusterMapFlags.YawLeft;

    [KsaAnchor("Vehicle._manualControlInputs.ThrusterCommandFlags (private; set by reflection); ThrusterMapFlags",
        SourceFile = "KSA/Vehicle.cs / KSA/ThrusterMapFlags.cs / KSA/FlightComputer.cs", Verified = "2026-07-22",
        GameVersion = "2026.7.6.4939", Risk = ChurnRisk.High,
        Notes = "Same struct ThrottleActuator/TranslateActuator write. FlightComputer.ComputeRcsControl "
            + "consumes the flags each solver step (Direct mode → SelectJetsToFire; ComputeTvcControl "
            + "decodes the same bits for gimbals). Auto attitude strips ROTATION bits (WithNoRotation), "
            + "so full authority needs attitude_mode=manual — the inverse of translate's compose note. "
            + "Sign→flag mapping is KSA's own torque-command convention (FlightComputer.ComputeTvcControl "
            + ":559-585): +X=RollRight, +Y=PitchUp, +Z=YawRight on the X-nose/Y-right/Z-down body frame.")]
    internal static CommandResult SetRotation(Vehicle vehicle, IReadOnlyList<double> axes)
    {
        // Re-validate through the shared game-free rule: the HTTP/MQTT command paths reach here
        // without the 9p control-file parse, so arity/finiteness are not pre-checked on that route.
        if (RotateRules.Validate(axes) is { } error)
            return new CommandResult(CommandOutcome.Invalid, error);
        if (ManualInputsField is null)
            return new CommandResult(CommandOutcome.Unsupported, "manual control inputs field not found in this build");

        // _manualControlInputs is a struct: box it, mutate the field, write the box back.
        var inputs = ManualInputsField.GetValue(vehicle);
        if (inputs is null)
            return new CommandResult(CommandOutcome.Fault, "manual control inputs unavailable");

        var flagsField = inputs.GetType().GetField("ThrusterCommandFlags",
            BindingFlags.Instance | BindingFlags.Public);
        if (flagsField is null)
            return new CommandResult(CommandOutcome.Unsupported, "ThrusterCommandFlags field not found in this build");

        // Replace only the rotation bits; any manual translation bits stay untouched.
        var current = (ThrusterMapFlags)flagsField.GetValue(inputs)!;
        flagsField.SetValue(inputs, (current & ~AllRotation) | Encode(axes));
        ManualInputsField.SetValue(vehicle, inputs);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Body-axis signs → flags, per KSA's own torque-command decode
    ///     (<c>FlightComputer.ComputeTvcControl</c>): +X = <c>RollRight</c> (about the nose axis),
    ///     +Y = <c>PitchUp</c> (about the right axis), +Z = <c>YawRight</c> (about the down axis).
    /// </summary>
    private static ThrusterMapFlags Encode(IReadOnlyList<double> axes)
    {
        var flags = ThrusterMapFlags.None;
        flags |= axes[0] > 0 ? ThrusterMapFlags.RollRight
            : axes[0] < 0 ? ThrusterMapFlags.RollLeft : ThrusterMapFlags.None;
        flags |= axes[1] > 0 ? ThrusterMapFlags.PitchUp
            : axes[1] < 0 ? ThrusterMapFlags.PitchDown : ThrusterMapFlags.None;
        flags |= axes[2] > 0 ? ThrusterMapFlags.YawRight
            : axes[2] < 0 ? ThrusterMapFlags.YawLeft : ThrusterMapFlags.None;
        return flags;
    }

    /// <summary>Read-back for the sampler: decode the live flags to body-axis signs (−1/0/+1).</summary>
    [KsaAnchor("Vehicle.GetThrusterFlags()", SourceFile = "KSA/Vehicle.cs", Verified = "2026-07-22",
        GameVersion = "2026.7.6.4939", Risk = ChurnRisk.Medium,
        Notes = "Public reader of _manualControlInputs.ThrusterCommandFlags.")]
    internal static (double X, double Y, double Z) Read(Vehicle vehicle)
    {
        var flags = vehicle.GetThrusterFlags();
        return (Axis(flags, ThrusterMapFlags.RollRight, ThrusterMapFlags.RollLeft),
            Axis(flags, ThrusterMapFlags.PitchUp, ThrusterMapFlags.PitchDown),
            Axis(flags, ThrusterMapFlags.YawRight, ThrusterMapFlags.YawLeft));
    }

    private static double Axis(ThrusterMapFlags flags, ThrusterMapFlags positive, ThrusterMapFlags negative)
        => (flags & positive) != 0 ? 1 : (flags & negative) != 0 ? -1 : 0;
}
