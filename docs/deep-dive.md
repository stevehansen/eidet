---
title: Deep Dive
nav_order: 10
---

# Eidet Deep Dive

A comprehensive guide to how Eidet works, from the philosophy behind it to the internals of every subsystem.

---

## The Problem

AI coding agents are amnesiac. Every session starts from zero — the agent re-discovers your architecture, re-learns your patterns, makes the same mistakes, asks the same questions. Context windows are large but finite, and nothing persists once the session ends.

Some teams work around this with massive `CLAUDE.md` files or carefully curated prompts, but these are manual, brittle, and don't capture what the agent *learns during work*. The agent discovers that your CI flakes on Windows paths over 260 characters, but tomorrow's session won't know that.

Eidet solves this by giving agents a structured, searchable, persistent memory that lives on your machine.

---

## Architecture Overview

```
  AI Agent (Claude Code, Cursor, etc.)
       |
       |--- MCP (stdio) -----> McpServer --------+
       |                                          |
       +--- REST (HTTP) -----> EidetApiServer ----+
                                                  |
                                             MemoryService
                                            /      |      \
                                    WriteGates   Search   Scoring
                                    (Secret +   (Vector + (Importance,
                                     Signal)    Full-text) Recency,
                                        |          |       Frequency,
                                        +----+-----+      Confidence)
                                             |
                                          RavenDB
                                       (embedded or
                                        external)
                                             |
                                    +--------+--------+
                                    |                 |
                              MaintenanceScheduler  OllamaEnrichment
                              (decay, dedup,        (summaries, one-liners,
                               consolidation)        foresight hints)
```

There are two entry points into Eidet:

1. **MCP (Model Context Protocol)** — The primary interface for AI agents. Claude Code, Cursor, and other MCP-aware clients connect via stdio (`eidet mcp`) or HTTP (`POST /mcp`). The MCP server exposes 13 tools that agents call directly.

2. **REST API** — A standard HTTP API at `http://localhost:19380`. Used by the Web UI, SDKs, and any HTTP client. Also serves the embedded Web UI at `/ui`.

Both entry points talk to the same `MemoryService`, which is the heart of Eidet.

---

## Memory Types

Eidet doesn't treat all memories equally. Based on the [ENGRAM research](https://arxiv.org/abs/2401.14112), which showed a 30 percentage-point improvement over single-bucket systems, Eidet uses four distinct memory types:

### Observations

**What:** Raw facts noticed during a session.
**Lifetime:** Short (30-day half-life).
**Examples:**
- "The `UserService` talks to Redis for caching"
- "CI builds take ~8 minutes on this repo"
- "The `auth` module uses JWT with RS256 signing"

Observations are the most numerous type. Agents create them freely as they work. They decay fast — most will fade within a month unless they prove useful (via echo feedback) or get consolidated into insights.

### Insights

**What:** Confirmed knowledge distilled from multiple observations.
**Lifetime:** Medium (90-day half-life).
**Examples:**
- "Redis is the caching layer; all cache keys use the `user:` prefix pattern"
- "The API uses a middleware chain: auth -> rate-limit -> handler -> response-transform"

Insights are created by consolidation — when Eidet finds 3+ observations with overlapping tags, it groups them and creates (or boosts) an insight. With Ollama enabled, large groups get LLM-generated summaries. Insights form the bulk of what agents see in their session context.

### Procedures

**What:** Multi-step workflows and recipes.
**Lifetime:** Long (365-day half-life).
**Examples:**
- "To deploy: push to main, wait for CI, tag release, verify staging"
- "To add a new API endpoint: create handler, add route, update OpenAPI spec, add test"

Procedures are rarely created but highly durable. They capture institutional knowledge about *how to do things* in your project.

### Heuristics

**What:** Rules of thumb and decision shortcuts.
**Lifetime:** Nearly immortal (730-day half-life).
**Examples:**
- "Always prefer `httpx` over `requests` in this codebase"
- "Never use `--force` with the migration tool"
- "The CI flakes on Windows when paths exceed 260 characters"

Heuristics are the most stable memories. They represent hard-won knowledge that rarely changes.

### Why Types Matter

Types aren't just labels — they affect every part of the system:

| Aspect | How types are used |
|--------|-------------------|
| **Decay rate** | Observations fade 24x faster than heuristics |
| **Context budgets** | L1 context reserves slots per type (~50% insights, ~30% procedures, ~20% heuristics) |
| **Recall budgets** | Search results are diversified by type (40% insights, 25% observations, 20% procedures, 15% heuristics) |
| **Consolidation** | Only observations consolidate into insights |
| **Maintenance** | Observation retention is capped (default 90 days); other types have no hard cap |

---

## The Lifecycle of a Memory

### 1. Creation (Store)

When an agent calls `eidet_store`, the memory goes through a pipeline:

```
Agent calls eidet_store(content, type, tags, importance)
    |
    v
SecretScanner --- blocks if content looks like a secret
    |              (AWS keys, private keys, tokens, high-entropy strings)
    |              13 regex patterns. Cannot be disabled.
    v
SignalGate ------ blocks if content is low-signal
    |              (self-talk, tool dumps, generic observations)
    |              17 blocklist phrases + self-talk detection.
    v
HookRunner ------ runs pre-store hooks (if configured)
    |              External commands can reject via non-zero exit.
    v
EntityExtractor - extracts named entities
    |              (file paths, class names, packages, env vars, URLs)
    |              9 regex patterns + validation.
    v
DuplicateCheck -- checks for existing similar memories
    |              Vector similarity > 0.92 = duplicate.
    |              Full-text fallback if no vectors available.
    v
ID Generation --- deterministic ID from content hash
    |              Format: memories/{repoId}/{type}/{first12CharsOfSHA256}
    v
RavenDB Write --- stored as a document
    |
    v
Post-store hooks fire (if configured)
```

**Secret scanning is the hardest gate.** It runs 13 patterns covering AWS keys, private keys, GitHub/Slack/Azure tokens, connection strings with passwords, and high-entropy strings. It cannot be disabled — this is a design decision to prevent accidental credential leakage into the memory store.

**The signal gate** filters out noise. AI agents sometimes try to store meta-commentary ("I should remember this for next time") or tool output dumps. The signal gate catches these with 17 blocklist phrases and a self-talk detector.

### 2. Retrieval (Recall)

When an agent needs information, it calls `eidet_recall` with a natural language query:

```
Agent calls eidet_recall(query, limit=10)
    |
    v
Pre-recall hooks (if configured)
    |
    v
+-- Full-text search (RavenDB Corax engine) --+
|   Searches across composite SearchText:       |
|   Content + Summary + OneLiner +              |
|   ForesightHint + Tags + Entities             |
|   Fetches 2x limit for merge quality         |
+----------------------------------------------+
    |                                           |
    |   (parallel)                              |
    |                                           |
+-- Vector search (bge-micro-v2 embeddings) ---+
|   Searches SearchVector (composite text)      |
|   Cosine similarity, minimum 0.70 threshold   |
+----------------------------------------------+
    |
    v
Merge + Deduplicate
    |
    v
Score each result:
    importance * 0.30
  + confidence * 0.15
  + recency    * 0.25  (7-day half-life exponential decay)
  + frequency  * 0.30  (access count, capped at 10)
    |
    v
Apply type diversity budgets:
    40% insights, 25% observations,
    20% procedures, 15% heuristics
    |
    v
Apply non-local de-boost (0.8x for layer results)
    |
    v
Add staleness warnings (if > 7 days since last access)
    |
    v
Bump access counts on returned results
    |
    v
Return scored, ranked results
```

The **composite search index** is key. Earlier versions only searched the `Content` field, but Ollama-enriched fields like summaries and foresight hints contain valuable searchable text. The `SearchText` field concatenates everything, and the `SearchVector` embeds that composite text — so a search for "deployment" will match a memory whose one-liner mentions "deployment" even if the original content didn't use that word.

**Cross-repo search** defaults to off for safety. MCP recall explicitly enables it, but REST API calls default to single-repo. This prevents accidental leakage of project-specific memories into unrelated searches.

### 3. Context Loading (Session Start)

The most common first call in any agent session is `eidet_context`. This returns a compact summary (under 600 tokens) that gives the agent immediate situational awareness:

```
eidet_context(repo)
    |
    v
L0 — Identity line:
    "[Memory: 142 entries, 45 observations, 67 insights, 18 procedures, 12 heuristics]"
    |
    v
L1 — Top-20 memories by score:
    Scored by importance, confidence, recency, frequency
    Type budgets: ~50% insights, ~30% procedures, ~20% heuristics
    Dense-packed with type prefixes:
      [I] Redis is the caching layer; all cache keys use `user:` prefix
      [P] Deploy: push to main → wait for CI → tag release → verify staging
      [H] Always use forward slashes in CI paths (Windows compat)
    |
    v
Auto-intake (first session only):
    If no memories exist for this repo, scan project files:
    CLAUDE.md, README.md, .editorconfig, package.json, *.csproj
    Split by headings, extract entities, store as observations.
```

The 600-token budget is intentional. It's small enough to prepend to every conversation without significant context cost, but large enough to carry the most important knowledge. The type budgets ensure the agent doesn't just see 20 observations — it gets a balanced view.

### 4. Feedback (Echo / Fizzle)

After using recalled memories, agents report whether they were helpful:

- **Echo** (`was_used: true`): The memory was useful. Importance +0.05, confidence +0.10.
- **Fizzle** (`was_used: false`): The memory was irrelevant. Importance -0.10, confidence -0.15.

This creates evolutionary pressure. Useful memories rise in score and appear more often. Unhelpful ones sink and eventually decay away. Over time, the memory store self-optimizes for what agents actually find valuable.

### 5. Decay and Maintenance

Memories don't live forever (except heuristics, nearly). Eidet runs a maintenance pipeline on a schedule (default: every 24 hours) that applies several transformations:

```
Stage 1: TTL Expiry
    Remove memories past their ForgetAfter date.

Stage 2: Observation Retention
    Remove observations older than 90 days (configurable)
    that haven't been accessed recently.

Stage 3: Deduplication Sweep
    Find memory pairs with Jaccard word similarity > 0.85.
    Keep the higher-scored one, expire the other.

Stage 4: Importance Decay (FadeMem)
    Apply differential decay per type:
      decay = exp(-0.693 * (age / halfLife)^shape)
    High-confidence memories decay slower.

Stage 5a: Orphan Cleanup
    Remove broken DerivedFrom references.

Stage 5b: Enrichment Cleanup
    Strip corrupted chain-of-thought reasoning from
    Ollama-generated fields (some models leak reasoning
    even with think:false).

Stage 6: Backfill Enrichment
    Add entities and one-liners to memories missing them.

Stage 7: Auto-Consolidation
    Group related observations by tag overlap, create insights.
```

#### FadeMem Differential Decay

The decay model is inspired by how human memory works — different types of knowledge fade at different rates:

```
decay_factor = exp(-0.693 * (age_days / half_life)^shape)
```

| Type | Half-life | Shape | Behavior |
|------|-----------|-------|----------|
| Observation | 30 days | 1.2 (super-linear) | Fades fast, then gone |
| Insight | 90 days | 1.0 (exponential) | Standard decay curve |
| Procedure | 365 days | 0.8 (sub-linear) | Slow initial fade, long tail |
| Heuristic | 730 days | 0.7 (sub-linear) | Nearly immortal |

The `shape` parameter controls the curve: >1.0 means super-linear (fast initial drop), <1.0 means sub-linear (slow initial drop with a long tail). High-confidence memories have their effective age reduced, making them decay even slower.

### 6. Consolidation

Consolidation is how observations evolve into insights:

```
Group observations by tag overlap (union-find algorithm):
    obs1 tags: [redis, caching]
    obs2 tags: [caching, performance]
    obs3 tags: [redis, keys]
    → All three end up in one group (transitive merge)

If group has 3+ observations:
    Check if an existing insight covers this topic
    (vector similarity > 0.85 against existing insights)
    |
    +-- Yes: Boost existing insight's importance
    |
    +-- No: Create new insight
        - With Ollama: LLM-generated summary merging all observations
        - Without Ollama: Concatenate observation one-liners
        
    Mark source observations with DerivedFrom links
    
Apply FadeMem decay to all memories in the repo
```

---

## Storage: RavenDB

Eidet uses [RavenDB](https://ravendb.net/) as its storage engine. This isn't a typical choice for a small local tool, but it provides three capabilities that would be painful to build from scratch:

### 1. Hybrid Search in One Query

RavenDB 7.x supports vector search, full-text search, and metadata filtering in the same index. Eidet's `Memories_Search` index combines all three:

```
Content → Full-text search (Corax engine)
Content → Vector embeddings (bge-micro-v2, 384 dimensions)
RepoId, Type, Tags → Metadata filters
```

This means a single query can say "find memories in repo X that are semantically similar to 'deployment process' AND match the keyword 'staging' AND are of type 'procedure'." Most vector databases require separate queries and client-side merging.

### 2. Built-in Embeddings

RavenDB generates embeddings server-side using `bge-micro-v2`. Eidet doesn't need to run an embedding model or call an API — the database handles it. This keeps the write path zero-LLM (deterministic, fast).

### 3. Embedded Mode

For zero-setup installations, RavenDB can run embedded inside the Eidet process via the `RavenDB.Embedded` NuGet package. No separate server, no Docker container, no configuration — just `eidet setup --embedded` and it works. Data lives in `~/.eidet/data/raven`.

For developers who already run RavenDB (or want Studio access for debugging), Eidet also supports connecting to an external instance.

### The Search Index

The `Memories_Search` index is the most important piece of infrastructure:

```csharp
// Composite text field for full-text search
SearchText = Content + " " + Summary + " " + OneLiner 
           + " " + ForesightHint + " " + string.Join(" ", Tags)
           + " " + string.Join(" ", Entities)

// Same composite text for vector embeddings
SearchVector = embedding(SearchText)  // bge-micro-v2, 384 dims
```

This composite approach means that Ollama-enriched fields (summaries, one-liners, foresight hints) are searchable both by keyword and by semantic similarity. A memory about Redis caching whose LLM-generated summary mentions "distributed cache invalidation" will match searches for that phrase even if the original content was more terse.

**Important RavenDB gotcha:** RavenDB's `Search()` method uses OR semantics by default. The query `.Search("Content", text).WhereIn("RepoId", ids)` actually means `content matches text OR repoId in ids` — returning results from ALL repos. Eidet uses explicit `AndAlso()` chaining to enforce correct filter behavior.

---

## Write Gates

### Secret Scanner

The secret scanner is Eidet's first line of defense. It runs 13 regex patterns against every memory before storage:

| Pattern | Detects |
|---------|---------|
| AWS Access Key | `AKIA[0-9A-Z]{16}` |
| AWS Secret Key | `[A-Za-z0-9/+=]{40}` with AWS context |
| Private Keys | `-----BEGIN ... PRIVATE KEY-----` |
| GitHub Tokens | `gh[ps]_[A-Za-z0-9_]{36,}` |
| Slack Tokens | `xox[bpras]-...` |
| Azure Storage | `DefaultEndpointsProtocol=...AccountKey=...` |
| GCP Service Account | `"type": "service_account"` |
| Connection Strings | `password=...` in connection string format |
| High-Entropy Strings | Long hex/base64 strings that look like API keys |
| Generic API Keys | `api[_-]?key...=...` patterns |

The scanner **cannot be disabled**. This is a deliberate design choice — memories are meant to be searchable and shareable (via packs), so accidentally storing a secret could have cascading consequences.

### Signal Gate

The signal gate prevents low-value content from polluting the memory store:

**17 blocklist phrases** catch meta-commentary:
- "I should remember this"
- "Let me note that"
- "For future reference"
- Generic phrases that don't carry project-specific information

**Self-talk detection** catches agent internal monologue that sometimes leaks into store calls.

**Minimum length** ensures memories carry enough information to be useful.

---

## Enrichment: Ollama, LM Studio, or a private cluster

Enrichment is entirely optional. When enabled, Eidet uses a model server — Ollama's native API, or any OpenAI-compatible endpoint (LM Studio, llama.cpp, vLLM) — to enrich memories with the fields below.

### Backends and fallback

The configured primary backend plus an ordered `enrichment.fallbacks` list form a chain. Every call goes to the first backend that answers its health probe and returns text; one that is offline, rejects the request, or fails mid-call hands the call to the next. The intended shape is a fast private network model (a vLLM cluster behind a bearer token, `apiKey`) in front of an always-on local one. Each backend has its own five-minute health cache with a five-second probe budget, so an unreachable primary costs one probe per five minutes and nothing per call. A per-backend `thinking: false` turns a reasoning model's chain of thought off at the chat template (`chat_template_kwargs.thinking` on vLLM/llama.cpp, `think` on Ollama), which on DeepSeek-class models is roughly a fivefold saving per call; unset sends nothing so a strict gateway is never handed a field it does not know. The service log names the backend that answers and reports when a fallback takes over; `eidet doctor` prints one row per backend.

### One-liner Summaries
A single-sentence distillation of the memory's content. These are what agents see in L1 context, so they need to be dense and accurate. Example:
- **Content:** "The UserService class in src/services/user.ts handles all user CRUD operations. It uses Prisma for database access and has a caching layer that stores results in Redis with a 5-minute TTL. The cache key pattern is `user:{id}` for single users and `users:list:{page}` for paginated lists."
- **One-liner:** "UserService: Prisma-backed CRUD with Redis caching (5-min TTL, `user:{id}` and `users:list:{page}` keys)"

### Summaries
Longer summaries (2-3 sentences) for the detail panel and search results.

### Foresight Hints
Predictions about when this memory might be relevant. Example:
- **Content:** "The migration system doesn't support rollbacks. Once applied, you need to manually write a reverse migration."
- **Foresight:** "Relevant when: planning database schema changes, discussing migration strategy, or recovering from a bad migration"

### Entity Extraction (LLM Supplement)
The regex-based entity extractor catches most named entities, but the LLM can find domain-specific ones that regex misses (business terms, architectural patterns, framework-specific concepts).

### Consolidation Merge
For large observation groups (5+), the LLM writes a coherent insight summary instead of just concatenating one-liners.

### Chain-of-Thought Stripping

Some models (particularly Gemma4) output reasoning traces even with `think:false`. Eidet's `StripChainOfThought()` extracts the actual answer from `<channel|>` delimiters and `<think>...</think>` blocks. This runs both on new responses and as a maintenance cleanup stage for existing data.

---

## Layers

Eidet uses a Docker-like layer model for memory scope:

```
┌─────────────────────────┐
│     Local Layer (rw)     │  Your project's memories
├─────────────────────────┤
│    Shared Layer (ro)     │  Mounted from sibling projects
├─────────────────────────┤
│     Base Layer (ro)      │  Imported knowledge packs
└─────────────────────────┘
```

**Writes always go to the local layer.** When you store a memory, it's stored with your repo's ID and no layer ID. When you recall, Eidet searches across all mounted layers but applies a 0.8x score de-boost to non-local results.

### Layer Use Cases

- **Shared layers:** You have a monorepo with multiple services. Each service has its own memory scope, but they can mount each other's layers to share architectural knowledge.
- **Base layers:** You import a community knowledge pack (e.g., "React best practices" or "PostgreSQL optimization tips") as a read-only base layer.
- **Pack import:** When importing an `.eidet` pack file, it can be auto-mounted as a Base layer. The layer ID uses the legacy `bundle:{packId}` prefix on disk (retained for backward compatibility).

---

## The MCP Surface

Eidet exposes 13 tools via MCP. Here's what each does and when agents typically use it:

| Tool | Purpose | Typical timing |
|------|---------|----------------|
| `eidet_context` | Load L0+L1 session context | First call, session start |
| `eidet_recall` | Semantic search for memories | During work, when agent needs info |
| `eidet_store` | Save a new memory | Agent discovers something worth remembering |
| `eidet_feedback` | Echo (useful) or fizzle (irrelevant) | After using or discarding a recalled memory |
| `eidet_forget` | Soft-delete with audit trail | Agent finds a memory is wrong/outdated |
| `eidet_history` | View version chain | When investigating memory provenance |
| `eidet_intake` | Ingest project files | First session or after major changes |
| `eidet_link` | Cross-repo memory link | Connecting related knowledge across projects |
| `eidet_consolidate` | Group observations into insights | Periodically, after many stores |
| `eidet_maintenance` | Run full maintenance pipeline | Periodically, or when quality degrades |
| `eidet_export` | Export as markdown | Sharing or backup |
| `eidet_pack_export` | Export as portable .eidet file | Sharing knowledge packs |
| `eidet_pack_import` | Import .eidet pack | Importing shared knowledge |

### The Typical Agent Session

```
Session starts
  |
  +-- eidet_context → "Ah, I know this project. 142 memories.
  |                     Redis caching, JWT auth, deployment via tags..."
  |
  +-- (Agent works on task)
  |
  +-- eidet_recall("how does the auth middleware work")
  |     → 5 results, including an insight about the middleware chain
  |
  +-- eidet_feedback(insight_id, was_used=true)
  |     → Insight's importance goes up for next time
  |
  +-- (Agent discovers a new pattern)
  |
  +-- eidet_store("The rate limiter uses a sliding window with
  |    Redis MULTI/EXEC for atomicity", type=observation,
  |    tags=[redis, rate-limiting, auth])
  |
  +-- (Agent finds an old memory is wrong)
  |
  +-- eidet_forget(old_id, reason="Rate limiter was rewritten to use token bucket")
  |
  Session ends — memories persist for the next session
```

---

## Scoring

Every memory has a numeric score used for ranking in recall and context loading:

```
score = importance * 0.30
      + confidence * 0.15
      + recency    * 0.25
      + frequency  * 0.30
```

### Importance (0.0 - 1.0)

Set on creation and adjusted by feedback:
- Default: 0.5
- Echo: +0.05
- Fizzle: -0.10
- Suggested values: 0.3 (minor), 0.5 (normal), 0.8 (important), 1.0 (critical)

### Confidence (0.0 - 1.0)

Based on provenance and adjusted by feedback:
- User-stated: 0.7
- Agent-inferred: 0.6
- Echo: +0.10
- Fizzle: -0.15

### Recency

Exponential decay with a 7-day half-life:
```
recency = exp(-0.693 * days_since_creation / 7)
```
A memory created today scores 1.0. After 7 days: 0.5. After 14 days: 0.25. After 30 days: ~0.05.

### Frequency

Based on access count, capped at 10:
```
frequency = min(1.0, access_count / 10)
```
A memory accessed 10+ times scores 1.0. Never accessed: 0.0.

---

## Hooks

Eidet supports lifecycle hooks — custom commands that run at key points. Inspired by [Claude Code hooks](https://docs.anthropic.com/en/docs/claude-code/hooks).

### Hook Events

| Event | When | Can reject? |
|-------|------|-------------|
| `preStore` | Before write | Yes (non-zero exit) |
| `postStore` | After write | No |
| `preRecall` | Before search | Yes |
| `postRecall` | After search | No |
| `preForget` | Before delete | Yes |
| `postForget` | After delete | No |

### How They Work

1. Eidet serializes the event context as JSON
2. Launches the hook command as a child process
3. Writes context JSON to stdin
4. Waits for exit (up to `timeoutSeconds`)
5. Pre-hooks: non-zero exit = rejection (stderr as reason)
6. Post-hooks: fire-and-forget, exit code ignored

### Zero Overhead

When no hooks are configured, Eidet uses `NullHookRunner` — a zero-cost no-op. There's no process spawning, no JSON serialization, nothing. The overhead is a single null check.

---

## The Web UI

Eidet ships with a built-in Web UI at `http://localhost:19380/ui`. It's a vanilla HTML/CSS/JS SPA compiled as embedded resources into the binary — no framework, no build step, no external dependencies.

### Pages

| Page | Purpose |
|------|---------|
| **Dashboard** | Memory counts, agent context preview, recent memories |
| **Browser** | Full-text search + browse with type filters and pagination |
| **Graph** | Interactive knowledge graph showing memory relationships |
| **Timeline** | Chronological view grouped by date |
| **Usage** | Operations breakdown and hourly activity charts |
| **Settings** | Service status, intake/consolidate/maintenance actions |

### The Knowledge Graph

The graph page visualizes memories as an interactive force-directed network:

- **Nodes** are memories, colored by type (blue=observation, purple=insight, green=procedure, orange=heuristic)
- **Node size** reflects importance
- **Node opacity** reflects confidence
- **Edges** represent DerivedFrom and Link relationships
- **Click** a node to see its details in a side panel
- **Hover** to highlight connections
- **Drag** nodes to rearrange
- **Zoom/pan** to navigate large graphs

This gives you a visual map of how knowledge is connected — which observations led to which insights, how different topics relate, and where your knowledge graph has gaps.

---

## Security

### API Key Authentication

For non-localhost access, Eidet supports Bearer token authentication with scoped API keys:

| Scope | Allows |
|-------|--------|
| `read:all` | All read operations (recall, browse, context, etc.) |
| `write:observations` | Store observations only |
| `write:all` | Store any memory type (implies `write:observations`) |
| `admin` | Everything (implies all other scopes) |

Keys are SHA256-hashed in the config file. Health and status endpoints are always public. The Web UI is always public (it's served locally).

### Network Binding Guard

`eidet serve` refuses to start on non-localhost addresses without auth enabled. This prevents accidentally exposing your memory store to the network without authentication.

### CORS

All API responses include `Access-Control-Allow-Origin: *`. This is safe because:
1. The service runs locally by default
2. Non-localhost requires auth
3. The Web UI needs cross-origin access when served from the same host

---

## The Service

### Running Modes

| Mode | Command | Use case |
|------|---------|----------|
| **Foreground** | `eidet serve` | Development, debugging |
| **System service** | `eidet install` | Always-on background service |
| **MCP only** | `eidet mcp` | Stdio MCP server for AI clients |

### System Service Installation

`eidet install` registers Eidet as:
- **Windows:** Scheduled task (runs at login, restarts on failure up to 999 times)
- **macOS:** launchd plist (runs at login, keep-alive)
- **Linux:** systemd user unit (runs at login, restart=always)

It also auto-configures MCP for detected clients (Claude Code, Claude Desktop).

### Service Lock

Only one instance of `eidet serve` can run at a time. A PID lock file at `{configDir}/eidet.lock` prevents double-serving. The lock file contains PID, port, bind address, and start time. Stale locks (dead PID) are automatically cleaned up.

### Maintenance Scheduler

When running as a service (`eidet serve`), a background scheduler runs maintenance and consolidation at configured intervals:

- **Maintenance:** 5-minute initial delay, then every 24 hours (default)
- **Consolidation:** 2-minute initial delay, then every 6 hours (default)

Both timers use `System.Threading.Timer` and reset on service restart. There is no persistence of "last run" time — after a restart, maintenance runs 5 minutes later regardless. This is safe because all maintenance operations are idempotent.

The scheduler discovers active repos via `GetDistinctRepoIdsAsync()` and runs maintenance/consolidation for each one.

### Logging

`EidetLog` writes to `{configDir}/logs/eidet.log` with 5MB rotation. Thread-safe, never throws. Logs startup, shutdown, crashes, and unhandled API errors with stack traces. Visible via `eidet status`.

### Graceful Shutdown

`eidet serve` handles Ctrl+C and SIGTERM cleanly: stops the scheduler, disposes enrichment services, closes the RavenDB store, stops the HTTP listener. Uses linked cancellation tokens so all async operations cancel cleanly.

---

## Docker and Containers

Eidet runs in containers via the published Docker image or self-contained binaries:

```yaml
services:
  eidet:
    image: eidet/eidet:latest
    ports:
      - "19380:19380"
    environment:
      - EIDET_STORAGE_MODE=Embedded
      - EIDET_API_URL=http://0.0.0.0:19380
    volumes:
      - eidet-data:/root/.eidet
```

Environment variable overrides (`EIDET_API_URL`, `EIDET_RAVEN_URL`, `EIDET_OLLAMA_URL`, `EIDET_OLLAMA_MODEL`) enable configuration without modifying the config file.

Container detection (`/.dockerenv`, `DOTNET_RUNNING_IN_CONTAINER`, `/proc/1/cgroup`) is used for UX adjustments.

---

## Client SDKs

Eidet provides client SDKs for three languages:

### TypeScript (`@eidet/sdk`)
```typescript
import { EidetClient } from '@eidet/sdk';

const client = new EidetClient('http://localhost:19380');
const context = await client.context('my-repo');
const results = await client.recall('my-repo', 'how does auth work');
await client.store({ repo: 'my-repo', content: '...', type: 'observation' });
```

### Python (`eidet-sdk`)
```python
from eidet_sdk import EidetClient

with EidetClient("http://localhost:19380") as client:
    context = client.context("my-repo")
    results = client.recall("my-repo", "how does auth work")
    client.store(repo="my-repo", content="...", type=MemoryType.OBSERVATION)
```

### C# (`Eidet.Sdk`)
```csharp
using var client = new EidetClient("http://localhost:19380");
var context = await client.ContextAsync("my-repo");
var results = await client.RecallAsync("my-repo", "how does auth work");
await client.StoreAsync(new StoreRequest { Repo = "my-repo", Content = "...", Type = "observation" });
```

All SDKs support API key authentication, full type definitions, and expose every REST endpoint.

---

## Quality and Backup

### Memory Quality Dashboard

`eidet quality` runs 8 diagnostic checks:

| Check | What it catches |
|-------|----------------|
| Stale memories | Not accessed in a long time |
| High-fizzle | Memories repeatedly marked as irrelevant |
| Potential conflicts | Memories that might contradict each other |
| Orphan observations | Old observations that never consolidated |
| Tag concentration | Too many memories with the same tags |
| Type imbalance | Unhealthy distribution across types |
| Low-confidence | Memories with very low confidence scores |
| Missing entities | Memories without extracted entities |

Returns an overall score (0.0-1.0) and specific issues with suggestions.

### Backup and Restore

`eidet backup create` creates `.eidetbackup` files (ZIP containing RavenDB Smuggler dump + SHA256 manifest). Auto-prune keeps the last N backups (default 10).

```bash
eidet backup create          # Create a backup
eidet backup list            # List all backups
eidet backup restore <file>  # Restore from backup
eidet backup prune           # Remove old backups
```

---

## Configuration Quick Reference

Config lives at `%APPDATA%\Eidet\eidet.json` (Windows) or `~/.eidet/eidet.json` (Unix).

```bash
eidet config list              # See all settings
eidet config get memory.l1Count  # Get one setting
eidet config set service.port 19380  # Change a setting
```

### Key Settings

| Setting | Default | What it does |
|---------|---------|-------------|
| `storage.mode` | `External` | `Embedded` or `External` RavenDB |
| `memory.l1Count` | `20` | How many memories in L1 context |
| `memory.duplicateThreshold` | `0.92` | Vector similarity for duplicate detection |
| `memory.observationRetentionDays` | `90` | Max age for unused observations |
| `memory.autoIntakeOnFirstSession` | `true` | Auto-scan project files on first session |
| `maintenance.intervalHours` | `24` | Maintenance frequency |
| `maintenance.consolidationIntervalHours` | `6` | Consolidation frequency |
| `maintenance.atLocalTime` | `03:00` | Local anchor the nightly pass lands on |
| `enrichment.ollamaEnabled` | `false` | Enable Ollama enrichment |
| `enrichment.ollamaModel` | `gemma4` | Which model for enrichment |

---

## Glossary

| Term | Meaning |
|------|---------|
| **Echo** | Positive feedback — memory was useful |
| **Fizzle** | Negative feedback — memory was irrelevant |
| **FadeMem** | Differential decay algorithm — different types decay at different rates |
| **L0** | Identity line — memory count summary |
| **L1** | Top-K context — highest-scored memories loaded at session start |
| **L2** | On-demand recall — deeper search triggered by the agent |
| **Consolidation** | Grouping observations into insights |
| **Supersession** | Replacing a memory with an updated version (preserving history) |
| **Write gate** | Pre-storage filter (secret scanner + signal gate) |
| **Pack** | Portable `.eidet` file — the canonical shareable memory bundle |
| **Layer** | Scope for memory isolation (Local/Shared/Base) |
| **RepoId** | Normalized identifier for a project (e.g., `P--Eidet` for `P:\Eidet`) |
