# Eidet Retrieval Benchmark Scorecard

This measures the part of recall Eidet **owns**: ranking, hybrid fusion, and type-budget
quality on a curated, adversarial in-repo dataset. Each gold case scripts per-arm raw
scores `(lex, vec)`; the runner feeds them through the **real** `RecallScoring.Fuse` +
`ApplyTypeBudgets` pipeline and compares the v2 fusion ranker against a **flat baseline**
(the pre-#33 scoring: lexical hits = 1.0, vector-only hits = 0.9, no normalization, no UCB,
no recency) over the same dataset. The lift is fusion recovering gold that flat scoring
loses at the truncation boundary.

It does **not** measure embedding quality (RavenDB owns embeddings, which can't run
deterministically in CI), and it is **not** the external SWE Context Bench leaderboard —
that is a deferred epic. This is a deterministic, no-LLM, CI regression guard.

## v2 Fusion vs Flat Baseline

| Capability | Cases | Metric | v2 Fusion | Flat Baseline | Delta |
|---|---|---|---|---|---|
| Recall | 14 | Recall@k | 0.857 | 0.321 | +0.536 |
|  |  | MRR | 0.833 | 0.405 | +0.429 |
|  |  | nDCG@k | 0.804 | 0.246 | +0.559 |
|  |  | Gold survival | 0.750 | 0.357 | +0.393 |
| StateUpdating | 1 | Recall@k | 1.000 | 1.000 | +0.000 |
|  |  | MRR | 1.000 | 1.000 | +0.000 |
|  |  | nDCG@k | 1.000 | 1.000 | +0.000 |
|  |  | Gold survival | 1.000 | 1.000 | +0.000 |
| **Overall** | 15 | Recall@k | 0.867 | 0.367 | +0.500 |
|  |  | MRR | 0.844 | 0.444 | +0.400 |
|  |  | nDCG@k | 0.817 | 0.296 | +0.522 |
|  |  | Gold survival | 0.767 | 0.400 | +0.367 |

## Capabilities not evaluated

- **CausalInference** — N/A — requires LLM-in-loop; deferred to the SWE Context Bench epic (issue #36 follow-up).
- **StateAbstraction** — N/A — requires LLM-in-loop; deferred to the SWE Context Bench epic (issue #36 follow-up).
