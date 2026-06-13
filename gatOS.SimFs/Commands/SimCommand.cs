namespace gatOS.SimFs.Commands;

/// <summary>
///     When a command must be executed on the game thread (KSA_GAME_INTEGRATION_PLAN §3.1).
///     <see cref="Frame"/> commands drain in the per-frame game-thread hook (the vast majority);
///     <see cref="Solver"/> commands drain in a Harmony prefix on the vehicle-solver phase so the
///     mutation is visible to the physics solvers within the same sim step (refills, robotics).
/// </summary>
public enum CommandPhase
{
    /// <summary>Drained on the per-frame game-thread hook (before UI). The default.</summary>
    Frame,

    /// <summary>Drained inside the vehicle-solver phase (solver-visible mutations).</summary>
    Solver,
}

/// <summary>
///     An immutable, transport-agnostic request to mutate KSA game state
///     (KSA_GAME_INTEGRATION_PLAN Part 5). Built on a transport thread (9p control file, later
///     HTTP/serial), drained and executed on the game thread by an <see cref="ICommandExecutor"/>.
///     This type is game-free: it names its target by string + ordinal so the executor — the only
///     KSA-aware code — resolves it against live game state.
/// </summary>
/// <param name="VesselId">The target vehicle id (the stable <c>Vehicle.Id</c>).</param>
/// <param name="Action">
///     The stable action key the executor dispatches on (e.g. <c>"vessel.ignite"</c>,
///     <c>"engine.active"</c>, <c>"animation.goal"</c>). Defined once in the integration matrix.
/// </param>
/// <param name="Ordinal">
///     The module ordinal the action addresses (engine/animation index), or <c>-1</c> for a
///     vessel-level action.
/// </param>
/// <param name="Value">
///     The numeric argument: a <c>0</c>/<c>1</c> flag, a <c>0..1</c> fraction, or the trigger
///     token (conventionally <c>1</c>).
/// </param>
/// <param name="Phase">Which game-thread phase must execute the command.</param>
public sealed record SimCommand(
    string VesselId,
    string Action,
    int Ordinal,
    double Value,
    CommandPhase Phase = CommandPhase.Frame)
{
    /// <summary>Sentinel for <see cref="Ordinal"/> meaning "vessel-level, no module".</summary>
    public const int NoOrdinal = -1;

    /// <summary>
    ///     Action keys whose mutation must be visible to the physics solvers within the same sim
    ///     step, so they drain in the solver-phase queue rather than the per-frame hook (the debug
    ///     refills today). The single source of truth: transports that infer phase from a parsed
    ///     action key (HTTP/MQTT) call <see cref="PhaseFor"/>; the 9p tree builds these commands
    ///     with the matching explicit phase. Add a new solver action here and the inference follows.
    /// </summary>
    private static readonly HashSet<string> SolverActions =
        new(StringComparer.Ordinal) { "debug.refill_fuel", "debug.refill_battery" };

    /// <summary>The game-thread phase an action must execute in (KSA_GAME_INTEGRATION_PLAN §3.1).</summary>
    public static CommandPhase PhaseFor(string action)
        => SolverActions.Contains(action) ? CommandPhase.Solver : CommandPhase.Frame;

    /// <summary>
    ///     Multi-component numeric payload for vector actions (attitude quaternion, burn
    ///     <c>ut + Δv</c>, teleport state). Null for scalar/flag/trigger actions, which use
    ///     <see cref="Value"/>.
    /// </summary>
    public IReadOnlyList<double>? Values { get; init; }

    /// <summary>
    ///     Symbolic-token payload for enum actions (attitude mode/frame, vessel-switch target id).
    ///     Null for numeric actions.
    /// </summary>
    public string? Token { get; init; }
}
