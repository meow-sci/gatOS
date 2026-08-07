namespace gatOS.SimFs.Camera;

/// <summary>
///     The game-free validation and token vocabulary of the <c>/sim/camera</c> surface — the
///     <c>&lt;Feature&gt;Rules</c> class AGENTS.md §3 mandates (precedents: <c>ScaleRules</c>,
///     <c>ImpulseRules</c>, <c>TranslateRules</c>, <c>RotateRules</c>).
/// </summary>
/// <remarks>
///     <para>
///         It exists because there are <b>two</b> ways into the command pipeline. A 9p write parses
///         through <c>CameraCommands</c> and never reaches the sink with a bad value; but
///         <c>POST /v1/command</c> and MQTT <c>gatos/command</c> author a <c>SimCommand</c> directly
///         and bypass that parse entirely. Both sides must apply the <i>same</i> rules, so the rules
///         live here — table-driven, allocation-free, and unit-testable with no game DLLs present.
///     </para>
///     <para>
///         Nothing here reads config. Bounds that a player can tune (the FOV limits) arrive as
///         parameters, so this class stays a pure function of its inputs and the config plumbing
///         stays in one place.
///     </para>
/// </remarks>
public static class CameraRules
{
    /// <summary>The canonical <see cref="FrameKind"/> tokens, indexed by enum ordinal.</summary>
    public static readonly string[] FrameTokens = ["ecl", "cce", "bodyfixed", "enu", "lvlh", "chase"];

    /// <summary>The canonical <see cref="AimUpKind"/> tokens, indexed by enum ordinal.</summary>
    public static readonly string[] AimUpTokens = ["world", "target", "velocity", "free"];

    /// <summary>The canonical <see cref="CameraModeKind"/> tokens, indexed by enum ordinal.</summary>
    public static readonly string[] ModeTokens = ["orbit", "free", "map", "iva", "fixed"];

    /// <summary>The widest smoothing time accepted, in seconds. Beyond this the camera stops tracking.</summary>
    public const double MaxSmoothingSeconds = 10.0;

    /// <summary>
    ///     The <c>/sim</c> sanitized-id charset (SPEC §2.2): 1..64 chars of <c>[A-Za-z0-9._-]</c>,
    ///     excluding <c>.</c> and <c>..</c>. Identical to <c>AudioStore.IsValidName</c> and
    ///     <c>ScheduleStore.IsValidId</c> — one rule, so a <c>vessel:</c> reference names exactly what
    ///     <c>/sim/vessels/by-id/&lt;id&gt;</c> does.
    /// </summary>
    public static bool IsValidId(string id)
    {
        if (id.Length is 0 or > 64 || id is "." or "..")
            return false;
        foreach (var c in id)
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
                return false;
        return true;
    }

    // ---- token tables ---------------------------------------------------------------------------

    /// <summary>Parses a frame token (case-insensitive). False ⇒ EINVAL.</summary>
    public static bool TryParseFrame(string? token, out FrameKind frame)
        => TryParseToken(FrameTokens, token, out var ordinal)
            ? Set(out frame, (FrameKind)ordinal)
            : Set(out frame, FrameKind.Ecl, false);

    /// <summary>The canonical token for a frame, or null when the value is out of range.</summary>
    public static string? NameOf(FrameKind frame) => NameOfOrdinal(FrameTokens, (int)frame);

    /// <summary>Parses an aim-up token (case-insensitive). False ⇒ EINVAL.</summary>
    public static bool TryParseAimUp(string? token, out AimUpKind up)
        => TryParseToken(AimUpTokens, token, out var ordinal)
            ? Set(out up, (AimUpKind)ordinal)
            : Set(out up, AimUpKind.World, false);

    /// <summary>The canonical token for an aim-up mode, or null when the value is out of range.</summary>
    public static string? NameOf(AimUpKind up) => NameOfOrdinal(AimUpTokens, (int)up);

    /// <summary>Parses a camera-mode token (case-insensitive). False ⇒ EINVAL.</summary>
    public static bool TryParseMode(string? token, out CameraModeKind mode)
        => TryParseToken(ModeTokens, token, out var ordinal)
            ? Set(out mode, (CameraModeKind)ordinal)
            : Set(out mode, CameraModeKind.Orbit, false);

    /// <summary>The canonical token for a camera mode, or null when the value is out of range.</summary>
    public static string? NameOf(CameraModeKind mode) => NameOfOrdinal(ModeTokens, (int)mode);

    // ---- scalar validation ----------------------------------------------------------------------

    /// <summary>
    ///     Whether a field of view in degrees is acceptable. The bounds come from
    ///     <c>[camera] camera_fov_min</c>/<c>camera_fov_max</c> and are deliberately wider than the
    ///     game's own 15–120 clamp: <c>Camera.SetFieldOfView</c> does not clamp, so fisheye and
    ///     telephoto are genuinely available.
    /// </summary>
    public static bool IsValidFov(double deg, double min, double max)
        => double.IsFinite(deg) && deg >= min && deg <= max;

    /// <summary>Whether a geodetic latitude is in <c>[-90, 90]</c> degrees.</summary>
    public static bool IsValidLatitude(double deg) => double.IsFinite(deg) && deg is >= -90 and <= 90;

    /// <summary>
    ///     Whether a geodetic longitude is acceptable. <c>[-180, 360]</c> is accepted so that both
    ///     conventions ("-80.6" and "279.4") work without the caller knowing which one gatOS prefers;
    ///     <see cref="NormalizeLongitude"/> folds either into the canonical <c>[-180, 180)</c>.
    /// </summary>
    public static bool IsValidLongitude(double deg) => double.IsFinite(deg) && deg is >= -180 and <= 360;

    /// <summary>
    ///     Folds an accepted longitude into the canonical <c>[-180, 180)</c> half-open range, so a
    ///     read-back never reports a value the writer would not recognise.
    /// </summary>
    public static double NormalizeLongitude(double deg)
    {
        if (!double.IsFinite(deg))
            return 0;
        var wrapped = (deg + 180.0) % 360.0;
        if (wrapped < 0)
            wrapped += 360.0;
        return wrapped - 180.0;
    }

    /// <summary>
    ///     Whether an altitude above the reference surface is acceptable: finite and non-negative.
    ///     (The game's own <c>ClampCamera</c> floor of 0.5 m still applies below that — a feature,
    ///     not a rule this class can express.)
    /// </summary>
    public static bool IsValidAltitude(double metres) => double.IsFinite(metres) && metres >= 0;

    /// <summary>Whether a roll angle in degrees is usable (any finite value; roll wraps freely).</summary>
    public static bool IsValidRoll(double deg) => double.IsFinite(deg);

    /// <summary>Whether an orthographic half-height in metres is usable: finite and strictly positive.</summary>
    public static bool IsValidOrthoHeight(double metres) => double.IsFinite(metres) && metres > 0;

    /// <summary>
    ///     Whether a smoothing time in seconds is usable: <c>[0, 10]</c>. <c>0</c> is raw (no
    ///     smoothing); the upper bound exists because a critically-damped follow with a multi-second
    ///     time constant stops tracking its target in any recognisable way.
    /// </summary>
    public static bool IsValidSmoothing(double seconds)
        => double.IsFinite(seconds) && seconds >= 0 && seconds <= MaxSmoothingSeconds;

    /// <summary>Whether an orbit radius about the anchor is usable: finite and non-negative.</summary>
    public static bool IsValidOrbitRadius(double metres) => double.IsFinite(metres) && metres >= 0;

    /// <summary>
    ///     Whether an orbit elevation angle is in <c>[-90, 90]</c> degrees — the poles of the sphere
    ///     the camera is placed on, exactly like a latitude.
    /// </summary>
    public static bool IsValidOrbitElevation(double deg) => IsValidLatitude(deg);

    /// <summary>Whether an orbit azimuth is usable (any finite value; azimuth wraps freely).</summary>
    public static bool IsValidOrbitAzimuth(double deg) => double.IsFinite(deg);

    /// <summary>
    ///     Whether a simulation time-scale factor is usable: finite and non-negative. <c>0</c> is a
    ///     legal pause, matching <c>debug.warp</c>'s own vocabulary.
    /// </summary>
    public static bool IsValidTimeScale(double factor) => double.IsFinite(factor) && factor >= 0;

    // ---- vector validation ----------------------------------------------------------------------

    /// <summary>Whether every component of a vector is finite (the "refuse to move" gate).</summary>
    public static bool IsFiniteVector(IReadOnlyList<double>? values)
    {
        if (values is null)
            return false;
        for (var i = 0; i < values.Count; i++)
            if (!double.IsFinite(values[i]))
                return false;
        return true;
    }

    /// <summary>Whether a vector is finite and has exactly <paramref name="arity"/> components.</summary>
    public static bool IsFiniteVector(IReadOnlyList<double>? values, int arity)
        => values is not null && values.Count == arity && IsFiniteVector(values);

    /// <summary>
    ///     Whether four components can be read as a rotation: finite, and with a norm inside
    ///     <c>[0.5, 2.0]</c> so the caller's quaternion can be renormalised without amplifying noise.
    ///     A zero quaternion is rejected outright — it names no rotation, and normalising it would
    ///     silently substitute identity for whatever the author meant.
    /// </summary>
    public static bool IsUnitQuaternionish(IReadOnlyList<double>? values)
    {
        if (!IsFiniteVector(values, 4))
            return false;
        var lengthSquared = values![0] * values[0] + values[1] * values[1]
                            + values[2] * values[2] + values[3] * values[3];
        return lengthSquared is >= 0.25 and <= 4.0;
    }

    // ---- internals --------------------------------------------------------------------------------

    private static bool TryParseToken(string[] table, string? token, out int ordinal)
    {
        ordinal = -1;
        if (string.IsNullOrEmpty(token))
            return false;
        for (var i = 0; i < table.Length; i++)
        {
            if (!string.Equals(table[i], token, StringComparison.OrdinalIgnoreCase))
                continue;
            ordinal = i;
            return true;
        }

        return false;
    }

    private static string? NameOfOrdinal(string[] table, int ordinal)
        => ordinal >= 0 && ordinal < table.Length ? table[ordinal] : null;

    /// <summary>Assigns an out parameter inside an expression body and reports the outcome.</summary>
    private static bool Set<T>(out T target, T value, bool result = true)
    {
        target = value;
        return result;
    }
}
