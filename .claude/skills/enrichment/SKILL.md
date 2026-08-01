---
name: enrichment
description: Prime on Eidet's optional local-LLM layer before changing it — EnrichmentService, IEnrichmentPort and its Ollama/OpenAI-compatible/Null/InMemory adapters, prompt kinds, the health cache, CoT sanitizing, the store-time EnrichmentWorker subscription, and the generated Summary/OneLiner/ForesightHint/Entities fields plus drift-review and reflection proposals. Use when the task touches enrichment config or reload, an Ollama/LM Studio backend, a prompt, a parser for model output, or "why is Summary null". Not for the stages that schedule these calls (see maintenance), not for the zero-LLM store gates (see writepath).
---

# Enrichment — priming

**Canonical spec:** `docs/domains/enrichment.md` — read it for the full field policy, all invariants,
key files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Write path (Enrichment).
Config: `docs/configuration.md`.

The only place a model touches a memory, and it is entirely optional: ports-and-adapters over a local
backend (Ollama-native or any OpenAI-compatible server). The store path stays zero-LLM — every call
site must be correct when the backend is absent.

## Core invariants (get these right)

- **Prompts, transport, health caching, and CoT quirks stay behind the port.** Callers pass an
  `EnrichmentPrompt` kind; anything structured is rendered into `Primary`/`Aux` at the facade.
- **Nothing fails because enrichment is unavailable** — short-circuit to `false`/`null`/`[]`; adapters
  swallow transport errors.
- **Never enrich a redaction tombstone** — the model would paraphrase scrubbed content back into
  `Summary`, and therefore into `SearchText`.
- **`Summary == null` is the work queue** (subscription + unenriched stats key on it). That's why
  redaction writes `""`.
- **The one-liner is re-generated only while it still equals the deterministic heuristic** — that's the
  "never enriched" test for a field that is never null.
- **Enrichment writes go through `MemoryService`** — these fields feed `SearchText` and scoring, so a
  direct store write leaves a stale recall cache.
- **The nightly sweep selects *unenriched*, not top-scored** — the subscription acks failures and never
  re-sends, so the sweep is the only retry.
- **Health verdicts expire optimistically** so a recovered backend is re-probed rather than pinned off.
- **Model output is advisory content only** — the calling engine stamps every trust-bearing field, and
  LLM-fresh text still passes the write gates.

## Key files / reuse

- `src/Eidet.Core/Enrichment/EnrichmentService.cs` — the facade (+ `Reconfigure` for live reload).
- `src/Eidet.Core/Enrichment/IEnrichmentPort.cs` — the port; add a prompt kind here, not a new method.
- `src/Eidet.Core/Enrichment/EnrichmentPrompts.cs` — every prompt string.
- `src/Eidet.Core/Enrichment/InMemoryEnrichmentAdapter.cs` — public test double; don't hand-roll one.
- `src/Eidet.Core/Services/EnrichmentWorker.cs` + `src/Eidet.Core/Maintenance/Stages/OllamaEnrichmentStage.cs` — the
  two drivers.

## Gotchas

- `Reconfigure` disposes the old adapter and always takes ownership of the new one.
- The health cache is per-adapter, so a reload starts optimistic — `status` can look healthy pre-probe.
- CoT sanitizing only cleans *fresh* responses; `EnrichmentCleanupStage` exists for already-corrupted
  stored fields.
- The nightly batch is capped per repo per run — a big backlog drains over several nights
  (`GetUnenrichedStatsAsync` makes it visible).
- `ReviewDriftAsync` returning null means "retry later", not "no drift".
