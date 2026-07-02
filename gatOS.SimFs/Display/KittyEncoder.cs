using System.IO.Compression;
using System.Text;

namespace gatOS.SimFs.Display;

/// <summary>
///     Encodes a captured BGRA frame into a self-contained <b>Kitty terminal graphics protocol</b>
///     unit (STREAM_PLAN.md §4.1) — the bytes a guest program <c>cat</c>s to its SSH stdout for the
///     terminal (in-game purrTTY or any external kitty-capable emulator) to render.
/// </summary>
/// <remarks>
///     <para>The frame is wrapped so a plain <c>cat /sim/display/stream</c> renders in place without
///     disturbing the shell: <c>ESC 7</c> (save cursor) · <c>ESC [H</c> (home) · the Kitty image
///     transmit+display · <c>ESC 8</c> (restore cursor). <c>C=1</c> keeps the cursor from advancing
///     past the image.</para>
///     <para><b>The fixed-id "video" pattern — replace, never delete.</b> Every frame re-transmits the
///     <i>same</i> image (and placement) id with <b>no delete</b>: a kitty <c>a=T</c> with an existing
///     id replaces the stored image atomically at commit (the terminal frees the old data itself), so
///     <b>the previous frame stays visible while the next one loads</b>. This matters because a frame
///     unit spans several terminal render ticks at real data rates — the earlier delete-then-retransmit
///     variant left the terminal imageless at almost every tick boundary (the following frame's delete
///     lands in the same tick as the commit), rendering the stream permanently invisible in-game while
///     passing unit-atomic headless tests. One fixed id also avoids per-frame GPU texture churn (a
///     fresh id per frame makes the terminal allocate a new texture every frame).</para>
///     <para>The whole sequence is <b>LF-free by construction</b> (base64 has no newline, and every
///     escape is ESC-prefixed) so it survives a cooked PTY (<c>ONLCR</c>/<c>OPOST</c> cannot corrupt
///     it) with no raw-mode dance required of the consumer.</para>
/// </remarks>
public static class KittyEncoder
{
    /// <summary>The single, fixed image (and placement) id reused for every frame — see the class remarks.</summary>
    public const int VideoImageId = 1;

    /// <summary>
    ///     Max base64 chars per chunk. Kept below 4096 <b>minus the per-chunk control+framing overhead</b>
    ///     so the <i>entire</i> escape (<c>ESC _ G</c> + control + <c>;</c> + payload + <c>ESC \</c>) stays
    ///     under 4096 bytes. Terminals/PTYs (libghostty, the guest's SSH PTY) cap escape-sequence length
    ///     around 4096; a full 4096-char payload pushes the escape to ~4146 bytes, which gets truncated in
    ///     transit and corrupts the base64 (the consumer reports <c>Base64Invalid</c>). 4000 leaves ample
    ///     room for the first chunk's longer header (e.g. <c>s=1920,v=1080</c>).
    /// </summary>
    private const int ChunkBytes = 4000;

    private const byte Esc = 0x1b;

    // ESC 7  /  ESC [H  /  ESC 8  — save cursor, home, restore cursor (all LF-free).
    private static readonly byte[] SaveCursor = [Esc, (byte)'7'];
    private static readonly byte[] HomeCursor = [Esc, (byte)'[', (byte)'H'];
    private static readonly byte[] RestoreCursor = [Esc, (byte)'8'];
    private static readonly byte[] ApcStart = [Esc, (byte)'_', (byte)'G'];
    private static readonly byte[] ApcEnd = [Esc, (byte)'\\'];

    /// <summary>
    ///     Encodes a frame. <paramref name="bgra"/> is row-major top-to-bottom 32-bit BGRA (the KSA
    ///     swapchain/offscreen byte order); it is swizzled to RGBA, optionally zlib-deflated, base64'd,
    ///     and framed into one or more chunked Kitty APC escapes.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="bgra">The pixel block: <paramref name="width"/>×<paramref name="height"/>×4 bytes, BGRA.</param>
    /// <param name="encoding">Whether to zlib-deflate the RGBA block.</param>
    /// <param name="imageId">
    ///     The single, fixed Kitty image (and placement) id reused every frame (default
    ///     <see cref="VideoImageId"/>). Each frame re-transmits this id with no delete, so the terminal
    ///     replaces one image in place at commit and keeps the previous frame visible while the next
    ///     loads (see the class remarks).
    /// </param>
    /// <returns>The complete, self-contained frame bytes ready to write to a terminal.</returns>
    public static byte[] EncodeFrame(int width, int height, ReadOnlySpan<byte> bgra,
        DisplayEncoding encoding, int imageId = VideoImageId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var expected = checked(width * height * 4);
        if (bgra.Length < expected)
            throw new ArgumentException($"frame is {bgra.Length} bytes, expected at least {expected}", nameof(bgra));

        var rgba = new byte[expected];
        SwizzleBgraToRgba(bgra[..expected], rgba);

        var payload = encoding == DisplayEncoding.RgbaZlib ? ZlibDeflate(rgba) : rgba;
        var base64 = Convert.ToBase64String(payload);

        using var output = new MemoryStream(base64.Length + 256);
        output.Write(SaveCursor);
        output.Write(HomeCursor);
        // No delete: re-transmitting an existing id replaces the stored image at commit (the terminal
        // frees the old data), keeping the previous frame visible while this one loads — see the class
        // remarks for why an explicit per-frame delete blanks the stream at real data rates.
        WriteKittyImage(output, width, height, imageId, encoding == DisplayEncoding.RgbaZlib, base64);
        output.Write(RestoreCursor);
        return output.ToArray();
    }

    /// <summary>Reorders a BGRA span into RGBA in place into <paramref name="rgba"/> (alpha forced opaque).</summary>
    private static void SwizzleBgraToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];     // R ← B-position
            rgba[i + 1] = bgra[i + 1]; // G
            rgba[i + 2] = bgra[i];     // B ← R-position
            rgba[i + 3] = 255;         // opaque (the captured alpha is meaningless for a screen grab)
        }
    }

    private static byte[] ZlibDeflate(ReadOnlySpan<byte> data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(data);
        return compressed.ToArray();
    }

    /// <summary>
    ///     Writes the chunked Kitty graphics transmit-and-display escapes. All control keys ride on the
    ///     first chunk; continuation chunks carry only <c>m</c>; the final chunk sets <c>m=0</c>.
    /// </summary>
    private static void WriteKittyImage(Stream output, int width, int height, int imageId, bool zlib, string base64)
    {
        var offset = 0;
        var first = true;
        // An empty payload (impossible here — frames have pixels) would still need one m=0 escape.
        do
        {
            var take = Math.Min(ChunkBytes, base64.Length - offset);
            var last = offset + take >= base64.Length;

            output.Write(ApcStart);
            var header = first
                ? BuildFirstHeader(width, height, imageId, zlib, more: !last)
                : $"m={(last ? 0 : 1)}";
            output.Write(Encoding.ASCII.GetBytes(header));
            output.WriteByte((byte)';');
            output.Write(Encoding.ASCII.GetBytes(base64.Substring(offset, take)));
            output.Write(ApcEnd);

            offset += take;
            first = false;
        }
        while (offset < base64.Length);
    }

    private static string BuildFirstHeader(int width, int height, int imageId, bool zlib, bool more)
    {
        // a=T transmit+display, q=2 suppress responses, f=32 RGBA, i/p fixed ids (replace each frame),
        // s/v pixel dims (required for raw/zlib RGBA), C=1 don't advance the cursor, o=z when deflated.
        var sb = new StringBuilder(64);
        sb.Append("a=T,q=2,i=").Append(imageId).Append(",p=").Append(imageId).Append(",f=32");
        if (zlib)
            sb.Append(",o=z");
        sb.Append(",s=").Append(width).Append(",v=").Append(height).Append(",C=1");
        sb.Append(",m=").Append(more ? 1 : 0);
        return sb.ToString();
    }
}
