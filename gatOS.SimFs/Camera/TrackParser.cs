using System.Globalization;
using System.Text.Json;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Camera;

/// <summary>
///     Turns the uploaded JSON of a camera track into a validated <see cref="Track"/>
///     (plans/CAMERA_CONTROLS_PLAN.md §4.4). This is the <b>only</b> place a track is validated, and it
///     is deliberately strict: every rejection names the offending shot index, channel and key index,
///     because a track is authored in a text editor on the host and the only feedback loop the author
///     has is the message on the failed write.
/// </summary>
/// <remarks>
///     <para>
///         <b>Unknown keys are rejected, not ignored.</b> A silently-ignored <c>"durration"</c> is a
///         shot that plays for zero seconds with no explanation; a rejected one is a typo fixed in five
///         seconds. The same reasoning covers unknown enum tokens, an unknown channel name, and a
///         <c>"mode":"orbit"</c> block carrying cartesian <c>keys</c> — all EINVAL.
///     </para>
///     <para>
///         <b>Comments and trailing commas are accepted.</b> Plan §4.4 documents the format with
///         <c>//</c> annotations, and a shot list is exactly the kind of file people comment. Both are
///         skipped by the reader, so the on-disk file an author keeps under <c>/mnt</c> parses verbatim.
///     </para>
///     <para>
///         <b>The ease resolution rule.</b> A segment's ease comes from its <i>start</i> key; if the
///         start key names none, the <i>end</i> key's ease is used; failing both, the track's
///         <c>defaults.ease</c>; failing that, linear. Both spellings appear in plan §4.4's own example
///         (the position channel puts the ease on the departing key, the fov and roll channels put it
///         on the arriving one), and the rule is what makes both mean what they look like. It is folded
///         into every key here, so <see cref="TrackEvaluator"/> never looks sideways.
///     </para>
/// </remarks>
public static class TrackParser
{
    /// <summary>
    ///     The most shots one track may declare. <c>CameraLimits</c> has no shot cap — adding one would
    ///     mean a new <c>[camera]</c> config key — and this bound exists only to stop a pathological
    ///     upload from building a huge object graph inside the byte cap. A 256-shot take is already far
    ///     past anything a human authors by hand.
    /// </summary>
    public const int MaxShots = 256;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    /// <summary>
    ///     Parses and validates a track. Throws <see cref="VfsErrorException"/> with
    ///     <see cref="LinuxErrno.EINVAL"/> and a message naming the offending location on any problem.
    /// </summary>
    /// <param name="json">The committed track bytes (UTF-8).</param>
    /// <param name="limits">The caps to enforce (<see cref="CameraLimits.MaxKeys"/>, byte size, FOV bounds).</param>
    /// <returns>The validated track.</returns>
    /// <exception cref="VfsErrorException">EINVAL — the message is the diagnosis.</exception>
    public static Track Parse(byte[] json, CameraLimits limits)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(limits);

        if (json.Length == 0)
            throw Bad("", "the track is empty");
        if (json.Length > limits.MaxTrackBytes)
            throw Bad("", $"the track is {json.Length} bytes, past the "
                          + $"{limits.MaxTrackBytes}-byte per-track cap");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw Bad("", $"not valid JSON ({ex.Message})");
        }

        using (document)
        {
            return ParseRoot(document.RootElement, limits);
        }
    }

    /// <summary>
    ///     The non-throwing form, for the play-time cache: returns false and the diagnosis instead of
    ///     raising. Same rules, same messages.
    /// </summary>
    /// <param name="json">The committed track bytes (UTF-8).</param>
    /// <param name="limits">The caps to enforce.</param>
    /// <param name="track">The validated track, or null on failure.</param>
    /// <param name="error">The diagnosis, or null on success.</param>
    /// <returns>True when the track parsed.</returns>
    public static bool TryParse(byte[] json, CameraLimits limits, out Track? track, out string? error)
    {
        try
        {
            track = Parse(json, limits);
            error = null;
            return true;
        }
        catch (VfsErrorException ex)
        {
            track = null;
            error = ex.Message;
            return false;
        }
    }

    // ---- root ---------------------------------------------------------------------------------------

    private static Track ParseRoot(JsonElement root, CameraLimits limits)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Bad("", "the top level must be a JSON object");
        RejectUnknown(root, "", "loop", "defaults", "shots");

        var loop = ReadOptionalBool(root, "loop", "loop") ?? false;
        var defaults = ParseDefaults(root);

        if (!root.TryGetProperty("shots", out var shotsElement))
            throw Bad("", "no 'shots' array — a track with no shots can never drive anything");
        if (shotsElement.ValueKind != JsonValueKind.Array)
            throw Bad("shots", "must be an array");
        var count = shotsElement.GetArrayLength();
        if (count == 0)
            throw Bad("shots", "is empty — a track with no shots can never drive anything");
        if (count > MaxShots)
            throw Bad("shots", $"has {count} shots, past the {MaxShots}-shot cap");

        var shots = new Shot[count];
        var index = 0;
        foreach (var element in shotsElement.EnumerateArray())
        {
            shots[index] = ParseShot(element, index, defaults, limits);
            index++;
        }

        // Ordered and non-overlapping. Overlap is almost always an authoring slip (a duration edited
        // without moving the next shot), and "which shot wins" has no answer an author would predict —
        // so it is rejected rather than resolved by a precedence rule nobody would remember.
        for (var i = 1; i < shots.Length; i++)
        {
            if (shots[i].TSeconds < shots[i - 1].TSeconds)
                throw Bad($"shots[{i}]", $"starts at t={Num(shots[i].TSeconds)}, before shots[{i - 1}] "
                                         + $"at t={Num(shots[i - 1].TSeconds)}; shots must be listed in time order");
            if (shots[i].TSeconds < shots[i - 1].EndSeconds)
                throw Bad($"shots[{i}]", $"starts at t={Num(shots[i].TSeconds)} but shots[{i - 1}] "
                                         + $"runs to t={Num(shots[i - 1].EndSeconds)}; shots must not overlap");
        }

        return new Track(loop, defaults, shots);
    }

    private static TrackDefaults ParseDefaults(JsonElement root)
    {
        if (!root.TryGetProperty("defaults", out var element))
            return TrackDefaults.None;
        if (element.ValueKind != JsonValueKind.Object)
            throw Bad("defaults", "must be an object");
        RejectUnknown(element, "defaults", "frame", "anchor", "ease", "ease_power");

        FrameKind? frame = null;
        if (element.TryGetProperty("frame", out var frameElement))
            frame = ReadFrame(frameElement, "defaults.frame");

        TargetRef? anchor = null;
        if (element.TryGetProperty("anchor", out var anchorElement))
            anchor = ReadTarget(anchorElement, "defaults.anchor");

        return new TrackDefaults(frame, anchor, ReadEase(element, "defaults"));
    }

    // ---- shot ---------------------------------------------------------------------------------------

    private static Shot ParseShot(JsonElement element, int index, TrackDefaults defaults, CameraLimits limits)
    {
        var where = $"shots[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
            throw Bad(where, "must be an object");
        RejectUnknown(element, where,
            "name", "t", "duration", "anchor", "blend_in",
            "position", "rotation", "aim", "roll", "fov", "time");

        var name = ReadOptionalString(element, "name", $"{where}.name")
                   ?? $"shot-{index.ToString(CultureInfo.InvariantCulture)}";

        var t = ReadOptionalNumber(element, "t", $"{where}.t") ?? 0.0;
        if (t < 0)
            throw Bad($"{where}.t", $"is {Num(t)}; a shot cannot start before the track does");

        var duration = ReadOptionalNumber(element, "duration", $"{where}.duration")
                       ?? throw Bad(where, "has no 'duration'");
        if (duration <= 0)
            throw Bad($"{where}.duration", $"is {Num(duration)}; a shot must last a positive time");

        var blendIn = ReadOptionalNumber(element, "blend_in", $"{where}.blend_in") ?? 0.0;
        if (blendIn < 0)
            throw Bad($"{where}.blend_in", $"is {Num(blendIn)}; a blend cannot be negative");

        var anchor = element.TryGetProperty("anchor", out var anchorElement)
            ? ReadTarget(anchorElement, $"{where}.anchor")
            : defaults.Anchor ?? TargetRef.None;

        var position = element.TryGetProperty("position", out var positionElement)
            ? ParsePosition(positionElement, $"{where}.position", duration, defaults, limits)
            : null;

        var aim = element.TryGetProperty("aim", out var aimElement)
            ? ParseAim(aimElement, $"{where}.aim", duration, defaults, limits)
            : null;

        TrackChannel<Quat>? rotation = null;
        if (element.TryGetProperty("rotation", out var rotationElement))
        {
            // aim and rotation both fix orientation. Plan §4.4 settles the tie in aim's favour rather
            // than failing, because the two together is a coherent thing to author while iterating
            // ("keep the old rotation keys around, aim at the ship for now").
            if (aim is not null)
                ModLog.Log.Warn($"camera track: {where} declares both 'aim' and 'rotation'; "
                                + "'aim' wins and the rotation channel is ignored");
            else
                rotation = ParseChannel(rotationElement, $"{where}.rotation", duration, defaults, limits,
                    ReadQuat, CurveKind.CatmullRom, allowBezier: false);
        }

        TrackChannel<double>? roll = null;
        if (element.TryGetProperty("roll", out var rollElement))
        {
            if (aim?.Roll is not null)
                throw Bad($"{where}.roll", "roll is declared both at the shot level and inside 'aim'; "
                                           + "keep one of them");
            roll = ParseScalarChannel(rollElement, $"{where}.roll", duration, defaults, limits,
                CameraRules.IsValidRoll, "a finite number of degrees");
        }

        var fov = element.TryGetProperty("fov", out var fovElement)
            ? ParseScalarChannel(fovElement, $"{where}.fov", duration, defaults, limits,
                v => CameraRules.IsValidFov(v, limits.FovMin, limits.FovMax),
                $"in [{Num(limits.FovMin)}, {Num(limits.FovMax)}] degrees")
            : null;

        var time = element.TryGetProperty("time", out var timeElement)
            ? ParseScalarChannel(timeElement, $"{where}.time", duration, defaults, limits,
                CameraRules.IsValidTimeScale, "a finite factor ≥ 0 (0 pauses)")
            : null;

        var shot = new Shot(name, t, duration, anchor, blendIn, position, aim, rotation, roll, fov, time);
        if (shot.Channels == CameraChannelMask.None)
            throw Bad(where, "declares no channels — a shot that animates nothing would only "
                             + "occupy the timeline");
        return shot;
    }

    // ---- position ------------------------------------------------------------------------------------

    private static PositionSpec ParsePosition(
        JsonElement element, string where, double duration, TrackDefaults defaults, CameraLimits limits)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Bad(where, "must be an object");
        RejectUnknown(element, where,
            "mode", "curve", "frame", "keys", "offset", "radius", "azimuth", "elevation");

        var hasKeys = element.TryGetProperty("keys", out var keysElement);
        var hasOffset = element.TryGetProperty("offset", out var offsetElement);
        var hasRadius = element.TryGetProperty("radius", out var radiusElement);
        var hasAzimuth = element.TryGetProperty("azimuth", out var azimuthElement);
        var hasElevation = element.TryGetProperty("elevation", out var elevationElement);
        var hasOrbit = hasRadius || hasAzimuth || hasElevation;

        // The mode is inferable from what was authored; an explicit mode must agree with it, so a
        // half-converted block (orbit mode still carrying the old cartesian keys) is caught here rather
        // than played as something the author never asked for.
        var mode = element.TryGetProperty("mode", out var modeElement)
            ? ReadMode(modeElement, $"{where}.mode")
            : hasOrbit ? PositionMode.Orbit
            : hasOffset ? PositionMode.Attach
            : PositionMode.Cartesian;

        var frame = element.TryGetProperty("frame", out var frameElement)
            ? ReadFrame(frameElement, $"{where}.frame")
            : defaults.Frame ?? FrameKind.Ecl;

        switch (mode)
        {
            case PositionMode.Cartesian:
            {
                if (hasOrbit)
                    throw Bad(where, "is 'cartesian' but carries orbit channels "
                                     + "(radius/azimuth/elevation); use \"mode\": \"orbit\"");
                if (hasOffset)
                    throw Bad(where, "is 'cartesian' but carries an 'offset'; use \"mode\": \"attach\"");
                if (!hasKeys)
                    throw Bad(where, "is 'cartesian' but has no 'keys'");
                var keys = ParseChannel(keysElement, $"{where}.keys", duration, defaults, limits,
                    ReadVec3, CurveKind.Linear, allowBezier: true, curveFrom: element, curveWhere: where);
                return new PositionSpec(mode, frame, keys, Vec3.Zero, null, null, null);
            }

            case PositionMode.Attach:
            {
                if (hasKeys)
                    throw Bad(where, "is 'attach' but carries 'keys'; an attach placement is a "
                                     + "constant offset");
                if (hasOrbit)
                    throw Bad(where, "is 'attach' but carries orbit channels "
                                     + "(radius/azimuth/elevation)");
                if (!hasOffset)
                    throw Bad(where, "is 'attach' but has no 'offset'");
                return new PositionSpec(mode, frame, null, ReadVec3(offsetElement, $"{where}.offset"),
                    null, null, null);
            }

            case PositionMode.Orbit:
            default:
            {
                if (hasKeys)
                    throw Bad(where, "is 'orbit' but carries cartesian 'keys'; orbit placement uses "
                                     + "the radius/azimuth/elevation channels");
                if (hasOffset)
                    throw Bad(where, "is 'orbit' but carries an 'offset'");
                if (!hasOrbit)
                    throw Bad(where, "is 'orbit' but declares none of radius/azimuth/elevation");

                var radius = hasRadius
                    ? ParseScalarChannel(radiusElement, $"{where}.radius", duration, defaults, limits,
                        CameraRules.IsValidOrbitRadius, "a finite distance ≥ 0 metres")
                    : null;
                var azimuth = hasAzimuth
                    ? ParseScalarChannel(azimuthElement, $"{where}.azimuth", duration, defaults, limits,
                        CameraRules.IsValidOrbitAzimuth, "a finite number of degrees")
                    : null;
                var elevation = hasElevation
                    ? ParseScalarChannel(elevationElement, $"{where}.elevation", duration, defaults, limits,
                        CameraRules.IsValidOrbitElevation, "in [-90, 90] degrees")
                    : null;
                return new PositionSpec(mode, frame, null, Vec3.Zero, radius, azimuth, elevation);
            }
        }
    }

    // ---- aim -----------------------------------------------------------------------------------------

    private static AimSpec ParseAim(
        JsonElement element, string where, double duration, TrackDefaults defaults, CameraLimits limits)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Bad(where, "must be an object");
        RejectUnknown(element, where, "target", "offset", "frame", "up", "roll");

        if (!element.TryGetProperty("target", out var targetElement))
            throw Bad(where, "has no 'target'");
        var target = ReadTarget(targetElement, $"{where}.target");
        if (!target.HasTarget)
            throw Bad($"{where}.target", "is 'none'; an aim block with nothing to aim at drives nothing");

        var offset = element.TryGetProperty("offset", out var offsetElement)
            ? ReadVec3(offsetElement, $"{where}.offset")
            : Vec3.Zero;

        // bodyfixed, not the track default: an aim offset is measured on the SUBJECT, and the whole
        // point of the channel is that "+0.9 m on its own Y axis" stays its head as it walks.
        var frame = element.TryGetProperty("frame", out var frameElement)
            ? ReadFrame(frameElement, $"{where}.frame")
            : FrameKind.BodyFixed;

        var up = element.TryGetProperty("up", out var upElement)
            ? ReadAimUp(upElement, $"{where}.up")
            : AimUpKind.World;

        var roll = element.TryGetProperty("roll", out var rollElement)
            ? ParseScalarChannel(rollElement, $"{where}.roll", duration, defaults, limits,
                CameraRules.IsValidRoll, "a finite number of degrees")
            : null;

        return new AimSpec(target, offset, frame, up, roll);
    }

    // ---- channels ------------------------------------------------------------------------------------

    private static TrackChannel<double> ParseScalarChannel(
        JsonElement element, string where, double duration, TrackDefaults defaults, CameraLimits limits,
        Func<double, bool> valid, string expectation)
        => ParseChannel(element, where, duration, defaults, limits,
            (value, keyWhere) =>
            {
                var v = ReadNumber(value, keyWhere);
                return valid(v) ? v : throw Bad(keyWhere, $"is {Num(v)}; expected {expectation}");
            },
            CurveKind.Linear, allowBezier: true);

    /// <summary>
    ///     The shared channel shape: <c>{ "curve": …, "keys": [ … ] }</c>. <paramref name="curveFrom"/>
    ///     lets the position block keep its <c>curve</c> beside <c>keys</c> rather than inside it, which
    ///     is how plan §4.4 spells it.
    /// </summary>
    private static TrackChannel<TValue> ParseChannel<TValue>(
        JsonElement element, string where, double duration, TrackDefaults defaults, CameraLimits limits,
        Func<JsonElement, string, TValue> readValue, CurveKind defaultCurve, bool allowBezier,
        JsonElement? curveFrom = null, string? curveWhere = null)
        where TValue : struct
    {
        JsonElement keysElement;
        var curve = defaultCurve;
        string keysWhere;

        if (curveFrom is { } outer)
        {
            // The position block: `keys` is the array itself and `curve` sits on the parent object,
            // which is how plan §4.4 spells it.
            keysElement = element;
            keysWhere = where;
            if (outer.TryGetProperty("curve", out var outerCurve))
                curve = ReadCurve(outerCurve, $"{curveWhere}.curve", allowBezier);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            // The bare-array shorthand: "fov": [ {...}, {...} ].
            keysElement = element;
            keysWhere = where;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            RejectUnknown(element, where, "curve", "keys");
            if (element.TryGetProperty("curve", out var curveElement))
                curve = ReadCurve(curveElement, $"{where}.curve", allowBezier);
            if (!element.TryGetProperty("keys", out keysElement))
                throw Bad(where, "has no 'keys'");
            keysWhere = $"{where}.keys";
        }
        else
        {
            throw Bad(where, "must be an object with 'keys', or a bare key array");
        }

        if (keysElement.ValueKind != JsonValueKind.Array)
            throw Bad(keysWhere, "must be an array");
        var count = keysElement.GetArrayLength();
        if (count == 0)
            throw Bad(keysWhere, "is empty");
        if (count > limits.MaxKeys)
            throw Bad(keysWhere, $"has {count} keys, past the {limits.MaxKeys}-key cap");

        var keys = new RawKey<TValue>[count];
        var index = 0;
        foreach (var keyElement in keysElement.EnumerateArray())
        {
            keys[index] = ParseKey(keyElement, $"{keysWhere}[{index}]", readValue);
            index++;
        }

        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (key.TSeconds < 0 || key.TSeconds > duration)
                throw Bad($"{keysWhere}[{i}]", $"is at t={Num(key.TSeconds)}, outside the shot's "
                                               + $"[0, {Num(duration)}] window");
            if (i > 0 && key.TSeconds <= keys[i - 1].TSeconds)
                throw Bad($"{keysWhere}[{i}]", $"is at t={Num(key.TSeconds)}, not after [{i - 1}] "
                                               + $"at t={Num(keys[i - 1].TSeconds)}; key times must "
                                               + "strictly increase");
        }

        ValidateHandles(keys, keysWhere, curve);

        // Fold the ease-resolution rule in (see the type remarks) so the evaluator never looks sideways.
        var resolved = new TrackKey<TValue>[count];
        for (var i = 0; i < count; i++)
        {
            var ease = keys[i].Ease
                       ?? (i + 1 < count ? keys[i + 1].Ease : null)
                       ?? defaults.Ease
                       ?? EaseSpec.Linear;
            resolved[i] = new TrackKey<TValue>(keys[i].TSeconds, keys[i].Value, ease,
                keys[i].HandleIn, keys[i].HandleOut);
        }

        return new TrackChannel<TValue>(curve, resolved);
    }

    private static void ValidateHandles<TValue>(RawKey<TValue>[] keys, string where, CurveKind curve)
        where TValue : struct
    {
        if (curve == CurveKind.Bezier)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                if (i + 1 < keys.Length && keys[i].HandleOut is null)
                    throw Bad($"{where}[{i}]", "has no 'handle_out'; a bezier curve needs both "
                                               + "handles on every segment");
                if (i > 0 && keys[i].HandleIn is null)
                    throw Bad($"{where}[{i}]", "has no 'handle_in'; a bezier curve needs both "
                                               + "handles on every segment");
            }

            return;
        }

        for (var i = 0; i < keys.Length; i++)
            if (keys[i].HandleIn is not null || keys[i].HandleOut is not null)
                throw Bad($"{where}[{i}]", "carries a bezier handle but the curve is "
                                           + $"'{CurveToken(curve)}'");
    }

    private static RawKey<TValue> ParseKey<TValue>(
        JsonElement element, string where, Func<JsonElement, string, TValue> readValue)
        where TValue : struct
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Bad(where, "must be an object");
        RejectUnknown(element, where, "t", "v", "ease", "ease_power", "handle_in", "handle_out");

        if (!element.TryGetProperty("t", out var tElement))
            throw Bad(where, "has no 't'");
        if (!element.TryGetProperty("v", out var vElement))
            throw Bad(where, "has no 'v'");

        TValue? handleIn = element.TryGetProperty("handle_in", out var inElement)
            ? readValue(inElement, $"{where}.handle_in")
            : default(TValue?);
        TValue? handleOut = element.TryGetProperty("handle_out", out var outElement)
            ? readValue(outElement, $"{where}.handle_out")
            : default(TValue?);

        return new RawKey<TValue>(
            ReadNumber(tElement, $"{where}.t"),
            readValue(vElement, $"{where}.v"),
            ReadEase(element, where),
            handleIn,
            handleOut);
    }

    // ---- primitives ----------------------------------------------------------------------------------

    private static EaseSpec? ReadEase(JsonElement owner, string where)
    {
        var hasEase = owner.TryGetProperty("ease", out var easeElement);
        var hasPower = owner.TryGetProperty("ease_power", out var powerElement);

        if (!hasEase)
        {
            // A power with nothing to raise is always a mistake, and silently dropping it would make an
            // authored curve read as linear with no clue why.
            if (hasPower)
                throw Bad($"{where}.ease_power", "has no 'ease' to apply to");
            return null;
        }

        if (easeElement.ValueKind == JsonValueKind.Array)
        {
            if (hasPower)
                throw Bad($"{where}.ease_power", "cannot be combined with explicit bezier handles");
            if (easeElement.GetArrayLength() != 4)
                throw Bad($"{where}.ease", "must be a name or four bezier handle numbers [x1,y1,x2,y2]");
            var h = new double[4];
            var i = 0;
            foreach (var component in easeElement.EnumerateArray())
            {
                h[i] = ReadNumber(component, $"{where}.ease[{i}]");
                i++;
            }

            return EaseSpec.Cubic(h[0], h[1], h[2], h[3]);
        }

        if (easeElement.ValueKind != JsonValueKind.String)
            throw Bad($"{where}.ease", "must be a name or four bezier handle numbers [x1,y1,x2,y2]");
        if (!EaseSpec.TryParse(easeElement.GetString(), out var named))
            throw Bad($"{where}.ease", $"'{easeElement.GetString()}' is not one of "
                                       + "linear|in|out|in-out (or a [x1,y1,x2,y2] array)");
        if (!hasPower)
            return named;

        var power = ReadNumber(powerElement, $"{where}.ease_power");
        if (power is < EaseSpec.MinPower or > EaseSpec.MaxPower)
            throw Bad($"{where}.ease_power", $"is {Num(power)}; expected "
                                             + $"[{Num(EaseSpec.MinPower)}, {Num(EaseSpec.MaxPower)}]");
        return EaseSpec.Named(named.Kind, power, power);
    }

    private static double ReadNumber(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
            throw Bad(where, "must be a number");
        if (!double.IsFinite(value))
            throw Bad(where, "must be a finite number");
        return value;
    }

    private static Vec3 ReadVec3(JsonElement element, string where)
    {
        var v = ReadArray(element, where, 3);
        return new Vec3(v[0], v[1], v[2]);
    }

    private static Quat ReadQuat(JsonElement element, string where)
    {
        var v = ReadArray(element, where, 4);
        if (!CameraRules.IsUnitQuaternionish(v))
            throw Bad(where, "is not a usable rotation (its norm must be in [0.5, 2]; "
                             + "a zero quaternion names no rotation at all)");
        return new Quat(v[0], v[1], v[2], v[3]);
    }

    private static double[] ReadArray(JsonElement element, string where, int arity)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != arity)
            throw Bad(where, $"must be an array of {arity} numbers");
        var values = new double[arity];
        var i = 0;
        foreach (var component in element.EnumerateArray())
        {
            values[i] = ReadNumber(component, $"{where}[{i}]");
            i++;
        }

        return values;
    }

    private static FrameKind ReadFrame(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.String
            || !CameraRules.TryParseFrame(element.GetString(), out var frame))
            throw Bad(where, $"must be one of {string.Join('|', CameraRules.FrameTokens)}");
        return frame;
    }

    private static AimUpKind ReadAimUp(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.String
            || !CameraRules.TryParseAimUp(element.GetString(), out var up))
            throw Bad(where, $"must be one of {string.Join('|', CameraRules.AimUpTokens)}");
        return up;
    }

    private static PositionMode ReadMode(JsonElement element, string where)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString() switch
            {
                "cartesian" => PositionMode.Cartesian,
                "orbit" => PositionMode.Orbit,
                "attach" => PositionMode.Attach,
                _ => throw Bad(where, "must be one of cartesian|orbit|attach"),
            }
            : throw Bad(where, "must be one of cartesian|orbit|attach");

    private static CurveKind ReadCurve(JsonElement element, string where, bool allowBezier)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Bad(where, "must be one of step|linear|catmull-rom" + (allowBezier ? "|bezier" : ""));
        var curve = element.GetString() switch
        {
            "step" => CurveKind.Step,
            "linear" => CurveKind.Linear,
            "catmull-rom" => CurveKind.CatmullRom,
            "bezier" => CurveKind.Bezier,
            _ => throw Bad(where, "must be one of step|linear|catmull-rom" + (allowBezier ? "|bezier" : "")),
        };
        if (curve == CurveKind.Bezier && !allowBezier)
            throw Bad(where, "cannot be 'bezier' here — a rotation channel interpolates with "
                             + "slerp/squad, so use 'catmull-rom'");
        return curve;
    }

    private static TargetRef ReadTarget(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.String
            || !TargetRef.TryParse(element.GetString(), out var target))
            throw Bad(where, "must be \"vessel:<id>\", \"body:<id>\", \"part:<vessel-id>/<instance-id>\" "
                             + "or \"none\"");
        return target;
    }

    private static bool? ReadOptionalBool(JsonElement owner, string property, string where)
    {
        if (!owner.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Bad(where, "must be true or false"),
        };
    }

    private static double? ReadOptionalNumber(JsonElement owner, string property, string where)
        => owner.TryGetProperty(property, out var element) ? ReadNumber(element, where) : null;

    private static string? ReadOptionalString(JsonElement owner, string property, string where)
    {
        if (!owner.TryGetProperty(property, out var element))
            return null;
        if (element.ValueKind != JsonValueKind.String)
            throw Bad(where, "must be a string");
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? throw Bad(where, "must not be empty") : value;
    }

    private static void RejectUnknown(JsonElement element, string where, params string[] known)
    {
        foreach (var property in element.EnumerateObject())
        {
            var found = false;
            foreach (var name in known)
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

            if (!found)
                throw Bad(where.Length == 0 ? property.Name : $"{where}.{property.Name}",
                    $"is not a known key here (expected one of {string.Join(", ", known)})");
        }
    }

    private static string CurveToken(CurveKind curve) => curve switch
    {
        CurveKind.Step => "step",
        CurveKind.CatmullRom => "catmull-rom",
        CurveKind.Bezier => "bezier",
        _ => "linear",
    };

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static VfsErrorException Bad(string where, string message)
        => new(LinuxErrno.EINVAL,
            where.Length == 0 ? $"camera track: {message}" : $"camera track: {where} {message}");

    /// <summary>A key as authored, before the ease-resolution pass folds in its neighbours' eases.</summary>
    private readonly record struct RawKey<TValue>(
        double TSeconds, TValue Value, EaseSpec? Ease, TValue? HandleIn, TValue? HandleOut)
        where TValue : struct;
}
