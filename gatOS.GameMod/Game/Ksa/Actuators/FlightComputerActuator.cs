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
        SourceFile = "KSA/FlightComputer.cs", Verified = "2026-07-22", GameVersion = "2026.7.8.4980",
        Risk = ChurnRisk.Medium,
        Notes = "'manual' → AttitudeMode.Manual; any other token is an auto track-target. 4980 (rev "
            + "4946/4949): new FlightComputer.RCSMode (Enabled/Disabled, toggled in-game with R) gates the "
            + "RCS torque-authority scan in UpdateActiveControlSystems — with it Disabled, an auto hold on "
            + "an RCS-only vessel silently does not actuate (only gimballed TVC survives, and only while "
            + "burning). gatOS neither reads nor sets it; pre-existing silent-ignore caveat extended.")]
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

    /// <summary>
    ///     The flight computer's RCS master switch (<c>ctl/rcs_mode</c>) — the file twin of the
    ///     in-game <b>R</b> keybind. Solver-phase like every other FC setpoint, because
    ///     <c>FlightComputer.CopyFrom</c> snapshots and restores it.
    /// </summary>
    [KsaAnchor("FlightComputer.RCSMode (FlightComputerRCSMode)", SourceFile = "KSA/FlightComputer.cs:41,471,884",
        Verified = "2026-08-05", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "5168 (rev 5143) turned this into a HARD master cut-off: ComputeRcsControl now zeroes "
            + "inputs.ThrusterCommandFlags outright when RCSMode != Enabled (FlightComputer.cs:471), and "
            + "UpdateRcsParams zeroes the thruster authority (:884). Before 5168 only auto attitude holds "
            + "were gated and manual translate/rotate fired regardless — gatOS's scope/ docs said exactly "
            + "that, and 5143 falsified it. Exposed as a read + control so a flight program can see and "
            + "clear the condition instead of watching ctl/translate silently do nothing.")]
    internal static CommandResult SetRcsMode(Vehicle vehicle, string token)
    {
        if (!Enum.TryParse<FlightComputerRCSMode>(token, ignoreCase: true, out var mode))
            return new CommandResult(CommandOutcome.Invalid, $"unknown rcs mode '{token}'");
        vehicle.FlightComputer.RCSMode = mode;
        return CommandResult.Ok;
    }

    /// <summary>Read-back for the sampler: the live RCS master mode name.</summary>
    [KsaAnchor("FlightComputer.RCSMode (read)", SourceFile = "KSA/FlightComputer.cs:41",
        Verified = "2026-08-05", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium)]
    internal static string ReadRcsMode(Vehicle vehicle) => vehicle.FlightComputer.RCSMode.ToString();

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
        SourceFile = "KSA/FlightComputer.cs", Verified = "2026-07-22", GameVersion = "2026.7.8.4980",
        Risk = ChurnRisk.Medium,
        Notes = "Custom track recomputes Target2Cci from CustomAttitudeTarget (euler) every solver step "
            + "(UpdateAttitudeTarget), so a directly-set Target2Cci is discarded — we must set the euler "
            + "form in an AttitudeFrame instead. EclBody is inertial, so its frame2Cci needs only the "
            + "parent's Cce2Cci (no FlightComputerNavigation). 4980 (rev 4978): FlightComputer.RollMode "
            + "default flipped Up → Decoupled (\"ANY\"), so a fresh FC no longer actuates roll — the +X "
            + "pointing converges but the quaternion's roll component is held only if the player (or a "
            + "loaded save) sets RollMode; gatOS does not set it.")]
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
