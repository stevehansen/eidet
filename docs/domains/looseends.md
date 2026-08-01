# Loose Ends

Open work an agent parks mid-task — a sibling of Memory, not a kind of one.

**Status:** current as of [#77](https://github.com/stevehansen/eidet/issues/77) (priority clamp,
STRIDE T-10) · **Governing issues:**
[#42](https://github.com/stevehansen/eidet/issues/42) (park/resolve v1),
[#46](https://github.com/stevehansen/eidet/issues/46)/[#48](https://github.com/stevehansen/eidet/issues/48)
(atomic promote via claim-before-promote), [#77](https://github.com/stevehansen/eidet/issues/77).
Replaces the retired *LooseEndSpec.md* design spec.
**Priming skill:** [`.claude/skills/looseends/SKILL.md`](../../.claude/skills/looseends/SKILL.md)

## What it is

A deferred, still-actionable note: a suspected bug, out-of-scope work, a thread to pull later. Parking
is deliberately low-friction — terse and speculative phrasing is the *point*, so the signal gate does
not apply. A Loose End keeps surfacing until an agent explicitly resolves it; nothing closes it
automatically.

It is **not** a Memory (knowledge vs open work — see the glossary's flagged ambiguity), not a work
tracker (no assignees, no due dates, no sprints), and not a Canon draft (**canon** — pending
*knowledge* awaiting review, rather than pending *work*). Deliberately: `Resolve` is not `Forget`.

## Core entities & relationships

```
LooseEnd  (own collection: looseends/{repoId}/{shortHash}; Local layer only, no LayerId)
  State: Open → Resolving → Resolved         // Resolving is an internal claim, not a user-facing state
  Resolution: Done | Dropped | Promoted | Superseded
  Priority: 1 high / 2 normal / 3 low        // wake-up ordering only; never decays

LooseEndService (deep facade over three seams)
  ├─ ILooseEndStore      — persistence + ordering + the atomic TryClaimForResolveAsync
  ├─ IPromotionPort      — the ONLY edge back into the gated memory write funnel
  └─ TimeProvider        — so park/resolve timestamps are assertable

Surfaces: RenderWakeupSliceAsync (session start) · RideAlongAsync (tag overlap on recall) ·
          PullAsync (REST/UI list) · CountOpenAsync (the L0 addendum)
```

Because Loose Ends live in their own collection, **no maintenance stage can reach them** — no FadeMem
decay, no consolidation, no dedup, no retention, no TTL expiry. That exemption is structural, not a
set of opt-outs to remember.

## Invariants & rules

- **Park is secret-scanned but never signal-gated.** Terse speculation is the feature; credentials are
  still never stored. `SecretScanRule` runs at the single park choke point.
- **`IPromotionPort` is the only path from a Loose End into a Memory.** Park never reaches
  `MemoryService.StoreAsync`; promote only ever reaches it through the adapter. That split is what keeps
  the two gates honestly different.
- **Resolve claims before it promotes.** `Open → Resolving` is an atomic store-level claim, so a
  concurrent or retried resolve can never both pass the `Open` check and double-mint. Exactly one caller
  wins.
- **A failed promote releases the claim back to a *clean* `Open`.** Every staged resolution field is
  cleared, so a released end is indistinguishable from a never-resolved one (a stranded minted memory is
  absorbed by the write-path duplicate gate on retry). The release runs on `CancellationToken.None` —
  it compensates a side effect already committed, so a cancelled resolve must never leave an end wedged
  in `Resolving`.
- **Resolve is idempotent.** Re-resolving a `Resolved` end returns its current state and never re-mints.
  A lost claim distinguishes three cases: peer finished (idempotent success), peer mid-flight (reject),
  peer released back to `Open` (bounded retry).
- **Priority is clamped to 1–3 at the park choke point.** Priority is the wake-up sort key, so an
  unclamped value lets any write-capable caller pin its note to the top of every session's agent
  context (STRIDE T-10). Clamped in the service, not per surface.
- **Promotion onto an existing near-duplicate memory counts as success.** The knowledge already exists;
  stranding the end open and dropping the duplicate id would be worse.
- **A promote with an external ref mints nothing** and does not enter the memory gate — the end closes
  as linked to the issue. Whitespace-only refs are treated as absent.
- **Ordering lives in the store, rendering in the service.** `ListOpenAsync` returns already-ordered,
  already-capped results (priority, then oldest-first within a tier); `RenderSlice` is pure,
  token-budgeted rendering with the `[~]` open-work prefix.
- **Loose Ends never enter a Pack or a Shared layer.** They are local, per-repo, and not shareable
  knowledge.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/LooseEnds/LooseEnd.cs` | The entity, both enums, and the field-level intent comments |
| `src/Eidet.Core/LooseEnds/LooseEndService.cs` | Park, resolve/claim/release, all four surfacing verbs, `ParkOptions`/`ResolveOptions`/results |
| `src/Eidet.Core/LooseEnds/ILooseEndStore.cs` | Persistence seam, incl. `TryClaimForResolveAsync` and the ordering contract |
| `src/Eidet.Core/LooseEnds/IPromotionPort.cs` | The promote seam (`PromoteOptions` / `PromotionResult`) |
| `src/Eidet.Core/LooseEnds/Promotion/MemoryServicePromotionAdapter.cs` | The prod adapter — the single gate-split enforcement point |
| `src/Eidet.Core/LooseEnds/LooseEndIdGenerator.cs` | `looseends/{repoId}/{shortHash}` |
| `src/Eidet.Core/Storage/RavenLooseEndStore.cs` | The RavenDB implementation (claim + ordered queries) |
| `src/Eidet.Service/Tools/Handlers/{Park,Resolve}ToolHandler.cs` | `eidet_park` / `eidet_resolve` — the two MCP tools |
| `src/Eidet.Service/Tools/Handlers/RecallToolHandler.cs` | Where the ride-along is attached to a recall |
| `src/Eidet.Service/Api/Endpoints/LooseEndEndpoints.cs` | REST surface |

## Gotchas

- **`MemoryService.LooseEnds` is a settable property, not a constructor dependency** — the promotion
  adapter wraps `MemoryService`, so a ctor edge would be a construction cycle. Every host that wants
  the wake-up slice must assign it (`EidetHost`, `ContextCommand`, `McpCommand`, and the integration
  fixture all do). Forget it and the slice is silently empty — the null case is a legitimate no-op.
- **The ride-along only fires when the recall carries tags.** A tagless recall never surfaces open work,
  by design (there is no relevance model for Loose Ends — only tag overlap).
- **`Resolving` is invisible to callers.** It exists for the claim; a UI that renders raw states will
  show a flicker state that has no user meaning.
- **The wake-up slice is item-capped *and* token-capped**, and its sub-budget is carved from L1 — never
  from L0. A long note simply doesn't render.
- **`Dropped` is not `Forget`.** Both retire something, but one closes open work and the other retires
  knowledge; conflating them makes the quality dashboard count a finished todo as an expired memory.

## Executable references

- `tests/Eidet.Core.Tests/LooseEnds/LooseEndServiceTests.cs` — **the authority on this domain**
  end-to-end: park gating (secret rejected, low-signal accepted), priority clamping, the wake-up slice's
  cap/prefix/ordering/token budget, tag ride-along, idempotent resolve, and every claim race —
  claim-before-promote, release-to-clean-Open on a failed or throwing promote, and the bounded retry.
- `tests/Eidet.Core.Tests/LooseEnds/TestDoubles.cs` — the in-memory store/promotion doubles; extend
  these rather than mocking the seams ad hoc.
- `tests/Eidet.Service.Tests/Tools/{Park,Resolve}ToolHandlerTests.cs` — settle the MCP argument surface
  and error shapes.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Loose End lifecycle, plus the flagged ambiguities
  *Memory vs Loose End*, *Resolve vs Forget/TTL expiry/Supersession*, *Park vs Store*,
  *Promote vs Supersession*, *Todo/Task*
- Threat model: `STRIDE.md` T-10 (priority injection into agent context)
- Related domains: **memory** (what a promote mints) · **writepath** (the gate promote re-enters) ·
  **recall** (the wake-up slice and the ride-along attach points) · **maintenance** (structurally cannot
  touch Loose Ends; mines resolved ones as reflection residue) · **canon** (pending knowledge, not
  pending work)
- Priming skill: `.claude/skills/looseends/SKILL.md`
