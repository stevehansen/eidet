---
name: memory
description: Prime on Eidet's Memory core before touching it — the MemoryEntry aggregate, its deterministic id (which doubles as a content commitment), validity intervals, supersession chains, forget/redact/edit verbs, and the Valence and FunctionalStage dimensions. Use when the task touches MemoryEntry, memory ids, MemoryType, Valence, Validity, provenance fields, supersession, version chains, forget, redact, links, or echo/fizzle feedback. Not for the gates that accept or reject a write (see writepath), not for ranking or context packing (see recall), not for scheduled rewrites (see maintenance).
---

# Memory core — priming

**Canonical spec:** `docs/domains/memory.md` — read it for the full entity shape, all invariants, key
files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Memory core / Lifecycle. Design
rationale: `docs/specs/CoreSpec.md`.

A Memory is a typed, repo-namespaced, append-only document with a deterministic id and a validity
interval. This domain owns its shape and its lifecycle verbs. Whether a write is *allowed* belongs to
**writepath**; how a memory *comes back* belongs to **recall**.

## Core invariants (get these right)

- **The id is a frozen persisted format and doubles as the content commitment.** Never hand-roll one,
  never regex its shape — ask `MemoryIdGenerator.Matches`. Changing a preimage silently de-boosts the
  entire corpus at recall instead of failing a build.
- **A git worktree is not its own repo.** `RepoPathResolver.Resolve` maps a working directory to the
  repository it belongs to (a worktree's `.git` pointer file names the main checkout), and it runs
  where a path *first* becomes a repo — the CLI's and MCP's working directory. Never put filesystem
  access in `RepoIdNormalizer`: that is a pure string map called on already-normalized ids at ~40
  sites. Never resolve a *scan root* — intake reads the files in front of it and stores them under the
  resolved repo. Memories banked before this are moved with `eidet repo rehome`.
- **Every mutation goes through `MemoryService`'s `RunWriteAsync`/`RunMutationAsync`.** The storage
  write API is unreachable outside that file, and the cache generation bump lives in its `finally`.
  A bypassing write path serves stale recalls silently.
- **Content is never edited in place** — a content change supersedes (new doc, `IsLatest=false` and a
  closed validity on the incumbent). Metadata-only edits update in place. `RedactAsync` is the sole
  content rewrite, and it keeps the id deliberately.
- **No hard delete.** `Forget` closes the validity interval, records a reason, and writes a system
  audit `Observation`.
- **`Provenance` defaults to `Unknown`, not to a trusted origin** — set it explicitly on any new
  write path.
- **`Summary == null` = awaiting enrichment; `Summary == ""` = redacted.** Both are queried on; don't
  collapse them.
- **`Valence.Neutral` and `FunctionalStage.None` are `0`** for free backfill, and `None` also means
  "applies to any stage" (recall's hard filter depends on it). Sign arithmetic lives *only* in
  `ValencePolarity`.

## Key files / reuse

- `src/Eidet.Core/Domain/MemoryEntry.cs` — the root; its field comments name each field's owner.
- `src/Eidet.Core/Domain/MemoryIdGenerator.cs` — both id conventions and `Matches`.
- `src/Eidet.Core/Services/MemoryService.cs` — every lifecycle verb + the mutation/cache gate.
- `src/Eidet.Core/Services/MemoryServiceOptions.cs` — `StoreOptions` / `EditOptions`.
- `src/Eidet.Core/Memory/ValencePolarity.cs` — the only home for stance signs.

## Gotchas

- `EditAsync` returns `NotFound` for a gate rejection too (legacy conflation) — the caller cannot tell
  the two apart.
- A redaction tombstone deliberately fails its own id check and reads as *Amended*; scrubbing content
  without `MemoryCommitment.Render` reads as tampering.
- `src/Eidet.Core/Services/VersionHistory.cs` is the installed-app-version log, **not** the memory version chain
  (`MemoryService.GetVersionChainAsync`).
- Supersessions bypass the poison fast-path and conflict gate on purpose.
