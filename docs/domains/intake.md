# Intake

Seeding a repo's memory from what already exists — its markdown, its manifests, its git history, and
Claude Code's own memory directory.

**Status:** current as of the P1–P3 wave ([#68](https://github.com/stevehansen/eidet/issues/68)) ·
**Governing issues:** [#63](https://github.com/stevehansen/eidet/issues/63) (per-candidate gate on the
bulk path), git-history intake and Claude-memory interop shipped in #68.
**Priming skill:** [`.claude/skills/intake/SKILL.md`](../../.claude/skills/intake/SKILL.md)

## What it is

A pluggable extract-and-seed pipeline: `IntakeService` owns dedup, gating, store plumbing, dry-run
semantics, and result aggregation, while each `IIntakeExtractor` knows exactly one ecosystem or
document type. Four entry verbs — whole-repo, docs-folder, git-history, Claude-Code-memory — each of
which activates a *different subset* of extractors.

It is *not* pack import (**sharing** — that mounts a read-only layer instead of writing local
memories), *not* the `/memories` file store (**memorytool**), and *not* consolidation (**maintenance**
turns observations into insights; intake only seeds).

## Core entities & relationships

```
IntakeService.Ingest{,Docs,Git,ClaudeMemory}Async
  → IntakeContext { RepoId, ProjectPath, DryRun, IntakeOptions }
  → MemoryService.RunBulkAsync(Validate: false)          // deliberate — see invariants
      → foreach extractor where AppliesTo(ctx): ExtractAsync(ctx, sink)
      → OrchestratorSink.AddMemoryAsync per candidate:
           length floor → WriteValidator.Validate → content-addressed id → duplicate probe → store
  → IntakeResult { Items[] (each new or skipped-with-reason), NewCount, SkippedCount,
                   DetectedLinks, ProducedPackages }

Extractors: ClaudeMd · AgentsMd · Readme · EditorConfig · DocsFolder* · ClaudeCodeMemory* ·
            NuGetDependency · NpmDependency · GitHistory*        (* option-gated, inactive by default)
Git side:   IGitHistorySource ← GitCliAdapter | InMemoryGitHistorySource | NullGitHistorySource
```

Every stored intake memory carries `Source = "intake"` and `Provenance = MemoryProvenance.Intake`,
which puts it on the import trust floor for the rest of its life (**writepath**).

## Invariants & rules

- **The bulk write runs with validation off, and the gate runs per candidate instead.** The bulk
  validate path *throws* and aborts the whole batch on the first bad candidate; intake calls
  `WriteValidator.Validate` inside the sink so a secret-bearing or low-signal candidate is skipped with
  its reason surfaced and the run continues. Skip-not-abort is the rule — and it means no intake
  candidate is ever stored unscanned (#63, STRIDE T-15/I-7).
- **A rejected candidate's content is blanked in the result.** Otherwise a caught secret would leak
  straight back out through CLI/REST output.
- **A seed must never outrank earned knowledge.** An intake memory is an unverified restatement of a
  file the agent can already open, so every extractor caps importance at or below `0.5` (CLAUDE/AGENTS
  `0.5`, Claude-Code memory `0.5`, README and docs-folder `0.4`, editorconfig `0.35`). Earlier builds
  minted these at `0.8` — above the observed `AgentInferred` median of `0.63` — and because
  `GetTopScoredAsync` orders the L1 candidate pool by importance alone, the wake-up slice filled with
  doc chunks and echoed `CLAUDE.md` back at an agent that already had it loaded. `CorpusRepairStage`
  re-baselines seeds minted under the old values.
- **Mined tags are prose and must be cleaned.** A heading splits into function words and bare numbers
  ("How to Make Changes" → `how`/`to`/`make`/`changes`; "2026-04-08" → `2026`/`04`/`08`), which tag
  most of a corpus and narrow nothing. `TagsFromHeading`/`TagsFromFileName` both run `TagHygiene.Clean`.
- **Intake ids are content-addressed and must be minted by `MemoryIdGenerator`.** That is what makes
  re-ingesting an unchanged file a single `GetAsync` probe instead of a similarity query — and minting
  locally instead would make every intake memory read as rewritten content to the commitment check
  (**writepath**).
- **Every skip carries a reason.** `"0 new"` is never mysterious: per-file, per-candidate, and
  per-commit skips all land in `IntakeResult.Items` with a `SkipReason`.
- **Git intake mines the *pattern*, never the diff.** The problem comes from the commit message; the fix
  shape comes from change stats and hunk-header regions. Raw diff lines are never stored. The gate is a
  deterministic Conventional-Commits allowlist (zero-LLM) — `fix` becomes a Procedure, the
  feature/perf/refactor family becomes an Insight.
- **The git watermark is read *before* the run and advanced after.** A commit landing mid-run stays
  ahead of the watermark and is picked up next time: at-least-once, with content-hash dedup absorbing
  the replay. A dry run never advances it.
- **Out-of-repo sources are opt-in and path-constrained.** The Claude-Code-memory extractor reads only
  the single resolved per-project memory directory, never arbitrary home paths (STRIDE I-8), and never
  runs in the default pass. Eidet never writes into Claude Code's own memory directory.
- **A verb runs only its own extractors.** `IngestGitAsync` and `IngestClaudeMemoryAsync` filter the
  registry by type so file extractors can't ride along on a git or interop run.
- **Markdown section rules live in one place.** `MarkdownIntake` holds the heading split, the minimum
  section length, the body-less predicate, and tag mining, so every markdown extractor stays in
  lock-step. Pure functions — no I/O, no store access.
- **A heading is a label for knowledge, never the knowledge.** `MinSectionLength` cannot express this:
  it measures length, and `## Development Patterns` is 23 characters of pure heading. So the sink also
  rejects any candidate where `MarkdownIntake.IsHeadingOnly` holds — nothing but headings, blank lines
  and fence delimiters — with the reason `"heading with no body"`, distinct from `"too short"` so the
  report never sends anyone looking for a length to raise. A body-less memory is worse than low-signal
  because *every* rendered form of it has to be invented: a field corpus banked 1,000 of them (9% of
  everything live, across 74 repos), 843 with an LLM one-liner asserting a claim the repo never made
  (`## Development Patterns` → "Focus on iterative development cycles for faster, adaptable product
  improvements"), and because L1 prefers the one-liner, 59 wake-up lines across 26 repos were
  fabrications while the summary honestly reporting "this is a heading, not content" stayed hidden.
  **enrichment** refuses these as a backstop and **maintenance** retires the ones already stored.
  Deliberately narrow — one letter or digit outside the structure is a body, so `## Build` +
  `dotnet build` is kept. Rejecting emptiness is the job; rejecting terseness is not.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Services/IntakeService.cs` | The four verbs, the bulk scope, `OrchestratorSink` (gate + dedup + store) |
| `src/Eidet.Core/Intake/IIntakeExtractor.cs` | The extractor contract (`Name`, `AppliesTo`, `ExtractAsync`) |
| `src/Eidet.Core/Intake/{IIntakeSink,IntakeContext,IntakeMemory}.cs` | What an extractor is handed and what it may emit |
| `src/Eidet.Core/Intake/MarkdownIntake.cs` | Shared heading split / length floor / body-less predicate / tag mining |
| `src/Eidet.Core/Intake/Extractors/` | One file per source (markdown, editorconfig, NuGet, npm, docs folder, Claude memory) |
| `src/Eidet.Core/Intake/Git/GitHistoryExtractor.cs` | The commit gate + pattern mining, with its own per-commit caps |
| `src/Eidet.Core/Intake/Git/{IGitHistorySource,GitCliAdapter,InMemoryGitHistorySource,NullGitHistorySource}.cs` | The git port and its adapters |
| `src/Eidet.Service/Mcp/AutoIntakeOnContext.cs` | First-context auto-seed hook (service-side trigger) |
| `src/Eidet.Service/Commands/Intake{,Git,ClaudeMemory}Command.cs` | The CLI verbs |

## Gotchas

- **Adding an extractor to `DefaultExtractors()` is not enough — and can be too much.** An extractor
  with no option gate runs on *every* whole-repo intake; one that needs a gate must check
  `IntakeOptions` in `AppliesTo`, or it will fire on runs it has no business in.
- **`DocsFolderExtractor` is registered but inert** until `IntakeOptions.DocsPattern` is set. Same
  pattern for `ClaudeCodeMemoryExtractor` and `GitHistoryExtractor` — registered ≠ active.
- **Dry run still runs the gate and the duplicate probe**, so a dry-run report is honest about what
  would be skipped, but it writes nothing *and* leaves the watermark untouched.
- **Git intake is bounded per commit** (files scanned, files named in a pattern, regions, hunks, body
  characters). A huge commit produces a truncated pattern, not a huge memory.
- **`GitCliAdapter.TryCreate` returns null outside a git repo**, and the service falls back to
  `NullGitHistorySource` — which reports the run as skipped rather than failing. "Not a git repository"
  is a normal result.
- **Intake memories start on the intake trust floor (`0.7`), not the import floor.** A seeded memory
  ranks below first-party knowledge until it earns echoes — deliberate, not a bug to tune away — but it
  outranks a remote pack, because it came from a file in the user's own tree. See **writepath** for the
  three tiers and the measurement that set this one.

## Executable references

- `tests/Eidet.Core.Tests/Intake/IntakeSinkValidationTests.cs` — **the authority on skip-not-abort**:
  the per-candidate gate, the blanked content of a rejected item, and the batch surviving a bad
  candidate.
- `tests/Eidet.Core.Tests/Intake/HeadingOnlyGateTests.cs` — **the authority on what counts as a
  body-less section** and on intake refusing to store one. The "keeps" cases are as load-bearing as the
  "rejects" ones: over-rejection here would silently discard real terse memories.
- `tests/Eidet.Core.Tests/Memory/MemoryIdConventionTests.cs` — settles that intake ids stay
  content-addressed so re-ingest skips as a duplicate (shared with **memory**).
- `tests/Eidet.Core.Tests/Intake/Git/{GitHistoryExtractorTests,GitCliParserTests,IntakeServiceGitTests}.cs`
  — settle the Conventional-Commits gate, pattern mining without diff lines, and watermark advance /
  dry-run behaviour.
- `tests/Eidet.Core.Tests/Intake/IntakeServiceInteropTests.cs` +
  `tests/Eidet.Core.Tests/Intake/Extractors/ClaudeCodeMemoryExtractorTests.cs` — settle the opt-in interop verb and its path
  constraint.
- `tests/Eidet.Core.Tests/Intake/MarkdownIntakeTests.cs` + `tests/Eidet.Core.Tests/Intake/Extractors/*ExtractorTests.cs` — settle the
  heading split, the length floor, and each source's tags/types.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Lifecycle (Intake, Git-History Intake, Watermark)
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) ·
  [`docs/specs/IntegrationSpec.md`](../specs/IntegrationSpec.md) (Claude Code interop)
- Related domains: **writepath** (the gate intake calls per candidate; the import trust floor) ·
  **memory** (content-addressed ids) · **sharing** (packs — the *other* inbound path) · **memorytool**
  (Claude's file store, not a seed source) · **maintenance** (what happens to seeds afterwards)
- Priming skill: `.claude/skills/intake/SKILL.md`
