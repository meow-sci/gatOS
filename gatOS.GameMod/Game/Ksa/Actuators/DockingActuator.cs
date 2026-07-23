using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Docking-port controls (KSA_GAME_INTEGRATION_PLAN §5.2). Two actions:
///     <list type="bullet">
///         <item><see cref="Undock"/> separates a docked port exactly the way the game's own
///         "UnDock" context-menu item does — by enqueuing a
///         <see cref="InputEvents.VehicleDockingInputData"/> with <c>Undock = true</c>, which the
///         game applies in its input phase (<c>DockingPort.Undock</c> →
///         <c>Vehicle.Split(Connector, PushoffImpulse)</c>). Buffering through KSA's own path (rather
///         than mutating the part tree inline) keeps the camera/flight-computer fix-up the game does
///         after a split; and</item>
///         <item><see cref="SetPushoffImpulse"/> overwrites that separation impulse (newton-seconds,
///         N·s) on the live module — the debug knob over the value the part's XML seeds
///         (<c>PushoffImpulse</c>, stock 7000 N·s).</item>
///     </list>
///     Game-thread only.
/// </summary>
internal static class DockingActuator
{
    [KsaAnchor("DockingPort.Docked; InputEvents.VehicleDockingInputData{Undock=true} → DockingPort.Undock "
            + "→ Vehicle.Split(Connector, PushoffImpulse)",
        SourceFile = "KSA/DockingPort.cs / KSA/InputEvents.cs", Verified = "2026-07-22",
        GameVersion = "2026.7.8.4980", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the UnDock context-menu enqueue. Undocking a port that is not docked is rejected "
            + "(EBUSY) rather than handing Split a null connection. 4750 (rev 4683): Undock now hands "
            + "Vehicle.Split an impulse (PushoffImpulse, N·s) — Undock itself enqueues, never calls Split. "
            + "4980 (rev 4943): OldMeanRadius removed from VehicleDockingInputData (camera fix-up no longer "
            + "needs it) — the enqueue is just {Vehicle, DockingPort, Undock}.")]
    internal static CommandResult Undock(Vehicle vehicle, int ordinal)
    {
        var ports = vehicle.Parts.Modules.Get<DockingPort>();
        if (ordinal < 0 || ordinal >= ports.Length)
            return new CommandResult(CommandOutcome.NotFound, $"docking port {ordinal} does not exist");
        var port = ports[ordinal];
        if (!port.Docked)
            return new CommandResult(CommandOutcome.Busy, $"docking port {ordinal} is not docked");
        InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData
        {
            Vehicle = vehicle,
            DockingPort = port,
            Undock = true,
        });
        return CommandResult.Ok;
    }

    [KsaAnchor("DockingPort.PushoffImpulse (public required float, seeded from DockingPortTemplate.PushoffImpulse)",
        SourceFile = "KSA/DockingPort.cs", Verified = "2026-06-27", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Medium,
        Notes = "The separation impulse Undock's Vehicle.Split applies; stock parts seed 7000 N·s from XML. "
            + "Negative values are rejected (EINVAL). 4750 (rev 4683): renamed PushoffForce→PushoffImpulse, "
            + "force (N)→impulse (N·s).")]
    internal static CommandResult SetPushoffImpulse(Vehicle vehicle, int ordinal, double impulse)
    {
        if (impulse < 0)
            return new CommandResult(CommandOutcome.Invalid, "pushoff impulse must be >= 0 N·s");
        var ports = vehicle.Parts.Modules.Get<DockingPort>();
        if (ordinal < 0 || ordinal >= ports.Length)
            return new CommandResult(CommandOutcome.NotFound, $"docking port {ordinal} does not exist");
        ports[ordinal].PushoffImpulse = (float)impulse;
        return CommandResult.Ok;
    }
}
