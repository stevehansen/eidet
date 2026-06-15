# Loose End Spec: Parked Open-Work

> **Scope**: This spec defines the **Loose End** feature — a way for an agent to *park* a terse, low-context, still-actionable note mid-task (a suspected bug, out-of-scope work) and reliably get it back until it is explicitly *resolved*. A Loose End is **open work, not recalled knowledge**: it is a first-class document distinct from `MemoryEntry`, exempt from decay/consolidation, and surfaced proactively until closed. Terminology is governed by `UBIQUITOUS_LANGUAGE.md` (§Loose End lifecycle).
>
> *Status: ⏳ **designed, not yet implemented**. Interface hardened via `/design-interface` (2026-06) — chosen design is a hybrid of the "common case" and "ports & adapters" framings; see §Designs Considered (and Rejected).*

---

## Problem Statement

- **No home for half-formed work**: mid-task an agent discovers a possible bug or out-of-scope item but lacks the context to fix it now. Writing a `.md` or opening a GitHub issue is too heavy when little is known; the thought is dropped instead.
- **Memories are the wrong tool for it**: storing the note as an `Observation` fails on every axis a deferred todo needs —
  - it **decays** (FadeMem, fastest half-life for Observations) and is **auto-expired by age** (`ObservationRetentionStage`), so it silently dies before it is acted on;
  - it can be **consolidated** into an Insight or **deduped** away (a near-identical second todo tombstones the first);
  - it ranks by relevance in `eidet_recall` and is **excluded from the wake-up `eidet_context`** (L1 is Insight/Procedure/Heuristic only), so the agent must *remember to ask* — defeating "pick it up later";
  - terse self-talk phrasing ("revisit this") risks the signal/self-talk **write gate**.
- **The closure verbs are already overloaded**: the model has three ways a memory stops mattering (Forget / TTL expiry / Supersession). A todo's "done" needs to be a *fourth, distinct* concept or quality reports conflate a *done todo* with an *expired memory*.

## Goals

1. **One-call park** — drop a terse note in a single `eidet_park` call; no required ceremony, no fight with the signal gate.
2. **Survive until resolved** — an open Loose End never decays, never auto-expires, is never consolidated or deduped. The only exit is an explicit resolve.
3. **Resurface deterministically** — open Loose Ends appear in the <600-token wake-up context *because they are open*, not because they win a relevance ranking; plus tag/area ride-along on recall and an explicit pull list.
4. **Typed resolution** — `Done` / `Dropped` / `Promoted` / `Superseded`, distinguishable at the data level from any memory-closure path.
5. **Promote bridge** — graduate a confirmed Loose End into a real, gated `MemoryEntry` (or link an external issue) in one call.
6. **Don't pollute knowledge** — Loose Ends live in their own collection; they never enter the memory recall pool's ranking, are Local-only, and are never exported in a Pack.

## Non-Goals

- **Not a task tracker.** No assignees, no due dates, no sprint/board semantics. "Todo"/"Task" are aliases to avoid (`UBIQUITOUS_LANGUAGE.md`).
- **No date/time reminders.** There is no per-item scheduler primitive in Eidet and this feature does not add one. Resurfacing is on-wake-up / on-recall / on-pull, never "ping me at time T."
- **Not a 5th `MemoryType`** and **not a status flag on `MemoryEntry`.** (Both rejected during design — see §Designs Considered.)
- **Not shared.** Local layer only; never serialized into a Pack or a Shared layer.
- **Not auto-closed.** Nothing resolves a Loose End implicitly; backlog hygiene is surfaced (counts/aging), not enforced by silent expiry.

---

## Codebase Constraints

Load-bearing facts the design assumes; revisit the relevant section if any changes.

| Concern | Current state | Impact on Loose Ends |
|---------|---------------|----------------------|
| Tool registration | `ToolDispatcherFactory.Create` builds the single dispatcher shared by REST + MCP (one array). MCP exposure gated by `IToolHandler.McpExposed` (default `true`). | `eidet_park` + `eidet_resolve` = two new handler classes + two lines here. They auto-expose on MCP (6→8). Human browse/list stays off-MCP via `McpExposed => false`. |
| Write gate | `WriteValidator.Validate` runs `SecretScanRule.Check` then `CheckSignal` (20-char floor, low-signal exact-match, self-talk prefixes). `WriteValidator.cs:30-35`. | Park reuses **secret scan only** and skips `CheckSignal`. The gate split must be enforced by a port (see §Risks) so a future caller can't route park through the full gate. |
| Memory write funnel | `MemoryService.StoreAsync` (`MemoryService.cs:61`): `WriteValidator.BuildEntry` → `FindDuplicateAsync` → `RunMutationAsync` (pre/post hooks → `ctx.StoreNewAsync`). | **Promote** must re-enter *this* gated path (via `IPromotionPort`) so a graduated memory is secret-/signal-scanned like any other. |
| Wake-up context | `MemoryService.GetContextAsync` (`MemoryService.cs:400`): L0 count header → L1 `GetTopScoredAsync([Insight,Procedure,Heuristic], 60)` scored/budgeted/dense-packed (`maxItems=20`), token-capped at `maxTokens=600`. Inject seam between L0 (`:417`) and L1 (`:423`). | The Loose End slice injects here, with a reserved sub-budget carved from L1 (never from L0/identity). |
| Maintenance pipeline | FadeMem decay, `ConsolidationEngine`, `DedupEngine`, `ObservationRetentionStage`, `TtlExpiryStage` all enumerate the **`MemoryEntry`** collection only. | The "no decay / no consolidation" invariant is **structural**: a separate `looseends/*` collection is simply never read by any stage. No exemption flags to forget. |
| Port idiom | `IEnrichmentPort` (Ollama/Null internal, `InMemoryEnrichmentAdapter` public) + an `IEidetStore`-style interface with 16 in-memory test fakes. | Mirror exactly: `ILooseEndStore` (Raven + in-memory), `IPromotionPort` (adapter over `MemoryService` + in-memory), time via `TimeProvider`. |
| Document IDs | `MemoryIdGenerator.Generate(repoId, type, hash)` → `memories/{repoId}/{type}/{shortHash}`; hash over content+createdAt. | Sibling generator → `looseends/{repoId}/{shortHash}` (no type segment — one kind). Time comes from `TimeProvider` for deterministic tests. |
| API route ordering | `EidetApi.cs:168` registers a catch-all `GET /api/eidet/{id}`. | Every `/api/eidet/loose-ends*` route MUST register **before** line 168, alongside the exact routes near `:125–139`. |

---

## Naming (Ubiquitous Language)

Canonical terms are defined in `UBIQUITOUS_LANGUAGE.md` (§Loose End lifecycle). In brief:

| Term | Meaning |
|------|---------|
| **Loose End** | A deferred, still-actionable note parked mid-task. Open work, not a Memory. |
| **Park** | Capture a Loose End (`eidet_park`). Terse/speculative phrasing allowed. |
| **Resolve** | Explicitly close a Loose End with a **Resolution kind** (`eidet_resolve`). Distinct from **Forget** / **TTL expiry** / **Supersession**, which retire a *Memory*. |
| **Resolution kind** | `Done` · `Dropped` · `Promoted` · `Superseded`. |
| **Promote** | Resolve by graduating the note into a `MemoryEntry` (or linking an external issue). |

> **Terminology landmine (from the UL pass):** "Resolved" must remain a typed, first-class Loose-End state. Do **not** implement it by reusing a memory-closure path's meaning — a resolved todo is not an expired/forgotten/superseded memory.

### Relationship to memories

| | **Memory** (`MemoryEntry`) | **Loose End** |
|---|---|---|
| Nature | Recalled knowledge | Open work |
| Collection | `memories/*` | `looseends/*` |
| Decays / consolidates / dedups | Yes | **Never** |
| Surfaces by | Relevance score (recall), L1 budget (context) | Open-ness (wake-up slice), tag overlap (recall ride-along), explicit pull |
| Closure | Forget / TTL expiry / Supersession | **Resolve** (`Done`/`Dropped`/`Promoted`/`Superseded`) |
| Shared in a Pack | Yes | **No** (Local only) |
| Write gate | Secret + Signal | Secret only (Signal skipped) |

---

## Domain Model

```csharp
namespace Eidet.Core.LooseEnds;

public enum LooseEndState { Open, Resolved }
public enum ResolutionKind { Done, Dropped, Promoted, Superseded }

/// <summary>
/// Open work an agent parked mid-task. A sibling of MemoryEntry, NOT a MemoryType.
/// ID: "looseends/{repoId}/{shortHash}". Lives in its own RavenDB collection so no
/// maintenance stage (FadeMem / consolidation / dedup / retention / TTL) ever touches it.
/// </summary>
public sealed class LooseEnd
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";            // Local layer only; no LayerId

    public string Note { get; set; } = "";              // terse, speculative; secret-scanned, NOT signal-gated
    public List<string> Tags { get; set; } = [];        // ride-along match keys (v1: one list, no separate "Areas")
    public int Priority { get; set; } = 2;              // 1 high / 2 normal / 3 low — wake-up ordering only; never decays

    public LooseEndState State { get; set; } = LooseEndState.Open;
    public ResolutionKind? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
    public string? PromotedToMemoryId { get; set; }     // set when Resolution == Promoted mints a MemoryEntry
    public string? ExternalRef { get; set; }            // e.g. "gh#412" when promoted to an issue instead of a memory

    public string Source { get; set; } = "claude-session";
    public string? SourceSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
```

**Field rationale.** `Priority` is a 3-level ordering knob for the wake-up slice only — it is explicitly *not* an `Importance`/decay weight (Loose Ends do not decay). `Tags` is a **single** list in v1: tag-overlap drives recall ride-along; a separate file-anchored "Areas" axis is deferred until it earns its keep. `Resolution`/`ResolutionNote`/`PromotedToMemoryId`/`ExternalRef` make closure typed and auditable, keeping it distinct from the three memory-closure mechanisms. Resolved Loose Ends are **kept** (append-only audit), filtered out of the open views.

---

## Write Path: the gate split

Park is deliberately low-friction, but **secret scanning is non-negotiable** (it is always-on for every write surface in Eidet).

- `ParkAsync` calls `SecretScanRule.Check(note)` directly and **skips `CheckSignal`** — a terse, speculative note ("flaky test in auth path", "revisit retry logic") is the *point* and must never be rejected as low-signal or self-talk.
- There is **no `ParkValidator` rule-chain** — one rule (secret scan) does not warrant the `WriteValidator` chain abstraction. Park calls the secret rule and constructs the `LooseEnd` in one canonical path (mirroring the single-construction-path discipline of `WriteValidator.BuildEntry`).
- **Promote re-enters the full gate.** When a Loose End is resolved as `Promoted`, the minted `MemoryEntry` goes through `MemoryService.StoreAsync` (secret **and** signal gate, dedup, hooks) via `IPromotionPort` — a weak promoted note is rejected at the memory boundary even though the original park bypassed the signal gate.

---

## Lifecycle & the no-decay invariant

- **Open → Resolved is the only transition**, and it is always explicit (`eidet_resolve`). No code path removes or expires an open Loose End.
- **No decay / no consolidation / no dedup / no TTL** — guaranteed *structurally*: `LooseEnd` is a separate collection that no maintenance stage enumerates. There is no `Validity.ValidUntil`/`ForgetAfter` field to expire against.
- **Resolve is idempotent** — resolving an already-resolved Loose End is a no-op that returns the current document (error defined out of existence, not thrown).
- **Resolution kinds:**
  - `Done` — handled; kept as audit history.
  - `Dropped` — decided not worth doing; `ResolutionNote` captures why. *Not* the same as Forget (which retires a Memory).
  - `Promoted` — graduated into a `MemoryEntry` (`PromotedToMemoryId`) via the gated path, **or** linked to an external issue (`ExternalRef`). The bridge from "scratch hunch" to durable knowledge.
  - `Superseded` — folded into another Loose End (the manual analogue of dedup, agent-driven, never automatic).

---

## Surfacing (three modes)

### 1. Wake-up slice (primary)
Injected into `GetContextAsync` between L0 (`:417`) and L1 (`:423`):
- Reserved sub-budget of **~120 tokens carved from the L1 budget** (never from L0/identity). If there are no open Loose Ends, L1 gets the full budget.
- **Cap 3 items** in v1; rendered with a distinct `[~]` prefix so the agent reads them as open work, not facts.
- **Ordering: `Priority` ascending (1 = high, so high-priority sorts first), then `CreatedAt` asc** (oldest-first within a priority tier) — surfaces the *stalest* high-priority work, which is the backlog-hygiene signal.
- L0 count header gains an addendum: `[Memory: … | 3 open loose ends]`.

### 2. Recall ride-along
- `eidet_recall` returns matching open Loose Ends whose `Tags` overlap the query — **on by default** (the user named tag/area-triggered surfacing a must-have).
- Returned in a **separate section** of the result, never mixed into the relevance-ranked memory list, and capped — so Loose Ends never pollute knowledge ranking.

### 3. Pull list
- Explicit listing via REST/Web UI (`GET /api/eidet/loose-ends?repo=…&state=open`) for humans, and available to the agent through the recall ride-along path. Listing/browse is **off the MCP surface** (`McpExposed => false`).

---

## Ports & Service

Mirrors the existing `IEnrichmentPort` / `IEidetStore` idiom so the whole park → surface → resolve → promote loop is testable end-to-end against in-memory adapters (no RavenDB, no MCP transport).

```csharp
// PORT 1 — storage (Raven prod adapter + in-memory test adapter)
public interface ILooseEndStore
{
    Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default);
    Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default);
    Task UpdateAsync(LooseEnd e, CancellationToken ct = default);
    Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default);
    Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default);
}

// PORT 2 — promote bridge: the ONLY edge into the gated memory write path.
//   prod: MemoryServicePromotionAdapter (→ MemoryService.StoreAsync); test: InMemoryPromotionAdapter
public interface IPromotionPort
{
    Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default);
}
public sealed record PromoteOptions(MemoryType Type, float Importance, string? ExternalRef);
public sealed record PromotionResult(bool Success, string? MemoryId, string? ExternalRef, string? Reason);

// PORT 3 — time via TimeProvider (System in prod, FakeTimeProvider in tests) for deterministic IDs/ordering.

public sealed class LooseEndService
{
    public LooseEndService(ILooseEndStore store, IPromotionPort promote, TimeProvider clock, IHookRunner hooks);

    public Task<ParkResult>    ParkAsync(string repoId, string note, CancellationToken ct = default);  // 80% surface
    public Task<ParkResult>    ParkAsync(ParkOptions opts, CancellationToken ct = default);             // 20% surface (+Tags, +Priority)
    public Task<ResolveResult> ResolveAsync(string id, ResolutionKind kind, ResolveOptions? o = null, CancellationToken ct = default); // idempotent

    public Task<string>                       RenderWakeupSliceAsync(string repoId, int maxTokens, CancellationToken ct = default); // pure, deterministic
    public Task<IReadOnlyList<LooseEnd>>      RideAlongAsync(string repoId, IReadOnlyList<string> recallTags, CancellationToken ct = default);
    public Task<IReadOnlyList<LooseEnd>>      PullAsync(string repoId, int max = 20, CancellationToken ct = default);
}

public sealed record ParkOptions(string RepoId, string Note)
{
    public IReadOnlyList<string>? Tags { get; init; }
    public int Priority { get; init; } = 2;
    public string Source { get; init; } = "claude-session";
}
public sealed record ResolveOptions
{
    public string? Note { get; init; }
    public MemoryType PromoteType { get; init; } = MemoryType.Insight;  // honored only when kind == Promoted
    public float PromoteImportance { get; init; } = 0.5f;
    public string? ExternalRef { get; init; }                          // link instead of mint
}
```

**Coupling note.** `GetContextAsync` lives on `MemoryService` and is the one place memories and Loose Ends meet. `MemoryService` gains an optional `LooseEndService? looseEnds = null` constructor dependency; when null it acts as a NullObject (empty slice), so existing `MemoryService` tests stay green with no behavior change.

---

## MCP Tools (6 → 8)

The agent-facing surface deliberately grows from 6 to 8 — a justified reversal of commit `298aa05` ("slim to 6 core tools"), because Loose Ends are a genuinely new capability with their own verbs, not a knob on `eidet_store`/`eidet_forget`. Folding them in would force a `kind` discriminator onto those tools and mix two lifecycles in one handler.

| Tool | Required | Optional | Returns |
|------|----------|----------|---------|
| `eidet_park` | `note` | `tags` (string[]), `priority` (1\|2\|3) | `{ id, state: "open" }` |
| `eidet_resolve` | `id`, **`kind`** (`done`\|`dropped`\|`promoted`\|`superseded`) | `note`, `promote_type`, `promote_to` (external ref) | `{ id, state: "resolved", kind, promotedToMemoryId? }` |

> **`kind` is required** on `eidet_resolve` (the C# convenience overload may default to `Done`, the tool does not). It is one token, and Done-vs-Dropped-vs-Promoted is exactly the quality signal Eidet exists to capture — a defaulted `Done` silently mislabels abandoned work.

`eidet_park`'s schema description must steer phrasing toward facts and away from the (bypassed-but-still-worth-avoiding) self-talk shape, e.g.: *"Park an open todo to revisit later. Stores a Loose End that won't decay or auto-expire until you resolve it. Use for terse mid-task notes ('possible bug in retry logic, revisit')."*

---

## REST / API Additions

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/eidet/loose-ends?repo=…` | Park a Loose End. |
| POST | `/api/eidet/loose-ends/{id}/resolve` | Resolve with a kind. |
| GET  | `/api/eidet/loose-ends?repo=…&state=open` | List (human pull surface). |

**Route ordering:** all `/api/eidet/loose-ends*` routes register **before** the catch-all `GET /api/eidet/{id}` at `EidetApi.cs:168` (alongside the exact routes near `:125–139`), or they are misread as memory-by-id lookups.

---

## Implementation Sketch

```
Eidet.Core/LooseEnds/
├── LooseEnd.cs                       # domain doc + enums
├── LooseEndService.cs                # facade over the three ports; park/resolve/surface
├── ILooseEndStore.cs                 # PORT 1
├── IPromotionPort.cs                 # PORT 2 (+ PromoteOptions / PromotionResult)
├── LooseEndIdGenerator.cs            # looseends/{repoId}/{shortHash}
└── LooseEndWakeup.cs                 # pure slice renderer (cap, ordering, [~] prefix)

Eidet.Core/Storage/
└── RavenLooseEndStore.cs             # ILooseEndStore prod adapter (sibling collection)

Eidet.Core/LooseEnds/Promotion/
├── MemoryServicePromotionAdapter.cs  # IPromotionPort → MemoryService.StoreAsync (gated)
└── (GitHubIssuePromotionAdapter)     # v1.1 — link instead of mint

Eidet.Service/Tools/Handlers/
├── ParkToolHandler.cs                # eidet_park   (template: StoreToolHandler)
└── ResolveToolHandler.cs             # eidet_resolve (template: ForgetToolHandler)
```

Wiring: register `ParkToolHandler` + `ResolveToolHandler` in `ToolDispatcherFactory.Create`; pass `LooseEndService` into `MemoryService` (optional). `GetContextAsync` calls `RenderWakeupSliceAsync` between `:417` and `:423`.

Tests:
- `InMemoryLooseEndStore` + `InMemoryPromotionAdapter` + `FakeTimeProvider` → end-to-end test: park → assert wake-up slice contains it → resolve-as-promoted → assert a gated `MemoryEntry` was minted → assert the slice no longer surfaces it.
- Gate-split test: a `note` containing a credential is rejected; a terse/self-talk note is accepted.
- Wake-up budget test: N open ends respect the item cap and never spend more than the reserved token sub-budget.

---

## Phased Delivery

| Phase | Scope |
|-------|-------|
| **v1 (MVP)** | `LooseEnd` domain + `ILooseEndStore` (Raven + in-memory) + `IPromotionPort` (mint-a-memory adapter) + `TimeProvider`. `eidet_park` / `eidet_resolve` (kind required). Wake-up slice (cap 3, ~120 tok, `[~]`, high-priority → oldest). Recall ride-along on by default. Pull list via REST/UI. Promote-to-memory. |
| **v1.1** | `GitHubIssuePromotionAdapter` (resolve-as-Promoted with an external ref links an issue instead of minting). Quality-dashboard **open count + aging** (the rot mitigation). Web UI Loose Ends panel. |
| **v2** | Only on demonstrated demand: per-tag/area split (`Areas`), priority auto-bump for aging items, batch resolve. Reminders remain out of scope unless a per-item scheduler primitive is introduced separately. |

---

## Resolved Design Decisions

| # | Decision |
|---|----------|
| 1 | **Separate first-class document/collection** (`looseends/*`), not a 5th `MemoryType` and not a status flag on `MemoryEntry`. The "no decay / no consolidation" invariant is therefore *structural*, not a set of exemption guards. |
| 2 | **Two MCP tools** `eidet_park` + `eidet_resolve` (surface 6 → 8); human list/browse stays off-MCP. |
| 3 | **`kind` is required** on `eidet_resolve`; the C# convenience overload may default to `Done`. |
| 4 | **Single `Tags` list** in v1 (no separate `Areas` axis); ride-along matches tag overlap. |
| 5 | **Park = secret-scan only**, signal gate skipped; **promote = full gate** via `IPromotionPort`. No `ParkValidator` chain (one rule). |
| 6 | **Wake-up slice**: cap 3 / ~120 tokens (carved from L1, never L0); ordered `Priority` ascending (1 = high first) then `CreatedAt` asc; `[~]` prefix; L0 count addendum. |
| 7 | **Recall ride-along on by default**, in a separated, capped result section. |
| 8 | **One-call promote**; resolve mints the `MemoryEntry` (gated) and records `PromotedToMemoryId`, or links an `ExternalRef`. |
| 9 | **Idempotent resolve** (re-resolve = no-op). Resolved Loose Ends are kept as audit, filtered from open views. |
| 10 | **No date/time reminders**; Local-layer only; never exported in a Pack. |
| 11 | **Three ports** (`ILooseEndStore`, `IPromotionPort`, `TimeProvider`); `MemoryService` gets an optional `LooseEndService?` (NullObject) for the slice. |

---

## Designs Considered (and Rejected)

Four interface framings were explored in parallel via `/design-interface`. The chosen design takes the **common-case caller surface** built on the **ports-&-adapters seams**.

| Framing | Gist | Verdict |
|---------|------|---------|
| **A — Minimize** | Two methods on `MemoryService`; extend the memory store interface; unscored 5-item slice. | **Rejected (structure).** Couples two lifecycles onto `MemoryService` and extends `IEidetStore` with loose-end concerns (information-hiding leak); its own unscored slice is the rot trap with no priority lever. Its *minimalism instinct* is kept (thin everything, no extra interfaces). |
| **B — Maximize flexibility** | Five interfaces (`IParkRule` chain, `IResolutionHandler` registry, `ILooseEndSurfacer`, `ILooseEndRanker`, `ILooseEndStore`) + a `JsonObject Extra`. | **Rejected (over-engineering).** Shallow interfaces (interface ≈ impl) against the codebase's deep-module ethos; an enum **and** a handler registry for resolution kinds invites "kind with no handler" mismatches a plain `switch` eliminates. Self-rejected by the design agent. |
| **C — Common case** | Dedicated `LooseEndService`, 80/20 overload pair, idempotent resolve, capped priority-ordered slice, ride-along default, one-call promote. | **Adopted (caller surface).** 1-line happy path; matches the documented "20% surface" convention; sensible defaults. |
| **D — Ports & adapters** | `ILooseEndStore` + `IPromotionPort` + `TimeProvider`; whole loop testable in-memory; `MemoryService` gets optional `LooseEndService?`. | **Adopted (seams).** `IPromotionPort` is the deep one — severs the service from `MemoryService`, holds the mint-vs-link fork, keeps the gate-split independently testable. |

---

## Risks

- **Wake-up-slice rot** *(consistency/operational — the primary risk)*: if agents park but never resolve, the same N highest-priority ends render every wake-up and the agent learns to ignore `[~]` lines. The data model can't fix this; mitigation is the hard item cap + Priority/age ordering **plus a quality-dashboard open-count and aging signal** (shipped in v1.1, not deferred indefinitely).
- **Gate-split leak** *(security/correctness)*: if anyone later lets promote call `StoreAsync` directly instead of through `IPromotionPort`, a future caller may route *park* through the gated builder and the signal gate starts rejecting the terse notes the feature exists to keep. The port is the guardrail — keep it, and keep park's secret-only path and promote's full-gate path independently tested.
- **Low-value promoted memories**: promote could mint weak Insights. Bounded structurally — promotion goes through the full `WriteValidator` signal gate, so a weak promoted note is rejected at the memory boundary.
- **Backlog growth**: an unbounded open set is working state, not knowledge, but still costs the slice's attention. Surfaced (count + aging), never silently expired (silent expiry is exactly the failure mode the feature exists to prevent).
