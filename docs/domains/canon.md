# Canon

The human-approved subset of a repo's memories, structured as glossary and domain pages.

**Status:** P1 (Terms) shipped in [#75](https://github.com/stevehansen/eidet/issues/75)/[#76](https://github.com/stevehansen/eidet/issues/76);
P2 (Domains) and P3 (Portal convergence, OKF export) designed but not built.
Replaces the retired *CanonSpec.md* design spec.
**Priming skill:** [`.claude/skills/canon/SKILL.md`](../../.claude/skills/canon/SKILL.md)

## What it is

A propose → review → approve loop over syntheses of existing knowledge. A **Canon draft** is *not* a
memory: it lives in its own `canondrafts/*` collection until an Operator approves it, at which point it
is minted as a `canon:*`-tagged `Insight` through the full write gate. Approve is the **sole** write
edge into `memories/*` from here.

It is *not* the whole store (only the curated subset is Canon), *not* the Portal (**portal** renders a
live view; Canon is approved content), and *not* a Loose End (**looseends** — pending *work*, whereas a
draft is pending *knowledge*). REST and Web UI only: Canon has no MCP surface.

Only **Term** pages ship today. Two deterministic, zero-LLM sources produce them: entity aggregation
over the store, and a `UBIQUITOUS_LANGUAGE.md` seed parser.

## Core entities & relationships

```
ICanonDraftSource ×N ──proposes──> CanonDraftCandidate { Kind, Slug, Title, ProposedContent,
                                                        MemberIds, Fingerprint }
        │                                    │
        │                            secret scan, then the DAMPER
        ▼                                    ▼
CanonDraft   id = canondrafts/{repoId}/{kind}/{slug}      ← slug-keyed: ONE doc per (repo, kind, slug)
   Status: Pending → Approving → Approved | Rejected (+cooldown) | Superseded

CanonService (deep facade)
  ├─ ICanonDraftStore  — persistence + the atomic TryClaimForApproveAsync
  ├─ ICanonMintPort    — the ONLY edge into memories/*  (MemoryServiceCanonAdapter)
  ├─ ICanonDraftSource — the extension seam; adding a source never changes the service
  └─ IEidetStore       — read-only, for hydrating citations at GET time

Approved page: MemoryType.Insight, tagged canon:term:<slug> (or canon:domain:<slug>),
               DerivedFrom = the full member snapshot, Supersedes = the prior page when re-approved
```

## Invariants & rules

- **Approve is the only write path into `canon:*`**, and it always routes through `ICanonMintPort` →
  `MemoryService.StoreAsync`. That single enforcement point is what preserves the zero-LLM write-path
  invariant when synthesized prose eventually comes from a model.
- **Draft prose is secret-scanned at creation, and gated again at approve.** Defence in depth: a
  source's prose can echo a secret present in member content, so the candidate is dropped before it can
  sit in the review queue (**writepath** owns the gate).
- **The draft id is the damper anchor.** One document per `(repo, kind, slug)`, refreshed in place —
  which is what makes regeneration idempotent: identical fingerprint ⇒ nothing happens.
- **The fingerprint covers render fields *and* the ordered member set.** A changed fingerprint means the
  synthesis or its membership drifted; that is the only thing that reopens a rejected draft or queues a
  superseding draft over a live page.
- **A rejected draft needs *both* an elapsed cooldown and a changed fingerprint to re-propose.** Either
  alone leaves it rejected — otherwise a reviewer's "no" is undone by the next nightly run.
- **Regeneration never disturbs a draft mid-mint** (`Approving` is skipped by the damper).
- **Approve claims before it mints** (`Pending → Approving`), releases to a *clean* `Pending` on failure,
  and is idempotent — a second approve returns the existing minted id. Same protocol as
  **looseends**' resolve, including the bounded retry when a peer claimed-then-released and the
  `CancellationToken.None` release so a cancelled approve never wedges a draft.
- **Consolidation and dedup must never touch a `canon:*` page.** Both filter on `CanonTags.IsCanonPage`
  — a curated page carries a human's judgment and must not be folded into a machine insight
  (**maintenance**).
- **A page never re-tags itself by another page's tag.** The minted page carries its own `canon:*` tag
  plus the union of member tags with `canon:*` stripped out.
- **Minting inherits provenance from the surviving members** via the anti-laundering rule, so a page
  built over imported memories does not become fully trusted by being approved (**writepath**).
- **A forgotten member degrades, never throws.** Citation hydration renders a placeholder, minting skips
  the missing member, and `DerivedFrom` keeps the full original snapshot either way — a deleted member
  can neither 500 the review view nor block an approve.
- **A near-duplicate mint counts as success** onto the existing memory (mirrors loose-end promotion).

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Canon/CanonService.cs` | The whole reviewer loop, the damper matrix, claim protocol, DTO projection |
| `src/Eidet.Core/Canon/CanonDraft.cs` | The entity + both enums, with the `Approving`/`Superseded` semantics |
| `src/Eidet.Core/Canon/ICanonMintPort.cs` + `MemoryServiceCanonAdapter.cs` | The single write edge into `memories/*` |
| `src/Eidet.Core/Canon/ICanonDraftSource.cs` | The source seam (`AppliesTo` + async candidate stream) |
| `src/Eidet.Core/Canon/Sources/EntityAggregationDraftSource.cs` | Entity-clustered term drafts with deterministic definitions |
| `src/Eidet.Core/Canon/Sources/UbiquitousLanguageDraftSource.cs` | `UBIQUITOUS_LANGUAGE.md` table parser (authored terms, no members) |
| `src/Eidet.Core/Canon/{CanonFingerprint,CanonSlug,CanonDraftId,CanonTags}.cs` | Staleness, slugging, id shape, and the tag namespace + shared guard predicate |
| `src/Eidet.Core/Storage/RavenCanonDraftStore.cs` | Persistence + the atomic claim |
| `src/Eidet.Service/Api/Endpoints/CanonEndpoints.cs` | The REST surface (list / detail / approve / reject / regenerate / bulk-approve) |

## Gotchas

- **`IsStale` on a draft summary is hardcoded `false`.** Live fingerprint drift detection would re-run
  every source on every list; it is P2 work. Don't read the flag as authoritative.
- **`Superseded` is vestigial for slug-keyed drafts** — a newer candidate refreshes the one document in
  place. It is reserved for later phases, so a state machine drawn from the enum alone will be wrong.
- **`Approving` never appears in REST responses.** It exists purely for the claim window.
- **`RegenerateDraftsAsync` takes the repo's filesystem *path*, not a normalized id** — the id is derived
  from it, and the path is passed through verbatim so file-backed sources (the UL parser) can resolve.
  Passing a normalized id silently disables those sources.
- **The UL source deliberately skips the narrative sections** ("Example dialogue", "Flagged
  ambiguities") and any row whose term cell isn't bolded. A term that doesn't appear as a draft is
  usually a table-shape problem, not a bug.
- **The entity source excludes Observations** (session residue) and needs a minimum number of distinct
  citing memories before an entity is worth a page.
- **Canon has no MCP tool, on purpose.** Approval is an Operator act; the agent-facing surface stays
  slim.

## Executable references

- `tests/Eidet.Core.Tests/Canon/CanonServiceTests.cs` — **the authority on the reviewer loop and the
  damper matrix**: idempotent regeneration, fingerprint-driven refresh, the rejection cooldown +
  fingerprint requirement, the claim protocol (including release-to-clean-Pending and bounded retry),
  and idempotent approve.
- `tests/Eidet.Core.Tests/Canon/CanonGateIntegrationTests.cs` — settles that minting really traverses
  the write gate (secret/low-signal rejection surfaces as a failed approve) and the provenance
  inheritance.
- `tests/Eidet.Core.Tests/Canon/CanonDraftSourceTests.cs` — settles both sources' parsing/aggregation
  rules, including the skipped UL sections and the citation minimum.
- `tests/Eidet.Core.Tests/Canon/CanonCompanionEditTests.cs` — settles the companion invariants shipped
  with P1 (`DerivedFrom` carry-through, the `canon:*` guard in consolidation/dedup).
- `tests/Eidet.Core.Tests/Canon/TestDoubles.cs` — in-memory draft store / mint port; extend these.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Canon (Canon, Canon page, Domain, Term, Canon draft, Approve),
  plus the flagged ambiguities *Canon vs the memory store*, *Approve vs Promote*,
  *Domain (Canon) vs domain (DDD)*, *Canon draft vs Loose End*
- Related domains: **writepath** (the gate every mint passes; anti-laundering provenance) ·
  **maintenance** (must skip `canon:*`) · **looseends** (the claim protocol this mirrors) · **portal**
  (P3 will read `canon:*` for its Glossary/Domains sections) · **memory** (a Canon page *is* a Memory)
- Priming skill: `.claude/skills/canon/SKILL.md`
