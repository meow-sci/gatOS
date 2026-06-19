using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Display;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md §4.1: the binary <c>/sim/display/stream</c> feed — a blocking-event file that
///     parks until the next encoded frame, registers as a reader while open, and delivers raw bytes
///     (binary-safe) through the real 9p server.
/// </summary>
[TestFixture]
public sealed class DisplayStreamFileTests
{
    private DisplaySurface _surface = null!;
    private DisplayStreamFile _file = null!;

    [SetUp]
    public void SetUp()
    {
        _surface = new DisplaySurface(new DisplaySettings(enabled: true, fps: 30, width: 4, height: 4));
        _surface.Start();
        _file = new DisplayStreamFile("stream", 1, _surface);
    }

    [TearDown]
    public void TearDown() => _surface.Dispose();

    [Test]
    public async Task Open_RegistersAReader_DisposeUnregisters()
    {
        Assert.That(_surface.HasReaders, Is.False);
        var handle = _file.Open();
        Assert.That(_surface.HasReaders, Is.True);
        handle.Dispose();
        Assert.That(_surface.HasReaders, Is.False);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Read_ParksUntilFrame_DeliversIt_ThenOwesTwoZeros()
    {
        using var handle = _file.Open();
        var read = handle.ReadAsync(0, 65536, CancellationToken.None).AsTask();
        Assert.That(await Task.WhenAny(read, Task.Delay(150)), Is.Not.SameAs(read), "parks with no frame");

        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var frame = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(frame.Length, Is.GreaterThan(0));
        Assert.That(frame.Span[0], Is.EqualTo((byte)0x1b), "a Kitty frame begins with ESC");

        // The two zero-byte completions, then park again.
        Assert.That((await handle.ReadAsync((ulong)frame.Length, 65536, CancellationToken.None)).IsEmpty);
        Assert.That((await handle.ReadAsync((ulong)frame.Length, 65536, CancellationToken.None)).IsEmpty);
        var parked = handle.ReadAsync((ulong)frame.Length, 65536, CancellationToken.None).AsTask();
        Assert.That(await Task.WhenAny(parked, Task.Delay(150)), Is.Not.SameAs(parked));
    }

    [Test]
    public async Task Dispose_UnparksAWaitingRead()
    {
        var handle = _file.Open();
        var read = handle.ReadAsync(0, 65536, CancellationToken.None).AsTask();
        await Task.Delay(50);
        handle.Dispose();
        Assert.CatchAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task OverTheServer_DeliversBinaryFrameBytes()
    {
        var root = DelegateDirectory.Fixed("/", 100, _file);
        await using var server = new NinePServer(root);
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);
        await client.WalkAsync(0, 1, "stream");
        await client.LopenAsync(1);

        var (_, read) = client.BeginRead(1, 0, 65536);
        _surface.SubmitFrame(4, 4, Solid(4, 4));
        var data = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(data, Is.Not.Empty);
        Assert.That(data[0], Is.EqualTo((byte)0x1b));
    }

    private static byte[] Solid(int w, int h)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)200);
        return px;
    }
}
