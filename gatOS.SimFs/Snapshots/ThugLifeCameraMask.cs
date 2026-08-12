namespace gatOS.SimFs.Snapshots;

/// <summary>
///     The camera-mask vocabulary for a thug-life quad's <c>cameras</c> leaf: which of the game's
///     per-frame render passes the quad is recorded into. One quad entry is drawn once per pass, so
///     "both" is a filter, never a second quad.
/// </summary>
/// <remarks>
///     Bits, not an enum-of-choices, because the passes are independent: <c>main</c> is the player's
///     main view, <c>crew</c> is the two 128² crew-portrait viewports (the kitten face cams), and
///     <c>other</c> is any additional visible viewport (the secondary camera windows). The default is
///     <see cref="All"/> — a quad shows everywhere, which is also the pre-mask behaviour.
/// </remarks>
public static class ThugLifeCameraMask
{
    /// <summary>The player's main viewport.</summary>
    public const int Main = 1;

    /// <summary>The crew-portrait (kitten face cam) viewports.</summary>
    public const int Crew = 2;

    /// <summary>Any other visible viewport (secondary camera windows).</summary>
    public const int Other = 4;

    /// <summary>Every pass — the default.</summary>
    public const int All = Main | Crew | Other;

    /// <summary>
    ///     Parses a <c>cameras</c> line: <c>all</c>, or one-or-more of <c>main</c>/<c>crew</c>/<c>other</c>
    ///     (space- or comma-separated, case-insensitive, duplicates tolerated). An empty selection is
    ///     rejected — hiding a quad is <c>visible 0</c>'s job, and two spellings of "off" would drift.
    /// </summary>
    public static bool TryParse(string? line, out int mask)
    {
        mask = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        foreach (var token in line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
            switch (token.ToLowerInvariant())
            {
                case "all":
                    mask |= All;
                    break;
                case "main":
                    mask |= Main;
                    break;
                case "crew":
                    mask |= Crew;
                    break;
                case "other":
                    mask |= Other;
                    break;
                default:
                    mask = 0;
                    return false;
            }

        return mask != 0;
    }

    /// <summary>The canonical read-back: <c>all</c>, or the set bits in <c>main crew other</c> order.</summary>
    public static string Format(int mask)
    {
        mask &= All;
        if (mask == All)
            return "all";
        Span<string> parts = new string[3];
        var n = 0;
        if ((mask & Main) != 0) parts[n++] = "main";
        if ((mask & Crew) != 0) parts[n++] = "crew";
        if ((mask & Other) != 0) parts[n++] = "other";
        return n == 0 ? "all" : string.Join(' ', parts[..n].ToArray());
    }
}
