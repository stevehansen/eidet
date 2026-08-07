# Maintenance

The scheduled passes that rewrite the corpus between sessions — expiry, dedup, decay, consolidation,
reflection, retention, and the nightly integrity audit.

**Status:** current as of [#80](https://github.com/stevehansen/eidet/issues/80) · **Governing issues:**
[#22](https://github.com/stevehansen/eidet/issues/22) (composable stages + orchestrator),
[#39](https://github.com/stevehansen/eidet/issues/39) (recall-consistency guard, budgeted forgetting,
two-altitude consolidation), [#60](https://github.com/stevehansen/eidet/issues/60) (ACE-style
Reflector), [#55](https://github.com/stevehansen/eidet/issues/55) (ROI decay).
**Priming skill:** [`.claude/skills/maintenance/SKILL.md`](../../.claude/skills/maintenance/SKILL.md)

## What it is

A fixed-order pipeline of small, independently testable stages, run by the scheduler nightly and
reachable ad-hoc from REST/CLI. Stages either **move scores** (decay, ROI, drift), **retire memories**
(TTL, retention, dedup, eviction), or **mint new ones** (consolidation, reflection).

It is *not* the LLM plumbing the enrichment/drift/reflection stages call into (**enrichment** owns the
port and the prompts), *not* one-off ingestion (**intake**), and *not* the scheduler process itself
(`src/Eidet.Service/Scheduler/` — service plumbing, see `docs/specs/ServiceSpec.md`).

## Core entities & relationships

```
IMaintenanceRunner ← MaintenanceOrchestrator
    RunAsync(MaintenanceRequest)                  // OnlyStages / SkipStages, by stage NAME
      → MemoryService.RunBulkAsync                // ONE bulk scope per run: invalidate once, in a finally
        → MaintenanceContext { Store, Write, Enrichment, Consolidation, Reflection, Dedup,
                               Auditor, RepoId, IsRepoActive, Now, Items, per-feature configs }
        → foreach stage: ExecuteAsync → StageOutcome(Name, Affected, Error?)   // per-stage try/catch
      → MaintenanceReport { Stages[], CompletedAt }

Dual-use engines (also callable stand-alone from REST/CLI, with or without a bulk scope):
  ConsolidationEngine  — observations → insight, or the two-altitude procedure pair; + importance decay
  DedupEngine          — semantic pass then lexical pass → one deterministic MergeAsync
  ReflectionEngine     — feedback residue → net-new memories via one LLM call (dormant by default)
```

Stage order is declared once in `MaintenanceOrchestrator.DefaultStages()` and it is **load-bearing** —
retention runs after importance is final, and `ForgetIntegrityStage` runs last so it audits the state
this run produced.

## Invariants & rules

- **One bulk scope per run.** Every direct-writing stage and both dual-use engines write through
  `ctx.Write`, so touched scopes are invalidated exactly once in the `finally`. A stage that writes via
  its own `MemoryService` breaks recall coherence for the whole run.
- **A stage failure is a report line, not an aborted run.** The orchestrator try/catches each stage and
  records `StageOutcome(name, 0, error)`. Cancellation breaks the loop cleanly.
- **Stage selection compares names, never parses them.** A stage whose `Name` has no `MaintenanceStep`
  member simply never matches an `Only` filter instead of throwing — selection stays total.
- **A scheduled stage must converge.** Every stage runs repeatedly over a corpus that mostly does not
  change, so "does nothing the second time" is a correctness property, not an optimization. Consolidation
  enforces it with a *lineage* check — the set of observation ids already folded into a live derived
  memory (`ConsumedObservationIdsAsync`); a fully-consumed bucket is skipped, and the boost path lifts
  importance only for contributors not already in `DerivedFrom`. It deliberately does **not** ask "does
  content like this exist": an unenriched consolidation emits the representative observation's content
  verbatim, so the nearest match to a bucket's output is the bucket's own input, and a content probe
  reads done as not-done. That defect re-emitted one observation 240 times on the 6-hour schedule.
- **Tag unions are ranked and capped, never raw.** A consolidated memory can itself be re-consolidated,
  so `TagHygiene.Clean` bounds `unionTags`; an uncapped union compounds each generation until tags
  cover the corpus (observed at 199 tags on one entry).
- **`CorpusRepairStage` is idempotent by construction.** It doubles as the migration for corpora damaged
  by older builds and as standing hygiene, so it carries no version flag or run-once marker — a repo
  that was never repaired and one repaired long ago then re-damaged want the same action. It runs
  before `DedupSweep` so exact folds shrink the similarity candidate set and stale seed importance
  can't decide a survivor.
- **Dedup and consolidation must never touch a `canon:*` page.** Canon pages are human-approved
  syntheses; they are excluded from the dedup candidate set and from the consolidation boost path.
- **A merge is vetoed unless the survivor still surfaces for the discard's own retrieval intent.**
  `RecallConsistencyGuard` proves it structurally (vector arm, lexical fallback) before anything is
  folded. A veto forgets nothing — both memories stay live and the discard gets a
  `LastMergeRejectedAt` stamp so the dashboard can show it.
- **Never fold a claim into its contradiction.** `MergeAsync` returns early on conflicting hard
  valence, and consolidation partitions every tag group by valence sign *before* the minimum-group
  check. Both survivors keep the opinionated stance (**memory** owns the sign helper).
- **Synthesis inherits its least-trusted contributor** on both the create *and* the boost path, and a
  boost with zero trusted contributors is skipped entirely — otherwise low-trust observations could
  launder themselves into a trusted insight's importance and lineage (**writepath**).
- **Reflection content is LLM-fresh, so it must pass the write gates.** Consolidation may skip them
  (its text is pre-gated memory content); reflection may not. All trust-bearing fields
  (importance, confidence, provenance) are engine-owned, never model-owned.
- **Every retirement is a forget-with-reason, never a hard delete** — so a wrongly-evicted memory is
  restored by clearing `ValidUntil`/`ForgetReason`, and the integrity auditor covers it.
- **Quarantined memories are exempt from budget eviction** — a quarantined memory must stay recallable
  long enough to earn the echo that clears it.
- **Decay is skipped for an inactive repo**, and `IsRepoActive` is derived in exactly one place so a
  CLI caller can't accidentally decay a dormant corpus.
- **The optional stages ship dormant.** Drift review, reflection, budget eviction, and deprecation all
  no-op unless their config enables them (drift and reflection additionally require an available
  enrichment backend).

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Maintenance/MaintenanceOrchestrator.cs` | Stage order, per-stage isolation, the one bulk scope |
| `src/Eidet.Core/Maintenance/IMaintenanceStage.cs` | The stage contract + `MaintenanceContext` (incl. `ForTest`) |
| `src/Eidet.Core/Maintenance/Stages/` | One file per stage; each `internal`, each with its own `StageName` |
| `src/Eidet.Core/Maintenance/ConsolidationEngine.cs` | Grouping, valence partitioning, anti-laundering, two-altitude emission, importance decay |
| `src/Eidet.Core/Maintenance/DedupEngine.cs` | Semantic + lexical passes and the single `MergeAsync` |
| `src/Eidet.Core/Maintenance/Stages/CorpusRepairStage.cs` | Idempotent repair: exact-content folds (cross-type), tag hygiene, intake importance re-baseline, heading-only one-liner clearing |
| `src/Eidet.Core/Text/TagHygiene.cs` | The tag noise rule + growth cap shared by mining, consolidation, and the write gate |
| `src/Eidet.Core/Maintenance/RecallConsistencyGuard.cs` | The per-merge retrievability veto |
| `src/Eidet.Core/Maintenance/ReflectionEngine.cs` | Residue arms (echoes / loose ends / drift) → net-new memories |
| `src/Eidet.Core/Maintenance/FadeMemCurve.cs` | Per-type half-life/shape table — the decay *and* recall-recency source |
| `src/Eidet.Core/Maintenance/TagOverlapGrouper.cs`, `src/Eidet.Core/Text/WordSimilarity.cs` | Deterministic grouping / similarity helpers |
| `src/Eidet.Core/Memory/RetentionScore.cs` | The eviction ordering key |
| `src/Eidet.Core/Maintenance/IMaintenanceRunner.cs` | The thin facade the scheduler / REST / CLI depend on |

## Gotchas

- **`FadeMemCurve.Defaults` is a dictionary indexed by `MemoryType`.** A new memory type that isn't
  added to it throws at decay time — and the same table feeds recall's recency, so a missing entry
  breaks ranking too.
- **Dedup runs two passes with different thresholds**, both below the write-time duplicate threshold;
  the semantic pass exists to catch paraphrases and the lexical pass exists so embeddings-less installs
  don't regress. Changing one threshold without the other silently shifts which duplicates survive.
- **The lexical dedup pass is O(n²)** over the per-type candidate cap. That cap is the only thing
  bounding it.
- **`MaintenanceContext.Items`** is a stage-to-stage scratch dictionary. It exists, it's untyped, and
  it should stay near-empty — reach for it only when a stage genuinely needs a predecessor's output.
- **`MaintenanceContext.ForTest` builds engines on a throwaway `MemoryService`.** That's only coherent
  because engines write through the supplied bulk scope; an engine that ever fell back to its own scope
  would invalidate the wrong cache in tests and pass anyway.
- **Consolidation's two-altitude path emits *two* procedures per staged cluster** and links the
  abstraction to the fine-grained one; the ids differ only by a tick on `CreatedAt`, which is what keeps
  them distinct.
- **The observation retention stage applies a grace window on top of the cutoff**, so a recently
  *accessed* old observation survives. "Older than the retention window" alone doesn't predict expiry.

## Executable references

- `tests/Eidet.Core.Tests/Maintenance/MaintenanceOrchestratorTests.cs` + `MaintenanceStageIsolationTests.cs`
  — **the authority on orchestration**: stage isolation, `Only`/`Skip` selection totality, and report shape.
- `tests/Eidet.Core.Tests/Maintenance/MaintenanceCacheInvalidationTests.cs` — settles the
  one-invalidation-per-scope-per-run contract (including when a stage throws).
- `tests/Eidet.Core.Tests/Maintenance/DedupEngineTests.cs` + `DedupGuardVetoTests.cs` +
  `RecallConsistencyGuardTests.cs` — settle survivor selection, tag union, the veto, and the
  `LastMergeRejectedAt` stamp.
- `tests/Eidet.Core.Tests/Maintenance/ConsolidationTrustTests.cs` + `TwoAltitudeConsolidationTests.cs` +
  `ValenceWritePathGuardTests.cs` — settle anti-laundering on both paths, the procedure pair, and
  polarity partitioning.
- `tests/Eidet.Core.Tests/Maintenance/RetentionStagesTests.cs` + `RoiDecayStageTests.cs` +
  `FadeMemCurveTests.cs` — settle eviction ordering, the quarantine exemption, ROI demotion, and the
  per-type curves.
- `tests/Eidet.Core.Tests/Maintenance/{ReflectionEngine,ReflectionStage,DriftReviewStage,OllamaEnrichmentStage}Tests.cs`
  — settle the dormant-by-default gates and the LLM-facing stages' behaviour when the backend is absent.
- `tests/Eidet.Core.Tests/Maintenance/MaintenanceRepoActiveDerivationTests.cs` — settles the single
  `IsRepoActive` derivation site.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Lifecycle (Consolidation, Maintenance, TTL expiry, FadeMem),
  Feedback & scoring (ROI)
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) § consolidation, FadeMem decay,
  maintenance pipeline
- Related domains: **memory** (the bulk mutation scope) · **recall** (shares `FadeMemCurve`; reads the
  scores these stages move) · **writepath** (`ForgetIntegrityStage` runs the auditor; anti-laundering
  rules) · **enrichment** (the backend the LLM stages call) · **canon** (excluded from dedup and
  consolidation) · **looseends** (a reflection residue arm)
- Priming skill: `.claude/skills/maintenance/SKILL.md`
