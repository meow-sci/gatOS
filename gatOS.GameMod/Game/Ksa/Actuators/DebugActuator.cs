using Brutal.Numerics;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     The cheat-tier actuators behind <c>/sim/debug</c> (KSA_GAME_INTEGRATION_PLAN §5.3, G-D2):
///     teleport, instant refills, warp-set and vessel switch. Fenced off from the "realistic
///     hardware" surface and gated by <c>[control] debug_namespace</c>. The refills run in the
///     <b>solver phase</b> (the proven unscience <c>eternal-flame</c> pattern); the rest run in the
///     frame phase. Game-thread only.
/// </summary>
internal static class DebugActuator
{
    [KsaAnchor("Universe.SetSimulationSpeed(double, alert:false)", SourceFile = "KSA/Universe.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium, Notes = "Public; no reflection needed.")]
    internal static CommandResult SetWarp(double factor)
    {
        if (!double.IsFinite(factor) || factor < 0)
            return new CommandResult(CommandOutcome.Invalid, "warp must be a finite factor ≥ 0");
        Universe.SetSimulationSpeed(factor, alert: false);
        return CommandResult.Ok;
    }

    [KsaAnchor("Program.ControlledVehicle (public static field)", SourceFile = "KSA/Program.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium, Notes = "Direct assignment; no reflection needed.")]
    internal static CommandResult SwitchVessel(Vehicle target)
    {
        Program.ControlledVehicle = target;
        return CommandResult.Ok;
    }

    [KsaAnchor("Orbit.CreateFromStateCci + Vehicle.Teleport + Vehicle.UpdatePerFrameData (garrys-torch pattern)",
        SourceFile = "KSA/Orbit.cs / KSA/Vehicle.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
        Notes = "Sets a CCI state vector about the current parent; UpdatePerFrameData syncs caches.")]
    internal static CommandResult Teleport(Vehicle vehicle, IReadOnlyList<double> state)
    {
        if (state.Count != 6)
            return new CommandResult(CommandOutcome.Invalid, "teleport expects 'px py pz vx vy vz' (CCI)");
        foreach (var c in state)
            if (!double.IsFinite(c))
                return new CommandResult(CommandOutcome.Invalid, "teleport components must be finite");
        if (vehicle.Parent is not { } parent)
            return new CommandResult(CommandOutcome.Busy, "vessel has no parent body to teleport about");

        var position = new double3(state[0], state[1], state[2]);
        var velocity = new double3(state[3], state[4], state[5]);
        var orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedSimTime(), position, velocity, default);
        vehicle.Teleport(orbit, null, null);
        vehicle.UpdatePerFrameData();
        return CommandResult.Ok;
    }

    [KsaAnchor("Vehicle.RefillConsumables()", SourceFile = "KSA/Vehicle.cs", Verified = "2026-06-12",
        Risk = ChurnRisk.Medium, Notes = "Solver phase (resource state is solver-visible).")]
    internal static CommandResult RefillFuel(Vehicle vehicle)
    {
        vehicle.RefillConsumables();
        return CommandResult.Ok;
    }

    [KsaAnchor("Battery.Refill(ref state) via Batteries.GetModuleAndAllMutableStatesForInitialization (eternal-flame)",
        SourceFile = "KSA/Battery.cs", Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Solver phase: mutable state ref obtained the way eternal-flame does.")]
    internal static CommandResult RefillBattery(Vehicle vehicle)
    {
        var batteries = vehicle.Parts.Batteries;
        foreach (var battery in batteries.Modules)
        {
            var mutable = batteries.GetModuleAndAllMutableStatesForInitialization(battery);
            mutable.Module.Refill(ref mutable.State);
        }

        return CommandResult.Ok;
    }
}
