using gatOS.GameMod.Game.Ksa.Render;
using gatOS.SimFs.Commands;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Actuator for the global <c>/sim/debug/always_render_iva</c> cheat — toggles
///     <see cref="IvaForceRender"/>. Vessel-agnostic (handled before vehicle resolution in
///     <see cref="KsaCatalog"/>), drained on the game thread.
/// </summary>
internal static class IvaActuator
{
    /// <summary>Turns the always-render-IVA cheat on/off; never fails (the toggle is pure-local state).</summary>
    internal static CommandResult SetAlwaysRender(bool on)
    {
        IvaForceRender.SetEnabled(on);
        return CommandResult.Ok;
    }
}
