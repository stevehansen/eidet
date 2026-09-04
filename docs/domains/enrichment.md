# Enrichment

The optional local-LLM layer — the only place in Eidet where a model touches a memory.

**Status:** current as of 0.14.0 (network-first fallback chain, bearer auth, thinking switch) · **Governing issues:**
[#21](https://github.com/stevehansen/eidet/issues/21) (OpenAI-compatible backends),
[#60](https://github.com/stevehansen/eidet/issues/60) (Reflector prompts), plus the nightly drift review.
**Priming skill:** [`.claude/skills/enrichment/SKILL.md`](../../.claude/skills/enrichment/SKILL.md)

## What it is

A ports-and-adapters wrapper around one or more model servers (Ollama-native or any OpenAI-compatible
endpoint: LM Studio, llama.cpp, vLLM — on this machine, or a private network cluster reached with a
bearer token). Backends form an ordered chain: a fast network model first, an always-on local one
behind it. It generates the derived fields a memory can live without —
`Summary`, `OneLiner`, `ForesightHint`, extra `Entities` — plus three analytical calls: observation
merging for consolidation, drift review, and reflection proposals.

Everything here is **optional and additive**. The write path is zero-LLM by design (**writepath**), so
every call site must behave correctly when the backend is absent. This domain does *not* own the
stages that schedule it (**maintenance**), and it never decides whether a memory is stored.

## Core entities & relationships

```
EnrichmentService (facade — owns "which fields does this memory still need", merge semantics)
   └─ IEnrichmentPort  ── OllamaEnrichmentAdapter      (native /api/chat, think on/off)
                       ├─ OpenAiEnrichmentAdapter      (/v1/chat/completions, optional chat_template_kwargs.thinking)
                       ├─ FallbackEnrichmentAdapter    (ordered chain of the above — one per configured backend)
                       ├─ NullEnrichmentAdapter        (IsAvailable == false; the disabled default)
                       └─ InMemoryEnrichmentAdapter    (public, for tests)
        shared: EnrichmentHttp (URL normalisation, bearer auth, probe path) · EnrichmentHealthCache ·
                EnrichmentPrompts · OllamaTextSanitizer

Config: EnrichmentConfig IS the primary EnrichmentBackendConfig (flat keys predate fallbacks) and
carries Fallbacks: [EnrichmentBackendConfig]; Backends = [primary, ..fallbacks] is the derived view
every consumer iterates.

Two drivers, one net:
  EnrichmentWorker      — RavenDB data subscription, fires the moment a memory is stored
  OllamaEnrichmentStage — nightly sweep over whatever is *still* unenriched (the retry net)

Parsers for the structured calls: DriftReviewParser → DriftReview · ReflectionProposalParser → ReflectionProposal
```

`EnrichmentService.CreateFromConfig` and `Reconfigure` share one mapping function, which is what makes
`POST /api/config/enrichment/reload` safe: the adapter is swapped under a lock and every holder of the
facade sees the new backend on its next call. The same reload hands the drift-review and reflection
settings to the maintenance pipeline (`MaintenanceOrchestrator.Reconfigure`, see **maintenance**) and
re-renders the `Nightly AI:` verdict, which `eidet enrichment reload` prints — so turning drift review
on or off is a reload, not a restart.

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
- **A body-less heading is never enriched either.** `EnrichMemoryAsync` refuses content where
  `MarkdownIntake.IsHeadingOnly` holds. Asked to describe a label, a model supplies the knowledge the
  label implies, and that fabrication then *outranks* the real fields because L1 renders `OneLiner`
  first: on a field corpus, 843 heading-only memories carried an invented one-liner and 59 of them
  reached wake-ups as assertions the repo never made — while the summary that honestly said "this is a
  heading, not content" stayed hidden behind it. **intake** rejects these at the gate now; this is the
  backstop for what earlier builds stored, and enrichment must not be the component that turns
  `## Development Patterns` into advice about iterative development.
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
  forever. The probe itself has a 5-second budget, separate from the 120-second completion timeout,
  so a black-holed host (a VPN that is not up) costs seconds before the chain moves on.
- **The chain is tried in config order, per call, and the first backend that is up *and answers* wins.**
  A backend that is down, rejects the request, fails mid-call, or returns an empty completion hands
  the request to the next; the caller never learns which one answered except through `ModelName`,
  which names the backend behind the most recent answer (the primary's until something has answered).
  A recovered primary takes over on its next call — nothing pins the chain to the fallback.
- **Every surface that reaches a backend goes through `EnrichmentHttp`.** URL normalisation (a
  trailing `/v1` is dropped — the adapters add the versioned path themselves), the bearer token, and
  the provider's probe path live in one place, so a backend that needs auth is reachable from the
  adapter, model discovery, the health monitor and `eidet doctor` alike — or from none of them.
- **Thinking is a per-backend switch, and unset means absent.** `thinking: false` rides as
  `chat_template_kwargs.thinking` on OpenAI-compatible servers (the field the vLLM/llama.cpp chat
  template honours) and as Ollama's native `think`. Unset puts nothing on the wire: a strict gateway
  rejects unknown fields, and a rejected request would surface only as a silently unenriched memory.
  Measured 2026-09-04 on deepseek-v4-flash-0731 (vLLM): default answered with 95 completion tokens
  and its thoughts in a separate `message.reasoning` field; `thinking: false` answered with 38 in
  under half the time. `reasoning_effort: "none"` happened to work on that build too, but
  `low`/`minimal` are accepted and ignored, so the template kwarg is the one we send.
- **Model output is advisory content only.** Every trust-bearing field on a synthesized memory
  (importance, confidence, provenance) is stamped by the calling engine, and LLM-fresh text passes the
  write gates (**maintenance**, **writepath**).

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Enrichment/EnrichmentService.cs` | The facade: field policy, `Reconfigure`, the analytical calls |
| `src/Eidet.Core/Enrichment/IEnrichmentPort.cs` | The port + `EnrichmentPrompt` / `EnrichmentRequest` |
| `src/Eidet.Core/Enrichment/{OllamaEnrichmentAdapter,OpenAiEnrichmentAdapter}.cs` | The two real backends |
| `src/Eidet.Core/Enrichment/FallbackEnrichmentAdapter.cs` | The chain: one port over N backends, first-up-and-answering wins |
| `src/Eidet.Core/Enrichment/EnrichmentHttp.cs` | URL normalisation, bearer auth, probe path — the only way to build a backend `HttpClient` |
| `src/Eidet.Core/Configuration/EidetConfig.cs` (`EnrichmentBackendConfig`, `EnrichmentConfig.Fallbacks`/`Backends`) | One backend's shape; the primary-plus-fallbacks view |
| `src/Eidet.Core/Enrichment/{NullEnrichmentAdapter,InMemoryEnrichmentAdapter}.cs` | Disabled default; test double (public on purpose) |
| `src/Eidet.Core/Enrichment/EnrichmentPrompts.cs` | Every prompt string, and residue rendering |
| `src/Eidet.Core/Enrichment/OllamaTextSanitizer.cs` | Strips `<channel\|>` / `<think>` CoT leakage |
| `src/Eidet.Core/Enrichment/EnrichmentHealthCache.cs` | Shared probe cache; the optimistic-expiry rule |
| `src/Eidet.Core/Enrichment/{DriftReviewParser,ReflectionProposalParser}.cs` | Tolerant parsers — unparseable ⇒ skip and retry later |
| `src/Eidet.Core/Services/EnrichmentWorker.cs` | The store-time subscription driver |
| `src/Eidet.Core/Maintenance/Stages/{OllamaEnrichment,EnrichmentCleanup,HeuristicEnrichmentBackfill}Stage.cs` | Nightly sweep, retroactive CoT cleanup, heuristic backfill |
| `src/Eidet.Service/Commands/EnrichmentCommand.cs` | `eidet enrichment setup` / `reload` — backend detection, API key + thinking prompts, keep-as-fallback offer, atomic config write |
| `src/Eidet.Service/HealthMonitor.cs` · `src/Eidet.Service/Commands/DoctorCommand.cs` | Probe the chain directly (bypassing the adapters' cache); the monitor names the answering backend, doctor prints one row per backend |
| `src/Eidet.Core/Services/{OllamaService,OpenAiCompatibleService}.cs` | Lower-level backend clients (status, model listing) used by setup/status |

## Gotchas

- **`Reconfigure` disposes the old adapter.** A caller that constructed the service with
  `ownsPort: false` and kept its own reference to that adapter will find it disposed after a reload —
  the swap always takes ownership of the new port.
- **The health cache is per-adapter instance**, so a reload starts from an unknown verdict (and
  therefore an optimistic `IsAvailable`). A `status` call right after a reload can look healthy before
  the first real probe.
- **CoT sanitizing runs on fresh responses only inside the adapters** —
  `EnrichmentCleanupStage` exists precisely because fields stored before it existed are still corrupt.
  Don't assume stored `Summary` values are clean. A vLLM host started *with* a reasoning parser puts
  the thoughts in a separate `reasoning`/`reasoning_content` field the adapter never reads; one
  started without puts them inline as `<think>` blocks, which the sanitizer strips. Either way the
  answer is correct — turning thinking off is a cost lever, not a correctness fix.
- **A wrong or missing bearer token looks exactly like an offline server.** `/v1/models` answers 401,
  the health cache records "unhealthy", and the adapter falls through to the next backend or returns
  null — no error is logged. `eidet doctor` is the surface that tells the two apart (it prints the
  HTTP status and points at `apiKey`); the wizard says "offline, or the API key rejected".
- **`ModelName` on a chain is the *last answering* backend, not the configured primary.** Drift
  reviews stamp `review.Model` from it right after the call, so a review made while the primary was
  down is honestly attributed to the fallback. Concurrent calls can interleave, so treat it as a
  label, not a lock.
- **The flat `provider`/`url`/`model`/`apiKey`/`thinking` keys *are* the primary backend.**
  `EnrichmentConfig` inherits `EnrichmentBackendConfig`; there is no separate `primary` object, and
  `Backends` is a derived view that is never serialised. `eidet config set` addresses the primary's
  keys only — fallbacks are edited in the file or through the wizard.
- **The nightly enrichment batch is capped per repo per run**, so a large unenriched backlog drains over
  several nights. `GetUnenrichedStatsAsync` is how the backlog becomes visible.
- **The drift review folds age and "today" into the prompt text** because the request type carries only
  strings — a test that freezes the clock must freeze the value passed in, not `DateTime.UtcNow`.
- **`Entities` and `OneLiner` are load-bearing for retrieval, not just for display.** `Entities` are the
  **cue anchors** recall expands along, and `OneLiner` is the first choice for the abstraction arm's
  embedding (see **recall**). Changing an extraction prompt therefore changes what is *reachable*, not
  just what is rendered — and a corpus that never ran enrichment has no cue expansion at all. The
  abstraction arm is exempt: it falls back to `Content` at index time, so it is never enrichment-gated.
- **Extracted entities run `EntityHygiene.Clean`, and the rules key on *shape*.** A reasoning model
  answers the extraction prompt with its own chain of thought often enough to matter — 443 such strings
  across 223 memories on one corpus ("The user wants me to act as an information extractor", a numbered
  restatement of the entity types it was asked about, once a bare `<channel|>` token) — plus markdown
  leftovers (1,574 numbered-list fragments, 338 with fences, 241 bare heading markers). Wording is
  unbounded and cannot be filtered; shape can. Identifiers are short, unpunctuated, and never
  sentences, which is what keeps `Vidyano.RavenDB` and `/api/eidet/context` while dropping the prose
  around them. `Clean` is idempotent, so **maintenance** re-runs it as repair. The **deterministic**
  extractor cleans too (`EntityExtractor.Extract`), and that is not redundant: while any deriver emits
  noise, maintenance's repair of the field cannot persist — the backfill re-derives what repair just
  dropped and the two stages undo each other on every pass. Hygiene belongs at every derivation point,
  not only at the LLM one.
- **`ReviewDriftAsync` returning null is normal** (unavailable, no content, or unparseable) and means
  "retry on a future run", not "no drift".
- **Drift review is the enrichment surface with an unbounded budget, and the only one whose cost is
  recurring.** Every other call is per-memory and happens once; drift review is `NightlyBatch` calls per
  repo per night, and until `ReviewIntervalDays` existed it re-reviewed settled memories forever — 27
  repos × 25 calls held a local 12B model busy for over two hours a night, indefinitely. When adding a
  model-calling stage, ask what makes it stop, not just what caps one run. The startup banner's
  `Nightly AI:` line is where the answer becomes visible to whoever is running the service.

## Executable references

- `tests/Eidet.Core.Tests/Services/EnrichmentServiceTests.cs` — **the authority on field policy**: which
  fields are filled, the tombstone and body-less-heading refusals, the chain-of-thought entity drop, and
  the unavailable-port short circuits.
- `tests/Eidet.Core.Tests/Text/EntityHygieneTests.cs` — **the authority on what survives the entity
  field**, in both directions: the corpus's real chain-of-thought leakage on one side, and the
  identifiers that must not be filtered away on the other.
- `tests/Eidet.Core.Tests/Services/EnrichmentServiceDriftReviewTests.cs` +
  `tests/Eidet.Core.Tests/Enrichment/ReflectionProposalParserTests.cs` — settle the structured calls and
  their tolerant parsing (including malformed output ⇒ null/empty).
- `tests/Eidet.Core.Tests/Services/EnrichmentWorkerTests.cs` — settles the subscription driver,
  including that a failed enrichment is still acked.
- `tests/Eidet.Core.Tests/Services/EnrichmentHealthCacheTests.cs` — settles the optimistic-expiry rule.
- `tests/Eidet.Core.Tests/Enrichment/FallbackEnrichmentAdapterTests.cs` — **the authority on the chain**:
  first-up-and-answering wins, down/empty falls through, `ModelName` follows the answer, a recovered
  primary takes over.
- `tests/Eidet.Core.Tests/Enrichment/{EnrichmentHttpTests,OpenAiEnrichmentAdapterTests}.cs` — settle
  `/v1` normalisation, the bearer header, and that the thinking kwarg is present only when configured.
- `tests/Eidet.Core.Tests/Configuration/ConfigManagerTests.cs` (`Enrichment_ChainWithKeyAndThinking_RoundTrips`,
  `Enrichment_LegacyFlatConfig_HasNoFallbacks`) — settle the config shape and that `Backends` is never
  persisted.
- `tests/Eidet.Service.Tests/HealthMonitorTests.cs` — the monitor reports the chain (`+N fallback`) on reload.
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
