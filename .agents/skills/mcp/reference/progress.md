---
type: SDK Documentation
title: "Progress"
description: "Local copy of the C# MCP SDK documentation page Progress."
resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/progress/progress.md"
tags: [mcp, csharp, sdk, documentation]
sources:
  - id: csharp-sdk-doc
    resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/progress/progress.md"
    title: "Progress"
generated: { by: "codex/gpt-5.6", at: "2026-08-14T00:00:00Z" }
status: stable
---

This is a normalized local copy of the official C# SDK documentation page.[^csharp-sdk-doc]
## Progress

The Model Context Protocol (MCP) supports [progress tracking] for long-running operations through notification messages.

[progress tracking]: https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/progress

Typically, progress tracking is supported by server tools that perform operations that take a significant amount of time to complete, such as image generation or complex calculations.
However, progress tracking is defined in the MCP specification as a general feature that can be implemented for any request that's handled by either a server or a client.
This project illustrates the common case of a server tool that performs a long-running operation and sends progress updates to the client.

> [!NOTE]
> Progress notifications are sent inline as part of the response to a request — they are not unsolicited. Progress tracking works in both [stateless and stateful](stateless.md) modes as well as stdio.

### Server implementation

When processing a request, the server can use the `ModelContextProtocol.McpSession.SendNotificationAsync` extension method of `ModelContextProtocol.Server.McpServer` to send progress updates,
specifying `"notifications/progress"` as the notification method name.
The C# SDK registers an instance of `ModelContextProtocol.Server.McpServer` with the dependency injection container,
so tools can simply add a parameter of type `ModelContextProtocol.Server.McpServer` to their method signature to access it.
The parameters passed to `ModelContextProtocol.McpSession.SendNotificationAsync` should be an instance of `ModelContextProtocol.Protocol.ProgressNotificationParams`, which includes the current progress, total steps, and an optional message.

The server must verify that the caller provided a `progressToken` in the request and include it in the call to `ModelContextProtocol.McpSession.SendNotificationAsync`. The following example demonstrates how a server can send a progress notification:

[C# source excerpt: LongRunningTools.cs](samples/progress-LongRunningTools.cs)]

### Client implementation

Clients request progress updates by including a `progressToken` in the parameters of a request.
Note that servers aren't required to support progress tracking, so clients should not depend on receiving progress updates.

In the MCP C# SDK, clients can specify a `progressToken` in the request parameters when calling a tool method.
The client should also provide a notification handler to process "notifications/progress" notifications.
There are two ways to do this. The first is to register a notification handler using the `ModelContextProtocol.McpSession.RegisterNotificationHandler` method on the `ModelContextProtocol.Client.McpClient` instance. A handler registered this way will receive all progress notifications sent by the server.

```csharp
await using var handler = mcpClient.RegisterNotificationHandler(NotificationMethods.ProgressNotification,
    (notification, cancellationToken) =>
    {
        if (JsonSerializer.Deserialize<ProgressNotificationParams>(notification.Params) is { } pn &&
            pn.ProgressToken == progressToken)
        {
            // progress.Report(pn.Progress);
            Console.WriteLine($"Tool progress: {pn.Progress.Progress} of {pn.Progress.Total} - {pn.Progress.Message}");
        }
        return ValueTask.CompletedTask;
    });
```

The second way is to pass a [`Progress<T>`](https://learn.microsoft.com/dotnet/api/system.progress-1) instance to the tool method. `Progress<T>` is a standard .NET type that provides a way to receive progress updates.
For the purposes of MCP progress notifications, `T` should be `ModelContextProtocol.ProgressNotificationValue`.
The MCP C# SDK automatically handles progress notifications and reports them through the `Progress<T>` instance.
This notification handler will only receive progress updates for the specific request that was made,
rather than all progress notifications from the server.

[C# source excerpt: Program.cs](samples/progress-Program.cs)]

[^csharp-sdk-doc]: Official C# SDK documentation source.
