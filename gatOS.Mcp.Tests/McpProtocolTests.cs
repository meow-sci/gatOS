using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Snapshots;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace gatOS.Mcp.Tests;

[TestFixture]
public sealed class McpProtocolTests
{
    private SimMcpServer _server = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _server = new SimMcpServer(new McpRegistry(new SnapshotStore()), "test");
        await _server.StartAsync(0);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Test]
    public async Task CurrentDiscover_IsStateless_AndListsTools()
    {
        var discover = await PostCurrentAsync("server/discover", new { });
        Assert.That(discover.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(discover.Headers.Contains("Mcp-Session-Id"), Is.False);
        Assert.That(discover.Headers.Connection, Does.Contain("keep-alive"));

        var list = await PostCurrentAsync("tools/list", new { });
        var listText = await list.Content.ReadAsStringAsync();
        var data = listText.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using var json = JsonDocument.Parse(data);
        var body = json.RootElement;
        Assert.That(body.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()), Does.Contain("gatos.get_world"));
    }

    [Test]
    public async Task OfficialSdkClient_DiscoversAndListsStatelessServer()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_server.Port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        }, NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var tools = await client.ListToolsAsync();
        var missing = await client.CallToolAsync("gatos.get_celestial",
            new Dictionary<string, object?> { ["id"] = "missing" });
        Assert.Multiple(() =>
        {
            Assert.That(tools.Select(t => t.Name), Does.Contain("gatos.get_world"));
            Assert.That(tools.Select(t => t.Name), Has.None.Contains("display"));
            Assert.That(missing.IsError, Is.True);
            Assert.That(missing.StructuredContent?.GetProperty("errno").GetString(), Is.EqualTo("ENOENT"));
        });
    }

    [Test]
    public async Task AudioRetrieve_ReturnsAnMcpAudioContentBlock()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        var audio = new AudioStore();
        audio.HttpUpload("beep.wav", 0, [1, 2, 3, 4], complete: true);
        _server = new SimMcpServer(new McpRegistry(new SnapshotStore(), audio: audio), "test");
        await _server.StartAsync(0);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_server.Port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        }, NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var result = await client.CallToolAsync("gatos.audio_clip",
            new Dictionary<string, object?> { ["operation"] = "retrieve", ["name"] = "beep.wav" });

        var block = result.Content.OfType<AudioContentBlock>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(block.MimeType, Is.EqualTo("audio/wav"));
            Assert.That(block.DecodedData.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        });
    }

    [Test]
    public async Task RejectsGetForeignHostSessionAndDisplay()
    {
        Assert.That((await _client.GetAsync("mcp")).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));

        using var host = NewCurrent("tools/list", new { });
        host.Headers.Host = "evil.example";
        Assert.That((await _client.SendAsync(host)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var session = NewCurrent("tools/list", new { });
        session.Headers.Add("Mcp-Session-Id", "not-allowed");
        Assert.That((await _client.SendAsync(session)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var resources = await PostCurrentAsync("resources/list", new { });
        Assert.That(await resources.Content.ReadAsStringAsync(), Does.Not.Contain("display"));
    }

    [Test]
    public async Task DownlevelInitialize_IsAcceptedWithoutCreatingSession()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "downlevel-test", version = "1" } },
            }),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
            Assert.That(text, Does.Contain("2025-11-25"));
            Assert.That(response.Headers.Contains("Mcp-Session-Id"), Is.False);
        });
    }

    private async Task<HttpResponseMessage> PostCurrentAsync(string method, object parameters)
    {
        using var request = NewCurrent(method, parameters);
        return await _client.SendAsync(request);
    }

    private HttpRequestMessage NewCurrent(string method, object parameters)
    {
        var paramsNode = JsonSerializer.SerializeToNode(parameters)!.AsObject();
        paramsNode["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "gatOS-tests", ["version"] = "1" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = method, ["params"] = paramsNode,
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        return request;
    }
}
