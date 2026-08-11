---
name: intake
description: Prime on Eidet's intake pipeline before changing it — IntakeService's four verbs (whole-repo, docs-folder, git-history, Claude-Code-memory), the IIntakeExtractor/IIntakeSink contract, the per-candidate write gate, content-addressed dedup, the git commit gate and watermark. Use when the task touches an intake extractor, seeding memories from CLAUDE.md/README/manifests, git-history mining, Claude Code memory import, or a skip reason in an intake result. Not for pack import (see sharing), not for the /memories file store (see memorytool), not for turning seeds into insights (see maintenance).
---

# Intake — priming

**Canonical spec:** `docs/domains/intake.md` — read it for the four verbs, all invariants, key files,
and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Lifecycle (Intake, Git-History Intake,
Watermark).

`IntakeService` owns gating, dedup, store plumbing, dry-run, and result aggregation; each extractor
knows exactly one source. Every seeded memory lands on the import trust floor
(`Provenance = Intake`).

## Core invariants (get these right)

- **The bulk write runs `Validate: false` on purpose** — that path throws and aborts the batch. Intake
  calls `WriteValidator.Validate` per candidate in the sink: **skip-not-abort**, reason surfaced, and
  nothing is ever stored unscanned.
- **Blank a rejected candidate's content in the result** — otherwise a caught secret leaks out through
  CLI/REST output.
- **Intake ids are content-addressed via `MemoryIdGenerator`** — that's what makes re-ingest a single
  `GetAsync` probe, and minting locally would make every intake memory read as tampered.
- **Every skip carries a reason** so "0 new" is never mysterious.
- **Git intake mines the pattern, never diff lines**; the gate is a deterministic Conventional-Commits
  allowlist (`fix` → Procedure, feat/perf/refactor → Insight).
- **Read the git watermark before the run, advance it after** (at-least-once; dedup absorbs replays).
  Dry runs never advance it.
- **Out-of-repo sources are opt-in and path-constrained** — the Claude-memory extractor reads only the
  resolved per-project directory, and Eidet never writes there.
- **A verb runs only its own extractors** (filtered by type), so file extractors never ride along on a
  git or interop run.
- **Markdown rules live only in `MarkdownIntake`** — heading split, length floor, body-less predicate,
  tag mining.
- **A heading with no body is rejected, not stored** (`IsHeadingOnly`, reason `"heading with no body"`).
  Length cannot catch it — `## Development Patterns` is 23 chars — and every rendered form of a
  body-less memory has to be invented: 843 of 1,000 in the field carried a fabricated one-liner, 59 of
  which reached wake-ups. One letter or digit outside the headings/fences counts as a body.

## Key files / reuse

- `src/Eidet.Core/Services/IntakeService.cs` — the verbs and `OrchestratorSink`.
- `src/Eidet.Core/Intake/IIntakeExtractor.cs` + `IIntakeSink.cs` — implement these to add a source.
- `src/Eidet.Core/Intake/MarkdownIntake.cs` — reuse; don't re-split headings.
- `src/Eidet.Core/Intake/Git/IGitHistorySource.cs` — the port (`InMemoryGitHistorySource` for tests).

## Gotchas

- Registered ≠ active: `DocsFolder`, `ClaudeCodeMemory`, and `GitHistory` extractors are inert until
  their `IntakeOptions` switch is set. An ungated new extractor fires on every whole-repo intake.
- Dry run still gates and probes for duplicates; it just writes nothing.
- Git mining is capped per commit (files, regions, hunks, body chars) — big commits truncate.
- No git repo ⇒ `NullGitHistorySource` ⇒ the run reports *skipped*, not failed.
