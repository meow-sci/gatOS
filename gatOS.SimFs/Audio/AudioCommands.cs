using System.Globalization;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Audio;

/// <summary>
///     The <c>/sim/audio/{play,set,stop}</c> line grammars, parsed into transport-agnostic
///     <see cref="SimCommand"/>s (GATOS_CUSTOM_AUDIO_PLAN). The grammar parses fully here — in the
///     game-free SimFs layer — so a bad line fails the guest's <c>write(2)</c> with EINVAL
///     immediately and the whole grammar is unit-testable without a game; the normalized command
///     is what rides the queue to the FMOD actuator (and what <c>POST /v1/command</c> /
///     <c>gatos/command</c> callers author directly).
/// </summary>
/// <remarks>
///     Command shapes (the SPEC documents these as the <c>audio.*</c> action payloads):
///     <list type="bullet">
///         <item><c>audio.play</c> — <c>Token</c> = clip name, <c>Aux</c> = caller id (null = auto),
///             <c>Values</c> = the fixed 7-slot array
///             <c>[start_ms, end_ms, vol, loop, pan, pitch, group]</c> (defaults
///             <c>[0, 0, 1, 0, 0, 1, 0]</c>; <c>end_ms</c> 0 = play to the clip end; group
///             ordinals per <see cref="GroupName"/>).</item>
///         <item><c>audio.set</c> — <c>Token</c> = channel id or clip name, <c>Values</c> = flat
///             <c>[key, value, …]</c> pairs (keys per the <c>Set*</c> constants).</item>
///         <item><c>audio.stop</c> — <c>Token</c> = <c>all</c> | channel id | clip name.</item>
///     </list>
/// </remarks>
public static class AudioCommands
{
    /// <summary>The play action key.</summary>
    public const string PlayAction = "audio.play";

    /// <summary>The live-adjust action key.</summary>
    public const string SetAction = "audio.set";

    /// <summary>The stop action key.</summary>
    public const string StopAction = "audio.stop";

    // audio.play Values slots.
    /// <summary>Play slot 0: start offset in ms (≥ 0).</summary>
    public const int PlayStartMs = 0;

    /// <summary>Play slot 1: end position in ms (0 = play to the clip end; else &gt; start).</summary>
    public const int PlayEndMs = 1;

    /// <summary>Play slot 2: volume 0..1.</summary>
    public const int PlayVol = 2;

    /// <summary>Play slot 3: loop flag 0/1 (loops the whole clip, or the start/end range).</summary>
    public const int PlayLoop = 3;

    /// <summary>Play slot 4: stereo pan -1..1.</summary>
    public const int PlayPan = 4;

    /// <summary>Play slot 5: pitch/speed multiplier (0 &lt; pitch ≤ 100).</summary>
    public const int PlayPitch = 5;

    /// <summary>Play slot 6: channel-group ordinal (see <see cref="GroupName"/>).</summary>
    public const int PlayGroup = 6;

    /// <summary>The number of <c>audio.play</c> value slots.</summary>
    public const int PlaySlots = 7;

    // audio.set (key, value) pair keys.
    /// <summary>Set key: volume 0..1.</summary>
    public const int SetVol = 0;

    /// <summary>Set key: stereo pan -1..1.</summary>
    public const int SetPan = 1;

    /// <summary>Set key: pitch/speed multiplier (0 &lt; pitch ≤ 100).</summary>
    public const int SetPitch = 2;

    /// <summary>Set key: paused flag (1 = pause, 0 = resume).</summary>
    public const int SetPaused = 3;

    /// <summary>Set key: seek to a position in ms (≥ 0).</summary>
    public const int SetSeekMs = 4;

    /// <summary>The <c>group=</c> tokens by ordinal: 0 = sfx (default), 1 = music, 2 = ui.</summary>
    private static readonly string[] GroupNames = ["sfx", "music", "ui"];

    /// <summary>The canonical group token for an ordinal, or null when out of range.</summary>
    public static string? GroupName(int ordinal)
        => ordinal >= 0 && ordinal < GroupNames.Length ? GroupNames[ordinal] : null;

    /// <summary>The group ordinal for a token (case-insensitive), or null when unknown.</summary>
    public static int? GroupOrdinal(string token)
    {
        for (var i = 0; i < GroupNames.Length; i++)
            if (string.Equals(GroupNames[i], token, StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }

    /// <summary>
    ///     Whether a token is a usable channel target: a clip-shaped name, or an auto id
    ///     (<c>#</c> + digits). Caller-chosen <c>id=</c> values must be clip-shaped (the <c>#</c>
    ///     prefix is reserved for auto ids precisely so they can never collide).
    /// </summary>
    public static bool IsValidTarget(string token)
        => AudioStore.IsValidName(token)
           || (token.Length is > 1 and <= 64 && token[0] == '#' && token.Skip(1).All(char.IsAsciiDigit));

    /// <summary>
    ///     Parses a <c>play</c> line — <c>&lt;name&gt; [start=ms] [end=ms] [vol=0..1] [loop=0|1]
    ///     [group=sfx|music|ui] [id=token] [pan=-1..1] [pitch=mult]</c> — into an
    ///     <c>audio.play</c> command. Null (⇒ EINVAL) on any malformed or duplicate token.
    /// </summary>
    public static SimCommand? ParsePlay(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !AudioStore.IsValidName(tokens[0]))
            return null;

        var values = new double[PlaySlots];
        values[PlayVol] = 1;
        values[PlayPitch] = 1;
        string? id = null;
        var seen = 0; // duplicate-key bitmask (bit = slot; id uses bit 30)
        for (var i = 1; i < tokens.Length; i++)
        {
            if (SplitKeyValue(tokens[i]) is not var (key, value))
                return null;
            int slot;
            switch (key)
            {
                case "start":
                    slot = PlayStartMs;
                    if (ParseNumber(value, min: 0) is not { } start)
                        return null;
                    values[slot] = start;
                    break;
                case "end":
                    slot = PlayEndMs;
                    if (ParseNumber(value, min: 0) is not { } end || end <= 0)
                        return null;
                    values[slot] = end;
                    break;
                case "vol":
                    slot = PlayVol;
                    if (ParseNumber(value, min: 0, max: 1) is not { } vol)
                        return null;
                    values[slot] = vol;
                    break;
                case "loop":
                    slot = PlayLoop;
                    if (value is not ("0" or "1"))
                        return null;
                    values[slot] = value == "1" ? 1 : 0;
                    break;
                case "pan":
                    slot = PlayPan;
                    if (ParseNumber(value, min: -1, max: 1) is not { } pan)
                        return null;
                    values[slot] = pan;
                    break;
                case "pitch":
                    slot = PlayPitch;
                    if (ParseNumber(value, max: 100) is not { } pitch || pitch <= 0)
                        return null;
                    values[slot] = pitch;
                    break;
                case "group":
                    slot = PlayGroup;
                    if (GroupOrdinal(value) is not { } group)
                        return null;
                    values[slot] = group;
                    break;
                case "id":
                    slot = 30;
                    if (!AudioStore.IsValidName(value))
                        return null; // '#' is reserved for auto ids
                    id = value;
                    break;
                default:
                    return null;
            }

            if ((seen & (1 << slot)) != 0)
                return null;
            seen |= 1 << slot;
        }

        // A range must be forward: end (when given) strictly after start.
        if (values[PlayEndMs] > 0 && values[PlayEndMs] <= values[PlayStartMs])
            return null;

        return new SimCommand("", PlayAction, SimCommand.NoOrdinal, 0)
        {
            Token = tokens[0],
            Aux = id,
            Values = values,
        };
    }

    /// <summary>
    ///     Parses a <c>set</c> line — <c>&lt;id-or-name&gt; [vol=] [pan=] [pitch=] [pause=0|1]
    ///     [resume=1] [seek=ms]</c> — into an <c>audio.set</c> command carrying (key, value) pairs.
    ///     At least one adjustment is required; <c>pause</c> and <c>resume</c> are exclusive.
    ///     Null (⇒ EINVAL) on any malformed, duplicate or missing token.
    /// </summary>
    public static SimCommand? ParseSet(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !IsValidTarget(tokens[0]))
            return null;

        var pairs = new List<double>(2 * (tokens.Length - 1));
        var seen = 0;
        for (var i = 1; i < tokens.Length; i++)
        {
            if (SplitKeyValue(tokens[i]) is not var (key, value))
                return null;
            int pairKey;
            double pairValue;
            switch (key)
            {
                case "vol":
                    pairKey = SetVol;
                    if (ParseNumber(value, min: 0, max: 1) is not { } vol)
                        return null;
                    pairValue = vol;
                    break;
                case "pan":
                    pairKey = SetPan;
                    if (ParseNumber(value, min: -1, max: 1) is not { } pan)
                        return null;
                    pairValue = pan;
                    break;
                case "pitch":
                    pairKey = SetPitch;
                    if (ParseNumber(value, max: 100) is not { } pitch || pitch <= 0)
                        return null;
                    pairValue = pitch;
                    break;
                case "pause":
                    pairKey = SetPaused;
                    if (value is not ("0" or "1"))
                        return null;
                    pairValue = value == "1" ? 1 : 0;
                    break;
                case "resume":
                    pairKey = SetPaused; // resume=1 is pause=0 — one exclusive paused slot
                    if (value is not "1")
                        return null;
                    pairValue = 0;
                    break;
                case "seek":
                    pairKey = SetSeekMs;
                    if (ParseNumber(value, min: 0) is not { } seek)
                        return null;
                    pairValue = seek;
                    break;
                default:
                    return null;
            }

            if ((seen & (1 << pairKey)) != 0)
                return null;
            seen |= 1 << pairKey;
            pairs.Add(pairKey);
            pairs.Add(pairValue);
        }

        return new SimCommand("", SetAction, SimCommand.NoOrdinal, 0)
        {
            Token = tokens[0],
            Values = pairs,
        };
    }

    /// <summary>
    ///     Parses a <c>stop</c> line — <c>all</c> | <c>&lt;id-or-name&gt;</c> — into an
    ///     <c>audio.stop</c> command. Null (⇒ EINVAL) on anything else.
    /// </summary>
    public static SimCommand? ParseStop(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 1 || (tokens[0] != "all" && !IsValidTarget(tokens[0])))
            return null;
        return new SimCommand("", StopAction, SimCommand.NoOrdinal, 0) { Token = tokens[0] };
    }

    /// <summary>Splits one <c>key=value</c> token; null when it is not exactly that shape.</summary>
    private static (string Key, string Value)? SplitKeyValue(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 && eq < token.Length - 1 && token.IndexOf('=', eq + 1) < 0
            ? (token[..eq], token[(eq + 1)..])
            : null;
    }

    /// <summary>Parses a finite invariant-culture number within [min, max]; null otherwise.</summary>
    private static double? ParseNumber(string value, double min = double.MinValue, double max = double.MaxValue)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
           && double.IsFinite(parsed) && parsed >= min && parsed <= max
            ? parsed
            : null;
}
