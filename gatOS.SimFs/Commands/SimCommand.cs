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
public sealed record SimCommand(
    string VesselId,
    string Action,
    int Ordinal,
    double Value)
{
    /// <summary>Sentinel for <see cref="Ordinal"/> meaning "vessel-level, no module".</summary>
    public const int NoOrdinal = -1;

    /// <summary>
    ///     The lone source of truth for which actions must drain in the solver phase: their mutation
    ///     is only visible to (or only survives) the per-vehicle physics solver if applied inside the
    ///     solver step (KSA_GAME_INTEGRATION_PLAN §3.1). Two reasons land an action here:
    ///     <list type="bullet">
    ///         <item>the debug refills mutate resource state the solver reads that same tick; and</item>
    ///         <item>the flight-computer setpoints (<c>attitude_mode</c>/<c>attitude_frame</c>/
    ///         <c>attitude_target</c>/<c>burn</c>) write fields that KSA's async vehicle solver
    ///         <i>snapshots and restores</i> every frame (<c>FlightComputer.CopyFrom</c> at prepare
    ///         and apply). A frame-phase write lands outside that capture and is overwritten by the
    ///         in-flight solve — the value flashes on, then reverts to manual. Draining in the solver
    ///         prefix (just before the prepare-capture) makes it stick.</item>
    ///     </list>
    ///     Because <see cref="Phase"/> is derived from this set, every transport (9p/HTTP/MQTT/serial)
    ///     gets the right phase by construction — add an action here and they all follow.
    /// </summary>
    private static readonly HashSet<string> SolverActions =
        new(StringComparer.Ordinal)
        {
            "debug.refill_fuel", "debug.refill_battery",
            "vessel.attitude_mode", "vessel.attitude_frame", "vessel.attitude_target", "vessel.burn",
        };

    /// <summary>The game-thread phase an action must execute in (KSA_GAME_INTEGRATION_PLAN §3.1).</summary>
    public static CommandPhase PhaseFor(string action)
        => SolverActions.Contains(action) ? CommandPhase.Solver : CommandPhase.Frame;

    /// <summary>
    ///     Which game-thread phase must execute this command — derived purely from
    ///     <see cref="Action"/> (see <see cref="SolverActions"/>), so it cannot be set wrong at a
    ///     construction site. Excluded from value-equality (no backing field); <see cref="Action"/>
    ///     already participates, and phase is a function of it.
    /// </summary>
    public CommandPhase Phase => PhaseFor(Action);

    /// <summary>
    ///     Multi-component numeric payload for vector actions (attitude quaternion, burn
    ///     <c>ut + Δv</c>, teleport state, impulse vector). Null for scalar/flag/trigger actions,
    ///     which use <see cref="Value"/>.
    /// </summary>
    public IReadOnlyList<double>? Values { get; init; }

    /// <summary>
    ///     Symbolic-token payload for enum actions (attitude mode/frame, vessel-switch target id,
    ///     the <c>debug.impulse</c> frame keyword). Null for numeric actions.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    ///     Secondary symbolic payload, for the rare action whose argument shape carries <i>two</i>
    ///     strings: <c>audio.play</c> uses <see cref="Token"/> for the clip name and this for the
    ///     caller-chosen channel <c>id=</c> (null = auto-assign); <c>debug.impulse</c> uses
    ///     <see cref="Token"/> for the frame keyword and this for the unit keyword. Null everywhere
    ///     else.
    /// </summary>
    public string? Aux { get; init; }
}
