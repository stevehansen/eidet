# Memory tool (Claude `/memories` files)

Eidet as the backend for Claude's `memory_20250818` tool — byte-exact scratch files, not knowledge.

**Status:** current as of the P1–P3 wave ([#68](https://github.com/stevehansen/eidet/issues/68)) ·
**Governing issues:** #68 (memory-tool backend + opt-in bridge). Threats: `STRIDE.md` I-8, T-15.
**Priming skill:** [`.claude/skills/memorytool/SKILL.md`](../../.claude/skills/memorytool/SKILL.md)

## What it is

A faithful filesystem-shaped store: path-keyed blobs the model re-reads verbatim, served through the
six `memory_20250818` commands (view, create, str_replace, insert, delete, rename). A **Memory file** is
a different concept from a **Memory** — it bypasses the signal gate, FadeMem decay, and consolidation
*by design*, because Claude's own contract is "the bytes I wrote are the bytes I read".

It is *not* semantic memory (**memory**), *not* an intake source (**intake** seeds from repo files, not
from `/memories`), and *not* open work (**looseends**). The one bridge to the semantic store is
one-way, opt-in, and off by default.

## Core entities & relationships

```
MemoryCommand (Invalid | View | Create | StrReplace | Insert | Delete | Rename)
        │  parsed at the transport boundary; malformed input becomes Invalid, never an exception
        ▼
MemoryToolTranslator  (single ExecuteAsync entry; repo bound at construction)
   ├─ MemoryPath          — validated canonical path; the one choke point for path safety
   ├─ IMemoryFileStore    — RavenMemoryFileStore (memoryfiles/*) | InMemoryFileStore (tests)
   ├─ MemoryToolOptions   — MaxFileBytes, SecretPolicy (Reject | Redact)
   └─ IMemoryBridge       — NullMemoryBridge (default) | EidetMemoryBridge (opt-in, one-way)
        ▼
MemoryToolResult (IsError, Text)   — every model-caused failure is a result, never a throw

Reserved read-only subtree: /memories/.recall — surfaces hybrid recall as a virtual directory
```

## Invariants & rules

- **Nothing the model can do produces an exception.** Bad paths, missing files, failed replacements,
  out-of-bounds inserts, oversized writes — all come back as `is_error` results with the exact strings
  the tool contract expects. A storage fault is logged server-side and returns a *generic* error; the
  exception message is never echoed to the model.
- **`MemoryPath` is the only path validator.** Traversal segments, backslashes, control characters, and
  url-encoded escapes (including nested ones) are rejected at construction, so downstream code never
  sees an unsafe path (STRIDE I-8). Never build a blob key by string concatenation.
- **The write gate here is the secret scan plus the size cap — nothing else.** The semantic store's
  low-signal and self-talk gates would reject Claude's legitimately short scratch files and break the
  filesystem contract. Secret scanning is still not optional (**writepath**).
- **A redacting write is reported.** Under `SecretPolicy.Redact` the content is rewritten in place *and*
  the success message carries a redaction count — round-trip honesty about altered bytes. Under
  `Reject`, nothing is stored.
- **Bytes are never rewritten semantically.** The translator edits exactly what the command says
  (occurrence-counted `str_replace`, bounds-checked `insert`) and stores the result verbatim.
- **The blob is the source of truth; the bridge is best-effort.** Promotion failures are logged and
  swallowed — the file write already succeeded. Rejections and duplicates on the semantic side are
  *expected*, not errors.
- **The bridge is one-way and off by default.** `NullMemoryBridge` is the default; nothing in this
  domain ever rewrites a blob from the semantic store.
- **`/memories/.recall` is read-only and reserved.** Writes under it are refused, and the root itself is
  never a file.
- **Repo isolation is bound at construction**, not passed per command — one translator serves one repo.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/MemoryTool/MemoryToolTranslator.cs` | The deep module: all six commands, rendering, guards, the `.recall` subtree |
| `src/Eidet.Core/MemoryTool/MemoryPath.cs` | Canonical path + `TryParse`/`Of`; the path-safety choke point |
| `src/Eidet.Core/MemoryTool/MemoryCommand.cs` | The command union, including `Invalid` |
| `src/Eidet.Core/MemoryTool/MemoryToolResult.cs` | `(IsError, Text)` — the tool-shaped result |
| `src/Eidet.Core/MemoryTool/MemoryToolOptions.cs` | Size cap + `SecretPolicy` |
| `src/Eidet.Core/MemoryTool/{IMemoryFileStore,InMemoryFileStore}.cs` + `src/Eidet.Core/Storage/RavenMemoryFileStore.cs` | The blob store seam and its two implementations |
| `src/Eidet.Core/MemoryTool/{IMemoryBridge,NullMemoryBridge,EidetMemoryBridge}.cs` | The opt-in one-way shadow into the semantic store |
| `src/Eidet.Core/MemoryTool/MemoryFile.cs` | The stored document |
| `src/Eidet.Service/Api/Endpoints/MemoryToolEndpoint.cs` | `POST /api/eidet/memory-tool` — the command relay |

## Gotchas

- **Directories are implied, never stored.** A listing derives subdirectories from deeper blob keys, so
  an "empty directory" cannot exist and `view` on one reports the path as nonexistent.
- **Line handling is logical, not literal.** `SplitLines` drops the phantom element a trailing newline
  creates, and `view` renders 1-based numbers with `-1` meaning "to the end". Off-by-one bugs here are
  visible to the model as wrong line numbers.
- **The error strings are part of the contract.** Reword one and the model's recovery behaviour changes;
  the translator tests pin them.
- **Promoted files land as `memory-tool`-tagged Observations**, with the file path itself as a tag — that
  tag is how `.recall` maps a hit back to a path. Changing the tag shape breaks the mapping silently.
- **`.recall` results are capped tightly** and come from single-repo recall (never cross-repo).
- **The size cap is measured in UTF-8 bytes, not characters**, so a multi-byte file hits the cap sooner
  than its length suggests.

## Executable references

- `tests/Eidet.Core.Tests/MemoryTool/MemoryToolTranslatorTests.cs` — **the authority on all six
  commands**: exact success/error strings, line rendering and ranges, `str_replace` occurrence counting,
  `insert` bounds, root and `.recall` protection, the size cap, and both secret policies.
- `tests/Eidet.Core.Tests/MemoryTool/MemoryPathTests.cs` — settles path safety: traversal, backslashes,
  control characters, and url-encoded escapes.
- `tests/Eidet.Core.Tests/MemoryTool/MemoryCommandParseTests.cs` — settles that malformed transport
  input becomes `Invalid` rather than throwing.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Memory-tool files (Memory file, Memory tool, Translator, Bridge)
- Design rationale: [`docs/specs/IntegrationSpec.md`](../specs/IntegrationSpec.md) (Claude Code / API
  interop) · threat model: `STRIDE.md` I-8, T-15
- Related domains: **writepath** (the always-on secret scan, and the `Redact` variant used only here) ·
  **memory** (what the bridge promotes into) · **recall** (backs `/memories/.recall`) · **intake**
  (the *other* file-shaped inbound path, and a different concept)
- Priming skill: `.claude/skills/memorytool/SKILL.md`
