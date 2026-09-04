---
name: enrichment
description: Prime on Eidet's optional LLM layer before changing it — EnrichmentService, IEnrichmentPort and its Ollama/OpenAI-compatible/Fallback/Null/InMemory adapters, the primary-plus-fallbacks backend chain, bearer auth and the thinking switch, prompt kinds, the health cache, CoT sanitizing, the store-time EnrichmentWorker subscription, and the generated Summary/OneLiner/ForesightHint/Entities fields plus drift-review and reflection proposals. Use when the task touches enrichment config or reload, an Ollama/LM Studio/vLLM backend or a network model cluster, a prompt, a parser for model output, or "why is Summary null". Not for the stages that schedule these calls (see maintenance), not for the zero-LLM store gates (see writepath).
---

# Enrichment — priming

**Canonical spec:** `docs/domains/enrichment.md` — read it for the full field policy, all invariants,
key files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Write path (Enrichment).
Config: `docs/configuration.md`.

The only place a model touches a memory, and it is entirely optional: ports-and-adapters over one or
more backends (Ollama-native or any OpenAI-compatible server, local or a bearer-authenticated network
cluster), tried as an ordered chain. The store path stays zero-LLM — every call site must be correct
when every backend is absent.

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
- **Health verdicts expire optimistically** so a recovered backend is re-probed rather than pinned off;
  the probe has its own 5s budget so a black-holed host costs seconds before the chain moves on.
- **The chain is config order, per call: first backend that is up *and answers* wins.** Down, rejected,
  failed, or empty falls through. `ModelName` names the backend behind the last answer.
- **Every backend `HttpClient` comes from `EnrichmentHttp`** — `/v1` normalisation, bearer token, probe
  path in one place, so adapter, discovery, monitor and doctor agree on how to reach a server.
- **`thinking` unset means absent on the wire.** `false` → `chat_template_kwargs.thinking` (OpenAI-compat)
  / `think` (Ollama). Never send it unasked: a strict gateway rejects unknown fields silently-to-us.
- **Model output is advisory content only** — the calling engine stamps every trust-bearing field, and
  LLM-fresh text still passes the write gates.

## Key files / reuse

- `src/Eidet.Core/Enrichment/EnrichmentService.cs` — the facade (+ `Reconfigure` for live reload).
- `src/Eidet.Core/Enrichment/IEnrichmentPort.cs` — the port; add a prompt kind here, not a new method.
- `src/Eidet.Core/Enrichment/FallbackEnrichmentAdapter.cs` + `EnrichmentHttp.cs` — the chain and the one
  way to build a backend client. `EnrichmentConfig` *is* the primary `EnrichmentBackendConfig`;
  `Backends` = primary + `Fallbacks`.
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
- A wrong bearer token looks exactly like an offline server (401 → unhealthy → fall through, no log).
  `eidet doctor` is what tells them apart.
- `eidet config set` reaches the primary's keys only; fallbacks are edited in the file or via the wizard.
