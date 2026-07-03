using System.Net;
using System.Text;
using System.Text.Json;
using gatOS.SimFs;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.Http.Tests;

/// <summary>
///     The dedicated binary audio routes (GATOS_CUSTOM_AUDIO_PLAN) over a real loopback socket:
///     single-shot and chunked uploads, the clip list, delete, the errno→status mapping
///     (EINVAL 400 / EFBIG 413 / ENOSPC 507), the disabled-audio 404, and the free field-mirror
///     control path (<c>POST /v1/fs/audio/play</c>).
/// </summary>
[TestFixture]
public sealed class AudioHttpRoutesTests
{
    private SnapshotStore _store = null!;
    private AudioStore _audio = null!;
    private Sink _sink = null!;
    private SimHttpServer _server = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _audio = new AudioStore(maxClipBytes: 1024, maxTotalBytes: 4096, maxClips: 4);
        _sink = new Sink();
        var simRoot = SimFsTree.Build(_store, _sink, null, audio: _audio);
        _server = new SimHttpServer(_store, _sink, null, simRoot, _audio);
        await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Test]
    public async Task Put_SingleShot_CommitsAndLists()
    {
        var payload = Encoding.ASCII.GetBytes("not-really-an-mp3");
        var response = await _client.PutAsync("v1/audio/file/alarm.mp3", new ByteArrayContent(payload));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.Multiple(() =>
        {
            Assert.That(_audio.TryGet("alarm.mp3", out var clip), Is.EqualTo(AudioClipLookup.Ready));
            Assert.That(clip!.Bytes, Is.EqualTo(payload));
        });

        using var files = JsonDocument.Parse(await _client.GetStringAsync("v1/audio/files"));
        var entry = files.RootElement[0];
        Assert.Multiple(() =>
        {
            Assert.That(entry.GetProperty("name").GetString(), Is.EqualTo("alarm.mp3"));
            Assert.That(entry.GetProperty("bytes").GetInt64(), Is.EqualTo(payload.Length));
            Assert.That(entry.GetProperty("ready").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Post_Chunked_AppendsByPosition_AndCommitsOnComplete()
    {
        await Expect200(_client.PostAsync("v1/audio/file/big.ogg?offset=0&complete=0",
            new ByteArrayContent([1, 2, 3])));
        Assert.That(_audio.TryGet("big.ogg", out _), Is.EqualTo(AudioClipLookup.Uploading));
        await Expect200(_client.PostAsync("v1/audio/file/big.ogg?offset=3&complete=1",
            new ByteArrayContent([4, 5])));
        Assert.That(_audio.SnapshotBytes("big.ogg"), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public async Task Chunk_OutOfOrder_Is400Einval()
    {
        await Expect200(_client.PutAsync("v1/audio/file/a.ogg?complete=0", new ByteArrayContent([1])));
        var response = await _client.PutAsync("v1/audio/file/a.ogg?offset=9&complete=0",
            new ByteArrayContent([2]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(body, Does.Contain("EINVAL"));
        });
    }

    [Test]
    public async Task PerClipCap_Is413Efbig()
    {
        var response = await _client.PutAsync("v1/audio/file/big.mp3", new ByteArrayContent(new byte[2000]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(body, Does.Contain("EFBIG"));
        });
    }

    [Test]
    public async Task ClipCountCap_Is507Enospc()
    {
        for (var i = 0; i < 4; i++)
            await Expect200(_client.PutAsync($"v1/audio/file/clip{i}.ogg", new ByteArrayContent([1])));
        var response = await _client.PutAsync("v1/audio/file/five.ogg", new ByteArrayContent([1]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InsufficientStorage));
            Assert.That(body, Does.Contain("ENOSPC"));
        });
    }

    [Test]
    public async Task Delete_Evicts_ThenMissingIs404()
    {
        await Expect200(_client.PutAsync("v1/audio/file/x.wav", new ByteArrayContent([1])));
        Assert.That((await _client.DeleteAsync("v1/audio/file/x.wav")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.DeleteAsync("v1/audio/file/x.wav")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AudioDisabled_RoutesAre404()
    {
        await using var bare = new SimHttpServer(_store); // no AudioStore wired
        await bare.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bare.Port}/") };
        Assert.That((await client.GetAsync("v1/audio/files")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task FieldMirror_Play_SubmitsTheAudioCommand()
    {
        // The control surface needs no dedicated route: the field mirror writes /sim/audio/play.
        await Expect200(_client.PutAsync("v1/audio/file/alarm.mp3", new ByteArrayContent([1, 2])));
        var response = await _client.PostAsync("v1/fs/audio/play",
            new StringContent("alarm.mp3 vol=0.8", Encoding.UTF8));
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_sink.Last!.Action, Is.EqualTo("audio.play"));
            Assert.That(_sink.Last!.Token, Is.EqualTo("alarm.mp3"));
        });
    }

    private static async Task Expect200(Task<HttpResponseMessage> send)
    {
        var response = await send;
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
    }

    private sealed class Sink : ICommandSink
    {
        public bool ControlEnabled => true;
        public bool DebugEnabled => false;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(CommandResult.Ok);
        }
    }
}
