namespace gatOS.SimFs.Display;

/// <summary>
///     The wire format of a captured frame inside the Kitty graphics payload (STREAM_PLAN.md S3).
///     Both variants are <c>f=32</c> (32-bit RGBA) Kitty transmissions purrTTY decodes natively
///     (<c>KittyImageDecoder</c>); they differ only in whether the pixel block is zlib-compressed.
/// </summary>
public enum DisplayEncoding
{
    /// <summary>Raw RGBA, zlib-deflated (<c>f=32,o=z</c>). The default — small wire size, cheap CPU.</summary>
    RgbaZlib,

    /// <summary>Raw uncompressed RGBA (<c>f=32</c>). A dependency-free fallback if zlib decode misbehaves.</summary>
    Rgba,
}

/// <summary>Parsing and rendering of the <c>/sim/display/encoding</c> token.</summary>
public static class DisplayEncodings
{
    /// <summary>The canonical token for an encoding (the value <c>/sim/display/encoding</c> reads back).</summary>
    public static string Token(this DisplayEncoding encoding) => encoding switch
    {
        DisplayEncoding.RgbaZlib => "rgba-zlib",
        DisplayEncoding.Rgba => "rgba",
        _ => "rgba-zlib",
    };

    /// <summary>Parses a token (case-insensitive); null when it names no known encoding.</summary>
    public static DisplayEncoding? Parse(string token) => token.Trim().ToLowerInvariant() switch
    {
        "rgba-zlib" or "rgba_zlib" or "zlib" => DisplayEncoding.RgbaZlib,
        "rgba" or "raw" => DisplayEncoding.Rgba,
        _ => null,
    };
}
