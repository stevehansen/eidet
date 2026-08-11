---
name: maintenance
description: Prime on Eidet's maintenance pipeline before changing it — the MaintenanceOrchestrator stage order, IMaintenanceStage/MaintenanceContext contract, TTL expiry, observation retention, dedup, FadeMem importance decay, ROI decay, budget eviction, deprecation, consolidation, the ACE-style Reflector, drift review, and the nightly forget-integrity audit. Use when the task touches a maintenance stage, ConsolidationEngine, DedupEngine, ReflectionEngine, RecallConsistencyGuard, FadeMemCurve, RetentionScore, or a nightly/scheduled corpus rewrite. Not for the LLM backend those stages call (see enrichment), not for one-off ingestion (see intake), not for the read path (see recall).
---

# Maintenance — priming

**Canonical spec:** `docs/domains/maintenance.md` — read it for the full stage order, all invariants,
key files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Lifecycle. Design rationale:
`docs/specs/CoreSpec.md`.

A fixed-order pipeline of small stages that either move scores, retire memories, or mint new ones. The
order in `MaintenanceOrchestrator.DefaultStages()` is load-bearing: retention runs after importance is
final, and `ForgetIntegrityStage` runs last so it audits what this run produced.

## Core invariants (get these right)

- **One bulk scope per run** — every stage and engine writes through `ctx.Write`, so touched scopes
  invalidate exactly once in the `finally`. A stage using its own `MemoryService` breaks coherence.
- **One object per memory per pass** — `ctx.Store` is a `SharedEntryStore`, the read-side twin of that
  rule. Stages write whole documents off index-backed queries, so without shared instances a late stage
  persists a copy loaded before an early stage's write and silently reverts its field. A stage or engine
  that opens its own read port re-introduces it; the auditor's raw store is a deliberate exception.
- **A stage failure is a report line, not an aborted run** (per-stage try/catch → `StageOutcome`).
- **Stage selection compares `Name` strings, never parses them** — selection must stay total.
- **Every stage must converge** — "does nothing the second time" is correctness, not optimization.
  Consolidation gets there two ways, and both are load-bearing: it never emits content a source already
  holds (compose the cluster, don't nominate a member), and its lineage check reads the *whole* history
  via `GetConsolidatedSourceIdsAsync`, retired memories included. Scope lineage to live memories and any
  stage that retires something — repair, dedup, TTL — silently un-consolidates the cluster it touched.
- **Never touch a `canon:*` page** in dedup or the consolidation boost path.
- **No merge without the recall-consistency veto** — the survivor must still surface for the discard's
  own retrieval intent. A veto forgets nothing and stamps `LastMergeRejectedAt`. The guard discounts rows
  whose document is already retired, because a bulk run closes documents while the search index lags.
- **Shared `DerivedFrom` is not a duplicate signal in general — but an identical one *is*, for
  consolidation only.** A lineage pass over *any* source was tried and reverted: family members scored
  0.12–0.29 lexical similarity to their survivor (the lexical pass folds at 0.85), and Reflection
  deliberately emits multi-aspect insights over one source set, so for it identical sources means same
  evidence, not same claim. Consolidation is the opposite: one bucket yields one merge, so a repeated
  lineage is accidental re-derivation, and the low similarity is a *paraphrase*, not a distinct aspect —
  78 live insights over one 14-observation cluster in a single repo, all restating it. Hence
  `CorpusRepairStage`'s fold is scoped to `Source == "consolidation"` and keeps the oldest. The
  two-altitude pair survives because the abstraction cites the fine procedure ahead of the cluster.
- **Body-less intake memories are retired, not repaired.** A heading with nothing under it has no
  content to render, so clearing its fabricated one-liner just falls through to the heading. Scoped to
  `Provenance == Intake`; a terse hand-authored memory is never touched.
- **Tag and entity hygiene are repair, not content edits** — both are *derived* retrieval keys, so
  re-deriving them claims nothing new. Both `Clean` calls are idempotent, which is what keeps the stage
  a no-op on a clean corpus.
- **Never fold a claim into its contradiction** — early return on conflicting hard valence; partition
  consolidation groups by sign *before* the minimum-group check.
- **Synthesis inherits its least-trusted contributor**, and a boost with no trusted contributor is
  skipped (anti-laundering).
- **Reflection output must pass the write gates** (LLM-fresh text) and all trust-bearing fields are
  engine-owned, never model-owned.
- **Retire by forget-with-reason, never hard delete.** Quarantined memories are exempt from eviction.
- **Optional stages ship dormant** — drift, reflection, budget eviction, deprecation all no-op without
  config (drift/reflection also need an available backend).

## Key files / reuse

- `src/Eidet.Core/Maintenance/MaintenanceOrchestrator.cs` — order + isolation + the bulk scope.
- `src/Eidet.Core/Maintenance/IMaintenanceStage.cs` — the contract, `MaintenanceContext`, `ForTest`.
- `src/Eidet.Core/Maintenance/SharedEntryStore.cs` — the pass-scoped id → instance map. Read its header
  before adding a stage that mutates an existing entry.
- `src/Eidet.Core/Maintenance/{ConsolidationEngine,DedupEngine,ReflectionEngine}.cs` — dual-use engines
  (stand-alone or joined to a bulk scope via the `write` parameter).
- `src/Eidet.Core/Maintenance/{RecallConsistencyGuard,FadeMemCurve,TagOverlapGrouper}.cs` — the
  deterministic helpers; reuse rather than re-deriving similarity or decay.

## Gotchas

- `FadeMemCurve.Defaults` is keyed by `MemoryType` — a new type not added there throws at decay time
  *and* breaks recall recency.
- Dedup's semantic and lexical thresholds are a pair; move one and duplicate behaviour shifts silently.
  The lexical pass is O(n²) over the per-type candidate cap. All three passes share ONE candidate
  fetch — they mutate survivors in place, so a second fetch would let a later pass write back a stale
  copy and drop an earlier fold.
- `MaintenanceContext.Items` is an untyped stage-to-stage scratch dict — keep it near-empty.
- `ForTest` builds engines on a throwaway `MemoryService`; that only works because engines write through
  the supplied scope.
- The two-altitude path emits *two* procedures per staged cluster, distinguished by a one-tick
  `CreatedAt` difference.
- Observation retention adds a grace window on top of the cutoff — age alone doesn't predict expiry.
