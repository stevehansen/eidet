# SWE Context Bench — Eidet Memory-Backend Harness

> **Fixture run — NOT a leaderboard number.** This report is rendered from the bundled
> synthetic fixture and a replayed transcript; it exists to byte-guard the harness logic
> in CI. A publishable SWE Context Bench figure requires a fresh recorded run against
> the real dataset (https://huggingface.co/datasets/jiayuanz3/SWEContextBench);
> `eidet bench full` refuses to emit one from anything else.

Methodology mirrors SWE-ContextBench (arXiv:2602.08316): related tasks are solved first
and their trajectories are ingested into the memory backend; each base task then recalls
context, the solver attempts a patch, and an execution oracle requires FAIL_TO_PASS +
PASS_TO_PASS. Resolution rate and solver tokens count evaluated (base) attempts only.

## Run

| | |
|---|---|
| Dataset | bundled-fixture-v1 |
| Memory backend | eidet-inprocess |
| Related tasks (ingested) | 2 |
| Base tasks (evaluated) | 3 |
| Resolved | 2 |
| Resolution rate | 0.667 |
| Solver tokens per resolved | 1750 |

## AMA capability rows

`CausalInference` and `StateAbstraction` complement the deterministic scorecard in
[benchmark.md](benchmark.md) (which reports them N/A — they need an LLM in the loop)
and are only ever populated here from a recorded real run.

- **CausalInference** — N/A — pending a recorded real run (LLM-in-loop scorer, Phase 1 of issue #36).
- **StateAbstraction** — N/A — pending a recorded real run (LLM-in-loop scorer, Phase 1 of issue #36).
