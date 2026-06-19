using System.Text;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Display;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md §4.1: the <c>/sim/display/</c> control surface walked over a live 9p server —
///     reads show the live settings, writes retune them with errno feedback, and the binary
///     <c>stream</c> feed is present.
/// </summary>
[TestFixture]
public sealed class DisplayTreeTests
{
    private DisplaySurface _surface = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _surface = new DisplaySurface(new DisplaySettings());
        _surface.Start();
        var store = new SnapshotStore();
        _server = new NinePServer(SimFsTree.Build(store, null, null, _surface));
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
        _nextFid = 1;
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _surface.Dispose();
    }

    [Test]
    public async Task DisplayDir_ExposesTheControlSurface_AndStream()
    {
        Assert.That(await ReadAsync("display", "enabled"), Is.EqualTo("0\n"), "defaults off");
        Assert.That(await ReadAsync("display", "fps"), Is.EqualTo("15\n"));
        Assert.That(await ReadAsync("display", "width"), Is.EqualTo("320\n"));
        Assert.That(await ReadAsync("display", "height"), Is.EqualTo("180\n"));
        Assert.That(await ReadAsync("display", "encoding"), Is.EqualTo("rgba-zlib\n"));
        Assert.That(await ReadAsync("display", "format"), Is.EqualTo("320x180@15 rgba-zlib\n"));

        // The binary stream is walkable (its bytes are exercised in DisplayStreamFileTests).
        var (fid, _) = await WalkAsync("display", "stream");
        await _client.ClunkAsync(fid);
    }

    [Test]
    public async Task WritingEnabled_FlipsTheLiveSetting()
    {
        await WriteAsync("1", "display", "enabled");
        Assert.That(_surface.Settings.Enabled, Is.True);
        Assert.That(await ReadAsync("display", "enabled"), Is.EqualTo("1\n"));

        await WriteAsync("off", "display", "enabled");
        Assert.That(_surface.Settings.Enabled, Is.False);
    }

    [Test]
    public async Task WritingFpsAndSize_Retunes_AndClamps()
    {
        await WriteAsync("24", "display", "fps");
        Assert.That(_surface.Settings.Fps, Is.EqualTo(24));

        await WriteAsync("9999", "display", "fps"); // clamped, not rejected
        Assert.That(_surface.Settings.Fps, Is.EqualTo(DisplaySettings.MaxFps));

        await WriteAsync("640", "display", "width");
        await WriteAsync("360", "display", "height");
        Assert.That(await ReadAsync("display", "format"), Is.EqualTo($"640x360@{DisplaySettings.MaxFps} rgba-zlib\n"));
    }

    [Test]
    public async Task WritingEncoding_SwitchesFormat_AndRejectsUnknown()
    {
        await WriteAsync("rgba", "display", "encoding");
        Assert.That(_surface.Settings.Encoding, Is.EqualTo(DisplayEncoding.Rgba));

        Assert.CatchAsync(() => WriteAsync("png", "display", "encoding"));
        Assert.That(_surface.Settings.Encoding, Is.EqualTo(DisplayEncoding.Rgba), "rejected write does not change it");
    }

    [Test]
    public void WritingGarbageToFps_FailsTheWrite()
        => Assert.CatchAsync(() => WriteAsync("abc", "display", "fps"));

    private async Task<(uint Fid, object? _)> WalkAsync(params string[] names)
    {
        var fid = _nextFid++;
        await _client.WalkAsync(0, fid, names);
        return (fid, null);
    }

    private async Task<string> ReadAsync(params string[] names)
    {
        var (fid, _) = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var content = Encoding.UTF8.GetString(await _client.ReadToEndAsync(fid));
        await _client.ClunkAsync(fid);
        return content;
    }

    private async Task WriteAsync(string value, params string[] names)
    {
        var (fid, _) = await WalkAsync(names);
        await _client.LopenAsync(fid, 1); // O_WRONLY
        await _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(value + "\n"));
        await _client.ClunkAsync(fid);
    }
}
