# Memory core

What a **Memory** is, and every way one legitimately changes over its life.

**Status:** current as of #80 (closed-world provenance). Replaces the retired *ValenceSpec.md* design spec.
**Governing issues:** no single PRD (foundational). Shaped by [#9](https://github.com/stevehansen/eidet/issues/9)/[#10](https://github.com/stevehansen/eidet/issues/10)/[#17](https://github.com/stevehansen/eidet/issues/17) (single write funnel), [#59](https://github.com/stevehansen/eidet/issues/59) (Valence), [#65](https://github.com/stevehansen/eidet/issues/65) (versioned curation, redaction).
**Priming skill:** [`.claude/skills/memory/SKILL.md`](../../.claude/skills/memory/SKILL.md)

## What it is

The entity at the centre of Eidet: a typed, namespaced, append-only unit of agent knowledge with a
deterministic id and a validity interval. This domain owns the **shape** of a Memory and its
**lifecycle verbs** — store, forget, edit, supersede, redact, link, feedback — plus the mutation
funnel every one of them passes through.

It is *not* the gates that decide whether a write is allowed (**writepath**), *not* how memories are
ranked or packed for an agent (**recall**), and *not* the scheduled passes that rewrite them in the
background (**maintenance**). Parked open work is a **Loose End**, not a Memory (**looseends**); a
byte-exact `/memories` blob is a **Memory file** (**memorytool**).

## Core entities & relationships

`MemoryEntry` is the aggregate root and the only document type this domain owns —
`src/Eidet.Core/Domain/MemoryEntry.cs`. Everything else here is a dimension of it or a verb on it.

```
MemoryEntry ──has 1── MemoryType      (Observation | Insight | Procedure | Heuristic)
            ──has 1── Valence         (stance, orthogonal to type)
            ──has 1── FunctionalStage (subtask, orthogonal to type)
            ──has 1── Validity        (ValidFrom / ValidUntil)
            ──has 1── MemoryProvenance
            ──has *── MemoryLink      (cross-repo / memory-to-memory edges)
            ──ParentMemoryId──> the version it superseded (chain, latest has IsLatest=true)
            ──DerivedFrom──────> the memories it was synthesized from (citations)
```

`LayerId == null` means the Local (read-write) layer; a non-null value points at a mounted read-only
layer (**sharing** owns mounting and the read-only rule).

`MemoryEntry` is a wide document deliberately: other domains own individual fields on it
(`Quarantine`, `Drift`, `LastLexShare`, `LastMergeRejectedAt`, echo/fizzle counters). Adding a field
means deciding which domain owns writing it — see Gotchas.

## Invariants & rules

- **A memory id is a frozen persisted format, and it *is* that memory's content commitment.**
  The id embeds a truncated SHA256 over the memory's own content, so changing the preimage — its
  inputs, their order, or their rendering — invalidates every id ever minted and silently de-boosts
  the whole corpus at recall instead of failing a build. Two minting conventions exist (timestamped
  and content-addressed); callers never invent a third and never pattern-match the shape by hand —
  `MemoryIdGenerator.Matches` is the only correct way to ask "could this id have come from here?".
  Owned by `src/Eidet.Core/Domain/MemoryIdGenerator.cs`.
- **A worktree's memories belong to the main repository.** A git worktree is a second checkout of one
  repo, and its path is routinely temporary — a PR branch under `.claude-worktrees/`, a session
  scratchpad under the system temp directory. Taking the working directory verbatim made each checkout
  its own namespace, so an agent session running in one banked memories that describe the repository
  but are unreachable from it and outlive the directory they were named after. Measured on a field
  corpus: 130 live memories across two such namespaces, more than every genuine repo's unenriched
  backlog combined, and 108 of them were content-identical to memories the main repo already held
  (the same sessions wrote to both). `RepoPathResolver.Resolve` reads the worktree's `.git` *pointer
  file* and returns the main checkout; it is applied where a filesystem path first becomes a repo —
  the CLI's working directory and `McpCommand`'s, never inside `RepoIdNormalizer`, which is a pure
  string map called on already-normalized ids at ~40 sites and must stay filesystem-free and
  idempotent. Scan roots are deliberately *not* resolved: intake still reads the files in front of it
  and stores them under the resolved repo (`IntakeCommand` keeps `repoId` and `projectPath` separate).
  Owned by `src/Eidet.Core/Domain/RepoPathResolver.cs`.
- **Nothing mutates a stored memory outside `MemoryService`'s mutation gate.** Every
  store/forget/feedback/edit/link write funnels through `RunWriteAsync`/`RunMutationAsync`, which
  writes via a file-scoped `MutationCtx` and bumps the recall cache's per-scope generation in a
  `finally`. The storage write API is unreachable from any code path outside that one file. A new
  write path that bypasses it does not fail — it silently serves stale recalls.
  Owned by `src/Eidet.Core/Services/MemoryService.cs`.
- **Content is never edited in place.** A content change supersedes: the incumbent gets
  `IsLatest=false` and a closed `Validity.ValidUntil`, and a *new* document is stored pointing back
  at it. Metadata-only edits update in place. The single exception is `RedactAsync`, and it keeps the
  id on purpose so the chain stays walkable.
- **There is no hard delete.** `Forget` closes the validity interval, records a reason, and stores a
  low-importance system audit `Observation` citing the forgotten id. The document survives for audit.
- **Provenance defaults to `Unknown`, never to a trusted origin.** An entry that never had provenance
  established must not be indistinguishable from one an agent vouched for; every honest write path
  sets it explicitly. Owned by `src/Eidet.Core/Domain/MemoryProvenance.cs` (+ **writepath** for the
  trust rules derived from it).
- **`Summary == null` means "awaiting enrichment"; `Summary == ""` means "redacted".** The
  distinction is load-bearing: the enrichment subscription, the nightly sweep, and the unenriched
  stats all queue on `null`, and a redaction tombstone must never re-enter those queues.
- **Only `ValencePolarity` does valence sign arithmetic.** The write choke points ask
  `Conflicts`/`Merge` a domain question and never compute signs themselves. `Cautionary` is
  deliberately sign-0 — a warning does not contradict an affirming claim, so it still folds normally;
  only hard `Affirming`↔`Refuting` pairs are protected. Owned by
  `src/Eidet.Core/Memory/ValencePolarity.cs`.
- **`Valence.Neutral` and `FunctionalStage.None` are both `0` so every pre-existing document
  backfills for free** — and `None` additionally carries the first-class meaning
  "stage-agnostic / applies broadly", which is what makes recall's hard stage pre-filter safe.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Domain/MemoryEntry.cs` | The aggregate root; field-level comments state each field's owner and null semantics |
| `src/Eidet.Core/Domain/MemoryIdGenerator.cs` | Both id conventions + `Matches`; the frozen-format contract |
| `src/Eidet.Core/Domain/{MemoryType,Valence,FunctionalStage,Validity,MemoryLayer,MemoryLink}.cs` | The dimensions |
| `src/Eidet.Core/Domain/{MemoryProvenance,MemoryProvenanceJsonConverter}.cs` | Provenance value + its persisted form |
| `src/Eidet.Core/Domain/RepoIdNormalizer.cs` | Filesystem path → `RepoId` namespace (`P--Eidet`); pure string map, no filesystem access |
| `src/Eidet.Core/Domain/RepoPathResolver.cs` | Working directory → the repository it belongs to (a worktree resolves to its main checkout), applied before normalization |
| `src/Eidet.Core/Services/RepoRehomeService.cs` | Moves a namespace's memories into another repo — the repair for what was banked before the resolver existed (`eidet repo rehome`) |
| `src/Eidet.Core/Storage/RavenEidetStore.cs` (`GetLiveCountsByRepoAsync`) | The exhaustive repo enumeration — reads the `Memories_CountByType` reduce index, not a page of documents |
| `src/Eidet.Core/Domain/RepoUsage.cs` | Per-repo anchor doc: `OriginalPath` maps a normalized id back to the filesystem path (what lets the Web UI trigger intake), plus the learned recall alpha and the git-intake watermark |
| `src/Eidet.Core/Services/MemoryService.cs` | Every lifecycle verb + the mutation/cache gate (also hosts recall — see **recall**) |
| `src/Eidet.Core/Services/MemoryServiceOptions.cs` | `StoreOptions` / `EditOptions` / `RecallOptions` — the 20% surface |
| `src/Eidet.Core/Memory/ValencePolarity.cs` | The only home for stance sign logic |
| `src/Eidet.Core/Storage/RavenEidetStore.cs` | The RavenDB persistence port behind `IEidetStore` |

## Gotchas

- **Enumerating repos by projecting `RepoId` off the search index is wrong, and wrong quietly.**
  `Distinct` over a document query only ever sees the first page: on a 23k-entry corpus that
  reported 27 of 93 repos while looking like a complete answer, and the repos it omitted were the
  small ones — which is exactly where a stranded namespace hides, so the one caller that needed it
  most was the one it failed. `GetLiveCountsByRepoAsync` reads the `Memories_CountByType`
  map-reduce index instead (already grouped by repo and type, already filtering retired entries),
  which is one query of roughly one row per repo per type and yields exact counts as a side effect.
  `GetDistinctRepoIdsAsync` is now its keys. Anything else that wants "all the X" from a document
  query has the same bug waiting.
- **`EditAsync` reports a gate rejection as `NotFound`.** Deliberate, to preserve the pre-#65 contract
  of the `UpdateMemoryAsync` bool wrapper — so a caller genuinely cannot distinguish "no such memory"
  from "the new content was rejected". Don't build UX on that return alone.
- **A redaction tombstone no longer satisfies its own id.** That's expected: the content is rendered
  through `MemoryCommitment.Render`, so the integrity audit classifies it *Amended* rather than
  *Broken*. Hand-scrubbing content without that rendering reads as tampering (**writepath**).
- **`MemoryIdGenerator` normalizes `DateTime.Kind` itself**, because `ToString("O")` emits a `Z` only
  for `Utc` — a serializer round-trip that dropped `Kind` would otherwise report the entire corpus as
  tampered. Don't move that normalization to call sites.
- **A supersession is exempt from the poison fast-path and the conflict gate** — contradicting the
  incumbent is the whole point of a correction — and its own target is not treated as a duplicate.
- **`negative: true` on a store defaults the *type* to Heuristic** — near-immortal and L1-visible,
  which is right for a real dead-end and wrong for a transient failure ("it broke because of a typo").
  Bounded by the signal gate and ROI decay, not eliminated: pass an explicit `type` when the failure
  only deserves an Insight's lifespan. Owned by `src/Eidet.Service/Tools/Handlers/StoreToolHandler.cs`.
- **An echo clears a quarantine verdict; a fizzle does not set one.** Feedback tuning and the
  quarantine lifecycle are entangled in `FeedbackAsync`; read both before changing either.
- **`src/Eidet.Core/Services/VersionHistory.cs` is the *installed app version* log, not the memory version chain.**
  The chain is `MemoryService.GetVersionChainAsync`. The names collide; the concepts don't.

## Executable references

- `tests/Eidet.Core.Tests/Memory/MemoryIdConventionTests.cs` — **the authority on id/commitment
  behaviour**: content-addressed vs timestamped preimages, `Matches` accepting both but never
  rewritten content, and both audit observations being intact and first-party.
- `tests/Eidet.Core.Tests/Memory/MemoryCommitmentTests.cs` — settles Intact / Amended / Broken
  classification, including that a non-conforming id carries no commitment and that forging the
  amendment shape is self-defeating.
- `tests/Eidet.Core.Tests/Services/MemoryServiceBoundaryTests.cs` — **the authority on the write
  funnel**: cache invalidation per scope, bulk paths invalidating even when the body throws, a
  validator rejection *not* invalidating, and no stale result under concurrent store-during-recall.
- `tests/Eidet.Core.Tests/Services/CurationSafetyTests.cs` — settles edit/redact semantics:
  stale-sha precondition, idempotent redaction, mid-chain redaction, and that redacted content stops
  surfacing in recall.
- `tests/Eidet.Core.Tests/Memory/ValencePolarityTests.cs` +
  `tests/Eidet.Core.Tests/Maintenance/ValenceWritePathGuardTests.cs` — the standing tripwire that an
  `Affirming`/`Refuting` near-duplicate pair survives the dup-gate, dedup, *and* consolidation.
- `tests/Eidet.Core.Tests/Domain/RepoPathResolverTests.cs` — **the authority on repo identity for a
  second checkout**: a worktree resolves to its main repository through rooted, forward-slash and
  relative pointers, while a primary checkout, a plain directory, a submodule, a already-normalized
  repo id, a missing path and a malformed pointer all come back unchanged.
- `tests/Eidet.Core.Tests/Services/RepoRehomeServiceTests.cs` — **the authority on moving a
  namespace**: the arriving copy is live while only the original is retired (the regression — taking
  both from one object retires the memory it was rescuing), the new id satisfies its own content
  commitment, fields survive, content the target already holds is folded rather than copied, the
  source ends up empty either way, and a second run is a no-op.
- `tests/Eidet.Core.Tests/Domain/` — `ValidityTests`, `MemoryIdGeneratorTests`,
  `ValenceBackfillTests`, `MemoryProvenanceJsonConverterTests`, `RepoIdNormalizerTests`.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Memory core, Memory types, Namespacing & layers, Lifecycle
  (and the flagged ambiguities *Importance vs Confidence*, *Delete vs Forget*, *Memory vs Loose End*)
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) (domain model, types, layers)
- Related domains: **writepath** (may this write happen, and what does it earn) · **recall** (how it
  comes back) · **maintenance** (who rewrites it later) · **sharing** (layer mounting, pack round-trip)
- Priming skill: `.claude/skills/memory/SKILL.md`
