using System.Globalization;

namespace gatOS.Paint;

/// <summary>A normalized sRGB colour used by the paint API.</summary>
public readonly record struct PaintColor(double R, double G, double B)
{
    /// <summary>The default colour assigned to a newly-created disabled rule.</summary>
    public static PaintColor Default => new(1, 0.25, 0.2);

    /// <summary>Whether every component is finite and inside the published [0,1] range.</summary>
    public bool IsValid => double.IsFinite(R) && double.IsFinite(G) && double.IsFinite(B)
        && R is >= 0 and <= 1 && G is >= 0 and <= 1 && B is >= 0 and <= 1;

    /// <summary>Formats the public three-component line representation.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{R:G9} {G:G9} {B:G9}");

    /// <summary>Creates a validated colour from a command vector.</summary>
    public static bool TryFrom(IReadOnlyList<double>? values, out PaintColor color)
    {
        color = default;
        if (values is not { Count: 3 }) return false;
        var candidate = new PaintColor(values[0], values[1], values[2]);
        if (!candidate.IsValid) return false;
        color = candidate;
        return true;
    }
}

/// <summary>How the part fragment shader combines paint with sampled albedo.</summary>
public enum PaintBlendMode
{
    /// <summary>Multiply sampled albedo by paint.</summary>
    Multiply,
    /// <summary>Recolour by sampled luminance, allowing brightening.</summary>
    Tint,
    /// <summary>Replace sampled albedo while retaining normal/PBR shading.</summary>
    Replace,
}

/// <summary>Parsing and formatting for the published blend tokens.</summary>
public static class PaintBlendModes
{
    /// <summary>Parses multiply, tint, or replace, case-insensitively.</summary>
    public static bool TryParse(string? token, out PaintBlendMode mode)
        => Enum.TryParse(token, true, out mode) && Enum.IsDefined(mode);

    /// <summary>Formats the lower-case wire token.</summary>
    public static string Format(PaintBlendMode mode) => mode.ToString().ToLowerInvariant();
}
