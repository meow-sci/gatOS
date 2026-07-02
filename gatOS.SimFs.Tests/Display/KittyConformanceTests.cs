using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md §11 tier 2 (headless half): every frame <c>KittyEncoder</c> emits must
///     survive the <b>strict</b> sequential validation in <see cref="KittyStrict"/> — the checks a
///     strict terminal would apply (exact framing, escape budget, key grammar, m= sequencing,
///     4-aligned chunk splits, padding placement) — and its pixels must round-trip exactly.
/// </summary>
[TestFixture]
public sealed class KittyConformanceTests
{
    [TestCase(DisplayEncoding.RgbaZlib)]
    [TestCase(DisplayEncoding.Rgba)]
    public void SingleChunkFrame_IsStrictlyValid_AndRoundTrips(DisplayEncoding encoding)
    {
        var bgra = Gradient(6, 4);
        var frame = KittyEncoder.EncodeFrame(6, 4, bgra, encoding);

        var decoded = KittyStrict.ValidateFrame(frame);

        Assert.That((decoded.Width, decoded.Height), Is.EqualTo((6, 4)));
        Assert.That(decoded.Zlib, Is.EqualTo(encoding == DisplayEncoding.RgbaZlib));
        Assert.That(decoded.ImageId, Is.EqualTo(KittyEncoder.VideoImageId));
        Assert.That(decoded.Rgba, Is.EqualTo(SwizzleOpaque(bgra)));
    }

    [TestCase(DisplayEncoding.RgbaZlib)]
    [TestCase(DisplayEncoding.Rgba)]
    public void StreamSizedFrame_MultiChunk_IsStrictlyValid_AndRoundTrips(DisplayEncoding encoding)
    {
        // The default stream geometry. The gradient defeats zlib enough to force multiple chunks
        // in both encodings (raw RGBA is 230 KB → ~59 chunks).
        var bgra = Gradient(320, 180);
        var frame = KittyEncoder.EncodeFrame(320, 180, bgra, encoding);

        var decoded = KittyStrict.ValidateFrame(frame);

        Assert.That(decoded.Units.Count, Is.GreaterThan(2), "expected a chunked transmit");
        Assert.That(decoded.Rgba, Is.EqualTo(SwizzleOpaque(bgra)));
    }

    [Test]
    public void EveryEscape_StaysInsideTheTerminalBudget()
    {
        // Worst case for header length: large dims + zlib key; KittyStrict enforces the ≤4096 B
        // escape budget per unit, so a pass here pins the ChunkBytes headroom.
        var bgra = Gradient(1920, 64);
        var frame = KittyEncoder.EncodeFrame(1920, 64, bgra, DisplayEncoding.RgbaZlib);

        Assert.DoesNotThrow(() => KittyStrict.ValidateFrame(frame));
    }

    /// <summary>Deterministic BGRA noise-gradient (defeats zlib, exercises every channel).</summary>
    private static byte[] Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        var seed = 12345u;
        for (var i = 0; i < px.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u; // LCG — deterministic, incompressible enough
            px[i] = (byte)(seed >> 24);
        }

        return px;
    }

    /// <summary>The reference transform: BGRA → RGBA with alpha forced opaque (what the encoder does).</summary>
    private static byte[] SwizzleOpaque(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = 255;
        }

        return rgba;
    }
}
