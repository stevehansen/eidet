# Portal

A generated, source-traceable "what does Eidet know about this repo" page.

**Status:** v1 (`augment=off`) shipped — five sections, live render, no cache. `summary`/`narrative`
augmentation, the Glossary/Recent-Activity/Linked-Repos sections, the per-section refresh endpoint, and
the prose cache are designed but not built. Replaces the retired *PortalSpec.md* design spec.
**Priming skill:** [`.claude/skills/portal/SKILL.md`](../../.claude/skills/portal/SKILL.md)

## What it is

A read-only projection of a repo's live memories into an HTML document of sections, every claim
hyperlinked back to the memory it came from. It answers "what has this agent actually learned here?" in
a form a human can audit — the Portal never asserts anything a memory doesn't already say.

It is *not* Canon (**canon** is human-*approved* content that becomes memories; the Portal only
renders what exists), *not* the Web UI itself (the SPA consumes `/api/eidet/portal`), and *not* an
agent surface — there is deliberately no MCP tool.

## Core entities & relationships

```
PortalRenderer.RenderAsync(repoId)
  ├─ one BrowseAsync (all currently-valid memories, bounded) + one GetCountsByTypeAsync
  ├─ PortalContext { RepoId, AllValidMemories, CountsByType }        ← shared by every section
  └─ foreach IPortalSection: RenderAsync → PortalSection | null      ← null ⇒ omitted
     Identity · Architecture · Procedures · Heuristics · Health
  → PortalDocument(repo, augment: "off", PortalStats, sections[])

PortalMarkup — the shared HTML helpers: Esc, Cite (→ #memory/<id> with data-mid), Bullet, lists
```

Each section owns one deterministic selection rule and nothing else; `AlwaysPresent` sections emit a
stub rather than returning null.

## Invariants & rules

- **Every claim is a citation.** Sections render memory labels as anchors to the SPA's
  `#memory/<id>` route with `data-mid` for hover-fetch. A section must not state anything it cannot
  hyperlink — that traceability *is* the feature.
- **All output is HTML-escaped through `PortalMarkup.Esc`.** Memory content is agent-written and
  arrives unescaped; a section that concatenates raw content is an XSS hole.
- **Selection rules are deterministic and total.** Identity, for example, is a strict 4-step precedence
  (curated `portal:identity` memory → top insights → intake-derived → count-only stub), first match
  wins, later steps not evaluated. Ordering ties break on id so two renders of the same corpus agree.
- **Sections read only the shared context.** The renderer pre-fetches once; a section that opens its own
  store query breaks the one-fetch contract and the section-isolation model.
- **v1 renders live on every call and caches nothing.** Caching was designed only for augmented prose —
  templated rendering is cheap and always current.
- **`augment` is hardcoded `"off"` in the document.** Both the field and the query parameter exist so
  the API shape doesn't change when augmentation lands; today no other value does anything.
- **The renderer normalizes the repo id once.** `BrowseAsync` normalizes internally and
  `GetCountsByTypeAsync` does not — normalizing at the top is what keeps the two calls on the same key.
- **No MCP tool, deliberately.** The Portal is a human surface; agents have `eidet_recall` and
  `eidet_context`. One would be added only once augmentation makes a single rendered call genuinely
  cheaper than N recalls.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Portal/PortalRenderer.cs` | The facade: pre-fetch, section loop, document assembly |
| `src/Eidet.Core/Portal/IPortalSection.cs` | The section contract (`Id`, `Title`, `AlwaysPresent`, `RenderAsync`) |
| `src/Eidet.Core/Portal/PortalContext.cs` | The shared pre-fetched input every section reads |
| `src/Eidet.Core/Portal/PortalDocument.cs` | `PortalDocument` / `PortalSection` / `PortalStats` — the JSON shape |
| `src/Eidet.Core/Portal/PortalMarkup.cs` | Escaping, citation anchors, list helpers, the shared ordering |
| `src/Eidet.Core/Portal/Sections/` | One file per section, each with its selection rule in the class comment |
| `src/Eidet.Service/Api/Endpoints/PortalEndpoint.cs` | `GET /api/eidet/portal?repo=…` |

## Gotchas

- **The pre-fetch is bounded.** A repo past the browse cap renders a *partial* Portal with no warning —
  worth knowing before reading a section as exhaustive.
- **`IPortalSection` is `internal`.** Sections are added inside `Eidet.Core`, not plugged in from
  outside; the public seam is the rendered document.
- **`AlwaysPresent` is a promise the section must keep itself** — the renderer only drops nulls; it does
  not check the flag. An `AlwaysPresent` section returning null silently disappears.
- **Health metrics are deliberately limited to what every memory carries** (counts and a `CreatedAt`
  freshness histogram). Last-modified and last-consolidation were excluded for v1 because not every
  memory has them.
- **The Glossary section does not exist yet**, and it is where Canon convergence is designed to land —
  don't wire `canon:*` reads into an existing section instead.

## Executable references

- `tests/Eidet.Core.Tests/Portal/PortalRendererTests.cs` — the only test file for this domain: settles
  the section loop, null-omission, always-present stubs, citation anchors, and escaping.
- **Untested and riskiest:** the per-section selection rules beyond Identity, and the browse cap's
  partial-render behaviour. Changing a section's rule is not currently caught by anything but that one
  file — extend it in the same PR.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Canon (for the Canon/Portal distinction), Actors & surfaces
- Related domains: **canon** (approved pages; the designed source for a future Glossary section) ·
  **recall** (the *other* read surface — ranked, agent-facing) · **memory** (everything rendered here) ·
  **quality** (the other health-flavoured surface, and the one that owns findings)
- Priming skill: `.claude/skills/portal/SKILL.md`
