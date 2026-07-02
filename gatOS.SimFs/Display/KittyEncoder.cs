using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace gatOS.SimFs.Display;

/// <summary>
///     Encodes a captured BGRA frame into a self-contained <b>Kitty terminal graphics protocol</b>
///     unit (STREAM_PLAN.md §4.1) — the bytes a guest program <c>cat</c>s to its SSH stdout for the
///     terminal (in-game purrTTY or any external kitty-capable emulator) to render.
/// </summary>
/// <remarks>
///     <para>The frame is wrapped so a plain <c>cat /sim/display/stream</c> renders in place without
///     disturbing the shell: <c>ESC 7</c> (save cursor) · <c>ESC [H</c> (home) · the Kitty image
///     transmit · <c>ESC 8</c> (restore cursor). <c>C=1</c> keeps the cursor from advancing
///     past the image.</para>
///     <para><b>The fixed-id "video" pattern — replace, never delete.</b> Every frame re-transmits the
///     <i>same</i> image (and placement) id with <b>no delete</b>: a kitty transmit with an existing
///     id replaces the stored image atomically at commit (the terminal frees the old data itself), so
///     <b>the previous frame stays visible while the next one loads</b>. This matters because a frame
///     unit spans several terminal render ticks at real data rates — the earlier delete-then-retransmit
///     variant left the terminal imageless at almost every tick boundary (the following frame's delete
///     lands in the same tick as the commit), rendering the stream permanently invisible in-game while
///     passing unit-atomic headless tests. One fixed id also avoids per-frame GPU texture churn (a
///     fresh id per frame makes the terminal allocate a new texture every frame).</para>
///     <para><b>Keyframes vs replace frames (PERF_IMPROVEMENT_PLAN.md P0.3).</b> Two unit forms share
///     that pattern: a <b>keyframe</b> (<c>a=T</c>, transmit+display) [re]creates the placement, and a
///     steady-state <b>replace</b> frame (<c>a=t</c>, transmit only) swaps the stored image bytes in
///     place — the placement from the last keyframe re-renders the new pixels. Re-displaying every
///     frame is not just redundant: each kitty display step allocates a cursor-tracking pin in the
///     terminal, and ghostty's placement overwrite leaks it (~one pin per frame, unbounded — perf plan
///     §2 R3). <see cref="DisplaySurface"/> chooses the form per frame: keyframe for the first frame,
///     on a new reader, on a size/encoding change, and at least once per second (so a consumer
///     attaching mid-stream sees video within ≤1 s); everything else is a replace frame.</para>
///     <para><b>Zero-allocation hot path (PERF_IMPROVEMENT_PLAN.md P2).</b> The span overload composes
///     the frame directly into a caller-supplied buffer: the payload is sliced into
///     <see cref="ChunkRawBytes"/>-byte strides (each encodes independently to exactly
///     <see cref="ChunkBytes"/> base64 chars with no interior padding, so concatenation equals
///     whole-payload base64 and the ≤4096 B escape budget holds), <see cref="Base64.EncodeToUtf8"/>
///     writes each stride in place, headers are written as ASCII spans, and the swizzle is
///     SIMD-shuffled. Scratch (the RGBA block and the deflate output) is rented from a private pool —
///     steady state allocates <b>nothing</b>. The previous implementation allocated ~67 MB of
///     transients per 1440×900 frame (a 13.8 MB base64 <i>string</i>, ~1700 substrings, a doubling
///     MemoryStream), keeping the shared game-process GC in permanent gen2/LOH storms.</para>
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

    /// <summary>
    ///     Raw payload bytes per chunk: 3000 raw bytes base64-encode to exactly <see cref="ChunkBytes"/>
    ///     chars, and 3000 % 3 == 0 means every non-final stride encodes with no padding — per-stride
    ///     encoding concatenates to the identical bytes whole-payload encoding would produce.
    /// </summary>
    private const int ChunkRawBytes = 3000;

    /// <summary>Upper bound for the first chunk's control header (`a=..,q=2,i=..,p=..,f=32,o=z,s=....,v=....,C=1,m=1`).</summary>
    private const int MaxHeaderBytes = 96;

    private const byte Esc = 0x1b;

    /// <summary>
    ///     Scratch for the swizzled RGBA block and the deflate output — rented per call, returned before
    ///     exit. A private pool because <see cref="ArrayPool{T}.Shared"/> caps its buckets at 1 MiB and
    ///     stream frames reach ~14.7 MB at the 1920×1920 clamp.
    /// </summary>
    private static readonly ArrayPool<byte> Scratch = ArrayPool<byte>.Create(1 << 26, 4);

    /// <summary>
    ///     The tight upper bound of <see cref="EncodeFrame(int,int,ReadOnlySpan{byte},DisplayEncoding,bool,int,Span{byte})"/>'s
    ///     output for a frame of this geometry/encoding — size the destination buffer with it.
    /// </summary>
    public static int GetMaxEncodedLength(int width, int height, DisplayEncoding encoding)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var n = checked(width * height * 4);
        var payload = encoding == DisplayEncoding.RgbaZlib ? ZlibBound(n) : n;
        var base64 = (payload + 2) / 3 * 4;
        var chunks = Math.Max(1, (payload + ChunkRawBytes - 1) / ChunkRawBytes);
        // ESC7 + ESC[H + ESC8 (7 B) + per chunk: ESC_G (3) + header (≤3 for continuations; the
        // first chunk's longer header is covered by the flat MaxHeaderBytes term) + ';' + ESC\.
        return checked(7 + MaxHeaderBytes + base64 + chunks * (3 + 3 + 1 + 2));
    }

    /// <summary>
    ///     Encodes a frame into a fresh exact-size array. The compatibility/test surface — the live
    ///     path uses the span overload with pooled buffers.
    /// </summary>
    /// <inheritdoc cref="EncodeFrame(int,int,ReadOnlySpan{byte},DisplayEncoding,bool,int,Span{byte})"/>
    public static byte[] EncodeFrame(int width, int height, ReadOnlySpan<byte> bgra,
        DisplayEncoding encoding, bool display = true, int imageId = VideoImageId)
    {
        var buffer = Scratch.Rent(GetMaxEncodedLength(width, height, encoding));
        try
        {
            var length = EncodeFrame(width, height, bgra, encoding, display, imageId, buffer);
            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            Scratch.Return(buffer);
        }
    }

    /// <summary>
    ///     Encodes a frame directly into <paramref name="destination"/> (sized via
    ///     <see cref="GetMaxEncodedLength"/>) and returns the bytes written. <paramref name="bgra"/> is
    ///     row-major top-to-bottom 32-bit BGRA (the KSA readback byte order); it is swizzled to RGBA,
    ///     optionally zlib-deflated, base64'd, and framed into one or more chunked Kitty APC escapes.
    ///     Steady state allocates nothing (scratch is pooled).
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="bgra">The pixel block: <paramref name="width"/>×<paramref name="height"/>×4 bytes, BGRA.</param>
    /// <param name="encoding">Whether to zlib-deflate the RGBA block.</param>
    /// <param name="display">
    ///     <c>true</c> emits a <b>keyframe</b> (<c>a=T</c>, transmit+display — [re]creates the
    ///     placement); <c>false</c> emits a steady-state <b>replace</b> frame (<c>a=t</c>, transmit
    ///     only — the existing placement re-renders the swapped bytes, no placement churn). See the
    ///     class remarks for the cadence.
    /// </param>
    /// <param name="imageId">
    ///     The single, fixed Kitty image (and placement) id reused every frame (default
    ///     <see cref="VideoImageId"/>). Each frame re-transmits this id with no delete, so the terminal
    ///     replaces one image in place at commit and keeps the previous frame visible while the next
    ///     loads (see the class remarks).
    /// </param>
    /// <param name="destination">The output buffer; must hold <see cref="GetMaxEncodedLength"/> bytes.</param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public static int EncodeFrame(int width, int height, ReadOnlySpan<byte> bgra,
        DisplayEncoding encoding, bool display, int imageId, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var expected = checked(width * height * 4);
        if (bgra.Length < expected)
            throw new ArgumentException($"frame is {bgra.Length} bytes, expected at least {expected}", nameof(bgra));
        if (destination.Length < GetMaxEncodedLength(width, height, encoding))
            throw new ArgumentException(
                $"destination is {destination.Length} bytes, needs {GetMaxEncodedLength(width, height, encoding)}",
                nameof(destination));

        var rgba = Scratch.Rent(expected);
        byte[]? deflated = null;
        try
        {
            SwizzleBgraToRgba(bgra[..expected], rgba.AsSpan(0, expected));

            ReadOnlySpan<byte> payload;
            if (encoding == DisplayEncoding.RgbaZlib)
            {
                deflated = Scratch.Rent(ZlibBound(expected));
                payload = deflated.AsSpan(0, DeflateZlib(rgba.AsSpan(0, expected), deflated));
            }
            else
            {
                payload = rgba.AsSpan(0, expected);
            }

            // No delete: re-transmitting an existing id replaces the stored image at commit (the
            // terminal frees the old data), keeping the previous frame visible while this one loads —
            // see the class remarks for why an explicit per-frame delete blanks the stream.
            return WriteFrame(destination, width, height, imageId,
                encoding == DisplayEncoding.RgbaZlib, display, payload);
        }
        finally
        {
            Scratch.Return(rgba);
            if (deflated is not null)
                Scratch.Return(deflated);
        }
    }

    /// <summary>Reorders a BGRA span into RGBA into <paramref name="rgba"/> (alpha forced opaque), SIMD-shuffled.</summary>
    private static void SwizzleBgraToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        var i = 0;
        if (Vector128.IsHardwareAccelerated && bgra.Length >= 16)
        {
            // Per 16 bytes (4 px): B,G,R,A → R,G,B,A lane reorder, then force alpha to 0xFF.
            var map = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            var alpha = Vector128.Create(0xFF000000u).AsByte();
            ref var src = ref MemoryMarshal.GetReference(bgra);
            ref var dst = ref MemoryMarshal.GetReference(rgba);
            for (; i + 16 <= bgra.Length; i += 16)
                (Vector128.Shuffle(Vector128.LoadUnsafe(ref src, (nuint)i), map) | alpha)
                    .StoreUnsafe(ref dst, (nuint)i);
        }

        for (; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];     // R ← B-position
            rgba[i + 1] = bgra[i + 1]; // G
            rgba[i + 2] = bgra[i];     // B ← R-position
            rgba[i + 3] = 255;         // opaque (the captured alpha is meaningless for a screen grab)
        }
    }

    /// <summary>
    ///     Worst-case zlib output for <paramref name="n"/> input bytes. Deflate at
    ///     <see cref="CompressionLevel.Fastest"/> can EXPAND incompressible input by up to ~12.5%
    ///     (fixed-Huffman literals are 8–9 bits) — 25% + slack keeps a comfortable margin over any
    ///     block-overhead pathology (the pool rounds buffers up anyway).
    /// </summary>
    private static int ZlibBound(int n) => checked(n + (n >> 2) + 256);

    /// <summary>Deflates into <paramref name="destination"/> (sized ≥ <see cref="ZlibBound"/>) and returns the length.</summary>
    private static int DeflateZlib(ReadOnlySpan<byte> data, byte[] destination)
    {
        using var output = new MemoryStream(destination, 0, destination.Length, writable: true);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(data);
        return (int)output.Position;
    }

    /// <summary>
    ///     Writes the complete unit: the save/home wrapper, the chunked transmit escapes (all control
    ///     keys on the first chunk; continuations carry only <c>m</c>; the final chunk sets <c>m=0</c>),
    ///     and the restore wrapper. Returns the bytes written.
    /// </summary>
    private static int WriteFrame(Span<byte> dest, int width, int height, int imageId, bool zlib,
        bool display, ReadOnlySpan<byte> payload)
    {
        var pos = 0;
        dest[pos++] = Esc;
        dest[pos++] = (byte)'7'; // save cursor
        dest[pos++] = Esc;
        dest[pos++] = (byte)'[';
        dest[pos++] = (byte)'H'; // home

        var offset = 0;
        var first = true;
        // An empty payload (impossible here — frames have pixels) would still need one m=0 escape.
        do
        {
            var take = Math.Min(ChunkRawBytes, payload.Length - offset);
            var last = offset + take >= payload.Length;

            dest[pos++] = Esc;
            dest[pos++] = (byte)'_';
            dest[pos++] = (byte)'G';
            pos += first
                ? WriteFirstHeader(dest[pos..], width, height, imageId, zlib, display, more: !last)
                : WriteContinuationHeader(dest[pos..], last);
            dest[pos++] = (byte)';';
            Base64.EncodeToUtf8(payload.Slice(offset, take), dest[pos..], out _, out var written);
            pos += written;
            dest[pos++] = Esc;
            dest[pos++] = (byte)'\\';

            offset += take;
            first = false;
        }
        while (offset < payload.Length);

        dest[pos++] = Esc;
        dest[pos++] = (byte)'8'; // restore cursor
        return pos;
    }

    private static int WriteFirstHeader(Span<byte> dest, int width, int height, int imageId, bool zlib,
        bool display, bool more)
    {
        // a=T transmit+display (keyframe) or a=t transmit-only (replace frame), q=2 suppress
        // responses, f=32 RGBA, i the fixed image id, s/v pixel dims (required for raw/zlib RGBA),
        // o=z when deflated. p (placement id) and C=1 (don't advance the cursor) belong to the
        // display step, so only keyframes carry them.
        var pos = WriteAscii(dest, 0, "a=");
        dest[pos++] = display ? (byte)'T' : (byte)'t';
        pos = WriteAscii(dest, pos, ",q=2,i=");
        pos = WriteInt(dest, pos, imageId);
        if (display)
        {
            pos = WriteAscii(dest, pos, ",p=");
            pos = WriteInt(dest, pos, imageId);
        }

        pos = WriteAscii(dest, pos, ",f=32");
        if (zlib)
            pos = WriteAscii(dest, pos, ",o=z");
        pos = WriteAscii(dest, pos, ",s=");
        pos = WriteInt(dest, pos, width);
        pos = WriteAscii(dest, pos, ",v=");
        pos = WriteInt(dest, pos, height);
        if (display)
            pos = WriteAscii(dest, pos, ",C=1");
        pos = WriteAscii(dest, pos, ",m=");
        dest[pos++] = more ? (byte)'1' : (byte)'0';
        return pos;
    }

    private static int WriteContinuationHeader(Span<byte> dest, bool last)
    {
        dest[0] = (byte)'m';
        dest[1] = (byte)'=';
        dest[2] = last ? (byte)'0' : (byte)'1';
        return 3;
    }

    private static int WriteAscii(Span<byte> dest, int pos, string ascii)
    {
        foreach (var c in ascii)
            dest[pos++] = (byte)c;
        return pos;
    }

    private static int WriteInt(Span<byte> dest, int pos, int value)
    {
        return Utf8Formatter.TryFormat(value, dest[pos..], out var written)
            ? pos + written
            : throw new ArgumentException("destination too small for an integer key");
    }
}
