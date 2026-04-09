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

## Implementation Status

### Phase 1 — Done
- Solution structure (Eidet.Core, Eidet.Service, test projects)
- Domain model: MemoryEntry, 4 types, Validity, MemoryLink, MemoryLayer, EidetPack
- RavenDB Memories/Search index (vector + full-text + Corax, Result projection)
- Write gates: SecretScanner (10 patterns), SignalGate (17 low-signal phrases, self-talk detection), WriteGate
- Entity extraction: 9 regex patterns, validation, heuristic one-liner generation
- Configuration model with StorageMode enum
- CLI: `eidet doctor` (rich TUI + JSON), `eidet status`
- 71 unit tests

### Phase 2 — Done
- **MemoryService**: Store (gates + entities + ID + duplicate detection + supersession), Recall (parallel full-text + vector, scoring, type diversity budgets, staleness warnings, LRU cache), Context (L0 identity + L1 top-K with type budgets), Forget (soft-delete + audit trail), Feedback (echo/fizzle), History (version chain)
- **REST API**: HttpListener-based on localhost:19380 — `/api/health`, `/api/eidet/context`, `/api/eidet/search`, `/api/eidet` (store), `/api/eidet/{id}` (get/delete), `/api/eidet/feedback`, `/api/eidet/history/{id}`, `/api/eidet/stats`
- **Code review fixes**: Single DocumentStore in doctor, StorageMode enum, shared version constant, EnvVar regex precision, removed redundant RegexOptions.Compiled, index name constant

### Phase 3 — Next
- `eidet setup` interactive wizard (TUI)
- MCP server (stdio bridge + streamable HTTP)
- `eidet serve` as system service (Windows Service/launchd/systemd)
- Intake system (CLAUDE.md, README, deps)
- Consolidation pipeline
- Ollama enrichment

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

- **.NET 10** (latest SDK)
- **RavenDB 7.x** — hybrid search (vector + full-text + metadata in one round-trip), built-in embeddings (bge-micro-v2)
- **Spectre.Console 0.55** — rich TUI + CLI commands
- **HttpListener** — lightweight REST API (no ASP.NET dependency)
- Minimal dependencies. See ServiceSpec.md "Dependency Philosophy" section.

## Project Structure

```
eidet/
├── src/
│   ├── Eidet.Core/               # Core library
│   │   ├── Configuration/        # EidetConfig, ConfigManager, StorageMode
│   │   ├── Domain/               # MemoryEntry, MemoryType, Validity, layers, links, packs
│   │   ├── Gates/                # SecretScanner, SignalGate, WriteGate
│   │   ├── Indexes/              # Memories_Search (hybrid vector + full-text)
│   │   ├── Services/             # MemoryService, EntityExtractor, StoreResult
│   │   └── Storage/              # IEidetStore, RavenEidetStore, DocumentStoreFactory
│   ├── Eidet.Service/            # CLI + REST API
│   │   ├── Api/                  # EidetApiServer (HttpListener)
│   │   └── Commands/             # serve, doctor, status
│   └── Eidet.Sync/              # Sync adapters (future)
├── tests/
│   ├── Eidet.Core.Tests/        # 71 tests
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
- **HttpListener over ASP.NET**: Lightweight, zero extra dependencies, matches TerminalHost approach.
- **Cross-checked with TerminalHost.Memory**: Domain model, indexes, write gates, entity extraction all verified against the reference implementation.

## API Quick Reference

```bash
# Health check
curl http://localhost:19380/api/health

# Get L0+L1 context for a repo
curl "http://localhost:19380/api/eidet/context?repo=P%3A%5CEidet"

# Search memories
curl "http://localhost:19380/api/eidet/search?repo=P%3A%5CEidet&q=RavenDB&limit=10"

# Store a memory
curl -X POST http://localhost:19380/api/eidet \
  -H "Content-Type: application/json" \
  -d '{"repo":"P:\\Eidet","content":"...","type":"observation"}'

# Feedback (echo/fizzle)
curl -X POST http://localhost:19380/api/eidet/feedback \
  -H "Content-Type: application/json" \
  -d '{"memoryId":"memories/P--Eidet/insight/abc123","wasUsed":true}'
```

## Links

- **GitHub**: https://github.com/stevehansen/eidet (private)
- **Domain**: eidet.dev
