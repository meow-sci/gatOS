using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Keyframe-animation deploy control (KSA_GAME_INTEGRATION_PLAN §5.2 <c>animations/&lt;n&gt;/goal</c>
///     and <c>solar/&lt;n&gt;/goal</c>). Drives the onboard setpoint <see cref="KeyframeAnimationModule.TimeGoal"/>
///     to <c>fraction × Duration</c>; the sim then integrates the animation itself, so this behaves
///     correctly at any time-warp (the proven unscience <c>red-alert</c> solar-deploy path). Game-thread only.
/// </summary>
internal static class AnimationActuator
{
    [KsaAnchor("KeyframeAnimationModule.TimeGoal = fraction × Shared.Duration",
        SourceFile = "KSA/KeyframeAnimationModule.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "fraction 0 = retract, 1 = deploy (PWM-duty-cycle semantics). Ordinal = animation index "
                + "from VesselReader.SampleAnimations; solar/<n> maps its ordinal to the same index.")]
    internal static CommandResult SetGoal(Vehicle vehicle, int ordinal, double fraction)
    {
        var animations = vehicle.Parts.Modules.Get<KeyframeAnimationModule>();
        if (ordinal < 0 || ordinal >= animations.Length)
            return new CommandResult(CommandOutcome.NotFound, $"animation {ordinal} does not exist");
        var module = animations[ordinal];
        module.TimeGoal = (float)(Math.Clamp(fraction, 0, 1) * module.Shared.Duration);
        return CommandResult.Ok;
    }
}
