using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Decoupler fire (KSA_GAME_INTEGRATION_PLAN §5.2 <c>decouplers/&lt;n&gt;/fire</c>): a one-shot
///     <see cref="Decoupler.SetIsActive"/>. Firing is irreversible — KSA silently ignores a re-fire,
///     so an already-fired decoupler returns EBUSY rather than a misleading success. A
///     player-<i>disabled</i> decoupler is likewise ignored by KSA and returns EOPNOTSUPP.
///     Game-thread only.
/// </summary>
internal static class DecouplerActuator
{
    [KsaAnchor("Decoupler.IsActive / IsEnabled / SetIsActive(Vehicle, true)",
        SourceFile = "KSA/Decoupler.cs:71,73,149", Verified = "2026-08-23",
        GameVersion = "2026.8.22.5348", Risk = ChurnRisk.Medium,
        Notes = "Re-fire is rejected by KSA → EBUSY. 5168 (rev 5132): SetIsActive gained an IsEnabled "
            + "precondition (players may disable a part's decoupler module, e.g. turning an adapter into "
            + "a static fairing). Without the guard below the call is a silent no-op that gatOS would "
            + "still report as success, so a disabled decoupler is rejected up front with EOPNOTSUPP. "
            + "5348: Decoupler became a multi-instance component module — PartTemplate.Decoupler was "
            + "deleted and instances are built from template.Components — so a single part could now "
            + "host more than one. Stock content still has exactly one per part, so decouplers/<n> "
            + "ordinals are stable today. IsActive/IsEnabled/SetIsActive are otherwise unchanged; "
            + "the cited line numbers moved.")]
    internal static CommandResult Fire(Vehicle vehicle, int ordinal)
    {
        var decouplers = vehicle.Parts.Modules.Get<Decoupler>();
        if (ordinal < 0 || ordinal >= decouplers.Length)
            return new CommandResult(CommandOutcome.NotFound, $"decoupler {ordinal} does not exist");
        var decoupler = decouplers[ordinal];
        if (decoupler.IsActive)
            return new CommandResult(CommandOutcome.Busy, $"decoupler {ordinal} already fired");
        if (!decoupler.IsEnabled)
            return new CommandResult(CommandOutcome.Unsupported,
                $"decoupler {ordinal} is disabled on its part and cannot fire");
        decoupler.SetIsActive(vehicle, true);
        return CommandResult.Ok;
    }
}
