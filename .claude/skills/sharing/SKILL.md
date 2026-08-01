---
name: sharing
description: Prime on Eidet's layers and packs before changing them — MemoryLayer types and the Local/Shared/Base priority ladder, LayerService mounting and applicability, the immutable LayerScope handed to recall, MarkdownPackFormat's YAML+markdown round-trip and its import provenance clamp, LayerSyncService and ILayerSource, and the AGENTS.md export. Use when the task touches pack export/import, mounting or unmounting a layer, cross-repo scope, the .eidet markdown format, or ExportService. Not for seeding from a repo's own files (see intake), not for backup/restore or remote sync.
---

# Sharing (layers & packs) — priming

**Canonical spec:** `docs/domains/sharing.md` — read it for the layer model, the pack format, all
invariants, key files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Namespacing & layers,
Sharing. Threat: `STRIDE.md` T-7.

Layers stack namespaces (Local rw on top; Shared/Base ro below) and resolve into the recall scope.
Packs are the human-readable transport that auto-mounts as a layer on import.

## Core invariants (get these right)

- **Writes always land in the Local layer** — Shared/Base contribute to recall but never accept writes.
- **Clamp declared provenance on import.** A poisoned pack can write `provenance=userStated`; anything
  above the `Pack` trust floor is clamped back down (T-7). Lower-or-equal origins pass through.
- **`Unknown` provenance never crosses the wire** — omit it on export so a foreign install applies its
  own default and the clamp holds it at the pack floor.
- **`LayerScope` is resolved once at the boundary** and is immutable; the read pipeline never learns
  about mounting.
- **Applicability has three routes**: universal (empty `ApplicableRepos`), explicit repo, or
  package-dependency match (the auto-mount path).
- **The pack format is a published contract** (ScribeGate + plain markdown viewers): every field must
  round-trip and defaults are omitted. A new `MemoryEntry` field needs a wire decision.
- **Mounting is idempotent**, and a legacy `bundle:` layer id is reused rather than forked.
- **Loose Ends and Memory files are never exported.**

## Key files / reuse

- `src/Eidet.Core/Services/LayerService.cs` — mount/applicability/scope.
- `src/Eidet.Core/Layers/LayerScope.cs` — the scope snapshot + `NonLocalDeBoost`.
- `src/Eidet.Core/Services/MarkdownPackFormat.cs` — the format *and* the import clamp.
- `src/Eidet.Core/Layers/ILayerSource.cs` — add a transport here (`file` is the only scheme today).

## Gotchas

- `GetMountedLayersAsync("")` is an intentional "all layers" wildcard, not a bug.
- Cross-repo recall is off by default — a layer that "isn't working" is usually a `CrossRepo: false` call.
- `AutoMountByDependenciesAsync` mutates the *layer* (appending repo ids), not the repo.
- Pack import mints ids under the *pack* id, not the importing repo — imports are a separate namespace.
- The de-boost is a multiplier, not a filter.
- `AGENTS.md` export is a one-way rendering; regenerate rather than hand-edit.
