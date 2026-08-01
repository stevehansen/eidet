---
name: portal
description: Prime on Eidet's Portal before changing it — the per-repo generated HTML state view behind GET /api/eidet/portal, PortalRenderer's one-fetch section loop, the IPortalSection contract and its five v1 sections (Identity, Architecture, Procedures, Heuristics, Health), PortalMarkup citation anchors and escaping, and the augment=off liveness model. Use when the task touches a Portal section, portal rendering or citations, or the Web UI's portal view. Not for the approve-a-page curation loop (see canon), not for ranked agent-facing retrieval (see recall).
---

# Portal — priming

**Canonical spec:** `docs/domains/portal.md` — read it for the section rules, all invariants, key files,
and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Canon (for the Canon/Portal distinction).

A read-only projection of live memories into cited HTML sections: "what has this agent actually learned
here?", auditable by a human. Human surface only — no MCP tool, on purpose.

## Core invariants (get these right)

- **Every claim is a citation** — render through `PortalMarkup.Cite` (anchor to `#memory/<id>` with
  `data-mid`). A section must never state something it can't hyperlink.
- **Escape everything** via `PortalMarkup.Esc`; memory content is agent-written and unescaped.
- **Selection rules are deterministic and total** — first-match precedence, ties broken on id, so two
  renders of the same corpus agree.
- **Sections read only `PortalContext`** — the renderer pre-fetches once; a section issuing its own query
  breaks that contract.
- **v1 renders live and caches nothing**; `augment` is hardcoded `"off"` (the field exists so the API
  shape survives augmentation landing).
- **Normalize the repo id once at the top** — `BrowseAsync` normalizes internally, `GetCountsByTypeAsync`
  doesn't.

## Key files / reuse

- `src/Eidet.Core/Portal/PortalRenderer.cs` — the facade + the default section list.
- `src/Eidet.Core/Portal/IPortalSection.cs` — implement this (it's `internal`) to add a section.
- `src/Eidet.Core/Portal/PortalMarkup.cs` — escaping, citations, ordering; never hand-roll HTML.
- `src/Eidet.Core/Portal/PortalContext.cs` — the only input a section gets.

## Gotchas

- The pre-fetch is bounded — a large repo renders a *partial* Portal with no warning.
- `AlwaysPresent` is a promise the section keeps itself; the renderer only drops nulls.
- Health metrics stick to what every memory carries (counts + a `CreatedAt` histogram).
- The Glossary section doesn't exist yet and is where Canon convergence is designed to land — don't
  bolt `canon:*` reads onto an existing section.
- Only one test file covers this domain (`PortalRendererTests`); a changed section rule needs a test in
  the same PR or nothing catches it.
