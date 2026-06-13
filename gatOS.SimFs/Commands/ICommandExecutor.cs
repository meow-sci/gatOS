namespace gatOS.SimFs.Commands;

/// <summary>
///     The game-thread seam that turns a <see cref="SimCommand"/> into a KSA mutation
///     (KSA_GAME_INTEGRATION_PLAN §3.1/§3.2). Implemented only by the game-coupled integration
///     layer (<c>gatOS.GameMod/Game/Ksa/KsaCatalog</c>) — the single place KSA types appear.
///     <see cref="Execute"/> always runs on the game thread (or solver phase), so it may touch
///     game state directly; it must never throw (faults are returned as
///     <see cref="CommandOutcome.Fault"/>).
/// </summary>
public interface ICommandExecutor
{
    /// <summary>Executes one command on the game thread and reports the outcome.</summary>
    CommandResult Execute(SimCommand command);
}
