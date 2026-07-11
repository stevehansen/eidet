# Valence Spec: Negative Knowledge (dead-ends, refuted claims)

> **Scope**: This spec defines **Valence** — a small stance dimension, *orthogonal to `MemoryType`*, that lets a memory carry negative polarity: "approach X was tried and failed because Y", a disproven hypothesis, a cautionary warning. The goal is that a future session **recalls the dead-end before repeating it**. Valence is a property of *knowledge* (a `MemoryEntry`), distinct from a `LooseEnd` `Dropped` resolution (abandoned *open work*) and richer than a distilled `Heuristic` rule. Terminology is governed by `UBIQUITOUS_LANGUAGE.md`.
>
> *Status: ⏳ **designed, not yet implemented**. Interface hardened via `/design-interface` (2026-07) — chosen design is the **Valence dimension (framing B)** wrapped in **common-case caller ergonomics (framing C)**; see §Designs Considered (and Rejected). Motivated by Lilian Weng's "Harness Engineering for Self-Improvement" (Future Challenge #3, "make failed attempts easy to preserve" — `docs/harness-engineering-2026-07-04.md`).*

---

## Problem Statement

- **No home for negative knowledge.** An agent that discovers "we tried X and it failed because Y" has nowhere correct to put it. Today's options all fail:
  - **`Observation`** — the intuitive home for a raw session finding — is the **worst**: fastest decay (30d, super-linear `FadeMemCurve.cs:11`), auto-expired by `ObservationRetentionStage`, and **excluded from the L1 wake-up** (`GetContextAsync` fetches only `[Insight, Procedure, Heuristic]`, `MemoryService.cs:~648`). The failure silently dies before it's ever recalled.
  - **`Heuristic`** ("don't do X") holds the *distilled rule* but not the *failure narrative*, and its outcome-negative nature trips `MemoryRoi.Factor` (`MemoryRoi.cs:28-35`), which demotes any Procedure/Heuristic with `FizzleCount > EchoCount` **below its trust floor** — the ROI logic can bury exactly the warning we want surfaced.
- **Contradicting memories silently corrupt each other (latent bug, exists today).** Independent of this feature, the current pipeline cannot hold a positive and a negative claim about the same subject:
  - **Store dup-gate** (`MemoryService.cs:87`, `DuplicateThreshold=0.92f`) rejects a new memory that is content-similar to an existing one — "X does **not** work" can be swallowed as a near-duplicate of "X works".
  - **Dedup** (`DedupEngine`, ~0.86 semantic / 0.85 lexical) merges same-type near-duplicates and **unions their tags** (`MergeAsync`), so the survivor carries *both* polarities and the loser is tombstoned.
  - **Consolidation** (`ConsolidationEngine.cs:~62`) groups by tag overlap and takes `representative = highest-importance`, collapsing a mixed-polarity group to one sign and dropping the negation.
  - Polarity is invisible to embeddings and Jaccard overlap, so none of these stages can see the contradiction. **This is a data-loss defect the moment anyone stores contradicting memories** — the Valence guards fix it as a side effect.

## Goals

1. **Record a failure in one call** — `eidet_store` with `negative: true` (or explicit `valence`) captures a dead-end with no ceremony; the caller need not reason about type or decay.
2. **Survive and resurface** — negative knowledge lands on a long-lived, L1-visible lifecycle, and gets a bounded wake-up floor so a lone dead-end is not crowded out.
3. **Never collapse into its contradiction** — a `Refuting` memory is never deduped, dup-gated, or consolidated into the `Affirming` claim it contradicts (and vice-versa).
4. **Stay orthogonal to type** — a refuting *Procedure*, a refuting *Insight*, and a cautionary *Heuristic* are all expressible; polarity does not become a category.
5. **Read as a warning** — surfaced with a distinct glyph (`✗` refuting / `⚠` cautionary) so the agent parses it as a dead-end, not as advice to follow.
6. **Zero migration** — `Neutral = 0` means every existing document backfills to "no stance" for free.

## Non-Goals

- **Not a 5th `MemoryType`.** (Rejected — see §Designs Considered. A type flattens the valence×type matrix and forces a benchmark-regressing budget re-tune.)
- **Not a recall-scoring change.** A dead-end is not less important, only differently *signed* — no Fuse/trust/ROI multiplier keys on valence.
- **Not sentiment / not confidence.** `Confidence` (how sure) and `Valence` (which way the claim points) are independent axes; a high-confidence `Refuting` memory is the strongest kind of dead-end.
- **Not the Loose End `Dropped` path.** That closes *open work*; Valence marks *retained knowledge*.

---

## Codebase Constraints

Load-bearing facts the design assumes; revisit the relevant section if any changes.

| Concern | Current state | Impact on Valence |
|---------|---------------|-------------------|
| Type persistence | `MemoryType` is a **plain enum stored as a string** (RavenDB default, no `SaveEnumsAsIntegers`), also baked into the doc id. `MemoryType.cs:3-9`. | A new *field* (not a type) serializes by name; existing docs simply lack it and load as `Neutral`. No index rebuild, no backfill. |
| Write funnel | `MemoryService.StoreAsync` → `WriteValidator.BuildEntry` (`WriteValidator.cs:43-72`) → `FindDuplicateAsync` (`:87`, 0.92) → `RunMutationAsync` (pre/post hooks, supersession, `StoreNewAsync`). Single canonical construction + single mutation entry. | `BuildEntry` sets `Valence = opts.Valence`; `BuildEditEntry` carries it forward. The dup-gate is **choke point #1**. |
| Dedup | `DedupEngine` loops `Enum.GetValues<MemoryType>()` per type; `MergeAsync` merges within a type's candidate list and **unions tags**. | **Choke point #2**: `MergeAsync` must early-return when valences conflict; survivor keeps the opinionated stance. |
| Consolidation | `ConsolidationEngine.ConsolidateAsync` (`:28`) groups un-consolidated `Observation`s by `TagOverlapGrouper`, needs ≥3, representative = highest importance, creates/boosts an `Insight` (near-dup `FindDuplicate` at 0.85). | **Choke point #3**: partition each group by valence sign before the ≥3 check; created memory inherits the group's valence; boost path skips on conflict. |
| Wake-up context | `GetContextAsync` (`MemoryService.cs:600-712`): L1 = `GetTopScoredAsync([Insight,Procedure,Heuristic])`, `maxItems=20`, budgets Insight 50% / Procedure 30% (capped 3) / Heuristic remainder, **no Observations**; prefix switch `[I]/[P]/[H]/[O]` (`:685-691`); L1 scoring `RecallScoring.ComputeL1Score` (fixed 7-day half-life, importance weight ~0.3). | A **bounded negative-valence floor (≤2 slots)** is carved from the Insight/Heuristic budget so a lone dead-end is not crowded out. Glyph replaces/augments the type prefix for signed memories. |
| Recall budgets | `RecallScoring.ApplyTypeBudgets` (`RecallScoring.cs:176-218`): Insight .40 / Observation .25 / Procedure .20 / Heuristic rest — **per type, valence-agnostic**. | Unchanged. Callers who must see dead-ends pass the `valence` recall filter. |
| Search index | `Memories_Search` composite `SearchText` (Content+Summary+OneLiner+ForesightHint+Tags+Entities) + `SearchVector`. Enum fields (`Type`, `Provenance`) indexed for `WhereEquals`, not in `SearchText`. | Add `Valence` to the index projection for `WhereEquals` filtering — same treatment as `Type`. Not added to `SearchText` (polarity is not a lexical match key). |
| Store tool | `StoreToolHandler` (`:29-61`): `type` parsed via `Enum.TryParse(..., ignoreCase:true)`; required = `content`+`type`; schema `:77-119`. Surface deliberately slim (6 core MCP tools). | Add optional `negative` (bool) + `valence` (enum string); **no new tool** — the sugar rides `eidet_store`. |
| Pack round-trip | `MarkdownPackFormat` serializes memory fields to YAML frontmatter. | `Valence` must be emitted/parsed in the pack (default `Neutral` omitted) so signed memories survive export/import. |
| Existing negative signals | `FizzleReason { WrongContext, Incorrect, VersionDrift, Other }` + `FizzleCount` (`FizzleReason.cs:9-25`) capture "*this memory was wrong*" (feedback on recall). `LooseEnd.Dropped` closes open work. | Distinct layers — Valence marks the *content's* polarity at store time, not recall feedback or work closure. No overlap to reconcile. |

---

## Naming (Ubiquitous Language)

Proposed canonical terms (to be added to `UBIQUITOUS_LANGUAGE.md` on implementation, mirroring the Loose End lifecycle entry):

| Term | Meaning |
|------|---------|
| **Valence** | The stance a memory's content takes toward its subject. One of `Neutral` / `Affirming` / `Refuting` / `Cautionary`. Orthogonal to `MemoryType`. |
| **Affirming** | The claim holds — "X works". (The default for a positively-phrased memory that opts in.) |
| **Refuting** | The claim is negated — "X was tried and does **not** work", a disproven hypothesis. The primary "dead-end". |
| **Cautionary** | Not a hard negation but a warning — "X works but has sharp edges Y". |
| **Neutral** | No stance (default; every pre-existing memory). |
| **Dead-end** | Informal alias for a `Refuting` memory; the reserved tag applied by the `negative: true` sugar. |

> **Terminology landmine:** *Valence* is not *Confidence* (certainty), not *Fizzle* (recall feedback that a memory was wrong), and not a Loose End *Dropped* (abandoning open work). Keep them separate in code and UI.

---

## Domain Model

```csharp
namespace Eidet.Core.Domain;

/// <summary>
/// The stance a memory's content takes toward its subject — orthogonal to MemoryType.
/// Neutral = 0 so every pre-existing document backfills to "no stance" with no migration.
/// </summary>
public enum Valence { Neutral = 0, Affirming, Refuting, Cautionary }
```

One field on `MemoryEntry` (after `Type`, `MemoryEntry.cs:~17`):

```csharp
public Valence Valence { get; set; } = Valence.Neutral;
```

The entire cross-stage contract is one pure helper — **callers and stages never do sign arithmetic**, they ask a domain question:

```csharp
namespace Eidet.Core.Memory;

public static class ValencePolarity
{
    private static int Sign(Valence v) => v switch
    {
        Valence.Affirming  =>  1,
        Valence.Refuting   => -1,
        _                  =>  0,   // Neutral, Cautionary: no hard sign
    };

    /// <summary>True iff collapsing a and b would erase a contradiction (opposite hard signs).</summary>
    public static bool Conflicts(Valence a, Valence b) => Sign(a) * Sign(b) < 0;

    /// <summary>Survivor stance when a NON-conflicting pair merges: keep the opinionated one.</summary>
    public static Valence Merge(Valence a, Valence b) => a != Valence.Neutral ? a : b;
}
```

`Cautionary` is deliberately sign-`0`: a warning does not *contradict* an affirming claim, so it should still be free to dedup/consolidate with related memories — only hard `Affirming`↔`Refuting` pairs are protected.

**Threaded through (real sites):**

- `StoreOptions.Valence { get; init; } = Valence.Neutral;` → `WriteValidator.BuildEntry` sets it; `BuildEditEntry` carries `original.Valence` forward.
- `RecallOptions.Valence` (nullable filter) → `MemoryQuery.Valence` → `WhereEquals` on the index.
- `MemorySearchResult.Valence` so callers/renderers see the stance.

---

## Write Path: the three polarity guards

The correctness core. All three are mandatory — the current pipeline corrupts contradicting memories without them (see §Problem Statement).

1. **Store dup-gate** (`MemoryService.cs:87`):
   ```csharp
   if (duplicate is not null && !ValencePolarity.Conflicts(duplicate.Valence, entry.Valence))
       return StoreResult.Duplicate(duplicate.Id);
   // a Refuting store at 0.94 similarity to an Affirming memory now SURVIVES instead of being swallowed
   ```
2. **Dedup** (`DedupEngine.MergeAsync`, at entry):
   ```csharp
   if (ValencePolarity.Conflicts(a.Valence, b.Valence)) return;   // never fold a claim into its contradiction
   merged.Valence = ValencePolarity.Merge(a.Valence, b.Valence);  // survivor keeps the opinionated stance
   ```
3. **Consolidation** (`ConsolidationEngine`): partition each tag group by `Sign` **before** the ≥3 threshold; the created/boosted memory inherits the group's valence; the near-insight boost path skips on `Conflicts`.

Everything else on the write path is unchanged: secret + signal gates, provenance, entity extraction. Valence is set on the built entry and never gates a write.

---

## Surfacing

- **Recall render** (`RecallToolHandler`): prepend a glyph — `Refuting → "✗ "`, `Cautionary → "⚠ "`, else none. `[I] ✗ Tried Npgsql pooling — deadlocks under load` reads as a dead-end.
- **Wake-up** (`GetContextAsync`): same glyph, plus a **bounded floor of ≤2 slots** reserved for negative-valence (`Refuting`/`Cautionary`) memories, carved from the Insight/Heuristic budget (never from L0/identity). If there are none, the budget reverts fully to the normal split — no wasted slots (unlike a per-type floor). L0 count header may gain a `… | N dead-ends` addendum.
- **Recall scoring untouched** — no valence term in `Fuse`, trust, or ROI.

---

## Caller Ergonomics (the common-case sugar)

`eidet_store` gains two optional inputs; **no new MCP tool** (respects the slim-6 surface):

| Input | Type | Behaviour |
|-------|------|-----------|
| `negative` | bool | Shorthand for the 95% case: sets `Valence = Refuting`, and when `type` is omitted defaults it to `Heuristic` (near-immortal, L1-visible — the right lifecycle for a dead-end). Importance defaults to `0.7`. Auto-adds the reserved `dead-end` tag. |
| `valence` | enum string (`neutral`\|`affirming`\|`refuting`\|`cautionary`) | Explicit stance for power users; overrides `negative`. Lets a caller mark a `Cautionary` `Procedure`, etc. |

`type` is dropped from `required` (only `content` stays required) so `{ content, negative: true }` is a legal one-line call.

```jsonc
// Session A, mid-task — ONE line, no type, no tags:
eidet_store({
  content: "Tried batching store writes via BulkInsert inside RunMutationAsync — corrupts the recall-cache generation bump, so recall returns stale results under concurrency. BulkInsert bypasses the MutationCtx finally-block invalidation.",
  negative: true })
// → Stored (valence=refuting, type auto=heuristic, tag=dead-end, importance=0.70)

// Session B, weeks later — at wake-up, before any work:
// [Memory: 214 entries, … | 3 dead-ends]
// [H] ✗ BulkInsert inside RunMutationAsync corrupts recall-cache invalidation — do NOT batch store writes there

// power user, same subject, opposite stance — both survive the dup-gate:
eidet_store({ type: "insight", content: "Recall cache works well as an in-memory generation-token map.", valence: "affirming", tags: ["recall-cache"] })
```

---

## MCP / REST Additions

- **MCP**: `eidet_store` schema gains `negative` + `valence`; `eidet_recall` gains an optional `valence` filter. No new tools (surface stays 8).
- **REST**: `POST /api/eidet` and `GET /api/eidet/search` accept the same optional fields. No new routes.

---

## Implementation Sketch

```
src/Eidet.Core/Domain/
├── Valence.cs                    # enum (Neutral=0, Affirming, Refuting, Cautionary)
└── MemoryEntry.cs                # + Valence Valence  (after Type)

src/Eidet.Core/Memory/
├── ValencePolarity.cs            # Conflicts / Merge — the ONLY sign logic
└── RecallScoring.cs              # MemorySearchResult.Valence copy; budgets untouched

src/Eidet.Core/Services/
├── MemoryServiceOptions.cs       # StoreOptions.Valence, RecallOptions.Valence
└── MemoryService.cs              # dup-gate guard (:87); GetContextAsync glyph + ≤2 floor

src/Eidet.Core/Gates/WriteValidator.cs        # BuildEntry / BuildEditEntry carry Valence
src/Eidet.Core/Maintenance/DedupEngine.cs     # MergeAsync conflict guard + Merge survivor
src/Eidet.Core/Maintenance/ConsolidationEngine.cs  # partition groups by sign
src/Eidet.Core/Storage/Memories_Search.cs     # index Valence for WhereEquals
src/Eidet.Core/Packs/MarkdownPackFormat.cs    # frontmatter round-trip (omit when Neutral)

src/Eidet.Service/Tools/Handlers/
├── StoreToolHandler.cs           # negative + valence inputs; type optional
└── RecallToolHandler.cs          # valence filter; ✗/⚠ glyph render
```

Tests:
- **Regression tripwire (the trap):** store an `Affirming` + `Refuting` near-duplicate pair → assert both survive the store dup-gate, survive dedup, and survive consolidation as two entries; assert a `Cautionary` note still folds normally.
- One-line sugar: `{ content, negative:true }` → stored as `Refuting`/`Heuristic`/`dead-end` tag.
- Wake-up floor: N dead-ends respect the ≤2 slot floor and never starve when there are zero.
- Backfill: a doc written before the field loads as `Neutral`.

---

## Phased Delivery

| Phase | Scope |
|-------|-------|
| **v1 (MVP)** | `Valence` enum + field + `ValencePolarity`; the **three write-path guards** (dup-gate, dedup, consolidation); `negative`/`valence` on `eidet_store`; `valence` recall filter; ✗/⚠ glyphs; index projection; pack round-trip. |
| **v1.1** | Wake-up ≤2-slot negative floor + L0 `dead-ends` addendum; Web UI stance badge + filter. |
| **v2** | Only on demand: `Deprecated` valence value (deprecation is the same machinery — one switch arm); analytics on dead-end recall hit-rate. |

---

## Resolved Design Decisions

| # | Decision |
|---|----------|
| 1 | **A dimension, not a 5th `MemoryType`.** Polarity is a stance any type can carry; a type flattens the valence×type matrix and forces a budget re-tune. |
| 2 | **`Neutral = 0`** → zero-migration backfill; enum-by-name serialization means future values never disturb stored data. |
| 3 | **One `ValencePolarity` helper** is the single home for sign logic; the three write choke points ask `Conflicts`/`Merge`, never compute signs. |
| 4 | **`Cautionary` is sign-0** — a warning does not contradict an affirming claim, so it still dedups/consolidates freely; only hard `Affirming`↔`Refuting` pairs are protected. |
| 5 | **Recall scoring untouched** — a dead-end is differently signed, not less important. |
| 6 | **`negative: true` sugar on `eidet_store`** (→ `Refuting`, default type `Heuristic`); no new MCP tool. Explicit `valence` for power users. |
| 7 | **Bounded ≤2-slot wake-up floor** for negatives (not a per-type floor) — surfaces a lone dead-end without wasting slots when there are none. |
| 8 | **Never `Observation` for a kept failure** — documented in the store-tool description and CLAUDE.md. |

---

## Designs Considered (and Rejected)

Four interface framings were explored in parallel via `/design-interface`. The chosen design takes the **Valence dimension (B)** for correctness + expressiveness, wrapped in the **common-case ergonomics (C)**.

| Framing | Gist | Verdict |
|---------|------|---------|
| **A — Minimize (convention)** | Canonical tag `negative-result` + store-tool steering; reuse Insight/Heuristic; no schema. | **Rejected (correctness).** Cannot hold the invariants — dedup + consolidation silently collapse polarity, shipping a data-loss bug. Its lasting contribution: proving the polarity guards are *mandatory in code* regardless of design, plus the "never store a failure as an Observation" rule (adopted as documentation). |
| **B — Valence dimension** | `enum Valence` orthogonal to type + one `ValencePolarity` helper threaded through all three choke points. | **Adopted (core).** Most expressive and general (add `Deprecated` in one switch arm); orthogonal to type; correctness centralized in one helper; zero migration. |
| **C — `negative:true` flag** | One bool → `IsDeadEnd`; auto-Heuristic; ⛔ marker; dedup guard only. | **Adopted (ergonomics only).** The 1-line caller surface is kept (as the `negative` sugar). Rejected as the *whole* design: binary can't carry `Cautionary` vs `Refuting`, auto-Heuristic mis-classes transient failures, and it left **2 of 3** collisions (store dup-gate, consolidation) unfixed. |
| **D — 5th `MemoryType` `Antipattern`** | A new type with its own decay curve, recall/L1 budget, consolidation target; dedup-safe by construction. | **Rejected (complexity + testability).** ~13 touch points incl. 2 silent-throw traps (`FadeMemCurve.Defaults[type]`, budget dicts) and a budget re-tune that regresses every other type's recall share (needs a gold-dataset re-run). Flattens the valence×type matrix (can't express a refuting Procedure vs a refuting Insight). Its one genuine win — a guaranteed L1 slot — is folded into B as the bounded ≤2-slot floor. |

---

## Risks

- **Unenforced invariant (correctness/consistency — the primary risk).** Valence is threaded through the choke points by *code discipline*, not enforced by the compiler. A future merge/group/dedup path that forgets `ValencePolarity.Conflicts` silently reintroduces the data-loss bug — the exact failure D is structurally immune to. **Mitigation:** the store dup-gate and `DedupEngine.MergeAsync` are the *only two* mutation entries today (single-write-funnel invariant); keep them so, and keep the regression tripwire test (Affirming + Refuting near-dup pair survives dedup **and** consolidation) as a standing guard.
- **ROI demotion of correctly-warning memories.** A `Refuting` memory stored as a `Heuristic` is outcome-negative by nature; `MemoryRoi.Factor` demotes Procedure/Heuristic with `FizzleCount > EchoCount`. Valence itself doesn't touch ROI (by decision #5), so a dead-end that keeps being *irrelevant* still fades correctly — but implementers must confirm ROI keys on recall-fizzles, not on the memory's polarity, so a *useful* warning isn't buried.
- **Sugar mis-classification.** `negative: true` defaulting to `Heuristic` makes a transient failure ("failed due to a typo") near-immortal. Bounded by the signal gate + ROI decay, not eliminated. Guidance: don't flag trivia; use explicit `type` for a failure worth only an Insight's lifespan.
- **Backfill semantics.** Every pre-existing memory is `Neutral`, so no historical failure is retroactively marked — acceptable; the feature is forward-looking.
