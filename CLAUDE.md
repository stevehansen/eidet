# CLAUDE.md — Eidet

> Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.

## What Is Eidet?

A standalone local service that gives AI coding agents persistent, semantic memory across sessions.

*From "eidetic" — relating to extraordinarily vivid, detailed recall.*

## Specs

All design decisions are documented in `docs/specs/`:

| Spec | What it covers |
|------|---------------|
| [CoreSpec.md](docs/specs/CoreSpec.md) | Domain model, 4 memory types (Observation/Insight/Procedure/Heuristic), Docker-like layers, tiered loading (L0/L1/L2), scoring, hybrid retrieval, write gates (secret scanner + signal gate), consolidation, FadeMem differential decay, maintenance pipeline, design decisions with research citations |
| [ServiceSpec.md](docs/specs/ServiceSpec.md) | Local daemon (Windows Service/launchd/systemd), MCP server (stdio + HTTP), REST API, RavenDB (embedded OR external), rich TUI (`eidet setup`, `eidet doctor`), Docker container integration, installation, updates, dependency philosophy, CLI interface |
| [SyncSpec.md](docs/specs/SyncSpec.md) | *(Future — not MVP)* Remote sync, append-only events (CRDT-like), E2E encryption, team sharing, 3 backend options (SaaS/self-hosted/orchestrator-only), Bitwarden model |
| [IntegrationSpec.md](docs/specs/IntegrationSpec.md) | Claude Code, Claude Desktop, Cursor, TerminalHost, Docker/devcontainers, CI/CD, web UI, client SDKs |

## MVP Scope

Local-only. No team/sync/remote yet.

- Local RavenDB (embedded or connect to existing instance)
- Local optional Ollama enrichment
- MCP server (stdio for AI clients, HTTP for network)
- REST API (for TerminalHost and other tools)
- Rich TUI for setup, configuration, troubleshooting
- Docker container integration guidance
- Full 13-tool MCP surface
- System service (always running)

## Tech Stack

- **.NET 8** (or latest LTS)
- **RavenDB** — hybrid search (vector + full-text + metadata in one round-trip), built-in embeddings
- **Spectre.Console** — rich TUI
- **Microsoft.Extensions.Hosting** — service hosting, DI, configuration
- Minimal dependencies. See ServiceSpec.md "Dependency Philosophy" section.

## Project Structure

```
eidet/
├── src/
│   ├── Eidet.Core/               # Core library (domain, services, indexes)
│   ├── Eidet.Service/            # System service (MCP, REST, scheduler, TUI)
│   └── Eidet.Sync/              # Sync adapters (future)
├── tests/
│   ├── Eidet.Core.Tests/
│   └── Eidet.Service.Tests/
└── docs/
    └── specs/
```

## Key Design Decisions

- **RavenDB dual mode**: External (recommended for devs who already run RavenDB) vs Embedded (zero-setup). Auto-detected in `eidet setup`.
- **Append-only with validity intervals**: No deletes, full audit trail, trivially syncable (future).
- **Zero-LLM write path**: Deterministic stores. Ollama only for optional background enrichment.
- **Typed memories with per-type budgets**: ENGRAM research shows +30pp vs single bucket.
- **Docker-like layers**: Local (rw) + Shared (ro) + Base (ro). Writes always local.
- **Secret scanning gate**: Runs locally before ANY storage. Cannot be disabled.
- **< 600 token wake-up**: L0 identity + L1 top-20 dense-packed context.

## Links

- **GitHub**: https://github.com/stevehansen/eidet (private)
- **Domain**: eidet.dev
