using System.Globalization;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Camera;

/// <summary>
///     The <c>camera.*</c> action-key vocabulary and the four composite line grammars behind
///     <c>/sim/camera/{pose/aim, pose/geo, pose/position, play, set}</c>
///     (plans/CAMERA_CONTROLS_PLAN.md §4). Everything else on the camera surface is a plain archetype
///     (Flag/Number/Vector/Enum/Token/Trigger) and needs no bespoke parser.
/// </summary>
/// <remarks>
///     <para>
///         The grammars parse fully <b>here</b>, in the game-free SimFs layer, so a bad line fails the
///         guest's <c>write(2)</c> with EINVAL immediately and the whole grammar is unit-testable
///         without a game. The normalized command is what rides the queue to the director — and what
///         <c>POST /v1/command</c> / <c>gatos/command</c> callers author directly, which is why the
///         director must re-check the same <see cref="CameraRules"/> game-side.
///     </para>
///     <para>
///         <b>Addressing is global</b> (AGENTS.md §4 mode 1): <c>VesselId</c> is <c>""</c> and
///         <c>Ordinal</c> is <see cref="SimCommand.NoOrdinal"/> on every action here — the camera is one
///         object, not a per-vessel module. Every action is <b>Frame</b> phase; none is in
///         <c>SimCommand.SolverActions</c>, because nothing about the camera is visible to the vehicle
///         solver.
///     </para>
///     <para>
///         Argument style: a positional token 0 where the grammar has an obvious subject, then
///         <c>keyword value…</c> groups — the plan's own spelling
///         (<c>vessel:kitten-01 off 0 0.9 0 frame bodyfixed up world</c>), not <c>key=value</c>. Fixed-arity
///         <c>Values</c> slot arrays (with named slot constants) rather than sparse maps, so the actuator
///         reads a slot instead of searching; the one exception is <see cref="ParseSet"/>, which is a
///         genuinely sparse patch and therefore uses flat <c>[key, value, …]</c> pairs like
///         <c>audio.set</c>.
///     </para>
/// </remarks>
public static class CameraCommands
{
    // ---- action keys -------------------------------------------------------------------------------

    /// <summary>Take (1) or release (0) ownership of the camera.</summary>
    public const string EnabledAction = "camera.enabled";

    /// <summary>Hard restore to game control (trigger).</summary>
    public const string ReleaseAction = "camera.release";

    /// <summary>Set the viewport camera mode (enum token).</summary>
    public const string ModeAction = "camera.mode";

    /// <summary>Set what the game camera follows (target reference token).</summary>
    public const string FollowAction = "camera.follow";

    /// <summary>Set the tidal-locking flag of the follow.</summary>
    public const string TidalAction = "camera.tidal";

    /// <summary>Place the camera: <c>"x y z [frame]"</c>.</summary>
    public const string PositionAction = "camera.position";

    /// <summary>Set the default frame position writes resolve in (enum token).</summary>
    public const string FrameAction = "camera.frame";

    /// <summary>Set the target frames resolve against (target reference token).</summary>
    public const string AnchorAction = "camera.anchor";

    /// <summary>Place the camera geodetically: <c>"lat lon alt [body:&lt;id&gt;]"</c>.</summary>
    public const string GeoAction = "camera.geo";

    /// <summary>Spherical placement about the anchor: radius, metres.</summary>
    public const string OrbitRadiusAction = "camera.orbit_radius";

    /// <summary>Spherical placement about the anchor: azimuth, degrees.</summary>
    public const string OrbitAzimuthAction = "camera.orbit_azimuth";

    /// <summary>Spherical placement about the anchor: elevation, degrees.</summary>
    public const string OrbitElevationAction = "camera.orbit_elevation";

    /// <summary>Set the ECL orientation quaternion (<c>x y z w</c>).</summary>
    public const string RotationAction = "camera.rotation";

    /// <summary>The composite aim convenience — target, offset, frame, up and optionally roll at once.</summary>
    public const string AimAction = "camera.aim";

    /// <summary>Set what the camera looks at (target reference token).</summary>
    public const string AimTargetAction = "camera.aim_target";

    /// <summary>Set the offset from the aim target (<c>x y z</c>).</summary>
    public const string AimOffsetAction = "camera.aim_offset";

    /// <summary>Set the frame the aim offset resolves in (enum token).</summary>
    public const string AimFrameAction = "camera.aim_frame";

    /// <summary>Set where "up" comes from (enum token).</summary>
    public const string AimUpAction = "camera.aim_up";

    /// <summary>Set the roll about the view axis, degrees.</summary>
    public const string RollAction = "camera.roll";

    /// <summary>Set the vertical field of view, degrees.</summary>
    public const string FovAction = "camera.fov";

    /// <summary>Set orthographic projection on/off.</summary>
    public const string OrthoAction = "camera.ortho";

    /// <summary>Set the orthographic half-height, metres.</summary>
    public const string OrthoHeightAction = "camera.ortho_height";

    /// <summary>Set the critically-damped follow time, seconds (0 = raw).</summary>
    public const string SmoothingAction = "camera.smoothing";

    /// <summary>Drop every live pose override (trigger).</summary>
    public const string PoseResetAction = "camera.pose_reset";

    /// <summary>Start playing an uploaded track.</summary>
    public const string PlayAction = "camera.play";

    /// <summary>Adjust the running player (sparse patch).</summary>
    public const string SetAction = "camera.set";

    /// <summary>Stop playback (trigger).</summary>
    public const string StopAction = "camera.stop";

    // ---- camera.aim value slots ---------------------------------------------------------------------

    /// <summary>Aim slot 0: offset X in the aim frame, metres.</summary>
    public const int AimOffX = 0;

    /// <summary>Aim slot 1: offset Y in the aim frame, metres.</summary>
    public const int AimOffY = 1;

    /// <summary>Aim slot 2: offset Z in the aim frame, metres.</summary>
    public const int AimOffZ = 2;

    /// <summary>Aim slot 3: the <see cref="FrameKind"/> ordinal the offset resolves in.</summary>
    public const int AimFrameOrdinal = 3;

    /// <summary>Aim slot 4: the <see cref="AimUpKind"/> ordinal.</summary>
    public const int AimUpOrdinal = 4;

    /// <summary>Aim slot 5: roll in degrees (meaningful only when <see cref="AimRollPresent"/> is 1).</summary>
    public const int AimRoll = 5;

    /// <summary>
    ///     Aim slot 6: 1 when the line carried a <c>roll</c> keyword. Roll is a channel of its own, so
    ///     an aim write that does not mention it must leave it alone rather than silently zero it.
    /// </summary>
    public const int AimRollPresent = 6;

    /// <summary>The number of <c>camera.aim</c> value slots.</summary>
    public const int AimSlots = 7;

    // ---- camera.geo / camera.position value slots ----------------------------------------------------

    /// <summary>Geo slot 0: latitude, degrees.</summary>
    public const int GeoLat = 0;

    /// <summary>Geo slot 1: longitude, degrees (already normalized to <c>[-180, 180)</c>).</summary>
    public const int GeoLon = 1;

    /// <summary>Geo slot 2: altitude above the reference surface, metres.</summary>
    public const int GeoAlt = 2;

    /// <summary>The number of <c>camera.geo</c> value slots.</summary>
    public const int GeoSlots = 3;

    /// <summary>The number of <c>camera.position</c> value slots (x, y, z).</summary>
    public const int PositionSlots = 3;

    // ---- camera.play value slots ---------------------------------------------------------------------

    /// <summary>Play slot 0: start offset in seconds.</summary>
    public const int PlayAtSeconds = 0;

    /// <summary>Play slot 1: rate multiplier.</summary>
    public const int PlayRate = 1;

    /// <summary>Play slot 2: loop flag 0/1.</summary>
    public const int PlayLoop = 2;

    /// <summary>Play slot 3: 1 when the line carried <c>at</c>.</summary>
    public const int PlayAtPresent = 3;

    /// <summary>Play slot 4: 1 when the line carried <c>rate</c>.</summary>
    public const int PlayRatePresent = 4;

    /// <summary>Play slot 5: 1 when the line carried <c>loop</c>.</summary>
    public const int PlayLoopPresent = 5;

    /// <summary>The number of <c>camera.play</c> value slots.</summary>
    public const int PlaySlots = 6;

    // ---- camera.set (key, value) pair keys ------------------------------------------------------------

    /// <summary>Set key: seek to a position, seconds (≥ 0).</summary>
    public const int SetT = 0;

    /// <summary>Set key: rate multiplier.</summary>
    public const int SetRate = 1;

    /// <summary>Set key: loop flag 0/1.</summary>
    public const int SetLoop = 2;

    /// <summary>Set key: paused flag 0/1.</summary>
    public const int SetPaused = 3;

    /// <summary>The widest accepted playback rate — the <c>PlaybackClock</c> ceiling.</summary>
    public const double MaxRate = 100.0;

    // ---- parsers --------------------------------------------------------------------------------------

    /// <summary>
    ///     Parses an <c>aim</c> line — <c>&lt;target&gt; [off &lt;x&gt; &lt;y&gt; &lt;z&gt;]
    ///     [frame &lt;frame&gt;] [up &lt;world|target|velocity|free&gt;] [roll &lt;deg&gt;]</c> — into a
    ///     <c>camera.aim</c> command. Null (⇒ EINVAL) on anything malformed, duplicated or unknown.
    /// </summary>
    /// <remarks>
    ///     A convenience that sets four channels at once (aim target, offset, frame, up) plus roll when
    ///     asked. Omitted <c>off</c>/<c>frame</c>/<c>up</c> take defaults — zero offset,
    ///     <see cref="FrameKind.BodyFixed"/> (so an offset stays attached to a moving subject: <c>+0.9</c>
    ///     on a kittenaut's own Y axis <i>stays</i> its head as it walks), <see cref="AimUpKind.World"/> —
    ///     because a composite convenience with half-set channels would be far more surprising than one
    ///     with documented defaults. Write the granular leaves when you want to change only one.
    /// </remarks>
    public static SimCommand? ParseAim(string line)
    {
        var tokens = Split(line);
        if (tokens.Length == 0 || !TargetRef.TryParse(tokens[0], out var target))
            return null;

        var values = new double[AimSlots];
        values[AimFrameOrdinal] = (int)FrameKind.BodyFixed;
        values[AimUpOrdinal] = (int)AimUpKind.World;
        var seen = 0;
        var i = 1;
        while (i < tokens.Length)
        {
            switch (tokens[i])
            {
                case "off":
                    if (!Claim(ref seen, 0) || i + 3 >= tokens.Length)
                        return null;
                    for (var axis = 0; axis < 3; axis++)
                    {
                        if (ParseNumber(tokens[i + 1 + axis]) is not { } component)
                            return null;
                        values[AimOffX + axis] = component;
                    }

                    i += 4;
                    break;

                case "frame":
                    if (!Claim(ref seen, 1) || i + 1 >= tokens.Length
                        || !CameraRules.TryParseFrame(tokens[i + 1], out var frame))
                        return null;
                    values[AimFrameOrdinal] = (int)frame;
                    i += 2;
                    break;

                case "up":
                    if (!Claim(ref seen, 2) || i + 1 >= tokens.Length
                        || !CameraRules.TryParseAimUp(tokens[i + 1], out var up))
                        return null;
                    values[AimUpOrdinal] = (int)up;
                    i += 2;
                    break;

                case "roll":
                    if (!Claim(ref seen, 3) || i + 1 >= tokens.Length
                        || ParseNumber(tokens[i + 1]) is not { } roll || !CameraRules.IsValidRoll(roll))
                        return null;
                    values[AimRoll] = roll;
                    values[AimRollPresent] = 1;
                    i += 2;
                    break;

                default:
                    return null;
            }
        }

        return new SimCommand("", AimAction, SimCommand.NoOrdinal, 0)
        {
            Token = target.ToString(),
            Values = values,
        };
    }

    /// <summary>
    ///     Parses a <c>geo</c> line — <c>&lt;lat&gt; &lt;lon&gt; &lt;alt&gt; [body:&lt;id&gt;]</c> — into a
    ///     <c>camera.geo</c> command. Null (⇒ EINVAL) on a bad number, a bad range, or a fourth token
    ///     that is not a <c>body:</c> reference.
    /// </summary>
    /// <remarks>
    ///     The body tail is optional: omitted, <see cref="SimCommand.Token"/> is <c>""</c> and the
    ///     placement uses whatever <c>pose/anchor</c> currently names. Longitude is normalized to
    ///     <c>[-180, 180)</c> at parse time so both the <c>-80.6</c> and <c>279.4</c> conventions work
    ///     and read-back is canonical.
    /// </remarks>
    public static SimCommand? ParseGeo(string line)
    {
        var tokens = Split(line);
        if (tokens.Length is < 3 or > 4)
            return null;
        if (ParseNumber(tokens[0]) is not { } lat || !CameraRules.IsValidLatitude(lat))
            return null;
        if (ParseNumber(tokens[1]) is not { } lon || !CameraRules.IsValidLongitude(lon))
            return null;
        if (ParseNumber(tokens[2]) is not { } alt || !CameraRules.IsValidAltitude(alt))
            return null;

        var body = "";
        if (tokens.Length == 4)
        {
            if (!TargetRef.TryParse(tokens[3], out var target) || target.Kind != TargetKind.Body)
                return null;
            body = target.ToString();
        }

        var values = new double[GeoSlots];
        values[GeoLat] = lat;
        values[GeoLon] = CameraRules.NormalizeLongitude(lon);
        values[GeoAlt] = alt;
        return new SimCommand("", GeoAction, SimCommand.NoOrdinal, 0) { Token = body, Values = values };
    }

    /// <summary>
    ///     Parses a <c>position</c> line — <c>&lt;x&gt; &lt;y&gt; &lt;z&gt; [&lt;frame&gt;]</c> — into a
    ///     <c>camera.position</c> command. Null (⇒ EINVAL) on a non-finite component or an unknown frame.
    /// </summary>
    /// <remarks>
    ///     The frame tail is optional: omitted, <see cref="SimCommand.Token"/> is <c>""</c> and the write
    ///     uses whatever <c>pose/frame</c> currently says — so a client animating a curve in one frame
    ///     names it once and then writes bare triples at frame rate.
    /// </remarks>
    public static SimCommand? ParsePosition(string line)
    {
        var tokens = Split(line);
        if (tokens.Length is < 3 or > 4)
            return null;

        var values = new double[PositionSlots];
        for (var i = 0; i < PositionSlots; i++)
        {
            if (ParseNumber(tokens[i]) is not { } component)
                return null;
            values[i] = component;
        }

        var frameToken = "";
        if (tokens.Length == 4)
        {
            if (!CameraRules.TryParseFrame(tokens[3], out var frame))
                return null;
            frameToken = CameraRules.NameOf(frame)!;
        }

        return new SimCommand("", PositionAction, SimCommand.NoOrdinal, 0)
        {
            Token = frameToken,
            Values = values,
        };
    }

    /// <summary>
    ///     Parses a <c>rotation</c> line — four space-separated reals <c>x y z w</c> — into a
    ///     <c>camera.rotation</c> command. Null (⇒ EINVAL) on the wrong arity, a non-finite component,
    ///     or a norm outside <c>[0.5, 2]</c>.
    /// </summary>
    /// <remarks>
    ///     Wire-identical to an ordinary 4-arity vector control (four reals in, <c>Values</c> out); it
    ///     has its own parser purely so <see cref="CameraRules.IsUnitQuaternionish"/> runs on the write
    ///     path. A zero quaternion names no rotation, and silently normalising it to identity would
    ///     point the camera somewhere the author never asked for.
    /// </remarks>
    public static SimCommand? ParseRotation(string line)
    {
        var tokens = Split(line);
        if (tokens.Length != 4)
            return null;

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (ParseNumber(tokens[i]) is not { } component)
                return null;
            values[i] = component;
        }

        return CameraRules.IsUnitQuaternionish(values)
            ? new SimCommand("", RotationAction, SimCommand.NoOrdinal, 0) { Values = values }
            : null;
    }

    /// <summary>
    ///     Parses a <c>play</c> line — <c>&lt;track&gt; [at &lt;sec&gt;] [rate &lt;x&gt;] [loop 0|1]
    ///     [group &lt;token&gt;]</c> — into a <c>camera.play</c> command. Null (⇒ EINVAL) on a bad track
    ///     name, a duplicate or unknown keyword, or a bad value.
    /// </summary>
    /// <remarks>
    ///     <c>group</c> joins the player to a shared-clock group in <c>/sim/ctl/schedules</c>, so a dolly
    ///     move and its cue schedule pause and scrub as one take.
    /// </remarks>
    public static SimCommand? ParsePlay(string line)
    {
        var tokens = Split(line);
        if (tokens.Length == 0 || !CameraStore.IsValidName(tokens[0]))
            return null;

        var values = new double[PlaySlots];
        values[PlayRate] = 1;
        string? group = null;
        var seen = 0;
        var i = 1;
        while (i < tokens.Length)
        {
            switch (tokens[i])
            {
                case "at":
                    if (!Claim(ref seen, 0) || i + 1 >= tokens.Length
                        || ParseNumber(tokens[i + 1], min: 0) is not { } at)
                        return null;
                    values[PlayAtSeconds] = at;
                    values[PlayAtPresent] = 1;
                    break;

                case "rate":
                    if (!Claim(ref seen, 1) || i + 1 >= tokens.Length
                        || ParseNumber(tokens[i + 1], min: 0, max: MaxRate) is not { } rate)
                        return null;
                    values[PlayRate] = rate;
                    values[PlayRatePresent] = 1;
                    break;

                case "loop":
                    if (!Claim(ref seen, 2) || i + 1 >= tokens.Length || tokens[i + 1] is not ("0" or "1"))
                        return null;
                    values[PlayLoop] = tokens[i + 1] == "1" ? 1 : 0;
                    values[PlayLoopPresent] = 1;
                    break;

                case "group":
                    if (!Claim(ref seen, 3) || i + 1 >= tokens.Length || !CameraRules.IsValidId(tokens[i + 1]))
                        return null;
                    group = tokens[i + 1];
                    break;

                default:
                    return null;
            }

            i += 2;
        }

        return new SimCommand("", PlayAction, SimCommand.NoOrdinal, 0)
        {
            Token = tokens[0],
            Aux = group,
            Values = values,
        };
    }

    /// <summary>
    ///     Parses a <c>set</c> line — <c>[t &lt;sec&gt;] [rate &lt;x&gt;] [loop 0|1] [paused 0|1]</c> —
    ///     into a <c>camera.set</c> command carrying flat <c>[key, value, …]</c> pairs. At least one
    ///     adjustment is required. Null (⇒ EINVAL) on anything malformed, duplicated or unknown.
    /// </summary>
    /// <remarks>
    ///     A genuinely sparse patch — "change only the rate, leave everything else alone" — so it uses the
    ///     <c>audio.set</c> pair shape rather than a slot array with presence flags.
    /// </remarks>
    public static SimCommand? ParseSet(string line)
    {
        var tokens = Split(line);
        if (tokens.Length < 2)
            return null;

        var pairs = new List<double>(tokens.Length);
        var seen = 0;
        var i = 0;
        while (i < tokens.Length)
        {
            int key;
            double value;
            switch (tokens[i])
            {
                case "t":
                    key = SetT;
                    if (i + 1 >= tokens.Length || ParseNumber(tokens[i + 1], min: 0) is not { } t)
                        return null;
                    value = t;
                    break;

                case "rate":
                    key = SetRate;
                    if (i + 1 >= tokens.Length || ParseNumber(tokens[i + 1], min: 0, max: MaxRate) is not { } rate)
                        return null;
                    value = rate;
                    break;

                case "loop":
                    key = SetLoop;
                    if (i + 1 >= tokens.Length || tokens[i + 1] is not ("0" or "1"))
                        return null;
                    value = tokens[i + 1] == "1" ? 1 : 0;
                    break;

                case "paused":
                    key = SetPaused;
                    if (i + 1 >= tokens.Length || tokens[i + 1] is not ("0" or "1"))
                        return null;
                    value = tokens[i + 1] == "1" ? 1 : 0;
                    break;

                default:
                    return null;
            }

            if (!Claim(ref seen, key))
                return null;
            pairs.Add(key);
            pairs.Add(value);
            i += 2;
        }

        return new SimCommand("", SetAction, SimCommand.NoOrdinal, 0) { Values = pairs };
    }

    // ---- shared helpers ---------------------------------------------------------------------------------

    private static string[] Split(string line)
        => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Sets bit <paramref name="bit"/> of the duplicate-keyword mask; false when already set.</summary>
    private static bool Claim(ref int seen, int bit)
    {
        var mask = 1 << bit;
        if ((seen & mask) != 0)
            return false;
        seen |= mask;
        return true;
    }

    /// <summary>Parses a finite invariant-culture number within <c>[min, max]</c>; null otherwise.</summary>
    private static double? ParseNumber(string value, double min = double.MinValue, double max = double.MaxValue)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
           && double.IsFinite(parsed) && parsed >= min && parsed <= max
            ? parsed
            : null;
}
