using System.Reflection;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Manual RCS translation (<c>ctl/translate</c>) — the file twin of the player's translation
///     keys. Writes the translate bits of the private
///     <c>Vehicle._manualControlInputs.ThrusterCommandFlags</c> (the same struct
///     <see cref="ThrottleActuator"/> writes <c>EngineThrottle</c> on) by reflection. KSA's flight
///     computer consumes those flags every solver step: in the default <c>ManualThrustMode.Direct</c>
///     they pass straight to <c>SelectJetsToFire</c>, firing every thruster whose <c>ControlMap</c>
///     matches at full thrust — bang-bang, like a held key. An active auto-attitude hold strips only
///     the <b>rotation</b> bits, so translation composes with flight-computer tracking; this actuator
///     likewise preserves the rotation bits it finds. The command <b>latches</b> until overwritten —
///     write <c>0 0 0</c> to stop. Game-thread only.
/// </summary>
internal static class TranslateActuator
{
    private static readonly FieldInfo? ManualInputsField =
        typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

    private const ThrusterMapFlags AllTranslation =
        ThrusterMapFlags.TranslateForward | ThrusterMapFlags.TranslateBackward
        | ThrusterMapFlags.TranslateRight | ThrusterMapFlags.TranslateLeft
        | ThrusterMapFlags.TranslateDown | ThrusterMapFlags.TranslateUp;

    [KsaAnchor("Vehicle._manualControlInputs.ThrusterCommandFlags (private; set by reflection); ThrusterMapFlags",
        SourceFile = "KSA/Vehicle.cs / KSA/ThrusterMapFlags.cs / KSA/FlightComputer.cs", Verified = "2026-07-04",
        GameVersion = "2026.7.3.4826", Risk = ChurnRisk.High,
        Notes = "Same struct ThrottleActuator writes. FlightComputer.ComputeRcsControl consumes the flags "
            + "each solver step (Direct mode → SelectJetsToFire; Auto attitude strips only ROTATION bits, "
            + "so translation composes with tracking). Sign→flag mapping verified against the "
            + "KittenBackPackSubPart nozzle geometry (exhaust opposes thrust): +X=Forward(nose), "
            + "+Y=Right, +Z=Down — KSA's body frame is X-nose/Y-right/Z-down.")]
    internal static CommandResult SetTranslation(Vehicle vehicle, IReadOnlyList<double> axes)
    {
        // Re-validate through the shared game-free rule: the HTTP/MQTT command paths reach here
        // without the 9p control-file parse, so arity/finiteness are not pre-checked on that route.
        if (TranslateRules.Validate(axes) is { } error)
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

        // Replace only the translation bits; any manual rotation bits stay untouched.
        var current = (ThrusterMapFlags)flagsField.GetValue(inputs)!;
        flagsField.SetValue(inputs, (current & ~AllTranslation) | Encode(axes));
        ManualInputsField.SetValue(vehicle, inputs);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Body-axis signs → flags. The mapping follows the RCS part nozzle geometry (exhaust points
    ///     opposite the thrust): +X = <c>TranslateForward</c> (along the nose), +Y = <c>Right</c>,
    ///     +Z = <c>Down</c>.
    /// </summary>
    private static ThrusterMapFlags Encode(IReadOnlyList<double> axes)
    {
        var flags = ThrusterMapFlags.None;
        flags |= axes[0] > 0 ? ThrusterMapFlags.TranslateForward
            : axes[0] < 0 ? ThrusterMapFlags.TranslateBackward : ThrusterMapFlags.None;
        flags |= axes[1] > 0 ? ThrusterMapFlags.TranslateRight
            : axes[1] < 0 ? ThrusterMapFlags.TranslateLeft : ThrusterMapFlags.None;
        flags |= axes[2] > 0 ? ThrusterMapFlags.TranslateDown
            : axes[2] < 0 ? ThrusterMapFlags.TranslateUp : ThrusterMapFlags.None;
        return flags;
    }

    /// <summary>Read-back for the sampler: decode the live flags to body-axis signs (−1/0/+1).</summary>
    [KsaAnchor("Vehicle.GetThrusterFlags()", SourceFile = "KSA/Vehicle.cs", Verified = "2026-07-04",
        GameVersion = "2026.7.3.4826", Risk = ChurnRisk.Medium,
        Notes = "Public reader of _manualControlInputs.ThrusterCommandFlags.")]
    internal static (double X, double Y, double Z) Read(Vehicle vehicle)
    {
        var flags = vehicle.GetThrusterFlags();
        return (Axis(flags, ThrusterMapFlags.TranslateForward, ThrusterMapFlags.TranslateBackward),
            Axis(flags, ThrusterMapFlags.TranslateRight, ThrusterMapFlags.TranslateLeft),
            Axis(flags, ThrusterMapFlags.TranslateDown, ThrusterMapFlags.TranslateUp));
    }

    private static double Axis(ThrusterMapFlags flags, ThrusterMapFlags positive, ThrusterMapFlags negative)
        => (flags & positive) != 0 ? 1 : (flags & negative) != 0 ? -1 : 0;
}
