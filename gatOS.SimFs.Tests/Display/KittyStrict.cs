using System.IO.Compression;
using System.Text;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     A <b>strict</b> offline decoder/validator for the frames <c>KittyEncoder</c> produces
///     (STREAM_PLAN.md §11, tier 2). Unlike the loose scan in <c>KittyEncoderTests.Parse</c>, this
///     consumes the byte stream <b>sequentially</b> and rejects anything a strict terminal could
///     choke on: unknown bytes between escapes, over-long escapes, missing/duplicate/misplaced
///     control keys, bad <c>m=</c> sequencing, non-4-aligned base64 chunk splits, interior padding,
///     or a pixel block whose size disagrees with <c>s×v</c>. Throws <see cref="KittyFormatException"/>
///     with a byte-offset diagnostic on the first violation.
/// </summary>
internal static class KittyStrict
{
    private const byte Esc = 0x1b;

    /// <summary>The max total escape length (<c>ESC _ G … ESC \</c>) tolerated by terminals/PTYs.</summary>
    private const int MaxEscapeBytes = 4096;

    internal sealed class KittyFormatException(string message) : Exception(message);

    /// <summary>One decoded APC unit: its control string, raw payload text, and total escape size.</summary>
    internal sealed record Unit(string Control, string Payload, int EscapeBytes)
    {
        /// <summary>The control string parsed as ordered key=value pairs.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Keys { get; } = ParseKeys(Control);

        public string? Get(string key) => Keys.FirstOrDefault(k => k.Key == key).Value;

        private static List<KeyValuePair<string, string>> ParseKeys(string control)
        {
            var keys = new List<KeyValuePair<string, string>>();
            if (control.Length == 0)
                return keys;
            foreach (var part in control.Split(','))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0 || eq == part.Length - 1)
                    throw new KittyFormatException($"malformed control key '{part}' in '{control}'");
                keys.Add(new KeyValuePair<string, string>(part[..eq], part[(eq + 1)..]));
            }

            return keys;
        }
    }

    /// <summary>A fully validated frame: the declared geometry, form, and the decoded RGBA pixels.</summary>
    /// <param name="Display"><c>true</c> for a keyframe (<c>a=T</c>), <c>false</c> for a transmit-only replace (<c>a=t</c>).</param>
    internal sealed record Frame(
        int Width, int Height, bool Zlib, bool Display, int ImageId, byte[] Rgba, IReadOnlyList<Unit> Units);

    /// <summary>
    ///     Validates one complete <c>KittyEncoder.EncodeFrame</c> unit end to end and returns the
    ///     decoded pixels. Expects exactly: <c>ESC 7</c> · <c>ESC [H</c> · 1..n transmit APCs ·
    ///     <c>ESC 8</c>, with nothing before, between, or after. The transmit is either a keyframe
    ///     (<c>a=T</c>, carries the placement keys <c>p</c>/<c>C=1</c>) or a transmit-only replace
    ///     (<c>a=t</c>, which must NOT carry them — there is no display step). Deliberately <b>no
    ///     delete APC</b>: the video pattern replaces the fixed id on re-transmit so the previous
    ///     frame stays visible while the next loads — a per-frame delete blanks the stream at real
    ///     data rates (a unit spans several terminal render ticks; see KittyEncoder remarks).
    /// </summary>
    public static Frame ValidateFrame(byte[] frame)
    {
        if (Array.IndexOf(frame, (byte)'\n') is var lf && lf >= 0)
            throw new KittyFormatException($"LF at offset {lf} — the unit must be LF-free to survive a cooked PTY");

        var pos = 0;
        Expect(frame, ref pos, [Esc, (byte)'7'], "save-cursor ESC 7");
        Expect(frame, ref pos, [Esc, (byte)'[', (byte)'H'], "home ESC [H");

        var units = new List<Unit>();
        while (pos + 1 < frame.Length && frame[pos] == Esc && frame[pos + 1] == (byte)'_')
            units.Add(ReadApc(frame, ref pos));

        Expect(frame, ref pos, [Esc, (byte)'8'], "restore-cursor ESC 8");
        if (pos != frame.Length)
            throw new KittyFormatException($"{frame.Length - pos} trailing bytes after ESC 8 (offset {pos})");
        if (units.Count < 1)
            throw new KittyFormatException("expected ≥1 transmit APC, got none");
        if (units.Any(u => u.Get("a") == "d"))
            throw new KittyFormatException(
                "the unit must not delete: a per-frame delete blanks the stream mid-transmission");

        // Unit 0: the transmit header carries every control key.
        var head = units[0];
        var display = head.Get("a") switch
        {
            "T" => true,
            "t" => false,
            _ => throw new KittyFormatException(
                $"transmit header must be a=T (keyframe) or a=t (replace), got '{head.Control}'"),
        };
        if (head.Get("q") != "2")
            throw new KittyFormatException("transmit must set q=2 (suppress responses; nothing drains them)");
        if (head.Get("f") != "32")
            throw new KittyFormatException($"pixel format must be f=32 (RGBA), got f={head.Get("f")}");
        var imageId = ParseInt(head, "i");
        if (display)
        {
            if (head.Get("C") != "1")
                throw new KittyFormatException("a keyframe must set C=1 (don't move the cursor)");
            if (ParseInt(head, "p") != imageId)
                throw new KittyFormatException(
                    $"keyframe placement id p={head.Get("p")} must match the image id i={imageId}");
        }
        else
        {
            // p and C belong to the display step; a transmit-only replace must not carry them.
            if (head.Get("p") is not null)
                throw new KittyFormatException("a=t (transmit only) must not carry a placement id (p=)");
            if (head.Get("C") is not null)
                throw new KittyFormatException("a=t (transmit only) must not carry C= (no display step)");
        }

        var zlib = head.Get("o") is { } o && (o == "z"
            ? true
            : throw new KittyFormatException($"unknown compression o={o}"));
        var width = ParseInt(head, "s");
        var height = ParseInt(head, "v");
        if (width <= 0 || height <= 0)
            throw new KittyFormatException($"bad geometry s={width},v={height}");

        // m= sequencing across the transmit chunks; continuations must carry ONLY m (kitty spec:
        // subsequent chunks' control data "must contain only the m key").
        var chunks = units;
        for (var c = 0; c < chunks.Count; c++)
        {
            var expectMore = c < chunks.Count - 1 ? "1" : "0";
            if (chunks[c].Get("m") != expectMore)
                throw new KittyFormatException(
                    $"chunk {c}/{chunks.Count} has m={chunks[c].Get("m")}, expected m={expectMore}");
            if (c > 0 && (chunks[c].Keys.Count != 1 || chunks[c].Keys[0].Key != "m"))
                throw new KittyFormatException(
                    $"continuation chunk {c} must carry only the m key, got '{chunks[c].Control}'");
        }

        // Payload rules: base64 alphabet only; '=' padding only at the very end of the final chunk;
        // every non-final chunk 4-aligned so per-chunk decoders survive the split.
        var base64 = new StringBuilder();
        for (var c = 0; c < chunks.Count; c++)
        {
            var payload = chunks[c].Payload;
            if (payload.Length == 0)
                throw new KittyFormatException($"transmit chunk {c} has an empty payload");
            var final = c == chunks.Count - 1;
            if (!final && payload.Length % 4 != 0)
                throw new KittyFormatException(
                    $"non-final chunk {c} payload length {payload.Length} is not a multiple of 4");
            for (var i = 0; i < payload.Length; i++)
            {
                var ch = payload[i];
                var isPad = ch == '=';
                var valid = ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/';
                if (!(valid || (isPad && final && i >= payload.Length - 2)))
                    throw new KittyFormatException($"chunk {c} char {i}: '{ch}' is not valid base64 here");
            }

            base64.Append(payload);
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException ex)
        {
            throw new KittyFormatException($"reassembled payload is not valid base64: {ex.Message}");
        }

        var rgba = zlib ? Inflate(data) : data;
        if (rgba.Length != width * height * 4)
            throw new KittyFormatException(
                $"pixel block is {rgba.Length} B, but s={width},v={height} demands {width * height * 4} B");

        return new Frame(width, height, zlib, display, imageId, rgba, units);
    }

    private static Unit ReadApc(byte[] frame, ref int pos)
    {
        var start = pos;
        Expect(frame, ref pos, [Esc, (byte)'_', (byte)'G'], "APC start ESC _ G");
        var bodyStart = pos;
        while (true)
        {
            if (pos + 1 >= frame.Length)
                throw new KittyFormatException($"APC at offset {start} is never terminated by ESC \\");
            if (frame[pos] == Esc)
            {
                if (frame[pos + 1] != (byte)'\\')
                    throw new KittyFormatException(
                        $"stray ESC inside APC at offset {pos} (only the ESC \\ terminator may follow)");
                break;
            }

            if (frame[pos] < 0x20 || frame[pos] > 0x7e)
                throw new KittyFormatException(
                    $"non-printable byte 0x{frame[pos]:x2} inside APC at offset {pos}");
            pos++;
        }

        var body = Encoding.ASCII.GetString(frame, bodyStart, pos - bodyStart);
        pos += 2; // ESC \
        var escapeBytes = pos - start;
        if (escapeBytes > MaxEscapeBytes)
            throw new KittyFormatException(
                $"APC at offset {start} is {escapeBytes} B — exceeds the {MaxEscapeBytes} B escape budget");

        var semi = body.IndexOf(';');
        return semi < 0
            ? new Unit(body, "", escapeBytes)
            : new Unit(body[..semi], body[(semi + 1)..], escapeBytes);
    }

    private static void Expect(byte[] frame, ref int pos, ReadOnlySpan<byte> bytes, string what)
    {
        if (pos + bytes.Length > frame.Length || !frame.AsSpan(pos, bytes.Length).SequenceEqual(bytes))
            throw new KittyFormatException($"expected {what} at offset {pos}");
        pos += bytes.Length;
    }

    private static int ParseInt(Unit unit, string key)
        => int.TryParse(unit.Get(key), out var value)
            ? value
            : throw new KittyFormatException($"missing/non-integer key {key}= in '{unit.Control}'");

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
