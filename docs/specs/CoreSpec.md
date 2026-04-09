# Core Spec: Eidet — Agentic Long-Term Memory

> **Scope**: This spec defines WHAT Eidet is — domain model, memory types, layers, tiered loading, scoring, retrieval, write gates, consolidation, maintenance, and the design decisions behind them. It is implementation-agnostic: whether running as the Eidet service, embedded in a host application, or accessed via API, the core semantics are identical.
>
> *From "eidetic" — relating to extraordinarily vivid, detailed recall.*

---

## Problem Statement

- **Context amnesia**: AI assistants lose all learned context between sessions — user preferences, architectural decisions, debugging history, codebase insights.
- **Flat-file fragility**: Claude Code's `MEMORY.md` is manually curated, unsearchable beyond grep, has no semantic recall, and grows unwieldy.
- **No cross-session learning**: Repeated mistakes, re-asked questions, and re-discovered patterns waste developer time every session.
- **No recall precision**: Existing solutions either dump everything into context (token waste) or miss relevant memories (semantic gap).

## Goals

1. **Fully local, zero API keys** — RavenDB with built-in embeddings. No external services, no cloud dependencies, no Python.
2. **Per-repo memory with cross-repo linking** — each repo gets its own namespace, but memories can reference and link across repos.
3. **Typed memory entries** — observations, insights, procedures, heuristics with distinct lifecycles and retrieval characteristics.
4. **Memory layers (Docker-like)** — read-only base layers from package authors, shared team layers, and local read-write layer.
5. **Minimal wake-up cost** — L0 (~50 tokens) + L1 (~500 tokens) at session start for <600 token overhead.
6. **Hybrid retrieval** — vector search + full-text + metadata filters in a single query round-trip.
7. **Append-only corrections** — validity intervals instead of deletion; full audit trail.
8. **Intake system** — structured ingestion from CLAUDE.md, README, docs, and package bundles.
9. **Consolidation** — periodic merging of granular observations into stable insights.
10. **MCP integration** — exposed as MCP tools for any compatible AI client.
11. **Testable** — RavenDB.Embedded for integration tests; pure logic unit-testable without database.
12. **Immediate benefit** — useful from first session via intake + L0/L1 context injection.

## Non-Goals

- General-purpose knowledge base or RAG over arbitrary documents (this is memory, not search).
- LLM-in-the-loop for every write (zero-LLM write path for most operations).
- Hosting a public bundle registry (bundles are files that can be shared via any mechanism).

---

## Domain Model

### MemoryEntry (Base Document)

Every memory is a single RavenDB document with a deterministic ID.

```csharp
public class MemoryEntry
{
    // ID format: "memories/{repoId}/{type}/{shortHash}"
    // shortHash = first 12 chars of SHA256(content + createdAt)
    public string Id { get; set; } = "";

    // Namespace isolation
    public string RepoId { get; set; } = "";       // "P--TerminalHost" format
    public string? LayerId { get; set; }            // null = local layer

    // Classification
    public MemoryType Type { get; set; }
    public List<string> Tags { get; set; } = [];

    // Content
    public string Content { get; set; } = "";
    public string? Summary { get; set; }            // 1-2 sentence summary (Ollama-generated)
    public string? OneLiner { get; set; }           // ~10 word ultra-compact (for dense L1)
    public List<string> Entities { get; set; } = []; // Extracted entities (file paths, classes, etc.)

    // Temporal
    public DateTime CreatedAt { get; set; }
    public Validity Validity { get; set; } = new();
    public DateTime? ForgetAfter { get; set; }      // TTL expiry
    public string? ForgetReason { get; set; }

    // Provenance
    public MemoryProvenance Provenance { get; set; } = MemoryProvenance.AgentInferred;
    public string Source { get; set; } = "";        // "claude-session", "user", "consolidation", "intake", "bundle"
    public string? SourceSessionId { get; set; }
    public List<string> DerivedFrom { get; set; } = [];

    // Version chain
    public string? ParentMemoryId { get; set; }     // Supersedes this memory
    public bool IsLatest { get; set; } = true;

    // Cross-repo linking
    public List<MemoryLink> Links { get; set; } = [];

    // Ranking
    public float Importance { get; set; } = 0.5f;   // 0.0-1.0
    public float Confidence { get; set; } = 0.7f;   // 0.0-1.0 (separate from importance)
    public int AccessCount { get; set; }
    public DateTime? LastAccessedAt { get; set; }

    // Enrichment
    public string? ForesightHint { get; set; }      // Predictive relevance signal
}

public enum MemoryType
{
    Observation,   // Raw facts, events, decisions from a session
    Insight,       // Consolidated/derived knowledge
    Procedure,     // How-to steps, workflows, patterns
    Heuristic,     // Do/don't lessons from experience
}

public enum MemoryProvenance
{
    UserStated,      // User explicitly told the agent
    AgentInferred,   // Agent's own observation (lower starting confidence: 0.6)
    ToolOutput,      // From tool execution results
    Consolidation,   // Merged from multiple observations
    Intake,          // From file intake (CLAUDE.md, README, etc.)
    Bundle,          // From imported bundle
    System,          // System-generated
}

public class Validity
{
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }        // null = still valid

    public bool IsValidAt(DateTime t) =>
        t >= ValidFrom && (ValidUntil == null || t <= ValidUntil.Value);

    public bool IsCurrentlyValid => IsValidAt(DateTime.UtcNow);
}

public class MemoryLink
{
    public string TargetRepoId { get; set; } = "";
    public string? TargetMemoryId { get; set; }      // null = repo-level link
    public string Relation { get; set; } = "";        // "depends-on", "uses-library", "supports", "conflicts", "refines"
}
```

### RepoId Normalization

```csharp
public static class RepoIdNormalizer
{
    // Uses Claude Code's project path format: "P:\TerminalHost" → "P--TerminalHost"
    public static string Normalize(string directoryPath)
    {
        var normalized = directoryPath
            .TrimEnd('\\', '/')
            .Replace(':', '-')
            .Replace('\\', '-')
            .Replace('/', '-')
            .Replace('.', '-')
            .Replace('_', '-');
        return string.IsNullOrEmpty(normalized) ? "unknown" : normalized;
    }
}
```

---

## Memory Types

| Type | What | When Created | Lifecycle | Decay Half-Life | Example |
|------|------|--------------|-----------|-----------------|---------|
| **Observation** | Raw fact/event/decision from a session | During AI session (high volume) | Short-lived → consolidated into Insights | 30d (super-linear, shape 1.2) | "User prefers tabs over spaces in this repo" |
| **Insight** | Stable knowledge derived from observations | By consolidation or explicit store | Long-lived, updated via validity intervals | 90d (linear) | "This repo uses 4-space indentation, EditorConfig enforced" |
| **Procedure** | Reusable workflow or pattern | Explicit store when pattern emerges | Long-lived, versioned | 365d (sub-linear, shape 0.8) | "To deploy: run `dotnet publish`, then copy to server" |
| **Heuristic** | Do/don't lessons from experience | When failure patterns are identified | Nearly immortal | 730d (sub-linear, shape 0.7) | "Never run migrations before backup in this repo" |

**Research basis**: ENGRAM ablation studies show typed stores + per-type budgets beat "one giant bucket" by 30+ percentage points on LoCoMo benchmark.

---

## Memory Layers

Inspired by Docker's layered filesystem. Recall reads from all mounted layers; writes always go to the local layer.

### Layer Types

```csharp
public class MemoryLayer
{
    public string Id { get; set; } = "";             // "bundle:acme-utils-v3", "shared:team-backend"
    public string Name { get; set; } = "";
    public LayerType Type { get; set; }
    public bool ReadOnly { get; set; }               // true for base/shared, false for local
    public string? SourcePath { get; set; }
    public DateTime MountedAt { get; set; }
    public int Priority { get; set; }                // local=100, shared=50, base=10
    public List<string> ApplicableRepos { get; set; } = [];
}

public enum LayerType { Local, Shared, Base }
```

### How Layers Compose

```
┌─────────────────────────────────────────┐
│  Local Layer (read-write)               │  ← Your project memories
│  "The API timeout was changed to 30s"   │
├─────────────────────────────────────────┤
│  Shared Layer: team-backend (read-only) │  ← Team conventions
│  "We use FluentValidation for all DTOs" │
├─────────────────────────────────────────┤
│  Base Layer: vidyano-v6 (read-only)     │  ← Framework author knowledge
│  "Use AddVidyanoRavenDB() in Startup"   │
├─────────────────────────────────────────┤
│  Base Layer: acme-utils-v3 (read-only)  │  ← Library author knowledge
│  "IndexHelper.Register() auto-discovers │
│   all AbstractIndexCreationTask impls"  │
└─────────────────────────────────────────┘
```

**Key properties**:
- Writes always go to Local layer
- Base/Shared layers are immutable (agent writes cannot corrupt imported knowledge)
- Recall searches ALL layers, tags results with source layer
- Non-local results de-boosted by 0.8× (prefer local knowledge)
- New bundle version = re-import; local memories untouched
- `.eidet` pack files are portable (git, email, network share)
- Auto-mount via dependency detection (NuGet/npm refs)

### Layer Applicability

A layer applies to a repo if:
- `ApplicableRepos` is empty (universal layer), OR
- The repo's RepoId is in `ApplicableRepos`, OR
- The repo has a `depends-on` link to a repo covered by the layer

---

## Tiered Context Loading (L0 + L1 + L2)

Total wake-up cost target: **< 600 tokens**.

### L0 — Identity (~50 tokens)

Compact summary derived at query time from document counts + layer metadata + link graph.

```
Repo: TerminalHost | Stack: .NET 8, WPF, RavenDB | Last session: 2h ago
Memories: 47 observations, 12 insights, 3 procedures, 2 heuristics
Layers: acme-utils-v3 (82 entries), vidyano-v6 (45 entries)
Links: depends-on acme-utils, depends-on vidyano-service
```

### L1 — Top-K Relevant (~500 tokens)

Top 20 memories selected by scoring function from all applicable layers. Uses OneLiner > Summary > Content hierarchy for dense packing.

**Scoring**: `score = importance × 0.3 + confidence × 0.15 + recency × 0.25 + frequency × 0.3`

Where:
- `importance` = `MemoryEntry.Importance` (0.0–1.0)
- `confidence` = `MemoryEntry.Confidence` (0.0–1.0)
- `recency` = exponential decay from `CreatedAt`, half-life 7 days
- `frequency` = `min(1.0, AccessCount / 10.0)`

**Type budgets**: Insights 50%, Procedures 30%, Heuristics 20%. Ensures diverse recall.

```
L1 Context (20 memories):
[I] 4-space indentation, EditorConfig enforced
[I] Terse responses, no trailing summaries
[P] Deploy: safe dotnet publish -c Release
[H] Always update SHORTCUTS.md when adding shortcuts
[I] MainViewModel.cs is the command palette hub
[I] [acme-utils] IndexHelper auto-discovers index classes
[P] [vidyano] Use AddVidyanoRavenDB() in Startup for DI
...
```

### L2 — On-Demand Recall (unbounded)

Full hybrid search triggered by explicit `memory_recall` calls. Vector similarity + full-text + metadata filters. Cross-repo support with layer awareness.

---

## RavenDB Index

Single combined index for all retrieval modes.

```csharp
public class Memories_Search : AbstractIndexCreationTask<MemoryEntry, Memories_Search.Result>
{
    public Memories_Search()
    {
        Map = entries => from e in entries
            select new
            {
                e.Content,
                ContentVector = CreateVector(e.Content),
                e.RepoId, e.Type, Tags = e.Tags.ToArray(),
                e.CreatedAt, ValidUntil = e.Validity.ValidUntil,
                e.Importance, e.AccessCount, e.Summary,
                e.OneLiner, e.Provenance, e.ForesightHint,
                Entities = e.Entities.ToArray(),
            };

        Index("Content", FieldIndexing.Search);
        Analyze("Content", "StandardAnalyzer");

        VectorIndexes.Add(x => x.ContentVector, new VectorOptions
        {
            SourceEmbeddingType = VectorEmbeddingType.Text,
            DestinationEmbeddingType = VectorEmbeddingType.Single,
            NumberOfEdges = 20, NumberOfCandidates = 50,
        });

        StoreAllFields(FieldStorage.Yes);
        SearchEngineType = SearchEngineType.Corax;
    }
}
```

---

## Hybrid Retrieval

When recalling:

1. Resolve applicable scope: local repo + all mounted layers
2. Full-text search (keyword precision) — over-fetch 2× limit
3. Vector search (semantic recall) — minimum similarity 0.70
4. Merge, deduplicate by ID, score with layer awareness
5. De-boost non-local results by 0.8×
6. Tag results with layer source for transparency
7. Bump access count on local memories only (base/shared are read-only)
8. Apply type diversity budgets: Insights 40%, Observations 25%, Procedures 20%, Heuristics 15%
9. Sort by score descending, take limit

**Query expansion**: Short queries (1-3 words) auto-expanded with related tags from existing memories. Levenshtein-based alias resolution for close matches.

**Recall cache**: In-memory LRU (100 entries, 5min TTL). Invalidated on any write.

**Staleness warnings**: Results older than configurable threshold (default 7d) annotated with `[stale: Nd ago — verify before acting]`.

---

## Write Gates

Two pre-storage validation gates prevent noise and secrets from entering the memory store.

### Secret Scanner

Rejects content matching any of 10 regex patterns:

| Pattern | Blocks |
|---------|--------|
| `AKIA[0-9A-Z]{16}` | AWS access keys |
| `sk-[a-zA-Z0-9]{20,}` | API secret keys |
| `ghp_`, `gho_`, `ghs_`, `github_pat_` | GitHub tokens |
| `Bearer [token]{40+}` | Bearer tokens |
| `eyJ[...].` | JWT tokens |
| `-----BEGIN PRIVATE KEY-----` | Private keys |
| `Password=` / `Pwd=` | Connection string passwords |
| `API_KEY=`, `SECRET_KEY=`, etc. | Secret environment variables |
| `[base64]{40+}==` | Base64-encoded keys |
| `npm_[a-zA-Z0-9]{36}` | npm tokens |

**Critical**: Secret scanning runs locally, BEFORE any content leaves the machine. This is a hard requirement even when remote sync is enabled.

### Signal Gate

Rejects content that fails to clear the signal threshold:
- **Empty or < 20 chars**: Too short to be meaningful
- **Low-signal patterns**: "tests passed", "it works", "done", "no changes", etc.
- **Agent self-talk**: Starting with "I will...", "Let me...", "I'm going to..."

### Duplicate Gate

Near-duplicate detection: vector similarity > 0.92 against existing valid memories in same repo. If found, returns existing memory ID and asks the agent to decide: update importance, supersede, or skip.

---

## Cross-Repo Linking

Memories can reference other repos. This enables knowledge flow between related projects.

### Link Types

| Relation | Meaning | Auto-Detection |
|----------|---------|----------------|
| `depends-on` | This project uses that library | NuGet/npm package refs |
| `uses-library` | Specific library usage patterns | Import statements |
| `forked-from` | This repo was forked from that one | Git remote URL |
| `related` | General topical relationship | Manual or AI-suggested |
| `supports` | Memory-to-memory: reinforces | Auto-link on store |
| `conflicts` | Memory-to-memory: contradicts | Auto-link + Ollama |
| `refines` | Memory-to-memory: more specific | Auto-link on store |

### Cross-Repo Recall

When recalling with `cross_repo: true`:
1. Collect all repos linked via `depends-on` or `uses-library`
2. Include any mounted base/shared layers
3. Execute hybrid search across all applicable collections
4. Tag results with source repo
5. De-boost cross-repo results by 0.8×
6. Merge and return top-K

### Automatic Dependency Detection

On intake or explicit trigger: scan project for `.csproj` PackageReference, `package.json` dependencies, git submodules. Match against known repoIds. Auto-detect produced packages via `.csproj` PackageId/AssemblyName.

---

## Entity Extraction

On every `memory_store`, regex-based extraction runs (zero-LLM, always active):

| Pattern | Extracts |
|---------|----------|
| File paths | `/src/Services/MemoryService.cs` |
| Class/type names | `MemoryService`, `IMemoryStore` |
| API endpoints | `POST /api/memory` |
| CLI commands | `dotnet publish`, `git rebase` |
| Backtick code | `` `CreateVector()` `` |
| PascalCase identifiers | `TerminalHost`, `ConsolidationService` |
| Package names | `RavenDB.Client`, `pptxgenjs` |
| Environment variables | `$HOME`, `%APPDATA%` |
| URL patterns | `http://localhost:8080` |

Entities stored in `MemoryEntry.Entities` and indexed for keyword search.

---

## Consolidation

Background process that merges granular observations into stable insights.

### Algorithm

1. Query recent valid observations (not already derived-from by an insight)
2. Group by tag overlap (observations sharing >= 1 tag)
3. Groups with >= 3 observations are consolidation candidates
4. For each candidate:
   - Check existing insights for topic coverage (vector similarity > 0.85)
   - If covered: boost existing insight's importance
   - If new: create insight with Content from most representative observation (or Ollama-merged summary for groups > 5), Tags = union, Importance = mean × 1.2, DerivedFrom = source IDs
5. Source observations remain valid (they're evidence) but are excluded from future consolidation runs

### LLM-Assisted Consolidation (Optional)

For groups with > 5 observations, optionally use Ollama to generate merged summary. Falls back to most representative observation if unavailable.

---

## Maintenance Pipeline

Periodic background pipeline (default: every 24h). Runs per-repo.

| Stage | Operation | Description |
|-------|-----------|-------------|
| 1 | TTL Expiry | Expire memories past `ForgetAfter`. Sets `ForgetReason = "TTL expired"`. |
| 2 | Observation Retention | Auto-expire observations older than retention window. Skip recently accessed. |
| 3 | Dedup Sweep | Jaccard word similarity > 0.85 within same type → merge (keep higher importance). |
| 4 | Importance Decay | FadeMem differential curves per type. Skip recently accessed (< 7d). Floor: 0.05. |
| 5 | Orphan Cleanup | Remove empty-content and old low-signal system notes. |
| 6 | Backfill Enrichment | Entity extraction, heuristic one-liners, Ollama enrichment for missing fields. |
| 7 | Auto-Consolidation | Trigger consolidation alongside maintenance. |

### FadeMem Differential Decay

| Type | Half-Life | Shape | Behavior |
|------|-----------|-------|----------|
| Observation | 30 days | 1.2 (super-linear) | Decays faster than exponential — ephemeral by design |
| Insight | 90 days | 1.0 (linear) | Standard exponential decay |
| Procedure | 365 days | 0.8 (sub-linear) | Decays slower — workflows remain relevant longer |
| Heuristic | 730 days | 0.7 (sub-linear) | Nearly immortal — hard-won lessons persist |

**Activity-day awareness**: Tracks repo last-active date. Skips decay for dormant repos (prevents unfair decay during vacations/breaks). High-confidence memories decay slower (up to 1.25× half-life).

---

## Confidence Scoring

Each memory carries `Confidence` (0.0–1.0, default 0.7) independent of `Importance`.

- **Importance** = how useful this memory is (set by author, decayed over time)
- **Confidence** = how certain we are this memory is correct (updated by feedback)

### Feedback Effects (Echo/Fizzle)

| Event | Importance | Confidence |
|-------|-----------|------------|
| Echo (memory was used) | +0.05 | +0.10 |
| Fizzle (memory was irrelevant) | −0.10 | −0.15 |
| Superseded | Set to 0 (via ValidUntil) | Unchanged |

### Provenance Impact

- `UserStated`: default confidence 0.7
- `AgentInferred`: default confidence 0.6 (lower — agent may be wrong)
- `Consolidation`: inherits from source observations
- `Intake`: 0.7 (from curated files)

---

## Eidet Packs (Memory Bundles)

Exportable, shareable packages of memory — like Docker images for knowledge. Distributed as `.eidet` files.

```csharp
public class EidetPack
{
    public string Id { get; set; } = "";              // "acme-utils-v3"
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";         // Semver
    public string Author { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> ApplicablePackages { get; set; } = [];  // NuGet/npm names
    public List<MemoryEntry> Entries { get; set; } = [];
}
```

**Author workflow**: Build knowledge → `eidet pack export` → share `.eidet` file via git/email/network.

**Consumer workflow**: Place `.eidet` in pack directory → auto-mount by dependency match → pack memories appear in recall tagged with layer source.

---

## Intake System

Structured ingestion of existing knowledge sources. Provides immediate benefit from first session.

### Sources

| Source | Extracted As | Priority |
|--------|-------------|----------|
| `CLAUDE.md` | Insights | High |
| `MEMORY.md` | Insights | High |
| `~/.claude/projects/{slug}/memory/*.md` | Type-mapped entries (feedback→Heuristic, project→Observation, user→Insight) | High |
| `README.md` | Insights + Procedures | Medium |
| `.editorconfig` | Insights | Medium |
| `*.csproj` / `package.json` | Cross-repo links | Medium |
| `.memory-intake.json` | Explicit structured seeds | High |
| `.eidet` packs | Base layer entries | High |

### Intake Process

1. Trigger: first `memory_context` call for a repo (auto), or explicit `memory_intake` call
2. Scan project root for intake sources
3. Parse each source into structured chunks, classify as MemoryType, extract tags
4. Set importance by source (CLAUDE.md = 0.8, README = 0.6)
5. Check for duplicates against existing memories
6. Store with Source = "intake"
7. Scan for dependencies → create cross-repo links
8. Mount any `.eidet` packs

---

## Ollama Integration (Optional)

Local LLM enrichment — opt-in, privacy-preserving, background-only.

### 6 Enrichment Tasks

| Task | Description |
|------|-------------|
| One-Liners | Ultra-compact ~10 word summary for dense L1 packing |
| Summaries | 1-2 sentence summary for medium-context display |
| Foresight Hints | Predict when/how memory will be useful |
| Entity Extraction | LLM-assisted (supplements regex) |
| Consolidation Merge | Merge >5 observations into coherent insight |
| Conflict Detection | Flag contradictions with existing knowledge |

**Implementation**: Uses `/api/chat` with `think: false` (supports thinking models). 120s HttpClient timeout for cold starts. Lazy health re-check. Fire-and-forget for conflict detection. `NullEnricher` when disabled (zero overhead).

**Key principle**: Ollama enrichment is always additive and asynchronous. The core memory system works perfectly without it.

---

## MCP Tools

13 tools exposed via MCP:

| Tool | Group | Description |
|------|-------|-------------|
| `eidet_store` | Core | Store observations, insights, procedures, heuristics |
| `eidet_recall` | Core | Hybrid search (vector + full-text + metadata) |
| `eidet_context` | Core | L0 + L1 context block for session start |
| `eidet_forget` | Core | Soft-delete with reason tracking |
| `eidet_intake` | Intake | Ingest CLAUDE.md, README, deps as seeds |
| `eidet_link` | Linking | Cross-repo and memory-to-memory relationships |
| `eidet_consolidate` | Lifecycle | Merge observations into insights |
| `eidet_history` | Lifecycle | Version chain for a memory |
| `eidet_feedback` | Lifecycle | Echo/fizzle feedback for recall quality |
| `eidet_maintenance` | Lifecycle | Dedup, decay, TTL expiry, orphan cleanup |
| `eidet_export` | Sharing | Export memories as formatted markdown |
| `eidet_pack_export` | Sharing | Export as shareable .eidet pack |
| `eidet_pack_import` | Sharing | Import .eidet pack as read-only layer |

---

## Design Decisions

### Why RavenDB (not SQLite + ChromaDB)?

| Factor | RavenDB | SQLite + ChromaDB |
|--------|---------|-------------------|
| Vector search | Built-in, single index | Separate service |
| Full-text search | Built-in Corax engine | Requires FTS5 |
| Hybrid search | Single round-trip | Two queries + merge |
| Embeddings | Built-in (`CreateVector`) | External model |
| Testing | RavenDB.Embedded (in-process) | Multiple mocks |
| .NET integration | First-class client | Multiple libraries |
| Operational | Single process | Two processes |

### Why Typed Memories?
ENGRAM ablation: +30pp on LoCoMo benchmark. Enables per-type retention, retrieval strategies, consolidation, and L1 formatting.

### Why Append-Only with Validity Intervals?
Zep/Hindsight research: preserves audit trail, debugging history, learning from mistakes, safety. **Also critical for sync**: append-only events are trivially replicable with no conflict resolution needed.

### Why Zero-LLM Write Path?
Deterministic, fast, free, testable. No API latency or model drift. LLM only used optionally for consolidation merge.

### Why Per-Repo Isolation with Cross-Repo Linking?
Isolation by default (React patterns don't leak into Go projects), explicit sharing (opt-in links), clear ownership (deletable local layer), composable (Docker-like stacking), scalable (1000 memories per repo, not 100K in one bucket).

### Why Docker-Like Layers?
Authorship (curate separately), immutability (your work builds on top), portability (.eidet packs), composability (dependency graph), trust boundaries (read-only layers can't be corrupted by writes).

### Why an Intake System?
Immediate value from session one. Bridges MEMORY.md → semantic search. Idempotent re-runs. Structured seeding > gradual rediscovery.

---

## Performance Targets

| Metric | Target |
|--------|--------|
| Recall (hybrid search, 10K memories) | p95 < 100ms |
| L0 + L1 context generation | p95 < 50ms |
| Single memory store | p95 < 20ms |
| Session wake-up token cost | < 600 tokens |

---

## Security

- **Fully local by default**: RavenDB and API localhost-bound. Built-in embeddings = zero data leaves machine.
- **Write gates**: Secret scanning + signal gate + duplicate detection before any storage.
- **No instructions in memory**: Content is data, never treated as system instructions. No prompt injection vector.
- **Provenance tracking**: Every memory records source and session ID. Tainted memories traceable and bulk-invalidatable.
- **Layer isolation**: Base/shared are read-only. Agent writes cannot corrupt imported knowledge.
- **Bundle trust**: Explicit user import only. No auto-download from external sources.
- **Soft delete only**: No permanent destruction via MCP tools.
- **Cross-repo boundaries**: Cross-repo recall is opt-in, follows explicit links only.

---

## Research Sources

| Enhancement | Source | Impact |
|-------------|--------|--------|
| Typed memory + budgets | ENGRAM | +30pp on LoCoMo benchmark |
| Differential decay (FadeMem) | FadeMem | 45% storage reduction |
| Version chains | Supermemory | Full supersession audit trail |
| Echo/fizzle feedback | @jumperz / Codex | Closes recall quality loop |
| Auto-link (Zettelkasten) | A-MEM (NeurIPS 2025) | Automatic knowledge graph |
| Heuristic memory type | ERL (ICLR 2026) | Do/don't lessons, near-immortal |
| Entity extraction | Cognee / Neo4j | 9 regex patterns, zero-LLM |
| Foresight hints | EverMemOS | Predictive relevance signal |
| Provenance tracking | Mem0 | User vs agent vs tool origin |
| Activity-day decay | MIRA-OSS | Fair decay for dormant repos |
| Secret scanning gate | Gigabrain / Codex / Hermes | Block secrets at write time |
| Confidence scoring | @jumperz | Separate from importance |
| Dense L1 packing | Hmem | 20+ entries via one-liners |
| Staleness warnings | Claude Code production | A/B tested annotation |
