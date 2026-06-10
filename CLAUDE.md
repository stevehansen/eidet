# CLAUDE.md — Eidet

> Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.
> *From "eidetic" — extraordinarily vivid, detailed recall.*

A standalone local service that gives AI coding agents persistent, semantic memory across sessions.

## Specs

Design decisions live in `docs/specs/`:

| Spec | Covers |
|------|--------|
| [CoreSpec.md](docs/specs/CoreSpec.md) | Domain model, 4 memory types, layers, tiered loading (L0/L1/L2), scoring, hybrid retrieval, write gates, consolidation, FadeMem decay, maintenance pipeline |
| [ServiceSpec.md](docs/specs/ServiceSpec.md) | Daemon, MCP server, REST API, RavenDB (embedded/external), TUI, Docker, installation, CLI |
| [SyncSpec.md](docs/specs/SyncSpec.md) | *(Future)* Remote sync, E2E encryption, team sharing |
| [IntegrationSpec.md](docs/specs/IntegrationSpec.md) | Claude Code, Claude Desktop, Cursor, TerminalHost, Docker, CI/CD, client SDKs |

- Architecture deep dive: [docs/deep-dive.md](docs/deep-dive.md)
- Phase-by-phase implementation history: [docs/phases.md](docs/phases.md)

## Current Capabilities

- **16 MCP tools**: store, recall, context, forget, feedback, history, intake, link, consolidate, maintenance, export, pack_export, pack_import, edit (+ layer mgmt)
- **4 memory types**: Observation, Insight, Procedure, Heuristic — each with per-type retrieval budgets
- **Storage**: RavenDB (embedded or external) with vector + full-text hybrid search on composite `SearchText` field
- **Write gates**: `WriteValidator` chains rules (13 secret patterns + signal/low-signal + self-talk) — deterministic, local, always-on; single entry point from `MemoryService.StoreAsync` and `UpdateMemoryAsync`
- **Optional enrichment**: Ollama background workers generate one-liners, summaries, foresight hints, entity supplements
- **Layers**: Local (rw) + Shared/Base (ro); auto-mount on pack import
- **Interfaces**: MCP stdio, MCP HTTP, REST API, CLI, Web UI (`/ui`), SDKs (TS/Python/C#)
- **Operations**: API key auth (4 scopes), hooks (6 lifecycle events), persistent scheduler (RavenDB Refresh), quality dashboard, backup/restore, usage analytics
- **Curation**: Versioned `PUT` edits / MCP `eidet_edit`, AI-assisted enrichment via `/api/eidet/enrich`, Web UI inline editing
- **Pack format**: Human-readable markdown with YAML frontmatter — ScribeGate compatible
- **Test coverage**: 646 tests (Core + Service + Integration)

## Installation

Shipped as a dotnet tool (primary) + standalone binaries for Docker/non-.NET.

```bash
dotnet tool install -g eidet
eidet setup      # interactive wizard (RavenDB, Ollama, embeddings)
eidet install    # registers service + auto-configures Claude Code/Desktop MCP
eidet update     # stop service → update → restart
```

Distribution: NuGet (`eidet`, `Eidet.Sdk`), npm (`@eidet/sdk`), PyPI (`eidet-sdk`), Docker (`eidet/eidet:latest`), GitHub Releases.

MCP config (written by `eidet install`):
- Claude Code: `~/.claude.json` → `mcpServers.eidet`
- Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json` / `~/Library/Application Support/Claude/claude_desktop_config.json`

## Tech Stack

- **.NET 10** (latest SDK)
- **RavenDB 7.x** — hybrid search + built-in embeddings (bge-micro-v2)
- **Spectre.Console 0.55** — TUI + CLI
- **HttpListener** — REST API (no ASP.NET dependency)

Minimal dependencies. See ServiceSpec.md "Dependency Philosophy".

## Project Structure

```
eidet/
├── src/
│   ├── Eidet.Core/              # Domain, gates, indexes, services, storage
│   ├── Eidet.Service/           # CLI, REST API, MCP, scheduler, embedded Web UI
│   └── Eidet.Sync/              # Sync adapters (future)
├── sdk/{typescript,python,dotnet}/
├── tests/{Core,Service,Integration}.Tests/
└── docs/{specs/,phases.md,deep-dive.md}
```

## Key Design Decisions

- **RavenDB dual mode** — embedded for zero-setup, external for existing installs
- **Append-only + validity intervals** — audit trail, future sync-friendly
- **Zero-LLM write path** — deterministic stores; Ollama only for optional background enrichment
- **Typed memories + per-type budgets** — ENGRAM research shows +30pp vs single bucket
- **Docker-like layers** — Local (rw), Shared/Base (ro); writes always local
- **Secret scanning is always-on** — runs before any storage, cannot be disabled
- **< 600 token wake-up** — L0 identity + L1 top-20 dense-packed
- **HttpListener over ASP.NET** — lightweight, matches TerminalHost
- **Single-repo search by default** — `CrossRepo` defaults to `false`; MCP `eidet_recall` opts in explicitly
- **Composite search index** — `SearchText` concatenates Content + Summary + OneLiner + ForesightHint + Tags + Entities; `SearchVector` embeds the same. AND-semantics enforced with explicit `AndAlso()` on filter clauses
- **API key auth (4 scopes)** — SHA256 hashed, network binding guard prevents non-localhost without auth; CORS enabled; `/ui` and health exempt
- **Hooks system** — 6 lifecycle events; pre-hooks gate via exit code, post-hooks fire-and-forget; zero overhead with `NullHookRunner`
- **Persisted scheduler** — RavenDB Refresh feature as alarm clock; survives restarts; overdue tasks run within 30s of startup
- **Repo path tracking** — `RepoUsage.OriginalPath` maps normalized IDs back to filesystem paths; enables Web UI intake
- **Enrichment port/adapter** — `Eidet.Core.Enrichment`: `EnrichmentService` facade over `IEnrichmentPort`; `OllamaEnrichmentAdapter`/`NullEnrichmentAdapter` internal, `InMemoryEnrichmentAdapter` public for tests. `EnrichMemoryAsync(entry)` replaces the duplicated per-field loop; `OllamaTextSanitizer.Clean()` handles `<channel|>` and `<think>` delimiters from Ollama/Gemma responses.
- **Maintenance pipeline** — `Eidet.Core.Maintenance`: composable `IMaintenanceStage` (9 internal stages) + `MaintenanceOrchestrator` (per-stage try/catch, `OnlyStages`/`SkipStages`) + thin `IMaintenanceRunner` facade for scheduler/REST/MCP. `ConsolidationEngine` exposes `ConsolidateAsync(dryRun)` for ad-hoc API/MCP use. Helpers extracted: `Eidet.Core.Text.WordSimilarity`, `FadeMemCurve.Defaults`, `TagOverlapGrouper`.
- **Versioned curation** — content edits create supersession chains; metadata edits update in place
- **Markdown pack format** — YAML frontmatter + HTML comment metadata; renders in any viewer, machine-parseable, ScribeGate compatible
- **Embedded Web UI** — vanilla HTML/CSS/JS SPA, canvas graph, shipped in the binary; dark theme, responsive

## API Quick Reference

Base: `http://localhost:19380`

| Method | Path | Purpose |
|--------|------|---------|
| GET  | `/api/health` | Health check |
| GET  | `/api/status` | Service + Ollama status |
| GET  | `/api/eidet/context?repo=...` | L0+L1 agent context |
| GET  | `/api/eidet/context/preview?repo=...` | Debug view (layers, cross-repo) |
| GET  | `/api/eidet/search?repo=...&q=...` | Hybrid search (single-repo by default) |
| GET  | `/api/eidet/browse?repo=...&type=...` | Paginated browse |
| GET  | `/api/eidet/graph?repo=...` | Graph data (nodes + edges) |
| GET  | `/api/eidet/repos` | List repos (with original paths) |
| GET  | `/api/eidet/usage?repo=...&days=30` | Usage stats |
| GET  | `/api/eidet/usage/hourly?repo=...` | Hourly bucket counts |
| GET  | `/api/eidet/scheduled-tasks` | Scheduler state |
| GET  | `/api/eidet/quality?repo=...` | Quality report |
| POST | `/api/eidet` | Store memory |
| PUT  | `/api/eidet/{id}` | Update (versioned on content change) |
| POST | `/api/eidet/{id}/links` | Add cross-repo link |
| POST | `/api/eidet/feedback` | Echo / fizzle |
| POST | `/api/eidet/enrich` | On-demand Ollama enrichment |
| POST | `/api/eidet/intake` | Ingest project files |
| POST | `/api/eidet/consolidate` | Consolidate observations → insights |
| POST | `/api/maintenance` | Run maintenance pipeline |
| POST | `/mcp?repo=...` | MCP JSON-RPC over HTTP |
| GET  | `/ui` | Web UI SPA |

Memory IDs are URL-encoded. `repo` param is the filesystem path. Memory IDs are `memories/{normalized-repo}/{type}/{hash}`.

## Security

STRIDE threat model in `STRIDE.md`. Update when adding auth, changing storage/secrets handling, adding external integrations, modifying trust boundaries, or after incidents. Critical/High findings need linked GitHub issues with the `security` label. Review annually or after major releases.

## Links

- **GitHub**: https://github.com/stevehansen/eidet (private)
- **Domain**: eidet.dev
