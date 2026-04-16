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
- **Full 15-tool MCP surface**: eidet_store, eidet_recall, eidet_context, eidet_forget, eidet_feedback, eidet_history, eidet_intake, eidet_link, eidet_consolidate, eidet_maintenance, eidet_export, eidet_pack_export, eidet_pack_import
- **IntakeService**: Ingests CLAUDE.md, MEMORY.md, README.md, .editorconfig, NuGet/npm deps. Splits by headings, deduplicates by content hash, extracts entities and one-liners.
- **ConsolidationService**: Groups observations by tag overlap (union-find), creates insights from groups of 3+ (or boosts existing insights if topic already covered via vector similarity > 0.85), FadeMem differential decay (per-type half-lives).
- **MaintenanceService**: 8-stage pipeline — TTL expiry, observation retention, dedup sweep (Jaccard 0.85), importance decay, orphan cleanup, enrichment cleanup (CoT stripping), backfill enrichment (entities + one-liners), auto-consolidation.
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
- **MCP streamable HTTP transport**: POST /mcp endpoint on EidetApiServer. Reuses McpServer.ProcessRequestAsync for JSON-RPC over HTTP. Supports all 15 tools. 204 No Content for notifications.
- **System service**: `eidet install` (Windows scheduled task, macOS launchd plist, Linux systemd user unit), `eidet uninstall` (with --purge). Uses dotnet tool shim path (`~/.dotnet/tools/eidet`). Auto-configures MCP for Claude Code (`~/.claude/settings.json`) and Claude Desktop.
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
- **`eidet update`**: Update via `dotnet tool update -g eidet`. Checks NuGet for latest version, stops service, updates tool, records in version history, restarts service. `--check` (version check only), `--json`, `--force`.
- **`eidet feedback`**: Opens GitHub Issues in browser with pre-filled version, OS, and runtime info.
- **Test coverage**: 157 → 186 tests. Added ConfigHelper (10), InstructionsCommand (5), OllamaService (12), DockerCommand (1), UpdateCommand (1).

### Phase 6.5 — Polish (Done)
- **GetDistinctRepoIdsAsync**: New `IEidetStore` method for querying distinct RepoId values. Implemented in `RavenEidetStore` using index projection. Replaced MaintenanceScheduler placeholder — scheduler now discovers all active repos automatically.
- **Environment variable overrides**: `ConfigManager.Load()` applies env var overrides after loading config: `EIDET_API_URL` (bind address + port), `EIDET_RAVEN_URL`, `EIDET_OLLAMA_URL`, `EIDET_OLLAMA_MODEL`. Enables container and CI/CD configuration without config files.
- **Auto-intake on first session**: MCP `eidet_context` triggers automatic intake (CLAUDE.md, README, etc.) when no memories exist for the repo. Controlled by `memory.autoIntakeOnFirstSession` config. Only runs once per MCP session.
- **`eidet install` auto-configures MCP clients**: Detects Claude Code (`~/.claude/settings.json` → `mcpServers`) and Claude Desktop (`claude_desktop_config.json`). Idempotent — skips if already configured.
- **Graceful shutdown**: `eidet serve` handles Ctrl+C/SIGTERM cleanly — stops scheduler, disposes enrichment, closes RavenDB store, stops HttpListener. Uses `CancellationTokenSource.CreateLinkedTokenSource`.
- **Test coverage**: 186 → 193 tests. Added ConfigManager defaults (7), InstallCommand MCP config (1), verified existing patterns.

### Phase 7 — Done
- **RavenDB Embedded mode**: `DocumentStoreFactory.CreateEmbedded()` and `CreateFromConfig()` — starts embedded RavenDB server from `RavenDB.Embedded` NuGet, manages lifecycle. All commands now use `CreateFromConfig` (auto-selects embedded vs external). `eidet setup --embedded` provisions indexes and embeddings. `eidet doctor` tests embedded mode. Default data dir: `~/.eidet/data/raven` (Unix) or `%APPDATA%\Eidet\data\raven` (Windows).
- **API key authentication**: `AuthConfig` with `Enabled`, `RequireForNonLocalhost`, `ApiKeys` list. `ApiKeyService` in Core: SHA256 key hashing, scope validation (`read:all`, `write:observations`, `write:all`, `admin`), `admin` implies all scopes, `write:all` implies `write:observations`. `EidetApiServer` auth middleware: checks `Authorization: Bearer` header, validates key, checks scope. Health/status endpoints exempt. CORS headers (`Access-Control-Allow-Origin: *`) + OPTIONS preflight handling.
- **`eidet api-key create/list/revoke`**: CLI commands for key management. Creating first key auto-enables auth. Revoking last key auto-disables. `--scopes` flag, `--json` output.
- **Network binding guard**: `eidet serve` refuses to start on non-localhost without auth enabled. Configurable via `auth.requireForNonLocalhost`.
- **Test coverage**: 193 → 220 tests. Added ApiKeyService (16), AuthConfig (3), DocumentStoreFactory (4), ConfigHelper auth keys (4).

### Phase 8 — Done
- **Local Web UI**: SPA served at `http://localhost:19380/ui` from embedded resources. Dark-themed, responsive. 6 pages:
  - **Dashboard**: Repo selector, memory counts by type, recent memories list, agent context preview (L0+L1 text, token estimate, cross-repo scope, mounted layers).
  - **Memory Browser**: Full-text search + browse with type filter, paginated results, detail panel (content, entities, tags, importance, confidence, provenance, echo/fizzle counts).
  - **Knowledge Graph**: Canvas-based force-directed graph. Nodes colored by type (blue=observation, purple=insight, green=procedure, orange=heuristic), sized by importance. Edges from DerivedFrom/Links. Interactive drag, hover tooltips.
  - **Timeline**: Chronological view grouped by date, type badges, tag chips.
  - **Usage**: Operations breakdown table (calls, avg/min/max duration, results) + hourly activity bar chart. Configurable period (24h/7d/30d/90d).
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
- **CI/CD Pipeline**: GitHub Actions workflows — `ci.yml` (build + test on push/PR, matrix: Windows/Ubuntu/macOS, NuGet caching), `release.yml` (on `v*` tag: pack dotnet tool + SDK packages, build self-contained binaries, create GitHub Release with changelog, publish to NuGet/npm/PyPI with trusted publishing/OIDC). Version validation against `EidetVersion.Current`.
- **Docker**: `Dockerfile` (multi-stage, self-contained single-file publish on `runtime-deps:10.0`), `docker-compose.yml` (Eidet + optional RavenDB external + optional Ollama via profiles), `.dockerignore`. New env var overrides: `EIDET_STORAGE_MODE`, `EIDET_DATA_DIR`, `EIDET_AUTH_REQUIRE_NONLOCALHOST`.
- **Memory Quality Dashboard**: `QualityService` with 8 checks (stale memories, high-fizzle, potential conflicts, orphan observations, tag concentration, type imbalance, low-confidence, missing entities). Overall score 0.0–1.0. `QualityReport` model with issues + breakdown. CLI: `eidet quality --repo ... --json`. API: `GET /api/eidet/quality?repo=...`.
- **Backup/Restore**: `BackupService` using RavenDB Smuggler API. `.eidetbackup` format (ZIP: `backup.ravendbdump` + `manifest.json` with SHA256 checksum). CLI: `eidet backup create/restore/list/prune`. `BackupConfig`: `backupDir`, `retainCount` (default 10), `autoBackupIntervalHours`.
- **Integration Tests**: `Eidet.Integration.Tests` project with `EidetApiFixture` (starts real API server on random port with embedded RavenDB, unique database per test class). Tests: health/status, store/recall lifecycle, context, browse, repos, quality, feedback, secret rejection. Uses `SkippableFact` for environments without RavenDB Embedded.
- **Test coverage**: 253 → 272+ unit tests. Added QualityService (7), BackupService (11). Plus ~11 integration tests (skippable).

### Phase 10.5 — Service Operations (Done)
- **ServiceLock**: PID/lock file at `{configDir}/eidet.lock` with PID, port, bind address, start time. File-level locking prevents double-serve. Stale lock detection (checks if PID is alive). Cleaned up on shutdown.
- **Double-serve prevention**: `eidet serve` acquires lock before starting. If another instance is running, reports PID and suggests different port.
- **Port conflict handling**: Catches `HttpListenerException` for address-in-use, suggests `--port` or `eidet config set service.port`.
- **Service detection in status/doctor**: `eidet status` reads lock file, shows service PID/port/uptime and health status. `eidet doctor` adds Service health check (process alive + HTTP health endpoint).
- **Ollama health in /api/status**: Status API endpoint now includes Ollama connectivity check when enrichment is configured (enabled, healthy, model, URL).
- **HTTP MCP repo override**: `POST /mcp?repo=...` query parameter for per-request repo scoping. `ConcurrentDictionary<string, McpServer>` pool creates per-repo MCP server instances. Used by TerminalHost container overlay.
- **Test coverage**: 272+ → 290 unit tests. Added ServiceLock (7).

### Phase 11 — Search Quality + Usage Analytics (Done)
- **Search bug fix (critical)**: RavenDB `Search()` uses OR semantics by default — `.Search("Content", text).WhereIn("RepoId", ids)` was effectively `content matches text OR repoId IN ids`, returning results from ALL repos. Fixed with explicit `WhereIn().AndAlso().Search()` and `AndAlso()` on all filter clauses in `ApplyFilters`.
- **Composite SearchText index**: New `SearchText` field in `Memories_Search` index concatenates Content + Summary + OneLiner + ForesightHint + Tags + Entities. Full-text search now matches across all textual fields. `SearchVector` (renamed from `ContentVector`) uses composite text for richer vector embeddings. BREAKING: Index schema change requires RavenDB re-index on next serve start.
- **CrossRepo default changed**: `MemoryQuery.CrossRepo` default changed from `true` to `false`. MCP `eidet_recall` still explicitly passes `cross_repo=true`. REST API `/api/eidet/search` accepts `cross_repo` query param (defaults to false). Prevents accidental cross-repo result leakage.
- **Usage statistics tracking**: `UsageTracker` service records per-repo API call metrics using RavenDB time series on `RepoUsage` anchor documents. Each operation type records duration and result count via `UsageScope` (Stopwatch-based, fire-and-forget). `NullUsageTracker` for zero-overhead when disabled. Instrumented all API handlers and MCP tool execution.
- **Usage API endpoints**: `GET /api/eidet/usage` (aggregated stats per repo), `GET /api/eidet/usage/timeseries` (raw time series per operation), `GET /api/eidet/usage/hourly` (hourly bucketed call counts).
- **Context preview API**: `GET /api/eidet/context/preview` returns context text + layers + cross-repo scope for debugging.
- **`eidet context`**: New CLI command to preview L0+L1 context, mounted layers, and cross-repo scope. Rich TUI output with Spectre.Console Panel. `--json` support.
- **`eidet help <command>`**: Translates to `<command> --help` (Spectre.Console compat).
- **`eidet recall --cross-repo`**: Opt-in flag for cross-repo search (default off).
- **`eidet mcp --repo <ID>`**: Explicit repo identifier for Claude Desktop integration where no working directory concept exists.
- **Web UI enhancements**: New "Usage" page (operations table + hourly bar chart), dashboard context preview panel (L0+L1 text color-coded by type, token estimate, cross-repo scope, mounted layers), repo dropdown sorted alphabetically with parent path disambiguation.
- **Root URL handling**: Browser redirect to `/ui`, helpful JSON for API clients, 404 hint messages.
- **SDK updates**: All three SDKs (TypeScript, Python, C#) now expose `usage()`, `usageTimeSeries()`, `usageHourly()`, `contextPreview()` with full type definitions.
- **Test coverage**: 290 → 315 tests. Added UsageTracker (14).

### Phase 11.5 — Data Quality Fixes (Done)
- **Index deployment on startup**: `DeployIndexes()` now runs during `eidet serve` and `eidet mcp` startup (not just `eidet setup`). Ensures index schema changes (like SearchText/SearchVector) take effect without manual re-setup. RavenDB's `IndexCreation.CreateIndexes` is idempotent.
- **Ollama CoT stripping**: `OllamaEnrichmentService.StripChainOfThought()` strips chain-of-thought reasoning from model responses. Some models (e.g., Gemma4) output `<channel|>` delimited reasoning even with `think:false`. Extracts actual answer after the last delimiter. Also handles `<think>...</think>` blocks.
- **Duplicate detection fix**: `FindDuplicateAsync` had the same RavenDB OR semantics bug as the main search — missing `AndAlso()` between `WhereEquals` and `Search`/`VectorSearch` clauses. Both Strategy 1 (vector) and Strategy 2 (full-text fallback) now chain filters correctly.
- **Per-repo auto-intake**: Auto-intake on first `eidet_context` call now checks per-repo memory count (`GetCountsByTypeAsync`) instead of global database document count. Previously, once any repo had memories, auto-intake would never trigger for new repos.
- **Maintenance Stage 5b — Enrichment cleanup**: New stage strips corrupted CoT reasoning from existing `summary`/`oneLiner`/`foresightHint` fields. Runs before backfill enrichment so cleaned fields get re-enriched.
- **Test coverage**: 315 → 319 tests. Added StripChainOfThought (9).

### Phase 11.6 — Web UI Fixes + Service Reliability (Done)
- **Web UI intake fix**: Intake API was passing normalized repo ID (e.g., `P--claude`) as the filesystem path, causing `DirectoryNotFoundException`. `RepoUsage` anchor documents now track `OriginalPath`. `HandleIntake` resolves the real path via explicit `path` param, `LooksLikePath()` check, or `UsageTracker.GetOriginalPathAsync()` lookup. Returns 400 with clear message if path can't be resolved.
- **Repo path backfill**: `RepoUsage.TryInferPath()` reverses the common `X--Name` → `X:\Name` pattern (verified against filesystem). `GetAllRepoPathsAsync()` infers and persists paths for existing anchor documents on first access.
- **Stdio MCP usage tracking**: `McpCommand` was creating `McpServer` without a `UsageTracker`, so `OriginalPath` was never recorded from Claude Code sessions. Now wired in.
- **Repos API with original paths**: `GET /api/eidet/repos` returns `originalPath` alongside `repoId`. Web UI dropdown shows real paths with smart abbreviation (`formatRepoDisplay`: deep paths show `Name  (drive:\...\parent)`).
- **Web UI error handling**: `apiPost()` extracts JSON error messages from non-200 responses for clean display instead of generic "API error: 500".
- **EidetLog file logger**: `EidetLog` static class in `Eidet.Core` — writes timestamped entries to `{configDir}/logs/eidet.log`. 4 levels (Info/Warn/Error/Error+Exception), 5MB rotation with `.log.1` backup, thread-safe, never throws. Logs startup, shutdown, crashes, and unhandled API errors with full stack traces.
- **Service restart reliability**: Scheduled task `RestartOnFailure` count increased from 3 to 999. `ServeCommand` catches unexpected exceptions with non-zero exit code so Task Scheduler triggers restart. `OperationCanceledException` from Ctrl+C exits cleanly (code 0).
- **Status log path**: `eidet status` now shows the log file path.

### Phase 12 — Persisted Scheduler + Knowledge Graph + Deep Dive Docs (Done)
- **Persisted scheduled tasks**: Replaced in-memory `MaintenanceScheduler` (System.Threading.Timer) with `ScheduledTaskService` backed by RavenDB documents. Natural keys: `scheduledtasks/maintenance`, `scheduledtasks/consolidation`. Uses RavenDB Refresh feature (`@refresh` metadata) as a persistent alarm clock — documents are modified at the scheduled time, surviving service restarts. Polling loop (1-min interval) detects due tasks and executes them. Tracks full run history: `LastRunAt`, `LastCompletedAt`, `LastDurationMs`, `RunCount`, `ErrorCount`, `Status`. On restart, overdue tasks run within 30 seconds. `DatabaseProvisioner.EnsureRefreshEnabled()` enables the Refresh feature on startup.
- **Scheduled tasks API**: `GET /api/eidet/scheduled-tasks` returns current state of all scheduled tasks (type, interval, next/last run, duration, status, error info).
- **Web UI scheduled tasks**: Settings page shows scheduled task cards with status badges, timing info, run counts, and error display.
- **Knowledge Graph rewrite**: Complete rewrite of the graph page for Obsidian-like interactivity. Click-to-select with detail side panel (type, importance, confidence, echo/fizzle counts, age, tags, entities, connections). Node labels visible for important nodes and on zoom. Confidence as node opacity. Echo count as golden ring. Edge arrows with relationship labels. Type filter checkboxes. Zoom/pan via mouse wheel and buttons. Connected-node highlighting on select/hover (dims unrelated nodes). Click connections in detail panel to navigate. Responsive layout with mobile support.
- **GraphNode enrichment**: Added `Confidence`, `CreatedAt`, `AccessCount`, `EchoCount`, `FizzleCount`, `Entities` fields to `GraphNode` domain type and `GetGraphDataAsync`.
- **Deep dive documentation**: `docs/deep-dive.md` — comprehensive narrative guide covering architecture, memory lifecycle (creation through gates, retrieval with scoring, context loading, feedback, decay, consolidation), RavenDB storage model, write gates, Ollama enrichment, layers, MCP surface, hooks, Web UI, security, service operations, Docker, SDKs, quality/backup, configuration, and glossary.
- **Test coverage**: 319 → 328 tests. Added ScheduledTask (9).

### Phase 13 — Memory Curation + AI Enrichment UI (Done)
- **Memory update/edit API**: `PUT /api/eidet/{id}` — update content (creates versioned supersession), tags, importance, confidence, type, oneLiner, summary, foresightHint. Content changes go through write gates (secret scanning + signal gate) and create new version with `ParentMemoryId` chain. Metadata-only changes update in place. `MemoryService.UpdateMemoryAsync` with full field support.
- **Memory link management API**: `POST /api/eidet/{id}/links` (add link to existing memory), `DELETE /api/eidet/{id}/links` (remove link). `MemoryService.AddLinkAsync` with dedup, `RemoveLinkAsync`. Memory IDs with slashes handled via `ExtractMemoryIdFromLinkPath` helper.
- **AI enrichment API**: `POST /api/eidet/enrich` — on-demand enrichment generation using Ollama. Supports 4 tasks: `oneliner`, `summary`, `foresight`, `entities`. Returns generated text for user review before applying. Used by Web UI for interactive AI-assisted curation.
- **MCP `eidet_edit` tool**: 14th MCP tool for editing memories from AI coding agents. Supports content, tags, importance, confidence, type updates. Wired through `McpServer.ExecuteEdit`.
- **Web UI Memory Browser curation**: Enhanced detail panel with action bar (Edit, Echo, Fizzle, Forget buttons). Edit mode with inline content editing, tag modification, importance/confidence sliders, type selector. AI enrichment buttons (Regenerate one-liner/summary/foresight) with "Apply" action to save generated text directly to the memory. Link display with remove buttons.
- **Web UI Create Memory dialog**: Modal dialog for manually creating memories from the browser. Type selector, content textarea (20+ char validation), tags input, importance slider. Creates via `POST /api/eidet` with `source: web-ui`.
- **Web UI Repo Links**: Settings page section for managing cross-repo relationships. Target repo dropdown, relationship selector (depends-on, uses-library, supports, related, conflicts, forked-from, refines). Shows existing links with remove buttons.
- **Enrichment availability detection**: Web UI checks `/api/status` for Ollama health on load. AI buttons only shown when enrichment service is available and healthy.
- **Test coverage**: 328 → 342 tests. Added CurationApiTests (11): ExtractMemoryIdFromLinkPath (3), auth scope for PUT/POST/DELETE curation endpoints (4), request model validation (4). Updated McpToolDefinitionsTests: 14 tools, eidet_edit tool schema (2).

### Phase 14 — Markdown Pack Format + ScribeGate Integration (Done)
- **MarkdownPackFormat**: New `MarkdownPackFormat` static class in `Eidet.Core.Services` — serializes/deserializes `EidetPack` to/from human-readable markdown. Format: YAML frontmatter (pack metadata with `eidet:` namespace), H2 type groups (Observations, Insights, Procedures, Heuristics), H3 per memory (heading = one-liner), HTML comments for per-memory metadata (`<!-- eidet: importance=0.85 confidence=0.75 tags=react,hooks -->`), enrichment in separate comments (`<!-- eidet-entities: ... -->`, `<!-- eidet-summary: ... -->`, `<!-- eidet-foresight: ... -->`). Cross-platform `\n` line endings.
- **Contextual heading detection**: Parser uses smart boundary detection — H2 is a type group only if it matches a known type plural, H3 is a memory boundary only if followed by `<!-- eidet:` comment within 4 lines. This allows memory content to contain markdown headings without breaking the parse.
- **ExportService markdown integration**: `ExportPackToFileAsync` auto-detects format by `.md` extension. New `ExportPackToMarkdownAsync` and `ImportPackFromMarkdownAsync` explicit methods. `ImportPackFromFileAsync` auto-detects markdown vs JSON.
- **CLI `--format pack` export**: `eidet export --format pack --bundle-id my-pack --name "My Pack" --version 1.0.0 --output my-pack.md`. Defaults to `.md` output (markdown format). All pack metadata via CLI flags (`--author`, `--description`, `--packages`).
- **MCP `eidet_pack_export` / `eidet_pack_import` tools**: 15th and 16th MCP tools. Pack export writes markdown or JSON file (detected by extension), returns memory count. Pack import reads file, imports entries, auto-mounts as Base layer via `LayerService`. `ExportService` and `LayerService` wired into `McpServer` constructor.
- **ScribeGate compatibility**: Markdown format designed for publishing on ScribeGate (scribegate.dev). YAML frontmatter matches ScribeGate's document format. Rendered markdown is human-readable in any viewer (GitHub, VS Code, ScribeGate). HTML comments are invisible when rendered but machine-parseable. ScribeGate's review/approval flows enable community curation of memory packs.
- **Test coverage**: 342 → 407 tests. Added MarkdownPackFormatTests (48): serialize (12), deserialize (9), round-trip (3), content-with-markdown (6), internal helpers (10), boundary detection (3), frontmatter parsing (3), meta pairs (1), inline list (1). Updated McpToolDefinitionsTests: 13 tools, pack_export/pack_import schemas (2).

## Installation & Distribution

Eidet is distributed as a **dotnet tool** (primary) and **standalone binaries** (Docker/non-.NET).

```bash
# Install (requires .NET 10 SDK)
dotnet tool install -g eidet

# First-time setup
eidet setup      # interactive wizard for RavenDB (embedded or external), Ollama, embeddings
eidet install    # registers scheduled task/launchd/systemd, configures Claude Code & Desktop MCP

# Update
eidet update           # stops service → dotnet tool update → restarts service
eidet update --check   # check only, don't install

# Feedback / bug reports
eidet feedback   # opens GitHub Issues with version + OS pre-filled
```

**Distribution channels:**
- **NuGet**: `dotnet tool install -g eidet` (tool) + `Eidet.Sdk` (C# client SDK)
- **npm**: `@eidet/sdk` (TypeScript client SDK)
- **PyPI**: `eidet-sdk` (Python client SDK)
- **GitHub Releases**: Self-contained binaries for Docker and non-.NET environments
- **Docker**: `eidet/eidet:latest`

**Update flow:** `eidet update` checks NuGet for the latest version, stops the running service (scheduled task/launchd/systemd), runs `dotnet tool update -g eidet`, records the update in version history (`version-history.json`), and restarts the service. Version history is shown in `eidet status` and the Web UI.

**MCP configuration:** `eidet install` auto-configures:
- Claude Code: `~/.claude.json` → `mcpServers.eidet`
- Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json` (Windows) / `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS)

## MVP Scope

Local-only. No team/sync/remote yet.

- Local RavenDB (embedded or connect to existing instance)
- Local optional Ollama enrichment
- MCP server (stdio for AI clients, HTTP for network)
- REST API (for TerminalHost and other tools)
- Rich TUI for setup, configuration, troubleshooting
- Docker container integration guidance
- Full 15-tool MCP surface
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
│   │   ├── Domain/               # MemoryEntry, MemoryType, Validity, layers, links, packs, GraphData, RepoUsage
│   │   ├── Gates/                # SecretScanner, SignalGate, WriteGate
│   │   ├── Indexes/              # Memories_Search, Memories_CountByType
│   │   ├── Services/             # MemoryService, EntityExtractor, LayerService, OllamaService, ApiKeyService, HookRunner, QualityService, BackupService, ServiceLock, UsageTracker, enrichment (IEnrichmentService, OllamaEnrichmentService, NullEnrichmentService)
│   │   ├── EidetLog.cs            # File logger ({configDir}/logs/eidet.log, 5MB rotation)
│   │   ├── StringUtils.cs        # Shared string helpers (Truncate)
│   │   └── Storage/              # IEidetStore, RavenEidetStore, DatabaseProvisioner, DocumentStoreFactory (embedded + external)
│   ├── Eidet.Service/            # CLI + REST API + system service
│   │   ├── Api/                  # EidetApiServer (HttpListener + MCP HTTP + embedded Web UI)
│   │   ├── Commands/             # setup, mcp, serve, doctor, status, recall, store, stats, context, export, intake, maintain, quality, backup, install, uninstall, config, instructions, ollama, docker, update, feedback, api-key
│   │   ├── Mcp/                  # McpServer (stdio + HTTP), McpModels, McpToolDefinitions
│   │   ├── Scheduler/            # MaintenanceScheduler (background timers)
│   │   └── wwwroot/              # Web UI SPA (index.html, app.css, app.js) — embedded resources
│   └── Eidet.Sync/              # Sync adapters (future)
├── sdk/
│   ├── typescript/              # @eidet/sdk npm package (EidetClient, full types)
│   ├── python/                  # eidet-sdk pip package (EidetClient, httpx, type hints)
│   └── dotnet/Eidet.Sdk/       # Eidet.Sdk NuGet package (EidetClient, record types)
├── tests/
│   ├── Eidet.Core.Tests/        # 251 tests
│   ├── Eidet.Service.Tests/     # 68 tests
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
- **Composite search index**: `SearchText` field concatenates Content + Summary + OneLiner + ForesightHint + Tags + Entities for full-text search. `SearchVector` embeds the same composite text. Ensures AI-enriched content is searchable.
- **Single-repo search by default**: `MemoryQuery.CrossRepo` defaults to `false`. MCP `eidet_recall` passes `true` explicitly. REST API and CLI default to single-repo to prevent accidental cross-repo leakage.
- **Usage tracking via RavenDB time series**: Fire-and-forget recording of per-operation timing via `UsageScope`. Zero overhead when no tracker configured (NullUsageTracker). Time series on per-repo anchor documents.
- **Index deployment on startup**: `DeployIndexes()` runs on every `serve`/`mcp` startup, not just `setup`. Ensures index schema changes apply automatically. Idempotent via RavenDB's `IndexCreation.CreateIndexes`.
- **Enrichment CoT stripping**: `StripChainOfThought()` extracts actual answer from Ollama responses that contain `<channel|>` reasoning delimiters or `<think>` blocks. Applied both on new responses and as a maintenance cleanup stage for existing data.
- **Repo path tracking via RepoUsage**: `OriginalPath` field on existing per-repo anchor documents maps normalized IDs back to filesystem paths. Populated by `UsageTracker.RecordAsync` when it sees a path-like repo ID. `TryInferPath()` backfills existing docs for the common `X--Name` → `X:\Name` pattern. Enables Web UI intake and other path-dependent operations.
- **EidetLog file logger**: Lightweight static logger writing to `{configDir}/logs/eidet.log`. Thread-safe, never throws, 5MB rotation. Used by serve command (startup/shutdown/crash) and API server (unhandled request errors). Visible via `eidet status`.
- **Persisted scheduler via RavenDB Refresh**: `ScheduledTaskService` replaces in-memory `MaintenanceScheduler`. Task documents (`scheduledtasks/maintenance`, `scheduledtasks/consolidation`) use `@refresh` metadata to schedule execution at a future time. RavenDB modifies the document when the refresh time arrives. A 1-minute polling loop detects due tasks. Survives restarts — overdue tasks execute within 30 seconds of startup. Full run history tracked (counts, durations, errors, status).
- **Memory curation via versioned updates**: Content changes create new versions (supersession chain) preserving full history. Metadata-only changes (tags, importance, confidence, enrichment fields) update in place. Write gates still enforced on content changes. Web UI provides inline editing with AI-assisted enrichment regeneration.
- **On-demand AI enrichment**: `/api/eidet/enrich` endpoint exposes Ollama enrichment for interactive use. Users can preview generated one-liners, summaries, foresight hints before applying them. Decouples enrichment from the background pipeline, enabling manual curation workflows.
- **Markdown pack format**: `MarkdownPackFormat` serializes/deserializes `EidetPack` as human-readable markdown. YAML frontmatter for pack metadata, H2 type groups, H3 per memory with HTML comment metadata. Contextual heading detection (H2 = type boundary only if known type, H3 = memory boundary only if followed by eidet comment). Enables publishing to ScribeGate for community curation, review, and approval of memory packs. Library authors publish markdown packs to `scribegate.dev/eidet-packs/{slug}`, consumers import directly. Markdown renders beautifully in any viewer while being fully machine-parseable.

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

# Context preview (with layers + cross-repo scope)
curl "http://localhost:19380/api/eidet/context/preview?repo=P%3A%5CEidet"

# Usage stats (last 30 days)
curl "http://localhost:19380/api/eidet/usage?repo=P%3A%5CEidet&days=30"

# Usage hourly breakdown
curl "http://localhost:19380/api/eidet/usage/hourly?repo=P%3A%5CEidet&days=7"

# Scheduled tasks status
curl http://localhost:19380/api/eidet/scheduled-tasks

# Update a memory (metadata-only or content change with versioning)
curl -X PUT http://localhost:19380/api/eidet/memories%2FP--Eidet%2Finsight%2Fabc123 \
  -H "Content-Type: application/json" \
  -d '{"tags":["updated","tags"],"importance":0.8}'

# Add a link to an existing memory
curl -X POST http://localhost:19380/api/eidet/memories%2FP--Eidet%2Finsight%2Fabc123/links \
  -H "Content-Type: application/json" \
  -d '{"targetRepoId":"P:\\Other","relation":"depends-on"}'

# AI enrichment (requires Ollama)
curl -X POST http://localhost:19380/api/eidet/enrich \
  -H "Content-Type: application/json" \
  -d '{"content":"Memory content here","task":"oneliner"}'

# Web UI (browser auto-redirects from root /)
# Open http://localhost:19380/ui in browser
```

## Security Documentation

### STRIDE.md Threat Model

This repository includes a STRIDE threat model (`STRIDE.md`) for security analysis.

**When to update STRIDE.md:**
- Adding new authentication/authorization mechanisms
- Changing data storage, encryption, or secrets handling
- Adding new external integrations or API endpoints
- Modifying trust boundaries (new external connections, database access)
- After security incidents or penetration test findings
- When addressing security recommendations from the document

**How to update:**
1. Add new threats to the relevant STRIDE category (Spoofing, Tampering, Repudiation, Information Disclosure, Denial of Service, Elevation of Privilege)
2. Assess likelihood (Very Low → High) and impact (Low → Critical)
3. Document existing mitigations or add recommendations
4. Link GitHub issues for unresolved findings
5. Update the Review History table
6. Update version if using frontmatter

**Tracking critical findings:**
- Critical/High risk findings should have a linked GitHub issue with `security` label
- Review STRIDE.md annually or after major releases

## Links

- **GitHub**: https://github.com/stevehansen/eidet (private)
- **Domain**: eidet.dev
