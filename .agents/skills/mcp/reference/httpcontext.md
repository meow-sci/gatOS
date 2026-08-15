---
type: SDK Documentation
title: "HTTP Context"
description: "How to access the HttpContext in the MCP C# SDK."
resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/httpcontext/httpcontext.md"
tags: [mcp, csharp, sdk, documentation]
sources:
  - id: csharp-sdk-doc
    resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/httpcontext/httpcontext.md"
    title: "HTTP Context"
generated: { by: "codex/gpt-5.6", at: "2026-08-14T00:00:00Z" }
status: stable
---

This is a normalized local copy of the official C# SDK documentation page.[^csharp-sdk-doc]
## HTTP Context

When using the Streamable HTTP transport, an MCP server might need to access the underlying [HttpContext] for a request.
The [HttpContext] object contains request metadata such as the HTTP headers, authorization context, and the actual path and query string for the request.

To access the [HttpContext], the MCP server should add the [IHttpContextAccessor] service to the application service collection (typically in Program.cs).
Then any classes, for example, a class containing MCP tools, should accept an [IHttpContextAccessor] in their constructor and store this for use by its methods.
Methods then use the [HttpContext property][IHttpContextAccessor.HttpContext] of the accessor to get the current context.

[HttpContext]: https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.httpcontext
[IHttpContextAccessor]: https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.ihttpcontextaccessor
[IHttpContextAccessor.HttpContext]: https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.ihttpcontextaccessor.httpcontext

The following code snippet illustrates how to add the [IHttpContextAccessor] service to the application service collection:

[C# source excerpt: Program.cs](samples/httpcontext-Program.cs)]

Any class that needs access to the [HttpContext] can accept an [IHttpContextAccessor] in its constructor and store it for later use.
Methods of the class can then access the current [HttpContext] using the stored accessor.

The following code snippet shows the `ContextTools` class accepting an [IHttpContextAccessor] in its primary constructor
and the `GetHttpHeaders` method accessing the current [HttpContext] to retrieve the HTTP headers from the current request.

[C# source excerpt: ContextTools.cs](samples/httpcontext-ContextTools.cs)]

### SSE transport and stale HttpContext

When using the legacy SSE transport, be aware that the `HttpContext` returned by `IHttpContextAccessor` references the long-lived SSE connection request — not the individual `POST` request that triggered the tool call. This means:

- The `HttpContext.User` might contain stale claims if the client's token was refreshed after the SSE connection was established.
- Request headers, query strings, and other per-request metadata will reflect the initial SSE connection, not the current operation.

The Streamable HTTP transport does not have this issue because each tool call is its own HTTP request, so `IHttpContextAccessor.HttpContext` always reflects the current request. In [stateless](stateless.md) mode, this is guaranteed since every request creates a fresh server context.

<!-- mlc-disable-next-line -->
> [!NOTE]
> The server validates that the user identity has not changed between the session-initiating request and subsequent requests (using the `sub`, `NameIdentifier`, or `UPN` claim). If the user identity changes, the request is rejected with `403 Forbidden`. However, other claims (roles, permissions, custom claims) are not re-validated and might become stale over the lifetime of a session.

[^csharp-sdk-doc]: Official C# SDK documentation source.
