# Sharing (layers & packs)

Stacking someone else's memories read-only, and shipping your own as a human-readable bundle.

**Status:** current as of [#80](https://github.com/stevehansen/eidet/issues/80) (the pack provenance
clamp) · **Governing issues:** [#34](https://github.com/stevehansen/eidet/issues/34) (import trust
floor / MemoryGraft), STRIDE T-7. Remote/team sync remains future work
([`docs/specs/SyncSpec.md`](../specs/SyncSpec.md)).
**Priming skill:** [`.claude/skills/sharing/SKILL.md`](../../.claude/skills/sharing/SKILL.md)

## What it is

Two halves of one story. **Layers** stack memory namespaces Docker-style — Local (read-write) on top,
Shared and Base below (read-only) — and resolve into the scope a recall fans out across. **Packs** are
the transport: a markdown bundle with YAML frontmatter that renders in any viewer, round-trips through
Eidet, and auto-mounts as a layer on import. Plus one adjacent export shape: rendering a repo's
memories as an `AGENTS.md` instruction file.

It is *not* the inbound path for a repo's *own* artifacts (**intake**), *not* backup/restore
(`BackupService` — operational, see `docs/specs/ServiceSpec.md`), and *not* remote sync (designed only).

## Core entities & relationships

```
MemoryLayer { Id, Type (Local|Shared|Base), ReadOnly, Priority, ApplicableRepos, ApplicablePackages }
   Priority: Local 100 · Shared 50 · Base 10          ReadOnly = (Type != Local)

LayerService     — mount/unmount, applicability, AutoMountByDependencies
      └─ ResolveScopeAsync → LayerScope { PrimaryRepoId, RepoIds[], MountedLayers[], CrossRepo }
                             + NonLocalDeBoost, consumed by recall

EidetPack ⇄ MarkdownPackFormat   (YAML frontmatter · H1 title · H2 type groups · H3 one-liner
                                  headings · HTML-comment per-memory metadata · content between H3s)
LayerSyncService — diffs a pack against a mounted layer's stored entries (add/update/remove)
      └─ ILayerSource by scheme  → FilesystemLayerSource ("file"); HTTP/registry is a plug-in point

ExportService    — ExportMarkdownAsync (human dump) · ExportAgentsMdAsync (instruction file)
```

An imported memory carries a non-null `LayerId` (`pack:<id>`) and `Provenance = Pack`, which is what
makes it read-only in practice and permanently provisional in trust (**writepath**).

## Invariants & rules

- **Writes always land in the Local layer.** Shared and Base layers contribute to recall but never
  accept writes; correcting a bad imported memory means forgetting it or writing a correcting memory
  locally.
- **An imported pack is untrusted-until-echoed regardless of what it *declares*.** A poisoned pack
  controls its own bytes and could write `provenance=userStated` to self-assign full trust, so the
  importer clamps any declared provenance whose trust floor is above `Pack` back down to `Pack`
  (STRIDE T-7, #34). Lower-or-equal origins are left as declared.
- **`Unknown` provenance never crosses the wire.** The serializer omits it (as it omits the historical
  `AgentInferred` default), so a foreign install applies *its* own default rather than inheriting our
  failure to establish provenance — and the importer's clamp then holds it at the pack floor (#80).
- **`LayerScope` is resolved once, at the boundary.** The read pipeline receives an immutable snapshot
  and never learns about mounting; the non-local de-boost constant lives on the scope.
- **Layer applicability has three routes**: universal (empty `ApplicableRepos`), explicit repo listing,
  or package-dependency match. Dependency auto-mounting is what makes a framework author's Base layer
  arrive without user action.
- **The pack format is the contract, not an implementation detail.** It is published to ScribeGate and
  read by humans in plain markdown viewers, so every field must round-trip and default-valued fields are
  omitted rather than written. Adding a `MemoryEntry` field means deciding whether it crosses the wire.
- **A newly mounted layer prefers the canonical `pack:` id but reuses a legacy `bundle:` mount** when
  one exists, so pre-rename imports keep syncing to the same layer instead of forking.
- **Mounting is idempotent** — mounting an already-mounted id returns the existing layer untouched.
- **Loose Ends and Memory files are never exported.** Packs carry knowledge only.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Domain/MemoryLayer.cs` | The layer entity + `LayerType` and the priority ladder |
| `src/Eidet.Core/Layers/LayerScope.cs` | The immutable per-request scope snapshot + `NonLocalDeBoost` |
| `src/Eidet.Core/Services/LayerService.cs` | Mount/unmount, applicability, scope resolution, dependency auto-mount |
| `src/Eidet.Core/Services/LayerSyncService.cs` | Pack↔layer diffing and the source registry |
| `src/Eidet.Core/Layers/{ILayerSource,FilesystemLayerSource,LayerSourceRef}.cs` | The transport seam |
| `src/Eidet.Core/Services/MarkdownPackFormat.cs` | Serialize/Deserialize + the import provenance clamp |
| `src/Eidet.Core/Domain/EidetPack.cs` | The pack model |
| `src/Eidet.Core/Services/ExportService.cs` | Markdown dump + `AGENTS.md` instruction rendering |
| `src/Eidet.Service/Tools/Handlers/Pack{Export,Import}ToolHandler.cs` | The off-MCP handlers (REST/CLI reachable) |
| `src/Eidet.Service/Commands/{LayerCommand,ExportCommand}.cs` | The CLI surface |

## Gotchas

- **`GetMountedLayersAsync("")` means "all layers".** An empty repo id is an intentional wildcard used by
  the package-matching paths — easy to misread as a bug and "fix".
- **Cross-repo recall is off by default**, so mounted layers contribute nothing unless the caller opts
  in. A layer that "isn't working" is usually a `CrossRepo: false` call.
- **`AutoMountByDependenciesAsync` mutates the layer, not the repo.** It appends the repo id to the
  layer's `ApplicableRepos` — so a shared layer document accumulates every repo that ever matched it.
- **Pack import mints fresh ids scoped to the *pack* id, not the importing repo.** Imported entries live
  in their own namespace; they are not merged into the local corpus.
- **The de-boost is a recall-time multiplier, not a filter** — non-local memories still surface, just
  lower (**recall**).
- **`ExportAgentsMdAsync` is a rendering, not a sync.** Nothing reads the generated `AGENTS.md` back in;
  regenerate it rather than hand-editing.
- **Pack round-trip tests are the largest test file in the repo** for a reason: silent field loss is the
  failure mode this format is most prone to.

## Executable references

- `tests/Eidet.Core.Tests/Services/MarkdownPackFormatTests.cs` — **the authority on the pack format**:
  frontmatter, type grouping, per-memory metadata comments, and full round-trip fidelity.
- `tests/Eidet.Core.Tests/Services/MarkdownPackFormat{Valence,Stage}Tests.cs` — settle that the
  orthogonal dimensions survive export/import and that defaults are omitted.
- `tests/Eidet.Core.Tests/Services/LayerServiceTests.cs` — settles mounting idempotence, the priority
  ladder, applicability (universal / listed / package-matched), and scope resolution.
- `tests/Eidet.Core.Tests/Services/LayerSyncServiceTests.cs` — settles the add/update/remove diff and the
  legacy `bundle:` layer-id reuse.
- `tests/Eidet.Core.Tests/Layers/LayerScopeTests.cs` — settles locality checks and the de-boost contract.
- `tests/Eidet.Core.Tests/Services/ExportAgentsMdTests.cs` — settles the instruction-file shape.
- `tests/Eidet.Service.Tests/Tools/Pack{Export,Import}ToolHandlerTests.cs` — settle the handler surface.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Namespacing & layers (Layer, Local/Shared/Base, Link), Sharing
  (Pack, Pack export/import, ScribeGate), plus the flagged ambiguities *Layer vs Repo*, *Bundle vs Pack*
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) § layers ·
  [`docs/specs/SyncSpec.md`](../specs/SyncSpec.md) (future remote sync, E2E encryption)
- Threat model: `STRIDE.md` T-7 (pack poisoning / MemoryGraft)
- Related domains: **recall** (consumes `LayerScope` and the de-boost) · **writepath** (the import trust
  floor the clamp defends) · **memory** (`LayerId == null` means local) · **intake** (the other inbound
  path) · **memorytool** (never exported)
- Priming skill: `.claude/skills/sharing/SKILL.md`
