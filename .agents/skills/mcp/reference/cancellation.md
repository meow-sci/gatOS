---
type: SDK Documentation
title: "Cancellation"
description: "How to cancel in-flight MCP requests using cancellation tokens and notifications."
resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/cancellation/cancellation.md"
tags: [mcp, csharp, sdk, documentation]
sources:
  - id: csharp-sdk-doc
    resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/cancellation/cancellation.md"
    title: "Cancellation"
generated: { by: "codex/gpt-5.6", at: "2026-08-14T00:00:00Z" }
status: stable
---

This is a normalized local copy of the official C# SDK documentation page.[^csharp-sdk-doc]
## Cancellation

MCP supports [cancellation] of in-flight requests. Either side can cancel a previously issued request, and `CancellationToken` parameters on MCP methods are wired to send and receive `notifications/cancelled` notifications over the protocol.

[cancellation]: https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/cancellation
[task cancellation]: https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

> [!NOTE]
> The source and lifetime of the `CancellationToken` provided to server handlers depends on the transport and session mode. In [stateless mode](stateless.md#stateless-mode-recommended), the token is tied to the HTTP request — if the client disconnects, the handler is cancelled. In [stateful mode](stateless.md#stateful-mode-sessions), the token is tied to the session lifetime. See [Cancellation and disposal](stateless.md#cancellation-and-disposal) for details.

### How cancellation maps to MCP notifications

When a `CancellationToken` passed to a client method (such as `ModelContextProtocol.Client.McpClient.CallToolAsync`) is cancelled, a `notifications/cancelled` notification is sent to the server with the request ID. On the server side, the `CancellationToken` provided to the tool method is then triggered, allowing the handler to stop work gracefully. This same mechanism works in reverse for server-to-client requests.

### Server-side cancellation handling

Server tool methods receive a `CancellationToken` that is triggered when the client sends a cancellation notification. Pass this token through to any async operations so they stop promptly:

```csharp
[McpServerTool, Description("A long-running computation")]
public static async Task<string> LongComputation(
    [Description("Number of iterations")] int iterations,
    CancellationToken cancellationToken)
{
    for (int i = 0; i < iterations; i++)
    {
        await Task.Delay(1000, cancellationToken);
    }

    return $"Completed {iterations} iterations.";
}
```

When the client sends a cancellation notification, the `OperationCanceledException` propagates back to the client as a cancellation response.

### Cancellation notification details

The cancellation notification includes:

- **RequestId**: The ID of the request to cancel, allowing the receiver to correlate the cancellation with the correct in-flight request.
- **Reason**: An optional human-readable reason for the cancellation.

Cancellation notifications can be observed by registering a handler. For broader interception of notifications and other messages, you can add `ModelContextProtocol.Server.McpMessageFilter` delegates to the `ModelContextProtocol.Server.McpMessageFilters.IncomingFilters` collection in `ModelContextProtocol.Server.McpServerOptions.Filters`.

```csharp
mcpClient.RegisterNotificationHandler(
    NotificationMethods.CancelledNotification,
    (notification, ct) =>
    {
        var cancelled = notification.Params?.Deserialize<CancelledNotificationParams>(
            McpJsonUtilities.DefaultOptions);
        if (cancelled is not null)
        {
            Console.WriteLine($"Request {cancelled.RequestId} cancelled: {cancelled.Reason}");
        }
        return default;
    });
```

[^csharp-sdk-doc]: Official C# SDK documentation source.
