---
name: recall
description: Prime on Eidet's read path before changing it — hybrid lexical+vector+abstraction fusion, UCB exploration, dual-clock FadeMem recency, per-repo alpha learning, graph-neighbour and cue-anchor expansion, trust/ROI/quarantine de-boosts, per-type budgets, the generation-token recall cache, and L0/L1 context assembly. Use when the task touches RecallScoring, Fuse, RecallCache, RecallOptions/MemoryQuery, GetContextAsync, the wake-up budget, stage or valence recall filters, Memories_Search, or "why did this memory (not) come back". Not for the write-time gates or the trust model itself (see writepath), not for the MemoryEntry shape (see memory), not for retrieval benchmarking (see quality).
---

# Recall & context — priming

**Canonical spec:** `docs/domains/recall.md` — read it for the full pipeline order, all invariants, key
files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Retrieval & context loading. Design
rationale: `docs/specs/CoreSpec.md`.

Two ranked surfaces, deliberately not unified: **Recall** (hybrid query → fused → policy → budgeted →
cached) and **Context** (L0 identity + L1 dense-packed one-liners, <600 tokens). **writepath** defines
trust; this domain only multiplies by it.

## Core invariants (get these right)

- **`RecallScoring.Fuse` is the only home of the ranking math, and production policy stays outside
  it** — trust, ROI, layer de-boost, and quarantine are applied after `Fuse` because the benchmark
  calls `Fuse` directly.
- **Every field that changes the result set belongs in `RecallCache.ComputeKey`** — including the
  rounded alpha bucket and *both expansion flags*. A missing filter serves another query's cached
  results; this has bitten three times.
- **Resolve alpha before `TryGet`** (it's part of the key), and **`Set` drops the write** if any
  tracked scope generation moved during the query. That is the coherence guarantee.
- **`FunctionalStage.None` is a wildcard** — the filter is `Stage == requested OR Stage == None`.
  Filter clauses need explicit `AndAlso()`; RavenDB's default would widen them.
- **Three arms, and α blends only two of them.** α is lexical-vs-vector; the abstraction arm adds
  `β·normAbs` on top. An absent third arm scores identically to two-arm fusion — which is what keeps
  the benchmark stable and lets a store fake skip the arm entirely.
- **Both expansions trust the loaded entry, not the pointer**: in-scope by real `RepoId` *and*
  `ValidUntil is null`. An `IsLatest`-only check resurfaces forgotten memories. Cue expansion
  re-checks the store's own results — a lax backend must not be able to leak through recall.
- **Each reachability path needs its own `IntegrityCheck`**, and every probe pins the flags of the
  paths it isn't testing. A new expansion without one fails the auditor's coverage guard.
- **L1 carries no Observations** (`[Insight, Procedure, Heuristic]` only) — a failure stored as an
  Observation never resurfaces at wake-up.
- **Procedures are hard-capped in the wake-up**; freed slots backfill insights, never heuristics.
- **De-boost, never hide** — every policy term is multiplicative.

## Key files / reuse

- `src/Eidet.Core/Memory/RecallScoring.cs` — `Fuse`, `ExpandNeighbors`, `ExpandEntities`,
  `ApplyTypeBudgets`, `ComputeL1Score`.
- `src/Eidet.Core/Indexes/Memories_Search.cs` — `SearchVector` (whole entry) and `AbstractionVector`
  (derived one-liner, `IsNullOrEmpty`-chained so a *redacted* field falls through); same embedder.
- `src/Eidet.Core/Memory/RecallCache.cs` — the key and the generation tokens.
- `src/Eidet.Core/Services/MemoryService.cs` — `RecallAsync`, `GetContextAsync`, the policy layer.
- `src/Eidet.Core/Memory/MemoryRoi.cs`, `src/Eidet.Core/Maintenance/FadeMemCurve.cs`,
  `src/Eidet.Core/Layers/LayerScope.cs` — the multipliers.

## Gotchas

- `ExplainRecallAsync` is pre-expansion, cache-bypassing, hook-free — neither graph neighbours nor cue
  matches ever show up.
- Cue expansion is only as dense as enrichment: `Entities` is LLM-extracted, so the path is a no-op on
  an unenriched corpus. The abstraction arm is not — it falls back to `Content` at index time.
- Two recency curves on purpose: fixed 7-day for L1, per-type `FadeMemCurve` for fusion. Don't unify.
- Valence glyphs exist only in `RecallToolHandler`; the wake-up has no stance marker and no dead-end
  floor (the designed negative-valence floor was never shipped).
- A denying `PreRecall` hook returns an empty list, indistinguishable from "no matches".
- Access-count writes on the read path are intentional, patch-only, and exempt from invalidation —
  cached recalls are slightly stale on recency by design.
- Type budgets are soft: a backfill pass tops results up to `Limit`.
