using System.Text;
using System.Text.Json;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;
using static gatOS.SimFs.Tests.TestData;

namespace gatOS.SimFs.Tests;

/// <summary>
///     OS_PLAN.md T8.3: the blocking-event model (spike/NOTES.md T1.2 rule 3) — a fresh read
///     parks until the next event, delivers the line(s), then owes the kernel exactly two
///     0-byte continuation reads so the syscall completes immediately.
/// </summary>
[TestFixture]
public sealed class EventsFileTests
{
    private SnapshotStore _store = null!;
    private EventsFile _file = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new SnapshotStore();
        _file = new EventsFile("events", 1, _store);
    }

    [Test]
    public async Task Read_ParksUntilAnEventArrives_ThenOwesTwoZeros()
    {
        using var handle = _file.Open();
        var read = handle.ReadAsync(0, 8192, CancellationToken.None).AsTask();
        var first = await Task.WhenAny(read, Task.Delay(100));
        Assert.That(first, Is.Not.SameAs(read), "must park while no event exists");

        // A snapshot without events must NOT wake the delivery.
        _store.Publish(Snapshot(1, Vessel()));
        await Task.Delay(100);
        Assert.That(read.IsCompleted, Is.False, "event-less snapshots are not events");

        _store.Publish(Snapshot(2, Vessel())
            .WithEvents(new SimEvent(0.2, "situation-change", "test-1", "Landed→Freefall")));
        var data = await read.WaitAsync(TimeSpan.FromSeconds(10));
        using (var json = JsonDocument.Parse(Encoding.UTF8.GetString(data.Span)))
        {
            Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("situation-change"));
        }

        // The two zero-byte completions (spike rule 2), then park again.
        Assert.That((await handle.ReadAsync((ulong)data.Length, 8192, CancellationToken.None)).IsEmpty);
        Assert.That((await handle.ReadAsync((ulong)data.Length, 8192, CancellationToken.None)).IsEmpty);
        var parked = handle.ReadAsync((ulong)data.Length, 8192, CancellationToken.None).AsTask();
        Assert.That(await Task.WhenAny(parked, Task.Delay(100)), Is.Not.SameAs(parked),
            "after the zeros the next read parks for the next event");

        _store.Publish(Snapshot(3, Vessel()).WithEvents(new SimEvent(0.3, "warp-changed", null, "1→10")));
        var next = await parked.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(Encoding.UTF8.GetString(next.Span), Does.Contain("warp-changed"));
    }

    [Test]
    public async Task MultipleEventsInOneSnapshot_AllDelivered_AsNdjsonLines()
    {
        using var handle = _file.Open();
        var read = handle.ReadAsync(0, 8192, CancellationToken.None).AsTask();
        _store.Publish(Snapshot(1, Vessel()).WithEvents(
            new SimEvent(0.1, "vessel-appeared", "a", "a"),
            new SimEvent(0.1, "vessel-appeared", "b", "b"),
            new SimEvent(0.1, "active-changed", null, "→a")));

        var text = Encoding.UTF8.GetString((await read.WaitAsync(TimeSpan.FromSeconds(10))).Span);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines.All(l => l.StartsWith('{') && l.EndsWith('}')), Is.True);
    }

    [Test]
    public async Task SmallCount_DeliversInChunks_ZerosOnlyAfterTheLastByte()
    {
        using var handle = _file.Open();
        var read = handle.ReadAsync(0, 10, CancellationToken.None).AsTask();
        _store.Publish(Snapshot(1, Vessel()).WithEvents(new SimEvent(0.1, "warp-changed", null, "1→10")));

        var collected = new StringBuilder();
        var chunk = await read.WaitAsync(TimeSpan.FromSeconds(10));
        while (!chunk.IsEmpty)
        {
            Assert.That(chunk.Length, Is.LessThanOrEqualTo(10));
            collected.Append(Encoding.UTF8.GetString(chunk.Span));
            chunk = await handle.ReadAsync((ulong)collected.Length, 10, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.That(collected.ToString(), Does.EndWith("\n"));
        using var json = JsonDocument.Parse(collected.ToString());
        Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("warp-changed"));
    }

    [Test]
    public async Task ParkedRead_CancelsPromptly()
    {
        using var handle = _file.Open();
        using var cts = new CancellationTokenSource();
        var read = handle.ReadAsync(0, 8192, cts.Token).AsTask();
        await Task.Delay(50);
        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task Dispose_UnparksAWaitingRead()
    {
        var handle = _file.Open();
        var read = handle.ReadAsync(0, 8192, CancellationToken.None).AsTask();
        await Task.Delay(50);
        handle.Dispose();
        Assert.CatchAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task OnlyEventsAfterTheOpen_AreDelivered()
    {
        _store.Publish(Snapshot(1, Vessel()).WithEvents(new SimEvent(0.1, "vessel-appeared", "old", "old")));
        using var handle = _file.Open();
        var read = handle.ReadAsync(0, 8192, CancellationToken.None).AsTask();
        var first = await Task.WhenAny(read, Task.Delay(100));
        Assert.That(first, Is.Not.SameAs(read), "pre-open events are history, not news");

        _store.Publish(Snapshot(2, Vessel()).WithEvents(new SimEvent(0.2, "vessel-appeared", "new", "new")));
        var text = Encoding.UTF8.GetString((await read.WaitAsync(TimeSpan.FromSeconds(10))).Span);
        Assert.That(text, Does.Contain("\"new\"").And.Not.Contain("\"old\""));
    }

    [Test]
    public async Task OverTheServer_BlockedRead_SurvivesTflush_AndDeliversTheNextEvent()
    {
        var root = DelegateDirectory.Fixed("/", 100, new EventsFile("events", 1, _store));
        await using var server = new NinePServer(root);
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);
        await client.WalkAsync(0, 1, "events");
        await client.LopenAsync(1);

        // Ctrl-C on a blocked cat: flush the parked read; the server must not wedge.
        var (tag, parked) = client.BeginRead(1, 0, 8192);
        await Task.Delay(100);
        await client.FlushAsync(tag);
        Assert.That(parked.IsCompleted, Is.False, "flushed read is never answered");

        // A fresh read on the same fid still delivers the next event.
        var (_, next) = client.BeginRead(1, 0, 8192);
        _store.Publish(Snapshot(1, Vessel()).WithEvents(new SimEvent(0.1, "liftoff", "test-1", "up!")));
        var data = await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(Encoding.UTF8.GetString(data), Does.Contain("liftoff"));
    }
}
