---
name: looseends
description: Prime on Eidet's Loose Ends before changing them — parked open work in its own looseends/* collection, LooseEndService's park/resolve verbs, resolution kinds (Done/Dropped/Promoted/Superseded), the claim-before-promote race protection, IPromotionPort, priority clamping, and the wake-up slice plus recall ride-along. Use when the task touches eidet_park/eidet_resolve, LooseEnd, promotion to a memory or an external issue, or open-work surfacing at session start. Not for memories or forget (see memory), not for pending knowledge awaiting review (see canon).
---

# Loose Ends — priming

**Canonical spec:** `docs/domains/looseends.md` — read it for the state machine, all invariants, key
files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Loose End lifecycle (and the
*Memory vs Loose End* / *Resolve vs Forget* / *Park vs Store* ambiguities). Threat: `STRIDE.md` T-10.

Open *work*, not knowledge. Own collection, so no maintenance stage can reach it — no decay, no
consolidation, no dedup, no TTL. It surfaces until an agent resolves it.

## Core invariants (get these right)

- **Park is secret-scanned, never signal-gated** — terse speculation is the feature.
- **`IPromotionPort` is the only edge back into the gated memory write funnel.** Park never reaches
  `StoreAsync`; promote only reaches it through the adapter.
- **Claim before you promote** (`Open → Resolving`, atomic in the store) or a retry double-mints.
- **A failed promote releases to a *clean* `Open`** — clear every staged resolution field, and release on
  `CancellationToken.None` so a cancelled resolve never wedges an end in `Resolving`.
- **Resolve is idempotent**; a lost claim distinguishes finished (success) / mid-flight (reject) /
  released (bounded retry).
- **Clamp priority to 1–3 at the park choke point** — it's the wake-up sort key, so unclamped values let
  any caller pin a note into every session's context (T-10).
- **A near-duplicate promote succeeds onto the existing memory**; an external-ref promote mints nothing
  and skips the gate.
- **Ordering lives in the store, rendering in the service** (pure, token-budgeted, `[~]` prefix).
- **Loose Ends never enter a Pack or Shared layer.**

## Key files / reuse

- `src/Eidet.Core/LooseEnds/LooseEndService.cs` — park, resolve, and all four surfacing verbs.
- `src/Eidet.Core/LooseEnds/{ILooseEndStore,IPromotionPort}.cs` — the seams.
- `src/Eidet.Core/LooseEnds/Promotion/MemoryServicePromotionAdapter.cs` — the gate-split enforcement point.
- `tests/Eidet.Core.Tests/LooseEnds/TestDoubles.cs` — reuse these doubles.

## Gotchas

- `MemoryService.LooseEnds` is a *settable property* (a ctor edge would be a construction cycle) — every
  host must assign it or the wake-up slice is silently empty.
- The ride-along fires only when the recall carries tags; there is no relevance model, just tag overlap.
- `Resolving` is an internal claim state with no user meaning — don't render it.
- The wake-up slice is item- *and* token-capped, and its budget is carved from L1, never L0.
- `Dropped` ≠ `Forget`: one closes open work, the other retires knowledge.
