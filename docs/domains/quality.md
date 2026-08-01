# Quality & benchmarking

Two ways of asking "is this memory store any good?" — a per-repo health report, and a deterministic
retrieval scorecard that guards against fooling ourselves.

**Status:** current as of 0.9.x · **Governing issues:**
[#36](https://github.com/stevehansen/eidet/issues/36) (deterministic retrieval scorecard, v2 fusion vs
flat baseline), [#39](https://github.com/stevehansen/eidet/issues/39) (merge-rejection visibility), plus
the SWE Context Bench harness shipped in [#68](https://github.com/stevehansen/eidet/issues/68).
**Priming skill:** [`.claude/skills/quality/SKILL.md`](../../.claude/skills/quality/SKILL.md)

## What it is

- **Quality** — `QualityService` runs a fixed battery of checks over a repo's memories and returns a
  scored report of typed issues (stale, high-fizzle, orphaned, tag-concentrated, drift-flagged,
  merge-rejected, integrity findings…). It is the operator-facing dashboard behind
  `GET /api/eidet/quality`.
- **Benchmark** — a deterministic, zero-LLM retrieval harness that scores a gold dataset under the v2
  fusion ranker *and* the pre-fusion flat baseline on the same candidate pools, so a ranking change has
  to prove its lift. Plus the SWE Context Bench harness (`tools/Eidet.Bench`), which measures whether
  memory actually helps an agent solve tasks.

It is *not* the ranking pipeline itself (**recall**), *not* the corpus-mutating passes whose findings it
surfaces (**maintenance**), and *not* the integrity auditor (**writepath** owns it; quality just renders
its findings).

## Core entities & relationships

```
QualityService.AnalyzeAsync(repo) → QualityReport { Score, Issues[] }
   fixed check battery over a bounded browse + (optional) IIntegrityAuditor findings

Retrieval scorecard (in-process, deterministic):
  BenchmarkCase[] → BenchmarkRunner.Run(cases, now)
        ├─ fused arm    : RecallScoring.Fuse → ExpandNeighbors → ApplyTypeBudgets
        └─ baseline arm : flat constants (lexical 1.0 / vector 0.9) → same budget pass
     → BenchmarkReport { Fused[], Baseline[] } grouped by AmaCapability
     → ToMarkdown()  — pure, timestamp-free ⇒ docs/benchmark.md is CI-assertable byte-equal
  RetrievalMetrics: RecallAtK · ReciprocalRank · NdcgAtK · gold survival

SWE Context Bench (tools/Eidet.Bench): ISweDatasetPort · IMemoryBackend · ISolverPort · IOraclePort
        · ICapabilityScorer → SweBenchHarness (ingestion phase, then evaluation phase)
        · LeaderboardGuard — the anti-misreporting gate
```

## Invariants & rules

- **A leaderboard-shaped number may only come from the real dataset.** The bundled fixture exists to
  prove harness logic, never to produce a citable figure — `LeaderboardGuard` refuses in the gate *and*
  the rendered artifact carries a "not a leaderboard" banner, so fixture output can't be screenshotted
  as a score.
- **`ToMarkdown` is a pure function of the two scorecards** — no timestamps, no environment — which is
  what lets CI assert the committed `docs/benchmark.md` byte-for-byte. Regenerate with the documented
  environment variable rather than hand-editing.
- **Both arms run over the *same* candidate pools.** The comparison is only honest if the baseline
  differs by ranking alone.
- **Capabilities that a deterministic harness cannot honestly score are reported as not-evaluated**,
  with the reason, rather than being fabricated or silently dropped.
- **Metrics define their edge cases out of existence.** Empty gold yields 0 (never NaN, never a throw),
  `k` clamps to the list length, duplicate ids can't push recall above 1.0. Every result is finite and
  in `[0, 1]`.
- **The benchmark calls `RecallScoring.Fuse` directly, not production recall.** That is precisely why
  trust/ROI/quarantine policy is layered *outside* `Fuse` (**recall**) — folding it in would penalize
  Procedure/Heuristic gold cases.
- **The quality report is advisory and read-only.** It never mutates a memory; it surfaces what other
  domains recorded (drift verdicts, `LastMergeRejectedAt` stamps, quarantine, integrity findings).
- **The integrity auditor is an optional dependency.** Without it wired, the report is simply missing
  those findings — a clean report from an auditor-less service is not a clean corpus.
- **The analysed sample is bounded.** `TotalMemories` and `AnalyzedCount` are separate fields on the
  report for exactly this reason; percentages are over what was analysed.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Services/QualityService.cs` | The check battery + scoring |
| `src/Eidet.Core/Domain/QualityReport.cs` | `QualityReport` / `QualityIssue` and the severity levels |
| `src/Eidet.Core/Benchmark/BenchmarkRunner.cs` | Both arms, on shared pools |
| `src/Eidet.Core/Benchmark/RetrievalMetrics.cs` | The pure IR metrics |
| `src/Eidet.Core/Benchmark/{Scorecard,BenchmarkCase,AmaCapability}.cs` | Report shape, case model, capability headings |
| `tools/Eidet.Bench/SweBenchHarness.cs` | The two-phase SWE Context Bench run loop |
| `tools/Eidet.Bench/{ISweDatasetPort,IMemoryBackend,ISolverPort,IOraclePort,ICapabilityScorer}.cs` | The four external ports + scoring seam |
| `tools/Eidet.Bench/{LeaderboardGuard,FixtureDataset,RecordReplay,Transcript}.cs` | The publish gate, the offline fixture, and record/replay |
| `src/Eidet.Service/Commands/{QualityCommand,BenchCommand}.cs` | `eidet quality` / `eidet bench` |
| `src/Eidet.Service/Api/Endpoints/QualityEndpoint.cs` | `GET /api/eidet/quality` |
| `docs/benchmark.md`, `docs/swe-context-bench.md` | The generated scorecard and the harness write-up — both CI-guarded |

## Gotchas

- **`tools/Eidet.Bench` is a separate project outside `src/`.** A repo-wide sweep scoped to `src/**`
  misses it entirely — including its ports and the leaderboard guard.
- **`docs/benchmark.md` and `docs/swe-context-bench.md` are asserted by tests**
  (`ScorecardSyncTests`, `SweContextBenchDocTests`). Editing either by hand fails CI; regenerate.
- **The scorecard sync test skips silently when it can't find the repo root** (a packaged run), so a
  green suite off a non-checkout does not mean the doc is current.
- **A quality *score* is a heuristic aggregate**, not a measurement. Treat the individual issues as the
  signal; the number is for trend-watching.
- **Adding a quality check means adding it to the fixed battery list** in `AnalyzeAsync` — there is no
  registry and no discovery.
- **The fixture dataset is deliberately small and unrepresentative.** It proves the harness runs; it
  says nothing about retrieval quality.

## Executable references

- `tests/Eidet.Benchmark.Tests/BenchmarkScorecardTests.cs` + `ScorecardSyncTests.cs` — **the authority
  on the scorecard**: metric correctness, the fused-vs-baseline comparison, and that the committed
  markdown is the current rendered output.
- `tests/Eidet.Benchmark.Tests/GoldDataset.cs` + `ScorecardBuilder.cs` — the dataset and builder to
  extend when adding cases (not a test per se, but the input of record).
- `tests/Eidet.Benchmark.Tests/FamaForgetTests.cs` — the per-memory "an invalidated memory stays gone"
  predicate that **writepath**'s auditor broadens.
- `tests/Eidet.Bench.Tests/LeaderboardGuardTests.cs` — **the authority on the anti-misreporting rule**:
  fixtures may never publish, a missing real dataset is refused with a download hint, and the rendered
  fixture report carries the banner.
- `tests/Eidet.Bench.Tests/{SweBenchHarnessTests,FixtureDatasetTests,RealDatasetTests,TranscriptTests,FixtureTranscriptSyncTests}.cs`
  — settle the two-phase loop, record/replay, and fixture/transcript consistency.
- `tests/Eidet.Core.Tests/Services/QualityServiceTests.cs` + `QualityMergeRejectedTests.cs` — settle the
  check battery and the merge-rejection surfacing.

## Links

- Generated artifacts: [`docs/benchmark.md`](../benchmark.md) ·
  [`docs/swe-context-bench.md`](../swe-context-bench.md)
- Glossary: `UBIQUITOUS_LANGUAGE.md` § Feedback & scoring
- Related domains: **recall** (the pipeline under measurement, and why policy sits outside `Fuse`) ·
  **writepath** (integrity findings rendered here) · **maintenance** (drift verdicts and merge
  rejections surfaced here) · **portal** (the other read-only human view)
- Priming skill: `.claude/skills/quality/SKILL.md`
