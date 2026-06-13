using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Decoupler fire (KSA_GAME_INTEGRATION_PLAN §5.2 <c>decouplers/&lt;n&gt;/fire</c>): a one-shot
///     <see cref="Decoupler.SetIsActive"/>. Firing is irreversible — KSA silently ignores a re-fire,
///     so an already-fired decoupler returns EBUSY rather than a misleading success. Game-thread only.
/// </summary>
internal static class DecouplerActuator
{
    [KsaAnchor("Decoupler.IsActive / SetIsActive(Vehicle, true)", SourceFile = "KSA/Decoupler.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium, Notes = "Re-fire is rejected by KSA → EBUSY.")]
    internal static CommandResult Fire(Vehicle vehicle, int ordinal)
    {
        var decouplers = vehicle.Parts.Modules.Get<Decoupler>();
        if (ordinal < 0 || ordinal >= decouplers.Length)
            return new CommandResult(CommandOutcome.NotFound, $"decoupler {ordinal} does not exist");
        var decoupler = decouplers[ordinal];
        if (decoupler.IsActive)
            return new CommandResult(CommandOutcome.Busy, $"decoupler {ordinal} already fired");
        decoupler.SetIsActive(vehicle, true);
        return CommandResult.Ok;
    }
}
