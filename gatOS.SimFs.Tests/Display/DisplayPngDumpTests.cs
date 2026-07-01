using System.Buffers.Binary;
using System.Text;
using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md "tier-1 validation": with <see cref="DisplaySurface.PngDumpDirectory"/> set,
///     the encode worker bypasses the Kitty encoder — a submitted frame lands on disk as a valid PNG
///     and the stream feed carries a plain-text progress line instead of Kitty bytes.
/// </summary>
[TestFixture]
public sealed class DisplayPngDumpTests
{
    private string _dir = null!;
    private DisplaySurface _surface = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gatos-png-dump-{Guid.NewGuid():N}");
        _surface = new DisplaySurface(new DisplaySettings(enabled: true, fps: 30, width: 4, height: 4))
        {
            PngDumpDirectory = _dir,
        };
        _surface.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _surface.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS temp dir reaps leftovers.
        }
    }

    [Test]
    public async Task SubmitFrame_WritesAPng_AndPublishesATextLine_NotKittyBytes()
    {
        _surface.SubmitFrame(4, 4, Solid(4, 4, value: 200));
        var frame = await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();

        var text = Encoding.ASCII.GetString(frame.Bytes);
        Assert.That(text, Does.StartWith("wrote screencap-"));
        Assert.That(frame.Bytes, Does.Not.Contain((byte)0x1b), "no ESC — this is not a Kitty unit");

        var files = Directory.GetFiles(_dir, "screencap-*.png");
        Assert.That(files, Has.Length.EqualTo(1));

        var png = await File.ReadAllBytesAsync(files[0]);
        Assert.That(png[..8], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.That(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), Is.EqualTo(4), "IHDR width");
        Assert.That(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)), Is.EqualTo(4), "IHDR height");
    }

    [Test]
    public async Task RapidFrames_AreThrottledToOnePngPerSecond()
    {
        // All submits land well inside one second, so exactly the first becomes a file (the 1 Hz
        // throttle drops the rest — the dump rate is decoupled from the capture cadence).
        _surface.SubmitFrame(4, 4, Solid(4, 4, value: 10));
        await _surface.WaitForNextEncodedAsync(0, Cancel(5)).AsTask();
        for (var i = 0; i < 5; i++)
            _surface.SubmitFrame(4, 4, Solid(4, 4, value: (byte)(20 + i)));
        await Task.Delay(200);

        Assert.That(Directory.GetFiles(_dir, "screencap-*.png"), Has.Length.EqualTo(1));
    }

    private static byte[] Solid(int w, int h, byte value)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, value);
        return px;
    }

    private static CancellationToken Cancel(int seconds)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
