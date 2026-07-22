namespace gatOS.SimFs.Commands;

/// <summary>
///     The game-free validation rule for the <c>vessel.rotate</c> action (the <c>ctl/rotate</c>
///     control): exactly three finite components, whose <b>signs</b> command bang-bang RCS torque
///     about the vessel body axes (+X = roll right, +Y = pitch up, +Z = yaw right; <c>0</c> = that
///     axis off). Lives here rather than in the game-coupled actuator so the EINVAL boundary is
///     unit-testable without game assemblies — the HTTP/MQTT <c>command</c> paths reach the
///     actuator without the 9p control-file parse, so the actuator re-validates through this same
///     rule.
/// </summary>
public static class RotateRules
{
    /// <summary>
    ///     Validates a <c>vessel.rotate</c> payload. Returns the EINVAL detail message, or null
    ///     when valid.
    /// </summary>
    public static string? Validate(IReadOnlyList<double> axes)
    {
        if (axes.Count != 3)
            return "rotate expects 'x y z' (signs command body-axis torque; '0 0 0' stops)";
        foreach (var component in axes)
            if (!double.IsFinite(component))
                return "rotate components must be finite";
        return null;
    }
}
