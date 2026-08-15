---
type: SDK Documentation
title: "Capabilities"
description: "How capability and protocol version negotiation works in MCP."
resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/capabilities/capabilities.md"
tags: [mcp, csharp, sdk, documentation]
sources:
  - id: csharp-sdk-doc
    resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/capabilities/capabilities.md"
    title: "Capabilities"
generated: { by: "codex/gpt-5.6", at: "2026-08-14T00:00:00Z" }
status: stable
---

This is a normalized local copy of the official C# SDK documentation page.[^csharp-sdk-doc]
## Capabilities

MCP uses a [capability negotiation] mechanism during connection setup. Clients and servers exchange their supported capabilities so each side can adapt its behavior accordingly. Both sides should check the other's capabilities before using optional features.

[capability negotiation]: https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle#initialization

### Client capabilities

`ModelContextProtocol.Protocol.ClientCapabilities` declares what features the client supports:

| Capability | Type | Description |
|-----------|------|-------------|
| `Roots` | `ModelContextProtocol.Protocol.RootsCapability` | Client can provide filesystem root URIs |
| `Sampling` | `ModelContextProtocol.Protocol.SamplingCapability` | Client can handle LLM sampling requests |
| `Elicitation` | `ModelContextProtocol.Protocol.ElicitationCapability` | Client can present forms or URLs to the user |
| `Experimental` | `IDictionary<string, object>` | Experimental capabilities |

Configure client capabilities when creating an MCP client:

```csharp
var options = new McpClientOptions
{
    Capabilities = new ClientCapabilities
    {
        Roots = new RootsCapability { ListChanged = true },
        Sampling = new SamplingCapability(),
        Elicitation = new ElicitationCapability
        {
            Form = new FormElicitationCapability(),
            Url = new UrlElicitationCapability()
        }
    }
};

await using var client = await McpClient.CreateAsync(transport, options);
```

Handlers for each capability (roots, sampling, and elicitation) are covered in their respective documentation pages.

### Server capabilities

`ModelContextProtocol.Protocol.ServerCapabilities` declares what features the server supports:

| Capability | Type | Description |
|-----------|------|-------------|
| `Tools` | `ModelContextProtocol.Protocol.ToolsCapability` | Server exposes callable tools |
| `Prompts` | `ModelContextProtocol.Protocol.PromptsCapability` | Server exposes prompt templates |
| `Resources` | `ModelContextProtocol.Protocol.ResourcesCapability` | Server exposes readable resources |
| `Logging` | `ModelContextProtocol.Protocol.LoggingCapability` | Server can send log messages |
| `Completions` | `ModelContextProtocol.Protocol.CompletionsCapability` | Server supports argument completions |
| `Experimental` | `IDictionary<string, object>` | Experimental capabilities |

Server capabilities are automatically inferred from the configured features. For example, registering tools with `.WithTools<T>()` automatically declares the tools capability.

### Checking capabilities

Before using an optional feature, check whether the other side declared the corresponding capability.

#### Check server capabilities from the client

```csharp
await using var client = await McpClient.CreateAsync(transport);

// Check if the server supports tools
if (client.ServerCapabilities.Tools is not null)
{
    var tools = await client.ListToolsAsync();
}

// Check if the server supports resources with subscriptions
if (client.ServerCapabilities.Resources is { Subscribe: true })
{
    await client.SubscribeToResourceAsync("config://app/settings");
}

// Check if the server supports prompts with list-changed notifications
if (client.ServerCapabilities.Prompts is { ListChanged: true })
{
    client.RegisterNotificationHandler(
        NotificationMethods.PromptListChangedNotification,
        async (notification, ct) =>
        {
            var prompts = await client.ListPromptsAsync(cancellationToken: ct);
        });
}

// Check if the server supports logging
if (client.ServerCapabilities.Logging is not null)
{
    await client.SetLoggingLevelAsync(LoggingLevel.Info);
}

// Check if the server supports completions
if (client.ServerCapabilities.Completions is not null)
{
    var completions = await client.CompleteAsync(
        new PromptReference { Name = "my_prompt" },
        argumentName: "language",
        argumentValue: "py");
}
```

### Protocol version negotiation

During connection setup, the client and server negotiate a mutually supported MCP protocol version. After initialization, the negotiated version is available on both sides:

```csharp
// On the client
string? version = client.NegotiatedProtocolVersion;

// On the server (within a tool or handler)
string? version = server.NegotiatedProtocolVersion;
```

Version negotiation is handled automatically. If the client and server cannot agree on a compatible protocol version, the initialization fails with an error.

[^csharp-sdk-doc]: Official C# SDK documentation source.
