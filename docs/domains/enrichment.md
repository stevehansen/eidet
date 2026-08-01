# Enrichment

The optional local-LLM layer — the only place in Eidet where a model touches a memory.

**Status:** current as of 0.10.0 (setup wizard + live config reload) · **Governing issues:**
[#21](https://github.com/stevehansen/eidet/issues/21) (OpenAI-compatible backends),
[#60](https://github.com/stevehansen/eidet/issues/60) (Reflector prompts), plus the nightly drift review.
**Priming skill:** [`.claude/skills/enrichment/SKILL.md`](../../.claude/skills/enrichment/SKILL.md)

## What it is

A ports-and-adapters wrapper around a local model server (Ollama-native or any OpenAI-compatible
endpoint: LM Studio, llama.cpp, vLLM). It generates the derived fields a memory can live without —
`Summary`, `OneLiner`, `ForesightHint`, extra `Entities` — plus three analytical calls: observation
merging for consolidation, drift review, and reflection proposals.

Everything here is **optional and additive**. The write path is zero-LLM by design (**writepath**), so
every call site must behave correctly when the backend is absent. This domain does *not* own the
stages that schedule it (**maintenance**), and it never decides whether a memory is stored.

## Core entities & relationships

```
EnrichmentService (facade — owns "which fields does this memory still need", merge semantics)
   └─ IEnrichmentPort  ── OllamaEnrichmentAdapter      (native /api/generate)
                       ├─ OpenAiEnrichmentAdapter      (/v1/chat/completions)
                       ├─ NullEnrichmentAdapter        (IsAvailable == false; the disabled default)
                       └─ InMemoryEnrichmentAdapter    (public, for tests)
        shared: EnrichmentHealthCache · EnrichmentPrompts · OllamaTextSanitizer

Two drivers, one net:
  EnrichmentWorker      — RavenDB data subscription, fires the moment a memory is stored
  OllamaEnrichmentStage — nightly sweep over whatever is *still* unenriched (the retry net)

Parsers for the structured calls: DriftReviewParser → DriftReview · ReflectionProposalParser → ReflectionProposal
```

`EnrichmentService.CreateFromConfig` and `Reconfigure` share one mapping function, which is what makes
`POST /api/config/enrichment/reload` safe: the adapter is swapped under a lock and every holder of the
facade sees the new backend on its next call.

## Invariants & rules

- **The prompt wording, HTTP transport, health caching, and model CoT quirks stay behind the port.**
  Callers ask for `EnrichmentPrompt.Summary`, not for a prompt string. `EnrichmentRequest` carries only
  strings, so anything structured (an age, a sibling list) is rendered into `Primary`/`Aux` at the
  facade — never at a call site.
- **Nothing fails because enrichment is unavailable.** `IsAvailable == false` short-circuits every
  method to `false` / `null` / `[]`, and adapters swallow transport errors. A call in flight during a
  `Reconfigure` fails softly and that one enrichment is skipped.
- **A redaction tombstone is never enriched.** `EnrichMemoryAsync` refuses content with the redaction
  prefix — otherwise the model would paraphrase the scrubbed payload straight back into `Summary` and
  therefore into `SearchText`.
- **`Summary == null` is the work queue.** The subscription query and the unenriched stats both key on
  it, which is exactly why redaction writes `""` instead of `null` (**memory**).
- **The one-liner is re-generated only while it still equals the deterministic heuristic one.** That
  equality check is how "never enriched" is distinguished from "already enriched or hand-edited" for a
  field that is never null.
- **Enrichment writes route through `MemoryService`.** `Summary`/`OneLiner`/`ForesightHint` feed
  `SearchText` and recall scoring, so a direct store write would leave a stale recall cache.
- **The nightly sweep selects the *unenriched*, not the top-scored.** The worker's subscription acks a
  document even when enrichment fails and never re-sends it, so the sweep is the only retry — and a
  top-scored selection silently skipped low-scoring documents in repos past the scan cap.
- **Health verdicts expire optimistically.** Once the cache goes stale, `IsAvailable` returns true
  regardless of the last verdict, so a backend that comes back is re-probed instead of being pinned off
  forever.
- **Model output is advisory content only.** Every trust-bearing field on a synthesized memory
  (importance, confidence, provenance) is stamped by the calling engine, and LLM-fresh text passes the
  write gates (**maintenance**, **writepath**).

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Enrichment/EnrichmentService.cs` | The facade: field policy, `Reconfigure`, the analytical calls |
| `src/Eidet.Core/Enrichment/IEnrichmentPort.cs` | The port + `EnrichmentPrompt` / `EnrichmentRequest` |
| `src/Eidet.Core/Enrichment/{OllamaEnrichmentAdapter,OpenAiEnrichmentAdapter}.cs` | The two real backends |
| `src/Eidet.Core/Enrichment/{NullEnrichmentAdapter,InMemoryEnrichmentAdapter}.cs` | Disabled default; test double (public on purpose) |
| `src/Eidet.Core/Enrichment/EnrichmentPrompts.cs` | Every prompt string, and residue rendering |
| `src/Eidet.Core/Enrichment/OllamaTextSanitizer.cs` | Strips `<channel\|>` / `<think>` CoT leakage |
| `src/Eidet.Core/Enrichment/EnrichmentHealthCache.cs` | Shared probe cache; the optimistic-expiry rule |
| `src/Eidet.Core/Enrichment/{DriftReviewParser,ReflectionProposalParser}.cs` | Tolerant parsers — unparseable ⇒ skip and retry later |
| `src/Eidet.Core/Services/EnrichmentWorker.cs` | The store-time subscription driver |
| `src/Eidet.Core/Maintenance/Stages/{OllamaEnrichment,EnrichmentCleanup,HeuristicEnrichmentBackfill}Stage.cs` | Nightly sweep, retroactive CoT cleanup, heuristic backfill |
| `src/Eidet.Service/Commands/EnrichmentCommand.cs` | `eidet enrichment setup` / `reload` — backend detection and atomic config write |
| `src/Eidet.Core/Services/{OllamaService,OpenAiCompatibleService}.cs` | Lower-level backend clients (status, model listing) used by setup/status |

## Gotchas

- **`Reconfigure` disposes the old adapter.** A caller that constructed the service with
  `ownsPort: false` and kept its own reference to that adapter will find it disposed after a reload —
  the swap always takes ownership of the new port.
- **The health cache is per-adapter instance**, so a reload starts from an unknown verdict (and
  therefore an optimistic `IsAvailable`). A `status` call right after a reload can look healthy before
  the first real probe.
- **CoT sanitizing runs on fresh responses only inside the Ollama adapter** —
  `EnrichmentCleanupStage` exists precisely because fields stored before it existed are still corrupt.
  Don't assume stored `Summary` values are clean.
- **The nightly enrichment batch is capped per repo per run**, so a large unenriched backlog drains over
  several nights. `GetUnenrichedStatsAsync` is how the backlog becomes visible.
- **The drift review folds age and "today" into the prompt text** because the request type carries only
  strings — a test that freezes the clock must freeze the value passed in, not `DateTime.UtcNow`.
- **`ReviewDriftAsync` returning null is normal** (unavailable, no content, or unparseable) and means
  "retry on a future run", not "no drift".

## Executable references

- `tests/Eidet.Core.Tests/Services/EnrichmentServiceTests.cs` — **the authority on field policy**: which
  fields are filled, the tombstone refusal, and the unavailable-port short circuits.
- `tests/Eidet.Core.Tests/Services/EnrichmentServiceDriftReviewTests.cs` +
  `tests/Eidet.Core.Tests/Enrichment/ReflectionProposalParserTests.cs` — settle the structured calls and
  their tolerant parsing (including malformed output ⇒ null/empty).
- `tests/Eidet.Core.Tests/Services/EnrichmentWorkerTests.cs` — settles the subscription driver,
  including that a failed enrichment is still acked.
- `tests/Eidet.Core.Tests/Services/EnrichmentHealthCacheTests.cs` — settles the optimistic-expiry rule.
- `tests/Eidet.Core.Tests/Maintenance/OllamaEnrichmentStageTests.cs` — settles the retry sweep selecting
  unenriched (not top-scored) entries.
- `tests/Eidet.Core.Tests/Services/{OllamaServiceTests,OpenAiCompatibleServiceTests}.cs` — settle backend
  detection and response shapes.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Write path (Enrichment), Memory core (Summary, One-liner),
  Retrieval & context loading (Foresight hint)
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) · configuration:
  [`docs/configuration.md`](../configuration.md)
- Related domains: **maintenance** (schedules every batch call) · **memory** (the null-vs-empty field
  semantics) · **writepath** (why the store path stays zero-LLM) · **canon** (drafts are deterministic
  today; LLM-proposed pages are the designed next step)
- Priming skill: `.claude/skills/enrichment/SKILL.md`
