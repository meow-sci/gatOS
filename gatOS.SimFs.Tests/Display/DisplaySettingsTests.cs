using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>STREAM_PLAN.md §4.1: the runtime-mutable stream parameters clamp like the config.</summary>
[TestFixture]
public sealed class DisplaySettingsTests
{
    [Test]
    public void Defaults_AreOffAt320x180At15Fps()
    {
        var s = new DisplaySettings();
        Assert.Multiple(() =>
        {
            Assert.That(s.Enabled, Is.False, "streaming defaults off");
            Assert.That(s.Fps, Is.EqualTo(15));
            Assert.That(s.Width, Is.EqualTo(320));
            Assert.That(s.Height, Is.EqualTo(180));
            // Raw is the default: the purrTTY-pinned libghostty native corrupts on o=z
            // payloads of compressible data (purrtty gotcha 34; STREAM_PLAN.md §11).
            Assert.That(s.Encoding, Is.EqualTo(DisplayEncoding.Rgba));
        });
    }

    [Test]
    public void Fps_ClampsToRange()
    {
        var s = new DisplaySettings();
        s.Fps = 1000;
        Assert.That(s.Fps, Is.EqualTo(DisplaySettings.MaxFps));
        s.Fps = 0;
        Assert.That(s.Fps, Is.EqualTo(DisplaySettings.MinFps));
    }

    [Test]
    public void Edges_ClampToRange()
    {
        var s = new DisplaySettings();
        s.Width = 99999;
        s.Height = 1;
        Assert.That(s.Width, Is.EqualTo(DisplaySettings.MaxEdge));
        Assert.That(s.Height, Is.EqualTo(DisplaySettings.MinEdge));
    }

    [TestCase("rgba-zlib", DisplayEncoding.RgbaZlib)]
    [TestCase("zlib", DisplayEncoding.RgbaZlib)]
    [TestCase("rgba", DisplayEncoding.Rgba)]
    [TestCase("RAW", DisplayEncoding.Rgba)]
    public void Encoding_ParsesKnownTokens(string token, DisplayEncoding expected)
        => Assert.That(DisplayEncodings.Parse(token), Is.EqualTo(expected));

    [Test]
    public void Encoding_RejectsUnknownToken()
        => Assert.That(DisplayEncodings.Parse("png"), Is.Null);

    [Test]
    public void Encoding_TokenRoundTrips()
    {
        Assert.That(DisplayEncoding.RgbaZlib.Token(), Is.EqualTo("rgba-zlib"));
        Assert.That(DisplayEncoding.Rgba.Token(), Is.EqualTo("rgba"));
    }
}
