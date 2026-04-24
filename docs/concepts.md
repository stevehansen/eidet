---
title: Concepts
nav_order: 3
---

# Concepts

## Memory Types

Eidet uses four memory types, inspired by the [ENGRAM research](https://arxiv.org/abs/2401.14112) which showed +30 percentage points improvement over single-bucket memory systems.

| Type | Description | Decay rate | Example |
|------|-------------|------------|---------|
| **Observation** | Raw facts noticed during a session | Fast (30-day half-life) | "The `UserService` talks to Redis for caching" |
| **Insight** | Confirmed knowledge distilled from observations | Medium (90-day half-life) | "Redis is the caching layer; all cache keys use `user:` prefix" |
| **Procedure** | Multi-step workflows | Slow (365-day half-life) | "To add a new API endpoint: create handler, add route, update OpenAPI spec, add test" |
| **Heuristic** | Rules of thumb, decision shortcuts | Nearly immortal (730-day half-life) | "Always prefer `httpx` over `requests` in this codebase" |

Observations are the most numerous and decay fastest. Insights are consolidated from groups of related observations. Procedures and heuristics are rare but long-lived.

## Tiered Loading

Agents don't load all memories at once. Eidet uses a three-tier system:

### L0 — Identity (always loaded)

A single line showing memory counts:

```
[Memory: 142 entries, 45 observations, 67 insights, 18 procedures, 12 heuristics]
```

### L1 — Top-K Context (loaded at session start)

The 20 highest-scored memories, dense-packed into under 600 tokens:

```
[I] Redis is the caching layer; all cache keys use `user:` prefix
[P] Deploy: push to main → wait for CI → tag release → verify staging
[H] Always use forward slashes in CI paths (Windows compat)
```

Type budgets ensure diversity: ~50% insights, ~30% procedures, ~20% heuristics.

### L2 — On-Demand Recall

Deeper recall triggered by the agent when it needs specific knowledge. Uses hybrid retrieval (vector + full-text) with scoring and type diversity budgets.

## Scoring

Each memory is scored by combining four signals:

| Signal | Weight | Description |
|--------|--------|-------------|
| **Importance** | 30% | Set on creation (0.0–1.0), adjusted by feedback |
| **Confidence** | 15% | Provenance-based: user-stated = 0.7, agent-inferred = 0.6 |
| **Recency** | 25% | Exponential decay, 7-day half-life |
| **Frequency** | 30% | Access count, capped at 10 accesses |

The formula: `score = importance * 0.3 + confidence * 0.15 + recency * 0.25 + frequency * 0.3`

## Hybrid Retrieval

Recall runs two searches in parallel:

1. **Full-text search** — keyword matching via RavenDB's Corax engine
2. **Vector search** — semantic similarity via `bge-micro-v2` embeddings

Results are merged, deduplicated, and scored. Full-text hits get a slight score boost (1.0 vs 0.9 for vector-only), then type diversity budgets are applied:

| Type | Budget |
|------|--------|
| Insights | 40% |
| Observations | 25% |
| Procedures | 20% |
| Heuristics | 15% |

## Write Gates

Every store operation passes through two gates before anything is written:

### Secret Scanner

Blocks content containing patterns that look like secrets:

- AWS keys (`AKIA...`)
- Private keys (`-----BEGIN ... PRIVATE KEY-----`)
- GitHub/Slack/Azure tokens
- Connection strings with passwords
- High-entropy strings (potential API keys)

The secret scanner **cannot be disabled**. It runs locally before any storage.

### Signal Gate

Blocks low-value content:

- Self-talk and meta-commentary ("I should remember this")
- Tool output dumps
- Generic observations that could apply to any project
- Content shorter than a useful threshold

## Feedback (Echo / Fizzle)

Agents report whether recalled memories were actually useful:

- **Echo** (wasUsed = true): Boosts importance +0.05, confidence +0.1
- **Fizzle** (wasUsed = false): Reduces importance -0.1, confidence -0.15

This creates a natural selection pressure — useful memories rise, unhelpful ones fade.

## Consolidation

Periodically, Eidet groups related observations (by tag overlap using union-find) and creates insights from groups of 3+. If an insight already covers the topic (vector similarity > 0.85), it gets boosted instead of creating a duplicate.

With Ollama enabled, large observation groups (5+) get LLM-generated insight summaries.

## FadeMem Differential Decay

Each memory type decays at a different rate, following a modified exponential curve:

```
decay = exp(-0.693 * (age / halfLife)^shape)
```

| Type | Half-life | Shape | Behavior |
|------|-----------|-------|----------|
| Observation | 30 days | 1.2 (super-linear) | Fast fade |
| Insight | 90 days | 1.0 (linear) | Standard exponential |
| Procedure | 365 days | 0.8 (sub-linear) | Slow fade |
| Heuristic | 730 days | 0.7 (sub-linear) | Nearly immortal |

High-confidence memories decay slower (confidence acts as a brake).

## Layers

Eidet uses a Docker-like layer model:

| Layer | Access | Description |
|-------|--------|-------------|
| **Local** | Read/Write | Your project's memories |
| **Shared** | Read-only | Mounted from sibling projects or team packs |
| **Base** | Read-only | Imported knowledge packs |

Writes always go to the local layer. Non-local results get a 0.8x score de-boost.

## Supersession

Memories can be superseded rather than deleted. When you store a new memory with `supersedes: "old-memory-id"`, the old memory's `ValidUntil` is set and it stops appearing in recall. The full version chain is preserved for audit.

## Entity Extraction

Eidet automatically extracts named entities from memory content:

- File paths (`src/auth/handler.ts`)
- Class/function names (`UserService`, `handleAuth()`)
- Package names (`@prisma/client`, `httpx`)
- Environment variables (`DATABASE_URL`)
- URLs, ports, error codes

Entities improve search precision and enable the knowledge graph visualization.
