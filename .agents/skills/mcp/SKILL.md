---
name: mcp
description: Build, extend, review, test, or deploy Model Context Protocol (MCP) servers with the official C# SDK. Use for C#/.NET MCP server projects using ModelContextProtocol, ModelContextProtocol.AspNetCore, MCP tools, resources, prompts, Streamable HTTP, stdio, authorization, server-to-client interaction, task execution, or MCP Apps.
---

# C# MCP server

Build the smallest server that meets the requested transport and capability needs. Prefer the high-level hosting API and attribute discovery unless the project has a reason to use low-level `ModelContextProtocol.Core` APIs.

## Workflow

1. Select a package and transport in [getting started](reference/getting-started.md) and [transports](reference/transports.md).
2. For HTTP, choose the session model before designing features. Start stateless; read [stateless and stateful](reference/stateless.md).
3. Add the user-facing surface: [tools](reference/tools.md), [resources](reference/resources.md), and/or [prompts](reference/prompts.md).
4. Pass `CancellationToken` through every cancellable operation. Read [cancellation](reference/cancellation.md), [progress](reference/progress.md), and [capabilities](reference/capabilities.md).
5. For client/user input on current Streamable HTTP, use [MRTR](reference/mrtr.md), not deprecated server-to-client APIs.
6. Apply [identity](reference/identity.md), [HTTP context](reference/httpcontext.md), and [filters](reference/filters.md), test with a real MCP client, then run build and tests.

## Transport decision

| Need | Choose |
| --- | --- |
| Local child process / desktop integration | stdio; protocol on stdout, logs on stderr. |
| Remote or containerized server | Streamable HTTP. |
| Horizontal scale, ordinary request/response tools | Stateless Streamable HTTP. |
| Resource subscriptions, unsolicited notifications, or legacy server-to-client requests | Stateful Streamable HTTP, with session affinity. |
| Legacy client compatibility | SSE only when required; it is legacy and stateful. |

## Reference map

| Need | Read |
| --- | --- |
| Packages, minimal stdio/HTTP server | [getting started](reference/getting-started.md) |
| Transport, hosts, CORS | [transports](reference/transports.md) |
| Full local SDK-documentation catalogue | [reference index](reference/index.md) |
| Stateless/stateful trade-offs | [stateless and stateful](reference/stateless.md) |
| Tool schemas, injection, output, errors | [tools](reference/tools.md) |
| Static/template resources and subscriptions | [resources](reference/resources.md) |
| Prompts and completions | [prompts](reference/prompts.md) |
| Filters, authorization, identity | [filters](reference/filters.md), [identity](reference/identity.md), [HTTP context](reference/httpcontext.md) |
| Cancellation, progress, pagination, capability checks | [cancellation](reference/cancellation.md), [progress](reference/progress.md), [pagination](reference/pagination.md), [capabilities](reference/capabilities.md) |
| Stateless-compatible input requests | [MRTR](reference/mrtr.md) |
| Sampling, elicitation, roots, logging | [sampling](reference/sampling.md), [elicitation](reference/elicitation.md), [roots](reference/roots.md), [logging](reference/logging.md) |
| Long-running task extension | [tasks](reference/tasks.md) |
| Experimental interactive UI | [MCP Apps](reference/apps.md) |
| Container deployment | [Docker deployment](reference/docker.md) |
| SDK support policy and experimental APIs | [versioning](reference/versioning.md), [experimental APIs](reference/experimental.md), [diagnostics](reference/list-of-diagnostics.md) |

## Non-negotiable checks

- Keep stdio stdout protocol-clean; send logs to stderr.
- Keep HTTP `AllowedHosts` exact. CORS is separate and only for intentional browser access.
- Check optional client capabilities and negotiated protocol version before using a feature.
- Do not use stateful-only behavior in a stateless HTTP handler.
- Return useful, stable tool descriptions; validate untrusted input; throw `McpException` for expected protocol errors.
- Do not expose secrets through results, logs, environment-printing tools, or inherited child-process environments.
