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
///         <c>Vehicle.Split(Connector, PushoffForce)</c>). Buffering through KSA's own path (rather
///         than mutating the part tree inline) keeps the camera/flight-computer fix-up the game does
///         after a split; and</item>
///         <item><see cref="SetPushoffForce"/> overwrites that separation impulse (Newtons) on the
///         live module — the debug knob over the value the part's XML seeds (<c>PushoffForce</c>,
///         stock 7000 N).</item>
///     </list>
///     Game-thread only.
/// </summary>
internal static class DockingActuator
{
    [KsaAnchor("DockingPort.Docked; InputEvents.VehicleDockingInputData{Undock=true} → DockingPort.Undock "
            + "→ Vehicle.Split(Connector, PushoffForce); Vehicle.MeanRadius",
        SourceFile = "KSA/DockingPort.cs / KSA/InputEvents.cs", Verified = "2026-06-16", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the UnDock context-menu enqueue. Undocking a port that is not docked is rejected "
            + "(EBUSY) rather than handing Split a null connection.")]
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
            OldMeanRadius = vehicle.MeanRadius,
            Undock = true,
        });
        return CommandResult.Ok;
    }

    [KsaAnchor("DockingPort.PushoffForce (public mutable float, seeded from DockingPortTemplate.PushoffForce)",
        SourceFile = "KSA/DockingPort.cs", Verified = "2026-06-16", Risk = ChurnRisk.Medium,
        Notes = "The separation impulse Undock's Vehicle.Split applies; stock parts seed 7000 N from XML. "
            + "Negative values are rejected (EINVAL).")]
    internal static CommandResult SetPushoffForce(Vehicle vehicle, int ordinal, double newtons)
    {
        if (newtons < 0)
            return new CommandResult(CommandOutcome.Invalid, "pushoff force must be >= 0 N");
        var ports = vehicle.Parts.Modules.Get<DockingPort>();
        if (ordinal < 0 || ordinal >= ports.Length)
            return new CommandResult(CommandOutcome.NotFound, $"docking port {ordinal} does not exist");
        ports[ordinal].PushoffForce = (float)newtons;
        return CommandResult.Ok;
    }
}
