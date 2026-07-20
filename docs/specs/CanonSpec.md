# Canon Spec: Curated Knowledge Base (Domains, Glossary, OKF Export)

> **Scope**: **Canon** — the human-approved subset of a repo's memories, structured as domain overviews and glossary terms, produced by a propose → review → approve curation loop, stored back as first-class memories, and (final phase) rendered as an [OKF v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf) bundle for humans and non-Eidet consumers.
>
> *Status: 📐 designed — grilled 2026-07-20, 9 decisions locked (§Resolved Design Decisions). Supersedes the KnowledgeBaseSpec.md sketch. Not implemented. P1 interface designed (§P1 Interface) → [#75](https://github.com/stevehansen/eidet/issues/75).*

---

## Problem Statement

- **Memories are entries, not a knowledge base.** Hundreds of memories recall well by query, but nothing organizes them into a browsable, structured corpus for humans or other agent ecosystems. The Portal covers the live-view case; nothing covers the curated, portable case.
- **Domain structure and terminology are latent.** Tags approximate domains; `Entities` approximate a glossary; `UBIQUITOUS_LANGUAGE.md` is authored by hand and disconnected from the store. Nobody synthesizes these into pages.
- **Synthesis that never lands in the store improves nothing.** Portal augmentation is ephemeral by design. A domain overview worth reading is worth recalling — it should *be* a memory.
- **OKF standardized the publication target.** Google's OKF v0.1 (June 2026): markdown + YAML frontmatter, only `type` required, `index.md` progressive disclosure, `log.md` history, links as graph edges, unknown frontmatter keys preserved. Eidet's pack format is 90% there; the missing 10% is concept-per-file structure — which is exactly what Canon pages are.

## The Canon Loop

```
1. Propose   CanonProposalStage (maintenance): TagOverlapGrouper clusters → Domain drafts;
             entity aggregation + UL.md seed → Term drafts; enrichment writes prose
             into canondrafts/*                                  (nightly, config-gated)
2. Review    Operator reads drafts in the Web UI Canon panel — prose with clickable
             citations into source memories
3. Approve   Draft graduates to a canon:* Memory through the full Write gate
             (the ONLY write path into canon:*)
4. Recall    Canon pages participate in normal recall/context — the store itself improves
5. Render    Portal sections and the OKF bundle read the same canon:* memories (deterministic)
```

Step 3→4 is the point: syntheses aren't a lossy report, they land in the store — recall gets the overviews and canonical definitions, and export becomes deterministic and cheap afterwards.

## Resolved Design Decisions (grill 2026-07-20)

| # | Decision | Accepted trap |
|---|----------|---------------|
| 1 | **Loop-first** — the curation loop is the deliverable; OKF export is a later renderer over the curated store | Value hinges on local-LLM draft quality; no early shippable artifact |
| 2 | **Canon pages are regular memories** — `type=Insight`, tags `canon:domain:<slug>` / `canon:term:<slug>`, `derivedFrom` = members. Consolidation/dedup skip `canon:*` (polarity-guard precedent) | Magic-tag special-casing; migrating to a real `Concept` type later costs a data migration |
| 3 | **Drafts live in a side collection** `canondrafts/*` (LooseEnd precedent) — LLM output never touches `memories/*`; Approve is the only write, through standard gates. Preserves the zero-LLM write path invariant | New plumbing (documents, endpoints, staleness, cleanup) for a possibly low-cadence workflow |
| 4 | **Live membership + snapshot citations** — domain membership = tag intersection with the domain page's own tags, evaluated at read time; the synthesis cites its `derivedFrom` snapshot; fingerprint drift queues re-proposal | Tag quality becomes load-bearing; needs a membership filter and cooldown to avoid churn |
| 5 | **`CanonProposalStage` in the maintenance pipeline** — config-gated + enrichment-gated; on-demand via `OnlyStages`. Damper: re-propose only if the fingerprint changed AND (cooldown elapsed OR member delta material) | Draft churn → review fatigue if the damper is mistuned |
| 6 | **Web UI review panel first** — "Canon" page in the SPA; REST endpoints underneath; CLI thin client later | Most complex SPA page yet; the queue rots if the Operator never opens `/ui` |
| 7 | **Approved provenance = anti-laundered `Consolidation`** — reuse `ConsolidationEngine.ProvenanceFor` (all-trusted members → `Consolidation`, else demote to least-trusted contributor); `Source="canon-review"`; one rule regardless of review edits | Rubber-stamped weak drafts are born fully trusted; review discipline is the safeguard |
| 8 | **UBIQUITOUS_LANGUAGE.md seeds Term drafts** via an intake-style extractor; bulk-approve promotes them. UL.md becomes a rendered *output* of the glossary later | Dual source of truth until the render-back ships |
| 9 | **Name: Canon** — `eidet canon ...`, `canon:*` tags, verb **Approve** (not Promote — reserved for Loose Ends). Gloss "Canon — curated knowledge base" on every user-facing surface | Coined term; discoverability suffers if the gloss slips |

## Data Model

### Canon page (approved)

A regular `MemoryEntry`: `Type=Insight`, `Tags` = [`canon:domain:<slug>` or `canon:term:<slug>`, …member-defining tags], `DerivedFrom` = cited member IDs, `Provenance` per decision 7, `Source="canon-review"`. Re-approval of a newer draft supersedes the prior page (normal version chain).

### Canon draft (`canondrafts/*` collection)

```
CanonDraft {
  Id, RepoId,
  Kind: Domain | Term,
  Slug, Title,
  ProposedContent,      // synthesized prose; citation-bearing
  MemberIds,            // the snapshot the synthesis cites
  Fingerprint,          // SHA256 over live member set + rendering fields (PortalSectionFingerprint shape)
  ProposedAt, CooldownUntil,
  Status: Pending | Approved | Rejected | Superseded,
  RejectReason?,
  SupersedesCanonId?    // set when re-proposing over an existing approved page
}
```

Lifecycle: `Pending` → **Approved** (memory written via full gates; supersedes prior canon page when set) | **Rejected** (reason + cooldown; no re-proposal until the fingerprint changes materially past cooldown) | **Superseded** (a newer draft for the same slug replaced it).

**Hallucination guard** (Portal rule): any synthesized sentence without ≥1 citation into `MemberIds` is dropped at draft time.

**Membership filter**: domain membership counts Insights, Procedures, Heuristics with open validity; Observations excluded (session residue — AGENTS.md-export precedent). *(C. call, veto-able.)*

## Domain & Term Pages

**Domain** (`canon:domain:<slug>`): the page's own tags (minus `canon:*`) define the member set by intersection — authored in one queryable place, no config file. Body = synthesized overview + grouped member list. Seeded by `TagOverlapGrouper` cluster proposals; enrichment names the cluster.

**Term** (`canon:term:<slug>`): one glossary entry (entity or authored term). Source precedence:
1. Approved `canon:term` page (this feature's output).
2. Enrichment-synthesized definition from all memories citing the entity (draft path).
3. Deterministic fallback: OneLiner of the highest-importance citing memory (Portal Glossary rule) — the LLM-free degraded mode.

Term drafts come from entity aggregation plus the UL.md seed extractor. Entity/alias normalization (casing, plural folding) is open question 1.

## Surfaces

| Surface | Shape |
|---------|-------|
| Maintenance | `CanonProposalStage` (new `IMaintenanceStage`), config-gated, enrichment-gated; on-demand via `OnlyStages` |
| REST | `GET /api/eidet/canon/drafts?repo=…`, `GET /api/eidet/canon/drafts/{id}`, `POST …/{id}/approve` (optional edited content), `POST …/{id}/reject` (reason). Register before the `EidetApi.cs` catch-all |
| Web UI | "Canon" nav page: draft queue → prose view with clickable citations (`#memory/<id>` route exists) → inline edit → approve/reject |
| CLI | Later: `eidet canon review` thin client over the same endpoints |
| MCP | None (pack-export/Portal precedent — Canon is an Operator surface) |

New `IEnrichmentPort` ops: `NameCluster`, `SynthesizeDomain`, `DefineTerm` (prompts adapter-internal; `InMemoryEnrichmentAdapter` fakes them for tests).

## Portal Convergence

Portal Glossary (v1.1) gains `canon:term:*` as its first-precedence source; a Domains view can augment the Architecture section's tag-cluster grouping. Portal and Canon render the same memories — no drift by construction.

## OKF Export (final phase)

The bundle renders deterministically from `canon:*` + evidence memories:

```
<repo>-canon/
├── index.md                      # root TOC (OKF grouped-list format, no frontmatter)
├── log.md                        # newest-first ISO-date headings from CreatedAt + supersessions
├── domains/<slug>.md             # type: Domain — approved canon pages
├── glossary/<term-slug>.md       # type: Term
├── procedures/<slug>-<hash>.md   # type: Procedure — 1:1 memory → doc
├── heuristics/<slug>-<hash>.md   # type: Heuristic (valence surfaced: ✗ dead-ends, ⚠ cautions)
└── insights/<slug>-<hash>.md     # type: Insight — evidence layer; citation targets
```

Frontmatter: OKF-required `type`; recommended `title` (OneLiner), `description` (Summary), `resource` (`eidet://memories/…` URI back to the live memory), `tags`, `timestamp` (CreatedAt, ISO 8601). Eidet metadata rides in an `eidet:` extension block (importance, confidence, valence, stage, provenance, id) — OKF consumers must preserve unknown keys. Body = Content verbatim + `# Citations` from `derivedFrom`. Entity mentions link to `/glossary/<term>.md` when the term page exists; cross-repo links emit as `eidet://` URIs (OKF tolerates unresolvable references).

Observations stay out by default (flag to include). File naming `<slugified-oneliner>-<12-char-hash>.md`. Delivery: CLI writes a directory (`eidet canon export`); REST zip only on demand. Degraded LLM-free mode: evidence layer + fallback glossary, domains as member lists without prose.

Future: an `OkfBundleExtractor` intake path (known `type` → `MemoryType`, unknown → insight tagged `okf:<type>`) to consume third-party OKF bundles as layers.

## Assumptions (not grilled — veto by editing this section)

- **Per-repo scope**, like the Portal; "Atlas" stays reserved for the cross-repo aggregate.
- **ReflectionEngine is prior art**: reuse its anti-laundering, dry-run, and proposal-minting patterns where they fit. Canon does **not** reuse Reflection's unreviewed minting — the human gate is the feature.
- **Terms ship before Domains** (P1 vs P2): UL-seeded terms give immediate content with minimal LLM risk while the review panel proves out.

## Phasing

| Phase | Scope |
|-------|-------|
| **P1 — Terms** | `canondrafts/*` collection, UL.md seed extractor, entity-aggregation term drafts (deterministic fallback definitions), Web UI review panel, approve/reject endpoints, `canon:*` write path + consolidation/dedup guard |
| **P2 — Domains** | `TagOverlapGrouper` clustering, `NameCluster`/`SynthesizeDomain` enrichment ops, fingerprint staleness + cooldown damper, `CanonProposalStage` wiring |
| **P3 — Convergence** | Portal Glossary/Domains read `canon:*`; echo/fizzle handling for canon pages (fizzle → re-review draft?) |
| **P4 — OKF** | Bundle export (CLI directory), `eidet://` link emission; OKF import extractor + REST zip on explicit demand |

## P1 Interface (designed 2026-07-20)

Chosen from a 4-way design fan-out (minimal / flexible / common-case / ports-and-adapters); full interface, testing strategy, and rejected designs in [#75](https://github.com/stevehansen/eidet/issues/75). Shape (`Eidet.Core.Canon`):

```csharp
// CanonDraft: id "canondrafts/{repo}/{kind}/{slug}" (slug = damper anchor);
// Status adds transient Approving (claim state, LooseEnd Resolving twin)
public interface ICanonDraftStore { /* Store/Get/Update/List/FindBySlug + atomic TryClaimForApproveAsync */ }
public interface ICanonMintPort  { /* MintAsync(draft, editedContent) — SOLE gated edge (IPromotionPort twin) */ }
public interface ICanonDraftSource {                       // P2/P3 extension seam; orchestrator never changes
    string Name { get; }
    bool AppliesTo(CanonProposalContext ctx);
    IAsyncEnumerable<CanonDraftCandidate> ProposeAsync(CanonProposalContext ctx, CancellationToken ct = default);
}
public sealed class CanonService(ICanonDraftStore, ICanonMintPort, IEnumerable<ICanonDraftSource>, TimeProvider) {
    // 80% reviewer loop: ListPendingAsync / GetDraftAsync (hydrated Citations) / ApproveAsync / RejectAsync
    // 20%: RegenerateDraftsAsync (damped, idempotent; P2 stage body) / BulkApproveAsync (UL-seeded terms)
}
```

P1 sources: `EntityAggregationDraftSource` (over `IEidetStore`), `UbiquitousLanguageDraftSource` (UL.md) — sources own their reads (no service-level read ports). Companion edits ride the same PR: `StoreOptions.DerivedFrom` (+ `WriteValidator.BuildEntry` carry), `ProvenanceFor` → public `ProvenanceRules.ForContributors`, `canon:*` candidate-set guard in consolidation/dedup, `CanonEndpoints` before the `EidetApi.cs` catch-all, STRIDE.md notes below.

## Risks (STRIDE ride-along notes for the implementation PR)

- **Rubber-stamp laundering**: LLM prose entering the trusted store via skim-approval (decision 7's accepted trap). Mitigations: hallucination guard at draft time, anti-laundering provenance, citations rendered inline in the review UI.
- **`canondrafts/*` is a new agent-context-adjacent surface**: draft prose is untrusted content and must be render-trust-gated in the Web UI (loose-ends T-10 lesson).
- **Secret scan at both ends**: standard gates run at Approve; the scan should also run at draft creation (enrichment output can echo a secret present in member content — defense in depth, matches intake-pipeline precedent).

`STRIDE.md` updates ride in the implementation PR, per repo policy.

## Open Questions

1. Entity/alias normalization for terms (casing, plural folding) — needed before term slugs are stable.
2. Fizzle on a canon page: auto-queue a re-review draft, or decay normally?
3. Damper thresholds (cooldown days / member delta) — pick starting values, tune after real use.
4. REST zip vs CLI-only for OKF export (deferred to P4).
