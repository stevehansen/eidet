---
name: quality
description: Prime on Eidet's quality and benchmarking surfaces before changing them — QualityService's check battery and QualityReport behind /api/eidet/quality, the deterministic retrieval scorecard (BenchmarkRunner's fused-vs-flat-baseline arms, RetrievalMetrics, AmaCapability headings, the CI-asserted docs/benchmark.md), and the SWE Context Bench harness in tools/Eidet.Bench with its LeaderboardGuard. Use when the task touches a quality check, a retrieval metric, the gold dataset, eidet bench, or a benchmark number. Not for the ranking pipeline itself (see recall), not for the integrity auditor (see writepath).
---

# Quality & benchmarking — priming

**Canonical spec:** `docs/domains/quality.md` — read it for the check battery, the harness shape, all
invariants, key files, and gotchas. Generated artifacts: `docs/benchmark.md`,
`docs/swe-context-bench.md`.

Two questions: "is this repo's memory healthy?" (`QualityService`, advisory and read-only) and "does
our ranking actually beat the baseline?" (deterministic scorecard + the SWE Context Bench harness).

## Core invariants (get these right)

- **A leaderboard-shaped number may only come from the real dataset.** `LeaderboardGuard` refuses for
  fixtures, and the rendered fixture report carries a "not a leaderboard" banner.
- **`ToMarkdown` is pure** (no timestamps/environment) so CI can assert `docs/benchmark.md` byte-equal.
  Regenerate via the documented env var; never hand-edit.
- **Both arms run over the same candidate pools** — the baseline must differ by ranking alone.
- **Report un-scoreable capabilities as not-evaluated, with the reason** — never fabricate.
- **Metrics define edge cases out of existence**: empty gold → 0, `k` clamps, duplicates can't exceed
  1.0, everything finite in `[0,1]`.
- **The benchmark calls `RecallScoring.Fuse` directly**, which is exactly why production trust/ROI
  policy stays outside `Fuse`.
- **The quality report never mutates anything** — it surfaces what other domains recorded.
- **The analysed sample is bounded** (`TotalMemories` vs `AnalyzedCount`), and the integrity auditor is
  optional — a clean report isn't proof of a clean corpus.

## Key files / reuse

- `src/Eidet.Core/Services/QualityService.cs` — add a check to the fixed battery in `AnalyzeAsync`.
- `src/Eidet.Core/Benchmark/{BenchmarkRunner,RetrievalMetrics,Scorecard}.cs` — the scorecard.
- `tools/Eidet.Bench/` — the SWE Context Bench harness, its four ports, and `LeaderboardGuard`.
- `tests/Eidet.Benchmark.Tests/GoldDataset.cs` — extend this to add cases.

## Gotchas

- `tools/Eidet.Bench` lives **outside `src/`** — a `src/**`-scoped sweep misses it.
- `docs/benchmark.md` and `docs/swe-context-bench.md` are test-asserted; hand-edits fail CI.
- The scorecard sync test skips silently off a non-checkout — green ≠ current.
- The overall quality *score* is a heuristic aggregate; the individual issues are the signal.
- The bundled fixture dataset is tiny and unrepresentative by design.
