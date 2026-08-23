using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     RCS controls (KSA_GAME_INTEGRATION_PLAN §5.1/§5.2): a vessel master toggle that activates
///     every <see cref="ThrusterController"/>, and a per-controller toggle. Game-thread only.
/// </summary>
internal static class RcsActuator
{
    [KsaAnchor("ThrusterController.SetIsActive(Vehicle, bool) over all controllers",
        SourceFile = "KSA/ThrusterController.cs", Verified = "2026-08-23",
        GameVersion = "2026.8.22.5348", Risk = ChurnRisk.Medium,
        Notes = "5348: SetIsActive is unchanged, but the ThrusterController constructor dropped its "
            + "part.ActivateInStage(null) broadcast, so RCS no longer self-activates at part "
            + "construction. IsActive still defaults true, so the observable rcs state is unchanged.")]
    internal static CommandResult SetMaster(Vehicle vehicle, bool on)
    {
        foreach (var thruster in vehicle.Parts.Modules.Get<ThrusterController>())
            thruster.SetIsActive(vehicle, on);
        return CommandResult.Ok;
    }

    [KsaAnchor("ThrusterController.SetIsActive(Vehicle, bool)", SourceFile = "KSA/ThrusterController.cs",
        Verified = "2026-08-23", GameVersion = "2026.8.22.5348", Risk = ChurnRisk.Medium,
        Notes = "5348: SetIsActive is unchanged, but the ThrusterController constructor dropped its "
            + "part.ActivateInStage(null) broadcast (RCS no longer self-activates at construction) and "
            + "ctl/stage no longer activates thrusters either, since ThrusterController is IActivate "
            + "but not ISequenced. IsActive still defaults true.")]
    internal static CommandResult SetActive(Vehicle vehicle, int ordinal, bool on)
    {
        var thrusters = vehicle.Parts.Modules.Get<ThrusterController>();
        if (ordinal < 0 || ordinal >= thrusters.Length)
            return new CommandResult(CommandOutcome.NotFound, $"rcs {ordinal} does not exist");
        thrusters[ordinal].SetIsActive(vehicle, on);
        return CommandResult.Ok;
    }
}
