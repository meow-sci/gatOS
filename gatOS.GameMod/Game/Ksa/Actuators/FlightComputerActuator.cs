using Brutal.Numerics;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Flight-computer setpoints (KSA_GAME_INTEGRATION_PLAN §5.1): attitude mode/frame, a custom
///     attitude quaternion, and an impulsive burn target. These are <i>onboard</i> setpoints the
///     sim integrates itself, so they behave correctly at any time-warp (the guest is mission
///     control, the autopilot flies it). Game-thread only.
/// </summary>
internal static class FlightComputerActuator
{
    [KsaAnchor("FlightComputer.{AttitudeMode,AttitudeTrackTarget}; FlightComputerAttitudeMode/...TrackTarget",
        SourceFile = "KSA/FlightComputer.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "'manual' → AttitudeMode.Manual; any other token is an auto track-target.")]
    internal static CommandResult SetAttitudeMode(Vehicle vehicle, string token)
    {
        var fc = vehicle.FlightComputer;
        if (string.Equals(token, "manual", StringComparison.OrdinalIgnoreCase))
        {
            fc.AttitudeMode = FlightComputerAttitudeMode.Manual;
            return CommandResult.Ok;
        }

        if (!Enum.TryParse<FlightComputerAttitudeTrackTarget>(token, ignoreCase: true, out var target))
            return new CommandResult(CommandOutcome.Invalid, $"unknown attitude mode '{token}'");
        fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
        fc.AttitudeTrackTarget = target;
        return CommandResult.Ok;
    }

    [KsaAnchor("FlightComputer.AttitudeFrame (VehicleReferenceFrame)", SourceFile = "KSA/FlightComputer.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium)]
    internal static CommandResult SetAttitudeFrame(Vehicle vehicle, string token)
    {
        if (!Enum.TryParse<VehicleReferenceFrame>(token, ignoreCase: true, out var frame))
            return new CommandResult(CommandOutcome.Invalid, $"unknown attitude frame '{token}'");
        vehicle.FlightComputer.AttitudeFrame = frame;
        return CommandResult.Ok;
    }

    [KsaAnchor("FlightComputer.AttitudeTarget = new AttitudeTarget{Target2Cci,RatesCci}; AttitudeTrackTarget.Custom",
        SourceFile = "KSA/AttitudeTarget.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Drives a custom CCI-frame quaternion in auto mode.")]
    internal static CommandResult SetAttitudeTarget(Vehicle vehicle, IReadOnlyList<double> quat)
    {
        if (quat.Count != 4)
            return new CommandResult(CommandOutcome.Invalid, "attitude_target expects 'x y z w'");
        var fc = vehicle.FlightComputer;
        fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
        fc.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        fc.AttitudeTarget = new AttitudeTarget
        {
            Target2Cci = new doubleQuat(quat[0], quat[1], quat[2], quat[3]),
            RatesCci = double3.Zero,
        };
        return CommandResult.Ok;
    }

    [KsaAnchor("FlightComputer.Burn = new BurnTarget{ImpulsiveInstant,DeltaVTargetCci}",
        SourceFile = "KSA/BurnTarget.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "ut + Δv (CCI); the autopilot executes it.")]
    internal static CommandResult SetBurn(Vehicle vehicle, IReadOnlyList<double> burn)
    {
        if (burn.Count != 4)
            return new CommandResult(CommandOutcome.Invalid, "burn expects 'ut dvx dvy dvz'");
        vehicle.FlightComputer.Burn = new BurnTarget
        {
            ImpulsiveInstant = new SimTime(burn[0]),
            DeltaVTargetCci = new float3((float)burn[1], (float)burn[2], (float)burn[3]),
        };
        return CommandResult.Ok;
    }
}
