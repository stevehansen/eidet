# Portal Spec: Per-Repo State Portal (Web UI View)

> **Scope**: This spec defines a generated, audited per-repo *state view* rendered in the Eidet Web UI — a single front door that describes a repo as its memories see it, with every claim traceable back to its source memory. It is a live view served by the existing service; it is **not** a file on disk.
>
> *Status: ✅ implemented — `Eidet.Core/Portal/*` (renderer + sections) + `Api/Endpoints/PortalEndpoint.cs` + Web UI hash route. No `eidet_portal` MCP tool (deferred by design, see below).*

---

## Problem Statement

- **Memories don't read as a story**: 100+ memories per active repo retrieve well by query, but there is no coherent "what is this repo right now" page anyone can read top-to-bottom.
- **The Web UI today is exploratory**: search, browse, graph — all great for hunting. Nothing answers "give me the current synthesis."
- **`eidet_context` is for machines**: <600 tokens, dense-packed, optimized for session-start injection. Unreadable as a UI page.
- **Cross-repo edges are buried**: the `links` data exists; nothing surfaces it as a per-repo neighborhood narrative.
- **No audit trail for synthesis**: the moment we synthesize multiple memories into a paragraph, the paragraph's claims have no provenance. Every claim needs a one-click path back to its source.

## Goals

1. **One Web UI page per repo** that fully describes the repo as Eidet sees it — sectioned, navigable, scannable.
2. **Live**: rendered from the current memory state on each view; no caching layer that can drift.
3. **Audited**: every non-trivial sentence is a clickable link into the existing memory detail view.
4. **Augmented (optional)**: Ollama may turn raw memory bullets into prose, group related items, surface themes. Augmentation is opt-in via a control on the page itself, and the un-augmented form must always work.
5. **Cross-repo aware**: shows this repo's neighbors as a small interactive graph plus a list of edges with citations.
6. **Read-only**: corrections flow back as memory writes (edit / forget / counter-observation), not as page edits. The page links to the actions.

## Non-Goals

- **Not a markdown file on disk.** No `PORTAL.md` checked into the repo. No drift detection. No CI lint. No gitattributes guidance.
- **Not a replacement for `eidet_context`** — the portal targets a human or an agent doing deeper exploration, not a session-start injection.
- **Not editable as a wiki.** The only write path is through memory tools.
- **Not a public-facing page.** Local Web UI only; same auth posture as the rest of `/ui`.

---

## Codebase Constraints

The Portal v1 design is constrained by the current state of the codebase. These are the load-bearing facts the rest of this spec assumes; if any of them changes, revisit the relevant section.

| Concern | Current state | Impact on Portal |
|---------|---------------|------------------|
| Web UI routing | Hash-routed SPA (`app.js:34`); known routes: `dashboard`, `browser`, `graph`, `timeline`, `usage`, `settings`. No path routing. | Portal lives at `/ui#portal/<repo>`, not `/ui/portal/<repo>`. Citations require a new `/ui#memory/<id>` hash route. |
| API route ordering | `EidetApi.cs:168` registers `MapPrefix("GET", "/api/eidet/", _memoryRead.GetMemory)` as a catch-all memory-by-id route. | All `/api/eidet/portal*` routes MUST register before that line, alongside the existing exact routes near `EidetApi.cs:125–139`. |
| Memory timestamps | `MemoryEntry` has `CreatedAt` and nullable `LastAccessedAt`; no `UpdatedAt`/`LastModifiedAt`. | "Last modified" / "stale by inactivity" are not derivable today. Health section uses `CreatedAt` only in v1. |
| Graph endpoint scope | `/api/eidet/graph` is intra-repo (one repo's memory→memory edges). | Portal does **not** reuse it for cross-repo neighborhood; ships dedicated `/api/eidet/portal/neighborhood` endpoint. |
| Cross-repo links | Stored as `LinksOut` IDs on tagged insight memories; no repo-neighborhood index. | v1 neighborhood endpoint scans via `Browse` + filter; index is a follow-up if perf demands it. |
| Auth/ACL | Coarse `read:all`/`write:all`; `/ui` is public; no per-repo ACL. | v1 cross-repo section surfaces only repo IDs + edge labels, never neighbor memory content. Neighbor summaries return when layer ACLs ship. |
| Identity content source | `eidet_context` L0 is a count header, not a paragraph. | Identity section composes from a documented precedence list (curated identity memory → top insights → intake → count fallback). |

---

## Naming

User-facing term: **Portal**. URL: **`/ui#portal/<url-encoded-repo-id>`** — the existing Web UI is a hash-routed SPA (`app.js` reads `location.hash.slice(1)`), so the Portal page must register as a hash route, not a path route. Top-level Web UI nav entry "Portal" (alongside Dashboard, Browser, Graph). "Atlas" is reserved for a possible future cross-repo aggregate view.

Memory citations target **`/ui#memory/<url-encoded-id>`**, which does **not exist today** — v1 must add this hash route to the SPA. Implementation: extend `showPage()` to recognize `portal/<repo>` and `memory/<id>` patterns (split on `/`); the latter routes into the existing Browser page and calls its existing `showDetail(id)` after load. Without this addition, citations have no working target.

---

## Relationship to Existing Artifacts

| Artifact | Author | Audience | Lifecycle | Style |
|----------|--------|----------|-----------|-------|
| `README.md` (in repo) | Human | Anyone | Hand-edited | Marketing / quickstart |
| `CLAUDE.md` (in repo) | Human | AI agent | Hand-edited | Prescriptive, opinionated |
| `eidet_context` (in-memory blob) | Eidet | AI agent | Per session, ephemeral | Dense, ~600 tokens |
| **Portal (`/ui/portal/<repo>`)** | **Eidet (live render)** | **Human in the UI; future: agent via API** | **Recomputed on every view** | **Descriptive, audited, navigable** |
| Web UI Search / Browse / Graph | Eidet | Human | Live, interactive | Exploratory |

Portal sits *next to* Search / Browse / Graph in the Web UI. It does not touch the filesystem.

---

## Page Structure

A single scrollable page with anchored sections, a sticky table of contents on the left, and section-level controls (collapse, "show provenance," refresh).

```
┌──────────────────────────────────────────────────────────────────────┐
│  /ui/portal/P--Eidet                            [augment: summary ▾] │
├──────────┬───────────────────────────────────────────────────────────┤
│ TOC      │  § Identity                                               │
│  Identity│  One paragraph "what is this repo." Each claim links to   │
│  Arch.   │  its source memory.                                       │
│  How To  │                                                           │
│  Rules   │  § Architecture                                           │
│  Recent  │  Bulleted insights, grouped by tag-cluster. Each bullet   │
│  Linked  │  is a link to the memory detail view.                     │
│  Glossary│                                                           │
│  Health  │  § How To (Procedures)                                    │
│  ...     │  Numbered recipes from procedure-typed memories.          │
│          │                                                           │
│          │  § Rules of Thumb (Heuristics)                            │
│          │                                                           │
│          │  § Recent Activity                                        │
│          │  Last N observations with timestamps. Stale ones marked.  │
│          │                                                           │
│          │  § Linked Repos                                           │
│          │  Mermaid/canvas graph centred on this repo, one hop.      │
│          │                                                           │
│          │  § Glossary / Entities                                    │
│          │                                                           │
│          │  § Memory Health                                          │
│          │  Counts, freshness histogram, last consolidation, quality.│
└──────────┴───────────────────────────────────────────────────────────┘
```

**Identity and Health are always present.** Every other section is rendered conditionally — if a section has no content, it is omitted entirely from both the body and TOC rather than shown as "(none)."

**Identity content** is composed by the following precedence (first match wins, then stop):
1. A memory tagged `portal:identity` if one exists for the repo (lets users curate the paragraph).
2. The top 3 insights by importance score, concatenated with their OneLiners.
3. Intake-derived content from README.md / CLAUDE.md if a recent intake ran (memories with `provenance=intake` and a known source-file tag).
4. Fallback: a count-only stub (e.g., "Eidet sees 47 insights, 3 procedures, and 2 heuristics for this repo. Run `eidet intake` to seed an identity paragraph.").

The current `eidet_context` L0 is just a count header (`[Memory: N entries, ...]`) — it does **not** produce a usable identity paragraph and is not a source for this section.

**Health content** in v1 covers only what the existing data model can support:
- Counts by type (already in `Stats` API).
- Quality score (already in `/api/eidet/quality`).
- Freshness histogram bucketed by `CreatedAt` (the only timestamp every memory has).

**Excluded from v1 Health**: "last modified," "last consolidation per repo," and "stale items" requiring an `UpdatedAt`/`LastModifiedAt` field. `MemoryEntry` today has only `CreatedAt` and nullable `LastAccessedAt` — neither tracks edits. Scheduled-task state is global, not per-repo. Restoring those fields is follow-up work tracked separately (see §Codebase Constraints).

**Liveness model**: templates are rendered live on every page view; only Ollama-augmented prose is cached. See §Augmentation for the cache key.

---

## Citations

Every non-trivial sentence carries a hover-revealable citation:

- Inline: claim text is a link to `/ui#memory/<id>` (existing memory detail view).
- Hovering shows a tooltip with the memory's type, importance, last-touched, and OneLiner. Tooltips fetch fresh on hover so they never lag behind edits.
- Sections have a "show provenance" toggle that expands every claim with its memory ID, importance, and a small inline preview.
- Toggle defaults to **collapsed** (clean prose first impression). The user's preference is remembered in localStorage so power users get expanded automatically after their first toggle.

This replaces the markdown-comment scheme from the previous draft — in a UI we can use real hyperlinks and hovers instead of HTML comments meant for grep.

---

## Augmentation Levels

The portal must be useful without Ollama. Three levels, switched by a control in the page header:

| Level | Source | Description | Requires |
|-------|--------|-------------|----------|
| `off` | Memory `OneLiner`, verbatim | Pure templated rendering. Each bullet is exactly the OneLiner. | Always works |
| `summary` *(default if Ollama available)* | Tag-clustered groups → 2–3 sentence prose paragraph per cluster | Ollama generates per-section overview prose; bullets remain verbatim underneath as evidence | Ollama running |
| `narrative` *(opt-in)* | Whole-section LLM rewrite citing source memories | Section bodies become flowing prose with hyperlinked citations | Ollama + explicit toggle |

**Hallucination guard**: in `summary` and `narrative` modes, the renderer drops any sentence that does not contain at least one citation linking to a memory the section was given as input. Better to omit a sentence than print one without provenance.

**Caching scope**: only Ollama-augmented prose is cached — templated section rendering runs live on every view. Cached entries are keyed on `(repo, section, augment_level, PortalSectionFingerprint)` and stored in RavenDB. The fingerprint is a SHA256 over the section's *current input set*, not over the prior render's citations — otherwise a newly-eligible memory (just stored, not yet cited) would not invalidate the cache.

**`PortalSectionFingerprint`** for a given section is the SHA256 of a deterministic JSON serialization of:
- The ordered list of memory IDs the section's selection rules return *right now* (see §Off-Mode Section Selection Rules).
- For each ID, a tuple of every field that can affect rendering: `Type`, `Content`, `OneLiner`, `Summary`, `ForesightHint`, `Tags` (sorted), `Importance`, `Confidence`, `Entities` (sorted), `ValidityEnd` (forget marker), `LinksOut` IDs (sorted).

Hash inputs are read in a single RavenDB session per render to avoid race-induced inconsistency. Computing the fingerprint requires only the existing `Browse` / `GetByIds` reader paths; no new index. If profiling after v1 shows template rendering itself is too slow on large repos, full-section caching is a strict superset that can be added later without changing the key structure.

**Phased delivery**: `off` ships in v1, `summary` in v1.1, `narrative` in v1.2 once the citation filter has been battle-tested on `summary` output.

---

## Off-Mode Section Selection Rules

Off-mode rendering is purely templated: each section runs a deterministic query, takes a deterministic ordering, and emits OneLiners verbatim. Determinism matters because the cache fingerprint depends on it — two renders of the same memory state must select the same memory IDs in the same order.

| Section | Selection | Ordering | Cap |
|---------|-----------|----------|-----|
| Identity | Per the precedence list in §Page Structure. | Insights ordered by `Importance` desc, then `Id` asc as tiebreak. | Top 3 insights (when option 2 of the precedence list applies). |
| Architecture | All `Insight`-typed memories with `ValidityEnd == null`. | `Importance` desc, then `Id` asc. Grouped by primary tag (the alphabetically-first tag); groups ordered by max-Importance member desc. | None — all insights surface. |
| Procedures | All `Procedure`-typed memories with `ValidityEnd == null`. | `Importance` desc, then `Id` asc. | None. |
| Heuristics | All `Heuristic`-typed memories with `ValidityEnd == null`. | `Importance` desc, then `Id` asc. | None. |
| Recent Activity (v1.1) | All `Observation`-typed memories with `ValidityEnd == null`. | `CreatedAt` desc, then `Id` asc. | Top 20. |
| Linked Repos (v1.1) | Memories with at least one `LinksOut` whose target's normalized repo prefix differs from the center repo's. | Group by target repo; within group order by `Importance` desc. Groups ordered by `edge_count` desc. | None. |
| Glossary (v1.1) | All `Entities` extracted across all `ValidityEnd == null` memories. | Entity name asc; for each entity, definition is the OneLiner of the highest-Importance memory citing it. | None. |
| Health | N/A — derived counts, not memory selection. | — | — |

These rules are also the input to `PortalSectionFingerprint` — the cache key sees exactly the IDs the section will render.

---

## Cross-Repo Section

`/api/eidet/graph` is **not** suitable as-is — it's an intra-repo memory graph (browses memories for one repo and emits derived/same-repo target-memory edges). Cross-repo links created by `eidet_link` live as tagged insight memories with `LinksOut` entries pointing to other repos' memory IDs; they are not surfaced as repo-neighborhood edges anywhere today.

v1 adds a dedicated **repo-neighborhood projection**:

- New endpoint **`GET /api/eidet/portal/neighborhood?repo=...`** returns `{ center: <repo>, neighbors: [{ repo, edge_count, citing_memory_ids: [...], edge_labels: [...], last_link_created_at }] }`.
- Implementation: query memories with `LinksOut` entries whose target ID's normalized-repo-prefix differs from the center repo's. Group by target repo. No new index strictly required for v1 — a `Browse` + filter is acceptable for typical link counts; if it gets slow, a `Memories_Links` index over `LinksOut` is the follow-up.
- The §Linked Repos section in the Portal page consumes this endpoint, **not** `/api/eidet/graph`.

Section content:
- Centred-on-this-repo graph, **one hop out**, rendered with the same canvas/d3 infrastructure as the existing graph view (presentation layer can be shared even though the data source differs).
- Below the graph: a table of edges. For each linked repo: edge labels, citing memory IDs (linkified), last-link-created timestamp.
- A small "explore further in Graph view →" link sends users to the existing N-hop interactive Graph view for deeper transitive exploration.

**Excluded from v1**: pulling a one-line summary from a neighbor repo's content. The current auth model is coarse (`read:all`/`write:all`) and `/ui` is public; embedding neighbor memory text in this repo's Portal would leak content across whatever ACL boundary later layers introduce. v1 surfaces only repo IDs, edge labels, and citing memory IDs — all of which are already addressable on their own. Neighbor summaries return when layer-level ACLs ship.

The Portal answers a narrow question — "what does *this* repo connect to, and why?" — and hands off to Graph view for everything broader. This is the section that's genuinely impossible to do in `eidet_context`.

---

## "Editing" the Portal

The portal is read-only. To correct a claim, the user clicks its citation, lands in the memory detail view, and from there:

- **Edit** (PUT `/api/eidet/{id}` — already exists).
- **Forget** with reason (already exists).
- **Add a counter-observation** (`POST /api/eidet`).

After any of those, the portal page picks up the change on the next render (or immediately, when the cache invalidation hooks fire).

For convenience, each claim's hover-card includes an inline **"add counter-observation"** action — the lowest-stakes, additive correction path — so a reader spotting a wrong claim can refute it without leaving the page. **`forget` and `edit` both navigate to the memory detail view**: forget destroys (soft-deletes) and warrants the gravitas of a separate page with a required reason; edit needs the heavier editor surface anyway. Friction is a feature for destructive actions.

---

## API Additions

The Web UI is an SPA over REST; the new endpoints follow existing conventions.

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/eidet/portal?repo=...&augment=off\|summary\|narrative` | Returns the rendered portal as JSON: `{ sections: [{ id, title, html, citations: [{ memory_id, anchor }] }], stats }`. Web UI consumes this. |
| GET | `/api/eidet/portal/sections/<section-id>?repo=...&augment=...` | Single-section render — used to lazy-load or refresh just one section after an inline edit. |
| GET | `/api/eidet/portal/state-hash?repo=...` | Cheap cache-validation — returns the current memory_state_hash for the repo so the SPA can decide whether to refetch. |

| GET | `/api/eidet/portal/neighborhood?repo=...` | Returns the cross-repo neighborhood projection (see §Cross-Repo Section). |

**Route ordering**: every `/api/eidet/portal*` route MUST be registered **before** the catch-all `r.MapPrefix("GET", "/api/eidet/", _memoryRead.GetMemory)` at `EidetApi.cs:168`. Routes registered after the catch-all are interpreted as memory-by-id lookups and 404 on the lookup. Add the Portal block alongside the other "non-id-prefixed" exact routes near `EidetApi.cs:125–139`.

No `POST /api/eidet/portal` and no file-writing endpoint. Markdown-export-to-stdout is **not** in v1; if anyone wants it later it's a thin wrapper that emits the same data as plain markdown for piping to other tools.

**No MCP tool in the v1.x roadmap.** The Portal is a human surface; agents already have `eidet_recall` and `eidet_context`. An `eidet_portal` MCP tool will be added on explicit demand, not speculatively — likely once `summary` augmentation makes a single-call rendered Portal genuinely token-cheaper than N recall calls and reassembly.

---

## Implementation Sketch

New module: `Eidet.Core.Portal` (server side) + portal page in the existing Web UI SPA.

```
Eidet.Core.Portal/
├── PortalRenderer.cs        # facade: input repo + augment-level → list of sections
├── PortalSections/          # one class per section, IPortalSection
│   ├── IdentitySection.cs
│   ├── ArchitectureSection.cs
│   ├── ProceduresSection.cs
│   ├── HeuristicsSection.cs
│   ├── RecentActivitySection.cs
│   ├── LinkedReposSection.cs
│   ├── GlossarySection.cs
│   └── HealthSection.cs
├── MemoryStateHasher.cs     # stable digest of cited memory IDs+content for cache key
├── Augmentation/            # reuses existing IEnrichmentPort
│   ├── IPortalAugmenter.cs
│   ├── NoopAugmenter.cs
│   └── OllamaSummaryAugmenter.cs
├── Caching/
│   └── PortalCache.cs       # RavenDB-backed, keyed on (repo, section, augment_level, hash)
└── Rendering/
    └── HtmlBuilder.cs       # outputs sanitized HTML fragments + a citations array
```

Each `IPortalSection.RenderAsync(PortalContext) → SectionResult` returns either rendered HTML + cited memory IDs, or `Skip` (section omitted entirely from response).

Web UI:
```
ui/portal/
├── portal.html              # SPA route /ui/portal/<repo>
├── portal.js                # fetches /api/eidet/portal, renders sections, handles toggles
└── portal.css               # matches existing dark theme
```

Reuses:
- `MemoryService` / `IMemoryReader` for source data (Browse, GetByIds, Stats, Quality).
- `EnrichmentService` for augmentation (already abstracted via `IEnrichmentPort`).
- `WriteValidator`'s secret scanner over augmented output (defense in depth — Ollama is local but still untrusted).
- Existing canvas/d3 graph rendering code in `app.js` for the §Linked Repos visualization (presentation only — data source is the new neighborhood endpoint, not `/api/eidet/graph`).
- Existing Browser page's `showDetail(id)` as the target of memory citations (after the new `/ui#memory/<id>` hash route is added).

New code paths:
- **API route registration** in `EidetApi.cs` adds `/api/eidet/portal`, `/api/eidet/portal/sections/<id>`, `/api/eidet/portal/state-hash`, `/api/eidet/portal/neighborhood` — all registered **before** the catch-all at line 168.
- **SPA hash router** in `app.js` `showPage()` recognizes `portal/<repo>` and `memory/<id>` (split on `/`); the latter activates the Browser page and calls `showDetail(decoded-id)`.
- **`Eidet.Core.Portal.Neighborhood`** computes the cross-repo projection by browsing memories with `LinksOut` and grouping by target repo prefix (no new index in v1).

Tests:
- Each section unit-tested with a fixture memory store.
- One end-to-end integration test asserts JSON shape of `/api/eidet/portal` and that every citation memory_id resolves.
- Augmentation tests use `InMemoryEnrichmentAdapter` (already public for tests).

---

## Phased Delivery

| Phase | Scope |
|-------|-------|
| **v1 (MVP)** | Sections: Identity, Architecture, Procedures, Heuristics, Health (Identity + Health always present; others omitted if empty). Augmentation `off` only. `/api/eidet/portal` + Web UI page with TOC, citations as hyperlinks, collapsed-by-default provenance toggle (sticky in localStorage), hover tooltips fetched fresh on hover. |
| **v1.1** | Sections: Recent Activity, Linked Repos (one-hop graph + edges table + link to Graph view), Glossary. Augmentation `summary` (Ollama) with prose-cache keyed on `(repo, section, level, hash-of-cited-memories)`. Per-section refresh endpoint. Inline `add counter-observation` action in hover-card; `forget`/`edit` navigate to detail view. |
| **v1.2** | Augmentation `narrative` behind a feature flag, gated on the citation-required sentence filter proving out under `summary` traffic. Profiling-driven decision on whether to add full-section caching (template renders) on top of prose caching. |
| **v2** | Cross-repo aggregate "Atlas" page. Markdown export wrapper, MCP `eidet_portal` tool — both only if explicit demand emerges. |

---

## Resolved Design Decisions

| # | Decision |
|---|----------|
| 1 | User-facing term is **Portal**; URL `/ui/portal/<repo-id>`; "Atlas" reserved for the cross-repo aggregate. |
| 2 | **Identity and Health always present**; every other section omitted if empty (no "(none)" placeholders). |
| 3 | Templates render live; **only Ollama-augmented prose is cached**. Hash-keyed; no TTL; no invalidation hooks. |
| 4 | "Show provenance" toggle defaults to **collapsed**; sticky in localStorage. |
| 5 | Augmentation phased: **`off` v1, `summary` v1.1, `narrative` v1.2.** |
| 6 | Linked-repos graph shows **one hop only**; link out to Graph view for deeper exploration. |
| 7 | Hover-card has **inline `add counter-observation` only**; `forget` and `edit` navigate to memory detail view. |
| 8 | **No MCP `eidet_portal` tool in the v1.x roadmap** — added on explicit demand only. |

## Risks

- **Ollama hallucination**: augmented prose may invent claims. Mitigation: citation-required sentence filter; `summary` mode only *groups* OneLiners, doesn't generate facts; `narrative` is opt-in and v1.2.
- **Render cost**: a portal view that re-runs Ollama on every page load is slow. Mitigation: section-level cache keyed on memory_state_hash; cache hit is the normal path.
- **Stale tooltip data**: hover-cards showing OneLiner text could lag behind edits. Mitigation: tooltips fetch fresh on hover, not at page load.
- **Cross-repo information leak**: a user with access to repo A but not repo B should not see B's memory contents in A's portal. **Today, no per-repo ACL exists** — `/ui` is public, auth is coarse `read:all`/`write:all`. Mitigation for v1 is structural rather than enforced: §Linked Repos surfaces only repo IDs, edge labels, and citing memory IDs (all already addressable on their own); neighbor *content* — one-line summaries, OneLiner pull-throughs, anything beyond an ID — is excluded from v1 entirely. Neighbor summaries return only after layer-level ACLs ship and the citation link target can enforce them at fetch time.
- **Empty-portal trap**: brand-new repos with 2 memories produce a sad-looking page. Mitigation: when memory count <10, show a friendly "your portal grows as you store memories — try `eidet_intake`" panel instead of a sparse skeleton.
