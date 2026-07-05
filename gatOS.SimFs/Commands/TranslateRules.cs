namespace gatOS.SimFs.Commands;

/// <summary>
///     The game-free validation rule for the <c>vessel.translate</c> action (the
///     <c>ctl/translate</c> control): exactly three finite components, whose <b>signs</b> command
///     bang-bang RCS thrust along the vessel body axes (+X = forward/nose, +Y = right, +Z = down;
///     <c>0</c> = that axis off). Lives here rather than in the game-coupled actuator so the EINVAL
///     boundary is unit-testable without game assemblies — the HTTP/MQTT <c>command</c> paths reach
///     the actuator without the 9p control-file parse, so the actuator re-validates through this
///     same rule.
/// </summary>
public static class TranslateRules
{
    /// <summary>
    ///     Validates a <c>vessel.translate</c> payload. Returns the EINVAL detail message, or null
    ///     when valid.
    /// </summary>
    public static string? Validate(IReadOnlyList<double> axes)
    {
        if (axes.Count != 3)
            return "translate expects 'x y z' (signs command body-axis thrust; '0 0 0' stops)";
        foreach (var component in axes)
            if (!double.IsFinite(component))
                return "translate components must be finite";
        return null;
    }
}
