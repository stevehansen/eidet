---
name: memorytool
description: Prime on Eidet's memory_20250818 backend before changing it — MemoryToolTranslator's six commands (view/create/str_replace/insert/delete/rename), MemoryPath validation, byte-exact path-keyed blobs in memoryfiles/*, the size cap and SecretPolicy, the reserved /memories/.recall subtree, and the opt-in one-way IMemoryBridge into the semantic store. Use when the task touches Claude memory-tool commands, /memories paths, MemoryFile storage, or POST /api/eidet/memory-tool. Not for semantic memories (see memory), not for seeding from repo files (see intake).
---

# Memory tool — priming

**Canonical spec:** `docs/domains/memorytool.md` — read it for command behaviour, all invariants, key
files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Memory-tool files. Threats:
`STRIDE.md` I-8, T-15.

A faithful filesystem: the model re-reads exactly the bytes it wrote. A **Memory file** is not a
**Memory** — it skips the signal gate, decay, and consolidation by design.

## Core invariants (get these right)

- **Nothing the model can do throws.** Every expected failure is an `is_error` result with the contract's
  exact wording; a storage fault returns a *generic* error and logs server-side (never leak `ex.Message`).
- **`MemoryPath` is the only path validator** — traversal, backslashes, control chars, and url-encoded
  escapes are rejected at construction (I-8). Never concatenate a blob key.
- **The gate here is the secret scan + size cap only** — the semantic low-signal/self-talk gates would
  break the filesystem contract. Secret scanning is still mandatory.
- **A redacting write reports its redaction count**; a rejecting policy stores nothing.
- **Never rewrite bytes semantically** — occurrence-counted `str_replace`, bounds-checked `insert`,
  verbatim store.
- **The blob is the source of truth; the bridge is best-effort, one-way, and off by default**
  (`NullMemoryBridge`). Semantic-side rejections and duplicates are expected.
- **`/memories/.recall` is read-only and reserved**; the root is never a file.
- **Repo isolation is bound at construction** — one translator per repo.

## Key files / reuse

- `src/Eidet.Core/MemoryTool/MemoryToolTranslator.cs` — the single `ExecuteAsync` entry.
- `src/Eidet.Core/MemoryTool/MemoryPath.cs` — parse/validate here, always.
- `src/Eidet.Core/MemoryTool/MemoryToolOptions.cs` — size cap + `SecretPolicy`.
- `src/Eidet.Core/MemoryTool/InMemoryFileStore.cs` — the test store.

## Gotchas

- Directories are implied by deeper blob keys, never stored — an empty directory can't exist.
- Line handling is logical: the trailing-newline phantom line is dropped; `view` is 1-based with `-1`
  meaning end-of-file.
- The error strings are part of the contract and are pinned by tests — rewording changes model behaviour.
- Promoted files become `memory-tool`-tagged Observations carrying the file path as a tag; `.recall`
  depends on that tag shape.
- The size cap counts UTF-8 bytes, not characters.
