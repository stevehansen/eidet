---
title: Client SDKs
nav_order: 6
has_children: true
---

# Client SDKs

Eidet provides official client SDKs for three languages. Each is a thin wrapper around the REST API with full type support.

| SDK | Package | Install |
|-----|---------|---------|
| [TypeScript]({% link sdk/typescript.md %}) | `@eidet/sdk` | `npm install @eidet/sdk` |
| [Python]({% link sdk/python.md %}) | `eidet-sdk` | `pip install eidet-sdk` |
| [C#]({% link sdk/dotnet.md %}) | `Eidet.Sdk` | `dotnet add package Eidet.Sdk` |

All SDKs support:

- Store, recall, context, forget, feedback, history
- Browse, graph, repos listing
- Intake, consolidate, maintenance, export
- Health check and availability probe
- API key authentication
- Custom base URL

## When to Use an SDK

Use an SDK when you're building tooling around Eidet:

- **CI/CD pipelines** — store build observations, export memories for review
- **Custom integrations** — connect Eidet to your own AI agents or tools
- **Scripts** — batch operations, migrations, reporting
- **Testing** — verify memory state in integration tests
- **Web apps** — build custom UIs that interact with Eidet

For AI agent integration, use the MCP server instead — it's the native protocol for agent-to-tool communication.
