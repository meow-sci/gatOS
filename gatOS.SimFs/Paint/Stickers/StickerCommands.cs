using System.Globalization;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Paint.Stickers;

/// <summary>
///     The <c>/sim/paint/stickers</c> line grammars (STICKERS_PLAN §3.6): <c>place</c> (exact,
///     scriptable) and <c>spray</c> (aimed at whatever the camera or cursor is pointing at).
/// </summary>
/// <remarks>
///     <para>
///         Both parse fully here — in the game-free SimFs layer — so a bad line fails the guest's
///         <c>write(2)</c> with EINVAL immediately and the whole grammar is unit-testable without a
///         game; the normalized command is what rides the queue to the game-side registry (and what
///         <c>POST /v1/command</c> / <c>gatos/command</c> callers author directly).
///     </para>
///     <para>
///         Shapes (<c>VesselId</c> is always empty — stickers are registry-keyed, not per-vessel):
///         <list type="bullet">
///             <item><c>place</c>: <c>Token</c> = image, <c>Aux</c> = <c>"vessel &lt;id&gt; &lt;iid&gt;"</c>
///             or <c>"body &lt;id&gt;"</c>, <c>Values</c> = 12 doubles
///             <c>[p0 p1 p2, n0 n1 n2, rot, w, h, d, alpha, brightness]</c>. For a body anchor
///             <c>p</c> is <c>(lat, lon, 0)</c>, <c>n</c> is zero and <c>rot</c> is the heading.</item>
///             <item><c>spray</c>: <c>Token</c> = image, <c>Aux</c> = <c>camera</c>|<c>cursor</c>,
///             <c>Values</c> = 7 doubles <c>[range, roll, w, h, d, alpha, brightness]</c>. <c>d</c> is
///             the sentinel <c>-1</c> when the caller did not pass <c>d=</c>, so the game side can
///             substitute the anchor-kind default once the ray tells it what it hit.</item>
///         </list>
///     </para>
/// </remarks>
public static class StickerCommands
{
    /// <summary>The exact-placement action key.</summary>
    public const string PlaceAction = SimActions.PaintStickerPlace;

    /// <summary>The aimed-placement action key.</summary>
    public const string SprayAction = SimActions.PaintStickerSpray;

    /// <summary>The <c>place</c> anchor keyword for a part-local placement.</summary>
    public const string VesselAnchor = "vessel";

    /// <summary>The <c>place</c> anchor keyword for a geodetic placement.</summary>
    public const string BodyAnchor = "body";

    /// <summary>The <c>spray</c> depth slot's "the caller said nothing" sentinel.</summary>
    public const double DepthUnset = -1;

    // Values slots. place: rotation..brightness are the optional tail; spray has its own layout.
    private const int PlaceRotation = 6;
    private const int PlaceWidth = 7;
    private const int PlaceHeight = 8;
    private const int PlaceDepth = 9;
    private const int PlaceAlpha = 10;
    private const int PlaceBrightness = 11;
    private const int SprayRange = 0;
    private const int SprayRotation = 1;
    private const int SprayWidth = 2;
    private const int SprayHeight = 3;
    private const int SprayDepth = 4;
    private const int SprayAlpha = 5;
    private const int SprayBrightness = 6;
    private const int AimSeenBit = 12; // outside every value slot, so it cannot collide

    /// <summary>
    ///     Parses a <c>place</c> line — <c>&lt;image&gt; vessel &lt;vessel_id&gt; &lt;part_iid&gt; x y z
    ///     nx ny nz [roll=|w=|h=|d=|alpha=|brightness=]</c> or <c>&lt;image&gt; body &lt;body_id&gt;
    ///     &lt;lat&gt; &lt;lon&gt; [heading=|w=|h=|d=|alpha=|brightness=]</c>. Returns null (⇒ EINVAL)
    ///     on any malformed, duplicate, unknown or out-of-range token.
    /// </summary>
    public static SimCommand? ParsePlace(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3 || !StickerRules.IsValidImage(tokens[0]) || !StickerRules.IsValidTarget(tokens[2]))
            return null;
        return tokens[1] switch
        {
            VesselAnchor => ParseVesselPlace(tokens),
            BodyAnchor => ParseBodyPlace(tokens),
            _ => null,
        };
    }

    /// <summary>
    ///     Parses a <c>spray</c> line — <c>&lt;image&gt; [aim=camera|cursor] [range=] [roll=] [w=] [h=]
    ///     [d=] [alpha=] [brightness=]</c>. Returns null (⇒ EINVAL) on any malformed, duplicate,
    ///     unknown or out-of-range token.
    /// </summary>
    public static SimCommand? ParseSpray(string line)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 1 || !StickerRules.IsValidImage(tokens[0]))
            return null;

        var values = new double[7];
        values[SprayRange] = StickerRules.DefaultRange;
        values[SprayRotation] = StickerRules.DefaultRotation;
        values[SprayWidth] = StickerRules.DefaultWidth;
        values[SprayHeight] = StickerRules.DefaultHeight;
        values[SprayDepth] = DepthUnset; // "the caller said nothing" — the game picks the kind default
        values[SprayAlpha] = StickerRules.DefaultAlpha;
        values[SprayBrightness] = StickerRules.DefaultBrightness;

        var seen = 0;
        var cursor = false;
        for (var i = 1; i < tokens.Length; i++)
        {
            if (!SplitOption(tokens[i], out var key, out var raw))
                return null;
            var ok = key switch
            {
                "aim" => Mark(ref seen, AimSeenBit) && StickerRules.TryParseAim(raw, out cursor),
                "range" => TryAssign(raw, values, SprayRange, StickerRules.IsValidRange, ref seen),
                "roll" => TryAssign(raw, values, SprayRotation, StickerRules.IsValidRotation, ref seen),
                "w" => TryAssign(raw, values, SprayWidth, StickerRules.IsValidWidth, ref seen),
                "h" => TryAssign(raw, values, SprayHeight, StickerRules.IsValidHeight, ref seen),
                "d" => TryAssign(raw, values, SprayDepth, StickerRules.IsValidDepth, ref seen),
                "alpha" => TryAssign(raw, values, SprayAlpha, StickerRules.IsValidAlpha, ref seen),
                "brightness" => TryAssign(raw, values, SprayBrightness, StickerRules.IsValidBrightness, ref seen),
                _ => false,
            };
            if (!ok)
                return null;
        }

        return new SimCommand("", SprayAction, SimCommand.NoOrdinal, 0)
        {
            Token = tokens[0],
            Aux = StickerRules.FormatAim(cursor),
            Values = values,
        };
    }

    /// <summary>
    ///     The canonical sticker spec line — the read-back of
    ///     <c>/sim/paint/stickers/&lt;id&gt;/spec</c> and exactly the form <c>place</c> accepts, so a
    ///     read can be echoed straight back to recreate the sticker (as a new id).
    /// </summary>
    public static string FormatSpec(StickerSnapshot s)
        => s.Kind == StickerAnchorKind.Body
            ? $"{s.Image} {BodyAnchor} {s.TargetId} "
              + $"{Formats.Scalar(s.Position.X)} {Formats.Scalar(s.Position.Y)} "
              + $"heading={Formats.Scalar(s.RotationDeg)} {Tail(s)}"
            : $"{s.Image} {VesselAnchor} {s.TargetId} {Formats.UInt(s.PartInstanceId)} "
              + $"{Formats.Vector(s.Position)} {Formats.Vector(s.Normal)} "
              + $"roll={Formats.Scalar(s.RotationDeg)} {Tail(s)}";

    private static string Tail(StickerSnapshot s)
        => $"w={Formats.Scalar(s.Width)} h={Formats.Scalar(s.Height)} d={Formats.Scalar(s.Depth)} "
           + $"alpha={Formats.Scalar(s.Alpha)} brightness={Formats.Scalar(s.Brightness)}";

    private static SimCommand? ParseVesselPlace(string[] tokens)
    {
        // <image> vessel <vessel_id> <part_iid> x y z nx ny nz  — 10 positional tokens.
        if (tokens.Length < 10
            || !uint.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var partIid))
            return null;

        var values = new double[12];
        for (var i = 0; i < 6; i++)
            if (!double.TryParse(tokens[4 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !StickerRules.IsValidPosition(values[i]))
                return null;
        if (!StickerRules.IsValidNormal(values[3], values[4], values[5]))
            return null;

        FillPlaceDefaults(values, StickerRules.DefaultDepthVessel);
        if (!TryPlaceOptions(tokens, 10, "roll", values))
            return null;

        return new SimCommand("", PlaceAction, SimCommand.NoOrdinal, 0)
        {
            Token = tokens[0],
            Aux = $"{VesselAnchor} {tokens[2]} {partIid.ToString(CultureInfo.InvariantCulture)}",
            Values = values,
        };
    }

    private static SimCommand? ParseBodyPlace(string[] tokens)
    {
        // <image> body <body_id> <lat> <lon>  — 5 positional tokens; normal and p2 stay zero.
        if (tokens.Length < 5)
            return null;
        if (!double.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !StickerRules.IsValidLatitude(lat)
            || !double.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || !StickerRules.IsValidLongitude(lon))
            return null;

        var values = new double[12];
        values[0] = lat;
        values[1] = lon;
        FillPlaceDefaults(values, StickerRules.DefaultDepthBody);
        if (!TryPlaceOptions(tokens, 5, "heading", values))
            return null;

        return new SimCommand("", PlaceAction, SimCommand.NoOrdinal, 0)
        {
            Token = tokens[0],
            Aux = $"{BodyAnchor} {tokens[2]}",
            Values = values,
        };
    }

    private static void FillPlaceDefaults(double[] values, double depth)
    {
        values[PlaceRotation] = StickerRules.DefaultRotation;
        values[PlaceWidth] = StickerRules.DefaultWidth;
        values[PlaceHeight] = StickerRules.DefaultHeight;
        values[PlaceDepth] = depth;
        values[PlaceAlpha] = StickerRules.DefaultAlpha;
        values[PlaceBrightness] = StickerRules.DefaultBrightness;
    }

    /// <summary>
    ///     Applies the trailing <c>key=value</c> tokens of a <c>place</c> line. The rotation key is
    ///     anchor-specific (<c>roll</c> for a vessel, <c>heading</c> for a body) so neither spelling
    ///     can be used against the wrong anchor.
    /// </summary>
    private static bool TryPlaceOptions(string[] tokens, int start, string rotationKey, double[] values)
    {
        var seen = 0;
        for (var i = start; i < tokens.Length; i++)
        {
            if (!SplitOption(tokens[i], out var key, out var raw))
                return false;
            if (key == rotationKey)
            {
                if (!TryAssign(raw, values, PlaceRotation, StickerRules.IsValidRotation, ref seen))
                    return false;
                continue;
            }

            var ok = key switch
            {
                "w" => TryAssign(raw, values, PlaceWidth, StickerRules.IsValidWidth, ref seen),
                "h" => TryAssign(raw, values, PlaceHeight, StickerRules.IsValidHeight, ref seen),
                "d" => TryAssign(raw, values, PlaceDepth, StickerRules.IsValidDepth, ref seen),
                "alpha" => TryAssign(raw, values, PlaceAlpha, StickerRules.IsValidAlpha, ref seen),
                "brightness" => TryAssign(raw, values, PlaceBrightness, StickerRules.IsValidBrightness, ref seen),
                _ => false,
            };
            if (!ok)
                return false;
        }

        return true;
    }

    /// <summary>Splits a <c>key=value</c> token; false (⇒ EINVAL) when it is not one.</summary>
    private static bool SplitOption(string token, out string key, out string value)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0 || eq == token.Length - 1)
        {
            key = "";
            value = "";
            return false;
        }

        key = token[..eq];
        value = token[(eq + 1)..];
        return true;
    }

    /// <summary>Parses, validates and stores one option; false on a duplicate key or a bad value.</summary>
    private static bool TryAssign(string raw, double[] values, int slot, Func<double, bool> valid, ref int seen)
        => Mark(ref seen, slot)
           && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && valid(value)
           && Set(values, slot, value);

    /// <summary>Records that a key was seen; false when it was already given (duplicates ⇒ EINVAL).</summary>
    private static bool Mark(ref int seen, int bit)
    {
        var mask = 1 << bit;
        if ((seen & mask) != 0)
            return false;
        seen |= mask;
        return true;
    }

    private static bool Set(double[] values, int slot, double value)
    {
        values[slot] = value;
        return true;
    }
}
