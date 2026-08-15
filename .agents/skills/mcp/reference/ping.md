---
type: SDK Documentation
title: "Ping"
description: "How to use the MCP ping mechanism to check connection health."
resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/ping/ping.md"
tags: [mcp, csharp, sdk, documentation]
sources:
  - id: csharp-sdk-doc
    resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/ping/ping.md"
    title: "Ping"
generated: { by: "codex/gpt-5.6", at: "2026-08-14T00:00:00Z" }
status: stable
---

This is a normalized local copy of the official C# SDK documentation page.[^csharp-sdk-doc]
## Ping

MCP includes a [ping mechanism] that allows either side of a connection to verify that the other side is still responsive. This is useful for connection health monitoring and keep-alive scenarios.

[ping mechanism]: https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/ping

### Pinging from the client

Use the `ModelContextProtocol.Client.McpClient.PingAsync` method to verify the server is responsive:

```csharp
await client.PingAsync(cancellationToken: cancellationToken);
```

### Automatic ping handling

Incoming ping requests from either side are responded to automatically. No additional configuration is needed&mdash;when a ping request is received, a ping response is sent immediately.

[^csharp-sdk-doc]: Official C# SDK documentation source.
