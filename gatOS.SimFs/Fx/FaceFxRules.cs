using System.Globalization;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Fx;

/// <summary>
///     The game-free half of the face-FX surface (<c>/sim/debug/fx/</c>): the profile vocabulary and
///     the <c>spawn</c> line grammar. The game seam (<c>FaceFxManager</c>) implements every profile
///     named here with a hand-built KSA particle-emitter template; the canonical token list living on
///     this side is what lets the tree, the help text and the transports stay game-free.
/// </summary>
public static class FaceFxRules
{
    /// <summary>
    ///     The profile vocabulary, in display order. Each is a complete authored look on the game side:
    ///     <c>party</c> (confetti burst), <c>sparkle</c> (gold glitter), <c>danger</c> (fire flash),
    ///     <c>death</c> (a slow grey puff).
    /// </summary>
    public static readonly string[] Profiles = ["party", "sparkle", "danger", "death"];

    /// <summary>Action keys (all global addressing, Frame phase — cosmetics are solver-invisible).</summary>
    public const string SpawnAction = "debug.fx_spawn";

    /// <summary>The clear trigger's action key.</summary>
    public const string ClearAction = "debug.fx_clear";

    /// <summary>Value-slot indices for <see cref="SpawnAction"/>'s <c>Values</c> array.</summary>
    public const int SpawnScale = 0;

    /// <summary>1 when the line carried an explicit <c>offset</c>; 0 = use the face default.</summary>
    public const int SpawnHasOffset = 1;

    /// <summary>Offset x/y/z slots (assembly frame, metres).</summary>
    public const int SpawnOffX = 2, SpawnOffY = 3, SpawnOffZ = 4;

    /// <summary>Total slots in a spawn command's <c>Values</c>.</summary>
    public const int SpawnSlots = 5;

    /// <summary>Whether a token names a known profile (case-insensitive; canonical form returned).</summary>
    public static bool TryParseProfile(string? token, out string profile)
    {
        profile = "";
        if (string.IsNullOrEmpty(token))
            return false;
        foreach (var candidate in Profiles)
            if (string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                return true;
            }

        return false;
    }

    /// <summary>
    ///     Parses a <c>/sim/debug/fx/spawn</c> line —
    ///     <c>"&lt;vessel&gt; &lt;profile&gt; [scale &lt;s&gt;] [offset &lt;x&gt; &lt;y&gt; &lt;z&gt;]"</c>
    ///     (keyword groups order-independent, each at most once) — into a <see cref="SpawnAction"/>
    ///     command. Returns null (⇒ EINVAL) on any malformed token. The vessel id rides
    ///     <see cref="SimCommand.Token"/> and the canonical profile rides <see cref="SimCommand.Aux"/>;
    ///     vessel existence is the game seam's ENOENT.
    /// </summary>
    public static SimCommand? ParseSpawn(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].Length == 0)
            return null;
        if (!TryParseProfile(parts[1], out var profile))
            return null;

        var values = new double[SpawnSlots];
        values[SpawnScale] = 1.0;

        var sawScale = false;
        var sawOffset = false;
        var i = 2;
        while (i < parts.Length)
            switch (parts[i].ToLowerInvariant())
            {
                case "scale":
                    if (sawScale || i + 1 >= parts.Length
                                 || !TryFinite(parts[i + 1], out values[SpawnScale])
                                 || values[SpawnScale] <= 0)
                        return null;
                    sawScale = true;
                    i += 2;
                    break;

                case "offset":
                    if (sawOffset || i + 3 >= parts.Length
                                  || !TryFinite(parts[i + 1], out values[SpawnOffX])
                                  || !TryFinite(parts[i + 2], out values[SpawnOffY])
                                  || !TryFinite(parts[i + 3], out values[SpawnOffZ]))
                        return null;
                    values[SpawnHasOffset] = 1;
                    sawOffset = true;
                    i += 4;
                    break;

                default:
                    return null;
            }

        return new SimCommand("", SpawnAction, SimCommand.NoOrdinal, 0)
        {
            Token = parts[0],
            Aux = profile,
            Values = values,
        };
    }

    private static bool TryFinite(string s, out double value)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && double.IsFinite(value);
}
