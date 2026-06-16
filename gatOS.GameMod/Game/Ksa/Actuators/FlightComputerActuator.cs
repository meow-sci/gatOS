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

    [KsaAnchor("FlightComputer.{CustomAttitudeTarget,AttitudeFrame}; VehicleReferenceFrameEx.{GetEclBody2Cci,QuaternionToEulerAngles}",
        SourceFile = "KSA/FlightComputer.cs", Verified = "2026-06-15", Risk = ChurnRisk.Medium,
        Notes = "Custom track recomputes Target2Cci from CustomAttitudeTarget (euler) every solver step "
            + "(UpdateAttitudeTarget), so a directly-set Target2Cci is discarded — we must set the euler "
            + "form in an AttitudeFrame instead. EclBody is inertial, so its frame2Cci needs only the "
            + "parent's Cce2Cci (no FlightComputerNavigation).")]
    internal static CommandResult SetAttitudeTarget(Vehicle vehicle, IReadOnlyList<double> quat)
    {
        if (quat.Count != 4)
            return new CommandResult(CommandOutcome.Invalid, "attitude_target expects 'x y z w'");
        var fc = vehicle.FlightComputer;
        var target2Cci = new doubleQuat(quat[0], quat[1], quat[2], quat[3]);

        // KSA's Custom track recomputes Target2Cci as Concatenate(EulerAnglesToQuaternion(CustomAttitudeTarget),
        // frame2Cci) every solver step, so we express the desired Body→CCI rotation as euler angles in the
        // (inertial) EclBody frame. The round-trip is exact: with euler = QuaternionToEulerAngles(target2Cci ∘
        // frame2Cci⁻¹), KSA rebuilds Concatenate(target2Cci ∘ frame2Cci⁻¹, frame2Cci) == target2Cci.
        const VehicleReferenceFrame frame = VehicleReferenceFrame.EclBody;
        var frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(vehicle.Orbit.Parent.GetCce2Cci());
        var frame2Desired = doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse());

        fc.AttitudeMode = FlightComputerAttitudeMode.Auto;
        fc.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
        fc.AttitudeFrame = frame;
        fc.CustomAttitudeTarget = frame.QuaternionToEulerAngles(frame2Desired);
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
