---
type: SDK Documentation
title: "Index"
description: "Local copy of the C# MCP SDK documentation page Index."
resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/index.md"
tags: [mcp, csharp, sdk, documentation]
sources:
  - id: csharp-sdk-doc
    resource: "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/index.md"
    title: "Index"
generated: { by: "codex/gpt-5.6", at: "2026-08-14T00:00:00Z" }
status: stable
---

This is a normalized local copy of the official C# SDK documentation page.[^csharp-sdk-doc]
# Conceptual documentation

Welcome to the conceptual documentation for the Model Context Protocol SDK. Here you'll find high-level overviews, explanations, and guides to help you understand how the SDK implements the Model Context Protocol.

## Contents

### Getting started

To install the SDK and build your first MCP client and server, see [Getting started](getting-started.md).

### Deployment

| Title | Description |
| - | - |
| [Docker deployment](docker.md) | Learn how to package and run ASP.NET Core MCP servers in Docker containers using Streamable HTTP transport. |

### Base protocol

| Title | Description |
| - | - |
| [Capabilities](capabilities.md) | Learn how client and server capabilities are negotiated during initialization, including protocol version negotiation. |
| [Transports](transports.md) | Learn how to configure stdio, Streamable HTTP, and SSE transports for client-server communication. |
| [Stateless and Stateful](stateless.md) | Learn when to use stateless vs. stateful mode for HTTP servers and how to configure sessions. |
| [Ping](ping.md) | Learn how to verify connection health using the ping mechanism. |
| [Progress tracking](progress.md) | Learn how to track progress for long-running operations through notification messages. |
| [Cancellation](cancellation.md) | Learn how to cancel in-flight MCP requests using cancellation tokens and notifications. |
| [Multi Round-Trip Requests (MRTR)](mrtr.md) | Learn how servers request client input during tool execution using input-required results and retries. |

### Client features

| Title | Description |
| - | - |
| [Sampling](sampling.md) | Learn how servers request LLM completions from the client using the sampling feature. |
| [Roots](roots.md) | Learn how clients provide filesystem roots to servers for context-aware operations. |
| [Elicitation](elicitation.md) | Learn how to request additional information from users during interactions. |

### Server features

| Title | Description |
| - | - |
| [Tools](tools.md) | Learn how to implement and consume tools that return text, images, audio, and embedded resources. |
| [Resources](resources.md) | Learn how to expose and consume data through MCP resources, including templates and subscriptions. |
| [Prompts](prompts.md) | Learn how to implement and consume reusable prompt templates with rich content types. |
| [Completions](completions.md) | Learn how to implement argument auto-completion for prompts and resource templates. |
| [Logging](logging.md) | Learn how to implement logging in MCP servers and how clients can consume log messages. |
| [Pagination](pagination.md) | Learn how to use cursor-based pagination when listing tools, prompts, and resources. |
| [HTTP Context](httpcontext.md) | Learn how to access the underlying `HttpContext` for a request. |
| [MCP Server Handler Filters](filters.md) | Learn how to add filters to the handler pipeline. Filters let you wrap the original handler with additional functionality. |

### Extensions

| Title | Description |
| - | - |
| [MCP Apps](apps.md) | Learn how to use the MCP Apps extension to deliver interactive UIs from MCP servers. |
| [Tasks](tasks.md) | Learn how to use task-based execution for long-running operations that can be polled for status and results. |
| [Identity and Roles](identity.md) | Learn how to access caller identity and roles in MCP tool, prompt, and resource handlers. |

[^csharp-sdk-doc]: Official C# SDK documentation source.
