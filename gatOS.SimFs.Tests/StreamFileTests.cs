using System.Text;
using System.Text.Json;
using gatOS.SimFs.Snapshots;
using static gatOS.SimFs.Tests.TestData;

namespace gatOS.SimFs.Tests;

/// <summary>
///     OS_PLAN.md T8.3: the growing-log model (spike/NOTES.md T1.2 rule 3) — never block,
///     0 bytes at the frontier, truthful growing size, line-preserving trim under the cap.
/// </summary>
[TestFixture]
public sealed class StreamFileTests
{
    private SnapshotStore _store = null!;
    private StreamFile _file = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new SnapshotStore();
        _file = new StreamFile("stream", 1, _store, "test-1");
    }

    [Test]
    public async Task Open_SeedsTheCurrentSnapshotLine_SoSizeIsNeverZeroWhileTheVesselExists()
    {
        _store.Publish(Snapshot(1, Vessel()));
        using var handle = _file.Open();
        Assert.That(handle.Size, Is.GreaterThan(0), "spike rule 1: size 0 would suppress all reads");

        var data = await handle.ReadAsync(0, 8192, CancellationToken.None);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(data.Span));
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(1));
            Assert.That(handle.Size, Is.EqualTo(data.Length));
            Assert.That(_file.Size, Is.EqualTo(data.Length), "unopened size = the would-be seed line");
        });
    }

    [Test]
    public async Task ReadAtFrontier_ReturnsZeroBytesImmediately_NeverBlocks()
    {
        _store.Publish(Snapshot(1, Vessel()));
        using var handle = _file.Open();
        var frontier = (ulong)handle.Size;

        var read = handle.ReadAsync(frontier, 8192, CancellationToken.None).AsTask();
        var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Multiple(() =>
        {
            Assert.That(done, Is.SameAs(read), "a frontier read must return synchronously-ish, never park");
            Assert.That(read.Result.IsEmpty, Is.True, "tail -f needs the 0 to enter follow mode");
        });
    }

    [Test]
    public async Task PublishedSnapshots_AppendLines_AndSizeGrows()
    {
        _store.Publish(Snapshot(1, Vessel()));
        using var handle = _file.Open();
        var seeded = handle.Size;

        _store.Publish(Snapshot(2, Vessel(radarAltitude: 100)));
        _store.Publish(Snapshot(3, Vessel(radarAltitude: 200)));
        await WaitUntilAsync(() => handle.Size > seeded, "pump should append published lines");
        await WaitUntilAsync(
            () => CountLines(handle) >= 2, "both publishes should eventually be visible");

        var text = ReadAll(handle);
        var lines = text.TrimEnd('\n').Split('\n');
        var sequences = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64());
        Assert.That(sequences, Is.Ordered.Ascending);
        Assert.That(handle.Size, Is.EqualTo(Encoding.UTF8.GetByteCount(text)), "truthful size");
    }

    [Test]
    public void VesselAbsent_OpensEmpty()
    {
        _store.Publish(Snapshot(1, Vessel("other")));
        using var handle = _file.Open();
        Assert.Multiple(() =>
        {
            Assert.That(handle.Size, Is.Zero);
            Assert.That(_file.Size, Is.Zero);
        });
    }

    [Test]
    public async Task DisposedHandle_StopsPumping()
    {
        _store.Publish(Snapshot(1, Vessel()));
        var handle = _file.Open();
        var sizeAtDispose = handle.Size;
        handle.Dispose();

        _store.Publish(Snapshot(2, Vessel()));
        await Task.Delay(100);
        Assert.That(handle.Size, Is.EqualTo(sizeAtDispose), "no appends after clunk");
    }

    [Test]
    public async Task BufferCap_TrimsWholeLines_AndNotesTheGap()
    {
        // Fat lines (a ~16 KiB situation string) reach the 256 KiB cap in ~17 lines. The pump
        // deliberately coalesces racing publishes, so pace them: publish, wait for the append.
        var fat = new string('s', 16 * 1024);
        _store.Publish(Snapshot(1, Vessel(situation: fat)));
        using var handle = _file.Open();

        for (var i = 2; i <= 25; i++)
        {
            var before = handle.Size;
            _store.Publish(Snapshot(i, Vessel(situation: fat)));
            await WaitUntilAsync(() => handle.Size > before, $"line {i} should be appended");
        }

        var produced = handle.Size;

        // A read at long-gone offset 0 serves from the oldest retained byte instead.
        var data = await handle.ReadAsync(0, uint.MaxValue, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(produced, Is.GreaterThan(StreamFile.BufferCap), "stream kept growing past the cap");
            Assert.That(data.Length, Is.LessThanOrEqualTo(StreamFile.BufferCap + 64), "window stays capped");
        });

        var text = Encoding.UTF8.GetString(data.Span);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("{\"notice\":\"dropped\"}"), "the gap is announced");
            Assert.That(lines.All(l => l.StartsWith('{') && l.EndsWith('}')), Is.True,
                "trim drops whole lines — every retained line stays parseable");
        });
        // And the newest line is the newest publish.
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.That(last.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(25));
    }

    private static string ReadAll(gatOS.NineP.Vfs.IVfsFileHandle handle)
    {
        var data = handle.ReadAsync(0, uint.MaxValue, CancellationToken.None).AsTask().Result;
        return Encoding.UTF8.GetString(data.Span);
    }

    private static int CountLines(gatOS.NineP.Vfs.IVfsFileHandle handle)
        => ReadAll(handle).Count(c => c == '\n');
}
