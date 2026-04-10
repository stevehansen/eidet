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

### Phase 6 — Done
- **`eidet config get/set/list`**: CLI command for reading/writing all config values via dotted path notation (e.g., `storage.ravenUrl`, `enrichment.ollamaEnabled`). Case-insensitive keys, `--json` output for `list`. Invariant culture for float parsing.
- **`eidet instructions`**: Generate CLAUDE.md memory usage instructions. `--print` (stdout, default), `--install` (append to `~/.claude/CLAUDE.md`), `--project` (create in project root). Idempotent — uses HTML markers to replace existing sections on re-run.
- **Ollama model management**: `OllamaService` in Core — list models, pull with streaming progress, suggest best model, check availability. `eidet ollama status/pull/list` CLI commands. `RecommendedModels` list (gemma4, gemma3, llama3.2, phi4, qwen3). Auto-enables enrichment after first pull. Spectre.Console progress bar for downloads.
- **Layer auto-mount on pack import**: `ExportService.ImportPackWithLayerAsync` — imports pack entries and auto-mounts as Base layer via LayerService. Layer ID uses `bundle:{packId}` convention.
- **`eidet docker`**: Docker/devcontainer integration guide. Shows devcontainer.json, Dockerfile, MCP config snippets. Detects container environment (/.dockerenv, DOTNET_RUNNING_IN_CONTAINER, /proc/1/cgroup). `--json` for programmatic use.
- **`eidet update`**: Self-update via GitHub Releases API. `--check` (version check only), `--json`, `--force`. Platform-aware asset download (win-x64, osx-arm64, linux-x64). Binary backup/replace with rollback on failure.
- **Test coverage**: 157 → 186 tests. Added ConfigHelper (10), InstructionsCommand (5), OllamaService (12), DockerCommand (1), UpdateCommand (1).

### Phase 6.5 — Polish (Done)
- **GetDistinctRepoIdsAsync**: New `IEidetStore` method for querying distinct RepoId values. Implemented in `RavenEidetStore` using index projection. Replaced MaintenanceScheduler placeholder — scheduler now discovers all active repos automatically.
- **Environment variable overrides**: `ConfigManager.Load()` applies env var overrides after loading config: `EIDET_API_URL` (bind address + port), `EIDET_RAVEN_URL`, `EIDET_OLLAMA_URL`, `EIDET_OLLAMA_MODEL`. Enables container and CI/CD configuration without config files.
- **Auto-intake on first session**: MCP `eidet_context` triggers automatic intake (CLAUDE.md, README, etc.) when no memories exist for the repo. Controlled by `memory.autoIntakeOnFirstSession` config. Only runs once per MCP session.
- **`eidet install` auto-configures MCP clients**: Detects Claude Code (`~/.claude/`) and Claude Desktop (`%APPDATA%\Claude\`), auto-creates `claude_desktop_config.json` with Eidet MCP server entry. Idempotent — skips if already configured.
- **Graceful shutdown**: `eidet serve` handles Ctrl+C/SIGTERM cleanly — stops scheduler, disposes enrichment, closes RavenDB store, stops HttpListener. Uses `CancellationTokenSource.CreateLinkedTokenSource`.
- **Test coverage**: 186 → 193 tests. Added ConfigManager defaults (7), InstallCommand MCP config (1), verified existing patterns.

### Phase 7 — Done
- **RavenDB Embedded mode**: `DocumentStoreFactory.CreateEmbedded()` and `CreateFromConfig()` — starts embedded RavenDB server from `RavenDB.Embedded` NuGet, manages lifecycle. All commands now use `CreateFromConfig` (auto-selects embedded vs external). `eidet setup --embedded` provisions indexes and embeddings. `eidet doctor` tests embedded mode. Default data dir: `~/.eidet/data/raven` (Unix) or `%APPDATA%\Eidet\data\raven` (Windows).
- **API key authentication**: `AuthConfig` with `Enabled`, `RequireForNonLocalhost`, `ApiKeys` list. `ApiKeyService` in Core: SHA256 key hashing, scope validation (`read:all`, `write:observations`, `write:all`, `admin`), `admin` implies all scopes, `write:all` implies `write:observations`. `EidetApiServer` auth middleware: checks `Authorization: Bearer` header, validates key, checks scope. Health/status endpoints exempt. CORS headers (`Access-Control-Allow-Origin: *`) + OPTIONS preflight handling.
- **`eidet api-key create/list/revoke`**: CLI commands for key management. Creating first key auto-enables auth. Revoking last key auto-disables. `--scopes` flag, `--json` output.
- **Network binding guard**: `eidet serve` refuses to start on non-localhost without auth enabled. Configurable via `auth.requireForNonLocalhost`.
- **Test coverage**: 193 → 220 tests. Added ApiKeyService (16), AuthConfig (3), DocumentStoreFactory (4), ConfigHelper auth keys (4).

### Phase 8 — Done
- **Local Web UI**: SPA served at `http://localhost:19380/ui` from embedded resources. Dark-themed, responsive. 5 pages:
  - **Dashboard**: Repo selector, memory counts by type, recent memories list.
  - **Memory Browser**: Full-text search + browse with type filter, paginated results, detail panel (content, entities, tags, importance, confidence, provenance, echo/fizzle counts).
  - **Knowledge Graph**: Canvas-based force-directed graph. Nodes colored by type (blue=observation, purple=insight, green=procedure, orange=heuristic), sized by importance. Edges from DerivedFrom/Links. Interactive drag, hover tooltips.
  - **Timeline**: Chronological view grouped by date, type badges, tag chips.
  - **Settings**: Service status display (version, uptime, database info). Action buttons: intake, consolidate, maintenance, export.
- **New API endpoints**: `GET /api/eidet/repos` (list all repo IDs), `GET /api/eidet/browse` (paginated browse with type filter, no search query required), `GET /api/eidet/graph` (graph data — nodes + edges for visualization).
- **BrowseAsync**: New `IEidetStore.BrowseAsync` method for paginated memory listing by repo, ordered by creation date. `MemoryService.BrowseAsync` and `GetGraphDataAsync` wrappers.
- **GraphData domain types**: `GraphData`, `GraphNode`, `GraphEdge` — compact graph representation with type, label, importance, edges.
- **Embedded resource serving**: Static files compiled into assembly via `<EmbeddedResource>`. Served from `EidetApiServer` for `/ui/*` routes. MIME type mapping for HTML/CSS/JS/SVG/PNG. Path traversal protection.
- **UI routes exempt from auth**: `/ui` and `/ui/*` paths are public (no API key required) in `ApiKeyService.GetRequiredScope`.
- **Test coverage**: 220 → 233 tests. Added GraphData (4), WebUI embedded resources (5), ApiKeyService UI scope (2).

### Phase 9 — Done
- **TypeScript SDK** (`sdk/typescript/`): `@eidet/sdk` npm package. `EidetClient` class wrapping all REST endpoints with full TypeScript types. ESM module, zero runtime dependencies (uses native `fetch`). Methods: `store`, `recall`, `context`, `browse`, `graph`, `repos`, `forget`, `feedback`, `history`, `intake`, `consolidate`, `maintenance`, `exportMarkdown`, `health`, `status`. `EidetError` for HTTP errors. Supports API key auth.
- **Python SDK** (`sdk/python/`): `eidet-sdk` pip package. `EidetClient` class using `httpx`. Full type hints, context manager support. `MemoryType` enum, `StoreRequest` dataclass. Methods mirror TypeScript SDK. `EidetError` exception. Python 3.10+.
- **C# SDK** (`sdk/dotnet/Eidet.Sdk/`): `Eidet.Sdk` NuGet package targeting `net8.0`. `EidetClient` (IDisposable) using `HttpClient` + `System.Text.Json`. Full record types for all request/response models (`StoreRequest`, `MemoryEntry`, `SearchResult`, `BrowseResponse`, `GraphData`, etc.). `EidetException` for HTTP errors. CancellationToken support throughout. `IsAvailableAsync` health check.

### Phase 9.5 — Hooks System (Done)
- **HookRunner**: `IHookRunner` interface with `HookRunner` (real) and `NullHookRunner` (zero-overhead no-op). Runs external commands via `Process.Start`, passes JSON context on stdin, captures stdout/stderr. Configurable timeout per hook with process tree kill on timeout.
- **Hook events**: 6 lifecycle points — `PreStore`, `PostStore`, `PreRecall`, `PostRecall`, `PreForget`, `PostForget`. Pre-hooks can reject (non-zero exit code, stderr as reason). Post-hooks are fire-and-forget.
- **HooksConfig**: Per-event hook lists in `EidetConfig`. Each `HookDefinition` has `Command`, `TimeoutSeconds` (default 10), `Enabled` (default true).
- **MemoryService integration**: Hooks wired into Store (pre/post), Recall (pre/post), Forget (pre/post). Pre-hook rejection returns `StoreResult.Rejected` / empty results / false. Post-hooks don't block the caller.
- **All entry points**: ServeCommand, McpCommand, StoreCommand, RecallCommand all construct `HookRunner` from config when hooks are defined.
- **Config visibility**: `eidet config list` shows hook counts per event.
- **Test coverage**: 233 → 253 tests. Added HookRunner (15): NullHookRunner, ParseCommand, HasHooks, HookEvent mapping, defaults, HookContext, HookResult.

### Phase 10 — Production Readiness (Done)
- **CI/CD Pipeline**: GitHub Actions workflows — `ci.yml` (build + test on push/PR, matrix: Windows/Ubuntu/macOS, NuGet caching), `release.yml` (on `v*` tag: build self-contained binaries for win-x64/osx-arm64/linux-x64, create GitHub Release with changelog, publish NuGet/npm/PyPI SDK packages). Version validation against `EidetVersion.Current`.
- **Docker**: `Dockerfile` (multi-stage, self-contained single-file publish on `runtime-deps:10.0`), `docker-compose.yml` (Eidet + optional RavenDB external + optional Ollama via profiles), `.dockerignore`. New env var overrides: `EIDET_STORAGE_MODE`, `EIDET_DATA_DIR`, `EIDET_AUTH_REQUIRE_NONLOCALHOST`.
- **Memory Quality Dashboard**: `QualityService` with 8 checks (stale memories, high-fizzle, potential conflicts, orphan observations, tag concentration, type imbalance, low-confidence, missing entities). Overall score 0.0–1.0. `QualityReport` model with issues + breakdown. CLI: `eidet quality --repo ... --json`. API: `GET /api/eidet/quality?repo=...`.
- **Backup/Restore**: `BackupService` using RavenDB Smuggler API. `.eidetbackup` format (ZIP: `backup.ravendbdump` + `manifest.json` with SHA256 checksum). CLI: `eidet backup create/restore/list/prune`. `BackupConfig`: `backupDir`, `retainCount` (default 10), `autoBackupIntervalHours`.
- **Integration Tests**: `Eidet.Integration.Tests` project with `EidetApiFixture` (starts real API server on random port with embedded RavenDB, unique database per test class). Tests: health/status, store/recall lifecycle, context, browse, repos, quality, feedback, secret rejection. Uses `SkippableFact` for environments without RavenDB Embedded.
- **Test coverage**: 253 → 272+ unit tests. Added QualityService (7), BackupService (11). Plus ~11 integration tests (skippable).

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
│   │   ├── Domain/               # MemoryEntry, MemoryType, Validity, layers, links, packs, GraphData
│   │   ├── Gates/                # SecretScanner, SignalGate, WriteGate
│   │   ├── Indexes/              # Memories_Search, Memories_CountByType
│   │   ├── Services/             # MemoryService, EntityExtractor, LayerService, OllamaService, ApiKeyService, HookRunner, QualityService, BackupService, enrichment (IEnrichmentService, OllamaEnrichmentService, NullEnrichmentService)
│   │   ├── StringUtils.cs        # Shared string helpers (Truncate)
│   │   └── Storage/              # IEidetStore, RavenEidetStore, DatabaseProvisioner, DocumentStoreFactory (embedded + external)
│   ├── Eidet.Service/            # CLI + REST API + system service
│   │   ├── Api/                  # EidetApiServer (HttpListener + MCP HTTP + embedded Web UI)
│   │   ├── Commands/             # setup, mcp, serve, doctor, status, recall, store, stats, export, intake, maintain, quality, backup, install, uninstall, config, instructions, ollama, docker, update, api-key
│   │   ├── Mcp/                  # McpServer (stdio + HTTP), McpModels, McpToolDefinitions
│   │   ├── Scheduler/            # MaintenanceScheduler (background timers)
│   │   └── wwwroot/              # Web UI SPA (index.html, app.css, app.js) — embedded resources
│   └── Eidet.Sync/              # Sync adapters (future)
├── sdk/
│   ├── typescript/              # @eidet/sdk npm package (EidetClient, full types)
│   ├── python/                  # eidet-sdk pip package (EidetClient, httpx, type hints)
│   └── dotnet/Eidet.Sdk/       # Eidet.Sdk NuGet package (EidetClient, record types)
├── tests/
│   ├── Eidet.Core.Tests/        # 217 tests
│   ├── Eidet.Service.Tests/     # 55 tests
│   └── Eidet.Integration.Tests/ # ~11 integration tests (skippable, needs embedded RavenDB)
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
- **API key auth with scope model**: Bearer token auth, SHA256 hashed keys in config, 4 scopes (read:all, write:observations, write:all, admin). Health/status always public. Network binding guard prevents non-localhost without auth.
- **CORS enabled**: All responses include `Access-Control-Allow-Origin: *` for browser/Web UI access.
- **Embedded Web UI**: SPA compiled as embedded resources — no external files to manage, ships with the binary. Vanilla HTML/CSS/JS with canvas-based graph (no framework dependencies). Dark theme, responsive.
- **Hooks system**: Claude Code-inspired lifecycle hooks. External commands receive JSON context on stdin, pre-hooks gate operations (non-zero exit = reject), post-hooks fire-and-forget. Configurable per-event with timeout and enable/disable. Zero overhead when no hooks configured (NullHookRunner).

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

# Browse memories (paginated, no search query)
curl "http://localhost:19380/api/eidet/browse?repo=P%3A%5CEidet&skip=0&take=50&type=insight"

# List all repos
curl http://localhost:19380/api/eidet/repos

# Graph data for visualization
curl "http://localhost:19380/api/eidet/graph?repo=P%3A%5CEidet&limit=200"

# Web UI
# Open http://localhost:19380/ui in browser
```

## Links

- **GitHub**: https://github.com/stevehansen/eidet (private)
- **Domain**: eidet.dev
