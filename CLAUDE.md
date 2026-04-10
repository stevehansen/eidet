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
- Unit tests (expanded to 133 in Phase 4.5)

### Phase 2 — Done
- **MemoryService**: Store (gates + entities + ID + duplicate detection + supersession), Recall (parallel full-text + vector, scoring, type diversity budgets, staleness warnings, LRU cache), Context (L0 identity + L1 top-K with type budgets), Forget (soft-delete + audit trail), Feedback (echo/fizzle), History (version chain)
- **REST API**: HttpListener-based on localhost:19380 — `/api/health`, `/api/eidet/context`, `/api/eidet/search`, `/api/eidet` (store), `/api/eidet/{id}` (get/delete), `/api/eidet/feedback`, `/api/eidet/history/{id}`, `/api/eidet/stats`
- **Code review fixes**: Single DocumentStore in doctor, StorageMode enum, shared version constant, EnvVar regex precision, removed redundant RegexOptions.Compiled, index name constant

### Phase 3 — Done
- **`eidet setup`**: Interactive TUI wizard — detects RavenDB, creates database, deploys indexes, configures bge-micro-v2 embeddings, detects Ollama, saves config. Non-interactive mode with `--non-interactive`.
- **`eidet mcp`**: MCP server over stdio (JSON-RPC). Direct MemoryService integration (no HTTP hop).
- **DatabaseProvisioner**: EnsureDatabaseExists, DeployIndexes, EnsureEmbeddingsConfigured (AI connection string + embeddings generation task).
- **SecretScanner**: Added Azure storage, GCP service account, Slack token patterns (10→13).

### Phase 4 — Done
- **Full 13-tool MCP surface**: eidet_store, eidet_recall, eidet_context, eidet_forget, eidet_feedback, eidet_history, eidet_intake, eidet_link, eidet_consolidate, eidet_maintenance, eidet_export, eidet_pack_export, eidet_pack_import
- **IntakeService**: Ingests CLAUDE.md, MEMORY.md, README.md, .editorconfig, NuGet/npm deps. Splits by headings, deduplicates by content hash, extracts entities and one-liners.
- **ConsolidationService**: Groups observations by tag overlap (union-find), creates insights from groups of 3+ (or boosts existing insights if topic already covered via vector similarity > 0.85), FadeMem differential decay (per-type half-lives).
- **MaintenanceService**: 7-stage pipeline — TTL expiry, observation retention, dedup sweep (Jaccard 0.85), importance decay, orphan cleanup, backfill enrichment (entities + one-liners), auto-consolidation.
- **ExportService**: Markdown export, .eidet pack export/import with session field stripping.
- **REST API expanded**: /api/eidet/intake, /api/eidet/consolidate, /api/maintenance, /api/eidet/export, /api/status, /api/eidet/links, /api/eidet/packs/export, /api/eidet/packs/import
- **Recall access tracking**: Bumps access count on local memories during recall (spec compliance).

### Phase 4.5 — Polish (Done)
- **Test coverage**: 77 → 133 tests. Added tests for IntakeService (SplitByHeadings), ConsolidationService (GroupByTagOverlap, transitive merge, case-insensitive), MaintenanceService (Jaccard word similarity), FadeMem decay math (type hierarchy, confidence adjustment, floor), MCP tool definitions (all 13, schemas, required fields), StringUtils.
- **Shared StringUtils**: Extracted duplicated `Truncate` helper from ExportService and McpServer into `Eidet.Core.StringUtils`.
- **InternalsVisibleTo**: Eidet.Core exposes internals to Eidet.Core.Tests for testing static/internal helpers.
- **Hybrid retrieval over-fetch 2×**: Full-text search now fetches 2× limit for better merge quality (spec compliance).
- **CLI memory commands**: `eidet recall`, `eidet store`, `eidet stats`, `eidet export`, `eidet intake`, `eidet maintain` — all with `--json` support.

### Phase 5 — Done
- **OllamaEnrichmentService**: IEnrichmentService interface with NullEnrichmentService (zero-overhead no-op) and OllamaEnrichmentService (/api/chat, think:false, 120s timeout, lazy health re-check). 6 enrichment tasks: one-liner, summary, foresight hint, entity extraction (LLM supplement), consolidation merge (>5 observations), conflict detection. Integrated into MaintenanceService (Stage 6b) and ConsolidationService (merge for large groups).
- **LayerService**: Mount/unmount layers, scope resolution for layer-aware recall, auto-mount by package dependencies. IEidetStore layer CRUD (StoreMountedLayer, UnmountLayer, GetMountedLayers, GetLayer). Non-local de-boost 0.8×, layer-tagged search results. REST API: GET/POST/DELETE /api/eidet/layers.
- **MCP streamable HTTP transport**: POST /mcp endpoint on EidetApiServer. Reuses McpServer.ProcessRequestAsync for JSON-RPC over HTTP. Supports all 13 tools. 204 No Content for notifications.
- **System service**: `eidet install` (Windows Service via sc.exe, macOS launchd plist, Linux systemd user unit), `eidet uninstall` (with --purge). Binary copies to ~/.eidet/bin/ (or %APPDATA%\Eidet\bin\).
- **MaintenanceScheduler**: Background timer for periodic maintenance and consolidation at configured intervals (default 24h/6h). Runs inside `eidet serve`.
- **Doctor Ollama check**: Verifies model availability (not just connectivity).
- **InternalsVisibleTo**: Eidet.Service exposes internals to Eidet.Service.Tests.
- **Test coverage**: 133 → 157 tests. Added tests for NullEnrichmentService (9), OllamaEnrichmentService (3), LayerService (8), InstallCommand (4).

### Phase 6 — Next
- Ollama model management (auto-pull, model suggestions)
- Layer import from .eidet pack files (auto-mount)
- `eidet config get/set` command
- `eidet instructions` — generate CLAUDE.md memory instructions
- `eidet docker` — Docker/devcontainer integration guidance
- `eidet update` — self-update mechanism
- Web UI for memory exploration

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
│   │   ├── Indexes/              # Memories_Search, Memories_CountByType
│   │   ├── Services/             # MemoryService, EntityExtractor, LayerService, enrichment (IEnrichmentService, OllamaEnrichmentService, NullEnrichmentService)
│   │   ├── StringUtils.cs        # Shared string helpers (Truncate)
│   │   └── Storage/              # IEidetStore, RavenEidetStore, DatabaseProvisioner
│   ├── Eidet.Service/            # CLI + REST API + system service
│   │   ├── Api/                  # EidetApiServer (HttpListener + MCP HTTP)
│   │   ├── Commands/             # setup, mcp, serve, doctor, status, recall, store, stats, export, intake, maintain, install, uninstall
│   │   ├── Mcp/                  # McpServer (stdio + HTTP), McpModels, McpToolDefinitions
│   │   └── Scheduler/            # MaintenanceScheduler (background timers)
│   └── Eidet.Sync/              # Sync adapters (future)
├── tests/
│   ├── Eidet.Core.Tests/        # 131 tests
│   └── Eidet.Service.Tests/     # 26 tests
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
