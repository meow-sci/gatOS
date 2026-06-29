namespace gatOS.GameMod.Game.Ksa.ThugLife;

/// <summary>
///     Static pixel pattern for the thug-life sunglasses meme, a 26×5 grid (ported from the sibling
///     <c>unscience</c> mod). Character legend:
///     <list type="bullet">
///         <item><c>'.'</c> = transparent (no quad emitted → the background shows through);</item>
///         <item><c>'#'</c> = black opaque (RGBA 0,0,0,255);</item>
///         <item><c>'W'</c> = white opaque (RGBA 255,255,255,255) — the sunflare/glare highlight.</item>
///     </list>
///     <see cref="Authored"/> reads naturally left-to-right: cols 0–11 = left lens, 12–13 = transparent
///     bridge, 14–25 = right lens; each lens is a square with stair-stepped corners and a 3-row diagonal
///     glare in its upper-left. <see cref="Rows"/> (what the texture + geometry are built from) is that
///     pattern <b>mirrored horizontally</b> — see the note on <see cref="Rows"/>.
/// </summary>
internal static class ThugLifeTexturePattern
{
    public const int Width = 26;
    public const int Height = 5;

    /// <summary>The human-readable, un-mirrored authoring of the pattern (see the class legend).</summary>
    private static readonly string[] Authored =
    [
        "##########################", // row 0 — solid lens top
        "##W#W#######..##W#W#######", // row 1 — glare row 1
        ".##W#W######..###W#W#####.", // row 2 — glare row 2 (stepped)
        "..##W#W####....###W#W###..", // row 3 — glare row 3 (more stepped)
        "...#######......#######...", // row 4 — stepped bottom
    ];

    /// <summary>
    ///     The pattern the texture <em>and</em> the per-pixel cut-out geometry are built from: the
    ///     <see cref="Authored"/> layout <b>mirrored horizontally</b> (each row reversed). The quad is
    ///     double-sided (<c>CullNone</c>) and unlit (the stock <c>UnlitMesh</c> shader samples the same
    ///     texel on either face), so the only visible effect of the anchor part's local <c>+Z</c> facing
    ///     "into" the craft is a left-right flip of the meme — i.e. it reads mirrored / "180° about Y"
    ///     from expectation by default. Pre-mirroring the source cancels that, so a fresh entry
    ///     (<c>rotation 0 0 0</c>) looks correct. Mirroring the one source array keeps the texture colors
    ///     and the cut-out geometry consistent (both are built from this array).
    /// </summary>
    public static readonly string[] Rows = MirrorHorizontally(Authored);

    private static string[] MirrorHorizontally(string[] rows)
    {
        var result = new string[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var chars = rows[i].ToCharArray();
            Array.Reverse(chars);
            result[i] = new string(chars);
        }

        return result;
    }
}
