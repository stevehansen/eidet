# Eidet — Architecture Friction & Deepening Candidates

> Captured from `/improve-codebase-architecture` exploration on 2026-05-05.
> Goal: identify shallow modules + tight coupling clusters that would benefit from being deepened (small interface hiding larger implementation).
> No interfaces proposed yet — this is just the punch list to come back to.

## TL;DR

Strongest candidates: **#1 (Memory pipeline)** ✅ done (RFC #9) and **#3 (Hook plumbing)** → RFC [#19](https://github.com/stevehansen/eidet/issues/19) (open).
Skip: **#6 (Intake)** — just refactored. **#7 (Enrichment)** — already correct ports & adapters shape.

---

## 1. Memory pipeline: `MemoryService` + `MemoryWriter` + `MemoryRecall` + `RecallScoring` + `RecallCache` — ✅ DONE

> Shipped 2026-05-09 via RFC [#9](https://github.com/stevehansen/eidet/issues/9) (commit `25abde0`).
> `MemoryWriter` / `MemoryRecall` / `MemoryQueries` collapsed into `MemoryService`; storage writes go
> through a private `MutationCtx` gate; `RecallCache` rewritten with per-scope generation tokens;
> 4 latent staleness bugs in bulk-write paths fixed via tactical patches.
> Phase 2 (migrate bulk callers onto `BulkMutationCtx`) tracked in [#10](https://github.com/stevehansen/eidet/issues/10).

**Files**
- `src/Eidet.Core/Memory/MemoryWriter.cs`
- `src/Eidet.Core/Memory/MemoryRecall.cs`
- `src/Eidet.Core/Memory/RecallScoring.cs`
- `src/Eidet.Core/Memory/RecallCache.cs`
- `src/Eidet.Core/Services/MemoryService.cs`

**Coupling**
- `MemoryWriter` invalidates `RecallCache` after every mutation (store/supersede/forget/feedback/edit/link).
- `MemoryRecall` reads `RecallCache` before every recall.
- `RecallScoring.ApplyTypeBudgets` is a pure function called from exactly one site inside `MemoryRecall.RecallAsync` (post-merge, pre-sort).
- `MemoryService` is a thin facade that constructs Writer + Recall and delegates.
- The store-then-recall contract is an implicit cross-object protocol — forget to invalidate in a new mutation path → silent stale read.

**Dependency category**
- In-process. `IEidetStore` is local-substitutable (RavenDB embedded for tests).

**Tests**
- `MemoryServiceTests` covers facade end-to-end.
- Scoring + cache tested in isolation.
- The integration seam (cache coherence under concurrent mutations) is **not** asserted.

**Why this is #1**
Textbook "pure-extraction-for-testability + implicit shared-mutable-state" pattern. Real bugs hide in invalidation order and budget-vs-flow alignment, not in `ApplyTypeBudgets` math. Boundary tests on a unified module would replace several internal-state tests.

---

## 2. API/Tools three-tier routing: HTTP endpoint → `ToolDispatcher` → `IToolHandler`

**Files**
- `src/Eidet.Service/Api/EidetApiServer.cs`
- `src/Eidet.Service/Api/Endpoints/*` — 11 endpoint classes
- `src/Eidet.Service/Tools/ToolDispatcher.cs`
- `src/Eidet.Service/Tools/ToolDispatcherFactory.cs`
- `src/Eidet.Service/Tools/Handlers/*` — 13 handler classes
- `src/Eidet.Service/Tools/{ToolArgs,ToolRequest,ToolResult}.cs`

**Coupling**
- Same operation traverses three layers: endpoint marshals HTTP → `ToolRequest`; dispatcher routes by name + wraps exceptions; handler parses args from `JsonElement` and calls a Core service.
- Argument-parsing logic duplicated between `ToolArgs` and individual handlers.
- MCP & REST both reuse handlers but each adds its own thin transport wrapper.

**Dependency category**
- In-process to Core services.

**Tests**
- Endpoint-level + handler-level tests both exist.
- End-to-end HTTP→Core service tests are sparse.

**Note**
The handler split is intentional (sharing between MCP and REST). Question is whether the dispatcher pulls its weight or whether dispatcher + handler should fold into one boundary class per operation. Bigger architectural conversation than #1.

---

## 3. Hook event plumbing — 🔬 RFC FILED

> Promoted to RFC [#19](https://github.com/stevehansen/eidet/issues/19) on 2026-06-01 (open):
> collapse firing onto `MutationKind`, kill the duplicated runner predicate.

**Files**
- `src/Eidet.Core/Services/HookRunner.cs` (defines `HookEvent` enum + `IHookRunner` + `NullHookRunner` + `HookRunner`)
- Injection sites: `MemoryWriter` (PreStore / PostStore / PreForget / PostForget), `MemoryRecall` (PreRecall / PostRecall)

**Coupling**
- Six call sites scattered across two classes.
- `HookEvent` is a hardcoded enum — adding a new event = hunt-and-poke across mutation paths.
- Pre-hooks block synchronously via `Allowed=false`.
- Post-hooks fire-and-forget via `Task.Run` with no await — failure semantics are not documented.
- No contract test asserts "every mutation fires its expected hook".

**Dependency category**
- The hooks themselves are true-external (shell/HTTP webhooks) — category 4.
- The event-firing logic is in-process.

**Tests**
- `HookRunnerTests` covers the runner itself.
- No "mutation X fires hook Y" contract test.

---

## 4. Maintenance stages + orchestrator — ✅ DONE

> Shipped 2026-06-12 via RFC [#22](https://github.com/stevehansen/eidet/issues/22) → PR [#23](https://github.com/stevehansen/eidet/pull/23).
> Design hardened 2026-06-11 via `/design-interface`.
> Verdict: **stage count is not the problem.** Chosen hybrid — collapse the redundant `IMaintenanceRunner`/`IMaintenanceOrchestrator`
> double-facade, add a `RunAsync(string repo)` happy-path overload that derives `IsRepoActive` (fixes a CLI footgun),
> add a `MaintenanceContext.ForTest` seam so the 10 stages become unit-testable, and type `OnlyStages`/`SkipStages`
> as a `MaintenanceStep` enum. Rejected: folding stages into private methods (kills testability — the scope invariant
> is already structurally sealed), `RunsAfter` topo-sort (speculative), engine ports (shallow pass-throughs).

**Files**
- `src/Eidet.Core/Maintenance/MaintenanceOrchestrator.cs`
- `src/Eidet.Core/Maintenance/IMaintenanceStage.cs`
- `src/Eidet.Core/Maintenance/MaintenanceContext.cs`
- `src/Eidet.Core/Maintenance/Stages/*` — 9 stage files
- `src/Eidet.Core/Maintenance/ConsolidationEngine.cs`
- `src/Eidet.Core/Maintenance/TagOverlapGrouper.cs`
- `src/Eidet.Core/Maintenance/FadeMemCurve.cs`

**Coupling**
- Many stages are 10–50-line wrappers over one store call. Example: `ConsolidationStage` is essentially `await ctx.Consolidation.ConsolidateAsync()`.
- Pure helpers (`FadeMemCurve`, `TagOverlapGrouper`) live in the Maintenance folder but each is used by exactly one stage.
- `MaintenanceContext` is a god-bag every stage reaches into.

**Dependency category**
- In-process. `IEidetStore` is local-substitutable.

**Tests**
- `MaintenanceOrchestratorTests` + helper tests exist.
- Individual stage classes are barely tested (they're thin wrappers).

**Caveat**
The stage interface exists for `OnlyStages` / `SkipStages` composability — there *is* a reason for the split. The question is whether 9 classes is the right granularity vs. one runner with named steps.

---

## 5. Gates: `WriteValidator` + `IValidationRule` + 2 rules — ✅ DONE

> Shipped 2026-06-13 via RFC [#30](https://github.com/stevehansen/eidet/issues/30) → PR [#31](https://github.com/stevehansen/eidet/pull/31) (squash `bcfed1e`).
> Killed the `IValidationRule` interface + the static `Rules[]` array; `WriteValidator.Validate` now calls secret-scan
> then signal in an explicit ordered body (kept `SecretScanRule` isolated for its 13 security regexes; folded `SignalRule`
> into a private `CheckSignal`). Replaced the 9-arg `TryBuildStoreEntry` with `BuildEntry(StoreOptions)` and
> `TryBuildEditEntry` with `BuildEditEntry(MemoryEntry, EditOptions)`; validation stays folded into both build paths so
> secret-scan can't be bypassed. +11 boundary tests.
> The original premise below was stale — `WriteValidator` had grown from ~23 LOC to 131 by absorbing the canonical
> entry-construction path, so the design was hardened against that current shape. Rejected: a pluggable `ValidationPipeline`
> (over-engineering with no 3rd rule; downgrades the secret-scan guarantee from structural to test-enforced).

**Files**
- `src/Eidet.Core/Gates/WriteValidator.cs` (~23 lines)
- `src/Eidet.Core/Gates/IValidationRule.cs` (~10 lines)
- `src/Eidet.Core/Gates/SecretScanRule.cs` (~77 lines, 13 regex patterns)
- `src/Eidet.Core/Gates/SignalRule.cs` (~58 lines)
- `src/Eidet.Core/Gates/ValidationResult.cs`

**Coupling**
- `WriteValidator` is 23 lines and statically iterates a hardcoded rule array.
- Two rules form a single "is-this-writeable" concern split into separate files for no architectural reason.
- No third rule has shown up.

**Dependency category**
- Pure (in-process).

**Tests**
- `WriteValidatorTests` covers the chain.
- Rules tested through it, not in isolation.

**Note**
Smallest absolute impact (~170 LOC total) but cleanest "shallow module" example in the repo. Good warm-up refactor.

---

## 6. Intake extractors — ⚠ SKIP

**Files**
- `src/Eidet.Core/Intake/IntakeService.cs`
- 6 extractor classes (`ClaudeMdExtractor`, `ReadmeExtractor`, `EditorConfigExtractor`, `DocsFolderExtractor`, `NuGetDependencyExtractor`, `NpmDependencyExtractor`)
- `src/Eidet.Core/Intake/MarkdownIntake.cs`

**Why skip**
This was *just* refactored last commit (`6dcef6f refactor: split intake into extractor pipeline (#7)`). The current shape is intentional and recent. Revisit only if specific friction surfaces.

---

## 7. Enrichment service + adapters + sanitizer — ⚠ SKIP

**Files**
- `src/Eidet.Core/Enrichment/EnrichmentService.cs`
- `src/Eidet.Core/Enrichment/IEnrichmentPort.cs`
- `src/Eidet.Core/Enrichment/{Ollama,InMemory,Null}EnrichmentAdapter.cs`
- `src/Eidet.Core/Enrichment/OllamaTextSanitizer.cs`

**Why skip**
Textbook ports & adapters pattern. Ollama is genuinely remote/external (category 3/4). The `IEnrichmentPort` exists precisely to keep the deep module testable without Ollama. The skill's recommended shape *is* what's already there.

Minor possible nit: `OllamaTextSanitizer` is shared between `OllamaEnrichmentAdapter` (output sanitize) and `EnrichmentCleanupStage` (retroactive clean). If that ever drifts, colocating sanitize logic with the adapter that produces the CoT leakage is a small win — but not worth a refactor on its own.

---

## Summary table

| # | Cluster | Impact | Risk | Already considered? |
|---|---------|--------|------|---------------------|
| 1 | Memory pipeline | High | Medium (touches hot read/write path) | ✅ DONE — RFC #9 / `25abde0` |
| 2 | API/Tools three-tier | Medium-High | High (cross-cutting MCP+REST) | Yes (intentional split) |
| 3 | Hook event plumbing | Medium | Low | 🔬 RFC #19 (open) |
| 4 | Maintenance stages | Medium | Low | ✅ DONE — RFC #22 / PR #23 |
| 5 | Gates | Low | Very low | ✅ DONE — RFC #30 / PR #31 |
| 6 | Intake | — | — | Just refactored, skip |
| 7 | Enrichment | — | — | Already correct shape, skip |

## Suggested next step

Pick #1 first. Spawn 3+ parallel design sub-agents with these constraints:
- **Agent A**: Minimize the interface — 1–3 entry points max.
- **Agent B**: Maximize flexibility — many use cases, extension points.
- **Agent C**: Optimize for the most common caller — make the default trivial.

After comparing, file a refactor RFC as a GitHub issue.
