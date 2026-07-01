using System.Buffers.Binary;
using System.IO.Compression;
using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md "tier-1 validation": the debug PNG writer must emit standard PNGs whose pixels
///     round-trip the captured BGRA bytes exactly — the proof that the capture pipeline delivers real
///     rasterized image data, independent of any Kitty encoding.
/// </summary>
[TestFixture]
public sealed class PngEncoderTests
{
    [Test]
    public void Encode_EmitsTheSignature_Ihdr_AndKnownIendCrc()
    {
        var png = PngEncoder.EncodeBgra(3, 2, Gradient(3, 2));

        Assert.That(png[..8], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

        var (type, data) = ReadChunk(png, 8, out _);
        Assert.That(type, Is.EqualTo("IHDR"));
        Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data), Is.EqualTo(3), "width");
        Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), Is.EqualTo(2), "height");
        Assert.That(data[8], Is.EqualTo(8), "bit depth");
        Assert.That(data[9], Is.EqualTo(6), "color type RGBA");
        Assert.That(data[10..13], Is.EqualTo(new byte[] { 0, 0, 0 }), "compression/filter/interlace");

        // The IEND chunk (length 0) has the well-known constant CRC AE 42 60 82 — an independent
        // check of the CRC-32 implementation, not derived from our own code.
        Assert.That(png[^12..], Is.EqualTo(new byte[]
        {
            0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82,
        }));
    }

    [Test]
    public void Encode_PixelsRoundTrip_BgraToRgba()
    {
        const int w = 5;
        const int h = 4;
        var bgra = Gradient(w, h);
        var png = PngEncoder.EncodeBgra(w, h, bgra);

        var raw = InflateIdat(png);
        Assert.That(raw.Length, Is.EqualTo(h * (1 + w * 4)));

        for (var y = 0; y < h; y++)
        {
            var row = y * (1 + w * 4);
            Assert.That(raw[row], Is.EqualTo(0), $"row {y} filter byte");
            for (var x = 0; x < w; x++)
            {
                var s = (y * w + x) * 4;
                var d = row + 1 + x * 4;
                Assert.That(raw[d], Is.EqualTo(bgra[s + 2]), $"R at {x},{y}");
                Assert.That(raw[d + 1], Is.EqualTo(bgra[s + 1]), $"G at {x},{y}");
                Assert.That(raw[d + 2], Is.EqualTo(bgra[s]), $"B at {x},{y}");
                Assert.That(raw[d + 3], Is.EqualTo(bgra[s + 3]), $"A at {x},{y}");
            }
        }
    }

    [Test]
    public void Encode_RejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.EncodeBgra(0, 1, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.EncodeBgra(1, 0, new byte[4]));
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeBgra(2, 2, new byte[15]));
    }

    /// <summary>A deterministic BGRA test card where every byte differs by position and channel.</summary>
    private static byte[] Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i++)
            px[i] = (byte)(i * 7 + (i % 4) * 31);
        return px;
    }

    private static (string Type, byte[] Data) ReadChunk(byte[] png, int at, out int next)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
        var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
        var data = png.AsSpan(at + 8, length).ToArray();
        next = at + 8 + length + 4; // + CRC
        return (type, data);
    }

    /// <summary>Concatenates and zlib-inflates every IDAT chunk (the raw filtered scanline stream).</summary>
    private static byte[] InflateIdat(byte[] png)
    {
        using var deflated = new MemoryStream();
        var at = 8;
        while (at < png.Length)
        {
            var (type, data) = ReadChunk(png, at, out at);
            if (type == "IDAT")
                deflated.Write(data);
            if (type == "IEND")
                break;
        }

        deflated.Position = 0;
        using var inflate = new ZLibStream(deflated, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        return raw.ToArray();
    }
}
