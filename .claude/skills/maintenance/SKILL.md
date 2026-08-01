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
- **A stage failure is a report line, not an aborted run** (per-stage try/catch → `StageOutcome`).
- **Stage selection compares `Name` strings, never parses them** — selection must stay total.
- **Never touch a `canon:*` page** in dedup or the consolidation boost path.
- **No merge without the recall-consistency veto** — the survivor must still surface for the discard's
  own retrieval intent. A veto forgets nothing and stamps `LastMergeRejectedAt`.
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
- `src/Eidet.Core/Maintenance/{ConsolidationEngine,DedupEngine,ReflectionEngine}.cs` — dual-use engines
  (stand-alone or joined to a bulk scope via the `write` parameter).
- `src/Eidet.Core/Maintenance/{RecallConsistencyGuard,FadeMemCurve,TagOverlapGrouper}.cs` — the
  deterministic helpers; reuse rather than re-deriving similarity or decay.

## Gotchas

- `FadeMemCurve.Defaults` is keyed by `MemoryType` — a new type not added there throws at decay time
  *and* breaks recall recency.
- Dedup's semantic and lexical thresholds are a pair; move one and duplicate behaviour shifts silently.
  The lexical pass is O(n²) over the per-type candidate cap.
- `MaintenanceContext.Items` is an untyped stage-to-stage scratch dict — keep it near-empty.
- `ForTest` builds engines on a throwaway `MemoryService`; that only works because engines write through
  the supplied scope.
- The two-altitude path emits *two* procedures per staged cluster, distinguished by a one-tick
  `CreatedAt` difference.
- Observation retention adds a grace window on top of the cutoff — age alone doesn't predict expiry.
