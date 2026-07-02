using System.IO.Compression;
using System.Text;
using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md S3: the Kitty graphics encoder. A frame must be a self-contained,
///     in-place, LF-free Kitty unit whose pixels survive a base64(+zlib) round-trip with the
///     BGRA→RGBA swizzle applied.
/// </summary>
[TestFixture]
public sealed class KittyEncoderTests
{
    private const byte Esc = 0x1b;

    [Test]
    public void EncodeFrame_WrapsInSaveHomeRestore_AndIsLfFree()
    {
        var frame = KittyEncoder.EncodeFrame(2, 2, SolidBgra(2, 2, 10, 20, 30),
            DisplayEncoding.RgbaZlib, imageId: 1);

        // ESC 7 (save) … ESC 8 (restore) bracket; ESC [H home sits right after the save.
        Assert.That(frame[0], Is.EqualTo(Esc));
        Assert.That(frame[1], Is.EqualTo((byte)'7'));
        Assert.That(frame[2], Is.EqualTo(Esc));
        Assert.That(frame[3], Is.EqualTo((byte)'['));
        Assert.That(frame[4], Is.EqualTo((byte)'H'));
        Assert.That(frame[^2], Is.EqualTo(Esc));
        Assert.That(frame[^1], Is.EqualTo((byte)'8'));

        // LF-free by construction (so a cooked PTY's ONLCR cannot corrupt it).
        Assert.That(Array.IndexOf(frame, (byte)'\n'), Is.EqualTo(-1));
    }

    [Test]
    public void EncodeFrame_FirstHeader_CarriesTheControlKeys()
    {
        var frame = KittyEncoder.EncodeFrame(4, 3, SolidBgra(4, 3, 1, 2, 3),
            DisplayEncoding.RgbaZlib, imageId: 7);
        var (headers, _) = Parse(frame);

        Assert.That(headers, Is.Not.Empty);
        var first = headers.First(h => h.Contains("a=T")); // the transmit header
        Assert.That(first, Does.Contain("a=T"));        // transmit + display
        Assert.That(first, Does.Contain("f=32"));       // 32-bit RGBA
        Assert.That(first, Does.Contain("o=z"));        // zlib
        Assert.That(first, Does.Contain("i=7"));        // image id
        Assert.That(first, Does.Contain("p=7"));        // placement id (replace each frame)
        Assert.That(first, Does.Contain("s=4"));        // width px
        Assert.That(first, Does.Contain("v=3"));        // height px
        Assert.That(first, Does.Contain("C=1"));        // do not advance the cursor
        Assert.That(headers[^1], Does.Contain("m=0"));  // final chunk
    }

    [Test]
    public void EncodeFrame_Rgba_OmitsZlibKey_AndRoundTripsPixels()
    {
        // A 2×2 frame with four distinct BGRA pixels; verify the exact swizzle to RGBA.
        var bgra = new byte[]
        {
            10, 20, 30, 99, /* px0 BGRA */ 40, 50, 60, 99,
            70, 80, 90, 99, /* px2      */ 100, 110, 120, 99,
        };
        var frame = KittyEncoder.EncodeFrame(2, 2, bgra, DisplayEncoding.Rgba, imageId: 1);
        var (headers, payload) = Parse(frame);

        Assert.That(headers[0], Does.Not.Contain("o=z"));
        // Expected RGBA: R=B-pos, G, B=R-pos, A=255.
        var expected = new byte[]
        {
            30, 20, 10, 255, 60, 50, 40, 255,
            90, 80, 70, 255, 120, 110, 100, 255,
        };
        Assert.That(payload, Is.EqualTo(expected));
    }

    [Test]
    public void EncodeFrame_Zlib_RoundTripsPixels()
    {
        var bgra = SolidBgra(8, 8, 12, 34, 56);
        var frame = KittyEncoder.EncodeFrame(8, 8, bgra, DisplayEncoding.RgbaZlib, imageId: 1);
        var (headers, payload) = Parse(frame);

        var rgba = headers.Any(h => h.Contains("o=z")) ? Inflate(payload) : payload;
        Assert.That(rgba.Length, Is.EqualTo(8 * 8 * 4));
        // Every pixel is the swizzled solid color, opaque.
        for (var i = 0; i < rgba.Length; i += 4)
        {
            Assert.That(rgba[i], Is.EqualTo(56));      // R ← B
            Assert.That(rgba[i + 1], Is.EqualTo(34));  // G
            Assert.That(rgba[i + 2], Is.EqualTo(12));  // B ← R
            Assert.That(rgba[i + 3], Is.EqualTo(255)); // A
        }
    }

    [Test]
    public void EncodeFrame_LargePayload_SplitsIntoChunks_FirstMore_LastFinal()
    {
        // 64×16 uncompressed RGBA = 4096 bytes → base64 ≈ 5464 chars → two ≤4096-byte chunks.
        var frame = KittyEncoder.EncodeFrame(64, 16, SolidBgra(64, 16, 1, 1, 1),
            DisplayEncoding.Rgba, imageId: 1);
        var (headers, _) = Parse(frame);

        var chunks = headers.Where(h => h.Contains("m=")).ToList();
        Assert.That(chunks, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(chunks[0], Does.Contain("m=1"));
        Assert.That(chunks[^1], Does.Contain("m=0"));
        Assert.That(chunks.Take(chunks.Count - 1), Has.All.Contain("m=1"));
    }

    [Test]
    public void EncodeFrame_RejectsTooSmallABuffer()
        => Assert.Throws<ArgumentException>(() =>
            KittyEncoder.EncodeFrame(4, 4, new byte[10], DisplayEncoding.Rgba, 1));

    [Test]
    public void EncodeFrame_NeverDeletes_ReplaceOnRetransmitKeepsTheOldFrameVisible()
    {
        var frame = KittyEncoder.EncodeFrame(2, 2, SolidBgra(2, 2, 1, 1, 1), DisplayEncoding.Rgba, imageId: 7);
        var text = Encoding.ASCII.GetString(frame);

        // A frame unit spans several terminal render ticks at real data rates, so a per-frame
        // delete leaves the terminal imageless at almost every tick boundary — the stream renders
        // invisible. Re-transmitting the fixed id with NO delete replaces the image atomically at
        // commit (the terminal frees the old data itself), keeping the previous frame visible
        // while the next one loads.
        Assert.That(text, Does.Not.Contain("a=d"), "the unit must not delete its id");
        Assert.That(text, Does.Contain("a=T"), "the unit transmits (and displays) its fixed id");
    }

    [Test]
    public void EncodeFrame_DefaultsToTheFixedVideoId()
    {
        var frame = KittyEncoder.EncodeFrame(2, 2, SolidBgra(2, 2, 1, 1, 1), DisplayEncoding.Rgba);
        var (headers, _) = Parse(frame);
        var transmit = headers.First(h => h.Contains("a=T"));
        Assert.That(transmit, Does.Contain($"i={KittyEncoder.VideoImageId}"));
    }

    // ---- helpers ----

    private static byte[] SolidBgra(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }

        return px;
    }

    /// <summary>Scans the frame for every <c>ESC _ G … ESC \</c> unit, returning the headers and the
    /// concatenated base64-decoded payload.</summary>
    private static (List<string> Headers, byte[] Payload) Parse(byte[] frame)
    {
        var headers = new List<string>();
        var base64 = new StringBuilder();
        var i = 0;
        while (i < frame.Length)
        {
            // Find ESC _ G.
            if (i + 2 < frame.Length && frame[i] == Esc && frame[i + 1] == (byte)'_' && frame[i + 2] == (byte)'G')
            {
                var start = i + 3;
                var end = start;
                while (!(end + 1 < frame.Length && frame[end] == Esc && frame[end + 1] == (byte)'\\'))
                    end++;
                var body = Encoding.ASCII.GetString(frame, start, end - start);
                var semi = body.IndexOf(';');
                if (semi < 0)
                {
                    headers.Add(body); // control-only APC (e.g. a delete) — no ';' payload
                }
                else
                {
                    headers.Add(body[..semi]);
                    base64.Append(body[(semi + 1)..]);
                }

                i = end + 2;
            }
            else
            {
                i++;
            }
        }

        return (headers, Convert.FromBase64String(base64.ToString()));
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
