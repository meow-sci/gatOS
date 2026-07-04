using Brutal.Numerics;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     The cheat-tier actuators behind <c>/sim/debug</c> (KSA_GAME_INTEGRATION_PLAN §5.3, G-D2):
///     teleport, one-shot impulse, instant refills, warp-set and vessel switch. Fenced off from the "realistic
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

    [KsaAnchor("Program.GetMainCamera().SetFollow(vehicle, changeControl:false); Program.ControlledVehicle = vehicle",
        SourceFile = "KSA/Program.cs / KSA/Camera.cs", Verified = "2026-06-16", Risk = ChurnRisk.Medium,
        Notes = "Focuses the main camera on the vehicle AND takes control of it. Camera move is best-effort "
            + "(skipped if no camera); the control grant is unconditional. Cheat-tier (grants authority).")]
    internal static CommandResult ControlVessel(Vehicle target)
    {
        Program.GetMainCamera()?.SetFollow(target, tidalLocking: true, changeControl: false);
        Program.ControlledVehicle = target;
        return CommandResult.Ok;
    }

    [KsaAnchor("Orbit.CreateFromStateCci + Vehicle.Teleport + Vehicle.UpdatePerFrameData (physics-bypass teleport pattern)",
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

    [KsaAnchor("Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,TotalMass,Parent} + Orbit.CreateFromStateCci "
            + "+ Vehicle.Teleport + Vehicle.UpdatePerFrameData (velocity-bump variant of the teleport pattern)",
        SourceFile = "KSA/Vehicle.cs / KSA/Orbit.cs", Verified = "2026-07-04", Risk = ChurnRisk.High,
        Notes = "One-shot impulsive kick: Δv = J/TotalMass — the same math as KSA's own separation impulse "
            + "(Vehicle.Split) — applied by rebuilding the orbit from the current CCI position and the bumped "
            + "velocity, so it works on-rails and in the physics bubble alike. 'body' rotates the vector by "
            + "Body2Cci (body +X = nose); 'dv' skips the mass division and applies the vector as Δv m/s.")]
    internal static CommandResult Impulse(Vehicle vehicle, IReadOnlyList<double> vector, string? frame, string? unit)
    {
        // Re-validate through the shared game-free rules: the HTTP/MQTT command paths reach here
        // without the 9p control-file parse, so arity/keywords are not pre-checked on that route.
        if (ImpulseRules.Validate(vector, frame, unit) is { } error)
            return new CommandResult(CommandOutcome.Invalid, error);
        if (vehicle.Parent is not { } parent)
            return new CommandResult(CommandOutcome.Busy, "vessel has no parent body to impulse about");

        var input = new double3(vector[0], vector[1], vector[2]);
        var cci = ImpulseRules.IsBodyFrame(frame) ? double3.Transform(input, vehicle.GetBody2Cci()) : input;
        var deltaV = cci;
        if (!ImpulseRules.IsDeltaV(unit))
        {
            double mass = vehicle.TotalMass;
            if (!double.IsFinite(mass) || mass <= 0)
                return new CommandResult(CommandOutcome.Busy, "vessel mass unavailable for an N·s impulse");
            deltaV = cci / mass;
        }

        if (deltaV.X == 0 && deltaV.Y == 0 && deltaV.Z == 0)
            return CommandResult.Ok; // zero kick — nothing to apply

        var velocity = vehicle.GetVelocityCci() + deltaV;
        if (!double.IsFinite(velocity.X) || !double.IsFinite(velocity.Y) || !double.IsFinite(velocity.Z))
            return new CommandResult(CommandOutcome.Invalid, "resulting velocity is not finite");

        var orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedSimTime(),
            vehicle.GetPositionCci(), velocity, default);
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
