# Recall & context

Turning a query into ranked memories, and packing the <600-token wake-up an agent gets for free.

**Status:** current as of [#80](https://github.com/stevehansen/eidet/issues/80) · **Governing issues:**
[#33](https://github.com/stevehansen/eidet/issues/33) (hybrid fusion, dual-clock recency, alpha
learning, graph expansion), [#35](https://github.com/stevehansen/eidet/issues/35) (ROI gating),
[#38](https://github.com/stevehansen/eidet/issues/38) (stage filter),
[#9](https://github.com/stevehansen/eidet/issues/9) (cache coherence).
**Priming skill:** [`.claude/skills/recall/SKILL.md`](../../.claude/skills/recall/SKILL.md)

## What it is

The read side. Two ranked surfaces, deliberately not unified:

- **Recall** — a hybrid query (lexical + vector) fused, policy-adjusted, type-budgeted, and cached.
- **Context** — the L0 identity line plus L1 dense-packed one-liners assembled at session start.

It is *not* the write path's trust model (**writepath** defines `MemoryTrust`; recall only multiplies
by it), *not* the maintenance passes that mutate the stored fields ranking reads (**maintenance**), and
*not* the retrieval-quality measurement harness (**quality**).

## Core entities & relationships

```
RecallAsync(repo, RecallOptions)
  → ResolveScopeAsync ............... LayerScope (primary repo + mounted layers; cross-repo opt-in)
  → ResolveAlphaAsync ............... override ?? learned RepoUsage.AlphaLex ?? default, clamped
  → RecallCache.TryGet .............. key includes alpha bucket; snapshots scope generations
  → SearchScoredAsync × 2 ........... Lexical and Vector arms, in parallel
  → RecallScoring.Fuse .............. per-arm min-max normalize → α·lex + (1-α)·vec + UCB + recency
  → ExpandNeighborsAsync ............ one hop, damped inheritance, bounded
  → per-candidate policy ............ × MemoryTrust × MemoryRoi × non-local de-boost × quarantine
  → RecallScoring.ApplyTypeBudgets .. rerank-before-truncate to Limit
  → BumpAccessCountsAsync (patch-only, fire-and-forget) + RecallCache.Set (may drop)

GetContextAsync(repo, maxTokens)
  → L0: counts by type + open Loose End count
  → Loose End wake-up slice (sub-budget carved from L1, never from L0)
  → L1: GetTopScoredAsync([Insight, Procedure, Heuristic]) → ComputeL1Score → budgets → token-bounded
```

`FusedCandidate` carries every component (`Lex`, `Vec`, `Recency`, `Ucb`, `Fused`) so
`ExplainRecallAsync` can show the arithmetic; `MemorySearchResult` is the shaped output that also
carries `TrustFactor`, `RoiFactor`, and a staleness/drift warning string.

## Invariants & rules

- **`RecallScoring.Fuse` is the single home of the ranking math, and production policy stays outside
  it.** Trust, ROI, layer de-boost, and quarantine are applied by the caller *after* `Fuse`, because
  the benchmark scorecard calls `Fuse` directly and folding retrieval policy in would unfairly
  penalize its Procedure/Heuristic gold cases. Owned by `src/Eidet.Core/Memory/RecallScoring.cs`.
- **Every field that changes the result set must be in the cache key.** Repo, text, type, valence,
  stage, tags, limit, include-expired, cross-repo, *and* the rounded alpha bucket. A filter missing
  from the key lets a filtered recall collide with an unfiltered one and serve the wrong results —
  this has happened twice. Owned by `RecallCache.ComputeKey`.
- **The learned alpha is resolved *before* `TryGet`**, because it is part of the key — a learned shift
  then invalidates cleanly via the bucket instead of serving results ranked under an old blend.
- **A recall drops its own cache write if any tracked scope's generation moved during the query.**
  That, plus the mutation-side generation bump (**memory**), is the whole coherence guarantee.
- **`FunctionalStage.None` is a wildcard, not a value.** The stage filter matches
  `Stage == requested OR Stage == None`; dropping the `None` arm silently hides every stage-agnostic
  memory. Owned by `src/Eidet.Core/Storage/RavenEidetStore.cs` (filter clauses use explicit
  `AndAlso()` — RavenDB's default OR semantics would otherwise widen the filter).
- **Graph expansion trusts the loaded entry, never the link.** A neighbour is admitted only if its
  *actual* `RepoId` is in scope and `Validity.ValidUntil is null` — an `IsLatest`-only check
  resurfaces forgotten memories through a live parent, which is exactly what the `GraphNeighbor`
  integrity probe hunts for.
- **L1 wake-up carries no Observations.** Candidates are `[Insight, Procedure, Heuristic]` only — which
  is why a failure worth keeping must never be stored as an Observation (it would never resurface).
- **Procedures are hard-capped in the wake-up**, below their soft type budget, because a
  wrongly-recalled procedure is net-negative; the freed slots backfill *insights* (fully trusted), not
  heuristics (equally action-shaped).
- **Recall de-boosts, never hides.** Non-local, low-trust, negative-ROI, and quarantined memories all
  survive with a multiplied-down score.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Memory/RecallScoring.cs` | `Fuse`, `ExpandNeighbors`, `ApplyTypeBudgets`, `ComputeL1Score`, `RecallWeights` |
| `src/Eidet.Core/Memory/RecallCache.cs` | Bounded TTL cache + per-scope generation tokens + the key |
| `src/Eidet.Core/Memory/MemoryRoi.cs` | The ROI factor that demotes proven net-negative Procedures/Heuristics |
| `src/Eidet.Core/Memory/RecallExplanation.cs` | Per-candidate component breakdown (diagnostic surface) |
| `src/Eidet.Core/Maintenance/FadeMemCurve.cs` | Per-type dual-clock recency used inside `Fuse` |
| `src/Eidet.Core/Services/MemoryService.cs` | `RecallAsync` / `GetContextAsync` / `ExplainRecallAsync` and the policy layer |
| `src/Eidet.Core/Domain/MemoryQuery.cs` | The resolved query (filters, limit, expansion flags) |
| `src/Eidet.Core/Indexes/Memories_Search.cs` | Composite `SearchText` + `SearchVector` projection; enum fields for `WhereEquals` |
| `src/Eidet.Core/Layers/LayerScope.cs` | Scope resolution + the non-local de-boost constant |
| `src/Eidet.Service/Tools/Handlers/RecallToolHandler.cs` | Agent-facing render, including the `✗`/`⚠` valence glyphs |

## Gotchas

- **`ExplainRecallAsync` is a pre-expansion view** that also bypasses the cache and fires no hooks. Its
  rows show arm-fusion math only, so a link-reachable candidate production recall *would* surface is
  simply absent. Don't debug a missing result with it alone.
- **Two recency curves, on purpose.** L1 uses a fixed 7-day half-life (`ComputeL1Score`); recall fusion
  uses the per-type `FadeMemCurve`. They rank for different purposes and are deliberately not unified —
  don't "fix" the duplication.
- **The valence glyphs live only in the recall tool render, not in the wake-up.** `GetContextAsync`
  still emits `[I]`/`[P]`/`[H]` with no stance marker and no reserved dead-end slots — the designed
  bounded floor for negative-valence memories was never shipped. Check before claiming an agent "sees"
  dead-ends at session start.
- **A `PreRecall` hook that denies returns an empty list, not an error.** A misconfigured hook looks
  exactly like "no memories match".
- **Access tracking writes on the read path** — deliberately, through a patch-only context, and
  deliberately exempt from cache invalidation. So a cached recall can be marginally stale on recency
  within the cache TTL. That is the accepted trade, not a bug to fix.
- **Alpha is clamped to a band** so neither arm can ever be fully muted by learning; a benchmark run
  that expects a pure-lexical or pure-vector ranking must pass an override.
- **Type budgets are soft**: a second pass backfills to `Limit` after the budgeted pass, so an
  over-represented type can still fill the tail.

## Executable references

- `tests/Eidet.Core.Tests/Memory/RecallFusionTests.cs` — **the authority on `Fuse`**: per-arm
  normalization edge cases (empty arm, all-equal scores), the outer join, UCB, and
  rerank-before-truncate ordering.
- `tests/Eidet.Core.Tests/Memory/AlphaLearningTests.cs` — settles the EWMA fold, the clamp band, and
  that alpha participates in the cache key.
- `tests/Eidet.Core.Tests/Memory/GraphExpansionTests.cs` — settles bounded one-hop expansion, damped
  inheritance, and the scope/validity re-check that stops a forgotten neighbour resurfacing.
- `tests/Eidet.Core.Tests/Memory/RecallTrustGatingTests.cs` + `RoiGatingTests.cs` — settle that policy
  multiplies rather than filters.
- `tests/Eidet.Core.Tests/Memory/ContextProcedureCapTests.cs` — **the authority on the wake-up
  budget**: the hard procedure cap and insights (not heuristics) absorbing the freed slots.
- `tests/Eidet.Core.Tests/Memory/FunctionalStageTests.cs` — settles `None`-as-wildcard in the hard
  pre-filter.
- `tests/Eidet.Core.Tests/Services/MemoryServiceBoundaryTests.cs` — cache coherence under concurrent
  store-during-recall (shared with **memory**, which owns the invalidation side).

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Retrieval & context loading (Recall, Context, L0/L1/L2,
  Foresight hint, Cross-repo), Feedback & scoring (Echo, Fizzle, FadeMem, ROI)
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) § tiered loading, scoring, hybrid
  retrieval
- Related domains: **memory** (owns the fields ranking reads, and cache invalidation) · **writepath**
  (defines trust) · **maintenance** (rewrites importance/ROI/drift between recalls) · **sharing**
  (layer scope and the non-local de-boost) · **looseends** (the wake-up slice) · **quality** (measures
  this pipeline)
- Priming skill: `.claude/skills/recall/SKILL.md`
