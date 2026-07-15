using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Staging (KSA_GAME_INTEGRATION_PLAN §5.1 <c>ctl/stage</c>): activates the next stage in the
///     vessel's sequence — the same call the game's stage key triggers
///     (<see cref="SequenceList.ActivateNextSequence"/> + a part-tree refresh). Game-thread only.
/// </summary>
internal static class StagingActuator
{
    [KsaAnchor("vehicle.Parts.SequenceList.ActivateNextSequence(vehicle); Vehicle.UpdateAfterPartTreeModification()",
        SourceFile = "KSA/SequenceList.cs / KSA/Vehicle.cs", Verified = "2026-07-14", GameVersion = "2026.7.5.4892", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the in-game stage key (Vehicle.cs ProcessInput). 4892: the KSA 'Staging' window "
            + "class became ResourceGroups (UI-only, unrelated); ActivateNextSequence keeps its signature, "
            + "now ends in a batched RemoveSpentSequences, and sequences are double-buffered for the UI - "
            + "activation semantics unchanged.")]
    internal static CommandResult Stage(Vehicle vehicle)
    {
        vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
        vehicle.UpdateAfterPartTreeModification();
        return CommandResult.Ok;
    }
}
