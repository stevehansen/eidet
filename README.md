# Eidet

> Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.

*From "eidetic" — relating to extraordinarily vivid, detailed recall.*

## What Is This?

A persistent, semantic memory system that gives AI coding agents the ability to learn and remember across sessions. Built as a standalone local service that any MCP-compatible AI client can use.

**Core philosophy**: The local service is the product. It just works. Complexity is for the app, simplicity is for the user.

## Key Properties

- **Local-first**: Fully functional offline. RavenDB (embedded or external) with built-in vector search. No cloud required.
- **Universal**: Works with Claude Code, Claude Desktop, Cursor, Windsurf, TerminalHost, or any MCP client.
- **Typed memories**: Observations, Insights, Procedures, Heuristics — each with distinct lifecycles and decay curves.
- **Docker-like layers**: Local (read-write) + Shared (read-only, team) + Base (read-only, from packs).
- **< 600 token wake-up**: L0 identity + L1 top-K context at session start. Minimal overhead.
- **Hybrid search**: Vector + full-text + metadata in a single round-trip.
- **Self-improving**: Echo/fizzle feedback loop tunes recall quality over time.
- **Privacy-absolute**: Secret scanning gate, localhost-only API, no data leaves the machine.
- **Always running**: System service (Windows Service / macOS launchd / Linux systemd) with auto-update.
- **Rich TUI**: Interactive setup, connection testing, troubleshooting — built for developers AND AI agents.

## Quick Start

```bash
# Install and start the service
eidet install

# Interactive setup (TUI) — detects RavenDB, configures connections, tests everything
eidet setup

# Verify it's running
eidet status

# Add to Claude Code MCP config (auto-detected during setup)
# Start a Claude Code session — memory is available immediately
```

## MVP Scope

The initial release is **local-only**:
- Local RavenDB (embedded or connect to existing instance)
- Local optional Ollama enrichment
- MCP server (stdio for AI clients)
- REST API (for TerminalHost and other tools)
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

## Origin

Extracted from [TerminalHost](https://github.com/user/TerminalHost)'s Agentic Memory system (Phases 1-7, 123 tests, 13 MCP tools). The core library (`TerminalHost.Memory`) was already a standalone .NET class library with zero project dependencies — this project wraps it in a service host.

## Project Structure

```
eidet/
├── src/
│   ├── Eidet.Core/               # Core library (domain, services, indexes)
│   ├── Eidet.Service/            # System service (MCP, REST, scheduler)
│   └── Eidet.Sync/              # Sync adapters (future)
├── tests/
│   ├── Eidet.Core.Tests/
│   └── Eidet.Service.Tests/
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

## Distribution

| Surface | Name |
|---------|------|
| Binary | `eidet` |
| NuGet | `Eidet` / `Eidet.Core` |
| npm (future) | `@eidet/sdk` |
| Homebrew | `eidet` |
| GitHub | `eidet` |
| Docker | `ghcr.io/eidet/eidet-server` (future) |

## License

TBD
