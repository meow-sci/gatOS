using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Vessel master lights (KSA_GAME_INTEGRATION_PLAN §5.1 <c>ctl/lights</c>). Mirrors
///     <c>Vehicle.ToggleLights()</c> but as a deterministic set: flips the master flag and writes
///     <c>LightIsActive</c> on every light-switch <see cref="PowerConsumer"/> (the field the
///     solver reads each tick). The proven unscience <c>zippo</c>/<c>red-alert</c> light path.
///     Game-thread only.
/// </summary>
internal static class LightActuator
{
    [KsaAnchor("Vehicle.LightsOn; PowerConsumer.LightSwitch/.LightIsActive", SourceFile = "KSA/Vehicle.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Replicates ToggleLights() as a set. PowerConsumer.UpdateModules reads LightIsActive "
                + "into state.Active each tick when LightSwitch is set.")]
    internal static CommandResult SetMaster(Vehicle vehicle, bool on)
    {
        vehicle.LightsOn = on;
        foreach (var consumer in vehicle.Parts.Modules.Get<PowerConsumer>())
            if (consumer.LightSwitch)
                consumer.LightIsActive = on;
        return CommandResult.Ok;
    }
}
