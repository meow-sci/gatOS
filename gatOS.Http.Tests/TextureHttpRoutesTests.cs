using System.Net;
using System.Net.Http.Headers;
using System.Text;
using gatOS.SimFs;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.Http.Tests;

/// <summary>
///     The dedicated binary <c>/v1/paint/texture/…</c> routes
///     (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): single-shot and chunked upload, the append-by-position
///     protocol, the store caps on the wire, eviction, the disabled-store 404, the explicit
///     oversized-body 413 that the audio route lacks, and the free field-mirror control path.
/// </summary>
[TestFixture]
public sealed class TextureHttpRoutesTests
{
    private static readonly byte[] PngHeader = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private SnapshotStore _store = null!;
    private RecordingSink _sink = null!;
    private TextureStore _textures = null!;
    private SimHttpServer _server = null!;
    private HttpClient _client = null!;

    private static byte[] Png(int extra = 8)
    {
        var bytes = new byte[PngHeader.Length + extra];
        PngHeader.CopyTo(bytes, 0);
        return bytes;
    }

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _sink = new RecordingSink();
        _textures = new TextureStore(maxFileBytes: 1024, maxTotalBytes: 4096, maxFiles: 4, maxBindings: 2);
        var simRoot = SimFsTree.Build(_store, _sink, null, textures: _textures);
        _server = new SimHttpServer(_store, _sink, null, simRoot, null, _textures);
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
        var bytes = Png(64);
        await Expect200(await _client.PutAsync("v1/paint/texture/file/rock.png", Binary(bytes)));
        Assert.That(_textures.TryGet("rock.png", out var file), Is.EqualTo(TextureLookup.Ready));
        Assert.That(file!.Kind, Is.EqualTo(TextureImageKind.Png));

        var list = await _client.GetStringAsync("v1/paint/texture/files");
        Assert.Multiple(() =>
        {
            Assert.That(list, Does.Contain("rock.png"));
            Assert.That(list, Does.Contain("\"kind\":\"png\""));
            Assert.That(list, Does.Contain("\"ready\":true"));
        });
    }

    [Test]
    public async Task Post_Chunked_AppendsByPosition_AndCommitsOnComplete()
    {
        var bytes = Png(120);
        await Expect200(await _client.PostAsync(
            "v1/paint/texture/file/rock.png?offset=0&complete=0", Binary(bytes.AsSpan(0, 64).ToArray())));
        Assert.That(_textures.TryGet("rock.png", out _), Is.EqualTo(TextureLookup.Uploading));
        await Expect200(await _client.PostAsync(
            "v1/paint/texture/file/rock.png?offset=64&complete=1", Binary(bytes.AsSpan(64).ToArray())));
        Assert.That(_textures.SnapshotBytes("rock.png"), Is.EqualTo(bytes));
    }

    [Test]
    public async Task Chunk_OutOfOrder_Is400Einval()
    {
        await Expect200(await _client.PostAsync(
            "v1/paint/texture/file/rock.png?offset=0&complete=0", Binary(Png(56))));
        var response = await _client.PostAsync(
            "v1/paint/texture/file/rock.png?offset=999&complete=1", Binary(Png()));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("EINVAL"));
    }

    [Test]
    public async Task PerFileCap_Is413Efbig()
    {
        var response = await _client.PutAsync("v1/paint/texture/file/big.png", Binary(new byte[2048]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("EFBIG"));
    }

    [Test]
    public async Task FileCountCap_Is507Enospc()
    {
        for (var i = 0; i < 4; i++)
            await Expect200(await _client.PutAsync($"v1/paint/texture/file/f{i}.png", Binary(Png())));
        var response = await _client.PutAsync("v1/paint/texture/file/five.png", Binary(Png()));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InsufficientStorage));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("ENOSPC"));
    }

    [Test]
    public async Task OversizedBody_Is413_RatherThanASilentlyEmptyUpload()
    {
        // The reader refuses to buffer a body past its 1 MiB cap, which would otherwise commit an
        // empty file and desynchronize the keep-alive connection. PNGs routinely exceed it.
        var response = await _client.PutAsync("v1/paint/texture/file/huge.png",
            Binary(new byte[(1 << 20) + 1]));
        Assert.Multiple(async () =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("chunks"));
            Assert.That(_textures.Exists("huge.png"), Is.False, "nothing may be committed");
        });
    }

    [Test]
    public async Task Delete_Evicts_ThenMissingIs404()
    {
        await Expect200(await _client.PutAsync("v1/paint/texture/file/rock.png", Binary(Png())));
        await Expect200(await _client.DeleteAsync("v1/paint/texture/file/rock.png"));
        Assert.That(_textures.Exists("rock.png"), Is.False);
        var response = await _client.DeleteAsync("v1/paint/texture/file/rock.png");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TexturesDisabled_RoutesAre404()
    {
        await using var bare = new SimHttpServer(_store);
        await bare.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bare.Port}/") };
        var response = await client.GetAsync("v1/paint/texture/files");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task FieldMirror_BindAndClear_NeedNoDedicatedRoute()
    {
        // The control surface rides the generic field mirror; only the binary upload needs a route.
        await Expect200(await _client.PostAsync("v1/fs/paint/textures/bind",
            new StringContent("Core/Rock.png rock.png", Encoding.UTF8)));
        Assert.That(_sink.Last!.Action, Is.EqualTo("paint.texture_bind"));
        Assert.That(_sink.Last!.Aux, Is.EqualTo("rock.png"));

        await Expect200(await _client.PostAsync("v1/fs/paint/textures/clear",
            new StringContent("1", Encoding.UTF8)));
        Assert.That(_sink.Last!.Action, Is.EqualTo("paint.texture_clear"));
    }

    [Test]
    public async Task FieldMirror_ReadsTheListings()
    {
        _textures.PublishCatalog([
            new ClutterTextureInfo("Core/Rock.png", "diffuse", 512, 512, 10, 1, ["Rocks"]),
        ]);
        Assert.That(await _client.GetStringAsync("v1/fs/paint/textures/clutter"),
            Does.Contain("Core/Rock.png diffuse 512 512 10 1 Rocks"));
    }

    private static ByteArrayContent Binary(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task Expect200(HttpResponseMessage response)
        => Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());

    private sealed class RecordingSink : ICommandSink
    {
        public bool ControlEnabled => true;
        public bool DebugEnabled => true;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(CommandResult.Ok);
        }
    }
}
