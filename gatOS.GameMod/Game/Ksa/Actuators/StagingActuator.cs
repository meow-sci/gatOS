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
        SourceFile = "KSA/SequenceList.cs / KSA/Vehicle.cs", Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the in-game stage key (Vehicle.cs ProcessInput). 4892: the KSA 'Staging' window "
            + "class became ResourceGroups (UI-only, unrelated); ActivateNextSequence keeps its signature, "
            + "now ends in a batched RemoveSpentSequences, and sequences are double-buffered for the UI - "
            + "activation semantics unchanged. 5348 (rev 5329): ActivateNextSequence keeps its "
            + "signature but its body now calls Part.ActivateSubtreeInStage(vehicle, sequence.Number) "
            + "instead of Part.ActivateInStage(vehicle) — it walks GetSubtreeSequencedModules() and "
            + "activates only ISequenced modules (exactly EngineController and Decoupler) whose own "
            + "Sequence matches. ThrusterController is IActivate but NOT ISequenced, so ctl/stage no "
            + "longer flips rcs/<n>/active as a side effect; engines/decouplers on SUB-parts are now "
            + "staged where they used to be skipped; and a part holding modules in two different "
            + "sequences needs two presses."
            + "5402: ActivateNextSequence (:161) and Part.ActivateSubtreeInStage are byte-identical — the 624-line SequenceList diff is the staging-window UI rework (SequenceAction; Part.CountEnabledSubtreeSequencedModules removed, never used here). NEW ISequenced+IActivate modules now fire from a stage: ParachuteDeploy (SetIsActive → IActivateInputData{ActivateOp.Parachute} → ArmAll(): Stowed→Armed, bay doors open) and ParachuteCut (ActivateOp.ParachuteCut → CutAll()), both applied at InputEvents.ApplyInputEvents after the solver join — so ctl/stage now also arms/cuts parachutes exactly like the stage key, deferred through the input buffer (no new Frame-lane race).")]
    internal static CommandResult Stage(Vehicle vehicle)
    {
        vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
        vehicle.UpdateAfterPartTreeModification();
        return CommandResult.Ok;
    }
}
