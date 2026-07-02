namespace gatOS.SimFs.Display;

/// <summary>
///     The wire format of a captured frame inside the Kitty graphics payload (STREAM_PLAN.md S3).
///     Both variants are <c>f=32</c> (32-bit RGBA) Kitty transmissions purrTTY decodes natively
///     (<c>KittyImageDecoder</c>); they differ only in whether the pixel block is zlib-compressed.
/// </summary>
public enum DisplayEncoding
{
    /// <summary>
    ///     Raw RGBA, zlib-deflated (<c>f=32,o=z</c>). <b>The default</b> — 3–10× smaller on the
    ///     wire (space scenes deflate hard), which is what lets large captures stream through the
    ///     slirp/SSH/PTY chain (PERF_IMPROVEMENT_PLAN.md P6). Requires purrTTY's 2026-07-02+
    ///     native: earlier pins memory-corrupted on compressible <c>o=z</c> payloads (a zig
    ///     0.15.2 std flate bug — purrtty gotcha 34, fixed by the <c>purrtty/vt-video-fixes</c>
    ///     native patch; purrTTY's <c>ZlibRealFrame_DecodesToGroundTruth</c> is the standing
    ///     regression gate).
    /// </summary>
    RgbaZlib,

    /// <summary>
    ///     Raw uncompressed RGBA (<c>f=32</c>). Selectable fallback — pixel-exact through every
    ///     consumer with zero inflate cost, at ~3–10× the wire size.
    /// </summary>
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
        _ => "rgba",
    };

    /// <summary>Parses a token (case-insensitive); null when it names no known encoding.</summary>
    public static DisplayEncoding? Parse(string token) => token.Trim().ToLowerInvariant() switch
    {
        "rgba-zlib" or "rgba_zlib" or "zlib" => DisplayEncoding.RgbaZlib,
        "rgba" or "raw" => DisplayEncoding.Rgba,
        _ => null,
    };
}
