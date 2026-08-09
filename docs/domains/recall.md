# Recall & context

Turning a query into ranked memories, and packing the <600-token wake-up an agent gets for free.

**Status:** current as of the abstraction-arm + cue-anchor work (MEMORA review, 2026-08-07) ·
**Governing issues:**
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
  → SearchScoredAsync × 3 ........... Lexical, Vector and Abstraction arms, in parallel
  → RecallScoring.Fuse .............. per-arm min-max normalize → α·lex + (1-α)·vec + β·abs + UCB + recency
  → ExpandNeighborsAsync ............ authored links: one hop, damped inheritance (0.5), bounded
  → ExpandEntitiesAsync ............. shared entities: strongest-parent inheritance (0.35), bounded
  → per-candidate policy ............ × MemoryTrust × MemoryRoi × non-local de-boost × quarantine
  → RecallScoring.ApplyTypeBudgets .. rerank-before-truncate to Limit
  → BumpAccessCountsAsync (patch-only, fire-and-forget) + RecallCache.Set (may drop)

GetContextAsync(repo, maxTokens)
  → L0: counts by type + open Loose End count
  → Loose End wake-up slice (sub-budget carved from L1, never from L0)
  → L1: GetTopScoredAsync([Insight, Procedure, Heuristic]) → ComputeL1Score → budgets → token-bounded
```

`FusedCandidate` carries every component (`Lex`, `Vec`, `Abs`, `Recency`, `Ucb`, `Fused`) so
`ExplainRecallAsync` can show the arithmetic; `MemorySearchResult` is the shaped output that also
carries `TrustFactor`, `RoiFactor`, and a staleness/drift warning string.

Two reachability paths sit between fusion and policy, and they answer different questions. Link
expansion follows edges somebody **authored**; cue expansion follows entities enrichment
**extracted**, so a related memory nobody ever linked is still reachable. Both admit candidates
*before* trust gating, so an expanded memory faces exactly the same downstream policy as a direct hit.

## Invariants & rules

- **`RecallScoring.Fuse` is the single home of the ranking math, and production policy stays outside
  it.** Trust, ROI, layer de-boost, and quarantine are applied by the caller *after* `Fuse`, because
  the benchmark scorecard calls `Fuse` directly and folding retrieval policy in would unfairly
  penalize its Procedure/Heuristic gold cases. Owned by `src/Eidet.Core/Memory/RecallScoring.cs`.
- **What is rendered is part of the read path, not a presentation detail.** A memory's `OneLiner` is a
  ~12-word model abstraction that reliably drops the class names, thresholds and file paths that make
  it actionable, so a renderer preferring it hands the agent a topic label and silently discards the
  knowledge the store retained. `RecallToolHandler` therefore renders full `Content` for the top
  `DetailedHits` and stays terse below the cut; only 45 of 15.6k live memories had a null `OneLiner`,
  so an `OneLiner ?? Summary ?? Content` chain makes the later branches unreachable in practice.
- **A candidate pool cut by importance decides the ranking before the ranking runs.** `GetTopScoredAsync`
  can only order by `Importance` in the index, but callers re-rank on access frequency and dual-clock
  recency, which the index cannot see. It therefore over-fetches (`PoolOverfetch`, capped at `MaxPool`)
  so a high-importance, never-used seed cannot lock out earned knowledge that wins on the full score —
  and so the client-side `IsLatest` filter doesn't eat into the caller's budget.
- **The wake-up slice dedups on what it renders.** L1 shows one-liners, and distinct memories routinely
  abstract to near-identical sentences, so two paraphrases would otherwise both consume one of only 20
  slots under a 600-token cap. The `L1DuplicateThreshold` word-overlap check is deliberately looser than
  the write-time duplicate gate: a false positive here costs the next-best line, not stored knowledge.
- **That word-overlap check cannot see a paraphrase, so consolidation is capped separately.** Word
  overlap catches a reworded *template*; it cannot catch the same claim written twice in different
  vocabulary, which is precisely what a scheduled re-consolidation of one observation cluster produces.
  Measured across ten repos: **97% of duplicate wake-up lines were consolidation output**, at a median
  word overlap of **0.25** — far under the threshold. Hence `consolidationWakeupCap` (6 of 20 slots,
  the share procedures already get), matched on `Source` rather than `Provenance` because
  consolidation's anti-laundering rule stamps the least-trusted *contributor's* provenance. The cap is
  deliberately asymmetric: a symmetric per-source cap was measured to evict good lines from repos whose
  knowledge is mostly session-sourced and genuinely varied.
- **The L1 candidate pool is 120, not 20, because of that cap.** A cap that rejects candidates mid-scan
  needs alternatives to backfill with, and at a pool of 60 a consolidation-heavy repo simply ran out —
  the cap bought diversity by *shortening* the wake-up, trading duplication for silence. At 120 the
  freed slots refill from other sources: measured 179 of 181 slots retained while redundant lines fell
  from 80 to 35.
- **Every field that changes the result set must be in the cache key.** Repo, text, type, valence,
  stage, tags, limit, include-expired, cross-repo, *both expansion flags*, *and* the rounded alpha
  bucket. A filter missing from the key lets a filtered recall collide with an unfiltered one and
  serve the wrong results — this has happened three times, most recently with the expansion flags,
  where the integrity auditor's per-path probes (which differ *only* by those flags) could answer each
  other from cache. Owned by `RecallCache.ComputeKey`.
- **The abstraction arm rides on top of the α blend, never inside it.** α answers "which arm does this
  repo reward"; the abstraction arm does not participate in that question, and folding it in would make
  the learned alpha mean two things. It contributes `β·normAbs` additively. An *absent* abstraction arm
  normalizes to 0 for every candidate, so two-arm fusion is bit-identical to three-arm fusion with no
  third arm — which is what keeps the benchmark scorecard's numbers stable and every store fake honest.
- **The abstraction is derived at index time, never stored.** `Memories_Search` projects the first
  *non-empty* of `OneLiner`, `Summary`, `Content` (clamped). `IsNullOrEmpty`, not `??`, because null
  means "awaiting enrichment" but empty means **redacted** — a redacted one-liner must fall through
  rather than embed nothing. The `Content` fallback is what keeps the arm dense on a zero-LLM write
  path: every memory has an abstraction from the moment it is stored, enriched or not.
- **The learned alpha is resolved *before* `TryGet`**, because it is part of the key — a learned shift
  then invalidates cleanly via the bucket instead of serving results ranked under an old blend.
- **A recall drops its own cache write if any tracked scope's generation moved during the query.**
  That, plus the mutation-side generation bump (**memory**), is the whole coherence guarantee.
- **`FunctionalStage.None` is a wildcard, not a value.** The stage filter matches
  `Stage == requested OR Stage == None`; dropping the `None` arm silently hides every stage-agnostic
  memory. Owned by `src/Eidet.Core/Storage/RavenEidetStore.cs` (filter clauses use explicit
  `AndAlso()` — RavenDB's default OR semantics would otherwise widen the filter).
- **Both expansions trust the loaded entry, never the pointer.** A neighbour is admitted only if its
  *actual* `RepoId` is in scope and `Validity.ValidUntil is null` — an `IsLatest`-only check
  resurfaces forgotten memories through a live parent, which is exactly what the `GraphNeighbor` and
  `EntityNeighbor` integrity probes hunt for. Cue expansion repeats the check on the store's results
  rather than trusting them: a backend that forgets to filter must not be able to leak through recall.
- **Every reachability path carries its own `IntegrityCheck`, and every probe pins the flags of the
  paths it is not testing.** Otherwise a leak is attributed to the wrong mechanism or masked by a
  neighbouring one. Adding an expansion without its own check fails the auditor's coverage guard.
- **A shared entity is weaker evidence than an authored link**, so cue expansion damps harder (0.35 vs
  0.5) and runs *second* — link expansion claims a memory at the higher decay first, and cue expansion
  skips anything already in the pool.
- **Cue expansion is only as dense as enrichment.** Entities are LLM-extracted, so this path is a
  no-op on an unenriched corpus. That is a property to remember when a recall "should" have reached
  something, not a bug.
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
| `src/Eidet.Core/Memory/RecallScoring.cs` | `Fuse`, `ExpandNeighbors`, `ExpandEntities`, `ApplyTypeBudgets`, `ComputeL1Score`, `RecallWeights` |
| `src/Eidet.Core/Memory/RecallCache.cs` | Bounded TTL cache + per-scope generation tokens + the key |
| `src/Eidet.Core/Memory/MemoryRoi.cs` | The ROI factor that demotes proven net-negative Procedures/Heuristics |
| `src/Eidet.Core/Memory/RecallExplanation.cs` | Per-candidate component breakdown (diagnostic surface) |
| `src/Eidet.Core/Maintenance/FadeMemCurve.cs` | Per-type dual-clock recency used inside `Fuse` |
| `src/Eidet.Core/Services/MemoryService.cs` | `RecallAsync` / `GetContextAsync` / `ExplainRecallAsync` and the policy layer |
| `src/Eidet.Core/Domain/MemoryQuery.cs` | The resolved query (filters, limit, expansion flags) |
| `src/Eidet.Core/Indexes/Memories_Search.cs` | Composite `SearchText` + `SearchVector`, derived `AbstractionText` + `AbstractionVector`, lower-cased keyword-analyzed `Entities`; enum fields for `WhereEquals` |
| `src/Eidet.Core/Layers/LayerScope.cs` | Scope resolution + the non-local de-boost constant |
| `src/Eidet.Service/Tools/Handlers/RecallToolHandler.cs` | Agent-facing render: `✗`/`⚠` valence glyphs, and full content for the top `DetailedHits` |

## Gotchas

- **A vector query needs an explicit `AndAlso()` before `VectorSearch`, and getting it wrong is silent.**
  `.WhereIn("RepoId", ids).VectorSearch(...)` emits the two clauses adjacent, and the server rejects the
  whole query with a parse error at the vector clause. Every vector call site wraps its query in
  `catch { return []; }` — a deliberate "embeddings may not be configured" fallback — so a malformed query
  is reported as *no semantic hits*, which is exactly what a healthy embeddings-less install looks like.
  Three sites shipped this way (`VectorScoredAsync`, `FindDuplicateCoreAsync`, `FindNearDuplicatesAsync`),
  which meant the semantic arm, the abstraction arm, the vector write-time duplicate gate and dedup's
  semantic pass were all dead in production while ~1,500 tests stayed green. Nothing caught it because no
  fixture provisioned an embeddings task, so the entire suite exercised only the lexical fallback —
  `VectorSearchArmTests` now stands up a real one. If you touch a vector query, keep the operator explicit
  and assume any `catch`-to-empty is hiding a bug until a live-embeddings test says otherwise.

- **`numberOfCandidates` is a ceiling on rows returned, not just search breadth.** It has to track the
  requested limit; pinned below it, `.Take(limit)` silently yields the smaller number, so a request for a
  wide page quietly truncates.

- **The lexical arm has no relevance floor.** RavenDB's `Search` is OR-by-default, so any single query
  term matches, and `Fuse` min-max-normalizes each arm — which maps the best candidate to 1.0 *whatever
  its raw score*. A query with no real match therefore returns a full page of confident-looking noise
  rather than nothing. The semantic arms are floored (`VectorSimilarityMinimum`, now actually read from
  `memory.vectorSimilarityMinimum` — it was a live config knob that nothing consumed), but an absolute
  lexical floor still needs calibration work. Treat a uniformly low-relevance result set with suspicion.

- **`ExplainRecallAsync` is a pre-expansion view** that also bypasses the cache and fires no hooks. Its
  rows show arm-fusion math only, so a candidate production recall *would* surface via a link or a
  shared entity is simply absent. Don't debug a missing result with it alone.
- **Two vectors are indexed per memory, and the query text is embedded by the same task for both**, so
  the arms are on one similarity scale and `Fuse` can normalize them independently without per-arm
  calibration. Change one embedder and you must change the other.
- **Cue matching is case-insensitive only because BOTH sides lower-case.** `Entities` is projected
  lower-cased in the index and the cue values are lower-cased in the query — `KeywordAnalyzer`
  preserves case, so dropping either side makes the lookup silently match *nothing*. It did exactly
  that on first run; `CueAnchorQueryTests` is the regression guard.
- **Both new queries fail SILENTLY in production.** The abstraction arm degrades to `[]` like any
  vector arm, and a throwing cue lookup is swallowed by the expansion wrapper. That is correct for a
  best-effort enhancement, but it means a broken query translation looks exactly like "nothing
  related" — which is why the cue lookup has integration coverage against real RavenDB rather than
  fakes alone.
- **The abstraction arm has no integration coverage.** The integration fixture never configures the
  embeddings task, so every vector arm returns `[]` there — the arm's Raven query is exercised only by
  its structural equivalence to the `SearchVector` query it mirrors. Verify it against a real
  embeddings-enabled store before trusting a β tuning result.
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
- `tests/Eidet.Core.Tests/Memory/EntityExpansionTests.cs` — **the authority on cue anchors**:
  strongest-parent attribution, best-score-first capping, case-insensitive matching, the three
  admission guards against a store that skips its own filters, and that the two expansions are
  independent (with a memory reachable both ways keeping the stronger link score).
- `tests/Eidet.Core.Tests/Memory/AbstractionArmTests.cs` — **the authority on the third arm**: that an
  absent arm is bit-identical to two-arm fusion for any β, that β adds on top of the blend, that an
  abstraction-only hit enters the pool, and that it carries zero lexical share for alpha learning.
- `tests/Eidet.Core.Tests/Memory/RecallTrustGatingTests.cs` + `RoiGatingTests.cs` — settle that policy
  multiplies rather than filters.
- `tests/Eidet.Core.Tests/Memory/ContextProcedureCapTests.cs` — **the authority on the wake-up
  budget**: the hard procedure cap and insights (not heuristics) absorbing the freed slots.
- `tests/Eidet.Core.Tests/Memory/ContextConsolidationCapTests.cs` — **the authority on the source
  cap**: that it binds when consolidation outranks everything, that the slots it frees backfill from
  another source rather than shortening the wake-up, and that a wholly session-sourced repo is left
  uncapped.
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
