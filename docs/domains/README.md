# Eidet — domain documentation

The **per-domain documentation layer**: one deep living spec per business domain, each paired with a
thin agent-priming skill. A living spec describes *current state* — entities, invariants, key files,
gotchas — and links down into `../specs/` for design rationale and into `../../UBIQUITOUS_LANGUAGE.md`
for terminology. Build instructions, architecture, and conventions stay in `../../CLAUDE.md`.

This folder is excluded from the published eidet.dev site (`../_config.yml`), like `../specs/`.

## Domain index

| Domain | Living spec | Priming skill | Governing issues |
|---|---|---|---|
| Memory core | [`memory.md`](memory.md) | [`.claude/skills/memory/`](../../.claude/skills/memory/SKILL.md) | foundational; #9/#10/#17, #59, #65 |
| Write path & integrity | [`writepath.md`](writepath.md) | [`.claude/skills/writepath/`](../../.claude/skills/writepath/SKILL.md) | #34, #37, #80 |
| Recall & context | [`recall.md`](recall.md) | [`.claude/skills/recall/`](../../.claude/skills/recall/SKILL.md) | #33, #35, #38 |
| Maintenance | [`maintenance.md`](maintenance.md) | [`.claude/skills/maintenance/`](../../.claude/skills/maintenance/SKILL.md) | #22, #39, #55, #60 |
| Enrichment | [`enrichment.md`](enrichment.md) | [`.claude/skills/enrichment/`](../../.claude/skills/enrichment/SKILL.md) | #21, #60 |
| Intake | [`intake.md`](intake.md) | [`.claude/skills/intake/`](../../.claude/skills/intake/SKILL.md) | #63, #68 |
| Loose Ends | [`looseends.md`](looseends.md) | [`.claude/skills/looseends/`](../../.claude/skills/looseends/SKILL.md) | #42, #46/#48, #77 |
| Canon | [`canon.md`](canon.md) | [`.claude/skills/canon/`](../../.claude/skills/canon/SKILL.md) | #75/#76 |
| Memory tool | [`memorytool.md`](memorytool.md) | [`.claude/skills/memorytool/`](../../.claude/skills/memorytool/SKILL.md) | #68 |
| Sharing (layers & packs) | [`sharing.md`](sharing.md) | [`.claude/skills/sharing/`](../../.claude/skills/sharing/SKILL.md) | #34, #80 |
| Portal | [`portal.md`](portal.md) | [`.claude/skills/portal/`](../../.claude/skills/portal/SKILL.md) | — |
| Quality & benchmarking | [`quality.md`](quality.md) | [`.claude/skills/quality/`](../../.claude/skills/quality/SKILL.md) | #36, #39, #68 |

**Not domains** — these are technical or operational layers and stay in `../../CLAUDE.md` and
[`../specs/ServiceSpec.md`](../specs/ServiceSpec.md): the REST router and auth, the MCP surface and
`ToolDispatcher`, CLI commands (install/update/setup/doctor), RavenDB storage and provisioning,
configuration, hooks, the scheduler, usage analytics, backup/restore, the Web UI, and the SDKs.

## Other references

- [`../../UBIQUITOUS_LANGUAGE.md`](../../UBIQUITOUS_LANGUAGE.md) — the canonical glossary; specs link
  *down* into it rather than redefining terms
- [`../specs/`](../specs/) — design specs (`CoreSpec`, `ServiceSpec`, `IntegrationSpec`, `SyncSpec`):
  intent and rationale. Living specs sit *above* them and describe what is true today. The retired
  *PortalSpec*, *LooseEndSpec*, *ValenceSpec*, and *CanonSpec* design specs were folded into `portal.md`,
  `looseends.md`, `memory.md`, and `canon.md` respectively.
- [`../../STRIDE.md`](../../STRIDE.md) — threat model; security-relevant changes update it in the same PR
- [`../deep-dive.md`](../deep-dive.md), [`../phases.md`](../phases.md) — architecture walk-through and
  implementation history
- Published user docs (getting-started, concepts, api-reference, configuration, hooks, mcp-tools, sdk)
  live at the root of `../` and *are* part of the eidet.dev site

## Adding a new domain

Each domain gets a hybrid pair, split by audience: a deep human-facing living spec at
`docs/domains/<domain>.md`, and a thin agent-facing priming skill at
`.claude/skills/<domain>/SKILL.md` that links *down* to the spec. Lowercase single-word filenames.

**Living-spec sections:** title + one-line purpose · status/governing issues · what it is (including
what it is *not*, and which sibling owns that) · core entities & relationships · invariants & rules ·
key files · gotchas · executable references (the tests that pin the invariants) · links.

**Priming-skill shape:** frontmatter (`name` matching the directory, `description` naming concrete
entities, trigger phrases, and an explicit `Not for X (see sibling)`) → one line on what it is plus a
link to the spec → get-these-right invariants → key files → gotchas. 25–50 lines.

**The iron rule: the skill links, never duplicates.** Anything more than a compact essential belongs in
the spec, with the skill pointing at it.

**Same-PR sync rule:** any change to a domain's behavior updates its living spec **in the same PR** as
the code change — never as a follow-up. If it alters a load-bearing invariant, update the priming skill
too. A domain-behavior diff with no matching spec edit is incomplete.

**Anti-transcription rule:** if a line would change whenever the code changes, link to the file instead
of restating it. No property lists, no endpoint tables, no copied thresholds — cite the file that owns
them. Never write an invariant you have not traced to code you read.

Auditing and adding domains is handled by the user-level `domain-priming` skill.
