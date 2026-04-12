# Eidet

[![CI](https://github.com/stevehansen/eidet/actions/workflows/ci.yml/badge.svg)](https://github.com/stevehansen/eidet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/eidet?label=dotnet%20tool)](https://www.nuget.org/packages/eidet)
[![NuGet SDK](https://img.shields.io/nuget/v/Eidet.Sdk?label=Eidet.Sdk)](https://www.nuget.org/packages/Eidet.Sdk)
[![npm](https://img.shields.io/npm/v/@eidet/sdk)](https://www.npmjs.com/package/@eidet/sdk)
[![PyPI](https://img.shields.io/pypi/v/eidet-sdk)](https://pypi.org/project/eidet-sdk/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

> Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.

*From "eidetic" — relating to extraordinarily vivid, detailed recall.*

## What Is This?

A persistent, semantic memory system that gives AI coding agents the ability to learn and remember across sessions. Built as a standalone local service that any MCP-compatible AI client can use.

**Core philosophy**: The local service is the product. It just works. Complexity is for the app, simplicity is for the user.

## Quick Start

```bash
# Install (requires .NET 10 SDK)
dotnet tool install -g eidet

# Interactive setup — configure RavenDB (embedded or external), Ollama, embeddings
eidet setup

# Register as system service + auto-configure MCP for Claude Code & Desktop
eidet install

# Verify everything is running
eidet status
```

After `eidet install`, the service runs in the background and your MCP client (Claude Code, Claude Desktop) can use Eidet's memory tools immediately.

## Key Properties

- **Local-first**: Fully functional offline. RavenDB (embedded or external) with built-in vector search. No cloud required.
- **Universal**: Works with Claude Code, Claude Desktop, Cursor, Windsurf, TerminalHost, or any MCP client.
- **Typed memories**: Observations, Insights, Procedures, Heuristics — each with distinct lifecycles and decay curves.
- **Docker-like layers**: Local (read-write) + Shared (read-only, team) + Base (read-only, from packs).
- **< 600 token wake-up**: L0 identity + L1 top-K context at session start. Minimal overhead.
- **Hybrid search**: Vector + full-text + metadata in a single round-trip.
- **Self-improving**: Echo/fizzle feedback loop tunes recall quality over time.
- **Privacy-absolute**: Secret scanning gate, localhost-only API, no data leaves the machine.
- **Always running**: System service (scheduled task / launchd / systemd) with managed updates.
- **Rich TUI**: Interactive setup, connection testing, troubleshooting — built for developers AND AI agents.

## Updates & Feedback

```bash
# Check for updates
eidet update --check

# Update (stops service, updates tool, restarts service)
eidet update

# Report an issue (opens GitHub with version pre-filled)
eidet feedback
```

## Distribution

| Channel | Package | What |
|---------|---------|------|
| **NuGet** (dotnet tool) | [`eidet`](https://www.nuget.org/packages/eidet) | CLI + MCP server + REST API + system service |
| **NuGet** (library) | [`Eidet.Sdk`](https://www.nuget.org/packages/Eidet.Sdk) | C# client SDK |
| **npm** | [`@eidet/sdk`](https://www.npmjs.com/package/@eidet/sdk) | TypeScript client SDK |
| **PyPI** | [`eidet-sdk`](https://pypi.org/project/eidet-sdk/) | Python client SDK |
| **GitHub Releases** | [Standalone binaries](https://github.com/stevehansen/eidet/releases) | Self-contained for Docker / non-.NET |
| **Docker** | `eidet/eidet:latest` | Container image |

## MVP Scope

The initial release is **local-only**:
- Local RavenDB (embedded or connect to existing instance)
- Local optional Ollama enrichment
- MCP server (stdio for AI clients)
- REST API (for tools and custom integrations)
- Rich TUI for setup, configuration, troubleshooting
- Docker container integration (devcontainers, custom containers)
- Full 13-tool MCP surface

Team sync, remote backup, and collaboration features are designed but deferred to a future release. See [SyncSpec](docs/specs/SyncSpec.md) for the planned architecture.

## Documentation

| Document | Description |
|----------|-------------|
| [Core Spec](docs/specs/CoreSpec.md) | Memory types, layers, tiered loading, scoring, write gates, consolidation, maintenance, design decisions |
| [Service Spec](docs/specs/ServiceSpec.md) | Local daemon, MCP/REST hosting, RavenDB, installation, TUI, Docker, configuration |
| [Sync Spec](docs/specs/SyncSpec.md) | *(Future)* Remote sync, E2E encryption, team sharing, orchestrator options |
| [Integration Spec](docs/specs/IntegrationSpec.md) | Claude Code, Claude Desktop, TerminalHost, Docker containers, CI/CD |

## Project Structure

```
eidet/
├── src/
│   ├── Eidet.Core/               # Core library (domain, services, indexes)
│   ├── Eidet.Service/            # System service (MCP, REST, scheduler)
│   └── Eidet.Sync/              # Sync adapters (future)
├── sdk/
│   ├── typescript/              # @eidet/sdk
│   ├── python/                  # eidet-sdk
│   └── dotnet/Eidet.Sdk/       # Eidet.Sdk
├── tests/
│   ├── Eidet.Core.Tests/
│   ├── Eidet.Service.Tests/
│   └── Eidet.Integration.Tests/
└── docs/
    └── specs/
```

## Branded Concepts

| Concept | Description |
|---------|-------------|
| `eidet recall` | Search memories by meaning, keywords, or filters |
| `eidet store` | Persist an observation, insight, procedure, or heuristic |
| `eidet layers` | Docker-like layer stack (local, shared, base) |
| `eidet pack` | Exportable knowledge packs (`.eidet` files) |
| `eidet context` | L0+L1 session context injection |
| `eidet setup` | Interactive TUI for first-time configuration |
| `eidet doctor` | Connection testing, troubleshooting, health checks |

## License

[MIT](LICENSE)
